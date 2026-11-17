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

        // E3-M2f-3i: the capability-refusal evidence index, a separate privileged step (migration identity, CREATE INDEX
        // CONCURRENTLY, no EF migration, no grant re-application). It refuses --migrate and the floor command alongside it, so it
        // is dispatched first.
        if (CapabilityRefusalEvidenceIndexCommand.TryRun(args, out var evidenceIndexExitCode))
        {
            return evidenceIndexExitCode;
        }

        // E3-M2b-1F: Release B only, an explicit operator action; ordinary --migrate never applies the floor, and the two are
        // never combined in one invocation (the command refuses --migrate alongside it).
        if (ContentAcceptanceFloorCommand.TryRun(args, out var floorExitCode))
        {
            return floorExitCode;
        }

        if (MigrateCommand.TryRun(args, out var migrateExitCode))
        {
            return migrateExitCode;
        }

        // IX-7 ST-PERF-7: the seal-throughput harness runs on the fully composed host (same signer, policy and pin), then exits.
        var measureSeal = SealThroughputCommand.Requested(args, out var sealCount, out var sealConcurrency, out args);
        // E-5: Development/Testing operator command, run on the composed host (same store and signing provider), then exits.
        var activateKey = IntegrityKeyActivationCommand.Requested(args, out var activation, out args);
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
        // IX-1 (C-126): Authority-owned search index; Disabled unless configured, Dark (no query path) only in Development/Testing.
        builder.Services.AddSearchIndex(builder.Configuration, builder.Environment);
        builder.Services.AddSearchAccessProjection(builder.Configuration, builder.Environment); // IX-5a: dark Tier 2 access projection
        // C-137 (R-37): optional phase-duration diagnostics; off unless configured, Development/Testing only.
        builder.Services.AddOperationTiming(builder.Configuration, builder.Environment);
        // Phase C5 (D10): field-scoped contributor writes; off unless the deployment sets exactly "true"; refused in Production.
        builder.Services.AddSingleton(AuthorizationAuthority.Application.Features.Content.ShareContributorWriteSwitch.Create(
            builder.Configuration[AuthorizationAuthority.Application.Features.Content.ShareContributorWriteSwitch.ConfigurationKey],
            builder.Environment.IsProduction(),
            builder.Environment.IsEnvironment("Testing")));
        // E3-M2e (OD-M2e-07): Envelope v2 acceptance; off unless the deployment sets exactly "true"; refused outside Development/Testing,
        // without Release B RequiredFloor 3, or without the data-key ledger migration. E3-M2e-1 is dark: never enabled.
        builder.Services.AddEnvelopeV2Acceptance();
        // E3-M2f-2 (§9): the business envelope capability floor; deployment floor from Content:MinimumBusinessEnvelopeCapability (absent => 1,
        // exactly "1" or "2" otherwise; anything else refuses the start). The check itself runs at the seven content entry points.
        builder.Services.AddEnvelopeCapabilityFloor();

        // E3-M2f-3b: bounded capability-refusal evidence on the organization audit stream (recorder, endpoint filter on the seven
        // capability routes, in-memory retry). Never changes a refusal; adds no endpoint, permission or configuration key.
        builder.Services.AddCapabilityRefusalEvidence();
        AuthorizationAuthority.Api.Features.Health.TestStackIdentityEndpoints.RefuseOutsideTesting(builder.Configuration, builder.Environment);
        builder.Services.AddInternalAuthentication(builder.Configuration);
        // Phase C8 AC-9: read-only outcome lookup for the business reconciler (own credential, rate limit; bad limits fail at startup).
        builder.Services.AddReconcilerLookup(builder.Configuration);
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
        // C8 M2 Stages 1-2: Platform Operators. Unavailable unless configured; Create refuses the development-only synthetic mode
        // outside Development/Testing (startup failure). Production stays closed until PB-08, PIM and an independent review.
        builder.Services.AddSingleton(AuthorizationAuthority.Application.Features.PlatformOperators.PlatformOperatorPolicy.Create(
            builder.Configuration.GetSection(AuthorizationAuthority.Application.Features.PlatformOperators.PlatformOperatorOptions.SectionName).Get<AuthorizationAuthority.Application.Features.PlatformOperators.PlatformOperatorOptions>(),
            builder.Environment.EnvironmentName));
        // C8 onboarding (C-104): approved owner issuers and the legacy fixture bootstrap switch; both refused outside Development/Testing.
        builder.Services.AddSingleton(AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationOnboardingPolicy.Create(
            builder.Configuration.GetSection(AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationOnboardingOptions.SectionName).Get<AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationOnboardingOptions>(),
            builder.Environment.EnvironmentName));
        // Phase C7 M1 (AC-5): provisional catalogue limits (section Catalogue, C-56 D4); a bad value fails at startup.
        var catalogueOptions = builder.Configuration.GetSection(AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptions.SectionName).Get<AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptions>() ?? new AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptions();
        AuthorizationAuthority.Application.Features.Catalogue.CatalogueOptionsValidation.Validate(catalogueOptions);
        builder.Services.AddSingleton(catalogueOptions);
        builder.Services.AddSingleton<AuthorizationAuthority.Application.Features.Catalogue.SearchCatalogueService>();
        builder.Services.AddSingleton<AuthorizationAuthority.Application.Features.Catalogue.ProtectionReconciliationService>();
        builder.Services.AddSingleton<SpiceDbSchemaInitializer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SpiceDbSchemaInitializer>());
        builder.Services.AddHostedService<AuthorityWorkLoop>();
        // C-150 R56-B: every instance listens for committed authorization changes (any instance's commit wakes all loops).
        var authorityWork = builder.Configuration.GetSection(AuthorizationAuthority.Application.Common.AuthorityWorkOptions.SectionName).Get<AuthorizationAuthority.Application.Common.AuthorityWorkOptions>() ?? new AuthorizationAuthority.Application.Common.AuthorityWorkOptions();
        if (authorityWork.CommitWakeupEnabled && builder.Configuration.GetConnectionString("AuthorityDb") is { Length: > 0 } workListenerConnection)
        {
            builder.Services.AddHostedService(sp => new AuthorityWorkNotificationListener(workListenerConnection, sp.GetRequiredService<AuthorizationAuthority.Application.Common.AuthorityWorkSignal>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AuthorityWorkNotificationListener>>()));
        }
        // C8 M4 S1 (S2): periodic projection-integrity check; it complements, never replaces, the decision-time protocol.
        builder.Services.AddSingleton(builder.Configuration.GetSection(ProjectionIntegrityOptions.SectionName).Get<ProjectionIntegrityOptions>() ?? new ProjectionIntegrityOptions());
        builder.Services.AddHostedService<ProjectionIntegrityMonitorLoop>();

        var app = builder.Build();
        if (measureSeal)
        {
            return SealThroughputCommand.Run(app.Services, app.Environment, sealCount, sealConcurrency);
        }

        if (activateKey)
        {
            return IntegrityKeyActivationCommand.Run(app.Services, app.Environment, activation);
        }


        app.UseMiddleware<ReconcilerLookupRefusalMiddleware>(); // outermost: observes the final status of AC-9 refusals
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        if (app.Environment.IsEnvironment("Testing"))
        {
            AuthorizationAuthority.Api.Features.Health.SpiceDbCallCountingEndpoints.UseSpiceDbCallAttribution(app); // C-149 P-2 (when enabled)
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();
        if (app.Environment.IsEnvironment("Testing"))
        {
            app.MapTestStackIdentityEndpoints();
            AuthorizationAuthority.Api.Features.SearchIndex.IndexedSearchEndpoints.MapIndexedSearchTestingEndpoints(app); // IX-3 plan-shape test
            AuthorizationAuthority.Api.Features.SearchIndex.SearchAccessTestingEndpoints.MapSearchAccessTestingEndpoints(app); // IX-5a
            AuthorizationAuthority.Api.Features.Health.SpiceDbCallCountingEndpoints.MapSpiceDbCallCountingEndpoints(app); // C-149 P-2
            AuthorizationAuthority.Api.Features.Health.AuthorityWorkTestingEndpoints.MapAuthorityWorkTestingEndpoints(app); // C-150 R56-B
        }

        app.MapAuthorizationEndpoints();
        app.MapFormAccessEndpoints();
        app.MapSecurityCommandEndpoints();
        app.MapSecurityCommandOutcomeEndpoints();
        app.MapContentEndpoints();
        app.MapCatalogueEndpoints();
        app.MapOperatorEndpoints();
        AuthorizationAuthority.Api.Features.PlatformOperators.PlatformEndpoints.MapPlatformEndpoints(app);
        AuthorizationAuthority.Api.Features.PlatformOperators.OrganizationOnboardingEndpoints.MapOrganizationOnboardingEndpoints(app);
        AuthorizationAuthority.Api.Features.PlatformOperators.OwnerRecoveryEndpoints.MapOwnerRecoveryEndpoints(app);
        app.MapSessionEndpoints();
        app.MapFieldSearchEndpoints();
        AuthorizationAuthority.Api.Features.SearchIndex.SearchIndexBuilderEndpoints.MapSearchIndexBuilderEndpoints(app);
        AuthorizationAuthority.Api.Features.SearchIndex.IndexedSearchEndpoints.MapIndexedSearchEndpoints(app); // IX-3
        AuthorizationAuthority.Api.Features.Membership.MembershipEndpoints.MapMembershipEndpoints(app);

        app.Run();
        return 0;
    }
}
