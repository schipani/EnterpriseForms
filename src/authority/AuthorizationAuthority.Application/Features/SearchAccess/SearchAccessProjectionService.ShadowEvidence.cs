using System.Text.Json;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>One UTC day of shadow sampling evidence (sampling plan §6). Counts only.</summary>
public sealed record ShadowEvidenceDay(DateOnly Day, int Samples, int Matches, int Mismatches, int NotComparable, int Cancelled, int Failed, int CanarySamples, int CanaryMismatches,
    IReadOnlyDictionary<string, int> NotComparableReasons, IReadOnlyDictionary<string, int> PredicateShapes);

/// <summary>An event that restarts the clean period (sampling plan §3).</summary>
public sealed record ShadowRestartEvent(DateTimeOffset At, string ReasonCode);

/// <summary>
/// The IX-5c online-sampling evidence for a period (sampling plan §5, §6), from the stored samples and the authorization journal. It reports;
/// it never decides that a gate passed: <see cref="FourteenDayCriterionMet"/> is computed from the plan's proposed criteria, which still await
/// review (TQ-3), and only for this environment and workload.
/// </summary>
public sealed record ShadowEvidenceReport(DateTimeOffset From, DateTimeOffset To, int Organizations, int Samples, int Matches, int NonCanaryMismatches, int CanaryControlsDetected,
    double NotComparableFraction, IReadOnlyList<ShadowEvidenceDay> Days, IReadOnlyList<ShadowRestartEvent> RestartEvents, DateTimeOffset? CleanPeriodStartedOn, double CleanPeriodDays,
    double LongestSamplingGapHours, IReadOnlyDictionary<string, int> SecurityCommandKinds, bool FourteenDayCriterionMet, string Statement);

public sealed partial class SearchAccessProjectionService
{
    private static readonly TimeSpan SamplingOutage = TimeSpan.FromHours(6);

