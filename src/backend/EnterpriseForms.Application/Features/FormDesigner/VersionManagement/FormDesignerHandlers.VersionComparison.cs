using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner;

/// <summary>
/// Phase C9 M2: publish impact (WP-4) and version-to-version comparison (WP-5) built on the semantic layout comparison
/// (WP-3, <see cref="FormLayoutComparison"/>). Read-only, Authority design permission on the form (404 otherwise), like
/// every designer read of drafts and versions. Comparisons hold form-design metadata only; record impact is the D9-4
/// approximate record-level count over records the caller may discover — never field values.
/// </summary>
public sealed partial class FormDesignerHandlers
{
    /// <summary>The word for the draft side of a comparison (<c>?from=draft</c> / <c>?to=draft</c>).</summary>
    public const string DraftReference = "draft";

    public const string PublishDataStatement =
        "Publishing creates a new immutable version. It never deletes or changes stored values: values of fields that are no longer in the layout are kept, and existing records are only checked against the new version when they are next edited with it.";

    /// <summary>The draft exactly as publishing it would store it (labels, protection and option snapshots taken now).</summary>
    private sealed record PublishSnapshot(FormLayout Layout, string Canonical, Dictionary<Guid, FieldDefinition> CatalogFields);

    private static async Task<PublishSnapshot> SnapshotForPublishAsync(IBusinessScope scope, RequestContext context, Guid formDefinitionId, FormLayout layout, CancellationToken cancellationToken)
    {
        var catalogFields = (await scope.GetFieldDefinitionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FieldDefinitionId);
        var catalogRelationships = (await scope.GetRelationshipDefinitionsAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.FormRelationshipDefinitionId);
        var catalogOptions = (await scope.GetFieldOptionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false)).GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.ToList());
        var snapshot = SnapshotLabels(layout, catalogFields, catalogRelationships, catalogOptions);
        return new PublishSnapshot(snapshot, FormLayoutCodec.Serialize(snapshot with { LayoutSchemaVersion = FormLayout.CurrentSchemaVersion }), catalogFields);
    }

    /// <summary>
    /// WP-4: what publishing the draft at <paramref name="expectedDraftRevision"/> would change — the semantic comparison of
    /// the current version with the draft as it would be published, notices about potential impact on future edits, and the
    /// approximate record-level counts. Nothing is written or audited. A stale revision is 409 DRAFT_REVISION_CONFLICT.
    /// </summary>
    public async Task<PublishImpactResponse> PreviewPublishImpactAsync(RequestContext context, Guid formDefinitionId, long expectedDraftRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        FormDefinition definition;
        FormVersion? currentVersion;
        PublishSnapshot snapshot;
        int nextNumber;
        long draftRevision;
        string? blockedCode = null;
        string? blocked = null;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            if (definition.LifecycleStatus == "Deleted")
            {
                throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            }

            var draft = await scope.FindDraftAsync(formDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NO_DRAFT", 409, "there is no draft to publish");
            if (draft.DraftRevision != expectedDraftRevision)
            {
                throw new BusinessException("DRAFT_REVISION_CONFLICT", 409, "draft revision is stale");
            }

            draftRevision = draft.DraftRevision;
            var versions = await scope.GetFormVersionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
            var currentNumber = versions.Count == 0 ? 0 : versions.Max(v => v.VersionNumber);
            currentVersion = versions.FirstOrDefault(v => v.VersionNumber == currentNumber);
            nextNumber = currentNumber + 1;
            var layout = FormLayoutCodec.Parse(draft.LayoutJson);
            try
            {
                await ValidateLayoutReferencesAsync(scope, context, formDefinitionId, layout, cancellationToken).ConfigureAwait(false);
                if (!layout.Elements.Any())
                {
                    throw new BusinessException("LAYOUT_INVALID", 422, "a published version needs at least one element");
                }
            }
            catch (BusinessException ex) when (ex.HttpStatusCode == 422)
            {
                blockedCode = ex.ErrorCode;
                blocked = ex.Message;
            }

            snapshot = await SnapshotForPublishAsync(scope, context, formDefinitionId, layout, cancellationToken).ConfigureAwait(false);
            if (blockedCode is null && currentVersion is not null && FormLayoutCodec.Serialize(FormLayoutCodec.Parse(currentVersion.LayoutJson)) == snapshot.Canonical)
            {
                blockedCode = "NO_DRAFT_CHANGES";
                blocked = "the draft is identical to the current published version; nothing to publish";
            }
        }

        var comparison = FormLayoutComparison.Compare(currentVersion is null ? null : FormLayoutCodec.Parse(currentVersion.LayoutJson), FormLayoutCodec.Parse(snapshot.Canonical));
        var records = await PublishRecordImpactAsync(context, definition, cancellationToken).ConfigureAwait(false);
        return new PublishImpactResponse(formDefinitionId, draftRevision, currentVersion?.VersionNumber, nextNumber, definition.VersionBehavior, blockedCode is null, blockedCode, blocked,
            ToResponse(comparison), Notices(comparison), records, PublishDataStatement);
    }

    /// <summary>
    /// WP-5: comparison of two published versions of the form, or of a published version and the draft (as it would be
    /// published). <paramref name="from"/> and <paramref name="to"/> are version ids or <see cref="DraftReference"/>. A version
    /// of another form, an unknown version or a missing draft is 404; malformed references are 422.
    /// </summary>
    public async Task<VersionComparisonResponse> CompareVersionsAsync(RequestContext context, Guid formDefinitionId, string? from, string? to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var fromReference = ParseReference(from, "from");
        var toReference = ParseReference(to, "to");
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.LifecycleStatus == "Deleted")
        {
            throw new BusinessException("NOT_FOUND", 404, "form definition not found");
        }

        var (fromLayout, fromSide) = await LoadSideAsync(scope, context, formDefinitionId, fromReference, cancellationToken).ConfigureAwait(false);
        var (toLayout, toSide) = await LoadSideAsync(scope, context, formDefinitionId, toReference, cancellationToken).ConfigureAwait(false);
        return new VersionComparisonResponse(formDefinitionId, fromSide, toSide, ToResponse(FormLayoutComparison.Compare(fromLayout, toLayout)));
    }

    private static Guid? ParseReference(string? reference, string name)
    {
        if (string.Equals(reference, DraftReference, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParse(reference, out var id) ? id : throw new BusinessException("INVALID_REQUEST", 422, $"{name} must be a version id or '{DraftReference}'") { FieldKeys = [name] };
    }

    private static async Task<(FormLayout Layout, ComparedLayoutResponse Side)> LoadSideAsync(IBusinessScope scope, RequestContext context, Guid formDefinitionId, Guid? formVersionId, CancellationToken cancellationToken)
    {
        if (formVersionId is { } id)
        {
            var version = await scope.FindFormVersionAsync(id, cancellationToken).ConfigureAwait(false);
            if (version is null || version.FormDefinitionId != formDefinitionId)
            {
                throw new BusinessException("VERSION_NOT_FOUND", 404, "version not found");
            }

            return (FormLayoutCodec.Parse(version.LayoutJson), new ComparedLayoutResponse("Version", version.FormVersionId, version.VersionNumber, null, version.PublishedOn));
        }

        var draft = await scope.FindDraftAsync(formDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NO_DRAFT", 404, "this form has no draft");
        var snapshot = await SnapshotForPublishAsync(scope, context, formDefinitionId, FormLayoutCodec.Parse(draft.LayoutJson), cancellationToken).ConfigureAwait(false);
        return (FormLayoutCodec.Parse(snapshot.Canonical), new ComparedLayoutResponse("Draft", null, null, draft.DraftRevision, null));
    }

    /// <summary>D9-4: visible records of the form and how many would be shown / edited with the new version under its behaviour.</summary>
    private async Task<PublishRecordImpactResponse> PublishRecordImpactAsync(RequestContext context, FormDefinition definition, CancellationToken cancellationToken)
    {
        var authorized = await AuthorizedRecordEnumerator.EnumerateAsync(authority, context.ToDelegatedUser(), context.OrganizationId, cancellationToken).ConfigureAwait(false);
        List<Domain.Features.FormRecords.FormRecord> records = [];
        if (authorized.FormRecordIds.Count > 0)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            records = await scope.GetFormRecordsAsync(context.OrganizationId, authorized.FormRecordIds, cancellationToken).ConfigureAwait(false);
        }

        var authorizedIds = authorized.FormRecordIds.ToHashSet();
        var described = records.Select(r => r.FormRecordId).ToHashSet();
        var ofForm = records.Where(r => r.FormDefinitionId == definition.FormDefinitionId && authorizedIds.Contains(r.FormRecordId)).ToList();
        // The version about to be published becomes the current version; a placeholder id stands for it.
        var next = Guid.CreateVersion7();
        var shown = ofForm.Count(r => FormVersionResolution.Resolve(definition.VersionBehavior, RecordLayoutPurpose.View, r.CreatedFormVersionId, next) == next);
        var edited = ofForm.Count(r => FormVersionResolution.Resolve(definition.VersionBehavior, RecordLayoutPurpose.Edit, r.CreatedFormVersionId, next) == next);
        return new PublishRecordImpactResponse(ofForm.Count, shown, edited, authorized.FormRecordIds.Count(id => !described.Contains(id)), authorized.AuthorizationRevision, true, VersionBehaviorImpactScopeStatement);
    }

    /// <summary>Potential-impact notices derived from the comparison (aspect codes), never from record data.</summary>
    public static IReadOnlyList<PublishImpactNoticeResponse> Notices(LayoutComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (!comparison.HasChanges)
        {
            return [new PublishImpactNoticeResponse("NO_CHANGES", "Information", [])];
        }

        var notices = new List<PublishImpactNoticeResponse>();
        var elements = comparison.Changes.Where(c => c.Target is not LayoutChangeTargets.Form and not LayoutChangeTargets.Section).ToList();
        void Add(string code, string severity, IEnumerable<string> labels)
        {
            var list = labels.Distinct(StringComparer.Ordinal).ToList();
            if (list.Count > 0)
            {
                notices.Add(new PublishImpactNoticeResponse(code, severity, list));
            }
        }

        Add("NEW_REQUIRED_FIELDS", "Attention", elements.Where(c => c.Details.Any(d => d.Tightened && d.Aspect is LayoutChangeAspects.Required or LayoutChangeAspects.RequiredWhen)).Select(c => c.Label));
        Add("VALIDATION_TIGHTENED", "Attention", elements.Where(c => c.Details.Any(d => d.Tightened && d.Aspect is LayoutChangeAspects.MinLength or LayoutChangeAspects.MaxLength or LayoutChangeAspects.Pattern or LayoutChangeAspects.Minimum or LayoutChangeAspects.Maximum or LayoutChangeAspects.MinDate or LayoutChangeAspects.MaxDate)).Select(c => c.Label));
        Add("OPTIONS_NO_LONGER_AVAILABLE", "Attention", elements.SelectMany(c => c.Details.Where(d => d.Aspect is LayoutChangeAspects.OptionDeactivated or LayoutChangeAspects.OptionRemoved).Select(d => c.Label + ": " + d.Subject)));
        Add("LIMITS_TIGHTENED", "Attention", comparison.Changes.Where(c => c.Details.Any(d => d.Tightened && d.Aspect is LayoutChangeAspects.UploadPolicy or LayoutChangeAspects.RepeatLimits)).Select(c => c.Label));
        Add("ELEMENTS_REMOVED", "Information", comparison.Changes.Where(c => c.ChangeType == LayoutChangeTypes.Removed && c.Target != LayoutChangeTargets.Section).Select(c => c.Label));
        Add("READ_ONLY_OR_HIDDEN", "Information", elements.Where(c => c.Details.Any(d => (d.Aspect == LayoutChangeAspects.ReadOnly && d.After == "yes") || (d.Aspect == LayoutChangeAspects.ShowInForm && d.After == "no"))).Select(c => c.Label));
        Add("PROTECTION_CHANGED", "Information", elements.Where(c => c.Details.Any(d => d.Aspect == LayoutChangeAspects.Protection)).Select(c => c.Label));
        Add("REPEATING_SECTIONS_CHANGED", "Information", comparison.Changes.Where(c => c.Target == LayoutChangeTargets.Section && c.ChangeType != LayoutChangeTypes.Added && c.Details.Any(d => d.Aspect is LayoutChangeAspects.Repeat or LayoutChangeAspects.RepeatGroup)).Select(c => c.Label));
        if (comparison.Changes.All(c => c.Significance == LayoutChangeSignificance.Presentation))
        {
            notices.Add(new PublishImpactNoticeResponse("PRESENTATION_ONLY", "Information", []));
        }

        return notices;
    }

    public static LayoutComparisonResponse ToResponse(LayoutComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return new LayoutComparisonResponse(comparison.AddedCount, comparison.RemovedCount, comparison.BehaviourChangeCount, comparison.PresentationChangeCount, comparison.HasChanges,
            comparison.Changes.Select(c => new LayoutChangeResponse(c.Target, c.ElementId, c.ChangeType, c.Significance, c.Label, c.SectionTitle,
                c.Details.Select(d => new LayoutChangeDetailResponse(d.Aspect, d.Significance, d.Before, d.After, d.Subject, d.Tightened)).ToList())).ToList());
    }
}
