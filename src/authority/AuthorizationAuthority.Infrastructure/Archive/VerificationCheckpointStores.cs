using System.Globalization;
using AuthorizationAuthority.Application.Common.Ports;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Infrastructure.Archive;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, contract §7.4): settings of the Development/Testing journal-verification checkpoint store. Not registered
/// in any host yet (the verifier, WP-4, is not authorized); production uses an immutable container plus Confidential Ledger (WP-8).
/// </summary>
public sealed class VerificationCheckpointStoreOptions
{
    public const string SectionName = "JournalVerificationCheckpointStore";

    /// <summary>"InMemory" (unit tests) or "Blob" (a separate Azurite instance in Development/Testing).</summary>
    public string Provider { get; set; } = "Unconfigured";

    public string? BlobServiceUri { get; set; }

    public string? AccountName { get; set; }

    public string? AccountKey { get; set; }

    /// <summary>WP-5a.1: a file holding the account key (a per-stack generated key); read once at startup when set.</summary>
    public string? AccountKeyFile { get; set; }

    public string ContainerName { get; set; } = "journal-verification";

    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Development/Testing checkpoint store on a <b>separate</b> Azurite instance with its own non-default account key. Construction refuses
/// the publicly known Azurite development key and any configuration that points at the security-event archive's endpoint, account or key,
/// so the store cannot silently be the archive. This separation is configuration, not independence: one Development administrator
/// controls both, and nothing here gives checkpoint-freshness assurance (contract §7.3 CKR-3, §7.4).
/// </summary>
public sealed class BlobVerificationCheckpointStore : IVerificationCheckpointStore
{
    /// <summary>The publicly documented Azurite / storage-emulator development key. Never acceptable for the checkpoint store.</summary>
    internal const string WellKnownDevelopmentStorageKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly BlobContainerClient container;
    private readonly TimeSpan timeout;

    public BlobVerificationCheckpointStore(IOptions<VerificationCheckpointStoreOptions> options, IOptions<ArchiveOptions> archiveOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(archiveOptions);
        var value = options.Value;
        if (RefusalReason(value, archiveOptions.Value) is { } reason)
        {
            throw new InvalidOperationException("JournalVerificationCheckpointStore refused: " + reason);
        }

        var service = new BlobServiceClient(new Uri(value.BlobServiceUri!), new StorageSharedKeyCredential(value.AccountName, value.AccountKey));
        container = service.GetBlobContainerClient(value.ContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
    }

    public string StoreName => "Blob(" + container.Name + ")";

    /// <summary>Why a Blob configuration is unacceptable, or null.</summary>
    internal static string? RefusalReason(VerificationCheckpointStoreOptions store, ArchiveOptions archive)
    {
        if (string.IsNullOrWhiteSpace(store.BlobServiceUri) || string.IsNullOrWhiteSpace(store.AccountName) || string.IsNullOrWhiteSpace(store.AccountKey))
        {
            return "BlobServiceUri, AccountName and AccountKey are required";
        }

        if (!Uri.TryCreate(store.BlobServiceUri, UriKind.Absolute, out var storeUri))
        {
            return "BlobServiceUri is not an absolute URI";
        }

        if (string.Equals(store.AccountKey, WellKnownDevelopmentStorageKey, StringComparison.Ordinal))
        {
            return "the publicly known Azurite development key is not permitted; configure a distinct, non-default key";
        }

        if (!string.IsNullOrEmpty(archive.AccountKey) && string.Equals(store.AccountKey, archive.AccountKey, StringComparison.Ordinal))
        {
            return "the account key equals the security-event archive's key";
        }

        if (!string.IsNullOrEmpty(archive.AccountName) && string.Equals(store.AccountName, archive.AccountName, StringComparison.OrdinalIgnoreCase))
        {
            return "the account name equals the security-event archive's account";
        }

        if (Uri.TryCreate(archive.BlobServiceUri, UriKind.Absolute, out var archiveUri)
            && string.Equals(storeUri.Host, archiveUri.Host, StringComparison.OrdinalIgnoreCase) && storeUri.Port == archiveUri.Port)
        {
            return "the endpoint is the security-event archive's endpoint; a separate store instance is required";
        }

        return null;
    }

    internal static string CheckpointBlobName(Guid organizationId, string streamName, long checkpointSequence) =>
        $"verification/{organizationId:D}/{streamName}/{checkpointSequence.ToString("D12", CultureInfo.InvariantCulture)}.json";

    public async Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, CheckpointBlobName(organizationId, streamName, checkpointSequence), checkpointBytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : existing.AsSpan().SequenceEqual(checkpointBytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, existing)
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, existing);
    }

