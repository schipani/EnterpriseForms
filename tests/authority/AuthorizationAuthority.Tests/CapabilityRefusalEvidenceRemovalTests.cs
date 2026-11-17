using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// FU-M2f3-01, closure test M2F3-43 (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §10.5): "Evidence-removal: delete or alter a delivered
/// evidence row ⇒ chain / archive verification detects it". Executed against the product's existing verification only: the unchanged
/// <see cref="ArchiveProcessor"/> (pre-delivery digest check, archive conflict), the archive port (create-only, readable), and the
/// recorder's own lookup verification (LK-3/LK-4 over the newest eight evidence entries). A privileged AuthorityDb writer is emulated by
/// editing the rows of the transactional double directly, after the entries were delivered (<c>Anchored</c>).
/// <para>
/// Two kinds of test are in this class and they are reported separately (FU-M2f3-01 record):
/// <list type="bullet">
/// <item><b>Detected</b> (trait <c>M2F3-43 = Detected</c>): the alterations the existing product verification does detect.</item>
/// <item><b>Expected gap</b> (trait <c>M2F3-43 = ExpectedGap</c>): characterization of the cases the requirement names that NO product
/// mechanism detects (deletion; a consistently re-digested forgery that is not re-sent; an alteration outside the newest eight). These
/// tests assert the observed non-detection so that a future verifier makes them fail and forces this record to be revisited. They are
/// demonstrations of a gap, <b>not</b> passes of M2F3-43, and are outside every pass total.</item>
/// </list>
/// </para>
/// </summary>
public sealed class CapabilityRefusalEvidenceRemovalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Delivered(CapabilityRefusalEvidenceWorld World, Guid Org, CapabilityRefusalEvidenceRecorder Recorder, InMemorySecurityEventArchive Archive,
        IReadOnlyDictionary<string, (long Revision, byte[] Body, byte[] Digest)> Original);

    /// <summary>Records <paramref name="entryPoints"/> for one organization and delivers every entry to the archive (Anchored).</summary>
    private static async Task<Delivered> DeliveredAsync(params string[] entryPoints)
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        var recorder = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock);
        foreach (var entry in entryPoints)
        {
            world.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await recorder.RecordAsync(new CapabilityRefusalContext(org, 1), entry, CancellationToken.None));
        }

        var archive = new InMemorySecurityEventArchive();
        Assert.Equal(entryPoints.Length, await new ArchiveProcessor(world.Store, archive, world.Clock).ProcessOrganizationAsync(org, Ct));
        Assert.All(world.Journal, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        var original = world.Journal.ToDictionary(EntryPointOf, e => (e.JournalStreamRevision, Encoding.UTF8.GetBytes(e.JournalEventBodyJson), e.JournalEventDigest));
        return new Delivered(world, org, recorder, archive, original);
    }

    private static string EntryPointOf(SecurityJournalEntry entry) =>
        CapabilityRefusalEvidenceLookup.Verify(new CapabilityRefusalEvidenceRow(entry.SecurityJournalEntryId, entry.JournalStreamRevision, entry.JournalEventBodyJson, entry.JournalEventDigest), entry.OrganizationId)!.Key.EntryPoint;

    private static SecurityJournalEntry Row(Delivered d, string entryPoint) => d.World.Journal.Single(e => e.JournalStreamRevision == d.Original[entryPoint].Revision);

    /// <summary>A privileged in-place alteration of a delivered row: prepare → commit (same workload); the stored digest is kept or re-computed.</summary>
    private static void Alter(SecurityJournalEntry row, bool recomputeDigest)
    {
        row.JournalEventBodyJson = row.JournalEventBodyJson.Replace("\"entry_point\":\"prepare\"", "\"entry_point\":\"commit\"", StringComparison.Ordinal);
        Assert.Contains("\"entry_point\":\"commit\"", row.JournalEventBodyJson, StringComparison.Ordinal);
        if (recomputeDigest)
        {
            row.JournalEventDigest = CanonicalJson.Digest(Encoding.UTF8.GetBytes(row.JournalEventBodyJson));
        }
    }

    private static Task<int> ArchivePassAsync(Delivered d) => new ArchiveProcessor(d.World.Store, d.Archive, d.World.Clock).ProcessOrganizationAsync(d.Org, Ct);

    private static async Task AssertArchiveKeepsTheDeliveredBytesAsync(Delivered d)
    {
        foreach (var (_, (revision, body, digest)) in d.Original)
        {
            var archived = await d.Archive.ReadAsync(d.Org, JournalStreamNames.Audit, revision, Ct);
            Assert.NotNull(archived);
            Assert.Equal(body, archived);
            Assert.Equal(digest, CanonicalJson.Digest(archived));
        }
    }

    // ---- Detected by existing product verification ----

    [Fact]
    [Trait("M2F3-43", "Detected")]
    public async Task AnAlteredDeliveredRowThatIsSentAgainIsRefusedByThePreDeliveryDigestCheckAndQuarantines()
    {
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare);
        var row = Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare);
        Alter(row, recomputeDigest: false);
        row.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // the writer also makes it eligible for delivery again

        Assert.Equal(0, await ArchivePassAsync(d));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, ArchiveProcessor.JournalBodyDigestMismatch), (row.ArchiveDeliveryState, row.LastArchiveErrorCode));
        Assert.Equal((true, ArchiveProcessor.JournalBodyDigestMismatch), (d.World.Organizations[d.Org].IntegrityQuarantineEnabled, d.World.Organizations[d.Org].IntegrityQuarantineReasonCode));
        await AssertArchiveKeepsTheDeliveredBytesAsync(d);
    }

    [Fact]
    [Trait("M2F3-43", "Detected")]
    public async Task AConsistentlyRedigestedDeliveredRowThatIsSentAgainCollidesWithTheArchiveAndQuarantines()
    {
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare);
        var row = Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare);
        Alter(row, recomputeDigest: true);
        row.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;

        Assert.Equal(0, await ArchivePassAsync(d));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "ARCHIVE_BODY_MISMATCH"), (row.ArchiveDeliveryState, row.LastArchiveErrorCode));
        Assert.Equal((true, "ARCHIVE_BODY_MISMATCH"), (d.World.Organizations[d.Org].IntegrityQuarantineEnabled, d.World.Organizations[d.Org].IntegrityQuarantineReasonCode));
        await AssertArchiveKeepsTheDeliveredBytesAsync(d);
    }

    [Fact]
    [Trait("M2F3-43", "Detected")]
    public async Task AnAlteredDeliveredRowAmongTheNewestEightMakesTheNextLookupInconclusiveAndAppendsNothing()
    {
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Verify);
        Alter(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare), recomputeDigest: false);
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        var count = d.World.Journal.Count;

        Assert.Equal(CapabilityRefusalEvidenceOutcome.Inconclusive, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.ProtectionCensus, CancellationToken.None));
        Assert.Equal(count, d.World.Journal.Count);

        // Only the recorder's own lookup notices: the archive processor never revisits a delivered row (no quarantine, nothing flagged).
        Assert.Equal(0, await ArchivePassAsync(d));
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled);
        await AssertArchiveKeepsTheDeliveredBytesAsync(d);
    }

    [Fact]
    [Trait("M2F3-43", "Detected")]
    public async Task TheArchiveKeepsEveryDeliveredBodyAfterTheRowIsDeletedOrAltered()
    {
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Commit, CapabilityRefusalEvidence.EntryPoints.Verify);
        Alter(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare), recomputeDigest: true);
        d.World.Journal.Remove(Row(d, CapabilityRefusalEvidence.EntryPoints.Commit));

        // Create-only archive: an attempt to overwrite a delivered key with other bytes is refused, the original stays readable.
        var revision = d.Original[CapabilityRefusalEvidence.EntryPoints.Prepare].Revision;
        var forged = Encoding.UTF8.GetBytes(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare).JournalEventBodyJson);
        await Assert.ThrowsAsync<AuthorizationAuthority.Application.Common.Ports.ArchiveConflictException>(() =>
            d.Archive.AppendAsync(d.Org, JournalStreamNames.Audit, revision, forged, CanonicalJson.Digest(forged), Ct));
        await AssertArchiveKeepsTheDeliveredBytesAsync(d);
    }

    // ---- Expected gap: what M2F3-43 names but no product mechanism detects (demonstrations, outside pass totals) ----

    [Fact]
    [Trait("M2F3-43", "ExpectedGap")]
    public async Task ExpectedGapDeletingADeliveredRowIsNotDetectedAndTheKeyIsRecordedAgain()
    {
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Verify);
        d.World.Journal.Remove(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare));

        // No product check notices: the archive processor reads only pending rows; the organization stays unquarantined.
        Assert.Equal(0, await ArchivePassAsync(d));
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled);

        // The recorder's lookup sees a consistent journal without the key and records it again in the same bucket.
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None));
        Assert.Equal(1, await ArchivePassAsync(d));
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled);

        // The archive now holds two entries for the same key and bucket at different revisions; nothing compares them.
        var renewed = d.World.Journal.Single(e => EntryPointOf(e) == CapabilityRefusalEvidence.EntryPoints.Prepare);
        Assert.NotEqual(d.Original[CapabilityRefusalEvidence.EntryPoints.Prepare].Revision, renewed.JournalStreamRevision);
        Assert.NotNull(await d.Archive.ReadAsync(d.Org, JournalStreamNames.Audit, d.Original[CapabilityRefusalEvidence.EntryPoints.Prepare].Revision, Ct));
        Assert.NotNull(await d.Archive.ReadAsync(d.Org, JournalStreamNames.Audit, renewed.JournalStreamRevision, Ct));
    }

    [Fact]
    [Trait("M2F3-43", "ExpectedGap")]
    public async Task ExpectedGapAConsistentlyRedigestedDeliveredRowThatIsNotSentAgainIsNotDetected()
    {
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Verify);
        Alter(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare), recomputeDigest: true);

        Assert.Equal(0, await ArchivePassAsync(d));
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled);

        // The lookup verifies each row's own digest and closed body, not the chain link to its successor: the forgery is accepted,
        // so the forged key counts as recorded and the original key is recorded again.
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.AlreadyRecorded, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None));
        Assert.Equal(1, await ArchivePassAsync(d));
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled);
    }

    [Fact]
    [Trait("M2F3-43", "ExpectedGap")]
    public async Task ExpectedGapAnAlteredDeliveredRowOutsideTheNewestEightIsNotDetected()
    {
        // Prepare first, then eight later evidence entries of the same organization (seven keys in the next hour plus one), so the
        // altered row is not among the eight the lookup reads.
        var d = await DeliveredAsync(CapabilityRefusalEvidence.EntryPoints.Prepare);
        d.World.Clock.Advance(TimeSpan.FromHours(1));
        foreach (var entry in CapabilityRefusalEvidence.EntryPoints.All)
        {
            Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await d.Recorder.RecordAsync(new CapabilityRefusalContext(d.Org, 1), entry, CancellationToken.None));
        }

        d.World.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await d.Recorder.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None));
        Assert.Equal(8, await ArchivePassAsync(d));

        Alter(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare), recomputeDigest: false);
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None));
        Assert.Equal(1, await ArchivePassAsync(d));
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled);
    }
}
