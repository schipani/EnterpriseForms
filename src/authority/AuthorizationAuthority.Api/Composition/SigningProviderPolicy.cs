using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Infrastructure;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Azure Managed HSM oct-HSM HS256 keys. The only provider permitted in Production. Not implemented in Stage A.</summary>
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

    /// <summary>Fail closed: Production accepts only Azure Managed HSM; test providers are confined to Development/Testing.</summary>
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
            if (!Uri.TryCreate(options.Value.VaultAddress, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("Signing:VaultAddress must be an absolute URI when Provider is VaultTransit.");
            }

            if (environment.IsStaging())
            {
                throw new InvalidOperationException("VaultTransit is a local Docker provider and is forbidden in Staging; use AzureManagedHsm.");
            }
        }
    }
}

public static class SigningProviderRegistration
{
    public static IServiceCollection AddSigningProviderConfiguration(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<SigningProviderOptions>(configuration.GetSection(SigningProviderOptions.SectionName));
        services.AddSingleton<SigningProviderPolicy>();
        services.AddHostedService<SigningProviderStartupGuard>();
        services.AddSingleton<AuthorityReadiness>();

        // Provider implementation is chosen here and nowhere else. Unsupported/unconfigured kinds register a provider that always fails closed.
        var kind = configuration.GetSection(SigningProviderOptions.SectionName).GetValue<SigningProviderKind>("Provider");
        var vaultAddress = configuration.GetSection(SigningProviderOptions.SectionName).GetValue<string>("VaultAddress");
        switch (kind)
        {
            case SigningProviderKind.LocalDevelopment when environment.IsDevelopment() || environment.IsEnvironment("Testing"):
                services.AddLocalDevelopmentMacProvider();
                break;
            case SigningProviderKind.VaultTransit when !environment.IsProduction() && !environment.IsStaging() && Uri.TryCreate(vaultAddress, UriKind.Absolute, out _):
                services.AddVaultTransitMacProvider(configuration, vaultAddress!);
                break;
            default:
                services.AddSingleton<IIntegrityMacProvider>(new UnavailableMacProvider(kind == SigningProviderKind.AzureManagedHsm ? "AzureManagedHsmProviderNotImplemented" : "SigningProviderUnconfigured"));
                break;
        }

        return services;
    }
}

/// <summary>Registered when no approved provider is usable: every operation fails closed; readiness reports the reason.</summary>
public sealed class UnavailableMacProvider(string reason) : IIntegrityMacProvider
{
    public string ProviderName => "Unavailable";

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) => throw new IntegrityProviderUnavailableException(reason);

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken) => throw new IntegrityProviderUnavailableException(reason);

    public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => throw new IntegrityProviderUnavailableException(reason);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((false, reason));
}

/// <summary>Readiness = provider usable AND authority store reachable AND SpiceDB schema present AND archive reachable.</summary>
public sealed class AuthorityReadiness(IIntegrityMacProvider provider, ISpiceDbProjection spiceDb, ISecurityEventArchive archive, IDbContextFactory<AuthorityDbContext> contextFactory)
{
    public async Task<(bool Ready, string Detail)> CheckAsync(CancellationToken cancellationToken)
    {
        var (providerReady, providerDetail) = await provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!providerReady)
        {
            return (false, providerDetail);
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return (false, "AuthorityDbUnreachable");
            }
        }
        catch (Npgsql.NpgsqlException)
        {
            return (false, "AuthorityDbUnreachable");
        }

        var (spiceReady, spiceDetail) = await spiceDb.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!spiceReady)
        {
            return (false, spiceDetail);
        }

        var (archiveReady, archiveDetail) = await archive.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return archiveReady ? (true, $"{providerDetail}; {spiceDetail}; {archiveDetail}") : (false, archiveDetail);
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
