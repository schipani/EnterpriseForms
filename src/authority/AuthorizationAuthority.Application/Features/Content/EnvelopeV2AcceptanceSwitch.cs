using System.Security.Cryptography;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md OD-M2e-07, AM-5, AM-9): whether this Authority instance accepts **New** Envelope v2
/// data-key groups. Resolved once from Authority deployment configuration (<c>Content:EnvelopeV2AcceptanceEnabled</c>), never from an
/// organization, form, user, database or API value. <b>Default off</b>: absent, empty, malformed or any value other than the literal
/// <c>true</c> is off. No automatic fallback in either direction.
/// <list type="bullet">
/// <item>Configured on outside Development/Testing (Staging and Production included): startup refusal.</item>
/// <item>Configured on without Release B and <c>RequiredFloor = 3</c>: startup refusal (Release A has no transactional write fence).</item>
/// <item>Configured on without the data-key ledger migration: startup refusal (checked by the Api startup guard, which can read the database).</item>
/// <item>Even with every prerequisite, acceptance is effective only once the exact effective policy has been durably recorded on the
/// platform journal (<c>EnvelopeAcceptancePolicyObserved</c>, OD-M2e-13). A change not yet recorded leaves acceptance off.</item>
/// <item>The transactional floor and ledger checks (Release B fence, L-13) are enforced by the store in every accepting transaction;
/// this switch is never their substitute.</item>
/// </list>
/// Envelope v2 wrap codes {1, 2} are allowed only in Development/Testing; elsewhere the set is empty (AM-9). E3-M2e-1 is dark: this
/// increment never enables the switch anywhere.
/// </summary>
public sealed class EnvelopeV2AcceptanceSwitch
{
    public const string ConfigurationKey = "Content:EnvelopeV2AcceptanceEnabled";

    /// <summary>The content acceptance floor that acceptance requires (CARV 3, the ledger rule version; AM-5).</summary>
    public const int RequiredFloor = 3;

    private static readonly IReadOnlySet<int> DevelopmentWrapCodes = new HashSet<int>(EnvelopeV2Rules.WrapAlgorithms.Keys);
    private static readonly IReadOnlySet<int> NoWrapCodes = new HashSet<int>();

    private readonly object gate = new();
    private int testingOverride = -1;
    private string? observedPolicyDigest;

    private EnvelopeV2AcceptanceSwitch(bool configuredEnabled, bool developmentOrTesting, bool testingOverrideAllowed, bool prerequisitesHold, string environmentClass, string policyDescription)
    {
        ConfiguredEnabled = configuredEnabled;
        IsDevelopmentOrTesting = developmentOrTesting;
        TestingOverrideAllowed = testingOverrideAllowed;
        StaticPrerequisitesHold = prerequisitesHold;
        EnvironmentClass = environmentClass;
        ContentAcceptancePolicyDescription = policyDescription;
    }

    /// <summary>
    /// The default when no switch is composed (unit tests, tools): off, never enableable, with the Development/Testing wrap codes so
    /// the dark E3-M2d rule behaviour of existing fixtures is unchanged. The Authority host always composes a real instance.
    /// </summary>
    public static EnvelopeV2AcceptanceSwitch DarkForTests { get; } = new(false, true, false, false, "DevelopmentOrTesting", "unconfigured");

    public bool ConfiguredEnabled { get; }

    public bool IsDevelopmentOrTesting { get; }

    public bool TestingOverrideAllowed { get; }

    /// <summary>The environment and content acceptance prerequisites that do not need the database (Development/Testing, Release B, RequiredFloor 3).</summary>
    public bool StaticPrerequisitesHold { get; }

    /// <summary><c>DevelopmentOrTesting</c> or <c>Other</c>; recorded in the policy event.</summary>
    public string EnvironmentClass { get; }

    public string ContentAcceptancePolicyDescription { get; }

    /// <summary>AM-9: the Envelope v2 wrap-algorithm codes this environment admits, for every v2 row (Prepare and Commit).</summary>
    public IReadOnlySet<int> AllowedWrapCodes => IsDevelopmentOrTesting ? DevelopmentWrapCodes : NoWrapCodes;

    /// <summary>What the operator asked for: the Testing override when one is set, otherwise the configuration.</summary>
    public bool Requested
    {
        get
        {
            lock (gate)
            {
                return testingOverride switch
                {
                    0 => false,
                    1 => true,
                    _ => ConfiguredEnabled,
                };
            }
        }
    }