    public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        return BlobInventorySupport.ReadAsync(container, CheckpointBlobName(organizationId, streamName, checkpointSequence), timeout, Unavailable, cancellationToken);
    }

    public async Task<CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var prefix = $"verification/{organizationId:D}/{streamName}/";
        var (names, continuation, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, prefix, continuationToken, pageSizeHint, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return new CheckpointListingPage([.. names.Select(n => Classify(n, prefix))], continuation, containerAbsent);
    }

    internal static CheckpointListedObject Classify(string name, string prefix)
    {
        var rest = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : null;
        return rest is not null && !rest.Contains('/', StringComparison.Ordinal) && BlobInventorySupport.ParseCanonicalNumberFile(rest) is { } sequence
            ? new CheckpointListedObject(name, true, sequence)
            : new CheckpointListedObject(name, false, null);
    }

    private static Exception Unavailable(string detail, Exception? inner) => new CheckpointStoreUnavailableException("checkpoint store " + detail, inner);
}

/// <summary>Unit-test checkpoint store. Not durable and not independent; never a Development, Testing-integration, Staging or Production store.</summary>
public sealed class InMemoryVerificationCheckpointStore : IVerificationCheckpointStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);

    public string StoreName => "InMemory";

    private static string Key(Guid organizationId, string streamName, long sequence) =>
        $"{organizationId:D}/{streamName}/{sequence.ToString(CultureInfo.InvariantCulture)}";

    public Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var candidate = checkpointBytes.ToArray();
        var stored = objects.GetOrAdd(Key(organizationId, streamName, checkpointSequence), candidate);
        return Task.FromResult(ReferenceEquals(stored, candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : stored.AsSpan().SequenceEqual(candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, stored.ToArray())
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, stored.ToArray()));
    }

    public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        return Task.FromResult(objects.TryGetValue(Key(organizationId, streamName, checkpointSequence), out var stored) ? StoredObjectRead.Present(stored.ToArray()) : StoredObjectRead.Absent);
    }

    public Task<CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var prefix = $"{organizationId:D}/{streamName}/";
        var names = objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var offset = continuationToken is null ? 0 : int.Parse(continuationToken, NumberStyles.None, CultureInfo.InvariantCulture);
        var size = Math.Clamp(pageSizeHint, 1, 5000);
        var page = names.Skip(offset).Take(size).Select(n =>
        {
            var rest = n[prefix.Length..];
            return long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence >= 1
                && string.Equals(sequence.ToString(CultureInfo.InvariantCulture), rest, StringComparison.Ordinal)
                ? new CheckpointListedObject(n, true, sequence) : new CheckpointListedObject(n, false, null);
        }).ToList();
        var next = offset + size < names.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new CheckpointListingPage(page, next, false));
    }

    /// <summary>Test-only: arbitrary bytes at an arbitrary key (emulates checkpoint-store compromise, J6).</summary>
    internal void PutObjectForTesting(string key, byte[] bytes) => objects[key] = bytes.ToArray();

    /// <summary>Test-only: removes an object (emulates deletion of the newest checkpoints, CKR-3).</summary>
    internal bool RemoveObjectForTesting(string key) => objects.TryRemove(key, out _);

    internal static string KeyForTesting(Guid organizationId, string streamName, long sequence) => Key(organizationId, streamName, sequence);
}

