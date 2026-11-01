using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Features.OperatorApprovals;
using EnterpriseForms.PlatformConsole.Features.Session;
using EnterpriseForms.PlatformConsole.Infrastructure;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole;

/// <summary>
/// C8 M2 Stage 3: the Platform Console backend-for-frontend (D8-33 option A, approved C-99). Separately deployed from the business
/// application: own origin, own cookie, own Authority credential (Platform Console workload + operator assertion), no business
/// credential, no database, no private key (operator evidence is signed in Vault Transit with each operator's own key). Every
/// operation is the Authority's platform plane; the console adds sessions, CSRF/origin checks and evidence signing for the
/// signed-in operator only. Development/Testing only: startup refuses any other environment until PB-08 exists.
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
        builder.Services.Configure<PlatformConsoleOptions>(builder.Configuration.GetSection(PlatformConsoleOptions.SectionName));
        if (!HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            PlatformConsoleOptionsValidation.Validate(builder.Configuration.GetSection(PlatformConsoleOptions.SectionName).Get<PlatformConsoleOptions>() ?? new PlatformConsoleOptions(), builder.Environment.EnvironmentName);
        }

        builder.Services.ConfigureHttpJsonOptions(options => ConsoleJson.ConfigureBrowser(options.SerializerOptions));
        builder.Services.AddProblemDetails();
        // Malformed or over-specified bodies (unknown members) become a problem body through the exception handler in every environment.
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddExceptionHandler<ConsoleExceptionHandler>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<OperatorSessionStore>();
        builder.Services.AddScoped<OperatorDecisionService>();
        builder.Services.AddConsoleRateLimits();
        builder.Services.AddHttpClient("health");
        builder.Services.AddHttpClient<AuthorityPlatformClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(EnsureSlash(sp.GetRequiredService<IOptions<PlatformConsoleOptions>>().Value.Authority.BaseAddress));
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<IOperatorEvidenceSigner, VaultTransitEvidenceSigner>((sp, client) =>
        {
            client.BaseAddress = new Uri(EnsureSlash(sp.GetRequiredService<IOptions<PlatformConsoleOptions>>().Value.Vault.Address));
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new Microsoft.OpenApi.OpenApiInfo();
                document.Info.Title = "Enterprise Forms Platform Console (Development/Testing)";
                document.Info.Version = "v1";
                document.Info.Description = "Backend-for-frontend of the separately deployed Platform Console (PG-070 Operator approvals). Cookie session + CSRF; the signed-in Platform Operator is always the session's. Not part of the business API.";
                document.Servers = [];
                return Task.CompletedTask;
            });
        });

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseConsoleSecurityHeaders();
        app.UseConsoleOriginPolicy();
        app.UseRateLimiter();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapConsoleHealth();
        app.MapSessionEndpoints();
        app.MapOperatorApprovalEndpoints();
        app.Run();
        return 0;
    }

    private static string EnsureSlash(string? address) => string.IsNullOrEmpty(address) ? "http://unconfigured.invalid/" : address.EndsWith('/') ? address : address + "/";
}
