// E3-M2f-3G group L (M2F3-30, M2F3-70 and the liveness degradations): a mature organization with a large unrelated audit history,
// plans captured by auto_explain INSIDE the real T2 (enabled on this disposable copy only, after seeding), the generic prepared plan
// under the runtime identity, earlier-hour history, statement bounds, and missing / mismatched / invalid IX-B and its recovery by the
// E3-M2f-3i command. Disposable-container planner results do not establish Azure PostgreSQL behaviour.
using System.Diagnostics;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Features.Content;
using Npgsql;

internal sealed partial class Lab
{
    private const string LookupPositional = "SELECT security_journal_entry_id, journal_stream_revision, journal_event_body_json, journal_event_digest FROM authority.security_journal_entries WHERE organization_id = $1 AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' ORDER BY journal_stream_revision DESC LIMIT 8";

    /// <summary>The auto_explain plan of the eight-row lookup issued by <paramref name="application"/> since <paramref name="since"/>.</summary>
    private async Task<string> LookupPlanAsync(string application, DateTimeOffset since, string label)
    {
        await Task.Delay(500);
        var entries = await ServerLogAsync(application, since);
        var plan = entries.LastOrDefault(e => e.Contains("plan:", StringComparison.Ordinal) && e.Contains("LIMIT 8", StringComparison.Ordinal) && e.Contains("EnvelopeCapabilityRefused", StringComparison.Ordinal)) ?? "NO PLAN LOGGED";
        await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, "plans", label + ".txt"), plan + "\n");
        return plan;
    }

    internal sealed record PlanFacts(bool BackwardOnIxB, bool HasLimit, long IndexRows, long SharedBuffers, bool Filtered, string Detail)
    {
        public override string ToString() => Detail;
    }

    private static PlanFacts Facts(string plan)
    {
        var backward = plan.Contains("Index Scan Backward using ix_journal_capability_refusal_evidence", StringComparison.Ordinal);
        var limit = Regex.IsMatch(plan, @"\bLimit\b");
        var scanLine = plan.Split('\n').FirstOrDefault(l => l.Contains("Index Scan", StringComparison.Ordinal) || l.Contains("Seq Scan", StringComparison.Ordinal) || l.Contains("Bitmap", StringComparison.Ordinal)) ?? string.Empty;
        var rowsMatch = Regex.Match(scanLine, @"actual time=[0-9.]+\.\.[0-9.]+ rows=([0-9]+)");
        var rows = rowsMatch.Success ? long.Parse(rowsMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
        long buffers = 0;
        foreach (Match m in Regex.Matches(plan, @"Buffers: shared (?:hit=([0-9]+))?\s*(?:read=([0-9]+))?"))
        {
            buffers = Math.Max(buffers, (m.Groups[1].Success ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0) + (m.Groups[2].Success ? long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0));
        }

        var filtered = plan.Contains("Rows Removed by Filter", StringComparison.Ordinal);
        return new PlanFacts(backward, limit, rows, buffers, filtered, $"backward IX-B {backward}; Limit {limit}; scan rows {rows}; shared buffers {buffers}; rows removed by filter {filtered}; scan [{Short(scanLine.Trim(), 160)}]");
    }

    /// <summary>The time between T2's organization lock and its COMMIT/ROLLBACK, from the server log timestamps (%m).</summary>
    private static long LockHoldMilliseconds(List<string> entries)
    {
        static DateTimeOffset? Stamp(string entry) =>
            DateTimeOffset.TryParse(entry[..Math.Min(23, entry.Length)], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        var lockEntry = entries.FirstOrDefault(e => Classify(Statements([e]).FirstOrDefault() ?? string.Empty) == "LOCK_ORG");
        var endEntry = entries.LastOrDefault(e => Statements([e]).FirstOrDefault() is { } s && (Classify(s) is "COMMIT" or "ROLLBACK"));
        return lockEntry is not null && endEntry is not null && Stamp(lockEntry) is { } a && Stamp(endEntry) is { } b ? (long)(b - a).TotalMilliseconds : -1;
    }

    private async Task<(CapabilityRefusalEvidenceOutcome Outcome, CapabilityRefusalEvidenceReason Reason, long Milliseconds, string Plan, List<string> Entries, long LockHold)> MeasuredRecordAsync(string db, Org org, string entryPoint, string application, string label)
    {
        var since = DateTimeOffset.UtcNow;
        var observer = new StatementObserver();
        var commits = new CommitHook();
        var node = Recorder(db, application, interceptors: [observer, commits]);
        var sw = Stopwatch.StartNew();
        var outcome = await node.Recorder.RecordAsync(Refusal(org), entryPoint, CancellationToken.None);
        var ms = sw.ElapsedMilliseconds;
        var plan = await LookupPlanAsync(application, since, label);
        var entries = await ServerLogAsync(application, since);
        await SaveTraceAsync(label, entries);
        // T2 lock hold (TQ-14): from the completion of the organization row lock to the return of COMMIT (EF side; -1 without a commit).
        var locked = observer.Completed.FirstOrDefault(c => Classify(c.Text) == "LOCK_ORG").At;
        var hold = locked != default && commits.LastCommitted is { } committed ? (long)(committed - locked).TotalMilliseconds : -1;
        return (outcome, node.Outcomes.All.LastOrDefault().Reason, ms, plan, entries, hold);
    }

    private async Task LivenessScenariosAsync()
    {
        CurrentGroup = "L";
        var db = await T2DatabaseAsync("g3_l1");
        var mature = await SeedOrgAsync(db);
        var others = new List<Org>();
        for (var i = 0; i < 4; i++)
        {
            others.Add(await SeedOrgAsync(db));
        }

        var sw = Stopwatch.StartNew();
        await SeedMatureHistoryAsync(mature, inputs.BigRows, others);
        var seeded = await CountAsync(db, "SELECT count(*) FROM authority.security_journal_entries");
        Log($"mature history seeded: {seeded} journal entries in {sw.Elapsed.TotalSeconds:F0} s");
        foreach (var setting in new[] { "session_preload_libraries = 'auto_explain'", "auto_explain.log_min_duration = 0", "auto_explain.log_analyze = on", "auto_explain.log_buffers = on", "auto_explain.log_timing = on", "auto_explain.log_format = 'text'" })
        {
            await ExecAsync(Maintenance, $"ALTER DATABASE {db} SET {setting}");
        }

        NpgsqlConnection.ClearAllPools();
        var stats = await ScalarAsync(db, "SELECT coalesce(max(last_analyze)::text, 'never') || '/' || coalesce(max(last_autoanalyze)::text, 'never') FROM pg_stat_user_tables WHERE relname = 'security_journal_entries'");

        // L-01 (M2F3-70, M2F3-30): the first refusal of a mature organization, no statistics.
        {
            var r = await MeasuredRecordAsync(db, mature, CapabilityRefusalEvidence.EntryPoints.Verify, "g3l01", "L-01-first-refusal-no-stats");
            var f = Facts(r.Plan);
            var hold = r.LockHold;
            var oracle = await OracleAsync(db, mature.Id, inputs.BigRows);
            Record("L-01", "Liveness", $"M2F3-70 / M2F3-30: a mature organization ({inputs.BigRows} unrelated audit entries; {seeded} journal entries in all; statistics {stats}) records its FIRST refusal on the first attempt; inside the real T2 the eight-row lookup is a backward index scan on IX-B under Limit that returns 0 rows and touches a handful of buffers (no unrelated audit entry read); T2 holds the organization row briefly",
                "Committed; backward IX-B; Limit; scan rows 0; buffers < 50; no filter removals; lock hold < 500 ms; oracle holds",
                $"{r.Outcome} in {r.Milliseconds} ms; {f}; lock hold {hold} ms; {oracle}",
                r.Outcome == CapabilityRefusalEvidenceOutcome.Committed && f.BackwardOnIxB && f.HasLimit && f.IndexRows == 0 && f.SharedBuffers < 50 && !f.Filtered && hold is >= 0 and < 500 && oracle.Total == 1 && oracle.Holds());
        }

        // L-02: repeat (another process): found in one row.
        {
            var r = await MeasuredRecordAsync(db, mature, CapabilityRefusalEvidence.EntryPoints.Verify, "g3l02", "L-02-repeat");
            var f = Facts(r.Plan);
            Record("L-02", "Liveness", "repeat refusal (fresh process): the lookup returns the one entry through IX-B; AlreadyRecorded; nothing appended",
                "AlreadyRecorded; backward IX-B; scan rows 1", $"{r.Outcome} in {r.Milliseconds} ms; {f}; evidence {await EvidenceCountAsync(db, mature.Id)}",
                r.Outcome == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && f.BackwardOnIxB && f.IndexRows == 1 && await EvidenceCountAsync(db, mature.Id) == 1);
        }

        // L-03: multiple entry points: all seven keys, then a lookup over seven entries (at most eight heap rows).
        {
            var outcomes = new List<string>();
            foreach (var e in Seven.Where(e => e != CapabilityRefusalEvidence.EntryPoints.Verify))
            {
                outcomes.Add((await Recorder(db, "g3l03-" + e).Recorder.RecordAsync(Refusal(mature), e, CancellationToken.None)).ToString());
            }

            var r = await MeasuredRecordAsync(db, mature, CapabilityRefusalEvidence.EntryPoints.Prepare, "g3l03", "L-03-seven-entries");
            var f = Facts(r.Plan);
            var oracle = await OracleAsync(db, mature.Id, inputs.BigRows);
            Record("L-03", "Liveness", "multiple entry points on the mature organization: the six other keys each recorded on the first attempt; a further lookup reads the seven entries (≤ 8 heap rows) through IX-B and finds its key",
                "Committed x6; AlreadyRecorded; backward IX-B; scan rows 7; oracle 7 holds", $"{string.Join(",", outcomes)}; {r.Outcome}; {f}; {oracle}",
                outcomes.All(o => o == "Committed") && r.Outcome == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && f.BackwardOnIxB && f.IndexRows == 7 && oracle.Total == 7 && oracle.Holds());
        }

        // L-04: after ANALYZE the plan is unchanged.
        {
            await ExecAsync(db, "ANALYZE authority.security_journal_entries");
            var r = await MeasuredRecordAsync(db, mature, CapabilityRefusalEvidence.EntryPoints.Commit, "g3l04", "L-04-analyzed");
            var f = Facts(r.Plan);
            Record("L-04", "Liveness", "after ANALYZE: inside T2 the lookup is still the backward IX-B scan under Limit", "AlreadyRecorded; backward IX-B; Limit", $"{r.Outcome}; {f}", r.Outcome == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && f.BackwardOnIxB && f.HasLimit);
        }

        // L-05: the generic prepared plan (a parameterized organization identifier) under the runtime identity.
        {
            await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(RuntimeFor(db, "g3l05")) { Pooling = false }.ConnectionString);
            await c.OpenAsync();
            await using (var set = new NpgsqlCommand($"SET plan_cache_mode = force_generic_plan; PREPARE g3_lookup(uuid) AS {LookupPositional}", c))
            {
                await set.ExecuteNonQueryAsync();
            }

            var lines = new List<string>();
            await using (var explain = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS) EXECUTE g3_lookup('{mature.Id:D}')", c))
            await using (var reader = await explain.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(0));
                }
            }

            var plan = string.Join("\n", lines);
            await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, "plans", "L-05-generic-prepared.txt"), plan + "\n");
            var f = Facts(plan);
            Record("L-05", "Liveness", "generic prepared plan (plan_cache_mode = force_generic_plan, runtime identity, parameterized organization): the literal predicates still match IX-B: backward IX-B scan under Limit",
                "backward IX-B; Limit; scan rows 7", f.ToString(), f.BackwardOnIxB && f.HasLimit && f.IndexRows == 7);
        }

        // L-06: earlier-hour history (controlled setup: seven keys in each of the three previous hours, through the real AuditJournal).
        {
            var org = await SeedOrgAsync(db);
            var now = await DatabaseNowAsync(db);
            var hour = CapabilityRefusalEvidence.BucketOf(now);
            for (var h = 3; h >= 1; h--)
            {
                for (var i = 0; i < Seven.Length; i++)
                {
                    await AppendAuditAsync(org, CapabilityRefusalEvidence.EventKind, EvidenceBody(org, Seven[i], hour.AddHours(-h).AddMinutes(1 + i)));
                }
            }

            var r = await MeasuredRecordAsync(db, org, CapabilityRefusalEvidence.EntryPoints.Verify, "g3l06", "L-06-earlier-hours");
            var f = Facts(r.Plan);
            var oracle = await OracleAsync(db, org.Id);
            Record("L-06", "Liveness", "later hour: with seven entries in each of the three previous hours (the eight newest are all older than the current bucket), the current hour's first refusal of a key is recorded; the lookup reads exactly eight rows; per-bucket bound holds",
                "Committed; backward IX-B; scan rows 8; evidence 22; max per bucket 7; oracle holds", $"{r.Outcome}; {f}; {oracle}",
                r.Outcome == CapabilityRefusalEvidenceOutcome.Committed && f.BackwardOnIxB && f.IndexRows == 8 && oracle.Total == 22 && oracle.MaxPerBucket == 7 && oracle.Holds());
        }

        // L-07: lookup timeout on the mature organization (IX-B held by an open REINDEX in another session).
        {
            var org = others[0];
            await using var holder = await OpenAdminAsync(db, "g3-l-index-holder");
            var tx = await holder.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand("REINDEX INDEX authority.ix_journal_capability_refusal_evidence", holder, tx))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var r = await MeasuredRecordAsync(db, org, CapabilityRefusalEvidence.EntryPoints.Verify, "g3l07", "L-07-lookup-timeout");
            await tx.RollbackAsync();
            var during = await EvidenceCountAsync(db, org.Id); // after the release: planning a journal query needs a lock on IX-B
            Record("L-07", "Liveness", "lookup timeout on a mature organization: IX-B held ACCESS EXCLUSIVE by another session; the lookup is cancelled by statement_timeout; Deferred within the budget; nothing appended",
                "Deferred; server statement-timeout at LOOKUP; < 1500 ms; evidence 0", $"{r.Outcome} {r.Reason}; server {CancelledStatement(r.Entries)}; {r.Milliseconds} ms; evidence {during}",
                r.Outcome == CapabilityRefusalEvidenceOutcome.Deferred && CancelledStatement(r.Entries) == "statement-timeout LOOKUP" && r.Milliseconds < 1500 && during == 0);
        }

        // L-08: catalogue check failure on the mature organization (journal table locked): cancelled at the catalogue check.
        {
            var org = others[1];
            await using var holder = await OpenAdminAsync(db, "g3-l-table-holder");
            var tx = await holder.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand("LOCK TABLE authority.security_journal_entries IN ACCESS EXCLUSIVE MODE", holder, tx))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var r = await MeasuredRecordAsync(db, org, CapabilityRefusalEvidence.EntryPoints.Verify, "g3l08", "L-08-catalogue-failure");
            await tx.RollbackAsync();
            Record("L-08", "Liveness", "catalogue check failure on a mature organization: the journal table held ACCESS EXCLUSIVE; the catalogue check (deparse, ACCESS SHARE on the table) is cancelled by statement_timeout; Deferred; no lookup ran; nothing appended",
                "Deferred; server statement-timeout at INDEX_CHECK; no LOOKUP; evidence 0", $"{r.Outcome} {r.Reason}; server {CancelledStatement(r.Entries)}; lookups {Statements(r.Entries).Count(s => Classify(s) == "LOOKUP")}; evidence {await EvidenceCountAsync(db, org.Id)}",
                r.Outcome == CapabilityRefusalEvidenceOutcome.Deferred && CancelledStatement(r.Entries) == "statement-timeout INDEX_CHECK" && Statements(r.Entries).All(s => Classify(s) != "LOOKUP") && await EvidenceCountAsync(db, org.Id) == 0);
        }

        // L-09 / L-10 / L-11: missing, mismatched and invalid IX-B on the mature database: recording stops (INDEX_UNAVAILABLE), no lookup.
        var degraded = others[2];
        async Task IndexDegradedAsync(string id, string description, string state)
        {
            var application = "g3" + id.ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal);
            var since = DateTimeOffset.UtcNow;
            var node = Recorder(db, application);
            var outcome = await node.Recorder.RecordAsync(Refusal(degraded), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var reason = node.Outcomes.All.Last().Reason;
            var again = await node.Recorder.RecordAsync(Refusal(degraded), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            await Task.Delay(400);
            var entries = await ServerLogAsync(application, since);
            await SaveTraceAsync(id + "-index-" + state, entries);
            var classes = Statements(entries).Select(Classify).ToList();
            var oracle = await OracleAsync(db, degraded.Id, inputs.BigRows / 4 / 4);
            Record(id, "Liveness", description + ": T2 checks the catalogue, finds no usable IX-B, runs no lookup and appends nothing (Deferred INDEX_UNAVAILABLE; under D-3b-2 a retry is scheduled, log 9253, rather than the 9254/negative cache named in the closure text of M2F3-62); a repeat refusal of the key is suppressed while the retry is pending; the oracle holds",
                $"index {state}; Deferred IndexUnavailable; repeat SuppressedPending; INDEX_CHECK x1; LOOKUP none; evidence 0; oracle holds",
                $"index {await IndexStateAsync(db)}; {outcome} {reason}; repeat {again}; INDEX_CHECK x{classes.Count(c => c == "INDEX_CHECK")}; LOOKUP x{classes.Count(c => c == "LOOKUP")}; {oracle}",
                await IndexStateAsync(db) == state && outcome == CapabilityRefusalEvidenceOutcome.Deferred && reason == CapabilityRefusalEvidenceReason.IndexUnavailable && again == CapabilityRefusalEvidenceOutcome.SuppressedPending
                && classes.Count(c => c == "INDEX_CHECK") == 1 && !classes.Contains("LOOKUP") && oracle.Total == 0 && oracle.Holds());
        }

        await ExecAsync(db, "DROP INDEX CONCURRENTLY authority.ix_journal_capability_refusal_evidence");
        await IndexDegradedAsync("L-09", "missing IX-B on the mature database", "absent");
        await ExecAsync(db, "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision)");
        await IndexDegradedAsync("L-10", "mismatched IX-B (same name, no predicate) on the mature database", "mismatched");
        await ExecAsync(db, "DROP INDEX authority.ix_journal_capability_refusal_evidence");
        var invalid = await CreateInvalidIndexAsync(db);
        if (invalid)
        {
            await IndexDegradedAsync("L-11", "invalid IX-B (an interrupted CREATE INDEX CONCURRENTLY of the approved definition) on the mature database", "invalid");
        }
        else
        {
            Record("L-11", "Liveness", "invalid IX-B setup (interrupted concurrent build)", "invalid", await IndexStateAsync(db), false);
        }

        // L-12 (M2F3-84 liveness half): recovery by the accepted command restores first-attempt recording.
        {
            await ExecAsync(db, "DROP INDEX CONCURRENTLY IF EXISTS authority.ix_journal_capability_refusal_evidence");
            var provision = await ProvisionIndexAsync(db, "mature-recovery");
            var r = await MeasuredRecordAsync(db, degraded, CapabilityRefusalEvidence.EntryPoints.Verify, "g3l12", "L-12-recovered");
            var f = Facts(r.Plan);
            var oracle = await OracleAsync(db, degraded.Id, inputs.BigRows / 4 / 4);
            Record("L-12", "Liveness", "recovery: after the manual DROP of the unusable index, the accepted E3-M2f-3i command (migration identity) rebuilds IX-B on the mature table; the next refusal is recorded on the first attempt through IX-B",
                "command exit 0; approved-valid; Committed; backward IX-B; scan rows 0; evidence 1; oracle holds", $"command {provision}; {await IndexStateAsync(db)}; {r.Outcome}; {f}; {oracle}",
                provision.ExitCode == 0 && await IndexStateAsync(db) == "approved-valid" && r.Outcome == CapabilityRefusalEvidenceOutcome.Committed && f.BackwardOnIxB && f.IndexRows == 0 && oracle.Total == 1 && oracle.Holds());
        }
    }

    /// <summary>Controlled setup: an INVALID index with the approved name (a concurrent build of the approved definition, cancelled while it waits for another session's snapshot).</summary>
    private async Task<bool> CreateInvalidIndexAsync(string db)
    {
        await using var snapshot = await OpenAdminAsync(db, "g3-snapshot-holder");
        var tx = await snapshot.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using (var hold = new NpgsqlCommand("SELECT count(*) FROM authority.organization_authorization_states", snapshot, tx))
        {
            await hold.ExecuteScalarAsync();
        }

        var builder = Task.Run(async () =>
        {
            try
            {
                await ExecAsync(db, "CREATE INDEX CONCURRENTLY ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'");
            }
            catch (PostgresException ex) when (ex.SqlState == "57014")
            {
                // cancelled, as intended
            }
        });
        int? pid = null;
        for (var i = 0; i < 300 && pid is null; i++)
        {
            await Task.Delay(100);
            var p = await ScalarAsync(Maintenance, $"SELECT coalesce((SELECT pid FROM pg_stat_activity WHERE datname = '{db}' AND query ILIKE 'CREATE INDEX CONCURRENTLY%' AND wait_event_type = 'Lock' LIMIT 1)::text, '')");
            if (p.Length > 0)
            {
                pid = int.Parse(p, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (pid is not null)
        {
            await ExecAsync(Maintenance, $"SELECT pg_cancel_backend({pid})");
        }

        await builder;
        await tx.RollbackAsync();
        return await IndexStateAsync(db) == "invalid";
    }
}
