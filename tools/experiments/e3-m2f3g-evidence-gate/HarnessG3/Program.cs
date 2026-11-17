// E3-M2f-3G capability-refusal evidence: disposable PostgreSQL 18 verification gate (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5, §5A,
// §10; record docs/security/E3_M2F3G_VERIFICATION_RECORD.md). TEST-ONLY. Development/Testing only.
//
// What is real here: PostgreSQL 18.0 (one disposable container; every database is created by this run and admitted by nonce and system
// identifier, Linked/Isolation.cs); one disposable Vault dev container (the host's Vault Transit signer); this tree's Authority Release
// build, as (a) the real HOST process (dotnet AuthorizationAuthority.Api.dll, Development, settings file removed, every setting explicit,
// deployment floor D = 2) driven over HTTP by real business clients, and (b)
// the real CapabilityRefusalEvidenceRecorder over the real AuthorityStore with the approved runtime identity, in-process, for the
// scenarios that need an exact fault point; the E3-M2f-3i command for the index; the real archive processor. Nothing else substitutes for
// row locking, database time, the catalogue check, the eight-row lookup, the journal insert, the audit-revision update, the digest
// chain, commit/rollback or ambiguous commits.
//
// What is controlled test setup (disposable databases only, all labelled): organizations and census snapshots seeded as the services
// would write them; a large synthetic audit history; earlier-hour and malformed evidence rows appended through the real AuditJournal;
// server-side faults (a NOT VALID CHECK constraint, a deferred constraint trigger) added to one disposable copy each; in-process
// interceptors that pause, terminate or fail a statement or a commit at an exact point.
//
// Never: an Azure call, a persistent database, Envelope v2 acceptance (Content:EnvelopeV2AcceptanceEnabled is false everywhere and no
// acceptance policy is recorded), a data-key binding (no bound organization exists; no v2 content is written), a runtime privilege
// change, a migration, or a G14 recovery mechanism. The uniqueness proof is an independent read-only SQL oracle (Oracle.cs) after every
// applicable scenario, never the recorder's own result classification.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Npgsql;

string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException(name);

var inputs = new Inputs(
    Need("G3_AUTHORITY_DLL"), Need("G3_HOSTBIN"), Need("G3_DOTNET"), Need("G3_ADMIN"), Need("G3_RUNTIME"), Need("G3_EVIDENCE"),
    int.Parse(Need("G3_PORT"), System.Globalization.CultureInfo.InvariantCulture), Need("G3_NONCE"), Need("G3_SYSID"), Need("G3_CONTAINER"),
    (Environment.GetEnvironmentVariable("G3_BUSINESS_AGENTS") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal),
    Need("G3_AGENT_WORK"), Environment.GetEnvironmentVariable("G3_GROUPS") ?? "all",
    int.Parse(Environment.GetEnvironmentVariable("G3_BIG_ROWS") ?? "2000000", System.Globalization.CultureInfo.InvariantCulture),
    int.Parse(Environment.GetEnvironmentVariable("G3_BOUNDARY_MAX_WAIT_MINUTES") ?? "61", System.Globalization.CultureInfo.InvariantCulture),
    Environment.GetEnvironmentVariable("G3_RUN_LABEL") ?? "run",
    Need("G3_VAULT_ADDRESS"), int.Parse(Need("G3_VAULT_PORT"), System.Globalization.CultureInfo.InvariantCulture), Need("G3_VAULT_TOKEN"));
foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("G3_", StringComparison.Ordinal)).ToList())
{
    Environment.SetEnvironmentVariable(key, null); // never inherited by a child process
}

var removed = Target.ScrubProcessEnvironment();
return await new Lab(inputs, removed).RunAsync();

internal sealed record Inputs(string AuthorityDll, string HostBinaries, string Dotnet, string AdminCs, string RuntimeCs, string EvidenceDir, int Port, string Nonce, string SystemIdentifier,
    string Container, IReadOnlyDictionary<string, string> BusinessAgents, string AgentWorkDir, string Groups, int BigRows, int BoundaryMaxWaitMinutes, string RunLabel,
    string VaultAddress, int VaultPort, string VaultAuthorityToken);

internal sealed record Check(string Id, string Group, string Kind, string Description, string Expected, string Observed, bool Pass, string? Gap);

internal sealed partial class Lab(Inputs inputs, IReadOnlyList<string> scrubbed)
{
    private const string Maintenance = "m2f3ggate";
    private const string RuntimeRole = "authority_runtime";

    private readonly List<Check> checks = [];
    private readonly List<(string Id, string Reason)> notExecuted = [];
    private readonly List<string> log = [];
    private readonly HashSet<string> groups = new(StringComparer.Ordinal);
    private string adminCs = string.Empty;
    private string CurrentGroup { get; set; } = "setup";

