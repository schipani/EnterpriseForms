using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// C8 owner recovery (C-105): the organization administration notice. The Authority decides who may see the open case and object
/// (enabled owners and Security Administrators); anyone else gets no case and cannot object. An objection is confirmed only after the
/// Authority has recorded it (journal and organization audit stream). The business plane cannot open, approve or execute a case, and
/// never handles Platform Operator credentials or signatures. Display names come from the local directory (labels only).
/// </summary>
public sealed class OwnerRecoveryNoticeHandlers(IBusinessStore store, IOwnerRecoveryNoticeAuthority authority)
{
    public const string SyntheticStepUpMarker = "synthetic-development-step-up";

    public async Task<OwnerRecoveryNoticeResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthorityOwnerRecoveryNotice notice;
        try
        {
            notice = await authority.GetOwnerRecoveryNoticeAsync(context.ToDelegatedUser(), new OwnerRecoveryNoticeQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode is 403 or 404)
        {
            return new OwnerRecoveryNoticeResponse(null, false);
        }

        if (notice.Case is not { } open)
        {
            return new OwnerRecoveryNoticeResponse(null, false);
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        return new OwnerRecoveryNoticeResponse(new OwnerRecoveryNoticeCaseResponse(open.CaseId, open.IncidentReference, open.Justification, open.OpenedOn, open.WaitingPeriodEndsOn, open.ApprovalsRecorded, open.ApprovalsRequired,
            open.Subjects.Select(s => new OwnerRecoveryNoticeSubjectResponse(s.Role, s.AuthorityUserId, names.GetValueOrDefault(s.AuthorityUserId) ?? s.DisplayNameHint, s.IsNewIdentity, s.UnavailabilityReason)).ToList()),
            notice.CanObject);
    }

    public async Task<OwnerRecoveryObjectionResponse> ObjectAsync(RequestContext context, Guid caseId, OwnerRecoveryObjectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "an explanation of 10 to 500 characters is required");
        }

        try
        {
            var recorded = await authority.ObjectToOwnerRecoveryAsync(context.ToDelegatedUser(), new OwnerRecoveryObjectionCommand(context.OrganizationId, caseId, reason, request.UseDevelopmentStepUp ? SyntheticStepUpMarker : null),
                cancellationToken).ConfigureAwait(false);
            return new OwnerRecoveryObjectionResponse(recorded.CaseId, recorded.State, recorded.ObjectedOn);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode is 403 or 404 && ex.ErrorCode is "NOT_FOUND" or "ACTOR_NOT_BOUND")
        {
            throw new BusinessException("NOT_FOUND", 404, "no open recovery case");
        }
    }
}
