using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>Settings of section <c>JournalVerification</c> (SEC-BL-01 WP-3/WP-4). Development/Testing only.</summary>
public sealed class JournalVerificationSettings
{
    public const string SectionName = "JournalVerification";

    /// <summary>Starts the scheduled verifier loop. Default off.</summary>
    public bool Enabled { get; set; }

    public int TickSeconds { get; set; } = 30;

    public int IncrementalIntervalMinutes { get; set; } = 5;

    public int FullIntervalMinutes { get; set; } = 24 * 60;

    public int DeliveryLagMinutes { get; set; } = 15;

    public int VerificationStalenessMinutes { get; set; } = 15;

    public string VerifierIdentityLabel { get; set; } = "journal-verifier-authority-hosted-dev";
}

/// <summary>
/// SEC-BL-01 WP-3/WP-4 (C-155, contract §8.5): composes the baseline service and the scheduled verifier in the Authority host for
/// <b>Development/Testing only</b> (OD-DV-5, Q-8). Nothing is registered unless the checkpoint store is configured; outside Development and
/// Testing any configuration is refused at startup (production needs the separate workload and identities of WP-8). The verifier does
/// not wait for SpiceDB (it uses neither the projection nor the gate).
/// </summary>
public static class JournalVerificationComposition
{
    /// <summary>
    /// SEC-BL-01 WP-5a.1: re-checks the D+ finding-store requirement against the host's final configuration when the host starts, before
    /// the work loop (registered next) can deliver anything. <see cref="AddJournalVerification"/> applies the same rule at registration.
    /// </summary>
    public static void AddDPlusStartupValidation(this IServiceCollection services) => services.AddHostedService<DPlusStartupValidation>();
    public static bool AddJournalVerification(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var settings = configuration.GetSection(JournalVerificationSettings.SectionName).Get<JournalVerificationSettings>() ?? new JournalVerificationSettings();
        var provider = configuration.GetValue<string>($"{VerificationCheckpointStoreOptions.SectionName}:Provider") ?? "Unconfigured";
        var configured = !string.Equals(provider, "Unconfigured", StringComparison.Ordinal);
        RequireFindingStoreForDPlus(configuration, environment, provider, configured);

        // G14-R2 (A-4 §24): the recovery approver key set is validated on every start (the synthetic mode is refused outside
        // Development/Testing, a key shared with OperatorApprovals is refused), whether or not the store is composed.
        var recoveryApprovals = DPlusRecoveryApprovalVerifier.Create(
            configuration.GetSection(JournalRecoveryOptions.SectionName).Get<JournalRecoveryOptions>(), environment.EnvironmentName,
            configuration.GetSection("OperatorApprovals:Approvers").GetChildren().Select(c => c["PublicKeyBase64"]).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!));
        if (!configured && !settings.Enabled)
        {
            return false;
        }

        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                $"Journal verification (SEC-BL-01 WP-3/WP-4) is a Development/Testing composition and must not be configured in the {environment.EnvironmentName} environment; production requires the separate verifier workload and identities of WP-8. Refusing to start.");
        }

        if (!configured)
        {
            throw new InvalidOperationException($"{JournalVerificationSettings.SectionName}:Enabled requires {VerificationCheckpointStoreOptions.SectionName}:Provider. Refusing to start.");
        }

        var connection = configuration.GetConnectionString("AuthorityDbJournalVerification") ?? configuration.GetConnectionString("AuthorityDb")
            ?? throw new InvalidOperationException("ConnectionStrings:AuthorityDbJournalVerification or ConnectionStrings:AuthorityDb is required for journal verification.");
        services.Configure<VerificationCheckpointStoreOptions>(configuration.GetSection(VerificationCheckpointStoreOptions.SectionName));
        switch (provider)
        {
            case "Blob":
                // WP-5a.1: a per-stack generated key may be supplied as a file (never a public default credential); read at startup.
                if (configuration.GetValue<string>($"{VerificationCheckpointStoreOptions.SectionName}:AccountKeyFile") is { Length: > 0 } keyFile)
                {
                    var key = ReadKeyFile(keyFile);
                    services.PostConfigure<VerificationCheckpointStoreOptions>(o => o.AccountKey = key);
                }

                services.AddSingleton<IVerificationCheckpointStore, BlobVerificationCheckpointStore>();
                services.AddSingleton<IJournalVerificationFindingStore, BlobJournalVerificationFindingStore>();
                services.AddSingleton<IJournalAcceptedExceptionStore, BlobJournalAcceptedExceptionStore>();
                services.AddSingleton<IArchiveProvisioningStore, BlobArchiveProvisioningStore>();
                services.AddSingleton<IJournalVerificationRecordStore, BlobJournalVerificationRecordStore>();
                break;
            case "InMemory":
                services.AddSingleton<IVerificationCheckpointStore, InMemoryVerificationCheckpointStore>();
                services.AddSingleton<IJournalVerificationFindingStore, InMemoryJournalVerificationFindingStore>();
                services.AddSingleton<IJournalAcceptedExceptionStore, InMemoryJournalAcceptedExceptionStore>();
                services.AddSingleton<IArchiveProvisioningStore, InMemoryArchiveProvisioningStore>();
                services.AddSingleton<IJournalVerificationRecordStore, InMemoryJournalVerificationRecordStore>();
                break;
            default:
                throw new InvalidOperationException($"{VerificationCheckpointStoreOptions.SectionName}:Provider '{provider}' is not supported (Blob or InMemory). Refusing to start.");
        }

        services.AddSingleton(new JournalVerificationReader(connection));
        services.AddSingleton<IJournalVerificationReader>(sp => sp.GetRequiredService<JournalVerificationReader>());
        services.AddSingleton<ISecurityEventArchiveInventory>(sp => sp.GetRequiredService<ISecurityEventArchive>() as ISecurityEventArchiveInventory
            ?? throw new InvalidOperationException("the configured security-event archive has no inventory"));
        services.AddSingleton(new JournalVerifierOptions
        {
            IncrementalInterval = TimeSpan.FromMinutes(Positive(settings.IncrementalIntervalMinutes, nameof(settings.IncrementalIntervalMinutes))),
            FullInterval = TimeSpan.FromMinutes(Positive(settings.FullIntervalMinutes, nameof(settings.FullIntervalMinutes))),
            DeliveryLagThreshold = TimeSpan.FromMinutes(Positive(settings.DeliveryLagMinutes, nameof(settings.DeliveryLagMinutes))),
            VerificationStaleness = TimeSpan.FromMinutes(Positive(settings.VerificationStalenessMinutes, nameof(settings.VerificationStalenessMinutes))),
            VerifierIdentityLabel = settings.VerifierIdentityLabel,
        });
        services.AddSingleton(sp => new JournalVerifier(sp.GetRequiredService<IJournalVerificationReader>(), sp.GetRequiredService<ISecurityEventArchiveInventory>(),
            sp.GetRequiredService<IVerificationCheckpointStore>(), sp.GetRequiredService<IJournalVerificationFindingStore>(), sp.GetRequiredService<IJournalAcceptedExceptionStore>(),
            sp.GetRequiredService<ISecurityAlertSink>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<JournalVerifierOptions>(), sp.GetRequiredService<IJournalVerificationRecordStore>()));
        services.AddSingleton(sp => new JournalVerificationCoordinator(sp.GetRequiredService<JournalVerifier>(), sp.GetRequiredService<IJournalVerificationReader>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new JournalBaselineService(sp.GetRequiredService<IJournalVerificationReader>(), sp.GetRequiredService<ISecurityEventArchiveInventory>(),
            sp.GetRequiredService<IVerificationCheckpointStore>(), sp.GetRequiredService<IJournalAcceptedExceptionStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ArchiveProvisioningService(sp.GetRequiredService<IJournalVerificationReader>(), sp.GetRequiredService<ISecurityEventArchiveInventory>(),
            sp.GetRequiredService<IArchiveProvisioningStore>(), sp.GetRequiredService<TimeProvider>()));

        // G14-R2 (A-4 §24): D+ durability (W2-S commitments, instance pin, recovery records) and the recovery tool. ArchiveProcessor resolves
        // DPlusDurability when it is registered; without it (production until WP-8) the audit stream is never evaluated (fail closed).
        services.AddSingleton(recoveryApprovals);
        services.AddSingleton(sp => new DPlusDurability(sp.GetRequiredService<IJournalVerificationRecordStore>(), sp.GetRequiredService<IArchiveProvisioningStore>(),
            sp.GetRequiredService<IVerificationCheckpointStore>(), sp.GetRequiredService<DPlusRecoveryApprovalVerifier>()));
        services.AddSingleton(sp => new DPlusStreamRecoveryService(sp.GetRequiredService<AuthorizationAuthority.Application.Common.Ports.IAuthorityStore>(), sp.GetRequiredService<ISecurityEventArchive>(),
            sp.GetRequiredService<IJournalVerificationFindingStore>(), sp.GetRequiredService<DPlusDurability>(), sp.GetRequiredService<TimeProvider>()));
        if (settings.Enabled)
        {
            services.AddSingleton(settings);
            services.AddHostedService<JournalVerificationLoop>();
        }

        return true;
    }

    /// <summary>
    /// WP-5a.1 (A-2 correction): D+ is always active on the audit stream, and a confirmed refusal must be recorded in a durable finding store
    /// before the row state changes. In Development and Testing a host that delivers to an archive therefore refuses to start without the
    /// journal-verification store: a Blob archive needs the Blob store (durable, same lifetime, RST-1); the Development-only in-memory
    /// archive may use either. Production hosting is unchanged (a store there is WP-8); without one, D+ refusals hold the stream with
    /// <c>DPLUS_FINDING_STORE_UNAVAILABLE</c> (fail closed).
    /// </summary>
    internal static void RequireFindingStoreForDPlus(IConfiguration configuration, IHostEnvironment environment, string storeProvider, bool storeConfigured)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            return;
        }

        var archiveProvider = configuration.GetValue<string>($"{ArchiveOptions.SectionName}:Provider") ?? "Unconfigured";
        var blobArchive = string.Equals(archiveProvider, "Blob", StringComparison.OrdinalIgnoreCase);
        var inMemoryArchive = string.Equals(archiveProvider, "InMemory", StringComparison.OrdinalIgnoreCase) && environment.IsDevelopment();
        if (!blobArchive && !inMemoryArchive)
        {
            return; // nothing is delivered (the archive is unavailable), so D+ never refuses
        }

        if (!storeConfigured)
        {
            throw new InvalidOperationException(
                $"D+ (SEC-BL-01 WP-5a.1) requires the journal-verification store for durable findings in {environment.EnvironmentName}: configure {VerificationCheckpointStoreOptions.SectionName}:Provider. Refusing to start.");
        }

        if (blobArchive && !string.Equals(storeProvider, "Blob", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A Blob security-event archive requires a Blob journal-verification store (durable, same lifetime as the archive); provider {storeProvider} is not durable. Refusing to start.");
        }
    }

    internal static string ReadKeyFile(string path)
    {
        try
        {
            var key = File.ReadAllText(path).Trim();
            return key.Length > 0 ? key : throw new InvalidOperationException($"{VerificationCheckpointStoreOptions.SectionName}:AccountKeyFile is empty. Refusing to start.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"{VerificationCheckpointStoreOptions.SectionName}:AccountKeyFile cannot be read. Refusing to start.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"{VerificationCheckpointStoreOptions.SectionName}:AccountKeyFile cannot be read. Refusing to start.", ex);
        }
    }

    private static int Positive(int value, string name) => value > 0 ? value : throw new InvalidOperationException($"{JournalVerificationSettings.SectionName}:{name} must be positive.");
}

