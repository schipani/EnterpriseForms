using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseForms.SecurityTests.Harness;

/// <summary>Raw HTTP result: status, body text and parsed JSON (when any). Tests assert the external effect, never only a status code.</summary>
public sealed record HttpResult(HttpStatusCode Status, string Body, JsonDocument? Json)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public JsonElement Root => Json?.RootElement ?? throw new InvalidOperationException("no JSON body: " + Body);

    public string? ErrorCode => Json is not null && Json.RootElement.ValueKind == JsonValueKind.Object
        ? (Json.RootElement.TryGetProperty("title", out var title) ? title.GetString() : Json.RootElement.TryGetProperty("error_code", out var code) ? code.GetString() : null)
        : null;
}

/// <summary>Business API client (host loopback port of the security-test stack). Every mutation carries an Idempotency-Key.</summary>
public sealed class BusinessApiClient(string baseAddress)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient http = new() { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

    public Task<HttpResult> GetAsync(string path, string token) => SendAsync(HttpMethod.Get, path, token, null, null);

    public Task<HttpResult> PostAsync(string path, string token, object body, string? idempotencyKey = null) => SendAsync(HttpMethod.Post, path, token, body, idempotencyKey ?? Guid.NewGuid().ToString("N"));

    public Task<HttpResult> PatchAsync(string path, string token, object body, string? idempotencyKey = null) => SendAsync(HttpMethod.Patch, path, token, body, idempotencyKey ?? Guid.NewGuid().ToString("N"));

    public async Task<HttpResult> SendAsync(HttpMethod method, string path, string? token, object? body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonDocument? document = null;
        try
        {
            document = text.Length > 0 ? JsonDocument.Parse(text) : null;
        }
        catch (JsonException)
        {
        }

        return new HttpResult(response.StatusCode, text, document);
    }
}

/// <summary>Authority client used by the harness for the operator plane (bootstrap, gate, fault injection) and for direct SEC-P11 negative calls.</summary>
public sealed class AuthorityClient(string baseAddress)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient http = new() { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

    public async Task<HttpResult> SendAsync(HttpMethod method, string path, string? bearer, object? body, string? userAssertion = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (userAssertion is not null)
        {
            request.Headers.Add("X-Delegated-User-Assertion", userAssertion);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonDocument? document = null;
        try
        {
            document = text.Length > 0 ? JsonDocument.Parse(text) : null;
        }
        catch (JsonException)
        {
        }

        return new HttpResult(response.StatusCode, text, document);
    }

    public Task<HttpResult> OperatorAsync(HttpMethod method, string path, object? body) => SendAsync(method, path, TestIdentity.OperatorToken(), body);

    public async Task<JsonElement> GateAsync(Guid organizationId)
    {
        var result = await OperatorAsync(HttpMethod.Get, $"/internal/v1/operator/organizations/{organizationId:D}/gate", null).ConfigureAwait(false);
        return result.Root.Clone();
    }

    public async Task WaitReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await SendAsync(HttpMethod.Get, "/internal/v1/health/ready", null, null).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException("authority did not become ready within " + timeout);
    }

    public async Task<bool> IsReadyAsync()
    {
        try
        {
            return (await SendAsync(HttpMethod.Get, "/internal/v1/health/ready", null, null).ConfigureAwait(false)).IsSuccess;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
