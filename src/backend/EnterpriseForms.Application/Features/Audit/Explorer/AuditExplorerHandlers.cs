using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Audit.Explorer;

/// <summary>Validated explorer filter (AC-8 §6.1). Every member is an id, a code or a date: never free text.</summary>
public sealed record AuditExplorerQuery(DateTimeOffset? From, DateTimeOffset? To, Guid? ActorUserId, string? Action, string? Category, string? Outcome, string? EntityType, Guid? EntityId, Guid? FormRecordId, Guid? CorrelationId,
    bool ExcludeAuditAccess, int PageSize)
{
    public const int DefaultPageSize = 25;

    public const int MaximumPageSize = 50;

    /// <summary>
    /// Lower-case hex SHA-256 of the canonical filter (order fixed, UTC ticks for dates, "-" for absent). It is the filter digest
    /// sent to the Authority (AC-8.3) and the binding of the page cursor.
    /// </summary>
    public string Digest()
    {
        static string Part(object? value) => value switch
        {
            null => "-",
            DateTimeOffset d => d.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };

        var canonical = string.Join('|', "audit-explorer/v1", Part(From), Part(To), Part(ActorUserId), Part(Action), Part(Category), Part(Outcome), Part(EntityType), Part(EntityId), Part(FormRecordId), Part(CorrelationId), Part(ExcludeAuditAccess), Part(PageSize));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// C8 M4-2: PG-054 audit explorer and PG-055 audit event detail over the business audit projection (AC-8 §6).
/// <list type="number">
/// <item>The filter is validated (ids, codes and dates only) and a cursor is checked against the caller, organization and filter.</item>
/// <item>The Authority authorizes the read (view_audit) and commits <c>AuditAccessAuthorized</c> before answering (AC-8.3). Without
/// that allow nothing is read: 403, 429 or 503 and no data.</item>
/// <item>Only then are rows read, always predicated on the session's organization (R-15: there is no business RLS), and in the same
/// business transaction the delivery outcome (<c>AUDIT_EVENTS_VIEWED</c>/<c>AUDIT_EVENT_VIEWED</c>, <c>Delivered</c>) is written with
/// the Authority's journal revision. If the read or that write fails, nothing is served and a <c>Failed</c> row is attempted.</item>
/// <item>Rows render through <see cref="AuditEventCatalog"/>: list items are envelopes; detail adds only allowlisted metadata.</item>
/// </list>
/// No permission is cached: every page and detail is authorized and recorded again. Rows can be forged or deleted by a business
/// database administrator (RR-1); they are labelled as an unverified projection and never carry a verified badge.
/// </summary>
public sealed partial class AuditExplorerHandlers(IBusinessStore store, IAuditAccessAuthority authority, IOpaqueCursorProtector cursors, TimeProvider clock)
{
    public const string TargetEntityType = "audit_event";

    public const string OrderingStatement =
        "Newest first by event time, with the sequence number as tie-break. This is event-time order, not commit order. Times are the application server's clock when the event was recorded, in UTC.";

    public const string EvidenceStatement =
        "Business projection — not independently verified (AUD-C7-01/G24 open). These rows are held in the application database, where a database administrator could add, change or delete them; they have no archive receipt.";

    public const string AccessStatement = "Access authorized and recorded by the Authorization Authority; delivery is business-reported.";

    private static readonly string[] AuditAccessActions = [AuditActions.AuditEventsViewed, AuditActions.AuditEventViewed];

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityTypePattern();

    public static AuditExplorerQuery Normalize(DateTimeOffset? from, DateTimeOffset? to, Guid? actorUserId, string? action, string? category, string? outcome, string? entityType, Guid? entityId, Guid? formRecordId, Guid? correlationId,
        bool? excludeAuditAccess, int? pageSize)
    {
        static string? Checked(string? value, Regex pattern, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return pattern.IsMatch(trimmed) ? trimmed : throw new BusinessException("INVALID_REQUEST", 422, "invalid " + name);
        }

        if (from is { } f && to is { } t && t <= f)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "the end of the period must be after its start");
        }

        if (pageSize is < 1 or > AuditExplorerQuery.MaximumPageSize)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "pageSize must be 1..50");
        }

        static Guid? NonEmpty(Guid? id) => id == Guid.Empty ? throw new BusinessException("INVALID_REQUEST", 422, "an identifier filter must not be empty") : id;

        return new AuditExplorerQuery(from?.ToUniversalTime(), to?.ToUniversalTime(), NonEmpty(actorUserId), Checked(action, ActionPattern(), "action"), Checked(category, WordPattern(), "category"), Checked(outcome, WordPattern(), "outcome"),
            Checked(entityType, EntityTypePattern(), "entity type"), NonEmpty(entityId), NonEmpty(formRecordId), NonEmpty(correlationId), excludeAuditAccess ?? false, pageSize ?? AuditExplorerQuery.DefaultPageSize);
    }

    public async Task<AuditExplorerPageResponse> SearchAsync(RequestContext context, AuditExplorerQuery query, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var digest = query.Digest();
        var position = AuditExplorerCursor.Decode(cursors, cursor, context, digest);
        var decision = await AuthorizeAsync(context, "ExplorerPage", digest, query.PageSize, null, cancellationToken).ConfigureAwait(false);

        var filter = new OrganizationAuditEventFilter(query.From, query.To, query.ActorUserId, query.Action, query.Category, query.Outcome, query.EntityType, query.EntityId, query.FormRecordId, query.CorrelationId,
            query.ExcludeAuditAccess ? AuditAccessActions : []);
        try
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var rows = await scope.QueryOrganizationAuditEventsAsync(context.OrganizationId, filter, position, query.PageSize + 1, cancellationToken).ConfigureAwait(false);
            var page = rows.Take(query.PageSize).ToList();
            var directory = await DirectoryAsync(scope, context, cancellationToken).ConfigureAwait(false);
            var items = page.Select(e => Envelope(e, directory)).ToList();
            var next = rows.Count > query.PageSize ? AuditExplorerCursor.Encode(cursors, context, digest, new AuditEventPosition(page[^1].EventOccurredOn, page[^1].AuditSequence)) : null;
            RecordDelivery(scope, context, AuditActions.AuditEventsViewed, "audit explorer page served", "Delivered", decision, "ExplorerPage", digest, null);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AuditExplorerPageResponse(items, next, query.PageSize, OrderingStatement, EvidenceStatement, new AuditAccessEvidenceResponse(decision.AuditJournalRevision, AccessStatement));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BusinessException)
        {
            await RecordFailedDeliveryAsync(context, AuditActions.AuditEventsViewed, decision, "ExplorerPage", digest, null).ConfigureAwait(false);
            throw new BusinessException("AUDIT_DELIVERY_FAILED", 503, "the audit page could not be read; nothing was served");
        }
    }

    public async Task<AuditEventDetailResponse> GetAsync(RequestContext context, Guid auditEventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var digest = new AuditExplorerQuery(null, null, null, null, null, null, TargetEntityType, auditEventId, null, null, false, 1).Digest();
        var decision = await AuthorizeAsync(context, "EventDetail", digest, 1, auditEventId, cancellationToken).ConfigureAwait(false);
        AuditEventDetailResponse? response = null;
        try
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var row = await scope.FindOrganizationAuditEventAsync(context.OrganizationId, auditEventId, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                var directory = await DirectoryAsync(scope, context, cancellationToken).ConfigureAwait(false);
                var rendered = AuditEventCatalog.Render(row);
                response = new AuditEventDetailResponse(
                    Envelope(row, directory),
                    rendered.Description,
                    rendered.Metadata.Select(m => new AuditMetadataItemResponse(m.Key, m.Label, m.Kind.ToString(), m.Value)).ToList(),
                    rendered.Withheld.Select(w => new AuditWithheldMetadataResponse(w.Label, w.Policy)).ToList(),
                    Links(row),
                    new AuditReceiptResponse("None", "AUD-C7-01/G24"),
                    OrderingStatement,
                    EvidenceStatement,
                    new AuditAccessEvidenceResponse(decision.AuditJournalRevision, AccessStatement));
            }

            // Another organization's id and an unknown id are the same 404; the authorized access is still reported.
            RecordDelivery(scope, context, AuditActions.AuditEventViewed, row is null ? "audit event not found" : "audit event detail served", row is null ? "NotFound" : "Delivered", decision, "EventDetail", digest, auditEventId);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BusinessException)
        {
            await RecordFailedDeliveryAsync(context, AuditActions.AuditEventViewed, decision, "EventDetail", digest, auditEventId).ConfigureAwait(false);
            throw new BusinessException("AUDIT_DELIVERY_FAILED", 503, "the audit event could not be read; nothing was served");
        }

        return response ?? throw new BusinessException("NOT_FOUND", 404, "audit event not found");
    }

    /// <summary>AC-8.3: the Authority's allow (committed evidence) or a refusal with nothing served.</summary>
    private async Task<AuthorityAuditAccessDecision> AuthorizeAsync(RequestContext context, string accessKind, string digest, int pageSize, Guid? auditEventId, CancellationToken cancellationToken)
    {
        AuthorityAuditAccessDecision decision;
        try
        {
            decision = await authority.AuthorizeAuditAccessAsync(context.ToDelegatedUser(), new AuditAccessQuery(context.OrganizationId, accessKind, digest, pageSize, auditEventId, context.CorrelationId), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex)
        {
            throw ex.HttpStatusCode switch
            {
                403 => new BusinessException("AUDIT_VIEW_FORBIDDEN", 403, "the audit explorer is available to Security Auditors"),
                429 => new BusinessException("RATE_LIMITED", 429, "too many audit requests; wait a moment and try again"),
                _ when ex.ErrorCode is "AUDIT_EVIDENCE_BUSY" => new BusinessException("AUDIT_EVIDENCE_BUSY", 503, "audit access could not be recorded right now; nothing was served — try again"),
                _ when ex.ErrorCode is "AUDIT_EVIDENCE_UNAVAILABLE" => new BusinessException("AUDIT_EVIDENCE_UNAVAILABLE", 503, "audit access could not be recorded; nothing was served"),
                _ when ex.ErrorCode is "AUTHORITY_NOT_READY" or "INTEGRITY_QUARANTINE" || ex.HttpStatusCode == 423 => new BusinessException("AUTHORITY_GATE_CLOSED", 503, "the authorization authority is not ready for this organization; audit is unavailable"),
                _ => new BusinessException("AUTHORITY_UNAVAILABLE", 503, "the authorization authority is unavailable; audit is unavailable"),
            };
        }

        if (!decision.Allowed || decision.AuditJournalRevision <= 0 || !string.Equals(decision.Delivery, "not_attested", StringComparison.Ordinal))
        {
            throw new BusinessException("AUDIT_EVIDENCE_UNAVAILABLE", 503, "the authority did not confirm recorded audit access; nothing was served");
        }

        return decision;
    }

    private void RecordDelivery(IBusinessScope scope, RequestContext context, string action, string description, string outcome, AuthorityAuditAccessDecision decision, string accessKind, string digest, Guid? auditEventId) =>
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, action, description, targetEntityType: auditEventId is null ? null : TargetEntityType, targetEntityId: auditEventId,
            metadata: new { deliveryOutcome = outcome, authorityJournalRevision = decision.AuditJournalRevision, accessKind, filterDigest = digest },
            // event_outcome is constrained to Success/Denied/Failed (ck_audit_events_outcome): an authorized lookup that found no row
            // completed successfully; the Delivered/NotFound distinction is the deliveryOutcome metadata.
            outcome: outcome == "Failed" ? "Failed" : "Success");

    /// <summary>Best effort; a failure here never changes the response (nothing was served either way).</summary>
    private async Task RecordFailedDeliveryAsync(RequestContext context, string action, AuthorityAuditAccessDecision decision, string accessKind, string digest, Guid? auditEventId)
    {
        try
        {
            await using var scope = await store.BeginScopeAsync(CancellationToken.None).ConfigureAwait(false);
            RecordDelivery(scope, context, action, "audit read failed after authorization; nothing was served", "Failed", decision, accessKind, digest, auditEventId);
            await scope.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the Authority's AuditAccessAuthorized entry remains the evidence of the authorized access
        }
    }

    private static async Task<(Dictionary<Guid, string> Users, Dictionary<Guid, string> Forms)> DirectoryAsync(IBusinessScope scope, RequestContext context, CancellationToken cancellationToken)
    {
        var users = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);
        var forms = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FormDefinitionId, f => f.FormDefinitionName);
        return (users, forms);
    }

    private static AuditExplorerEventResponse Envelope(AuditEvent e, (Dictionary<Guid, string> Users, Dictionary<Guid, string> Forms) directory)
    {
        var recognized = AuditEventCatalog.Find(e.EventAction) is not null;
        string? actorName = e.ActorUserId is { } actor ? directory.Users.GetValueOrDefault(actor) ?? "Former member" : null;
        string? formName = e.FormDefinitionId is { } form ? directory.Forms.GetValueOrDefault(form) : null;
        return new AuditExplorerEventResponse(e.AuditEventId, e.EventOccurredOn, e.AuditSequence, e.EventCategory, e.EventAction, AuditEventCatalog.LabelFor(e.EventAction), recognized, e.EventOutcome,
            new AuditExplorerActorResponse(e.ActorUserId, actorName, e.ActorType), new AuditExplorerTargetResponse(e.TargetEntityType, e.TargetEntityId),
            e.FormDefinitionId, formName, e.FormRecordId, e.FieldDefinitionId, e.CorrelationIdentifier, e.EventSchemaVersion, "BusinessProjection");
    }

    /// <summary>Links to normal routes, which authorize on their own (R-4); and explorer filters by actor and correlation.</summary>
    private static List<AuditEventLinkResponse> Links(AuditEvent e)
    {
        var links = new List<AuditEventLinkResponse>();
        if (e.FormRecordId is { } record)
        {
            links.Add(new AuditEventLinkResponse("record", "Open the record (authorized separately)", "/app/records/" + record.ToString("D")));
        }

        if (e.TargetEntityType == "team" && e.TargetEntityId is { } team)
        {
            links.Add(new AuditEventLinkResponse("team", "Open the team (authorized separately)", "/app/organization/teams/" + team.ToString("D")));
        }

        if (e.TargetEntityType == "security_incident" && e.TargetEntityId is { } incident)
        {
            links.Add(new AuditEventLinkResponse("incident", "Open the security incident (authorized separately)", "/app/security/incidents/" + incident.ToString("D")));
        }

        links.Add(new AuditEventLinkResponse("correlation", "Events with the same correlation id", "/app/security/audit?correlationId=" + e.CorrelationIdentifier.ToString("D")));
        if (e.ActorUserId is { } actor)
        {
            links.Add(new AuditEventLinkResponse("actor", "Events by the same actor", "/app/security/audit?actorUserId=" + actor.ToString("D")));
        }

        return links;
    }
}
