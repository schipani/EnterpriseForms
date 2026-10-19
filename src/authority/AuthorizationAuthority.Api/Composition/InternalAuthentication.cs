using System.Security.Claims;
using AuthorizationAuthority.Application.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// Service-to-service authentication for the internal contract (TRUST_AUTHORITY_ARCHITECTURE.md §5):
/// (1) a workload bearer credential identifying the business API deployment, (2) a separate operator credential for
/// the operator plane, and (3) independent validation of the delegated end-user assertion carried in
/// <c>X-Delegated-User-Assertion</c>. The authority never trusts a user id in JSON from the business API.
/// Stage A uses HS256 shared credentials issued through deployment configuration (synthetic in Compose); production
/// requires mTLS + Entra workload identity and Entra user tokens (PB-08) — the TestIdentityProvider is refused there.
/// </summary>
public static class InternalAuthentication
{
    public const string WorkloadScheme = "Workload";
    public const string OperatorScheme = "Operator";
    public const string WorkloadPolicy = "WorkloadCaller";
    public const string OperatorPolicy = "OperatorCaller";
    public const string UserAssertionHeader = "X-Delegated-User-Assertion";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static IServiceCollection AddInternalAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.SectionName));
        services.AddSingleton<DelegatedUserAssertionValidator>();
        services.AddHostedService<IdentityPolicyStartupGuard>();

        var identity = configuration.GetSection(IdentityOptions.SectionName).Get<IdentityOptions>() ?? new IdentityOptions();
        services.AddAuthentication(WorkloadScheme)
            .AddJwtBearer(WorkloadScheme, options => Configure(options, identity.Workload))
            .AddJwtBearer(OperatorScheme, options => Configure(options, identity.Operator));
        services.AddAuthorizationBuilder()
            .AddPolicy(WorkloadPolicy, policy => policy.AddAuthenticationSchemes(WorkloadScheme).RequireAuthenticatedUser().RequireClaim("workload_role", "business-api"))
            .AddPolicy(OperatorPolicy, policy => policy.AddAuthenticationSchemes(OperatorScheme).RequireAuthenticatedUser().RequireClaim("workload_role", "authority-operator"));
        return services;
    }

    private static void Configure(JwtBearerOptions options, CredentialOptions credential)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = credential.Issuer ?? "unconfigured",
            ValidAudience = credential.Audience ?? "unconfigured",
            IssuerSigningKey = credential.SymmetricKey(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };
    }

    /// <summary>Resolves the delegated end user from the assertion header; absent/invalid ⇒ 401.</summary>
    public static VerifiedActor RequireActor(HttpContext context, DelegatedUserAssertionValidator validator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validator);
        var assertion = context.Request.Headers[UserAssertionHeader].ToString();
        var workload = context.User.FindFirstValue("workload_name") ?? "unknown";
        return validator.Validate(assertion, workload);
    }

    /// <summary>Like <see cref="RequireActor"/> but returns null when no assertion header is present (workload-only operations that carry their own authorization reference). An invalid assertion is still rejected.</summary>
    public static VerifiedActor? TryActor(HttpContext context, DelegatedUserAssertionValidator validator)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.IsNullOrWhiteSpace(context.Request.Headers[UserAssertionHeader].ToString()) ? null : RequireActor(context, validator);
    }

    public static Guid Correlation(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Guid.TryParse(context.Request.Headers[CorrelationHeader].ToString(), out var id) ? id : Guid.CreateVersion7();
    }
}

public class CredentialOptions
{
    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    /// <summary>Base64 of a ≥32-byte random secret, from deployment secret configuration. Never committed.</summary>
    public string? SharedKeyBase64 { get; set; }

    public SymmetricSecurityKey SymmetricKey()
    {
        var bytes = string.IsNullOrWhiteSpace(SharedKeyBase64) ? new byte[32] : Convert.FromBase64String(SharedKeyBase64);
        return new SymmetricSecurityKey(bytes.Length >= 32 ? bytes : new byte[32]);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(SharedKeyBase64) && Convert.FromBase64String(SharedKeyBase64).Length >= 32;
}

public sealed class UserAssertionOptions : CredentialOptions
{
    /// <summary>TestIdentityProvider (Development/Testing only) or EntraId (not implemented in Stage A ⇒ refused at startup, fail closed).</summary>
    public string ProviderKind { get; set; } = "Unconfigured";
}

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public CredentialOptions Workload { get; set; } = new();

    public CredentialOptions Operator { get; set; } = new();

    public UserAssertionOptions UserAssertion { get; set; } = new();
}

public sealed class DelegatedUserAssertionValidator(IOptions<IdentityOptions> options)
{
    private readonly JsonWebTokenHandler handler = new();

    public VerifiedActor Validate(string assertion, string workloadName)
    {
        var settings = options.Value.UserAssertion;
        if (string.IsNullOrWhiteSpace(assertion) || !settings.IsConfigured)
        {
            throw new AuthorityException(AuthorityErrorCodes.Unauthorized, 401, false, "delegated user assertion missing");
        }

        var result = handler.ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = settings.SymmetricKey(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        }).GetAwaiter().GetResult();
        if (!result.IsValid)
        {
            throw new AuthorityException(AuthorityErrorCodes.Unauthorized, 401, false, "delegated user assertion invalid");
        }

        var subject = result.Claims.TryGetValue("sub", out var sub) ? sub?.ToString() : null;
        var issuer = result.Claims.TryGetValue("iss", out var iss) ? iss?.ToString() : null;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer))
        {
            throw new AuthorityException(AuthorityErrorCodes.Unauthorized, 401, false, "delegated user assertion lacks iss/sub");
        }

        return new VerifiedActor(issuer, subject, workloadName);
    }
}

/// <summary>Fail closed: all three credentials configured; TestIdentityProvider only in Development/Testing; Production needs EntraId (unimplemented ⇒ refuse).</summary>
public sealed class IdentityPolicyStartupGuard(IOptions<IdentityOptions> options, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return Task.CompletedTask;
        }

        var identity = options.Value;
        if (!identity.Workload.IsConfigured || !identity.Operator.IsConfigured || !identity.UserAssertion.IsConfigured)
        {
            throw new InvalidOperationException("Identity:Workload, Identity:Operator and Identity:UserAssertion must be configured with ≥256-bit secrets (fail closed).");
        }

        if (identity.Workload.SharedKeyBase64 == identity.Operator.SharedKeyBase64 || identity.Workload.SharedKeyBase64 == identity.UserAssertion.SharedKeyBase64)
        {
            throw new InvalidOperationException("Workload, operator and user-assertion credentials must be distinct.");
        }

        var provider = identity.UserAssertion.ProviderKind;
        var testProviderAllowed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (string.Equals(provider, "TestIdentityProvider", StringComparison.Ordinal) && !testProviderAllowed)
        {
            throw new InvalidOperationException("TestIdentityProvider user assertions are permitted only in Development/Testing (PB-08).");
        }

        if (!string.Equals(provider, "TestIdentityProvider", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Identity:UserAssertion:ProviderKind '{provider}' is not implemented in Stage A; Entra ID validation (PB-08) is required before Staging/Production. Refusing to start.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
