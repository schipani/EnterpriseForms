using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// IX-5a: EF Core implementation of <see cref="ISearchAccessStore"/> on the scope's own context and transaction. Every statement runs
/// after <see cref="BindOrganizationAsync"/> has set the transaction-local organization the forced RLS policies check.
/// </summary>
internal sealed class SearchAccessStore(AuthorityDbContext context) : ISearchAccessStore
{
    public Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('authority_search_access.organization_id', {organizationId.ToString("D")}, true)", cancellationToken);

    public async Task<SearchAccessCheckpoint?> FindCheckpointForUpdateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        (await context.SearchAccessCheckpoints
            .FromSqlInterpolated($"SELECT * FROM authority_search_access.search_access_checkpoints WHERE organization_id = {organizationId} FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<SearchAccessCheckpoint?> FindCheckpointAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchAccessCheckpoints.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);

    public async Task InsertCheckpointAsync(SearchAccessCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        context.SearchAccessCheckpoints.Add(checkpoint);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(checkpoint).State = EntityState.Detached;
    }

    public async Task<bool> TryUpdateCheckpointAsync(Guid organizationId, long expectedCheckpointRevision, long consumedAuthorizationRevision, byte[]? consumedJournalDigest, string stateName, string? quarantineReasonCode, int protocolVersion, DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.SearchAccessCheckpoints
            .Where(c => c.OrganizationId == organizationId && c.CheckpointRevision == expectedCheckpointRevision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConsumedAuthorizationRevision, consumedAuthorizationRevision)
                .SetProperty(c => c.ConsumedJournalDigest, consumedJournalDigest)
                .SetProperty(c => c.AccessProjectionStateName, stateName)
                .SetProperty(c => c.QuarantineReasonCode, quarantineReasonCode)
                .SetProperty(c => c.AccessProjectionProtocolVersion, protocolVersion)
                .SetProperty(c => c.CheckpointModifiedOn, now)
                .SetProperty(c => c.CheckpointRevision, expectedCheckpointRevision + 1), cancellationToken).ConfigureAwait(false) == 1;

    public Task<List<SearchAccessEdge>> GetEdgesByResourceAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken)
    {
        var ids = resourceEntityIds.ToList();
        return context.SearchAccessEdges.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && ids.Contains(e.ResourceEntityId))
            .ToListAsync(cancellationToken);
    }

    public Task<List<SearchAccessEdge>> GetEdgesBySubjectAsync(Guid organizationId, string subjectTypeName, IReadOnlyCollection<Guid> subjectEntityIds, string resourceTypeName, string relationshipName, CancellationToken cancellationToken)
    {
        var ids = subjectEntityIds.ToList();
        return context.SearchAccessEdges.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.SubjectTypeName == subjectTypeName && ids.Contains(e.SubjectEntityId) && e.ResourceTypeName == resourceTypeName && e.RelationshipName == relationshipName)
            .ToListAsync(cancellationToken);
    }

    public Task<SearchAccessEdge?> FindEdgeAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, string subjectRelationName, CancellationToken cancellationToken) =>
        context.SearchAccessEdges.AsTracking().SingleOrDefaultAsync(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && e.ResourceEntityId == resourceEntityId
            && e.RelationshipName == relationshipName && e.SubjectTypeName == subjectTypeName && e.SubjectEntityId == subjectEntityId && e.SubjectRelationName == subjectRelationName, cancellationToken);

    public void AddEdge(SearchAccessEdge edge) => context.SearchAccessEdges.Add(edge);

    public void RemoveEdge(SearchAccessEdge edge) => context.SearchAccessEdges.Remove(edge);

    public Task FlushAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    public async Task DeleteGrantsOfRecordsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds.ToList();
        await context.SearchAccessRecordGrants.Where(g => g.OrganizationId == organizationId && ids.Contains(g.FormRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessFieldItemGrants.Where(g => g.OrganizationId == organizationId && ids.Contains(g.FormRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AddRecordGrants(IEnumerable<SearchAccessRecordGrant> grants) => context.SearchAccessRecordGrants.AddRange(grants);

    public void AddFieldItemGrants(IEnumerable<SearchAccessFieldItemGrant> grants) => context.SearchAccessFieldItemGrants.AddRange(grants);

    public async Task PurgeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await context.SearchAccessFieldItemGrants.Where(g => g.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessRecordGrants.Where(g => g.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessEdges.Where(e => e.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<List<SearchAccessEdge>> GetAllEdgesAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchAccessEdges.AsNoTracking().Where(e => e.OrganizationId == organizationId).ToListAsync(cancellationToken);

    public Task<List<SearchAccessRecordGrant>> GetRecordGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds?.ToList();
        return context.SearchAccessRecordGrants.AsNoTracking().Where(g => g.OrganizationId == organizationId && (ids == null || ids.Contains(g.FormRecordId))).ToListAsync(cancellationToken);
    }

    public Task<List<SearchAccessFieldItemGrant>> GetFieldItemGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds?.ToList();
        return context.SearchAccessFieldItemGrants.AsNoTracking().Where(g => g.OrganizationId == organizationId && (ids == null || ids.Contains(g.FormRecordId))).ToListAsync(cancellationToken);
    }
}
