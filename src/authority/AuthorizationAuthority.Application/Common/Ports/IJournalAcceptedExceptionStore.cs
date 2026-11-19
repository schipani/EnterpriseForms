namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 WP-4A (contract amendment A-1, §7.5): create-only accepted-exception records in the separately controlled checkpoint store,
/// keyed by (organization, stream, exception id). A record is never overwritten: identical bytes ⇒ IdenticalExists, other bytes ⇒ Conflict.
/// Unavailability throws <see cref="CheckpointStoreUnavailableException"/>. The Development/Testing store is <b>not</b> protected against a
/// sufficiently privileged administrator (J5/J6): a record there proves only what that administrator left in place.
/// </summary>
public interface IJournalAcceptedExceptionStore
{
    Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, string exceptionId, ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken);

    Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, string exceptionId, CancellationToken cancellationToken);
}
