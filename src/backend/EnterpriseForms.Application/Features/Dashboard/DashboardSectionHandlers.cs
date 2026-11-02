using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Paging;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Application.Features.FormRecords.ListRecords;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Documents;

namespace EnterpriseForms.Application.Features.Dashboard;

/// <summary>
/// Dashboard analytics and sections (PG-006 redesign). Each section is its own request so one failure never blocks the
/// others. Every count is computed over data the caller may already list: records from the Authority's complete discover
/// set (the My Records boundary), uploads and activity from the caller's own rows, forms from the Authority's per-form
/// design/create decisions. Lists are paged on the server with opaque cursors (default 15 rows), newest first with a
/// unique tie-breaker so pages never repeat or skip rows of an unchanged list.
/// </summary>
public sealed class DashboardSectionHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, IOpaqueCursorProtector cursors, FormDesignerHandlers forms, TimeProvider clock)
{
    public const string RecordScopeStatement = "Counts cover every top-level record you can discover. Status and dates are listing details from the application database; opening a record verifies it.";

    public static readonly IReadOnlyList<string> UploadStatusGroups = ["InProgress", "Completed", "Failed", "Cancelled"];

    public static readonly IReadOnlyList<string> FormAccessFilters = ["create", "design"];

    public static readonly IReadOnlyList<string> FormStatusFilters = ["published", "unpublished"];

    public static string UploadGroupOf(string sessionState) => sessionState switch
    {
        UploadSessionStates.Committed => "Completed",
        UploadSessionStates.Rejected or UploadSessionStates.Failed => "Failed",
        UploadSessionStates.Cancelled or UploadSessionStates.Expired => "Cancelled",
        _ => "InProgress",
    };

    public static IReadOnlyList<string> StatesOfGroup(string? group) => group switch
    {
        null => [UploadSessionStates.Open, UploadSessionStates.Completed, UploadSessionStates.Processing, UploadSessionStates.Processed, UploadSessionStates.Committed, UploadSessionStates.Rejected, UploadSessionStates.Failed, UploadSessionStates.Cancelled, UploadSessionStates.Expired],
        "InProgress" => [UploadSessionStates.Open, UploadSessionStates.Completed, UploadSessionStates.Processing, UploadSessionStates.Processed],
        "Completed" => [UploadSessionStates.Committed],
        "Failed" => [UploadSessionStates.Rejected, UploadSessionStates.Failed],
        "Cancelled" => [UploadSessionStates.Cancelled, UploadSessionStates.Expired],
        _ => throw new BusinessException("INVALID_REQUEST", 422, "unknown upload status"),
    };

    public async Task<DashboardRecordStatisticsResponse> RecordStatisticsAsync(RequestContext context, int? periodDays, string? timeZoneId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = TimeBucketPlan.Create(clock.GetUtcNow(), periodDays, timeZoneId);
        var authorized = await AuthorizedRecordEnumerator.EnumerateAsync(authority, context.ToDelegatedUser(), context.OrganizationId, cancellationToken).ConfigureAwait(false);
        List<Domain.Features.FormRecords.FormRecord> records = [];
        if (authorized.FormRecordIds.Count > 0)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            records = await scope.GetFormRecordsAsync(context.OrganizationId, authorized.FormRecordIds, cancellationToken).ConfigureAwait(false);
        }

        // Same boundary as My Records: an authorized id without business metadata is reported, a business row the Authority did not list is never counted.
        var described = records.Select(r => r.FormRecordId).ToHashSet();
        var unavailable = authorized.FormRecordIds.Count(id => !described.Contains(id));
        var topLevel = records.Where(r => r.ParentFormRecordId is null).ToList();
        var created = new int[plan.BucketCount];
        var submitted = new int[plan.BucketCount];
        foreach (var record in topLevel)
        {
            if (plan.IndexOf(record.CreatedOn) is { } c)
            {
                created[c]++;
            }

            if (record.SubmittedOn is { } on && plan.IndexOf(on) is { } s)
            {
                submitted[s]++;
            }
        }

