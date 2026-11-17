// E3-M2f-3i capability-refusal evidence index: disposable-PostgreSQL gate (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5A;
// record docs/security/E3_M2F3I_IMPLEMENTATION_RECORD.md).
//
// The command under test always runs as a SEPARATE PROCESS of this working tree's Authority Release build
// (dotnet AuthorizationAuthority.Api.dll --provision-capability-refusal-evidence-index), exactly as an operator would run it, with
// the identity given in ConnectionStrings__AuthorityDbMigration. The harness only creates disposable databases inside ONE disposable
// container, seeds synthetic rows with the container superuser (test setup), holds competing sessions, and checks the results with
// its OWN catalogue queries (independent of the command's verification code). Nothing here touches any other database.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

var lab = new Lab(
    Env("M3I_AUTHORITY_DLL"), Env("M3I_DOTNET"), Env("M3I_ADMIN"), Env("M3I_RUNTIME_USER"), Env("M3I_RUNTIME_PASSWORD"),
    Env("M3I_OUTSIDER_USER"), Env("M3I_OUTSIDER_PASSWORD"), Env("M3I_CONTAINER"), Env("M3I_EVIDENCE"), int.Parse(Env("M3I_BIG_ROWS"), System.Globalization.CultureInfo.InvariantCulture));
return await lab.RunAsync();

static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);

internal sealed record CommandResult(int ExitCode, JsonNode? Report, string Stdout, string Stderr, long Milliseconds)
{
    public string? Outcome => Report?["outcome"]?.GetValue<string>();

    public string? StateAfter => Report?["state_after"]?.GetValue<string>();

    public string? SqlState => Report?["sql_state"]?.GetValue<string>();

    public long? IndexOid => Report?["index_oid"]?.GetValue<long?>();

    public override string ToString() => $"exit={ExitCode} outcome={Outcome} state_before={Report?["state_before"]} state_after={StateAfter} sqlstate={SqlState} ms={Milliseconds}";
}

internal sealed class Lab(string authorityDll, string dotnet, string adminCs, string runtimeUser, string runtimePassword, string outsiderUser, string outsiderPassword, string container, string evidenceDir, int bigRows)
{
    private const string IndexName = "ix_journal_capability_refusal_evidence";
    private const string App = "authority-capability-evidence-index";

    // The harness's own copy of the approved texts (closure §5A.3/§5A.4); the command's constants are checked against these by unit tests.
    private const string ApprovedDefinition = "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries USING btree (organization_id, journal_stream_revision) WHERE (((journal_stream_name)::text = 'audit'::text) AND ((journal_event_kind)::text = 'EnvelopeCapabilityRefused'::text))";
    private const string Lookup = "SELECT security_journal_entry_id, journal_stream_revision, journal_event_body_json, journal_event_digest FROM authority.security_journal_entries WHERE organization_id = @organization_id AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' ORDER BY journal_stream_revision DESC LIMIT 8";
    private const string LookupPositional = "SELECT security_journal_entry_id, journal_stream_revision, journal_event_body_json, journal_event_digest FROM authority.security_journal_entries WHERE organization_id = $1 AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' ORDER BY journal_stream_revision DESC LIMIT 8";

