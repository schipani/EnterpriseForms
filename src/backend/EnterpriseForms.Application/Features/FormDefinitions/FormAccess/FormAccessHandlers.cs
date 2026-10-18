using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.FormDefinitions.FormAccess;

/// <summary>
/// Who may create records for an existing form (the form_user relation). The independent Authorization Authority is the
/// only source of truth: reads come from its desired state behind the organization gate, writes are journaled
/// <c>UpdateFormUsers</c> commands authorized against the caller's design permission (or organization administration).
/// Business tables hold no copy, so a hostile database edit cannot grant or hide anything. A change never creates a form
/// version and never touches record ownership or shares: it grants no read/update/delete on any existing record, and
/// revoking it does not remove records the principal already owns.
/// </summary>
public sealed class FormAccessHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    private const int MaxChangesPerCommand = 200;

    public async Task<FormAccessResponse> GetAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = await authority.GetFormAccessAsync(context.ToDelegatedUser(), context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        return await ToResponseAsync(context, state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormAccessResponse> UpdateAsync(RequestContext context, Guid formDefinitionId, UpdateFormAccessRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var grantUsers = (request.GrantUserIds ?? []).Distinct().ToList();
        var grantTeams = (request.GrantTeamIds ?? []).Distinct().ToList();
        var revokeUsers = (request.RevokeUserIds ?? []).Distinct().ToList();
        var revokeTeams = (request.RevokeTeamIds ?? []).Distinct().ToList();
        var reason = request.ChangeReason?.Trim() ?? string.Empty;
        var total = grantUsers.Count + grantTeams.Count + revokeUsers.Count + revokeTeams.Count;
        if (total == 0 || total > MaxChangesPerCommand || reason.Length is < 3 or > 500 || grantUsers.Concat(grantTeams).Concat(revokeUsers).Concat(revokeTeams).Any(id => id == Guid.Empty))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "1..200 principal changes and a change reason of 3..500 characters are required");
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            // Tenant guard on the business side; the authority independently verifies the organization binding and registration.
            if (await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            }
        }

        var user = context.ToDelegatedUser();
        // Reading first both proves the caller may manage this form (the authority answers 404 otherwise) and yields the revision the command is bound to.
        var before = await authority.GetFormAccessAsync(user, context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new
        {
            form_definition_id = formDefinitionId,
            grant_user_ids = grantUsers,
            grant_team_ids = grantTeams,
            revoke_user_ids = revokeUsers,
            revoke_team_ids = revokeTeams,
        });
        var command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.UpdateFormUsers, new ResourceReference(ProtectedResourceKind.FormDefinition, formDefinitionId), before.AuthorizationRevision, payload, reason), "formaccess:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (!outcome.Effective)
        {
            throw new PendingCommandException(outcome);
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            void Audit(string action, string kind, Guid subjectId) =>
                AuditRecorder.Record(scope, context, clock, AuditCategories.Access, action, "form create-record permission changed by the authority", formDefinitionId: formDefinitionId, targetEntityType: "form_definition", targetEntityId: formDefinitionId, metadata: new { subjectKind = kind, subjectId, commandId = command.CommandId, changeReason = reason });
            grantUsers.ForEach(id => Audit(AuditActions.FormAccessGranted, "User", id));
            grantTeams.ForEach(id => Audit(AuditActions.FormAccessGranted, "Team", id));
            revokeUsers.ForEach(id => Audit(AuditActions.FormAccessRevoked, "User", id));
            revokeTeams.ForEach(id => Audit(AuditActions.FormAccessRevoked, "Team", id));
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Effective means committed, archived and applied to the projection: read the resulting state back from the authority.
        return await GetAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FormAccessResponse> ToResponseAsync(RequestContext context, FormAccessState state, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var teams = await scope.GetTeamsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var teamNames = teams.ToDictionary(t => t.TeamId, t => t.TeamName);
        // Display names are a local directory projection; an unknown principal is still listed (by id) because the authority says so.
        FormAccessSubjectResponse Map(FormAccessSubjectState subject) => new(subject.SubjectKind, subject.SubjectId,
            (subject.SubjectKind == "Team" ? teamNames.GetValueOrDefault(subject.SubjectId) : users.GetValueOrDefault(subject.SubjectId)) ?? subject.SubjectId.ToString("D"));
        return new FormAccessResponse(
            state.FormDefinitionId,
            state.AuthorizationRevision,
            "Authorization Authority (independent desired state, projection applied)",
            state.Designers.Select(Map).ToList(),
            state.FormUsers.Select(Map).ToList(),
            teams.Select(t => new DirectoryTeamResponse(t.TeamId, t.TeamName)).ToList());
    }
}
