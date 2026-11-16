using System.Data.Common;
using AuthorizationAuthority.Application.Features.Content;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>What the content write fence reads from the catalog, in the write transaction, after the table lock.</summary>
public sealed record WriteFenceFacts(
    bool FenceRecorded,
    bool FloorRecorded,
    string? FloorDefinition,
    int? HighestFloor,
    string? CommitCheckDefinition,
    string? PositiveCheckDefinition,
    string? StateGuardDefinition,
    string? StateGuardEnabled,
    string? StateGuardSource,
    int OtherTriggerCount,
    int RuleCount,
    int StampDefaultCount,
    string? RelationKind,
    int ChildCount,
    int? FloorConstraintCount = null,
    IReadOnlySet<int>? RecordedFloors = null)
{
    /// <summary>
    /// E3-M2d: how many constraints named <c>ck_content_operation_rule_floor_v*</c> exist. Null (facts built before E3-M2d, tests)
    /// means "one if <see cref="FloorDefinition"/> is known, otherwise none".
    /// </summary>
    public int EffectiveFloorConstraintCount => FloorConstraintCount ?? (FloorDefinition is null ? 0 : 1);

    /// <summary>E3-M2d: the recognized floors whose migration the history records. Null means floor 1 only, per <see cref="FloorRecorded"/>.</summary>
    public IReadOnlySet<int> EffectiveRecordedFloors => RecordedFloors ?? (FloorRecorded ? new HashSet<int> { ContentAcceptanceRules.MinimumFloor } : new HashSet<int>());
}

/// <summary>
/// RB-CARV-1 (contract §22): verifies, inside a content write transaction, that the database really enforces the required CARV
/// floor and the E3-M2b-1 fence it builds on. Used by <see cref="AuthorityStore"/> for every Release B content write and by the
/// readiness reader (outside a transaction, as a signal only).
/// <para>
/// <b>Race safety.</b> <see cref="LockSql"/> runs first and takes ROW EXCLUSIVE on <c>content_change_operations</c>, held to the end
/// of the transaction. Every statement that can remove, replace or weaken these objects (<c>ALTER TABLE ... DROP/ADD CONSTRAINT</c>,
/// <c>... ALTER COLUMN ... SET DEFAULT</c>, <c>DISABLE TRIGGER</c>, <c>DROP TRIGGER</c>, <c>CREATE RULE</c>, the floor migration itself)
/// needs a lock that conflicts with ROW EXCLUSIVE, so it cannot commit between this verification and the transaction's commit.
/// The write transactions run READ COMMITTED, so the facts statement after the lock sees every catalog change committed before
/// the lock was granted, which is also the definition PostgreSQL enforces on the following writes.
/// <b>Not covered:</b> <c>CREATE OR REPLACE FUNCTION</c> of the state-guard function takes no table lock (its source is verified,
/// but a concurrent replacement by the function owner is not serialized; the floor itself is a CHECK and is unaffected), and a
/// superuser editing the catalogs directly (R-CARV-24).
/// </para>
/// <para>
/// <b>Exact definitions.</b> The expected texts are PostgreSQL 18's rendering of the objects the two migrations create
/// (<c>pg_get_constraintdef</c>, <c>pg_get_triggerdef</c>, <c>pg_proc.prosrc</c>), captured from postgres:18.0. A different server
/// rendering is refused (fail closed, availability only) until verified (R-CARV-23).
/// </para>
/// </summary>
public static class ContentAcceptanceWriteFence
{
    public const string LockSql = "LOCK TABLE authority.content_change_operations IN ROW EXCLUSIVE MODE";

    public const string ExpectedFloorDefinition =
        "CHECK ((((operation_state_name)::text <> ALL ((ARRAY['Prepared'::character varying, 'Committed'::character varying])::text[])) OR ((prepare_acceptance_rule_version IS NOT NULL) AND (prepare_acceptance_rule_version >= 1)))) NOT VALID";

    public const string ExpectedCommitCheckDefinition =
        "CHECK (((((operation_state_name)::text = 'Committed'::text) AND (((prepare_acceptance_rule_version IS NULL) AND (commit_acceptance_rule_version IS NULL)) OR ((prepare_acceptance_rule_version IS NOT NULL) AND (commit_acceptance_rule_version IS NOT NULL) AND (commit_acceptance_rule_version = prepare_acceptance_rule_version)))) OR (((operation_state_name)::text <> 'Committed'::text) AND (commit_acceptance_rule_version IS NULL))))";

    public const string ExpectedPositiveCheckDefinition =
        "CHECK ((((prepare_acceptance_rule_version IS NULL) OR (prepare_acceptance_rule_version >= 1)) AND ((commit_acceptance_rule_version IS NULL) OR (commit_acceptance_rule_version >= 1))))";

    public const string ExpectedStateGuardDefinition =
        "CREATE TRIGGER trg_content_operation_rule_state_guard BEFORE INSERT OR UPDATE ON authority.content_change_operations FOR EACH ROW EXECUTE FUNCTION authority.content_operation_rule_state_guard()";

