// E3-M2b-2 business real-PostgreSQL gate harness (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §23). Development/Testing
// only. Drives THIS tree's Release binaries (the real migration, EF model, signed-schema registry, clone helpers, business
// schema guard, the Worker startup guard and the API monitor) against a disposable PostgreSQL 18 container created by
// scripts/dev/e3-m2b2-business-pg-gate.ps1. Phases: pre | up | post | restore. Exit code 0 only if every check passed.
using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var phase = args.Length > 0 ? args[0] : throw new InvalidOperationException("phase required: pre | up | post | restore");
var gate = new Gate(phase);
try
{
    await (phase switch
    {
        "pre" => gate.PreAsync(),
        "up" => gate.UpAsync(),
        "post" => gate.PostAsync(),
        "restore" => gate.RestoreAsync(),
        _ => throw new InvalidOperationException("unknown phase " + phase),
    });
}
catch (Exception ex)
{
    gate.Record("HARNESS", "the phase ran to completion", false, ex.GetType().Name + ": " + ex.Message);
}

return gate.Finish();

internal sealed class Gate(string phase)
{
    public const string Previous = "20261103054837_PhaseC8M3RegionalPreferences";
    public const string Target = "20261115125042_EnvelopeV2SignedColumns";
    private const string Runtime = "business_api_runtime";

    private readonly List<object> checks = [];
    private readonly string admin = Env("GATE_ADMIN");
    private readonly string runtime = Env("GATE_RUNTIME");
    private readonly string evidence = Env("GATE_EVIDENCE");

    private static readonly string[] NewTableChecks =
    [
        EnvelopeMetadataConstraints.FieldEnvelopeVersionName, EnvelopeMetadataConstraints.FieldSchemaEnvelopeTieName, EnvelopeMetadataConstraints.FieldMetadataName,
        EnvelopeMetadataConstraints.AttachmentEnvelopeVersionName, EnvelopeMetadataConstraints.AttachmentSchemaEnvelopeTieName, EnvelopeMetadataConstraints.AttachmentMetadataName,
    ];

    private static readonly (string Table, string Column)[] NewColumns =
    [
        ("field_encrypted_values", "data_key_identifier"), ("field_encrypted_values", "data_key_commitment_bytes"), ("field_encrypted_values", "key_wrap_algorithm_code"),
        ("file_attachment_versions", "data_key_identifier"), ("file_attachment_versions", "data_key_commitment_bytes"), ("file_attachment_versions", "key_wrap_algorithm_code"),
    ];

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is not set");