    private bool Run(string group) => groups.Contains("all") || groups.Contains(group);

    public async Task<int> RunAsync()
    {
        foreach (var g in inputs.Groups.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            groups.Add(g);
        }

        Directory.CreateDirectory(Path.Combine(inputs.EvidenceDir, "plans"));
        Directory.CreateDirectory(Path.Combine(inputs.EvidenceDir, "traces"));
        var stopped = false;
        var started = Stopwatch.StartNew();
        try
        {
            Target.Configure(inputs.Port, inputs.Nonce, inputs.SystemIdentifier, inputs.VaultPort, Maintenance);
            await Target.AdmitAsync(inputs.AdminCs, Maintenance);
            adminCs = Target.Require(new NpgsqlConnectionStringBuilder(inputs.AdminCs) { Database = Maintenance, Pooling = false }.ConnectionString);
            await ScenariosAsync();
        }
        catch (IsolationViolation ex)
        {
            stopped = true;
            Log("STOP: " + ex.Message);
            checks.Add(new Check("STOP", "Isolation", "Isolation", "every connection stays inside the verified disposable environment", "no isolation violation", ex.Message, false, null));
        }
        catch (Exception ex)
        {
            Log("HARNESS ABORTED: " + ex);
            checks.Add(new Check("HARNESS", CurrentGroup, "Harness", "harness completed", "no unexpected exception", Describe(ex), false, null));
        }
        finally
        {
            await StopAllHostsAsync();
            NpgsqlConnection.ClearAllPools();
        }

        var counted = checks.Where(c => c.Gap is null).ToList();
        var failed = counted.Count(c => !c.Pass);
        var gaps = checks.Where(c => c.Gap is not null).ToList();
        var json = JsonSerializer.Serialize(new
        {
            harness = "E3-M2f-3G capability-refusal evidence PostgreSQL verification (real Authority host and real recorder/store on disposable PostgreSQL 18.0)",
            run = inputs.RunLabel,
            groups = inputs.Groups,
            isolation = new { databases = Target.Databases.Order(StringComparer.Ordinal).ToList(), scrubbedEnvironmentVariables = scrubbed, stopped },
            postgres = stopped ? "not queried after a stop" : await SafeScalarAsync("SELECT version()"),
            elapsedSeconds = (long)started.Elapsed.TotalSeconds,
            checksTotal = counted.Count,
            checksFailed = failed,
            byKind = counted.GroupBy(c => c.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new { total = g.Count(), passed = g.Count(c => c.Pass), failed = g.Count(c => !c.Pass) }),
            byGroup = counted.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new { total = g.Count(), passed = g.Count(c => c.Pass), failed = g.Count(c => !c.Pass) }),
            demonstrationsOutsidePassTotals = gaps.Count,
            notExecuted = notExecuted.Select(n => new { id = n.Id, reason = n.Reason }).ToList(),
            checks = counted,
            demonstrations = gaps,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, "m2f3g-evidence.json"), json + "\n");
        await File.WriteAllLinesAsync(Path.Combine(inputs.EvidenceDir, "m2f3g-log.txt"), log);
        foreach (var c in checks)
        {
            Console.WriteLine($"{(c.Gap is not null ? c.Gap : c.Pass ? "PASS" : "FAIL")} {c.Id} [{c.Group}/{c.Kind}] {c.Description} | expected: {c.Expected} | observed: {c.Observed}");
        }

        foreach (var n in notExecuted)
        {
            Console.WriteLine($"NOT EXECUTED {n.Id}: {n.Reason}");
        }

