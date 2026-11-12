using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Signing;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// IX-1 (C-126): the Authority-owned search index. Off by default (Disabled). Dark mode (derivation, seals, sweeps; no query
/// path) is refused outside Development/Testing in IX-1. The seal provider is chosen from deployment configuration only:
/// LocalDevelopment (Development/Testing), VaultTransit (not Staging/Production) or AzureManagedHsm (IX-7: the Managed HSM REST
/// provider with the Authority's managed identity; unit-tested against a fake endpoint only, never against a real Managed HSM).
/// Pinned verification keys come from configuration only; the startup pin check compares the HSM public key with the pin.
/// Dark mode itself stays refused outside Development/Testing, so the Managed HSM provider is not activated anywhere by this change.
/// </summary>
public static class SearchIndexComposition
{
    public static IServiceCollection AddSearchIndex(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var options = configuration.GetSection(SearchIndexOptions.SectionName).Get<SearchIndexOptions>() ?? new SearchIndexOptions();
        var seal = configuration.GetSection(SearchSealOptions.SectionName).Get<SearchSealOptions>() ?? new SearchSealOptions();
        var developmentOrTesting = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        Validate(options, seal, developmentOrTesting, environment);

        services.AddSingleton(options);
        services.AddSingleton(seal);
        services.AddSingleton<SearchSealVerifier>();
        switch (seal.Provider)
        {
            case SearchSealProviderKind.LocalDevelopment when developmentOrTesting:
                services.AddSingleton<ISearchSealSigner>(_ => new LocalSearchSealSigner(seal));
                break;
            case SearchSealProviderKind.VaultTransit when !environment.IsProduction() && !environment.IsStaging():
                services.AddSingleton<ISearchSealSigner, VaultTransitSearchSealSigner>();
                break;
            case SearchSealProviderKind.AzureManagedHsm:
                var hsm = configuration.GetSection(ManagedHsmSealOptions.SectionName).Get<ManagedHsmSealOptions>() ?? new ManagedHsmSealOptions();
                hsm.Validate(seal);
                services.AddSingleton(hsm);
                services.AddHttpClient(ManagedHsmSearchSealSigner.HttpClientName);
                // Managed identity only (system-assigned, or the configured user-assigned client id); no secret is ever configured.
                services.AddSingleton<Azure.Core.TokenCredential>(_ => string.IsNullOrWhiteSpace(hsm.ManagedIdentityClientId)
                    ? new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.SystemAssigned)
                    : new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(hsm.ManagedIdentityClientId)));
                services.AddSingleton<ISearchSealSigner>(sp => new ManagedHsmSearchSealSigner(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<Azure.Core.TokenCredential>(), hsm, seal));
                break;
            default:
                services.AddSingleton<ISearchSealSigner>(_ => new UnavailableSearchSealSigner("SearchSealProviderUnconfigured", seal));
                break;
        }

        services.AddSingleton<SearchSealSigningGate>();
        // IX-1 measurement only: the per-stage timing line (identifiers and durations) exists in the Testing environment alone.
        var emitStageTimings = environment.IsEnvironment("Testing");
        services.AddSingleton(sp =>
        {
            Action<Guid, SearchDerivationStageTimings>? sink = null;
            if (emitStageTimings)
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AuthorizationAuthority.Ix1CommitTiming");
                sink = (record, t) =>
                {
                    SearchIndexTimingLog.Stages(logger, record, t.ReadsMilliseconds, t.DeriveMilliseconds, t.SignMilliseconds, t.WritesMilliseconds);
                    if (t.ReadSplit is { } r)
                    {
                        SearchIndexTimingLog.ReadSplit(logger, record, r.RegistryMilliseconds, r.FirstScanMilliseconds, r.SecondScanMilliseconds, r.QueryMilliseconds, r.CommandExecuteMilliseconds, r.Commands, r.TrackedEntities, r.ApprovalRows, r.TrackedMergePath);
                    }
                };
            }

            return new SearchIndexWriter(options, options.Mode == SearchIndexMode.Dark ? sp.GetRequiredService<SearchSealSigningGate>() : null, sink);
        });
        services.AddSingleton<ContentRevisionCoordinator>();
        services.AddSingleton<SearchIndexSweepService>();
        services.AddSingleton<SearchIndexBuilderService>(); // IX-2: builder plane (every call refused unless Dark)
        services.AddSingleton<SearchProjectionRebuildService>();
        if (options.Mode == SearchIndexMode.Dark)
        {
            services.AddHostedService<SearchIndexSweepLoop>();
        }

        // IX-3: the Tier 1 query path. Off by default; Development/Testing only; needs the index (Dark) and its own reader credential.
        var query = configuration.GetSection(IndexedSearchOptions.SectionName).Get<IndexedSearchOptions>() ?? new IndexedSearchOptions();
        IndexedSearchOptions.Validate(query);
        var readerConnection = configuration.GetConnectionString("AuthoritySearchReader");
        if (query.RecordListEnabled && !query.QueryEnabled)
        {
            throw new InvalidOperationException("SearchIndex:Query:RecordListEnabled (IX-6) requires SearchIndex:Query:QueryEnabled.");
        }

        if (query.QueryEnabled)
        {
            if (!developmentOrTesting)
            {
                throw new InvalidOperationException("SearchIndex:Query:QueryEnabled is permitted only in Development or Testing in IX-3.");
            }

            if (options.Mode != SearchIndexMode.Dark)
            {
                throw new InvalidOperationException("SearchIndex:Query:QueryEnabled requires SearchIndex:Mode=Dark (the index must be maintained).");
            }

            if (string.IsNullOrWhiteSpace(readerConnection) || string.Equals(readerConnection, configuration.GetConnectionString("AuthorityDb"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SearchIndex:Query:QueryEnabled requires ConnectionStrings:AuthoritySearchReader, distinct from the runtime connection (design §5.1).");
            }
        }

        services.AddSingleton(query);
        services.AddSingleton<ISearchQueryReader>(_ => new SearchQueryReader(query.QueryEnabled ? readerConnection : null, query));
        if (environment.IsEnvironment("Testing"))
        {
            // C-141 (RQ-2): the share-only step probe exists in the Testing environment alone; elsewhere the service receives null.
            services.AddSingleton<AuthorizationAuthority.Api.Features.SearchIndex.IndexedSearchTestingProbe>();
            services.AddSingleton<IIndexedSearchProbe>(sp => sp.GetRequiredService<AuthorizationAuthority.Api.Features.SearchIndex.IndexedSearchTestingProbe>());
        }

        services.AddSingleton<IndexedSearchService>();
        if (query.QueryEnabled)
        {
            services.AddHostedService<SearchResultSnapshotPurgeLoop>();
        }

        return services;
    }

    private static void Validate(SearchIndexOptions options, SearchSealOptions seal, bool developmentOrTesting, IHostEnvironment environment)
    {
        if (!Enum.IsDefined(options.Mode))
        {
            throw new InvalidOperationException("SearchIndex:Mode is not a known mode.");
        }

        if (options.Mode == SearchIndexMode.Dark && !developmentOrTesting)
        {
            throw new InvalidOperationException("SearchIndex:Mode=Dark is permitted only in Development or Testing in IX-1.");
        }

        if (options.SealCheckTransactionSize < 1 || options.SealCheckTransactionSize > options.SealCheckBatchSize)
        {
            throw new InvalidOperationException("SearchIndex:SealCheckTransactionSize must be 1..SealCheckBatchSize (C-136).");
        }

        if (options.CheapSweepIntervalSeconds < 1 || options.DeadlineEvaluationIntervalSeconds < 1 || options.SealCheckBatchSize < 1
            || options.CheapPassDeadlineSeconds < 1 || options.SealVerificationDeadlineSeconds < 1 || options.DeepVerificationDeadlineSeconds < 1 || options.FutureTimestampToleranceSeconds < 0)
        {
            throw new InvalidOperationException("SearchIndex intervals, deadlines and batch sizes must be positive.");
        }

        if (options.CheapSweepIntervalSeconds >= options.CheapPassDeadlineSeconds)
        {
            throw new InvalidOperationException("SearchIndex:CheapSweepIntervalSeconds must be shorter than the S-A deadline (C-124 scheduling target).");
        }

        if (!developmentOrTesting && (options.CheapPassDeadlineSeconds != 3600 || options.SealVerificationDeadlineSeconds != 86_400 || options.DeepVerificationDeadlineSeconds != 2_592_000))
        {
            throw new InvalidOperationException("The C-124 enforcement deadlines (1 h, 24 h, 30 days) may be changed only in Development or Testing.");
        }

        if (seal.Provider == SearchSealProviderKind.LocalDevelopment && !developmentOrTesting)
        {
            throw new InvalidOperationException("The LocalDevelopment seal provider is permitted only in Development or Testing.");
        }

        if (seal.Provider == SearchSealProviderKind.VaultTransit && (environment.IsProduction() || environment.IsStaging()))
        {
            throw new InvalidOperationException("The VaultTransit seal provider is a local Docker provider; it is forbidden in Staging and Production.");
        }

        if (!string.IsNullOrEmpty(seal.LocalPrivateKeyPkcs8Base64) && !developmentOrTesting)
        {
            throw new InvalidOperationException("A local seal private key may be configured only in Development or Testing.");
        }

        if (options.Mode == SearchIndexMode.Dark)
        {
            if (seal.Provider == SearchSealProviderKind.Unconfigured)
            {
                throw new InvalidOperationException("SearchIndex:Mode=Dark requires SearchIndex:Seal:Provider.");
            }

            if (!seal.PinnedKeys.Any(k => k.KeyName == seal.SigningKeyName && k.KeyVersion == seal.SigningKeyVersion))
            {
                throw new InvalidOperationException("SearchIndex:Seal:PinnedKeys must pin the signing key version (C-126 pinned public key).");
            }
        }
    }
}

