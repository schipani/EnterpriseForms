namespace AuthorizationAuthority.Domain.Features.Journal;

/// <summary>
/// Hash-chained, independently archived semantic event body (G14: full replayable bodies, not only digests).
/// A command or content commit becomes effective/readable only after the entry's archive receipt is recorded.
/// </summary>
public sealed class SecurityJournalEntry
{
    public Guid SecurityJournalEntryId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>"authorization" (security commands) or "content" (content-integrity checkpoints).</summary>
    public string JournalStreamName { get; set; } = string.Empty;

    public long JournalStreamRevision { get; set; }

    public string JournalEventKind { get; set; } = string.Empty;

    public Guid? SecurityCommandId { get; set; }

    public Guid? ContentChangeOperationId { get; set; }

    public string JournalEventBodyJson { get; set; } = "{}";

    public byte[] JournalEventDigest { get; set; } = [];

    public byte[]? JournalPreviousDigest { get; set; }

    public string ArchiveDeliveryState { get; set; } = ArchiveDeliveryStates.Pending;

    public string? ArchiveReceiptReference { get; set; }

    public byte[]? ArchiveReceiptDigest { get; set; }

    public int ArchiveAttemptCount { get; set; }

    public string? LastArchiveErrorCode { get; set; }

    public DateTimeOffset JournalCreatedOn { get; set; }

    public DateTimeOffset? JournalArchivedOn { get; set; }
}

public static class ArchiveDeliveryStates
{
    public const string Pending = "Pending";
    public const string Anchored = "Anchored";
    public const string VerificationFailed = "VerificationFailed";
}

public static class JournalStreamNames
{
    public const string Authorization = "authorization";
    public const string Content = "content";
}
