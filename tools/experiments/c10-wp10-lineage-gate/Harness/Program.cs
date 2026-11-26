using System.Diagnostics;
using System.Text.Json;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// C10 M1-WP10 gate harness (see Harness.csproj). Usage:
//   C10Wp10Harness --admin <admin connection to a disposable server> --authority <Authority Release folder> --evidence <folder>
// Seeds synthetic legacy shares (no metadata rows) with committed history into a fresh database, runs the operator command as a
// separate process of the built Authority (as a SELECT-only role and as the administrator), checks the classifications, the
// refusals, the redaction and the refusal exit codes, and proves every Authority table is unchanged. Exit 0 only if all pass.
var options = ParseArguments(args);
var admin = options["--admin"];
var authority = Path.GetFullPath(options["--authority"]);
var evidence = Directory.CreateDirectory(options["--evidence"]).FullName;
var failures = new List<string>();
void Check(bool condition, string description)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + description);
    if (!condition)
    {
        failures.Add(description);
    }
}

var database = "c10wp10_" + Guid.NewGuid().ToString("N")[..12];
var role = "c10wp10_reader_" + Guid.NewGuid().ToString("N")[..8];
var password = "r" + Guid.NewGuid().ToString("N");
await ExecuteAsync(admin, $"CREATE DATABASE {database}");
try
{
    var adminDb = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = false }.ConnectionString;
    var contextOptions = new DbContextOptionsBuilder<AuthorityDbContext>()
        .UseNpgsql(adminDb, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
    Seeded seeded;
    await using (var context = new AuthorityDbContext(contextOptions))
    {
        await context.Database.MigrateAsync();
        seeded = await SeedAsync(context);
    }

    await ExecuteAsync(adminDb,
        $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS",
        $"GRANT CONNECT ON DATABASE {database} TO {role}",
        $"GRANT USAGE ON SCHEMA {AuthorityDbContext.SchemaName} TO {role}",
        $"GRANT SELECT ON ALL TABLES IN SCHEMA {AuthorityDbContext.SchemaName} TO {role}");
    var readerDb = new NpgsqlConnectionStringBuilder(adminDb) { Username = role, Password = password }.ConnectionString;
    var before = await FingerprintAsync(adminDb);

    foreach (var (label, connection) in new[] { ("select-only role", readerDb), ("administrator", adminDb) })
    {
        var output = Path.Combine(evidence, $"report-{label.Replace(' ', '-')}.json");
        var (exit, stdout, _) = await RunCommandAsync(authority, connection, "Testing", seeded.Organization, output);
        Check(exit == 6, $"{label}: exit 6 (report written, evidence incomplete); was {exit}");
        Check(File.Exists(output), $"{label}: the report file was written");
        if (!File.Exists(output))
        {
            continue;
        }

        var text = await File.ReadAllTextAsync(output);
        using var report = JsonDocument.Parse(text);
        var entries = report.RootElement.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("record_share_id").GetGuid(), e => e.GetProperty("classification").GetString());
        Check(entries.GetValueOrDefault(seeded.OwnerShare) == "owner_granted", $"{label}: owner-granted share classified owner_granted");
        Check(entries.GetValueOrDefault(seeded.DelegatedShare) == "unambiguous_parent", $"{label}: delegated legacy share attached to its owner-granted parent");
        Check(entries.GetValueOrDefault(seeded.NonOwnerShare) == "no_covering_parent", $"{label}: verified non-owner without a Manager share is no_covering_parent");
        Check(entries.GetValueOrDefault(seeded.UnattributedManager) == "insufficient_evidence", $"{label}: share with an uncommitted-to-Effective registration is refused");
        Check(entries.GetValueOrDefault(seeded.UnresolvedShare) == "insufficient_evidence", $"{label}: share under an unresolved Manager share is refused, not invalidated");
        Check(report.RootElement.GetProperty("evidence_refusals").GetArrayLength() == 2, $"{label}: exactly two refusals are propagated");
        Check(entries.Count == 5, $"{label}: every Active legacy share is reported ({entries.Count})");
        Check(seeded.People.All(p => !text.Contains(p.ToString("D"), StringComparison.OrdinalIgnoreCase) && !stdout.Contains(p.ToString("D"), StringComparison.OrdinalIgnoreCase)),
            $"{label}: no user or binding identifier appears in the report or on standard output");
        Check(!stdout.Contains(seeded.OwnerShare.ToString("D"), StringComparison.OrdinalIgnoreCase), $"{label}: standard output carries counts only");
    }

    var refusedOutput = Path.Combine(evidence, "refused-production.json");
    var production = await RunCommandAsync(authority, readerDb, "Production", seeded.Organization, refusedOutput);
    Check(production.Exit == 2 && !File.Exists(refusedOutput), "Production environment is refused before any read (exit 2, no file)");

    var existing = Path.Combine(evidence, "existing.json");
    await File.WriteAllTextAsync(existing, "existing");
    var exists = await RunCommandAsync(authority, readerDb, "Testing", seeded.Organization, existing);
    Check(exists.Exit == 7 && await File.ReadAllTextAsync(existing) == "existing", "an existing output file is refused and left unchanged (exit 7)");

    var unknownOutput = Path.Combine(evidence, "unknown.json");
    var unknown = await RunCommandAsync(authority, readerDb, "Testing", Guid.CreateVersion7(), unknownOutput);
    Check(unknown.Exit == 5 && !File.Exists(unknownOutput), "an unknown organization writes no report (exit 5)");

    Check(before == await FingerprintAsync(adminDb), "every Authority table is unchanged after all runs");
    try
    {
        await ExecuteAsync(readerDb, $"UPDATE {AuthorityDbContext.SchemaName}.authority_resources SET resource_lifecycle_state = 'Revoked' WHERE resource_entity_id = '{seeded.NonOwnerShare}'");
        Check(false, "the select-only role cannot write");
    }
    catch (PostgresException ex)
    {
        Check(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege, "the select-only role cannot write (insufficient privilege)");
    }
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)", $"DROP ROLE IF EXISTS {role}");
}

Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED");
return failures.Count == 0 ? 0 : 1;

