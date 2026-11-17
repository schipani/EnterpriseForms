using AuthorizationAuthority.Application.Features.Content;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>The catalogue state of the capability-refusal evidence index (E3-M2f-3i).</summary>
public enum CapabilityRefusalEvidenceIndexState
{
    /// <summary>No relation of that name exists in the schema.</summary>
    Absent,

    /// <summary>Present, valid, ready and live, with exactly the approved definition, ownership and privileges.</summary>
    ValidMatching,

    /// <summary>Approved definition, but not valid, ready or live, and no build is visibly in progress (an interrupted or failed build).</summary>
    Invalid,

    /// <summary>Approved definition, not yet valid, and another session is visibly building it or holds the table's build lock.</summary>
    BuildInProgress,

    /// <summary>A relation of that name exists whose definition, ownership or privileges differ from the approved ones.</summary>
    DefinitionMismatch,
}

/// <summary>Catalogue facts of the relation named <see cref="CapabilityRefusalEvidenceIndex.IndexName"/> in the schema.</summary>
public sealed record CapabilityRefusalEvidenceIndexFacts(
    long RelationOid,
    string RelationKind,
    bool OnTargetTable,
    string? AccessMethod,
    bool IsUnique,
    bool IsPrimary,
    bool IsExclusion,
    bool IsValid,
    bool IsReady,
    bool IsLive,
    int AttributeCount,
    int KeyAttributeCount,
    bool HasExpressions,
    IReadOnlyList<string> KeyColumns,
    string? KeyOptions,
    IReadOnlyList<string> OperatorClasses,
    string? Predicate,
    string? Definition,
    bool OwnedByTableOwner,
    bool HasPrivileges,
    bool BuildVisiblyInProgress);

/// <summary>The classification of one inspection, with every deviation from the approved definition named.</summary>
public sealed record CapabilityRefusalEvidenceIndexInspection(CapabilityRefusalEvidenceIndexState State, CapabilityRefusalEvidenceIndexFacts? Facts, IReadOnlyList<string> Mismatches);

/// <summary>
/// E3-M2f-3i (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5A; owner approval of OD-IX-1, 2026-11-17): the approved partial,
/// non-unique B-tree index IX-B on <c>authority.security_journal_entries</c> that serves the capability-refusal evidence lookup
/// (IXP-6), and its catalogue verification. It is provisioned only by the dedicated privileged command
/// (<c>--provision-capability-refusal-evidence-index</c>), never by the ordinary <c>--migrate</c> and never by the EF model, so
/// no EF migration, migration-history lock or runtime-grant re-application is involved. It confers no permission and changes no data.
/// <para>
/// The index is a cost device only: the lookup's result is defined by SQL semantics whatever plan is used (closure L-D). The
/// stream and kind appear as SQL literals so PostgreSQL can prove that a query implies the partial predicate, also for a generic
/// prepared plan (closure §5A.2).
/// </para>
/// </summary>
public static class CapabilityRefusalEvidenceIndex
{
    public const string SchemaName = AuthorityDbContext.SchemaName;
    public const string TableName = "security_journal_entries";
    public const string IndexName = "ix_journal_capability_refusal_evidence";

    /// <summary>IXP-1, verbatim: one statement, executed alone and outside any transaction block.</summary>
    public const string CreateSql =
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS " + IndexName + " ON " + SchemaName + "." + TableName
        + " (organization_id, journal_stream_revision) WHERE journal_stream_name = '" + CapabilityRefusalEvidence.StreamName
        + "' AND journal_event_kind = '" + CapabilityRefusalEvidence.EventKind + "'";

    /// <summary>The approved key columns, in order (both ascending, default NULLS LAST).</summary>
    public static readonly IReadOnlyList<string> KeyColumns = ["organization_id", "journal_stream_revision"];

    /// <summary>The default B-tree operator classes of <c>uuid</c> and <c>bigint</c>.</summary>
    public static readonly IReadOnlyList<string> OperatorClasses = ["uuid_ops", "int8_ops"];

    /// <summary><c>indoption</c> of two ascending, NULLS LAST keys.</summary>
    public const string KeyOptions = "0 0";

    /// <summary>PostgreSQL 18's <c>pg_get_expr(indpred, indrelid)</c> rendering of the approved predicate (pinned; IXP-8, TQ-19).</summary>
    public const string ExpectedPredicate =
        "(((journal_stream_name)::text = '" + CapabilityRefusalEvidence.StreamName + "'::text) AND ((journal_event_kind)::text = '"
        + CapabilityRefusalEvidence.EventKind + "'::text))";

