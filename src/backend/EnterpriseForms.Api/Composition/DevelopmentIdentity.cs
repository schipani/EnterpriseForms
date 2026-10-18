using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Contracts.Features;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.Api.Composition;

public sealed class DevelopmentIdentityOptions
{
    public const string SectionName = "DevelopmentIdentity";

    /// <summary>Must be explicitly enabled; ignored (login refused) unless the host environment is Development/Testing AND the provider is TestIdentityProvider.</summary>
    public bool Enabled { get; set; }

    /// <summary>JSON file produced by the dev-seed tool after bootstrapping the organization through the authority operator plane.</summary>
    public string? PersonasFilePath { get; set; }

    public List<DevelopmentPersona> Personas { get; set; } = [];

    public int SessionHours { get; set; } = 8;
}

public sealed class DevelopmentPersona
{
    public string PersonaKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public Guid AuthorityUserId { get; set; }

    public string SubjectIdentifier { get; set; } = string.Empty;

    public Guid OrganizationId { get; set; }

    public string OrganizationDisplayName { get; set; } = string.Empty;

    public List<string> OrganizationRoles { get; set; } = [];
}

/// <summary>
/// Development login (B-01): mints a TestIdentityProvider assertion for a seeded synthetic persona and places it in the
/// BFF session cookie. Personas are bootstrapped by the separate dev-seed tool through the authority operator plane —
/// this API holds no operator credential and cannot create identities or roles. Not a production bypass: the startup
/// guard already refuses the TestIdentityProvider outside Development/Testing, and this provider additionally refuses
/// to mint unless explicitly enabled in a Development/Testing host.
/// </summary>
public sealed class DevelopmentIdentityProvider(IOptions<DevelopmentIdentityOptions> options, IOptions<UserAuthenticationOptions> authentication, IHostEnvironment environment, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly JsonWebTokenHandler handler = new();
    private List<DevelopmentPersona>? cached;
    private DateTimeOffset cachedOn;

    public bool IsAvailable => options.Value.Enabled && authentication.Value.ProviderKind == "TestIdentityProvider" && authentication.Value.IsConfigured && (environment.IsDevelopment() || environment.IsEnvironment("Testing"));

    public IReadOnlyList<DevelopmentPersona> Personas()
    {
        if (!IsAvailable)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        if (cached is not null && now - cachedOn < TimeSpan.FromSeconds(5))
        {
            return cached;
        }

        var personas = new List<DevelopmentPersona>(options.Value.Personas);
        var path = options.Value.PersonasFilePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var fromFile = JsonSerializer.Deserialize<List<DevelopmentPersona>>(File.ReadAllText(path), Json) ?? [];
                personas.AddRange(fromFile.Where(f => personas.All(p => p.PersonaKey != f.PersonaKey)));
            }
            catch (JsonException)
            {
                // a malformed seed file yields no personas rather than a partial identity set
            }
        }

        cached = personas;
        cachedOn = now;
        return personas;
    }

    public (string Assertion, DevelopmentPersona Persona, DateTimeOffset ExpiresOn) Mint(string personaKey)
    {
        if (!IsAvailable)
        {
            throw new BusinessException("DEVELOPMENT_LOGIN_DISABLED", 404, "development login is not available in this environment");
        }

        var persona = Personas().FirstOrDefault(p => string.Equals(p.PersonaKey, personaKey, StringComparison.Ordinal))
                      ?? throw new BusinessException("UNKNOWN_PERSONA", 404, "unknown development persona");
        var settings = authentication.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddHours(Math.Clamp(options.Value.SessionHours, 1, 12));
        var assertion = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime.AddSeconds(-5),
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = persona.SubjectIdentifier,
                ["org"] = persona.OrganizationId.ToString("D"),
                ["authority_user_id"] = persona.AuthorityUserId.ToString("D"),
                ["name"] = persona.DisplayName,
            },
            SigningCredentials = new SigningCredentials(settings.SymmetricKey(), SecurityAlgorithms.HmacSha256),
        });
        return (assertion, persona, expires);
    }

    public static DevelopmentPersonaResponse ToResponse(DevelopmentPersona p) => new(p.PersonaKey, p.DisplayName, p.OrganizationId, p.OrganizationDisplayName, p.OrganizationRoles);
}
