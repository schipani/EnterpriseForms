using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>Shared rules of the platform plane: who the caller is, candidate identities, summaries and justifications.</summary>
public static partial class PlatformOperatorAccess
{
    /// <summary>
    /// The registered operator named by the verified operator assertion, which must be Active. Unknown, suspended and removed
    /// identities get the same refusal (no registry enumeration). Organization identities never qualify: the operator registry is
    /// separate from identity bindings, and holding an organization role confers nothing here.
    /// </summary>
    public static async Task<PlatformOperator> ResolveActiveAsync(IAuthorityScope scope, VerifiedActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        var platformOperator = await scope.FindPlatformOperatorByIdentityAsync(actor.IssuerUri, actor.SubjectIdentifier, cancellationToken).ConfigureAwait(false);
        if (platformOperator is null || platformOperator.OperatorStatusName != PlatformOperatorStates.Active)
        {
            throw new AuthorityException(AuthorityErrorCodes.PlatformOperatorNotActive, 403, false, "the signed-in identity is not an active Platform Operator");
        }

        return platformOperator;
    }

    public static string RequireJustification(string? justification)
    {
        var trimmed = justification?.Trim() ?? string.Empty;
        if (trimmed.Length is < 10 or > 500 || trimmed.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw AuthorityException.Invalid("a justification of 10 to 500 characters is required");
        }

        return trimmed;
    }

    public static PlatformOperatorSummary Summary(PlatformOperator o) =>
        new(o.PlatformOperatorId, o.OperatorIssuerUri, o.OperatorSubjectIdentifier, o.OperatorDisplayName, o.OperatorStatusName, o.OperatorEvidenceKindName, o.SyntheticApprovalKeyIdentifier,
            o.OperatorRowRevision, o.OperatorProvisionedOn, o.OperatorStatusChangedOn, o.OperatorStatusReasonText);

    /// <summary>A validated new operator identity (issuer, subject, display name, evidence kind and synthetic public key).</summary>
    public sealed record ValidatedCandidate(string IssuerUri, string SubjectIdentifier, string DisplayName, string EvidenceKind, string? KeyIdentifier, byte[]? PublicKey)
    {
        public string? PublicKeySha256 => PublicKey is null ? null : Convert.ToHexStringLower(SHA256.HashData(PublicKey));
    }

    public static ValidatedCandidate ValidateCandidate(PlatformOperatorCandidate? candidate)
    {
        if (candidate is null)
        {
            throw AuthorityException.Invalid("a candidate operator identity is required");
        }

        var issuer = candidate.IssuerUri?.Trim() ?? string.Empty;
        var subject = candidate.SubjectIdentifier?.Trim() ?? string.Empty;
        var name = candidate.DisplayName?.Trim() ?? string.Empty;
        if (issuer.Length is 0 or > 512 || !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps)
        {
            throw AuthorityException.Invalid("the operator issuer must be an absolute https URI of at most 512 characters");
        }

        if (subject.Length is 0 or > 512 || subject.Any(char.IsWhiteSpace) || subject.Contains('@', StringComparison.Ordinal))
        {
            throw AuthorityException.Invalid("the operator subject must be 1 to 512 characters, without spaces, and never an email address");
        }

        if (name.Length is 0 or > 200 || name.Any(char.IsControl))
        {
            throw AuthorityException.Invalid("a display name of 1 to 200 characters is required (display only, never an identifier)");
        }

        if (candidate.EvidenceKind == PlatformOperatorEvidenceKinds.EntraTransactionStepUp)
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorEvidenceUnavailable, 403, false, "operators with Entra transaction-bound step-up evidence cannot be registered until PB-08 exists");
        }

        if (candidate.EvidenceKind != PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws)
        {
            throw AuthorityException.Invalid("unknown evidence kind");
        }

        var keyId = candidate.SyntheticKeyIdentifier?.Trim() ?? string.Empty;
        if (!KeyIdentifierPattern().IsMatch(keyId))
        {
            throw AuthorityException.Invalid("a synthetic key identifier of 1 to 64 characters (letters, digits, '.', '_', '-') is required");
        }

        byte[] spki;
        try
        {
            spki = Convert.FromBase64String(candidate.SyntheticPublicKeyBase64 ?? string.Empty);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw AuthorityException.Invalid("the synthetic public key must be a P-256 SubjectPublicKeyInfo");
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw AuthorityException.Invalid("the synthetic public key must be a base64 P-256 SubjectPublicKeyInfo");
        }

        return new ValidatedCandidate(issuer, subject, name, candidate.EvidenceKind, keyId, spki);
    }

    /// <summary>Identity, key id and public key are each unique across every operator ever registered (removed ones included).</summary>
    public static AuthorityException? Conflicts(ValidatedCandidate candidate, IEnumerable<PlatformOperator> registered)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        foreach (var o in registered)
        {
            if ((o.OperatorIssuerUri == candidate.IssuerUri && o.OperatorSubjectIdentifier == candidate.SubjectIdentifier)
                || (candidate.KeyIdentifier is not null && o.SyntheticApprovalKeyIdentifier == candidate.KeyIdentifier)
                || (candidate.PublicKey is not null && o.SyntheticApprovalPublicKey is not null && o.SyntheticApprovalPublicKey.AsSpan().SequenceEqual(candidate.PublicKey)))
            {
                return new AuthorityException(AuthorityErrorCodes.OperatorIdentityExists, 409, false, "this identity or approval key is already registered (identities and keys are never reused)");
            }
        }

        return null;
    }

    public static PlatformOperator NewOperator(Guid platformOperatorId, ValidatedCandidate candidate, Guid? provisionedBy, string reason, DateTimeOffset now) => new()
    {
        PlatformOperatorId = platformOperatorId,
        OperatorIssuerUri = candidate.IssuerUri,
        OperatorSubjectIdentifier = candidate.SubjectIdentifier,
        OperatorDisplayName = candidate.DisplayName,
        OperatorStatusName = PlatformOperatorStates.Active,
        OperatorEvidenceKindName = candidate.EvidenceKind,
        SyntheticApprovalKeyIdentifier = candidate.KeyIdentifier,
        SyntheticApprovalPublicKey = candidate.PublicKey,
        OperatorProvisionedOn = now,
        ProvisionedByPlatformOperatorId = provisionedBy,
        OperatorStatusChangedOn = now,
        OperatorStatusReasonText = reason.Length <= 500 ? reason : reason[..500],
        OperatorRowRevision = 1,
    };

    public static PlatformOperatorEvidenceUse EvidenceUse(VerifiedOperatorEvidence evidence, Guid platformOperatorId, string decision, DateTimeOffset now) => new()
    {
        PlatformOperatorEvidenceUseId = Guid.CreateVersion7(),
        EvidenceUseIdentifier = evidence.EvidenceIdentifier,
        EvidenceUseSha256 = evidence.EvidenceSha256,
        PlatformOperatorId = platformOperatorId,
        EvidenceDecisionName = decision,
        EvidenceUsedOn = now,
    };

    public static async Task RequireUnusedAsync(IAuthorityScope scope, VerifiedOperatorEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(evidence);
        if (await scope.IsPlatformEvidenceUsedAsync(evidence.EvidenceIdentifier, evidence.EvidenceSha256, cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorEvidenceReplayed, 409, false, "this evidence was already used once");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex KeyIdentifierPattern();
}
