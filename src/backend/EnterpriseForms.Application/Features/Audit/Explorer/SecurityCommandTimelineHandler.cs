using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Audit.Explorer;

/// <summary>Validated timeline filter (AC-8 §6.1): one kind code, one state code and dates only; never free text.</summary>
public sealed record SecurityCommandTimelineQuery(string? Kind, string? State, DateTimeOffset? From, DateTimeOffset? To, int PageSize)
{
    public const int DefaultPageSize = 25;

    public const int MaximumPageSize = 50;

    /// <summary>Lower-case hex SHA-256 of the canonical filter; binds the page cursor to these filters.</summary>
    public string Digest()
    {
        var canonical = string.Join('|', "security-command-timeline/v1", Kind ?? "-", State ?? "-",
            From?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-", To?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-", PageSize.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// C8 M4-3 (AC-8.2): the security-command timeline tab of PG-054. The Authority checks view_audit, reads the page inside its AC-8.3
/// audit-access transaction and commits <c>AuditAccessAuthorized</c> (served_by authority) before it answers; this handler only
/// validates the filter, protects the cursor, resolves directory display names for the actors (D8-67a) and renders the envelopes the
/// Authority built field by field. It writes nothing (R-5: no business delivery row) and reads no command data of its own. Every
/// page is authorized again; no permission is cached.
/// </summary>
public sealed partial class SecurityCommandTimelineHandler(IBusinessStore store, ISecurityCommandTimelineAuthority authority, IOpaqueCursorProtector cursors)
{
    public const string OrderingStatement =
        "Newest first by event time; at an equal time, command rows come before audit-stream entries. Times are the Authorization Authority's, in UTC.";

    public const string EvidenceStatement =
        "Read from the Authorization Authority's own database. A command item's kind, state, revision and details are read from the Authority's command row, "
        + "which is not cryptographically verified. \"Journal-backed\" means a journal entry naming the command was found (its stream, revision, digest and "
        + "archive state are shown; local meaning): the command row's fields are not checked against that entry, and this page does not re-verify the journal's "
        + "hash chain. \"Command row only\" items, such as most rejected commands, have no such entry: this is expected and is not a sign of tampering.";

    public const string AccessStatement = "Read and served by the Authorization Authority after it recorded this access; receipt by your browser is not attested.";

    public static readonly IReadOnlyList<string> States = ["Pending", "CommittedAwaitingProjection", "Effective", "Rejected", "FailedReconciliation"];

    [GeneratedRegex("^[A-Z][A-Za-z]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();

    public static SecurityCommandTimelineQuery Normalize(string? kind, string? state, DateTimeOffset? from, DateTimeOffset? to, int? pageSize)
    {
        var trimmedKind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        if (trimmedKind is not null && !KindPattern().IsMatch(trimmedKind))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "invalid kind");
        }

        var trimmedState = string.IsNullOrWhiteSpace(state) ? null : state.Trim();
        if (trimmedState is not null && !States.Contains(trimmedState, StringComparer.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "invalid state");
        }

        if (from is { } f && to is { } t && t <= f)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "the end of the period must be after its start");
        }

        if (pageSize is < 1 or > SecurityCommandTimelineQuery.MaximumPageSize)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "pageSize must be 1..50");
        }

