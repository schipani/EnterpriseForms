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

    public bool CompletedByUserCommit { get; set; }

    public DateTimeOffset ItemModifiedOn { get; set; }
}

public static class FieldProtectionMigrationItemStates
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
