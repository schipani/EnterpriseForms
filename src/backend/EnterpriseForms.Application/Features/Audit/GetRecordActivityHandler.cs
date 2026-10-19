using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Audit;

/// <summary>Record activity timeline (PG-025): authority Read permission is required; events are the local projection, labelled as such.</summary>
public sealed class GetRecordActivityHandler(IBusinessStore store, IAuthorizationAuthorityClient authority)
{
    public const string TrustNote = "Local audit projection (ADR-SEC-2026-10-16-01). The independent Authorization Authority journal is the trusted security evidence; command ids in event metadata reference it.";

    public async Task<RecordActivityResponse> HandleAsync(RequestContext context, Guid formRecordId, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.Read), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "record not found");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var events = await scope.GetRecordAuditEventsAsync(context.OrganizationId, formRecordId, Math.Clamp(limit, 1, 500), cancellationToken).ConfigureAwait(false);
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        return new RecordActivityResponse(formRecordId,
            events.Select(e => new AuditEventResponse(e.AuditEventId, e.EventOccurredOn, e.EventCategory, e.EventAction, e.EventOutcome, e.ActorUserId, e.ActorUserId is { } a ? users.GetValueOrDefault(a) : null, e.EventDescription, e.CorrelationIdentifier, e.FileAttachmentId, e.TargetEntityId)).ToList(),
            TrustNote);
    }

    public async Task<IReadOnlyList<AuditEventResponse>> MyActivityAsync(RequestContext context, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var events = await scope.GetActorAuditEventsAsync(context.OrganizationId, context.LocalUserId, Math.Clamp(limit, 1, 100), cancellationToken).ConfigureAwait(false);
        return events.Select(e => new AuditEventResponse(e.AuditEventId, e.EventOccurredOn, e.EventCategory, e.EventAction, e.EventOutcome, e.ActorUserId, context.DisplayName, e.EventDescription, e.CorrelationIdentifier, e.FileAttachmentId, e.TargetEntityId)).ToList();
    }
}
