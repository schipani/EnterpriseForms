using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C2: dropdown / multi-select write path and the declarative rule engine (conditional visibility, conditional
/// required, Show in form, Read-only, hidden-value retention). The shared vectors in the frontend package keep the .NET
/// and TypeScript evaluators identical.
/// </summary>
public sealed class PhaseC2RulesAndOptionsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly AesGcmFieldEncryptionService Encryption = new(new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));

    private static string VectorsPath => Path.Combine(FindRoot(), "src", "frontend", "packages", "form-renderer", "src", "rule-vectors.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static FieldDefinition Field(string key, string type, string mode = "Single", string protection = "Standard") => new()
    {
        FieldDefinitionId = Guid.CreateVersion7(), FieldKey = key, FieldDisplayName = key, FieldDataType = type, FieldClassification = protection == "Standard" ? "Internal" : "Confidential", FieldProtectionMode = protection, OptionSelectionMode = mode,
    };

    private static List<FieldOption> Opts(FieldDefinition field, params string[] keys) =>
        keys.Select((k, i) => new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = field.FieldDefinitionId, OptionKey = k, OptionDisplayText = k.ToUpperInvariant(), OptionSortOrder = i, IsActive = true }).ToList();

    private static LayoutElement Element(FieldDefinition field, string control = "text", bool required = false, RuleCondition? visibleWhen = null, RuleCondition? requiredWhen = null, bool readOnly = false, bool showInForm = true, bool clearWhenHidden = false, IReadOnlyList<string>? defaults = null) =>
        new(LayoutElementKinds.Field, field.FieldDefinitionId, null, control, field.FieldDisplayName, null, new LayoutPlacement(4, false), new LayoutValidation(required, null, null, null, null, null, null, null), null, null,
            DefaultOptionKeys: defaults, ShowInForm: showInForm, ReadOnly: readOnly, ClearWhenHidden: clearWhenHidden, VisibleWhen: visibleWhen, RequiredWhen: requiredWhen);

    private static FormLayout Layout(params LayoutElement[] elements) => new(2, "Test", [new LayoutSection("main", "Main", 4, elements)]);

    private static RuleComparison Eq(FieldDefinition field, string value) => new(RuleOperators.Equal, field.FieldDefinitionId, value, null);

    private static CandidateBuilder.ApplyResult Apply(FormLayout layout, IReadOnlyList<FieldDefinition> fields, IReadOnlyList<FieldOption> options, IReadOnlyList<FieldValueRowBase> current, Dictionary<string, string> values, IReadOnlySet<Guid>? editable = null, RecordWriteOrigin origin = RecordWriteOrigin.User, bool creating = false) =>
        CandidateBuilder.ApplyValuesDetailed(current, values, layout, fields, options, editable, Tenant, Record, Now, Actor, options.ToDictionary(o => o.FieldOptionId), Encryption, origin, creating);

    private static List<FieldValueRowBase> Stored(IReadOnlyList<FieldDefinition> fields, IReadOnlyList<FieldOption> options, Dictionary<string, string> values) =>
        CandidateBuilder.ApplyValues([], values, Layout(fields.Select(f => Element(f, f.FieldDataType == "Option" ? (f.OptionSelectionMode == "Multiple" ? "multiselect" : "select") : "text")).ToArray()), fields, options, null, Tenant, Record, Now, Actor, options.ToDictionary(o => o.FieldOptionId), Encryption, creating: true);

    private static string WireOf(IEnumerable<FieldValueRowBase> rows, FieldDefinition field, IReadOnlyList<FieldOption> options) =>
        FieldValueCodec.FormatField(field, rows.Where(r => r.FieldDefinitionId == field.FieldDefinitionId), options.ToDictionary(o => o.FieldOptionId), Encryption);

    // ---- shared vectors -------------------------------------------------------------------------------------------

    [Fact]
    public void SharedVectorsEvaluateIdenticallyToTheTypeScriptImplementation()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        var root = document.RootElement;
        var fields = root.GetProperty("fields").EnumerateArray().Select(f => new FieldDefinition
        {
            FieldDefinitionId = Guid.Parse(f.GetProperty("fieldDefinitionId").GetString()!), FieldKey = f.GetProperty("fieldKey").GetString()!, FieldDataType = f.GetProperty("fieldDataType").GetString()!, OptionSelectionMode = f.GetProperty("optionSelectionMode").GetString()!,
        }).ToDictionary(f => f.FieldDefinitionId);
        var idByKey = fields.Values.ToDictionary(f => f.FieldKey, f => f.FieldDefinitionId);

        var failures = new List<string>();
        var count = 0;
        foreach (var vector in root.GetProperty("cases").EnumerateArray())
        {
            count++;
            var condition = FormRulesCodec.Parse(vector.GetProperty("condition"));
            var values = vector.GetProperty("values").EnumerateObject().ToDictionary(p => idByKey[p.Name], p => p.Value.GetString()!);
            var actual = FormRules.Test(condition, fields, id => values.GetValueOrDefault(id) ?? string.Empty);
            if (actual != vector.GetProperty("expected").GetBoolean())
            {
                failures.Add(vector.GetProperty("name").GetString()!);
            }
        }

        Assert.Empty(failures);
        Assert.True(count >= 50, "the shared vectors must keep their breadth");
    }

    [Fact]
    public void SharedInvalidConditionsAreRejectedByTheParser()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        foreach (var vector in document.RootElement.GetProperty("invalid").EnumerateArray())
        {
            var ex = Assert.Throws<BusinessException>(() => FormRulesCodec.Parse(vector.GetProperty("condition")));
            Assert.Equal("LAYOUT_INVALID", ex.ErrorCode);
        }
    }

    // ---- layout contract ------------------------------------------------------------------------------------------

    [Fact]
    public void LayoutBehaviourPropertiesRoundTripAndLegacyLayoutsSerializeUnchanged()
    {
        var status = Field("status", "Option");
        var note = Field("note", "String");
        var rule = new RuleGroup(RuleOperators.And, [Eq(status, "open"), new RuleComparison(RuleOperators.IsNotEmpty, note.FieldDefinitionId, null, null)]);
        var layout = Layout(Element(status, "select", defaults: ["open"]), Element(note, visibleWhen: rule, requiredWhen: Eq(status, "open"), readOnly: true, showInForm: false, clearWhenHidden: true));
        var json = FormLayoutCodec.Serialize(layout);
        var parsed = FormLayoutCodec.Parse(json);
        Assert.Equal(json, FormLayoutCodec.Serialize(parsed));
        var element = parsed.Elements.Last();
        Assert.False(element.ShowInForm);
        Assert.True(element.ReadOnly);
        Assert.True(element.ClearWhenHidden);
        Assert.NotNull(element.VisibleWhen);
        Assert.NotNull(element.RequiredWhen);
        Assert.Equal(["open"], parsed.Elements.First().DefaultOptionKeys);

        // A layout that uses none of the Phase C2 properties does not mention them at all (published versions stay byte-identical).
        var plain = FormLayoutCodec.Serialize(Layout(Element(note)));
        foreach (var property in new[] { "showInForm", "readOnly", "clearWhenHidden", "visibleWhen", "requiredWhen", "defaultOptionKeys", "options" })
        {
            Assert.DoesNotContain(property, plain, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LayoutSnapshotsAreImmutableAndRejectUnsafeContent()
    {
        var status = Field("status", "Option");
        var options = Opts(status, "open", "closed");
        var layout = FormLayoutCodec.Parse(FormLayoutCodec.Serialize(Layout(Element(status, "select"))));
        var snapshot = EnterpriseForms.Application.Features.FormDesigner.FormDesignerHandlers.SnapshotLabels(layout, new Dictionary<Guid, FieldDefinition> { [status.FieldDefinitionId] = status }, new Dictionary<Guid, EnterpriseForms.Domain.Features.RelatedForms.FormRelationshipDefinition>(), new Dictionary<Guid, List<FieldOption>> { [status.FieldDefinitionId] = options });
        var published = snapshot.Elements.Single().Options!;
        Assert.Equal(["open", "closed"], published.Select(o => o.OptionKey));
        Assert.Equal(options[0].FieldOptionId, published[0].FieldOptionId);

        // Relabelling or deactivating a catalog option afterwards does not alter the already-serialized version.
        var serialized = FormLayoutCodec.Serialize(snapshot);
        options[0].OptionDisplayText = "Renamed";
        options[1].IsActive = false;
        Assert.Equal(serialized, FormLayoutCodec.Serialize(FormLayoutCodec.Parse(serialized)));
        Assert.Contains("\"label\":\"OPEN\"", serialized, StringComparison.Ordinal);

        var markup = serialized.Replace("OPEN", "<b>x</b>", StringComparison.Ordinal);
        Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(markup));
    }

    // ---- design-time validation -----------------------------------------------------------------------------------

    private static void Validate(FormLayout layout, IReadOnlyList<FieldDefinition> fields, Dictionary<Guid, List<FieldOption>>? options = null) =>
        FormRules.ValidateLayout(layout, fields.ToDictionary(f => f.FieldDefinitionId), options ?? []);

    [Fact]
    public void RuleValidationAcceptsSoundRulesAndRejectsBrokenOnesWithAnExplanation()
    {
        var kind = Field("kind", "Option");
        var kindOptions = Opts(kind, "individual", "company");
        var company = Field("company_name", "String");
        var age = Field("age", "Integer");
        var doc = Field("attachments", "File");
        var optionMap = new Dictionary<Guid, List<FieldOption>> { [kind.FieldDefinitionId] = kindOptions };

        Validate(Layout(Element(kind, "select"), Element(company, visibleWhen: Eq(kind, "company"))), [kind, company], optionMap);

        void Rejects(FormLayout layout, string fragment, params FieldDefinition[] fields)
        {
            var ex = Assert.Throws<BusinessException>(() => Validate(layout, fields, optionMap));
            Assert.Equal("LAYOUT_INVALID", ex.ErrorCode);
            Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Rejects(Layout(Element(kind, "select"), Element(company, visibleWhen: Eq(kind, "nonsense"))), "existing option", kind, company);
        Rejects(Layout(Element(age, "number"), Element(company, visibleWhen: new RuleComparison(RuleOperators.Contains, age.FieldDefinitionId, "1", null))), "cannot be used", age, company);
        Rejects(Layout(Element(age, "number"), Element(company, visibleWhen: new RuleComparison(RuleOperators.GreaterThan, age.FieldDefinitionId, "abc", null))), "whole number", age, company);
        Rejects(Layout(Element(company, visibleWhen: Eq(Field("ghost", "String"), "x"))), "does not belong", company);
        Rejects(Layout(Element(kind, "select"), Element(company, visibleWhen: Eq(company, "x"))), "cannot read the field it controls", kind, company);
        Rejects(Layout(Element(company, visibleWhen: Eq(kind, "company"))), "placed in this layout", kind, company);
        Rejects(Layout(Element(kind, "select"), Element(company, requiredWhen: new RuleComparison(RuleOperators.IsEmpty, doc.FieldDefinitionId, null, null))), "placed in this layout", kind, company, doc);
        Rejects(Layout(Element(company, showInForm: false, required: true)), "not shown in the form", company);
        Rejects(Layout(Element(company, readOnly: true, required: true)), "read-only", company);
        Rejects(Layout(Element(company, clearWhenHidden: true)), "clear-when-hidden", company);
    }

    [Fact]
    public void CircularDependenciesAreRejectedNamingTheCycle()
    {
        var a = Field("alpha", "String");
        var b = Field("beta", "String");
        var c = Field("gamma", "String");
        var two = Layout(Element(a, visibleWhen: Eq(b, "x")), Element(b, requiredWhen: Eq(a, "y")));
        var ex = Assert.Throws<BusinessException>(() => Validate(two, [a, b]));
        Assert.Contains("circular", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("beta", ex.Message, StringComparison.Ordinal);

        var three = Layout(Element(a, visibleWhen: Eq(b, "x")), Element(b, visibleWhen: Eq(c, "x")), Element(c, visibleWhen: Eq(a, "x")));
        Assert.Contains("circular", Assert.Throws<BusinessException>(() => Validate(three, [a, b, c])).Message, StringComparison.OrdinalIgnoreCase);

        // A chain (not a cycle) is fine, and so is two fields reading the same source.
        Validate(Layout(Element(a), Element(b, visibleWhen: Eq(a, "x")), Element(c, visibleWhen: Eq(b, "x"), requiredWhen: Eq(a, "x"))), [a, b, c]);
    }

    [Fact]
    public void DefaultSelectionsMustNameActiveOptionsAndFitTheSelectionMode()
    {
        var single = Field("status", "Option");
        var multi = Field("tags", "Option", "Multiple");
        var singleOptions = Opts(single, "open", "closed");
        var multiOptions = Opts(multi, "a", "b", "c");
        multiOptions[2].IsActive = false;
        var optionMap = new Dictionary<Guid, List<FieldOption>> { [single.FieldDefinitionId] = singleOptions, [multi.FieldDefinitionId] = multiOptions };

        void Check(LayoutElement element, FieldDefinition field, string? expectedFragment)
        {
            try
            {
                EnterpriseForms.Application.Features.FormDesigner.FormDesignerHandlers.ValidateOptionSettings(element, field, optionMap[field.FieldDefinitionId]);
                Assert.Null(expectedFragment);
            }
            catch (BusinessException business)
            {
                Assert.NotNull(expectedFragment);
                Assert.Contains(expectedFragment, business.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        Check(Element(single, "select", defaults: ["open"]), single, null);
        Check(Element(single, "select", defaults: ["open", "closed"]), single, "only one default");
        Check(Element(single, "select", defaults: ["nope"]), single, "not an option");
        Check(Element(multi, "multiselect", defaults: ["a", "b"]), multi, null);
        Check(Element(multi, "multiselect", defaults: ["c"]), multi, "deactivated");
    }

    // ---- rule evaluation ------------------------------------------------------------------------------------------

    [Fact]
    public void HiddenFieldsCountAsEmptyForTheRulesThatReadThemSoHidingPropagates()
    {
        var a = Field("alpha", "String");
        var b = Field("beta", "String");
        var c = Field("gamma", "String");
        var layout = Layout(Element(a), Element(b, visibleWhen: Eq(a, "show")), Element(c, visibleWhen: Eq(b, "x"), required: true));
        var fields = new[] { a, b, c }.ToDictionary(f => f.FieldDefinitionId);
        var visibleChain = FormRules.Evaluate(layout, fields, new Dictionary<Guid, string> { [a.FieldDefinitionId] = "show", [b.FieldDefinitionId] = "x" });
        Assert.True(visibleChain[b.FieldDefinitionId].Visible);
        Assert.True(visibleChain[c.FieldDefinitionId].Visible);
        Assert.True(visibleChain[c.FieldDefinitionId].Required);

        // alpha no longer says "show": beta hides, so its stored "x" no longer satisfies gamma's rule and gamma hides too (and is not required).
        var hidden = FormRules.Evaluate(layout, fields, new Dictionary<Guid, string> { [a.FieldDefinitionId] = "other", [b.FieldDefinitionId] = "x" });
        Assert.False(hidden[b.FieldDefinitionId].Visible);
        Assert.False(hidden[c.FieldDefinitionId].Visible);
        Assert.False(hidden[c.FieldDefinitionId].Required);
    }

    // ---- record write path ----------------------------------------------------------------------------------------

    [Fact]
    public void MultiSelectStoresOneRowPerOptionAndRoundTripsThroughUpdates()
    {
        var tags = Field("tags", "Option", "Multiple");
        var options = Opts(tags, "red", "green", "blue");
        var layout = Layout(Element(tags, "multiselect"));
        var created = Apply(layout, [tags], options, [], new() { ["tags"] = "blue,red" }, creating: true).Rows;
        Assert.Equal(2, created.Count);
        Assert.All(created, r => Assert.IsType<FieldOptionValue>(r));
        Assert.Equal(2, created.Select(r => r.FieldValueItemId).Distinct().Count());
        Assert.Equal("red,blue", WireOf(created, tags, options)); // display (sort) order, not input order

        // Unchanged keeps both items; adding one keeps the originals' identities; removing one drops only that row.
        var same = Apply(layout, [tags], options, created, new() { ["tags"] = "red,blue" }).Rows;
        Assert.Equal(created.Select(r => r.FieldValueItemId).Order(), same.Select(r => r.FieldValueItemId).Order());
        var added = Apply(layout, [tags], options, created, new() { ["tags"] = "red,green,blue" }).Rows;
        Assert.Equal(3, added.Count);
        Assert.All(created, original => Assert.Contains(added, r => r.FieldValueItemId == original.FieldValueItemId));
        var removed = Apply(layout, [tags], options, added, new() { ["tags"] = "green" }).Rows;
        var single = Assert.Single(removed);
        Assert.Equal("green", WireOf(removed, tags, options));
        Assert.Contains(added, r => r.FieldValueItemId == single.FieldValueItemId);
        Assert.Empty(Apply(layout, [tags], options, removed, new() { ["tags"] = string.Empty }).Rows);
    }

    [Fact]
    public void MultiSelectRejectsUnknownDuplicateAndInactiveOptionsAtomically()
    {
        var tags = Field("tags", "Option", "Multiple");
        var options = Opts(tags, "red", "green");
        options[1].IsActive = false;
        var layout = Layout(Element(tags, "multiselect"));
        Assert.Equal("VALIDATION_FAILED", Assert.Throws<BusinessException>(() => Apply(layout, [tags], options, [], new() { ["tags"] = "red,purple" }, creating: true)).ErrorCode);
        Assert.Equal("ORGANIZATION_OPTION_DISABLED", Assert.Throws<BusinessException>(() => Apply(layout, [tags], options, [], new() { ["tags"] = "red,green" }, creating: true)).ErrorCode);
        // Duplicate keys collapse to one selection rather than two rows.
        Assert.Single(Apply(layout, [tags], options, [], new() { ["tags"] = "red,red" }, creating: true).Rows);

        // A single-select dropdown does not accept a list.
        var status = Field("status", "Option");
        var statusOptions = Opts(status, "open", "closed");
        Assert.Throws<BusinessException>(() => Apply(Layout(Element(status, "select")), [status], statusOptions, [], new() { ["status"] = "open,closed" }, creating: true));
    }

    [Fact]
    public void EncryptedMultiSelectStoresOneCiphertextRowPerOptionWithNoTypedPlaintextCopy()
    {
        var tags = Field("tags", "Option", "Multiple", "ApplicationEncrypted");
        var options = Opts(tags, "red", "green");
        var rows = Apply(Layout(Element(tags, "multiselect")), [tags], options, [], new() { ["tags"] = "red,green" }, creating: true).Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.IsType<FieldEncryptedValue>(r));
        Assert.Equal("red,green", WireOf(rows, tags, options));
    }

    [Fact]
    public void ADeactivatedOptionKeepsItsHistoricalValueButBlocksANewSave()
    {
        var status = Field("status", "Option");
        var options = Opts(status, "open", "closed");
        var layout = Layout(Element(status, "select"));
        var stored = Apply(layout, [status], options, [], new() { ["status"] = "closed" }, creating: true).Rows;
        options[1].IsActive = false;

        // The stored row is untouched by deactivation and still formats to its key (the UI marks it "no longer available").
        Assert.Equal("closed", WireOf(stored, status, options));
        // Saving the record with that selection still in the editable scope is blocked until it is replaced; nothing is rewritten.
        Assert.Equal("ORGANIZATION_OPTION_DISABLED", Assert.Throws<BusinessException>(() => Apply(layout, [status], options, stored, new() { ["status"] = "closed" })).ErrorCode);
        var replaced = Apply(layout, [status], options, stored, new() { ["status"] = "open" }).Rows;
        Assert.Equal("open", WireOf(replaced, status, options));
    }

    [Fact]
    public void ConditionalRequiredIsEnforcedOnTheServerOnlyWhenTheRuleHolds()
    {
        var kind = Field("kind", "Option");
        var kindOptions = Opts(kind, "individual", "company");
        var company = Field("company_name", "String");
        var fields = new[] { kind, company };
        var options = kindOptions;
        var layout = Layout(Element(kind, "select"), Element(company, requiredWhen: Eq(kind, "company")));

        Apply(layout, fields, options, [], new() { ["kind"] = "individual" }, creating: true);
        var ex = Assert.Throws<BusinessException>(() => Apply(layout, fields, options, [], new() { ["kind"] = "company" }, creating: true));
        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
        Assert.Contains("company_name", ex.Message, StringComparison.Ordinal);
        Apply(layout, fields, options, [], new() { ["kind"] = "company", ["company_name"] = "Acme" }, creating: true);

        // Switching an existing record to "company" without a name fails; supplying it in the same save succeeds.
        var stored = Apply(layout, fields, options, [], new() { ["kind"] = "individual" }, creating: true).Rows;
        Assert.Throws<BusinessException>(() => Apply(layout, fields, options, stored, new() { ["kind"] = "company" }));
        Apply(layout, fields, options, stored, new() { ["kind"] = "company", ["company_name"] = "Acme" });
    }

    [Fact]
    public void AStaticallyRequiredFieldIsNotRequiredWhileItsVisibilityRuleHidesIt()
    {
        var kind = Field("kind", "Option");
        var options = Opts(kind, "individual", "company");
        var company = Field("company_name", "String");
        var layout = Layout(Element(kind, "select"), Element(company, required: true, visibleWhen: Eq(kind, "company")));
        Apply(layout, [kind, company], options, [], new() { ["kind"] = "individual" }, creating: true);
        Assert.Throws<BusinessException>(() => Apply(layout, [kind, company], options, [], new() { ["kind"] = "company" }, creating: true));
    }

    [Fact]
    public void AFieldHiddenByARuleCannotBeChangedButItsStoredValueIsRetainedByDefault()
    {
        var kind = Field("kind", "Option");
        var options = Opts(kind, "individual", "company");
        var company = Field("company_name", "String", protection: "ApplicationEncrypted");
        var fields = new[] { kind, company };
        var layout = Layout(Element(kind, "select"), Element(company, visibleWhen: Eq(kind, "company")));

        // A value cannot be written into a hidden field (create or update).
        var create = Assert.Throws<BusinessException>(() => Apply(layout, fields, options, [], new() { ["kind"] = "individual", ["company_name"] = "Sneaky" }, creating: true));
        Assert.Equal("FIELD_HIDDEN_BY_RULE", create.ErrorCode);

        var stored = Apply(layout, fields, options, [], new() { ["kind"] = "company", ["company_name"] = "Acme" }, creating: true).Rows;
        Assert.IsType<FieldEncryptedValue>(stored.Single(r => r.FieldDefinitionId == company.FieldDefinitionId));

        // Switching to "individual" hides the field; its sensitive value is retained (still sealed), not cleared or exposed.
        var retained = Apply(layout, fields, options, stored, new() { ["kind"] = "individual" });
        Assert.Empty(retained.ClearedByRuleFieldKeys);
        Assert.Equal("Acme", WireOf(retained.Rows, company, options));
        Assert.IsType<FieldEncryptedValue>(retained.Rows.Single(r => r.FieldDefinitionId == company.FieldDefinitionId));
        // The same save may carry the unchanged hidden value (the edit form always sends it) but not a different one.
        Apply(layout, fields, options, stored, new() { ["kind"] = "individual", ["company_name"] = "Acme" });
        Assert.Equal("FIELD_HIDDEN_BY_RULE", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, stored, new() { ["kind"] = "individual", ["company_name"] = "Changed" })).ErrorCode);
        Assert.Equal("FIELD_HIDDEN_BY_RULE", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, stored, new() { ["kind"] = "individual", ["company_name"] = string.Empty })).ErrorCode);
    }

    [Fact]
    public void ClearWhenHiddenRemovesTheValueOnlyBecauseTheDesignerExplicitlyAskedForIt()
    {
        var kind = Field("kind", "Option");
        var options = Opts(kind, "individual", "company");
        var company = Field("company_name", "String");
        var fields = new[] { kind, company };
        var layout = Layout(Element(kind, "select"), Element(company, visibleWhen: Eq(kind, "company"), clearWhenHidden: true));
        var stored = Apply(layout, fields, options, [], new() { ["kind"] = "company", ["company_name"] = "Acme" }, creating: true).Rows;
        var result = Apply(layout, fields, options, stored, new() { ["kind"] = "individual" });
        Assert.Equal(["company_name"], result.ClearedByRuleFieldKeys);
        Assert.DoesNotContain(result.Rows, r => r.FieldDefinitionId == company.FieldDefinitionId);
        // Showing it again later starts empty.
        var again = Apply(layout, fields, options, result.Rows, new() { ["kind"] = "company" }).Rows;
        Assert.DoesNotContain(again, r => r.FieldDefinitionId == company.FieldDefinitionId);
    }

    [Fact]
    public void ReadOnlyFieldsRejectUserChangesAcceptOnlyTheDefaultOnCreateAndStillAllowAuthorizedSystemWrites()
    {
        var status = Field("status", "Option");
        var options = Opts(status, "draft", "final");
        var note = Field("note", "String");
        var fields = new[] { status, note };
        var layout = Layout(Element(status, "select", readOnly: true, required: true, defaults: ["draft"]), Element(note, readOnly: true));

        // Create: the default is applied by the server even when the form omitted it; any other value is refused.
        var created = Apply(layout, fields, options, [], new(), creating: true).Rows;
        Assert.Equal("draft", WireOf(created, status, options));
        Apply(layout, fields, options, [], new() { ["status"] = "draft" }, creating: true);
        Assert.Equal("FIELD_READ_ONLY", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, [], new() { ["status"] = "final" }, creating: true)).ErrorCode);
        Assert.Equal("FIELD_READ_ONLY", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, [], new() { ["status"] = "draft", ["note"] = "typed" }, creating: true)).ErrorCode);

        // Update: unchanged values pass (the edit form submits them), changes and clears do not.
        Apply(layout, fields, options, created, new() { ["status"] = "draft", ["note"] = string.Empty });
        Assert.Equal("FIELD_READ_ONLY", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, created, new() { ["status"] = "final" })).ErrorCode);
        Assert.Equal("FIELD_READ_ONLY", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, created, new() { ["status"] = string.Empty })).ErrorCode);
        Assert.Equal("FIELD_READ_ONLY", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, created, new() { ["note"] = "typed" })).ErrorCode);

        // A separately authorized system operation (never reachable from the public API) may update the value.
        var system = Apply(layout, fields, options, created, new() { ["status"] = "final" }, origin: RecordWriteOrigin.System).Rows;
        Assert.Equal("final", WireOf(system, status, options));
    }

    [Fact]
    public void ASharedContributorCannotChangeReadOnlyFieldsMultiSelectionsOrFieldsOutsideTheirScope()
    {
        var tags = Field("tags", "Option", "Multiple");
        var options = Opts(tags, "a", "b");
        var note = Field("note", "String");
        var fields = new[] { tags, note };
        var layout = Layout(Element(tags, "multiselect"), Element(note, readOnly: true));
        var stored = Apply(Layout(Element(tags, "multiselect"), Element(note)), fields, options, [], new() { ["tags"] = "a", ["note"] = "kept" }, creating: true).Rows;
        var scope = stored.Select(r => r.FieldValueItemId).ToHashSet();

        Assert.Equal("FIELD_READ_ONLY", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, stored, new() { ["note"] = "changed" }, scope)).ErrorCode);
        Assert.Equal("FORBIDDEN", Assert.Throws<BusinessException>(() => Apply(layout, fields, options, stored, new() { ["tags"] = "a,b" }, scope)).ErrorCode);
        // Nothing about conditional rules is evaluated or revealed for a partial scope.
        var ruled = Layout(Element(tags, "multiselect"), Element(note, requiredWhen: Eq(Field("hidden_source", "String"), "x")));
        Apply(ruled, fields, options, stored, new() { ["tags"] = "a" }, scope);
    }

    [Fact]
    public void APartialProjectionNeverShipsRuleMetadataAboutFieldsTheViewerCannotSee()
    {
        var kind = Field("kind", "Option");
        var company = Field("company_name", "String");
        var layout = Layout(Element(kind, "select"), Element(company, visibleWhen: Eq(kind, "company"), requiredWhen: Eq(kind, "company"), clearWhenHidden: true, readOnly: true));
        // The viewer is shared only the company name: the rule that reads the (unshared) applicant kind must not travel with it.
        var reduced = EnterpriseForms.Application.Features.FormRecords.GetFormRecord.GetRecordProjectionHandler.AuthorizedLayout(layout, new HashSet<Guid> { company.FieldDefinitionId }, new HashSet<Guid>());
        var element = Assert.Single(Assert.Single(reduced.Sections).Elements);
        Assert.Null(element.VisibleWhen);
        Assert.Null(element.RequiredWhen);
        Assert.False(element.ClearWhenHidden);
        Assert.True(element.ReadOnly); // presentation of the viewer's own field is kept
        var json = FormLayoutCodec.Serialize(reduced);
        Assert.DoesNotContain(kind.FieldDefinitionId.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain("visibleWhen", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowInFormIsPresentationOnlyAndNeverAnAuthorizationDecision()
    {
        var note = Field("note", "String");
        var layout = Layout(Element(note, showInForm: false));
        // The server accepts, stores and returns a value for a field the form does not display: visibility in the form is not what protects it.
        var rows = Apply(layout, [note], [], [], new() { ["note"] = "api-written" }, creating: true).Rows;
        Assert.Equal("api-written", WireOf(rows, note, []));
        // A partial-scope writer is still limited to the items its share names, whatever the form shows.
        Assert.Equal("FORBIDDEN", Assert.Throws<BusinessException>(() => Apply(layout, [note], [], rows, new() { ["note"] = "other" }, new HashSet<Guid>())).ErrorCode);
    }
}
