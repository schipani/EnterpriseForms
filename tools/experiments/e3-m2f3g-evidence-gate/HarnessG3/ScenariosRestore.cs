// E3-M2f-3G group G (M2F3-55, M2F3-57, M2F3-84): restore-triggered G14 DETECTION observed through the unchanged archive processor and
// the production InMemorySecurityEventArchive shared by the live database and its restore copy (a database copied before the live
// evidence). No recovery mechanism is implemented or simulated: BD-1, BD-2 and G14 recovery stay open; the restore gap is reported as
// EXPECTED_GAP_RESTORE_EVIDENCE outside the pass totals. Group W (M2F3-28 without binding inserts): T2 under floor activation and
// next to Release B level-2 writers.
using System.Diagnostics;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;

internal sealed partial class Lab
{
    private async Task<string> ArchiveAsync(string db, Org org, InMemorySecurityEventArchive archive, string application)
    {
        var processor = new ArchiveProcessor(Store(db, application), archive, TimeProvider.System);
        var notes = new List<string>();
        for (var pass = 0; pass < 20; pass++)
        {
            try
            {
                if (await processor.ProcessOrganizationAsync(org.Id, CancellationToken.None) == 0)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                notes.Add(Describe(ex));
                break;
            }
        }

        return notes.Count == 0 ? "ok" : string.Join(" | ", notes);
    }

    private async Task<string> AuditDeliveryAsync(string db, Org org) =>
        await ScalarAsync(db, $"SELECT coalesce(string_agg(journal_stream_revision || ':' || (journal_event_body_json::jsonb->'body'->>'entry_point') || ':' || archive_delivery_state || ':' || coalesce(last_archive_error_code, '-'), ',' ORDER BY journal_stream_revision), 'none') FROM authority.security_journal_entries WHERE organization_id = '{org.Id:D}' AND journal_stream_name = 'audit'");