    /// <summary>The state-guard function body exactly as migration AuthorityContentAcceptanceRuleVersion creates it (line endings normalized).</summary>
    public static readonly string ExpectedStateGuardSource = ExtractFunctionSource(ContentAcceptanceRuleSql.UpSql);

    // Literal object names only; the parameters are the migration ids and the floor constraint name.
    private const string FactsSql = """
        SELECT
          EXISTS (SELECT 1 FROM authority.ef_migrations_history h WHERE h."MigrationId" = @fence) AS fence_recorded,
          EXISTS (SELECT 1 FROM authority.ef_migrations_history h WHERE h."MigrationId" = @floor) AS floor_recorded,
          (SELECT pg_catalog.pg_get_constraintdef(c.oid) FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = t.oid AND c.contype = 'c' AND c.conname ~ '^ck_content_operation_rule_floor_v[0-9]+$'
            ORDER BY substring(c.conname FROM '([0-9]+)$')::integer DESC LIMIT 1) AS floor_definition,
          (SELECT max(substring(c.conname FROM '^ck_content_operation_rule_floor_v([0-9]+)$')::integer) FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = t.oid) AS highest_floor,
          (SELECT pg_catalog.pg_get_constraintdef(c.oid) FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = t.oid AND c.contype = 'c' AND c.conname = 'ck_content_operation_commit_rule_version' AND c.convalidated) AS commit_check_definition,
          (SELECT pg_catalog.pg_get_constraintdef(c.oid) FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = t.oid AND c.contype = 'c' AND c.conname = 'ck_content_operation_rule_versions_positive' AND c.convalidated) AS positive_check_definition,
          (SELECT pg_catalog.pg_get_triggerdef(g.oid) FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_operation_rule_state_guard') AS state_guard_definition,
          (SELECT g.tgenabled::text FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_operation_rule_state_guard') AS state_guard_enabled,
          (SELECT p.prosrc FROM pg_catalog.pg_trigger g
             JOIN pg_catalog.pg_proc p ON p.oid = g.tgfoid
             JOIN pg_catalog.pg_language l ON l.oid = p.prolang
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname = 'trg_content_operation_rule_state_guard'
              AND l.lanname = 'plpgsql' AND NOT p.prosecdef) AS state_guard_source,
          (SELECT count(*)::integer FROM pg_catalog.pg_trigger g
            WHERE g.tgrelid = t.oid AND NOT g.tgisinternal AND g.tgname <> 'trg_content_operation_rule_state_guard') AS other_trigger_count,
          (SELECT count(*)::integer FROM pg_catalog.pg_rewrite r WHERE r.ev_class = t.oid) AS rule_count,
          (SELECT count(*)::integer FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = t.oid AND NOT a.attisdropped
              AND a.attname IN ('prepare_acceptance_rule_version', 'commit_acceptance_rule_version')
              AND (a.atthasdef OR a.attgenerated <> '')) AS stamp_default_count,
          t.relkind::text AS relation_kind,
          (SELECT count(*)::integer FROM pg_catalog.pg_inherits i WHERE i.inhparent = t.oid) AS child_count,
          (SELECT count(*)::integer FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = t.oid AND c.conname LIKE 'ck\_content\_operation\_rule\_floor\_v%') AS floor_constraint_count,
          (SELECT coalesce(array_agg(h."MigrationId" ORDER BY h."MigrationId"), ARRAY[]::text[]) FROM authority.ef_migrations_history h
            WHERE h."MigrationId" = ANY(@floor_migrations)) AS recorded_floor_migrations
        FROM pg_catalog.pg_class t
        WHERE t.oid = 'authority.content_change_operations'::regclass
        """;

