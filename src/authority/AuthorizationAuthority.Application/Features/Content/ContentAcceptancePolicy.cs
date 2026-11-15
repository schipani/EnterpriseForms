using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>RB-CARV-1 (contract §22): the deployment phase of the content acceptance rule fence.</summary>
public enum ContentAcceptancePhase
{
    /// <summary>The configuration could not be resolved. Every content write is refused and the host refuses to start.</summary>
    Unresolved = 0,

    /// <summary>Release A: CARV builds, no floor required (the floor may or may not be installed yet).</summary>
    ReleaseA = 1,

    /// <summary>Release B: the floor <see cref="ContentAcceptancePolicy.RequiredFloor"/> must be verified in every content write transaction.</summary>
    ReleaseB = 2,
}

/// <summary>
/// RB-CARV-1 (contract §22): the content acceptance enforcement policy of this Authority instance, resolved once at startup from
/// deployment configuration (<c>ContentAcceptance:EnforcementPhase</c>, <c>ContentAcceptance:RequiredFloor</c>) and never from
/// the database, an organization setting or an API. It is held outside the database being restored, so a restored pre-floor
/// AuthorityDb cannot turn a Release B instance back into Release A.
/// <para>
/// There is no default: an absent, blank, <c>Unconfigured</c> or unknown phase is a configuration failure, never Release A.
/// Release B requires exactly the floor this build defines (<see cref="ReleaseBFloor"/>); a lower value (a downgrade), a higher
/// one (this build cannot satisfy it), a malformed one or a missing one is a configuration failure. Release A refuses a
/// <c>RequiredFloor</c> (an ambiguous half-configuration). An invalid policy refuses every content write and the host start.
/// </para>
/// <para>
/// The application cannot protect its own configuration: keeping these two values out of reach of business deployers and the
/// Authority runtime, and auditing their changes, is a deployment control (PB-11), not something this class guarantees.
/// </para>
/// </summary>
public sealed class ContentAcceptancePolicy
{
    public const string SectionName = "ContentAcceptance";
    public const string PhaseKey = SectionName + ":EnforcementPhase";
    public const string RequiredFloorKey = SectionName + ":RequiredFloor";
    public const string ReleaseAName = "ReleaseA";
    public const string ReleaseBName = "ReleaseB";
    public const string UnconfiguredName = "Unconfigured";

    /// <summary>The only floor this build defines and can enforce (floor migration AuthorityContentAcceptanceRuleFloor1).</summary>
    public const int ReleaseBFloor = 1;

    // Configuration failure reasons (diagnostic codes; contain no configuration values).
    public const string PhaseUnconfigured = "EnforcementPhaseUnconfigured";
    public const string PhaseUnsupported = "EnforcementPhaseUnsupported";
    public const string FloorNotPermittedInReleaseA = "RequiredFloorNotPermittedInReleaseA";
    public const string FloorMissing = "RequiredFloorMissing";
    public const string FloorMalformed = "RequiredFloorMalformed";
    public const string FloorBelowMinimum = "RequiredFloorBelowMinimum";
    public const string FloorUnsupported = "RequiredFloorUnsupported";

    private ContentAcceptancePolicy(ContentAcceptancePhase phase, int? requiredFloor, string? configurationFailure)
    {
        Phase = phase;
        RequiredFloor = requiredFloor;
        ConfigurationFailure = configurationFailure;
    }

    public ContentAcceptancePhase Phase { get; }

    /// <summary>Release B only: the floor every content write transaction must verify. Null in Release A and when invalid.</summary>
    public int? RequiredFloor { get; }

    /// <summary>Null when the policy is valid; otherwise the reason (one of the constants above).</summary>
    public string? ConfigurationFailure { get; }

    public bool IsValid => ConfigurationFailure is null;

    /// <summary>True when content writes must verify the floor (Release B) or are refused outright (an invalid policy).</summary>
    public bool GuardsContentWrites => !IsValid || Phase == ContentAcceptancePhase.ReleaseB;

    /// <summary>Observable, secret-free description, for example <c>phase=ReleaseB requiredFloor=1</c>.</summary>
    public string Describe() => IsValid
        ? Phase == ContentAcceptancePhase.ReleaseB
            ? $"phase={ReleaseBName} requiredFloor={RequiredFloor!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"phase={ReleaseAName}"
        : $"phase=Unresolved failure={ConfigurationFailure}";

