// E3-M2e-2 Authority host-start checks (D-5). The real Authority host (dotnet AuthorizationAuthority.Api.dll) is started from a COPY of
// this tree's Release output whose appsettings.Development.json was deleted by the script, with the working directory (content root)
// set to that copy, so no default can point at a persistent stack: every setting is passed explicitly and every connection string
// passes Target.Require. The host runs against dedicated disposable databases (m2e2host, m2e2noledger), never against a database a
// scenario writes to. SpiceDB points at a closed loopback port; the security archive is the in-memory archive (Development) or
// unavailable (Testing). Each host process is killed at the end of its check.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

internal sealed partial class Lab
{
    private const string HostDatabase = "m2e2host";
    private const string NoLedgerDatabase = "m2e2noledger";

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private Dictionary<string, string> HostSettings(string database, string connectionString, string requiredFloor = "3", bool acceptanceOn = true) => new(StringComparer.Ordinal)
    {
        ["ConnectionStrings__AuthorityDb"] = Target.Require(new NpgsqlConnectionStringBuilder(connectionString) { Database = database, ApplicationName = "m2e2-host" }.ConnectionString),
        ["Signing__Provider"] = "LocalDevelopment",
        ["SecurityArchive__Provider"] = "InMemory",
        ["SpiceDb__Endpoint"] = "127.0.0.1:" + FreeLoopbackPort(),
        ["SpiceDb__PresharedKey"] = "m2e2-unused-preshared-key",
        ["ContentAcceptance__EnforcementPhase"] = "ReleaseB",
        ["ContentAcceptance__RequiredFloor"] = requiredFloor,
        ["Content__EnvelopeV2AcceptanceEnabled"] = acceptanceOn ? "true" : "false",
        ["Identity__Workload__Issuer"] = "enterpriseforms-business-api",
        ["Identity__Workload__Audience"] = "authorization-authority",
        ["Identity__Workload__SharedKeyBase64"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("m2e2-workload-test-only-shared-key-000000")),
        ["Identity__Operator__Issuer"] = "authority-operator",
        ["Identity__Operator__Audience"] = "authorization-authority-operator",
        ["Identity__Operator__SharedKeyBase64"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("m2e2-operator-test-only-shared-key-000000")),
        ["Identity__UserAssertion__ProviderKind"] = "TestIdentityProvider",
        ["Identity__UserAssertion__Issuer"] = "https://test-idp.m2e2.invalid",
        ["Identity__UserAssertion__Audience"] = "enterpriseforms",
        ["Identity__UserAssertion__SharedKeyBase64"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("m2e2-user-assertion-test-only-key-000000")),
        ["ASPNETCORE_URLS"] = "http://127.0.0.1:" + FreeLoopbackPort(),
    };

