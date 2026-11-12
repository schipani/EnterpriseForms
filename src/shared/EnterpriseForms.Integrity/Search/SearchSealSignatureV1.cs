using System.Security.Cryptography;

namespace EnterpriseForms.Integrity.Search;

/// <summary>
/// IX-1 (C-126): ES256 verification of a search entry seal. ECDSA over NIST P-256 with SHA-256 of the message, signature in
/// the fixed 64-byte IEEE P1363 form (r || s, as JWS ES256). Verification uses only a public key supplied by the caller from
/// its pinned configuration; this type never fetches a key. Signing happens only in the Authority's signature provider
/// (Managed HSM in production, Vault Transit in Docker, an in-process key in unit tests).
/// </summary>
public static class SearchSealSignatureV1
{
    public const string Algorithm = "ES256";
    public const int SignatureLength = 64;

    /// <summary>
    /// Imports a SubjectPublicKeyInfo that must be an EC public key on the P-256 curve. Any other key (RSA, another curve, a
    /// private key, trailing bytes) is refused.
    /// </summary>
    public static ECDsa ImportPinnedPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
            if (read != subjectPublicKeyInfo.Length)
            {
                throw new CryptographicException("trailing bytes after the SubjectPublicKeyInfo");
            }

            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (parameters.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new CryptographicException("the pinned seal key is not on the P-256 curve");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>True only for a 64-byte P1363 signature that verifies over <paramref name="message"/> with <paramref name="publicKey"/>.</summary>
    public static bool Verify(ECDsa publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return signature.Length == SignatureLength && publicKey.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>SHA-256 thumbprint of a SubjectPublicKeyInfo, lower-case hex (diagnostics and pin comparison).</summary>
    public static string Thumbprint(ReadOnlySpan<byte> subjectPublicKeyInfo) => Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
}