    private readonly List<(string Id, string Title, bool Passed, string Detail)> checks = [];
    private readonly List<(string Id, string Title, string Detail)> demos = [];
    private readonly List<string> commandLog = [];
    private readonly List<Guid> orgs = [];

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Path.Combine(evidenceDir, "plans"));
        var started = Stopwatch.StartNew();
        try
        {
            await PrepareTemplateAsync();
            await FreshAsync();
            await VerifyOnlyAsync();
            await InvalidAsync();
            await MismatchAsync();
            await ConcurrentAsync();
            await InProgressAsync();
            await InterruptedAsync();
            await PrivilegesAsync();
            await LockTimeoutTableAsync();
            await LockTimeoutWaitAsync();
            await NoHistoryOrOrganizationLockAsync();
            await BigAsync();
        }
        catch (Exception ex)
        {
            Record("I-99", "harness completed without an unexpected exception", false, ex.ToString());
        }

        var failed = checks.Count(c => !c.Passed);
        var report = new JsonObject
        {
            ["gate"] = "E3-M2f-3i capability-refusal evidence index (disposable PostgreSQL)",
            ["checksTotal"] = checks.Count,
            ["checksFailed"] = failed,
            ["elapsedSeconds"] = (long)started.Elapsed.TotalSeconds,
            ["checks"] = new JsonArray([.. checks.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["title"] = c.Title, ["passed"] = c.Passed, ["detail"] = c.Detail })]),
            ["demonstrationsOutsidePassTotals"] = new JsonArray([.. demos.Select(d => (JsonNode)new JsonObject { ["id"] = d.Id, ["title"] = d.Title, ["detail"] = d.Detail })]),
        };
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "m2f3i-evidence.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        await File.WriteAllLinesAsync(Path.Combine(evidenceDir, "command-runs.txt"), commandLog);
        foreach (var c in checks)
        {
            Console.WriteLine($"{(c.Passed ? "PASS" : "FAIL")} {c.Id} {c.Title} :: {c.Detail}");
        }

        foreach (var d in demos)
        {
            Console.WriteLine($"DEMO {d.Id} {d.Title} :: {d.Detail}");
        }

        Console.WriteLine($"checks {checks.Count}, failed {failed}");
        return failed == 0 ? 0 : 1;
    }

    private void Record(string id, string title, bool passed, string detail) => checks.Add((id, title, passed, detail));

    // ---- connections and commands ------------------------------------------------------------------------------------------

    private string Cs(string database, string? user = null, string? password = null)
    {
        var b = new NpgsqlConnectionStringBuilder(adminCs) { Database = database, Pooling = false };
        if (user is not null)
        {
            b.Username = user;
            b.Password = password;
        }

        return b.ConnectionString;
    }

    private async Task<NpgsqlConnection> OpenAsync(string database, string? user = null, string? password = null, string? application = null)
    {
        var b = new NpgsqlConnectionStringBuilder(Cs(database, user, password));
        if (application is not null)
        {
            b.ApplicationName = application;
        }

        var c = new NpgsqlConnection(b.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    private static async Task ExecAsync(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecAsync(string database, string sql)
    {
        await using var c = await OpenAsync(database);
        await ExecAsync(c, sql);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var c = await OpenAsync(database);
        return await ScalarAsync(c, sql);
    }

    private static async Task<List<string>> LinesAsync(NpgsqlConnection c, string sql)
    {
        var lines = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            lines.Add(r.IsDBNull(0) ? "<null>" : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return lines;
    }

    private async Task<List<string>> LinesAsync(string database, string sql)
    {
        await using var c = await OpenAsync(database);
        return await LinesAsync(c, sql);
    }

    private async Task<int> RunProcessAsync(string[] args, Dictionary<string, string> environment, StringBuilder output)
    {
        var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("M3I_", StringComparison.Ordinal)).ToList())
        {
            psi.Environment.Remove(key);
        }

        foreach (var (k, v) in environment)
        {
            psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        output.Append(await so).Append("\n--stderr--\n").Append(await se);
        return p.ExitCode;
    }

    private async Task MigrateAsync(string database)
    {
        var output = new StringBuilder();
        var code = await RunProcessAsync([authorityDll, "--migrate"], new() { ["ConnectionStrings__AuthorityDbMigration"] = Cs(database), ["Database__RuntimeRoleName"] = runtimeUser }, output);
        if (code != 0)
        {
            throw new InvalidOperationException("--migrate failed: " + output);
        }
    }

    private Task<CommandResult> CommandAsync(string database, string label, bool verifyOnly = false, string? user = null, string? password = null) =>
        Task.Run(async () =>
        {
            var args = verifyOnly
                ? new[] { authorityDll, "--provision-capability-refusal-evidence-index", "--verify-only" }
                : new[] { authorityDll, "--provision-capability-refusal-evidence-index" };
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("M3I_", StringComparison.Ordinal)).ToList())
            {
                psi.Environment.Remove(key);
            }

            psi.Environment["ConnectionStrings__AuthorityDbMigration"] = Cs(database, user, password);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var stdout = await so;
            var stderr = await se;
            JsonNode? report = null;
            foreach (var line in (stdout + "\n" + stderr).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')))
            {
                try
                {
                    report = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // not the report line
                }
            }

            var result = new CommandResult(p.ExitCode, report, stdout, stderr, sw.ElapsedMilliseconds);
            lock (commandLog)
            {
                commandLog.Add($"[{label}] db={database} identity={(user ?? "migration")} {result}");
                commandLog.Add("    report: " + (report?.ToJsonString() ?? "<none>"));
            }

            return result;
        });

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await ExecAsync(new NpgsqlConnectionStringBuilder(adminCs).Database!, $"CREATE DATABASE {name} TEMPLATE m2f3i_template");
        return name;
    }

    // ---- independent catalogue, privilege and data views ------------------------------------------------------------------

    /// <summary>The harness's own view of the relation of that name (not the command's query).</summary>
    private static async Task<Dictionary<string, string>?> IndexFactsAsync(NpgsqlConnection c)
    {
        const string sql = """
            SELECT c.oid::text AS oid, c.relkind::text AS kind, pg_get_indexdef(c.oid) AS def, i.indisvalid::text AS valid, i.indisready::text AS ready,
                   i.indislive::text AS live, i.indisunique::text AS uniq, am.amname::text AS am, i.indrelid::regclass::text AS tbl,
                   pg_get_expr(i.indpred, i.indrelid) AS pred, i.indkey::text AS indkey, i.indoption::text AS opt,
                   (SELECT string_agg(a.attname, ',' ORDER BY k.n) FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum, n) JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum) AS cols,
                   (c.relowner = t.relowner)::text AS owner_ok, (c.relacl IS NULL)::text AS no_acl, pg_get_userbyid(c.relowner) AS owner
            FROM pg_class c LEFT JOIN pg_index i ON i.indexrelid = c.oid LEFT JOIN pg_class t ON t.oid = i.indrelid LEFT JOIN pg_am am ON am.oid = c.relam
            WHERE c.relnamespace = 'authority'::regnamespace AND c.relname = 'ix_journal_capability_refusal_evidence'
            """;
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            return null;
        }

        var d = new Dictionary<string, string>();
        for (var i = 0; i < r.FieldCount; i++)
        {
            d[r.GetName(i)] = r.IsDBNull(i) ? "<null>" : r.GetValue(i).ToString()!;
        }

        return d;
    }

    private async Task<Dictionary<string, string>?> IndexFactsAsync(string database)
    {
        await using var c = await OpenAsync(database);
        return await IndexFactsAsync(c);
    }

    private static string Describe(Dictionary<string, string>? f) => f is null ? "absent" : string.Join("; ", f.Select(kv => kv.Key + "=" + kv.Value));

    private static bool IsApprovedValid(Dictionary<string, string>? f) =>
        f is not null && f["kind"] == "i" && f["def"] == ApprovedDefinition && f["valid"] == "true" && f["ready"] == "true" && f["live"] == "true"
        && f["uniq"] == "false" && f["am"] == "btree" && f["tbl"] == "authority.security_journal_entries" && f["cols"] == "organization_id,journal_stream_revision"
        && f["opt"] == "0 0" && f["owner_ok"] == "true" && f["no_acl"] == "true";

    /// <summary>Structure, privileges, roles and migration history (the harness's own listing).</summary>
    private async Task<HashSet<string>> CatalogueAsync(string database)
    {
        var sql = $"""
            SELECT 'rel ' || n.nspname || '.' || c.relname || ' ' || c.relkind::text || ' ' || pg_get_userbyid(c.relowner) || ' ' || coalesce(array_to_string(c.relacl, ','), '-')
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname LIKE 'authority%' OR n.nspname = 'public'
            UNION ALL SELECT 'ix ' || schemaname || '.' || tablename || ' ' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname LIKE 'authority%'
            UNION ALL SELECT 'nsp ' || nspname || ' ' || pg_get_userbyid(nspowner) || ' ' || coalesce(array_to_string(nspacl, ','), '-') FROM pg_namespace WHERE nspname LIKE 'authority%' OR nspname = 'public'
            UNION ALL SELECT 'role ' || rolname || ' ' || rolsuper::text || ' ' || rolcreaterole::text || ' ' || rolcreatedb::text || ' ' || rolcanlogin::text || ' ' || rolbypassrls::text || ' ' || rolinherit::text || ' ' || coalesce(array_to_string(rolconfig, ','), '-') FROM pg_roles WHERE rolname NOT LIKE 'pg\_%'
            UNION ALL SELECT 'member ' || roleid::regrole || ' ' || member::regrole || ' ' || admin_option::text FROM pg_auth_members
            UNION ALL SELECT 'con ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid) || ' ' || convalidated::text FROM pg_constraint WHERE connamespace::regnamespace::text LIKE 'authority%'
            UNION ALL SELECT 'trg ' || tgrelid::regclass || ' ' || tgname || ' ' || tgenabled::text FROM pg_trigger WHERE NOT tgisinternal
            UNION ALL SELECT 'fn ' || p.oid::regprocedure || ' ' || pg_get_userbyid(p.proowner) || ' ' || coalesce(array_to_string(p.proacl, ','), '-') FROM pg_proc p WHERE p.pronamespace::regnamespace::text LIKE 'authority%'
            UNION ALL SELECT 'dacl ' || defaclrole::regrole || ' ' || defaclobjtype::text || ' ' || coalesce(array_to_string(defaclacl, ','), '-') FROM pg_default_acl
            UNION ALL SELECT 'db ' || datname || ' ' || coalesce(array_to_string(datacl, ','), '-') FROM pg_database WHERE datname = current_database()
            UNION ALL SELECT 'migration ' || "MigrationId" || ' ' || "ProductVersion" FROM authority.ef_migrations_history
            UNION ALL SELECT 'priv ' || r.rolname || ' ' || c.oid::regclass || ' ' || p.priv || ' ' || has_table_privilege(r.rolname, c.oid, p.priv)::text
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) p(priv)
              CROSS JOIN (SELECT rolname FROM pg_roles WHERE rolname IN ('{runtimeUser}', '{outsiderUser}')) r
              WHERE n.nspname LIKE 'authority%' AND c.relkind IN ('r', 'p', 'v', 'm')
            UNION ALL SELECT 'nspriv ' || r.rolname || ' ' || n.nspname || ' ' || p.priv || ' ' || has_schema_privilege(r.rolname, n.oid, p.priv)::text
              FROM pg_namespace n CROSS JOIN (VALUES ('USAGE'), ('CREATE')) p(priv)
              CROSS JOIN (SELECT rolname FROM pg_roles WHERE rolname IN ('{runtimeUser}', '{outsiderUser}')) r WHERE n.nspname LIKE 'authority%' OR n.nspname = 'public'
            """;
        return [.. await LinesAsync(database, sql)];
    }

    /// <summary>Row count and an order-independent content hash of every table in the Authority schemas (superuser: RLS does not apply).</summary>
    private async Task<SortedDictionary<string, string>> DataAsync(string database)
    {
        await using var c = await OpenAsync(database);
        var tables = await LinesAsync(c, "SELECT quote_ident(n.nspname) || '.' || quote_ident(c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname LIKE 'authority%' AND c.relkind IN ('r', 'p') ORDER BY 1");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            result[t] = Convert.ToString(await ScalarAsync(c, $"SELECT count(*)::text || ':' || coalesce(sum(hashtextextended(x::text, 0))::text, '0') FROM {t} x"), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        return result;
    }

    private static string Diff(HashSet<string> before, HashSet<string> after) =>
        "added: [" + string.Join(" | ", after.Except(before).Order(StringComparer.Ordinal)) + "] removed: [" + string.Join(" | ", before.Except(after).Order(StringComparer.Ordinal)) + "]";

    private static bool OnlyIndexAdded(HashSet<string> before, HashSet<string> after) =>
        !before.Except(after).Any() && after.Except(before).All(l => l.Contains(IndexName, StringComparison.Ordinal)) && after.Except(before).Count() == 2;

    // ---- competing sessions ------------------------------------------------------------------------------------------------

    /// <summary>A session that holds a snapshot (REPEATABLE READ) in the database: a concurrent build waits for it.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldSnapshotAsync(string database)
    {
        var c = await OpenAsync(database, application: "gate-snapshot-holder");
        var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await ExecAsync(c, "SELECT 1", tx);
        return (c, tx);
    }

    private static async Task ReleaseAsync((NpgsqlConnection Connection, NpgsqlTransaction Transaction) holder)
    {
        await holder.Transaction.RollbackAsync();
        await holder.Connection.DisposeAsync();
    }

    /// <summary>The backend pid of an active CREATE INDEX CONCURRENTLY in the database (optionally of one application).</summary>
    private async Task<int?> WaitForBuildAsync(string database, string? application, TimeSpan timeout)
    {
        await using var c = await OpenAsync(new NpgsqlConnectionStringBuilder(adminCs).Database!);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            await using var cmd = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE datname = @db AND state = 'active' AND query ILIKE 'CREATE INDEX CONCURRENTLY%' AND (@app::text IS NULL OR application_name = @app::text) AND pid <> pg_backend_pid() LIMIT 1", c);
            cmd.Parameters.AddWithValue("db", database);
            cmd.Parameters.Add(new NpgsqlParameter("app", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)application ?? DBNull.Value });
            if (await cmd.ExecuteScalarAsync() is int pid)
            {
                return pid;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private async Task CancelBackendAsync(int pid) => await ScalarAsync(new NpgsqlConnectionStringBuilder(adminCs).Database!, $"SELECT pg_cancel_backend({pid})");

    // ---- setup ------------------------------------------------------------------------------------------------------------

    private async Task PrepareTemplateAsync()
    {
        var maintenance = new NpgsqlConnectionStringBuilder(adminCs).Database!;
        await ExecAsync(maintenance, "CREATE DATABASE m2f3i_template");
        await MigrateAsync("m2f3i_template");
        await using var c = await OpenAsync("m2f3i_template");
        var columns = new List<(string Name, string Type, bool Nullable, bool HasDefault)>();
        await using (var cmd = new NpgsqlCommand("SELECT column_name, data_type, is_nullable = 'YES', column_default IS NOT NULL FROM information_schema.columns WHERE table_schema = 'authority' AND table_name = 'organization_authorization_states' ORDER BY ordinal_position", c))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                columns.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3)));
            }
        }

        // Ten synthetic organizations (test setup with the superuser; every CHECK applies). Values: zero revisions, no quarantine.
        for (var i = 0; i < 10; i++)
        {
            var org = Guid.NewGuid();
            orgs.Add(org);
            var names = new List<string>();
            var values = new List<string>();
            foreach (var (name, type, nullable, hasDefault) in columns)
            {
                if (name == "organization_id")
                {
                    names.Add(name);
                    values.Add($"'{org}'");
                    continue;
                }

                if (nullable || hasDefault)
                {
                    continue;
                }

                names.Add(name);
                values.Add(name switch
                {
                    "checkpoint_enforcement_mode_name" => "'Asynchronous'",
                    _ => type switch
                    {
                        "uuid" => "gen_random_uuid()",
                        "bigint" or "integer" or "smallint" or "numeric" => "0",
                        "boolean" => "false",
                        "bytea" => "'\\x'::bytea",
                        "timestamp with time zone" => "now()",
                        "jsonb" => "'{}'::jsonb",
                        _ => $"'gate-{i}'",
                    },
                });
            }

            await ExecAsync(c, $"INSERT INTO authority.organization_authorization_states ({string.Join(", ", names)}) VALUES ({string.Join(", ", values)})");
        }

        // A small unrelated audit and authorization history per organization (no capability evidence).
        foreach (var org in orgs)
        {
            await ExecAsync(c, Journal(org, "audit", 1, 300, "'FieldSearchExecuted'"));
            await ExecAsync(c, Journal(org, "authorization", 1, 50, "'SecurityCommandCommitted'"));
        }
    }

    private static string Journal(Guid org, string stream, long from, long to, string kindExpression) => $"""
        INSERT INTO authority.security_journal_entries (security_journal_entry_id, organization_id, journal_stream_name, journal_stream_revision, journal_event_kind,
            journal_event_body_json, journal_event_digest, archive_delivery_state, archive_attempt_count, journal_created_on)
        SELECT gen_random_uuid(), '{org}', '{stream}', g, {kindExpression}, '{"{"}"synthetic":"m2f3i"{"}"}', sha256(convert_to('{"{"}"synthetic":"m2f3i"{"}"}', 'UTF8')), 'Anchored', 1,
               timestamptz '2026-01-01 00:00:00+00' + g * interval '1 second'
        FROM generate_series({from}::bigint, {to}::bigint) g
        """;

    // ---- scenarios --------------------------------------------------------------------------------------------------------

    private async Task FreshAsync()
    {
        var db = await CreateDatabaseAsync("gate_fresh");
        var catalogueBefore = await CatalogueAsync(db);
        var dataBefore = await DataAsync(db);
        var first = await CommandAsync(db, "fresh-1");
        var facts = await IndexFactsAsync(db);
        Record("I-01", "fresh creation: exit 0, outcome Created, before Absent", first.ExitCode == 0 && first.Outcome == "Created" && (string?)first.Report?["state_before"] == "Absent", first.ToString());
        Record("I-02", "independent catalogue verification: schema, table, name, btree, columns and order, ascending, predicate, valid/ready/live, non-unique, owner = table owner, no ACL, exact pg_get_indexdef", IsApprovedValid(facts), Describe(facts));

        await using (var c = await OpenAsync(db))
        {
            // Predicate semantics, behaviourally: the planner can prove the exact stream/kind predicate implies the index, and nothing broader.
            await ExecAsync(c, "SET enable_seqscan = off");
            var exact = string.Join("\n", await LinesAsync(c, "EXPLAIN SELECT 1 FROM authority.security_journal_entries WHERE organization_id = '00000000-0000-0000-0000-000000000001' AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'"));
            var otherKind = string.Join("\n", await LinesAsync(c, "EXPLAIN SELECT 1 FROM authority.security_journal_entries WHERE organization_id = '00000000-0000-0000-0000-000000000001' AND journal_stream_name = 'audit' AND journal_event_kind = 'FieldSearchExecuted'"));
            var otherStream = string.Join("\n", await LinesAsync(c, "EXPLAIN SELECT 1 FROM authority.security_journal_entries WHERE organization_id = '00000000-0000-0000-0000-000000000001' AND journal_stream_name = 'authorization' AND journal_event_kind = 'EnvelopeCapabilityRefused'"));
            Record("I-03", "predicate semantics: usable for stream 'audit' + kind 'EnvelopeCapabilityRefused' only (not another kind, not another stream)",
                exact.Contains(IndexName, StringComparison.Ordinal) && !otherKind.Contains(IndexName, StringComparison.Ordinal) && !otherStream.Contains(IndexName, StringComparison.Ordinal),
                $"exact uses index: {exact.Contains(IndexName, StringComparison.Ordinal)}; other kind: {otherKind.Contains(IndexName, StringComparison.Ordinal)}; other stream: {otherStream.Contains(IndexName, StringComparison.Ordinal)}");
        }

        var catalogueAfter = await CatalogueAsync(db);
        var dataAfter = await DataAsync(db);
        Record("I-04", "catalogue (structure, privileges, roles, memberships, migration history) differs by exactly the new index", OnlyIndexAdded(catalogueBefore, catalogueAfter), Diff(catalogueBefore, catalogueAfter));
        Record("I-05", "no data change in any Authority table (organization, authorization, content, ledger, quarantine, journal, search)", dataBefore.SequenceEqual(dataAfter), $"{dataAfter.Count} tables compared");
        Record("I-06", "the command's own report agrees: catalogue added exactly the index relation and definition, nothing removed",
            first.Report?["catalogue_added"]?.AsArray().Count == 2 && first.Report?["catalogue_removed"]?.AsArray().Count == 0, first.Report?["catalogue_added"]?.ToJsonString() ?? "<none>");

        var second = await CommandAsync(db, "fresh-2");
        var factsAgain = await IndexFactsAsync(db);
        var catalogueAgain = await CatalogueAsync(db);
        Record("I-07", "repeated invocation is an idempotent success: exit 0 AlreadyValid, same index oid, no catalogue change",
            second.ExitCode == 0 && second.Outcome == "AlreadyValid" && factsAgain?["oid"] == facts?["oid"] && catalogueAgain.SetEquals(catalogueAfter), second + " " + Diff(catalogueAfter, catalogueAgain));
        var verify = await CommandAsync(db, "fresh-verify", verifyOnly: true);
        Record("I-08", "--verify-only on the valid index: exit 0 AlreadyValid, nothing changed", verify.ExitCode == 0 && verify.Outcome == "AlreadyValid" && (await CatalogueAsync(db)).SetEquals(catalogueAfter), verify.ToString());

        // Statement trace of the first invocation: the server logs every statement (log_statement = all, prefix with database and application).
        var log = await DockerLogsAsync();
        var statements = log.Split('\n').Where(l => l.Contains($" {db} {App} ", StringComparison.Ordinal) && (l.Contains("statement: ", StringComparison.Ordinal) || l.Contains("execute ", StringComparison.Ordinal)))
            .Select(l => l[(l.IndexOf(": ", l.IndexOf("LOG:", StringComparison.Ordinal) + 4, StringComparison.Ordinal) + 2)..].Trim()).ToList();
        await File.WriteAllLinesAsync(Path.Combine(evidenceDir, "statement-trace-fresh.txt"), statements);
        // Whole words only (the inspection query reads pg_locks.granted, which is not a GRANT statement).
        var forbidden = new System.Text.RegularExpressions.Regex(@"\b(BEGIN|COMMIT|ROLLBACK|START\s+TRANSACTION|SAVEPOINT|ef_migrations_history|organization_authorization_states|GRANT|REVOKE|DROP|ALTER|LOCK\s+TABLE|REINDEX|INSERT|UPDATE|DELETE|TRUNCATE|VACUUM|ANALYZE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var hits = statements.Where(s => forbidden.IsMatch(s)).ToList();
        var lockTimeout = statements.FindIndex(s => s.StartsWith("SET lock_timeout = '5s'", StringComparison.Ordinal));
        var advisory = statements.FindIndex(s => s.Contains("pg_try_advisory_lock", StringComparison.Ordinal));
        var create = statements.FindIndex(s => s.StartsWith("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_journal_capability_refusal_evidence", StringComparison.Ordinal));
        Record("I-09", "statement trace (3 invocations): no transaction statement, no migration-history, organization, grant, DDL other than the one build, no data change; lock timeout, then provisioning lock, then the build",
            hits.Count == 0 && lockTimeout >= 0 && advisory > lockTimeout && create > advisory && statements.Count(s => s.StartsWith("CREATE INDEX", StringComparison.Ordinal)) == 1,
            $"{statements.Count} statements; forbidden hits: [{string.Join(" | ", hits)}]; order lock_timeout={lockTimeout} advisory={advisory} create={create}");
    }

    private async Task<string> DockerLogsAsync()
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("logs");
        psi.ArgumentList.Add(container);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await so) + "\n" + (await se);
    }

    private async Task VerifyOnlyAsync()
    {
        var db = await CreateDatabaseAsync("gate_verify");
        var before = await CatalogueAsync(db);
        var r = await CommandAsync(db, "verify-absent", verifyOnly: true);
        Record("I-10", "--verify-only on an absent index: exit 11 Absent, nothing created", r.ExitCode == 11 && r.Outcome == "Absent" && await IndexFactsAsync(db) is null && (await CatalogueAsync(db)).SetEquals(before), r.ToString());
    }

    private async Task InvalidAsync()
    {
        // An interrupted build by another session leaves an INVALID index with the approved definition.
        var db = await CreateDatabaseAsync("gate_invalid");
        var holder = await HoldSnapshotAsync(db);
        await using var other = await OpenAsync(db, application: "gate-other-builder");
        var build = Task.Run(async () =>
        {
            try
            {
                await ExecAsync(other, "CREATE INDEX CONCURRENTLY ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'");
                return "completed";
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        });
        var pid = await WaitForBuildAsync(db, "gate-other-builder", TimeSpan.FromSeconds(20));
        await Task.Delay(500);
        if (pid is { } p)
        {
            await CancelBackendAsync(p);
        }

        var buildOutcome = await build;
        await ReleaseAsync(holder);
        var invalid = await IndexFactsAsync(db);
        var catalogueBefore = await CatalogueAsync(db);
        var r = await CommandAsync(db, "invalid");
        var after = await IndexFactsAsync(db);
        Record("I-11", "existing invalid index (another session's build cancelled): exit 4 IndexInvalid; not dropped, rebuilt or validated; catalogue unchanged",
            buildOutcome == "57014" && invalid?["valid"] == "false" && r.ExitCode == 4 && r.Outcome == "IndexInvalid" && after?["oid"] == invalid?["oid"] && after?["valid"] == "false" && (await CatalogueAsync(db)).SetEquals(catalogueBefore),
            $"setup build={buildOutcome}; before: {Describe(invalid)}; {r}");

        // Documented manual recovery (runbook): DROP INDEX CONCURRENTLY by the operator, then re-run the command.
        await ExecAsync(db, "DROP INDEX CONCURRENTLY authority.ix_journal_capability_refusal_evidence");
        var again = await CommandAsync(db, "invalid-recovered");
        Record("I-12", "manual recovery: after the operator's DROP INDEX CONCURRENTLY, the command creates a valid approved index", again.ExitCode == 0 && again.Outcome == "Created" && IsApprovedValid(await IndexFactsAsync(db)), again.ToString());
    }

    private async Task MismatchAsync()
    {
        var variants = new (string Label, string Sql)[]
        {
            ("column order", "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries (journal_stream_revision, organization_id) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'"),
            ("unique", "CREATE UNIQUE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'"),
            ("kind-only predicate", "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision) WHERE journal_event_kind = 'EnvelopeCapabilityRefused'"),
            ("no predicate", "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision)"),
            ("descending revision", "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision DESC) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'"),
            ("other table", "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.organization_authorization_states (organization_id)"),
            ("not an index", "CREATE VIEW authority.ix_journal_capability_refusal_evidence AS SELECT 1 AS gate_value"),
        };
        var i = 0;
        foreach (var (label, sql) in variants)
        {
            var db = await CreateDatabaseAsync($"gate_mismatch_{i++}");
            await ExecAsync(db, sql);
            var before = await IndexFactsAsync(db);
            var catalogueBefore = await CatalogueAsync(db);
            var r = await CommandAsync(db, "mismatch-" + label);
            var after = await IndexFactsAsync(db);
            Record($"I-13.{i}", $"same name, wrong definition ({label}): exit 5 DefinitionMismatch, object unchanged, nothing created",
                r.ExitCode == 5 && r.Outcome == "DefinitionMismatch" && after?["oid"] == before?["oid"] && after?["def"] == before?["def"] && (await CatalogueAsync(db)).SetEquals(catalogueBefore),
                r + " mismatches=" + (r.Report?["mismatches"]?.ToJsonString() ?? "-"));
        }
    }

    private async Task ConcurrentAsync()
    {
        // Two invocations at once: the second finds the provisioning lock held and exits 10 at once; the first completes.
        var db = await CreateDatabaseAsync("gate_concurrent");
        var holder = await HoldSnapshotAsync(db);
        var first = CommandAsync(db, "concurrent-1");
        var pid = await WaitForBuildAsync(db, App, TimeSpan.FromSeconds(20));
        var second = await CommandAsync(db, "concurrent-2");
        await ReleaseAsync(holder);
        var firstResult = await first;
        Record("I-14", "concurrent invocations: the second exits 10 Busy without inspecting or changing anything; the first creates the index",
            pid is not null && second.ExitCode == 10 && second.Outcome == "Busy" && firstResult.ExitCode == 0 && firstResult.Outcome == "Created" && IsApprovedValid(await IndexFactsAsync(db)),
            $"build pid seen={pid is not null}; second {second}; first {firstResult}");
    }

    private async Task InProgressAsync()
    {
        // Another (non-command) session is building the approved index: the command reports it and changes nothing.
        var db = await CreateDatabaseAsync("gate_inprogress");
        var holder = await HoldSnapshotAsync(db);
        await using var other = await OpenAsync(db, application: "gate-other-builder");
        var build = ExecAsync(other, "CREATE INDEX CONCURRENTLY ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'");
        var pid = await WaitForBuildAsync(db, "gate-other-builder", TimeSpan.FromSeconds(20));
        var during = await CommandAsync(db, "in-progress");
        await ReleaseAsync(holder);
        await build;
        var afterwards = await CommandAsync(db, "in-progress-after");
        Record("I-15", "concurrent build by another session: exit 6 BuildInProgress, nothing changed; once it completes, exit 0 AlreadyValid",
            pid is not null && during.ExitCode == 6 && during.Outcome == "BuildInProgress" && afterwards.ExitCode == 0 && afterwards.Outcome == "AlreadyValid",
            $"during {during}; afterwards {afterwards}");
    }

    private async Task InterruptedAsync()
    {
        // The command's own build is interrupted (its statement cancelled): it reports the invalid index and does not repair it.
        var db = await CreateDatabaseAsync("gate_interrupted");
        var holder = await HoldSnapshotAsync(db);
        var run = CommandAsync(db, "interrupted");
        var pid = await WaitForBuildAsync(db, App, TimeSpan.FromSeconds(20));
        await Task.Delay(300);
        if (pid is { } p)
        {
            await CancelBackendAsync(p);
        }

        var r = await run;
        await ReleaseAsync(holder);
        var left = await IndexFactsAsync(db);
        Record("I-16", "interrupted concurrent build: exit 4 IndexInvalid (SQLSTATE 57014), the invalid index is left in place (same oid), not dropped or rebuilt",
            pid is not null && r.ExitCode == 4 && r.SqlState == "57014" && r.StateAfter == "Invalid" && left?["valid"] == "false" && left?["oid"] == r.IndexOid?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{r}; left: {Describe(left)}");
        var rerun = await CommandAsync(db, "interrupted-rerun");
        Record("I-17", "re-running over the invalid index does not repair it: exit 4 again, still invalid", rerun.ExitCode == 4 && (await IndexFactsAsync(db))?["valid"] == "false", rerun.ToString());
    }

    private async Task PrivilegesAsync()
    {
        var db = await CreateDatabaseAsync("gate_privileges");
        var before = await CatalogueAsync(db);
        var runtime = await CommandAsync(db, "privileges-runtime", user: runtimeUser, password: runtimePassword);
        var outsider = await CommandAsync(db, "privileges-outsider", user: outsiderUser, password: outsiderPassword);
        var after = await CatalogueAsync(db);
        Record("I-18", "insufficient privileges: the runtime role and an unrelated login are refused (exit 7 NotOwner), no index, no catalogue or privilege change",
            runtime.ExitCode == 7 && outsider.ExitCode == 7 && await IndexFactsAsync(db) is null && after.SetEquals(before),
            $"runtime {runtime}; outsider {outsider}; {Diff(before, after)}");
        // After a successful provisioning with the migration identity, the runtime and outsider privileges are unchanged and the index has no ACL.
        var ok = await CommandAsync(db, "privileges-owner");
        var granted = await CatalogueAsync(db);
        var privBefore = before.Where(l => l.StartsWith("priv ", StringComparison.Ordinal) || l.StartsWith("nspriv ", StringComparison.Ordinal) || l.StartsWith("member ", StringComparison.Ordinal) || l.StartsWith("role ", StringComparison.Ordinal)).ToHashSet();
        var privAfter = granted.Where(l => l.StartsWith("priv ", StringComparison.Ordinal) || l.StartsWith("nspriv ", StringComparison.Ordinal) || l.StartsWith("member ", StringComparison.Ordinal) || l.StartsWith("role ", StringComparison.Ordinal)).ToHashSet();
        Record("I-19", "no runtime privilege expansion: role attributes, memberships, table and schema privileges of the runtime role and the unrelated login unchanged; index ACL empty",
            ok.ExitCode == 0 && privBefore.SetEquals(privAfter) && (await IndexFactsAsync(db))?["no_acl"] == "true", $"{ok}; privilege lines {privAfter.Count}; {Diff(privBefore, privAfter)}");
    }

    private async Task LockTimeoutTableAsync()
    {
        // A session holds a lock that conflicts with the build's table lock: the build waits at most 5 s and nothing is created.
        var db = await CreateDatabaseAsync("gate_locktable");
        await using var c = await OpenAsync(db, application: "gate-lock-holder");
        await using var tx = await c.BeginTransactionAsync();
        await ExecAsync(c, "LOCK TABLE authority.security_journal_entries IN SHARE UPDATE EXCLUSIVE MODE", tx);
        var r = await CommandAsync(db, "lock-timeout-table");
        await tx.RollbackAsync();
        Record("I-20", "lock timeout on the table lock: exit 8 LockTimeout (55P03) within the bound, index absent afterwards",
            r.ExitCode == 8 && r.SqlState == "55P03" && r.StateAfter == "Absent" && await IndexFactsAsync(db) is null && r.Milliseconds < 30_000, r.ToString());
    }

    private async Task LockTimeoutWaitAsync()
    {
        // A session keeps an old snapshot for longer than the bound: the build's wait for it times out; the invalid index is reported, not removed.
        var db = await CreateDatabaseAsync("gate_lockwait");
        var holder = await HoldSnapshotAsync(db);
        var run = CommandAsync(db, "lock-timeout-wait");
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))); // bounded: never hold the snapshot indefinitely
        await ReleaseAsync(holder);
        var r = await run;
        var left = await IndexFactsAsync(db);
        Record("I-21", "lock timeout while the build waits for an old snapshot: exit 8 LockTimeout (55P03), the invalid index is reported and left in place",
            r.ExitCode == 8 && r.SqlState == "55P03" && r.StateAfter == "Invalid" && left?["valid"] == "false", $"{r}; left: {Describe(left)}");
    }

    private async Task NoHistoryOrOrganizationLockAsync()
    {
        // The migration history and the organization table are locked ACCESS EXCLUSIVE by another session (as an EF migration
        // would lock the history): the command never needs either, so it still completes.
        var db = await CreateDatabaseAsync("gate_nolocks");
        var historyBefore = await LinesAsync(db, "SELECT \"MigrationId\" FROM authority.ef_migrations_history ORDER BY 1");
        await using var c = await OpenAsync(db, application: "gate-lock-holder");
        await using var tx = await c.BeginTransactionAsync();
        await ExecAsync(c, "LOCK TABLE authority.ef_migrations_history IN ACCESS EXCLUSIVE MODE", tx);
        await ExecAsync(c, "LOCK TABLE authority.organization_authorization_states IN ACCESS EXCLUSIVE MODE", tx);
        var r = await CommandAsync(db, "no-history-lock");
        await tx.RollbackAsync();
        var historyAfter = await LinesAsync(db, "SELECT \"MigrationId\" FROM authority.ef_migrations_history ORDER BY 1");
        Record("I-22", "no migration-history or organization-row interaction: completes while both tables are held ACCESS EXCLUSIVE by another session; history unchanged",
            r.ExitCode == 0 && r.Outcome == "Created" && historyBefore.SequenceEqual(historyAfter), $"{r}; migrations {historyAfter.Count}");
    }

    private async Task BigAsync()
    {
        var db = await CreateDatabaseAsync("gate_big");
        var mature = orgs[0];
        var evidenceOrg = orgs[1];
        var sw = Stopwatch.StartNew();
        var matureRows = bigRows / 2;
        var evidenceRows = bigRows / 10;
        var otherRows = (bigRows - matureRows - evidenceRows) / 8;
        await ExecAsync(db, Journal(mature, "audit", 301, 300 + matureRows, "'FieldSearchExecuted'"));
        await ExecAsync(db, Journal(mature, "authorization", 51, 50 + (bigRows / 40), "'SecurityCommandCommitted'"));
        await ExecAsync(db, Journal(evidenceOrg, "audit", 301, 300 + evidenceRows, "CASE WHEN g % 40 = 0 THEN 'EnvelopeCapabilityRefused' ELSE 'FieldSearchExecuted' END"));
        foreach (var org in orgs.Skip(2))
        {
            await ExecAsync(db, Journal(org, "audit", 301, 300 + otherRows, "'FieldSearchExecuted'"));
        }

        var total = Convert.ToInt64(await ScalarAsync(db, "SELECT count(*) FROM authority.security_journal_entries"), System.Globalization.CultureInfo.InvariantCulture);
        var evidence = Convert.ToInt64(await ScalarAsync(db, "SELECT count(*) FROM authority.security_journal_entries WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'"), System.Globalization.CultureInfo.InvariantCulture);
        var matureAudit = Convert.ToInt64(await ScalarAsync(db, $"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{mature}'"), System.Globalization.CultureInfo.InvariantCulture);
        var seeding = sw.ElapsedMilliseconds;

        var dataBefore = await DataAsync(db);
        var catalogueBefore = await CatalogueAsync(db);
        var create = await CommandAsync(db, "big-create");
        var size = Convert.ToInt64(await ScalarAsync(db, "SELECT pg_relation_size('authority.ix_journal_capability_refusal_evidence')"), System.Globalization.CultureInfo.InvariantCulture);
        var tableSize = Convert.ToInt64(await ScalarAsync(db, "SELECT pg_total_relation_size('authority.security_journal_entries')"), System.Globalization.CultureInfo.InvariantCulture);
        Record("I-23", "large unrelated history: the index builds concurrently on a journal of this size and is valid; it covers only the evidence rows",
            create.ExitCode == 0 && create.Outcome == "Created" && IsApprovedValid(await IndexFactsAsync(db)),
            $"{total} journal rows ({matureAudit} of the mature organization, {evidence} evidence rows), seeding {seeding} ms; {create}; index {size} bytes, table+indexes {tableSize} bytes");
        Record("I-24", "no data change on the large database (every Authority table, including the 2M-row journal) and catalogue differs by exactly the index",
            dataBefore.SequenceEqual(await DataAsync(db)) && OnlyIndexAdded(catalogueBefore, await CatalogueAsync(db)), $"{dataBefore.Count} tables");

        // Query plans of the approved lookup, run as the runtime role (the identity that will run it in E3-M2f-3b).
        await PlanChecksAsync(db, mature, evidenceOrg, "I-25", "no statistics (never analyzed)");
        await ExecAsync(db, "ANALYZE authority.security_journal_entries");
        await PlanChecksAsync(db, mature, evidenceOrg, "I-26", "after ANALYZE");
        await ExecAsync(db, "UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending' WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'");
        await ExecAsync(db, "UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored' WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'");
        await PlanChecksAsync(db, mature, evidenceOrg, "I-27", "with dead index entries from archive-delivery updates (no vacuum)");
        await ExecAsync(db, "VACUUM ANALYZE authority.security_journal_entries");
        await PlanChecksAsync(db, mature, evidenceOrg, "I-28", "after VACUUM ANALYZE");
        await GenericPlanAsync(db, evidenceOrg);

        // Live synthetic writers while the index is (re)built: drop it (test setup), then build with writers on all streams.
        await ExecAsync(db, "DROP INDEX authority.ix_journal_capability_refusal_evidence");
        await LiveWritersAsync(db);
    }

    private async Task PlanChecksAsync(string db, Guid mature, Guid evidenceOrg, string id, string condition)
    {
        await using var c = await OpenAsync(db, runtimeUser, runtimePassword, "gate-plan");
        foreach (var (label, org, expectedRows) in new[] { ("mature organization without evidence", mature, 0), ("organization with evidence", evidenceOrg, 8) })
        {
            await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + Lookup, c);
            cmd.Parameters.AddWithValue("organization_id", org);
            var json = (string)(await cmd.ExecuteScalarAsync())!;
            var root = JsonNode.Parse(json)![0]!["Plan"]!;
            var (ok, detail) = AnalyzePlan(root, expectedRows);
            await File.WriteAllTextAsync(Path.Combine(evidenceDir, "plans", $"{id}-{(expectedRows == 0 ? "mature" : "evidence")}.json"), json);
            Record($"{id}{(expectedRows == 0 ? "a" : "b")}", $"plan of the approved lookup, {label}, {condition}: Limit over a backward index scan of IX-B, at most 8 rows, no other index, no sequential or bitmap scan", ok, detail);
        }
    }

    private static (bool Ok, string Detail) AnalyzePlan(JsonNode root, int expectedRows)
    {
        var nodes = new List<JsonNode>();
        void Walk(JsonNode n)
        {
            nodes.Add(n);
            if (n["Plans"] is JsonArray children)
            {
                foreach (var child in children)
                {
                    Walk(child!);
                }
            }
        }

        Walk(root);
        var types = nodes.Select(n => n["Node Type"]!.GetValue<string>()).ToList();
        var indexes = nodes.Where(n => n["Index Name"] is not null).Select(n => n["Index Name"]!.GetValue<string>()).ToList();
        var scan = nodes.FirstOrDefault(n => n["Index Name"]?.GetValue<string>() == IndexName);
        var direction = scan?["Scan Direction"]?.GetValue<string>();
        var rows = root["Actual Rows"]!.GetValue<double>();
        long Blocks(JsonNode n, string k) => n[k]?.GetValue<long>() ?? 0;
        var buffers = Blocks(root, "Shared Hit Blocks") + Blocks(root, "Shared Read Blocks");
        var heapFetches = scan?["Heap Fetches"]?.GetValue<long>();
        var ok = types[0] == "Limit" && scan is not null && direction == "Backward" && indexes.All(i => i == IndexName)
                 && !types.Any(t => t.Contains("Seq Scan", StringComparison.Ordinal) || t.Contains("Bitmap", StringComparison.Ordinal) || t == "Sort")
                 && (int)rows == expectedRows && buffers <= 64;
        return (ok, $"nodes [{string.Join(" > ", types)}], index [{string.Join(",", indexes)}], direction {direction}, rows {rows}, shared buffers {buffers}, heap fetches {heapFetches?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, time {root["Actual Total Time"]} ms");
    }

    private async Task GenericPlanAsync(string db, Guid evidenceOrg)
    {
        await using var c = await OpenAsync(db, runtimeUser, runtimePassword, "gate-plan");
        await ExecAsync(c, "SET plan_cache_mode = force_generic_plan");
        await ExecAsync(c, "PREPARE gate_lookup(uuid) AS " + LookupPositional);
        var json = (string)(await ScalarAsync(c, $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) EXECUTE gate_lookup('{evidenceOrg}')"))!;
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "plans", "I-29-generic.json"), json);
        var (ok, detail) = AnalyzePlan(JsonNode.Parse(json)![0]!["Plan"]!, 8);
        Record("I-29", "generic prepared plan (plan_cache_mode = force_generic_plan) with literal stream and kind still uses IX-B", ok, detail);

        // Demonstration (outside pass totals): with the stream and kind as parameters, a generic plan cannot use the partial index.
        await ExecAsync(c, "PREPARE gate_lookup_param(uuid, text, text) AS SELECT security_journal_entry_id, journal_stream_revision FROM authority.security_journal_entries WHERE organization_id = $1 AND journal_stream_name = $2 AND journal_event_kind = $3 ORDER BY journal_stream_revision DESC LIMIT 8");
        var demo = (string)(await ScalarAsync(c, $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) EXECUTE gate_lookup_param('{evidenceOrg}', 'audit', 'EnvelopeCapabilityRefused')"))!;
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "plans", "D-1-generic-parameterized.json"), demo);
        var (_, demoDetail) = AnalyzePlan(JsonNode.Parse(demo)![0]!["Plan"]!, 8);
        demos.Add(("D-1", "generic plan with the stream and kind as bind parameters (why the literals are binding)", demoDetail + $"; uses IX-B: {demo.Contains(IndexName, StringComparison.Ordinal)}"));
    }

    private async Task LiveWritersAsync(string db)
    {
        using var stop = new CancellationTokenSource();
        var errors = new List<string>();
        var inserted = new long[4];
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            var org = orgs[2 + w];
            await using var c = await OpenAsync(db, application: $"gate-writer-{w}");
            var revision = Convert.ToInt64(await ScalarAsync(c, $"SELECT max(journal_stream_revision) FROM authority.security_journal_entries WHERE organization_id = '{org}' AND journal_stream_name = 'audit'"), System.Globalization.CultureInfo.InvariantCulture);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    revision++;
                    var kind = revision % 10 == 0 ? "'EnvelopeCapabilityRefused'" : "'FieldSearchExecuted'";
                    if (w < 2)
                    {
                        await ExecAsync(c, Journal(org, "audit", revision, revision, kind));
                    }
                    else
                    {
                        // As the Authority writes: organization row lock first, then the journal row, in one short transaction.
                        await using var tx = await c.BeginTransactionAsync();
                        await ExecAsync(c, $"SELECT 1 FROM authority.organization_authorization_states WHERE organization_id = '{org}' FOR UPDATE", tx);
                        await ExecAsync(c, Journal(org, "audit", revision, revision, kind), tx);
                        if (w == 3)
                        {
                            await ExecAsync(c, $"UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending' WHERE organization_id = '{org}' AND journal_stream_name = 'audit' AND journal_stream_revision = {revision}", tx);
                        }

                        await tx.CommitAsync();
                    }

                    Interlocked.Increment(ref inserted[w]);
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add($"writer {w}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                await Task.Delay(5);
            }
        })).ToList();

        await Task.Delay(1000);
        var r = await CommandAsync(db, "live-writers");
        await Task.Delay(1000);
        await stop.CancelAsync();
        await Task.WhenAll(writers);

        string viaIndex, viaHeap, indexPlan;
        const string countSql = "SELECT count(*)::text || ':' || coalesce(sum(hashtextextended(security_journal_entry_id::text, 0))::text, '0') FROM authority.security_journal_entries WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'";
        await using (var c = await OpenAsync(db))
        {
            await ExecAsync(c, "SET enable_seqscan = off");
            await ExecAsync(c, "SET enable_bitmapscan = off");
            indexPlan = string.Join(" ", await LinesAsync(c, "EXPLAIN " + countSql));
            viaIndex = (string)(await ScalarAsync(c, countSql))!;
        }

        await using (var c = await OpenAsync(db))
        {
            await ExecAsync(c, "SET enable_indexscan = off");
            await ExecAsync(c, "SET enable_indexonlyscan = off");
            await ExecAsync(c, "SET enable_bitmapscan = off");
            viaHeap = (string)(await ScalarAsync(c, countSql))!;
        }

        Record("I-30", "concurrent build with live synthetic writers (autocommit and organization-locked transactions, archive-state updates): no writer error, no deadlock, valid index, exactly the matching rows indexed",
            r.ExitCode == 0 && r.Outcome == "Created" && errors.Count == 0 && IsApprovedValid(await IndexFactsAsync(db)) && indexPlan.Contains(IndexName, StringComparison.Ordinal) && viaIndex == viaHeap,
            $"{r}; writer rows {string.Join("/", inserted)}; errors {errors.Count} [{string.Join(" | ", errors.Take(3))}]; via index {viaIndex}; via heap {viaHeap}");
    }
}
