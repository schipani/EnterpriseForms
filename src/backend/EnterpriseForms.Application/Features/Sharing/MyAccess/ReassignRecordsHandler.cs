using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Sharing.MyAccess;

/// <summary>
/// C10 M2 (WP-0 Section 3.7, D8-4, PG-053): reassign the records a member directly owns before the member is disabled. The Authority
/// lists them (administrators only) and each is moved by its own TransferOwnership command through the existing transfer handler —
/// same reason, step-up and administrator-override rules (G25: never to the administrator or a team they belong to), its own
/// idempotency key per record, and its own outcome. Nothing is transferred in bulk and a refusal of one record does not hide the
/// others' outcomes. A pending command is reported as pending, never as transferred.
/// </summary>
public sealed class ReassignRecordsHandler(IShareDetailsAuthority authority, TransferOwnershipHandler transfer, Common.Persistence.IBusinessStore store, TimeProvider clock)
{
    public async Task<MemberOwnedRecordsResponse> ListAsync(RequestContext context, Guid memberUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var owned = await OwnedAsync(context, memberUserId, cancellationToken).ConfigureAwait(false);
        // C10 review N-3: the administrator's listing of a member's records is audited after the Authority allowed it.
        await Audit.AuditRecorder.RecordAsync(store, context, clock, Audit.AuditCategories.Access, Domain.Features.Audit.AuditActions.MemberOwnedRecordsViewed,
            "member owned records listed", null, "Success", new { memberUserId, returnedCount = owned.FormRecordIds.Count }, cancellationToken).ConfigureAwait(false);
        return new MemberOwnedRecordsResponse(memberUserId, owned.FormRecordIds, owned.Truncated, owned.AuthorizationRevision);
    }

    public async Task<ReassignRecordsResponse> ReassignAsync(RequestContext context, Guid memberUserId, ReassignRecordsRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if ((request.NewOwnerUserId is null) == (request.NewOwnerTeamId is null) || string.IsNullOrWhiteSpace(request.TransferReason))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "exactly one new owner and a transfer reason are required");
        }

        var owned = await OwnedAsync(context, memberUserId, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<ReassignedRecordResponse>();
        foreach (var recordId in owned.FormRecordIds)
        {
            try
            {
                var result = await transfer.HandleAsync(context, recordId, new TransferOwnershipRequest(request.NewOwnerUserId, request.NewOwnerTeamId, request.TransferReason, request.UseDevelopmentStepUp),
                    idempotencyKey + ":" + recordId.ToString("N"), cancellationToken).ConfigureAwait(false);
                outcomes.Add(new ReassignedRecordResponse(recordId, result.Command.CommandStatus, result.Command.Effective, null));
            }
            catch (AuthorityClientException ex)
            {
                outcomes.Add(new ReassignedRecordResponse(recordId, "Refused", false, ex.ErrorCode));
            }
            catch (BusinessException ex)
            {
                outcomes.Add(new ReassignedRecordResponse(recordId, "Refused", false, ex.ErrorCode));
            }
        }

        return new ReassignRecordsResponse(memberUserId, outcomes, owned.Truncated);
    }

    private async Task<AuthorityMemberOwnedRecords> OwnedAsync(RequestContext context, Guid memberUserId, CancellationToken cancellationToken)
    {
        try
        {
            return await authority.GetMemberOwnedRecordsAsync(context.ToDelegatedUser(), context.OrganizationId, memberUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 404)
        {
            throw new BusinessException("MEMBER_NOT_FOUND", 404, "member not found");
        }
    }
}
