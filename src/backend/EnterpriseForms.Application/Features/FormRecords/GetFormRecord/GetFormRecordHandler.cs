using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
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
        RecordProjectionSummaryResponse summary;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            relationships = await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, verified.Definition.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            var definitions = await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
            formNames = definitions.ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
            attachments = await scope.GetAttachmentsAsync(formRecordId, cancellationToken).ConfigureAwait(false);
            var row = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            var creator = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(u => u.UserId == verified.Header.CreatedByUserId);
            summary = new RecordProjectionSummaryResponse(ListRecords.RecordListQuery.ReferenceFor(formRecordId), row?.CreatedOn, row?.ModifiedOn, creator?.DisplayName, verified.Header.CreatedByUserId == context.LocalUserId);
            AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordViewed, "verified record projection read", formRecordId, metadata: new { complete = verified.IsComplete, contentRevision = verified.Head.CommittedContentRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Documents are released only when their version leaf is in the accepted manifest and authorized (never from local rows alone).
        var attachmentNames = attachments.ToDictionary(a => a.FileAttachmentId, a => a.AttachmentLifecycleState);
        var documents = verified.AuthorizedAttachments
            .OrderBy(a => a.UploadedOn)
            .Select(a => new DocumentSummaryResponse(a.FileAttachmentId, a.FieldDefinitionId, a.OriginalFileName, a.VersionNumber, a.DetectedContentType, a.PlaintextSizeBytes, a.ScanVerdict, attachmentNames.GetValueOrDefault(a.FileAttachmentId, "Ready"), a.UploadedOn, a.UploadedByUserId, true))
            .ToList();

        // Authorized projection (CLAUDE.md §4H): a partial (share-scoped) read serializes only the field definitions,
        // layout elements, sections and relationship metadata the caller may see. Labels of unshared fields, document
        // collections and child lists are removed server-side, never merely hidden by the renderer.
        var authorizedFieldIds = verified.IsComplete ? null : verified.AuthorizedValues.Select(v => v.FieldDefinitionId).ToHashSet();
        var authorizedDocumentFieldIds = verified.IsComplete ? null : verified.AuthorizedAttachments.Select(a => a.FieldDefinitionId).ToHashSet();
        var layout = verified.IsComplete ? verified.Layout : AuthorizedLayout(verified.Layout, authorizedFieldIds!, authorizedDocumentFieldIds!);
        var visibleRelationshipIds = layout.Elements.Where(e => e.Kind == LayoutElementKinds.RelatedFormList && e.FormRelationshipDefinitionId is { } id).Select(e => e.FormRelationshipDefinitionId!.Value).ToHashSet();
        var options = verified.Options.GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.Select(o => new FieldOptionResponse(o.FieldOptionId, o.OptionKey, o.OptionDisplayText, o.OptionSortOrder, o.IsActive)).ToList());
        var fields = verified.Fields
            .Where(f => authorizedFieldIds is null || authorizedFieldIds.Contains(f.FieldDefinitionId) || authorizedDocumentFieldIds!.Contains(f.FieldDefinitionId))
            .Select(f => new FieldDefinitionResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDescription, f.FieldDataType, f.FieldClassification, f.LifecycleStatus, options.GetValueOrDefault(f.FieldDefinitionId, []), f.FieldDefinitionRevision, f.FieldProtectionMode, f.ProtectionPolicyRevision, f.OptionSelectionMode, GroupDefinitionId: f.GroupDefinitionId))
            .ToList();
        // Phase C5: per-field edit eligibility for a share editor (never for owners; reasons never describe unreadable fields).
        List<SharedFieldAccessResponse>? sharedAccess = null;
        if (!verified.IsComplete)
        {
            var editableItems = (verified.Verification.EditableItems ?? []).Select(i => i.ItemId).ToHashSet();
            sharedAccess = SharedEdits.SharedFieldEligibility.Compute(verified.Layout, verified.FieldsById, SharedEdits.SharedFieldWriteBuilder.ReadableRows(verified), editableItems).Values
                .OrderBy(a => a.Field.FieldKey, StringComparer.Ordinal)
                .Select(a => new SharedFieldAccessResponse(a.Field.FieldKey, a.Editable && verified.Verification.ContributorWritesEnabled, a.Editable && !verified.Verification.ContributorWritesEnabled ? "ContributorWritesDisabled" : a.NotEditableReason))
                .ToList();
        }

        var version = verified.LayoutVersion;
        return new RecordProjectionResponse(
            verified.ToRecordResponse(context.OrganizationId),
            verified.Definition.FormDefinitionName,
            new FormVersionResponse(version.FormVersionId, version.VersionNumber, version.LayoutSchemaVersion, verified.IsComplete ? version.LayoutJson : FormLayoutCodec.Serialize(layout), version.ChangeReason, version.RestoredFromFormVersionId, version.PublishedOn, version.PublishedByUserId),
            fields,
            documents,
            relationships.Where(r => verified.IsComplete || visibleRelationshipIds.Contains(r.FormRelationshipDefinitionId)).Select(r => new RelationshipDefinitionResponse(r.FormRelationshipDefinitionId, r.ParentFormDefinitionId, r.ChildFormDefinitionId, formNames.GetValueOrDefault(r.ChildFormDefinitionId, string.Empty), r.RelationshipDefinitionKey, r.RelationshipDisplayName, r.MinimumChildCount, r.MaximumChildCount, r.LifecycleStatus)).ToList(),
            new RecordPermissionsResponse(true, decisions[0].Allowed, decisions[1].Allowed, decisions[2].Allowed, verified.IsComplete, sharedAccess?.Any(a => a.Editable) == true && Lifecycle.RecordLifecycle.IsContentEditable(verified.Header.RecordStatus), verified.Verification.ContributorWritesEnabled),
            verified.Header.ParentFormRecordId,
            verified.Header.FormRelationshipDefinitionId,
            verified.ETag,
            summary,
            sharedAccess,
            // Phase C6 M1: status from the Authority-verified signed header (also for a partial projection); transitions only for the owner.
            new RecordLifecycleStateResponse(verified.Header.RecordStatus, verified.Header.SubmittedOn, verified.IsComplete ? Lifecycle.RecordLifecycle.AllowedFor(verified.Header.RecordStatus) : [], Lifecycle.RecordLifecycle.IsContentEditable(verified.Header.RecordStatus)));
    }

    /// <summary>
    /// Keeps field elements the caller may read, document collections with at least one authorized version, no child lists
    /// (a parent share never covers children, §4S.4), and drops sections left empty. Conditional rules are removed from every
    /// remaining element: a rule names fields (and comparison values) the viewer may not be allowed to see, and a partial
    /// projection never evaluates rules, so shipping them would only disclose layout structure. Dropdown dependencies are
    /// removed for the same reason (they name the parent field), and repeating sections are dropped entirely: their entries
    /// are not part of any share (C-17).
    /// </summary>
    public static FormLayout AuthorizedLayout(FormLayout layout, IReadOnlySet<Guid> authorizedFieldIds, IReadOnlySet<Guid> authorizedDocumentFieldIds)
    {
        var sections = layout.Sections
            .Where(section => section.Repeat is null)
            .Select(section => section with
            {
                Elements = section.Elements.Where(e => e.Kind switch
                {
                    LayoutElementKinds.Field => e.FieldDefinitionId is { } id && authorizedFieldIds.Contains(id),
                    LayoutElementKinds.DocumentCollection => e.FieldDefinitionId is { } id && authorizedDocumentFieldIds.Contains(id),
                    _ => false,
                }).Select(e => e with { VisibleWhen = null, RequiredWhen = null, ClearWhenHidden = false, OptionFilter = null }).ToList(),
            })
            .Where(section => section.Elements.Count > 0)
            .ToList();
        return layout with { Sections = sections };
    }
}