    private async Task RestoreScenariosAsync()
    {
        CurrentGroup = "G";

        // G-01..G-04 (M2F3-55, M2F3-57).
        {
            var live = await T2DatabaseAsync("g3_g_live");
            var gA = await SeedOrgAsync(live);
            var gB = await SeedOrgAsync(live);
            var gC = await SeedOrgAsync(live);
            const string restored = "g3_g_t0";
            await CreateDatabaseAsync(restored, live); // the restore point: before any evidence
            var archive = new InMemorySecurityEventArchive();

            var nodeLive = Recorder(live, "g3g-live");
            var la = await nodeLive.Recorder.RecordAsync(Refusal(gA), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var lb = await nodeLive.Recorder.RecordAsync(Refusal(gB), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var archivedLive = await ArchiveAsync(live, gA, archive, "g3g-arch-live") + "/" + await ArchiveAsync(live, gB, archive, "g3g-arch-live");
            var liveA = await AuditDeliveryAsync(live, gA);
            var liveBody = await ScalarAsync(live, $"SELECT journal_event_body_json FROM authority.security_journal_entries WHERE organization_id = '{gA.Id:D}' AND journal_stream_name = 'audit' AND journal_stream_revision = 1");
            Record("G-01", "Restore", "live database: evidence recorded for two organizations and anchored by the unchanged archive processor (production InMemorySecurityEventArchive)",
                "Committed x2; gA 1:verify:Anchored", $"{la}, {lb}; archive {archivedLive}; gA {liveA}", la == CapabilityRefusalEvidenceOutcome.Committed && lb == CapabilityRefusalEvidenceOutcome.Committed && liveA == "1:verify:Anchored:-");

            // The "restore": the copy taken before the evidence now serves. A post-restore refusal of gA appends at the same revision.
            var nodeRestored = Recorder(restored, "g3g-restored");
            var ra = await nodeRestored.Recorder.RecordAsync(Refusal(gA), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None);
            var rc = await nodeRestored.Recorder.RecordAsync(Refusal(gC), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var afterT2 = await RevisionsAsync(restored, gA.Id);
            Record("G-02", "Restore", "M2F3-55: T2 never quarantines, not even in a restored database about to collide: after the post-restore append gA is not quarantined by T2",
                "Committed; gA quarantine none", $"{ra}; {afterT2}", ra == CapabilityRefusalEvidenceOutcome.Committed && afterT2.EndsWith("quarantine none", StringComparison.Ordinal));

            var archA = await ArchiveAsync(restored, gA, archive, "g3g-arch-restored");
            var archB = await ArchiveAsync(restored, gB, archive, "g3g-arch-restored");
            var archC = await ArchiveAsync(restored, gC, archive, "g3g-arch-restored");
            var qA = await RevisionsAsync(restored, gA.Id);
            var qB = await RevisionsAsync(restored, gB.Id);
            var qC = await RevisionsAsync(restored, gC.Id);
            var restoredA = await AuditDeliveryAsync(restored, gA);
            var restoredC = await AuditDeliveryAsync(restored, gC);
            var archivedA = await archive.ReadAsync(gA.Id, "audit", 1, CancellationToken.None);
            var archiveIntact = archivedA is not null && System.Text.Encoding.UTF8.GetString(archivedA) == liveBody;
            var liveUnchanged = await AuditDeliveryAsync(live, gA) == liveA;
            Record("G-03", "Restore", "M2F3-57 (RB-a): the archive processor finds the post-restore entry colliding with the archived pre-restore entry at the same revision and quarantines THAT organization (ARCHIVE_BODY_MISMATCH); an organization whose post-restore evidence does not collide (gC) is anchored normally; an organization without post-restore evidence (gB) is untouched; the archived pre-restore evidence is intact; the post-restore entry is retained (not deleted); the live database is unchanged; nothing is cleared",
                "gA quarantine ARCHIVE_BODY_MISMATCH, entry 1:prepare retained with ARCHIVE_BODY_MISMATCH; gB none; gC none and 1:verify:Anchored; archive intact; live unchanged",
                $"gA {qA}; gA audit {restoredA}; gB {qB}; gC {qC}; gC audit {restoredC}; archive intact {archiveIntact}; live unchanged {liveUnchanged}; processor {archA} / {archB} / {archC}",
                qA.EndsWith("quarantine ARCHIVE_BODY_MISMATCH", StringComparison.Ordinal) && restoredA.StartsWith("1:prepare:", StringComparison.Ordinal) && restoredA.Contains("ARCHIVE_BODY_MISMATCH", StringComparison.Ordinal)
                && qB.EndsWith("quarantine none", StringComparison.Ordinal) && qC.EndsWith("quarantine none", StringComparison.Ordinal) && restoredC == "1:verify:Anchored:-" && archiveIntact && liveUnchanged && rc == CapabilityRefusalEvidenceOutcome.Committed);
            Demonstrate("G-04", "EXPECTED_GAP_RESTORE_EVIDENCE", "M2F3-57 boundary: the restore lost the live evidence of gA and gB (the restored database does not have them); the loss is detected only when a post-restore append collides in the archive (gA); gB's lost entry is not detected by this path; no recovery mechanism exists (BD-1, BD-2 and G14 recovery stay open)",
                "restored gA has no verify entry; restored gB has no entry; gB not quarantined", $"restored gA {restoredA}; restored gB {await AuditDeliveryAsync(restored, gB)}; gB {qB}",
                !restoredA.Contains(":verify:", StringComparison.Ordinal) && await AuditDeliveryAsync(restored, gB) == "none" && qB.EndsWith("quarantine none", StringComparison.Ordinal));
        }

        // G-05 (M2F3-84): restore from a point before IX-B existed.
        {
            var live = await T2DatabaseAsync("g3_g_live2", NoIndex);
            var gD = await SeedOrgAsync(live);
            const string restored = "g3_g_t0b";
            await CreateDatabaseAsync(restored, live); // before IX-B
            var archive = new InMemorySecurityEventArchive();
            var provisionLive = await ProvisionIndexAsync(live, "g-live2");
            var la = await Recorder(live, "g3g5-live").Recorder.RecordAsync(Refusal(gD), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var archivedLive = await ArchiveAsync(live, gD, archive, "g3g5-arch-live");
            var catalogueBefore = await LinesAsync(restored, "SELECT c.relname || ':' || c.relkind::text FROM pg_class c WHERE c.relnamespace = 'authority'::regnamespace ORDER BY 1");
            var nodeR = Recorder(restored, "g3g5-restored");
            var r1 = await nodeR.Recorder.RecordAsync(Refusal(gD), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None);
            var reason1 = nodeR.Outcomes.All.Last().Reason;
            var during = await EvidenceCountAsync(restored, gD.Id);
            var archiveDuring = await ArchiveAsync(restored, gD, archive, "g3g5-arch-restored");
            var qDuring = await RevisionsAsync(restored, gD.Id);
            var provision = await ProvisionIndexAsync(restored, "g-restored");
            var catalogueAfter = await LinesAsync(restored, "SELECT c.relname || ':' || c.relkind::text FROM pg_class c WHERE c.relnamespace = 'authority'::regnamespace ORDER BY 1");
            var r2 = await Recorder(restored, "g3g5-restored-2").Recorder.RecordAsync(Refusal(gD), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None);
            var archiveAfter = await ArchiveAsync(restored, gD, archive, "g3g5-arch-restored-2");
            var qAfter = await RevisionsAsync(restored, gD.Id);
            var added = catalogueAfter.Except(catalogueBefore).ToList();
            Record("G-05", "Restore", "M2F3-84: a restore copy from before IX-B existed: evidence recording stops (Deferred INDEX_UNAVAILABLE, nothing appended, nothing to collide, no quarantine); re-running the accepted command restores recording; the next post-restore entry collides with the archived live entry and the archive processor quarantines the organization exactly as in M2F3-57; the command added exactly IX-B and nothing else",
                "live: command 0, Committed, archived; restored: Deferred IndexUnavailable, evidence 0, quarantine none; command 0; added [ix_journal_capability_refusal_evidence:i]; Committed; quarantine ARCHIVE_BODY_MISMATCH",
                $"live: command {provisionLive}, {la}, archive {archivedLive}; restored: {r1} {reason1}, evidence {during}, archive {archiveDuring}, {qDuring}; command {provision}; added [{string.Join(",", added)}]; {r2}; archive {archiveAfter}; {qAfter}",
                provisionLive.ExitCode == 0 && la == CapabilityRefusalEvidenceOutcome.Committed && r1 == CapabilityRefusalEvidenceOutcome.Deferred && reason1 == CapabilityRefusalEvidenceReason.IndexUnavailable && during == 0
                && qDuring.EndsWith("quarantine none", StringComparison.Ordinal) && provision.ExitCode == 0 && added.SequenceEqual(["ix_journal_capability_refusal_evidence:i"]) && catalogueBefore.Except(catalogueAfter).Any() == false
                && r2 == CapabilityRefusalEvidenceOutcome.Committed && qAfter.EndsWith("quarantine ARCHIVE_BODY_MISMATCH", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------------------------------------ group W (M2F3-28 without binding inserts)
    private async Task WriterScenariosAsync()
    {
        CurrentGroup = "W";
        NotExecuted("M2F3-28 (binding-inserting Prepares)", "a Prepare that inserts a data-key binding needs a bound organization and an Envelope v2 write; both are outside the E3-M2f-3G authorization (separate owner approval required, as OPEN-M2f-4)");
        var db = await T2DatabaseAsync("g3_w1", PreFloor);
        var provision = await ProvisionIndexAsync(db, "w-prefloor");
        var orgs = new List<Org>();
        for (var i = 0; i < 4; i++)
        {
            orgs.Add(await SeedOrgAsync(db));
        }

        // W-01: floor activation (A-1) while T2s run continuously on every organization.
        {
            using var stop = new CancellationTokenSource();
            var errors = new List<string>();
            var flood = Task.Run(async () =>
            {
                var round = 0;
                while (!stop.IsCancellationRequested)
                {
                    var node = Recorder(db, "g3w01-" + (round++ % 3)); // a new process (empty pre-filter) each second, three pools
                    var tasks = orgs.SelectMany(o => Seven.Select(e => node.Recorder.RecordAsync(Refusal(o), e, CancellationToken.None))).ToList();
                    foreach (var o in await Task.WhenAll(tasks))
                    {
                        if (o is not (CapabilityRefusalEvidenceOutcome.Committed or CapabilityRefusalEvidenceOutcome.AlreadyRecorded or CapabilityRefusalEvidenceOutcome.SuppressedInFlight or CapabilityRefusalEvidenceOutcome.SuppressedRecorded))
                        {
                            lock (errors)
                            {
                                errors.Add(o.ToString());
                            }
                        }
                    }

                    await Task.Delay(1000);
                }
            });
            await Task.Delay(1500);
            var floors = new List<string>();
            foreach (var f in new[] { "1", "2", "3" })
            {
                floors.Add((await AuthorityCommandAsync(db, "g3w-floor" + f, "--apply-content-acceptance-floor", f)).ToString());
            }

            await Task.Delay(1500);
            stop.Cancel();
            await flood;
            var (holds, detail) = await FleetOracleAsync(db, orgs);
            Record("W-01", "Safety", "M2F3-28 (A-1): floors 1 -> 2 -> 3 are applied (migration identity) while T2s run continuously on four organizations: every floor step succeeds, no T2 fails or waits out, each organization records its seven keys once; the fleet oracle holds (the deadlock count is in the gate summary)",
                "index command 0; floors exit 0 x3; current floor 3; T2 outcomes only Committed/AlreadyRecorded/Suppressed*; fleet oracle holds with 28 entries", $"index command {provision}; floors {string.Join(",", floors)}; unexpected T2 outcomes [{string.Join(",", errors.Distinct())}]; floors now {await FloorsAsync(db)}; {detail}",
                provision.ExitCode == 0 && floors.All(f => f == "exit 0") && errors.Count == 0 && await FloorsAsync(db) == "3" && holds && detail.Contains("evidence 28;", StringComparison.Ordinal));
        }

        // W-02: Release B level-2 writers (in-process content service: D = 2, Release B 3, acceptance off, v1 fields only) on the same
        // organizations while T1 refusals and T2s run on them.
        {
            using var stop = new CancellationTokenSource();
            var writes = new System.Collections.Concurrent.ConcurrentBag<string>();
            var refusals = new System.Collections.Concurrent.ConcurrentBag<string>();
            var unexpected = new System.Collections.Concurrent.ConcurrentBag<string>();
            var writers = orgs.SelectMany(o => o.Records).Select((r, i) => Task.Run(async () =>
            {
                var service = Service(db, "g3w02-writer-" + (i % 4));
                while (!stop.IsCancellationRequested)
                {
                    writes.Add(await WriteV1Async(service, r));
                }
            })).ToList();
            var refusers = orgs.Select((o, i) => Task.Run(async () =>
            {
                var service = Service(db, "g3w02-t1-" + i);
                while (!stop.IsCancellationRequested)
                {
                    var node = Recorder(db, "g3w02-t2-" + i);
                    var (refusal, outcome, _) = await RefusePrepareInProcessAsync(service, node, o.Records[0]);
                    refusals.Add(refusal);
                    if (outcome is not (CapabilityRefusalEvidenceOutcome.Committed or CapabilityRefusalEvidenceOutcome.AlreadyRecorded))
                    {
                        unexpected.Add(outcome.ToString());
                    }

                    foreach (var e in Seven)
                    {
                        var x = await node.Recorder.RecordAsync(Refusal(o), e, CancellationToken.None);
                        if (x is not (CapabilityRefusalEvidenceOutcome.Committed or CapabilityRefusalEvidenceOutcome.AlreadyRecorded or CapabilityRefusalEvidenceOutcome.SuppressedRecorded))
                        {
                            unexpected.Add(x.ToString());
                        }
                    }
                }
            })).ToList();
            await Task.Delay(TimeSpan.FromSeconds(15));
            stop.Cancel();
            await Task.WhenAll(writers.Concat(refusers));
            var heads = await ScalarAsync(db, $"SELECT string_agg(committed_content_revision::text, ',' ORDER BY form_record_id) FROM authority.record_content_heads WHERE organization_id IN ({string.Join(",", orgs.Select(o => $"'{o.Id:D}'"))})");
            var (holds, detail) = await FleetOracleAsync(db, orgs);
            var failedWrites = writes.Where(w => w != "OK").GroupBy(w => w).Select(g => g.Key + "x" + g.Count()).ToList();
            Record("W-02", "Safety", "M2F3-28 (Release B writers): for 15 s, eight level-2 writers (v1 Prepare + Commit each, Release B floor 3, D = 2) and four refusal loops (level-1 T1 refusals followed by T2s on all seven keys) run on the same four organizations: every write commits, every refusal is the 409, no T2 fails, no evidence beyond seven per organization; the fleet oracle holds",
                "writes all OK (> 0); refusals all 409; unexpected T2 outcomes none; fleet oracle holds with 28 entries",
                $"writes {writes.Count} ({(failedWrites.Count == 0 ? "all OK" : string.Join(",", failedWrites))}); content heads [{heads}]; refusals {refusals.Count} ({refusals.Distinct().Count()} distinct: {string.Join(" | ", refusals.Distinct().Select(r => Short(r, 80)))}); unexpected [{string.Join(",", unexpected.Distinct())}]; {detail}",
                writes.Count > 0 && failedWrites.Count == 0 && refusals.Count > 0 && refusals.All(r => r.Contains("CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT 409", StringComparison.Ordinal)) && unexpected.IsEmpty && holds && detail.Contains("evidence 28;", StringComparison.Ordinal));
        }
    }
}
