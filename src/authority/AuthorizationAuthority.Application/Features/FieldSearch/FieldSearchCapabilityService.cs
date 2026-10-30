using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.FieldSearch;

/// <summary>
/// AC-6 §4.6 (D5): what a caller may search on a form, and — only for designers of the form and security administrators —
/// the search configuration of its fields. Everything comes from Authority-held facts (approvals with their frozen labels
/// and options, the registered published version, the organization policy, protection resources) and from the caller's
/// permissions at one authorization revision. A caller without query permission, an unknown form and a form without
/// approvals all get the same empty field list; configuration is never returned to other callers.
/// </summary>
public sealed class FieldSearchCapabilityService(IAuthorityStore store, PermissionCheckService permissions, FieldSearchApprovalGate gate)
{
    public async Task<FieldSearchCapabilitiesResponse> GetAsync(VerifiedActor actor, FieldSearchCapabilitiesRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FormDefinitionId == Guid.Empty)
        {
            throw AuthorityException.Invalid("form_definition_id is required");
        }

        var decisions = await permissions.CheckBatchAsync(actor,
        [
            new PermissionCheckRequest(request.OrganizationId, new ResourceRef(ResourceKind.FormDefinition, request.FormDefinitionId), RequestedAction.QueryFields),
            new PermissionCheckRequest(request.OrganizationId, new ResourceRef(ResourceKind.FormDefinition, request.FormDefinitionId), RequestedAction.DesignForm),
            new PermissionCheckRequest(request.OrganizationId, new ResourceRef(ResourceKind.Organization, request.OrganizationId), RequestedAction.AdministerSecurity),
        ], correlationId, cancellationToken).ConfigureAwait(false);
        var (mayQuery, mayDesign, mayAdminister) = (decisions[0].Allowed, decisions[1].Allowed, decisions[2].Allowed);
        var revision = decisions[0].AuthorizationRevision;

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var policy = await scope.FindProtectionPolicyAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        var fields = new List<SearchableFieldCapability>();
        FieldSearchConfiguration? configuration = null;
        // administer_security is an organization permission: it shows configuration only for a form registered and active
        // in the caller's own organization. Anything else gets the same empty answer as an unknown form.
        var form = await scope.FindResourceAsync(request.OrganizationId, ResourceTypeNames.FormDefinition, request.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        if (form is { ResourceLifecycleState: ResourceLifecycleStates.Active } && (mayQuery || mayDesign || mayAdminister))
        {
            var approvals = (await scope.GetFieldSearchApprovalsAsync(request.OrganizationId, request.FormDefinitionId, cancellationToken).ConfigureAwait(false)).ToList();
            var active = approvals.Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active).ToList();
            if (mayQuery && policy is { PlaintextSearchPermitted: true })
            {
                fields.AddRange(active.OrderBy(a => a.ApprovedFieldLabelSnapshot, StringComparer.Ordinal).ThenBy(a => a.FieldDefinitionId)
                    .Select(a => new SearchableFieldCapability(a.FieldDefinitionId, a.ApprovedFieldLabelSnapshot, a.ApprovedFieldDataType, FieldSearchRules.ReadOperators(a), FieldSearchRules.ReadOptions(a), a.ApprovedFormVersionNumber)));
            }

            if (mayDesign || mayAdminister)
            {
                var latest = await scope.FindLatestPublishedFormVersionAsync(request.OrganizationId, request.FormDefinitionId, cancellationToken).ConfigureAwait(false);
                var statuses = new List<FieldSearchStatus>();
                foreach (var entry in latest is null ? [] : FieldSearchRules.ReadManifest(latest))
                {
                    var facts = await FieldSearchRules.ReadProtectionFactsAsync(scope, request.OrganizationId, entry.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
                    var current = approvals.Where(a => a.FieldDefinitionId == entry.FieldDefinitionId).OrderByDescending(a => a.ApprovedOn).FirstOrDefault();
                    statuses.Add(new FieldSearchStatus(entry.FieldDefinitionId, entry.FieldLabel, entry.FieldDataType, entry.FieldClassification, facts.RequirementActive ? "ApplicationEncrypted" : entry.ProtectionMode,
                        FieldSearchRules.Eligibility(entry, policy, facts), current?.ApprovalStateName ?? "None", current?.InvalidationReasonCode,
                        current is { ApprovalStateName: FieldSearchApprovalStates.Active } ? FieldSearchRules.ReadOperators(current) : [], FieldSearchRules.PermittedOperators(entry.FieldDataType)));
                }

                IReadOnlyList<FormAccessSubject>? searchers = null;
                if (mayAdminister)
                {
                    var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false));
                    searchers = graph.EdgesOf(ResourceTypeNames.FormDefinition, request.FormDefinitionId, "field_searcher")
                        .Select(e => new FormAccessSubject(e.SubjectTypeName == ResourceTypeNames.Team ? "Team" : "User", e.SubjectEntityId))
                        .OrderBy(s => s.SubjectKind, StringComparer.Ordinal).ThenBy(s => s.SubjectId).ToList();
                }

                configuration = new FieldSearchConfiguration(mayAdminister, gate.ApprovalsOperational, gate.UnavailableReason, latest?.FormVersionNumber, statuses.OrderBy(s => s.FieldLabel, StringComparer.Ordinal).ToList(), searchers);
            }
        }

        var after = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (after is null || after.AuthorizationRevision != revision)
        {
            throw AuthorityException.NotReady("authorization state moved while reading field search capabilities");
        }

        return new FieldSearchCapabilitiesResponse(request.FormDefinitionId, policy?.PlaintextSearchPermitted ?? false, policy?.SearchPolicyRevision ?? 0, revision, fields, configuration, correlationId);
    }

    /// <summary>
    /// Phase C7 M3: search-approval administration for security administrators. Lists the forms of the caller's organization
    /// that have an Authority-registered published version (latest version, registered title, field and approval counts),
    /// so approvals can be administered without Designer or record access. Only <c>administer_security</c>; nothing from
    /// business rows; forms whose resource is not Active are omitted.
    /// </summary>
    public async Task<FieldSearchAdministrationResponse> AdministerAsync(VerifiedActor actor, FieldSearchAdministrationRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decisions = await permissions.CheckBatchAsync(actor,
            [new PermissionCheckRequest(request.OrganizationId, new ResourceRef(ResourceKind.Organization, request.OrganizationId), RequestedAction.AdministerSecurity)], correlationId, cancellationToken).ConfigureAwait(false);
        if (!decisions[0].Allowed)
        {
            throw AuthorityException.Forbidden("search-approval administration requires administer_security");
        }

        var revision = decisions[0].AuthorizationRevision;
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var policy = await scope.FindProtectionPolicyAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        var approvals = (await scope.GetFieldSearchApprovalsAsync(request.OrganizationId, null, cancellationToken).ConfigureAwait(false)).ToList();
        var forms = new List<FieldSearchAdministrationForm>();
        foreach (var version in await scope.GetLatestPublishedFormVersionsAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false))
        {
            var form = await scope.FindResourceAsync(request.OrganizationId, ResourceTypeNames.FormDefinition, version.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            if (form is not { ResourceLifecycleState: ResourceLifecycleStates.Active })
            {
                continue;
            }

            var entries = FieldSearchRules.ReadManifest(version);
            var eligible = 0;
            foreach (var entry in entries)
            {
                var facts = await FieldSearchRules.ReadProtectionFactsAsync(scope, request.OrganizationId, entry.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
                if (FieldSearchRules.Eligibility(entry, policy, facts) == FieldSearchEligibility.Eligible)
                {
                    eligible++;
                }
            }

            var formApprovals = approvals.Where(a => a.FormDefinitionId == version.FormDefinitionId).ToList();
            forms.Add(new FieldSearchAdministrationForm(version.FormDefinitionId, version.FormTitleSnapshot, version.FormVersionNumber, version.RegisteredOn, entries.Count, eligible,
                formApprovals.Count(a => a.ApprovalStateName == FieldSearchApprovalStates.Active), formApprovals.Count(a => a.ApprovalStateName == FieldSearchApprovalStates.Invalidated)));
        }

        var after = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (after is null || after.AuthorizationRevision != revision)
        {
            throw AuthorityException.NotReady("authorization state moved while reading search-approval administration");
        }

        return new FieldSearchAdministrationResponse(policy?.PlaintextSearchPermitted ?? false, gate.ApprovalsOperational, gate.UnavailableReason,
            forms.OrderBy(f => f.FormTitle ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.FormDefinitionId).ToList(), revision, correlationId);
    }
}
