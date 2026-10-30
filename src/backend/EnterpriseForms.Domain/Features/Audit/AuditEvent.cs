namespace EnterpriseForms.Domain.Features.Audit;

public static class AuditActions
{
    public const string FormCreated = "FORM_CREATED";
    public const string FormDraftUpdated = "FORM_DRAFT_UPDATED";
    public const string FormVersionPublished = "FORM_VERSION_PUBLISHED";
    public const string FormRenamed = "FORM_RENAMED";
    public const string FieldConfigurationChanged = "FIELD_CONFIGURATION_CHANGED";
    public const string FieldOptionsChanged = "FIELD_OPTIONS_CHANGED";
    public const string OptionListChanged = "OPTION_LIST_CHANGED";
    public const string FieldProtectionStrengthened = "FIELD_PROTECTION_STRENGTHENED";
    public const string FieldProtectionMigrationStarted = "FIELD_PROTECTION_MIGRATION_STARTED";
    public const string FieldValuesReprotected = "FIELD_VALUES_REPROTECTED";
    public const string FieldProtectionMigrationRecordFailed = "FIELD_PROTECTION_MIGRATION_RECORD_FAILED";
    public const string FieldProtectionMigrationCompleted = "FIELD_PROTECTION_MIGRATION_COMPLETED";
    public const string FieldProtectionMigrationFailed = "FIELD_PROTECTION_MIGRATION_FAILED";
    public const string FieldProtectionMigrationRetried = "FIELD_PROTECTION_MIGRATION_RETRIED";
    public const string FieldProtectionReconciliationRequested = "FIELD_PROTECTION_RECONCILIATION_REQUESTED";
    public const string FieldProtectionReconciliationAttested = "FIELD_PROTECTION_RECONCILIATION_ATTESTED";
    public const string FieldDeclassified = "FIELD_DECLASSIFIED";
    public const string FieldProtectionDowngraded = "FIELD_PROTECTION_DOWNGRADED";
    public const string FieldValuesDeprotected = "FIELD_VALUES_DEPROTECTED";
    public const string FieldProtectionDowngradeCancelled = "FIELD_PROTECTION_DOWNGRADE_CANCELLED";
    public const string OrganizationSecurityPolicyUpdated = "ORGANIZATION_SECURITY_POLICY_UPDATED";
    public const string FormAccessGranted = "FORM_ACCESS_GRANTED";
    public const string FormAccessRevoked = "FORM_ACCESS_REVOKED";
    public const string RecordCreated = "RECORD_CREATED";
    public const string RecordViewed = "RECORD_VIEWED";
    public const string RecordUpdated = "RECORD_UPDATED";
    public const string RecordSubmitted = "RECORD_SUBMITTED";
    public const string RecordReopened = "RECORD_REOPENED";
    public const string RecordArchived = "RECORD_ARCHIVED";
    public const string RecordUnarchived = "RECORD_UNARCHIVED";
    public const string RecordLifecycleDenied = "RECORD_LIFECYCLE_DENIED";
    public const string RecordVersionViewed = "RECORD_VERSION_VIEWED";
    public const string RecordVersionRestored = "RECORD_VERSION_RESTORED";
    public const string ChildRecordLinked = "CHILD_RECORD_LINKED";
    public const string ChildRecordUnlinked = "CHILD_RECORD_UNLINKED";
    public const string DocumentUploadStarted = "DOCUMENT_UPLOAD_STARTED";
    public const string DocumentProcessed = "DOCUMENT_PROCESSED";
    public const string DocumentRejected = "DOCUMENT_REJECTED";
    public const string DocumentReady = "DOCUMENT_READY";
    public const string DocumentDownloaded = "DOCUMENT_DOWNLOADED";
    public const string DocumentRemoved = "DOCUMENT_REMOVED";
    public const string RecordAccessGranted = "RECORD_ACCESS_GRANTED";
    public const string RecordAccessRevoked = "RECORD_ACCESS_REVOKED";
    public const string RecordOwnershipTransferred = "RECORD_OWNERSHIP_TRANSFERRED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string FieldSearchApproved = "FIELD_SEARCH_APPROVED";
    public const string SecurityAdministratorAppointed = "SECURITY_ADMINISTRATOR_APPOINTED";
    public const string SecurityAdministratorRemoved = "SECURITY_ADMINISTRATOR_REMOVED";
    public const string FieldSearchWithdrawn = "FIELD_SEARCH_WITHDRAWN";
    public const string FieldSearchersChanged = "FIELD_SEARCHERS_CHANGED";
    public const string FormVersionAuthorityRegistered = "FORM_VERSION_AUTHORITY_REGISTERED";
    public const string FormVersionAuthorityRegistrationFailed = "FORM_VERSION_AUTHORITY_REGISTRATION_FAILED";

    // Phase C8 M1 (F-C7-03 and D8-8): organization logo and display-only team rename.
    public const string OrganizationLogoUploaded = "ORGANIZATION_LOGO_UPLOADED";
    public const string OrganizationLogoReplaced = "ORGANIZATION_LOGO_REPLACED";
    public const string TeamRenamed = "TEAM_RENAMED";

    // Phase C8 (C8-M1-01, D8-17): reconciler failures and operator actions. Service actor; the initiator is in metadata only.
    public const string TeamMembershipReconciliationFailed = "TEAM_MEMBERSHIP_RECONCILIATION_FAILED";
    public const string TeamMembershipProjectionDrift = "TEAM_MEMBERSHIP_PROJECTION_DRIFT";
    public const string TeamMembershipOperationRechecked = "TEAM_MEMBERSHIP_OPERATION_RECHECKED";
    public const string TeamMembershipOperationReviewed = "TEAM_MEMBERSHIP_OPERATION_REVIEWED";

    // Security monitoring (PG-056): incident triage store. Detection is recorded with the observing request's actor;
    // investigation changes with the investigator. Never record content, values, file names or keys.
    public const string SecurityIncidentDetected = "SECURITY_INCIDENT_DETECTED";
    public const string SecurityIncidentViewed = "SECURITY_INCIDENT_VIEWED";
    public const string SecurityIncidentStatusChanged = "SECURITY_INCIDENT_STATUS_CHANGED";
    public const string SecurityIncidentAssigned = "SECURITY_INCIDENT_ASSIGNED";
    public const string SecurityIncidentNoteAdded = "SECURITY_INCIDENT_NOTE_ADDED";
}

/// <summary>
/// Queryable local audit projection (ADR-SEC-2026-10-16-01: a projection, not the trusted journal). Append-only for the
/// runtime role; never carries protected values, secrets or search terms.
/// </summary>
public sealed class AuditEvent
{
    public Guid AuditEventId { get; set; }

    public Guid TenantId { get; set; }

    public long AuditSequence { get; set; }

    public DateTimeOffset EventOccurredOn { get; set; }

    public string EventCategory { get; set; } = string.Empty;

    public string EventAction { get; set; } = string.Empty;

    public string EventOutcome { get; set; } = "Success";

    public Guid? ActorUserId { get; set; }

    public string ActorType { get; set; } = "User";

    public Guid? FormDefinitionId { get; set; }

    public Guid? FormRecordId { get; set; }

    public Guid? FieldDefinitionId { get; set; }

    public Guid? FileAttachmentId { get; set; }

    public string? TargetEntityType { get; set; }

    public Guid? TargetEntityId { get; set; }

    public Guid CorrelationIdentifier { get; set; }

    public string EventDescription { get; set; } = string.Empty;

    public string EventMetadataJson { get; set; } = "{}";

    public int EventSchemaVersion { get; set; } = 1;
}
