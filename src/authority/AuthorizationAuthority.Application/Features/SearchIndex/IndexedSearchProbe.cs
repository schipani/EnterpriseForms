namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// One share-only readability step, as observed (counts and a code only; no identifiers of records, fields or items, no values).
/// <paramref name="ShareOnlyRecords"/> is |S| (the caller's share-only records of the form), <paramref name="CandidateItems"/> the
/// rows read from the caller's own grants, <paramref name="ItemChecksIssued"/> the field_item#read checks sent to SpiceDB.
/// </summary>
public sealed record ShareOnlyProbeObservation(string Path, int ShareOnlyRecords, int CandidateItems, int ItemChecksIssued, int EvaluableRecords, string OutcomeCode);

/// <summary>
/// C-141 (RQ-2): Testing environment only. Lets the focused UF-25 regression tests read the exact number of item checks a search
/// issued, by correlation id. Not registered outside Testing, where <see cref="IndexedSearchService"/> receives null.
/// </summary>
public interface IIndexedSearchProbe
{
    void ShareOnlyStep(Guid correlationId, ShareOnlyProbeObservation observation);
}
