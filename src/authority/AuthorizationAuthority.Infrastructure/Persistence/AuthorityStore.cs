using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
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
