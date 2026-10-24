using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Phase C7 M1 — AC-5 Authority record catalogue (C-56 D1, C-60 OD-1/OD-5/OD-6/OD-8), at unit level against the real
/// catalogue and content services, real signed manifests (LocalHmacProvider) and the in-memory store; SpiceDB answers from
/// explicit sets. The real PostgreSQL/SpiceDB/Vault path runs in the Stage B integration suite (SearchCatalogueTests).
/// </summary>
public sealed class SearchCatalogueTests
{
    private const string Issuer = "https://issuer.test";

    /// <summary>SpiceDB stand-in: checks and discover lookups answer from <see cref="Allowed"/>; <see cref="OnLookup"/> runs during a lookup.</summary>
    public class SpiceDb : DispatchProxy
    {
        public HashSet<(string Type, string Id, string Permission, string User)> Allowed { get; } = [];

        public Action? OnLookup { get; set; }

        /// <summary>Runs once, on the first permission check after it is set.</summary>
        public Action? OnCheck { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ISpiceDbProjection.CheckAsync):
                    var hook = OnCheck;
                    OnCheck = null;
                    hook?.Invoke();
                    var check = (SpiceDbCheck)args![0]!;
                    return Task.FromResult(Outcome(check));
                case nameof(ISpiceDbProjection.CheckBulkAsync):
                    var checks = (IReadOnlyList<SpiceDbCheck>)args![0]!;
                    return Task.FromResult<IReadOnlyList<SpiceDbCheckOutcome>>(checks.Select(Outcome).ToList());
                case nameof(ISpiceDbProjection.LookupResourcesAsync):
                    OnLookup?.Invoke();
                    var (type, permission, user) = ((string)args![0]!, (string)args[1]!, (string)args[3]!);
                    return Task.FromResult<IReadOnlyList<string>>(Allowed.Where(a => a.Type == type && a.Permission == permission && a.User == user).Select(a => a.Id).ToList());
                default:
                    throw new NotSupportedException("unexpected SpiceDB call " + targetMethod?.Name);
            }
        }

        private SpiceDbCheckOutcome Outcome(SpiceDbCheck check) => Allowed.Contains((check.ResourceType, check.ResourceId, check.Permission, check.SubjectId)) ? SpiceDbCheckOutcome.Allowed : SpiceDbCheckOutcome.Denied;
    }

    private sealed class Harness
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid FormA { get; } = Guid.CreateVersion7();
        public Guid FormB { get; } = Guid.CreateVersion7();
        public Guid Field { get; } = Guid.CreateVersion7();
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Other { get; } = Guid.CreateVersion7();
        public InMemoryAuthorityStore Store { get; } = new();
        public SpiceDb Spice { get; }
        public ContentService Content { get; }
        public SearchCatalogueService Catalogue { get; }
        public CatalogueOptions Options { get; } = new() { MaximumUserEntries = 500, MigrationPageSize = 2 };
        public Dictionary<Guid, List<StagedContentItem>> Accepted { get; } = [];
        public Dictionary<Guid, Guid> AcceptedOperation { get; } = [];

        public Harness()
        {
            var mac = new LocalHmacProvider();
            mac.ProvisionKeyAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, "org-key", CancellationToken.None).GetAwaiter().GetResult();
            var spice = DispatchProxy.Create<ISpiceDbProjection, SpiceDb>();
            Spice = (SpiceDb)(object)spice;
            Content = new ContentService(Store, mac, spice, new NoOpFaultInjector(), TimeProvider.System, new ShareContributorWriteSwitch(false, testingOverrideAllowed: true));
            Catalogue = new SearchCatalogueService(Store, spice, TimeProvider.System, Options);
            Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous });
            foreach (var user in new[] { Owner, Other })
            {
                Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = user.ToString("D"), IdentityBindingEnabled = true });
            }

            Store.Keys.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = Org, ProviderName = "LocalDevelopment", ProviderKeyName = "org-key", CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 });
        }

        public OrganizationAuthorizationState State => Store.Organizations.Single();

        public static VerifiedActor Actor(Guid user) => new(Issuer, user.ToString("D"), "test");

        /// <summary>Registers a record of <paramref name="form"/> owned by <see cref="Owner"/>; with content unless <paramref name="withContent"/> is false.</summary>
        public async Task<Guid> RecordAsync(Guid form, bool discoverableByOwner = true, bool withContent = true, string value = "value")
        {
            var record = Guid.CreateVersion7();
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, ParentResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active });
            Store.Heads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = Org, FormRecordId = record, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1 });
            Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = Owner, SubjectRelationName = string.Empty });
            if (discoverableByOwner)
            {
                Allow(ResourceTypeNames.FormRecord, record, "discover", Owner);
                Allow(ResourceTypeNames.FormRecord, record, "read", Owner);
            }

            if (withContent)
            {
                await WriteAsync(record, value);
            }

            return record;
        }

        public async Task WriteAsync(Guid record, string value)
        {
            var head = Head(record);
            var operation = Guid.CreateVersion7();
            var rows = new List<Row> { Header(Org, record), Text(Org, record, Field, ItemFor(record), value) };
            var candidate = rows.Select(r => Encode(r, Org, operation, head.CommittedContentRevision + 1)).ToList();
            var current = Accepted.GetValueOrDefault(record);
            var request = new PrepareContentChangeRequest(Org, record, operation, State.AuthorizationRevision, head.CommittedContentRevision, Convert.ToBase64String(head.CommittedManifestRoot),
                candidate.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: current);
            var prepared = await Content.PrepareAsync(Actor(Owner), request, "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
            var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            var staged = candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
            await Content.CommitAsync(Actor(Owner), prepared.OperationId, new CommitContentChangeRequest(Org, record, staged), Guid.CreateVersion7(), CancellationToken.None);
            Accepted[record] = staged;
            AcceptedOperation[record] = prepared.OperationId;
            foreach (var entry in Store.Journal)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            }
        }

        private readonly Dictionary<Guid, Guid> items = [];

        public Guid ItemFor(Guid record) => items.TryGetValue(record, out var item) ? item : items[record] = Guid.CreateVersion7();

        public RecordContentHead Head(Guid record) => Store.Heads.Single(h => h.FormRecordId == record);

        public AuthorityResource Resource(Guid record) => Store.Resources.Single(r => r.ResourceEntityId == record && r.ResourceTypeName == ResourceTypeNames.FormRecord);

        public void Allow(string type, Guid id, string permission, Guid user) => Spice.Allowed.Add((type, id.ToString("D"), permission, user.ToString("D")));

        /// <summary>A security change: the in-memory store hands out one state object, so a "moved" state is a replaced row.</summary>
        public void AdvanceAuthorization()
        {
            var s = State;
            Store.Organizations[0] = Clone(s, s.AuthorizationRevision + 1, s.ProjectionAppliedRevision + 1, s.ContentIntegrityRevision);
        }

        public void AdvanceContentRevision()
        {
            var s = State;
            Store.Organizations[0] = Clone(s, s.AuthorizationRevision, s.ProjectionAppliedRevision, s.ContentIntegrityRevision + 1);
        }

        private static OrganizationAuthorizationState Clone(OrganizationAuthorizationState s, long authorization, long projection, long content) => new()
        {
            OrganizationAuthorizationStateId = s.OrganizationAuthorizationStateId,
            OrganizationId = s.OrganizationId,
            AuthorizationRevision = authorization,
            ProjectionAppliedRevision = projection,
            ProjectionZedToken = s.ProjectionZedToken,
            ContentIntegrityRevision = content,
            CheckpointEnforcementModeName = s.CheckpointEnforcementModeName,
            LatestSecurityJournalDigest = s.LatestSecurityJournalDigest,
            LatestContentJournalDigest = s.LatestContentJournalDigest,
            AuditJournalRevision = s.AuditJournalRevision,
            LatestAuditJournalDigest = s.LatestAuditJournalDigest,
        };

        public Task<SearchCatalogueResponse> UserCatalogueAsync(Guid user, Guid form, int maximum = 500) =>
            Catalogue.IssueUserCatalogueAsync(Actor(user), new SearchCatalogueRequest(Org, form, maximum), Guid.CreateVersion7(), CancellationToken.None);

        public Task<CatalogueSnapshotValidation> ValidateAsync(Guid user, Guid snapshot) =>
            Catalogue.ValidateUserSnapshotAsync(Actor(user), snapshot, Guid.CreateVersion7(), CancellationToken.None);

        /// <summary>An effective strengthening command of <paramref name="form"/> that is still the field's current protection decision.</summary>
        public Guid ProtectionCommand(Guid form, bool current = true)
        {
            var command = new SecurityCommand
            {
                SecurityCommandId = Guid.CreateVersion7(),
                OrganizationId = Org,
                ActorIdentityBindingId = Store.Bindings[0].IdentityBindingId,
                CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision,
                CommandStateName = SecurityCommandStates.Effective,
                CommandPayloadJson = System.Text.Encoding.UTF8.GetString(CanonicalJson.SerializeBytes(new PublishFormSecurityRevisionPayload(form, Field, "ApplicationEncrypted", 2))),
            };
            Store.Commands.Add(command);
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = Field, ParentResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = current ? command.SecurityCommandId : Guid.CreateVersion7() });
            return command.SecurityCommandId;
        }

        public BatchVerificationItem BatchItem(Guid record, IReadOnlyList<StagedContentItem>? observed = null, Guid? expected = null) =>
            new(record, expected ?? AcceptedOperation[record], observed ?? Accepted[record]);

        public Task<VerifyRecordProjectionsResponse> BatchAsync(Guid user, params BatchVerificationItem[] records) =>
            Content.VerifyProjectionsAsync(Actor(user), new VerifyRecordProjectionsRequest(Org, records), Guid.CreateVersion7(), CancellationToken.None);
    }

    // ---- user-scoped catalogue ----

    [Fact]
    public async Task TheUserCatalogueIsExactlyTheDiscoverableRegisteredRecordsOfTheFormWithTheirAcceptedHeads()
    {
        var h = new Harness();
        var a1 = await h.RecordAsync(h.FormA);
        var a2 = await h.RecordAsync(h.FormA);
        var hidden = await h.RecordAsync(h.FormA, discoverableByOwner: false);
        var otherForm = await h.RecordAsync(h.FormB);
        var catalogue = await h.UserCatalogueAsync(h.Owner, h.FormA);

        Assert.Equal(new[] { a1, a2 }.Order(), catalogue.Entries.Select(e => e.FormRecordId));
        Assert.DoesNotContain(catalogue.Entries, e => e.FormRecordId == hidden || e.FormRecordId == otherForm);
        foreach (var entry in catalogue.Entries)
        {
            Assert.Equal(h.Head(entry.FormRecordId).CommittedContentRevision, entry.CommittedContentRevision);
            Assert.Equal(h.AcceptedOperation[entry.FormRecordId], entry.CommittedOperationId);
            Assert.False(entry.ContentHeadQuarantined);
        }

        Assert.Equal(h.State.AuthorizationRevision, catalogue.AuthorizationRevision);
        Assert.Equal(h.State.ContentIntegrityRevision, catalogue.ContentIntegrityRevision);
        // A user who may discover nothing of the form gets an empty catalogue, not an error.
        Assert.Empty((await h.UserCatalogueAsync(h.Other, h.FormA)).Entries);
    }

    [Fact]
    public async Task TheScopeComesFromTheRegistryNotFromAnyBusinessColumn()
    {
        var h = new Harness();
        var moved = await h.RecordAsync(h.FormA);
        // The business database may claim any form; only the Authority registry parent decides membership.
        Assert.Contains((await h.UserCatalogueAsync(h.Owner, h.FormA)).Entries, e => e.FormRecordId == moved);
        Assert.Empty((await h.UserCatalogueAsync(h.Owner, h.FormB)).Entries);
        // A business row the registry does not know (injected) cannot appear, even if SpiceDB were to list it.
        var injected = Guid.CreateVersion7();
        h.Allow(ResourceTypeNames.FormRecord, injected, "discover", h.Owner);
        Assert.DoesNotContain((await h.UserCatalogueAsync(h.Owner, h.FormA)).Entries, e => e.FormRecordId == injected);
    }

    [Fact]
    public async Task RecordsWithoutContentAndQuarantinedRecordsStayInScopeWithTheirStateRevokedOnesLeave()
    {
        var h = new Harness();
        var empty = await h.RecordAsync(h.FormA, withContent: false);
        var quarantinedHead = await h.RecordAsync(h.FormA);
        var quarantinedResource = await h.RecordAsync(h.FormA);
        var revoked = await h.RecordAsync(h.FormA);
        h.Head(quarantinedHead).ContentHeadQuarantined = true;
        h.Resource(quarantinedResource).ResourceLifecycleState = ResourceLifecycleStates.Quarantined;
        h.Resource(revoked).ResourceLifecycleState = ResourceLifecycleStates.Revoked;

        var entries = (await h.UserCatalogueAsync(h.Owner, h.FormA)).Entries.ToDictionary(e => e.FormRecordId);
        Assert.Equal(0, entries[empty].CommittedContentRevision);
        Assert.Null(entries[empty].CommittedOperationId);
        Assert.True(entries[quarantinedHead].ContentHeadQuarantined);
        Assert.True(entries[quarantinedResource].ResourceQuarantined);
        Assert.False(entries.ContainsKey(revoked));
    }

    [Fact]
    public async Task MoreEligibleRecordsThanTheLimitIsRefusedWithNoEntriesAndAnOverlargeLimitIsInvalid()
    {
        var h = new Harness();
        for (var i = 0; i < 3; i++)
        {
            await h.RecordAsync(h.FormA, withContent: false);
        }

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.UserCatalogueAsync(h.Owner, h.FormA, maximum: 2));
        Assert.Equal(AuthorityErrorCodes.LimitExceeded, refused.ErrorCode);
        Assert.Equal(422, refused.HttpStatusCode);
        Assert.Empty(h.Store.Snapshots);
        Assert.Equal(3, (await h.UserCatalogueAsync(h.Owner, h.FormA, maximum: 3)).Entries.Count);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => h.UserCatalogueAsync(h.Owner, h.FormA, maximum: 501))).ErrorCode);
    }

    [Fact]
    public async Task ACatalogueReadWhileAuthorizationOrContentMovedFailsClosed()
    {
        var h = new Harness();
        await h.RecordAsync(h.FormA);
        h.Spice.OnLookup = h.AdvanceAuthorization;
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, (await Assert.ThrowsAsync<AuthorityException>(() => h.UserCatalogueAsync(h.Owner, h.FormA))).ErrorCode);
        h.Spice.OnLookup = h.AdvanceContentRevision;
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, (await Assert.ThrowsAsync<AuthorityException>(() => h.UserCatalogueAsync(h.Owner, h.FormA))).ErrorCode);
        Assert.Empty(h.Store.Snapshots);
    }

    [Fact]
    public async Task IssuanceIsJournaledOnTheAuditStreamWithoutRecordContentAndWithoutMovingContentRevision()
    {
        var h = new Harness();
        await h.RecordAsync(h.FormA, value: "SECRET-VALUE-123");
        var contentBefore = h.State.ContentIntegrityRevision;
        var catalogue = await h.UserCatalogueAsync(h.Owner, h.FormA);
        var audit = Assert.Single(h.Store.Journal, j => j.JournalStreamName == JournalStreamNames.Audit);
        Assert.Equal("SearchCatalogueIssued", audit.JournalEventKind);
        Assert.Equal(1, audit.JournalStreamRevision);
        Assert.Contains(catalogue.SearchCatalogueSnapshotId.ToString("D"), audit.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-VALUE-123", audit.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(catalogue.Entries[0].FormRecordId.ToString("D"), audit.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Equal(contentBefore, h.State.ContentIntegrityRevision);
        Assert.Equal(1, h.State.AuditJournalRevision);
        Assert.NotNull(h.State.LatestAuditJournalDigest);
    }

    // ---- snapshot validation ----

    [Fact]
    public async Task AFreshSnapshotIsValidAndAnyAuthorizationChangeInvalidatesItPermanently()
    {
        var h = new Harness();
        await h.RecordAsync(h.FormA);
        var snapshot = (await h.UserCatalogueAsync(h.Owner, h.FormA)).SearchCatalogueSnapshotId;
        Assert.True((await h.ValidateAsync(h.Owner, snapshot)).Valid);
        h.AdvanceAuthorization(); // a revocation, a share, a new registration, a team change …
        var invalid = await h.ValidateAsync(h.Owner, snapshot);
        Assert.False(invalid.Valid);
        Assert.Equal(CatalogueInvalidationReasons.AuthorizationChanged, invalid.InvalidationReason);
        Assert.Equal(CatalogueInvalidationReasons.AuthorizationChanged, h.Store.Snapshots.Single().SnapshotInvalidationReason);
    }

    [Fact]
    public async Task ContentChangesElsewhereKeepTheSnapshotButAChangedQuarantinedOrNewlyFilledEntryInvalidatesIt()
    {
        var h = new Harness();
        var inScope = await h.RecordAsync(h.FormA);
        var empty = await h.RecordAsync(h.FormA, withContent: false);
        var elsewhere = await h.RecordAsync(h.FormB);

        var s1 = (await h.UserCatalogueAsync(h.Owner, h.FormA)).SearchCatalogueSnapshotId;
        await h.WriteAsync(elsewhere, "changed elsewhere");
        Assert.True((await h.ValidateAsync(h.Owner, s1)).Valid); // slow path: none of its entries moved

        await h.WriteAsync(inScope, "edited");
        Assert.Equal(CatalogueInvalidationReasons.ContentChanged, (await h.ValidateAsync(h.Owner, s1)).InvalidationReason);

        var s2 = (await h.UserCatalogueAsync(h.Owner, h.FormA)).SearchCatalogueSnapshotId;
        await h.WriteAsync(empty, "first content"); // a newly appearing match must not be missed
        Assert.Equal(CatalogueInvalidationReasons.ContentChanged, (await h.ValidateAsync(h.Owner, s2)).InvalidationReason);

        var s3 = (await h.UserCatalogueAsync(h.Owner, h.FormA)).SearchCatalogueSnapshotId;
        h.Head(inScope).ContentHeadQuarantined = true;
        h.AdvanceContentRevision();
        Assert.Equal(CatalogueInvalidationReasons.ContentChanged, (await h.ValidateAsync(h.Owner, s3)).InvalidationReason);
    }

    [Fact]
    public async Task ARecordThatLeavesTheRegistryScopeDoesNotDisappearSilently()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var snapshot = (await h.UserCatalogueAsync(h.Owner, h.FormA)).SearchCatalogueSnapshotId;
        h.Resource(record).ResourceLifecycleState = ResourceLifecycleStates.Quarantined;
        h.AdvanceContentRevision();
        Assert.Equal(CatalogueInvalidationReasons.RecordUnavailable, (await h.ValidateAsync(h.Owner, snapshot)).InvalidationReason);
    }

    [Fact]
    public async Task ExpiredForeignAndUnknownSnapshotsAreNeverValid()
    {
        var h = new Harness();
        await h.RecordAsync(h.FormA);
        h.Allow(ResourceTypeNames.FormRecord, Guid.CreateVersion7(), "discover", h.Other);
        var mine = (await h.UserCatalogueAsync(h.Owner, h.FormA)).SearchCatalogueSnapshotId;
        Assert.Equal(AuthorityErrorCodes.NotFound, (await Assert.ThrowsAsync<AuthorityException>(() => h.ValidateAsync(h.Other, mine))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.NotFound, (await Assert.ThrowsAsync<AuthorityException>(() => h.ValidateAsync(h.Owner, Guid.CreateVersion7()))).ErrorCode);
        h.Store.Snapshots.Single().SnapshotExpiresOn = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(CatalogueInvalidationReasons.Expired, (await h.ValidateAsync(h.Owner, mine)).InvalidationReason);
    }

    // ---- final AC-5 review (C-66): completeness cases named by the owner ----

    [Fact]
    public async Task ANewlyGrantedDiscoverableRecordInvalidatesTheRecipientsEarlierSnapshotAndEntersTheNextOne()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var before = await h.UserCatalogueAsync(h.Other, h.FormA);
        Assert.Empty(before.Entries);

        h.Allow(ResourceTypeNames.FormRecord, record, "discover", h.Other); // the grant (a security command) …
        h.AdvanceAuthorization();                                          // … advances the authorization revision
        Assert.Equal(CatalogueInvalidationReasons.AuthorizationChanged, (await h.ValidateAsync(h.Other, before.SearchCatalogueSnapshotId)).InvalidationReason);
        Assert.Equal([record], (await h.UserCatalogueAsync(h.Other, h.FormA)).Entries.Select(e => e.FormRecordId));
    }

    [Fact]
    public async Task ARecordMovingBetweenFormsInTheRegistryInvalidatesAndMovesBetweenCatalogues()
    {
        // No command re-parents a record today; if a registry change ever moves one, it is a security change (authorization
        // revision) and both forms' catalogues follow the registry, never a business column.
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var formA = await h.UserCatalogueAsync(h.Owner, h.FormA);
        var formB = await h.UserCatalogueAsync(h.Owner, h.FormB);
        Assert.Equal([record], formA.Entries.Select(e => e.FormRecordId));
        Assert.Empty(formB.Entries);

        h.Resource(record).ParentResourceEntityId = h.FormB;
        h.AdvanceAuthorization();
        Assert.False((await h.ValidateAsync(h.Owner, formA.SearchCatalogueSnapshotId)).Valid);
        Assert.False((await h.ValidateAsync(h.Owner, formB.SearchCatalogueSnapshotId)).Valid);
        Assert.Empty((await h.UserCatalogueAsync(h.Owner, h.FormA)).Entries);
        Assert.Equal([record], (await h.UserCatalogueAsync(h.Owner, h.FormB)).Entries.Select(e => e.FormRecordId));
    }

    [Fact]
    public async Task ResourcesOfAnotherOrganizationNeverEnterTheCatalogueEvenUnderTheSameFormId()
    {
        var h = new Harness();
        var own = await h.RecordAsync(h.FormA);
        var foreignOrganization = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = foreignOrganization, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = foreign, ParentResourceEntityId = h.FormA, ResourceLifecycleState = ResourceLifecycleStates.Active });
        h.Allow(ResourceTypeNames.FormRecord, foreign, "discover", h.Owner); // even a (forged) discover answer for it does not admit it

        var entries = (await h.UserCatalogueAsync(h.Owner, h.FormA)).Entries.Select(e => e.FormRecordId).ToList();
        Assert.Equal([own], entries);
        var migration = await h.Catalogue.IssueMigrationCatalogueAsync("field-protection-verifier", new MigrationCatalogueRequest(h.Org, h.FormA, h.ProtectionCommand(h.FormA)), Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(1, migration.TotalEntryCount);
    }

    [Fact]
    public async Task RefusedUserAndMigrationCataloguesAreJournaledBeforeTheRefusalIsReturned()
    {
        var h = new Harness();
        for (var i = 0; i < 3; i++)
        {
            await h.RecordAsync(h.FormA, withContent: false);
        }

        await Assert.ThrowsAsync<AuthorityException>(() => h.UserCatalogueAsync(h.Owner, h.FormA, maximum: 2));
        var refused = Assert.Single(h.Store.Journal, j => j.JournalStreamName == JournalStreamNames.Audit);
        Assert.Equal("SearchCatalogueRefused", refused.JournalEventKind);
        Assert.Contains("VERIFICATION_LIMIT", refused.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Contains(h.Owner.ToString("D"), refused.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Empty(h.Store.Snapshots);

        h.Options.MaximumMigrationEntries = 2;
        await Assert.ThrowsAsync<AuthorityException>(() => h.Catalogue.IssueMigrationCatalogueAsync("field-protection-verifier", new MigrationCatalogueRequest(h.Org, h.FormA, h.ProtectionCommand(h.FormA)), Guid.CreateVersion7(), CancellationToken.None));
        Assert.Contains(h.Store.Journal, j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == "MigrationCatalogueRefused" && j.JournalEventBodyJson.Contains("MIGRATION_CEILING", StringComparison.Ordinal));
        Assert.Equal(2, h.State.AuditJournalRevision);
    }

    // ---- privileged migration catalogue ----

    [Fact]
    public async Task TheMigrationCatalogueIsTheCompletePopulationIncludingRecordsNoUserCanDiscover()
    {
        var h = new Harness();
        var visible = await h.RecordAsync(h.FormA);
        var hidden = await h.RecordAsync(h.FormA, discoverableByOwner: false);
        var pending = await h.RecordAsync(h.FormA, discoverableByOwner: false, withContent: false);
        h.Resource(pending).ResourceLifecycleState = ResourceLifecycleStates.Registered;
        await h.RecordAsync(h.FormB);
        var command = h.ProtectionCommand(h.FormA);

        var first = await h.Catalogue.IssueMigrationCatalogueAsync("field-protection-verifier", new MigrationCatalogueRequest(h.Org, h.FormA, command), Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(3, first.TotalEntryCount);
        Assert.Equal(2, first.Entries.Count); // page size 2
        Assert.Equal(2, first.NextOffset);
        var second = await h.Catalogue.ReadMigrationCatalogueAsync("field-protection-verifier", first.SearchCatalogueSnapshotId, 2, Guid.CreateVersion7(), CancellationToken.None);
        Assert.Null(second.NextOffset);
        Assert.Equal(new[] { visible, hidden, pending }.Order(), first.Entries.Concat(second.Entries).Select(e => e.FormRecordId));
        Assert.Contains(h.Store.Journal, j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == "MigrationCatalogueIssued");
        // Bound to the issuing workload: another workload name cannot read or validate it.
        await Assert.ThrowsAsync<AuthorityException>(() => h.Catalogue.ReadMigrationCatalogueAsync("someone-else", first.SearchCatalogueSnapshotId, 0, Guid.CreateVersion7(), CancellationToken.None));
        Assert.True((await h.Catalogue.ValidateMigrationSnapshotAsync("field-protection-verifier", first.SearchCatalogueSnapshotId, Guid.CreateVersion7(), CancellationToken.None)).Valid);
    }

    [Fact]
    public async Task TheMigrationCatalogueNeedsAnEffectiveCurrentCommandOfThatFormAndDenialsAreJournaled()
    {
        var h = new Harness();
        await h.RecordAsync(h.FormA);
        var commandForB = h.ProtectionCommand(h.FormB);
        var superseded = h.ProtectionCommand(h.FormA, current: false);
        foreach (var command in new[] { commandForB, superseded, Guid.CreateVersion7() })
        {
            var denied = await Assert.ThrowsAsync<AuthorityException>(() => h.Catalogue.IssueMigrationCatalogueAsync("field-protection-verifier", new MigrationCatalogueRequest(h.Org, h.FormA, command), Guid.CreateVersion7(), CancellationToken.None));
            Assert.Equal(AuthorityErrorCodes.Forbidden, denied.ErrorCode);
        }

        Assert.Equal(3, h.Store.Journal.Count(j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == "MigrationCatalogueDenied"));
        Assert.Empty(h.Store.Snapshots);
    }

    [Fact]
    public async Task AMigrationSnapshotIsInvalidOnceItsCommandIsNoLongerTheCurrentDecision()
    {
        var h = new Harness();
        await h.RecordAsync(h.FormA);
        var command = h.ProtectionCommand(h.FormA);
        var page = await h.Catalogue.IssueMigrationCatalogueAsync("field-protection-verifier", new MigrationCatalogueRequest(h.Org, h.FormA, command), Guid.CreateVersion7(), CancellationToken.None);
        h.Store.Resources.Single(r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement).RegisteredBySecurityCommandId = Guid.CreateVersion7();
        var validation = await h.Catalogue.ValidateMigrationSnapshotAsync("field-protection-verifier", page.SearchCatalogueSnapshotId, Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(CatalogueInvalidationReasons.CommandSuperseded, validation.InvalidationReason);
    }

    // ---- batch verification ----

    [Fact]
    public async Task BatchVerificationVerifiesEachRecordIndependentlyAndQuarantinesOnlyTheTamperedOne()
    {
        var h = new Harness();
        var good = await h.RecordAsync(h.FormA, value: "good");
        var tampered = await h.RecordAsync(h.FormA, value: "tampered");
        var missing = await h.RecordAsync(h.FormA, value: "missing");
        var forged = h.Accepted[tampered].Select(i => i.ItemKind == ManifestItemKind.FieldValue
            ? i with { CanonicalBytesBase64 = Encode(Text(h.Org, tampered, h.Field, i.ItemId, "forged"), h.Org, h.AcceptedOperation[tampered], 1).Base64 }
            : i).ToList();

        var response = await h.BatchAsync(h.Owner, h.BatchItem(good), h.BatchItem(tampered, forged), h.BatchItem(missing, h.Accepted[missing].Take(1).ToList()));
        var results = response.Results.ToDictionary(r => r.FormRecordId);
        Assert.True(results[good].Verified);
        Assert.True(results[good].IsCompleteProjection);
        Assert.Equal(h.Accepted[good].Count, results[good].ReadableItems.Count);
        Assert.False(results[tampered].Verified);
        Assert.Equal("ITEM_DIGEST_MISMATCH", results[tampered].FailureReasonCode);
        Assert.Empty(results[tampered].ReadableItems);
        Assert.Equal("MISSING_ITEM", results[missing].FailureReasonCode);
        Assert.True(h.Head(tampered).ContentHeadQuarantined);
        Assert.True(h.Head(missing).ContentHeadQuarantined);
        Assert.False(h.Head(good).ContentHeadQuarantined);
    }

    [Fact]
    public async Task AChangedHeadAnUndiscoverableRecordAndAQuarantinedHeadAreTypedResultsNotSilentGaps()
    {
        var h = new Harness();
        var stale = await h.RecordAsync(h.FormA);
        var expectedOperation = h.AcceptedOperation[stale];
        var oldItems = h.Accepted[stale];
        await h.WriteAsync(stale, "newer");
        var hidden = await h.RecordAsync(h.FormA, discoverableByOwner: false);
        var quarantined = await h.RecordAsync(h.FormA);
        h.Head(quarantined).ContentHeadQuarantined = true;

        var results = (await h.BatchAsync(h.Owner, h.BatchItem(stale, oldItems, expectedOperation), h.BatchItem(hidden), h.BatchItem(quarantined))).Results.ToDictionary(r => r.FormRecordId);
        Assert.Equal(ContentService.HeadChanged, results[stale].FailureReasonCode);
        Assert.False(h.Head(stale).ContentHeadQuarantined); // a changed head is not tampering
        Assert.Equal(ContentService.RecordUnavailable, results[hidden].FailureReasonCode);
        Assert.Equal("QUARANTINED", results[quarantined].FailureReasonCode);
    }

    [Fact]
    public async Task APartialReaderGetsOnlyTheReadableItemsOfAVerifiedRecord()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        h.Allow(ResourceTypeNames.FormRecord, record, "discover", h.Other);
        var result = Assert.Single((await h.BatchAsync(h.Other, h.BatchItem(record))).Results);
        Assert.True(result.Verified);
        Assert.False(result.IsCompleteProjection);
        Assert.Empty(result.ReadableItems); // nothing shared yet
        h.Allow(ResourceTypeNames.FieldItem, h.ItemFor(record), "read", h.Other);
        var shared = Assert.Single((await h.BatchAsync(h.Other, h.BatchItem(record))).Results);
        Assert.Equal([h.ItemFor(record)], shared.ReadableItems.Select(i => i.ItemId));
    }

    [Fact]
    public async Task BatchesAreBoundedAndRecordsAppearOnce()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => h.BatchAsync(h.Owner))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => h.BatchAsync(h.Owner, h.BatchItem(record), h.BatchItem(record)))).ErrorCode);
        var tooMany = Enumerable.Range(0, ContentService.MaximumBatchRecords + 1).Select(_ => new BatchVerificationItem(Guid.CreateVersion7(), Guid.CreateVersion7(), [])).ToArray();
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => h.BatchAsync(h.Owner, tooMany))).ErrorCode);
    }

    [Fact]
    public async Task ABatchWhileAuthorizationMovedFailsButKeepsTamperEvidence()
    {
        var h = new Harness();
        var tampered = await h.RecordAsync(h.FormA);
        h.Spice.OnCheck = h.AdvanceAuthorization; // a revocation lands while the batch runs
        var moved = await Assert.ThrowsAsync<AuthorityException>(() => h.BatchAsync(h.Owner, h.BatchItem(tampered, h.Accepted[tampered].Take(1).ToList())));
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, moved.ErrorCode);
        Assert.True(h.Head(tampered).ContentHeadQuarantined); // the tampering is still recorded
    }
}
