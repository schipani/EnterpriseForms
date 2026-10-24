using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>Phase C7 M2 — AC-6 commands (C-56 D2; C-60 OD-2, OD-3, OD-4, OD-7). See FieldSearchRules for eligibility.</summary>
public static partial class CommandPlanner
{
    /// <summary>
    /// OD-3: records a newly published form version (designer of the form or an organization administrator). Version
    /// numbers only increase; a field the Authority knows to be encrypted cannot be declared Standard. Active approvals of
    /// fields whose recorded entry changed, or that are no longer in the version, are invalidated in the same revision.
    /// </summary>
    private static async Task<CommandPlan> RegisterPublishedFormVersionAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<RegisterPublishedFormVersionPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!graph.CanDesignForm(actor.AuthorityUserId, payload.FormDefinitionId) && !IsOrganizationAdministrator(graph, organizationId, actor.AuthorityUserId))
        {
            throw AuthorityException.Forbidden("registering a published form version requires design permission on the form");
        }

        if (payload.FormVersionNumber < 1 || payload.LayoutDigestHex is not { Length: 64 } || payload.Fields is null || payload.Fields.Count > 1000
            || payload.Fields.Select(f => f.FieldDefinitionId).Distinct().Count() != payload.Fields.Count)
        {
            throw AuthorityException.Invalid("form_version_number >= 1, a 64-hex layout digest and unique field entries (at most 1000) are required");
        }

        byte[] digest;
        try
        {
            digest = Convert.FromHexString(payload.LayoutDigestHex);
        }
        catch (FormatException)
        {
            throw AuthorityException.Invalid("layout_digest_hex must be hexadecimal");
        }

        foreach (var field in payload.Fields)
        {
            FieldSearchRules.ValidateEntry(field);
            var facts = await FieldSearchRules.ReadProtectionFactsAsync(scope, organizationId, field.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
            if (facts.RequirementActive && field.ProtectionMode != "ApplicationEncrypted")
            {
                throw new AuthorityException(AuthorityErrorCodes.FormVersionUntrusted, 422, false, $"field {field.FieldDefinitionId} is encrypted according to the Authority but was published as Standard");
            }
        }

        var latest = await scope.FindLatestPublishedFormVersionAsync(organizationId, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (latest is not null && payload.FormVersionNumber <= latest.FormVersionNumber)
        {
            throw new AuthorityException(AuthorityErrorCodes.FormVersionUntrusted, 409, false, "a version with this number or a later one is already registered");
        }

        var manifest = FieldSearchRules.WriteManifest(payload.Fields);
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        plan.PublishedVersion = new PublishedFormVersion
        {
            PublishedFormVersionId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            FormDefinitionId = payload.FormDefinitionId,
            FormVersionNumber = payload.FormVersionNumber,
            LayoutDigest = digest,
            FieldManifestJson = manifest,
        };

        var entries = payload.Fields.ToDictionary(f => f.FieldDefinitionId);
        foreach (var approval in await ActiveApprovalsAsync(scope, organizationId, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false))
        {
            if (!entries.TryGetValue(approval.FieldDefinitionId, out var entry) || !SameApprovedEntry(approval, entry))
            {
                Invalidate(plan, approval, FieldSearchInvalidationReasons.FormVersionChanged);
            }
            else
            {
                approval.ApprovedFormVersionNumber = payload.FormVersionNumber; // unchanged entry: the approval carries over to the new version
                approval.ApprovedLayoutDigest = digest;
                plan.ChangedApprovals.Add(approval);
            }
        }

        await AdvanceSearchPolicyAsync(scope, organizationId, plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// AC-6 approval. Only <c>administer_security</c> (OD-2 A); step-up gate first (OD-7); then only Authority-held facts:
    /// the latest registered version's entry for the field, the organization policy and the field's protection resources.
    /// </summary>
    private static async Task<CommandPlan> ApproveFieldSearchAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, FieldSearchApprovalGate gate, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<ApproveFieldSearchPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        if (!graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.SystemAdministrator))
        {
            throw AuthorityException.Forbidden("approving field search requires administer_security");
        }

        gate.RequireStepUp(payload.StepUpEvidence);
        if (string.IsNullOrWhiteSpace(payload.ApprovalReference) || payload.ApprovalReference.Length > 200)
        {
            throw AuthorityException.Invalid("approval_reference (1..200 characters) identifying the governance decision is required");
        }

        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        var latest = await scope.FindLatestPublishedFormVersionAsync(organizationId, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false)
                     ?? throw new AuthorityException(AuthorityErrorCodes.FormVersionUntrusted, 422, false, "no published version of this form is registered with the Authority; publish the form again");
        var entry = FieldSearchRules.ReadManifest(latest).FirstOrDefault(f => f.FieldDefinitionId == payload.FieldDefinitionId)
                    ?? throw new AuthorityException(AuthorityErrorCodes.FormVersionUntrusted, 422, false, "the field is not part of the latest registered version of the form");
        var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var facts = await FieldSearchRules.ReadProtectionFactsAsync(scope, organizationId, payload.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var eligibility = FieldSearchRules.Eligibility(entry, policy, facts);
        if (eligibility != FieldSearchEligibility.Eligible)
        {
            throw new AuthorityException(AuthorityErrorCodes.FieldSearchNotEligible, 422, false, eligibility);
        }

        var permitted = FieldSearchRules.PermittedOperators(entry.FieldDataType);
        if (payload.Operators is not { Count: > 0 } || payload.Operators.Distinct(StringComparer.Ordinal).Count() != payload.Operators.Count || payload.Operators.Any(o => !permitted.Contains(o)))
        {
            throw new AuthorityException(AuthorityErrorCodes.FieldSearchNotEligible, 422, false, "OPERATOR_NOT_APPLICABLE");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { ApprovalReference = payload.ApprovalReference.Trim(), StepUpEvidence = null }) };
        foreach (var previous in await ActiveApprovalsAsync(scope, organizationId, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false))
        {
            if (previous.FieldDefinitionId == payload.FieldDefinitionId)
            {
                // A re-approval (e.g. different operators) supersedes the previous one.
                previous.ApprovalStateName = FieldSearchApprovalStates.Withdrawn;
                plan.ChangedApprovals.Add(previous);
            }
        }

        plan.NewApprovals.Add(new FieldSearchApproval
        {
            FieldSearchApprovalId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            FormDefinitionId = payload.FormDefinitionId,
            FieldDefinitionId = payload.FieldDefinitionId,
            ApprovedFormVersionNumber = latest.FormVersionNumber,
            ApprovedLayoutDigest = latest.LayoutDigest,
            ApprovedFieldDataType = entry.FieldDataType,
            ApprovedFieldClassification = entry.FieldClassification,
            ApprovedProtectionMode = entry.ProtectionMode,
            ApprovedOperatorsJson = JsonSerializer.Serialize(payload.Operators.OrderBy(o => o, StringComparer.Ordinal).ToList()),
            ApprovedFieldLabelSnapshot = entry.FieldLabel,
            ApprovedOptionSnapshotJson = FieldSearchRules.WriteOptions(entry.Options),
            ApprovalReferenceText = payload.ApprovalReference.Trim(),
            ApprovalStateName = FieldSearchApprovalStates.Active,
        });
        await AdvanceSearchPolicyAsync(scope, organizationId, plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    private static async Task<CommandPlan> WithdrawFieldSearchAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<WithdrawFieldSearchPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        if (!graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.SystemAdministrator))
        {
            throw AuthorityException.Forbidden("withdrawing a field search approval requires administer_security");
        }

        if (string.IsNullOrWhiteSpace(payload.WithdrawalReason) || payload.WithdrawalReason.Length > 500)
        {
            throw AuthorityException.Invalid("withdrawal_reason (1..500 characters) is required");
        }

        var approval = (await ActiveApprovalsAsync(scope, organizationId, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.FieldDefinitionId == payload.FieldDefinitionId)
                       ?? throw AuthorityException.Invalid("the field has no active search approval");
        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload with { WithdrawalReason = payload.WithdrawalReason.Trim() }) };
        approval.ApprovalStateName = FieldSearchApprovalStates.Withdrawn;
        plan.ChangedApprovals.Add(approval);
        await AdvanceSearchPolicyAsync(scope, organizationId, plan, cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>OD-4: the per-form field_searcher relation. Granted and revoked by administer_security only (the same principals as approvals).</summary>
    private static async Task<CommandPlan> UpdateFieldSearchersAsync(IAuthorityScope scope, Guid organizationId, BoundActor actor, SecurityCommandRequest request, DesiredStateGraph graph, CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<UpdateFieldSearchersPayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormDefinition, payload.FormDefinitionId);
        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormDefinition, payload.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (!graph.HasOrganizationRole(organizationId, actor.AuthorityUserId, OrganizationRoleNames.SystemAdministrator))
        {
            throw AuthorityException.Forbidden("granting field search access requires administer_security");
        }

        if (payload.GrantUserIds is null || payload.GrantTeamIds is null || payload.RevokeUserIds is null || payload.RevokeTeamIds is null)
        {
            throw AuthorityException.Invalid("grant_user_ids, grant_team_ids, revoke_user_ids and revoke_team_ids are required (may be empty)");
        }

        var all = payload.GrantUserIds.Concat(payload.GrantTeamIds).Concat(payload.RevokeUserIds).Concat(payload.RevokeTeamIds).ToList();
        if (all.Count == 0 || all.Count > 100 || all.Distinct().Count() != all.Count)
        {
            throw AuthorityException.Invalid("between 1 and 100 distinct grants or revocations are required");
        }

        var plan = new CommandPlan { NormalizedPayloadJson = CanonicalJson.Serialize(payload) };
        var existing = graph.EdgesOf(ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "field_searcher").ToList();
        foreach (var user in payload.GrantUserIds)
        {
            await RequireUserBindingAsync(scope, organizationId, user, cancellationToken).ConfigureAwait(false);
            if (!existing.Any(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == user))
            {
                plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "field_searcher", ResourceTypeNames.User, user, string.Empty));
            }
        }

        foreach (var team in payload.GrantTeamIds)
        {
            await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.Team, team, cancellationToken).ConfigureAwait(false);
            if (!existing.Any(e => e.SubjectTypeName == ResourceTypeNames.Team && e.SubjectEntityId == team))
            {
                plan.AddEdges.Add((ResourceTypeNames.FormDefinition, payload.FormDefinitionId, "field_searcher", ResourceTypeNames.Team, team, "participant"));
            }
        }

        foreach (var user in payload.RevokeUserIds)
        {
            plan.RemoveEdges.AddRange(existing.Where(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectEntityId == user));
        }

        foreach (var team in payload.RevokeTeamIds)
        {
            plan.RemoveEdges.AddRange(existing.Where(e => e.SubjectTypeName == ResourceTypeNames.Team && e.SubjectEntityId == team));
        }

        if (plan.AddEdges.Count == 0 && plan.RemoveEdges.Count == 0)
        {
            throw AuthorityException.Invalid("nothing to change");
        }

        return plan;
    }

    /// <summary>Strengthening and policy hooks: every approval that no longer conforms is invalidated in the same revision.</summary>
    private static async Task InvalidateNonConformingApprovalsAsync(IAuthorityScope scope, Guid organizationId, OrganizationProtectionPolicy policy, CommandPlan plan, CancellationToken cancellationToken)
    {
        foreach (var approval in await ActiveApprovalsAsync(scope, organizationId, null, cancellationToken).ConfigureAwait(false))
        {
            if (!policy.PlaintextSearchPermitted || !FieldSearchRules.ClassificationPermitted(approval.ApprovedFieldClassification, policy))
            {
                Invalidate(plan, approval, FieldSearchInvalidationReasons.OrganizationPolicyChanged);
            }
        }
    }

    private static async Task InvalidateFieldApprovalAsync(IAuthorityScope scope, Guid organizationId, Guid formDefinitionId, Guid fieldDefinitionId, CommandPlan plan, CancellationToken cancellationToken)
    {
        foreach (var approval in (await ActiveApprovalsAsync(scope, organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(a => a.FieldDefinitionId == fieldDefinitionId))
        {
            Invalidate(plan, approval, FieldSearchInvalidationReasons.FieldStrengthened);
        }

        if (plan.ChangedApprovals.Count > 0)
        {
            await AdvanceSearchPolicyAsync(scope, organizationId, plan, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<FieldSearchApproval>> ActiveApprovalsAsync(IAuthorityScope scope, Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken) =>
        (await scope.GetFieldSearchApprovalsAsync(organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active).ToList();

    private static void Invalidate(CommandPlan plan, FieldSearchApproval approval, string reason)
    {
        approval.ApprovalStateName = FieldSearchApprovalStates.Invalidated;
        approval.InvalidationReasonCode = reason;
        plan.ChangedApprovals.Add(approval);
    }

    private static bool SameApprovedEntry(FieldSearchApproval approval, PublishedFieldEntry entry) =>
        approval.ApprovedFieldDataType == entry.FieldDataType
        && approval.ApprovedFieldClassification == entry.FieldClassification
        && approval.ApprovedProtectionMode == entry.ProtectionMode
        && approval.ApprovedFieldLabelSnapshot == entry.FieldLabel
        && FieldSearchRules.SameOptions(approval, entry.Options);

    private static async Task AdvanceSearchPolicyAsync(IAuthorityScope scope, Guid organizationId, CommandPlan plan, CancellationToken cancellationToken)
    {
        var policy = plan.ProtectionPolicy ?? await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (policy is not null)
        {
            plan.SearchPolicyToAdvance = policy;
        }
    }
}
