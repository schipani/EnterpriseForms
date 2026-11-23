using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.RelatedForms;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C9 M2: the semantic layout comparison (WP-3) — matching by stable id, presentation vs behaviour vs structure,
/// moves are not removals, deterministic order — the potential-impact notices, the publish-impact preview (WP-4: design
/// permission, draft revision, identical drafts, discoverable-only counts, nothing written) and version comparison (WP-5:
/// own versions only, the draft, malformed references). The real-stack proof is FormVersionComparisonTests in Stage B.
/// </summary>
public sealed class FormVersionComparisonTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly Guid Given = Guid.CreateVersion7();
    private static readonly Guid Phone = Guid.CreateVersion7();
    private static readonly Guid Alias = Guid.CreateVersion7();
    private static readonly Guid Status = Guid.CreateVersion7();
    private static readonly Guid Open = Guid.CreateVersion7();
    private static readonly Guid Closed = Guid.CreateVersion7();
    private static readonly Guid Pending = Guid.CreateVersion7();

    private static LayoutElement Field(Guid id, string label, bool required = false, int? maxLength = null, string control = "text", IReadOnlyList<LayoutOption>? options = null, int span = 4) =>
        new(LayoutElementKinds.Field, id, null, control, label, null, new LayoutPlacement(span, false), new LayoutValidation(required, null, maxLength, null, null, null, null, null), null, null, Options: options);

    private static List<LayoutOption> Options(params (Guid Id, string Key, string Label, bool Active)[] options) =>
        options.Select((o, i) => new LayoutOption(o.Id, o.Key, o.Label, i + 1, o.Active)).ToList();

    private static FormLayout Layout(params LayoutSection[] sections) => new(2, "Intake", sections);

    private static LayoutSection Section(string key, string title, params LayoutElement[] elements) => new(key, title, 4, elements);

    private static FormLayout V1() => Layout(Section("main", "Main", Field(Given, "Given name", required: true, maxLength: 100), Field(Phone, "Phone"), Field(Status, "Status", control: "select", options: Options((Open, "open", "Open", true), (Closed, "closed", "Closed", true)))));

    // ---- WP-3: the comparison ----

    [Fact]
    public void IdenticalLayoutsHaveNoChanges()
    {
        var result = FormLayoutComparison.Compare(V1(), V1());
        Assert.False(result.HasChanges);
        Assert.Empty(result.Changes);
        Assert.Single(FormDesignerHandlers.Notices(result), n => n.Code == "NO_CHANGES");
    }

    [Fact]
    public void ARenamedFieldIsAPresentationChangeOfTheSameFieldNotARemovalAndAnAddition()
    {
        var after = Layout(Section("main", "Main", Field(Given, "First name", required: true, maxLength: 100), Field(Phone, "Phone"), V1().Sections[0].Elements[2]));
        var change = Assert.Single(FormLayoutComparison.Compare(V1(), after).Changes);
        Assert.Equal((LayoutChangeTargets.Field, Given.ToString("D"), LayoutChangeTypes.Modified, LayoutChangeSignificance.Presentation, "First name"), (change.Target, change.ElementId, change.ChangeType, change.Significance, change.Label));
        var detail = Assert.Single(change.Details);
        Assert.Equal((LayoutChangeAspects.Label, "Given name", "First name"), (detail.Aspect, detail.Before, detail.After));
        Assert.Single(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(V1(), after)), n => n.Code == "PRESENTATION_ONLY");
    }

    [Fact]
    public void ReorderingIsAMoveOfPresentationSignificanceAndAnInsertionMovesNothingElse()
    {
        var v1 = V1();
        var reordered = Layout(Section("main", "Main", v1.Sections[0].Elements[1], v1.Sections[0].Elements[0], v1.Sections[0].Elements[2]));
        var moves = FormLayoutComparison.Compare(v1, reordered).Changes;
        Assert.All(moves, c => Assert.Equal((LayoutChangeTypes.Moved, LayoutChangeSignificance.Presentation), (c.ChangeType, c.Significance)));
        Assert.Equal(2, moves.Count);

        var inserted = Layout(Section("main", "Main", v1.Sections[0].Elements[0], Field(Alias, "Alias"), v1.Sections[0].Elements[1], v1.Sections[0].Elements[2]));
        var change = Assert.Single(FormLayoutComparison.Compare(v1, inserted).Changes);
        Assert.Equal((LayoutChangeTypes.Added, LayoutChangeSignificance.Structure, Alias.ToString("D")), (change.ChangeType, change.Significance, change.ElementId));
    }

    [Fact]
    public void MovingAFieldToAnotherSectionIsAMoveNamingBothSections()
    {
        var v1 = V1();
        var after = Layout(Section("main", "Main", v1.Sections[0].Elements[0], v1.Sections[0].Elements[2]), Section("contact", "Contact", v1.Sections[0].Elements[1]));
        var changes = FormLayoutComparison.Compare(v1, after).Changes;
        Assert.Contains(changes, c => c is { Target: LayoutChangeTargets.Section, ChangeType: LayoutChangeTypes.Added, ElementId: "contact" });
        var moved = Assert.Single(changes, c => c.ElementId == Phone.ToString("D"));
        Assert.Equal(LayoutChangeTypes.Moved, moved.ChangeType);
        Assert.Equal((LayoutChangeAspects.Section, "Main", "Contact"), (moved.Details[0].Aspect, moved.Details[0].Before, moved.Details[0].After));
        Assert.DoesNotContain(changes, c => c.ChangeType == LayoutChangeTypes.Removed);
    }

    [Fact]
    public void AddedAndRemovedFieldsAreStructuralAndANewRequiredFieldIsFlagged()
    {
        var v1 = V1();
        var after = Layout(Section("main", "Main", v1.Sections[0].Elements[0], v1.Sections[0].Elements[2], Field(Alias, "Alias", required: true)));
        var result = FormLayoutComparison.Compare(v1, after);
        Assert.Equal((1, 1), (result.AddedCount, result.RemovedCount));
        var added = Assert.Single(result.Changes, c => c.ChangeType == LayoutChangeTypes.Added);
        Assert.Contains(added.Details, d => d is { Aspect: LayoutChangeAspects.Required, Tightened: true });
        Assert.Equal("Phone", Assert.Single(result.Changes, c => c.ChangeType == LayoutChangeTypes.Removed).Label);
        var notices = FormDesignerHandlers.Notices(result);
        Assert.Equal(["Alias"], Assert.Single(notices, n => n.Code == "NEW_REQUIRED_FIELDS").ElementLabels);
        Assert.Equal(["Phone"], Assert.Single(notices, n => n.Code == "ELEMENTS_REMOVED").ElementLabels);
        Assert.Equal("Information", Assert.Single(notices, n => n.Code == "ELEMENTS_REMOVED").Severity);
    }

    [Fact]
    public void ValidationChangesAreBehaviourAndOnlyStricterRulesAreMarkedTightened()
    {
        var v1 = V1();
        LayoutComparisonResult With(LayoutElement given) => FormLayoutComparison.Compare(v1, Layout(Section("main", "Main", given, v1.Sections[0].Elements[1], v1.Sections[0].Elements[2])));
        var stricter = Assert.Single(Assert.Single(With(Field(Given, "Given name", required: true, maxLength: 50)).Changes).Details);
        Assert.Equal((LayoutChangeAspects.MaxLength, LayoutChangeSignificance.Behaviour, "100", "50", true), (stricter.Aspect, stricter.Significance, stricter.Before, stricter.After, stricter.Tightened));
        Assert.False(Assert.Single(Assert.Single(With(Field(Given, "Given name", required: true, maxLength: 200)).Changes).Details).Tightened);
        var optional = Assert.Single(Assert.Single(With(Field(Given, "Given name", required: false, maxLength: 100)).Changes).Details);
        Assert.Equal((LayoutChangeAspects.Required, false), (optional.Aspect, optional.Tightened));
        Assert.Single(FormDesignerHandlers.Notices(With(Field(Given, "Given name", required: true, maxLength: 50))), n => n.Code == "VALIDATION_TIGHTENED");
    }

    [Fact]
    public void OptionsAreMatchedByIdAndRelabelOrderAddDeactivateAndReactivateAreDistinguished()
    {
        var v1 = V1();
        LayoutComparisonResult With(IReadOnlyList<LayoutOption> options) => FormLayoutComparison.Compare(v1, Layout(Section("main", "Main", v1.Sections[0].Elements[0], v1.Sections[0].Elements[1], Field(Status, "Status", control: "select", options: options))));
        var details = Assert.Single(With(Options((Closed, "closed", "Closed", false), (Open, "open", "Open (default)", true), (Pending, "pending", "Pending", true))).Changes).Details;
        Assert.Contains(details, d => d is { Aspect: LayoutChangeAspects.OptionRelabelled, Significance: LayoutChangeSignificance.Presentation, Before: "Open", After: "Open (default)" });
        Assert.Contains(details, d => d is { Aspect: LayoutChangeAspects.OptionDeactivated, Subject: "Closed", Tightened: true });
        Assert.Contains(details, d => d is { Aspect: LayoutChangeAspects.OptionAdded, Subject: "Pending" });
        Assert.Contains(details, d => d.Aspect == LayoutChangeAspects.OptionOrder);
        var notice = Assert.Single(FormDesignerHandlers.Notices(With(Options((Open, "open", "Open", true), (Closed, "closed", "Closed", false)))), n => n.Code == "OPTIONS_NO_LONGER_AVAILABLE");
        Assert.Equal(["Status: Closed"], notice.ElementLabels);
        var reactivated = FormLayoutComparison.Compare(Layout(Section("main", "Main", Field(Status, "Status", control: "select", options: Options((Open, "open", "Open", true), (Closed, "closed", "Closed", false))))), Layout(Section("main", "Main", Field(Status, "Status", control: "select", options: Options((Open, "open", "Open", true), (Closed, "closed", "Closed", true))))));
        Assert.Contains(Assert.Single(reactivated.Changes).Details, d => d.Aspect == LayoutChangeAspects.OptionReactivated);
    }

    [Fact]
    public void RulesReadOnlyAndShowInFormAreBehaviourChanges()
    {
        var v1 = V1();
        var rule = new RuleComparison(RuleOperators.Equal, Status, "open", null);
        var after = Layout(Section("main", "Main", v1.Sections[0].Elements[0] with { ReadOnly = true }, v1.Sections[0].Elements[1] with { VisibleWhen = rule, ShowInForm = false }, v1.Sections[0].Elements[2]));
        var changes = FormLayoutComparison.Compare(v1, after).Changes;
        Assert.All(changes, c => Assert.Equal(LayoutChangeSignificance.Behaviour, c.Significance));
        Assert.Contains(changes.Single(c => c.ElementId == Given.ToString("D")).Details, d => d is { Aspect: LayoutChangeAspects.ReadOnly, After: "yes" });
        Assert.Contains(changes.Single(c => c.ElementId == Phone.ToString("D")).Details, d => d is { Aspect: LayoutChangeAspects.VisibleWhen, Before: "none", After: "set" });
        Assert.Equal(2, Assert.Single(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(v1, after)), n => n.Code == "READ_ONLY_OR_HIDDEN").ElementLabels.Count);
    }

    [Fact]
    public void SectionsAreMatchedByKeyAndWidthOrTitleChangesArePresentation()
    {
        var v1 = V1();
        var after = Layout(new LayoutSection("main", "Main details", 4, [v1.Sections[0].Elements[0] with { Layout = new LayoutPlacement(2, false) }, v1.Sections[0].Elements[1], v1.Sections[0].Elements[2]]));
        var changes = FormLayoutComparison.Compare(v1, after).Changes;
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(LayoutChangeSignificance.Presentation, c.Significance));
        Assert.Contains(changes, c => c is { Target: LayoutChangeTargets.Section, ElementId: "main" } && c.Details.Single().Aspect == LayoutChangeAspects.Title);
        Assert.Contains(changes.Single(c => c.ElementId == Given.ToString("D")).Details, d => d.Aspect == LayoutChangeAspects.Placement);
    }

    [Fact]
    public void RepeatingSectionLimitsAndTheFirstVersionAreReported()
    {
        var group = Guid.CreateVersion7();
        var before = Layout(new LayoutSection("vehicles", "Vehicles", 4, [Field(Alias, "Plate")], Repeat: new LayoutRepeat(group, 0, 10, true, true, true)));
        var after = Layout(new LayoutSection("vehicles", "Vehicles", 4, [Field(Alias, "Plate")], Repeat: new LayoutRepeat(group, 1, 5, true, true, true)));
        var limits = Assert.Single(Assert.Single(FormLayoutComparison.Compare(before, after).Changes).Details);
        Assert.Equal((LayoutChangeAspects.RepeatLimits, true), (limits.Aspect, limits.Tightened));
        Assert.Single(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(before, after)), n => n.Code == "LIMITS_TIGHTENED");

        var first = FormLayoutComparison.Compare(null, V1());
        Assert.Equal(4, first.AddedCount); // the section and its three fields
        Assert.Equal(0, first.RemovedCount);
    }

    [Fact]
    public void TheComparisonIsDeterministic()
    {
        var v1 = V1();
        var after = Layout(Section("main", "Main", Field(Alias, "Alias", required: true), v1.Sections[0].Elements[2]), Section("extra", "Extra", v1.Sections[0].Elements[0]));
        var a = FormDesignerHandlers.ToResponse(FormLayoutComparison.Compare(v1, after));
        var b = FormDesignerHandlers.ToResponse(FormLayoutComparison.Compare(v1, after));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
        Assert.Equal(LayoutChangeTypes.Removed, a.Changes[^1].ChangeType); // removed items come last
    }

    // ---- corrective review of PR #71: proven tightening and comparison completeness ----

    private static readonly RuleComparison WhenOpen = new(RuleOperators.Equal, Status, "open", null);

    private static readonly RuleComparison WhenClosed = new(RuleOperators.Equal, Status, "closed", null);

    private static LayoutChangeDetail RequiredWhenDetail(LayoutElement before, LayoutElement after) =>
        Assert.Single(Assert.Single(FormLayoutComparison.Compare(Layout(Section("main", "Main", before)), Layout(Section("main", "Main", after))).Changes).Details, d => d.Aspect == LayoutChangeAspects.RequiredWhen);

    [Fact]
    public void AConditionalRequirementAddedToAnOptionalFieldIsAProvenTightening()
    {
        var detail = RequiredWhenDetail(Field(Phone, "Phone"), Field(Phone, "Phone") with { RequiredWhen = WhenOpen });
        Assert.Equal((LayoutChangeSignificance.Behaviour, "none", "set", true), (detail.Significance, detail.Before, detail.After, detail.Tightened));
        Assert.Single(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(Layout(Section("main", "Main", Field(Phone, "Phone"))), Layout(Section("main", "Main", Field(Phone, "Phone") with { RequiredWhen = WhenOpen })))), n => n.Code == "NEW_REQUIRED_FIELDS");
    }

    [Fact]
    public void AReplacedConditionalRequirementIsABehaviourChangeButNotAProvenTightening()
    {
        var before = Layout(Section("main", "Main", Field(Phone, "Phone") with { RequiredWhen = WhenOpen }));
        var after = Layout(Section("main", "Main", Field(Phone, "Phone") with { RequiredWhen = WhenClosed }));
        var detail = RequiredWhenDetail(before.Sections[0].Elements[0], after.Sections[0].Elements[0]);
        Assert.Equal((LayoutChangeSignificance.Behaviour, "set", "set", false), (detail.Significance, detail.Before, detail.After, detail.Tightened));
        Assert.DoesNotContain(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(before, after)), n => n.Code == "NEW_REQUIRED_FIELDS");
    }

    [Fact]
    public void ARemovedConditionalRequirementIsARelaxation()
    {
        var detail = RequiredWhenDetail(Field(Phone, "Phone") with { RequiredWhen = WhenOpen }, Field(Phone, "Phone"));
        Assert.Equal(("set", "none", false), (detail.Before, detail.After, detail.Tightened));
    }

    [Fact]
    public void ReplacingAnUnconditionalRequirementWithAConditionalOneRelaxesIt()
    {
        var before = Layout(Section("main", "Main", Field(Phone, "Phone", required: true)));
        var after = Layout(Section("main", "Main", Field(Phone, "Phone", required: false) with { RequiredWhen = WhenOpen }));
        var details = Assert.Single(FormLayoutComparison.Compare(before, after).Changes).Details;
        Assert.All(details, d => Assert.False(d.Tightened));
        Assert.DoesNotContain(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(before, after)), n => n.Code == "NEW_REQUIRED_FIELDS");
        // A conditional requirement on a field that stays unconditionally required adds nothing either.
        Assert.False(RequiredWhenDetail(Field(Phone, "Phone", required: true), Field(Phone, "Phone", required: true) with { RequiredWhen = WhenOpen }).Tightened);
        // A new field that is conditionally (not unconditionally) required is flagged.
        var added = Assert.Single(FormLayoutComparison.Compare(Layout(Section("main", "Main")), Layout(Section("main", "Main", Field(Alias, "Alias") with { RequiredWhen = WhenOpen }))).Changes, c => c.ChangeType == LayoutChangeTypes.Added);
        Assert.True(Assert.Single(added.Details, d => d.Aspect == LayoutChangeAspects.RequiredWhen).Tightened);
        var alreadyRequired = Assert.Single(FormLayoutComparison.Compare(Layout(Section("main", "Main")), Layout(Section("main", "Main", Field(Alias, "Alias", required: true) with { RequiredWhen = WhenOpen }))).Changes, c => c.ChangeType == LayoutChangeTypes.Added);
        Assert.False(Assert.Single(alreadyRequired.Details, d => d.Aspect == LayoutChangeAspects.RequiredWhen).Tightened);
    }

    [Fact]
    public void OnlyAnAddedPatternIsAProvenTightening()
    {
        static LayoutElement WithPattern(string? pattern) => Field(Phone, "Phone") with { Validation = new LayoutValidation(false, null, null, pattern, null, null, null, null) };
        LayoutChangeDetail PatternChange(string? before, string? after) => Assert.Single(Assert.Single(FormLayoutComparison.Compare(Layout(Section("main", "Main", WithPattern(before))), Layout(Section("main", "Main", WithPattern(after)))).Changes).Details);
        Assert.True(PatternChange(null, "^[0-9]+$").Tightened);
        Assert.False(PatternChange("^[0-9]+$", "^[0-9-]+$").Tightened);
        Assert.False(PatternChange("^[0-9]+$", null).Tightened);
    }

    [Fact]
    public void ProtectionIsReportedWhenAVersionFirstRecordsIt()
    {
        var before = Layout(Section("main", "Main", Field(Phone, "Phone")));
        var after = Layout(Section("main", "Main", Field(Phone, "Phone") with { Protection = new LayoutProtection("Confidential", "ApplicationEncrypted") }));
        var detail = Assert.Single(Assert.Single(FormLayoutComparison.Compare(before, after).Changes).Details);
        Assert.Equal((LayoutChangeAspects.Protection, LayoutChangeSignificance.Behaviour, (string?)null, "Confidential / ApplicationEncrypted"), (detail.Aspect, detail.Significance, detail.Before, detail.After));
        Assert.Single(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(before, after)), n => n.Code == "PROTECTION_CHANGED");
        Assert.Single(Assert.Single(FormLayoutComparison.Compare(after, before).Changes).Details, d => d is { Aspect: LayoutChangeAspects.Protection, After: null });
    }

    [Fact]
    public void ASectionThatPlacesADifferentRepeatingGroupIsReported()
    {
        var before = Layout(new LayoutSection("entries", "Entries", 4, [Field(Alias, "Plate")], Repeat: new LayoutRepeat(Guid.CreateVersion7(), 0, 10, true, true, true)));
        var after = Layout(new LayoutSection("entries", "Entries", 4, [Field(Alias, "Plate")], Repeat: new LayoutRepeat(Guid.CreateVersion7(), 0, 10, true, true, true)));
        var change = Assert.Single(FormLayoutComparison.Compare(before, after).Changes);
        Assert.Equal((LayoutChangeTargets.Section, LayoutChangeSignificance.Behaviour), (change.Target, change.Significance));
        Assert.Single(change.Details, d => d.Aspect == LayoutChangeAspects.RepeatGroup);
        Assert.Single(FormDesignerHandlers.Notices(FormLayoutComparison.Compare(before, after)), n => n.Code == "REPEATING_SECTIONS_CHANGED");
        var madeRepeating = FormLayoutComparison.Compare(Layout(Section("entries", "Entries", Field(Alias, "Plate"))), after);
        Assert.Single(Assert.Single(madeRepeating.Changes).Details, d => d.Aspect == LayoutChangeAspects.Repeat);
    }

    [Fact]
    public void AnUploadPolicyAppearingOrDisappearingAndAKindChangeAreReported()
    {
        var policy = new LayoutUploadPolicy(true, 5, 1024, ["application/pdf"]);
        var collection = new LayoutElement(LayoutElementKinds.DocumentCollection, Alias, null, "fileUpload", "Documents", null, new LayoutPlacement(4, false), new LayoutValidation(false, null, null, null, null, null, null, null), policy, null);
        var withoutPolicy = collection with { Upload = null };
        var appeared = Assert.Single(Assert.Single(FormLayoutComparison.Compare(Layout(Section("main", "Main", withoutPolicy)), Layout(Section("main", "Main", collection))).Changes).Details);
        Assert.Equal((LayoutChangeAspects.UploadPolicy, (string?)null, false), (appeared.Aspect, appeared.Before, appeared.Tightened));
        Assert.NotNull(appeared.After);
        Assert.Single(Assert.Single(FormLayoutComparison.Compare(Layout(Section("main", "Main", collection)), Layout(Section("main", "Main", withoutPolicy))).Changes).Details, d => d is { Aspect: LayoutChangeAspects.UploadPolicy, After: null });
        var asField = Field(Alias, "Documents") with { Validation = collection.Validation };
        var kind = FormLayoutComparison.Compare(Layout(Section("main", "Main", asField)), Layout(Section("main", "Main", collection))).Changes.Single();
        Assert.Contains(kind.Details, d => d is { Aspect: LayoutChangeAspects.Kind, Before: LayoutElementKinds.Field, After: LayoutElementKinds.DocumentCollection });
    }

    // ---- WP-4 / WP-5: handlers ----

    [Fact]
    public async Task PublishImpactComparesTheCurrentVersionWithTheDraftAndCountsOnlyDiscoverableRecords()
    {
        var fake = new FakeForm();
        var mine = fake.AddRecord(fake.V1Id);
        fake.AddRecord(fake.V1Id); // not discoverable
        fake.Discoverable.Add(mine);
        var impact = await fake.Handlers(true).PreviewPublishImpactAsync(Context, fake.Definition.FormDefinitionId, 7, CancellationToken.None);
        Assert.True(impact.CanPublish);
        Assert.Equal((1, 2), (impact.CurrentVersionNumber, impact.NextVersionNumber));
        Assert.Equal((1, 1), (impact.Comparison.AddedCount, impact.Comparison.RemovedCount));
        Assert.Contains(impact.Notices, n => n.Code == "NEW_REQUIRED_FIELDS" && n.ElementLabels.SequenceEqual(["Alias"]));
        Assert.Equal((1, 1, 1), (impact.Records.VisibleRecordCount, impact.Records.ShownWithNewVersionCount, impact.Records.EditedWithNewVersionCount));
        Assert.True(impact.Records.CountsAreApproximate);
        Assert.Contains("never deletes or changes stored values", impact.DataStatement, StringComparison.Ordinal);
        Assert.Equal(0, fake.Commits);
        Assert.Empty(fake.AuditEvents);
    }

    [Fact]
    public async Task UnderHistoricalNoExistingRecordWouldBeShownOrEditedWithTheNewVersion()
    {
        var fake = new FakeForm();
        fake.Definition.VersionBehavior = VersionBehaviors.Historical;
        fake.Discoverable.Add(fake.AddRecord(fake.V1Id));
        var impact = await fake.Handlers(true).PreviewPublishImpactAsync(Context, fake.Definition.FormDefinitionId, 7, CancellationToken.None);
        Assert.Equal((1, 0, 0), (impact.Records.VisibleRecordCount, impact.Records.ShownWithNewVersionCount, impact.Records.EditedWithNewVersionCount));
    }

    [Fact]
    public async Task PublishImpactNeedsTheDesignPermissionADraftAndTheCurrentDraftRevision()
    {
        var fake = new FakeForm();
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(false).PreviewPublishImpactAsync(Context, fake.Definition.FormDefinitionId, 7, CancellationToken.None))).HttpStatusCode);
        Assert.Equal(0, fake.ScopesOpened);
        Assert.Equal("DRAFT_REVISION_CONFLICT", (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(true).PreviewPublishImpactAsync(Context, fake.Definition.FormDefinitionId, 6, CancellationToken.None))).ErrorCode);
        fake.Draft = null;
        Assert.Equal("NO_DRAFT", (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(true).PreviewPublishImpactAsync(Context, fake.Definition.FormDefinitionId, 7, CancellationToken.None))).ErrorCode);
    }

    [Fact]
    public async Task ADraftIdenticalToTheCurrentVersionCannotBePublished()
    {
        var fake = new FakeForm();
        fake.Draft!.LayoutJson = fake.V1Json;
        var impact = await fake.Handlers(true).PreviewPublishImpactAsync(Context, fake.Definition.FormDefinitionId, 7, CancellationToken.None);
        Assert.False(impact.CanPublish);
        Assert.Equal("NO_DRAFT_CHANGES", impact.BlockedReasonCode);
        Assert.False(impact.Comparison.HasChanges);
    }

    [Fact]
    public async Task VersionsCompareOnlyWithinTheFormAndTheDraftIsComparedAsItWouldBePublished()
    {
        var fake = new FakeForm();
        var handlers = fake.Handlers(true);
        var withDraft = await handlers.CompareVersionsAsync(Context, fake.Definition.FormDefinitionId, fake.V1Id.ToString("D"), "draft", CancellationToken.None);
        Assert.Equal(("Version", "Draft", 7L), (withDraft.From.Kind, withDraft.To.Kind, withDraft.To.DraftRevision!.Value));
        Assert.Equal((1, 1), (withDraft.Comparison.AddedCount, withDraft.Comparison.RemovedCount));
        Assert.False((await handlers.CompareVersionsAsync(Context, fake.Definition.FormDefinitionId, fake.V1Id.ToString("D"), fake.V1Id.ToString("D"), CancellationToken.None)).Comparison.HasChanges);
        Assert.Equal("VERSION_NOT_FOUND", (await Assert.ThrowsAsync<BusinessException>(() => handlers.CompareVersionsAsync(Context, fake.Definition.FormDefinitionId, fake.ForeignVersionId.ToString("D"), "draft", CancellationToken.None))).ErrorCode);
        Assert.Equal(422, (await Assert.ThrowsAsync<BusinessException>(() => handlers.CompareVersionsAsync(Context, fake.Definition.FormDefinitionId, "v1", "draft", CancellationToken.None))).HttpStatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(false).CompareVersionsAsync(Context, fake.Definition.FormDefinitionId, fake.V1Id.ToString("D"), "draft", CancellationToken.None))).HttpStatusCode);
        fake.Draft = null;
        Assert.Equal("NO_DRAFT", (await Assert.ThrowsAsync<BusinessException>(() => handlers.CompareVersionsAsync(Context, fake.Definition.FormDefinitionId, fake.V1Id.ToString("D"), "draft", CancellationToken.None))).ErrorCode);
    }

    private sealed class FakeForm
    {
        public FakeForm()
        {
            // As publishing stores it: labels, protection and option snapshots taken from the catalog.
            V1Json = FormLayoutCodec.Serialize(FormDesignerHandlers.SnapshotLabels(Layout(Section("main", "Main", Field(Given, "Given name", required: true), Field(Phone, "Phone"))), Fields().ToDictionary(f => f.FieldDefinitionId), new Dictionary<Guid, FormRelationshipDefinition>()));
            Versions = [new FormVersion { FormVersionId = V1Id, FormDefinitionId = Definition.FormDefinitionId, VersionNumber = 1, LayoutJson = V1Json }];
            Definition.CurrentFormVersionId = V1Id;
            Draft = new FormDraft { FormDraftId = Guid.CreateVersion7(), FormDefinitionId = Definition.FormDefinitionId, DraftRevision = 7, LayoutJson = FormLayoutCodec.Serialize(Layout(Section("main", "Main", Field(Given, "Given name", required: true), Field(Alias, "Alias", required: true)))) };
        }

        public FormDefinition Definition { get; } = new() { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionName = "Intake", FormDefinitionRevision = 1 };

        public Guid V1Id { get; } = Guid.CreateVersion7();

        public Guid ForeignVersionId { get; } = Guid.CreateVersion7();

        public string V1Json { get; }

        public List<FormVersion> Versions { get; }

        public FormDraft? Draft { get; set; }

        public List<FormRecord> Records { get; } = [];

        public List<Guid> Discoverable { get; } = [];

        public List<AuditEvent> AuditEvents { get; } = [];

        public int ScopesOpened { get; private set; }

        public int Commits { get; private set; }

        public Guid AddRecord(Guid createdVersion)
        {
            var record = new FormRecord { FormRecordId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionId = Definition.FormDefinitionId, CreatedFormVersionId = createdVersion };
            Records.Add(record);
            return record.FormRecordId;
        }

        private List<FieldDefinition> Fields() =>
            [.. new[] { (Given, "given_name", "Given name"), (Phone, "phone_number", "Phone"), (Alias, "alias_name", "Alias") }.Select(f => new FieldDefinition { FieldDefinitionId = f.Item1, FormDefinitionId = Definition.FormDefinitionId, FieldKey = f.Item2, FieldDisplayName = f.Item3, FieldDataType = "String", FieldClassification = "Internal" })];

        public FormDesignerHandlers Handlers(bool designAllowed)
        {
            var authority = Stubs.Create<IAuthorizationAuthorityClient>((method, args) => method.Name switch
            {
                nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Task.FromResult(new PermissionDecision(designAllowed, null, 9, 9, null, Guid.Empty)),
                nameof(IAuthorizationAuthorityClient.DiscoverAuthorizedRecordsAsync) => Task.FromResult(new RecordDiscoveryPage(Discoverable.ToList(), 9, null)),
                _ => throw new NotSupportedException(method.Name),
            });
            var store = Stubs.Create<IBusinessStore>((_, _) =>
            {
                ScopesOpened++;
                return Task.FromResult(Scope());
            });
            return new FormDesignerHandlers(store, authority, null!, TimeProvider.System, null!);
        }

        private IBusinessScope Scope() => Stubs.Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.FindFormDefinitionAsync) => (object?)Task.FromResult<FormDefinition?>((Guid)args[1]! == Definition.FormDefinitionId ? Definition : null),
            nameof(IBusinessScope.FindDraftAsync) => Task.FromResult(Draft),
            nameof(IBusinessScope.GetFormVersionsAsync) => Task.FromResult(Versions.ToList()),
            nameof(IBusinessScope.FindFormVersionAsync) => Task.FromResult((Guid)args[0]! == ForeignVersionId ? new FormVersion { FormVersionId = ForeignVersionId, FormDefinitionId = Guid.CreateVersion7(), VersionNumber = 1, LayoutJson = V1Json } : Versions.FirstOrDefault(v => v.FormVersionId == (Guid)args[0]!)),
            nameof(IBusinessScope.GetFieldDefinitionsAsync) => Task.FromResult(Fields()),
            nameof(IBusinessScope.GetFieldOptionsAsync) => Task.FromResult(new List<FieldOption>()),
            nameof(IBusinessScope.GetGroupDefinitionsAsync) => Task.FromResult(new List<GroupDefinition>()),
            nameof(IBusinessScope.GetRelationshipDefinitionsAsync) => Task.FromResult(new List<FormRelationshipDefinition>()),
            nameof(IBusinessScope.GetFormRecordsAsync) => Task.FromResult(Records.Where(r => ((IReadOnlyCollection<Guid>)args[1]!).Contains(r.FormRecordId)).ToList()),
            nameof(IBusinessScope.AddAuditEvent) => Record((AuditEvent)args[0]!),
            nameof(IBusinessScope.CommitAsync) => Commit(),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });

        private object? Record(AuditEvent auditEvent)
        {
            AuditEvents.Add(auditEvent);
            return null;
        }

        private Task Commit()
        {
            Commits++;
            return Task.CompletedTask;
        }
    }

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class Stubs
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
