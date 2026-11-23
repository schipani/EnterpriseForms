namespace EnterpriseForms.Contracts.Features;

// Phase C9 M2 (WP-3, WP-4, WP-5): semantic layout comparison, publish impact and version-to-version comparison. Form-design
// metadata only (labels, rules, limits, options as published); no record values are ever part of these responses.

/// <summary>One changed aspect of an element. <c>Aspect</c> is a stable code (Label, Required, MaxLength, OptionDeactivated, …); <c>Subject</c> names the option it concerns; <c>Tightened</c> marks a rule or limit that can refuse input it accepted before.</summary>
public sealed record LayoutChangeDetailResponse(string Aspect, string Significance, string? Before, string? After, string? Subject, bool Tightened);

/// <summary>
/// One changed element, identified by its stable id. <c>Target</c>: Form, Section, Field, DocumentCollection, RelatedFormList.
/// <c>ChangeType</c>: Added, Removed, Modified, Moved. <c>Significance</c>: Presentation (wording, width, position, order),
/// Behaviour (validation, rules, read-only, options, limits, protection) or Structure (added or removed).
/// </summary>
public sealed record LayoutChangeResponse(string Target, string ElementId, string ChangeType, string Significance, string Label, string? SectionTitle, IReadOnlyList<LayoutChangeDetailResponse> Details);

/// <summary>Counts and changes, in the order of the later layout then removed items.</summary>
public sealed record LayoutComparisonResponse(int AddedCount, int RemovedCount, int BehaviourChangeCount, int PresentationChangeCount, bool HasChanges, IReadOnlyList<LayoutChangeResponse> Changes);

/// <summary>One side of a comparison: a published version (<c>Kind</c> = Version) or the current draft (<c>Kind</c> = Draft).</summary>
public sealed record ComparedLayoutResponse(string Kind, Guid? FormVersionId, int? VersionNumber, long? DraftRevision, DateTimeOffset? PublishedOn);

/// <summary>Phase C9 WP-5: comparison of two published versions, or of a published version and the draft (as it would be published).</summary>
public sealed record VersionComparisonResponse(Guid FormDefinitionId, ComparedLayoutResponse From, ComparedLayoutResponse To, LayoutComparisonResponse Comparison);

/// <summary>
/// A publish-impact notice. <c>Code</c>: NEW_REQUIRED_FIELDS, VALIDATION_TIGHTENED, OPTIONS_NO_LONGER_AVAILABLE,
/// LIMITS_TIGHTENED, ELEMENTS_REMOVED, READ_ONLY_OR_HIDDEN, PROTECTION_CHANGED, PRESENTATION_ONLY, NO_CHANGES.
/// <c>Severity</c>: Attention (records edited with the new version may need attention) or Information. Notices describe
/// potential impact on future edits; publishing never deletes or changes stored values.
/// </summary>
public sealed record PublishImpactNoticeResponse(string Code, string Severity, IReadOnlyList<string> ElementLabels);

/// <summary>
/// Approximate record-level impact, over records the caller may discover only (D9-4): visible records of the form, how
/// many would be shown and how many edited with the new version under the form's version behaviour, authorized records
/// without listing details (not counted). No field values are counted.
/// </summary>
public sealed record PublishRecordImpactResponse(int VisibleRecordCount, int ShownWithNewVersionCount, int EditedWithNewVersionCount, int UnavailableRecordCount, long AuthorizationRevision, bool CountsAreApproximate, string ScopeStatement);

/// <summary>
/// Phase C9 WP-4: what publishing the current draft would change, before confirming. <c>Comparison</c> is the current
/// version against the draft exactly as it would be published. <c>CanPublish</c> is false with a reason when the draft is
/// identical to the current version or invalid. Read-only: nothing is written or audited.
/// </summary>
public sealed record PublishImpactResponse(
    Guid FormDefinitionId,
    long DraftRevision,
    int? CurrentVersionNumber,
    int NextVersionNumber,
    string VersionBehavior,
    bool CanPublish,
    string? BlockedReasonCode,
    string? BlockedReason,
    LayoutComparisonResponse Comparison,
    IReadOnlyList<PublishImpactNoticeResponse> Notices,
    PublishRecordImpactResponse Records,
    string DataStatement);
