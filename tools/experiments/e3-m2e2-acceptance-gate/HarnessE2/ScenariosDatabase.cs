// E3-M2e-2 database-level enforcement with acceptance ON (group D): every ledger object L-13 verifies and every floor-3 corruption is
// applied in the disposable database with the container superuser, a New v2 write is attempted, the refusal is checked, the object is
// restored and the catalog digest is verified equal to its baseline (the database-mode enforcement-removal checks of §9).
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;

internal sealed partial class Lab
{
    private const string LedgerNotEnforced = "AuthorityException CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED 503 retryable=False";
    private const string FloorNotEnforced = "AuthorityException CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED 503 retryable=False";

    private async Task<string> DefinitionsDigestAsync(string? database = null)
    {
        var text = await ScalarAsync(database is null ? adminCs : AdminFor(database), """
            SELECT concat_ws(' || ',
              (SELECT string_agg(pg_get_indexdef(i.indexrelid), ' ; ' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = 'authority.content_data_key_bindings'::regclass),
              (SELECT string_agg(t.tgname || ':' || t.tgenabled::text || ':' || pg_get_triggerdef(t.oid), ' ; ' ORDER BY t.tgname) FROM pg_trigger t WHERE t.tgrelid = 'authority.content_data_key_bindings'::regclass AND NOT t.tgisinternal),
              (SELECT string_agg(p.proname || ':' || md5(p.prosrc) || ':' || p.prosecdef::text, ' ; ' ORDER BY p.proname) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'authority' AND p.proname LIKE 'content\_data\_key\_binding%'),
              (SELECT string_agg(conname || ':' || pg_get_constraintdef(oid), ' ; ' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname LIKE 'ck\_content\_operation\_rule\_floor\_v%'),
              (SELECT string_agg("MigrationId", ',' ORDER BY "MigrationId") FROM authority.ef_migrations_history))
            """);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private async Task DatabaseEnforcementScenariosAsync()
    {
        CurrentGroup = "D";
        var dOrg = await SeedOrgAsync(40, localKeys!, 1);
        var baseline = await DefinitionsDigestAsync();
        var next = 0;
        Rec NextRecord() => dOrg.Records[next++];

        async Task<(string Outcome, string Alerts, long OpsDelta, long BindingsDelta, int Signed)> AttemptNewAsync()
        {
            var r = NextRecord();
            var batch = await BatchAsync(r);
            var ops = await OpsAsync();
            var bindings = await BindingCountAsync();
            var signatures = mac.Signatures;
            var raised = alerts.Count;
            var prepared = await PrepareAsync(acc, r, Owner(r, batch.Seal("weakened")));
            return (prepared.Outcome, alerts.Since(raised), await OpsAsync() - ops, await BindingCountAsync() - bindings, mac.Signatures - signatures);
        }

        async Task<string> HealthyWriteAsync()
        {
            var r = NextRecord();
            var batch = await BatchAsync(r);
            return await WriteAsync(acc, r, Owner(r, batch.Seal("after-restore")));
        }

        // Ledger objects (L-13): Prepare with a New group refuses with the ledger not enforced, before signing, nothing persisted.
        var ledgerCases = new (string Id, string Name, string Weaken, string Restore)[]
        {
            ("D-01", "binding guard (P-1) disabled", "ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard", "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard"),
            ("D-02", "binding guard body replaced by a permissive one", "CREATE OR REPLACE FUNCTION authority.content_data_key_binding_guard() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$ BEGIN RETURN NEW; END $$",
                "DROP TRIGGER trg_content_data_key_binding_guard ON authority.content_data_key_bindings; DROP FUNCTION authority.content_data_key_binding_guard(); " + ContentDataKeyLedgerSql.GuardFunctionSql + " REVOKE ALL ON FUNCTION authority.content_data_key_binding_guard() FROM PUBLIC; CREATE TRIGGER trg_content_data_key_binding_guard BEFORE INSERT ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard();"),
            ("D-03", "immutability (P-2) TRUNCATE trigger dropped", "DROP TRIGGER trg_content_data_key_binding_no_truncate ON authority.content_data_key_bindings",
                "CREATE TRIGGER trg_content_data_key_binding_no_truncate BEFORE TRUNCATE ON authority.content_data_key_bindings FOR EACH STATEMENT EXECUTE FUNCTION authority.content_data_key_binding_is_permanent()"),
            ("D-04", "immutability (P-2) UPDATE/DELETE trigger disabled", "ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_permanent", "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_permanent"),
            ("D-05", "global unique index replaced by a non-unique index", "DROP INDEX authority.uq_content_data_key_binding_identifier; CREATE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings (data_key_identifier)",
                "DROP INDEX authority.uq_content_data_key_binding_identifier; CREATE UNIQUE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings (data_key_identifier)"),
            ("D-06", "an extra trigger on the ledger", "CREATE TRIGGER trg_extra BEFORE INSERT ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard()", "DROP TRIGGER trg_extra ON authority.content_data_key_bindings"),
        };
        foreach (var (id, name, weaken, restore) in ledgerCases)
        {
            var applied = await ExecAsync(adminCs, weaken);
            var attempt = await AttemptNewAsync();
            var restored = await ExecAsync(adminCs, restore);
            var digest = await DefinitionsDigestAsync();
            var healthy = await HealthyWriteAsync();
            Record(id, "Database", $"L-13 with acceptance on: {name}: a New v2 write is refused in its Prepare transaction before signing, nothing persisted; restored exactly; writes resume",
                $"weaken OK; {LedgerNotEnforced}; ops +0; bindings +0; signed 0; restored; digest = baseline; healthy write OK",
                $"weaken {applied}; {attempt.Outcome}; ops +{attempt.OpsDelta}; bindings +{attempt.BindingsDelta}; signed {attempt.Signed}; restore {restored}; digest {(digest == baseline ? "= baseline" : "DIFFERS")}; healthy {healthy}",
                applied == "OK" && attempt.Outcome == LedgerNotEnforced && attempt.OpsDelta == 0 && attempt.BindingsDelta == 0 && attempt.Signed == 0 && restored == "OK" && digest == baseline && healthy == "OK");
        }

        // A Commit that must consult the ledger refuses while it is weakened, and leaves the operation Prepared (nothing decided).
        {
            var r = NextRecord();
            var batch = await BatchAsync(r);
            var prepared = await PrepareAsync(acc, r, Owner(r, batch.Seal("commit-while-weakened")));
            await ExecCheckedAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard");
            var refused = await CommitAsync(acc, r, prepared);
            var state = await StateAsync(prepared.OperationId);
            await ExecCheckedAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard");
            var retried = await CommitAsync(acc, r, prepared);
            Record("D-07", "Database", "L-13 at Commit with acceptance on: a Commit that must consult a weakened ledger refuses and leaves the operation Prepared; after the restore the same operation commits",
                $"Prepare OK; {LedgerNotEnforced}; 3/NULL Prepared; retry OK", $"Prepare {prepared.Outcome}; {refused.Outcome}; {state}; retry {retried.Outcome}",
                prepared.Outcome == "OK" && refused.Outcome == LedgerNotEnforced && state == "3/NULL Prepared -" && retried.Outcome == "OK");
        }

        // Floor 3 corruptions (Release B RequiredFloor 3): every write is refused by the transactional fence; restore or reinstate.
        var history3 = await RowsAsync(adminCs, $"SELECT \"MigrationId\", \"ProductVersion\" FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor3Sql.MigrationId}'");
        var floorCases = new (string Id, string Name, string Weaken, string? Restore, string Reason)[]
        {
            ("D-08", "floor 3 dropped (recorded but missing)", "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3", null, ContentAcceptanceFloorFailures.FloorRecordedButMissing),
            ("D-09", "floor 3 replaced by floor 2 (lowered)", "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3; ALTER TABLE authority.content_change_operations ADD CONSTRAINT ck_content_operation_rule_floor_v2 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)) NOT VALID", null, ContentAcceptanceFloorFailures.FloorLowered),
            // Restore: drop the weakened constraint, then the product's own reinstatement installs the exact floor (re-adding the rendered
            // text is not identical: PostgreSQL re-renders the array casts; found in development run 4).
            ("D-10", "floor 3 weakened in place (same name, >= 2)", "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3; ALTER TABLE authority.content_change_operations ADD CONSTRAINT ck_content_operation_rule_floor_v3 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)) NOT VALID",
                "REINSTATE:ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v3", ContentAcceptanceFloorFailures.FloorDefinitionMismatch),
            ("D-11", "a second floor added beside floor 3", "ALTER TABLE authority.content_change_operations ADD CONSTRAINT ck_content_operation_rule_floor_v2 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)) NOT VALID",
                "ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2", ContentAcceptanceFloorFailures.FloorDefinitionMismatch),
            ("D-12", "the floor-3 history row deleted (installed but not recorded)", $"DELETE FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{ContentAcceptanceFloor3Sql.MigrationId}'",
                $"INSERT INTO authority.ef_migrations_history (\"MigrationId\", \"ProductVersion\") VALUES ('{history3[0][0]}', '{history3[0][1]}')", ContentAcceptanceFloorFailures.FloorNotRecorded),
        };
        foreach (var (id, name, weaken, restore, reason) in floorCases)
        {
            var applied = await ExecAsync(adminCs, weaken);
            var attempt = await AttemptNewAsync();
            string restored;
            if (restore is null || restore.StartsWith("REINSTATE:", StringComparison.Ordinal))
            {
                var dropped = restore is null ? "OK" : await ExecAsync(adminCs, restore["REINSTATE:".Length..]);
                var reinstated = await HostCommandAsync(adminCs, "m2e2-reinstate-" + id, "--apply-content-acceptance-floor", "3", "--reinstate-recorded-floor");
                restored = dropped == "OK" ? "reinstate exit " + reinstated.Code : "drop " + dropped;
            }
            else
            {
                restored = await ExecAsync(adminCs, restore);
            }

            var digest = await DefinitionsDigestAsync();
            var healthy = await HealthyWriteAsync();
            Record(id, "Database", $"floor with acceptance on: {name}: every write is refused by the Release B fence in its transaction (reason {reason}), nothing persisted; restored; writes resume",
                $"weaken OK; {FloorNotEnforced}; alert reason={reason}; ops +0; bindings +0; restored; digest = baseline; healthy OK",
                $"weaken {applied}; {attempt.Outcome}; alert has reason {attempt.Alerts.Contains("reason=" + reason, StringComparison.Ordinal)}; ops +{attempt.OpsDelta}; bindings +{attempt.BindingsDelta}; {restored}; digest {(digest == baseline ? "= baseline" : "DIFFERS")}; healthy {healthy}",
                applied == "OK" && attempt.Outcome == FloorNotEnforced && attempt.Alerts.Contains("reason=" + reason, StringComparison.Ordinal) && attempt.OpsDelta == 0 && attempt.BindingsDelta == 0
                && (restored == "OK" || restored == "reinstate exit 0") && digest == baseline && healthy == "OK");
        }

        // Release B refusals on a database restored to floor 2 (the disposable copy taken after floor 2, before floor 3).
        {
            var copySwitch = EnvelopeV2AcceptanceSwitch.Create("true", "Testing", ReleaseB3);
            var observed = await ObserveAsync(copySwitch, Floor2CopyDatabase);
            var node = Instance("FLOOR2-COPY", ReleaseB3, copySwitch, Floor2CopyDatabase);
            var r = setupOrg.Records[25];
            var shadow = new Rec(setupOrg, r.Id);
            var batch = await BatchAsync(shadow);
            var ops = await OpsAsync(Floor2CopyDatabase);
            var raised = alerts.Count;
            var prepared = await PrepareAsync(node, shadow, Owner(shadow, batch.Seal("restored-floor-2")), database: Floor2CopyDatabase);
            var v1 = await PrepareAsync(node, shadow, Owner(shadow), database: Floor2CopyDatabase);
            var reasons = string.Join(",", System.Text.RegularExpressions.Regex.Matches(alerts.Since(raised), "reason=([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal));
            Record("D-13", "Database", "a database restored to floor 2 (copy taken before floor 3): an instance requiring floor 3 with acceptance on refuses every write, v2 and v1, in its transaction; nothing persisted",
                $"policy recorded; {FloorNotEnforced} x2 with a fence reason alert; ops +0", $"policy {observed} enabled {copySwitch.Enabled}; v2 {prepared.Outcome}; v1 {v1.Outcome}; alert reasons [{reasons}]; ops {ops}->{await OpsAsync(Floor2CopyDatabase)}",
                prepared.Outcome == FloorNotEnforced && v1.Outcome == FloorNotEnforced && reasons.Length > 0 && ops == await OpsAsync(Floor2CopyDatabase));
        }

        Record("D-14", "Database", "end of group D: the ledger and floor catalog digest equals its baseline", "= baseline", await DefinitionsDigestAsync() == baseline ? "= baseline" : "DIFFERS", await DefinitionsDigestAsync() == baseline);
        await GrantSentinelAsync("after-database");
    }
}
