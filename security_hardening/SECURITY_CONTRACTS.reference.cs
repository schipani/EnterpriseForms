// Reference-only C# interfaces for the Level 2 security simplification.
// This file is NOT compiled, integrated with actual DTOs, or production-tested.
// Implement within the independently governed Authority / application boundaries.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicForms.Security.Reference;

public enum VerifiedSearchMode
{
    BoundedAuthorityEnumeration,
    IndependentTrustedIndex
}

public enum EventSensitivity
{
    Ordinary,
    Sensitive,
    SecurityCritical
}

public sealed record AuditEventEnvelope(
    Guid AuditEventId,
    Guid TenantId,
    string OriginServiceIdentifier,
    string EventAction,
    Guid? ActorUserId,
    Guid OperationId,
    DateTimeOffset EventOccurredOnUtc,
    long? AuthorityRevision,
    long? ContentRevision,
    EventSensitivity EventSensitivity,
    IReadOnlyDictionary<string, string> RedactedMetadata);

public sealed record DurableAuditAcceptance(
    Guid AuditEventId,
    string ExternalIntakeIdentifier,
    DateTimeOffset AcceptedOnUtc);

// Invoked by the application; actual durable intake must be independently
// administered, not backed exclusively by the hostile Business PostgreSQL.
public interface IAuditEvidenceIntake
{
    Task<DurableAuditAcceptance> RecordRequiredEventAsync(
        AuditEventEnvelope auditEvent,
        CancellationToken cancellationToken);
}

public sealed record VerifiedSearchPredicate(
    Guid FieldDefinitionId,
    string ApprovedOperator,
    string TypedCanonicalValue);

public sealed record VerifiedSearchRequest(
    Guid TenantId,
    Guid ActorUserId,
    Guid FormDefinitionId,
    IReadOnlyList<VerifiedSearchPredicate> SearchPredicates,
    VerifiedSearchMode RequestedMode,
    int MaximumResults,
    string? PageCursor);

public sealed record VerifiedSearchResult(
    IReadOnlyList<Guid> AuthorizedFormRecordIds,
    VerifiedSearchMode UsedMode,
    long TrustedAuthorityRevision,
    long TrustedSearchContentRevision,
    string? NextPageCursor,
    bool ResultCompletenessVerified);

// Never expose an implicit 'best effort' result as completeness verified.
// SEARCH_VERIFICATION_LIMIT, SEARCH_INDEX_LAG and INTEGRITY_QUARANTINED
// are typed failure outcomes, not successful empty pages.
public interface ITrustedRecordSearchService
{
    Task<VerifiedSearchResult> SearchAsync(
        VerifiedSearchRequest request,
        CancellationToken cancellationToken);
}

public sealed record AuthenticationAssuranceRequest(
    Guid TenantId,
    Guid ActorUserId,
    string ProtectedAction,
    Guid? FormRecordId,
    DateTimeOffset VerificationRequiredOnUtc);

public sealed record AuthenticationAssuranceDecision(
    bool IsSatisfied,
    string VerifiedMethodClass,
    DateTimeOffset? AuthenticationTimeUtc,
    string SessionBindingIdentifier,
    long TrustedPolicyRevision);

public interface IAuthenticationAssuranceEvaluator
{
    Task<AuthenticationAssuranceDecision> VerifyFreshAssuranceAsync(
        AuthenticationAssuranceRequest request,
        CancellationToken cancellationToken);
}

public sealed record RecordIntegrityVerificationRequest(
    Guid TenantId,
    Guid FormRecordId,
    long ExpectedContentRevision,
    IReadOnlyList<Guid> PermittedFieldDefinitionIds,
    IReadOnlyList<Guid> PermittedFileAttachmentIds);

public sealed record RecordIntegrityVerificationResult(
    bool IsVerified,
    long AcceptedContentRevision,
    string AcceptedManifestRootHex,
    string VerificationFailureCode);

public interface IRecordIntegrityVerifier
{
    Task<RecordIntegrityVerificationResult> VerifyAuthorizedProjectionAsync(
        RecordIntegrityVerificationRequest request,
        CancellationToken cancellationToken);
}

// No SignArbitraryRow, WriteSpiceDbRelationships, or ChangeAuthorityHead
// operation is exposed to Business API callers.
