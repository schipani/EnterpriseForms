using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.Staging;

/// <summary>Maps repeating-section wire requests to builder input, and builder changes to audit metadata (ids and counts, never values).</summary>
public static class RecordGroupWrites
{
    public static IReadOnlyList<CandidateBuilder.GroupWrite>? From(IReadOnlyList<RecordGroupValuesRequest>? groups) =>
        groups?.Select(g => new CandidateBuilder.GroupWrite(g?.GroupKey ?? string.Empty, g?.Occurrences?.Select(o => o is null ? null! : new CandidateBuilder.OccurrenceWrite(o.FormRecordGroupId, o.Values)).ToList()!)).ToList();

    public static IReadOnlyList<object> AuditOf(IReadOnlyList<CandidateBuilder.GroupChange> changes) =>
        changes?.Select(c => (object)new { groupKey = c.GroupKey, added = c.AddedOccurrenceIds, removed = c.RemovedOccurrenceIds, reordered = c.Reordered, entryCount = c.OccurrenceCount }).ToList() ?? [];
}
