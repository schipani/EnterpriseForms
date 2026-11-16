// E3-M2e-2 recovery-gap evidence (closure §6.2 item 10; owner authorization §8). Nothing here implements BD-1 or BD-2:
// - M2-RST-06: the ledger is reconstructed as a deterministic function of the ARCHIVED ContentHeadCommitted bodies (in-memory archive)
//   and compared with the original ledger; nothing is inserted anywhere.
// - BD-2 is evidenced by read-only queries of a disposable restore copy (T0) only.
// - BD-1 is demonstrated ONLY in the disposable restore copy: with acceptance on, a lost identifier is accepted again. That outcome is
//   recorded as EXPECTED_GAP_BD1 and never counted as a successful security invariant.
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;

internal sealed partial class Lab
{
    private const string RestoreCopyDatabase = "m2e2_t0";

    private static List<BindingRow> Reconstruct(IEnumerable<(long Revision, byte[] Body)> bodies, Guid org)
    {
        var tuples = new List<BindingRow>();
        foreach (var (_, bytes) in bodies.OrderBy(b => b.Revision))
        {
            var envelope = JsonDocument.Parse(bytes).RootElement;
            if (Prop(envelope, "event_kind").GetString() != "ContentHeadCommitted")
            {
                continue;
            }

            var body = Prop(envelope, "body");
            if (body.TryGetProperty("new_data_key_bindings", out _) || body.TryGetProperty("NewDataKeyBindings", out _))
            {
                tuples.AddRange(TuplesOf(body, org));
            }
        }

        return tuples;
    }

    /// <summary>The archive chain check alone: contiguous revisions from 1 and each envelope's previous digest equal to the prior body's digest.</summary>
    private static (bool Complete, string Detail) ChainCheck(IReadOnlyList<(long Revision, byte[] Body)> bodies)
    {
        var ordered = bodies.OrderBy(b => b.Revision).ToList();
        string? previous = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Revision != i + 1)
            {
                return (false, $"revision gap before r{ordered[i].Revision}");
            }

            var envelope = JsonDocument.Parse(ordered[i].Body).RootElement;
            var prev = Prop(envelope, "previous_digest_hex");
            var prevHex = prev.ValueKind == JsonValueKind.Null ? null : prev.GetString();
            if (prevHex != previous)
            {
                return (false, $"previous digest mismatch at r{ordered[i].Revision}");
            }

