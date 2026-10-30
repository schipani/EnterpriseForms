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
