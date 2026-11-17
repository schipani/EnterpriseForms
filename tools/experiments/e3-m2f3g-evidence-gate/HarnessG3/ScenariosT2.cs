// E3-M2f-3G groups T (the real T2 on PostgreSQL: statements, footprint, side effects, deduplication, eviction, coalescing, contention,
// lock and statement bounds), F (server-side and commit-phase faults, clock regression, invalid bodies) and Q (quota, retry capacity,
// retry exhaustion, restart). Each fault is added to ONE disposable copy only; every scenario ends with the independent oracle.
using System.Diagnostics;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchAccess;
using Npgsql;

internal sealed partial class Lab
{
    /// <summary>The planned T2 read statements on the server (auto_explain), in order; then exactly one INSERT and one UPDATE (EF orders its batch).</summary>
    private static readonly string[] T2ReadShape = ["LOCK_ORG", "CLOCK", "INDEX_CHECK", "LOOKUP"];

    /// <summary>The EF-issued T2 commands (observing interceptor), in order: the lock bound, the lock, the statement bound, one SaveChanges batch.</summary>
    private static readonly string[] T2EfShape = ["SET_LOCK", "LOCK_ORG", "SET_STMT", "INSERT_JOURNAL+UPDATE_ORG"];

    private static bool IsT2Shape(IReadOnlyList<string> server) =>
        server.Count == 6 && server.Take(4).SequenceEqual(T2ReadShape) && server.Skip(4).Order(StringComparer.Ordinal).SequenceEqual(["INSERT_JOURNAL", "UPDATE_ORG"]);

    internal static string Classify(string statement)
    {
        var s = statement.Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (s.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            return "BEGIN";
        }

        if (s.StartsWith("DISCARD", StringComparison.OrdinalIgnoreCase) || s.StartsWith("SET SESSION", StringComparison.OrdinalIgnoreCase))
        {
            return "RESET";
        }

        if (s.Contains("FROM pg_type", StringComparison.Ordinal) || s.StartsWith("SELECT version()", StringComparison.Ordinal) || s.Contains("pg_enum", StringComparison.Ordinal) || s.Contains("FROM pg_catalog.pg_type", StringComparison.Ordinal))
        {
            return "BOOTSTRAP"; // Npgsql's type loading when a physical connection opens
        }

        if (s.Contains("INSERT INTO authority.security_journal_entries", StringComparison.Ordinal) && s.Contains("UPDATE authority.organization_authorization_states", StringComparison.Ordinal))
        {
            return "INSERT_JOURNAL+UPDATE_ORG"; // one EF batch
        }

        if (s.StartsWith("SET LOCAL lock_timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "SET_LOCK";
        }

        if (s.StartsWith("SET LOCAL statement_timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "SET_STMT";
        }

        if (s.Contains("organization_authorization_states", StringComparison.Ordinal) && s.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return "LOCK_ORG";
        }

        if (s.Contains("clock_timestamp()", StringComparison.Ordinal) && s.Length < 60)
        {
            return "CLOCK";
        }

        if (s.Contains("pg_get_indexdef", StringComparison.Ordinal))
        {
            return "INDEX_CHECK";
        }

        if (s.Contains("journal_event_kind = 'EnvelopeCapabilityRefused'", StringComparison.Ordinal) && s.Contains("LIMIT 8", StringComparison.Ordinal))
        {
            return "LOOKUP";
        }

        if (s.StartsWith("INSERT INTO authority.security_journal_entries", StringComparison.Ordinal))
        {
            return "INSERT_JOURNAL";
        }

        if (s.StartsWith("UPDATE authority.organization_authorization_states", StringComparison.Ordinal))
        {
            return "UPDATE_ORG";
        }

        if (s.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase))
        {
            return "COMMIT";
        }

        if (s.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            return "ROLLBACK";
        }

        return "OTHER:" + Short(s, 80);
    }

    /// <summary>The T2 statements of one in-process store (its unique application name), classified; transaction-control noise kept.</summary>
    private async Task<(List<string> Classes, List<string> Statements, List<string> Lines)> TraceAsync(string application, DateTimeOffset since, string label)
    {
        await Task.Delay(400);
        var lines = await ServerLogAsync(application, since);
        await SaveTraceAsync(label, lines);
        var statements = Statements(lines);
        return ([.. statements.Select(Classify)], statements, lines);
    }

    /// <summary>The statement a server-side cancellation named (log ERROR line followed by its STATEMENT line).</summary>
    private static string CancelledStatement(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains("ERROR:  canceling statement due to statement timeout", StringComparison.Ordinal) || lines[i].Contains("ERROR:  canceling statement due to lock timeout", StringComparison.Ordinal))
            {
                var kind = lines[i].Contains("lock timeout", StringComparison.Ordinal) ? "lock-timeout" : "statement-timeout";
                for (var j = i + 1; j < Math.Min(lines.Count, i + 4); j++)
                {
                    var k = lines[j].IndexOf("STATEMENT:  ", StringComparison.Ordinal);
                    if (k >= 0)
                    {
                        return kind + " " + Classify(lines[j][(k + 12)..]);
                    }
                }

                return kind + " ?";
            }
        }

