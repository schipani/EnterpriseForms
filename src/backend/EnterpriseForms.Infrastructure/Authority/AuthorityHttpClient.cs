using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseForms.Application.Common.Authority;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Infrastructure.Authority;

public sealed class AuthorityClientOptions
{
    public const string SectionName = "TrustAuthority";

    public string? BaseAddress { get; set; }

    /// <summary>Workload credential (issuer/audience/secret) used to mint short-lived HS256 bearer tokens. Secret from deployment configuration only.</summary>
    public string? WorkloadIssuer { get; set; }

    public string? WorkloadAudience { get; set; }

    public string? WorkloadSharedKeyBase64 { get; set; }

    public string WorkloadName { get; set; } = "enterpriseforms-api";

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// The only implementation of the authority port. Each call carries a freshly minted workload token (2-minute lifetime)
/// and forwards the end user's assertion unchanged in <c>X-Delegated-User-Assertion</c>; the authority validates both
/// independently. Every non-2xx response maps to <see cref="AuthorityClientException"/>; transport failures map to
/// AUTHORITY_UNAVAILABLE (fail closed). There is no method that can request an arbitrary signature.
/// </summary>
public sealed class AuthorityHttpClient(IHttpClientFactory httpClientFactory, IOptions<AuthorityClientOptions> options, TimeProvider clock) : IAuthorizationAuthorityClient, IBrowserSessionStateAuthority, IFieldSearchPolicyAuthority, ISecurityAdministratorAuthority
{
    public const string HttpClientName = "trust-authority";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly JsonWebTokenHandler tokenHandler = new();

    public Task<PermissionDecision> CheckPermissionAsync(DelegatedUserContext user, PermissionCheckRequest request, CancellationToken cancellationToken) =>
        SendAsync<PermissionDecision>(HttpMethod.Post, "internal/v1/authorization/check", user, request, null, cancellationToken);

