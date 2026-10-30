using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>Isolated authority PostgreSQL store. Implemented only in AuthorizationAuthority.Infrastructure.</summary>
public interface IAuthorityStore
{
    /// <summary>Opens one database transaction. Everything performed through the scope commits atomically or not at all.</summary>
    Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Phase C8 AC-9: one REPEATABLE READ, READ ONLY transaction (a single snapshot, no locks). Anything staged through the
    /// scope fails at commit; callers never commit it.
    /// </summary>
    Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken);

    /// <summary>Organizations that have pending projection or archive work (used by background processors, outside a scope).</summary>
    Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken);
}

public interface IAuthorityScope : IAsyncDisposable
{
    /// <summary>SELECT ... FOR UPDATE on the organization state row: serializes every mutation of that organization (A6).</summary>
    Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddOrganization(OrganizationAuthorizationState state);

    Task<IdentityBinding?> FindIdentityBindingAsync(Guid organizationId, string issuerUri, string subjectIdentifier, CancellationToken cancellationToken);

    Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken);

    /// <summary>Owner message 7: every identity binding of the organization (read-only).</summary>
    Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken);

    void AddIdentityBinding(IdentityBinding binding);

    Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken);

    void AddResource(AuthorityResource resource);

    /// <summary>Active resources of the given types whose parent is the given entity (e.g. the protection requirements/releases of a form's fields).</summary>
    Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid organizationId, Guid parentResourceEntityId, IReadOnlyCollection<string> resourceTypeNames, CancellationToken cancellationToken);

    Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddProtectionPolicy(OrganizationProtectionPolicy policy);

    /// <summary>All currently active desired-state edges of the organization (normalized authoritative graph).</summary>
    Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddEdge(AuthorityRelationshipEdge edge);

    /// <summary>Phase C8 AC-9: every desired-state edge (active and removed) of one exact relationship, read-only.</summary>
    Task<List<AuthorityRelationshipEdge>> GetRelationshipEdgeHistoryAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, CancellationToken cancellationToken);

    /// <summary>Phase C8 AC-9: committed authorization revisions of the given commands of one organization (read-only; absent or uncommitted ⇒ null).</summary>
    Task<Dictionary<Guid, long?>> GetCommittedRevisionsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken);

    Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken);

    Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken);

    void AddCommand(SecurityCommand command);

    void AddOutboxEvent(ProjectionOutboxEvent outboxEvent);

    Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken);

    void AddJournalEntry(SecurityJournalEntry entry);

    Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken);

    /// <summary>The content-stream ContentHeadCommitted entry of an accepted operation (latest if ever repeated), by index — not by scanning revisions.</summary>
    Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

    Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken);

    Task<RecordContentHead?> FindContentHeadAsync(Guid organizationId, Guid formRecordId, bool lockForUpdate, CancellationToken cancellationToken);

    void AddContentHead(RecordContentHead head);

    Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken);

    Task<ContentChangeOperation?> FindOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken);

    void AddOperation(ContentChangeOperation operation);

    void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves);

    Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid contentChangeOperationId, CancellationToken cancellationToken);

    void AddManifestCommit(RecordManifestCommit commit);

    void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves);

    Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid recordManifestCommitId, CancellationToken cancellationToken);

    /// <summary>The accepted manifest commit of one earlier operation of a record (historic verification), or null.</summary>
    Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken);

    /// <summary>AC-1: accepted manifest commits of a record with a revision above <paramref name="afterRevision"/>, ascending, at most <paramref name="limit"/>. Read-only.</summary>
    Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken cancellationToken);

    /// <summary>AC-1: the content operations with the given ids (read-only; actor and reprotection attribution).</summary>
    Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken cancellationToken);

    void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint);

    Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken);

    void AddKeyRegistration(IntegrityKeyRegistration registration);

    /// <summary>Record IDs registered and active for the organization (authority-sourced discovery candidates).</summary>
    Task<List<Guid>> GetActiveRecordIdsAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Phase C7 AC-5: registered form_record resources of one form (registry parent = form) in the given lifecycle states, in id order.</summary>
    Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken);

    /// <summary>Registry rows of the given resources (any lifecycle state), for snapshot validation.</summary>
    Task<List<AuthorityResource>> GetResourcesAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken);

    /// <summary>Accepted content heads of the given records (read-only).</summary>
    Task<List<RecordContentHead>> GetContentHeadsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries);

    /// <summary>Tracked, so an invalidation can be recorded on it.</summary>
    Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken);

    /// <summary>Entries in catalogue order; <paramref name="limit"/> null = all.</summary>
    Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid searchCatalogueSnapshotId, int offset, int? limit, CancellationToken cancellationToken);

    /// <summary>G23: the session with this secret digest (tracked, so revocation/last-seen updates commit with the scope), or null.</summary>
    Task<AuthenticatedSession?> FindSessionByDigestAsync(byte[] sessionSecretDigest, CancellationToken cancellationToken);

    void AddSession(AuthenticatedSession session);

    /// <summary>Phase C7 M2 (OD-3): the highest registered published version of a form, or null (read-only).</summary>
    Task<PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    void AddPublishedFormVersion(PublishedFormVersion version);

    /// <summary>Phase C7 M3: the highest registered published version of every form of the organization (read-only).</summary>
    Task<List<PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Phase C7 M2 (AC-6): approvals of the organization (optionally one form), tracked so commands can change their state.</summary>
    Task<List<FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken);

    void AddFieldSearchApproval(FieldSearchApproval approval);

    /// <summary>Phase C7 M4: the entries of one snapshot for the given records only (read-only; uses the snapshot/record unique index).</summary>
    Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesForRecordsAsync(Guid searchCatalogueSnapshotId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    /// <summary>Phase C7 M4: registry rows of the given types and lifecycle states across the organization (read-only).</summary>
    Task<List<AuthorityResource>> GetResourcesByTypeAsync(Guid organizationId, IReadOnlyCollection<string> resourceTypeNames, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken);

    /// <summary>Phase C7 M4: number of accepted manifest commits (accepted revisions) per record, for the given records (read-only; records without any are absent).</summary>
    Task<Dictionary<Guid, int>> CountManifestCommitsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    /// <summary>Phase C7 M4: census results held for one protection decision and the given records (any operation; read-only).</summary>
    Task<List<ProtectionCensusResult>> GetProtectionCensusResultsAsync(Guid organizationId, Guid scopingSecurityCommandId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    void AddProtectionCensusResult(ProtectionCensusResult result);

    /// <summary>Phase C7 M4: one held census (tracked, so its verification time can be refreshed), or null.</summary>
    Task<ProtectionCensusResult?> FindProtectionCensusResultAsync(Guid scopingSecurityCommandId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken);

    void AddProtectionAttestation(ProtectionReconciliationAttestation attestation);

    /// <summary>Phase C7 M4: the most recent attestation of each of the given protection decisions (read-only).</summary>
    Task<List<ProtectionReconciliationAttestation>> GetLatestProtectionAttestationsAsync(Guid organizationId, IReadOnlyCollection<Guid> scopingSecurityCommandIds, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}
