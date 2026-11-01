using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.OrganizationOnboarding;

/// <summary>C8 onboarding configuration (section <c>OrganizationOnboarding</c>).</summary>
public sealed class OrganizationOnboardingOptions
{
    public const string SectionName = "OrganizationOnboarding";

    /// <summary>V1 issuer allow-list: initial owners must come from one of these issuers. Empty ⇒ onboarding unavailable.</summary>
    public List<string> ApprovedIdentityIssuers { get; set; } = [];

    /// <summary>
    /// D8-60: the legacy single-call <c>POST /internal/v1/operator/organizations</c> serves only the approved development seed and test
    /// harness fixtures. False (default) refuses it with <c>LEGACY_ORGANIZATION_BOOTSTRAP_DISABLED</c>; true is refused at startup
    /// outside Development/Testing.
    /// </summary>
    public bool LegacyOrganizationBootstrapEnabled { get; set; }

    /// <summary>D8-52: days a request may wait for its two approvals (1..30, default 7).</summary>
    public int PendingApprovalDays { get; set; } = 7;

    /// <summary>D8-52: hours between approval and initialization for the key custody step (1..168, default 72).</summary>
    public int KeyCustodyHours { get; set; } = 72;

    /// <summary>
    /// C-105 owner recovery: seconds between opening a case and the earliest execution (production target 72 hours = 259200).
    /// Development/Testing may accelerate it down to 10 seconds; outside them anything below 72 hours refuses to start.
    /// </summary>
    public int RecoveryWaitingPeriodSeconds { get; set; } = 259200;

    /// <summary>C-105: days a case stays executable after its waiting period ends (1..30, default 7).</summary>
    public int RecoveryCaseDays { get; set; } = 7;

    /// <summary>C-105: days during which owners recovered by the same case cannot add one another to teams or grant one another designer (1..90, default 14).</summary>
    public int RecoveredOwnerProbationDays { get; set; } = 14;
}

/// <summary>
/// Whether organization onboarding is available here. It needs the platform plane (Platform Operators) and an approved issuer list,
/// and exists only in Development/Testing: production requires verified control of each initial owner's identity (owner decision 9,
/// V3 proof of control through PB-08), which is not implemented, so a configured issuer list outside Development/Testing refuses to start.
/// </summary>
public sealed class OrganizationOnboardingPolicy
{
    public const int MinimumOwners = 2;
    public const int RecommendedOwners = 3;
    public const int MaximumOwners = 10;
    public const int RequiredApprovals = 2;

    public const int ProductionRecoveryWaitingPeriodSeconds = 259200;
    public const int MinimumDevelopmentRecoveryWaitingPeriodSeconds = 10;

    private OrganizationOnboardingPolicy(IReadOnlyList<string> issuers, bool legacy, TimeSpan pending, TimeSpan custody, TimeSpan recoveryWaiting, TimeSpan recoveryCase, TimeSpan probation)
    {
        RecoveryWaitingPeriod = recoveryWaiting;
        RecoveryCaseLifetime = recoveryCase;
        RecoveredOwnerProbation = probation;
        ApprovedIdentityIssuers = issuers;
        LegacyOrganizationBootstrapEnabled = legacy;
        PendingApprovalWindow = pending;
        KeyCustodyWindow = custody;
    }

    public IReadOnlyList<string> ApprovedIdentityIssuers { get; }

    public bool LegacyOrganizationBootstrapEnabled { get; }

    public TimeSpan PendingApprovalWindow { get; }

    public TimeSpan KeyCustodyWindow { get; }

    /// <summary>C-105: trusted-clock waiting period before a recovery case may be executed.</summary>
    public TimeSpan RecoveryWaitingPeriod { get; }

    /// <summary>C-105: how long a case stays executable after its waiting period.</summary>
    public TimeSpan RecoveryCaseLifetime { get; }

    /// <summary>C-105: the recovered-owner safeguard period (in addition to the permanent OB-F8 rule).</summary>
    public TimeSpan RecoveredOwnerProbation { get; }

    /// <summary>Onboarding and owner recovery need an approved issuer list, which only Development/Testing accepts (PB-08 outstanding).</summary>
    public bool Operational => ApprovedIdentityIssuers.Count > 0;

    public static OrganizationOnboardingPolicy Create(OrganizationOnboardingOptions? options, string environmentName)
    {
        options ??= new OrganizationOnboardingOptions();
        var development = environmentName is "Development" or "Testing";
        var issuers = options.ApprovedIdentityIssuers.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (!development && (issuers.Count > 0 || options.LegacyOrganizationBootstrapEnabled))
        {
            throw new InvalidOperationException($"{OrganizationOnboardingOptions.SectionName}: onboarding issuers and the legacy organization bootstrap are development-only; production onboarding needs verified owner identity control (PB-08). Refusing to start in {environmentName}.");
        }

        foreach (var issuer in issuers)
        {
            if (issuer.Length > 512 || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException($"{OrganizationOnboardingOptions.SectionName}:ApprovedIdentityIssuers must be absolute https URIs.");
            }
        }

        if (options.PendingApprovalDays is < 1 or > 30 || options.KeyCustodyHours is < 1 or > 168)
        {
            throw new InvalidOperationException($"{OrganizationOnboardingOptions.SectionName}: PendingApprovalDays must be 1..30 and KeyCustodyHours 1..168.");
        }

        var minimumWaiting = development ? MinimumDevelopmentRecoveryWaitingPeriodSeconds : ProductionRecoveryWaitingPeriodSeconds;
        if (options.RecoveryWaitingPeriodSeconds < minimumWaiting || options.RecoveryWaitingPeriodSeconds > 30 * 86400 || options.RecoveryCaseDays is < 1 or > 30 || options.RecoveredOwnerProbationDays is < 1 or > 90)
        {
            throw new InvalidOperationException($"{OrganizationOnboardingOptions.SectionName}: RecoveryWaitingPeriodSeconds must be {minimumWaiting}..2592000 in {environmentName}, RecoveryCaseDays 1..30 and RecoveredOwnerProbationDays 1..90.");
        }

        return new OrganizationOnboardingPolicy(issuers, options.LegacyOrganizationBootstrapEnabled, TimeSpan.FromDays(options.PendingApprovalDays), TimeSpan.FromHours(options.KeyCustodyHours),
            TimeSpan.FromSeconds(options.RecoveryWaitingPeriodSeconds), TimeSpan.FromDays(options.RecoveryCaseDays), TimeSpan.FromDays(options.RecoveredOwnerProbationDays));
    }

    public void RequireOperational()
    {
        if (!Operational)
        {
            throw new AuthorityException(AuthorityErrorCodes.OrganizationOnboardingUnavailable, 403, false, "organization onboarding is not available in this environment (no approved owner identity issuers; production needs verified owner identity control)");
        }
    }

    public void RequireLegacyBootstrap()
    {
        if (!LegacyOrganizationBootstrapEnabled)
        {
            throw new AuthorityException(AuthorityErrorCodes.LegacyOrganizationBootstrapDisabled, 403, false, "the legacy organization bootstrap is restricted to approved development fixtures; create organizations through Platform Console onboarding");
        }
    }
}
