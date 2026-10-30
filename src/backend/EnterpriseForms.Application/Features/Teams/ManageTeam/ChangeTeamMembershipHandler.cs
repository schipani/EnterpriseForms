using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Teams.MembershipReconciliation;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Teams;

namespace EnterpriseForms.Application.Features.Teams.ManageTeam;

/// <summary>
/// Phase C8 (C8-M1-01, R-3): team add/remove. The operation is recorded durably BEFORE the command is submitted (T1) and the
/// Authority's command id and revision are recorded after it (T2); the Worker's reconciler then projects the Authority's
/// confirmed pair state. This handler never writes <c>team_memberships</c> (single logical writer, D8-21, I-10). A terminal
/// operation is never re-sent (I-8); the same key with a different request is refused without a second operation (RC-12).
/// </summary>
public sealed class ChangeTeamMembershipHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TeamMembershipReconciliationOptions options, TimeProvider clock)
{
    private static readonly int[] DefinitiveRefusals = [400, 401, 403, 404, 409, 422, 423];

    public async Task<TeamMembershipChangeResponse> HandleAsync(RequestContext context, Guid teamId, Guid memberUserId, bool add, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = add ? TeamMembershipCommandKinds.AddTeamMember : TeamMembershipCommandKinds.RemoveTeamMember;
        var authorityKey = TeamMembershipCommandKinds.KeyPrefix(kind) + idempotencyKey;
        var user = context.ToDelegatedUser();
        // N-2: a preliminary gate BEFORE anything is persisted, so a caller the Authority would refuse cannot fill the operations table
        // with rejected commands. It asks the Authority for the same two principals its team-command rule accepts (organization owner
        // or Security Administrator) and widens nothing. It is only a gate: the Authority still decides the command itself at submit,
        // bound to this revision, so a change of authorization after this point is refused there (409/403) and recorded as Rejected.
        var decision = await AdministrationAccess.RequireAdministratorAsync(authority, context, cancellationToken).ConfigureAwait(false);

        var operation = await RecordAsync(context, teamId, memberUserId, kind, authorityKey, cancellationToken).ConfigureAwait(false);
        if (operation.IsTerminal)
        {
            return await ReplayTerminalAsync(context, operation, cancellationToken).ConfigureAwait(false);
        }

        SecurityCommandResult command;
        try
        {
            var payload = JsonSerializer.SerializeToElement(new { team_id = teamId, member_user_id = memberUserId });
            var request = new SecurityCommandRequest(context.OrganizationId, add ? SecurityCommandKind.AddTeamMember : SecurityCommandKind.RemoveTeamMember, new ResourceReference(ProtectedResourceKind.Team, teamId), decision.AuthorizationRevision, payload, add ? "add member" : "remove member");
            command = await authority.SubmitSecurityCommandAsync(user, request, authorityKey, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (Array.IndexOf(DefinitiveRefusals, ex.HttpStatusCode) >= 0)
        {
            // The Authority answered: this request was refused and consumed no revision. Recorded as Rejected; never projected.
            await ChangeAsync(operation.AuthorityCommandOperationId, o =>
            {
                o.OperationStateName = AuthorityCommandOperationStates.Rejected;
                o.RejectionReasonCode = Truncate(ex.ErrorCode, 80);
                o.OperationCompletedOn = clock.GetUtcNow();
            }, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // T2: the committed command's identity (NULL → value only); due for the Worker at once.
        await ChangeAsync(operation.AuthorityCommandOperationId, o =>
        {
            o.AuthorityCommandId ??= command.CommandId;
            if (command.CommandStatus == SecurityCommandStatus.Rejected)
            {
                o.OperationStateName = AuthorityCommandOperationStates.Rejected;
                o.RejectionReasonCode = Truncate(command.RejectionReasonCode ?? "COMMAND_REJECTED", 80);
                o.OperationCompletedOn = clock.GetUtcNow();
                return;
            }

            o.CommittedAuthorizationRevision ??= command.AuthorizationRevision;
            if (o.OperationStateName == AuthorityCommandOperationStates.AwaitingCommit)
            {
                o.OperationStateName = AuthorityCommandOperationStates.AwaitingEffective;
            }

            o.NextAttemptOn = clock.GetUtcNow();
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            command = await awaiter.AwaitAsync(user, command, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == "PROJECTION_DIVERGENCE")
        {
            // FailedReconciliation: the Worker confirms it through AC-9 and records AuthorityFailed; nothing is projected.
        }

        var effective = command.CommandStatus == SecurityCommandStatus.Effective;
        if (effective)
        {
            await ChangeAsync(operation.AuthorityCommandOperationId, o => o.NextAttemptOn = clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        var current = await AwaitProjectionAsync(operation.AuthorityCommandOperationId, effective ? TimeSpan.FromSeconds(options.ApiProjectionWaitSeconds) : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, effective);
        return new TeamMembershipChangeResponse(outcome, await TeamMembershipOperationView.ToResponseAsync(store, context, current, cancellationToken).ConfigureAwait(false));
    }

    private async Task<AuthorityCommandOperation> RecordAsync(RequestContext context, Guid teamId, Guid memberUserId, string kind, string authorityKey, CancellationToken cancellationToken)
    {
        if (authorityKey.Length > 128)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "Idempotency-Key is too long for a team membership command");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var existing = await scope.FindAuthorityCommandOperationByKeyAsync(context.OrganizationId, authorityKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var now = clock.GetUtcNow();
            var operation = new AuthorityCommandOperation
            {
                AuthorityCommandOperationId = Guid.CreateVersion7(),
                TenantId = context.OrganizationId,
                InitiatedByUserId = context.LocalUserId,
                CommandKindName = kind,
                TeamId = teamId,
                MemberUserId = memberUserId,
                AuthorityIdempotencyKey = authorityKey,
                RequestCorrelationIdentifier = context.CorrelationId,
                OperationCreatedOn = now,
                OperationStateName = AuthorityCommandOperationStates.AwaitingCommit,
                // If this request dies before T2, the Worker resolves the operation by key after the in-flight window (RC-06/RC-07).
                NextAttemptOn = now.Add(options.InFlightWindow),
                OperationModifiedOn = now,
            };
            if (await scope.TryAddAuthorityCommandOperationAsync(operation, cancellationToken).ConfigureAwait(false))
            {
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                return operation;
            }

            existing = await scope.FindAuthorityCommandOperationByKeyAsync(context.OrganizationId, authorityKey, cancellationToken).ConfigureAwait(false); // an identical key raced this request
        }

        if (existing is null || existing.TeamId != teamId || existing.MemberUserId != memberUserId || existing.CommandKindName != kind || existing.InitiatedByUserId != context.LocalUserId)
        {
            throw new BusinessException("IDEMPOTENCY_KEY_REUSED", 409, "this Idempotency-Key was already used for a different team membership request");
        }

        return existing;
    }

    private async Task<TeamMembershipChangeResponse> ReplayTerminalAsync(RequestContext context, AuthorityCommandOperation operation, CancellationToken cancellationToken)
    {
        switch (operation.OperationStateName)
        {
            case AuthorityCommandOperationStates.Rejected:
                throw new AuthorityClientException(operation.RejectionReasonCode ?? "COMMAND_REJECTED", 422, false, "security command rejected: " + operation.RejectionReasonCode);
            case AuthorityCommandOperationStates.NotCommitted:
                throw new BusinessException("OPERATION_NOT_COMMITTED", 409, "the Authority never received this request; it is not sent again — start a new change");
            case AuthorityCommandOperationStates.AuthorityFailed:
                throw new AuthorityClientException("PROJECTION_DIVERGENCE", 503, false, "security command failed reconciliation; operator intervention required");
        }

        var outcome = new CommandOutcome(operation.AuthorityCommandId ?? Guid.Empty, "Effective", operation.CommittedAuthorizationRevision ?? 0, true);
        return new TeamMembershipChangeResponse(outcome, await TeamMembershipOperationView.ToResponseAsync(store, context, operation, cancellationToken).ConfigureAwait(false));
    }

    private async Task<AuthorityCommandOperation> AwaitProjectionAsync(Guid operationId, TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow().Add(budget);
        while (true)
        {
            AuthorityCommandOperation? operation;
            await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
            {
                operation = await scope.FindAuthorityCommandOperationAsync(operationId, lockForUpdate: false, cancellationToken).ConfigureAwait(false);
            }

            if (operation is null)
            {
                throw new BusinessException("NOT_FOUND", 404, "team membership operation not found");
            }

            if (operation.IsTerminal || clock.GetUtcNow() >= deadline)
            {
                return operation;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Operation bookkeeping only (never the roster); a terminal row is left untouched (the schema-owned trigger would refuse it anyway).</summary>
    private async Task ChangeAsync(Guid operationId, Action<AuthorityCommandOperation> change, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var operation = await scope.FindAuthorityCommandOperationAsync(operationId, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.IsTerminal)
        {
            return;
        }

        change(operation);
        operation.OperationModifiedOn = clock.GetUtcNow();
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
