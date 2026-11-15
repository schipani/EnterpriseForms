// E3-M2b-1 implementation gate, harness B (scratchpad only; never part of the repository).
//
// Runs the REAL E3-M2b-1 Authority code (Release build of the working tree, ..\newbin): its ContentService CARV rules, its EF
// AuthorityStore with the CARV refusal mapping, its ContentAcceptanceRuleSchemaReader/Guard and its real migration (--migrate),
// against a disposable PostgreSQL 18 container, together with a frozen LEGACY build driven through the legacy agent child
// process (..\agent-<commit>). No emulation trigger is installed: every CARV stamp comes from the real build.
// "RB" = real CARV build in this process; "LB" = legacy binary in the agent process.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var lab = new Lab(
    Environment.GetEnvironmentVariable("CARV_NEWBIN") ?? throw new InvalidOperationException("CARV_NEWBIN"),
    Environment.GetEnvironmentVariable("CARV_AGENT") ?? throw new InvalidOperationException("CARV_AGENT"),
    Environment.GetEnvironmentVariable("CARV_LEGACY") ?? throw new InvalidOperationException("CARV_LEGACY"),
    Environment.GetEnvironmentVariable("CARV_ADMIN") ?? throw new InvalidOperationException("CARV_ADMIN"),
    Environment.GetEnvironmentVariable("CARV_RUNTIME") ?? throw new InvalidOperationException("CARV_RUNTIME"),
    Environment.GetEnvironmentVariable("CARV_SQL") ?? throw new InvalidOperationException("CARV_SQL"),
    Environment.GetEnvironmentVariable("CARV_EVIDENCE") ?? throw new InvalidOperationException("CARV_EVIDENCE"));
return await lab.RunAsync();

internal sealed class Lab(string newBinary, string agent, string legacy, string adminCs, string runtimeCs, string sqlDir, string evidenceDir)
{
    private const string Issuer = "https://carv-harness.invalid/";
    private const string CarvMigrationId = "20261115014221_AuthorityContentAcceptanceRuleVersion";
    private const string FloorQuery = "SELECT coalesce(max(substring(conname FROM 'ck_content_operation_rule_floor_v([0-9]+)$')::int)::text, 'none') FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass";
    private readonly List<Check> checks = [];
    private readonly List<string> log = [];
    private readonly TestMac mac = new();
    private readonly CapturingAlerts alerts = new();
    private int agentCalls;