    public Task<FormAccessState> GetFormAccessAsync(DelegatedUserContext user, Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        SendAsync<FormAccessState>(HttpMethod.Get, $"internal/v1/form-definitions/{organizationId:D}/{formDefinitionId:D}/access", user, null, null, cancellationToken);

    /// <summary>Any number of checks: split into authority calls of at most 250 and recombined only at one revision (C-27).</summary>
    public Task<IReadOnlyList<PermissionDecision>> CheckPermissionsBatchAsync(DelegatedUserContext user, IReadOnlyList<PermissionCheckRequest> requests, CancellationToken cancellationToken) =>
        PermissionCheckBatching.CheckAllAsync(requests, async (chunk, token) =>
        {
            var response = await SendAsync<BatchResponse>(HttpMethod.Post, "internal/v1/authorization/check-batch", user, new { checks = chunk }, null, token).ConfigureAwait(false);
            return response.Decisions;
        }, cancellationToken);

    public Task<RecordDiscoveryPage> DiscoverAuthorizedRecordsAsync(DelegatedUserContext user, Guid organizationId, int pageSize, string? continuationToken, CancellationToken cancellationToken) =>
        SendAsync<RecordDiscoveryPage>(HttpMethod.Post, "internal/v1/authorization/discover", user, new { organization_id = organizationId, page_size = pageSize, continuation_token = continuationToken }, null, cancellationToken);

    public Task<SecurityCommandResult> SubmitSecurityCommandAsync(DelegatedUserContext user, SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync<SecurityCommandResult>(HttpMethod.Post, "internal/v1/security-commands", user, request, idempotencyKey, cancellationToken);

    public Task<SecurityCommandResult> GetSecurityCommandStatusAsync(DelegatedUserContext user, Guid commandId, CancellationToken cancellationToken) =>
        SendAsync<SecurityCommandResult>(HttpMethod.Get, $"internal/v1/security-commands/{commandId:D}", user, null, null, cancellationToken);

    public Task<PreparedContentOperation> PrepareContentChangeAsync(DelegatedUserContext user, PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync<PreparedContentOperation>(HttpMethod.Post, "internal/v1/content-changes/prepare", user, request, idempotencyKey, cancellationToken);

    public Task<AcceptedContentRevision> CommitContentChangeAsync(DelegatedUserContext user, Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedContentRevision>(HttpMethod.Post, $"internal/v1/content-changes/{operationId:D}/commit", user, request, null, cancellationToken);

    public Task<AcceptedContentRevision> GetCommittedContentRevisionAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedContentRevision>(HttpMethod.Get, $"internal/v1/content-revisions/{organizationId:D}/{formRecordId:D}", user, null, null, cancellationToken);

    public Task<VerifyRecordProjectionResult> VerifyRecordProjectionAsync(DelegatedUserContext user, VerifyRecordProjectionRequest request, CancellationToken cancellationToken) =>
        SendAsync<VerifyRecordProjectionResult>(HttpMethod.Post, "internal/v1/integrity/verify-record-projection", user, request, null, cancellationToken);

    public Task<SearchCatalogue> IssueSearchCatalogueAsync(DelegatedUserContext user, SearchCatalogueRequest request, CancellationToken cancellationToken) =>
        SendAsync<SearchCatalogue>(HttpMethod.Post, "internal/v1/search-catalogue", user, request, null, cancellationToken);

    public Task<CatalogueSnapshotValidation> ValidateSearchCatalogueAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) =>
        SendAsync<CatalogueSnapshotValidation>(HttpMethod.Post, $"internal/v1/search-catalogue/{searchCatalogueSnapshotId:D}/validate", user, null, null, cancellationToken);

    public Task<VerifyRecordProjectionsResponse> VerifyRecordProjectionsAsync(DelegatedUserContext user, VerifyRecordProjectionsRequest request, CancellationToken cancellationToken) =>
        SendAsync<VerifyRecordProjectionsResponse>(HttpMethod.Post, "internal/v1/integrity/verify-record-projections", user, request, null, cancellationToken);

    public Task<ContentHistoryPage> GetContentHistoryAsync(DelegatedUserContext user, Guid organizationId, Guid formRecordId, long? afterRevision, int limit, CancellationToken cancellationToken) =>
        SendAsync<ContentHistoryPage>(HttpMethod.Get, $"internal/v1/content-revisions/{organizationId:D}/{formRecordId:D}/history?limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}" + (afterRevision is { } after ? "&after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty), user, null, null, cancellationToken);

    public Task<AcceptedContentRevision> GetCommittedContentRevisionForReprotectionAsync(Guid organizationId, Guid formRecordId, Guid reprotectionCommandId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedContentRevision>(HttpMethod.Get, $"internal/v1/content-revisions/{organizationId:D}/{formRecordId:D}?reprotectionCommandId={reprotectionCommandId:D}", null, null, null, cancellationToken);

    public Task<PreparedContentOperation> PrepareReprotectionAsync(PrepareContentChangeRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReprotectionCommandId is null)
        {
            throw new ArgumentException("a reprotection prepare needs the authorizing command", nameof(request));
        }

        return SendAsync<PreparedContentOperation>(HttpMethod.Post, "internal/v1/content-changes/prepare", null, request, idempotencyKey, cancellationToken);
    }

    public Task<AcceptedContentRevision> CommitReprotectionAsync(Guid operationId, CommitContentChangeRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedContentRevision>(HttpMethod.Post, $"internal/v1/content-changes/{operationId:D}/commit", null, request, null, cancellationToken);

    public Task<AuthorityFieldSearchCapabilities> GetFieldSearchCapabilitiesAsync(DelegatedUserContext user, FieldSearchCapabilitiesQuery query, CancellationToken cancellationToken) =>
        SendAsync<AuthorityFieldSearchCapabilities>(HttpMethod.Post, "internal/v1/field-search/capabilities", user, query, null, cancellationToken);

    public Task<AuthoritySecurityAdministratorRoster> GetSecurityAdministratorsAsync(DelegatedUserContext user, SecurityAdministratorRosterQuery query, CancellationToken cancellationToken) =>
        SendAsync<AuthoritySecurityAdministratorRoster>(HttpMethod.Post, "internal/v1/security-administrators", user, query, null, cancellationToken);

    public Task<AuthorityFieldSearchAdministration> GetFieldSearchAdministrationAsync(DelegatedUserContext user, FieldSearchAdministrationQuery query, CancellationToken cancellationToken) =>
        SendAsync<AuthorityFieldSearchAdministration>(HttpMethod.Post, "internal/v1/field-search/administration", user, query, null, cancellationToken);

    public Task RecordSearchExecutionAsync(DelegatedUserContext user, Guid searchCatalogueSnapshotId, SearchExecutionEvidence evidence, CancellationToken cancellationToken) =>
        SendAsync<System.Text.Json.JsonElement>(HttpMethod.Post, $"internal/v1/search-catalogue/{searchCatalogueSnapshotId:D}/executions", user, evidence, null, cancellationToken);

    public Task<BrowserSessionState> CreateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, DateTimeOffset absoluteExpiresOn, string? replacedSessionDigestBase64, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return SendAsync<BrowserSessionState>(HttpMethod.Post, "internal/v1/sessions", user, new SessionCreateBody(user.OrganizationId, sessionDigestBase64, absoluteExpiresOn, replacedSessionDigestBase64), null, cancellationToken);
    }

    public Task<BrowserSessionState> ValidateSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return SendAsync<BrowserSessionState>(HttpMethod.Post, "internal/v1/sessions/validate", user, new SessionDigestBody(user.OrganizationId, sessionDigestBase64), null, cancellationToken);
    }

    public Task<BrowserSessionState> RevokeSessionAsync(DelegatedUserContext user, string sessionDigestBase64, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return SendAsync<BrowserSessionState>(HttpMethod.Post, "internal/v1/sessions/revoke", user, new SessionDigestBody(user.OrganizationId, sessionDigestBase64), null, cancellationToken);
    }

    /// <summary>user == null: workload-only call (reprotection); the authority then requires the operation's own authorization reference.</summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, DelegatedUserContext? user, object? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BaseAddress) || string.IsNullOrWhiteSpace(settings.WorkloadSharedKeyBase64))
        {
            throw new AuthorityClientException("AUTHORITY_UNAVAILABLE", 503, true, "authority not configured");
        }

        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintWorkloadToken(settings));
        if (user is not null)
        {
            request.Headers.Add("X-Delegated-User-Assertion", user.RawAssertion);
        }

        request.Headers.Add("X-Correlation-Id", Guid.CreateVersion7().ToString("D"));
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(settings.BaseAddress, UriKind.Absolute);
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

    private string MintWorkloadToken(AuthorityClientOptions settings)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var key = new SymmetricSecurityKey(Convert.FromBase64String(settings.WorkloadSharedKeyBase64!));
        return tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.WorkloadIssuer,
            Audience = settings.WorkloadAudience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(2),
            Claims = new Dictionary<string, object> { ["workload_role"] = "business-api", ["workload_name"] = settings.WorkloadName },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    private sealed record BatchResponse(IReadOnlyList<PermissionDecision> Decisions);

    private sealed record SessionCreateBody(Guid OrganizationId, string SessionDigestBase64, DateTimeOffset AbsoluteExpiresOn, string? ReplacedSessionDigestBase64);

    private sealed record SessionDigestBody(Guid OrganizationId, string SessionDigestBase64);

    private sealed record ErrorBody(string? ErrorCode, Guid? CorrelationId, bool? Retryable, string? Detail);
}
