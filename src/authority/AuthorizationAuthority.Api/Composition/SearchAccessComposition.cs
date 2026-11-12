using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchAccess;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// IX-5a (C-147): the Tier 2 access projection. Off by default. Dark (maintained from the authorization journal and verified, never
/// read by a search path) is refused outside Development/Testing. No search-serving switch exists in IX-5a.
/// </summary>
public static class SearchAccessComposition
{
    public static IServiceCollection AddSearchAccessProjection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var options = configuration.GetSection(SearchAccessOptions.SectionName).Get<SearchAccessOptions>() ?? new SearchAccessOptions();
        if (options.Mode == SearchAccessMode.Dark && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException("SearchIndex:Access:Mode=Dark is permitted only in Development or Testing in IX-5a.");
        }

        if (options.MaintenanceIntervalMilliseconds < 50 || options.EventsPerPass < 1 || options.VerificationSampleSize < 0 || options.MaximumParentDepth is < 1 or > 64 || options.ReconciliationIntervalSeconds is < 1 or > 3600
            || options.ShadowQueueCapacity is < 1 or > 1024 || options.ShadowMaximumMilliseconds is < 100 or > 30_000 || options.ShadowEvidenceRetentionDays is < 1 or > 400
            || options.VerificationIntervalSeconds is < 1 or > 3600 || options.FullVerificationIntervalSeconds is < 10 or > 86_400 || options.FullVerificationDeadlineSeconds < options.FullVerificationIntervalSeconds
            || options.FullVerificationDeadlineSeconds > 86_400 || options.SpiceDbSweepBatchSize is < 1 or > 10_000 || options.SpiceDbSweepIntervalSeconds is < 10 or > 604_800
            || options.SpiceDbSweepDeadlineSeconds < options.SpiceDbSweepIntervalSeconds || options.SpiceDbSweepDeadlineSeconds > 604_800 || options.FenceMaximumRecords is < 1 or > 100_000
            || options.MaximumLookupSubjects is < 1 or > 1_000_000)
        {
            throw new InvalidOperationException("SearchIndex:Access options are out of range.");
        }

        // IX-39 coupling guard (startup): the derivation mirrors one exact SpiceDB schema; any other schema disables Tier 2 (rows are then
        // never maintained, verified or compared). The reason is set by the host only, never taken from configuration.
        options.DisabledReasonCode = null;
        if (options.Mode == SearchAccessMode.Dark && !SearchAccessSchemaCoupling.Coupled(AuthorizationAuthority.Infrastructure.SpiceDb.SpiceDbProjectionClient.EmbeddedSchema()))
        {
            options.Mode = SearchAccessMode.Off;
            options.ShadowComparisonEnabled = false;
            options.DisabledReasonCode = "SCHEMA_COUPLING_BROKEN";
        }

        // C-150 IX-5c shadow preparation: off by default; only with the maintained dark projection, only in Development/Testing.
        if (options.ShadowComparisonEnabled && (options.Mode != SearchAccessMode.Dark || (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))))
        {
            throw new InvalidOperationException("SearchIndex:Access:ShadowComparisonEnabled requires Mode=Dark and is permitted only in Development or Testing (C-150).");
        }

        services.AddSingleton(options);
        services.AddSingleton<SearchAccessMaintenanceSwitch>();
        services.AddSingleton<SearchAccessProjectionService>();
        if (options.Mode == SearchAccessMode.Dark)
        {
            services.AddHostedService<SearchAccessMaintenanceLoop>();
            services.AddHostedService<SearchAccessVerificationLoop>(); // IX-5 M1 fence and M2 sweep
        }

        if (options.ShadowComparisonEnabled)
        {
            // Tier 1 sees only the sink interface; the comparer and its worker never serve and never alter a Tier 1 answer.
            services.AddSingleton<ISearchAccessShadowObserver, SearchAccessShadowLogging>();
            services.AddSingleton<SearchAccessShadowComparer>();
            services.AddSingleton<AuthorizationAuthority.Application.Features.SearchIndex.IIndexedSearchShadowSink>(sp => sp.GetRequiredService<SearchAccessShadowComparer>());
            services.AddHostedService<SearchAccessShadowWorker>();
        }

        return services;
    }
}

/// <summary>Testing only: pauses the maintenance loop so a test can observe the not-ready state between a command and its consumption.</summary>
public sealed class SearchAccessMaintenanceSwitch : IDisposable
{
    private readonly SemaphoreSlim step = new(1, 1);
    private volatile bool paused;

    public bool Paused => paused;

    public void Dispose() => step.Dispose();

    /// <summary>Pauses and waits until no organization step of the loop is in flight, so a paused loop has truly stopped.</summary>
    public async Task SetPausedAsync(bool value, CancellationToken cancellationToken)
    {
        paused = value;
        if (value)
        {
            await step.WaitAsync(cancellationToken).ConfigureAwait(false);
            step.Release();
        }
    }

    /// <summary>The loop runs each organization's step inside this, and skips it when paused.</summary>
    public async Task RunUnlessPausedAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await step.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!paused)
            {
                await action().ConfigureAwait(false);
            }
        }
        finally
        {
            step.Release();
        }
    }
}

