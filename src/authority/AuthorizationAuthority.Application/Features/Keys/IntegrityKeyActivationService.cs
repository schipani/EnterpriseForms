using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;

namespace AuthorizationAuthority.Application.Features.Keys;

/// <summary>The outcome of an activation: the registration's signing version before and after, and the audit entry that records it.</summary>
public sealed record IntegrityKeyActivationResult(
    Guid OrganizationId,
    string KeyPurposeName,
    string ProviderKeyName,
    int PreviousSigningKeyVersion,
    int CurrentSigningKeyVersion,
    int MinimumVerificationKeyVersion,
    long AuditJournalRevision,
    Guid CorrelationId);

/// <summary>
/// E-5 controlled activation of an HMAC integrity-key version. The Authority's key registration (organization + purpose) is the
/// only place that names the signing version: <see cref="Content.ContentService"/> signs new rows with
/// <c>CurrentSigningKeyVersion</c>, and every verification uses the version recorded with the signature (row header, prepared
/// operation, manifest commit, content head) above <c>MinimumVerificationKeyVersion</c>. Activation changes exactly that one
/// field of exactly one registration and nothing else: no signature, manifest, head or journal entry is rewritten.
/// <para>
/// Provisioning a version (adding it in the provider: Vault <c>.../rotate</c> by the operator, or the local provider's explicit
/// action) never activates it. Activation is refused unless: the registration exists, is not paused and belongs to the configured
/// provider; the caller's expected current version matches (compare-and-set under the organization lock); the target version is
/// not below the verification floor; the provider reports it provisioned and usable; and the Authority can sign and verify a
/// domain-separated probe with exactly that version. Nothing falls back to "latest". The change and its reason are journaled on
/// the organization's audit stream in the same transaction.
/// </para>
/// Development/Testing operator action (no endpoint, role or permission): the governed production workflow (step-up, dual control,
/// independent approval, CRYPTOGRAPHIC_SIGNING_AUTHORITY.md §5) is not implemented.
/// </summary>
public sealed partial class IntegrityKeyActivationService(IAuthorityStore store, IIntegrityMacProvider macProvider, TimeProvider clock)
{
    public const string ActivatedEventKind = "IntegrityKeyVersionActivated";

    private static readonly byte[] ProbeDomain = "DynamicForms/IntegrityKeyActivationProbe/v1\n"u8.ToArray();

    public async Task<IntegrityKeyActivationResult> ActivateAsync(
        Guid organizationId, string keyPurposeName, int expectedCurrentVersion, int signingKeyVersion, string operatorName, string reason, Guid correlationId, CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty || !IntegrityKeyPurposes.IsAllowed(keyPurposeName ?? string.Empty))
        {
            throw AuthorityException.Invalid("a registered organization and an allow-listed key purpose are required");
        }

        if (expectedCurrentVersion < 1 || signingKeyVersion < 1 || signingKeyVersion == expectedCurrentVersion)
        {
            throw AuthorityException.Invalid("the expected current version and a different positive signing version are required");
        }

        if (!OperatorPattern().IsMatch(operatorName ?? string.Empty) || string.IsNullOrWhiteSpace(reason) || reason.Length > 200 || reason.Any(char.IsControl))
        {
            throw AuthorityException.Invalid("an operator name and a reason of at most 200 printable characters are required");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var locked = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                     ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "organization not registered");
        var key = await scope.FindKeyRegistrationForUpdateAsync(organizationId, keyPurposeName!, cancellationToken).ConfigureAwait(false)
                  ?? throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "organization integrity key is not registered");

        if (key.KeyUsagePaused)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "organization integrity key usage is paused");
        }

        if (!string.Equals(key.ProviderName, macProvider.ProviderName, StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "the registration does not belong to the configured signing provider");
        }

        if (key.CurrentSigningKeyVersion != expectedCurrentVersion)
        {
            throw new AuthorityException(AuthorityErrorCodes.OperatorStateConflict, 409, false, "the current signing version is not the expected version (nothing changed)");
        }

        if (signingKeyVersion < key.MinimumVerificationKeyVersion)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "the version is below the verification floor and cannot be activated");
        }

        await RequireProvisionedAsync(organizationId, key, signingKeyVersion, cancellationToken).ConfigureAwait(false);

        var previous = key.CurrentSigningKeyVersion;
        key.CurrentSigningKeyVersion = signingKeyVersion;
        key.KeyModifiedOn = clock.GetUtcNow();
        var entry = AuditJournal.Append(scope, locked, ActivatedEventKind, new
        {
            organization_id = organizationId,
            key_purpose_name = key.KeyPurposeName,
            provider_name = key.ProviderName,
            provider_key_name = key.ProviderKeyName,
            previous_signing_key_version = previous,
            signing_key_version = signingKeyVersion,
            minimum_verification_key_version = key.MinimumVerificationKeyVersion,
            operator_workload = operatorName,
            reason,
            correlation_id = correlationId,
        }, clock);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new IntegrityKeyActivationResult(organizationId, key.KeyPurposeName, key.ProviderKeyName, previous, signingKeyVersion, key.MinimumVerificationKeyVersion, entry.JournalStreamRevision, correlationId);
    }

    /// <summary>The provider has exactly this version, it is usable, and the Authority can sign and verify with it (the provider itself rejects a malformed or foreign key reference).</summary>
    private async Task RequireProvisionedAsync(Guid organizationId, IntegrityKeyRegistration key, int version, CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await macProvider.InspectKeyAsync(key.ProviderKeyName, cancellationToken).ConfigureAwait(false);
            if (!inspection.Exists || !inspection.Usable || inspection.LatestVersion < version)
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationKeyNotProvisioned, 409, false, "the signing version is not provisioned and usable in the key provider");
            }

            var probe = Probe(organizationId, version);
            var target = new IntegrityKeyReference(organizationId, key.KeyPurposeName, key.ProviderKeyName, version);
            var mac = await macProvider.SignAsync(target, probe, cancellationToken).ConfigureAwait(false);
            if (mac.KeyVersion != version || mac.Mac.Length != 32 || !await macProvider.VerifyAsync(target, probe, mac.Mac, cancellationToken).ConfigureAwait(false))
            {
                throw new AuthorityException(AuthorityErrorCodes.OrganizationKeyNotProvisioned, 409, false, "the Authority cannot sign and verify with the signing version");
            }

        }
        catch (IntegrityProviderUnavailableException)
        {
            throw new AuthorityException(AuthorityErrorCodes.SigningUnavailable, 503, true, "the signing provider is unavailable; nothing changed");
        }
        catch (CryptographicException)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "the key reference is not accepted by the signing provider");
        }
    }

    private static byte[] Probe(Guid organizationId, int version)
    {
        var bytes = new byte[ProbeDomain.Length + 16 + 4];
        ProbeDomain.CopyTo(bytes, 0);
        organizationId.TryWriteBytes(bytes.AsSpan(ProbeDomain.Length, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(ProbeDomain.Length + 16), version);
        return bytes;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperatorPattern();
}
