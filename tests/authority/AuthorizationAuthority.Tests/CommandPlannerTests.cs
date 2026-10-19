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
        var request = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2 });

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
        var weaken = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, Form, new { form_definition_id = Form, field_definition_id = field, required_protection_mode = "Standard", protection_policy_revision = 3 });
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), weaken, graph, CancellationToken.None))).ErrorCode);
        var unknownForm = Guid.CreateVersion7();
        var unregistered = Command(SecurityCommandKind.PublishFormSecurityRevision, ResourceKind.FormDefinition, unknownForm, new { form_definition_id = unknownForm, field_definition_id = field, required_protection_mode = "ApplicationEncrypted", protection_policy_revision = 2 });
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), unregistered, graph, CancellationToken.None))).ErrorCode);
        scope.Resources.Add(Resource(ResourceTypeNames.FieldProtectionRequirement, field));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => CommandPlanner.PlanAsync(scope, Org, Actor(designer), request, graph, CancellationToken.None))).ErrorCode);
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

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken ct) => Task.FromResult<IdentityBinding?>(null);

        public Task<SecurityCommand?> FindCommandAsync(Guid id, CancellationToken ct) => Task.FromResult<SecurityCommand?>(null);

        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddOrganization(OrganizationAuthorizationState state) => throw new NotSupportedException();

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid org, string issuer, string subject, CancellationToken ct) => throw new NotSupportedException();

        public void AddIdentityBinding(IdentityBinding binding) => throw new NotSupportedException();

        public void AddResource(AuthorityResource resource) => throw new NotSupportedException();

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid org, CancellationToken ct) => throw new NotSupportedException();

        public void AddEdge(AuthorityRelationshipEdge edge) => throw new NotSupportedException();

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid org, string key, CancellationToken ct) => throw new NotSupportedException();

        public void AddCommand(SecurityCommand command) => throw new NotSupportedException();

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => throw new NotSupportedException();

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid org, long revision, CancellationToken ct) => throw new NotSupportedException();

        public void AddJournalEntry(SecurityJournalEntry entry) => throw new NotSupportedException();

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

        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
