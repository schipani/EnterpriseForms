using AuthorizationAuthority.Application.Features.Content;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-1 CARV database fence: the exact SQL of migration <c>AuthorityContentAcceptanceRuleVersion</c>
/// (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §19.5). The model configuration uses the same CHECK expressions, so the
/// EF snapshot and the database agree. The later floor migration (E3-M2b-1F) is deliberately NOT here: it is a separate,
/// later release (OD-M2-21/28) because EF <c>Migrate()</c> applies every pending migration at once.
/// </summary>
public static class ContentAcceptanceRuleSql
{
    /// <summary>Migration id; the readiness guard requires it in <c>authority.ef_migrations_history</c>.</summary>
    public const string MigrationId = "20261115014221_AuthorityContentAcceptanceRuleVersion";

    public const string PositiveCheckName = ContentAcceptanceRules.DatabaseObjects.PositiveCheck;

    public const string PositiveCheckSql =
        "(prepare_acceptance_rule_version IS NULL OR prepare_acceptance_rule_version >= 1)\n"
        + "           AND (commit_acceptance_rule_version IS NULL OR commit_acceptance_rule_version >= 1)";

    public const string CommitCheckName = ContentAcceptanceRules.DatabaseObjects.CommitCheck;

    public const string CommitCheckSql =
        "(operation_state_name = 'Committed'\n"
        + "                AND ((prepare_acceptance_rule_version IS NULL AND commit_acceptance_rule_version IS NULL)\n"
        + "                  OR (prepare_acceptance_rule_version IS NOT NULL AND commit_acceptance_rule_version IS NOT NULL\n"
        + "                      AND commit_acceptance_rule_version = prepare_acceptance_rule_version)))\n"
        + "            OR (operation_state_name <> 'Committed' AND commit_acceptance_rule_version IS NULL)";

    /// <summary>
    /// Up, in the migration's own transaction (atomic: a lock timeout or any error applies nothing). The trigger is a state guard:
    /// inserts are Prepared without a commit stamp, terminal rows are immutable, and the Prepare stamp never changes (D-5).
    /// </summary>
    public const string UpSql = """
        SET LOCAL lock_timeout = '5s';
        ALTER TABLE authority.content_change_operations
            ADD COLUMN prepare_acceptance_rule_version integer NULL,
            ADD COLUMN commit_acceptance_rule_version integer NULL;
        ALTER TABLE authority.content_change_operations
            ADD CONSTRAINT ck_content_operation_rule_versions_positive
                CHECK ((prepare_acceptance_rule_version IS NULL OR prepare_acceptance_rule_version >= 1)
                   AND (commit_acceptance_rule_version IS NULL OR commit_acceptance_rule_version >= 1)),
            ADD CONSTRAINT ck_content_operation_commit_rule_version
                CHECK ((operation_state_name = 'Committed'
                        AND ((prepare_acceptance_rule_version IS NULL AND commit_acceptance_rule_version IS NULL)
                          OR (prepare_acceptance_rule_version IS NOT NULL AND commit_acceptance_rule_version IS NOT NULL
                              AND commit_acceptance_rule_version = prepare_acceptance_rule_version)))
                    OR (operation_state_name <> 'Committed' AND commit_acceptance_rule_version IS NULL));
        CREATE FUNCTION authority.content_operation_rule_state_guard() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                IF NEW.operation_state_name <> 'Prepared' OR NEW.commit_acceptance_rule_version IS NOT NULL THEN
                    RAISE EXCEPTION 'content operation must be inserted as Prepared without a commit rule version'
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_operation_rule_state_guard';
                END IF;
                RETURN NEW;
            END IF;
            IF OLD.operation_state_name <> 'Prepared' THEN
                RAISE EXCEPTION 'a terminal content operation is immutable'
                    USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_operation_rule_state_guard';
            END IF;
            IF NEW.prepare_acceptance_rule_version IS DISTINCT FROM OLD.prepare_acceptance_rule_version THEN
                RAISE EXCEPTION 'content operation prepare rule version is immutable'
                    USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_operation_rule_state_guard';
            END IF;
            RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION authority.content_operation_rule_state_guard() FROM PUBLIC;
        CREATE TRIGGER trg_content_operation_rule_state_guard
            BEFORE INSERT OR UPDATE ON authority.content_change_operations
            FOR EACH ROW EXECUTE FUNCTION authority.content_operation_rule_state_guard();
        """;

    /// <summary>
    /// Down: Development/Testing only, guarded. Refuses once any floor exists (floors are never lowered) or while operations
    /// prepared under CARV rules are in flight; otherwise removes the trigger, function, CHECKs and columns.
    /// </summary>
    public const string DownSql = """
        SET LOCAL lock_timeout = '5s';
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname LIKE 'ck_content_operation_rule_floor_v%') THEN
                RAISE EXCEPTION 'CARV down refused: a content acceptance rule floor is installed; floors are never lowered';
            END IF;
            IF EXISTS (SELECT 1 FROM authority.content_change_operations WHERE operation_state_name = 'Prepared' AND prepare_acceptance_rule_version IS NOT NULL) THEN
                RAISE EXCEPTION 'CARV down refused: operations prepared under CARV rules are in flight';
            END IF;
        END $$;
        DROP TRIGGER trg_content_operation_rule_state_guard ON authority.content_change_operations;
        DROP FUNCTION authority.content_operation_rule_state_guard();
        ALTER TABLE authority.content_change_operations
            DROP CONSTRAINT ck_content_operation_commit_rule_version,
            DROP CONSTRAINT ck_content_operation_rule_versions_positive,
            DROP COLUMN commit_acceptance_rule_version,
            DROP COLUMN prepare_acceptance_rule_version;
        """;
}