/// <summary>
/// SEC-BL-01 WP-4 (contract §9.3): create-only finding records in the Development/Testing checkpoint store's container, under
/// <c>findings/{organization}/{stream}/{identity}.json</c>. Same configuration and refusal rules as <see cref="BlobVerificationCheckpointStore"/>.
/// </summary>
public sealed class BlobJournalVerificationFindingStore : IJournalVerificationFindingStore
{
    private readonly BlobContainerClient container;
    private readonly TimeSpan timeout;

    public BlobJournalVerificationFindingStore(IOptions<VerificationCheckpointStoreOptions> options, IOptions<ArchiveOptions> archiveOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(archiveOptions);
        var value = options.Value;
        if (BlobVerificationCheckpointStore.RefusalReason(value, archiveOptions.Value) is { } reason)
        {
            throw new InvalidOperationException("JournalVerificationCheckpointStore refused: " + reason);
        }

        container = new BlobServiceClient(new Uri(value.BlobServiceUri!), new StorageSharedKeyCredential(value.AccountName, value.AccountKey)).GetBlobContainerClient(value.ContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
    }

    internal static string FindingBlobName(Guid organizationId, string streamName, string findingIdentity) => $"findings/{organizationId:D}/{streamName}/{findingIdentity}.json";

    public async Task<FindingRecordStatus> RecordAsync(Guid organizationId, string streamName, string findingIdentity, ReadOnlyMemory<byte> findingBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        ArgumentException.ThrowIfNullOrEmpty(findingIdentity);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, FindingBlobName(organizationId, streamName, findingIdentity), findingBytes, timeout,
            (detail, inner) => new CheckpointStoreUnavailableException("finding store " + detail, inner), cancellationToken).ConfigureAwait(false);
        return existing is null ? FindingRecordStatus.Created : FindingRecordStatus.AlreadyRecorded;
    }

    /// <summary>WP-5a.2 (A-3 SM-5): complete listing of <c>findings/{organization}/{stream}/</c>, each record read.</summary>
    public async Task<IReadOnlyList<byte[]>> ListAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var prefix = $"findings/{organizationId:D}/{streamName}/";
        var result = new List<byte[]>();
        string? continuation = null;
        for (var page = 0; page < 100_000; page++)
        {
            var (names, next, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, prefix, continuation, 1000, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            if (containerAbsent)
            {
                return result;
            }

            foreach (var name in names)
            {
                var read = await BlobInventorySupport.ReadAsync(container, name, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
                result.Add(read.Status == StoredObjectStatus.Present ? read.Bytes! : throw new CheckpointStoreUnavailableException("a listed finding could not be read"));
            }

            if (next is null)
            {
                return result;
            }

            continuation = next;
        }

        throw new CheckpointStoreUnavailableException("finding listing exceeded the page bound");
    }

    private static Exception Unavailable(string detail, Exception? inner) => new CheckpointStoreUnavailableException("finding store " + detail, inner);
}

/// <summary>Unit-test finding store (create-only by identity). Not durable, not independent.</summary>
public sealed class InMemoryJournalVerificationFindingStore : IJournalVerificationFindingStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> records = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Records => records;

    public Task<FindingRecordStatus> RecordAsync(Guid organizationId, string streamName, string findingIdentity, ReadOnlyMemory<byte> findingBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(records.TryAdd($"{organizationId:D}/{streamName}/{findingIdentity}", findingBytes.ToArray()) ? FindingRecordStatus.Created : FindingRecordStatus.AlreadyRecorded);
    }

    public Task<IReadOnlyList<byte[]>> ListAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = $"{organizationId:D}/{streamName}/";
        return Task.FromResult<IReadOnlyList<byte[]>>([.. records.Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value.ToArray())]);
    }

    /// <summary>Test-only: removes a finding record (emulates J6).</summary>
    internal bool RemoveForTesting(Guid organizationId, string streamName, string findingIdentity) => records.TryRemove($"{organizationId:D}/{streamName}/{findingIdentity}", out _);
}

