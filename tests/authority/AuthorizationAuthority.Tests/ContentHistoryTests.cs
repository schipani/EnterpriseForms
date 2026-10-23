using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
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
/// Phase C6 M2: AC-1 content history read and historic verification with the real <see cref="ContentService"/>, the real
/// canonical encoding and the in-process HMAC provider over an in-memory store. Covers the trusted version list (exactly the
/// accepted revisions, ascending, actor and attribution from the Authority's own rows, paging, read-only), who may read it
/// (full readers now only; concealment-safe 404 for everyone else, other records and other organizations; quarantine; gate;
/// authorization moving during the read) and verifying an earlier revision (tampered, missing and extra rows fail closed and
/// quarantine the head as today, AC-4; aborted or foreign operations are not versions; a non-reader gets 404).
/// </summary>
public sealed class ContentHistoryTests
{
    private const string Issuer = "https://issuer.test";

    /// <summary>SpiceDB stand-in: only CheckAsync is used; answers from the explicit set of (permission, user) pairs.</summary>
    public class SpiceDb : DispatchProxy
    {
        public HashSet<(string Permission, string User)> Allowed { get; } = [];

        public Action? DuringCheck { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISpiceDbProjection.CheckAsync))
            {
                throw new NotSupportedException("unexpected call " + targetMethod?.Name);
            }