    /// <summary>PostgreSQL 18's <c>pg_get_indexdef</c> rendering of the approved index (pinned; IXP-8, IXP-11, TQ-19).</summary>
    public const string ExpectedDefinition =
        "CREATE INDEX " + IndexName + " ON " + SchemaName + "." + TableName + " USING btree (organization_id, journal_stream_revision) WHERE "
        + ExpectedPredicate;

    /// <summary>
    /// IXP-6: the evidence lookup the index serves (stream and kind as literals; only the organization is a parameter). E3-M2f-3i uses
    /// it for query-plan verification only; the recorder that executes it is E3-M2f-3b.
    /// </summary>
    public const string EvidenceLookupSql =
        "SELECT security_journal_entry_id, journal_stream_revision, journal_event_body_json, journal_event_digest FROM " + SchemaName + "."
        + TableName + " WHERE organization_id = @organization_id AND journal_stream_name = '" + CapabilityRefusalEvidence.StreamName
        + "' AND journal_event_kind = '" + CapabilityRefusalEvidence.EventKind + "' ORDER BY journal_stream_revision DESC LIMIT 8";

    /// <summary>E3-M2f-3b T2-3: the evidence transaction's lock-wait bound, set before the organization lock (closure §5.7: about 1 s).</summary>
    public const string EvidenceLockTimeoutSql = "SET LOCAL lock_timeout = '1s'";

    /// <summary>E3-M2f-3b IXP-7: the evidence transaction's statement bound, set after the organization lock (closure §5A.4: 500 ms).</summary>
    public const string EvidenceStatementTimeoutSql = "SET LOCAL statement_timeout = '500ms'";

    /// <summary>
    /// E3-M2f-3b T2-4 / closure §5.2: the authoritative time, read in its own statement after the organization lock. Never
    /// <c>now()</c> or <c>transaction_timestamp()</c>, which are fixed at transaction start, before the lock wait.
    /// </summary>
    public const string DatabaseClockSql = "SELECT pg_catalog.clock_timestamp()";

    /// <summary>Whether the target table exists and the current role may act as its owner (CREATE INDEX requires ownership).</summary>
    public const string TargetSql =
        "SELECT to_regclass('" + SchemaName + "." + TableName + "') IS NOT NULL AS table_exists, "
        + "coalesce((SELECT pg_has_role(current_user, t.relowner, 'USAGE') FROM pg_catalog.pg_class t WHERE t.oid = to_regclass('" + SchemaName + "." + TableName + "')), false) AS may_act_as_owner";

    /// <summary>
    /// The catalogue facts of the relation of that name. Reads only pg_catalog; the two deparsing calls (which take ACCESS SHARE
    /// on the indexed table in PostgreSQL 18) are made only when the index is on the journal itself, never on the migration history or
    /// the organization table. A build is "visibly in progress" when <c>pg_stat_progress_create_index</c> reports this
    /// index, or another session holds SHARE UPDATE EXCLUSIVE on the table (a concurrent build, or VACUUM/ANALYZE: conservative).
    /// </summary>
    public const string FactsSql =
        "SELECT c.oid::bigint AS relation_oid, c.relkind::text AS relation_kind, "
        + "coalesce(i.indrelid = to_regclass('" + SchemaName + "." + TableName + "'), false) AS on_target_table, "
        + "am.amname::text AS access_method, coalesce(i.indisunique, false) AS is_unique, coalesce(i.indisprimary, false) AS is_primary, "
        + "coalesce(i.indisexclusion, false) AS is_exclusion, coalesce(i.indisvalid, false) AS is_valid, coalesce(i.indisready, false) AS is_ready, "
        + "coalesce(i.indislive, false) AS is_live, coalesce(i.indnatts, 0)::integer AS attribute_count, coalesce(i.indnkeyatts, 0)::integer AS key_attribute_count, "
        + "(i.indexprs IS NOT NULL) AS has_expressions, "
        + "coalesce(ARRAY(SELECT a.attname::text FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum ORDER BY k.ord), ARRAY[]::text[]) AS key_columns, "
        + "i.indoption::text AS key_options, "
        + "coalesce(ARRAY(SELECT oc.opcname::text FROM unnest(i.indclass::oid[]) WITH ORDINALITY AS k(opc, ord) "
        + "JOIN pg_catalog.pg_opclass oc ON oc.oid = k.opc ORDER BY k.ord), ARRAY[]::text[]) AS operator_classes, "
        + "CASE WHEN i.indrelid = to_regclass('" + SchemaName + "." + TableName + "') THEN pg_catalog.pg_get_expr(i.indpred, i.indrelid) END AS predicate, "
        + "CASE WHEN i.indrelid = to_regclass('" + SchemaName + "." + TableName + "') THEN pg_catalog.pg_get_indexdef(c.oid) END AS definition, "
        + "coalesce(c.relowner = t.relowner, false) AS owned_by_table_owner, (c.relacl IS NOT NULL) AS has_privileges, "
        + "(EXISTS (SELECT 1 FROM pg_catalog.pg_stat_progress_create_index p WHERE p.index_relid = c.oid) "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_locks l WHERE l.locktype = 'relation' AND l.relation = i.indrelid AND l.granted "
        + "AND l.mode = 'ShareUpdateExclusiveLock' AND l.pid <> pg_catalog.pg_backend_pid())) AS build_visibly_in_progress "
        + "FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + "LEFT JOIN pg_catalog.pg_index i ON i.indexrelid = c.oid LEFT JOIN pg_catalog.pg_class t ON t.oid = i.indrelid "
        + "LEFT JOIN pg_catalog.pg_am am ON am.oid = c.relam "
        + "WHERE n.nspname = '" + SchemaName + "' AND c.relname = '" + IndexName + "'";

