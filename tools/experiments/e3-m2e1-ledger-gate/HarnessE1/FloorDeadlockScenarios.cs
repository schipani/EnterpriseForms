// F-E1 / E-33b focused floor-deadlock verification (M2E_MODE=fe1; docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §22.14, RB-CARV-1
// amendment A-1). Runs the REAL Authority code of the build under test (M2eBinaries) against fresh databases of the same disposable
// PostgreSQL 18 container: each scenario gets its own database copied from the migrated, floorless gate database (CREATE DATABASE ...
// TEMPLATE), so every floor transition 1 -> 2 -> 3 is a genuine raise by the real --apply-content-acceptance-floor command.
//
// Controlled test-only mechanism: an EF command interceptor (FenceProbe) on a writer's DbContext observes the fence's lock statements
// and, after the fence's operations-table lock, either pauses the writer at that exact point (deterministic interleavings) or adds a
// short random delay (0-15 ms, standing in for a network round trip between two statements). It changes no SQL, no verification and
// no outcome; the statements run exactly as the build issues them. Role-level settings (deadlock_timeout, log_statement) are set per
// scenario database only, to choose which waiter runs PostgreSQL's deadlock check first and to record the floor command's statements.
// Catalog and history mutations use the container's superuser, as a privileged change or a restore would, and only touch the
// scenario databases, which are dropped afterwards.
//
// The checks state the behaviour A-1 must have. Run against a pre-A-1 build they are expected to FAIL where the deadlock occurs; that
// run is the evidence that these checks detect the defect.
using System.Data.Common;
using System.Diagnostics;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

internal sealed partial class Lab
{
    internal static readonly string Mode = Environment.GetEnvironmentVariable("M2E_MODE") ?? "gate";

    /// <summary>A-1's history lock, as a literal so the harness also builds against a pre-A-1 build (no HistoryLockSql constant).</summary>
    private const string HistoryLockText = "LOCK TABLE authority.ef_migrations_history IN ACCESS SHARE MODE";

    private static readonly string Floor1History = ContentAcceptanceFloorSql.MigrationId;
    private static readonly string Floor2History = $"{ContentAcceptanceFloorSql.MigrationId},{ContentAcceptanceFloor2Sql.MigrationId}";
    private static readonly string Floor3History = $"{Floor2History},{ContentAcceptanceFloor3Sql.MigrationId}";

