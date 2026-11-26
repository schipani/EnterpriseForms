using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using AuthorizationAuthority.Domain.Features.Sharing;

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

    /// <summary>C8 M4 S1: every registered organization (the startup marker transition and the periodic integrity check, outside a scope).</summary>
    Task<IReadOnlyList<Guid>> GetAllOrganizationIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// C-149 (R-55): every registered organization with its three committed journal revisions, in one read-only statement outside
    /// a scope (no row lock). A background processor compares them with the revisions it last saw settled, to find organizations
    /// whose state may have moved; the markers are never trusted as the state itself.
    /// </summary>
    Task<IReadOnlyList<OrganizationChangeMarker>> GetOrganizationChangeMarkersAsync(CancellationToken cancellationToken);
}

/// <summary>
/// C-149 (R-55): an organization's committed authorization, content-integrity and audit journal revisions. Every security command,
/// content commit and audited administrative change (including Tier 1 attestations, reconciliations, rebuilds and Tier 2
/// rebuilds and quarantines) advances one of them in its own transaction.
/// </summary>
public sealed record OrganizationChangeMarker(Guid OrganizationId, long AuthorizationRevision, long ContentIntegrityRevision, long AuditJournalRevision);

/// <summary>E3-M2f-3b (closure IXP-6): one capability-refusal evidence journal row as the evidence lookup reads it (no other column).</summary>
public sealed record CapabilityRefusalEvidenceRow(Guid SecurityJournalEntryId, long JournalStreamRevision, string JournalEventBodyJson, byte[] JournalEventDigest);

public interface IAuthorityScope : IAsyncDisposable
{
    /// <summary>IX-1: the Authority-owned search index, in this scope's transaction.</summary>
    ISearchIndexStore SearchIndex { get; }

    /// <summary>IX-5a: the Tier 2 access projection (schema authority_search_access), in this scope's transaction.</summary>
    ISearchAccessStore SearchAccess { get; }

    /// <summary>SELECT ... FOR UPDATE on the organization state row: serializes every mutation of that organization (A6).</summary>
    Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// RB-CARV-1 (contract §22): the content write fence of this transaction. Release A: no-op. Release B: takes a ROW EXCLUSIVE
    /// lock on the content operations table (held to the end of the transaction, so no floor DDL can commit until this transaction
    /// ends), then verifies in this transaction that the required floor and the E3-M2b-1 fence are installed exactly as defined;
    /// anything else, including an unreadable catalog or an invalid policy, throws
    /// <see cref="AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced"/> (503) and raises an operational alert. Called at the
    /// entry of every content mutation (Prepare, Commit, record registration); the store additionally applies it before writing
    /// any content-acceptance entity, so a path that forgets the call still cannot write.
    /// </summary>
    Task RequireContentAcceptanceFloorAsync(CancellationToken cancellationToken);

    Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddOrganization(OrganizationAuthorizationState state);

