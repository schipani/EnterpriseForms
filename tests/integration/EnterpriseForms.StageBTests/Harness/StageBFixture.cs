using System.Net.Http.Headers;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Harness;

/// <summary>
/// Verifies the shared disposable stack is running with the Stage B services (ClamAV, worker, Azurite) and exposes the
/// raw clients. Unlike the Stage A fixture it writes no gate manifest: Stage B evidence is the TRX output of this run.
/// </summary>
public sealed class StageBFixture : IAsyncLifetime
{
    public const string ApiBaseAddress = "http://127.0.0.1:5180";
    public const string AuthorityBaseAddress = "http://127.0.0.1:5190";

    private readonly HttpClient raw = new() { BaseAddress = new Uri(ApiBaseAddress), Timeout = TimeSpan.FromMinutes(5) };

    public ComposeStack Stack { get; } = new();

    public BusinessApiClient Api { get; } = new(ApiBaseAddress);

    public AuthorityClient Authority { get; } = new(AuthorityBaseAddress);

    public string CommitSha { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        CommitSha = ComposeStack.GitCommit();
        var status = Stack.Compose("ps --format json", 60, throwOnError: false);
        foreach (var service in new[] { "\"authority\"", "\"api\"", "\"worker\"", "\"clamav\"" })
        {
            if (!status.Contains(service, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The security-test stack is not running with {service}. Start it with: docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait (infrastructure/docker).");
            }
        }

        await Authority.WaitReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        var ready = await Api.GetAsync("/health/ready", "x").ConfigureAwait(false);
        if (!ready.IsSuccess)
        {
            throw new InvalidOperationException("business API is not ready: " + ready.Body);
        }
    }

    public ValueTask DisposeAsync()
    {
        raw.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>PUT raw bytes (upload chunk) with the user's bearer token.</summary>
    public Task<HttpResult> PutBytesAsync(string path, string token, byte[] bytes) => SendBytesAsync(HttpMethod.Put, path, token, bytes, "application/octet-stream");

    /// <summary>Raw body with an explicit media type (logo upload is image/png; the server refuses anything else with 415).</summary>
    public async Task<HttpResult> SendBytesAsync(HttpMethod method, string path, string token, byte[] bytes, string contentType)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await raw.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        System.Text.Json.JsonDocument? json = null;
        try
        {
            json = text.Length > 0 ? System.Text.Json.JsonDocument.Parse(text) : null;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new HttpResult(response.StatusCode, text, json);
    }

    /// <summary>GET raw bytes (document download) and the response headers that matter for the download contract.</summary>
    public async Task<(System.Net.HttpStatusCode Status, byte[] Body, string? ContentDisposition, string? CacheControl, string? ContentType, string? NoSniff)> DownloadAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await raw.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return (response.StatusCode, body, response.Content.Headers.ContentDisposition?.ToString(), response.Headers.CacheControl?.ToString(), response.Content.Headers.ContentType?.ToString(), response.Headers.TryGetValues("X-Content-Type-Options", out var values) ? string.Join(",", values) : null);
    }

    /// <summary>
    /// CONS-23 (C-201): opens a download and reads only its first <paramref name="initialBytes"/>, then stops reading, so the server's
    /// writes block once the transport buffers are full; <see cref="PausedDownload.ReadRestAsync"/> resumes it.
    /// </summary>
    public async Task<PausedDownload> StartPausedDownloadAsync(string path, string token, int initialBytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await raw.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var download = new PausedDownload(request, response, await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
        await download.ReadAsync(initialBytes, bytesPerSecond: null).ConfigureAwait(false);
        return download;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StageBStackCollectionDefinition : ICollectionFixture<StageBFixture>
{
    public const string Name = "stage-b-stack";
}

/// <summary>CONS-23 (C-201): a download read under the test's control; it records how many bytes arrived and how the body ended.</summary>
public sealed class PausedDownload(HttpRequestMessage request, HttpResponseMessage response, Stream body) : IDisposable
{
    private readonly System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

    public System.Net.HttpStatusCode Status => response.StatusCode;

    public long? DeclaredLength => response.Content.Headers.ContentLength;

    public long Received { get; private set; }

    /// <summary>True when the body ended with all declared bytes; false when the server stopped it early.</summary>
    public bool Completed { get; private set; }

    public string? Failure { get; private set; }

    public byte[] Sha256 => hash.GetCurrentHash();

    /// <summary>Reads up to <paramref name="limit"/> bytes (all when null), optionally throttled; returns false when the body ended.</summary>
    public async Task<bool> ReadAsync(long? limit, int? bytesPerSecond)
    {
        var buffer = new byte[16 * 1024];
        var target = limit is { } l ? Received + l : long.MaxValue;
        var started = DateTime.UtcNow;
        var startedAt = Received;
        try
        {
            while (Received < target)
            {
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, target - Received))).ConfigureAwait(false);
                if (read == 0)
                {
                    Completed = DeclaredLength is { } declared && Received == declared;
                    return false;
                }

                hash.AppendData(buffer, 0, read);
                Received += read;
                if (bytesPerSecond is { } rate)
                {
                    var due = TimeSpan.FromSeconds((double)(Received - startedAt) / rate) - (DateTime.UtcNow - started);
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due).ConfigureAwait(false);
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            Failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Resumes reading to the end of the body (throttled when <paramref name="bytesPerSecond"/> is set).</summary>
    public Task ReadRestAsync(int? bytesPerSecond = null) => ReadAsync(null, bytesPerSecond);

    public void Dispose()
    {
        body.Dispose();
        response.Dispose();
        request.Dispose();
        hash.Dispose();
    }
}
