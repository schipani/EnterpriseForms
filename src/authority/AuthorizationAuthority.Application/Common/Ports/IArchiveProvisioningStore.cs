namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 WP-5a.1 (amendment A-2 correction): the provisioning record of the security-event archive, kept in the separately configured
/// journal-verification store (a different Azurite instance and key than the archive), so deleting archive objects cannot recreate or
/// remove it. Create-only: identical bytes ⇒ IdenticalExists, other bytes ⇒ Conflict, never overwritten. Unavailability throws
/// <see cref="CheckpointStoreUnavailableException"/>. In Development/Testing the store is not protected against its own administrator (J6).
/// </summary>
public interface IArchiveProvisioningStore
{
    Task<CheckpointCreateResult> CreateRecordAsync(ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken);

    Task<StoredObjectRead> ReadRecordAsync(CancellationToken cancellationToken);

    /// <summary>True when a complete listing of the whole store finds any object (checkpoint, finding, exception or record).</summary>
    Task<bool> HoldsAnyObjectAsync(CancellationToken cancellationToken);
}
