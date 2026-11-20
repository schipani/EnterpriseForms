using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Domain.Features.Journal;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5a.2 (amendment A-3, SM-7): the full sweep enumerates the reserved <c>meta/</c> prefix. The sentinel and canonical stop
/// markers are recognized and this organization's markers validated; malformed markers and unexpected objects are reported, never ignored.
/// </summary>
public sealed class StopMarkerVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Audit = JournalStreamNames.Audit;

    private static async Task<(JournalVerificationWorld World, Guid Org)> BaselinedAsync()
    {
        var world = new JournalVerificationWorld();
        var org = world.AddOrganization();
        world.Append(org, Audit, 3);
        await world.DeliverAsync(org, Audit);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await world.Baseline().RunAsync(JournalVerificationWorld.BaselineRequest(org, Audit), Ct)).Outcome);
        return (world, org);
    }

    private static string Key(Guid org, long revision) => $"meta/dplus-stops/{org:D}/{Audit}/{revision:D12}.json";

    [Fact]
    public async Task AValidMarkerIsRecognizedAndProducesNoFinding()
    {
        var (world, org) = await BaselinedAsync();
        var instance = (await world.Archive.ReadIdentityAsync(Ct)).Identity!.ArchiveInstanceId;
        var marker = new DPlusStreamStop(DPlusStreamStop.SchemaV1, org, Audit, 4, JournalFindingCodes.PredecessorMissing, null, "identity", Guid.CreateVersion7(), instance,
            JournalVerificationWorld.Start, "test");
        await world.Archive.CreateStopMarkerAsync(org, Audit, 4, DPlusStreamStops.Serialize(marker), Ct);
        var run = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.DoesNotContain(run.Findings, f => f.FindingCode is JournalFindingCodes.DPlusStopMarkerInvalid or JournalFindingCodes.ArchiveUnexpectedObject);
    }

    [Fact]
    public async Task AnInvalidMarkerAndAnUnexpectedMetaObjectAreReportedByTheFullSweep()
    {
        var (world, org) = await BaselinedAsync();
        world.Archive.PutMetaForTesting(Key(org, 4), "{\"not\":\"a marker\"}"u8.ToArray());
        world.Archive.PutMetaForTesting("meta/something-else.json", "{}"u8.ToArray());
        world.Archive.PutMetaForTesting($"meta/dplus-stops/{org:D}/{Audit}/x.json", "{}"u8.ToArray());
        var run = await world.Verifier().SweepAsync(org, Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.DPlusStopMarkerInvalid && f.FirstRevision == 4);
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveUnexpectedObject && f.EvidenceReferences.Contains("archive:meta/something-else.json"));
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveUnexpectedObject && f.EvidenceReferences.Any(e => e.EndsWith("/x.json", StringComparison.Ordinal)));
    }
}
