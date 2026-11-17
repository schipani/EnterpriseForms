using System.Data.Common;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3; OD-M2e-01..04): the SQL of the data-key ledger beyond what EF creates (the
/// table, its CHECKs, unique and lookup indexes and foreign keys come from the model), and the in-transaction verification (L-13).
/// <list type="bullet">
/// <item><b>P-1 binding guard</b> (<c>BEFORE INSERT</c>, row): a binding may only be inserted for a <c>Prepared</c> content operation
/// of the same organization and record whose Prepare rule version is at least 3. It resolves OD-M2-23 in the database.</item>
/// <item><b>P-2 immutability</b> (<c>BEFORE UPDATE OR DELETE</c>, row; <c>BEFORE TRUNCATE</c>, statement): always refused, for every
/// role the trigger fires for, independently of the runtime grants (SELECT and INSERT only).</item>
/// </list>
/// Both functions are plpgsql, not SECURITY DEFINER, with a fixed <c>search_path = pg_catalog, pg_temp</c> and no PUBLIC execute
/// grant (G08 inventory). No SQL here contains a brace (EF's ExecuteSqlRaw reads braces as format items).
/// </summary>
public static class ContentDataKeyLedgerSql
{
    public const string MigrationId = "20261116031706_AuthorityContentDataKeyBindings";

    public const string TableName = "content_data_key_bindings";

    public const string UniqueIdentifierIndex = "uq_content_data_key_binding_identifier";

    public const string GuardTrigger = "trg_content_data_key_binding_guard";

    public const string GuardFunction = "content_data_key_binding_guard";

    public const string PermanentTrigger = "trg_content_data_key_binding_permanent";

    public const string TruncateTrigger = "trg_content_data_key_binding_no_truncate";

    public const string PermanentFunction = "content_data_key_binding_is_permanent";

    /// <summary>The minimum Prepare rule version of an operation that may introduce a binding (CARV 3, the ledger rule version).</summary>
    public const int MinimumBindingRuleVersion = 3;

    /// <summary>First statement of the migration: a bounded wait for the locks the new foreign key and triggers need.</summary>
    public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s';";

    public const string GuardFunctionSql = """
        CREATE FUNCTION authority.content_data_key_binding_guard() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM authority.content_change_operations o
                            WHERE o.content_change_operation_id = NEW.binding_content_change_operation_id
                              AND o.operation_state_name = 'Prepared'
                              AND o.organization_id = NEW.organization_id
                              AND o.form_record_id = NEW.form_record_id
                              AND o.prepare_acceptance_rule_version IS NOT NULL
                              AND o.prepare_acceptance_rule_version >= 3) THEN
                RAISE EXCEPTION 'a data-key binding must belong to a Prepared content operation of the same organization and record, prepared under content acceptance rule version 3 or later'
                    USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_data_key_binding_guard';
            END IF;
            RETURN NEW;
        END $$;
        """;

    public const string PermanentFunctionSql = """
        CREATE FUNCTION authority.content_data_key_binding_is_permanent() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
        BEGIN
            RAISE EXCEPTION 'content data-key bindings are permanent: they are never updated, deleted or truncated'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_data_key_binding_permanent';
        END $$;
        """;

    /// <summary>The migration SQL after the EF-created table: functions, PUBLIC revokes and the three triggers.</summary>
    public static readonly string TriggersSql = GuardFunctionSql + PermanentFunctionSql + """
        REVOKE ALL ON FUNCTION authority.content_data_key_binding_guard() FROM PUBLIC;
        REVOKE ALL ON FUNCTION authority.content_data_key_binding_is_permanent() FROM PUBLIC;
        CREATE TRIGGER trg_content_data_key_binding_guard
            BEFORE INSERT ON authority.content_data_key_bindings
            FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard();
        CREATE TRIGGER trg_content_data_key_binding_permanent
            BEFORE UPDATE OR DELETE ON authority.content_data_key_bindings
            FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_is_permanent();
        CREATE TRIGGER trg_content_data_key_binding_no_truncate
            BEFORE TRUNCATE ON authority.content_data_key_bindings
            FOR EACH STATEMENT EXECUTE FUNCTION authority.content_data_key_binding_is_permanent();
        """;

    /// <summary>
    /// Down (Development/Testing only): refuses while any binding exists, then drops the triggers and functions (EF drops the table
    /// afterwards). It runs before the table drop, so a non-empty ledger is never removed.
    /// </summary>
    public const string DownGuardSql = """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM authority.content_data_key_bindings) THEN
                RAISE EXCEPTION 'the data-key ledger migration cannot be reverted while any binding exists';
            END IF;
        END $$;
        DROP TRIGGER trg_content_data_key_binding_no_truncate ON authority.content_data_key_bindings;
        DROP TRIGGER trg_content_data_key_binding_permanent ON authority.content_data_key_bindings;
        DROP TRIGGER trg_content_data_key_binding_guard ON authority.content_data_key_bindings;
        DROP FUNCTION authority.content_data_key_binding_is_permanent();
        DROP FUNCTION authority.content_data_key_binding_guard();
        """;

    // ---- L-13: the in-transaction ledger verification ----

    /// <summary>Taken first: conflicts with every statement that drops, replaces or disables the index or triggers, until the transaction ends.</summary>
    public const string LockSql = "LOCK TABLE authority.content_data_key_bindings IN ROW EXCLUSIVE MODE";

    /// <summary>PostgreSQL 18's renderings (pg_get_indexdef / pg_get_triggerdef), verified live by the E3-M2e-1 gate (R-CARV-23 applies).</summary>
    public const string ExpectedUniqueIndexDefinition =
        "CREATE UNIQUE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings USING btree (data_key_identifier)";

    public const string ExpectedGuardTriggerDefinition =
        "CREATE TRIGGER trg_content_data_key_binding_guard BEFORE INSERT ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard()";

    public const string ExpectedPermanentTriggerDefinition =
        "CREATE TRIGGER trg_content_data_key_binding_permanent BEFORE DELETE OR UPDATE ON authority.content_data_key_bindings FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_is_permanent()";

    public const string ExpectedTruncateTriggerDefinition =
        "CREATE TRIGGER trg_content_data_key_binding_no_truncate BEFORE TRUNCATE ON authority.content_data_key_bindings FOR EACH STATEMENT EXECUTE FUNCTION authority.content_data_key_binding_is_permanent()";

    public static readonly string ExpectedGuardSource = FunctionSource(GuardFunctionSql);

    public static readonly string ExpectedPermanentSource = FunctionSource(PermanentFunctionSql);

    private const string FactsSql = """
        SELECT
          EXISTS (SELECT 1 FROM authority.ef_migrations_history h WHERE h."MigrationId" = @ledger) AS ledger_recorded,
          (SELECT pg_catalog.pg_get_indexdef(i.indexrelid) FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid
            WHERE i.indrelid = t.oid AND c.relname = 'uq_content_data_key_binding_identifier' AND i.indisunique AND i.indisvalid AND i.indisready) AS unique_index,
          (SELECT pg_catalog.pg_get_triggerdef(g.oid) FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_data_key_binding_guard' AND g.tgenabled IN ('O', 'A')) AS guard_definition,
          (SELECT p.prosrc FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid JOIN pg_catalog.pg_language l ON l.oid = p.prolang
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_data_key_binding_guard' AND l.lanname = 'plpgsql' AND NOT p.prosecdef) AS guard_source,
          (SELECT pg_catalog.pg_get_triggerdef(g.oid) FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_data_key_binding_permanent' AND g.tgenabled IN ('O', 'A')) AS permanent_definition,
          (SELECT pg_catalog.pg_get_triggerdef(g.oid) FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_data_key_binding_no_truncate' AND g.tgenabled IN ('O', 'A')) AS truncate_definition,
          (SELECT count(DISTINCT p.prosrc)::integer FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid JOIN pg_catalog.pg_language l ON l.oid = p.prolang
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname IN ('trg_content_data_key_binding_permanent', 'trg_content_data_key_binding_no_truncate')
              AND l.lanname = 'plpgsql' AND NOT p.prosecdef) AS permanent_source_count,
          (SELECT min(p.prosrc) FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname IN ('trg_content_data_key_binding_permanent', 'trg_content_data_key_binding_no_truncate')) AS permanent_source,
          (SELECT count(*)::integer FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal
              AND g.tgname NOT IN ('trg_content_data_key_binding_guard', 'trg_content_data_key_binding_permanent', 'trg_content_data_key_binding_no_truncate')) AS other_trigger_count,
          (SELECT count(*)::integer FROM pg_catalog.pg_rewrite r WHERE r.ev_class = t.oid) AS rule_count,
          (SELECT count(*)::integer FROM pg_catalog.pg_inherits i WHERE i.inhparent = t.oid) AS child_count,
          t.relkind::text AS relation_kind
        FROM pg_catalog.pg_class t
        WHERE t.oid = 'authority.content_data_key_bindings'::regclass
        """;

    /// <summary>What the ledger verification reads, in the write transaction, after <see cref="LockSql"/>.</summary>
    public sealed record LedgerFacts(
        bool LedgerRecorded,
        string? UniqueIndexDefinition,
        string? GuardTriggerDefinition,
        string? GuardSource,
        string? PermanentTriggerDefinition,
        string? TruncateTriggerDefinition,
        int PermanentSourceCount,
        string? PermanentSource,
        int OtherTriggerCount,
        int RuleCount,
        int ChildCount,
        string? RelationKind);

    public static async Task<LedgerFacts> ReadAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FactsSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "ledger";
        parameter.Value = MigrationId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("data-key ledger facts query returned no row");
        }

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new LedgerFacts(reader.GetBoolean(0), Text(1), Text(2), Text(3), Text(4), Text(5), reader.GetInt32(6), Text(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), Text(11));
    }

    /// <summary>Failure reasons of <see cref="Evaluate"/> (diagnostic codes only; no identifiers).</summary>
    public static class Failures
    {
        public const string LedgerMigrationNotRecorded = "DataKeyLedgerMigrationNotRecorded";
        public const string UniqueIndexMismatch = "DataKeyLedgerUniqueIndexMismatch";
        public const string GuardMismatch = "DataKeyLedgerGuardMismatch";
        public const string PermanenceMismatch = "DataKeyLedgerPermanenceMismatch";
        public const string UnexpectedTableObject = "DataKeyLedgerUnexpectedTableObject";
        public const string Unverifiable = "DataKeyLedgerUnverifiable";
    }

    /// <summary>Null when the ledger is enforced exactly as created; otherwise the first failure (fixed order). Pure.</summary>
    public static string? Evaluate(LedgerFacts? facts)
    {
        if (facts is null)
        {
            return Failures.Unverifiable;
        }

        if (!facts.LedgerRecorded)
        {
            return Failures.LedgerMigrationNotRecorded;
        }

        if (!string.Equals(facts.UniqueIndexDefinition, ExpectedUniqueIndexDefinition, StringComparison.Ordinal))
        {
            return Failures.UniqueIndexMismatch;
        }

        if (!string.Equals(facts.GuardTriggerDefinition, ExpectedGuardTriggerDefinition, StringComparison.Ordinal)
            || facts.GuardSource is null || !string.Equals(Normalize(facts.GuardSource), ExpectedGuardSource, StringComparison.Ordinal))
        {
            return Failures.GuardMismatch;
        }

        if (!string.Equals(facts.PermanentTriggerDefinition, ExpectedPermanentTriggerDefinition, StringComparison.Ordinal)
            || !string.Equals(facts.TruncateTriggerDefinition, ExpectedTruncateTriggerDefinition, StringComparison.Ordinal)
            || facts.PermanentSourceCount != 1 || facts.PermanentSource is null
            || !string.Equals(Normalize(facts.PermanentSource), ExpectedPermanentSource, StringComparison.Ordinal))
        {
            return Failures.PermanenceMismatch;
        }

        return facts.OtherTriggerCount != 0 || facts.RuleCount != 0 || facts.ChildCount != 0 || !string.Equals(facts.RelationKind, "r", StringComparison.Ordinal)
            ? Failures.UnexpectedTableObject
            : null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The text between <c>AS $$</c> and the closing <c>$$</c>, which PostgreSQL stores in <c>prosrc</c>.</summary>
    private static string FunctionSource(string functionSql)
    {
        var sql = Normalize(functionSql);
        var start = sql.IndexOf("AS $$", StringComparison.Ordinal);
        var end = start < 0 ? -1 : sql.IndexOf("$$;", start + 5, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException("the data-key ledger function SQL has no body");
        }

        return sql[(start + 5)..end];
    }
}
