using System.Security.Cryptography;

namespace EnterpriseForms.Integrity.Canonical;

/// <summary>
/// HMAC-SHA-256 helper over canonical row bytes. In production the key is held by the independent signing
/// authority (Vault Transit locally, Azure Managed HSM in Azure) and this method runs only inside that
/// authority or in tests with disposable keys. The business API never holds organization integrity keys.
/// </summary>
public static class RowIntegrityHmac
{
    public const int TagLength = 32;
    public const int MinimumKeyLength = 32;

    public static byte[] ComputeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> canonicalBytes)
    {
        if (key.Length < MinimumKeyLength)
        {
            throw new ArgumentException("Integrity key must be at least 256 bits.", nameof(key));
        }

        return HMACSHA256.HashData(key, canonicalBytes);
    }

    /// <summary>Constant-time verification. Any length other than 32 is invalid.</summary>
    public static bool VerifyTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> canonicalBytes, ReadOnlySpan<byte> expectedTag)
    {
        if (expectedTag.Length != TagLength)
        {
            return false;
        }

        var actual = ComputeTag(key, canonicalBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expectedTag);
    }
}
