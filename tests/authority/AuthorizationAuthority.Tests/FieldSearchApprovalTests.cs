using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Phase C7 M2 — AC-6 at unit level: the approval, withdrawal, query-grant and version-registration planners and their
/// strengthening/policy hooks, against the in-memory store with Authority-held facts only. The full command path (journal,
/// projection, SpiceDB, capabilities) runs against Docker in the Stage B suite (FieldSearchApprovalTests there).
/// </summary>
public sealed class FieldSearchApprovalTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Designer = Guid.CreateVersion7();
    private static readonly Guid Searcher = Guid.CreateVersion7();
    private static readonly Guid PublicText = Guid.CreateVersion7();
    private static readonly Guid InternalNumber = Guid.CreateVersion7();
    private static readonly Guid ConfidentialText = Guid.CreateVersion7();
    private static readonly Guid EncryptedText = Guid.CreateVersion7();
    private static readonly Guid Status = Guid.CreateVersion7();
    private static readonly Guid Reference = Guid.CreateVersion7();
    private static readonly Guid OpenOption = Guid.CreateVersion7();

    private static readonly FieldSearchApprovalGate Synthetic = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly InMemoryAuthorityStore store = new();

    public FieldSearchApprovalTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        foreach (var user in new[] { Administrator, Owner, Designer, Searcher })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = EncryptedText, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Administrator));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, Owner));
        store.Edges.Add(Edge(ResourceTypeNames.FormDefinition, Form, "designer", Designer));
        store.Policies.Add(new OrganizationProtectionPolicy { OrganizationProtectionPolicyId = Guid.CreateVersion7(), OrganizationId = Org, MinimumFieldClassification = "Public", PlaintextInternalFieldsPermitted = true, PlaintextSearchPermitted = true, SearchPolicyRevision = 1 });
        store.PublishedVersions.Add(Version(1, Entries()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<PublishedFieldEntry> Entries(string publicLabel = "Surname") =>
    [
        new(PublicText, "String", "Public", "Standard", publicLabel),
        new(InternalNumber, "Integer", "Internal", "Standard", "Count"),
        new(ConfidentialText, "String", "Confidential", "Standard", "Notes"),
        new(EncryptedText, "String", "Restricted", "ApplicationEncrypted", "Secret"),
        new(Status, "Option", "Public", "Standard", "Status", [new PublishedOptionEntry(OpenOption, "Open")]),
        new(Reference, "Guid", "Public", "Standard", "Reference"),
    ];

    private static PublishedFormVersion Version(int number, IEnumerable<PublishedFieldEntry> fields) => new()
    {
        PublishedFormVersionId = Guid.CreateVersion7(),
        OrganizationId = Org,
        FormDefinitionId = Form,
        FormVersionNumber = number,
        LayoutDigest = new byte[32],
        FieldManifestJson = FieldSearchRules.WriteManifest(fields),
    };

    private static AuthorityRelationshipEdge Edge(string type, Guid id, string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = type,
        ResourceEntityId = id,
        RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User,
        SubjectEntityId = user,
        SubjectRelationName = string.Empty,
    };

    private BoundActor Actor(Guid user) => new(new VerifiedActor("https://idp", user.ToString("N"), "test"), store.Bindings.Single(b => b.AuthorityUserId == user));

    private async Task<CommandPlan> PlanAsync(Guid user, SecurityCommandKind kind, ResourceKind resourceKind, Guid resourceId, object payload, FieldSearchApprovalGate? gate = null)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var request = new SecurityCommandRequest(Org, kind, new ResourceRef(resourceKind, resourceId), 3, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, gate ?? Synthetic);
    }

    private Task<CommandPlan> ApproveAsync(Guid user, Guid field, string[] operators, FieldSearchApprovalGate? gate = null, string? evidence = FieldSearchApprovalGate.SyntheticStepUpEvidence) =>
        PlanAsync(user, SecurityCommandKind.ApproveFieldSearch, ResourceKind.FormDefinition, Form, new ApproveFieldSearchPayload(Form, field, operators, "GOV-2026-17", evidence), gate);

    private async Task<FieldSearchApproval> ApprovedAsync(Guid field, params string[] operators)
    {
        var plan = await ApproveAsync(Administrator, field, operators);
        var approval = Assert.Single(plan.NewApprovals);
        approval.ApprovedOn = DateTimeOffset.UtcNow;
        store.Approvals.Add(approval);
        return approval;
    }

    private static async Task<string> RejectedAsync(Func<Task> action, string? code = null)
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(action);
        if (code is not null)
        {
            Assert.Equal(code, ex.ErrorCode);
        }

        return ex.Message;
    }

    // ---- 1–4: who may approve ----

    [Fact]
    public async Task ASecurityAdministratorApprovesAnEligiblePublicFieldBoundToTheRegisteredVersion()
    {
        var plan = await ApproveAsync(Administrator, PublicText, ["startsWith", "equals"]);
        var approval = Assert.Single(plan.NewApprovals);
        Assert.Equal((Form, PublicText, 1, "String", "Public", "Standard", "Surname"), (approval.FormDefinitionId, approval.FieldDefinitionId, approval.ApprovedFormVersionNumber, approval.ApprovedFieldDataType, approval.ApprovedFieldClassification, approval.ApprovedProtectionMode, approval.ApprovedFieldLabelSnapshot));
        Assert.Equal("[\"equals\",\"startsWith\"]", approval.ApprovedOperatorsJson);
        Assert.Equal(FieldSearchApprovalStates.Active, approval.ApprovalStateName);
        Assert.NotNull(plan.SearchPolicyToAdvance);
        Assert.DoesNotContain(FieldSearchApprovalGate.SyntheticStepUpEvidence, plan.NormalizedPayloadJson, StringComparison.Ordinal); // the marker is not journaled as if it were evidence
    }

    [Fact]
    public async Task DesignersAndOrganizationOwnersWithoutAdministerSecurityCannotApprove()
    {
        await RejectedAsync(() => ApproveAsync(Designer, PublicText, ["equals"]), AuthorityErrorCodes.Forbidden);
        await RejectedAsync(() => ApproveAsync(Owner, PublicText, ["equals"]), AuthorityErrorCodes.Forbidden);
        await RejectedAsync(() => ApproveAsync(Searcher, PublicText, ["equals"]), AuthorityErrorCodes.Forbidden);
    }

    [Fact]
    public async Task AFormOfAnotherOrganizationIsNeverApproved()
    {
        var foreignForm = Guid.CreateVersion7(); // registered nowhere in this organization's registry
        await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.ApproveFieldSearch, ResourceKind.FormDefinition, foreignForm, new ApproveFieldSearchPayload(foreignForm, PublicText, ["equals"], "GOV-1", FieldSearchApprovalGate.SyntheticStepUpEvidence)));
    }

    // ---- 5–7: what may be approved ----

    [Fact]
    public async Task ConfidentialRestrictedAndEncryptedFieldsAreNeverEligible()
    {
        Assert.Equal(FieldSearchEligibility.ClassificationNotPermitted, await RejectedAsync(() => ApproveAsync(Administrator, ConfidentialText, ["equals"]), AuthorityErrorCodes.FieldSearchNotEligible));
        Assert.Equal(FieldSearchEligibility.Encrypted, await RejectedAsync(() => ApproveAsync(Administrator, EncryptedText, ["equals"]), AuthorityErrorCodes.FieldSearchNotEligible));
        Assert.Equal(FieldSearchEligibility.TypeNotSearchable, await RejectedAsync(() => ApproveAsync(Administrator, Reference, ["equals"]), AuthorityErrorCodes.FieldSearchNotEligible));
    }

    [Fact]
    public async Task AnAuthorityHeldEncryptionRequirementWinsOverAPublishedStandardEntry()
    {
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = PublicText, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Assert.Equal(FieldSearchEligibility.Encrypted, await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"]), AuthorityErrorCodes.FieldSearchNotEligible));
    }

    [Fact]
    public async Task OrganizationFloorsDecideInternalFieldsAndTheSearchSwitch()
    {
        var policy = store.Policies.Single();
        policy.PlaintextInternalFieldsPermitted = false;
        Assert.Equal(FieldSearchEligibility.ClassificationNotPermitted, await RejectedAsync(() => ApproveAsync(Administrator, InternalNumber, ["equals"])));
        policy.PlaintextInternalFieldsPermitted = true;
        policy.MinimumFieldClassification = "Confidential";
        Assert.Equal(FieldSearchEligibility.ClassificationNotPermitted, await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"])));
        policy.MinimumFieldClassification = "Public";
        policy.PlaintextSearchPermitted = false;
        Assert.Equal(FieldSearchEligibility.OrganizationSearchDisabled, await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"])));
        store.Policies.Clear();
        Assert.Equal(FieldSearchEligibility.OrganizationPolicyMissing, await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"])));
    }

    [Fact]
    public async Task OperatorsMustBeOperatorsOfTheFieldsTypeAndNonEmptyAndDistinct()
    {
        Assert.Equal("OPERATOR_NOT_APPLICABLE", await RejectedAsync(() => ApproveAsync(Administrator, InternalNumber, ["contains"]), AuthorityErrorCodes.FieldSearchNotEligible));
        Assert.Equal("OPERATOR_NOT_APPLICABLE", await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["regex"])));
        Assert.Equal("OPERATOR_NOT_APPLICABLE", await RejectedAsync(() => ApproveAsync(Administrator, PublicText, [])));
        Assert.Equal("OPERATOR_NOT_APPLICABLE", await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals", "equals"])));
    }

    // ---- 8–10: binding to Authority-held definitions (G21) ----

    [Fact]
    public async Task OnlyTheLatestRegisteredVersionCountsAndAnUnregisteredFieldOrFormIsUntrusted()
    {
        var unknownField = Guid.CreateVersion7(); // e.g. a field id substituted in business metadata
        await RejectedAsync(() => ApproveAsync(Administrator, unknownField, ["equals"]), AuthorityErrorCodes.FormVersionUntrusted);
        store.PublishedVersions.Add(Version(2, Entries().Where(e => e.FieldDefinitionId != PublicText)));
        await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"]), AuthorityErrorCodes.FormVersionUntrusted);
        store.PublishedVersions.Clear();
        await RejectedAsync(() => ApproveAsync(Administrator, InternalNumber, ["equals"]), AuthorityErrorCodes.FormVersionUntrusted);
    }

    [Fact]
    public async Task TheApprovalFreezesTheRegisteredLabelAndOptionsNotAnythingTheRequestCarries()
    {
        var approval = (await ApproveAsync(Administrator, Status, ["is"])).NewApprovals.Single();
        Assert.Equal("Status", approval.ApprovedFieldLabelSnapshot);
        var options = FieldSearchRules.ReadOptions(approval);
        Assert.Equal([(OpenOption, "Open")], options.Select(o => (o.FieldOptionId, o.OptionLabel)));
    }

    [Fact]
    public async Task RegisteringAVersionValidatesEntriesRefusesEncryptionMismatchesAndOnlyMovesForward()
    {
        var mismatch = Entries().Select(e => e.FieldDefinitionId == EncryptedText ? e with { ProtectionMode = "Standard", FieldClassification = "Public" } : e).ToList();
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, new string('a', 64), mismatch)), AuthorityErrorCodes.FormVersionUntrusted);
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 1, new string('a', 64), Entries())), AuthorityErrorCodes.FormVersionUntrusted);
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, "not-a-digest", Entries())), AuthorityErrorCodes.InvalidCommand);
        await RejectedAsync(() => PlanAsync(Searcher, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, new string('a', 64), Entries())), AuthorityErrorCodes.Forbidden);
        var plan = await PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, new string('a', 64), Entries()));
        Assert.Equal(2, plan.PublishedVersion!.FormVersionNumber);
    }

    [Fact]
    public async Task ANewVersionCarriesUnchangedApprovalsAndInvalidatesChangedOrRemovedOnes()
    {
        var kept = await ApprovedAsync(InternalNumber, "equals");
        var relabelled = await ApprovedAsync(PublicText, "equals");
        var removed = await ApprovedAsync(Status, "is");
        var next = Entries("Family name").Where(e => e.FieldDefinitionId != Status).ToList();
        var plan = await PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, new string('b', 64), next));
        Assert.Equal((FieldSearchApprovalStates.Active, 2), (kept.ApprovalStateName, kept.ApprovedFormVersionNumber));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (relabelled.ApprovalStateName, relabelled.InvalidationReasonCode));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (removed.ApprovalStateName, removed.InvalidationReasonCode));
        Assert.Equal(3, plan.ChangedApprovals.Count);
    }

    /// <summary>
    /// Regression (found on the real stack): the option snapshot is stored as jsonb, which PostgreSQL reformats, so an
    /// unchanged Option field must be recognised by value — not by JSON text — when the next version is registered.
    /// Options keep the published order; reordering is not a change, relabelling is.
    /// </summary>
    [Fact]
    public async Task AnUnchangedOptionFieldKeepsItsApprovalWhateverTheStoredJsonFormattingAndTheOptionsKeepThePublishedOrder()
    {
        var closed = Guid.CreateVersion7();
        List<PublishedFieldEntry> WithStatus(params PublishedOptionEntry[] options) => [.. Entries().Where(e => e.FieldDefinitionId != Status), new(Status, "Option", "Public", "Standard", "Status", options)];
        store.PublishedVersions.Clear();
        store.PublishedVersions.Add(Version(1, WithStatus(new(closed, "Closed"), new(OpenOption, "Open"))));
        var approval = await ApprovedAsync(Status, "is");
        Assert.Equal([closed, OpenOption], FieldSearchRules.ReadOptions(approval).Select(o => o.FieldOptionId));
        // What a jsonb round trip does to the text.
        approval.ApprovedOptionSnapshotJson = JsonSerializer.Serialize(JsonDocument.Parse(approval.ApprovedOptionSnapshotJson).RootElement, Indented);

        await PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, new string('b', 64), WithStatus(new(OpenOption, "Open"), new(closed, "Closed"))));
        Assert.Equal((FieldSearchApprovalStates.Active, 2), (approval.ApprovalStateName, approval.ApprovedFormVersionNumber));

        await PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 3, new string('c', 64), WithStatus(new(OpenOption, "Open"), new(closed, "Dismissed"))));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (approval.ApprovalStateName, approval.InvalidationReasonCode));
    }

    /// <summary>Phase C7 M3: fields inside repeating sections are not searchable in V1; moving an approved field into one invalidates it.</summary>
    [Fact]
    public async Task RepeatingGroupFieldsAreNeverEligibleAndMovingAnApprovedFieldIntoOneInvalidatesIt()
    {
        var approval = await ApprovedAsync(PublicText, "equals");
        List<PublishedFieldEntry> Grouped(Guid field) => [.. Entries().Select(e => e.FieldDefinitionId == field ? e with { InRepeatingGroup = true } : e)];
        var plan = await PlanAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, ResourceKind.FormDefinition, Form, new RegisterPublishedFormVersionPayload(Form, 2, new string('d', 64), Grouped(PublicText), "Case intake"));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (approval.ApprovalStateName, approval.InvalidationReasonCode));
        Assert.Equal("Case intake", plan.PublishedVersion!.FormTitleSnapshot);

        store.PublishedVersions.Clear();
        store.PublishedVersions.Add(Version(1, Grouped(InternalNumber)));
        Assert.Equal(FieldSearchEligibility.RepeatingGroupNotSearchable, FieldSearchRules.Eligibility(Grouped(InternalNumber).Single(e => e.FieldDefinitionId == InternalNumber), store.Policies.Single(), new FieldSearchRules.ProtectionFacts(false, false, false)));
        await RejectedAsync(() => ApproveAsync(Administrator, InternalNumber, ["equals"]), AuthorityErrorCodes.FieldSearchNotEligible);
    }

    // ---- 11–12: strengthening, policy changes, withdrawal and query permission ----

    [Fact]
    public async Task EncryptingAnApprovedFieldInvalidatesItsApprovalInTheSameCommand()
    {
        var approval = await ApprovedAsync(PublicText, "equals");
        var plan = await PlanAsync(Designer, SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new PublishFormSecurityRevisionPayload(Form, PublicText, "ApplicationEncrypted", 2, "Confidential"));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FieldStrengthened), (approval.ApprovalStateName, approval.InvalidationReasonCode));
        Assert.Contains(approval, plan.ChangedApprovals);
        Assert.Single(plan.RegisterResources, r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement);
    }

    [Fact]
    public async Task TurningSearchOffOrRaisingFloorsInvalidatesEveryNonConformingApproval()
    {
        var internalApproval = await ApprovedAsync(InternalNumber, "equals");
        var publicApproval = await ApprovedAsync(PublicText, "equals");
        await PlanAsync(Owner, SecurityCommandKind.UpdateMandatoryPolicy, ResourceKind.Organization, Org, new UpdateMandatoryPolicyPayload("Public", PlaintextInternalFieldsPermitted: false, PlaintextDowngradePermitted: false, PlaintextSearchPermitted: true));
        Assert.Equal(FieldSearchApprovalStates.Invalidated, internalApproval.ApprovalStateName);
        Assert.Equal(FieldSearchApprovalStates.Active, publicApproval.ApprovalStateName);
        var plan = await PlanAsync(Owner, SecurityCommandKind.UpdateMandatoryPolicy, ResourceKind.Organization, Org, new UpdateMandatoryPolicyPayload("Public", false, false, PlaintextSearchPermitted: false));
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.OrganizationPolicyChanged), (publicApproval.ApprovalStateName, publicApproval.InvalidationReasonCode));
        Assert.NotNull(plan.SearchPolicyToAdvance);
    }

    [Fact]
    public async Task WithdrawalIsForSecurityAdministratorsWithAReason()
    {
        var approval = await ApprovedAsync(PublicText, "equals");
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.WithdrawFieldSearch, ResourceKind.FormDefinition, Form, new WithdrawFieldSearchPayload(Form, PublicText, "no longer needed")), AuthorityErrorCodes.Forbidden);
        await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.WithdrawFieldSearch, ResourceKind.FormDefinition, Form, new WithdrawFieldSearchPayload(Form, PublicText, " ")), AuthorityErrorCodes.InvalidCommand);
        await PlanAsync(Administrator, SecurityCommandKind.WithdrawFieldSearch, ResourceKind.FormDefinition, Form, new WithdrawFieldSearchPayload(Form, PublicText, "no longer needed"));
        Assert.Equal(FieldSearchApprovalStates.Withdrawn, approval.ApprovalStateName);
    }

    [Fact]
    public async Task QueryPermissionIsGrantedAndRevokedOnlyBySecurityAdministrators()
    {
        await RejectedAsync(() => PlanAsync(Owner, SecurityCommandKind.UpdateFieldSearchers, ResourceKind.FormDefinition, Form, new UpdateFieldSearchersPayload(Form, [Searcher], [], [], [])), AuthorityErrorCodes.Forbidden);
        await RejectedAsync(() => PlanAsync(Designer, SecurityCommandKind.UpdateFieldSearchers, ResourceKind.FormDefinition, Form, new UpdateFieldSearchersPayload(Form, [Searcher], [], [], [])), AuthorityErrorCodes.Forbidden);
        var grant = await PlanAsync(Administrator, SecurityCommandKind.UpdateFieldSearchers, ResourceKind.FormDefinition, Form, new UpdateFieldSearchersPayload(Form, [Searcher], [], [], []));
        Assert.Equal((ResourceTypeNames.FormDefinition, Form, "field_searcher", ResourceTypeNames.User, Searcher, string.Empty), Assert.Single(grant.AddEdges));
        store.Edges.Add(Edge(ResourceTypeNames.FormDefinition, Form, "field_searcher", Searcher));
        var revoke = await PlanAsync(Administrator, SecurityCommandKind.UpdateFieldSearchers, ResourceKind.FormDefinition, Form, new UpdateFieldSearchersPayload(Form, [], [], [Searcher], []));
        Assert.Single(revoke.RemoveEdges);
        await RejectedAsync(() => PlanAsync(Administrator, SecurityCommandKind.UpdateFieldSearchers, ResourceKind.FormDefinition, Form, new UpdateFieldSearchersPayload(Form, [Guid.CreateVersion7()], [], [], [])));
    }

    // ---- 15–16: step-up gate (OD-7) ----

    [Fact]
    public async Task WithoutTheDevelopmentStepUpPathEveryApprovalIsRefusedEvenForAnAdministrator()
    {
        await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"], FieldSearchApprovalGate.Unavailable), AuthorityErrorCodes.StepUpRequired);
        await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"], evidence: null), AuthorityErrorCodes.StepUpRequired);
        await RejectedAsync(() => ApproveAsync(Administrator, PublicText, ["equals"], evidence: "forged"), AuthorityErrorCodes.StepUpRequired);
    }

    [Fact]
    public void TheDevelopmentStepUpPathCannotBeEnabledInStagingOrProductionOrWithARealIdentityProvider()
    {
        Assert.False(FieldSearchApprovalGate.Create(null, "Production", "EntraId").ApprovalsOperational);
        Assert.False(FieldSearchApprovalGate.Create("Unavailable", "Testing", "TestIdentityProvider").ApprovalsOperational);
        foreach (var environment in new[] { "Production", "Staging", "Preproduction" })
        {
            Assert.Throws<InvalidOperationException>(() => FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, environment, "TestIdentityProvider"));
        }

        Assert.Throws<InvalidOperationException>(() => FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Development", "EntraId"));
        Assert.Throws<InvalidOperationException>(() => FieldSearchApprovalGate.Create("Anything", "Development", "TestIdentityProvider"));
        Assert.True(FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Development", "TestIdentityProvider").ApprovalsOperational);
    }
}
