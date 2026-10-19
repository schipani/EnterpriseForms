using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Api.Features.Authorization;
using AuthorizationAuthority.Api.Features.Content;
using AuthorizationAuthority.Api.Features.Health;
using AuthorizationAuthority.Api.Features.OperatorPlane;
using AuthorizationAuthority.Api.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure;

namespace AuthorizationAuthority.Api;

/// <summary>
/// Independently deployed Authorization Authority. Internal network only; never exposed through the browser API.
/// Host start fails closed unless an approved signing provider, distinct workload/operator/user-assertion credentials
/// and a permitted archive provider are configured for the environment.
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

        if (MigrateCommand.TryRun(args, out var migrateExitCode))
        {
            return migrateExitCode;
        }

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureHttpJsonOptions(options => AuthorityJson.Configure(options.SerializerOptions));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AuthorityExceptionHandler>();
        builder.Services.AddOpenApi(InternalApi.DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info ??= new Microsoft.OpenApi.OpenApiInfo();
                document.Info.Title = "Enterprise Forms Authorization Authority (INTERNAL)";
                document.Info.Version = "internal-v1";
                document.Info.Description = "Private contract. Reachable only over the authority network path with the workload credential and a delegated user assertion. Not part of the browser-facing API.";
                document.Servers = [];
                return Task.CompletedTask;
            });
        });

        builder.Services.AddAuthorityInfrastructure(builder.Configuration, builder.Environment);
        builder.Services.AddSigningProviderConfiguration(builder.Configuration, builder.Environment);
        builder.Services.AddInternalAuthentication(builder.Configuration);
        builder.Services.AddSingleton<SpiceDbSchemaInitializer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SpiceDbSchemaInitializer>());
        builder.Services.AddHostedService<AuthorityWorkLoop>();

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();
        app.MapAuthorizationEndpoints();
        app.MapSecurityCommandEndpoints();
        app.MapContentEndpoints();
        app.MapOperatorEndpoints();

        app.Run();
        return 0;
    }
}
