using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseForms.SecurityTests.Harness;

/// <summary>
/// TestIdentityProvider (synthetic, env.example keys). Mints end-user assertions exactly as the Docker business API and
/// authority validate them, plus operator and (for SEC-P11) forged workload tokens. Nothing here is a production identity.
/// </summary>
public static class TestIdentity
{
    public const string Issuer = "https://test-idp.enterpriseforms.invalid";
    public const string Audience = "enterpriseforms";
    private const string UserKeyBase64 = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==";
    private const string OperatorKeyBase64 = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
    private const string WorkloadKeyBase64 = "d29ya2xvYWQtZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";

    private static readonly JsonWebTokenHandler Handler = new();

    public static string UserToken(TestUser user, Guid organizationId, TimeSpan? lifetime = null) =>
        Mint(UserKeyBase64, Issuer, Audience, user.Subject, new Dictionary<string, object>
        {
            ["org"] = organizationId.ToString("D"),
            ["authority_user_id"] = user.AuthorityUserId.ToString("D"),
            ["name"] = user.DisplayName,
        }, lifetime ?? TimeSpan.FromMinutes(30));

    public static string OperatorToken() =>
        Mint(OperatorKeyBase64, "authority-operator", "authorization-authority-operator", "stage-a-harness", new Dictionary<string, object> { ["workload_role"] = "authority-operator", ["workload_name"] = "stage-a-harness" }, TimeSpan.FromMinutes(10));

    /// <summary>A business-workload token: used by SEC-P11 to prove the business credential cannot reach the operator plane.</summary>
    public static string WorkloadToken() =>
        Mint(WorkloadKeyBase64, "enterpriseforms-business-api", "authorization-authority", "enterpriseforms-api", new Dictionary<string, object> { ["workload_role"] = "business-api", ["workload_name"] = "sec-p11-harness" }, TimeSpan.FromMinutes(2));

    /// <summary>A token signed with a key the authority does not trust (forged issuer).</summary>
    public static string ForgedUserToken(TestUser user, Guid organizationId) =>
        Mint(Convert.ToBase64String(new byte[32]), Issuer, Audience, user.Subject, new Dictionary<string, object> { ["org"] = organizationId.ToString("D"), ["authority_user_id"] = user.AuthorityUserId.ToString("D") }, TimeSpan.FromMinutes(5));

    private static string Mint(string keyBase64, string issuer, string audience, string subject, Dictionary<string, object> claims, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        claims["sub"] = subject;
        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.Add(lifetime),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(keyBase64)), SecurityAlgorithms.HmacSha256),
        });
    }
}

public sealed record TestUser(Guid AuthorityUserId, string Subject, string DisplayName)
{
    public static TestUser New(string name) => new(Guid.CreateVersion7(), name.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..8], name);
}
