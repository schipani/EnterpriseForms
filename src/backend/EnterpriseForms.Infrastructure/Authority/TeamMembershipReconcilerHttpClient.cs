using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
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
/// Phase C8 AC-9 (D8-12): the team-membership reconciler credential. Configured for the Worker only (section
/// <c>TeamMembershipReconciler</c>); complete or absent; its key and issuer are distinct from the business workload and the
/// field-protection verifier credentials (C-2). Outside Development/Testing it additionally requires an https Authority
/// address and a client certificate (C-1).
/// </summary>
public sealed class TeamMembershipReconcilerOptions
{
    public const string SectionName = "TeamMembershipReconciler";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? SharedKeyBase64 { get; set; }

    public string WorkloadName { get; set; } = "business-projection-reconciler";

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>PKCS#12 client certificate for mTLS (required outside Development/Testing, C-1).</summary>
    public string? ClientCertificatePath { get; set; }

    public string? ClientCertificatePassword { get; set; }

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
/// The only implementation of <see cref="ITeamMembershipOutcomeAuthority"/>: mints short-lived reconciler tokens
/// (<c>workload_role = business-projection-reconciler</c>) and calls the AC-9 lookup. No user assertion, idempotency header or
/// submit route exists here. Every non-success maps to a typed <see cref="ReconcilerLookupException"/>.
/// </summary>
public sealed class TeamMembershipReconcilerHttpClient(IHttpClientFactory httpClientFactory, IOptions<TeamMembershipReconcilerOptions> options, IOptions<AuthorityClientOptions> authority, TimeProvider clock) : ITeamMembershipOutcomeAuthority
{
    public const string HttpClientName = "trust-authority-reconciler";
    public const string Route = "internal/v1/security-command-outcomes/lookup";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly JsonWebTokenHandler tokenHandler = new();

    public bool IsConfigured => options.Value.IsConfigured && !string.IsNullOrWhiteSpace(authority.Value.BaseAddress);

    public async Task<SecurityCommandOutcomeAnswer> LookupAsync(Guid organizationId, string idempotencyKey, string expectedCommandKind, Guid correlationId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            throw new ReconcilerLookupException(ReconcilerLookupFailure.NotConfigured, "RECONCILER_NOT_CONFIGURED");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Route, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(settings));
        request.Headers.Add("X-Correlation-Id", correlationId.ToString("D"));
        request.Content = JsonContent.Create(new { organization_id = organizationId, idempotency_key = idempotencyKey, expected_command_kind = expectedCommandKind }, options: Json);

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(authority.Value.BaseAddress!, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds));
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new ReconcilerLookupException(ReconcilerLookupFailure.Unavailable, "AUTHORITY_UNAVAILABLE");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReconcilerLookupException(ReconcilerLookupFailure.Unavailable, "AUTHORITY_TIMEOUT");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    try
                    {
                        return await response.Content.ReadFromJsonAsync<SecurityCommandOutcomeAnswer>(Json, cancellationToken).ConfigureAwait(false)
                               ?? throw new ReconcilerLookupException(ReconcilerLookupFailure.Refused, "EMPTY_AUTHORITY_RESPONSE");
                    }
                    catch (JsonException)
                    {
                        throw new ReconcilerLookupException(ReconcilerLookupFailure.Refused, "UNREADABLE_AUTHORITY_RESPONSE");
                    }

                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    throw new ReconcilerLookupException(ReconcilerLookupFailure.CredentialRefused, response.StatusCode == HttpStatusCode.Unauthorized ? "UNAUTHORIZED" : "FORBIDDEN");
                case HttpStatusCode.TooManyRequests:
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : null);
                    throw new ReconcilerLookupException(ReconcilerLookupFailure.RateLimited, "RATE_LIMITED", retryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1));
                case HttpStatusCode.BadRequest:
                case HttpStatusCode.UnprocessableEntity:
                    throw new ReconcilerLookupException(ReconcilerLookupFailure.Refused, (int)response.StatusCode == 400 ? "LOOKUP_REFUSED_400" : "LOOKUP_REFUSED_422");
                default:
                    throw new ReconcilerLookupException(ReconcilerLookupFailure.Unavailable, "AUTHORITY_STATUS_" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private string MintToken(TeamMembershipReconcilerOptions settings)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(2),
            Claims = new Dictionary<string, object> { ["workload_role"] = "business-projection-reconciler", ["workload_name"] = settings.WorkloadName },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(settings.SharedKeyBase64!)), SecurityAlgorithms.HmacSha256),
        });
    }
}

public static class TeamMembershipReconcilerRegistration
{
    /// <summary>
    /// Worker host only (D8-12): registers the AC-9 lookup port. Refuses a partial credential, one that reuses the business
    /// workload or verifier key or issuer (C-2), and — outside Development/Testing — a plain-http Authority address or a
    /// missing client certificate (C-1). Without configuration the port reports itself unconfigured and every lookup fails
    /// closed; the business API host never calls this method.
    /// </summary>
    public static IServiceCollection AddTeamMembershipReconciler(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var reconciler = configuration.GetSection(TeamMembershipReconcilerOptions.SectionName).Get<TeamMembershipReconcilerOptions>() ?? new TeamMembershipReconcilerOptions();
        var workload = configuration.GetSection(AuthorityClientOptions.SectionName).Get<AuthorityClientOptions>() ?? new AuthorityClientOptions();
        var verifier = configuration.GetSection(ProtectionVerifierOptions.SectionName).Get<ProtectionVerifierOptions>() ?? new ProtectionVerifierOptions();
        if (reconciler.IsPartlyConfigured)
        {
            throw new InvalidOperationException("TeamMembershipReconciler must be configured completely (issuer, audience, ≥256-bit key) or not at all (C-2).");
        }

        if (reconciler.IsConfigured)
        {
            var keys = new[] { workload.WorkloadSharedKeyBase64, verifier.SharedKeyBase64 };
            var issuers = new[] { workload.WorkloadIssuer, verifier.Issuer };
            if (keys.Any(k => !string.IsNullOrWhiteSpace(k) && k == reconciler.SharedKeyBase64) || issuers.Any(i => !string.IsNullOrWhiteSpace(i) && i == reconciler.Issuer))
            {
                throw new InvalidOperationException("The team-membership reconciler credential must have its own key and issuer, distinct from the business workload and verifier credentials (C-2).");
            }

            var synthetic = string.Equals(environmentName, "Development", StringComparison.Ordinal) || string.Equals(environmentName, "Testing", StringComparison.Ordinal);
            var https = Uri.TryCreate(workload.BaseAddress, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps;
            if (!synthetic && (!https || string.IsNullOrWhiteSpace(reconciler.ClientCertificatePath)))
            {
                throw new InvalidOperationException("Outside Development/Testing the team-membership reconciler requires an https Authority address and a client certificate (C-1).");
            }
        }

        services.Configure<TeamMembershipReconcilerOptions>(configuration.GetSection(TeamMembershipReconcilerOptions.SectionName));
        services.AddHttpClient(TeamMembershipReconcilerHttpClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            if (reconciler.IsConfigured && !string.IsNullOrWhiteSpace(reconciler.ClientCertificatePath))
            {
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                handler.ClientCertificates.Add(X509CertificateLoader.LoadPkcs12FromFile(reconciler.ClientCertificatePath, reconciler.ClientCertificatePassword));
            }

            return handler;
        });
        services.AddSingleton<ITeamMembershipOutcomeAuthority, TeamMembershipReconcilerHttpClient>();
        return services;
    }
}