    /// <summary>
    /// Whether New v2 groups are accepted now: requested, every static prerequisite holding, and this exact effective policy durably
    /// recorded. Fail closed: any missing element means off.
    /// </summary>
    public bool Enabled
    {
        get
        {
            lock (gate)
            {
                var requested = testingOverride switch { 0 => false, 1 => true, _ => ConfiguredEnabled };
                return requested && StaticPrerequisitesHold && observedPolicyDigest is not null
                    && string.Equals(observedPolicyDigest, PolicyDigestCore(requested), StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The policy this instance would record now (secret-free; OD-M2e-13).</summary>
    public EnvelopeAcceptancePolicyDescriptor Describe()
    {
        lock (gate)
        {
            var requested = testingOverride switch { 0 => false, 1 => true, _ => ConfiguredEnabled };
            return new EnvelopeAcceptancePolicyDescriptor(requested, ConfiguredEnabled, testingOverride switch { 0 => "Off", 1 => "On", _ => "None" }, EnvironmentClass,
                [.. AllowedWrapCodes.Order()], ContentAcceptancePolicyDescription, ContentAcceptanceRules.CurrentVersion, PolicyDigestCore(requested));
        }
    }

    /// <summary>Called only after the descriptor with this digest is durably on the platform journal (the observer).</summary>
    public void MarkObserved(string policyDigestHex)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyDigestHex);
        lock (gate)
        {
            observedPolicyDigest = policyDigestHex;
        }
    }

    /// <summary>Fail-closed parsing: only the literal <c>true</c> (case-insensitive, surrounding spaces ignored) enables.</summary>
    public static bool Parse(string? raw) => string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Startup policy (OD-M2e-07, AM-5). Throws (the host refuses to start) when acceptance is configured on outside Development/Testing,
    /// or without Release B and <see cref="RequiredFloor"/>. The ledger-migration prerequisite is checked by the Api startup guard.
    /// </summary>
    public static EnvelopeV2AcceptanceSwitch Create(string? configuredValue, string environmentName, ContentAcceptancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(policy);
        var enabled = Parse(configuredValue);
        var developmentOrTesting = environmentName is "Development" or "Testing";
        var floorHolds = policy.IsValid && policy.Phase == ContentAcceptancePhase.ReleaseB && policy.RequiredFloor == RequiredFloor;
        if (enabled && !developmentOrTesting)
        {
            throw new InvalidOperationException(ConfigurationKey + " must not be enabled outside Development/Testing (Staging and Production included): Envelope v2 acceptance is not approved there (E3-M2e).");
        }

        if (enabled && !floorHolds)
        {
            throw new InvalidOperationException(ConfigurationKey + " requires " + ContentAcceptancePolicy.PhaseKey + " = " + ContentAcceptancePolicy.ReleaseBName + " and "
                + ContentAcceptancePolicy.RequiredFloorKey + " = 3 (transactional floor enforcement); this instance has " + policy.Describe() + ".");
        }

        return new EnvelopeV2AcceptanceSwitch(enabled, developmentOrTesting, environmentName == "Testing", developmentOrTesting && floorHolds, developmentOrTesting ? "DevelopmentOrTesting" : "Other", policy.Describe());
    }

    /// <summary>
    /// Testing environment only (bounded, OD-M2e-07(d)): disabling is always allowed; enabling only when the static prerequisites hold.
    /// Null clears the override. The new effective policy is not accepted until the observer records it.
    /// </summary>
    public void SetTestingOverride(bool? enabled)
    {
        if (!TestingOverrideAllowed)
        {
            throw new InvalidOperationException("the Envelope v2 acceptance override is available only in the Testing environment");
        }

        if (enabled == true && !StaticPrerequisitesHold)
        {
            throw new InvalidOperationException("the Envelope v2 acceptance override cannot enable acceptance: Release B with RequiredFloor = 3 is required");
        }

        lock (gate)
        {
            testingOverride = enabled switch
            {
                null => -1,
                true => 1,
                false => 0,
            };
        }
    }

    private string PolicyDigestCore(bool requested)
    {
        var canonical = string.Join('|', "DynamicForms/EnvelopeAcceptancePolicy/v1", requested ? "on" : "off", ConfiguredEnabled ? "configured-on" : "configured-off",
            testingOverride.ToString(System.Globalization.CultureInfo.InvariantCulture), EnvironmentClass, string.Join(',', AllowedWrapCodes.Order()), ContentAcceptancePolicyDescription,
            ContentAcceptanceRules.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>The secret-free acceptance policy recorded on the platform journal (OD-M2e-13).</summary>
public sealed record EnvelopeAcceptancePolicyDescriptor(
    bool AcceptanceRequested,
    bool AcceptanceConfigured,
    string TestingOverride,
    string EnvironmentClass,
    IReadOnlyList<int> AllowedWrapCodes,
    string ContentAcceptancePolicy,
    int ContentAcceptanceRuleVersion,
    string PolicyDigestHex);
