using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// Per-organization fail-closed gate (A3): protected Allow is prohibited while any committed revision is not applied to
/// SpiceDB, while any authorization-stream journal entry lacks its archive receipt, or while the organization is quarantined.
/// <para>
/// SEC-BL-01 E-6 (C-158, A-7 §27 GT-1..GT-5): the archive condition no longer rests on mutable AuthorityDb fields. The latest authorization
/// entry is read from the journal itself (GT-1) and must carry a <b>trusted delivery receipt</b> (<see cref="IJournalReceiptVerifier"/>: the
/// archived object equals the row, the archive is the pinned instance, and the W2-S commitment names the row; GT-2). Under D+ an entry is archived
/// only after its predecessor (DP-9), so a verified head proves the whole prefix was delivered. The pending count stays as a cheap pre-check
/// (GT-3). The enforcement mode column is mutable AuthorityDb state, so it can no longer waive the requirement (GT-4). Every request reads the
/// head; only the receipt of a new head reaches the external stores (cached by row digest, GT-5), so a new revocation is never served from cache.
/// </para>
/// <para>
/// WP-7A (C-160, A-9 TR-6′): the head's receipt is a <b>stream-head</b> receipt: its current state is re-established at most the configured
/// freshness bound before the evaluation, and it is refused while any successor evidence exists outside AuthorityDb (an archived object,
/// evidence commitment or stop marker above the head), so a deleted or hidden newer entry closes the gate (RV-4).
/// </para>
/// </summary>
public static class OrganizationGate
{
    public static async Task<ProjectionGateState> EvaluateAsync(IAuthorityScope scope, OrganizationAuthorizationState state, CancellationToken cancellationToken)
    {
        if (state.IntegrityQuarantineEnabled)
        {
            return ProjectionGateState.Quarantined;
        }

        // Archive receipts precede projection (G14): report the earliest blocker; either one keeps the gate closed.
        if (await scope.CountPendingArchiveAsync(state.OrganizationId, JournalStreamNames.Authorization, cancellationToken).ConfigureAwait(false) > 0)
        {
            return ProjectionGateState.AwaitingArchive;
        }

        if (await scope.FindLatestJournalEntryAsync(state.OrganizationId, JournalStreamNames.Authorization, cancellationToken).ConfigureAwait(false) is { } head
            && !(await scope.JournalReceipts.VerifyStreamHeadAsync(head, cancellationToken).ConfigureAwait(false)).IsVerified)
        {
            return ProjectionGateState.AwaitingArchive;
        }

        if (state.ProjectionAppliedRevision != state.AuthorizationRevision || string.IsNullOrEmpty(state.ProjectionZedToken))
        {
            return ProjectionGateState.AwaitingProjection;
        }

        return ProjectionGateState.Ready;
    }

    public static void RequireOpen(ProjectionGateState gate)
    {
        switch (gate)
        {
            case ProjectionGateState.Ready:
                return;
            case ProjectionGateState.Quarantined:
                throw AuthorityException.Quarantined("organization quarantined");
            default:
                throw AuthorityException.NotReady(gate.ToString());
        }
    }
}

/// <summary>
/// The only way Authority code sets an organization's integrity quarantine. It is permanent under normal operation
/// (C8 M4 S1, B-Q7): the first reason is kept, and the AuthorityDb trigger refuses any UPDATE that clears it or changes its
/// reason. Lifting it is reserved for a separately reviewed G14 recovery process.
/// </summary>
public static class OrganizationQuarantine
{
    public static void Set(OrganizationAuthorizationState state, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IntegrityQuarantineEnabled)
        {
            return;
        }

        state.IntegrityQuarantineEnabled = true;
        state.IntegrityQuarantineReasonCode = reasonCode;
    }
}
