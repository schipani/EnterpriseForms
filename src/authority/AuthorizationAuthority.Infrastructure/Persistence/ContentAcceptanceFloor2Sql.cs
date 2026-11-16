namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2d CARV floor 2 (owner decisions D-3, D-4; docs/security/E3_M2D_IMPLEMENTATION_RECORD.md): the exact SQL of floor migration
/// <c>AuthorityContentAcceptanceRuleFloor2</c>. Like floor 1 it belongs to <see cref="ContentAcceptanceFloorContext"/> only, so the
/// ordinary <c>--migrate</c> runner never applies it; it is applied only by <c>--apply-content-acceptance-floor 2</c>, which
/// migrates the floor context to exactly this migration. <b>D-3: exercised only in disposable PostgreSQL; never installed in a
/// persistent environment by this increment.</b>
/// <para>
/// Up runs in one transaction: lock timeout 5 s; ACCESS EXCLUSIVE on <c>content_change_operations</c> (the serialization point);
/// then, under the lock, the preconditions: the E3-M2b-1 fence complete; floor 1 installed, recorded and the only floor (floors
/// are raised one step at a time, never skipped); no unexpired Prepared operation stamped below 2 (or unstamped); no connected
/// session able to write operations whose application name lacks a <c>carv-v</c> marker of at least 2 (the LISTEN-only connection
/// excepted). Then the raise, add-then-drop (§19.5): <c>ck_content_operation_rule_floor_v2</c> (NOT VALID, Prepared and Committed
/// rows need a Prepare stamp of at least 2), then <c>DROP CONSTRAINT ck_content_operation_rule_floor_v1</c>. Any failure applies
/// nothing, including the history row. Down always refuses: floors are never lowered.
/// </para>
/// </summary>
public static class ContentAcceptanceFloor2Sql
{
    public const int FloorVersion = 2;

    public const string MigrationId = "20261115200000_AuthorityContentAcceptanceRuleFloor2";

    public const string ConstraintName = "ck_content_operation_rule_floor_v2";

    /// <summary>The floor CHECK: Prepared and Committed rows need a non-NULL Prepare stamp of at least 2 (two-valued: D-1, D-2).</summary>
    public const string FloorCheckSql =
        "operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)";

    /// <summary>PostgreSQL 18's rendering of the installed floor-2 constraint (pg_get_constraintdef), verified live by the M2d gate.</summary>
    public const string ExpectedDefinition =
        "CHECK ((((operation_state_name)::text <> ALL ((ARRAY['Prepared'::character varying, 'Committed'::character varying])::text[])) OR ((prepare_acceptance_rule_version IS NOT NULL) AND (prepare_acceptance_rule_version >= 2)))) NOT VALID";

    /// <summary>The checks shared by Up and the reinstatement, after the lock (the floor-specific part differs).</summary>
    private const string CommonPreconditions = """
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261115014221_AuthorityContentAcceptanceRuleVersion')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                              AND conname = 'ck_content_operation_commit_rule_version' AND contype = 'c' AND convalidated)
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid = 'authority.content_change_operations'::regclass
                              AND tgname = 'trg_content_operation_rule_state_guard' AND NOT tgisinternal AND tgenabled IN ('O', 'A')) THEN
                RAISE EXCEPTION 'CARV floor 2 refused: the content acceptance rule fence (E3-M2b-1) is not fully installed';
            END IF;
            SELECT count(*) INTO blocking FROM authority.content_change_operations
             WHERE operation_state_name = 'Prepared' AND (prepare_acceptance_rule_version IS NULL OR prepare_acceptance_rule_version < 2)
               AND operation_expires_on > now();
            IF blocking > 0 THEN
                RAISE EXCEPTION 'CARV floor 2 refused: % unexpired operation(s) prepared below content acceptance rule version 2 are in flight; let them expire or abort them first', blocking;
            END IF;
            SELECT count(*) INTO blocking FROM pg_catalog.pg_stat_activity a
             WHERE a.backend_type = 'client backend' AND a.pid <> pg_backend_pid()
               AND a.usename IS NOT NULL AND a.usename <> current_user
               AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname = a.usename AND r.rolsuper)
               AND has_table_privilege(a.usename, 'authority.content_change_operations', 'UPDATE')
               AND coalesce(substring(coalesce(a.application_name, '') FROM 'carv-v([0-9]+)')::numeric, 0) < 2
               AND coalesce(a.application_name, '') <> 'authority-work-listener';
            IF blocking > 0 THEN
                RAISE EXCEPTION 'CARV floor 2 refused: % session(s) able to write content operations are connected without a CARV build marker of version 2 or later (an older Authority instance may still run)', blocking;
            END IF;
        """;

