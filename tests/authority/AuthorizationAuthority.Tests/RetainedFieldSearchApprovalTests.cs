using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-188 (Option B, D-H1…D-H18) at unit level, against the in-memory store with Authority-held facts only: the explicit
/// RetainedField approval, its lifecycle across version registrations (still absent → unchanged; returns unchanged and eligible →
/// converted in place; changed, ineligible or disabled → invalidated), no revival of invalidated approvals, strengthening and
/// withdrawal, the field-to-form binding in registrations, approvals and protection commands, the per-use effectiveness check,
/// the configuration flag and the connection fence. The real-stack proof (journal, projection, both search engines, migration,
/// row-level security, trigger) is the Stage B class RetainedFieldSearchTests.
/// </summary>
public sealed class RetainedFieldSearchApprovalTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid OtherForm = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid Designer = Guid.CreateVersion7();
    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Phone = Guid.CreateVersion7();
    private static readonly Guid Email = Guid.CreateVersion7();
    private static readonly Guid Notes = Guid.CreateVersion7();

    private static readonly FieldSearchApprovalGate Disabled = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private static readonly FieldSearchApprovalGate Enabled = Disabled.WithRetainedFieldApprovals(FieldSearchApprovalGate.RetainedFieldApprovalsEnabledValue);

    private readonly InMemoryAuthorityStore store = new();

    public RetainedFieldSearchApprovalTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        foreach (var user in new[] { Administrator, Designer })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        foreach (var form in new[] { Form, OtherForm })
        {
            store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active });
            store.Edges.Add(Edge(ResourceTypeNames.FormDefinition, form, "designer", Designer));
        }

        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Administrator));
        store.Policies.Add(new OrganizationProtectionPolicy { OrganizationProtectionPolicyId = Guid.CreateVersion7(), OrganizationId = Org, MinimumFieldClassification = "Public", PlaintextInternalFieldsPermitted = true, PlaintextSearchPermitted = true, PlaintextDowngradePermitted = true, SearchPolicyRevision = 1 });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PublishedFieldEntry FirstEntry => new(First, "String", "Internal", "Standard", "First name");

    private static PublishedFieldEntry PhoneEntry(string label = "Phone", bool inRepeatingGroup = false) => new(Phone, "String", "Internal", "Standard", label, InRepeatingGroup: inRepeatingGroup);

    private static PublishedFieldEntry EmailEntry => new(Email, "String", "Internal", "Standard", "Email");

    private static AuthorityRelationshipEdge Edge(string type, Guid id, string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = user, SubjectRelationName = string.Empty,
    };

    private BoundActor Actor(Guid user) => new(new VerifiedActor("https://idp", user.ToString("N"), "test"), store.Bindings.Single(b => b.AuthorityUserId == user));

    private async Task<CommandPlan> PlanAsync(Guid user, SecurityCommandKind kind, Guid form, object payload, FieldSearchApprovalGate gate)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var request = new SecurityCommandRequest(Org, kind, new ResourceRef(ResourceKind.FormDefinition, form), 3, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, gate);
    }

    /// <summary>Plans and "commits" a registration the way ApplyPlan does for these tables (version row, new bindings; approvals change in place).</summary>
    private async Task<CommandPlan> RegisterAsync(int number, FieldSearchApprovalGate gate, params PublishedFieldEntry[] fields) => await RegisterAsync(Form, number, gate, fields);

    private async Task<CommandPlan> RegisterAsync(Guid form, int number, FieldSearchApprovalGate gate, params PublishedFieldEntry[] fields)
    {
        // The layout digest of version n is 32 bytes of n, so tests can tell which version an approval is bound to.
        var digest = string.Concat(Enumerable.Repeat(((byte)number).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
        var plan = await PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, form, new RegisterPublishedFormVersionPayload(form, number, digest, fields), gate);
        store.PublishedVersions.Add(plan.PublishedVersion!);
        Commit(plan);
        return plan;
    }

    private void Commit(CommandPlan plan)
    {
        foreach (var binding in plan.NewFieldFormBindings)
        {
            store.AddFieldFormBindingForTest(binding);
        }

        foreach (var approval in plan.NewApprovals)
        {
            approval.ApprovedOn = DateTimeOffset.UtcNow;
            store.Approvals.Add(approval);
        }
    }

    private Task<CommandPlan> ApproveAsync(Guid field, string? scope, FieldSearchApprovalGate gate, Guid? user = null, string? evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence, string[]? operators = null) =>
        PlanAsync(user ?? Administrator, SecurityCommandKind.ApproveFieldSearch, Form, new ApproveFieldSearchPayload(Form, field, operators ?? ["equals", "startsWith"], "GOV-C188", evidence, scope), gate);

    private async Task<FieldSearchApproval> ApprovedAsync(Guid field, string? scope, FieldSearchApprovalGate gate)
    {
        var plan = await ApproveAsync(field, scope, gate);
        Commit(plan);
        return Assert.Single(plan.NewApprovals);
    }

    private static async Task<string> RejectedAsync(Func<Task> action, string code)
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(action);
        Assert.Equal(code, ex.ErrorCode);
        return ex.Message;
    }

    /// <summary>V1 (first, phone) with phone approved; V2 (first, email) invalidates it.</summary>
    private async Task<FieldSearchApproval> PhoneRemovedAsync(FieldSearchApprovalGate gate)
    {
        await RegisterAsync(1, gate, FirstEntry, PhoneEntry());
        var normal = await ApprovedAsync(Phone, null, gate);
        await RegisterAsync(2, gate, FirstEntry, EmailEntry);
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (normal.ApprovalStateName, normal.InvalidationReasonCode)); // D-H3 unchanged
        return normal;
    }

    // ---- T2/T4: approval of a retained field ----

    [Fact]
    public async Task ASecurityAdministratorApprovesARemovedFieldAsRetainedBoundToTheLastVersionThatPlacedIt()
    {
        var normal = await PhoneRemovedAsync(Enabled);
        var plan = await ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        var retained = Assert.Single(plan.NewApprovals);
        Assert.NotEqual(normal.FieldSearchApprovalId, retained.FieldSearchApprovalId); // a new approval, never the invalidated one revived
        Assert.Equal((FieldSearchApprovalScopes.RetainedField, 1, 1, "Phone", FieldSearchApprovalStates.Active), (retained.ApprovalScopeName, retained.SourceFormVersionNumber, retained.ApprovedFormVersionNumber, retained.ApprovedFieldLabelSnapshot, retained.ApprovalStateName));
        Assert.Equal(Enumerable.Repeat((byte)1, 32), retained.ApprovedLayoutDigest); // the V1 digest, not the latest
        Assert.Empty(plan.ChangedApprovals); // the invalidated approval is not touched
        Assert.Equal(FieldSearchApprovalStates.Invalidated, normal.ApprovalStateName);
        Assert.NotNull(plan.SearchPolicyToAdvance);
        Assert.Contains("\"approval_scope\":\"RetainedField\"", plan.NormalizedPayloadJson, StringComparison.Ordinal); // journaled, replayable
        Assert.Empty(plan.NewFieldFormBindings); // already bound by the registrations
    }

    [Fact]
    public async Task RetainedApprovalsAreRefusedWhenDisabledOrForAFieldStillInTheFormOrNeverRegistered()
    {
        await PhoneRemovedAsync(Disabled);
        await RejectedAsync(() => ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField, Disabled), AuthorityErrorCodes.RetainedFieldApprovalsDisabled);
        await RejectedAsync(() => ApproveAsync(First, FieldSearchApprovalScopes.RetainedField, Enabled), AuthorityErrorCodes.FormVersionUntrusted); // in the current version
        await RejectedAsync(() => ApproveAsync(Guid.CreateVersion7(), FieldSearchApprovalScopes.RetainedField, Enabled), AuthorityErrorCodes.FormVersionUntrusted); // never registered (D-H14)
        await RejectedAsync(() => ApproveAsync(Phone, null, Enabled), AuthorityErrorCodes.FormVersionUntrusted); // a normal approval still needs the latest version
        await RejectedAsync(() => ApproveAsync(Phone, "Historical", Enabled), AuthorityErrorCodes.InvalidCommand);
        await RejectedAsync(() => ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled, Designer), AuthorityErrorCodes.Forbidden);
        await RejectedAsync(() => ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled, evidence: null), AuthorityErrorCodes.StepUpRequired);
        await RejectedAsync(() => ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled, operators: ["greaterThan"]), AuthorityErrorCodes.FieldSearchNotEligible);
        Assert.DoesNotContain(store.Approvals, a => a.ApprovalScopeName == FieldSearchApprovalScopes.RetainedField);
    }

    [Theory]
    [InlineData("Confidential", "Standard", false, false)] // classification not permitted
    [InlineData("Internal", "ApplicationEncrypted", false, false)] // encrypted
    [InlineData("Internal", "Standard", true, false)] // repeating group
    [InlineData("Internal", "Standard", false, true)] // Authority-held encryption requirement
    public async Task AnIneligibleRemovedFieldIsNeverApprovedAsRetained(string classification, string protection, bool repeating, bool requirement)
    {
        await RegisterAsync(1, Enabled, FirstEntry, new PublishedFieldEntry(Phone, "String", classification, protection, "Phone", InRepeatingGroup: repeating));
        await RegisterAsync(2, Enabled, FirstEntry);
        if (requirement)
        {
            store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = Phone, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        }

        await RejectedAsync(() => ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled), AuthorityErrorCodes.FieldSearchNotEligible);
    }

    // ---- T13/T14/T15: registrations after a retained approval ----

    [Fact]
    public async Task ARetainedApprovalStaysWhileTheFieldIsAbsentAndBecomesCurrentWhenItReturnsUnchangedAndEligible()
    {
        await PhoneRemovedAsync(Enabled);
        var retained = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        var stillAbsent = await RegisterAsync(3, Enabled, FirstEntry, EmailEntry);
        Assert.DoesNotContain(retained, stillAbsent.ChangedApprovals);
        Assert.Equal((FieldSearchApprovalStates.Active, FieldSearchApprovalScopes.RetainedField, 1), (retained.ApprovalStateName, retained.ApprovalScopeName, retained.ApprovedFormVersionNumber));

        var returned = await RegisterAsync(4, Enabled, FirstEntry, PhoneEntry(), EmailEntry);
        Assert.Same(retained, Assert.Single(returned.ChangedApprovals)); // converted in place: same approval id, same snapshot
        Assert.Equal((FieldSearchApprovalStates.Active, FieldSearchApprovalScopes.CurrentVersion, (int?)null, 4), (retained.ApprovalStateName, retained.ApprovalScopeName, retained.SourceFormVersionNumber, retained.ApprovedFormVersionNumber));
        Assert.Equal(Enumerable.Repeat((byte)4, 32), retained.ApprovedLayoutDigest);
        Assert.Single(store.Approvals, a => a.FieldDefinitionId == Phone && a.ApprovalStateName == FieldSearchApprovalStates.Active); // one Active approval per field

        // Back to normal life: removing it again invalidates it (D-H3), never a silent retention.
        await RegisterAsync(5, Enabled, FirstEntry, EmailEntry);
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (retained.ApprovalStateName, retained.InvalidationReasonCode));
    }

    [Fact]
    public async Task AFieldReturningChangedIneligibleOrWhileDisabledInvalidatesTheRetainedApproval()
    {
        // Changed (relabelled) on return.
        await PhoneRemovedAsync(Enabled);
        var changed = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        await RegisterAsync(3, Enabled, FirstEntry, PhoneEntry("Telephone"));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (changed.ApprovalStateName, changed.InvalidationReasonCode));

        // A newer retained approval, then search turned off by the organization: the unchanged return is invalidated, not converted.
        await RegisterAsync(4, Enabled, FirstEntry);
        var policyBlocked = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        store.Policies.Single().PlaintextSearchPermitted = false;
        await RegisterAsync(5, Enabled, FirstEntry, PhoneEntry("Telephone"));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.OrganizationPolicyChanged), (policyBlocked.ApprovalStateName, policyBlocked.InvalidationReasonCode));
        store.Policies.Single().PlaintextSearchPermitted = true;

        // A pending protection change on return: invalidated as strengthened.
        await RegisterAsync(6, Enabled, FirstEntry);
        var pending = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldReclassification, ResourceEntityId = Phone, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        await RegisterAsync(7, Enabled, FirstEntry, PhoneEntry("Telephone"));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FieldStrengthened), (pending.ApprovalStateName, pending.InvalidationReasonCode));
        store.Resources.RemoveAll(r => r.ResourceTypeName == ResourceTypeNames.FieldReclassification);

        // Retained approvals disabled when the field returns: invalidated, never converted.
        await RegisterAsync(8, Enabled, FirstEntry);
        var disabled = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        await RegisterAsync(9, Disabled, FirstEntry, PhoneEntry("Telephone"));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (disabled.ApprovalStateName, disabled.InvalidationReasonCode));

        // No revival: every one of them stays invalidated through later registrations.
        await RegisterAsync(10, Enabled, FirstEntry, PhoneEntry("Telephone"));
        Assert.All(new[] { changed, policyBlocked, pending, disabled }, a => Assert.Equal(FieldSearchApprovalStates.Invalidated, a.ApprovalStateName));
        Assert.DoesNotContain(store.Approvals, a => a.FieldDefinitionId == Phone && a.ApprovalStateName == FieldSearchApprovalStates.Active);
    }

    [Fact]
    public async Task StrengtheningAndWithdrawalEndARetainedApproval()
    {
        await PhoneRemovedAsync(Enabled);
        var strengthened = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        var plan = await PlanAsync(Designer, SecurityCommandKind.PublishFormSecurityRevision, Form, new { form_definition_id = Form, field_definition_id = Phone, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2, field_classification = "Confidential" }, Enabled);
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FieldStrengthened), (strengthened.ApprovalStateName, strengthened.InvalidationReasonCode));
        Assert.Contains(strengthened, plan.ChangedApprovals);

        store.Resources.RemoveAll(r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement);
        var withdrawn = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        await PlanAsync(Administrator, SecurityCommandKind.WithdrawFieldSearch, Form, new WithdrawFieldSearchPayload(Form, Phone, "no longer needed"), Disabled); // withdrawal works even while disabled
        Assert.Equal(FieldSearchApprovalStates.Withdrawn, withdrawn.ApprovalStateName);
    }

    /// <summary>T11: tightening the organization policy invalidates a retained approval in the same revision, like any other approval.</summary>
    [Fact]
    public async Task TighteningTheOrganizationPolicyInvalidatesARetainedApproval()
    {
        await PhoneRemovedAsync(Enabled);
        var retained = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        var owner = Guid.CreateVersion7();
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = owner, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = owner.ToString("N"), IdentityBindingEnabled = true });
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, owner));

        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var payload = new UpdateMandatoryPolicyPayload("Public", PlaintextInternalFieldsPermitted: false, PlaintextDowngradePermitted: false, PlaintextSearchPermitted: true);
        var request = new SecurityCommandRequest(Org, SecurityCommandKind.UpdateMandatoryPolicy, new ResourceRef(ResourceKind.Organization, Org), 3, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(owner), request, graph, Ct, Enabled);

        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.OrganizationPolicyChanged), (retained.ApprovalStateName, retained.InvalidationReasonCode));
        Assert.Contains(retained, plan.ChangedApprovals);
        Assert.NotNull(plan.SearchPolicyToAdvance);
    }

    // ---- T9/D-H7/D-H13: field-to-form binding ----

    [Fact]
    public async Task AFieldBoundToOneFormCannotBeRegisteredApprovedOrProtectedForAnother()
    {
        var first = await RegisterAsync(1, Enabled, FirstEntry, PhoneEntry());
        Assert.Equal(new[] { First, Phone }.Order(), first.NewFieldFormBindings.Select(b => b.FieldDefinitionId).Order());
        Assert.All(first.NewFieldFormBindings, b => Assert.Equal((Form, FieldFormBindingSources.VersionRegistration), (b.FormDefinitionId, b.BindingSourceName)));
        Assert.Empty((await RegisterAsync(2, Enabled, FirstEntry, PhoneEntry())).NewFieldFormBindings); // reuse across versions of the same form is legitimate

        // Another form names the phone field: registration, protection commands and approvals are all refused; nothing is written.
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, OtherForm, new RegisterPublishedFormVersionPayload(OtherForm, 1, new string('f', 64), [PhoneEntry()]), Enabled), AuthorityErrorCodes.FieldBoundToAnotherForm);
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.PublishFormSecurityRevision, OtherForm, new { form_definition_id = OtherForm, field_definition_id = Phone, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2, field_classification = "Confidential" }, Enabled), AuthorityErrorCodes.FieldBoundToAnotherForm);
        await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.ApproveFieldSearch, OtherForm, new ApproveFieldSearchPayload(OtherForm, Phone, ["equals"], "GOV", FieldSearchApprovalGate.SyntheticStepUpEvidence), Enabled), AuthorityErrorCodes.FormVersionUntrusted); // no registered version of OtherForm
        Assert.DoesNotContain(store.PublishedVersions, v => v.FormDefinitionId == OtherForm);
        Assert.Single(store.FieldFormBindings, b => b.FieldDefinitionId == Phone);

        // A protection command first binds an unbound field; a requirement recorded for another form refuses.
        var protectedPlan = await PlanAsync(Designer, SecurityCommandKind.PublishFormSecurityRevision, Form, new { form_definition_id = Form, field_definition_id = Notes, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2, field_classification = "Confidential" }, Enabled);
        Assert.Equal((Notes, Form, FieldFormBindingSources.ProtectionCommand), (protectedPlan.NewFieldFormBindings.Single().FieldDefinitionId, protectedPlan.NewFieldFormBindings.Single().FormDefinitionId, protectedPlan.NewFieldFormBindings.Single().BindingSourceName));
        var stray = Guid.CreateVersion7();
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = stray, ParentResourceEntityId = OtherForm, ResourceLifecycleState = ResourceLifecycleStates.Active });
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.PublishFormSecurityRevision, Form, new { form_definition_id = Form, field_definition_id = stray, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2, field_classification = "Confidential" }, Enabled), AuthorityErrorCodes.FieldBoundToAnotherForm);
    }

    [Fact]
    public async Task OneRegistrationNamingAFieldTwiceIsRefusedAndABindingIsWrittenOnce()
    {
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, Form, new RegisterPublishedFormVersionPayload(Form, 1, new string('a', 64), [PhoneEntry(), PhoneEntry("Again")]), Enabled), AuthorityErrorCodes.InvalidCommand);
        await RegisterAsync(1, Enabled, FirstEntry, PhoneEntry());
        Assert.Throws<InvalidOperationException>(() => store.AddFieldFormBindingForTest(new FieldFormBinding { OrganizationId = Org, FieldDefinitionId = Phone, FormDefinitionId = OtherForm })); // the primary key (organization, field)
    }

    // ---- T17/D-H16: effectiveness at use time ----

    [Fact]
    public async Task ARetainedApprovalIsEffectiveOnlyWhileEnabledAbsentBoundAndEligible()
    {
        await PhoneRemovedAsync(Enabled);
        var retained = await ApprovedAsync(Phone, FieldSearchApprovalScopes.RetainedField, Enabled);
        await RegisterAsync(3, Enabled, FirstEntry, EmailEntry);
        var current = await ApprovedAsync(First, null, Enabled);

        async Task<HashSet<Guid>> IneffectiveAsync(bool enabled)
        {
            await using var scope = await store.BeginScopeAsync(Ct);
            return await RetainedFieldApprovals.IneffectiveAsync(scope, Org, store.Approvals, store.Policies.Single(), enabled, Ct);
        }

        Assert.Empty(await IneffectiveAsync(true));
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync(false)); // disabled: never effective; a current approval never listed

        store.Policies.Single().MinimumFieldClassification = "Confidential"; // tightened while (say) an older binary could not invalidate it
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync(true));
        store.Policies.Single().MinimumFieldClassification = "Public";

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = Phone, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync(true));
        store.Resources.RemoveAll(r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement);

        store.FieldFormBindings.RemoveAll(b => b.FieldDefinitionId == Phone); // binding missing: fail closed
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync(true));
        store.AddFieldFormBindingForTest(new FieldFormBinding { OrganizationId = Org, FieldDefinitionId = Phone, FormDefinitionId = Form, BindingSourceName = FieldFormBindingSources.Backfill });
        Assert.Empty(await IneffectiveAsync(true));

        // D-H16: the field is back in the latest version without a conversion (only an older binary could register that): not effective.
        store.PublishedVersions.Add(new PublishedFormVersion { PublishedFormVersionId = Guid.CreateVersion7(), OrganizationId = Org, FormDefinitionId = Form, FormVersionNumber = 9, LayoutDigest = new byte[32], FieldManifestJson = FieldSearchRules.WriteManifest([FirstEntry, PhoneEntry()]) });
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync(true));
        Assert.Equal(FieldSearchApprovalScopes.CurrentVersion, current.ApprovalScopeName);
    }

    // ---- T23/D-H18: configuration and the connection fence ----

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Disabled", false)]
    [InlineData("Enabled", true)]
    public void TheRetainedFieldSettingDefaultsToDisabled(string? value, bool enabled) =>
        Assert.Equal(enabled, FieldSearchApprovalGate.Unavailable.WithRetainedFieldApprovals(value).RetainedFieldApprovalsEnabled);

    [Theory]
    [InlineData("enabled")]
    [InlineData("On")]
    [InlineData("true")]
    public void AnUnknownRetainedFieldSettingRefusesStartup(string value) =>
        Assert.Throws<InvalidOperationException>(() => FieldSearchApprovalGate.Unavailable.WithRetainedFieldApprovals(value));

    [Fact]
    public void TheGateKeepsItsStepUpModeWhenTheRetainedSettingIsApplied()
    {
        Assert.True(Enabled.ApprovalsOperational);
        Assert.True(Enabled.RetainedFieldApprovalsEnabled);
        Assert.False(FieldSearchApprovalGate.Unavailable.RetainedFieldApprovalsEnabled);
    }

    [Fact]
    public void EveryRuntimeConnectionDeclaresScopeAwarenessOnceWithoutLosingOtherOptions()
    {
        var plain = FieldSearchScopeConnection.WithScopeAwareness("Host=db;Database=authority;Username=runtime;Password=x")!;
        Assert.Contains("-c authority.field_search_scope_aware=on", new Npgsql.NpgsqlConnectionStringBuilder(plain).Options, StringComparison.Ordinal);
        var withOptions = FieldSearchScopeConnection.WithScopeAwareness("Host=db;Options=-c statement_timeout=5000")!;
        var options = new Npgsql.NpgsqlConnectionStringBuilder(withOptions).Options!;
        Assert.Contains("statement_timeout=5000", options, StringComparison.Ordinal);
        Assert.Contains("authority.field_search_scope_aware=on", options, StringComparison.Ordinal);
        Assert.Equal(withOptions, FieldSearchScopeConnection.WithScopeAwareness(withOptions)); // idempotent
        Assert.Null(FieldSearchScopeConnection.WithScopeAwareness(null));
    }
}
