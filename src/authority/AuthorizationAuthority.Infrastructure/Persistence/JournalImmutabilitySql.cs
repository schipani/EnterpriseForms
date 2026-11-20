namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// SEC-BL-01 G08 (C-158, contract §11 M-6 as amended by A-7 §27): database enforcement of security-journal immutability.
/// <list type="bullet">
/// <item><b>G8-1 immutable fields</b> (<c>BEFORE UPDATE</c>, row): identity, organization, stream, revision, kind, command and operation
/// identifiers, body, digest, previous digest and creation time never change.</item>
/// <item><b>G8-2 delivery-state transitions</b>: <c>Pending → Pending</c> (attempt count and error code only, no receipt fields),
/// <c>Pending → Anchored</c> (receipt reference, receipt digest and archived-on time all required), <c>Pending → VerificationFailed</c>
/// (no receipt fields). <c>Anchored</c> is final. <c>VerificationFailed</c> changes only by the G14-R2 recovery hint
/// (<c>→ Pending</c> with code <c>DPLUS_RECOVERY_AUTHORIZED:…</c>, no receipt fields; A-4 EX-2). The attempt count never decreases.</item>
/// <item><b>G8-3 no deletion</b> (<c>BEFORE DELETE</c>, row; <c>BEFORE TRUNCATE</c>, statement): always refused.</item>
/// <item><b>G8-4</b> no INSERT trigger (M-6: it would fire inside T2 and contradict M2F3-54); a forged insertion is detected by D-R and never
/// carries a trusted receipt (E-6/E-7).</item>
/// </list>
/// Functions are plpgsql, SECURITY INVOKER (the default), with a fixed <c>search_path = pg_catalog, pg_temp</c>, owned by the migration identity,
/// with PUBLIC execute revoked. <b>Limit:</b> an AuthorityDb administrator or superuser (J2) can disable or drop triggers, change grants, or set
/// <c>session_replication_role = replica</c>; G08 narrows J1 and does not constrain J2, which stays detection-only (D-R) and can never forge a
/// trusted receipt.
/// </summary>
public static class JournalImmutabilitySql
{
    public const string MigrationId = "20261119021623_AuthorityJournalImmutabilityG08";

    public const string TableName = "security_journal_entries";

    public const string GuardFunction = "security_journal_entry_guard";

    public const string GuardTrigger = "trg_security_journal_entry_guard";

    public const string TruncateFunction = "security_journal_entry_no_truncate";

    public const string TruncateTrigger = "trg_security_journal_entry_no_truncate";

    /// <summary>The columns the runtime role may UPDATE (column-level grant; M-6 "delivery metadata", E-5). Everything else is insert-only.</summary>
    public static readonly string[] RuntimeUpdatableColumns =
        ["archive_delivery_state", "archive_attempt_count", "archive_receipt_reference", "archive_receipt_digest", "journal_archived_on", "last_archive_error_code"];

    public const string RecoveryHintPrefix = "DPLUS_RECOVERY_AUTHORIZED:";

