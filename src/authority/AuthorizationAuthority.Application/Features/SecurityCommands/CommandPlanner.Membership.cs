using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C8 M2 (AC-7, decisions D8-24…D8-32): organization roles and membership, administered by organization owners only.
/// <list type="bullet">
/// <item>The actor must be an enabled <c>organization_owner</c>; <c>administer_security</c> and <c>designer</c> never
/// qualify (G25 separation). Nobody changes their own roles or membership.</item>
/// <item>Designer grants and revocations need one owner. Owner grants and revocations, and disabling or re-enabling an owner,
/// need two distinct owners: one proposes (<c>ProposeOrganizationOwnerChange</c>, 24 hours), another executes with the
/// proposal id. Every precondition is re-checked at execution — a delayed or replayed approval never bypasses the
/// current roles, membership state, expiry, record ownership or last-holder rules.</item>
/// <item>The last enabled owner and the last enabled Security Administrator are never removed or disabled.</item>
/// <item>Disabling keeps every edge (roles, teams, shares) and revokes the person's sessions; the binding is refused by
/// ActorResolver and session validation. It is refused while the person directly owns active records (count only).</item>
/// <item>Membership requests grant nothing: only two operator approvals create the binding (MembershipProvisioningService).</item>
/// <item>Fresh step-up is required (PB-08 gate: synthetic in Development/Testing, refused in staging and production).</item>
/// </list>
/// The planner only validates and describes the change; <see cref="SecurityCommandService.ApplyPlan"/> applies it in the
/// command's transaction, so a refused command leaves nothing changed.
/// </summary>
public static partial class CommandPlanner
{
    public static readonly TimeSpan OwnerProposalLifetime = TimeSpan.FromHours(24);

    public static readonly TimeSpan MembershipRequestLifetime = TimeSpan.FromDays(7);

    private static readonly HashSet<string> AdministeredRoles = [OrganizationRoleNames.OrganizationOwner, OrganizationRoleNames.Designer];

    private static async Task<CommandPlan> OrganizationRoleAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, DateTimeOffset now, bool grant, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<OrganizationRolePayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireOwnerActor(graph, organizationId, actor);
        RequireReason(payload.Reason);
        if (!AdministeredRoles.Contains(payload.RoleName ?? string.Empty))
        {
            throw AuthorityException.Invalid("only organization_owner and designer are administered by these commands (Security Administrators have their own commands)");
        }

        RequireNotSelf(actor, payload.AuthorityUserId);
        gate.RequireStepUp(payload.StepUpEvidence);
        var target = await scope.FindIdentityBindingByUserAsync(organizationId, payload.AuthorityUserId, cancellationToken).ConfigureAwait(false)
                     ?? throw AuthorityException.Invalid("the person is not a member of this organization");
        var holds = graph.HasOrganizationRole(organizationId, target.AuthorityUserId, payload.RoleName!);
        if (grant && !target.IdentityBindingEnabled)
        {
            throw new AuthorityException(AuthorityErrorCodes.MembershipStateConflict, 409, false, "a disabled member cannot receive a role; re-enable the membership first");
        }

        if (grant && holds)
        {
            throw new AuthorityException(AuthorityErrorCodes.RoleAlreadyHeld, 409, false, "the person already holds this role");
        }

