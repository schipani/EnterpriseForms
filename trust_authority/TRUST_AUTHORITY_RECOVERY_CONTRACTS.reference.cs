// G14 sample domain contracts: conceptual reference, not an authenticated service endpoint.
// Recovery interfaces are INTERNAL to the independently administered trust authority.
// Never grant these capabilities to the business API or normal tenant users.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicForms.TrustAuthority.Recovery;

public enum RecoveryOperationState
{
    Isolated,
    RestoringCandidate,
    VerifyingSecurity,
    VerifyingKeys,
    VerifyingContent,
    RebuildingProjection,
    ReadyForApproval,
    Active,
    Quarantined,
    Cancelled
}

public sealed record RecoveryOperationReference(
    Guid RecoveryOperationId,
    Guid RecoveryEpochId,
    Guid? OrganizationId);

public sealed record TrustedCheckpointReference(
    Guid RecoveryCheckpointId,
    Guid OrganizationId,
    long AcceptedAuthorizationRevision,
    long AcceptedContentRevision,
    string LedgerTransactionReference,
    string ImmutableSecurityArchiveReference);

public sealed record RecoveryEvidenceReport(
    RecoveryOperationReference Operation,
    TrustedCheckpointReference Checkpoint,
    bool AuthorizationReplayVerified,
    bool HistoricalKeyInventoryVerified,
    bool ContentManifestCompletenessVerified,
    bool SpiceDbProjectionVerified,
    bool CurrentAuthenticationFloorVerified,
    bool PrivilegeIsolationVerified,
    bool SearchAndAuditReconciled,
    IReadOnlyList<string> QuarantinedResourceIdentifiers,
    byte[] EvidenceDigest);

public sealed record RecoveryReleaseDecision(
    RecoveryOperationReference Operation,
    Guid ApproverAuthorityUserId,
    string ApprovalReason,
    byte[] EvidenceDigest);

public interface IRecoveryCoordinator
{
    Task<RecoveryOperationReference> OpenAsync(
        string incidentReference,
        Guid? organizationId,
        Guid verifiedOperatorId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task FenceOldEpochAsync(
        RecoveryOperationReference operation,
        CancellationToken cancellationToken);

    Task<TrustedCheckpointReference> SelectLatestVerifiedCheckpointAsync(
        RecoveryOperationReference operation,
        CancellationToken cancellationToken);

    Task<RecoveryEvidenceReport> VerifyCandidateAsync(
        RecoveryOperationReference operation,
        TrustedCheckpointReference checkpoint,
        CancellationToken cancellationToken);

    Task RegisterApprovalAsync(
        RecoveryReleaseDecision approval,
        CancellationToken cancellationToken);

    Task ActivateAsync(
        RecoveryOperationReference operation,
        byte[] expectedEvidenceDigest,
        CancellationToken cancellationToken);
}

public interface ISecurityEventReplayVerifier
{
    Task<bool> VerifyAndReplayThroughLatestAcceptedRevisionAsync(
        RecoveryOperationReference operation,
        TrustedCheckpointReference checkpoint,
        CancellationToken cancellationToken);
}

public interface IHistoricalKeyRecoveryVerifier
{
    // Provider-specific probes only. Never export raw keys or grant decrypt permission to an operator.
    Task<bool> VerifyRequiredKeyVersionsAsync(
        RecoveryOperationReference operation,
        CancellationToken cancellationToken);
}

public interface ICommittedContentRecoveryVerifier
{
    Task<IReadOnlyList<Guid>> FindMissingOrInvalidRecordContentsAsync(
        RecoveryOperationReference operation,
        CancellationToken cancellationToken);
}

public interface IAuthorizationProjectionRebuilder
{
    Task<bool> RebuildAndVerifyCurrentStateAsync(
        RecoveryOperationReference operation,
        CancellationToken cancellationToken);
}

// Required enforcement (not expressible by the contracts alone):
// - mTLS/workload identity and independently verified recovery-operator FIDO2 step-up.
// - two distinct independently authorized humans approve release;
// - protected egress and old workers fenced in an external authority epoch;
// - no request body may assert "passed=true" in lieu of independent checks;
// - monotonic accepted security/content heads and ledger/archive proof, no regression;
// - no public routes for these internal interfaces;
// - fail closed when an event payload or data/object/key version is missing;
// - all state transitions auditable with durable independent evidence.
