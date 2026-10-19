using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>Isolated authority PostgreSQL store. Implemented only in AuthorizationAuthority.Infrastructure.</summary>
public interface IAuthorityStore
{
    /// <summary>Opens one database transaction. Everything performed through the scope commits atomically or not at all.</summary>
    Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken);

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

    Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken);

    void AddIdentityBinding(IdentityBinding binding);

    Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken);

    void AddResource(AuthorityResource resource);

    /// <summary>All currently active desired-state edges of the organization (normalized authoritative graph).</summary>
    Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken);

    void AddEdge(AuthorityRelationshipEdge edge);

    Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken);

    Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken);

    void AddCommand(SecurityCommand command);

    void AddOutboxEvent(ProjectionOutboxEvent outboxEvent);

    Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken);

    void AddJournalEntry(SecurityJournalEntry entry);

    Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken);

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

    void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint);

    Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken);

    void AddKeyRegistration(IntegrityKeyRegistration registration);

    /// <summary>Record IDs registered and active for the organization (authority-sourced discovery candidates).</summary>
    Task<List<Guid>> GetActiveRecordIdsAsync(Guid organizationId, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}
