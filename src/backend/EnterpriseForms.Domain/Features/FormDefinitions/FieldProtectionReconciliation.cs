namespace EnterpriseForms.Domain.Features.FormDefinitions;

/// <summary>
/// Phase C7 M4 (G19): one reconciliation of a field's protection decision against the Authorization Authority's complete
/// record population, run by the Worker under the separate field-protection verifier identity. A durable, leased, resumable
/// job: catalogue (Authority snapshot) → census of every accepted record in pages (checkpointed by offset) → the
/// Authority's attestation. The attestation copy and the business-side counts held here are stored in the business
/// database and are evidence pointers only: the authoritative report is the Authority's attestation, journaled on its
/// audit stream under <see cref="AuthorityAttestationId"/>. Never used for authorization, decryption, reads or search.
/// </summary>
public sealed class FieldProtectionReconciliation
{
    public Guid FieldProtectionReconciliationId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    /// <summary>The migration whose completion this reconciliation decides (MigrationCompletion), or the latest one (VerifyOnly).</summary>
    public Guid? FieldProtectionMigrationId { get; set; }

    /// <summary>The protection decision (effective PublishFormSecurityRevision command) reconciled.</summary>
    public Guid AuthorityCommandId { get; set; }

    public string MigrationDirection { get; set; } = FieldProtectionMigrationDirections.Encrypt;

    public string ReconciliationMode { get; set; } = FieldProtectionReconciliationModes.MigrationCompletion;

    public string ReconciliationState { get; set; } = FieldProtectionReconciliationStates.Queued;

    public Guid? RequestedByUserId { get; set; }

    public string? RequestReasonText { get; set; }

    public DateTimeOffset ReconciliationRequestedOn { get; set; }

    public string? LeaseOwnerName { get; set; }

    public DateTimeOffset? LeaseExpiresOn { get; set; }

    public DateTimeOffset NextAttemptOn { get; set; }

    /// <summary>Catalogue rounds started (a round restarts when the population or a head moved, or the snapshot expired).</summary>
    public int ReconciliationRoundCount { get; set; }

    public int TransientFailureCount { get; set; }

    public string? LastErrorCode { get; set; }

    /// <summary>The Authority migration catalogue snapshot of the current round.</summary>
    public Guid? CatalogueSnapshotId { get; set; }

    public int? CatalogueEntryCount { get; set; }

    /// <summary>Durable checkpoint: catalogue entries of the current round already processed.</summary>
    public int CensusNextOffset { get; set; }

    /// <summary>Records the Authority newly verified in census calls (all rounds).</summary>
    public int CensusVerifiedRecordCount { get; set; }

    /// <summary>Records whose census the Authority already held for the same accepted operation (not re-verified).</summary>
    public int CensusReusedRecordCount { get; set; }

    /// <summary>Records the census reported as failing (integrity, missing content, retired key …) in the current round.</summary>
    public int CensusFailedRecordCount { get; set; }

    /// <summary>Business form_records rows of the form that the Authority does not register (injected or forged; never read, reported).</summary>
    public int? BusinessOnlyRecordCount { get; set; }

    /// <summary>Authority-registered records of the form that the business database does not list under it (deleted or moved; still censused).</summary>
    public int? BusinessMissingRecordCount { get; set; }

    /// <summary>Encrypt direction: ciphertext rows of the field the worker decrypted (AEAD tag and AAD checked) and compared with the field type.</summary>
    public int DecryptCheckedValueCount { get; set; }

    public int DecryptFailedValueCount { get; set; }

    public int LogicalTypeMismatchCount { get; set; }

    /// <summary>
    /// Residual exposure (untrusted count): rows of the field in the old form that the business database still holds outside
    /// the records' current accepted revisions — immutable history, abandoned candidates. Not migrated, not deleted.
    /// </summary>
    public int? RetainedOldFormRowCount { get; set; }

    public Guid? AuthorityAttestationId { get; set; }

    public string? AttestationDigestHex { get; set; }

    public long? AttestationAuditJournalRevision { get; set; }

    public DateTimeOffset? AttestedOn { get; set; }

    public bool? AttestationComplete { get; set; }

    public string? AttestationIncompleteReason { get; set; }

    /// <summary>Copy of the Authority's report (counts only), for display; the Authority journal holds the original.</summary>
    public string? AttestationReportJson { get; set; }

    public DateTimeOffset? ReconciliationCompletedOn { get; set; }

    public DateTimeOffset ReconciliationModifiedOn { get; set; }
}

public static class FieldProtectionReconciliationModes
{
    /// <summary>Decides whether a migration may be marked completed; may hand records that still hold the old form back to the migration.</summary>
    public const string MigrationCompletion = "MigrationCompletion";

    /// <summary>Requested by an authorized person: verifies and reports only; never converts anything.</summary>
    public const string VerifyOnly = "VerifyOnly";
}

public static class FieldProtectionReconciliationStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";

    /// <summary>A dependency (Authority, verifier credential reachability, key provider) is unavailable; retried automatically.</summary>
    public const string Blocked = "Blocked";

    /// <summary>The Authority attested completeness and every decrypt check passed.</summary>
    public const string Complete = "Complete";

    /// <summary>Finished with an explicit reason (old form remaining, quarantined, unverifiable, limit …); never a success claim.</summary>
    public const string Incomplete = "Incomplete";

    /// <summary>This deployment has no verifier credential: no independent reconciliation is possible (fail closed).</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>The protection decision changed while it ran; a reconciliation under the new decision is needed.</summary>
    public const string Superseded = "Superseded";
}
