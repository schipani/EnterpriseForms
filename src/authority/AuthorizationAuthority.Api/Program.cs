using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Api.Features.Authorization;
using AuthorizationAuthority.Api.Features.Catalogue;
using AuthorizationAuthority.Api.Features.Content;
using AuthorizationAuthority.Api.Features.FieldSearch;
using AuthorizationAuthority.Api.Features.Health;
using AuthorizationAuthority.Api.Features.OperatorPlane;
using AuthorizationAuthority.Api.Features.SecurityCommands;
using AuthorizationAuthority.Api.Features.Sessions;
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
        // Phase C5 (D10): field-scoped contributor writes; off unless the deployment sets exactly "true"; refused in Production.
        builder.Services.AddSingleton(AuthorizationAuthority.Application.Features.Content.ShareContributorWriteSwitch.Create(
            builder.Configuration[AuthorizationAuthority.Application.Features.Content.ShareContributorWriteSwitch.ConfigurationKey],
            builder.Environment.IsProduction(),
            builder.Environment.IsEnvironment("Testing")));
        builder.Services.AddInternalAuthentication(builder.Configuration);
        // Phase C7 M2 (OD-7): AC-6 approvals are unavailable unless the development-only synthetic step-up path is configured;
        // Create refuses that path outside Development/Testing or without the synthetic TestIdentityProvider (startup failure).
        builder.Services.AddSingleton(AuthorizationAuthority.Application.Features.SecurityCommands.FieldSearchApprovalGate.Create(
            builder.Configuration[AuthorizationAuthority.Application.Features.SecurityCommands.FieldSearchApprovalGate.ConfigurationKey],
            builder.Environment.EnvironmentName,
            builder.Configuration["Identity:UserAssertion:ProviderKind"]));
        // DR-C7-3: dual-controlled operator approvals for first/recovery Security Administrators. Unavailable unless configured;
        // Create refuses the development-only synthetic approver keys outside Development/Testing (startup failure).
        builder.Services.AddSingleton(AuthorizationAuthority.Application.Features.OperatorPlane.OperatorApprovalVerifier.Create(
            builder.Configuration.GetSection(AuthorizationAuthority.Application.Features.OperatorPlane.OperatorApprovalOptions.SectionName).Get<AuthorizationAuthority.Application.Features.OperatorPlane.OperatorApprovalOptions>(),
            builder.Environment.EnvironmentName));
        // Phase C7 M1 (AC-5): provisional catalogue limits (section Catalogue, C-56 D4); a bad value fails at startup.
        var catalogueOptions = builder.Configuration.GetSection(AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptions.SectionName).Get<AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptions>() ?? new AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptions();
        AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptionsValidation.Validate(catalogueOptions);
        builder.Services.AddSingleton(catalogueOptions);
        builder.Services.AddSingleton<AuthorizationAuthority.Application.Features.Catalogue.SearchCatalogueService>();
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
        app.MapFormAccessEndpoints();
        app.MapSecurityCommandEndpoints();
        app.MapContentEndpoints();
        app.MapCatalogueEndpoints();
        app.MapOperatorEndpoints();
        app.MapSessionEndpoints();
        app.MapFieldSearchEndpoints();

        app.Run();
        return 0;
    }
}
