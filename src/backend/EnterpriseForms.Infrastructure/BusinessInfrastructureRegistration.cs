using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Dashboard;
using EnterpriseForms.Application.Features.Documents.Documents;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.Documents.Processing;
using EnterpriseForms.Application.Features.Documents.UploadSessions;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.FormDefinitions.FormAccess;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Application.Features.FormDesigner.OptionLists;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.GetFormRecord;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
using EnterpriseForms.Application.Features.Organizations;
using EnterpriseForms.Application.Features.RelatedForms;
using EnterpriseForms.Application.Features.Sharing.CreateShare;
using EnterpriseForms.Application.Features.Sharing.ListShares;
using EnterpriseForms.Application.Features.Sharing.RevokeShare;
using EnterpriseForms.Application.Features.Sharing.UpdateShare;
using EnterpriseForms.Application.Features.Teams.ManageTeam;
using EnterpriseForms.Infrastructure.Authority;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Infrastructure.Features.SecurityOperations;
namespace EnterpriseForms.Infrastructure;

public static class BusinessInfrastructureRegistration
{
    public static IServiceCollection AddBusinessInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("BusinessDb");
        services.AddDbContextFactory<BusinessDbContext>(options => options.UseNpgsql(connectionString ?? "Host=unconfigured.invalid;Database=unconfigured;Username=unconfigured;Password=unconfigured"));
        services.AddSingleton<IBusinessStore, BusinessStore>();

