using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Phase C5 end-to-end content protocol with the real <see cref="ContentService"/>, the real canonical encoding and the
/// in-process HMAC provider over an in-memory store: owner compatibility, share-editor writes, the default-off switch,
/// revocation and switch changes between prepare and commit (commit-time authorization), concurrent writes, replay,
/// tampered current snapshots and staged candidates. Lock ordering against PostgreSQL is covered by the Docker tests.
/// </summary>
public sealed class ContributorWriteProtocolTests
{
    private const string Issuer = "https://issuer.test";

    private sealed class Harness
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid Record { get; } = Guid.CreateVersion7();
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Contributor { get; } = Guid.CreateVersion7();
        public Guid Team { get; } = Guid.CreateVersion7();
        public Guid SharedField { get; } = Guid.CreateVersion7();
        public Guid OtherField { get; } = Guid.CreateVersion7();
        public Guid SealedField { get; } = Guid.CreateVersion7();
        public Guid Shared { get; } = Guid.CreateVersion7();
        public Guid Other { get; } = Guid.CreateVersion7();
        public Guid SealedItem { get; } = Guid.CreateVersion7();
        public Guid Document { get; } = Guid.CreateVersion7();
        public Guid ShareId { get; } = Guid.CreateVersion7();
        public InMemoryAuthorityStore Store { get; } = new();
        public ShareContributorWriteSwitch Switch { get; }
        public ContentService Service { get; }
        public OrganizationAuthorizationState State { get; }

        /// <summary>The accepted rows of the head with their signatures (what the business store would hold).</summary>
        public List<(Staged Row, byte[] Signature)> Accepted { get; private set; } = [];

