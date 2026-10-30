using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>EF Core implementation of the authority store port. One DbContext + one transaction per scope.</summary>
public sealed class AuthorityStore(IDbContextFactory<AuthorityDbContext> contextFactory) : IAuthorityStore
{
    public async Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        return new AuthorityScope(context, transaction);
    }

    public async Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var projection = context.OrganizationStates.Where(o => o.ProjectionAppliedRevision < o.AuthorizationRevision && !o.IntegrityQuarantineEnabled).Select(o => o.OrganizationId);
        var archive = context.JournalEntries.Where(j => j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending).Select(j => j.OrganizationId);
        return await projection.Union(archive).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class AuthorityScope(AuthorityDbContext context, IDbContextTransaction transaction) : IAuthorityScope
    {
        private bool committed;

        public Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.OrganizationStates.FromSqlInterpolated($"SELECT * FROM authority.organization_authorization_states WHERE organization_id = {organizationId} FOR UPDATE")
                .AsTracking().FirstOrDefaultAsync(cancellationToken);

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.OrganizationStates.AsNoTracking().FirstOrDefaultAsync(o => o.OrganizationId == organizationId, cancellationToken);

        public void AddOrganization(OrganizationAuthorizationState state) => context.OrganizationStates.Add(state);

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid organizationId, string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().FirstOrDefaultAsync(b => b.OrganizationId == organizationId && b.IdentityIssuerUri == issuerUri && b.IdentitySubjectIdentifier == subjectIdentifier, cancellationToken);

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().FirstOrDefaultAsync(b => b.OrganizationId == organizationId && b.AuthorityUserId == authorityUserId, cancellationToken);

        public Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().Where(b => b.OrganizationId == organizationId).ToListAsync(cancellationToken);

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().FirstOrDefaultAsync(b => b.IdentityBindingId == identityBindingId, cancellationToken);

        public void AddIdentityBinding(IdentityBinding binding) => context.IdentityBindings.Add(binding);

        public Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken) =>
            context.Resources.FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && r.ResourceEntityId == resourceEntityId, cancellationToken);

        public void AddResource(AuthorityResource resource) => context.Resources.Add(resource);

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid organizationId, Guid parentResourceEntityId, IReadOnlyCollection<string> resourceTypeNames, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ParentResourceEntityId == parentResourceEntityId && resourceTypeNames.Contains(r.ResourceTypeName) && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToListAsync(cancellationToken);

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.ProtectionPolicies.FirstOrDefaultAsync(p => p.OrganizationId == organizationId, cancellationToken);

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => context.ProtectionPolicies.Add(policy);

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.RelationshipEdges.Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId == null).ToListAsync(cancellationToken);

        public void AddEdge(AuthorityRelationshipEdge edge) => context.RelationshipEdges.Add(edge);

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.SecurityCommands.FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.CommandIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken) =>
            context.SecurityCommands.FirstOrDefaultAsync(c => c.SecurityCommandId == securityCommandId, cancellationToken);

        public void AddCommand(SecurityCommand command) => context.SecurityCommands.Add(command);

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => context.ProjectionOutbox.Add(outboxEvent);

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken) =>
            context.ProjectionOutbox.FirstOrDefaultAsync(e => e.OrganizationId == organizationId && e.TargetAuthorizationRevision == targetAuthorizationRevision, cancellationToken);

        public void AddJournalEntry(SecurityJournalEntry entry) => context.JournalEntries.Add(entry);

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            context.JournalEntries.FirstOrDefaultAsync(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.JournalStreamRevision == streamRevision, cancellationToken);

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.JournalEntries
                .Where(j => j.OrganizationId == organizationId && j.JournalStreamName == JournalStreamNames.Content && j.ContentChangeOperationId == contentChangeOperationId && j.JournalEventKind == "ContentHeadCommitted")
                .OrderByDescending(j => j.JournalStreamRevision)
                .FirstOrDefaultAsync(cancellationToken);

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
            context.JournalEntries.Where(j => j.OrganizationId == organizationId && j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending)
                .OrderBy(j => j.JournalStreamName).ThenBy(j => j.JournalStreamRevision).Take(limit).ToListAsync(cancellationToken);

        public Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            context.JournalEntries.CountAsync(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored, cancellationToken);

        public Task<RecordContentHead?> FindContentHeadAsync(Guid organizationId, Guid formRecordId, bool lockForUpdate, CancellationToken cancellationToken) =>
            lockForUpdate
                ? context.ContentHeads.FromSqlInterpolated($"SELECT * FROM authority.record_content_heads WHERE organization_id = {organizationId} AND form_record_id = {formRecordId} FOR UPDATE").AsTracking().FirstOrDefaultAsync(cancellationToken)
                : context.ContentHeads.FirstOrDefaultAsync(h => h.OrganizationId == organizationId && h.FormRecordId == formRecordId, cancellationToken);

        public void AddContentHead(RecordContentHead head) => context.ContentHeads.Add(head);

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.ContentOperations.FirstOrDefaultAsync(o => o.OrganizationId == organizationId && o.OperationIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<ContentChangeOperation?> FindOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.ContentOperations.FirstOrDefaultAsync(o => o.ContentChangeOperationId == contentChangeOperationId, cancellationToken);

        public void AddOperation(ContentChangeOperation operation) => context.ContentOperations.Add(operation);

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves) => context.CandidateLeaves.AddRange(leaves);

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.CandidateLeaves.AsNoTracking().Where(l => l.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken);

        public void AddManifestCommit(RecordManifestCommit commit) => context.ManifestCommits.Add(commit);

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => context.ManifestLeaves.AddRange(leaves);

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid recordManifestCommitId, CancellationToken cancellationToken) =>
            context.ManifestLeaves.AsNoTracking().Where(l => l.RecordManifestCommitId == recordManifestCommitId).OrderBy(l => l.ManifestLeafPosition).ToListAsync(cancellationToken);

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            context.ManifestCommits.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedOperationId == committedOperationId, cancellationToken);

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken cancellationToken) =>
            context.ManifestCommits.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedContentRevision > afterRevision)
                .OrderBy(c => c.CommittedContentRevision)
                .Take(limit)
                .ToListAsync(cancellationToken);

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken cancellationToken) =>
            context.ContentOperations.AsNoTracking().Where(o => contentChangeOperationIds.Contains(o.ContentChangeOperationId)).ToListAsync(cancellationToken);

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => context.ContentCheckpoints.Add(checkpoint);

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken) =>
            context.KeyRegistrations.AsNoTracking().FirstOrDefaultAsync(k => k.OrganizationId == organizationId && k.KeyPurposeName == keyPurposeName, cancellationToken);

        public void AddKeyRegistration(IntegrityKeyRegistration registration) => context.KeyRegistrations.Add(registration);

        public Task<List<Guid>> GetActiveRecordIdsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ResourceLifecycleState == ResourceLifecycleStates.Active).Select(r => r.ResourceEntityId).ToListAsync(cancellationToken);

        public Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ParentResourceEntityId == formDefinitionId && lifecycleStates.Contains(r.ResourceLifecycleState))
                .OrderBy(r => r.ResourceEntityId)
                .ToListAsync(cancellationToken);

        public Task<List<AuthorityResource>> GetResourcesAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && resourceEntityIds.Contains(r.ResourceEntityId)).ToListAsync(cancellationToken);

        public Task<List<RecordContentHead>> GetContentHeadsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.ContentHeads.AsNoTracking().Where(h => h.OrganizationId == organizationId && formRecordIds.Contains(h.FormRecordId)).ToListAsync(cancellationToken);

        public void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries)
        {
            context.CatalogueSnapshots.Add(snapshot);
            context.CatalogueSnapshotEntries.AddRange(entries);
        }

        public Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) =>
            context.CatalogueSnapshots.AsTracking().FirstOrDefaultAsync(s => s.SearchCatalogueSnapshotId == searchCatalogueSnapshotId, cancellationToken);

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid searchCatalogueSnapshotId, int offset, int? limit, CancellationToken cancellationToken)
        {
            var query = context.CatalogueSnapshotEntries.AsNoTracking().Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId).OrderBy(e => e.CatalogueEntryPosition).Skip(offset);
            return (limit is { } take ? query.Take(take) : query).ToListAsync(cancellationToken);
        }

        public Task<AuthenticatedSession?> FindSessionByDigestAsync(byte[] sessionSecretDigest, CancellationToken cancellationToken) =>
            context.Sessions.AsTracking().FirstOrDefaultAsync(s => s.SessionSecretDigest == sessionSecretDigest, cancellationToken);

        public void AddSession(AuthenticatedSession session) => context.Sessions.Add(session);

        public Task<PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.PublishedFormVersions.AsNoTracking().Where(v => v.OrganizationId == organizationId && v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.FormVersionNumber).FirstOrDefaultAsync(cancellationToken);

        public void AddPublishedFormVersion(PublishedFormVersion version) => context.PublishedFormVersions.Add(version);

        public async Task<List<PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            (await context.PublishedFormVersions.AsNoTracking().Where(v => v.OrganizationId == organizationId).ToListAsync(cancellationToken).ConfigureAwait(false))
                .GroupBy(v => v.FormDefinitionId).Select(g => g.MaxBy(v => v.FormVersionNumber)!).ToList();

        public Task<List<FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken) =>
            context.FieldSearchApprovals.AsTracking().Where(a => a.OrganizationId == organizationId && (formDefinitionId == null || a.FormDefinitionId == formDefinitionId)).OrderBy(a => a.FieldSearchApprovalId).ToListAsync(cancellationToken);

        public void AddFieldSearchApproval(FieldSearchApproval approval) => context.FieldSearchApprovals.Add(approval);

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesForRecordsAsync(Guid searchCatalogueSnapshotId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.CatalogueSnapshotEntries.AsNoTracking().Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId && formRecordIds.Contains(e.FormRecordId)).ToListAsync(cancellationToken);

        public Task<List<AuthorityResource>> GetResourcesByTypeAsync(Guid organizationId, IReadOnlyCollection<string> resourceTypeNames, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && resourceTypeNames.Contains(r.ResourceTypeName) && lifecycleStates.Contains(r.ResourceLifecycleState))
                .OrderBy(r => r.ResourceEntityId)
                .ToListAsync(cancellationToken);

        public async Task<Dictionary<Guid, int>> CountManifestCommitsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            formRecordIds.Count == 0
                ? []
                : await context.ManifestCommits.AsNoTracking()
                    .Where(c => c.OrganizationId == organizationId && formRecordIds.Contains(c.FormRecordId))
                    .GroupBy(c => c.FormRecordId)
                    .Select(g => new { FormRecordId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.FormRecordId, g => g.Count, cancellationToken).ConfigureAwait(false);

        public Task<List<ProtectionCensusResult>> GetProtectionCensusResultsAsync(Guid organizationId, Guid scopingSecurityCommandId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.ProtectionCensusResults.AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && r.ScopingSecurityCommandId == scopingSecurityCommandId && formRecordIds.Contains(r.FormRecordId))
                .ToListAsync(cancellationToken);

        public void AddProtectionCensusResult(ProtectionCensusResult result) => context.ProtectionCensusResults.Add(result);

        public Task<ProtectionCensusResult?> FindProtectionCensusResultAsync(Guid scopingSecurityCommandId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            context.ProtectionCensusResults.AsTracking().FirstOrDefaultAsync(r => r.ScopingSecurityCommandId == scopingSecurityCommandId && r.FormRecordId == formRecordId && r.CommittedOperationId == committedOperationId, cancellationToken);

        public void AddProtectionAttestation(ProtectionReconciliationAttestation attestation) => context.ProtectionAttestations.Add(attestation);

        public async Task<List<ProtectionReconciliationAttestation>> GetLatestProtectionAttestationsAsync(Guid organizationId, IReadOnlyCollection<Guid> scopingSecurityCommandIds, CancellationToken cancellationToken) =>
            scopingSecurityCommandIds.Count == 0
                ? []
                : (await context.ProtectionAttestations.AsNoTracking()
                    .Where(a => a.OrganizationId == organizationId && scopingSecurityCommandIds.Contains(a.ScopingSecurityCommandId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .GroupBy(a => a.ScopingSecurityCommandId)
                    .Select(g => g.MaxBy(a => a.AttestedAuditJournalRevision)!)
                    .ToList();

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!committed)
            {
                try
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // already completed by the provider
                }
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
