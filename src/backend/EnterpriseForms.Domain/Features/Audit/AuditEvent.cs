namespace EnterpriseForms.Domain.Features.Audit;

public static class AuditActions
{
    public const string FormCreated = "FORM_CREATED";
    public const string FormDraftUpdated = "FORM_DRAFT_UPDATED";
    public const string FormVersionPublished = "FORM_VERSION_PUBLISHED";
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
