using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// C10 M1-WP6 (WP-0 Section 4 "Workload identity"): the Authority-hosted loop that converges expired shares by recording the internal
/// <c>ExpireShare</c> command, one share at a time, through <see cref="ShareExpiryConvergenceService"/>. Its only input is a share id
/// and revision read from AuthorityDb; it has no endpoint, credential or business-worker involvement, and access-time enforcement
/// does not depend on it. A pass skips organizations whose gate is closed (the service finds them ineligible) and retries on the next
/// pass; a backwards clock step stops the loop with an alert; a share still expired and unconverged after two passes raises an alert.
/// Registered in Development and Testing only (Section 9: no activation elsewhere in M1).
/// </summary>
public sealed partial class ShareExpiryLoop(IAuthorityStore store, ShareExpiryConvergenceService convergence, ISecurityAlertSink alerts, TimeProvider clock, ILogger<ShareExpiryLoop> logger) : BackgroundService
{
    public const string ClockAlertKind = "SHARE_EXPIRY_CLOCK_UNSAFE";
    public const string StuckAlertKind = "SHARE_EXPIRY_NOT_CONVERGED";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Guid, int> passesUnconverged = [];

    /// <summary>True after a backwards clock step: the loop records no further command until restarted.</summary>
    public bool Stopped { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested && !Stopped)
        {
            try
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPassFailed(logger, ex.GetType().Name); // enforcement is unaffected; the next pass retries
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass over every organization (internal for tests). Returns the number of shares converged.</summary>
    internal async Task<int> RunPassAsync(CancellationToken cancellationToken)
    {
        var converged = 0;
        var seen = new HashSet<Guid>();
        foreach (var organization in await store.GetAllOrganizationIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            List<(Guid Share, long Revision)> candidates;
            await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = clock.GetUtcNow();
                candidates = [.. (await scope.GetActiveShareMetadataWithExpiryAsync(organization, cancellationToken).ConfigureAwait(false))
                    .Where(m => m.ShareExpiresOn <= now).OrderBy(m => m.ShareExpiresOn).Select(m => (m.RecordShareId, m.ShareRevision))];
            }

            foreach (var (share, revision) in candidates)
            {
                seen.Add(share);
                var outcome = await convergence.ConvergeAsync(organization, share, revision, cancellationToken).ConfigureAwait(false);
                switch (outcome.Result)
                {
                    case ShareExpiryConvergenceService.Result.Converged:
                    case ShareExpiryConvergenceService.Result.Replayed:
                        converged++;
                        passesUnconverged.Remove(share);
                        break;
                    case ShareExpiryConvergenceService.Result.ClockUnsafe:
                        Stopped = true;
                        alerts.Raise(new SecurityAlert(ClockAlertKind, "High", organization, Guid.CreateVersion7(), null, "the Authority clock is earlier than the organization's latest journal entry; share expiry convergence stopped", Guid.CreateVersion7()));
                        LogClockUnsafe(logger, organization);
                        return converged;
                    default:
                        var passes = passesUnconverged[share] = passesUnconverged.GetValueOrDefault(share) + 1;
                        if (passes == 3)
                        {
                            alerts.Raise(new SecurityAlert(StuckAlertKind, "Medium", organization, Guid.CreateVersion7(), null, "an expired share has not converged for more than two passes (" + outcome.ReasonCode + "); access to it is still denied", Guid.CreateVersion7()));
                        }

                        break;
                }
            }
        }

        foreach (var gone in passesUnconverged.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            passesUnconverged.Remove(gone);
        }

        return converged;
    }

    [LoggerMessage(EventId = 9310, Level = LogLevel.Warning, Message = "Share expiry convergence pass failed ({Failure}); access-time enforcement is unaffected; retrying")]
    private static partial void LogPassFailed(ILogger logger, string failure);

    [LoggerMessage(EventId = 9311, Level = LogLevel.Critical, Message = "Share expiry convergence stopped: the clock is earlier than the latest journal entry of organization {OrganizationId}")]
    private static partial void LogClockUnsafe(ILogger logger, Guid organizationId);
}