    /// <summary>
    /// Reads the facts with one statement on <paramref name="connection"/> (in <paramref name="transaction"/> when given). E3-M2d: the
    /// facts describe the installed floor (the single highest-numbered floor constraint), the number of floor constraints and the
    /// recorded recognized floor migrations, independently of <paramref name="requiredFloor"/>; <see cref="Evaluate"/> compares
    /// them with the required minimum.
    /// </summary>
    public static async Task<WriteFenceFacts> ReadAsync(DbConnection connection, DbTransaction? transaction, int requiredFloor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FactsSql;
        Add(command, "fence", ContentAcceptanceRuleSql.MigrationId);
        Add(command, "floor", ContentAcceptanceFloorSql.MigrationId);
        Add(command, "floor_migrations", ContentAcceptanceFloors.Recognized.Select(f => f.MigrationId).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("content write fence facts query returned no row");
        }

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new WriteFenceFacts(
            reader.GetBoolean(0), reader.GetBoolean(1), Text(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), Text(4), Text(5), Text(6), Text(7), Text(8),
            reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), Text(12), reader.GetInt32(13),
            reader.GetInt32(14), ContentAcceptanceFloors.RecordedFloors(reader.GetFieldValue<string[]>(15)));
    }

    /// <summary>
    /// Null when the database enforces exactly the required floor and the E3-M2b-1 fence; otherwise the first failure reason
    /// (<see cref="ContentAcceptanceFloorFailures"/>). Pure; the order is fixed so diagnostics are deterministic.
    /// </summary>
    public static string? Evaluate(WriteFenceFacts? facts, int requiredFloor, int buildVersion = ContentAcceptanceRules.CurrentVersion)
    {
        // E3-M2d (D-4): the required floor is a minimum and must be a floor this build recognizes (and can compare exactly).
        if (facts is null || ContentAcceptanceFloors.Find(requiredFloor) is null || requiredFloor > buildVersion)
        {
            return ContentAcceptanceFloorFailures.FloorUnverifiable;
        }

        if (!facts.FenceRecorded)
        {
            return ContentAcceptanceFloorFailures.FenceMigrationNotRecorded;
        }

        if (!string.Equals(facts.CommitCheckDefinition, ExpectedCommitCheckDefinition, StringComparison.Ordinal))
        {
            return ContentAcceptanceFloorFailures.FenceCommitCheckMismatch;
        }

        if (!string.Equals(facts.PositiveCheckDefinition, ExpectedPositiveCheckDefinition, StringComparison.Ordinal))
        {
            return ContentAcceptanceFloorFailures.FencePositiveCheckMismatch;
        }

        if (!string.Equals(facts.StateGuardDefinition, ExpectedStateGuardDefinition, StringComparison.Ordinal)
            || facts.StateGuardEnabled is not ("O" or "A")
            || facts.StateGuardSource is null
            || !string.Equals(Normalize(facts.StateGuardSource), ExpectedStateGuardSource, StringComparison.Ordinal))
        {
            return ContentAcceptanceFloorFailures.FenceStateGuardMismatch;
        }

        if (facts.StampDefaultCount != 0)
        {
            return ContentAcceptanceFloorFailures.FenceStampDefaultPresent;
        }

        if (facts.OtherTriggerCount != 0 || facts.RuleCount != 0 || facts.ChildCount != 0 || !string.Equals(facts.RelationKind, "r", StringComparison.Ordinal))
        {
            return ContentAcceptanceFloorFailures.FenceUnexpectedTableObject;
        }

        if (facts.HighestFloor is { } highest && highest > buildVersion)
        {
            return ContentAcceptanceFloorFailures.FloorAboveBuild;
        }

        var recorded = facts.EffectiveRecordedFloors;
        var count = facts.EffectiveFloorConstraintCount;
        if (count == 0)
        {
            if (facts.HighestFloor is { } lower && lower < requiredFloor)
            {
                return ContentAcceptanceFloorFailures.FloorLowered;
            }

            return recorded.Count > 0 ? ContentAcceptanceFloorFailures.FloorRecordedButMissing : ContentAcceptanceFloorFailures.FloorNotInstalled;
        }

        // E3-M2d: exactly one floor constraint, a recognized floor, at least the required minimum, with exactly its definition.
        if (count != 1 || facts.FloorDefinition is null || facts.HighestFloor is not { } installed || ContentAcceptanceFloors.Find(installed) is not { } floor)
        {
            return facts.HighestFloor is { } lowerFloor && lowerFloor < requiredFloor && count == 1
                ? ContentAcceptanceFloorFailures.FloorLowered
                : ContentAcceptanceFloorFailures.FloorDefinitionMismatch;
        }

        if (installed < requiredFloor)
        {
            return ContentAcceptanceFloorFailures.FloorLowered;
        }

        // The floor is permanently NOT VALID (D-4); a validated floor (impossible while unstamped historical rows exist) is not weaker.
        if (!string.Equals(facts.FloorDefinition, floor.ExpectedDefinition, StringComparison.Ordinal)
            && !string.Equals(facts.FloorDefinition + " NOT VALID", floor.ExpectedDefinition, StringComparison.Ordinal))
        {
            return ContentAcceptanceFloorFailures.FloorDefinitionMismatch;
        }

        // A recorded floor above the installed one means a higher floor's constraint is missing: a lowering (monotonic progression).
        if (recorded.Count > 0 && recorded.Max() > installed)
        {
            return ContentAcceptanceFloorFailures.FloorRecordedButMissing;
        }

        // Every floor up to the installed one must be recorded (floors are applied one step at a time by the floor command).
        return ContentAcceptanceFloors.Recognized.Where(f => f.Version <= installed).All(f => recorded.Contains(f.Version)) ? null : ContentAcceptanceFloorFailures.FloorNotRecorded;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The text between <c>AS $$</c> and the closing <c>$$</c>, which is what PostgreSQL stores in <c>prosrc</c>.</summary>
    private static string ExtractFunctionSource(string migrationSql)
    {
        var sql = Normalize(migrationSql);
        var start = sql.IndexOf("AS $$", StringComparison.Ordinal);
        var end = start < 0 ? -1 : sql.IndexOf("$$;", start + 5, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException("the E3-M2b-1 migration SQL no longer contains the state-guard function body");
        }

        return sql[(start + 5)..end];
    }
}