    private async Task FloorDeadlockScenariosAsync()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("M2E_FE1_ITERATIONS"), out var n) && n > 0 ? n : 10;
        var a1 = typeof(ContentAcceptanceWriteFence).GetField("HistoryLockSql") is not null;
        Log($"F-E1 build under test: {(a1 ? "A-1 (history lock before the operations lock)" : "pre-A-1 (operations lock, then the facts read the history)")}; iterations {iterations}");

        var migrated = await HostCommandAsync(adminCs, "fe1-migrate", "--migrate");
        Record("FE-00", "setup: the gate database is migrated by this build's ordinary --migrate, without any floor (it is the template of every scenario database)",
            "exit 0; floors none", $"exit {migrated.Code}; floors {await FloorAsync()}", migrated.Code == 0 && await FloorAsync() == "none");

        await InDatabaseAsync("fe1_da", lab => lab.DeterministicCycleAsync("DA", floor: 3, writerChecksFirst: true));
        await InDatabaseAsync("fe1_db", lab => lab.DeterministicCycleAsync("DB", floor: 3, writerChecksFirst: false));
        await InDatabaseAsync("fe1_d2", lab => lab.DeterministicCycleAsync("D2", floor: 2, writerChecksFirst: true));
        await InDatabaseAsync("fe1_lt", lab => lab.LockTimeoutAsync());
        await InDatabaseAsync("fe1_h", lab => lab.HistoryCatalogRestoreAsync());
        for (var i = 1; i <= iterations; i++)
        {
            var tag = $"S{i:00}";
            await InDatabaseAsync("fe1_" + tag.ToLowerInvariant(), lab => lab.StressIterationAsync(tag));
        }
    }

    // ------------------------------------------------------------------------------------------------ scenario databases
    private string Database => new NpgsqlConnectionStringBuilder(adminCs).Database!;

    private string Maintenance => new NpgsqlConnectionStringBuilder(adminCs) { Database = "postgres", Pooling = false }.ConnectionString;

    private static string WithDatabase(string cs, string database) => new NpgsqlConnectionStringBuilder(cs) { Database = database }.ConnectionString;

    private Lab For(string database) => new(newBinary, carv1Agent, carv2Agent, WithDatabase(adminCs, database), WithDatabase(runtimeCs, database), evidenceDir);

    /// <summary>A physical copy of <paramref name="template"/> (also the restore model: an earlier state brought back as a database).</summary>
    private async Task CopyDatabaseAsync(string database, string template)
    {
        NpgsqlConnection.ClearAllPools();
        for (var attempt = 0; ; attempt++)
        {
            var created = await ExecAsync(Maintenance, $"CREATE DATABASE {database} TEMPLATE {template}");
            if (created == "OK")
            {
                return;
            }

            if (attempt >= 40 || !created.StartsWith("55006", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"CREATE DATABASE {database} failed: {created}");
            }

            await Task.Delay(250); // a closing backend of the template is still visible
        }
    }

    private async Task<string> DropDatabaseAsync(string database)
    {
        NpgsqlConnection.ClearAllPools();
        return await ExecAsync(Maintenance, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
    }

    private async Task InDatabaseAsync(string database, Func<Lab, Task> body)
    {
        await CopyDatabaseAsync(database, Database);
        var lab = For(database);
        try
        {
            await body(lab);
        }
        catch (Exception ex)
        {
            lab.Log("SCENARIO ABORTED: " + Describe(ex) + " " + ex);
            lab.checks.Add(new Check("HARNESS", "scenario completed", "no unexpected exception", Describe(ex), false));
        }

        // Backends flush their statistics when they exit or go idle; pg_stat_database.deadlocks counts deadlocks detected in this database.
        NpgsqlConnection.ClearAllPools();
        await Task.Delay(2500);
        var deadlocks = await ScalarAsync(Maintenance, $"SELECT deadlocks FROM pg_stat_database WHERE datname = '{database}'");
        lab.Record("DL", "PostgreSQL detected no deadlock in this database during the scenario (pg_stat_database.deadlocks)", "0", deadlocks, deadlocks == "0");
        foreach (var check in lab.checks)
        {
            checks.Add(check with { Id = database + "/" + check.Id });
        }

        lock (log)
        {
            log.Add($"---- {database}");
            log.AddRange(lab.log);
        }

        Log($"drop {database}: {await DropDatabaseAsync(database)}");
    }

    private async Task<(int Code, string Output)> RaiseAsync(int floor, string? application = null, params string[] extra) =>
        await HostCommandAsync(adminCs, application ?? $"fe1-floor{floor}", ["--apply-content-acceptance-floor", floor.ToString(System.Globalization.CultureInfo.InvariantCulture), .. extra]);

    private async Task<string> HistoryAsync() => await ScalarAsync(adminCs, FloorHistoryQuery);

    private async Task<string> LiveDefinitionAsync(int floor) => await ScalarAsync(adminCs,
        $"SELECT coalesce((SELECT pg_get_constraintdef(oid) FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname = 'ck_content_operation_rule_floor_v{floor}'), 'none')");

    private static string ExpectedDefinition(int floor) => ContentAcceptanceFloors.Find(floor)!.ExpectedDefinition;

    private async Task<long> UnequalStampsAsync() =>
        await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version IS DISTINCT FROM commit_acceptance_rule_version");

    private string ReasonsSince(int index) =>
        string.Join(",", System.Text.RegularExpressions.Regex.Matches(alerts.Since(index), "reason=([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal));

    private async Task SetupFloorsAsync(string tag, int upTo)
    {
        for (var f = 1; f <= upTo; f++)
        {
            var raised = await RaiseAsync(f);
            Record($"{tag}-setup{f}", $"setup: floor {f} raised with no concurrent writer", $"exit 0; floors {f}", $"exit {raised.Code}; floors {await FloorAsync()}",
                raised.Code == 0 && await FloorAsync() == f.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // ------------------------------------------------------------------------------------------------ lock observation
    /// <summary>The relation lock the first backend with <paramref name="application"/> waits for, once it waits (null on timeout).</summary>
    private async Task<string?> WaitForLockWaitAsync(string application, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var waiting = await ScalarAsync(adminCs, $"""
                SELECT coalesce((SELECT l.relation::regclass::text || ' ' || l.mode FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid
                 WHERE a.application_name = '{application}' AND NOT l.granted AND l.locktype = 'relation' LIMIT 1), '')
                """);
            if (waiting.Length > 0)
            {
                return waiting;
            }

            await Task.Delay(25);
        }

        return null;
    }

    /// <summary>The floor command's and the writer's locks on the two relations of F-E1, with who blocks whom (pg_blocking_pids).</summary>
    private async Task<string> LockGraphAsync(string floorApplication, string writerName) => await ScalarAsync(adminCs, $"""
        WITH parties AS (
          SELECT a.pid, CASE WHEN a.application_name = '{floorApplication}' THEN 'floor' ELSE 'writer' END AS party
            FROM pg_catalog.pg_stat_activity a
           WHERE a.datname = current_database() AND (a.application_name = '{floorApplication}' OR a.application_name LIKE 'm2e-runtime-{writerName} %'))
        SELECT coalesce(string_agg(p.party || CASE WHEN l.granted THEN ' holds ' ELSE ' WAITS FOR ' END || l.mode || ' on ' || l.relation::regclass::text
                 || CASE WHEN l.granted THEN '' ELSE ' (blocked by ' || coalesce((SELECT string_agg(b.party, ',') FROM parties b WHERE b.pid = ANY(pg_catalog.pg_blocking_pids(l.pid))), 'nobody') || ')' END,
                 '; ' ORDER BY p.party, l.granted DESC, l.relation::regclass::text), 'none')
          FROM pg_catalog.pg_locks l JOIN parties p ON p.pid = l.pid
         WHERE l.locktype = 'relation' AND l.relation IN ('authority.ef_migrations_history'::regclass, 'authority.content_change_operations'::regclass)
        """);

    private async Task<bool> MutualBlockingAsync(string floorApplication, string writerName) => await ScalarAsync(adminCs, $"""
        SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity f, pg_catalog.pg_stat_activity w
          WHERE f.application_name = '{floorApplication}' AND w.application_name LIKE 'm2e-runtime-{writerName} %'
            AND w.pid = ANY(pg_catalog.pg_blocking_pids(f.pid)) AND f.pid = ANY(pg_catalog.pg_blocking_pids(w.pid)))::text
        """) == "true";

    // ------------------------------------------------------------------------------------------------ D: the deterministic interleaving
    /// <summary>
    /// The F-E1 interleaving, made deterministic: the writer has passed the fence's operations lock and is paused there; then the floor
    /// command starts. Before A-1 the floor command takes the history lock and waits for the operations lock; the released writer then
    /// waits for the history: a cycle. <paramref name="writerChecksFirst"/> chooses which waiter runs the deadlock check while the cycle
    /// exists (each runs it once, deadlock_timeout after it starts waiting): true is E-33b's shape (the writer is refused fail closed),
    /// false makes the floor command the victim (it applies nothing). With A-1 the writer already holds the history lock, the floor
    /// command waits for it there, and no cycle can form: both complete.
    /// </summary>
    private async Task DeterministicCycleAsync(string tag, int floor, bool writerChecksFirst)
    {
        var org = await SeedOrgAsync(4);
        await SetupFloorsAsync(tag, floor - 1);
        var policy = floor == 3 ? ReleaseB2 : ReleaseB1;
        if (writerChecksFirst)
        {
            // The floor command checks after 1 s (default), before the writer waits: no cycle then. The writer checks 2 s after it
            // starts waiting, before the floor migration's 5 s lock timeout.
            await ExecAsync(adminCs, $"ALTER ROLE authority_runtime IN DATABASE {Database} SET deadlock_timeout = '2s'");
        }
        else
        {
            await ExecAsync(adminCs, $"ALTER ROLE postgres IN DATABASE {Database} SET deadlock_timeout = '3s'");
            await ExecAsync(adminCs, $"ALTER ROLE authority_runtime IN DATABASE {Database} SET deadlock_timeout = '30s'");
        }

        await ExecAsync(adminCs, $"ALTER ROLE postgres IN DATABASE {Database} SET log_statement = 'all'"); // the floor command's statement order, in the server log
        NpgsqlConnection.ClearAllPools();

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FenceProbe(async () =>
        {
            paused.TrySetResult();
            await release.Task;
        });
        var writerName = tag + "-W";
        var writer = Instance(writerName, policy, probe);
        var alertsBefore = alerts.Count;
        var write = Outcome(WriteAsync(writer, org.Records[0], Variant.Valid));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var floorApplication = "fe1-floor-" + tag.ToLowerInvariant();
        var raise = RaiseAsync(floor, floorApplication);
        var floorWaits = await WaitForLockWaitAsync(floorApplication, TimeSpan.FromSeconds(30)) ?? "never waited";
        var graphWhilePaused = await LockGraphAsync(floorApplication, writerName);
        await Task.Delay(writerChecksFirst ? 1500 : 400);
        release.TrySetResult();
        await Task.Delay(700);
        var graphAfterRelease = await LockGraphAsync(floorApplication, writerName);
        var cycle = await MutualBlockingAsync(floorApplication, writerName);
        var outcome = await write;
        var applied = await raise;
        var reasons = ReasonsSince(alertsBefore);
        Log($"{tag} lock graph while the writer is paused after the operations lock: {graphWhilePaused}");
        Log($"{tag} lock graph after the writer is released: {graphAfterRelease}; mutual blocking: {cycle}");

        var statements = string.Join(" | ", probe.LockStatements.Take(2));
        Record($"{tag}-1", "A-1: the writer's fence locks the migration history first, then the operations table (the statements the build issued)",
            $"{HistoryLockText} | {ContentAcceptanceWriteFence.LockSql}", statements, statements == $"{HistoryLockText} | {ContentAcceptanceWriteFence.LockSql}");
        Record($"{tag}-2", "with the writer inside its fence, the floor command waits at EF's migration lock on the history (ACCESS EXCLUSIVE), not holding it while it waits for the operations table",
            "authority.ef_migrations_history AccessExclusiveLock", floorWaits, floorWaits == "authority.ef_migrations_history AccessExclusiveLock");
        Record($"{tag}-3", "after the writer is released no lock cycle exists between the floor command and the writer (pg_blocking_pids)", "no mutual blocking",
            $"{(cycle ? "MUTUAL BLOCKING" : "no mutual blocking")}; {graphAfterRelease}", !cycle);
        Record($"{tag}-4", $"the paused Release B (floor {floor - 1}) write completes; no fence refusal", "OK; refusal reasons none",
            $"{outcome}; refusal reasons {(reasons.Length == 0 ? "none" : reasons)}", outcome == "OK" && reasons.Length == 0);
        var history = floor == 3 ? Floor3History : Floor2History;
        Record($"{tag}-5", $"the floor command raises floor {floor} exactly (exit 0, exact live definition, every floor recorded) and every committed operation has P = C",
            $"exit 0; floors {floor}; definition exact; history {history}; P<>C 0",
            $"exit {applied.Code}; floors {await FloorAsync()}; definition {(await LiveDefinitionAsync(floor) == ExpectedDefinition(floor) ? "exact" : "DIFFERS")}; history {await HistoryAsync()}; P<>C {await UnequalStampsAsync()}",
            applied.Code == 0 && await FloorAsync() == floor.ToString(System.Globalization.CultureInfo.InvariantCulture) && await LiveDefinitionAsync(floor) == ExpectedDefinition(floor)
            && await HistoryAsync() == history && await UnequalStampsAsync() == 0);
        if (applied.Code != 0)
        {
            // Not the expected outcome, but it must still be all-or-nothing: the lower floor and its history stay exactly as they were.
            Record($"{tag}-6", "a failed raise applies nothing (no partial commit)", $"floors {floor - 1}; history of floor {floor - 1}",
                $"floors {await FloorAsync()}; history {await HistoryAsync()}; output {(applied.Output.Length > 160 ? applied.Output[^160..] : applied.Output)}",
                await FloorAsync() == (floor - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) && await HistoryAsync() == (floor == 3 ? Floor2History : Floor1History));
        }
    }

    // ------------------------------------------------------------------------------------------------ LT: lock timeout
    /// <summary>
    /// A Release B writer stays inside its fence longer than the floor command's 5 s lock timeout. The raise must give up within a
    /// bounded time and apply nothing; a second writer arriving while the raise waits must complete after it gives up; the paused
    /// writer completes; the raise then succeeds.
    /// </summary>
    private async Task LockTimeoutAsync()
    {
        var org = await SeedOrgAsync(4);
        var other = await SeedOrgAsync(2); // the second writer's organization: no shared organization row lock with the held writer
        await SetupFloorsAsync("LT", 2);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FenceProbe(async () =>
        {
            paused.TrySetResult();
            await release.Task;
        });
        var alertsBefore = alerts.Count;
        var held = Outcome(WriteAsync(Instance("LT-W", ReleaseB2, probe), org.Records[0], Variant.Valid));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var clock = Stopwatch.StartNew();
        var raise = RaiseAsync(3, "fe1-floor-lt");
        var floorWaits = await WaitForLockWaitAsync("fe1-floor-lt", TimeSpan.FromSeconds(30)) ?? "never waited";
        var second = Instance("LT-W2", ReleaseB2);
        var secondWrite = Outcome(WriteAsync(second, other.Records[0], Variant.Valid));
        if (await Task.WhenAny(raise, Task.Delay(TimeSpan.FromSeconds(20))) != raise)
        {
            Log("LT: the raise did not give up within 20 s; releasing the writer");
            release.TrySetResult();
        }

        var refused = await raise;
        var elapsed = clock.Elapsed.TotalSeconds;
        var floorsAfter = await FloorAsync();
        var historyAfter = await HistoryAsync();
        var floor3After = await LiveDefinitionAsync(3);
        var secondOutcome = await secondWrite.WaitAsync(TimeSpan.FromSeconds(30));
        release.TrySetResult();
        var heldOutcome = await held;
        Record("LT-1", "the raise waits for the writer at the history lock and gives up after the 5 s lock timeout (bounded; dotnet start-up included)",
            "waits on authority.ef_migrations_history AccessExclusiveLock; exit 3 (55P03 lock timeout) within 4.5-10 s",
            $"waits on {floorWaits}; exit {refused.Code} ({(refused.Output.Contains("55P03", StringComparison.Ordinal) ? "55P03" : refused.Output[^Math.Min(160, refused.Output.Length)..])}) after {elapsed:0.0} s",
            floorWaits == "authority.ef_migrations_history AccessExclusiveLock" && refused.Code == 3 && refused.Output.Contains("55P03", StringComparison.Ordinal) && elapsed is >= 4.5 and <= 10);
        Record("LT-2", "the timed-out raise applied nothing (no partial commit)", $"floors 2; history {Floor2History}; no floor-3 constraint",
            $"floors {floorsAfter}; history {historyAfter}; floor-3 constraint {floor3After}", floorsAfter == "2" && historyAfter == Floor2History && floor3After == "none");
        Record("LT-3", "a writer that arrived while the raise waited, and the writer held inside its fence, both commit; no fence refusal",
            "OK; OK; refusal reasons none", $"{secondOutcome}; {heldOutcome}; refusal reasons {(ReasonsSince(alertsBefore) is { Length: > 0 } r ? r : "none")}",
            secondOutcome == "OK" && heldOutcome == "OK" && ReasonsSince(alertsBefore).Length == 0);
        var again = await RaiseAsync(3);
        Record("LT-4", "re-running the raise once no writer holds the fence succeeds", "exit 0; floors 3; history of floor 3",
            $"exit {again.Code}; floors {await FloorAsync()}; history {await HistoryAsync()}", again.Code == 0 && await FloorAsync() == "3" && await HistoryAsync() == Floor3History);
    }

    // ------------------------------------------------------------------------------------------------ H: history, catalog, restore
    private async Task HistoryCatalogRestoreAsync()
    {
        var org = await SeedOrgAsync(14);
        await SetupFloorsAsync("H", 2);
        var snapshot = Database + "_snap2";
        await CopyDatabaseAsync(snapshot, Database); // the database as it was on floor 2 (a backup taken before the raise)
        var raised = await RaiseAsync(3);
        Record("H-0", "setup: floor 3 raised after the floor-2 copy was taken", "exit 0; floors 3", $"exit {raised.Code}; floors {await FloorAsync()}", raised.Code == 0 && await FloorAsync() == "3");
        var rb3 = Instance("H-RB3", ReleaseB3);
        var rb2 = Instance("H-RB2", ReleaseB2);
        var control = $"{await Outcome(WriteAsync(rb3, org.Records[0], Variant.Valid))}; {await Outcome(WriteAsync(rb2, org.Records[1], Variant.Valid))}";
        Record("H-ok", "control: Release B writers requiring floors 2 and 3 commit on floor 3", "OK; OK", control, control == "OK; OK");

        var product = await ScalarAsync(adminCs, $"SELECT \"ProductVersion\" FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor3Sql.MigrationId}'");

        // H-1: the floor constraint present, its history row missing.
        await ExecAsync(adminCs, $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor3Sql.MigrationId}'");
        await RefusedWriteAsync("H-1a", "floor-3 constraint present but its history row missing: a Release B (floor 3) write is refused", rb3, org, org.Records[2], ContentAcceptanceFloorFailures.FloorNotRecorded);
        await RefusedWriteAsync("H-1b", "the same for a Release B (floor 2) write (every installed floor must be recorded)", rb2, org, org.Records[3], ContentAcceptanceFloorFailures.FloorNotRecorded);
        var rerun = await RaiseAsync(3, "fe1-floor3-history-missing");
        Record("H-1c", "the floor command then refuses (its preconditions require floor 2 as the only floor) and changes nothing", $"exit 3; floors 3; history {Floor2History}",
            $"exit {rerun.Code}; floors {await FloorAsync()}; history {await HistoryAsync()}", rerun.Code == 3 && await FloorAsync() == "3" && await HistoryAsync() == Floor2History);
        await ExecAsync(adminCs, $"INSERT INTO authority.ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('{ContentAcceptanceFloor3Sql.MigrationId}', '{product}')");
        var resumed = await Outcome(WriteAsync(rb3, org.Records[2], Variant.Valid));
        Record("H-1d", "with the history row restored the floor-3 writer commits again", "OK", resumed, resumed == "OK");

        // H-2: a corrupted history row (a floor 2 id altered).
        await ExecAsync(adminCs, $"UPDATE authority.ef_migrations_history SET \"MigrationId\" = \"MigrationId\" || '-corrupted' WHERE \"MigrationId\" = '{ContentAcceptanceFloor2Sql.MigrationId}'");
        await RefusedWriteAsync("H-2", "corrupted migration history (the floor-2 row altered): a Release B (floor 3) write is refused", rb3, org, org.Records[4], ContentAcceptanceFloorFailures.FloorNotRecorded);
        await ExecAsync(adminCs, $"UPDATE authority.ef_migrations_history SET \"MigrationId\" = '{ContentAcceptanceFloor2Sql.MigrationId}' WHERE \"MigrationId\" = '{ContentAcceptanceFloor2Sql.MigrationId}-corrupted'");

        // H-3: the E3-M2b-1 fence migration row missing.
        var fenceProduct = await ScalarAsync(adminCs, $"SELECT \"ProductVersion\" FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceRuleSql.MigrationId}'");
        await ExecAsync(adminCs, $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceRuleSql.MigrationId}'");
        await RefusedWriteAsync("H-3", "the E3-M2b-1 fence migration missing from the history: refused", rb3, org, org.Records[5], ContentAcceptanceFloorFailures.FenceMigrationNotRecorded);
        await ExecAsync(adminCs, $"INSERT INTO authority.ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('{ContentAcceptanceRuleSql.MigrationId}', '{fenceProduct}')");

        // H-4: the history table itself missing (the fence's first lock, or its read, cannot be taken).
        await ExecAsync(adminCs, "ALTER TABLE authority.ef_migrations_history RENAME TO ef_migrations_history_moved");
        await RefusedWriteAsync("H-4", "the migration history table missing: refused fail closed", rb3, org, org.Records[6], ContentAcceptanceFloorFailures.FloorUnverifiable);
        await ExecAsync(adminCs, "ALTER TABLE authority.ef_migrations_history_moved RENAME TO ef_migrations_history");
        var back = await Outcome(WriteAsync(rb3, org.Records[6], Variant.Valid));
        Record("H-4b", "with the history table back the floor-3 writer commits", "OK", back, back == "OK");

        // H-5: floor recorded but missing from the catalog.
        await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3");
        await RefusedWriteAsync("H-5a", "floor 3 recorded but its constraint missing: refused", rb3, org, org.Records[7], ContentAcceptanceFloorFailures.FloorRecordedButMissing);
        var exit4 = await RaiseAsync(3, "fe1-floor3-missing");
        var reinstated = await RaiseAsync(3, "fe1-floor3-reinstate", "--reinstate-recorded-floor");
        Record("H-5b", "the floor command reports it (exit 4) and reinstates it only with the flag", "exit 4; exit 0; floors 3; history of floor 3",
            $"exit {exit4.Code}; exit {reinstated.Code}; floors {await FloorAsync()}; history {await HistoryAsync()}",
            exit4.Code == 4 && reinstated.Code == 0 && await FloorAsync() == "3" && await HistoryAsync() == Floor3History);

        // H-6: the database restored to its floor-2 copy.
        var restored = For(snapshot);
        var restoredRb3 = restored.Instance("H-R-RB3", ReleaseB3);
        await restored.RefusedWriteAsync("H-6a", "database restored to the earlier floor-2 state: a Release B instance requiring floor 3 refuses (the restore lowered the floor)",
            restoredRb3, org, org.Records[10], ContentAcceptanceFloorFailures.FloorLowered);
        var reraised = await restored.RaiseAsync(3, "fe1-floor3-restored");
        var restoredWrite = await Outcome(restored.WriteAsync(restoredRb3, org.Records[11], Variant.Valid));
        restored.Record("H-6b", "on the restored database the floor is raised again by the command; the floor-3 writer then commits", "exit 0; floors 3; OK",
            $"exit {reraised.Code}; floors {await restored.FloorAsync()}; {restoredWrite}",
            reraised.Code == 0 && await restored.FloorAsync() == "3" && restoredWrite == "OK");
        foreach (var check in restored.checks)
        {
            checks.Add(check with { Id = "restored/" + check.Id });
        }

        lock (log)
        {
            log.AddRange(restored.log.Select(l => "restored: " + l));
        }

        NpgsqlConnection.ClearAllPools();
        await Task.Delay(2500);
        var restoredDeadlocks = await ScalarAsync(Maintenance, $"SELECT deadlocks FROM pg_stat_database WHERE datname = '{snapshot}'");
        Record("H-6c", "no deadlock in the restored database", "0", restoredDeadlocks, restoredDeadlocks == "0");
        Log($"drop {snapshot}: {await DropDatabaseAsync(snapshot)}");

        // H-7: the older CARV-2 build (a1127a1) cannot prepare after floor 3.
        var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
        var legacy = await LegacyPrepareAsync(carv2Agent, org.Records[12], Variant.Valid);
        Record("H-7", "the CARV-2 build (a1127a1) is refused by the floor-3 CHECK; nothing persisted", "refused (ck_content_operation_rule_floor_v3); no operation",
            $"{(legacy.Outcome.Length > 200 ? legacy.Outcome[..200] : legacy.Outcome)}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}",
            legacy.Outcome != "OK" && legacy.Outcome.Contains("ck_content_operation_rule_floor_v3", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations"));
        Record("H-8", "end state: P = C for every committed operation; floor 3 exact", "P<>C 0; definition exact",
            $"P<>C {await UnequalStampsAsync()}; definition {(await LiveDefinitionAsync(3) == ExpectedDefinition(3) ? "exact" : "DIFFERS")}",
            await UnequalStampsAsync() == 0 && await LiveDefinitionAsync(3) == ExpectedDefinition(3));
    }

    // ------------------------------------------------------------------------------------------------ S: repeated concurrency
    private sealed record Timed(string Outcome, DateTimeOffset Finished, string Kind);

    private async Task<List<Timed>> LoopAsync(AuthorityNode node, RecordModel record, string kind, Func<bool> stopped)
    {
        var results = new List<Timed>();
        for (var n = 0; n < 400 && !stopped(); n++)
        {
            results.Add(new Timed(await Outcome(WriteAsync(node, record, Variant.Valid)), DateTimeOffset.UtcNow, kind));
        }

        return results;
    }

    /// <summary>Runs <paramref name="command"/> while <paramref name="writers"/> write in a loop, from 400 ms before to 500 ms after it.</summary>
    private async Task<(int Code, List<Timed> Writes, DateTimeOffset Start, DateTimeOffset End, string Reasons)> UnderLoadAsync(Func<Task<(int Code, string Output)>> command, IReadOnlyList<(AuthorityNode Node, RecordModel Record, string Kind)> writers)
    {
        var stop = false;
        var alertsBefore = alerts.Count;
        var loads = writers.Select(w => Task.Run(() => LoopAsync(w.Node, w.Record, w.Kind, () => Volatile.Read(ref stop)))).ToList();
        await Task.Delay(400);
        var start = DateTimeOffset.UtcNow;
        var result = await command();
        var end = DateTimeOffset.UtcNow;
        await Task.Delay(500);
        Volatile.Write(ref stop, true);
        var writes = (await Task.WhenAll(loads)).SelectMany(x => x).ToList();
        return (result.Code, writes, start, end, ReasonsSince(alertsBefore));
    }

    private static string Buckets(List<Timed> writes, DateTimeOffset start, DateTimeOffset end) =>
        $"before {writes.Count(w => w.Finished < start)}, during {writes.Count(w => w.Finished >= start && w.Finished <= end)}, after {writes.Count(w => w.Finished > end)}";

    private static bool Covered(List<Timed> writes, DateTimeOffset start, DateTimeOffset end) =>
        writes.Any(w => w.Finished < start) && writes.Any(w => w.Finished >= start && w.Finished <= end) && writes.Any(w => w.Finished > end);

    private static string Summary(List<Timed> writes) =>
        string.Join(", ", writes.GroupBy(w => (w.Kind, w.Outcome)).OrderBy(g => g.Key.Kind, StringComparer.Ordinal).Select(g => $"{g.Key.Kind} {(g.Key.Outcome == "OK" ? "OK" : g.Key.Outcome)} x{g.Count()}"));

    private async Task StressIterationAsync(string tag)
    {
        // Four organizations, writers assigned round-robin: writers of different organizations are inside their fences at the same
        // time (writers of one organization serialize on its row lock, as in production).
        var orgs = new List<Org>();
        for (var o = 0; o < 4; o++)
        {
            orgs.Add(await SeedOrgAsync(8));
        }

        FenceProbe Latency() => new(null, maxDelayMilliseconds: 15);
        var next = 0;
        List<(AuthorityNode, RecordModel, string)> Writers(string prefix, ContentAcceptancePolicy policy, int count, bool latency) =>
            Enumerable.Range(0, count).Select(i =>
            {
                var slot = next++;
                return (Instance($"{tag}-{prefix}{i}", policy, latency ? Latency() : null), orgs[slot % 4].Records[slot / 4], prefix);
            }).ToList();

        // Floor 1 under Release A writers (no fence) and Release B (floor 1) writers, which are refused only until floor 1 exists.
        var ra = Writers("RA", ReleaseA, 4, latency: false);
        var rb1Early = Writers("RB1e", ReleaseB1, 2, latency: true);
        var f1 = await UnderLoadAsync(() => RaiseAsync(1), [.. ra, .. rb1Early]);
        var rbEarly = f1.Writes.Where(w => w.Kind == "RB1e").ToList();
        Record("F1", "floor 1 raised while Release A writers and Release B (floor 1) writers run: exit 0, floor 1 exact; every write commits or (before floor 1 exists) is refused FloorNotInstalled, never FloorUnverifiable",
            "exit 0; floors 1; definition exact; outcomes within OK / FloorNotEnforced; reasons within FloorNotInstalled; some OK after the raise",
            $"exit {f1.Code}; floors {await FloorAsync()}; definition {(await LiveDefinitionAsync(1) == ExpectedDefinition(1) ? "exact" : "DIFFERS")}; {Summary(f1.Writes)}; reasons {(f1.Reasons.Length == 0 ? "none" : f1.Reasons)}; {Buckets(f1.Writes, f1.Start, f1.End)}",
            f1.Code == 0 && await FloorAsync() == "1" && await LiveDefinitionAsync(1) == ExpectedDefinition(1)
            && f1.Writes.Where(w => w.Kind == "RA").All(w => w.Outcome == "OK") && rbEarly.All(w => w.Outcome == "OK" || w.Outcome == FloorNotEnforced)
            && (f1.Reasons.Length == 0 || f1.Reasons == ContentAcceptanceFloorFailures.FloorNotInstalled)
            && rbEarly.Any(w => w.Outcome == "OK" && w.Finished > f1.End));

        var f2 = await UnderLoadAsync(() => RaiseAsync(2), Writers("RB1-", ReleaseB1, 8, latency: true));
        Record("F2", "floor 1 -> 2 raised while eight Release B (floor 1) writers run before, during and after: exit 0, floor 2 exact and recorded; every write commits",
            $"exit 0; floors 2; definition exact; history {Floor2History}; all OK; writes before, during and after",
            $"exit {f2.Code}; floors {await FloorAsync()}; definition {(await LiveDefinitionAsync(2) == ExpectedDefinition(2) ? "exact" : "DIFFERS")}; history {await HistoryAsync()}; {Summary(f2.Writes)}; reasons {(f2.Reasons.Length == 0 ? "none" : f2.Reasons)}; {Buckets(f2.Writes, f2.Start, f2.End)}",
            f2.Code == 0 && await FloorAsync() == "2" && await LiveDefinitionAsync(2) == ExpectedDefinition(2) && await HistoryAsync() == Floor2History
            && f2.Writes.Count > 0 && f2.Writes.All(w => w.Outcome == "OK") && Covered(f2.Writes, f2.Start, f2.End));

        var f3 = await UnderLoadAsync(() => RaiseAsync(3), Writers("RB2-", ReleaseB2, 8, latency: true));
        Record("F3", "floor 2 -> 3 raised while eight Release B (floor 2) writers run before, during and after (E-33 / E-33b): exit 0, floor 2 dropped, floor 3 exact and recorded; every write commits",
            $"exit 0; floors 3; definition exact; history {Floor3History}; all OK; writes before, during and after",
            $"exit {f3.Code}; floors {await FloorAsync()}; definition {(await LiveDefinitionAsync(3) == ExpectedDefinition(3) ? "exact" : "DIFFERS")}; history {await HistoryAsync()}; {Summary(f3.Writes)}; reasons {(f3.Reasons.Length == 0 ? "none" : f3.Reasons)}; {Buckets(f3.Writes, f3.Start, f3.End)}",
            f3.Code == 0 && await FloorAsync() == "3" && await LiveDefinitionAsync(3) == ExpectedDefinition(3) && await HistoryAsync() == Floor3History
            && f3.Writes.Count > 0 && f3.Writes.All(w => w.Outcome == "OK") && Covered(f3.Writes, f3.Start, f3.End));

        var migrate = await UnderLoadAsync(() => HostCommandAsync(adminCs, $"fe1-migrate-{tag.ToLowerInvariant()}", "--migrate"), Writers("RB3-", ReleaseB3, 4, latency: true));
        // F-E2 (observed, out of scope): after its migrations --migrate re-applies the runtime grants one autocommit statement at a time,
        // starting with REVOKE ALL ON ALL TABLES, so a concurrent write can be denied (42501, or FloorUnverifiable when the fence's own
        // lock or read is denied). Those refusals fail closed; they are classified here and reported, not treated as F-E1. Deadlocks
        // are counted by the DL check of this database.
        static bool GrantWindow(string outcome) => outcome == FloorNotEnforced || outcome.Contains("PostgresException 42501", StringComparison.Ordinal);
        var otherReasons = migrate.Reasons.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(r => r != ContentAcceptanceFloorFailures.FloorUnverifiable).ToList();
        Record("FM", "an ordinary --migrate (nothing pending; it still takes EF's migration lock) while Release B (floor 3) writers run: exit 0, floor 3 kept; every write commits or is refused fail closed in the grant re-application window (F-E2)",
            "exit 0; floors 3; outcomes within OK / 42501 / FloorNotEnforced (FloorUnverifiable only); some OK",
            $"exit {migrate.Code}; floors {await FloorAsync()}; {Summary(migrate.Writes)}; reasons {(migrate.Reasons.Length == 0 ? "none" : migrate.Reasons)}",
            migrate.Code == 0 && await FloorAsync() == "3" && migrate.Writes.Any(w => w.Outcome == "OK") && migrate.Writes.All(w => w.Outcome == "OK" || GrantWindow(w.Outcome)) && otherReasons.Count == 0);

        Record("PC", "every committed operation has P = C (CARV Prepare/Commit equality)", "0", (await UnequalStampsAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture), await UnequalStampsAsync() == 0);
    }
}

/// <summary>
/// F-E1 test-only probe: records the LOCK statements the store issues through EF and, after the fence's operations-table lock, either
/// pauses once (<paramref name="pauseOnce"/>) or waits a random 0..<paramref name="maxDelayMilliseconds"/> ms. It never changes a
/// statement or a result. The facts statement runs on the raw connection, outside EF, so it is not intercepted.
/// </summary>
internal sealed class FenceProbe(Func<Task>? pauseOnce, int maxDelayMilliseconds = 0) : DbCommandInterceptor
{
    private Func<Task>? pause = pauseOnce;

    public System.Collections.Concurrent.ConcurrentQueue<string> LockStatements { get; } = new();

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith("LOCK TABLE", StringComparison.Ordinal))
        {
            LockStatements.Enqueue(command.CommandText);
        }

        if (command.CommandText == ContentAcceptanceWriteFence.LockSql)
        {
            if (Interlocked.Exchange(ref pause, null) is { } hook)
            {
                await hook();
            }
            else if (maxDelayMilliseconds > 0)
            {
                await Task.Delay(Random.Shared.Next(maxDelayMilliseconds + 1), cancellationToken);
            }
        }

        return result;
    }
}
