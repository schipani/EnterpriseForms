using AuthorizationAuthority.Api.Composition;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-137 (R-39): when a seal pass takes longer than the sweep interval, the scheduler runs the next pass after the previous
/// one has finished (back to back), never two passes for the same organization at the same time within one Authority instance.
/// The scheduler is one sequential loop; this test runs the real loop against the in-memory store with a slow seal selection.
/// </summary>
public sealed class SweepSchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APassLongerThanTheIntervalIsFollowedBackToBackAndNeverOverlapsTheNextPass()
    {
        using var h = new SearchIndexTests.Harness();
        await h.ApproveStandardFieldsAsync();
        for (var i = 0; i < 2; i++)
        {
            await h.WriteAsync(h.Record());
        }

        h.Options.SealCheckTransactionSize = 1; // two entries: three selections (two non-empty, one empty) per pass
        h.Options.CheapSweepIntervalSeconds = 1;
        h.Options.DeadlineEvaluationIntervalSeconds = 3_600;
        var inSelection = 0;
        var maximum = 0;
        var gate = new object();
        h.Index.BeforeSealCheckSelection = _ =>
        {
            lock (gate)
            {
                inSelection++;
                maximum = Math.Max(maximum, inSelection);
            }

            Thread.Sleep(450); // each pass takes about 1.35 s, longer than the 1 s interval
            lock (gate)
            {
                inSelection--;
            }
        };

        var loop = new SearchIndexSweepLoop(h.Sweep, h.Options, TimeProvider.System, NullLogger<SearchIndexSweepLoop>.Instance);
        await loop.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(4.5), Ct);
        await loop.StopAsync(CancellationToken.None);

        Assert.Equal(1, maximum); // never two selections (hence never two passes) in progress at once
        Assert.True(h.Index.SealCheckSelections >= 6, $"expected at least two complete passes, saw {h.Index.SealCheckSelections} selections");
    }
}