/// <summary>
/// Consumes the authorization and content journals and the coverage set for every organization, asynchronously. An organization
/// without a checkpoint is built once from the canonical edges; a quarantined organization is never rebuilt automatically.
/// C-149 (R-55): change-aware. Each pass reads every organization's change markers in one statement; an organization whose markers
/// equal those it was last settled at is skipped without a transaction, an organization whose markers moved is caught up, and on
/// the first pass and every reconciliation interval every organization is probed (one read-only transaction, then the steps only if
/// the probe finds work). The steps, their ordering, the checkpoint compare-and-set, the row-lock fencing and quarantine are
/// unchanged; readiness is computed from the live state on every read and does not depend on this loop.
/// </summary>
public sealed partial class SearchAccessMaintenanceLoop(IAuthorityStore store, SearchAccessProjectionService projection, SearchAccessOptions options, SearchAccessMaintenanceSwitch maintenanceSwitch, TimeProvider clock, ILogger<SearchAccessMaintenanceLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        var schedule = new SearchAccessMaintenanceSchedule(options, clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!maintenanceSwitch.Paused)
                {
                    var (items, reconciliation, skipped) = schedule.Plan(await store.GetOrganizationChangeMarkersAsync(stoppingToken).ConfigureAwait(false));
                    foreach (var item in items)
                    {
                        // Checked per organization: a pause takes effect before the next organization, not after the whole pass. A
                        // skipped (paused) organization keeps its previous memo, so a moved marker still makes it due next pass.
                        await maintenanceSwitch.RunUnlessPausedAsync(() => MaintainAsync(schedule, item, stoppingToken), stoppingToken).ConfigureAwait(false);
                    }

                    if (reconciliation)
                    {
                        LogReconciliation(logger, items.Count, skipped, schedule.SettledCount);
                    }
                }

                await Task.Delay(options.MaintenanceIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailure(logger, ex.GetType().Name);
                await Task.Delay(options.MaintenanceIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MaintainAsync(SearchAccessMaintenanceSchedule schedule, SearchAccessMaintenanceItem item, CancellationToken stoppingToken)
    {
        var organization = item.Marker.OrganizationId;
        try
        {
            var result = await projection.MaintainAsync(schedule, item, stoppingToken).ConfigureAwait(false);
            if (!result.SettledByProbe && (result.Outcome == SearchAccessStepOutcome.Quarantined || result.Advanced > 0))
            {
                LogPass(logger, organization, result.Outcome, result.Advanced);
            }
        }
        catch (AuthorityException ex)
        {
            LogOrganizationFailure(logger, organization, ex.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOrganizationFailure(logger, organization, ex.GetType().Name); // one organization's failure never stops the others
        }
    }

    [LoggerMessage(EventId = 5301, Level = LogLevel.Information, Message = "Search access projection organization={Organization} outcome={Outcome} advanced={Advanced}")]
    private static partial void LogPass(ILogger logger, Guid organization, SearchAccessStepOutcome outcome, int advanced);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning, Message = "Search access projection organization={Organization} refused={ErrorCode}")]
    private static partial void LogOrganizationFailure(ILogger logger, Guid organization, string errorCode);

    [LoggerMessage(EventId = 5303, Level = LogLevel.Error, Message = "Search access projection pass failed: {ExceptionType}")]
    private static partial void LogFailure(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5304, Level = LogLevel.Debug, Message = "Search access reconciliation pass: processed={Processed} skipped={Skipped} settled={Settled}")]
    private static partial void LogReconciliation(ILogger logger, int processed, int skipped, int settled);
}

/// <summary>C-150 IX-5c: runs the shadow comparisons off the search path (one at a time).</summary>
public sealed class SearchAccessShadowWorker(SearchAccessShadowComparer comparer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        try
        {
            await comparer.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host stopping
        }
    }
}

/// <summary>C-150 IX-5c: one log line per comparison; a mismatch is a Warning. Counts, codes and the correlation id only.</summary>
public sealed partial class SearchAccessShadowLogging(ILogger<SearchAccessShadowLogging> logger) : ISearchAccessShadowObserver
{
    public void Compared(SearchAccessShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Classification == SearchAccessShadowClassifications.Mismatch)
        {
            LogMismatch(logger, result.CorrelationId, result.Tier1Count, result.Tier2Count ?? -1, result.OnlyInTier1Count, result.OnlyInTier2Count, result.ConditionCount);
        }
        else
        {
            LogCompared(logger, result.Classification, result.ReasonCode ?? "-", result.CorrelationId, result.Tier1Count, result.Tier2Count ?? -1, result.ExcludedByMetadataCount, result.ElapsedMilliseconds);
        }
    }

    [LoggerMessage(EventId = 5320, Level = LogLevel.Warning, Message = "IX5C-SHADOW Mismatch correlation={CorrelationId} tier1={Tier1Count} tier2={Tier2Count} only_tier1={OnlyTier1} only_tier2={OnlyTier2} conditions={Conditions}")]
    private static partial void LogMismatch(ILogger logger, Guid correlationId, int tier1Count, int tier2Count, int onlyTier1, int onlyTier2, int conditions);

    [LoggerMessage(EventId = 5321, Level = LogLevel.Information, Message = "IX5C-SHADOW {Classification} reason={Reason} correlation={CorrelationId} tier1={Tier1Count} tier2={Tier2Count} excluded_by_metadata={Excluded} elapsed_ms={Elapsed}")]
    private static partial void LogCompared(ILogger logger, string classification, string reason, Guid correlationId, int tier1Count, int tier2Count, int excluded, double elapsed);
}

