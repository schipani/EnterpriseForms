namespace AuthorizationAuthority.Domain.Features.Keys;

/// <summary>Authority-only key catalog entry: provider-native key name and the approved signing version. Never key material.</summary>
public sealed class IntegrityKeyRegistration
{
    public Guid IntegrityKeyRegistrationId { get; set; }

    public Guid OrganizationId { get; set; }

    public string KeyPurposeName { get; set; } = IntegrityKeyPurposes.RowIntegrityV1;

    public string ProviderName { get; set; } = string.Empty;

    public string ProviderKeyName { get; set; } = string.Empty;

    public int CurrentSigningKeyVersion { get; set; }

    public int MinimumVerificationKeyVersion { get; set; } = 1;

    public bool KeyUsagePaused { get; set; }

    public DateTimeOffset KeyRegisteredOn { get; set; }

    public DateTimeOffset KeyModifiedOn { get; set; }
}

public static class IntegrityKeyPurposes
{
    public const string RowIntegrityV1 = "row-integrity-v1";

    public static bool IsAllowed(string purpose) => purpose is RowIntegrityV1;
}
