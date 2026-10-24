using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
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

    /// <summary>Previously revoked registry rows that become Active again under this command (the registry key is unique per entity).</summary>
    public List<AuthorityResource> ReactivateResources { get; } = [];

    /// <summary>Organization protection policy written by this command (UpdateMandatoryPolicy), new or tracked.</summary>
    public OrganizationProtectionPolicy? ProtectionPolicy { get; set; }

    public bool ProtectionPolicyIsNew { get; set; }

    /// <summary>Phase C7 M2 (OD-3): a newly registered published form version.</summary>
    public PublishedFormVersion? PublishedVersion { get; set; }

    /// <summary>Phase C7 M2 (AC-6): new approvals and tracked approvals whose state or binding this command changes.</summary>
    public List<FieldSearchApproval> NewApprovals { get; } = [];

    public List<FieldSearchApproval> ChangedApprovals { get; } = [];

    /// <summary>The policy whose search_policy_revision advances because field search policy changed under this command.</summary>
    public OrganizationProtectionPolicy? SearchPolicyToAdvance { get; set; }

    public string NormalizedPayloadJson { get; set; } = "{}";
}

/// <summary>
/// Kind-specific validation of security commands against the authoritative desired state. Each kind enforces
/// actor privilege, same-organization binding and structural invariants (one owner, registered resources,
/// bounded delegation). Unknown or unimplemented kinds are rejected; there is no generic relationship writer.
/// </summary>
public static partial class CommandPlanner
{
    public static async Task<CommandPlan> PlanAsync(
        IAuthorityScope scope,
        Guid organizationId,
        BoundActor actor,
        SecurityCommandRequest request,
        DesiredStateGraph graph,
        CancellationToken cancellationToken,
        FieldSearchApprovalGate? approvalGate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.CommandKind switch
        {
            SecurityCommandKind.RegisterTeam => await RegisterTeamAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.AddTeamMember => await TeamMembershipAsync(scope, organizationId, actor, request, graph, add: true, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RemoveTeamMember => await TeamMembershipAsync(scope, organizationId, actor, request, graph, add: false, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RegisterFormDefinition => await RegisterFormDefinitionAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.UpdateFormUsers => await UpdateFormUsersAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RegisterRecord => await RegisterRecordAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.TransferOwnership => await TransferOwnershipAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.CreateShare => await CreateShareAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RevokeShare => await RevokeShareAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.UnlinkChildRecord => await UnlinkChildRecordAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.PublishFormSecurityRevision => await PublishFormSecurityRevisionAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.UpdateMandatoryPolicy => await UpdateMandatoryPolicyAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.ReclassifyField => await ReclassifyFieldAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.RegisterPublishedFormVersion => await RegisterPublishedFormVersionAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.ApproveFieldSearch => await ApproveFieldSearchAsync(scope, organizationId, actor, request, graph, approvalGate ?? FieldSearchApprovalGate.Unavailable, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.WithdrawFieldSearch => await WithdrawFieldSearchAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
            SecurityCommandKind.UpdateFieldSearchers => await UpdateFieldSearchersAsync(scope, organizationId, actor, request, graph, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// Grants/revokes create-record permission (form_user edges) on an existing form. The actor must be a designer of that
    /// form or an organization administrator; the form's version, record ownership, shares and the designer edges are
    /// never touched, so a change grants no read/update/delete on any existing record. Every change is a journaled,
    /// revision-bound command; revocation is effective as soon as the projection barrier opens.
    /// </summary>
    private static async Task<CommandPlan> UpdateFormUsersAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<UpdateFormUsersPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!graph.CanDesignForm(actor.AuthorityUserId, payload.FormDefinitionId) && !IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("changing who may create records requires design permission on the form or an organization administrator");
        }

        var grantUsers = payload.GrantUserIds.Distinct().ToList();
        var grantTeams = payload.GrantTeamIds.Distinct().ToList();
        var revokeUsers = payload.RevokeUserIds.Distinct().ToList();
        var revokeTeams = payload.RevokeTeamIds.Distinct().ToList();
        if (grantUsers.Count + grantTeams.Count + revokeUsers.Count + revokeTeams.Count == 0)
        {
            throw AuthorityException.Invalid("at least one grant or revoke is required");
        }

        if (grantUsers.Count + grantTeams.Count + revokeUsers.Count + revokeTeams.Count > 200)
        {
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, "too many changes in one command");
        }

        if (grantUsers.Intersect(revokeUsers).Any() || grantTeams.Intersect(revokeTeams).Any())
        {
            throw AuthorityException.Invalid("a principal cannot be granted and revoked in the same command");
        }

        var existing = graph.EdgesOf(ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "form_user").ToList();
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { GrantUserIds = grantUsers, GrantTeamIds = grantTeams, RevokeUserIds = revokeUsers, RevokeTeamIds = revokeTeams }) };
        foreach (var userId in grantUsers)
        {
            await RequireUserBindingAsync(scope, organizationId, userId, cancellationToken).ConfigureAwait(false);
            if (existing.Any(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == userId))
            {
                throw AuthorityException.Invalid("user may already create records for this form");
            }

            plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "form_user", ResourceTypeNames.User, userId, string.Empty));
        }

