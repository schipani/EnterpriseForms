using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Single-threaded in-memory Authority store for content-protocol unit tests (Phase C5). It reproduces the store's data
/// contract, not PostgreSQL concurrency: lock ordering between security commands and content commits is covered by the
/// Docker integration tests (revocation race) and the code inspection recorded for U5.
/// </summary>
internal sealed class InMemoryAuthorityStore : IAuthorityStore
{
    public List<OrganizationAuthorizationState> Organizations { get; } = [];
    public List<IdentityBinding> Bindings { get; } = [];
    public List<AuthorityResource> Resources { get; } = [];
    public List<AuthorityRelationshipEdge> Edges { get; } = [];
    public List<ContentChangeOperation> Operations { get; } = [];
    public List<ContentCandidateLeaf> CandidateLeaves { get; } = [];
    public List<RecordContentHead> Heads { get; } = [];
    public List<RecordManifestCommit> Commits { get; } = [];
    public List<RecordManifestLeaf> ManifestLeaves { get; } = [];
    public List<SecurityJournalEntry> Journal { get; } = [];
    public List<ContentIntegrityCheckpoint> Checkpoints { get; } = [];
    public List<IntegrityKeyRegistration> Keys { get; } = [];
    public List<SearchCatalogueSnapshot> Snapshots { get; } = [];
    public List<SecurityCommand> Commands { get; } = [];
    public List<SearchCatalogueSnapshotEntry> SnapshotEntries { get; } = [];

    public List<AuthenticatedSession> Sessions { get; } = [];
    public List<PublishedFormVersion> PublishedVersions { get; } = [];
    public List<FieldSearchApproval> Approvals { get; } = [];
    public List<OrganizationProtectionPolicy> Policies { get; } = [];
    public int ScopeCommitCount { get; private set; }

