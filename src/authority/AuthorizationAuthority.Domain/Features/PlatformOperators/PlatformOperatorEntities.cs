namespace AuthorizationAuthority.Domain.Features.PlatformOperators;

/// <summary>
/// C8 M2 Stage 1 (D8-34): a registered Platform Operator. Platform-scoped and Authority-owned: never an organization identity
/// binding, never a SpiceDB edge, never derived from an organization role. One row per verified human identity; an identity is
/// never registered twice and a removed one is never reused. Development/Testing operators carry a synthetic P-256 approval key
/// (public half only); production operators would carry Entra evidence (not implemented, PB-08).
/// </summary>
public sealed class PlatformOperator
{
    public Guid PlatformOperatorId { get; set; }

    public string OperatorIssuerUri { get; set; } = string.Empty;

    public string OperatorSubjectIdentifier { get; set; } = string.Empty;

    /// <summary>Display only, never a key.</summary>
    public string OperatorDisplayName { get; set; } = string.Empty;

    /// <summary>One of <see cref="PlatformOperatorStates"/>.</summary>
    public string OperatorStatusName { get; set; } = PlatformOperatorStates.Active;

    /// <summary>One of <see cref="PlatformOperatorEvidenceKinds"/>.</summary>
    public string OperatorEvidenceKindName { get; set; } = PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws;

    public string? SyntheticApprovalKeyIdentifier { get; set; }

    /// <summary>SubjectPublicKeyInfo (DER) of the operator's synthetic P-256 approval key; the Authority never holds the private half.</summary>
    public byte[]? SyntheticApprovalPublicKey { get; set; }

    public DateTimeOffset OperatorProvisionedOn { get; set; }

    /// <summary>Null when registered by the recovery ceremony.</summary>
    public Guid? ProvisionedByPlatformOperatorId { get; set; }

    public DateTimeOffset OperatorStatusChangedOn { get; set; }

    public string OperatorStatusReasonText { get; set; } = string.Empty;

    /// <summary>Incremented on every status change; statements bind it so a decision prepared on an older reading is refused.</summary>
    public long OperatorRowRevision { get; set; } = 1;

    /// <summary>Platform journal revision of the event that last changed this row.</summary>
    public long LastPlatformJournalRevision { get; set; }
}

/// <summary>The first half of a two-operator registry change (provision, reinstate, remove). Single use; expires after 24 hours.</summary>
public sealed class PlatformOperatorChangeProposal
{
    public Guid PlatformOperatorChangeProposalId { get; set; }

    /// <summary>One of <see cref="PlatformOperatorChanges"/> (Provision, Reinstate, Remove).</summary>
    public string ProposedChangeName { get; set; } = string.Empty;

    /// <summary>For Provision the id the new operator will receive (the proposal id).</summary>
    public Guid TargetPlatformOperatorId { get; set; }

    public string TargetIssuerUri { get; set; } = string.Empty;

    public string TargetSubjectIdentifier { get; set; } = string.Empty;

    public string TargetDisplayName { get; set; } = string.Empty;

    public string TargetEvidenceKindName { get; set; } = string.Empty;

    public string? TargetSyntheticKeyIdentifier { get; set; }

    public byte[]? TargetSyntheticPublicKey { get; set; }

    /// <summary>The target's row revision the proposal was made against (0 for Provision).</summary>
    public long TargetRowRevision { get; set; }

    public Guid ProposedByPlatformOperatorId { get; set; }

    public string ProposalJustificationText { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical change statement both operators sign.</summary>
    public byte[] ProposalStatementSha256 { get; set; } = [];

    public DateTimeOffset ProposalCreatedOn { get; set; }

    public DateTimeOffset ProposalExpiresOn { get; set; }

    /// <summary>Pending or Consumed (expiry is a time comparison, never a stored transition).</summary>
    public string ProposalStateName { get; set; } = PlatformProposalStates.Pending;

    public Guid? ConfirmedByPlatformOperatorId { get; set; }

    public DateTimeOffset? ProposalConsumedOn { get; set; }
}

/// <summary>
/// C8 M2 Stage 2 (D8-36…D8-38): one operator's approval of one membership request. Evidence, not authorization: it grants
/// nothing and is never projected. At most one is Pending per request; the second valid approval by a different operator
/// creates the membership and consumes both. Closed rows never change again.
/// </summary>
public sealed class PlatformOperatorApproval
{
    public Guid PlatformOperatorApprovalId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid OrganizationMembershipRequestId { get; set; }

    public Guid PlatformOperatorId { get; set; }

    public byte[] ApprovalStatementSha256 { get; set; } = [];

    public int ApprovalStatementVersion { get; set; }

    public string ApprovalJustificationText { get; set; } = string.Empty;

    public string ApprovalEvidenceKindName { get; set; } = string.Empty;

    public byte[] ApprovalEvidenceSha256 { get; set; } = [];

    public DateTimeOffset ApprovalRecordedOn { get; set; }

    /// <summary>Never later than the request's expiry.</summary>
    public DateTimeOffset ApprovalValidUntil { get; set; }

    /// <summary>One of <see cref="PlatformApprovalStates"/>.</summary>
    public string ApprovalStateName { get; set; } = PlatformApprovalStates.Pending;

