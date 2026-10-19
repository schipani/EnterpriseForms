namespace EnterpriseForms.Contracts.Features;

// Stage B business wire contracts (/api/v1). Values travel as invariant strings per field data type
// (ISO date, invariant decimal, "true"/"false", option key); the server validates and stores typed rows.

// ---- Session / bootstrap (BFF, B-01) ----

public sealed record DevelopmentPersonaResponse(string PersonaKey, string DisplayName, Guid OrganizationId, string OrganizationDisplayName, IReadOnlyList<string> OrganizationRoles);

public sealed record DevelopmentLoginRequest(string PersonaKey);

public sealed record SessionResponse(bool Authenticated, Guid? OrganizationId, string? OrganizationDisplayName, Guid? UserId, string? DisplayName, IReadOnlyList<string> Capabilities, string CsrfToken, DateTimeOffset? ExpiresOn, string IdentityProviderKind);

public sealed record BootstrapResponse(SessionResponse Session, BrandingResponse Branding, UiPreferenceResponse Preferences, long AuthorizationRevision);

public sealed record BrandingResponse(string OrganizationDisplayName, bool HasLogo, string? LogoAlternativeText, string PrimaryButtonBackgroundColor, string PrimaryButtonForegroundColor, string SecondaryButtonBackgroundColor, string SecondaryButtonForegroundColor, string SecondaryButtonBorderColor, decimal OrganizationDefaultTextScalePercent, string OrganizationColorMode, long BrandingRevision);

public sealed record UpdateBrandingRequest(long ExpectedBrandingRevision, string OrganizationDisplayName, string? LogoAlternativeText, string PrimaryButtonBackgroundColor, string PrimaryButtonForegroundColor, string SecondaryButtonBackgroundColor, string SecondaryButtonForegroundColor, string SecondaryButtonBorderColor, decimal OrganizationDefaultTextScalePercent, string OrganizationColorMode);

public sealed record UiPreferenceResponse(string TextScalePreferenceMode, decimal? ExplicitTextScalePercent, decimal EffectiveTextScalePercent, long PreferenceRevision);

public sealed record UpdateUiPreferenceRequest(string TextScalePreferenceMode, decimal? ExplicitTextScalePercent);

public sealed record DirectoryUserResponse(Guid UserId, string DisplayName);

// ---- Form definitions / designer (B-03) ----

public sealed record FormDefinitionSummaryResponse(Guid FormDefinitionId, string FormDefinitionName, string FormDefinitionDescription, string LifecycleStatus, int? CurrentVersionNumber, Guid? CurrentFormVersionId, bool HasDraft, bool CanDesign, bool CanCreateRecord, DateTimeOffset ModifiedOn);

public sealed record FieldDefinitionResponse(Guid FieldDefinitionId, string FieldKey, string FieldDisplayName, string FieldDescription, string FieldDataType, string FieldClassification, string LifecycleStatus, IReadOnlyList<FieldOptionResponse> Options, long FieldDefinitionRevision = 1, string FieldProtectionMode = "Standard", long ProtectionPolicyRevision = 1);

public sealed record FieldOptionResponse(Guid FieldOptionId, string OptionKey, string OptionDisplayText, int OptionSortOrder, bool IsActive);

public sealed record FormVersionResponse(Guid FormVersionId, int VersionNumber, int LayoutSchemaVersion, string LayoutJson, string ChangeReason, Guid? RestoredFromFormVersionId, DateTimeOffset PublishedOn, Guid PublishedByUserId);

public sealed record FormDraftResponse(Guid FormDraftId, Guid? BasedOnFormVersionId, int LayoutSchemaVersion, string LayoutJson, long DraftRevision, DateTimeOffset UpdatedOn);

public sealed record RelationshipDefinitionResponse(Guid FormRelationshipDefinitionId, Guid ParentFormDefinitionId, Guid ChildFormDefinitionId, string ChildFormDefinitionName, string RelationshipDefinitionKey, string RelationshipDisplayName, int MinimumChildCount, int MaximumChildCount, string LifecycleStatus);

