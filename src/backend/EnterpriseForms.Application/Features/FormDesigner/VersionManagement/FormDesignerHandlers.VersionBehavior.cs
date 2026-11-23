using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner;

/// <summary>
/// Phase C9 M1 (WP-2): the form's version behaviour — which published layout records are viewed and edited with
/// (CLAUDE.md §3; D9-1, D9-2, D9-4, D9-7). Business-side form metadata, like the display name: the Authority's design
/// permission on the form is required (404 otherwise), the change is optimistic on <c>form_definition_revision</c> (409 when
/// stale; the Designer's revision-in-body convention, D9-3) and audited in the same transaction. No record, value,
/// published version, signed row or Authority state changes.
/// </summary>
public sealed partial class FormDesignerHandlers
{
    public const int VersionBehaviorReasonMinimumLength = 3;

    public const int VersionBehaviorReasonMaximumLength = 500;

    public const string VersionBehaviorImpactScopeStatement =
        "Counts cover only records you can discover, and are approximate: they come from listing details in the application database, not from verified record reads. Records you cannot discover are never counted, and no field values are inspected.";

    public async Task<FormDefinitionDetailResponse> ChangeVersionBehaviorAsync(RequestContext context, Guid formDefinitionId, ChangeVersionBehaviorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var target = RequireKnownBehavior(request.VersionBehavior);
        var reason = request.ChangeReason?.Trim() ?? string.Empty;
        if (reason.Length is < VersionBehaviorReasonMinimumLength or > VersionBehaviorReasonMaximumLength || reason.Any(char.IsControl))
        {
            throw new BusinessException("INVALID_REQUEST", 422, $"a change reason of {VersionBehaviorReasonMinimumLength}-{VersionBehaviorReasonMaximumLength} characters (no control characters) is required") { FieldKeys = ["changeReason"] };
        }

        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
            if (definition is null || definition.LifecycleStatus == "Deleted")
            {
                throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            }

            // Re-sending a change that already took effect returns the current form without a second change or audit event.
            if (!string.Equals(definition.VersionBehavior, target, StringComparison.Ordinal))
            {
                if (request.ExpectedFormDefinitionRevision != definition.FormDefinitionRevision)
                {
                    throw new BusinessException("FORM_DEFINITION_MODIFIED_CONCURRENTLY", 409, "the form's settings were changed by someone else; reload and retry");
                }

                var previous = definition.VersionBehavior;
                definition.VersionBehavior = target;
                definition.FormDefinitionRevision++;
                definition.ModifiedOn = clock.GetUtcNow();
                definition.ModifiedByUserId = context.LocalUserId;
                AuditRecorder.Record(scope, context, clock, AuditCategories.Form, AuditActions.FormVersionBehaviorChanged,
                    "form version behaviour changed (which published layout records are viewed and edited with; no record, value or version changed)", formDefinitionId: formDefinitionId,
                    metadata: new { previousVersionBehavior = previous, versionBehavior = target, formDefinitionRevision = definition.FormDefinitionRevision, currentFormVersionId = definition.CurrentFormVersionId, changeReason = reason });
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await GetAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Impact preview of switching to <paramref name="targetVersionBehavior"/> (D9-4): approximate record-level counts over
    /// the caller's complete discoverable set (Authority lookup at one authorization revision, fail closed above the listing
    /// limit). Counted: visible records of the form, those that would be shown or edited with a different layout, and the
    /// visible records per created version. Never counted: records the caller cannot discover, field values.
    /// </summary>
    public async Task<VersionBehaviorImpactResponse> PreviewVersionBehaviorImpactAsync(RequestContext context, Guid formDefinitionId, string? targetVersionBehavior, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = RequireKnownBehavior(targetVersionBehavior);
        await RequireDesignAsync(context, formDefinitionId, cancellationToken).ConfigureAwait(false);
        Domain.Features.FormDefinitions.FormDefinition definition;
        List<FormVersion> versions;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            definition = await scope.FindFormDefinitionAsync(context.OrganizationId, formDefinitionId, cancellationToken).ConfigureAwait(false) ?? throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            if (definition.LifecycleStatus == "Deleted")
            {
                throw new BusinessException("NOT_FOUND", 404, "form definition not found");
            }

            versions = await scope.GetFormVersionsAsync(formDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var authorized = await AuthorizedRecordEnumerator.EnumerateAsync(authority, context.ToDelegatedUser(), context.OrganizationId, cancellationToken).ConfigureAwait(false);
        List<Domain.Features.FormRecords.FormRecord> records = [];
        if (authorized.FormRecordIds.Count > 0)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            records = await scope.GetFormRecordsAsync(context.OrganizationId, authorized.FormRecordIds, cancellationToken).ConfigureAwait(false);
        }

        // Same boundary as My Records and the dashboard: an authorized id without business metadata is reported, a business row the Authority did not list is never counted.
        var authorizedIds = authorized.FormRecordIds.ToHashSet();
        var described = records.Select(r => r.FormRecordId).ToHashSet();
        var unavailable = authorized.FormRecordIds.Count(id => !described.Contains(id));
        var ofForm = records.Where(r => r.FormDefinitionId == formDefinitionId && authorizedIds.Contains(r.FormRecordId)).ToList();
        var current = definition.CurrentFormVersionId;
        var viewChanges = ofForm.Count(r => FormVersionResolution.Resolve(definition.VersionBehavior, RecordLayoutPurpose.View, r.CreatedFormVersionId, current) != FormVersionResolution.Resolve(target, RecordLayoutPurpose.View, r.CreatedFormVersionId, current));
        var editChanges = ofForm.Count(r => FormVersionResolution.Resolve(definition.VersionBehavior, RecordLayoutPurpose.Edit, r.CreatedFormVersionId, current) != FormVersionResolution.Resolve(target, RecordLayoutPurpose.Edit, r.CreatedFormVersionId, current));
        var numbers = versions.ToDictionary(v => v.FormVersionId, v => v.VersionNumber);
        var byVersion = ofForm
            .GroupBy(r => numbers.TryGetValue(r.CreatedFormVersionId, out var n) ? n : (int?)null)
            .OrderBy(g => g.Key ?? int.MaxValue)
            .Select(g => new VersionRecordCountResponse(g.Key, g.Count()))
            .ToList();
        int? currentNumber = current is { } c && numbers.TryGetValue(c, out var cn) ? cn : null;
        return new VersionBehaviorImpactResponse(formDefinitionId, definition.VersionBehavior, target, currentNumber, ofForm.Count, viewChanges, editChanges, unavailable, byVersion,
            definition.FormDefinitionRevision, authorized.AuthorizationRevision, CountsAreApproximate: true, VersionBehaviorImpactScopeStatement);
    }

    private static string RequireKnownBehavior(string? value) =>
        VersionBehaviors.IsKnown(value) ? value! : throw new BusinessException("INVALID_REQUEST", 422, "versionBehavior must be Historical, Latest or Hybrid") { FieldKeys = ["versionBehavior"] };
}