/// <summary>
/// SEC-BL-01 WP-4A (contract amendment A-1, §7.5): create-only accepted-exception records in the Development/Testing checkpoint store's
/// container, under <c>exceptions/{organization}/{stream}/{exceptionId}.json</c>. Same configuration and refusal rules as
/// <see cref="BlobVerificationCheckpointStore"/>. Not protected against a privileged administrator of that store (J5/J6).
/// </summary>
public sealed class BlobJournalAcceptedExceptionStore : IJournalAcceptedExceptionStore
{
    private readonly BlobContainerClient container;
    private readonly TimeSpan timeout;

    public BlobJournalAcceptedExceptionStore(IOptions<VerificationCheckpointStoreOptions> options, IOptions<ArchiveOptions> archiveOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(archiveOptions);
        var value = options.Value;
        if (BlobVerificationCheckpointStore.RefusalReason(value, archiveOptions.Value) is { } reason)
        {
            throw new InvalidOperationException("JournalVerificationCheckpointStore refused: " + reason);
        }

        container = new BlobServiceClient(new Uri(value.BlobServiceUri!), new StorageSharedKeyCredential(value.AccountName, value.AccountKey)).GetBlobContainerClient(value.ContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
    }

    internal static string ExceptionBlobName(Guid organizationId, string streamName, string exceptionId) => $"exceptions/{organizationId:D}/{streamName}/{exceptionId}.json";

    public async Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, string exceptionId, ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        InMemoryJournalAcceptedExceptionStore.RequireIdentifier(exceptionId);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, ExceptionBlobName(organizationId, streamName, exceptionId), recordBytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : existing.AsSpan().SequenceEqual(recordBytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, existing)
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, existing);
    }

    public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, string exceptionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        InMemoryJournalAcceptedExceptionStore.RequireIdentifier(exceptionId);
        return BlobInventorySupport.ReadAsync(container, ExceptionBlobName(organizationId, streamName, exceptionId), timeout, Unavailable, cancellationToken);
    }

    private static Exception Unavailable(string detail, Exception? inner) => new CheckpointStoreUnavailableException("accepted-exception store " + detail, inner);
}

/// <summary>Unit-test accepted-exception store (create-only). Not durable, not independent.</summary>
public sealed class InMemoryJournalAcceptedExceptionStore : IJournalAcceptedExceptionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> records = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Records => records;

    internal static void RequireIdentifier(string exceptionId)
    {
        if (string.IsNullOrEmpty(exceptionId) || exceptionId.Length > 64 || !exceptionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException("invalid accepted-exception identifier", nameof(exceptionId));
        }
    }

    private static string Key(Guid organizationId, string streamName, string exceptionId) => $"{organizationId:D}/{streamName}/{exceptionId}";

    public Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, string exceptionId, ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireIdentifier(exceptionId);
        var candidate = recordBytes.ToArray();
        var stored = records.GetOrAdd(Key(organizationId, streamName, exceptionId), candidate);
        return Task.FromResult(ReferenceEquals(stored, candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : stored.AsSpan().SequenceEqual(candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, stored.ToArray())
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, stored.ToArray()));
    }

    public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, string exceptionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireIdentifier(exceptionId);
        return Task.FromResult(records.TryGetValue(Key(organizationId, streamName, exceptionId), out var stored) ? StoredObjectRead.Present(stored.ToArray()) : StoredObjectRead.Absent);
    }

    /// <summary>Test-only: replaces a record (emulates tampering with the checkpoint store, J6).</summary>
    internal void PutForTesting(Guid organizationId, string streamName, string exceptionId, byte[] bytes) => records[Key(organizationId, streamName, exceptionId)] = bytes.ToArray();

    /// <summary>Test-only: removes a record.</summary>
    internal bool RemoveForTesting(Guid organizationId, string streamName, string exceptionId) => records.TryRemove(Key(organizationId, streamName, exceptionId), out _);
}

