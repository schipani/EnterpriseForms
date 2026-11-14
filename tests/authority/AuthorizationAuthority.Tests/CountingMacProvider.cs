using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2a, test only: delegates to the real provider and counts every signature the content service asks for, so a test can
/// show that a refused candidate was refused before anything was signed.
/// </summary>
internal sealed class CountingMacProvider(IIntegrityMacProvider inner) : IIntegrityMacProvider
{
    private int signatures;

    public int SignatureCount => Volatile.Read(ref signatures);

    public string ProviderName => inner.ProviderName;

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref signatures);
        return inner.SignAsync(keyReference, canonicalPayload, cancellationToken);
    }

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken) =>
        inner.VerifyAsync(keyReference, canonicalPayload, expectedMac, cancellationToken);

    public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) =>
        inner.ProvisionKeyAsync(organizationId, keyPurpose, providerKeyName, cancellationToken);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

    public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) => inner.InspectKeyAsync(providerKeyName, cancellationToken);
}
