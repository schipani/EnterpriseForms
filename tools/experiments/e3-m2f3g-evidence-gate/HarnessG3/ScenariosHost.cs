// E3-M2f-3G setup (group S), the real host on all seven routes (group H) and several real hosts on one database (group M).
using System.Diagnostics;
using AuthorizationAuthority.Application.Features.Content;

internal sealed partial class Lab
{
    private const string Base = "g3_base";
    private const string PreFloor = "g3_prefloor";
    private const string NoIndex = "g3_noindex";
    private const string ApprovedIndexDefinition = "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries USING btree (organization_id, journal_stream_revision) WHERE (((journal_stream_name)::text = 'audit'::text) AND ((journal_event_kind)::text = 'EnvelopeCapabilityRefused'::text))";

    private string privilegeBaseline = string.Empty;
    private readonly List<string> hostDatabases = [];

    /// <summary>The harness's own catalogue view of IX-B (independent of the command's verification).</summary>
    private async Task<string> IndexStateAsync(string database) =>
        await ScalarAsync(database, """
            SELECT coalesce((SELECT CASE WHEN pg_get_indexdef(c.oid) = '__DEF__' AND i.indisvalid AND i.indisready AND i.indislive AND NOT i.indisunique THEN 'approved-valid'
                                         WHEN NOT i.indisvalid THEN 'invalid' ELSE 'mismatched' END
                             FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid WHERE c.relnamespace = 'authority'::regnamespace AND c.relname = 'ix_journal_capability_refusal_evidence'), 'absent')
            """.Replace("__DEF__", ApprovedIndexDefinition.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal));

    // ------------------------------------------------------------------------------------------------ group S
    private async Task SetupAsync()
    {
        CurrentGroup = "S";
        var version = await ScalarAsync(Maintenance, "SELECT version()");
        Record("S-01", "Setup", "the server is a disposable PostgreSQL 18.0 (one container of this run; admitted by nonce and system identifier)", "PostgreSQL 18.0", Short(version, 60), version.StartsWith("PostgreSQL 18.0", StringComparison.Ordinal));
        var role = await ScalarAsync(Maintenance, $"SELECT rolsuper::text || rolcreaterole::text || rolcreatedb::text || rolbypassrls::text || rolreplication::text || rolinherit::text || rolcanlogin::text FROM pg_roles WHERE rolname = '{RuntimeRole}'");
        Record("S-02", "Setup", "the approved runtime identity is an ordinary login (no superuser, CREATEROLE, CREATEDB, BYPASSRLS, REPLICATION; NOINHERIT)", "falsefalsefalsefalsefalsefalsetrue", role, role == "falsefalsefalsefalsefalsefalsetrue");

        await CreateDatabaseAsync(Base);
        var migrated = await AuthorityCommandAsync(Base, "g3-migrate", "--migrate");
        await CreateDatabaseAsync(PreFloor, Base);
        var floors = new List<int>();
        foreach (var f in new[] { "1", "2", "3" })
        {
            floors.Add((await AuthorityCommandAsync(Base, "g3-floor" + f, "--apply-content-acceptance-floor", f)).ExitCode);
        }

        await CreateDatabaseAsync(NoIndex, Base);
        var before = await IndexStateAsync(Base);
        var provision = await ProvisionIndexAsync(Base, "template");
        var verify = await ProvisionIndexAsync(Base, "template-verify", verifyOnly: true);
        var after = await IndexStateAsync(Base);
        Record("S-03", "Setup", "templates: the ordinary migration (migration identity), floors 1 -> 2 -> 3 (Release B floor 3; Envelope v2 acceptance stays OFF), then IX-B by the accepted E3-M2f-3i command (migration identity) and its --verify-only; the harness's own catalogue query finds the approved definition valid, ready and live. g3_prefloor (migrated only) and g3_noindex (floors, no IX-B) are copies taken before",
            "migrate 0; floors 0,0,0; index absent -> exit 0 -> approved-valid; verify-only exit 0",
            $"migrate {migrated.ExitCode}; floors {string.Join(",", floors)}; index {before} -> {provision} -> {after}; verify-only {verify}",
            migrated.ExitCode == 0 && floors.All(c => c == 0) && before == "absent" && provision.ExitCode == 0 && verify.ExitCode == 0 && after == "approved-valid");
        Record("S-04", "Setup", "the copies keep their states: g3_noindex has no IX-B, g3_prefloor has no floor and no IX-B",
            "g3_noindex absent; g3_prefloor absent and no floor", $"g3_noindex {await IndexStateAsync(NoIndex)}; g3_prefloor {await IndexStateAsync(PreFloor)}; prefloor floors {await FloorsAsync(PreFloor)}",
            await IndexStateAsync(NoIndex) == "absent" && await IndexStateAsync(PreFloor) == "absent" && await FloorsAsync(PreFloor) == "none");
        privilegeBaseline = await PrivilegeSentinelAsync(Base);
        Log("privilege baseline " + privilegeBaseline);
    }

