using System.Text.Json;

namespace EnterpriseForms.Application.Common.Authority;

// Port for the independent Authorization Authority (trust_authority/trust-authority.openapi.yaml, Stage A revision).
// The only implementation is an authenticated internal HTTP adapter in Infrastructure. It never derives decisions from
// business PostgreSQL owner/share/team rows and exposes no generic signing method. DTO shapes mirror the private
// contract; the authority re-validates identity, tenant and every revision independently.

public enum ProtectedResourceKind
{
    FormDefinition,
    FormRecord,
    FieldItem,
    DocumentItem,
    Team,
    Organization,
}

public enum ProtectedAction
{
    Discover,
    Read,
    Edit,
    Download,
    Replace,
    Delegate,
    ManageAccess,
    TransferOwnership,
    DesignForm,
    CreateRecord,
    ManageOrganization,

    /// <summary>Organization security administration (system administrators); Amendment A1 governance routes accept it alongside ManageOrganization.</summary>
    AdministerSecurity,
}

public enum SecurityCommandKind
{
    RegisterTeam,
    AddTeamMember,
    RemoveTeamMember,
    RegisterFormDefinition,
    RegisterRecord,
    TransferOwnership,
    CreateShare,
    UpdateShareScope,
    RevokeShare,
    GrantSystemRole,
    RevokeSystemRole,
    UpdateMandatoryPolicy,
    PublishFormSecurityRevision,
    UnlinkChildRecord,
    UpdateFormUsers,
    ReclassifyField,
    RegisterPublishedFormVersion,
    ApproveFieldSearch,
    WithdrawFieldSearch,
    UpdateFieldSearchers,
}

public enum SecurityCommandStatus
{
    Pending,
    CommittedAwaitingProjection,
    Effective,
    Rejected,
    FailedReconciliation,
}

public enum ManifestItemKind
{
    RecordHeader = 1,
    RepeatedGroup = 2,
    FieldValue = 3,
    FieldSelection = 4,
    Attachment = 5,
    AttachmentLifecycle = 6,
}

public sealed record ResourceReference(ProtectedResourceKind ResourceKind, Guid ResourceId);

public sealed record PermissionCheckRequest(Guid OrganizationId, ResourceReference Resource, ProtectedAction RequestedAction, Guid? UnlockReference = null, long? MinimumContentRevision = null);

public sealed record PermissionDecision(bool Allowed, string? DenialReasonCode, long AuthorizationRevision, long ProjectionRevision, long? ContentRevision, Guid CorrelationId);

public sealed record SecurityCommandRequest(Guid OrganizationId, SecurityCommandKind CommandKind, ResourceReference Resource, long ExpectedAuthorizationRevision, JsonElement CommandPayload, string? ChangeReason = null, Guid? ApprovalReference = null);

public sealed record SecurityCommandResult(Guid CommandId, SecurityCommandStatus CommandStatus, long AuthorizationRevision, long? ProjectionRevision, string ProjectionGateState, string? RejectionReasonCode, Guid CorrelationId);

public sealed record CandidateContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64);

/// <summary>ReprotectionCommandId + CurrentItems: a workload reprotection operation (no delegated user) under an effective PublishFormSecurityRevision command; null for user operations.</summary>
public sealed record PrepareContentChangeRequest(Guid OrganizationId, Guid FormRecordId, Guid OperationId, long ExpectedAuthorizationRevision, long ExpectedContentRevision, string ExpectedManifestRootBase64, IReadOnlyList<CandidateContentItem> CandidateItems, Guid? ReprotectionCommandId = null, IReadOnlyList<StagedContentItem>? CurrentItems = null);

public sealed record SignedCandidateItem(ManifestItemKind ItemKind, Guid ItemId, string RowSignatureBase64, string CanonicalPayloadDigestBase64, string MutationKind);

public sealed record PreparedContentOperation(Guid OperationId, long CandidateContentRevision, int SignatureKeyVersion, string CandidateManifestRootBase64, IReadOnlyList<SignedCandidateItem> SignedItems, string OperationStatus, Guid CorrelationId);

public sealed record StagedContentItem(ManifestItemKind ItemKind, Guid ItemId, string CanonicalBytesBase64, string RowSignatureBase64);

public sealed record CommitContentChangeRequest(Guid OrganizationId, Guid FormRecordId, IReadOnlyList<StagedContentItem> StagedItems);

public sealed record AcceptedContentRevision(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string CommittedManifestRootBase64, long AuthorizationRevision, int IntegrityKeyVersion, int CurrentSigningKeyVersion, string ArchiveState, Guid CorrelationId);

