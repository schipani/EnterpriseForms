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

        var connectionString = configuration.GetConnectionString("AuthorityDb");
        services.AddDbContextFactory<AuthorityDbContext>(options =>
        {
            options.UseNpgsql(connectionString ?? "Host=unconfigured.invalid;Database=unconfigured;Username=unconfigured;Password=unconfigured", npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName));
        });
        services.AddSingleton<IAuthorityStore, AuthorityStore>();

        services.Configure<SpiceDbOptions>(configuration.GetSection(SpiceDbOptions.SectionName));
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
        services.AddSingleton<SecurityCommandService>();
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
