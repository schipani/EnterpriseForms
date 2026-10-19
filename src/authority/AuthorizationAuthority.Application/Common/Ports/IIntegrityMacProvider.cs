using AuthorizationAuthority.Domain.Features.Keys;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>Reference to a managed key version; never key material (CRYPTOGRAPHIC_SIGNING_AUTHORITY.md §3).</summary>
public sealed record IntegrityKeyReference(Guid OrganizationId, string KeyPurpose, string ProviderKeyName, int KeyVersion);

public sealed record IntegrityMacResult(byte[] Mac, int KeyVersion, string Algorithm);

/// <summary>
/// Provider boundary INSIDE the authority only. Callers are the content service (after validation) and the verifier.
/// No controller, job, or client ever receives this interface; there is no sign-any-bytes endpoint.
/// </summary>
public interface IIntegrityMacProvider
{
    string ProviderName { get; }

    Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken);

    Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken);

    /// <summary>Provisions (or confirms) the provider-native key for the organization/purpose and returns its current version. Operator workflow only.</summary>
    Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken);

    Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Thrown when the signer/verifier is unavailable; every caller fails closed (SIG-07).</summary>
public sealed class IntegrityProviderUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);

public static class IntegrityKeyNaming
{
    /// <summary>Deterministic provider key name: org-&lt;compact uuid&gt;-&lt;purpose&gt;; never built from user input.</summary>
    public static string ProviderKeyName(Guid organizationId, string keyPurpose)
    {
        if (!IntegrityKeyPurposes.IsAllowed(keyPurpose))
        {
            throw new ArgumentException("Key purpose is not allow-listed.", nameof(keyPurpose));
        }

        return $"org-{organizationId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)}-{keyPurpose}";
    }
}
