using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>What one piece of evidence must bind besides its operator.</summary>
public sealed record OperatorEvidenceBinding(string Decision, string OperationKind, Guid OperationId, Guid OrganizationId, string StatementSha256, string JustificationSha256);

/// <summary>Evidence that passed every check. Its identifier and digest are single use.</summary>
public sealed record VerifiedOperatorEvidence(string EvidenceIdentifier, byte[] EvidenceSha256, string EvidenceKind, string KeyIdentifier, DateTimeOffset IssuedOn, DateTimeOffset ExpiresOn);

/// <summary>
/// D8-35: verifies that one registered operator decided one exact thing. The evidence is checked against <b>that operator's</b>
/// registry entry, so the session that submits it and the evidence must name the same person: an operator who presents another
/// operator's evidence is refused <c>OPERATOR_EVIDENCE_IDENTITY_MISMATCH</c>, and one key can never speak for two operators.
/// <para>
/// <c>SyntheticDevelopmentJws</c> (Development/Testing): compact JWS, <c>alg</c> exactly ES256, <c>kid</c> = <c>sub</c> = the
/// operator's registered synthetic key id, signed by that key (the Authority holds the public half only), with
/// <c>platform_operator_id</c>, <c>decision</c>, <c>operation_kind</c>, <c>operation_id</c>, <c>organization_id</c>,
/// <c>statement_sha256</c>, <c>justification_sha256</c>, <c>jti</c>, <c>iat</c>, <c>nbf</c>, <c>exp</c> (lifetime bounded by
/// policy, 30 s skew). <c>EntraTransactionStepUp</c> is always refused: a generic Entra token is not transaction-specific
/// evidence, and the PB-08 transaction-bound step-up is not implemented.
/// </para>
/// </summary>
public sealed class PlatformOperatorEvidenceVerifier(PlatformOperatorPolicy policy)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static string JustificationSha256(string justification) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(justification)));

    public VerifiedOperatorEvidence Verify(PlatformOperator platformOperator, string? evidenceKind, string? evidence, OperatorEvidenceBinding binding, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(platformOperator);
        ArgumentNullException.ThrowIfNull(binding);
        policy.RequireOperational();
        if (string.IsNullOrWhiteSpace(evidenceKind) || string.IsNullOrWhiteSpace(evidence))
        {
            throw Invalid("evidence of this operator's decision is required");
        }

        if (evidenceKind == PlatformOperatorEvidenceKinds.EntraTransactionStepUp)
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorEvidenceUnavailable, 403, false, "Entra transaction-bound step-up evidence is not implemented (PB-08); a generic Entra token is never accepted as approval evidence");
        }

        if (evidenceKind != PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws)
        {
            throw Invalid("unknown evidence kind");
        }

        if (platformOperator.OperatorEvidenceKindName != PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws || platformOperator.SyntheticApprovalPublicKey is not { Length: > 0 } spki || platformOperator.SyntheticApprovalKeyIdentifier is not { } keyId)
        {
            throw Mismatch("this evidence kind is not registered for the signed-in operator");
        }

        var parts = evidence.Split('.');
        if (parts.Length != 3 || evidence.Length > 8192)
        {
            throw Invalid("evidence is not a compact JWS");
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
            throw Invalid("evidence is malformed");
        }

        if (header.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Object || String(header, "alg") != "ES256")
        {
            throw Invalid("evidence must be an ES256 JWS");
        }

        // Identity before signature: evidence of another operator is reported as such, never verified against this operator's key.
        if (String(header, "kid") != keyId || String(claims, "sub") != keyId || String(claims, PlatformOperatorEvidenceClaims.PlatformOperatorId) != platformOperator.PlatformOperatorId.ToString("D"))
        {
            throw Mismatch("the evidence was not produced by the signed-in operator");
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(spki, out _);
            if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256))
            {
                throw Invalid("the evidence signature is not valid");
            }
        }

        if (String(claims, "iss") != PlatformOperatorEvidenceClaims.Issuer || String(claims, "aud") != PlatformOperatorEvidenceClaims.Audience || String(claims, "jti") is not { Length: > 0 and <= 128 } jti)
        {
            throw Invalid("the evidence has the wrong issuer or audience, or no id");
        }

        if (String(claims, PlatformOperatorEvidenceClaims.Decision) != binding.Decision
            || String(claims, PlatformOperatorEvidenceClaims.OperationKind) != binding.OperationKind
            || String(claims, PlatformOperatorEvidenceClaims.OperationId) != binding.OperationId.ToString("D")
            || String(claims, PlatformOperatorEvidenceClaims.OrganizationId) != binding.OrganizationId.ToString("D")
            || !string.Equals(String(claims, PlatformOperatorEvidenceClaims.StatementDigest), binding.StatementSha256, StringComparison.Ordinal)
            || !string.Equals(String(claims, PlatformOperatorEvidenceClaims.JustificationDigest), binding.JustificationSha256, StringComparison.Ordinal))
        {
            throw Invalid("the evidence is for a different decision, operation, organization, statement or justification");
        }

        if (Seconds(claims, "iat") is not { } iat || Seconds(claims, "exp") is not { } exp || Seconds(claims, "nbf") is not { } nbf)
        {
            throw Invalid("the evidence lacks iat, nbf or exp");
        }

        if (exp <= iat || exp - iat > policy.EvidenceMaxLifetime || iat > now + ClockSkew || nbf > now + ClockSkew)
        {
            throw Invalid("the evidence's lifetime is not acceptable");
        }

        if (exp <= now - ClockSkew)
        {
            throw Invalid("the evidence has expired");
        }

        return new VerifiedOperatorEvidence(jti, SHA256.HashData(Encoding.ASCII.GetBytes(evidence)), evidenceKind, keyId, iat, exp);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static AuthorityException Invalid(string detail) => new(AuthorityErrorCodes.OperatorEvidenceInvalid, 403, false, detail);

    private static AuthorityException Mismatch(string detail) => new(AuthorityErrorCodes.OperatorEvidenceIdentityMismatch, 403, false, detail);
}