/// <summary>
/// SEC-BL-01 WP-4: the alert-only scheduled verifier loop. Each tick runs the due sweeps (one at a time); failures are logged and the loop
/// continues. Logs carry identifiers, codes, revision ranges and counts only — never journal bodies, digests of bodies or credentials.
/// </summary>
public sealed partial class JournalVerificationLoop(JournalVerificationCoordinator coordinator, JournalVerificationSettings settings, TimeProvider clock, ILogger<JournalVerificationLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, settings.TickSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var run in await coordinator.RunDueAsync(stoppingToken).ConfigureAwait(false))
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        var security = run.Findings.Count(f => f.Severity == JournalFindingSeverity.Security);
                        var operational = run.Findings.Count - security;
                        var inconclusive = string.Join(",", run.InconclusiveReasons);
                        var kind = run.Kind.ToString();
                        var outcome = run.Outcome.ToString();
                        LogRun(logger, run.RunId, run.OrganizationId, run.StreamName, kind, outcome, security, operational, inconclusive, run.CheckpointAction,
                            run.Metrics.DatabaseRowsInspected, run.Metrics.ArchiveObjectsRead, run.Metrics.ElapsedMilliseconds);
                    }

                    foreach (var finding in run.Findings)
                    {
                        LogFinding(logger, run.RunId, finding.OrganizationId, finding.StreamName, finding.FindingCode, finding.Subtype ?? "-", finding.Severity.ToString(),
                            finding.FirstRevision, finding.LastRevision);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // the loop must survive any tick failure; it is alert-only and decides nothing
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTickFailed(logger, ex.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.TickSeconds)), clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 9400, Level = LogLevel.Information, Message = "Journal verifier started (Development/Testing, alert-only), tick {TickSeconds} s.")]
    private static partial void LogStarted(ILogger logger, int tickSeconds);

    [LoggerMessage(EventId = 9401, Level = LogLevel.Information,
        Message = "Journal verification run {RunId}: organization {OrganizationId} stream {StreamName} kind {Kind} outcome {Outcome}; security {SecurityFindings} operational {OperationalFindings}; inconclusive [{Inconclusive}]; checkpoint {CheckpointAction}; rows {Rows} archive reads {ArchiveReads} in {ElapsedMs} ms.")]
    private static partial void LogRun(ILogger logger, Guid runId, Guid organizationId, string streamName, string kind, string outcome, int securityFindings, int operationalFindings, string inconclusive,
        string checkpointAction, long rows, long archiveReads, long elapsedMs);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Warning, Message = "Journal verification finding (run {RunId}): organization {OrganizationId} stream {StreamName} {FindingCode}/{Subtype} severity {Severity} revisions {FirstRevision}..{LastRevision}.")]
    private static partial void LogFinding(ILogger logger, Guid runId, Guid organizationId, string streamName, string findingCode, string subtype, string severity, long? firstRevision, long? lastRevision);

    [LoggerMessage(EventId = 9403, Level = LogLevel.Error, Message = "Journal verification tick failed ({ExceptionType}); the loop continues and decides nothing.")]
    private static partial void LogTickFailed(ILogger logger, string exceptionType);
}

/// <summary>WP-5a.1: fails host start when a Development/Testing Authority would deliver without a durable D+ finding store.</summary>
public sealed class DPlusStartupValidation(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var provider = configuration.GetValue<string>($"{VerificationCheckpointStoreOptions.SectionName}:Provider") ?? "Unconfigured";
        JournalVerificationComposition.RequireFindingStoreForDPlus(configuration, environment, provider, !string.Equals(provider, "Unconfigured", StringComparison.Ordinal));
        if (string.Equals(provider, "Blob", StringComparison.Ordinal)
            && configuration.GetValue<string>($"{VerificationCheckpointStoreOptions.SectionName}:AccountKeyFile") is { Length: > 0 } keyFile)
        {
            _ = JournalVerificationComposition.ReadKeyFile(keyFile);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}