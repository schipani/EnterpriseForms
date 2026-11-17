// E3-M2d CARV 2 / floor 2 focused real-PostgreSQL gate (docs/security/E3_M2D_IMPLEMENTATION_RECORD.md).
//
// Runs the REAL Authority code of this working tree (Release build, M2dBinaries; ContentAcceptanceRules.CurrentVersion = 2)
// in-process against ONE disposable PostgreSQL 18 container: this build's own --migrate and --apply-content-acceptance-floor
// (separate processes, migration identity), and ContentService + EF AuthorityStore instances (Release B with required floor 1,
// Release B with required floor 2, Release A) on the non-superuser runtime role. The version-1 build (the accepted signed E3-M2c
// commit 8915f77, CurrentVersion = 1) runs in a separate process (V1Agent) for the cross-build checks.
//
// Controlled test-only mechanism (as in the E3-M2b-2 S-4 gate): a candidate signed with the harness's deterministic TEST-ONLY HMAC
// provider is recorded as a Prepared operation through an ordinary admin session, so every table constraint, the CARV state guard
// and the floor CHECK apply. No production code, configuration, verification or validation is changed or bypassed. Catalog
// mutations (dropping, weakening or duplicating a floor) are made with the container's superuser, as an attacker or a faulty
// privileged change would; they only ever touch this disposable container.
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var lab = new Lab(
    Environment.GetEnvironmentVariable("M2D_NEWBIN") ?? throw new InvalidOperationException("M2D_NEWBIN"),
    Environment.GetEnvironmentVariable("M2D_V1AGENT") ?? throw new InvalidOperationException("M2D_V1AGENT"),
    Environment.GetEnvironmentVariable("M2D_ADMIN") ?? throw new InvalidOperationException("M2D_ADMIN"),
    Environment.GetEnvironmentVariable("M2D_RUNTIME") ?? throw new InvalidOperationException("M2D_RUNTIME"),
    Environment.GetEnvironmentVariable("M2D_EVIDENCE") ?? throw new InvalidOperationException("M2D_EVIDENCE"));
return await lab.RunAsync();

