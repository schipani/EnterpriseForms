using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.History;

/// <summary>
/// Phase C6 M3 restore plan (architecture §4.2): turns one verified earlier version into a COMPLETE owner write applied to
/// the verified current head, so the ordinary owner write path does the rest (validation against the current form,
/// protection under the current field policy, identity, prepare/stage/commit).
/// <list type="bullet">
/// <item><b>Exact (OD-11):</b> every field gets the source's value; a field with a value now but none in the source is cleared.
/// Repeating sections get the source's entries in the source's order.</item>
/// <item><b>Identity rule I-R:</b> the write never names a source-only item id. A value of a field that has a value now keeps
/// the current item (as any owner edit); a value or entry that exists only in the source is written as a NEW item, so a share
/// edge left behind by a removed item can never cover a restored value. Entries are matched by their stable id only.</item>
/// <item><b>Not restored:</b> documents (carried from the current head by the caller), related records, header status,
/// owners, shares, protection policy and classification.</item>
/// <item><b>OD-12:</b> values of fields that are still defined but no longer placed are restored (type-checked) and flagged.</item>
/// <item><b>OD-13:</b> a value that was stored encrypted in the source and would now be stored as plaintext (the field was
/// released since) is listed in <see cref="RestorePlan.PlaintextAcknowledgementFieldKeys"/>; the caller requires an explicit
/// acknowledgement. The current field policy (and with it the organization floor) always decides how a value is stored.</item>
/// </list>
/// Source values are decrypted here (after the Authority verified the version); an unavailable or unusable key fails the whole
/// restore with <c>HISTORIC_KEY_UNAVAILABLE</c>. Nothing here writes.
/// </summary>
public static class RestorePlanner
{
    public static class Changes
    {
        public const string Unchanged = "Unchanged";
        public const string Changed = "Changed";
        public const string Added = "Added";
        public const string Cleared = "Cleared";
    }

    public sealed record Snapshot(IReadOnlyList<FieldValueRowBase> Values, IReadOnlyList<FormRecordGroupRevision> Occurrences);

    public sealed record RestorePlan(
        Dictionary<string, string> Values,
        List<CandidateBuilder.GroupWrite> GroupWrites,
        List<RestoreFieldChangeResponse> Fields,
        List<RestoreSectionChangeResponse> Sections,
        List<string> PlaintextAcknowledgementFieldKeys,
        List<string> ChangedFieldKeys,
        List<string> ClearedFieldKeys,
        string? Unsupported)
    {
        public bool HasChanges => Values.Count > 0 || GroupWrites.Count > 0;
    }

