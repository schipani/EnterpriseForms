using System.Globalization;
using System.Security.Cryptography;

using AuthorizationAuthority.Application.Common.Ports;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Infrastructure.Archive;

public sealed class ArchiveOptions
{
    public const string SectionName = "SecurityArchive";

    /// <summary>"InMemory" (unit tests / local Development) or "Blob" (Docker Azurite on the authority network; Azure immutable storage in production).</summary>
    public string Provider { get; set; } = "Unconfigured";

    public string? BlobServiceUri { get; set; }

    public string? AccountName { get; set; }

    public string? AccountKey { get; set; }

    public string ContainerName { get; set; } = "security-journal";

    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Append-only blob archive. Writes use <c>If-None-Match: *</c> so an existing object is never overwritten; a
/// re-delivery of identical bytes is idempotent, different bytes under the same key is a verification failure.
/// The Docker deployment uses Azurite as a TEST DOUBLE for a separately administered WORM store (ADR-PB-003 Level A);
/// it is not Azure immutable storage, a legal hold or a Confidential Ledger receipt.
/// </summary>
public sealed class BlobSecurityEventArchive : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
{
    private readonly BlobContainerClient container;
    private readonly TimeSpan timeout;

    public BlobSecurityEventArchive(IOptions<ArchiveOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.BlobServiceUri) || string.IsNullOrWhiteSpace(value.AccountName) || string.IsNullOrWhiteSpace(value.AccountKey))
        {
            throw new InvalidOperationException("SecurityArchive blob settings are required for the Blob archive provider.");
        }

        var service = new BlobServiceClient(new Uri(value.BlobServiceUri), new StorageSharedKeyCredential(value.AccountName, value.AccountKey));
        container = service.GetBlobContainerClient(value.ContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
    }

    public string ArchiveName => "Blob(" + container.Name + ")";