        return new SecurityCommandTimelineQuery(trimmedKind, trimmedState, from?.ToUniversalTime(), to?.ToUniversalTime(), pageSize ?? SecurityCommandTimelineQuery.DefaultPageSize);
    }

    public async Task<SecurityCommandTimelinePageResponse> ReadAsync(RequestContext context, SecurityCommandTimelineQuery query, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var digest = query.Digest();
        var after = TimelineCursor.Decode(cursors, cursor, context, digest);
        AuthorityTimelinePage page;
        try
        {
            page = await authority.ReadSecurityCommandTimelineAsync(context.ToDelegatedUser(),
                new AuthorityTimelineQuery(context.OrganizationId, after, query.PageSize, query.Kind is null ? null : [query.Kind], query.State is null ? null : [query.State], query.From, query.To, context.CorrelationId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex)
        {
            throw ex.HttpStatusCode switch
            {
                403 => new BusinessException("AUDIT_VIEW_FORBIDDEN", 403, "the audit explorer is available to Security Auditors"),
                422 => new BusinessException("INVALID_REQUEST", 422, "these filters are not valid for the security-command timeline"),
                429 => new BusinessException("RATE_LIMITED", 429, "too many audit requests; wait a moment and try again"),
                _ when ex.ErrorCode is "AUDIT_EVIDENCE_BUSY" => new BusinessException("AUDIT_EVIDENCE_BUSY", 503, "audit access could not be recorded right now; nothing was served — try again"),
                _ when ex.ErrorCode is "AUDIT_EVIDENCE_UNAVAILABLE" => new BusinessException("AUDIT_EVIDENCE_UNAVAILABLE", 503, "audit access could not be recorded; nothing was served"),
                _ when ex.ErrorCode is "AUTHORITY_NOT_READY" or "INTEGRITY_QUARANTINE" || ex.HttpStatusCode == 423 => new BusinessException("AUTHORITY_GATE_CLOSED", 503, "the authorization authority is not ready for this organization; audit is unavailable"),
                _ => new BusinessException("AUTHORITY_UNAVAILABLE", 503, "the authorization authority is unavailable; audit is unavailable"),
            };
        }

        if (page.AuditJournalRevision <= 0 || !string.Equals(page.ServedBy, "authority", StringComparison.Ordinal) || !string.Equals(page.Delivery, "not_attested", StringComparison.Ordinal))
        {
            throw new BusinessException("AUDIT_EVIDENCE_UNAVAILABLE", 503, "the authority did not confirm recorded audit access; nothing was served");
        }

        Dictionary<Guid, string> names;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            names = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).Where(u => u.DisplayName.Length > 0).ToDictionary(u => u.UserId, u => u.DisplayName);
        }

        var items = page.Items.Select(i => Item(i, names)).ToList();
        var next = page.After is { Length: > 0 and <= 200 } position ? TimelineCursor.Encode(cursors, context, digest, position) : null;
        return new SecurityCommandTimelinePageResponse(items, next, query.PageSize, OrderingStatement, EvidenceStatement, new AuditAccessEvidenceResponse(page.AuditJournalRevision, AccessStatement));
    }

    private static SecurityCommandTimelineItemResponse Item(AuthorityTimelineEntry entry, Dictionary<Guid, string> names) => new(
        entry.Source,
        entry.Kind,
        entry.OccurredOn,
        entry.CommandId,
        entry.CommandState,
        entry.CommittedAuthorizationRevision,
        entry.RejectionCode,
        entry.ActorUserId,
        entry.ActorUserId is { } actor ? names.GetValueOrDefault(actor) ?? "Former member" : null,
        entry.EffectiveOn,
        entry.EvidenceBasis,
        entry.ReportedBy,
        entry.Evidence is { } evidence ? new SecurityCommandTimelineEvidenceResponse(evidence.Stream, evidence.StreamRevision, evidence.EntryDigestHex, evidence.ArchiveState, evidence.ReceiptReferencePresent) : null,
        entry.Details.Select(d => new SecurityCommandTimelineDetailResponse(d.Key, d.Kind, d.Value, d.Values,
            d.Kind == "Identifier" && Guid.TryParse(d.Value, out var id) ? names.GetValueOrDefault(id) : null)).ToList(),
        entry.WithheldFields);

    /// <summary>
    /// The page cursor: the Authority's keyset position, authenticated-encrypted, expiring and bound to the organization, the caller and
    /// the filter digest. It only locates a position; the Authority validates it again and authorizes every page.
    /// </summary>
    public static class TimelineCursor
    {
        private const int Version = 1;

        public static string Encode(IOpaqueCursorProtector protector, RequestContext context, string filterDigest, string authorityPosition)
        {
            ArgumentNullException.ThrowIfNull(protector);
            ArgumentNullException.ThrowIfNull(context);
            return protector.Protect(JsonSerializer.Serialize(new Payload(Version, context.OrganizationId, context.LocalUserId, filterDigest, authorityPosition)), AuditExplorerCursor.Lifetime);
        }

        public static string? Decode(IOpaqueCursorProtector protector, string? token, RequestContext context, string filterDigest)
        {
            ArgumentNullException.ThrowIfNull(protector);
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var json = token.Length > 4096 ? null : protector.Unprotect(token);
            Payload? payload = null;
            if (json is not null)
            {
                try
                {
                    payload = JsonSerializer.Deserialize<Payload>(json);
                }
                catch (JsonException)
                {
                    payload = null;
                }
            }

            if (payload is null || payload.V != Version || payload.O != context.OrganizationId || payload.U != context.LocalUserId || !string.Equals(payload.Q, filterDigest, StringComparison.Ordinal)
                || payload.A is not { Length: > 0 and <= 200 })
            {
                throw new BusinessException("CURSOR_INVALID", 400, "this page link is not valid for you, these filters or any more; reload the list");
            }

            return payload.A;
        }

        private sealed record Payload(int V, Guid O, Guid U, string Q, string A);
    }
}
