namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// Phase C5 rollout switch (decision D10): whether share editors (contributors/managers) may commit field-scoped content
/// changes. Resolved once from Authority deployment configuration — never from an organization, form, user or API value.
/// Off by default everywhere; only the literal <c>true</c> enables it; a production-designated Authority refuses to start
/// with it enabled. Enforced at prepare and at commit. A Testing-environment-only override exists for the disposable
/// integration stack (operator plane, operator credential), so tests can prove both states.
/// </summary>
public sealed class ShareContributorWriteSwitch
{
    public const string ConfigurationKey = "Content:ShareContributorWritesEnabled";

    private readonly bool configuredEnabled;
    private readonly bool testingOverrideAllowed;
    private volatile int testingOverride = -1;

    public ShareContributorWriteSwitch(bool configuredEnabled, bool testingOverrideAllowed)
    {
        this.configuredEnabled = configuredEnabled;
        this.testingOverrideAllowed = testingOverrideAllowed;
    }

    /// <summary>The effective state: the Testing override when one is set, otherwise the deployment configuration.</summary>
    public bool Enabled => testingOverride switch
    {
        0 => false,
        1 => true,
        _ => configuredEnabled,
    };

    public bool ConfiguredEnabled => configuredEnabled;

    public bool TestingOverrideAllowed => testingOverrideAllowed;

    /// <summary>Fail-closed parsing: absent, empty, malformed or any value other than <c>true</c> (case-insensitive) is disabled.</summary>
    public static bool Parse(string? raw) => string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Startup policy: the production-designated environment may not enable contributor writes until a reviewed change removes this guard (C-34 condition 8).</summary>
    public static ShareContributorWriteSwitch Create(string? configuredValue, bool isProductionEnvironment, bool isTestingEnvironment)
    {
        var enabled = Parse(configuredValue);
        if (enabled && isProductionEnvironment)
        {
            throw new InvalidOperationException(ConfigurationKey + " must not be enabled in Production: field-scoped contributor writes await the independent Level 2 security review (Phase C5, C-34).");
        }

        return new ShareContributorWriteSwitch(enabled, isTestingEnvironment);
    }

    /// <summary>Testing environment only: null clears the override and restores the configured value.</summary>
    public void SetTestingOverride(bool? enabled)
    {
        if (!testingOverrideAllowed)
        {
            throw new InvalidOperationException("the contributor-write override is available only in the Testing environment");
        }

        testingOverride = enabled switch
        {
            null => -1,
            true => 1,
            false => 0,
        };
    }
}