    public async Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedDigest);
        var name = $"org/{organizationId:D}/{streamName}/{streamRevision.ToString("D12", CultureInfo.InvariantCulture)}.json";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var blob = container.GetBlobClient(name);
        try
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cts.Token).ConfigureAwait(false);
            using var stream = new MemoryStream(eventBody.ToArray(), writable: false);
            var response = await blob.UploadAsync(stream, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Metadata = new Dictionary<string, string> { ["event_digest_sha256"] = Convert.ToHexStringLower(expectedDigest) },
            }, cts.Token).ConfigureAwait(false);
            return Receipt(name, response.Value.ETag.ToString(), expectedDigest);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Exists already: verify the stored bytes are the same event (idempotent retry) and never overwrite.
            try
            {
                var existing = await blob.DownloadContentAsync(cts.Token).ConfigureAwait(false);
                var digest = SHA256.HashData(existing.Value.Content.ToArray());
                if (!digest.AsSpan().SequenceEqual(expectedDigest))
                {
                    throw new ArchiveConflictException("archived body differs from the journal entry");
                }

                return Receipt(name, existing.Value.Details.ETag.ToString(), expectedDigest);
            }
            catch (RequestFailedException inner)
            {
                throw new ArchiveUnavailableException("archive read-back failed: " + inner.Status, inner);
            }
            catch (AggregateException inner)
            {
                // SEC-BL-01 WP-4A (OBS-1): exhausted SDK retries with mixed failures; unavailability unless the caller cancelled.
                cancellationToken.ThrowIfCancellationRequested();
                throw new ArchiveUnavailableException("archive read-back failed after retries", inner);
            }
            catch (OperationCanceledException inner) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ArchiveUnavailableException("archive read-back timeout", inner);
            }
        }
        catch (RequestFailedException ex)
        {
            throw new ArchiveUnavailableException("archive write failed: " + ex.Status, ex);
        }
        catch (AggregateException ex)
        {
            // SEC-BL-01 WP-4A (OBS-1): the SDK reports exhausted retries with mixed failures (connection refused, then a timeout) as an
            // AggregateException. It is archive unavailability (the entry stays Pending and is retried), unless the caller cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            throw new ArchiveUnavailableException("archive write failed after retries", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArchiveUnavailableException("archive timeout", ex);
        }
    }

    public async Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken)
    {
        var name = $"org/{organizationId:D}/{streamName}/{streamRevision.ToString("D12", CultureInfo.InvariantCulture)}.json";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var existing = await container.GetBlobClient(name).DownloadContentAsync(cts.Token).ConfigureAwait(false);
            return existing.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // nothing archived at this key (or the container does not exist yet)
        }
        catch (RequestFailedException ex)
        {
            throw new ArchiveUnavailableException("archive read failed: " + ex.Status, ex);
        }
        catch (AggregateException ex)
        {
            // SEC-BL-01 WP-4A (OBS-1): see AppendAsync.
            cancellationToken.ThrowIfCancellationRequested();
            throw new ArchiveUnavailableException("archive read failed after retries", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArchiveUnavailableException("archive timeout", ex);
        }
    }

    public async Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cts.Token).ConfigureAwait(false);
            return (true, "archive reachable (Azurite TEST DOUBLE for WORM storage)");
        }
        catch (RequestFailedException ex)
        {
            return (false, "ArchiveUnavailable:" + ex.Status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "ArchiveTimeout");
        }
    }

    // ---- SEC-BL-01 WP-2 (C-155): read-side inventory. WP-2 left delivery (AppendAsync), ReadAsync and ProbeAsync above unchanged; WP-4A
    // (OBS-1) changed only their exception classification (AggregateException and read-back timeout ⇒ ArchiveUnavailableException). ----

    /// <summary>The sentinel's reserved name: outside every <c>org/</c> entry prefix, so it is never listed as an entry.</summary>
    internal const string IdentityBlobName = "meta/archive-identity.json";

    /// <summary>The canonical entry name; identical to the name <see cref="AppendAsync"/> and <see cref="ReadAsync"/> use (pinned by the Azurite integration test).</summary>
    internal static string EntryBlobName(Guid organizationId, string streamName, long streamRevision) =>
        $"org/{organizationId:D}/{streamName}/{streamRevision.ToString("D12", CultureInfo.InvariantCulture)}.json";

    public async Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        var read = await BlobInventorySupport.ReadAsync(container, IdentityBlobName, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return read.Status == StoredObjectStatus.Present
            ? new ArchiveIdentityRead(StoredObjectStatus.Present, Application.Features.Journal.Verification.ArchiveIdentityCodec.TryParse(read.Bytes), read.Bytes)
            : new ArchiveIdentityRead(read.Status, null, null);
    }

    public async Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken)
    {
        var bytes = Application.Features.Journal.Verification.ArchiveIdentityCodec.Serialize(identity);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, IdentityBlobName, bytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null
            ? new ArchiveIdentityCreateResult(ArchiveIdentityCreateStatus.Created, identity, bytes)
            : new ArchiveIdentityCreateResult(ArchiveIdentityCreateStatus.AlreadyExists, Application.Features.Journal.Verification.ArchiveIdentityCodec.TryParse(existing), existing);
    }

    public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        return BlobInventorySupport.ReadAsync(container, EntryBlobName(organizationId, streamName, streamRevision), timeout, Unavailable, cancellationToken);
    }

    public async Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken)
    {
        var organizationPrefix = $"org/{organizationId:D}/";
        var prefix = streamName is null ? organizationPrefix : organizationPrefix + streamName + "/";
        var (names, continuation, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, prefix, continuationToken, pageSizeHint, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return new ArchiveListingPage([.. names.Select(n => Classify(n, organizationPrefix, streamName))], continuation, containerAbsent);
    }

    /// <summary>
    /// WP-4A bounded listing: the canonical names above <paramref name="afterRevision"/> are exactly those starting with one of the decimal
    /// prefixes of <see cref="RevisionPrefixesAbove"/> (at most 108), so the listing costs those prefix requests plus the entries above the
    /// revision, independent of history. The continuation token is <c>{prefix index}|{blob continuation}</c>.
    /// </summary>
    public async Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var organizationPrefix = $"org/{organizationId:D}/";
        var streamPrefix = organizationPrefix + streamName + "/";
        var prefixes = RevisionPrefixesAbove(afterRevision);
        var index = 0;
        string? blobToken = null;
        if (!string.IsNullOrEmpty(continuationToken))
        {
            var separator = continuationToken.IndexOf('|', StringComparison.Ordinal);
            index = int.Parse(continuationToken.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture);
            blobToken = separator + 1 < continuationToken.Length ? continuationToken[(separator + 1)..] : null;
        }

        while (index < prefixes.Count)
        {
            var (names, next, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, streamPrefix + prefixes[index], blobToken, pageSizeHint, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            if (containerAbsent)
            {
                return new ArchiveListingPage([], null, true);
            }

            var objects = names.Select(n => Classify(n, organizationPrefix, streamName)).Where(o => !o.IsCanonicalEntryKey || o.StreamRevision > afterRevision).ToList();
            var token = next is not null ? index.ToString(CultureInfo.InvariantCulture) + "|" + next
                : index + 1 < prefixes.Count ? (index + 1).ToString(CultureInfo.InvariantCulture) + "|" : null;
            if (objects.Count > 0 || token is null)
            {
                return new ArchiveListingPage(objects, token, false);
            }

            // An empty page: continue within this call (bounded by the prefix count) instead of returning empty pages.
            if (next is not null)
            {
                blobToken = next;
            }
            else
            {
                index++;
                blobToken = null;
            }
        }

        return new ArchiveListingPage([], null, false);
    }

    /// <summary>WP-5a.1: pages through the whole container; counts every object except the sentinel.</summary>
    public async Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken)
    {
        string? continuation = null;
        long count = 0;
        for (var page = 0; page < 100_000; page++)
        {
            var (names, next, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, string.Empty, continuation, 1000, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            if (containerAbsent)
            {
                return new ArchiveContentSummary(true, 0);
            }

            count += names.Count(n => !string.Equals(n, IdentityBlobName, StringComparison.Ordinal));
            if (next is null)
            {
                return new ArchiveContentSummary(false, count);
            }

            continuation = next;
        }

        throw new ArchiveUnavailableException("archive listing exceeded the page bound; the archive content is not established");
    }
    internal const string StopMarkerPrefix = "meta/dplus-stops/";

    internal static string StopMarkerBlobName(Guid organizationId, string streamName, long failedRevision) =>
        $"{StopMarkerPrefix}{organizationId:D}/{streamName}/{failedRevision.ToString("D12", CultureInfo.InvariantCulture)}.json";

    /// <summary>WP-5a.2 (A-3): create-only stop marker; never overwritten.</summary>
    public async Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, StopMarkerBlobName(organizationId, streamName, failedRevision), markerBytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : existing.AsSpan().SequenceEqual(markerBytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, existing)
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, existing);
    }

    /// <summary>WP-5a.2 (A-3): complete listing of the stream's stop markers, each read.</summary>
    public async Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var prefix = $"{StopMarkerPrefix}{organizationId:D}/{streamName}/";
        var result = new List<ArchiveMetaObject>();
        foreach (var name in await ListAllAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            var read = await BlobInventorySupport.ReadAsync(container, name, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            if (read.Status != StoredObjectStatus.Present)
            {
                throw new ArchiveUnavailableException("a listed stop marker could not be read");
            }

            var rest = name[prefix.Length..];
            result.Add(new ArchiveMetaObject(name, rest.Contains('/', StringComparison.Ordinal) ? null : BlobInventorySupport.ParseCanonicalNumberFile(rest), read.Bytes!));
        }

        return result;
    }

    /// <summary>WP-5a.2 (A-3 SM-7): every name under <c>meta/</c>.</summary>
    public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => ListAllAsync("meta/", cancellationToken);

    // ---- G14-R2 (A-4 §24): generic create-only objects under the reserved meta/ prefix ----

    private static void RequireMetaName(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("meta/", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)
            || string.Equals(name, IdentityBlobName, StringComparison.Ordinal))
        {
            throw new ArgumentException("not a writable meta/ object name", nameof(name));
        }
    }

    public async Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        RequireMetaName(metaObjectName);
        var existing = await BlobInventorySupport.CreateOnlyAsync(container, metaObjectName, bytes, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
        return existing is null ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : existing.AsSpan().SequenceEqual(bytes.Span) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, existing)
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, existing);
    }

    public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken)
    {
        RequireMetaName(metaObjectName);
        return BlobInventorySupport.ReadAsync(container, metaObjectName, timeout, Unavailable, cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken)
    {
        RequireMetaName(metaPrefix);
        var result = new List<ArchiveMetaObject>();
        foreach (var name in await ListAllAsync(metaPrefix, cancellationToken).ConfigureAwait(false))
        {
            var read = await BlobInventorySupport.ReadAsync(container, name, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            result.Add(read.Status == StoredObjectStatus.Present ? new ArchiveMetaObject(name, null, read.Bytes!) : throw new ArchiveUnavailableException("a listed meta object could not be read"));
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> ListAllAsync(string prefix, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        string? continuation = null;
        for (var page = 0; page < 100_000; page++)
        {
            var (batch, next, containerAbsent) = await BlobInventorySupport.ListPageAsync(container, prefix, continuation, 1000, timeout, Unavailable, cancellationToken).ConfigureAwait(false);
            if (containerAbsent)
            {
                return names;
            }

            names.AddRange(batch);
            if (next is null)
            {
                return names;
            }

            continuation = next;
        }

        throw new ArchiveUnavailableException("archive listing exceeded the page bound");
    }

    /// <summary>
    /// The decimal prefixes (of the 12-digit zero-padded revision) whose union is exactly the revisions above <paramref name="afterRevision"/>,
    /// in ascending order: the next revision itself, then, from the last digit to the first, every greater digit after the shared prefix.
    /// </summary>
    internal static IReadOnlyList<string> RevisionPrefixesAbove(long afterRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        var next = afterRevision + 1;
        if (next > 999_999_999_999)
        {
            return [];
        }

        var digits = next.ToString("D12", CultureInfo.InvariantCulture);
        var prefixes = new List<string> { digits };
        for (var position = 11; position >= 0; position--)
        {
            for (var digit = digits[position] + 1; digit <= '9'; digit++)
            {
                prefixes.Add(string.Concat(digits.AsSpan(0, position), ((char)digit).ToString()));
            }
        }

        return prefixes;
    }

    /// <summary>Canonical entry shape: <c>org/{organization}/{stream}/{revision:D12}.json</c> with revision ≥ 1; anything else is listed as non-canonical.</summary>
    internal static ArchiveListedObject Classify(string name, string organizationPrefix, string? requestedStream)
    {
        if (name.StartsWith(organizationPrefix, StringComparison.Ordinal))
        {
            var parts = name[organizationPrefix.Length..].Split('/');
            if (parts.Length == 2 && parts[0].Length > 0 && (requestedStream is null || string.Equals(parts[0], requestedStream, StringComparison.Ordinal))
                && BlobInventorySupport.ParseCanonicalNumberFile(parts[1]) is { } revision)
            {
                return new ArchiveListedObject(name, true, parts[0], revision);
            }
        }

        return new ArchiveListedObject(name, false, null, null);
    }

    private static Exception Unavailable(string detail, Exception? inner) => new ArchiveUnavailableException("archive " + detail, inner);

    private static ArchiveReceipt Receipt(string name, string etag, byte[] digest)
    {
        var reference = name + "|" + etag;
        return new ArchiveReceipt(reference, SHA256.HashData([.. System.Text.Encoding.UTF8.GetBytes(reference), .. digest]));
    }
}

/// <summary>Unit-test and local Development archive. Not durable; never permitted in Testing-integration, Staging or Production.</summary>
public sealed class InMemorySecurityEventArchive : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> entries = new(StringComparer.Ordinal);
    private readonly Lock identityLock = new();
    private byte[]? identityBytes;

    public string ArchiveName => "InMemory";

    public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken)
    {
        var key = $"{organizationId:D}/{streamName}/{streamRevision}";
        var stored = entries.GetOrAdd(key, _ => eventBody.ToArray());
        if (!SHA256.HashData(stored).AsSpan().SequenceEqual(expectedDigest))
        {
            throw new ArchiveConflictException("archived body differs");
        }

        return Task.FromResult(new ArchiveReceipt(key, SHA256.HashData(expectedDigest)));
    }

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "InMemory archive (NOT durable; development only)"));

    public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
        Task.FromResult(entries.TryGetValue($"{organizationId:D}/{streamName}/{streamRevision}", out var stored) ? stored.ToArray() : null);

    // ---- SEC-BL-01 WP-2 (C-155): read-side inventory. Not durable (see the class remark); there is no container, so ContainerAbsent never occurs. ----

    public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (identityLock)
        {
            return Task.FromResult(identityBytes is { } bytes
                ? new ArchiveIdentityRead(StoredObjectStatus.Present, Application.Features.Journal.Verification.ArchiveIdentityCodec.TryParse(bytes), bytes.ToArray())
                : new ArchiveIdentityRead(StoredObjectStatus.Absent, null, null));
        }
    }

    public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Application.Features.Journal.Verification.ArchiveIdentityCodec.Serialize(identity);
        lock (identityLock)
        {
            if (identityBytes is { } existing)
            {
                return Task.FromResult(new ArchiveIdentityCreateResult(ArchiveIdentityCreateStatus.AlreadyExists, Application.Features.Journal.Verification.ArchiveIdentityCodec.TryParse(existing), existing.ToArray()));
            }

            identityBytes = bytes;
            return Task.FromResult(new ArchiveIdentityCreateResult(ArchiveIdentityCreateStatus.Created, identity, bytes.ToArray()));
        }
    }

    public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        return Task.FromResult(entries.TryGetValue($"{organizationId:D}/{streamName}/{streamRevision}", out var stored) ? StoredObjectRead.Present(stored.ToArray()) : StoredObjectRead.Absent);
    }

    public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var organizationPrefix = $"{organizationId:D}/";
        var prefix = streamName is null ? organizationPrefix : organizationPrefix + streamName + "/";
        var names = entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var offset = continuationToken is null ? 0 : int.Parse(continuationToken, NumberStyles.None, CultureInfo.InvariantCulture);
        var size = Math.Clamp(pageSizeHint, 1, 5000);
        var page = names.Skip(offset).Take(size).Select(n => Classify(n, organizationPrefix, streamName)).ToList();
        var next = offset + size < names.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new ArchiveListingPage(page, next, false));
    }

    /// <summary>WP-4A bounded listing: canonical entries of the stream above <paramref name="afterRevision"/> (same paging as <see cref="ListEntriesAsync"/>).</summary>
    public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var organizationPrefix = $"{organizationId:D}/";
        var prefix = organizationPrefix + streamName + "/";
        var names = entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(n => Classify(n, organizationPrefix, streamName))
            .Where(o => o.IsCanonicalEntryKey && o.StreamRevision > afterRevision).OrderBy(o => o.StreamRevision).ToList();
        var offset = continuationToken is null ? 0 : int.Parse(continuationToken, NumberStyles.None, CultureInfo.InvariantCulture);
        var size = Math.Clamp(pageSizeHint, 1, 5000);
        var next = offset + size < names.Count ? (offset + size).ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new ArchiveListingPage([.. names.Skip(offset).Take(size)], next, false));
    }

    /// <summary>WP-5a.1: there is no container in memory, so it is reported absent until anything (sentinel or entry) was written.</summary>
    public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool hasIdentity;
        lock (identityLock)
        {
            hasIdentity = identityBytes is not null;
        }

        return Task.FromResult(new ArchiveContentSummary(!hasIdentity && entries.IsEmpty && meta.IsEmpty, entries.Count + meta.Count));
    }
    // ---- WP-5a.2 (A-3): stop markers, under the same reserved names as the Blob archive ----
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> meta = new(StringComparer.Ordinal);

    /// <summary>Test-only (G14-R2): when set, every stop-marker write (any generation) throws <see cref="ArchiveUnavailableException"/>.</summary>
    internal bool FailStopMarkerWritesForTesting { get; set; }

    public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailStopMarkerWritesForTesting)
        {
            throw new ArchiveUnavailableException("stop-marker write failed (test)");
        }

        var candidate = markerBytes.ToArray();
        var stored = meta.GetOrAdd(BlobSecurityEventArchive.StopMarkerBlobName(organizationId, streamName, failedRevision), candidate);
        return Task.FromResult(ReferenceEquals(stored, candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : stored.AsSpan().SequenceEqual(candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, stored.ToArray())
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, stored.ToArray()));
    }

    public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = $"{BlobSecurityEventArchive.StopMarkerPrefix}{organizationId:D}/{streamName}/";
        return Task.FromResult<IReadOnlyList<ArchiveMetaObject>>([.. meta.Where(m => m.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new ArchiveMetaObject(m.Key, m.Key[prefix.Length..].Contains('/', StringComparison.Ordinal) ? null : BlobInventorySupport.ParseCanonicalNumberFile(m.Key[prefix.Length..]), m.Value.ToArray()))]);
    }

    public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool hasIdentity;
        lock (identityLock)
        {
            hasIdentity = identityBytes is not null;
        }

        IReadOnlyList<string> names = [.. meta.Keys.Order(StringComparer.Ordinal), .. hasIdentity ? new[] { BlobSecurityEventArchive.IdentityBlobName } : []];
        return Task.FromResult(names);
    }

    // ---- G14-R2 (A-4 §24): generic create-only meta/ objects (same reserved names as the Blob archive) ----

    public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailStopMarkerWritesForTesting && metaObjectName?.StartsWith(BlobSecurityEventArchive.StopMarkerPrefix, StringComparison.Ordinal) == true)
        {
            throw new ArchiveUnavailableException("stop-marker write failed (test)");
        }

        if (string.IsNullOrEmpty(metaObjectName) || !metaObjectName.StartsWith("meta/", StringComparison.Ordinal) || string.Equals(metaObjectName, BlobSecurityEventArchive.IdentityBlobName, StringComparison.Ordinal))
        {
            throw new ArgumentException("not a writable meta/ object name", nameof(metaObjectName));
        }

        var candidate = bytes.ToArray();
        var stored = meta.GetOrAdd(metaObjectName, candidate);
        return Task.FromResult(ReferenceEquals(stored, candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.Created, null)
            : stored.AsSpan().SequenceEqual(candidate) ? new CheckpointCreateResult(CheckpointCreateStatus.IdenticalExists, stored.ToArray())
            : new CheckpointCreateResult(CheckpointCreateStatus.Conflict, stored.ToArray()));
    }

    public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(meta.TryGetValue(metaObjectName, out var stored) ? StoredObjectRead.Present(stored.ToArray()) : StoredObjectRead.Absent);
    }

    public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ArchiveMetaObject>>([.. meta.Where(m => m.Key.StartsWith(metaPrefix, StringComparison.Ordinal)).OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new ArchiveMetaObject(m.Key, null, m.Value.ToArray()))]);
    }

    /// <summary>Test-only: every meta/ object (names and bytes).</summary>
    internal IReadOnlyDictionary<string, byte[]> MetaForTesting => meta;

    /// <summary>Test-only: places or replaces an object under <c>meta/</c> (emulates an archive actor, J4/J5).</summary>
    internal void PutMetaForTesting(string name, byte[] bytes) => meta[name] = bytes.ToArray();

    /// <summary>Test-only: removes an object under <c>meta/</c>.</summary>
    internal bool RemoveMetaForTesting(string name) => meta.TryRemove(name, out _);

    /// <summary>Canonical in-memory entry shape: <c>{organization}/{stream}/{revision}</c>, revision a positive integer without leading zeros.</summary>
    internal static ArchiveListedObject Classify(string name, string organizationPrefix, string? requestedStream)
    {
        var parts = name[organizationPrefix.Length..].Split('/');
        if (parts.Length == 2 && parts[0].Length > 0 && (requestedStream is null || string.Equals(parts[0], requestedStream, StringComparison.Ordinal))
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision >= 1
            && string.Equals(revision.ToString(CultureInfo.InvariantCulture), parts[1], StringComparison.Ordinal))
        {
            return new ArchiveListedObject(name, true, parts[0], revision);
        }

        return new ArchiveListedObject(name, false, null, null);
    }

    /// <summary>Test-only: places arbitrary bytes at an arbitrary key (emulates an archive-credential holder, J4, or a non-canonical object).</summary>
    internal void PutObjectForTesting(string key, byte[] bytes) => entries[key] = bytes.ToArray();

    /// <summary>Test-only: removes an object (emulates archive deletion by J4/J5).</summary>
    internal bool RemoveObjectForTesting(string key) => entries.TryRemove(key, out _);

    /// <summary>Test-only: removes only the sentinel (emulates a partial archive loss, WP-5a.1).</summary>
    internal void RemoveIdentityForTesting()
    {
        lock (identityLock)
        {
            identityBytes = null;
        }
    }

    /// <summary>Test-only: wipes the archive including the sentinel (emulates a replaced or wiped container).</summary>
    internal void WipeForTesting()
    {
        entries.Clear();
        meta.Clear();
        lock (identityLock)
        {
            identityBytes = null;
        }
    }
}