    public async Task<int> RunAsync()
    {
        try
        {
            await ScenariosAsync();
        }
        catch (Exception ex)
        {
            Log("HARNESS ABORTED: " + Describe(ex) + " " + ex);
            checks.Add(new Check("HARNESS", "harness completed", "no unexpected exception", Describe(ex), false));
        }

        var failed = checks.Count(c => !c.Pass);
        var json = JsonSerializer.Serialize(new
        {
            harness = "E3-M2b-1 CARV implementation gate, harness B (real CARV build + legacy agent)",
            carvBuild = "E3-M2b-1 Release build of the working tree (newbin-sha256.txt); ContentAcceptanceRules.CurrentVersion = " + ContentAcceptanceRules.CurrentVersion,
            legacyBuild = legacy,
            postgres = await ScalarAsync(adminCs, "SELECT version()"),
            legacyAgentCalls = agentCalls,
            checksTotal = checks.Count,
            checksFailed = failed,
            checks,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "carv-evidence.json"), json);
        await File.WriteAllLinesAsync(Path.Combine(evidenceDir, "carv-log.txt"), log);
        Console.WriteLine($"checks: {checks.Count}, failed: {failed}");
        foreach (var c in checks)
        {
            Console.WriteLine($"{(c.Pass ? "PASS" : "FAIL")} {c.Id}: {c.Description} | observed: {c.Observed}");
        }

        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ scenarios
    private async Task ScenariosAsync()
    {
        var orgA = await SeedOrgAsync("A", 7);
        var orgB = await SeedOrgAsync("B", 2);
        var rb = Instance("RB", runtimeCs);
        var rbLate = Instance("RB-late", runtimeCs, clockOffset: TimeSpan.FromMinutes(11));
        var guard = new ContentAcceptanceRuleGuard(new ContentAcceptanceRuleSchemaReader(Factory.For(runtimeCs)), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance);
        // The real host readiness check with the real guard and a real store connection; SpiceDB and the archive are probe-only fakes.
        var transition = new ProjectionMarkerTransitionState();
        transition.MarkCompleted();
        var readiness = new AuthorityReadiness(mac, ProbeOnlyProxy.Create<ISpiceDbProjection>(), ProbeOnlyProxy.Create<ISecurityEventArchive>(), Factory.For(runtimeCs), transition, guard);

        // ---------- before the CARV migration: legacy writes only; the real guard sees no fence
        Record("RB-G0", "real guard on the legacy schema (runtime role): not ready, migration missing", "False ContentAcceptanceRuleMigrationMissing", await GuardAsync(guard), await GuardAsync(guard) == "False ContentAcceptanceRuleMigrationMissing");
        Record("RB-R0", "host readiness (AuthorityReadiness) reports the guard: not ready on the legacy schema", "False ContentAcceptanceRuleMigrationMissing", await ReadinessAsync(readiness), await ReadinessAsync(readiness) == "False ContentAcceptanceRuleMigrationMissing");
        foreach (var record in orgA.Records.Concat(orgB.Records))
        {
            await LegacyWriteAsync(record);
        }

        var historical = (Record: orgA.Records[2], Operation: orgA.Records[2].LastOperation!.Value);
        var x0 = await LegacyPrepareAsync(orgA.Records[3]); // left Prepared across the migration and the floor
        Record("RB-P0", "legacy binary writes before the migration (agent)", "9 commits, 1 Prepared, all unstamped later", await OpsSummaryAsync(), await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name='Committed'") == 9 && x0.Error is null);

        // ---------- the real migration (new binary --migrate)
        var migrated = await RealMigrateAsync("carv-migration");
        Record("RB-M1", "the real CARV migration applies over the legacy schema; the emulation trigger is NOT installed", "OK; carv recorded; no emulation trigger",
            $"{migrated}; history carv={await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'")}; emulation triggers={await CountAsync("SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_a_test_carv_build_emulation'")}",
            migrated == "OK" && await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'") == 1 && await CountAsync("SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_a_test_carv_build_emulation'") == 0);
        Record("RB-G1", "real guard after the migration: ready, no floor", "True content acceptance rules v1 (floor none)", await GuardAsync(guard), await GuardAsync(guard) == "True content acceptance rules v1 (floor none)");
        Record("RB-R1", "host readiness after the migration: ready, with the guard's detail", "True ...; content acceptance rules v1 (floor none)", await ReadinessAsync(readiness), (await ReadinessAsync(readiness)).StartsWith("True", StringComparison.Ordinal) && (await ReadinessAsync(readiness)).EndsWith("content acceptance rules v1 (floor none)", StringComparison.Ordinal));

        // ---------- M2-CARV-01 on real PostgreSQL
        {
            var r = orgA.Records[0];
            var op = await WriteAsync(rb, r);
            Record("RB-01", "a real CARV write stamps P = V at Prepare and C = V at Commit", "1/1 Committed", await StampsAsync(op), await StampsAsync(op) == "1/1 Committed");
        }

        // ---------- CARV Prepare -> legacy Commit (MX-01 with the real build)
        {
            var r = orgA.Records[1];
            var (op, rows) = await PrepareWithRowsAsync(rb, r);
            var stamp = await StampsAsync(op.OperationId);
            var before = await SnapshotAsync(orgA, r);
            var legacyCommit = await LegacyCommitAsync(r, op, rows);
            var after = await SnapshotAsync(orgA, r);
            Record("RB-02a", "real CARV Prepare stamps the operation", "1/NULL Prepared", stamp, stamp == "1/NULL Prepared");
            Record("RB-02b", "the legacy binary cannot commit a real-CARV-prepared operation; nothing advances", "23514 ck_content_operation_commit_rule_version; snapshot unchanged; 1/NULL Prepared",
                $"{legacyCommit.Error}; {Diff(before, after)}; {await StampsAsync(op.OperationId)}", (legacyCommit.Error ?? string.Empty).Contains("ck_content_operation_commit_rule_version", StringComparison.Ordinal) && before == after && await StampsAsync(op.OperationId) == "1/NULL Prepared");
            Record("RB-02c", "legacy host classification of that refusal", "503 AUTHORITY_NOT_READY retryable (R-CARV-2)", legacyCommit.Mapped ?? "none", (legacyCommit.Mapped ?? string.Empty).StartsWith("503", StringComparison.Ordinal));
            var carv = await Outcome(CommitAsync(rb, r, op, rows));
            Record("RB-02d", "the same operation then commits through the real CARV build", "OK; 1/1 Committed", $"{carv}; {await StampsAsync(op.OperationId)}", carv == "OK" && await StampsAsync(op.OperationId) == "1/1 Committed");
        }

        // ---------- legacy Prepare -> CARV Commit: the APPLICATION rule refuses and aborts (MX-02 with the real build)
        {
            var r = orgA.Records[1];
            var legacyPrepared = await LegacyPrepareAsync(r);
            var op = legacyPrepared.Prepared!;
            var before = await SnapshotAsync(orgA, r);
            var signaturesVerified = mac.Verifications;
            var carv = await Outcome(CommitAsync(rb, r, op, legacyPrepared.Rows));
            var after = await SnapshotAsync(orgA, r);
            Record("RB-03a", "real CARV Commit of a legacy-prepared operation: 409 OPERATION_RULES_OUTDATED before any verification", "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False; no signature verified",
                $"{carv}; verifications {signaturesVerified}->{mac.Verifications}", carv == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && signaturesVerified == mac.Verifications);
            Record("RB-03b", "the operation is aborted with reason OPERATION_RULES_OUTDATED; nothing advances", "NULL/NULL Aborted OPERATION_RULES_OUTDATED; snapshot unchanged",
                $"{await StampsAsync(op.OperationId)} {await ReasonAsync(op.OperationId)}; {Diff(before, after)}", await StampsAsync(op.OperationId) == "NULL/NULL Aborted" && await ReasonAsync(op.OperationId) == "OPERATION_RULES_OUTDATED" && before == after);
            var legacyRetry = await LegacyCommitAsync(r, op, legacyPrepared.Rows);
            Record("RB-03c", "the aborted operation can never be accepted afterwards, not even by the legacy build", "OPERATION_STATE_INVALID", legacyRetry.Error ?? "accepted!", (legacyRetry.Error ?? string.Empty).Contains("OPERATION_STATE_INVALID", StringComparison.Ordinal));
            var fresh = await Outcome(WriteAsync(rb, r));
            Record("RB-03d", "recovery: a fresh operation (new idempotency key) on the real build completes", "OK", fresh, fresh == "OK");
        }

        // ---------- M2-CARV-05: a historical (legacy, unstamped) committed operation still replays
        {
            var r = historical.Record;
            var before = await SnapshotAsync(orgA, r);
            var replay = await Outcome(rb.Service.CommitAsync(Actor(orgA), historical.Operation, new CommitContentChangeRequest(orgA.Id, r.Id, r.AcceptedStaged()), Guid.CreateVersion7(), CancellationToken.None));
            var after = await SnapshotAsync(orgA, r);
            Record("RB-05", "real CARV Commit replay of a historical NULL/NULL Committed operation returns the head and writes nothing", "OK; snapshot unchanged; NULL/NULL Committed",
                $"{replay}; {Diff(before, after)}; {await StampsAsync(historical.Operation)}", replay == "OK" && before == after && await StampsAsync(historical.Operation) == "NULL/NULL Committed");
        }

        // ---------- M2-CARV-04: Prepare replay of a legacy-prepared operation
        {
            var r = orgA.Records[2];
            var operation = Guid.CreateVersion7();
            var (request, rows) = await BuildRequestAsync(r, operation);
            var key = "carv-replay-" + operation.ToString("N");
            var legacyPrepared = await LegacyPrepareRequestAsync(r, request, rows, key);
            var (ops, leaves) = (await CountAsync("SELECT count(*) FROM authority.content_change_operations"), await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves"));
            var replay = await Outcome(rb.Service.PrepareAsync(Actor(r.Org), request, key, Guid.CreateVersion7(), CancellationToken.None));
            Record("RB-04", "real CARV Prepare replay of a legacy-prepared operation (same key and request): 409, nothing written", "OPERATION_RULES_OUTDATED; no new operation or leaf; NULL/NULL Prepared",
                $"{legacyPrepared.Error ?? "legacy prepared"}; {replay}; ops {ops}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; leaves {leaves}->{await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves")}; {await StampsAsync(operation)}",
                legacyPrepared.Error is null && replay == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && ops == await CountAsync("SELECT count(*) FROM authority.content_change_operations") && leaves == await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves") && await StampsAsync(operation) == "NULL/NULL Prepared");
        }

        // ---------- M2-CARV-03 / R-M2a-3 regression with the real build
        {
            var r = orgA.Records[4];
            var operation = Guid.CreateVersion7();
            var (request, rows) = await BuildRequestAsync(r, operation, reuseCurrentKeyAndNonce: true);
            var control = await Outcome(rb.Service.PrepareAsync(Actor(r.Org), request with { OperationId = Guid.CreateVersion7() }, "carv-reuse-control-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None));
            Record("RB-06a", "control: the real build's Prepare refuses a candidate reusing the current wrapped key and nonce", "AuthorityException INVALID_COMMAND (ENVELOPE_INVALID)", control, control.StartsWith("AuthorityException INVALID_COMMAND", StringComparison.Ordinal));
            var before = await SnapshotAsync(orgA, r);
            var legacyPrepared = await LegacyPrepareRequestAsync(r, request, rows, "carv-reuse-" + operation.ToString("N"));
            if (legacyPrepared.Error is null)
            {
                var carv = await Outcome(CommitAsync(rb, r, legacyPrepared.Prepared!, rows));
                var after = await SnapshotAsync(orgA, r);
                Record("RB-06b", "R-M2a-3: the legacy build prepared the reuse (no cross-revision rule); the real CARV Commit refuses it; never accepted", "legacy prepared; OPERATION_RULES_OUTDATED; snapshot unchanged; NULL/NULL Aborted",
                    $"legacy prepared; {carv}; {Diff(before, after)}; {await StampsAsync(operation)}", carv == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && before == after && await StampsAsync(operation) == "NULL/NULL Aborted");
            }
            else
            {
                var after = await SnapshotAsync(orgA, r);
                Record("RB-06b", "R-M2a-3: this legacy build (E3-M2a) refuses the reuse at its own Prepare; nothing prepared or accepted", "legacy refused ENVELOPE_INVALID; snapshot unchanged",
                    $"{legacyPrepared.Error}; {Diff(before, after)}", legacyPrepared.Error.Contains("INVALID_COMMAND", StringComparison.Ordinal) && before == after);
            }
        }

        // ---------- the existing abort paths work under the state guard with stamped operations
        {
            var r = orgA.Records[5];
            var (op, rows) = await PrepareWithRowsAsync(rb, r);
            var expired = await Outcome(CommitAsync(rbLate, r, op, rows));
            Record("RB-07", "expiry abort of a stamped operation (real build, clock past the lifetime)", "OPERATION_EXPIRED; 1/NULL Aborted", $"{expired}; {await StampsAsync(op.OperationId)}",
                expired.Contains("OPERATION_EXPIRED", StringComparison.Ordinal) && await StampsAsync(op.OperationId) == "1/NULL Aborted");

            var r6 = orgA.Records[6];
            var (first, firstRows) = await PrepareWithRowsAsync(rb, r6);
            var (second, secondRows) = await PrepareWithRowsAsync(rb, r6);
            var won = await Outcome(CommitAsync(rb, r6, first, firstRows));
            var lost = await Outcome(CommitAsync(rb, r6, second, secondRows));
            Record("RB-08", "revision-conflict abort of a stamped operation", "OK; CONTENT_REVISION_CONFLICT; 1/1 Committed, 1/NULL Aborted",
                $"{won}; {lost}; {await StampsAsync(first.OperationId)}, {await StampsAsync(second.OperationId)}", won == "OK" && lost.Contains("CONTENT_REVISION_CONFLICT", StringComparison.Ordinal) && await StampsAsync(first.OperationId) == "1/1 Committed" && await StampsAsync(second.OperationId) == "1/NULL Aborted");
        }

        // ---------- concurrency of the real CARV rules (MX-03 with the real build)
        {
            var result = await ConcurrentCarvAsync(6, 4, [orgA.Records[5], orgA.Records[6], orgB.Records[0], orgB.Records[1]]);
            var mismatched = await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name='Committed' AND (prepare_acceptance_rule_version IS DISTINCT FROM commit_acceptance_rule_version)");
            Record("RB-09", "6 concurrent real CARV instances x 4 rounds on 4 records in 2 organizations", "only OK or CONTENT_REVISION_CONFLICT; every Committed row P = C",
                result.Summary + $"; committed rows with P<>C: {mismatched}", result.Unexpected == 0 && result.Ok > 0 && mismatched == 0);
        }

        // ---------- the versioned application name (operator inventory for the floor release)
        {
            var r = orgA.Records[0];
            var (op, rows) = await PrepareWithRowsAsync(rb, r);
            var held = rb.Faults.BlockAt(FaultNames.CrashAfterContentHeadCommit + ":before");
            var commit = Task.Run(() => CommitAsync(rb, r, op, rows));
            await held.Reached.WaitAsync(TimeSpan.FromSeconds(20));
            var names = await ScalarAsync(adminCs, "SELECT coalesce(string_agg(DISTINCT application_name, ',' ORDER BY application_name), 'none') FROM pg_stat_activity WHERE usename = 'authority_runtime' AND state <> 'idle'");
            held.Release.Release();
            var done = await Outcome(commit);
            Record("RB-10", "real CARV connections carry the rule version in application_name (pg_stat_activity)", "contains 'carv-v1'; commit OK", $"{names}; {done}", names.Contains("carv-v1", StringComparison.Ordinal) && done == "OK");
        }

        // ---------- the real guarded Down and the state guard switch
        {
            var r = orgB.Records[0];
            var (inFlight, _) = await PrepareWithRowsAsync(rb, r);
            var down = await ExecScriptAsync(adminCs, "real_down.sql", "carv-down");
            Record("RB-11", "the real guarded Down refuses while an operation prepared by the real build is in flight", "refused (in flight); 1/NULL Prepared", $"{down}; {await StampsAsync(inFlight.OperationId)}", down.Contains("in flight", StringComparison.Ordinal) && await StampsAsync(inFlight.OperationId) == "1/NULL Prepared");

            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DISABLE TRIGGER trg_content_operation_rule_state_guard");
            var disabled = await GuardAsync(guard);
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations ENABLE TRIGGER trg_content_operation_rule_state_guard");
            var enabled = await GuardAsync(guard);
            Record("RB-12", "real guard: a disabled state guard (superuser) is not ready; re-enabled is ready", "False ContentAcceptanceRuleStateGuardMissing | True ...", $"{disabled} | {enabled}", disabled == "False ContentAcceptanceRuleStateGuardMissing" && enabled.StartsWith("True", StringComparison.Ordinal));
        }

        // ---------- G08: privilege inventory of the trigger function
        {
            var inventory = await ScalarAsync(adminCs, "SELECT p.proname||'|secdef='||p.prosecdef||'|owner='||pg_get_userbyid(p.proowner)||'|config='||coalesce(array_to_string(p.proconfig, ';'), '')||'|acl='||coalesce(p.proacl::text, 'default')||'|runtime_execute='||has_function_privilege('authority_runtime', p.oid, 'EXECUTE') FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'authority' AND p.proname = 'content_operation_rule_state_guard'");
            await File.WriteAllTextAsync(Path.Combine(evidenceDir, "trigger-function-privilege-inventory.txt"), inventory + Environment.NewLine);
            Record("RB-13", "G08 inventory: the state-guard function is SECURITY INVOKER, fixed search_path, no PUBLIC or runtime EXECUTE (the trigger still fires)", "secdef=false; search_path=pg_catalog, pg_temp; runtime_execute=false",
                inventory, inventory.Contains("secdef=false", StringComparison.Ordinal) && inventory.Contains("search_path=pg_catalog, pg_temp", StringComparison.Ordinal) && inventory.Contains("runtime_execute=false", StringComparison.Ordinal) && !inventory.Contains("{=X/", StringComparison.Ordinal) && !inventory.Contains(",=X/", StringComparison.Ordinal));
        }

        // ---------- the floor (harness SQL: M2b-1F is a separate, later release) with real CARV writers during the transition
        {
            var work = ConcurrentCarvAsync(4, 6, [orgA.Records[5], orgA.Records[6], orgB.Records[0], orgB.Records[1]]);
            await Task.Delay(300);
            await using (var floorTx = await HoldFloorAsync("floor_v1.sql"))
            {
                await Task.Delay(1500);
                await floorTx.CommitAsync();
            }

            var result = await work;
            Record("RB-14", "4 concurrent real CARV instances while the floor is installed (held 1.5 s)", "only OK or CONTENT_REVISION_CONFLICT; floor 1", result.Summary + "; floor=" + await ScalarAsync(adminCs, FloorQuery), result.Unexpected == 0 && result.Ok > 0 && await ScalarAsync(adminCs, FloorQuery) == "1");
            Record("RB-G2", "real guard with floor 1 = build version 1: ready", "True content acceptance rules v1 (floor 1)", await GuardAsync(guard), await GuardAsync(guard) == "True content acceptance rules v1 (floor 1)");
        }

        // ---------- after the floor
        {
            var r = orgA.Records[0];
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var legacyPrepare = await LegacyPrepareAsync(r);
            Record("RB-15", "the legacy binary cannot prepare after the floor; nothing persisted", "23514 ck_content_operation_rule_floor_v1; no new operation", $"{legacyPrepare.Error}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}",
                (legacyPrepare.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations"));

            var x0Record = orgA.Records[3];
            var before = await SnapshotAsync(orgA, x0Record);
            var x0Legacy = await LegacyCommitAsync(x0Record, x0.Prepared!, x0.Rows);
            var x0Carv = await Outcome(CommitAsync(rb, x0Record, x0.Prepared!, x0.Rows));
            var after = await SnapshotAsync(orgA, x0Record);
            Record("RB-16", "an operation the legacy build prepared before the migration: the legacy Commit is refused by the floor, the real build aborts it", "23514 floor_v1 | OPERATION_RULES_OUTDATED | NULL/NULL Aborted; snapshot unchanged",
                $"{x0Legacy.Error} | {x0Carv} | {await StampsAsync(x0.Prepared!.OperationId)}; {Diff(before, after)}",
                (x0Legacy.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && x0Carv == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && await StampsAsync(x0.Prepared!.OperationId) == "NULL/NULL Aborted" && before == after);

            var carv = await Outcome(WriteAsync(rb, r));
            Record("RB-17", "a real CARV write after the floor succeeds", "OK", carv, carv == "OK");
        }

        // ---------- a newer floor overtakes the running v1 build: database refusals are mapped and alerted; the guard turns not ready
        {
            var r = orgA.Records[0];
            var (op, rows) = await PrepareWithRowsAsync(rb, r); // prepared at v1 before the raise
            var raise = await ExecFileAsync(adminCs, "floor_v2.sql", "carv-floor");
            var alertsBefore = alerts.Raised.Count;
            var before = await SnapshotAsync(orgA, r);
            var commit = await Outcome(CommitAsync(rb, r, op, rows));
            var after = await SnapshotAsync(orgA, r);
            var commitAlert = alerts.Raised.Skip(alertsBefore).FirstOrDefault();
            Record("RB-18", "after a raise to floor 2, the real v1 build's Commit is refused by the database and mapped to 409 OPERATION_RULES_OUTDATED with an alert; nothing advances", "OK raise; OPERATION_RULES_OUTDATED; alert names ck_content_operation_rule_floor_v2; snapshot unchanged; 1/NULL Prepared",
                $"{raise}; {commit}; alert={commitAlert?.AlertKind}:{commitAlert?.Summary}; {Diff(before, after)}; {await StampsAsync(op.OperationId)}",
                raise == "OK" && commit == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && commitAlert is { AlertKind: ContentAcceptanceRuleRefusals.AlertKind } && commitAlert.Summary.Contains("ck_content_operation_rule_floor_v2", StringComparison.Ordinal) && commitAlert.OperationId == op.OperationId && before == after && await StampsAsync(op.OperationId) == "1/NULL Prepared");

            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            alertsBefore = alerts.Raised.Count;
            var prepare = await Outcome(PrepareWithRowsAsync(rb, r));
            var prepareAlert = alerts.Raised.Skip(alertsBefore).FirstOrDefault();
            Record("RB-19", "the real v1 build's Prepare after the raise is refused by the database, mapped and alerted; nothing persisted", "OPERATION_RULES_OUTDATED; alert; no new operation",
                $"{prepare}; alert={prepareAlert?.AlertKind}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}",
                prepare == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && prepareAlert is { AlertKind: ContentAcceptanceRuleRefusals.AlertKind } && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations"));
            Record("RB-G3", "real guard with floor 2 above build version 1: not ready", "False ContentAcceptanceRuleFloorAboveBuild(floor=2, build=1)", await GuardAsync(guard), await GuardAsync(guard) == "False ContentAcceptanceRuleFloorAboveBuild(floor=2, build=1)");
            Record("RB-R2", "host readiness of the overtaken v1 build: not ready", "False ContentAcceptanceRuleFloorAboveBuild(floor=2, build=1)", await ReadinessAsync(readiness), await ReadinessAsync(readiness) == "False ContentAcceptanceRuleFloorAboveBuild(floor=2, build=1)");
        }

        Record("FINAL", "final operation table summary", "informational", await OpsSummaryAsync() + $"; legacy agent calls={agentCalls}", true);
    }

    // ------------------------------------------------------------------------------------------------ model
    private sealed record Check(string Id, string Description, string Expected, string Observed, bool Pass);

    private sealed record AgentResult(PreparedContentOperation? Prepared, List<Staged> Rows, string? Error, string? Mapped);

    private sealed class Org(Guid id, Guid owner, string label)
    {
        public Guid Id { get; } = id;
        public Guid Owner { get; } = owner;
        public string Label { get; } = label;
        public List<RecordModel> Records { get; } = [];
    }

    private sealed class RecordModel(Org org, Guid id)
    {
        public Org Org { get; } = org;
        public Guid Id { get; } = id;
        public Guid TextField { get; } = Guid.CreateVersion7();
        public Guid TextItem { get; } = Guid.CreateVersion7();
        public Guid SealedField { get; } = Guid.CreateVersion7();
        public Guid SealedItem { get; } = Guid.CreateVersion7();
        public List<(Staged Row, byte[] Signature)> Accepted { get; set; } = [];
        public Guid? LastOperation { get; set; }
        public byte AcceptedSeed { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        private int writes;

        public int NextSeed() => Interlocked.Increment(ref writes) % 200 + 10;

        public List<StagedContentItem> AcceptedStaged() => Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList();
    }

    private sealed record AuthorityNode(string Name, ContentService Service, GatedFaults Faults);

    private AuthorityNode Instance(string name, string connectionString, TimeSpan? clockOffset = null)
    {
        // The real build's versioned application name (AddAuthorityInfrastructure applies the same helper in the host).
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "carv-runtime-" + name }.ConnectionString)!;
        var store = new AuthorityStore(Factory.For(cs), null, null, alerts);
        var clock = new OffsetClock(clockOffset ?? TimeSpan.Zero);
        var faults = new GatedFaults();
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), clock);
        return new AuthorityNode(name, new ContentService(store, mac, verifier, faults, clock, new ShareContributorWriteSwitch(false, false)), faults);
    }

    private async Task<Org> SeedOrgAsync(string label, int records)
    {
        var org = new Org(Guid.CreateVersion7(), Guid.CreateVersion7(), label);
        var keyName = "carv-key-" + org.Id.ToString("N");
        var seedCs = new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "carv-seed" }.ConnectionString;
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var now = DateTimeOffset.UtcNow;
        context.OrganizationStates.Add(new OrganizationAuthorizationState { OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org.Id, OrganizationDisplayName = "CARV " + label, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous, StateModifiedOn = now });
        context.IdentityBindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = org.Id, AuthorityUserId = org.Owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = org.Owner.ToString("D"), IdentityBindingEnabled = true, IdentityCreatedOn = now });
        context.KeyRegistrations.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = org.Id, ProviderName = "LocalDevelopment", ProviderKeyName = keyName, CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1, KeyRegisteredOn = now, KeyModifiedOn = now });
        for (var i = 0; i < records; i++)
        {
            var record = new RecordModel(org, Guid.CreateVersion7());
            org.Records.Add(record);
            context.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, ParentResourceEntityId = Fixtures.Form, RegisteredBySecurityCommandId = Guid.CreateVersion7(), ResourceRegisteredOn = now });
            context.ContentHeads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = org.Id, FormRecordId = record.Id, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1, ContentHeadModifiedOn = now });
            context.RelationshipEdges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = org.Owner, SubjectRelationName = string.Empty, CreatedBySecurityCommandId = Guid.CreateVersion7(), RelationshipCreatedOn = now });
        }

        await context.SaveChangesAsync();
        return org;
    }

    // ------------------------------------------------------------------------------------------------ protocol drivers
    private static VerifiedActor Actor(Org org) => new(Issuer, org.Owner.ToString("D"), "carv-harness");

    private async Task<(PrepareContentChangeRequest Request, List<Staged> Rows)> BuildRequestAsync(RecordModel record, Guid operation, bool reuseCurrentKeyAndNonce = false)
    {
        (long Revision, byte[] Root) head;
        List<StagedContentItem>? current;
        await record.Lock.WaitAsync();
        try
        {
            head = await HeadAsync(record);
            current = record.Accepted.Count == 0 ? null : record.AcceptedStaged();
        }
        finally
        {
            record.Lock.Release();
        }

        var seed = (byte)record.NextSeed();
        var sealedRow = reuseCurrentKeyAndNonce
            ? Fixtures.SealedReusing(record.Org.Id, record.Id, record.SealedField, record.SealedItem, record.AcceptedSeed, ciphertextFill: 250)
            : Fixtures.Sealed(record.Org.Id, record.Id, record.SealedField, record.SealedItem, seed);
        var rows = new List<Fixtures.Row> { Fixtures.Header(record.Org.Id, record.Id), Fixtures.Text(record.Org.Id, record.Id, record.TextField, record.TextItem, "value-" + seed), sealedRow }
            .Select(r => Fixtures.Encode(r, record.Org.Id, operation, head.Revision + 1, seed)).ToList();
        var request = new PrepareContentChangeRequest(record.Org.Id, record.Id, operation, 7, head.Revision, Convert.ToBase64String(head.Root),
            rows.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: current);
        return (request, rows);
    }

    private async Task<(PreparedContentOperation Prepared, List<Staged> Rows)> PrepareWithRowsAsync(AuthorityNode instance, RecordModel record)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation);
        var prepared = await instance.Service.PrepareAsync(Actor(record.Org), request, "carv-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
        return (prepared, rows);
    }

    private static async Task CommitAsync(AuthorityNode instance, RecordModel record, PreparedContentOperation prepared, List<Staged> rows)
    {
        var staged = Staged(prepared, rows);
        await instance.Service.CommitAsync(Actor(record.Org), prepared.OperationId, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None);
        Accept(record, prepared, rows);
    }

    private static List<StagedContentItem> Staged(PreparedContentOperation prepared, List<Staged> rows)
    {
        var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
        return rows.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
    }

    private static void Accept(RecordModel record, PreparedContentOperation prepared, List<Staged> rows)
    {
        var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => Convert.FromBase64String(s.RowSignatureBase64));
        record.Accepted = rows.Select(c => (c, signatures[(c.Kind, c.ItemId)])).ToList();
        record.LastOperation = prepared.OperationId;
        record.AcceptedSeed = rows.Single(r => r.Row.Schema.EntityKind == "field_encrypted_values").Seed;
    }

    private async Task<Guid> WriteAsync(AuthorityNode instance, RecordModel record)
    {
        var (prepared, rows) = await PrepareWithRowsAsync(instance, record);
        await CommitAsync(instance, record, prepared, rows);
        return prepared.OperationId;
    }

    // ---- the legacy agent (frozen legacy binary in a child process)
    private async Task<AgentResult> LegacyPrepareAsync(RecordModel record)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation);
        return await LegacyPrepareRequestAsync(record, request, rows, "carv-legacy-" + operation.ToString("N"));
    }

    private async Task<AgentResult> LegacyPrepareRequestAsync(RecordModel record, PrepareContentChangeRequest request, List<Staged> rows, string key)
    {
        var output = await AgentAsync("prepare", new { Issuer, Subject = record.Org.Owner.ToString("D"), Key = key, Request = request });
        var prepared = output.GetProperty("Ok").GetBoolean() ? output.GetProperty("Result").Deserialize<PreparedContentOperation>() : null;
        return new AgentResult(prepared, rows, output.GetProperty("Error").GetString(), output.GetProperty("Mapped").GetString());
    }

    private async Task<AgentResult> LegacyCommitAsync(RecordModel record, PreparedContentOperation prepared, List<Staged> rows)
    {
        var output = await AgentAsync("commit", new { Issuer, Subject = record.Org.Owner.ToString("D"), OperationId = prepared.OperationId, Request = new CommitContentChangeRequest(record.Org.Id, record.Id, Staged(prepared, rows)) });
        var ok = output.GetProperty("Ok").GetBoolean();
        if (ok)
        {
            Accept(record, prepared, rows);
        }

        return new AgentResult(prepared, rows, output.GetProperty("Error").GetString(), output.GetProperty("Mapped").GetString());
    }

    private async Task LegacyWriteAsync(RecordModel record)
    {
        var prepared = await LegacyPrepareAsync(record);
        if (prepared.Error is not null)
        {
            throw new InvalidOperationException("legacy prepare failed: " + prepared.Error);
        }

        var committed = await LegacyCommitAsync(record, prepared.Prepared!, prepared.Rows);
        if (committed.Error is not null)
        {
            throw new InvalidOperationException("legacy commit failed: " + committed.Error);
        }
    }

    private async Task<JsonElement> AgentAsync(string command, object input)
    {
        Interlocked.Increment(ref agentCalls);
        var inputFile = Path.Combine(Path.GetTempPath(), "carv-agent-in-" + Guid.NewGuid().ToString("N") + ".json");
        var outputFile = Path.Combine(Path.GetTempPath(), "carv-agent-out-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(inputFile, JsonSerializer.Serialize(input));
        try
        {
            var start = new ProcessStartInfo(agent) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(command);
            start.ArgumentList.Add(inputFile);
            start.ArgumentList.Add(outputFile);
            start.Environment["CARV_RUNTIME"] = runtimeCs;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("agent did not start");
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(outputFile))
            {
                throw new InvalidOperationException($"agent {command} exit {process.ExitCode}: {await stderr}");
            }

            var output = JsonDocument.Parse(await File.ReadAllTextAsync(outputFile)).RootElement.Clone();
            Log($"LB {command}: {(output.GetProperty("Ok").GetBoolean() ? "OK" : output.GetProperty("Error").GetString())}");
            return output;
        }
        finally
        {
            File.Delete(inputFile);
            File.Delete(outputFile);
        }
    }

    private sealed record ConcurrentResult(int Ok, int Conflicts, int Unexpected, string Summary);

    private async Task<ConcurrentResult> ConcurrentCarvAsync(int instances, int rounds, RecordModel[] records)
    {
        var pool = Enumerable.Range(0, instances).Select(i => Instance($"RB-par{i}", runtimeCs)).ToArray();
        int ok = 0, conflicts = 0, unexpected = 0;
        var unexpectedText = new ConcurrentBag<string>();
        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, instances), async (i, _) =>
        {
            for (var round = 0; round < rounds; round++)
            {
                var record = records[(i + round) % records.Length];
                try
                {
                    var (prepared, rows) = await PrepareWithRowsAsync(pool[i], record);
                    await record.Lock.WaitAsync();
                    try
                    {
                        await CommitAsync(pool[i], record, prepared, rows);
                    }
                    finally
                    {
                        record.Lock.Release();
                    }

                    Interlocked.Increment(ref ok);
                }
                catch (AuthorityException ex) when (ex.ErrorCode is AuthorityErrorCodes.ContentRevisionConflict or AuthorityErrorCodes.IdempotencyConflict)
                {
                    Interlocked.Increment(ref conflicts);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref unexpected);
                    unexpectedText.Add(Describe(ex));
                }
            }
        });
        var summary = $"ok={ok}; conflicts={conflicts}; unexpected={unexpected}{(unexpected > 0 ? " [" + string.Join(" | ", unexpectedText.Take(3)) + "]" : string.Empty)}; {stopwatch.ElapsedMilliseconds} ms";
        Log("concurrent: " + summary);
        return new ConcurrentResult(ok, conflicts, unexpected, summary);
    }

    // ------------------------------------------------------------------------------------------------ evidence helpers
    private static async Task<string> GuardAsync(ContentAcceptanceRuleGuard guard)
    {
        var (ready, detail) = await guard.RefreshAsync(CancellationToken.None);
        return $"{ready} {detail}";
    }

    private static async Task<string> ReadinessAsync(AuthorityReadiness readiness)
    {
        var (ready, detail) = await readiness.CheckAsync(CancellationToken.None);
        return $"{ready} {detail}";
    }

    private async Task<string> RealMigrateAsync(string application)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(newBinary);
        start.ArgumentList.Add("--migrate");
        start.Environment["ConnectionStrings__AuthorityDbMigration"] = new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = application }.ConnectionString;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start --migrate");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + "\n" + (await stderr);
        Log($"real --migrate ({application}) exit {process.ExitCode}: {output.Trim().Split('\n').LastOrDefault()}");
        return process.ExitCode == 0 ? "OK" : "exit " + process.ExitCode;
    }

    private async Task<(long Revision, byte[] Root)> HeadAsync(RecordModel record)
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT committed_content_revision, committed_manifest_root FROM authority.record_content_heads WHERE organization_id = @o AND form_record_id = @r", connection);
        command.Parameters.AddWithValue("o", record.Org.Id);
        command.Parameters.AddWithValue("r", record.Id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), (byte[])reader[1]);
    }

    private async Task<string> SnapshotAsync(Org org, RecordModel record) => await ScalarAsync(adminCs, $"""
        SELECT concat_ws('; ',
          'head='||(SELECT committed_content_revision||':'||encode(committed_manifest_root,'hex') FROM authority.record_content_heads WHERE form_record_id='{record.Id}'),
          'commits='||(SELECT count(*) FROM authority.record_manifest_commits WHERE organization_id='{org.Id}'),
          'leaves='||(SELECT count(*) FROM authority.record_manifest_leaves l JOIN authority.record_manifest_commits c ON c.record_manifest_commit_id=l.record_manifest_commit_id WHERE c.organization_id='{org.Id}'),
          'identities='||(SELECT count(*) FROM authority.content_item_identities WHERE organization_id='{org.Id}'),
          'checkpoints='||(SELECT count(*) FROM authority.content_integrity_checkpoints WHERE organization_id='{org.Id}'),
          'journal='||(SELECT count(*) FROM authority.security_journal_entries WHERE organization_id='{org.Id}'),
          'committedOps='||(SELECT count(*) FROM authority.content_change_operations WHERE organization_id='{org.Id}' AND operation_state_name='Committed'))
        """);

    private static string Diff(string before, string after) => before == after ? "unchanged (" + before + ")" : "CHANGED " + before + " -> " + after;

    private async Task<string> StampsAsync(Guid operationId) => await ScalarAsync(adminCs, $"SELECT coalesce(prepare_acceptance_rule_version::text,'NULL')||'/'||coalesce(commit_acceptance_rule_version::text,'NULL')||' '||operation_state_name FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private async Task<string> ReasonAsync(Guid operationId) => await ScalarAsync(adminCs, $"SELECT coalesce(operation_quarantine_reason_code, 'NULL') FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private async Task<string> OpsSummaryAsync() => await ScalarAsync(adminCs, "SELECT coalesce(string_agg(s, ', ' ORDER BY s), 'none') FROM (SELECT operation_state_name||'='||count(*) AS s FROM authority.content_change_operations GROUP BY operation_state_name) q");

    private async Task<long> CountAsync(string sql) => long.Parse(await ScalarAsync(adminCs, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    private static async Task<string> ExecAsync(string cs, string sql)
    {
        try
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return "OK";
        }
        catch (PostgresException ex)
        {
            return $"{ex.SqlState} {ex.ConstraintName}: {ex.MessageText}";
        }
    }

    private async Task<string> ExecFileAsync(string cs, string file, string application)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(sqlDir, file));
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { ApplicationName = application }.ConnectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(sql, connection, tx))
            {
                await command.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            Log($"{file} applied ({application})");
            return "OK";
        }
        catch (PostgresException ex)
        {
            return $"{ex.SqlState} {ex.ConstraintName}: {ex.MessageText}";
        }
    }

    private async Task<string> ExecScriptAsync(string cs, string file, string application)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(sqlDir, file));
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { ApplicationName = application }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return "OK";
        }
        catch (PostgresException ex)
        {
            return $"{ex.SqlState} : {ex.MessageText}";
        }
    }

    private async Task<NpgsqlTransaction> HoldFloorAsync(string file)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "carv-floor" }.ConnectionString);
        await connection.OpenAsync();
        var tx = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(sqlDir, file)), connection, tx);
        await command.ExecuteNonQueryAsync();
        return tx;
    }

    private static async Task<string> Outcome(Task task)
    {
        try
        {
            await task;
            return "OK";
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    private static async Task<string> Outcome<T>(Task<T> task)
    {
        try
        {
            await task;
            return "OK";
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            parts.Add(e switch
            {
                PostgresException pg => $"PostgresException {pg.SqlState} {pg.ConstraintName}",
                AuthorityException a => $"AuthorityException {a.ErrorCode} {a.HttpStatusCode} retryable={a.Retryable}",
                _ => e.GetType().Name,
            });
        }

        return string.Join(" <- ", parts);
    }

    private void Record(string id, string description, string expected, string observed, bool pass)
    {
        checks.Add(new Check(id, description, expected, observed, pass));
        Log($"{(pass ? "PASS" : "FAIL")} {id}: {observed}");
    }

    private void Log(string line)
    {
        lock (log)
        {
            log.Add(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
        }
    }
}

// ---------------------------------------------------------------------------------------------------- ports and fixtures
internal sealed class Factory(DbContextOptions<AuthorityDbContext> options) : IDbContextFactory<AuthorityDbContext>
{
    public static Factory For(string connectionString) => new(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(connectionString, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options);

    public AuthorityDbContext CreateDbContext() => new(options);
}

internal sealed class OffsetClock(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().Add(offset);
}

internal sealed class NoAlerts : ISecurityAlertSink
{
    public void Raise(SecurityAlert alert)
    {
    }
}

internal sealed class CapturingAlerts : ISecurityAlertSink
{
    public List<SecurityAlert> Raised { get; } = [];

    public void Raise(SecurityAlert alert)
    {
        lock (Raised)
        {
            Raised.Add(alert);
        }
    }
}

internal sealed class Gate
{
    public SemaphoreSlim Reached { get; } = new(0);
    public SemaphoreSlim Release { get; } = new(0);
}

internal sealed class GatedFaults : IFaultInjector
{
    private string? armed;
    private Gate? gate;

    public Gate BlockAt(string faultName)
    {
        var g = new Gate();
        gate = g;
        Volatile.Write(ref armed, faultName);
        return g;
    }

    public void CrashIfArmed(string faultName)
    {
        if (Interlocked.CompareExchange(ref armed, null, faultName) == faultName && gate is { } g)
        {
            g.Reached.Release();
            g.Release.Wait(TimeSpan.FromSeconds(60));
        }
    }
}

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256, identical to the legacy agent's (so both builds verify each other's rows).</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    private int verifications;

    public int Verifications => Volatile.Read(ref verifications);

    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"carv-implementation-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
        Task.FromResult(new IntegrityMacResult(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), keyReference.KeyVersion, "HMAC-SHA-256"));

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref verifications);
        return Task.FromResult(expectedMac.Length == 32 && CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), expectedMac.Span));
    }

    public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => Task.FromResult(1);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "test"));

    public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Answers only ProbeAsync (ready); every other member throws. Used for SpiceDB and the archive in the readiness check.</summary>