    public DateTimeOffset? ApprovalClosedOn { get; set; }

    /// <summary>One of <see cref="PlatformApprovalClosureReasons"/> once closed.</summary>
    public string? ApprovalClosureReasonName { get; set; }

    public Guid? ConsumedBySecurityCommandId { get; set; }
}

/// <summary>Single-use register of operator evidence (and ceremony operation ids): an identifier or digest is accepted once.</summary>
public sealed class PlatformOperatorEvidenceUse
{
    public Guid PlatformOperatorEvidenceUseId { get; set; }

    /// <summary>The evidence's own identifier (the JWS <c>jti</c>), or <c>operation:{id}</c> for a ceremony.</summary>
    public string EvidenceUseIdentifier { get; set; } = string.Empty;

    public byte[] EvidenceUseSha256 { get; set; } = [];

    /// <summary>Null for the recovery ceremony (custodian approvals, not an operator).</summary>
    public Guid? PlatformOperatorId { get; set; }

    public string EvidenceDecisionName { get; set; } = string.Empty;

    public DateTimeOffset EvidenceUsedOn { get; set; }
}

/// <summary>
/// The single head row of the platform journal (D8-44). Locked (SELECT … FOR UPDATE) by every platform mutation, so platform
/// events are strictly ordered; it holds the revision and digest the next event chains to.
/// </summary>
public sealed class PlatformJournalHead
{
    /// <summary>The one row's fixed id.</summary>
    public static readonly Guid SingletonId = new("01990000-0000-7000-8000-00000000c8a2");

    public Guid PlatformJournalHeadId { get; set; } = SingletonId;

    public long PlatformJournalRevision { get; set; }

    public byte[]? LatestPlatformJournalDigest { get; set; }

    public bool PlatformQuarantineEnabled { get; set; }

    public string? PlatformQuarantineReasonCode { get; set; }

    public DateTimeOffset HeadModifiedOn { get; set; }
}

/// <summary>Hash-chained, independently archived platform event. The body is the full envelope (replayable state delta); the digest chains to the previous entry.</summary>
public sealed class PlatformJournalEntry
{
    public Guid PlatformJournalEntryId { get; set; }

    public long PlatformJournalRevision { get; set; }

    public string JournalEventKind { get; set; } = string.Empty;

    public string JournalEventBodyJson { get; set; } = string.Empty;

    public byte[] JournalEventDigest { get; set; } = [];

    public byte[]? JournalPreviousDigest { get; set; }

    /// <summary>Pending, Anchored or VerificationFailed (same states as the organization journal).</summary>
    public string ArchiveDeliveryState { get; set; } = "Pending";

    public string? ArchiveReceiptReference { get; set; }

    public byte[]? ArchiveReceiptDigest { get; set; }

    public int ArchiveAttemptCount { get; set; }

    public string? LastArchiveErrorCode { get; set; }

    public DateTimeOffset JournalCreatedOn { get; set; }

    public DateTimeOffset? JournalArchivedOn { get; set; }
}

public static class PlatformOperatorStates
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Removed = "Removed";
}

public static class PlatformOperatorEvidenceKinds
{
    /// <summary>Development/Testing only: ES256 JWS signed by the operator's own synthetic key. Never two-human approval evidence.</summary>
    public const string SyntheticDevelopmentJws = "SyntheticDevelopmentJws";

    /// <summary>Production (not implemented; PB-08): Entra transaction-bound FIDO2 step-up. Always refused today.</summary>
    public const string EntraTransactionStepUp = "EntraTransactionStepUp";
}

public static class PlatformOperatorChanges
{
    public const string Provision = "Provision";
    public const string Reinstate = "Reinstate";
    public const string Remove = "Remove";

    public static readonly IReadOnlyList<string> TwoOperatorChanges = [Provision, Reinstate, Remove];
}

public static class PlatformProposalStates
{
    public const string Pending = "Pending";
    public const string Consumed = "Consumed";
}

public static class PlatformApprovalStates
{
    public const string Pending = "Pending";
    public const string Consumed = "Consumed";
    public const string Withdrawn = "Withdrawn";
    public const string Invalidated = "Invalidated";
}

public static class PlatformApprovalClosureReasons
{
    public const string ConsumedByMembership = "ConsumedByMembership";
    public const string WithdrawnByOperator = "WithdrawnByOperator";
    public const string RequestCancelled = "RequestCancelled";
    public const string RequestReplaced = "RequestReplaced";
    public const string RequestRejected = "RequestRejected";
    public const string RequestExpired = "RequestExpired";
    public const string RequestChanged = "RequestChanged";
    public const string RequestDecidedElsewhere = "RequestDecidedElsewhere";
    public const string PendingApprovalExpired = "PendingApprovalExpired";
    public const string OperatorSuspended = "OperatorSuspended";
    public const string OperatorRemoved = "OperatorRemoved";
    public const string OperatorIneligible = "OperatorIneligible";
}

/// <summary>What a piece of evidence decides; bound into the evidence so an approval can never be replayed as a rejection or vice versa.</summary>
public static class PlatformDecisions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Withdraw = "withdraw";
    public const string Propose = "propose";
    public const string Confirm = "confirm";
    public const string Suspend = "suspend";
    public const string View = "view";
    public const string Ceremony = "ceremony";
}
