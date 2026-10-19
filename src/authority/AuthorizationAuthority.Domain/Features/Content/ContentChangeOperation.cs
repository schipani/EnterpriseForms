namespace AuthorizationAuthority.Domain.Features.Content;

/// <summary>Prepared/committed content operation (CROSS_SYSTEM §3). The candidate leaf set the authority signed is stored alongside so commit can prove the staged bytes are exactly what was approved.</summary>
public sealed class ContentChangeOperation
{
    public Guid ContentChangeOperationId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid ActorIdentityBindingId { get; set; }

    public string OperationIdempotencyKey { get; set; } = string.Empty;

    public byte[] OperationRequestDigest { get; set; } = [];

    public long ExpectedAuthorizationRevision { get; set; }

    public long ExpectedContentRevision { get; set; }

    public long CandidateContentRevision { get; set; }

    public int CandidateKeyVersion { get; set; }

    public byte[] CandidateManifestRoot { get; set; } = [];

    public byte[] CandidateLeafIndexDigest { get; set; } = [];

    public long CandidateLeafCount { get; set; }

    public string OperationStateName { get; set; } = ContentOperationStates.Prepared;

    public string? OperationQuarantineReasonCode { get; set; }

    public Guid CorrelationIdentifier { get; set; }

    public DateTimeOffset OperationCreatedOn { get; set; }

    public DateTimeOffset OperationExpiresOn { get; set; }

    public DateTimeOffset? OperationCommittedOn { get; set; }

    /// <summary>Set for workload reprotection operations: the effective PublishFormSecurityRevision command that authorizes them. Null for user operations.</summary>
    public Guid? ReprotectionCommandId { get; set; }
}

public static class ContentOperationStates
{
    public const string Prepared = "Prepared";
    public const string Committed = "Committed";
    public const string Aborted = "Aborted";
    public const string Quarantined = "Quarantined";
}

/// <summary>One candidate leaf the authority signed during prepare. Immutable.</summary>
public sealed class ContentCandidateLeaf
{
    public Guid ContentCandidateLeafId { get; set; }

    public Guid ContentChangeOperationId { get; set; }

    public byte[] ManifestItemKey { get; set; } = [];

    public byte ManifestItemKindCode { get; set; }

    public Guid ManifestItemId { get; set; }

    public string MutationKindName { get; set; } = string.Empty;

    public byte[] CanonicalPayloadDigest { get; set; } = [];

    public byte[] RowSignature { get; set; } = [];

    public byte[] LeafDigest { get; set; } = [];
}

public static class ContentMutationKinds
{
    public const string Add = "Add";
    public const string Update = "Update";
    public const string Remove = "Remove";
    public const string Unchanged = "Unchanged";

    /// <summary>A plaintext row of a field replaced by an encrypted row of the same item (reprotection operations only).</summary>
    public const string Reprotect = "Reprotect";
}
