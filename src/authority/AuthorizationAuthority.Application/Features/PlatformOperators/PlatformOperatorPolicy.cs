using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>C8 M2 Stage 1 configuration (section <c>PlatformOperators</c>).</summary>
public sealed class PlatformOperatorOptions
{
    public const string SectionName = "PlatformOperators";

    /// <summary><c>Unavailable</c> (default; the only mode outside Development/Testing) or <c>SyntheticDevelopment</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>D8-37: lifetime of a pending first approval, 1..72 hours (default 24), never beyond the request's expiry.</summary>
    public int PendingApprovalHours { get; set; } = 24;

    /// <summary>D8-37: upper bound of a piece of evidence's lifetime (exp − iat), 60..900 seconds (default 600).</summary>
    public int EvidenceMaxLifetimeSeconds { get; set; } = 600;

    /// <summary>Lifetime of a two-operator registry proposal, 1..72 hours (default 24).</summary>
    public int ProposalHours { get; set; } = 24;
}

/// <summary>
/// Whether the platform plane is operational here. <c>Unavailable</c> is the default and the only mode outside Development/
/// Testing: every platform and ceremony operation is refused <c>PLATFORM_OPERATORS_UNAVAILABLE</c> until PB-08 identity, PIM and
/// the independent review exist (D8-31, D8-45 restrictions). <c>SyntheticDevelopment</c> uses development-only operator keys;
/// every decision made with them is recorded as synthetic and is never two-human approval evidence.
/// </summary>
public sealed class PlatformOperatorPolicy
{
    public const string UnavailableMode = "Unavailable";
    public const string SyntheticMode = "SyntheticDevelopment";
    public const string SyntheticAssurance = "SyntheticDevelopment (development-only operator keys; not two-human approval evidence)";
    public const string UnavailableAssurance = "None (platform operator decisions are not available in this environment)";
    public const int RequiredMembershipApprovals = 2;

    private PlatformOperatorPolicy(string mode, TimeSpan pendingApproval, TimeSpan evidenceLifetime, TimeSpan proposal)
    {
        Mode = mode;
        PendingApprovalWindow = pendingApproval;
        EvidenceMaxLifetime = evidenceLifetime;
        ProposalLifetime = proposal;
    }

    public static PlatformOperatorPolicy Unavailable { get; } = new(UnavailableMode, TimeSpan.FromHours(24), TimeSpan.FromMinutes(10), TimeSpan.FromHours(24));

    public string Mode { get; }

    public bool Operational => Mode == SyntheticMode;

    public string Assurance => Operational ? SyntheticAssurance : UnavailableAssurance;

    public TimeSpan PendingApprovalWindow { get; }

    public TimeSpan EvidenceMaxLifetime { get; }

    public TimeSpan ProposalLifetime { get; }

    public static PlatformOperatorPolicy Create(PlatformOperatorOptions? options, string environmentName)
    {
        options ??= new PlatformOperatorOptions();
        if (string.IsNullOrWhiteSpace(options.Mode) || string.Equals(options.Mode, UnavailableMode, StringComparison.Ordinal))
        {
            return Unavailable;
        }

        if (!string.Equals(options.Mode, SyntheticMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{PlatformOperatorOptions.SectionName}:Mode must be {UnavailableMode} or {SyntheticMode}; production operator evidence (Entra transaction-bound step-up, PB-08) is not implemented. Refusing to start.");
        }

        if (environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"{PlatformOperatorOptions.SectionName}:Mode={SyntheticMode} is development-only and must not be set in the {environmentName} environment. Refusing to start.");
        }

        if (options.PendingApprovalHours is < 1 or > 72 || options.ProposalHours is < 1 or > 72)
        {
            throw new InvalidOperationException($"{PlatformOperatorOptions.SectionName}: PendingApprovalHours and ProposalHours must be 1..72.");
        }

        if (options.EvidenceMaxLifetimeSeconds is < 60 or > 900)
        {
            throw new InvalidOperationException($"{PlatformOperatorOptions.SectionName}:EvidenceMaxLifetimeSeconds must be 60..900.");
        }

        return new PlatformOperatorPolicy(SyntheticMode, TimeSpan.FromHours(options.PendingApprovalHours), TimeSpan.FromSeconds(options.EvidenceMaxLifetimeSeconds), TimeSpan.FromHours(options.ProposalHours));
    }

    public void RequireOperational()
    {
        if (!Operational)
        {
            throw new AuthorityException(AuthorityErrorCodes.PlatformOperatorsUnavailable, 403, false, "platform operator decisions are not available in this environment (production requires PB-08 identity assurance, PIM and an independent review)");
        }
    }
}
