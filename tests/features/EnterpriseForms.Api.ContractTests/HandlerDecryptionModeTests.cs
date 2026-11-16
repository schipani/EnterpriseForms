using System.Reflection;
using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;
using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2c closure §4: the REAL <see cref="CreateFormRecordHandler"/> and <see cref="UpdateFormRecordHandler"/> (with the real
/// <see cref="RecordCandidateStager"/>, <see cref="VerifiedRecordReader"/>, candidate builder and AES-GCM field service) run against
/// an in-memory business store and an in-memory Authority, so the handlers' own response decisions — first attempt, retry and
/// idempotent replay — are exercised without Stage B. The fakes are <see cref="DispatchProxy"/> stand-ins: a method a fake does
/// not implement throws, so the tests show exactly what each path uses. The store returns NEW instances on every read (as EF with
/// a fresh scope and AsNoTracking does), so a stored row can never be mistaken for the instance a handler sealed. The in-memory
/// Authority does not verify signatures or manifests (that is the real Authority's job, tested in AuthorizationAuthority.Tests); it
/// models heads, prepare/commit and the share scope of a projection.
/// </summary>
public sealed class HandlerDecryptionModeTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-0f00-7000-8000-0000000000a1");
    private static readonly Guid Owner = Guid.Parse("019a5b2c-0f00-7000-8000-0000000000a2");
    private static readonly Guid Editor = Guid.Parse("019a5b2c-0f00-7000-8000-0000000000a3");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- harness ------------------------------------------------------------------------------------------------------

    /// <summary>Generic DispatchProxy fake: method name → handler; anything unhandled throws.</summary>
    public class Fake : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is not null && Handlers.TryGetValue(targetMethod.Name, out var handler)
                ? handler(args ?? [])
                : throw new NotSupportedException("the in-memory fake does not implement " + targetMethod?.Name);

        public static (T Proxy, Fake Fake) Create<T>()
            where T : class
        {
            var proxy = Create<T, Fake>();
            return (proxy, (Fake)(object)proxy);
        }
    }

    /// <summary>A new instance with every public property copied (byte arrays copied too): what a fresh database read returns.</summary>
    private static T Materialize<T>(T row)
        where T : class
    {
        var copy = (T)Activator.CreateInstance(row.GetType())!;
        foreach (var property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            var value = property.GetValue(row);
            property.SetValue(copy, value is byte[] bytes ? bytes.ToArray() : value);
        }

        return copy;
    }

    private sealed class SpyEncryption(IFieldEncryptionService inner) : IFieldEncryptionService
    {
        public List<Guid> SelfSealed { get; } = [];

        public List<Guid> Verified { get; } = [];

        public string ProviderName => inner.ProviderName;

        public bool IsAvailable => inner.IsAvailable;

        public Task<FieldEncryptedValue> EncryptAsync(FieldEncryptionContext context, string canonicalPlaintext, EncryptionKeyPurpose purpose, CancellationToken cancellationToken) => inner.EncryptAsync(context, canonicalPlaintext, purpose, cancellationToken);

        public Task<string> DecryptAsync(FieldEncryptedValue value, EncryptionKeyPurpose purpose, CancellationToken cancellationToken)
        {
            SelfSealed.Add(value.FieldValueItemId);
            return inner.DecryptAsync(value, purpose, cancellationToken);
        }

        public Task<IReadOnlyList<string>> DecryptVerifiedAsync(Guid organizationId, IReadOnlyList<FieldEncryptedValue> values, CancellationToken cancellationToken)
        {
            Verified.AddRange(values.Select(v => v.FieldValueItemId));
            return inner.DecryptVerifiedAsync(organizationId, values, cancellationToken);
        }

        public Task<byte[]> OpenCreationFingerprintKeyAsync(CreationFingerprintKeyEnvelope envelope, CancellationToken cancellationToken) => inner.OpenCreationFingerprintKeyAsync(envelope, cancellationToken);

        public void Reset()
        {
            SelfSealed.Clear();
            Verified.Clear();
        }
    }

    private sealed class NoIncidents : ISecurityIncidentRecorder
    {
        public Task RecordAsync(SecurityIncidentObservation observation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Throws once at the named checkpoint (a crash after staging, before the commit).</summary>
    private sealed class FailOnce(string checkpoint) : IWriteFaultInjector
    {
        private bool fired;

        public void Checkpoint(string checkpointName)
        {
            if (!fired && checkpointName == checkpoint)
            {
                fired = true;
                throw new IOException("injected crash at " + checkpointName);
            }
        }
    }

    private sealed class World
    {
        public FieldDefinition Secret { get; } = Field("secret", "ApplicationEncrypted");
        public FieldDefinition Note { get; } = Field("note", "ApplicationEncrypted");
        public FieldDefinition Hidden { get; } = Field("hidden_secret", "ApplicationEncrypted");
        public FieldDefinition Title { get; } = Field("title", "Standard");
        public FormDefinition Definition { get; }
        public FormVersion Version { get; }
        public SpyEncryption Encryption { get; } = new(new AesGcmFieldEncryptionService(new EnvelopeV2KeyDouble()));

        // Business store state (rows are kept as stored copies).
        public Dictionary<(Guid, Guid), (FormRecordRevision Header, List<ISignedContentRow> Rows)> Staged { get; } = [];
        public Dictionary<Guid, FormRecordCreationAttempt> Attempts { get; } = [];
        public Dictionary<Guid, FormRecord> Records { get; } = [];

        // Authority state.
        public Dictionary<Guid, (long Revision, Guid? Operation)> Heads { get; } = [];
        public Dictionary<Guid, long> Prepared { get; } = [];
        public int PrepareCalls { get; set; }
        public bool PartialProjection { get; set; }
        public HashSet<Guid> ReadableItems { get; } = [];
        public HashSet<Guid> EditableItems { get; } = [];

        public IBusinessStore Store { get; }
        public IAuthorizationAuthorityClient Authority { get; }

        public World()
        {
            Version = new FormVersion { FormVersionId = Guid.CreateVersion7(), LayoutJson = FormLayoutCodec.Serialize(new FormLayout(2, "Test", [new LayoutSection("main", "Main", 4, new[] { Secret, Note, Hidden, Title }.Select(Element).ToList())])) };
            Definition = new FormDefinition { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionName = "Test", CurrentFormVersionId = Version.FormVersionId, LifecycleStatus = "Active", VersionBehavior = "Latest" };
            Version.FormDefinitionId = Definition.FormDefinitionId;
            foreach (var field in new[] { Secret, Note, Hidden, Title })
            {
                field.FormDefinitionId = Definition.FormDefinitionId;
            }

            Store = BuildStore();
            Authority = BuildAuthority();
        }

        private static FieldDefinition Field(string key, string protection) => new()
        {
            FieldDefinitionId = Guid.CreateVersion7(), FieldKey = key, FieldDisplayName = key, FieldDataType = "String", FieldClassification = protection == "ApplicationEncrypted" ? "Confidential" : "Internal", FieldProtectionMode = protection, OptionSelectionMode = "Single",
        };

        private static LayoutElement Element(FieldDefinition field) =>
            new(LayoutElementKinds.Field, field.FieldDefinitionId, null, "text", field.FieldDisplayName, null, new LayoutPlacement(4, false), new LayoutValidation(false, null, null, null, null, null, null, null), null, null);

        private IBusinessStore BuildStore()
        {
            var (store, storeFake) = Fake.Create<IBusinessStore>();
            storeFake.Handlers["BeginScopeAsync"] = _ => Task.FromResult(BuildScope());
            return store;
        }

        private IBusinessScope BuildScope()
        {
            var (scope, h) = Fake.Create<IBusinessScope>();
            var fields = new List<FieldDefinition> { Secret, Note, Hidden, Title };
            h.Handlers["FindFormDefinitionAsync"] = a => Task.FromResult((Guid)a[0]! == Org && (Guid)a[1]! == Definition.FormDefinitionId ? Definition : null);
            h.Handlers["FindFormVersionAsync"] = a => Task.FromResult((Guid)a[0]! == Version.FormVersionId ? Version : null);
            h.Handlers["GetFieldDefinitionsAsync"] = _ => Task.FromResult(fields.ToList());
            h.Handlers["GetFieldOptionsAsync"] = _ => Task.FromResult(new List<FieldOption>());
            h.Handlers["GetGroupDefinitionsAsync"] = _ => Task.FromResult(new List<GroupDefinition>());
            h.Handlers["FindCreationAttemptAsync"] = a => Task.FromResult(Attempts.TryGetValue((Guid)a[1]!, out var attempt) ? Materialize(attempt) : null);
            h.Handlers["TryAddCreationAttemptAsync"] = a => Task.FromResult(Attempts.TryAdd(((FormRecordCreationAttempt)a[0]!).FormRecordId, Materialize((FormRecordCreationAttempt)a[0]!)));
            h.Handlers["FindFormRecordAsync"] = a => Task.FromResult(Records.TryGetValue((Guid)a[1]!, out var record) ? record : null);
            h.Handlers["AddFormRecord"] = a => { var r = (FormRecord)a[0]!; Records[r.FormRecordId] = r; return null; };
            h.Handlers["AddAuditEvent"] = _ => null;
            h.Handlers["ProjectAcceptedHeadAsync"] = _ => Task.FromResult(true);
            h.Handlers["ProjectLifecycleAsync"] = _ => Task.FromResult(false);
            h.Handlers["StageCandidate"] = a =>
            {
                var header = (FormRecordRevision)a[0]!;
                Staged[(header.FormRecordId, header.ContentChangeOperationId)] = (Materialize(header), ((IEnumerable<ISignedContentRow>)a[1]!).Select(Materialize).ToList());
                return null;
            };
            h.Handlers["FindStagedHeaderAsync"] = a => Task.FromResult(Staged.TryGetValue(((Guid)a[0]!, (Guid)a[1]!), out var s) ? Materialize(s.Header) : null);
            h.Handlers["GetStagedRowsAsync"] = a => Task.FromResult(Staged.TryGetValue(((Guid)a[0]!, (Guid)a[1]!), out var s) ? s.Rows.Select(Materialize).ToList() : new List<ISignedContentRow>());
            h.Handlers["CommitAsync"] = _ => Task.CompletedTask;
            h.Handlers["DisposeAsync"] = _ => ValueTask.CompletedTask;
            return scope;
        }

        private AcceptedContentRevision Head(Guid record) =>
            new(record, Heads.TryGetValue(record, out var head) ? head.Revision : 0, Heads.TryGetValue(record, out head) ? head.Operation : null, Convert.ToBase64String(new byte[32]), 5, 1, 1, "Anchored", Guid.CreateVersion7());

        private IAuthorizationAuthorityClient BuildAuthority()
        {
            var (authority, h) = Fake.Create<IAuthorizationAuthorityClient>();
            h.Handlers["CheckPermissionAsync"] = _ => Task.FromResult(new PermissionDecision(true, null, 5, 5, null, Guid.CreateVersion7()));
            h.Handlers["SubmitSecurityCommandAsync"] = _ => Task.FromResult(new SecurityCommandResult(Guid.CreateVersion7(), SecurityCommandStatus.Effective, 5, 5, "Open", null, Guid.CreateVersion7()));
            h.Handlers["GetCommittedContentRevisionAsync"] = a => Task.FromResult(Head((Guid)a[2]!));
            h.Handlers["PrepareContentChangeAsync"] = a =>
            {
                PrepareCalls++;
                var request = (PrepareContentChangeRequest)a[1]!;
                var current = Head(request.FormRecordId);
                if (request.ExpectedContentRevision != current.CommittedContentRevision)
                {
                    throw new AuthorityClientException("CONTENT_REVISION_CONFLICT", 409, false, "stale");
                }

                Prepared[request.OperationId] = request.ExpectedContentRevision + 1;
                var signed = request.CandidateItems.Select(i => new SignedCandidateItem(i.ItemKind, i.ItemId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Convert.ToBase64String(new byte[32]), "Upsert")).ToList();
                return Task.FromResult(new PreparedContentOperation(request.OperationId, request.ExpectedContentRevision + 1, 1, Convert.ToBase64String(new byte[32]), signed, "Prepared", Guid.CreateVersion7()));
            };
            h.Handlers["CommitContentChangeAsync"] = a =>
            {
                var operation = (Guid)a[1]!;
                var request = (CommitContentChangeRequest)a[2]!;
                if (Heads.TryGetValue(request.FormRecordId, out var head) && head.Operation == operation)
                {
                    return Task.FromResult(Head(request.FormRecordId));
                }

                if (!Prepared.TryGetValue(operation, out var revision) || revision != Head(request.FormRecordId).CommittedContentRevision + 1)
                {
                    throw new AuthorityClientException("CONTENT_REVISION_CONFLICT", 409, false, "stale");
                }

                Heads[request.FormRecordId] = (revision, operation);
                return Task.FromResult(Head(request.FormRecordId));
            };
            h.Handlers["VerifyRecordProjectionAsync"] = a =>
            {
                var request = (VerifyRecordProjectionRequest)a[1]!;
                var head = Head(request.FormRecordId);
                var items = request.ObservedItems.Select(i => new VerifiedProjectionItem(i.ItemKind, i.ItemId)).ToList();
                if (!PartialProjection)
                {
                    return Task.FromResult(new VerifyRecordProjectionResult(true, null, head.CommittedContentRevision, head.CommittedOperationId, 5, items, true, Guid.CreateVersion7()));
                }

                var readable = items.Where(i => ReadableItems.Contains(i.ItemId)).ToList();
                var editable = items.Where(i => EditableItems.Contains(i.ItemId)).ToList();
                return Task.FromResult(new VerifyRecordProjectionResult(true, null, head.CommittedContentRevision, head.CommittedOperationId, 5, readable, false, Guid.CreateVersion7(), readable, editable, ContributorWritesEnabled: true));
            };
            return authority;
        }

        public CreateFormRecordHandler CreateHandler(IWriteFaultInjector? faults = null)
        {
            var injector = faults ?? new NoWriteFaults();
            var stager = new RecordCandidateStager(Store, Authority, TimeProvider.System, injector);
            return new CreateFormRecordHandler(Store, Authority, new AuthorityCommandAwaiter(Authority, TimeProvider.System), stager, TimeProvider.System, Encryption, Reader(stager), injector, NoOperationTiming.Instance);
        }

        public UpdateFormRecordHandler UpdateHandler()
        {
            var stager = new RecordCandidateStager(Store, Authority, TimeProvider.System, new NoWriteFaults());
            return new UpdateFormRecordHandler(Reader(stager), Store, stager, TimeProvider.System, Application.Features.FormRecords.Lifecycle.LifecycleTestingHooks.Disabled);
        }

        private VerifiedRecordReader Reader(RecordCandidateStager stager) => new(Store, Authority, stager, Encryption, new NoIncidents());

        /// <summary>Every stored encrypted row of the record's current head (fresh instances, as from the database).</summary>
        public List<FieldEncryptedValue> StoredEncrypted(Guid record) =>
            Staged[(record, Heads[record].Operation!.Value)].Rows.OfType<FieldEncryptedValue>().Select(Materialize).ToList();

        public Guid StoredItemOf(Guid record, FieldDefinition field) => Staged[(record, Heads[record].Operation!.Value)].Rows.OfType<FieldValueRowBase>().Single(r => r.FieldDefinitionId == field.FieldDefinitionId).FieldValueItemId;
    }

    private static RequestContext Context(Guid user) => new(Org, user, "https://issuer.test", user.ToString("N"), "Test User", "assertion", Guid.CreateVersion7());

    private static Dictionary<string, string> Values(FormRecordResponse response) => response.Values.ToDictionary(v => v.FieldKey, v => v.StringValue, StringComparer.Ordinal);

    private static async Task<(World World, FormRecordResponse Created)> CreatedAsync()
    {
        var world = new World();
        var created = await world.CreateHandler().HandleAsync(Context(Owner), world.Definition.FormDefinitionId,
            new CreateFormRecordRequest(new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-1", ["hidden_secret"] = "h-1", ["title"] = "t-1" }), "create-1", Ct);
        return (world, created);
    }

    // ---- C: creation --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreationFirstAttemptAnswersWithItsOwnSealsInSelfSealedModeOnly()
    {
        var (world, created) = await CreatedAsync();
        Assert.Equal(new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-1", ["hidden_secret"] = "h-1", ["title"] = "t-1" }, Values(created));
        Assert.Equal(3, world.Encryption.SelfSealed.Count); // the three encrypted values of the response
        Assert.Empty(world.Encryption.Verified);           // nothing stored was decrypted
        Assert.Equal(1, created.ContentRevision);
    }

    [Fact]
    public async Task CreationReplayAnswersWithFreshSealsAndNeverDecryptsTheCommittedRows()
    {
        var (world, _) = await CreatedAsync();
        var committed = world.StoredEncrypted(world.Records.Keys.Single());
        world.Encryption.Reset();
        var prepares = world.PrepareCalls;

        var replay = await world.CreateHandler().HandleAsync(Context(Owner), world.Definition.FormDefinitionId,
            new CreateFormRecordRequest(new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-1", ["hidden_secret"] = "h-1", ["title"] = "t-1" }), "create-1", Ct);

        Assert.Equal("s-1", Values(replay)["secret"]);
        Assert.Equal(prepares, world.PrepareCalls); // replay of the accepted revision 1: nothing prepared or staged again
        Assert.Equal(3, world.Encryption.SelfSealed.Count);
        Assert.Empty(world.Encryption.Verified);
        Assert.All(committed, row => Assert.DoesNotContain(row.FieldValueItemId, world.Encryption.Verified));

        // The committed rows themselves can never be decrypted through the self-sealed step.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptSelfSealedAsync(world.Encryption, committed, Ct));
    }

    [Fact]
    public async Task CreationRetryAfterACrashBetweenStageAndCommitCommitsTheStagedRowsButAnswersWithItsOwnFreshSeals()
    {
        var world = new World();
        var request = new CreateFormRecordRequest(new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-1", ["hidden_secret"] = "h-1", ["title"] = "t-1" });
        await Assert.ThrowsAsync<IOException>(() => world.CreateHandler(new FailOnce(WriteFaultCheckpoints.ContentStaged)).HandleAsync(Context(Owner), world.Definition.FormDefinitionId, request, "create-crash", Ct));
        var record = world.Records.Keys.Single();
        Assert.False(world.Heads.ContainsKey(record)); // staged, not committed
        world.Encryption.Reset();

        var retry = await world.CreateHandler().HandleAsync(Context(Owner), world.Definition.FormDefinitionId, request, "create-crash", Ct);

        Assert.Equal(1, retry.ContentRevision);
        Assert.Equal("h-1", Values(retry)["hidden_secret"]);
        Assert.Equal(3, world.Encryption.SelfSealed.Count);
        Assert.Empty(world.Encryption.Verified);
        // What was committed is the earlier attempt's staged ciphertext; the retry answered from its own seals (distinct item ids),
        // never from the committed rows.
        var committedItems = world.StoredEncrypted(record).Select(r => r.FieldValueItemId).ToHashSet();
        Assert.Equal(3, committedItems.Count);
        Assert.Empty(committedItems.Intersect(world.Encryption.SelfSealed));
    }

    [Fact]
    public async Task ACreationReplayAfterTheRecordWasEditedAnswersThroughTheVerifiedReader()
    {
        var (world, created) = await CreatedAsync();
        await world.UpdateHandler().HandleAsync(Context(Owner), created.FormRecordId, new UpdateFormRecordRequest(1, new Dictionary<string, string> { ["note"] = "n-2" }), "update-1", Ct);
        world.Encryption.Reset();

        var replay = await world.CreateHandler().HandleAsync(Context(Owner), world.Definition.FormDefinitionId,
            new CreateFormRecordRequest(new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-1", ["hidden_secret"] = "h-1", ["title"] = "t-1" }), "create-1", Ct);

        Assert.Equal(2, replay.ContentRevision);
        Assert.Equal("n-2", Values(replay)["note"]);        // the accepted current record, not the request
        Assert.Empty(world.Encryption.SelfSealed);          // previously accepted content: never self-sealed
        Assert.Equal(3, world.Encryption.Verified.Count);
    }

    // ---- D: owner update ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task OwnerUpdateReadsStoredRowsVerifiedAndAnswersWithFreshSealsAlsoOnAnIdempotentReplay()
    {
        var (world, created) = await CreatedAsync();
        var stored = world.StoredEncrypted(created.FormRecordId).Select(r => r.FieldValueItemId).ToHashSet();
        world.Encryption.Reset();

        var updated = await world.UpdateHandler().HandleAsync(Context(Owner), created.FormRecordId, new UpdateFormRecordRequest(1, new Dictionary<string, string> { ["note"] = "n-2" }), "update-1", Ct);

        Assert.Equal(new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-2", ["hidden_secret"] = "h-1", ["title"] = "t-1" }, Values(updated));
        Assert.Equal(stored, world.Encryption.Verified.ToHashSet()); // current stored rows: verified reader (carry-forward)
        Assert.Equal(3, world.Encryption.SelfSealed.Count);          // the response: this operation's own seals

        // Idempotent replay: the same key with the revision the first attempt produced; the stager returns the accepted head.
        world.Encryption.Reset();
        var prepares = world.PrepareCalls;
        var replay = await world.UpdateHandler().HandleAsync(Context(Owner), created.FormRecordId, new UpdateFormRecordRequest(2, new Dictionary<string, string> { ["note"] = "n-2" }), "update-1", Ct);
        Assert.Equal(prepares, world.PrepareCalls); // nothing prepared again
        Assert.Equal(2, replay.ContentRevision);
        Assert.Equal("n-2", Values(replay)["note"]);
        Assert.Equal(3, world.Encryption.Verified.Count);   // the committed rows of revision 2, via the verified reader
        Assert.Equal(3, world.Encryption.SelfSealed.Count); // the answer: fresh seals again

        // The stale revision is refused before anything is built or decrypted.
        world.Encryption.Reset();
        var stale = await Assert.ThrowsAsync<BusinessException>(() => world.UpdateHandler().HandleAsync(Context(Owner), created.FormRecordId, new UpdateFormRecordRequest(1, new Dictionary<string, string> { ["note"] = "n-2" }), "update-1", Ct));
        Assert.Equal("CONTENT_REVISION_CONFLICT", stale.ErrorCode);
        Assert.Empty(world.Encryption.SelfSealed);
    }

    // ---- E: shared edit -----------------------------------------------------------------------------------------------

    private static async Task<(World World, Guid Record, Guid Note, Guid Secret, Guid Hidden)> SharedAsync()
    {
        var (world, created) = await CreatedAsync();
        var record = created.FormRecordId;
        var (note, secret, hidden, title) = (world.StoredItemOf(record, world.Note), world.StoredItemOf(record, world.Secret), world.StoredItemOf(record, world.Hidden), world.StoredItemOf(record, world.Title));
        world.PartialProjection = true;
        world.ReadableItems.UnionWith([note, secret, title]); // hidden_secret is not shared with the editor
        world.EditableItems.Add(note);
        world.Encryption.Reset();
        return (world, record, note, secret, hidden);
    }

    [Fact]
    public async Task SharedEditAnswersEditedRowsSelfSealedCopiedRowsVerifiedAndNothingUnshared()
    {
        var (world, record, note, secret, hidden) = await SharedAsync();

        var response = await world.UpdateHandler().HandleAsync(Context(Editor), record, new UpdateFormRecordRequest(1, new Dictionary<string, string> { ["note"] = "edited" }), "share-1", Ct);

        Assert.False(response.IsCompleteProjection);
        Assert.Equal(new Dictionary<string, string> { ["note"] = "edited", ["secret"] = "s-1", ["title"] = "t-1" }, Values(response)); // no hidden_secret
        Assert.Equal([note], world.Encryption.SelfSealed);   // only the fresh seal of the edit
        Assert.Contains(secret, world.Encryption.Verified);  // the copied stored row
        Assert.DoesNotContain(hidden, world.Encryption.Verified);
        Assert.DoesNotContain(hidden, world.Encryption.SelfSealed); // unshared content is never decrypted
        Assert.DoesNotContain(response.Values, v => v.FieldValueItemId == hidden);
    }

    [Fact]
    public async Task SharedEditReplayAnswersNoChangeWithoutSelfSealingAndAStaleRevisionIsRefused()
    {
        var (world, record, _, _, hidden) = await SharedAsync();
        await world.UpdateHandler().HandleAsync(Context(Editor), record, new UpdateFormRecordRequest(1, new Dictionary<string, string> { ["note"] = "edited" }), "share-1", Ct);
        world.Encryption.Reset();

        // The replayed request against the revision it produced: the stored value already equals it, so nothing is sealed or self-sealed.
        var replay = await Assert.ThrowsAsync<BusinessException>(() => world.UpdateHandler().HandleAsync(Context(Editor), record, new UpdateFormRecordRequest(2, new Dictionary<string, string> { ["note"] = "edited" }), "share-1", Ct));
        Assert.Equal("NO_CHANGE", replay.ErrorCode);
        Assert.Empty(world.Encryption.SelfSealed);
        Assert.DoesNotContain(hidden, world.Encryption.Verified);

        // The replay with the original expected revision is refused before anything is decrypted for the response.
        world.Encryption.Reset();
        var stale = await Assert.ThrowsAsync<BusinessException>(() => world.UpdateHandler().HandleAsync(Context(Editor), record, new UpdateFormRecordRequest(1, new Dictionary<string, string> { ["note"] = "edited" }), "share-1", Ct));
        Assert.Equal("CONTENT_REVISION_CONFLICT", stale.ErrorCode);
        Assert.Empty(world.Encryption.SelfSealed);
        Assert.Empty(world.Encryption.Verified);
    }

    [Fact]
    public async Task TheHarnessStoreReturnsNewInstancesSoStoredRowsAreNeverSelfSealed()
    {
        // Guards the harness itself: if the fake returned the handler's own instances, the tests above would prove nothing.
        var (world, created) = await CreatedAsync();
        var first = world.StoredEncrypted(created.FormRecordId);
        var second = world.StoredEncrypted(created.FormRecordId);
        Assert.All(first, row => Assert.DoesNotContain(second, other => ReferenceEquals(other, row)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptSelfSealedAsync(world.Encryption, first, Ct));
        Assert.Equal(["h-1", "n-1", "s-1"], (await world.Encryption.DecryptVerifiedAsync(Org, first, Ct)).Order(StringComparer.Ordinal));
    }
}
