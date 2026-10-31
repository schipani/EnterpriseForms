using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Membership;

/// <summary>
/// C8 M2 (AC-7.3, D8-24): the organization's complete membership from the Authority's own state, behind the organization
/// gate — every identity binding with its enabled state and organization roles, plus two-owner proposals and membership
/// requests of the last 30 days with their state computed against the Authority clock. For the organization's owners and
/// Security Administrators only; anyone else, another organization included, gets the not-found answer. No record content,
/// no record counts, no session data.
/// </summary>
public sealed class OrganizationRosterService(IAuthorityStore store, FieldSearchApprovalGate gate, OperatorApprovalVerifier operatorApprovals, TimeProvider clock)
{
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    private static readonly string[] RoleOrder = [OrganizationRoleNames.OrganizationOwner, OrganizationRoleNames.Designer, OrganizationRoleNames.SystemAdministrator];

    public async Task<OrganizationRosterResponse> GetAsync(VerifiedActor actor, OrganizationRosterRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
        var isOwner = graph.HasOrganizationRole(state.OrganizationId, bound.AuthorityUserId, OrganizationRoleNames.OrganizationOwner);
        var isSecurityAdministrator = graph.HasOrganizationRole(state.OrganizationId, bound.AuthorityUserId, OrganizationRoleNames.SystemAdministrator);
        if (!isOwner && !isSecurityAdministrator)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var now = clock.GetUtcNow();
        var since = now - HistoryWindow;
        var bindings = await scope.GetIdentityBindingsAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var requests = await scope.GetMembershipRequestsAsync(state.OrganizationId, DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false);
        var hints = requests.Where(r => r.RequestStateName == MembershipRequestStates.Approved).ToDictionary(r => r.RequestedAuthorityUserId, r => r.RequestedDisplayNameHint);
        var members = bindings
            .Select(b => new OrganizationRosterMember(b.AuthorityUserId, b.IdentityIssuerUri, b.IdentitySubjectIdentifier, b.IdentityBindingEnabled, b.IdentityCreatedOn, b.IdentityDisabledOn,
                RoleOrder.Where(role => graph.HasOrganizationRole(state.OrganizationId, b.AuthorityUserId, role)).ToList(), hints.GetValueOrDefault(b.AuthorityUserId)))
            .OrderBy(m => m.SubjectIdentifier, StringComparer.Ordinal).ToList();
        var proposals = (await scope.GetRoleChangeProposalsAsync(state.OrganizationId, since, cancellationToken).ConfigureAwait(false))
            .Select(p => new OrganizationOwnerProposal(p.OrganizationRoleChangeProposalId, p.TargetAuthorityUserId, p.ProposedChangeName, p.ProposedByAuthorityUserId, p.ProposalReasonText, p.ProposalCreatedOn, p.ProposalExpiresOn,
                p.ProposalStateName == ProposalStates.Pending && p.ProposalExpiresOn <= now ? "Expired" : p.ProposalStateName, p.ConsumedBySecurityCommandId))
            .ToList();
        var replacedBy = requests.Where(r => r.ReplacesMembershipRequestId is not null).GroupBy(r => r.ReplacesMembershipRequestId!.Value).ToDictionary(g => g.Key, g => (Guid?)g.First().OrganizationMembershipRequestId);
        var requestEntries = requests.Where(r => r.RequestCreatedOn >= since || r.RequestStateName == MembershipRequestStates.Pending).Select(r => ToEntry(r, now, replacedBy.GetValueOrDefault(r.OrganizationMembershipRequestId))).ToList();
        return new OrganizationRosterResponse(isOwner, isSecurityAdministrator, gate.ApprovalsOperational, gate.UnavailableReason, operatorApprovals.Operational,
            CommandPlanner.OrganizationIdentityIssuer(graph, state.OrganizationId, bindings), members, proposals, requestEntries, state.AuthorizationRevision, correlationId);
    }

    public static OrganizationMembershipRequestEntry ToEntry(OrganizationMembershipRequest r, DateTimeOffset now, Guid? replacedByRequestId = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        var stateName = r.RequestStateName == MembershipRequestStates.Pending && r.RequestExpiresOn <= now ? MembershipRequestStates.Expired : r.RequestStateName;
        return new OrganizationMembershipRequestEntry(r.OrganizationMembershipRequestId, r.RequestedAuthorityUserId, r.RequestedIssuerUri, r.RequestedSubjectIdentifier, r.RequestedDisplayNameHint, r.RequestedInitialRoleName,
            r.RequestedByAuthorityUserId, r.RequestReasonText, r.RequestCreatedOn, r.RequestExpiresOn, stateName, r.RequestDecidedOn, r.RequestDecisionReasonText,
            MembershipRequestDigest.Compute(r), r.ReplacesMembershipRequestId, replacedByRequestId, r.RequestCancelledOn);
    }
}
