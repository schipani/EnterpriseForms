using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.PlatformOperators;

/// <summary>
/// C8 M2 Stages 1-2: the platform plane. Requires the Platform Console credential (never the business workload, operator, verifier
/// or reconciler credential) AND a Platform Operator assertion naming a registered, active operator; every decision additionally
/// needs that operator's own evidence. Nothing here reads or changes organization records, documents, teams or roles; the only
/// organization effect is the membership a second valid approval creates. Unavailable (fail closed) outside Development/Testing.
/// </summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/platform").WithTags("Platform").RequireAuthorization(InternalAuthentication.PlatformConsolePolicy);

        group.MapGet("/operator/me", static async Task<Ok<PlatformOperatorProfile>> (HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
                TypedResults.Ok(await registry.GetProfileAsync(InternalAuthentication.RequireOperator(context, assertions), InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetPlatformOperatorProfile")
            .WithSummary("The signed-in Platform Operator, their four platform permissions and the platform journal state.");

        group.MapGet("/operators", static async Task<Ok<PlatformOperatorRegistryView>> (HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
                TypedResults.Ok(await registry.GetRegistryAsync(InternalAuthentication.RequireOperator(context, assertions), InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ListPlatformOperators")
            .WithSummary("platform.operators.view: every registered operator (identity and status) and pending two-operator proposals.");

        group.MapPost("/operators/proposals", static async Task<Ok<PlatformOperatorDecisionResponse>> (PlatformOperatorProposalRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
                TypedResults.Ok(await registry.ProposeAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ProposePlatformOperatorChange")
            .WithSummary("platform.operators.manage: propose Provision, Reinstate or Remove (dry run returns the statement). A different active operator, not the target, must confirm.");

        group.MapPost("/operators/proposals/{proposalId:guid}/confirm", static async Task<Ok<PlatformOperatorDecisionResponse>> (Guid proposalId, PlatformOperatorConfirmationRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
                TypedResults.Ok(await registry.ConfirmAsync(InternalAuthentication.RequireOperator(context, assertions), proposalId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ConfirmPlatformOperatorChange");

        group.MapPost("/operators/{platformOperatorId:guid}/suspend", static async Task<Ok<PlatformOperatorDecisionResponse>> (Guid platformOperatorId, PlatformOperatorSuspensionRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformOperatorRegistryService registry, CancellationToken cancellationToken) =>
                TypedResults.Ok(await registry.SuspendAsync(InternalAuthentication.RequireOperator(context, assertions), platformOperatorId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("SuspendPlatformOperator")
            .WithSummary("Emergency suspension by one active operator (self included): immediate, invalidates the target's pending approvals, alerted.");

        group.MapPost("/membership-requests/search", static async Task<Ok<PlatformMembershipRequestPage>> (PlatformMembershipRequestSearchRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformMembershipApprovalService approvals, CancellationToken cancellationToken) =>
                TypedResults.Ok(await approvals.SearchAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("SearchPlatformMembershipRequests")
            .WithSummary("platform.membership_requests.review: cross-organization request metadata, newest first, 15 per page by default (opaque cursor bound to the operator and filter).");

        group.MapGet("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}", static async Task<Ok<PlatformMembershipRequestDetail>> (Guid organizationId, Guid requestId, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformMembershipApprovalService approvals, CancellationToken cancellationToken) =>
                TypedResults.Ok(await approvals.GetDetailAsync(InternalAuthentication.RequireOperator(context, assertions), organizationId, requestId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetPlatformMembershipRequest")
            .WithSummary("Request metadata, the exact statement v2 and its SHA-256, recorded approvals and the caller's eligibility. The view is journaled.");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/approvals", static async Task<Ok<PlatformMembershipDecisionResponse>> (Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformMembershipApprovalService approvals, CancellationToken cancellationToken) =>
                TypedResults.Ok(await approvals.ApproveAsync(InternalAuthentication.RequireOperator(context, assertions), organizationId, requestId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ApprovePlatformMembershipRequest")
            .WithSummary("platform.membership_requests.decide: one operator's approval. The first is stored as pending evidence; a valid second approval by a different operator creates the membership atomically.");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/approvals/withdraw", static async Task<Ok<PlatformMembershipDecisionResponse>> (Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformMembershipApprovalService approvals, CancellationToken cancellationToken) =>
                TypedResults.Ok(await approvals.WithdrawAsync(InternalAuthentication.RequireOperator(context, assertions), organizationId, requestId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("WithdrawPlatformMembershipApproval")
            .WithSummary("The approving operator withdraws their own pending approval (nobody else can).");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/reject", static async Task<Ok<PlatformMembershipDecisionResponse>> (Guid organizationId, Guid requestId, PlatformMembershipDecisionRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, PlatformMembershipApprovalService approvals, CancellationToken cancellationToken) =>
                TypedResults.Ok(await approvals.RejectAsync(InternalAuthentication.RequireOperator(context, assertions), organizationId, requestId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("RejectPlatformMembershipRequest")
            .WithSummary("One operator rejects with a justification and evidence; any pending approval is invalidated; nothing is granted.");

        return endpoints;
    }
}
