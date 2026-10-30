namespace EnterpriseForms.Domain.Features.Teams;

/// <summary>
/// Phase C8 (C8-M1-01, Design R): one durable team-membership request to the Authorization Authority. Recorded before the
/// command is submitted and completed by the Worker's reconciler from a validated AC-9 answer. Non-authoritative (D8-18):
/// it decides <em>when</em> the Authority is asked, never <em>what</em> is projected. Identity columns are immutable to the
/// runtime role (column grants + schema-owned trigger); there is no DELETE.
/// </summary>
public sealed class AuthorityCommandOperation
{
    public Guid AuthorityCommandOperationId { get; set; }

    public Guid TenantId { get; set; }

    public Guid InitiatedByUserId { get; set; }

    /// <summary><see cref="TeamMembershipCommandKinds"/>.</summary>
    public string CommandKindName { get; set; } = string.Empty;

    public Guid TeamId { get; set; }

    /// <summary>The person named by the request (no FK: an unknown person is a legitimate, Authority-rejected request).</summary>
    public Guid MemberUserId { get; set; }

    /// <summary>The exact key sent to the Authority (<c>team-add:</c> or <c>team-remove:</c> + the browser key).</summary>
    public string AuthorityIdempotencyKey { get; set; } = string.Empty;

    public Guid RequestCorrelationIdentifier { get; set; }

    public DateTimeOffset OperationCreatedOn { get; set; }

    /// <summary>NULL → value only.</summary>
    public Guid? AuthorityCommandId { get; set; }

    /// <summary>NULL → value only.</summary>
    public long? CommittedAuthorizationRevision { get; set; }

    public string OperationStateName { get; set; } = AuthorityCommandOperationStates.AwaitingCommit;

    public string? OperationOutcomeCode { get; set; }

    public string? RejectionReasonCode { get; set; }

    public long? EvaluatedProjectionRevision { get; set; }

    public long? ProjectedMembershipRevision { get; set; }

    public bool? ProjectedMembershipPresent { get; set; }

    public bool? SupersededByLaterRevision { get; set; }

    public DateTimeOffset NextAttemptOn { get; set; }

    public int AttemptCount { get; set; }

    public int ConsecutiveErrorCount { get; set; }

    public string? LeaseOwnerName { get; set; }

    public DateTimeOffset? LeaseExpiresOn { get; set; }

    /// <summary>First AC-9 "not found" answer; <c>NotCommitted</c> needs a second one at least one in-flight window later.</summary>
    public DateTimeOffset? FirstNotFoundOn { get; set; }

    public DateTimeOffset? NeedsAttentionFlaggedOn { get; set; }

    public string? LastReconciliationErrorCode { get; set; }

    /// <summary>After <c>NotCommitted</c>: the daily late-commit sweep runs until this time.</summary>
    public DateTimeOffset? LateCommitSweepUntil { get; set; }

    public DateTimeOffset? LateCommitDetectedOn { get; set; }

    public DateTimeOffset? OperationReviewedOn { get; set; }

    public Guid? OperationReviewedByUserId { get; set; }

    public DateTimeOffset? OperationCompletedOn { get; set; }

    public DateTimeOffset OperationModifiedOn { get; set; }

    public bool IsTerminal => AuthorityCommandOperationStates.IsTerminal(OperationStateName);
}

public static class TeamMembershipCommandKinds
{
    public const string AddTeamMember = "AddTeamMember";
    public const string RemoveTeamMember = "RemoveTeamMember";
    public const string AddKeyPrefix = "team-add:";
    public const string RemoveKeyPrefix = "team-remove:";

    public static string KeyPrefix(string commandKindName) => commandKindName switch
    {
        AddTeamMember => AddKeyPrefix,
        RemoveTeamMember => RemoveKeyPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(commandKindName), commandKindName, "not a reconcilable team membership command"),
    };

    /// <summary>Package §1.3: the row's key prefix must match its kind before any lookup is sent.</summary>
    public static bool PrefixMatches(string commandKindName, string authorityIdempotencyKey) => commandKindName switch
    {
        AddTeamMember => authorityIdempotencyKey.StartsWith(AddKeyPrefix, StringComparison.Ordinal),
        RemoveTeamMember => authorityIdempotencyKey.StartsWith(RemoveKeyPrefix, StringComparison.Ordinal),
        _ => false,
    };
}

public static class AuthorityCommandOperationStates
{
    /// <summary>Recorded before submit (T1); the Authority may or may not hold the command.</summary>
    public const string AwaitingCommit = "AwaitingCommit";

    /// <summary>Committed (command id and revision known), not yet projected.</summary>
    public const string AwaitingEffective = "AwaitingEffective";

    /// <summary>The organization is integrity-quarantined: nothing is projected until the Authority clears it.</summary>
    public const string AwaitingQuarantineClearance = "AwaitingQuarantineClearance";

    /// <summary>Validation failure or local anomaly; re-opened only by an audited operator "check again".</summary>
    public const string ReconciliationError = "ReconciliationError";

    public const string Projected = "Projected";
    public const string Rejected = "Rejected";
    public const string AuthorityFailed = "AuthorityFailed";
    public const string NotCommitted = "NotCommitted";

    public static readonly string[] All = [AwaitingCommit, AwaitingEffective, AwaitingQuarantineClearance, ReconciliationError, Projected, Rejected, AuthorityFailed, NotCommitted];

    public static bool IsTerminal(string state) => state is Projected or Rejected or AuthorityFailed or NotCommitted;

    /// <summary>States the reconciler leases. <see cref="ReconciliationError"/> waits for an operator.</summary>
    public static readonly string[] Due = [AwaitingCommit, AwaitingEffective, AwaitingQuarantineClearance];
}

public static class AuthorityCommandOperationOutcomes
{
    public const string MembershipPresentProjected = "MembershipPresentProjected";
    public const string MembershipAbsentProjected = "MembershipAbsentProjected";
    public const string ProjectionUnchanged = "ProjectionUnchanged";
    public const string DriftCorrected = "DriftCorrected";
}

public static class TeamMembershipReconciliationErrors
{
    public const string OperationMismatch = "OPERATION_MISMATCH";
    public const string InvalidAuthorityResponse = "INVALID_AUTHORITY_RESPONSE";
    public const string LocalWatermarkAhead = "LOCAL_WATERMARK_AHEAD";
    public const string LateCommitDetected = "LATE_COMMIT_DETECTED";
    public const string CommandNotFound = "COMMITTED_COMMAND_NOT_FOUND";
    public const string TeamProjectionMissing = "TEAM_PROJECTION_MISSING";
}

/// <summary>
/// Phase C8 (C8-M1-02, R-2): the per-(tenant, team, member) high-water mark of the roster projection. Written only by the
/// reconciler, together with the <c>team_memberships</c> change and the operation completion, in one transaction (D8-21).
/// A schema-owned trigger refuses any update that lowers <see cref="ProjectedMembershipRevision"/> (I-3).
/// </summary>
public sealed class TeamMembershipProjectionState
{
    public Guid TeamMembershipProjectionStateId { get; set; }

    public Guid TenantId { get; set; }

    public Guid TeamId { get; set; }

    public Guid MemberUserId { get; set; }

    public bool ProjectedMembershipPresent { get; set; }

    /// <summary>The Authority's effective membership revision of the pair last projected (0 = never projected).</summary>
    public long ProjectedMembershipRevision { get; set; }

    public Guid? LastAuthorityCommandOperationId { get; set; }

    public DateTimeOffset ProjectionStateModifiedOn { get; set; }
}
