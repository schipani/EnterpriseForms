using System.Security.Claims;
using EnterpriseForms.Application.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// End-user authentication for Stage A: bearer assertions from the TestIdentityProvider (HS256, Development/Testing
/// only). The production BFF with Entra ID OIDC (PB-08) replaces this; the startup guard refuses the test provider in
/// Staging/Production. The raw assertion is forwarded to the authority, which validates it independently — the business
/// API never re-asserts identity claims to the authority.
/// </summary>
public static class BusinessAuthentication
{
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;
    public const string Policy = "AuthenticatedUser";
    public const string CorrelationHeader = "X-Correlation-Id";

    public static IServiceCollection AddBusinessAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<UserAuthenticationOptions>(configuration.GetSection(UserAuthenticationOptions.SectionName));
        services.AddHostedService<UserAuthenticationStartupGuard>();
        var settings = configuration.GetSection(UserAuthenticationOptions.SectionName).Get<UserAuthenticationOptions>() ?? new UserAuthenticationOptions();
        services.AddAuthentication(Scheme).AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = settings.Issuer ?? "unconfigured",
                ValidAudience = settings.Audience ?? "unconfigured",
                IssuerSigningKey = settings.SymmetricKey(),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true,
                RequireExpirationTime = true,
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Keep the raw assertion for forwarding; it is never persisted or logged.
                    if (context.Request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        context.HttpContext.Items[RawAssertionItem] = header[7..].Trim();
                    }

                    return Task.CompletedTask;
                },
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    public const string RawAssertionItem = "EnterpriseForms.RawAssertion";

    /// <summary>Builds the request context from the validated principal. organization_id is an assertion that the authority checks on every call.</summary>
    public static RequestContext RequireContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        var issuer = user.FindFirstValue("iss");
        var subject = user.FindFirstValue("sub");
        var organization = user.FindFirstValue("org");
        var authorityUser = user.FindFirstValue("authority_user_id");
        var raw = context.Items[RawAssertionItem] as string;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(organization, out var organizationId) || !Guid.TryParse(authorityUser, out var localUserId) || string.IsNullOrWhiteSpace(raw))
        {
            throw new BusinessException("UNAUTHORIZED", 401, "assertion lacks iss/sub/org/authority_user_id");
        }

        var correlation = Guid.TryParse(context.Request.Headers[CorrelationHeader].ToString(), out var id) ? id : Guid.CreateVersion7();
        return new RequestContext(organizationId, localUserId, issuer, subject, user.FindFirstValue("name") ?? subject, raw, correlation);
    }
}

public sealed class UserAuthenticationOptions
{
    public const string SectionName = "UserAuthentication";

    public string ProviderKind { get; set; } = "Unconfigured";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? SharedKeyBase64 { get; set; }

    public SymmetricSecurityKey SymmetricKey()
    {
        var bytes = string.IsNullOrWhiteSpace(SharedKeyBase64) ? new byte[32] : Convert.FromBase64String(SharedKeyBase64);
        return new SymmetricSecurityKey(bytes.Length >= 32 ? bytes : new byte[32]);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(SharedKeyBase64) && Convert.FromBase64String(SharedKeyBase64).Length >= 32;
}

/// <summary>Fail closed: the TestIdentityProvider is permitted only in Development/Testing; Entra BFF (PB-08) is required before Staging/Production.</summary>
public sealed class UserAuthenticationStartupGuard(IOptions<UserAuthenticationOptions> options, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return Task.CompletedTask;
        }

        var settings = options.Value;
        var testAllowed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (settings.ProviderKind == "TestIdentityProvider" && testAllowed && settings.IsConfigured)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"UserAuthentication:ProviderKind '{settings.ProviderKind}' is not permitted in {environment.EnvironmentName} or is not configured. Stage A supports TestIdentityProvider in Development/Testing only; Entra ID BFF (PB-08) is required otherwise. Refusing to start.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
