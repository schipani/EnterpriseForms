using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C7 OD-1 on the real Authority host pipeline: the privileged migration catalogue accepts only the separate
/// field-protection verifier credential. The business workload credential, user assertions and forged or default-key
/// tokens never pass; an unconfigured verifier means nothing passes; a partial or reused verifier credential refuses to start.
/// </summary>
public sealed class VerifierBoundaryTests
{
    private const string WorkloadKey = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string OperatorKey = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string VerifierKey = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string VerifierIssuer = "enterpriseforms-field-protection-verifier";
    private const string VerifierAudience = "authorization-authority-verifier";
    private const string Route = "/internal/v1/verifier/migration-catalogue";

    private static string Token(string keyBase64, string issuer, string audience, string role) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddSeconds(-5),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "c7-verifier-test", ["workload_role"] = role, ["workload_name"] = "c7-verifier-test" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(keyBase64)), SecurityAlgorithms.HmacSha256),
        });

    private static WebApplicationFactory<AuthorizationAuthority.Api.Program> Authority(bool verifierConfigured, Action<IWebHostBuilder>? more = null) =>
        new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:AuthorityDb", "Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none;Timeout=2");
            if (verifierConfigured)
            {
                b.UseSetting("Identity:Verifier:Issuer", VerifierIssuer);
                b.UseSetting("Identity:Verifier:Audience", VerifierAudience);
                b.UseSetting("Identity:Verifier:SharedKeyBase64", VerifierKey);
            }

            more?.Invoke(b);
        });

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new { organization_id = Guid.CreateVersion7(), form_definition_id = Guid.CreateVersion7(), security_command_id = Guid.CreateVersion7() }) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    [Fact]
    public async Task OnlyTheVerifierCredentialReachesTheMigrationCatalogue()
    {
        using var authority = Authority(verifierConfigured: true);
        using var client = authority.CreateClient();
        // The business API's own workload credential (the one it holds in every environment) is refused.
        Assert.Contains(await PostAsync(client, Token(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", "business-api")), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        // A business workload token that claims the verifier role is still signed with the business key: refused.
        Assert.Contains(await PostAsync(client, Token(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", "field-protection-verifier")), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        // The verifier key with the wrong role, the operator credential, a default zero key, and no token: refused.
        Assert.Contains(await PostAsync(client, Token(VerifierKey, VerifierIssuer, VerifierAudience, "business-api")), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains(await PostAsync(client, Token(OperatorKey, "authority-operator", "authorization-authority-operator", "authority-operator")), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains(await PostAsync(client, Token(Convert.ToBase64String(new byte[32]), "unconfigured", "unconfigured", "field-protection-verifier")), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(client, null));
        // The genuine verifier credential passes authorization; the (deliberately unreachable) store then fails closed.
        var genuine = await PostAsync(client, Token(VerifierKey, VerifierIssuer, VerifierAudience, "field-protection-verifier"));
        Assert.DoesNotContain(genuine, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK });
    }

    [Fact]
    public async Task WithoutAVerifierCredentialNothingReachesTheMigrationCatalogue()
    {
        using var authority = Authority(verifierConfigured: false);
        using var client = authority.CreateClient();
        foreach (var token in new[]
                 {
                     Token(VerifierKey, VerifierIssuer, VerifierAudience, "field-protection-verifier"),
                     Token(Convert.ToBase64String(new byte[32]), "unconfigured", "unconfigured", "field-protection-verifier"),
                     Token(WorkloadKey, "enterpriseforms-business-api", "authorization-authority", "field-protection-verifier"),
                 })
        {
            Assert.Contains(await PostAsync(client, token), new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void APartialOrReusedVerifierCredentialRefusesToStart(bool partial, bool reuseWorkloadKey)
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Identity:Verifier:Issuer", VerifierIssuer);
            if (!partial)
            {
                b.UseSetting("Identity:Verifier:Audience", VerifierAudience);
                b.UseSetting("Identity:Verifier:SharedKeyBase64", reuseWorkloadKey ? WorkloadKey : VerifierKey);
            }
        });
        var failure = Assert.ThrowsAny<Exception>(() => authority.CreateClient());
        Assert.Contains(partial ? "configured completely" : "own key and issuer", Flatten(failure), StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        var text = string.Empty;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text += current.Message + "\n";
        }

        return text;
    }
}
