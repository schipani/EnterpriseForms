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

        if (options.MaintenanceIntervalMilliseconds < 50 || options.EventsPerPass < 1 || options.VerificationSampleSize < 0 || options.MaximumParentDepth is < 1 or > 64 || options.ReconciliationIntervalSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException("SearchIndex:Access options are out of range.");
        }

        services.AddSingleton(options);
        services.AddSingleton<SearchAccessMaintenanceSwitch>();
        services.AddSingleton<SearchAccessProjectionService>();
        if (options.Mode == SearchAccessMode.Dark)
        {
            services.AddHostedService<SearchAccessMaintenanceLoop>();
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
