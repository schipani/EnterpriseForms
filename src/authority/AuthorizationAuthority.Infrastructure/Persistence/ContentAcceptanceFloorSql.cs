namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-1F CARV floor 1: the exact SQL of floor migration <c>AuthorityContentAcceptanceRuleFloor1</c>
/// (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §19.5 and §21). The migration belongs to
/// <see cref="ContentAcceptanceFloorContext"/>, NOT to <see cref="AuthorityDbContext"/>: the ordinary <c>--migrate</c> runner can
/// never discover or apply it (Release A / Release B separation). It is applied only by the explicit operator command
/// <c>--apply-content-acceptance-floor 1</c>.
/// <para>
/// Up runs in one transaction: lock timeout 5 s; ACCESS EXCLUSIVE on <c>content_change_operations</c> (the serialization point);
/// then, while the lock is held, the preconditions (E3-M2b-1 fence complete, no floor yet, no unexpired operation prepared by a
/// pre-CARV build, no connected Authority session without a CARV build marker); then the floor CHECK, permanently NOT VALID
/// (historical rows are unstamped; D-4). Any failure applies nothing, including the history row.
/// </para>
/// </summary>
public static class ContentAcceptanceFloorSql
{
    /// <summary>The floor this migration installs.</summary>
    public const int FloorVersion = 1;

    /// <summary>Migration id; recorded in <c>authority.ef_migrations_history</c> (shared with the Authority context).</summary>
    public const string MigrationId = "20261115032228_AuthorityContentAcceptanceRuleFloor1";

    public const string ConstraintName = "ck_content_operation_rule_floor_v1";

    /// <summary>The floor CHECK: Prepared and Committed rows need a non-NULL Prepare stamp of at least 1 (two-valued: D-1, D-2).</summary>
    public const string FloorCheckSql =
        "operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)";

    /// <summary>Application name of the Authority's LISTEN-only connection, which never writes content operations.</summary>
    public const string WorkListenerApplicationName = "authority-work-listener";

    public const string UpSql = """
        SET LOCAL lock_timeout = '5s';
        LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;
        DO $$
        DECLARE
            blocking bigint;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM authority.ef_migrations_history WHERE "MigrationId" = '20261115014221_AuthorityContentAcceptanceRuleVersion')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                              AND conname = 'ck_content_operation_commit_rule_version' AND contype = 'c' AND convalidated)
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid = 'authority.content_change_operations'::regclass
                              AND tgname = 'trg_content_operation_rule_state_guard' AND NOT tgisinternal AND tgenabled IN ('O', 'A')) THEN
                RAISE EXCEPTION 'CARV floor refused: the content acceptance rule fence (E3-M2b-1) is not fully installed';
            END IF;
            IF EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass
                       AND conname LIKE 'ck\_content\_operation\_rule\_floor\_v%') THEN
                RAISE EXCEPTION 'CARV floor refused: a content acceptance rule floor is already installed';
            END IF;
            SELECT count(*) INTO blocking FROM authority.content_change_operations
             WHERE operation_state_name = 'Prepared' AND prepare_acceptance_rule_version IS NULL AND operation_expires_on > now();
            IF blocking > 0 THEN
                RAISE EXCEPTION 'CARV floor refused: % unexpired operation(s) prepared by a pre-CARV build are in flight; let them expire or abort them first', blocking;
            END IF;
            SELECT count(*) INTO blocking FROM pg_catalog.pg_stat_activity a
             WHERE a.backend_type = 'client backend' AND a.pid <> pg_backend_pid()
               AND a.usename IS NOT NULL AND a.usename <> current_user
               AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname = a.usename AND r.rolsuper)
               AND has_table_privilege(a.usename, 'authority.content_change_operations', 'UPDATE')
               AND coalesce(a.application_name, '') NOT LIKE '%carv-v%'
               AND coalesce(a.application_name, '') <> 'authority-work-listener';
            IF blocking > 0 THEN
                RAISE EXCEPTION 'CARV floor refused: % session(s) able to write content operations are connected without a CARV build marker (a pre-CARV Authority instance may still run)', blocking;
            END IF;
        END $$;
        ALTER TABLE authority.content_change_operations
            ADD CONSTRAINT ck_content_operation_rule_floor_v1
                CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID;
        """;

    /// <summary>Floors are never lowered (OD-M2-21): the Down refuses, in every environment.</summary>
    public const string DownSql = """
        DO $$
        BEGIN
            RAISE EXCEPTION 'CARV floor down refused: content acceptance rule floors are never lowered';
        END $$;
        """;

    /// <summary>
    /// Recovery only (contract §21.6): re-installs floor 1 when the history records it but the constraint is missing (for example a
    /// privileged DROP or a partial restore). The same preconditions and lock as <see cref="UpSql"/>; it never touches the history.
    /// </summary>
    public static string ReinstateSql => UpSql;
}