/// <summary>AC-1 (Phase C6): one accepted revision as the Authority records it (see the Authority contract of the same name).</summary>
public sealed record ContentHistoryEntry(long ContentRevision, Guid CommittedOperationId, DateTimeOffset AcceptedOn, string ActorKind, Guid? ActorAuthorityUserId, Guid? ReprotectionCommandId, long ManifestLeafCount, int RowSignatureKeyVersion, bool VerifiableNow, bool IsCurrent);

public sealed record ContentHistoryPage(Guid OrganizationId, Guid FormRecordId, long CurrentContentRevision, IReadOnlyList<ContentHistoryEntry> Entries, long? NextAfterRevision, long AuthorizationRevision, Guid CorrelationId);

/// <summary>HistoricCommittedOperationId: verify an earlier accepted manifest of the record (historic document versions; full readers only).</summary>
public sealed record VerifyRecordProjectionRequest(Guid OrganizationId, Guid FormRecordId, ProtectedAction RequestedAction, IReadOnlyList<StagedContentItem> ObservedItems, Guid? HistoricCommittedOperationId = null);

public sealed record VerifiedProjectionItem(ManifestItemKind ItemKind, Guid ItemId);

/// <summary>Phase C5 (D7, additive): readable/editable subsets of a partial projection (advisory) and the Authority's contributor-write switch (D10).</summary>
public sealed record VerifyRecordProjectionResult(bool Verified, string? FailureReasonCode, long CommittedContentRevision, Guid? CommittedOperationId, long AuthorizationRevision, IReadOnlyList<VerifiedProjectionItem> AuthorizedItems, bool IsCompleteProjection, Guid CorrelationId,
    IReadOnlyList<VerifiedProjectionItem>? ReadableItems = null, IReadOnlyList<VerifiedProjectionItem>? EditableItems = null, bool ContributorWritesEnabled = false);

public sealed record FormAccessSubjectState(string SubjectKind, Guid SubjectId);

/// <summary>Authoritative designers and create-record principals of a form, read from the authority's desired state behind its gate.</summary>
public sealed record FormAccessState(Guid FormDefinitionId, long AuthorizationRevision, long ProjectionRevision, IReadOnlyList<FormAccessSubjectState> Designers, IReadOnlyList<FormAccessSubjectState> FormUsers);

public sealed record RecordDiscoveryPage(IReadOnlyList<Guid> FormRecordIds, long AuthorizationRevision, string? ContinuationToken);

// Phase C7 M1 — AC-5 record catalogue and OD-6 batch verification (mirrors of the Authority's internal contract).

public sealed record SearchCatalogueRequest(Guid OrganizationId, Guid FormDefinitionId, int MaximumEntries);

/// <remarks>Phase C7 M3: Authority-held registration and head-change times (sorting and display of verified results).</remarks>
public sealed record CatalogueEntry(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, bool ContentHeadQuarantined, bool ResourceQuarantined,
    DateTimeOffset? RecordRegisteredOn = null, DateTimeOffset? ContentHeadModifiedOn = null, string? ProtectionCensusOutcome = null);

/// <summary>The Authority's expected population of one form for the caller, with accepted heads, at one snapshot.</summary>
public sealed record SearchCatalogue(Guid SearchCatalogueSnapshotId, Guid OrganizationId, Guid FormDefinitionId, long AuthorizationRevision, long ProjectionRevision, long ContentIntegrityRevision,
    IReadOnlyList<CatalogueEntry> Entries, DateTimeOffset SnapshotExpiresOn, Guid CorrelationId);

public sealed record CatalogueSnapshotValidation(Guid SearchCatalogueSnapshotId, bool Valid, string? InvalidationReason, long CurrentAuthorizationRevision, long CurrentContentIntegrityRevision, Guid CorrelationId);

public sealed record BatchVerificationItem(Guid FormRecordId, Guid ExpectedCommittedOperationId, IReadOnlyList<StagedContentItem> ObservedItems);

public sealed record VerifyRecordProjectionsRequest(Guid OrganizationId, IReadOnlyList<BatchVerificationItem> Records);

public sealed record BatchVerificationResult(Guid FormRecordId, bool Verified, string? FailureReasonCode, long CommittedContentRevision, Guid? CommittedOperationId, bool IsCompleteProjection, IReadOnlyList<VerifiedProjectionItem> ReadableItems);

public sealed record VerifyRecordProjectionsResponse(IReadOnlyList<BatchVerificationResult> Results, long AuthorizationRevision, Guid CorrelationId);

/// <summary>Stable authority error surfaced to callers; the business API maps it to a Problem Details response without leaking internals.</summary>
public sealed class AuthorityClientException(string errorCode, int httpStatusCode, bool retryable, string? detail = null) : Exception(detail ?? errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int HttpStatusCode { get; } = httpStatusCode;

    public bool Retryable { get; } = retryable;
}