            var check = (SpiceDbCheck)args![0]!;
            DuringCheck?.Invoke();
            return Task.FromResult(Allowed.Contains((check.Permission, check.SubjectId)) ? SpiceDbCheckOutcome.Allowed : SpiceDbCheckOutcome.Denied);
        }
    }

    private sealed class Harness
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid Record { get; } = Guid.CreateVersion7();
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Reader { get; } = Guid.CreateVersion7();
        public Guid Field { get; } = Guid.CreateVersion7();
        public Guid Item { get; } = Guid.CreateVersion7();
        public InMemoryAuthorityStore Store { get; } = new();
        public ContentService Service { get; }
        public SpiceDb SpiceDbControl { get; }
        public OrganizationAuthorizationState State { get; }

        /// <summary>Accepted rows (with signatures) of every revision, as the business store keeps them.</summary>
        public Dictionary<long, List<StagedContentItem>> Snapshots { get; } = [];

        public Harness()
        {
            var mac = new LocalHmacProvider();
            mac.ProvisionKeyAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, "org-key", CancellationToken.None).GetAwaiter().GetResult();
            var spiceDb = DispatchProxy.Create<ISpiceDbProjection, SpiceDb>();
            SpiceDbControl = (SpiceDb)(object)spiceDb;
            SpiceDbControl.Allowed.Add(("read", Owner.ToString("D")));
            SpiceDbControl.Allowed.Add(("edit", Owner.ToString("D")));
            SpiceDbControl.Allowed.Add(("discover", Owner.ToString("D")));
            SpiceDbControl.Allowed.Add(("discover", Reader.ToString("D")));
            Service = new ContentService(Store, mac, spiceDb, new NoOpFaultInjector(), TimeProvider.System, new ShareContributorWriteSwitch(false, testingOverrideAllowed: true));
            State = new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous };
            Store.Organizations.Add(State);
            foreach (var user in new[] { Owner, Reader })
            {
                Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = user.ToString("D"), IdentityBindingEnabled = true });
            }

            Store.Keys.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = Org, ProviderName = "LocalDevelopment", ProviderKeyName = "org-key", CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 });
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, ParentResourceEntityId = Ids.Form });
            Store.Heads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = Org, FormRecordId = Record, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1 });
            Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = Owner, SubjectRelationName = string.Empty });
        }

        public RecordContentHead Head => Store.Heads.Single(h => h.FormRecordId == Record);

        public static VerifiedActor Actor(Guid user) => new(Issuer, user.ToString("D"), "test");

        public List<Row> Rows(string value) => [Header(Org, Record), Text(Org, Record, Field, Item, value)];

        public async Task<Guid> WriteAsync(string value, bool commit = true)
        {
            var operation = Guid.CreateVersion7();
            var candidate = Rows(value).Select(r => Encode(r, Org, operation, Head.CommittedContentRevision + 1)).ToList();
            var request = new PrepareContentChangeRequest(Org, Record, operation, State.AuthorizationRevision, Head.CommittedContentRevision, Convert.ToBase64String(Head.CommittedManifestRoot),
                candidate.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList());
            var prepared = await Service.PrepareAsync(Actor(Owner), request, "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
            if (!commit)
            {
                return operation;
            }

            var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            var staged = candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
            var revision = await Service.CommitAsync(Actor(Owner), operation, new CommitContentChangeRequest(Org, Record, staged), Guid.CreateVersion7(), CancellationToken.None);
            Snapshots[revision.CommittedContentRevision] = staged;
            foreach (var entry in Store.Journal)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // archive receipt present (SEC-P12 precondition for any release)
            }

            return operation;
        }

        public async Task<Harness> WithRevisionsAsync(int count)
        {
            for (var i = 1; i <= count; i++)
            {
                await WriteAsync("value " + i);
            }

            return this;
        }

        public Task<ContentHistoryPage> HistoryAsync(Guid user, long? after = null, int? limit = null, Guid? record = null, Guid? org = null) =>
            Service.GetHistoryAsync(Actor(user), org ?? Org, record ?? Record, after, limit, Guid.CreateVersion7(), CancellationToken.None);

        public Guid OperationOf(long revision) => Store.Commits.Single(c => c.FormRecordId == Record && c.CommittedContentRevision == revision).CommittedOperationId;

        public Task<VerifyRecordProjectionResult> VerifyHistoricAsync(Guid user, long revision, List<StagedContentItem>? observed = null, Guid? operation = null) =>
            Service.VerifyProjectionAsync(Actor(user), new VerifyRecordProjectionRequest(Org, Record, RequestedAction.Read, observed ?? Snapshots[revision], operation ?? OperationOf(revision)), Guid.CreateVersion7(), CancellationToken.None);
    }

    // ---- AC-1 listing ----

    [Fact]
    public async Task TheListHasExactlyTheAcceptedRevisionsInOrderWithAuthorityActorAndCurrentMarker()
    {
        var h = await new Harness().WithRevisionsAsync(3);
        await h.WriteAsync("prepared, never committed", commit: false); // an abandoned candidate is not a version

        var page = await h.HistoryAsync(h.Owner);

        Assert.Equal([1L, 2L, 3L], page.Entries.Select(e => e.ContentRevision));
        Assert.Equal(3, page.CurrentContentRevision);
        Assert.Null(page.NextAfterRevision);
        Assert.Equal([false, false, true], page.Entries.Select(e => e.IsCurrent));
        Assert.All(page.Entries, e =>
        {
            Assert.Equal(ContentHistoryActorKinds.User, e.ActorKind);
            Assert.Equal(h.Owner, e.ActorAuthorityUserId);
            Assert.True(e.VerifiableNow);
            Assert.Equal(2, e.ManifestLeafCount);
        });
        Assert.Equal(h.Store.Commits.OrderBy(c => c.CommittedContentRevision).Select(c => (c.CommittedOperationId, c.ManifestCommittedOn)), page.Entries.Select(e => (e.CommittedOperationId, e.AcceptedOn)));
    }

    [Fact]
    public async Task ListingPagesByRevisionBoundsThePageSizeAndRefusesANegativeCursor()
    {
        var h = await new Harness().WithRevisionsAsync(5);

        var first = await h.HistoryAsync(h.Owner, limit: 2);
        Assert.Equal([1L, 2L], first.Entries.Select(e => e.ContentRevision));
        Assert.Equal(2, first.NextAfterRevision);
        var second = await h.HistoryAsync(h.Owner, after: first.NextAfterRevision, limit: 2);
        Assert.Equal([3L, 4L], second.Entries.Select(e => e.ContentRevision));
        var last = await h.HistoryAsync(h.Owner, after: second.NextAfterRevision, limit: 2);
        Assert.Equal([5L], last.Entries.Select(e => e.ContentRevision));
        Assert.Null(last.NextAfterRevision);

        Assert.Equal(5, (await h.HistoryAsync(h.Owner, limit: 10_000)).Entries.Count); // clamped to the maximum, never unbounded
        var invalid = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner, after: -1));
        Assert.Equal(422, invalid.HttpStatusCode);
    }

    [Fact]
    public async Task ListingIsReadOnly()
    {
        var h = await new Harness().WithRevisionsAsync(2);
        var journal = h.Store.Journal.Count;
        var operations = h.Store.Operations.Count;
        var head = (h.Head.CommittedContentRevision, h.Head.ContentHeadQuarantined, h.State.ContentIntegrityRevision, h.State.AuthorizationRevision);

        await h.HistoryAsync(h.Owner);

        Assert.Equal(journal, h.Store.Journal.Count);
        Assert.Equal(operations, h.Store.Operations.Count);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.ContentHeadQuarantined, h.State.ContentIntegrityRevision, h.State.AuthorizationRevision));
    }

    [Fact]
    public async Task AReprotectionRevisionIsAttributedToTheProtectionMigrationAndOlderKeysAreMarkedUnverifiable()
    {
        var h = await new Harness().WithRevisionsAsync(3);
        var migration = h.Store.Operations.Single(o => o.ContentChangeOperationId == h.OperationOf(2));
        migration.ReprotectionCommandId = Guid.CreateVersion7();
        h.Store.Commits.Single(c => c.CommittedContentRevision == 1).RowSignatureKeyVersion = 1;
        h.Store.Keys.Single().MinimumVerificationKeyVersion = 2;
        foreach (var commit in h.Store.Commits.Where(c => c.CommittedContentRevision > 1))
        {
            commit.RowSignatureKeyVersion = 2;
        }

        var page = await h.HistoryAsync(h.Owner);

        var second = page.Entries.Single(e => e.ContentRevision == 2);
        Assert.Equal(ContentHistoryActorKinds.ProtectionMigration, second.ActorKind);
        Assert.Null(second.ActorAuthorityUserId);
        Assert.Equal(migration.ReprotectionCommandId, second.ReprotectionCommandId);
        Assert.False(page.Entries.Single(e => e.ContentRevision == 1).VerifiableNow);
        Assert.True(page.Entries.Single(e => e.ContentRevision == 3).VerifiableNow);
    }

    [Fact]
    public async Task AnAcceptedCommitWithoutAConsistentOperationIsReportedUnknownNotAttributedToAnyone()
    {
        var h = await new Harness().WithRevisionsAsync(2);
        h.Store.Operations.RemoveAll(o => o.ContentChangeOperationId == h.OperationOf(1));

        var first = (await h.HistoryAsync(h.Owner)).Entries.Single(e => e.ContentRevision == 1);
        Assert.Equal(ContentHistoryActorKinds.Unknown, first.ActorKind);
        Assert.Null(first.ActorAuthorityUserId);
    }

    // ---- who may list ----

    [Fact]
    public async Task OnlyAFullReaderNowMayListAndEveryoneElseGetsTheSameNotFoundAsForAMissingRecord()
    {
        var h = await new Harness().WithRevisionsAsync(2);

        // A user who can discover the record (e.g. a field-share reader) but not read all of it: no history.
        var shareReader = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Reader));
        var missing = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner, record: Guid.CreateVersion7()));
        Assert.Equal((AuthorityErrorCodes.NotFound, 404), (shareReader.ErrorCode, shareReader.HttpStatusCode));
        Assert.Equal((missing.ErrorCode, missing.HttpStatusCode), (shareReader.ErrorCode, shareReader.HttpStatusCode));

        // Revoked: the owner loses read (ownership transferred); the earlier revisions are not listed any more.
        h.SpiceDbControl.Allowed.Remove(("read", h.Owner.ToString("D")));
        var revoked = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner));
        Assert.Equal(404, revoked.HttpStatusCode);
    }

    [Fact]
    public async Task AnotherOrganizationsRecordIsNotFoundAndAnUnboundActorIsRefused()
    {
        var h = await new Harness().WithRevisionsAsync(1);
        var other = new OrganizationAuthorizationState { OrganizationId = Guid.CreateVersion7(), AuthorizationRevision = 1, ProjectionAppliedRevision = 1, ProjectionZedToken = "zed-1", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous };
        h.Store.Organizations.Add(other);
        h.Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = other.OrganizationId, AuthorityUserId = h.Owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = h.Owner.ToString("D"), IdentityBindingEnabled = true });

        // The same user asks through the other organization for this organization's record: it does not exist there.
        var crossTenant = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner, org: other.OrganizationId));
        Assert.Equal(404, crossTenant.HttpStatusCode);

        var stranger = Guid.CreateVersion7();
        var unbound = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(stranger));
        Assert.Equal(403, unbound.HttpStatusCode);
    }

    [Fact]
    public async Task AQuarantinedRecordAClosedGateOrAuthorizationMovingDuringTheReadFailClosed()
    {
        var h = await new Harness().WithRevisionsAsync(2);

        h.Head.ContentHeadQuarantined = true;
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner))).HttpStatusCode);
        h.Head.ContentHeadQuarantined = false;

        h.State.AuthorizationRevision = 8; // projection not caught up
        Assert.Equal(503, (await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner))).HttpStatusCode);
        h.State.ProjectionAppliedRevision = 8;
        h.State.ProjectionZedToken = "zed-8";

        // A revocation commits while the list is read. The in-memory store hands out one shared instance, so the committed state
        // is a new row object, as a fresh PostgreSQL read would return.
        h.SpiceDbControl.DuringCheck = () =>
        {
            h.Store.Organizations.Remove(h.State);
            h.Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = h.Org, AuthorizationRevision = 9, ProjectionAppliedRevision = 9, ProjectionZedToken = "zed-9", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous });
        };
        var moved = await Assert.ThrowsAsync<AuthorityException>(() => h.HistoryAsync(h.Owner));
        Assert.Equal((AuthorityErrorCodes.AuthorityNotReady, 503), (moved.ErrorCode, moved.HttpStatusCode));
    }

    // ---- verifying an earlier revision (existing protocol, exercised for M2) ----

    [Fact]
    public async Task AnEarlierRevisionVerifiesAgainstItsOwnAcceptedManifest()
    {
        var h = await new Harness().WithRevisionsAsync(3);
        var result = await h.VerifyHistoricAsync(h.Owner, 2);

        Assert.True(result.Verified);
        Assert.Equal(2, result.CommittedContentRevision);
        Assert.Equal(h.OperationOf(2), result.CommittedOperationId);
        Assert.True(result.IsCompleteProjection);
        Assert.False(h.Head.ContentHeadQuarantined);
    }

    [Fact]
    public async Task TamperedMissingOrForeignHistoricRowsFailClosedAndQuarantineTheHeadAsToday()
    {
        foreach (var scenario in new[] { "tampered", "missing", "extra", "swapped" })
        {
            var h = await new Harness().WithRevisionsAsync(3);
            var observed = h.Snapshots[2].ToList();
            var value = observed.Single(o => o.ItemKind == ManifestItemKind.FieldValue);
            switch (scenario)
            {
                case "tampered":
                    observed[observed.IndexOf(value)] = value with { CanonicalBytesBase64 = h.Snapshots[3].Single(o => o.ItemKind == ManifestItemKind.FieldValue).CanonicalBytesBase64 };
                    break;
                case "missing":
                    observed.Remove(value);
                    break;
                case "extra":
                    observed.Add(value with { ItemId = Guid.CreateVersion7() });
                    break;
                default:
                    observed = h.Snapshots[3].ToList(); // the rows of another accepted revision of the same record
                    break;
            }

            var journal = h.Store.Journal.Count;
            var result = await h.VerifyHistoricAsync(h.Owner, 2, observed);

            Assert.False(result.Verified, scenario);
            Assert.Empty(result.AuthorizedItems);
            Assert.True(h.Head.ContentHeadQuarantined, scenario); // AC-4: current behaviour kept (quarantine the head)
            Assert.Equal(journal + 1, h.Store.Journal.Count); // one minimal incident journaled
        }
    }

    [Fact]
    public async Task AnAbandonedOperationOrAnotherRecordsOperationIsNotAVersionAndQuarantinesNothing()
    {
        var h = await new Harness().WithRevisionsAsync(2);
        var abandoned = await h.WriteAsync("never committed", commit: false);
        var other = await new Harness().WithRevisionsAsync(2);

        foreach (var operation in new[] { abandoned, other.OperationOf(1), Guid.CreateVersion7() })
        {
            var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.VerifyHistoricAsync(h.Owner, 1, operation: operation));
            Assert.Equal(404, refused.HttpStatusCode);
        }

        Assert.False(h.Head.ContentHeadQuarantined);
    }

    [Fact]
    public async Task AReaderWhoCannotReadTheWholeRecordNowCannotVerifyAnEarlierRevision()
    {
        var h = await new Harness().WithRevisionsAsync(2);
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.VerifyHistoricAsync(h.Reader, 1));
        Assert.Equal(404, refused.HttpStatusCode);
        Assert.False(h.Head.ContentHeadQuarantined);
    }
}