public sealed record FormDefinitionDetailResponse(
    FormDefinitionSummaryResponse Summary,
    IReadOnlyList<FieldDefinitionResponse> Fields,
    FormVersionResponse? CurrentVersion,
    FormDraftResponse? Draft,
    IReadOnlyList<RelationshipDefinitionResponse> Relationships);

public sealed record CreateFormDefinitionV2Request(string FormDefinitionName, string FormDefinitionDescription, IReadOnlyList<Guid> FormUserUserIds, IReadOnlyList<Guid> FormUserTeamIds);

/// <summary>
/// Edits the catalog metadata of an existing field. <c>FieldKey</c> and <c>FieldDataType</c> are accepted only so a client can
/// state what it believes they are: any value that differs from the stored identity is refused (the key and storage type are
/// permanent; use a new field instead). Labels, help text and validation of a form version live in the layout draft.
/// </summary>
public sealed record UpdateFieldDefinitionRequest(string FieldDisplayName, string? FieldDescription, long ExpectedFieldRevision, string? FieldKey = null, string? FieldDataType = null);

/// <summary>Delta of who may create records for a form (authority form_user edges). Independent of form versions.</summary>
public sealed record UpdateFormAccessRequest(IReadOnlyList<Guid> GrantUserIds, IReadOnlyList<Guid> GrantTeamIds, IReadOnlyList<Guid> RevokeUserIds, IReadOnlyList<Guid> RevokeTeamIds, string ChangeReason);

/// <summary>A principal in a form's access state; the display name comes from the local directory projection and confers nothing.</summary>
public sealed record FormAccessSubjectResponse(string SubjectKind, Guid SubjectId, string DisplayName);

/// <summary>Authoritative form access read from the independent Authorization Authority (not from business tables).</summary>
public sealed record FormAccessResponse(Guid FormDefinitionId, long AuthorizationRevision, string Source, IReadOnlyList<FormAccessSubjectResponse> Designers, IReadOnlyList<FormAccessSubjectResponse> FormUsers, IReadOnlyList<DirectoryTeamResponse> AvailableTeams);

public sealed record DirectoryTeamResponse(Guid TeamId, string TeamName);

/// <summary>Classification defaults to Confidential for String/File and Internal otherwise; protection defaults to ApplicationEncrypted for Confidential/Restricted and for String, and may be requested explicitly for Public/Internal. Plaintext requires the organization policy to permit it.</summary>
public sealed record AddFieldDefinitionRequest(string FieldKey, string FieldDisplayName, string FieldDescription, string FieldDataType, IReadOnlyList<CreateFieldOptionRequest>? Options, string? FieldClassification = null, string? FieldProtectionMode = null);

/// <summary>Strengthening only (higher classification and/or Standard → ApplicationEncrypted); a reason is recorded with the audit event.</summary>
public sealed record StrengthenFieldProtectionRequest(string? TargetClassification, string? TargetProtectionMode, string ChangeReason, long ExpectedProtectionRevision);

/// <summary>Protection state of a field. <c>MigrationState</c>: Plaintext, Encrypted, or EncryptionPendingRewrite (records still carrying a legacy plaintext copy: <c>LegacyPlaintextRecordCount</c>, a business-table estimate).</summary>
public sealed record FieldProtectionResponse(Guid FieldDefinitionId, string FieldKey, string FieldClassification, string FieldProtectionMode, long ProtectionPolicyRevision, DateTimeOffset? ProtectionChangedOn, string MigrationState, int LegacyPlaintextRecordCount, string SearchImpact, string OrganizationMinimumClassification, bool OrganizationPermitsPlaintext,
    bool FullyEncrypted = false, FieldProtectionMigrationResponse? Migration = null, bool FullyPlaintext = false, int LegacyEncryptedRecordCount = 0);

/// <summary>
/// Progress of the background plaintext→encrypted migration of a field (B-28). <c>MigrationState</c>: Queued, Running,
/// Blocked (dependency unavailable, retried), Failed (bounded attempts exhausted for some records; retry available),
/// Completed (every record re-protected through the authority and the post-check found no plaintext copy).
/// </summary>
public sealed record FieldProtectionMigrationResponse(Guid FieldProtectionMigrationId, string MigrationState, int TotalRecordCount, int TotalValueCount, int CompletedRecordCount, int FailedRecordCount, int PendingRecordCount, string? LastErrorCode, DateTimeOffset StartedOn, DateTimeOffset? CompletedOn, DateTimeOffset? VerifiedOn, Guid AuthorityCommandId,
    string MigrationDirection = "Encrypt", DateTimeOffset? CancelledOn = null, Guid? RollbackOfMigrationId = null);