/// <summary>The verified end-user context forwarded to the authority: the raw assertion is forwarded unchanged; no claims are re-asserted by the business API.</summary>
public sealed record DelegatedUserContext(string IssuerUri, string SubjectIdentifier, Guid OrganizationId, string RawAssertion);

public interface IAuthorizationAuthorityClient
{
    Task<PermissionDecision> CheckPermissionAsync(DelegatedUserContext user, PermissionCheckRequest request, CancellationToken cancellationToken);

    Task<FormAccessState> GetFormAccessAsync(DelegatedUserContext user, Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(DelegatedUserContext user, IReadOnlyList<PermissionCheckRequest> requests, CancellationToken cancellationToken);

    Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(DelegatedUserContext user, Guid organizationId, int pageSize, string? continuationToken, CancellationToken cancellationToken);

    Task<SecurityCommandResult> SubmitSecurityCommandAsync(DelegatedUserContext user, SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<SecurityCommandResult> GetSecurityCommandStatusAsync(DelegatedUserContext user, Guid commandId, CancellationToken cancellationToken);

    Task<PreparedContentOperation> PrepareContentChangeAsync(DelegatedUserContext user, PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> CommitContentChangeAsync(DelegatedUserContext user, Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken);

    Task<VerifyRecordProjectionResult> VerifyRecordProjectionAsync(DelegatedUserContext user, VerifyRecordProjectionRequest request, CancellationToken cancellationToken);

    /// <summary>Phase C7 AC-5: the user-scoped catalogue of one form (Authority snapshot).</summary>
    Task<SearchCatalogue> IssueSearchCatalogueAsync(DelegatedUserContext user, SearchCatalogueRequest request, CancellationToken cancellationToken);

    /// <summary>Phase C7 AC-5: validates the caller's own snapshot against current Authority state.</summary>
    Task<CatalogueSnapshotValidation> ValidateSearchCatalogueAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, CancellationToken cancellationToken);

    /// <summary>Phase C7 OD-6: verifies 1..50 records, each independently against its expected accepted head.</summary>
    Task<VerifyRecordProjectionsResponse> VerifyRecordProjectionsAsync(DelegatedUserContext user, VerifyRecordProjectionsRequest request, CancellationToken cancellationToken);

    /// <summary>AC-1 (Phase C6): accepted content revisions of a record, ascending from <paramref name="afterRevision"/>; full readers only (404 otherwise).</summary>
    Task<ContentHistoryPage> GetContentHistoryAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, long? afterRevision, int limit, CancellationToken cancellationToken);

    // Workload reprotection operations (B-28): no delegated user; the authority authorizes them from the effective PublishFormSecurityRevision command only.
    Task<AcceptedContentRevision> GetCommittedContentRevisionForReprotectionAsync(Guid organizationId, Guid formRecordId, Guid reprotectionCommandId, CancellationToken cancellationToken);

    Task<PreparedContentOperation> PrepareReprotectionAsync(PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<AcceptedContentRevision> CommitReprotectionAsync(Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken);
}

/// <summary>G23: the Authority's verdict on a browser session (reason for the trusted business API only, never the browser).</summary>
public sealed record BrowserSessionState(bool Valid, string? InvalidReason, DateTimeOffset? ExpiresOn, Guid CorrelationId);

/// <summary>
/// G23: server-side browser session state, held by the Authority (outside BusinessDb). Only SHA-256 digests of session
/// secrets are sent; the secret itself stays in the encrypted session cookie. Separate from
/// <see cref="IAuthorizationAuthorityClient"/> because only the BFF session composition uses it.
/// </summary>
public interface IBrowserSessionStateAuthority
{
    /// <summary>Registers a new session and revokes <paramref name="replacedSessionDigestBase64"/> (the session the browser presented), if any.</summary>
    Task<BrowserSessionState> CreateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, DateTimeOffset absoluteExpiresOn, string? replacedSessionDigestBase64, CancellationToken cancellationToken);

    Task<BrowserSessionState> ValidateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<BrowserSessionState> RevokeSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken);
}

// ---- Phase C7 M2 (AC-6): Authority field search capabilities (mirrors AuthorizationAuthority.Contracts.V1.FieldSearchContracts).

public sealed record FieldSearchCapabilitiesQuery(Guid OrganizationId, Guid FormDefinitionId);

public sealed record FieldSearchOptionSnapshot(Guid FieldOptionId, string OptionLabel);

public sealed record AuthoritySearchableField(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, IReadOnlyList<string> Operators, IReadOnlyList<FieldSearchOptionSnapshot> Options, int ApprovedFormVersionNumber);

