namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2e CARV floor 3 (docs/security/E3_M2E_CONTRACT_CLOSURE.md AM-1, OD-M2e-06): the exact SQL of floor migration
/// <c>AuthorityContentAcceptanceRuleFloor3</c>. Like floors 1 and 2 it belongs to <see cref="ContentAcceptanceFloorContext"/> only, so
/// the ordinary <c>--migrate</c> runner never applies it (the data-key ledger migration installs no floor); it is applied only by
/// <c>--apply-content-acceptance-floor 3</c>, which migrates the floor context to exactly this migration, after floor 2.
/// <b>E3-M2e-1: exercised only in disposable PostgreSQL; never installed in a persistent environment by this increment.</b>
/// <para>
/// Up runs in one transaction: lock timeout 5 s; ACCESS EXCLUSIVE on <c>content_change_operations</c>; then, under the lock: the
/// E3-M2b-1 fence complete; floor 2 installed, recorded and the only floor (one step at a time); the data-key ledger migration
/// recorded and its unique identifier index and P-1/P-2 triggers present and enabled exactly as created, with plpgsql functions
/// that are not SECURITY DEFINER (the function bodies are verified exactly by every ledger-using write, L-13); no unexpired
/// Prepared operation stamped below 3 (or unstamped); no connected session able to write operations whose application name
/// lacks a <c>carv-v</c> marker of at least 3 (the LISTEN-only connection excepted). Then add-then-drop:
/// <c>ck_content_operation_rule_floor_v3</c> (NOT VALID) and <c>DROP CONSTRAINT ck_content_operation_rule_floor_v2</c>. Any failure
/// applies nothing, including the history row. Down always refuses. No brace appears in any of this SQL.
/// </para>
/// </summary>
public static class ContentAcceptanceFloor3Sql
{
    public const int FloorVersion = 3;

    public const string MigrationId = "20261116040000_AuthorityContentAcceptanceRuleFloor3";

    public const string ConstraintName = "ck_content_operation_rule_floor_v3";

    /// <summary>The floor CHECK: Prepared and Committed rows need a non-NULL Prepare stamp of at least 3 (two-valued: D-1, D-2).</summary>
    public const string FloorCheckSql =
        "operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 3)";

    /// <summary>PostgreSQL 18's rendering of the installed floor-3 constraint (pg_get_constraintdef), verified live by the E3-M2e-1 gate.</summary>
    public const string ExpectedDefinition =
        "CHECK ((((operation_state_name)::text <> ALL ((ARRAY['Prepared'::character varying, 'Committed'::character varying])::text[])) OR ((prepare_acceptance_rule_version IS NOT NULL) AND (prepare_acceptance_rule_version >= 3)))) NOT VALID";

    /// <summary>AM-1: the data-key ledger must be present exactly as created before floor 3 (C-11).</summary>
    private const string LedgerPreconditions = """
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261116031706_AuthorityContentDataKeyBindings')
               OR to_regclass('authority.content_data_key_bindings') IS NULL THEN
                RAISE EXCEPTION 'CARV floor 3 refused: the data-key ledger migration is not applied';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid
                            WHERE i.indrelid = 'authority.content_data_key_bindings'::regclass AND c.relname = 'uq_content_data_key_binding_identifier'
                              AND i.indisunique AND i.indisvalid AND i.indisready
                              AND pg_catalog.pg_get_indexdef(i.indexrelid) = 'CREATE UNIQUE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings USING btree (data_key_identifier)')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid JOIN pg_catalog.pg_language l ON l.oid = p.prolang
                               WHERE g.tgrelid = 'authority.content_data_key_bindings'::regclass AND NOT g.tgisinternal AND g.tgenabled IN ('O', 'A')
                                 AND l.lanname = 'plpgsql' AND NOT p.prosecdef
                                 AND pg_catalog.pg_get_triggerdef(g.oid) = 'CREATE TRIGGER trg_content_data_key_binding_guard BEFORE INSERT ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard()')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid JOIN pg_catalog.pg_language l ON l.oid = p.prolang
                               WHERE g.tgrelid = 'authority.content_data_key_bindings'::regclass AND NOT g.tgisinternal AND g.tgenabled IN ('O', 'A')
                                 AND l.lanname = 'plpgsql' AND NOT p.prosecdef
                                 AND pg_catalog.pg_get_triggerdef(g.oid) = 'CREATE TRIGGER trg_content_data_key_binding_permanent BEFORE DELETE OR UPDATE ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_is_permanent()')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid JOIN pg_catalog.pg_language l ON l.oid = p.prolang
                               WHERE g.tgrelid = 'authority.content_data_key_bindings'::regclass AND NOT g.tgisinternal AND g.tgenabled IN ('O', 'A')
                                 AND l.lanname = 'plpgsql' AND NOT p.prosecdef
                                 AND pg_catalog.pg_get_triggerdef(g.oid) = 'CREATE TRIGGER trg_content_data_key_binding_no_truncate BEFORE TRUNCATE ON authority.content_data_key_bindings FOR EACH STATEMENT EXECUTE FUNCTION authority.content_data_key_binding_is_permanent()') THEN
                RAISE EXCEPTION 'CARV floor 3 refused: the data-key ledger index or its binding-guard and immutability triggers are not present exactly as created';
            END IF;
        """;