    /// <summary>
    /// The sampling evidence report over [<paramref name="from"/>, <paramref name="to"/>) for every organization (each read in its own RLS
    /// context). Restart events: a Mismatch outside a canary organization; a change of derivation protocol or schema digest between consecutive
    /// samples; a gap of more than 6 hours between consecutive samples; a day with canary samples but no detected canary control. Security
    /// command kinds are counted from the authorization journal over the revisions the samples span (scenario coverage, §4.1).
    /// </summary>
    public async Task<ShadowEvidenceReport> ShadowEvidenceReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var samples = new List<SearchAccessShadowSample>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var organizations = 0;
        foreach (var organizationId in await store.GetAllOrganizationIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
            await scope.SearchAccess.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var mine = await scope.SearchAccess.GetShadowSamplesAsync(organizationId, from, to, cancellationToken).ConfigureAwait(false);
            if (mine.Count == 0)
            {
                continue;
            }

            organizations++;
            samples.AddRange(mine);
            var first = mine.Min(s => s.SampleAuthorizationRevision);
            var last = mine.Max(s => s.SampleAuthorizationRevision);
            for (var revision = Math.Max(1, first); revision <= last; revision++)
            {
                var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, revision, cancellationToken).ConfigureAwait(false);
                var kind = CommandKind(entry);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            }
        }

        samples = samples.OrderBy(s => s.SampleComparedOn).ToList();
        var restarts = new List<ShadowRestartEvent>();
        SearchAccessShadowSample? previous = null;
        var longestGap = TimeSpan.Zero;
        foreach (var sample in samples)
        {
            if (sample.SampleClassificationName == SearchAccessShadowClassifications.Mismatch && !sample.IsCanaryOrganization)
            {
                restarts.Add(new ShadowRestartEvent(sample.SampleComparedOn, "NON_CANARY_MISMATCH"));
            }

            if (previous is not null)
            {
                if (previous.DerivationProtocolVersion != sample.DerivationProtocolVersion || !previous.DerivationSchemaDigest.AsSpan().SequenceEqual(sample.DerivationSchemaDigest))
                {
                    restarts.Add(new ShadowRestartEvent(sample.SampleComparedOn, "DERIVATION_CHANGED"));
                }

                var gap = sample.SampleComparedOn - previous.SampleComparedOn;
                longestGap = gap > longestGap ? gap : longestGap;
                if (gap > SamplingOutage)
                {
                    restarts.Add(new ShadowRestartEvent(sample.SampleComparedOn, "SAMPLING_OUTAGE"));
                }
            }

            previous = sample;
        }

        var days = samples.GroupBy(s => DateOnly.FromDateTime(s.SampleComparedOn.UtcDateTime)).OrderBy(g => g.Key).Select(g => new ShadowEvidenceDay(g.Key, g.Count(),
            g.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.Match), g.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.Mismatch),
            g.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.NotComparable), g.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.Cancelled),
            g.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.Failed), g.Count(s => s.IsCanaryOrganization),
            g.Count(s => s.IsCanaryOrganization && s.SampleClassificationName == SearchAccessShadowClassifications.Mismatch),
            g.Where(s => s.SampleClassificationName == SearchAccessShadowClassifications.NotComparable).GroupBy(s => s.SampleReasonCode ?? "-").ToDictionary(r => r.Key, r => r.Count()),
            g.GroupBy(s => s.PredicateShapeText).ToDictionary(r => r.Key, r => r.Count()))).ToList();
        foreach (var day in days.Where(d => d.CanarySamples > 0 && d.CanaryMismatches == 0))
        {
            restarts.Add(new ShadowRestartEvent(new DateTimeOffset(day.Day.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero), "POSITIVE_CONTROL_MISSED"));
        }

        restarts = restarts.OrderBy(r => r.At).ToList();
        var nonCanary = samples.Where(s => !s.IsCanaryOrganization).ToList();
        var cleanStart = samples.Count == 0 ? (DateTimeOffset?)null : restarts.Count == 0 ? samples[0].SampleComparedOn : restarts[^1].At;
        var end = samples.Count == 0 ? from : samples[^1].SampleComparedOn;
        var cleanDays = cleanStart is { } start && end > start ? Math.Round((end - start).TotalDays, 3) : 0;
        var notComparable = nonCanary.Count == 0 ? 0 : Math.Round(nonCanary.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.NotComparable) / (double)nonCanary.Count, 4);
        var canaryDays = days.Count(d => d.CanarySamples > 0);
        var criterion = cleanDays >= 14 && notComparable < 0.05 && canaryDays > 0 && days.Where(d => d.CanarySamples > 0).All(d => d.CanaryMismatches > 0);
        var statement = "Evidence from this environment's stored shadow samples only. The 14-day criterion is the sampling plan's proposal (TQ-3, not yet reviewed); "
                        + "it is reported, not approved, and it does not cover the scenario minimums of §4.1, which are listed as counts for review. "
                        + (criterion ? "The proposed numeric criterion is met for this period." : "The proposed numeric criterion is not met for this period.");
        return new ShadowEvidenceReport(from, to, organizations, samples.Count, samples.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.Match),
            nonCanary.Count(s => s.SampleClassificationName == SearchAccessShadowClassifications.Mismatch),
            samples.Count(s => s.IsCanaryOrganization && s.SampleClassificationName == SearchAccessShadowClassifications.Mismatch), notComparable, days, restarts, cleanStart, cleanDays,
            Math.Round(longestGap.TotalHours, 3), kinds, criterion, statement);
    }

    /// <summary>The retention purge of shadow evidence (every organization, its own RLS context).</summary>
    public async Task<int> PurgeShadowEvidenceAsync(CancellationToken cancellationToken)
    {
        var before = clock.GetUtcNow().AddDays(-options.ShadowEvidenceRetentionDays);
        var purged = 0;
        foreach (var organizationId in await store.GetAllOrganizationIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            await scope.SearchAccess.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            purged += await scope.SearchAccess.PurgeShadowSamplesAsync(organizationId, before, cancellationToken).ConfigureAwait(false);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return purged;
    }

    private static string CommandKind(SecurityJournalEntry? entry)
    {
        if (entry is null)
        {
            return "(missing)";
        }

        try
        {
            using var document = JsonDocument.Parse(entry.JournalEventBodyJson);
            var root = document.RootElement;
            var body = root.TryGetProperty("body", out var b) ? b : root;
            return body.TryGetProperty("command_kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? "(unnamed)" : entry.JournalEventKind;
        }
        catch (JsonException)
        {
            return "(unparsable)";
        }
    }
}