public sealed record AuthorityFieldSearchStatus(Guid FieldDefinitionId, string FieldLabel, string FieldDataType, string FieldClassification, string ProtectionMode, string Eligibility,
    string ApprovalState, string? InvalidationReason, IReadOnlyList<string> ApprovedOperators, IReadOnlyList<string> PermittedOperators);

public sealed record AuthorityFieldSearchConfiguration(bool CanApprove, bool ApprovalOperational, string ApprovalUnavailableReason, int? RegisteredFormVersionNumber, IReadOnlyList<AuthorityFieldSearchStatus> Fields,
    IReadOnlyList<FormAccessSubjectState>? FieldSearchers = null);

public sealed record AuthorityFieldSearchCapabilities(Guid FormDefinitionId, bool PlaintextSearchPermitted, long SearchPolicyRevision, long AuthorizationRevision,
    IReadOnlyList<AuthoritySearchableField> Fields, AuthorityFieldSearchConfiguration? Configuration, Guid CorrelationId);

/// <summary>Phase C7 M3 (OD-8): evidence of one execution — field ids, operators, counts, outcome (never values or record ids).</summary>
public sealed record SearchExecutionEvidence(IReadOnlyList<Guid> FieldDefinitionIds, IReadOnlyList<string> Operators, int ConditionCount, string Outcome, int? EvaluatedRecordCount, int? MatchedRecordCount, long ElapsedMilliseconds);

public sealed record FieldSearchAdministrationQuery(Guid OrganizationId);

public sealed record AuthorityFieldSearchAdministrationForm(Guid FormDefinitionId, string? FormTitle, int RegisteredFormVersionNumber, DateTimeOffset RegisteredOn, int FieldCount, int EligibleFieldCount, int ActiveApprovalCount, int InvalidatedApprovalCount);

public sealed record AuthorityFieldSearchAdministration(bool PlaintextSearchPermitted, bool ApprovalOperational, string ApprovalUnavailableReason, IReadOnlyList<AuthorityFieldSearchAdministrationForm> Forms, long AuthorizationRevision, Guid CorrelationId);

/// <summary>Phase C7 M2/M3: the Authority's field search capabilities, administration listing and execution evidence (narrow port; only the Search feature uses it).</summary>
public interface IFieldSearchPolicyAuthority
{
    Task<AuthorityFieldSearchCapabilities> GetFieldSearchCapabilitiesAsync(DelegatedUserContext user, FieldSearchCapabilitiesQuery query, CancellationToken cancellationToken);

    /// <summary>Security administrators only (<c>administer_security</c>): forms with a registered published version and their search-approval counts.</summary>
    Task<AuthorityFieldSearchAdministration> GetFieldSearchAdministrationAsync(DelegatedUserContext user, FieldSearchAdministrationQuery query, CancellationToken cancellationToken);

    /// <summary>Journals one execution over the caller's own catalogue snapshot on the Authority audit stream.</summary>
    Task RecordSearchExecutionAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, SearchExecutionEvidence evidence, CancellationToken cancellationToken);
}

// ---- Owner message 7: Security Administrators (mirrors AuthorizationAuthority.Contracts.V1 SecurityAdministratorRoster) ----

public sealed record SecurityAdministratorRosterQuery(Guid OrganizationId);

public sealed record AuthoritySecurityAdministrator(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier, DateTimeOffset AppointedOn, Guid AppointedBySecurityCommandId);

public sealed record AuthoritySecurityAdministratorCandidate(Guid AuthorityUserId, string IssuerUri, string SubjectIdentifier);

public sealed record AuthoritySecurityAdministratorRoster(bool CanManage, bool AppointmentOperational, string AppointmentUnavailableReason, IReadOnlyList<AuthoritySecurityAdministrator> Administrators,
    IReadOnlyList<AuthoritySecurityAdministratorCandidate>? Candidates, long AuthorizationRevision, Guid CorrelationId);

/// <summary>Owner message 7: the Authority's Security Administrator roster (narrow port; used by Organizations, and read-only by SecurityOperations to list and validate incident investigators).</summary>
public interface ISecurityAdministratorAuthority
{
    Task<AuthoritySecurityAdministratorRoster> GetSecurityAdministratorsAsync(DelegatedUserContext user, SecurityAdministratorRosterQuery query, CancellationToken cancellationToken);
}

// ---- C7 M4 incident correction: isolated test-stack identity (Testing environment only) ----

public sealed record AuthorityStackIdentity(string EnvironmentName, string StackIdentity);

/// <summary>The Authority's configured test-stack identity (Testing only; anything else fails). Used solely by the Testing-only identity endpoint.</summary>
public interface ITestStackIdentityAuthority
{
    Task<AuthorityStackIdentity> GetTestStackIdentityAsync(CancellationToken cancellationToken);
}