public sealed record RetryFieldProtectionMigrationRequest(string RetryReason);

/// <summary>Amendment A1 step 1 (organization owner / system administrator): declassify an encrypted Confidential/Restricted field to Public or Internal; the governance approval reference is journaled by the authority.</summary>
public sealed record ReclassifyFieldRequest(string TargetClassification, string ApprovalReference, string ChangeReason);

/// <summary>Amendment A1 step 2 (a distinct organization owner / system administrator for sensitive fields): release the field from application encryption and start the audited decryption migration.</summary>
public sealed record DowngradeFieldProtectionRequest(string ChangeReason, long ExpectedProtectionRevision);

/// <summary>Amendment A1 rollback: re-require encryption; already decrypted active values are re-encrypted by a new migration. Historical plaintext revisions, logs, WAL and backups are not reversible.</summary>
public sealed record CancelFieldProtectionDowngradeRequest(string ChangeReason);

public sealed record OrganizationSecurityPolicyResponse(string MinimumFieldClassification, bool PlaintextInternalFieldsPermitted, string PolicyStatementText, DateTimeOffset? PolicyReviewedOn, Guid? PolicyReviewedByUserId, long SecurityPolicyRevision, bool IsUnreviewedDefault, bool PlaintextDowngradePermitted = false, Guid? AuthorityCommandId = null);

public sealed record UpdateOrganizationSecurityPolicyRequest(string MinimumFieldClassification, bool PlaintextInternalFieldsPermitted, string PolicyStatementText, long ExpectedPolicyRevision, bool PlaintextDowngradePermitted = false);

public sealed record CreateFieldOptionRequest(string OptionKey, string OptionDisplayText);

public sealed record UpdateFieldOptionsRequest(IReadOnlyList<FieldOptionChange> Changes);

/// <summary>Add (FieldOptionId null) or update display text/active flag/sort of an existing option; options are never deleted.</summary>
public sealed record FieldOptionChange(Guid? FieldOptionId, string OptionKey, string OptionDisplayText, int OptionSortOrder, bool IsActive);

public sealed record SaveDraftRequest(long ExpectedDraftRevision, string LayoutJson);

public sealed record PublishFormRequest(long ExpectedDraftRevision, int ExpectedCurrentVersionNumber, string ChangeReason);

public sealed record RestoreVersionRequest(Guid FromFormVersionId);

public sealed record CreateRelationshipDefinitionRequest(Guid ChildFormDefinitionId, string RelationshipDefinitionKey, string RelationshipDisplayName, int MinimumChildCount, int MaximumChildCount);

// ---- Records (B-02, B-04, B-07) ----

public sealed record CreateRecordV2Request(IReadOnlyDictionary<string, string> Values, Guid? OwnerTeamId = null);

public sealed record RecordListItemResponse(Guid FormRecordId, Guid FormDefinitionId, string FormDefinitionName, string RecordStatus, DateTimeOffset CreatedOn, DateTimeOffset ModifiedOn, Guid? ParentFormRecordId, bool ProjectionVerified);

public sealed record RecordListResponse(IReadOnlyList<RecordListItemResponse> Items, long AuthorizationRevision, string? ContinuationToken);

public sealed record RecordProjectionResponse(
    FormRecordResponse Record,
    string FormDefinitionName,
    FormVersionResponse LayoutVersion,
    IReadOnlyList<FieldDefinitionResponse> Fields,
    IReadOnlyList<DocumentSummaryResponse> Documents,
    IReadOnlyList<RelationshipDefinitionResponse> Relationships,
    RecordPermissionsResponse Permissions,
    Guid? ParentFormRecordId,
    Guid? FormRelationshipDefinitionId,
    string ETag);

public sealed record RecordPermissionsResponse(bool CanRead, bool CanEdit, bool CanManageAccess, bool CanTransferOwnership, bool IsCompleteProjection);

