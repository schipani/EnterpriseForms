// E3-M2b-1F floor gate, harness F (Development/Testing only; built by scripts/dev/e3-m2b1-carv-pg-gate.ps1, never in place).
// The REAL floor migration applied only by the real build's --apply-content-acceptance-floor command, with the real CARV
// build in-process and a frozen legacy build through the legacy agent. Derived from harness B (same helpers and fixtures).
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
using Microsoft.EntityFrameworkCore.Infrastructure;
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
            harness = "E3-M2b-1F CARV floor gate, harness F (real floor command + real CARV build + legacy agent)",
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
    // E3-M2b-1F: the REAL floor migration, applied only by the real build's --apply-content-acceptance-floor command, against
    // the real CARV build in-process and the frozen legacy build through the legacy agent (optionally with a lagging clock).
    private const string FloorMigrationId = "20261115032228_AuthorityContentAcceptanceRuleFloor1";

    private async Task ScenariosAsync()
    {
        var orgA = await SeedOrgAsync("A", 6);
        var orgB = await SeedOrgAsync("B", 2);
        var rb = Instance("RB", runtimeCs);
        // Raw runtime-role SQL uses unpooled connections: an idle pooled runtime connection without a CARV marker would (rightly)
        // block activation, exactly like an idle pre-CARV instance.
        var rawRuntime = new NpgsqlConnectionStringBuilder(runtimeCs) { Pooling = false, ApplicationName = "carv-raw-sql" }.ConnectionString;
        var markedRuntime = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "carv-guard" }.ConnectionString)!;
        // RB-CARV-1: the required floor now comes from the Release B policy (the E3-M2b-1F option class was replaced, contract §22).
        ContentAcceptanceRuleGuard Guard(int? requiredFloor) => new(new ContentAcceptanceRuleSchemaReader(Factory.For(markedRuntime)), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance, requiredFloor is null ? null : ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, requiredFloor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var record in orgA.Records.Concat(orgB.Records))
        {
            await LegacyWriteAsync(record);
        }

        // ---------- before E3-M2b-1: the floor command refuses and applies nothing
        {
            var (code, output) = await FloorCommandAsync();
            Record("FX-01", "the floor command on a pre-CARV schema is refused; nothing applied", "exit 3 'not fully installed'; no floor; no floor history row",
                $"exit {code}: {output}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                code == 3 && output.Contains("not fully installed", StringComparison.Ordinal) && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
        }

        // ---------- Release A: the ordinary --migrate of a build that CONTAINS the floor migration applies E3-M2b-1 only
        {
            var first = await RealMigrateAsync("carv-migration");
            Record("FX-02", "Release A: ordinary --migrate (the build contains the floor migration) applies E3-M2b-1 and NOT the floor", "OK; CARV recorded; floor none; no floor history row",
                $"{first}; carv={await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'")}; floor={await ScalarAsync(adminCs, FloorQuery)}; floorHistory={await FloorHistoryAsync()}",
                first == "OK" && await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'") == 1 && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
            var second = await RealMigrateAsync("carv-migration");
            Record("FX-03", "re-running the ordinary --migrate still never applies the floor", "OK; floor none; no floor history row", $"{second}; floor={await ScalarAsync(adminCs, FloorQuery)}; floorHistory={await FloorHistoryAsync()}",
                second == "OK" && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
        }

        // ---------- Release A behaviour (the R-CARV-1 exposure the floor closes)
        {
            var carv = await Outcome(WriteAsync(rb, orgA.Records[0]));
            var r = orgA.Records[1];
            var legacyPrepared = await LegacyPrepareAsync(r);
            var legacyCommitted = legacyPrepared.Error is null ? await LegacyCommitAsync(r, legacyPrepared.Prepared!, legacyPrepared.Rows) : legacyPrepared;
            Record("FX-04", "Release A, before the floor: CARV writes commit, and a legacy instance can still commit its own operation (R-CARV-1, the exposure closed by the floor)", "OK; legacy old->old accepted; NULL/NULL Committed",
                $"{carv}; legacy={legacyCommitted.Error ?? "accepted"}; {await StampsAsync(legacyPrepared.Prepared!.OperationId)}",
                carv == "OK" && legacyCommitted.Error is null && await StampsAsync(legacyPrepared.Prepared!.OperationId) == "NULL/NULL Committed");
            Record("FX-05", "readiness before the floor: ready without a required floor; not ready when the deployment requires floor 1", "True ...(floor none) | False ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)",
                $"{await GuardAsync(Guard(null))} | {await GuardAsync(Guard(1))}",
                await GuardAsync(Guard(null)) == "True content acceptance rules v1 (floor none)" && await GuardAsync(Guard(1)) == "False ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)");
        }

        // ---------- activation preconditions (all checked inside the locked floor transaction)
        {
            var r = orgA.Records[2];
            var inFlight = await LegacyPrepareAsync(r); // a legacy instance still preparing
            var (code, output) = await FloorCommandAsync();
            Record("FX-06", "activation refused while an unexpired operation prepared by a pre-CARV build is in flight; nothing applied", "exit 3 'in flight'; floor none; no floor history row",
                $"exit {code}: {output}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                code == 3 && output.Contains("in flight", StringComparison.Ordinal) && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
            var reconcile = await Outcome(CommitAsync(rb, r, inFlight.Prepared!, inFlight.Rows));
            Record("FX-07", "reconciliation: the real CARV build aborts the in-flight legacy operation", "OPERATION_RULES_OUTDATED; NULL/NULL Aborted", $"{reconcile}; {await StampsAsync(inFlight.Prepared!.OperationId)}",
                reconcile == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && await StampsAsync(inFlight.Prepared!.OperationId) == "NULL/NULL Aborted");

            string session;
            int sessionCode;
            await using (var legacyLike = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "carv-runtime-LB-idle", Pooling = false }.ConnectionString))
            {
                await legacyLike.OpenAsync(); // an idle pooled connection of a pre-CARV instance (no carv-v marker)
                (sessionCode, session) = await FloorCommandAsync();
            }

            Record("FX-08", "activation refused while a session that can write operations is connected without a CARV build marker", "exit 3 'without a CARV build marker'; floor none",
                $"exit {sessionCode}: {session}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                sessionCode == 3 && session.Contains("without a CARV build marker", StringComparison.Ordinal) && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
        }

        // ---------- atomicity: lock timeout and an interrupted activation apply nothing; a retry succeeds
        {
            await using (var holder = await HoldTableLockAsync())
            {
                var (code, output) = await FloorCommandAsync();
                Record("FX-09", "activation blocked by an open transaction times out (5 s) and applies nothing", "exit 3 55P03; floor none; no floor history row",
                    $"exit {code}: {output}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                    code == 3 && output.Contains("55P03", StringComparison.Ordinal) && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
                await holder.RollbackAsync();
            }

            await using (var holder = await HoldTableLockAsync())
            {
                var command = FloorCommandAsync("carv-floor-interrupted");
                var waited = await WaitForLockWaitAsync("carv-floor-interrupted");
                var killed = await ScalarAsync(adminCs, "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = 'carv-floor-interrupted'");
                var (code, output) = await command;
                Record("FX-10", "an activation interrupted while waiting (backend terminated) applies nothing", "waited; killed 1; non-zero exit; floor none; no floor history row",
                    $"waited={waited}; killed={killed}; exit {code}: {output}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                    waited && killed == "1" && code != 0 && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
                await holder.RollbackAsync();
            }
        }

        // ---------- historical: a legacy instance whose clock lags the database prepared an operation that is already expired in the database
        var lagging = await LegacyPrepareAsync(orgA.Records[3], clockOffsetMinutes: -11);
        Record("FX-11", "setup: a legacy operation prepared with a lagging clock is Prepared, unstamped and past its database expiry", "NULL/NULL Prepared; expired",
            $"{await StampsAsync(lagging.Prepared!.OperationId)}; expired={await ScalarAsync(adminCs, $"SELECT operation_expires_on < now() FROM authority.content_change_operations WHERE content_change_operation_id = '{lagging.Prepared!.OperationId}'")}",
            await StampsAsync(lagging.Prepared!.OperationId) == "NULL/NULL Prepared" && await ScalarAsync(adminCs, $"SELECT operation_expires_on < now() FROM authority.content_change_operations WHERE content_change_operation_id = '{lagging.Prepared!.OperationId}'") == "True");

        // ---------- a legacy Prepare in progress during activation (connected, inside its transaction, waiting on the organization row)
        {
            var r = orgA.Records[2];
            string waitedLegacy;
            (int Code, string Output) activation;
            AgentResult queued;
            var holderConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "carv-org-holder" }.ConnectionString);
            await holderConnection.OpenAsync();
            await using (var holder = await holderConnection.BeginTransactionAsync())
            {
                await using (var lockOrg = new NpgsqlCommand($"SELECT 1 FROM authority.organization_authorization_states WHERE organization_id = '{orgA.Id}' FOR UPDATE", holderConnection, holder))
                {
                    await lockOrg.ExecuteScalarAsync();
                }

                var legacy = LegacyPrepareAsync(r); // its first statement locks the organization row: it waits, connected
                waitedLegacy = (await WaitForLockWaitAsync("carv-runtime-LB")).ToString();
                activation = await FloorCommandAsync("carv-floor-queued");
                await holder.RollbackAsync();
                queued = await legacy;
            }

            await holderConnection.DisposeAsync();
            Record("FX-12a", "a legacy Prepare in progress during activation: the session check under the table lock sees it and refuses activation; the Prepare then completes pre-floor; nothing of the floor is applied", "legacy waited; exit 3 'without a CARV build marker'; legacy NULL/NULL Prepared; floor none",
                $"legacy waited={waitedLegacy}; exit {activation.Code}: {activation.Output}; legacy={queued.Error ?? "prepared " + await StampsAsync(queued.Prepared!.OperationId)}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                waitedLegacy == "True" && activation.Code == 3 && activation.Output.Contains("without a CARV build marker", StringComparison.Ordinal) && queued.Error is null && await StampsAsync(queued.Prepared!.OperationId) == "NULL/NULL Prepared" && await ScalarAsync(adminCs, FloorQuery) == "none" && await FloorHistoryAsync() == 0);
            var reconcile = await Outcome(CommitAsync(rb, r, queued.Prepared!, queued.Rows));
            Record("FX-12b", "reconciliation of that operation by the real CARV build before activation", "OPERATION_RULES_OUTDATED; NULL/NULL Aborted", $"{reconcile}; {await StampsAsync(queued.Prepared!.OperationId)}",
                reconcile == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && await StampsAsync(queued.Prepared!.OperationId) == "NULL/NULL Aborted");
        }

        // ---------- Release B: activation while real CARV instances keep writing
        DateTimeOffset activatedAfter;
        {
            activatedAfter = DateTimeOffset.UtcNow;
            var work = ConcurrentCarvAsync(4, 6, [orgA.Records[4], orgA.Records[5], orgB.Records[0], orgB.Records[1]]);
            await Task.Delay(200);
            var (code, output) = await FloorCommandAsync();
            var result = await work;
            Record("FX-12", "Release B: the explicit floor command activates floor 1 while 4 real CARV instances write concurrently", "exit 0; floor 1 recorded once; writers only OK or CONTENT_REVISION_CONFLICT",
                $"exit {code}: {output}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}; {result.Summary}",
                code == 0 && await ScalarAsync(adminCs, FloorQuery) == "1" && await FloorHistoryAsync() == 1 && result.Unexpected == 0 && result.Ok > 0);
            var definition = await ScalarAsync(adminCs, "SELECT convalidated||' '||pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_content_operation_rule_floor_v1'");
            Record("FX-13", "the installed floor is NOT VALID and covers Prepared and Committed rows with a two-valued test", "false CHECK (... <> ALL (Prepared, Committed) ... IS NOT NULL AND >= 1) NOT VALID", definition,
                definition.StartsWith("false ", StringComparison.Ordinal) && definition.Contains("'Prepared'", StringComparison.Ordinal) && definition.Contains("'Committed'", StringComparison.Ordinal) && definition.Contains("IS NOT NULL", StringComparison.Ordinal) && definition.Contains("NOT VALID", StringComparison.Ordinal));
        }

        // ---------- after the floor: legacy builds can neither prepare nor commit
        {
            var r = orgA.Records[1];
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var prepare = await LegacyPrepareAsync(r);
            Record("FX-14", "a legacy Prepare after the floor is refused by the database; nothing persisted; the legacy host reports 503 (binary rollback is impossible)", "23514 ck_content_operation_rule_floor_v1; no new operation; 503",
                $"{prepare.Error}; {prepare.Mapped?.Split(',')[0]}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}",
                (prepare.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && (prepare.Mapped ?? string.Empty).StartsWith("503", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations"));

            var h = orgA.Records[3];
            var before = await SnapshotAsync(orgA, h);
            var commit = await LegacyCommitAsync(h, lagging.Prepared!, lagging.Rows, clockOffsetMinutes: -11);
            var after = await SnapshotAsync(orgA, h);
            Record("FX-15", "the lagging-clock legacy instance commits its pre-floor operation after the floor: refused by the database; nothing advances", "23514 ck_content_operation_rule_floor_v1; snapshot unchanged; NULL/NULL Prepared",
                $"{commit.Error}; {Diff(before, after)}; {await StampsAsync(lagging.Prepared!.OperationId)}",
                (commit.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && before == after && await StampsAsync(lagging.Prepared!.OperationId) == "NULL/NULL Prepared");

            var acceptedLegacy = await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version IS NULL AND operation_committed_on >= '{activatedAfter:O}'");
            Record("FX-16", "no legacy-prepared operation reached Committed after activation (old->old closed)", "0", acceptedLegacy.ToString(System.Globalization.CultureInfo.InvariantCulture), acceptedLegacy == 0);

            var carv = await WriteAsync(rb, orgA.Records[0]);
            Record("FX-17", "a real CARV write after the floor commits", "1/1 Committed", await StampsAsync(carv), await StampsAsync(carv) == "1/1 Committed");
        }

        // ---------- version mismatches and forged rows (runtime-role SQL, the strongest non-Authority writer)
        {
            var (template, _) = await PrepareWithRowsAsync(rb, orgA.Records[0]);
            var copy = (string id, string state, string prepare, string commit) => $"INSERT INTO authority.content_change_operations (content_change_operation_id, organization_id, form_record_id, actor_identity_binding_id, operation_idempotency_key, operation_request_digest, expected_authorization_revision, expected_content_revision, candidate_content_revision, candidate_key_version, candidate_manifest_root, candidate_leaf_index_digest, candidate_leaf_count, operation_state_name, correlation_identifier, operation_created_on, operation_expires_on, prepare_acceptance_rule_version, commit_acceptance_rule_version) SELECT '{id}', organization_id, form_record_id, actor_identity_binding_id, 'forged-'||gen_random_uuid(), operation_request_digest, expected_authorization_revision, expected_content_revision, candidate_content_revision, candidate_key_version, candidate_manifest_root, candidate_leaf_index_digest, candidate_leaf_count, '{state}', correlation_identifier, now(), now() + interval '10 minutes', {prepare}, {commit} FROM authority.content_change_operations WHERE content_change_operation_id = '{template.OperationId}'";
            var unstamped = await ExecAsync(rawRuntime, copy(Guid.CreateVersion7().ToString(), "Prepared", "NULL", "NULL"));
            var zero = await ExecAsync(rawRuntime, copy(Guid.CreateVersion7().ToString(), "Prepared", "0", "NULL"));
            var mismatch = await ExecAsync(rawRuntime, $"UPDATE authority.content_change_operations SET operation_state_name = 'Committed', commit_acceptance_rule_version = 2 WHERE content_change_operation_id = '{template.OperationId}'");
            Record("FX-18", "forged Prepared rows without an acceptable stamp, and a commit at another rule version, are refused by the database", "23514 floor_v1 | 23514 versions_positive | 23514 commit_rule_version",
                $"{unstamped} | {zero} | {mismatch}",
                unstamped.Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && zero.StartsWith("23514", StringComparison.Ordinal) && mismatch.Contains("ck_content_operation_commit_rule_version", StringComparison.Ordinal));

            var newer = Guid.CreateVersion7();
            var inserted = await ExecAsync(rawRuntime, copy(newer.ToString(), "Prepared", "2", "NULL")); // as a hypothetical rule-version-2 build would stamp it
            var committed = await Outcome(rb.Service.CommitAsync(Actor(orgA), newer, new CommitContentChangeRequest(orgA.Id, orgA.Records[0].Id, []), Guid.CreateVersion7(), CancellationToken.None));
            Record("FX-19", "an operation stamped by a newer rule version is refused by the v1 build's Commit rule and aborted", "OK insert; OPERATION_RULES_OUTDATED; 2/NULL Aborted",
                $"{inserted}; {committed}; {await StampsAsync(newer)}", inserted == "OK" && committed == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && await StampsAsync(newer) == "2/NULL Aborted");
        }

        // ---------- historical operations cannot bypass the floor
        {
            var historicalCommitted = await ScalarAsync(adminCs, "SELECT content_change_operation_id::text FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version IS NULL ORDER BY operation_created_on LIMIT 1");
            var historicalAborted = await ScalarAsync(adminCs, "SELECT content_change_operation_id::text FROM authority.content_change_operations WHERE operation_state_name = 'Aborted' AND prepare_acceptance_rule_version IS NULL ORDER BY operation_created_on LIMIT 1");
            var reopenCommitted = await ExecAsync(rawRuntime, $"UPDATE authority.content_change_operations SET operation_state_name = 'Prepared' WHERE content_change_operation_id = '{historicalCommitted}'");
            var reviveAborted = await ExecAsync(rawRuntime, $"UPDATE authority.content_change_operations SET operation_state_name = 'Committed' WHERE content_change_operation_id = '{historicalAborted}'");
            var touchPrepared = await ExecAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_expires_on = now() + interval '10 minutes' WHERE content_change_operation_id = '{lagging.Prepared!.OperationId}'");
            Record("FX-20", "historical unstamped rows: a Committed row cannot be reopened, an Aborted row cannot be revived, and even a superuser update of an unstamped Prepared row now violates the floor", "23514 state guard | 23514 state guard | 23514 floor_v1",
                $"{reopenCommitted} | {reviveAborted} | {touchPrepared}",
                reopenCommitted.Contains("trg_content_operation_rule_state_guard", StringComparison.Ordinal) && reviveAborted.Contains("trg_content_operation_rule_state_guard", StringComparison.Ordinal) && touchPrepared.Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal));
            var replay = await Outcome(rb.Service.CommitAsync(Actor(orgB), orgB.Records[0].LastOperation!.Value, new CommitContentChangeRequest(orgB.Id, orgB.Records[0].Id, orgB.Records[0].AcceptedStaged()), Guid.CreateVersion7(), CancellationToken.None));
            Record("FX-21", "an accepted operation still replays after the floor (idempotent Commit replay)", "OK", replay, replay == "OK");
        }

        // ---------- monotonic: re-run is a no-op; ordinary --migrate keeps it; no Down; the runtime role cannot touch it
        {
            var (code, output) = await FloorCommandAsync();
            var migrate = await RealMigrateAsync("carv-migration");
            Record("FX-22", "re-running the floor command is a no-op and the ordinary --migrate keeps the floor", "exit 0; OK; floor 1; floor history 1",
                $"exit {code}: {output}; {migrate}; floor={await ScalarAsync(adminCs, FloorQuery)}; history={await FloorHistoryAsync()}",
                code == 0 && migrate == "OK" && await ScalarAsync(adminCs, FloorQuery) == "1" && await FloorHistoryAsync() == 1);

            string down;
            try
            {
                await using var floorContext = new ContentAcceptanceFloorContext(ContentAcceptanceFloorContext.OptionsFor(adminCs));
                await floorContext.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("0");
                down = "reverted!";
            }
            catch (PostgresException ex)
            {
                down = $"{ex.SqlState} : {ex.MessageText}";
            }

            var carvDown = await ExecScriptAsync(adminCs, "real_down.sql", "carv-down");
            var drop = await ExecAsync(rawRuntime, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1");
            Record("FX-23", "the floor migration's Down refuses, the E3-M2b-1 Down refuses while a floor exists, and the runtime role cannot drop the floor", "P0001 never lowered | P0001 floors are never lowered | 42501",
                $"{down} | {carvDown} | {drop}; floor={await ScalarAsync(adminCs, FloorQuery)}",
                down.Contains("never lowered", StringComparison.Ordinal) && carvDown.Contains("floors are never lowered", StringComparison.Ordinal) && drop.StartsWith("42501", StringComparison.Ordinal) && await ScalarAsync(adminCs, FloorQuery) == "1");
            Record("FX-24", "readiness with the floor installed: ready, with or without a required floor 1", "True ...(floor 1) | True ...(floor 1); phase=ReleaseB requiredFloor=1", $"{await GuardAsync(Guard(null))} | {await GuardAsync(Guard(1))}",
                await GuardAsync(Guard(null)) == "True content acceptance rules v1 (floor 1)" && await GuardAsync(Guard(1)) == "True content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1"); // RB-CARV-1: a Release B ready detail names its phase
        }

        // ---------- recovery: a missing floor is detected and can only be re-installed (never lowered)
        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1"); // simulated privileged drop
            var detected = await GuardAsync(Guard(null));
            var (stopCode, stopOutput) = await FloorCommandAsync();
            var (reinstateCode, reinstateOutput) = await FloorCommandAsync(extra: ContentAcceptanceFloorCommand.ReinstateFlag);
            Record("FX-25", "a dropped floor whose history row remains: detected (not ready), the command stops (exit 4), and the explicit reinstatement re-installs it", "False FloorMissing | exit 4 | exit 0; floor 1",
                $"{detected} | exit {stopCode}: {stopOutput} | exit {reinstateCode}: {reinstateOutput}; floor={await ScalarAsync(adminCs, FloorQuery)}; ready={await GuardAsync(Guard(1))}",
                detected.StartsWith("False ContentAcceptanceRuleFloorMissing", StringComparison.Ordinal) && stopCode == 4 && reinstateCode == 0 && await ScalarAsync(adminCs, FloorQuery) == "1" && (await GuardAsync(Guard(1))).StartsWith("True", StringComparison.Ordinal));

            // A restore to before the floor removes both the constraint and its history row.
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1");
            await ExecAsync(adminCs, $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{FloorMigrationId}'");
            var unconfigured = await GuardAsync(Guard(null));
            var configured = await GuardAsync(Guard(1));
            var (reapplyCode, reapplyOutput) = await FloorCommandAsync();
            Record("FX-26", "a restore that removed floor and history: undetectable without configuration, detected with RequiredFloor = 1; the floor command re-applies it", "True ...(floor none) | False FloorBelowRequired | exit 0; floor 1; ready",
                $"{unconfigured} | {configured} | exit {reapplyCode}: {reapplyOutput}; floor={await ScalarAsync(adminCs, FloorQuery)}; ready={await GuardAsync(Guard(1))}",
                unconfigured == "True content acceptance rules v1 (floor none)" && configured == "False ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)" && reapplyCode == 0 && await ScalarAsync(adminCs, FloorQuery) == "1" && (await GuardAsync(Guard(1))).StartsWith("True", StringComparison.Ordinal));

            var legacyAgain = await LegacyPrepareAsync(orgA.Records[1]);
            Record("FX-27", "after re-application, the legacy build is refused again", "23514 ck_content_operation_rule_floor_v1", legacyAgain.Error ?? "accepted!", (legacyAgain.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal));
        }

        Record("FINAL", "final operation table summary", "informational", await OpsSummaryAsync() + $"; legacy agent calls={agentCalls}", true);
    }

    private async Task<long> FloorHistoryAsync() => await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{FloorMigrationId}'");

    /// <summary>The real build's own floor command, run as the migration identity (a separate process, as an operator would).</summary>
    private async Task<(int Code, string Output)> FloorCommandAsync(string application = "carv-floor-command", string? extra = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(newBinary);
        start.ArgumentList.Add("--apply-content-acceptance-floor");
        start.ArgumentList.Add("1");
        if (extra is not null)
        {
            start.ArgumentList.Add(extra);
        }

        start.Environment["ConnectionStrings__AuthorityDbMigration"] = new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = application }.ConnectionString;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the floor command");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = ((await stdout) + " " + (await stderr)).Replace('\n', ' ').Replace('\r', ' ').Trim();
        Log($"floor command ({application}) exit {process.ExitCode}: {output}");
        return (process.ExitCode, output.Length > 400 ? output[..400] : output);
    }

    /// <summary>An admin transaction holding ACCESS SHARE on content_change_operations (as any open Prepare or Commit does).</summary>
    private async Task<NpgsqlTransaction> HoldTableLockAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "carv-lock-holder" }.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("LOCK TABLE authority.content_change_operations IN ACCESS SHARE MODE", connection, transaction);
        await command.ExecuteNonQueryAsync();
        return transaction;
    }

    private async Task<bool> WaitForLockWaitAsync(string application)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await CountAsync($"SELECT count(*) FROM pg_stat_activity WHERE application_name = '{application}' AND wait_event_type = 'Lock'") > 0)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
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
        var store = new AuthorityStore(Factory.For(cs), ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseAName, null), null, null, alerts); // RB-CARV-1: this harness runs Release A instances (unchanged behaviour)
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
    private async Task<AgentResult> LegacyPrepareAsync(RecordModel record, int clockOffsetMinutes = 0)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation);
        return await LegacyPrepareRequestAsync(record, request, rows, "carv-legacy-" + operation.ToString("N"), clockOffsetMinutes);
    }

    private async Task<AgentResult> LegacyPrepareRequestAsync(RecordModel record, PrepareContentChangeRequest request, List<Staged> rows, string key, int clockOffsetMinutes = 0)
    {
        var output = await AgentAsync("prepare", new { Issuer, Subject = record.Org.Owner.ToString("D"), Key = key, Request = request }, clockOffsetMinutes);
        var prepared = output.GetProperty("Ok").GetBoolean() ? output.GetProperty("Result").Deserialize<PreparedContentOperation>() : null;
        return new AgentResult(prepared, rows, output.GetProperty("Error").GetString(), output.GetProperty("Mapped").GetString());
    }

    private async Task<AgentResult> LegacyCommitAsync(RecordModel record, PreparedContentOperation prepared, List<Staged> rows, int clockOffsetMinutes = 0)
    {
        var output = await AgentAsync("commit", new { Issuer, Subject = record.Org.Owner.ToString("D"), OperationId = prepared.OperationId, Request = new CommitContentChangeRequest(record.Org.Id, record.Id, Staged(prepared, rows)) }, clockOffsetMinutes);
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

    private async Task<JsonElement> AgentAsync(string command, object input, int clockOffsetMinutes = 0)
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
            start.Environment["CARV_CLOCK_OFFSET_MINUTES"] = clockOffsetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
