using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Staging;

/// <summary>
/// Builds the full candidate snapshot for a write (v1 policy: every leaf re-signed under the candidate operation).
/// Unchanged items keep their item identity; changed items keep identity with new content; new keys create new items;
/// attachments are carried forward as fresh per-operation copies of the committed version rows.
/// </summary>
public static class CandidateBuilder
{
    /// <summary>The authority refuses a candidate with more items than this (header, values, occurrences and attachments).</summary>
    public const int MaxCandidateItems = 500;

    public static FormRecordRevision CloneHeader(FormRecordRevision current) => new()
    {
        FormRecordRevisionId = Guid.CreateVersion7(),
        TenantId = current.TenantId,
        FormRecordId = current.FormRecordId,
        FormDefinitionId = current.FormDefinitionId,
        CreatedFormVersionId = current.CreatedFormVersionId,
        CreatedByUserId = current.CreatedByUserId,
        RecordStatus = current.RecordStatus,
        SubmittedOn = current.SubmittedOn,
        ParentFormRecordId = current.ParentFormRecordId,
        FormRelationshipDefinitionId = current.FormRelationshipDefinitionId,
        RelationshipSortOrder = current.RelationshipSortOrder,
    };

    /// <summary>Per-operation copy of a committed or processed attachment version row (same object key and envelope, new physical row).</summary>
    public static FileAttachmentVersion CloneAttachment(FileAttachmentVersion v, string versionState = "Committed") => new()
    {
        FileAttachmentVersionId = Guid.CreateVersion7(),
        TenantId = v.TenantId,
        FormRecordId = v.FormRecordId,
        FileAttachmentId = v.FileAttachmentId,
        FieldDefinitionId = v.FieldDefinitionId,
        VersionNumber = v.VersionNumber,
        OriginalFileName = v.OriginalFileName,
        DetectedContentType = v.DetectedContentType,
        PlaintextSizeBytes = v.PlaintextSizeBytes,
        PlaintextSha256 = v.PlaintextSha256,
        CiphertextSizeBytes = v.CiphertextSizeBytes,
        CiphertextSha256 = v.CiphertextSha256,
        StorageProviderName = v.StorageProviderName,
        StorageObjectKey = v.StorageObjectKey,
        EncryptionAlgorithmName = v.EncryptionAlgorithmName,
        EncryptionKeyReference = v.EncryptionKeyReference,
        EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion,
        WrappedDataKey = v.WrappedDataKey,
        EncryptionNoncePrefix = v.EncryptionNoncePrefix,
        EncryptionSegmentSizeBytes = v.EncryptionSegmentSizeBytes,
        ScanVerdict = v.ScanVerdict,
        ScannerName = v.ScannerName,
        ScanSignatureName = v.ScanSignatureName,
        ScannedOn = v.ScannedOn,
        UploadedByUserId = v.UploadedByUserId,
        UploadedOn = v.UploadedOn,
        VersionState = versionState,
    };

    /// <summary>Per-operation copy of a committed repeating-group occurrence row (same stable occurrence id and order, new physical row).</summary>
    public static FormRecordGroupRevision CloneOccurrence(FormRecordGroupRevision g, long? sortOrder = null) => new()
    {
        FormRecordGroupRevisionId = Guid.CreateVersion7(),
        TenantId = g.TenantId,
        FormRecordId = g.FormRecordId,
        FormRecordGroupId = g.FormRecordGroupId,
        GroupDefinitionId = g.GroupDefinitionId,
        ParentFormRecordGroupId = g.ParentFormRecordGroupId,
        GroupSortOrder = sortOrder ?? g.GroupSortOrder,
        CreatedOn = g.CreatedOn,
        CreatedByUserId = g.CreatedByUserId,
    };

    /// <summary>Refuses a candidate the authority would refuse anyway, before anything is registered or staged, with a clear reason.</summary>
    public static void EnsureWithinCeiling(int itemCountIncludingHeader)
    {
        if (itemCountIncludingHeader > MaxCandidateItems)
        {
            throw new BusinessException("RECORD_TOO_LARGE", 422, $"a record can hold at most {MaxCandidateItems} stored items (values, selections, entries and documents); this change would need {itemCountIncludingHeader}");
        }
    }

