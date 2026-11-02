using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.OrganizationOnboarding;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.OperatorPlane;

/// <summary>
/// Operator-plane provisioning: organization state, independent identity bindings, organization roles and the
/// organization's integrity key (provisioned in the provider, never exported). Stage A stands in for the production
/// PIM/dual-control workflow; it is reachable only with the operator credential.
/// <para>
/// C8 onboarding (C-104, D8-60): restricted to the approved development seed and test-harness fixtures
/// (<c>OrganizationOnboarding:LegacyOrganizationBootstrapEnabled</c>, Development/Testing only). New organizations are created through
/// Platform Console onboarding. A repeated call for an existing organization succeeds only when it repeats the original bootstrap
/// exactly (display name, identities and their bootstrap roles); any other input is refused <c>IDEMPOTENCY_CONFLICT</c> instead of the
/// former false success (OB-F2). An organization id reserved by an onboarding request is refused.
/// </para>
/// </summary>
public sealed class OrganizationBootstrapService(IAuthorityStore store, IIntegrityMacProvider macProvider, ISpiceDbProjection spiceDb, OrganizationOnboardingPolicy onboardingPolicy, TimeProvider clock)
{
    public async Task<BootstrapOrganizationResponse> BootstrapAsync(BootstrapOrganizationRequest request, string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        onboardingPolicy.RequireLegacyBootstrap();
        if (request.OrganizationId == Guid.Empty || request.Identities.Count == 0 || request.Identities.Count > 100)
        {
            throw AuthorityException.Invalid("organization id and 1..100 identities are required");
        }

        var owners = request.Identities.Where(i => i.OrganizationRoles.Contains(OrganizationRoleNames.OrganizationOwner, StringComparer.Ordinal)).ToList();
        if (owners.Count == 0)
        {
            throw AuthorityException.Invalid("at least one organization_owner identity is required");
        }

        if (owners.Count > OrganizationOwnerLimits.MaximumActiveOwners)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationOwnerLimit, 409, false, $"an organization has at most {OrganizationOwnerLimits.MaximumActiveOwners} owners");
        }

        foreach (var identity in request.Identities)
        {
            if (identity.AuthorityUserId == Guid.Empty || string.IsNullOrWhiteSpace(identity.IssuerUri) || string.IsNullOrWhiteSpace(identity.SubjectIdentifier)
                || identity.OrganizationRoles.Any(r => r is not (OrganizationRoleNames.OrganizationOwner or OrganizationRoleNames.SystemAdministrator or OrganizationRoleNames.Designer)))
            {
                throw AuthorityException.Invalid("invalid identity or role");
            }
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var existing = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await RequireSameBootstrapAsync(scope, existing, request, cancellationToken).ConfigureAwait(false);
            var key = await scope.FindKeyRegistrationAsync(request.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false);
            return new BootstrapOrganizationResponse(existing.OrganizationId, existing.AuthorizationRevision, Guid.Empty, key?.ProviderKeyName ?? string.Empty, key?.CurrentSigningKeyVersion ?? 0, correlationId);
        }

        if (await scope.FindOnboardingRequestByOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationIdReserved, 409, false, "this organization id is reserved by an onboarding request");
        }

        var now = clock.GetUtcNow();
        var state = new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(),
            OrganizationId = request.OrganizationId,
            OrganizationDisplayName = request.OrganizationDisplayName,
            AuthorizationRevision = 0,
            ProjectionAppliedRevision = 0,
            ContentIntegrityRevision = 0,
            CheckpointEnforcementModeName = CheckpointEnforcementModes.RequiredBeforeEffective,
            StateModifiedOn = now,
        };
        scope.AddOrganization(state);

        var bindings = new List<IdentityBinding>();
        foreach (var identity in request.Identities)
        {
            var binding = new IdentityBinding
            {
                IdentityBindingId = Guid.CreateVersion7(),
                OrganizationId = request.OrganizationId,
                AuthorityUserId = identity.AuthorityUserId,
                IdentityIssuerUri = identity.IssuerUri,
                IdentitySubjectIdentifier = identity.SubjectIdentifier,
                IdentityBindingEnabled = true,
                IdentityCreatedOn = now,
            };
            scope.AddIdentityBinding(binding);
            bindings.Add(binding);
        }

        var keyName = IntegrityKeyNaming.ProviderKeyName(request.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1);
        int version;
        try
        {
            version = await macProvider.ProvisionKeyAsync(request.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, keyName, cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrityProviderUnavailableException ex)
        {
            throw new AuthorityException(AuthorityErrorCodes.SigningUnavailable, 503, true, ex.Message);
        }

        scope.AddKeyRegistration(new IntegrityKeyRegistration
        {
            IntegrityKeyRegistrationId = Guid.CreateVersion7(),
            OrganizationId = request.OrganizationId,
            KeyPurposeName = IntegrityKeyPurposes.RowIntegrityV1,
            ProviderName = macProvider.ProviderName,
            ProviderKeyName = keyName,
            CurrentSigningKeyVersion = version,
            MinimumVerificationKeyVersion = 1,
            KeyRegisteredOn = now,
            KeyModifiedOn = now,
        });

        scope.AddResource(new AuthorityResource
        {
            AuthorityResourceId = Guid.CreateVersion7(),
            OrganizationId = request.OrganizationId,
            ResourceTypeName = ResourceTypeNames.Organization,
            ResourceEntityId = request.OrganizationId,
            ResourceLifecycleState = ResourceLifecycleStates.Active,
            RegisteredBySecurityCommandId = Guid.Empty,
            ResourceRegisteredOn = now,
        });

        // Revision 1: organization roles, journaled as a command by the first organization owner on behalf of the operator.
        var ownerBinding = bindings.First(b => b.AuthorityUserId == owners[0].AuthorityUserId);
        var actor = new BoundActor(new VerifiedActor(ownerBinding.IdentityIssuerUri, ownerBinding.IdentitySubjectIdentifier, operatorName), ownerBinding);
        var payload = new BootstrapPayload(request.Identities.Select(i => new BootstrapRole(i.AuthorityUserId, i.OrganizationRoles.ToList())).ToList(), operatorName);
        var payloadJson = CanonicalJson.Serialize(payload);
        var commandRequest = new SecurityCommandRequest(request.OrganizationId, SecurityCommandKind.GrantSystemRole, new ResourceRef(ResourceKind.Organization, request.OrganizationId), 0, System.Text.Json.JsonDocument.Parse(payloadJson).RootElement, "operator bootstrap");
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = request.OrganizationId,
            ActorIdentityBindingId = ownerBinding.IdentityBindingId,
            CommandKindName = "BootstrapOrganization",
            CommandIdempotencyKey = "bootstrap:" + request.OrganizationId.ToString("N"),
            CommandRequestDigest = CanonicalJson.Digest(payloadJson),
            ExpectedAuthorizationRevision = 0,
            CommandPayloadJson = payloadJson,
            CommandReasonText = "operator bootstrap by " + operatorName,
            CorrelationIdentifier = correlationId,
            CommandCreatedOn = now,
            CommandModifiedOn = now,
        };
        var plan = new CommandPlan { NormalizedPayloadJson = payloadJson };
        foreach (var identity in request.Identities)
        {
            foreach (var role in identity.OrganizationRoles.Distinct(StringComparer.Ordinal))
            {
                plan.AddEdges.Add((ResourceTypeNames.Organization, request.OrganizationId, role, ResourceTypeNames.User, identity.AuthorityUserId, string.Empty));
            }
        }

        SecurityCommandService.ApplyPlan(scope, state, command, plan, actor, commandRequest, cancellationToken);

        // SpiceDB cursor marker for step 0 must exist before revision 1 can be projected; idempotent (MUST_NOT_MATCH existing marker).
        try
        {
            await spiceDb.InitializeCursorAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiceDbUnavailableException ex)
        {
            throw AuthorityException.NotReady("SpiceDB unavailable: " + ex.Message);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BootstrapOrganizationResponse(request.OrganizationId, state.AuthorizationRevision, command.SecurityCommandId, keyName, version, correlationId);
    }

    public async Task<OrganizationGateStatus> GetGateStatusAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var gate = await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false);
        var pending = await scope.CountPendingArchiveAsync(organizationId, Domain.Features.Journal.JournalStreamNames.Authorization, cancellationToken).ConfigureAwait(false)
                      + await scope.CountPendingArchiveAsync(organizationId, Domain.Features.Journal.JournalStreamNames.Content, cancellationToken).ConfigureAwait(false);
        return new OrganizationGateStatus(organizationId, state.AuthorizationRevision, state.ProjectionAppliedRevision, state.ContentIntegrityRevision, gate, pending, state.IntegrityQuarantineEnabled);
    }

    /// <summary>
    /// OB-F2: an existing organization answers success only for the original bootstrap input. Compared against the Authority's own
    /// records: the display name, the identities bound by the bootstrap and the roles recorded in the bootstrap command (later role or
    /// membership changes by owners do not matter; the operator workload name is not part of the comparison).
    /// </summary>
    private static async Task RequireSameBootstrapAsync(IAuthorityScope scope, OrganizationAuthorizationState existing, BootstrapOrganizationRequest request, CancellationToken cancellationToken)
    {
        static AuthorityException Conflict() => new(AuthorityErrorCodes.IdempotencyConflict, 409, false, "an organization with this id already exists and was created from different input");
        if (!string.Equals(existing.OrganizationDisplayName, request.OrganizationDisplayName, StringComparison.Ordinal) || existing.OrganizationOnboardingRequestId is not null)
        {
            throw Conflict();
        }

        var command = await scope.FindCommandByIdempotencyKeyAsync(existing.OrganizationId, "bootstrap:" + existing.OrganizationId.ToString("N"), cancellationToken).ConfigureAwait(false) ?? throw Conflict();
        var recorded = CanonicalJson.Deserialize<BootstrapPayload>(System.Text.Json.JsonDocument.Parse(command.CommandPayloadJson).RootElement);
        static string Roles(IEnumerable<string> roles) => string.Join(',', roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var expected = recorded.Roles.ToDictionary(r => r.AuthorityUserId, r => Roles(r.OrganizationRoles));
        if (expected.Count != request.Identities.Count)
        {
            throw Conflict();
        }

        foreach (var identity in request.Identities)
        {
            var binding = await scope.FindIdentityBindingByUserAsync(existing.OrganizationId, identity.AuthorityUserId, cancellationToken).ConfigureAwait(false);
            if (!expected.TryGetValue(identity.AuthorityUserId, out var roles) || roles != Roles(identity.OrganizationRoles) || binding is null
                || binding.IdentityIssuerUri != identity.IssuerUri || binding.IdentitySubjectIdentifier != identity.SubjectIdentifier)
            {
                throw Conflict();
            }
        }
    }

    private sealed record BootstrapRole(Guid AuthorityUserId, IReadOnlyList<string> OrganizationRoles);

    private sealed record BootstrapPayload(IReadOnlyList<BootstrapRole> Roles, string OperatorName);
}
