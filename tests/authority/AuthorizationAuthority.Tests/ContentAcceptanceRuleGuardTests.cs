using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2b-1 CARV: the startup/readiness guard (M2-CARV-09, OD-M2-20/30), the store-adapter mapping of database fence refusals
/// (M2-CARV-07), the versioned connection application name, and the agreement between the EF model, the migration SQL and the
/// contract (§19.5). The database behaviour itself is proven against real PostgreSQL in docs/evidence/e3-m2b1-carv.
/// </summary>
public sealed class ContentAcceptanceRuleGuardTests
{
    private static readonly ContentAcceptanceRules.SchemaFacts Complete = new(MigrationApplied: true, ColumnsPresent: true, CommitCheckPresent: true, StateGuardEnabled: true, InstalledFloor: null, DataKeyLedgerApplied: true);

    // ---- M2-CARV-09: the decision ------------------------------------------------------------------------------------------

    public static TheoryData<string, bool, string> Decisions => new()
    {
        { "unknown", false, "ContentAcceptanceRulesUnverified" },
        { "complete, no floor", true, "content acceptance rules v3 (floor none)" },
        { "floor 1 below the build", true, "content acceptance rules v3 (floor 1)" }, // E3-M2d: a build accepts a lower floor (a minimum)
        { "floor 2 below the build", true, "content acceptance rules v3 (floor 2)" },
        { "floor equal to the build", true, "content acceptance rules v3 (floor 3)" }, // E3-M2e: CARV 3
        { "floor above the build", false, "ContentAcceptanceRuleFloorAboveBuild(floor=4, build=3)" },
        { "data-key ledger migration missing", false, "ContentDataKeyLedgerMigrationMissing" }, // E3-M2e (AM-12)
        { "data-key ledger migration unknown", false, "ContentDataKeyLedgerMigrationMissing" }, // unknown is not ready
        { "migration missing", false, "ContentAcceptanceRuleMigrationMissing" },
        { "columns missing", false, "ContentAcceptanceRuleSchemaIncomplete" },
        { "commit check missing", false, "ContentAcceptanceRuleSchemaIncomplete" },
        { "state guard disabled or missing", false, "ContentAcceptanceRuleStateGuardMissing" },
    };

