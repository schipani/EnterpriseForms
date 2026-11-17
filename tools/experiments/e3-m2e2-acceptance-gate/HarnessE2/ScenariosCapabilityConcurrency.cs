// E3-M2f-2G group GC (docs/security/E3_M2F2G_VERIFICATION_RECORD.md; closure §4.2 FB-1, M2F-34): the capability decision and the first
// data-key binding of an organization on REAL PostgreSQL, with transactions held open at exact statements (PauseAfter) and every wait read
// from pg_stat_activity / pg_locks. This tree's Authority in-process; acceptance on only inside this run.
using AuthorizationAuthority.Contracts.V1;

internal sealed partial class Lab
{
    /// <summary>Waits until a session of <paramref name="applicationName"/> waits on a heavyweight lock; returns the evidence (or "NO WAIT").</summary>
    private async Task<string> AwaitLockWaitAsync(string applicationName, string blockerApplicationName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var rows = await RowsAsync(adminCs, $"""
                SELECT a.pid, a.wait_event_type, a.wait_event, left(regexp_replace(a.query, '\s+', ' ', 'g'), 90),
                       (SELECT string_agg(b.application_name, ',') FROM pg_stat_activity b WHERE b.pid = ANY (pg_blocking_pids(a.pid)))
                FROM pg_stat_activity a WHERE a.application_name LIKE '{applicationName}%' AND a.wait_event_type = 'Lock'
                """);
            if (rows.Count > 0)
            {
                var r = rows[0];
                var blockedBy = r[4] ?? "NULL";
                return $"waits {r[1]}/{r[2]} on [{r[3]}] blocked by [{blockedBy}]{(blockedBy.Contains(blockerApplicationName, StringComparison.Ordinal) ? " (the paused transaction)" : " (NOT the paused transaction)")}";
            }

            await Task.Delay(100);
        }

