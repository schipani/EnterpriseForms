using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2b-1F CARV floor 1 (contract §21): the Release A / Release B separation (the ordinary Authority migration set can never
/// contain the floor), the exact floor SQL, the detection of a missing or lowered floor, and the operator command's refusals.
/// Database behaviour is proven against real PostgreSQL and real legacy binaries in docs/evidence/e3-m2b1-carv.
/// </summary>
public sealed class ContentAcceptanceFloorTests
{
    private const string Placeholder = "Host=unit-test.invalid;Database=unused;Username=unused;Password=unused";

    // ---- Release A / Release B separation -------------------------------------------------------------------------------

    [Fact]
    public void TheOrdinaryAuthorityMigrationSetNeverContainsTheFloor()
    {
        using var authority = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>()
            .UseNpgsql(Placeholder, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options);
        var ordinary = authority.Database.GetMigrations().ToList();

        Assert.Contains(ContentAcceptanceRuleSql.MigrationId, ordinary);
        Assert.DoesNotContain(ContentAcceptanceFloorSql.MigrationId, ordinary);
        Assert.DoesNotContain(ordinary, id => id.Contains("Floor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheFloorContextOwnsExactlyTheFloorMigrationAndSharesTheAuthorityHistoryTable()
    {
        using var floor = new ContentAcceptanceFloorContext(ContentAcceptanceFloorContext.OptionsFor(Placeholder));
        Assert.Equal([ContentAcceptanceFloorSql.MigrationId], floor.Database.GetMigrations().ToList());
        Assert.Empty(floor.Model.GetEntityTypes()); // SQL-only: it can never map or change an Authority table
        var history = floor.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>();
        var script = history.GetCreateIfNotExistsScript();
        Assert.Contains("ef_migrations_history", script, StringComparison.Ordinal);
        Assert.Contains("authority", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__EFMigrationsHistory", script, StringComparison.Ordinal);
    }

    // ---- the floor SQL (§19.5, OD-M2-21/28) --------------------------------------------------------------------------------

    [Fact]
    public void TheFloorSqlLocksChecksPreconditionsThenAddsAPermanentlyUnvalidatedTwoValuedFloor()
    {
        var up = ContentAcceptanceFloorSql.UpSql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith("SET LOCAL lock_timeout = '5s';\nLOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;", up, StringComparison.Ordinal);

        var alter = up.IndexOf("ADD CONSTRAINT ck_content_operation_rule_floor_v1", StringComparison.Ordinal);
        Assert.True(alter > 0);
        foreach (var precondition in new[] { "is not fully installed", "is already installed", "prepared by a pre-CARV build are in flight", "without a CARV build marker" })
        {
            var at = up.IndexOf(precondition, StringComparison.Ordinal);
            Assert.InRange(at, 0, alter); // every precondition runs under the lock, before the constraint
        }

        Assert.Contains("CHECK (" + ContentAcceptanceFloorSql.FloorCheckSql + ") NOT VALID;", up, StringComparison.Ordinal);
        Assert.Contains("NOT IN ('Prepared', 'Committed')", ContentAcceptanceFloorSql.FloorCheckSql, StringComparison.Ordinal); // D-2
        Assert.Contains("prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1", ContentAcceptanceFloorSql.FloorCheckSql, StringComparison.Ordinal); // D-1
        Assert.DoesNotContain("VALIDATE", up, StringComparison.Ordinal); // D-4
        Assert.Equal(ContentAcceptanceFloorSql.UpSql, ContentAcceptanceFloorSql.ReinstateSql);
        Assert.Contains("floors are never lowered", ContentAcceptanceFloorSql.DownSql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", ContentAcceptanceFloorSql.DownSql, StringComparison.OrdinalIgnoreCase);
        Assert.True(ContentAcceptanceRules.DatabaseObjects.IsFenceObject(ContentAcceptanceFloorSql.ConstraintName)); // refusals map to 409
        Assert.Equal(ContentAcceptanceRules.CurrentVersion, ContentAcceptanceFloorSql.FloorVersion);
    }

    // ---- detection of a missing or lowered floor (§21.6) --------------------------------------------------------------------

    private static readonly ContentAcceptanceRules.SchemaFacts Fence = new(MigrationApplied: true, ColumnsPresent: true, CommitCheckPresent: true, StateGuardEnabled: true, InstalledFloor: null);

    public static TheoryData<string, bool, string> FloorDecisions => new()
    {
        { "release A: no floor, none required", true, "content acceptance rules v1 (floor none)" },
        { "release B: floor installed and recorded, required", true, "content acceptance rules v1 (floor 1)" },
        { "floor recorded but its constraint dropped", false, "ContentAcceptanceRuleFloorMissing(recorded in the migration history, constraint absent)" },
        { "restore removed floor and history, floor required", false, "ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)" },
        { "required floor above this build", false, "ContentAcceptanceRuleRequiredFloorAboveBuild(required=2, build=1)" },
    };

    [Theory]
    [MemberData(nameof(FloorDecisions))]
    public void AMissingOrLoweredFloorIsNotReady(string scenario, bool ready, string detail)
    {
        var (facts, required) = scenario switch
        {
            "release A: no floor, none required" => (Fence, (int?)null),
            "release B: floor installed and recorded, required" => (Fence with { InstalledFloor = 1, FloorRecorded = true }, 1),
            "floor recorded but its constraint dropped" => (Fence with { FloorRecorded = true }, null),
            "restore removed floor and history, floor required" => (Fence, 1),
            "required floor above this build" => (Fence with { InstalledFloor = 1, FloorRecorded = true }, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Equal((ready, detail), ContentAcceptanceRules.Evaluate(facts, ContentAcceptanceRules.CurrentVersion, required));
    }

    private sealed class FixedReader(ContentAcceptanceRules.SchemaFacts facts) : IContentAcceptanceRuleSchemaReader
    {
        public Task<ContentAcceptanceRules.SchemaFacts> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(facts);
    }

    [Fact]
    public async Task TheGuardAppliesTheDeploymentsRequiredFloor()
    {
        var restored = Fence; // a restore to before the floor: neither constraint nor history row
        var without = new ContentAcceptanceRuleGuard(new FixedReader(restored), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance);
        var with = new ContentAcceptanceRuleGuard(new FixedReader(restored), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance, new ContentAcceptanceRuleOptions { RequiredFloor = 1 });

        Assert.True((await without.RefreshAsync(CancellationToken.None)).Ready); // undetectable from the database alone: configure the floor
        Assert.Equal((false, "ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)"), await with.RefreshAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnInvalidRequiredFloorFailsAtStartup(int value) =>
        Assert.Throws<InvalidOperationException>(() => ContentAcceptanceRuleOptions.Validate(new ContentAcceptanceRuleOptions { RequiredFloor = value }));

    [Fact]
    public void AnAbsentOrValidRequiredFloorIsAccepted()
    {
        Assert.Null(ContentAcceptanceRuleOptions.Validate(null).RequiredFloor);
        Assert.Equal(1, ContentAcceptanceRuleOptions.Validate(new ContentAcceptanceRuleOptions { RequiredFloor = 1 }).RequiredFloor);
    }

    // ---- the operator command ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheFloorCommandIsInactiveWithoutItsFlag() =>
        Assert.False(ContentAcceptanceFloorCommand.TryRun(["--migrate"], out _));

    /// <summary>A migration connection string that can never connect: any exit code other than 2 means the command got past its refusals.</summary>
    private static Microsoft.Extensions.Configuration.IConfiguration Unreachable() => new Microsoft.Extensions.Configuration.ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AuthorityDbMigration"] = "Host=unit-test.invalid;Database=unused;Username=unused;Password=unused;Timeout=2" })
        .Build();

    [Theory]
    [InlineData("--apply-content-acceptance-floor")]
    [InlineData("--apply-content-acceptance-floor 2")]
    [InlineData("--apply-content-acceptance-floor one")]
    [InlineData("--apply-content-acceptance-floor 1 --migrate")]
    [InlineData("--migrate --apply-content-acceptance-floor 1")]
    public void TheFloorCommandRefusesAnUnstatedFloorOrACombinationWithMigrate(string commandLine)
    {
        // The connection string is supplied, so exit code 2 can only come from the refusal itself (not from a missing setting).
        Assert.True(ContentAcceptanceFloorCommand.TryRun(commandLine.Split(' '), Unreachable(), out var exitCode));
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void AValidInvocationGetsPastTheRefusalsAndFailsClosedWhenTheDatabaseIsUnreachable()
    {
        Assert.True(ContentAcceptanceFloorCommand.TryRun(["--apply-content-acceptance-floor", "1"], Unreachable(), out var exitCode));
        Assert.Equal(3, exitCode); // not applied: the control shows the refusal cases above would otherwise reach this point
    }

    [Fact]
    public void TheFloorCommandRequiresTheMigrationIdentity()
    {
        var empty = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.True(ContentAcceptanceFloorCommand.TryRun(["--apply-content-acceptance-floor", "1"], empty, out var exitCode));
        Assert.Equal(2, exitCode);
    }
}
