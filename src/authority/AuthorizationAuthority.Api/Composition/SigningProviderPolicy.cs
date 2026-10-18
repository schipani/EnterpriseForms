using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>Integrity MAC provider selection (trust_authority/CRYPTOGRAPHIC_SIGNING_AUTHORITY.md). Resolved from deployment configuration only.</summary>
public enum SigningProviderKind
{
    /// <summary>Unset. Startup is refused.</summary>
    Unconfigured = 0,

    /// <summary>In-process random keys. Unit tests and local Development ONLY.</summary>
    LocalDevelopment = 1,

    /// <summary>HashiCorp Vault Transit dev mode in the Docker Compose stack. Local/integration ONLY.</summary>
    VaultTransit = 2,

    /// <summary>Azure Managed HSM oct-HSM HS256 keys. The only provider permitted in Production.</summary>
    AzureManagedHsm = 3,
}

public sealed class SigningProviderOptions
{
    public const string SectionName = "Signing";

    public SigningProviderKind Provider { get; set; } = SigningProviderKind.Unconfigured;

    /// <summary>Vault address for <see cref="SigningProviderKind.VaultTransit"/>; must be a private/loopback address.</summary>
    public string? VaultAddress { get; set; }
}

public sealed class SigningProviderPolicy(IOptions<SigningProviderOptions> options, IHostEnvironment environment)
{
    public SigningProviderKind Provider => options.Value.Provider;

    public string? VaultAddress => options.Value.VaultAddress;

    /// <summary>Fail closed: Production accepts only Azure Managed HSM; test providers are confined to Development/Testing/Staging-integration.</summary>
    public void EnsureAllowedForEnvironment()
    {
        var provider = options.Value.Provider;
        if (provider == SigningProviderKind.Unconfigured)
        {
            throw new InvalidOperationException("Signing:Provider is not configured. The Authorization Authority refuses to start without an approved integrity MAC provider.");
        }

        if (environment.IsProduction() && provider != SigningProviderKind.AzureManagedHsm)
        {
            throw new InvalidOperationException($"Signing provider '{provider}' is forbidden in Production. Only AzureManagedHsm is permitted (ADR-PB-005 §5).");
        }

        if (provider == SigningProviderKind.LocalDevelopment && !(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            throw new InvalidOperationException("LocalDevelopment signing provider is permitted only in Development or Testing environments.");
        }

        if (provider == SigningProviderKind.VaultTransit)
        {
            if (!Uri.TryCreate(options.Value.VaultAddress, UriKind.Absolute, out var vault))
            {
                throw new InvalidOperationException("Signing:VaultAddress must be an absolute URI when Provider is VaultTransit.");
            }

            if (environment.IsStaging())
            {
                throw new InvalidOperationException("VaultTransit is a local Docker provider and is forbidden in Staging; use AzureManagedHsm.");
            }

            _ = vault;
        }
    }
}

public static class SigningProviderRegistration
{
    public static IServiceCollection AddSigningProviderConfiguration(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        _ = environment;
        services.Configure<SigningProviderOptions>(configuration.GetSection(SigningProviderOptions.SectionName));
        services.AddSingleton<SigningProviderPolicy>();
        services.AddHostedService<SigningProviderStartupGuard>();
        services.AddHttpClient(SigningProviderReadiness.VaultHealthClientName);
        services.AddSingleton<SigningProviderReadiness>();
        return services;
    }
}

/// <summary>Readiness reflects whether the configured provider is actually usable. Phase 0 implements configuration and Vault health only.</summary>
public sealed class SigningProviderReadiness(SigningProviderPolicy policy, IHttpClientFactory httpClientFactory)
{
    public const string VaultHealthClientName = "vault-health";

    public async Task<(bool Ready, string Detail)> CheckAsync(CancellationToken cancellationToken)
    {
        switch (policy.Provider)
        {
            case SigningProviderKind.LocalDevelopment:
                return (true, "LocalDevelopment provider (TEST ONLY; not a key custodian)");
            case SigningProviderKind.VaultTransit:
                try
                {
                    using var client = httpClientFactory.CreateClient(VaultHealthClientName);
                    client.BaseAddress = new Uri(policy.VaultAddress!, UriKind.Absolute);
                    client.Timeout = TimeSpan.FromSeconds(2);
                    using var response = await client.GetAsync(new Uri("v1/sys/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
                    return response.IsSuccessStatusCode ? (true, "VaultTransit reachable (DEV ONLY)") : (false, "VaultUnhealthy");
                }
                catch (HttpRequestException)
                {
                    return (false, "VaultUnreachable");
                }
                catch (TaskCanceledException)
                {
                    return (false, "VaultTimeout");
                }
            case SigningProviderKind.AzureManagedHsm:
                return (false, "AzureManagedHsmProviderNotImplemented");
            default:
                return (false, "SigningProviderUnconfigured");
        }
    }
}

/// <summary>Runs first on host start; throwing here aborts startup with a non-zero exit (fail closed).</summary>
public sealed class SigningProviderStartupGuard(SigningProviderPolicy policy) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            policy.EnsureAllowedForEnvironment();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
