namespace EnterpriseForms.Contracts.Features;

// ---- Dashboard analytics and sections (PG-006 redesign) ----

/// <summary>One time bucket of the record-activity chart. <c>BucketStartDate</c> is a calendar date (yyyy-MM-dd) in <c>TimeZoneId</c>.</summary>
public sealed record RecordActivityBucketResponse(string BucketStartDate, int BucketDayCount, int CreatedRecordCount, int SubmittedRecordCount);

/// <summary>
/// Statistics over the caller's complete authority-discoverable set of top-level records (the My Records boundary). Status and
/// dates are business-database listing metadata (unverified, the same as My Records); opening a record verifies it.
/// </summary>
public sealed record DashboardRecordStatisticsResponse(
    int AccessibleRecordCount,
    int DraftRecordCount,
    int SubmittedRecordCount,
    int ArchivedRecordCount,
    int OtherStatusRecordCount,
    int UnavailableRecordCount,
    int PeriodDays,
    string TimeZoneId,
    IReadOnlyList<RecordActivityBucketResponse> Activity,
    long AuthorizationRevision,
    string ScopeStatement);

/// <summary>One time bucket of the upload-activity chart (the caller's own upload sessions, by outcome group).</summary>
public sealed record UploadActivityBucketResponse(string BucketStartDate, int BucketDayCount, int InProgressUploadCount, int CompletedUploadCount, int FailedUploadCount, int CancelledUploadCount);

public sealed record DashboardUploadStatisticsResponse(
    int PeriodDays,
    string TimeZoneId,
    int TotalUploadCount,
    int InProgressUploadCount,
    int CompletedUploadCount,
    int FailedUploadCount,
    int CancelledUploadCount,
    IReadOnlyList<UploadActivityBucketResponse> Activity);

/// <summary>One of the caller's own upload sessions. <c>StatusGroup</c>: InProgress, Completed, Failed or Cancelled.</summary>
public sealed record DashboardUploadItemResponse(Guid UploadSessionId, Guid FormRecordId, string OriginalFileName, string SessionState, string StatusGroup, DateTimeOffset SessionCreatedOn, long DeclaredSizeBytes, string? FailureReasonCode);

/// <summary>Opaque-cursor page (PB-06). <c>PageStart</c> is 1-based (0 when empty).</summary>
public sealed record DashboardUploadPageResponse(IReadOnlyList<DashboardUploadItemResponse> Items, int TotalCount, int PageStart, int PageSize, string? NextToken, string? PreviousToken);

public sealed record DashboardActivityPageResponse(IReadOnlyList<AuditEventResponse> Items, int TotalCount, int PageStart, int PageSize, string? NextToken, string? PreviousToken, IReadOnlyList<string> AvailableCategories);

public sealed record DashboardFormPageResponse(IReadOnlyList<FormDefinitionSummaryResponse> Items, int TotalCount, int PageStart, int PageSize, string? NextToken, string? PreviousToken);

// ---- Security monitoring (PG-056) ----

/// <summary>
/// An incident as listed. <c>FormRecordId</c> and <c>RecordReference</c> are returned only when the viewer may discover the
/// record (Authority decision); otherwise <c>RecordWithheld</c> is true and no record identifier is disclosed.
/// </summary>
public sealed record SecurityIncidentListItemResponse(
    Guid SecurityIncidentId,
    string IncidentReference,
    string IncidentTypeName,
    string IncidentSeverityName,
    string IncidentStatusName,
    DateTimeOffset FirstDetectedOn,
    DateTimeOffset LastDetectedOn,
    int OccurrenceCount,
    bool RecordScoped,
    bool RecordWithheld,
    Guid? FormRecordId,
    string? RecordReference,
    Guid? AssignedToUserId,
    string? AssignedToDisplayName);

public sealed record SecurityIncidentPageResponse(IReadOnlyList<SecurityIncidentListItemResponse> Items, int TotalCount, int PageStart, int PageSize, string? NextToken, string? PreviousToken);

public sealed record SecurityIncidentBucketResponse(string BucketStartDate, int BucketDayCount, int IntegrityIncidentCount, int OperationalIncidentCount);

/// <summary>Counts over every incident of the organization in the triage store. Zero incidents is not evidence that data was verified.</summary>
public sealed record SecurityIncidentSummaryResponse(
    int OpenIncidentCount,
    int InvestigatingIncidentCount,
    int ResolvedIncidentCount,
    int FalsePositiveIncidentCount,
    int ConfirmedIntegrityFailureCount,
    int CriticalIncidentCount,
    int HighIncidentCount,
    int MediumIncidentCount,
    int LowIncidentCount,
    int RecentIncidentCount,
    int PeriodDays,
    string TimeZoneId,
    IReadOnlyList<SecurityIncidentBucketResponse> Detections,
    string CoverageStatement);

public sealed record SecurityIncidentOccurrenceResponse(Guid SecurityIncidentOccurrenceId, DateTimeOffset OccurrenceDetectedOn, string ObservedIncidentTypeName, string DetectionSourceName, string VerificationErrorCode, string? ObservedByDisplayName, Guid RequestCorrelationIdentifier, long? CommittedContentRevision);

public sealed record SecurityIncidentNoteResponse(Guid SecurityIncidentNoteId, DateTimeOffset NoteCreatedOn, string AuthorDisplayName, string InvestigationNoteText);

public sealed record SecurityIncidentInvestigatorResponse(Guid UserId, string DisplayName);

public sealed record SecurityIncidentDetailResponse(
    SecurityIncidentListItemResponse Incident,
    string DetectionSourceName,
    string VerificationErrorCode,
    Guid FirstCorrelationIdentifier,
    string? ResolutionExplanationText,
    DateTimeOffset? ResolvedOn,
    string? ResolvedByDisplayName,
    long IncidentRevision,
    IReadOnlyList<SecurityIncidentOccurrenceResponse> Occurrences,
    IReadOnlyList<SecurityIncidentNoteResponse> Notes,
    IReadOnlyList<AuditEventResponse> AuditTrail,
    IReadOnlyList<SecurityIncidentInvestigatorResponse> AssignableInvestigators,
    string EvidenceStatement);

/// <summary>
/// Investigation change. Leave a member null to keep it; <c>ClearAssignment</c> removes the investigator. Resolved and
/// FalsePositive require <c>ResolutionExplanation</c>. Changing the status never changes record visibility.
/// </summary>
public sealed record UpdateSecurityIncidentRequest(long ExpectedIncidentRevision, string? IncidentStatusName = null, Guid? AssignedToUserId = null, bool ClearAssignment = false, string? ResolutionExplanation = null);

public sealed record AddSecurityIncidentNoteRequest(string InvestigationNote);