    public const string UpSql = """
        SET LOCAL lock_timeout = '5s';
        LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;
        DO $$
        DECLARE
            blocking bigint;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261115032228_AuthorityContentAcceptanceRuleFloor1')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                              AND conname = 'ck_content_operation_rule_floor_v1' AND contype = 'c')
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                          AND conname LIKE 'ck\_content\_operation\_rule\_floor\_v%' AND conname <> 'ck_content_operation_rule_floor_v1') THEN
                RAISE EXCEPTION 'CARV floor 2 refused: floor 1 must be installed and recorded, and be the only floor (floors are raised one step at a time)';
            END IF;
        """ + CommonPreconditions + """
        END $$;
        ALTER TABLE authority.content_change_operations
            ADD CONSTRAINT ck_content_operation_rule_floor_v2
                CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)) NOT VALID;
        ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1;
        """;

    /// <summary>Floors are never lowered (OD-M2-21): the Down refuses, in every environment.</summary>
    public const string DownSql = """
        DO $$
        BEGIN
            RAISE EXCEPTION 'CARV floor down refused: content acceptance rule floors are never lowered';
        END $$;
        """;

    /// <summary>
    /// Recovery only: re-installs floor 2 when the history records it but its constraint is missing (a privileged DROP, or a lower
    /// floor put back in its place). The same lock and common preconditions as <see cref="UpSql"/>, and no floor of 2 or above may
    /// exist; it adds floor 2 and drops a remaining floor 1. It never touches the history.
    /// </summary>
    public const string ReinstateSql = """
        SET LOCAL lock_timeout = '5s';
        LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;
        DO $$
        DECLARE
            blocking bigint;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261115200000_AuthorityContentAcceptanceRuleFloor2')
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                          AND conname LIKE 'ck\_content\_operation\_rule\_floor\_v%' AND conname <> 'ck_content_operation_rule_floor_v1') THEN
                RAISE EXCEPTION 'CARV floor 2 reinstatement refused: floor 2 is not recorded, or a floor other than floor 1 is installed';
            END IF;
        """ + CommonPreconditions + """
        END $$;
        ALTER TABLE authority.content_change_operations
            ADD CONSTRAINT ck_content_operation_rule_floor_v2
                CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2)) NOT VALID;
        ALTER TABLE authority.content_change_operations DROP CONSTRAINT IF EXISTS ck_content_operation_rule_floor_v1;
        """;
}

/// <summary>
/// E3-M2d (D-4): the floors this build recognizes, with their migration, constraint and exact PostgreSQL 18 definition. Minimum
/// semantics: a Release B deployment requires one recognized floor as a minimum, the database must hold exactly one floor
/// constraint, and that constraint must be a recognized floor with exactly its recognized definition. Anything else fails closed.
/// </summary>
public static class ContentAcceptanceFloors
{
    public sealed record Floor(int Version, string MigrationId, string ConstraintName, string ExpectedDefinition, string UpSql, string ReinstateSql);

    public static readonly IReadOnlyList<Floor> Recognized =
    [
        new(ContentAcceptanceFloorSql.FloorVersion, ContentAcceptanceFloorSql.MigrationId, ContentAcceptanceFloorSql.ConstraintName, ContentAcceptanceWriteFence.ExpectedFloorDefinition, ContentAcceptanceFloorSql.UpSql, ContentAcceptanceFloorSql.ReinstateSql),
        new(ContentAcceptanceFloor2Sql.FloorVersion, ContentAcceptanceFloor2Sql.MigrationId, ContentAcceptanceFloor2Sql.ConstraintName, ContentAcceptanceFloor2Sql.ExpectedDefinition, ContentAcceptanceFloor2Sql.UpSql, ContentAcceptanceFloor2Sql.ReinstateSql),
    ];

    public static Floor? Find(int version) => Recognized.FirstOrDefault(f => f.Version == version);

    /// <summary>The recognized floor versions whose migration id appears in <paramref name="recordedMigrationIds"/>.</summary>
    public static IReadOnlySet<int> RecordedFloors(IEnumerable<string> recordedMigrationIds)
    {
        var ids = recordedMigrationIds.ToHashSet(StringComparer.Ordinal);
        return Recognized.Where(f => ids.Contains(f.MigrationId)).Select(f => f.Version).ToHashSet();
    }
}
