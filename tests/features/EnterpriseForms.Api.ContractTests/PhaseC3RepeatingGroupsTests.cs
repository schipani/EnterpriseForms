using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C3: repeating groups (occurrence identity, add/remove/reorder, bounds, per-occurrence rules, read-only, encryption,
/// signed occurrence rows) and dependent dropdowns (stable option-key mapping, design-time validation including cycles,
/// server-side refusal of invalid combinations).
/// </summary>
public sealed class PhaseC3RepeatingGroupsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid FormId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly AesGcmFieldEncryptionService Encryption = new(new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));

    private static FieldDefinition Field(string key, string type, GroupDefinition? group = null, string mode = "Single", string protection = "Standard") => new()
    {
        FieldDefinitionId = Guid.CreateVersion7(), FormDefinitionId = FormId, FieldKey = key, FieldDisplayName = key, FieldDataType = type, FieldClassification = protection == "Standard" ? "Internal" : "Confidential", FieldProtectionMode = protection, OptionSelectionMode = mode, GroupDefinitionId = group?.GroupDefinitionId,
    };

    private static GroupDefinition Group(string key) => new() { GroupDefinitionId = Guid.CreateVersion7(), FormDefinitionId = FormId, GroupKey = key, GroupDisplayName = key, LifecycleStatus = "Available", CreatedOn = Now, CreatedByUserId = Actor };

    private static List<FieldOption> Opts(FieldDefinition field, params string[] keys) =>
        keys.Select((k, i) => new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = field.FieldDefinitionId, OptionKey = k, OptionDisplayText = k.ToUpperInvariant(), OptionSortOrder = i, IsActive = true }).ToList();

    private static string ControlOf(FieldDefinition f) => f.FieldDataType switch
    {
        "Option" => f.OptionSelectionMode == "Multiple" ? "multiselect" : "select",
        "Integer" => "number",
        "File" => "fileUpload",
        _ => "text",
    };

    private static LayoutElement Element(FieldDefinition field, bool required = false, RuleCondition? visibleWhen = null, RuleCondition? requiredWhen = null, bool readOnly = false, IReadOnlyList<string>? defaults = null, LayoutOptionFilter? filter = null) =>
        new(field.FieldDataType == "File" ? LayoutElementKinds.DocumentCollection : LayoutElementKinds.Field, field.FieldDefinitionId, null, ControlOf(field), field.FieldDisplayName, null, new LayoutPlacement(4, false), new LayoutValidation(required, null, null, null, null, null, null, null), null, null,
            DefaultOptionKeys: defaults, ReadOnly: readOnly, VisibleWhen: visibleWhen, RequiredWhen: requiredWhen, OptionFilter: filter);

    private static LayoutSection TopSection(params LayoutElement[] elements) => new("main", "Main", 4, elements);

    private static LayoutSection Repeating(GroupDefinition group, LayoutRepeat? repeat, params LayoutElement[] elements) =>
        new(group.GroupKey, "People", 4, elements, null, repeat ?? new LayoutRepeat(group.GroupDefinitionId, 0, 5, true, true, true));

    private static LayoutRepeat Settings(GroupDefinition group, int min = 0, int max = 5, bool add = true, bool remove = true, bool reorder = true) => new(group.GroupDefinitionId, min, max, add, remove, reorder);

    private static FormLayout Layout(params LayoutSection[] sections) => new(2, "Test", sections);

    private static RuleComparison Eq(FieldDefinition field, string value) => new(RuleOperators.Equal, field.FieldDefinitionId, value, null);

    private static CandidateBuilder.OccurrenceWrite Entry(Guid? id, params (string Key, string Value)[] values) => new(id, values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal));

    private sealed record State(List<FieldValueRowBase> Values, List<FormRecordGroupRevision> Occurrences);

    private static CandidateBuilder.ApplyResult Apply(FormLayout layout, IReadOnlyList<FieldDefinition> fields, IReadOnlyList<FieldOption> options, IReadOnlyList<GroupDefinition> groups, State? current, Dictionary<string, string>? values, IReadOnlyList<CandidateBuilder.GroupWrite>? writes, bool creating = false, IReadOnlySet<Guid>? editable = null, RecordWriteOrigin origin = RecordWriteOrigin.User) =>
        CandidateBuilder.ApplyValuesDetailed(current?.Values ?? [], values ?? [], layout, fields, options, editable, Tenant, Record, Now, Actor, options.ToDictionary(o => o.FieldOptionId), Encryption, origin, creating,
            new CandidateBuilder.GroupInput(groups, current?.Occurrences ?? [], writes));

    private static State StateOf(CandidateBuilder.ApplyResult result) => new(result.Rows, result.Occurrences);

    private static BusinessException Fails(Action action, string code)
    {
        var ex = Assert.Throws<BusinessException>(action);
        Assert.Equal(code, ex.ErrorCode);
        return ex;
    }

    private static string WireIn(State state, FieldDefinition field, Guid? occurrence, IReadOnlyList<FieldOption> options) =>
        FieldValueCodec.FormatField(field, state.Values.Where(r => r.FieldDefinitionId == field.FieldDefinitionId && r.FormRecordGroupId == occurrence), options.ToDictionary(o => o.FieldOptionId), Encryption);

    // ---- people section fixture --------------------------------------------------------------------------------------

    private sealed class People
    {
        public People(bool encryptedName = false, int min = 0, int max = 5, bool add = true, bool remove = true, bool reorder = true)
        {
            Group = Group("people");
            Title = Field("case_title", "String");
            Name = Field("person_name", "String", Group, protection: encryptedName ? "ApplicationEncrypted" : "Standard");
            Role = Field("person_role", "Option", Group);
            Options = Opts(Role, "witness", "suspect");
            Notes = Field("person_notes", "String", Group);
            Fields = [Title, Name, Role, Notes];
            Layout = Layout(
                TopSection(Element(Title)),
                Repeating(Group, Settings(Group, min, max, add, remove, reorder),
                    Element(Name, required: true),
                    Element(Role),
                    Element(Notes, visibleWhen: Eq(Role, "suspect"), requiredWhen: Eq(Role, "suspect"))));
        }

        public GroupDefinition Group { get; }
        public FieldDefinition Title { get; }
        public FieldDefinition Name { get; }
        public FieldDefinition Role { get; }
        public FieldDefinition Notes { get; }
        public List<FieldOption> Options { get; }
        public List<FieldDefinition> Fields { get; }
        public FormLayout Layout { get; }

        public CandidateBuilder.ApplyResult Write(State? current, IReadOnlyList<CandidateBuilder.OccurrenceWrite>? entries, bool creating = false, Dictionary<string, string>? values = null, IReadOnlySet<Guid>? editable = null) =>
            Apply(Layout, Fields, Options, [Group], current, values, entries is null ? null : [new CandidateBuilder.GroupWrite("people", entries)], creating, editable);

        public State Create(params CandidateBuilder.OccurrenceWrite[] entries) => StateOf(Write(null, entries, creating: true, values: new() { ["case_title"] = "Case 1" }));
    }

    // ---- occurrences --------------------------------------------------------------------------------------------------

    [Fact]
    public void CreatingEntriesGivesEachAStableIdentityOrderAndItsOwnValues()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada"), ("person_role", "witness")), Entry(null, ("person_name", "Bo")));

        Assert.Equal(2, state.Occurrences.Count);
        Assert.Equal([0L, 1L], state.Occurrences.Select(o => o.GroupSortOrder));
        Assert.All(state.Occurrences, o => Assert.Equal(p.Group.GroupDefinitionId, o.GroupDefinitionId));
        Assert.Equal(2, state.Occurrences.Select(o => o.FormRecordGroupId).Distinct().Count());
        Assert.Equal("Ada", WireIn(state, p.Name, state.Occurrences[0].FormRecordGroupId, p.Options));
        Assert.Equal("Bo", WireIn(state, p.Name, state.Occurrences[1].FormRecordGroupId, p.Options));
        Assert.Equal("witness", WireIn(state, p.Role, state.Occurrences[0].FormRecordGroupId, p.Options));
        Assert.Equal(string.Empty, WireIn(state, p.Role, state.Occurrences[1].FormRecordGroupId, p.Options));
        Assert.Equal("Case 1", WireIn(state, p.Title, null, p.Options));
        // An occurrence row is a RepeatedGroup manifest item whose item id is the stable occurrence id.
        Assert.All(state.Occurrences, o => Assert.Equal((byte)2, o.ItemKindCode));
        Assert.All(state.Occurrences, o => Assert.Equal(o.FormRecordGroupId, o.ItemId));
    }

    [Fact]
    public void EditingAndReorderingKeepsOccurrenceAndValueIdentities()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")), Entry(null, ("person_name", "Bo")), Entry(null, ("person_name", "Cy")));
        var ids = state.Occurrences.Select(o => o.FormRecordGroupId).ToList();
        var adaItem = state.Values.Single(v => v.FieldDefinitionId == p.Name.FieldDefinitionId && v.FormRecordGroupId == ids[0]).FieldValueItemId;

        // Reverse the order and rename Bo; Ada and Cy are sent with no values (unchanged).
        var result = p.Write(state, [Entry(ids[2]), Entry(ids[1], ("person_name", "Bob")), Entry(ids[0])]);
        var next = StateOf(result);
        Assert.Equal([ids[2], ids[1], ids[0]], next.Occurrences.OrderBy(o => o.GroupSortOrder).Select(o => o.FormRecordGroupId));
        Assert.Equal("Bob", WireIn(next, p.Name, ids[1], p.Options));
        Assert.Equal("Ada", WireIn(next, p.Name, ids[0], p.Options));
        Assert.Equal(adaItem, next.Values.Single(v => v.FieldDefinitionId == p.Name.FieldDefinitionId && v.FormRecordGroupId == ids[0]).FieldValueItemId);
        var change = Assert.Single(result.GroupChanges);
        Assert.True(change.Reordered);
        Assert.Empty(change.AddedOccurrenceIds);
        Assert.Empty(change.RemovedOccurrenceIds);
    }

    [Fact]
    public void RemovingAnEntryDropsItsValuesAndAddingOneCreatesAFreshIdentity()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")), Entry(null, ("person_name", "Bo")));
        var ids = state.Occurrences.Select(o => o.FormRecordGroupId).ToList();

        var result = p.Write(state, [Entry(ids[1]), Entry(null, ("person_name", "Cy"))]);
        var next = StateOf(result);
        Assert.DoesNotContain(next.Occurrences, o => o.FormRecordGroupId == ids[0]);
        Assert.DoesNotContain(next.Values, v => v.FormRecordGroupId == ids[0]);
        var added = Assert.Single(result.GroupChanges.Single().AddedOccurrenceIds);
        Assert.DoesNotContain(added, ids);
        Assert.Equal("Cy", WireIn(next, p.Name, added, p.Options));
        Assert.Equal([ids[0]], result.GroupChanges.Single().RemovedOccurrenceIds);
    }

    [Fact]
    public void AnUpdateThatDoesNotNameTheSectionKeepsEveryEntryUnchanged()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")));
        var result = p.Write(state, null, values: new() { ["case_title"] = "Renamed" });
        var next = StateOf(result);
        Assert.Equal(state.Occurrences.Select(o => (o.FormRecordGroupId, o.GroupSortOrder)), next.Occurrences.Select(o => (o.FormRecordGroupId, o.GroupSortOrder)));
        Assert.Equal("Ada", WireIn(next, p.Name, state.Occurrences[0].FormRecordGroupId, p.Options));
        Assert.Empty(result.GroupChanges);
    }

    [Fact]
    public void ForgedForeignAndDuplicatedOccurrenceIdsAreRefused()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")));
        var id = state.Occurrences[0].FormRecordGroupId;

        Fails(() => p.Write(state, [Entry(Guid.CreateVersion7(), ("person_name", "X"))]), "OCCURRENCE_UNKNOWN");
        Fails(() => p.Write(state, [Entry(id), Entry(id)]), "INVALID_REQUEST");

        // An occurrence id of another record's section (or another section of this record) is foreign here.
        var other = new People();
        var otherState = other.Create(Entry(null, ("person_name", "Zed")));
        Fails(() => p.Write(state, [Entry(otherState.Occurrences[0].FormRecordGroupId, ("person_name", "Stolen"))]), "OCCURRENCE_UNKNOWN");
    }

    [Fact]
    public void ValuesCanOnlyBeWrittenInTheirOwnScope()
    {
        var p = new People();
        // A grouped field sent as a record-level value, and a record-level field sent inside an entry.
        Fails(() => p.Write(null, [], creating: true, values: new() { ["person_name"] = "Ada" }), "INVALID_REQUEST");
        Fails(() => p.Write(null, [Entry(null, ("person_name", "Ada"), ("case_title", "x"))], creating: true), "INVALID_REQUEST");
        Fails(() => p.Write(null, [Entry(null, ("unknown_key", "x"))], creating: true), "INVALID_REQUEST");
        Fails(() => Apply(p.Layout, p.Fields, p.Options, [p.Group], null, null, [new CandidateBuilder.GroupWrite("not_a_group", [])], creating: true), "INVALID_REQUEST");
        Fails(() => Apply(p.Layout, p.Fields, p.Options, [p.Group], null, null, [new CandidateBuilder.GroupWrite("people", []), new CandidateBuilder.GroupWrite("people", [])], creating: true), "INVALID_REQUEST");
    }

    [Fact]
    public void SectionSettingsControlAddRemoveReorderAndBounds()
    {
        var fixed3 = new People(min: 2, max: 2, add: false, remove: false, reorder: false);
        var state = fixed3.Create(Entry(null, ("person_name", "Ada")), Entry(null, ("person_name", "Bo")));
        var ids = state.Occurrences.Select(o => o.FormRecordGroupId).ToList();

        Fails(() => fixed3.Write(state, [Entry(ids[0]), Entry(ids[1]), Entry(null, ("person_name", "Cy"))]), "GROUP_ADD_NOT_ALLOWED");
        Fails(() => fixed3.Write(state, [Entry(ids[0])]), "GROUP_REMOVE_NOT_ALLOWED");
        Fails(() => fixed3.Write(state, [Entry(ids[1]), Entry(ids[0])]), "GROUP_REORDER_NOT_ALLOWED");
        // Editing values in place is always allowed.
        Assert.Equal("Ann", WireIn(StateOf(fixed3.Write(state, [Entry(ids[0], ("person_name", "Ann")), Entry(ids[1])])), fixed3.Name, ids[0], fixed3.Options));
        // When people cannot add entries, a new record has exactly the minimum.
        Fails(() => fixed3.Create(Entry(null, ("person_name", "Ada"))), "GROUP_OCCURRENCE_COUNT");

        var bounded = new People(min: 1, max: 2);
        Fails(() => bounded.Create(), "GROUP_OCCURRENCE_COUNT");
        Fails(() => bounded.Create(Entry(null, ("person_name", "A")), Entry(null, ("person_name", "B")), Entry(null, ("person_name", "C"))), "GROUP_OCCURRENCE_COUNT");
        var one = bounded.Create(Entry(null, ("person_name", "A")));
        Fails(() => bounded.Write(one, []), "GROUP_OCCURRENCE_COUNT");
    }

    [Fact]
    public void ANewRecordStartsWithTheMinimumNumberOfEntriesWhenTheClientSendsNone()
    {
        var group = Group("people");
        var name = Field("person_name", "String", group);
        var optional = Layout(Repeating(group, Settings(group, min: 2), Element(name)));
        var result = Apply(optional, [name], [], [group], null, null, null, creating: true);
        Assert.Equal(2, result.Occurrences.Count);
        Assert.Equal(2, result.Occurrences.Select(o => o.FormRecordGroupId).Distinct().Count());

        // ...and a required field inside those entries still has to be provided.
        var p = new People(min: 1);
        var ex = Fails(() => Apply(p.Layout, p.Fields, p.Options, [p.Group], null, new() { ["case_title"] = "Case" }, null, creating: true), "VALIDATION_FAILED");
        Assert.Contains("people[1].person_name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesAreEvaluatedPerEntryAndRequiredMessagesNameTheEntry()
    {
        var p = new People();
        // Entry 2 is a suspect: its notes are visible and required; entry 1 (a witness) has no notes requirement.
        var ex = Fails(() => p.Create(Entry(null, ("person_name", "Ada"), ("person_role", "witness")), Entry(null, ("person_name", "Bo"), ("person_role", "suspect"))), "VALIDATION_FAILED");
        Assert.Contains("people[2].person_notes", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("people[1]", ex.Message, StringComparison.Ordinal);

        Fails(() => p.Create(Entry(null, ("person_role", "witness"))), "VALIDATION_FAILED");

        var state = p.Create(Entry(null, ("person_name", "Ada"), ("person_role", "witness")), Entry(null, ("person_name", "Bo"), ("person_role", "suspect"), ("person_notes", "seen")));
        var ids = state.Occurrences.Select(o => o.FormRecordGroupId).ToList();
        // Notes are hidden for the witness: they cannot be written there...
        Fails(() => p.Write(state, [Entry(ids[0], ("person_notes", "sneaky")), Entry(ids[1])]), "FIELD_HIDDEN_BY_RULE");
        // ...and changing entry 2's role to witness hides (but retains) its notes.
        var next = StateOf(p.Write(state, [Entry(ids[0]), Entry(ids[1], ("person_role", "witness"))]));
        Assert.Equal("seen", WireIn(next, p.Notes, ids[1], p.Options));
    }

    [Fact]
    public void RulesInsideAnEntryMayReadRecordLevelFields()
    {
        var group = Group("people");
        var kind = Field("case_kind", "Option");
        var kindOptions = Opts(kind, "minor", "major");
        var detail = Field("person_detail", "String", group);
        var layout = Layout(TopSection(Element(kind)), Repeating(group, null, Element(detail, requiredWhen: Eq(kind, "major"))));
        IReadOnlyList<FieldDefinition> fields = [kind, detail];
        FormRules.ValidateLayout(layout, fields.ToDictionary(f => f.FieldDefinitionId), new Dictionary<Guid, List<FieldOption>> { [kind.FieldDefinitionId] = kindOptions }, new HashSet<Guid> { group.GroupDefinitionId });

        var write = new CandidateBuilder.GroupWrite("people", [Entry(null)]);
        Apply(layout, fields, kindOptions, [group], null, new() { ["case_kind"] = "minor" }, [write], creating: true);
        var ex = Fails(() => Apply(layout, fields, kindOptions, [group], null, new() { ["case_kind"] = "major" }, [write], creating: true), "VALIDATION_FAILED");
        Assert.Contains("people[1].person_detail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyFieldsInsideEntriesTakeOnlyTheirDefault()
    {
        var group = Group("items");
        var state = Field("item_state", "Option", group);
        var options = Opts(state, "new", "used");
        var layout = Layout(Repeating(group, null, Element(state, readOnly: true, defaults: ["new"])));
        IReadOnlyList<FieldDefinition> fields = [state];

        var created = Apply(layout, fields, options, [group], null, null, [new CandidateBuilder.GroupWrite("items", [Entry(null), Entry(null, ("item_state", "new"))])], creating: true);
        Assert.All(created.Occurrences, o => Assert.Equal("new", WireIn(StateOf(created), state, o.FormRecordGroupId, options)));
        Fails(() => Apply(layout, fields, options, [group], null, null, [new CandidateBuilder.GroupWrite("items", [Entry(null, ("item_state", "used"))])], creating: true), "FIELD_READ_ONLY");

        // A new entry added later also receives the default; an existing entry cannot be changed.
        var current = StateOf(created);
        var ids = current.Occurrences.Select(o => o.FormRecordGroupId).ToList();
        var added = Apply(layout, fields, options, [group], current, null, [new CandidateBuilder.GroupWrite("items", [Entry(ids[0]), Entry(ids[1]), Entry(null)])]);
        Assert.Equal("new", WireIn(StateOf(added), state, added.GroupChanges.Single().AddedOccurrenceIds.Single(), options));
        Fails(() => Apply(layout, fields, options, [group], current, null, [new CandidateBuilder.GroupWrite("items", [Entry(ids[0], ("item_state", "used")), Entry(ids[1])])]), "FIELD_READ_ONLY");
    }

    [Fact]
    public void EncryptedFieldsInsideEntriesAreSealedPerOccurrence()
    {
        var p = new People(encryptedName: true);
        var state = p.Create(Entry(null, ("person_name", "Ada")), Entry(null, ("person_name", "Ada")));
        var sealedRows = state.Values.Where(v => v.FieldDefinitionId == p.Name.FieldDefinitionId).ToList();
        Assert.Equal(2, sealedRows.Count);
        Assert.All(sealedRows, r => Assert.IsType<FieldEncryptedValue>(r));
        Assert.Equal(state.Occurrences.Select(o => (Guid?)o.FormRecordGroupId).OrderBy(i => i), sealedRows.Select(r => r.FormRecordGroupId).OrderBy(i => i));
        Assert.DoesNotContain(state.Values.OfType<FieldStringValue>(), v => v.StringValue == "Ada");
        // Same plaintext in two entries, distinct ciphertext (unique nonce/item binding).
        var envelopes = sealedRows.Cast<FieldEncryptedValue>().Select(r => Convert.ToHexString(r.EncryptedValueBytes)).ToList();
        Assert.NotEqual(envelopes[0], envelopes[1]);
        Assert.Equal("Ada", WireIn(state, p.Name, state.Occurrences[1].FormRecordGroupId, p.Options));
    }

    [Fact]
    public void ASharedContributorCannotChangeEntries()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")));
        var editable = state.Values.Select(v => v.FieldValueItemId).ToHashSet();
        Fails(() => p.Write(state, [Entry(state.Occurrences[0].FormRecordGroupId, ("person_name", "Eve"))], editable: editable), "FORBIDDEN");
    }

    [Fact]
    public void ACallerWithoutGroupStateCannotSilentlyDropEntries()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")));
        Assert.Throws<InvalidOperationException>(() => CandidateBuilder.ApplyValuesDetailed(state.Values, new Dictionary<string, string>(), p.Layout, p.Fields, p.Options, null, Tenant, Record, Now, Actor, p.Options.ToDictionary(o => o.FieldOptionId), Encryption));
    }

    [Fact]
    public void CandidatesOverTheAuthorityCeilingAreRefusedWithAClearReason()
    {
        CandidateBuilder.EnsureWithinCeiling(CandidateBuilder.MaxCandidateItems);
        var ex = Fails(() => CandidateBuilder.EnsureWithinCeiling(CandidateBuilder.MaxCandidateItems + 1), "RECORD_TOO_LARGE");
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    // ---- signed occurrence rows ---------------------------------------------------------------------------------------

    [Fact]
    public void OccurrenceRowsAreSignedSoMovingOrReorderingChangesTheCanonicalBytes()
    {
        var p = new People();
        var state = p.Create(Entry(null, ("person_name", "Ada")), Entry(null, ("person_name", "Bo")));
        var occurrence = state.Occurrences[0];
        occurrence.ContentChangeOperationId = Guid.CreateVersion7();
        occurrence.ContentRevision = 1;
        occurrence.RowRevision = 1;
        occurrence.RowSignatureKeyVersion = 1;
        occurrence.RowSignatureSchemaVersion = BusinessSignedSchemas.CurrentSchemaVersion(occurrence);
        var original = BusinessSignedSchemas.Encode(occurrence, 1);
        Assert.Equal(original, BusinessSignedSchemas.Encode(occurrence, 1));

        var reordered = CandidateBuilder.CloneOccurrence(occurrence, 7);
        Copy(occurrence, reordered);
        Assert.NotEqual(original, BusinessSignedSchemas.Encode(reordered, 1));

        var reparented = CandidateBuilder.CloneOccurrence(occurrence);
        Copy(occurrence, reparented);
        reparented.GroupDefinitionId = Guid.CreateVersion7();
        Assert.NotEqual(original, BusinessSignedSchemas.Encode(reparented, 1));

        // A per-operation copy with the same content (only the physical row id differs) encodes identically.
        var copy = CandidateBuilder.CloneOccurrence(occurrence);
        Copy(occurrence, copy);
        Assert.Equal(original, BusinessSignedSchemas.Encode(copy, 1));

        // A value moved to the other entry changes the value row's signed bytes (form_record_group_id is a signed column).
        var value = state.Values.First(v => v.FormRecordGroupId == occurrence.FormRecordGroupId);
        value.ContentChangeOperationId = occurrence.ContentChangeOperationId;
        value.ContentRevision = 1;
        value.RowRevision = 1;
        value.RowSignatureKeyVersion = 1;
        value.RowSignatureSchemaVersion = BusinessSignedSchemas.CurrentSchemaVersion(value);
        var before = BusinessSignedSchemas.Encode(value, 1);
        value.FormRecordGroupId = state.Occurrences[1].FormRecordGroupId;
        Assert.NotEqual(before, BusinessSignedSchemas.Encode(value, 1));

        static void Copy(FormRecordGroupRevision from, FormRecordGroupRevision to)
        {
            to.ContentChangeOperationId = from.ContentChangeOperationId;
            to.ContentRevision = from.ContentRevision;
            to.RowRevision = from.RowRevision;
            to.RowSignatureKeyVersion = from.RowSignatureKeyVersion;
            to.RowSignatureSchemaVersion = from.RowSignatureSchemaVersion;
        }
    }

    // ---- layout contract ----------------------------------------------------------------------------------------------

    [Fact]
    public void RepeatAndOptionFilterRoundTripAndLegacyLayoutsDoNotMentionThem()
    {
        var group = Group("people");
        var country = Field("country", "Option");
        var province = Field("province", "Option");
        var name = Field("person_name", "String", group);
        var filter = new LayoutOptionFilter(country.FieldDefinitionId, new Dictionary<string, IReadOnlyList<string>> { ["ontario"] = ["canada"], ["quebec"] = ["canada"] });
        var layout = Layout(TopSection(Element(country), Element(province, filter: filter)), Repeating(group, Settings(group, 1, 4, add: true, remove: false, reorder: true), Element(name)));
        var json = FormLayoutCodec.Serialize(layout);
        var parsed = FormLayoutCodec.Parse(json);
        Assert.Equal(json, FormLayoutCodec.Serialize(parsed));
        var repeat = parsed.Sections[1].Repeat!;
        Assert.Equal((group.GroupDefinitionId, 1, 4, true, false, true), (repeat.GroupDefinitionId, repeat.MinOccurrences, repeat.MaxOccurrences, repeat.AllowAdd, repeat.AllowRemove, repeat.AllowReorder));
        Assert.Equal(["canada"], parsed.Sections[0].Elements[1].OptionFilter!.ParentOptionKeysByOption["ontario"]);

        var plain = FormLayoutCodec.Serialize(Layout(TopSection(Element(country))));
        Assert.DoesNotContain("repeat", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("optionFilter", plain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"groupDefinitionId\":\"GID\",\"minOccurrences\":3,\"maxOccurrences\":2}")]
    [InlineData("{\"groupDefinitionId\":\"GID\",\"minOccurrences\":0,\"maxOccurrences\":51}")]
    [InlineData("{\"groupDefinitionId\":\"GID\",\"minOccurrences\":-1,\"maxOccurrences\":2}")]
    [InlineData("{\"groupDefinitionId\":\"GID\",\"minOccurrences\":0,\"maxOccurrences\":0}")]
    [InlineData("{\"groupDefinitionId\":\"GID\",\"minOccurrences\":0,\"maxOccurrences\":3,\"allowAdd\":false}")]
    [InlineData("{\"minOccurrences\":0,\"maxOccurrences\":3}")]
    [InlineData("\"yes\"")]
    public void InvalidRepeatSettingsAreRejected(string repeat)
    {
        var json = "{\"layoutSchemaVersion\":2,\"title\":\"t\",\"sections\":[{\"sectionKey\":\"people\",\"title\":\"People\",\"gridColumns\":4,\"repeat\":" + repeat.Replace("GID", Guid.CreateVersion7().ToString("D"), StringComparison.Ordinal) + ",\"elements\":[]}]}";
        Fails(() => FormLayoutCodec.Parse(json), "LAYOUT_INVALID");
    }

    [Theory]
    [InlineData("{\"parentFieldDefinitionId\":\"PID\",\"parentOptionKeysByOption\":{\"Ontario\":[\"canada\"]}}")]
    [InlineData("{\"parentFieldDefinitionId\":\"PID\",\"parentOptionKeysByOption\":{\"ontario\":\"canada\"}}")]
    [InlineData("{\"parentFieldDefinitionId\":\"PID\",\"parentOptionKeysByOption\":{\"ontario\":[\"<script>\"]}}")]
    [InlineData("{\"parentOptionKeysByOption\":{}}")]
    [InlineData("{\"parentFieldDefinitionId\":\"PID\"}")]
    public void InvalidOptionFiltersAreRejected(string filter)
    {
        var field = Guid.CreateVersion7();
        var json = "{\"layoutSchemaVersion\":2,\"title\":\"t\",\"sections\":[{\"sectionKey\":\"main\",\"title\":\"Main\",\"gridColumns\":4,\"elements\":[{\"kind\":\"field\",\"fieldDefinitionId\":\"" + field.ToString("D") + "\",\"control\":\"select\",\"label\":\"x\",\"layout\":{\"columnSpan\":4,\"startOnNewRow\":false},\"optionFilter\":" + filter.Replace("PID", Guid.CreateVersion7().ToString("D"), StringComparison.Ordinal) + "}]}]}";
        Fails(() => FormLayoutCodec.Parse(json), "LAYOUT_INVALID");
    }

    // ---- design-time validation ---------------------------------------------------------------------------------------

    private static void Validate(FormLayout layout, IReadOnlyList<FieldDefinition> fields, IReadOnlyList<GroupDefinition> groups, Dictionary<Guid, List<FieldOption>>? options = null) =>
        FormRules.ValidateLayout(layout, fields.ToDictionary(f => f.FieldDefinitionId), options ?? [], groups.Select(g => g.GroupDefinitionId).ToHashSet());

    private static void Rejects(Action action, string fragment)
    {
        var ex = Fails(action, "LAYOUT_INVALID");
        Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FieldPlacementRespectsRepeatingSections()
    {
        var group = Group("people");
        var other = Group("vehicles");
        var title = Field("case_title", "String");
        var name = Field("person_name", "String", group);
        var plate = Field("plate", "String", other);
        var docs = Field("case_documents", "File");
        Validate(Layout(TopSection(Element(title)), Repeating(group, null, Element(name))), [title, name], [group]);

        Rejects(() => Validate(Layout(TopSection(Element(title), Element(name))), [title, name], [group]), "can only be placed in that section");
        Rejects(() => Validate(Layout(TopSection(), Repeating(group, null, Element(title))), [title], [group]), "one value per record");
        Rejects(() => Validate(Layout(Repeating(group, null, Element(plate))), [plate], [group, other]), "can only be placed in that section");
        Rejects(() => Validate(Layout(Repeating(group, null, Element(name))), [name], []), "does not belong");
        Rejects(() => Validate(Layout(Repeating(group, null, Element(name)), new LayoutSection("again", "Again", 4, [], null, Settings(group))), [name], [group]), "one section only");
        Rejects(() => Validate(Layout(Repeating(group, null, Element(name), Element(docs))), [name, docs], [group]), "document collections");
    }

    [Fact]
    public void RuleScopesFollowTheEntryBoundary()
    {
        var group = Group("people");
        var other = Group("vehicles");
        var kind = Field("case_kind", "String");
        var role = Field("person_role", "String", group);
        var notes = Field("person_notes", "String", group);
        var plate = Field("plate", "String", other);
        IReadOnlyList<GroupDefinition> groups = [group, other];

        // Same entry and record level: allowed.
        Validate(Layout(TopSection(Element(kind)), Repeating(group, null, Element(role), Element(notes, visibleWhen: Eq(role, "x"), requiredWhen: Eq(kind, "y")))), [kind, role, notes], groups);

        Rejects(() => Validate(Layout(TopSection(Element(kind, visibleWhen: Eq(role, "x"))), Repeating(group, null, Element(role))), [kind, role], groups), "one value per entry");
        Rejects(() => Validate(Layout(Repeating(group, null, Element(role, visibleWhen: Eq(plate, "x"))), Repeating(other, null, Element(plate))), [role, plate], groups), "same section");
    }

    [Fact]
    public void DependentDropdownsValidateParentsKeysScopesAndCycles()
    {
        var group = Group("addresses");
        var country = Field("country", "Option");
        var province = Field("province", "Option");
        var city = Field("city", "Option");
        var note = Field("note", "String");
        var street = Field("street_province", "Option", group);
        var countryOptions = Opts(country, "canada", "usa");
        var provinceOptions = Opts(province, "ontario", "quebec", "texas");
        var cityOptions = Opts(city, "toronto", "montreal", "austin");
        var streetOptions = Opts(street, "ontario", "texas");
        var options = new Dictionary<Guid, List<FieldOption>>
        {
            [country.FieldDefinitionId] = countryOptions, [province.FieldDefinitionId] = provinceOptions, [city.FieldDefinitionId] = cityOptions, [street.FieldDefinitionId] = streetOptions,
        };
        LayoutOptionFilter By(FieldDefinition parent, params (string Child, string[] Parents)[] map) => new(parent.FieldDefinitionId, map.ToDictionary(m => m.Child, m => (IReadOnlyList<string>)m.Parents));
        IReadOnlyList<FieldDefinition> fields = [country, province, city, note, street];
        IReadOnlyList<GroupDefinition> groups = [group];

        var provinceFilter = By(country, ("ontario", ["canada"]), ("quebec", ["canada"]), ("texas", ["usa"]));
        var cityFilter = By(province, ("toronto", ["ontario"]), ("montreal", ["quebec"]), ("austin", ["texas"]));
        // Country → Province → City, plus an entry-level dropdown depending on the record-level country.
        Validate(Layout(TopSection(Element(country), Element(province, filter: provinceFilter), Element(city, filter: cityFilter)), Repeating(group, null, Element(street, filter: By(country, ("ontario", ["canada"]))))), fields, groups, options);

        Rejects(() => Validate(Layout(TopSection(Element(province, filter: By(province, ("ontario", ["ontario"]))))), fields, groups, options), "by itself");
        Rejects(() => Validate(Layout(TopSection(Element(note), Element(province, filter: new LayoutOptionFilter(note.FieldDefinitionId, new Dictionary<string, IReadOnlyList<string>>())))), fields, groups, options), "must be a dropdown");
        Rejects(() => Validate(Layout(TopSection(Element(province, filter: provinceFilter))), fields, groups, options), "must be a dropdown placed");
        Rejects(() => Validate(Layout(TopSection(Element(country), Element(province, filter: By(country, ("nowhere", ["canada"]))))), fields, groups, options), "is not an option of this dropdown");
        Rejects(() => Validate(Layout(TopSection(Element(country), Element(province, filter: By(country, ("ontario", ["mars"]))))), fields, groups, options), "is not an option of 'country'");
        Rejects(() => Validate(Layout(TopSection(Element(note, filter: By(country)), Element(country))), fields, groups, options), "only dropdowns");
        // A record-level dropdown cannot depend on a per-entry dropdown.
        Rejects(() => Validate(Layout(TopSection(Element(province, filter: By(street, ("ontario", ["ontario"])))), Repeating(group, null, Element(street))), fields, groups, options), "same repeating section");

        // Country → Province → City → Country is circular.
        var countryFilter = By(city, ("canada", ["toronto"]));
        Rejects(() => Validate(Layout(TopSection(Element(country, filter: countryFilter), Element(province, filter: provinceFilter), Element(city, filter: cityFilter))), fields, groups, options), "circular dropdown dependency");
    }

    [Fact]
    public void DependentOptionValidityUsesStableKeysWithUnmappedOptionsAlwaysAvailable()
    {
        var province = Field("province", "Option");
        var tags = Field("tags", "Option", mode: "Multiple");
        var filter = new LayoutOptionFilter(Guid.CreateVersion7(), new Dictionary<string, IReadOnlyList<string>> { ["ontario"] = ["canada"], ["texas"] = ["usa"] });

        Assert.Null(FormRules.DependentOptionViolation(filter, province, "ontario", "canada"));
        Assert.Equal("ontario", FormRules.DependentOptionViolation(filter, province, "ontario", "usa"));
        Assert.Equal("ontario", FormRules.DependentOptionViolation(filter, province, "ontario", ""));
        Assert.Null(FormRules.DependentOptionViolation(filter, province, "elsewhere", ""));
        Assert.Null(FormRules.DependentOptionViolation(filter, province, "", "usa"));
        // Multi-select parent and child.
        Assert.Null(FormRules.DependentOptionViolation(filter, tags, "ontario,texas", "canada,usa"));
        Assert.Equal("texas", FormRules.DependentOptionViolation(filter, tags, "ontario,texas", "canada"));
    }

    [Fact]
    public void TheServerRefusesInvalidDependentSelectionsWithoutClearingThem()
    {
        var group = Group("addresses");
        var country = Field("country", "Option");
        var province = Field("province", "Option");
        var entryProvince = Field("entry_province", "Option", group);
        var options = Opts(country, "canada", "usa").Concat(Opts(province, "ontario", "texas")).Concat(Opts(entryProvince, "ontario", "texas")).ToList();
        var map = new Dictionary<string, IReadOnlyList<string>> { ["ontario"] = ["canada"], ["texas"] = ["usa"] };
        var layout = Layout(
            TopSection(Element(country), Element(province, filter: new LayoutOptionFilter(country.FieldDefinitionId, map))),
            Repeating(group, null, Element(entryProvince, filter: new LayoutOptionFilter(country.FieldDefinitionId, map))));
        IReadOnlyList<FieldDefinition> fields = [country, province, entryProvince];

        var ok = Apply(layout, fields, options, [group], null, new() { ["country"] = "canada", ["province"] = "ontario" }, [new CandidateBuilder.GroupWrite("addresses", [Entry(null, ("entry_province", "ontario"))])], creating: true);
        var state = StateOf(ok);

        var ex = Fails(() => Apply(layout, fields, options, [group], null, new() { ["country"] = "usa", ["province"] = "ontario" }, null, creating: true), "DEPENDENT_OPTION_INVALID");
        Assert.Contains("province", ex.Message, StringComparison.Ordinal);

        // Changing the parent while leaving a now-invalid child (top level and inside the entry) is refused; nothing is cleared.
        var change = Fails(() => Apply(layout, fields, options, [group], state, new() { ["country"] = "usa" }, null), "DEPENDENT_OPTION_INVALID");
        Assert.Contains("addresses[1].entry_province", change.Message, StringComparison.Ordinal);
        Assert.Contains(": province, ", change.Message, StringComparison.Ordinal);

        // Fixing both children in the same write succeeds.
        var occurrence = state.Occurrences.Single().FormRecordGroupId;
        var fixedResult = StateOf(Apply(layout, fields, options, [group], state, new() { ["country"] = "usa", ["province"] = "texas" }, [new CandidateBuilder.GroupWrite("addresses", [Entry(occurrence, ("entry_province", "texas"))])]));
        Assert.Equal("texas", WireIn(fixedResult, entryProvince, occurrence, options));
    }
}
