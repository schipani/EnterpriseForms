using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;

namespace EnterpriseForms.Application.Features.FormRecords.GetFormRecord;

/// <summary>Verified read returning the Stage A wire shape (used by the SEC tests) — see <see cref="VerifiedRecordReader"/>.</summary>
public sealed class GetFormRecordHandler(VerifiedRecordReader reader, IBusinessStore store, TimeProvider clock)
{
    public async Task<FormRecordResponse> HandleAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        await AuditRecorder.RecordAsync(store, context, clock, AuditCategories.Record, AuditActions.RecordViewed, "verified record read", formRecordId, "Success", new { complete = verified.IsComplete, contentRevision = verified.Head.CommittedContentRevision }, cancellationToken).ConfigureAwait(false);
        return verified.ToRecordResponse(context.OrganizationId);
    }
}

/// <summary>Verified read with layout, field metadata, authorized documents and permitted actions for the UI (PG-016).</summary>
public sealed class GetRecordProjectionHandler(VerifiedRecordReader reader, IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public async Task<RecordProjectionResponse> HandleAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var verified = await reader.ReadAsync(context, formRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        var user = context.ToDelegatedUser();
        var decisions = await authority.CheckPermissionsBatchAsync(user,
        [
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.Edit),
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.ManageAccess),
            new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, formRecordId), ProtectedAction.TransferOwnership),
        ], cancellationToken).ConfigureAwait(false);

        List<Domain.Features.RelatedForms.FormRelationshipDefinition> relationships;
        Dictionary<Guid, string> formNames;
        List<FileAttachment> attachments;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            relationships = await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, verified.Definition.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            var definitions = await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
            formNames = definitions.ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
            attachments = await scope.GetAttachmentsAsync(formRecordId, cancellationToken).ConfigureAwait(false);
            AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordViewed, "verified record projection read", formRecordId, metadata: new { complete = verified.IsComplete, contentRevision = verified.Head.CommittedContentRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Documents are released only when their version leaf is in the accepted manifest and authorized (never from local rows alone).
        var attachmentNames = attachments.ToDictionary(a => a.FileAttachmentId, a => a.AttachmentLifecycleState);
        var documents = verified.AuthorizedAttachments
            .OrderBy(a => a.UploadedOn)
            .Select(a => new DocumentSummaryResponse(a.FileAttachmentId, a.FieldDefinitionId, a.OriginalFileName, a.VersionNumber, a.DetectedContentType, a.PlaintextSizeBytes, a.ScanVerdict, attachmentNames.GetValueOrDefault(a.FileAttachmentId, "Ready"), a.UploadedOn, a.UploadedByUserId, true))
            .ToList();

        var options = verified.Options.GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.Select(o => new FieldOptionResponse(o.FieldOptionId, o.OptionKey, o.OptionDisplayText, o.OptionSortOrder, o.IsActive)).ToList());
        var fields = verified.Fields.Select(f => new FieldDefinitionResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDescription, f.FieldDataType, f.FieldClassification, f.LifecycleStatus, options.GetValueOrDefault(f.FieldDefinitionId, []))).ToList();
        var version = verified.LayoutVersion;
        return new RecordProjectionResponse(
            verified.ToRecordResponse(context.OrganizationId),
            verified.Definition.FormDefinitionName,
            new FormVersionResponse(version.FormVersionId, version.VersionNumber, version.LayoutSchemaVersion, version.LayoutJson, version.ChangeReason, version.RestoredFromFormVersionId, version.PublishedOn, version.PublishedByUserId),
            fields,
            documents,
            relationships.Select(r => new RelationshipDefinitionResponse(r.FormRelationshipDefinitionId, r.ParentFormDefinitionId, r.ChildFormDefinitionId, formNames.GetValueOrDefault(r.ChildFormDefinitionId, string.Empty), r.RelationshipDefinitionKey, r.RelationshipDisplayName, r.MinimumChildCount, r.MaximumChildCount, r.LifecycleStatus)).ToList(),
            new RecordPermissionsResponse(true, decisions[0].Allowed, decisions[1].Allowed, decisions[2].Allowed, verified.IsComplete),
            verified.Header.ParentFormRecordId,
            verified.Header.FormRelationshipDefinitionId,
            verified.ETag);
    }
}
