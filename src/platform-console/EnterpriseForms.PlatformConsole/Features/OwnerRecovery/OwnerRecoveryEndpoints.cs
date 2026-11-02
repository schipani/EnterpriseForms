using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Features.OperatorApprovals;
using EnterpriseForms.PlatformConsole.Features.Session;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.PlatformConsole.Features.OwnerRecovery;

/// <summary>An owner declared unavailable, as the browser may send it: the reason and documented evidence are required by the Authority.</summary>
public sealed record RecoveryDeclaredInput(Guid AuthorityUserId, string Reason, string EvidenceReference, string EvidenceSummary);

/// <summary>A proposed replacement owner: an existing enabled member (<c>ExistingAuthorityUserId</c>) or a new identity from the organization's issuer.</summary>
public sealed record RecoveryReplacementInput(Guid? ExistingAuthorityUserId, string? IssuerUri, string? SubjectIdentifier, string? DisplayNameHint, string VerificationMethod, string VerificationReference);

/// <summary>A new owner recovery case as the browser may send it (unknown members are refused).</summary>
public sealed record RecoveryCreateInput(string IdempotencyKey, Guid OrganizationId, string IncidentReference, string Justification, IReadOnlyList<RecoveryDeclaredInput>? DeclaredUnavailable,
    IReadOnlyList<RecoveryReplacementInput> Replacements, bool DryRun = false);

/// <summary>
/// C8 owner recovery (C-105): PG-075 Owner recovery (feature owner: Platform administration / Security). Every endpoint needs a live
/// console session (and CSRF on POST); the operator is always the session's. Reads and decisions are the Authority's platform-plane
/// operations; this layer adds no rule. Decisions use the same dry-run → fingerprint check → Vault signing → submit flow as the other
/// approvals. Owner identities and case evidence only: no record, document, team or share data.
/// </summary>
public static class OwnerRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapOwnerRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ConsoleSessionCookie.ApiPath + "/v1").WithTags("OwnerRecovery").AddEndpointFilter<RequireOperatorSession>();

        group.MapGet("/owner-recovery", static async Task<Ok<OwnerRecoveryPage>> (string? q, string? status, string? cursor, int? limit, HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                return TypedResults.Ok(await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
                    authority.SearchRecoveryAsync(session.SubjectIdentifier, new OwnerRecoverySearchRequest(q, status, cursor, limit), SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false));
            })
            .WithName("SearchOwnerRecovery")
            .WithSummary("PG-075 queue: owner recovery cases, newest first (15 per page by default, at most 50), and this environment's waiting period.");

        group.MapGet("/owner-recovery/organizations/{organizationId:guid}/members", static async Task<Ok<OwnerRecoveryMemberPage>> (Guid organizationId, string? q, string? cursor, int? limit, Guid[]? exclude, HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                return TypedResults.Ok(await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
                    authority.SearchRecoveryMembersAsync(session.SubjectIdentifier, organizationId, new OwnerRecoveryMemberSearchRequest(q, cursor, limit, exclude), SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false));
            })
            .WithName("SearchOwnerRecoveryMembers")
            .WithSummary("PG-075 member picker: members who may be proposed as replacement owners (enabled, not owners, not operator identities), at most 10 per page; `exclude` (repeatable) lists already selected members. Minimum metadata; the Authority audits each search before disclosure.");

        group.MapPost("/owner-recovery", static async Task<Ok<OwnerRecoveryCreateResponse>> (RecoveryCreateInput input, HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                var request = new OwnerRecoveryCreateRequest(input.IdempotencyKey, input.OrganizationId, input.IncidentReference, input.Justification,
                    (input.DeclaredUnavailable ?? []).Select(d => new OwnerRecoveryDeclaredInput(d.AuthorityUserId, d.Reason, d.EvidenceReference, d.EvidenceSummary)).ToList(),
                    (input.Replacements ?? []).Select(r => new OwnerRecoveryReplacementInput(r.ExistingAuthorityUserId, r.IssuerUri, r.SubjectIdentifier, r.DisplayNameHint, r.VerificationMethod, r.VerificationReference)).ToList(),
                    input.DryRun);
                return TypedResults.Ok(await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
                    authority.CreateRecoveryAsync(session.SubjectIdentifier, request, SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false));
            })
            .RequireRateLimiting(ConsoleRateLimits.DecisionPolicy)
            .WithName("OpenOwnerRecovery")
            .WithSummary("PG-075: open an immutable recovery case (dryRun validates only) for an organization with fewer than two eligible owners. Opening grants nothing.");

        group.MapGet("/owner-recovery/{caseId:guid}", static async Task<Ok<OwnerRecoveryCaseDetail>> (Guid caseId, HttpContext http, AuthorityPlatformClient authority, OperatorSessionStore store, IHostEnvironment environment, CancellationToken cancellationToken) =>
            {
                var session = RequireOperatorSession.Current(http);
                return TypedResults.Ok(await OperatorSessionGuard.RunAsync(http, session, store, environment, () =>
                    authority.GetRecoveryAsync(session.SubjectIdentifier, caseId, SessionEndpoints.Correlation(http), cancellationToken)).ConfigureAwait(false));
            })
            .WithName("GetOwnerRecovery")
            .WithSummary("PG-075 case: frozen statement and fingerprint, owner status, evidence, approvals, waiting period, objection and the operator's eligibility. The Authority audits each view (fail closed).");

        MapDecision(group, "/owner-recovery/{caseId:guid}/approvals", OperatorDecisions.Approve, "ApproveOwnerRecovery", "The signed-in operator's approval; three different operators are required.");
        MapDecision(group, "/owner-recovery/{caseId:guid}/approvals/withdraw", OperatorDecisions.Withdraw, "WithdrawOwnerRecoveryApproval", "Withdraws the signed-in operator's own approval.");
        MapDecision(group, "/owner-recovery/{caseId:guid}/cancel", OperatorDecisions.Cancel, "CancelOwnerRecovery", "Cancels an open case; nothing is changed in the organization.");
        MapDecision(group, "/owner-recovery/{caseId:guid}/execute", OperatorDecisions.Execute, "ExecuteOwnerRecovery",
            "After three approvals, the waiting period and no objection: adds the replacement owners in one Authority transaction (nothing is removed or disabled).");
        return endpoints;
    }

    private static void MapDecision(RouteGroupBuilder group, string pattern, string decision, string name, string summary)
    {
        group.MapPost(pattern, async Task<Ok<OwnerRecoveryDecisionResponse>> (Guid caseId, OperatorDecisionInput input, HttpContext http, OperatorDecisionService decisions, CancellationToken cancellationToken) =>
                TypedResults.Ok(await decisions.DecideRecoveryAsync(http, RequireOperatorSession.Current(http), caseId, decision, input, cancellationToken).ConfigureAwait(false)))
            .RequireRateLimiting(ConsoleRateLimits.DecisionPolicy)
            .WithName(name)
            .WithSummary(summary);
    }
}
