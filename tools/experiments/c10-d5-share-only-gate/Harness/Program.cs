// C10 D5 real-PostgreSQL gate for the expiry-aware share-only candidate query (SearchQueryReader.ReadShareReadableItemsAsync).
// Development/Testing only; run through scripts/dev/c10-d5-share-only-pg-gate.ps1 against a disposable postgres:18.0 container.
// Seeds one caller with a live direct share, an expired direct share and a team share on one record, each sharing one field item with
// a current posting, then asserts that the real query returns every candidate without exclusions and never an excluded share's item.
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var admin = args.Length == 2 && args[0] == "--admin" ? args[1] : throw new ArgumentException("usage: --admin <connection string>");
var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(admin, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
await using (var context = new AuthorityDbContext(options))
{
    await context.Database.MigrateAsync();
}

Guid org = Guid.CreateVersion7(), user = Guid.CreateVersion7(), team = Guid.CreateVersion7(), record = Guid.CreateVersion7(), field = Guid.CreateVersion7();
Guid approval = Guid.CreateVersion7(), operation = Guid.CreateVersion7();
Guid live = Guid.CreateVersion7(), expired = Guid.CreateVersion7(), viaTeam = Guid.CreateVersion7();
Guid liveItem = Guid.CreateVersion7(), expiredItem = Guid.CreateVersion7(), teamItem = Guid.CreateVersion7();

await using var connection = new NpgsqlConnection(admin);
await connection.OpenAsync();
await ExecuteAsync("SET session_replication_role = replica"); // seed rows only; foreign keys and triggers are not under test here (CHECK constraints still apply)

async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
{
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters)
    {
        command.Parameters.AddWithValue(name, value);
    }

    await command.ExecuteNonQueryAsync();
}

// Inserts one row, filling every NOT NULL column without a default from its type unless overridden.
async Task InsertAsync(string schema, string table, Dictionary<string, object> values)
{
    var columns = new List<(string Name, string Type)>();
    await using (var command = new NpgsqlCommand("SELECT column_name, CASE WHEN data_type = 'ARRAY' THEN udt_name ELSE data_type END FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND is_nullable = 'NO' AND column_default IS NULL", connection))
    {
        command.Parameters.AddWithValue("s", schema);
        command.Parameters.AddWithValue("t", table);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }
    }

    var row = new Dictionary<string, object>(values);
    foreach (var (name, type) in columns.Where(c => !row.ContainsKey(c.Name)))
    {
        row[name] = type switch
        {
            "uuid" => Guid.CreateVersion7(),
            "text" or "character varying" => "x",
            "bigint" => 1L,
            "integer" => 1,
            "smallint" => (short)1,
            "boolean" => false,
            "bytea" => new byte[] { 0 },
            "timestamp with time zone" => DateTimeOffset.UtcNow,
            "date" => DateOnly.FromDateTime(DateTime.UtcNow),
            "numeric" => 1m,
            "jsonb" => "{}",
            "_uuid" => Array.Empty<Guid>(),
            "_int4" => Array.Empty<int>(),
            "_int8" => Array.Empty<long>(),
            "_text" or "_varchar" => Array.Empty<string>(),
            _ => throw new InvalidOperationException($"{table}.{name}: no filler for {type}"),
        };
    }

    var names = row.Keys.ToList();
    await using var insert = new NpgsqlCommand($"INSERT INTO {schema}.{table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((n, i) => (columns.FirstOrDefault(c => c.Name == n).Type == "jsonb" ? $"@p{i}::jsonb" : $"@p{i}")))})", connection);
    for (var i = 0; i < names.Count; i++)
    {
        insert.Parameters.AddWithValue($"p{i}", row[names[i]]);
    }

    await insert.ExecuteNonQueryAsync();
}

Task Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") => InsertAsync("authority", "authority_relationship_edges", new()
{
    ["authority_relationship_edge_id"] = Guid.CreateVersion7(), ["organization_id"] = org, ["resource_type_name"] = type, ["resource_entity_id"] = id, ["relationship_name"] = relation,
    ["subject_type_name"] = subjectType, ["subject_entity_id"] = subject, ["subject_relation_name"] = subjectRelation, ["created_by_security_command_id"] = Guid.CreateVersion7(),
    ["relationship_created_on"] = DateTimeOffset.UtcNow,
});

await Edge("team", team, "member", "user", user);
foreach (var (share, item, holderType, holder, holderRelation) in new[] { (live, liveItem, "user", user, ""), (expired, expiredItem, "user", user, ""), (viaTeam, teamItem, "team", team, "participant") })
{
    await Edge("record_share", share, "reader", holderType, holder, holderRelation);
    await Edge("form_record", record, "shared_via", "record_share", share);
    await Edge("field_item", item, "shared_via", "record_share", share);
    await InsertAsync("authority_search", "search_string_postings", new()
    {
        ["organization_id"] = org, ["form_record_id"] = record, ["field_definition_id"] = field, ["manifest_item_id"] = item, ["field_search_approval_id"] = approval, ["indexed_operation_id"] = operation,
        ["manifest_item_key"] = new byte[17], ["source_canonical_bytes"] = new byte[] { 1 },
    });
}

// A minimal valid entry (derivation version 1): the query joins postings to the record's current entry only.
await InsertAsync("authority_search", "search_record_entries", new()
{
    ["organization_id"] = org, ["form_record_id"] = record, ["indexed_operation_id"] = operation, ["derived_field_search_approval_ids"] = new[] { approval },
    ["entry_derivation_version"] = 1, ["indexed_manifest_root"] = new byte[32], ["indexed_content_revision"] = 1L, ["entry_seal_signature"] = new byte[64],
    ["seal_key_version"] = 1, ["seal_schema_version"] = 1, ["indexed_posting_count"] = 3, ["indexed_anomaly_count"] = 0,
});
await ExecuteAsync("SET session_replication_role = origin");

var searchReader = new SearchQueryReader(admin, new IndexedSearchOptions());
async Task<HashSet<Guid>> CandidatesAsync(IReadOnlyCollection<Guid> excluded)
{
    await using var session = await searchReader.BeginAsync(org, CancellationToken.None);
    var items = await session.ReadShareReadableItemsAsync(user, [record], [new PredicateFieldApproval(field, "String", approval)], 100, excluded, CancellationToken.None);
    return items.Select(i => i.ManifestItemId).ToHashSet();
}

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    failures += ok ? 0 : 1;
}

var all = await CandidatesAsync([]);
Check(all.SetEquals([liveItem, expiredItem, teamItem]), "without exclusions every shared item is a candidate (direct and team)");
var withoutExpired = await CandidatesAsync([expired]);
Check(withoutExpired.SetEquals([liveItem, teamItem]), "an excluded (expired) direct share contributes no candidate; the live and team shares still do");
var withoutTeam = await CandidatesAsync([viaTeam, expired]);
Check(withoutTeam.SetEquals([liveItem]), "an excluded team share contributes no candidate");
var unrelated = await CandidatesAsync([Guid.CreateVersion7()]);
Check(unrelated.SetEquals(all), "excluding an unrelated share id changes nothing");
Console.WriteLine(failures == 0 ? "D5 SHARE-ONLY GATE PASS" : $"D5 SHARE-ONLY GATE FAIL ({failures})");
return failures == 0 ? 0 : 1;