        return "NO WAIT";
    }

    /// <summary>The heavyweight locks a session of <paramref name="applicationName"/> holds (relation:mode, tuple:mode, ...), sorted.</summary>
    private async Task<string> HeldLocksAsync(string applicationName) => await ScalarAsync(adminCs, $"""
        SELECT coalesce(string_agg(DISTINCT CASE WHEN l.locktype = 'relation' THEN l.relation::regclass::text ELSE l.locktype END || ':' || l.mode, ', '), 'none')
        FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
        WHERE a.application_name LIKE '{applicationName}%' AND l.granted AND l.locktype IN ('relation', 'tuple', 'transactionid')
          AND NOT (l.locktype = 'relation' AND l.relation::regclass::text LIKE 'pg_%')
        """);

    private async Task<long> DeadlockCounterAsync() => await CountAsync("SELECT coalesce(sum(deadlocks), 0) FROM pg_stat_database");

    private async Task<Org> SeedV1OrgAsync(int records, GNode node)
    {
        var org = await SeedOrgAsync(records, localKeys!, 1, journalDigest: true);
        foreach (var r in org.Records)
        {
            var w = await WriteAsync(AsNode(node), r, Owner(r, Rows.V1Field(org.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), (byte)(20 + org.Records.IndexOf(r)))));
            if (w != "OK")
            {
                throw new InvalidOperationException("v1 seed write failed: " + w);
            }
        }

        await VerifyArchivedContentStreamAsync(org, PrimaryDatabase);
        return org;
    }

    private static bool InsertsBinding(string sql) => sql.Contains("INSERT INTO authority.content_data_key_bindings", StringComparison.Ordinal);

    private static bool ExistenceQuery(string sql) => sql.Contains("content_data_key_bindings", StringComparison.Ordinal) && sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase);

    private async Task CapabilityConcurrencyScenariosAsync()
    {
        CurrentGroup = "GC";
        var setup = GInstance("GCsetup");
        var deadlocksBefore = await DeadlockCounterAsync();

        // GC-01 (M2F-34): the first binding's Prepare holds the organization lock with its binding inserted; a level-1 Verify waits on that
        // lock (not on the ledger), and after the Prepare commits it sees the binding and is refused.
        Org fbA;
        {
            fbA = await SeedV1OrgAsync(3, setup);
            var pause = new PauseAfter(InsertsBinding);
            var p = GInstance("GC01P", pause: pause);
            var v = GInstance("GC01V");
            var target = fbA.Records[0];
            var prepare = PrepareAsync(AsNode(p), target, Owner(target, (await BatchAsync(target)).Seal("first-binding")));
            var reached = await Task.WhenAny(pause.Reached.Task, Task.Delay(TimeSpan.FromSeconds(30))) == pause.Reached.Task;
            var held = await HeldLocksAsync(p.ApplicationName);
            var r1 = fbA.Records[1];
            var h1 = await HeadIdentityAsync(r1);
            var verify = CallAsync(v, new GCase(Entry.Verify, r1, h1.Operation, h1.Revision), r1.AcceptedStaged(), BusinessBuild.Undeclared);
            var wait = await AwaitLockWaitAsync(v.ApplicationName, p.ApplicationName, TimeSpan.FromSeconds(15));
            var verifyStillOpen = !verify.IsCompleted;
            var uncommittedVisible = await BindingCountAsync($"organization_id = '{fbA.Id}'");
            pause.Release.TrySetResult();
            var prepared = await prepare;
            var outcome = await verify;
            Record("GC-01", "Security", "M2F-34 FB-1 on real PostgreSQL (binding committed): while the first-binding Prepare holds the organization lock with its binding inserted but uncommitted, a level-1 Verify of another record WAITS on the organization lock (pg_stat_activity Lock wait, blocked by that Prepare) and cannot decide; after the Prepare commits, the Verify sees the binding and is refused",
                $"paused at the binding insert; Prepare holds the org row and the ledger; Verify waits (blocked by the paused transaction); binding invisible to others while uncommitted; Prepare OK; Verify {Refused}",
                $"paused {reached}; Prepare holds [{held}]; {wait}; verify open {verifyStillOpen}; visible bindings while paused {uncommittedVisible}; Prepare {prepared.Outcome}; Verify {outcome}; bindings now {await BindingCountAsync($"organization_id = '{fbA.Id}'")}",
                reached && held.Contains("content_data_key_bindings", StringComparison.Ordinal) && held.Contains("organization_authorization_states", StringComparison.Ordinal) && wait.EndsWith("(the paused transaction)", StringComparison.Ordinal)
                && verifyStillOpen && uncommittedVisible == 0 && prepared.Outcome == "OK" && outcome == Refused);
        }

        // GC-02: the same race, but the first-binding Prepare rolls back after its insert: the waiting Verify decides "pre-binding" and is admitted.
        {
            var fbB = await SeedV1OrgAsync(3, setup);
            // Held after the binding insert; then its COMMIT fails before it is sent (FailCommit), so the transaction rolls back cleanly.
            var pause = new PauseAfter(InsertsBinding);
            var p = GInstance("GC02P", pause: pause, extra: new FailCommit());
            var v = GInstance("GC02V");
            var target = fbB.Records[0];
            var prepare = PrepareAsync(AsNode(p), target, Owner(target, (await BatchAsync(target)).Seal("rolled-back-binding")));
            var reached = await Task.WhenAny(pause.Reached.Task, Task.Delay(TimeSpan.FromSeconds(30))) == pause.Reached.Task;
            var r1 = fbB.Records[1];
            var h1 = await HeadIdentityAsync(r1);
            var verify = CallAsync(v, new GCase(Entry.Verify, r1, h1.Operation, h1.Revision), r1.AcceptedStaged(), BusinessBuild.Undeclared);
            var wait = await AwaitLockWaitAsync(v.ApplicationName, p.ApplicationName, TimeSpan.FromSeconds(15));
            pause.Release.TrySetResult();
            var prepared = await prepare;
            var outcome = await verify;
            Record("GC-02", "Security", "M2F-34 FB-1 (binding rolled back): the first-binding Prepare fails after inserting its binding and rolls back; the Verify that waited on the organization lock then sees no binding and is admitted (pre-binding), and no binding or operation survives",
                "paused; Verify waits (blocked by the paused transaction); Prepare fails; Verify OK: verified True; bindings 0; operations of the organization unchanged",
                $"paused {reached}; {wait}; Prepare {prepared.Outcome}; Verify {outcome}; bindings {await BindingCountAsync($"organization_id = '{fbB.Id}'")}; operations {await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id = '{fbB.Id}' AND form_record_id = '{target.Id}' AND operation_state_name <> 'Committed'")}",
                reached && wait.EndsWith("(the paused transaction)", StringComparison.Ordinal) && prepared.Outcome != "OK" && outcome.StartsWith("OK: verified True", StringComparison.Ordinal)
                && await BindingCountAsync($"organization_id = '{fbB.Id}'") == 0
                && await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id = '{fbB.Id}' AND form_record_id = '{target.Id}' AND operation_state_name <> 'Committed'") == 0);
        }

        // GC-03: the reverse order: a level-1 Verify holds the organization lock just after its existence query (no binding yet) and the
        // first-binding Prepare waits for it. The Verify is admitted (pre-binding); the Prepare then binds; the same node is refused afterwards
        // (nothing is cached: no stale negative), and so is a second node.
        {
            var fbC = await SeedV1OrgAsync(3, setup);
            var pause = new PauseAfter(ExistenceQuery);
            var v = GInstance("GC03V", pause: pause);
            var p = GInstance("GC03P");
            var r1 = fbC.Records[1];
            var h1 = await HeadIdentityAsync(r1);
            var c = new GCase(Entry.Verify, r1, h1.Operation, h1.Revision);
            var verify = CallAsync(v, c, r1.AcceptedStaged(), BusinessBuild.Undeclared);
            var reached = await Task.WhenAny(pause.Reached.Task, Task.Delay(TimeSpan.FromSeconds(30))) == pause.Reached.Task;
            var vLocks = await HeldLocksAsync(v.ApplicationName);
            var target = fbC.Records[0];
            var prepare = PrepareAsync(AsNode(p), target, Owner(target, (await BatchAsync(target)).Seal("after-verify")));
            var wait = await AwaitLockWaitAsync(p.ApplicationName, v.ApplicationName, TimeSpan.FromSeconds(15));
            pause.Release.TrySetResult();
            var first = await verify;
            var prepared = await prepare;
            var commit = prepared.Outcome == "OK" ? (await CommitAsync(AsNode(p), target, prepared)).Outcome : "not prepared";
            var again = await CallAsync(v, c, r1.AcceptedStaged(), BusinessBuild.Undeclared);
            var other = await CallAsync(GInstance("GC03W"), c, r1.AcceptedStaged(), BusinessBuild.Undeclared);
            Record("GC-03", "Security", "M2F-34 FB-1 (reverse order) and no stale negative: a level-1 Verify paused right after its existence query holds the organization row lock and only an ACCESS SHARE lock on the ledger; the first-binding Prepare waits for it; the Verify is admitted (pre-binding); after the Prepare binds and commits, the SAME node and a second node are both refused",
                $"Verify holds organization_authorization_states and content_data_key_bindings:AccessShareLock only; Prepare waits (blocked by the paused Verify); Verify OK: verified True; Prepare/Commit OK; same node {Refused}; other node {Refused}",
                $"paused {reached}; Verify holds [{vLocks}]; {wait}; Verify {first}; Prepare {prepared.Outcome}; Commit {commit}; same node {again}; other node {other}",
                reached && vLocks.Contains("organization_authorization_states", StringComparison.Ordinal) && vLocks.Contains("content_data_key_bindings:AccessShareLock", StringComparison.Ordinal)
                && !vLocks.Contains("content_data_key_bindings:RowExclusiveLock", StringComparison.Ordinal) && wait.EndsWith("(the paused transaction)", StringComparison.Ordinal)
                && first.StartsWith("OK: verified True", StringComparison.Ordinal) && prepared.Outcome == "OK" && commit == "OK" && again == Refused && other == Refused);
        }

        // GC-04 (closure §4.2 point 4): a level-1 Prepare admitted before the first binding; its Commit waits on the lock held by the
        // first-binding Prepare and is then refused; the operation stays Prepared, the head does not move; a current build can still commit it.
        {
            var fbD = await SeedV1OrgAsync(3, setup);
            var n = GInstance("GC04N");
            var r1 = fbD.Records[1];
            var legacyPrepared = await PrepareAsync(AsNode(n), r1, Owner(r1, Rows.V1Field(fbD.Id, r1.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 77)), build: BusinessBuild.Undeclared);
            var pause = new PauseAfter(InsertsBinding);
            var p = GInstance("GC04P", pause: pause);
            var target = fbD.Records[0];
            var prepare = PrepareAsync(AsNode(p), target, Owner(target, (await BatchAsync(target)).Seal("first-binding-commit-race")));
            var reached = await Task.WhenAny(pause.Reached.Task, Task.Delay(TimeSpan.FromSeconds(30))) == pause.Reached.Task;
            var headBefore = await HeadStateAsync(r1);
            var commitTask = CommitAsync(AsNode(GInstance("GC04C")), r1, legacyPrepared, build: BusinessBuild.Undeclared);
            var wait = await AwaitLockWaitAsync("m2e2-runtime-GC04C", p.ApplicationName, TimeSpan.FromSeconds(15));
            pause.Release.TrySetResult();
            var prepared = await prepare;
            var legacyCommit = await commitTask;
            var stateAfter = await StateAsync(legacyPrepared.OperationId);
            var headAfter = await HeadStateAsync(r1);
            var currentCommit = await CommitAsync(AsNode(n), r1, legacyPrepared, build: BusinessBuild.Current);
            Record("GC-04", "Security", "closure §4.2 point 4 on real PostgreSQL: a level-1 Prepare admitted in an unbound organization; its level-1 Commit waits on the organization lock of the concurrent first-binding Prepare and, once that commits, is refused: the operation stays Prepared (not aborted) and the head does not move. A current build then commits the same operation normally",
                $"legacy Prepare OK (P = 3); Commit waits (blocked by the paused transaction); first-binding Prepare OK; legacy Commit {Refused}; still 3/NULL Prepared; head unchanged; current Commit OK",
                $"legacy Prepare {legacyPrepared.Outcome}; paused {reached}; {wait}; first-binding {prepared.Outcome}; legacy Commit {legacyCommit.Outcome}; state {stateAfter}; head {Diff(headBefore, headAfter)}; current Commit {currentCommit.Outcome}",
                legacyPrepared.Outcome == "OK" && reached && wait.EndsWith("(the paused transaction)", StringComparison.Ordinal) && prepared.Outcome == "OK" && legacyCommit.Outcome == Refused
                && stateAfter == "3/NULL Prepared -" && headBefore == headAfter && currentCommit.Outcome == "OK");
        }

        // GC-05: the organization lock is per organization: while a first-binding Prepare of one organization is paused holding its lock,
        // a level-1 Verify in another (unbound) organization completes at once (admitted) and one in a bound organization completes at once (refused).
        {
            var fbE = await SeedV1OrgAsync(2, setup);
            var fbF = await SeedV1OrgAsync(2, setup);
            var pause = new PauseAfter(InsertsBinding);
            var p = GInstance("GC05P", pause: pause);
            var target = fbE.Records[0];
            var prepare = PrepareAsync(AsNode(p), target, Owner(target, (await BatchAsync(target)).Seal("isolation")));
            var reached = await Task.WhenAny(pause.Reached.Task, Task.Delay(TimeSpan.FromSeconds(30))) == pause.Reached.Task;
            var other = fbF.Records[0];
            var ho = await HeadIdentityAsync(other);
            var bound = fbA.Records[1];
            var hb = await HeadIdentityAsync(bound);
            var v = GInstance("GC05V");
            var unboundVerify = CallAsync(v, new GCase(Entry.Verify, other, ho.Operation, ho.Revision), other.AcceptedStaged(), BusinessBuild.Undeclared);
            var boundVerify = CallAsync(GInstance("GC05W"), new GCase(Entry.Verify, bound, hb.Operation, hb.Revision), bound.AcceptedStaged(), BusinessBuild.Undeclared);
            await Task.WhenAny(Task.WhenAll(unboundVerify, boundVerify), Task.Delay(TimeSpan.FromSeconds(15)));
            var finished = unboundVerify.IsCompleted && boundVerify.IsCompleted;
            var whilePaused = !prepare.IsCompleted;
            pause.Release.TrySetResult();
            var prepared = await prepare;
            Record("GC-05", "Security", "CAP-F7 / M2F-09 isolation on real PostgreSQL: while another organization's first-binding Prepare is paused holding its lock, a level-1 Verify in an unbound organization is admitted and one in a bound organization is refused, both without waiting",
                "both completed while the other organization's Prepare was still paused; unbound OK: verified True; bound " + Refused,
                $"paused {reached}; both finished {finished}; Prepare still paused then {whilePaused}; unbound {await unboundVerify}; bound {await boundVerify}; Prepare {prepared.Outcome}",
                reached && finished && whilePaused && (await unboundVerify).StartsWith("OK: verified True", StringComparison.Ordinal) && await boundVerify == Refused && prepared.Outcome == "OK");
        }

        // GC-06: mixed concurrent stress across two organizations (one bound, one unbound): level-2 v2 writes (new bindings), level-1 and
        // level-2 Verifies, level-1 v1 writes; every outcome is in its expected set; no deadlock, no lock timeout.
        {
            var sA = await SeedV1OrgAsync(14, setup);
            var sB = await SeedV1OrgAsync(14, setup);
            var first = await WriteAsync(AsNode(setup), sA.Records[0], Owner(sA.Records[0], (await BatchAsync(sA.Records[0])).Seal("stress-first")));
            await VerifyArchivedContentStreamAsync(sA, PrimaryDatabase);
            var outcomes = new List<(string Kind, string Outcome)>();
            for (var round = 0; round < 4; round++)
            {
                var a = sA.Records[1 + round];
                var b = sB.Records[1 + round];
                var readA = sA.Records[10];
                var readB = sB.Records[10];
                var ha = await HeadIdentityAsync(readA);
                var hb = await HeadIdentityAsync(readB);
                var batch = await BatchAsync(a);
                var tasks = new List<Task<(string, string)>>
                {
                    Tag("A v2 write (L2)", WriteAsync(AsNode(GInstance($"GC06a{round}")), a, Owner(a, batch.Seal("stress-" + round)))),
                    Tag("A verify (L1)", CallAsync(GInstance($"GC06b{round}"), new GCase(Entry.Verify, readA, ha.Operation, ha.Revision), readA.AcceptedStaged(), BusinessBuild.Undeclared)),
                    Tag("A verify (L2)", CallAsync(GInstance($"GC06c{round}"), new GCase(Entry.Verify, readA, ha.Operation, ha.Revision), readA.AcceptedStaged(), BusinessBuild.Current)),
                    Tag("B v1 write (L1)", WriteAsync(AsNode(GInstance($"GC06d{round}")), b, Owner(b, Rows.V1Field(sB.Id, b.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), (byte)(90 + round))), build: BusinessBuild.Undeclared)),
                    Tag("B verify (L1)", CallAsync(GInstance($"GC06e{round}"), new GCase(Entry.Verify, readB, hb.Operation, hb.Revision), readB.AcceptedStaged(), BusinessBuild.Undeclared)),
                    Tag("A v1 write (L1)", WriteAsync(AsNode(GInstance($"GC06f{round}")), sA.Records[6 + round], Owner(sA.Records[6 + round], Rows.V1Field(sA.Id, sA.Records[6 + round].Id, Guid.CreateVersion7(), Guid.CreateVersion7(), (byte)(110 + round))), build: BusinessBuild.Undeclared)),
                };
                outcomes.AddRange(await Task.WhenAll(tasks));
                await VerifyArchivedContentStreamAsync(sA, PrimaryDatabase);
                await VerifyArchivedContentStreamAsync(sB, PrimaryDatabase);
            }

            bool Expected((string Kind, string Outcome) o) => o.Kind switch
            {
                "A v2 write (L2)" or "B v1 write (L1)" => o.Outcome == "OK",
                "A verify (L1)" => o.Outcome == Refused,
                "A v1 write (L1)" => o.Outcome.StartsWith("prepare: " + Refused, StringComparison.Ordinal),
                _ => o.Outcome.StartsWith("OK: verified True", StringComparison.Ordinal),
            };
            var unexpected = outcomes.Where(o => !Expected(o)).ToList();
            var deadlocks = await DeadlockCounterAsync() - deadlocksBefore;
            Record("GC-06", "Security", "mixed concurrent stress on real PostgreSQL (4 rounds x 6 concurrent requests, two organizations): level-2 v2 writes create bindings, level-1 Verifies and writes in the bound organization are refused, everything else proceeds; no deadlock (pg_stat_database.deadlocks unchanged since the group started)",
                "first write OK; 24 outcomes all in their expected sets; deadlocks 0",
                $"first {first}; outcomes {outcomes.Count}; unexpected {unexpected.Count} [{string.Join(" | ", unexpected.Take(4).Select(u => u.Kind + ": " + u.Outcome))}]; deadlocks {deadlocks}; bindings A {await BindingCountAsync($"organization_id = '{sA.Id}'")} B {await BindingCountAsync($"organization_id = '{sB.Id}'")}",
                first == "OK" && outcomes.Count == 24 && unexpected.Count == 0 && deadlocks == 0 && await BindingCountAsync($"organization_id = '{sB.Id}'") == 0);
        }

        await GrantSentinelAsync("after-capability-concurrency");
    }

    private static async Task<(string, string)> Tag(string kind, Task<string> task) => (kind, await task);
}
