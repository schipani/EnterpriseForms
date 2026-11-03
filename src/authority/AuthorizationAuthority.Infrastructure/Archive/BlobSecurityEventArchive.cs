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
public sealed class BlobSecurityEventArchive : ISecurityEventArchive
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
        }
        catch (RequestFailedException ex)
        {
            throw new ArchiveUnavailableException("archive write failed: " + ex.Status, ex);
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

    private static ArchiveReceipt Receipt(string name, string etag, byte[] digest)
    {
        var reference = name + "|" + etag;
        return new ArchiveReceipt(reference, SHA256.HashData([.. System.Text.Encoding.UTF8.GetBytes(reference), .. digest]));
    }
}

/// <summary>Unit-test and local Development archive. Not durable; never permitted in Testing-integration, Staging or Production.</summary>
public sealed class InMemorySecurityEventArchive : ISecurityEventArchive
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> entries = new(StringComparer.Ordinal);

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
}

/// <summary>Registered when no permitted archive is configured: every append fails closed and readiness reports the reason.</summary>
public sealed class UnavailableSecurityEventArchive(string reason) : ISecurityEventArchive
{
    public string ArchiveName => "Unavailable";

    public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((false, reason));

    public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) => throw new ArchiveUnavailableException(reason);
}
