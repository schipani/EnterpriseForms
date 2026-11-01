using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.OrganizationOnboarding;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.PlatformOperators;

/// <summary>
/// C8 owner recovery (C-105). Platform plane (Platform Console credential + an active operator's assertion): open, approve, withdraw,
/// cancel and execute recovery cases, each decision with that operator's own evidence. Business plane (workload credential + the
/// delegated user's assertion): the organization's in-app notice and objection, for enabled owners and Security Administrators only.
/// The business plane can only object; it has no route that opens, approves or executes a case.
/// </summary>
public static class OwnerRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapOwnerRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/platform").WithTags("Platform").RequireAuthorization(InternalAuthentication.PlatformConsolePolicy);

        group.MapPost("/owner-recovery", static async Task<Ok<OwnerRecoveryCreateResponse>> (OwnerRecoveryCreateRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OwnerRecoveryService recovery, CancellationToken cancellationToken) =>
                TypedResults.Ok(await recovery.CreateAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("OpenOwnerRecoveryCase")
            .WithSummary("Open an immutable owner recovery case (dry run validates) for an organization with fewer than two eligible owners. Grants nothing; three operators approve, then the waiting period runs.");

        group.MapPost("/owner-recovery/search", static async Task<Ok<OwnerRecoveryPage>> (OwnerRecoverySearchRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OwnerRecoveryService recovery, CancellationToken cancellationToken) =>
                TypedResults.Ok(await recovery.SearchAsync(InternalAuthentication.RequireOperator(context, assertions), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("SearchOwnerRecoveryCases")
            .WithSummary("Recovery cases, newest first, 15 per page by default (opaque cursor bound to the operator and filter).");

        group.MapGet("/owner-recovery/{caseId:guid}", static async Task<Ok<OwnerRecoveryCaseDetail>> (Guid caseId, HttpContext context, PlatformOperatorAssertionValidator assertions, OwnerRecoveryService recovery, CancellationToken cancellationToken) =>
                TypedResults.Ok(await recovery.GetDetailAsync(InternalAuthentication.RequireOperator(context, assertions), caseId, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOwnerRecoveryCase")
            .WithSummary("The case, its frozen statement and SHA-256, owners, evidence, approvals, waiting period, objection and the caller's eligibility. Audited on the organization's audit stream before it is returned.");

        MapDecision(group, "/owner-recovery/{caseId:guid}/approvals", "ApproveOwnerRecoveryCase", static (s, a, id, r, c, t) => s.ApproveAsync(a, id, r, c, t));
        MapDecision(group, "/owner-recovery/{caseId:guid}/approvals/withdraw", "WithdrawOwnerRecoveryApproval", static (s, a, id, r, c, t) => s.WithdrawAsync(a, id, r, c, t));
        MapDecision(group, "/owner-recovery/{caseId:guid}/cancel", "CancelOwnerRecoveryCase", static (s, a, id, r, c, t) => s.CancelAsync(a, id, r, c, t));
        MapDecision(group, "/owner-recovery/{caseId:guid}/execute", "ExecuteOwnerRecoveryCase", static (s, a, id, r, c, t) => s.ExecuteAsync(a, id, r, c, t));

        var business = endpoints.MapGroup(InternalApi.Prefix + "/organizations/owner-recovery").WithTags("Membership").RequireAuthorization(InternalAuthentication.WorkloadPolicy);
        business.MapPost("/notice", static async Task<Ok<OwnerRecoveryNotice>> (OwnerRecoveryNoticeRequest request, HttpContext context, OwnerRecoveryService recovery, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
                TypedResults.Ok(await recovery.GetNoticeAsync(InternalAuthentication.RequireActor(context, validator), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("GetOwnerRecoveryNotice")
            .WithSummary("The organization's open recovery case for an enabled owner or Security Administrator (not found for anyone else).");

        business.MapPost("/objections", static async Task<Ok<OwnerRecoveryObjectionResponse>> (OwnerRecoveryObjectionRequest request, HttpContext context, OwnerRecoveryService recovery, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
                TypedResults.Ok(await recovery.ObjectAsync(InternalAuthentication.RequireActor(context, validator), request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName("ObjectToOwnerRecoveryCase")
            .WithSummary("Object to the open case (fresh step-up, explanation). Ends the case immediately; recorded in the platform journal and the organization's audit stream.");

        return endpoints;
    }

    private static void MapDecision(RouteGroupBuilder group, string pattern, string name,
        Func<OwnerRecoveryService, Application.Common.VerifiedActor, Guid, OwnerRecoveryDecisionRequest, Guid, CancellationToken, Task<OwnerRecoveryDecisionResponse>> decide)
    {
        group.MapPost(pattern, async Task<Ok<OwnerRecoveryDecisionResponse>> (Guid caseId, OwnerRecoveryDecisionRequest request, HttpContext context, PlatformOperatorAssertionValidator assertions, OwnerRecoveryService recovery, CancellationToken cancellationToken) =>
                TypedResults.Ok(await decide(recovery, InternalAuthentication.RequireOperator(context, assertions), caseId, request, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false)))
            .WithName(name);
    }
}
