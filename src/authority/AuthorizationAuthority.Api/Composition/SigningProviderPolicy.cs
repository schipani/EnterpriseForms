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
        // RB-CARV-1 (contract §22): the content acceptance phase and required floor (section ContentAcceptance), from deployment
        // configuration only; no default; an invalid policy refuses the start and every content write.
        services.AddContentAcceptancePolicy();
        services.AddSingleton<ContentAcceptanceRuleGuard>(); // E3-M2b-1: CARV startup/readiness guard
        services.AddHostedService<ContentAcceptanceRuleMonitor>();

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

    public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) => throw new IntegrityProviderUnavailableException(reason);
}

/// <summary>
/// Readiness = provider usable AND authority store reachable AND SpiceDB schema present AND archive reachable AND the C8 M4 S1
/// projection-marker transition completed (until then no decision is pinned and the projector does not write) AND the E3-M2b-1
/// content acceptance rule fence matches this build (<see cref="ContentAcceptanceRuleGuard"/>). E3-M2f-2 (INV-4): a ready detail also states that
/// the envelope capability floor is enforced and the effective deployment floor (supporting evidence only, never proof of the build).
/// </summary>
public sealed class AuthorityReadiness(IIntegrityMacProvider provider, ISpiceDbProjection spiceDb, ISecurityEventArchive archive, IDbContextFactory<AuthorityDbContext> contextFactory, AuthorizationAuthority.Application.Common.ProjectionMarkerTransitionState transition, ContentAcceptanceRuleGuard contentAcceptanceRules, AuthorizationAuthority.Application.Features.Content.EnvelopeCapabilityFloor? envelopeCapabilityFloor = null,
    AuthorizationAuthority.Application.Features.Journal.Recovery.DPlusDurability? dplusDurability = null)
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

        var (rulesReady, rulesDetail) = contentAcceptanceRules.Current;
        if (!rulesReady)
        {
            return (false, rulesDetail);
        }

        var (spiceReady, spiceDetail) = await spiceDb.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!spiceReady)
        {
            return (false, spiceDetail);
        }

        var (archiveReady, archiveDetail) = await archive.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!archiveReady)
        {
            return (false, archiveDetail);
        }

        // SEC-BL-01 WP-5b (C-157 SC-5, amendment A-6 §26): every journal stream is D+-enabled or about to be, and D+ cannot evaluate anything without
        // its durability (W2-S commitment stores, instance pin, recovery records). A usable archive without it is a CONFIGURATION error: delivery
        // of D+ streams would never happen (the authorization gate would stay closed). Reported as not ready, never bypassed or downgraded.
        if (DPlusReadiness.Check(dplusDurability) is { Ready: false } dplus)
        {
            return (false, dplus.Detail!);
        }

        var capabilityDetail = (envelopeCapabilityFloor ?? AuthorizationAuthority.Application.Features.Content.EnvelopeCapabilityFloor.Default).Description;
        return transition.Completed ? (true, $"{providerDetail}; {spiceDetail}; {archiveDetail}; projection markers verified; {rulesDetail}; {capabilityDetail}") : (false, "ProjectionMarkerTransitionPending");
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

/// <summary>
/// SEC-BL-01 WP-5b (C-157 SC-5, amendment A-6 §26.6): readiness of the D+ composition. A host whose archive is usable but whose D+ durability
/// (journal-verification record store, instance pin, recovery verifier) is not composed can never deliver a D+ stream, so it is reported as a
/// configuration error (not an outage and not an integrity failure). Never bypassed and never downgraded.
/// </summary>
public static class DPlusReadiness
{
    public static (bool Ready, string? Detail) Check(AuthorizationAuthority.Application.Features.Journal.Recovery.DPlusDurability? durability) =>
        durability is null ? (false, AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor.DurabilityNotComposedCode) : (true, null);
}
