using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Domain.Features.Lineage;

namespace AuthorizationAuthority.Application.Features.Lineage;

/// <summary>
/// C10 M1-WP10: generates keyed pseudonymous references for actors in the lineage classification report.
/// Uses domain separation and organization binding to ensure:
/// - The same actor has different pseudonyms in different organizations
/// - The same actor has different pseudonyms for different domains (grantor vs recipient)
/// - Pseudonyms are deterministic within a report run but cannot be correlated across runs or organizations
/// - No PII (names, emails) appears in output
/// </summary>
public sealed class LineagePseudonymizer
{
    private readonly byte[] _keyMaterial;
    private readonly Guid _organizationId;
    private const string GrantorDomain = "lineage-report:grantor";
    private const string RecipientDomain = "lineage-report:recipient";

    /// <summary>
    /// Creates a pseudonymizer bound to a specific organization and report key material.
    /// </summary>
    /// <param name="organizationId">The organization ID for domain separation.</param>
    /// <param name="keyMaterial">Random key material for this report run. Must be at least 32 bytes.</param>
    /// <exception cref="ArgumentException">Thrown when key material is too short.</exception>
    public LineagePseudonymizer(Guid organizationId, byte[] keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(keyMaterial);
        if (keyMaterial.Length < 32)
        {
            throw new ArgumentException("Key material must be at least 32 bytes.", nameof(keyMaterial));
        }

        _organizationId = organizationId;
        _keyMaterial = keyMaterial;
    }

    /// <summary>
    /// Creates a pseudonymizer with fresh random key material for a single report run.
    /// </summary>
    /// <param name="organizationId">The organization ID for domain separation.</param>
    public static LineagePseudonymizer CreateWithRandomKey(Guid organizationId)
    {
        var keyMaterial = RandomNumberGenerator.GetBytes(32);
        return new LineagePseudonymizer(organizationId, keyMaterial);
    }

    /// <summary>
    /// Generates a pseudonymous reference for a grantor (the user who created the share).
    /// </summary>
    /// <param name="grantorUserId">The authority user ID of the grantor.</param>
    /// <returns>A keyed pseudonymous reference that cannot be reversed to the original user ID.</returns>
    public string PseudonymizeGrantor(Guid grantorUserId)
    {
        return ComputePseudonym(GrantorDomain, grantorUserId);
    }

    /// <summary>
    /// Generates a pseudonymous reference for a recipient (user or team receiving the share).
    /// </summary>
    /// <param name="recipientId">The authority user ID or team ID of the recipient.</param>
    /// <returns>A keyed pseudonymous reference that cannot be reversed to the original ID.</returns>
    public string PseudonymizeRecipient(Guid recipientId)
    {
        return ComputePseudonym(RecipientDomain, recipientId);
    }

    private string ComputePseudonym(string domain, Guid entityId)
    {
        // Domain-separated HMAC-SHA256 keyed by: key_material || organization_id || domain || entity_id
        // This ensures:
        // - Different organizations produce different pseudonyms for the same actor
        // - Different domains (grantor vs recipient) produce different pseudonyms
        // - Different report runs (different key material) produce different pseudonyms
        var inputBytes = new List<byte>();
        inputBytes.AddRange(_organizationId.ToByteArray());
        inputBytes.AddRange(Encoding.UTF8.GetBytes(domain));
        inputBytes.AddRange(entityId.ToByteArray());

        var hmac = HMACSHA256.HashData(_keyMaterial, inputBytes.ToArray());

        // Return first 16 bytes as a hex string (32 characters) - sufficient entropy, still opaque
        return Convert.ToHexString(hmac.AsSpan(0, 16)).ToLowerInvariant();
    }
}

/// <summary>
/// C10 M1-WP10: validates that the classification report contains no PII.
/// </summary>
public static class LineageReportPiiValidator
{
    /// <summary>
    /// Validates that a classification entry contains no PII (names, emails, raw actor IDs in display text).
    /// </summary>
    /// <param name="entry">The entry to validate.</param>
    /// <returns>True if the entry passes validation; false otherwise.</returns>
    public static bool ValidateNoPii(LegacyShareClassificationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Pseudonymous references must be hex strings (32 chars, lowercase hex only)
        if (!IsValidPseudonym(entry.GrantorPseudonymousReference))
        {
            return false;
        }

        if (!IsValidPseudonym(entry.RecipientPseudonymousReference))
        {
            return false;
        }

        // Share role must be one of the known roles
        var validRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "reader", "contributor", "manager" };
        if (!validRoles.Contains(entry.ShareRole))
        {
            return false;
        }

        return true;
    }

    private static bool IsValidPseudonym(string pseudonym)
    {
        if (string.IsNullOrEmpty(pseudonym))
        {
            return false;
        }

        // Must be exactly 32 lowercase hex characters
        if (pseudonym.Length != 32)
        {
            return false;
        }

        return pseudonym.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
}
