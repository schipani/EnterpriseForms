using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Sharing.RevokeShare;

/// <summary>Revocation is effective at the authority revision barrier; the local projection is updated afterwards and is irrelevant to enforcement.</summary>
public sealed class RevokeShareHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public async Task<CommandStatusResponse> HandleAsync(RequestContext context, Guid formRecordId, Guid recordShareId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.ToDelegatedUser();
        var decision = await authority.CheckPermissionAsync(user, new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.ManageAccess), cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new { record_share_id = recordShareId, form_record_id = formRecordId });
        var command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.RevokeShare, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), decision.AuthorizationRevision, payload, "revoke share"), "revoke:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (outcome.Effective)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var share = await scope.FindShareProjectionAsync(context.OrganizationId, recordShareId, cancellationToken).ConfigureAwait(false);
            if (share is not null && share.ShareStatus == "Active")
            {
                share.ShareStatus = "Revoked";
                share.ShareRevokedOn = clock.GetUtcNow();
                share.ShareRevokedByUserId = context.LocalUserId;
                share.ShareRevision++;
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new CommandStatusResponse(outcome);
    }
}
