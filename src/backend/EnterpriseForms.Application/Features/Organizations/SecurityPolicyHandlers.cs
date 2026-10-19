using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.Protection;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Organizations;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// Organization data-protection policy (ADR-PB-004 §Binding decision 2). Any member may read it (the designer dialog
/// explains the rules with it); only the organization owner (authority manage_organization) may change it, with a
/// reason, audited. The policy can be tightened freely; loosening is recorded as such in the audit metadata.
/// </summary>
public sealed class SecurityPolicyHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public const string DevelopmentDefaultStatement = "Development default, not yet reviewed: Internal fields may be stored in plaintext (readable by a database administrator); new free text and documents are Confidential and encrypted.";

    public async Task<OrganizationSecurityPolicyResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var policy = await EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(policy);
    }

    public async Task<OrganizationSecurityPolicyResponse> UpdateAsync(RequestContext context, UpdateOrganizationSecurityPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "only the organization owner may change the data-protection policy");
        }

        _ = FieldProtectionPolicy.Rank(request.MinimumFieldClassification ?? string.Empty);
        var statement = request.PolicyStatementText?.Trim() ?? string.Empty;
        if (statement.Length is < 10 or > 2000)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a policy statement of 10..2000 characters is required (why plaintext is or is not acceptable for this organization)");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var policy = await EnsureAsync(scope, context.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (policy.SecurityPolicyRevision != request.ExpectedPolicyRevision)
        {
            throw new BusinessException("POLICY_REVISION_CONFLICT", 409, "the policy was changed by someone else; reload");
        }

        var loosened = FieldProtectionPolicy.Rank(request.MinimumFieldClassification!) < FieldProtectionPolicy.Rank(policy.MinimumFieldClassification)
                       || (request.PlaintextInternalFieldsPermitted && !policy.PlaintextInternalFieldsPermitted);
        var now = clock.GetUtcNow();
        var previous = new { policy.MinimumFieldClassification, policy.PlaintextInternalFieldsPermitted };
        policy.MinimumFieldClassification = request.MinimumFieldClassification!;
        policy.PlaintextInternalFieldsPermitted = request.PlaintextInternalFieldsPermitted;
        policy.PolicyStatementText = statement;
        policy.PolicyReviewedOn = now;
        policy.PolicyReviewedByUserId = context.LocalUserId;
        policy.SecurityPolicyRevision++;
        policy.PolicyModifiedOn = now;
        policy.PolicyModifiedByUserId = context.LocalUserId;
        AuditRecorder.Record(scope, context, clock, AuditCategories.Security, AuditActions.OrganizationSecurityPolicyUpdated, loosened ? "organization data-protection policy loosened by the organization owner" : "organization data-protection policy reviewed/tightened by the organization owner", metadata: new { previous, minimumFieldClassification = policy.MinimumFieldClassification, plaintextInternalFieldsPermitted = policy.PlaintextInternalFieldsPermitted, loosened, revision = policy.SecurityPolicyRevision });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(policy);
    }

    /// <summary>Reads the policy, adding the documented development default to the scope on first use (never silently stronger or weaker than documented). The caller commits.</summary>
    public static async Task<OrganizationSecurityPolicy> EnsureAsync(IBusinessScope scope, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var policy = await scope.FindSecurityPolicyAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            policy = new OrganizationSecurityPolicy
            {
                OrganizationSecurityPolicyId = Guid.CreateVersion7(),
                TenantId = tenantId,
                MinimumFieldClassification = "Internal",
                PlaintextInternalFieldsPermitted = true,
                PolicyStatementText = DevelopmentDefaultStatement,
                SecurityPolicyRevision = 1,
                PolicyModifiedOn = DateTimeOffset.UtcNow,
            };
            scope.AddSecurityPolicy(policy); // persisted by the caller's commit; a rolled-back caller simply recreates the same default next time
        }

        return policy;
    }

    private static OrganizationSecurityPolicyResponse ToResponse(OrganizationSecurityPolicy p) =>
        new(p.MinimumFieldClassification, p.PlaintextInternalFieldsPermitted, p.PolicyStatementText, p.PolicyReviewedOn, p.PolicyReviewedByUserId, p.SecurityPolicyRevision, p.PolicyReviewedOn is null);
}
