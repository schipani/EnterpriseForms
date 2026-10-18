using Microsoft.Extensions.Options;

namespace EnterpriseForms.Api.Composition;

public sealed class TrustAuthorityOptions
{
    public const string SectionName = "TrustAuthority";

    /// <summary>Private base address of the independently deployed Authorization Authority. Required for readiness.</summary>
    public string? BaseAddress { get; set; }

    public int ReadinessTimeoutSeconds { get; set; } = 2;
}

public sealed record ReadinessResult(bool Ready, string Dependency, string Detail);

/// <summary>
/// Readiness depends on the Authorization Authority being reachable. Missing configuration or an unreachable
/// authority makes the API not ready: protected workflows must fail closed rather than fall back to local decisions.
/// </summary>
public sealed class TrustAuthorityReadinessProbe(IHttpClientFactory httpClientFactory, IOptions<TrustAuthorityOptions> options)
{
    public const string HttpClientName = "trust-authority-readiness";
    public const string DependencyName = "AuthorizationAuthority";

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value.BaseAddress;
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out var baseAddress))
        {
            return new ReadinessResult(false, DependencyName, "AuthorityNotConfigured");
        }

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ReadinessTimeoutSeconds));
            using var response = await client.GetAsync(new Uri("internal/v1/health/live", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new ReadinessResult(true, DependencyName, "Reachable")
                : new ReadinessResult(false, DependencyName, "AuthorityUnhealthy");
        }
        catch (HttpRequestException)
        {
            return new ReadinessResult(false, DependencyName, "AuthorityUnreachable");
        }
        catch (TaskCanceledException)
        {
            return new ReadinessResult(false, DependencyName, "AuthorityTimeout");
        }
    }
}

public static class TrustAuthorityReadinessRegistration
{
    public static IServiceCollection AddTrustAuthorityReadinessProbe(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TrustAuthorityOptions>(configuration.GetSection(TrustAuthorityOptions.SectionName));
        services.AddHttpClient(TrustAuthorityReadinessProbe.HttpClientName);
        services.AddSingleton<TrustAuthorityReadinessProbe>();
        return services;
    }
}