    /// <summary>The applied CARV floors (the floor constraints on content_change_operations), as the E3-M2e-2 harness reads them.</summary>
    private Task<string> FloorsAsync(string database) =>
        ScalarAsync(database, "SELECT coalesce(string_agg(substring(conname FROM 'ck_content_operation_rule_floor_v([0-9]+)$'), ',' ORDER BY conname), 'none') FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname LIKE 'ck\\_content\\_operation\\_rule\\_floor\\_v%'");

    /// <summary>A new host database copied from <paramref name="template"/> and remembered for the final sentinels.</summary>
    private async Task<string> HostDatabaseAsync(string name, string template = Base)
    {
        await CreateDatabaseAsync(name, template);
        hostDatabases.Add(name);
        return name;
    }

    private async Task<Org> HostOrgAsync(string database)
    {
        var org = await SeedOrgAsync(database);
        await SeedCensusSnapshotAsync(org);
        return org;
    }

    // ------------------------------------------------------------------------------------------------ group H
    private async Task HostScenariosAsync()
    {
        CurrentGroup = "H";
        var db = await HostDatabaseAsync("g3_h1");
        var hA = await HostOrgAsync(db);
        var hD = await HostOrgAsync(db);
        var hE = await HostOrgAsync(db);
        var hF = await HostOrgAsync(db);
        var agents = new Dictionary<string, Org>(StringComparer.Ordinal);
        foreach (var build in new[] { "11e15ee", "0be5f52" })
        {
            agents[build] = await HostOrgAsync(db);
        }

        var (host, detail) = await StartHostAsync(db, "h1");
        if (host is null)
        {
            Record("H-00", "Setup", "the real Authority host starts (D = 2, Release B floor 3, acceptance off, Vault Transit signer, in-memory archive)", "live", detail, false);
            return;
        }

        var startup = host.Text.Contains("deployment floor 2", StringComparison.Ordinal);
        Record("H-00", "Setup", "the real Authority host is live on a disposable copy with D = 2 and logs it at startup", "live; startup names deployment floor 2", $"{detail}; startup floor 2 {startup}", startup);

        // H-01 (M2F3-07..14 on PostgreSQL, M2F3-17): seven level-1 refusals of one organization, each records exactly one entry.
        {
            var printBefore = await FingerprintAsync(db, hA.Id);
            var revBefore = await RevisionsAsync(db, hA.Id);
            var t0 = await DatabaseNowAsync(db);
            var (results, summary) = await SevenAsync(host, hA);
            var t1 = await DatabaseNowAsync(db);
            var printAfter = await FingerprintAsync(db, hA.Id);
            var entries = await EvidenceEntryPointsAsync(db, hA.Id);
            var oracle = await OracleAsync(db, hA.Id);
            var times = await LinesAsync(db, $"SELECT (journal_event_body_json::jsonb->'body'->>'evidence_database_time_utc') FROM authority.security_journal_entries WHERE organization_id = '{hA.Id:D}' AND journal_event_kind = 'EnvelopeCapabilityRefused'");
            var inWindow = times.All(t => { var v = DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture); return v >= t0 && v <= t1; });
            Record("H-01", "Security", "M2F3-07..14/17 on the real host and PostgreSQL: the seven routes refuse a level-1 client (no header) with the fixed non-retryable 409; each refusal leaves exactly one evidence entry (seven distinct entry points, workloads by entry point); every evidence database time lies inside the run's own database-clock window around the requests; the refused T1 transactions and the evidence footprint changed nothing else of the organization (fingerprint excludes only the audit head and the evidence entries); the oracle holds",
                "7 x 409 refusal; evidence = the 7 approved entry points once each; times in window; fingerprint unchanged; oracle holds",
                $"{summary}; evidence [{string.Join(",", entries)}]; times in window {inWindow}; fingerprint {FingerprintDiff(printBefore, printAfter)}; revisions {revBefore} -> {await RevisionsAsync(db, hA.Id)}; max refusal {results.Max(r => r.Milliseconds)} ms; {oracle}",
                results.All(r => r.IsCapabilityRefusal) && entries.Count == 7 && entries.Order(StringComparer.Ordinal).SequenceEqual(Seven.Order(StringComparer.Ordinal)) && inWindow && printBefore == printAfter && oracle.Holds() && oracle.Total == 7);
        }