    public const string GuardFunctionSql = """
        CREATE FUNCTION authority.security_journal_entry_guard() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
        BEGIN
            -- Every condition is NULL-safe (IS DISTINCT FROM, coalesce): a NULL must refuse, never pass (G08 attempt-1 defect).
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'G08: security journal entries are append-only; they are never deleted' USING ERRCODE = 'P0001';
            END IF;
            IF NEW.security_journal_entry_id IS DISTINCT FROM OLD.security_journal_entry_id
               OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
               OR NEW.journal_stream_name IS DISTINCT FROM OLD.journal_stream_name
               OR NEW.journal_stream_revision IS DISTINCT FROM OLD.journal_stream_revision
               OR NEW.journal_event_kind IS DISTINCT FROM OLD.journal_event_kind
               OR NEW.security_command_id IS DISTINCT FROM OLD.security_command_id
               OR NEW.content_change_operation_id IS DISTINCT FROM OLD.content_change_operation_id
               OR NEW.journal_event_body_json IS DISTINCT FROM OLD.journal_event_body_json
               OR NEW.journal_event_digest IS DISTINCT FROM OLD.journal_event_digest
               OR NEW.journal_previous_digest IS DISTINCT FROM OLD.journal_previous_digest
               OR NEW.journal_created_on IS DISTINCT FROM OLD.journal_created_on THEN
                RAISE EXCEPTION 'G08: immutable security journal fields cannot change' USING ERRCODE = 'P0001';
            END IF;
            IF NEW.archive_attempt_count < OLD.archive_attempt_count THEN
                RAISE EXCEPTION 'G08: the archive attempt count never decreases' USING ERRCODE = 'P0001';
            END IF;
            IF OLD.archive_delivery_state = 'Anchored' THEN
                IF NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'G08: an Anchored security journal entry is final' USING ERRCODE = 'P0001';
                END IF;
            ELSIF OLD.archive_delivery_state = 'VerificationFailed' THEN
                IF NEW IS DISTINCT FROM OLD AND NOT (NEW.archive_delivery_state = 'Pending'
                        AND coalesce(NEW.last_archive_error_code, '') LIKE 'DPLUS\_RECOVERY\_AUTHORIZED:%' ESCAPE '\'
                        AND NEW.archive_receipt_reference IS NULL AND NEW.archive_receipt_digest IS NULL AND NEW.journal_archived_on IS NULL
                        AND NEW.archive_attempt_count = OLD.archive_attempt_count) THEN
                    RAISE EXCEPTION 'G08: a VerificationFailed entry changes only through the recovery scheduling hint' USING ERRCODE = 'P0001';
                END IF;
            ELSIF NEW.archive_delivery_state = 'Anchored' THEN
                IF NEW.archive_receipt_reference IS NULL OR NEW.archive_receipt_digest IS NULL OR NEW.journal_archived_on IS NULL THEN
                    RAISE EXCEPTION 'G08: an entry becomes Anchored only with its receipt' USING ERRCODE = 'P0001';
                END IF;
            ELSIF NEW.archive_receipt_reference IS DISTINCT FROM OLD.archive_receipt_reference OR NEW.archive_receipt_digest IS DISTINCT FROM OLD.archive_receipt_digest
                  OR NEW.journal_archived_on IS DISTINCT FROM OLD.journal_archived_on THEN
                RAISE EXCEPTION 'G08: a receipt is recorded only with the Anchored state' USING ERRCODE = 'P0001';
            END IF;
            RETURN NEW;
        END
        $$;

        """;

    public const string TruncateFunctionSql = """
        CREATE FUNCTION authority.security_journal_entry_no_truncate() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
        BEGIN
            RAISE EXCEPTION 'G08: the security journal is append-only; it is never truncated' USING ERRCODE = 'P0001';
        END
        $$;

        """;

    public static readonly string UpSql = "SET LOCAL lock_timeout = '5s';\n" + GuardFunctionSql + TruncateFunctionSql + """
        REVOKE ALL ON FUNCTION authority.security_journal_entry_guard() FROM PUBLIC;
        REVOKE ALL ON FUNCTION authority.security_journal_entry_no_truncate() FROM PUBLIC;
        CREATE TRIGGER trg_security_journal_entry_guard
            BEFORE UPDATE OR DELETE ON authority.security_journal_entries
            FOR EACH ROW EXECUTE FUNCTION authority.security_journal_entry_guard();
        CREATE TRIGGER trg_security_journal_entry_no_truncate
            BEFORE TRUNCATE ON authority.security_journal_entries
            FOR EACH STATEMENT EXECUTE FUNCTION authority.security_journal_entry_no_truncate();
        """;

    /// <summary>Down (Development/Testing): removes the triggers and functions only; journal rows are untouched.</summary>
    public const string DownSql = """
        DROP TRIGGER IF EXISTS trg_security_journal_entry_no_truncate ON authority.security_journal_entries;
        DROP TRIGGER IF EXISTS trg_security_journal_entry_guard ON authority.security_journal_entries;
        DROP FUNCTION IF EXISTS authority.security_journal_entry_no_truncate();
        DROP FUNCTION IF EXISTS authority.security_journal_entry_guard();
        """;
}
