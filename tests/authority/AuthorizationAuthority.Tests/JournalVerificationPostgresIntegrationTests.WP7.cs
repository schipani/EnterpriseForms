using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using GateState = AuthorizationAuthority.Contracts.V1.ProjectionGateState;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-7 (C-159, contract §13; A-8): integrated adversarial verification on real stores — disposable PostgreSQL 18 (real migrations,
/// G08 triggers and grants, the Authority runtime role), the Azurite security-event archive and the separately keyed Azurite
/// journal-verification store — across the <b>audit, authorization and content</b> streams, independently and together. Synthetic data and
/// synthetic approver keys only. Every test names the attacker class it exercises (J1 runtime credential, J2 AuthorityDb administrator with
/// the explicit trigger bypass, J3 restore, J4 archive credential, J6 verification-store credential); a J1 attack that G08 refuses is
/// reported as prevented, every other attack as detected, and an out-of-scope combination as a characterized limit. Development/Testing
/// evidence only; no production claim.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    public static TheoryData<string> WP7Streams => [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];

    private static readonly string[] Wp7TwoApprovers = ["approver-a", "approver-b"];

    private static readonly string[] Wp7StreamNames = [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];

    /// <summary>Delivered-row attacks (contract §13.2), each against every stream.</summary>
    public static TheoryData<string, string> WP7RowAttacks
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var stream in new[] { JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content })
            {
                foreach (var attack in new[] { "ADV-01-above", "ADV-01-below", "ADV-02", "ADV-02-anchored", "ADV-03", "ADV-05-above", "ADV-05-below", "ADV-06-above", "ADV-06-below", "ADV-07", "ADV-07-anchored", "ADV-09", "ADV-10", "ADV-14" })
                {
                    data.Add(stream, attack);
                }
            }

            return data;
        }
    }

    /// <summary>Archive-side attacks (J4), each against every stream.</summary>
    public static TheoryData<string, string> WP7ArchiveAttacks
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var stream in new[] { JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content })
            {
                foreach (var attack in new[] { "sentinel-deleted", "instance-replaced", "object-overwritten", "anchor-overwritten", "predecessor-deleted", "unexpected-object", "beyond-head-gap", "beyond-head-contiguous", "cross-organization" })
                {
                    data.Add(stream, attack);
                }
            }

            return data;
        }
    }

    private sealed record Wp7Rig(StreamRig Rig, JournalVerificationReader Reader, JournalVerifier Verifier, RecordingAlerts Alerts)
    {
        public Guid Org => Rig.Org;

        public string Stream => Rig.Stream;
    }

    private sealed record Wp7CheckpointBody(Guid FormRecordId, long CommittedContentRevision, Guid CommittedOperationId, string ManifestRootHex);

    private static JournalVerifier Wp7Verifier(StreamRig rig, JournalVerificationReader reader, RecordingAlerts alerts, TimeProvider? clock = null, ISecurityEventArchiveInventory? archive = null,
        IVerificationCheckpointStore? checkpoints = null) =>
        new(reader, archive ?? rig.Archive, checkpoints ?? Wp2Azurite.NewCheckpointStore(rig.StoreContainer), rig.Findings, Wp2Azurite.NewExceptionStore(rig.StoreContainer), alerts,
            clock ?? TimeProvider.System, new JournalVerifierOptions(), Wp2Azurite.NewRecordStore(rig.StoreContainer));

    private static async Task<JournalBaselineReport> Wp7BaselineAsync(StreamRig rig, JournalVerificationReader reader, string? stream = null,
        IReadOnlyList<JournalBaselineExceptionDisposition>? dispositions = null) =>
        await new JournalBaselineService(reader, rig.Archive, Wp2Azurite.NewCheckpointStore(rig.StoreContainer), Wp2Azurite.NewExceptionStore(rig.StoreContainer), TimeProvider.System)
            .RunAsync(JournalVerificationWorld.BaselineRequest(rig.Org, stream ?? rig.Stream, createSentinel: false, dispositions: dispositions), Ct);

    /// <summary>Four genuine rows delivered by D+ and baselined (checkpoint 1 at v = 4), then four more delivered (5..8, above the trusted checkpoint).</summary>
    private static async Task<Wp7Rig> Wp7RigAsync(string stream, SyntheticRecoveryApprovers approvers)
    {
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, 4);
        Assert.Equal(4, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var reader = SharedRuntimeReader.Value; // WP-7A: one pool for the run (per-test readers were never disposed)
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await Wp7BaselineAsync(rig, reader)).Outcome);
        await AppendStreamAsync(rig.Org, stream, 4);
        Assert.Equal(4, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var alerts = new RecordingAlerts();
        return new Wp7Rig(rig, reader, Wp7Verifier(rig, reader, alerts), alerts);
    }

    private static string Describe(JournalVerificationRun run) =>
        run.Outcome + " [" + string.Join(", ", run.Findings.Select(f => f.FindingCode + "/" + f.Subtype + "@" + f.FirstRevision + ".." + f.LastRevision)) + "] inconclusive=["
        + string.Join(",", run.InconclusiveReasons) + "]";

    private static void AssertFinding(JournalVerificationRun run, string code, string? subtype, long? revision)
    {
        Assert.True(run.Findings.Any(f => f.FindingCode == code && (subtype is null || f.Subtype == subtype) && (revision is null || f.FirstRevision == revision)),
            "expected " + code + "/" + subtype + "@" + revision + "; got " + Describe(run));
        Assert.Null(run.NewCheckpoint); // CK-1: no checkpoint advance on findings
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
    }

    private static (string Revision, string Digest) HeadColumns(string stream) => stream switch
    {
        JournalStreamNames.Authorization => ("authorization_revision", "latest_security_journal_digest"),
        JournalStreamNames.Content => ("content_integrity_revision", "latest_content_journal_digest"),
        _ => ("audit_journal_revision", "latest_audit_journal_digest"),
    };

    /// <summary>J2: resets the organization head of <paramref name="stream"/> to an existing revision (head digest from that row), as a consistent tail rewrite or a restore leaves it.</summary>
    private static async Task ResetHeadAsync(Guid org, string stream, long revision)
    {
        var (rev, digest) = HeadColumns(stream);
        var projection = stream == JournalStreamNames.Authorization ? ", projection_applied_revision = " + revision.ToString(CultureInfo.InvariantCulture) : string.Empty;
        await AdminSqlAsync($"UPDATE authority.organization_authorization_states SET {rev} = {revision.ToString(CultureInfo.InvariantCulture)}, {digest} = " +
            "(SELECT journal_event_digest FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision = "
            + revision.ToString(CultureInfo.InvariantCulture) + ")" + projection + " WHERE organization_id = @org", org, stream);
    }

    /// <summary>J2: deletes every row at or above <paramref name="fromRevision"/> and appends <paramref name="count"/> forged rows chained consistently from the row below (re-chained head).</summary>
    private static async Task RewriteTailAsync(Guid org, string stream, long fromRevision, int count)
    {
        await AdminSqlAsync("DELETE FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision >= "
            + fromRevision.ToString(CultureInfo.InvariantCulture), org, stream);
        await ResetHeadAsync(org, stream, fromRevision - 1);
        await AppendStreamAsync(org, stream, count);
    }

    private const string RowWhere = " WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision = ";

    private static string Rev(long revision) => revision.ToString(CultureInfo.InvariantCulture);

    /// <summary>J2 marks every non-Anchored row of the stream Anchored with forged receipt fields (passes nothing outside AuthorityDb).</summary>
    private static Task ForgeAnchoredAsync(Guid org, string stream) =>
        AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged', " +
                      "archive_receipt_digest = decode(repeat('ab', 32), 'hex'), journal_archived_on = now() WHERE organization_id = @org AND journal_stream_name = @stream AND archive_delivery_state <> 'Anchored'",
            org, stream);

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-01: delivered-row attacks ADV-01..ADV-10, ADV-14 on every stream (verifier; D+ at the next delivery; G08 prevention for J1)
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WP7-PG-01. Above the checkpoint (revisions 5..8) the incremental sweep detects within T_inc; below it (revisions 1..3) only the full sweep
    /// detects (A-1 IV-5), and the incremental sweep is asserted Clean to pin that latency. J1 variants are first attempted as the runtime role and
    /// must be refused by G08 (prevention); the same change is then made by J2 with the trigger bypass and must be detected. Nothing is quarantined
    /// by the verifier (alert-only); the protected gate never opens on forged state.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP7RowAttacks))]
    public async Task WP7DeliveredRowAttacksAreDetectedOnEveryStream(string stream, string attack)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        var below = attack.EndsWith("-below", StringComparison.Ordinal);
        var target = below ? 2L : 6L;
        var sweepKind = below ? JournalSweepKind.Full : JournalSweepKind.Incremental;
        string code;
        string? subtype;
        var revision = target;
        var nextDelivery = "none"; // what D+ must do with the next genuine append
        switch (attack)
        {
            case "ADV-01-above" or "ADV-01-below": // J2: delete a delivered row, no re-chain
                Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("DELETE FROM authority.security_journal_entries" + RowWhere + Rev(target), w.Org, stream)));
                await AdminSqlAsync("DELETE FROM authority.security_journal_entries" + RowWhere + Rev(target), w.Org, stream);
                (code, subtype) = (JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.RevisionGap);
                break;
            case "ADV-02" or "ADV-02-anchored": // J2: delete row 6 and re-chain consistently (7, 8 replaced by a valid chain 6, 7), head updated
                await RewriteTailAsync(w.Org, stream, 6, 2);
                (code, subtype) = (JournalFindingCodes.AnchorMismatch, null);
                // Rows left Pending: D+ re-evaluates forged revision 6, whose archived counterpart holds the committed genuine bytes (EP-4), so the
                // append collides: the existing ARCHIVE_BODY_MISMATCH quarantine (DP-10). Rows also forged Anchored: D+ evaluates the next genuine
                // append and refuses its link to the forged head (durable LINK_MISMATCH stop).
                nextDelivery = attack == "ADV-02" ? "quarantine" : "refused";
                break;
            case "ADV-03": // J2: truncate the delivered tail and reset the head; the organization then stays quiet
                await AdminSqlAsync("DELETE FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision >= 6", w.Org, stream);
                await ResetHeadAsync(w.Org, stream, 5);
                (code, subtype) = (JournalFindingCodes.BehindArchive, null);
                break;
            case "ADV-14": // J3: an earlier copy restored (row-level equivalent of a restore to revision 5: rows above 5 gone, head as of 5)
                await AdminSqlAsync("DELETE FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision > 5", w.Org, stream);
                await ResetHeadAsync(w.Org, stream, 5);
                (code, subtype) = (JournalFindingCodes.BehindArchive, null);
                nextDelivery = "quarantine"; // the next append collides with the archived revision 6 (ARCHIVE_BODY_MISMATCH, M2F3-57)
                break;
            case "ADV-05-above" or "ADV-05-below": // re-digested alteration of a delivered row, never re-sent
                Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync(
                    "UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC')" + RowWhere + Rev(target), w.Org, stream)));
                await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC'), " +
                                    "journal_event_digest = sha256(convert_to(replace(journal_event_body_json, 'synthetic', 'SYNTHETIC'), 'UTF8'))" + RowWhere + Rev(target), w.Org, stream);
                // Above: the row differs from its archived bytes. Below: only the successor's link proves it (R-7), found by the full sweep.
                (code, subtype, revision) = below ? (JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.LinkMismatch, target + 1) : (JournalFindingCodes.AnchorMismatch, (string?)null, target);
                break;
            case "ADV-06-above" or "ADV-06-below": // digest-kept alteration of a delivered row
                Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync(
                    "UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC')" + RowWhere + Rev(target), w.Org, stream)));
                await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC')" + RowWhere + Rev(target), w.Org, stream);
                (code, subtype) = (JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.BodyDigestMismatch);
                break;
            case "ADV-07" or "ADV-07-anchored": // rewrite-and-extend (W-1): consistent rewrite of delivered rows 6..8, then a genuine append chained onto the forged head
                Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync(
                    "UPDATE authority.security_journal_entries SET journal_event_digest = decode(repeat('00', 32), 'hex')" + RowWhere + Rev(6), w.Org, stream)));
                await RewriteTailAsync(w.Org, stream, 6, 3);
                (code, subtype) = (JournalFindingCodes.AnchorMismatch, null);
                nextDelivery = attack == "ADV-07" ? "quarantine" : "refused"; // as ADV-02
                break;
            case "ADV-09": // swap revisions 6 and 7
                Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET journal_stream_revision = 1000000" + RowWhere + Rev(6), w.Org, stream)));
                await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_stream_revision = 1000000" + RowWhere + "6; " +
                                    "UPDATE authority.security_journal_entries SET journal_stream_revision = 6" + RowWhere + "7; " +
                                    "UPDATE authority.security_journal_entries SET journal_stream_revision = 7" + RowWhere + "1000000", w.Org, stream);
                (code, subtype) = (JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
                break;
            case "ADV-10": // move row 7 to another stream of the same organization
                var other = stream == JournalStreamNames.Audit ? JournalStreamNames.Content : JournalStreamNames.Audit;
                Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await Wp7BaselineAsync(w.Rig, w.Reader, other)).Outcome); // the destination is verified too (empty, revision 0)
                Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET journal_stream_name = '" + other + "'" + RowWhere + Rev(7), w.Org, stream)));
                await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_stream_name = '" + other + "'" + RowWhere + Rev(7), w.Org, stream);
                var destination = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, other, JournalSweepKind.Incremental, Ct);
                AssertFinding(destination, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, 7);
                (code, subtype, revision) = (JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.RevisionGap, 7);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(attack));
        }

        if (below)
        {
            var incremental = await w.Verifier.SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
            Assert.True(incremental.Outcome == JournalSweepOutcome.Clean, "below the checkpoint the incremental sweep is not expected to see it (IV-5): " + Describe(incremental));
        }

        var run = await w.Verifier.SweepAsync(w.Org, stream, sweepKind, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " " + attack + ": " + Describe(run));
        AssertFinding(run, code, subtype, revision);
        if (attack is "ADV-02" or "ADV-02-anchored")
        {
            AssertFinding(run, JournalFindingCodes.BehindArchive, null, 8); // the archive still holds the deleted revision
        }

        Assert.NotEmpty(w.Alerts.Raised);
        Assert.False(await QuarantinedAsync(w.Org)); // the verifier is alert-only (OD-DV-3)

        if (attack is "ADV-02-anchored" or "ADV-07-anchored")
        {
            await ForgeAnchoredAsync(w.Org, stream); // J2 also forges delivery state for the re-chained head
            if (stream == JournalStreamNames.Authorization)
            {
                Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(w.Rig)); // E-6: the forged head has no trusted receipt
            }
        }

        if (nextDelivery != "none")
        {
            await AppendStreamAsync(w.Org, stream, 1);
            var delivered = await w.Rig.Processor().ProcessOrganizationAsync(w.Org, Ct);
            var rows = await AuditRowsAsync(w.Org, stream);
            var last = rows[^1];
            TestContext.Current.TestOutputHelper?.WriteLine(stream + " " + attack + " next delivery: delivered=" + delivered + " quarantined=" + await QuarantinedAsync(w.Org) + " rows="
                + string.Join(",", rows.Select(r => r.Revision + ":" + r.State + ":" + r.Code)));
            Assert.Equal(0, delivered);
            if (nextDelivery == "refused")
            {
                Assert.NotEqual(ArchiveDeliveryStates.Anchored, last.State);
                Assert.NotEmpty(await RawAsync(Wp2Azurite.RawArchiveContainer(w.Rig.Container), $"meta/dplus-stops/{w.Org:D}/{stream}/")); // durable stop
            }
            else
            {
                Assert.True(await QuarantinedAsync(w.Org)); // ADV-04 / M2F3-57: the existing collision quarantine, unchanged
                Assert.Contains(rows, r => r.State == ArchiveDeliveryStates.VerificationFailed && r.Code == "ARCHIVE_BODY_MISMATCH");
                if (stream == JournalStreamNames.Authorization)
                {
                    Assert.Equal(GateState.Quarantined, await RealGateAsync(w.Rig));
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-02: ADV-04 truncate then append; ADV-08 forged Anchored insertion by the runtime credential (J1) — per stream
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>WP7-PG-02 (ADV-04, J2/J3): truncating the delivered tail and appending again collides with the archive: the existing ARCHIVE_BODY_MISMATCH quarantine, unchanged.</summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7TruncatingTheTailThenAppendingKeepsTheExistingCollisionQuarantine(string stream)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        await AdminSqlAsync("DELETE FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = @stream AND journal_stream_revision >= 6", w.Org, stream);
        await ResetHeadAsync(w.Org, stream, 5);
        await AppendStreamAsync(w.Org, stream, 1); // a different revision 6
        Assert.Equal(0, await w.Rig.Processor().ProcessOrganizationAsync(w.Org, Ct));
        var row = (await AuditRowsAsync(w.Org, stream)).Single(r => r.Revision == 6);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " ADV-04: " + row.State + " " + row.Code);
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "ARCHIVE_BODY_MISMATCH"), (row.State, row.Code));
        Assert.True(await QuarantinedAsync(w.Org));
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.Quarantined, await RealGateAsync(w.Rig));
        }
    }

    /// <summary>
    /// WP7-PG-03 (ADV-08, ADV-18, J1): the runtime credential inserts a forged row already marked Anchored with fake receipt fields and a
    /// future-dated creation time (INSERT has no trigger, G8-4). Not prevented; detected as FAKE_RECEIPT; it opens no gate and releases nothing
    /// (E-6/E-7); the next genuine append is refused by D+ as PREDECESSOR_MISSING (a durable stop).
    /// </summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7AForgedAnchoredRowInsertedByTheRuntimeCredentialIsAFakeReceiptAndOpensNothing(string stream)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.Ready, await RealGateAsync(w.Rig)); // positive control: every entry genuinely delivered
        }

        SecurityJournalEntry forged;
        await using (var runtime = new RuntimeContextFactory(Runtime!).CreateDbContext())
        {
            var state = await runtime.OrganizationStates.SingleAsync(s => s.OrganizationId == w.Org, Ct);
            var (revisionColumn, _) = HeadColumns(stream);
            _ = revisionColumn;
            forged = JournalChain.Append(state, stream, 9, "SyntheticEvent", new MeasuredBody("forged " + stream, 9), null, null, TimeProvider.System);
            if (stream == JournalStreamNames.Audit)
            {
                state.AuditJournalRevision = 9;
            }
            else if (stream == JournalStreamNames.Authorization)
            {
                state.AuthorizationRevision = 9;
                state.ProjectionAppliedRevision = 9;
            }
            else
            {
                runtime.Entry(state).Property(nameof(state.ContentIntegrityRevision)).CurrentValue = 9L;
            }

            forged.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            forged.ArchiveReceiptReference = "forged-receipt";
            forged.ArchiveReceiptDigest = SHA256.HashData("forged"u8);
            forged.JournalArchivedOn = DateTimeOffset.UtcNow;
            forged.JournalCreatedOn = DateTimeOffset.UtcNow.AddDays(1); // future-dated: informational only (contract §4)
            runtime.JournalEntries.Add(forged);
            await runtime.SaveChangesAsync(Ct); // J1 succeeds: the database cannot tell a fake receipt
        }

        var run = await w.Verifier.SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " ADV-08: " + Describe(run));
        AssertFinding(run, JournalFindingCodes.FakeReceipt, null, 9);
        Assert.False((await new TrustedJournalReceipts(w.Rig.Archive, Durable(w.Rig.Container)).VerifyAsync(forged, Ct)).IsVerified);
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(w.Rig)); // E-6: the forged head has no trusted receipt
        }

        await AppendStreamAsync(w.Org, stream, 1);
        Assert.Equal(0, await w.Rig.Processor().ProcessOrganizationAsync(w.Org, Ct));
        var next = (await AuditRowsAsync(w.Org, stream)).Single(r => r.Revision == 10);
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), (next.State, next.Code));
        Assert.False(await QuarantinedAsync(w.Org));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-04: archive-side attacks (J4) per stream: verifier, D+ and the trusted receipt
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(WP7ArchiveAttacks))]
    public async Task WP7ArchiveSideAttacksAreDetectedAndNeverEstablishTrust(string stream, string attack)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        var raw = Wp2Azurite.RawArchiveContainer(w.Rig.Container);
        var kind = JournalSweepKind.Incremental;
        string code;
        long? revision;
        string? subtype = null;
        var dplus = "none"; // expected D+ consequence for the next genuine append
        switch (attack)
        {
            case "sentinel-deleted": // ADV-12: the container's identity removed (wipe, replacement or misconfiguration)
                await raw.GetBlobClient("meta/archive-identity.json").DeleteAsync(cancellationToken: Ct);
                (code, revision) = (JournalFindingCodes.ArchiveRegression, null);
                dplus = "wait";
                break;
            case "instance-replaced": // ADV-12 / archive-instance replacement: a new sentinel with another instance identifier
                await raw.GetBlobClient("meta/archive-identity.json").DeleteAsync(cancellationToken: Ct);
                Assert.Equal(ArchiveIdentityCreateStatus.Created, (await w.Rig.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow), Ct)).Status);
                (code, revision) = (JournalFindingCodes.ArchiveRegression, null);
                dplus = "stop";
                break;
            case "object-overwritten": // ADV-12: a delivered object above the checkpoint overwritten (create-only is client-side on Azurite)
                await raw.GetBlobClient(StreamKey(w.Org, stream, 6)).UploadAsync(BinaryData.FromString("{\"overwritten\":true}"), overwrite: true, Ct);
                (code, revision) = (JournalFindingCodes.AnchorMismatch, 6);
                break;
            case "anchor-overwritten": // ADV-12: the checkpoint's own object replaced
                await raw.GetBlobClient(StreamKey(w.Org, stream, 4)).UploadAsync(BinaryData.FromString("{\"overwritten\":true}"), overwrite: true, Ct);
                (code, revision) = (JournalFindingCodes.ArchiveRegression, 4);
                break;
            case "predecessor-deleted": // archive predecessor deletion: the archived head removed before the next delivery
                await raw.GetBlobClient(StreamKey(w.Org, stream, 8)).DeleteAsync(cancellationToken: Ct);
                (code, revision) = (JournalFindingCodes.FakeReceipt, 8); // the row claims archival, the archive has no object (VF-8)
                dplus = "predecessor-missing";
                break;
            case "unexpected-object": // AE-3
                await raw.GetBlobClient($"org/{w.Org:D}/{stream}/not-a-revision.json").UploadAsync(BinaryData.FromString("{}"), overwrite: true, Ct);
                (code, revision, kind) = (JournalFindingCodes.ArchiveUnexpectedObject, null, JournalSweepKind.Full);
                break;
            case "beyond-head-gap": // AE-4/AE-6: an object above the AuthorityDb head after a gap
                await raw.GetBlobClient(StreamKey(w.Org, stream, 12)).UploadAsync(BinaryData.FromString("{}"), overwrite: true, Ct);
                (code, revision) = (JournalFindingCodes.ArchiveDiscontinuity, 9);
                break;
            case "beyond-head-contiguous": // AE-6: J4 pre-empts the next revision (R-DR-6)
                var forgedNext = (await AppendStreamAsync(w.Org, stream, 1))[0];
                await raw.GetBlobClient(StreamKey(w.Org, stream, 9)).UploadAsync(BinaryData.FromString("{\"pre-empted\":true}"), overwrite: true, Ct);
                (code, revision) = (JournalFindingCodes.AnchorMismatch, 9);
                _ = forgedNext;
                dplus = "preempted";
                break;
            case "cross-organization": // an archived object of another organization's same stream substituted at this key
                using (var otherApprovers = Approvers())
                {
                    var other = await StreamRigAsync(stream, otherApprovers);
                    await AppendStreamAsync(other.Org, stream, 6);
                    Assert.Equal(6, await other.Processor().ProcessOrganizationAsync(other.Org, Ct));
                    var foreign = (await Wp2Azurite.RawArchiveContainer(other.Container).GetBlobClient(StreamKey(other.Org, stream, 6)).DownloadContentAsync(Ct)).Value.Content;
                    await raw.GetBlobClient(StreamKey(w.Org, stream, 6)).UploadAsync(foreign, overwrite: true, Ct);
                }

                (code, revision) = (JournalFindingCodes.AnchorMismatch, 6);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(attack));
        }

        var run = await w.Verifier.SweepAsync(w.Org, stream, kind, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " " + attack + ": " + Describe(run));
        AssertFinding(run, code, subtype, revision);
        if (attack == "cross-organization")
        {
            AssertFinding(run, JournalFindingCodes.ChainIntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch, 6); // the envelope names the other organization (R-3)
        }

        Assert.False(await QuarantinedAsync(w.Org));

        // A fresh process (empty receipt cache) never verifies an entry whose archive evidence was attacked.
        var receipts = new TrustedJournalReceipts(w.Rig.Archive, Durable(w.Rig.Container));
        await using (var context = AdminContext())
        {
            var affected = attack switch { "anchor-overwritten" => 4L, "object-overwritten" or "cross-organization" => 6L, _ => 8L };
            var entry = await context.JournalEntries.AsNoTracking().SingleAsync(e => e.OrganizationId == w.Org && e.JournalStreamName == stream && e.JournalStreamRevision == affected, Ct);
            var verdict = await receipts.VerifyAsync(entry, Ct);
            var expectVerified = attack is "unexpected-object" or "beyond-head-gap" or "beyond-head-contiguous";
            TestContext.Current.TestOutputHelper?.WriteLine(stream + " " + attack + " receipt@" + affected + ": " + verdict.Code);
            Assert.Equal(expectVerified, verdict.IsVerified);
        }

        if (stream == JournalStreamNames.Authorization && attack is "sentinel-deleted" or "instance-replaced" or "predecessor-deleted")
        {
            Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(w.Rig));
        }

        if (dplus != "none")
        {
            if (dplus != "preempted")
            {
                await AppendStreamAsync(w.Org, stream, 1);
            }

            Assert.Equal(0, await w.Rig.Processor().ProcessOrganizationAsync(w.Org, Ct));
            var row = (await AuditRowsAsync(w.Org, stream)).Single(r => r.Revision == 9);
            TestContext.Current.TestOutputHelper?.WriteLine(stream + " " + attack + " D+: " + row.State + " " + row.Code);
            switch (dplus)
            {
                case "wait": // DD-1: archive-level, re-derived, not marked
                    Assert.Equal(ArchiveDeliveryStates.Pending, row.State);
                    break;
                case "stop": // GP-2: INSTANCE_CHANGED, durable, not recoverable in this increment
                    Assert.Equal(ArchiveDeliveryStates.VerificationFailed, row.State);
                    Assert.Contains("INSTANCE_CHANGED", row.Code, StringComparison.Ordinal);
                    break;
                case "predecessor-missing":
                    Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), (row.State, row.Code));
                    break;
                case "preempted": // the pre-empting object collides with the genuine entry: the existing quarantine (R-DR-6 denial of service)
                    Assert.Equal(ArchiveDeliveryStates.VerificationFailed, row.State);
                    Assert.True(await QuarantinedAsync(w.Org));
                    break;
            }
        }
    }

    // WP-7A (C-160, A-9): the pre-remediation characterization WP7OiDr7ArchivedObjectsBelowTheCheckpointAreNotReReadByTheFullSweep (executed in the WP-7 runs of record) is superseded by
    // WP7AOiDr7TheFullSweepVerifiesEveryHistoricalArchivedObject (WP7A-PG-03); the behaviour it pinned was remediated.

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-05: durable stop records, findings and evidence commitments under attack (J4, J6, J4+J6) per stream
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WP7-PG-05: a durable stop on real stores. Deleting the marker (J4) or the finding (J6) leaves the stream stopped and is reported or
    /// completed; changing the refused revision's commitment (J6) is a stream-stopping evidence change; deleting BOTH stop records and BOTH
    /// commitment copies (J4 + J6 collusion, out of scope) leaves no record and delivery can resume — characterized, not claimed.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7StopRecordsFindingsAndCommitmentsUnderAttackOnEveryStream(string stream)
    {
        Require();
        using var approvers = Approvers();
        var (rig, _) = await StoppedStreamAsync(stream, 3, approvers);
        var archive = Wp2Azurite.RawArchiveContainer(rig.Container);
        var store = Wp2Azurite.RawCheckpointContainer(rig.StoreContainer);
        var markers = await RawAsync(archive, $"meta/dplus-stops/{rig.Org:D}/{stream}/");
        var findings = await RawAsync(store, $"findings/{rig.Org:D}/{stream}/");
        Assert.Single(markers);
        Assert.NotEmpty(findings);

        // J4: marker deleted. Stays stopped; the finding re-creates the marker and reports the removal. Discovery runs on a pass with a pending
        // entry (SM-5), so a later change is committed first.
        await archive.GetBlobClient(markers[0].Name).DeleteAsync(cancellationToken: Ct);
        await AppendStreamAsync(rig.Org, stream, 1);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Single(await RawAsync(archive, $"meta/dplus-stops/{rig.Org:D}/{stream}/"));
        Assert.Contains(await RawAsync(store, $"findings/{rig.Org:D}/{stream}/"), f => Encoding.UTF8.GetString(f.Bytes).Contains(JournalFindingCodes.DPlusStopRecordIncomplete, StringComparison.Ordinal));

        // J6: every finding deleted. Stays stopped; the marker completes the finding.
        foreach (var finding in await RawAsync(store, $"findings/{rig.Org:D}/{stream}/"))
        {
            await store.GetBlobClient(finding.Name).DeleteAsync(cancellationToken: Ct);
        }

        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.NotEmpty(await RawAsync(store, $"findings/{rig.Org:D}/{stream}/"));
        Assert.False(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, 4)).ExistsAsync(Ct));

        // J6: the refused revision's store commitment replaced. Still stopped (the stop records stand); the verifier-side comparison is WP7-PG-08.
        var intent = Assert.Single(await RawAsync(store, $"intents/{rig.Org:D}/{stream}/{Rev(4).PadLeft(12, '0')}"));
        var changed = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(intent.Bytes)! with { RowBodySha256Hex = new string('e', 64) };
        await store.GetBlobClient(intent.Name).UploadAsync(BinaryData.FromBytes(DPlusRecoveryCodec.Serialize(changed)), overwrite: true, Ct);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));

        // J4 + J6 (out of scope, A-3 §23.4): both stop records and both commitment copies removed, archived predecessor restored by J4.
        foreach (var item in (await RawAsync(archive, $"meta/dplus-stops/{rig.Org:D}/{stream}/")).Concat(await RawAsync(archive, $"meta/dplus-intents/{rig.Org:D}/{stream}/{Rev(4).PadLeft(12, '0')}")))
        {
            await archive.GetBlobClient(item.Name).DeleteAsync(cancellationToken: Ct);
        }

        foreach (var item in (await RawAsync(store, $"findings/{rig.Org:D}/{stream}/")).Concat(await RawAsync(store, $"intents/{rig.Org:D}/{stream}/{Rev(4).PadLeft(12, '0')}")))
        {
            await store.GetBlobClient(item.Name).DeleteAsync(cancellationToken: Ct);
        }

        var resumed = await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " J4+J6 collusion (out of scope): delivered " + resumed + "; rows " +
            string.Join(",", (await AuditRowsAsync(rig.Org, stream)).Select(r => r.Revision + ":" + r.State + ":" + r.Code)));
        // The predecessor (revision 3) is still absent, so D+ refuses again from the archive itself (DP-9) — the collusion does not deliver
        // the stopped entry without also restoring revision 3; with no durable record, that refusal is simply re-derived.
        Assert.Equal(0, resumed);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-06: checkpoint-chain corruption and rollback (J6) per stream
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>WP7-PG-06: CKR-1 corruption is found by the full sweep; CKR-3 rollback is found within the process and NOT after a restart (Development/Testing limit).</summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7CheckpointChainCorruptionAndRollbackOnEveryStream(string stream)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        var store = Wp2Azurite.RawCheckpointContainer(w.Rig.StoreContainer);
        var second = await w.Verifier.SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, 2L), (second.Outcome, second.NewCheckpoint!.CheckpointSequence));
        string Name(long sequence) => $"verification/{w.Org:D}/{stream}/{sequence.ToString("D12", CultureInfo.InvariantCulture)}.json";

        // CKR-3: the newest checkpoint deleted. Same process: regression. New process: the older head is accepted (no freshness in Development/Testing).
        var newest = (await store.GetBlobClient(Name(2)).DownloadContentAsync(Ct)).Value.Content;
        await store.GetBlobClient(Name(2)).DeleteAsync(cancellationToken: Ct);
        AssertFinding(await w.Verifier.SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct), JournalFindingCodes.CheckpointStoreRegression, null, null);
        var restarted = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        Assert.True(restarted.Outcome == JournalSweepOutcome.Clean, "CKR-3 limit: after a restart the rollback is not detected: " + Describe(restarted));
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " CKR-3 after restart: " + Describe(restarted) + " (characterized Development/Testing limit)");
        _ = newest;

        // CKR-1: the baseline checkpoint's bytes altered. The full sweep verifies the entire chain.
        var first = (await store.GetBlobClient(Name(1)).DownloadContentAsync(Ct)).Value.Content.ToString();
        await store.GetBlobClient(Name(1)).UploadAsync(BinaryData.FromString(first.Replace("\"verified_revision\":4", "\"verified_revision\":3", StringComparison.Ordinal)), overwrite: true, Ct);
        var full = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Full, Ct);
        AssertFinding(full, JournalFindingCodes.CheckpointChainInvalid, null, null);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-07: accepted-exception manipulation (J6) per stream
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7AcceptedExceptionManipulationIsReportedOnEveryStream(string stream)
    {
        Require();
        using var approvers = Approvers();
        var rig = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(rig.Org, stream, 6);
        Assert.Equal(6, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container);
        await raw.GetBlobClient(StreamKey(rig.Org, stream, 3)).DeleteAsync(cancellationToken: Ct); // a legacy gap, owner-dispositioned below
        var reader = SharedRuntimeReader.Value; // WP-7A: one pool for the run (per-test readers were never disposed)
        var disposition = new JournalBaselineExceptionDisposition("BX-WP7-" + stream.ToUpperInvariant(), "LEGACY_ARCHIVE_GAP", rig.Org, stream, JournalFindingCodes.ArchiveDiscontinuity, null, 3, 3,
            "C-TEST-WP7", DateTimeOffset.UtcNow.AddMinutes(-5));
        var baseline = await Wp7BaselineAsync(rig, reader, dispositions: [disposition]);
        Assert.Equal((nameof(JournalBaselineOutcome.ExceptionAccepted), "Reduced"), (baseline.Outcome, baseline.AssuranceLevel));
        var store = Wp2Azurite.RawCheckpointContainer(rig.StoreContainer);
        var recordName = $"exceptions/{rig.Org:D}/{stream}/{disposition.ExceptionId}.json";
        var record = (await store.GetBlobClient(recordName).DownloadContentAsync(Ct)).Value.Content.ToString();

        var clean = await Wp7Verifier(rig, reader, new RecordingAlerts()).SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
        Assert.True(clean.Outcome == JournalSweepOutcome.Clean, Describe(clean));
        Assert.Single(clean.AcceptedObservations!);

        // (a) the record's scope widened (J6 rewrites it in place): invalid, and the exception is no longer applied.
        await store.GetBlobClient(recordName).UploadAsync(BinaryData.FromString(record.Replace("\"last_revision\":3", "\"last_revision\":5", StringComparison.Ordinal)), overwrite: true, Ct);
        var widened = await Wp7Verifier(rig, reader, new RecordingAlerts()).SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " exception widened: " + Describe(widened));
        Assert.Contains(widened.Findings, f => f.FindingCode is JournalFindingCodes.AcceptedExceptionRecordInvalid or JournalFindingCodes.AcceptedExceptionEvidenceChanged);
        Assert.Contains(widened.Findings, f => f.FindingCode == JournalFindingCodes.ArchiveDiscontinuity); // no longer covered

        // (b) the record restored, then the accepted range's evidence changed (J4 re-creates the missing object): evidence changed.
        await store.GetBlobClient(recordName).UploadAsync(BinaryData.FromString(record), overwrite: true, Ct);
        await raw.GetBlobClient(StreamKey(rig.Org, stream, 3)).UploadAsync(BinaryData.FromString("{\"replanted\":true}"), overwrite: true, Ct);
        AssertFinding(await Wp7Verifier(rig, reader, new RecordingAlerts()).SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct), JournalFindingCodes.AcceptedExceptionEvidenceChanged, null, null);

        // (c) the record deleted: invalid (a checkpoint names it).
        await store.GetBlobClient(recordName).DeleteAsync(cancellationToken: Ct);
        AssertFinding(await Wp7Verifier(rig, reader, new RecordingAlerts()).SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct), JournalFindingCodes.AcceptedExceptionRecordInvalid, null, null);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-08: forged and replayed recovery approvals, generation substitution, termination during execution and evidence replacement
    // ---------------------------------------------------------------------------------------------------------------------------------

    private static async Task<(Guid Id, DPlusRecoveryBinding Binding)> ProposeRestoreAsync(StreamRig rig, byte[] genuine, long anchoredRevision, DPlusStreamRecoveryService? service = null,
        bool createAnchor = true)
    {
        if (createAnchor)
        {
            var checkpoint = DPlusRecordInspection.FirstCheckpoint(rig.Org, rig.Stream, rig.Instance, anchoredRevision, SHA256.HashData(genuine), DateTimeOffset.UtcNow);
            var created = await Durable(rig.Container).Checkpoints.CreateAsync(rig.Org, rig.Stream, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct);
            Assert.True(created.Status is CheckpointCreateStatus.Created or CheckpointCreateStatus.IdenticalExists, created.Status.ToString());
        }

        var proposed = await (service ?? rig.Service()).ProposeAsync(new DPlusRecoveryProposal(rig.Org, rig.Stream, DPlusRecoveryStrategies.RestorePredecessor, "wp7-requester", "wp7 recovery reason",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "wp7-analyst", "wp7 narrative", TimeSpan.FromHours(1), genuine, "wp7"), Ct);
        Assert.True(proposed.Outcome == DPlusRecoveryCommandOutcome.Done, proposed.Code + ": " + proposed.Detail);
        var id = proposed.RecoveryId!.Value;
        var read = await Durable(rig.Container).Records.ReadAsync(DPlusRecoveryNames.StoreRecoveryPrefix + DPlusRecoveryNames.Statement(rig.Org, rig.Stream, id), Ct);
        return (id, SyntheticRecoveryApprovers.Binding(DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(read.Bytes)!, DPlusRecoveryCodec.Sha256Hex(read.Bytes!)));
    }

    /// <summary>
    /// WP7-PG-08a: approvals signed by keys outside the configuration, approvals of another stream's or another recovery's statement, a reused
    /// token and a single genuine approval never authorize; two genuine distinct approvals do.
    /// </summary>
    [Fact]
    public async Task WP7ForgedAndReplayedRecoveryApprovalsNeverAuthorize()
    {
        Require();
        using var approvers = Approvers();
        using var impostors = Approvers(); // same approver ids, different keys (not in the configuration)
        var (content, contentGenuine) = await StoppedStreamAsync(JournalStreamNames.Content, 3, approvers);
        var (authorization, authorizationGenuine) = await StoppedStreamAsync(JournalStreamNames.Authorization, 3, approvers);
        var (contentId, contentBinding) = await ProposeRestoreAsync(content, contentGenuine, 3);
        var (authorizationId, authorizationBinding) = await ProposeRestoreAsync(authorization, authorizationGenuine, 3);
        var now = DateTimeOffset.UtcNow;

        foreach (var forged in new[] { impostors.Sign("approver-a", contentBinding, now), impostors.Sign("approver-b", contentBinding, now) })
        {
            var r = await content.Service().RecordDecisionAsync(content.Org, content.Stream, contentId, forged, Ct);
            TestContext.Current.TestOutputHelper?.WriteLine("forged key: " + r.Outcome + " " + r.Code);
            Assert.NotEqual(DPlusRecoveryCommandOutcome.Done, r.Outcome);
        }

        // Replay across streams and recoveries: a genuine authorization-stream approval presented for the content recovery.
        var other = approvers.Sign("approver-a", authorizationBinding, now);
        var replay = await content.Service().RecordDecisionAsync(content.Org, content.Stream, contentId, other, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine("cross-stream replay: " + replay.Outcome + " " + replay.Code);
        Assert.NotEqual(DPlusRecoveryCommandOutcome.Done, replay.Outcome);

        // One genuine approval, then the same token again (single use, AP-6): still one approval, not authorized.
        var genuineA = approvers.Sign("approver-a", contentBinding, now);
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await content.Service().RecordDecisionAsync(content.Org, content.Stream, contentId, genuineA, Ct)).Outcome);
        await content.Service().RecordDecisionAsync(content.Org, content.Stream, contentId, genuineA, Ct);
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await content.Service().ExecuteAsync(content.Org, content.Stream, contentId, "wp7-executor", Ct)).Code);
        Assert.Equal(0, await content.Processor().ProcessOrganizationAsync(content.Org, Ct));

        // A genuine content approval replayed onto the authorization recovery is not counted there either.
        Assert.NotEqual(DPlusRecoveryCommandOutcome.Done, (await authorization.Service().RecordDecisionAsync(authorization.Org, authorization.Stream, authorizationId, genuineA, Ct)).Outcome);
        Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(authorization));

        // Positive control: a second distinct genuine approver authorizes the content recovery only.
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await content.Service().RecordDecisionAsync(content.Org, content.Stream, contentId, approvers.Sign("approver-b", contentBinding, now), Ct)).Outcome);
        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await content.Service().ExecuteAsync(content.Org, content.Stream, contentId, "wp7-executor", Ct)).Outcome);
        Assert.Equal(1, await content.Processor().ProcessOrganizationAsync(content.Org, Ct));
        Assert.Equal(DPlusRecoveryRefusals.NotAuthorized, (await authorization.Service().ExecuteAsync(authorization.Org, authorization.Stream, authorizationId, "wp7-executor", Ct)).Code);
    }

    /// <summary>A record store whose creates fail for names matching a predicate (termination before that write), every other operation delegated.</summary>
    private sealed class FailingRecordStore(IJournalVerificationRecordStore inner, Func<string, bool> fail) : IJournalVerificationRecordStore
    {
        public Task<CheckpointCreateResult> CreateAsync(string objectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            fail(objectName) ? throw new CheckpointStoreUnavailableException("terminated before " + objectName + " (wp7)") : inner.CreateAsync(objectName, bytes, cancellationToken);

        public Task<StoredObjectRead> ReadAsync(string objectName, CancellationToken cancellationToken) => inner.ReadAsync(objectName, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ListAsync(string prefix, CancellationToken cancellationToken) => inner.ListAsync(prefix, cancellationToken);
    }

    /// <summary>The real Blob archive whose writes fail for a predicate (entry appends or meta names), every other operation delegated.</summary>
    private sealed class FailingWriteArchive(BlobSecurityEventArchive inner, Func<long, bool> failAppend, Func<string, bool> failMeta) : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
    {
        public string ArchiveName => "FailingWrite";

        public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) =>
            failAppend(streamRevision) ? throw new ArchiveUnavailableException("append failed (wp7)") : inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken);

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken);

        public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken) => inner.ReadIdentityAsync(cancellationToken);

        public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => inner.CreateIdentityAsync(identity, cancellationToken);

        public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => inner.ReadEntryAsync(organizationId, streamName, streamRevision, cancellationToken);

        public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            inner.ListEntriesAsync(organizationId, streamName, continuationToken, pageSizeHint, cancellationToken);

        public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            inner.ListEntriesAboveAsync(organizationId, streamName, afterRevision, continuationToken, pageSizeHint, cancellationToken);

        public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => inner.SummarizeAsync(cancellationToken);

        public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken) =>
            inner.CreateStopMarkerAsync(organizationId, streamName, failedRevision, markerBytes, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => inner.ReadStopMarkersAsync(organizationId, streamName, cancellationToken);

        public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => inner.ListMetaObjectNamesAsync(cancellationToken);

        public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            failMeta(metaObjectName) ? throw new ArchiveUnavailableException("meta write failed (wp7)") : inner.CreateMetaAsync(metaObjectName, bytes, cancellationToken);

        public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken) => inner.ReadMetaAsync(metaObjectName, cancellationToken);

        public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken) => inner.ListMetaAsync(metaPrefix, cancellationToken);
    }

    /// <summary>
    /// WP7-PG-08b: termination at each critical transition of an SR-C1 execution (before the predecessor write; after it and before the
    /// execution record) lifts nothing; a re-run resumes idempotently. Evidence replaced after a termination makes the re-run fail (EX-1 step 3);
    /// nothing is lifted and the stream stays stopped. A recovery-generation substitution (execution record replaced in one store) makes the
    /// record contradictory: the lift disappears.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP7TerminationReplacementAndGenerationSubstitutionDuringRecoveryLiftNothing(string stream)
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedStreamAsync(stream, 3, approvers);
        var (id, binding) = await ProposeRestoreAsync(rig, genuine, 3);
        foreach (var approver in Wp7TwoApprovers)
        {
            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign(approver, binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        }

        // (1) terminated before the predecessor write (archive append of revision 3 fails).
        var noRestore = new FailingWriteArchive(rig.Archive, r => r == 3, _ => false);
        var beforeRestore = await Record(() => new DPlusStreamRecoveryService(RuntimeStore(), noRestore, rig.Findings, Durable(rig.Container), TimeProvider.System)
            .ExecuteAsync(rig.Org, stream, id, "wp7-executor", Ct));
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " terminated before the predecessor write: " + beforeRestore);
        Assert.DoesNotContain("Done", beforeRestore, StringComparison.Ordinal);
        Assert.False(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, 3)).ExistsAsync(Ct));
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));

        // (2) terminated after the predecessor write and before the execution record (the last write, EX-1).
        var durable = Durable(rig.Container);
        var noExecution = new DPlusDurability(new FailingRecordStore(durable.Records, n => n.Contains("/executions/", StringComparison.Ordinal)), durable.Provisioning, durable.Checkpoints, durable.Approvals);
        var noExecutionArchive = new FailingWriteArchive(rig.Archive, _ => false, n => n.Contains("/executions/", StringComparison.Ordinal));
        var terminated = await Record(() => new DPlusStreamRecoveryService(RuntimeStore(), noExecutionArchive, rig.Findings, noExecution, TimeProvider.System).ExecuteAsync(rig.Org, stream, id, "wp7-executor", Ct));
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " terminated before the execution record: " + terminated);
        Assert.DoesNotContain("Done", terminated, StringComparison.Ordinal);
        Assert.True(await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, 3)).ExistsAsync(Ct)); // the restore was written
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct)); // nothing is lifted without the execution record
        Assert.Empty(await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), $"meta/dplus-recoveries/{rig.Org:D}/{stream}/executions/"));

        // (3) evidence replaced after the termination (J4 overwrites the restored predecessor): the re-run fails, nothing is lifted.
        var restored = Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(StreamKey(rig.Org, stream, 3));
        await restored.UploadAsync(BinaryData.FromString("{\"replaced\":true}"), overwrite: true, Ct);
        var rerun = await rig.Service().ExecuteAsync(rig.Org, stream, id, "wp7-executor", Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " re-run after evidence replacement: " + rerun.Outcome + " " + rerun.Code + " " + rerun.Detail);
        Assert.NotEqual(DPlusRecoveryCommandOutcome.Done, rerun.Outcome);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));

        // (4) J4 removes the replaced object (the predecessor is absent again); a NEW recovery (the first one is terminally Failed) restores and lifts.
        await restored.DeleteAsync(cancellationToken: Ct);
        var fresh = await ProposeRestoreAsync(rig, genuine, 3, createAnchor: false);
        foreach (var approver in Wp7TwoApprovers)
        {
            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, fresh.Id, approvers.Sign(approver, fresh.Binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        }

        var executed = await rig.Service().ExecuteAsync(rig.Org, stream, fresh.Id, "wp7-executor", Ct);
        Assert.True(executed.Outcome == DPlusRecoveryCommandOutcome.Done, executed.Code + ": " + executed.Detail);
        Assert.Equal(1, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.Equal(genuine, (await restored.DownloadContentAsync(Ct)).Value.Content.ToArray());

        // (5) generation substitution: J4 replaces the resolved generation-0 stop marker with other bytes. The executed recovery no longer covers it
        // (same name, SHA-256, kind and generation required, EX-3): it is an invalid, unresolved marker and the stream stops again.
        var marker = Assert.Single(await RawAsync(Wp2Azurite.RawArchiveContainer(rig.Container), $"meta/dplus-stops/{rig.Org:D}/{stream}/"));
        await Wp2Azurite.RawArchiveContainer(rig.Container).GetBlobClient(marker.Name).UploadAsync(BinaryData.FromBytes([.. marker.Bytes, (byte)' ']), overwrite: true, Ct);
        await AppendStreamAsync(rig.Org, stream, 1);
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.NotEqual(ArchiveDeliveryStates.Anchored, (await AuditRowsAsync(rig.Org, stream))[^1].State);
    }

    private static async Task<string> Record(Func<Task<DPlusRecoveryCommandResult>> action)
    {
        try
        {
            var r = await action();
            return r.Outcome + " " + r.Code;
        }
        catch (Exception ex) when (ex is ArchiveUnavailableException or CheckpointStoreUnavailableException)
        {
            return "terminated: " + ex.Message;
        }
    }

    /// <summary>WP7-PG-08c: no recovery path lifts an organization quarantine (§24.8; RX-2): a quarantined organization's recovery is refused.</summary>
    [Fact]
    public async Task WP7RecoveryNeverLiftsAnOrganizationQuarantineOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedStreamAsync(JournalStreamNames.Authorization, 3, approvers);
        await AdminNoBypassSqlAsync("UPDATE authority.organization_authorization_states SET integrity_quarantine_enabled = true, integrity_quarantine_reason_code = 'ARCHIVE_BODY_MISMATCH' WHERE organization_id = @org",
            rig.Org, rig.Stream);
        var checkpoint = DPlusRecordInspection.FirstCheckpoint(rig.Org, rig.Stream, rig.Instance, 3, SHA256.HashData(genuine), DateTimeOffset.UtcNow);
        await Durable(rig.Container).Checkpoints.CreateAsync(rig.Org, rig.Stream, 1, JournalVerificationCheckpoints.Serialize(checkpoint), Ct);
        var proposed = await rig.Service().ProposeAsync(new DPlusRecoveryProposal(rig.Org, rig.Stream, DPlusRecoveryStrategies.RestorePredecessor, "wp7-requester", "wp7 recovery reason",
            DPlusRecoveryStrategies.PredecessorLostCause, false, null, "wp7-analyst", "wp7 narrative", TimeSpan.FromHours(1), genuine, "wp7"), Ct);
        Assert.Equal(DPlusRecoveryRefusals.OrganizationQuarantined, proposed.Code);
        Assert.True(await QuarantinedAsync(rig.Org));
        Assert.Equal(GateState.Quarantined, await RealGateAsync(rig));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-09: authorization assurance — cached receipts, new heads, instance changes, recovery generations, J2 deletion exposure
    // ---------------------------------------------------------------------------------------------------------------------------------

    private static async Task<GateState> GateAsync(Guid org, IJournalReceiptVerifier receipts)
    {
        var store = new AuthorityStore(new RuntimeContextFactory(Runtime!), ContentAcceptancePolicy.Resolve(null, null), journalReceipts: receipts);
        await using var scope = await store.BeginScopeAsync(Ct);
        var state = (await scope.LockOrganizationAsync(org, Ct))!;
        return await OrganizationGate.EvaluateAsync(scope, state, Ct);
    }

    // WP-7A (C-160, A-9): the pre-remediation characterization WP7CachedReceiptsNeverAnswerANewHeadAndTheirLimitAfterAnExternalChangeIsCharacterized (executed in the WP-7 runs of record) is superseded by
    // WP7AF1ChangedEvidenceOfAnUnchangedHeadIsRefusedPastTheFreshnessBound (WP7A-PG-01); the behaviour it pinned was remediated.

    /// <summary>WP7-PG-09b: after an SR-C1 recovery the recovered entry's receipt is established at the new stop generation (1); the generation-0 commitment written at the refusal is not used.</summary>
    [Theory]
    [MemberData(nameof(WP5BStreams))]
    public async Task WP7AfterRecoveryTheReceiptIsEstablishedAtTheNewGeneration(string stream)
    {
        Require();
        using var approvers = Approvers();
        var (rig, genuine) = await StoppedStreamAsync(stream, 3, approvers);
        var cache = new TrustedJournalReceipts(rig.Archive, Durable(rig.Container));
        var (id, binding) = await ProposeRestoreAsync(rig, genuine, 3);
        foreach (var approver in Wp7TwoApprovers)
        {
            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign(approver, binding, DateTimeOffset.UtcNow), Ct)).Outcome);
        }

        await using (var context = AdminContext())
        {
            var refused = await context.JournalEntries.AsNoTracking().SingleAsync(e => e.OrganizationId == rig.Org && e.JournalStreamName == stream && e.JournalStreamRevision == 4, Ct);
            Assert.False((await cache.VerifyAsync(refused, Ct)).IsVerified); // not delivered: never cached
        }

        Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().ExecuteAsync(rig.Org, stream, id, "wp7-executor", Ct)).Outcome);
        Assert.Equal(1, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        await using (var context = AdminContext())
        {
            var recovered = await context.JournalEntries.AsNoTracking().SingleAsync(e => e.OrganizationId == rig.Org && e.JournalStreamName == stream && e.JournalStreamRevision == 4, Ct);
            var verdict = await cache.VerifyAsync(recovered, Ct);
            TestContext.Current.TestOutputHelper?.WriteLine(stream + " recovered receipt: " + verdict);
            Assert.True(verdict.IsVerified);
            Assert.Equal(1, verdict.StopGeneration);
        }

        Assert.Equal(2, (await RawAsync(Wp2Azurite.RawCheckpointContainer(rig.StoreContainer), $"intents/{rig.Org:D}/{stream}/{Rev(4).PadLeft(12, '0')}")).Count); // gen 0 and gen 1 kept
        if (stream == JournalStreamNames.Authorization)
        {
            Assert.Equal(GateState.Ready, await GateAsync(rig.Org, cache));
        }
    }

    // WP-7A (C-160, A-9): the pre-remediation characterization WP7J2DeletionOfTheAuthorizationHeadExposureWindowIsCharacterized (executed in the WP-7 runs of record) is superseded by
    // WP7AJ2RevocationDeletionPhasesOnTheRealGate (WP7A-PG-04); the behaviour it pinned was remediated.

    /// <summary>
    /// WP7-PG-09d (package §5): concurrent permission changes, two delivery processors and gate evaluations on real stores, then a stop and an
    /// SR-C1 recovery during the same load. Invariant checked on every evaluation: whenever the gate is Ready, the authorization head read in
    /// the same locked scope is archived byte-for-byte (no undelivered change is ever effective).
    /// </summary>
    [Fact]
    public async Task WP7ConcurrentPermissionsDeliveryAndRecoveryIntroduceNoUnauthorizedAccess()
    {
        Require();
        using var approvers = Approvers();
        const string stream = JournalStreamNames.Authorization;
        var rig = await StreamRigAsync(stream, approvers);
        var raw = Wp2Azurite.RawArchiveContainer(rig.Container);
        var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var ready = 0;
        var evaluations = 0;

        async Task CheckAsync(CancellationToken token)
        {
            var store = new AuthorityStore(new RuntimeContextFactory(Runtime!), ContentAcceptancePolicy.Resolve(null, null), journalReceipts: new TrustedJournalReceipts(rig.Archive, Durable(rig.Container)));
            await using var scope = await store.BeginScopeAsync(token);
            var state = (await scope.LockOrganizationAsync(rig.Org, token))!;
            var gate = await OrganizationGate.EvaluateAsync(scope, state, token);
            Interlocked.Increment(ref evaluations);
            if (gate != GateState.Ready)
            {
                return;
            }

            Interlocked.Increment(ref ready);
            var head = await scope.FindLatestJournalEntryAsync(rig.Org, stream, token);
            if (head is null)
            {
                return;
            }

            var blob = raw.GetBlobClient(StreamKey(rig.Org, stream, head.JournalStreamRevision));
            if (!await blob.ExistsAsync(token) || !(await blob.DownloadContentAsync(token)).Value.Content.ToArray().AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(head.JournalEventBodyJson)))
            {
                violations.Enqueue("Ready with an unarchived head " + head.JournalStreamRevision);
            }
        }

        async Task RunAsync(int changes, Func<Task>? midway = null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var evaluators = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await CheckAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                    }
                }
            })).ToList();
            var processors = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await rig.Processor().ProcessOrganizationAsync(rig.Org, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                    }
                }
            })).ToList();
            for (var i = 0; i < changes; i++)
            {
                await AppendStreamAsync(rig.Org, stream, 1);
                if (i == changes / 2 && midway is not null)
                {
                    await midway();
                }

                await Task.Delay(25, Ct);
            }

            await Task.Delay(1500, Ct);
            await cts.CancelAsync();
            await Task.WhenAll(evaluators.Concat(processors).Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }

        await RunAsync(20);
        var head1 = (await AuditRowsAsync(rig.Org, stream))[^1].Revision;

        // A stop under load (J4 deletes the archived head), more changes, then an authorized SR-C1 recovery while the load continues.
        var genuine = (await raw.GetBlobClient(StreamKey(rig.Org, stream, head1)).DownloadContentAsync(Ct)).Value.Content.ToArray();
        await raw.GetBlobClient(StreamKey(rig.Org, stream, head1)).DeleteAsync(cancellationToken: Ct);
        await RunAsync(10, async () =>
        {
            for (var attempt = 0; attempt < 50 && (await RawAsync(raw, $"meta/dplus-stops/{rig.Org:D}/{stream}/")).Count == 0; attempt++)
            {
                await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct);
            }

            var (id, binding) = await ProposeRestoreAsync(rig, genuine, head1);
            foreach (var approver in Wp7TwoApprovers)
            {
                Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().RecordDecisionAsync(rig.Org, stream, id, approvers.Sign(approver, binding, DateTimeOffset.UtcNow), Ct)).Outcome);
            }

            Assert.Equal(DPlusRecoveryCommandOutcome.Done, (await rig.Service().ExecuteAsync(rig.Org, stream, id, "wp7-executor", Ct)).Outcome);
        });
        while (await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct) > 0)
        {
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"evaluations={evaluations} ready={ready} violations={violations.Count} head={(await AuditRowsAsync(rig.Org, stream))[^1].Revision}");
        Assert.Empty(violations);
        Assert.True(evaluations > 20 && ready > 0, "the load exercised both states");
        Assert.All(await AuditRowsAsync(rig.Org, stream), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.Equal(GateState.Ready, await RealGateAsync(rig));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-10: content release (E-7, CR-1..CR-3) on real stores
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WP7-PG-10: a content checkpoint entry on PostgreSQL, delivered by D+ to the Azurite archive, releases exactly its head (positive control).
    /// It never releases another record, another revision, another operation or another manifest root (CR-1); a forged Anchored state written by
    /// the runtime credential (J1) releases nothing (CR-2); an archive outage releases nothing (CR-3); the operation column cannot be repointed by
    /// J1 (G08). An instance replacement withholds release in a fresh process.
    /// </summary>
    [Fact]
    public async Task WP7ContentReleaseIsBoundToTheExactHeadOnRealStores()
    {
        Require();
        using var approvers = Approvers();
        const string stream = JournalStreamNames.Content;
        var rig = await StreamRigAsync(stream, approvers);
        var record = Guid.CreateVersion7();
        var operation = Guid.CreateVersion7();
        var root = RandomNumberGenerator.GetBytes(32);
        SecurityJournalEntry checkpoint;
        await using (var context = AdminContext())
        {
            var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == rig.Org, Ct);
            context.Entry(state).Property(nameof(state.ContentIntegrityRevision)).CurrentValue = 1L;
            checkpoint = JournalChain.Append(state, stream, 1, ContentCheckpointReceipts.CheckpointEventKind, new Wp7CheckpointBody(record, 1, operation, Convert.ToHexStringLower(root)), null, operation, TimeProvider.System);
            context.JournalEntries.Add(checkpoint);
            await context.SaveChangesAsync(Ct);
        }

        RecordContentHead Head(Guid formRecord, long revision, Guid? op, byte[] manifest) => new()
        {
            RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = rig.Org, FormRecordId = formRecord, CommittedContentRevision = revision, CommittedOperationId = op,
            CommittedManifestRoot = manifest, ContentHeadModifiedOn = DateTimeOffset.UtcNow,
        };

        async Task<bool> ReleasedAsync(RecordContentHead head, IJournalReceiptVerifier? receipts = null)
        {
            var store = new AuthorityStore(new RuntimeContextFactory(Runtime!), ContentAcceptancePolicy.Resolve(null, null), journalReceipts: receipts ?? new TrustedJournalReceipts(rig.Archive, Durable(rig.Container)));
            await using var scope = await store.BeginScopeAsync(Ct);
            await using var context = AdminContext();
            var entry = await context.JournalEntries.AsNoTracking().SingleAsync(e => e.SecurityJournalEntryId == checkpoint.SecurityJournalEntryId, Ct);
            return await ContentCheckpointReceipts.IsDeliveredAsync(scope, head, entry, Ct);
        }

        var exact = Head(record, 1, operation, root);
        Assert.True(ContentCheckpointReceipts.Binds(checkpoint, exact));
        Assert.False(await ReleasedAsync(exact)); // not delivered yet

        // J1: the runtime credential forges the undelivered checkpoint's delivery state (the trigger permits Pending -> Anchored with receipt fields).
        Assert.Equal(1, await RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged', " +
            "archive_receipt_digest = decode(repeat('ab', 32), 'hex'), journal_archived_on = now()" + RowWhere + "1", rig.Org, stream));
        Assert.False(await ReleasedAsync(exact)); // CR-2: no trusted receipt
        Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET content_change_operation_id = gen_random_uuid()" + RowWhere + "1", rig.Org, stream)));

        // Genuine delivery path: a fresh organization row for the same body is not possible (forged row is Anchored and final); deliver a second, genuine checkpoint.
        var record2 = Guid.CreateVersion7();
        var operation2 = Guid.CreateVersion7();
        var root2 = RandomNumberGenerator.GetBytes(32);
        await using (var context = AdminContext())
        {
            var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == rig.Org, Ct);
            context.Entry(state).Property(nameof(state.ContentIntegrityRevision)).CurrentValue = 2L;
            checkpoint = JournalChain.Append(state, stream, 2, ContentCheckpointReceipts.CheckpointEventKind, new Wp7CheckpointBody(record2, 1, operation2, Convert.ToHexStringLower(root2)), null, operation2, TimeProvider.System);
            context.JournalEntries.Add(checkpoint);
            await context.SaveChangesAsync(Ct);
        }

        // D+ refuses revision 2: its predecessor (forged revision 1) was never archived. Re-create the scenario cleanly in a second organization.
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));

        var clean = await StreamRigAsync(stream, approvers);
        await using (var context = AdminContext())
        {
            var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == clean.Org, Ct);
            context.Entry(state).Property(nameof(state.ContentIntegrityRevision)).CurrentValue = 1L;
            checkpoint = JournalChain.Append(state, stream, 1, ContentCheckpointReceipts.CheckpointEventKind, new Wp7CheckpointBody(record, 1, operation, Convert.ToHexStringLower(root)), null, operation, TimeProvider.System);
            context.JournalEntries.Add(checkpoint);
            await context.SaveChangesAsync(Ct);
        }

        rig = clean;
        exact = Head(record, 1, operation, root);
        exact.OrganizationId = clean.Org;
        Assert.Equal(1, await clean.Processor().ProcessOrganizationAsync(clean.Org, Ct));
        Assert.True(await ReleasedAsync(exact)); // positive control: the genuine checkpoint releases exactly its head

        foreach (var (label, head) in new[]
                 {
                     ("another record", Head(Guid.CreateVersion7(), 1, operation, root)), ("another revision", Head(record, 2, operation, root)),
                     ("another operation", Head(record, 1, Guid.CreateVersion7(), root)), ("another manifest root", Head(record, 1, operation, RandomNumberGenerator.GetBytes(32))),
                 })
        {
            head.OrganizationId = clean.Org;
            Assert.False(await ReleasedAsync(head), label); // CR-1
        }

        var outage = new BlobSecurityEventArchive(Microsoft.Extensions.Options.Options.Create(Wp2Azurite.Archive(clean.Container, key: Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)))));
        Assert.False(await ReleasedAsync(exact, new TrustedJournalReceipts(outage, Durable(clean.Container)))); // CR-3: unavailable is not released

        var rawClean = Wp2Azurite.RawArchiveContainer(clean.Container);
        await rawClean.GetBlobClient("meta/archive-identity.json").DeleteAsync(cancellationToken: Ct);
        await clean.Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow), Ct);
        Assert.False(await ReleasedAsync(exact)); // a fresh process after an instance replacement withholds release (TR-3)
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // WP7-PG-11: all three streams together; isolation; lag; outages and a killed sweep; statement trace (ADV-13, -15, -16, -17)
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// WP7-PG-11 (ADV-15, ADV-17): two organizations, all three streams delivered together by one processor, baselined and swept incrementally
    /// and fully: Clean everywhere, only SELECTs in read-only snapshots, AuthorityDb unchanged. Then one stream of one organization is tampered:
    /// only that (organization, stream) has findings; every other stream of both organizations stays Clean and keeps delivering.
    /// </summary>
    [Fact]
    public async Task WP7AllThreeStreamsTogetherAreCleanReadOnlyAndIsolatedUnderTampering()
    {
        Require();
        using var approvers = Approvers();
        var rigs = new List<StreamRig>();
        foreach (var _ in new[] { 0, 1 })
        {
            var rig = await StreamRigAsync(JournalStreamNames.Audit, approvers);
            foreach (var stream in Wp7StreamNames)
            {
                await AppendStreamAsync(rig.Org, stream, 5);
            }

            Assert.Equal(15, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
            rigs.Add(rig);
        }

        var statements = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var reader = new JournalVerificationReader(Runtime!, statements.Enqueue);
        var fingerprints = new Dictionary<Guid, string>();
        foreach (var rig in rigs)
        {
            fingerprints[rig.Org] = await FingerprintAsync(rig.Org);
            foreach (var stream in Wp7StreamNames)
            {
                Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await Wp7BaselineAsync(rig, reader, stream)).Outcome);
                var verifier = Wp7Verifier(rig, reader, new RecordingAlerts());
                Assert.Equal(JournalSweepOutcome.Clean, (await verifier.SweepAsync(rig.Org, stream, JournalSweepKind.Incremental, Ct)).Outcome);
                var full = await verifier.SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
                Assert.True(full.Outcome == JournalSweepOutcome.Clean, stream + ": " + Describe(full));
            }

            Assert.Equal(fingerprints[rig.Org], await FingerprintAsync(rig.Org)); // ADV-17: no AuthorityDb write by baseline or sweeps
        }

        Assert.NotEmpty(statements);
        Assert.All(statements, s => Assert.True(s.StartsWith("SET TRANSACTION READ ONLY", StringComparison.Ordinal) || s.StartsWith("SELECT ", StringComparison.Ordinal), s));

        // Tamper (J2) organization 0's content stream only.
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC')" + RowWhere + "3",
            rigs[0].Org, JournalStreamNames.Content);
        foreach (var rig in rigs)
        {
            foreach (var stream in Wp7StreamNames)
            {
                var run = await Wp7Verifier(rig, reader, new RecordingAlerts()).SweepAsync(rig.Org, stream, JournalSweepKind.Full, Ct);
                var tampered = rig == rigs[0] && stream == JournalStreamNames.Content;
                Assert.True(tampered ? run.Outcome == JournalSweepOutcome.Findings : run.Outcome == JournalSweepOutcome.Clean, rig.Org + " " + stream + ": " + Describe(run));
            }

            foreach (var stream in Wp7StreamNames)
            {
                await AppendStreamAsync(rig.Org, stream, 1);
            }

            Assert.Equal(3, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct)); // delivery of every stream continues (the tampered row is below the head)
            Assert.False(await QuarantinedAsync(rig.Org));
        }
    }

    /// <summary>WP7-PG-12 (ADV-16): rows pending beyond the lag threshold are an operational DELIVERY_LAG only, on every stream; no security finding, no quarantine.</summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7DeliveryLagIsOperationalOnlyOnEveryStream(string stream)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        await AppendStreamAsync(w.Org, stream, 2); // never delivered (the work loop "stopped")
        var later = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(20));
        var run = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts(), later).SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(stream + " lag: " + Describe(run));
        Assert.Contains(run.Findings, f => f.FindingCode == JournalFindingCodes.DeliveryLag && f.FirstRevision == 9 && f.LastRevision == 10);
        Assert.DoesNotContain(run.Findings, f => f.Severity == JournalFindingSeverity.Security);
        Assert.False(await QuarantinedAsync(w.Org));
    }

    /// <summary>
    /// WP7-PG-13 (ADV-13): an archive outage and a checkpoint-store outage (credential rejected) are Inconclusive with no checkpoint; a sweep
    /// killed mid-way (cancelled after its first row batch) writes nothing; a restarted verifier completes and advances the chain exactly once.
    /// </summary>
    [Theory]
    [MemberData(nameof(WP7Streams))]
    public async Task WP7OutagesAndAKilledSweepAreInconclusiveAndResumeOnEveryStream(string stream)
    {
        Require();
        using var approvers = Approvers();
        var w = await Wp7RigAsync(stream, approvers);
        var badKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var deadArchive = new BlobSecurityEventArchive(Microsoft.Extensions.Options.Options.Create(Wp2Azurite.Archive(w.Rig.Container, key: badKey)));
        var archiveDown = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts(), archive: deadArchive).SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Inconclusive, (JournalCheckpointReference?)null), (archiveDown.Outcome, archiveDown.NewCheckpoint));
        var deadStore = new BlobVerificationCheckpointStore(Microsoft.Extensions.Options.Options.Create(new VerificationCheckpointStoreOptions
        {
            Provider = "Blob", BlobServiceUri = Wp2Azurite.CheckpointUri, ContainerName = w.Rig.StoreContainer, AccountName = Wp2Azurite.CheckpointAccount, AccountKey = badKey, TimeoutSeconds = 3,
        }), Microsoft.Extensions.Options.Options.Create(Wp2Azurite.Archive("security-journal")));
        var storeDown = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts(), checkpoints: deadStore).SweepAsync(w.Org, stream, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Inconclusive, (JournalCheckpointReference?)null), (storeDown.Outcome, storeDown.NewCheckpoint));

        using var kill = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var killingReader = new JournalVerificationReader(Runtime!, s =>
        {
            if (s.Contains("FROM authority.security_journal_entries", StringComparison.Ordinal) && s.Contains("journal_stream_revision BETWEEN", StringComparison.Ordinal))
            {
                kill.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wp7Verifier(w.Rig, killingReader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Full, kill.Token));
        var head = await JournalVerificationCheckpoints.DiscoverAsync(Wp2Azurite.NewCheckpointStore(w.Rig.StoreContainer), w.Org, stream, CheckpointDiscoveryMode.FullChain, 0, 100, Ct);
        Assert.Equal(1L, head.Head!.CheckpointSequence); // nothing written by the killed sweep
        var resumed = await Wp7Verifier(w.Rig, w.Reader, new RecordingAlerts()).SweepAsync(w.Org, stream, JournalSweepKind.Full, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, 2L, 8L), (resumed.Outcome, resumed.NewCheckpoint!.CheckpointSequence, resumed.NewCheckpoint.VerifiedRevision));
    }
}
