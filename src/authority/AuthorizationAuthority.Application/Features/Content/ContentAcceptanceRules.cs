namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2b-1 content acceptance rule version (CARV; docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §19).
/// <para>
/// Every content operation records the rule version its Prepare applied (P). Commit accepts only an operation whose P equals
/// this build's compiled <see cref="CurrentVersion"/> (V), and records its own version (C = V). The database enforces C = P (or
/// both NULL for an operation a pre-CARV build prepared and commits) and a state guard; together they fence operations that
/// cross builds with different rules during a rolling deployment, independent of time and of the operation lifetime.
/// </para>
/// <para>
/// <see cref="CurrentVersion"/> is incremented by a reviewed code change whenever Prepare starts enforcing a rule that Commit does
/// not fully re-derive. Version 1 = the E3-M2a rule set, including the cross-revision envelope rule. Version 2 (E3-M2d, owner
/// decision D-2) = version 1 plus the Prepare-only Envelope v2 rules: carried/new classification against the current accepted
/// head, DK-3, the refusal of every new v2 row and the v2-to-v1 downgrade guard (EnvelopeV2Rules.EnforceAgainstCurrent).
/// Version 3 (E3-M2e, docs/security/E3_M2E_CONTRACT_CLOSURE.md) = version 2 plus the Prepare-only data-key ledger rules: the
/// carried/new classification now leads to acceptance (behind the default-off switch), DK-4 against the ledger and the DK-5
/// binding insert in the Prepare transaction. Commit re-derives DK-6 and P-4 from the staged bytes and the ledger.
/// </para>
/// </summary>
public static class ContentAcceptanceRules
{
    /// <summary>V: the rule version this build applies at Prepare and requires at Commit.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// E3-M2d (D-3/D-4): the floors this build recognizes, each with an exact database definition (floor migrations
    /// AuthorityContentAcceptanceRuleFloor1, -Floor2 and, E3-M2e (AM-1), -Floor3). A Release B deployment requires one of them as a
    /// minimum; an installed floor outside this set, or above <see cref="CurrentVersion"/>, fails closed.
    /// </summary>
    public static readonly IReadOnlyList<int> RecognizedFloors = [1, 2, 3];

    /// <summary>The lowest recognized floor (floor 1, E3-M2b-1F).</summary>
    public const int MinimumFloor = 1;

    /// <summary>OD-M2-30: how often a running instance re-reads the database fence for its readiness decision.</summary>
    public static readonly TimeSpan ReadinessRecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Abort reason recorded on an operation refused by the CARV Commit rule.</summary>
    public const string OutdatedAbortReason = "OPERATION_RULES_OUTDATED";

    /// <summary>Database objects of the fence (migration AuthorityContentAcceptanceRuleVersion and later floor migrations).</summary>
    public static class DatabaseObjects
    {
        public const string PrepareColumn = "prepare_acceptance_rule_version";
        public const string CommitColumn = "commit_acceptance_rule_version";
        public const string PositiveCheck = "ck_content_operation_rule_versions_positive";
        public const string CommitCheck = "ck_content_operation_commit_rule_version";
        public const string StateGuardTrigger = "trg_content_operation_rule_state_guard";
        public const string StateGuardFunction = "content_operation_rule_state_guard";

        /// <summary>Floor constraints are named <c>ck_content_operation_rule_floor_v{F}</c>; exactly one is present once a floor exists.</summary>
        public const string FloorConstraintPrefix = "ck_content_operation_rule_floor_v";

        /// <summary>True for the name of any constraint or guard whose refusal means "this operation crosses rule versions".</summary>
        public static bool IsFenceObject(string? name) =>
            name is CommitCheck or StateGuardTrigger
            || (name is not null && name.StartsWith(FloorConstraintPrefix, StringComparison.Ordinal) && name.Length > FloorConstraintPrefix.Length
                && name.AsSpan(FloorConstraintPrefix.Length).IndexOfAnyExceptInRange('0', '9') < 0);
    }

    /// <summary>The Commit rule: only an operation prepared under exactly this build's rules may be committed by it.</summary>
    public static bool IsCommittableByThisBuild(int? prepareAcceptanceRuleVersion) => prepareAcceptanceRuleVersion == CurrentVersion;

    /// <summary>
    /// What the readiness guard reads from the database (one query; see the Infrastructure reader). E3-M2b-1F adds
    /// <paramref name="FloorRecorded"/>: the migration history records that a floor migration was applied.
    /// </summary>
    /// E3-M2d adds <paramref name="HighestRecordedFloor"/>: the highest recognized floor whose migration the history records (null
    /// when the reader cannot tell; then <paramref name="FloorRecorded"/> alone means floor 1, as in E3-M2b-1F).
    /// E3-M2e (AM-12) adds <paramref name="DataKeyLedgerApplied"/>: the history records the data-key ledger migration. Null (unknown)
    /// is not ready, like every other unknown fact.
    public sealed record SchemaFacts(bool MigrationApplied, bool ColumnsPresent, bool CommitCheckPresent, bool StateGuardEnabled, int? InstalledFloor, bool FloorRecorded = false, string? WriteFenceFailure = null, int? HighestRecordedFloor = null, bool? DataKeyLedgerApplied = null);

