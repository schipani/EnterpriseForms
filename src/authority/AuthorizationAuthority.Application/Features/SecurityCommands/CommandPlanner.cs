using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using EnterpriseForms.Integrity.Merkle;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>The validated effect of one command: exact edge additions/removals and registry changes.</summary>
public sealed class CommandPlan
{
    public List<(string ResourceType, Guid ResourceId, string Relation, string SubjectType, Guid SubjectId, string SubjectRelation)> AddEdges { get; } = [];

    public List<AuthorityRelationshipEdge> RemoveEdges { get; } = [];

    public List<AuthorityResource> RegisterResources { get; } = [];

    public List<AuthorityResource> RevokeResources { get; } = [];

    public string NormalizedPayloadJson { get; set; } = "{}";
}

/// <summary>
/// Kind-specific validation of security commands against the authoritative desired state. Each kind enforces
/// actor privilege, same-organization binding and structural invariants (one owner, registered resources,
/// bounded delegation). Unknown or unimplemented kinds are rejected; there is no generic relationship writer.
/// </summary>
public static class CommandPlanner
{
    public static async Task<CommandPlan> PlanAsync(
        IAuthorityScope scope,
        Guid organizationId,
        BoundActor actor,
        SecurityCommandRequest request,
        DesiredStateGraph graph,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.CommandKind switch
        {
            SecurityCommandKind.RegisterTeam => await RegisterTeamAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.AddTeamMember => await TeamMembershipAsync(scope, organizationId, actor, request, graph, add: true, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RemoveTeamMember => await TeamMembershipAsync(scope, organizationId, actor, request, graph, add: false, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RegisterFormDefinition => await RegisterFormDefinitionAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RegisterRecord => await RegisterRecordAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.TransferOwnership => await TransferOwnershipAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.CreateShare => await CreateShareAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RevokeShare => await RevokeShareAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.UnlinkChildRecord => await UnlinkChildRecordAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            _ => throw new AuthorityException(AuthorityErrorCodes.CommandKindNotImplemented, 422, false, $"command kind {request.CommandKind} is not implemented in Stage A"),
        };
    }

    private static bool IsOrganizationAdministrator(DesiredStateGraph graph, Guid organizationId, Guid userId) =>
        graph.HasOrganizationRole(organizationId, userId, OrganizationRoleNames.OrganizationOwner)
        || graph.HasOrganizationRole(organizationId, userId, OrganizationRoleNames.SystemAdministrator);

    private static async Task<CommandPlan> RegisterTeamAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<RegisterTeamPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Team, payload.TeamId);
        if (!IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("team registration requires organization owner or system administrator");
        }

        if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.Team, payload.TeamId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AuthorityException.Invalid("team already registered");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.Team, payload.TeamId, null));
        return plan;
    }

    private static async Task<CommandPlan> TeamMembershipAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, bool add, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<TeamMemberPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Team, payload.TeamId);
        if (!IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("team membership changes require organization owner or system administrator");
        }

        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, payload.TeamId, cancellationToken).ConfigureAwait(false);
        await RequireUserBindingAsync(scope, organizationId, payload.MemberUserId, cancellationToken).ConfigureAwait(false);

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        var existing = graph.EdgesOf(ResourceTypeNames.Team, payload.TeamId, "member").FirstOrDefault(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == payload.MemberUserId);
        if (add)
        {
            if (existing is not null)
            {
                throw AuthorityException.Invalid("user is already a team member");
            }

            plan.AddEdges.Add((ResourceTypeNames.Team, payload.TeamId, "member", ResourceTypeNames.User, payload.MemberUserId, string.Empty));
        }
        else
        {
            if (existing is null)
            {
                throw AuthorityException.Invalid("user is not a team member");
            }

            plan.RemoveEdges.Add(existing);
        }

        return plan;
    }

    private static async Task<CommandPlan> RegisterFormDefinitionAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<RegisterFormDefinitionPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        if (!graph.CanCreateForm(organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("form registration requires the designer role");
        }

        if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AuthorityException.Invalid("form definition already registered");
        }

        if (payload.FormUserUserIds.Count + payload.FormUserTeamIds.Count > 200)
        {
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, "too many form users");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, null));
        plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "organization", ResourceTypeNames.Organization, organizationId, string.Empty));
        plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "designer", ResourceTypeNames.User, actor.AuthorityUserId, string.Empty));
        foreach (var userId in payload.FormUserUserIds.Distinct())
        {
            await RequireUserBindingAsync(scope, organizationId, userId, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "form_user", ResourceTypeNames.User, userId, string.Empty));
        }

        foreach (var teamId in payload.FormUserTeamIds.Distinct())
        {
            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, teamId, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "form_user", ResourceTypeNames.Team, teamId, "participant"));
        }

        return plan;
    }

    private static async Task<CommandPlan> RegisterRecordAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<RegisterRecordPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormRecord, payload.FormRecordId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!graph.CanCreateRecord(actor.AuthorityUserId, payload.FormDefinitionId))
        {
            throw AuthorityException.Forbidden("actor may not create records for this form");
        }

        if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormRecord, payload.FormRecordId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AuthorityException.Invalid("record already registered");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.FormRecord, payload.FormRecordId, payload.FormDefinitionId));
        plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "organization", ResourceTypeNames.Organization, organizationId, string.Empty));
        if ((payload.ParentFormRecordId is null) != (payload.FormRelationshipDefinitionId is null))
        {
            throw AuthorityException.Invalid("parent_form_record_id and form_relationship_definition_id must be provided together");
        }

        if (payload.ParentFormRecordId is { } parentId)
        {
            // Child record (B-04): the actor must be the parent's effective owner; the child gets a parent edge instead of an owner edge.
            if (payload.OwnerTeamId is not null)
            {
                throw AuthorityException.Invalid("a child record cannot name an owner team; ownership derives from the parent");
            }

            if (parentId == payload.FormRecordId)
            {
                throw AuthorityException.Invalid("a record cannot be its own parent");
            }

            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormRecord, parentId, cancellationToken).ConfigureAwait(false);
            if (!graph.IsRecordOwner(actor.AuthorityUserId, parentId))
            {
                throw AuthorityException.Forbidden("creating a child record requires effective ownership of the parent");
            }

            plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "parent", ResourceTypeNames.FormRecord, parentId, string.Empty));
            return plan;
        }

        if (payload.OwnerTeamId is { } teamId)
        {
            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, teamId, cancellationToken).ConfigureAwait(false);
            if (!graph.IsTeamMember(actor.AuthorityUserId, teamId))
            {
                throw AuthorityException.Forbidden("actor may assign ownership only to a team they belong to");
            }

            plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "owner", ResourceTypeNames.Team, teamId, "participant"));
        }
        else
        {
            plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "owner", ResourceTypeNames.User, actor.AuthorityUserId, string.Empty));
        }

        return plan;
    }

    private static async Task<CommandPlan> UnlinkChildRecordAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<UnlinkChildRecordPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormRecord, payload.FormRecordId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormRecord, payload.FormRecordId, cancellationToken).ConfigureAwait(false);
        var parentEdges = graph.EdgesOf(ResourceTypeNames.FormRecord, payload.FormRecordId, "parent").Where(e => e.SubjectTypeName == ResourceTypeNames.FormRecord && e.SubjectEntityId == payload.ParentFormRecordId).ToList();
        if (parentEdges.Count == 0)
        {
            throw AuthorityException.Invalid("record is not linked to that parent");
        }

        if (!graph.IsRecordOwner(actor.AuthorityUserId, payload.ParentFormRecordId))
        {
            throw AuthorityException.Forbidden("unlinking a child record requires effective ownership of the parent");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RemoveEdges.AddRange(parentEdges);
        // Data is preserved (no cascade delete); the acting user becomes the owner so the record stays reachable and auditable.
        plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "owner", ResourceTypeNames.User, actor.AuthorityUserId, string.Empty));
        return plan;
    }

    private static async Task<CommandPlan> TransferOwnershipAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<TransferOwnershipPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormRecord, payload.FormRecordId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormRecord, payload.FormRecordId, cancellationToken).ConfigureAwait(false);
        if ((payload.NewOwnerUserId is null) == (payload.NewOwnerTeamId is null))
        {
            throw AuthorityException.Invalid("exactly one of new_owner_user_id or new_owner_team_id is required");
        }

        var isOwner = graph.IsRecordOwner(actor.AuthorityUserId, payload.FormRecordId);
        var isAdministrator = graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.SystemAdministrator);
        if (!isOwner && !isAdministrator)
        {
            throw AuthorityException.Forbidden("ownership transfer requires the current owner or an audited system administrator override");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RemoveEdges.AddRange(graph.EdgesOf(ResourceTypeNames.FormRecord, payload.FormRecordId, "owner"));
        if (payload.NewOwnerUserId is { } userId)
        {
            await RequireUserBindingAsync(scope, organizationId, userId, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "owner", ResourceTypeNames.User, userId, string.Empty));
        }
        else
        {
            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, payload.NewOwnerTeamId!.Value, cancellationToken).ConfigureAwait(false);
            plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "owner", ResourceTypeNames.Team, payload.NewOwnerTeamId.Value, "participant"));
        }

        return plan;
    }

    private static async Task<CommandPlan> CreateShareAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<CreateSharePayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormRecord, payload.FormRecordId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormRecord, payload.FormRecordId, cancellationToken).ConfigureAwait(false);
        if ((payload.RecipientUserId is null) == (payload.RecipientTeamId is null))
        {
            throw AuthorityException.Invalid("exactly one recipient user or team is required");
        }

        var role = payload.RecipientRole switch
        {
            RecipientRoles.Reader => "reader",
            RecipientRoles.Contributor => "contributor",
            RecipientRoles.Manager => "manager",
            _ => throw AuthorityException.Invalid("unknown recipient role"),
        };

        if (payload.FieldItemIds.Count == 0 || payload.FieldItemIds.Count > 500 || payload.FieldItemIds.Distinct().Count() != payload.FieldItemIds.Count)
        {
            throw AuthorityException.Invalid("field_item_ids must be a non-empty distinct list (explicit allow-list, no wildcard)");
        }

        if (await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, payload.RecordShareId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AuthorityException.Invalid("share already registered");
        }

        // Scope is bounded by the accepted content manifest: only currently committed field items can be shared (no future items).
        var head = await scope.FindContentHeadAsync(organizationId, payload.FormRecordId, lockForUpdate: false, cancellationToken).ConfigureAwait(false);
        if (head is null || head.CurrentRecordManifestCommitId is null)
        {
            throw AuthorityException.Invalid("record has no accepted content to share");
        }

        var leaves = await scope.GetManifestLeavesAsync(head.CurrentRecordManifestCommitId.Value, cancellationToken).ConfigureAwait(false);
        var committedFieldItems = leaves.Where(l => l.ManifestItemKindCode == (byte)MerkleItemKind.FieldValue).Select(l => l.ManifestItemId).ToHashSet();
        var unknown = payload.FieldItemIds.Where(id => !committedFieldItems.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw AuthorityException.Invalid("field_item_ids must reference items in the accepted manifest");
        }

        var isOwner = graph.IsRecordOwner(actor.AuthorityUserId, payload.FormRecordId);
        if (!isOwner)
        {
            // Bounded delegation: a manager may share only items currently delegable to them, and may not create further managers.
            var delegable = graph.DelegableFieldItems(actor.AuthorityUserId, payload.FormRecordId);
            if (delegable.Count == 0)
            {
                throw AuthorityException.Forbidden("sharing requires record ownership or a manager share");
            }

            if (role == "manager" || payload.FieldItemIds.Any(id => !delegable.Contains(id)))
            {
                throw new AuthorityException(AuthorityErrorCodes.InvalidCommand, 422, false, "delegation exceeds the actor's delegable scope");
            }
        }

        if (payload.RecipientUserId is { } userId)
        {
            await RequireUserBindingAsync(scope, organizationId, userId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, payload.RecipientTeamId!.Value, cancellationToken).ConfigureAwait(false);
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.RecordShare, payload.RecordShareId, payload.FormRecordId));
        plan.AddEdges.Add((ResourceTypeNames.FormRecord, payload.FormRecordId, "shared_via", ResourceTypeNames.RecordShare, payload.RecordShareId, string.Empty));
        if (payload.RecipientUserId is { } recipientUser)
        {
            plan.AddEdges.Add((ResourceTypeNames.RecordShare, payload.RecordShareId, role, ResourceTypeNames.User, recipientUser, string.Empty));
        }
        else
        {
            plan.AddEdges.Add((ResourceTypeNames.RecordShare, payload.RecordShareId, role, ResourceTypeNames.Team, payload.RecipientTeamId!.Value, "participant"));
        }

        foreach (var fieldItemId in payload.FieldItemIds)
        {
            plan.AddEdges.Add((ResourceTypeNames.FieldItem, fieldItemId, "shared_via", ResourceTypeNames.RecordShare, payload.RecordShareId, string.Empty));
        }

        return plan;
    }

    private static async Task<CommandPlan> RevokeShareAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<RevokeSharePayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormRecord, payload.FormRecordId);
        var share = await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.RecordShare, payload.RecordShareId, cancellationToken).ConfigureAwait(false);
        if (share.ParentResourceEntityId != payload.FormRecordId)
        {
            throw AuthorityException.Invalid("share does not belong to the record");
        }

        var isOwner = graph.IsRecordOwner(actor.AuthorityUserId, payload.FormRecordId);
        if (!isOwner)
        {
            var creator = await scope.FindCommandAsync(share.RegisteredBySecurityCommandId, cancellationToken).ConfigureAwait(false);
            if (creator is null || creator.ActorIdentityBindingId != actor.Binding.IdentityBindingId)
            {
                throw AuthorityException.Forbidden("only the owner or the share's grantor may revoke it");
            }
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.RemoveEdges.AddRange(graph.EdgesWithSubject(ResourceTypeNames.RecordShare, payload.RecordShareId));
        plan.RemoveEdges.AddRange(graph.EdgesOf(ResourceTypeNames.RecordShare, payload.RecordShareId));
        share.ResourceLifecycleState = ResourceLifecycleStates.Revoked;
        plan.RevokeResources.Add(share);
        return plan;
    }

    private static void RequireResource(SecurityCommandRequest request, ResourceKind kind, Guid id)
    {
        if (request.Resource.ResourceKind != kind || request.Resource.ResourceId != id)
        {
            throw AuthorityException.Invalid("resource reference does not match the command payload");
        }
    }

    private static async Task<AuthorityResource> RequireActiveResourceAsync(IAuthorityScope scope, Guid organizationId, string type, Guid id, CancellationToken cancellationToken)
    {
        var resource = await scope.FindResourceAsync(organizationId, type, id, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active)
        {
            throw AuthorityException.NotRegistered($"{type} is not an active registered resource of the organization");
        }

        return resource;
    }

    private static async Task RequireUserBindingAsync(IAuthorityScope scope, Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken)
    {
        var binding = await scope.FindIdentityBindingByUserAsync(organizationId, authorityUserId, cancellationToken).ConfigureAwait(false);
        if (binding is null || !binding.IdentityBindingEnabled)
        {
            throw AuthorityException.Invalid("user is not an enabled principal of the organization");
        }
    }

    private static AuthorityResource NewResource(Guid organizationId, string type, Guid id, Guid? parent) => new()
    {
        AuthorityResourceId = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        ResourceTypeName = type,
        ResourceEntityId = id,
        ParentResourceEntityId = parent,
        ResourceLifecycleState = ResourceLifecycleStates.Active,
    };

    internal static JsonElement EmptyPayload() => JsonDocument.Parse("{}").RootElement.Clone();
}
