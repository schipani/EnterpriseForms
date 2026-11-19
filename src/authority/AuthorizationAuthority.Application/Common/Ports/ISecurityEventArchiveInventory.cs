namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, <c>docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §6): read-side inventory of the security-event archive for the
/// future baseline tool, scheduled verifier and D+ delivery enforcement. It is a separate port so that the delivery contract
/// (<see cref="ISecurityEventArchive"/>, used by <c>ArchiveProcessor</c> and <c>PlatformJournal</c>) stays exactly as it is.
/// <para>
/// Outcome conventions (AS-2, DP-5, DP-7): an object that does not exist is reported as <see cref="StoredObjectStatus.Absent"/>; a
/// missing container is reported as <see cref="StoredObjectStatus.ContainerAbsent"/> (never silently as "absent object"); a store that
/// cannot be read throws <see cref="ArchiveUnavailableException"/>. Cancellation by the caller propagates as
/// <see cref="OperationCanceledException"/>; an internal timeout is unavailability.
/// </para>
/// </summary>
public interface ISecurityEventArchiveInventory
{
    /// <summary>The archive-identity sentinel (AS-1), or Absent / ContainerAbsent. Never creates anything.</summary>
    Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken);

    /// <summary>Creates the sentinel once (create-only). An existing sentinel is never overwritten: its stored identity is returned.</summary>
    Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken);

    /// <summary>The archived entry bytes at the canonical key of (organization, stream, revision), or Absent / ContainerAbsent. Never creates anything.</summary>
    Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the complete listing of every object under the organization's prefix (or the organization+stream prefix when
    /// <paramref name="streamName"/> is given), in store order. Every object is listed, canonical or not (AE-1, AE-3); the caller pages
    /// with <see cref="ArchiveListingPage.ContinuationToken"/> until it is null. A failed page is unavailability, never an empty listing.
    /// </summary>
    Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken);

    /// <summary>
    /// SEC-BL-01 WP-4A (contract amendment A-1, bounded incremental listing): one page of the complete listing of every <b>canonical</b>
    /// entry of the stream with revision above <paramref name="afterRevision"/>. Its cost is bounded by the number of such entries (plus a
    /// constant number of requests), not by the stream's history. Non-canonical objects whose names fall outside the canonical revision
    /// space are <b>not</b> guaranteed to be listed here: they are detected by the full listing of the full sweep. Page with
    /// <see cref="ArchiveListingPage.ContinuationToken"/> until it is null; a failed page is unavailability.
    /// </summary>
    Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken);

    /// <summary>
    /// WP-5a.1 (amendment A-2 correction): a complete listing of the whole archive container: whether the container exists and how many
    /// objects it holds besides the identity sentinel. Evidence for provisioning only, never proof of freshness on its own (an empty or
    /// absent container is what a full wipe also looks like). A failed page or an exceeded page bound is
    /// <see cref="ArchiveUnavailableException"/>, never "empty".
    /// </summary>
    Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// WP-5a.2 (amendment A-3, SM-1/SM-3): the create-only D+ stop marker at <c>meta/dplus-stops/{organization:D}/{stream}/{revision:D12}.json</c>,
    /// outside every <c>org/</c> entry prefix. Identical bytes on retry ⇒ IdenticalExists; other bytes ⇒ Conflict; never overwritten.
    /// Unavailability throws <see cref="ArchiveUnavailableException"/>.
    /// </summary>
    Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken);

    /// <summary>
    /// WP-5a.2 (A-3 SM-5): every object under <c>meta/dplus-stops/{organization:D}/{stream}/</c> with its bytes (complete listing).
    /// <see cref="ArchiveMetaObject.FailedRevision"/> is set only for canonical names. A failed page or read is
    /// <see cref="ArchiveUnavailableException"/>, never "no marker".
    /// </summary>
    Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken);

    /// <summary>WP-5a.2 (A-3 SM-7): every object name under the reserved <c>meta/</c> prefix (complete listing, for the verifier).</summary>
    Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken);
}

/// <summary>Read status of one stored object (archive entry, sentinel or checkpoint).</summary>
public enum StoredObjectStatus
{
    Present,

    /// <summary>The store is reachable and the container exists, but no object has this key.</summary>
    Absent,

    /// <summary>The store is reachable but the container itself does not exist (wiped, replaced, never initialized or misconfigured).</summary>
    ContainerAbsent,
}

public sealed record StoredObjectRead(StoredObjectStatus Status, byte[]? Bytes)
{
    public static StoredObjectRead Absent { get; } = new(StoredObjectStatus.Absent, null);

    public static StoredObjectRead ContainerAbsent { get; } = new(StoredObjectStatus.ContainerAbsent, null);

    public static StoredObjectRead Present(byte[] bytes) => new(StoredObjectStatus.Present, bytes ?? throw new ArgumentNullException(nameof(bytes)));
}

/// <summary>The archive-identity sentinel content (AS-1): which archive instance is being read. It proves nothing about completeness or freshness (AS-4).</summary>
public sealed record ArchiveIdentity(string IdentitySchema, string ArchiveInstanceId, DateTimeOffset IdentityCreatedOnUtc)
{
    public const string SchemaV1 = "DynamicForms/ArchiveIdentity/v1";
}

public sealed record ArchiveIdentityRead(StoredObjectStatus Status, ArchiveIdentity? Identity, byte[]? Bytes);

public enum ArchiveIdentityCreateStatus
{
    Created,

    /// <summary>A sentinel already existed; it was not changed. <see cref="ArchiveIdentityCreateResult.Existing"/> is what is stored.</summary>
    AlreadyExists,
}

public sealed record ArchiveIdentityCreateResult(ArchiveIdentityCreateStatus Status, ArchiveIdentity? Existing, byte[] StoredBytes);

/// <summary>
/// One listed object. <see cref="StreamName"/> and <see cref="StreamRevision"/> are set only when the object name has the adapter's
/// canonical entry shape under the requested organization prefix (<see cref="IsCanonicalEntryKey"/>); every other object is listed
/// with both null. Whether the stream is a known one is decided by the caller (<c>ArchiveCompleteness</c>), not the adapter.
/// </summary>
public sealed record ArchiveListedObject(string ObjectName, bool IsCanonicalEntryKey, string? StreamName, long? StreamRevision);

public sealed record ArchiveListingPage(IReadOnlyList<ArchiveListedObject> Objects, string? ContinuationToken, bool ContainerAbsent);

/// <summary>WP-5a.1: the archive container's existence and its object count excluding the sentinel (complete listing).</summary>
public sealed record ArchiveContentSummary(bool ContainerAbsent, long ObjectCount);

/// <summary>WP-5a.2: one object under the stop-marker prefix; <see cref="FailedRevision"/> is null for a non-canonical name.</summary>
public sealed record ArchiveMetaObject(string ObjectName, long? FailedRevision, byte[] Bytes);
