using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
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
/// <item><b>Detected by the verifier</b> (SEC-BL-01 WP-7, traits <c>SEC-BL-01 = EG-1..EG-3</c>): the three cases FU-M2f3-01 recorded as
/// expected gaps (deletion; a consistently re-digested forgery that is not re-sent; an alteration outside the newest eight), inverted per
/// contract §13.5: attack steps unchanged, non-detection assertions replaced by the WP-4 verifier's finding, the recorder's residual
/// behaviour still asserted. The historical expected-gap outcomes stay recorded in <c>E3_M2F3_FU_VERIFICATION_RECORD.md</c> §2.3.</item>
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
        await world.ProvisionArchiveAsync(archive); // WP-5a: sentinel and the archived predecessor (D+ on the audit stream)
        Assert.Equal(entryPoints.Length, await new ArchiveProcessor(world.Store, archive, world.Clock, durability: world.DPlus.Durability).ProcessOrganizationAsync(org, Ct));
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

    private static Task<int> ArchivePassAsync(Delivered d) => new ArchiveProcessor(d.World.Store, d.Archive, d.World.Clock, durability: d.World.DPlus.Durability).ProcessOrganizationAsync(d.Org, Ct);

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

    // ---- SEC-BL-01 WP-7 (C-159): EG-1..EG-3, the expected-gap tests inverted (contract §13.5) ----
    //
    // Each attack is unchanged from the FU-M2f3-01 expected-gap demonstration (E3_M2F3_FU_VERIFICATION_RECORD.md §2.3, outcomes preserved there).
    // The verifier (WP-4) is added: a baseline before the attack, then the scheduled sweeps. The recorder's behaviour after the attack is kept
    // as asserted residual characterization (R-DR-2: the recorder acts on the forged journal until detection). The incremental sweep is
    // asserted Clean because every change lies below its trusted checkpoint (A-1 IV-5); the full sweep (VF-12/VF-13, every T_full) detects it.

    private sealed record Verification(JournalVerifier Verifier, JournalBaselineService Baseline);

    /// <summary>Delivers <paramref name="entryPoints"/> for an organization whose audit stream starts at genesis (so the verifier sees the whole stream).</summary>
    private static async Task<Delivered> DeliveredFromGenesisAsync(params string[] entryPoints)
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization(freshAuditStream: true);
        var recorder = new CapabilityRefusalEvidenceRecorder(world.Store, world.Clock);
        foreach (var entry in entryPoints)
        {
            world.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await recorder.RecordAsync(new CapabilityRefusalContext(org, 1), entry, CancellationToken.None));
        }

        var archive = new InMemorySecurityEventArchive();
        await world.ProvisionArchiveAsync(archive);
        Assert.Equal(entryPoints.Length, await new ArchiveProcessor(world.Store, archive, world.Clock, durability: world.DPlus.Durability).ProcessOrganizationAsync(org, Ct));
        Assert.All(world.Journal, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        var original = world.Journal.ToDictionary(EntryPointOf, e => (e.JournalStreamRevision, Encoding.UTF8.GetBytes(e.JournalEventBodyJson), e.JournalEventDigest));
        return new Delivered(world, org, recorder, archive, original);
    }

    /// <summary>The WP-4 verifier and baseline over this world's journal (read-only reader), archive and separately held checkpoint store.</summary>
    private static Verification VerificationOf(Delivered d)
    {
        var reader = new InMemoryJournalVerificationReader(() => d.World.Journal, () => d.World.Organizations.Values, () => 0);
        var checkpoints = new InMemoryVerificationCheckpointStore();
        var exceptions = new InMemoryJournalAcceptedExceptionStore();
        return new Verification(
            new JournalVerifier(reader, d.Archive, checkpoints, new InMemoryJournalVerificationFindingStore(), exceptions, new RecordingAlerts(), d.World.Clock, new JournalVerifierOptions(),
                d.World.DPlus.Records),
            new JournalBaselineService(reader, d.Archive, checkpoints, exceptions, d.World.Clock));
    }

    private static async Task BaselineCleanAsync(Verification v, Delivered d) =>
        Assert.Equal(nameof(JournalBaselineOutcome.Clean),
            (await v.Baseline.RunAsync(JournalVerificationWorld.BaselineRequest(d.Org, JournalStreamNames.Audit, createSentinel: false), Ct)).Outcome);

    /// <summary>The incremental sweep misses a change below its checkpoint (IV-5); the full sweep reports it, alert-only (no quarantine).</summary>
    private static async Task DetectedByTheFullSweepAsync(Verification v, Delivered d, string code, string? subtype, long revision)
    {
        var incremental = await v.Verifier.SweepAsync(d.Org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, incremental.Outcome);
        var full = await v.Verifier.SweepAsync(d.Org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, full.Outcome);
        Assert.True(full.Findings.Any(f => f.FindingCode == code && f.Subtype == subtype && f.FirstRevision == revision),
            "expected " + code + "/" + subtype + "@" + revision + "; got " + string.Join(", ", full.Findings.Select(f => f.FindingCode + "/" + f.Subtype + "@" + f.FirstRevision)));
        Assert.Null(full.NewCheckpoint);
        Assert.False(d.World.Organizations[d.Org].IntegrityQuarantineEnabled); // alert-only (OD-DV-3)
    }

    /// <summary>EG-1 (inverts <c>ExpectedGapDeletingADeliveredRowIsNotDetectedAndTheKeyIsRecordedAgain</c>): deletion of a delivered row (J2).</summary>
    [Fact]
    [Trait("M2F3-43", "Detected")]
    [Trait("SEC-BL-01", "EG-1")]
    public async Task M2F343VerifierDeletingADeliveredRowIsDetectedByTheVerifierAndTheKeyIsStillRecordedAgainUntilDetection()
    {
        var d = await DeliveredFromGenesisAsync(CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Verify);
        var v = VerificationOf(d);
        await BaselineCleanAsync(v, d);
        d.World.Journal.Remove(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare));

        // Residual (R-DR-2): delivery reads only pending rows; the recorder's lookup sees a journal without the key and records it again.
        Assert.Equal(0, await ArchivePassAsync(d));
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None));
        Assert.Equal(1, await ArchivePassAsync(d)); // D+ accepts it: its predecessor (Verify) is archived and linked

        await DetectedByTheFullSweepAsync(v, d, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.RevisionGap, d.Original[CapabilityRefusalEvidence.EntryPoints.Prepare].Revision);
        await AssertArchiveKeepsTheDeliveredBytesAsync(d);
    }

    /// <summary>EG-2 (inverts <c>ExpectedGapAConsistentlyRedigestedDeliveredRowThatIsNotSentAgainIsNotDetected</c>): a re-digested delivered row, never re-sent (J1).</summary>
    [Fact]
    [Trait("M2F3-43", "Detected")]
    [Trait("SEC-BL-01", "EG-2")]
    public async Task M2F343VerifierAConsistentlyRedigestedDeliveredRowThatIsNotSentAgainIsDetectedByTheVerifier()
    {
        var d = await DeliveredFromGenesisAsync(CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Verify);
        var v = VerificationOf(d);
        await BaselineCleanAsync(v, d);
        Alter(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare), recomputeDigest: true);

        // Residual (R-DR-2): the lookup accepts the forgery (own digest and closed body only) until the verifier reports it.
        Assert.Equal(0, await ArchivePassAsync(d));
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.AlreadyRecorded, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None));
        Assert.Equal(1, await ArchivePassAsync(d));

        // The row's own digest was recomputed (R-1 holds); its successor still links to the original digest (R-7).
        await DetectedByTheFullSweepAsync(v, d, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.LinkMismatch, d.Original[CapabilityRefusalEvidence.EntryPoints.Verify].Revision);
        await AssertArchiveKeepsTheDeliveredBytesAsync(d);
    }

    /// <summary>EG-3 (inverts <c>ExpectedGapAnAlteredDeliveredRowOutsideTheNewestEightIsNotDetected</c>): a digest-kept alteration outside the lookup window (J1).</summary>
    [Fact]
    [Trait("M2F3-43", "Detected")]
    [Trait("SEC-BL-01", "EG-3")]
    public async Task M2F343VerifierAnAlteredDeliveredRowOutsideTheNewestEightIsDetectedByTheVerifier()
    {
        var d = await DeliveredFromGenesisAsync(CapabilityRefusalEvidence.EntryPoints.Prepare);
        d.World.Clock.Advance(TimeSpan.FromHours(1));
        foreach (var entry in CapabilityRefusalEvidence.EntryPoints.All)
        {
            Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await d.Recorder.RecordAsync(new CapabilityRefusalContext(d.Org, 1), entry, CancellationToken.None));
        }

        d.World.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await d.Recorder.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None));
        Assert.Equal(8, await ArchivePassAsync(d));
        var v = VerificationOf(d);
        await BaselineCleanAsync(v, d);

        Alter(Row(d, CapabilityRefusalEvidence.EntryPoints.Prepare), recomputeDigest: false);
        var restarted = new CapabilityRefusalEvidenceRecorder(d.World.Store, d.World.Clock);
        Assert.Equal(CapabilityRefusalEvidenceOutcome.Committed, await restarted.RecordAsync(new CapabilityRefusalContext(d.Org, 1), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None));
        Assert.Equal(1, await ArchivePassAsync(d));

        await DetectedByTheFullSweepAsync(v, d, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.BodyDigestMismatch, d.Original[CapabilityRefusalEvidence.EntryPoints.Prepare].Revision);
    }
}