        public Harness(bool contributorWritesEnabled = true)
        {
            var mac = new LocalHmacProvider();
            mac.ProvisionKeyAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, "org-key", CancellationToken.None).GetAwaiter().GetResult();
            Switch = new ShareContributorWriteSwitch(contributorWritesEnabled, testingOverrideAllowed: true);
            Service = new ContentService(Store, mac, ThrowingProxy.Create<ISpiceDbProjection>(), new NoOpFaultInjector(), TimeProvider.System, Switch);
            State = new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous };
            Store.Organizations.Add(State);
            foreach (var user in new[] { Owner, Contributor })
            {
                Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = user.ToString("D"), IdentityBindingEnabled = true });
            }

            Store.Keys.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = Org, ProviderName = "LocalDevelopment", ProviderKeyName = "org-key", CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 });
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, ParentResourceEntityId = Ids.Form });
            Store.Heads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = Org, FormRecordId = Record, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1 });
            Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, Owner);
        }

        public RecordContentHead Head => Store.Heads.Single();

        public static VerifiedActor Actor(Guid user) => new(Issuer, user.ToString("D"), "test");

        public AuthorityRelationshipEdge Edge(string resourceType, Guid resource, string relation, string subjectType, Guid subject, string subjectRelation = "")
        {
            var edge = new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = resourceType, ResourceEntityId = resource, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation };
            Store.Edges.Add(edge);
            return edge;
        }

        /// <summary>Mirrors CommandPlanner.CreateShare edges; returns the role edge (revoking it is the effective revocation).</summary>
        public AuthorityRelationshipEdge ShareWithContributor(string role = "contributor", bool viaTeam = false, params Guid[] items)
        {
            Edge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, ShareId);
            var roleEdge = viaTeam
                ? Edge(ResourceTypeNames.RecordShare, ShareId, role, ResourceTypeNames.Team, Team, "participant")
                : Edge(ResourceTypeNames.RecordShare, ShareId, role, ResourceTypeNames.User, Contributor);
            foreach (var item in items.Length == 0 ? [Shared] : items)
            {
                Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, ShareId);
            }

            return roleEdge;
        }

        public void Remove(AuthorityRelationshipEdge edge)
        {
            edge.RemovedBySecurityCommandId = Guid.CreateVersion7();
            State.AuthorizationRevision++;
            State.ProjectionAppliedRevision++;
        }

        public List<Row> Rows(string sharedValue = "before") =>
        [
            Header(Org, Record),
            Text(Org, Record, SharedField, Shared, sharedValue),
            Text(Org, Record, OtherField, Other, "not shared"),
            Sealed(Org, Record, SealedField, SealedItem, 10),
            Attachment(Org, Record, Document),
        ];

        public PrepareContentChangeRequest Request(Guid operation, IEnumerable<Staged> candidate, bool withCurrent) => new(
            Org, Record, operation, State.AuthorizationRevision, Head.CommittedContentRevision, Convert.ToBase64String(Head.CommittedManifestRoot),
            candidate.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(),
            CurrentItems: withCurrent ? Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList() : null);

        public List<Staged> Stage(IEnumerable<Row> rows, Guid operation) => rows.Select(r => Encode(r, Org, operation, Head.CommittedContentRevision + 1)).ToList();

        public async Task<PreparedContentOperation> PrepareAsync(Guid actor, Guid operation, List<Staged> candidate, bool withCurrent, string? key = null) =>
            await Service.PrepareAsync(Actor(actor), Request(operation, candidate, withCurrent), key ?? "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);

        public async Task<ContentRevision> CommitAsync(Guid actor, PreparedContentOperation prepared, List<Staged> candidate)
        {
            var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => Convert.FromBase64String(s.RowSignatureBase64));
            var staged = candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, Convert.ToBase64String(signatures[(c.Kind, c.ItemId)]))).ToList();
            var revision = await Service.CommitAsync(Actor(actor), prepared.OperationId, new CommitContentChangeRequest(Org, Record, staged), Guid.CreateVersion7(), CancellationToken.None);
            Accepted = candidate.Select(c => (c, signatures[(c.Kind, c.ItemId)])).ToList();
            return revision;
        }

        public async Task<ContentRevision> WriteAsync(Guid actor, IEnumerable<Row> rows, bool withCurrent)
        {
            var operation = Guid.CreateVersion7();
            var candidate = Stage(rows, operation);
            var prepared = await PrepareAsync(actor, operation, candidate, withCurrent);
            return await CommitAsync(actor, prepared, candidate);
        }

        public async Task<Harness> CreatedAsync()
        {
            await WriteAsync(Owner, Rows(), withCurrent: false);
            return this;
        }
    }

    private static List<Row> Change(List<Row> rows, Guid item, Func<Row, Row> change) => rows.Select(r => r.ItemId == item ? change(r) : r).ToList();

    [Fact]
    public async Task OwnerWritesAreUnchangedAndPassIdentityBinding()
    {
        var h = await new Harness().CreatedAsync();
        var revision = await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("owner edit"))), withCurrent: false);
        Assert.Equal(2, revision.CommittedContentRevision);

        // An owner candidate whose value row names another record is refused by identity binding (D8 applies to owners too).
        var forged = Change(h.Rows(), h.Other, r => r.With("form_record_id", RowValue.Uuid(Guid.CreateVersion7())));
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(forged, operation), withCurrent: false));
        Assert.Contains("IDENTITY_BINDING", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContributorChangesOnlyTheSharedValueAndEveryOtherItemIsPreserved()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var before = h.Accepted.ToDictionary(a => a.Row.ItemId, a => a.Row);
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), operation);
        var prepared = await h.PrepareAsync(h.Contributor, operation, candidate, withCurrent: true);

        Assert.Single(prepared.SignedItems, s => s.MutationKind == ContentMutationKinds.Update && s.ItemId == h.Shared);
        Assert.All(prepared.SignedItems.Where(s => s.ItemId != h.Shared), s => Assert.Equal(ContentMutationKinds.Unchanged, s.MutationKind));
        var revision = await h.CommitAsync(h.Contributor, prepared, candidate);
        Assert.Equal(2, revision.CommittedContentRevision);
        Assert.Equal(operation, h.Head.CommittedOperationId);

        // Ciphertext and document artifact preserved byte-for-byte in the accepted state (only B/S differ).
        var sealedBefore = RowCanonicalDecoder.Decode(before[h.SealedItem].Bytes);
        var sealedAfter = RowCanonicalDecoder.Decode(h.Accepted.Single(a => a.Row.ItemId == h.SealedItem).Row.Bytes);
        foreach (var column in new[] { "encrypted_value_bytes", "encryption_nonce_bytes", "wrapped_data_key_bytes", "encryption_auth_tag_bytes" })
        {
            Assert.True(sealedBefore.Column(column)!.FramedValueEquals(sealedAfter.Column(column)!));
        }

        var documentAfter = RowCanonicalDecoder.Decode(h.Accepted.Single(a => a.Row.ItemId == h.Document).Row.Bytes);
        Assert.Equal("objects/one", documentAfter.Column("storage_object_key")!.AsText());
        Assert.Equal(operation, documentAfter.Column("content_change_operation_id")!.AsUuid());
    }

    [Fact]
    public async Task ContributorWritesAreRefusedWhenTheSwitchIsOffAndNothingIsSigned()
    {
        var h = await new Harness(contributorWritesEnabled: false).CreatedAsync();
        h.ShareWithContributor();
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, operation, h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), operation), withCurrent: true));
        Assert.Equal(AuthorityErrorCodes.ContributorWritesDisabled, ex.ErrorCode);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation);

        // Owner writes are unaffected by the switch.
        Assert.Equal(2, (await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("owner"))), withCurrent: false)).CommittedContentRevision);
    }

    [Fact]
    public async Task TurningTheSwitchOffBetweenPrepareAndCommitBlocksTheCommit()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), operation);
        var prepared = await h.PrepareAsync(h.Contributor, operation, candidate, withCurrent: true);
        h.Switch.SetTestingOverride(false);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared, candidate));
        Assert.Equal(AuthorityErrorCodes.ContributorWritesDisabled, ex.ErrorCode);
        Assert.Equal(1, h.Head.CommittedContentRevision);
        Assert.Equal(ContentOperationStates.Aborted, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationStateName);
    }

    [Fact]
    public async Task ARevocationBetweenPrepareAndCommitAbortsTheCommit()
    {
        var h = await new Harness().CreatedAsync();
        var role = h.ShareWithContributor();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), operation);
        var prepared = await h.PrepareAsync(h.Contributor, operation, candidate, withCurrent: true);
        h.Remove(role);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared, candidate));
        Assert.Equal(AuthorityErrorCodes.Forbidden, ex.ErrorCode);
        var aborted = h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);
        Assert.Equal(ContentOperationStates.Aborted, aborted.OperationStateName);
        Assert.Equal("PERMISSION_REVOKED_BEFORE_COMMIT", aborted.OperationQuarantineReasonCode);
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task LeavingTheRecipientTeamBetweenPrepareAndCommitAbortsTheCommit()
    {
        var h = await new Harness().CreatedAsync();
        var membership = h.Edge(ResourceTypeNames.Team, h.Team, "member", ResourceTypeNames.User, h.Contributor);
        h.ShareWithContributor(viaTeam: true);
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), operation);
        var prepared = await h.PrepareAsync(h.Contributor, operation, candidate, withCurrent: true);
        h.Remove(membership);

        await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared, candidate));
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task ReadersAndUnsharedItemsCannotBeWritten()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor(role: "reader");
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, operation, h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), operation), withCurrent: true));
        Assert.Equal(AuthorityErrorCodes.Forbidden, ex.ErrorCode);

        var h2 = await new Harness().CreatedAsync();
        h2.ShareWithContributor();
        var op2 = Guid.CreateVersion7();
        var hidden = await Assert.ThrowsAsync<AuthorityException>(() => h2.PrepareAsync(h2.Contributor, op2, h2.Stage(Change(h2.Rows(), h2.Other, r => r.With("string_value", RowValue.Text("hidden edit"))), op2), withCurrent: true));
        Assert.Contains("SHARE_ITEM_NOT_IN_SCOPE", hidden.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h2.Store.Operations, o => o.ContentChangeOperationId == op2);
    }

    [Fact]
    public async Task HeaderAndDocumentChangesByAContributorAreRefusedEndToEnd()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var op = Guid.CreateVersion7();
        var header = Change(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), h.Record, r => r.With("record_status", RowValue.Text("Submitted")));
        Assert.Contains("SHARE_ITEM_NOT_UPDATABLE", (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, op, h.Stage(header, op), withCurrent: true))).Message, StringComparison.Ordinal);
        var op2 = Guid.CreateVersion7();
        var document = Change(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), h.Document, r => r.With("storage_object_key", RowValue.Text("objects/swapped")));
        Assert.Contains("SHARE_ITEM_NOT_UPDATABLE", (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, op2, h.Stage(document, op2), withCurrent: true))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentWritesOnTheSameHeadAcceptExactlyOne()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var opA = Guid.CreateVersion7();
        var candidateA = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("contributor"))), opA);
        var preparedA = await h.PrepareAsync(h.Contributor, opA, candidateA, withCurrent: true);
        var opB = Guid.CreateVersion7();
        var candidateB = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("owner"))), opB);
        var preparedB = await h.PrepareAsync(h.Owner, opB, candidateB, withCurrent: false);

        await h.CommitAsync(h.Owner, preparedB, candidateB);
        var lost = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, preparedA, candidateA));
        Assert.Equal(AuthorityErrorCodes.ContentRevisionConflict, lost.ErrorCode);
        Assert.Equal(opB, h.Head.CommittedOperationId);
        Assert.Equal(2, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task AStaleAuthorizationRevisionOrMissingSnapshotIsRefusedAtPrepare()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var op = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), op);
        var stale = h.Request(op, candidate, withCurrent: true) with { ExpectedAuthorizationRevision = h.State.AuthorizationRevision - 1 };
        Assert.Equal(AuthorityErrorCodes.StaleAuthorizationRevision, (await Assert.ThrowsAsync<AuthorityException>(() => h.Service.PrepareAsync(Harness.Actor(h.Contributor), stale, "stale-key-0001", Guid.CreateVersion7(), CancellationToken.None))).ErrorCode);

        var missing = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, op, candidate, withCurrent: false));
        Assert.Contains("CURRENT_SNAPSHOT_REQUIRED", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATamperedCurrentSnapshotIsRefusedJournaledAndNothingIsSigned()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var tampered = h.Accepted.Select(a => a.Row.ItemId == h.Other ? (Encode(Text(h.Org, h.Record, h.OtherField, h.Other, "forged in the business store"), h.Org, h.Head.CommittedOperationId!.Value, 1), a.Signature) : a).ToList();
        var journalBefore = h.Store.Journal.Count;
        var op = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), op);
        var request = h.Request(op, candidate, withCurrent: false) with { CurrentItems = tampered.Select(a => new StagedContentItem(a.Item1.Kind, a.Item1.ItemId, a.Item1.Base64, Convert.ToBase64String(a.Signature))).ToList() };

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.PrepareAsync(Harness.Actor(h.Contributor), request, "tamper-key-0001", Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.CurrentSnapshotMismatch, ex.ErrorCode);
        Assert.Equal("CURRENT_SNAPSHOT_DIGEST", ex.Message);
        Assert.Equal(journalBefore + 1, h.Store.Journal.Count);
        Assert.Equal("CurrentSnapshotMismatch", h.Store.Journal[^1].JournalEventKind);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == op);
    }

    [Fact]
    public async Task ReplayIsIdempotentForTheSamePayloadAndRefusedForAnother()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var op = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), op);
        var first = await h.PrepareAsync(h.Contributor, op, candidate, withCurrent: true, key: "replay-key-0001");
        var again = await h.PrepareAsync(h.Contributor, op, candidate, withCurrent: true, key: "replay-key-0001");
        Assert.Equal(first.OperationId, again.OperationId);
        Assert.Equal(first.CandidateManifestRootBase64, again.CandidateManifestRootBase64);

        var other = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("different"))), op);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, op, other, withCurrent: true, key: "replay-key-0001"))).ErrorCode);

        await h.CommitAsync(h.Contributor, first, candidate);
        var replayedCommit = await h.CommitAsync(h.Contributor, first, candidate); // idempotent: no second revision
        Assert.Equal(2, replayedCommit.CommittedContentRevision);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, op, h.Stage(Change(h.Rows("after"), h.Shared, r => r.With("string_value", RowValue.Text("again"))), op), withCurrent: true))).ErrorCode);
    }

    [Fact]
    public async Task AStagedCandidateAlteredBeforeCommitIsRefused()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var op = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Shared, r => r.With("string_value", RowValue.Text("after"))), op);
        var prepared = await h.PrepareAsync(h.Contributor, op, candidate, withCurrent: true);
        var altered = candidate.Select(c => c.ItemId == h.Other ? Encode(Text(h.Org, h.Record, h.OtherField, h.Other, "swapped after signing"), h.Org, op, 2) : c).ToList();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Contributor, prepared with { SignedItems = prepared.SignedItems }, altered));
        Assert.Equal(AuthorityErrorCodes.CandidateMismatch, ex.ErrorCode);
        Assert.Equal(1, h.Head.CommittedContentRevision);
    }

    [Fact]
    public async Task ACandidateBoundToAnotherTenantIsRefused()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor();
        var op = Guid.CreateVersion7();
        var foreign = h.Rows("after").Select(r => Encode(r, Guid.CreateVersion7(), op, 2)).ToList();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, op, foreign, withCurrent: true));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
    }

    /// <summary>A stand-in for ports the content protocol never calls; any call fails the test.</summary>
    public class ThrowingProxy : DispatchProxy
    {
        public static T Create<T>()
            where T : class => DispatchProxy.Create<T, ThrowingProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.DeclaringType?.Name + "." + targetMethod?.Name + " is not used by the content protocol");
    }
}