            previous = Convert.ToHexStringLower(CanonicalJson.Digest(ordered[i].Body));
        }

        return (true, $"{ordered.Count} bodies contiguous and chained");
    }

    private async Task RecoveryScenariosAsync()
    {
        CurrentGroup = "R";
        var rOrg = await SeedOrgAsync(12, localKeys!, 1);

        // Before the restore point: three accepted New groups.
        var pre = new List<string>();
        foreach (var i in new[] { 0, 1, 2 })
        {
            var r = rOrg.Records[i];
            var items = i == 1 ? new List<object> { await DocumentAsync(r, 2048) } : [(await BatchAsync(r)).Seal("pre-" + i), (await BatchAsync(r)).Seal("pre-b-" + i)];
            pre.Add(await WriteAsync(acc, r, Owner(r, [.. items])));
        }

        // T0: the disposable restore copy (CREATE DATABASE ... TEMPLATE). Every other connection to the primary is closed first.
        await CopyDatabaseAsync(PrimaryDatabase, RestoreCopyDatabase);
        var bd1Record = rOrg.Records[3];
        var t0Shadow = new Rec(rOrg, bd1Record.Id); // its head at T0: empty

        // After T0: committed groups K1..K3, and three reservations that never commit (Prepared, aborted by switch-off, expired).
        var k1Batch = await BatchAsync(bd1Record);
        var k1 = await WriteAsync(acc, bd1Record, Owner(bd1Record, k1Batch.Seal("k1-a"), k1Batch.Seal("k1-b")));
        var k1Staged = bd1Record.Accepted.Where(a => a.Row.Source is FieldEncryptedValue { EncryptionEnvelopeVersion: 2 }).Select(a => a.Row).ToList();
        var k1Operation = Guid.Parse(await ScalarAsync(adminCs, $"SELECT committed_operation_id FROM authority.record_content_heads WHERE form_record_id = '{bd1Record.Id}'"));
        var k1Id = ((FieldEncryptedValue)bd1Record.Items[0]).DataKeyIdentifier!.Value;
        var k2 = await WriteAsync(acc, rOrg.Records[4], Owner(rOrg.Records[4], await DocumentAsync(rOrg.Records[4], 900)));
        var k3 = await WriteAsync(acc, rOrg.Records[0], Owner(rOrg.Records[0], [.. rOrg.Records[0].Items, (await BatchAsync(rOrg.Records[0])).Seal("k3")]));
        var prepared = await PrepareAsync(acc, rOrg.Records[5], Owner(rOrg.Records[5], (await BatchAsync(rOrg.Records[5])).Seal("reserved-prepared")));
        var toAbort = await PrepareAsync(acc, rOrg.Records[6], Owner(rOrg.Records[6], (await BatchAsync(rOrg.Records[6])).Seal("reserved-aborted")));
        var aborted = await CommitAsync(Instance("OFF-R", ReleaseB3, EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseB3)), rOrg.Records[6], toAbort);
        var toExpire = await PrepareAsync(acc, rOrg.Records[7], Owner(rOrg.Records[7], (await BatchAsync(rOrg.Records[7])).Seal("reserved-expired")));
        var expired = await CommitAsync(Instance("LATE-R", ReleaseB3, acceptance, clock: new OffsetClock(TimeSpan.FromMinutes(11))), rOrg.Records[7], toExpire);
        Record("R-00", "Recovery", "setup: 3 groups accepted before T0; T0 restore copy; K1..K3 accepted after T0; three reservations that never commit (Prepared, aborted at Commit by the switch, expired)",
            "OK x3; OK x3; Prepared OK; aborted NOT_ENABLED; expired OPERATION_EXPIRED",
            $"pre {string.Join(",", pre)}; post {k1},{k2},{k3}; prepared {prepared.Outcome}; aborted {aborted.Message}; expired {expired.Outcome}",
            pre.All(p => p == "OK") && k1 == "OK" && k2 == "OK" && k3 == "OK" && prepared.Outcome == "OK" && aborted.Message == NotEnabled && expired.Outcome.Contains("OPERATION_EXPIRED", StringComparison.Ordinal));

        // M2-RST-06: reconstruction from the archive equals the committed ledger, column for column.
        var verdict = await VerifyArchivedContentStreamAsync(rOrg, PrimaryDatabase);
        var reconstructed = Reconstruct(verdict.Bodies, rOrg.Id);
        var ledgerAll = await BindingRowsAsync($"organization_id = '{rOrg.Id}'");
        var states = (await RowsAsync(adminCs, $"SELECT content_change_operation_id, operation_state_name FROM authority.content_change_operations WHERE organization_id = '{rOrg.Id}'")).ToDictionary(r => Guid.Parse(r[0]), r => r[1]);
        var committed = ledgerAll.Where(b => states.GetValueOrDefault(b.Operation) == "Committed").ToList();
        var uncommitted = ledgerAll.Where(b => states.GetValueOrDefault(b.Operation) != "Committed").ToList();
        Record("R-01", "Recovery", "M2-RST-06: the ledger rebuilt deterministically from the ARCHIVED ContentHeadCommitted bodies (chain verified) equals every binding of a committed operation, column for column (identifier, binding id, organization, record, kind, commitment, count, operation, bound-on)",
            "chain verified; reconstructed = committed ledger (8 rows: 5 groups before T0, K1, K2, K3 after)", $"{verdict.Summary}; reconstructed {reconstructed.Count}; committed ledger {committed.Count}; equal {SameRows(reconstructed, committed)}",
            verdict.ChainValid && verdict.AllArchived && committed.Count == 8 && SameRows(reconstructed, committed));
        Demonstrate("R-02", "EXPECTED_LIMIT", "unrecoverable reservations (OD-M2e-03, accepted loss): bindings of operations that never committed are in the live ledger but in no archived body, so no reconstruction can restore them",
            "3 uncommitted reservations (Prepared, Aborted x2), none reconstructible",
            $"uncommitted {uncommitted.Count} [{string.Join(", ", uncommitted.Select(b => states.GetValueOrDefault(b.Operation)))}]; in reconstruction {uncommitted.Count(u => reconstructed.Any(t => t.BindingId == u.BindingId))}",
            uncommitted.Count == 3 && uncommitted.All(u => reconstructed.All(t => t.BindingId != u.BindingId)));

        // BD-2 (read-only, T0 copy): the operations that the post-T0 bindings reference are missing, and P-1 admits only Prepared operations.
        {
            var t0Ops = (await RowsAsync(AdminFor(RestoreCopyDatabase), $"SELECT content_change_operation_id FROM authority.content_change_operations WHERE organization_id = '{rOrg.Id}'")).Select(r => Guid.Parse(r[0])).ToHashSet();
            var t0Ledger = await BindingRowsAsync($"organization_id = '{rOrg.Id}'", RestoreCopyDatabase);
            var missingOps = reconstructed.Where(t => !t0Ops.Contains(t.Operation)).ToList();
            var missingBindings = reconstructed.Where(t => t0Ledger.All(l => l.BindingId != t.BindingId)).ToList();
            var guard = await ScalarAsync(AdminFor(RestoreCopyDatabase), "SELECT prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'authority' AND p.proname = 'content_data_key_binding_guard'");
            Demonstrate("R-03", "EXPECTED_LIMIT", "BD-2 (read-only, restore copy T0): the post-T0 bindings are missing from the restored ledger AND the committed operations they reference are missing (FK RESTRICT); P-1 admits a binding only for a Prepared operation, so re-inserting a reconstructed tuple would need a recovery-plane path (not built)",
                "3 tuples missing (K1, K2, K3); their 3 operations missing; the guard requires Prepared",
                $"missing bindings {missingBindings.Count}; missing operations {missingOps.Select(m => m.Operation).Distinct().Count()} (for {missingOps.Count} tuples); guard requires Prepared {guard.Contains("Prepared", StringComparison.Ordinal)}; nothing inserted",
                missingBindings.Count == 3 && missingOps.Count == 3 && guard.Contains("Prepared", StringComparison.Ordinal));
        }

        // Archive completeness limit: a reconstruction from an archive with one body missing silently lacks its tuples; only the chain shows it.
        {
            var k1Body = verdict.Bodies.Single(b => JsonDocument.Parse(b.Body).RootElement is var e && Prop(e, "event_kind").GetString() == "ContentHeadCommitted"
                && Prop(Prop(e, "body"), "committed_operation_id").GetString() == k1Operation.ToString("D"));
            var gapped = verdict.Bodies.Where(b => b.Revision != k1Body.Revision).ToList();
            var fromGapped = Reconstruct(gapped, rOrg.Id);
            var chain = ChainCheck(gapped);
            Demonstrate("R-04", "EXPECTED_LIMIT", "archive completeness: with one archived ContentHeadCommitted body missing, the reconstruction silently lacks that commit's tuples (the reconstruction itself raises nothing); only the digest chain over the archive reveals the gap",
                "reconstruction 7 of 8 tuples, no error; chain incomplete", $"reconstruction {fromGapped.Count} of {reconstructed.Count}; chain {(chain.Complete ? "complete" : "INCOMPLETE")} ({chain.Detail})",
                fromGapped.Count == reconstructed.Count - 1 && !chain.Complete);
        }

        // Commitment limit: the archive alone cannot show that a tuple's commitment is the one in the committed rows; independently
        // verified content bytes can (their digest equals the Authority leaf; the commitment column read from them equals the ledger).
        {
            var original = reconstructed.Single(t => t.DataKey == k1Id);
            var altered = original with { CommitmentHex = string.Concat(original.CommitmentHex.Select(c => c == '0' ? '1' : '0')) };
            var leafDigests = (await RowsAsync(adminCs, $"SELECT encode(l.canonical_payload_digest,'hex') FROM authority.record_manifest_leaves l JOIN authority.record_manifest_commits c ON c.record_manifest_commit_id = l.record_manifest_commit_id WHERE c.committed_operation_id = '{k1Operation}'")).Select(r => r[0]).ToHashSet();
            var verifiedBytes = k1Staged.Where(s => leafDigests.Contains(Convert.ToHexStringLower(SHA256.HashData(s.Bytes)))).ToList();
            var fromBytes = verifiedBytes.Select(s => RowCanonicalDecoder.Decode(s.Bytes).Column("data_key_commitment_bytes")!.AsBytes()!.Value.ToArray()).Select(Convert.ToHexStringLower).Distinct().ToList();
            Demonstrate("R-05", "EXPECTED_LIMIT", "commitment validation: an altered commitment in an archived tuple is indistinguishable from a genuine one in the archive alone; content bytes verified against the Authority leaves (digest equality) yield the true commitment; the Authority can never check the commitment against the seed",
                "altered != ledger; 2 verified rows; commitment from verified bytes = ledger, != altered",
                $"altered equals ledger {altered.CommitmentHex == original.CommitmentHex}; verified rows {verifiedBytes.Count}; from bytes {string.Join(",", fromBytes.Select(f => f[..8]))} = ledger {fromBytes.SequenceEqual([original.CommitmentHex])}",
                altered.CommitmentHex != original.CommitmentHex && verifiedBytes.Count == 2 && fromBytes.SequenceEqual([original.CommitmentHex]));
        }

        // Contrast on the unrestored primary: the lost identifier is refused.
        await RefusedPrepareAsync("R-06", "contrast (primary, not restored): the post-T0 identifier K1 transplanted to another record is refused (DK-4)", rOrg.Records[8],
            Owner(rOrg.Records[8], Rows.Transplant((FieldEncryptedValue)bd1Record.Items[0], rOrg.Id, rOrg.Records[8].Id, Guid.CreateVersion7(), Guid.CreateVersion7())), Reused);

        // BD-1 (disposable restore copy only): with acceptance on in a process attached to the restored database, the lost identifier K1 is accepted.
        {
            var restoredSwitch = EnvelopeV2AcceptanceSwitch.Create("true", "Testing", ReleaseB3);
            var observed = await ObserveAsync(restoredSwitch, RestoreCopyDatabase);
            var (ready, detail) = await Guard(ReleaseB3, RestoreCopyDatabase).RefreshAsync(CancellationToken.None);
            var node = Instance("RESTORED-T0", ReleaseB3, restoredSwitch, RestoreCopyDatabase);
            var reused = k1Batch.Seal("k1-reused-after-restore");
            var p = await PrepareAsync(node, t0Shadow, Owner(t0Shadow, reused), database: RestoreCopyDatabase);
            var c = p.Outcome == "OK" ? await CommitAsync(node, t0Shadow, p) : (p.Outcome, p.Message);
            var t0Binding = await BindingRowsAsync($"data_key_identifier = '{k1Id}'", RestoreCopyDatabase);
            Demonstrate("R-07", "EXPECTED_GAP_BD1", "BD-1 (restore copy T0 only): after an AuthorityDb restore to before K1, an instance with acceptance on accepts a New group that re-uses the lost identifier K1; nothing in the restored database (readiness, floors, Release B) detects it. This is the gap BD-1 must close; it is not a passing security property",
                "readiness ready (no barrier); Prepare OK; Commit OK; K1 bound again in the restored ledger",
                $"policy {observed}; readiness {ready} {detail}; Prepare {p.Outcome}; Commit {c.Item1}; K1 bindings in the restored ledger {t0Binding.Count} (operation {(t0Binding.Count == 1 ? t0Binding[0].Operation.ToString("N")[..8] : "-")})",
                ready && p.Outcome == "OK" && c.Item1 == "OK" && t0Binding.Count == 1);
        }

        await GrantSentinelAsync("after-recovery");
    }
}