        var draft = topLevel.Count(r => r.RecordStatus == "Draft");
        var sent = topLevel.Count(r => r.RecordStatus == "Submitted");
        var archived = topLevel.Count(r => r.RecordStatus == "Archived");
        return new DashboardRecordStatisticsResponse(topLevel.Count, draft, sent, archived, topLevel.Count - draft - sent - archived, unavailable, plan.PeriodDays, plan.TimeZoneId,
            Enumerable.Range(0, plan.BucketCount).Select(i => new RecordActivityBucketResponse(plan.BucketStartDate(i), plan.BucketDayCount, created[i], submitted[i])).ToList(),
            authorized.AuthorizationRevision, RecordScopeStatement);
    }

    public async Task<DashboardUploadStatisticsResponse> UploadStatisticsAsync(RequestContext context, int? periodDays, string? timeZoneId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = TimeBucketPlan.Create(clock.GetUtcNow(), periodDays, timeZoneId);
        List<UploadSessionStatistic> sessions;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions = await scope.GetUploadSessionStatisticsAsync(context.OrganizationId, context.LocalUserId, plan.StartInstant, cancellationToken).ConfigureAwait(false);
        }

        var buckets = Enumerable.Range(0, plan.BucketCount).Select(_ => new int[4]).ToArray();
        var totals = new int[4];
        foreach (var session in sessions)
        {
            if (plan.IndexOf(session.SessionCreatedOn) is not { } index)
            {
                continue;
            }

            var group = UploadStatusGroups.ToList().IndexOf(UploadGroupOf(session.SessionState));
            buckets[index][group]++;
            totals[group]++;
        }

        return new DashboardUploadStatisticsResponse(plan.PeriodDays, plan.TimeZoneId, totals.Sum(), totals[0], totals[1], totals[2], totals[3],
            Enumerable.Range(0, plan.BucketCount).Select(i => new UploadActivityBucketResponse(plan.BucketStartDate(i), plan.BucketDayCount, buckets[i][0], buckets[i][1], buckets[i][2], buckets[i][3])).ToList());
    }

    public async Task<DashboardUploadPageResponse> UploadsAsync(RequestContext context, string? search, string? statusGroup, DateTimeOffset? from, DateTimeOffset? before, int? pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = NormalizeText(search, 120);
        var group = string.IsNullOrWhiteSpace(statusGroup) ? null : statusGroup.Trim();
        var states = StatesOfGroup(group);
        RequireRange(from, before);
        var size = OffsetPageCursor.ClampPageSize(pageSize);
        var digest = OffsetPageCursor.Digest("dashboard-uploads", text, group, from, before, size);
        var offset = OffsetPageCursor.DecodeOffset(cursors, continuationToken, context, digest);
        StorePage<UploadSession> page;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            page = await scope.QueryUploadSessionsForUserAsync(context.OrganizationId, context.LocalUserId, new UploadSessionFilter(states, text, from, before), offset, size, cancellationToken).ConfigureAwait(false);
        }

        var items = page.Items.Select(s => new DashboardUploadItemResponse(s.UploadSessionId, s.FormRecordId, s.OriginalFileName, s.SessionState, UploadGroupOf(s.SessionState), s.SessionCreatedOn, s.DeclaredSizeBytes, s.FailureReasonCode)).ToList();
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, size, page.TotalCount);
        return new DashboardUploadPageResponse(items, page.TotalCount, page.TotalCount == 0 ? 0 : offset + 1, size, next, previous);
    }

    public async Task<DashboardActivityPageResponse> ActivityAsync(RequestContext context, string? category, string? outcome, DateTimeOffset? from, DateTimeOffset? before, int? pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var categoryFilter = NormalizeText(category, 40);
        var outcomeFilter = NormalizeText(outcome, 40);
        RequireRange(from, before);
        var size = OffsetPageCursor.ClampPageSize(pageSize);
        var digest = OffsetPageCursor.Digest("dashboard-activity", categoryFilter, outcomeFilter, from, before, size);
        var offset = OffsetPageCursor.DecodeOffset(cursors, continuationToken, context, digest);
        StorePage<Domain.Features.Audit.AuditEvent> page;
        List<string> categories;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            page = await scope.QueryActorAuditEventsAsync(context.OrganizationId, context.LocalUserId, new ActorAuditEventFilter(categoryFilter, outcomeFilter, from, before), offset, size, cancellationToken).ConfigureAwait(false);
            categories = await scope.GetActorAuditCategoriesAsync(context.OrganizationId, context.LocalUserId, cancellationToken).ConfigureAwait(false);
        }

        var items = page.Items.Select(e => new AuditEventResponse(e.AuditEventId, e.EventOccurredOn, e.EventCategory, e.EventAction, e.EventOutcome, e.ActorUserId, context.DisplayName, e.EventDescription, e.CorrelationIdentifier, e.FileAttachmentId, e.TargetEntityId)).ToList();
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, size, page.TotalCount);
        return new DashboardActivityPageResponse(items, page.TotalCount, page.TotalCount == 0 ? 0 : offset + 1, size, next, previous, categories);
    }

    public async Task<DashboardFormPageResponse> FormsAsync(RequestContext context, string? search, string? access, string? status, int? pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = NormalizeText(search, 120);
        var accessFilter = Allowed(access, FormAccessFilters, "form access filter");
        var statusFilter = Allowed(status, FormStatusFilters, "form status filter");
        var size = OffsetPageCursor.ClampPageSize(pageSize);
        var digest = OffsetPageCursor.Digest("dashboard-forms", text, accessFilter, statusFilter, size);
        var offset = OffsetPageCursor.DecodeOffset(cursors, continuationToken, context, digest);

        // The complete catalog decided by the Authority at one revision (forms the caller may neither design nor use are never listed).
        var permitted = await forms.ListAsync(context, cancellationToken).ConfigureAwait(false);
        var matching = permitted
            .Where(f => text is null || f.FormDefinitionName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(f => accessFilter switch { "create" => f.CanCreateRecord, "design" => f.CanDesign, _ => true })
            .Where(f => statusFilter switch { "published" => f.CurrentVersionNumber is not null, "unpublished" => f.CurrentVersionNumber is null, _ => true })
            .OrderBy(f => f.FormDefinitionName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.FormDefinitionId)
            .ToList();
        offset = Math.Min(offset, matching.Count);
        var items = matching.Skip(offset).Take(size).ToList();
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, size, matching.Count);
        return new DashboardFormPageResponse(items, matching.Count, matching.Count == 0 ? 0 : offset + 1, size, next, previous);
    }

    private static string? NormalizeText(string? text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= maximumLength ? trimmed : throw new BusinessException("INVALID_REQUEST", 422, "search text is too long");
    }

    private static string? Allowed(string? value, IReadOnlyList<string> allowed, string name) =>
        string.IsNullOrWhiteSpace(value) ? null : allowed.Contains(value.Trim()) ? value.Trim() : throw new BusinessException("INVALID_REQUEST", 422, "unknown " + name);

    private static void RequireRange(DateTimeOffset? from, DateTimeOffset? before)
    {
        if (from is { } f && before is { } b && b <= f)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "the end date must be after the start date");
        }
    }
}