    public void Record(string id, string description, bool passed, string? detail = null)
    {
        checks.Add(new { checkId = id, description, passed, detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {id} {description}{(detail is null ? string.Empty : " :: " + detail)}");
    }

    public int Finish()
    {
        var failed = checks.Count(c => !(bool)c.GetType().GetProperty("passed")!.GetValue(c)!);
        File.WriteAllText(Path.Combine(evidence, $"gate-{phase}.json"), JsonSerializer.Serialize(new { gate = "E3-M2b-2 business PostgreSQL gate", phase, checksTotal = checks.Count, checksFailed = failed, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static string WithDatabase(string connectionString, string database) => new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    private static string Replica(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c session_replication_role=replica" }.ConnectionString;

    private static BusinessDbContext Context(string connectionString) => new(new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static PostgresException? Postgres(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg)
            {
                return pg;
            }
        }

        return null;
    }

    private async Task<bool> MigrationRecordedAsync(string connectionString) =>
        await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Target}'") == 1;

    private static async Task<long> CountColumnsAsync(string connectionString) =>
        await ScalarAsync<long>(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND (table_name, column_name) IN ("
            + string.Join(", ", NewColumns.Select(c => $"('{c.Table}', '{c.Column}')")) + ")");

    private static async Task<long> CountConstraintsAsync(string connectionString, IEnumerable<string> names, bool validatedOnly = false) =>
        await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM pg_constraint WHERE contype = 'c' {(validatedOnly ? "AND convalidated" : string.Empty)} AND conname IN ({string.Join(", ", names.Select(n => $"'{n}'"))})");

    private BusinessSchemaGuard NewGuard(string connectionString)
    {
        var factory = new GateContextFactory(connectionString);
        return new BusinessSchemaGuard(new BusinessMigrationHistoryReader(factory), TimeProvider.System, NullLogger<BusinessSchemaGuard>.Instance);
    }

    private static void Migrate(string connectionString, string target)
    {
        using var context = Context(connectionString);
        context.GetService<IMigrator>().Migrate(target);
    }

    // ---------------------------------------------------------------- pre: the database before the E3-M2b-2 migration

    public async Task PreAsync()
    {
        Migrate(admin, Previous);
        BusinessRuntimeGrants.Apply(Context(admin), Runtime);
        Record("PRE-01", "baseline: history at the previous migration, without the E3-M2b-2 migration or its columns",
            !await MigrationRecordedAsync(admin) && await CountColumnsAsync(admin) == 0 && await CountConstraintsAsync(admin, [EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName]) == 1);

        // The new binary refuses an unmigrated database (real reader, runtime role).
        var guard = NewGuard(runtime);
        var state = await guard.RefreshAsync(CancellationToken.None);
        Record("PRE-02", "the business schema guard reports MigrationMissing for an unmigrated database (runtime role, real reader)",
            state == BusinessSchemaState.MigrationMissing && !guard.Current.Ready, $"{state} / {guard.Current.Detail}");

        var workerLifetime = new GateLifetime();
        var workerGuard = new EnterpriseForms.Worker.Composition.BusinessSchemaStartupGuard(NewGuard(runtime), workerLifetime, NullLogger<EnterpriseForms.Worker.Composition.BusinessSchemaStartupGuard>.Instance);
        Exception? refused = null;
        try
        {
            await workerGuard.StartAsync(CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            refused = ex;
        }

        Record("PRE-03", "the real Worker startup guard refuses to start on the unmigrated database and requests a host stop", refused is not null && workerLifetime.StopRequested, refused?.Message);

        var apiLifetime = new GateLifetime();
        var monitor = new EnterpriseForms.Api.Composition.BusinessSchemaMonitor(NewGuard(runtime), apiLifetime);
        await monitor.StartAsync(CancellationToken.None);
        var stopped = await apiLifetime.WaitForStopAsync(TimeSpan.FromSeconds(15));
        await monitor.StopAsync(CancellationToken.None);
        Record("PRE-04", "the real API monitor stops the host on the unmigrated database", stopped);

        Exception? query = null;
        try
        {
            await using var context = Context(runtime);
            _ = await context.Set<FieldEncryptedValue>().AsNoTracking().Take(1).ToListAsync(); // materializing selects every mapped column
        }
        catch (Exception ex)
        {
            query = ex;
        }

        Record("PRE-05", "the new EF model cannot read the envelope table of an unmigrated database (42703: fail closed even without the guard)", Postgres(query)?.SqlState == "42703", Postgres(query)?.SqlState);

        // Migration atomicity: an existing row that violates a new CHECK makes the whole migration fail and apply nothing.
        await ExecAsync(Replica(admin), InsertSql("field_encrypted_values", schemaVersion: 2, envelopeVersion: 1));
        Exception? failed = null;
        try
        {
            Migrate(admin, Target);
        }
        catch (Exception ex)
        {
            failed = ex;
        }

        var pg = Postgres(failed);
        Record("PRE-06", "a pre-existing schema/envelope mismatch makes the migration fail (23514) and apply nothing (no columns, no CHECKs, old CHECK kept, no history row)",
            pg?.SqlState == "23514" && !await MigrationRecordedAsync(admin) && await CountColumnsAsync(admin) == 0 && await CountConstraintsAsync(admin, NewTableChecks) == 0
            && await CountConstraintsAsync(admin, [EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName]) == 1, $"{pg?.SqlState} {pg?.ConstraintName}");
        await ExecAsync(admin, "DELETE FROM field_encrypted_values");

        // Lock timeout: an open transaction on file_attachment_versions makes the migration time out (55P03) after the
        // field_encrypted_values statements already ran, and the whole transaction rolls back.
        await using (var holder = new NpgsqlConnection(admin))
        {
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT count(*) FROM file_attachment_versions", holder, transaction))
            {
                await hold.ExecuteScalarAsync();
            }

            var started = DateTimeOffset.UtcNow;
            Exception? timedOut = null;
            try
            {
                Migrate(admin, Target);
            }
            catch (Exception ex)
            {
                timedOut = ex;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            await transaction.RollbackAsync();
            Record("PRE-07", "a concurrent open transaction makes the migration fail with lock_timeout (55P03) within the bound and roll back completely",
                Postgres(timedOut)?.SqlState == "55P03" && elapsed < TimeSpan.FromSeconds(30) && !await MigrationRecordedAsync(admin) && await CountColumnsAsync(admin) == 0
                && await CountConstraintsAsync(admin, [EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName]) == 1, $"{Postgres(timedOut)?.SqlState} after {elapsed.TotalSeconds:F1} s");
        }
    }

    /// <summary>A raw INSERT of one row into a pre-migration envelope table (every NOT NULL column filled; FKs disabled by the replica session).</summary>
    private static string InsertSql(string table, int schemaVersion, int envelopeVersion) => table == "field_encrypted_values"
        ? "INSERT INTO field_encrypted_values (field_encrypted_value_id, tenant_id, form_record_id, field_definition_id, field_value_item_id, content_change_operation_id, "
          + "content_revision, row_revision, row_signature, row_signature_key_version, row_signature_schema_version, created_on, created_by_user_id, encrypted_value_bytes, "
          + "encryption_nonce_bytes, encryption_auth_tag_bytes, wrapped_data_key_bytes, encryption_algorithm_name, encryption_key_reference, encryption_envelope_version, encrypted_logical_type) "
          + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, 1, decode(repeat('ab', 32), 'hex'), 1, "
          + $"{schemaVersion}, now(), gen_random_uuid(), decode('0102', 'hex'), decode(repeat('0b', 12), 'hex'), decode(repeat('aa', 16), 'hex'), decode(repeat('cc', 60), 'hex'), "
          + $"'AES-256-GCM', 'local-dev-kek-v1', {envelopeVersion}, 'String')"
        : throw new NotSupportedException(table);

    // ---------------------------------------------------------------- up: the migration and the migrated database

    public async Task UpAsync()
    {
        Migrate(admin, Target);
        BusinessRuntimeGrants.Apply(Context(admin), Runtime);
        var definitions = new List<string>();
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT conrelid::regclass || '.' || conname || ': ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = ANY(@names) ORDER BY 1", connection);
            command.Parameters.AddWithValue("names", NewTableChecks);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                definitions.Add(reader.GetString(0));
            }
        }

        File.WriteAllLines(Path.Combine(evidence, "constraint-definitions.txt"), definitions);
        var nullable = await ScalarAsync<long>(admin, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND is_nullable = 'YES' AND column_default IS NULL AND (table_name, column_name, data_type) IN ("
            + string.Join(", ", NewColumns.Select(c => $"('{c.Table}', '{c.Column}', '{(c.Column == "data_key_identifier" ? "uuid" : c.Column == "data_key_commitment_bytes" ? "bytea" : "integer")}')")) + ")");
        Record("UP-01", "the migration applies: history row, six nullable columns without defaults (uuid/bytea/integer), six validated CHECKs, the open envelope_positive CHECK dropped",
            await MigrationRecordedAsync(admin) && nullable == 6 && await CountConstraintsAsync(admin, NewTableChecks, validatedOnly: true) == 6
            && await CountConstraintsAsync(admin, [EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName]) == 0, string.Join(" | ", definitions));

        var guard = NewGuard(runtime);
        Record("UP-02", "the business schema guard is ready on the migrated database (runtime role, real reader)", await guard.RefreshAsync(CancellationToken.None) == BusinessSchemaState.Ready && guard.Current.Ready);
        var workerLifetime = new GateLifetime();
        await new EnterpriseForms.Worker.Composition.BusinessSchemaStartupGuard(NewGuard(runtime), workerLifetime, NullLogger<EnterpriseForms.Worker.Composition.BusinessSchemaStartupGuard>.Instance).StartAsync(CancellationToken.None);
        var apiLifetime = new GateLifetime();
        var monitor = new EnterpriseForms.Api.Composition.BusinessSchemaMonitor(NewGuard(runtime), apiLifetime);
        await monitor.StartAsync(CancellationToken.None);
        var stopped = await apiLifetime.WaitForStopAsync(TimeSpan.FromSeconds(3));
        await monitor.StopAsync(CancellationToken.None);
        Record("UP-03", "the real Worker startup guard starts and the real API monitor does not stop the host on the migrated database", !workerLifetime.StopRequested && !stopped);

        var unreachable = NewGuard(new NpgsqlConnectionStringBuilder(runtime) { Host = "127.0.0.1", Port = 1, Timeout = 3 }.ConnectionString);
        Record("UP-04", "an unreachable database is Unreadable (not ready, no host stop)", await unreachable.RefreshAsync(CancellationToken.None) == BusinessSchemaState.Unreadable && !unreachable.Current.Ready);

        await CheckMatrixAsync("UP-05", admin);
        await RoundTripAndCloneAsync();
        await RuntimeRoleAsync();
    }

    private sealed record Case(string Label, ISignedContentRow Row, string? ExpectedConstraint);

    private static IEnumerable<Case> Matrix()
    {
        foreach (var kind in new[] { "field", "attachment" })
        {
            var metadata = kind == "field" ? EnvelopeMetadataConstraints.FieldMetadataName : EnvelopeMetadataConstraints.AttachmentMetadataName;
            var tie = kind == "field" ? EnvelopeMetadataConstraints.FieldSchemaEnvelopeTieName : EnvelopeMetadataConstraints.AttachmentSchemaEnvelopeTieName;
            var version = kind == "field" ? EnvelopeMetadataConstraints.FieldEnvelopeVersionName : EnvelopeMetadataConstraints.AttachmentEnvelopeVersionName;
            ISignedContentRow Make(int schema, int envelope, bool withMetadata, Action<ISignedContentRow>? alter = null)
            {
                var row = kind == "field" ? (ISignedContentRow)Rows.Field(schema, envelope, withMetadata) : Rows.Attachment(schema, envelope, withMetadata);
                alter?.Invoke(row);
                return row;
            }

            yield return new($"{kind} schema 1 / envelope 1 without metadata", Make(1, 1, false), null);
            yield return new($"{kind} schema 2 / envelope 2 with complete metadata (wrap 1)", Make(2, 2, true), null);
            yield return new($"{kind} schema 2 / envelope 2 with complete metadata (wrap 2)", Make(2, 2, true, r => Rows.SetWrap(r, 2)), null);
            yield return new($"{kind} envelope 1 with a data-key identifier", Make(1, 1, false, r => Rows.SetIdentifier(r, Guid.CreateVersion7())), metadata);
            yield return new($"{kind} envelope 1 with a commitment", Make(1, 1, false, r => Rows.SetCommitment(r, new byte[32])), metadata);
            yield return new($"{kind} envelope 1 with a wrap code", Make(1, 1, false, r => Rows.SetWrap(r, 1)), metadata);
            yield return new($"{kind} envelope 2 with a NULL data-key identifier", Make(2, 2, true, r => Rows.SetIdentifier(r, null)), metadata);
            yield return new($"{kind} envelope 2 with a NULL commitment", Make(2, 2, true, r => Rows.SetCommitment(r, null)), metadata);
            yield return new($"{kind} envelope 2 with a NULL wrap code (D-1 regression)", Make(2, 2, true, r => Rows.SetWrap(r, null)), metadata);
            yield return new($"{kind} envelope 2 with a 31-byte commitment", Make(2, 2, true, r => Rows.SetCommitment(r, new byte[31])), metadata);
            yield return new($"{kind} envelope 2 with a 33-byte commitment", Make(2, 2, true, r => Rows.SetCommitment(r, new byte[33])), metadata);
            yield return new($"{kind} envelope 2 with wrap code 3", Make(2, 2, true, r => Rows.SetWrap(r, 3)), metadata);
            yield return new($"{kind} envelope 2 with wrap code 0", Make(2, 2, true, r => Rows.SetWrap(r, 0)), metadata);
            yield return new($"{kind} schema 1 / envelope 2 (tie)", Make(1, 2, true), tie);
            yield return new($"{kind} schema 2 / envelope 1 (tie)", Make(2, 1, false), tie);
            // Envelope 3 violates the closed-version CHECK and the metadata CHECK; PostgreSQL reports the first it evaluates.
            yield return new($"{kind} schema 3 / envelope 3 (closed versions)", Make(3, 3, true), version + "|" + metadata);
        }
    }

    private async Task CheckMatrixAsync(string id, string connectionString)
    {
        var failures = new List<string>();
        var cases = Matrix().ToList();
        foreach (var c in cases)
        {
            string? violated = null;
            try
            {
                await using var context = Context(Replica(connectionString));
                context.Add((object)c.Row);
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (Postgres(ex) is { SqlState: "23514" } pg)
            {
                violated = pg.ConstraintName;
            }

            if (c.ExpectedConstraint is null ? violated is not null : violated is null || !c.ExpectedConstraint.Split('|').Contains(violated))
            {
                failures.Add($"{c.Label}: expected {c.ExpectedConstraint ?? "accepted"}, got {violated ?? "accepted"}");
            }
        }

        Record(id, $"CHECK matrix ({cases.Count} cases, both tables): each v2 column NULL alone, lengths, wrap codes, v1 with metadata, both tie directions, closed versions", failures.Count == 0, failures.Count == 0 ? null : string.Join("; ", failures));
    }

    private async Task RoundTripAndCloneAsync()
    {
        var originals = new ISignedContentRow[] { Rows.Field(1, 1, false), Rows.Field(2, 2, true), Rows.Attachment(1, 1, false), Rows.Attachment(2, 2, true) };
        await using (var context = Context(Replica(admin)))
        {
            foreach (var row in originals)
            {
                context.Add((object)row);
            }

            await context.SaveChangesAsync();
        }

        var cloneValue = typeof(CandidateBuilder).Assembly.GetType("EnterpriseForms.Application.Features.Documents.UploadSessions.UploadSessionHandlers", throwOnError: true)!
            .GetMethod("CloneValue", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var failures = new List<string>();
        var encodings = new Dictionary<string, string>();
        foreach (var original in originals)
        {
            var read = await ReadAsync(admin, original);
            if (!Rows.SameEnvelope(original, read) || read.RowSignatureSchemaVersion != original.RowSignatureSchemaVersion)
            {
                failures.Add(Rows.Describe(original) + ": EF round trip changed the envelope or schema version");
            }

            var encodedOriginal = Convert.ToHexStringLower(BusinessSignedSchemas.Encode(original, 1));
            if (encodedOriginal != Convert.ToHexStringLower(BusinessSignedSchemas.Encode(read, 1)))
            {
                failures.Add(Rows.Describe(original) + ": canonical bytes differ after the round trip");
            }

            encodings[original.RowPrimaryKey.ToString("D")] = encodedOriginal;

            // Clone as every carry does, re-bound to a new operation exactly as the stager re-binds, persisted and read back.
            ISignedContentRow clone = read is FieldEncryptedValue f ? (ISignedContentRow)cloneValue.Invoke(null, [f])! : CandidateBuilder.CloneAttachment((FileAttachmentVersion)read);
            clone.ContentChangeOperationId = Guid.CreateVersion7();
            clone.ContentRevision = 2;
            clone.RowRevision = 2;
            clone.RowSignature = Enumerable.Repeat((byte)0x5a, 32).ToArray();
            clone.RowSignatureKeyVersion = 1;
            await using (var context = Context(Replica(admin)))
            {
                context.Add((object)clone);
                await context.SaveChangesAsync();
            }

            var cloneRead = await ReadAsync(admin, clone);
            if (!Rows.SameEnvelope(original, cloneRead) || cloneRead.RowSignatureSchemaVersion != original.RowSignatureSchemaVersion || BusinessSignedSchemas.CurrentSchemaVersion(cloneRead) != original.RowSignatureSchemaVersion)
            {
                failures.Add(Rows.Describe(original) + ": the persisted clone lost envelope metadata or the schema version");
            }
        }

        File.WriteAllText(Path.Combine(evidence, "round-trip-encodings.json"), JsonSerializer.Serialize(encodings, new JsonSerializerOptions { WriteIndented = true }));
        Record("UP-06", "EF round trip and clone persistence: v1 and v2 field and document rows keep every envelope column, the schema version and their canonical bytes; persisted clones keep them too",
            failures.Count == 0, failures.Count == 0 ? null : string.Join("; ", failures));
    }

    private static async Task<ISignedContentRow> ReadAsync(string connectionString, ISignedContentRow row)
    {
        await using var context = Context(connectionString);
        return row switch
        {
            FieldEncryptedValue f => await context.Set<FieldEncryptedValue>().AsNoTracking().SingleAsync(v => v.FieldEncryptedValueId == f.FieldEncryptedValueId),
            FileAttachmentVersion a => await context.Set<FileAttachmentVersion>().AsNoTracking().SingleAsync(v => v.FileAttachmentVersionId == a.FileAttachmentVersionId),
            _ => throw new NotSupportedException(),
        };
    }

    private async Task RuntimeRoleAsync()
    {
        var attempts = new (string Label, string Sql)[]
        {
            ("drop the metadata CHECK", $"ALTER TABLE field_encrypted_values DROP CONSTRAINT {EnvelopeMetadataConstraints.FieldMetadataName}"),
            ("drop the tie CHECK", $"ALTER TABLE file_attachment_versions DROP CONSTRAINT {EnvelopeMetadataConstraints.AttachmentSchemaEnvelopeTieName}"),
            ("add a weaker CHECK", "ALTER TABLE field_encrypted_values ADD CONSTRAINT ck_gate_probe CHECK (true)"),
            ("drop a v2 column", "ALTER TABLE file_attachment_versions DROP COLUMN key_wrap_algorithm_code"),
            ("update v2 metadata (insert-only table)", "UPDATE field_encrypted_values SET key_wrap_algorithm_code = 3"),
            ("delete the migration record", $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Target}'"),
            ("forge a migration record", "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_Forged', '10.0.0')"),
        };
        var failures = new List<string>();
        foreach (var (label, sql) in attempts)
        {
            try
            {
                await ExecAsync(runtime, sql);
                failures.Add(label + ": allowed");
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
            }
            catch (PostgresException ex)
            {
                failures.Add($"{label}: {ex.SqlState} instead of 42501");
            }
        }

        var readable = await ScalarAsync<long>(runtime, "SELECT count(*) FROM \"__EFMigrationsHistory\"") > 0;
        Record("UP-07", "the runtime role cannot alter, drop or weaken the CHECKs or columns, update envelope rows, or change the migration history (42501), and can read the history",
            failures.Count == 0 && readable && await CountConstraintsAsync(admin, NewTableChecks, validatedOnly: true) == 6, failures.Count == 0 ? null : string.Join("; ", failures));
    }

    // ---------------------------------------------------------------- post: legacy writes, guarded Down, re-apply

    public async Task PostAsync()
    {
        var legacy = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "legacy-writer.json"))).RootElement;
        var failures = new List<string>();
        foreach (var row in legacy.GetProperty("rows").EnumerateArray())
        {
            var id = Guid.Parse(row.GetProperty("primaryKey").GetString()!);
            var kind = row.GetProperty("entityKind").GetString()!;
            await using var context = Context(admin);
            ISignedContentRow read = kind == "field_encrypted_values"
                ? await context.Set<FieldEncryptedValue>().AsNoTracking().SingleAsync(v => v.FieldEncryptedValueId == id)
                : await context.Set<FileAttachmentVersion>().AsNoTracking().SingleAsync(v => v.FileAttachmentVersionId == id);
            if (read.RowSignatureSchemaVersion != 1 || Rows.EnvelopeVersion(read) != 1 || Rows.HasAnyMetadata(read))
            {
                failures.Add(kind + ": legacy row is not schema 1 / envelope 1 with NULL metadata");
            }

            if (Convert.ToHexStringLower(BusinessSignedSchemas.Encode(read, 1)) != row.GetProperty("legacyCanonicalHex").GetString())
            {
                failures.Add(kind + ": the new encoder's bytes differ from the legacy build's bytes for the same stored row");
            }
        }

        Record("POST-01", "a pre-E3-M2b-2 business build (legacy EF model without the columns) writes v1 rows to the migrated database; they satisfy every CHECK, are schema 1 / envelope 1 with NULL metadata, and encode to the same canonical bytes in both builds",
            failures.Count == 0 && legacy.GetProperty("modelHasEnvelopeV2Properties").GetBoolean() == false && legacy.GetProperty("rows").GetArrayLength() == 2,
            failures.Count == 0 ? legacy.GetProperty("legacyBuild").GetString() : string.Join("; ", failures));

        // Guarded Down with envelope-2 rows present: refused, nothing changes.
        var counts = await ScalarAsync<long>(admin, "SELECT (SELECT count(*) FROM field_encrypted_values) + (SELECT count(*) FROM file_attachment_versions)");
        Exception? refused = null;
        try
        {
            Migrate(admin, Gate.Previous);
        }
        catch (Exception ex)
        {
            refused = ex;
        }

        Record("POST-02", "the guarded Down refuses while envelope-2 rows exist (P0001, fixed message) and changes nothing",
            Postgres(refused) is { SqlState: "P0001" } pg && pg.MessageText.Contains("envelope version 2 rows exist", StringComparison.Ordinal) && await MigrationRecordedAsync(admin)
            && await CountColumnsAsync(admin) == 6 && await CountConstraintsAsync(admin, NewTableChecks) == 6
            && await ScalarAsync<long>(admin, "SELECT (SELECT count(*) FROM field_encrypted_values) + (SELECT count(*) FROM file_attachment_versions)") == counts,
            Postgres(refused)?.MessageText);

        // Without envelope-2 rows the Down runs: columns and CHECKs removed, the old CHECK restored, legacy v1 rows intact.
        await ExecAsync(admin, "DELETE FROM field_encrypted_values WHERE encryption_envelope_version = 2; DELETE FROM file_attachment_versions WHERE encryption_envelope_version = 2;");
        var v1Rows = await ScalarAsync<long>(admin, "SELECT (SELECT count(*) FROM field_encrypted_values) + (SELECT count(*) FROM file_attachment_versions)");
        Migrate(admin, Gate.Previous);
        Record("POST-03", "the Down runs once no envelope-2 row exists: columns and CHECKs removed, envelope_positive restored, history row removed, v1 rows intact",
            !await MigrationRecordedAsync(admin) && await CountColumnsAsync(admin) == 0 && await CountConstraintsAsync(admin, NewTableChecks) == 0
            && await CountConstraintsAsync(admin, [EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName]) == 1
            && await ScalarAsync<long>(admin, "SELECT (SELECT count(*) FROM field_encrypted_values) + (SELECT count(*) FROM file_attachment_versions)") == v1Rows);
        var guard = NewGuard(runtime);
        Record("POST-04", "after the Down the guard reports MigrationMissing again", await guard.RefreshAsync(CancellationToken.None) == BusinessSchemaState.MigrationMissing);

        Migrate(admin, Gate.Target);
        BusinessRuntimeGrants.Apply(Context(admin), Runtime);
        var reapplied = await NewGuard(runtime).RefreshAsync(CancellationToken.None);
        var stillEqual = true;
        foreach (var row in legacy.GetProperty("rows").EnumerateArray())
        {
            var id = Guid.Parse(row.GetProperty("primaryKey").GetString()!);
            await using var context = Context(admin);
            ISignedContentRow read = row.GetProperty("entityKind").GetString() == "field_encrypted_values"
                ? await context.Set<FieldEncryptedValue>().AsNoTracking().SingleAsync(v => v.FieldEncryptedValueId == id)
                : await context.Set<FileAttachmentVersion>().AsNoTracking().SingleAsync(v => v.FileAttachmentVersionId == id);
            stillEqual &= Convert.ToHexStringLower(BusinessSignedSchemas.Encode(read, 1)) == row.GetProperty("legacyCanonicalHex").GetString();
        }

        Record("POST-05", "re-applying the migration over existing v1 data succeeds; the guard is ready and the v1 rows still encode identically", reapplied == BusinessSchemaState.Ready && stillEqual && await CountConstraintsAsync(admin, NewTableChecks, validatedOnly: true) == 6);
    }

    // ---------------------------------------------------------------- restore: pg_dump/pg_restore of both states

    public async Task RestoreAsync()
    {
        var restoredUp = WithDatabase(admin, "restored_up");
        var restoredUpRuntime = WithDatabase(runtime, "restored_up");
        var encodings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(evidence, "round-trip-encodings.json")))!;
        var mismatches = 0;
        foreach (var (key, hex) in encodings)
        {
            var id = Guid.Parse(key);
            await using var context = Context(restoredUp);
            var field = await context.Set<FieldEncryptedValue>().AsNoTracking().SingleOrDefaultAsync(v => v.FieldEncryptedValueId == id);
            ISignedContentRow? read = field is not null ? field : await context.Set<FileAttachmentVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.FileAttachmentVersionId == id);
            if (read is null || Convert.ToHexStringLower(BusinessSignedSchemas.Encode(read, 1)) != hex)
            {
                mismatches++;
            }
        }

        var guard = NewGuard(restoredUpRuntime);
        Record("RST-01", "a restore of the migrated database (pg_dump/pg_restore) keeps the history row, the six validated CHECKs and the v1/v2 rows with identical canonical bytes; the guard is ready on it",
            await MigrationRecordedAsync(restoredUp) && await CountConstraintsAsync(restoredUp, NewTableChecks, validatedOnly: true) == 6 && mismatches == 0
            && await guard.RefreshAsync(CancellationToken.None) == BusinessSchemaState.Ready, $"{mismatches} mismatched rows of {encodings.Count}");
        await CheckMatrixAsync("RST-02", restoredUp);

        var restoredPre = WithDatabase(admin, "restored_pre");
        var restoredPreRuntime = WithDatabase(runtime, "restored_pre");
        var preGuard = NewGuard(restoredPreRuntime);
        var preState = await preGuard.RefreshAsync(CancellationToken.None);
        Exception? query = null;
        try
        {
            await using var context = Context(restoredPreRuntime);
            _ = await context.Set<FileAttachmentVersion>().AsNoTracking().Take(1).ToListAsync(); // materializing selects every mapped column
        }
        catch (Exception ex)
        {
            query = ex;
        }

        Record("RST-03", "a restore of the pre-migration database is refused by the guard (MigrationMissing) and unreadable by the new EF model (42703)",
            preState == BusinessSchemaState.MigrationMissing && Postgres(query)?.SqlState == "42703", $"{preState} / {Postgres(query)?.SqlState}");
        Migrate(restoredPre, Gate.Target);
        BusinessRuntimeGrants.Apply(Context(restoredPre), Runtime);
        Record("RST-04", "re-applying the migration to the restored pre-migration database makes it ready", await NewGuard(restoredPreRuntime).RefreshAsync(CancellationToken.None) == BusinessSchemaState.Ready);
    }
}

internal sealed class GateContextFactory(string connectionString) : IDbContextFactory<BusinessDbContext>
{
    public BusinessDbContext CreateDbContext() => new(new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql(connectionString).Options);
}

internal sealed class GateLifetime : IHostApplicationLifetime
{
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool StopRequested => stop.Task.IsCompleted;

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => stop.TrySetResult();

    public async Task<bool> WaitForStopAsync(TimeSpan timeout) => await Task.WhenAny(stop.Task, Task.Delay(timeout)) == stop.Task;
}

/// <summary>Valid envelope row fixtures (FKs are disabled by the replica session; every CHECK still applies).</summary>
internal static class Rows
{
    public static FieldEncryptedValue Field(int schema, int envelope, bool metadata) => new()
    {
        FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), FormRecordId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(),
        FieldValueItemId = Guid.CreateVersion7(), ContentChangeOperationId = Guid.CreateVersion7(), ContentRevision = 1, RowRevision = 1, RowSignature = Enumerable.Repeat((byte)0x11, 32).ToArray(),
        RowSignatureKeyVersion = 1, RowSignatureSchemaVersion = schema, CreatedOn = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), CreatedByUserId = Guid.CreateVersion7(),
        EncryptedValueBytes = [1, 2, 3, 4, 5], EncryptionNonceBytes = Enumerable.Repeat((byte)0x0b, 12).ToArray(), EncryptionAuthTagBytes = Enumerable.Repeat((byte)0xaa, 16).ToArray(),
        WrappedDataKeyBytes = Enumerable.Repeat((byte)0xcc, 60).ToArray(), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = "local-dev-kek-v1", EncryptionEnvelopeVersion = envelope,
        EncryptedLogicalType = "String",
        DataKeyIdentifier = metadata ? Guid.CreateVersion7() : null, DataKeyCommitmentBytes = metadata ? Enumerable.Repeat((byte)0xdd, 32).ToArray() : null, KeyWrapAlgorithmCode = metadata ? 1 : null,
    };

    public static FileAttachmentVersion Attachment(int schema, int envelope, bool metadata) => new()
    {
        FileAttachmentVersionId = Guid.CreateVersion7(), TenantId = Guid.CreateVersion7(), FormRecordId = Guid.CreateVersion7(), FileAttachmentId = Guid.CreateVersion7(),
        FieldDefinitionId = Guid.CreateVersion7(), VersionNumber = 1, OriginalFileName = "gate.pdf", DetectedContentType = "application/pdf", PlaintextSizeBytes = 10,
        PlaintextSha256 = Enumerable.Repeat((byte)0x33, 32).ToArray(), CiphertextSizeBytes = 38, CiphertextSha256 = Enumerable.Repeat((byte)0x11, 32).ToArray(), StorageProviderName = "azurite",
        StorageObjectKey = "documents/gate/" + Guid.CreateVersion7().ToString("N"), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = "local-dev-kek-v1", EncryptionEnvelopeVersion = envelope,
        WrappedDataKey = Enumerable.Repeat((byte)0x44, 60).ToArray(), EncryptionNoncePrefix = Enumerable.Repeat((byte)0x22, 8).ToArray(), EncryptionSegmentSizeBytes = 1048576,
        ScanVerdict = "Clean", ScannerName = "clamd", ScanSignatureName = null, ScannedOn = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), UploadedByUserId = Guid.CreateVersion7(),
        UploadedOn = new DateTimeOffset(2026, 11, 15, 11, 59, 0, TimeSpan.Zero), ContentChangeOperationId = Guid.CreateVersion7(), ContentRevision = 1, RowRevision = 1,
        RowSignature = Enumerable.Repeat((byte)0x11, 32).ToArray(), RowSignatureKeyVersion = 1, RowSignatureSchemaVersion = schema, VersionState = "Committed",
        DataKeyIdentifier = metadata ? Guid.CreateVersion7() : null, DataKeyCommitmentBytes = metadata ? Enumerable.Repeat((byte)0x55, 32).ToArray() : null, KeyWrapAlgorithmCode = metadata ? 2 : null,
    };

