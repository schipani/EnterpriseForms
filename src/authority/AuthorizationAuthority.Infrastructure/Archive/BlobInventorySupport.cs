using System.Globalization;
using AuthorizationAuthority.Application.Common.Ports;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AuthorizationAuthority.Infrastructure.Archive;

/// <summary>
/// SEC-BL-01 WP-2: shared Blob read and listing mechanics for the archive inventory and the checkpoint store, so both map outcomes the
/// same way: blob not found ⇒ Absent; container not found ⇒ ContainerAbsent (never "absent object"); any other failure or an internal
/// timeout ⇒ the caller's unavailability exception; cancellation by the caller propagates unchanged.
/// </summary>
internal static class BlobInventorySupport
{
    internal const string ContainerNotFound = "ContainerNotFound";

    internal static bool IsContainerNotFound(RequestFailedException ex) => ex.Status == 404 && string.Equals(ex.ErrorCode, ContainerNotFound, StringComparison.Ordinal);

    internal static async Task<StoredObjectRead> ReadAsync(BlobContainerClient container, string name, TimeSpan timeout, Func<string, Exception?, Exception> unavailable, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var content = await container.GetBlobClient(name).DownloadContentAsync(cts.Token).ConfigureAwait(false);
            return StoredObjectRead.Present(content.Value.Content.ToArray());
        }
        catch (RequestFailedException ex) when (IsContainerNotFound(ex))
        {
            return StoredObjectRead.ContainerAbsent;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return StoredObjectRead.Absent;
        }
        catch (RequestFailedException ex)
        {
            throw unavailable("read failed: " + ex.Status.ToString(CultureInfo.InvariantCulture), ex);
        }
        catch (AggregateException ex)
        {
            // The SDK reports exhausted retries with mixed failures (for example connection refused, then a timeout) as an
            // AggregateException rather than a RequestFailedException: still unavailability, unless the caller cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            throw unavailable("request failed after retries", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw unavailable("timeout", ex);
        }
    }

    /// <summary>Create-only upload. Returns null when created, otherwise the bytes already stored at <paramref name="name"/> (never overwritten).</summary>
    internal static async Task<byte[]?> CreateOnlyAsync(BlobContainerClient container, string name, ReadOnlyMemory<byte> bytes, TimeSpan timeout, Func<string, Exception?, Exception> unavailable, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var blob = container.GetBlobClient(name);
        try
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cts.Token).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            }, cts.Token).ConfigureAwait(false);
            return null;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            try
            {
                var existing = await blob.DownloadContentAsync(cts.Token).ConfigureAwait(false);
                return existing.Value.Content.ToArray();
            }
            catch (RequestFailedException inner)
            {
                throw unavailable("read-back failed: " + inner.Status.ToString(CultureInfo.InvariantCulture), inner);
            }
            catch (AggregateException inner)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw unavailable("read-back failed after retries", inner);
            }
            catch (OperationCanceledException inner) when (!cancellationToken.IsCancellationRequested)
            {
                throw unavailable("read-back timeout", inner);
            }
        }
        catch (RequestFailedException ex)
        {
            throw unavailable("write failed: " + ex.Status.ToString(CultureInfo.InvariantCulture), ex);
        }
        catch (AggregateException ex)
        {
            // The SDK reports exhausted retries with mixed failures (for example connection refused, then a timeout) as an
            // AggregateException rather than a RequestFailedException: still unavailability, unless the caller cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            throw unavailable("request failed after retries", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw unavailable("timeout", ex);
        }
    }

    /// <summary>One page of the names under <paramref name="prefix"/> (every object, canonical or not).</summary>
    internal static async Task<(IReadOnlyList<string> Names, string? Continuation, bool ContainerAbsent)> ListPageAsync(BlobContainerClient container, string prefix, string? continuationToken,
        int pageSizeHint, TimeSpan timeout, Func<string, Exception?, Exception> unavailable, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var pages = container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cts.Token).AsPages(continuationToken, Math.Clamp(pageSizeHint, 1, 5000));
            await foreach (var page in pages.ConfigureAwait(false))
            {
                return (page.Values.Select(b => b.Name).ToList(), string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken, false);
            }

            return ([], null, false);
        }
        catch (RequestFailedException ex) when (IsContainerNotFound(ex))
        {
            return ([], null, true);
        }
        catch (RequestFailedException ex)
        {
            throw unavailable("listing failed: " + ex.Status.ToString(CultureInfo.InvariantCulture), ex);
        }
        catch (AggregateException ex)
        {
            // The SDK reports exhausted retries with mixed failures (for example connection refused, then a timeout) as an
            // AggregateException rather than a RequestFailedException: still unavailability, unless the caller cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            throw unavailable("request failed after retries", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw unavailable("timeout", ex);
        }
    }

    /// <summary><c>D12</c> revision or sequence followed by <c>.json</c>, value at least 1; otherwise null.</summary>
    internal static long? ParseCanonicalNumberFile(string file)
    {
        if (file.Length != 17 || !file.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var digits = file.AsSpan(0, 12);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        var value = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return value >= 1 ? value : null;
    }
}
