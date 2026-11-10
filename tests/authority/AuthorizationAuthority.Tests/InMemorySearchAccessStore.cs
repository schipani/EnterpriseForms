using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-5a in-memory <see cref="ISearchAccessStore"/> for unit tests: the contract only (no transactions, no row locks). Every
/// operation must target the bound organization, as the forced RLS policies require in PostgreSQL.
/// </summary>
internal sealed class InMemorySearchAccessStore : ISearchAccessStore
{
    private Guid? bound;

    public List<SearchAccessCheckpoint> Checkpoints { get; } = [];

    public List<SearchAccessEdge> Edges { get; } = [];

    public List<SearchAccessRecordGrant> RecordGrants { get; } = [];

    public List<SearchAccessFieldItemGrant> FieldItemGrants { get; } = [];

    /// <summary>Simulates a concurrent maintainer: the next compare-and-set fails.</summary>
    public bool LoseNextCompareAndSet { get; set; }

    public Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        bound = organizationId;
        return Task.CompletedTask;
    }

    public Task<SearchAccessCheckpoint?> FindCheckpointForUpdateAsync(Guid organizationId, CancellationToken cancellationToken) => FindCheckpointAsync(organizationId, cancellationToken);

    public Task<SearchAccessCheckpoint?> FindCheckpointAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        Require(organizationId);
        var found = Checkpoints.SingleOrDefault(c => c.OrganizationId == organizationId);
        return Task.FromResult(found is null ? null : Copy(found));
    }

    public Task InsertCheckpointAsync(SearchAccessCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Require(checkpoint.OrganizationId);
        if (Checkpoints.Any(c => c.OrganizationId == checkpoint.OrganizationId))
        {
            throw new InvalidOperationException("uq_search_access_checkpoint_org");
        }

        Checkpoints.Add(Copy(checkpoint));
        return Task.CompletedTask;
    }

    public Task<bool> TryUpdateCheckpointAsync(Guid organizationId, long expectedCheckpointRevision, long consumedAuthorizationRevision, byte[]? consumedJournalDigest, string stateName, string? quarantineReasonCode, int protocolVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Require(organizationId);
        var stored = Checkpoints.SingleOrDefault(c => c.OrganizationId == organizationId && c.CheckpointRevision == expectedCheckpointRevision);
        if (stored is null || LoseNextCompareAndSet)
        {
            LoseNextCompareAndSet = false;
            return Task.FromResult(false);
        }

        stored.ConsumedAuthorizationRevision = consumedAuthorizationRevision;
        stored.ConsumedJournalDigest = consumedJournalDigest;
        stored.AccessProjectionStateName = stateName;
        stored.QuarantineReasonCode = quarantineReasonCode;
        stored.AccessProjectionProtocolVersion = protocolVersion;
        stored.CheckpointModifiedOn = now;
        stored.CheckpointRevision = expectedCheckpointRevision + 1;
        return Task.FromResult(true);
    }

    public Task<List<SearchAccessEdge>> GetEdgesByResourceAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken)
    {
        Require(organizationId);
        return Task.FromResult(Edges.Where(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && resourceEntityIds.Contains(e.ResourceEntityId)).ToList());
    }

    public Task<List<SearchAccessEdge>> GetEdgesBySubjectAsync(Guid organizationId, string subjectTypeName, IReadOnlyCollection<Guid> subjectEntityIds, string resourceTypeName, string relationshipName, CancellationToken cancellationToken)
    {
        Require(organizationId);
        return Task.FromResult(Edges.Where(e => e.OrganizationId == organizationId && e.SubjectTypeName == subjectTypeName && subjectEntityIds.Contains(e.SubjectEntityId) && e.ResourceTypeName == resourceTypeName && e.RelationshipName == relationshipName).ToList());
    }

    public Task<SearchAccessEdge?> FindEdgeAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, string subjectRelationName, CancellationToken cancellationToken)
    {
        Require(organizationId);
        return Task.FromResult(Edges.SingleOrDefault(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && e.ResourceEntityId == resourceEntityId && e.RelationshipName == relationshipName
                                                          && e.SubjectTypeName == subjectTypeName && e.SubjectEntityId == subjectEntityId && e.SubjectRelationName == subjectRelationName));
    }

    public void AddEdge(SearchAccessEdge edge)
    {
        Require(edge.OrganizationId);
        if (Edges.Any(e => e.OrganizationId == edge.OrganizationId && e.ResourceTypeName == edge.ResourceTypeName && e.ResourceEntityId == edge.ResourceEntityId && e.RelationshipName == edge.RelationshipName
                           && e.SubjectTypeName == edge.SubjectTypeName && e.SubjectEntityId == edge.SubjectEntityId && e.SubjectRelationName == edge.SubjectRelationName))
        {
            throw new InvalidOperationException("uq_search_access_edge_tuple");
        }

        Edges.Add(edge);
    }

    public void RemoveEdge(SearchAccessEdge edge) => Edges.Remove(edge);

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteGrantsOfRecordsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken)
    {
        Require(organizationId);
        RecordGrants.RemoveAll(g => g.OrganizationId == organizationId && formRecordIds.Contains(g.FormRecordId));
        FieldItemGrants.RemoveAll(g => g.OrganizationId == organizationId && formRecordIds.Contains(g.FormRecordId));
        return Task.CompletedTask;
    }

    public void AddRecordGrants(IEnumerable<SearchAccessRecordGrant> grants) => RecordGrants.AddRange(grants.Select(g => { Require(g.OrganizationId); return g; }));

    public void AddFieldItemGrants(IEnumerable<SearchAccessFieldItemGrant> grants) => FieldItemGrants.AddRange(grants.Select(g => { Require(g.OrganizationId); return g; }));

    public Task PurgeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        Require(organizationId);
        Edges.RemoveAll(e => e.OrganizationId == organizationId);
        RecordGrants.RemoveAll(g => g.OrganizationId == organizationId);
        FieldItemGrants.RemoveAll(g => g.OrganizationId == organizationId);
        return Task.CompletedTask;
    }

    public Task<List<SearchAccessEdge>> GetAllEdgesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        Require(organizationId);
        return Task.FromResult(Edges.Where(e => e.OrganizationId == organizationId).ToList());
    }

    public Task<List<SearchAccessRecordGrant>> GetRecordGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        Require(organizationId);
        return Task.FromResult(RecordGrants.Where(g => g.OrganizationId == organizationId && (formRecordIds is null || formRecordIds.Contains(g.FormRecordId))).ToList());
    }

    public Task<List<SearchAccessFieldItemGrant>> GetFieldItemGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        Require(organizationId);
        return Task.FromResult(FieldItemGrants.Where(g => g.OrganizationId == organizationId && (formRecordIds is null || formRecordIds.Contains(g.FormRecordId))).ToList());
    }

    private void Require(Guid organizationId)
    {
        if (bound != organizationId)
        {
            throw new InvalidOperationException("row-level security: the operation does not target the bound organization");
        }
    }

    private static SearchAccessCheckpoint Copy(SearchAccessCheckpoint c) => new()
    {
        SearchAccessCheckpointId = c.SearchAccessCheckpointId,
        OrganizationId = c.OrganizationId,
        AccessProjectionStateName = c.AccessProjectionStateName,
        ConsumedAuthorizationRevision = c.ConsumedAuthorizationRevision,
        ConsumedJournalDigest = c.ConsumedJournalDigest,
        CheckpointRevision = c.CheckpointRevision,
        AccessProjectionProtocolVersion = c.AccessProjectionProtocolVersion,
        QuarantineReasonCode = c.QuarantineReasonCode,
        CheckpointModifiedOn = c.CheckpointModifiedOn,
    };
}
