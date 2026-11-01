using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Features.Session;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole.Features.OperatorApprovals;

/// <summary>Approval status of one request for the signed-in operator (from the Authority's dry run; reading it records nothing).</summary>
public sealed record ApprovalStatusView(
    Guid RequestId,
    Guid OrganizationId,
    int ApprovalsRecorded,
    int ApprovalsRequired,
    bool CanApprove,
    string? RefusalCode,
    string? RefusalDetail,
    string StatementSha256,
    string EvidenceAssurance,
    Guid CorrelationId);

/// <summary>
/// PG-070 "Operator approvals" API (feature owner: Platform administration / Security). Every endpoint needs a live console session
/// (and CSRF for decisions); the operator is always the session's. Reads and decisions are the Authority's Stage 2 platform-plane
/// operations, unchanged; this layer adds no approval rule. Membership-request metadata only: no record, document, team or share data.
/// </summary>
public static class OperatorApprovalEndpoints
{
    public static IEndpointRouteBuilder MapOperatorApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ConsoleSessionCookie.ApiPath + "/v1").WithTags("OperatorApprovals").AddEndpointFilter<RequireOperatorSession>();

        group.MapGet("/operator/me", static async Task<Ok<PlatformOperatorProfile>> (HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IOptions<PlatformConsoleOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
                TypedResults.Ok(await OperatorProfiles.CurrentAsync(http, RequireOperatorSession.Current(http), authority, store, options.Value, environment, cancellationToken).ConfigureAwait(false)))
            .WithName("GetCurrentOperator")
            .WithSummary("The signed-in operator's registry entry, platform permissions and the platform journal state.");

        group.MapGet("/membership-requests", static async Task<Ok<PlatformMembershipRequestPage>> (string? q, string? status, string? cursor, int? limit, HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                return TypedResults.Ok(await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
                    authority.SearchAsync(session.SubjectIdentifier, new PlatformMembershipRequestSearchRequest(q, status, cursor, limit), SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false));
            })
            .WithName("SearchMembershipRequests")
            .WithSummary("Cross-organization approval queue (15 per page by default, at most 50; opaque cursor bound to the operator and filter).");

        group.MapGet("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}", static async Task<Ok<PlatformMembershipRequestDetail>> (Guid organizationId, Guid requestId, HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                return TypedResults.Ok(await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
                    authority.GetDetailAsync(session.SubjectIdentifier, organizationId, requestId, SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false));
            })
            .WithName("GetMembershipRequest")
            .WithSummary("Request details, the exact statement and its fingerprint, recorded approvals and eligibility. A sensitive disclosure: the Authority audits each view (fail closed).");

        group.MapGet("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/approval-status", static async Task<Ok<ApprovalStatusView>> (Guid organizationId, Guid requestId, HttpContext http, OperatorDecisionService decisions, CancellationToken cancellationToken) =>
            {
                var preview = await decisions.PreviewAsync(http, RequireOperatorSession.Current(http), organizationId, requestId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new ApprovalStatusView(preview.RequestId, preview.OrganizationId, preview.ApprovalsRecorded, preview.ApprovalsRequired, preview.Eligible, preview.RefusalCode, preview.RefusalDetail, preview.StatementSha256, preview.EvidenceAssurance, preview.CorrelationId));
            })
            .WithName("GetMembershipApprovalStatus")
            .WithSummary("Pending approval status (approvals recorded of required) and whether the signed-in operator can approve now.");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/approvals", static (Guid organizationId, Guid requestId, OperatorDecisionInput input, HttpContext http, OperatorDecisionService decisions, CancellationToken cancellationToken) =>
                DecideAsync(organizationId, requestId, OperatorDecisions.Approve, input, http, decisions, cancellationToken))
            .RequireRateLimiting(ConsoleRateLimits.DecisionPolicy)
            .WithName("ApproveMembershipRequest")
            .WithSummary("The signed-in operator's approval. First valid approval: AwaitingSecondApproval. A valid approval by a second, different operator: Approved (the Authority creates the membership).");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/approvals/withdraw", static (Guid organizationId, Guid requestId, OperatorDecisionInput input, HttpContext http, OperatorDecisionService decisions, CancellationToken cancellationToken) =>
                DecideAsync(organizationId, requestId, OperatorDecisions.Withdraw, input, http, decisions, cancellationToken))
            .RequireRateLimiting(ConsoleRateLimits.DecisionPolicy)
            .WithName("WithdrawMembershipApproval")
            .WithSummary("Withdraws the signed-in operator's own pending approval (never another operator's).");

        group.MapPost("/organizations/{organizationId:guid}/membership-requests/{requestId:guid}/reject", static (Guid organizationId, Guid requestId, OperatorDecisionInput input, HttpContext http, OperatorDecisionService decisions, CancellationToken cancellationToken) =>
                DecideAsync(organizationId, requestId, OperatorDecisions.Reject, input, http, decisions, cancellationToken))
            .RequireRateLimiting(ConsoleRateLimits.DecisionPolicy)
            .WithName("RejectMembershipRequest")
            .WithSummary("Rejects the request with a justification; any pending approval is invalidated and nothing is granted.");

        return endpoints;
    }

    private static async Task<Ok<PlatformMembershipDecisionResponse>> DecideAsync(Guid organizationId, Guid requestId, string decision, OperatorDecisionInput input, HttpContext http, OperatorDecisionService decisions, CancellationToken cancellationToken) =>
        TypedResults.Ok(await decisions.DecideAsync(http, RequireOperatorSession.Current(http), organizationId, requestId, decision, input, cancellationToken).ConfigureAwait(false));
}
