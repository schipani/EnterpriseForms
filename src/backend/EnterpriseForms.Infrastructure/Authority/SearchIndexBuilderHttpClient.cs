using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Search.IndexBuilder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Infrastructure.Authority;

/// <summary>
/// IX-2 (IX-D6, OD-1 pattern): the only implementation of <see cref="ISearchIndexBuilderAuthority"/>. Mints short-lived builder
/// tokens (<c>workload_role = search-index-builder</c>) and calls the Authority's builder plane. No user assertion is ever sent.
/// Every non-2xx response maps to <see cref="AuthorityClientException"/>; transport failures fail closed.
/// </summary>
public sealed class SearchIndexBuilderHttpClient(IHttpClientFactory httpClientFactory, IOptions<SearchIndexBuilderOptions> options, IOptions<AuthorityClientOptions> authority, TimeProvider clock) : ISearchIndexBuilderAuthority
{
    public const string HttpClientName = "trust-authority-search-index-builder";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly JsonWebTokenHandler tokenHandler = new();

    public bool IsConfigured => options.Value.IsConfigured && !string.IsNullOrWhiteSpace(authority.Value.BaseAddress);

    public Task<SearchIndexBuilderWork> GetWorkAsync(CancellationToken cancellationToken) =>
        SendAsync<SearchIndexBuilderWork>("internal/v1/search-index-builder/work", new { }, cancellationToken);

    public Task<SearchIndexPopulationPage> ReadPopulationAsync(Guid organizationId, Guid formDefinitionId, Guid? afterFormRecordId, int limit, CancellationToken cancellationToken) =>
        SendAsync<SearchIndexPopulationPage>("internal/v1/search-index-builder/population", new { organization_id = organizationId, form_definition_id = formDefinitionId, after_form_record_id = afterFormRecordId, limit }, cancellationToken);

    public Task<SearchIndexBackfillResponse> BackfillAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyList<SearchIndexBackfillItem> records, CancellationToken cancellationToken) =>
        SendAsync<SearchIndexBackfillResponse>("internal/v1/search-index-builder/backfill", new { organization_id = organizationId, form_definition_id = formDefinitionId, records }, cancellationToken);

    public Task<SearchIndexAttestation> AttestAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        SendAsync<SearchIndexAttestation>("internal/v1/search-index-builder/attestation", new { organization_id = organizationId, form_definition_id = formDefinitionId }, cancellationToken);

    public Task<SearchIndexDeepSweepWork> GetDeepSweepWorkAsync(Guid organizationId, int oldestLimit, int sampleLimit, CancellationToken cancellationToken) =>
        SendAsync<SearchIndexDeepSweepWork>("internal/v1/search-index-builder/deep-sweep/work", new { organization_id = organizationId, oldest_limit = oldestLimit, sample_limit = sampleLimit }, cancellationToken);

    public Task<SearchIndexDeepVerifyResponse> DeepVerifyAsync(Guid organizationId, IReadOnlyList<SearchIndexBackfillItem> records, CancellationToken cancellationToken) =>
        SendAsync<SearchIndexDeepVerifyResponse>("internal/v1/search-index-builder/deep-sweep/verify", new { organization_id = organizationId, records }, cancellationToken);

    private async Task<T> SendAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            throw new AuthorityClientException("BUILDER_NOT_CONFIGURED", 503, false, "no search-index builder credential is configured for this host");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(settings));
        request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString("D"));
        EnvelopeCapability.Declare(request); // E3-M2f-1: every request of this build declares its fixed capability (CAP-S2)
        request.Content = JsonContent.Create(body, options: Json);

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

    private string MintToken(SearchIndexBuilderOptions settings)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(2),
            Claims = new Dictionary<string, object> { ["workload_role"] = "search-index-builder", ["workload_name"] = settings.WorkloadName },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(settings.SharedKeyBase64!)), SecurityAlgorithms.HmacSha256),
        });
    }

    private sealed record ErrorBody(string? ErrorCode, Guid? CorrelationId, bool? Retryable, string? Detail);
}

public static class SearchIndexBuilderRegistration
{
    /// <summary>
    /// Worker host only (IX-D6): registers the builder port and its workflow. Refuses a partial builder configuration and a builder
    /// key or issuer equal to the business workload, verifier or reconciler credential. Without configuration the port reports
    /// itself unconfigured and the builder stays idle; the business API host never calls this method.
    /// </summary>
    public static IServiceCollection AddSearchIndexBuilder(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = configuration.GetSection(SearchIndexBuilderOptions.SectionName).Get<SearchIndexBuilderOptions>() ?? new SearchIndexBuilderOptions();
        var workload = configuration.GetSection(AuthorityClientOptions.SectionName).Get<AuthorityClientOptions>() ?? new AuthorityClientOptions();
        var verifier = configuration.GetSection(ProtectionVerifierOptions.SectionName).Get<ProtectionVerifierOptions>() ?? new ProtectionVerifierOptions();
        if (builder.IsPartlyConfigured)
        {
            throw new InvalidOperationException("SearchIndexBuilder must be configured completely (issuer, audience, >=256-bit key) or not at all.");
        }

        if (builder.IsConfigured && (builder.SharedKeyBase64 == workload.WorkloadSharedKeyBase64 || builder.Issuer == workload.WorkloadIssuer
                                     || builder.SharedKeyBase64 == verifier.SharedKeyBase64 || builder.Issuer == verifier.Issuer
                                     || builder.SharedKeyBase64 == configuration["TeamMembershipReconciler:SharedKeyBase64"] || builder.Issuer == configuration["TeamMembershipReconciler:Issuer"]))
        {
            throw new InvalidOperationException("The search-index builder credential must have its own key and issuer, distinct from the business workload, verifier and reconciler credentials (IX-D6).");
        }

        builder.Validate();
        services.Configure<SearchIndexBuilderOptions>(configuration.GetSection(SearchIndexBuilderOptions.SectionName));
        services.AddSingleton(builder);
        services.AddHttpClient(SearchIndexBuilderHttpClient.HttpClientName);
        services.AddSingleton<ISearchIndexBuilderAuthority, SearchIndexBuilderHttpClient>();
        services.AddSingleton<SearchIndexBuilderWorkflow>();
        return services;
    }
}
