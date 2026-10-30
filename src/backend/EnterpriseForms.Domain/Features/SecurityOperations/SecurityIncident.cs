namespace EnterpriseForms.Domain.Features.SecurityOperations;

/// <summary>What the business API observed. A type never asserts intent: a confirmed mismatch is not proof of DBA activity.</summary>
public static class SecurityIncidentTypes
{
    /// <summary>The Authority verified the staged rows against its accepted manifest and they did not match (it quarantined the record and journaled the failure).</summary>
    public const string ConfirmedIntegrityMismatch = "ConfirmedIntegrityMismatch";

    /// <summary>A read was refused because the Authority already holds the record in integrity quarantine.</summary>
    public const string IntegrityQuarantine = "IntegrityQuarantine";

    /// <summary>The Authority, its key provider or the connection to it was unavailable, so nothing could be verified (fail closed).</summary>
    public const string VerificationServiceUnavailable = "VerificationServiceUnavailable";

    /// <summary>Verification could not be completed for another operational reason (for example a missing committed header).</summary>
    public const string OperationalVerificationFailure = "OperationalVerificationFailure";

    public static readonly IReadOnlyList<string> All = [ConfirmedIntegrityMismatch, IntegrityQuarantine, VerificationServiceUnavailable, OperationalVerificationFailure];
}

public static class SecurityIncidentSeverities
{
    public const string Critical = "Critical";
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";

    public static readonly IReadOnlyList<string> All = [Critical, High, Medium, Low];

    public static string For(string incidentTypeName) => incidentTypeName switch
    {
        SecurityIncidentTypes.ConfirmedIntegrityMismatch => Critical,
        SecurityIncidentTypes.IntegrityQuarantine => High,
        _ => Medium,
    };
}

public static class SecurityIncidentStatuses
{
    public const string Open = "Open";
    public const string Investigating = "Investigating";
    public const string Resolved = "Resolved";
    public const string FalsePositive = "FalsePositive";

    public static readonly IReadOnlyList<string> All = [Open, Investigating, Resolved, FalsePositive];

    /// <summary>Statuses that still collect new detections of the same problem (deduplication).</summary>
    public static bool IsActive(string status) => status is Open or Investigating;

    public static bool IsClosed(string status) => status is Resolved or FalsePositive;
}

public static class SecurityIncidentDetectionSources
{
    public const string RecordRead = "RecordRead";
    public const string RecordVersionRead = "RecordVersionRead";
    public const string DocumentVersionDownload = "DocumentVersionDownload";
}

/// <summary>
/// Triage record of a verification problem observed by the business API (SecurityOperations, PG-056). Untrusted
/// projection class: it lives in business PostgreSQL, so a business-database administrator can alter or delete it.
/// The independent evidence of a confirmed mismatch is the Authority's hash-chained, archived journal entry
/// (IntegrityVerificationFailed) together with its record quarantine; this row only makes that observable and
/// investigable. Contains no record content, field values, file names, keys or signatures.
/// </summary>
public sealed class SecurityIncident
{
    public Guid SecurityIncidentId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? FormRecordId { get; set; }

    public string IncidentTypeName { get; set; } = string.Empty;

    public string IncidentSeverityName { get; set; } = SecurityIncidentSeverities.Medium;

    public string IncidentStatusName { get; set; } = SecurityIncidentStatuses.Open;

    /// <summary>Groups repeated detections of one problem (for example "integrity:{record}"); never shown to users.</summary>
    public string IncidentDeduplicationKey { get; set; } = string.Empty;

    public DateTimeOffset FirstDetectedOn { get; set; }

    public DateTimeOffset LastDetectedOn { get; set; }

    public int OccurrenceCount { get; set; } = 1;

    public string DetectionSourceName { get; set; } = string.Empty;

    public string VerificationErrorCode { get; set; } = string.Empty;

    /// <summary>Correlation identifier of the first detection; it also identifies the matching Authority journal entry.</summary>
    public Guid FirstCorrelationIdentifier { get; set; }

    public Guid? AssignedToUserId { get; set; }

    public string? ResolutionExplanationText { get; set; }

    public DateTimeOffset? ResolvedOn { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public long IncidentRevision { get; set; } = 1;

    public DateTimeOffset IncidentModifiedOn { get; set; }

    public Guid? IncidentModifiedByUserId { get; set; }
}

/// <summary>One detection (occurrence history). Repeated detections within a minute are counted but share one history row.</summary>
public sealed class SecurityIncidentOccurrence
{
    public Guid SecurityIncidentOccurrenceId { get; set; }

    public Guid SecurityIncidentId { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset OccurrenceDetectedOn { get; set; }

    public string ObservedIncidentTypeName { get; set; } = string.Empty;

    public string DetectionSourceName { get; set; } = string.Empty;

    public string VerificationErrorCode { get; set; } = string.Empty;

    public Guid? ObservedByUserId { get; set; }

    public Guid RequestCorrelationIdentifier { get; set; }

    public long? CommittedContentRevision { get; set; }
}

/// <summary>Append-only investigation note written by an authorized investigator. Investigators are told not to include record content.</summary>
public sealed class SecurityIncidentNote
{
    public const int MaximumNoteLength = 2000;

    public Guid SecurityIncidentNoteId { get; set; }

    public Guid SecurityIncidentId { get; set; }

    public Guid TenantId { get; set; }

    public Guid NoteAuthorUserId { get; set; }

    public DateTimeOffset NoteCreatedOn { get; set; }

    public string InvestigationNoteText { get; set; } = string.Empty;
}
