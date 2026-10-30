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
using EnterpriseForms.Application.Features.Teams.ManageTeam;
using EnterpriseForms.Infrastructure.Authority;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddSingleton<ISecurityAdministratorAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<ITestStackIdentityAuthority>(sp => sp.GetRequiredService<AuthorityHttpClient>());
        services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.Ports.IRecordDisplayDirectory, EnterpriseForms.Application.Features.FormRecords.Ports.RecordDisplayDirectory>();
        services.AddSingleton<EnterpriseForms.Application.Features.Search.FieldSearchPolicy.FieldSearchPolicyHandlers>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuthorityCommandAwaiter>();
        // Every host gets the no-op fault seam; only the Testing API host replaces it (TestingWriteFaults, F-C7-01).
        services.TryAddSingleton<EnterpriseForms.Application.Common.IWriteFaultInjector>(EnterpriseForms.Application.Common.NoWriteFaults.Instance);
        services.AddSingleton<RecordCandidateStager>();
        services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.Ports.IAcceptedRecordItemsReader, EnterpriseForms.Application.Features.FormRecords.Ports.AcceptedRecordItemsReader>();
        services.AddSingleton<VerifiedRecordReader>();
        services.AddSingleton<EnsureIdentityProjectionHandler>();
        services.AddSingleton<CreateFormDefinitionHandler>();
        services.AddSingleton<FormDesignerHandlers>();
        services.AddSingleton<OptionListHandlers>();
        services.AddSingleton<FormAccessHandlers>();
        services.AddSingleton<SecurityPolicyHandlers>();
        services.AddSingleton<SecurityAdministratorsHandlers>();
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
        services.AddSingleton<ListSharesHandler>();
        services.AddSingleton<CreateTeamHandler>();
        services.AddSingleton<ChangeTeamMembershipHandler>();
        services.AddSingleton<GetRecordActivityHandler>();
        services.AddSingleton<BrandingHandlers>();
        services.AddSingleton<DashboardHandler>();
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

        var keyProvider = configuration[$"{EncryptionOptions.SectionName}:KeyProvider"] ?? "Unconfigured";
        if (keyProvider == "LocalDevelopment")
        {
            services.AddSingleton<IEncryptionKeyProvider, LocalDevelopmentKeyProvider>();
        }
        else
        {
            services.AddSingleton<IEncryptionKeyProvider>(new UnavailableKeyProvider("no permitted encryption key provider is configured (Encryption:KeyProvider); Azure Key Vault/Managed HSM providers are not implemented in Stage B"));
        }

        services.AddSingleton<IFileEncryptionService, AesGcmFileEncryptionService>();
        services.AddSingleton<IFieldEncryptionService, AesGcmFieldEncryptionService>();
    }
}
