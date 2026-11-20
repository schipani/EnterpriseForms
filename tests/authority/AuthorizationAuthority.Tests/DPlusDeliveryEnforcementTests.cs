using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-5a: delivery-time chain enforcement D+ on the audit stream (<c>SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §5, phase P1; amendment A-2).
/// Genesis and predecessor delivery; missing, corrupted, malformed, re-chained and mis-linked predecessors; forged Anchored rows; outage and
/// recovery; the stream stop and its re-derivation; concurrency; isolation of the authorization and content streams; no quarantine; the
/// finding-before-state order; the A-2 sentinel bootstrap.
/// <para>
/// SEC-BL-01 WP-5b (C-157, amendment A-6 §26): the suite runs once per D+-enabled stream (one concrete class per stream). The stream under
/// test is <c>Rig.Stream</c>; the other known streams are delivered alongside it to show isolation.
/// </para>
/// </summary>
public abstract class DPlusDeliveryEnforcementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The D+-enabled stream this run of the suite exercises.</summary>
    protected abstract string Stream { get; }

    private sealed record Rig(CapabilityRefusalEvidenceWorld World, Guid Org, CountingArchive Archive, RecordingAlerts Alerts, InMemoryJournalVerificationFindingStore Findings, ArchiveProcessor Processor,
        string Stream);

    /// <summary>An organization with an archived genuine predecessor at 40 (unless <paramref name="archivePredecessor"/> is false) and a sentinel.</summary>
    private async Task<Rig> RigAsync(bool archivePredecessor = true, bool sentinel = true, bool fresh = false, IJournalVerificationFindingStore? findingStore = null, bool noStore = false)
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization(freshAuditStream: fresh);
        var archive = new CountingArchive();
        if (!fresh)
        {
            world.AddStreamHistory(org, Stream); // WP-5b: genuine revision-40 history of the stream under test (audit: unchanged)
        }

        // The audit history of a non-fresh organization is archived when audit is not the stream under test, so audit delivers alongside.
        if (!fresh && Stream != JournalStreamNames.Audit)
        {
            var audit = world.AuditPredecessors[org];
            await archive.Inner.AppendAsync(org, JournalStreamNames.Audit, 40, Encoding.UTF8.GetBytes(audit.JournalEventBodyJson), audit.JournalEventDigest, Ct);
        }

        if (archivePredecessor && !fresh)
        {
            var predecessor = world.Predecessor(org, Stream);
            await archive.Inner.AppendAsync(org, Stream, 40, Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson), predecessor.JournalEventDigest, Ct);
        }

        // G14-R2 (A-4 §24): the trusted expected instance is pinned whether or not the sentinel exists yet (a baseline or restore later
        // provides a sentinel naming this instance).
        if (sentinel)
        {
            await world.DPlus.ProvisionAsync(archive.Inner, world.Clock.GetUtcNow());
        }
        else
        {
            await world.DPlus.PinAsync(PinnedInstance, world.Clock.GetUtcNow());
        }

        var alerts = new RecordingAlerts();
        var findings = new InMemoryJournalVerificationFindingStore();
        return new Rig(world, org, archive, alerts, findings, new ArchiveProcessor(world.Store, archive, world.Clock, alerts, noStore ? null : findingStore ?? findings, world.DPlus.Durability), Stream);
    }

    /// <summary>G14-R2: the instance pinned for a rig built without a sentinel (a later baseline or restore provides a sentinel naming it).</summary>
    private const string PinnedInstance = "archive-instance-pinned-for-test";

    /// <summary>G14-R2: the sentinel a baseline or a restore of the pinned archive provides.</summary>
    private static ArchiveIdentity PinnedIdentity(Rig rig) => new(ArchiveIdentity.SchemaV1, PinnedInstance, rig.World.Clock.GetUtcNow());

    /// <summary>Appends an entry to the stream under test (the name is kept from the audit-only suite).</summary>
    private static SecurityJournalEntry AppendAudit(Rig rig, string note = "event")
    {
        rig.World.Clock.Advance(TimeSpan.FromSeconds(1));
        return rig.World.SeedStream(rig.Org, rig.Stream, "TestAuditEvent", new { note });
    }

    /// <summary>Appends an entry to another stream (a stream without history starts at genesis; the audit stream continues its archived history).</summary>
    private static SecurityJournalEntry AppendOther(Rig rig, string stream)
    {
        Assert.NotEqual(rig.Stream, stream);
        rig.World.Clock.Advance(TimeSpan.FromSeconds(1));
        return rig.World.SeedStream(rig.Org, stream, "TestEvent", new { stream });
    }

    /// <summary>The known streams other than the one under test.</summary>
    private static List<string> Others(Rig rig) => [.. AuditDeliveryEnforcement.KnownStreams.Where(s => s != rig.Stream)];

    /// <summary>J1 sets the stream head digest the next entry chains onto (the stream's own organization column).</summary>
    private static void SetHeadDigest(Rig rig, byte[] digest)
    {
        var state = rig.World.Organizations[rig.Org];
        switch (rig.Stream)
        {
            case JournalStreamNames.Audit:
                state.LatestAuditJournalDigest = digest;
                break;
            case JournalStreamNames.Authorization:
                state.LatestSecurityJournalDigest = digest;
                break;
            default:
                state.LatestContentJournalDigest = digest;
                break;
        }
    }

    private static Task<int> PassAsync(Rig rig) => rig.Processor.ProcessOrganizationAsync(rig.Org, Ct);

    private static void AssertRefused(Rig rig, SecurityJournalEntry entry, string code, string? subtype = null)
    {
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState);
        Assert.Equal("DPLUS_" + code + (subtype is null ? string.Empty : ":" + subtype), entry.LastArchiveErrorCode);
        Assert.Null(entry.ArchiveReceiptReference);
        Assert.DoesNotContain(entry.JournalStreamRevision, rig.Archive.Appended.Where(a => a.Stream == rig.Stream).Select(a => a.Revision));
        Assert.Single(rig.Findings.Records, r => Encoding.UTF8.GetString(r.Value).Contains("\"finding_code\":\"" + code + "\"", StringComparison.Ordinal));
        Assert.Contains(rig.Alerts.Raised, a => a.AlertKind == "JournalDeliveryRefused" && a.Severity == "Critical" && a.Summary.Contains("DPLUS_" + code, StringComparison.Ordinal));
        Assert.False(rig.World.Organizations[rig.Org].IntegrityQuarantineEnabled); // DP-11
    }

    // ---- Valid delivery ----

    [Fact]
    public async Task AGenesisEntryAndItsSuccessorsAreDeliveredWithinOnePassAndAcrossPasses()
    {
        var rig = await RigAsync(fresh: true);
        var first = AppendAudit(rig);
        var second = AppendAudit(rig);
        Assert.Equal(1, first.JournalStreamRevision);
        Assert.Equal(2, await PassAsync(rig)); // revision 2's predecessor is the bytes just appended (DP-4)
        var third = AppendAudit(rig);
        Assert.Equal(1, await PassAsync(rig)); // revision 3's predecessor is read from the archive
        Assert.All([first, second, third], e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        Assert.Equal([1L, 2L, 3L], rig.Archive.Appended.Where(a => a.Stream == rig.Stream).Select(a => a.Revision));
        Assert.Empty(rig.Alerts.Raised);
        Assert.Empty(rig.Findings.Records);
    }

    [Fact]
    public async Task AnEntryWhosePredecessorIsArchivedAndLinkedIsDelivered()
    {
        var rig = await RigAsync();
        var entry = AppendAudit(rig);
        Assert.Equal(41, entry.JournalStreamRevision);
        Assert.Equal(1, await PassAsync(rig));
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
        Assert.Equal(1, rig.Archive.PredecessorReads);
    }

    [Fact]
    public async Task AGenesisRowCarryingAPreviousDigestOrAMismatchedColumnIsRefused()
    {
        var rig = await RigAsync(fresh: true);
        SetHeadDigest(rig, SHA256.HashData("forged"u8)); // revision 1 then carries a previous digest
        var genesis = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AssertRefused(rig, genesis, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.GenesisViolation);

        var other = await RigAsync();
        var entry = AppendAudit(other);
        entry.JournalEventKind = "ForgedKind"; // J1 edits a column; the envelope keeps the original kind
        Assert.Equal(0, await PassAsync(other));
        AssertRefused(other, entry, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
    }

    // ---- Refusals ----

    [Fact]
    public async Task AMissingPredecessorPreventsTheAppend()
    {
        var rig = await RigAsync(archivePredecessor: false);
        var entry = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AssertRefused(rig, entry, JournalFindingCodes.PredecessorMissing);
    }

    [Theory]
    [InlineData("altered")]
    [InlineData("malformed")]
    [InlineData("other-revision")]
    public async Task ACorruptedOrMalformedArchivedPredecessorPreventsTheAppend(string corruption)
    {
        var rig = await RigAsync(archivePredecessor: false);
        var predecessor = rig.World.Predecessor(rig.Org, rig.Stream);
        var bytes = corruption switch
        {
            "altered" => Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson.Replace("genuine", "GENUINE", StringComparison.Ordinal)),
            "malformed" => "{\"not\":\"an envelope\"}"u8.ToArray(),
            _ => Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson.Replace("\"stream_revision\":40", "\"stream_revision\":39", StringComparison.Ordinal)),
        };
        rig.Archive.Inner.PutObjectForTesting($"{rig.Org:D}/{rig.Stream}/40", bytes);
        var entry = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        switch (corruption)
        {
            case "altered":
                AssertRefused(rig, entry, JournalFindingCodes.DeliveryLinkMismatch);
                break;
            case "malformed":
                AssertRefused(rig, entry, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.EnvelopeMalformed);
                break;
            default:
                AssertRefused(rig, entry, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
                break;
        }
    }

    [Fact]
    public async Task ARechainedRowWithChangedHistoricalContentAndRecomputedDigestsIsRefused()
    {
        var rig = await RigAsync();
        var delivered = AppendAudit(rig, "original");
        Assert.Equal(1, await PassAsync(rig));

        // J1 rewrites the delivered row 41 and recomputes its digest, then appends 42 chained onto the forged 41.
        delivered.JournalEventBodyJson = delivered.JournalEventBodyJson.Replace("original", "rewritten", StringComparison.Ordinal);
        delivered.JournalEventDigest = CanonicalJson.Digest(Encoding.UTF8.GetBytes(delivered.JournalEventBodyJson));
        SetHeadDigest(rig, delivered.JournalEventDigest);
        var next = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AssertRefused(rig, next, JournalFindingCodes.DeliveryLinkMismatch); // the archive still holds the original 41
    }

    [Fact]
    public async Task AnIncorrectPredecessorLinkIsRefused()
    {
        var rig = await RigAsync();
        SetHeadDigest(rig, SHA256.HashData("not the predecessor"u8));
        var entry = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AssertRefused(rig, entry, JournalFindingCodes.DeliveryLinkMismatch);
    }

    [Fact]
    public async Task AForgedAnchoredPredecessorIsNotTrustedTheArchiveDecides()
    {
        var rig = await RigAsync();
        var forged = AppendAudit(rig);
        forged.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // J1: claims archival, never archived
        forged.ArchiveReceiptReference = "forged-receipt";
        var next = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AssertRefused(rig, next, JournalFindingCodes.PredecessorMissing);
    }

    // ---- Outage and recovery ----

    [Theory]
    [InlineData("sentinel")]
    [InlineData("predecessor")]
    [InlineData("append")]
    public async Task AnArchiveOutageLeavesTheEntryPendingWithoutFindingAlertOrQuarantine(string failing)
    {
        var rig = await RigAsync();
        var entry = AppendAudit(rig);
        rig.Archive.Failing = failing;
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal(ArchiveDeliveryStates.Pending, entry.ArchiveDeliveryState);
        Assert.Equal("injected " + failing + " outage", entry.LastArchiveErrorCode);
        Assert.Empty(rig.Findings.Records);
        Assert.Empty(rig.Alerts.Raised);
        Assert.False(rig.World.Organizations[rig.Org].IntegrityQuarantineEnabled);

        // Recovery: the next pass delivers it, in order, exactly once.
        rig.Archive.Failing = null;
        var later = AppendAudit(rig);
        Assert.Equal(2, await PassAsync(rig));
        Assert.Equal([41L, 42L], rig.Archive.Appended.Where(a => a.Stream == rig.Stream).Select(a => a.Revision));
        Assert.All([entry, later], e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
    }

    [Fact]
    public async Task ACrashBetweenTheArchiveWriteAndTheRowUpdateIsCompletedIdempotently()
    {
        var rig = await RigAsync();
        var entry = AppendAudit(rig);
        await rig.Archive.Inner.AppendAsync(rig.Org, rig.Stream, 41, Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), entry.JournalEventDigest, Ct); // written, row still Pending (DP-8)
        Assert.Equal(1, await PassAsync(rig));
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
        Assert.Empty(rig.Alerts.Raised);
    }

    [Fact]
    public async Task AnArchivedSuccessorThatDiffersFromTheRowKeepsTheExistingConflictQuarantine()
    {
        // §5.2 / DP-10: after an AuthorityDb restore, r + 1 passes D+ and then collides in AppendAsync (existing ARCHIVE_BODY_MISMATCH quarantine).
        var rig = await RigAsync();
        var entry = AppendAudit(rig);
        rig.Archive.Inner.PutObjectForTesting($"{rig.Org:D}/{rig.Stream}/41", "{\"different\":true}"u8.ToArray());
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal(("ARCHIVE_BODY_MISMATCH", true), (entry.LastArchiveErrorCode, rig.World.Organizations[rig.Org].IntegrityQuarantineEnabled));
        Assert.Empty(rig.Alerts.Raised); // not a D+ refusal
    }

    // ---- Stream stop ----

    [Fact]
    public async Task AStoppedStreamDeliversNoLaterRevisionAndDoesNotCascade()
    {
        var rig = await RigAsync(archivePredecessor: false);
        var refused = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        var later = Enumerable.Range(0, 3).Select(_ => AppendAudit(rig)).ToList();
        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(0, await PassAsync(rig));
        }

        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, refused.ArchiveDeliveryState);
        Assert.All(later, e => Assert.Equal(ArchiveDeliveryStates.Pending, e.ArchiveDeliveryState)); // not cascaded (§5.3)
        Assert.Equal(ArchiveProcessor.StreamStoppedCode, later[0].LastArchiveErrorCode);
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        Assert.Single(rig.Findings.Records); // one finding for the stop; the held rows add none
        Assert.Single(rig.Alerts.Raised);
    }

    [Fact]
    public async Task ARowAfterAnExistingDigestMismatchIsHeldNotDeliveredAcrossTheGap()
    {
        // E-3 before WP-5a: the next revision was delivered after a VerificationFailed row, leaving an archive gap. D+ holds it.
        var rig = await RigAsync();
        var tampered = AppendAudit(rig);
        tampered.JournalEventBodyJson = tampered.JournalEventBodyJson.Replace("event", "EVENT", StringComparison.Ordinal);
        var next = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig)); // existing R-1 response: VerificationFailed + quarantine (DP-10, unchanged)
        Assert.Equal(ArchiveProcessor.JournalBodyDigestMismatch, tampered.LastArchiveErrorCode);
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.StreamStoppedCode), (next.ArchiveDeliveryState, next.LastArchiveErrorCode));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
    }

    // ---- Sentinel and A-2 ----

    [Fact]
    public async Task AnAbsentSentinelRefusesAuditDeliveryWithoutATerminalStateAndDeliveryResumesOnceItExists()
    {
        var rig = await RigAsync(sentinel: false);
        var entry = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.Pending, "DPLUS_ARCHIVE_REGRESSION:SENTINEL_ABSENT"), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Contains(rig.Alerts.Raised, a => a.Summary.Contains("ARCHIVE_REGRESSION", StringComparison.Ordinal));
        Assert.False(rig.World.Organizations[rig.Org].IntegrityQuarantineEnabled);

        await rig.Archive.Inner.CreateIdentityAsync(PinnedIdentity(rig), Ct); // e.g. by a baseline or restore of the pinned instance (G14-R2)
        Assert.Equal(1, await PassAsync(rig));
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
    }

    // ---- Durable findings (DP-12, WP-5a.1) ----

    // ---- Durable stop: marker + finding (A-3, WP-5a.2) ----

    private static async Task RestoreGenuinePredecessorAsync(Rig rig)
    {
        var predecessor = rig.World.Predecessor(rig.Org, rig.Stream);
        await rig.Archive.Inner.AppendAsync(rig.Org, rig.Stream, 40, Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson), predecessor.JournalEventDigest, Ct); // J4 replaces the evidence
    }

    private static ArchiveProcessor Restart(Rig rig, IJournalVerificationFindingStore? store) => new(rig.World.Store, rig.Archive, rig.World.Clock, rig.Alerts, store, rig.World.DPlus.Durability);

    private static string MarkerKey(Rig rig, long revision) => $"meta/dplus-stops/{rig.Org:D}/{rig.Stream}/{revision:D12}";

    [Fact]
    public async Task BothStoresAvailableARefusalWritesTheMarkerAndTheFindingThenMarksTheRow()
    {
        var rig = await RigAsync(archivePredecessor: false);
        var entry = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        var marker = Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));
        Assert.EndsWith("/000000000041.json", marker.ObjectName, StringComparison.Ordinal);
        var parsed = DPlusStreamStops.TryParse(marker.Bytes, rig.Org, rig.Stream, 41)!;
        var finding = Assert.Single(rig.Findings.Records);
        Assert.Contains(parsed.FindingIdentity, finding.Key, StringComparison.Ordinal); // the marker names the finding's deterministic identity
        Assert.Contains("marker:meta/dplus-stops/", Encoding.UTF8.GetString(finding.Value), StringComparison.Ordinal);
        Assert.False(rig.World.Organizations[rig.Org].IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task FindingStoreUnavailableTheMarkerAloneStopsTheStreamAcrossRestartAndEvidenceReplacementAndRecoveryCompletesTheFinding()
    {
        var store = new SwitchableFindingStore { RecordAvailable = false };
        var rig = await RigAsync(archivePredecessor: false, findingStore: store);
        var entry = AppendAudit(rig);
        var later = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.FindingStoreUnavailableCode), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct)); // durable stop established

        // "Termination after marker persistence, before finding persistence", then the evidence is replaced.
        await RestoreGenuinePredecessorAsync(rig);
        Assert.Equal(0, await Restart(rig, store).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);

        store.RecordAvailable = true; // the finding store recovers: discovery completes the finding from the marker, then marks the row
        Assert.Equal(0, await Restart(rig, store).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Single(store.Records); // the original classification, once; no fabricated security finding for an outage
        Assert.Equal(0, await Restart(rig, store).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.Pending, later.ArchiveDeliveryState);
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
    }

    [Fact]
    public async Task ArchiveMarkerWriteUnavailableTheFindingAloneStopsTheStreamAndTheMarkerIsCompletedLater()
    {
        var rig = await RigAsync(archivePredecessor: false);
        var entry = AppendAudit(rig);
        rig.Archive.Failing = "marker-write";
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.StopMarkerUnavailableCode), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Single(rig.Findings.Records);
        Assert.Empty(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));

        // "Termination after finding persistence", evidence replaced: the finding keeps the stream stopped.
        await RestoreGenuinePredecessorAsync(rig);
        Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);

        rig.Archive.Failing = null; // the archive accepts writes again: the marker is created from the finding, then the row is marked
        Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState);
        Assert.DoesNotContain(rig.Findings.Records, r => Encoding.UTF8.GetString(r.Value).Contains(JournalFindingCodes.DPlusStopRecordIncomplete, StringComparison.Ordinal)); // an outage, not tampering
    }

    [Fact]
    public async Task BothStoresUnavailableTheRefusalIsHeldInMemoryAndTheDurableStopIsNotEstablished()
    {
        var store = new SwitchableFindingStore { RecordAvailable = false };
        var rig = await RigAsync(archivePredecessor: false, findingStore: store);
        var entry = AppendAudit(rig);
        rig.Archive.Failing = "marker-write";
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.DurableStopNotEstablishedCode), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Contains(rig.Alerts.Raised, a => a.AlertKind == "JournalDeliveryDurableStopNotEstablished");

        // In the same process the hold survives evidence replacement; once a store recovers, the original records are written.
        await RestoreGenuinePredecessorAsync(rig);
        Assert.Equal(0, await PassAsync(rig));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        rig.Archive.Failing = null;
        store.RecordAvailable = true;
        Assert.Equal(0, await PassAsync(rig));
        Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));
        Assert.Single(store.Records);
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, entry.ArchiveDeliveryState);
    }

    /// <summary>
    /// G14-R2 (A-4 §24, W2-S; SRT-40): the A-3 residual sequence (§23.3) — a termination before either durable stop record is written,
    /// followed by replacement of the evidence — no longer resumes delivery. The evidence commitment was durable before D+ evaluated anything,
    /// so the restarted process sees the replaced predecessor as a change and stops the stream durably. (Inverted from the former
    /// <c>AcceptedResidualTerminationBeforeAnyDurableRecordThenEvidenceReplacementResumesDelivery</c>; DD-10 is not thereby closed.)
    /// </summary>
    [Fact]
    public async Task W2SDetectsEvidenceReplacedAfterATerminationBeforeAnyDurableStopRecord()
    {
        var store = new SwitchableFindingStore { RecordAvailable = false };
        var rig = await RigAsync(archivePredecessor: false, findingStore: store);
        var entry = AppendAudit(rig);
        rig.Archive.Failing = "marker-write";
        Assert.Equal(0, await PassAsync(rig)); // detected; neither stop record written; the commitment was written first
        await RestoreGenuinePredecessorAsync(rig);
        rig.Archive.Failing = null;
        store.RecordAvailable = true;
        Assert.Equal(0, await Restart(rig, store).ProcessOrganizationAsync(rig.Org, Ct)); // the process "terminated": its in-memory hold is gone
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_" + JournalFindingCodes.DPlusEvidenceChangedAfterCommitment), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        Assert.Contains(store.Records.Values, r => Encoding.UTF8.GetString(r).Contains(JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, StringComparison.Ordinal));
        Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));
    }

    [Fact]
    public async Task AResetOrForgedRowStateNeverResumesDeliveryAndTheFailedRowIsReMarkedFromTheRecords()
    {
        var rig = await RigAsync(archivePredecessor: false);
        var refused = AppendAudit(rig);
        var next = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        refused.ArchiveDeliveryState = ArchiveDeliveryStates.Pending; // J1 reset
        Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, refused.ArchiveDeliveryState);
        refused.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // J1 forges archival
        refused.ArchiveReceiptReference = "fake";
        await RestoreGenuinePredecessorAsync(rig);
        Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.StreamStoppedCode), (next.ArchiveDeliveryState, next.LastArchiveErrorCode));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        Assert.Single(rig.Findings.Records);
    }

    [Fact]
    public async Task AMarkerRemovedByAnArchiveActorIsReportedAndRecreatedTheStreamStaysStopped()
    {
        var rig = await RigAsync(archivePredecessor: false);
        AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AppendAudit(rig); // a later entry, pending behind the stop
        Assert.True(rig.Archive.Inner.RemoveMetaForTesting(MarkerKey(rig, 41) + ".json"));
        await RestoreGenuinePredecessorAsync(rig);
        Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        Assert.Contains(rig.Findings.Records, r => Encoding.UTF8.GetString(r.Value).Contains(JournalFindingCodes.DPlusStopRecordIncomplete, StringComparison.Ordinal));
        Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct)); // recreated, create-only
    }

    [Theory]
    [InlineData("altered")]
    [InlineData("foreign")]
    [InlineData("non-canonical-name")]
    public async Task AnAlteredOrUnexpectedMarkerKeepsTheStreamStoppedAndIsReported(string tampering)
    {
        var rig = await RigAsync(archivePredecessor: false);
        AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        AppendAudit(rig); // a later entry, pending behind the stop
        var stored = Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));
        switch (tampering)
        {
            case "altered":
                rig.Archive.Inner.PutMetaForTesting(stored.ObjectName, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(stored.Bytes).Replace("PREDECESSOR_MISSING", "LINK_MISMATCH", StringComparison.Ordinal) + " "));
                break;
            case "foreign":
                var marker = DPlusStreamStops.TryParse(stored.Bytes, rig.Org, rig.Stream, 41)!;
                rig.Archive.Inner.PutMetaForTesting(stored.ObjectName, DPlusStreamStops.Serialize(marker with { OrganizationId = Guid.CreateVersion7() }));
                break;
            default:
                rig.Archive.Inner.PutMetaForTesting(MarkerKey(rig, 41) + ".bak", "{}"u8.ToArray());
                break;
        }

        // Even with the evidence repaired and every finding removed by J6, an invalid or unexpected marker object keeps the stream stopped.
        await RestoreGenuinePredecessorAsync(rig);
        foreach (var key in rig.Findings.Records.Keys.ToList())
        {
            rig.Findings.RemoveForTesting(rig.Org, rig.Stream, key[(key.LastIndexOf('/') + 1)..]);
        }

        Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        Assert.Contains(rig.Findings.Records, r => Encoding.UTF8.GetString(r.Value).Contains(JournalFindingCodes.DPlusStopMarkerInvalid, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConflictingMarkerAtTheFailedRevisionIsReportedAndTheStreamStaysStopped()
    {
        var rig = await RigAsync(archivePredecessor: false);
        rig.Archive.Inner.PutMetaForTesting(MarkerKey(rig, 41) + ".json", "{\"pre-existing\":true}"u8.ToArray());
        AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig)); // the invalid pre-existing object already stops the stream at discovery
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
        Assert.Contains(rig.Findings.Records, r => Encoding.UTF8.GetString(r.Value).Contains(JournalFindingCodes.DPlusStopMarkerInvalid, StringComparison.Ordinal));
    }

    /// <summary>
    /// Known limit (A-3 §23.4, narrowed by A-4 §24 W2-S): an actor who removes the marker (J4/J5), the finding (J6) <b>and</b> both copies
    /// of the evidence commitment (J4/J5 and J6) leaves no durable record, and replaced evidence is delivered. Removing only the two stop
    /// records is no longer enough (<c>DPlusRecoveryTests.RemovingBothStopRecordsNoLongerResumesDeliveryWhenTheEvidenceChanged</c>).
    /// </summary>
    [Fact]
    public async Task KnownLimitRemovingBothStopRecordsAndBothCommitmentCopiesLeavesNoRecord()
    {
        var rig = await RigAsync(archivePredecessor: false);
        var entry = AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        rig.Archive.Inner.RemoveMetaForTesting(MarkerKey(rig, 41) + ".json");
        foreach (var key in rig.Findings.Records.Keys.ToList())
        {
            rig.Findings.RemoveForTesting(rig.Org, rig.Stream, key[(key.LastIndexOf('/') + 1)..]);
        }

        Assert.True(rig.Archive.Inner.RemoveMetaForTesting($"meta/dplus-intents/{rig.Org:D}/{rig.Stream}/000000000041.json"));
        Assert.True(rig.World.DPlus.Records.RemoveForTesting($"intents/{rig.Org:D}/{rig.Stream}/000000000041.json"));
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
        await RestoreGenuinePredecessorAsync(rig);
        Assert.Equal(1, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.Anchored, entry.ArchiveDeliveryState);
    }

    [Fact]
    public async Task DiscoveryFailuresAreNeverReadAsNoStopAndNeverFabricateAFinding()
    {
        // The marker listing fails: archive unavailability, the pass ends with the entry Pending (DP-7).
        var rig = await RigAsync();
        var entry = AppendAudit(rig);
        rig.Archive.Failing = "marker-read";
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal((ArchiveDeliveryStates.Pending, "injected marker-read outage"), (entry.ArchiveDeliveryState, entry.LastArchiveErrorCode));
        Assert.Empty(rig.Findings.Records);
        Assert.Empty(rig.Alerts.Raised);

        // The finding listing fails: inconclusive, the stream under test waits; the other streams' delivery continues; no finding.
        // (The finding store is shared, so another D+ stream with pending rows waits the same way; one fresh stream shows the isolation.)
        var store = new SwitchableFindingStore { ListAvailable = false };
        var other = await RigAsync(findingStore: store);
        var audit = AppendAudit(other);
        var unaffected = Others(other).Where(s => !AuditDeliveryEnforcement.IsEnforced(s)).Select(s => AppendOther(other, s)).ToList();
        Assert.Equal(unaffected.Count, await other.Processor.ProcessOrganizationAsync(other.Org, Ct));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.FindingStoreUnavailableCode), (audit.ArchiveDeliveryState, audit.LastArchiveErrorCode));
        Assert.All(unaffected, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        Assert.Empty(store.Records);
        store.ListAvailable = true;
        Assert.Equal(1, await other.Processor.ProcessOrganizationAsync(other.Org, Ct));
        Assert.Equal(ArchiveDeliveryStates.Anchored, audit.ArchiveDeliveryState);
    }

    [Fact]
    public async Task AMarkerStoppedStreamLeavesTheOtherStreamsDeliveryUnchanged()
    {
        var rig = await RigAsync(archivePredecessor: false);
        AppendAudit(rig);
        Assert.Equal(0, await PassAsync(rig));
        var others = Others(rig).Select(s => AppendOther(rig, s)).ToList();
        Assert.Equal(others.Count, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        Assert.All(others, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        Assert.False(rig.World.Organizations[rig.Org].IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task RepeatedPassesAndProcessorsCreateOneMarkerAndOneFinding()
    {
        var rig = await RigAsync(archivePredecessor: false);
        AppendAudit(rig);
        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(0, await Restart(rig, rig.Findings).ProcessOrganizationAsync(rig.Org, Ct));
        }

        Assert.Single(await rig.Archive.Inner.ReadStopMarkersAsync(rig.Org, rig.Stream, Ct));
        Assert.Single(rig.Findings.Records);
        Assert.Equal(1, rig.Archive.MarkerWrites);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task ConcurrentPassesDeliverEachEntryExactlyOnceAndInOrder()
    {
        var rig = await RigAsync();
        var entries = Enumerable.Range(0, 10).Select(_ => AppendAudit(rig)).ToList();
        var second = new ArchiveProcessor(rig.World.Store, rig.Archive, rig.World.Clock, rig.Alerts, rig.Findings, rig.World.DPlus.Durability);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => (i % 2 == 0 ? rig.Processor : second).ProcessOrganizationAsync(rig.Org, Ct), Ct)));
        Assert.Equal(10, results.Sum());
        Assert.Equal(Enumerable.Range(41, 10).Select(r => (long)r), rig.Archive.Appended.Where(a => a.Stream == rig.Stream).Select(a => a.Revision));
        Assert.All(entries, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        Assert.Empty(rig.Alerts.Raised);
    }

    // ---- Stream isolation ----

    /// <summary>WP-5b (A-6 §26): each stream has its own batch; more stopped rows than one batch never hold another stream (RD-6).</summary>
    [Fact]
    public async Task AStoppedStreamDoesNotHoldTheOtherStreamsEvenBeyondOneBatch()
    {
        var rig = await RigAsync(archivePredecessor: false);
        for (var i = 0; i < ArchiveProcessor.BatchSize + 8; i++)
        {
            AppendAudit(rig); // more blocked rows than one batch
        }

        var others = Others(rig).Select(s => AppendOther(rig, s)).ToList();
        Assert.Equal(others.Count, await PassAsync(rig));
        Assert.All(others, e => Assert.Equal(ArchiveDeliveryStates.Anchored, e.ArchiveDeliveryState));
        Assert.DoesNotContain(rig.Archive.Appended, a => a.Stream == rig.Stream);
    }

    /// <summary>WP-5b: an organization with nothing pending reads nothing from the archive (no per-pass sentinel or discovery cost).</summary>
    [Fact]
    public async Task NothingPendingReadsNothing()
    {
        Assert.True(AuditDeliveryEnforcement.IsEnforced(Stream));
        var rig = await RigAsync();
        Assert.Equal(0, await PassAsync(rig));
        Assert.Equal(0, rig.Archive.SentinelReads);
    }

    /// <summary>An in-memory archive with append and read counters and injected outages.</summary>
    private sealed class CountingArchive : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
    {
        public InMemorySecurityEventArchive Inner { get; } = new();

        public ConcurrentQueue<(string Stream, long Revision)> Appended { get; } = new();

        public string? Failing { get; set; }

        public int SentinelReads;

        public int PredecessorReads;

        public string ArchiveName => "Counting";

        private void Fail(string point)
        {
            if (string.Equals(Failing, point, StringComparison.Ordinal))
            {
                throw new ArchiveUnavailableException("injected " + point + " outage");
            }
        }

        public async Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken)
        {
            Fail("append");
            var receipt = await Inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken);
            Appended.Enqueue((streamName, streamRevision));
            return receipt;
        }

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Inner.ProbeAsync(cancellationToken);

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => Inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken);

        public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SentinelReads);
            Fail("sentinel");
            return Inner.ReadIdentityAsync(cancellationToken);
        }

        public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => Inner.CreateIdentityAsync(identity, cancellationToken);

        public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PredecessorReads);
            Fail("predecessor");
            return Inner.ReadEntryAsync(organizationId, streamName, streamRevision, cancellationToken);
        }

        public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            Inner.ListEntriesAsync(organizationId, streamName, continuationToken, pageSizeHint, cancellationToken);

        public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            Inner.ListEntriesAboveAsync(organizationId, streamName, afterRevision, continuationToken, pageSizeHint, cancellationToken);

        public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => Inner.SummarizeAsync(cancellationToken);

        public int MarkerWrites;

        public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref MarkerWrites);
            Fail("marker-write");
            return Inner.CreateStopMarkerAsync(organizationId, streamName, failedRevision, markerBytes, cancellationToken);
        }

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
        {
            Fail("marker-read");
            return Inner.ReadStopMarkersAsync(organizationId, streamName, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => Inner.ListMetaObjectNamesAsync(cancellationToken);

        // G14-R2 (A-4 §24): meta/ records. A generation >= 1 stop marker counts as a marker write (same injection point); W2-S commitment
        // and recovery-record I/O can be failed separately ("commitment", "recovery-read").
        public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (metaObjectName.StartsWith("meta/dplus-stops/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref MarkerWrites);
                Fail("marker-write");
            }
            else if (metaObjectName.StartsWith("meta/dplus-intents/", StringComparison.Ordinal))
            {
                Fail("commitment");
            }

            return Inner.CreateMetaAsync(metaObjectName, bytes, cancellationToken);
        }

        public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken)
        {
            if (metaObjectName.StartsWith("meta/dplus-intents/", StringComparison.Ordinal))
            {
                Fail("commitment");
            }

            return Inner.ReadMetaAsync(metaObjectName, cancellationToken);
        }

        public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken)
        {
            Fail("recovery-read");
            return Inner.ListMetaAsync(metaPrefix, cancellationToken);
        }
    }

    private sealed class SwitchableFindingStore : IJournalVerificationFindingStore
    {
        private int attempts;

        /// <summary>Both operations; kept for the WP-5a.1 tests.</summary>
        public bool Available
        {
            get => RecordAvailable && ListAvailable;
            set => RecordAvailable = ListAvailable = value;
        }

        public bool RecordAvailable { get; set; } = true;

        public bool ListAvailable { get; set; } = true;

        public int Attempts => Volatile.Read(ref attempts);

        public ConcurrentDictionary<string, byte[]> Records { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<byte[]>> ListAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            ListAvailable ? Task.FromResult<IReadOnlyList<byte[]>>([.. Records.Values.Select(v => v.ToArray())]) : throw new CheckpointStoreUnavailableException("finding store down");

        public Task<FindingRecordStatus> RecordAsync(Guid organizationId, string streamName, string findingIdentity, ReadOnlyMemory<byte> findingBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            return RecordAvailable ? Task.FromResult(Records.TryAdd(findingIdentity, findingBytes.ToArray()) ? FindingRecordStatus.Created : FindingRecordStatus.AlreadyRecorded)
                : throw new CheckpointStoreUnavailableException("finding store down");
        }
    }
}