        // H-02: repeats are deduplicated through the durable lookup (and the pre-filter).
        {
            var a = await SevenAsync(host, hA);
            var b = await SevenAsync(host, hA);
            var oracle = await OracleAsync(db, hA.Id);
            Record("H-02", "Safety", "M2F3-05 on the real host: two more passes of all seven level-1 refusals of the same organization in the same hour add nothing",
                "14 x 409; evidence still 7; oracle holds", $"refusals {a.Results.Concat(b.Results).Count(r => r.IsCapabilityRefusal)}/14; {oracle}",
                a.Results.Concat(b.Results).All(r => r.IsCapabilityRefusal) && oracle.Total == 7 && oracle.Holds());
        }

        // H-03: header grammar variants (all level 1) map to the same key.
        {
            var variants = new[] { new[] { "1" }, ["02"], ["2", "2"], ["two"], ["1,2"], [] };
            var outcomes = new List<SendResult>();
            foreach (var v in variants)
            {
                outcomes.Add(await SendAsync(host, CapabilityRefusalEvidence.EntryPoints.Verify, hE, v));
            }

            var entries = await EvidenceEntryPointsAsync(db, hE.Id);
            // A value padded with HTTP optional whitespace arrives trimmed (RFC 9110 field-value OWS), i.e. as "2": not a level-1 variant;
            // recorded as a diagnostic exactly as in E3-M2f-2G GH-06.
            var padded = await SendAsync(host, CapabilityRefusalEvidence.EntryPoints.Verify, hE, [" 2"]);
            Log("DIAGNOSTIC padded header ' 2' (HTTP optional whitespace) -> " + padded);
            Record("H-03", "Safety", "CAP-S3/S6 variants on the wire (1, 02, two instances, two, 1,2, absent) are all level 1, refused, and all one key: one evidence entry (a value padded with HTTP optional whitespace arrives as \"2\" and is logged as a diagnostic, as in E3-M2f-2G)",
                "6 x 409; evidence [verify]", $"{string.Join(", ", outcomes.Select(o => o.ToString()))}; evidence [{string.Join(",", entries)}]; padded ' 2' -> {padded}",
                outcomes.All(o => o.IsCapabilityRefusal) && entries.SequenceEqual([CapabilityRefusalEvidence.EntryPoints.Verify]));
        }

