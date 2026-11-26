namespace EnterpriseForms.Contracts.Features;

// Stage A business wire contracts (/api/v1). Minimal authenticated create/read/update/share/team workflow sufficient to
// exercise the independent authority, signed staging and verified reads (ADR-PB-002 Stage A). Not the Accused UI.

public sealed record CurrentUserResponse(Guid OrganizationId, string IssuerUri, string SubjectIdentifier, string DisplayName);

public sealed record CreateFormDefinitionRequest(string FormDefinitionName, string FormDefinitionDescription, IReadOnlyList<CreateFieldRequest> Fields, IReadOnlyList<Guid> FormUserUserIds, IReadOnlyList<Guid> FormUserTeamIds);

public sealed record CreateFieldRequest(string FieldKey, string FieldDisplayName);

public sealed record FormDefinitionResponse(Guid FormDefinitionId, Guid OrganizationId, string FormDefinitionName, Guid CurrentFormVersionId, IReadOnlyList<FieldResponse> Fields, long AuthorizationRevision, CommandOutcome Command);

public sealed record FieldResponse(Guid FieldDefinitionId, string FieldKey, string FieldDisplayName, string FieldDataType);

/// <summary>Outcome of the authority security command backing a mutation. Effective=false means the change is committed but not yet effective (poll).</summary>
public sealed record CommandOutcome(Guid CommandId, string CommandStatus, long AuthorizationRevision, bool Effective);

public sealed record CreateFormRecordRequest(IReadOnlyDictionary<string, string> Values, Guid? OwnerTeamId = null, IReadOnlyList<RecordGroupValuesRequest>? Groups = null);

/// <summary><c>Groups</c>: for each repeating section named, the complete new entry list in display order (listed ids are kept, entries without an id are new, omitted ids are removed). Sections not named are unchanged.</summary>
/// <summary><c>ExpectedLayoutVersionId</c> (Phase C9, optional): the edit layout the client validated against; a different current edit layout is 409 EDIT_LAYOUT_CHANGED. Omitted: validated against the current edit layout without stale-layout detection (compatibility exception for older API clients).</summary>
public sealed record UpdateFormRecordRequest(long ExpectedContentRevision, IReadOnlyDictionary<string, string> Values, IReadOnlyList<RecordGroupValuesRequest>? Groups = null, Guid? ExpectedLayoutVersionId = null);

/// <summary>Values of one repeating section (Phase C3), keyed by the section's stable group key.</summary>
public sealed record RecordGroupValuesRequest(string GroupKey, IReadOnlyList<RecordOccurrenceValuesRequest> Occurrences);

/// <summary>One entry: its stable id (null for a new entry) and its values by field key (omitted keys of an existing entry are unchanged).</summary>
public sealed record RecordOccurrenceValuesRequest(Guid? FormRecordGroupId, IReadOnlyDictionary<string, string> Values);

/// <summary>The entries of one repeating section in display order; each entry's values are the <see cref="FieldValueResponse"/> rows carrying its id.</summary>
public sealed record RecordGroupResponse(Guid GroupDefinitionId, string GroupKey, IReadOnlyList<RecordOccurrenceResponse> Occurrences);

public sealed record RecordOccurrenceResponse(Guid FormRecordGroupId, long GroupSortOrder);

public sealed record FormRecordResponse(
    Guid FormRecordId,
    Guid OrganizationId,
    Guid FormDefinitionId,
    string RecordStatus,
    long ContentRevision,
    Guid? CommittedOperationId,
    long AuthorizationRevision,
    bool IsCompleteProjection,
    IReadOnlyList<FieldValueResponse> Values,
    string IntegrityStatus,
    IReadOnlyList<RecordGroupResponse>? Groups = null);

/// <summary>Invariant wire value: text, integer/decimal literal, ISO date, true/false or option key (Stage B typed values, B-02).</summary>
/// <summary><c>ApplicationEncrypted</c>: the value was stored as ciphertext and decrypted for this authorized response. <c>LegacyPlaintext</c>: the field is now encrypted but this record still holds a plaintext copy until its next commit (migration pending).</summary>
/// <summary><c>FormRecordGroupId</c>: the repeating-section entry the value belongs to (null for a value of the record itself).</summary>
public sealed record FieldValueResponse(Guid FieldDefinitionId, string FieldKey, Guid FieldValueItemId, string StringValue, string FieldDataType = "String", bool ApplicationEncrypted = false, bool LegacyPlaintext = false, Guid? FormRecordGroupId = null);

