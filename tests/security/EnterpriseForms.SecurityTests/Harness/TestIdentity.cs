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
    private const string VerifierKeyBase64 = "dmVyaWZpZXItdGVzdC1vbmx5LXNoYXJlZC1rZXktMDAwMDAwMDAw";
    private const string ReconcilerKeyBase64 = "cmVjb25jaWxlci10ZXN0LW9ubHktc2hhcmVkLWtleS0wMDAwMDA=";

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

    /// <summary>Phase C7 OD-1: the field-protection verifier workload (compose.security-test.yaml synthetic credential).</summary>
    public static string VerifierToken(string workloadName = "field-protection-verifier") =>
        Mint(VerifierKeyBase64, "enterpriseforms-field-protection-verifier", "authorization-authority-verifier", "c7-verifier-harness", new Dictionary<string, object> { ["workload_role"] = "field-protection-verifier", ["workload_name"] = workloadName }, TimeSpan.FromMinutes(5));

    /// <summary>Phase C8 AC-9: the team-membership reconciler workload (compose.security-test.yaml synthetic credential).</summary>
    public static string ReconcilerToken(string role = "business-projection-reconciler", string? keyBase64 = null) =>
        Mint(keyBase64 ?? ReconcilerKeyBase64, "enterpriseforms-team-membership-reconciler", "authorization-authority-reconciler", "c8-reconciler-harness", new Dictionary<string, object> { ["workload_role"] = role, ["workload_name"] = "c8-reconciler-harness" }, TimeSpan.FromMinutes(5));

    /// <summary>A correctly signed user token that expired ten minutes ago (beyond the 30-second clock skew).</summary>
    public static string ExpiredUserToken(TestUser user, Guid organizationId) =>
        Mint(UserKeyBase64, Issuer, Audience, user.Subject, new Dictionary<string, object>
        {
            ["org"] = organizationId.ToString("D"),
            ["authority_user_id"] = user.AuthorityUserId.ToString("D"),
            ["name"] = user.DisplayName,
        }, TimeSpan.FromMinutes(5), DateTime.UtcNow.AddMinutes(-15));

    /// <summary>DR-C7-3: synthetic operator approver keys of the security-test stack (compose.security-test.yaml holds the public halves).</summary>
    public static readonly IReadOnlyList<(string ApproverId, string PrivateKeyBase64)> SyntheticApprovers =
    [
        ("test-synthetic-approver-1", "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgubV0XkbnW6lMPVhJU6JKK4FQRgKcwkxV3TgVshYi4UKhRANCAARoELZWd+48UQ18N+lEvmAbpmFgIPE4kH8yPzJe/DY/G6x8FGBXV3XCO/L8c/rtUhEJ9bSHrv6Ey4cIXHutX+oN"),
        ("test-synthetic-approver-2", "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQguS+l+uQrwnftGAtgkLX9YrrSmPp5RJrSBIFbyAg8EBehRANCAAThWvAJuI7Aysr5TgXTtXlpoudJOuB8GHSnuH1pqkv8i5ma8gj9i/L2df8jPq+NUPPjrkNEd7R1Zl4gtHiH8T8T"),
    ];

    /// <summary>
    /// DR-C7-3: an operator approval (compact JWS, ES256) over a dry-run preview's statement, as an approver's tooling produces
    /// it. <paramref name="key"/> null uses the registered synthetic key of <paramref name="approverId"/>.
    /// </summary>
    public static string OperatorApproval(System.Text.Json.JsonElement preview, string approverId, System.Security.Cryptography.ECDsa? key = null, Action<Dictionary<string, object>>? tamper = null, DateTimeOffset? issuedAt = null, TimeSpan? lifetime = null)
    {
        using var registered = System.Security.Cryptography.ECDsa.Create();
        if (key is null)
        {
            registered.ImportPkcs8PrivateKey(Convert.FromBase64String(SyntheticApprovers.Single(a => a.ApproverId == approverId).PrivateKeyBase64), out _);
        }

        var iat = issuedAt ?? DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "enterpriseforms-operator-approval",
            ["aud"] = "authorization-authority-operator-approval",
            ["sub"] = approverId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = iat.ToUnixTimeSeconds(),
            ["nbf"] = iat.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = iat.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds(),
            ["statement_sha256"] = preview.GetProperty("approval_statement_sha256").GetString()!,
            ["operation_id"] = preview.GetProperty("operation_id").GetString()!,
            ["operation_kind"] = preview.GetProperty("operation_kind").GetString()!,
            ["organization_id"] = preview.GetProperty("organization_id").GetString()!,
            ["target_authority_user_id"] = preview.GetProperty("target").GetProperty("authority_user_id").GetString()!,
        };
        tamper?.Invoke(claims);
        var header = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = approverId }));
        var payload = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = (key ?? registered).SignData(System.Text.Encoding.ASCII.GetBytes(header + "." + payload), System.Security.Cryptography.HashAlgorithmName.SHA256);
        return header + "." + payload + "." + System.Buffers.Text.Base64Url.EncodeToString(signature);
    }

    /// <summary>A token signed with a key the authority does not trust (forged issuer).</summary>
    public static string ForgedUserToken(TestUser user, Guid organizationId) =>
        Mint(Convert.ToBase64String(new byte[32]), Issuer, Audience, user.Subject, new Dictionary<string, object> { ["org"] = organizationId.ToString("D"), ["authority_user_id"] = user.AuthorityUserId.ToString("D") }, TimeSpan.FromMinutes(5));

    private static string Mint(string keyBase64, string issuer, string audience, string subject, Dictionary<string, object> claims, TimeSpan lifetime, DateTime? issuedAt = null)
    {
        var now = issuedAt ?? DateTime.UtcNow;
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
