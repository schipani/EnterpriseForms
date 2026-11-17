// E3-M2e-1 dark data-key ledger focused real-PostgreSQL gate (docs/security/E3_M2E_CONTRACT_CLOSURE.md; implementation record
// docs/security/E3_M2E1_IMPLEMENTATION_RECORD.md). Derived from the E3-M2d gate (HarnessD): its model, drivers, evidence helpers and
// fixtures are reused; the scenarios are new.
//
// Runs the REAL Authority code of this working tree (Release build, M2eBinaries; ContentAcceptanceRules.CurrentVersion = 3)
// in-process against ONE disposable PostgreSQL 18 container: this build's own --migrate and --apply-content-acceptance-floor
// (separate processes, migration identity), and ContentService + EF AuthorityStore instances (Release A, Release B with required
// floor 2 and 3) on the non-superuser runtime role, with Envelope v2 acceptance OFF (never enabled by this gate). Two frozen builds run
// in separate processes (LegacyAgent): the CARV-1 build 8915f77 (E3-M2c) for the C-14 downgrade evidence and the CARV-2 build a1127a1
// (E3-M2d) for the floor-3 cross-build check.
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var lab = new Lab(
    Environment.GetEnvironmentVariable("M2E_NEWBIN") ?? throw new InvalidOperationException("M2E_NEWBIN"),
    Environment.GetEnvironmentVariable("M2E_CARV1AGENT") ?? throw new InvalidOperationException("M2E_CARV1AGENT"),
    Environment.GetEnvironmentVariable("M2E_CARV2AGENT") ?? throw new InvalidOperationException("M2E_CARV2AGENT"),
    Environment.GetEnvironmentVariable("M2E_ADMIN") ?? throw new InvalidOperationException("M2E_ADMIN"),
    Environment.GetEnvironmentVariable("M2E_RUNTIME") ?? throw new InvalidOperationException("M2E_RUNTIME"),
    Environment.GetEnvironmentVariable("M2E_EVIDENCE") ?? throw new InvalidOperationException("M2E_EVIDENCE"));
return await lab.RunAsync();