        // H-04: level-2 requests are not refused by the capability check and record nothing.
        {
            var (results, summary) = await SevenAsync(host, hD, ["2"]);
            var count = await EvidenceCountAsync(db, hD.Id);
            Record("H-04", "Safety", "the current build's level-2 header at D = 2 is admitted past the capability check on all seven routes (later stages answer, never the capability refusal) and writes no evidence",
                "no capability refusal; evidence 0", $"{summary}; evidence {count}", results.All(r => !r.IsCapabilityRefusal) && count == 0);
        }

        // H-05 (M2F3-26): the REAL historical business builds, through their own clients.
        foreach (var (build, org) in agents)
        {
            if (!inputs.BusinessAgents.ContainsKey(build))
            {
                Record("H-05-" + build, "Setup", $"the real {build} BusinessAgent is available", "present", "missing", false);
                continue;
            }

            var printBefore = await FingerprintAsync(db, org.Id);
            var outcomes = new List<string>();
            var headers = new List<string>();
            foreach (var entry in Seven)
            {
                var (o, h) = await AgentSendAsync(build, host, entry, org);
                outcomes.Add(entry + "=" + o);
                headers.Add(h);
            }

            var entries = await EvidenceEntryPointsAsync(db, org.Id);
            var oracle = await OracleAsync(db, org.Id);
            Record("H-05-" + build, "Security", $"M2F3-26: the real {build} business build (git archive, unmodified, its own Authority/verifier/builder clients) is refused at all seven routes with the fixed 409; it sent no capability header; exactly one evidence entry per route; the refused transactions committed nothing else; the oracle holds",
                "7 x " + Refused409 + "; headers [] only; evidence 7 distinct; fingerprint unchanged; oracle holds",
                $"{string.Join("; ", outcomes)}; headers {string.Join(",", headers.Distinct())}; evidence [{string.Join(",", entries)}]; fingerprint {FingerprintDiff(printBefore, await FingerprintAsync(db, org.Id))}; {oracle}",
                outcomes.All(o => o.EndsWith("=" + Refused409, StringComparison.Ordinal)) && headers.All(h => h.Split(',').All(x => x == "[]")) && entries.Distinct().Count() == 7 && entries.Count == 7
                && printBefore == await FingerprintAsync(db, org.Id) && oracle.Holds());
        }

        // H-06 (M2F3-31, delivery). The host's AuthorityWorkLoop archives only after SpiceDbSchemaInitializer reports the schema ready;
        // SpiceDB is deliberately unreachable in this disposable run, so the host loop delivers nothing here (recorded as an observation,
        // as E3-M2e-2 and E3-M2f-2G did). Delivery is verified with the UNCHANGED ArchiveProcessor on the host's database (runtime identity),
        // the processor that loop runs.
        {
            var orgs = new List<Org> { hA, hE }.Concat(agents.Values).ToList();
            string NotAnchoredSql() => $"SELECT count(*) FROM authority.security_journal_entries WHERE journal_event_kind = 'EnvelopeCapabilityRefused' AND archive_delivery_state <> 'Anchored' AND organization_id IN ({string.Join(",", orgs.Select(o => $"'{o.Id:D}'"))})";
            var hostLoop = await CountAsync(db, NotAnchoredSql());
            Log($"OBSERVATION host work loop (SpiceDB schema not ready in this run): {hostLoop} evidence entries not anchored after the host scenarios");
            var archive = new AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive();
            var notes = new List<string>();
            foreach (var o in orgs)
            {
                var processor = new AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor(Store(db, "g3h06-archive"), archive, TimeProvider.System);
                for (var pass = 0; pass < 20; pass++)
                {
                    if (await processor.ProcessOrganizationAsync(o.Id, CancellationToken.None) == 0)
                    {
                        break;
                    }
                }
            }

            var pending = await CountAsync(db, NotAnchoredSql());
            var total = await CountAsync(db, "SELECT count(*) FROM authority.security_journal_entries WHERE journal_event_kind = 'EnvelopeCapabilityRefused'");
            var quarantined = await CountAsync(db, "SELECT count(*) FROM authority.organization_authorization_states WHERE integrity_quarantine_enabled");
            Record("H-06", "Liveness", "M2F3-31 (delivery): the unchanged archive processor (the one the host's work loop runs; that loop waits for SpiceDB, unreachable in this run, and delivered nothing) anchors every evidence entry the host recorded, body digests verified by the in-memory archive; no organization quarantined",
                "0 entries not anchored; quarantined 0", $"host loop left {hostLoop} unanchored (observation); after the processor not anchored {pending} of {total}; quarantined {quarantined}",
                pending == 0 && total > 0 && quarantined == 0);
        }

