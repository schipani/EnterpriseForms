using System.Globalization;
using System.Text.Json;
using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>One verified match as carried in a result cursor: what a My Records row needs, all from verified sources.</summary>
public sealed record FieldSearchMatch(Guid FormRecordId, DateTimeOffset CreatedOn, DateTimeOffset ModifiedOn, string RecordStatus, Guid CreatedByUserId, Guid? ParentFormRecordId);

/// <summary>
/// Phase C7 M3 — the result of one verified search, carried by the client between pages as an opaque, expiring token
/// (ASP.NET Core Data Protection, authenticated encryption, shared key ring). It is bound to the organization, the user, the
/// Authority catalogue snapshot and authorization revision it was verified at, and to a digest of the request; it holds the
/// sorted matches with their verified display facts, so later pages never re-read business rows or re-evaluate anything.
/// Every page re-validates the Authority snapshot: any authorization change (a revocation included) or a change to a
/// record in scope invalidates the cursor (409 <c>SEARCH_SNAPSHOT_CHANGED</c>; run the search again). The page offset is
/// outside the protected payload (<c>offset.payload</c>): changing it only selects another page of the caller's own result.
/// </summary>
public sealed record FieldSearchCursor(Guid OrganizationId, Guid UserId, Guid FormDefinitionId, Guid SearchCatalogueSnapshotId, long AuthorizationRevision, string RequestDigest, int PageSize, DateTimeOffset ExpiresOn,
    string ScopeStatement, IReadOnlyList<FieldSearchMatch> Matches)
{
    public const int Version = 1;

    /// <summary>Upper bound of an accepted token (500 matches fit well within it).</summary>
    public const int MaximumTokenLength = 200_000;

    public string Encode(IOpaqueCursorProtector protector, int offset, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var payload = new Payload("field-search", Version, OrganizationId, UserId, FormDefinitionId, SearchCatalogueSnapshotId, AuthorizationRevision, RequestDigest, PageSize, ExpiresOn, ScopeStatement, Pack(Matches));
        var lifetime = ExpiresOn - now;
        return offset.ToString(CultureInfo.InvariantCulture) + "." + protector.Protect(JsonSerializer.Serialize(payload), lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromSeconds(1));
    }

    /// <summary>Decodes a client token for this caller, or throws the typed refusal (never a widened or foreign result).</summary>
    public static (FieldSearchCursor Cursor, int Offset) Decode(IOpaqueCursorProtector protector, string? token, RequestContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(context);
        var dot = token?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (token is null || token.Length > MaximumTokenLength || dot < 1 || !int.TryParse(token.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            throw SearchException.RequestInvalid("this result page is not valid; run the search again");
        }

        Payload? payload = null;
        if (protector.Unprotect(token[(dot + 1)..]) is { } json)
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

        if (payload is null || payload.K != "field-search" || payload.V != Version || payload.O != context.OrganizationId || payload.U != context.LocalUserId || payload.X <= now)
        {
            // Malformed, altered, expired or another caller's: the result cannot be paged consistently.
            throw SearchException.SnapshotChanged();
        }

        var matches = Unpack(payload.M);
        if (offset > matches.Count)
        {
            throw SearchException.RequestInvalid("this result page is not valid; run the search again");
        }

        return (new FieldSearchCursor(payload.O, payload.U, payload.F, payload.S, payload.R, payload.D, payload.P, payload.X, payload.T, matches), offset);
    }

    private static readonly string[] Statuses = ["Draft", "Submitted", "Archived", "Deleted"];

    private static string Pack(IReadOnlyList<FieldSearchMatch> matches)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(matches.Count);
            foreach (var m in matches)
            {
                writer.Write(m.FormRecordId.ToByteArray());
                writer.Write(m.CreatedOn.UtcTicks);
                writer.Write(m.ModifiedOn.UtcTicks);
                var status = Array.IndexOf(Statuses, m.RecordStatus);
                writer.Write((byte)(status < 0 ? 255 : status));
                if (status < 0)
                {
                    writer.Write(m.RecordStatus);
                }

                writer.Write(m.CreatedByUserId.ToByteArray());
                writer.Write((m.ParentFormRecordId ?? Guid.Empty).ToByteArray());
            }
        }

        return Convert.ToBase64String(stream.ToArray());
    }

    private static List<FieldSearchMatch> Unpack(string packed)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(Convert.FromBase64String(packed)));
            var count = reader.ReadInt32();
            if (count is < 0 or > FieldSearchLimits.ApprovedMaximumEligibleRecords)
            {
                throw SearchException.SnapshotChanged();
            }

            var matches = new List<FieldSearchMatch>(count);
            for (var i = 0; i < count; i++)
            {
                var id = new Guid(reader.ReadBytes(16));
                var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var modified = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var code = reader.ReadByte();
                var status = code == 255 ? reader.ReadString() : Statuses[code];
                var creator = new Guid(reader.ReadBytes(16));
                var parent = new Guid(reader.ReadBytes(16));
                matches.Add(new FieldSearchMatch(id, created, modified, status, creator, parent == Guid.Empty ? null : parent));
            }

            return matches;
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or IndexOutOfRangeException or ArgumentException)
        {
            throw SearchException.SnapshotChanged();
        }
    }

    /// <summary>K kind, V version, O organization, U user, F form, S snapshot, R authorization revision, D request digest, P page size, X expiry, T scope statement, M matches.</summary>
    private sealed record Payload(string K, int V, Guid O, Guid U, Guid F, Guid S, long R, string D, int P, DateTimeOffset X, string T, string M);
}