public class ProbeOnlyProxy : System.Reflection.DispatchProxy
{
    public static T Create<T>()
        where T : class => Create<T, ProbeOnlyProxy>();

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "ProbeAsync" ? Task.FromResult((true, "probe-only fake")) : throw new NotSupportedException(targetMethod?.Name);
}

public class ThrowingProxy : System.Reflection.DispatchProxy
{
    public static T Create<T>()
        where T : class => Create<T, ThrowingProxy>();

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name + " is not used by the content protocol");
}

internal sealed record Staged(Fixtures.Row Row, Guid StorageKey, byte[] Bytes, ManifestItemKind Kind, Guid ItemId, byte Seed)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

/// <summary>Canonical rows shaped like the business registry (as in the design-gate harness).</summary>
internal static class Fixtures
{
    public static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();

    private static readonly RowSchemaDefinition HeaderV3 = new("form_records", 3,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("created_by_user_id", RowColumnType.Uuid, false), new("created_form_version_id", RowColumnType.Uuid, false),
        new("form_definition_id", RowColumnType.Uuid, false), new("form_relationship_definition_id", RowColumnType.Uuid, true), new("parent_form_record_id", RowColumnType.Uuid, true),
        new("record_status", RowColumnType.Text, false), new("relationship_sort_order", RowColumnType.Int64, true), new("submitted_on", RowColumnType.TimestampUtc, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    private static readonly RowSchemaDefinition StringValues = new("field_string_values", 1, new[]
    {
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false), new RowSignedColumn("field_definition_id", RowColumnType.Uuid, false),
        new RowSignedColumn("field_value_item_id", RowColumnType.Uuid, false), new RowSignedColumn("form_record_group_id", RowColumnType.Uuid, true),
        new RowSignedColumn("form_record_id", RowColumnType.Uuid, false), new RowSignedColumn("tenant_id", RowColumnType.Uuid, false), new RowSignedColumn("string_value", RowColumnType.Text, false),
    }.OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    private static readonly RowSchemaDefinition EncryptedValues = new("field_encrypted_values", 1,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("encrypted_logical_type", RowColumnType.Text, false), new("encrypted_value_bytes", RowColumnType.Bytes, false),
        new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_auth_tag_bytes", RowColumnType.Bytes, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_bytes", RowColumnType.Bytes, false), new("field_definition_id", RowColumnType.Uuid, false),
        new("field_value_item_id", RowColumnType.Uuid, false), new("form_record_group_id", RowColumnType.Uuid, true), new("form_record_id", RowColumnType.Uuid, false),
        new("tenant_id", RowColumnType.Uuid, false), new("wrapped_data_key_bytes", RowColumnType.Bytes, false),
    ]);

