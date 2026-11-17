namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>An ES256 seal signature (64-byte P1363 r || s) and the key version that produced it.</summary>
public sealed record SearchSealSignature(byte[] Signature, string KeyName, int KeyVersion);

/// <summary>
/// IX-1 (C-126): the signing half of the search entry seal. Implemented by a non-exportable P-256 key held by the provider
/// (Azure Managed HSM in production, Vault Transit in Docker, an in-process key in unit tests). Inside the Authority only:
/// called by the commit-time derivation after the staged bytes were verified, never exposed as an endpoint, never given a
/// caller-chosen message. Verification is local (<c>SearchSealVerifier</c>) with the pinned public key.
/// </summary>
public interface ISearchSealSigner
{
    string ProviderName { get; }

    /// <summary>The configured signing key name and version (from deployment configuration only).</summary>
    string SigningKeyName { get; }

    int SigningKeyVersion { get; }

    Task<SearchSealSignature> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>The provider's SubjectPublicKeyInfo for a key version, used only to check it against the pin at startup.</summary>
    Task<byte[]> GetPublicKeyAsync(string keyName, int keyVersion, CancellationToken cancellationToken);
}

/// <summary>Thrown when the seal signer cannot sign or answer; the commit that needed it fails closed.</summary>
public sealed class SearchSealUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);
