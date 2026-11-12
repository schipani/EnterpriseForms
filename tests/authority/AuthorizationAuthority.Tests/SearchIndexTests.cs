using System.Reflection;
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-1 (C-126) unit tests of the Authority-owned search index: derivation at the content commit (real ContentService flow on
/// the in-memory store), ES256 seals with a pinned key, coverage lifecycle, INV-R, the cheap/seal sweeps and the C-124
/// deadline contract (K1-K9). Atomicity, RLS, grants and the database INV-R trigger need real PostgreSQL: they are the
/// Docker integration tests (SearchIndexFoundationTests).
/// </summary>
public sealed class SearchIndexTests
{
    private const string Issuer = "https://issuer.test";

    internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class RecordingAlerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }

    /// <summary>IX-2: a MAC provider that can be switched to unavailable (IX-27 provider outage during backfill).</summary>
    internal sealed class SwitchableMac(IIntegrityMacProvider inner) : IIntegrityMacProvider
    {
        public bool Unavailable { get; set; }

        public string ProviderName => inner.ProviderName;

        public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
            Unavailable ? throw new IntegrityProviderUnavailableException("test outage") : inner.SignAsync(keyReference, canonicalPayload, cancellationToken);

        public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken) =>
            Unavailable ? throw new IntegrityProviderUnavailableException("test outage") : inner.VerifyAsync(keyReference, canonicalPayload, expectedMac, cancellationToken);

        public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => inner.ProvisionKeyAsync(organizationId, keyPurpose, providerKeyName, cancellationToken);

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

        public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) => inner.InspectKeyAsync(providerKeyName, cancellationToken);
    }

    internal sealed class Harness : IDisposable
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid Form { get; } = Ids.Form;
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Contributor { get; } = Guid.CreateVersion7();
        public Guid ContributorBindingId { get; } = Guid.CreateVersion7();
        public Guid StringField { get; } = Guid.CreateVersion7();
        public Guid IntegerField { get; } = Guid.CreateVersion7();
        public Guid OptionField { get; } = Guid.CreateVersion7();
        public Guid EncryptedField { get; } = Guid.CreateVersion7();
        public Guid GroupField { get; } = Guid.CreateVersion7();
        public Guid UnapprovedField { get; } = Guid.CreateVersion7();
        public Guid OptionA { get; } = Guid.CreateVersion7();
        public Guid OptionB { get; } = Guid.CreateVersion7();
        public InMemoryAuthorityStore Store { get; } = new();
        public ContentService Content { get; }
        public SearchIndexWriter Writer { get; }
        public SearchSealVerifier Verifier { get; }
        public SearchSealSigningGate Gate { get; }
        public SearchIndexOptions Options { get; } = new() { Mode = SearchIndexMode.Dark };
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        public RecordingAlerts Alerts { get; } = new();
        public SearchIndexSweepService Sweep { get; }
        public Dictionary<Guid, List<StagedContentItem>> Accepted { get; } = [];

        /// <summary>IX-2: the SpiceDB double (decisions answer from its Allowed set), the MAC switch, and the builder and rebuild services.</summary>
        public SearchCatalogueTests.SpiceDb Spice { get; }

        public SwitchableMac Mac { get; }

        public SearchIndexBuilderService Builder { get; }

        public SearchProjectionRebuildService Rebuild { get; }

        private readonly ECDsa sealKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly LocalSearchSealSigner signer;

        public Harness(ISearchSealSigner? signerOverride = null, byte[]? pinnedSpki = null, bool contributorWrites = false)
        {
            var local = new LocalHmacProvider();
            local.ProvisionKeyAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, "org-key", CancellationToken.None).GetAwaiter().GetResult();
            var mac = Mac = new SwitchableMac(local);
            var spice = DispatchProxy.Create<ISpiceDbProjection, SearchCatalogueTests.SpiceDb>();
            Spice = (SearchCatalogueTests.SpiceDb)(object)spice;
            Spice.Markers = org => TestProjection.MarkerFor(Store.Organizations.Single(o => o.OrganizationId == org));
            var sealOptions = new SearchSealOptions
            {
                Provider = SearchSealProviderKind.LocalDevelopment,
                LocalPrivateKeyPkcs8Base64 = Convert.ToBase64String(sealKey.ExportPkcs8PrivateKey()),
                PinnedKeys = [new PinnedSealKey { KeyName = "search-entry-seal-v1", KeyVersion = 1, SubjectPublicKeyInfoBase64 = Convert.ToBase64String(pinnedSpki ?? sealKey.ExportSubjectPublicKeyInfo()) }],
            };
            signer = new LocalSearchSealSigner(sealOptions);
            Verifier = new SearchSealVerifier(sealOptions);
            Gate = new SearchSealSigningGate(signerOverride ?? signer, Verifier);
            Writer = new SearchIndexWriter(Options, Gate);
            var projectionVerifier = TestProjection.Verifier(Store, spice);
            Content = new ContentService(Store, mac, projectionVerifier, new NoOpFaultInjector(), TimeProvider.System, new ShareContributorWriteSwitch(contributorWrites, testingOverrideAllowed: true), new ContentRevisionCoordinator(Writer, TimeProvider.System));
            Sweep = new SearchIndexSweepService(Store, Writer, Verifier, Options, Clock, Alerts);
            Builder = new SearchIndexBuilderService(Store, Content, Writer, Options, Clock, Alerts);
            Rebuild = new SearchProjectionRebuildService(Store, new AuthorizationAuthority.Application.Features.Authorization.PermissionCheckService(Store, projectionVerifier), Options, Clock);
            Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest());
            Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = Owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Owner.ToString("D"), IdentityBindingEnabled = true });
            Store.Bindings.Add(new IdentityBinding { IdentityBindingId = ContributorBindingId, OrganizationId = Org, AuthorityUserId = Contributor, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Contributor.ToString("D"), IdentityBindingEnabled = true });
            Store.Keys.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = Org, ProviderName = "LocalDevelopment", ProviderKeyName = "org-key", CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 });
        }

        public OrganizationAuthorizationState State => Store.Organizations.Single();

        public InMemorySearchIndexStore Index => Store.SearchIndex;

        public SearchProjectionState Projection => Index.Projections.Single();

        public FieldSearchApproval Approval(Guid field, string type, params string[] operators)
        {
            var approval = new FieldSearchApproval
            {
                FieldSearchApprovalId = Guid.CreateVersion7(), OrganizationId = Org, FormDefinitionId = Form, FieldDefinitionId = field, ApprovedFieldDataType = type, ApprovedFieldClassification = "Internal",
                ApprovedProtectionMode = "Standard", ApprovedOperatorsJson = System.Text.Json.JsonSerializer.Serialize(operators), ApprovalStateName = FieldSearchApprovalStates.Active, ApprovedOn = Clock.Now,
            };
            Store.Approvals.Add(approval);
            return approval;
        }

        /// <summary>The security-command step of AC-6 approval changes (normally run by SecurityCommandService in the command transaction).</summary>
        public async Task ApplyAsync(IReadOnlyCollection<FieldSearchApproval> added, IReadOnlyCollection<FieldSearchApproval> changed)
        {
            await using var scope = await Store.BeginScopeAsync(CancellationToken.None);
            await Writer.ApplyApprovalChangesAsync(scope, State, added, changed, Clock.Now, CancellationToken.None);
            await scope.CommitAsync(CancellationToken.None);
        }

        public async Task<FieldSearchApproval[]> ApproveStandardFieldsAsync()
        {
            var approvals = new[]
            {
                Approval(StringField, "String", "equals", "startsWith"), Approval(IntegerField, "Integer", "equals"), Approval(OptionField, "Option", "is", "includes"),
                Approval(EncryptedField, "String", "equals"), Approval(GroupField, "String", "equals"),
            };
            await ApplyAsync(approvals, []);
            return approvals;
        }

        public Guid Record()
        {
            var record = Guid.CreateVersion7();
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
            Store.Heads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = Org, FormRecordId = record, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1 });
            Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = Owner, SubjectRelationName = string.Empty });
            return record;
        }

        /// <summary>Mirrors CommandPlanner.CreateShare edges: the record and one field item shared with the contributor.</summary>
        public void ShareFieldWithContributor(Guid record, Guid item)
        {
            var share = Guid.CreateVersion7();
            void Edge(string resourceType, Guid resource, string relation, string subjectType, Guid subject) =>
                Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = resourceType, ResourceEntityId = resource, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = string.Empty });
            Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, share);
            Edge(ResourceTypeNames.RecordShare, share, "contributor", ResourceTypeNames.User, Contributor);
            Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, share);
        }

        public List<Row> Rows(Guid record, string text = "  Case Ref ", long number = 42, Guid? groupOccurrence = null)
        {
            // R-53: a record keeps its group occurrence (an item never moves to another occurrence); tests may still pass one.
            var occurrence = groupOccurrence ?? (occurrences.TryGetValue(record, out var known) ? known : occurrences[record] = Guid.CreateVersion7());
            return
            [
                Header(Org, record, "Submitted"), Text(Org, record, StringField, Item(record, 1), text), Integer(Org, record, IntegerField, Item(record, 2), number),
                Option(Org, record, OptionField, Item(record, 3), OptionA), Option(Org, record, OptionField, Item(record, 4), OptionB), Sealed(Org, record, EncryptedField, Item(record, 5), 7),
                Group(Org, record, occurrence, 1), Text(Org, record, GroupField, Item(record, 6), "in a group", occurrence), Text(Org, record, UnapprovedField, Item(record, 7), "never indexed"),
            ];
        }

        private readonly Dictionary<(Guid, int), Guid> items = [];

        private readonly Dictionary<Guid, Guid> occurrences = [];

        public Guid Item(Guid record, int slot) => items.TryGetValue((record, slot), out var item) ? item : items[(record, slot)] = Guid.CreateVersion7();

        public async Task<(Guid OperationId, List<StagedContentItem> Staged)> PrepareAsync(Guid record, List<Row> rows)
        {
            var head = Head(record);
            var operation = Guid.CreateVersion7();
            var candidate = rows.Select(r => Encode(r, Org, operation, head.CommittedContentRevision + 1)).ToList();
            var request = new PrepareContentChangeRequest(Org, record, operation, State.AuthorizationRevision, head.CommittedContentRevision, Convert.ToBase64String(head.CommittedManifestRoot),
                candidate.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: Accepted.GetValueOrDefault(record));
            var prepared = await Content.PrepareAsync(new VerifiedActor(Issuer, Owner.ToString("D"), "test"), request, "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
            var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            return (prepared.OperationId, candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList());
        }

        public async Task CommitAsync(Guid record, Guid operationId, List<StagedContentItem> staged)
        {
            await Content.CommitAsync(new VerifiedActor(Issuer, Owner.ToString("D"), "test"), operationId, new CommitContentChangeRequest(Org, record, staged), Guid.CreateVersion7(), CancellationToken.None);
            Accepted[record] = staged;
            foreach (var entry in Store.Journal)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            }
        }

        public async Task WriteAsync(Guid record, List<Row>? rows = null)
        {
            var (operation, staged) = await PrepareAsync(record, rows ?? Rows(record));
            await CommitAsync(record, operation, staged);
        }

        public RecordContentHead Head(Guid record) => Store.Heads.Single(h => h.FormRecordId == record);

        public SearchRecordEntry Entry(Guid record) => Index.Entries.Single(e => e.FormRecordId == record);

        public void Dispose()
        {
            sealKey.Dispose();
            signer.Dispose();
            Gate.Dispose();
            Verifier.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Derivation at the content commit (W1) ----------------------------------------------------------------------

    [Fact]
    public async Task CommitDerivesASealedEntryForApprovedTopLevelValuesOnly()
    {
        using var h = new Harness();
        var approvals = await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);

        var entry = h.Entry(record);
        var head = h.Head(record);
        Assert.Equal(head.CommittedOperationId, entry.IndexedOperationId);
        Assert.Equal(head.CommittedContentRevision, entry.IndexedContentRevision);
        Assert.Equal(head.CommittedManifestRoot, entry.IndexedManifestRoot);
        Assert.Equal("Submitted", entry.RecordStatusName);
        Assert.Equal(approvals.Select(a => a.FieldSearchApprovalId).Order(), entry.DerivedFieldSearchApprovalIds.Order());
        Assert.Equal(64, entry.EntrySealSignature.Length);

        var postings = h.Index.Postings.Where(p => p.FormRecordId == record).ToList();
        Assert.Equal("CASE REF", Assert.Single(postings.OfType<SearchStringPosting>()).NormalizedStringValue);
        Assert.Equal(42, Assert.Single(postings.OfType<SearchIntegerPosting>()).IntegerValue);
        Assert.Equal(new[] { h.OptionA, h.OptionB }.Order(), postings.OfType<SearchOptionPosting>().Select(p => p.FieldOptionId).Order());
        Assert.DoesNotContain(postings, p => p.FieldDefinitionId == h.EncryptedField || p.FieldDefinitionId == h.GroupField || p.FieldDefinitionId == h.UnapprovedField);
        Assert.Equal(SearchAnomalyKinds.EncryptedValuePresent, Assert.Single(h.Index.Anomalies, a => a.FormRecordId == record).AnomalyKindName);
        Assert.All(postings, p => Assert.Equal(SHA256.HashData(p.SourceCanonicalBytes), h.Store.ManifestLeaves.Single(l => l.RecordManifestCommitId == head.CurrentRecordManifestCommitId && l.ManifestItemKey.AsSpan().SequenceEqual(p.ManifestItemKey)).CanonicalPayloadDigest));
        Assert.Equal(h.State.ContentIntegrityRevision, h.Projection.ProjectionContentIntegrityRevision);

        await using var scope = await h.Store.BeginScopeAsync(Ct);
        var (outcome, code) = await h.Sweep.VerifyEntryAsync(scope, entry, h.Store.Approvals.ToDictionary(a => a.FieldSearchApprovalId), Ct);
        Assert.Equal((SweepUnitOutcome.Pass, "VALID"), (outcome, code));
    }

    [Fact]
    public async Task ARecommitReplacesTheRecordsEntryAtTheNewHead()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        await h.WriteAsync(record, h.Rows(record, "changed", 7));

        var entry = Assert.Single(h.Index.Entries);
        Assert.Equal(h.Head(record).CommittedOperationId, entry.IndexedOperationId);
        Assert.Equal("CHANGED", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
        Assert.Equal(7, Assert.Single(h.Index.Postings.OfType<SearchIntegerPosting>()).IntegerValue);
        Assert.All(h.Index.Postings, p => Assert.Equal(entry.IndexedOperationId, p.IndexedOperationId));
        Assert.Equal(2, h.Index.FormStates.Single().FormContentRevision);
    }

    [Fact]
    public async Task WithoutCoverageNothingIsIndexedAndTheRevisionStillAdvances()
    {
        using var h = new Harness();
        var record = h.Record();
        await h.WriteAsync(record);
        Assert.Empty(h.Index.Projections);
        Assert.Empty(h.Index.Entries);
        Assert.Empty(h.Index.Postings);
        Assert.Equal(1, h.State.ContentIntegrityRevision);
    }

    [Fact]
    public async Task ATypeThatDoesNotMatchTheApprovalAbortsTheCommit()
    {
        using var h = new Harness();
        await h.ApplyAsync([h.Approval(h.StringField, "Integer", "equals")], []);
        var record = h.Record();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(record));
        Assert.Equal(AuthorityErrorCodes.SearchIndexDerivationFailed, ex.ErrorCode);
        Assert.Empty(h.Index.Entries);
    }

    // ---- ES256 seal and pinned key ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnavailableSealProviderRefusesTheCommitAndWritesNoIndexRow()
    {
        using var h = new Harness(signerOverride: new UnavailableSearchSealSigner("test outage", new SearchSealOptions()));
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(record));
        Assert.Equal(AuthorityErrorCodes.SearchSealUnavailable, ex.ErrorCode);
        Assert.Empty(h.Index.Entries);
        Assert.Empty(h.Index.Postings);
    }

    [Fact]
    public async Task AProviderKeyThatIsNotThePinnedKeyRefusesSigning()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var h = new Harness(pinnedSpki: other.ExportSubjectPublicKeyInfo());
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.WriteAsync(record));
        Assert.Equal(AuthorityErrorCodes.SearchSealUnavailable, ex.ErrorCode);
        Assert.Contains("SEAL_KEY_PIN_MISMATCH", ex.Detail, StringComparison.Ordinal);
        Assert.Empty(h.Index.Entries);
    }

    [Fact]
    public void TheVerifierAcceptsOnlyPinnedKeysAndReportsUnknownVersions()
    {
        using var h = new Harness();
        Assert.Equal(SealVerificationOutcome.UnknownKey, h.Verifier.Verify("search-entry-seal-v1", 2, [1, 2, 3], new byte[64]));
        Assert.Equal(SealVerificationOutcome.UnknownKey, h.Verifier.Verify("another-key", 1, [1, 2, 3], new byte[64]));
        Assert.Equal(SealVerificationOutcome.Invalid, h.Verifier.Verify("search-entry-seal-v1", 1, [1, 2, 3], new byte[64]));
    }

    // ---- Failed and rejected commits publish nothing (OR-3, IX-61) --------------------------------------------------------

    [Fact]
    public async Task AnExpiredOperationWritesNothingToTheIndex()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        var (operation, staged) = await h.PrepareAsync(record, h.Rows(record));
        h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1);
        var revision = h.Projection.ProjectionContentIntegrityRevision;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(record, operation, staged));
        Assert.Equal(AuthorityErrorCodes.OperationExpired, ex.ErrorCode);
        Assert.Empty(h.Index.Entries);
        Assert.Empty(h.Index.FormStates);
        Assert.Equal(revision, h.Projection.ProjectionContentIntegrityRevision);
        Assert.Equal(revision, h.State.ContentIntegrityRevision);
    }

    [Fact]
    public async Task ARevisionConflictLeavesTheFirstCommitsEntryUntouched()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        var first = await h.PrepareAsync(record, h.Rows(record, "first"));
        var second = await h.PrepareAsync(record, h.Rows(record, "second"));
        await h.CommitAsync(record, first.OperationId, first.Staged);
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(record, second.OperationId, second.Staged));
        Assert.Equal(AuthorityErrorCodes.ContentRevisionConflict, ex.ErrorCode);
        Assert.Equal(first.OperationId, h.Entry(record).IndexedOperationId);
        Assert.Equal("FIRST", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
    }

    [Fact]
    public async Task APermissionRevokedBeforeCommitWritesNothingToTheIndex()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record, h.Rows(record, "first"));
        var revision = h.State.ContentIntegrityRevision;
        var formRevision = h.Index.FormStates.Single().FormContentRevision;
        var (operation, staged) = await h.PrepareAsync(record, h.Rows(record, "second"));
        h.Store.Edges.Single(e => e.ResourceEntityId == record && e.RelationshipName == "owner").RemovedBySecurityCommandId = Guid.CreateVersion7(); // revoked after prepare
        await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(record, operation, staged));
        Assert.Equal("PERMISSION_REVOKED_BEFORE_COMMIT", h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.NotEqual(operation, h.Entry(record).IndexedOperationId);
        Assert.Equal("FIRST", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
        Assert.Equal(formRevision, h.Index.FormStates.Single().FormContentRevision);
        Assert.Equal(revision, h.State.ContentIntegrityRevision);
        Assert.Equal(revision, h.Projection.ProjectionContentIntegrityRevision);
    }

    [Fact]
    public async Task StagedBytesThatDifferFromTheCandidateWriteNothingToTheIndex()
    {
        // IX-77 (OR-3): a staged-byte verification failure (CandidateMismatch) publishes no posting and advances nothing.
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record, h.Rows(record, "first"));
        var revision = h.State.ContentIntegrityRevision;
        var (operation, staged) = await h.PrepareAsync(record, h.Rows(record, "second"));
        var target = staged.FindIndex(s => s.ItemKind != ManifestItemKind.RecordHeader);
        var bytes = Convert.FromBase64String(staged[target].CanonicalBytesBase64);
        bytes[^1] ^= 0x01;
        staged[target] = staged[target] with { CanonicalBytesBase64 = Convert.ToBase64String(bytes) };
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(record, operation, staged));
        Assert.Equal(AuthorityErrorCodes.CandidateMismatch, ex.ErrorCode);
        Assert.NotEqual(operation, h.Entry(record).IndexedOperationId);
        Assert.Equal("FIRST", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
        Assert.Equal(revision, h.State.ContentIntegrityRevision);
        Assert.Equal(revision, h.Projection.ProjectionContentIntegrityRevision);
    }

    [Fact]
    public async Task ARecordLockedAtCommitWritesNothingToTheIndex()
    {
        // IX-61, record-locked path (OR-3). The commit-time lock check (RECORD_LOCKED_BEFORE_COMMIT) sits behind the prepare-time
        // check and the head compare-and-swap, so normal calls cannot reach it. It is reached here by simulating an operation
        // whose prepare-time check was bypassed: the owner prepares an edit of one field of a Submitted record; the stored
        // operation is then given the share-editor classification (Update of the shared field only, everything else Unchanged)
        // and re-bound to a contributor who holds a valid share on that field.
        using var h = new Harness(contributorWrites: true);
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        var occurrence = Guid.CreateVersion7();
        await h.WriteAsync(record, h.Rows(record, "first", groupOccurrence: occurrence));
        var revision = h.State.ContentIntegrityRevision;
        var formRevision = h.Index.FormStates.Single().FormContentRevision;
        var entry = h.Entry(record).SearchRecordEntryId;
        h.ShareFieldWithContributor(record, h.Item(record, 1));
        var (operation, staged) = await h.PrepareAsync(record, h.Rows(record, "locked edit", groupOccurrence: occurrence));
        h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).ActorIdentityBindingId = h.ContributorBindingId;
        foreach (var leaf in h.Store.CandidateLeaves.Where(l => l.ContentChangeOperationId == operation))
        {
            leaf.MutationKindName = leaf.ManifestItemId == h.Item(record, 1) ? ContentMutationKinds.Update : ContentMutationKinds.Unchanged;
        }

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Content.CommitAsync(new VerifiedActor(Issuer, h.Contributor.ToString("D"), "test"), operation, new CommitContentChangeRequest(h.Org, record, staged), Guid.CreateVersion7(), CancellationToken.None));
        Assert.True(ex.ErrorCode == AuthorityErrorCodes.RecordNotEditable, ex.ErrorCode + ": " + ex.Detail + " / " + h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.Equal("RECORD_LOCKED_BEFORE_COMMIT", h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.Equal(entry, h.Entry(record).SearchRecordEntryId);
        Assert.Equal("FIRST", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
        Assert.Equal(formRevision, h.Index.FormStates.Single().FormContentRevision);
        Assert.Equal(revision, h.State.ContentIntegrityRevision);
        Assert.Equal(revision, h.Projection.ProjectionContentIntegrityRevision);
    }

    [Fact]
    public async Task ACommitUsesOneResetCommandAndTheRegisteredFormFromTheCommit()
    {
        // IX-1 remediation: a derivation writes the record index through one reset (binding, delete, form-state increment) and
        // queues the new rows; a commit without coverage resets only to remove a stale entry (nothing to remove here).
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        Assert.Equal([(h.Org, record, true)], h.Index.Resets);
        Assert.Equal([h.Org], h.Index.BoundOrganizations.Distinct());
        Assert.Equal(1, h.Index.FormStates.Single().FormContentRevision);

        await h.WriteAsync(record, h.Rows(record, "second"));
        Assert.Equal(2, h.Index.Resets.Count);
        Assert.Equal(2, h.Index.FormStates.Single().FormContentRevision);
        Assert.Equal("SECOND", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
    }

    // ---- Coverage lifecycle -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithdrawalPurgesTheWholeFormAndReturnsSiblingCoveragesToBuilding()
    {
        using var h = new Harness();
        var approvals = await h.ApproveStandardFieldsAsync();
        var one = h.Record();
        var two = h.Record();
        await h.WriteAsync(one);
        await h.WriteAsync(two);
        var sibling = h.Index.Coverages.Single(c => c.FieldSearchApprovalId == approvals[1].FieldSearchApprovalId);
        sibling.CoverageStateName = SearchCoverageStates.Complete;
        sibling.CoverageCompletedOn = h.Clock.Now;
        var revisionBefore = h.Index.FormStates.Single().FormContentRevision;

        approvals[0].ApprovalStateName = FieldSearchApprovalStates.Withdrawn;
        await h.ApplyAsync([], [approvals[0]]);

        Assert.Empty(h.Index.Entries);
        Assert.Empty(h.Index.Postings);
        Assert.Empty(h.Index.Anomalies);
        Assert.Equal(SearchCoverageStates.Withdrawn, h.Index.Coverages.Single(c => c.FieldSearchApprovalId == approvals[0].FieldSearchApprovalId).CoverageStateName);
        Assert.Equal(SearchCoverageStates.Building, sibling.CoverageStateName);
        Assert.Null(sibling.CoverageCompletedOn);
        Assert.True(h.Index.FormStates.Single().FormContentRevision > revisionBefore);

        await h.WriteAsync(one);
        Assert.DoesNotContain(h.Index.Postings, p => p.FieldDefinitionId == h.StringField);
        Assert.DoesNotContain(approvals[0].FieldSearchApprovalId, h.Entry(one).DerivedFieldSearchApprovalIds);
    }

    [Fact]
    public async Task ApprovalChangesDoNothingWhenTheIndexIsDisabled()
    {
        using var h = new Harness();
        h.Options.Mode = SearchIndexMode.Disabled;
        await h.ApplyAsync([h.Approval(h.StringField, "String", "equals")], []);
        Assert.Empty(h.Index.Projections);
        Assert.Empty(h.Index.Coverages);
    }

    // ---- INV-R (OR-2) -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EventsWithoutIndexChangeKeepTheProjectionAtTheContentRevision()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var coordinator = new ContentRevisionCoordinator(h.Writer, TimeProvider.System);
        await using var scope = await h.Store.BeginScopeAsync(Ct);
        for (var i = 0; i < 25; i++)
        {
            await coordinator.AdvanceWithoutIndexChangeAsync(scope, h.State, Ct); // W2-W4: e.g. repeated legitimate snapshot mismatches
        }

        Assert.Equal(25, h.State.ContentIntegrityRevision);
        Assert.Equal(h.State.ContentIntegrityRevision, h.Projection.ProjectionContentIntegrityRevision);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
    }

    [Fact]
    public async Task ACommitWhileTheIndexIsDisabledMarksReconciliationRequiredNotQuarantine()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var disabled = new ContentRevisionCoordinator(new SearchIndexWriter(new SearchIndexOptions { Mode = SearchIndexMode.Disabled }, null), TimeProvider.System);
        var record = h.Record();
        await using var scope = await h.Store.BeginScopeAsync(Ct);
        var previous = h.State.ContentIntegrityRevision;
        await disabled.AdvanceForCommitAsync(scope, h.State, new CommitIndexInput(record, h.Head(record), [], DateTimeOffset.UtcNow), Ct);
        Assert.Equal(SearchProjectionStatuses.ReconciliationRequired, h.Projection.ProjectionStatusName);
        Assert.Equal(previous, h.Projection.ReconciliationFromRevision);
        Assert.Equal(previous + 1, h.State.ContentIntegrityRevision);
    }

    // ---- Cheap sweep (S-A) and seal sweep (S-B) ---------------------------------------------------------------------------

    [Fact]
    public async Task ACleanCheapPassAndSealPassWriteTheirScopeTimestamps()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        h.Clock.Now = h.Clock.Now.AddMinutes(30);

        var cheap = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        var seals = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal(SweepUnitOutcome.Pass, cheap.Outcome);
        Assert.Equal((SweepUnitOutcome.Pass, 1), (seals.Outcome, seals.UnitsPassed));
        Assert.Equal(h.Clock.Now, h.Projection.LastSuccessfulCheapPassOn);
        Assert.Equal(h.Clock.Now, h.Entry(record).LastSealVerifiedOn);
        Assert.Equal("VALID", h.Entry(record).LastSealAttemptOutcomeCode);
    }

    public static TheoryData<string, string> Tamperings() => new()
    {
        { "typed-value", "POSTING_VALUE_MISMATCH" },
        { "seal-signature", "SEAL_INVALID" },
        { "approval-operators", "SEAL_INVALID" },
        { "record-status", "SEAL_INVALID" },
        { "deleted-posting", "ENTRY_COUNT_MISMATCH" },
        { "leaf-digest", "POSTING_NOT_IN_ACCEPTED_MANIFEST" },
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task AnAuthorityDomainChangeFailsTheSealPassAndQuarantinesOnlyTheIndex(string tampering, string expected)
    {
        using var h = new Harness();
        var approvals = await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        var head = h.Head(record);
        switch (tampering)
        {
            case "typed-value":
                h.Index.Postings.OfType<SearchStringPosting>().Single().NormalizedStringValue = "OTHER";
                break;
            case "seal-signature":
                h.Entry(record).EntrySealSignature[5] ^= 0x01;
                break;
            case "approval-operators":
                approvals[0].ApprovedOperatorsJson = "[\"equals\",\"startsWith\",\"contains\"]"; // an Authority-domain writer widening the operators
                break;
            case "record-status":
                h.Entry(record).RecordStatusName = "Draft";
                break;
            case "deleted-posting":
                h.Index.Postings.Remove(h.Index.Postings.OfType<SearchIntegerPosting>().Single());
                break;
            case "leaf-digest":
                var leaf = h.Store.ManifestLeaves.Single(l => l.RecordManifestCommitId == head.CurrentRecordManifestCommitId && l.ManifestItemKey.AsSpan().SequenceEqual(MerkleManifestV1.ItemKey(MerkleItemKind.FieldValue, h.Item(record, 1))));
                leaf.CanonicalPayloadDigest = new byte[32];
                break;
        }

        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Fail, expected), (result.Outcome, result.FindingCode));
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
        Assert.False(h.State.IntegrityQuarantineEnabled); // the organization (S1) is untouched
        Assert.Contains(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_QUARANTINE" && a.Severity == "Critical");
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchIndexQuarantined");
    }

    [Fact]
    public async Task AQuarantineIsNeverClearedByLaterSuccessfulPasses()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        h.Entry(record).EntrySealSignature[0] ^= 0xFF;
        await h.Sweep.RunSealPassAsync(h.Org, Ct);
        h.Entry(record).EntrySealSignature[0] ^= 0xFF; // even if the evidence is restored, nothing clears the quarantine automatically

        for (var i = 0; i < 3; i++)
        {
            h.Clock.Now = h.Clock.Now.AddMinutes(15);
            await h.Sweep.RunCheapPassAsync(h.Org, Ct);
            await h.Sweep.RunSealPassAsync(h.Org, Ct);
            await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        }

        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
    }

    [Fact]
    public async Task AnUnknownSealKeyIsInconclusiveAndDoesNotAdvanceTheDeadline()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        var entry = h.Entry(record);
        entry.SealKeyVersion = 99;
        var before = entry.LastSealVerifiedOn;
        h.Clock.Now = h.Clock.Now.AddHours(2);

        var result = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Inconclusive, 0, 1), (result.Outcome, result.UnitsPassed, result.UnitsInconclusive));
        Assert.Equal(before, entry.LastSealVerifiedOn);
        Assert.Equal(h.Clock.Now, entry.LastSealAttemptedOn);
        Assert.Equal("SEAL_KEY_NOT_PINNED", entry.LastSealAttemptOutcomeCode);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
    }

    [Fact]
    public async Task ResidueOfAWithdrawnCoverageIsACheapSweepFinding()
    {
        using var h = new Harness();
        var approvals = await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        h.Index.Coverages.Single(c => c.FieldSearchApprovalId == approvals[1].FieldSearchApprovalId).CoverageStateName = SearchCoverageStates.Withdrawn; // purge skipped by a defect
        var lastPass = h.Projection.LastSuccessfulCheapPassOn;

        var result = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal(SweepUnitOutcome.Fail, result.Outcome);
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
        Assert.Equal(lastPass, h.Projection.LastSuccessfulCheapPassOn); // D1: a failing pass writes no completion
    }

    [Fact]
    public async Task ARevisionMismatchRequiresReconciliationAndNeverQuarantines()
    {
        using var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        h.State.AdvanceContentIntegrityRevision(ContentRevisionProjectionAction.AdvanceWithDerivation); // INV-R bypassed (old image or out-of-band write)
        h.Head(record).CommittedOperationId = Guid.CreateVersion7(); // and the record moved without derivation

        var first = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal("RECONCILIATION_REQUIRED", first.FindingCode);
        Assert.Equal(SearchProjectionStatuses.ReconciliationRequired, h.Projection.ProjectionStatusName);
        Assert.Contains(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_RECONCILIATION_REQUIRED" && a.Severity == "High");

        var second = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal(SweepUnitOutcome.Pass, second.Outcome);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
        Assert.Equal(h.State.ContentIntegrityRevision, h.Projection.ProjectionContentIntegrityRevision);
        Assert.Empty(h.Index.Entries); // the stale entry was deleted, never re-signed
        Assert.DoesNotContain(h.Store.Journal, j => j.JournalEventKind == "SearchIndexQuarantined");
    }

    // ---- Deadlines (C-124 contract, K1-K9) ---------------------------------------------------------------------------------

    private static async Task<Harness> IndexedAsync()
    {
        var h = new Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        var baseline = h.Clock.Now;
        h.Projection.ProjectionCreatedOn = baseline;
        h.Projection.LastSuccessfulCheapPassOn = baseline;
        h.Entry(record).LastSealVerifiedOn = baseline;
        h.Entry(record).LastDeepVerifiedOn = baseline;
        return h;
    }

    [Theory]
    [InlineData("S-A", 3600)]
    [InlineData("S-B", 86_400)]
    [InlineData("S-C", 2_592_000)]
    public async Task EachDeadlineIsEnforcedAtExactlyItsLengthWithoutGrace(string scope, int deadlineSeconds)
    {
        using var h = await IndexedAsync();
        var baseline = h.Clock.Now;
        void Refresh(DateTimeOffset at)
        {
            if (scope != "S-A")
            {
                h.Projection.LastSuccessfulCheapPassOn = at;
            }

            if (scope != "S-B")
            {
                h.Index.Entries.ForEach(e => e.LastSealVerifiedOn = at);
            }

            if (scope != "S-C")
            {
                h.Index.Entries.ForEach(e => e.LastDeepVerifiedOn = at);
            }
        }

        h.Clock.Now = baseline.AddSeconds(deadlineSeconds - 1);
        Refresh(h.Clock.Now);
        var before = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.False(before.Suspended, $"{scope} suspended one second before its deadline");

        h.Clock.Now = baseline.AddSeconds(deadlineSeconds + 1);
        Refresh(h.Clock.Now);
        var after = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.True(after.Suspended);
        Assert.Equal([scope], after.OverdueScopes);
        Assert.Equal(scope, h.Projection.SuspendedScopeNames);
    }

    [Fact]
    public async Task SuspensionClearsOnlyWhenEveryOverdueScopeHasPassedAgain()
    {
        using var h = await IndexedAsync();
        h.Clock.Now = h.Clock.Now.AddHours(25); // S-A and S-B overdue
        Assert.Equal(["S-A", "S-B"], (await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct)).OverdueScopes);

        Assert.Equal(SweepUnitOutcome.Pass, (await h.Sweep.RunCheapPassAsync(h.Org, Ct)).Outcome);
        var afterCheap = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.True(afterCheap.Suspended);
        Assert.Equal(["S-B"], afterCheap.OverdueScopes);

        Assert.Equal(SweepUnitOutcome.Pass, (await h.Sweep.RunSealPassAsync(h.Org, Ct)).Outcome);
        var afterSeals = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.False(afterSeals.Suspended);
        Assert.Null(h.Projection.SuspendedScopeNames);
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchIndexVerificationResumed");
    }

    [Fact]
    public async Task InconclusiveOrInterruptedRunsNeverMoveADeadline()
    {
        using var h = await IndexedAsync();
        var entry = h.Index.Entries.Single();
        entry.SealKeyVersion = 99; // every S-B attempt is Inconclusive
        h.Clock.Now = h.Clock.Now.AddHours(25);
        await h.Sweep.RunSealPassAsync(h.Org, Ct);
        await h.Sweep.RunSealPassAsync(h.Org, Ct);
        var result = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.Contains("S-B", result.OverdueScopes);
        Assert.True(result.Suspended);
    }

    [Fact]
    public async Task AFutureVerificationTimestampIsInconclusiveAndCannotClearASuspension()
    {
        using var h = await IndexedAsync();
        h.Clock.Now = h.Clock.Now.AddHours(25);
        Assert.True((await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct)).Suspended);
        h.Projection.LastSuccessfulCheapPassOn = h.Clock.Now;
        h.Index.Entries.Single().LastSealVerifiedOn = h.Clock.Now.AddMinutes(2); // more than 1 minute in the future
        var result = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.True(result.Suspended);
        Assert.Equal(["S-B"], result.OverdueScopes);

        h.Index.Entries.Single().LastSealVerifiedOn = h.Clock.Now.AddSeconds(30); // within the tolerance: accepted
        Assert.False((await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct)).Suspended);
    }

    [Fact]
    public async Task ABackwardClockStepNeverClearsASuspensionAndAForwardStepOnlySuspends()
    {
        using var h = await IndexedAsync();
        var baseline = h.Clock.Now;
        h.Clock.Now = baseline.AddHours(2); // S-A overdue
        Assert.True((await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct)).Suspended);

        h.Clock.Now = baseline.AddMinutes(10); // the clock goes back: by wall time S-A would look current again
        var back = await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.True(back.Suspended);
        Assert.Contains("S-A", back.OverdueScopes);
        Assert.Equal(baseline.AddHours(2), h.Projection.LastEvaluationOn);

        h.Projection.LastSuccessfulCheapPassOn = baseline.AddMinutes(10); // a pass recorded on the stepped-back clock
        Assert.True((await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct)).Suspended);

        using var fresh = await IndexedAsync();
        fresh.Clock.Now = fresh.Clock.Now.AddDays(40); // a forward step: suspends (S-A, S-B, S-C), never clears
        var forward = await fresh.Sweep.EvaluateDeadlinesAsync(fresh.Org, Ct);
        Assert.True(forward.Suspended);
        Assert.Equal(["S-A", "S-B", "S-C"], forward.OverdueScopes);
    }

    [Fact]
    public async Task ASuspendedIndexIsReportedOnceAndJournaled()
    {
        using var h = await IndexedAsync();
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
        Assert.Single(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_VERIFICATION_OVERDUE");
        Assert.Single(h.Store.Journal, j => j.JournalEventKind == "SearchIndexVerificationSuspended");
        Assert.True(h.Projection.VerificationSuspended);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName); // suspension is not quarantine
    }
}
