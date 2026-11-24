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
/// C-188 BF-1 at unit level (in-memory store, Authority-held facts only): a publication staged before the business commits it makes
/// every approval of the form ineffective - including the ordinary approval of a field the new version removes, and a retained
/// approval of a field it brings back - until it is resolved; only a resolution matching the staged version ends that state; a
/// registration from the staged manifest applies D-H3/D-H4 exactly as a one-step registration did; abandonment registers nothing;
/// approvals, one-step registrations and any other prepare of the form are refused meanwhile (C-191: a pending publication is never
/// superseded); and resolution re-validates against current facts. The real-stack proof (journal, both engines, open pages, snapshots, business recovery, concurrency) is the Stage B class
/// FormVersionPublicationBarrierTests.
/// </summary>
public sealed class FormVersionPublicationTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Administrator = Guid.CreateVersion7();
    private static readonly Guid Designer = Guid.CreateVersion7();
    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Phone = Guid.CreateVersion7();
    private static readonly Guid Email = Guid.CreateVersion7();

    private static readonly FieldSearchApprovalGate Enabled = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider")
        .WithRetainedFieldApprovals(FieldSearchApprovalGate.RetainedFieldApprovalsEnabledValue);

    private readonly InMemoryAuthorityStore store = new();

    public FormVersionPublicationTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        foreach (var user in new[] { Administrator, Designer })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(ResourceTypeNames.FormDefinition, Form, "designer", Designer));
        store.Edges.Add(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, Administrator));
        store.Policies.Add(new OrganizationProtectionPolicy { OrganizationProtectionPolicyId = Guid.CreateVersion7(), OrganizationId = Org, MinimumFieldClassification = "Public", PlaintextInternalFieldsPermitted = true, PlaintextSearchPermitted = true, PlaintextDowngradePermitted = true, SearchPolicyRevision = 1 });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PublishedFieldEntry FirstEntry => new(First, "String", "Internal", "Standard", "First name");

    private static PublishedFieldEntry PhoneEntry => new(Phone, "String", "Internal", "Standard", "Phone");

    private static PublishedFieldEntry EmailEntry => new(Email, "String", "Internal", "Standard", "Email");

    /// <summary>The layout digest of version n is 32 bytes of n.</summary>
    private static string Digest(int number) => string.Concat(Enumerable.Repeat(((byte)number).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));

    private static AuthorityRelationshipEdge Edge(string type, Guid id, string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = user, SubjectRelationName = string.Empty,
    };

    private BoundActor Actor(Guid user) => new(new VerifiedActor("https://idp", user.ToString("N"), "test"), store.Bindings.Single(b => b.AuthorityUserId == user));

    /// <summary>Plans a command and applies it the way ApplyPlan does for these tables (versions, bindings, approvals, publications).</summary>
    private async Task<CommandPlan> RunAsync(Guid user, SecurityCommandKind kind, object payload)
    {
        CommandPlan plan;
        await using (var scope = await store.BeginScopeAsync(Ct))
        {
            var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
            var request = new SecurityCommandRequest(Org, kind, new ResourceRef(ResourceKind.FormDefinition, Form), 3, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
            plan = await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, Enabled);
        }

        var command = Guid.CreateVersion7();
        if (plan.PublishedVersion is { } version)
        {
            version.RegisteredBySecurityCommandId = command;
            store.PublishedVersions.Add(version);
        }

        foreach (var binding in plan.NewFieldFormBindings)
        {
            store.AddFieldFormBindingForTest(binding);
        }

        foreach (var approval in plan.NewApprovals)
        {
            approval.ApprovedOn = DateTimeOffset.UtcNow;
            store.Approvals.Add(approval);
        }

        foreach (var resolved in plan.ResolvedPublications)
        {
            resolved.ResolvedBySecurityCommandId = command;
            resolved.ResolvedOn = DateTimeOffset.UtcNow;
        }

        if (plan.NewPublication is { } publication)
        {
            publication.PreparedBySecurityCommandId = command;
            publication.PreparedOn = DateTimeOffset.UtcNow;
            await using var scope = await store.BeginScopeAsync(Ct);
            scope.AddFormVersionPublication(publication);
        }

        return plan;
    }

    private Task<CommandPlan> RegisterOneStepAsync(int number, params PublishedFieldEntry[] fields) =>
        RunAsync(Designer, SecurityCommandKind.RegisterPublishedFormVersion, new RegisterPublishedFormVersionPayload(Form, number, Digest(number), fields));

    private async Task<Guid> PrepareAsync(int number, params PublishedFieldEntry[] fields)
    {
        var businessVersion = Guid.CreateVersion7();
        await RunAsync(Designer, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, businessVersion, number, Digest(number), fields, "Intake"));
        return businessVersion;
    }

    private Task<CommandPlan> ResolveAsync(Guid businessVersion, int number, string resolution, string? digest = null) =>
        RunAsync(Designer, SecurityCommandKind.ResolveFormVersionPublication, new ResolveFormVersionPublicationPayload(Form, businessVersion, number, digest ?? Digest(number), resolution));

    private async Task<FieldSearchApproval> ApproveAsync(Guid field, string? scope = null)
    {
        var plan = await RunAsync(Administrator, SecurityCommandKind.ApproveFieldSearch, new ApproveFieldSearchPayload(Form, field, ["equals"], "GOV-BF1", FieldSearchApprovalGate.SyntheticStepUpEvidence, scope));
        return Assert.Single(plan.NewApprovals);
    }

    private async Task<HashSet<Guid>> IneffectiveAsync()
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        return await RetainedFieldApprovals.IneffectiveAsync(scope, Org, store.Approvals, store.Policies.Single(), retainedEnabled: true, Ct);
    }

    private static async Task RejectedAsync(Func<Task> action, string code) => Assert.Equal(code, (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode);

    private FormVersionPublication? Pending => store.Publications.SingleOrDefault(p => p.PublicationStateName == FormVersionPublicationStates.Pending);

    /// <summary>BF-1: the publication that removes an approved field is staged first; from then on the ordinary approval confers nothing.</summary>
    [Fact]
    public async Task APendingPublicationThatRemovesAnApprovedFieldMakesTheApprovalIneffectiveUntilItIsRegistered()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var phone = await ApproveAsync(Phone);
        var first = await ApproveAsync(First);
        Assert.Empty(await IneffectiveAsync());

        var v2 = await PrepareAsync(2, FirstEntry, EmailEntry); // phone removed; the business has not committed yet (or its registration failed)
        Assert.Equal((v2, 2, "Intake"), (Pending!.BusinessFormVersionId, Pending.FormVersionNumber, Pending.FormTitleSnapshot));
        Assert.Equal(FieldSearchApprovalStates.Active, phone.ApprovalStateName); // nothing is invalidated before the Authority knows the outcome
        Assert.Equal(new[] { phone.FieldSearchApprovalId, first.FieldSearchApprovalId }.Order(), (await IneffectiveAsync()).Order()); // every approval of the form, fail closed
        Assert.DoesNotContain(store.PublishedVersions, v => v.FormVersionNumber == 2);

        var registered = await ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered);
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (phone.ApprovalStateName, phone.InvalidationReasonCode)); // D-H3 at registration
        Assert.Equal((FieldSearchApprovalStates.Active, 2), (first.ApprovalStateName, first.ApprovedFormVersionNumber)); // unchanged entry carried over
        Assert.Equal((2, "Intake"), (registered.PublishedVersion!.FormVersionNumber, registered.PublishedVersion.FormTitleSnapshot)); // from the staged manifest
        Assert.Null(Pending);
        Assert.Equal(FormVersionPublicationStates.Registered, store.Publications.Single().PublicationStateName);
        Assert.Empty(await IneffectiveAsync());
        Assert.NotNull(registered.SearchPolicyToAdvance);
    }

    /// <summary>The field-return case: a retained approval of a field the pending publication brings back confers nothing until registration converts it.</summary>
    [Fact]
    public async Task APendingPublicationThatBringsARetainedFieldBackMakesItIneffectiveUntilRegistrationConvertsIt()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        await RegisterOneStepAsync(2, FirstEntry);
        var retained = await ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField);
        Assert.Empty(await IneffectiveAsync());

        var v3 = await PrepareAsync(3, FirstEntry, PhoneEntry);
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync());
        Assert.Equal(FieldSearchApprovalScopes.RetainedField, retained.ApprovalScopeName);

        await ResolveAsync(v3, 3, FormVersionPublicationResolutions.Registered);
        Assert.Equal((FieldSearchApprovalStates.Active, FieldSearchApprovalScopes.CurrentVersion, 3), (retained.ApprovalStateName, retained.ApprovalScopeName, retained.ApprovedFormVersionNumber)); // D-H4
        Assert.Empty(await IneffectiveAsync());
    }

    /// <summary>A staged version the business never committed is withdrawn: nothing registered, the approvals of the latest registered version apply again.</summary>
    [Fact]
    public async Task AnAbandonedPublicationRegistersNothingAndRestoresTheRegisteredState()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var phone = await ApproveAsync(Phone);
        var v2 = await PrepareAsync(2, FirstEntry);
        Assert.Equal([phone.FieldSearchApprovalId], await IneffectiveAsync());

        var abandoned = await ResolveAsync(v2, 2, FormVersionPublicationResolutions.Abandoned);
        Assert.Null(abandoned.PublishedVersion);
        Assert.Equal(FormVersionPublicationStates.Abandoned, store.Publications.Single().PublicationStateName);
        Assert.Equal(FieldSearchApprovalStates.Active, phone.ApprovalStateName);
        Assert.Empty(await IneffectiveAsync());
        Assert.Single(store.PublishedVersions);
        Assert.NotNull(abandoned.SearchPolicyToAdvance);
    }

    /// <summary>Only the exact pending publication can be resolved; a resolution is never replayed onto a later state.</summary>
    [Fact]
    public async Task OnlyTheExactPendingPublicationCanBeResolvedAndOnlyOnce()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var v2 = await PrepareAsync(2, FirstEntry);
        await RejectedAsync(() => ResolveAsync(Guid.CreateVersion7(), 2, FormVersionPublicationResolutions.Registered), AuthorityErrorCodes.FormPublicationNotPending); // another version id
        await RejectedAsync(() => ResolveAsync(v2, 3, FormVersionPublicationResolutions.Registered), AuthorityErrorCodes.FormPublicationNotPending); // another number
        await RejectedAsync(() => ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered, Digest(9)), AuthorityErrorCodes.FormPublicationNotPending); // another layout
        await RejectedAsync(() => ResolveAsync(v2, 2, "Published"), AuthorityErrorCodes.InvalidCommand);
        var stranger = Guid.CreateVersion7(); // a member with no design permission and no administrator role (same rule as registration)
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = stranger, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = stranger.ToString("N"), IdentityBindingEnabled = true });
        await RejectedAsync(() => RunAsync(stranger, SecurityCommandKind.ResolveFormVersionPublication, new ResolveFormVersionPublicationPayload(Form, v2, 2, Digest(2), FormVersionPublicationResolutions.Abandoned)), AuthorityErrorCodes.Forbidden);
        await RejectedAsync(() => RunAsync(stranger, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, Guid.CreateVersion7(), 3, Digest(3), [FirstEntry])), AuthorityErrorCodes.Forbidden);
        Assert.NotNull(Pending);

        await ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered);
        await RejectedAsync(() => ResolveAsync(v2, 2, FormVersionPublicationResolutions.Abandoned), AuthorityErrorCodes.FormPublicationNotPending); // already resolved
        await RejectedAsync(() => ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered), AuthorityErrorCodes.FormPublicationNotPending);
        Assert.Equal(2, store.PublishedVersions.Count);
    }

    /// <summary>While a publication is pending no approval is accepted and the one-step registration cannot end the fail-closed state.</summary>
    [Fact]
    public async Task ApprovalsAndOneStepRegistrationsAreRefusedWhileAPublicationIsPending()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var v2 = await PrepareAsync(2, FirstEntry, PhoneEntry);
        await RejectedAsync(() => ApproveAsync(Phone), AuthorityErrorCodes.FormPublicationPending);
        await RejectedAsync(() => RegisterOneStepAsync(2, FirstEntry, PhoneEntry), AuthorityErrorCodes.FormPublicationPending);
        await RejectedAsync(() => RegisterOneStepAsync(3, FirstEntry, PhoneEntry), AuthorityErrorCodes.FormPublicationPending);
        Assert.Empty(store.Approvals);

        // Withdrawal (a tightening) is never blocked.
        await ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered);
        var phone = await ApproveAsync(Phone);
        await PrepareAsync(3, FirstEntry, PhoneEntry);
        await RunAsync(Administrator, SecurityCommandKind.WithdrawFieldSearch, new WithdrawFieldSearchPayload(Form, Phone, "no longer needed"));
        Assert.Equal(FieldSearchApprovalStates.Withdrawn, phone.ApprovalStateName);
    }

    private static PublishedFieldEntry PhoneRelabelled => PhoneEntry with { FieldLabel = "Mobile" };

    /// <summary>
    /// C-191: v2 (Phone removed) is committed in the business and its resolution is lost. No later version (v3 bringing Phone back), no
    /// other v2 (keeping Phone) and no repeat of v2 can be staged until v2 is resolved; nothing is staged by the refusals and the barrier
    /// stays. Resolving v2 applies D-H3 (Phone invalidated), and v3 bringing Phone back never revives it. At 34a5dae the v3 prepare
    /// superseded v2: registering v3 left Phone Active at v3 (D-H3 skipped) and registering the other v2 left it effective on a manifest
    /// the business never made current.
    /// </summary>
    [Fact]
    public async Task APendingPublicationIsNeverSupersededSoACommittedRemovalTakesEffectBeforeAnyLaterVersion()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var phone = await ApproveAsync(Phone);
        var v2 = await PrepareAsync(2, FirstEntry); // committed in the business; its resolution is lost

        await RejectedAsync(() => PrepareAsync(3, FirstEntry, PhoneEntry), AuthorityErrorCodes.FormPublicationPending); // a later version
        await RejectedAsync(() => PrepareAsync(2, FirstEntry, PhoneEntry), AuthorityErrorCodes.FormPublicationPending); // another v2
        await RejectedAsync(() => RunAsync(Designer, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, v2, 2, Digest(2), [FirstEntry], "Intake")), AuthorityErrorCodes.FormPublicationPending); // v2 again
        await RejectedAsync(() => RunAsync(Administrator, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, Guid.CreateVersion7(), 3, Digest(3), [FirstEntry, PhoneEntry])), AuthorityErrorCodes.FormPublicationPending); // an administrator too
        var only = Assert.Single(store.Publications); // the refusals staged nothing
        Assert.Equal((v2, FormVersionPublicationStates.Pending), (only.BusinessFormVersionId, only.PublicationStateName));
        Assert.Equal([phone.FieldSearchApprovalId], await IneffectiveAsync()); // the barrier stays
        Assert.Single(store.PublishedVersions);

        await ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered); // recovery
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged, 1), (phone.ApprovalStateName, phone.InvalidationReasonCode, phone.ApprovedFormVersionNumber)); // D-H3

        var v3 = await PrepareAsync(3, FirstEntry, PhoneEntry); // now accepted: Phone returns
        await ResolveAsync(v3, 3, FormVersionPublicationResolutions.Registered);
        Assert.Equal((FieldSearchApprovalStates.Invalidated, 1), (phone.ApprovalStateName, phone.ApprovedFormVersionNumber)); // never revived
        Assert.Empty(await IneffectiveAsync());
        Assert.DoesNotContain(store.Approvals, a => a.FieldDefinitionId == Phone && a.ApprovalStateName == FieldSearchApprovalStates.Active);
        Assert.Equal([1, 2, 3], store.PublishedVersions.Select(v => v.FormVersionNumber).Order());
    }

    /// <summary>
    /// C-191: the barrier of a committed v2 cannot be lifted through a later version: a v3 that was refused staging cannot be abandoned
    /// (nothing of it is pending). At 34a5dae, v3 superseded v2 and abandoning v3 (its business commit failed) lifted the barrier with the
    /// Authority judging v1 while the business was at v2 - the removed field's ordinary approval searchable again (BF-1 reopened).
    /// </summary>
    [Fact]
    public async Task ARefusedLaterVersionCannotBeAbandonedToLiftTheBarrierOfACommittedRemoval()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var phone = await ApproveAsync(Phone);
        var v2 = await PrepareAsync(2, FirstEntry); // committed; resolution lost
        var v3 = Guid.CreateVersion7();
        await RejectedAsync(() => RunAsync(Designer, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, v3, 3, Digest(3), [FirstEntry, PhoneEntry])), AuthorityErrorCodes.FormPublicationPending);
        await RejectedAsync(() => ResolveAsync(v3, 3, FormVersionPublicationResolutions.Abandoned), AuthorityErrorCodes.FormPublicationNotPending);
        await RejectedAsync(() => ResolveAsync(v3, 3, FormVersionPublicationResolutions.Registered), AuthorityErrorCodes.FormPublicationNotPending);
        Assert.Equal(v2, Pending!.BusinessFormVersionId);
        Assert.Equal(FieldSearchApprovalStates.Active, phone.ApprovalStateName);
        Assert.Equal([phone.FieldSearchApprovalId], await IneffectiveAsync()); // still closed
    }

    /// <summary>
    /// C-191, retained variant: v3 brings a retained field back with a changed entry (committed, resolution lost) and v4 would remove it again.
    /// v4 cannot be staged until v3 is resolved; resolving v3 invalidates the retained approval (D-H4: changed entry), and v4 does not
    /// revive it. At 34a5dae, v4 superseded v3 and the retained approval stayed effective although the business had changed the field.
    /// </summary>
    [Fact]
    public async Task ARetainedApprovalIsJudgedAgainstEveryCommittedVersionNotOnlyTheLastOne()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        await RegisterOneStepAsync(2, FirstEntry);
        var retained = await ApproveAsync(Phone, FieldSearchApprovalScopes.RetainedField);
        var v3 = await PrepareAsync(3, FirstEntry, PhoneRelabelled); // committed; resolution lost
        await RejectedAsync(() => PrepareAsync(4, FirstEntry), AuthorityErrorCodes.FormPublicationPending);
        Assert.Equal([retained.FieldSearchApprovalId], await IneffectiveAsync());

        await ResolveAsync(v3, 3, FormVersionPublicationResolutions.Registered);
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (retained.ApprovalStateName, retained.InvalidationReasonCode)); // D-H4
        var v4 = await PrepareAsync(4, FirstEntry);
        await ResolveAsync(v4, 4, FormVersionPublicationResolutions.Registered);
        Assert.Equal(FieldSearchApprovalStates.Invalidated, retained.ApprovalStateName);
        Assert.Empty(await IneffectiveAsync());
    }

    /// <summary>Registration from the staged manifest re-checks current facts: an encryption requirement recorded meanwhile refuses it, and the form stays pending.</summary>
    [Fact]
    public async Task ResolvingAsRegisteredRevalidatesTheStagedManifestAgainstCurrentFacts()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var v2 = await PrepareAsync(2, FirstEntry, PhoneEntry);
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = Phone, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        await RejectedAsync(() => ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered), AuthorityErrorCodes.FormVersionUntrusted);
        Assert.NotNull(Pending); // still fail closed
        Assert.Single(store.PublishedVersions);

        // The prepare itself is validated the same way: a version publishing an encrypted field as Standard is refused before any business commit.
        await RejectedAsync(() => PrepareAsync(3, FirstEntry, PhoneEntry), AuthorityErrorCodes.FormVersionUntrusted);
    }

    /// <summary>D-H7 at prepare time: a version naming a field of another form is refused before the business commits it; nothing is staged.</summary>
    [Fact]
    public async Task APrepareNamingAFieldOfAnotherFormIsRefusedAndStagesNothing()
    {
        var otherForm = Guid.CreateVersion7();
        store.AddFieldFormBindingForTest(new FieldFormBinding { OrganizationId = Org, FieldDefinitionId = Phone, FormDefinitionId = otherForm, BindingSourceName = FieldFormBindingSources.Backfill });
        await RejectedAsync(() => PrepareAsync(1, FirstEntry, PhoneEntry), AuthorityErrorCodes.FieldBoundToAnotherForm);
        Assert.Empty(store.Publications);
        Assert.DoesNotContain(store.FieldFormBindings, b => b.FieldDefinitionId == First); // bindings are written only at registration
    }

    /// <summary>The in-memory store mirrors the database rule: at most one Pending publication per form.</summary>
    [Fact]
    public async Task TheStoreHoldsAtMostOnePendingPublicationPerForm()
    {
        await RegisterOneStepAsync(1, FirstEntry);
        await PrepareAsync(2, FirstEntry);
        await using var scope = await store.BeginScopeAsync(Ct);
        Assert.Throws<InvalidOperationException>(() => scope.AddFormVersionPublication(new FormVersionPublication { OrganizationId = Org, FormDefinitionId = Form, BusinessFormVersionId = Guid.CreateVersion7(), FormVersionNumber = 2, LayoutDigest = new byte[32] }));
    }
}