    /// <summary>
    /// RB-CARV-1 (contract §22): the readiness decision for this instance's policy. Release A (or no policy, as in the E3-M2b-1
    /// tests) is exactly <see cref="Evaluate"/> without a required floor, so every E3-M2b-1/1F decision and detail string is
    /// unchanged. An invalid policy is not ready. Release B adds the required floor and the write fence verdict
    /// (<see cref="SchemaFacts.WriteFenceFailure"/>, the same check every Release B content write performs in its transaction).
    /// Readiness remains a signal: the write path enforces independently.
    /// </summary>
    public static (bool Ready, string Detail) EvaluateForPolicy(SchemaFacts? facts, ContentAcceptancePolicy? policy, int compiledVersion = CurrentVersion)
    {
        if (policy is null || (policy.IsValid && policy.Phase == ContentAcceptancePhase.ReleaseA))
        {
            return Evaluate(facts, compiledVersion);
        }

        if (!policy.IsValid)
        {
            return (false, $"ContentAcceptancePolicyInvalid({policy.ConfigurationFailure})");
        }

        var (ready, detail) = Evaluate(facts, compiledVersion, policy.RequiredFloor);
        if (!ready)
        {
            return (false, detail);
        }

        if (facts!.WriteFenceFailure is { } failure)
        {
            return (false, $"ContentAcceptanceFloorNotEnforced(reason={failure}, category={ContentAcceptanceFloorFailures.CategoryOf(failure)})");
        }

        return (true, $"{detail}; {policy.Describe()}");
    }

    /// <summary>
    /// The startup and readiness decision (§19.8): ready only if the CARV migration, both columns, the commit CHECK and an
    /// enabled state guard are present, and no installed floor exceeds this build's version. Unknown facts are not ready.
    /// E3-M2b-1F (§21.6) adds the detection of a missing or lowered floor: not ready when the history records a floor whose
    /// constraint is absent, or when the deployment's <paramref name="requiredFloor"/> is above the installed floor (or above this
    /// build). Readiness is detection only; the database enforces the floor.
    /// </summary>
    public static (bool Ready, string Detail) Evaluate(SchemaFacts? facts, int compiledVersion = CurrentVersion, int? requiredFloor = null)
    {
        if (facts is null)
        {
            return (false, "ContentAcceptanceRulesUnverified");
        }

        if (!facts.MigrationApplied)
        {
            return (false, "ContentAcceptanceRuleMigrationMissing");
        }

        if (!facts.ColumnsPresent || !facts.CommitCheckPresent)
        {
            return (false, "ContentAcceptanceRuleSchemaIncomplete");
        }

        if (!facts.StateGuardEnabled)
        {
            return (false, "ContentAcceptanceRuleStateGuardMissing");
        }

        // E3-M2e (AM-12): a CARV-3 build requires the data-key ledger migration (a database restored below it, or never migrated).
        if (facts.DataKeyLedgerApplied is not true)
        {
            return (false, "ContentDataKeyLedgerMigrationMissing");
        }

        if (facts.InstalledFloor is { } floor && floor > compiledVersion)
        {
            return (false, $"ContentAcceptanceRuleFloorAboveBuild(floor={floor}, build={compiledVersion})");
        }

        if (requiredFloor is { } required && required > compiledVersion)
        {
            return (false, $"ContentAcceptanceRuleRequiredFloorAboveBuild(required={required}, build={compiledVersion})");
        }

        // E3-M2d: a recorded floor higher than the installed one is a missing (lowered) floor, for every recognized floor.
        var recorded = facts.HighestRecordedFloor ?? (facts.FloorRecorded ? MinimumFloor : null);
        if (recorded is { } highestRecorded && highestRecorded > (facts.InstalledFloor ?? 0))
        {
            return (false, "ContentAcceptanceRuleFloorMissing(recorded in the migration history, constraint absent)");
        }

        if (requiredFloor is { } minimum && (facts.InstalledFloor ?? 0) < minimum)
        {
            return (false, $"ContentAcceptanceRuleFloorBelowRequired(installed={(facts.InstalledFloor is { } i ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}, required={minimum})");
        }

        return (true, $"content acceptance rules v{compiledVersion} (floor {(facts.InstalledFloor is { } f ? f.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")})");
    }
}
