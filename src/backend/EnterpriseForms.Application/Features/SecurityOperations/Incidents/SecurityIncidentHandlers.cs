using System.Globalization;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Paging;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.SecurityOperations;

namespace EnterpriseForms.Application.Features.SecurityOperations.Incidents;

/// <summary>Validated incident-list query (dashboard section and PG-056).</summary>
public sealed record SecurityIncidentListQuery(string? Search, string? IncidentTypeName, string? IncidentSeverityName, string? IncidentStatusName, DateTimeOffset? DetectedFrom, DateTimeOffset? DetectedBefore, int PageSize);

/// <summary>
/// Security monitoring (PG-056). Every operation requires the Authority's <c>administer_security</c> on the organization
/// (the Security Administrator role, organization relation <c>system_administrator</c>) — the existing permission, unchanged.
/// Organization owners, designers and members get 403 and no counts. Record identifiers are disclosed only for records
/// the viewer may discover; nothing here reads, verifies or releases record content, and no status change affects record
/// visibility (the Authority's quarantine is untouched). The store is business PostgreSQL (untrusted projection class).
/// </summary>
public sealed partial class SecurityIncidentHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, ISecurityAdministratorAuthority roster, IOpaqueCursorProtector cursors, TimeProvider clock)
{
    public const string TargetEntityType = "security_incident";

    public const string CoverageStatement =
        "Incidents are recorded when the application observes a verification problem while serving a request. They are not a scan: no incidents does not mean every record was verified or that the system is secure. The independent evidence of a confirmed mismatch is the Authorization Authority's journal and record quarantine; this list is a triage copy held in the application database.";

    public const string EvidenceStatement =
        "Technical evidence only: codes, identifiers, revisions and times. Record content that failed verification is never shown. Resolving an incident does not lift the Authority's quarantine or restore access to the record.";

    [GeneratedRegex("^INC-([0-9A-Fa-f]{8})$")]
    private static partial Regex IncidentReferencePattern();

    [GeneratedRegex("^REC-([0-9A-Fa-f]{8})$")]
    private static partial Regex RecordReferencePattern();

    public static string ReferenceFor(Guid securityIncidentId) => "INC-" + securityIncidentId.ToString("N")[^8..].ToUpperInvariant();

