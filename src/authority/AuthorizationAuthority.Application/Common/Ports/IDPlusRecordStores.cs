namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 G14-R2 (contract amendment A-4, §24): create-only objects under the security-event archive's reserved <c>meta/</c> prefix, for
/// generation stop markers, recovery records and W2-S evidence commitments. It is a separate port so that the existing inventory and delivery
/// ports stay unchanged. Identical bytes on retry ⇒ IdenticalExists; other bytes ⇒ Conflict; nothing is ever overwritten or deleted.
/// Unavailability throws <see cref="ArchiveUnavailableException"/>, never "absent".
/// </summary>
public interface IArchiveMetaRecords
{
    Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken);

    /// <summary>Every object under <paramref name="metaPrefix"/> with its bytes (complete listing). A failed page or read is unavailability.</summary>
    Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken);
}

/// <summary>
/// SEC-BL-01 G14-R2 (A-4): create-only objects in the separately configured journal-verification store, outside its checkpoint, finding,
/// exception and provisioning names (recovery records under <c>recovery/</c>, evidence commitments under <c>intents/</c>). Same semantics as
/// <see cref="IArchiveMetaRecords"/>; unavailability throws <see cref="CheckpointStoreUnavailableException"/>. In Development/Testing the store
/// is configuration-separated from the archive, not independent of its administrator (PV-9, J6).
/// </summary>
public interface IJournalVerificationRecordStore
{
    Task<CheckpointCreateResult> CreateAsync(string objectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    Task<StoredObjectRead> ReadAsync(string objectName, CancellationToken cancellationToken);

    /// <summary>Every object under <paramref name="prefix"/> with its bytes (complete listing). A failed page or read is unavailability.</summary>
    Task<IReadOnlyList<ArchiveMetaObject>> ListAsync(string prefix, CancellationToken cancellationToken);
}
