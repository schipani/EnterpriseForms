using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// RB-CARV-1 (contract §22) on the real <see cref="ContentService"/>: every content write entry (Prepare, Prepare replay, Commit,
/// Commit replay) asks the store for the content write fence once, first, and a Release B refusal writes nothing and advances
/// nothing. The fence verification itself is PostgreSQL-only (harness R); here the in-memory store stands in for its verdict.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static void AssertFloorRefusal(AuthorityException ex) =>
        Assert.Equal((AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced, 503, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));

    [Fact]
    public async Task EveryContentWriteEntryChecksTheFenceExactlyOnce()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("checked"))), operation);

        var before = h.Store.ContentAcceptanceFenceChecks;
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        Assert.Equal(before + 1, h.Store.ContentAcceptanceFenceChecks);
        await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true); // replay
        Assert.Equal(before + 2, h.Store.ContentAcceptanceFenceChecks);
        await h.CommitAsync(h.Owner, prepared, candidate);
        Assert.Equal(before + 3, h.Store.ContentAcceptanceFenceChecks);
        await h.CommitAsync(h.Owner, prepared, candidate); // idempotent replay
        Assert.Equal(before + 4, h.Store.ContentAcceptanceFenceChecks);
    }

    [Fact]
    public async Task AReleaseBInstanceWhoseFloorIsNotEnforcedRefusesPrepareAndWritesNothing()
    {
        var h = await new Harness().CreatedAsync();
        var before = StateOf(h);
        var operations = h.Store.Operations.Count;
        var signatures = h.Signer.SignatureCount;
        h.Store.ContentAcceptanceFenceRefusal = ContentAcceptanceFloorFailures.FloorNotInstalled;

        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("refused"))), operation), withCurrent: true));

        AssertFloorRefusal(ex);
        Assert.Equal(before, StateOf(h));
        Assert.Equal(operations, h.Store.Operations.Count);
        Assert.Equal(signatures, h.Signer.SignatureCount); // refused before any signing
    }

    [Fact]
    public async Task AnInFlightOperationCannotBeReplayedOrCommittedWhileTheFloorIsNotEnforcedAndCommitsOnceItIs()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("in flight"))), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        var before = StateOf(h);

        h.Store.ContentAcceptanceFenceRefusal = ContentAcceptanceFloorFailures.FloorRecordedButMissing;
        AssertFloorRefusal(await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true)));
        AssertFloorRefusal(await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, prepared, candidate)));
        Assert.Equal(before, StateOf(h));
        Assert.Equal(ContentOperationStates.Prepared, Operation(h, operation).OperationStateName); // not aborted, not committed

        h.Store.ContentAcceptanceFenceRefusal = null; // the floor reinstated and verified
        var revision = await h.CommitAsync(h.Owner, prepared, candidate);
        Assert.Equal(before.Revision + 1, revision.CommittedContentRevision);
    }

    [Fact]
    public async Task ACommitReplayIsRefusedWhileTheFloorIsNotEnforced()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("replayed"))), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        await h.CommitAsync(h.Owner, prepared, candidate);
        var before = StateOf(h);

        h.Store.ContentAcceptanceFenceRefusal = ContentAcceptanceFloorFailures.FloorUnverifiable;
        AssertFloorRefusal(await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, prepared, candidate)));
        Assert.Equal(before, StateOf(h));
    }
}