/// <summary>
/// SEC-BL-01 WP-5a.1: the archive provisioning record at <c>provisioning/archive-provisioning.json</c> in the journal-verification store's
/// container. Same configuration and refusal rules as <see cref="BlobVerificationCheckpointStore"/> (never the archive's endpoint, account or key).
/// </summary>
public sealed class BlobArchiveProvisioningStore : IArchiveProvisioningStore
{
    internal const string RecordBlobName = "provisioning/archive-provisioning.json";

    private readonly BlobContainerClient container;
    private readonly TimeSpan timeout;

    public BlobArchiveProvisioningStore(IOptions<VerificationCheckpointStoreOptions> options, IOptions<ArchiveOptions> archiveOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(archiveOptions);
        var value = options.Value;
        if (BlobVerificationCheckpointStore.RefusalReason(value, archiveOptions.Value) is { } reason)
        {
            throw new InvalidOperationException("JournalVerificationCheckpointStore refused: " + reason);
        }

        container = new BlobServiceClient(new Uri(value.BlobServiceUri!), new StorageSharedKeyCredential(value.AccountName, value.AccountKey)).GetBlobContainerClient(value.ContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
    }

    public async Task<CheckpointCreateResult> CreateRecordAsync(ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken)
    {
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, RecordBlobName, recordBytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : existing.AsSpan().SequenceEqual(recordBytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, existing)
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, existing);
    }

    public Task<StoredObjectRead> ReadRecordAsync(CancellationToken cancellationToken) => BlobInventorySupport.ReadAsync(container, RecordBlobName, timeout, Unavailable, cancellationToken);

    public async Task<bool> HoldsAnyObjectAsync(CancellationToken cancellationToken)
    {
        var (names, _, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, string.Empty, null, 1, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return !containerAbsent && names.Count > 0;
    }

    private static Exception Unavailable(string detail, Exception? inner) => new CheckpointStoreUnavailableException("provisioning store " + detail, inner);
}

/// <summary>Unit-test provisioning store (create-only). Not durable, not independent.</summary>
public sealed class InMemoryArchiveProvisioningStore : IArchiveProvisioningStore
{
    private readonly Lock gate = new();
    private byte[]? record;

    /// <summary>Other objects of the store (checkpoints, findings); tests set it to emulate a used store.</summary>
    public bool HoldsOtherObjects { get; set; }

    public Task<CheckpointCreateResult> CreateRecordAsync(ReadOnlyMemory<byte> recordBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (record is null)
            {
                record = recordBytes.ToArray();
                return Task.FromResult(new CheckpointCreateResult(CheckpointCreateStatus.Created, null));
            }

            return Task.FromResult(record.AsSpan().SequenceEqual(recordBytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, record.ToArray())
                : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, record.ToArray()));
        }
    }

    public Task<StoredObjectRead> ReadRecordAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(record is null ? StoredObjectRead.Absent : StoredObjectRead.Present(record.ToArray()));
        }
    }

    public Task<bool> HoldsAnyObjectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(record is not null || HoldsOtherObjects);
        }
    }

    /// <summary>Test-only: removes the record (emulates loss of the provisioning record, J6).</summary>
    internal void RemoveRecordForTesting()
    {
        lock (gate)
        {
            record = null;
        }
    }
}
/// <summary>
/// SEC-BL-01 G14-R2 (contract amendment A-4, §24): create-only recovery records (<c>recovery/</c>) and W2-S evidence commitments
/// (<c>intents/</c>) in the Development/Testing journal-verification store's container. Same configuration and refusal rules as
/// <see cref="BlobVerificationCheckpointStore"/> (never the archive's endpoint, account or key). Not protected against its own administrator (J6).
/// </summary>
public sealed class BlobJournalVerificationRecordStore : IJournalVerificationRecordStore
{
    private readonly BlobContainerClient container;
    private readonly TimeSpan timeout;

