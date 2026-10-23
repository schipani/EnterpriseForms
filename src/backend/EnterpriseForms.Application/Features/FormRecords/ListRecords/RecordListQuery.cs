using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>
/// My Records query (C-29): filters, sort and page size of one listing, validated server-side. Filtering and sorting run
/// over the caller's complete authorized set (authority-sourced ids) joined with business metadata — never over a single
/// authority page — so ordering is deterministic across pages (record id is the final tie-break).
/// </summary>
public sealed record RecordListQuery(Guid? FormDefinitionId, string? RecordStatus, bool IncludeChildren, string SortBy, string SortDirection, int PageSize, bool IncludeArchived = false, string? Search = null)
{
    public const int MaximumPageSize = 100;

    /// <summary>The parsed exact search (record id or short reference), or null.</summary>
    public RecordLookupQuery? SearchQuery => Search is null ? null : RecordLookupQuery.Parse(Search);

    public static readonly IReadOnlyList<string> SortFields = ["modifiedOn", "createdOn", "formName", "recordStatus"];

    public static readonly IReadOnlyList<string> Statuses = ["Draft", "Submitted", "Archived", "Deleted"];

    public static RecordListQuery Create(Guid? formDefinitionId, string? recordStatus, bool includeChildren, string? sortBy, string? sortDirection, int pageSize, bool includeArchived = false, string? search = null)
    {
        var sort = string.IsNullOrEmpty(sortBy) ? "modifiedOn" : sortBy;
        if (!SortFields.Contains(sort, StringComparer.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "sortBy must be one of: " + string.Join(", ", SortFields));
        }

        var direction = string.IsNullOrEmpty(sortDirection) ? (sort is "formName" or "recordStatus" ? "asc" : "desc") : sortDirection;
        if (direction is not ("asc" or "desc"))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "sortDirection must be asc or desc");
        }

        if (recordStatus is not null && !Statuses.Contains(recordStatus, StringComparer.Ordinal))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "recordStatus must be one of: " + string.Join(", ", Statuses));
        }

        // Phase C6 unified search: an exact record id or short reference (anything else is refused, never searched as text).
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : RecordLookupQuery.Parse(search).Normalized;
        return new RecordListQuery(formDefinitionId, recordStatus, includeChildren, sort, direction, Math.Clamp(pageSize, 1, MaximumPageSize), includeArchived, normalizedSearch);
    }

    /// <summary>Stable digest of everything that shapes the ordered result; a cursor is valid only for the same scope.</summary>
    public string ScopeDigest()
    {
        var text = string.Join('|', FormDefinitionId?.ToString("D") ?? "-", RecordStatus ?? "-", IncludeChildren ? "1" : "0", IncludeArchived ? "a" : "-", Search ?? "-", SortBy, SortDirection, PageSize.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Applies the filters and the deterministic sort. <paramref name="formNames"/> resolves form display names.</summary>
    public List<FormRecord> Apply(IEnumerable<FormRecord> records, IReadOnlyDictionary<Guid, string> formNames)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(formNames);
        var filtered = records
            // An exact search names a record: it finds archived and related records too (the Status and Form filters still apply).
            .Where(r => Search is not null || IncludeChildren || r.ParentFormRecordId is null)
            .Where(r => FormDefinitionId is null || r.FormDefinitionId == FormDefinitionId)
            .Where(r => RecordStatus is null || string.Equals(r.RecordStatus, RecordStatus, StringComparison.Ordinal))
            // Phase C6 M1: Archived records are hidden from the ordinary list unless asked for (explicit status filter or includeArchived).
            // The status here is the unsigned listing projection; opening a record always shows its verified status.
            .Where(r => RecordStatus is not null || Search is not null || IncludeArchived || !string.Equals(r.RecordStatus, RecordLifecycleStatuses.Archived, StringComparison.Ordinal));
        var descending = SortDirection == "desc";
        IOrderedEnumerable<FormRecord> ordered = SortBy switch
        {
            "createdOn" => descending ? filtered.OrderByDescending(r => r.CreatedOn) : filtered.OrderBy(r => r.CreatedOn),
            "formName" => descending ? filtered.OrderByDescending(r => formNames.GetValueOrDefault(r.FormDefinitionId, string.Empty), StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(r => formNames.GetValueOrDefault(r.FormDefinitionId, string.Empty), StringComparer.OrdinalIgnoreCase),
            "recordStatus" => descending ? filtered.OrderByDescending(r => r.RecordStatus, StringComparer.Ordinal) : filtered.OrderBy(r => r.RecordStatus, StringComparer.Ordinal),
            _ => descending ? filtered.OrderByDescending(r => r.ModifiedOn) : filtered.OrderBy(r => r.ModifiedOn),
        };

        // Secondary keys keep the order total and identical on every request: last change, then record id.
        return (descending ? ordered.ThenByDescending(r => r.ModifiedOn).ThenByDescending(r => r.FormRecordId) : ordered.ThenBy(r => r.ModifiedOn).ThenBy(r => r.FormRecordId)).ToList();
    }

    /// <summary>Short, human-readable reference for a record id (display only; the full id stays authoritative).</summary>
    public static string ReferenceFor(Guid formRecordId) => "REC-" + formRecordId.ToString("N")[^8..].ToUpperInvariant();
}

/// <summary>Position of a page within one listing scope, bound to the caller and the authorization revision it was computed at.</summary>
public sealed record RecordListCursor(Guid OrganizationId, Guid UserId, long AuthorizationRevision, string ScopeDigest, int Offset)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public string Encode(IOpaqueCursorProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return protector.Protect(JsonSerializer.Serialize(new Payload(1, OrganizationId, UserId, AuthorizationRevision, ScopeDigest, Offset)), Lifetime);
    }

    /// <summary>
    /// Decodes and checks a client cursor: 400 <c>CURSOR_INVALID</c> when it is malformed, altered, expired or issued to
    /// another caller/organization; 400 <c>CURSOR_SCOPE_MISMATCH</c> when the filters or sort differ; 409
    /// <c>LIST_CHANGED</c> (retryable) when access changed since it was issued — the client restarts from the first page.
    /// </summary>
    public static RecordListCursor Decode(IOpaqueCursorProtector protector, string token, RequestContext context, RecordListQuery query, long currentAuthorizationRevision)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var json = token.Length is 0 or > 4096 ? null : protector.Unprotect(token);
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

        if (payload is null || payload.V != 1 || payload.O != context.OrganizationId || payload.U != context.LocalUserId || payload.S < 0)
        {
            throw new BusinessException("CURSOR_INVALID", 400, "this page link is not valid or has expired; reload the list");
        }

        if (!string.Equals(payload.Q, query.ScopeDigest(), StringComparison.Ordinal))
        {
            throw new BusinessException("CURSOR_SCOPE_MISMATCH", 400, "this page link belongs to different filters or sorting; reload the list");
        }

        if (payload.R != currentAuthorizationRevision)
        {
            throw new BusinessException("LIST_CHANGED", 409, "access changed since this page was loaded; reload the list from the first page");
        }

        return new RecordListCursor(payload.O, payload.U, payload.R, payload.Q, payload.S);
    }

    private sealed record Payload(int V, Guid O, Guid U, long R, string Q, int S);
}

/// <summary>Status literals used by listing (the lifecycle rules live in <c>Lifecycle.RecordLifecycle</c>).</summary>
internal static class RecordLifecycleStatuses
{
    public const string Archived = "Archived";
}
