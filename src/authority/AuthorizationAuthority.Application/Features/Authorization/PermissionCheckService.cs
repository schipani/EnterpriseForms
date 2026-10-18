using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Authorization;

/// <summary>Authorization check (CROSS_SYSTEM §2 "Authorization check"): gate → fresh SpiceDB check at the authority-held token → gate re-read.</summary>
public sealed class PermissionCheckService(IAuthorityStore store, ISpiceDbProjection spiceDb)
{
    private const int MaxGateRetries = 2;

    public async Task<PermissionDecision> CheckAsync(VerifiedActor actor, PermissionCheckRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decisions = await CheckBatchAsync(actor, [request], correlationId, cancellationToken).ConfigureAwait(false);
        return decisions[0];
    }

    public async Task<IReadOnlyList<PermissionDecision>> CheckBatchAsync(VerifiedActor actor, IReadOnlyList<PermissionCheckRequest> requests, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is 0 or > 250)
        {
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, "1..250 checks per call");
        }

        var organizationId = requests[0].OrganizationId;
        if (requests.Any(r => r.OrganizationId != organizationId))
        {
            throw AuthorityException.Invalid("all checks in a batch must target one organization");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt <= MaxGateRetries; attempt++)
        {
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw AuthorityException.NotRegistered("organization is not registered");
            var gate = await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false);
            OrganizationGate.RequireOpen(gate);
            var zedToken = state.ProjectionZedToken!;

            var checks = new List<SpiceDbCheck>(requests.Count);
            var registryDenied = new bool[requests.Count];
            for (var i = 0; i < requests.Count; i++)
            {
                var (check, requiresRegistry, registryType) = MapCheck(requests[i], bound.AuthorityUserId);
                checks.Add(check);
                if (requiresRegistry)
                {
                    var resource = await scope.FindResourceAsync(organizationId, registryType!, requests[i].Resource.ResourceId, cancellationToken).ConfigureAwait(false);
                    registryDenied[i] = resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active;
                }
            }

            var outcomes = await spiceDb.CheckBulkAsync(checks, zedToken, cancellationToken).ConfigureAwait(false);

            // Re-read: if the authoritative state moved during the check, the outcome may be stale — retry or fail closed.
            var after = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)!;
            if (after!.AuthorizationRevision != state.AuthorizationRevision || after.ProjectionAppliedRevision != state.ProjectionAppliedRevision || after.IntegrityQuarantineEnabled)
            {
                continue;
            }

            var decisions = new List<PermissionDecision>(requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                long? contentRevision = null;
                if (requests[i].Resource.ResourceKind == ResourceKind.FormRecord)
                {
                    contentRevision = (await scope.FindContentHeadAsync(organizationId, requests[i].Resource.ResourceId, false, cancellationToken).ConfigureAwait(false))?.CommittedContentRevision;
                }

                var allowed = !registryDenied[i] && outcomes[i] == SpiceDbCheckOutcome.Allowed;
                decisions.Add(new PermissionDecision(
                    allowed,
                    allowed ? null : registryDenied[i] ? AuthorityErrorCodes.ResourceNotRegistered : outcomes[i] == SpiceDbCheckOutcome.Unknown ? "CONDITIONAL_TREATED_AS_DENY" : AuthorityErrorCodes.Forbidden,
                    state.AuthorizationRevision,
                    state.ProjectionAppliedRevision,
                    contentRevision,
                    correlationId));
            }

            return decisions;
        }

        throw AuthorityException.NotReady("authorization state moved during check");
    }

    public async Task<DiscoverResponse> DiscoverAsync(VerifiedActor actor, DiscoverRecordsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize is < 1 or > 250)
        {
            throw AuthorityException.Invalid("page_size must be 1..250");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, request.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var registered = (await scope.GetActiveRecordIdsAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)).ToHashSet();
        var lookedUp = await spiceDb.LookupResourcesAsync(ResourceTypeNames.FormRecord, "discover", ResourceTypeNames.User, bound.AuthorityUserId.ToString("D"), state.ProjectionZedToken!, 1000, cancellationToken).ConfigureAwait(false);
        var ids = lookedUp.Select(Guid.Parse).Where(registered.Contains).OrderBy(id => id).ToList();

        var skip = 0;
        if (!string.IsNullOrEmpty(request.ContinuationToken))
        {
            var parts = request.ContinuationToken.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var tokenRevision) || tokenRevision != state.AuthorizationRevision || !int.TryParse(parts[1], out skip))
            {
                throw AuthorityException.Invalid("continuation token invalidated by an authorization revision change");
            }
        }

        var page = ids.Skip(skip).Take(request.PageSize).ToList();
        var next = skip + page.Count < ids.Count ? $"{state.AuthorizationRevision}:{skip + page.Count}" : null;
        return new DiscoverResponse(page, state.AuthorizationRevision, next);
    }

    internal static (SpiceDbCheck Check, bool RequiresRegistry, string? RegistryType) MapCheck(PermissionCheckRequest request, Guid userId)
    {
        var subject = userId.ToString("D");
        var id = request.Resource.ResourceId.ToString("D");
        return (request.Resource.ResourceKind, request.RequestedAction) switch
        {
            (ResourceKind.FormRecord, RequestedAction.Discover) => (new SpiceDbCheck(ResourceTypeNames.FormRecord, id, "discover", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormRecord),
            (ResourceKind.FormRecord, RequestedAction.Read) => (new SpiceDbCheck(ResourceTypeNames.FormRecord, id, "read", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormRecord),
            (ResourceKind.FormRecord, RequestedAction.Edit) => (new SpiceDbCheck(ResourceTypeNames.FormRecord, id, "edit", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormRecord),
            (ResourceKind.FormRecord, RequestedAction.ManageAccess) => (new SpiceDbCheck(ResourceTypeNames.FormRecord, id, "manage_access", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormRecord),
            (ResourceKind.FormRecord, RequestedAction.TransferOwnership) => (new SpiceDbCheck(ResourceTypeNames.FormRecord, id, "transfer_ownership", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormRecord),
            (ResourceKind.FieldItem, RequestedAction.Read) => (new SpiceDbCheck(ResourceTypeNames.FieldItem, id, "read", ResourceTypeNames.User, subject), false, null),
            (ResourceKind.FieldItem, RequestedAction.Edit) => (new SpiceDbCheck(ResourceTypeNames.FieldItem, id, "edit", ResourceTypeNames.User, subject), false, null),
            (ResourceKind.FormDefinition, RequestedAction.DesignForm) => (new SpiceDbCheck(ResourceTypeNames.FormDefinition, id, "design", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormDefinition),
            (ResourceKind.FormDefinition, RequestedAction.CreateRecord) => (new SpiceDbCheck(ResourceTypeNames.FormDefinition, id, "create_record", ResourceTypeNames.User, subject), true, ResourceTypeNames.FormDefinition),
            (ResourceKind.Organization, RequestedAction.ManageOrganization) => (new SpiceDbCheck(ResourceTypeNames.Organization, id, "manage_configuration", ResourceTypeNames.User, subject), false, null),
            (ResourceKind.Organization, RequestedAction.DesignForm) => (new SpiceDbCheck(ResourceTypeNames.Organization, id, "create_form", ResourceTypeNames.User, subject), false, null),
            _ => throw AuthorityException.Invalid($"action {request.RequestedAction} is not defined for {request.Resource.ResourceKind}"),
        };
    }
}