    /// <summary>
    /// Applies a values dictionary (field key → invariant wire value; empty string = clear) to the current typed rows.
    /// <paramref name="editableItems"/> null = owner (any mutation); otherwise only listed items may change and nothing may be added or removed.
    /// </summary>
    public static List<FieldValueRowBase> ApplyValues(
        IReadOnlyList<FieldValueRowBase> currentValues,
        IReadOnlyDictionary<string, string> values,
        FormLayout layout,
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyList<FieldOption> options,
        IReadOnlySet<Guid>? editableItems,
        Guid tenantId,
        Guid formRecordId,
        DateTimeOffset now,
        Guid actorUserId,
        IReadOnlyDictionary<Guid, FieldOption> optionsById,
        Encryption.IFieldEncryptionService encryption,
        RecordWriteOrigin origin = RecordWriteOrigin.User,
        bool creating = false) =>
        ApplyValuesDetailed(currentValues, values, layout, fields, options, editableItems, tenantId, formRecordId, now, actorUserId, optionsById, encryption, origin, creating).Rows;

    /// <summary>
    /// Rows of the new candidate (values and occurrences), the keys of fields whose value a visibility rule cleared (explicit
    /// opt-in per field; audited by the caller) and what changed in each repeating section (ids and counts only).
    /// </summary>
    public sealed record ApplyResult(List<FieldValueRowBase> Rows, IReadOnlyList<string> ClearedByRuleFieldKeys, List<FormRecordGroupRevision> Occurrences, IReadOnlyList<GroupChange> GroupChanges)
    {
        public ApplyResult(List<FieldValueRowBase> rows, IReadOnlyList<string> clearedByRuleFieldKeys)
            : this(rows, clearedByRuleFieldKeys, [], [])
        {
        }
    }

    /// <summary>Audit summary of one repeating section's change (never values).</summary>
    public sealed record GroupChange(string GroupKey, IReadOnlyList<Guid> AddedOccurrenceIds, IReadOnlyList<Guid> RemovedOccurrenceIds, bool Reordered, int OccurrenceCount);

    /// <summary>The complete new occurrence list of one repeating section, in display order.</summary>
    public sealed record GroupWrite(string GroupKey, IReadOnlyList<OccurrenceWrite> Occurrences);

    /// <summary>One occurrence: an existing stable id (its omitted field keys stay unchanged) or null for a new entry.</summary>
    public sealed record OccurrenceWrite(Guid? FormRecordGroupId, IReadOnlyDictionary<string, string> Values);

    /// <summary>Repeating-group state of the record: the form's group definitions, the committed occurrences, and the requested writes (null = keep every section as is).</summary>
    public sealed record GroupInput(IReadOnlyList<GroupDefinition> Definitions, IReadOnlyList<FormRecordGroupRevision> CurrentOccurrences, IReadOnlyList<GroupWrite>? Writes);

