using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Sharing;

namespace EnterpriseForms.Application.Features.Sharing.UpdateShare;

/// <summary>
/// C10 M2 (WP-0 Sections 3.2, 3.3, 3.7): replaces a share's scope through the Authority's <c>UpdateShareScope</c> command. The
/// request is the complete new scope: role, field keys bound to the record's accepted content, and an explicit expiry decision.
/// The Authority owns the share revision, the scope, the named parent and the effective-permission caps; it compares the share
/// revision from the caller's <c>If-Match</c> atomically with the change (stale: 412) and the organization revision separately
/// (moved: 409, never retried under the same key). Step-up is carried only through <see cref="HumanCommandPayload"/> and is never
/// stored here. The local projection changes only after the command is Effective; it confers nothing.
/// </summary>
public sealed class UpdateShareHandler(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, TimeProvider clock, FormRecords.Projection.VerifiedRecordReader? reader = null)
{
    public const string StaleShareRevision = "STALE_SHARE_REVISION";
    public const string StaleAuthorizationRevision = "STALE_AUTHORIZATION_REVISION";

    public async Task<UpdateShareResponse> HandleAsync(RequestContext context, Guid formRecordId, Guid recordShareId, long expectedShareRevision, UpdateShareRequest request,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedShareRevision);
        var fieldKeys = request.FieldKeys ?? [];
        if (fieldKeys.Count + (request.Documents?.Count ?? 0) == 0 || fieldKeys.Any(string.IsNullOrWhiteSpace) ||
            request.RecipientRole is not (RecipientRoleNames.Reader or RecipientRoleNames.Contributor or RecipientRoleNames.Manager))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a valid role and at least one field key or document are required");
        }

        // The Authority replaces the expiry with what is sent (none means no expiry), so the caller must decide it explicitly:
        // an omitted expiry never silently removes an existing one.
        if ((request.ShareExpiresOn is null) == (request.NoExpiry != true))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "state the expiry explicitly: either share_expires_on or no_expiry = true");
        }

        var user = context.ToDelegatedUser();
        var decision = await authority.CheckPermissionAsync(user,
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.ManageAccess),
            cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            // Concealment-safe, as the share list: a caller who cannot manage access learns nothing about the record or its shares.
            throw new BusinessException("RECORD_NOT_FOUND", 404, "record not found");
        }

        var (_, items) = await ShareFieldItems.ResolveAsync(store, authority, ShareFieldItems.ReadableBy(reader, context, formRecordId), user, context.OrganizationId, formRecordId, fieldKeys, request.ParentRecordShareId, cancellationToken).ConfigureAwait(false);
        var commandFields = JsonSerializer.SerializeToNode(new
        {
            record_share_id = recordShareId,
            form_record_id = formRecordId,
            recipient_role = request.RecipientRole,
            field_item_ids = items.Select(i => i.ItemId).ToList(),
            expected_share_revision = expectedShareRevision,
        })!.AsObject();
        if (request.ParentRecordShareId is { } parent)
        {
            commandFields["parent_record_share_id"] = parent;
        }

        if (request.ShareExpiresOn is { } expiry)
        {
            commandFields["share_expires_on"] = expiry.ToUniversalTime();
        }

        if (ShareFieldItems.Documents(request.Documents) is { } documents)
        {
            commandFields["documents"] = documents;
        }

        var payload = HumanCommandPayload.Serialize(commandFields, request.UseDevelopmentStepUp == true);
        SecurityCommandResult command;
        try
        {
            command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.UpdateShareScope,
                new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), decision.AuthorizationRevision, payload, "update share scope"),
                "share-update:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == StaleShareRevision)
        {
            // Fresh or replayed under the key, a stale share revision is the PB-06 precondition failure.
            throw new BusinessException("PRECONDITION_FAILED", 412, "the share changed since the supplied ETag; reload it and retry");
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == StaleAuthorizationRevision)
        {
            // Organization-wide authorization moved: a conflict, stored under the key by the Authority, so not retried here.
            throw new BusinessException(StaleAuthorizationRevision, 409, "authorization changed while the update was prepared; retry with a new Idempotency-Key");
        }

        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (!outcome.Effective)
        {
            throw new PendingCommandException(outcome);
        }

        // The Authority advanced the share revision by exactly one in this command (its compare-and-set on the expected revision).
        var shareRevision = checked(expectedShareRevision + 1);
        await UpdateProjectionAsync(context, formRecordId, recordShareId, request, items, shareRevision, command.CommandId, cancellationToken).ConfigureAwait(false);
        return new UpdateShareResponse(recordShareId, formRecordId, request.RecipientRole, items.Select(i => i.ItemId).ToList(), shareRevision, "Active", outcome);
    }

    private async Task UpdateProjectionAsync(RequestContext context, Guid formRecordId, Guid recordShareId, UpdateShareRequest request,
        IReadOnlyList<(Guid FieldDefinitionId, Guid ItemId)> items, long shareRevision, Guid commandId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var share = await scope.FindShareProjectionAsync(context.OrganizationId, recordShareId, cancellationToken).ConfigureAwait(false);
        if (share is null || share.FormRecordId != formRecordId || share.ShareStatus != "Active" || share.ShareRevision >= shareRevision)
        {
            return; // no projection, another record's, no longer active, or already at least as new (a replayed update)
        }

        var existing = await scope.GetShareFieldsOfShareAsync(context.OrganizationId, recordShareId, cancellationToken).ConfigureAwait(false);
        var wanted = items.Select(i => i.ItemId).ToHashSet();
        var canEdit = request.RecipientRole != RecipientRoleNames.Reader;
        // Stage B (C-200): the business runtime role may never DELETE (G08 least privilege, BusinessRuntimeGrants), so a field the update
        // no longer shares is marked unshared (view and edit off) rather than deleted, and a field shared again is turned back on. The
        // projection confers nothing either way; the Authority's scope decides every access.
        foreach (var row in existing)
        {
            var shared = wanted.Contains(row.FieldValueItemId);
            row.CanViewField = shared;
            row.CanEditField = shared && canEdit;
        }

        var present = existing.Select(f => f.FieldValueItemId).ToHashSet();
        scope.AddShareFields(items.Where(i => !present.Contains(i.ItemId)).Select(i => new RecordShareField
        {
            RecordShareFieldId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            RecordShareId = recordShareId,
            FieldDefinitionId = i.FieldDefinitionId,
            FieldValueItemId = i.ItemId,
            CanViewField = true,
            CanEditField = canEdit,
        }).ToList());

        share.RecipientRole = request.RecipientRole;
        share.ShareRevision = shareRevision;
        share.AuthorityCommandId = commandId;
        Audit.AuditRecorder.Record(scope, context, clock, Audit.AuditCategories.Access, Domain.Features.Audit.AuditActions.RecordAccessGranted,
            "share scope updated at the authority barrier", formRecordId, targetEntityType: "record_share", targetEntityId: recordShareId,
            metadata: new { change = "scope_updated", role = request.RecipientRole, fieldKeys = request.FieldKeys, shareRevision, commandId });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The business recipient role names (the Authority's own constants are not referenced from the business).</summary>
internal static class RecipientRoleNames
{
    public const string Reader = "Reader";
    public const string Contributor = "Contributor";
    public const string Manager = "Manager";
}
