using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthorizationAuthority.Api.Features.SecurityCommands;

/// <summary>
/// Phase C8 — AC-9 (PHASE_C8_AC9_SECURITY_REVIEW_PACKAGE.md §1–§2): the business Worker's read-only outcome lookup. Reconciler
/// credential only; a delegated user assertion is refused so the endpoint can never act as, or for, a user. Rate-limited per
/// reconciler issuer. C-3: the idempotency key, team and member are never logged or echoed; a success is logged with the
/// command id.
/// </summary>
public static class SecurityCommandOutcomeEndpoints
{
    public const string Route = InternalApi.Prefix + "/security-command-outcomes/lookup";
    public const string RateLimitPolicy = "reconciler-outcome-lookup";

    public static IEndpointRouteBuilder MapSecurityCommandOutcomeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, static async Task<Ok<SecurityCommandOutcome>> (HttpContext context, SecurityCommandOutcomeService service, ReconcilerLookupTelemetry telemetry, CancellationToken cancellationToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                if (!string.IsNullOrEmpty(context.Request.Headers[InternalAuthentication.UserAssertionHeader].ToString()))
                {
                    throw new AuthorityException(AuthorityErrorCodes.DelegatedAssertionNotAccepted, 400, false, "this endpoint never acts for a user");
                }

                if (!Guid.TryParse(context.Request.Headers[InternalAuthentication.CorrelationHeader].ToString(), out var correlation))
                {
                    throw new AuthorityException(AuthorityErrorCodes.InvalidRequest, 400, false, "X-Correlation-Id (uuid) is required");
                }

                var request = await ReadRequestAsync(context, cancellationToken).ConfigureAwait(false);
                SecurityCommandOutcome outcome;
                try
                {
                    outcome = await service.LookupAsync(request, correlation, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.InnerException is Npgsql.NpgsqlException)
                {
                    // The execution strategy wraps an unreachable store; answer the retryable 503 of the contract (fail closed).
                    throw AuthorityException.NotReady("authority store unavailable");
                }

                context.Response.Headers[InternalAuthentication.CorrelationHeader] = correlation.ToString("D");
                context.Response.Headers.CacheControl = "no-store";
                telemetry.Answered(outcome, request.ExpectedCommandKind, Stopwatch.GetElapsedTime(started));
                return TypedResults.Ok(outcome);
            })
            .WithTags("SecurityCommands")
            .Accepts<SecurityCommandOutcomeLookupRequest>("application/json")
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(InternalAuthentication.ReconcilerPolicy)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("LookupSecurityCommandOutcome")
            .WithSummary("AC-9 (reconciler credential only): read-only outcome of one AddTeamMember/RemoveTeamMember command bound by (organization, idempotency key, allowlisted kind), with the organization's revisions and the effective membership of the command's own (team, member) pair at the projection-applied revision. Uniform found=false for unknown organization, key or kind mismatch; membership null when rejected or quarantined. No lock, revision, journal or SpiceDB access.");
        return endpoints;
    }

    /// <summary>Registers the reconciler rate limiter (token bucket per validated issuer; D8-16 bounds, startup failure when out of bounds).</summary>
    public static IServiceCollection AddReconcilerLookup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var limits = configuration.GetSection(ReconcilerLookupLimits.SectionName).Get<ReconcilerLookupLimits>() ?? new ReconcilerLookupLimits();
        limits.Validate();
        services.AddSingleton(limits);
        services.AddSingleton<SecurityCommandOutcomeService>();
        services.AddSingleton<ReconcilerLookupTelemetry>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (rejected, token) =>
            {
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                rejected.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                rejected.HttpContext.Response.Headers[InternalAuthentication.CorrelationHeader] = InternalAuthentication.Correlation(rejected.HttpContext).ToString("D");
                await rejected.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(AuthorityErrorCodes.RateLimited, InternalAuthentication.Correlation(rejected.HttpContext), true, "reconciler rate limit"), token).ConfigureAwait(false);
            };
            options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetTokenBucketLimiter(
                // The validated issuer is fixed per credential; workload_name is caller-chosen and would let a key holder open extra buckets.
                http.User.FindFirstValue("iss") ?? "unauthenticated",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.BurstLimit,
                    TokensPerPeriod = limits.TokensPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });
        return services;
    }

    private static async Task<SecurityCommandOutcomeLookupRequest> ReadRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > 4096)
        {
            throw Invalid();
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("organization_id", out var organization) || !organization.TryGetGuid(out var organizationId)
                || !root.TryGetProperty("idempotency_key", out var key) || key.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("expected_command_kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<SecurityCommandKind>(kind.GetString(), ignoreCase: false, out var commandKind)
                || !Enum.IsDefined(commandKind) || int.TryParse(kind.GetString(), out _))
            {
                throw Invalid();
            }

            return new SecurityCommandOutcomeLookupRequest(organizationId, key.GetString()!, commandKind);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static AuthorityException Invalid() => new(AuthorityErrorCodes.InvalidRequest, 400, false, "request body invalid");
}