    public static void SetIdentifier(ISignedContentRow row, Guid? value)
    {
        if (row is FieldEncryptedValue f) { f.DataKeyIdentifier = value; } else { ((FileAttachmentVersion)row).DataKeyIdentifier = value; }
    }

    public static void SetCommitment(ISignedContentRow row, byte[]? value)
    {
        if (row is FieldEncryptedValue f) { f.DataKeyCommitmentBytes = value; } else { ((FileAttachmentVersion)row).DataKeyCommitmentBytes = value; }
    }

    public static void SetWrap(ISignedContentRow row, int? value)
    {
        if (row is FieldEncryptedValue f) { f.KeyWrapAlgorithmCode = value; } else { ((FileAttachmentVersion)row).KeyWrapAlgorithmCode = value; }
    }

    public static int EnvelopeVersion(ISignedContentRow row) => row is FieldEncryptedValue f ? f.EncryptionEnvelopeVersion : ((FileAttachmentVersion)row).EncryptionEnvelopeVersion;

    public static bool HasAnyMetadata(ISignedContentRow row) => row is FieldEncryptedValue f
        ? f.DataKeyIdentifier is not null || f.DataKeyCommitmentBytes is not null || f.KeyWrapAlgorithmCode is not null
        : row is FileAttachmentVersion a && (a.DataKeyIdentifier is not null || a.DataKeyCommitmentBytes is not null || a.KeyWrapAlgorithmCode is not null);