    /// <summary>
    /// A canonical, line-per-object listing of the catalogue state the command must not change (relations, indexes, constraints,
    /// triggers, functions, schemas, roles, memberships, default privileges, database privileges), outside temporary and system
    /// schemas. Before and after a successful run the difference must be exactly the new index or nothing.
    /// <para>
    /// Lock-free by construction: it reads raw catalogue columns only (<c>pg_node_tree</c> text, <c>regclass</c>/<c>regprocedure</c>
    /// output). PostgreSQL 18's deparsing functions (<c>pg_get_indexdef</c>, <c>pg_get_expr</c>, <c>pg_get_constraintdef</c> of a CHECK,
    /// <c>pg_get_triggerdef</c> of a trigger with WHEN) take ACCESS SHARE on the underlying table, which would make the command wait on
    /// (for example) the organization table or the migration history (verified on PostgreSQL 18; E3-M2f-3i record).
    /// </para>
    /// </summary>
    public const string CatalogueSnapshotSql =
        "WITH nsp AS (SELECT oid, nspname FROM pg_catalog.pg_namespace WHERE nspname NOT IN ('pg_catalog', 'information_schema') "
        + "AND nspname NOT LIKE 'pg\\_toast%' AND nspname NOT LIKE 'pg\\_temp\\_%') "
        + "SELECT 'rel|' || n.nspname || '|' || c.relname || '|' || c.relkind::text || '|' || pg_catalog.pg_get_userbyid(c.relowner) || '|' || coalesce(c.relacl::text, '') "
        + "FROM pg_catalog.pg_class c JOIN nsp n ON n.oid = c.relnamespace "
        + "UNION ALL SELECT 'idx|' || n.nspname || '.' || ci.relname || '|' || tn.nspname || '.' || ct.relname || '|' || i.indisunique::text || '|' || i.indisprimary::text || '|' "
        + "|| i.indisexclusion::text || '|' || i.indimmediate::text || '|' || i.indisvalid::text || '|' || i.indisready::text || '|' || i.indislive::text || '|' "
        + "|| i.indkey::text || '|' || i.indclass::text || '|' || i.indcollation::text || '|' || i.indoption::text || '|' || coalesce(i.indexprs::text, '') || '|' || coalesce(i.indpred::text, '') "
        + "FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class ci ON ci.oid = i.indexrelid JOIN nsp n ON n.oid = ci.relnamespace "
        + "JOIN pg_catalog.pg_class ct ON ct.oid = i.indrelid JOIN pg_catalog.pg_namespace tn ON tn.oid = ct.relnamespace "
        + "UNION ALL SELECT 'con|' || n.nspname || '|' || co.conname || '|' || co.contype::text || '|' || co.conrelid::regclass::text || '|' || coalesce(co.conkey::text, '') || '|' "
        + "|| coalesce(co.confkey::text, '') || '|' || co.confrelid::regclass::text || '|' || coalesce(co.conbin::text, '') || '|' || co.convalidated::text "
        + "FROM pg_catalog.pg_constraint co JOIN nsp n ON n.oid = co.connamespace "
        + "UNION ALL SELECT 'trg|' || tg.tgrelid::regclass::text || '|' || tg.tgname || '|' || tg.tgenabled::text || '|' || tg.tgfoid::regprocedure::text || '|' || tg.tgtype::text || '|' "
        + "|| coalesce(tg.tgqual::text, '') FROM pg_catalog.pg_trigger tg JOIN pg_catalog.pg_class c ON c.oid = tg.tgrelid JOIN nsp n ON n.oid = c.relnamespace WHERE NOT tg.tgisinternal "
        + "UNION ALL SELECT 'fn|' || p.oid::regprocedure::text || '|' || pg_catalog.pg_get_userbyid(p.proowner) || '|' || coalesce(p.proacl::text, '') || '|' || p.prosecdef::text || '|' || md5(p.prosrc) "
        + "FROM pg_catalog.pg_proc p JOIN nsp n ON n.oid = p.pronamespace "
        + "UNION ALL SELECT 'nsp|' || ns.nspname || '|' || pg_catalog.pg_get_userbyid(ns.nspowner) || '|' || coalesce(ns.nspacl::text, '') "
        + "FROM pg_catalog.pg_namespace ns JOIN nsp n ON n.oid = ns.oid "
        + "UNION ALL SELECT 'role|' || r.rolname || '|' || r.rolsuper::text || '|' || r.rolinherit::text || '|' || r.rolcreaterole::text || '|' || r.rolcreatedb::text || '|' "
        + "|| r.rolcanlogin::text || '|' || r.rolreplication::text || '|' || r.rolbypassrls::text FROM pg_catalog.pg_roles r "
        + "UNION ALL SELECT 'mem|' || pg_catalog.pg_get_userbyid(m.roleid) || '|' || pg_catalog.pg_get_userbyid(m.member) || '|' || m.admin_option::text "
        + "FROM pg_catalog.pg_auth_members m "
        + "UNION ALL SELECT 'dacl|' || coalesce(d.defaclnamespace::regnamespace::text, '') || '|' || pg_catalog.pg_get_userbyid(d.defaclrole) || '|' || d.defaclobjtype::text || '|' || coalesce(d.defaclacl::text, '') "
        + "FROM pg_catalog.pg_default_acl d "
        + "UNION ALL SELECT 'db|' || db.datname || '|' || coalesce(db.datacl::text, '') FROM pg_catalog.pg_database db WHERE db.datname = current_database()";

