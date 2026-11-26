using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Sharing;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>Command authorization is computed from the authority's own desired state; forged business rows cannot influence it (A1, A6, CONS-10/12/29).</summary>
public sealed class CommandPlannerTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();
    private static readonly Guid OwnerUser = Guid.CreateVersion7();
    private static readonly Guid AttackerUser = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Item = Guid.CreateVersion7();

    [Fact]
    public async Task RegisterRecordRequiresFormUserAndRegisteredForm()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormDefinition, Form));
        var graph = Graph(Edge(ResourceTypeNames.FormDefinition, Form, "form_user", ResourceTypeNames.User, OwnerUser));
        var request = Command(SecurityCommandKind.RegisterRecord, ResourceKind.FormRecord, Record, new { form_record_id = Record, form_definition_id = Form, owner_team_id = (Guid?)null });

        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), request, graph, CancellationToken.None);
        Assert.Contains(plan.AddEdges, e => e.Relation == "owner" && e.SubjectId == OwnerUser);
        Assert.Single(plan.RegisterResources, r => r.ResourceTypeName == ResourceTypeNames.FormRecord);

        var denied = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.Forbidden, denied.ErrorCode);

        var unregistered = Command(SecurityCommandKind.RegisterRecord, ResourceKind.FormRecord, Record, new { form_record_id = Record, form_definition_id = Guid.CreateVersion7(), owner_team_id = (Guid?)null });
        var notRegistered = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), unregistered, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, notRegistered.ErrorCode);
    }

    // G27: HumanCommandStepUpGate for the development-only synthetic step-up path.
    private static readonly HumanCommandStepUpGate SyntheticHumanGate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");

    [Fact]
    public async Task CreateShareIsBoundedByOwnershipAndAcceptedManifest()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormRecord, Record));
        scope.Bindings.Add(new IdentityBinding { OrganizationId = Org, AuthorityUserId = AttackerUser, IdentityBindingEnabled = true });
        var commitId = Guid.CreateVersion7();
        scope.Heads[Record] = new RecordContentHead { OrganizationId = Org, FormRecordId = Record, CommittedContentRevision = 1, CurrentRecordManifestCommitId = commitId };
        scope.Leaves[commitId] = [new RecordManifestLeaf { ManifestItemKindCode = 3, ManifestItemId = Item }];
        var graph = Graph(Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, OwnerUser));
        var shareId = Guid.CreateVersion7();
        // G27: Include step_up_evidence for human commands.
        var request = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = shareId, form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { Item }, step_up_evidence = HumanCommandStepUpGate.SyntheticStepUpEvidence });

        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), request, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate);
        Assert.Contains(plan.AddEdges, e => e.ResourceType == ResourceTypeNames.FieldItem && e.ResourceId == Item && e.SubjectId == shareId);
        Assert.Contains(plan.AddEdges, e => e.ResourceType == ResourceTypeNames.RecordShare && e.Relation == "reader" && e.SubjectId == AttackerUser);

        // A non-owner without a manager share cannot share (no delegation path).
        var denied = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate));
        Assert.Equal(AuthorityErrorCodes.Forbidden, denied.ErrorCode);

        // Items outside the accepted manifest are rejected: no future/unknown items (MRK-30).
        var unknownItem = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = Guid.CreateVersion7(), form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { Guid.CreateVersion7() }, step_up_evidence = HumanCommandStepUpGate.SyntheticStepUpEvidence });
        var invalid = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), unknownItem, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, invalid.ErrorCode);
    }

    [Fact]
    public async Task ManagerCanDelegateManagementOnlyWithinTheNamedParentScope()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormRecord, Record));
        scope.Bindings.Add(new IdentityBinding { OrganizationId = Org, AuthorityUserId = AttackerUser, IdentityBindingEnabled = true });
        var commitId = Guid.CreateVersion7();
        var otherItem = Guid.CreateVersion7();
        scope.Heads[Record] = new RecordContentHead { OrganizationId = Org, FormRecordId = Record, CommittedContentRevision = 1, CurrentRecordManifestCommitId = commitId };
        scope.Leaves[commitId] = [new RecordManifestLeaf { ManifestItemKindCode = 3, ManifestItemId = Item }, new RecordManifestLeaf { ManifestItemKindCode = 3, ManifestItemId = otherItem }];
        var managerUser = Guid.CreateVersion7();
        var managerShare = Guid.CreateVersion7();
        scope.Resources.Add(Resource(ResourceTypeNames.RecordShare, managerShare, Record));
        scope.ShareMetadatas.Add(managerShare, new ShareResourceMetadata { OrganizationId = Org, RecordShareId = managerShare });
        var graph = Graph(
            Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, OwnerUser),
            Edge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, managerShare),
            Edge(ResourceTypeNames.RecordShare, managerShare, "manager", ResourceTypeNames.User, managerUser),
            Edge(ResourceTypeNames.FieldItem, Item, "shared_via", ResourceTypeNames.RecordShare, managerShare));

        var withinScope = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
            new CreateSharePayload(Guid.CreateVersion7(), Record, AttackerUser, null, RecipientRoles.Manager, [Item], HumanCommandStepUpGate.SyntheticStepUpEvidence, managerShare));
        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(managerUser), withinScope, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate);
        Assert.Contains(plan.AddEdges, e => e.ResourceId == Item);

        Assert.Contains(plan.AddEdges, e => e.ResourceType == ResourceTypeNames.RecordShare && e.Relation == "manager" && e.SubjectId == AttackerUser);
        Assert.Equal(2, plan.ShareMetadata!.DelegationDepth);
        var beyondScope = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
            new CreateSharePayload(Guid.CreateVersion7(), Record, AttackerUser, null, RecipientRoles.Manager, [otherItem], HumanCommandStepUpGate.SyntheticStepUpEvidence, managerShare));
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(managerUser), beyondScope, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
    }

    [Fact]
    public async Task TransferOwnershipRequiresOwnerOrSystemAdministratorAndExactlyOneNewOwner()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormRecord, Record));
        scope.Bindings.Add(new IdentityBinding { OrganizationId = Org, AuthorityUserId = AttackerUser, IdentityBindingEnabled = true });
        var admin = Guid.CreateVersion7();
        var graph = Graph(
            Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, OwnerUser),
            Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.SystemAdministrator, ResourceTypeNames.User, admin));
        // G27: Include step_up_evidence for human commands.
        var request = Command(SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new { form_record_id = Record, new_owner_user_id = AttackerUser, new_owner_team_id = (Guid?)null, step_up_evidence = HumanCommandStepUpGate.SyntheticStepUpEvidence });

        var byOwner = await CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), request, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate);
        Assert.Single(byOwner.RemoveEdges);
        Assert.Single(byOwner.AddEdges, e => e.SubjectId == AttackerUser);
        var byAdmin = await CommandPlanner.PlanAsync(scope, Org, Actor(admin), request, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate);
        Assert.Single(byAdmin.AddEdges);
        await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate));

        var both = Command(SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new { form_record_id = Record, new_owner_user_id = AttackerUser, new_owner_team_id = Guid.CreateVersion7(), step_up_evidence = HumanCommandStepUpGate.SyntheticStepUpEvidence });
        await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), both, graph, CancellationToken.None, humanCommandGate: SyntheticHumanGate));
    }

    [Fact]
    public async Task UnknownPayloadMembersAndUnknownCommandKindsAreRejected()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.Team, Form));
        var graph = Graph(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, OwnerUser));
        var massAssignment = Command(SecurityCommandKind.RegisterTeam, ResourceKind.Team, Form, new { team_id = Form, owner_user_id = AttackerUser });
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), massAssignment, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);

        var unknown = Command((SecurityCommandKind)int.MaxValue, ResourceKind.FormRecord, Org, new { });
        var ex2 = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), unknown, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.CommandKindNotImplemented, ex2.ErrorCode);

        // Owner message 7: GrantSystemRole is implemented, and an organization owner cannot use it (no self-elevation).
        var selfElevation = Command(SecurityCommandKind.GrantSystemRole, ResourceKind.Organization, Org, new { authority_user_id = OwnerUser, role_name = "system_administrator", reason = "none exists" });
        var ex3 = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), selfElevation, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.Forbidden, ex3.ErrorCode);
    }

    [Fact]
    public void TeamOwnedRecordsConveyOwnershipOnlyToCurrentMembers()
    {
        var team = Guid.CreateVersion7();
        var graph = Graph(
            Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.Team, team, "participant"),
            Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, OwnerUser));
        Assert.True(graph.IsRecordOwner(OwnerUser, Record));
        Assert.False(graph.IsRecordOwner(AttackerUser, Record));
    }

    [Fact]
    public async Task UpdateFormUsersIsLimitedToFormUserEdgesAndToDesignersOrAdministrators()
    {
        var designer = Guid.CreateVersion7();
        var administrator = Guid.CreateVersion7();
        var existingUser = Guid.CreateVersion7();
        var newUser = Guid.CreateVersion7();
        var newTeam = Guid.CreateVersion7();
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormDefinition, Form));
        scope.Resources.Add(Resource(ResourceTypeNames.Team, newTeam));
        scope.Bindings.Add(new IdentityBinding { OrganizationId = Org, AuthorityUserId = newUser, IdentityBindingEnabled = true });
        var graph = Graph(
            Edge(ResourceTypeNames.FormDefinition, Form, "designer", ResourceTypeNames.User, designer),
            Edge(ResourceTypeNames.FormDefinition, Form, "form_user", ResourceTypeNames.User, existingUser),
            Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, administrator));
        var request = Command(SecurityCommandKind.UpdateFormUsers, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, grant_user_ids = new[] { newUser }, grant_team_ids = new[] { newTeam }, revoke_user_ids = new[] { existingUser }, revoke_team_ids = Array.Empty<Guid>() });

        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(designer), request, graph, CancellationToken.None);
        // Only form_user edges change: no designer, owner, share or organization edge is touched, so no record access is granted or removed.
        Assert.All(plan.AddEdges, e => Assert.True(e.ResourceType == ResourceTypeNames.FormDefinition && e.Relation == "form_user"));
        Assert.Contains(plan.AddEdges, e => e.SubjectType == ResourceTypeNames.User && e.SubjectId == newUser && e.SubjectRelation.Length == 0);
        Assert.Contains(plan.AddEdges, e => e.SubjectType == ResourceTypeNames.Team && e.SubjectId == newTeam && e.SubjectRelation == "participant");
        var removed = Assert.Single(plan.RemoveEdges);
        Assert.Equal("form_user", removed.RelationshipName);
        Assert.Equal(existingUser, removed.SubjectEntityId);
        Assert.Empty(plan.RegisterResources);

        // An organization administrator may manage form users without being a designer; anyone else may not.
        await CommandPlanner.PlanAsync(scope, Org, Actor(administrator), request, graph, CancellationToken.None);
        var denied = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(existingUser), request, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.Forbidden, denied.ErrorCode);
        var attacker = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.Forbidden, attacker.ErrorCode);
    }

    [Fact]
    public async Task PublishFormSecurityRevisionIsDesignerOnlyRegistersARequirementAndNeverGrantsOrWeakens()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormDefinition, Form));
        var designer = Guid.CreateVersion7();
        var field = Guid.CreateVersion7();
        var graph = Graph(Edge(ResourceTypeNames.FormDefinition, Form, "designer", ResourceTypeNames.User, designer), Edge(ResourceTypeNames.FormDefinition, Form, "form_user", ResourceTypeNames.User, OwnerUser));
        var request = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2, field_classification = "Confidential" });

        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(designer), request, graph, CancellationToken.None);
        Assert.Empty(plan.AddEdges);
        Assert.Empty(plan.RemoveEdges);
        var resource = Assert.Single(plan.RegisterResources);
        Assert.Equal(ResourceTypeNames.FieldProtectionRequirement, resource.ResourceTypeName);
        Assert.Equal(field, resource.ResourceEntityId);
        Assert.Equal(Form, resource.ParentResourceEntityId);

        // A form user (clerk) or an unrelated user cannot publish a requirement.
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), request, graph, CancellationToken.None))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None))).ErrorCode);

        // Weakening is not a command; an unregistered form and a duplicate requirement are refused.
        // Weakening through the designer path is refused (a designer is never an administrator); an unregistered form is refused;
        // re-publishing an active requirement re-activates it (records the raised classification) instead of registering twice.
        var weaken = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, required_protection_mode = "Standard", protection_policy_revision = 3, field_classification = "Internal" });
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), weaken, graph, CancellationToken.None))).ErrorCode);
        var unknownForm = Guid.CreateVersion7();
        var unregistered = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, unknownForm, new { form_definition_id = unknownForm, field_definition_id = field, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2, field_classification = "Confidential" });
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), unregistered, graph, CancellationToken.None))).ErrorCode);
        var existing = Resource(ResourceTypeNames.FieldProtectionRequirement, field);
        scope.Resources.Add(existing);
        var republished = await CommandPlanner.PlanAsync(scope, Org, Actor(designer), request, graph, CancellationToken.None);
        Assert.Empty(republished.RegisterResources);
        Assert.Same(existing, Assert.Single(republished.ReactivateResources));
        var missingClassification = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = Guid.CreateVersion7(), required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2 });
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), missingClassification, graph, CancellationToken.None))).ErrorCode);
    }

    [Fact]
    public async Task EncryptionDowngradeNeedsPolicyAdministratorAndForSensitiveFieldsADistinctDeclassifier()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormDefinition, Form));
        var designer = Guid.CreateVersion7();
        var adminA = Guid.CreateVersion7();
        var adminB = Guid.CreateVersion7();
        var field = Guid.CreateVersion7();
        var graph = Graph(
            Edge(ResourceTypeNames.FormDefinition, Form, "designer", ResourceTypeNames.User, designer),
            Edge(ResourceTypeNames.Organization, Org, "organization_owner", ResourceTypeNames.User, adminA),
            Edge(ResourceTypeNames.Organization, Org, "system_administrator", ResourceTypeNames.User, adminB));
        var actorA = Actor(adminA);
        var actorB = Actor(adminB);
        var actorA2 = Actor(adminA); // the same user through a second identity binding
        scope.Bindings.AddRange([actorA.Binding, actorB.Binding, actorA2.Binding]);

        // The field is encrypted as Confidential according to the authority (publication command recorded).
        var publication = new SecurityCommand { SecurityCommandId = Guid.CreateVersion7(), OrganizationId = Org, CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision, CommandStateName = SecurityCommandStates.Effective, CommandPayloadJson = "{\"field_classification\":\"Confidential\",\"field_definition_id\":\"" + field.ToString("D") + "\",\"form_definition_id\":\"" + Form.ToString("D") + "\",\"protection_policy_revision\":2,\"required_protection_mode\":\"ApplicationEncrypted\"}", CommandCreatedOn = DateTimeOffset.UtcNow };
        scope.Commands[publication.SecurityCommandId] = publication;
        var requirement = Resource(ResourceTypeNames.FieldProtectionRequirement, field);
        requirement.RegisteredBySecurityCommandId = publication.SecurityCommandId;
        requirement.ParentResourceEntityId = Form;
        scope.Resources.Add(requirement);

        var downgrade = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, required_protection_mode = "Standard", protection_policy_revision = 3, field_classification = "Internal" });
        var reclassify = Command(SecurityCommandKind.ReclassifyField, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, target_classification = "Internal", approval_reference = "GOV-2026-017" });
        var policy = Command(SecurityCommandKind.UpdateMandatoryPolicy, ResourceKind.Organization, Org, new { minimum_field_classification = "Internal", plaintext_internal_fields_permitted = true, plaintext_downgrade_permitted = true });

        // No authority-held policy: nothing may be released or reclassified. A designer may not set the policy.
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorA, downgrade, graph, CancellationToken.None))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorA, reclassify, graph, CancellationToken.None))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), policy, graph, CancellationToken.None))).ErrorCode);

        var policyPlan = await CommandPlanner.PlanAsync(scope, Org, actorA, policy, graph, CancellationToken.None);
        Assert.NotNull(policyPlan.ProtectionPolicy);
        Assert.True(policyPlan.ProtectionPolicyIsNew);
        Assert.True(policyPlan.ProtectionPolicy!.PlaintextDowngradePermitted);
        scope.Policy = policyPlan.ProtectionPolicy;

        // A designer can neither release nor declassify; a Confidential field cannot be released without a declassification.
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), downgrade, graph, CancellationToken.None))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), reclassify, graph, CancellationToken.None))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorA, downgrade, graph, CancellationToken.None))).ErrorCode);

        // Declassification by admin A (approval reference required), then release by the same admin is refused, by admin B accepted.
        var noApproval = Command(SecurityCommandKind.ReclassifyField, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, target_classification = "Internal", approval_reference = "" });
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorA, noApproval, graph, CancellationToken.None))).ErrorCode);
        var reclassified = await CommandPlanner.PlanAsync(scope, Org, actorA, reclassify, graph, CancellationToken.None);
        var pending = Assert.Single(reclassified.RegisterResources);
        Assert.Equal(ResourceTypeNames.FieldReclassification, pending.ResourceTypeName);
        var declassification = new SecurityCommand { SecurityCommandId = Guid.CreateVersion7(), OrganizationId = Org, ActorIdentityBindingId = actorA.Binding.IdentityBindingId, CommandKindName = SecurityCommandKinds.ReclassifyField, CommandStateName = SecurityCommandStates.Effective, CommandPayloadJson = reclassified.NormalizedPayloadJson, CommandCreatedOn = DateTimeOffset.UtcNow };
        scope.Commands[declassification.SecurityCommandId] = declassification;
        pending.RegisteredBySecurityCommandId = declassification.SecurityCommandId;
        scope.Resources.Add(pending);

        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorA, downgrade, graph, CancellationToken.None))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorA2, downgrade, graph, CancellationToken.None))).ErrorCode); // same user, other binding
        var released = await CommandPlanner.PlanAsync(scope, Org, actorB, downgrade, graph, CancellationToken.None);
        Assert.Empty(released.AddEdges);
        Assert.Contains(released.RevokeResources, r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement);
        Assert.Contains(released.RevokeResources, r => r.ResourceTypeName == ResourceTypeNames.FieldReclassification);
        Assert.Single(released.RegisterResources, r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRelease);
        // The plan mutates the tracked resources in the (uncommitted) scope; undo as a rolled-back transaction would.
        requirement.ResourceLifecycleState = ResourceLifecycleStates.Active;
        pending.ResourceLifecycleState = ResourceLifecycleStates.Active;

        // A stale declassification (older than 24 h) is refused; a target that does not match the declassification is refused.
        declassification.CommandCreatedOn = DateTimeOffset.UtcNow.AddHours(-25);
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorB, downgrade, graph, CancellationToken.None))).ErrorCode);
        declassification.CommandCreatedOn = DateTimeOffset.UtcNow;
        var asPublic = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, required_protection_mode = "Standard", protection_policy_revision = 3, field_classification = "Public" });
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorB, asPublic, graph, CancellationToken.None))).ErrorCode); // below the policy floor (Internal)
        scope.Policy!.MinimumFieldClassification = "Public";
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorB, asPublic, graph, CancellationToken.None))).ErrorCode);

        // Policy withdrawn: the release is refused even with a valid declassification.
        scope.Policy!.PlaintextDowngradePermitted = false;
        Assert.Equal(AuthorityErrorCodes.Forbidden, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, actorB, downgrade, graph, CancellationToken.None))).ErrorCode);
    }

    [Fact]
    public async Task UpdateFormUsersRejectsEmptyContradictoryUnknownAndAlreadyAppliedChanges()
    {
        var designer = Guid.CreateVersion7();
        var existingUser = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.FormDefinition, Form));
        scope.Bindings.Add(new IdentityBinding { OrganizationId = Org, AuthorityUserId = existingUser, IdentityBindingEnabled = true });
        var graph = Graph(
            Edge(ResourceTypeNames.FormDefinition, Form, "designer", ResourceTypeNames.User, designer),
            Edge(ResourceTypeNames.FormDefinition, Form, "form_user", ResourceTypeNames.User, existingUser));
        var none = Array.Empty<Guid>();

        async Task<string> Rejected(object payload, Guid? resourceId = null, ResourceKind kind = ResourceKind.FormDefinition) =>
            (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), Command(SecurityCommandKind.UpdateFormUsers, kind, resourceId ?? Form, payload), graph, CancellationToken.None))).ErrorCode;

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await Rejected(new { form_definition_id = Form, grant_user_ids = none, grant_team_ids = none, revoke_user_ids = none, revoke_team_ids = none }));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await Rejected(new { form_definition_id = Form, grant_user_ids = new[] { existingUser }, grant_team_ids = none, revoke_user_ids = new[] { existingUser }, revoke_team_ids = none }));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await Rejected(new { form_definition_id = Form, grant_user_ids = new[] { existingUser }, grant_team_ids = none, revoke_user_ids = none, revoke_team_ids = none })); // already permitted
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await Rejected(new { form_definition_id = Form, grant_user_ids = none, grant_team_ids = none, revoke_user_ids = new[] { stranger }, revoke_team_ids = none })); // not currently permitted
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await Rejected(new { form_definition_id = Form, grant_user_ids = new[] { stranger }, grant_team_ids = none, revoke_user_ids = none, revoke_team_ids = none })); // not a principal of the organization
        var unknownForm = Guid.CreateVersion7();
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, await Rejected(new { form_definition_id = unknownForm, grant_user_ids = new[] { existingUser }, grant_team_ids = none, revoke_user_ids = none, revoke_team_ids = none }, unknownForm));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await Rejected(new { form_definition_id = Form, grant_user_ids = none, grant_team_ids = none, revoke_user_ids = new[] { existingUser }, revoke_team_ids = none }, Record)); // resource reference must match the payload
    }

    [Fact]
    public async Task OwnerCreatesARootShareAndCannotNameAParent()
    {
        var fixture = new SharePlanFixture();
        var expiry = SharePlanFixture.At.AddHours(2);
        var plan = await fixture.PlanAsync(null, user: OwnerUser, expiresOn: expiry);
        var metadata = Assert.IsType<ShareResourceMetadata>(plan.ShareMetadata);
        Assert.Null(metadata.ParentRecordShareId);
        Assert.Equal(1, metadata.DelegationDepth);
        Assert.Equal(expiry, metadata.ShareExpiresOn);
        using var payload = JsonDocument.Parse(plan.NormalizedPayloadJson);
        Assert.Equal(1, payload.RootElement.GetProperty("delegation_depth").GetInt32());
        Assert.Equal(expiry, payload.RootElement.GetProperty("share_expires_on").GetDateTimeOffset());
        var refusal = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(Guid.NewGuid(), user: OwnerUser));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refusal.ErrorCode);
    }

    [Fact]
    public async Task ManagerCanUseNamedParentButCannotCombineTwoGrants()
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item);
        fixture.AddGrant(fixture.OtherItem);
        var plan = await fixture.PlanAsync(parent.RecordShareId);
        var metadata = Assert.IsType<ShareResourceMetadata>(plan.ShareMetadata);
        Assert.Equal(parent.RecordShareId, metadata.ParentRecordShareId);
        Assert.Equal(2, metadata.DelegationDepth);

        var refusal = await Assert.ThrowsAsync<AuthorityException>(() =>
            fixture.PlanAsync(parent.RecordShareId, items: [Item, fixture.OtherItem]));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refusal.ErrorCode);
    }

    [Fact]
    public async Task ManagerMustNameAParentTheyHold()
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item);
        var missing = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(null));
        var anotherActor = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId, user: Guid.NewGuid()));
        Assert.Equal(AuthorityErrorCodes.Forbidden, missing.ErrorCode);
        Assert.Equal(AuthorityErrorCodes.Forbidden, anotherActor.ErrorCode);
    }

    [Fact]
    public async Task UnrelatedActorCannotDistinguishAnExistingParentFromAnUnknownId()
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item);
        var unrelated = Guid.NewGuid();
        var denied = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId, user: unrelated));
        var unknown = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(Guid.NewGuid(), user: unrelated));
        Assert.Equal(AuthorityErrorCodes.Forbidden, denied.ErrorCode);
        Assert.Equal(denied.ErrorCode, unknown.ErrorCode);
        Assert.Equal(denied.Message, unknown.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrossTenantAndCrossRecordParentsAreRefused(bool crossTenant)
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item, record: crossTenant ? Record : Guid.NewGuid());
        if (crossTenant)
        {
            parent.OrganizationId = Guid.NewGuid();
        }

        var refusal = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refusal.ErrorCode);
    }

    [Fact]
    public async Task UnresolvedLegacyParentIsRefused()
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item);
        parent.LegacyLineageUnresolved = true;
        var refusal = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refusal.ErrorCode);
    }

    [Fact]
    public async Task OrganizationDepthAndDeepestManagerLimitsAreIndependent()
    {
        var fixture = new SharePlanFixture();
        var root = fixture.AddGrant(Item);
        fixture.Scope.Policy = new OrganizationProtectionPolicy { OrganizationId = Org, MaximumDelegationDepth = 1 };
        var tenantLimit = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(root.RecordShareId));
        Assert.Equal(AuthorityErrorCodes.DelegationDepthExceeded, tenantLimit.ErrorCode);

        fixture.Scope.Policy.MaximumDelegationDepth = 3;
        var depthTwo = fixture.AddGrant(Item, parent: root.RecordShareId);
        var deepestManager = await Assert.ThrowsAsync<AuthorityException>(() =>
            fixture.PlanAsync(depthTwo.RecordShareId, role: RecipientRoles.Manager));
        Assert.Equal(AuthorityErrorCodes.ManagerAtMaximumDepth, deepestManager.ErrorCode);
        var deepestReader = await fixture.PlanAsync(depthTwo.RecordShareId);
        Assert.Equal(3, deepestReader.ShareMetadata!.DelegationDepth);
    }

    [Fact]
    public async Task ManagerTeamMustStillBeActive()
    {
        var fixture = new SharePlanFixture();
        var team = Resource(ResourceTypeNames.Team, Guid.NewGuid());
        fixture.Scope.Resources.Add(team);
        var parent = fixture.AddGrant(Item, team: team.ResourceEntityId);
        var plan = await fixture.PlanAsync(parent.RecordShareId);
        Assert.Equal(2, plan.ShareMetadata!.DelegationDepth);

        team.ResourceLifecycleState = ResourceLifecycleStates.Revoked;
        var refusal = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId));
        Assert.Equal(AuthorityErrorCodes.Forbidden, refusal.ErrorCode);
    }

    [Fact]
    public async Task ExpiryIsCappedByTheEarliestAncestorAndDeniesAtItsDeadline()
    {
        var fixture = new SharePlanFixture();
        var deadline = SharePlanFixture.At.AddHours(2);
        var root = fixture.AddGrant(Item, expiry: deadline);
        var parent = fixture.AddGrant(Item, parent: root.RecordShareId, expiry: deadline.AddHours(8));
        Assert.Equal(deadline, (await fixture.PlanAsync(parent.RecordShareId, expiresOn: deadline)).ShareMetadata!.ShareExpiresOn);
        await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId, expiresOn: deadline.AddHours(1)));
        await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId));

        root.ShareExpiresOn = SharePlanFixture.At;
        var expired = await Assert.ThrowsAsync<AuthorityException>(() => fixture.PlanAsync(parent.RecordShareId, expiresOn: deadline));
        Assert.Contains("active", expired.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopeUpdatePlansOnlyTheTargetShareDiffAndKeepsTrackedMetadataUntouched()
    {
        var fixture = new SharePlanFixture();
        var target = fixture.AddGrant(Item, expiry: SharePlanFixture.At.AddMinutes(30));
        var independent = fixture.AddGrant(Item);
        var plan = await fixture.UpdateAsync(target, [fixture.OtherItem], expiry: target.ShareExpiresOn);

        Assert.Equal(1, target.ShareRevision);
        Assert.Equal(2, plan.ShareMetadataUpdate!.Value.Revision);
        Assert.Equal(target.ShareExpiresOn, plan.ShareMetadataUpdate.Value.ExpiresOn);
        Assert.Contains(plan.RemoveEdges, edge => edge.ResourceEntityId == Item && edge.SubjectEntityId == target.RecordShareId);
        Assert.DoesNotContain(plan.RemoveEdges, edge => edge.SubjectEntityId == independent.RecordShareId || edge.ResourceEntityId == independent.RecordShareId);
        Assert.Contains(plan.AddEdges, edge => edge.ResourceId == fixture.OtherItem && edge.SubjectId == target.RecordShareId);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, plan.NormalizedPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopeUpdateKeepsItsParentAndRefusesAChangeWithDependentShares()
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item);
        var independentParent = fixture.AddGrant(fixture.OtherItem);
        var child = fixture.AddGrant(Item, parent: parent.RecordShareId);
        var update = await fixture.UpdateAsync(child, [Item], parent: parent.RecordShareId, asOwner: false);
        Assert.Equal(2, update.ShareMetadataUpdate!.Value.Revision);

        var reparent = await Assert.ThrowsAsync<AuthorityException>(() =>
            fixture.UpdateAsync(child, [fixture.OtherItem], parent: independentParent.RecordShareId, asOwner: false));
        Assert.Equal(AuthorityErrorCodes.Forbidden, reparent.ErrorCode);

        var descendant = await Assert.ThrowsAsync<AuthorityException>(() => fixture.UpdateAsync(parent, [fixture.OtherItem]));
        Assert.Equal(AuthorityErrorCodes.ShareHasActiveDescendants, descendant.ErrorCode);
        Assert.Equal(1, child.ShareRevision);
        Assert.Equal(1, parent.ShareRevision);
    }

    [Fact]
    public async Task ExpiredManagerCannotDistinguishAnExistingDependentShareFromAnUnknownId()
    {
        var fixture = new SharePlanFixture();
        var parent = fixture.AddGrant(Item, expiry: SharePlanFixture.At);
        var dependent = fixture.AddGrant(Item, parent: parent.RecordShareId);
        var unknown = new ShareResourceMetadata { OrganizationId = Org, RecordShareId = Guid.NewGuid() };
        var known = await Assert.ThrowsAsync<AuthorityException>(() =>
            fixture.UpdateAsync(dependent, [Item], parent: parent.RecordShareId, asOwner: false));
        var missing = await Assert.ThrowsAsync<AuthorityException>(() =>
            fixture.UpdateAsync(unknown, [Item], parent: parent.RecordShareId, asOwner: false));
        Assert.Equal(AuthorityErrorCodes.Forbidden, known.ErrorCode);
        Assert.Equal(known.ErrorCode, missing.ErrorCode);
        Assert.Equal(known.Message, missing.Message);
    }

    private sealed class SharePlanFixture
    {
        public static readonly DateTimeOffset At = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        private readonly Guid manager = Guid.NewGuid();
        private readonly Guid recipient = Guid.NewGuid();
        private readonly List<AuthorityRelationshipEdge> edges =
            [Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, OwnerUser)];

        public SharePlanFixture()
        {
            var commitId = Guid.NewGuid();
            Scope.Resources.Add(Resource(ResourceTypeNames.FormRecord, Record, Form));
            Scope.Bindings.Add(new IdentityBinding { OrganizationId = Org, AuthorityUserId = recipient, IdentityBindingEnabled = true });
            Scope.Heads[Record] = new RecordContentHead { CurrentRecordManifestCommitId = commitId };
            Scope.Leaves[commitId] = [Leaf(Item), Leaf(OtherItem)];
        }

        public FakeScope Scope { get; } = new(Org);
        public Guid OtherItem { get; } = Guid.NewGuid();

        public ShareResourceMetadata AddGrant(Guid item, Guid? parent = null, Guid? team = null, Guid? record = null, DateTimeOffset? expiry = null)
        {
            var id = Guid.NewGuid();
            Scope.Resources.Add(Resource(ResourceTypeNames.RecordShare, id, record ?? Record));
            edges.Add(Edge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, id));
            edges.Add(Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id));
            if (team is { } teamId)
            {
                edges.Add(Edge(ResourceTypeNames.RecordShare, id, "manager", ResourceTypeNames.Team, teamId, "participant"));
                edges.Add(Edge(ResourceTypeNames.Team, teamId, "member", ResourceTypeNames.User, manager));
            }
            else
            {
                edges.Add(Edge(ResourceTypeNames.RecordShare, id, "manager", ResourceTypeNames.User, manager));
            }

            var metadata = new ShareResourceMetadata
            {
                OrganizationId = Org,
                RecordShareId = id,
                ParentRecordShareId = parent,
                DelegationDepth = parent is { } parentId ? Scope.ShareMetadatas[parentId].DelegationDepth + 1 : 1,
                ShareExpiresOn = expiry,
            };
            Scope.ShareMetadatas.Add(id, metadata);
            return metadata;
        }

        public Task<CommandPlan> PlanAsync(Guid? parent, Guid? user = null, IReadOnlyList<Guid>? items = null,
            string role = RecipientRoles.Reader, DateTimeOffset? expiresOn = null)
        {
            var payload = new CreateSharePayload(Guid.NewGuid(), Record, recipient, null, role, items ?? [Item],
                HumanCommandStepUpGate.SyntheticStepUpEvidence, parent, expiresOn);
            var request = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, payload);
            return CommandPlanner.PlanAsync(Scope, Org, Actor(user ?? manager), request, new DesiredStateGraph(edges),
                TestContext.Current.CancellationToken, now: At, humanCommandGate: SyntheticHumanGate);
        }

        public Task<CommandPlan> UpdateAsync(ShareResourceMetadata target, IReadOnlyList<Guid> items,
            Guid? parent = null, DateTimeOffset? expiry = null, bool asOwner = true)
        {
            var payload = new UpdateShareScopePayload(target.RecordShareId, Record, RecipientRoles.Reader, items,
                target.ShareRevision, HumanCommandStepUpGate.SyntheticStepUpEvidence, parent, expiry);
            var request = Command(SecurityCommandKind.UpdateShareScope, ResourceKind.FormRecord, Record, payload);
            return CommandPlanner.PlanAsync(Scope, Org, Actor(asOwner ? OwnerUser : manager), request,
                new DesiredStateGraph(edges), TestContext.Current.CancellationToken, now: At, humanCommandGate: SyntheticHumanGate);
        }

        private static RecordManifestLeaf Leaf(Guid item) => new()
        {
            ManifestItemId = item,
            ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue,
        };
    }

    private static SecurityCommandRequest Command(SecurityCommandKind kind, ResourceKind resourceKind, Guid resourceId, object payload) =>
        new(Org, kind, new ResourceRef(resourceKind, resourceId), 0, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));

    private static BoundActor Actor(Guid userId) => new(new VerifiedActor("https://idp", userId.ToString("N"), "test"), new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = userId, IdentityBindingEnabled = true });

    private static DesiredStateGraph Graph(params AuthorityRelationshipEdge[] edges) => new(edges);

    private static AuthorityRelationshipEdge Edge(string resourceType, Guid resourceId, string relation, string subjectType, Guid subjectId, string subjectRelation = "") => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = resourceType,
        ResourceEntityId = resourceId,
        RelationshipName = relation,
        SubjectTypeName = subjectType,
        SubjectEntityId = subjectId,
        SubjectRelationName = subjectRelation,
    };

    private static AuthorityResource Resource(string type, Guid id) => new() { OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = id, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = Guid.CreateVersion7() };

    private static AuthorityResource Resource(string type, Guid id, Guid parentId) => new() { OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = id, ParentResourceEntityId = parentId, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = Guid.CreateVersion7() };

    /// <summary>In-memory scope: only the lookups the planner needs. Cross-organization lookups return nothing (tenant binding).</summary>
    private sealed class FakeScope(Guid organizationId) : IAuthorityScope
    {
        // E3-M2e ports (not used by these tests).
        public Task<List<ContentDataKeyBinding>> FindDataKeyBindingsAsync(IReadOnlyCollection<Guid> dataKeyIdentifiers, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> OrganizationHasDataKeyBindingsAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException(); // E3-M2f-2 (not used here)

        // E3-M2f-3b capability-refusal evidence port: not used by these tests.
        public Task SetCapabilityEvidenceLockTimeoutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DateTimeOffset> StartCapabilityEvidenceLookupAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsCapabilityRefusalEvidenceIndexUsableAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CapabilityRefusalEvidenceRow>> GetNewestCapabilityRefusalEvidenceAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<ContentDataKeyBinding>> GetDataKeyBindingsForOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddDataKeyBindings(IEnumerable<ContentDataKeyBinding> bindings) => throw new NotSupportedException();

        public Task RequireDataKeyLedgerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<PlatformJournalEntry>> GetLatestPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ISearchIndexStore SearchIndex => throw new NotSupportedException();

        public ISearchAccessStore SearchAccess => throw new NotSupportedException();

        public List<AuthorityResource> Resources { get; } = [];

        public List<IdentityBinding> Bindings { get; } = [];

        public Dictionary<Guid, RecordContentHead> Heads { get; } = [];

        public Dictionary<Guid, List<RecordManifestLeaf>> Leaves { get; } = [];

        public Dictionary<Guid, ShareResourceMetadata> ShareMetadatas { get; } = [];

        public Task<AuthorityResource?> FindResourceAsync(Guid org, string type, Guid id, CancellationToken ct) => Task.FromResult(org == organizationId ? Resources.FirstOrDefault(r => r.ResourceTypeName == type && r.ResourceEntityId == id) : null);

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid org, Guid userId, CancellationToken ct) => Task.FromResult(org == organizationId ? Bindings.FirstOrDefault(b => b.AuthorityUserId == userId) : null);

        public Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid org, CancellationToken ct) => Task.FromResult(org == organizationId ? Bindings.ToList() : []);

        public Task<RecordContentHead?> FindContentHeadAsync(Guid org, Guid recordId, bool lockForUpdate, CancellationToken ct) => Task.FromResult(org == organizationId && Heads.TryGetValue(recordId, out var head) ? head : null);

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid commitId, CancellationToken ct) => Task.FromResult(Leaves.TryGetValue(commitId, out var leaves) ? leaves : []);

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken ct) => Task.FromResult<RecordManifestCommit?>(null);

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken ct) => Task.FromResult(new List<RecordManifestCommit>());

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken ct) => Task.FromResult(new List<ContentChangeOperation>());

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken ct) => Task.FromResult(Bindings.FirstOrDefault(b => b.IdentityBindingId == identityBindingId));

        public Task<SecurityCommand?> FindCommandAsync(Guid id, CancellationToken ct) => Task.FromResult(Commands.TryGetValue(id, out var command) ? command : null);

        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task RequireContentAcceptanceFloorAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddOrganization(OrganizationAuthorizationState state) => throw new NotSupportedException();

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid org, string issuer, string subject, CancellationToken ct) => throw new NotSupportedException();

        public void AddIdentityBinding(IdentityBinding binding) => throw new NotSupportedException();

        public Task<IdentityBinding?> FindIdentityBindingForUpdateAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.Sessions.AuthenticatedSession>> GetUnrevokedSessionsAsync(Guid identityBindingId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationRoleChangeProposal?> FindRoleChangeProposalAsync(Guid organizationId, Guid proposalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationRoleChangeProposal>> GetRoleChangeProposalsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddRoleChangeProposal(OrganizationRoleChangeProposal proposal) => throw new NotSupportedException();

        public Task<OrganizationMembershipRequest?> FindMembershipRequestAsync(Guid organizationId, Guid requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationMembershipRequest>> GetMembershipRequestsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddMembershipRequest(OrganizationMembershipRequest request) => throw new NotSupportedException();

        public void AddResource(AuthorityResource resource) => throw new NotSupportedException();

        public Task<ShareResourceMetadata?> FindShareMetadataAsync(Guid org, Guid recordShareId, CancellationToken ct) =>
            Task.FromResult(org == organizationId && ShareMetadatas.TryGetValue(recordShareId, out var metadata) ? metadata : null);

        public void AddShareMetadata(ShareResourceMetadata metadata) => ShareMetadatas[metadata.RecordShareId] = metadata;

        public Task<List<ShareResourceMetadata>> FindShareMetadataByParentAsync(Guid org, Guid parentRecordShareId, CancellationToken ct) =>
            Task.FromResult(org == organizationId ? ShareMetadatas.Values.Where(m => m.ParentRecordShareId == parentRecordShareId).ToList() : []);

        public OrganizationProtectionPolicy? Policy { get; set; }

        public Dictionary<Guid, SecurityCommand> Commands { get; } = [];

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid org, Guid parent, IReadOnlyCollection<string> types, CancellationToken ct) =>
            Task.FromResult(org == organizationId ? Resources.Where(r => r.ParentResourceEntityId == parent && types.Contains(r.ResourceTypeName) && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToList() : []);

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid org, CancellationToken ct) => Task.FromResult(org == organizationId ? Policy : null);

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => Policy = policy;

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<ShareResourceMetadata>> GetActiveShareMetadataWithExpiryAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityRelationshipEdge>> GetActiveShareEdgesAsync(Guid org, IReadOnlyCollection<Guid> recordShareIds, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityRelationshipEdge>> GetActiveTeamMemberEdgesAsync(Guid org, IReadOnlyCollection<Guid> teamIds, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityRelationshipEdge>> GetRelationshipEdgeHistoryAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Dictionary<Guid, long?>> GetCommittedRevisionsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddEdge(AuthorityRelationshipEdge edge) => throw new NotSupportedException();

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid org, string key, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<string>> GetCommandPayloadsAsync(Guid organizationId, IReadOnlyCollection<string> commandKinds, CancellationToken cancellationToken) => Task.FromResult(new List<string>());

        public void AddCommand(SecurityCommand command) => throw new NotSupportedException();

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => throw new NotSupportedException();

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid org, long revision, CancellationToken ct) => throw new NotSupportedException();

        public void AddJournalEntry(SecurityJournalEntry entry) => throw new NotSupportedException();

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid org, Guid operation, CancellationToken ct) => throw new NotSupportedException();

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid org, string stream, long revision, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid org, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> CountPendingArchiveAsync(Guid org, string stream, CancellationToken ct) => throw new NotSupportedException();

        public void AddContentHead(RecordContentHead head) => throw new NotSupportedException();

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid org, string key, CancellationToken ct) => throw new NotSupportedException();

        public Task<ContentChangeOperation?> FindOperationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public void AddOperation(ContentChangeOperation operation) => throw new NotSupportedException();

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves) => throw new NotSupportedException();

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public void AddManifestCommit(RecordManifestCommit commit) => throw new NotSupportedException();

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => throw new NotSupportedException();

        public Task<List<ContentItemIdentity>> FindItemIdentitiesAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddItemIdentities(IEnumerable<ContentItemIdentity> identities) => throw new NotSupportedException();

        public Task<List<Guid>> FindAcceptedItemIdsAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => throw new NotSupportedException();

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid org, string purpose, CancellationToken ct) => throw new NotSupportedException();

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationForUpdateAsync(Guid org, string purpose, CancellationToken ct) => throw new NotSupportedException();

        public void AddKeyRegistration(IntegrityKeyRegistration registration) => throw new NotSupportedException();

        public Task<List<Guid>> GetActiveRecordIdsAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid org, Guid form, IReadOnlyCollection<string> states, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityResource>> GetResourcesAsync(Guid org, string type, IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<RecordContentHead>> GetContentHeadsAsync(Guid org, IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();

        public void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries) => throw new NotSupportedException();

        public Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid id, int offset, int? limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.Sessions.AuthenticatedSession?> FindSessionByDigestAsync(byte[] digest, CancellationToken ct) => throw new NotSupportedException();

        public void AddSession(AuthorizationAuthority.Domain.Features.Sessions.AuthenticatedSession session) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid org, Guid form, CancellationToken ct) => Task.FromResult<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion?>(null);

        public void AddPublishedFormVersion(AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion version) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid org, CancellationToken ct) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion>());

        public Task<List<AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid org, Guid? form, CancellationToken ct) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval>());

        public void AddFieldSearchApproval(AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval approval) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion>> GetPublishedFormVersionsAsync(Guid org, Guid form, CancellationToken ct) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.FieldSearch.PublishedFormVersion>());

        public Task<AuthorizationAuthority.Domain.Features.FieldSearch.FieldFormBinding?> FindFieldFormBindingAsync(Guid org, Guid field, CancellationToken ct) => Task.FromResult<AuthorizationAuthority.Domain.Features.FieldSearch.FieldFormBinding?>(null);

        public void AddFieldFormBinding(AuthorizationAuthority.Domain.Features.FieldSearch.FieldFormBinding binding) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.FieldSearch.FormVersionPublication?> FindPendingFormVersionPublicationAsync(Guid org, Guid form, CancellationToken ct) => Task.FromResult<AuthorizationAuthority.Domain.Features.FieldSearch.FormVersionPublication?>(null);

        public void AddFormVersionPublication(AuthorizationAuthority.Domain.Features.FieldSearch.FormVersionPublication publication) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.Catalogue.SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesForRecordsAsync(Guid snapshot, IReadOnlyCollection<Guid> records, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorityResource>> GetResourcesByTypeAsync(Guid org, IReadOnlyCollection<string> types, IReadOnlyCollection<string> states, CancellationToken ct) => throw new NotSupportedException();

        public Task<Dictionary<Guid, int>> CountManifestCommitsAsync(Guid org, IReadOnlyCollection<Guid> records, CancellationToken ct) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.Catalogue.ProtectionCensusResult>> GetProtectionCensusResultsAsync(Guid org, Guid command, IReadOnlyCollection<Guid> records, CancellationToken ct) => throw new NotSupportedException();

        public void AddProtectionCensusResult(AuthorizationAuthority.Domain.Features.Catalogue.ProtectionCensusResult result) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.Catalogue.ProtectionCensusResult?> FindProtectionCensusResultAsync(Guid command, Guid record, Guid operation, CancellationToken ct) => throw new NotSupportedException();

        public void AddProtectionAttestation(AuthorizationAuthority.Domain.Features.Catalogue.ProtectionReconciliationAttestation attestation) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.Catalogue.ProtectionReconciliationAttestation>> GetLatestProtectionAttestationsAsync(Guid org, IReadOnlyCollection<Guid> commands, CancellationToken ct) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformJournalHead?> LockPlatformJournalAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformJournalHead?> ReadPlatformJournalAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddPlatformJournalHead(AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformJournalHead head) => throw new NotSupportedException();

        public void AddPlatformJournalEntry(AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformJournalEntry entry) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformJournalEntry>> GetPendingPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountPendingPlatformArchiveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperator?> FindPlatformOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperator?> FindPlatformOperatorByIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperator>> GetPlatformOperatorsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddPlatformOperator(AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperator platformOperator) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorChangeProposal?> FindPlatformOperatorProposalAsync(Guid proposalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorChangeProposal>> GetPlatformOperatorProposalsAsync(DateTimeOffset createdSince, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddPlatformOperatorProposal(AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorChangeProposal proposal) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorApproval?> FindPlatformApprovalAsync(Guid platformOperatorApprovalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorApproval>> GetPlatformApprovalsForRequestAsync(Guid organizationMembershipRequestId, CancellationToken cancellationToken) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorApproval>());

        public Task<List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorApproval>> GetPendingPlatformApprovalsForOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorApproval>> GetPlatformApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationMembershipRequestIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddPlatformApproval(AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorApproval approval) => throw new NotSupportedException();

        public Task<bool> IsPlatformEvidenceUsedAsync(string evidenceUseIdentifier, byte[] evidenceUseSha256, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorEvidenceUse?> FindPlatformEvidenceUseAsync(Guid platformOperatorEvidenceUseId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddPlatformEvidenceUse(AuthorizationAuthority.Domain.Features.PlatformOperators.PlatformOperatorEvidenceUse evidenceUse) => throw new NotSupportedException();

        public Task<PlatformRequestStorePage> SearchMembershipRequestsAsync(PlatformMembershipRequestQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestAsync(Guid organizationOnboardingRequestId, bool lockForUpdate, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationOnboardingRequest>> GetOpenOnboardingRequestsByNameAsync(string normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddOnboardingRequest(OrganizationOnboardingRequest request) => throw new NotSupportedException();

        public Task<List<OrganizationOnboardingOwner>> GetOnboardingOwnersAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOnboardingOwner?> FindOnboardingOwnerAsync(Guid organizationOnboardingOwnerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddOnboardingOwner(OrganizationOnboardingOwner owner) => throw new NotSupportedException();

        public Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationOnboardingRequestIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOnboardingApproval?> FindOnboardingApprovalAsync(Guid organizationOnboardingApprovalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddOnboardingApproval(OrganizationOnboardingApproval approval) => throw new NotSupportedException();

        public Task<OnboardingRequestStorePage> SearchOnboardingRequestsAsync(OnboardingRequestQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationDirectoryRow>> GetOrganizationDirectoryRowsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountOrganizationsBindingIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseAsync(Guid organizationOwnerRecoveryCaseId, bool lockForUpdate, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOwnerRecoveryCase?> FindOpenOwnerRecoveryCaseAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddOwnerRecoveryCase(OrganizationOwnerRecoveryCase recoveryCase) => throw new NotSupportedException();

        public Task<List<OrganizationOwnerRecoverySubject>> GetOwnerRecoverySubjectsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOwnerRecoverySubject?> FindOwnerRecoverySubjectAsync(Guid organizationOwnerRecoverySubjectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddOwnerRecoverySubject(OrganizationOwnerRecoverySubject subject) => throw new NotSupportedException();

        public Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsForCasesAsync(IReadOnlyCollection<Guid> organizationOwnerRecoveryCaseIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrganizationOwnerRecoveryApproval?> FindOwnerRecoveryApprovalAsync(Guid organizationOwnerRecoveryApprovalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddOwnerRecoveryApproval(OrganizationOwnerRecoveryApproval approval) => throw new NotSupportedException();

        public Task<OwnerRecoveryStorePage> SearchOwnerRecoveryCasesAsync(OwnerRecoveryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<List<OrganizationRecoveredOwnerProbation>> GetActiveRecoveredOwnerProbationsAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(new List<OrganizationRecoveredOwnerProbation>());

        public Task<OrganizationRecoveredOwnerProbation?> FindRecoveredOwnerProbationAsync(Guid organizationRecoveredOwnerProbationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void AddRecoveredOwnerProbation(OrganizationRecoveredOwnerProbation probation) => throw new NotSupportedException();

        public Task<int> CountSessionsCreatedSinceAsync(Guid identityBindingId, DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