    public static RestorePlan Build(
        Snapshot current,
        Snapshot source,
        FormLayout layout,
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyList<GroupDefinition> groups,
        IReadOnlyDictionary<Guid, FieldOption> optionsById,
        Encryption.IFieldEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(groups);
        var fieldById = fields.ToDictionary(f => f.FieldDefinitionId);
        var placedFields = layout.Elements.Where(e => e.FieldDefinitionId is not null).Select(e => e.FieldDefinitionId!.Value).ToHashSet();
        var placedGroups = layout.Sections.Where(s => s.Repeat is not null).ToDictionary(s => s.Repeat!.GroupDefinitionId, s => s);
        var groupById = groups.ToDictionary(g => g.GroupDefinitionId);
        var plan = new RestorePlan([], [], [], [], [], [], [], null);

        string Wire(FieldDefinition field, IEnumerable<FieldValueRowBase> rows, bool fromSource)
        {
            try
            {
                return FieldValueCodec.FormatField(field, rows, optionsById, encryption);
            }
            catch (Exception ex) when (ex is Documents.Ports.EncryptionUnavailableException or CryptographicException)
            {
                // The rows verified; an envelope that cannot be opened means its key is unavailable (no historic KEK retention yet, R-08).
                throw new BusinessException(fromSource ? "HISTORIC_KEY_UNAVAILABLE" : "ENCRYPTION_UNAVAILABLE", 503, "a key needed to read this version is not available; nothing was restored");
            }
        }

        bool Restorable(FieldDefinition field) => field.FieldDataType != FieldDataTypes.File;

        // Compares one scope (top level or one entry) and records the field rows of the preview; returns the write for that scope.
        Dictionary<string, string> CompareScope(IReadOnlyList<FieldValueRowBase> currentRows, IReadOnlyList<FieldValueRowBase> sourceRows, Guid? groupDefinitionId, string path, bool newEntry)
        {
            var write = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = currentRows.Select(r => r.FieldDefinitionId).Concat(sourceRows.Select(r => r.FieldDefinitionId)).Distinct()
                .Where(id => fieldById.TryGetValue(id, out var f) && f.GroupDefinitionId == groupDefinitionId && Restorable(f))
                .OrderBy(id => fieldById[id].FieldKey, StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var field = fieldById[id];
                var currentField = currentRows.Where(r => r.FieldDefinitionId == id).ToList();
                var sourceField = sourceRows.Where(r => r.FieldDefinitionId == id).ToList();
                var currentWire = Wire(field, currentField, false);
                var sourceWire = Wire(field, sourceField, true);
                var change = string.Equals(currentWire, sourceWire, StringComparison.Ordinal) ? Changes.Unchanged
                    : sourceWire.Length == 0 ? Changes.Cleared
                    : currentWire.Length == 0 ? Changes.Added
                    : Changes.Changed;
                var willEncrypt = field.FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted;
                var needsAck = change is Changes.Added or Changes.Changed && !willEncrypt && sourceField.Any(r => r is FieldEncryptedValue);
                if (newEntry && sourceWire.Length > 0)
                {
                    write[field.FieldKey] = sourceWire;
                }
                else if (!newEntry && change != Changes.Unchanged)
                {
                    write[field.FieldKey] = sourceWire; // empty string clears (missing value = no row)
                }

                var key = path + field.FieldKey;
                if (needsAck)
                {
                    plan.PlaintextAcknowledgementFieldKeys.Add(key);
                }

                if (change == Changes.Cleared)
                {
                    plan.ClearedFieldKeys.Add(key);
                }
                else if (change != Changes.Unchanged)
                {
                    plan.ChangedFieldKeys.Add(key);
                }

                plan.Fields.Add(new RestoreFieldChangeResponse(key, field.FieldDisplayName, path.Length == 0 ? null : path.TrimEnd('.'), currentWire, sourceWire, change,
                    groupDefinitionId is null ? placedFields.Contains(id) : placedGroups.ContainsKey(groupDefinitionId.Value) && placedFields.Contains(id), willEncrypt, needsAck));
            }

            return write;
        }

        // ---- Top level ----
        var top = CompareScope(current.Values.Where(v => v.FormRecordGroupId is null).ToList(), source.Values.Where(v => v.FormRecordGroupId is null).ToList(), null, "", false);
        foreach (var (key, wire) in top)
        {
            plan.Values[key] = wire;
        }

        // ---- Repeating sections ----
        var groupIds = current.Occurrences.Select(o => o.GroupDefinitionId).Concat(source.Occurrences.Select(o => o.GroupDefinitionId)).Concat(placedGroups.Keys).Distinct()
            .OrderBy(id => groupById.TryGetValue(id, out var g) ? g.GroupKey : id.ToString("D"), StringComparer.Ordinal);
        foreach (var groupId in groupIds)
        {
            var groupKey = groupById.TryGetValue(groupId, out var definition) ? definition.GroupKey : groupId.ToString("D");
            var label = placedGroups.TryGetValue(groupId, out var section) ? section.Title : groupKey;
            var currentEntries = current.Occurrences.Where(o => o.GroupDefinitionId == groupId).OrderBy(o => o.GroupSortOrder).ThenBy(o => o.FormRecordGroupId).ToList();
            var sourceEntries = source.Occurrences.Where(o => o.GroupDefinitionId == groupId).OrderBy(o => o.GroupSortOrder).ThenBy(o => o.FormRecordGroupId).ToList();
            var currentIds = currentEntries.Select(o => o.FormRecordGroupId).ToHashSet();
            var occurrenceWrites = new List<CandidateBuilder.OccurrenceWrite>();
            var anyValueChange = false;
            for (var index = 0; index < sourceEntries.Count; index++)
            {
                var entry = sourceEntries[index];
                var existing = currentIds.Contains(entry.FormRecordGroupId);
                var changesBefore = plan.ChangedFieldKeys.Count + plan.ClearedFieldKeys.Count;
                var values = CompareScope(
                    existing ? current.Values.Where(v => v.FormRecordGroupId == entry.FormRecordGroupId).ToList() : [],
                    source.Values.Where(v => v.FormRecordGroupId == entry.FormRecordGroupId).ToList(),
                    groupId, $"{groupKey}[{index + 1}].", !existing);
                anyValueChange |= plan.ChangedFieldKeys.Count + plan.ClearedFieldKeys.Count > changesBefore;
                // An entry that exists now keeps its stable id; an entry that exists only in the source is created as a new entry.
                occurrenceWrites.Add(new CandidateBuilder.OccurrenceWrite(existing ? entry.FormRecordGroupId : null, values));
            }

            var sourceIds = sourceEntries.Select(o => o.FormRecordGroupId).ToList();
            var sameStructure = currentEntries.Select(o => o.FormRecordGroupId).SequenceEqual(sourceIds);
            var removed = currentEntries.Count(o => !sourceIds.Contains(o.FormRecordGroupId));
            var added = sourceEntries.Count(o => !currentIds.Contains(o.FormRecordGroupId));
            if (sameStructure && !anyValueChange)
            {
                if (sourceEntries.Count > 0 || currentEntries.Count > 0)
                {
                    plan.Sections.Add(new RestoreSectionChangeResponse(groupKey, label, currentEntries.Count, sourceEntries.Count, 0, 0, placedGroups.ContainsKey(groupId)));
                }

                continue;
            }

            if (!placedGroups.ContainsKey(groupId))
            {
                // The ordinary write path carries entries of a section that is no longer in the form unchanged and cannot rewrite
                // them; an exact restore is impossible, and V1 never restores part of a version (OD-11).
                plan = plan with { Unsupported = $"the repeating section '{groupKey}' is no longer part of the current form and differs from this version; it cannot be restored exactly" };
                plan.Sections.Add(new RestoreSectionChangeResponse(groupKey, label, currentEntries.Count, sourceEntries.Count, added, removed, false));
                continue;
            }

            plan.GroupWrites.Add(new CandidateBuilder.GroupWrite(groupKey, occurrenceWrites));
            plan.Sections.Add(new RestoreSectionChangeResponse(groupKey, label, currentEntries.Count, sourceEntries.Count, added, removed, true));
        }

        return plan;
    }
}