internal sealed class Lab(string newBinary, string v1Agent, string adminCs, string runtimeCs, string evidenceDir)
{
    private const string Issuer = "https://m2d-harness.invalid/";
    private const string FloorQuery = "SELECT coalesce(string_agg(substring(conname FROM 'ck_content_operation_rule_floor_v([0-9]+)$'), ',' ORDER BY conname), 'none') FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname LIKE 'ck\\_content\\_operation\\_rule\\_floor\\_v%'";
    private const string FloorHistoryQuery = "SELECT coalesce(string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\"), 'none') FROM authority.ef_migrations_history WHERE \"MigrationId\" LIKE '%ContentAcceptanceRuleFloor%'";
    private const string NotEnforced = "AuthorityException CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED 503 retryable=False";
    private const string Outdated = "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False";
    private static readonly ContentAcceptancePolicy ReleaseB1 = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "1");
    private static readonly ContentAcceptancePolicy ReleaseB2 = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "2");
    private static readonly ContentAcceptancePolicy ReleaseA = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseAName, null);
    private readonly List<Check> checks = [];
    private readonly List<string> log = [];
    private readonly TestMac mac = new();
    private readonly CapturingAlerts alerts = new();

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
            harness = "E3-M2d CARV 2 / floor 2 focused real-PostgreSQL gate (real Authority build in-process; version-1 build 8915f77 through V1Agent)",
            build = "Release build of the working tree (new-sha256.txt); ContentAcceptanceRules.CurrentVersion = " + ContentAcceptanceRules.CurrentVersion
                    + "; RecognizedFloors = " + string.Join(",", ContentAcceptanceRules.RecognizedFloors)
                    + "; EnvelopeRules.AllowedEnvelopeVersions = " + string.Join(",", EnvelopeRules.AllowedEnvelopeVersions),
            postgres = await ScalarAsync(adminCs, "SELECT version()"),
            checksTotal = checks.Count,
            checksFailed = failed,
            checks,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "m2d-evidence.json"), json);
        await File.WriteAllLinesAsync(Path.Combine(evidenceDir, "m2d-log.txt"), log);
        Console.WriteLine($"checks: {checks.Count}, failed: {failed}");
        foreach (var c in checks)
        {
            Console.WriteLine($"{(c.Pass ? "PASS" : "FAIL")} {c.Id}: {c.Description} | observed: {c.Observed}");
        }

        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ scenarios
    private async Task ScenariosAsync()
    {
        // ================================================================ setup: migrate, floor 1 (exactly floor 1)
        var migrated = await HostCommandAsync(adminCs, "m2d-migrate", "--migrate");
        Record("D-00", "setup: this build's ordinary --migrate applies the CARV fence and never a floor", "exit 0; floors none; floor history none",
            $"exit {migrated.Code}; floors {await FloorAsync()}; floor history {await ScalarAsync(adminCs, FloorHistoryQuery)}",
            migrated.Code == 0 && await FloorAsync() == "none" && await ScalarAsync(adminCs, FloorHistoryQuery) == "none");

        var floor1 = await HostCommandAsync(adminCs, "m2d-floor1", "--apply-content-acceptance-floor", "1");
        Record("D-00b", "the floor command with 1 applies exactly floor 1: the later floor-2 migration of this build is NOT applied implicitly", $"exit 0; floors 1; history {ContentAcceptanceFloorSql.MigrationId}",
            $"exit {floor1.Code}; floors {await FloorAsync()}; history {await ScalarAsync(adminCs, FloorHistoryQuery)}",
            floor1.Code == 0 && await FloorAsync() == "1" && await ScalarAsync(adminCs, FloorHistoryQuery) == ContentAcceptanceFloorSql.MigrationId);

        var org = await SeedOrgAsync(24);
        var g1 = Guard(ReleaseB1);
        var g2 = Guard(ReleaseB2);
        var rb1 = Instance("RB1", ReleaseB1);
        var rb2 = Instance("RB2", ReleaseB2);
        var ra = Instance("RA", ReleaseA);

        // ================================================================ phase A: floor-1 compatibility of the version-2 build
        Record("D-A1", "Release B (required floor 1) readiness of the version-2 build on floor 1; live fence verdicts", "True content acceptance rules v2 (floor 1); phase=ReleaseB requiredFloor=1 | required 1: none | required 2: FloorLowered",
            $"{await GuardAsync(g1)} | required 1: {await LiveVerdictAsync(1)} | required 2: {await LiveVerdictAsync(2)}",
            await GuardAsync(g1) == "True content acceptance rules v2 (floor 1); phase=ReleaseB requiredFloor=1" && await LiveVerdictAsync(1) == "none" && await LiveVerdictAsync(2) == ContentAcceptanceFloorFailures.FloorLowered);
        Record("D-A2", "the runtime role is an ordinary role (no superuser, no BYPASSRLS, not the table owner)", "false false false", await RoleFactsAsync(), await RoleFactsAsync() == "false false false");

        var initial = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            initial.Add(await Outcome(WriteAsync(rb1, org.Records[i], Variant.Valid)));
        }

        Record("D-A3", "version-2 Release B (floor 1) writes through the real Prepare + Commit; every committed operation P = C = 2", "10 x OK; committed 2/2: 10",
            $"{string.Join(",", initial.Distinct())}; committed 2/2: {await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id='{org.Id}' AND operation_state_name='Committed' AND prepare_acceptance_rule_version=2 AND commit_acceptance_rule_version=2")}",
            initial.All(o => o == "OK") && await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id='{org.Id}' AND operation_state_name='Committed' AND prepare_acceptance_rule_version=2 AND commit_acceptance_rule_version=2") == 10);

        await RefusedWriteAsync("D-A4", "a Release B instance requiring floor 2 refuses every write while only floor 1 is installed (minimum not met), transactionally", rb2, org, org.Records[0], ContentAcceptanceFloorFailures.FloorLowered);
        Record("D-A5", "its readiness is not ready", "False ContentAcceptanceRuleFloorBelowRequired(installed=1, required=2)", await GuardAsync(g2), await GuardAsync(g2) == "False ContentAcceptanceRuleFloorBelowRequired(installed=1, required=2)");

        // ================================================================ phase X: cross-build behaviour under floor 1
        {
            var record = org.Records[10];
            var v1 = await V1PrepareAsync(record, Variant.Valid);
            Record("D-X1a", "version-1 build (8915f77) Prepare under floor 1: accepted, stamped P = 1", "OK; 1/NULL Prepared", $"{v1.Outcome}; {await StampsAsync(v1.Operation)}", v1.Outcome == "OK" && await StampsAsync(v1.Operation) == "1/NULL Prepared");
            var before = await SnapshotAsync(org, record);
            var outcome = await Outcome(rb1.Service.CommitAsync(Actor(org), v1.Operation, new CommitContentChangeRequest(org.Id, record.Id, v1.Staged!), Guid.CreateVersion7(), CancellationToken.None));
            Record("D-X1b", "version-2 Commit of the version-1-prepared operation: refused (P = 1 <> V = 2) and aborted before any verification; nothing advances", $"{Outdated}; 1/NULL Aborted {ContentAcceptanceRules.OutdatedAbortReason}; snapshot unchanged",
                $"{outcome}; {await StampsAsync(v1.Operation)} {await ReasonAsync(v1.Operation)}; {Diff(before, await SnapshotAsync(org, record))}",
                outcome == Outdated && await StampsAsync(v1.Operation) == "1/NULL Aborted" && await ReasonAsync(v1.Operation) == ContentAcceptanceRules.OutdatedAbortReason && before == await SnapshotAsync(org, record));
        }

        {
            var record = org.Records[11];
            var (prepared, rows) = await PrepareAsync(rb1, record, Variant.Valid);
            var before = await SnapshotAsync(org, record);
            var v1 = await V1CommitAsync(record, prepared.OperationId, Staged(prepared, rows));
            Record("D-X2", "version-1 Commit of a version-2-prepared operation: refused by the version-1 rule (P = 2 <> V = 1); nothing advances", $"{Outdated}; 2/NULL Aborted; snapshot unchanged",
                $"{v1}; {await StampsAsync(prepared.OperationId)}; {Diff(before, await SnapshotAsync(org, record))}",
                v1 == Outdated && await StampsAsync(prepared.OperationId) == "2/NULL Aborted" && before == await SnapshotAsync(org, record));

            var (seededOp, _, seeded) = await SeedPreparedAsync(org, record, Variant.Valid, prepareRuleVersion: 2);
            var forced = await ExecAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_state_name='Committed', commit_acceptance_rule_version=1 WHERE content_change_operation_id='{seededOp}'");
            Record("D-X2b", "database fence independent of either build: a P = 2 operation cannot be marked Committed with C = 1 (exact equality C = P)", "seed OK; 23514 ck_content_operation_commit_rule_version; still 2/NULL Prepared",
                $"{seeded}; {forced}; {await StampsAsync(seededOp)}", seeded == "OK" && forced.StartsWith("23514 ck_content_operation_commit_rule_version", StringComparison.Ordinal) && await StampsAsync(seededOp) == "2/NULL Prepared");
        }

        Guid historicalOperation;
        List<StagedContentItem> historicalStaged;
        {
            var record = org.Records[12];
            var v1 = await V1PrepareAsync(record, Variant.Valid);
            var commit = await V1CommitAsync(record, v1.Operation, v1.Staged!);
            if (commit == "OK")
            {
                record.Accepted = v1.Rows!.Select(r => (r, Convert.FromBase64String(v1.Staged!.Single(s => s.ItemId == r.ItemId && s.ItemKind == r.Kind).RowSignatureBase64))).ToList();
            }

            Record("D-X3", "version-1 Prepare + Commit end to end under floor 1 (a mixed fleet before floor 2 is compatible)", "OK OK; 1/1 Committed", $"{v1.Outcome} {commit}; {await StampsAsync(v1.Operation)}", v1.Outcome == "OK" && commit == "OK" && await StampsAsync(v1.Operation) == "1/1 Committed");
            historicalOperation = v1.Operation;
            historicalStaged = v1.Staged!;
        }

        {
            var record = org.Records[13];
            var v1 = await V1PrepareAsync(record, Variant.Valid);
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var replay = await Outcome(rb1.Service.PrepareAsync(Actor(org), v1.Request!, v1.Key!, Guid.CreateVersion7(), CancellationToken.None));
            Record("D-X4", "version-2 Prepare replay (same key and request) of a version-1-prepared operation: refused, nothing written", $"{Outdated}; operations unchanged; 1/NULL Prepared",
                $"{replay}; operations {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; {await StampsAsync(v1.Operation)}",
                replay == Outdated && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations") && await StampsAsync(v1.Operation) == "1/NULL Prepared");
            await Outcome(rb1.Service.CommitAsync(Actor(org), v1.Operation, new CommitContentChangeRequest(org.Id, record.Id, v1.Staged!), Guid.CreateVersion7(), CancellationToken.None)); // aborts it (CARV)
        }

        {
            var record = org.Records[12];
            var before = await SnapshotAsync(org, record);
            var global = await GlobalSnapshotAsync(org);
            var replay = await Outcome(rb1.Service.CommitAsync(Actor(org), historicalOperation, new CommitContentChangeRequest(org.Id, record.Id, historicalStaged), Guid.CreateVersion7(), CancellationToken.None));
            Record("D-X5", "version-2 Commit replay of the committed version-1 operation returns the head and writes nothing (finished operations replay first)", "OK; snapshots unchanged",
                $"{replay}; {Diff(before, await SnapshotAsync(org, record))}", replay == "OK" && before == await SnapshotAsync(org, record) && global == await GlobalSnapshotAsync(org));
        }

        // ================================================================ phase V: Envelope v2 stays dark on the real persistence path
        foreach (var (id, variant, record) in new[] { ("D-V1", Variant.NewV2Field, org.Records[14]), ("D-V2", Variant.NewV2Document, org.Records[15]) })
        {
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var leavesBefore = await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves");
            var signs = mac.Signatures;
            var (outcome, message) = await OutcomeWithMessage(PrepareAsync(rb1, record, variant));
            Record(id, $"real Prepare of a well-formed new {variant} candidate: refused (no v2 acceptance); nothing signed or stored", "AuthorityException INVALID_COMMAND 422 retryable=False; ENVELOPE_INVALID: ENVELOPE_VERSION_NOT_ENABLED; no signature, operation or leaf",
                $"{outcome}; {message}; signatures {mac.Signatures - signs}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; leaves {leavesBefore}->{await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves")}",
                outcome == "AuthorityException INVALID_COMMAND 422 retryable=False" && message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled && mac.Signatures == signs
                && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations") && leavesBefore == await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves"));
        }

        foreach (var (id, variant, record, reason, abort) in new[]
        {
            ("D-V3", Variant.NewV2Field, org.Records[16], EnvelopeV2Rules.Reasons.VersionNotEnabled, EnvelopeV2Rules.NotEnabledAtCommitReason),
            ("D-V4", Variant.FieldSchema2Envelope1, org.Records[16], EnvelopeSchemaTie.MismatchReason, EnvelopeSchemaTie.MismatchAtCommitReason),
        })
        {
            var (operation, staged, seeded) = await SeedPreparedAsync(org, record, variant, prepareRuleVersion: 2);
            var before = await SnapshotAsync(org, record);
            var global = await GlobalSnapshotAsync(org);
            var verifications = mac.Verifications;
            var (outcome, message) = await OutcomeWithMessage(rb1.Service.CommitAsync(Actor(org), operation, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            Record(id, $"Release B Commit of an otherwise valid Prepared operation (P = 2) holding {variant}: signatures verify, then refused and aborted; nothing advances",
                $"seed OK; ENVELOPE_INVALID: {reason}; 2/NULL Aborted {abort}; signatures verified > 0; snapshots unchanged",
                $"{seeded}; {message}; {await StampsAsync(operation)} {await ReasonAsync(operation)}; verified {mac.Verifications - verifications}; {Diff(before, await SnapshotAsync(org, record))} | {Diff(global, await GlobalSnapshotAsync(org))}",
                seeded == "OK" && message == "ENVELOPE_INVALID: " + reason && await StampsAsync(operation) == "2/NULL Aborted" && await ReasonAsync(operation) == abort
                && mac.Verifications > verifications && before == await SnapshotAsync(org, record) && global == await GlobalSnapshotAsync(org));
        }

        {
            var record = org.Records[16];
            var (operation, staged, seeded) = await SeedPreparedAsync(org, record, Variant.Valid, prepareRuleVersion: 2);
            var before = await HeadAsync(record);
            var outcome = await Outcome(rb1.Service.CommitAsync(Actor(org), operation, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            Record("D-V5", "control: the same seeding with a valid v1 candidate commits (the refusals above are the v2 and S-4 rules, not the fixture)", "seed OK; OK; 2/2 Committed; head +1",
                $"{seeded}; {outcome}; {await StampsAsync(operation)}; head {before.Revision}->{(await HeadAsync(record)).Revision}",
                seeded == "OK" && outcome == "OK" && await StampsAsync(operation) == "2/2 Committed" && (await HeadAsync(record)).Revision == before.Revision + 1);
        }

        // ================================================================ phase F: floor 2 activation
        {
            var fresh = new NpgsqlConnectionStringBuilder(adminCs) { Database = "m2dfresh" }.ConnectionString;
            await ExecAsync(adminCs, "CREATE DATABASE m2dfresh");
            var freshMigrate = await HostCommandAsync(fresh, "m2d-fresh-migrate", "--migrate");
            var skip = await HostCommandAsync(fresh, "m2d-fresh-floor2", "--apply-content-acceptance-floor", "2");
            Record("D-F0", "floors are raised one step at a time: floor 2 on a database without floor 1 is refused; nothing applied", "migrate exit 0; floor 2 exit 2 'requires every lower floor'; floors none; floor history none",
                $"migrate exit {freshMigrate.Code}; floor 2 exit {skip.Code}; floors {await ScalarAsync(fresh, FloorQuery)}; floor history {await ScalarAsync(fresh, FloorHistoryQuery)}",
                freshMigrate.Code == 0 && skip.Code == 2 && skip.Output.Contains("requires every lower floor", StringComparison.Ordinal) && await ScalarAsync(fresh, FloorQuery) == "none" && await ScalarAsync(fresh, FloorHistoryQuery) == "none");
        }

        {
            var withMigrate = await HostCommandAsync(adminCs, "m2d-floor2-migrate", "--apply-content-acceptance-floor", "2", "--migrate");
            var three = await HostCommandAsync(adminCs, "m2d-floor3", "--apply-content-acceptance-floor", "3");
            Record("D-F1", "the command refuses floor 2 combined with --migrate, and an unrecognized floor 3; nothing applied", "exit 2 | exit 2; floors 1",
                $"exit {withMigrate.Code} | exit {three.Code}; floors {await FloorAsync()}", withMigrate.Code == 2 && three.Code == 2 && await FloorAsync() == "1");
        }

        {
            await using var stale = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "authority carv-v1", Pooling = false }.ConnectionString);
            await stale.OpenAsync(); // what a connected version-1 Authority instance looks like in pg_stat_activity
            var refused = await HostCommandAsync(adminCs, "m2d-floor2-stale", "--apply-content-acceptance-floor", "2");
            Record("D-F2", "activation precondition: a connected session able to write operations with an older CARV marker (carv-v1) blocks floor 2; nothing applied", "exit 3 'CARV build marker of version 2'; floors 1; history Floor1 only",
                $"exit {refused.Code}; marker message {refused.Output.Contains("CARV build marker of version 2", StringComparison.Ordinal)}; floors {await FloorAsync()}; history {await ScalarAsync(adminCs, FloorHistoryQuery)}",
                refused.Code == 3 && refused.Output.Contains("CARV build marker of version 2", StringComparison.Ordinal) && await FloorAsync() == "1" && await ScalarAsync(adminCs, FloorHistoryQuery) == ContentAcceptanceFloorSql.MigrationId);
        }

        {
            var record = org.Records[17];
            var v1 = await V1PrepareAsync(record, Variant.Valid);
            var refused = await HostCommandAsync(adminCs, "m2d-floor2-inflight", "--apply-content-acceptance-floor", "2");
            Record("D-F3", "activation precondition: an unexpired operation prepared below version 2 blocks floor 2; nothing applied", "prepare OK; exit 3 'prepared below content acceptance rule version 2'; floors 1",
                $"prepare {v1.Outcome}; exit {refused.Code}; in-flight message {refused.Output.Contains("prepared below content acceptance rule version 2", StringComparison.Ordinal)}; floors {await FloorAsync()}",
                v1.Outcome == "OK" && refused.Code == 3 && refused.Output.Contains("prepared below content acceptance rule version 2", StringComparison.Ordinal) && await FloorAsync() == "1");
            var abort = await Outcome(rb1.Service.CommitAsync(Actor(org), v1.Operation, new CommitContentChangeRequest(org.Id, record.Id, v1.Staged!), Guid.CreateVersion7(), CancellationToken.None));
            Log($"D-F3 reconciliation: the version-2 Commit aborts the in-flight version-1 operation: {abort}; {await StampsAsync(v1.Operation)}");
        }

        {
            await using var holder = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "m2d-lock-holder", Pooling = false }.ConnectionString);
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand("LOCK TABLE authority.content_change_operations IN ROW EXCLUSIVE MODE", holder, transaction))
            {
                await lockCommand.ExecuteNonQueryAsync();
            }

            var timedOut = await HostCommandAsync(adminCs, "m2d-floor2-locked", "--apply-content-acceptance-floor", "2");
            await transaction.RollbackAsync();
            Record("D-F4", "activation under a held writer lock times out (lock_timeout 5 s) and applies nothing", "exit 3 55P03; floors 1; history Floor1 only",
                $"exit {timedOut.Code}; 55P03 {timedOut.Output.Contains("55P03", StringComparison.Ordinal)}; floors {await FloorAsync()}; history {await ScalarAsync(adminCs, FloorHistoryQuery)}",
                timedOut.Code == 3 && timedOut.Output.Contains("55P03", StringComparison.Ordinal) && await FloorAsync() == "1" && await ScalarAsync(adminCs, FloorHistoryQuery) == ContentAcceptanceFloorSql.MigrationId);
        }

        var lagging = await V1PrepareAsync(org.Records[18], Variant.Valid, clockOffsetMinutes: -11);
        Record("D-F5", "a lagging version-1 instance prepares an operation that is already expired by the database clock (it must not block activation)", "OK; 1/NULL Prepared; expired by the database clock",
            $"{lagging.Outcome}; {await StampsAsync(lagging.Operation)}; expired {await ScalarAsync(adminCs, $"SELECT operation_expires_on < now() FROM authority.content_change_operations WHERE content_change_operation_id='{lagging.Operation}'")}",
            lagging.Outcome == "OK" && await StampsAsync(lagging.Operation) == "1/NULL Prepared" && await ScalarAsync(adminCs, $"SELECT operation_expires_on < now() FROM authority.content_change_operations WHERE content_change_operation_id='{lagging.Operation}'") == "True");

        {
            var writers = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                var outcomes = new List<string>();
                for (var round = 0; round < 3; round++)
                {
                    outcomes.Add(await Outcome(WriteAsync(rb1, org.Records[i], Variant.Valid)));
                }

                return outcomes;
            })).ToList();
            var activated = await HostCommandAsync(adminCs, "m2d-floor2", "--apply-content-acceptance-floor", "2");
            var written = (await Task.WhenAll(writers)).SelectMany(o => o).ToList();
            Record("D-F6", "floor 2 activation while four Release B (floor 1) version-2 writers run: applied; no writer refused (minimum semantics); floor 1 dropped in the same transaction", $"exit 0; 12 x OK; floors 2; history {ContentAcceptanceFloorSql.MigrationId},{ContentAcceptanceFloor2Sql.MigrationId}",
                $"exit {activated.Code}; {string.Join(",", written.GroupBy(o => o).Select(g => $"{g.Count()} x {g.Key}"))}; floors {await FloorAsync()}; history {await ScalarAsync(adminCs, FloorHistoryQuery)}",
                activated.Code == 0 && written.Count == 12 && written.All(o => o == "OK") && await FloorAsync() == "2" && await ScalarAsync(adminCs, FloorHistoryQuery) == ContentAcceptanceFloorSql.MigrationId + "," + ContentAcceptanceFloor2Sql.MigrationId);
        }

        var liveDefinition = await ScalarAsync(adminCs, "SELECT pg_get_constraintdef(oid) FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname = 'ck_content_operation_rule_floor_v2'");
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "floor2-definition.txt"), liveDefinition + Environment.NewLine);
        Record("D-F7", "the live PostgreSQL 18 rendering of floor 2 is exactly the definition the write fence recognizes (D-4); live verdicts at required 1 and 2", "definition equal; required 1: none; required 2: none",
            $"definition equal {liveDefinition == ContentAcceptanceFloor2Sql.ExpectedDefinition}; required 1: {await LiveVerdictAsync(1)}; required 2: {await LiveVerdictAsync(2)}",
            liveDefinition == ContentAcceptanceFloor2Sql.ExpectedDefinition && await LiveVerdictAsync(1) == "none" && await LiveVerdictAsync(2) == "none");
        Record("D-F8", "readiness after the raise: required floor 1 (a minimum) and required floor 2 both ready", "True ...v2 (floor 2); phase=ReleaseB requiredFloor=1 | True ...v2 (floor 2); phase=ReleaseB requiredFloor=2",
            $"{await GuardAsync(g1)} | {await GuardAsync(g2)}",
            await GuardAsync(g1) == "True content acceptance rules v2 (floor 2); phase=ReleaseB requiredFloor=1" && await GuardAsync(g2) == "True content acceptance rules v2 (floor 2); phase=ReleaseB requiredFloor=2");
        {
            var w2 = await Outcome(WriteAsync(rb2, org.Records[5], Variant.Valid));
            var w1 = await Outcome(WriteAsync(rb1, org.Records[6], Variant.Valid));
            Record("D-F9", "after the raise, Release B writes requiring floor 2 and floor 1 both commit (P = C = 2)", "OK | OK", $"{w2} | {w1}", w2 == "OK" && w1 == "OK");
        }

        {
            var again = await HostCommandAsync(adminCs, "m2d-floor2-again", "--apply-content-acceptance-floor", "2");
            var lower = await HostCommandAsync(adminCs, "m2d-floor1-again", "--apply-content-acceptance-floor", "1");
            Record("D-F10", "monotonic: re-applying floor 2 is a no-op; applying floor 1 after floor 2 is refused (never lowered)", "exit 0 | exit 2 'never lowered'; floors 2; one floor-2 history row",
                $"exit {again.Code} | exit {lower.Code} {lower.Output.Contains("never lowered", StringComparison.Ordinal)}; floors {await FloorAsync()}; floor-2 rows {await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor2Sql.MigrationId}'")}",
                again.Code == 0 && lower.Code == 2 && lower.Output.Contains("never lowered", StringComparison.Ordinal) && await FloorAsync() == "2"
                && await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor2Sql.MigrationId}'") == 1);
        }

        {
            var record = org.Records[19];
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var v1 = await V1PrepareAsync(record, Variant.Valid);
            Record("D-F11", "after floor 2, a version-1 Prepare is refused by the database floor (that build maps it to its own 409); nothing persisted", $"{Outdated} (the version-1 store's mapping of the database refusal; its alert names ck_content_operation_rule_floor_v2); operations unchanged",
                $"{v1.Outcome}; operations {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}",
                v1.Outcome.StartsWith(Outdated, StringComparison.Ordinal) && v1.Outcome.Contains("ck_content_operation_rule_floor_v2", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations"));
        }

        {
            var record = org.Records[18];
            var before = await SnapshotAsync(org, record);
            var global = await GlobalSnapshotAsync(org);
            var commit = await V1CommitAsync(record, lagging.Operation, lagging.Staged!, clockOffsetMinutes: -11);
            Record("D-F12", "after floor 2, the lagging version-1 instance cannot commit its pre-floor operation (P = 1 < floor 2); nothing advances", $"{Outdated} (its alert names ck_content_operation_rule_floor_v2); not Committed; snapshots unchanged",
                $"{commit}; {await StampsAsync(lagging.Operation)}; {Diff(before, await SnapshotAsync(org, record))} | {Diff(global, await GlobalSnapshotAsync(org))}",
                commit.StartsWith(Outdated, StringComparison.Ordinal) && commit.Contains("ck_content_operation_rule_floor_v2", StringComparison.Ordinal) && !(await StampsAsync(lagging.Operation)).EndsWith("Committed", StringComparison.Ordinal) && before == await SnapshotAsync(org, record) && global == await GlobalSnapshotAsync(org));
        }

        {
            var record = org.Records[12];
            var before = await SnapshotAsync(org, record);
            var replay = await Outcome(rb2.Service.CommitAsync(Actor(org), historicalOperation, new CommitContentChangeRequest(org.Id, record.Id, historicalStaged), Guid.CreateVersion7(), CancellationToken.None));
            Record("D-F13", "historical replay after floor 2: the committed version-1 operation (P = C = 1) still replays under Release B floor 2 and writes nothing (NOT VALID floor, immutable terminal rows)", "OK; still 1/1 Committed; snapshot unchanged",
                $"{replay}; {await StampsAsync(historicalOperation)}; {Diff(before, await SnapshotAsync(org, record))}",
                replay == "OK" && await StampsAsync(historicalOperation) == "1/1 Committed" && before == await SnapshotAsync(org, record));
        }

        {
            var (_, _, unstamped) = await SeedPreparedAsync(org, org.Records[20], Variant.Valid, prepareRuleVersion: 1);
            var (_, _, stamped) = await SeedPreparedAsync(org, org.Records[20], Variant.Valid, prepareRuleVersion: 2);
            Record("D-F14", "database floor 2 independent of any build: a Prepared operation stamped 1 cannot be inserted; stamped 2 can", "23514 ck_content_operation_rule_floor_v2 | OK",
                $"{unstamped} | {stamped}", unstamped.Contains("23514", StringComparison.Ordinal) && unstamped.Contains("ck_content_operation_rule_floor_v2", StringComparison.Ordinal) && stamped == "OK");
        }

        // ================================================================ phase M: missing and altered floors (Release B transactional fence)
        await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2");
        await RefusedWriteAsync("D-M1a", "floor 2 dropped (history kept): a Release B write requiring floor 2 is refused in the write transaction", rb2, org, org.Records[7], ContentAcceptanceFloorFailures.FloorRecordedButMissing);
        await RefusedWriteAsync("D-M1b", "floor 2 dropped: a Release B write requiring only floor 1 is refused too (a recorded higher floor is missing: a lowering)", rb1, org, org.Records[7], ContentAcceptanceFloorFailures.FloorRecordedButMissing);
        Record("D-M1c", "readiness of both Release B instances is not ready", "False ...FloorMissing | False ...FloorMissing",
            $"{await GuardAsync(g1)} | {await GuardAsync(g2)}",
            (await GuardAsync(g1)).StartsWith("False ContentAcceptanceRuleFloorMissing", StringComparison.Ordinal) && (await GuardAsync(g2)).StartsWith("False ContentAcceptanceRuleFloorMissing", StringComparison.Ordinal));
        {
            var exposure = await Outcome(WriteAsync(ra, org.Records[8], Variant.Valid));
            Record("D-M1d", "documented exposure R-CARV-21: an instance configured as Release A is bound only by the database floor, so it writes while the floor is missing", "OK (Release A has no write fence)", exposure, exposure == "OK");
        }

        {
            var noFlag = await HostCommandAsync(adminCs, "m2d-floor2-reinstate-noflag", "--apply-content-acceptance-floor", "2");
            var reinstated = await HostCommandAsync(adminCs, "m2d-floor2-reinstate", "--apply-content-acceptance-floor", "2", "--reinstate-recorded-floor");
            var write = await Outcome(WriteAsync(rb2, org.Records[7], Variant.Valid));
            Record("D-M2", "recovery: the command reports the recorded-but-missing floor (exit 4), reinstates it only with the explicit flag, then writes resume", "exit 4 | exit 0; floors 2; required-2 write OK",
                $"exit {noFlag.Code} | exit {reinstated.Code}; floors {await FloorAsync()}; write {write}", noFlag.Code == 4 && reinstated.Code == 0 && await FloorAsync() == "2" && write == "OK");
        }

        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2, ADD CONSTRAINT ck_content_operation_rule_floor_v2 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID");
            await RefusedWriteAsync("D-M3", "floor 2 weakened in place (same name, bound >= 1): refused as a definition mismatch", rb2, org, org.Records[7], ContentAcceptanceFloorFailures.FloorDefinitionMismatch);
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2");
            var restored = await HostCommandAsync(adminCs, "m2d-floor2-restore-1", "--apply-content-acceptance-floor", "2", "--reinstate-recorded-floor");
            Log($"D-M3 restore: exit {restored.Code}; floors {await FloorAsync()}");
        }

        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2, ADD CONSTRAINT ck_content_operation_rule_floor_v1 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID");
            await RefusedWriteAsync("D-M4a", "floor 2 replaced by an exact floor 1 (a lowering): a write requiring floor 1 is refused (floor 2 recorded but missing)", rb1, org, org.Records[7], ContentAcceptanceFloorFailures.FloorRecordedButMissing);
            await RefusedWriteAsync("D-M4b", "the same lowering: a write requiring floor 2 is refused (floor lowered)", rb2, org, org.Records[7], ContentAcceptanceFloorFailures.FloorLowered);
            var reinstated = await HostCommandAsync(adminCs, "m2d-floor2-restore-2", "--apply-content-acceptance-floor", "2", "--reinstate-recorded-floor");
            var write = await Outcome(WriteAsync(rb2, org.Records[7], Variant.Valid));
            Record("D-M4c", "reinstatement restores floor 2 and drops the substituted floor 1; writes resume", "exit 0; floors 2; OK", $"exit {reinstated.Code}; floors {await FloorAsync()}; {write}", reinstated.Code == 0 && await FloorAsync() == "2" && write == "OK");
        }

        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations ADD CONSTRAINT ck_content_operation_rule_floor_v1 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID");
            await RefusedWriteAsync("D-M5", "floor 1 added beside floor 2 (two floor constraints): refused as an unexpected floor set", rb1, org, org.Records[7], ContentAcceptanceFloorFailures.FloorDefinitionMismatch);
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1");
        }

        {
            await ExecAsync(adminCs, $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor2Sql.MigrationId}'");
            await RefusedWriteAsync("D-M6", "floor 2 present but its history row deleted: refused (inconsistent migration history)", rb2, org, org.Records[7], ContentAcceptanceFloorFailures.FloorNotRecorded);
            await ExecAsync(adminCs, $"INSERT INTO authority.ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('{ContentAcceptanceFloor2Sql.MigrationId}', '10.0.13')");
            var write = await Outcome(WriteAsync(rb2, org.Records[7], Variant.Valid));
            Record("D-M7", "after the catalog is put back exactly, readiness and writes recover; the live verdict is none", "OK; True ...requiredFloor=2; none",
                $"{write}; {await GuardAsync(g2)}; {await LiveVerdictAsync(2)}", write == "OK" && await GuardAsync(g2) == "True content acceptance rules v2 (floor 2); phase=ReleaseB requiredFloor=2" && await LiveVerdictAsync(2) == "none");
        }

        Record("D-99", "operation inventory of the run (for the record); no Committed operation has P <> C", "every Committed row P = C", await OpsSummaryAsync(),
            await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name='Committed' AND prepare_acceptance_rule_version IS DISTINCT FROM commit_acceptance_rule_version") == 0);
    }

    // ------------------------------------------------------------------------------------------------ model
    private sealed record Check(string Id, string Description, string Expected, string Observed, bool Pass);

    internal enum Variant
    {
        Valid,
        NewV2Field,
        NewV2Document,
        FieldSchema2Envelope1,
    }

    private sealed class Org(Guid id, Guid owner, Guid ownerBinding)
    {
        public Guid Id { get; } = id;
        public Guid Owner { get; } = owner;
        public Guid OwnerBinding { get; } = ownerBinding;
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
        public Guid DocumentField { get; } = Guid.CreateVersion7();
        public Guid Document { get; } = Guid.CreateVersion7();
        public List<(Staged Row, byte[] Signature)> Accepted { get; set; } = [];
        private int writes;

        public byte NextSeed() => (byte)(Interlocked.Increment(ref writes) % 200 + 10);

        public List<StagedContentItem> AcceptedStaged() => Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList();
    }

    private sealed record AuthorityNode(string Name, ContentService Service);

    private sealed record V1Prepared(string Outcome, Guid Operation, PrepareContentChangeRequest? Request, string? Key, List<Staged>? Rows, List<StagedContentItem>? Staged);

    private AuthorityNode Instance(string name, ContentAcceptancePolicy policy)
    {
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2d-runtime-" + name }.ConnectionString)!;
        var store = new AuthorityStore(Factory.For(cs), policy, null, null, alerts);
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), TimeProvider.System);
        return new AuthorityNode(name, new ContentService(store, mac, verifier, new NoFaults(), TimeProvider.System, new ShareContributorWriteSwitch(false, false)));
    }

    private ContentAcceptanceRuleGuard Guard(ContentAcceptancePolicy policy)
    {
        var marked = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2d-guard" }.ConnectionString)!;
        return new ContentAcceptanceRuleGuard(new ContentAcceptanceRuleSchemaReader(Factory.For(marked)), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance, policy);
    }

    private async Task<Org> SeedOrgAsync(int records)
    {
        var owner = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();
        var org = new Org(Guid.CreateVersion7(), owner, binding);
        var keyName = "m2d-key-" + org.Id.ToString("N");
        var seedCs = new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "m2d-seed" }.ConnectionString;
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var now = DateTimeOffset.UtcNow;
        context.OrganizationStates.Add(new OrganizationAuthorizationState { OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org.Id, OrganizationDisplayName = "M2d gate", AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous, StateModifiedOn = now });
        context.IdentityBindings.Add(new IdentityBinding { IdentityBindingId = binding, OrganizationId = org.Id, AuthorityUserId = owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = owner.ToString("D"), IdentityBindingEnabled = true, IdentityCreatedOn = now });
        context.KeyRegistrations.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = org.Id, ProviderName = "LocalDevelopment", ProviderKeyName = keyName, CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1, KeyRegisteredOn = now, KeyModifiedOn = now });
        for (var i = 0; i < records; i++)
        {
            var record = new RecordModel(org, Guid.CreateVersion7());
            org.Records.Add(record);
            context.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, ParentResourceEntityId = Fixtures.Form, RegisteredBySecurityCommandId = Guid.CreateVersion7(), ResourceRegisteredOn = now });
            context.ContentHeads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = org.Id, FormRecordId = record.Id, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1, ContentHeadModifiedOn = now });
            context.RelationshipEdges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner, SubjectRelationName = string.Empty, CreatedBySecurityCommandId = Guid.CreateVersion7(), RelationshipCreatedOn = now });
        }

        await context.SaveChangesAsync();
        return org;
    }

    // ------------------------------------------------------------------------------------------------ protocol drivers
    private static VerifiedActor Actor(Org org) => new(Issuer, org.Owner.ToString("D"), "m2d-harness");

    private async Task<(PrepareContentChangeRequest Request, List<Staged> Rows)> BuildRequestAsync(RecordModel record, Guid operation, Variant variant)
    {
        var head = await HeadAsync(record);
        var seed = record.NextSeed();
        var rows = Fixtures.Candidate(record.Org.Id, record.Id, record.TextField, record.TextItem, record.SealedField, record.SealedItem, record.DocumentField, record.Document, seed, variant)
            .Select(r => Fixtures.Encode(r, record.Org.Id, operation, head.Revision + 1)).ToList();
        var request = new PrepareContentChangeRequest(record.Org.Id, record.Id, operation, 7, head.Revision, Convert.ToBase64String(head.Root),
            rows.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: record.Accepted.Count == 0 ? null : record.AcceptedStaged());
        return (request, rows);
    }

    private async Task<(PreparedContentOperation Prepared, List<Staged> Rows)> PrepareAsync(AuthorityNode instance, RecordModel record, Variant variant)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation, variant);
        var prepared = await instance.Service.PrepareAsync(Actor(record.Org), request, "m2d-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
        return (prepared, rows);
    }

    private static List<StagedContentItem> Staged(PreparedContentOperation prepared, List<Staged> rows)
    {
        var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
        return rows.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
    }

    private async Task WriteAsync(AuthorityNode instance, RecordModel record, Variant variant)
    {
        var (prepared, rows) = await PrepareAsync(instance, record, variant);
        var staged = Staged(prepared, rows);
        await instance.Service.CommitAsync(Actor(record.Org), prepared.OperationId, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None);
        record.Accepted = rows.Select(c => (c, Convert.FromBase64String(staged.Single(s => s.ItemKind == c.Kind && s.ItemId == c.ItemId).RowSignatureBase64))).ToList();
    }

    /// <summary>A Release B write refused by the write fence: the outcome, the alert's reason, and the unchanged record and organization snapshots.</summary>
    private async Task RefusedWriteAsync(string id, string description, AuthorityNode instance, Org org, RecordModel record, string reason)
    {
        var before = await SnapshotAsync(org, record);
        var global = await GlobalSnapshotAsync(org);
        var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
        var raised = alerts.Count;
        var outcome = await Outcome(WriteAsync(instance, record, Variant.Valid));
        var alert = alerts.Since(raised);
        Record(id, description, $"{NotEnforced}; alert reason={reason}; no operation; snapshots unchanged",
            $"{outcome}; alert {alert}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; {Diff(before, await SnapshotAsync(org, record))} | {Diff(global, await GlobalSnapshotAsync(org))}",
            outcome == NotEnforced && alert.Contains("reason=" + reason + " ", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations")
            && before == await SnapshotAsync(org, record) && global == await GlobalSnapshotAsync(org));
    }

    // ---- the version-1 build (separate process) ----
    private async Task<V1Prepared> V1PrepareAsync(RecordModel record, Variant variant, int clockOffsetMinutes = 0)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation, variant);
        var key = "m2d-v1-" + operation.ToString("N");
        var output = await RunV1Async("prepare", new { Issuer, Subject = record.Org.Owner.ToString("D"), Request = request, Key = key, ClockOffsetMinutes = clockOffsetMinutes });
        if (!output.GetProperty("Ok").GetBoolean())
        {
            return new V1Prepared(V1Failure(output), operation, request, key, rows, null);
        }

        var prepared = output.GetProperty("Result").Deserialize<PreparedContentOperation>()!;
        return new V1Prepared("OK", prepared.OperationId, request, key, rows, Staged(prepared, rows));
    }

    private async Task<string> V1CommitAsync(RecordModel record, Guid operation, List<StagedContentItem> staged, int clockOffsetMinutes = 0)
    {
        var output = await RunV1Async("commit", new { Issuer, Subject = record.Org.Owner.ToString("D"), OperationId = operation, Request = new CommitContentChangeRequest(record.Org.Id, record.Id, staged), ClockOffsetMinutes = clockOffsetMinutes });
        return output.GetProperty("Ok").GetBoolean() ? "OK" : V1Failure(output);
    }

    /// <summary>The version-1 build's error and the alerts it raised (its store names a refusing fence constraint only in its alert).</summary>
    private static string V1Failure(JsonElement output)
    {
        var alertsRaised = output.TryGetProperty("Alerts", out var a) ? string.Join(" | ", a.EnumerateArray().Select(x => x.GetString())) : string.Empty;
        return (output.GetProperty("Error").GetString() ?? "error") + (alertsRaised.Length == 0 ? string.Empty : " ## alerts: " + alertsRaised);
    }

    private async Task<JsonElement> RunV1Async(string command, object input)
    {
        var dir = Path.Combine(evidenceDir, "v1-calls");
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:HHmmssfff}-{command}-{Guid.CreateVersion7():N}";
        var inPath = Path.Combine(dir, name + ".in.json");
        var outPath = Path.Combine(dir, name + ".out.json");
        await File.WriteAllTextAsync(inPath, JsonSerializer.Serialize(input));
        var start = new ProcessStartInfo(v1Agent) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(inPath);
        start.ArgumentList.Add(outPath);
        start.Environment["M2D_V1_RUNTIME"] = runtimeCs;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the version-1 agent");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = JsonDocument.Parse(await File.ReadAllTextAsync(outPath)).RootElement;
        Log($"v1 {command} exit {process.ExitCode}: ok={output.GetProperty("Ok")} error={(output.TryGetProperty("Error", out var e) ? e.ToString() : "")} mapped={(output.TryGetProperty("Mapped", out var m) ? m.ToString() : "")} build={(output.TryGetProperty("BuildRuleVersion", out var b) ? b.ToString() : "")} {(await stderr).Trim()}");
        _ = await stdout;
        return output;
    }

    /// <summary>The controlled test-only mechanism (as in the S-4 gate): a signed Prepared operation recorded without running Prepare.</summary>
    private async Task<(Guid Operation, List<StagedContentItem> Staged, string Seeded)> SeedPreparedAsync(Org org, RecordModel record, Variant variant, int? prepareRuleVersion)
    {
        var operation = Guid.CreateVersion7();
        var head = await HeadAsync(record);
        var revision = head.Revision + 1;
        var rows = Fixtures.Candidate(org.Id, record.Id, record.TextField, record.TextItem, record.SealedField, record.SealedItem, record.DocumentField, record.Document, record.NextSeed(), variant)
            .Select(r => Fixtures.Encode(r, org.Id, operation, revision)).ToList();
        var key = new IntegrityKeyReference(org.Id, IntegrityKeyPurposes.RowIntegrityV1, "m2d-key-" + org.Id.ToString("N"), 1);
        var signed = new List<(Staged Item, byte[] Signature)>();
        foreach (var item in rows)
        {
            signed.Add((item, (await mac.SignAsync(key, item.Bytes, CancellationToken.None)).Mac));
        }

        var manifest = MerkleManifestV1.Build(signed.Select(s => new MerkleLeafInput((MerkleItemKind)(byte)s.Item.Kind, s.Item.ItemId, s.Signature, SHA256.HashData(s.Item.Bytes), (ulong)revision, operation)));
        var now = DateTimeOffset.UtcNow;
        var seeded = "OK";
        try
        {
            await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "m2d-seed-operation" }.ConnectionString).Options);
            context.ContentOperations.Add(new ContentChangeOperation
            {
                ContentChangeOperationId = operation, OrganizationId = org.Id, FormRecordId = record.Id, ActorIdentityBindingId = org.OwnerBinding,
                OperationIdempotencyKey = "m2d-seeded-" + operation.ToString("N"), OperationRequestDigest = SHA256.HashData(operation.ToByteArray()),
                ExpectedAuthorizationRevision = 7, ExpectedContentRevision = head.Revision, CandidateContentRevision = revision, CandidateKeyVersion = 1,
                CandidateManifestRoot = manifest.MerkleRoot, CandidateLeafIndexDigest = manifest.LeafIndexDigest, CandidateLeafCount = (long)manifest.LeafCount,
                OperationStateName = ContentOperationStates.Prepared, CorrelationIdentifier = Guid.CreateVersion7(), OperationCreatedOn = now, OperationExpiresOn = now.AddMinutes(10),
                PrepareAcceptanceRuleVersion = prepareRuleVersion,
            });
            context.CandidateLeaves.AddRange(manifest.SortedLeaves.Select((leaf, i) => new ContentCandidateLeaf
            {
                ContentCandidateLeafId = Guid.CreateVersion7(), ContentChangeOperationId = operation, ManifestItemKey = leaf.ItemKey, ManifestItemKindCode = (byte)leaf.Kind,
                ManifestItemId = leaf.ItemId, MutationKindName = ContentMutationKinds.Update, CanonicalPayloadDigest = leaf.CanonicalPayloadDigest.ToArray(),
                RowSignature = leaf.RowSignature.ToArray(), LeafDigest = manifest.LeafHashes[i],
            }));
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            seeded = Describe(ex);
        }

        Log($"seed {variant} P={prepareRuleVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"} op={operation}: {seeded}");
        return (operation, signed.Select(s => new StagedContentItem(s.Item.Kind, s.Item.ItemId, s.Item.Base64, Convert.ToBase64String(s.Signature))).ToList(), seeded);
    }

    // ------------------------------------------------------------------------------------------------ evidence helpers
    private static async Task<string> GuardAsync(ContentAcceptanceRuleGuard guard)
    {
        var (ready, detail) = await guard.RefreshAsync(CancellationToken.None);
        return $"{ready} {detail}";
    }

    private async Task<string> LiveVerdictAsync(int required)
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        var facts = await ContentAcceptanceWriteFence.ReadAsync(connection, null, required, CancellationToken.None);
        return ContentAcceptanceWriteFence.Evaluate(facts, required) ?? "none";
    }

    private async Task<(int Code, string Output)> HostCommandAsync(string migrationCs, string application, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(newBinary);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["ConnectionStrings__AuthorityDbMigration"] = new NpgsqlConnectionStringBuilder(migrationCs) { ApplicationName = application }.ConnectionString;
        start.Environment["ContentAcceptance__EnforcementPhase"] = "ReleaseA";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + string.Join(' ', arguments));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = ((await stdout) + " " + (await stderr)).Replace('\n', ' ').Replace('\r', ' ').Trim();
        Log($"{string.Join(' ', arguments)} ({application}) exit {process.ExitCode}: {(output.Length > 500 ? output[^500..] : output)}");
        return (process.ExitCode, output);
    }

    private async Task<string> FloorAsync() => await ScalarAsync(adminCs, FloorQuery);

    private async Task<string> RoleFactsAsync() => await ScalarAsync(adminCs, "SELECT rolsuper||' '||rolbypassrls||' '||(SELECT tableowner = 'authority_runtime' FROM pg_tables WHERE schemaname='authority' AND tablename='content_change_operations') FROM pg_roles WHERE rolname='authority_runtime'");

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
          'head='||(SELECT committed_content_revision||':'||encode(committed_manifest_root,'hex')||':'||coalesce(committed_operation_id::text,'none')||':'||content_head_quarantined FROM authority.record_content_heads WHERE form_record_id='{record.Id}'),
          'commits='||(SELECT count(*) FROM authority.record_manifest_commits WHERE form_record_id='{record.Id}'),
          'leaves='||(SELECT count(*) FROM authority.record_manifest_leaves l JOIN authority.record_manifest_commits c ON c.record_manifest_commit_id=l.record_manifest_commit_id WHERE c.form_record_id='{record.Id}'),
          'identities='||(SELECT count(*) FROM authority.content_item_identities WHERE organization_id='{org.Id}' AND form_record_id='{record.Id}'),
          'committedOps='||(SELECT count(*) FROM authority.content_change_operations WHERE form_record_id='{record.Id}' AND operation_state_name='Committed'))
        """);

    private async Task<string> GlobalSnapshotAsync(Org org) => await ScalarAsync(adminCs, $"""
        SELECT concat_ws('; ',
          'contentIntegrityRevision='||(SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id='{org.Id}'),
          'authorizationRevision='||(SELECT authorization_revision FROM authority.organization_authorization_states WHERE organization_id='{org.Id}'),
          'checkpoints='||(SELECT count(*) FROM authority.content_integrity_checkpoints WHERE organization_id='{org.Id}'),
          'journal='||(SELECT count(*) FROM authority.security_journal_entries WHERE organization_id='{org.Id}'),
          'commits='||(SELECT count(*) FROM authority.record_manifest_commits WHERE organization_id='{org.Id}'),
          'identities='||(SELECT count(*) FROM authority.content_item_identities WHERE organization_id='{org.Id}'))
        """);

    private static string Diff(string before, string after) => before == after ? "unchanged" : "CHANGED " + before + " -> " + after;

    private async Task<string> StampsAsync(Guid operationId) => await ScalarAsync(adminCs, $"SELECT coalesce(prepare_acceptance_rule_version::text,'NULL')||'/'||coalesce(commit_acceptance_rule_version::text,'NULL')||' '||operation_state_name FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private async Task<string> ReasonAsync(Guid operationId) => await ScalarAsync(adminCs, $"SELECT coalesce(operation_quarantine_reason_code, 'NULL') FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private async Task<string> OpsSummaryAsync() => await ScalarAsync(adminCs, "SELECT coalesce(string_agg(s, ', ' ORDER BY s), 'none') FROM (SELECT operation_state_name||' P'||coalesce(prepare_acceptance_rule_version::text,'NULL')||'/C'||coalesce(commit_acceptance_rule_version::text,'NULL')||coalesce(' '||operation_quarantine_reason_code,'')||'='||count(*) AS s FROM authority.content_change_operations GROUP BY operation_state_name, prepare_acceptance_rule_version, commit_acceptance_rule_version, operation_quarantine_reason_code) q");

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
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return "OK";
        }
        catch (PostgresException ex)
        {
            return $"{ex.SqlState} {ex.ConstraintName ?? ex.Routine}: {ex.MessageText}";
        }
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

    private static async Task<(string Outcome, string Message)> OutcomeWithMessage(Task task)
    {
        try
        {
            await task;
            return ("OK", string.Empty);
        }
        catch (Exception ex)
        {
            return (Describe(ex), ex.Message);
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

internal sealed class NoAlerts : ISecurityAlertSink
{
    public void Raise(SecurityAlert alert)
    {
    }
}

internal sealed class CapturingAlerts : ISecurityAlertSink
{
    private readonly List<SecurityAlert> raised = [];

    public int Count
    {
        get
        {
            lock (raised)
            {
                return raised.Count;
            }
        }
    }

    /// <summary>Every alert raised after <paramref name="index"/>, flattened to text (kind and details), for the reason assertions.</summary>
    public string Since(int index)
    {
        lock (raised)
        {
            return string.Join(" || ", raised.Skip(index).Select(a => a.ToString()));
        }
    }

    public void Raise(SecurityAlert alert)
    {
        lock (raised)
        {
            raised.Add(alert);
        }
    }
}

internal sealed class NoFaults : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256 (shared with the version-1 agent; never a production provider).</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    private int verifications;
    private int signatures;

    public int Verifications => Volatile.Read(ref verifications);

    public int Signatures => Volatile.Read(ref signatures);

    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"m2d-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref signatures);
        return Task.FromResult(new IntegrityMacResult(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), keyReference.KeyVersion, "HMAC-SHA-256"));
    }

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref verifications);
        return Task.FromResult(expectedMac.Length == 32 && CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), expectedMac.Span));
    }

    public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => Task.FromResult(1);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "test"));

    public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public class ThrowingProxy : System.Reflection.DispatchProxy
{
    public static T Create<T>()
        where T : class => Create<T, ThrowingProxy>();

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name + " is not used by the content protocol");
}

internal sealed record Staged(Fixtures.Row Row, Guid StorageKey, byte[] Bytes, ManifestItemKind Kind, Guid ItemId)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

/// <summary>Canonical rows shaped like the business registry (schema 1 and schema 2 of both envelope kinds).</summary>
internal static class Fixtures
{
    public static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();
    private static readonly DateTimeOffset Fixed = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

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

    private static readonly RowSchemaDefinition Attachments = new("file_attachment_versions", 1,
    [
        new("ciphertext_sha256", RowColumnType.Bytes, false), new("ciphertext_size_bytes", RowColumnType.Int64, false), new("content_change_operation_id", RowColumnType.Uuid, false),
        new("detected_content_type", RowColumnType.Text, false), new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_prefix", RowColumnType.Bytes, false), new("encryption_segment_size_bytes", RowColumnType.Int32, false),
        new("field_definition_id", RowColumnType.Uuid, false), new("file_attachment_id", RowColumnType.Uuid, false), new("form_record_id", RowColumnType.Uuid, false),
        new("original_file_name", RowColumnType.Text, false), new("plaintext_sha256", RowColumnType.Bytes, false), new("plaintext_size_bytes", RowColumnType.Int64, false),
        new("scan_signature_name", RowColumnType.Text, true), new("scan_verdict", RowColumnType.Text, false), new("scanned_on", RowColumnType.TimestampUtc, false),
        new("scanner_name", RowColumnType.Text, false), new("storage_object_key", RowColumnType.Text, false), new("storage_provider_name", RowColumnType.Text, false),
        new("tenant_id", RowColumnType.Uuid, false), new("uploaded_by_user_id", RowColumnType.Uuid, false), new("uploaded_on", RowColumnType.TimestampUtc, false),
        new("version_number", RowColumnType.Int32, false), new("wrapped_data_key", RowColumnType.Bytes, false),
    ]);

    private static readonly RowSchemaDefinition EncryptedValuesV2 = WithV2Columns(EncryptedValues);
    private static readonly RowSchemaDefinition AttachmentsV2 = WithV2Columns(Attachments);

    private static RowSchemaDefinition WithV2Columns(RowSchemaDefinition schema1) => new(schema1.EntityKind, 2,
        schema1.Columns.Concat([
            new RowSignedColumn("data_key_commitment_bytes", RowColumnType.Bytes, false),
            new RowSignedColumn("data_key_identifier", RowColumnType.Uuid, false),
            new RowSignedColumn("key_wrap_algorithm_code", RowColumnType.Int32, false),
        ]).OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    public sealed record Row(ManifestItemKind Kind, Guid ItemId, RowSchemaDefinition Schema, Dictionary<string, RowValue?> Values, bool IdentityPrimaryKey);

    public static Staged Encode(Row row, Guid organizationId, Guid operation, long revision)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(operation) };
        var primaryKey = row.IdentityPrimaryKey ? row.ItemId : Guid.CreateVersion7();
        var bytes = RowCanonicalizerV1.Encode(row.Schema, new RowSigningContext(1, organizationId, primaryKey, (ulong)revision), values);
        return new Staged(row, primaryKey, bytes, row.Kind, row.ItemId);
    }

    /// <summary>Header, a text value, an encrypted field value (fresh envelope per seed) and an encrypted document; the variant re-encodes one envelope row.</summary>
    public static List<Row> Candidate(Guid org, Guid record, Guid textField, Guid textItem, Guid sealedField, Guid sealedItem, Guid documentField, Guid document, byte seed, Lab.Variant variant)
    {
        var sealedRow = Sealed(org, record, sealedField, sealedItem, seed);
        var documentRow = Attachment(org, record, documentField, document);
        switch (variant)
        {
            case Lab.Variant.NewV2Field: sealedRow = AsV2(sealedRow, EncryptedValuesV2, org, "field-value"); break;
            case Lab.Variant.NewV2Document: documentRow = With(AsV2(documentRow, AttachmentsV2, org, "document"), "ciphertext_size_bytes", RowValue.Int64(1020)); break;
            case Lab.Variant.FieldSchema2Envelope1: sealedRow = With(AsV2(sealedRow, EncryptedValuesV2, org, "field-value"), "encryption_envelope_version", RowValue.Int32(1)); break;
        }

        return [Header(org, record), Text(org, record, textField, textItem, "value-" + seed), sealedRow, documentRow];
    }

    private static Row With(Row row, string column, RowValue? value) => row with { Values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { [column] = value } };

    /// <summary>A well-formed Envelope v2 / schema 2 row under the v2 rules (canonical v2 reference, wrap code 1, UUIDv7 id, 32-byte commitment).</summary>
    private static Row AsV2(Row row, RowSchemaDefinition schema2, Guid org, string purpose)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal)
        {
            ["encryption_envelope_version"] = RowValue.Int32(2),
            ["encryption_key_reference"] = RowValue.Text($"localdev:{org:D}:{purpose}:1"),
            ["data_key_commitment_bytes"] = RowValue.Bytes(Fill(32, 0xdd)),
            ["data_key_identifier"] = RowValue.Uuid(Guid.CreateVersion7()),
            ["key_wrap_algorithm_code"] = RowValue.Int32(1),
        };
        return row with { Schema = schema2, Values = values };
    }

    private static Row Header(Guid org, Guid record) => new(ManifestItemKind.RecordHeader, record, HeaderV3, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["created_by_user_id"] = RowValue.Uuid(Creator), ["created_form_version_id"] = RowValue.Uuid(Version), ["form_definition_id"] = RowValue.Uuid(Form),
        ["form_relationship_definition_id"] = null, ["parent_form_record_id"] = null, ["record_status"] = RowValue.Text("Draft"),
        ["relationship_sort_order"] = null, ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(org),
    }, IdentityPrimaryKey: true);

    private static Row Text(Guid org, Guid record, Guid field, Guid item, string value) => new(ManifestItemKind.FieldValue, item, StringValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = null,
        ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["string_value"] = RowValue.Text(value),
    }, IdentityPrimaryKey: false);

    private static Row Sealed(Guid org, Guid record, Guid field, Guid item, byte seed) => new(ManifestItemKind.FieldValue, item, EncryptedValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["encrypted_logical_type"] = RowValue.Text("String"), ["encrypted_value_bytes"] = RowValue.Bytes(Fill(24, seed)), ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"),
        ["encryption_auth_tag_bytes"] = RowValue.Bytes(Fill(16, (byte)(seed + 1))), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_bytes"] = RowValue.Bytes(Fill(12, (byte)(seed + 2))), ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item),
        ["form_record_group_id"] = null, ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["wrapped_data_key_bytes"] = RowValue.Bytes(Fill(60, (byte)(seed + 3))),
    }, IdentityPrimaryKey: false);

    private static Row Attachment(Guid org, Guid record, Guid field, Guid attachment) => new(ManifestItemKind.Attachment, attachment, Attachments, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["ciphertext_sha256"] = RowValue.Bytes(Fill(32, 7)), ["ciphertext_size_bytes"] = RowValue.Int64(1024), ["detected_content_type"] = RowValue.Text("application/pdf"),
        ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_prefix"] = RowValue.Bytes(Fill(8, 3)), ["encryption_segment_size_bytes"] = RowValue.Int32(1048576), ["field_definition_id"] = RowValue.Uuid(field),
        ["file_attachment_id"] = RowValue.Uuid(attachment), ["form_record_id"] = RowValue.Uuid(record), ["original_file_name"] = RowValue.Text("statement.pdf"),
        ["plaintext_sha256"] = RowValue.Bytes(Fill(32, 9)), ["plaintext_size_bytes"] = RowValue.Int64(1000), ["scan_signature_name"] = null, ["scan_verdict"] = RowValue.Text("Clean"),
        ["scanned_on"] = RowValue.Timestamp(Fixed), ["scanner_name"] = RowValue.Text("clamav"), ["storage_object_key"] = RowValue.Text("objects/" + attachment.ToString("N")),
        ["storage_provider_name"] = RowValue.Text("azurite"), ["tenant_id"] = RowValue.Uuid(org), ["uploaded_by_user_id"] = RowValue.Uuid(Creator),
        ["uploaded_on"] = RowValue.Timestamp(Fixed), ["version_number"] = RowValue.Int32(1), ["wrapped_data_key"] = RowValue.Bytes(Fill(60, 5)),
    }, IdentityPrimaryKey: false);

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
}
