namespace AuthorizationAuthority.Contracts.V1;

// Phase C8 — AC-9 (Design R, PHASE_C8_AC9_SECURITY_REVIEW_PACKAGE.md §1): read-only security-command outcome lookup for the
// business Worker's team-membership reconciler. Internal contract only; never part of the browser-facing business OpenAPI.

/// <summary>
/// One lookup, bound to exactly one command by (organization, idempotency key) and an allowlisted kind. There is no
/// parameter for a team, a user or a revision range: the pair answered is the command's own.
/// </summary>
public sealed record SecurityCommandOutcomeLookupRequest(Guid OrganizationId, string IdempotencyKey, SecurityCommandKind ExpectedCommandKind);

/// <summary>
/// <c>Found = false</c> is the one uniform answer for an unknown organization, an unknown key and a kind mismatch; no other
/// field is then populated. <see cref="Membership"/> is null when the command is rejected or the organization is
/// integrity-quarantined (status only, package §4.6).
/// </summary>
public sealed record SecurityCommandOutcome(
    bool Found,
    Guid CorrelationId,
    Guid OrganizationId,
    Guid? SecurityCommandId,
    SecurityCommandKind? CommandKind,
    CommandStatus? CommandState,
    long? CommittedAuthorizationRevision,
    string? RejectionReasonCode,
    Guid? TeamId,
    Guid? MemberUserId,
    SecurityCommandOutcomeOrganizationState? OrganizationState,
    SecurityCommandOutcomeMembership? Membership);

public sealed record SecurityCommandOutcomeOrganizationState(long AuthorizationRevision, long ProjectionAppliedRevision, ProjectionGateState ProjectionGateState, bool IntegrityQuarantined);

/// <summary>
/// The Authority's effective membership of the command's own (team, member) pair at <see cref="EvaluatedAtRevision"/>
/// (= the organization's projection-applied revision P). <see cref="EffectiveMembershipRevision"/> is the largest create or
/// remove revision of the pair at or below P (0 = never a member); <see cref="LaterChangePending"/> reports a change above P.
/// </summary>
public sealed record SecurityCommandOutcomeMembership(long EvaluatedAtRevision, bool EffectiveMember, long EffectiveMembershipRevision, bool LaterChangePending);