    private static ContentAcceptanceRules.SchemaFacts? FactsFor(string scenario) => scenario switch
    {
        "unknown" => null,
        "complete, no floor" => Complete,
        "floor 1 below the build" => Complete with { InstalledFloor = 1 },
        "floor 2 below the build" => Complete with { InstalledFloor = 2 },
        "floor equal to the build" => Complete with { InstalledFloor = 3 },
        "floor above the build" => Complete with { InstalledFloor = 4 },
        "data-key ledger migration missing" => Complete with { DataKeyLedgerApplied = false },
        "data-key ledger migration unknown" => Complete with { DataKeyLedgerApplied = null },
        "migration missing" => Complete with { MigrationApplied = false },
        "columns missing" => Complete with { ColumnsPresent = false },
        "commit check missing" => Complete with { CommitCheckPresent = false },
        "state guard disabled or missing" => Complete with { StateGuardEnabled = false },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void TheGuardIsReadyOnlyForACompleteFenceAtOrBelowThisBuild(string scenario, bool ready, string detail) =>
        Assert.Equal((ready, detail), ContentAcceptanceRules.Evaluate(FactsFor(scenario)));

    [Fact]
    public void TheApprovedRecheckIntervalIsThirtySeconds() => Assert.Equal(TimeSpan.FromSeconds(30), ContentAcceptanceRules.ReadinessRecheckInterval); // OD-M2-30

    private sealed class FakeReader : IContentAcceptanceRuleSchemaReader
    {
        public Func<ContentAcceptanceRules.SchemaFacts> Next { get; set; } = () => Complete;

        public int Reads { get; private set; }

        public Task<ContentAcceptanceRules.SchemaFacts> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Next());
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ARunningInstanceBecomesNotReadyWhenANewerFloorAppearsOrTheFenceCannotBeRead()
    {
        var reader = new FakeReader();
        var clock = new ManualClock();
        var guard = new ContentAcceptanceRuleGuard(reader, clock, NullLogger<ContentAcceptanceRuleGuard>.Instance);
        Assert.Equal((false, "ContentAcceptanceRulesUnverified"), guard.Current); // before the first (startup) evaluation

        Assert.True((await guard.RefreshAsync(CancellationToken.None)).Ready);
        Assert.True(guard.Current.Ready);

        reader.Next = () => Complete with { InstalledFloor = 4 }; // a newer floor installed while this instance runs
        clock.Now += ContentAcceptanceRules.ReadinessRecheckInterval;
        await guard.RefreshAsync(CancellationToken.None);
        Assert.Equal((false, "ContentAcceptanceRuleFloorAboveBuild(floor=4, build=3)"), guard.Current);

        reader.Next = () => throw new NpgsqlException("unreachable");
        clock.Now += ContentAcceptanceRules.ReadinessRecheckInterval;
        await guard.RefreshAsync(CancellationToken.None);
        Assert.Equal((false, "ContentAcceptanceRulesUnverified"), guard.Current);

        reader.Next = () => Complete with { InstalledFloor = 1 };
        await guard.RefreshAsync(CancellationToken.None);
        Assert.True(guard.Current.Ready);

        clock.Now += ContentAcceptanceRuleGuard.MaximumDecisionAge + TimeSpan.FromSeconds(1); // the monitor stopped refreshing
        Assert.Equal((false, "ContentAcceptanceRulesStale"), guard.Current);
    }

    [Fact]
    public async Task TheMonitorEvaluatesTheGuardAtStartup()
    {
        var reader = new FakeReader();
        var guard = new ContentAcceptanceRuleGuard(reader, TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance);
        using var monitor = new ContentAcceptanceRuleMonitor(guard, TimeProvider.System);
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 100 && !guard.Current.Ready; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.True(guard.Current.Ready);
            Assert.Equal(1, reader.Reads); // the next read is a full interval away
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    // ---- M2-CARV-07: database refusals --------------------------------------------------------------------------------------

    private sealed class CapturingAlerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }

    private static DbUpdateException Refusal(string sqlState, string? constraint) =>
        new("save failed", new PostgresException("refused", "ERROR", "ERROR", sqlState, constraintName: constraint));

    public static TheoryData<string> FenceObjects =>
    [
        "ck_content_operation_commit_rule_version", "trg_content_operation_rule_state_guard", "ck_content_operation_rule_floor_v1", "ck_content_operation_rule_floor_v12",
    ];

    [Theory]
    [MemberData(nameof(FenceObjects))]
    public void ADatabaseFenceRefusalBecomesOperationRulesOutdatedAndRaisesAnAlert(string fenceObject)
    {
        var alerts = new CapturingAlerts();
        var translated = ContentAcceptanceRuleRefusals.Translate(Refusal("23514", fenceObject), alerts);

        Assert.NotNull(translated);
        Assert.Equal((AuthorityErrorCodes.OperationRulesOutdated, 409, false), (translated.ErrorCode, translated.HttpStatusCode, translated.Retryable));
        var alert = Assert.Single(alerts.Raised);
        Assert.Equal((ContentAcceptanceRuleRefusals.AlertKind, "Critical"), (alert.AlertKind, alert.Severity));
        Assert.Contains(fenceObject, alert.Summary, StringComparison.Ordinal);
    }

    public static TheoryData<string, string?> OtherErrors => new()
    {
        { "23514", "ck_content_operation_state" }, // another CHECK of the same table
        { "23514", "ck_content_operation_rule_floor_vx" },
        { "23514", "ck_content_operation_rule_floor_v" },
        { "23514", null },
        { "23505", "ck_content_operation_commit_rule_version" }, // not a check violation
        { "55P03", null },
    };

    [Theory]
    [MemberData(nameof(OtherErrors))]
    public void AnyOtherDatabaseErrorIsLeftUnchangedAndRaisesNoAlert(string sqlState, string? constraint)
    {
        var alerts = new CapturingAlerts();
        Assert.Null(ContentAcceptanceRuleRefusals.Translate(Refusal(sqlState, constraint), alerts));
        Assert.Empty(alerts.Raised);
    }

    // ---- versioned application name (§19.9 step 3) ------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=db;Database=authority;Username=authority_runtime", "authority carv-v3")]
    [InlineData("Host=db;Database=authority;Username=authority_runtime;Application Name=authority-api", "authority-api carv-v3")]
    [InlineData("Host=db;Database=authority;Username=authority_runtime;Application Name=authority-api carv-v3", "authority-api carv-v3")]
    [InlineData("Host=db;Database=authority;Username=authority_runtime;Application Name=authority-api carv-v2", "authority-api carv-v3")]
    [InlineData("Host=db;Database=authority;Username=authority_runtime;Application Name=authority-api carv-v1", "authority-api carv-v3")] // E3-M2d: a stale marker is replaced, never kept beside the build's
    public void StoreConnectionsCarryTheBuildRuleVersion(string configured, string applicationName) =>
        Assert.Equal(applicationName, new NpgsqlConnectionStringBuilder(ContentAcceptanceRuleConnection.WithVersionedApplicationName(configured)).ApplicationName);

    [Fact]
    public void AMissingConnectionStringStaysMissing() => Assert.Null(ContentAcceptanceRuleConnection.WithVersionedApplicationName(null));

    // ---- model, migration and contract agree (§19.5) ------------------------------------------------------------------------

    [Fact]
    public void TheModelMigrationAndContractSqlAgree()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>()
            .UseNpgsql("Host=unit-test.invalid;Database=unused;Username=unused;Password=unused", n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName))
            .Options;
        using var context = new AuthorityDbContext(options);
        var entity = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().Single(e => e.ClrType == typeof(ContentChangeOperation));
        Assert.Equal(ContentAcceptanceRuleSql.CommitCheckSql, entity.GetCheckConstraints().Single(c => c.Name == ContentAcceptanceRules.DatabaseObjects.CommitCheck).Sql);
        Assert.Equal(ContentAcceptanceRuleSql.PositiveCheckSql, entity.GetCheckConstraints().Single(c => c.Name == ContentAcceptanceRules.DatabaseObjects.PositiveCheck).Sql);
        foreach (var column in new[] { ContentAcceptanceRules.DatabaseObjects.PrepareColumn, ContentAcceptanceRules.DatabaseObjects.CommitColumn })
        {
            var property = entity.GetProperties().Single(p => p.GetColumnName() == column);
            Assert.True(property.IsNullable);
            Assert.Equal(typeof(int?), property.ClrType);
        }