/// <summary>
/// IX-1: the search-index sweep loop (Dark mode only). Every <see cref="SearchIndexOptions.CheapSweepIntervalSeconds"/>: S-A
/// then S-B for each indexed organization; every <see cref="SearchIndexOptions.DeadlineEvaluationIntervalSeconds"/>: the
/// deadline evaluation. No exception escapes (an exception would stop the host); a failure leaves the deadlines running.
/// </summary>
public sealed partial class SearchIndexSweepLoop(SearchIndexSweepService sweep, SearchIndexOptions options, TimeProvider clock, ILogger<SearchIndexSweepLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        var nextSweep = clock.GetUtcNow();
        var nextEvaluation = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                var organizations = now >= nextSweep || now >= nextEvaluation ? await sweep.IndexedOrganizationsAsync(stoppingToken).ConfigureAwait(false) : [];
                if (now >= nextSweep)
                {
                    foreach (var organization in organizations)
                    {
                        var cheap = await sweep.RunCheapPassAsync(organization, stoppingToken).ConfigureAwait(false);
                        var seals = await sweep.RunSealPassAsync(organization, stoppingToken).ConfigureAwait(false);
                        LogPass(logger, organization, cheap.Outcome, cheap.FindingCode, seals.Outcome, seals.UnitsPassed, seals.UnitsInconclusive, seals.FindingCode);
                        LogLockHold(logger, organization, cheap.LongestTransactionMilliseconds, seals.Transactions, seals.LongestTransactionMilliseconds, seals.TotalTransactionMilliseconds);
                    }

                    nextSweep = now.AddSeconds(options.CheapSweepIntervalSeconds);
                }

                if (now >= nextEvaluation)
                {
                    foreach (var organization in organizations)
                    {
                        await sweep.EvaluateDeadlinesAsync(organization, stoppingToken).ConfigureAwait(false);
                    }

                    nextEvaluation = now.AddSeconds(options.DeadlineEvaluationIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailure(logger, ex.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Search index sweep for {OrganizationId}: S-A {CheapOutcome} ({CheapFinding}); S-B {SealOutcome}, passed {Passed}, inconclusive {Inconclusive} ({SealFinding}).")]
    private static partial void LogPass(ILogger logger, Guid organizationId, SweepUnitOutcome cheapOutcome, string? cheapFinding, SweepUnitOutcome sealOutcome, int passed, int inconclusive, string? sealFinding);

    /// <summary>C-136: organization-lock hold of the sweep (milliseconds of each locked transaction; counts only).</summary>
    [LoggerMessage(EventId = 9305, Level = LogLevel.Information, Message = "IX4-SWEEP-LOCK organization={OrganizationId} s_a_ms={CheapMs} s_b_transactions={Transactions} s_b_longest_ms={LongestMs} s_b_total_ms={TotalMs}")]
    private static partial void LogLockHold(ILogger logger, Guid organizationId, double cheapMs, int transactions, double longestMs, double totalMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search index sweep pass failed ({ExceptionType}); deadlines keep running.")]
    private static partial void LogFailure(ILogger logger, string exceptionType);
}

internal static partial class SearchIndexTimingLog
{
    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "IX1-DERIVE-TIMING record={FormRecordId} reads_ms={Reads} derive_ms={Derive} sign_ms={Sign} writes_ms={Writes}")]
    public static partial void Stages(ILogger logger, Guid formRecordId, double reads, double derive, double sign, double writes);

    /// <summary>C-150 (R-02): the reads-stage split (durations and counts only).</summary>
    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "IX1-READ-SPLIT record={FormRecordId} registry_ms={Registry} first_scan_ms={FirstScan} second_scan_ms={SecondScan} query_ms={Query} command_ms={Command} commands={Commands} tracked_entities={Tracked} approval_rows={Rows} tracked_merge={Merge}")]
    public static partial void ReadSplit(ILogger logger, Guid formRecordId, double registry, double firstScan, double secondScan, double query, double command, int commands, int tracked, int rows, bool merge);
}

/// <summary>
/// IX-3 (§18.4): the result-snapshot purge job. Every <see cref="IndexedSearchOptions.PurgeIntervalSeconds"/> (default one minute):
/// expired and invalidated snapshots in bounded batches. Deletion in the invalidating transactions (triggers) is the first line;
/// this job removes what expiry, session timeouts and skipped (locked) rows leave behind. Counts only in the log.
/// </summary>
public sealed partial class SearchResultSnapshotPurgeLoop(IndexedSearchService service, IndexedSearchOptions options, TimeProvider clock, ILogger<SearchResultSnapshotPurgeLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await service.PurgeAsync(stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                {
                    LogPurged(logger, deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPurgeFailed(logger, ex.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PurgeIntervalSeconds), clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 9320, Level = LogLevel.Information, Message = "IX3-SNAPSHOT-PURGE deleted={Deleted}")]
    private static partial void LogPurged(ILogger logger, int deleted);

    [LoggerMessage(EventId = 9321, Level = LogLevel.Warning, Message = "Search result snapshot purge failed ({ExceptionType}); retried at the next interval.")]
    private static partial void LogPurgeFailed(ILogger logger, string exceptionType);
}
