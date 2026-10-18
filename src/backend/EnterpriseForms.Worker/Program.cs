using EnterpriseForms.Worker.Composition;

namespace EnterpriseForms.Worker;

/// <summary>Durable background host. Feature handlers register under Features/&lt;Owner&gt; in later phases.</summary>
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
        builder.Build().Run();
    }
}