internal sealed partial class Lab(string newBinary, string carv1Agent, string carv2Agent, string adminCs, string runtimeCs, string evidenceDir)
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
            await (Mode == "fe1" ? FloorDeadlockScenariosAsync() : ScenariosAsync()); // F-E1: M2E_MODE=fe1 runs FloorDeadlockScenarios.cs
        }
        catch (Exception ex)
        {
            Log("HARNESS ABORTED: " + Describe(ex) + " " + ex);
            checks.Add(new Check("HARNESS", "harness completed", "no unexpected exception", Describe(ex), false));
        }

        var failed = checks.Count(c => !c.Pass);
        var json = JsonSerializer.Serialize(new
        {
            harness = "E3-M2e-1 dark data-key ledger focused real-PostgreSQL gate (real Authority build in-process, acceptance off; CARV-1 build 8915f77 and CARV-2 build a1127a1 through LegacyAgent)",
            build = "Release build of the working tree (new-sha256.txt); ContentAcceptanceRules.CurrentVersion = " + ContentAcceptanceRules.CurrentVersion
                    + "; RecognizedFloors = " + string.Join(",", ContentAcceptanceRules.RecognizedFloors)
                    + "; EnvelopeRules.AllowedEnvelopeVersions = " + string.Join(",", EnvelopeRules.AllowedEnvelopeVersions),
            postgres = await ScalarAsync(adminCs, "SELECT version()"),
            checksTotal = checks.Count,
            checksFailed = failed,
            checks,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, (Mode == "fe1" ? "fe1" : "m2e1") + "-evidence.json"), json);
        await File.WriteAllLinesAsync(Path.Combine(evidenceDir, (Mode == "fe1" ? "fe1" : "m2e1") + "-log.txt"), log);
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
        // ================================================================ L: the data-key ledger migration and its database invariants
        var migrated = await HostCommandAsync(adminCs, "m2e-migrate", "--migrate");
        Record("E-00", "setup: this build's ordinary --migrate applies the CARV fence and the data-key ledger migration and never a floor (AM-1)", $"exit 0; floors none; floor history none; ledger history {ContentDataKeyLedgerSql.MigrationId}",
            $"exit {migrated.Code}; floors {await FloorAsync()}; floor history {await ScalarAsync(adminCs, FloorHistoryQuery)}; ledger history {await ScalarAsync(adminCs, LedgerHistoryQuery)}",
            migrated.Code == 0 && await FloorAsync() == "none" && await ScalarAsync(adminCs, FloorHistoryQuery) == "none" && await ScalarAsync(adminCs, LedgerHistoryQuery) == ContentDataKeyLedgerSql.MigrationId);

        var renderings = await LedgerRenderingsAsync();
        Record("E-01", "the ledger's unique index and P-1/P-2 triggers render exactly as the L-13 verification expects (PostgreSQL 18 renderings, R-CARV-23); the live verdict is none",
            "renderings equal the constants; verdict none", $"{(renderings.Equal ? "renderings equal" : "DIFFER: " + renderings.Live)}; verdict {await LedgerVerdictAsync()}", renderings.Equal && await LedgerVerdictAsync() == "none");

        var grants = await ScalarAsync(adminCs, "SELECT 'select='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','SELECT')::text||' insert='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','INSERT')::text||' update='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','UPDATE')::text||' delete='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','DELETE')::text||' truncate='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','TRUNCATE')::text");
        Record("E-02", "runtime grants on the ledger (L-8): SELECT and INSERT only", "select=true insert=true update=false delete=false truncate=false", grants,
            grants == "select=true insert=true update=false delete=false truncate=false");

        var org = await SeedOrgAsync(30);
        var other = await SeedOrgAsync(4);
        var ra = Instance("RA", ReleaseA);

        // A Prepared operation stamped 3 (the binding guard admits only those) and a valid binding inserted by the runtime role itself.
        var seededP3 = await SeedPreparedAsync(org, org.Records[0], Variant.Valid, 3);
        var reservedId = Guid.CreateVersion7();
        Record("E-03", "P-1 admits a binding inserted by the runtime role for a Prepared operation of the same organization and record stamped 3", "OK",
            await ExecAsync(runtimeCs, BindingSql(reservedId, org.Id, org.Records[0].Id, seededP3.Operation)), await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[0].Id, seededP3.Operation)) == "OK");
        var runtimeUpdate = await ExecAsync(runtimeCs, $"UPDATE authority.content_data_key_bindings SET binding_item_count = 2 WHERE data_key_identifier = '{reservedId}'");
        var runtimeDelete = await ExecAsync(runtimeCs, $"DELETE FROM authority.content_data_key_bindings WHERE data_key_identifier = '{reservedId}'");
        var runtimeTruncate = await ExecAsync(runtimeCs, "TRUNCATE authority.content_data_key_bindings");
        Record("E-04", "the runtime role cannot UPDATE, DELETE or TRUNCATE a binding (no privilege, L-8)", "42501 x3", $"{runtimeUpdate} | {runtimeDelete} | {runtimeTruncate}",
            runtimeUpdate.StartsWith("42501", StringComparison.Ordinal) && runtimeDelete.StartsWith("42501", StringComparison.Ordinal) && runtimeTruncate.StartsWith("42501", StringComparison.Ordinal));
        var adminUpdate = await ExecAsync(adminCs, $"UPDATE authority.content_data_key_bindings SET binding_item_count = 2 WHERE data_key_identifier = '{reservedId}'");
        var adminDelete = await ExecAsync(adminCs, $"DELETE FROM authority.content_data_key_bindings WHERE data_key_identifier = '{reservedId}'");
        var adminTruncate = await ExecAsync(adminCs, "TRUNCATE authority.content_data_key_bindings");
        Record("E-05", "P-2: even the table owner / superuser is refused UPDATE, DELETE and TRUNCATE by the immutability triggers", "23514 trg_content_data_key_binding_permanent x3; binding intact",
            $"{adminUpdate} | {adminDelete} | {adminTruncate}; bindings for the id {await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{reservedId}' AND binding_item_count = 1")}",
            new[] { adminUpdate, adminDelete, adminTruncate }.All(o => o.StartsWith("23514 trg_content_data_key_binding_permanent", StringComparison.Ordinal))
            && await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{reservedId}' AND binding_item_count = 1") == 1);

        // P-1 refusals.
        await WriteAsync(ra, org.Records[1], Variant.Valid); // a Committed operation stamped 3
        var committedOp = Guid.Parse(await ScalarAsync(adminCs, $"SELECT content_change_operation_id FROM authority.content_change_operations WHERE form_record_id = '{org.Records[1].Id}' AND operation_state_name = 'Committed'"));
        var seededP2 = await SeedPreparedAsync(org, org.Records[2], Variant.Valid, 2);
        var abortedOp = await SeedPreparedAsync(org, org.Records[3], Variant.Valid, 3);
        await ExecAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_state_name = 'Aborted' WHERE content_change_operation_id = '{abortedOp.Operation}'");
        var p1 = new Dictionary<string, string>
        {
            ["a Committed operation"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[1].Id, committedOp)),
            ["an Aborted operation"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[3].Id, abortedOp.Operation)),
            ["a Prepared operation stamped 2"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[2].Id, seededP2.Operation)),
            ["another organization"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), other.Id, org.Records[0].Id, seededP3.Operation)),
            ["another record"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[5].Id, seededP3.Operation)),
            ["a nonexistent operation"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[0].Id, Guid.CreateVersion7())),
        };
        Record("E-06", "P-1 refuses a binding for a Committed, Aborted, version-2, other-organization, other-record or nonexistent operation (the guard fires before the foreign key)", "23514 trg_content_data_key_binding_guard x6",
            string.Join(" | ", p1.Select(p => p.Key + ": " + p.Value)), p1.Values.All(v => v.StartsWith("23514 trg_content_data_key_binding_guard", StringComparison.Ordinal)));

        var checksRefused = new Dictionary<string, string>
        {
            ["UUIDv4 identifier"] = await ExecAsync(runtimeCs, BindingSql(Guid.NewGuid(), org.Id, org.Records[0].Id, seededP3.Operation)),
            ["31-byte commitment"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[0].Id, seededP3.Operation, commitmentHex: new string('d', 62))),
            ["kind 3"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[0].Id, seededP3.Operation, kind: 3)),
            ["document with 2 items"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[0].Id, seededP3.Operation, kind: 2, count: 2)),
            ["item count 0"] = await ExecAsync(runtimeCs, BindingSql(Guid.CreateVersion7(), org.Id, org.Records[0].Id, seededP3.Operation, count: 0)),
        };
        Record("E-07", "the ledger CHECKs refuse a non-UUIDv7 identifier, a 31-byte commitment, an unknown kind, a two-row document and an empty count", "23514 ck_content_data_key_binding_* x5",
            string.Join(" | ", checksRefused.Select(p => p.Key + ": " + p.Value)), checksRefused.Values.All(v => v.StartsWith("23514 ck_content_data_key_binding_", StringComparison.Ordinal)));

        var otherP3 = await SeedPreparedAsync(other, other.Records[0], Variant.Valid, 3);
        var duplicate = await ExecAsync(runtimeCs, BindingSql(reservedId, other.Id, other.Records[0].Id, otherP3.Operation));
        Record("E-08", "global uniqueness (OD-M2e-01): another organization's operation cannot bind an identifier already reserved", "23505 uq_content_data_key_binding_identifier", duplicate,
            duplicate.StartsWith("23505 uq_content_data_key_binding_identifier", StringComparison.Ordinal));

        // ================================================================ S: the real EF store (Prepare-transaction registration, L-3, L-13)
        {
            var id = Guid.CreateVersion7();
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var outcome = await Outcome(RegisterAsync(ReleaseA, org, org.Records[6], id));
            Record("E-09", "the real store inserts an operation and its binding in one transaction: EF orders the operation first and P-1 admits the binding", "OK; operation and binding persisted",
                $"{outcome}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; binding {await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'")}",
                outcome == "OK" && await CountAsync("SELECT count(*) FROM authority.content_change_operations") == opsBefore + 1 && await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'") == 1);

            var opsAfter = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var reuse = await Outcome(RegisterAsync(ReleaseA, other, other.Records[1], id));
            Record("E-10", "L-3: a binding of an already reserved identifier from another organization rolls back the whole transaction (operation, leaves, binding) and reports the approved refusal", "AuthorityException INVALID_COMMAND 422 retryable=False; no operation persisted",
                $"{reuse}; ops {opsAfter}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}", reuse == "AuthorityException INVALID_COMMAND 422 retryable=False" && await CountAsync("SELECT count(*) FROM authority.content_change_operations") == opsAfter);
        }

        {
            // Two organizations race for one identifier: no organization lock serializes them; the unique index does.
            var id = Guid.CreateVersion7();
            var store = new AuthorityStore(Factory.For(RuntimeCs("race-a")), ReleaseA, null, null, alerts);
            await using var first = await store.BeginScopeAsync(CancellationToken.None);
            var firstOperation = NewOperation(org, org.Records[7], 3);
            first.AddOperation(firstOperation);
            first.AddDataKeyBindings(DataKeyLedgerRules.BindingsFor([new DataKeyLedgerRules.DataKeyGroup(id, 1, Fill(32, 0xdd), 1)], org.Id, org.Records[7].Id, firstOperation.ContentChangeOperationId, DateTimeOffset.UtcNow));
            await first.FlushAsync(CancellationToken.None); // the first insert holds the index entry, uncommitted
            var second = RegisterAsync(ReleaseA, other, other.Records[2], id);
            await Task.Delay(1500);
            var blocked = !second.IsCompleted;
            await first.CommitAsync(CancellationToken.None);
            var raced = await Outcome(second);
            Record("E-11", "concurrent registration of one identifier by two organizations: the second waits on the unique index, then is refused and rolled back (M2-CON-02)", "second blocked; first committed; second AuthorityException INVALID_COMMAND 422; one binding",
                $"blocked {blocked}; second {raced}; bindings {await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'")}",
                blocked && raced == "AuthorityException INVALID_COMMAND 422 retryable=False" && await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'") == 1);
        }

        Record("E-12", "L-13 on a real store scope: the ledger verification passes on the migrated schema", "OK", await Outcome(VerifyLedgerAsync(ReleaseA)), await Outcome(VerifyLedgerAsync(ReleaseA)) == "OK");
        var weakened = new Dictionary<string, (string Weaken, string Restore, string Reason)>
        {
            ["guard disabled"] = ("ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard", "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard", ContentDataKeyLedgerSql.Failures.GuardMismatch),
            ["guard body replaced"] = ("CREATE OR REPLACE FUNCTION authority.content_data_key_binding_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$ BEGIN RETURN NEW; END $$", "DROP TRIGGER trg_content_data_key_binding_guard ON authority.content_data_key_bindings; DROP FUNCTION authority.content_data_key_binding_guard(); " + ContentDataKeyLedgerSql.GuardFunctionSql + " REVOKE ALL ON FUNCTION authority.content_data_key_binding_guard() FROM PUBLIC; CREATE TRIGGER trg_content_data_key_binding_guard BEFORE INSERT ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard();", ContentDataKeyLedgerSql.Failures.GuardMismatch),
            ["truncate trigger dropped"] = ("DROP TRIGGER trg_content_data_key_binding_no_truncate ON authority.content_data_key_bindings", "CREATE TRIGGER trg_content_data_key_binding_no_truncate BEFORE TRUNCATE ON authority.content_data_key_bindings FOR EACH STATEMENT EXECUTE FUNCTION authority.content_data_key_binding_is_permanent()", ContentDataKeyLedgerSql.Failures.PermanenceMismatch),
            ["unique index replaced by a plain one"] = ("DROP INDEX authority.uq_content_data_key_binding_identifier; CREATE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings (data_key_identifier)", "DROP INDEX authority.uq_content_data_key_binding_identifier; CREATE UNIQUE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings (data_key_identifier)", ContentDataKeyLedgerSql.Failures.UniqueIndexMismatch),
            ["an extra trigger"] = ("CREATE TRIGGER trg_extra BEFORE INSERT ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard()", "DROP TRIGGER trg_extra ON authority.content_data_key_bindings", ContentDataKeyLedgerSql.Failures.UnexpectedTableObject),
        };
        var weakenedObserved = new List<string>();
        var weakenedPass = true;
        foreach (var (name, (weaken, restore, reason)) in weakened)
        {
            var applied = await ExecAsync(adminCs, weaken);
            var verdict = await LedgerVerdictAsync();
            var refused = await Outcome(VerifyLedgerAsync(ReleaseA));
            var restored = await ExecAsync(adminCs, restore);
            var after = await LedgerVerdictAsync();
            weakenedObserved.Add($"{name}: {applied} -> {verdict}, {refused}; restored {restored} -> {after}");
            weakenedPass &= applied == "OK" && verdict == reason && refused == LedgerNotEnforced && restored == "OK" && after == "none";
        }

        Record("E-13", "L-13 refuses every weakened ledger object (disabled or replaced guard, dropped truncate trigger, non-unique index, extra trigger) in the write transaction, and passes again once restored",
            "each: verdict = its reason, store CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED 503, restored none", string.Join(" | ", weakenedObserved), weakenedPass);

        {
            var store = new AuthorityStore(Factory.For(RuntimeCs("ledger-held")), ReleaseA, null, null, alerts);
            await using var held = await store.BeginScopeAsync(CancellationToken.None);
            await held.RequireDataKeyLedgerAsync(CancellationToken.None);
            var disable = await ExecAsync(adminCs, "SET lock_timeout = '1s'; ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard");
            await held.CommitAsync(CancellationToken.None);
            Record("E-14", "L-13 lock: while a verified ledger transaction is open, disabling the guard cannot commit (ROW EXCLUSIVE conflicts with it); the guard stays enabled", "55P03 lock timeout; verdict none",
                $"{disable}; verdict {await LedgerVerdictAsync()}", disable.StartsWith("55P03", StringComparison.Ordinal) && await LedgerVerdictAsync() == "none");
        }

        // ================================================================ C: ContentService on real persistence, acceptance OFF (DK-6, P-4, switch re-check, DK-8)
        {
            var record = org.Records[8];
            await WriteAsync(ra, record, Variant.Valid);
            var stamps = await ScalarAsync(adminCs, $"SELECT string_agg(prepare_acceptance_rule_version||'/'||commit_acceptance_rule_version, ',') FROM authority.content_change_operations WHERE form_record_id = '{record.Id}' AND operation_state_name = 'Committed'");
            var body = await ScalarAsync(adminCs, $"SELECT journal_event_body_json FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'ContentHeadCommitted' ORDER BY journal_stream_revision DESC LIMIT 1");
            Record("E-15", "CARV 3 on the real write path: Prepare stamps P = 3, Commit C = 3; the DK-8 body carries an empty binding array; the connection marker names carv-v3", "3/3; new_data_key_bindings []; marker carv-v3",
                $"{stamps}; {(body.Contains("\"new_data_key_bindings\":[]", StringComparison.Ordinal) ? "new_data_key_bindings []" : "body without the array")}; marker {ContentAcceptanceRuleConnection.Marker}",
                stamps == "3/3" && body.Contains("\"new_data_key_bindings\":[]", StringComparison.Ordinal) && ContentAcceptanceRuleConnection.Marker == "carv-v3");
        }

        {
            var record = org.Records[9];
            var before = await SnapshotAsync(org, record);
            var bindings = await CountAsync("SELECT count(*) FROM authority.content_data_key_bindings");
            var signatures = mac.Signatures;
            var outcome = await OutcomeWithMessage(WriteAsync(ra, record, Variant.NewV2Field));
            Record("E-16", "with acceptance off a New v2 group is refused at Prepare before DK-4: nothing signed, no operation, no binding", "ENVELOPE_INVALID: ENVELOPE_VERSION_NOT_ENABLED; signatures, bindings, snapshot unchanged",
                $"{outcome.Message}; signatures {signatures}->{mac.Signatures}; bindings {bindings}->{await CountAsync("SELECT count(*) FROM authority.content_data_key_bindings")}; {Diff(before, await SnapshotAsync(org, record))}",
                outcome.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled && signatures == mac.Signatures && bindings == await CountAsync("SELECT count(*) FROM authority.content_data_key_bindings") && before == await SnapshotAsync(org, record));
        }

        {
            var record = org.Records[10];
            var before = await SnapshotAsync(org, record);
            var integrity = await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{org.Id}'");
            var incidents = await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'");
            var forged = await SeedPreparedAsync(org, record, Variant.NewV2Field, 3);
            var outcome = await Outcome(ra.Service.CommitAsync(Actor(org), forged.Operation, new CommitContentChangeRequest(org.Id, record.Id, forged.Staged), Guid.CreateVersion7(), CancellationToken.None));
            var incidentBody = await ScalarAsync(adminCs, $"SELECT journal_event_body_json FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed' ORDER BY journal_stream_revision DESC LIMIT 1");
            Record("E-17", "DK-6 / P-4 on real persistence: a forged Prepared operation (stamp 3) staging a v2 row with no binding is aborted, one minimal incident is journaled, the content-integrity revision advances, nothing is accepted",
                $"{BindingMismatch}; Aborted {DataKeyLedgerFailures.BindingMismatchReason}; incidents +1 with the reason; integrity +1; record unchanged",
                $"{outcome}; {await StampsAsync(forged.Operation)} {await ReasonAsync(forged.Operation)}; incidents {incidents}->{await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'")}; integrity {integrity}->{await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{org.Id}'")}; {Diff(before, await SnapshotAsync(org, record))}",
                outcome == BindingMismatch && await ReasonAsync(forged.Operation) == DataKeyLedgerFailures.BindingMismatchReason
                && await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'") == incidents + 1
                && incidentBody.Contains("\"failure_reason_code\":\"DATA_KEY_BINDING_MISMATCH\"", StringComparison.Ordinal)
                && await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{org.Id}'") == integrity + 1
                && before == await SnapshotAsync(org, record));
        }

        {
            var record = org.Records[11];
            var id = Guid.CreateVersion7();
            var before = await SnapshotAsync(org, record);
            var incidents = await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'");
            var forged = await SeedPreparedAsync(org, record, Variant.NewV2Field, 3, id);
            var bound = await ExecAsync(runtimeCs, BindingSql(id, org.Id, record.Id, forged.Operation));
            var outcome = await OutcomeWithMessage(ra.Service.CommitAsync(Actor(org), forged.Operation, new CommitContentChangeRequest(org.Id, record.Id, forged.Staged), Guid.CreateVersion7(), CancellationToken.None));
            Record("E-18", "DK-6 consistent but acceptance off: the New group bound to this operation is refused by the switch re-check (tightening wins); no incident; the reservation stays",
                $"binding OK; ENVELOPE_INVALID: ENVELOPE_VERSION_NOT_ENABLED; Aborted {EnvelopeV2Rules.NotEnabledAtCommitReason}; incidents unchanged; binding kept; record unchanged",
                $"binding {bound}; {outcome.Message}; {await StampsAsync(forged.Operation)} {await ReasonAsync(forged.Operation)}; incidents {incidents}->{await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'")}; binding {await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'")}; {Diff(before, await SnapshotAsync(org, record))}",
                bound == "OK" && outcome.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled && await ReasonAsync(forged.Operation) == EnvelopeV2Rules.NotEnabledAtCommitReason
                && await CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'") == incidents
                && await CountAsync($"SELECT count(*) FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'") == 1 && before == await SnapshotAsync(org, record));
        }

        {
            var record = org.Records[12];
            var id = Guid.CreateVersion7();
            var before = await SnapshotAsync(org, record);
            var forged = await SeedPreparedAsync(org, record, Variant.NewV2Field, 3, id);
            var bound = await ExecAsync(runtimeCs, BindingSql(id, org.Id, record.Id, forged.Operation, commitmentHex: new string('0', 63) + "1"));
            var outcome = await Outcome(ra.Service.CommitAsync(Actor(org), forged.Operation, new CommitContentChangeRequest(org.Id, record.Id, forged.Staged), Guid.CreateVersion7(), CancellationToken.None));
            Record("E-19", "DK-6: a binding whose commitment differs from the staged v2 rows aborts the operation as a ledger mismatch", $"binding OK; {BindingMismatch}; record unchanged",
                $"binding {bound}; {outcome}; {Diff(before, await SnapshotAsync(org, record))}", bound == "OK" && outcome == BindingMismatch && before == await SnapshotAsync(org, record));
        }

        {
            var record = org.Records[13];
            var forged = await SeedPreparedAsync(org, record, Variant.NewV2Field, 3);
            await ExecAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard");
            var outcome = await Outcome(ra.Service.CommitAsync(Actor(org), forged.Operation, new CommitContentChangeRequest(org.Id, record.Id, forged.Staged), Guid.CreateVersion7(), CancellationToken.None));
            await ExecAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard");
            Record("E-20", "a Commit that must consult the ledger refuses while the ledger is weakened (L-13) and leaves the operation Prepared; nothing decided", $"{LedgerNotEnforced}; Prepared",
                $"{outcome}; {await StampsAsync(forged.Operation)}", outcome == LedgerNotEnforced && (await StampsAsync(forged.Operation)).EndsWith("Prepared", StringComparison.Ordinal));
        }

        {
            await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(adminCs, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options);
            var down = await Outcome(Task.Run(() => context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate(ContentAcceptanceRuleSql.MigrationId)));
            Record("E-21", "the ledger migration's Down is refused while any binding exists; the table, its rows and the history row stay", "PostgresException P0001; ledger history kept; bindings kept",
                $"{down}; ledger history {await ScalarAsync(adminCs, LedgerHistoryQuery)}; bindings {await CountAsync("SELECT count(*) FROM authority.content_data_key_bindings")}",
                down.Contains("PostgresException P0001", StringComparison.Ordinal) && await ScalarAsync(adminCs, LedgerHistoryQuery) == ContentDataKeyLedgerSql.MigrationId && await CountAsync("SELECT count(*) FROM authority.content_data_key_bindings") > 0);
        }

        // ================================================================ F1/C-14: floor 1, a seeded v2 head, and the CARV-1 downgrade
        var floor3Early = await HostCommandAsync(adminCs, "m2e-floor3-early", "--apply-content-acceptance-floor", "3");
        Record("E-22", "floor 3 cannot be applied before floors 1 and 2 (one step at a time)", "exit 2; floors none", $"exit {floor3Early.Code}; floors {await FloorAsync()}", floor3Early.Code == 2 && await FloorAsync() == "none");
        var floor1 = await HostCommandAsync(adminCs, "m2e-floor1", "--apply-content-acceptance-floor", "1");
        Record("E-23", "floor 1 applies exactly floor 1", "exit 0; floors 1", $"exit {floor1.Code}; floors {await FloorAsync()}", floor1.Code == 0 && await FloorAsync() == "1");

        var downgraded = org.Records[14];
        var protectedRecord = org.Records[15];
        var seedHead1 = await SeedAcceptedV2HeadAsync(org, downgraded);
        var seedHead2 = await SeedAcceptedV2HeadAsync(org, protectedRecord);
        Record("E-24", "setup (controlled test-only mechanism): two records hold an accepted Envelope v2 field row in their head (no build accepts v2)", "OK OK; head revisions 1 1",
            $"{seedHead1} {seedHead2}; head revisions {(await HeadAsync(downgraded)).Revision} {(await HeadAsync(protectedRecord)).Revision}", seedHead1 == "OK" && seedHead2 == "OK");

        {
            var v1 = await LegacyPrepareAsync(carv1Agent, downgraded, Variant.Valid);
            var commit = v1.Outcome == "OK" ? await LegacyCommitAsync(carv1Agent, downgraded, v1.Operation, v1.Staged!) : "not prepared";
            Record("E-25", "C-14 finding: on floor 1 the CARV-1 build (8915f77) accepts an owner save that rewrites the v2 item of the head as a v1 envelope (it has no downgrade guard)", "Prepare OK; Commit OK; head revision 2",
                $"Prepare {v1.Outcome}; Commit {commit}; head revision {(await HeadAsync(downgraded)).Revision}", v1.Outcome == "OK" && commit == "OK" && (await HeadAsync(downgraded)).Revision == 2);
        }

        {
            var before = await SnapshotAsync(org, protectedRecord);
            var outcome = await OutcomeWithMessage(WriteAsync(ra, protectedRecord, Variant.Valid));
            Record("E-26", "this build refuses the same downgrade at Prepare (the CARV-2+ guard), before anything is signed or stored", "ENVELOPE_INVALID: ENVELOPE_DOWNGRADE; record unchanged",
                $"{outcome.Message}; {Diff(before, await SnapshotAsync(org, protectedRecord))}", outcome.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.Downgrade && before == await SnapshotAsync(org, protectedRecord));
        }

        var floor2 = await HostCommandAsync(adminCs, "m2e-floor2", "--apply-content-acceptance-floor", "2");
        Record("E-27", "floor 2 applies exactly floor 2 (floor 1 dropped)", "exit 0; floors 2", $"exit {floor2.Code}; floors {await FloorAsync()}", floor2.Code == 0 && await FloorAsync() == "2");
        {
            var before = await SnapshotAsync(org, protectedRecord);
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var v1 = await LegacyPrepareAsync(carv1Agent, protectedRecord, Variant.Valid);
            Record("E-28", "C-14 remedy: on floor 2 the CARV-1 build can no longer prepare the downgrade (the floor refuses its Prepare insert); nothing persisted", "refused (ck_content_operation_rule_floor_v2); no operation; record unchanged",
                $"{v1.Outcome}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; {Diff(before, await SnapshotAsync(org, protectedRecord))}",
                v1.Outcome != "OK" && v1.Outcome.Contains("ck_content_operation_rule_floor_v2", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations") && before == await SnapshotAsync(org, protectedRecord));
        }

        // ================================================================ F3: floor 3
        var migratedAgain = await HostCommandAsync(adminCs, "m2e-migrate-2", "--migrate");
        Record("E-29", "the ordinary --migrate never installs floor 3", "exit 0; floors 2", $"exit {migratedAgain.Code}; floors {await FloorAsync()}", migratedAgain.Code == 0 && await FloorAsync() == "2");

        await ExecAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_state_name = 'Aborted' WHERE content_change_operation_id = '{seededP2.Operation}'");
        {
            await using var held = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e-held carv-v2", Pooling = false }.ConnectionString);
            await held.OpenAsync();
            var refused = await HostCommandAsync(adminCs, "m2e-floor3-session", "--apply-content-acceptance-floor", "3");
            Record("E-30", "floor 3 is refused while a session able to write operations carries a carv-v marker below 3 (an older instance may run)", "exit 3; floors 2; message names the sessions",
                $"exit {refused.Code}; floors {await FloorAsync()}; {(refused.Output.Contains("session(s)", StringComparison.Ordinal) ? "names the sessions" : refused.Output)}",
                refused.Code == 3 && await FloorAsync() == "2" && refused.Output.Contains("session(s)", StringComparison.Ordinal));
        }

        {
            var lagging = await SeedPreparedAsync(org, org.Records[16], Variant.Valid, 2);
            var refused = await HostCommandAsync(adminCs, "m2e-floor3-inflight", "--apply-content-acceptance-floor", "3");
            await ExecAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_state_name = 'Aborted' WHERE content_change_operation_id = '{lagging.Operation}'");
            Record("E-31", "floor 3 is refused while an unexpired operation prepared below version 3 is in flight", "seed OK; exit 3; floors 2; message names the operations",
                $"seed {lagging.Seeded}; exit {refused.Code}; floors {await FloorAsync()}; {(refused.Output.Contains("prepared below content acceptance rule version 3", StringComparison.Ordinal) ? "names the operations" : refused.Output)}",
                lagging.Seeded == "OK" && refused.Code == 3 && await FloorAsync() == "2" && refused.Output.Contains("prepared below content acceptance rule version 3", StringComparison.Ordinal));
        }

        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard");
            var refused = await HostCommandAsync(adminCs, "m2e-floor3-ledger", "--apply-content-acceptance-floor", "3");
            await ExecAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard");
            Record("E-32", "AM-1 / C-11: floor 3 is refused while the data-key ledger objects are not exactly as created (guard disabled)", "exit 3; floors 2; message names the ledger",
                $"exit {refused.Code}; floors {await FloorAsync()}; {(refused.Output.Contains("data-key ledger", StringComparison.Ordinal) ? "names the ledger" : refused.Output)}",
                refused.Code == 3 && await FloorAsync() == "2" && refused.Output.Contains("data-key ledger", StringComparison.Ordinal));
        }

        {
            var writers = Enumerable.Range(0, 4).Select(i => Instance("RB2-" + i, ReleaseB2)).ToList();
            var alertsBefore = alerts.Count;
            var stop = false;
            var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
            var load = writers.Select((w, i) => Task.Run(async () =>
            {
                var n = 0;
                while (!Volatile.Read(ref stop) && n < 6)
                {
                    outcomes.Add(await Outcome(WriteAsync(w, org.Records[17 + i], Variant.Valid)));
                    n++;
                }
            })).ToList();
            await Task.Delay(300);
            var applied = await HostCommandAsync(adminCs, "m2e-floor3", "--apply-content-acceptance-floor", "3");
            Volatile.Write(ref stop, true);
            await Task.WhenAll(load);
            var live = await ScalarAsync(adminCs, "SELECT pg_get_constraintdef(oid) FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname = 'ck_content_operation_rule_floor_v3'");
            Log("E-33 alerts during the raise: " + alerts.Since(alertsBefore));
            var refusalReasons = string.Join(",", System.Text.RegularExpressions.Regex.Matches(alerts.Since(alertsBefore), "reason=([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct().Order());
            var unequal = await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version IS DISTINCT FROM commit_acceptance_rule_version");
            Record("E-33", "floor 3 applied while four Release B (floor 2) writers run (security invariants): exit 0, floor 2 dropped in the same transaction, the live definition equals the recognized one, all three floors recorded; every write either committed or was refused fail-closed (no other outcome), and every committed operation has P = C",
                $"exit 0; floors 3; definition exact; history {ContentAcceptanceFloorSql.MigrationId},{ContentAcceptanceFloor2Sql.MigrationId},{ContentAcceptanceFloor3Sql.MigrationId}; outcomes within OK / {FloorNotEnforced}; committed P<>C: 0",
                $"exit {applied.Code}; floors {await FloorAsync()}; definition {(live == ContentAcceptanceFloor3Sql.ExpectedDefinition ? "exact" : live)}; history {await ScalarAsync(adminCs, FloorHistoryQuery)}; outcomes {string.Join(",", outcomes.Distinct())} x{outcomes.Count}; committed P<>C: {unequal}",
                applied.Code == 0 && outcomes.Count > 0 && outcomes.All(o => o == "OK" || o == FloorNotEnforced) && await FloorAsync() == "3" && live == ContentAcceptanceFloor3Sql.ExpectedDefinition
                && await ScalarAsync(adminCs, FloorHistoryQuery) == $"{ContentAcceptanceFloorSql.MigrationId},{ContentAcceptanceFloor2Sql.MigrationId},{ContentAcceptanceFloor3Sql.MigrationId}" && unequal == 0);
            Record("E-33b", "availability during the floor-3 raise (the E3-M2d D-F6 expectation): every concurrent Release B write commits; a refusal names its fence reason",
                $"{outcomes.Count} x OK", $"OK {outcomes.Count(o => o == "OK")} of {outcomes.Count}; refusal reasons {(refusalReasons.Length == 0 ? "none" : refusalReasons)}",
                outcomes.Count > 0 && outcomes.All(o => o == "OK"));
        }

        var rb3 = Instance("RB3", ReleaseB3);
        var g3 = Guard(ReleaseB3);
        Record("E-34", "Release B with RequiredFloor = 3 (AM-5): ready on floor 3; the live write fence verdict is none; a write commits with P = C = 3", "True content acceptance rules v3 (floor 3); phase=ReleaseB requiredFloor=3 | none | OK",
            $"{await GuardAsync(g3)} | {await LiveVerdictAsync(3)} | {await Outcome(WriteAsync(rb3, org.Records[21], Variant.Valid))}",
            await GuardAsync(g3) == "True content acceptance rules v3 (floor 3); phase=ReleaseB requiredFloor=3" && await LiveVerdictAsync(3) == "none" && await Outcome(WriteAsync(rb3, org.Records[21], Variant.Valid)) == "OK");

        {
            var record = org.Records[22];
            var before = await SnapshotAsync(org, record);
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var v2 = await LegacyPrepareAsync(carv2Agent, record, Variant.Valid);
            Record("E-35", "the CARV-2 build (a1127a1) cannot prepare after floor 3 (the database refuses its Prepare insert); nothing persisted", "refused (ck_content_operation_rule_floor_v3); no operation; record unchanged",
                $"{v2.Outcome}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; {Diff(before, await SnapshotAsync(org, record))}",
                v2.Outcome != "OK" && v2.Outcome.Contains("ck_content_operation_rule_floor_v3", StringComparison.Ordinal) && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations") && before == await SnapshotAsync(org, record));
        }

        {
            var stamped2 = await SeedPreparedAsync(org, org.Records[23], Variant.Valid, 2);
            var stamped3 = await SeedPreparedAsync(org, org.Records[23], Variant.Valid, 3);
            Record("E-36", "the floor-3 CHECK refuses a direct insert stamped 2 and admits one stamped 3", "23514 ck_content_operation_rule_floor_v3; OK",
                $"{stamped2.Seeded}; {stamped3.Seeded}", stamped2.Seeded.Contains("23514 ck_content_operation_rule_floor_v3", StringComparison.Ordinal) && stamped3.Seeded == "OK");
        }

        {
            var again = await HostCommandAsync(adminCs, "m2e-floor3-again", "--apply-content-acceptance-floor", "3");
            var lower = await HostCommandAsync(adminCs, "m2e-floor2-after-3", "--apply-content-acceptance-floor", "2");
            Record("E-37", "monotonic: re-applying floor 3 is a no-op; applying floor 2 after floor 3 is refused (never lowered)", "exit 0, floors 3; exit 2, floors 3",
                $"exit {again.Code}, floors {await FloorAsync()}; exit {lower.Code}, floors {await FloorAsync()}", again.Code == 0 && lower.Code == 2 && await FloorAsync() == "3");
        }

        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3");
            await RefusedWriteAsync("E-38", "floor 3 dropped (a restore or privileged change): a Release B instance requiring floor 3 refuses every write in the write transaction", rb3, org, org.Records[24], ContentAcceptanceFloorFailures.FloorRecordedButMissing);
            var exit4 = await HostCommandAsync(adminCs, "m2e-floor3-missing", "--apply-content-acceptance-floor", "3");
            var reinstated = await HostCommandAsync(adminCs, "m2e-floor3-reinstate", "--apply-content-acceptance-floor", "3", "--reinstate-recorded-floor");
            Record("E-39", "recovery: the floor command reports the recorded-but-missing floor 3 (exit 4) and reinstates it with the flag; writes resume", "exit 4; exit 0; floors 3; write OK",
                $"exit {exit4.Code}; exit {reinstated.Code}; floors {await FloorAsync()}; write {await Outcome(WriteAsync(rb3, org.Records[24], Variant.Valid))}",
                exit4.Code == 4 && reinstated.Code == 0 && await FloorAsync() == "3" && await Outcome(WriteAsync(rb3, org.Records[24], Variant.Valid)) == "OK");
        }

        {
            await ExecAsync(adminCs, "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3; ALTER TABLE authority.content_change_operations ADD CONSTRAINT ck_content_operation_rule_floor_v2 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)) NOT VALID");
            await RefusedWriteAsync("E-40", "floor 3 replaced by floor 2: a Release B instance requiring floor 3 refuses every write", rb3, org, org.Records[25], ContentAcceptanceFloorFailures.FloorLowered);
            var reinstated = await HostCommandAsync(adminCs, "m2e-floor3-reinstate-2", "--apply-content-acceptance-floor", "3", "--reinstate-recorded-floor");
            Record("E-41", "reinstatement adds floor 3 and drops the lower floor that replaced it", "exit 0; floors 3", $"exit {reinstated.Code}; floors {await FloorAsync()}", reinstated.Code == 0 && await FloorAsync() == "3");
        }

        Record("E-42", "end state: the runtime role is ordinary, the ledger verdict is none and the fence verdict at floor 3 is none", "false false false | none | none",
            $"{await RoleFactsAsync()} | {await LedgerVerdictAsync()} | {await LiveVerdictAsync(3)}", await RoleFactsAsync() == "false false false" && await LedgerVerdictAsync() == "none" && await LiveVerdictAsync(3) == "none");
    }

    // ------------------------------------------------------------------------------------------------ E3-M2e-1 helpers
    private const string LedgerHistoryQuery = "SELECT coalesce(string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\"), 'none') FROM authority.ef_migrations_history WHERE \"MigrationId\" LIKE '%ContentDataKeyBindings%'";
    private const string LedgerNotEnforced = "AuthorityException CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED 503 retryable=False";
    private const string BindingMismatch = "AuthorityException DATA_KEY_BINDING_MISMATCH 409 retryable=False";
    private const string FloorNotEnforced = "AuthorityException CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED 503 retryable=False";
    private static readonly ContentAcceptancePolicy ReleaseB3 = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "3");

    private string RuntimeCs(string name) => ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e-runtime-" + name }.ConnectionString)!;

    /// <summary>A ledger insert in literal SQL (test-only values generated here; no external input).</summary>
    private static string BindingSql(Guid id, Guid organization, Guid record, Guid operation, short kind = 1, int count = 1, string? commitmentHex = null) =>
        $"INSERT INTO authority.content_data_key_bindings (content_data_key_binding_id, data_key_identifier, organization_id, form_record_id, envelope_payload_kind_code, data_key_commitment_bytes, binding_item_count, binding_content_change_operation_id, data_key_bound_on) "
        + $"VALUES ('{Guid.CreateVersion7()}', '{id}', '{organization}', '{record}', {kind}, '\\x{commitmentHex ?? new string('d', 64)}', {count}, '{operation}', now())";

    private ContentChangeOperation NewOperation(Org org, RecordModel record, int stamp)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        return new ContentChangeOperation
        {
            ContentChangeOperationId = id, OrganizationId = org.Id, FormRecordId = record.Id, ActorIdentityBindingId = org.OwnerBinding,
            OperationIdempotencyKey = "m2e-store-" + id.ToString("N"), OperationRequestDigest = SHA256.HashData(id.ToByteArray()),
            ExpectedAuthorizationRevision = 7, ExpectedContentRevision = 0, CandidateContentRevision = 1, CandidateKeyVersion = 1,
            CandidateManifestRoot = MerkleManifestV1.EmptyRoot(), CandidateLeafIndexDigest = new byte[32], CandidateLeafCount = 0,
            OperationStateName = ContentOperationStates.Prepared, CorrelationIdentifier = Guid.CreateVersion7(), OperationCreatedOn = now, OperationExpiresOn = now.AddMinutes(10),
            PrepareAcceptanceRuleVersion = stamp,
        };
    }

    /// <summary>The real store: an operation (stamp 3) and one binding added in one scope and committed (the DK-5 shape, without ContentService).</summary>
    private async Task RegisterAsync(ContentAcceptancePolicy policy, Org org, RecordModel record, Guid id)
    {
        var store = new AuthorityStore(Factory.For(RuntimeCs("register")), policy, null, null, alerts);
        await using var scope = await store.BeginScopeAsync(CancellationToken.None);
        await scope.LockOrganizationAsync(org.Id, CancellationToken.None);
        var operation = NewOperation(org, record, 3);
        scope.AddDataKeyBindings(DataKeyLedgerRules.BindingsFor([new DataKeyLedgerRules.DataKeyGroup(id, 1, Fill(32, 0xdd), 1)], org.Id, record.Id, operation.ContentChangeOperationId, DateTimeOffset.UtcNow)); // added first: EF must still order the operation first
        scope.AddOperation(operation);
        await scope.CommitAsync(CancellationToken.None);
    }

    private async Task VerifyLedgerAsync(ContentAcceptancePolicy policy)
    {
        var store = new AuthorityStore(Factory.For(RuntimeCs("verify")), policy, null, null, alerts);
        await using var scope = await store.BeginScopeAsync(CancellationToken.None);
        await scope.RequireDataKeyLedgerAsync(CancellationToken.None);
    }

    private async Task<string> LedgerVerdictAsync()
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        try
        {
            return ContentDataKeyLedgerSql.Evaluate(await ContentDataKeyLedgerSql.ReadAsync(connection, null, CancellationToken.None)) ?? "none";
        }
        catch (Exception ex)
        {
            return "unverifiable: " + Describe(ex);
        }
    }

    private async Task<(bool Equal, string Live)> LedgerRenderingsAsync()
    {
        var live = await ScalarAsync(adminCs, """
            SELECT concat_ws(' || ',
              (SELECT pg_get_indexdef(indexrelid) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relname = 'uq_content_data_key_binding_identifier'),
              (SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'trg_content_data_key_binding_guard'),
              (SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'trg_content_data_key_binding_permanent'),
              (SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'trg_content_data_key_binding_no_truncate'))
            """);
        var expected = string.Join(" || ", ContentDataKeyLedgerSql.ExpectedUniqueIndexDefinition, ContentDataKeyLedgerSql.ExpectedGuardTriggerDefinition, ContentDataKeyLedgerSql.ExpectedPermanentTriggerDefinition, ContentDataKeyLedgerSql.ExpectedTruncateTriggerDefinition);
        return (live == expected, live);
    }

    /// <summary>
    /// Controlled test-only mechanism (as in the E3-M2b-2 S-4 and E3-M2d gates): an accepted head holding an Envelope v2 field row,
    /// written through an admin session with triggers off (session_replication_role = replica); every CHECK still applies (the CARV
    /// commit CHECK and the installed floor). The committed operation is stamped 1 (the C-14 model: a head a CARV-1 build would edit).
    /// </summary>
    private async Task<string> SeedAcceptedV2HeadAsync(Org org, RecordModel record)
    {
        try
        {
            var head = await HeadAsync(record);
            var operation = Guid.CreateVersion7();
            var revision = head.Revision + 1;
            var rows = Fixtures.Candidate(org.Id, record.Id, record.TextField, record.TextItem, record.SealedField, record.SealedItem, record.DocumentField, record.Document, record.NextSeed(), Variant.NewV2Field)
                .Select(r => Fixtures.Encode(r, org.Id, operation, revision)).ToList();
            var key = new IntegrityKeyReference(org.Id, IntegrityKeyPurposes.RowIntegrityV1, "m2d-key-" + org.Id.ToString("N"), 1);
            var signed = new List<(Staged Item, byte[] Signature)>();
            foreach (var item in rows)
            {
                signed.Add((item, (await mac.SignAsync(key, item.Bytes, CancellationToken.None)).Mac));
            }

            var manifest = MerkleManifestV1.Build(signed.Select(s => new MerkleLeafInput((MerkleItemKind)(byte)s.Item.Kind, s.Item.ItemId, s.Signature, SHA256.HashData(s.Item.Bytes), (ulong)revision, operation)));
            var now = DateTimeOffset.UtcNow;
            var seedCs = new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "m2e-seed-head" }.ConnectionString;
            await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
            context.ContentOperations.Add(new ContentChangeOperation
            {
                ContentChangeOperationId = operation, OrganizationId = org.Id, FormRecordId = record.Id, ActorIdentityBindingId = org.OwnerBinding,
                OperationIdempotencyKey = "m2e-head-" + operation.ToString("N"), OperationRequestDigest = SHA256.HashData(operation.ToByteArray()),
                ExpectedAuthorizationRevision = 7, ExpectedContentRevision = head.Revision, CandidateContentRevision = revision, CandidateKeyVersion = 1,
                CandidateManifestRoot = manifest.MerkleRoot, CandidateLeafIndexDigest = manifest.LeafIndexDigest, CandidateLeafCount = (long)manifest.LeafCount,
                OperationStateName = ContentOperationStates.Committed, CorrelationIdentifier = Guid.CreateVersion7(), OperationCreatedOn = now, OperationExpiresOn = now.AddMinutes(10),
                OperationCommittedOn = now, PrepareAcceptanceRuleVersion = 1, CommitAcceptanceRuleVersion = 1,
            });
            var commit = new RecordManifestCommit
            {
                RecordManifestCommitId = Guid.CreateVersion7(), OrganizationId = org.Id, FormRecordId = record.Id, CommittedOperationId = operation, CommittedContentRevision = revision,
                ManifestSchemaVersion = 1, ManifestAlgorithmVersion = 1, RowSignatureKeyVersion = 1, ManifestLeafCount = (long)manifest.LeafCount, ManifestRootDigest = manifest.MerkleRoot,
                ManifestLeafIndexDigest = manifest.LeafIndexDigest, ManifestCommittedOn = now,
            };
            context.ManifestCommits.Add(commit);
            context.ManifestLeaves.AddRange(manifest.SortedLeaves.Select((leaf, position) => new RecordManifestLeaf
            {
                RecordManifestLeafId = Guid.CreateVersion7(), RecordManifestCommitId = commit.RecordManifestCommitId, ManifestLeafPosition = position, ManifestItemKey = leaf.ItemKey,
                ManifestItemKindCode = (byte)leaf.Kind, ManifestItemId = leaf.ItemId, CanonicalPayloadDigest = leaf.CanonicalPayloadDigest.ToArray(), RowSignature = leaf.RowSignature.ToArray(),
                ManifestLeafDigest = manifest.LeafHashes[position],
            }));
            var stored = await context.ContentHeads.AsTracking().SingleAsync(h => h.OrganizationId == org.Id && h.FormRecordId == record.Id);
            stored.CommittedContentRevision = revision;
            stored.CommittedOperationId = operation;
            stored.CurrentRecordManifestCommitId = commit.RecordManifestCommitId;
            stored.CommittedManifestRoot = manifest.MerkleRoot;
            stored.SignatureKeyVersion = 1;
            stored.ContentHeadModifiedOn = now;
            await context.SaveChangesAsync();
            record.Accepted = signed.Select(s => (s.Item, s.Signature)).ToList();
            return "OK";
        }
        catch (Exception ex)
        {
            Log("seed head failed: " + ex);
            return Describe(ex);
        }
    }

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

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

    private AuthorityNode Instance(string name, ContentAcceptancePolicy policy, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? probe = null)
    {
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e-runtime-" + name }.ConnectionString)!;
        var store = new AuthorityStore(Factory.For(cs, probe), policy, null, null, alerts);
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), TimeProvider.System);
        // E3-M2e-1: the real switch, resolved as the host would in Testing with acceptance NOT configured (off).
        return new AuthorityNode(name, new ContentService(store, mac, verifier, new NoFaults(), TimeProvider.System, new ShareContributorWriteSwitch(false, false), envelopeV2Acceptance: EnvelopeV2AcceptanceSwitch.Create(null, "Testing", policy)));
    }

    private ContentAcceptanceRuleGuard Guard(ContentAcceptancePolicy policy)
    {
        var marked = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e-guard" }.ConnectionString)!;
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

    // ---- the frozen builds (separate processes) ----
    private async Task<V1Prepared> LegacyPrepareAsync(string agent, RecordModel record, Variant variant, int clockOffsetMinutes = 0)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation, variant);
        var key = "m2e-legacy-" + operation.ToString("N");
        var output = await RunLegacyAsync(agent, "prepare", new { Issuer, Subject = record.Org.Owner.ToString("D"), Request = request, Key = key, ClockOffsetMinutes = clockOffsetMinutes });
        if (!output.GetProperty("Ok").GetBoolean())
        {
            return new V1Prepared(V1Failure(output), operation, request, key, rows, null);
        }

        var prepared = output.GetProperty("Result").Deserialize<PreparedContentOperation>()!;
        return new V1Prepared("OK", prepared.OperationId, request, key, rows, Staged(prepared, rows));
    }

    private async Task<string> LegacyCommitAsync(string agent, RecordModel record, Guid operation, List<StagedContentItem> staged, int clockOffsetMinutes = 0)
    {
        var output = await RunLegacyAsync(agent, "commit", new { Issuer, Subject = record.Org.Owner.ToString("D"), OperationId = operation, Request = new CommitContentChangeRequest(record.Org.Id, record.Id, staged), ClockOffsetMinutes = clockOffsetMinutes });
        var outcome = output.GetProperty("Ok").GetBoolean() ? "OK" : V1Failure(output);
        if (outcome == "OK")
        {
            record.Accepted = staged.Select(s => (FromStaged(record, s), Convert.FromBase64String(s.RowSignatureBase64))).ToList();
        }

        return outcome;
    }

    private static Staged FromStaged(RecordModel record, StagedContentItem item) => new(null!, Guid.Empty, Convert.FromBase64String(item.CanonicalBytesBase64), item.ItemKind, item.ItemId);

    /// <summary>The version-1 build's error and the alerts it raised (its store names a refusing fence constraint only in its alert).</summary>
    private static string V1Failure(JsonElement output)
    {
        var alertsRaised = output.TryGetProperty("Alerts", out var a) ? string.Join(" | ", a.EnumerateArray().Select(x => x.GetString())) : string.Empty;
        return (output.GetProperty("Error").GetString() ?? "error") + (alertsRaised.Length == 0 ? string.Empty : " ## alerts: " + alertsRaised);
    }

    private async Task<JsonElement> RunLegacyAsync(string agent, string command, object input)
    {
        var dir = Path.Combine(evidenceDir, "legacy-calls");
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:HHmmssfff}-{command}-{Guid.CreateVersion7():N}";
        var inPath = Path.Combine(dir, name + ".in.json");
        var outPath = Path.Combine(dir, name + ".out.json");
        await File.WriteAllTextAsync(inPath, JsonSerializer.Serialize(input));
        var start = new ProcessStartInfo(agent) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(inPath);
        start.ArgumentList.Add(outPath);
        start.Environment["M2E_LEGACY_RUNTIME"] = runtimeCs;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the version-1 agent");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = JsonDocument.Parse(await File.ReadAllTextAsync(outPath)).RootElement;
        Log($"legacy {Path.GetFileName(Path.GetDirectoryName(agent))} {command} exit {process.ExitCode}: ok={output.GetProperty("Ok")} error={(output.TryGetProperty("Error", out var e) ? e.ToString() : "")} mapped={(output.TryGetProperty("Mapped", out var m) ? m.ToString() : "")} build={(output.TryGetProperty("BuildRuleVersion", out var b) ? b.ToString() : "")} {(await stderr).Trim()}");
        _ = await stdout;
        return output;
    }

    /// <summary>The controlled test-only mechanism (as in the S-4 gate): a signed Prepared operation recorded without running Prepare.</summary>
    private async Task<(Guid Operation, List<StagedContentItem> Staged, string Seeded)> SeedPreparedAsync(Org org, RecordModel record, Variant variant, int? prepareRuleVersion, Guid? dataKeyIdentifier = null)
    {
        var operation = Guid.CreateVersion7();
        var head = await HeadAsync(record);
        var revision = head.Revision + 1;
        var rows = Fixtures.Candidate(org.Id, record.Id, record.TextField, record.TextItem, record.SealedField, record.SealedItem, record.DocumentField, record.Document, record.NextSeed(), variant, dataKeyIdentifier)
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
    public static Factory For(string connectionString) => For(connectionString, null);

    /// <summary>F-E1: with an optional command interceptor (the fence probe of FloorDeadlockScenarios.cs).</summary>
    public static Factory For(string connectionString, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(connectionString, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName));
        return new(interceptor is null ? builder.Options : builder.AddInterceptors(interceptor).Options);
    }

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
    public static List<Row> Candidate(Guid org, Guid record, Guid textField, Guid textItem, Guid sealedField, Guid sealedItem, Guid documentField, Guid document, byte seed, Lab.Variant variant, Guid? dataKeyIdentifier = null)
    {
        var sealedRow = Sealed(org, record, sealedField, sealedItem, seed);
        var documentRow = Attachment(org, record, documentField, document);
        switch (variant)
        {
            case Lab.Variant.NewV2Field: sealedRow = AsV2(sealedRow, EncryptedValuesV2, org, "field-value", dataKeyIdentifier); break;
            case Lab.Variant.NewV2Document: documentRow = With(AsV2(documentRow, AttachmentsV2, org, "document"), "ciphertext_size_bytes", RowValue.Int64(1020)); break;
            case Lab.Variant.FieldSchema2Envelope1: sealedRow = With(AsV2(sealedRow, EncryptedValuesV2, org, "field-value"), "encryption_envelope_version", RowValue.Int32(1)); break;
        }

        return [Header(org, record), Text(org, record, textField, textItem, "value-" + seed), sealedRow, documentRow];
    }

    private static Row With(Row row, string column, RowValue? value) => row with { Values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { [column] = value } };

    /// <summary>A well-formed Envelope v2 / schema 2 row under the v2 rules (canonical v2 reference, wrap code 1, UUIDv7 id, 32-byte commitment).</summary>
    private static Row AsV2(Row row, RowSchemaDefinition schema2, Guid org, string purpose, Guid? dataKeyIdentifier = null)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal)
        {
            ["encryption_envelope_version"] = RowValue.Int32(2),
            ["encryption_key_reference"] = RowValue.Text($"localdev:{org:D}:{purpose}:1"),
            ["data_key_commitment_bytes"] = RowValue.Bytes(Fill(32, 0xdd)),
            ["data_key_identifier"] = RowValue.Uuid(dataKeyIdentifier ?? Guid.CreateVersion7()),
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
