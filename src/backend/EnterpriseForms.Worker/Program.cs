using EnterpriseForms.Infrastructure;
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
        builder.Build().Run();
    }
}
