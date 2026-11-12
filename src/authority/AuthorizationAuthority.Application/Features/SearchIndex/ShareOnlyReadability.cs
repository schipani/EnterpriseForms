using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>The item-count facts of one share-only record's entry, as read at the reader snapshot.</summary>
public sealed record ShareOnlyRecordCounts(Guid FormRecordId, int? EntryDerivationVersion, Guid[]? DerivedFieldSearchApprovalIds, int[]? DerivedFieldItemCounts);

/// <summary>
/// One field item the caller may read through one of its current shares (canonical edges at the pinned authorization revision),
/// joined by item id to a current posting of a predicate field of a share-only record. Ids only, never values.
/// </summary>
public sealed record ShareReadableItem(Guid FormRecordId, Guid FieldDefinitionId, Guid ManifestItemId, Guid FieldSearchApprovalId);

/// <summary>The outcome class of a share-only readability step.</summary>
public enum ShareOnlyOutcome
{
    /// <summary>Decided: <see cref="ShareOnlyDecision.Evaluable"/> holds the records whose predicate fields are completely readable.</summary>
    Decided,

    /// <summary>A record's entry predates the item counts (derivation version 2): refused as not ready, retryable, no quarantine.</summary>
    NotReady,

    /// <summary>The index contradicts itself: the search index is quarantined (after the Q4 escalation where it applies).</summary>
    IndexFinding,
}

public sealed record ShareOnlyDecision(ShareOnlyOutcome Outcome, string? FindingCode, IReadOnlySet<Guid> Evaluable, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> ConfirmedItemsByRecord)
{
    public static ShareOnlyDecision Refused(ShareOnlyOutcome outcome, string code) => new(outcome, code, new HashSet<Guid>(), new Dictionary<Guid, IReadOnlySet<Guid>>());
}

/// <summary>
/// C-141 (UF-25, U-1b): the readable-for-predicate rule of R1 §E.3 for share-only records, decided without reading any item the
/// caller cannot read.
/// <para>
/// <b>Unchanged semantics.</b> A share-only record r is evaluable on predicate field f iff every indexed item of f in r is readable
/// by the caller; r is in scope iff it is evaluable on every predicate field. A field with no item is vacuously readable.
/// </para>
/// <para>
/// <b>New evaluation.</b> The candidates are the items the caller's own current shares reach (direct, and through every team the
/// caller is a member of), already restricted to current postings of the predicate fields; SpiceDB confirms each one. r is evaluable
/// on f iff the number of <i>distinct</i> confirmed items of f in r equals the accepted total count of f in r
/// (<see cref="SearchFieldItemCounts"/>). The union over all paths is taken before the comparison, so completeness is never inferred
/// from one path. For a share-only record, <c>field_item#read = shared_via-&gt;read</c> (the schema's other branch needs ownership,
/// which r lacks), so with S1 consistent the confirmed set is exactly the readable postings and the two rules decide the same scope.
/// </para>
/// <para>
/// <b>Fail closed.</b> Missing, malformed or contradictory counts, more readable items than the total, or a candidate outside the
/// requested records or fields are index findings; a version 2 entry is not ready. No partial decision is ever returned.
/// </para>
/// </summary>
public static class ShareOnlyReadability
{
    /// <summary>
    /// Validates every share-only record's count metadata for every predicate approval, before any candidate is read.
    /// Index findings take precedence over not-ready.
    /// </summary>
    public static ShareOnlyDecision? ValidateCounts(IReadOnlyCollection<ShareOnlyRecordCounts> records, IReadOnlyDictionary<Guid, Guid> predicateApprovalByField)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(predicateApprovalByField);
        var notReady = false;
        foreach (var record in records)
        {
            switch (record.EntryDerivationVersion)
            {
                case null:
                    return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_CONTENTFUL_RECORD_WITHOUT_ENTRY");
                case < SearchPostingDerivationV1.HeaderFactsEntryDerivationVersion:
                    return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_DERIVATION_OUTDATED");
                case < SearchPostingDerivationV1.FieldItemCountEntryDerivationVersion:
                    if (record.DerivedFieldItemCounts is not null)
                    {
                        return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_UNEXPECTED");
                    }

                    notReady = true;
                    continue;
            }

            if (record.DerivedFieldSearchApprovalIds is not { } approvals || record.DerivedFieldItemCounts is not { } counts || counts.Length != approvals.Length || counts.Any(c => c < 0))
            {
                return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_INVALID");
            }

            foreach (var approval in predicateApprovalByField.Values)
            {
                if (!SearchFieldItemCounts.TryGet(approvals, counts, approval, out _))
                {
                    return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNT_MISSING");
                }
            }
        }

