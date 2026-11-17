using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;
using static AuthorizationAuthority.Tests.EnvelopeCapabilityTestSupport;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §6) on the real <see cref="ContentService"/> Prepare and Commit paths: in an organization
/// with a data-key binding, a request without a level-2 declaration is refused before snapshot binding, replay, expiry or CARV aborts and
/// staged verification, and writes nothing (M2F-10, M2F-11, M2F-17, M2F-26); the Release B fence still comes first (M2F-18). Each test also
/// runs the identical request from a level-2 build as the control: that is the write or journal entry the refusal prevents (G-2).
/// Bindings are inserted into the in-memory ledger directly; Envelope v2 acceptance is never enabled.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static readonly ClientEnvelopeCapability?[] BelowLevelTwo = [null, ClientEnvelopeCapability.Legacy];

    /// <summary>
    /// The accepted current snapshot with the shared value row re-encoded (same item, same signature, different canonical bytes): what an
    /// honest older reader submits for a schema-2 row it re-encodes as schema 1 (G-2).
    /// </summary>
    private static List<StagedContentItem> ReencodedCurrent(Harness h) =>
        h.Accepted.Select(a => a.Row.ItemId == h.Shared
            ? new StagedContentItem(a.Row.Kind, a.Row.ItemId, Encode(Text(h.Org, h.Record, h.SharedField, h.Shared, "re-encoded"), h.Org, h.Head.CommittedOperationId!.Value, h.Head.CommittedContentRevision).Base64, Convert.ToBase64String(a.Signature))
            : new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList();

    private static Task<PreparedContentOperation> PrepareWithAsync(Harness h, PrepareContentChangeRequest request, string key, ClientEnvelopeCapability? capability) =>
        h.Service.PrepareAsync(Harness.Actor(h.Owner), request, key, Guid.CreateVersion7(), CancellationToken.None, capability);

    private static Task<ContentRevision> CommitWithAsync(Harness h, Guid operation, List<StagedContentItem> staged, ClientEnvelopeCapability? capability) =>
        h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None, capability);

    private static List<StagedContentItem> Signed(PreparedContentOperation prepared, IEnumerable<Staged> candidate)
    {
        var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
        return candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
    }

    [Fact]
    public async Task M2F10APrepareBelowLevelTwoInABoundOrganizationIsRefusedBeforeSnapshotBindingCanJournal()
    {
        var h = await new Harness().CreatedAsync();
        h.Store.DataKeyBindings.Add(Binding(h.Org, h.Record));
        var operation = Guid.CreateVersion7();
        var request = h.Request(operation, h.Stage(h.Rows("after"), operation), withCurrent: false) with { CurrentItems = ReencodedCurrent(h) };
        var key = "key-" + operation.ToString("N");
        var before = Fingerprint(h.Store);
        var signatures = h.Signer.SignatureCount;

        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => PrepareWithAsync(h, request, key, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store)); // no operation, no journal entry, no revision, no scope commit
        Assert.Equal(signatures, h.Signer.SignatureCount);
        Assert.Equal(0, IntegrityIncidents(h.Store));

        // Control: the same request from a level-2 build reaches snapshot binding, which journals CurrentSnapshotMismatch (W4).
        var integrityRevision = h.State.ContentIntegrityRevision;
        Assert.Equal(AuthorityErrorCodes.CurrentSnapshotMismatch, (await Assert.ThrowsAsync<AuthorityException>(() => PrepareWithAsync(h, request, key, ClientEnvelopeCapability.Current))).ErrorCode);
        Assert.Equal(1, IntegrityIncidents(h.Store));
        Assert.Equal(integrityRevision + 1, h.State.ContentIntegrityRevision);
    }

    [Fact]
    public async Task M2F11ACommitBelowLevelTwoInABoundOrganizationIsRefusedBeforeExpiryAbortAndStagedVerification()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows("after"), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true); // before the binding: a level-1 Prepare is admitted
        h.Store.DataKeyBindings.Add(Binding(h.Org, h.Record));
        h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1);
        var staged = Signed(prepared, candidate);
        var before = Fingerprint(h.Store);

        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => CommitWithAsync(h, operation, staged, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.Equal(ContentOperationStates.Prepared, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationStateName); // no abort

        // Control: a level-2 build reaches the expiry check, which aborts the operation.
        Assert.Equal(AuthorityErrorCodes.OperationExpired, (await Assert.ThrowsAsync<AuthorityException>(() => CommitWithAsync(h, operation, staged, ClientEnvelopeCapability.Current))).ErrorCode);
        Assert.Equal(ContentOperationStates.Aborted, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationStateName);
    }

    [Fact]
    public async Task M2F11ReencodedStagedBytesBelowLevelTwoAreRefusedBeforeTheCandidateIsVerified()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows("after"), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        h.Store.DataKeyBindings.Add(Binding(h.Org, h.Record));
        var staged = Signed(prepared, candidate);
        var index = staged.FindIndex(s => s.ItemId == h.Shared);
        staged[index] = staged[index] with { CanonicalBytesBase64 = Encode(Text(h.Org, h.Record, h.SharedField, h.Shared, "after, re-encoded"), h.Org, operation, h.Head.CommittedContentRevision + 1).Base64 };
        var before = Fingerprint(h.Store);
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId);

        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => CommitWithAsync(h, operation, staged, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId));
        Assert.Equal(AuthorityErrorCodes.CandidateMismatch, (await Assert.ThrowsAsync<AuthorityException>(() => CommitWithAsync(h, operation, staged, ClientEnvelopeCapability.Current))).ErrorCode); // control
    }

    [Fact]
    public async Task M2F17PrepareAndCommitReplaysBelowLevelTwoAreRefusedInABoundOrganization()
    {
        var h = await new Harness().CreatedAsync();
        h.Store.DataKeyBindings.Add(Binding(h.Org, h.Record));
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows("after"), operation);
        var request = h.Request(operation, candidate, withCurrent: true);
        var key = "key-" + operation.ToString("N");
        var queries = h.Store.DataKeyBindingExistenceQueries;
        var prepared = await PrepareWithAsync(h, request, key, ClientEnvelopeCapability.Current); // level 2 is admitted without reading the ledger
        Assert.Equal(queries, h.Store.DataKeyBindingExistenceQueries);

        var before = Fingerprint(h.Store);
        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => PrepareWithAsync(h, request, key, capability))); // never the replayed operation
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.Equal(prepared.OperationId, (await PrepareWithAsync(h, request, key, ClientEnvelopeCapability.Current)).OperationId); // level-2 replay unchanged

        var staged = Signed(prepared, candidate);
        var revision = await CommitWithAsync(h, operation, staged, ClientEnvelopeCapability.Current);
        before = Fingerprint(h.Store);
        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => CommitWithAsync(h, operation, staged, capability))); // never the idempotent revision
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.Equal(revision.CommittedOperationId, (await CommitWithAsync(h, operation, staged, ClientEnvelopeCapability.Current)).CommittedOperationId);
    }

    [Fact]
    public async Task M2F18TheReleaseBFenceStillRefusesFirstAndTheLedgerIsNotRead()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows("after"), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        h.Store.DataKeyBindings.Add(Binding(h.Org, h.Record));
        h.Store.ContentAcceptanceFenceRefusal = ContentAcceptanceFloorFailures.FloorNotInstalled;
        var queries = h.Store.DataKeyBindingExistenceQueries;

        Assert.Equal(AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced,
            (await Assert.ThrowsAsync<AuthorityException>(() => PrepareWithAsync(h, h.Request(Guid.CreateVersion7(), candidate, withCurrent: true), "key-fence-first", null))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced,
            (await Assert.ThrowsAsync<AuthorityException>(() => CommitWithAsync(h, operation, Signed(prepared, candidate), ClientEnvelopeCapability.Legacy))).ErrorCode);
        Assert.Equal(queries, h.Store.DataKeyBindingExistenceQueries);
    }

    [Fact]
    public async Task ALevelOnePrepareAndCommitInAnUnboundOrganizationAreUnchanged()
    {
        // CAP-S5 compatibility: E3-M2c..E3-M2e-2 business builds send no header (level 1) and keep working where no binding exists.
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows("level one"), operation);
        var queries = h.Store.DataKeyBindingExistenceQueries;
        var prepared = await PrepareWithAsync(h, h.Request(operation, candidate, withCurrent: true), "key-" + operation.ToString("N"), null);
        var revision = await CommitWithAsync(h, operation, Signed(prepared, candidate), ClientEnvelopeCapability.Legacy);
        Assert.Equal(2, revision.CommittedContentRevision);
        Assert.Equal(queries + 2, h.Store.DataKeyBindingExistenceQueries); // one boolean query per level-1 evaluation
    }
}