    /// <summary>Starts the host; returns when it exits or when <paramref name="until"/> holds (then it is killed), or after the timeout.</summary>
    private async Task<(bool Exited, int Code, string Output, bool Condition)> RunHostAsync(string environment, Dictionary<string, string> settings, Func<Task<bool>>? until, TimeSpan timeout)
    {
        if (File.Exists(Path.Combine(inputs.HostBinaries, "appsettings.Development.json")))
        {
            throw new IsolationViolation("the host copy still contains appsettings.Development.json");
        }

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = inputs.HostBinaries };
        start.ArgumentList.Add(Path.Combine(inputs.HostBinaries, "AuthorizationAuthority.Api.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        foreach (var (key, value) in settings)
        {
            start.Environment[key] = value;
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var deadline = DateTime.UtcNow + timeout;
        var condition = false;
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            if (until is not null && await until())
            {
                condition = true;
                break;
            }

            await Task.Delay(500);
        }

        var exited = process.HasExited;
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, $"host-{environment}-{DateTime.UtcNow:HHmmssfff}.log"), text);
        Log($"host {environment}: exited {exited} code {(exited ? process.ExitCode : -1)} condition {condition}");
        return (exited, exited ? process.ExitCode : 0, text, condition);
    }

    private async Task<long> AcceptanceRecordsAsync(string database) =>
        await CountAsync($"SELECT count(*) FROM authority.platform_journal_entries WHERE journal_event_kind = '{EnvelopeAcceptancePolicyObserver.EventKind}' AND journal_event_body_json LIKE '%\"acceptance_requested\":true%'", database);

    private async Task HostScenariosAsync()
    {
        CurrentGroup = "H";
        await CreateEmptyDatabaseAsync(HostDatabase);
        var migrated = await HostCommandAsync(AdminFor(HostDatabase), "m2e2-host-migrate", "--migrate");
        var floors = new List<int>();
        foreach (var floor in new[] { "1", "2", "3" })
        {
            floors.Add((await HostCommandAsync(AdminFor(HostDatabase), "m2e2-host-floor" + floor, "--apply-content-acceptance-floor", floor)).Code);
        }

        Record("H-00", "Host", "setup: a dedicated disposable database for the host, migrated (no runtime session yet) and raised to floor 3", "migrate 0; floors 0,0,0; floors 3",
            $"migrate {migrated.Code}; floors {string.Join(",", floors)}; installed {await FloorAsync(HostDatabase)}", migrated.Code == 0 && floors.All(c => c == 0) && await FloorAsync(HostDatabase) == "3");

        {
            var before = await AcceptanceRecordsAsync(HostDatabase);
            var run = await RunHostAsync("Development", HostSettings(HostDatabase, runtimeCs), async () => await AcceptanceRecordsAsync(HostDatabase) > before, TimeSpan.FromSeconds(90));
            var body = await ScalarAsync(AdminFor(HostDatabase), $"SELECT coalesce((SELECT journal_event_body_json FROM authority.platform_journal_entries WHERE journal_event_kind = '{EnvelopeAcceptancePolicyObserver.EventKind}' ORDER BY platform_journal_revision DESC LIMIT 1), 'none')");
            Record("H-01", "Host", "the real Authority host in Development (settings file removed; every setting explicit), acceptance configured on, Release B floor 3, ledger migrated: it starts, passes the startup guard and records the policy (acceptance requested, Release B floor 3) on the disposable platform journal; then it is stopped",
                "running when the record appears; startup log configured=True; recorded body names phase=ReleaseB requiredFloor=3",
                $"exited {run.Exited}; record appeared {run.Condition}; log configured=True {run.Output.Contains("configured=True", StringComparison.Ordinal)}; body Release B floor 3 {body.Contains("phase=ReleaseB requiredFloor=3", StringComparison.Ordinal)}",
                !run.Exited && run.Condition && run.Output.Contains("configured=True", StringComparison.Ordinal) && body.Contains("phase=ReleaseB requiredFloor=3", StringComparison.Ordinal));
        }

        {
            var before = await AcceptanceRecordsAsync(HostDatabase);
            var run = await RunHostAsync("Testing", HostSettings(HostDatabase, runtimeCs), null, TimeSpan.FromSeconds(25));
            Record("H-02", "Host", "the same host in Testing: the in-memory archive is not permitted there, so the policy can never be recorded: the host runs with acceptance configured but NOT effective (fail closed)",
                "still running at 25 s; configured=True; no new acceptance record", $"exited {run.Exited}; configured=True {run.Output.Contains("configured=True", StringComparison.Ordinal)}; acceptance records {before} -> {await AcceptanceRecordsAsync(HostDatabase)}",
                !run.Exited && run.Output.Contains("configured=True", StringComparison.Ordinal) && await AcceptanceRecordsAsync(HostDatabase) == before);
        }

        foreach (var (id, environment, floor, expected, what) in new[]
        {
            ("H-03", "Staging", "3", "must not be enabled outside Development/Testing", "Staging"),
            ("H-04", "Production", "3", "must not be enabled outside Development/Testing", "Production"),
            ("H-05", "Development", "2", "requires ContentAcceptance:EnforcementPhase = ReleaseB and ContentAcceptance:RequiredFloor = 3", "Development with Release B RequiredFloor 2"),
        })
        {
            var run = await RunHostAsync(environment, HostSettings(HostDatabase, runtimeCs, floor), null, TimeSpan.FromSeconds(60));
            Record(id, "Host", $"the host refuses to start with acceptance configured on in {what}", $"exits non-zero; message: {expected}",
                $"exited {run.Exited} code {run.Code}; message {(run.Output.Contains(expected, StringComparison.Ordinal) ? "present" : "ABSENT")}", run.Exited && run.Code != 0 && run.Output.Contains(expected, StringComparison.Ordinal));
        }

        {
            await CreateEmptyDatabaseAsync(NoLedgerDatabase);
            var migrateCs = Target.Require(new NpgsqlConnectionStringBuilder(AdminFor(NoLedgerDatabase)) { ApplicationName = "m2e2-noledger-migrate" }.ConnectionString);
            await using (var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(migrateCs, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options))
            {
                await context.GetService<IMigrator>().MigrateAsync(ContentAcceptanceRuleSql.MigrationId);
            }

            var ledger = await ScalarAsync(AdminFor(NoLedgerDatabase), "SELECT coalesce(string_agg(\"MigrationId\", ','), 'none') FROM authority.ef_migrations_history WHERE \"MigrationId\" LIKE '%ContentDataKeyBindings%'");
            var run = await RunHostAsync("Development", HostSettings(NoLedgerDatabase, adminCs), null, TimeSpan.FromSeconds(60));
            Record("H-06", "Host", "the host refuses to start with acceptance configured on when the data-key ledger migration is not recorded (database migrated only to the CARV migration)",
                "ledger none; exits non-zero; message: data-key ledger migration is not recorded", $"ledger {ledger}; exited {run.Exited} code {run.Code}; message {(run.Output.Contains("data-key ledger migration is not recorded", StringComparison.Ordinal) ? "present" : "ABSENT")}",
                ledger == "none" && run.Exited && run.Code != 0 && run.Output.Contains("data-key ledger migration is not recorded", StringComparison.Ordinal));
        }
    }
}