/// <summary>Registered when no permitted archive is configured: every append fails closed and readiness reports the reason.</summary>
public sealed class UnavailableSecurityEventArchive(string reason) : ISecurityEventArchive, ISecurityEventArchiveInventory, IArchiveMetaRecords
{
    public Task<ArchiveIdentityRead> ReadIdentityAsync(CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<ArchiveIdentityCreateResult> CreateIdentityAsync(ArchiveIdentity identity, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<StoredObjectRead> ReadEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<ArchiveListingPage> ListEntriesAsync(Guid organizationId, string? streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
        throw new ArchiveUnavailableException(reason);

    public Task<ArchiveListingPage> ListEntriesAboveAsync(Guid organizationId, string streamName, long afterRevision, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
        throw new ArchiveUnavailableException(reason);

    public Task<ArchiveContentSummary> SummarizeAsync(CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<CheckpointCreateResult> CreateStopMarkerAsync(Guid organizationId, string streamName, long failedRevision, ReadOnlyMemory<byte> markerBytes, CancellationToken cancellationToken) =>
        throw new ArchiveUnavailableException(reason);

    public Task<IReadOnlyList<ArchiveMetaObject>> ReadStopMarkersAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<IReadOnlyList<string>> ListMetaObjectNamesAsync(CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<CheckpointCreateResult> CreateMetaAsync(string metaObjectName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<StoredObjectRead> ReadMetaAsync(string metaObjectName, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<IReadOnlyList<ArchiveMetaObject>> ListMetaAsync(string metaPrefix, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public string ArchiveName => "Unavailable";

    public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((false, reason));

    public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);
}
