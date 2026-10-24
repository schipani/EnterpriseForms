using AuthorizationAuthority.Domain.Features.Catalogue;
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
        var request = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = shareId, form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { Item } });

        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), request, graph, CancellationToken.None);
        Assert.Contains(plan.AddEdges, e => e.ResourceType == ResourceTypeNames.FieldItem && e.ResourceId == Item && e.SubjectId == shareId);
        Assert.Contains(plan.AddEdges, e => e.ResourceType == ResourceTypeNames.RecordShare && e.Relation == "reader" && e.SubjectId == AttackerUser);

        // A non-owner without a manager share cannot share (no delegation path).
        var denied = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.Forbidden, denied.ErrorCode);

        // Items outside the accepted manifest are rejected: no future/unknown items (MRK-30).
        var unknownItem = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = Guid.CreateVersion7(), form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { Guid.CreateVersion7() } });
        var invalid = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), unknownItem, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, invalid.ErrorCode);
    }

    [Fact]
    public async Task ManagerCannotDelegateBeyondOwnScopeOrCreateManagers()
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
        var graph = Graph(
            Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, OwnerUser),
            Edge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, managerShare),
            Edge(ResourceTypeNames.RecordShare, managerShare, "manager", ResourceTypeNames.User, managerUser),
            Edge(ResourceTypeNames.FieldItem, Item, "shared_via", ResourceTypeNames.RecordShare, managerShare));

        var withinScope = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = Guid.CreateVersion7(), form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { Item } });
        var plan = await CommandPlanner.PlanAsync(scope, Org, Actor(managerUser), withinScope, graph, CancellationToken.None);
        Assert.Contains(plan.AddEdges, e => e.ResourceId == Item);

        var beyondScope = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = Guid.CreateVersion7(), form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { otherItem } });
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(managerUser), beyondScope, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);

        var amplify = Command(SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record, new { record_share_id = Guid.CreateVersion7(), form_record_id = Record, recipient_user_id = AttackerUser, recipient_team_id = (Guid?)null, recipient_role = "Manager", field_item_ids = new[] { Item } });
        var ex2 = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(managerUser), amplify, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex2.ErrorCode);
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
        var request = Command(SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new { form_record_id = Record, new_owner_user_id = AttackerUser, new_owner_team_id = (Guid?)null });

        var byOwner = await CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), request, graph, CancellationToken.None);
        Assert.Single(byOwner.RemoveEdges);
        Assert.Single(byOwner.AddEdges, e => e.SubjectId == AttackerUser);
        var byAdmin = await CommandPlanner.PlanAsync(scope, Org, Actor(admin), request, graph, CancellationToken.None);
        Assert.Single(byAdmin.AddEdges);
        await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(AttackerUser), request, graph, CancellationToken.None));

        var both = Command(SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record, new { form_record_id = Record, new_owner_user_id = AttackerUser, new_owner_team_id = Guid.CreateVersion7() });
        await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), both, graph, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownPayloadMembersAndUnimplementedKindsAreRejected()
    {
        var scope = new FakeScope(Org);
        scope.Resources.Add(Resource(ResourceTypeNames.Team, Form));
        var graph = Graph(Edge(ResourceTypeNames.Organization, Org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, OwnerUser));
        var massAssignment = Command(SecurityCommandKind.RegisterTeam, ResourceKind.Team, Form, new { team_id = Form, owner_user_id = AttackerUser });
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), massAssignment, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);

        var unimplemented = Command(SecurityCommandKind.GrantSystemRole, ResourceKind.Organization, Org, new { });
        var ex2 = await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(OwnerUser), unimplemented, graph, CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.CommandKindNotImplemented, ex2.ErrorCode);
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

    /// <summary>In-memory scope: only the lookups the planner needs. Cross-organization lookups return nothing (tenant binding).</summary>
    private sealed class FakeScope(Guid organizationId) : IAuthorityScope
    {
        public List<AuthorityResource> Resources { get; } = [];

        public List<IdentityBinding> Bindings { get; } = [];

        public Dictionary<Guid, RecordContentHead> Heads { get; } = [];

        public Dictionary<Guid, List<RecordManifestLeaf>> Leaves { get; } = [];

        public Task<AuthorityResource?> FindResourceAsync(Guid org, string type, Guid id, CancellationToken ct) => Task.FromResult(org == organizationId ? Resources.FirstOrDefault(r => r.ResourceTypeName == type && r.ResourceEntityId == id) : null);

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid org, Guid userId, CancellationToken ct) => Task.FromResult(org == organizationId ? Bindings.FirstOrDefault(b => b.AuthorityUserId == userId) : null);

        public Task<RecordContentHead?> FindContentHeadAsync(Guid org, Guid recordId, bool lockForUpdate, CancellationToken ct) => Task.FromResult(org == organizationId && Heads.TryGetValue(recordId, out var head) ? head : null);

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid commitId, CancellationToken ct) => Task.FromResult(Leaves.TryGetValue(commitId, out var leaves) ? leaves : []);

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken ct) => Task.FromResult<RecordManifestCommit?>(null);

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken ct) => Task.FromResult(new List<RecordManifestCommit>());

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken ct) => Task.FromResult(new List<ContentChangeOperation>());

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken ct) => Task.FromResult(Bindings.FirstOrDefault(b => b.IdentityBindingId == identityBindingId));

        public Task<SecurityCommand?> FindCommandAsync(Guid id, CancellationToken ct) => Task.FromResult(Commands.TryGetValue(id, out var command) ? command : null);

        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddOrganization(OrganizationAuthorizationState state) => throw new NotSupportedException();

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid org, string issuer, string subject, CancellationToken ct) => throw new NotSupportedException();

        public void AddIdentityBinding(IdentityBinding binding) => throw new NotSupportedException();

        public void AddResource(AuthorityResource resource) => throw new NotSupportedException();

        public OrganizationProtectionPolicy? Policy { get; set; }

        public Dictionary<Guid, SecurityCommand> Commands { get; } = [];

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid org, Guid parent, IReadOnlyCollection<string> types, CancellationToken ct) =>
            Task.FromResult(org == organizationId ? Resources.Where(r => r.ParentResourceEntityId == parent && types.Contains(r.ResourceTypeName) && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToList() : []);

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid org, CancellationToken ct) => Task.FromResult(org == organizationId ? Policy : null);

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => Policy = policy;

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddEdge(AuthorityRelationshipEdge edge) => throw new NotSupportedException();

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid org, string key, CancellationToken ct) => throw new NotSupportedException();

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

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => throw new NotSupportedException();

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid org, string purpose, CancellationToken ct) => throw new NotSupportedException();

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

        public Task<List<AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid org, Guid? form, CancellationToken ct) => Task.FromResult(new List<AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval>());

        public void AddFieldSearchApproval(AuthorizationAuthority.Domain.Features.FieldSearch.FieldSearchApproval approval) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
