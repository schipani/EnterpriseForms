namespace AuthorizationAuthority.Domain.Features.SecurityCommands;

/// <summary>Append-only security command journal row. Committed commands are never deleted; reversal is a new command with a new revision.</summary>
public sealed class SecurityCommand
{
    public Guid SecurityCommandId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ActorIdentityBindingId { get; set; }

    public string CommandKindName { get; set; } = string.Empty;

    public string CommandIdempotencyKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical request body; same key + different digest ⇒ IdempotencyConflict (A5).</summary>
    public byte[] CommandRequestDigest { get; set; } = [];

    public long ExpectedAuthorizationRevision { get; set; }

    public long? CommittedAuthorizationRevision { get; set; }

    public string CommandStateName { get; set; } = SecurityCommandStates.Rejected;

    public string CommandPayloadJson { get; set; } = "{}";

    public string? CommandReasonText { get; set; }

    public string? RejectionReasonCode { get; set; }

    public Guid CorrelationIdentifier { get; set; }

    public DateTimeOffset CommandCreatedOn { get; set; }

    public DateTimeOffset CommandModifiedOn { get; set; }

    public DateTimeOffset? CommandEffectiveOn { get; set; }
}

public static class SecurityCommandStates
{
    public const string Pending = "Pending";
    public const string CommittedAwaitingProjection = "CommittedAwaitingProjection";
    public const string Effective = "Effective";
    public const string Rejected = "Rejected";
    public const string FailedReconciliation = "FailedReconciliation";
}

/// <summary>Command kinds accepted by the Stage A authority. Other kinds in the contract enum are rejected with COMMAND_KIND_NOT_IMPLEMENTED.</summary>
public static class SecurityCommandKinds
{
    public const string RegisterTeam = "RegisterTeam";
    public const string AddTeamMember = "AddTeamMember";
    public const string RemoveTeamMember = "RemoveTeamMember";
    public const string RegisterFormDefinition = "RegisterFormDefinition";
    public const string RegisterRecord = "RegisterRecord";
    public const string TransferOwnership = "TransferOwnership";
    public const string CreateShare = "CreateShare";
    public const string RevokeShare = "RevokeShare";
    public const string UpdateFormUsers = "UpdateFormUsers";
    public const string PublishFormSecurityRevision = "PublishFormSecurityRevision";
    public const string UpdateMandatoryPolicy = "UpdateMandatoryPolicy";
    public const string ReclassifyField = "ReclassifyField";
}