    public sealed record Row(ManifestItemKind Kind, Guid ItemId, RowSchemaDefinition Schema, Dictionary<string, RowValue?> Values, bool IdentityPrimaryKey, byte Seed);

    public static Staged Encode(Row row, Guid organizationId, Guid operation, long revision, byte seed)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(operation) };
        var primaryKey = row.IdentityPrimaryKey ? row.ItemId : Guid.CreateVersion7();
        var bytes = RowCanonicalizerV1.Encode(row.Schema, new RowSigningContext(1, organizationId, primaryKey, (ulong)revision), values);
        return new Staged(row, primaryKey, bytes, row.Kind, row.ItemId, row.Schema.EntityKind == "field_encrypted_values" ? row.Seed : seed);
    }

    public static Row Header(Guid org, Guid record) => new(ManifestItemKind.RecordHeader, record, HeaderV3, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["created_by_user_id"] = RowValue.Uuid(Creator), ["created_form_version_id"] = RowValue.Uuid(Version), ["form_definition_id"] = RowValue.Uuid(Form),
        ["form_relationship_definition_id"] = null, ["parent_form_record_id"] = null, ["record_status"] = RowValue.Text("Draft"),
        ["relationship_sort_order"] = null, ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(org),
    }, IdentityPrimaryKey: true, 0);

    public static Row Text(Guid org, Guid record, Guid field, Guid item, string value) => new(ManifestItemKind.FieldValue, item, StringValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = null,
        ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["string_value"] = RowValue.Text(value),
    }, IdentityPrimaryKey: false, 0);

    /// <summary>A well-formed v1 envelope; every seed yields a distinct nonce and wrapped key.</summary>
    public static Row Sealed(Guid org, Guid record, Guid field, Guid item, byte seed) => SealedCore(org, record, field, item, seed, seed);

    /// <summary>R-M2a-3: the accepted envelope's nonce and wrapped key (seed <paramref name="acceptedSeed"/>) with changed ciphertext.</summary>
    public static Row SealedReusing(Guid org, Guid record, Guid field, Guid item, byte acceptedSeed, byte ciphertextFill) => SealedCore(org, record, field, item, acceptedSeed, ciphertextFill);

    private static Row SealedCore(Guid org, Guid record, Guid field, Guid item, byte seed, byte ciphertext) => new(ManifestItemKind.FieldValue, item, EncryptedValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["encrypted_logical_type"] = RowValue.Text("String"), ["encrypted_value_bytes"] = RowValue.Bytes(Fill(24, ciphertext)), ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"),
        ["encryption_auth_tag_bytes"] = RowValue.Bytes(Fill(16, (byte)(ciphertext + 1))), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_bytes"] = RowValue.Bytes(Fill(12, (byte)(seed + 2))), ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item),
        ["form_record_group_id"] = null, ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["wrapped_data_key_bytes"] = RowValue.Bytes(Fill(60, (byte)(seed + 3))),
    }, IdentityPrimaryKey: false, seed);

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
}
