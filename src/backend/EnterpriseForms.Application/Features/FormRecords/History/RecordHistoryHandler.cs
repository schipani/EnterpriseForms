using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.FormRecords.History;

/// <summary>
/// Phase C6 M2 — trusted record history (architecture §3, AC-1).
/// <list type="bullet">
/// <item><b>Which versions exist</b>, their revision, accepted time, actor and the protection-migration attribution come only
/// from the Authorization Authority (AC-1), which lists them only to a person who may read the whole record now.</item>
/// <item><b>Opening a version</b> resolves its operation from AC-1 (never from business rows) and verifies the exact rows of
/// that operation against the manifest the Authority accepted for it; anything missing, extra or altered fails closed and,
/// as today (AC-4), the Authority quarantines the record.</item>
/// <item><b>Action labels</b> are established by the Authority only for the first revision (created) and protection migrations;
/// otherwise they come from the local activity log (an unverified projection) and are marked as such, and only when the logged
/// actor matches the Authority's actor. AC-3 (Authority-recorded intent) is deferred.</item>
/// </list>
/// Field-share readers, contributors, revoked users and former owners get the same 404 as for a missing record (V1: history
/// needs full read now, so a value someone may no longer read never reaches them through history).
/// </summary>
public sealed class RecordHistoryHandler(VerifiedRecordReader reader, IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 100;

    /// <summary>Upper bound of local activity events read to label one page of versions (labels are optional; missing means "not recorded").</summary>
    public const int MaximumAuditEventsForLabels = 2_000;

    public static class ActionSources
    {
        public const string Authority = "Authority";
        public const string ActivityLog = "ActivityLog";
        public const string None = "None";
    }

    public async Task<RecordVersionListResponse> ListAsync(RequestContext context, Guid formRecordId, long? afterRevision, int? pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (afterRevision is < 0)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "after must be zero or a positive version number");
        }

        var page = await authority.GetContentHistoryAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, afterRevision, Math.Clamp(pageSize ?? DefaultPageSize, 1, MaximumPageSize), cancellationToken).ConfigureAwait(false);
        var (names, labels) = await DescribeAsync(context, formRecordId, cancellationToken).ConfigureAwait(false);
        return new RecordVersionListResponse(formRecordId, page.CurrentContentRevision, page.Entries.Select(e => ToItem(e, names, labels)).ToList(), page.NextAfterRevision, page.AuthorizationRevision);
    }

    public async Task<RecordVersionViewResponse> ViewAsync(RequestContext context, Guid formRecordId, long contentRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (contentRevision < 1)
        {
            throw new BusinessException("VERSION_NOT_FOUND", 404, "this version does not exist");
        }

        // The operation of the requested revision comes from the Authority's accepted history (which also re-checks full read).
        var page = await authority.GetContentHistoryAsync(context.ToDelegatedUser(), context.OrganizationId, formRecordId, contentRevision - 1, 1, cancellationToken).ConfigureAwait(false);
        var entry = page.Entries.FirstOrDefault(e => e.ContentRevision == contentRevision)
                    ?? throw new BusinessException("VERSION_NOT_FOUND", 404, "this version does not exist");
        if (!entry.VerifiableNow)
        {
            throw new BusinessException("HISTORIC_VERSION_UNVERIFIABLE", 503, "this version can't be verified with the keys that are accepted now");
        }

        var verified = await reader.ReadHistoricAsync(context, formRecordId, entry.CommittedOperationId, cancellationToken).ConfigureAwait(false);
        if (!verified.IsComplete || verified.Verification.CommittedContentRevision != entry.ContentRevision || verified.Verification.CommittedOperationId != entry.CommittedOperationId)
        {
            // Defensive: the Authority verified something other than the revision AC-1 named. Release nothing.
            throw new BusinessException("INTEGRITY_VERIFICATION_FAILED", 423, "the verified revision does not match the requested version");
        }

        var (names, labels) = await DescribeAsync(context, formRecordId, cancellationToken).ConfigureAwait(false);
        RecordProjectionSummaryResponse summary;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await scope.FindFormRecordAsync(context.OrganizationId, formRecordId, cancellationToken).ConfigureAwait(false);
            summary = new RecordProjectionSummaryResponse(RecordListQuery.ReferenceFor(formRecordId), row?.CreatedOn, row?.ModifiedOn, names.GetValueOrDefault(verified.Header.CreatedByUserId), verified.Header.CreatedByUserId == context.LocalUserId);
            AuditRecorder.Record(scope, context, clock, AuditCategories.Record, AuditActions.RecordVersionViewed, "earlier record version opened (verified, read-only)", formRecordId, verified.Definition.FormDefinitionId,
                metadata: new { viewedContentRevision = entry.ContentRevision, operationId = entry.CommittedOperationId, currentContentRevision = page.CurrentContentRevision });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Values are formatted (and protected values decrypted) only now, after verification and for a full reader; a key that is
        // unavailable fails the whole request rather than showing part of the version.
        var record = verified.ToRecordResponse(context.OrganizationId);
        var options = verified.Options.GroupBy(o => o.FieldDefinitionId).ToDictionary(g => g.Key, g => g.Select(o => new FieldOptionResponse(o.FieldOptionId, o.OptionKey, o.OptionDisplayText, o.OptionSortOrder, o.IsActive)).ToList());
        var fields = verified.Fields
            .Select(f => new FieldDefinitionResponse(f.FieldDefinitionId, f.FieldKey, f.FieldDisplayName, f.FieldDescription, f.FieldDataType, f.FieldClassification, f.LifecycleStatus, options.GetValueOrDefault(f.FieldDefinitionId, []), f.FieldDefinitionRevision, f.FieldProtectionMode, f.ProtectionPolicyRevision, f.OptionSelectionMode, GroupDefinitionId: f.GroupDefinitionId))
            .ToList();
        var documents = verified.AuthorizedAttachments
            .OrderBy(a => a.UploadedOn)
            .Select(a => new DocumentSummaryResponse(a.FileAttachmentId, a.FieldDefinitionId, a.OriginalFileName, a.VersionNumber, a.DetectedContentType, a.PlaintextSizeBytes, a.ScanVerdict, "Ready", a.UploadedOn, a.UploadedByUserId, true))
            .ToList();
        var version = verified.LayoutVersion;
        var projection = new RecordProjectionResponse(
            record,
            verified.Definition.FormDefinitionName,
            new FormVersionResponse(version.FormVersionId, version.VersionNumber, version.LayoutSchemaVersion, version.LayoutJson, version.ChangeReason, version.RestoredFromFormVersionId, version.PublishedOn, version.PublishedByUserId),
            fields,
            documents,
            [], // related (child) records have their own versions and are not part of this one
            new RecordPermissionsResponse(true, false, false, false, true),
            verified.Header.ParentFormRecordId,
            verified.Header.FormRelationshipDefinitionId,
            string.Empty, // read-only: no edit precondition is ever issued for an earlier version
            summary,
            null,
            new RecordLifecycleStateResponse(verified.Header.RecordStatus, verified.Header.SubmittedOn, [], false));
        return new RecordVersionViewResponse(ToItem(entry, names, labels), projection, page.CurrentContentRevision);
    }

    private async Task<(Dictionary<Guid, string> Names, Dictionary<long, (string Action, Guid? Actor, string Description)> Labels)> DescribeAsync(RequestContext context, Guid formRecordId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var events = await scope.GetRecordAuditEventsAsync(context.OrganizationId, formRecordId, MaximumAuditEventsForLabels, cancellationToken).ConfigureAwait(false);
        var labels = new Dictionary<long, (string, Guid?, string)>();
        foreach (var audit in events.Where(e => e.FormRecordId == formRecordId && string.Equals(e.EventOutcome, "Success", StringComparison.Ordinal) && LocalLabels.ContainsKey(e.EventAction)))
        {
            if (ContentRevisionOf(audit.EventMetadataJson) is { } revision)
            {
                labels.TryAdd(revision, (audit.EventAction, audit.ActorUserId, audit.EventDescription));
            }
        }

        return (names, labels);
    }

    /// <summary>Local activity actions that change content, with the label shown (marked as coming from the activity log).</summary>
    public static readonly IReadOnlyDictionary<string, string> LocalLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AuditActions.RecordUpdated] = "Edited",
        [AuditActions.RecordSubmitted] = "Submitted",
        [AuditActions.RecordReopened] = "Reopened",
        [AuditActions.RecordArchived] = "Archived",
        [AuditActions.RecordUnarchived] = "Unarchived",
        [AuditActions.DocumentReady] = "Document added",
        [AuditActions.DocumentRemoved] = "Document removed",
    };

    /// <summary>
    /// Builds one version row. Actor, time, revision and current marker are Authority facts. The action is an Authority fact
    /// for the first revision ("Created") and a protection migration; otherwise it is the local activity label for the same
    /// revision, used only when the logged actor is the Authority's actor, and reported with source <c>ActivityLog</c>.
    /// </summary>
    public static RecordVersionItemResponse ToItem(ContentHistoryEntry entry, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<long, (string Action, Guid? Actor, string Description)> labels)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(labels);
        string? label = null;
        var source = ActionSources.None;
        if (entry.ActorKind == "ProtectionMigration")
        {
            (label, source) = ("Protection update (system)", ActionSources.Authority);
        }
        else if (entry.ContentRevision == 1)
        {
            (label, source) = ("Created", ActionSources.Authority);
        }
        else if (entry.ActorKind == "User" && labels.TryGetValue(entry.ContentRevision, out var local) && local.Actor == entry.ActorAuthorityUserId)
        {
            label = local.Action == AuditActions.RecordUpdated && string.Equals(local.Description, UpdateFormRecord.UpdateFormRecordHandler.SharedEditAuditDescription, StringComparison.Ordinal)
                ? "Shared fields edited"
                : LocalLabels[local.Action];
            source = ActionSources.ActivityLog;
        }

        var actorName = entry.ActorKind switch
        {
            "User" when entry.ActorAuthorityUserId is { } id => names.GetValueOrDefault(id, "Unknown user"),
            "ProtectionMigration" => "System (field protection update)",
            _ => null,
        };
        return new RecordVersionItemResponse(entry.ContentRevision, entry.AcceptedOn, entry.ActorKind, entry.ActorKind == "User" ? entry.ActorAuthorityUserId : null, actorName, entry.IsCurrent, entry.VerifiableNow, label, source);
    }

    private static long? ContentRevisionOf(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(metadataJson) ? "{}" : metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("contentRevision", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision)
                ? revision
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
