using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Infrastructure.Signing;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// IX-1 (C-126): the Authority-owned search index. Off by default (Disabled). Dark mode (derivation, seals, sweeps; no query
/// path) is refused outside Development/Testing in IX-1. The seal provider is chosen from deployment configuration only:
/// LocalDevelopment (Development/Testing), VaultTransit (not Staging/Production) or AzureManagedHsm (not implemented in IX-1,
/// fails closed). Pinned verification keys come from configuration only.
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
            default:
                services.AddSingleton<ISearchSealSigner>(_ => new UnavailableSearchSealSigner(seal.Provider == SearchSealProviderKind.AzureManagedHsm ? "AzureManagedHsmSealProviderNotImplemented" : "SearchSealProviderUnconfigured", seal));
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
                sink = (record, t) => SearchIndexTimingLog.Stages(logger, record, t.ReadsMilliseconds, t.DeriveMilliseconds, t.SignMilliseconds, t.WritesMilliseconds);
            }

            return new SearchIndexWriter(options, options.Mode == SearchIndexMode.Dark ? sp.GetRequiredService<SearchSealSigningGate>() : null, sink);
        });
        services.AddSingleton<ContentRevisionCoordinator>();
        services.AddSingleton<SearchIndexSweepService>();
        if (options.Mode == SearchIndexMode.Dark)
        {
            services.AddHostedService<SearchIndexSweepLoop>();
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Search index sweep pass failed ({ExceptionType}); deadlines keep running.")]
    private static partial void LogFailure(ILogger logger, string exceptionType);
}

internal static partial class SearchIndexTimingLog
{
    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "IX1-DERIVE-TIMING record={FormRecordId} reads_ms={Reads} derive_ms={Derive} sign_ms={Sign} writes_ms={Writes}")]
    public static partial void Stages(ILogger logger, Guid formRecordId, double reads, double derive, double sign, double writes);
}
