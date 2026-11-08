using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Merkle;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>The outcome of I-2 for one candidate: either the verified header facts, or the finding code that fails the search.</summary>
public sealed record IndexedMatchVerification(bool Bound, string? FindingCode, DerivedHeaderFacts? Header);

/// <summary>
/// IX-3 I-2 (design §6.3, OR-1 §18.1.1): per candidate record, every predicate-field posting is bound to the accepted manifest
/// by CPU only — SHA-256 of its source bytes equals the payload digest of the leaf with the same item key in the head's current
/// commit; the source bytes decode to the posting's organization, record, field, item and typed value; and the commit's leaf set
/// recomputes the head's Merkle root with the recorded leaf count. The header bytes are bound the same way and decoded for the
/// metadata facts. The predicate is then re-evaluated on the decoded values; a candidate the SQL returned whose verified values do
/// not satisfy it is index damage. Unkeyed: a consistency check against AuthorityDb rows, never protection against a writer
/// holding the runtime credential (§18.1.1); the keyed check is the per-page seal verification.
/// </summary>
public static class IndexedSearchBinding
{
    public static IndexedMatchVerification VerifyMatch(SearchRecordBindingData data, IReadOnlyList<PreparedIndexedCondition> conditions, IReadOnlyDictionary<Guid, string> predicateValueTypes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(predicateValueTypes);
        var entry = data.Entry;
        if (data.HeadOperationId != entry.IndexedOperationId || data.HeadContentRevision != entry.IndexedContentRevision || !data.HeadManifestRoot.AsSpan().SequenceEqual(entry.IndexedManifestRoot))
        {
            return Fail("MATCH_ENTRY_NOT_AT_HEAD");
        }

        if (entry.EntryDerivationVersion < SearchPostingDerivationV1.CurrentEntryDerivationVersion || entry.HeaderSourceCanonicalBytes is not { Length: > 0 } headerBytes)
        {
            return Fail("MATCH_ENTRY_DERIVATION_OUTDATED");
        }

        if (data.CurrentRecordManifestCommitId is null || data.ManifestRootDigest is null || data.ManifestLeafCount is null
            || !data.ManifestRootDigest.AsSpan().SequenceEqual(data.HeadManifestRoot) || data.ManifestLeafCount != data.Leaves.Count)
        {
            return Fail("MATCH_MANIFEST_COMMIT_MISMATCH");
        }

        // Root: the commit's leaves, with the head's revision and operation (as the commit built them), recompute the head root.
        var leafInputs = new List<MerkleLeafInput>(data.Leaves.Count);
        var digestByKey = new Dictionary<string, byte[]>(data.Leaves.Count, StringComparer.Ordinal);
        byte[]? headerLeafDigest = null;
        try
        {
            foreach (var leaf in data.Leaves)
            {
                var kind = (MerkleItemKind)leaf.ManifestItemKindCode;
                if (!Enum.IsDefined(kind) || !MerkleManifestV1.ItemKey(kind, leaf.ManifestItemId).AsSpan().SequenceEqual(leaf.ManifestItemKey))
                {
                    return Fail("MATCH_LEAF_KEY_INVALID");
                }

                if (!digestByKey.TryAdd(Convert.ToHexStringLower(leaf.ManifestItemKey), leaf.CanonicalPayloadDigest))
                {
                    return Fail("MATCH_LEAF_DUPLICATE");
                }

                if (kind == MerkleItemKind.RecordHeader)
                {
                    if (headerLeafDigest is not null)
                    {
                        return Fail("MATCH_HEADER_LEAF_DUPLICATE");
                    }

                    headerLeafDigest = leaf.CanonicalPayloadDigest;
                }

                leafInputs.Add(new MerkleLeafInput(kind, leaf.ManifestItemId, leaf.RowSignature, leaf.CanonicalPayloadDigest, (ulong)data.HeadContentRevision, data.HeadOperationId!.Value));
            }

            if (!MerkleManifestV1.Build(leafInputs).MerkleRoot.AsSpan().SequenceEqual(data.HeadManifestRoot))
            {
                return Fail("MATCH_ROOT_MISMATCH");
            }
        }
        catch (IntegrityFormatException)
        {
            return Fail("MATCH_LEAF_INVALID");
        }

        // Header: bound to the header leaf, decoded, equal to the entry's stored facts.
        if (headerLeafDigest is null || !SHA256.HashData(headerBytes).AsSpan().SequenceEqual(headerLeafDigest))
        {
            return Fail("MATCH_HEADER_NOT_IN_ACCEPTED_MANIFEST");
        }

        DerivedHeaderFacts header;
        try
        {
            header = SearchPostingDerivationV1.HeaderFacts(headerBytes);
        }
        catch (AuthorityException)
        {
            return Fail("MATCH_HEADER_INVALID");
        }

        if (header.FormDefinitionId != entry.FormDefinitionId || !string.Equals(header.RecordStatusName, entry.RecordStatusName, StringComparison.Ordinal)
            || header.ParentFormRecordId != entry.ParentFormRecordId || header.CreatedByUserId != entry.CreatedByBusinessUserId)
        {
            return Fail("MATCH_HEADER_FACT_MISMATCH");
        }

        // Postings of the predicate fields: bound to their leaves, decoded, typed column equal to the decoded value.
        var values = new Dictionary<Guid, IReadOnlyList<SearchSealValue>>();
        var collected = predicateValueTypes.Keys.ToDictionary(f => f, _ => new List<SearchSealValue>());
        foreach (var posting in data.Postings)
        {
            if (!predicateValueTypes.TryGetValue(posting.FieldDefinitionId, out var valueType))
            {
                continue;
            }

            if (posting.OrganizationId != entry.OrganizationId || posting.FormDefinitionId != entry.FormDefinitionId || posting.FormRecordId != entry.FormRecordId
                || posting.IndexedOperationId != entry.IndexedOperationId || !entry.DerivedFieldSearchApprovalIds.Contains(posting.FieldSearchApprovalId))
            {
                return Fail("MATCH_POSTING_BINDING_MISMATCH");
            }

            if (!digestByKey.TryGetValue(Convert.ToHexStringLower(posting.ManifestItemKey), out var leafDigest) || !SHA256.HashData(posting.SourceCanonicalBytes).AsSpan().SequenceEqual(leafDigest))
            {
                return Fail("MATCH_POSTING_NOT_IN_ACCEPTED_MANIFEST");
            }

            try
            {
                var (value, document) = SearchPostingDerivationV1.ValueFromSource(posting.SourceCanonicalBytes, valueType);
                if (document.Header.OrganizationId != entry.OrganizationId || SearchPostingDerivationV1.FieldOf(document) != posting.FieldDefinitionId
                    || SearchPostingDerivationV1.RecordOf(document) != entry.FormRecordId || SearchPostingDerivationV1.ItemOf(document) != posting.ManifestItemId
                    || document.Header.EntityKind != posting.SourceEntityKind || !SearchEntryVerification.TypedColumnMatches(posting, value)
                    || document.Column("form_record_group_id") is not { IsNull: true })
                {
                    return Fail("MATCH_POSTING_VALUE_MISMATCH");
                }

                collected[posting.FieldDefinitionId].Add(value);
            }
            catch (AuthorityException)
            {
                return Fail("MATCH_POSTING_SOURCE_INVALID");
            }
            catch (IntegrityFormatException)
            {
                return Fail("MATCH_POSTING_SOURCE_INVALID");
            }
        }

        foreach (var (field, list) in collected)
        {
            values[field] = list;
        }

        return IndexedSearchPredicates.Matches(conditions, values)
            ? new IndexedMatchVerification(true, null, header)
            : Fail("MATCH_PREDICATE_NOT_SATISFIED_BY_VERIFIED_VALUE");
    }

    /// <summary>
    /// §18.1.4 step 4 for a page record: the predicate holds on the values bound by the (just verified) seal — the same postings,
    /// so a re-evaluation on the decoded values of every predicate-field posting of the sealed entry.
    /// </summary>
    public static bool PredicateHoldsOnSealedValues(SearchRecordBindingData data, IReadOnlyList<PreparedIndexedCondition> conditions, IReadOnlyDictionary<Guid, string> predicateValueTypes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(predicateValueTypes);
        var values = predicateValueTypes.Keys.ToDictionary(f => f, _ => (IReadOnlyList<SearchSealValue>)new List<SearchSealValue>());
        foreach (var group in data.Postings.Where(p => predicateValueTypes.ContainsKey(p.FieldDefinitionId)).GroupBy(p => p.FieldDefinitionId))
        {
            values[group.Key] = group.Select(p => SearchPostingDerivationV1.ValueFromSource(p.SourceCanonicalBytes, predicateValueTypes[group.Key]).Value).ToList();
        }

        return IndexedSearchPredicates.Matches(conditions, values);
    }

    private static IndexedMatchVerification Fail(string code) => new(false, code, null);
}
