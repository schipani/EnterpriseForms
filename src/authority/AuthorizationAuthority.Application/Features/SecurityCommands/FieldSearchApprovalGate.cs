using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// OD-7: AC-6 approvals need fresh step-up assurance (PB-08), which is not implemented. Approvals are therefore
/// unavailable everywhere by default. The only exception is a development-only synthetic path, accepted solely when all
/// of these hold — so it cannot be switched on in staging or production by configuration alone:
/// <list type="bullet">
/// <item><c>FieldSearch:ApprovalStepUp</c> is <c>SyntheticDevelopment</c>;</item>
/// <item>the Authority host environment is Development or Testing (any other environment refuses to start with it set);</item>
/// <item>delegated users are verified by the synthetic TestIdentityProvider (never Entra), so a production identity can
/// never present it;</item>
/// <item>the approval carries the synthetic step-up marker and a governance reference.</item>
/// </list>
/// This is not PB-08 step-up and is never reported as such.
/// </summary>
public sealed class FieldSearchApprovalGate
{
    public const string ConfigurationKey = "FieldSearch:ApprovalStepUp";
    public const string SyntheticMode = "SyntheticDevelopment";
    public const string SyntheticStepUpEvidence = "synthetic-development-step-up";
    public const string UnavailableReasonCode = "PB08_STEP_UP_NOT_AVAILABLE";

    /// <summary>C-188 (D-H18): deployment setting for retained-field approvals; Disabled when absent. Anything but Enabled/Disabled refuses startup.</summary>
    public const string RetainedFieldApprovalsKey = "FieldSearch:RetainedFieldApprovals";
    public const string RetainedFieldApprovalsEnabledValue = "Enabled";
    public const string RetainedFieldApprovalsDisabledValue = "Disabled";

    private FieldSearchApprovalGate(bool operational, bool retainedFieldApprovalsEnabled = false)
    {
        ApprovalsOperational = operational;
        RetainedFieldApprovalsEnabled = retainedFieldApprovalsEnabled;
    }

    public static FieldSearchApprovalGate Unavailable { get; } = new(false);

    public bool ApprovalsOperational { get; }

    /// <summary>
    /// C-188 (D-H18): whether retained-field approvals are accepted and honoured. Off by default. While off, new retained approvals
    /// are refused, existing Active ones are not effective (no capability, no predicate authorization, open indexed pages invalidated,
    /// V1 snapshots issued before this process started invalidated for forms that hold one), a retained field that returns is
    /// invalidated instead of converted, and withdrawal, strengthening and policy invalidation keep working.
    /// </summary>
    public bool RetainedFieldApprovalsEnabled { get; }

    /// <summary>The same gate with the retained-field setting resolved (startup); an unknown value is a configuration failure.</summary>
    public FieldSearchApprovalGate WithRetainedFieldApprovals(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue) || string.Equals(configuredValue, RetainedFieldApprovalsDisabledValue, StringComparison.Ordinal))
        {
            return new FieldSearchApprovalGate(ApprovalsOperational, false);
        }

        if (!string.Equals(configuredValue, RetainedFieldApprovalsEnabledValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{RetainedFieldApprovalsKey} must be {RetainedFieldApprovalsDisabledValue} or {RetainedFieldApprovalsEnabledValue}. Refusing to start.");
        }

        return new FieldSearchApprovalGate(ApprovalsOperational, true);
    }

    public string UnavailableReason => ApprovalsOperational ? string.Empty : UnavailableReasonCode;

    public static FieldSearchApprovalGate Create(string? configuredMode, string environmentName, string? userAssertionProviderKind)
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

        return new FieldSearchApprovalGate(true);
    }

    /// <summary>Fails closed: without an operational path every approval is refused with STEP_UP_REQUIRED.</summary>
    public void RequireStepUp(string? evidence)
    {
        if (!ApprovalsOperational)
        {
            throw new AuthorityException(AuthorityErrorCodes.StepUpRequired, 403, false, "search approval needs step-up assurance (PB-08), which is not available in this environment");
        }

        if (!string.Equals(evidence, SyntheticStepUpEvidence, StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.StepUpRequired, 403, false, "the development step-up marker is missing");
        }
    }
}
