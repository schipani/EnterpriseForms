using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2d CARV 2 and floor 2 (owner decisions D-2, D-3, D-4; docs/security/E3_M2D_IMPLEMENTATION_RECORD.md): the recognized floors,
/// the Release B policy with a minimum required floor, the generalized write-fence evaluator (exactly one recognized floor with
/// exactly its definition, at least the required floor, no higher floor recorded, every floor up to it recorded), readiness, the
/// floor-2 SQL and the operator command. Floor 2 itself is exercised only on disposable PostgreSQL (the M2d gate).
/// </summary>
public sealed class ContentAcceptanceFloor2Tests
{
    private static readonly int[] OneTwoAndThree = [1, 2, 3];

    [Fact]
    public void TheRecognizedFloorsAreOneTwoAndThreeAndMatchTheFloorMigrations()
    {
        Assert.Equal(3, ContentAcceptanceRules.CurrentVersion); // E3-M2e: CARV 3
        Assert.Equal(OneTwoAndThree, ContentAcceptanceRules.RecognizedFloors); // E3-M2e (AM-1): floor 3
        Assert.Equal(ContentAcceptanceRules.RecognizedFloors, ContentAcceptanceFloors.Recognized.Select(f => f.Version));
        Assert.All(ContentAcceptanceFloors.Recognized, f => Assert.Equal(ContentAcceptanceRules.DatabaseObjects.FloorConstraintPrefix + f.Version, f.ConstraintName));
        Assert.All(ContentAcceptanceFloors.Recognized, f => Assert.True(ContentAcceptanceRules.DatabaseObjects.IsFenceObject(f.ConstraintName))); // refusals map to 409
        Assert.All(ContentAcceptanceFloors.Recognized, f => Assert.True(f.Version <= ContentAcceptanceRules.CurrentVersion));
        Assert.Equal(1, ContentAcceptancePolicy.ReleaseBFloor);
        Assert.Equal(ContentAcceptanceFloorSql.MigrationId, ContentAcceptanceFloors.Find(1)!.MigrationId);
        Assert.Equal(ContentAcceptanceFloor2Sql.MigrationId, ContentAcceptanceFloors.Find(2)!.MigrationId);
        Assert.Equal(ContentAcceptanceFloor3Sql.MigrationId, ContentAcceptanceFloors.Find(3)!.MigrationId);
        Assert.Null(ContentAcceptanceFloors.Find(4));
        Assert.True(string.CompareOrdinal(ContentAcceptanceFloorSql.MigrationId, ContentAcceptanceFloor2Sql.MigrationId) < 0); // applied in order
        Assert.True(string.CompareOrdinal(ContentAcceptanceFloor2Sql.MigrationId, ContentAcceptanceFloor3Sql.MigrationId) < 0);
        Assert.True(string.CompareOrdinal(ContentDataKeyLedgerSql.MigrationId, ContentAcceptanceFloor3Sql.MigrationId) < 0); // the ledger migration precedes floor 3
    }

    // ---- policy (D-4: minimum semantics) --------------------------------------------------------------------------------

    [Theory]
    [InlineData("1", true, "phase=ReleaseB requiredFloor=1")]
    [InlineData("2", true, "phase=ReleaseB requiredFloor=2")]
    [InlineData("3", true, "phase=ReleaseB requiredFloor=3")] // E3-M2e: floor 3 is recognized by a CARV-3 build
    [InlineData("4", false, "phase=Unresolved failure=RequiredFloorUnsupported")]
    [InlineData("0", false, "phase=Unresolved failure=RequiredFloorBelowMinimum")]
    [InlineData("02", true, "phase=ReleaseB requiredFloor=2")]
    public void ReleaseBRequiresARecognizedFloorUpToThisBuild(string floor, bool valid, string described)
    {
        var policy = ContentAcceptancePolicy.Resolve("ReleaseB", floor);
        Assert.Equal((valid, described), (policy.IsValid, policy.Describe()));
        Assert.True(policy.GuardsContentWrites);
    }

    // ---- the generalized write-fence evaluator -----------------------------------------------------------------------------

