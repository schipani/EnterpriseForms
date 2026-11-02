using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.OrganizationOnboarding;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.PlatformOperators;

/// <summary>
/// C8 onboarding (C-104). Platform plane (Platform Console credential + an active operator's assertion): create, approve, withdraw,
/// reject, cancel and initialize onboarding requests, and the organizations directory. Every decision needs that operator's own evidence.
/// The only operator-plane route is the read-only key-custody status used by the development custody tool; there is no route that
/// creates, rotates or exports a key.
/// </summary>
public static class OrganizationOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/platform").WithTags("Platform").RequireAuthorization(InternalAuthentication.PlatformConsolePolicy);

        group.MapPost("/organization-onboarding", static async Task<Ok<OrganizationOnboardingCreateResponse>> (OrganizationOnboardingCreateRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OrganizationOnboardingService onboarding, CancellationToken cancellationToken) =>
                TypedResults.Ok(await onboarding.CreateAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("CreateOrganizationOnboardingRequest")
            .WithSummary("Create an immutable onboarding request (dry run validates). The Authority generates the organization id and every owner's user id; 2-10 owners from one approved issuer, never a Platform Operator identity.");

        group.MapPost("/organization-onboarding/search", static async Task<Ok<OrganizationOnboardingPage>> (OrganizationOnboardingSearchRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OrganizationOnboardingService onboarding, CancellationToken cancellationToken) =>
                TypedResults.Ok(await onboarding.SearchAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("SearchOrganizationOnboardingRequests")
            .WithSummary("Onboarding requests, newest first, 15 per page by default (opaque cursor bound to the operator and filter).");

        group.MapGet("/organization-onboarding/{requestId:guid}", static async Task<Ok<OrganizationOnboardingDetail>> (Guid requestId, HttpContext context, PlatformOperatorAssertionValidator assertions, OrganizationOnboardingService onboarding, CancellationToken cancellationToken) =>
                TypedResults.Ok(await onboarding.GetDetailAsync(InternalAuthentication.RequireOperator(context, assertions), requestId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationOnboardingRequest")
            .WithSummary("The request, its frozen statement and SHA-256, owners, approvals, key custody status, activation and the caller's eligibility.");

        MapDecision(group, "/organization-onboarding/{requestId:guid}/approvals", "ApproveOrganizationOnboardingRequest", static (s, a, id, r, c, t) => s.ApproveAsync(a, id, r, c, t));
        MapDecision(group, "/organization-onboarding/{requestId:guid}/approvals/withdraw", "WithdrawOrganizationOnboardingApproval", static (s, a, id, r, c, t) => s.WithdrawAsync(a, id, r, c, t));
        MapDecision(group, "/organization-onboarding/{requestId:guid}/reject", "RejectOrganizationOnboardingRequest", static (s, a, id, r, c, t) => s.RejectAsync(a, id, r, c, t));
        MapDecision(group, "/organization-onboarding/{requestId:guid}/cancel", "CancelOrganizationOnboardingRequest", static (s, a, id, r, c, t) => s.CancelAsync(a, id, r, c, t));
        MapDecision(group, "/organization-onboarding/{requestId:guid}/initialize", "InitializeOrganizationFromOnboarding", static (s, a, id, r, c, t) => s.InitializeAsync(a, id, r, c, t));

        group.MapPost("/organizations/search", static async Task<Ok<OrganizationDirectoryPage>> (OrganizationDirectorySearchRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OrganizationDirectoryService directory, CancellationToken cancellationToken) =>
                TypedResults.Ok(await directory.SearchAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("SearchOrganizationDirectory")
            .WithSummary("Organizations with lifecycle and owner, Security Administrator and member counts only (metadata; no records, documents, teams or shares).");

        group.MapGet("/organizations/{organizationId:guid}", static async Task<Ok<OrganizationDirectoryDetail>> (Guid organizationId, HttpContext context, PlatformOperatorAssertionValidator assertions, OrganizationDirectoryService directory, CancellationToken cancellationToken) =>
                TypedResults.Ok(await directory.GetDetailAsync(InternalAuthentication.RequireOperator(context, assertions), organizationId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationDirectoryEntry")
            .WithSummary("Owner identities, counts and onboarding provenance. The view is audited on the organization's audit stream before it is returned.");

        var operatorPlane = endpoints.MapGroup(InternalApi.Prefix + "/operator").WithTags("Operator").RequireAuthorization(InternalAuthentication.OperatorPolicy);
        operatorPlane.MapGet("/organization-onboarding/{requestId:guid}/key-custody", static async Task<Ok<OrganizationKeyCustodyStatus>> (Guid requestId, HttpContext context, OrganizationOnboardingService onboarding, CancellationToken cancellationToken) =>
                TypedResults.Ok(await onboarding.GetKeyCustodyAsync(requestId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationKeyCustodyStatus")
            .WithSummary("Read-only, for the development key custody tool: the single key name an approved request needs and whether it may be provisioned now. The Authority never creates the key.");

        return endpoints;
    }

    private static void MapDecision(RouteGroupBuilder group, string pattern, string name,
        Func<OrganizationOnboardingService, Application.Common.VerifiedActor, Guid, OrganizationOnboardingDecisionRequest, Guid, CancellationToken, Task<OrganizationOnboardingDecisionResponse>> decide)
    {
        group.MapPost(pattern, async Task<Ok<OrganizationOnboardingDecisionResponse>> (Guid requestId, OrganizationOnboardingDecisionRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OrganizationOnboardingService onboarding, CancellationToken cancellationToken) =>
                TypedResults.Ok(await decide(onboarding, InternalAuthentication.RequireOperator(context, assertions), requestId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName(name);
    }
}