    public static SecurityIncidentListQuery Normalize(string? search, string? type, string? severity, string? status, DateTimeOffset? from, DateTimeOffset? before, int? pageSize)
    {
        static string? Allowed(string? value, IReadOnlyList<string> allowed, string name) =>
            string.IsNullOrWhiteSpace(value) ? null : allowed.Contains(value.Trim()) ? value.Trim() : throw new BusinessException("INVALID_REQUEST", 422, "unknown " + name);

        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (text is { Length: > 64 })
        {
            throw new BusinessException("INVALID_REQUEST", 422, "search text is too long");
        }

        if (from is { } f && before is { } b && b <= f)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "the end date must be after the start date");
        }

        return new SecurityIncidentListQuery(text, Allowed(type, SecurityIncidentTypes.All, "incident type"), Allowed(severity, SecurityIncidentSeverities.All, "severity"), Allowed(status, SecurityIncidentStatuses.All, "status"), from, before, OffsetPageCursor.ClampPageSize(pageSize));
    }

    public async Task<SecurityIncidentPageResponse> ListAsync(RequestContext context, SecurityIncidentListQuery query, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        await RequireSecurityAdministratorAsync(context, cancellationToken).ConfigureAwait(false);
        var digest = OffsetPageCursor.Digest("security-incidents", query.Search, query.IncidentTypeName, query.IncidentSeverityName, query.IncidentStatusName, query.DetectedFrom, query.DetectedBefore, query.PageSize);
        var offset = OffsetPageCursor.DecodeOffset(cursors, continuationToken, context, digest);

        string? incidentSuffix = null;
        IReadOnlyCollection<Guid>? recordFilter = null;
        if (query.Search is { } text)
        {
            if (IncidentReferencePattern().Match(text) is { Success: true } incidentMatch)
            {
                incidentSuffix = incidentMatch.Groups[1].Value.ToLowerInvariant();
            }
            else if (Guid.TryParse(text, out var exactId))
            {
                // A full id names an incident, or else a record — and a record only counts when the viewer may discover it.
                var incidentExists = (await QueryAsync(context, new SecurityIncidentFilter(null, null, null, null, null, exactId.ToString("D"), null), 0, 1, cancellationToken).ConfigureAwait(false)).TotalCount > 0;
                if (incidentExists)
                {
                    incidentSuffix = exactId.ToString("D");
                }
                else
                {
                    recordFilter = await DiscoverableAsync(context, [exactId], cancellationToken).ConfigureAwait(false);
                }
            }
            else if (RecordReferencePattern().Match(text) is { Success: true } recordMatch)
            {
                await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                var candidates = await scope.FindSecurityIncidentsByRecordSuffixAsync(context.OrganizationId, recordMatch.Groups[1].Value.ToLowerInvariant(), 200, cancellationToken).ConfigureAwait(false);
                recordFilter = await DiscoverableAsync(context, candidates.Select(c => c.FormRecordId!.Value).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new BusinessException("INVALID_REQUEST", 422, "search by incident reference (INC-…), record reference (REC-…) or a full identifier");
            }
        }

        var filter = new SecurityIncidentFilter(query.IncidentTypeName, query.IncidentSeverityName, query.IncidentStatusName, query.DetectedFrom, query.DetectedBefore, incidentSuffix, recordFilter);
        var page = await QueryAsync(context, filter, offset, query.PageSize, cancellationToken).ConfigureAwait(false);
        var items = await DescribeAsync(context, page.Items, cancellationToken).ConfigureAwait(false);
        var (next, previous) = OffsetPageCursor.Links(cursors, context, digest, offset, items.Count, query.PageSize, page.TotalCount);
        return new SecurityIncidentPageResponse(items, page.TotalCount, page.TotalCount == 0 ? 0 : offset + 1, query.PageSize, next, previous);
    }

    public async Task<SecurityIncidentSummaryResponse> SummaryAsync(RequestContext context, int? periodDays, string? timeZoneId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireSecurityAdministratorAsync(context, cancellationToken).ConfigureAwait(false);
        var plan = TimeBucketPlan.Create(clock.GetUtcNow(), periodDays, timeZoneId);
        List<SecurityIncidentStatistic> statistics;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            statistics = await scope.GetSecurityIncidentStatisticsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        }

        var integrity = new int[plan.BucketCount];
        var operational = new int[plan.BucketCount];
        var recent = 0;
        foreach (var statistic in statistics)
        {
            if (plan.IndexOf(statistic.FirstDetectedOn) is { } index)
            {
                recent++;
                if (statistic.IncidentTypeName is SecurityIncidentTypes.ConfirmedIntegrityMismatch or SecurityIncidentTypes.IntegrityQuarantine)
                {
                    integrity[index]++;
                }
                else
                {
                    operational[index]++;
                }
            }
        }

        int Count(Func<SecurityIncidentStatistic, bool> predicate) => statistics.Count(predicate);
        return new SecurityIncidentSummaryResponse(
            Count(s => s.IncidentStatusName == SecurityIncidentStatuses.Open),
            Count(s => s.IncidentStatusName == SecurityIncidentStatuses.Investigating),
            Count(s => s.IncidentStatusName == SecurityIncidentStatuses.Resolved),
            Count(s => s.IncidentStatusName == SecurityIncidentStatuses.FalsePositive),
            Count(s => s.IncidentTypeName == SecurityIncidentTypes.ConfirmedIntegrityMismatch),
            Count(s => s.IncidentSeverityName == SecurityIncidentSeverities.Critical),
            Count(s => s.IncidentSeverityName == SecurityIncidentSeverities.High),
            Count(s => s.IncidentSeverityName == SecurityIncidentSeverities.Medium),
            Count(s => s.IncidentSeverityName == SecurityIncidentSeverities.Low),
            recent,
            plan.PeriodDays,
            plan.TimeZoneId,
            Enumerable.Range(0, plan.BucketCount).Select(i => new SecurityIncidentBucketResponse(plan.BucketStartDate(i), plan.BucketDayCount, integrity[i], operational[i])).ToList(),
            CoverageStatement);
    }

    public async Task<SecurityIncidentDetailResponse> GetAsync(RequestContext context, Guid securityIncidentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await RequireSecurityAdministratorAsync(context, cancellationToken).ConfigureAwait(false);
        SecurityIncident incident;
        List<SecurityIncidentOccurrence> occurrences;
        List<SecurityIncidentNote> notes;
        List<AuditEvent> trail;
        Dictionary<Guid, string> names;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            incident = await scope.FindSecurityIncidentAsync(context.OrganizationId, securityIncidentId, lockForUpdate: false, cancellationToken).ConfigureAwait(false)
                       ?? throw new BusinessException("NOT_FOUND", 404, "incident not found");
            occurrences = await scope.GetSecurityIncidentOccurrencesAsync(securityIncidentId, 100, cancellationToken).ConfigureAwait(false);
            notes = await scope.GetSecurityIncidentNotesAsync(securityIncidentId, cancellationToken).ConfigureAwait(false);
            trail = await scope.GetTargetAuditEventsAsync(context.OrganizationId, TargetEntityType, securityIncidentId, 200, cancellationToken).ConfigureAwait(false);
            names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
            // Access to security evidence is itself audited.
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.SecurityIncidentViewed, "security incident viewed", targetEntityType: TargetEntityType, targetEntityId: securityIncidentId);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var described = (await DescribeAsync(context, [incident], cancellationToken).ConfigureAwait(false))[0];
        var investigators = await InvestigatorsAsync(context, names, cancellationToken).ConfigureAwait(false);
        string? Name(Guid? id) => id is { } value ? names.GetValueOrDefault(value) ?? "Former member" : null;
        return new SecurityIncidentDetailResponse(
            described,
            incident.DetectionSourceName,
            incident.VerificationErrorCode,
            incident.FirstCorrelationIdentifier,
            incident.ResolutionExplanationText,
            incident.ResolvedOn,
            Name(incident.ResolvedByUserId),
            incident.IncidentRevision,
            occurrences.Select(o => new SecurityIncidentOccurrenceResponse(o.SecurityIncidentOccurrenceId, o.OccurrenceDetectedOn, o.ObservedIncidentTypeName, o.DetectionSourceName, o.VerificationErrorCode, Name(o.ObservedByUserId), o.RequestCorrelationIdentifier, o.CommittedContentRevision)).ToList(),
            notes.Select(n => new SecurityIncidentNoteResponse(n.SecurityIncidentNoteId, n.NoteCreatedOn, Name(n.NoteAuthorUserId)!, n.InvestigationNoteText)).ToList(),
            trail.Select(e => new AuditEventResponse(e.AuditEventId, e.EventOccurredOn, e.EventCategory, e.EventAction, e.EventOutcome, e.ActorUserId, Name(e.ActorUserId), e.EventDescription, e.CorrelationIdentifier, null, e.TargetEntityId)).ToList(),
            investigators,
            EvidenceStatement);
    }

    public async Task<SecurityIncidentDetailResponse> UpdateAsync(RequestContext context, Guid securityIncidentId, UpdateSecurityIncidentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireSecurityAdministratorAsync(context, cancellationToken).ConfigureAwait(false);
        var status = string.IsNullOrWhiteSpace(request.IncidentStatusName) ? null : request.IncidentStatusName.Trim();
        if (status is not null && !SecurityIncidentStatuses.All.Contains(status))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "unknown status");
        }

        var explanation = string.IsNullOrWhiteSpace(request.ResolutionExplanation) ? null : request.ResolutionExplanation.Trim();
        if (explanation is { Length: > SecurityIncidentNote.MaximumNoteLength })
        {
            throw new BusinessException("INVALID_REQUEST", 422, "the resolution explanation is too long (2000 characters at most)");
        }

        if (request.ClearAssignment && request.AssignedToUserId is not null)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "choose an investigator or clear the assignment, not both");
        }

        if (request.AssignedToUserId is { } assignee)
        {
            // Only a current Security Administrator (Authority roster) can be assigned.
            var current = await roster.GetSecurityAdministratorsAsync(context.ToDelegatedUser(), new SecurityAdministratorRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
            if (current.Administrators.All(a => a.AuthorityUserId != assignee))
            {
                throw new BusinessException("INVALID_REQUEST", 422, "incidents can be assigned only to a Security Administrator");
            }
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (await scope.FindSecurityIncidentAsync(context.OrganizationId, securityIncidentId, lockForUpdate: false, cancellationToken).ConfigureAwait(false)
                       ?? throw new BusinessException("NOT_FOUND", 404, "incident not found")).IncidentDeduplicationKey;
            // Same lock order as the recorder (problem lock, then rows), so a detection and an investigation never deadlock.
            var newest = await scope.LockLatestSecurityIncidentAsync(context.OrganizationId, key, cancellationToken).ConfigureAwait(false);
            var incident = await scope.FindSecurityIncidentAsync(context.OrganizationId, securityIncidentId, lockForUpdate: true, cancellationToken).ConfigureAwait(false)
                           ?? throw new BusinessException("NOT_FOUND", 404, "incident not found");
            if (incident.IncidentRevision != request.ExpectedIncidentRevision)
            {
                throw new BusinessException("INCIDENT_MODIFIED_CONCURRENTLY", 409, "someone else changed this incident; reload it and try again");
            }

            var now = clock.GetUtcNow();
            var changed = false;
            if (status is not null && status != incident.IncidentStatusName)
            {
                var closing = SecurityIncidentStatuses.IsClosed(status);
                if (closing && explanation is null)
                {
                    throw new BusinessException("INVALID_REQUEST", 422, "explain the resolution before closing the incident");
                }

                if (SecurityIncidentStatuses.IsActive(status) && SecurityIncidentStatuses.IsClosed(incident.IncidentStatusName))
                {
                    // Reopening: a newer active incident for the same problem already collects detections.
                    if (newest is not null && newest.SecurityIncidentId != incident.SecurityIncidentId && SecurityIncidentStatuses.IsActive(newest.IncidentStatusName))
                    {
                        throw new BusinessException("INCIDENT_SUPERSEDED", 409, "a newer open incident exists for the same problem (" + ReferenceFor(newest.SecurityIncidentId) + "); continue there");
                    }
                }

                var previous = incident.IncidentStatusName;
                incident.IncidentStatusName = status;
                if (closing)
                {
                    incident.ResolvedOn = now;
                    incident.ResolvedByUserId = context.LocalUserId;
                    incident.ResolutionExplanationText = explanation;
                }
                else
                {
                    incident.ResolvedOn = null;
                    incident.ResolvedByUserId = null;
                    incident.ResolutionExplanationText = null;
                }

                AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.SecurityIncidentStatusChanged, $"security incident status changed from {previous} to {status}",
                    targetEntityType: TargetEntityType, targetEntityId: securityIncidentId, metadata: new { previousStatus = previous, incidentStatus = status, explanationProvided = explanation is not null });
                changed = true;
            }
            else if (status is not null && SecurityIncidentStatuses.IsClosed(status) && explanation is not null && explanation != incident.ResolutionExplanationText)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "the incident is already closed; add a note instead");
            }

            var newAssignee = request.ClearAssignment ? null : request.AssignedToUserId ?? incident.AssignedToUserId;
            if (newAssignee != incident.AssignedToUserId)
            {
                var previous = incident.AssignedToUserId;
                incident.AssignedToUserId = newAssignee;
                AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.SecurityIncidentAssigned, newAssignee is null ? "security incident unassigned" : "security incident assigned",
                    targetEntityType: TargetEntityType, targetEntityId: securityIncidentId, metadata: new { previousAssigneeUserId = previous, assigneeUserId = newAssignee });
                changed = true;
            }

            if (!changed)
            {
                throw new BusinessException("INVALID_REQUEST", 422, "nothing to change");
            }

            incident.IncidentRevision++;
            incident.IncidentModifiedOn = now;
            incident.IncidentModifiedByUserId = context.LocalUserId;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(context, securityIncidentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecurityIncidentDetailResponse> AddNoteAsync(RequestContext context, Guid securityIncidentId, AddSecurityIncidentNoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireSecurityAdministratorAsync(context, cancellationToken).ConfigureAwait(false);
        var text = request.InvestigationNote?.Trim() ?? string.Empty;
        if (text.Length is 0 or > SecurityIncidentNote.MaximumNoteLength)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "write a note of 1 to 2000 characters");
        }

        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = await scope.FindSecurityIncidentAsync(context.OrganizationId, securityIncidentId, lockForUpdate: false, cancellationToken).ConfigureAwait(false)
                ?? throw new BusinessException("NOT_FOUND", 404, "incident not found");
            var note = new SecurityIncidentNote
            {
                SecurityIncidentNoteId = Guid.CreateVersion7(),
                SecurityIncidentId = securityIncidentId,
                TenantId = context.OrganizationId,
                NoteAuthorUserId = context.LocalUserId,
                NoteCreatedOn = clock.GetUtcNow(),
                InvestigationNoteText = text,
            };
            scope.AddSecurityIncidentNote(note);
            // The note text stays in the note table; the audit event records that a note was written, not what it says.
            AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.SecurityIncidentNoteAdded, "investigation note added", targetEntityType: TargetEntityType, targetEntityId: securityIncidentId,
                metadata: new { securityIncidentNoteId = note.SecurityIncidentNoteId, noteLength = text.Length.ToString(CultureInfo.InvariantCulture) });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(context, securityIncidentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Same rule as the other Security Administrator pages: the Authority's administer_security, or 403 with no detail.</summary>
    public async Task RequireSecurityAdministratorAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.AdministerSecurity), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "security incidents are available to Security Administrators");
        }
    }

    private async Task<StorePage<SecurityIncident>> QueryAsync(RequestContext context, SecurityIncidentFilter filter, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return await scope.QuerySecurityIncidentsAsync(context.OrganizationId, filter, offset, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Record ids among <paramref name="recordIds"/> the viewer may discover (Authority batch decision).</summary>
    private async Task<List<Guid>> DiscoverableAsync(RequestContext context, List<Guid> recordIds, CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return [];
        }

        var decisions = await authority.CheckPermissionsBatchAsync(context.ToDelegatedUser(),
            recordIds.Select(id => new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, id), ProtectedAction.Discover)).ToList(),
            cancellationToken).ConfigureAwait(false);
        return recordIds.Where((_, i) => decisions[i].Allowed).ToList();
    }

    private async Task<List<SecurityIncidentListItemResponse>> DescribeAsync(RequestContext context, IReadOnlyList<SecurityIncident> incidents, CancellationToken cancellationToken)
    {
        var recordIds = incidents.Where(i => i.FormRecordId is not null).Select(i => i.FormRecordId!.Value).Distinct().ToList();
        var visible = (await DiscoverableAsync(context, recordIds, cancellationToken).ConfigureAwait(false)).ToHashSet();
        Dictionary<Guid, string> names;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        }

        return incidents.Select(i =>
        {
            var shown = i.FormRecordId is { } record && visible.Contains(record);
            return new SecurityIncidentListItemResponse(
                i.SecurityIncidentId, ReferenceFor(i.SecurityIncidentId), i.IncidentTypeName, i.IncidentSeverityName, i.IncidentStatusName, i.FirstDetectedOn, i.LastDetectedOn, i.OccurrenceCount,
                i.FormRecordId is not null, i.FormRecordId is not null && !shown, shown ? i.FormRecordId : null, shown ? "REC-" + i.FormRecordId!.Value.ToString("N")[^8..].ToUpperInvariant() : null,
                i.AssignedToUserId, i.AssignedToUserId is { } a ? names.GetValueOrDefault(a) ?? "Former member" : null);
        }).ToList();
    }

    private async Task<List<SecurityIncidentInvestigatorResponse>> InvestigatorsAsync(RequestContext context, Dictionary<Guid, string> names, CancellationToken cancellationToken)
    {
        var current = await roster.GetSecurityAdministratorsAsync(context.ToDelegatedUser(), new SecurityAdministratorRosterQuery(context.OrganizationId), cancellationToken).ConfigureAwait(false);
        return current.Administrators.Select(a => new SecurityIncidentInvestigatorResponse(a.AuthorityUserId, names.GetValueOrDefault(a.AuthorityUserId) ?? a.AuthorityUserId.ToString("D")))
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
