namespace EnterpriseForms.Domain.Features.FormDefinitions;

/// <summary>
/// Durable plaintext→encrypted migration of one field (B-28). Created when a designer strengthens a field to
/// application encryption and the authority has accepted the PublishFormSecurityRevision command; processed by the
/// leased background worker one record at a time through the authority's content workflow. A projection with
/// per-record items; the authority's accepted heads remain the truth of what is encrypted.
/// </summary>
public sealed class FieldProtectionMigration
{
    public Guid FieldProtectionMigrationId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormDefinitionId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public long ProtectionPolicyRevision { get; set; }

    /// <summary>The effective authority command that authorizes every reprotection operation of this migration.</summary>
    public Guid AuthorityCommandId { get; set; }

    public string MigrationState { get; set; } = FieldProtectionMigrationStates.Queued;

    /// <summary>Encrypt (plaintext → ciphertext, B-28) or Decrypt (ciphertext → plaintext, Amendment A1).</summary>
    public string MigrationDirection { get; set; } = FieldProtectionMigrationDirections.Encrypt;

    /// <summary>For an Encrypt migration started by cancelling a downgrade: the Decrypt migration being rolled back.</summary>
    public Guid? RollbackOfMigrationId { get; set; }

    public DateTimeOffset? MigrationCancelledOn { get; set; }

    /// <summary>Records holding a plaintext copy of the field when the migration was created (the "before" count).</summary>
    public int TotalRecordCount { get; set; }

    /// <summary>Plaintext value rows of the field when the migration was created.</summary>
    public int TotalValueCount { get; set; }

    public int CompletedRecordCount { get; set; }

    public int FailedRecordCount { get; set; }

    public string? LastErrorCode { get; set; }

    public int AttemptCount { get; set; }

    public string? LeaseOwnerName { get; set; }

    public DateTimeOffset? LeaseExpiresOn { get; set; }

    public DateTimeOffset NextAttemptOn { get; set; }

    public Guid StartedByUserId { get; set; }

    public DateTimeOffset MigrationCreatedOn { get; set; }

    public DateTimeOffset MigrationModifiedOn { get; set; }

    public DateTimeOffset? MigrationCompletedOn { get; set; }

    /// <summary>Set when, after every item completed, no plaintext copy of the field was found in any latest committed snapshot.</summary>
    public DateTimeOffset? MigrationVerifiedOn { get; set; }

    /// <summary>
    /// Phase C6 M0 (S1): when the worker's last post-check ran, whatever its outcome. The post-check reads every listed
    /// record's Authority-accepted head and counts rows of the old form in exactly that operation. These results are
    /// stored in the business database: they record what the Authority-backed check found, but a hostile business
    /// database administrator could alter them, so the status never relies on them alone (it also requires the local
    /// estimate to agree), and the list of records checked comes from the business database (G19).
    /// </summary>
    public DateTimeOffset? VerificationCheckedOn { get; set; }

    /// <summary>Records whose Authority-accepted head the last post-check read.</summary>
    public int? VerifiedRecordCount { get; set; }

    /// <summary>Of those, records whose accepted head still held rows of the old form.</summary>
    public int? VerifiedRemainingRecordCount { get; set; }

    /// <summary>Rows of the old form in those accepted heads.</summary>
    public int? VerifiedRemainingValueCount { get; set; }

    /// <summary>Listed records the last post-check could not read from the Authority (forbidden, not found, locked); never counted as converted.</summary>
    public int? UnverifiedRecordCount { get; set; }
}

public static class FieldProtectionMigrationStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";

    /// <summary>Every item completed and the post-check found no plaintext copy; "fully encrypted" may be claimed.</summary>
    public const string Completed = "Completed";

    /// <summary>At least one item exhausted its attempts; the field is not fully encrypted until retried successfully.</summary>
    public const string Failed = "Failed";

    /// <summary>A dependency (key provider, authority, signer) is unavailable; retried automatically, nothing is lost.</summary>
    public const string Blocked = "Blocked";

    /// <summary>A downgrade cancelled by an administrator; the rollback Encrypt migration re-protects already converted values.</summary>
    public const string Cancelled = "Cancelled";
}

public static class FieldProtectionMigrationDirections
{
    public const string Encrypt = "Encrypt";
    public const string Decrypt = "Decrypt";
}

/// <summary>One record of a migration: pending, completed (by the worker or by a user's own commit), or failed after bounded attempts.</summary>
public sealed class FieldProtectionMigrationItem
{
    public Guid FieldProtectionMigrationItemId { get; set; }

    public Guid FieldProtectionMigrationId { get; set; }

    public Guid TenantId { get; set; }

    public Guid FormRecordId { get; set; }

    public string ItemState { get; set; } = FieldProtectionMigrationItemStates.Pending;

    public int AttemptCount { get; set; }

    public string? LastErrorCode { get; set; }

    public DateTimeOffset? LastAttemptOn { get; set; }

    /// <summary>The authority-accepted operation that holds the encrypted copy (the worker's, or a user's edit that re-protected on write).</summary>
    public Guid? CompletedOperationId { get; set; }

    public long? CompletedContentRevision { get; set; }

    /// <summary>True only when the accepted operation that completed the item is a user operation (<see cref="CompletedByOperationKind"/> = UserOperation).</summary>
    public bool CompletedByUserCommit { get; set; }

    /// <summary>
    /// Phase C6 M0 (S4): which kind of accepted operation completed the item, decided from the operation's identity (never
    /// from timing): this migration's own reprotection operation, another protection migration's operation, a user
    /// operation, or no accepted content at all. See <see cref="FieldProtectionCompletionKinds"/>.
    /// </summary>
    public string? CompletedByOperationKind { get; set; }

    /// <summary>Phase C6 M0 (S1): the Authority-accepted head this item was last checked against (operation and revision; revision 0 = no accepted content).</summary>
    public Guid? VerifiedHeadOperationId { get; set; }

    public long? VerifiedHeadContentRevision { get; set; }

    /// <summary>Rows of the old form found in that accepted head (counted from the business rows of exactly that operation).</summary>
    public int? VerifiedOldFormValueCount { get; set; }

    public DateTimeOffset? VerifiedOn { get; set; }

    public DateTimeOffset ItemModifiedOn { get; set; }
}

/// <summary>How a migration item was completed (Phase C6 M0, S4). Decided from the accepted operation's identity.</summary>
public static class FieldProtectionCompletionKinds
{
    /// <summary>The accepted head is an operation of this migration (its operation id derives from this migration's idempotency key).</summary>
    public const string MigrationWorker = "MigrationWorker";

    /// <summary>The accepted head is an operation of another protection migration of the same form.</summary>
    public const string OtherMigration = "OtherMigration";

    /// <summary>The accepted head is not a protection-migration operation: a user's own write already holds the field in the target form.</summary>
    public const string UserOperation = "UserOperation";

    /// <summary>The Authority holds no accepted content for the record (revision 0): there is no copy to convert.</summary>
    public const string NoAcceptedContent = "NoAcceptedContent";
}

public static class FieldProtectionMigrationItemStates
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