        return notReady ? ShareOnlyDecision.Refused(ShareOnlyOutcome.NotReady, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_PENDING_UPGRADE") : null;
    }

    /// <summary>
    /// Decides the evaluable share-only records from the SpiceDB-confirmed candidates. <paramref name="records"/> must have passed
    /// <see cref="ValidateCounts"/>.
    /// </summary>
    public static ShareOnlyDecision Decide(IReadOnlyCollection<ShareOnlyRecordCounts> records, IReadOnlyDictionary<Guid, Guid> predicateApprovalByField, IReadOnlyCollection<ShareReadableItem> confirmed)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(predicateApprovalByField);
        ArgumentNullException.ThrowIfNull(confirmed);
        var byId = records.ToDictionary(r => r.FormRecordId);
        var distinct = new Dictionary<(Guid Record, Guid Field), HashSet<Guid>>();
        var itemsByRecord = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var item in confirmed)
        {
            if (!byId.ContainsKey(item.FormRecordId) || !predicateApprovalByField.TryGetValue(item.FieldDefinitionId, out var expectedApproval) || expectedApproval != item.FieldSearchApprovalId)
            {
                return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_CANDIDATE_OUTSIDE_SCOPE");
            }

            if (!distinct.TryGetValue((item.FormRecordId, item.FieldDefinitionId), out var set))
            {
                distinct[(item.FormRecordId, item.FieldDefinitionId)] = set = [];
            }

            set.Add(item.ManifestItemId);
            if (!itemsByRecord.TryGetValue(item.FormRecordId, out var all))
            {
                itemsByRecord[item.FormRecordId] = all = [];
            }

            all.Add(item.ManifestItemId);
        }

        var evaluable = new HashSet<Guid>();
        foreach (var record in records)
        {
            var complete = true;
            foreach (var (field, approval) in predicateApprovalByField)
            {
                if (!SearchFieldItemCounts.TryGet(record.DerivedFieldSearchApprovalIds, record.DerivedFieldItemCounts, approval, out var total))
                {
                    return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNT_MISSING");
                }

                var readable = distinct.TryGetValue((record.FormRecordId, field), out var set) ? set.Count : 0;
                if (readable > total)
                {
                    return ShareOnlyDecision.Refused(ShareOnlyOutcome.IndexFinding, "SCOPE_READABLE_ITEMS_EXCEED_COUNT");
                }

                complete &= readable == total;
            }

            if (complete)
            {
                evaluable.Add(record.FormRecordId);
            }
        }

        return new ShareOnlyDecision(ShareOnlyOutcome.Decided, null, evaluable, itemsByRecord.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<Guid>)kv.Value));
    }

    /// <summary>
    /// The per-record check on bound data (search matches and served page records): for every predicate field, the record's bound
    /// postings are exactly as many as its stored count and every one of them was confirmed readable. Run after I-2, so the
    /// postings are already bound to the accepted manifest. Returns null when it holds, otherwise the finding code. For an honest
    /// evaluable record this touches readable items only (every posting of the field is readable by construction).
    /// </summary>
    public static string? VerifyBoundRecord(SearchRecordBindingData data, IReadOnlyDictionary<Guid, Guid> predicateApprovalByField, IReadOnlySet<Guid>? confirmedItems)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(predicateApprovalByField);
        foreach (var (field, approval) in predicateApprovalByField)
        {
            if (!SearchFieldItemCounts.TryGet(data.Entry.DerivedFieldSearchApprovalIds, data.Entry.DerivedFieldItemCounts, approval, out var total))
            {
                return "MATCH_FIELD_ITEM_COUNT_MISSING";
            }

            var postings = data.Postings.Where(p => p.FieldDefinitionId == field).ToList();
            if (postings.Count != total)
            {
                return "MATCH_FIELD_ITEM_COUNT_MISMATCH";
            }

            if (postings.Any(p => confirmedItems is null || !confirmedItems.Contains(p.ManifestItemId)))
            {
                return "MATCH_PREDICATE_ITEM_NOT_READABLE";
            }
        }

        return null;
    }
}
