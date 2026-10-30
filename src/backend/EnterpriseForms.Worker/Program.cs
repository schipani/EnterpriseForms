using EnterpriseForms.Infrastructure;
using EnterpriseForms.Infrastructure.Authority;
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
        builder.Services.AddBusinessInfrastructure(builder.Configuration);
        builder.Services.AddHostedService<DocumentProcessingWorker>();
        builder.Services.AddHostedService<EnterpriseForms.Worker.Features.FormDesigner.FieldProtectionMigrationWorker>();
        // Phase C7 M4 (OD-1): the field-protection verifier credential and the reconciliation that uses it exist in this host only.
        builder.Services.AddProtectionVerifier(builder.Configuration);
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionReconciliationService>();
        builder.Services.AddHostedService<EnterpriseForms.Worker.Features.FormDesigner.FieldProtectionReconciliationWorker>();
        builder.Build().Run();
    }
}