        Console.WriteLine($"checks {counted.Count}, failed {failed}, demonstrations outside the pass totals {gaps.Count}, not executed {notExecuted.Count}");
        return stopped || failed > 0 ? 1 : 0;
    }

    private async Task ScenariosAsync()
    {
        await SetupAsync();
        if (Run("H"))
        {
            await HostScenariosAsync();
        }

        if (Run("M"))
        {
            await MultiHostScenariosAsync();
        }

        if (Run("T"))
        {
            await T2ScenariosAsync();
        }

        if (Run("F"))
        {
            await FaultScenariosAsync();
        }

        if (Run("Q"))
        {
            await QuotaRetryScenariosAsync();
        }

        if (Run("L"))
        {
            await LivenessScenariosAsync();
        }

        if (Run("W"))
        {
            await WriterScenariosAsync();
        }

        if (Run("G"))
        {
            await RestoreScenariosAsync();
        }

        if (Run("B"))
        {
            await BoundaryScenariosAsync();
        }

        await FinalSentinelsAsync();
    }

    // ------------------------------------------------------------------------------------------------ records
    private void Record(string id, string kind, string description, string expected, string observed, bool pass)
    {
        checks.Add(new Check(id, CurrentGroup, kind, description, expected, observed, pass, null));
        Log($"{(pass ? "PASS" : "FAIL")} {id}: {observed}");
    }

    /// <summary>A demonstration outside the pass totals (an expected gap or a counterfactual); <paramref name="asExpected"/> is reported, never counted.</summary>
    private void Demonstrate(string id, string gap, string description, string expected, string observed, bool asExpected)
    {
        checks.Add(new Check(id, CurrentGroup, "Demonstration", description, expected, observed, asExpected, gap));
        Log($"{gap} {id} (as expected: {asExpected}): {observed}");
    }

    private void NotExecuted(string id, string reason)
    {
        notExecuted.Add((id, reason));
        Log($"NOT EXECUTED {id}: {reason}");
    }

    private void Log(string line)
    {
        var stamped = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line;
        lock (log)
        {
            log.Add(stamped);
            Console.WriteLine("LOG " + stamped); // progress is visible while the run is in flight
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        AuthorizationAuthority.Application.Common.AuthorityException a => $"AuthorityException {a.ErrorCode} {a.HttpStatusCode} retryable={a.Retryable}",
        PostgresException p => $"PostgresException {p.SqlState} {p.MessageText}",
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    // ------------------------------------------------------------------------------------------------ connections
    /// <summary>
    /// Admin (superuser) connections are POOLED: an unpooled connection per query exhausted the Windows ephemeral ports (SocketException
    /// 10048) in an exploratory run. Npgsql resets session state (DISCARD ALL) when a pooled connection is reused; database copies clear
    /// every pool and terminate backends on the template first.
    /// </summary>
    private string AdminFor(string database, string? application = null) =>
        Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Database = database, Pooling = true, MaxPoolSize = 30, ApplicationName = application ?? "g3-admin" }.ConnectionString);

    /// <summary>The approved runtime identity with this build's versioned application name (as the host composes it).</summary>
    private string RuntimeFor(string database, string application) =>
        Target.Require(ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(inputs.RuntimeCs) { Database = database, ApplicationName = application, Pooling = true, MaxPoolSize = 40 }.ConnectionString)!);

    private async Task<NpgsqlConnection> OpenAdminAsync(string database, string? application = null)
    {
        var c = new NpgsqlConnection(AdminFor(database, application));
        await c.OpenAsync();
        return c;
    }

    private async Task<string> ScalarAsync(string database, string sql)
    {
        await using var c = await OpenAdminAsync(database);
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    private async Task<long> CountAsync(string database, string sql) => long.Parse(await ScalarAsync(database, sql), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> SafeScalarAsync(string sql)
    {
        try
        {
            return await ScalarAsync(Maintenance, sql);
        }
        catch (Exception ex)
        {
            return "unavailable: " + Describe(ex);
        }
    }

    private async Task ExecAsync(string database, string sql)
    {
        await using var c = await OpenAdminAsync(database);
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> LinesAsync(string database, string sql)
    {
        await using var c = await OpenAdminAsync(database);
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await r.ReadAsync())
        {
            lines.Add(r.IsDBNull(0) ? "<null>" : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return lines;
    }

    /// <summary>A new disposable database (empty, or a copy of <paramref name="template"/>), commented with the run's nonce and admitted.</summary>
    private async Task CreateDatabaseAsync(string name, string? template = null)
    {
        NpgsqlConnection.ClearAllPools();
        if (template is not null)
        {
            await ExecAsync(Maintenance, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{template}' AND pid <> pg_backend_pid()");
        }

        await ExecAsync(Maintenance, template is null ? $"CREATE DATABASE {name}" : $"CREATE DATABASE {name} TEMPLATE {template}");
        await ExecAsync(Maintenance, $"COMMENT ON DATABASE {name} IS 'm2e2-nonce:{inputs.Nonce}'");
        await Target.AdmitAsync(inputs.AdminCs, name);
        Log($"database {name} created{(template is null ? string.Empty : " from " + template)} and admitted");
    }

    // ------------------------------------------------------------------------------------------------ Authority commands (separate processes)
    internal sealed record CommandResult(int ExitCode, string Output, JsonElement? Report)
    {
        public string? Outcome => Report is { } r && r.TryGetProperty("outcome", out var o) ? o.GetString() : null;

        public override string ToString() => $"exit {ExitCode}" + (Outcome is null ? string.Empty : " outcome " + Outcome);
    }

    private async Task<CommandResult> AuthorityCommandAsync(string database, string application, params string[] arguments)
    {
        var start = new ProcessStartInfo(inputs.Dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(inputs.AuthorityDll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("G3_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(key);
        }

        // The migration identity, only for the commands that require it (migrate, floors, the E3-M2f-3i index command).
        start.Environment["ConnectionStrings__AuthorityDbMigration"] = AdminFor(database, application);
        start.Environment["Database__RuntimeRoleName"] = RuntimeRole;
        start.Environment["ContentAcceptance__EnforcementPhase"] = "ReleaseA";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + string.Join(' ', arguments));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + "\n" + (await stderr);
        JsonElement? report = null;
        foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')))
        {
            try
            {
                report = JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (JsonException)
            {
                // not the report line
            }
        }

        var flat = output.Replace('\n', ' ').Replace('\r', ' ').Trim();
        Log($"command {string.Join(' ', arguments)} on {database} ({application}) exit {process.ExitCode}: {(flat.Length > 500 ? flat[^500..] : flat)}");
        return new CommandResult(process.ExitCode, flat, report);
    }

    private Task<CommandResult> ProvisionIndexAsync(string database, string label, bool verifyOnly = false) =>
        verifyOnly
            ? AuthorityCommandAsync(database, "g3-index-" + label, "--provision-capability-refusal-evidence-index", "--verify-only")
            : AuthorityCommandAsync(database, "g3-index-" + label, "--provision-capability-refusal-evidence-index");

    // ------------------------------------------------------------------------------------------------ server log (statement trace, plans)
    /// <summary>The container's server log lines that carry <paramref name="application"/> (log_line_prefix '%m [%p] %d %a '), with continuation lines.</summary>
    private async Task<List<string>> ServerLogAsync(string application, DateTimeOffset since)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "logs", "--since", since.AddSeconds(-2).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture), inputs.Container })
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)!;
        var so = process.StandardOutput.ReadToEndAsync();
        var se = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var all = ((await so) + "\n" + (await se)).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        // One entry per server log record: the prefixed line plus its continuation lines (multi-line statements, plans, DETAIL).
        var result = new List<string>();
        var inBlock = false;
        foreach (var line in all)
        {
            var prefixed = line.Length > 24 && char.IsDigit(line[0]) && line[4] == '-' && line.Contains(" [", StringComparison.Ordinal);
            if (prefixed)
            {
                inBlock = line.Contains(" " + application + " ", StringComparison.Ordinal);
                if (inBlock)
                {
                    result.Add(line);
                }
            }
            else if (inBlock && result.Count > 0)
            {
                result[^1] += "\n" + line;
            }
        }

        return result;
    }

    /// <summary>
    /// The statements of the sessions named <paramref name="application"/>, in order. The approved runtime role carries the product's
    /// role setting log_statement = 'none' (AuthorityRuntimeGrants), so its statements are taken from auto_explain entries (enabled per
    /// disposable database by the harness): the query text of every planned statement, in completion order. Utility statements (SET
    /// LOCAL, BEGIN, COMMIT) are not planned and do not appear; their effects are checked separately. Admin sessions still log statements.
    /// </summary>
    private static List<string> Statements(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            var plan = line.IndexOf("Query Text: ", StringComparison.Ordinal);
            if (line.Contains("LOG:  duration: ", StringComparison.Ordinal) && line.Contains(" plan:", StringComparison.Ordinal) && plan >= 0)
            {
                var queryLines = line[(plan + 12)..].Split('\n').TakeWhile(l => !l.Contains("(cost=", StringComparison.Ordinal)).Select(l => l.Trim()).Where(l => l.Length > 0);
                result.Add(string.Join(" ", queryLines).Trim());
                continue;
            }

            foreach (var marker in new[] { "LOG:  statement: ", "LOG:  execute <unnamed>: ", "LOG:  execute " })
            {
                var i = line.IndexOf(marker, StringComparison.Ordinal);
                if (i >= 0)
                {
                    var text = line[(i + marker.Length)..];
                    if (marker == "LOG:  execute " && text.Contains(": ", StringComparison.Ordinal))
                    {
                        text = text[(text.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                    }

                    result.Add(string.Join(" ", text.Split('\n').Select(l => l.Trim())).Trim());
                    break;
                }
            }
        }

        return result;
    }

    private async Task SaveTraceAsync(string name, IEnumerable<string> lines) =>
        await File.WriteAllLinesAsync(Path.Combine(inputs.EvidenceDir, "traces", name + ".log"), lines);

    private async Task<DateTimeOffset> DatabaseNowAsync(string database) =>
        DateTimeOffset.Parse(await ScalarAsync(database, "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static string Short(string text, int max = 400) => text.Length > max ? text[..max] + "…" : text;
}
