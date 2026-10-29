using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseForms.Application.Common.Authority;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Infrastructure.Authority;

/// <summary>
/// Phase C7 M4 (OD-1): the field-protection verifier workload credential. Configured for the Worker only (section
/// <c>ProtectionVerifier</c>); all three values or none. Never shared with the business workload credential.
/// </summary>
public sealed class ProtectionVerifierOptions
{
    public const string SectionName = "ProtectionVerifier";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? SharedKeyBase64 { get; set; }

    public string WorkloadName { get; set; } = "field-protection-verifier";

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Audience) && SharedKeyLength >= 32;

    public bool IsPartlyConfigured => !IsConfigured && (!string.IsNullOrWhiteSpace(Issuer) || !string.IsNullOrWhiteSpace(Audience) || !string.IsNullOrWhiteSpace(SharedKeyBase64));

    private int SharedKeyLength
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(SharedKeyBase64) ? 0 : Convert.FromBase64String(SharedKeyBase64).Length;
            }
            catch (FormatException)
            {
                return 0;
            }
        }
    }
}

/// <summary>
/// The only implementation of <see cref="IProtectionVerifierAuthority"/>: mints short-lived verifier tokens
/// (<c>workload_role = field-protection-verifier</c>) and calls the Authority's verifier routes. No user assertion is ever
/// sent. Every non-2xx response maps to <see cref="AuthorityClientException"/>; transport failures fail closed.
/// </summary>
public sealed class ProtectionVerifierHttpClient(IHttpClientFactory httpClientFactory, IOptions<ProtectionVerifierOptions> options, IOptions<AuthorityClientOptions> authority, TimeProvider clock) : IProtectionVerifierAuthority
{
    public const string HttpClientName = "trust-authority-verifier";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly JsonWebTokenHandler tokenHandler = new();

    public bool IsConfigured => options.Value.IsConfigured && !string.IsNullOrWhiteSpace(authority.Value.BaseAddress);

    public Task<MigrationCataloguePage> IssueMigrationCatalogueAsync(Guid organizationId, Guid formDefinitionId, Guid securityCommandId, CancellationToken cancellationToken) =>
        SendAsync<MigrationCataloguePage>(HttpMethod.Post, "internal/v1/verifier/migration-catalogue", new { organization_id = organizationId, form_definition_id = formDefinitionId, security_command_id = securityCommandId }, cancellationToken);

    public Task<MigrationCataloguePage> ReadMigrationCatalogueAsync(Guid searchCatalogueSnapshotId, int offset, CancellationToken cancellationToken) =>
        SendAsync<MigrationCataloguePage>(HttpMethod.Get, $"internal/v1/verifier/migration-catalogue/{searchCatalogueSnapshotId:D}/entries?offset={offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, cancellationToken);

    public Task<ProtectionCensusResponse> TakeCensusAsync(Guid searchCatalogueSnapshotId, IReadOnlyList<ProtectionCensusItem> records, Guid? reuseSinceSnapshotId, CancellationToken cancellationToken) =>
        SendAsync<ProtectionCensusResponse>(HttpMethod.Post, $"internal/v1/verifier/migration-catalogue/{searchCatalogueSnapshotId:D}/census", new { records, reuseSinceSnapshotId }, cancellationToken);

    public Task<ProtectionAttestation> AttestAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) =>
        SendAsync<ProtectionAttestation>(HttpMethod.Post, $"internal/v1/verifier/migration-catalogue/{searchCatalogueSnapshotId:D}/attestation", null, cancellationToken);

    public Task<ProtectionCoverage> GetCoverageAsync(Guid organizationId, CancellationToken cancellationToken) =>
        SendAsync<ProtectionCoverage>(HttpMethod.Post, "internal/v1/verifier/protection-coverage", new { organization_id = organizationId }, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            throw new AuthorityClientException("VERIFIER_NOT_CONFIGURED", 503, false, "no field-protection verifier credential is configured for this host");
        }

        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(settings));
        request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString("D"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(authority.Value.BaseAddress!, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds));
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "authority unreachable: " + ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "authority timeout: " + ex.Message);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
                return payload ?? throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "empty authority response");
            }

            ErrorBody? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
            }

            var status = (int)response.StatusCode;
            var code = error?.ErrorCode ?? (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "UNAUTHORIZED",
                HttpStatusCode.Forbidden => "FORBIDDEN",
                HttpStatusCode.NotFound => "NOT_FOUND",
                _ => "AUTHORITY_ERROR",
            });
            throw new AuthorityClientException(code, status is 401 or 403 or 404 or 409 or 422 or 423 or 429 ? status : 503, error?.Retryable ?? status >= 500, error?.Detail);
        }
    }

    private string MintToken(ProtectionVerifierOptions settings)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(2),
            Claims = new Dictionary<string, object> { ["workload_role"] = "field-protection-verifier", ["workload_name"] = settings.WorkloadName },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(settings.SharedKeyBase64!)), SecurityAlgorithms.HmacSha256),
        });
    }

    private sealed record ErrorBody(string? ErrorCode, Guid? CorrelationId, bool? Retryable, string? Detail);
}

public static class ProtectionVerifierRegistration
{
    /// <summary>
    /// Worker host only (OD-1): registers the verifier port. Refuses a partial verifier configuration and a verifier key or
    /// issuer equal to the business workload credential. Without configuration the port reports itself unconfigured and every
    /// call fails closed; the business API host never calls this method.
    /// </summary>
    public static IServiceCollection AddProtectionVerifier(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var verifier = configuration.GetSection(ProtectionVerifierOptions.SectionName).Get<ProtectionVerifierOptions>() ?? new ProtectionVerifierOptions();
        var workload = configuration.GetSection(AuthorityClientOptions.SectionName).Get<AuthorityClientOptions>() ?? new AuthorityClientOptions();
        if (verifier.IsPartlyConfigured)
        {
            throw new InvalidOperationException("ProtectionVerifier must be configured completely (issuer, audience, ≥256-bit key) or not at all.");
        }

        if (verifier.IsConfigured && (verifier.SharedKeyBase64 == workload.WorkloadSharedKeyBase64 || verifier.Issuer == workload.WorkloadIssuer))
        {
            throw new InvalidOperationException("The field-protection verifier credential must have its own key and issuer, distinct from the business workload credential (OD-1).");
        }

        services.Configure<ProtectionVerifierOptions>(configuration.GetSection(ProtectionVerifierOptions.SectionName));
        services.AddHttpClient(ProtectionVerifierHttpClient.HttpClientName);
        services.AddSingleton<IProtectionVerifierAuthority, ProtectionVerifierHttpClient>();
        return services;
    }
}
