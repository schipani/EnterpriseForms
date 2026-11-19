namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, <c>docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §7): the separately controlled store of journal-verification
/// checkpoints. Objects are create-only and keyed by (organization, stream, checkpoint sequence). The store holds opaque canonical
/// bytes; their meaning and chain rules live in <c>JournalVerificationCheckpoints</c>.
/// <para>
/// A store that cannot be read or written throws <see cref="CheckpointStoreUnavailableException"/>; caller cancellation propagates as
/// <see cref="OperationCanceledException"/>. A Development/Testing store is <b>not</b> independent of the Development administrator and
/// gives no checkpoint-freshness assurance (contract §7.3, CKR-3).
/// </para>
/// </summary>
public interface IVerificationCheckpointStore
{
    string StoreName { get; }

    /// <summary>Create-only write. Identical bytes already stored ⇒ <see cref="CheckpointCreateStatus.IdenticalExists"/>; other bytes ⇒ <see cref="CheckpointCreateStatus.Conflict"/> (never overwritten).</summary>
    Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken);

    /// <summary>The stored bytes, or Absent / ContainerAbsent. Never creates anything.</summary>
    Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken);

    /// <summary>One page of the complete listing under the (organization, stream) checkpoint prefix; non-canonical names are listed too.</summary>
    Task<CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken);
}

public enum CheckpointCreateStatus
{
    Created,
    IdenticalExists,
    Conflict,
}

public sealed record CheckpointCreateResult(CheckpointCreateStatus Status, byte[]? ExistingBytes);

/// <summary>One listed checkpoint object; <see cref="CheckpointSequence"/> is set only for a canonical name.</summary>
public sealed record CheckpointListedObject(string ObjectName, bool IsCanonicalCheckpointKey, long? CheckpointSequence);

public sealed record CheckpointListingPage(IReadOnlyList<CheckpointListedObject> Objects, string? ContinuationToken, bool ContainerAbsent);

public sealed class CheckpointStoreUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);