static async Task<(int Exit, string Stdout, string Stderr)> RunCommandAsync(string authority, string connection, string environment, Guid organization, string output)
{
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = authority,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in new[] { Path.Combine(authority, "AuthorizationAuthority.Api.dll"), "--legacy-lineage-dry-run", "--organization", organization.ToString("D"), "--output", output })
    {
        start.ArgumentList.Add(argument);
    }

    // Only the dedicated read-only connection is supplied; no runtime or migration connection exists in the child environment.
    start.Environment["ConnectionStrings__AuthorityDbReadOnly"] = connection;
    start.Environment["DOTNET_ENVIRONMENT"] = environment;
    start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
    start.Environment.Remove("ConnectionStrings__AuthorityDb");
    start.Environment.Remove("ConnectionStrings__AuthorityDbMigration");
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Console.WriteLine($"      [{environment}] exit {process.ExitCode}: {(await stdout).Trim()} {(await stderr).Trim()}");
    return (process.ExitCode, await stdout, await stderr);
}

static async Task<Seeded> SeedAsync(AuthorityDbContext context)
{
    var org = Guid.CreateVersion7();
    var record = Guid.CreateVersion7();
    var field = Guid.CreateVersion7();
    var now = DateTimeOffset.UtcNow.AddDays(-10);
    long revision = 0;
    context.OrganizationStates.Add(new OrganizationAuthorizationState { OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org, OrganizationDisplayName = "Synthetic WP10" });
    await context.SaveChangesAsync();

    var bindings = new Dictionary<Guid, Guid>();
    Guid User()
    {
        var user = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();
        bindings[user] = binding;
        context.IdentityBindings.Add(new IdentityBinding
        {
            IdentityBindingId = binding,
            OrganizationId = org,
            AuthorityUserId = user,
            IdentityIssuerUri = "https://synthetic.invalid/",
            IdentitySubjectIdentifier = user.ToString("N"),
            IdentityCreatedOn = now,
        });
        return user;
    }

    Guid Command(string kind, Guid actor, string state = SecurityCommandStates.Effective)
    {
        var id = Guid.CreateVersion7();
        context.SecurityCommands.Add(new SecurityCommand
        {
            SecurityCommandId = id,
            OrganizationId = org,
            ActorIdentityBindingId = bindings[actor],
            CommandKindName = kind,
            CommandIdempotencyKey = "wp10-" + id.ToString("N"),
            CommandRequestDigest = new byte[32],
            ExpectedAuthorizationRevision = revision,
            CommittedAuthorizationRevision = ++revision,
            CommandStateName = state,
            CorrelationIdentifier = Guid.CreateVersion7(),
            CommandCreatedOn = now,
            CommandModifiedOn = now,
            CommandEffectiveOn = now,
        });
        return id;
    }

    void Edge(string type, Guid id, string relation, string subjectType, Guid subject, Guid createdBy) =>
        context.RelationshipEdges.Add(new AuthorityRelationshipEdge
        {
            AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
            OrganizationId = org,
            ResourceTypeName = type,
            ResourceEntityId = id,
            RelationshipName = relation,
            SubjectTypeName = subjectType,
            SubjectEntityId = subject,
            SubjectRelationName = string.Empty,
            CreatedBySecurityCommandId = createdBy,
            RelationshipCreatedOn = now,
        });

    Guid Share(Guid grantor, Guid recipient, string role, string state = SecurityCommandStates.Effective)
    {
        var id = Guid.CreateVersion7();
        var command = Command(SecurityCommandKinds.CreateShare, grantor, state);
        context.Resources.Add(new AuthorityResource
        {
            AuthorityResourceId = Guid.CreateVersion7(),
            OrganizationId = org,
            ResourceTypeName = ResourceTypeNames.RecordShare,
            ResourceEntityId = id,
            ParentResourceEntityId = record,
            RegisteredBySecurityCommandId = command,
            ResourceRegisteredOn = now,
        });
        Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, recipient, command);
        Edge(ResourceTypeNames.FieldItem, field, "shared_via", ResourceTypeNames.RecordShare, id, command);
        return id;
    }

    var (owner, lead, worker, outsider, other) = (User(), User(), User(), User(), User());
    await context.SaveChangesAsync();
    var registerRecord = Command(SecurityCommandKinds.RegisterRecord, owner);
    context.Resources.Add(new AuthorityResource
    {
        AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record,
        RegisteredBySecurityCommandId = registerRecord, ResourceRegisteredOn = now,
    });
    Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, owner, registerRecord);
    var ownerShare = Share(owner, lead, "manager");
    var delegated = Share(lead, worker, "reader");
    var nonOwner = Share(outsider, other, "reader");

    // A Manager share whose registering command is committed but not yet Effective: its grantor cannot be attributed, so a share
    // the worker granted under it can be neither attached nor invalidated.
    var unattributed = Share(owner, worker, "manager", SecurityCommandStates.CommittedAwaitingProjection);
    var unresolved = Share(worker, other, "contributor");
    await context.SaveChangesAsync();
    await context.OrganizationStates.Where(s => s.OrganizationId == org)
        .ExecuteUpdateAsync(s => s.SetProperty(e => e.AuthorizationRevision, revision).SetProperty(e => e.ProjectionAppliedRevision, revision));
    return new Seeded(org, ownerShare, delegated, nonOwner, unattributed, unresolved, [owner, lead, worker, outsider, other, .. bindings.Values]);
}

