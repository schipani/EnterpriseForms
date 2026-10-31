using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;

namespace AuthorizationAuthority.Application.Features.OperatorPlane;

/// <summary>DR-C7-3 configuration (section <c>OperatorApprovals</c>). Public keys only; the Authority never holds an approver's private key.</summary>
public sealed class OperatorApprovalOptions
{
    public const string SectionName = "OperatorApprovals";

    /// <summary><c>Unavailable</c> (default) or <c>SyntheticDevelopment</c> (Development/Testing only).</summary>
    public string? Mode { get; set; }

    /// <summary>Upper bound of an approval's lifetime (exp − iat); 60..3600 seconds, default 900.</summary>
    public int MaxApprovalLifetimeSeconds { get; set; } = 900;

    public List<OperatorApproverOptions> Approvers { get; set; } = [];
}

public sealed class OperatorApproverOptions
{
    public string? ApproverId { get; set; }

    /// <summary>Base64 of the approver's P-256 public key (SubjectPublicKeyInfo, DER).</summary>
    public string? PublicKeyBase64 { get; set; }
}

/// <summary>One approval that passed every check.</summary>
public sealed record VerifiedOperatorApproval(string ApproverId, string ApprovalSha256, DateTimeOffset IssuedOn, DateTimeOffset ExpiresOn);

/// <summary>
/// DR-C7-3 dual control. An appointment or recovery executes only with two approvals, each a compact JWS (ES256) signed by a
/// different registered approver key over the exact approval statement (organization, member identity, role, kind,
/// justification, incident, recovery scope, single-use operation id), unexpired and short-lived. The Authority holds public
/// keys only, so neither the operator credential nor the Authority can produce an approval; one approver key cannot
/// produce two.
/// <para>
/// Modes: <c>Unavailable</c> — the default and the only mode outside Development/Testing until production approvals come
/// from the privileged identity (PIM) model; every execution is refused <c>OPERATOR_DUAL_CONTROL_UNAVAILABLE</c>.
/// <c>SyntheticDevelopment</c> — Development/Testing only (startup refuses it elsewhere): approver keys generated for a
/// local stack. The verification is identical, but one person typically holds both keys, so it is recorded as synthetic
/// and is never two-human approval evidence.
/// </para>
/// </summary>
public sealed class OperatorApprovalVerifier
{
    public const string UnavailableMode = "Unavailable";
    public const string SyntheticMode = "SyntheticDevelopment";
    public const string SyntheticAssurance = "SyntheticDevelopment (development-only approver keys; not two-human approval evidence)";
    public const string UnavailableAssurance = "None (dual-controlled operator approval is not available in this environment)";
    public const int RequiredApprovals = 2;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, byte[]> approvers;

    private OperatorApprovalVerifier(string mode, int maxLifetimeSeconds, Dictionary<string, byte[]> approvers)
    {
        Mode = mode;
        MaxApprovalLifetimeSeconds = maxLifetimeSeconds;
        this.approvers = approvers;
    }

    public static OperatorApprovalVerifier Unavailable { get; } = new(UnavailableMode, 900, new Dictionary<string, byte[]>(StringComparer.Ordinal));

    public string Mode { get; }

    public bool Operational => Mode == SyntheticMode;

    public string Assurance => Operational ? SyntheticAssurance : UnavailableAssurance;

    public int MaxApprovalLifetimeSeconds { get; }

    public static OperatorApprovalVerifier Create(OperatorApprovalOptions? options, string environmentName)
    {
        options ??= new OperatorApprovalOptions();
        if (string.IsNullOrWhiteSpace(options.Mode) || string.Equals(options.Mode, UnavailableMode, StringComparison.Ordinal))
        {
            return Unavailable;
        }

        if (!string.Equals(options.Mode, SyntheticMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}:Mode must be {UnavailableMode} or {SyntheticMode}; production approvals require the privileged identity model, which is not implemented. Refusing to start.");
        }

        if (environmentName is not ("Development" or "Testing"))
        {
            throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}:Mode={SyntheticMode} is development-only and must not be set in the {environmentName} environment. Refusing to start.");
        }

