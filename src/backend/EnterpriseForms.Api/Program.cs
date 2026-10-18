using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.Platform;

namespace EnterpriseForms.Api;

/// <summary>Business API/BFF composition root. Feature endpoints register themselves under <c>/api/v1</c>.</summary>
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
        builder.Services.AddOpenApi(ApiVersions.V1DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new Microsoft.OpenApi.OpenApiInfo();
                document.Info.Title = "Enterprise Forms Business API";
                document.Info.Version = "v1";
                document.Info.Description = "Browser-facing business API generated from compiled feature endpoints (ADR-PB-006). The Authorization Authority contract is private and published separately.";
                document.Servers = [];
                return Task.CompletedTask;
            });
        });

        builder.Services.AddTrustAuthorityReadinessProbe(builder.Configuration);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSecurityHeaders();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapPlatformEndpoints();

        app.Run();
        return 0;
    }
}