        return "none";
    }

    private async Task<Dictionary<string, long>> FunctionCallsAsync(string database)
    {
        NpgsqlConnection.ClearAllPools(); // backends exit and flush their function statistics
        await Task.Delay(1200);
        var lines = await LinesAsync(database, "SELECT schemaname || '.' || funcname || '=' || calls FROM pg_stat_user_functions");
        return lines.Select(l => l.Split('=')).ToDictionary(p => p[0], p => long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }

    /// <summary>A T2 copy: track_functions = all and auto_explain for every statement (database settings of this disposable copy only).</summary>
    private async Task<string> T2DatabaseAsync(string name, string template = Base)
    {
        await CreateDatabaseAsync(name, template);
        foreach (var setting in new[] { "track_functions = 'all'", "session_preload_libraries = 'auto_explain'", "auto_explain.log_min_duration = 0", "auto_explain.log_format = 'text'" })
        {
            await ExecAsync(Maintenance, $"ALTER DATABASE {name} SET {setting}");
        }

        return name;
    }

    private static List<string> Core(IEnumerable<string> classes) => [.. classes.Where(c => c is not "BEGIN" and not "RESET" and not "BOOTSTRAP")];

    private static long Percentile(IReadOnlyList<long> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    // ------------------------------------------------------------------------------------------------ group T
    private async Task T2ScenariosAsync()
    {
        CurrentGroup = "T";
        var db = await T2DatabaseAsync("g3_t1");
        var tA = await SeedOrgAsync(db);
        var others = new List<Org> { await SeedOrgAsync(db), await SeedOrgAsync(db) };

        // T-01 / T-02 / T-03 (M2F3-54, -56, -71, -28 "T2 never triggers the fence"): one real T2 on PostgreSQL.
        {
            var probe = Store(db, "g3-markers");
            var schedule = new SearchAccessMaintenanceSchedule(new SearchAccessOptions { ReconciliationIntervalSeconds = 3600 }, TimeProvider.System);
            foreach (var marker in await probe.GetOrganizationChangeMarkersAsync(CancellationToken.None))
            {
                schedule.Settle(marker);
            }

            schedule.Plan(await probe.GetOrganizationChangeMarkersAsync(CancellationToken.None));
            var fnBefore = await FunctionCallsAsync(db);
            var printBefore = await FingerprintAsync(db, tA.Id);
            var revBefore = await RevisionsAsync(db, tA.Id);
            var othersBefore = new List<string>();
            foreach (var o in others)
            {
                othersBefore.Add(await RevisionsAsync(db, o.Id) + " " + await FingerprintAsync(db, o.Id));
            }

            var since = DateTimeOffset.UtcNow;
            var observer = new StatementObserver();
            var commits = new CommitHook();
            var node = Recorder(db, "g3t01", interceptors: [observer, commits]);
            var outcome = await node.Recorder.RecordAsync(Refusal(tA), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var (classes, statements, _) = await TraceAsync("g3t01", since, "T-01-t2");
            var fnAfter = await FunctionCallsAsync(db);
            var core = Core(classes);
            var ef = observer.Texts.Select(Classify).ToList();
            await File.WriteAllLinesAsync(Path.Combine(inputs.EvidenceDir, "traces", "T-01-t2-ef.log"), observer.Texts);
            var update = statements.FirstOrDefault(s => Classify(s) == "UPDATE_ORG") ?? string.Empty;
            var setList = update.Contains(" SET ", StringComparison.Ordinal) ? update[(update.IndexOf(" SET ", StringComparison.Ordinal) + 5)..] : string.Empty;
            var whereAt = setList.IndexOf("WHERE", StringComparison.Ordinal);
            setList = whereAt >= 0 ? setList[..whereAt] : setList;
            var columns = setList.Split(',').Select(p => p.Split('=')[0].Trim()).Where(c => c.Length > 0).Order(StringComparer.Ordinal).ToList();
            var all = statements.Concat(observer.Texts).ToList();
            var forbidden = all.Where(s => s.Contains("transaction_timestamp", StringComparison.OrdinalIgnoreCase) || s.Contains("now()", StringComparison.OrdinalIgnoreCase)
                                           || s.Contains("current_timestamp", StringComparison.OrdinalIgnoreCase) || s.Contains("statement_timestamp", StringComparison.OrdinalIgnoreCase)).ToList();
            var fence = all.Where(s => s.Contains("content_change_operations", StringComparison.Ordinal) || s.Contains("rule_floor", StringComparison.Ordinal) || s.Contains("content_data_key_bindings", StringComparison.Ordinal)).ToList();
            Record("T-01", "Security", "M2F3-54 / M2F3-71 / M2F3-28 (fence): the real T2 on PostgreSQL 18. Server side (auto_explain; the runtime role logs no statements by product setting): the organization row lock, clock_timestamp() (after the lock), the catalogue check and the eight-row lookup, in that order, then exactly ONE INSERT into the journal and ONE UPDATE of the organization row (EF orders its batch) and nothing else. EF side (observing interceptor): SET LOCAL lock_timeout before the lock, SET LOCAL statement_timeout after it, one SaveChanges batch with the INSERT and the UPDATE, one commit. The UPDATE sets exactly the three permitted columns; no statement reads transaction_timestamp/now/current_timestamp/statement_timestamp; no statement touches content operations, the CARV floors or the data-key ledger",
                $"Committed; server [{string.Join(",", T2ReadShape)}] + one INSERT_JOURNAL + one UPDATE_ORG; EF [{string.Join(",", T2EfShape)}]; commits 1; update columns audit_journal_revision,latest_audit_journal_digest,state_modified_on; forbidden 0; fence 0",
                $"{outcome}; server [{string.Join(",", core)}]; EF [{string.Join(",", ef)}]; commits {commits.Commits}; update columns {string.Join(",", columns)}; forbidden {forbidden.Count}; fence {fence.Count}",
                outcome == CapabilityRefusalEvidenceOutcome.Committed && IsT2Shape(core) && ef.SequenceEqual(T2EfShape) && commits.Commits == 1
                && columns.SequenceEqual(["audit_journal_revision", "latest_audit_journal_digest", "state_modified_on"]) && forbidden.Count == 0 && fence.Count == 0);

            var revAfter = await RevisionsAsync(db, tA.Id);
            var printAfter = await FingerprintAsync(db, tA.Id);
            var othersAfter = new List<string>();
            foreach (var o in others)
            {
                othersAfter.Add(await RevisionsAsync(db, o.Id) + " " + await FingerprintAsync(db, o.Id));
            }

            var plan = schedule.Plan(await probe.GetOrganizationChangeMarkersAsync(CancellationToken.None));
            var catchUps = plan.Items.Where(i => i.Action == SearchAccessMaintenanceAction.CatchUp).Select(i => i.Marker.OrganizationId).ToList();
            var expectedRev = revBefore.Replace("audit 0;", "audit 1;", StringComparison.Ordinal);
            Record("T-02", "Security", "M2F3-56 on PostgreSQL: audit_journal_revision +1; authorization, projection and content-integrity revisions unchanged (so result snapshots, which compare authorization and content-integrity revisions, stay valid); no quarantine; nothing else of the organization changed; other organizations untouched; the search-access maintenance schedule plans exactly one CatchUp, for this organization",
                $"{expectedRev}; fingerprint unchanged; others unchanged; CatchUp [{tA.Short}]",
                $"{revBefore} -> {revAfter}; fingerprint {FingerprintDiff(printBefore, printAfter)}; others {(othersBefore.SequenceEqual(othersAfter) ? "unchanged" : "CHANGED")}; CatchUp [{string.Join(",", catchUps.Select(c => c.ToString("N")[..8]))}]",
                revAfter == expectedRev && printBefore == printAfter && othersBefore.SequenceEqual(othersAfter) && catchUps.SequenceEqual([tA.Id]));

            var called = fnAfter.Where(kv => kv.Value > fnBefore.GetValueOrDefault(kv.Key)).Select(kv => kv.Key + "+" + (kv.Value - fnBefore.GetValueOrDefault(kv.Key))).Order(StringComparer.Ordinal).ToList();
            Record("T-03", "Security", "M2F3-54 (triggers): during T2 only the quarantine-permanence trigger function runs (pg_stat_user_functions, track_functions = all on this copy)",
                "only a *quarantine* trigger function, once", called.Count == 0 ? "no function call recorded" : string.Join(", ", called),
                called.Count == 1 && called[0].Contains("quarantine", StringComparison.OrdinalIgnoreCase) && called[0].EndsWith("+1", StringComparison.Ordinal));

            var oracle = await OracleAsync(db, tA.Id);
            Record("T-04", "Safety", "M2F3-72 after T-01: the oracle holds (one entry, valid body, digest and chain intact, audit head matches)", "evidence 1; oracle holds", oracle.ToString(), oracle.Total == 1 && oracle.Holds());

            var since2 = DateTimeOffset.UtcNow;
            var observer2 = new StatementObserver();
            var commits2 = new CommitHook();
            var fresh = Recorder(db, "g3t05", interceptors: [observer2, commits2]);
            var again = await fresh.Recorder.RecordAsync(Refusal(tA), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var (classes2, _, _) = await TraceAsync("g3t05", since2, "T-05-dedup");
            var core2 = Core(classes2);
            var ef2 = observer2.Texts.Select(Classify).ToList();
            Record("T-05", "Safety", "M2F3-05 on PostgreSQL: a second process (fresh recorder, empty pre-filter) finds the key in its locked lookup and writes nothing: no INSERT, no UPDATE on the server, no SaveChanges and no commit on the EF side (the transaction is rolled back on disposal)",
                "AlreadyRecorded; server [LOCK_ORG,CLOCK,INDEX_CHECK,LOOKUP]; EF [SET_LOCK,LOCK_ORG,SET_STMT]; commits 0; evidence 1",
                $"{again}; server [{string.Join(",", core2)}]; EF [{string.Join(",", ef2)}]; commits {commits2.Commits}; evidence {await EvidenceCountAsync(db, tA.Id)}",
                again == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && core2.SequenceEqual(["LOCK_ORG", "CLOCK", "INDEX_CHECK", "LOOKUP"]) && ef2.SequenceEqual(["SET_LOCK", "LOCK_ORG", "SET_STMT"]) && commits2.Commits == 0 && await EvidenceCountAsync(db, tA.Id) == 1);
        }

        // T-06 (M2F3-52): pre-filter eviction (capacity 1) falls back to the durable lookup; under a flood attempts rise, entries do not.
        {
            var org = await SeedOrgAsync(db);
            var node = Recorder(db, "g3t06", CapabilityRefusalEvidenceLimits.Default with { PreFilterCapacity = 1 });
            var first = new List<CapabilityRefusalEvidenceOutcome>();
            foreach (var e in new[] { CapabilityRefusalEvidence.EntryPoints.Verify, CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Commit })
            {
                first.Add(await node.Recorder.RecordAsync(Refusal(org), e, CancellationToken.None));
            }

            var evicted = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var flood = new List<Org> { org, await SeedOrgAsync(db), await SeedOrgAsync(db) };
            var since = DateTimeOffset.UtcNow;
            var floodNode = Recorder(db, "g3t06f", CapabilityRefusalEvidenceLimits.Default with { PreFilterCapacity = 1 });
            var outcomes = new List<CapabilityRefusalEvidenceOutcome>();
            for (var pass = 0; pass < 5; pass++)
            {
                outcomes.AddRange(await Task.WhenAll(flood.SelectMany(o => Seven.Select(e => Task.Run(() => floodNode.Recorder.RecordAsync(Refusal(o), e, CancellationToken.None))))));
            }

            var (classes, _, _) = await TraceAsync("g3t06f", since, "T-06-eviction-flood");
            var (holds, detail) = await FleetOracleAsync(db, flood);
            Record("T-06", "Safety", "M2F3-52 on PostgreSQL: pre-filter capacity 1: the evicted first key is looked up durably again and found (AlreadyRecorded, not a second append); a flood of 5 passes x 3 organizations x 7 keys makes the lookups rise (most calls reach T2) while every organization keeps exactly its seven entries",
                "Committed x3; AlreadyRecorded; flood lookups > 21; inserts 18 (the 4 keys of the first organization not yet recorded + 7 + 7); fleet oracle holds with 21 entries",
                $"{string.Join(",", first)}; {evicted}; flood {string.Join(",", outcomes.GroupBy(o => o).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => g.Key + "x" + g.Count()))}; lookups {classes.Count(c => c == "LOOKUP")}; inserts {classes.Count(c => c == "INSERT_JOURNAL")}; {detail}",
                first.All(o => o == CapabilityRefusalEvidenceOutcome.Committed) && evicted == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && classes.Count(c => c == "LOOKUP") > 21
                && classes.Count(c => c == "INSERT_JOURNAL") == 18 && holds && detail.Contains("evidence 21;", StringComparison.Ordinal));
        }

        // T-07: in-flight coalescing (D-3b-3) on PostgreSQL.
        {
            var org = await SeedOrgAsync(db);
            var since = DateTimeOffset.UtcNow;
            var node = Recorder(db, "g3t07");
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None))));
            var (classes, _, _) = await TraceAsync("g3t07", since, "T-07-coalescing");
            var oracle = await OracleAsync(db, org.Id);
            var summary = string.Join(",", outcomes.GroupBy(o => o).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => g.Key + "x" + g.Count()));
            Record("T-07", "Safety", "D-3b-3 on PostgreSQL: 40 concurrent refusals of one key in one process: one T2 (one lookup, one INSERT), every other call suppressed in flight or by the pre-filter",
                "Committed x1, the rest SuppressedInFlight/SuppressedRecorded; lookups 1; inserts 1; evidence 1", $"{summary}; lookups {classes.Count(c => c == "LOOKUP")}; inserts {classes.Count(c => c == "INSERT_JOURNAL")}; {oracle}",
                outcomes.Count(o => o == CapabilityRefusalEvidenceOutcome.Committed) == 1 && outcomes.All(o => o is CapabilityRefusalEvidenceOutcome.Committed or CapabilityRefusalEvidenceOutcome.SuppressedInFlight or CapabilityRefusalEvidenceOutcome.SuppressedRecorded)
                && classes.Count(c => c == "LOOKUP") == 1 && classes.Count(c => c == "INSERT_JOURNAL") == 1 && oracle.Total == 1 && oracle.Holds());
        }

        // T-08 (M2F3-27 / M2F3-51 / M2F3-72 on the real row lock): R = 10 "restarts" (rounds of fresh recorder instances, separate
        // pre-filters, as separate processes) x 6 instances race on all seven keys of one organization within one bucket.
        {
            var org = await SeedOrgAsync(db);
            var outcomes = new List<CapabilityRefusalEvidenceOutcome>();
            var timings = new List<long>();
            for (var round = 0; round < 10; round++)
            {
                var nodes = Enumerable.Range(0, 6).Select(i => Recorder(db, $"g3t08-{i}")).ToList();
                var tasks = nodes.SelectMany(n => Seven.Select(e => Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var o = await n.Recorder.RecordAsync(Refusal(org), e, CancellationToken.None);
                    return (o, sw.ElapsedMilliseconds);
                }))).ToList();
                foreach (var (o, ms) in await Task.WhenAll(tasks))
                {
                    outcomes.Add(o);
                    timings.Add(ms);
                }
            }

            var oracle = await OracleAsync(db, org.Id);
            Record("T-08", "Safety", "M2F3-27 / M2F3-51 / M2F3-72 on the real organization row lock: R = 10 restarts x 6 recorder instances (separate pre-filters, as separate processes) x 7 keys concurrently within one bucket: exactly 7 Committed in total, every other attempt AlreadyRecorded; no error; the oracle holds independently of R",
                "Committed x7; AlreadyRecorded x413; evidence 7; oracle holds",
                $"{string.Join(",", outcomes.GroupBy(o => o).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => g.Key + "x" + g.Count()))}; T2 p50 {Percentile(timings, 0.5)} ms, p95 {Percentile(timings, 0.95)} ms, max {timings.Max()} ms; {oracle}",
                outcomes.Count(o => o == CapabilityRefusalEvidenceOutcome.Committed) == 7 && outcomes.Count(o => o == CapabilityRefusalEvidenceOutcome.AlreadyRecorded) == outcomes.Count - 7 && oracle.Total == 7 && oracle.Holds());
        }

        // T-09 (M2F3-36): the organization row held by another session: T2 waits at most lock_timeout (1 s) and appends nothing; the
        // refused request returns within the budget. (a) recorder alone; (b) the full in-process refusal pipeline, the row taken between
        // T1 and T2 at T2's first statement.
        {
            var org = await SeedOrgAsync(db);
            await using var holder = await OpenAdminAsync(db, "g3-row-holder");
            var tx = await holder.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand($"SELECT 1 FROM authority.organization_authorization_states WHERE organization_id = '{org.Id:D}' FOR UPDATE", holder, tx))
            {
                await lockCmd.ExecuteNonQueryAsync();
            }

            var since = DateTimeOffset.UtcNow;
            var node = Recorder(db, "g3t09");
            var sw = Stopwatch.StartNew();
            var outcome = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var ms = sw.ElapsedMilliseconds;
            var during = await EvidenceCountAsync(db, org.Id);
            await tx.RollbackAsync();
            var (_, _, lines) = await TraceAsync("g3t09", since, "T-09-lock-wait");
            var cancelled = CancelledStatement(lines);
            node.Clock.Advance(TimeSpan.FromSeconds(6));
            await node.Recorder.RetryDueAsync(CancellationToken.None);
            var after = await EvidenceCountAsync(db, org.Id);

            // (b) the whole pipeline: T1 refuses; when T2 sends its first statement the harness takes the row in another session.
            var org2 = await SeedOrgAsync(db);
            NpgsqlConnection? holder2 = null;
            NpgsqlTransaction? tx2 = null;
            var hook = new StatementHook(t => t.StartsWith("SET LOCAL lock_timeout", StringComparison.Ordinal), async _ =>
            {
                holder2 = await OpenAdminAsync(db, "g3-row-holder-2");
                tx2 = await holder2.BeginTransactionAsync();
                await using var c = new NpgsqlCommand($"SELECT 1 FROM authority.organization_authorization_states WHERE organization_id = '{org2.Id:D}' FOR UPDATE", holder2, tx2);
                await c.ExecuteNonQueryAsync();
            });
            var piped = Recorder(db, "g3t09b", interceptors: [hook]);
            var service = Service(db, "g3t09b-t1");
            var (refusal, pipedOutcome, pipedMs) = await RefusePrepareInProcessAsync(service, piped, org2.Records[0]);
            var pipedEvidence = await EvidenceCountAsync(db, org2.Id);
            if (tx2 is not null)
            {
                await tx2.RollbackAsync();
            }

            if (holder2 is not null)
            {
                await holder2.DisposeAsync();
            }

            Record("T-09", "Failure", "M2F3-36 on PostgreSQL: with the organization row held by another session, T2 gives up at lock_timeout (server: 55P03), appends nothing, and is retried (9253) once the row is free; through the whole pipeline (T1 refusal, row taken at T2's first statement) the request still returns the 409 within the budget, with no evidence",
                "Deferred in 1000..2500 ms; server lock-timeout at LOCK_ORG; evidence 0 -> 1 after the retry; pipeline 409, Deferred, < 3000 ms, evidence 0",
                $"{outcome} in {ms} ms; server {cancelled}; evidence {during} -> {after}; pipeline {refusal}, {pipedOutcome}, {pipedMs} ms, evidence {pipedEvidence}",
                outcome == CapabilityRefusalEvidenceOutcome.Deferred && ms is >= 900 and <= 2500 && cancelled == "lock-timeout LOCK_ORG" && during == 0 && after == 1
                && refusal.Contains("CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT 409", StringComparison.Ordinal) && pipedOutcome == CapabilityRefusalEvidenceOutcome.Deferred && pipedMs < 3000 && pipedEvidence == 0);
        }

        // T-10: the journal TABLE locked by another session: T2's catalogue check (its deparse takes ACCESS SHARE on the table) is
        // cancelled by the 500 ms statement timeout; nothing appended.
        {
            var org = await SeedOrgAsync(db);
            await using var holder = await OpenAdminAsync(db, "g3-table-holder");
            var tx = await holder.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand("LOCK TABLE authority.security_journal_entries IN ACCESS EXCLUSIVE MODE", holder, tx))
            {
                await lockCmd.ExecuteNonQueryAsync();
            }

            var since = DateTimeOffset.UtcNow;
            var node = Recorder(db, "g3t10");
            var sw = Stopwatch.StartNew();
            var outcome = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var ms = sw.ElapsedMilliseconds;
            await tx.RollbackAsync();
            var (_, _, lines) = await TraceAsync("g3t10", since, "T-10-table-lock");
            var cancelled = CancelledStatement(lines);
            var evidence = await EvidenceCountAsync(db, org.Id);
            Record("T-10", "Failure", "statement bound inside T2 (catalogue check): the journal table held ACCESS EXCLUSIVE by another session; the catalogue check is cancelled by statement_timeout (500 ms) on the server; Deferred; nothing appended; well inside the budget",
                "Deferred; server statement-timeout at INDEX_CHECK; < 1500 ms; evidence 0", $"{outcome}; server {cancelled}; {ms} ms; evidence {evidence}",
                outcome == CapabilityRefusalEvidenceOutcome.Deferred && cancelled == "statement-timeout INDEX_CHECK" && ms < 1500 && evidence == 0);
        }

        // T-11: IX-B itself held ACCESS EXCLUSIVE (an open REINDEX INDEX in another session): the catalogue check does not lock the index,
        // the lookup does; the lookup is cancelled by statement_timeout; nothing appended; after the release the retry records it.
        {
            var org = await SeedOrgAsync(db);
            await using var holder = await OpenAdminAsync(db, "g3-index-holder");
            var tx = await holder.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand("REINDEX INDEX authority.ix_journal_capability_refusal_evidence", holder, tx))
            {
                await lockCmd.ExecuteNonQueryAsync();
            }

            var since = DateTimeOffset.UtcNow;
            var node = Recorder(db, "g3t11");
            var sw = Stopwatch.StartNew();
            var outcome = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var ms = sw.ElapsedMilliseconds;
            await tx.RollbackAsync();
            // Counted after the release and before the retry (planning any journal query needs a lock on IX-B, held until the rollback).
            var during = await EvidenceCountAsync(db, org.Id);
            var (_, _, lines) = await TraceAsync("g3t11", since, "T-11-index-lock");
            var cancelled = CancelledStatement(lines);
            node.Clock.Advance(TimeSpan.FromSeconds(6));
            await node.Recorder.RetryDueAsync(CancellationToken.None);
            var after = await EvidenceCountAsync(db, org.Id);
            var oracle = await OracleAsync(db, org.Id);
            Record("T-11", "Failure", "statement bound inside T2 (lookup): IX-B held ACCESS EXCLUSIVE by another session; the eight-row lookup is cancelled by statement_timeout (500 ms) on the server; Deferred; nothing appended; after the release the retry records exactly one entry",
                "Deferred; server statement-timeout at LOOKUP; < 1500 ms; evidence 0 -> 1; oracle holds", $"{outcome}; server {cancelled}; {ms} ms; evidence {during} -> {after}; {oracle}",
                outcome == CapabilityRefusalEvidenceOutcome.Deferred && cancelled == "statement-timeout LOOKUP" && ms < 1500 && during == 0 && after == 1 && oracle.Holds());
        }

        // T-12 (M2F3-31, outage half): an archive outage does not block recording; delivery catches up afterwards.
        {
            var org = await SeedOrgAsync(db);
            var node = Recorder(db, "g3t12");
            var down = new AuthorizationAuthority.Infrastructure.Archive.UnavailableSecurityEventArchive("m2f3g archive outage (test)");
            var first = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            string outage;
            try
            {
                await new AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor(Store(db, "g3t12-archive"), down, TimeProvider.System).ProcessOrganizationAsync(org.Id, CancellationToken.None);
                outage = "processed";
            }
            catch (Exception ex)
            {
                outage = Describe(ex);
            }

            var second = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None);
            var during = await ScalarAsync(db, $"SELECT string_agg(archive_delivery_state || '/' || archive_attempt_count, ',' ORDER BY journal_stream_revision) FROM authority.security_journal_entries WHERE organization_id = '{org.Id:D}' AND journal_stream_name = 'audit'");
            var up = new AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive();
            var processor = new AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor(Store(db, "g3t12-archive"), up, TimeProvider.System);
            for (var pass = 0; pass < 10 && await processor.ProcessOrganizationAsync(org.Id, CancellationToken.None) > 0; pass++)
            {
            }

            var after = await ScalarAsync(db, $"SELECT string_agg(archive_delivery_state, ',' ORDER BY journal_stream_revision) FROM authority.security_journal_entries WHERE organization_id = '{org.Id:D}' AND journal_stream_name = 'audit'");
            var oracle = await OracleAsync(db, org.Id);
            Record("T-12", "Liveness", "M2F3-31 (outage): with the archive unavailable, delivery of the evidence entry fails and stays pending, while a further refusal of another key is still recorded; once the archive is back the unchanged archive processor anchors both",
                "Committed; delivery fails; Committed; both not anchored during the outage; both Anchored afterwards; oracle holds", $"{first}; outage {Short(outage, 120)}; {second}; during [{during}]; after [{after}]; {oracle}",
                first == CapabilityRefusalEvidenceOutcome.Committed && second == CapabilityRefusalEvidenceOutcome.Committed && !during.Contains("Anchored", StringComparison.Ordinal) && after == "Anchored,Anchored" && oracle.Total == 2 && oracle.Holds());
        }
    }

    // ------------------------------------------------------------------------------------------------ group F
    private async Task<(CapabilityRefusalEvidenceOutcome First, long During, string RevisionsDuring, CapabilityRefusalEvidenceOutcome? Retried, long After, BoundResult Oracle, string Observed)> FaultRunAsync(
        string db, Org org, string application, string? addFault, string? removeFault, IReadOnlyList<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>? interceptors = null)
    {
        if (addFault is not null)
        {
            await ExecAsync(db, addFault);
        }

        var revBefore = await RevisionsAsync(db, org.Id);
        var node = Recorder(db, application, interceptors: interceptors);
        var first = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
        var during = await EvidenceCountAsync(db, org.Id);
        var revDuring = await RevisionsAsync(db, org.Id);
        if (removeFault is not null)
        {
            await ExecAsync(db, removeFault);
        }

        node.Clock.Advance(TimeSpan.FromSeconds(6));
        await node.Recorder.RetryDueAsync(CancellationToken.None);
        var retried = node.Outcomes.All.Skip(1).Select(o => (CapabilityRefusalEvidenceOutcome?)o.Outcome).FirstOrDefault();
        var after = await EvidenceCountAsync(db, org.Id);
        var oracle = await OracleAsync(db, org.Id);
        return (first, during, revBefore == revDuring ? "unchanged" : revBefore + " -> " + revDuring, retried, after, oracle, node.Outcomes.Summary());
    }

    private async Task FaultScenariosAsync()
    {
        CurrentGroup = "F";

        // F-01: the journal INSERT fails on the server (a NOT VALID CHECK on this copy only).
        {
            var db = await T2DatabaseAsync("g3_f1");
            var org = await SeedOrgAsync(db);
            var r = await FaultRunAsync(db, org, "g3f01", "ALTER TABLE authority.security_journal_entries ADD CONSTRAINT g3_fault_insert CHECK (journal_event_kind <> 'EnvelopeCapabilityRefused') NOT VALID",
                "ALTER TABLE authority.security_journal_entries DROP CONSTRAINT g3_fault_insert");
            Record("F-01", "Failure", "journal insert failure (server CHECK violation in T2's SaveChanges, after the commit was requested): classified Ambiguous (D-6, safe under L-C); the whole T2 rolled back (no entry, audit revision unchanged); the retry reconciles against the journal and records exactly one entry",
                "Ambiguous; during 0, revisions unchanged; retry Committed; after 1; oracle holds", $"{r.First}; during {r.During}, revisions {r.RevisionsDuring}; retry {r.Retried}; after {r.After}; {r.Oracle} ({r.Observed})",
                r.First == CapabilityRefusalEvidenceOutcome.Ambiguous && r.During == 0 && r.RevisionsDuring == "unchanged" && r.Retried == CapabilityRefusalEvidenceOutcome.Committed && r.After == 1 && r.Oracle.Holds());
        }

        // F-02: the organization-row UPDATE fails on the server.
        {
            var db = await T2DatabaseAsync("g3_f2");
            var org = await SeedOrgAsync(db);
            var r = await FaultRunAsync(db, org, "g3f02", "ALTER TABLE authority.organization_authorization_states ADD CONSTRAINT g3_fault_update CHECK (audit_journal_revision = 0) NOT VALID",
                "ALTER TABLE authority.organization_authorization_states DROP CONSTRAINT g3_fault_update");
            Record("F-02", "Failure", "organization-row update failure (server CHECK violation on the audit head update): Ambiguous; the journal INSERT of the same transaction rolled back with it (no entry, revision unchanged); the retry records exactly one",
                "Ambiguous; during 0, revisions unchanged; retry Committed; after 1; oracle holds", $"{r.First}; during {r.During}, revisions {r.RevisionsDuring}; retry {r.Retried}; after {r.After}; {r.Oracle} ({r.Observed})",
                r.First == CapabilityRefusalEvidenceOutcome.Ambiguous && r.During == 0 && r.RevisionsDuring == "unchanged" && r.Retried == CapabilityRefusalEvidenceOutcome.Committed && r.After == 1 && r.Oracle.Holds());
        }

        // F-03: COMMIT itself fails on the server (a deferred constraint trigger on this copy only).
        {
            var db = await T2DatabaseAsync("g3_f3");
            var org = await SeedOrgAsync(db);
            await ExecAsync(db, "CREATE FUNCTION public.g3_commit_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'g3 commit fault'; END $$");
            var r = await FaultRunAsync(db, org, "g3f03", "CREATE CONSTRAINT TRIGGER g3_commit_fault AFTER INSERT ON authority.security_journal_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.g3_commit_fault()",
                "DROP TRIGGER g3_commit_fault ON authority.security_journal_entries");
            Record("F-03", "Failure", "commit failure (a deferred constraint trigger fails COMMIT on the server): Ambiguous; nothing durable; the retry records exactly one",
                "Ambiguous; during 0, revisions unchanged; retry Committed; after 1; oracle holds", $"{r.First}; during {r.During}, revisions {r.RevisionsDuring}; retry {r.Retried}; after {r.After}; {r.Oracle} ({r.Observed})",
                r.First == CapabilityRefusalEvidenceOutcome.Ambiguous && r.During == 0 && r.RevisionsDuring == "unchanged" && r.Retried == CapabilityRefusalEvidenceOutcome.Committed && r.After == 1 && r.Oracle.Holds());
        }

        // F-04 (M2F3-35): the commit succeeded but its outcome was lost (the process "crashes" right after COMMIT returned).
        {
            var db = await T2DatabaseAsync("g3_f4");
            var org = await SeedOrgAsync(db);
            var hook = new CommitHook();
            var armed = 1;
            hook.AfterCommit = () => Interlocked.Exchange(ref armed, 0) == 1 ? throw new SimulatedCrashException("after T2 COMMIT") : Task.CompletedTask;
            var r = await FaultRunAsync(db, org, "g3f04", null, null, [hook]);
            var restart = Recorder(db, "g3f04-restart");
            var restarted = await restart.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var oracle = await OracleAsync(db, org.Id);
            Record("F-04", "Failure", "M2F3-35 on PostgreSQL: COMMIT succeeded, its outcome was lost (exception after the commit returned): Ambiguous; the entry IS durable; the same process's retry and a restarted process both reconcile against the journal (AlreadyRecorded), never appending a second entry",
                "Ambiguous; during 1; retry AlreadyRecorded; restart AlreadyRecorded; evidence 1; oracle holds", $"{r.First}; during {r.During}; retry {r.Retried}; restart {restarted}; evidence {oracle.Total}; {oracle}",
                r.First == CapabilityRefusalEvidenceOutcome.Ambiguous && r.During == 1 && r.Retried == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && restarted == CapabilityRefusalEvidenceOutcome.AlreadyRecorded && oracle.Total == 1 && oracle.Holds());
        }

        // F-05: the outcome is lost because the session dies before COMMIT reaches the server (rolled back).
        {
            var db = await T2DatabaseAsync("g3_f5");
            var org = await SeedOrgAsync(db);
            var hook = new CommitHook();
            var armed = 1;
            hook.BeforeCommit = async transaction =>
            {
                if (Interlocked.Exchange(ref armed, 0) == 1 && transaction.Connection is NpgsqlConnection connection)
                {
                    await ExecAsync(db, $"SELECT pg_terminate_backend({connection.ProcessID})");
                    await Task.Delay(300);
                }
            };
            var r = await FaultRunAsync(db, org, "g3f05", null, null, [hook]);
            Record("F-05", "Failure", "unknown outcome, rolled back: the T2 session is terminated just before COMMIT: Ambiguous; nothing durable; the retry finds the key absent and records exactly one entry",
                "Ambiguous; during 0, revisions unchanged; retry Committed; after 1; oracle holds", $"{r.First}; during {r.During}, revisions {r.RevisionsDuring}; retry {r.Retried}; after {r.After}; {r.Oracle} ({r.Observed})",
                r.First == CapabilityRefusalEvidenceOutcome.Ambiguous && r.During == 0 && r.RevisionsDuring == "unchanged" && r.Retried == CapabilityRefusalEvidenceOutcome.Committed && r.After == 1 && r.Oracle.Holds());
        }

        var fdb = await T2DatabaseAsync("g3_f6");

        // F-06 (M2F3-34 in-process): the refused T1 committed nothing; a crash before T2 leaves no evidence; the next refusal records it.
        {
            var org = await SeedOrgAsync(fdb);
            var printBefore = await FingerprintAsync(fdb, org.Id);
            var service = Service(fdb, "g3f06-t1");
            string refusal;
            try
            {
                await service.VerifyProjectionAsync(Actor(org), new AuthorizationAuthority.Contracts.V1.VerifyRecordProjectionRequest(org.Id, org.Records[0].Id, AuthorizationAuthority.Contracts.V1.RequestedAction.Read, []), Guid.CreateVersion7(), CancellationToken.None, ClientEnvelopeCapability.Legacy);
                refusal = "ADMITTED";
            }
            catch (Exception ex)
            {
                refusal = Describe(ex); // the process "crashes" here: the recorder is never called
            }

            var crashed = await EvidenceCountAsync(fdb, org.Id);
            var printAfter = await FingerprintAsync(fdb, org.Id);
            var node = Recorder(fdb, "g3f06");
            var (refusal2, outcome2, _) = await RefusePrepareInProcessAsync(Service(fdb, "g3f06-t1b"), node, org.Records[0]);
            var oracle = await OracleAsync(fdb, org.Id);
            Record("F-06", "Failure", "M2F3-34 in-process: a refused T1 (level 1, D = 2) committed nothing (organization fingerprint and audit head unchanged); a crash before T2 leaves no evidence; the next refusal records exactly one entry",
                "409; evidence 0; fingerprint unchanged; next 409 Committed; evidence 1", $"{refusal}; evidence {crashed}; fingerprint {FingerprintDiff(printBefore, printAfter)}; next {refusal2} {outcome2}; {oracle}",
                refusal.Contains("CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT 409", StringComparison.Ordinal) && crashed == 0 && printBefore == printAfter && outcome2 == CapabilityRefusalEvidenceOutcome.Committed && oracle.Total == 1 && oracle.Holds());
        }

        // F-07 (M2F3-65): clock regression on PostgreSQL (the newest entry is dated after t_db, as after a backward clock step or failover).
        {
            var org = await SeedOrgAsync(fdb);
            var at = await DatabaseNowAsync(fdb);
            var ahead = at.AddSeconds(4);
            if (CapabilityRefusalEvidence.BucketOf(ahead) != CapabilityRefusalEvidence.BucketOf(at))
            {
                await Task.Delay(ahead - at + TimeSpan.FromSeconds(1)); // keep the scenario inside one bucket
                at = await DatabaseNowAsync(fdb);
                ahead = at.AddSeconds(4);
            }

            await AppendAuditAsync(org, CapabilityRefusalEvidence.EventKind, EvidenceBody(org, CapabilityRefusalEvidence.EntryPoints.Commit, ahead));
            var node = Recorder(fdb, "g3f07");
            var outcome = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var reason = node.Outcomes.All.Last().Reason;
            var during = await EvidenceCountAsync(fdb, org.Id);
            await Task.Delay(TimeSpan.FromSeconds(5)); // t_db overtakes the entry
            node.Clock.Advance(TimeSpan.FromSeconds(6));
            await node.Recorder.RetryDueAsync(CancellationToken.None);
            var resumed = node.Outcomes.All.Last();
            var oracle = await OracleAsync(fdb, org.Id);
            Record("F-07", "Failure", "M2F3-65 on PostgreSQL: the newest entry (controlled setup through the real AuditJournal) is dated 4 s after t_db; T2 defers (CLOCK_REGRESSION; under D-3b-2 a retry is scheduled, log 9253, rather than the 9254 named in the closure text) and appends nothing; once t_db has passed the entry the retry records exactly one entry",
                "Deferred ClockRegression; evidence 1 (seeded); retry Committed; evidence 2; oracle holds", $"{outcome} {reason}; evidence {during}; retry {resumed.Outcome}; {oracle}",
                outcome == CapabilityRefusalEvidenceOutcome.Deferred && reason == CapabilityRefusalEvidenceReason.ClockRegression && during == 1 && resumed.Outcome == CapabilityRefusalEvidenceOutcome.Committed && oracle.Total == 2 && oracle.Holds());
        }

        // F-10 (M2F3-64): a non-recorder audit entry already occupies the next revision (controlled setup with the container superuser,
        // emulating a concurrent non-recorder audit append): T2's insert hits the unique (organization, stream, revision) key; nothing appended.
        {
            var org = await SeedOrgAsync(fdb);
            await ExecAsync(fdb, $"""
                INSERT INTO authority.security_journal_entries (security_journal_entry_id, organization_id, journal_stream_name, journal_stream_revision, journal_event_kind,
                    journal_event_body_json, journal_event_digest, archive_delivery_state, archive_attempt_count, journal_created_on)
                VALUES (gen_random_uuid(), '{org.Id:D}', 'audit', 1, 'FieldSearchExecuted', '{"{"}"synthetic":"m2f3g"{"}"}', sha256(convert_to('{"{"}"synthetic":"m2f3g"{"}"}', 'UTF8')), 'Pending', 0, now())
                """);
            var node = Recorder(fdb, "g3f10");
            var outcome = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var evidence = await EvidenceCountAsync(fdb, org.Id);
            var revisions = await RevisionsAsync(fdb, org.Id);
            Record("F-10", "Failure", "M2F3-64 on PostgreSQL: a database error at the insert (unique violation: a non-recorder audit entry already at the revision T2 computed) leaves nothing appended and the audit head unchanged (Ambiguous, retried; the retry meets the same occupied revision until the inconsistency is resolved: evidence is lost, never duplicated)",
                "Ambiguous; evidence 0; audit head still 0", $"{outcome}; evidence {evidence}; {revisions}",
                outcome == CapabilityRefusalEvidenceOutcome.Ambiguous && evidence == 0 && revisions.StartsWith("audit 0;", StringComparison.Ordinal));
        }

        // F-08: invalid bodies are never trusted: (a) an unapproved key; (b) a stored body altered after commit (digest mismatch).
        {
            var orgA = await SeedOrgAsync(fdb);
            var now = await DatabaseNowAsync(fdb);
            await AppendAuditAsync(orgA, CapabilityRefusalEvidence.EventKind, new CapabilityRefusalEvidenceBody(1, CapabilityRefusalEvidence.RefusalCode, orgA.Id, "other", "export", 1, now, CapabilityRefusalEvidence.BucketOf(now), now));
            var nodeA = Recorder(fdb, "g3f08a");
            var a1 = await nodeA.Recorder.RecordAsync(Refusal(orgA), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var reasonA = nodeA.Outcomes.All.Last().Reason;
            var a2 = await nodeA.Recorder.RecordAsync(Refusal(orgA), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);

            var orgB = await SeedOrgAsync(fdb);
            await AppendAuditAsync(orgB, CapabilityRefusalEvidence.EventKind, EvidenceBody(orgB, CapabilityRefusalEvidence.EntryPoints.Commit, await DatabaseNowAsync(fdb)));
            await using (var c = new NpgsqlConnection(Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Database = fdb, Options = "-c session_replication_role=replica", Pooling = false }.ConnectionString)))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, '\"business-api\"', '\"other\"') WHERE organization_id = '{orgB.Id:D}' AND journal_event_kind = 'EnvelopeCapabilityRefused'", c);
                await cmd.ExecuteNonQueryAsync();
            }

            var nodeB = Recorder(fdb, "g3f08b");
            var b1 = await nodeB.Recorder.RecordAsync(Refusal(orgB), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var reasonB = nodeB.Outcomes.All.Last().Reason;
            var oracleA = await OracleAsync(fdb, orgA.Id);
            var oracleB = await OracleAsync(fdb, orgB.Id);
            Record("F-08", "Failure", "invalid evidence is never trusted on PostgreSQL: (a) an entry with an unapproved key (controlled setup, valid digest) makes the lookup inconclusive (UNVERIFIABLE_BODY, 9254), the next refusal is suppressed by the negative cache; (b) an entry altered in place after commit (digest no longer matches) makes the lookup inconclusive; nothing is appended in either case; the independent oracle sees the same defects",
                "(a) Inconclusive UnverifiableBody, SuppressedInconclusive, evidence 1, oracle invalid 1; (b) Inconclusive UnverifiableBody, evidence 1, oracle digest mismatch 1",
                $"(a) {a1} {reasonA}, {a2}, evidence {oracleA.Total}, invalid {oracleA.InvalidBodies}; (b) {b1} {reasonB}, evidence {oracleB.Total}, digest mismatches {oracleB.DigestMismatches}",
                a1 == CapabilityRefusalEvidenceOutcome.Inconclusive && reasonA == CapabilityRefusalEvidenceReason.UnverifiableBody && a2 == CapabilityRefusalEvidenceOutcome.SuppressedInconclusive && oracleA.Total == 1 && oracleA.InvalidBodies == 1
                && b1 == CapabilityRefusalEvidenceOutcome.Inconclusive && reasonB == CapabilityRefusalEvidenceReason.UnverifiableBody && oracleB.Total == 1 && oracleB.DigestMismatches == 1);
        }

        // F-09 (MZ-22 re-review on the PostgreSQL path): eight current-hour entries necessarily contain a duplicate key (seven approved keys).
        {
            var org = await SeedOrgAsync(fdb);
            var now = await DatabaseNowAsync(fdb);
            var keys = Seven.Concat([CapabilityRefusalEvidence.EntryPoints.Verify]).ToList();
            for (var i = 0; i < keys.Count; i++)
            {
                await AppendAuditAsync(org, CapabilityRefusalEvidence.EventKind, EvidenceBody(org, keys[i], now.AddMilliseconds(i)));
            }

            var node = Recorder(fdb, "g3f09");
            var outcome = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None);
            var reason = node.Outcomes.All.Last().Reason;
            var oracle = await OracleAsync(fdb, org.Id);
            Record("F-09", "Safety", "MZ-22 equivalence on the PostgreSQL path: eight current-hour entries can only exist with a duplicate key (seven approved keys; any other key is UNVERIFIABLE_BODY first); the duplicate makes T2 inconclusive (BOUND_INCONSISTENT) and nothing is appended. The rule MZ-22 removes ('eight rows, none older') is therefore never the deciding rule",
                "Inconclusive BoundInconsistent; evidence 8 (seeded); oracle max per (key, bucket) 2", $"{outcome} {reason}; evidence {oracle.Total}; max per (key, bucket) {oracle.MaxPerKeyBucket}",
                outcome == CapabilityRefusalEvidenceOutcome.Inconclusive && reason == CapabilityRefusalEvidenceReason.BoundInconsistent && oracle.Total == 8 && oracle.MaxPerKeyBucket == 2);
        }
    }

    // ------------------------------------------------------------------------------------------------ group Q
    private async Task QuotaRetryScenariosAsync()
    {
        CurrentGroup = "Q";
        var db = await T2DatabaseAsync("g3_q1");

        // Q-01: the per-process attempt limit (D-7): a refusal that finds the quota exhausted is NOT recorded in that hour by this process.
        {
            var org = await SeedOrgAsync(db);
            var since = DateTimeOffset.UtcNow;
            var node = Recorder(db, "g3q01", CapabilityRefusalEvidenceLimits.Default with { AttemptLimitPerWindow = 2 });
            var a = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var b = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None);
            var c = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None);
            var (classes, _, _) = await TraceAsync("g3q01", since, "Q-01-quota");
            var during = await EvidenceCountAsync(db, org.Id);
            node.Clock.Advance(TimeSpan.FromMinutes(61));
            var d = await node.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None);
            var oracle = await OracleAsync(db, org.Id);
            Record("Q-01", "Liveness", "D-7 attempt limit (limit 2 for the test) on PostgreSQL: the third key is QuotaSuppressed (9254) and causes no T2 at all (two lookups in the trace); it is not recorded while the window lasts (no unconditional first-refusal liveness under quota); after the window it is recorded",
                "Committed, Committed, QuotaSuppressed; lookups 2; evidence 2; after the window Committed; evidence 3; oracle holds",
                $"{a}, {b}, {c}; lookups {classes.Count(x => x == "LOOKUP")}; evidence {during}; after the window {d}; {oracle}",
                a == CapabilityRefusalEvidenceOutcome.Committed && b == CapabilityRefusalEvidenceOutcome.Committed && c == CapabilityRefusalEvidenceOutcome.QuotaSuppressed && classes.Count(x => x == "LOOKUP") == 2
                && during == 2 && d == CapabilityRefusalEvidenceOutcome.Committed && oracle.Total == 3 && oracle.Holds());
        }

        // Q-02 / Q-03 / Q-04: retry capacity, retry exhaustion and restart, with a persistent journal-insert fault on a dedicated copy.
        {
            var qdb = await T2DatabaseAsync("g3_q2");
            var org = await SeedOrgAsync(qdb);
            await ExecAsync(qdb, "ALTER TABLE authority.security_journal_entries ADD CONSTRAINT g3_fault_insert CHECK (journal_event_kind <> 'EnvelopeCapabilityRefused') NOT VALID");
            var node = Recorder(qdb, "g3q02", CapabilityRefusalEvidenceLimits.Default with { RetryCapacity = 2 });
            var first = new List<CapabilityRefusalEvidenceOutcome>();
            foreach (var e in new[] { CapabilityRefusalEvidence.EntryPoints.Verify, CapabilityRefusalEvidence.EntryPoints.Prepare, CapabilityRefusalEvidence.EntryPoints.Commit })
            {
                first.Add(await node.Recorder.RecordAsync(Refusal(org), e, CancellationToken.None));
            }

            var reasons = string.Join(",", node.Outcomes.All.Select(o => o.Outcome + "/" + o.Reason));
            var pendingAfterCapacity = node.Recorder.PendingCount;
            Record("Q-02", "Liveness", "retry capacity (capacity 2 for the test), persistent server-side fault: two keys wait for a retry (Ambiguous, 9253); the third is Abandoned with RETRY_CAPACITY (9254); nothing is appended",
                "Ambiguous, Ambiguous, Abandoned/RetryCapacity; pending 2; evidence 0", $"{reasons}; pending {pendingAfterCapacity}; evidence {await EvidenceCountAsync(qdb, org.Id)}",
                first[0] == CapabilityRefusalEvidenceOutcome.Ambiguous && first[1] == CapabilityRefusalEvidenceOutcome.Ambiguous && node.Outcomes.All.Any(o => o.Outcome == CapabilityRefusalEvidenceOutcome.Abandoned && o.Reason == CapabilityRefusalEvidenceReason.RetryCapacity)
                && pendingAfterCapacity == 2 && await EvidenceCountAsync(qdb, org.Id) == 0);

            for (var step = 0; step < 16; step++)
            {
                node.Clock.Advance(TimeSpan.FromMinutes(5));
                await node.Recorder.RetryDueAsync(CancellationToken.None);
            }

            var abandoned = node.Outcomes.All.Count(o => o.Outcome == CapabilityRefusalEvidenceOutcome.Abandoned && o.Reason == CapabilityRefusalEvidenceReason.Abandoned);
            var oracle = await OracleAsync(qdb, org.Id);
            Record("Q-03", "Liveness", "retry exhaustion: with the fault persisting, the waiting keys are retried with backoff (still no append) and abandoned after one hour (ABANDONED, 9254); the oracle holds",
                "abandoned 2; pending 0; evidence 0; oracle holds", $"abandoned {abandoned}; pending {node.Recorder.PendingCount}; evidence {oracle.Total}; outcomes {node.Outcomes.Summary()}; {oracle}",
                abandoned == 2 && node.Recorder.PendingCount == 0 && oracle.Total == 0 && oracle.Holds());

            // Q-04 (D-4): a process restart loses pending retries; the next refusal after the fault is gone records it.
            var node2 = Recorder(qdb, "g3q04");
            var pendingBefore = await node2.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var restarted = Recorder(qdb, "g3q04-restarted"); // the first process "restarts": its retry is gone
            await ExecAsync(qdb, "ALTER TABLE authority.security_journal_entries DROP CONSTRAINT g3_fault_insert");
            var lostRetry = await EvidenceCountAsync(qdb, org.Id);
            var next = await restarted.Recorder.RecordAsync(Refusal(org), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None);
            var oracle2 = await OracleAsync(qdb, org.Id);
            Record("Q-04", "Liveness", "D-4: a retry scheduled in a process that restarts is lost (nothing durable is pending); the next refusal after the fault cleared records exactly one entry",
                "Ambiguous; after restart evidence 0; next Committed; evidence 1; oracle holds", $"{pendingBefore}; after restart evidence {lostRetry}; next {next}; {oracle2}",
                pendingBefore == CapabilityRefusalEvidenceOutcome.Ambiguous && lostRetry == 0 && next == CapabilityRefusalEvidenceOutcome.Committed && oracle2.Total == 1 && oracle2.Holds());
        }
    }
}