    public Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken) => Task.FromResult<IAuthorityScope>(new Scope(this));

    public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

    private sealed class Scope(InMemoryAuthorityStore s) : IAuthorityScope
    {
        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Organizations.FirstOrDefault(o => o.OrganizationId == organizationId));

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Organizations.FirstOrDefault(o => o.OrganizationId == organizationId));

        public void AddOrganization(OrganizationAuthorizationState state) => s.Organizations.Add(state);

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid organizationId, string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.FirstOrDefault(b => b.OrganizationId == organizationId && b.IdentityIssuerUri == issuerUri && b.IdentitySubjectIdentifier == subjectIdentifier));

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.FirstOrDefault(b => b.OrganizationId == organizationId && b.AuthorityUserId == authorityUserId));

        public Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Bindings.Where(b => b.OrganizationId == organizationId).ToList());

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken) => Task.FromResult(s.Bindings.FirstOrDefault(b => b.IdentityBindingId == identityBindingId));

        public void AddIdentityBinding(IdentityBinding binding) => s.Bindings.Add(binding);

        public Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.FirstOrDefault(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && r.ResourceEntityId == resourceEntityId));

        public void AddResource(AuthorityResource resource) => s.Resources.Add(resource);

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid organizationId, Guid parentResourceEntityId, IReadOnlyCollection<string> resourceTypeNames, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ParentResourceEntityId == parentResourceEntityId && resourceTypeNames.Contains(r.ResourceTypeName) && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToList());

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(s.Policies.FirstOrDefault(p => p.OrganizationId == organizationId));

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => s.Policies.Add(policy);

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Edges.Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId is null).ToList());

        public void AddEdge(AuthorityRelationshipEdge edge) => s.Edges.Add(edge);

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) => Task.FromResult<SecurityCommand?>(null);

        public Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken) => Task.FromResult(s.Commands.FirstOrDefault(c => c.SecurityCommandId == securityCommandId));

        public void AddCommand(SecurityCommand command) => throw new NotSupportedException();

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => throw new NotSupportedException();

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken) => Task.FromResult<ProjectionOutboxEvent?>(null);

        public void AddJournalEntry(SecurityJournalEntry entry) => s.Journal.Add(entry);

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.Where(j => j.OrganizationId == organizationId && j.JournalStreamName == JournalStreamNames.Content && j.ContentChangeOperationId == contentChangeOperationId && j.JournalEventKind == "ContentHeadCommitted")
                .OrderByDescending(j => j.JournalStreamRevision).FirstOrDefault());

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Task.FromResult(s.Journal.FirstOrDefault(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.JournalStreamRevision == streamRevision));

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) => Task.FromResult(new List<SecurityJournalEntry>());

        public Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<RecordContentHead?> FindContentHeadAsync(Guid organizationId, Guid formRecordId, bool lockForUpdate, CancellationToken cancellationToken) =>
            Task.FromResult(s.Heads.FirstOrDefault(h => h.OrganizationId == organizationId && h.FormRecordId == formRecordId));

        public void AddContentHead(RecordContentHead head) => s.Heads.Add(head);

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.FirstOrDefault(o => o.OrganizationId == organizationId && o.OperationIdempotencyKey == idempotencyKey));

        public Task<ContentChangeOperation?> FindOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.FirstOrDefault(o => o.ContentChangeOperationId == contentChangeOperationId));

        public void AddOperation(ContentChangeOperation operation) => s.Operations.Add(operation);

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves) => s.CandidateLeaves.AddRange(leaves);

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.CandidateLeaves.Where(l => l.ContentChangeOperationId == contentChangeOperationId).ToList());

        public void AddManifestCommit(RecordManifestCommit commit) => s.Commits.Add(commit);

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => s.ManifestLeaves.AddRange(leaves);

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid recordManifestCommitId, CancellationToken cancellationToken) =>
            Task.FromResult(s.ManifestLeaves.Where(l => l.RecordManifestCommitId == recordManifestCommitId).OrderBy(l => l.ManifestLeafPosition).ToList());

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commits.FirstOrDefault(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedOperationId == committedOperationId));

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(s.Commits.Where(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedContentRevision > afterRevision).OrderBy(c => c.CommittedContentRevision).Take(limit).ToList());

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Operations.Where(o => contentChangeOperationIds.Contains(o.ContentChangeOperationId)).ToList());

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => s.Checkpoints.Add(checkpoint);

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken) =>
            Task.FromResult(s.Keys.FirstOrDefault(k => k.OrganizationId == organizationId && k.KeyPurposeName == keyPurposeName));

        public void AddKeyRegistration(IntegrityKeyRegistration registration) => s.Keys.Add(registration);

        public Task<List<Guid>> GetActiveRecordIdsAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(new List<Guid>());

        public Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ParentResourceEntityId == formDefinitionId && lifecycleStates.Contains(r.ResourceLifecycleState)).OrderBy(r => r.ResourceEntityId).ToList());

        public Task<List<AuthorityResource>> GetResourcesAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Resources.Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && resourceEntityIds.Contains(r.ResourceEntityId)).ToList());

        public Task<List<RecordContentHead>> GetContentHeadsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            Task.FromResult(s.Heads.Where(h => h.OrganizationId == organizationId && formRecordIds.Contains(h.FormRecordId)).ToList());

        public void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries)
        {
            s.Snapshots.Add(snapshot);
            s.SnapshotEntries.AddRange(entries);
        }

        public Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Snapshots.FirstOrDefault(x => x.SearchCatalogueSnapshotId == searchCatalogueSnapshotId));

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid searchCatalogueSnapshotId, int offset, int? limit, CancellationToken cancellationToken)
        {
            var query = s.SnapshotEntries.Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId).OrderBy(e => e.CatalogueEntryPosition).Skip(offset);
            return Task.FromResult((limit is { } take ? query.Take(take) : query).ToList());
        }

        public Task<AuthenticatedSession?> FindSessionByDigestAsync(byte[] sessionSecretDigest, CancellationToken cancellationToken) =>
            Task.FromResult(s.Sessions.FirstOrDefault(x => x.SessionSecretDigest.AsSpan().SequenceEqual(sessionSecretDigest)));

        public void AddSession(AuthenticatedSession session) => s.Sessions.Add(session);

        public Task<PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PublishedVersions.Where(v => v.OrganizationId == organizationId && v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.FormVersionNumber).FirstOrDefault());

        public void AddPublishedFormVersion(PublishedFormVersion version) => s.PublishedVersions.Add(version);

        public Task<List<PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(s.PublishedVersions.Where(v => v.OrganizationId == organizationId).GroupBy(v => v.FormDefinitionId).Select(g => g.MaxBy(v => v.FormVersionNumber)!).ToList());

        public Task<List<FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(s.Approvals.Where(a => a.OrganizationId == organizationId && (formDefinitionId == null || a.FormDefinitionId == formDefinitionId)).ToList());

        public void AddFieldSearchApproval(FieldSearchApproval approval) => s.Approvals.Add(approval);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            s.ScopeCommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
