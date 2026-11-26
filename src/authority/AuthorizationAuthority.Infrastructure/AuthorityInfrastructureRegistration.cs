using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.Projection;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Application.Features.Sessions;
using AuthorizationAuthority.Infrastructure.Alerts;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Faults;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Signing;
using AuthorizationAuthority.Infrastructure.SpiceDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AuthorizationAuthority.Infrastructure;

public static class AuthorityInfrastructureRegistration
{
    /// <summary>Registers store, SpiceDB, archive, fault injector and application services. The MAC provider is registered by the host after its environment policy check.</summary>
    public static IServiceCollection AddAuthorityInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // E3-M2b-1: a versioned application name on every Authority store connection, so an operator can confirm from
        // pg_stat_activity that no pre-CARV instance remains before the separate floor migration (contract §19.9 step 3).
        // C-188: every runtime connection declares that it understands retained-field approvals (FieldSearchScopeConnection).
        var connectionString = FieldSearchScopeConnection.WithScopeAwareness(ContentAcceptanceRuleConnection.WithVersionedApplicationName(configuration.GetConnectionString("AuthorityDb")));
        services.AddDbContextFactory<AuthorityDbContext>(options =>
        {
            options.UseNpgsql(connectionString ?? "Host=unconfigured.invalid;Database=unconfigured;Username=unconfigured;Password=unconfigured", npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName));
            if (environment.IsEnvironment("Testing"))
            {
                options.AddInterceptors(new AuthorityCommandCountingInterceptor()); // IX-1 measurement only (round-trip counts)
            }
        });
        // C-150 R56-B: committed authorization changes wake the work loop (transactional NOTIFY + in-process signal); the poll stays the fallback.
        services.AddSingleton(configuration.GetSection(AuthorityWorkOptions.SectionName).Get<AuthorityWorkOptions>() ?? new AuthorityWorkOptions());
        services.AddSingleton<AuthorityWorkSignal>();
        // SEC-BL-01 E-6/E-7 (A-7 §27): the trusted delivery-receipt verifier over the archive and the D+ durability (record stores, instance pin).
        // Without D+ durability composed it verifies nothing (fail closed), consistent with SC-5.
        services.AddSingleton<AuthorizationAuthority.Application.Features.Journal.TrustedJournalReceipts>(sp => new AuthorizationAuthority.Application.Features.Journal.TrustedJournalReceipts(
            sp.GetRequiredService<ISecurityEventArchive>(), sp.GetService<AuthorizationAuthority.Application.Features.Journal.Recovery.DPlusDurability>(),
            // WP-7A (A-9 TR-6′ RV-2): the current-state freshness bound, validated (0..30 s) at construction; an invalid value refuses start.
            new AuthorizationAuthority.Application.Features.Journal.TrustedJournalReceiptOptions
            {
                CurrentStateMaxAge = TimeSpan.FromMilliseconds(configuration.GetValue("JournalReceipts:CurrentStateMaxAgeMilliseconds", 1000)),
            }));
        services.AddSingleton<IJournalReceiptVerifier>(sp => sp.GetRequiredService<AuthorizationAuthority.Application.Features.Journal.TrustedJournalReceipts>());
        services.AddSingleton<IAuthorityStore, AuthorityStore>();
        services.AddSingleton<IContentAcceptanceRuleSchemaReader, ContentAcceptanceRuleSchemaReader>();

        services.Configure<SpiceDbOptions>(configuration.GetSection(SpiceDbOptions.SectionName));
        // C-149 (P-2): measurement-only SpiceDB call counting; off unless configured, refused outside Development/Testing.
        var countSpiceDbCalls = configuration.GetValue<bool>(SpiceDbCallCounter.ConfigurationKey);
        if (countSpiceDbCalls && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException($"{SpiceDbCallCounter.ConfigurationKey} is a Development/Testing diagnostic and must not be set in the {environment.EnvironmentName} environment. Refusing to start.");
        }

        services.AddSingleton(new SpiceDbCallCounter(countSpiceDbCalls));
        services.AddSingleton<ISpiceDbProjection, SpiceDbProjectionClient>();

        services.Configure<ArchiveOptions>(configuration.GetSection(ArchiveOptions.SectionName));
        var archiveProvider = configuration.GetValue<string>($"{ArchiveOptions.SectionName}:Provider") ?? "Unconfigured";
        if (string.Equals(archiveProvider, "Blob", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISecurityEventArchive, BlobSecurityEventArchive>();
        }
        else if (string.Equals(archiveProvider, "InMemory", StringComparison.OrdinalIgnoreCase) && environment.IsDevelopment())
        {
            services.AddSingleton<ISecurityEventArchive, InMemorySecurityEventArchive>();
        }
        else
        {
            services.AddSingleton<ISecurityEventArchive>(new UnavailableSecurityEventArchive($"SecurityArchiveUnconfigured: provider '{archiveProvider}' is not permitted in {environment.EnvironmentName}; use Blob (InMemory is Development only)."));
        }

        services.Configure<FaultInjectionOptions>(configuration.GetSection(FaultInjectionOptions.SectionName));
        var faultsEnabled = configuration.GetValue<bool>($"{FaultInjectionOptions.SectionName}:Enabled");
        if (faultsEnabled && environment.IsEnvironment("Testing"))
        {
            services.AddSingleton<ProcessCrashFaultInjector>();
            services.AddSingleton<IFaultInjector>(sp => sp.GetRequiredService<ProcessCrashFaultInjector>());
        }
        else
        {
            services.AddSingleton<IFaultInjector, NoOpFaultInjector>();
        }

        services.AddSingleton(TimeProvider.System);
        services.TryAddSingleton(FieldSearchApprovalGate.Unavailable); // the API host registers the configured gate (OD-7)
        services.TryAddSingleton(FormPublicationAbandonmentVerifier.Unconfigured); // C-191: the API host registers the configured key; absent => no abandonment is accepted
        services.AddSingleton<SecurityCommandService>();
        services.AddSingleton<ShareExpiryConvergenceService>(); // C10 M1-WP6: internal ExpireShare (no endpoint)
        // C8 M4 S1: projection-integrity protocol (pin, decide, verify), startup marker transition and periodic check.
        services.AddSingleton<ProjectionMarkerTransitionState>();
        services.AddSingleton<ProjectionIntegrityMetrics>();
        services.AddSingleton<ProjectionIntegrityVerifier>();
        services.AddSingleton<ProjectionIntegrityMonitor>();
        services.AddSingleton<PermissionCheckService>();
        services.AddSingleton<FormAccessService>();
        services.AddSingleton<ContentService>();
        services.AddSingleton<ProjectionProcessor>();
        services.AddSingleton<ArchiveProcessor>();
        services.AddSingleton<OrganizationBootstrapService>();
        services.TryAddSingleton(OperatorApprovalVerifier.Unavailable); // the API host registers the configured verifier (DR-C7-3)
        services.AddSingleton<ISecurityAlertSink, LoggingSecurityAlertSink>();
        services.AddSingleton<SecurityAdministratorProvisioningService>();
        var sessionOptions = configuration.GetSection(SessionOptions.SectionName).Get<SessionOptions>() ?? new SessionOptions();
        SessionOptions.Validate(sessionOptions);
        services.AddSingleton(sessionOptions);
        services.AddSingleton<SessionService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.FieldSearch.FieldSearchCapabilityService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.FieldSearch.SecurityAdministratorRosterService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.Membership.OrganizationRosterService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.Membership.MembershipProvisioningService>();
        // C8 M2 Stages 1-2: Platform Operators. Unavailable unless the API host registers the configured policy (Development/Testing only).
        services.TryAddSingleton(AuthorizationAuthority.Application.Features.PlatformOperators.PlatformOperatorPolicy.Unavailable);
        services.AddSingleton<AuthorizationAuthority.Application.Features.PlatformOperators.PlatformOperatorEvidenceVerifier>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.PlatformOperators.PlatformJournal>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.PlatformOperators.PlatformOperatorRegistryService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.PlatformOperators.PlatformMembershipApprovalService>();
        // C8 onboarding (C-104): unavailable (no issuers, legacy bootstrap off) unless the API host registers the configured policy.
        services.TryAddSingleton(AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationOnboardingPolicy.Create(null, "Production"));
        services.AddSingleton<AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationOnboardingService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.OrganizationOnboarding.OwnerRecoveryService>();
        services.AddSingleton<AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationDirectoryService>();
        return services;
    }

    public static IServiceCollection AddLocalDevelopmentMacProvider(this IServiceCollection services) =>
        services.AddSingleton<IIntegrityMacProvider, LocalHmacProvider>();

    public static IServiceCollection AddVaultTransitMacProvider(this IServiceCollection services, IConfiguration configuration, string vaultAddress)
    {
        services.Configure<VaultTransitOptions>(configuration.GetSection(VaultTransitOptions.SectionName));
        services.AddHttpClient(VaultTransitHmacProvider.HttpClientName, client => client.BaseAddress = new Uri(vaultAddress, UriKind.Absolute));
        services.AddSingleton<IIntegrityMacProvider, VaultTransitHmacProvider>();
        return services;
    }
}
