using EnterpriseForms.Infrastructure;
using EnterpriseForms.Infrastructure.Authority;
using EnterpriseForms.Infrastructure.Persistence;
using EnterpriseForms.Worker.Composition;
using EnterpriseForms.Worker.Features.Documents;

namespace EnterpriseForms.Worker;

/// <summary>Durable background host: document processing (scan → encrypt → store). Same application abstractions as the API, separate deployable image.</summary>
public sealed class Program
{
    private Program()
    {
    }

    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddOptions<WorkerStartupOptions>()
            .Bind(builder.Configuration.GetSection(WorkerStartupOptions.SectionName));
        builder.Services.AddHostedService<StartupConfigurationGuard>();
        // E0-1: same key-provider environment rule as the Business API; registered before every processing service (hosted services start in order).
        builder.Services.AddHostedService<EncryptionProviderStartupGuard>();
        builder.Services.AddBusinessInfrastructure(builder.Configuration);
        // E3-M2b-2 (D-5): the required business migrations are verified before any processing service starts, then every 30 s.
        builder.Services.AddBusinessSchemaGuard();
        builder.Services.AddHostedService<BusinessSchemaStartupGuard>();
        builder.Services.AddHostedService<BusinessSchemaMonitor>();
        builder.Services.AddHostedService<DocumentProcessingWorker>();
        builder.Services.AddHostedService<EnterpriseForms.Worker.Features.FormDesigner.FieldProtectionMigrationWorker>();
        // Phase C7 M4 (OD-1): the field-protection verifier credential and the reconciliation that uses it exist in this host only.
        builder.Services.AddProtectionVerifier(builder.Configuration);
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionReconciliationService>();
        builder.Services.AddHostedService<EnterpriseForms.Worker.Features.FormDesigner.FieldProtectionReconciliationWorker>();
        // IX-2 (IX-D6): the search-index builder credential and its loop exist in this host only (idle when unconfigured).
        builder.Services.AddSearchIndexBuilder(builder.Configuration);
        builder.Services.AddHostedService<EnterpriseForms.Worker.Features.Search.SearchIndexBuilderWorker>();
        // Phase C8 (C8-M1-01, AC-9): the team-membership reconciler credential and the reconciler exist in this host only. Its
        // timing parameters are validated at startup (D8-16); shortened windows are refused outside Testing.
        builder.Services.AddTeamMembershipReconciler(builder.Configuration, builder.Environment.EnvironmentName);
        var reconciliationOptions = builder.Configuration.GetSection(EnterpriseForms.Application.Features.Teams.MembershipReconciliation.TeamMembershipReconciliationOptions.SectionName).Get<EnterpriseForms.Application.Features.Teams.MembershipReconciliation.TeamMembershipReconciliationOptions>() ?? new();
        reconciliationOptions.Validate(builder.Environment.EnvironmentName, builder.Configuration.GetValue("TrustAuthority:TimeoutSeconds", 10));
        builder.Services.AddSingleton(reconciliationOptions);
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.Teams.MembershipReconciliation.TeamMembershipReconciliationService>();
        builder.Services.AddHostedService<EnterpriseForms.Worker.Features.Teams.TeamMembershipReconciliationWorker>();
        builder.Build().Run();
    }
}
