using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.TransferOwnership;

/// <summary>Ownership transfer is an authority command; the local owner projection is updated only after it is Effective and never consulted for authorization.</summary>
public sealed class TransferOwnershipHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock)
{
    public async Task<CommandStatusResponse> HandleAsync(RequestContext context, Guid formRecordId, TransferOwnershipRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if ((request.NewOwnerUserId is null) == (request.NewOwnerTeamId is null) || string.IsNullOrWhiteSpace(request.TransferReason))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "exactly one new owner and a transfer reason are required");
        }

        var user = context.ToDelegatedUser();
        var decision = await authority.CheckPermissionAsync(user, new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.TransferOwnership), cancellationToken).ConfigureAwait(false);
        // A system administrator override is validated by the authority; a denied discover check still returns the revision needed for CAS.
        var payload = JsonSerializer.SerializeToElement(new { form_record_id = formRecordId, new_owner_user_id = request.NewOwnerUserId, new_owner_team_id = request.NewOwnerTeamId });
        var command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.TransferOwnership, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), decision.AuthorizationRevision, payload, request.TransferReason), "transfer:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (outcome.Effective)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var record = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                record.OwnerUserId = request.NewOwnerUserId;
                record.OwnerTeamId = request.NewOwnerTeamId;
                record.ModifiedOn = clock.GetUtcNow();
                record.ModifiedByUserId = context.LocalUserId;
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new CommandStatusResponse(outcome);
    }
}
