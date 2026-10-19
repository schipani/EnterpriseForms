using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Domain.Features.RelatedForms;
using EnterpriseForms.Domain.Features.Sharing;
using EnterpriseForms.Domain.Features.Teams;
using EnterpriseForms.Domain.Features.UserPreferences;

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

    Task<List<User>> GetUsersAsync(Guid tenantId, CancellationToken cancellationToken);

    // FormDefinitions / FormDesigner
    /// <summary>Version may be null for a designer-created form that is not published yet (B-03).</summary>
    void AddFormDefinition(FormDefinition definition, FormVersion? version, IEnumerable<FieldDefinition> fields);

    Task<FormDefinition?> FindFormDefinitionAsync(Guid tenantId, Guid formDefinitionId, CancellationToken cancellationToken);

    Task<List<FormDefinition>> GetFormDefinitionsAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<List<FieldDefinition>> GetFieldDefinitionsAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    void AddFieldDefinition(FieldDefinition field);

    /// <summary>Tracked lookup for catalog metadata edits (display name/description only).</summary>
    Task<FieldDefinition?> FindFieldDefinitionAsync(Guid formDefinitionId, Guid fieldDefinitionId, CancellationToken cancellationToken);

    Task<List<FieldOption>> GetFieldOptionsAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    void AddFieldOption(FieldOption fieldOption);

    Task<FormVersion?> FindFormVersionAsync(Guid formVersionId, CancellationToken cancellationToken);

    Task<List<FormVersion>> GetFormVersionsAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    void AddFormVersion(FormVersion version);

    Task<FormDraft?> FindDraftAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    void AddDraft(FormDraft draft);

    // RelatedForms
    Task<List<FormRelationshipDefinition>> GetRelationshipDefinitionsAsync(Guid tenantId, Guid? parentFormDefinitionId, CancellationToken cancellationToken);

    Task<FormRelationshipDefinition?> FindRelationshipDefinitionAsync(Guid tenantId, Guid formRelationshipDefinitionId, CancellationToken cancellationToken);

    void AddRelationshipDefinition(FormRelationshipDefinition definition);

    Task<List<FormRecordRelationship>> GetActiveChildLinksAsync(Guid parentFormRecordId, Guid formRelationshipDefinitionId, CancellationToken cancellationToken);

    Task<FormRecordRelationship?> FindActiveChildLinkAsync(Guid childFormRecordId, CancellationToken cancellationToken);

    void AddChildLink(FormRecordRelationship link);

    // FormRecords
    Task<FormRecord?> FindFormRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken);

    Task<List<FormRecord>> GetFormRecordsAsync(Guid tenantId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken);

    void AddFormRecord(FormRecord record);

    void StageCandidate(FormRecordRevision headerRevision, IEnumerable<ISignedContentRow> rows);

    Task<FormRecordRevision?> FindStagedHeaderAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    /// <summary>Every staged non-header row of the operation (typed values and attachment versions), ordered by item id.</summary>
    Task<List<ISignedContentRow>> GetStagedRowsAsync(Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    // Documents
    Task<UploadSession?> FindUploadSessionAsync(Guid tenantId, Guid uploadSessionId, CancellationToken cancellationToken);

    Task<UploadSession?> FindUploadSessionByIdempotencyAsync(Guid tenantId, Guid ownerUserId, string idempotencyKey, CancellationToken cancellationToken);

    Task<List<UploadSession>> GetUploadSessionsForUserAsync(Guid tenantId, Guid ownerUserId, IReadOnlyCollection<string> states, CancellationToken cancellationToken);

    /// <summary>
    /// Sessions whose plaintext staging object must be purged (tracked for update): unpurged and either still open/received past
    /// their expiry, or terminal (processed, committed, rejected, cancelled, failed, expired) for longer than the grace period.
    /// </summary>
    Task<List<UploadSession>> GetSessionsDueForStagingPurgeAsync(DateTimeOffset now, DateTimeOffset terminalBefore, int limit, CancellationToken cancellationToken);

    void AddUploadSession(UploadSession session);

    void AddProcessingJob(DocumentProcessingJob job);

    Task<DocumentProcessingJob?> LeaseNextProcessingJobAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<DocumentProcessingJob?> FindProcessingJobAsync(Guid uploadSessionId, CancellationToken cancellationToken);

    Task<FileAttachment?> FindAttachmentAsync(Guid tenantId, Guid fileAttachmentId, CancellationToken cancellationToken);

    Task<List<FileAttachment>> GetAttachmentsAsync(Guid formRecordId, CancellationToken cancellationToken);

    void AddAttachment(FileAttachment attachment);

    Task<FileAttachmentVersion?> FindAttachmentVersionAsync(Guid fileAttachmentVersionId, CancellationToken cancellationToken);

    Task<List<FileAttachmentVersion>> GetAttachmentVersionsAsync(Guid fileAttachmentId, CancellationToken cancellationToken);

    void AddAttachmentVersion(FileAttachmentVersion version);

    // Sharing projections
    void AddShareProjection(RecordShare share, IEnumerable<RecordShareField> fields);

    Task<RecordShare?> FindShareProjectionAsync(Guid tenantId, Guid recordShareId, CancellationToken cancellationToken);

    Task<List<RecordShare>> GetSharesOfRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken);

    Task<List<RecordShareField>> GetShareFieldsOfRecordAsync(Guid tenantId, Guid formRecordId, CancellationToken cancellationToken);

    // Teams projections
    void AddTeamProjection(Team team);

    Task<Team?> FindTeamProjectionAsync(Guid tenantId, Guid teamId, CancellationToken cancellationToken);

    Task<List<Team>> GetTeamsAsync(Guid tenantId, CancellationToken cancellationToken);

    void AddTeamMembershipProjection(TeamMembership membership);

    Task<TeamMembership?> FindActiveTeamMembershipProjectionAsync(Guid tenantId, Guid teamId, Guid userId, CancellationToken cancellationToken);

    // Audit projection
    void AddAuditEvent(AuditEvent auditEvent);

    Task<List<AuditEvent>> GetRecordAuditEventsAsync(Guid tenantId, Guid formRecordId, int limit, CancellationToken cancellationToken);

    Task<List<AuditEvent>> GetActorAuditEventsAsync(Guid tenantId, Guid actorUserId, int limit, CancellationToken cancellationToken);

    // Organizations / preferences
    Task<OrganizationBranding?> FindBrandingAsync(Guid tenantId, CancellationToken cancellationToken);

    void AddBranding(OrganizationBranding branding);

    Task<OrganizationSecurityPolicy?> FindSecurityPolicyAsync(Guid tenantId, CancellationToken cancellationToken);

    void AddSecurityPolicy(OrganizationSecurityPolicy policy);

    Task<int> CountLegacyPlaintextRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken);

    Task<UserUiPreference?> FindUiPreferenceAsync(Guid userId, CancellationToken cancellationToken);

    void AddUiPreference(UserUiPreference preference);

    Task CommitAsync(CancellationToken cancellationToken);
}