    public static string Describe(ISignedContentRow row) => $"{row.EntityKind} schema {row.RowSignatureSchemaVersion}";

    public static bool SameEnvelope(ISignedContentRow a, ISignedContentRow b) => (a, b) switch
    {
        (FieldEncryptedValue x, FieldEncryptedValue y) => x.EncryptedValueBytes.AsSpan().SequenceEqual(y.EncryptedValueBytes) && x.EncryptionNonceBytes.AsSpan().SequenceEqual(y.EncryptionNonceBytes)
            && x.EncryptionAuthTagBytes.AsSpan().SequenceEqual(y.EncryptionAuthTagBytes) && x.WrappedDataKeyBytes.AsSpan().SequenceEqual(y.WrappedDataKeyBytes)
            && x.EncryptionAlgorithmName == y.EncryptionAlgorithmName && x.EncryptionKeyReference == y.EncryptionKeyReference && x.EncryptionEnvelopeVersion == y.EncryptionEnvelopeVersion
            && x.EncryptedLogicalType == y.EncryptedLogicalType && x.DataKeyIdentifier == y.DataKeyIdentifier && Same(x.DataKeyCommitmentBytes, y.DataKeyCommitmentBytes) && x.KeyWrapAlgorithmCode == y.KeyWrapAlgorithmCode,
        (FileAttachmentVersion x, FileAttachmentVersion y) => x.WrappedDataKey.AsSpan().SequenceEqual(y.WrappedDataKey) && x.EncryptionNoncePrefix.AsSpan().SequenceEqual(y.EncryptionNoncePrefix)
            && x.EncryptionAlgorithmName == y.EncryptionAlgorithmName && x.EncryptionKeyReference == y.EncryptionKeyReference && x.EncryptionEnvelopeVersion == y.EncryptionEnvelopeVersion
            && x.EncryptionSegmentSizeBytes == y.EncryptionSegmentSizeBytes && x.CiphertextSha256.AsSpan().SequenceEqual(y.CiphertextSha256) && x.StorageObjectKey == y.StorageObjectKey
            && x.DataKeyIdentifier == y.DataKeyIdentifier && Same(x.DataKeyCommitmentBytes, y.DataKeyCommitmentBytes) && x.KeyWrapAlgorithmCode == y.KeyWrapAlgorithmCode,
        _ => false,
    };

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
