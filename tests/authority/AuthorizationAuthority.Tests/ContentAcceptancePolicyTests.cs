using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// RB-CARV-1 (contract §22): the Release A / Release B policy (no default, no fallback, no downgrade), the content write fence
/// evaluator (missing, lowered, weakened and inconsistent floors), the failure vocabulary, and the policy-aware readiness. The
/// in-transaction verification itself runs against real PostgreSQL (harness R).
/// </summary>
public sealed class ContentAcceptancePolicyTests
{
    // ---- policy resolution -------------------------------------------------------------------------------------------------

    public static TheoryData<string?, string?, string> InvalidConfigurations => new()
    {
        { null, null, ContentAcceptancePolicy.PhaseUnconfigured },
        { "", null, ContentAcceptancePolicy.PhaseUnconfigured },
        { "Unconfigured", null, ContentAcceptancePolicy.PhaseUnconfigured },
        { null, "1", ContentAcceptancePolicy.PhaseUnconfigured }, // a required floor without a phase is never Release A
        { "releaseb", "1", ContentAcceptancePolicy.PhaseUnsupported },
        { " ReleaseB", "1", ContentAcceptancePolicy.PhaseUnsupported },
        { "ReleaseB ", "1", ContentAcceptancePolicy.PhaseUnsupported },
        { "ReleaseC", null, ContentAcceptancePolicy.PhaseUnsupported },
        { "B", "1", ContentAcceptancePolicy.PhaseUnsupported },
        { "ReleaseA", "1", ContentAcceptancePolicy.FloorNotPermittedInReleaseA },
        { "ReleaseB", null, ContentAcceptancePolicy.FloorMissing },
        { "ReleaseB", "", ContentAcceptancePolicy.FloorMissing },
        { "ReleaseB", "abc", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "1.0", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", " 1", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "+1", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "-1", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "1e0", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "9999999999", ContentAcceptancePolicy.FloorMalformed },
        { "ReleaseB", "0", ContentAcceptancePolicy.FloorBelowMinimum }, // the downgrade attempt
        { "ReleaseB", "3", ContentAcceptancePolicy.FloorUnsupported },  // E3-M2d: above this build (2) and not a recognized floor
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void AnInvalidConfigurationIsNeverReleaseAAndGuardsEveryContentWrite(string? phase, string? floor, string reason)
    {
        var policy = ContentAcceptancePolicy.Resolve(phase, floor);

        Assert.False(policy.IsValid);
        Assert.Equal(reason, policy.ConfigurationFailure);
        Assert.Equal(ContentAcceptancePhase.Unresolved, policy.Phase);
        Assert.NotEqual(ContentAcceptancePhase.ReleaseA, policy.Phase);
        Assert.Null(policy.RequiredFloor);
        Assert.True(policy.GuardsContentWrites); // the store refuses every content write of an invalid policy
        Assert.Equal($"phase=Unresolved failure={reason}", policy.Describe());
    }

    [Fact]
    public void OnlyTheTwoExactConfigurationsAreValid()
    {
        var a = ContentAcceptancePolicy.Resolve("ReleaseA", null);
        Assert.Equal((true, ContentAcceptancePhase.ReleaseA, (int?)null, false, "phase=ReleaseA"), (a.IsValid, a.Phase, a.RequiredFloor, a.GuardsContentWrites, a.Describe()));

        var b = ContentAcceptancePolicy.Resolve("ReleaseB", "1");
        Assert.Equal((true, ContentAcceptancePhase.ReleaseB, (int?)1, true, "phase=ReleaseB requiredFloor=1"), (b.IsValid, b.Phase, b.RequiredFloor, b.GuardsContentWrites, b.Describe()));

        Assert.Equal(ContentAcceptanceFloorSql.FloorVersion, ContentAcceptancePolicy.ReleaseBFloor); // the floor the migration installs
        Assert.Equal("ContentAcceptance:EnforcementPhase", ContentAcceptancePolicy.PhaseKey);
        Assert.Equal("ContentAcceptance:RequiredFloor", ContentAcceptancePolicy.RequiredFloorKey);
    }

    // ---- the content write fence evaluator -------------------------------------------------------------------------------

    /// <summary>Exactly what PostgreSQL 18 reports for a database with E3-M2b-1 and floor 1 installed (harness R checks the same live).</summary>
    private static readonly WriteFenceFacts Enforced = new(
        FenceRecorded: true, FloorRecorded: true, FloorDefinition: ContentAcceptanceWriteFence.ExpectedFloorDefinition, HighestFloor: 1,
        CommitCheckDefinition: ContentAcceptanceWriteFence.ExpectedCommitCheckDefinition, PositiveCheckDefinition: ContentAcceptanceWriteFence.ExpectedPositiveCheckDefinition,
        StateGuardDefinition: ContentAcceptanceWriteFence.ExpectedStateGuardDefinition, StateGuardEnabled: "O", StateGuardSource: ContentAcceptanceWriteFence.ExpectedStateGuardSource,
        OtherTriggerCount: 0, RuleCount: 0, StampDefaultCount: 0, RelationKind: "r", ChildCount: 0);

    private const string PreparedOnlyFloor =
        "CHECK ((((operation_state_name)::text <> 'Prepared'::text) OR ((prepare_acceptance_rule_version IS NOT NULL) AND (prepare_acceptance_rule_version >= 1)))) NOT VALID";

    public static TheoryData<string, string?> FenceStates => new()
    {
        { "enforced", null },
        { "enforced, floor validated", null },
        { "enforced, trigger ENABLE ALWAYS", null },
        { "enforced, CRLF function source", null },
        { "restore to before the floor", ContentAcceptanceFloorFailures.FloorNotInstalled },
        { "floor dropped, history kept", ContentAcceptanceFloorFailures.FloorRecordedButMissing },
        { "floor replaced by a lower floor", ContentAcceptanceFloorFailures.FloorLowered },
        { "floor replaced by a Prepared-only floor", ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "floor replaced without the IS NOT NULL term", ContentAcceptanceFloorFailures.FloorDefinitionMismatch },
        { "floor present, history row missing", ContentAcceptanceFloorFailures.FloorNotRecorded },
        { "E3-M2b-1 history row missing", ContentAcceptanceFloorFailures.FenceMigrationNotRecorded },
        { "commit CHECK dropped", ContentAcceptanceFloorFailures.FenceCommitCheckMismatch },
        { "commit CHECK replaced", ContentAcceptanceFloorFailures.FenceCommitCheckMismatch },
        { "positive CHECK dropped", ContentAcceptanceFloorFailures.FencePositiveCheckMismatch },
        { "state guard disabled", ContentAcceptanceFloorFailures.FenceStateGuardMismatch },
        { "state guard REPLICA only", ContentAcceptanceFloorFailures.FenceStateGuardMismatch },
        { "state guard dropped", ContentAcceptanceFloorFailures.FenceStateGuardMismatch },
        { "state guard function replaced", ContentAcceptanceFloorFailures.FenceStateGuardMismatch },
        { "state guard fires on INSERT only", ContentAcceptanceFloorFailures.FenceStateGuardMismatch },
        { "a default on the Prepare stamp", ContentAcceptanceFloorFailures.FenceStampDefaultPresent },
        { "an extra trigger", ContentAcceptanceFloorFailures.FenceUnexpectedTableObject },
        { "a rewrite rule", ContentAcceptanceFloorFailures.FenceUnexpectedTableObject },
        { "an inheritance child", ContentAcceptanceFloorFailures.FenceUnexpectedTableObject },
        { "a partitioned table", ContentAcceptanceFloorFailures.FenceUnexpectedTableObject },
        { "a floor above this build", ContentAcceptanceFloorFailures.FloorAboveBuild },
        { "no facts", ContentAcceptanceFloorFailures.FloorUnverifiable },
    };

    [Theory]
    [MemberData(nameof(FenceStates))]
    public void TheWriteFenceAcceptsOnlyTheExactlyInstalledFloorAndFence(string scenario, string? expected)
    {
        WriteFenceFacts? facts = scenario switch
        {
            "enforced" => Enforced,
            "enforced, floor validated" => Enforced with { FloorDefinition = ContentAcceptanceWriteFence.ExpectedFloorDefinition[..^" NOT VALID".Length] },
            "enforced, trigger ENABLE ALWAYS" => Enforced with { StateGuardEnabled = "A" },
            "enforced, CRLF function source" => Enforced with { StateGuardSource = ContentAcceptanceWriteFence.ExpectedStateGuardSource.Replace("\n", "\r\n", StringComparison.Ordinal) },
            "restore to before the floor" => Enforced with { FloorDefinition = null, HighestFloor = null, FloorRecorded = false },
            "floor dropped, history kept" => Enforced with { FloorDefinition = null, HighestFloor = null },
            "floor replaced by a lower floor" => Enforced with { FloorDefinition = null, HighestFloor = 0 },
            "floor replaced by a Prepared-only floor" => Enforced with { FloorDefinition = PreparedOnlyFloor },
            "floor replaced without the IS NOT NULL term" => Enforced with { FloorDefinition = ContentAcceptanceWriteFence.ExpectedFloorDefinition.Replace("(prepare_acceptance_rule_version IS NOT NULL) AND ", string.Empty, StringComparison.Ordinal) },
            "floor present, history row missing" => Enforced with { FloorRecorded = false },
            "E3-M2b-1 history row missing" => Enforced with { FenceRecorded = false },
            "commit CHECK dropped" => Enforced with { CommitCheckDefinition = null },
            "commit CHECK replaced" => Enforced with { CommitCheckDefinition = "CHECK (true)" },
            "positive CHECK dropped" => Enforced with { PositiveCheckDefinition = null },
            "state guard disabled" => Enforced with { StateGuardEnabled = "D" },
            "state guard REPLICA only" => Enforced with { StateGuardEnabled = "R" },
            "state guard dropped" => Enforced with { StateGuardDefinition = null, StateGuardEnabled = null, StateGuardSource = null },
            "state guard function replaced" => Enforced with { StateGuardSource = "\nBEGIN\n    RETURN NEW;\nEND " },
            "state guard fires on INSERT only" => Enforced with { StateGuardDefinition = ContentAcceptanceWriteFence.ExpectedStateGuardDefinition.Replace("INSERT OR UPDATE", "INSERT", StringComparison.Ordinal) },
            "a default on the Prepare stamp" => Enforced with { StampDefaultCount = 1 },
            "an extra trigger" => Enforced with { OtherTriggerCount = 1 },
            "a rewrite rule" => Enforced with { RuleCount = 1 },
            "an inheritance child" => Enforced with { ChildCount = 1 },
            "a partitioned table" => Enforced with { RelationKind = "p" },
            "a floor above this build" => Enforced with { HighestFloor = 3 }, // E3-M2d: floor 2 is this build's own version
            "no facts" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(expected, ContentAcceptanceWriteFence.Evaluate(facts, ContentAcceptancePolicy.ReleaseBFloor));
    }

    [Fact]
    public void TheFenceNeverComparesAFloorItDoesNotDefine()
    {
        Assert.Equal(ContentAcceptanceFloorFailures.FloorUnverifiable, ContentAcceptanceWriteFence.Evaluate(Enforced, 3)); // E3-M2d: 3 is neither recognized nor <= the build
        Assert.Equal(ContentAcceptanceFloorFailures.FloorUnverifiable, ContentAcceptanceWriteFence.Evaluate(Enforced, 0));
        Assert.Equal(ContentAcceptanceFloorFailures.FloorUnverifiable, ContentAcceptanceWriteFence.Evaluate(Enforced, 2, buildVersion: 1)); // a recognized floor above the build
    }

    [Fact]
    public void TheExpectedFunctionSourceIsExactlyTheMigrationsFunctionBody()
    {
        var source = ContentAcceptanceWriteFence.ExpectedStateGuardSource;
        Assert.StartsWith("\nBEGIN\n    IF TG_OP = 'INSERT' THEN", source, StringComparison.Ordinal);
        Assert.EndsWith("    RETURN NEW;\nEND ", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", source, StringComparison.Ordinal);
        Assert.Contains("a terminal content operation is immutable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockIsRowExclusiveOnTheOperationsTable() =>
        Assert.Equal("LOCK TABLE authority.content_change_operations IN ROW EXCLUSIVE MODE", ContentAcceptanceWriteFence.LockSql);

    // ---- diagnostics ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRequiredDiagnosticCategoriesAreDistinguishable()
    {
        Assert.Equal("MissingFloor", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.FloorNotInstalled));
        Assert.Equal("MissingFloor", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.FloorRecordedButMissing));
        Assert.Equal("LoweredFloor", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.FloorLowered));
        Assert.Equal("UnverifiableFloor", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.FloorUnverifiable));
        Assert.Equal("ConfigurationPolicyFailure", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.ConfigurationPolicyInvalid));
        Assert.Equal("WeakenedFence", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.FloorDefinitionMismatch));
        Assert.Equal("InconsistentMigrationHistory", ContentAcceptanceFloorFailures.CategoryOf(ContentAcceptanceFloorFailures.FloorNotRecorded));
        Assert.Equal("RecoveryRequired:ApplyFloor", ContentAcceptanceFloorFailures.RecoveryOf(ContentAcceptanceFloorFailures.FloorNotInstalled));
        Assert.Equal("RecoveryRequired:ReinstateRecordedFloor", ContentAcceptanceFloorFailures.RecoveryOf(ContentAcceptanceFloorFailures.FloorRecordedButMissing));
        Assert.StartsWith("RecoveryRequired:", ContentAcceptanceFloorFailures.RecoveryOf(ContentAcceptanceFloorFailures.FloorDefinitionMismatch), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCallerSeesOneNonRetryableUnavailableCodeAndNoDatabaseInternals()
    {
        var refusal = ContentAcceptanceFloorFailures.Refusal();
        Assert.Equal(("CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED", 503, false), (refusal.ErrorCode, refusal.HttpStatusCode, refusal.Retryable));
        Assert.NotEqual(Application.Common.AuthorityErrorCodes.OperationRulesOutdated, refusal.ErrorCode); // never an ordinary content conflict
        foreach (var internals in new[] { "ck_", "pg_", "constraint", "trigger", "Floor", "history" })
        {
            Assert.DoesNotContain(internals, refusal.Message, StringComparison.Ordinal);
        }
    }

    // ---- readiness ---------------------------------------------------------------------------------------------------------

    private static readonly ContentAcceptanceRules.SchemaFacts FloorFacts = new(MigrationApplied: true, ColumnsPresent: true, CommitCheckPresent: true, StateGuardEnabled: true, InstalledFloor: 1, FloorRecorded: true);

    [Fact]
    public void ReleaseAReadinessIsExactlyTheE3M2b1Decision()
    {
        var a = ContentAcceptancePolicy.Resolve("ReleaseA", null);
        var withFenceVerdict = FloorFacts with { WriteFenceFailure = ContentAcceptanceFloorFailures.FloorDefinitionMismatch };
        Assert.Equal(ContentAcceptanceRules.Evaluate(withFenceVerdict), ContentAcceptanceRules.EvaluateForPolicy(withFenceVerdict, a));
        Assert.Equal(ContentAcceptanceRules.Evaluate(FloorFacts), ContentAcceptanceRules.EvaluateForPolicy(FloorFacts, null));
    }

    [Fact]
    public void ReleaseBReadinessRequiresTheFloorAndTheWriteFence()
    {
        var b = ContentAcceptancePolicy.Resolve("ReleaseB", "1");
        Assert.Equal((true, "content acceptance rules v2 (floor 1); phase=ReleaseB requiredFloor=1"), ContentAcceptanceRules.EvaluateForPolicy(FloorFacts, b));
        Assert.Equal((false, "ContentAcceptanceRuleFloorBelowRequired(installed=none, required=1)"), ContentAcceptanceRules.EvaluateForPolicy(FloorFacts with { InstalledFloor = null, FloorRecorded = false }, b));
        Assert.Equal((false, "ContentAcceptanceFloorNotEnforced(reason=FloorDefinitionMismatch, category=WeakenedFence)"),
            ContentAcceptanceRules.EvaluateForPolicy(FloorFacts with { WriteFenceFailure = ContentAcceptanceFloorFailures.FloorDefinitionMismatch }, b));
        Assert.Equal((false, "ContentAcceptancePolicyInvalid(RequiredFloorMissing)"), ContentAcceptanceRules.EvaluateForPolicy(FloorFacts, ContentAcceptancePolicy.Resolve("ReleaseB", null)));
        Assert.Equal((false, "ContentAcceptanceRulesUnverified"), ContentAcceptanceRules.EvaluateForPolicy(null, b));
    }
}