/// <summary>
/// G27: <c>UseDevelopmentStepUp</c> (default false) presents the development-only synthetic step-up marker; the Authority
/// accepts it only where that path is enabled (never production or staging).
/// </summary>
public sealed record CreateShareRequest(Guid? RecipientUserId, Guid? RecipientTeamId, string RecipientRole,
    IReadOnlyList<string> FieldKeys, bool? UseDevelopmentStepUp = null, Guid? ParentRecordShareId = null, DateTimeOffset? ShareExpiresOn = null);

public sealed record ShareResponse(Guid RecordShareId, Guid FormRecordId, string RecipientRole, IReadOnlyList<Guid> FieldValueItemIds, string ShareStatus, CommandOutcome Command);

/// <summary>
/// C10 M2: the complete new scope of an existing share (<c>PATCH .../shares/{recordShareId}</c> with <c>If-Match</c>). Exactly one of
/// <c>ShareExpiresOn</c> or <c>NoExpiry = true</c> is required. <c>ParentRecordShareId</c> names the caller's Manager share when the
/// caller is not the owner; the share keeps its committed parent. <c>UseDevelopmentStepUp</c> as for share creation (G27).
/// </summary>
public sealed record UpdateShareRequest(string RecipientRole, IReadOnlyList<string> FieldKeys, DateTimeOffset? ShareExpiresOn = null, bool? NoExpiry = null,
    Guid? ParentRecordShareId = null, bool? UseDevelopmentStepUp = null);

/// <summary>
/// C10 M2 (WP-0 Section 3.5): one share as the Authority committed it. <c>ShareRevision</c> is the authoritative share revision and
/// is also served as the <c>ETag</c> for <c>PATCH</c>; <c>AuthorizationRevision</c> is the organization revision of the read. Field
/// items are authoritative; <c>FieldKeys</c> is a display hint from the local projection and may omit items it cannot name.
/// Recipient, role and items are empty once the share is no longer Active.
/// </summary>
public sealed record ShareDetailsResponse(Guid RecordShareId, Guid FormRecordId, string ShareStatus, Guid? RecipientUserId, Guid? RecipientTeamId, string? RecipientDisplayName,
    string? RecipientRole, IReadOnlyList<Guid> FieldValueItemIds, IReadOnlyList<string> FieldKeys, DateTimeOffset? ShareExpiresOn, DateTimeOffset? EffectiveExpiresOn,
    long ShareRevision, Guid? ParentRecordShareId, int DelegationDepth, bool LegacyLineageUnresolved, string? InvalidationReasonCode, Guid? GrantorUserId,
    string? GrantorDisplayName, long AuthorizationRevision);

/// <summary>
/// C10 M2 (WP-0 Section 3.5): one change in the record's sharing or ownership history. <c>CommandState</c> is the Authority command's
/// own state; only <c>Effective</c> means the change is in force. Names are display hints from the local directory.
/// </summary>
public sealed record RecordSecurityChangeResponse(string ChangeKind, Guid? RecordShareId, string? ShareEndState, string? InvalidationReasonCode, string? SubjectKind, Guid? SubjectId, string? SubjectDisplayName);

public sealed record RecordSecurityHistoryItemResponse(Guid SecurityCommandId, string CommandKind, string CommandState, DateTimeOffset OccurredOn, DateTimeOffset? EffectiveOn,
    long? CommittedAuthorizationRevision, Guid? ActorUserId, string? ActorDisplayName, IReadOnlyList<RecordSecurityChangeResponse> Changes);

public sealed record RecordSecurityHistoryResponse(Guid FormRecordId, IReadOnlyList<RecordSecurityHistoryItemResponse> Items, bool Truncated, long AuthorizationRevision);

/// <summary>C10 M2: the share after an Effective update; <c>ShareRevision</c> is also the new <c>ETag</c>.</summary>
public sealed record UpdateShareResponse(Guid RecordShareId, Guid FormRecordId, string RecipientRole, IReadOnlyList<Guid> FieldValueItemIds, long ShareRevision, string ShareStatus,
    CommandOutcome Command);

/// <summary>
/// G27: <c>UseDevelopmentStepUp</c> (default false) presents the development-only synthetic step-up marker; the Authority
/// accepts it only where that path is enabled (never production or staging).
/// </summary>
public sealed record TransferOwnershipRequest(Guid? NewOwnerUserId, Guid? NewOwnerTeamId, string TransferReason, bool? UseDevelopmentStepUp = null);

public sealed record CreateTeamRequest(string TeamName);

public sealed record TeamResponse(Guid TeamId, string TeamName, CommandOutcome Command);

public sealed record TeamMemberRequest(Guid UserId);

public sealed record CommandStatusResponse(CommandOutcome Command);
