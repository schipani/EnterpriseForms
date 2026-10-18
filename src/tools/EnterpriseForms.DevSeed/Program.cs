using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.DevSeed;

/// <summary>
/// Bootstraps ONE synthetic development organization with four personas through the authority operator plane
/// (idempotent: a second run reuses the persisted ids) and writes dev-personas.json. Configuration via DEVSEED_* env.
/// Synthetic identities only; nothing here is a production provisioning path (ADR-PB-003 documented gap).
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main()
    {
        var authority = Env("DEVSEED_AUTHORITY_BASE_ADDRESS", "http://localhost:5090");
        var operatorIssuer = Env("DEVSEED_OPERATOR_ISSUER", "authority-operator");
        var operatorAudience = Env("DEVSEED_OPERATOR_AUDIENCE", "authorization-authority-operator");
        var operatorKey = Env("DEVSEED_OPERATOR_SHARED_KEY_BASE64", "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==");
        var idpIssuer = Env("DEVSEED_TEST_IDP_ISSUER", "https://test-idp.enterpriseforms.invalid");
        var output = Env("DEVSEED_OUTPUT_PATH", "infrastructure/docker/dev-seed/dev-personas.json");
        var organizationName = Env("DEVSEED_ORGANIZATION_NAME", "Northwind Crown Office (synthetic)");

        // Stable ids across runs (persisted next to the output) so the business projections and personas stay consistent.
        var statePath = output + ".ids";
        var ids = File.Exists(statePath) ? JsonSerializer.Deserialize<SeedIds>(await File.ReadAllTextAsync(statePath).ConfigureAwait(false), Json) : null;
        ids ??= new SeedIds(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        var personas = new List<Persona>
        {
            new("owner", "Avery Owner (Organization Owner + Designer)", ids.Owner, "dev-owner", ids.Organization, organizationName, ["organization_owner", "designer"]),
            new("designer", "Dana Designer", ids.Designer, "dev-designer", ids.Organization, organizationName, ["designer"]),
            new("clerk", "Casey Clerk (record user)", ids.Clerk, "dev-clerk", ids.Organization, organizationName, []),
            new("reader", "Riley Reader (no grants)", ids.Reader, "dev-reader", ids.Organization, organizationName, []),
        };

        using var client = new HttpClient { BaseAddress = new Uri(authority), Timeout = TimeSpan.FromSeconds(30) };
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            try
            {
                using var ready = await client.GetAsync(new Uri("internal/v1/health/ready", UriKind.Relative)).ConfigureAwait(false);
                if (ready.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }

        var gate = await client.GetAsync(new Uri($"internal/v1/operator/organizations/{ids.Organization:D}/gate", UriKind.Relative), Bearer(operatorIssuer, operatorAudience, operatorKey)).ConfigureAwait(false);
        if (gate.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var bootstrap = new
            {
                organization_id = ids.Organization,
                organization_display_name = organizationName,
                identities = personas.Select(p => new { authority_user_id = p.AuthorityUserId, issuer_uri = idpIssuer, subject_identifier = p.SubjectIdentifier, organization_roles = p.OrganizationRoles }).ToArray(),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("internal/v1/operator/organizations", UriKind.Relative)) { Content = JsonContent.Create(bootstrap) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer(operatorIssuer, operatorAudience, operatorKey));
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine("bootstrap failed: " + (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return 2;
            }

            Console.WriteLine("Organization bootstrapped: " + ids.Organization.ToString("D"));
        }
        else if (gate.IsSuccessStatusCode)
        {
            Console.WriteLine("Organization already bootstrapped: " + ids.Organization.ToString("D"));
        }
        else
        {
            Console.Error.WriteLine("operator gate check failed: " + (int)gate.StatusCode);
            return 3;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(personas, Json)).ConfigureAwait(false);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(ids, Json)).ConfigureAwait(false);
        Console.WriteLine("Personas written to " + output + " (" + personas.Count.ToString(CultureInfo.InvariantCulture) + ")");
        return 0;
    }

    private static Task<HttpResponseMessage> GetAsync(this HttpClient client, Uri uri, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    private static string Bearer(string issuer, string audience, string keyBase64)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "dev-seed", ["workload_role"] = "authority-operator", ["workload_name"] = "dev-seed" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(keyBase64)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private sealed record SeedIds(Guid Organization, Guid Owner, Guid Designer, Guid Clerk, Guid Reader);

    private sealed record Persona(string PersonaKey, string DisplayName, Guid AuthorityUserId, string SubjectIdentifier, Guid OrganizationId, string OrganizationDisplayName, IReadOnlyList<string> OrganizationRoles);
}
