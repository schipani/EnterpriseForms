namespace EnterpriseForms.Experiments.ManagedHsmProbe;

/// <summary>
/// Managed HSM data-plane URLs (Key Vault keys REST API shape, served by https://&lt;name&gt;.managedhsm.azure.net). Every
/// cryptographic URL carries an explicit version identifier: a blank or invalid version is refused here, so the probe can
/// never send a request in which the service would choose a version (for Get Key, an omitted version means "latest").
/// </summary>
internal static class ManagedHsmUrls
{
    public static Uri KeyIdentifier(Uri endpoint, string keyName, string versionId) =>
        new(endpoint, $"keys/{Escape(keyName)}/{RequireVersion(versionId)}");

    public static Uri Key(Uri endpoint, string keyName, string versionId, string apiVersion) =>
        new(endpoint, $"keys/{Escape(keyName)}/{RequireVersion(versionId)}?api-version={Uri.EscapeDataString(apiVersion)}");

    public static Uri Sign(Uri endpoint, string keyName, string versionId, string apiVersion) =>
        new(endpoint, $"keys/{Escape(keyName)}/{RequireVersion(versionId)}/sign?api-version={Uri.EscapeDataString(apiVersion)}");

    public static Uri Verify(Uri endpoint, string keyName, string versionId, string apiVersion) =>
        new(endpoint, $"keys/{Escape(keyName)}/{RequireVersion(versionId)}/verify?api-version={Uri.EscapeDataString(apiVersion)}");

    /// <summary>Lists versions of one key (read-only; used to show the mapped identifiers exist and which one is latest).</summary>
    public static Uri Versions(Uri endpoint, string keyName, string apiVersion) =>
        new(endpoint, $"keys/{Escape(keyName)}/versions?api-version={Uri.EscapeDataString(apiVersion)}&maxresults=25");

    /// <summary>Import Key (PUT /keys/{name}); importing under an existing name creates a new version.</summary>
    public static Uri Import(Uri endpoint, string keyName, string apiVersion) =>
        new(endpoint, $"keys/{Escape(keyName)}?api-version={Uri.EscapeDataString(apiVersion)}");

    public static string RequireVersion(string versionId) =>
        !string.IsNullOrWhiteSpace(versionId) && ProbeConfiguration.VersionIdentifierPattern().IsMatch(versionId)
            ? versionId
            : throw new ArgumentException("an explicit Managed HSM key version identifier is required; the probe never lets the service choose the latest version", nameof(versionId));

    private static string Escape(string keyName) =>
        !string.IsNullOrEmpty(keyName) && keyName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? keyName
            : throw new ArgumentException("invalid key name", nameof(keyName));
}
