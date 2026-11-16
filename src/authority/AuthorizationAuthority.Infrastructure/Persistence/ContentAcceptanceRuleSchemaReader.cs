using AuthorizationAuthority.Application.Features.Content;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>Port for the CARV readiness guard: what the database fence looks like right now.</summary>
public interface IContentAcceptanceRuleSchemaReader
{
    Task<ContentAcceptanceRules.SchemaFacts> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// E3-M2b-1 (§19.8): reads the CARV fence with the runtime role (catalog views plus <c>SELECT</c> on the migrations history,
/// which the runtime role already holds). One read-only query, no table data.
/// </summary>
public sealed class ContentAcceptanceRuleSchemaReader(IDbContextFactory<AuthorityDbContext> contextFactory) : IContentAcceptanceRuleSchemaReader
{
    // Literal object names only; the parameters are the CARV and floor migration ids.
    private const string FactsSql = """
        SELECT
          EXISTS (SELECT 1 FROM authority.ef_migrations_history h WHERE h."MigrationId" = @migration) AS migration_applied,
          (SELECT count(*) FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = 'authority.content_change_operations'::regclass AND NOT a.attisdropped
              AND a.attname IN ('prepare_acceptance_rule_version', 'commit_acceptance_rule_version')) = 2 AS columns_present,
          EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = 'authority.content_change_operations'::regclass AND c.contype = 'c'
              AND c.conname = 'ck_content_operation_commit_rule_version' AND c.convalidated) AS commit_check_present,
          EXISTS (SELECT 1 FROM pg_catalog.pg_trigger t
            WHERE t.tgrelid = 'authority.content_change_operations'::regclass AND NOT t.tgisinternal
              AND t.tgname = 'trg_content_operation_rule_state_guard' AND t.tgenabled IN ('O', 'A')) AS state_guard_enabled,
          (SELECT max(substring(c.conname FROM '^ck_content_operation_rule_floor_v([0-9]+)$')::integer) FROM pg_catalog.pg_constraint c
            WHERE c.conrelid = 'authority.content_change_operations'::regclass AND c.contype = 'c') AS installed_floor,
          EXISTS (SELECT 1 FROM authority.ef_migrations_history h WHERE h."MigrationId" = @floor) AS floor_recorded,
          (SELECT coalesce(array_agg(h."MigrationId"), ARRAY[]::text[]) FROM authority.ef_migrations_history h
            WHERE h."MigrationId" = ANY(@floor_migrations)) AS recorded_floor_migrations
        """;

    public async Task<ContentAcceptanceRules.SchemaFacts> ReadAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = FactsSql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "migration";
            parameter.Value = ContentAcceptanceRuleSql.MigrationId;
            command.Parameters.Add(parameter);
            var floor = command.CreateParameter();
            floor.ParameterName = "floor";
            floor.Value = ContentAcceptanceFloorSql.MigrationId; // E3-M2b-1F: detects a recorded floor whose constraint is missing
            command.Parameters.Add(floor);
            var floorMigrations = command.CreateParameter();
            floorMigrations.ParameterName = "floor_migrations";
            floorMigrations.Value = ContentAcceptanceFloors.Recognized.Select(f => f.MigrationId).ToArray(); // E3-M2d: every recognized floor
            command.Parameters.Add(floorMigrations);
            ContentAcceptanceRules.SchemaFacts facts;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("content acceptance rule facts query returned no row");
                }

                var recordedFloors = ContentAcceptanceFloors.RecordedFloors(reader.GetFieldValue<string[]>(6));
                facts = new ContentAcceptanceRules.SchemaFacts(
                    reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetBoolean(5),
                    HighestRecordedFloor: recordedFloors.Count == 0 ? null : recordedFloors.Max());
            }

            // RB-CARV-1: the write fence verdict (used by Release B readiness only). Contained, so Release A readiness never depends on it.
            string writeFenceFailure;
            try
            {
                var fence = await ContentAcceptanceWriteFence.ReadAsync(connection, null, ContentAcceptancePolicy.ReleaseBFloor, cancellationToken).ConfigureAwait(false);
                writeFenceFailure = ContentAcceptanceWriteFence.Evaluate(fence, ContentAcceptancePolicy.ReleaseBFloor) ?? string.Empty;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                writeFenceFailure = ContentAcceptanceFloorFailures.FloorUnverifiable;
            }

            return facts with { WriteFenceFailure = writeFenceFailure.Length == 0 ? null : writeFenceFailure };
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
