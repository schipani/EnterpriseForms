// RB-CARV-1 gate, harness R (Development/Testing only; built by scripts/dev/e3-m2b1-carv-pg-gate.ps1, never in place).
// The Release B content write fence of the REAL build (ContentAcceptancePolicy, AuthorityStore, ContentAcceptanceWriteFence,
// ContentService, the readiness guard and the floor command) against a disposable PostgreSQL 18 container, together with a
// frozen LEGACY build driven through the legacy agent child process (..\agent-<commit>). Scenarios A..H of the owner's RB-CARV-1
// authorization (contract §22). Derived from harness F (same helpers and fixtures). "RA" = a Release A instance of the real
// build, "RB" = the long-running Release B instance (one object for the whole run), "LB" = the legacy binary.
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
            harness = "RB-CARV-1 gate, harness R (Release B write fence of the real build + real floor command + legacy agent)",
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
    // RB-CARV-1 (contract §22): the Release B content write fence of the real build (ContentAcceptancePolicy + AuthorityStore +
    // ContentAcceptanceWriteFence), against real PostgreSQL, the real floor command and a frozen legacy build. Scenarios A..H of
    // the owner's authorization; "RA" = a Release A instance, "RB" = the long-running Release B instance (never restarted).
    private const string FloorMigrationId = "20261115032228_AuthorityContentAcceptanceRuleFloor1";
    private const string DropFloor = "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1";
    private const string Refused = "AuthorityException CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED 503 retryable=False";
    private static readonly ContentAcceptancePolicy ReleaseA = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseAName, null);
    private static readonly ContentAcceptancePolicy ReleaseB = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "1");

    private string markedRuntime = string.Empty;

    private async Task ScenariosAsync()
    {
        var orgA = await SeedOrgAsync("A", 8);
        var orgB = await SeedOrgAsync("B", 2);
        markedRuntime = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "carv-guard" }.ConnectionString)!;
        foreach (var record in orgA.Records.Concat(orgB.Records))
        {
            await LegacyWriteAsync(record); // pre-CARV history written by the legacy build on its own schema
        }

        var migrated = await RealMigrateAsync("carv-migration");
        Record("RX-00", "setup: legacy history, then this build's ordinary --migrate applies E3-M2b-1 (never the floor)", "OK; floor none", $"{migrated}; floor={await FloorAsync()}",
            migrated == "OK" && await FloorAsync() == "none");
        Record("RX-00a", "setup: commit timestamps are tracked (scenario H orders commits against the floor removal)", "on", await ScalarAsync(adminCs, "SHOW track_commit_timestamp"),
            await ScalarAsync(adminCs, "SHOW track_commit_timestamp") == "on");

        var ra = Instance("RA", runtimeCs, ReleaseA);
        var rb = Instance("RB", runtimeCs, ReleaseB); // scenario G: this one instance runs through every scenario below
        var guardA = Guard(ReleaseA);
        var guardB = Guard(ReleaseB);

        // ---------- Scenario A: Release A, no floor
        AgentResult lagging;
        {
            var carv = await WriteAsync(ra, orgA.Records[0]);
            var legacy = await LegacyPrepareAsync(orgA.Records[1]);
            var legacyCommit = legacy.Error is null ? await LegacyCommitAsync(orgA.Records[1], legacy.Prepared!, legacy.Rows) : legacy;
            Record("RX-A1", "Release A, no floor: a CARV write commits; legacy compatibility is unchanged (a legacy Prepare and Commit are still accepted before the floor)", "1/1 Committed; legacy NULL/NULL Committed",
                $"{await StampsAsync(carv)}; legacy={legacyCommit.Error ?? await StampsAsync(legacy.Prepared!.OperationId)}",
                await StampsAsync(carv) == "1/1 Committed" && legacyCommit.Error is null && await StampsAsync(legacy.Prepared!.OperationId) == "NULL/NULL Committed");
            var a = await GuardAsync(guardA);
            var b = await GuardAsync(guardB);
            Record("RX-A2", "readiness without a floor: Release A ready (E3-M2b-1 detail unchanged); Release B not ready", "True ...(floor none) | False FloorBelowRequired", $"{a} | {b}",
                a == "True content acceptance rules v1 (floor none)" && b == "False ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)");
            var refused = await RefusedWriteAsync(rb, orgA, orgA.Records[2]);
            Record("RX-A3", "a Release B instance before activation refuses content writes; nothing persisted; alert reason FloorNotInstalled (MissingFloor)", $"{Refused}; unchanged; reason=FloorNotInstalled category=MissingFloor", refused.Observed,
                refused.Outcome == Refused && refused.Unchanged && refused.Alert.Contains("reason=FloorNotInstalled category=MissingFloor recovery=RecoveryRequired:ApplyFloor", StringComparison.Ordinal));
            lagging = await LegacyPrepareAsync(orgA.Records[3], clockOffsetMinutes: -11); // for RX-B4: a pre-floor legacy operation, already expired in the database
            var snapshot = await SnapshotDatabaseAsync("carv_prefloor"); // for scenario D: the database as it was before floor activation
            Record("RX-A4", "setup: a lagging-clock legacy operation (expired in the database) and a pre-floor database snapshot for scenario D", "NULL/NULL Prepared; snapshot OK",
                $"{await StampsAsync(lagging.Prepared!.OperationId)}; snapshot {snapshot}", await StampsAsync(lagging.Prepared!.OperationId) == "NULL/NULL Prepared" && snapshot == "OK");
        }

        // ---------- Scenario B: Release B, floor 1 installed by the explicit command
        {
            var (code, output) = await FloorCommandAsync();
            var verdict = await LiveVerdictAsync();
            Record("RX-B1", "the explicit floor command activates floor 1; the live catalog matches the write fence's expected PostgreSQL 18 definitions exactly", "exit 0; floor 1; verdict none",
                $"exit {code}: {output}; floor={await FloorAsync()}; verdict={verdict}", code == 0 && await FloorAsync() == "1" && verdict == "none");
            var carv = await WriteAsync(rb, orgA.Records[0]);
            var ready = await GuardAsync(guardB);
            Record("RX-B2", "Release B with the floor: a CARV write commits; readiness ready and names the phase", "1/1 Committed | True ...(floor 1); phase=ReleaseB requiredFloor=1",
                $"{await StampsAsync(carv)} | {ready}", await StampsAsync(carv) == "1/1 Committed" && ready == "True content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1");
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var prepare = await LegacyPrepareAsync(orgA.Records[1]);
            Record("RX-B3", "Release B: a legacy Prepare is refused by the database floor; nothing persisted; the legacy host reports 503", "23514 ck_content_operation_rule_floor_v1; 503; ops unchanged",
                $"{prepare.Error}; {prepare.Mapped?.Split(',')[0]}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}",
                (prepare.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && (prepare.Mapped ?? string.Empty).StartsWith("503", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations"));
            var h = orgA.Records[3];
            var before = await SnapshotAsync(orgA, h);
            var commit = await LegacyCommitAsync(h, lagging.Prepared!, lagging.Rows, clockOffsetMinutes: -11);
            var after = await SnapshotAsync(orgA, h);
            Record("RX-B4", "Release B: a legacy Commit of a pre-floor operation (lagging clock) is refused by the floor; nothing advances", "23514 ck_content_operation_rule_floor_v1; snapshot unchanged; NULL/NULL Prepared",
                $"{commit.Error}; {Diff(before, after)}; {await StampsAsync(lagging.Prepared!.OperationId)}",
                (commit.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal) && before == after && await StampsAsync(lagging.Prepared!.OperationId) == "NULL/NULL Prepared");
            var raWrite = await WriteAsync(ra, orgA.Records[4]);
            Record("RX-B5", "a Release A instance with the floor installed still writes (Release A semantics unchanged)", "1/1 Committed", await StampsAsync(raWrite), await StampsAsync(raWrite) == "1/1 Committed");
        }

        // ---------- Scenarios G and C: the floor removed under the running Release B instance
        {
            var r = orgA.Records[5];
            var (inFlight, inFlightRows, inFlightRequest, inFlightKey) = await PrepareKeepingRequestAsync(rb, r);
            var cachedBefore = await GuardAsync(guardB);
            await ExecAsync(adminCs, DropFloor); // privileged removal
            var refused = await RefusedWriteAsync(rb, orgA, orgA.Records[0]);
            var cachedAfter = guardB.Current; // not refreshed: the write path never consults it
            Record("RX-G1", "long-running Release B instance: the floor removed by a privileged connection; the very next write is refused without a restart or readiness refresh (the cached readiness still says ready); alert FloorRecordedButMissing",
                $"cached ready before and after; {Refused}; unchanged; reason=FloorRecordedButMissing", $"cached {cachedBefore} / {cachedAfter.Ready}; {refused.Observed}",
                cachedBefore.StartsWith("True", StringComparison.Ordinal) && cachedAfter.Ready && refused.Outcome == Refused && refused.Unchanged && refused.Alert.Contains("reason=FloorRecordedButMissing category=MissingFloor recovery=RecoveryRequired:ReinstateRecordedFloor", StringComparison.Ordinal));

            var before = await SnapshotAsync(orgA, r);
            var commit = await Outcome(CommitAsync(rb, r, inFlight, inFlightRows));
            var replayPrepare = await Outcome(rb.Service.PrepareAsync(Actor(orgA), inFlightRequest, inFlightKey, Guid.CreateVersion7(), CancellationToken.None));
            var replayCommit = await Outcome(rb.Service.CommitAsync(Actor(orgA), orgA.Records[0].LastOperation!.Value, new CommitContentChangeRequest(orgA.Id, orgA.Records[0].Id, orgA.Records[0].AcceptedStaged()), Guid.CreateVersion7(), CancellationToken.None));
            var after = await SnapshotAsync(orgA, r);
            Record("RX-C1", "with the floor removed: the in-flight Release B operation cannot commit, its Prepare replay and an accepted operation's Commit replay are refused; nothing advances; the operation stays Prepared",
                $"{Refused} x3; unchanged; 1/NULL Prepared", $"{commit} | {replayPrepare} | {replayCommit}; {Diff(before, after)}; {await StampsAsync(inFlight.OperationId)}",
                commit == Refused && replayPrepare == Refused && replayCommit == Refused && before == after && await StampsAsync(inFlight.OperationId) == "1/NULL Prepared");
            var detected = await GuardAsync(guardB);
            Record("RX-C2", "readiness detects the removal on its next refresh", "False ContentAcceptanceRuleFloorMissing", detected, detected.StartsWith("False ContentAcceptanceRuleFloorMissing", StringComparison.Ordinal));

            var legacy = await LegacyPrepareAsync(orgA.Records[6]);
            var (blockedCode, blockedOutput) = await FloorCommandAsync(extra: ContentAcceptanceFloorCommand.ReinstateFlag);
            Record("RX-C3", "while the floor is missing a legacy Prepare is accepted again (residual R-CARV-21: only the floor stops legacy builds); the reinstatement refuses until it is reconciled",
                "legacy NULL/NULL Prepared | exit 3 in flight", $"legacy {legacy.Error ?? await StampsAsync(legacy.Prepared!.OperationId)} | exit {blockedCode}: {blockedOutput}",
                legacy.Error is null && await StampsAsync(legacy.Prepared!.OperationId) == "NULL/NULL Prepared" && blockedCode == 3 && blockedOutput.Contains("in flight", StringComparison.Ordinal));

            var reconcile = await Outcome(CommitAsync(ra, orgA.Records[6], legacy.Prepared!, legacy.Rows)); // a CARV build aborts it
            var (stopCode, stopOutput) = await FloorCommandAsync();
            var (reCode, reOutput) = await FloorCommandAsync(extra: ContentAcceptanceFloorCommand.ReinstateFlag);
            Record("RX-C4", "recovery through the approved command only (migration identity): reconciliation by a CARV build; exit 4 without the reinstatement flag; exit 0 with it; floor 1; live verdict none",
                "OPERATION_RULES_OUTDATED | exit 4 | exit 0; floor 1; none", $"{reconcile} | exit {stopCode}: {stopOutput} | exit {reCode}: {reOutput}; floor={await FloorAsync()}; verdict={await LiveVerdictAsync()}",
                reconcile == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && stopCode == 4 && reCode == 0 && await FloorAsync() == "1" && await LiveVerdictAsync() == "none");
            var resumed = await Outcome(CommitAsync(rb, r, inFlight, inFlightRows));
            Record("RX-C5", "writes resume only after the floor is verified again: the same in-flight operation now commits", "OK; 1/1 Committed", $"{resumed}; {await StampsAsync(inFlight.OperationId)}",
                resumed == "OK" && await StampsAsync(inFlight.OperationId) == "1/1 Committed");
        }

        // ---------- Scenario C (continued): the floor or the fence replaced, disabled or weakened (each reversed afterwards)
        {
            var record = orgA.Records[0];
            const string Ops = "authority.content_change_operations";
            await WeakenedAsync("RX-C6", "the floor replaced by a same-named Prepared-only floor (D-2)", rb, orgA, record,
                $"ALTER TABLE {Ops} DROP CONSTRAINT ck_content_operation_rule_floor_v1; ALTER TABLE {Ops} ADD CONSTRAINT ck_content_operation_rule_floor_v1 CHECK (operation_state_name <> 'Prepared' OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID",
                ContentAcceptanceFloorFailures.FloorDefinitionMismatch, restoreFloor: true);
            await WeakenedAsync("RX-C7", "the floor replaced without its IS NOT NULL term (D-1)", rb, orgA, record,
                $"ALTER TABLE {Ops} DROP CONSTRAINT ck_content_operation_rule_floor_v1; ALTER TABLE {Ops} ADD CONSTRAINT ck_content_operation_rule_floor_v1 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR prepare_acceptance_rule_version >= 1) NOT VALID",
                ContentAcceptanceFloorFailures.FloorDefinitionMismatch, restoreFloor: true);
            await WeakenedAsync("RX-C8", "the state guard trigger disabled", rb, orgA, record,
                $"ALTER TABLE {Ops} DISABLE TRIGGER trg_content_operation_rule_state_guard", ContentAcceptanceFloorFailures.FenceStateGuardMismatch,
                restore: $"ALTER TABLE {Ops} ENABLE TRIGGER trg_content_operation_rule_state_guard");
            await WeakenedAsync("RX-C9", "the state guard function replaced by a no-op (owner privilege)", rb, orgA, record,
                "CREATE OR REPLACE FUNCTION authority.content_operation_rule_state_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$ BEGIN RETURN NEW; END $$",
                ContentAcceptanceFloorFailures.FenceStateGuardMismatch,
                restore: "CREATE OR REPLACE FUNCTION authority.content_operation_rule_state_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$" + ContentAcceptanceWriteFence.ExpectedStateGuardSource + "$$");
            await WeakenedAsync("RX-C10", "the E3-M2b-1 commit CHECK dropped", rb, orgA, record,
                $"ALTER TABLE {Ops} DROP CONSTRAINT ck_content_operation_commit_rule_version", ContentAcceptanceFloorFailures.FenceCommitCheckMismatch,
                restore: $"ALTER TABLE {Ops} ADD CONSTRAINT ck_content_operation_commit_rule_version CHECK ({ContentAcceptanceRuleSql.CommitCheckSql})");
            await WeakenedAsync("RX-C11", "an extra BEFORE trigger on the operations table", rb, orgA, record,
                $"CREATE FUNCTION authority.rb_extra_trigger() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; CREATE TRIGGER rb_extra BEFORE INSERT ON {Ops} FOR EACH ROW EXECUTE FUNCTION authority.rb_extra_trigger()",
                ContentAcceptanceFloorFailures.FenceUnexpectedTableObject, restore: $"DROP TRIGGER rb_extra ON {Ops}; DROP FUNCTION authority.rb_extra_trigger()");
            await WeakenedAsync("RX-C12", "a rewrite rule on the operations table", rb, orgA, record,
                $"CREATE RULE rb_rule AS ON INSERT TO {Ops} DO ALSO NOTIFY rb_rule_channel", ContentAcceptanceFloorFailures.FenceUnexpectedTableObject, restore: $"DROP RULE rb_rule ON {Ops}");

            // A column default would let a legacy Prepare pass the floor with a stamp it never applied: shown, then refused by the fence.
            await ExecAsync(adminCs, $"ALTER TABLE {Ops} ALTER COLUMN prepare_acceptance_rule_version SET DEFAULT 1");
            var stamped = await LegacyPrepareAsync(orgA.Records[7]);
            var stampedState = stamped.Error ?? await StampsAsync(stamped.Prepared!.OperationId);
            var defaultRefused = await RefusedWriteAsync(rb, orgA, record);
            await ExecAsync(adminCs, $"ALTER TABLE {Ops} ALTER COLUMN prepare_acceptance_rule_version DROP DEFAULT");
            await ExecAsync(adminCs, $"UPDATE {Ops} SET operation_state_name = 'Aborted', operation_quarantine_reason_code = 'RB_TEST_DEFAULT_STAMP' WHERE content_change_operation_id = '{stamped.Prepared?.OperationId}'");
            var defaultRestored = await Outcome(WriteAsync(rb, record));
            Record("RX-C13", "a DEFAULT on the Prepare stamp lets a legacy Prepare pass the floor stamped 1 (why the fence checks defaults); the Release B write fence refuses (FenceStampDefaultPresent); writes resume once dropped",
                "legacy 1/NULL Prepared | refused FenceStampDefaultPresent | OK", $"legacy {stampedState} | {defaultRefused.Observed} | {defaultRestored}",
                stampedState == "1/NULL Prepared" && defaultRefused.Outcome == Refused && defaultRefused.Unchanged && defaultRefused.Alert.Contains("reason=FenceStampDefaultPresent category=WeakenedFence", StringComparison.Ordinal) && defaultRestored == "OK");
        }

        // ---------- Scenario F: metadata unavailable, connection interrupted, inconsistent history
        {
            var record = orgA.Records[0];
            var runtimeRole = new NpgsqlConnectionStringBuilder(runtimeCs).Username;
            await WeakenedAsync("RX-F1", "metadata access denied: SELECT on the migration history revoked from the runtime role", rb, orgA, record,
                $"REVOKE SELECT ON authority.ef_migrations_history FROM {runtimeRole}", ContentAcceptanceFloorFailures.FloorUnverifiable,
                restore: $"GRANT SELECT ON authority.ef_migrations_history TO {runtimeRole}");
            await WeakenedAsync("RX-F2", "catalog access denied: SELECT on pg_catalog.pg_constraint revoked from PUBLIC", rb, orgA, record,
                "REVOKE SELECT ON pg_catalog.pg_constraint FROM PUBLIC", ContentAcceptanceFloorFailures.FloorUnverifiable, restore: "GRANT SELECT ON pg_catalog.pg_constraint TO PUBLIC");
            var product = await ScalarAsync(adminCs, $"SELECT \"ProductVersion\" FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{FloorMigrationId}'");
            await WeakenedAsync("RX-F3", "inconsistent history: the floor constraint present but its history row deleted", rb, orgA, record,
                $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{FloorMigrationId}'", ContentAcceptanceFloorFailures.FloorNotRecorded,
                restore: $"INSERT INTO authority.ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('{FloorMigrationId}', '{product}')");
            await WeakenedAsync("RX-F4", "inconsistent history: the E3-M2b-1 history row deleted", rb, orgA, record,
                $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'", ContentAcceptanceFloorFailures.FenceMigrationNotRecorded,
                restore: $"INSERT INTO authority.ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('{CarvMigrationId}', '{product}')");

            // The connection interrupted while the write waits for its table lock (an ACCESS EXCLUSIVE holder keeps it waiting).
            var before = await SnapshotAsync(orgA, record);
            string interrupted;
            string killed;
            await using (var holder = await HoldTableLockAsync("ACCESS EXCLUSIVE"))
            {
                var write = Outcome(WriteAsync(rb, record));
                var waited = await WaitForLockWaitLikeAsync("carv-runtime-RB %");
                killed = waited ? await ScalarAsync(adminCs, "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name LIKE 'carv-runtime-RB %' AND wait_event_type = 'Lock'") : "not waiting";
                await holder.RollbackAsync();
                interrupted = await write;
            }

            NpgsqlConnection.ClearAllPools();
            var after = await SnapshotAsync(orgA, record);
            var next = await Outcome(WriteAsync(rb, record));
            Record("RX-F5", "a write whose connection is terminated while it waits for the fence lock fails without writing; the next write succeeds", "killed 1; not OK; unchanged; next OK",
                $"killed {killed}; {interrupted}; {Diff(before, after)}; next {next}", killed == "1" && interrupted != "OK" && before == after && next == "OK");
        }

        // ---------- Scenario E: invalid configuration never becomes Release A (store level; host start: contract tests)
        {
            var results = new List<string>();
            var allRefused = true;
            foreach (var (phase, floor) in new (string?, string?)[] { (null, null), ("Unconfigured", null), ("ReleaseB", null), ("ReleaseB", "0"), ("ReleaseB", "2"), ("releaseb", "1"), ("ReleaseA", "1") })
            {
                var policy = ContentAcceptancePolicy.Resolve(phase, floor);
                var instance = Instance("RX-" + results.Count, runtimeCs, policy);
                var refused = await RefusedWriteAsync(instance, orgA, orgA.Records[0]);
                results.Add($"{phase ?? "<absent>"}/{floor ?? "<absent>"} -> {policy.ConfigurationFailure} {(refused.Outcome == Refused && refused.Unchanged ? "refused" : "NOT REFUSED " + refused.Observed)}");
                allRefused &= refused.Outcome == Refused && refused.Unchanged && refused.Alert.Contains("reason=ConfigurationPolicyInvalid category=ConfigurationPolicyFailure", StringComparison.Ordinal);
            }

            Record("RX-E1", "missing, malformed, unsupported and downgraded configurations refuse every content write even though the floor is fully enforced; none behaves as Release A", "7 refused (ConfigurationPolicyInvalid)",
                string.Join("; ", results), allRefused && results.Count == 7);
        }

        // ---------- secondary paths: the store's catch-all, with no explicit fence call (as a forgotten path would)
        {
            var record = orgB.Records[0];
            var (aborting, _, _, _) = await PrepareKeepingRequestAsync(rb, record);
            await ExecAsync(adminCs, DropFloor);
            var headId = Guid.CreateVersion7();
            var direct = await Outcome(DirectAsync(rb, orgB, scope =>
            {
                scope.AddContentHead(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = orgB.Id, FormRecordId = headId, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1, ContentHeadModifiedOn = DateTimeOffset.UtcNow });
                return Task.CompletedTask;
            }));
            Record("RX-S1", "a scope of the Release B store that writes a content head without calling the fence (a forgotten path) is refused by the store before SaveChanges; nothing inserted", $"{Refused}; 0 heads",
                $"{direct}; heads={await CountAsync($"SELECT count(*) FROM authority.record_content_heads WHERE form_record_id = '{headId}'")}",
                direct == Refused && await CountAsync($"SELECT count(*) FROM authority.record_content_heads WHERE form_record_id = '{headId}'") == 0);
            var revisionBefore = (await HeadAsync(record)).Revision;
            var advance = await Outcome(DirectAsync(rb, orgB, async scope =>
            {
                var head = await scope.FindContentHeadAsync(orgB.Id, record.Id, lockForUpdate: true, CancellationToken.None);
                head!.CommittedContentRevision += 1;
            }));
            Record("RX-S2", "a direct head advance without the fence is refused; the head is unchanged", $"{Refused}; revision unchanged", $"{advance}; revision {revisionBefore}->{(await HeadAsync(record)).Revision}",
                advance == Refused && (await HeadAsync(record)).Revision == revisionBefore);
            var abort = await Outcome(DirectAsync(rb, orgB, async scope =>
            {
                var operation = await scope.FindOperationAsync(aborting.OperationId, CancellationToken.None);
                operation!.OperationStateName = ContentOperationStates.Aborted;
                operation.OperationQuarantineReasonCode = "RB_TEST_PROTECTIVE_ABORT";
            }));
            Record("RX-S3", "the narrow protective exemption: aborting a Prepared operation still commits while the floor is missing (never an acceptance)", "OK; 1/NULL Aborted", $"{abort}; {await StampsAsync(aborting.OperationId)}",
                abort == "OK" && await StampsAsync(aborting.OperationId) == "1/NULL Aborted");
            var (reCode, reOutput) = await FloorCommandAsync(extra: ContentAcceptanceFloorCommand.ReinstateFlag);
            Record("RX-S4", "the floor reinstated by the approved command", "exit 0; floor 1", $"exit {reCode}: {reOutput}; floor={await FloorAsync()}", reCode == 0 && await FloorAsync() == "1");
        }

        // ---------- Scenario H: concurrent writes and privileged floor removal
        {
            // H1 (deterministic): once a write transaction has verified the fence, the floor cannot be dropped until it ends.
            string dropWhileVerified;
            string held;
            await using (var scope = await rb.Store.BeginScopeAsync(CancellationToken.None))
            {
                await scope.LockOrganizationAsync(orgA.Id, CancellationToken.None);
                await scope.RequireContentAcceptanceFloorAsync(CancellationToken.None);
                held = await ScalarAsync(adminCs, "SELECT coalesce(string_agg(l.mode, ','), 'none') FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE l.relation = 'authority.content_change_operations'::regclass AND a.application_name LIKE 'carv-runtime-RB %'");
                dropWhileVerified = await ExecAsync(adminCs, "SET lock_timeout = '2s'; " + DropFloor);
            }

            Record("RX-H1", "while a Release B write transaction holds its verified fence (ROW EXCLUSIVE), a privileged DROP of the floor cannot complete (lock timeout); the floor stays", "RowExclusiveLock held; 55P03; floor 1",
                $"held {held}; drop {dropWhileVerified}; floor={await FloorAsync()}", held.Contains("RowExclusiveLock", StringComparison.Ordinal) && dropWhileVerified.StartsWith("55P03", StringComparison.Ordinal) && await FloorAsync() == "1");

            // H2: four Release B instances write while the floor is dropped; no write commits after the drop committed.
            var pool = Enumerable.Range(0, 4).Select(i => Instance($"RB-par{i}", runtimeCs, ReleaseB)).ToArray();
            var started = await ScalarAsync(adminCs, "SELECT now()::text");
            var outcomes = new ConcurrentBag<string>();
            var records = new[] { orgA.Records[0], orgA.Records[1], orgA.Records[2], orgB.Records[1] };
            var work = Parallel.ForEachAsync(Enumerable.Range(0, pool.Length), async (i, _) =>
            {
                for (var round = 0; round < 10; round++)
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

                        outcomes.Add("OK");
                    }
                    catch (AuthorityException ex) when (ex.ErrorCode is AuthorityErrorCodes.ContentRevisionConflict or AuthorityErrorCodes.IdempotencyConflict or AuthorityErrorCodes.CurrentSnapshotMismatch)
                    {
                        outcomes.Add("conflict");
                    }
                    catch (AuthorityException ex) when (ex.ErrorCode is AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced)
                    {
                        outcomes.Add("refused");
                    }
                    catch (Exception ex)
                    {
                        outcomes.Add("UNEXPECTED " + Describe(ex));
                    }
                }
            });
            await Task.Delay(800);
            var dropTimestamp = await DropFloorRecordingCommitTimestampAsync();
            await work;
            var late = await ScalarAsync(adminCs, $"SELECT count(*) FILTER (WHERE pg_xact_commit_timestamp(xmin) >= '{dropTimestamp}'::timestamptz)||'/'||count(*) FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version = 1 AND operation_committed_on >= '{started}'::timestamptz");
            var grouped = string.Join(", ", outcomes.GroupBy(o => o.StartsWith("UNEXPECTED", StringComparison.Ordinal) ? o : o).Select(g => $"{g.Key}={g.Count()}"));
            Record("RX-H2", "four Release B instances writing while the floor is dropped: every accepted operation committed before the drop committed (commit timestamps); writes after it are refused; nothing unexpected",
                "late 0/N (N>0); refused>0; no UNEXPECTED", $"drop committed {dropTimestamp}; late/accepted {late}; {grouped}",
                late.StartsWith("0/", StringComparison.Ordinal) && late != "0/0" && outcomes.Contains("refused") && !outcomes.Any(o => o.StartsWith("UNEXPECTED", StringComparison.Ordinal)));
            var (reCode, reOutput) = await FloorCommandAsync(extra: ContentAcceptanceFloorCommand.ReinstateFlag);
            var resumed = await Outcome(WriteAsync(rb, orgA.Records[0]));
            Record("RX-H3", "after the concurrent removal, the approved reinstatement restores the floor and Release B writes resume", "exit 0; floor 1; OK", $"exit {reCode}: {reOutput}; floor={await FloorAsync()}; {resumed}",
                reCode == 0 && await FloorAsync() == "1" && resumed == "OK");
        }

        // ---------- Scenario D: the database restored to before floor activation; Release B intent kept in configuration
        {
            var restored = await RestoreDatabaseAsync("carv_prefloor");
            var orgD = await SeedOrgAsync("D", 3); // records whose accepted state the restored database and this harness agree on
            var refused = await RefusedWriteAsync(rb, orgD, orgD.Records[0]);
            var notReady = await GuardAsync(guardB);
            Record("RX-D1", "restore of the pre-floor database: the running Release B instance (its intent is configuration, outside the restored database) refuses writes; readiness not ready",
                $"restored; floor none; {Refused}; unchanged; reason=FloorNotInstalled | False FloorBelowRequired",
                $"{restored}; floor={await FloorAsync()}; history={await FloorHistoryAsync()}; {refused.Observed} | {notReady}",
                restored == "OK" && await FloorAsync() == "none" && await FloorHistoryAsync() == 0 && refused.Outcome == Refused && refused.Unchanged && refused.Alert.Contains("reason=FloorNotInstalled", StringComparison.Ordinal) && notReady == "False ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)");
            var releaseA = await Outcome(WriteAsync(ra, orgD.Records[1]));
            var legacy = await LegacyPrepareAsync(orgD.Records[2]);
            var legacyCommit = legacy.Error is null ? await LegacyCommitAsync(orgD.Records[2], legacy.Prepared!, legacy.Rows) : legacy;
            Record("RX-D2", "on the restored database a Release A-configured instance writes and a legacy build prepares and commits again: why Release B intent must be held outside the database (deployment control)",
                "OK | legacy accepted", $"{releaseA} | legacy {legacyCommit.Error ?? "accepted " + await StampsAsync(legacy.Prepared!.OperationId)}", releaseA == "OK" && legacyCommit.Error is null);
            var (code, output) = await FloorCommandAsync();
            var resumed = await Outcome(WriteAsync(rb, orgD.Records[0]));
            var legacyAgain = await LegacyPrepareAsync(orgD.Records[2]);
            Record("RX-D3", "the floor re-applied through the authorized command; Release B writes resume only then; the legacy build is refused again",
                "exit 0; floor 1; verdict none; OK; 23514 floor_v1", $"exit {code}: {output}; floor={await FloorAsync()}; verdict={await LiveVerdictAsync()}; {resumed}; legacy {legacyAgain.Error ?? "accepted!"}",
                code == 0 && await FloorAsync() == "1" && await LiveVerdictAsync() == "none" && resumed == "OK" && (legacyAgain.Error ?? string.Empty).Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal));
        }

        Record("FINAL", "final operation table summary", "informational", await OpsSummaryAsync() + $"; legacy agent calls={agentCalls}; alerts={alerts.Raised.Count}", true);
    }

    // ------------------------------------------------------------------------------------------------ RB-CARV-1 helpers
    private sealed record RefusedWrite(string Outcome, bool Unchanged, string Alert, string Observed);

    private ContentAcceptanceRuleGuard Guard(ContentAcceptancePolicy policy) =>
        new(new ContentAcceptanceRuleSchemaReader(Factory.For(markedRuntime)), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance, policy);

    private async Task<string> FloorAsync() => await ScalarAsync(adminCs, FloorQuery);

    /// <summary>The write fence verdict on the live catalog ("none" = enforced), read with the real build's own query and evaluator.</summary>
    private async Task<string> LiveVerdictAsync()
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        var facts = await ContentAcceptanceWriteFence.ReadAsync(connection, null, ContentAcceptancePolicy.ReleaseBFloor, CancellationToken.None);
        return ContentAcceptanceWriteFence.Evaluate(facts, ContentAcceptancePolicy.ReleaseBFloor) ?? "none";
    }

    /// <summary>A content write attempt that must be refused: outcome, whether operations and the record's accepted state are unchanged, and the alert it raised.</summary>
    private async Task<RefusedWrite> RefusedWriteAsync(AuthorityNode instance, Org org, RecordModel record)
    {
        var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
        var before = await SnapshotAsync(org, record);
        int alertIndex;
        lock (alerts.Raised)
        {
            alertIndex = alerts.Raised.Count;
        }

        var outcome = await Outcome(WriteAsync(instance, record));
        var after = await SnapshotAsync(org, record);
        var opsAfter = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
        string alert;
        lock (alerts.Raised)
        {
            alert = string.Join(" / ", alerts.Raised.Skip(alertIndex).Where(a => a.AlertKind == ContentAcceptanceFloorFailures.AlertKind).Select(a => a.Summary));
        }

        var unchanged = before == after && opsBefore == opsAfter;
        return new RefusedWrite(outcome, unchanged, alert, $"{outcome}; ops {opsBefore}->{opsAfter}; {(before == after ? "state unchanged" : "CHANGED")}; alert [{alert}]");
    }

    /// <summary>Weakens the database (privileged), proves the Release B write is refused with the expected reason, reverses it, proves writes resume.</summary>
    private async Task WeakenedAsync(string id, string description, AuthorityNode instance, Org org, RecordModel record, string weaken, string reason, string? restore = null, bool restoreFloor = false)
    {
        var applied = await ExecAsync(adminCs, weaken);
        var refused = await RefusedWriteAsync(instance, org, record);
        var verdict = await LiveVerdictSafeAsync();
        string restored;
        if (restoreFloor)
        {
            await ExecAsync(adminCs, DropFloor);
            var (code, output) = await FloorCommandAsync(extra: ContentAcceptanceFloorCommand.ReinstateFlag);
            restored = code == 0 ? "OK" : $"exit {code}: {output}";
        }
        else
        {
            restored = await ExecAsync(adminCs, restore!);
        }

        var resumed = await Outcome(WriteAsync(instance, record));
        Record(id, $"{description}: the Release B write is refused (reason {reason}); after reversal writes resume", $"weakened OK; {Refused}; unchanged; reason={reason}; restored OK; resumed OK",
            $"weakened {applied}; {refused.Observed}; live verdict {verdict}; restored {restored}; resumed {resumed}",
            applied == "OK" && refused.Outcome == Refused && refused.Unchanged && refused.Alert.Contains($"reason={reason} ", StringComparison.Ordinal) && restored == "OK" && resumed == "OK");
    }

    private async Task<string> LiveVerdictSafeAsync()
    {
        try
        {
            return await LiveVerdictAsync();
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    private async Task<(PreparedContentOperation Prepared, List<Staged> Rows, PrepareContentChangeRequest Request, string Key)> PrepareKeepingRequestAsync(AuthorityNode instance, RecordModel record)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation);
        var key = "carv-" + operation.ToString("N");
        var prepared = await instance.Service.PrepareAsync(Actor(record.Org), request, key, Guid.CreateVersion7(), CancellationToken.None);
        return (prepared, rows, request, key);
    }

    /// <summary>A store scope used directly, without any explicit fence call: the organization lock, the change, the commit.</summary>
    private static async Task DirectAsync(AuthorityNode instance, Org org, Func<IAuthorityScope, Task> change)
    {
        await using var scope = await instance.Store.BeginScopeAsync(CancellationToken.None);
        await scope.LockOrganizationAsync(org.Id, CancellationToken.None);
        await change(scope);
        await scope.CommitAsync(CancellationToken.None);
    }

    private async Task<string> DropFloorRecordingCommitTimestampAsync()
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        string xid;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var id = new NpgsqlCommand("SELECT pg_current_xact_id()::text", connection, transaction))
            {
                xid = Convert.ToString(await id.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
            }

            await using (var drop = new NpgsqlCommand(DropFloor, connection, transaction))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await using var ts = new NpgsqlCommand($"SELECT pg_xact_commit_timestamp('{xid}'::xid8::xid)::text", connection);
        var committed = Convert.ToString(await ts.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
        Log($"floor dropped by xid {xid}, committed {committed}");
        return committed;
    }

    private string MaintenanceCs => new NpgsqlConnectionStringBuilder(adminCs) { Database = "postgres", Pooling = false, ApplicationName = "carv-maintenance" }.ConnectionString;

    private string DatabaseName => new NpgsqlConnectionStringBuilder(adminCs).Database!;

    /// <summary>A file-system-level copy of the database (CREATE DATABASE ... TEMPLATE), as a backup taken at this point.</summary>
    private async Task<string> SnapshotDatabaseAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        var terminated = await ExecAsync(MaintenanceCs, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{DatabaseName}' AND pid <> pg_backend_pid()");
        var created = "not attempted";
        for (var attempt = 0; attempt < 20 && created != "OK"; attempt++)
        {
            created = await ExecAsync(MaintenanceCs, $"CREATE DATABASE {name} TEMPLATE {DatabaseName}"); // 55006 while terminated backends are still exiting
            if (created != "OK")
            {
                await Task.Delay(250);
            }
        }

        NpgsqlConnection.ClearAllPools();
        Log($"database snapshot {name}: terminate {terminated}; create {created}");
        return terminated == "OK" ? created : terminated;
    }

    /// <summary>Restores the database from the snapshot (drop, re-create from it): roles and the running instances are untouched.</summary>
    private async Task<string> RestoreDatabaseAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        var dropped = await ExecAsync(MaintenanceCs, $"DROP DATABASE {DatabaseName} WITH (FORCE)");
        var created = await ExecAsync(MaintenanceCs, $"CREATE DATABASE {DatabaseName} TEMPLATE {name}");
        NpgsqlConnection.ClearAllPools();
        Log($"database restored from {name}: drop {dropped}; create {created}");
        return dropped == "OK" ? created : dropped;
    }

    private async Task<bool> WaitForLockWaitLikeAsync(string applicationPattern)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await CountAsync($"SELECT count(*) FROM pg_stat_activity WHERE application_name LIKE '{applicationPattern}' AND wait_event_type = 'Lock'") > 0)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>An admin transaction holding the given lock on content_change_operations.</summary>
    private async Task<NpgsqlTransaction> HoldTableLockAsync(string mode)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "carv-lock-holder" }.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"LOCK TABLE authority.content_change_operations IN {mode} MODE", connection, transaction);
        await command.ExecuteNonQueryAsync();
        return transaction;
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

    private sealed record AuthorityNode(string Name, ContentService Service, GatedFaults Faults, IAuthorityStore Store);

    private AuthorityNode Instance(string name, string connectionString, ContentAcceptancePolicy policy, TimeSpan? clockOffset = null)
    {
        // The real build's versioned application name (AddAuthorityInfrastructure applies the same helper in the host).
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "carv-runtime-" + name }.ConnectionString)!;
        var store = new AuthorityStore(Factory.For(cs), policy, null, null, alerts); // RB-CARV-1: the instance's deployment policy
        var clock = new OffsetClock(clockOffset ?? TimeSpan.Zero);
        var faults = new GatedFaults();
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), clock);
        return new AuthorityNode(name, new ContentService(store, mac, verifier, faults, clock, new ShareContributorWriteSwitch(false, false)), faults, store);
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
        var pool = Enumerable.Range(0, instances).Select(i => Instance($"RB-par{i}", runtimeCs, ReleaseA)).ToArray();
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