    /// <summary>The checks shared by Up and the reinstatement, after the lock.</summary>
    private const string CommonPreconditions = """
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261115014221_AuthorityContentAcceptanceRuleVersion')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                              AND conname = 'ck_content_operation_commit_rule_version' AND contype = 'c' AND convalidated)
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid = 'authority.content_change_operations'::regclass
                              AND tgname = 'trg_content_operation_rule_state_guard' AND NOT tgisinternal AND tgenabled IN ('O', 'A')) THEN
                RAISE EXCEPTION 'CARV floor 3 refused: the content acceptance rule fence (E3-M2b-1) is not fully installed';
            END IF;
        """ + LedgerPreconditions + """
            SELECT count(*) INTO blocking FROM authority.content_change_operations
             WHERE operation_state_name = 'Prepared' AND (prepare_acceptance_rule_version IS NULL OR prepare_acceptance_rule_version < 3)
               AND operation_expires_on > now();
            IF blocking > 0 THEN
                RAISE EXCEPTION 'CARV floor 3 refused: % unexpired operation(s) prepared below content acceptance rule version 3 are in flight; let them expire or abort them first', blocking;
            END IF;
            SELECT count(*) INTO blocking FROM pg_catalog.pg_stat_activity a
             WHERE a.backend_type = 'client backend' AND a.pid <> pg_backend_pid()
               AND a.usename IS NOT NULL AND a.usename <> current_user
               AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname = a.usename AND r.rolsuper)
               AND has_table_privilege(a.usename, 'authority.content_change_operations', 'UPDATE')
               AND coalesce(substring(coalesce(a.application_name, '') FROM 'carv-v([0-9]+)')::numeric, 0) < 3
               AND coalesce(a.application_name, '') <> 'authority-work-listener';
            IF blocking > 0 THEN
                RAISE EXCEPTION 'CARV floor 3 refused: % session(s) able to write content operations are connected without a CARV build marker of version 3 or later (an older Authority instance may still run)', blocking;
            END IF;
        """;

    public static readonly string UpSql = """
        SET LOCAL lock_timeout = '5s';
        LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;
        DO $$
        DECLARE
            blocking bigint;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261115200000_AuthorityContentAcceptanceRuleFloor2')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                              AND conname = 'ck_content_operation_rule_floor_v2' AND contype = 'c')
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                          AND conname LIKE 'ck\_content\_operation\_rule\_floor\_v%' AND conname <> 'ck_content_operation_rule_floor_v2') THEN
                RAISE EXCEPTION 'CARV floor 3 refused: floor 2 must be installed and recorded, and be the only floor (floors are raised one step at a time)';
            END IF;
        """ + CommonPreconditions + """
        END $$;
        ALTER TABLE authority.content_change_operations
            ADD CONSTRAINT ck_content_operation_rule_floor_v3
                CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 3)) NOT VALID;
        ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2;
        """;

    /// <summary>Floors are never lowered (OD-M2-21): the Down refuses, in every environment.</summary>
    public const string DownSql = """
        DO $$
        BEGIN
            RAISE EXCEPTION 'CARV floor down refused: content acceptance rule floors are never lowered';
        END $$;
        """;

    /// <summary>
    /// Recovery only: re-installs floor 3 when the history records it but its constraint is missing. The same lock and common
    /// preconditions as <see cref="UpSql"/>; no floor other than floor 1 or 2 may exist; it adds floor 3 and drops a remaining lower
    /// floor. It never touches the history.
    /// </summary>
    public static readonly string ReinstateSql = """
        SET LOCAL lock_timeout = '5s';
        LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;
        DO $$
        DECLARE
            blocking bigint;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261116040000_AuthorityContentAcceptanceRuleFloor3')
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                          AND conname LIKE 'ck\_content\_operation\_rule\_floor\_v%'
                          AND conname NOT IN ('ck_content_operation_rule_floor_v1', 'ck_content_operation_rule_floor_v2')) THEN
                RAISE EXCEPTION 'CARV floor 3 reinstatement refused: floor 3 is not recorded, or a floor other than floor 1 or 2 is installed';
            END IF;
        """ + CommonPreconditions + """
        END $$;
        ALTER TABLE authority.content_change_operations
            ADD CONSTRAINT ck_content_operation_rule_floor_v3
                CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 3)) NOT VALID;
        ALTER TABLE authority.content_change_operations DROP CONSTRAINT IF EXISTS ck_content_operation_rule_floor_v2;
        ALTER TABLE authority.content_change_operations DROP CONSTRAINT IF EXISTS ck_content_operation_rule_floor_v1;
        """;
}