    /// <summary>
    /// Resolves the policy from the two raw configuration values. Never throws and never falls back: every value that is not
    /// exactly valid yields an invalid policy. Values are compared ordinally and are not trimmed.
    /// </summary>
    public static ContentAcceptancePolicy Resolve(string? phase, string? requiredFloor)
    {
        if (string.IsNullOrEmpty(phase) || string.Equals(phase, UnconfiguredName, StringComparison.Ordinal))
        {
            return Invalid(PhaseUnconfigured);
        }

        var floorGiven = !string.IsNullOrEmpty(requiredFloor);
        if (string.Equals(phase, ReleaseAName, StringComparison.Ordinal))
        {
            return floorGiven ? Invalid(FloorNotPermittedInReleaseA) : new ContentAcceptancePolicy(ContentAcceptancePhase.ReleaseA, null, null);
        }

        if (!string.Equals(phase, ReleaseBName, StringComparison.Ordinal))
        {
            return Invalid(PhaseUnsupported);
        }

        if (!floorGiven)
        {
            return Invalid(FloorMissing);
        }

        if (requiredFloor!.Length > 9 || requiredFloor.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return Invalid(FloorMalformed);
        }

        var floor = int.Parse(requiredFloor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        if (floor < ReleaseBFloor)
        {
            return Invalid(FloorBelowMinimum);
        }

        return floor > ReleaseBFloor ? Invalid(FloorUnsupported) : new ContentAcceptancePolicy(ContentAcceptancePhase.ReleaseB, floor, null);
    }

    private static ContentAcceptancePolicy Invalid(string reason) => new(ContentAcceptancePhase.Unresolved, null, reason);
}

/// <summary>
/// RB-CARV-1 (contract §22): why a Release B content write was refused. The reason codes are operational diagnostics (logs,
/// alerts, readiness); callers only ever see <see cref="AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced"/>.
/// </summary>
public static class ContentAcceptanceFloorFailures
{
    public const string AlertKind = "CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED";

    public const string ConfigurationPolicyInvalid = "ConfigurationPolicyInvalid";
    public const string FloorNotInstalled = "FloorNotInstalled";
    public const string FloorRecordedButMissing = "FloorRecordedButMissing";
    public const string FloorLowered = "FloorLowered";
    public const string FloorAboveBuild = "FloorAboveBuild";
    public const string FloorDefinitionMismatch = "FloorDefinitionMismatch";
    public const string FloorNotRecorded = "FloorNotRecorded";
    public const string FenceMigrationNotRecorded = "FenceMigrationNotRecorded";
    public const string FenceCommitCheckMismatch = "FenceCommitCheckMismatch";
    public const string FencePositiveCheckMismatch = "FencePositiveCheckMismatch";
    public const string FenceStateGuardMismatch = "FenceStateGuardMismatch";
    public const string FenceStampDefaultPresent = "FenceStampDefaultPresent";
    public const string FenceUnexpectedTableObject = "FenceUnexpectedTableObject";
    public const string FloorUnverifiable = "FloorUnverifiable";

    /// <summary>The diagnostic category the owner requires to be distinguishable (contract §22.7).</summary>
    public static string CategoryOf(string reason) => reason switch
    {
        ConfigurationPolicyInvalid => "ConfigurationPolicyFailure",
        FloorNotInstalled or FloorRecordedButMissing => "MissingFloor",
        FloorLowered => "LoweredFloor",
        FloorDefinitionMismatch or FenceCommitCheckMismatch or FencePositiveCheckMismatch or FenceStateGuardMismatch or FenceStampDefaultPresent or FenceUnexpectedTableObject => "WeakenedFence",
        FloorNotRecorded or FenceMigrationNotRecorded => "InconsistentMigrationHistory",
        FloorAboveBuild => "BuildBelowFloor",
        _ => "UnverifiableFloor",
    };

    /// <summary>The operator action the reason calls for (operations runbook §2b, §6).</summary>
    public static string RecoveryOf(string reason) => reason switch
    {
        ConfigurationPolicyInvalid => "CorrectDeploymentConfiguration",
        FloorNotInstalled => "RecoveryRequired:ApplyFloor",
        FloorRecordedButMissing => "RecoveryRequired:ReinstateRecordedFloor",
        FloorAboveBuild => "DeployBuildAtOrAboveFloor",
        FloorUnverifiable => "CheckAuthorityDbReachabilityAndPrivileges",
        _ => "RecoveryRequired:Incident",
    };

    /// <summary>What the caller receives: no database internals, no reason code (it would describe the security state).</summary>
    public static AuthorityException Refusal() =>
        new(AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced, 503, false, "content writes are refused: the required content acceptance floor is not verified; operator recovery is required");
}
