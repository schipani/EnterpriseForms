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
    public async Task<HttpResult> PutBytesAsync(string path, string token, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
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
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StageBStackCollectionDefinition : ICollectionFixture<StageBFixture>
{
    public const string Name = "stage-b-stack";
}
