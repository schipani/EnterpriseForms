using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseForms.Application.Features.FormDesigner;

/// <summary>
/// C-191 (final resolution): the business publication coordinator's attestation that a version staged with the Authority was never
/// committed here. Only <see cref="FormDesignerHandlers.RecoverAuthorityRegistrationAsync"/> produces it, holding the form's publication
/// lock after finding the staged version absent from the business store and every stored version numbered below it; the Authority accepts
/// an <c>Abandoned</c> resolution only with a valid one (design permission alone never lifts the barrier).
/// <para>
/// The key (<see cref="ConfigurationKey"/>) is dedicated to this purpose and must differ from the workload and user-assertion credentials,
/// so a holder of those cannot produce an attestation. Without it no attestation is produced and a never-committed staged version stays
/// pending (fail closed). The message is defined by the Authority's verifier; both sides are pinned to one test vector.
/// </para>
/// </summary>
public sealed class FormPublicationAbandonmentAttestor
{
    /// <summary>Base64 of the dedicated key (at least 256 bits), from deployment secret configuration; the Authority holds the same key.</summary>
    public const string ConfigurationKey = "FormPublication:AbandonmentAttestationKeyBase64";

    public const string MessageDomain = "enterpriseforms/form-publication-abandonment/v1";

    private readonly byte[]? key;

    private FormPublicationAbandonmentAttestor(byte[]? key) => this.key = key;

    public static FormPublicationAbandonmentAttestor Unconfigured { get; } = new(null);

    public bool IsConfigured => key is not null;

    /// <summary>Absent ⇒ <see cref="Unconfigured"/>. Present but not base64, shorter than 256 bits, or equal to another credential's key ⇒ startup failure.</summary>
    public static FormPublicationAbandonmentAttestor Create(string? keyBase64, IEnumerable<string?> otherCredentialKeysBase64)
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
            throw new InvalidOperationException($"{ConfigurationKey} must be distinct from the workload and user-assertion credential keys. Refusing to start.");
        }

        return new FormPublicationAbandonmentAttestor(bytes);
    }

    /// <summary>The exact bytes signed (UTF-8, LF-separated, lowercase canonical forms); identical to the Authority verifier's message.</summary>
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

    /// <summary>The attestation as sent in the <c>ResolveFormVersionPublication</c> payload (snake_case wire names).</summary>
    public object Attest(Guid organizationId, Guid formDefinitionId, Guid formVersionPublicationId, Guid businessFormVersionId, int formVersionNumber, string layoutDigestHex, int businessLatestFormVersionNumber, DateTimeOffset now)
    {
        if (key is null)
        {
            throw new InvalidOperationException("the publication coordinator attestation key is not configured");
        }

        var issued = now.ToUnixTimeSeconds();
        var mac = HMACSHA256.HashData(key, Message(organizationId, formDefinitionId, formVersionPublicationId, businessFormVersionId, formVersionNumber, layoutDigestHex, businessLatestFormVersionNumber, issued));
        return new
        {
            form_version_publication_id = formVersionPublicationId,
            business_latest_form_version_number = businessLatestFormVersionNumber,
            issued_at_unix_seconds = issued,
            attestation_mac_hex = Convert.ToHexStringLower(mac),
        };
    }
}