    public BlobJournalVerificationRecordStore(IOptions<VerificationCheckpointStoreOptions> options, IOptions<ArchiveOptions> archiveOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(archiveOptions);
        var value = options.Value;
        if (BlobVerificationCheckpointStore.RefusalReason(value, archiveOptions.Value) is { } reason)
        {
            throw new InvalidOperationException("JournalVerificationCheckpointStore refused: " + reason);
        }

        container = new BlobServiceClient(new Uri(value.BlobServiceUri!), new StorageSharedKeyCredential(value.AccountName, value.AccountKey)).GetBlobContainerClient(value.ContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
    }

    internal static void RequireRecordName(string name)
    {
        if (string.IsNullOrEmpty(name) || !(name.StartsWith("recovery/", StringComparison.Ordinal) || name.StartsWith("intents/", StringComparison.Ordinal))
            || name.Contains("..", StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("not a recovery or commitment record name", nameof(name));
        }
    }

    public async Task<CheckpointCreateResult> CreateAsync(string objectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        RequireRecordName(objectName);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, objectName, bytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : existing.AsSpan().SequenceEqual(bytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, existing)
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, existing);
    }

    public Task<StoredObjectRead> ReadAsync(string objectName, CancellationToken cancellationToken)
    {
        RequireRecordName(objectName);
        return BlobInventorySupport.ReadAsync(container, objectName, timeout, Unavailable, cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveMetaObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        RequireRecordName(prefix);
        var result = new List<ArchiveMetaObject>();
        string? continuation = null;
        for (var page = 0; page < 100_000; page++)
        {
            var (names, next, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, prefix, continuation, 1000, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            if (containerAbsent)
            {
                return result;
            }

            foreach (var name in names)
            {
                var read = await BlobInventorySupport.ReadAsync(container, name, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
                result.Add(read.Status == StoredObjectStatus.Present ? new ArchiveMetaObject(name, null, read.Bytes!) : throw new CheckpointStoreUnavailableException("a listed record could not be read"));
            }

            if (next is null)
            {
                return result;
            }

            continuation = next;
        }

        throw new CheckpointStoreUnavailableException("record listing exceeded the page bound");
    }

    private static Exception Unavailable(string detail, Exception? inner) => new CheckpointStoreUnavailableException("record store " + detail, inner);
}

/// <summary>Unit-test recovery-record and commitment store (create-only). Not durable, not independent.</summary>
public sealed class InMemoryJournalVerificationRecordStore : IJournalVerificationRecordStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> records = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Records => records;

    /// <summary>Test-only: when set, every operation throws <see cref="CheckpointStoreUnavailableException"/>.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Test-only: when set, operations on names starting with this prefix throw <see cref="CheckpointStoreUnavailableException"/>.</summary>
    public string? UnavailablePrefix { get; set; }

    public Task<CheckpointCreateResult> CreateAsync(string objectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlobJournalVerificationRecordStore.RequireRecordName(objectName);
        ThrowIfUnavailable(objectName);
        var candidate = bytes.ToArray();
        var stored = records.GetOrAdd(objectName, candidate);
        return Task.FromResult(ReferenceEquals(stored, candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : stored.AsSpan().SequenceEqual(candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, stored.ToArray())
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, stored.ToArray()));
    }

    public Task<StoredObjectRead> ReadAsync(string objectName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable(objectName);
        return Task.FromResult(records.TryGetValue(objectName, out var stored) ? StoredObjectRead.Present(stored.ToArray()) : StoredObjectRead.Absent);
    }

    public Task<IReadOnlyList<ArchiveMetaObject>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable(prefix);
        return Task.FromResult<IReadOnlyList<ArchiveMetaObject>>([.. records.Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new ArchiveMetaObject(r.Key, null, r.Value.ToArray()))]);
    }

    private void ThrowIfUnavailable(string name)
    {
        if (Unavailable || (UnavailablePrefix is { } prefix && name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new CheckpointStoreUnavailableException("record store unavailable (test)");
        }
    }

    /// <summary>Test-only: replaces or places a record (emulates J6).</summary>
    internal void PutForTesting(string objectName, byte[] bytes) => records[objectName] = bytes.ToArray();

    /// <summary>Test-only: removes a record (emulates J6).</summary>
    internal bool RemoveForTesting(string objectName) => records.TryRemove(objectName, out _);
}