        if (options.MaxApprovalLifetimeSeconds is < 60 or > 3600)
        {
            throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}:MaxApprovalLifetimeSeconds must be 60..3600.");
        }

        var registry = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var approver in options.Approvers)
        {
            if (string.IsNullOrWhiteSpace(approver.ApproverId) || approver.ApproverId.Length > 64 || string.IsNullOrWhiteSpace(approver.PublicKeyBase64))
            {
                throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}: every approver needs an id (≤ 64 characters) and a public key.");
            }

            var spki = Convert.FromBase64String(approver.PublicKeyBase64);
            using (var key = ECDsa.Create())
            {
                key.ImportSubjectPublicKeyInfo(spki, out _);
                if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                {
                    throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}: approver {approver.ApproverId} must use a P-256 key.");
                }
            }

            if (!registry.TryAdd(approver.ApproverId, spki) || !keys.Add(approver.PublicKeyBase64))
            {
                throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}: approver ids and keys must be distinct.");
            }
        }

        if (registry.Count < RequiredApprovals)
        {
            throw new InvalidOperationException($"{OperatorApprovalOptions.SectionName}: at least {RequiredApprovals} approvers with distinct keys are required.");
        }

        return new OperatorApprovalVerifier(SyntheticMode, options.MaxApprovalLifetimeSeconds, registry);
    }

    /// <summary>Fails closed on anything but exactly two valid, independent, unexpired approvals of this statement.</summary>
    public IReadOnlyList<VerifiedOperatorApproval> Verify(IReadOnlyList<string>? approvals, SecurityAdministratorApprovalStatement statement, string statementSha256, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return Verify(approvals, new ApprovalBinding(statement.OperationId, statement.OperationKind, statement.OrganizationId, statement.TargetAuthorityUserId), statementSha256, now);
    }

    /// <summary>C8 M2 (AC-7.4): the same two-approver verification for a membership request.</summary>
    public IReadOnlyList<VerifiedOperatorApproval> Verify(IReadOnlyList<string>? approvals, MembershipApprovalStatement statement, string statementSha256, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return Verify(approvals, new ApprovalBinding(statement.OperationId, statement.OperationKind, statement.OrganizationId, statement.TargetAuthorityUserId), statementSha256, now);
    }

    /// <summary>
    /// C8 M2 Stage 1: the Platform Operator recovery ceremony (no organization, no member): the same two-approver verification,
    /// bound to <see cref="Guid.Empty"/> organization and target so an approval for any organization operation can never be reused here.
    /// </summary>
    public IReadOnlyList<VerifiedOperatorApproval> VerifyPlatformCeremony(IReadOnlyList<string>? approvals, Guid operationId, string operationKind, string statementSha256, DateTimeOffset now) =>
        Verify(approvals, new ApprovalBinding(operationId, operationKind, Guid.Empty, Guid.Empty), statementSha256, now);

    private List<VerifiedOperatorApproval> Verify(IReadOnlyList<string>? approvals, ApprovalBinding statement, string statementSha256, DateTimeOffset now)
    {
        if (!Operational)
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorDualControlUnavailable, 403, false, "dual-controlled operator approval is not available in this environment (production requires the privileged identity model)");
        }

        if (approvals is null || approvals.Count != RequiredApprovals)
        {
            throw Invalid($"exactly {RequiredApprovals} approvals from distinct approvers are required");
        }

        var verified = approvals.Select(a => VerifyOne(a, statement, statementSha256, now)).ToList();
        if (verified.Select(v => v.ApproverId).Distinct(StringComparer.Ordinal).Count() != verified.Count)
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorApprovalsNotIndependent, 403, false, "the approvals must come from different approvers");
        }

        return verified;
    }

    /// <summary>The claims an approval must bind besides the statement digest.</summary>
    private sealed record ApprovalBinding(Guid OperationId, string OperationKind, Guid OrganizationId, Guid TargetAuthorityUserId);

    private VerifiedOperatorApproval VerifyOne(string token, ApprovalBinding statement, string statementSha256, DateTimeOffset now)
    {
        var parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 3 || token!.Length > 8192)
        {
            throw Invalid("an approval is not a compact JWS");
        }

        JsonElement header;
        JsonElement claims;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
            claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Invalid("an approval is malformed");
        }

        if (header.ValueKind != JsonValueKind.Object || String(header, "alg") != "ES256" || String(header, "kid") is not { } approverId || !approvers.TryGetValue(approverId, out var spki))
        {
            throw Invalid("an approval is not signed by a registered approver with ES256");
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(spki, out _);
            if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256))
            {
                throw Invalid("an approval signature is not valid");
            }
        }

        if (claims.ValueKind != JsonValueKind.Object
            || String(claims, "iss") != OperatorApprovalClaims.Issuer
            || String(claims, "aud") != OperatorApprovalClaims.Audience
            || String(claims, "sub") != approverId
            || String(claims, "jti") is not { Length: > 0 })
        {
            throw Invalid("an approval has the wrong issuer, audience, approver or no id");
        }

        if (!string.Equals(String(claims, OperatorApprovalClaims.StatementDigest), statementSha256, StringComparison.Ordinal)
            || String(claims, OperatorApprovalClaims.OperationId) != statement.OperationId.ToString("D")
            || String(claims, OperatorApprovalClaims.OperationKind) != statement.OperationKind
            || String(claims, OperatorApprovalClaims.OrganizationId) != statement.OrganizationId.ToString("D")
            || String(claims, OperatorApprovalClaims.TargetAuthorityUserId) != statement.TargetAuthorityUserId.ToString("D"))
        {
            throw Invalid("an approval is for a different operation, organization, member or statement");
        }

        if (Seconds(claims, "iat") is not { } iat || Seconds(claims, "exp") is not { } exp || Seconds(claims, "nbf") is not { } nbf)
        {
            throw Invalid("an approval lacks iat, nbf or exp");
        }

        if (exp <= iat || exp - iat > TimeSpan.FromSeconds(MaxApprovalLifetimeSeconds) || iat > now + ClockSkew || nbf > now + ClockSkew)
        {
            throw Invalid("an approval's lifetime is not acceptable");
        }

        if (exp <= now - ClockSkew)
        {
            throw Invalid("an approval has expired");
        }

        return new VerifiedOperatorApproval(approverId, Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token))), iat, exp);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static AuthorityException Invalid(string detail) => new(AuthorityErrorCodes.OperatorApprovalInvalid, 403, false, detail);
}