    /// <summary>The relation line a successful creation adds (owner = the table owner, no privileges).</summary>
    public static string CreatedRelationLine(string tableOwner) => "rel|" + SchemaName + "|" + IndexName + "|i|" + tableOwner + "|";

    /// <summary>
    /// The prefix of the index line a successful creation adds: the index on the journal, not unique, primary or exclusion,
    /// immediate, valid, ready and live. Its full structure is verified separately by <see cref="InspectAsync"/>.
    /// </summary>
    public const string CreatedIndexLinePrefix = "idx|" + SchemaName + "." + IndexName + "|" + SchemaName + "." + TableName + "|false|false|false|true|true|true|true|";

    /// <summary>Whether a catalogue difference is exactly one successful creation: the index relation and its index row, nothing else.</summary>
    public static bool IsExactlyTheCreation(IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed, string tableOwner)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);
        return removed.Count == 0 && added.Count == 2
               && added.Contains(CreatedRelationLine(tableOwner))
               && added.Count(l => l.StartsWith(CreatedIndexLinePrefix, StringComparison.Ordinal)) == 1;
    }

    /// <summary>Classifies an inspection. Definition, ownership and privilege deviations take precedence over validity.</summary>
    public static CapabilityRefusalEvidenceIndexInspection Classify(CapabilityRefusalEvidenceIndexFacts? facts)
    {
        if (facts is null)
        {
            return new(CapabilityRefusalEvidenceIndexState.Absent, null, []);
        }

        var mismatches = new List<string>();
        if (facts.RelationKind != "i")
        {
            mismatches.Add("relation kind is '" + facts.RelationKind + "', not an index");
        }
        else
        {
            if (!facts.OnTargetTable)
            {
                mismatches.Add("not an index on " + SchemaName + "." + TableName);
            }

            if (!string.Equals(facts.AccessMethod, "btree", StringComparison.Ordinal))
            {
                mismatches.Add("access method is '" + facts.AccessMethod + "', not btree");
            }

            if (facts.IsUnique || facts.IsPrimary || facts.IsExclusion)
            {
                mismatches.Add("unique, primary-key or exclusion index; the approved index is none of these");
            }

            if (facts.HasExpressions)
            {
                mismatches.Add("has expression columns");
            }

            if (facts.AttributeCount != KeyColumns.Count || facts.KeyAttributeCount != KeyColumns.Count)
            {
                mismatches.Add("column count " + facts.AttributeCount + " (key " + facts.KeyAttributeCount + "), expected " + KeyColumns.Count + " key columns and no INCLUDE columns");
            }

            if (!facts.KeyColumns.SequenceEqual(KeyColumns, StringComparer.Ordinal))
            {
                mismatches.Add("key columns (" + string.Join(", ", facts.KeyColumns) + "), expected (" + string.Join(", ", KeyColumns) + ")");
            }

            if (!string.Equals(facts.KeyOptions, KeyOptions, StringComparison.Ordinal))
            {
                mismatches.Add("key ordering options '" + facts.KeyOptions + "', expected '" + KeyOptions + "' (ascending, NULLS LAST)");
            }

            if (!facts.OperatorClasses.SequenceEqual(OperatorClasses, StringComparer.Ordinal))
            {
                mismatches.Add("operator classes (" + string.Join(", ", facts.OperatorClasses) + "), expected (" + string.Join(", ", OperatorClasses) + ")");
            }

            if (!string.Equals(facts.Predicate, ExpectedPredicate, StringComparison.Ordinal))
            {
                mismatches.Add("partial predicate differs from the approved predicate");
            }

            if (!string.Equals(facts.Definition, ExpectedDefinition, StringComparison.Ordinal))
            {
                mismatches.Add("pg_get_indexdef differs from the approved definition");
            }
        }

        if (!facts.OwnedByTableOwner)
        {
            mismatches.Add("not owned by the table owner");
        }

        if (facts.HasPrivileges)
        {
            mismatches.Add("carries explicit privileges (relacl is not null)");
        }

        if (mismatches.Count > 0)
        {
            return new(CapabilityRefusalEvidenceIndexState.DefinitionMismatch, facts, mismatches);
        }

        if (facts.IsValid && facts.IsReady && facts.IsLive)
        {
            return new(CapabilityRefusalEvidenceIndexState.ValidMatching, facts, []);
        }

        return new(facts.BuildVisiblyInProgress ? CapabilityRefusalEvidenceIndexState.BuildInProgress : CapabilityRefusalEvidenceIndexState.Invalid, facts, []);
    }

    /// <summary>Reads the catalogue facts on an open connection (autocommit; no transaction is started).</summary>
    public static async Task<CapabilityRefusalEvidenceIndexInspection> InspectAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(FactsSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Classify(null);
        }

        var facts = new CapabilityRefusalEvidenceIndexFacts(
            reader.GetInt64(reader.GetOrdinal("relation_oid")),
            reader.GetString(reader.GetOrdinal("relation_kind")),
            reader.GetBoolean(reader.GetOrdinal("on_target_table")),
            reader.IsDBNull(reader.GetOrdinal("access_method")) ? null : reader.GetString(reader.GetOrdinal("access_method")),
            reader.GetBoolean(reader.GetOrdinal("is_unique")),
            reader.GetBoolean(reader.GetOrdinal("is_primary")),
            reader.GetBoolean(reader.GetOrdinal("is_exclusion")),
            reader.GetBoolean(reader.GetOrdinal("is_valid")),
            reader.GetBoolean(reader.GetOrdinal("is_ready")),
            reader.GetBoolean(reader.GetOrdinal("is_live")),
            reader.GetInt32(reader.GetOrdinal("attribute_count")),
            reader.GetInt32(reader.GetOrdinal("key_attribute_count")),
            reader.GetBoolean(reader.GetOrdinal("has_expressions")),
            reader.GetFieldValue<string[]>(reader.GetOrdinal("key_columns")),
            reader.IsDBNull(reader.GetOrdinal("key_options")) ? null : reader.GetString(reader.GetOrdinal("key_options")),
            reader.GetFieldValue<string[]>(reader.GetOrdinal("operator_classes")),
            reader.IsDBNull(reader.GetOrdinal("predicate")) ? null : reader.GetString(reader.GetOrdinal("predicate")),
            reader.IsDBNull(reader.GetOrdinal("definition")) ? null : reader.GetString(reader.GetOrdinal("definition")),
            reader.GetBoolean(reader.GetOrdinal("owned_by_table_owner")),
            reader.GetBoolean(reader.GetOrdinal("has_privileges")),
            reader.GetBoolean(reader.GetOrdinal("build_visibly_in_progress")));
        return Classify(facts);
    }

    /// <summary>The catalogue snapshot lines (see <see cref="CatalogueSnapshotSql"/>).</summary>
    public static async Task<HashSet<string>> SnapshotAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var lines = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(CatalogueSnapshotSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                lines.Add(reader.GetString(0));
            }
        }

        return lines;
    }
}
