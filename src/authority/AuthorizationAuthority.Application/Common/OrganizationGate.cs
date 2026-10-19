using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// Per-organization fail-closed gate (A3): protected Allow is prohibited while any committed revision is not applied to
/// SpiceDB, while any authorization-stream journal entry lacks its archive receipt, or while the organization is quarantined.
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
        if (string.Equals(state.CheckpointEnforcementModeName, CheckpointEnforcementModes.RequiredBeforeEffective, StringComparison.Ordinal)
            && await scope.CountPendingArchiveAsync(state.OrganizationId, JournalStreamNames.Authorization, cancellationToken).ConfigureAwait(false) > 0)
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
