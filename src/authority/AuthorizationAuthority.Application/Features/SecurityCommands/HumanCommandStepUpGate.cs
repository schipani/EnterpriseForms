using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// G27: Human security commands (CreateShare, RevokeShare, TransferOwnership) require fresh step-up assurance (PB-08),
/// which is not implemented. These commands are therefore refused everywhere by default. The only exception is a
/// development-only synthetic path, accepted solely when all of these hold — so it cannot be switched on in staging
/// or production by configuration alone:
/// <list type="bullet">
/// <item><c>HumanCommand:StepUp</c> is <c>SyntheticDevelopment</c>;</item>
/// <item>the Authority host environment is Development or Testing (any other environment refuses to start with it set);</item>
/// <item>delegated users are verified by the synthetic TestIdentityProvider (never Entra), so a production identity can
/// never present it;</item>
/// <item>the command carries the synthetic step-up marker.</item>
/// </list>
/// This is not PB-08 step-up and is never reported as such.
/// </summary>
/// <remarks>
/// The pattern mirrors <see cref="FieldSearchApprovalGate"/> and shares the same synthetic step-up evidence marker.
/// CreateShare, RevokeShare (mandatory), and TransferOwnership are the human commands that require step-up. UpdateShareScope
/// is not yet implemented but will require step-up when WP4 adds it.
/// </remarks>
public sealed class HumanCommandStepUpGate
{
    public const string ConfigurationKey = "HumanCommand:StepUp";
    public const string SyntheticMode = "SyntheticDevelopment";
    public const string SyntheticStepUpEvidence = FieldSearchApprovalGate.SyntheticStepUpEvidence;
    public const string UnavailableReasonCode = "PB08_STEP_UP_NOT_AVAILABLE";

    private HumanCommandStepUpGate(bool operational)
    {
        StepUpOperational = operational;
    }

    public static HumanCommandStepUpGate Unavailable { get; } = new(false);

    public bool StepUpOperational { get; }

    public string UnavailableReason => StepUpOperational ? string.Empty : UnavailableReasonCode;

    public static HumanCommandStepUpGate Create(string? configuredMode, string environmentName, string? userAssertionProviderKind)
    {
        if (string.IsNullOrWhiteSpace(configuredMode) || string.Equals(configuredMode, "Unavailable", StringComparison.Ordinal))
        {
            return Unavailable;
        }

        if (!string.Equals(configuredMode, SyntheticMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{ConfigurationKey} must be Unavailable or {SyntheticMode}. Refusing to start.");
        }

        if (environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"{ConfigurationKey}={SyntheticMode} is a development-only path and must not be set in the {environmentName} environment. Refusing to start.");
        }

        if (!string.Equals(userAssertionProviderKind, "TestIdentityProvider", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{ConfigurationKey}={SyntheticMode} requires the synthetic TestIdentityProvider for delegated users. Refusing to start.");
        }

        return new HumanCommandStepUpGate(true);
    }

    /// <summary>
    /// Fails closed: without an operational path every human command is refused with PB08_STEP_UP_NOT_AVAILABLE;
    /// when the test gate is operational but evidence is missing/invalid, STEP_UP_REQUIRED is returned.
    /// </summary>
    public void RequireStepUp(string? evidence)
    {
        if (!StepUpOperational)
        {
            throw new AuthorityException(UnavailableReasonCode, 403, false, "human command needs step-up assurance (PB-08), which is not available in this environment");
        }

        if (!string.Equals(evidence, SyntheticStepUpEvidence, StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.StepUpRequired, 403, false, "the development step-up marker is missing");
        }
    }
}
