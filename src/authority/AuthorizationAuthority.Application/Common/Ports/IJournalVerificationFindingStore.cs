namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 WP-4 (C-155, contract §9.3): create-only finding records beside the checkpoints, in the separately controlled store. A
/// record is keyed by the finding's identity (organization, stream, code, subtype, revision range), so a finding re-observed by a later
/// sweep is <see cref="FindingRecordStatus.AlreadyRecorded"/> and never overwritten. Content carries no event body or protected data.
/// Unavailability throws <see cref="CheckpointStoreUnavailableException"/>.
/// </summary>
public interface IJournalVerificationFindingStore
{
    Task<FindingRecordStatus> RecordAsync(Guid organizationId, string streamName, string findingIdentity, ReadOnlyMemory<byte> findingBytes, CancellationToken cancellationToken);

    /// <summary>
    /// WP-5a.2 (amendment A-3, SM-5): the bytes of every finding record of (organization, stream), complete. Read-only; a failed page or
    /// read throws <see cref="CheckpointStoreUnavailableException"/>, never "none".
    /// </summary>
    Task<IReadOnlyList<byte[]>> ListAsync(Guid organizationId, string streamName, CancellationToken cancellationToken);
}

public enum FindingRecordStatus
{
    Created,
    AlreadyRecorded,
}
