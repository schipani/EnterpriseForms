namespace AuthorizationAuthority.Domain.Features.Catalogue;

/// <summary>
/// Phase C7 M4 (G19): the protection census of one accepted record revision under one protection decision. Written only by
/// the Authority after it has verified the observed rows against the accepted manifest of that exact operation (complete
/// leaf set, every HMAC, root, archive receipt). The counts come from the verified canonical bytes, never from the caller.
/// Accepted content of an operation is immutable, so a result stays true for that (record, operation, command) and is reused
/// by later reconciliations instead of being re-verified. Holds no record contents.
/// </summary>
public sealed class ProtectionCensusResult
{
    public Guid ProtectionCensusResultId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>The effective PublishFormSecurityRevision command (the protection decision) the census was taken under.</summary>
    public Guid ScopingSecurityCommandId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid FormRecordId { get; set; }

    public Guid CommittedOperationId { get; set; }

    public long CommittedContentRevision { get; set; }

    /// <summary><see cref="ProtectionCensusOutcomes"/>: verified, or the integrity/verification failure code.</summary>
    public string CensusOutcomeName { get; set; } = ProtectionCensusOutcomes.Verified;

    /// <summary>Rows of the field (value and selection items, repeating-section occurrences included) in the verified revision.</summary>
    public int FieldOccurrenceCount { get; set; }

    /// <summary>Rows of the field still in the form the decision removes (plaintext for Encrypt, ciphertext for Decrypt).</summary>
    public int OldFormOccurrenceCount { get; set; }

    /// <summary>Encrypted rows of the field whose envelope shape is not the approved one, or that reuse another row's nonce under the same wrapped key.</summary>
    public int EnvelopeIssueCount { get; set; }

    /// <summary>Signed header status of the verified revision (e.g. Draft, Archived); archived records stay in scope.</summary>
    public string? VerifiedRecordStatus { get; set; }

    public string RequestingWorkloadName { get; set; } = string.Empty;

    public DateTimeOffset CensusVerifiedOn { get; set; }
}

public static class ProtectionCensusOutcomes
{
    public const string Verified = "Verified";
}

/// <summary>
/// Phase C7 M4 (G19): an Authority-computed reconciliation report for one protection decision. Every count is derived by
/// the Authority under the organization lock from its own registry (the form's complete record population), its accepted
/// heads and its census results — nothing from the business database. Immutable once written; also journaled on the audit
/// stream. <c>AttestationComplete</c> is true only when every accepted record's current revision was verified and holds no
/// row of the field in the old form, no record is quarantined or unverifiable, and no search approval of the field is active.
/// It covers current accepted revisions only; retained earlier revisions are counted separately and are never claimed.
/// </summary>
public sealed class ProtectionReconciliationAttestation
{
    public Guid ProtectionReconciliationAttestationId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ScopingSecurityCommandId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    /// <summary>Encrypt (plaintext → ciphertext) or Decrypt (Amendment A1).</summary>
    public string ProtectionDirectionName { get; set; } = string.Empty;

    /// <summary>The verifier's migration catalogue snapshot the census work-list came from (evidence; validity is re-derived).</summary>
    public Guid? SearchCatalogueSnapshotId { get; set; }

    public int ExpectedRecordCount { get; set; }

    public int AcceptedRecordCount { get; set; }

    public int PendingCreationRecordCount { get; set; }

    public int QuarantinedRecordCount { get; set; }

    public int CompliantRecordCount { get; set; }

    public int NotApplicableRecordCount { get; set; }

    public int OldFormRemainingRecordCount { get; set; }

    public int OldFormRemainingOccurrenceCount { get; set; }

    public int TargetFormOccurrenceCount { get; set; }

    public int UnverifiedRecordCount { get; set; }

    public int NotYetCensusedRecordCount { get; set; }

    public int EnvelopeIssueRecordCount { get; set; }

    public int ArchivedRecordCount { get; set; }

    /// <summary>Records of the form whose registration was revoked: outside the executable scope; their retained content is not migrated.</summary>
    public int RetiredRecordCount { get; set; }

    /// <summary>Accepted revisions earlier than each record's current head (immutable history): outside the executable scope, never claimed migrated.</summary>
    public long RetainedHistoryRevisionCount { get; set; }

    public int ActiveSearchApprovalCount { get; set; }

    public bool AttestationComplete { get; set; }

    public string? AttestationIncompleteReason { get; set; }

    /// <summary>SHA-256 of the canonical report body (also the body journaled on the audit stream).</summary>
    public byte[] AttestationDigest { get; set; } = [];

    public long AttestedAuthorizationRevision { get; set; }

    public long AttestedContentIntegrityRevision { get; set; }

    public long AttestedAuditJournalRevision { get; set; }

    public string RequestingWorkloadName { get; set; } = string.Empty;

    public DateTimeOffset AttestedOn { get; set; }
}

/// <summary>Why an attestation is not complete (the first applicable reason, in this order).</summary>
public static class ProtectionAttestationReasons
{
    public const string Quarantined = "RECORDS_QUARANTINED";
    public const string Unverified = "RECORDS_UNVERIFIED";
    public const string NotYetCensused = "RECORDS_NOT_CENSUSED";
    public const string OldFormRemaining = "OLD_FORM_REMAINING";
    public const string EnvelopeIssues = "ENVELOPE_ISSUES";
    public const string SearchApprovalActive = "SEARCH_APPROVAL_ACTIVE";
}
