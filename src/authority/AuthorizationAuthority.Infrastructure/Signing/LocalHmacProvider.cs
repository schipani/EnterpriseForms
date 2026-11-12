using System.Collections.Concurrent;
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Keys;

namespace AuthorizationAuthority.Infrastructure.Signing;

/// <summary>
/// DEVELOPMENT/TEST ONLY (CRYPTOGRAPHIC_SIGNING_AUTHORITY.md §4). Random 256-bit keys per organization/purpose/version
/// held in process memory; they vanish on restart and are never persisted anywhere. Refused outside Development/Testing
/// by the host policy. Not a key custodian.
/// E-5: a key can gain further versions only through the explicit operator action <see cref="ProvisionKeyVersion"/>; signing and
/// verification use exactly the version they are given (never the latest), and a version that was never provisioned has no key.
/// </summary>
public sealed class LocalHmacProvider : IIntegrityMacProvider
{
    private readonly ConcurrentDictionary<string, byte[]> keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> latestVersions = new(StringComparer.Ordinal);
    private readonly object provisioning = new();

    public string ProviderName => "LocalDevelopment";

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ResolveKey(keyReference) ?? throw new IntegrityProviderUnavailableException("development key not provisioned");
        return Task.FromResult(new IntegrityMacResult(HMACSHA256.HashData(key, canonicalPayload.Span), keyReference.KeyVersion, "HMAC-SHA-256"));
    }

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedMac.Length != 32)
        {
            return Task.FromResult(false);
        }

        var key = ResolveKey(keyReference);
        if (key is null)
        {
            return Task.FromResult(false);
        }

        var actual = HMACSHA256.HashData(key, canonicalPayload.Span);
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(actual, expectedMac.Span));
    }

    public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IntegrityKeyPurposes.IsAllowed(keyPurpose))
        {
            throw new IntegrityProviderUnavailableException("key purpose not allow-listed");
        }

        lock (provisioning)
        {
            keys.GetOrAdd(providerKeyName + ":1", _ => RandomNumberGenerator.GetBytes(32));
            return Task.FromResult(latestVersions.GetOrAdd(providerKeyName, 1));
        }
    }

    /// <summary>
    /// E-5 operator action (development/test): adds exactly one new random version to an existing key and returns its number. It does
    /// not activate anything: signing keeps using whatever version the Authority's key registration names. Refuses a missing key.
    /// </summary>
    public int ProvisionKeyVersion(string providerKeyName)
    {
        ArgumentNullException.ThrowIfNull(providerKeyName);
        lock (provisioning)
        {
            if (!latestVersions.TryGetValue(providerKeyName, out var latest))
            {
                throw new IntegrityProviderUnavailableException("development key not provisioned");
            }

            var next = latest + 1;
            keys[providerKeyName + ":" + next.ToString(System.Globalization.CultureInfo.InvariantCulture)] = RandomNumberGenerator.GetBytes(32);
            latestVersions[providerKeyName] = next;
            return next;
        }
    }

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "LocalDevelopment provider (TEST ONLY; not a key custodian)"));

    public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(latestVersions.TryGetValue(providerKeyName, out var latest)
            ? new IntegrityKeyInspection(true, true, latest, "local test key")
            : new IntegrityKeyInspection(false, false, 0, "no key under this name"));
    }

    private byte[]? ResolveKey(IntegrityKeyReference reference)
    {
        if (reference.OrganizationId == Guid.Empty || !IntegrityKeyPurposes.IsAllowed(reference.KeyPurpose) || reference.KeyVersion <= 0)
        {
            return null;
        }

        return keys.TryGetValue(reference.ProviderKeyName + ":" + reference.KeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), out var key) ? key : null;
    }
}