        if (!grant && !holds)
        {
            throw new AuthorityException(AuthorityErrorCodes.RoleNotHeld, 409, false, "the person does not hold this role");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { StepUpEvidence = null }) };
        if (payload.RoleName == OrganizationRoleNames.OrganizationOwner)
        {
            var bindings = await EnabledUsersAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
            plan.ConsumedProposal = await RequireProposalAsync(scope, organizationId, actor, payload.ProposalId, grant ? OwnerChangeKinds.GrantOwner : OwnerChangeKinds.RevokeOwner, target.AuthorityUserId, graph, bindings, now, cancellationToken).ConfigureAwait(false);
            if (!grant)
            {
                RequireAnotherEnabledOwner(graph, organizationId, bindings, target.AuthorityUserId);
            }
        }
        else if (payload.ProposalId is not null)
        {
            throw AuthorityException.Invalid("designer changes need one owner and no proposal");
        }

        if (grant)
        {
            plan.AddEdges.Add((ResourceTypeNames.Organization, organizationId, payload.RoleName!, ResourceTypeNames.User, target.AuthorityUserId, string.Empty));
        }
        else
        {
            plan.RemoveEdges.AddRange(graph.EdgesOf(ResourceTypeNames.Organization, organizationId, payload.RoleName).Where(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == target.AuthorityUserId));
        }

        return plan;
    }

    private static async Task<CommandPlan> ProposeOwnerChangeAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<OwnerChangeProposalPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireOwnerActor(graph, organizationId, actor);
        RequireReason(payload.Reason);
        if (!OwnerChangeKinds.All.Contains(payload.ProposedChange ?? string.Empty))
        {
            throw AuthorityException.Invalid("proposed_change must be GrantOwner, RevokeOwner, DisableOwner or EnableOwner");
        }

        RequireNotSelf(actor, payload.AuthorityUserId);
        gate.RequireStepUp(payload.StepUpEvidence);
        var target = await scope.FindIdentityBindingByUserAsync(organizationId, payload.AuthorityUserId, cancellationToken).ConfigureAwait(false)
                     ?? throw AuthorityException.Invalid("the person is not a member of this organization");
        var isOwner = graph.HasOrganizationRole(organizationId, target.AuthorityUserId, OrganizationRoleNames.OrganizationOwner);
        var bindings = await EnabledUsersAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);

        // Early feedback only: the executing command re-checks everything against the state at that time.
        switch (payload.ProposedChange)
        {
            case OwnerChangeKinds.GrantOwner when !target.IdentityBindingEnabled:
                throw new AuthorityException(AuthorityErrorCodes.MembershipStateConflict, 409, false, "a disabled member cannot become an owner");
            case OwnerChangeKinds.GrantOwner when isOwner:
                throw new AuthorityException(AuthorityErrorCodes.RoleAlreadyHeld, 409, false, "the person is already an organization owner");
            case OwnerChangeKinds.RevokeOwner or OwnerChangeKinds.DisableOwner or OwnerChangeKinds.EnableOwner when !isOwner:
                throw new AuthorityException(AuthorityErrorCodes.RoleNotHeld, 409, false, "the person is not an organization owner; this change needs no second owner");
            case OwnerChangeKinds.RevokeOwner:
                RequireAnotherEnabledOwner(graph, organizationId, bindings, target.AuthorityUserId);
                break;
            case OwnerChangeKinds.DisableOwner:
                if (!target.IdentityBindingEnabled)
                {
                    throw new AuthorityException(AuthorityErrorCodes.MembershipStateConflict, 409, false, "the membership is already disabled");
                }

                RequireAnotherEnabledOwner(graph, organizationId, bindings, target.AuthorityUserId);
                await RequireDisableAllowedAsync(scope, organizationId, graph, bindings, target.AuthorityUserId, cancellationToken).ConfigureAwait(false);
                break;
            case OwnerChangeKinds.EnableOwner when target.IdentityBindingEnabled:
                throw new AuthorityException(AuthorityErrorCodes.MembershipStateConflict, 409, false, "the membership is already enabled");
        }

        var pending = await scope.GetRoleChangeProposalsAsync(organizationId, now - OwnerProposalLifetime, cancellationToken).ConfigureAwait(false);
        if (pending.Any(p => p.TargetAuthorityUserId == target.AuthorityUserId && p.ProposedChangeName == payload.ProposedChange && p.ProposalStateName == ProposalStates.Pending && p.ProposalExpiresOn > now))
        {
            throw new AuthorityException(AuthorityErrorCodes.ProposalExists, 409, false, "an identical proposal is already waiting for a second owner");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { StepUpEvidence = null }) };
        plan.NewProposal = new OrganizationRoleChangeProposal
        {
            OrganizationId = organizationId,
            TargetAuthorityUserId = target.AuthorityUserId,
            ProposedChangeName = payload.ProposedChange!,
            ProposedByAuthorityUserId = actor.AuthorityUserId,
            ProposalReasonText = payload.Reason.Trim(),
            ProposalCreatedOn = now,
            ProposalExpiresOn = now + OwnerProposalLifetime,
            ProposalStateName = ProposalStates.Pending,
        };
        return plan;
    }

    private static async Task<CommandPlan> MembershipStateAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, DateTimeOffset now, bool enable, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<MembershipStatePayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireOwnerActor(graph, organizationId, actor);
        RequireReason(payload.Reason);
        RequireNotSelf(actor, payload.AuthorityUserId);
        gate.RequireStepUp(payload.StepUpEvidence);
        var binding = await scope.FindIdentityBindingForUpdateAsync(organizationId, payload.AuthorityUserId, cancellationToken).ConfigureAwait(false)
                      ?? throw AuthorityException.Invalid("the person is not a member of this organization");
        if (binding.IdentityBindingEnabled == enable)
        {
            throw new AuthorityException(AuthorityErrorCodes.MembershipStateConflict, 409, false, enable ? "the membership is already enabled" : "the membership is already disabled");
        }

        var bindings = await EnabledUsersAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { StepUpEvidence = null }) };
        if (graph.HasOrganizationRole(organizationId, binding.AuthorityUserId, OrganizationRoleNames.OrganizationOwner))
        {
            plan.ConsumedProposal = await RequireProposalAsync(scope, organizationId, actor, payload.ProposalId, enable ? OwnerChangeKinds.EnableOwner : OwnerChangeKinds.DisableOwner, binding.AuthorityUserId, graph, bindings, now, cancellationToken).ConfigureAwait(false);
            if (!enable)
            {
                RequireAnotherEnabledOwner(graph, organizationId, bindings, binding.AuthorityUserId);
            }
        }
        else if (payload.ProposalId is not null)
        {
            throw AuthorityException.Invalid("only an owner's membership change needs a proposal");
        }

        if (enable)
        {
            plan.BindingToEnable = binding;
            return plan;
        }

        await RequireDisableAllowedAsync(scope, organizationId, graph, bindings, binding.AuthorityUserId, cancellationToken).ConfigureAwait(false);
        plan.BindingToDisable = binding;
        plan.SessionsToRevoke.AddRange(await scope.GetUnrevokedSessionsAsync(binding.IdentityBindingId, cancellationToken).ConfigureAwait(false));
        return plan;
    }

    private static async Task<CommandPlan> RequestMembershipAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<MembershipRequestPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.Organization, organizationId);
        RequireOwnerActor(graph, organizationId, actor);
        RequireReason(payload.Reason);
        if (payload.InitialRoleName is not (null or OrganizationRoleNames.Designer))
        {
            throw AuthorityException.Invalid("a new member may start with no role or designer; owners and Security Administrators are appointed afterwards");
        }

        var hint = payload.DisplayNameHint?.Trim() ?? string.Empty;
        if (hint.Length is 0 or > 200 || hint.Any(char.IsControl))
        {
            throw AuthorityException.Invalid("a display name of 1 to 200 characters is required (display only, never an identifier)");
        }

        gate.RequireStepUp(payload.StepUpEvidence);
        var issuer = payload.IssuerUri?.Trim() ?? string.Empty;
        var subject = payload.SubjectIdentifier?.Trim() ?? string.Empty;
        var bindings = await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var organizationIssuer = OrganizationIdentityIssuer(graph, organizationId, bindings);
        if (organizationIssuer is null || !string.Equals(issuer, organizationIssuer, StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.IdentityNotAllowed, 422, false, "the identity must come from the organization's configured identity provider tenant");
        }

        if (!IsValidSubject(issuer, subject))
        {
            throw new AuthorityException(AuthorityErrorCodes.IdentityNotAllowed, 422, false, IsEntraIssuer(issuer)
                ? "the subject must be the person's Microsoft Entra object id (a GUID), never an email address"
                : "the subject must be the identity provider's immutable subject identifier (letters, digits and . _ : -), never an email address");
        }

        if (bindings.Any(b => b.IdentityIssuerUri == issuer && b.IdentitySubjectIdentifier == subject))
        {
            throw new AuthorityException(AuthorityErrorCodes.IdentityAlreadyBound, 409, false, "this identity is already a member of the organization");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { IssuerUri = issuer, SubjectIdentifier = subject, DisplayNameHint = hint, Reason = payload.Reason.Trim(), StepUpEvidence = null }) };
        foreach (var existing in (await scope.GetMembershipRequestsAsync(organizationId, DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false))
                     .Where(r => r.RequestStateName == MembershipRequestStates.Pending && r.RequestedIssuerUri == issuer && r.RequestedSubjectIdentifier == subject))
        {
            if (existing.RequestExpiresOn > now)
            {
                throw new AuthorityException(AuthorityErrorCodes.MembershipRequestExists, 409, false, "a request for this identity is already waiting for operator approval");
            }

            plan.ExpiredMembershipRequests.Add(await scope.FindMembershipRequestAsync(organizationId, existing.OrganizationMembershipRequestId, cancellationToken).ConfigureAwait(false) ?? throw AuthorityException.NotReady("membership request vanished"));
        }

        plan.NewMembershipRequest = new OrganizationMembershipRequest
        {
            OrganizationId = organizationId,
            RequestedAuthorityUserId = Guid.CreateVersion7(),
            RequestedIssuerUri = issuer,
            RequestedSubjectIdentifier = subject,
            RequestedDisplayNameHint = hint,
            RequestedInitialRoleName = payload.InitialRoleName,
            RequestedByAuthorityUserId = actor.AuthorityUserId,
            RequestReasonText = payload.Reason.Trim(),
            RequestCreatedOn = now,
            RequestExpiresOn = now + MembershipRequestLifetime,
            RequestStateName = MembershipRequestStates.Pending,
        };
        return plan;
    }

    /// <summary>
    /// The organization's identity issuer: the single issuer of its enabled owners (bound by the operator plane at bootstrap
    /// from the organization's configured Microsoft Entra tenant). Null when it is not unambiguous — then nobody can be added.
    /// </summary>
    public static string? OrganizationIdentityIssuer(DesiredStateGraph graph, Guid organizationId, IEnumerable<IdentityBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issuers = bindings.Where(b => b.IdentityBindingEnabled && graph.HasOrganizationRole(organizationId, b.AuthorityUserId, OrganizationRoleNames.OrganizationOwner))
            .Select(b => b.IdentityIssuerUri).Distinct(StringComparer.Ordinal).ToList();
        return issuers.Count == 1 ? issuers[0] : null;
    }

    public static bool IsEntraIssuer(string issuer) =>
        issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase) || issuer.StartsWith("https://sts.windows.net/", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidSubject(string issuer, string subject) =>
        IsEntraIssuer(issuer) ? Guid.TryParseExact(subject, "D", out _) : SubjectPattern().IsMatch(subject);

    private static void RequireOwnerActor(DesiredStateGraph graph, Guid organizationId, BoundActor actor)
    {
        if (!graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.OrganizationOwner))
        {
            throw AuthorityException.Forbidden("membership and organization roles are administered by organization owners only");
        }
    }

    private static void RequireNotSelf(BoundActor actor, Guid target)
    {
        if (target == Guid.Empty)
        {
            throw AuthorityException.Invalid("authority_user_id is required");
        }

        if (target == actor.AuthorityUserId)
        {
            throw new AuthorityException(AuthorityErrorCodes.SelfChangeForbidden, 403, false, "nobody may change their own roles or membership");
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
        {
            throw AuthorityException.Invalid("a reason of 1 to 500 characters is required");
        }
    }

    private static async Task<Dictionary<Guid, IdentityBinding>> EnabledUsersAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken) =>
        (await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false)).Where(b => b.IdentityBindingEnabled).ToDictionary(b => b.AuthorityUserId);

    private static void RequireAnotherEnabledOwner(DesiredStateGraph graph, Guid organizationId, Dictionary<Guid, IdentityBinding> enabled, Guid target)
    {
        var remaining = graph.EdgesOf(ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.OrganizationOwner)
            .Count(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId != target && enabled.ContainsKey(e.SubjectEntityId));
        if (remaining == 0)
        {
            throw new AuthorityException(AuthorityErrorCodes.LastOwner, 409, false, "the last enabled organization owner cannot be removed or disabled");
        }
    }

    /// <summary>Last enabled Security Administrator and direct record ownership (C-78: refuse until reassigned; count only).</summary>
    private static async Task RequireDisableAllowedAsync(IAuthorityScope scope, Guid organizationId, DesiredStateGraph graph, Dictionary<Guid, IdentityBinding> enabled, Guid target, CancellationToken cancellationToken)
    {
        if (graph.HasOrganizationRole(organizationId, target, OrganizationRoleNames.SystemAdministrator)
            && !graph.EdgesOf(ResourceTypeNames.Organization, organizationId, OrganizationRoleNames.SystemAdministrator).Any(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId != target && enabled.ContainsKey(e.SubjectEntityId)))
        {
            throw new AuthorityException(AuthorityErrorCodes.LastSecurityAdministrator, 409, false, "the last enabled Security Administrator cannot be disabled; appoint another one first");
        }

        var directlyOwned = graph.Edges.Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.RelationshipName == "owner" && e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == target)
            .Select(e => e.ResourceEntityId).ToHashSet();
        if (directlyOwned.Count > 0)
        {
            var active = (await scope.GetActiveRecordIdsAsync(organizationId, cancellationToken).ConfigureAwait(false)).Count(directlyOwned.Contains);
            if (active > 0)
            {
                throw new AuthorityException(AuthorityErrorCodes.OwnsRecords, 409, false, $"the person directly owns {active} active record(s); transfer their ownership before disabling the membership");
            }
        }
    }

    /// <summary>
    /// Two distinct owners: another owner's pending, unexpired proposal for exactly this change and person, made by an owner
    /// who is still an enabled owner now. The executing owner is never the proposer (and never the person, checked earlier).
    /// </summary>
    private static async Task<OrganizationRoleChangeProposal> RequireProposalAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, Guid? proposalId, string change, Guid target, DesiredStateGraph graph,
        Dictionary<Guid, IdentityBinding> enabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (proposalId is not { } id || id == Guid.Empty)
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerApprovalRequired, 403, false, "this change needs two distinct owners: one proposes it and another approves it");
        }

        var proposal = await scope.FindRoleChangeProposalAsync(organizationId, id, cancellationToken).ConfigureAwait(false)
                       ?? throw new AuthorityException(AuthorityErrorCodes.ProposalNotPending, 409, false, "the proposal does not exist in this organization");
        if (proposal.ProposalStateName != ProposalStates.Pending)
        {
            throw new AuthorityException(AuthorityErrorCodes.ProposalNotPending, 409, false, "the proposal was already used");
        }

        if (proposal.ProposalExpiresOn <= now)
        {
            throw new AuthorityException(AuthorityErrorCodes.ProposalExpired, 409, false, "the proposal expired; propose the change again");
        }

        if (proposal.ProposedChangeName != change || proposal.TargetAuthorityUserId != target)
        {
            throw new AuthorityException(AuthorityErrorCodes.ProposalMismatch, 422, false, "the proposal is for a different person or change");
        }

        if (proposal.ProposedByAuthorityUserId == actor.AuthorityUserId)
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerApprovalRequired, 403, false, "a different owner must approve this proposal");
        }

        if (!enabled.ContainsKey(proposal.ProposedByAuthorityUserId) || !graph.HasOrganizationRole(organizationId, proposal.ProposedByAuthorityUserId, OrganizationRoleNames.OrganizationOwner))
        {
            throw new AuthorityException(AuthorityErrorCodes.OwnerApprovalRequired, 403, false, "the proposing owner is no longer an enabled owner; propose the change again");
        }

        return proposal;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    private static partial Regex SubjectPattern();
}
