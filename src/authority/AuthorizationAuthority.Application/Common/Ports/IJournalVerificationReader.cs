namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// SEC-BL-01 WP-3/WP-4 (C-155, contract §8.1): read-only access to AuthorityDb for the baseline and the scheduled verifier. One
/// snapshot is one consistent read-only view (PostgreSQL <c>REPEATABLE READ READ ONLY</c>); the port has no write operation, and the
/// PostgreSQL implementation refuses writes server-side for the whole snapshot. It reads journal rows and organization head columns
/// only, in bounded batches.
/// </summary>
public interface IJournalVerificationReader
{
    Task<IJournalVerificationSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken);
}

public interface IJournalVerificationSnapshot : IAsyncDisposable
{
    /// <summary>Organization identifiers in ascending order after <paramref name="after"/>, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> ListOrganizationsAsync(Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>WP-5a.1 (provisioning evidence): the number of security-journal and platform-journal rows of every organization.</summary>
    Task<long> CountJournalRowsAsync(CancellationToken cancellationToken);

    /// <summary>The stream's head columns and row statistics, or null when the organization is unknown.</summary>
    Task<JournalStreamHead?> ReadStreamHeadAsync(Guid organizationId, string streamName, CancellationToken cancellationToken);

    /// <summary>Rows of one stream with <c>fromRevision ≤ revision ≤ toRevision</c>, ascending, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<JournalRowSnapshot>> ReadRowsAsync(Guid organizationId, string streamName, long fromRevision, long toRevision, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Non-blocking exclusive sweep lock for (organization, stream), held until the snapshot is disposed (PostgreSQL: a
    /// transaction-scoped advisory lock, which writes no data). False when another sweep holds it.
    /// </summary>
    Task<bool> TryAcquireSweepLockAsync(Guid organizationId, string streamName, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="HeadRevisionColumn"/>/<see cref="HeadDigestColumn"/>: the organization row's stream head (authorization, content or audit
/// revision and latest digest), which AuthorityDb controls. <see cref="MaximumRowRevision"/>: the highest journal row (0 when none). Neither
/// is trusted; the verifier compares them with each other and with the archive. (No row count: it would scan the whole stream.)
/// </summary>
public sealed record JournalStreamHead(Guid OrganizationId, string StreamName, long HeadRevisionColumn, byte[]? HeadDigestColumn, long MaximumRowRevision);

/// <summary>One journal row as stored (body text, digests, delivery metadata). Never logged.</summary>
public sealed record JournalRowSnapshot(Guid SecurityJournalEntryId, Guid OrganizationId, string StreamName, long StreamRevision, string EventKind, string BodyJson,
    byte[] EventDigest, byte[]? PreviousDigest, string ArchiveDeliveryState, bool HasArchiveReceipt, DateTimeOffset JournalCreatedOn);
