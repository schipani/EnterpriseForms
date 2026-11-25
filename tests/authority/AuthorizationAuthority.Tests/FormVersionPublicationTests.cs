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
/// superseded); and resolution re-validates against current facts. C-191 (final): an abandonment needs the business publication
/// coordinator's attestation (<see cref="FormPublicationAbandonmentVerifier"/>); design permission alone, a forged, stale, replayed or
/// misbound attestation, or one whose own statement shows the version committed, leaves the publication pending. The real-stack proof (journal, both engines, open pages, snapshots, business recovery, concurrency) is the Stage B class
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

    /// <summary>C-191: the coordinator attestation key of these tests (the business attestor signs with the same key).</summary>
    private static readonly byte[] AttestationKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static readonly FormPublicationAbandonmentVerifier Verifier = FormPublicationAbandonmentVerifier.Create(Convert.ToBase64String(AttestationKey), []);

    private readonly InMemoryAuthorityStore store = new();

    /// <summary>The verifier the planner uses (C-191 tests replace it to show an unconfigured deployment).</summary>
    private FormPublicationAbandonmentVerifier verifier = Verifier;

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
            plan = await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, Enabled, DateTimeOffset.UtcNow, verifier);
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

    /// <summary>Resolves as the designer; an abandonment carries a valid coordinator attestation for the pending publication (business at number - 1).</summary>
    private Task<CommandPlan> ResolveAsync(Guid businessVersion, int number, string resolution, string? digest = null) =>
        RunAsync(Designer, SecurityCommandKind.ResolveFormVersionPublication, new ResolveFormVersionPublicationPayload(Form, businessVersion, number, digest ?? Digest(number), resolution,
            resolution == FormVersionPublicationResolutions.Abandoned ? Attest(Pending?.FormVersionPublicationId ?? Guid.CreateVersion7(), businessVersion, number, digest ?? Digest(number), number - 1) : null));

    /// <summary>C-191: an abandonment of the pending publication with exactly this attestation (null: none).</summary>
    private Task<CommandPlan> AbandonAsync(Guid user, Guid businessVersion, int number, FormPublicationAbandonmentAttestation? attestation) =>
        RunAsync(user, SecurityCommandKind.ResolveFormVersionPublication, new ResolveFormVersionPublicationPayload(Form, businessVersion, number, Digest(number), FormVersionPublicationResolutions.Abandoned, attestation));

    /// <summary>C-191: what the business publication coordinator sends (signed with <paramref name="key"/>, the coordinator key by default).</summary>
    private static FormPublicationAbandonmentAttestation Attest(Guid publication, Guid businessVersion, int number, string digest, int businessLatest, DateTimeOffset? issuedAt = null, byte[]? key = null)
    {
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var mac = System.Security.Cryptography.HMACSHA256.HashData(key ?? AttestationKey, FormPublicationAbandonmentVerifier.Message(Org, Form, publication, businessVersion, number, digest, businessLatest, issued));
        return new FormPublicationAbandonmentAttestation(publication, businessLatest, issued, Convert.ToHexStringLower(mac));
    }

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
        await RejectedAsync(() => AbandonAsync(stranger, v2, 2, Attest(Pending!.FormVersionPublicationId, v2, 2, Digest(2), 1)), AuthorityErrorCodes.Forbidden); // a valid attestation confers no permission
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

    /// <summary>
    /// C-191 (final): design permission alone never abandons. With v2 (Phone removed) staged, an abandonment without the coordinator's
    /// attestation - by the designer or by an organization administrator - and one signed with any other key (e.g. a caller holding only
    /// the business workload credential) or malformed is refused with FORM_PUBLICATION_ABANDONMENT_UNVERIFIED; the publication stays
    /// pending and the barrier stays.
    /// </summary>
    [Fact]
    public async Task AnAbandonmentNeedsThePublicationCoordinatorsAttestationNotDesignPermission()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var phone = await ApproveAsync(Phone);
        var v2 = await PrepareAsync(2, FirstEntry);
        var publication = Pending!.FormVersionPublicationId;

        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, null), AuthorityErrorCodes.FormPublicationAbandonmentUnverified);
        await RejectedAsync(() => AbandonAsync(Administrator, v2, 2, null), AuthorityErrorCodes.FormPublicationAbandonmentUnverified);
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), 1, key: System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))), AuthorityErrorCodes.FormPublicationAbandonmentUnverified); // another key
        var valid = Attest(publication, v2, 2, Digest(2), 1);
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, valid with { AttestationMacHex = "zz" + valid.AttestationMacHex[2..] }), AuthorityErrorCodes.FormPublicationAbandonmentUnverified); // not hexadecimal
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, valid with { AttestationMacHex = valid.AttestationMacHex[..62] }), AuthorityErrorCodes.FormPublicationAbandonmentUnverified); // truncated
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, valid with { BusinessLatestFormVersionNumber = 0 }), AuthorityErrorCodes.FormPublicationAbandonmentUnverified); // altered after signing

        Assert.Equal((v2, FormVersionPublicationStates.Pending), (Pending!.BusinessFormVersionId, Pending.PublicationStateName));
        Assert.Equal([phone.FieldSearchApprovalId], await IneffectiveAsync()); // still closed
        Assert.Single(store.PublishedVersions);

        await AbandonAsync(Designer, v2, 2, valid); // the coordinator's attestation: v2 was never committed
        Assert.Equal(FormVersionPublicationStates.Abandoned, store.Publications.Single().PublicationStateName);
        Assert.Empty(await IneffectiveAsync());
    }

    /// <summary>
    /// C-191 (final): a committed version cannot be abandoned. v2 (Phone removed) is committed and its resolution lost. A correctly signed
    /// attestation whose own statement shows a business version numbered at or above v2 does not establish non-commitment; an attestation
    /// for another publication, for another version, or outside its issue window is refused; so is every abandonment while no coordinator
    /// key is configured. The publication stays pending until it is registered, and then D-H3 invalidates Phone.
    /// </summary>
    [Fact]
    public async Task ACommittedVersionCannotBeAbandonedSoItsRemovalAlwaysTakesEffect()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var phone = await ApproveAsync(Phone);
        var v2 = await PrepareAsync(2, FirstEntry); // committed in the business; resolution lost
        var publication = Pending!.FormVersionPublicationId;
        var unverified = AuthorityErrorCodes.FormPublicationAbandonmentUnverified;

        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), 2)), unverified); // the business holds v2
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), 7)), unverified); // and later versions
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), -1)), unverified);
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(Guid.CreateVersion7(), v2, 2, Digest(2), 1)), unverified); // another publication
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, Guid.CreateVersion7(), 2, Digest(2), 1)), unverified); // signed for another version
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(7), 1)), unverified); // signed for another layout
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), 1, DateTimeOffset.UtcNow.AddMinutes(-6))), unverified); // stale
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), 1, DateTimeOffset.UtcNow.AddMinutes(6))), unverified); // not yet issued
        verifier = FormPublicationAbandonmentVerifier.Unconfigured;
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, Attest(publication, v2, 2, Digest(2), 1)), unverified); // no key configured: never
        verifier = Verifier;
        await RejectedAsync(() => RunAsync(Designer, SecurityCommandKind.ResolveFormVersionPublication, new ResolveFormVersionPublicationPayload(Form, v2, 2, Digest(2), FormVersionPublicationResolutions.Registered, Attest(publication, v2, 2, Digest(2), 1))), AuthorityErrorCodes.InvalidCommand); // a registration carries none

        Assert.Equal((v2, FormVersionPublicationStates.Pending), (Pending!.BusinessFormVersionId, Pending.PublicationStateName));
        Assert.Equal([phone.FieldSearchApprovalId], await IneffectiveAsync());

        await ResolveAsync(v2, 2, FormVersionPublicationResolutions.Registered); // recovery finds v2 committed
        Assert.Equal((FieldSearchApprovalStates.Invalidated, FieldSearchInvalidationReasons.FormVersionChanged), (phone.ApprovalStateName, phone.InvalidationReasonCode)); // D-H3
        Assert.Empty(await IneffectiveAsync());
    }

    /// <summary>
    /// C-191 (final): an attestation is bound to the Authority's publication, so it cannot be replayed onto a later prepare of the same
    /// business version: v2 abandoned with a valid attestation, the same v2 staged again, the old attestation refused, a fresh one accepted.
    /// </summary>
    [Fact]
    public async Task AnAbandonmentAttestationCannotBeReplayedOntoALaterPublication()
    {
        await RegisterOneStepAsync(1, FirstEntry, PhoneEntry);
        var v2 = Guid.CreateVersion7();
        await RunAsync(Designer, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, v2, 2, Digest(2), [FirstEntry], "Intake"));
        var first = Attest(Pending!.FormVersionPublicationId, v2, 2, Digest(2), 1);
        await AbandonAsync(Designer, v2, 2, first);

        await RunAsync(Designer, SecurityCommandKind.PrepareFormVersionPublication, new PrepareFormVersionPublicationPayload(Form, v2, 2, Digest(2), [FirstEntry], "Intake"));
        await RejectedAsync(() => AbandonAsync(Designer, v2, 2, first), AuthorityErrorCodes.FormPublicationAbandonmentUnverified);
        Assert.NotNull(Pending);
        await AbandonAsync(Designer, v2, 2, Attest(Pending!.FormVersionPublicationId, v2, 2, Digest(2), 1));
        Assert.Null(Pending);
    }

    /// <summary>C-191: the attested message is pinned (the business attestor's test pins the same vector), and the key is validated at startup.</summary>
    [Fact]
    public void TheAttestedMessageIsPinnedAndTheKeyIsValidatedAtStartup()
    {
        var message = FormPublicationAbandonmentVerifier.Message(Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"), Guid.Parse("00000000-0000-0000-0000-000000000004"), 2, string.Concat(Enumerable.Repeat("AB", 32)), 1, 1795000000);
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Assert.Equal("3d15f20a6da18c84883ad6410b245b538aed47b2bffb796254d9e8e4732d3157", Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(key, message)));

        Assert.False(FormPublicationAbandonmentVerifier.Create(null, []).IsConfigured);
        Assert.False(FormPublicationAbandonmentVerifier.Create(" ", []).IsConfigured);
        Assert.True(FormPublicationAbandonmentVerifier.Create(Convert.ToBase64String(key), ["d29ya2xvYWQ="]).IsConfigured);
        Assert.Throws<InvalidOperationException>(() => FormPublicationAbandonmentVerifier.Create("not base64!", []));
        Assert.Throws<InvalidOperationException>(() => FormPublicationAbandonmentVerifier.Create(Convert.ToBase64String(new byte[31]), []));
        Assert.Throws<InvalidOperationException>(() => FormPublicationAbandonmentVerifier.Create(Convert.ToBase64String(key), [null, Convert.ToBase64String(key)])); // equal to another credential
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