/// <summary>
/// IX-5 M1/M2 verification loop. Every <see cref="SearchAccessOptions.VerificationIntervalSeconds"/> it reads the organizations' change
/// markers in one statement and runs <see cref="SearchAccessProjectionService.VerifyStepAsync"/> only for an organization whose
/// authorization revision moved since it was last verified, whose M2 work is due, or that it has not seen since start; an idle verified
/// organization opens no transaction (the R-55 lesson). It also purges shadow evidence beyond the retention period. It never serves.
/// </summary>
public sealed partial class SearchAccessVerificationLoop(IAuthorityStore store, SearchAccessProjectionService projection, SearchAccessOptions options, SearchAccessMaintenanceSwitch maintenanceSwitch, TimeProvider clock, ILogger<SearchAccessVerificationLoop> logger) : BackgroundService
{
    private readonly Dictionary<Guid, (long Revision, DateTimeOffset Due, int Misses)> memo = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        var lastPurge = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!maintenanceSwitch.Paused)
                {
                    var now = clock.GetUtcNow();
                    foreach (var marker in await store.GetOrganizationChangeMarkersAsync(stoppingToken).ConfigureAwait(false))
                    {
                        if (memo.TryGetValue(marker.OrganizationId, out var seen) && seen.Revision == marker.AuthorizationRevision && now < seen.Due)
                        {
                            continue;
                        }

                        await maintenanceSwitch.RunUnlessPausedAsync(() => VerifyAsync(marker, now, stoppingToken), stoppingToken).ConfigureAwait(false);
                    }

                    if (now - lastPurge > TimeSpan.FromHours(1))
                    {
                        lastPurge = now;
                        await projection.PurgeShadowEvidenceAsync(stoppingToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(options.VerificationIntervalSeconds), clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogVerificationFailure(logger, Guid.Empty, ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(options.VerificationIntervalSeconds), clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task VerifyAsync(Application.Common.Ports.OrganizationChangeMarker marker, DateTimeOffset now, CancellationToken stoppingToken)
    {
        try
        {
            var step = await projection.VerifyStepAsync(marker.OrganizationId, Guid.NewGuid(), stoppingToken).ConfigureAwait(false);
            if (step.OutcomeCode is SearchAccessVerificationOutcomes.Diverged or SearchAccessVerificationOutcomes.FenceLimit or SearchAccessVerificationOutcomes.LookupLimit)
            {
                LogVerificationFinding(logger, marker.OrganizationId, step.OutcomeCode);
            }

            // Verified and nothing due before the next full check or pass: skip until the revision moves or the work falls due. A pass in
            // progress, an initialization or any non-final outcome is looked at again next interval.
            var settled = step.OutcomeCode == SearchAccessVerificationOutcomes.Verified && step.Verified && (step.SweptRecords == 0 || step.SweepCompleted);
            // A pass in progress continues next interval. An organization that is not at the head, not built, quarantined, or whose SpiceDB
            // projection lags is backed off exponentially (capped at the full-check interval) until its marker moves: an idle organization
            // that cannot be verified must not cost a transaction every interval (the R-55 lesson).
            var inProgress = step.OutcomeCode is SearchAccessVerificationOutcomes.Verified or SearchAccessVerificationOutcomes.Initialized;
            var misses = settled || inProgress ? 0 : (memo.TryGetValue(marker.OrganizationId, out var previous) && previous.Revision == marker.AuthorizationRevision ? previous.Misses + 1 : 1);
            var due = settled ? now + TimeSpan.FromSeconds(Math.Min(options.FullVerificationIntervalSeconds, options.SpiceDbSweepIntervalSeconds))
                : inProgress ? now
                : now + TimeSpan.FromSeconds(Math.Min(options.FullVerificationIntervalSeconds, options.VerificationIntervalSeconds * Math.Pow(2, Math.Min(misses, 10))));
            memo[marker.OrganizationId] = (marker.AuthorizationRevision, due, misses);
        }
        catch (AuthorityException ex)
        {
            memo.Remove(marker.OrganizationId);
            LogVerificationFailure(logger, marker.OrganizationId, ex.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            memo.Remove(marker.OrganizationId);
            LogVerificationFailure(logger, marker.OrganizationId, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 5322, Level = LogLevel.Warning, Message = "Search access verification organization={Organization} outcome={Outcome}")]
    private static partial void LogVerificationFinding(ILogger logger, Guid organization, string outcome);

    [LoggerMessage(EventId = 5323, Level = LogLevel.Warning, Message = "Search access verification organization={Organization} failed={Reason}")]
    private static partial void LogVerificationFailure(ILogger logger, Guid organization, string reason);
}
