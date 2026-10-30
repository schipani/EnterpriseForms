using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseForms.Application.Common.Paging;

/// <summary>
/// Opaque, expiring page cursor for the dashboard sections and the incident list (PB-06): bound to the caller, the
/// organization, the list name and a digest of its filters. It only locates a position; every page re-runs the
/// authorized query, so a cursor never grants anything. Decoding fails with 400 <c>CURSOR_INVALID</c> (malformed,
/// altered, expired, another caller) or 400 <c>CURSOR_SCOPE_MISMATCH</c> (different list or filters).
/// </summary>
public sealed record OffsetPageCursor(Guid OrganizationId, Guid UserId, string ScopeDigest, int Offset)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const int DefaultPageSize = 15;

    public const int MaximumPageSize = 50;

    public static int ClampPageSize(int? pageSize) => Math.Clamp(pageSize ?? DefaultPageSize, 1, MaximumPageSize);

    /// <summary>Digest of a list name plus its normalized filter values (order matters).</summary>
    public static string Digest(string listName, params object?[] parts)
    {
        var text = listName + "|" + string.Join('|', parts.Select(p => p switch
        {
            null => "-",
            DateTimeOffset d => d.UtcTicks.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    public string Encode(IOpaqueCursorProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return protector.Protect(JsonSerializer.Serialize(new Payload(1, OrganizationId, UserId, ScopeDigest, Offset)), Lifetime);
    }

    /// <summary>The offset of a client cursor (0 when absent), after checking caller, organization and scope.</summary>
    public static int DecodeOffset(IOpaqueCursorProtector protector, string? token, RequestContext context, string scopeDigest)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(token))
        {
            return 0;
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

        if (payload is null || payload.V != 1 || payload.O != context.OrganizationId || payload.U != context.LocalUserId || payload.S < 0)
        {
            throw new BusinessException("CURSOR_INVALID", 400, "this page link is not valid or has expired; reload the list");
        }

        if (!string.Equals(payload.Q, scopeDigest, StringComparison.Ordinal))
        {
            throw new BusinessException("CURSOR_SCOPE_MISMATCH", 400, "this page link belongs to different filters; reload the list");
        }

        return payload.S;
    }

    /// <summary>Next and previous tokens for a page that starts at <paramref name="offset"/> and returned <paramref name="returned"/> rows of <paramref name="total"/>.</summary>
    public static (string? Next, string? Previous) Links(IOpaqueCursorProtector protector, RequestContext context, string scopeDigest, int offset, int returned, int pageSize, int total)
    {
        ArgumentNullException.ThrowIfNull(context);
        var next = offset + returned < total ? new OffsetPageCursor(context.OrganizationId, context.LocalUserId, scopeDigest, offset + returned).Encode(protector) : null;
        var previous = offset > 0 ? new OffsetPageCursor(context.OrganizationId, context.LocalUserId, scopeDigest, Math.Max(0, offset - pageSize)).Encode(protector) : null;
        return (next, previous);
    }

    private sealed record Payload(int V, Guid O, Guid U, string Q, int S);
}
