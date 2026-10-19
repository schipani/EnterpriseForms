using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Domain.Features.Teams;

namespace EnterpriseForms.Application.Common.Persistence;

/// <summary>
/// Business PostgreSQL store (untrusted for authorization). Feature handlers use only the methods of their owned
/// aggregate; cross-feature reads go through published application contracts (ADR-ARCH-001).
/// </summary>
public interface IBusinessStore
{
    Task<IBusinessScope> BeginScopeAsync(CancellationToken cancellationToken);
}

public interface IBusinessScope : IAsyncDisposable
{
    // Identity projections
    Task EnsureTenantAndUserProjectionAsync(Guid tenantId, Guid userId, string issuerUri, string subjectIdentifier, string displayName, CancellationToken cancellationToken);

    // FormDefinitions
    void AddFormDefinition(FormDefinition definition, FormVersion version, IEnumerable<FieldDefinition> fields);

    Task<FormDefinition?> FindFormDefinitionAsync(Guid tenantId, Guid formDefinitionId, CancellationToken cancellationToken);

    Task<List<FieldDefinition>> GetFieldDefinitionsAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    // FormRecords
    Task<FormRecord?> FindFormRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken);

    void AddFormRecord(FormRecord record);

    void StageCandidate(FormRecordRevision headerRevision, IEnumerable<FieldStringValue> values);

    Task<FormRecordRevision?> FindStagedHeaderAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    Task<List<FieldStringValue>> GetStagedValuesAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    // Sharing projections
    void AddShareProjection(RecordShare share, IEnumerable<RecordShareField> fields);

    Task<RecordShare?> FindShareProjectionAsync(Guid tenantId, Guid recordShareId, CancellationToken cancellationToken);

    // Teams projections
    void AddTeamProjection(Team team);

    Task<Team?> FindTeamProjectionAsync(Guid tenantId, Guid teamId, CancellationToken cancellationToken);

    void AddTeamMembershipProjection(TeamMembership membership);

    Task<TeamMembership?> FindActiveTeamMembershipProjectionAsync(Guid tenantId, Guid teamId, Guid userId, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}