        services.Configure<AuthorityClientOptions>(configuration.GetSection(AuthorityClientOptions.SectionName));
        services.AddHttpClient(AuthorityHttpClient.HttpClientName);
        services.AddSingleton<AuthorityHttpClient>();
        services.AddSingleton<IAuthorizationAuthorityClient>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<IBrowserSessionStateAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<IFieldSearchPolicyAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<ISearchIndexAdministrationAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>()); // IX-2 rebuild
        services.AddSingleton<IIndexedSearchAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>()); // IX-3 indexed query (Shadow)
        services.AddSingleton<IIndexedRecordListAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>()); // IX-6 indexed My Records
        services.AddSingleton<ISecurityAdministratorAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<IAuditAccessAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>()); // C8 M4-2 (AC-8.3)
        services.AddSingleton<ISecurityCommandTimelineAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>()); // C8 M4-3 (AC-8.2)
        services.AddSingleton<ISecurityOverviewAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>()); // C8 M4-4 (AC-8.4/8.5)
        services.AddSingleton<IOrganizationRosterAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<IOwnerRecoveryNoticeAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<ITestStackIdentityAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.Ports.IRecordDisplayDirectory, EnterpriseForms.Application.Features.FormRecords.Ports.RecordDisplayDirectory>();
        services.AddSingleton<EnterpriseForms.Application.Features.Search.FieldSearchPolicy.FieldSearchPolicyHandlers>();
        services.AddSingleton<EnterpriseForms.Application.Features.Search.IndexBuilder.SearchIndexAdministrationHandler>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuthorityCommandAwaiter>();
        // Every host gets the no-op fault seam; only the Testing API host replaces it (TestingWriteFaults, F-C7-01).
        services.TryAddSingleton<EnterpriseForms.Application.Common.IWriteFaultInjector>(EnterpriseForms.Application.Common.NoWriteFaults.Instance);
        // C-137 (R-37): every host gets the no-op timing sink; only a Development/Testing API host replaces it (TestingOperationTiming).
        services.TryAddSingleton<EnterpriseForms.Application.Common.IOperationTimingSink>(EnterpriseForms.Application.Common.NoOperationTiming.Instance);
        services.AddSingleton<RecordCandidateStager>();
        services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.Ports.IAcceptedRecordItemsReader, EnterpriseForms.Application.Features.FormRecords.Ports.AcceptedRecordItemsReader>();
        services.AddSingleton<VerifiedRecordReader>();
        services.AddSingleton<EnsureIdentityProjectionHandler>();
        services.AddSingleton<CreateFormDefinitionHandler>();
        // C-191: the publication coordinator's abandonment attestation key (dedicated; distinct from the workload, user-assertion and reconciler
        // keys; absent => recovery never withdraws a staged version and the publication stays pending).
        services.AddSingleton(FormPublicationAbandonmentAttestor.Create(configuration[FormPublicationAbandonmentAttestor.ConfigurationKey],
            [configuration["TrustAuthority:WorkloadSharedKeyBase64"], configuration["UserAuthentication:SharedKeyBase64"], configuration["TeamMembershipReconciler:SharedKeyBase64"]]));
        services.AddSingleton<FormDesignerHandlers>();
        services.AddSingleton<OptionListHandlers>();
        services.AddSingleton<FormAccessHandlers>();
        services.AddSingleton<SecurityPolicyHandlers>();
        services.AddSingleton<SecurityAdministratorsHandlers>();
        services.AddSingleton<SecurityAuditorsHandlers>();
        services.AddSingleton<OwnerRecoveryNoticeHandlers>();
        services.AddSingleton<EnterpriseForms.Application.Features.Identity.MemberAdministration.MemberAdministrationHandlers>();
        services.AddSingleton<CreateFormRecordHandler>();
        services.AddSingleton<GetFormRecordHandler>();
        services.AddSingleton<GetRecordProjectionHandler>();
        services.AddSingleton<UpdateFormRecordHandler>();
        services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.Lifecycle.RecordLifecycleHandler>();
        services.AddSingleton(EnterpriseForms.Application.Features.FormRecords.Lifecycle.LifecycleTestingHooks.Disabled);
        services.AddSingleton<TransferOwnershipHandler>();
        services.AddSingleton<ChildRecordHandlers>();
        services.AddSingleton<CreateShareHandler>();
        services.AddSingleton<RevokeShareHandler>();
        services.AddSingleton<UpdateShareHandler>();
        services.AddSingleton<ListSharesHandler>();
        services.AddSingleton<CreateTeamHandler>();
        services.AddSingleton<ChangeTeamMembershipHandler>();
        services.AddSingleton<TeamMembershipOperationHandlers>();
        // Phase C8: timing options (the Worker replaces this with its startup-validated instance; the API reads only the projection wait).
        services.TryAddSingleton(sp => configuration.GetSection(EnterpriseForms.Application.Features.Teams.MembershipReconciliation.TeamMembershipReconciliationOptions.SectionName).Get<EnterpriseForms.Application.Features.Teams.MembershipReconciliation.TeamMembershipReconciliationOptions>() ?? new());
        services.AddSingleton<GetRecordActivityHandler>();
        services.AddSingleton<BrandingHandlers>();
        services.AddSingleton<RegionalSettingsHandlers>();
        services.AddSingleton<EnterpriseForms.Application.Features.UserPreferences.PreferenceHandlers>();
        services.AddSingleton<OrganizationOverviewHandler>();
        services.AddSingleton<TeamDirectoryHandlers>();
        services.AddSingleton<DashboardHandler>();
        services.AddSingleton<ISecurityIncidentRecorder, SecurityIncidentRecorder>();
        services.AddSingleton<IDecryptionFailureRecorder, EnterpriseForms.Infrastructure.Features.Audit.DecryptionFailureRecorder>(); // E0-4
        services.AddSingleton<UploadSessionHandlers>();
        services.AddSingleton<DocumentHandlers>();
        services.AddSingleton<DocumentProcessingService>();
        services.AddSingleton<EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService>();

        AddDocumentProviders(services, configuration);
        return services;
    }

    /// <summary>Provider selection by deployment configuration only (never by tenant data). Unconfigured providers fail closed.</summary>
    private static void AddDocumentProviders(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DocumentStorageOptions>(configuration.GetSection(DocumentStorageOptions.SectionName));
        services.Configure<MalwareScannerOptions>(configuration.GetSection(MalwareScannerOptions.SectionName));
        services.Configure<EncryptionOptions>(configuration.GetSection(EncryptionOptions.SectionName));

        var storage = configuration[$"{DocumentStorageOptions.SectionName}:Provider"] ?? "Unconfigured";
        if (storage == "AzureBlob")
        {
            services.AddSingleton<IFileStorageProvider, AzureBlobFileStorageProvider>();
        }
        else
        {
            services.AddSingleton<IFileStorageProvider>(new UnavailableFileStorageProvider("document storage is not configured (Storage:Provider)"));
        }

        var scanner = configuration[$"{MalwareScannerOptions.SectionName}:Provider"] ?? "Unconfigured";
        if (scanner == "ClamAv")
        {
            services.AddSingleton<IFileMalwareScanner, ClamAvTcpScanner>();
        }
        else
        {
            services.AddSingleton<IFileMalwareScanner>(new UnavailableMalwareScanner("malware scanner is not configured (MalwareScanner:Provider); documents stay ScanPending"));
        }

        // E0-1: EncryptionProviderEnvironmentPolicy (applied at startup by the API and the Worker) refuses this provider outside Development/Testing.
        var keyProvider = EncryptionProviderEnvironmentPolicy.ConfiguredKeyProvider(configuration);
        if (keyProvider == EncryptionOptions.LocalDevelopmentProviderName)
        {
            services.AddSingleton<IEncryptionKeyProvider, LocalDevelopmentKeyProvider>();
        }
        else if (keyProvider == EncryptionOptions.VaultTransitProviderName)
        {
            // E-2: exactly one provider is registered; the local keyring is never registered alongside it (no fallback). Redirects are
            // not followed, so the Vault token is never sent to another address.
            services.AddHttpClient(VaultTransitKeyProvider.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddSingleton<IEncryptionKeyProvider, VaultTransitKeyProvider>();
        }
        else
        {
            services.AddSingleton<IEncryptionKeyProvider>(new UnavailableKeyProvider("no permitted encryption key provider is configured (Encryption:KeyProvider); Azure Key Vault/Managed HSM providers are not implemented in Stage B"));
        }

        services.AddSingleton<IFileEncryptionService, AesGcmFileEncryptionService>();
        services.AddSingleton<IFieldEncryptionService, AesGcmFieldEncryptionService>();
        // C8 M4 M1: seal-only Security Auditor reason notes (same primitive and key provider, its own AAD context; no decrypt anywhere).
        services.AddSingleton<EnterpriseForms.Application.Features.Organizations.ReasonNotes.IAuditorNoteSealer, EnterpriseForms.Infrastructure.Features.Organizations.AesGcmAuditorNoteSealer>();
    }
}
