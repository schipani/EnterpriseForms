using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 G08 / E-6 / E-7 (C-158, contract amendment A-7 §27): a <b>trusted delivery receipt</b> for one journal entry. The mutable AuthorityDb
/// delivery fields (<c>archive_delivery_state</c>, receipt reference and digest, archived-on time) are never sufficient: an entry counts as
/// delivered only when evidence held <b>outside AuthorityDb</b> proves it — the archived object at the entry's key equals the row's bytes (SHA-256),
/// the archive is the pinned instance, and the entry's W2-S evidence commitment (both stores, highest stop generation) names the same row. Any
/// other outcome is not delivered (fail closed).
/// </summary>
/// <remarks>
/// WP-7A (C-160, A-9 TR-6′): a <c>Verified</c> verdict describes the external stores' state as established at most the configured freshness
/// bound before the call (never "for as long as the head is unchanged"). <see cref="VerifyStreamHeadAsync"/> additionally requires that no
/// evidence of a successor of the entry exists outside AuthorityDb (no archived object, evidence commitment or stop marker above it), because
/// the caller claims the entry is the stream's newest.
/// </remarks>
public interface IJournalReceiptVerifier
{
    /// <summary>A receipt for one entry, at any position in its stream (content checkpoint entries, projection).</summary>
    Task<JournalReceiptVerdict> VerifyAsync(SecurityJournalEntry entry, CancellationToken cancellationToken);

    /// <summary>A receipt for an entry the caller read as its stream's newest (the organization gate, GT-2).</summary>
    Task<JournalReceiptVerdict> VerifyStreamHeadAsync(SecurityJournalEntry entry, CancellationToken cancellationToken);
}

public enum JournalReceiptStatus
{
    /// <summary>The external evidence proves the entry was delivered through D+ to the pinned archive.</summary>
    Verified,

    /// <summary>The evidence is missing, forged, stale or contradictory: not delivered.</summary>
    NotVerified,

    /// <summary>A store could not be read: not delivered (operational; retried on the next request).</summary>
    Unavailable,
}

public sealed record JournalReceiptVerdict(JournalReceiptStatus Status, string Code, int? StopGeneration = null, string? ArchiveInstanceId = null)
{
    public bool IsVerified => Status == JournalReceiptStatus.Verified;

    public static JournalReceiptVerdict Not(string code) => new(JournalReceiptStatus.NotVerified, code);

    public static JournalReceiptVerdict Unavailable(string code) => new(JournalReceiptStatus.Unavailable, code);
}

/// <summary>Receipt verdict codes (never carry bodies or digests).</summary>
public static class JournalReceiptCodes
{
    public const string Verified = "RECEIPT_VERIFIED";
    public const string NotComposed = "RECEIPT_VERIFIER_NOT_COMPOSED";
    public const string NotDelivered = "RECEIPT_NOT_DELIVERED";
    public const string RowDigest = "RECEIPT_ROW_DIGEST_MISMATCH";
    public const string ArchiveAbsent = "RECEIPT_ARCHIVE_ABSENT";
    public const string ArchiveMismatch = "RECEIPT_ARCHIVE_MISMATCH";
    public const string InstanceUnpinned = "RECEIPT_INSTANCE_UNPINNED";
    public const string InstanceMismatch = "RECEIPT_INSTANCE_MISMATCH";
    public const string CommitmentMissing = "RECEIPT_COMMITMENT_MISSING";
    public const string CommitmentContradictory = "RECEIPT_COMMITMENT_CONTRADICTORY";
    public const string CommitmentMismatch = "RECEIPT_COMMITMENT_MISMATCH";
    public const string BindingMismatch = "RECEIPT_BINDING_MISMATCH";
    public const string ArchiveUnavailable = "RECEIPT_ARCHIVE_UNAVAILABLE";
    public const string StoreUnavailable = "RECEIPT_STORE_UNAVAILABLE";

    /// <summary>WP-7A (TR-6′ RV-6): evidence that verified this key earlier in the process later failed or changed; refused for the process lifetime.</summary>
    public const string EvidenceWithdrawn = "RECEIPT_EVIDENCE_WITHDRAWN";

    /// <summary>WP-7A (TR-6′ RV-4): a successor of the claimed stream head exists outside AuthorityDb (archived object, commitment or stop marker).</summary>
    public const string SuccessorEvidence = "RECEIPT_SUCCESSOR_EVIDENCE";

    /// <summary>WP-7A (TR-6′ RV-5): a stop marker at or below the entry is not superseded by a later-generation commitment.</summary>
    public const string StopUnresolved = "RECEIPT_STOP_UNRESOLVED";

    /// <summary>WP-7A (TR-6′ RV-8): the bounded record of withdrawn evidence is full; every receipt is refused.</summary>
    public const string ContradictionLimit = "RECEIPT_CONTRADICTION_LIMIT";
}

/// <summary>The fail-closed verifier used wherever none is composed: nothing is ever delivered.</summary>
public sealed class NotComposedJournalReceiptVerifier : IJournalReceiptVerifier
{
    public static NotComposedJournalReceiptVerifier Instance { get; } = new();

    public Task<JournalReceiptVerdict> VerifyAsync(SecurityJournalEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(JournalReceiptVerdict.Not(JournalReceiptCodes.NotComposed));

    public Task<JournalReceiptVerdict> VerifyStreamHeadAsync(SecurityJournalEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(JournalReceiptVerdict.Not(JournalReceiptCodes.NotComposed));
}
