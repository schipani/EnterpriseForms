using System.Threading.RateLimiting;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Audit;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.Audit;

/// <summary>
/// C8 M4-2 (AC-8.3, PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.3): audit-access evidence for the business audit explorer. Business
/// workload plus delegated user; the organization is bound to the user by the Authority. The answer is an allow only after the
/// AuditAccessAuthorized entry committed; anything else is 403, 429 or 503 and the business serves nothing.
/// </summary>
public static class AuditAccessEndpoints
{
    public const string Route = InternalApi.Prefix + "/organizations/audit/access";

    /// <summary>C8 M4-3 (AC-8.2): the security-command timeline, read and served by the Authority under the same protocol.</summary>
    public const string TimelineRoute = InternalApi.Prefix + "/organizations/audit/security-command-timeline";

    public static IEndpointRouteBuilder MapAuditAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, static async Task<Ok<AuditAccessDecision>> (AuditAccessRequest request, HttpContext context, AuditAccessService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                var decision = await service.AuthorizeAsync(actor, request, cancellationToken).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(decision);
            })
            .WithTags("Audit")
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("AuthorizeAuditAccess")
            .WithSummary("AC-8.3: authorizes one audit-explorer page or event detail for the delegated user (view_audit, Security Auditors) and commits an AuditAccessAuthorized entry (actor, kind, filter digest, authorization revision, delivery not_attested; no result counts) in the organization's audit stream before answering. The organization lock is taken NOWAIT with bounded retries (503 AUDIT_EVIDENCE_BUSY), and a failed or unknown commit is 503 AUDIT_EVIDENCE_UNAVAILABLE: no allow without committed evidence.");

        endpoints.MapPost(TimelineRoute, static async Task<Ok<SecurityCommandTimelinePage>> (SecurityCommandTimelineRequest request, HttpContext context, SecurityCommandTimelineService service, DelegatedUserAssertionValidator validator, CancellationToken cancellationToken) =>
            {
                var actor = InternalAuthentication.RequireActor(context, validator);
                var page = await service.ReadAsync(actor, request, cancellationToken).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(page);
            })
            .WithTags("Audit")
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(InternalAuthentication.WorkloadPolicy)
            .WithName("ReadSecurityCommandTimeline")
            .WithSummary("AC-8.2: one page of the organization's security-command timeline for the delegated Security Auditor (view_audit): command rows in every state, including Rejected and FailedReconciliation, and allowlisted audit-stream entries, newest first. Each envelope is built field by field from an allowlist; free text, notes, step-up evidence, share scopes, identity identifiers, rejection details and matched or evaluated counts are never returned. The page is read inside the AC-8.3 audit-access transaction (NOWAIT organization lock, bounded) and returned only after its AuditAccessAuthorized entry (served_by authority, delivery not_attested) committed.");
        return endpoints;
    }

    /// <summary>Registers the service with its validated TC-3 options (start-up fails when out of bounds).</summary>
    public static IServiceCollection AddAuditAccess(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(AuditAccessOptions.SectionName).Get<AuditAccessOptions>() ?? new AuditAccessOptions();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<AuditAccessTelemetry>();
        services.AddSingleton<IOrganizationPermissionCheck>(sp => sp.GetRequiredService<PermissionCheckService>());
        services.AddSingleton<IAuditAccessRateLimiter, AuditAccessRateLimiter>();
        services.AddSingleton<AuditAccessService>();
        services.AddSingleton<SecurityCommandTimelineService>();
        return services;
    }
}

/// <summary>
/// Token buckets for audit access (contract §5.3): one per (organization, issuer, subject) and one per organization, per Authority
/// instance (RR-10). Partitions are keyed by the verified identity, never by caller-chosen request fields.
/// </summary>
public sealed class AuditAccessRateLimiter : IAuditAccessRateLimiter, IDisposable
{
    private readonly PartitionedRateLimiter<(Guid Organization, VerifiedActor Actor)> limiter;

    public AuditAccessRateLimiter(AuditAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var user = PartitionedRateLimiter.Create<(Guid Organization, VerifiedActor Actor), string>(key => RateLimitPartition.GetTokenBucketLimiter(
            key.Organization.ToString("D") + "|" + key.Actor.IssuerUri + "|" + key.Actor.SubjectIdentifier,
            _ => Bucket(options.UserRequestsPerSecond, options.UserBurstLimit)));
        var organization = PartitionedRateLimiter.Create<(Guid Organization, VerifiedActor Actor), Guid>(key => RateLimitPartition.GetTokenBucketLimiter(
            key.Organization,
            _ => Bucket(options.OrganizationRequestsPerSecond, options.OrganizationBurstLimit)));
        limiter = PartitionedRateLimiter.CreateChained(user, organization);
    }

    public bool TryAcquire(Guid organizationId, VerifiedActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        using var lease = limiter.AttemptAcquire((organizationId, actor));
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();

    private static TokenBucketRateLimiterOptions Bucket(int perSecond, int burst) => new()
    {
        TokenLimit = burst,
        TokensPerPeriod = perSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