/// <summary>D8-16 / Q-6: reconciler lookup rate (per Authority instance, F-7). Defaults 20/s, burst 40; at most 50/s (PB-09 envelope).</summary>
public sealed class ReconcilerLookupLimits
{
    public const string SectionName = "Reconciler:LookupRateLimit";

    public int TokensPerSecond { get; set; } = 20;

    public int BurstLimit { get; set; } = 40;

    public void Validate()
    {
        if (TokensPerSecond is < 1 or > 50 || BurstLimit < TokensPerSecond || BurstLimit > 100)
        {
            throw new InvalidOperationException("Reconciler:LookupRateLimit must satisfy 1 ≤ TokensPerSecond ≤ 50 and TokensPerSecond ≤ BurstLimit ≤ 100.");
        }
    }
}

/// <summary>
/// Structured logs and metrics for AC-9 (package §2.1, C-3). Answers are logged with organization, kind, found, state, outcome,
/// correlation, latency and command id — never the key, team or member. Refusals (400, 401, 403, 422, 429) are counted and
/// logged as warnings so they can be alerted on. Lookups are not journaled (Q-3, open for the reviewer).
/// </summary>
public sealed partial class ReconcilerLookupTelemetry : IDisposable
{
    public const string MeterName = "AuthorizationAuthority.Reconciler";

    private readonly ILogger<ReconcilerLookupTelemetry> logger;
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> answered;
    private readonly Counter<long> refused;
    private readonly Histogram<double> latency;

    public ReconcilerLookupTelemetry(ILogger<ReconcilerLookupTelemetry> logger)
    {
        this.logger = logger;
        answered = meter.CreateCounter<long>("reconciler_outcome_lookups");
        refused = meter.CreateCounter<long>("reconciler_outcome_lookup_refusals");
        latency = meter.CreateHistogram<double>("reconciler_outcome_lookup_duration", "ms");
    }

    public void Answered(SecurityCommandOutcome outcome, SecurityCommandKind kind, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var state = outcome.CommandState?.ToString() ?? "None";
        answered.Add(1, new KeyValuePair<string, object?>("found", outcome.Found), new KeyValuePair<string, object?>("command_state", state));
        latency.Record(elapsed.TotalMilliseconds);
        var mode = outcome.Membership is null ? "StatusOnly" : "Membership";
        LogAnswered(logger, outcome.OrganizationId, kind, outcome.Found, state, mode, outcome.CorrelationId, elapsed.TotalMilliseconds, outcome.SecurityCommandId);
    }

    public void Refused(int statusCode, Guid correlationId)
    {
        refused.Add(1, new KeyValuePair<string, object?>("status_code", statusCode));
        LogRefused(logger, statusCode, correlationId);
    }

    public void Dispose() => meter.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "AC-9 lookup organization {OrganizationId} kind {CommandKind} found {Found} state {CommandState} outcome {Outcome} correlation {CorrelationId} latency {LatencyMs} ms command {SecurityCommandId}")]
    private static partial void LogAnswered(ILogger logger, Guid organizationId, SecurityCommandKind commandKind, bool found, string commandState, string outcome, Guid correlationId, double latencyMs, Guid? securityCommandId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AC-9 lookup refused with status {StatusCode} correlation {CorrelationId}")]
    private static partial void LogRefused(ILogger logger, int statusCode, Guid correlationId);
}

/// <summary>Counts AC-9 refusals after authentication, authorization, rate limiting and the exception handler have answered.</summary>
public sealed class ReconcilerLookupRefusalMiddleware(RequestDelegate next, ReconcilerLookupTelemetry telemetry)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await next(context).ConfigureAwait(false);
        if (context.Request.Path.Equals(SecurityCommandOutcomeEndpoints.Route, StringComparison.OrdinalIgnoreCase)
            && context.Response.StatusCode is 400 or 401 or 403 or 422 or 429)
        {
            telemetry.Refused(context.Response.StatusCode, InternalAuthentication.Correlation(context));
        }
    }
}