    private static readonly WriteFenceFacts Floor2Enforced = new(
        FenceRecorded: true, FloorRecorded: true, FloorDefinition: ContentAcceptanceFloor2Sql.ExpectedDefinition, HighestFloor: 2,
        CommitCheckDefinition: ContentAcceptanceWriteFence.ExpectedCommitCheckDefinition, PositiveCheckDefinition: ContentAcceptanceWriteFence.ExpectedPositiveCheckDefinition,
        StateGuardDefinition: ContentAcceptanceWriteFence.ExpectedStateGuardDefinition, StateGuardEnabled: "O", StateGuardSource: ContentAcceptanceWriteFence.ExpectedStateGuardSource,
        OtherTriggerCount: 0, RuleCount: 0, StampDefaultCount: 0, RelationKind: "r", ChildCount: 0,
        FloorConstraintCount: 1, RecordedFloors: new HashSet<int> { 1, 2 });

    private static readonly WriteFenceFacts Floor1Enforced = Floor2Enforced with
    {
        FloorDefinition = ContentAcceptanceWriteFence.ExpectedFloorDefinition, HighestFloor = 1, RecordedFloors = new HashSet<int> { 1 },
    };

    public static TheoryData<string, int, string?> Floor2States => new()
    {
        { "floor 2, floor 1 required (a minimum)", 1, null },
        { "floor 2, floor 2 required", 2, null },
        { "floor 2 validated", 2, null },
        { "floor 1, floor 1 required (unchanged)", 1, null },
        { "floor 1, floor 2 required", 2, ContentAcceptanceFloorFailures.FloorLowered },
        { "floor 1 put back after floor 2 was recorded, floor 1 required", 1, ContentAcceptanceFloorFailures.FloorRecordedButMissing },
        { "floor 1 put back after floor 2 was recorded, floor 2 required", 2, ContentAcceptanceFloorFailures.FloorLowered },
        { "floor 2 dropped, nothing put back", 2, ContentAcceptanceFloorFailures.FloorRecordedButMissing },
        { "floor 2 present, its history row missing", 1, ContentAcceptanceFloorFailures.FloorNotRecorded },
        { "floor 2 present, floor 1 history row missing", 1, ContentAcceptanceFloorFailures.FloorNotRecorded },
        { "floor 2 replaced by the floor-1 text", 1, ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "floor 2 without the IS NOT NULL term", 1, ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "floor 2 covering Prepared only", 1, ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "floors 1 and 2 both present", 1, ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "an unparseable floor name", 1, ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "floor 4 (above this build)", 1, ContentAcceptanceFloorFailures.FloorAboveBuild },
        { "floor 2, floor 3 required", 3, ContentAcceptanceFloorFailures.FloorLowered }, // E3-M2e: floor 3 is recognized, so floor 2 is below the required minimum
        { "floor 2, floor 4 required", 4, ContentAcceptanceFloorFailures.FloorUnverifiable },
        { "floor 2 on a version-1 build", 1, ContentAcceptanceFloorFailures.FloorAboveBuild },
    };

