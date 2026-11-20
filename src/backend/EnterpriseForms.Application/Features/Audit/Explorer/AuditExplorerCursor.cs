using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;

namespace EnterpriseForms.Application.Features.Audit.Explorer;

/// <summary>
/// C8 M4-2 keyset cursor of the audit explorer (AC-8 R-8): authenticated-encrypted by <see cref="IOpaqueCursorProtector"/>, expiring,
/// and bound to the organization, the caller and the filter digest. It only locates a position (event time and sequence of the last
/// row served); it never authorizes — every page is authorized and recorded again by the Authority. Any malformed, altered, expired
/// or foreign cursor (another caller, organization or filter) is 400 <c>CURSOR_INVALID</c>.
/// </summary>
public static class AuditExplorerCursor
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private const int Version = 1;

    public static string Encode(IOpaqueCursorProtector protector, RequestContext context, string filterDigest, AuditEventPosition position)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(position);
        return protector.Protect(JsonSerializer.Serialize(new Payload(Version, context.OrganizationId, context.LocalUserId, filterDigest, position.OccurredOn.UtcTicks, position.Sequence)), Lifetime);
    }

    /// <summary>The position after which the next page starts, or null for the first page (no cursor).</summary>
    public static AuditEventPosition? Decode(IOpaqueCursorProtector protector, string? token, RequestContext context, string filterDigest)
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
            || payload.T is < 0 || payload.T > DateTimeOffset.MaxValue.UtcTicks || payload.S < 0)
        {
            throw new BusinessException("CURSOR_INVALID", 400, "this page link is not valid for you, these filters or any more; reload the list");
        }

        return new AuditEventPosition(new DateTimeOffset(payload.T, TimeSpan.Zero), payload.S);
    }

    private sealed record Payload(int V, Guid O, Guid U, string Q, long T, long S);
}
