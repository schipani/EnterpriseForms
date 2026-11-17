// E3-M2f-3G group B: a REAL hour boundary of the database clock (the harness waits for the next HH:00, bounded by
// G3_BOUNDARY_MAX_WAIT_MINUTES; otherwise the group is reported NOT EXECUTED). M2F3-69 (two T2s that acquire the organization lock in
// reverse start order across the boundary), later-hour liveness, and M2F3-29 (a fleet flood across two buckets with level-2 latency
// measured on the real host). The transaction_timestamp() variant is a demonstration with plain SQL sessions, outside the pass totals.
using System.Diagnostics;
using AuthorizationAuthority.Application.Features.Content;
using Npgsql;

internal sealed partial class Lab
{
    /// <summary>Database clock minus harness clock (the database runs in the Docker VM); scheduling and comparisons use database time.</summary>
    private TimeSpan skew = TimeSpan.Zero;

    private async Task<TimeSpan> MeasureSkewAsync(string database)
    {
        var samples = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            var before = DateTimeOffset.UtcNow;
            var db = await DatabaseNowAsync(database);
            var after = DateTimeOffset.UtcNow;
            samples.Add(db - (before + (after - before) / 2));
        }

        return samples.Order().ElementAt(2); // median
    }

    /// <summary>Waits until the DATABASE clock reaches <paramref name="when"/>.</summary>
    private async Task WaitUntilAsync(DateTimeOffset when)
    {
        var delay = when - skew - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }
    }

    private async Task BoundaryScenariosAsync()
    {
        CurrentGroup = "B";
        var now = await DatabaseNowAsync(Maintenance);
        var boundary = CapabilityRefusalEvidence.BucketOf(now).AddHours(1);
        if (boundary - now > TimeSpan.FromMinutes(inputs.BoundaryMaxWaitMinutes) || boundary - now < TimeSpan.FromMinutes(3))
        {
            // Too close (no time to prepare) is treated like too far: the next boundary would exceed the wait bound.
            if (boundary - now < TimeSpan.FromMinutes(3))
            {
                boundary = boundary.AddHours(1);
            }

            if (boundary - now > TimeSpan.FromMinutes(inputs.BoundaryMaxWaitMinutes))
            {
                NotExecuted("M2F3-69 / M2F3-29 (two buckets) / later-hour liveness", $"the next usable hour boundary {boundary:HH:mm} is {(boundary - now).TotalMinutes:F0} minutes away (bound {inputs.BoundaryMaxWaitMinutes})");
                return;
            }
        }

        skew = await MeasureSkewAsync(Maintenance);
        Log($"group B: waiting for the hour boundary {boundary:O} ({(boundary - now).TotalMinutes:F1} minutes); database clock minus harness clock {skew.TotalMilliseconds:F0} ms");
        var db = await T2DatabaseAsync("g3_b1");
        var bOrder = await SeedOrgAsync(db);
        var bLate = await SeedOrgAsync(db);
        var bDemo = await SeedOrgAsync(db);
        var fleetDb = await HostDatabaseAsync("g3_b2");
        var fleet = new List<Org>();
        for (var i = 0; i < 25; i++)
        {
            fleet.Add(await HostOrgAsync(fleetDb));
        }

        var (host, detail) = await StartHostAsync(fleetDb, "b-latency");
        if (host is null)
        {
            Record("B-00", "Setup", "the real host for the level-2 latency measurement", "live", detail, false);
            return;
        }

        // Baseline level-2 latency (no flood): level-2 backfill requests pass the capability check and take the same organization lock as T2.
        var baseline = new List<long>();
        for (var i = 0; i < 40; i++)
        {
            baseline.Add((await SendAsync(host, CapabilityRefusalEvidence.EntryPoints.IndexBackfill, fleet[i % 5], ["2"])).Milliseconds);
        }

        await WaitUntilAsync(boundary.AddSeconds(-45));

        // B-03 flood: new processes every 3 s, each recording all 175 keys of the 25 organizations, from 45 s before to 40 s after the boundary.
        var floodStop = boundary.AddSeconds(40);
        var floodOutcomes = new System.Collections.Concurrent.ConcurrentBag<CapabilityRefusalEvidenceOutcome>();
        var stores = Enumerable.Range(0, 3).Select(i => Store(fleetDb, "g3b3-" + i)).ToList();
        var flood = Task.Run(async () =>
        {
            var round = 0;
            while (DateTimeOffset.UtcNow + skew < floodStop)
            {
                var recorders = stores.Select(s => new CapabilityRefusalEvidenceRecorder(s, TimeProvider.System, null, null)).ToList();
                var work = recorders.SelectMany(r => fleet.SelectMany(o => Seven.Select(e => (r, o, e)))).ToList();
                using var gate = new SemaphoreSlim(12);
                await Task.WhenAll(work.Select(async w =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        floodOutcomes.Add(await w.r.RecordAsync(Refusal(w.o), w.e, CancellationToken.None));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                round++;
                await Task.Delay(3000);
            }
        });
        var during = new List<long>();
        var latency = Task.Run(async () =>
        {
            var i = 0;
            while (DateTimeOffset.UtcNow + skew < floodStop)
            {
                during.Add((await SendAsync(host, CapabilityRefusalEvidence.EntryPoints.IndexBackfill, fleet[i++ % 5], ["2"])).Milliseconds);
                await Task.Delay(250);
            }
        });

        // B-02 setup: all seven keys of bLate recorded in the closing hour.
        await WaitUntilAsync(boundary.AddSeconds(-20));
        var lateBefore = new List<CapabilityRefusalEvidenceOutcome>();
        var lateNode = Recorder(db, "g3b2-before");
        foreach (var e in Seven)
        {
            lateBefore.Add(await lateNode.Recorder.RecordAsync(Refusal(bLate), e, CancellationToken.None));
        }

        // Demonstration setup: session C begins its transaction before the boundary and takes no lock yet.
        await WaitUntilAsync(boundary.AddSeconds(-1.0));
        await using var demoC = await OpenAdminAsync(db, "g3b-demo-c");
        var txC = await demoC.BeginTransactionAsync();
        DateTimeOffset tC;
        await using (var c = new NpgsqlCommand("SELECT transaction_timestamp()", demoC, txC))
        {
            tC = ((DateTime)(await c.ExecuteScalarAsync())!).ToUniversalTime();
        }

        // B-01: T2 A starts before the boundary (its first statement runs at once) and is held just before its organization lock
        // until T2 B (started after the boundary) has committed. A stays inside its 2 s budget.
        var bCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new StatementHook(t => t.Contains("organization_authorization_states", StringComparison.Ordinal) && t.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase), async _ =>
        {
            await Task.WhenAny(bCommitted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        });
        var observeA = new StatementObserver();
        var observeB = new StatementObserver();
        var nodeA = Recorder(db, "g3b1-a", interceptors: [observeA, hook]);
        var nodeB = Recorder(db, "g3b1-b", interceptors: [observeB]);
        await WaitUntilAsync(boundary.AddSeconds(-0.6));
        var aTask = Task.Run(() => nodeA.Recorder.RecordAsync(Refusal(bOrder), CapabilityRefusalEvidence.EntryPoints.Verify, CancellationToken.None));
        await WaitUntilAsync(boundary.AddSeconds(0.1));
        var bOutcome = await nodeB.Recorder.RecordAsync(Refusal(bOrder), CapabilityRefusalEvidence.EntryPoints.Commit, CancellationToken.None);
        bCommitted.TrySetResult();
        var aOutcome = await aTask;

        // Demonstration (outside the pass totals): session E starts after the boundary, locks, "appends" and commits first.
        await using var demoE = await OpenAdminAsync(db, "g3b-demo-e");
        var txE = await demoE.BeginTransactionAsync();
        DateTimeOffset tE;
        await using (var c = new NpgsqlCommand($"SELECT transaction_timestamp() FROM authority.organization_authorization_states WHERE organization_id = '{bDemo.Id:D}' FOR UPDATE", demoE, txE))
        {
            tE = ((DateTime)(await c.ExecuteScalarAsync())!).ToUniversalTime();
        }

        await txE.CommitAsync();
        DateTimeOffset cLockedClock;
        await using (var c = new NpgsqlCommand($"SELECT clock_timestamp() FROM authority.organization_authorization_states WHERE organization_id = '{bDemo.Id:D}' FOR UPDATE", demoC, txC))
        {
            cLockedClock = ((DateTime)(await c.ExecuteScalarAsync())!).ToUniversalTime();
        }

        await txC.CommitAsync();

        // B-01 checks.
        {
            var rows = await LinesAsync(db, $"SELECT journal_stream_revision || '|' || (journal_event_body_json::jsonb->'body'->>'entry_point') || '|' || (journal_event_body_json::jsonb->'body'->>'evidence_bucket_start_utc') || '|' || (journal_event_body_json::jsonb->'body'->>'evidence_database_time_utc') FROM authority.security_journal_entries WHERE organization_id = '{bOrder.Id:D}' AND journal_event_kind = 'EnvelopeCapabilityRefused' ORDER BY journal_stream_revision");
            // The runtime role logs no statements (product setting), so the first T2 command of A and B is timed on the EF side (the
            // moment SET LOCAL lock_timeout was sent, which opens the transaction) and checked against the database clock's boundary.
            DateTimeOffset? aStart = observeA.Sent.Count > 0 ? observeA.Sent[0].At + skew : null; // in database time
            DateTimeOffset? bStart = observeB.Sent.Count > 0 ? observeB.Sent[0].At + skew : null;
            var aFirstIsLockBound = observeA.Sent.Count > 0 && Classify(observeA.Sent[0].Text) == "SET_LOCK";
            var ordered = rows.Count == 2 && rows[0].Split('|')[1] == CapabilityRefusalEvidence.EntryPoints.Commit && rows[1].Split('|')[1] == CapabilityRefusalEvidence.EntryPoints.Verify;
            var buckets = rows.Select(r => DateTimeOffset.Parse(r.Split('|')[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var nonDecreasing = buckets.Zip(buckets.Skip(1), (x, y) => x <= y).All(b => b);
            var oracle = await OracleAsync(db, bOrder.Id);
            Record("B-01", "Safety", "M2F3-69 at a real hour boundary: T2 A starts before the boundary but acquires the organization lock only after T2 B (started after the boundary) committed; with t_db = clock_timestamp() read after the lock, both entries fall in the new bucket and the buckets are non-decreasing along revisions; the oracle (I-1, ≤ 1 per key per bucket) holds",
                $"A's first T2 command (SET LOCAL lock_timeout) sent before {boundary:HH:mm:ss}, B's after; B Committed first (commit), A Committed second (verify); both buckets {boundary:HH:mm}; non-decreasing; oracle holds",
                $"skew {skew.TotalMilliseconds:F0} ms; A first command {aStart:HH:mm:ss.fff} (lock bound {aFirstIsLockBound}); B first command {bStart:HH:mm:ss.fff}; B {bOutcome}; A {aOutcome}; rows [{string.Join(" ; ", rows)}]; non-decreasing {nonDecreasing}; {oracle}",
                aStart is { } s0 && s0 < boundary && aFirstIsLockBound && bStart is { } s1 && s1 >= boundary && bOutcome == CapabilityRefusalEvidenceOutcome.Committed && aOutcome == CapabilityRefusalEvidenceOutcome.Committed && ordered
                && buckets.All(b => b == boundary) && nonDecreasing && oracle.Holds() && oracle.TimeOrderViolations == 0);
            Demonstrate("B-01d", "COUNTERFACTUAL_TRANSACTION_TIMESTAMP", "§5.2 counterfactual with plain SQL sessions (no journal write): session C began its transaction before the boundary, session E after it; E locked and committed first, C locked second. Had the bucket been taken from transaction_timestamp(), C (later in lock order) would carry the EARLIER bucket: the order would decrease. clock_timestamp() read after C's lock gives the new bucket",
                "bucket(transaction_timestamp C) < bucket(transaction_timestamp E); bucket(clock_timestamp after C's lock) = new bucket",
                $"C transaction_timestamp {tC:HH:mm:ss.fff} (bucket {CapabilityRefusalEvidence.BucketOf(tC):HH:mm}); E transaction_timestamp {tE:HH:mm:ss.fff} (bucket {CapabilityRefusalEvidence.BucketOf(tE):HH:mm}); C clock_timestamp after its lock {cLockedClock:HH:mm:ss.fff} (bucket {CapabilityRefusalEvidence.BucketOf(cLockedClock):HH:mm})",
                CapabilityRefusalEvidence.BucketOf(tC) < CapabilityRefusalEvidence.BucketOf(tE) && CapabilityRefusalEvidence.BucketOf(cLockedClock) == boundary);
        }

        // B-02: later hour: every key of bLate is recorded again in the new bucket.
        {
            await WaitUntilAsync(boundary.AddSeconds(2));
            var after = new List<CapabilityRefusalEvidenceOutcome>();
            var node = Recorder(db, "g3b2-after");
            foreach (var e in Seven)
            {
                after.Add(await node.Recorder.RecordAsync(Refusal(bLate), e, CancellationToken.None));
            }

            var oracle = await OracleAsync(db, bLate.Id);
            Record("B-02", "Liveness", "later hour at a real boundary: the seven keys recorded in the closing hour are each recorded again (first attempt) in the new hour: 14 entries, 7 per bucket",
                "before Committed x7; after Committed x7; evidence 14; max per bucket 7; oracle holds", $"before {string.Join(",", lateBefore.Distinct())} x{lateBefore.Count}; after {string.Join(",", after.Distinct())} x{after.Count}; {oracle}",
                lateBefore.All(o => o == CapabilityRefusalEvidenceOutcome.Committed) && after.All(o => o == CapabilityRefusalEvidenceOutcome.Committed) && oracle.Total == 14 && oracle.MaxPerBucket == 7 && oracle.Holds());
        }

        // B-03 (M2F3-29): the flood across two buckets.
        {
            await flood;
            await latency;
            var perBucket = await PerBucketAsync(fleetDb, fleet);
            var (holds, fleetDetail) = await FleetOracleAsync(fleetDb, fleet);
            var outcomeSummary = string.Join(",", floodOutcomes.GroupBy(o => o).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => g.Key + "x" + g.Count()));
            var exact = perBucket.Count == 2 && perBucket.All(l => l.EndsWith("=175", StringComparison.Ordinal));
            var p95Base = Percentile(baseline, 0.95);
            var p95During = Percentile(during, 0.95);
            Record("B-03", "Safety", "M2F3-29 fleet flood across a real hour boundary: 25 organizations x 7 keys refused continuously by new recorder processes every 3 s from 45 s before to 40 s after the boundary: each bucket holds exactly 175 entries (≤ 7 x 25), every organization ≤ 7 per bucket; no attempt failed; level-2 requests (backfill, same organization locks) stay within 1 s at p95 during the flood",
                "2 buckets x 175; fleet oracle holds; outcomes only Committed/AlreadyRecorded/Suppressed*; level-2 p95 during < 1000 ms",
                $"buckets [{string.Join(", ", perBucket)}]; {fleetDetail}; outcomes {outcomeSummary}; level-2 p50/p95/max baseline {Percentile(baseline, 0.5)}/{p95Base}/{baseline.Max()} ms, during {Percentile(during, 0.5)}/{p95During}/{(during.Count == 0 ? 0 : during.Max())} ms ({during.Count} requests)",
                exact && holds && floodOutcomes.All(o => o is CapabilityRefusalEvidenceOutcome.Committed or CapabilityRefusalEvidenceOutcome.AlreadyRecorded or CapabilityRefusalEvidenceOutcome.SuppressedInFlight or CapabilityRefusalEvidenceOutcome.SuppressedRecorded)
                && during.Count > 0 && p95During < 1000);
        }

        await StopHostAsync(host);
    }
}