    /// <summary>
    /// The write rules of a record, in order: (1) every supplied key names a known field of its scope (top level, or the
    /// occurrence's repeating section); (2) occurrence ids are the record's own, each listed once; adding, removing and
    /// reordering entries follow the section's settings and entry bounds; (3) each changed value validates against the layout
    /// and option catalog and is protected per the field's policy; (4) a read-only field cannot be changed by a user write
    /// (create, and a new entry, may only receive its configured default); (5) conditional rules are evaluated per scope — the
    /// top level on top-level values, each occurrence on the top-level values plus its own — a field hidden by a rule cannot be
    /// changed (its stored value is retained, or cleared only when the designer opted in with clear-when-hidden), a visible
    /// required field must have a value, and a dependent dropdown's selection must be allowed by its parent's selection.
    /// Rules, requiredness and dependencies are enforced for full-scope (owner) writes; a shared contributor writes only the
    /// items its share grants, cannot see the fields a rule may read, is never told anything about them, and cannot change
    /// repeating sections.
    /// </summary>
    public static ApplyResult ApplyValuesDetailed(
        IReadOnlyList<FieldValueRowBase> currentValues,
        IReadOnlyDictionary<string, string> values,
        FormLayout layout,
        IReadOnlyList<FieldDefinition> fields,
        IReadOnlyList<FieldOption> options,
        IReadOnlySet<Guid>? editableItems,
        Guid tenantId,
        Guid formRecordId,
        DateTimeOffset now,
        Guid actorUserId,
        IReadOnlyDictionary<Guid, FieldOption> optionsById,
        Encryption.IFieldEncryptionService encryption,
        RecordWriteOrigin origin = RecordWriteOrigin.User,
        bool creating = false,
        GroupInput? groups = null)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(currentValues);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fields);
        var fieldByKey = fields.ToDictionary(f => f.FieldKey, StringComparer.Ordinal);
        var fieldById = fields.ToDictionary(f => f.FieldDefinitionId);
        var elementByField = layout.Elements.Where(e => e.FieldDefinitionId is not null).ToDictionary(e => e.FieldDefinitionId!.Value);
        var context = new ScopeContext(layout, fieldByKey, fieldById, elementByField, options, optionsById, encryption, editableItems, tenantId, formRecordId, now, actorUserId, origin);

        if (values.Values.Any(v => v is null || v.Length > 4000))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
        }

        foreach (var key in values.Keys)
        {
            if (!fieldByKey.TryGetValue(key, out var field))
            {
                throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
            }

            if (field.GroupDefinitionId is not null)
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"field '{key}' belongs to a repeating section; send its values per entry in 'groups'");
            }
        }

        if (groups is null && currentValues.Any(v => v.FormRecordGroupId is not null))
        {
            throw new InvalidOperationException("the record has repeating-section values; the caller must pass its group state");
        }

        // ---- Top level ----
        var top = ApplyScope(context, currentValues.Where(v => v.FormRecordGroupId is null).ToList(), values, null, null, creating, "");

        // ---- Repeating sections ----
        var definitions = groups?.Definitions ?? [];
        var currentOccurrences = groups?.CurrentOccurrences ?? [];
        var writes = groups?.Writes ?? [];
        var definitionByKey = definitions.ToDictionary(d => d.GroupKey, StringComparer.Ordinal);
        var repeatByGroup = layout.Sections.Where(s => s.Repeat is not null).ToDictionary(s => s.Repeat!.GroupDefinitionId, s => s);
        var writeByGroup = new Dictionary<Guid, GroupWrite>();
        foreach (var write in writes)
        {
            if (write is null || write.Occurrences is null || !definitionByKey.TryGetValue(write.GroupKey ?? string.Empty, out var definition) || !repeatByGroup.ContainsKey(definition.GroupDefinitionId))
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"'{write?.GroupKey}' is not a repeating section of this form version");
            }

            if (!writeByGroup.TryAdd(definition.GroupDefinitionId, write))
            {
                throw new BusinessException("INVALID_REQUEST", 422, $"repeating section '{write.GroupKey}' is listed more than once");
            }
        }

        if (writes.Count > 0 && editableItems is not null)
        {
            throw new BusinessException("FORBIDDEN", 403, "changing entries of a repeating section requires record ownership");
        }

        var scopes = new List<(ScopeResult Result, FormRecordGroupRevision Occurrence, string Path)>();
        var occurrenceRows = new List<FormRecordGroupRevision>();
        var groupChanges = new List<GroupChange>();
        var definitionById = definitions.ToDictionary(d => d.GroupDefinitionId);

        // Occurrences of groups that are no longer placed in the current layout are carried unchanged (their values are kept, CLAUDE.md §3).
        foreach (var orphan in currentOccurrences.Where(o => !repeatByGroup.ContainsKey(o.GroupDefinitionId)).OrderBy(o => o.GroupDefinitionId).ThenBy(o => o.GroupSortOrder))
        {
            var occurrence = CloneOccurrence(orphan);
            occurrenceRows.Add(occurrence);
            var carried = ApplyScope(context with { Origin = RecordWriteOrigin.System }, currentValues.Where(v => v.FormRecordGroupId == orphan.FormRecordGroupId).ToList(), new Dictionary<string, string>(), orphan.FormRecordGroupId, orphan.GroupDefinitionId, false, "");
            top.Rows.AddRange(carried.Rows);
        }

        foreach (var (groupId, section) in repeatByGroup.OrderBy(p => layout.Sections.ToList().IndexOf(p.Value)))
        {
            var repeat = section.Repeat!;
            var groupKey = definitionById.TryGetValue(groupId, out var groupDefinition) ? groupDefinition.GroupKey : groupId.ToString("D");
            var existing = currentOccurrences.Where(o => o.GroupDefinitionId == groupId).OrderBy(o => o.GroupSortOrder).ThenBy(o => o.FormRecordGroupId).ToList();
            var existingIds = existing.Select(o => o.FormRecordGroupId).ToHashSet();
            writeByGroup.TryGetValue(groupId, out var write);

            // Resulting occurrence list: (stable id, is new, supplied values).
            var planned = new List<(Guid Id, bool IsNew, IReadOnlyDictionary<string, string> Values)>();
            if (write is null)
            {
                planned.AddRange(existing.Select(o => (o.FormRecordGroupId, false, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())));
                if (creating && existing.Count == 0)
                {
                    // A new record starts with the section's minimum number of (empty) entries.
                    for (var i = 0; i < repeat.MinOccurrences; i++)
                    {
                        planned.Add((Guid.CreateVersion7(), true, new Dictionary<string, string>()));
                    }
                }
            }
            else
            {
                if (write.Occurrences.Count > LayoutRepeat.CeilingOccurrences)
                {
                    throw new BusinessException("GROUP_OCCURRENCE_COUNT", 422, $"'{section.Title}' allows at most {repeat.MaxOccurrences} entries");
                }

                var seen = new HashSet<Guid>();
                foreach (var occurrence in write.Occurrences)
                {
                    if (occurrence is null || occurrence.Values is null)
                    {
                        throw new BusinessException("INVALID_REQUEST", 422, $"every entry of '{section.Title}' needs a values object");
                    }

                    if (occurrence.FormRecordGroupId is { } id)
                    {
                        // Forged, foreign (another record or section) and duplicated ids are refused, never adopted.
                        if (!existingIds.Contains(id))
                        {
                            throw new BusinessException("OCCURRENCE_UNKNOWN", 422, $"an entry of '{section.Title}' does not belong to this record");
                        }

                        if (!seen.Add(id))
                        {
                            throw new BusinessException("INVALID_REQUEST", 422, $"an entry of '{section.Title}' is listed more than once");
                        }

                        planned.Add((id, false, occurrence.Values));
                    }
                    else
                    {
                        planned.Add((Guid.CreateVersion7(), true, occurrence.Values));
                    }
                }
            }

            var added = planned.Where(p => p.IsNew).Select(p => p.Id).ToList();
            var keptIds = planned.Where(p => !p.IsNew).Select(p => p.Id).ToList();
            var removed = existing.Select(o => o.FormRecordGroupId).Where(id => !keptIds.Contains(id)).ToList();
            var reordered = !existing.Select(o => o.FormRecordGroupId).Where(keptIds.Contains).SequenceEqual(keptIds);
            var structureChanged = added.Count > 0 || removed.Count > 0 || reordered;

            if (origin == RecordWriteOrigin.User && !creating)
            {
                if (added.Count > 0 && !repeat.AllowAdd)
                {
                    throw new BusinessException("GROUP_ADD_NOT_ALLOWED", 422, $"entries cannot be added to '{section.Title}'");
                }

                if (removed.Count > 0 && !repeat.AllowRemove)
                {
                    throw new BusinessException("GROUP_REMOVE_NOT_ALLOWED", 422, $"entries cannot be removed from '{section.Title}'");
                }

                if (reordered && !repeat.AllowReorder)
                {
                    throw new BusinessException("GROUP_REORDER_NOT_ALLOWED", 422, $"entries of '{section.Title}' cannot be reordered");
                }
            }

            if (creating && !repeat.AllowAdd && origin == RecordWriteOrigin.User && planned.Count != repeat.MinOccurrences)
            {
                throw new BusinessException("GROUP_OCCURRENCE_COUNT", 422, $"'{section.Title}' has exactly {repeat.MinOccurrences} entries");
            }

            // Bounds are checked whenever the entry list is created or changed (a bound tightened by a later version is enforced when the entries are next changed).
            if ((creating || structureChanged) && (planned.Count < repeat.MinOccurrences || planned.Count > repeat.MaxOccurrences))
            {
                throw new BusinessException("GROUP_OCCURRENCE_COUNT", 422, $"'{section.Title}' needs between {repeat.MinOccurrences} and {repeat.MaxOccurrences} entries; {planned.Count} were given");
            }

            for (var index = 0; index < planned.Count; index++)
            {
                var (id, isNew, occurrenceValues) = planned[index];
                foreach (var key in occurrenceValues.Keys)
                {
                    if (!fieldByKey.TryGetValue(key, out var field) || field.GroupDefinitionId != groupId)
                    {
                        throw new BusinessException("INVALID_REQUEST", 422, $"'{key}' is not a field of the repeating section '{section.Title}'");
                    }
                }

                if (occurrenceValues.Values.Any(v => v is null || v.Length > 4000))
                {
                    throw new BusinessException("INVALID_REQUEST", 422, "values must reference known field keys (max 4000 chars each)");
                }

                var source = existing.FirstOrDefault(o => o.FormRecordGroupId == id);
                var occurrence = source is null
                    ? new FormRecordGroupRevision { FormRecordGroupRevisionId = Guid.CreateVersion7(), TenantId = tenantId, FormRecordId = formRecordId, FormRecordGroupId = id, GroupDefinitionId = groupId, GroupSortOrder = index, CreatedOn = now, CreatedByUserId = actorUserId }
                    : CloneOccurrence(source, write is null ? source.GroupSortOrder : index);
                occurrenceRows.Add(occurrence);
                var path = $"{groupKey}[{index + 1}].";
                var applied = ApplyScope(context, isNew ? [] : currentValues.Where(v => v.FormRecordGroupId == id).ToList(), occurrenceValues, id, groupId, creating || isNew, path);
                scopes.Add((applied, occurrence, path));
            }

            if (structureChanged)
            {
                groupChanges.Add(new GroupChange(groupKey, added, removed, reordered, planned.Count));
            }
        }

        // Values of removed occurrences are not carried: they leave the next accepted revision (earlier revisions are untouched).

        // ---- Read-only (user writes only): the server, not the form, is what keeps the value fixed ----
        if (origin == RecordWriteOrigin.User)
        {
            EnforceReadOnly(context, top);
            foreach (var scope in scopes)
            {
                EnforceReadOnly(context, scope.Result);
            }
        }

        // ---- Rules, requiredness and dependent dropdowns (full scope only) ----
        var cleared = new List<string>();
        if (editableItems is null)
        {
            var missing = new List<string>();
            var dependencyErrors = new List<string>();
            var topStates = FormRules.Evaluate(layout, fieldById, top.Wire);
            EnforceRules(context, top, topStates, null, top.Wire, cleared, missing, dependencyErrors);
            foreach (var (result, occurrence, _) in scopes)
            {
                var merged = new Dictionary<Guid, string>(top.Wire);
                foreach (var (id, wire) in result.Wire)
                {
                    merged[id] = wire;
                }

                var states = FormRules.Evaluate(layout, fieldById, merged);
                EnforceRules(context, result, states, occurrence.GroupDefinitionId, merged, cleared, missing, dependencyErrors);
            }

            if (dependencyErrors.Count > 0)
            {
                throw new BusinessException("DEPENDENT_OPTION_INVALID", 422, "selections not allowed for the chosen parent value: " + string.Join(", ", dependencyErrors));
            }

            if (missing.Count > 0)
            {
                throw new BusinessException("VALIDATION_FAILED", 422, "required fields missing: " + string.Join(", ", missing));
            }
        }

        var rows = new List<FieldValueRowBase>(top.Rows);
        foreach (var scope in scopes)
        {
            rows.AddRange(scope.Result.Rows);
        }

        return new ApplyResult(rows, cleared, occurrenceRows, groupChanges);
    }

    private sealed record ScopeContext(
        FormLayout Layout,
        IReadOnlyDictionary<string, FieldDefinition> FieldByKey,
        IReadOnlyDictionary<Guid, FieldDefinition> FieldById,
        IReadOnlyDictionary<Guid, LayoutElement> ElementByField,
        IReadOnlyList<FieldOption> Options,
        IReadOnlyDictionary<Guid, FieldOption> OptionsById,
        Encryption.IFieldEncryptionService Encryption,
        IReadOnlySet<Guid>? EditableItems,
        Guid TenantId,
        Guid FormRecordId,
        DateTimeOffset Now,
        Guid ActorUserId,
        RecordWriteOrigin Origin);

    private sealed class ScopeResult(Guid? groupId, Guid? groupDefinitionId, bool creatingScope, string path)
    {
        public Guid? GroupId { get; } = groupId;

        /// <summary>The repeating group of the scope (null at the top level); fields belong to a scope by this id.</summary>
        public Guid? GroupDefinitionId { get; } = groupDefinitionId;

        public bool CreatingScope { get; } = creatingScope;

        public string Path { get; } = path;

        public List<FieldValueRowBase> Rows { get; } = [];

        public Dictionary<Guid, string> Wire { get; } = [];

        public HashSet<Guid> Changed { get; } = [];
    }

    /// <summary>
    /// One scope (the top level, or one occurrence): carries unchanged values, applies supplied ones, adds new ones. Every row
    /// produced carries the scope's occurrence id, so a value can never move between occurrences.
    /// </summary>
    private static ScopeResult ApplyScope(ScopeContext c, List<FieldValueRowBase> currentValues, IReadOnlyDictionary<string, string> values, Guid? groupId, Guid? groupDefinitionId, bool creatingScope, string path)
    {
        var scope = new ScopeResult(groupId, groupDefinitionId, creatingScope, path);
        var options = c.Options;

        // A read-only dropdown with a default receives it on create (and in a new entry) even when the client omitted it: nobody can type it.
        var effective = new Dictionary<string, string>(values, StringComparer.Ordinal);
        if (creatingScope)
        {
            foreach (var element in c.Layout.Elements.Where(e => e is { Kind: LayoutElementKinds.Field, ReadOnly: true, DefaultOptionKeys.Count: > 0 }))
            {
                var field = c.FieldById.GetValueOrDefault(element.FieldDefinitionId!.Value);
                if (field is { FieldDataType: FieldDataTypes.Option } && field.GroupDefinitionId == groupDefinitionId && (!effective.TryGetValue(field.FieldKey, out var given) || given.Length == 0))
                {
                    var activeDefaults = ActiveDefaults(element, field, options);
                    if (activeDefaults.Count > 0)
                    {
                        effective[field.FieldKey] = string.Join(',', activeDefaults);
                    }
                }
            }
        }

        var wireParts = new Dictionary<Guid, List<string>>();
        var existingFieldIds = currentValues.Select(v => v.FieldDefinitionId).ToHashSet();

        void Emit(FieldDefinition field, FieldValueRowBase typed, string wire)
        {
            typed.FormRecordGroupId = groupId;
            var row = FieldValueCodec.Protect(typed, field, c.OptionsById, c.Encryption);
            row.FormRecordGroupId = groupId;
            scope.Rows.Add(row);
            if (!wireParts.TryGetValue(field.FieldDefinitionId, out var parts))
            {
                wireParts[field.FieldDefinitionId] = parts = [];
            }

            parts.Add(wire);
        }

        // An unchanged value is carried forward under the field's current protection (a legacy plaintext value of a field
        // strengthened since the last save is re-sealed here), still validated against the current layout and option catalog:
        // a deactivated option or a tightened rule blocks the save.
        void Carry(FieldDefinition field, LayoutElement? element, FieldValueRowBase existing, string wire) =>
            Emit(field, FieldValueCodec.CreateRow(field, element, options, wire, c.TenantId, c.FormRecordId, existing.FieldValueItemId, existing.CreatedOn, existing.CreatedByUserId), wire);

        foreach (var group in currentValues.GroupBy(v => v.FieldDefinitionId))
        {
            var field = c.FieldById.GetValueOrDefault(group.Key);
            if (field is null)
            {
                continue; // field retired from the definition entirely; value no longer carried (never for Stage B data)
            }

            var element = c.ElementByField.GetValueOrDefault(field.FieldDefinitionId);
            var rows = group.ToList();
            // Carry-forward re-creates the row under the field's *current* protection: a field strengthened to encryption
            // since the last save rewrites its legacy plaintext as ciphertext here (the audited migration's backfill step).
            var currentWires = rows.Select(r => FieldValueCodec.Format(r, c.OptionsById, c.Encryption)).ToList();
            var hasNew = values.TryGetValue(field.FieldKey, out var wire);

            if (FieldValueCodec.IsMultiSelect(field))
            {
                var currentKeys = currentWires.ToHashSet(StringComparer.Ordinal);
                var newKeys = hasNew ? FormRules.SplitSelection(wire!) : currentKeys.ToList();
                var itemByKey = rows.Zip(currentWires).GroupBy(p => p.Second, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().First, StringComparer.Ordinal);
                if (hasNew && !currentKeys.SetEquals(newKeys))
                {
                    scope.Changed.Add(field.FieldDefinitionId);
                    if (c.EditableItems is not null)
                    {
                        throw new BusinessException("FORBIDDEN", 403, $"changing the selection of '{field.FieldKey}' requires record ownership");
                    }
                }

                foreach (var key in newKeys)
                {
                    var existing = itemByKey.GetValueOrDefault(key);
                    if (existing is not null)
                    {
                        Carry(field, element, existing, key);
                        continue;
                    }

                    Emit(field, FieldValueCodec.CreateRow(field, element, options, key, c.TenantId, c.FormRecordId, Guid.CreateVersion7(), c.Now, c.ActorUserId), key);
                }

                continue;
            }

            var current = rows[0];
            var currentWire = currentWires[0];
            if (!hasNew)
            {
                Carry(field, element, current, currentWire);
                continue;
            }

            if (string.Equals(wire, currentWire, StringComparison.Ordinal))
            {
                Carry(field, element, current, wire!);
                continue;
            }

            if (c.EditableItems is not null && !c.EditableItems.Contains(current.FieldValueItemId))
            {
                throw new BusinessException("FORBIDDEN", 403, $"field '{field.FieldKey}' is outside the caller's edit scope");
            }

            scope.Changed.Add(field.FieldDefinitionId);
            if (wire!.Length == 0)
            {
                if (c.EditableItems is not null)
                {
                    throw new BusinessException("FORBIDDEN", 403, "clearing items requires record ownership");
                }

                continue; // explicit clear: missing value means no row (CLAUDE.md §2.4)
            }

            Emit(field, FieldValueCodec.CreateRow(field, element, options, wire, c.TenantId, c.FormRecordId, current.FieldValueItemId, c.Now, c.ActorUserId), wire);
        }

        foreach (var (key, wire) in effective.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var field = c.FieldByKey[key];
            if (existingFieldIds.Contains(field.FieldDefinitionId) || wire.Length == 0)
            {
                continue;
            }

            if (c.EditableItems is not null)
            {
                throw new BusinessException("FORBIDDEN", 403, "adding items requires record ownership");
            }

            scope.Changed.Add(field.FieldDefinitionId);
            var element = c.ElementByField.GetValueOrDefault(field.FieldDefinitionId);
            if (FieldValueCodec.IsMultiSelect(field))
            {
                foreach (var typed in FieldValueCodec.CreateSelectionRows(field, element, options, FormRules.SplitSelection(wire), c.TenantId, c.FormRecordId, c.Now, c.ActorUserId))
                {
                    Emit(field, typed, FieldValueCodec.Format(typed, c.OptionsById));
                }

                continue;
            }

            Emit(field, FieldValueCodec.CreateRow(field, element, options, wire, c.TenantId, c.FormRecordId, Guid.CreateVersion7(), c.Now, c.ActorUserId), wire);
        }

        foreach (var (id, parts) in wireParts)
        {
            scope.Wire[id] = FieldValueCodec.IsMultiSelect(c.FieldById[id]) ? CanonicalSelection(c.FieldById[id], parts, options) : parts[0];
        }

        return scope;
    }

    private static void EnforceReadOnly(ScopeContext c, ScopeResult scope)
    {
        foreach (var element in c.Layout.Elements.Where(e => e is { Kind: LayoutElementKinds.Field, ReadOnly: true }))
        {
            var field = c.FieldById.GetValueOrDefault(element.FieldDefinitionId!.Value);
            if (field is null || field.GroupDefinitionId != scope.GroupDefinitionId || !scope.Changed.Contains(field.FieldDefinitionId))
            {
                continue;
            }

            var equalsDefault = scope.CreatingScope && element.DefaultOptionKeys is { Count: > 0 }
                                && string.Equals(scope.Wire.GetValueOrDefault(field.FieldDefinitionId) ?? string.Empty, CanonicalSelection(field, ActiveDefaults(element, field, c.Options), c.Options), StringComparison.Ordinal);
            if (!equalsDefault)
            {
                throw new BusinessException("FIELD_READ_ONLY", 403, $"field '{scope.Path}{field.FieldKey}' is read-only and cannot be changed");
            }
        }
    }

    private static void EnforceRules(ScopeContext c, ScopeResult scope, IReadOnlyDictionary<Guid, FieldRuleState> states, Guid? groupId, IReadOnlyDictionary<Guid, string> readable, List<string> cleared, List<string> missing, List<string> dependencyErrors)
    {
        foreach (var element in c.Layout.Elements.Where(e => e.Kind == LayoutElementKinds.Field && e.FieldDefinitionId is { } id && c.FieldById.TryGetValue(id, out var f) && f.GroupDefinitionId == groupId))
        {
            var field = c.FieldById[element.FieldDefinitionId!.Value];
            var state = states[field.FieldDefinitionId];
            var present = scope.Wire.TryGetValue(field.FieldDefinitionId, out var held) && held.Length > 0;
            if (!state.Visible)
            {
                if (scope.Changed.Contains(field.FieldDefinitionId) && c.Origin == RecordWriteOrigin.User)
                {
                    throw new BusinessException("FIELD_HIDDEN_BY_RULE", 422, $"field '{scope.Path}{field.FieldKey}' is hidden by a rule and cannot be changed");
                }

                if (element.ClearWhenHidden && present)
                {
                    scope.Rows.RemoveAll(r => r.FieldDefinitionId == field.FieldDefinitionId);
                    scope.Wire.Remove(field.FieldDefinitionId);
                    cleared.Add(scope.Path + field.FieldKey);
                }

                continue;
            }

            if (state.Required && !present)
            {
                missing.Add(scope.Path + field.FieldKey);
                continue;
            }

            // Dependent dropdown: the stored selection must be allowed by the parent's selection (never cleared silently).
            if (present && element.OptionFilter is { } filter)
            {
                var parentWire = readable.GetValueOrDefault(filter.ParentFieldDefinitionId) ?? string.Empty;
                if (FormRules.DependentOptionViolation(filter, field, held!, parentWire) is not null)
                {
                    dependencyErrors.Add(scope.Path + field.FieldKey);
                }
            }
        }
    }

    private static List<string> ActiveDefaults(LayoutElement element, FieldDefinition field, IReadOnlyList<FieldOption> options) =>
        (element.DefaultOptionKeys ?? []).Where(k => options.Any(o => o.FieldDefinitionId == field.FieldDefinitionId && o.OptionKey == k && o.IsActive)).ToList();

    private static string CanonicalSelection(FieldDefinition field, List<string> keys, IReadOnlyList<FieldOption> options)
    {
        if (!FieldValueCodec.IsMultiSelect(field))
        {
            return keys.Count > 0 ? keys[0] : string.Empty;
        }

        var order = options.Where(o => o.FieldDefinitionId == field.FieldDefinitionId).ToDictionary(o => o.OptionKey, o => o.OptionSortOrder, StringComparer.Ordinal);
        return string.Join(',', keys.Distinct(StringComparer.Ordinal).OrderBy(k => order.GetValueOrDefault(k, int.MaxValue)).ThenBy(k => k, StringComparer.Ordinal));
    }
}

/// <summary>Who is writing: ordinary users are bound by read-only and hidden-field rules; explicitly authorized system operations (e.g. a governed import) are not bound by read-only.</summary>
public enum RecordWriteOrigin
{
    User,
    System,
}
