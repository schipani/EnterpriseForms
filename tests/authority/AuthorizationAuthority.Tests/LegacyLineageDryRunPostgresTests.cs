using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP10 on a disposable real PostgreSQL 18 server (<c>scripts/dev/c10-wp10-lineage-pg-gate.ps1</c>; <c>C10_WP10_PG_ADMIN</c> is the
/// administrator connection to it). Each test creates and drops its own database. Seeds synthetic legacy shares (no metadata rows)
/// with committed history, then runs the dry run both as a SELECT-only role and as the write-capable administrator, and proves the
/// database is byte-for-byte unchanged and the transaction was read-only. Never run against a shared or persistent database.
/// </summary>
public sealed class LegacyLineageDryRunPostgresTests
{
    private static readonly string? Admin = Environment.GetEnvironmentVariable("C10_WP10_PG_ADMIN");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(Guid Organization, Guid OwnerShare, Guid DelegatedShare, Guid UnresolvedShare, Guid NonOwnerShare);

    [Fact]
    public async Task TheDryRunReadsOnlyAndClassifiesSeededLegacyShares()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Admin), "C10_WP10_PG_ADMIN not set (run scripts/dev/c10-wp10-lineage-pg-gate.ps1)");
        var database = "c10wp10_" + Guid.NewGuid().ToString("N")[..12];
        var role = "c10wp10_reader_" + Guid.NewGuid().ToString("N")[..8];
        var password = "r" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(Admin!, $"CREATE DATABASE {database}");
        try
        {
            var adminDb = new NpgsqlConnectionStringBuilder(Admin) { Database = database, Pooling = false }.ConnectionString;
            var options = new DbContextOptionsBuilder<AuthorityDbContext>()
                .UseNpgsql(adminDb, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
            Seeded seeded;
            await using (var context = new AuthorityDbContext(options))
            {
                await context.Database.MigrateAsync(Ct);
                seeded = await SeedAsync(context);
            }

            await ExecuteAsync(adminDb,
                $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS",
                $"GRANT CONNECT ON DATABASE {database} TO {role}",
                $"GRANT USAGE ON SCHEMA {AuthorityDbContext.SchemaName} TO {role}",
                $"GRANT SELECT ON ALL TABLES IN SCHEMA {AuthorityDbContext.SchemaName} TO {role}");
            var readerDb = new NpgsqlConnectionStringBuilder(adminDb) { Username = role, Password = password }.ConnectionString;

            var before = await FingerprintAsync(adminDb);
            foreach (var connection in new[] { readerDb, adminDb })
            {
                var output = Path.Combine(Path.GetTempPath(), "c10-wp10-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var exit = await LegacyLineageDryRunCommand.RunAsync(connection, seeded.Organization, output, TimeProvider.System, Ct);
                    Assert.Equal(LegacyLineageDryRunCommand.EvidenceIncomplete, exit); // two shares are refused, none invalidated

                    using var report = JsonDocument.Parse(await File.ReadAllTextAsync(output, Ct));
                    var entries = report.RootElement.GetProperty("entries").EnumerateArray()
                        .ToDictionary(e => e.GetProperty("record_share_id").GetGuid(), e => e.GetProperty("classification").GetString());
                    Assert.Equal("owner_granted", entries[seeded.OwnerShare]);
                    Assert.Equal("unambiguous_parent", entries[seeded.DelegatedShare]);
                    Assert.Equal("insufficient_evidence", entries[seeded.UnresolvedShare]);
                    Assert.Equal("no_covering_parent", entries[seeded.NonOwnerShare]);
                    Assert.Equal(2, report.RootElement.GetProperty("evidence_refusals").GetArrayLength()); // the unattributed Manager share and its child
                }
                finally
                {
                    File.Delete(output);
                }
            }

            Assert.Equal(before, await FingerprintAsync(adminDb));

            // The SELECT-only role cannot write at all, so the run above had no write path available to it.
            var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(readerDb,
                $"UPDATE {AuthorityDbContext.SchemaName}.authority_resources SET resource_lifecycle_state = 'Revoked' WHERE resource_entity_id = '{seeded.NonOwnerShare}'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(Admin!, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)", $"DROP ROLE IF EXISTS {role}");
        }
    }

    private static async Task<Seeded> SeedAsync(AuthorityDbContext context)
    {
        var org = Guid.CreateVersion7();
        var record = Guid.CreateVersion7();
        var field = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow.AddDays(-10);
        long revision = 0;
        context.OrganizationStates.Add(new OrganizationAuthorizationState { OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org, OrganizationDisplayName = "Synthetic WP10" });
        await context.SaveChangesAsync(Ct);

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

        void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation, Guid createdBy) =>
            context.RelationshipEdges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
                OrganizationId = org,
                ResourceTypeName = type,
                ResourceEntityId = id,
                RelationshipName = relation,
                SubjectTypeName = subjectType,
                SubjectEntityId = subject,
                SubjectRelationName = subjectRelation,
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
            Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, recipient, "", command);
            Edge(ResourceTypeNames.FieldItem, field, "shared_via", ResourceTypeNames.RecordShare, id, "", command);
            return id;
        }

        var (owner, lead, worker, outsider, other) = (User(), User(), User(), User(), User());
        await context.SaveChangesAsync(Ct);
        var registerRecord = Command(SecurityCommandKinds.RegisterRecord, owner);
        context.Resources.Add(new AuthorityResource
        {
            AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record,
            RegisteredBySecurityCommandId = registerRecord, ResourceRegisteredOn = now,
        });
        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, owner, "", registerRecord);
        var ownerShare = Share(owner, lead, "manager");
        var delegated = Share(lead, worker, "reader");
        var nonOwner = Share(outsider, other, "reader");

        // A Manager share whose registering command is committed but not yet Effective: its grantor cannot be attributed, so a
        // share the worker granted under it can be neither attached nor invalidated.
        Share(owner, worker, "manager", SecurityCommandStates.CommittedAwaitingProjection);
        var unresolved = Share(worker, other, "contributor");
        await context.SaveChangesAsync(Ct);
        await context.OrganizationStates.Where(s => s.OrganizationId == org)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AuthorizationRevision, revision).SetProperty(e => e.ProjectionAppliedRevision, revision), Ct);
        await context.SaveChangesAsync(Ct);
        return new Seeded(org, ownerShare, delegated, unresolved, nonOwner);
    }

    /// <summary>A digest of every row of every table in the Authority schema.</summary>
    private static async Task<string> FingerprintAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT format('%I.%I', schemaname, tablename) FROM pg_catalog.pg_tables WHERE schemaname = 'authority' ORDER BY 1", connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var parts = new List<string>();
        foreach (var table in tables)
        {
            await using var digest = new NpgsqlCommand($"SELECT count(*)::text || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t", connection);
            parts.Add(table + "=" + (string)(await digest.ExecuteScalarAsync(Ct))!);
        }

        return string.Join(";", parts);
    }

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