static async Task<string> FingerprintAsync(string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    var tables = new List<string>();
    await using (var list = new NpgsqlCommand("SELECT format('%I.%I', schemaname, tablename) FROM pg_catalog.pg_tables WHERE schemaname = 'authority' ORDER BY 1", connection))
    await using (var reader = await list.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
    }

    var parts = new List<string>();
    foreach (var table in tables)
    {
        await using var digest = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
        parts.Add(table + "=" + (string)(await digest.ExecuteScalarAsync())!);
    }

    return string.Join(";", parts);
}

static async Task ExecuteAsync(string connectionString, params string[] statements)
{
    await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
    await connection.OpenAsync();
    foreach (var statement in statements)
    {
        await using var command = new NpgsqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync();
    }
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        values[args[i]] = args[i + 1];
    }

    foreach (var required in new[] { "--admin", "--authority", "--evidence" })
    {
        if (!values.ContainsKey(required))
        {
            throw new ArgumentException($"usage: C10Wp10Harness --admin <connection> --authority <folder> --evidence <folder> (missing {required})");
        }
    }

    return values;
}

internal sealed record Seeded(Guid Organization, Guid OwnerShare, Guid DelegatedShare, Guid NonOwnerShare, Guid UnattributedManager, Guid UnresolvedShare, Guid[] People);