        // H-07: a restarted host (empty pre-filter) re-checks durably and adds nothing.
        await StopHostAsync(host);
        var (host2, detail2) = await StartHostAsync(db, "h1-restart");
        if (host2 is null)
        {
            Record("H-07", "Safety", "restart", "live", detail2, false);
            return;
        }

        {
            var (results, summary) = await SevenAsync(host2, hA);
            var oracle = await OracleAsync(db, hA.Id);
            Record("H-07", "Safety", "restart: a fresh host process (empty pre-filter and retry set) refuses all seven again; the durable lookup finds every key; nothing is appended",
                "7 x 409; evidence still 7; oracle holds", $"{summary}; {oracle}", results.All(r => r.IsCapabilityRefusal) && oracle.Total == 7 && oracle.Holds());
        }

        // H-08 (M2F3-34): T2 cannot commit (a statement of T2 waits on a harness lock and hits its statement timeout), the host is
        // killed before its retry: no evidence; after a restart the next refusal records it.
        {
            await using var holder = await OpenAdminAsync(db, "g3-lock-holder");
            await using var tx = await holder.BeginTransactionAsync();
            await using (var lockCmd = new Npgsql.NpgsqlCommand("LOCK TABLE authority.security_journal_entries IN ACCESS EXCLUSIVE MODE", holder, tx))
            {
                await lockCmd.ExecuteNonQueryAsync();
            }

            var refused = await SendAsync(host2, CapabilityRefusalEvidence.EntryPoints.Commit, hF, []);
            await StopHostAsync(host2, crash: true); // before the first retry (initial backoff 5 s)
            var deferredLogged = host2.Text.Contains("[9253]", StringComparison.Ordinal) || host2.Text.Contains("9253", StringComparison.Ordinal);
            await tx.RollbackAsync();
            var afterCrash = await EvidenceCountAsync(db, hF.Id);
            var (host3, detail3) = await StartHostAsync(db, "h1-after-crash");
            if (host3 is null)
            {
                Record("H-08", "Failure", "crash", "live", detail3, false);
                return;
            }

            var again = await SendAsync(host3, CapabilityRefusalEvidence.EntryPoints.Commit, hF, []);
            var entries = await EvidenceEntryPointsAsync(db, hF.Id);
            var oracle = await OracleAsync(db, hF.Id);
            Record("H-08", "Failure", "M2F3-34 on the real host: with the journal table locked by another session, T2 cannot complete (its statement hits the 500 ms statement timeout) while the client still gets its 409 at once; a retry is scheduled (log 9253); the host is killed before the retry: no evidence, nothing half-written; after a restart the next refusal records exactly one entry",
                "409; 9253 logged; after crash 0; after restart 409 and evidence [commit]; oracle holds",
                $"{refused} in {refused.Milliseconds} ms; 9253 logged {deferredLogged}; after crash {afterCrash}; after restart {again}; evidence [{string.Join(",", entries)}]; {oracle}",
                refused.IsCapabilityRefusal && refused.Milliseconds < 3000 && deferredLogged && afterCrash == 0 && again.IsCapabilityRefusal && entries.SequenceEqual([CapabilityRefusalEvidence.EntryPoints.Commit]) && oracle.Holds());
            await StopHostAsync(host3);
        }