/// <summary>SEC-BL-01 WP-5a/WP-5b: the D+ enforcement suite on the audit stream.</summary>
public sealed class DPlusDeliveryEnforcementAuditTests : DPlusDeliveryEnforcementTests
{
    protected override string Stream => JournalStreamNames.Audit;
}

/// <summary>SEC-BL-01 WP-5b (C-157 P2-D4, A-6 §26): the D+ enforcement suite on the content stream.</summary>
public sealed class DPlusDeliveryEnforcementContentTests : DPlusDeliveryEnforcementTests
{
    protected override string Stream => JournalStreamNames.Content;
}

/// <summary>SEC-BL-01 WP-5b (C-157 P2-D4, A-6 §26): the D+ enforcement suite on the authorization stream.</summary>
public sealed class DPlusDeliveryEnforcementAuthorizationTests : DPlusDeliveryEnforcementTests
{
    protected override string Stream => JournalStreamNames.Authorization;
}

/// <summary>SEC-BL-01 WP-5b: the D+-enabled stream set is fixed in code (§5.7): every journal stream, with no delivery path without D+.</summary>
public sealed class DPlusStreamPhaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryJournalStreamIsEnforcedAndTheSetIsFixed()
    {
        Assert.Equal([JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content], AuditDeliveryEnforcement.EnforcedStreams);
        Assert.Equal(AuditDeliveryEnforcement.KnownStreams, AuditDeliveryEnforcement.EnforcedStreams);
        Assert.All(AuditDeliveryEnforcement.KnownStreams, s => Assert.True(AuditDeliveryEnforcement.IsEnforced(s)));
        Assert.False(AuditDeliveryEnforcement.IsEnforced("platform-operators"));
    }

    /// <summary>No stream is delivered without D+: a forged head digest on any stream is refused, and a row of an unknown stream is never delivered.</summary>
    [Fact]
    public async Task NoStreamIsDeliveredWithoutDPlus()
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization(freshAuditStream: true);
        var archive = new InMemorySecurityEventArchive();
        await world.DPlus.ProvisionAsync(archive, world.Clock.GetUtcNow());
        var processor = new ArchiveProcessor(world.Store, archive, world.Clock, new RecordingAlerts(), new InMemoryJournalVerificationFindingStore(), world.DPlus.Durability);
        var state = world.Organizations[org];
        state.LatestSecurityJournalDigest = SHA256.HashData("forged predecessor"u8); // revision 1 would carry a previous digest
        state.LatestContentJournalDigest = SHA256.HashData("forged predecessor"u8);
        var refused = new[] { JournalStreamNames.Authorization, JournalStreamNames.Content }.Select(s => world.SeedStream(org, s, "TestEvent", new { s })).ToList();
        var unknown = JournalChain.Append(state, "unknown-stream", 1, "TestEvent", new { note = "x" }, null, null, world.Clock);
        lock (world.Journal)
        {
            world.Journal.Add(unknown);
        }

        Assert.Equal(0, await processor.ProcessOrganizationAsync(org, Ct));
        Assert.All(refused, e => Assert.Equal(ArchiveDeliveryStates.VerificationFailed, e.ArchiveDeliveryState));
        Assert.Equal(ArchiveDeliveryStates.Pending, unknown.ArchiveDeliveryState);
        Assert.Null(await archive.ReadAsync(org, "unknown-stream", 1, Ct));
    }
}
