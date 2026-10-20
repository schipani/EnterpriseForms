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
public sealed record LegacyPlaintextRecord(Guid FormRecordId, int PlaintextValueCount);

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

    // Shared option lists (Phase C2): tenant-scoped templates copied into fields on bind.
    Task<List<OptionList>> GetOptionListsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Tracked lookup (the list is edited through it).</summary>
    Task<OptionList?> FindOptionListAsync(Guid tenantId, Guid optionListId, CancellationToken cancellationToken);

    /// <summary>Tracked items of one list ordered by sort order then key.</summary>
    Task<List<OptionListItem>> GetOptionListItemsAsync(Guid optionListId, CancellationToken cancellationToken);

    /// <summary>Items of every listed list (untracked), for the list catalog.</summary>
    Task<List<OptionListItem>> GetOptionListItemsAsync(IReadOnlyCollection<Guid> optionListIds, CancellationToken cancellationToken);

    /// <summary>How many fields were created from each list (informational, shown to designers).</summary>
    Task<Dictionary<Guid, int>> CountFieldsPerOptionListAsync(IReadOnlyCollection<Guid> optionListIds, CancellationToken cancellationToken);

    void AddOptionList(OptionList optionList);

    void AddOptionListItem(OptionListItem item);

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

    /// <summary>Records whose latest committed snapshot still holds plaintext rows of the field, with the number of such rows (projection estimate).</summary>
    Task<List<LegacyPlaintextRecord>> GetLegacyPlaintextRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken);

    /// <summary>Plaintext rows of the field inside one operation's staged snapshot (used with the authority-accepted head operation for the verified post-check).</summary>
    Task<int> CountPlaintextRowsInOperationAsync(Guid fieldDefinitionId, Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    /// <summary>Records whose latest staged snapshot still holds encrypted rows of the field (projection estimate for a decrypt migration).</summary>
    Task<List<LegacyPlaintextRecord>> GetLegacyEncryptedRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken);

    Task<int> CountLegacyEncryptedRecordsAsync(Guid fieldDefinitionId, CancellationToken cancellationToken);

    Task<int> CountEncryptedRowsInOperationAsync(Guid fieldDefinitionId, Guid formRecordId, Guid contentChangeOperationId, CancellationToken cancellationToken);

    void AddProtectionMigration(FieldProtectionMigration migration);

    void AddProtectionMigrationItem(FieldProtectionMigrationItem item);

    Task<FieldProtectionMigration?> FindProtectionMigrationAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken);

    /// <summary>The latest migration of a field (highest protection policy revision), tracked.</summary>
    Task<FieldProtectionMigration?> FindLatestProtectionMigrationAsync(Guid fieldDefinitionId, CancellationToken cancellationToken);

    Task<FieldProtectionMigration?> LeaseNextProtectionMigrationAsync(string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<List<FieldProtectionMigrationItem>> GetPendingProtectionMigrationItemsAsync(Guid fieldProtectionMigrationId, int limit, CancellationToken cancellationToken);

    Task<FieldProtectionMigrationItem?> FindProtectionMigrationItemAsync(Guid fieldProtectionMigrationItemId, CancellationToken cancellationToken);

    Task<HashSet<Guid>> GetProtectionMigrationItemRecordIdsAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken);

    /// <summary>Every business record of a form (migration post-check scope; untrusted list, verified record-by-record against the authority).</summary>
    Task<List<Guid>> GetFormRecordIdsOfFormAsync(Guid formDefinitionId, CancellationToken cancellationToken);

    Task<List<FieldProtectionMigrationItem>> GetFailedProtectionMigrationItemsAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken);

    Task<int> CountPendingProtectionMigrationItemsAsync(Guid fieldProtectionMigrationId, CancellationToken cancellationToken);

    Task<UserUiPreference?> FindUiPreferenceAsync(Guid userId, CancellationToken cancellationToken);

    void AddUiPreference(UserUiPreference preference);

    Task CommitAsync(CancellationToken cancellationToken);
}
