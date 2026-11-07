using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common.Ports;
using EnterpriseForms.Integrity.Search;


namespace AuthorizationAuthority.Application.Features.SearchIndex;

public enum SealVerificationOutcome
{
    Valid = 1,
    Invalid = 2,

    /// <summary>No pinned key for the stated name and version: Inconclusive, never Valid (§17.10.2).</summary>
    UnknownKey = 3,
}

/// <summary>
/// IX-1 (C-126): local ES256 verification of search entry seals with the public keys pinned in deployment configuration.
/// It has no dependency on AuthorityDb or any provider: a writer of AuthorityDb cannot substitute a key (architecture test
/// <c>SearchSealVerifierReadsNoKeyFromTheDatabase</c>). Construction fails closed on a malformed or non-P-256 pin.
/// </summary>
public sealed class SearchSealVerifier : IDisposable
{
    private readonly Dictionary<(string KeyName, int KeyVersion), (ECDsa Key, byte[] Spki)> keys = [];

    public SearchSealVerifier(SearchSealOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var pin in options.PinnedKeys)
        {
            if (string.IsNullOrWhiteSpace(pin.KeyName) || pin.KeyVersion < 1)
            {
                throw new InvalidOperationException("SearchIndex:Seal:PinnedKeys entries need a key name and a version >= 1.");
            }

            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(pin.SubjectPublicKeyInfoBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"Pinned seal key {pin.KeyName} v{pin.KeyVersion} is not base64.", ex);
            }

            // Normalized DER (import then export), so the comparison with the provider's key does not depend on encoding details.
            var key = SearchSealSignatureV1.ImportPinnedPublicKey(spki);
            if (!keys.TryAdd((pin.KeyName, pin.KeyVersion), (key, key.ExportSubjectPublicKeyInfo())))
            {
                key.Dispose();
                throw new InvalidOperationException($"Pinned seal key {pin.KeyName} v{pin.KeyVersion} is listed twice.");
            }
        }
    }

    public IReadOnlyCollection<(string KeyName, int KeyVersion)> PinnedVersions => keys.Keys;

    public bool TryGetPinnedPublicKey(string keyName, int keyVersion, out byte[] subjectPublicKeyInfo)
    {
        if (keys.TryGetValue((keyName, keyVersion), out var entry))
        {
            subjectPublicKeyInfo = (byte[])entry.Spki.Clone();
            return true;
        }

        subjectPublicKeyInfo = [];
        return false;
    }

    public SealVerificationOutcome Verify(string keyName, int keyVersion, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (!keys.TryGetValue((keyName, keyVersion), out var entry))
        {
            return SealVerificationOutcome.UnknownKey;
        }

        return SearchSealSignatureV1.Verify(entry.Key, message, signature) ? SealVerificationOutcome.Valid : SealVerificationOutcome.Invalid;
    }

    public void Dispose()
    {
        foreach (var (key, _) in keys.Values)
        {
            key.Dispose();
        }

        keys.Clear();
    }
}

/// <summary>
/// IX-1: the only path to <see cref="ISearchSealSigner"/>. Before the first signature (and again until it succeeds) it fetches
/// the provider's public key for the signing version and requires it to equal the pinned key; otherwise signing is refused
/// and the commit that needed it fails closed (<c>SEAL_KEY_PIN_MISMATCH</c> / <c>SEAL_KEY_NOT_PINNED</c>). After signing it
/// verifies the signature locally against the pin, so a provider that signs with another key is detected at once.
/// </summary>
public sealed class SearchSealSigningGate(ISearchSealSigner signer, SearchSealVerifier verifier) : IDisposable
{
    private readonly SemaphoreSlim pinCheck = new(1, 1);
    private volatile bool pinVerified;

    public string KeyName => signer.SigningKeyName;

    public int KeyVersion => signer.SigningKeyVersion;

    public async Task<SearchSealSignature> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await EnsurePinnedAsync(cancellationToken).ConfigureAwait(false);
        var signature = await signer.SignAsync(message, cancellationToken).ConfigureAwait(false);
        if (signature.KeyName != signer.SigningKeyName || signature.KeyVersion != signer.SigningKeyVersion
            || verifier.Verify(signature.KeyName, signature.KeyVersion, message.Span, signature.Signature) != SealVerificationOutcome.Valid)
        {
            throw new SearchSealUnavailableException("SEAL_SIGNATURE_NOT_VERIFIABLE_WITH_PIN");
        }

        return signature;
    }

    public void Dispose() => pinCheck.Dispose();

    public async Task EnsurePinnedAsync(CancellationToken cancellationToken)
    {
        if (pinVerified)
        {
            return;
        }

        await pinCheck.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pinVerified)
            {
                return;
            }

            if (!verifier.TryGetPinnedPublicKey(signer.SigningKeyName, signer.SigningKeyVersion, out var pinned))
            {
                throw new SearchSealUnavailableException("SEAL_KEY_NOT_PINNED");
            }

            var provided = await signer.GetPublicKeyAsync(signer.SigningKeyName, signer.SigningKeyVersion, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(provided, pinned))
            {
                throw new SearchSealUnavailableException("SEAL_KEY_PIN_MISMATCH");
            }

            pinVerified = true;
        }
        finally
        {
            pinCheck.Release();
        }
    }
}
