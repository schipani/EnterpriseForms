using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.Dashboard;
using EnterpriseForms.Api.Features.Documents;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Api.Features.FormDesigner;
using EnterpriseForms.Api.Features.FormRecords;
using EnterpriseForms.Api.Features.Identity;
using EnterpriseForms.Api.Features.Organizations;
using EnterpriseForms.Api.Features.Platform;
using EnterpriseForms.Api.Features.RelatedForms;
using EnterpriseForms.Api.Features.Search;
using EnterpriseForms.Api.Features.Sharing;
using EnterpriseForms.Api.Features.Teams;
using EnterpriseForms.Infrastructure;

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

        if (MigrateCommand.TryRun(args, out var migrateExitCode))
        {
            return migrateExitCode;
        }

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<BusinessProblemHandler>();
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
        builder.Services.AddBusinessInfrastructure(builder.Configuration);
        // Phase C6 AC-2 tests: Testing-only bypass of the business lifecycle lock on the contributor path; refuses to start elsewhere.
        builder.Services.AddSingleton(EnterpriseForms.Application.Features.FormRecords.Lifecycle.LifecycleTestingHooks.FromConfiguration(
            builder.Configuration.GetValue<bool>(EnterpriseForms.Application.Features.FormRecords.Lifecycle.LifecycleTestingHooks.ConfigurationKey), builder.Environment.EnvironmentName));
        builder.Services.AddBusinessAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddWriteFaultInjection(builder.Configuration, builder.Environment);
        builder.Services.AddBusinessDataProtection(builder.Configuration);
        // My Records cursors (C-29) use the host's Data Protection key ring, like the session cookie; only the API serves lists.
        builder.Services.AddSingleton<EnterpriseForms.Application.Common.IOpaqueCursorProtector, DataProtectionCursorProtector>();
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.ListRecords.ListRecordsHandler>();
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.History.RecordHistoryHandler>();
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.FormRecords.History.RecordRestoreHandler>();
        // Phase C7 D4: provisional field-search limits (configuration FieldSearch); a bad value fails at startup.
        var fieldSearchLimits = builder.Configuration.GetSection(EnterpriseForms.Application.Features.Search.FieldSearch.FieldSearchLimits.SectionName).Get<EnterpriseForms.Application.Features.Search.FieldSearch.FieldSearchLimits>() ?? new EnterpriseForms.Application.Features.Search.FieldSearch.FieldSearchLimits();
        fieldSearchLimits.Validate();
        fieldSearchLimits.ValidateEnvironment(builder.Environment.EnvironmentName);
        builder.Services.AddSingleton(fieldSearchLimits);
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.Search.FieldSearch.FieldSearchAdmission>();
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.Search.FieldSearch.FieldSearchHandler>();
        builder.Services.AddSingleton<EnterpriseForms.Application.Features.Search.Verification.TrustedScopeVerifier>();
        builder.Services.Configure<DocumentProcessingHostOptions>(builder.Configuration.GetSection(DocumentProcessingHostOptions.SectionName));
        builder.Services.AddHostedService<DocumentProviderStartupGuard>();
        builder.Services.AddHostedService<DocumentProcessingHostedService>();
        builder.Services.AddHostedService<FieldProtectionMigrationHostedService>();
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 5L * 1024 * 1024); // one 4 MiB chunk (+ framing); never whole files

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSecurityHeaders();
        app.UseAuthentication();
        app.UseSessionCsrfProtection();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapPlatformEndpoints();
        app.MapIdentityEndpoints();
        app.MapSessionEndpoints();
        app.MapFormDefinitionEndpoints();
        app.MapFormDesignerEndpoints();
        app.MapFormRecordEndpoints();
        app.MapRelatedFormsEndpoints();
        app.MapDocumentEndpoints();
        app.MapSharingEndpoints();
        app.MapTeamEndpoints();
        app.MapDashboardEndpoints();
        app.MapOrganizationEndpoints();
        app.MapSearchEndpoints();
        if (app.Environment.IsEnvironment("Testing"))
        {
            // Phase C7 M1: real-stack proof of the AC-5 verification path. Never mapped outside the isolated Testing stack.
            app.MapSearchTestingEndpoints();
        }

        app.Run();
        return 0;
    }
}
