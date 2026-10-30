namespace EnterpriseForms.Application.Features.Teams.MembershipReconciliation;

/// <summary>
/// D8-16 timing parameters (package §6.3), section <c>TeamMembershipReconciliation</c>. Every value is validated at startup;
/// out of bounds ⇒ the host refuses to start. The Testing environment alone may shorten the windows below the normal lower
/// bounds (fault-injection pattern); any other environment refuses such values.
/// </summary>
public sealed class TeamMembershipReconciliationOptions
{
    public const string SectionName = "TeamMembershipReconciliation";

    /// <summary>A command may still be in flight this long after the operation was recorded; NotCommitted needs two not-found answers this far apart.</summary>
    public int InFlightWindowSeconds { get; set; } = 300;

    public int BackoffSeconds { get; set; } = 5;

    public int BackoffCapSeconds { get; set; } = 300;

    public int NeedsAttentionSeconds { get; set; } = 1800;

    public int ConsecutiveErrorBudget { get; set; } = 20;

    public int LateCommitSweepDays { get; set; } = 7;

    /// <summary>Interval of the late-commit sweep ("daily").</summary>
    public int LateCommitSweepIntervalSeconds { get; set; } = 86400;

    public int RetentionDays { get; set; } = 400;

    public int LeaseSeconds { get; set; } = 60;

    /// <summary>How long the business API waits for the Worker to project a command it just saw Effective (0 = return at once).</summary>
    public int ApiProjectionWaitSeconds { get; set; } = 5;

    public TimeSpan InFlightWindow => TimeSpan.FromSeconds(InFlightWindowSeconds);

    public TimeSpan NeedsAttention => TimeSpan.FromSeconds(NeedsAttentionSeconds);

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    /// <summary>Exponential backoff for the given consecutive attempt (1-based), capped.</summary>
    public TimeSpan Backoff(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var seconds = Math.Min((double)BackoffCapSeconds, BackoffSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    public void Validate(string environmentName, int authorityClientTimeoutSeconds)
    {
        var testing = string.Equals(environmentName, "Testing", StringComparison.Ordinal);
        var errors = new List<string>();
        void Require(bool condition, string message)
        {
            if (!condition)
            {
                errors.Add(message);
            }
        }

        Require(InFlightWindowSeconds <= 3600, "InFlightWindowSeconds ≤ 3600");
        Require(testing ? InFlightWindowSeconds >= 1 : InFlightWindowSeconds >= 3 * Math.Max(1, authorityClientTimeoutSeconds), "InFlightWindowSeconds ≥ 3 × the Authority client timeout");
        Require(testing ? BackoffSeconds >= 1 : BackoffSeconds >= 5, "BackoffSeconds ≥ 5");
        Require(BackoffSeconds <= BackoffCapSeconds, "BackoffSeconds ≤ BackoffCapSeconds");
        Require(BackoffCapSeconds <= 1800 && (testing ? BackoffCapSeconds >= 1 : BackoffCapSeconds >= 30), "BackoffCapSeconds within 30…1800");
        Require(NeedsAttentionSeconds <= 86400 && (testing ? NeedsAttentionSeconds >= 1 : NeedsAttentionSeconds >= 300), "NeedsAttentionSeconds within 300…86400");
        Require(ConsecutiveErrorBudget is >= 5 and <= 100, "ConsecutiveErrorBudget within 5…100");
        Require(LateCommitSweepDays is >= 1 and <= 30, "LateCommitSweepDays within 1…30");
        Require(LateCommitSweepIntervalSeconds <= 86400 && (testing ? LateCommitSweepIntervalSeconds >= 1 : LateCommitSweepIntervalSeconds >= 3600), "LateCommitSweepIntervalSeconds within 3600…86400");
        Require(RetentionDays >= 90, "RetentionDays ≥ 90");
        Require(LeaseSeconds is >= 10 and <= 600, "LeaseSeconds within 10…600");
        Require(ApiProjectionWaitSeconds is >= 0 and <= 15, "ApiProjectionWaitSeconds within 0…15");
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{SectionName} is out of bounds{(testing ? string.Empty : " (shortened windows are permitted only in Testing)")}: " + string.Join("; ", errors));
        }
    }
}