public sealed record CreateChildRecordRequest(Guid FormRelationshipDefinitionId, IReadOnlyDictionary<string, string> Values);

public sealed record ChildRecordListResponse(RelationshipDefinitionResponse Relationship, IReadOnlyList<ChildRecordSummaryResponse> Items, long AuthorizationRevision);

public sealed record ChildRecordSummaryResponse(Guid FormRecordId, long ListSortOrder, string RecordStatus, long ContentRevision, IReadOnlyList<FieldValueResponse> SummaryValues, string IntegrityStatus);

public sealed record UnlinkChildRecordRequest(string RemovalReason);

public sealed record AuditEventResponse(Guid AuditEventId, DateTimeOffset EventOccurredOn, string EventCategory, string EventAction, string EventOutcome, Guid? ActorUserId, string? ActorDisplayName, string EventDescription, Guid CorrelationIdentifier, Guid? FileAttachmentId, Guid? TargetEntityId);

public sealed record RecordActivityResponse(Guid FormRecordId, IReadOnlyList<AuditEventResponse> Events, string TrustNote);

// ---- Documents (B-05) ----

public sealed record BeginUploadRequest(Guid FormRecordId, Guid FieldDefinitionId, string OriginalFileName, string ContentType, long SizeBytes, Guid? ReplacesFileAttachmentId = null);

public sealed record UploadSessionResponse(Guid UploadSessionId, Guid FormRecordId, Guid FieldDefinitionId, string OriginalFileName, long DeclaredSizeBytes, int ChunkSizeBytes, int ExpectedChunkCount, IReadOnlyList<int> ReceivedChunkIndexes, long ReceivedBytes, string SessionState, string? FailureReasonCode, string ProcessingStage, DateTimeOffset SessionExpiresOn, Guid? FileAttachmentId, int? VersionNumber, string? ScanVerdict);

public sealed record UploadChunkReceiptResponse(Guid UploadSessionId, int ChunkIndex, long ReceivedBytes, IReadOnlyList<int> ReceivedChunkIndexes, bool Complete);

public sealed record DocumentSummaryResponse(Guid FileAttachmentId, Guid FieldDefinitionId, string OriginalFileName, int CurrentVersionNumber, string DetectedContentType, long PlaintextSizeBytes, string ScanVerdict, string AttachmentLifecycleState, DateTimeOffset UploadedOn, Guid UploadedByUserId, bool InAcceptedManifest);

public sealed record DocumentVersionResponse(Guid FileAttachmentVersionId, int VersionNumber, string OriginalFileName, string DetectedContentType, long PlaintextSizeBytes, string ScanVerdict, string ScannerName, DateTimeOffset UploadedOn, Guid UploadedByUserId, string VersionState, long? ContentRevision);

public sealed record DocumentDetailResponse(DocumentSummaryResponse Document, IReadOnlyList<DocumentVersionResponse> Versions);

public sealed record RemoveDocumentRequest(long ExpectedContentRevision, string RemovalReason);

// ---- Dashboard ----

public sealed record DashboardWidgetDefinitionResponse(string WidgetIdentifier, string WidgetTitle, string WidgetType, int MinimumColumnSpan, string DataEndpointKey);

public sealed record DashboardResponse(IReadOnlyList<DashboardWidgetDefinitionResponse> Widgets, IReadOnlyList<string> Capabilities);

public sealed record DashboardCountResponse(string WidgetIdentifier, long Count, DateTimeOffset ComputedOn, string Scope);

/// <summary>One share of a record as projected locally; the authority remains the only permission source.</summary>
public sealed record RecordShareSummaryResponse(Guid RecordShareId, Guid FormRecordId, Guid SharedByUserId, string? SharedByDisplayName, Guid? RecipientUserId, string? RecipientDisplayName, Guid? RecipientTeamId, string RecipientRole, string ShareScope, string ShareStatus, long ShareRevision, DateTimeOffset ShareCreatedOn, DateTimeOffset? ShareRevokedOn, IReadOnlyList<string> FieldKeys);

public sealed record RecordShareListResponse(IReadOnlyList<RecordShareSummaryResponse> Items, long AuthorizationRevision);
