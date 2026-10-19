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

public sealed record CreateFormRecordRequest(IReadOnlyDictionary<string, string> Values, Guid? OwnerTeamId = null);

public sealed record UpdateFormRecordRequest(long ExpectedContentRevision, IReadOnlyDictionary<string, string> Values);

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
    string IntegrityStatus);

/// <summary>Invariant wire value: text, integer/decimal literal, ISO date, true/false or option key (Stage B typed values, B-02).</summary>
/// <summary><c>ApplicationEncrypted</c>: the value was stored as ciphertext and decrypted for this authorized response. <c>LegacyPlaintext</c>: the field is now encrypted but this record still holds a plaintext copy until its next commit (migration pending).</summary>
public sealed record FieldValueResponse(Guid FieldDefinitionId, string FieldKey, Guid FieldValueItemId, string StringValue, string FieldDataType = "String", bool ApplicationEncrypted = false, bool LegacyPlaintext = false);

public sealed record CreateShareRequest(Guid? RecipientUserId, Guid? RecipientTeamId, string RecipientRole, IReadOnlyList<string> FieldKeys);

public sealed record ShareResponse(Guid RecordShareId, Guid FormRecordId, string RecipientRole, IReadOnlyList<Guid> FieldValueItemIds, string ShareStatus, CommandOutcome Command);

public sealed record TransferOwnershipRequest(Guid? NewOwnerUserId, Guid? NewOwnerTeamId, string TransferReason);

public sealed record CreateTeamRequest(string TeamName);

public sealed record TeamResponse(Guid TeamId, string TeamName, CommandOutcome Command);

public sealed record TeamMemberRequest(Guid UserId);

public sealed record CommandStatusResponse(CommandOutcome Command);