        // The Up SQL creates exactly the modelled CHECKs (verbatim), the two-valued terms, the state guard and its REVOKE.
        var up = ContentAcceptanceRuleSql.UpSql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith("SET LOCAL lock_timeout = '5s';", up, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT ck_content_operation_rule_versions_positive\n        CHECK (" + ContentAcceptanceRuleSql.PositiveCheckSql + "),", up, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT ck_content_operation_commit_rule_version\n        CHECK (" + ContentAcceptanceRuleSql.CommitCheckSql + ");", up, StringComparison.Ordinal);
        Assert.Contains("prepare_acceptance_rule_version IS NOT NULL AND commit_acceptance_rule_version IS NOT NULL", up, StringComparison.Ordinal);
        Assert.Contains("AND commit_acceptance_rule_version = prepare_acceptance_rule_version", up, StringComparison.Ordinal);
        Assert.DoesNotContain(">= COALESCE", up, StringComparison.Ordinal); // D-3: never the ">=" commit rule
        Assert.Contains("BEFORE INSERT OR UPDATE ON authority.content_change_operations", up, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON FUNCTION authority.content_operation_rule_state_guard() FROM PUBLIC;", up, StringComparison.Ordinal);
        Assert.Contains("SET search_path = pg_catalog, pg_temp", up, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY DEFINER", up, StringComparison.Ordinal);
        Assert.Contains("floors are never lowered", ContentAcceptanceRuleSql.DownSql, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMigrationInstallsAFloorAndTheDataKeyLedgerMigrationIsTheLatest()
    {
        var migrations = Directory.EnumerateFiles(RepositoryRoot("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations"), "*.cs").ToList();
        Assert.DoesNotContain(migrations, f => File.ReadAllText(f).Contains("ck_content_operation_rule_floor", StringComparison.Ordinal)); // OD-M2-21: M2b-1F is a separate release
        Assert.DoesNotContain(ContentAcceptanceRules.DatabaseObjects.FloorConstraintPrefix + "1", ContentAcceptanceRuleSql.UpSql, StringComparison.Ordinal);
        var ids = migrations.Select(Path.GetFileNameWithoutExtension).Where(n => n is not null && !n.EndsWith(".Designer", StringComparison.Ordinal) && n != "AuthorityDbContextModelSnapshot").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(ContentDataKeyLedgerSql.MigrationId, ids[^1]); // E3-M2e: the ledger migration follows the CARV migration and installs no floor (AM-1)
        Assert.Equal(ContentAcceptanceRuleSql.MigrationId, ids[^2]);
        Assert.DoesNotContain(ContentAcceptanceRules.DatabaseObjects.FloorConstraintPrefix, ContentDataKeyLedgerSql.TriggersSql, StringComparison.Ordinal);
        Assert.DoesNotContain(ContentAcceptanceRules.DatabaseObjects.FloorConstraintPrefix, ContentDataKeyLedgerSql.LockTimeoutSql + ContentDataKeyLedgerSql.DownGuardSql, StringComparison.Ordinal);
    }

    private static string RepositoryRoot(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("repository root not found"), .. parts]);
    }
}
