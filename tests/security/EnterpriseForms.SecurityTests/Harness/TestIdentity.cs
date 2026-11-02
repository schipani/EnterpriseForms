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

    // ---- C8 M2 Stages 1-2: Platform Operators (compose.security-test.yaml holds the console and operator-assertion keys) ----

    public const string PlatformOperatorIssuer = "https://platform-operators.sectest.enterpriseforms.invalid/";
    private const string PlatformConsoleKeyBase64 = "cGxhdGZvcm0tY29uc29sZS1zZWN0ZXN0LW9ubHktc2hhcmVkLWtleS0wMA==";
    private const string OperatorAssertionKeyBase64 = "cGxhdGZvcm0tb3BlcmF0b3ItYXNzZXJ0aW9uLXNlY3Rlc3Qtb25seS1rMDE=";

    /// <summary>
    /// Three DEVELOPMENT/TEST-ONLY synthetic Platform Operators (subject, display name, approval key id, PKCS#8 private key). They are
    /// registered by the recovery ceremony the first time a fresh stack needs them; the Authority only ever receives the public halves.
    /// One harness process holds all three keys, so nothing done with them is two-human approval evidence.
    /// </summary>
    public static readonly IReadOnlyList<(string Subject, string DisplayName, string KeyId, string PrivateKeyBase64)> SyntheticPlatformOperators =
    [
        ("sectest-operator-avery", "Avery Operator (synthetic)", "sectest-operator-key-avery", "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgUhXaF4rF\u002BJn6kloGJ2ZEPjy4kigiueg/90ohtF4X78ihRANCAASXj1UlLlZd41HaQ74j976Lf15SkD2Uuso1F9EM0U766fG6ZT9oxfUjIAKt1jIl2Zr/VRbm5dXUPsBuKMKf\u002BqqZ"),
        ("sectest-operator-blair", "Blair Operator (synthetic)", "sectest-operator-key-blair", "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg/lNZswPvAMHcat3NXfbfS1vcDJ3ZHmguQypX0UQrEcihRANCAASC0hCuDryciPThMmeLcphkkZr77IiHyv9IgiQhVIHIz2Ks\u002BRCkVQauEAr7WpyfaB0cpsER4mLR74CEtUhyifR7"),
        ("sectest-operator-cameron", "Cameron Operator (synthetic)", "sectest-operator-key-cameron", "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgwU/2uyEyssxFLRMGeiWqwCyoNgDkYAqC3niAP8OY3lmhRANCAATMAehdEXwB\u002BXQCGVLPvuYHrt9qWZLVWAgkK/C/j90Qpre2xUE2J2KlD8xTKJeNI0Z0Zl2EoQZfBDjakVUQl4Xp"),
    ];

    /// <summary>The Platform Console workload credential of the security-test stack.</summary>
    public static string PlatformConsoleToken(string role = "platform-console", string? keyBase64 = null) =>
        Mint(keyBase64 ?? PlatformConsoleKeyBase64, "enterpriseforms-platform-console", "authorization-authority-platform", "c8-platform-console-harness", new Dictionary<string, object> { ["workload_role"] = role, ["workload_name"] = "c8-platform-console-harness" }, TimeSpan.FromMinutes(5));

    /// <summary>The signed-in Platform Operator as the Platform Console asserts it (identity only; decisions need evidence).</summary>
    public static string PlatformOperatorAssertion(string subject, string? keyBase64 = null) =>
        Mint(keyBase64 ?? OperatorAssertionKeyBase64, PlatformOperatorIssuer, "authorization-authority-platform-operator", subject, [], TimeSpan.FromMinutes(5));

    public static string PlatformOperatorPublicKey(string privateKeyBase64)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static string JustificationSha256(string justification) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(justification)));

    /// <summary>One operator's evidence (compact JWS, ES256) of one decision, signed with <paramref name="privateKeyBase64"/>.</summary>
    public static string PlatformOperatorEvidence(string keyId, string privateKeyBase64, Guid platformOperatorId, string decision, string operationKind, Guid operationId, Guid organizationId, string statementSha256, string justification, Action<Dictionary<string, object>>? tamper = null)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "enterpriseforms-platform-operator-evidence",
            ["aud"] = "authorization-authority-platform-operator",
            ["sub"] = keyId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
            ["platform_operator_id"] = platformOperatorId.ToString("D"),
            ["decision"] = decision,
            ["operation_kind"] = operationKind,
            ["operation_id"] = operationId.ToString("D"),
            ["organization_id"] = organizationId.ToString("D"),
            ["statement_sha256"] = statementSha256,
            ["justification_sha256"] = JustificationSha256(justification),
        };
        tamper?.Invoke(claims);
        var header = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = (string)claims["sub"] }));
        var payload = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims));
        return header + "." + payload + "." + System.Buffers.Text.Base64Url.EncodeToString(key.SignData(System.Text.Encoding.ASCII.GetBytes(header + "." + payload), System.Security.Cryptography.HashAlgorithmName.SHA256));
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