    Task<IdentityBinding?> FindIdentityBindingAsync(Guid organizationId, string issuerUri, string subjectIdentifier, CancellationToken cancellationToken);

    Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken);

    /// <summary>Owner message 7: every identity binding of the organization (read-only).</summary>
    Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken);

    void AddIdentityBinding(IdentityBinding binding);

    /// <summary>C8 M2 (AC-7.2): the binding tracked for update inside the organization lock.</summary>
    Task<IdentityBinding?> FindIdentityBindingForUpdateAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken);

    /// <summary>C8 M2 (AC-7.2): sessions of one binding that are not revoked (tracked, to revoke them with the disabling command).</summary>
    Task<List<AuthenticatedSession>> GetUnrevokedSessionsAsync(Guid identityBindingId, CancellationToken cancellationToken);

    Task<OrganizationRoleChangeProposal?> FindRoleChangeProposalAsync(Guid organizationId, Guid proposalId, CancellationToken cancellationToken);

    Task<List<OrganizationRoleChangeProposal>> GetRoleChangeProposalsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken);

    void AddRoleChangeProposal(OrganizationRoleChangeProposal proposal);

    Task<OrganizationMembershipRequest?> FindMembershipRequestAsync(Guid organizationId, Guid requestId, CancellationToken cancellationToken);

    Task<List<OrganizationMembershipRequest>> GetMembershipRequestsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken);

    void AddMembershipRequest(OrganizationMembershipRequest request);

    Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken);

    void AddResource(AuthorityResource resource);

    /// <summary>Active resources of the given types whose parent is the given entity (e.g. the protection requirements/releases of a form's fields).</summary>
    Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid organizationId, Guid parentResourceEntityId, IReadOnlyCollection<string> resourceTypeNames, CancellationToken cancellationToken);

    // C10 M1-WP1: share-specific metadata stored alongside the generic AuthorityResource.

    /// <summary>C10 M1-WP1: finds the share metadata by organization and share id (unique key).</summary>
    Task<ShareResourceMetadata?> FindShareMetadataAsync(Guid organizationId, Guid recordShareId, CancellationToken cancellationToken);

    /// <summary>C10 M1-WP1: adds share metadata for a new share (must be unique per organization/share).</summary>
    void AddShareMetadata(ShareResourceMetadata metadata);

    /// <summary>C10 M1-WP5: finds all share metadata records where parent_record_share_id matches the given parent share id.</summary>
    Task<List<ShareResourceMetadata>> FindShareMetadataByParentAsync(Guid organizationId, Guid parentRecordShareId, CancellationToken cancellationToken);

    /// <summary>C10 M1-WP2: the metadata of the organization's Active shares that carry an expiry (access-time expiry overlay).</summary>
    Task<List<ShareResourceMetadata>> GetActiveShareMetadataWithExpiryAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>C10 M1-WP2: the active role edges of the given shares and the active <c>shared_via</c> edges whose subject is one of them.</summary>
    Task<List<AuthorityRelationshipEdge>> GetActiveShareEdgesAsync(Guid organizationId, IReadOnlyCollection<Guid> recordShareIds, CancellationToken cancellationToken);

    /// <summary>C10 M1-WP2: the active <c>team#member</c> edges of the given teams.</summary>
    Task<List<AuthorityRelationshipEdge>> GetActiveTeamMemberEdgesAsync(Guid organizationId, IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken);

    /// <summary>
    /// C10 M1-WP9 (WP-0 Section 3.5): every edge, active or removed, that ever gave the record an owner or a share: the record's
    /// <c>owner</c> and <c>shared_via</c> edges, and the role and item edges of every share that was ever <c>shared_via</c> the record.
    /// </summary>
    Task<List<AuthorityRelationshipEdge>> GetRecordSecurityEdgeHistoryAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("record security edge history is not available from this store");

    /// <summary>C10 D6: the committed (not Rejected) commands that targeted the record, newest first, at most <paramref name="limit"/>.</summary>
    Task<List<SecurityCommand>> GetRecordTargetedCommandsAsync(Guid organizationId, Guid formRecordId, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("record-targeted commands are not available from this store");

    /// <summary>C10 D6: which of the given commands have an entry on the organization's authorization journal stream.</summary>
    Task<HashSet<Guid>> GetAuthorizationJournalCommandIdsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("journal command lookup is not available from this store");

    Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddProtectionPolicy(OrganizationProtectionPolicy policy);

    /// <summary>All currently active desired-state edges of the organization (normalized authoritative graph).</summary>
    Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>R-53: the permanent identity bindings of the given item ids, in every organization.</summary>
    Task<List<ContentItemIdentity>> FindItemIdentitiesAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken);

    /// <summary>R-53: inserts identity bindings (never updated or deleted; the unique item id refuses a second binding).</summary>
    void AddItemIdentities(IEnumerable<ContentItemIdentity> identities);

    /// <summary>R-53: the given item ids that appear in any accepted manifest of any organization (identities predating the bindings).</summary>
    Task<List<Guid>> FindAcceptedItemIdsAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken);

    /// <summary>E3-M2e DK-4 / P-4: the ledger bindings of the given data-key identifiers, in every organization and operation state.</summary>
    Task<List<ContentDataKeyBinding>> FindDataKeyBindingsAsync(IReadOnlyCollection<Guid> dataKeyIdentifiers, CancellationToken cancellationToken);

    /// <summary>E3-M2e DK-6: every ledger binding whose binding operation is <paramref name="contentChangeOperationId"/>.</summary>
    Task<List<ContentDataKeyBinding>> GetDataKeyBindingsForOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken);

    /// <summary>
    /// E3-M2e DK-5: inserts ledger bindings with the operation that introduces them, in this transaction (never updated or deleted).
    /// The global unique identifier refuses a second binding; the store reports it as <c>422 DATA_KEY_IDENTIFIER_REUSED</c> and the
    /// whole transaction rolls back.
    /// </summary>
    void AddDataKeyBindings(IEnumerable<ContentDataKeyBinding> bindings);

    /// <summary>
    /// E3-M2e L-13: in this transaction, verifies that the ledger's unique identifier index and its P-1 binding guard and P-2
    /// immutability triggers exist exactly as created, holding a lock that keeps them from being dropped or disabled until the
    /// transaction ends. Called before any binding insert and before P-4 consults the ledger. Throws (fail closed) otherwise.
    /// </summary>
    Task RequireDataKeyLedgerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// E3-M2f-2 AM-F1 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §5): whether the organization has any data-key binding, in any state, in this
    /// transaction. Exactly <c>EXISTS (SELECT 1 … WHERE organization_id = @organization)</c>; returns a boolean only, materializes no binding
    /// column. Called solely by <c>EnvelopeCapabilityFloor</c> under the organization lock; never by a read, verification, search or history
    /// path for any other purpose. A failure throws (the caller refuses).
    /// </summary>
    Task<bool> OrganizationHasDataKeyBindingsAsync(Guid organizationId, CancellationToken cancellationToken);

    // ---- E3-M2f-3b capability-refusal evidence (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §3.2, §5.3, §5A). Called solely by the
    // evidence recorder, in its own transaction T2, never in a refused transaction. None of them reads the data-key ledger. ----

    /// <summary>T2-3: bounds this transaction's lock waits (<c>SET LOCAL lock_timeout</c>); called first, before the organization lock.</summary>
    Task SetCapabilityEvidenceLockTimeoutAsync(CancellationToken cancellationToken);

    /// <summary>
    /// T2-4 / IXP-7: after the organization lock, bounds this transaction's statements (<c>SET LOCAL statement_timeout</c>) and returns the
    /// authoritative time <c>t_db</c>: the AuthorityDb <c>clock_timestamp()</c> read in its own statement after the lock (never the
    /// transaction start time, never the instance clock).
    /// </summary>
    Task<DateTimeOffset> StartCapabilityEvidenceLookupAsync(CancellationToken cancellationToken);

    /// <summary>T2-8 / IXP-8: whether the E3-M2f-3i index exists, is valid, ready and live, and has exactly the approved definition.</summary>
    Task<bool> IsCapabilityRefusalEvidenceIndexUsableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// LK-1 / IXP-6: the newest at most eight capability-refusal evidence entries of the organization (stream and kind as SQL literals,
    /// ordered by stream revision descending). Unrelated audit entries are not read.
    /// </summary>
    Task<IReadOnlyList<CapabilityRefusalEvidenceRow>> GetNewestCapabilityRefusalEvidenceAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddEdge(AuthorityRelationshipEdge edge);

    /// <summary>Phase C8 AC-9: every desired-state edge (active and removed) of one exact relationship, read-only.</summary>
    Task<List<AuthorityRelationshipEdge>> GetRelationshipEdgeHistoryAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, CancellationToken cancellationToken);

    /// <summary>Phase C8 AC-9: committed authorization revisions of the given commands of one organization (read-only; absent or uncommitted ⇒ null).</summary>
    Task<Dictionary<Guid, long?>> GetCommittedRevisionsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken);

    Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>C8 M4 M1 (V10): stored payloads of the organization's non-rejected commands of the given kinds (note-id uniqueness, read under the organization lock).</summary>
    Task<List<string>> GetCommandPayloadsAsync(Guid organizationId, IReadOnlyCollection<string> commandKinds, CancellationToken cancellationToken);

    Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken);

    void AddCommand(SecurityCommand command);

    void AddOutboxEvent(ProjectionOutboxEvent outboxEvent);

    Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken);

    void AddJournalEntry(SecurityJournalEntry entry);

    Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken);

    /// <summary>The content-stream ContentHeadCommitted entry of an accepted operation (latest if ever repeated), by index — not by scanning revisions.</summary>
    Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// SEC-BL-01 WP-5a: the pending entries of one stream (<paramref name="includeStream"/> true) or of every other stream (false), ordered
    /// by stream then revision, at most <paramref name="limit"/>. A stopped D+ stream keeps its rows Pending, so they must not consume the
    /// batch of the other streams. The default (test doubles) filters <see cref="GetPendingJournalEntriesAsync"/>; the store overrides it
    /// with a bounded query.
    /// </summary>
    async Task<List<SecurityJournalEntry>> GetPendingJournalEntriesByStreamAsync(Guid organizationId, string streamName, bool includeStream, int limit, CancellationToken cancellationToken) =>
        [.. (await GetPendingJournalEntriesAsync(organizationId, int.MaxValue, cancellationToken).ConfigureAwait(false))
            .Where(e => string.Equals(e.JournalStreamName, streamName, StringComparison.Ordinal) == includeStream)
            .OrderBy(e => e.JournalStreamName, StringComparer.Ordinal).ThenBy(e => e.JournalStreamRevision).Take(limit)];

    Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken);

    /// <summary>
    /// SEC-BL-01 E-6 (A-7 §27): the entry with the highest revision of the stream, from the journal itself (never from a mutable organization
    /// head column). G08 makes revisions immutable and the journal undeletable for the runtime. The default refuses (fail closed): every store
    /// that can serve the organization gate implements it.
    /// </summary>
    Task<SecurityJournalEntry?> FindLatestJournalEntryAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("this authority scope cannot read the journal head (E-6): fail closed");

    /// <summary>
    /// SEC-BL-01 E-6/E-7 (A-7 §27): the trusted delivery-receipt verifier. The default is the fail-closed verifier: without one, nothing counts
    /// as delivered, so the gate stays closed and no content is released.
    /// </summary>
    IJournalReceiptVerifier JournalReceipts => NotComposedJournalReceiptVerifier.Instance;

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

    /// <summary>E-5: the registration tracked for update (activation of a signing-key version); callers hold the organization lock.</summary>
    Task<IntegrityKeyRegistration?> FindKeyRegistrationForUpdateAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken);

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

    /// <summary>C-188: every registered published version of a form, highest version number first (read-only).</summary>
    Task<List<PublishedFormVersion>> GetPublishedFormVersionsAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    /// <summary>C-188 (D-H7): the form a field definition is bound to in this organization, or null (read-only).</summary>
    Task<FieldFormBinding?> FindFieldFormBindingAsync(Guid organizationId, Guid fieldDefinitionId, CancellationToken cancellationToken);

    void AddFieldFormBinding(FieldFormBinding binding);

    /// <summary>C-188 BF-1: the form's Pending publication (tracked, so a resolution can resolve it), or null.</summary>
    Task<FormVersionPublication?> FindPendingFormVersionPublicationAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken);

    void AddFormVersionPublication(FormVersionPublication publication);

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

    // ---- C8 M2 Stages 1-2: Platform Operators. Lock order: organization state row first, then the platform journal head. ----

    /// <summary>SELECT ... FOR UPDATE on the single platform journal head row: serializes every platform mutation.</summary>
    Task<PlatformJournalHead?> LockPlatformJournalAsync(CancellationToken cancellationToken);

    Task<PlatformJournalHead?> ReadPlatformJournalAsync(CancellationToken cancellationToken);

    void AddPlatformJournalHead(PlatformJournalHead head);

    void AddPlatformJournalEntry(PlatformJournalEntry entry);

    /// <summary>Pending-archive platform entries in revision order (tracked).</summary>
    Task<List<PlatformJournalEntry>> GetPendingPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>E3-M2e (OD-M2e-13): the newest platform journal entries, highest revision first, at most <paramref name="limit"/> (a bounded read of the unique revision index).</summary>
    Task<List<PlatformJournalEntry>> GetLatestPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken);

    Task<int> CountPendingPlatformArchiveAsync(CancellationToken cancellationToken);

    /// <summary>Tracked.</summary>
    Task<PlatformOperator?> FindPlatformOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken);

    /// <summary>Tracked.</summary>
    Task<PlatformOperator?> FindPlatformOperatorByIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken);

    /// <summary>Every registered operator (tracked), in registration order.</summary>
    Task<List<PlatformOperator>> GetPlatformOperatorsAsync(CancellationToken cancellationToken);

    void AddPlatformOperator(PlatformOperator platformOperator);

    /// <summary>Tracked.</summary>
    Task<PlatformOperatorChangeProposal?> FindPlatformOperatorProposalAsync(Guid proposalId, CancellationToken cancellationToken);

    /// <summary>Proposals created since the given instant (tracked).</summary>
    Task<List<PlatformOperatorChangeProposal>> GetPlatformOperatorProposalsAsync(DateTimeOffset createdSince, CancellationToken cancellationToken);

    void AddPlatformOperatorProposal(PlatformOperatorChangeProposal proposal);

    /// <summary>Tracked.</summary>
    Task<PlatformOperatorApproval?> FindPlatformApprovalAsync(Guid platformOperatorApprovalId, CancellationToken cancellationToken);

    /// <summary>Every approval of one request (tracked), in recording order.</summary>
    Task<List<PlatformOperatorApproval>> GetPlatformApprovalsForRequestAsync(Guid organizationMembershipRequestId, CancellationToken cancellationToken);

    /// <summary>Pending approvals recorded by one operator (tracked).</summary>
    Task<List<PlatformOperatorApproval>> GetPendingPlatformApprovalsForOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken);

    /// <summary>Approvals of the given requests (read-only).</summary>
    Task<List<PlatformOperatorApproval>> GetPlatformApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationMembershipRequestIds, CancellationToken cancellationToken);

    void AddPlatformApproval(PlatformOperatorApproval approval);

    /// <summary>True when an evidence identifier or digest was already accepted once.</summary>
    Task<bool> IsPlatformEvidenceUsedAsync(string evidenceUseIdentifier, byte[] evidenceUseSha256, CancellationToken cancellationToken);

    /// <summary>Tracked (journal replay upserts by id).</summary>
    Task<PlatformOperatorEvidenceUse?> FindPlatformEvidenceUseAsync(Guid platformOperatorEvidenceUseId, CancellationToken cancellationToken);

    void AddPlatformEvidenceUse(PlatformOperatorEvidenceUse evidenceUse);

    /// <summary>Cross-organization page of membership requests (read-only), newest first, keyset after the cursor, with the total.</summary>
    Task<PlatformRequestStorePage> SearchMembershipRequestsAsync(PlatformMembershipRequestQuery query, CancellationToken cancellationToken);

    // ---- C8 onboarding (C-104). Lock order: platform journal head first, then the onboarding request row. ----

    /// <summary>The request, optionally locked FOR UPDATE.</summary>
    Task<OrganizationOnboardingRequest?> FindOnboardingRequestAsync(Guid organizationOnboardingRequestId, bool lockForUpdate, CancellationToken cancellationToken);

    Task<OrganizationOnboardingRequest?> FindOnboardingRequestByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Any request (any state) that reserved this organization id.</summary>
    Task<OrganizationOnboardingRequest?> FindOnboardingRequestByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Requests whose stored state is still open (PendingApproval or AwaitingKeyCustody) with this normalized name, tracked.</summary>
    Task<List<OrganizationOnboardingRequest>> GetOpenOnboardingRequestsByNameAsync(string normalizedName, CancellationToken cancellationToken);

    void AddOnboardingRequest(OrganizationOnboardingRequest request);

    Task<List<OrganizationOnboardingOwner>> GetOnboardingOwnersAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken);

    Task<OrganizationOnboardingOwner?> FindOnboardingOwnerAsync(Guid organizationOnboardingOwnerId, CancellationToken cancellationToken);

    void AddOnboardingOwner(OrganizationOnboardingOwner owner);

    Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken);

    Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationOnboardingRequestIds, CancellationToken cancellationToken);

    Task<OrganizationOnboardingApproval?> FindOnboardingApprovalAsync(Guid organizationOnboardingApprovalId, CancellationToken cancellationToken);

    void AddOnboardingApproval(OrganizationOnboardingApproval approval);

    /// <summary>Newest first; text matches the organization name (case-insensitive).</summary>
    Task<OnboardingRequestStorePage> SearchOnboardingRequestsAsync(OnboardingRequestQuery query, CancellationToken cancellationToken);

    /// <summary>Every organization with its enabled owner, Security Administrator and member counts (directory; metadata only).</summary>
    Task<List<OrganizationDirectoryRow>> GetOrganizationDirectoryRowsAsync(CancellationToken cancellationToken);

    /// <summary>Number of organizations in which this exact identity has a binding (any state).</summary>
    Task<int> CountOrganizationsBindingIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken);

    // ---- C8 owner recovery (C-105). Lock order: organization row, then the platform journal head, then the case row. ----

    /// <summary>With <paramref name="lockForUpdate"/> false: an untracked snapshot (used to find the organization before locking it).</summary>
    Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseAsync(Guid organizationOwnerRecoveryCaseId, bool lockForUpdate, CancellationToken cancellationToken);

    Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The organization's case whose stored state is Open (at most one), tracked.</summary>
    Task<OrganizationOwnerRecoveryCase?> FindOpenOwnerRecoveryCaseAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddOwnerRecoveryCase(OrganizationOwnerRecoveryCase recoveryCase);

    Task<List<OrganizationOwnerRecoverySubject>> GetOwnerRecoverySubjectsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken);

    Task<OrganizationOwnerRecoverySubject?> FindOwnerRecoverySubjectAsync(Guid organizationOwnerRecoverySubjectId, CancellationToken cancellationToken);

    void AddOwnerRecoverySubject(OrganizationOwnerRecoverySubject subject);

    Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken);

    Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsForCasesAsync(IReadOnlyCollection<Guid> organizationOwnerRecoveryCaseIds, CancellationToken cancellationToken);

    Task<OrganizationOwnerRecoveryApproval?> FindOwnerRecoveryApprovalAsync(Guid organizationOwnerRecoveryApprovalId, CancellationToken cancellationToken);

    void AddOwnerRecoveryApproval(OrganizationOwnerRecoveryApproval approval);

    /// <summary>Newest first; text matches the organization name or incident reference (case-insensitive).</summary>
    Task<OwnerRecoveryStorePage> SearchOwnerRecoveryCasesAsync(OwnerRecoveryQuery query, CancellationToken cancellationToken);

    /// <summary>Probations of the organization that end after <paramref name="now"/>.</summary>
    Task<List<OrganizationRecoveredOwnerProbation>> GetActiveRecoveredOwnerProbationsAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<OrganizationRecoveredOwnerProbation?> FindRecoveredOwnerProbationAsync(Guid organizationRecoveredOwnerProbationId, CancellationToken cancellationToken);

    void AddRecoveredOwnerProbation(OrganizationRecoveredOwnerProbation probation);

    /// <summary>Authenticated sessions of this binding created at or after <paramref name="since"/> (evidence that a declared-unavailable owner is active).</summary>
    Task<int> CountSessionsCreatedSinceAsync(Guid identityBindingId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Writes staged changes inside the open transaction without committing (orders updates that free a unique value before inserts that take it).</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Filters are applied in the store; <see cref="Status"/> is one of <c>PlatformRequestFilters</c>.</summary>
public sealed record PlatformMembershipRequestQuery(string? Text, string Status, DateTimeOffset Now, (DateTimeOffset CreatedOn, Guid RequestId)? After, int Limit);

public sealed record PlatformRequestStoreRow(OrganizationMembershipRequest Request, string OrganizationDisplayName);

public sealed record PlatformRequestStorePage(IReadOnlyList<PlatformRequestStoreRow> Rows, int TotalCount);

public sealed record OnboardingRequestQuery(string? Text, string Status, DateTimeOffset Now, (DateTimeOffset CreatedOn, Guid RequestId)? After, int Limit);

public sealed record OnboardingRequestStorePage(IReadOnlyList<OrganizationOnboardingRequest> Rows, int TotalCount);

public sealed record OrganizationDirectoryRow(OrganizationAuthorizationState State, int EnabledOwnerCount, int SecurityAdministratorCount, int EnabledMemberCount);

public sealed record OwnerRecoveryQuery(string? Text, string Status, DateTimeOffset Now, (DateTimeOffset OpenedOn, Guid CaseId)? After, int Limit);

public sealed record OwnerRecoveryStoreRow(OrganizationOwnerRecoveryCase Case, string OrganizationDisplayName, int ValidApprovals);

public sealed record OwnerRecoveryStorePage(IReadOnlyList<OwnerRecoveryStoreRow> Rows, int TotalCount);
