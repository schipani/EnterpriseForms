using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C-191 (final resolution): an <c>Abandoned</c> resolution of a pending form version publication is accepted only with an attestation
/// from the business publication coordinator, never on design permission alone.
/// <para>
/// The coordinator (business recovery, holding the form's publication lock, so no publish of the form is between its prepare and its
/// commit) attests that the staged business version is absent from the business store and names the highest version number the store
/// holds. It signs that statement with a dedicated deployment key (<see cref="ConfigurationKey"/>) that is distinct from every workload,
/// operator and user-assertion credential, so a caller holding only the business workload credential and a designer's assertion cannot
/// produce it. The attestation is bound to the Authority's own publication identifier (so it cannot be replayed onto a later prepare of
/// the same business version), to the staged version id, number and digest, and to a short issue window.
/// </para>
/// <para>
/// The Authority refuses the abandonment (<c>FORM_PUBLICATION_ABANDONMENT_UNVERIFIED</c>; the publication stays pending and the form
/// stays unsearchable) when the attestation is absent, unverifiable, stale, bound to another publication, or when the coordinator's own
/// statement does not establish non-commitment (a business version numbered at or above the staged one). Without a configured key no
/// abandonment is ever accepted. The attestation decides only that the staged version was never committed; it confers no permission:
/// design permission on the form is still checked by the Authority, which also keeps the staged manifest unchanged.
/// </para>
/// <para>
/// Trust boundary (recorded in C9 status §11.8): the key is held by the business API, so a fully compromised business runtime can still
/// produce an attestation, as it can already declare any manifest (OD-3). The mechanism removes the ability of any other holder of the
/// business workload credential, or of a design-permission user, to lift the barrier directly.
/// </para>
/// </summary>
public sealed class FormPublicationAbandonmentVerifier
{
    /// <summary>Base64 of a random key of at least 256 bits, from deployment secret configuration. Never committed except the synthetic local value.</summary>
    public const string ConfigurationKey = "FormPublication:AbandonmentAttestationKeyBase64";

    /// <summary>Domain separator of the attested message (the first line).</summary>
    public const string MessageDomain = "enterpriseforms/form-publication-abandonment/v1";

    /// <summary>The <c>Identity:*</c> credential sections whose keys the attestation key must differ from (checked at startup).</summary>
    public static readonly IReadOnlyList<string> OtherCredentialSections = ["Workload", "Operator", "UserAssertion", "Verifier", "Reconciler", "SearchIndexBuilder", "PlatformConsole", "OperatorAssertion"];

    /// <summary>How far the attestation's issue time may be from the Authority's clock.</summary>
    public static readonly TimeSpan IssueWindow = TimeSpan.FromMinutes(5);

    private readonly byte[]? key;

    private FormPublicationAbandonmentVerifier(byte[]? key) => this.key = key;

    /// <summary>No key configured: every abandonment is refused (fail closed; the publication stays pending).</summary>
    public static FormPublicationAbandonmentVerifier Unconfigured { get; } = new(null);

    public bool IsConfigured => key is not null;

    /// <summary>
    /// Builds the verifier from deployment configuration. Absent ⇒ <see cref="Unconfigured"/>. Present but not base64, shorter than 256
    /// bits, or equal to another configured credential's key ⇒ startup failure.
    /// </summary>
    public static FormPublicationAbandonmentVerifier Create(string? keyBase64, IEnumerable<string?> otherCredentialKeysBase64)
    {
        ArgumentNullException.ThrowIfNull(otherCredentialKeysBase64);
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            return Unconfigured;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{ConfigurationKey} must be base64. Refusing to start.");
        }

        if (bytes.Length < 32)
        {
            throw new InvalidOperationException($"{ConfigurationKey} must be at least 256 bits. Refusing to start.");
        }

        if (otherCredentialKeysBase64.Any(other => !string.IsNullOrWhiteSpace(other) && string.Equals(other, keyBase64, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"{ConfigurationKey} must be distinct from every workload, operator and user-assertion credential key. Refusing to start.");
        }

        return new FormPublicationAbandonmentVerifier(bytes);
    }

    /// <summary>The exact bytes the coordinator signs (UTF-8, LF-separated, lowercase canonical forms). Mirrored by the business attestor.</summary>
    public static byte[] Message(Guid organizationId, Guid formDefinitionId, Guid formVersionPublicationId, Guid businessFormVersionId, int formVersionNumber, string layoutDigestHex, int businessLatestFormVersionNumber, long issuedAtUnixSeconds) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            MessageDomain,
            "organization_id=" + organizationId.ToString("D"),
            "form_definition_id=" + formDefinitionId.ToString("D"),
            "form_version_publication_id=" + formVersionPublicationId.ToString("D"),
            "form_version_id=" + businessFormVersionId.ToString("D"),
            "form_version_number=" + formVersionNumber.ToString(CultureInfo.InvariantCulture),
            "layout_digest_hex=" + layoutDigestHex.ToLowerInvariant(),
            "business_latest_form_version_number=" + businessLatestFormVersionNumber.ToString(CultureInfo.InvariantCulture),
            "issued_at_unix_seconds=" + issuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Refuses the abandonment of <paramref name="pending"/> unless <paramref name="attestation"/> establishes that the business never
    /// committed it. Never discloses which check failed beyond the reason text recorded on the refused command.
    /// </summary>
    public void RequireNonCommitment(Guid organizationId, FormVersionPublication pending, FormPublicationAbandonmentAttestation? attestation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (key is null)
        {
            throw Unverified("no publication coordinator attestation key is configured, so an abandonment cannot be verified");
        }

        if (attestation is null || attestation.AttestationMacHex is not { Length: 64 })
        {
            throw Unverified("an abandonment needs the publication coordinator's attestation that the version was never committed");
        }

        if (attestation.FormVersionPublicationId != pending.FormVersionPublicationId)
        {
            throw Unverified("the attestation names another publication");
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(attestation.IssuedAtUnixSeconds);
        if ((now - issued).Duration() > IssueWindow)
        {
            throw Unverified("the attestation is outside its issue window");
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(attestation.AttestationMacHex);
        }
        catch (FormatException)
        {
            throw Unverified("the attestation is not valid");
        }

        var expected = HMACSHA256.HashData(key, Message(organizationId, pending.FormDefinitionId, pending.FormVersionPublicationId, pending.BusinessFormVersionId,
            pending.FormVersionNumber, Convert.ToHexStringLower(pending.LayoutDigest), attestation.BusinessLatestFormVersionNumber, attestation.IssuedAtUnixSeconds));
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            throw Unverified("the attestation is not valid");
        }

        if (attestation.BusinessLatestFormVersionNumber < 0 || attestation.BusinessLatestFormVersionNumber >= pending.FormVersionNumber)
        {
            // The coordinator's own statement shows a business version numbered at or above the staged one: non-commitment is not established.
            throw Unverified("the attested business versions do not establish that this version was never committed");
        }
    }

    private static AuthorityException Unverified(string message) => new(AuthorityErrorCodes.FormPublicationAbandonmentUnverified, 403, false, message);
}