    [Theory]
    [MemberData(nameof(Floor2States))]
    public void TheWriteFenceEnforcesTheRequiredFloorAsAMinimumOverExactlyRecognizedDefinitions(string scenario, int required, string? expected)
    {
        var facts = scenario switch
        {
            "floor 2, floor 1 required (a minimum)" or "floor 2, floor 2 required" or "floor 2, floor 3 required" or "floor 2, floor 4 required" or "floor 2 on a version-1 build" => Floor2Enforced,
            "floor 2 validated" => Floor2Enforced with { FloorDefinition = ContentAcceptanceFloor2Sql.ExpectedDefinition[..^" NOT VALID".Length] },
            "floor 1, floor 1 required (unchanged)" or "floor 1, floor 2 required" => Floor1Enforced,
            "floor 1 put back after floor 2 was recorded, floor 1 required" or "floor 1 put back after floor 2 was recorded, floor 2 required" => Floor1Enforced with { RecordedFloors = new HashSet<int> { 1, 2 } },
            "floor 2 dropped, nothing put back" => Floor2Enforced with { FloorDefinition = null, HighestFloor = null, FloorConstraintCount = 0 },
            "floor 2 present, its history row missing" => Floor2Enforced with { RecordedFloors = new HashSet<int> { 1 } },
            "floor 2 present, floor 1 history row missing" => Floor2Enforced with { RecordedFloors = new HashSet<int> { 2 } },
            "floor 2 replaced by the floor-1 text" => Floor2Enforced with { FloorDefinition = ContentAcceptanceWriteFence.ExpectedFloorDefinition },
            "floor 2 without the IS NOT NULL term" => Floor2Enforced with { FloorDefinition = ContentAcceptanceFloor2Sql.ExpectedDefinition.Replace("(prepare_acceptance_rule_version IS NOT NULL) AND ", string.Empty, StringComparison.Ordinal) },
            "floor 2 covering Prepared only" => Floor2Enforced with { FloorDefinition = "CHECK ((((operation_state_name)::text <> 'Prepared'::text) OR ((prepare_acceptance_rule_version IS NOT NULL) AND (prepare_acceptance_rule_version >= 2)))) NOT VALID" },
            "floors 1 and 2 both present" => Floor2Enforced with { FloorConstraintCount = 2 },
            "an unparseable floor name" => Floor2Enforced with { FloorDefinition = null, HighestFloor = null, FloorConstraintCount = 1 },
            "floor 4 (above this build)" => Floor2Enforced with { HighestFloor = 4 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var build = scenario == "floor 2 on a version-1 build" ? 1 : ContentAcceptanceRules.CurrentVersion;
        Assert.Equal(expected, ContentAcceptanceWriteFence.Evaluate(facts, required, build));
    }

    // ---- readiness ------------------------------------------------------------------------------------------------------

    private static readonly ContentAcceptanceRules.SchemaFacts Fence = new(MigrationApplied: true, ColumnsPresent: true, CommitCheckPresent: true, StateGuardEnabled: true, InstalledFloor: null, DataKeyLedgerApplied: true);

    [Fact]
    public void ReadinessReportsARecordedHigherFloorWhoseConstraintIsMissing()
    {
        Assert.Equal((true, "content acceptance rules v2 (floor 2)"), ContentAcceptanceRules.Evaluate(Fence with { InstalledFloor = 2, FloorRecorded = true, HighestRecordedFloor = 2 }, 2, 2));
        Assert.Equal((false, "ContentAcceptanceRuleFloorMissing(recorded in the migration history, constraint absent)"),
            ContentAcceptanceRules.Evaluate(Fence with { InstalledFloor = 1, FloorRecorded = true, HighestRecordedFloor = 2 }));
        Assert.Equal((false, "ContentAcceptanceRuleFloorBelowRequired(installed=1, required=2)"),
            ContentAcceptanceRules.Evaluate(Fence with { InstalledFloor = 1, FloorRecorded = true, HighestRecordedFloor = 1 }, 2, 2));
        Assert.Equal((false, "ContentAcceptanceRuleFloorAboveBuild(floor=2, build=1)"), ContentAcceptanceRules.Evaluate(Fence with { InstalledFloor = 2, FloorRecorded = true, HighestRecordedFloor = 2 }, 1));
        Assert.Equal((true, "content acceptance rules v3 (floor 2); phase=ReleaseB requiredFloor=2"),
            ContentAcceptanceRules.EvaluateForPolicy(Fence with { InstalledFloor = 2, FloorRecorded = true, HighestRecordedFloor = 2 }, ContentAcceptancePolicy.Resolve("ReleaseB", "2")));
        Assert.Equal((true, "content acceptance rules v3 (floor 2); phase=ReleaseB requiredFloor=1"),
            ContentAcceptanceRules.EvaluateForPolicy(Fence with { InstalledFloor = 2, FloorRecorded = true, HighestRecordedFloor = 2 }, ContentAcceptancePolicy.Resolve("ReleaseB", "1")));
    }

    // ---- the floor-2 SQL ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFloor2SqlLocksChecksPreconditionsAddsFloor2ThenDropsFloor1()
    {
        var up = ContentAcceptanceFloor2Sql.UpSql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith("SET LOCAL lock_timeout = '5s';\nLOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;", up, StringComparison.Ordinal);
        var add = up.IndexOf("ADD CONSTRAINT ck_content_operation_rule_floor_v2", StringComparison.Ordinal);
        var drop = up.IndexOf("DROP CONSTRAINT ck_content_operation_rule_floor_v1", StringComparison.Ordinal);
        Assert.True(add > 0 && drop > add); // add-then-drop: never a moment without a floor
        foreach (var precondition in new[] { "floor 1 must be installed and recorded", "is not fully installed", "prepared below content acceptance rule version 2", "without a CARV build marker of version 2" })
        {
            Assert.InRange(up.IndexOf(precondition, StringComparison.Ordinal), 0, add); // every precondition runs under the lock, before the raise
        }

        Assert.Contains("CHECK (" + ContentAcceptanceFloor2Sql.FloorCheckSql + ") NOT VALID;", up, StringComparison.Ordinal);
        Assert.Contains("NOT IN ('Prepared', 'Committed')", ContentAcceptanceFloor2Sql.FloorCheckSql, StringComparison.Ordinal); // D-2
        Assert.Contains("prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2", ContentAcceptanceFloor2Sql.FloorCheckSql, StringComparison.Ordinal); // D-1
        Assert.Contains("carv-v([0-9]+)')::numeric, 0) < 2", up, StringComparison.Ordinal); // an older or unmarked writer blocks the raise (numeric: no overflow)
        Assert.DoesNotContain("VALIDATE", up, StringComparison.Ordinal); // D-4 (E3-M2b-1)
        Assert.Contains("floors are never lowered", ContentAcceptanceFloor2Sql.DownSql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", ContentAcceptanceFloor2Sql.DownSql, StringComparison.OrdinalIgnoreCase);

        var reinstate = ContentAcceptanceFloor2Sql.ReinstateSql.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith("SET LOCAL lock_timeout = '5s';\nLOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE;", reinstate, StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT IF EXISTS ck_content_operation_rule_floor_v1", reinstate, StringComparison.Ordinal);
        Assert.Contains("floor 2 is not recorded", reinstate, StringComparison.Ordinal);
        Assert.DoesNotContain("ef_migrations_history (", reinstate, StringComparison.Ordinal); // never writes the history
    }

    /// <summary>
    /// The reinstatement runs through EF's ExecuteSqlRaw, which reads braces as format items: a brace in any floor SQL would crash the
    /// recovery command (found by the M2d PostgreSQL gate, run 1, with a "{1,9}" regex quantifier). No floor SQL may contain one.
    /// </summary>
    [Fact]
    public void NoFloorSqlContainsABrace()
    {
        foreach (var floor in ContentAcceptanceFloors.Recognized)
        {
            Assert.DoesNotContain('{', floor.UpSql);
            Assert.DoesNotContain('}', floor.UpSql);
            Assert.DoesNotContain('{', floor.ReinstateSql);
            Assert.DoesNotContain('}', floor.ReinstateSql);
        }
    }

    [Fact]
    public void TheExpectedFloor2DefinitionDiffersFromFloor1OnlyInTheBound()
    {
        Assert.Equal(ContentAcceptanceFloor2Sql.ExpectedDefinition, ContentAcceptanceWriteFence.ExpectedFloorDefinition.Replace(">= 1", ">= 2", StringComparison.Ordinal));
        Assert.Equal(ContentAcceptanceFloor2Sql.FloorCheckSql, ContentAcceptanceFloorSql.FloorCheckSql.Replace(">= 1", ">= 2", StringComparison.Ordinal));
    }

    // ---- the operator command -------------------------------------------------------------------------------------------

    private static Microsoft.Extensions.Configuration.IConfiguration Unreachable() => new Microsoft.Extensions.Configuration.ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AuthorityDbMigration"] = "Host=unit-test.invalid;Database=unused;Username=unused;Password=unused;Timeout=2" })
        .Build();

    [Fact]
    public void Floor2IsAcceptedByTheCommandAndFailsClosedWhenTheDatabaseIsUnreachable()
    {
        Assert.True(ContentAcceptanceFloorCommand.TryRun(["--apply-content-acceptance-floor", "2"], Unreachable(), out var exitCode));
        Assert.Equal(3, exitCode); // got past every argument refusal; nothing applied
        Assert.True(ContentAcceptanceFloorCommand.TryRun(["--apply-content-acceptance-floor", "2", "--migrate"], Unreachable(), out exitCode));
        Assert.Equal(2, exitCode);
    }
}
