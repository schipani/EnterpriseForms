using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Teams;

namespace EnterpriseForms.Application.Features.Teams.ManageTeam;

/// <summary>Maps a durable team-membership operation to its browser view (never the idempotency key).</summary>
public static class TeamMembershipOperationView
{
    public static async Task<TeamMembershipOperationResponse> ToResponseAsync(IBusinessStore store, RequestContext context, AuthorityCommandOperation operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        return ToResponse(operation, names);
    }

    public static TeamMembershipOperationResponse ToResponse(AuthorityCommandOperation operation, IReadOnlyDictionary<Guid, string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(displayNames);
        var state = operation.OperationStateName;
        return new TeamMembershipOperationResponse(
            operation.AuthorityCommandOperationId,
            operation.TeamId,
            operation.MemberUserId,
            displayNames.GetValueOrDefault(operation.MemberUserId) ?? "Unknown person",
            operation.CommandKindName == TeamMembershipCommandKinds.AddTeamMember ? "add" : "remove",
            state,
            operation.IsTerminal,
            state == AuthorityCommandOperationStates.Projected,
            operation.OperationOutcomeCode,
            operation.SupersededByLaterRevision == true,
            operation.RejectionReasonCode,
            operation.LastReconciliationErrorCode,
            operation.NeedsAttentionFlaggedOn is not null,
            operation.LateCommitDetectedOn is not null,
            operation.OperationReviewedOn is not null,
            operation.OperationCreatedOn,
            operation.OperationCompletedOn,
            CanRecheck(operation),
            CanMarkReviewed(operation));
    }

    /// <summary>"Check again" asks the Authority once more; it never submits. Not offered for terminal operations (I-9).</summary>
    public static bool CanRecheck(AuthorityCommandOperation operation) => !operation.IsTerminal;

    public static bool CanMarkReviewed(AuthorityCommandOperation operation) =>
        operation.OperationReviewedOn is null
        && (operation.OperationStateName is AuthorityCommandOperationStates.ReconciliationError or AuthorityCommandOperationStates.AuthorityFailed or AuthorityCommandOperationStates.NotCommitted || operation.LateCommitDetectedOn is not null);
}

/// <summary>
/// Phase C8 (R-4, RC-16, RC-17): read the durable operations of a team, and the two audited operator actions. Same principals
/// as the team detail (organization owner or Security Administrator). "Check again" only schedules a fresh AC-9 lookup; a
/// <c>ReconciliationError</c> re-opens to the state its recorded command implies and still projects only through the
/// reconciler's validation (I-1…I-3, I-9). Neither action submits, replays or alters an Authority command.
/// </summary>
public sealed class TeamMembershipOperationHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public async Task<TeamMembershipOperationResponse> GetAsync(RequestContext context, Guid teamId, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        var operation = await FindAsync(context, teamId, operationId, cancellationToken).ConfigureAwait(false);
        return await TeamMembershipOperationView.ToResponseAsync(store, context, operation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The caller's own browser key (8..100 characters) after a lost response; the key itself is never returned.</summary>
    public async Task<TeamMembershipOperationResponse> GetByClientKeyAsync(RequestContext context, Guid teamId, string clientKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientKey) || clientKey.Length is < 8 or > 100)
        {
            throw NotFound();
        }

        AuthorityCommandOperation? operation = null;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var kind in new[] { TeamMembershipCommandKinds.AddTeamMember, TeamMembershipCommandKinds.RemoveTeamMember })
            {
                operation ??= await scope.FindAuthorityCommandOperationByKeyAsync(context.OrganizationId, TeamMembershipCommandKinds.KeyPrefix(kind) + clientKey, cancellationToken).ConfigureAwait(false);
            }
        }

        if (operation is null || operation.TeamId != teamId || operation.InitiatedByUserId != context.LocalUserId)
        {
            throw NotFound();
        }

        return await TeamMembershipOperationView.ToResponseAsync(store, context, operation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamMembershipOperationResponse> RecheckAsync(RequestContext context, Guid teamId, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        AuthorityCommandOperation operation;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            operation = await scope.FindAuthorityCommandOperationAsync(operationId, lockForUpdate: true, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            if (operation.TenantId != context.OrganizationId || operation.TeamId != teamId)
            {
                throw NotFound();
            }

            if (!TeamMembershipOperationView.CanRecheck(operation))
            {
                throw new BusinessException("OPERATION_COMPLETED", 409, "this operation is complete; checking again changes nothing");
            }

            var previous = operation.OperationStateName;
            var now = clock.GetUtcNow();
            if (previous == AuthorityCommandOperationStates.ReconciliationError)
            {
                operation.OperationStateName = operation.CommittedAuthorizationRevision is null ? AuthorityCommandOperationStates.AwaitingCommit : AuthorityCommandOperationStates.AwaitingEffective;
            }

            operation.NextAttemptOn = now;
            operation.ConsecutiveErrorCount = 0;
            operation.OperationModifiedOn = now;
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.TeamMembershipOperationRechecked, "team membership operation checked again (no command submitted)", targetEntityType: "team", targetEntityId: teamId, metadata: new { authorityCommandOperationId = operation.AuthorityCommandOperationId, previousOperationState = previous, previousErrorCode = operation.LastReconciliationErrorCode });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await TeamMembershipOperationView.ToResponseAsync(store, context, operation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamMembershipOperationResponse> MarkReviewedAsync(RequestContext context, Guid teamId, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);
        AuthorityCommandOperation operation;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            operation = await scope.FindAuthorityCommandOperationAsync(operationId, lockForUpdate: true, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            if (operation.TenantId != context.OrganizationId || operation.TeamId != teamId)
            {
                throw NotFound();
            }

            if (!TeamMembershipOperationView.CanMarkReviewed(operation))
            {
                throw new BusinessException("OPERATION_NOT_REVIEWABLE", 409, "only failed or anomalous operations are marked reviewed");
            }

            var now = clock.GetUtcNow();
            operation.OperationReviewedOn = now;
            operation.OperationReviewedByUserId = context.LocalUserId;
            operation.OperationModifiedOn = now;
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.TeamMembershipOperationReviewed, "team membership operation failure reviewed by an administrator", targetEntityType: "team", targetEntityId: teamId, metadata: new { authorityCommandOperationId = operation.AuthorityCommandOperationId, operationState = operation.OperationStateName, errorCode = operation.LastReconciliationErrorCode, lateCommitDetected = operation.LateCommitDetectedOn is not null });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await TeamMembershipOperationView.ToResponseAsync(store, context, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthorityCommandOperation> FindAsync(RequestContext context, Guid teamId, Guid operationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var operation = await scope.FindAuthorityCommandOperationAsync(operationId, lockForUpdate: false, cancellationToken).ConfigureAwait(false);
        return operation is not null && operation.TenantId == context.OrganizationId && operation.TeamId == teamId ? operation : throw NotFound();
    }

    private static BusinessException NotFound() => new("NOT_FOUND", 404, "team membership operation not found");
}
