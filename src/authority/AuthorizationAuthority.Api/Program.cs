using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Api.Features.Health;

namespace AuthorizationAuthority.Api;

/// <summary>
/// Independently deployed Authorization Authority. Internal network only; never exposed through the browser API.
/// Host start fails closed unless an approved signing provider for the environment is configured (ADR-PB-005 §5, 4P);
/// the guard is a hosted service so build-time OpenAPI generation (host built, never started) is unaffected.
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    public static int Main(string[] args)
    {
        if (ProbeCommand.TryRun(args, out var probeExitCode))
        {
            return probeExitCode;
        }

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi(InternalApi.DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new Microsoft.OpenApi.OpenApiInfo();
                document.Info.Title = "Enterprise Forms Authorization Authority (INTERNAL)";
                document.Info.Version = "internal-v1";
                document.Info.Description = "Private contract. Reachable only over the authority network path with workload identity. Not part of the browser-facing API.";
                document.Servers = [];
                return Task.CompletedTask;
            });
        });
        builder.Services.AddSigningProviderConfiguration(builder.Configuration, builder.Environment);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();

        app.Run();
        return 0;
    }
}
