namespace EnterpriseForms.Application.Common.Authority;

// Phase C8 — AC-9 (Design R). Mirrors of AuthorizationAuthority.Contracts.V1 ReconcilerContracts, for the Worker's
// team-membership reconciler only. The business API never registers this port (D8-12, RC-26).

/// <summary>
/// Read-only outcome lookup under the separate reconciler credential. It has no submit, approve, replay or user-assertion
/// capability: the only operation is a lookup bound by (organization, idempotency key, allowlisted kind).
/// </summary>
public interface ITeamMembershipOutcomeAuthority
{
    /// <summary>False when this host has no reconciler credential; every lookup then fails closed without contacting the Authority.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Throws <see cref="ReconcilerLookupException"/> for every non-success; never returns a partial answer. The returned
    /// answer is still untrusted until <c>TeamMembershipOutcomeValidator</c> accepts it (package §1.3).
    /// </summary>
    Task<SecurityCommandOutcomeAnswer> LookupAsync(Guid organizationId, string idempotencyKey, string expectedCommandKind, Guid correlationId, CancellationToken cancellationToken);
}

public sealed record SecurityCommandOutcomeAnswer(
    bool Found,
    Guid CorrelationId,
    Guid OrganizationId,
    Guid? SecurityCommandId,
    string? CommandKind,
    string? CommandState,
    long? CommittedAuthorizationRevision,
    string? RejectionReasonCode,
    Guid? TeamId,
    Guid? MemberUserId,
    SecurityCommandOutcomeOrganizationAnswer? OrganizationState,
    SecurityCommandOutcomeMembershipAnswer? Membership);

public sealed record SecurityCommandOutcomeOrganizationAnswer(long AuthorizationRevision, long ProjectionAppliedRevision, string ProjectionGateState, bool IntegrityQuarantined);

public sealed record SecurityCommandOutcomeMembershipAnswer(long EvaluatedAtRevision, bool EffectiveMember, long EffectiveMembershipRevision, bool LaterChangePending);

public enum ReconcilerLookupFailure
{
    /// <summary>No reconciler credential in this host.</summary>
    NotConfigured,

    /// <summary>401/403: the credential is missing, rotated or revoked (RC-25). The reconciler marks itself Unavailable.</summary>
    CredentialRefused,

    /// <summary>429: honour <see cref="ReconcilerLookupException.RetryAfter"/>; consumes no error budget (RC-27).</summary>
    RateLimited,

    /// <summary>Transport failure, timeout or 5xx: back off and retry; nothing is projected (I-1).</summary>
    Unavailable,

    /// <summary>400/422 or an unreadable body: the request or the answer is malformed.</summary>
    Refused,
}

public sealed class ReconcilerLookupException(ReconcilerLookupFailure failure, string errorCode, TimeSpan? retryAfter = null) : Exception("reconciler lookup failed: " + errorCode)
{
    public ReconcilerLookupFailure Failure { get; } = failure;

    public string ErrorCode { get; } = errorCode;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
