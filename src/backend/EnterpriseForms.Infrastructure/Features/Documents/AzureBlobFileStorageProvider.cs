using System.Globalization;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using EnterpriseForms.Application.Features.Documents.Ports;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Infrastructure.Features.Documents;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"AzureBlob" (Azurite locally, Azure Blob Storage in Canadian regions in Azure) or "Unconfigured" (fail closed).</summary>
    public string Provider { get; set; } = "Unconfigured";

    public string? BlobServiceUri { get; set; }

    public string? AccountName { get; set; }

    public string? AccountKey { get; set; }

    public string StagingContainerName { get; set; } = "upload-staging";

    public string DocumentsContainerName { get; set; } = "documents";

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Azure Block Blob adapter (CLAUDE.md §4A): chunks are staged as blocks of one block blob in a private quarantine
/// container and committed as an ordered block list; ciphertext is uploaded to a separate private container. No SAS or
/// public URL is ever produced; clients never see object keys.
/// </summary>
public sealed class AzureBlobFileStorageProvider : IFileStorageProvider
{
    private readonly BlobContainerClient staging;
    private readonly BlobContainerClient documents;
    private readonly TimeSpan timeout;
    private int containersEnsured;

    public AzureBlobFileStorageProvider(IOptions<DocumentStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.BlobServiceUri) || string.IsNullOrWhiteSpace(value.AccountName) || string.IsNullOrWhiteSpace(value.AccountKey))
        {
            throw new InvalidOperationException("Storage blob settings are required for the AzureBlob provider.");
        }

        var service = new BlobServiceClient(new Uri(value.BlobServiceUri), new StorageSharedKeyCredential(value.AccountName, value.AccountKey));
        staging = service.GetBlobContainerClient(value.StagingContainerName);
        documents = service.GetBlobContainerClient(value.DocumentsContainerName);
        timeout = TimeSpan.FromSeconds(Math.Max(5, value.TimeoutSeconds));
    }

    public string ProviderName => "AzureBlob";

    public async Task StageChunkAsync(string objectKey, int chunkIndex, Stream content, long length, CancellationToken cancellationToken)
    {
        await EnsureContainersAsync(cancellationToken).ConfigureAwait(false);
        using var cts = Linked(cancellationToken);
        try
        {
            await staging.GetBlockBlobClient(objectKey).StageBlockAsync(BlockId(chunkIndex), content, cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new IOException("staging block failed: " + ex.Status, ex);
        }
    }

    public async Task CommitStagedChunksAsync(string objectKey, int chunkCount, CancellationToken cancellationToken)
    {
        using var cts = Linked(cancellationToken);
        try
        {
            var ids = Enumerable.Range(0, chunkCount).Select(BlockId).ToList();
            await staging.GetBlockBlobClient(objectKey).CommitBlockListAsync(ids, cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new IOException("commit block list failed: " + ex.Status, ex);
        }
    }

    public async Task<Stream> OpenReadAsync(StorageArea area, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            return await Container(area).GetBlobClient(objectKey).OpenReadAsync(new BlobOpenReadOptions(false) { BufferSize = 4 * 1024 * 1024 }, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new IOException("blob read failed: " + ex.Status, ex);
        }
    }

    public async Task UploadAsync(StorageArea area, string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        await EnsureContainersAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Container(area).GetBlobClient(objectKey).UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, // never overwrite an immutable version object
                TransferOptions = new StorageTransferOptions { MaximumConcurrency = 2, InitialTransferSize = 4 * 1024 * 1024, MaximumTransferSize = 4 * 1024 * 1024 },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new IOException("blob upload failed: " + ex.Status, ex);
        }
    }

    public async Task DeleteAsync(StorageArea area, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            if (area == StorageArea.Staging)
            {
                // Uncommitted plaintext blocks of an open/cancelled/expired session are not removed by Delete Blob (the blob
                // does not exist yet). Committing an empty block list discards every uncommitted block; the empty blob is then
                // deleted. For an already committed staging blob this simply truncates it before deletion.
                await EnsureContainersAsync(cancellationToken).ConfigureAwait(false);
                using var cts = Linked(cancellationToken);
                await staging.GetBlockBlobClient(objectKey).CommitBlockListAsync([], cancellationToken: cts.Token).ConfigureAwait(false);
            }

            await Container(area).GetBlobClient(objectKey).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new IOException("blob delete failed: " + ex.Status, ex);
        }
    }

    public async Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureContainersAsync(cancellationToken).ConfigureAwait(false);
            return (true, "blob storage reachable");
        }
        catch (RequestFailedException ex)
        {
            return (false, "StorageUnavailable:" + ex.Status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "StorageTimeout");
        }
    }

    private BlobContainerClient Container(StorageArea area) => area == StorageArea.Staging ? staging : documents;

    private async Task EnsureContainersAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref containersEnsured) == 1)
        {
            return;
        }

        using var cts = Linked(cancellationToken);
        await staging.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cts.Token).ConfigureAwait(false);
        await documents.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cts.Token).ConfigureAwait(false);
        Volatile.Write(ref containersEnsured, 1);
    }

    private CancellationTokenSource Linked(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static string BlockId(int index) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(index.ToString("D8", CultureInfo.InvariantCulture)));
}

/// <summary>Registered when storage is not configured: every operation fails closed and readiness reports it.</summary>
public sealed class UnavailableFileStorageProvider(string reason) : IFileStorageProvider
{
    public string ProviderName => "Unavailable";

    public Task StageChunkAsync(string objectKey, int chunkIndex, Stream content, long length, CancellationToken cancellationToken) => throw new IOException(reason);

    public Task CommitStagedChunksAsync(string objectKey, int chunkCount, CancellationToken cancellationToken) => throw new IOException(reason);

    public Task<Stream> OpenReadAsync(StorageArea area, string objectKey, CancellationToken cancellationToken) => throw new IOException(reason);

    public Task UploadAsync(StorageArea area, string objectKey, Stream content, string contentType, CancellationToken cancellationToken) => throw new IOException(reason);

    public Task DeleteAsync(StorageArea area, string objectKey, CancellationToken cancellationToken) => throw new IOException(reason);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((false, reason));
}
