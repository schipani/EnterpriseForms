using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// The exact verification of one stored entry against its accepted head and its ES256 seal (S-B, §17.10; and since IX-3 also
/// every record on a released page, §18.1.4). Pure: the caller supplies the rows it read (through whichever role it uses) and
/// gets a typed outcome. Never repairs anything. Extracted unchanged from the IX-1 sweep so there is one verification rule.
/// </summary>
public static class SearchEntryVerification
{
    public static (SweepUnitOutcome Outcome, string Code) Verify(
        SearchRecordEntry entry,
        IReadOnlyList<SearchPostingBase> postings,
        IReadOnlyList<SearchRecordFieldAnomaly> anomalies,
        Guid? headOperationId,
        long headContentRevision,
        byte[] headManifestRoot,
        IReadOnlyDictionary<string, byte[]> leafPayloadDigestsByKeyHex,
        IReadOnlyDictionary<Guid, FieldSearchApproval> approvals,
        SearchSealVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(anomalies);
        ArgumentNullException.ThrowIfNull(headManifestRoot);
        ArgumentNullException.ThrowIfNull(leafPayloadDigestsByKeyHex);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(verifier);
        if (headOperationId != entry.IndexedOperationId || headContentRevision != entry.IndexedContentRevision || !headManifestRoot.AsSpan().SequenceEqual(entry.IndexedManifestRoot))
        {
            return (SweepUnitOutcome.Fail, "ENTRY_NOT_AT_HEAD");
        }

        var bound = new List<FieldSearchApproval>();
        foreach (var id in entry.DerivedFieldSearchApprovalIds)
        {
            if (!approvals.TryGetValue(id, out var approval) || approval.OrganizationId != entry.OrganizationId || approval.FormDefinitionId != entry.FormDefinitionId)
            {
                return (SweepUnitOutcome.Fail, "APPROVAL_OF_ENTRY_MISSING");
            }

            bound.Add(approval);
        }

        var derived = new List<DerivedPosting>(postings.Count);
        try
        {
            foreach (var posting in postings)
            {
                var approval = bound.FirstOrDefault(a => a.FieldSearchApprovalId == posting.FieldSearchApprovalId && a.FieldDefinitionId == posting.FieldDefinitionId);
                if (approval is null || posting.IndexedOperationId != entry.IndexedOperationId || posting.OrganizationId != entry.OrganizationId || posting.FormDefinitionId != entry.FormDefinitionId
                    || posting.FormRecordId != entry.FormRecordId)
                {
                    return (SweepUnitOutcome.Fail, "POSTING_BINDING_MISMATCH");
                }

                var (value, document) = SearchPostingDerivationV1.ValueFromSource(posting.SourceCanonicalBytes, approval.ApprovedFieldDataType);
                if (document.Header.OrganizationId != entry.OrganizationId || SearchPostingDerivationV1.FieldOf(document) != posting.FieldDefinitionId
                    || SearchPostingDerivationV1.RecordOf(document) != entry.FormRecordId || SearchPostingDerivationV1.ItemOf(document) != posting.ManifestItemId
                    || document.Header.EntityKind != posting.SourceEntityKind || !TypedColumnMatches(posting, value))
                {
                    return (SweepUnitOutcome.Fail, "POSTING_VALUE_MISMATCH");
                }

                var digest = SHA256.HashData(posting.SourceCanonicalBytes);
                if (!leafPayloadDigestsByKeyHex.TryGetValue(Convert.ToHexStringLower(posting.ManifestItemKey), out var leafDigest) || !leafDigest.AsSpan().SequenceEqual(digest))
                {
                    return (SweepUnitOutcome.Fail, "POSTING_NOT_IN_ACCEPTED_MANIFEST");
                }

                derived.Add(new DerivedPosting(posting.FieldDefinitionId, posting.FieldSearchApprovalId, posting.ManifestItemKey, posting.ManifestItemId, posting.SourceEntityKind, approval.ApprovedFieldDataType, posting.SourceCanonicalBytes, digest, value));
            }

            var sealAnomalies = anomalies.Select(a => new SearchSealAnomaly(a.FieldDefinitionId, a.AnomalyKindName)).ToList();
            if (derived.Count != entry.IndexedPostingCount || sealAnomalies.Count != entry.IndexedAnomalyCount)
            {
                return (SweepUnitOutcome.Fail, "ENTRY_COUNT_MISMATCH");
            }

            // C-141 (UF-25): the per-field item counts are a function of the postings the seal binds; recounted here, so a changed
            // count fails with the seal check (keyed) exactly like a changed posting count.
            if (!SearchFieldItemCounts.MatchPostings(entry, derived.Select(p => p.FieldSearchApprovalId)))
            {
                return (SweepUnitOutcome.Fail, "ENTRY_FIELD_ITEM_COUNT_MISMATCH");
            }

            var message = SearchEntrySealBuilder.Message(entry, bound, derived, sealAnomalies);
            return verifier.Verify(entry.SealKeyName, entry.SealKeyVersion, message, entry.EntrySealSignature) switch
            {
                SealVerificationOutcome.Valid => (SweepUnitOutcome.Pass, "VALID"),
                SealVerificationOutcome.UnknownKey => (SweepUnitOutcome.Inconclusive, "SEAL_KEY_NOT_PINNED"),
                _ => (SweepUnitOutcome.Fail, "SEAL_INVALID"),
            };
        }
        catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.SearchIndexDerivationFailed)
        {
            return (SweepUnitOutcome.Fail, "POSTING_SOURCE_INVALID");
        }
        catch (IntegrityFormatException)
        {
            return (SweepUnitOutcome.Fail, "SEAL_MESSAGE_INVALID");
        }
        catch (System.Text.Json.JsonException)
        {
            return (SweepUnitOutcome.Fail, "APPROVAL_OPERATORS_INVALID");
        }
    }

    /// <summary>The typed value column of a posting equals the value decoded from its source bytes.</summary>
    public static bool TypedColumnMatches(SearchPostingBase posting, SearchSealValue value) => (posting, value) switch
    {
        (SearchStringPosting p, SearchSealValue.NormalizedString v) => string.Equals(p.NormalizedStringValue, v.Value, StringComparison.Ordinal),
        (SearchIntegerPosting p, SearchSealValue.IntegerValue v) => p.IntegerValue == v.Value,
        (SearchDecimalPosting p, SearchSealValue.DecimalParts v) => p.DecimalValue == SearchIndexValues.ToDecimal(v),
        (SearchDatePosting p, SearchSealValue.DateValue v) => p.DateValue == v.Value,
        (SearchDateTimePosting p, SearchSealValue.Timestamp v) => p.DateTimeValue.UtcTicks == v.Value.UtcTicks,
        (SearchBooleanPosting p, SearchSealValue.BooleanValue v) => p.BooleanValue == v.Value,
        (SearchOptionPosting p, SearchSealValue.OptionValue v) => p.FieldOptionId == v.FieldOptionId,
        _ => false,
    };
}
