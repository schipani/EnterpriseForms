using System.Collections.Concurrent;
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Keys;

namespace AuthorizationAuthority.Infrastructure.Signing;

/// <summary>
/// DEVELOPMENT/TEST ONLY (CRYPTOGRAPHIC_SIGNING_AUTHORITY.md §4). Random 256-bit keys per organization/purpose/version
/// held in process memory; they vanish on restart and are never persisted anywhere. Refused outside Development/Testing
/// by the host policy. Not a key custodian.
/// </summary>
public sealed class LocalHmacProvider : IIntegrityMacProvider
{
    private readonly ConcurrentDictionary<string, byte[]> keys = new(StringComparer.Ordinal);

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

        keys.GetOrAdd(providerKeyName + ":1", _ => RandomNumberGenerator.GetBytes(32));
        return Task.FromResult(1);
    }

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "LocalDevelopment provider (TEST ONLY; not a key custodian)"));

    private byte[]? ResolveKey(IntegrityKeyReference reference)
    {
        if (reference.OrganizationId == Guid.Empty || !IntegrityKeyPurposes.IsAllowed(reference.KeyPurpose) || reference.KeyVersion <= 0)
        {
            return null;
        }

        return keys.TryGetValue(reference.ProviderKeyName + ":" + reference.KeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), out var key) ? key : null;
    }
}