        foreach (var teamId in grantTeams)
        {
            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, teamId, cancellationToken).ConfigureAwait(false);
            if (existing.Any(e => e.SubjectTypeName == ResourceTypeNames.Team && e.SubjectEntityId == teamId))
            {
                throw AuthorityException.Invalid("team may already create records for this form");
            }

            plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "form_user", ResourceTypeNames.Team, teamId, "participant"));
        }

        foreach (var userId in revokeUsers)
        {
            var edge = existing.FirstOrDefault(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == userId)
                       ?? throw AuthorityException.Invalid("user is not currently permitted to create records for this form");
            plan.RemoveEdges.Add(edge);
        }

        foreach (var teamId in revokeTeams)
        {
            var edge = existing.FirstOrDefault(e => e.SubjectTypeName == ResourceTypeNames.Team && e.SubjectEntityId == teamId)
                       ?? throw AuthorityException.Invalid("team is not currently permitted to create records for this form");
            plan.RemoveEdges.Add(edge);
        }

        return plan;
    }

    private static readonly string[] Classifications = ["Public", "Internal", "Confidential", "Restricted"];

    internal static int Rank(string classification)
    {
        var index = Array.IndexOf(Classifications, classification);
        return index >= 0 ? index : throw AuthorityException.Invalid("classification must be Public, Internal, Confidential or Restricted");
    }

    /// <summary>
    /// The organization's data-protection policy, written by its owner or a system administrator and kept by the authority
    /// so that reclassification and downgrade decisions never depend on a business row (Amendment A1 rule 1/3).
    /// </summary>
    private static async Task<CommandPlan> UpdateMandatoryPolicyAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<UpdateMandatoryPolicyPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        if (!IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("the data-protection policy is set by the organization owner or a system administrator");
        }

        _ = Rank(payload.MinimumFieldClassification);
        var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        if (policy is null)
        {
            plan.ProtectionPolicy = new OrganizationProtectionPolicy { OrganizationProtectionPolicyId = Guid.CreateVersion7(), OrganizationId = organizationId, PolicyRevision = 1 };
            plan.ProtectionPolicyIsNew = true;
        }
        else
        {
            plan.ProtectionPolicy = policy;
            policy.PolicyRevision++;
        }

        plan.ProtectionPolicy.MinimumFieldClassification = payload.MinimumFieldClassification;
        plan.ProtectionPolicy.PlaintextInternalFieldsPermitted = payload.PlaintextInternalFieldsPermitted;
        plan.ProtectionPolicy.PlaintextDowngradePermitted = payload.PlaintextDowngradePermitted;
        var searchChanged = plan.ProtectionPolicy.PlaintextSearchPermitted != payload.PlaintextSearchPermitted;
        plan.ProtectionPolicy.PlaintextSearchPermitted = payload.PlaintextSearchPermitted;

        // AC-6 §4.2: turning search off, raising the minimum or disallowing plaintext Internal invalidates every approval
        // that no longer conforms, in this same revision.
        await InvalidateNonConformingApprovalsAsync(scope, organizationId, plan.ProtectionPolicy, plan, cancellationToken).ConfigureAwait(false);
        if (searchChanged || plan.ChangedApprovals.Count > 0)
        {
            plan.SearchPolicyToAdvance = plan.ProtectionPolicy;
        }

        return plan;
    }

    /// <summary>
    /// Declassification of a field to Public or Internal (Amendment A1 rule 2): organization owner or system administrator,
    /// governance approval reference required, target at or above the authority-held policy minimum. Registers a pending
    /// field_reclassification that only a downgrade command from a *different* principal may consume. Grants nothing.
    /// </summary>
    private static async Task<CommandPlan> ReclassifyFieldAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<ReclassifyFieldPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("declassifying a field requires the organization owner or a system administrator");
        }

        if (payload.TargetClassification is not ("Public" or "Internal"))
        {
            throw AuthorityException.Invalid("target_classification must be Public or Internal");
        }

        if (string.IsNullOrWhiteSpace(payload.ApprovalReference) || payload.ApprovalReference.Length > 200)
        {
            throw AuthorityException.Invalid("approval_reference (1..200 characters) identifying the governance decision is required");
        }

        var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false)
                     ?? throw AuthorityException.Forbidden("the organization has not published a data-protection policy to the authority");
        if (!policy.PlaintextDowngradePermitted || Rank(payload.TargetClassification) < Rank(policy.MinimumFieldClassification))
        {
            throw AuthorityException.Forbidden("the organization's data-protection policy does not permit this reclassification");
        }

        var requirement = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldProtectionRequirement, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        if (requirement is null || requirement.ResourceLifecycleState != ResourceLifecycleStates.Active)
        {
            throw AuthorityException.Invalid("the field is not application-encrypted according to the authority; reclassification applies to encrypted fields");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { ApprovalReference = payload.ApprovalReference.Trim() }) };
        var existing = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldReclassification, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.FieldReclassification, payload.FieldDefinitionId, payload.FormDefinitionId));
        }
        else
        {
            plan.ReactivateResources.Add(existing); // a newer declassification supersedes an unconsumed or consumed earlier one
        }

        return plan;
    }

    /// <summary>
    /// Publishes a field's required protection to the authority.
    /// <para><b>ApplicationEncrypted</b> (B-26/B-28): a designer of the form or an organization administrator; registers or
    /// reactivates the field_protection_requirement (re-publication records a raised classification), revokes an active
    /// release. From then on no user operation may carry a plaintext row of the field (barrier) and only reprotection
    /// operations are accepted for it.</para>
    /// <para><b>Standard</b> (Amendment A1 rules 2–5): an organization owner or system administrator — never a designer —
    /// while the authority-held policy permits plaintext downgrades and plaintext storage, and the target classification
    /// (Public/Internal) is at or above the policy minimum. If the field's recorded classification is Confidential or
    /// Restricted (or unknown), an active field_reclassification issued by a <em>different</em> principal with a governance
    /// approval reference within 24 hours is required and consumed. Revokes the requirement, registers the release: from
    /// then on no user operation may carry an encrypted row of the field (barrier) and only deprotection operations are
    /// accepted for it.</para>
    /// Neither direction creates a relationship or grants any access.
    /// </summary>
    private static async Task<CommandPlan> PublishFormSecurityRevisionAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<PublishFormSecurityRevisionPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (payload.FieldDefinitionId == Guid.Empty || payload.ProtectionPolicyRevision < 1)
        {
            throw AuthorityException.Invalid("field_definition_id and a positive protection_policy_revision are required");
        }

        if (payload.FieldClassification is not null)
        {
            _ = Rank(payload.FieldClassification);
        }

        var requirement = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldProtectionRequirement, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var release = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldProtectionRelease, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };

        if (payload.RequiredProtectionMode == "ApplicationEncrypted")
        {
            if (!graph.CanDesignForm(actor.AuthorityUserId, payload.FormDefinitionId) && !IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
            {
                throw AuthorityException.Forbidden("publishing a field protection requirement requires design permission on the form or an organization administrator");
            }

            if (payload.FieldClassification is null)
            {
                throw AuthorityException.Invalid("field_classification is required when publishing an encryption requirement");
            }

            if (requirement is null)
            {
                plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.FieldProtectionRequirement, payload.FieldDefinitionId, payload.FormDefinitionId));
            }
            else
            {
                plan.ReactivateResources.Add(requirement); // (re)activation also records the classification of this publication
            }

            if (release is not null && release.ResourceLifecycleState == ResourceLifecycleStates.Active)
            {
                release.ResourceLifecycleState = ResourceLifecycleStates.Revoked;
                plan.RevokeResources.Add(release);
            }

            // AC-6 §4.4: strengthening invalidates the field's search approval in the same command and revision.
            await InvalidateFieldApprovalAsync(scope, organizationId, payload.FormDefinitionId, payload.FieldDefinitionId, plan, cancellationToken).ConfigureAwait(false);
            return plan;
        }

        if (payload.RequiredProtectionMode != "Standard")
        {
            throw AuthorityException.Invalid("required_protection_mode must be ApplicationEncrypted or Standard");
        }

        // ---- Standard: controlled removal of encryption (Amendment A1).
        if (!IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("removing encryption from a field requires the organization owner or a system administrator; a designer cannot");
        }

        if (payload.FieldClassification is not ("Public" or "Internal"))
        {
            throw AuthorityException.Invalid("a field may only be released from encryption as Public or Internal");
        }

        var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false)
                     ?? throw AuthorityException.Forbidden("the organization has not published a data-protection policy to the authority");
        if (!policy.PlaintextDowngradePermitted || !policy.PlaintextInternalFieldsPermitted || Rank(payload.FieldClassification) < Rank(policy.MinimumFieldClassification))
        {
            throw AuthorityException.Forbidden("the organization's data-protection policy does not permit plaintext for this field");
        }

        if (requirement is null || requirement.ResourceLifecycleState != ResourceLifecycleStates.Active)
        {
            throw AuthorityException.Invalid("the field is not application-encrypted according to the authority");
        }

        // The classification the authority recorded when encryption was last published; unknown counts as sensitive (fail closed).
        var publication = await scope.FindCommandAsync(requirement.RegisteredBySecurityCommandId, cancellationToken).ConfigureAwait(false);
        string? recorded = null;
        if (publication is not null && publication.CommandKindName == SecurityCommandKinds.PublishFormSecurityRevision)
        {
            using var document = System.Text.Json.JsonDocument.Parse(publication.CommandPayloadJson);
            recorded = document.RootElement.TryGetProperty("field_classification", out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : null;
        }

        var sensitive = recorded is not ("Public" or "Internal");
        var reclassification = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldReclassification, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        if (sensitive)
        {
            if (reclassification is null || reclassification.ResourceLifecycleState != ResourceLifecycleStates.Active)
            {
                throw AuthorityException.Forbidden("a Confidential/Restricted field needs an authorized declassification (ReclassifyField with a governance approval) before encryption can be removed");
            }

            var declassification = await scope.FindCommandAsync(reclassification.RegisteredBySecurityCommandId, cancellationToken).ConfigureAwait(false)
                                   ?? throw AuthorityException.Forbidden("the declassification command is missing");
            // Distinct principal: compare the authority user behind the declassifier's binding, not just the binding row (a user may hold several bindings). Unknown binding fails closed.
            var declassifier = await scope.FindIdentityBindingByIdAsync(declassification.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false)
                               ?? throw AuthorityException.Forbidden("the declassifying principal cannot be resolved");
            if (declassification.ActorIdentityBindingId == actor.Binding.IdentityBindingId || declassifier.AuthorityUserId == actor.AuthorityUserId)
            {
                throw AuthorityException.Forbidden("removing encryption from a declassified field requires a second, distinct authorized principal");
            }

            if (declassification.CommandCreatedOn < DateTimeOffset.UtcNow - DeclassificationFreshness)
            {
                throw AuthorityException.Forbidden("the declassification is older than 24 hours; obtain a fresh one");
            }

            using var document = System.Text.Json.JsonDocument.Parse(declassification.CommandPayloadJson);
            var target = document.RootElement.TryGetProperty("target_classification", out var t) ? t.GetString() : null;
            if (target != payload.FieldClassification)
            {
                throw AuthorityException.Invalid("the release classification must equal the declassification target");
            }
        }

        if (reclassification is not null && reclassification.ResourceLifecycleState == ResourceLifecycleStates.Active)
        {
            reclassification.ResourceLifecycleState = ResourceLifecycleStates.Revoked; // consumed
            plan.RevokeResources.Add(reclassification);
        }

        requirement.ResourceLifecycleState = ResourceLifecycleStates.Revoked;
        plan.RevokeResources.Add(requirement);
        if (release is null)
        {
            plan.RegisterResources.Add(NewResource(organizationId, ResourceTypeNames.FieldProtectionRelease, payload.FieldDefinitionId, payload.FormDefinitionId));
        }
        else
        {
            plan.ReactivateResources.Add(release);
        }

        return plan;
    }

    /// <summary>A declassification must be consumed by the downgrade within this window (Amendment A1 §3).</summary>
    private static readonly TimeSpan DeclassificationFreshness = TimeSpan.FromHours(24);

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
        // A field's committed items are its value leaves and — for plaintext dropdowns / multi-selects — its selection leaves (one per selected option).
        var committedFieldItems = leaves.Where(l => l.ManifestItemKindCode is (byte)MerkleItemKind.FieldValue or (byte)MerkleItemKind.FieldSelection).Select(l => l.ManifestItemId).ToHashSet();
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