        // H-09 (M2F3-83): rolling deployment. A 3b build against a database without IX-B serves its refusals normally and records nothing;
        // once the E3-M2f-3i command has created IX-B, the evidence is recorded (by the scheduled retries or the next refusals).
        {
            var db2 = await HostDatabaseAsync("g3_h2", NoIndex);
            var org = await HostOrgAsync(db2);
            var (hostR, detailR) = await StartHostAsync(db2, "h2-rolling");
            if (hostR is null)
            {
                Record("H-09", "Liveness", "rolling deployment", "live", detailR, false);
                return;
            }

            var (first, summary1) = await SevenAsync(hostR, org);
            var without = await EvidenceCountAsync(db2, org.Id);
            var oracle1 = await OracleAsync(db2, org.Id);
            var provision = await ProvisionIndexAsync(db2, "rolling");
            var state = await IndexStateAsync(db2);
            long count = 0;
            for (var i = 0; i < 60 && count < 7; i++)
            {
                await Task.Delay(1000);
                count = await EvidenceCountAsync(db2, org.Id);
            }

            var (second, summary2) = await SevenAsync(hostR, org);
            var oracle2 = await OracleAsync(db2, org.Id);
            var logged = hostR.Text.Contains("9253", StringComparison.Ordinal);
            Record("H-09", "Liveness", "M2F3-83 rolling deployment on the real host: without IX-B the seven refusals are served normally (409) and nothing is recorded (INDEX_UNAVAILABLE, retry scheduled, log 9253); the E3-M2f-3i command (migration identity) creates IX-B while the host runs; then exactly the seven keys are recorded once; further refusals add nothing; the oracle holds throughout",
                "7 x 409; evidence 0; 9253 logged; command exit 0, approved-valid; evidence 7 within 60 s; 7 x 409; evidence 7; oracle holds",
                $"{summary1}; evidence {without}; 9253 {logged}; command {provision}, {state}; evidence {count}; {summary2}; {oracle2}",
                first.All(r => r.IsCapabilityRefusal) && without == 0 && oracle1.Holds() && logged && provision.ExitCode == 0 && state == "approved-valid" && count == 7 && second.All(r => r.IsCapabilityRefusal) && oracle2.Total == 7 && oracle2.Holds());
            await StopHostAsync(hostR);
        }
    }

    // ------------------------------------------------------------------------------------------------ group M (M2F3-27)
    private async Task MultiHostScenariosAsync()
    {
        CurrentGroup = "M";
        var db = await HostDatabaseAsync("g3_m1");
        var two = await HostOrgAsync(db);
        var three = await HostOrgAsync(db);
        var fleet = new List<Org>();
        for (var i = 0; i < 10; i++)
        {
            fleet.Add(await HostOrgAsync(db));
        }

        var hosts = new List<HostHandle>();
        for (var i = 0; i < 3; i++)
        {
            var (h, d) = await StartHostAsync(db, "m" + i);
            if (h is null)
            {
                Record("M-00", "Setup", "three real hosts on one database", "live x3", d, false);
                return;
            }

            hosts.Add(h);
        }

        async Task<(List<SendResult> Results, BoundResult Oracle)> BurstAsync(Org org, int hostCount, int repeats)
        {
            var tasks = new List<Task<SendResult>>();
            for (var r = 0; r < repeats; r++)
            {
                for (var h = 0; h < hostCount; h++)
                {
                    foreach (var entry in Seven)
                    {
                        var host = hosts[h];
                        tasks.Add(Task.Run(() => SendAsync(host, entry, org, [])));
                    }
                }
            }

            var results = (await Task.WhenAll(tasks)).ToList();
            return (results, await OracleAsync(db, org.Id));
        }

        var a = await BurstAsync(two, 2, 4);
        Record("M-01", "Safety", "M2F3-27 with 2 real hosts: 56 concurrent level-1 refusals of all seven keys of one organization (4 per key per host): exactly one entry per key", "56 x 409; evidence 7; ≤ 1 per key per bucket; oracle holds",
            $"409 {a.Results.Count(r => r.IsCapabilityRefusal)}/{a.Results.Count}; max {a.Results.Max(r => r.Milliseconds)} ms; {a.Oracle}", a.Results.All(r => r.IsCapabilityRefusal) && a.Oracle.Total == 7 && a.Oracle.Holds());
        var b = await BurstAsync(three, 3, 4);
        Record("M-02", "Safety", "M2F3-27 with 3 real hosts: 84 concurrent level-1 refusals of all seven keys of one organization: exactly one entry per key", "84 x 409; evidence 7; oracle holds",
            $"409 {b.Results.Count(r => r.IsCapabilityRefusal)}/{b.Results.Count}; max {b.Results.Max(r => r.Milliseconds)} ms; {b.Oracle}", b.Results.All(r => r.IsCapabilityRefusal) && b.Oracle.Total == 7 && b.Oracle.Holds());

        // M-03: ten organizations at once on three hosts (cross-organization independence under the real row locks).
        {
            var bursts = await Task.WhenAll(fleet.Select(o => BurstAsync(o, 3, 2)));
            var (holds, fleetDetail) = await FleetOracleAsync(db, fleet);
            Record("M-03", "Safety", "ten organizations concurrently on three real hosts (420 refusals): every organization records exactly its seven keys; the fleet bound holds",
                "420 x 409; 70 entries; every organization 7; fleet oracle holds",
                $"409 {bursts.Sum(x => x.Results.Count(r => r.IsCapabilityRefusal))}/{bursts.Sum(x => x.Results.Count)}; per-organization totals {string.Join(",", bursts.Select(x => x.Oracle.Total))}; {fleetDetail}",
                bursts.All(x => x.Results.All(r => r.IsCapabilityRefusal) && x.Oracle.Total == 7) && holds);
        }

        foreach (var h in hosts)
        {
            await StopHostAsync(h);
        }
    }

    // ------------------------------------------------------------------------------------------------ final sentinels
    private async Task FinalSentinelsAsync()
    {
        CurrentGroup = "Z";
        var privileges = await PrivilegeSentinelAsync(Base);
        Record("Z-01", "Control", "the runtime identity's privileges, memberships and role attributes are unchanged by the whole run (template database)", privilegeBaseline, privileges, privileges == privilegeBaseline && privilegeBaseline.Length > 0);
        var accepted = new List<string>();
        foreach (var db in Target.Databases.Where(d => d != Maintenance).Order(StringComparer.Ordinal))
        {
            var n = await CountAsync(db, $"SELECT count(*) FROM authority.platform_journal_entries WHERE journal_event_kind = '{EnvelopeAcceptancePolicyObserver.EventKind}' AND journal_event_body_json LIKE '%\"acceptance_requested\":true%'");
            if (n > 0)
            {
                accepted.Add(db + "=" + n);
            }
        }

        Record("Z-02", "Control", "Envelope v2 acceptance was never requested: no database of the run holds an acceptance policy record with acceptance requested (hosts ran with Content:EnvelopeV2AcceptanceEnabled = false)",
            "none", accepted.Count == 0 ? "none" : string.Join(",", accepted), accepted.Count == 0);
        var bindings = new List<string>();
        foreach (var db in Target.Databases.Where(d => d != Maintenance).Order(StringComparer.Ordinal))
        {
            var n = await CountAsync(db, "SELECT count(*) FROM authority.content_data_key_bindings");
            if (n > 0)
            {
                bindings.Add(db + "=" + n);
            }
        }

        Record("Z-03", "Control", "no data-key binding exists in any database of the run (every organization stayed unbound: no bound-organization scenario was executed)", "none", bindings.Count == 0 ? "none" : string.Join(",", bindings), bindings.Count == 0);
    }
}
