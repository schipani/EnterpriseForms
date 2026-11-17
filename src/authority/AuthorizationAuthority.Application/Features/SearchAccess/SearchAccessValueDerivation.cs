using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>
/// IX-5b (C-148, B2): the pure derivation of access-scoped value postings. For each searchable value of a record (a Tier 1 posting
/// at the accepted head under a Complete Public/Internal coverage), one row per principal that can read its item: every effective
/// owner (owners read every item) and every principal of a share covering the item. A principal reached by several paths gets
/// one row (the distinct-item union, INV-T2-13). Teams stay principals (D-1). No other value can enter a row.
/// </summary>
public static class SearchAccessValueDerivation
{
    public static List<SearchAccessValuePosting> Derive(Guid organizationId, IEnumerable<SearchAccessRecordGrant> recordGrants, IEnumerable<SearchAccessFieldItemGrant> itemGrants, IEnumerable<SearchablePosting> postings, long contentRevision)
    {
        var owners = recordGrants.Where(g => g.AccessKindName == SearchAccessKinds.EffectiveOwner)
            .GroupBy(g => g.FormRecordId).ToDictionary(g => g.Key, g => g.Select(x => (x.AccessPrincipalTypeName, x.AccessPrincipalEntityId)).ToHashSet());
        var readers = itemGrants.GroupBy(g => (g.FormRecordId, g.ManifestItemId))
            .ToDictionary(g => g.Key, g => g.Select(x => (x.AccessPrincipalTypeName, x.AccessPrincipalEntityId)).ToHashSet());
        var rows = new List<SearchAccessValuePosting>();
        foreach (var posting in postings)
        {
            var principals = new HashSet<(string, Guid)>();
            if (owners.TryGetValue(posting.FormRecordId, out var o))
            {
                principals.UnionWith(o);
            }

            if (readers.TryGetValue((posting.FormRecordId, posting.ManifestItemId), out var r))
            {
                principals.UnionWith(r);
            }

            foreach (var (type, id) in principals)
            {
                rows.Add(new SearchAccessValuePosting
                {
                    SearchAccessValuePostingId = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    AccessPrincipalTypeName = type,
                    AccessPrincipalEntityId = id,
                    FormDefinitionId = posting.FormDefinitionId,
                    FieldDefinitionId = posting.FieldDefinitionId,
                    FieldSearchApprovalId = posting.FieldSearchApprovalId,
                    FormRecordId = posting.FormRecordId,
                    ManifestItemId = posting.ManifestItemId,
                    IndexedOperationId = posting.IndexedOperationId,
                    ValueTypeName = posting.ValueTypeName,
                    NormalizedStringValue = posting.StringValue,
                    IntegerValue = posting.IntegerValue,
                    DecimalValue = posting.DecimalValue,
                    DateValue = posting.DateValue,
                    DateTimeValue = posting.DateTimeValue,
                    BooleanValue = posting.BooleanValue,
                    FieldOptionId = posting.FieldOptionId,
                    DerivedContentIntegrityRevision = contentRevision,
                });
            }
        }

        return rows;
    }

    /// <summary>A comparable key (surrogate ids and the derivation revision excluded).</summary>
    public static string Key(SearchAccessValuePosting v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return string.Join('|', v.AccessPrincipalTypeName, v.AccessPrincipalEntityId.ToString("D"), v.FormRecordId.ToString("D"), v.ManifestItemId.ToString("D"), v.FieldDefinitionId.ToString("D"),
            v.FieldSearchApprovalId.ToString("D"), v.IndexedOperationId.ToString("D"), v.ValueTypeName, v.NormalizedStringValue ?? "~", v.IntegerValue?.ToString(CultureInfo.InvariantCulture) ?? "~",
            v.DecimalValue?.ToString(CultureInfo.InvariantCulture) ?? "~", v.DateValue?.ToString("O", CultureInfo.InvariantCulture) ?? "~", v.DateTimeValue?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "~",
            v.BooleanValue?.ToString() ?? "~", v.FieldOptionId?.ToString("D") ?? "~");
    }

    /// <summary>SHA-256 over the sorted Complete Public/Internal coverages; null when there are none.</summary>
    public static byte[]? CoverageDigest(IEnumerable<SearchableCoverage> coverages)
    {
        var lines = coverages.Select(c => $"{c.FieldSearchApprovalId:D}|{c.FieldDefinitionId:D}|{c.FormDefinitionId:D}").Order(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? null : SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }

    public static bool DigestsEqual(byte[]? left, byte[]? right) =>
        (left is null || left.Length == 0) ? (right is null || right.Length == 0) : right is not null && left.AsSpan().SequenceEqual(right);
}
