using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C2 â€” conditional visibility, conditional required, Show in form and Read-only on the real stack. The server is
/// authoritative: rules are validated when a draft is saved/published and enforced on every record write, hidden values
/// are retained (cleared only on explicit opt-in), read-only fields cannot be changed by users, and none of the
/// presentation settings changes who may read or write what.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class RulesTests(StageBFixture fixture)
{
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    private sealed class Intake
    {
        public Guid FormId { get; init; }

        public Guid Kind { get; init; }

        public Guid CompanyName { get; init; }

        public Guid Age { get; init; }

        public Guid GuardianName { get; init; }

        public Guid Status { get; init; }

        public Guid ReferenceNote { get; init; }

        public Guid SideNote { get; init; }
    }

    private static object Eq(Guid field, string value) => new { op = "equals", fieldDefinitionId = field, value };

    private static string Element(Guid fieldId, string control, string label, bool required = false, Dictionary<string, object?>? extra = null)
    {
        var element = new Dictionary<string, object?> { ["kind"] = "field", ["fieldDefinitionId"] = fieldId, ["control"] = control, ["label"] = label, ["required"] = required, ["layout"] = new { columnSpan = 2, startOnNewRow = false }, ["validation"] = new { } };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                element[key] = value;
            }
        }

        return JsonSerializer.Serialize(element);
    }

    private async Task<Guid> AddAsync(StageBScenario s, Guid formId, string key, string name, string type, string[]? options = null)
    {
        var result = await fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", s.Token(s.Designer), new { fieldKey = key, fieldDisplayName = name, fieldDescription = string.Empty, fieldDataType = type, options = options?.Select(o => new { optionKey = o, optionDisplayText = char.ToUpperInvariant(o[0]) + o[1..] }).ToArray() });
        Assert.Equal(HttpStatusCode.Created, result.Status);
        return result.Root.GetProperty("fieldDefinitionId").GetGuid();
    }

    private async Task<Intake> BuildIntakeAsync(StageBScenario s, bool publish = true)
    {
        var formId = await s.CreateFormAsync("Intake " + s.Name, s.Clerk);
        var intake = new Intake
        {
            FormId = formId,
            Kind = await AddAsync(s, formId, "applicant_kind", "Applicant kind", "Option", ["person", "company"]),
            CompanyName = await AddAsync(s, formId, "company_name", "Company name", "String"), // free text: Confidential, application-encrypted by default
            Age = await AddAsync(s, formId, "applicant_age", "Age", "Integer"),
            GuardianName = await AddAsync(s, formId, "guardian_name", "Guardian name", "String"),
            Status = await AddAsync(s, formId, "intake_status", "Intake status", "Option", ["draft", "final"]),
            ReferenceNote = await AddAsync(s, formId, "reference_note", "Reference note", "String"),
            SideNote = await AddAsync(s, formId, "side_note", "Side note", "String"),
        };
        if (publish)
        {
            await s.SaveDraftAsync(formId, IntakeLayout(intake));
            await s.PublishAsync(formId, "intake with rules");
        }

        return intake;
    }

    private static string IntakeLayout(Intake i) => StageBScenario.Layout("Intake", StageBScenario.Section("main", "Intake",
        Element(i.Kind, "select", "Applicant kind", required: true),
        Element(i.CompanyName, "text", "Company name", required: true, extra: new() { ["visibleWhen"] = Eq(i.Kind, "company") }),
        Element(i.Age, "number", "Age"),
        Element(i.GuardianName, "text", "Guardian name", extra: new() { ["requiredWhen"] = new { op = "lessThan", fieldDefinitionId = i.Age, value = "18" } }),
        Element(i.Status, "select", "Intake status", extra: new() { ["readOnly"] = true, ["defaultOptionKeys"] = new[] { "draft" } }),
        Element(i.ReferenceNote, "text", "Reference note", extra: new() { ["showInForm"] = false }),
        Element(i.SideNote, "text", "Side note", extra: new() { ["visibleWhen"] = Eq(i.Kind, "company"), ["clearWhenHidden"] = true })));

    private async Task<HttpResult> CreateAsync(StageBScenario s, Intake intake, TestUser user, Dictionary<string, string> values) =>
        await fixture.Api.PostAsync($"/api/v1/form-definitions/{intake.FormId:D}/records", s.Token(user), new { values, ownerTeamId = (Guid?)null });

    private async Task<HttpResult> EditAsync(StageBScenario s, Guid record, TestUser user, Dictionary<string, string> values)
    {
        var projection = await s.WaitProjectionAsync(user, record);
        return await fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(user), new { expectedContentRevision = projection.Root.GetProperty("record").GetProperty("contentRevision").GetInt64(), values });
    }

    private static Dictionary<string, string> Values(JsonElement projectionRoot) =>
        projectionRoot.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);

    [Fact]
    public async Task InvalidRulesAreRefusedWhenTheDraftIsSavedWithAnExplanation()
    {
        var s = new StageBScenario(fixture, "rules-invalid");
        await s.BootstrapAsync();
        var i = await BuildIntakeAsync(s, publish: false);
        var ghost = Guid.CreateVersion7();

        async Task Rejects(string fragment, params string[] elements)
        {
            var draft = await fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{i.FormId:D}/draft", s.Token(s.Designer), new { expectedDraftRevision = await s.CurrentDraftRevisionAsync(i.FormId), layoutJson = StageBScenario.Layout("x", StageBScenario.Section("main", "Main", elements)) }, null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, draft.Status);
            Assert.Equal("LAYOUT_INVALID", draft.ErrorCode);
            Assert.Contains(fragment, draft.Body, StringComparison.OrdinalIgnoreCase);
        }

        // Circular dependencies, direct and through a chain.
        await Rejects("circular", Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = Eq(i.SideNote, "x") }), Element(i.SideNote, "text", "S", extra: new() { ["requiredWhen"] = Eq(i.CompanyName, "y") }));
        await Rejects("circular", Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = Eq(i.SideNote, "x") }), Element(i.SideNote, "text", "S", extra: new() { ["visibleWhen"] = Eq(i.GuardianName, "y") }), Element(i.GuardianName, "text", "G", extra: new() { ["visibleWhen"] = Eq(i.CompanyName, "z") }));
        await Rejects("cannot read the field it controls", Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = Eq(i.CompanyName, "x") }));
        // References: unknown field, unplaced field, document collections.
        await Rejects("does not belong", Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = Eq(ghost, "x") }));
        await Rejects("placed in this layout", Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = Eq(i.SideNote, "x") }));
        // Operators and operands must fit the source type.
        await Rejects("cannot be used", Element(i.Age, "number", "A"), Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = new { op = "contains", fieldDefinitionId = i.Age, value = "1" } }));
        await Rejects("whole number", Element(i.Age, "number", "A"), Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = new { op = "greaterThan", fieldDefinitionId = i.Age, value = "abc" } }));
        await Rejects("existing option", Element(i.Kind, "select", "K"), Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = Eq(i.Kind, "nonsense") }));
        await Rejects("unknown rule operator", Element(i.Kind, "select", "K"), Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = new { op = "matches", fieldDefinitionId = i.Kind, value = "x" } }));
        // Behavior consistency.
        await Rejects("not shown in the form", Element(i.CompanyName, "text", "C", required: true, extra: new() { ["showInForm"] = false }));
        await Rejects("read-only", Element(i.CompanyName, "text", "C", required: true, extra: new() { ["readOnly"] = true }));
        await Rejects("clear-when-hidden", Element(i.CompanyName, "text", "C", extra: new() { ["clearWhenHidden"] = true }));
        // Nothing executable can ride along in the layout.
        await Rejects("plain text", Element(i.Kind, "select", "K"), Element(i.CompanyName, "text", "C", extra: new() { ["visibleWhen"] = new { op = "equals", fieldDefinitionId = i.Kind, value = "<script>alert(1)</script>" } }));

        // A sound draft with the same pieces is accepted.
        await s.SaveDraftAsync(i.FormId, IntakeLayout(i));
    }

    [Fact]
    public async Task RulesAreEnforcedOnTheServerForCreateAndEditAndPublishedVersionsStayImmutable()
    {
        var s = new StageBScenario(fixture, "rules-enforced");
        await s.BootstrapAsync();
        var i = await BuildIntakeAsync(s);

        // The published layout carries the rules and behavior flags exactly as authored.
        var v1 = (await s.GetFormAsync(i.FormId)).GetProperty("currentVersion").GetProperty("layoutJson").GetString()!;
        using (var doc = JsonDocument.Parse(v1))
        {
            var elements = doc.RootElement.GetProperty("sections")[0].GetProperty("elements").EnumerateArray().ToDictionary(e => e.GetProperty("fieldDefinitionId").GetGuid());
            Assert.Equal("company", elements[i.CompanyName].GetProperty("visibleWhen").GetProperty("value").GetString());
            Assert.Equal("lessThan", elements[i.GuardianName].GetProperty("requiredWhen").GetProperty("op").GetString());
            Assert.True(elements[i.Status].GetProperty("readOnly").GetBoolean());
            Assert.False(elements[i.ReferenceNote].GetProperty("showInForm").GetBoolean());
            Assert.True(elements[i.SideNote].GetProperty("clearWhenHidden").GetBoolean());
        }

        // Conditional visibility + static required: a hidden required field is not required, a visible one is.
        var person = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person", ["applicant_age"] = "40" });
        Assert.Equal(HttpStatusCode.Created, person.Status);
        var companyMissing = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "company" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, companyMissing.Status);
        Assert.Equal("VALIDATION_FAILED", companyMissing.ErrorCode);
        Assert.Contains("company_name", companyMissing.Body, StringComparison.Ordinal);

        // Conditional required.
        var minorMissing = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person", ["applicant_age"] = "15" });
        Assert.Equal("VALIDATION_FAILED", minorMissing.ErrorCode);
        Assert.Contains("guardian_name", minorMissing.Body, StringComparison.Ordinal);
        var minor = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person", ["applicant_age"] = "15", ["guardian_name"] = "Parent Synthetic" });
        Assert.Equal(HttpStatusCode.Created, minor.Status);
        var adultNoGuardian = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person", ["applicant_age"] = "18" });
        Assert.Equal(HttpStatusCode.Created, adultNoGuardian.Status);

        // A field hidden by a rule cannot be written, whatever the client does.
        var hiddenWrite = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person", ["company_name"] = "Sneaky Ltd" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hiddenWrite.Status);
        Assert.Equal("FIELD_HIDDEN_BY_RULE", hiddenWrite.ErrorCode);

        // Read-only: the server supplies the default on create, refuses any other value, and refuses changes later.
        var personId = person.Root.GetProperty("formRecordId").GetGuid();
        var created = Values((await s.WaitProjectionAsync(s.Clerk, personId)).Root);
        Assert.Equal("draft", created["intake_status"]);
        var wrongStatus = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person", ["intake_status"] = "final" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongStatus.Status);
        Assert.Equal("FIELD_READ_ONLY", wrongStatus.ErrorCode);
        var change = await EditAsync(s, personId, s.Clerk, new() { ["intake_status"] = "final" });
        Assert.Equal("FIELD_READ_ONLY", change.ErrorCode);
        var clear = await EditAsync(s, personId, s.Clerk, new() { ["intake_status"] = string.Empty });
        Assert.Equal("FIELD_READ_ONLY", clear.ErrorCode);
        var unchanged = await EditAsync(s, personId, s.Clerk, new() { ["intake_status"] = "draft", ["applicant_age"] = "41" }); // an edit form submits unchanged read-only values
        Assert.True(unchanged.IsSuccess, unchanged.Body);
        Assert.Equal("draft", Values((await s.WaitProjectionAsync(s.Clerk, personId)).Root)["intake_status"]);

        // Edit-time conditional required: switching a person to a company needs the company name in the same save.
        var needsName = await EditAsync(s, personId, s.Clerk, new() { ["applicant_kind"] = "company" });
        Assert.Equal("VALIDATION_FAILED", needsName.ErrorCode);
        var switched = await EditAsync(s, personId, s.Clerk, new() { ["applicant_kind"] = "company", ["company_name"] = "Acme Synthetic Ltd" });
        Assert.True(switched.IsSuccess, switched.Body);

        // Publishing a new version leaves v1 untouched; the new rules apply to later saves.
        var v2Layout = StageBScenario.Layout("Intake", StageBScenario.Section("main", "Intake",
            Element(i.Kind, "select", "Applicant kind", required: true),
            Element(i.CompanyName, "text", "Company name"),
            Element(i.Age, "number", "Age", extra: new() { ["requiredWhen"] = Eq(i.Kind, "person") }),
            Element(i.GuardianName, "text", "Guardian name"),
            Element(i.Status, "select", "Intake status"),
            Element(i.ReferenceNote, "text", "Reference note"),
            Element(i.SideNote, "text", "Side note")));
        await s.SaveDraftAsync(i.FormId, v2Layout);
        await s.PublishAsync(i.FormId, "relax the rules, require age for people");
        var versions = (await fixture.Api.GetAsync($"/api/v1/form-definitions/{i.FormId:D}/versions", s.Token(s.Designer))).Root.EnumerateArray().ToList();
        Assert.Equal(v1, versions.Single(v => v.GetProperty("versionNumber").GetInt32() == 1).GetProperty("layoutJson").GetString());
        Assert.DoesNotContain("visibleWhen", versions.Single(v => v.GetProperty("versionNumber").GetInt32() == 2).GetProperty("layoutJson").GetString()!, StringComparison.Ordinal);
        // Under the new version: the hidden-field rule is gone (company name may be written for a person), read-only is gone, and age is required for people.
        var relaxed = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "company", ["intake_status"] = "final" });
        Assert.Equal(HttpStatusCode.Created, relaxed.Status);
        var ageRequired = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "person" });
        Assert.Equal("VALIDATION_FAILED", ageRequired.ErrorCode);
        Assert.Contains("applicant_age", ageRequired.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HiddenValuesAreRetainedClearedOnlyOnOptInAndPresentationSettingsNeverGrantOrWithholdAccess()
    {
        var s = new StageBScenario(fixture, "rules-retention");
        await s.BootstrapAsync();
        var i = await BuildIntakeAsync(s);

        var created = await CreateAsync(s, i, s.Clerk, new() { ["applicant_kind"] = "company", ["company_name"] = "Acme Synthetic Ltd", ["side_note"] = "temporary remark", ["reference_note"] = "api-only reference" });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var record = created.Root.GetProperty("formRecordId").GetGuid();
        var initial = Values((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal("Acme Synthetic Ltd", initial["company_name"]);
        // Show in form = false is presentation only: the API stored it and the owner can read it; the sensitive company name is ciphertext in the database.
        Assert.Equal("api-only reference", initial["reference_note"]);
        Assert.Equal("0", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_string_values WHERE form_record_id = '{record:D}';").Trim());
        Assert.DoesNotContain("Acme", fixture.Stack.AttackerSql($"SELECT encode(encrypted_value_bytes,'escape') FROM field_encrypted_values WHERE form_record_id = '{record:D}';"), StringComparison.OrdinalIgnoreCase);

        // Switching to a person hides both fields. The edit form sends the unchanged values: the sensitive one is retained (still ciphertext),
        // the one with clear-when-hidden is removed â€” only because the designer opted in.
        var toPerson = await EditAsync(s, record, s.Clerk, new() { ["applicant_kind"] = "person", ["company_name"] = "Acme Synthetic Ltd", ["side_note"] = "temporary remark" });
        Assert.True(toPerson.IsSuccess, toPerson.Body);
        var hidden = Values((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal("Acme Synthetic Ltd", hidden["company_name"]);
        Assert.False(hidden.ContainsKey("side_note"));
        Assert.Equal("person", hidden["applicant_kind"]);
        Assert.Equal("1", fixture.Stack.AttackerSql($"SELECT count(*) FROM field_encrypted_values e JOIN field_definitions f ON f.field_definition_id = e.field_definition_id WHERE e.form_record_id = '{record:D}' AND f.field_key = 'company_name' AND e.content_change_operation_id = '{AuthorityOracle.Head(fixture.Stack, record).OperationId!.Value:D}';").Trim());

        // While hidden, the value cannot be changed or cleared; switching back shows the retained value again, and the cleared one starts empty.
        Assert.Equal("FIELD_HIDDEN_BY_RULE", (await EditAsync(s, record, s.Clerk, new() { ["company_name"] = "Changed" })).ErrorCode);
        Assert.Equal("FIELD_HIDDEN_BY_RULE", (await EditAsync(s, record, s.Clerk, new() { ["company_name"] = string.Empty })).ErrorCode);
        var back = await EditAsync(s, record, s.Clerk, new() { ["applicant_kind"] = "company" });
        Assert.True(back.IsSuccess, back.Body);
        var restored = Values((await s.WaitProjectionAsync(s.Clerk, record)).Root);
        Assert.Equal("Acme Synthetic Ltd", restored["company_name"]);
        Assert.False(restored.ContainsKey("side_note"));

        // A user with no grant sees nothing; a Reader share of one field releases only that field â€” never the hidden/not-in-form ones.
        Assert.Contains((await s.ProjectionAsync(s.Reader, record)).Status, Denied);
        var share = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "applicant_kind" } });
        Assert.True(share.IsSuccess, share.Body);
        var shared = await s.WaitProjectionAsync(s.Reader, record, TimeSpan.FromSeconds(40));
        Assert.Equal(["applicant_kind"], shared.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()).ToArray());
        var reduced = shared.Root.GetProperty("layoutVersion").GetProperty("layoutJson").GetString()!;
        Assert.DoesNotContain("company_name", reduced, StringComparison.Ordinal);
        Assert.DoesNotContain("visibleWhen", reduced, StringComparison.Ordinal); // no rule metadata about fields the viewer cannot see
        Assert.DoesNotContain("Company name", reduced, StringComparison.Ordinal);

        // A field-scoped contributor: read-only and out-of-scope fields are refused before the authority is asked.
        var contributor = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = s.Designer.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = new[] { "intake_status", "reference_note" } });
        Assert.True(contributor.IsSuccess, contributor.Body);
        await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
        // Phase C5: contributor writes are off by default (D10) — refused before any candidate is built.
        var disabledWrite = await EditAsync(s, record, s.Designer, new() { ["intake_status"] = "final" });
        Assert.Equal((HttpStatusCode.Forbidden, "CONTRIBUTOR_WRITES_DISABLED"), (disabledWrite.Status, disabledWrite.ErrorCode));
        // With contributor writes on, a read-only field and an out-of-scope field get the same refusal (D9).
        Assert.True((await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = true })).IsSuccess);
        try
        {
            var statusWrite = await EditAsync(s, record, s.Designer, new() { ["intake_status"] = "final" });
            Assert.Equal((HttpStatusCode.Forbidden, "FIELD_NOT_EDITABLE"), (statusWrite.Status, statusWrite.ErrorCode));
            var outOfScope = await EditAsync(s, record, s.Designer, new() { ["applicant_kind"] = "person" });
            Assert.Equal((HttpStatusCode.Forbidden, "FIELD_NOT_EDITABLE"), (outOfScope.Status, outOfScope.ErrorCode));
        }
        finally
        {
            Assert.True((await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = (bool?)null })).IsSuccess);
        }

        // The owner (and only someone authorized for the field) can still write a field the form does not show.
        var ownerWrite = await EditAsync(s, record, s.Clerk, new() { ["reference_note"] = "owner reference" });
        Assert.True(ownerWrite.IsSuccess, ownerWrite.Body);
        Assert.Equal("owner reference", Values((await s.WaitProjectionAsync(s.Clerk, record)).Root)["reference_note"]);
    }
}
