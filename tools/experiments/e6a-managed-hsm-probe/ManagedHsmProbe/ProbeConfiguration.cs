using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

internal enum ProbeMode
{
    /// <summary>Default. Self-checks and vectors only; no transport, credential or network call is ever constructed.</summary>
    Local = 0,

    /// <summary>A future, separately authorized run against a throwaway non-production Managed HSM.</summary>
    ManagedHsm = 1,
}

internal enum ProbeCredentialKind
{
    /// <summary>The operator's own <c>az login</c> session against the test subscription.</summary>
    AzureCli = 1,

    /// <summary>A managed identity (system-assigned, or user-assigned by client id) of a test compute resource.</summary>
    ManagedIdentity = 2,
}

internal enum KeyProvenance
{
    /// <summary>The configured versions hold the published test keys, imported by the operator. Byte-for-byte comparison is possible.</summary>
    ImportedKnownTestKeys = 1,

    /// <summary>The HSM generated the key material. Byte-for-byte compatibility can never be concluded; such checks are Inconclusive.</summary>
    HsmGenerated = 2,
}

internal enum ProbeExperiment
{
    KeyInspection = 1,
    DigestPath = 2,
    RawInput = 3,
    Versions = 4,
}

/// <summary>
/// Test-only probe configuration (JSON). It holds no secret: authentication is the explicitly selected Azure Identity
/// credential. Unknown properties are rejected so a typo cannot silently change behaviour.
/// </summary>
internal sealed partial class ProbeConfiguration
{
    public const string ManagedHsmTokenScope = "https://managedhsm.azure.net/.default";

    public static readonly IReadOnlyList<string> SupportedApiVersions = ["7.6", "2025-07-01"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public ProbeMode Mode { get; set; } = ProbeMode.Local;

    /// <summary>Second explicit opt-in (with the <c>--allow-azure-calls</c> command-line flag) before any Azure call.</summary>
    public bool AllowAzureCalls { get; set; }

    public string? HsmEndpoint { get; set; }

    public string? KeyName { get; set; }

    public string ApiVersion { get; set; } = "2025-07-01";

    public ProbeCredentialKind? Credential { get; set; }

    public string? ManagedIdentityClientId { get; set; }

    public KeyProvenance? KeyProvenance { get; set; }

    /// <summary>Static TEST-ONLY mapping of an EnterpriseForms-style integer version to the opaque Managed HSM version identifier.</summary>
    public Dictionary<string, string> KeyVersionIdentifiers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>For <see cref="ManagedHsmProbe.KeyProvenance.ImportedKnownTestKeys"/>: which published test key each integer version holds.</summary>
    public Dictionary<string, string> KnownTestKeyByVersion { get; set; } = new(StringComparer.Ordinal);

    public int PrimaryKeyVersion { get; set; } = 1;

    public int OperationTimeoutSeconds { get; set; } = 15;

    /// <summary>Required for the separate <c>hsm-import-test-keys</c> command (an undocumented-behaviour experiment).</summary>
    public bool AllowTestKeyImport { get; set; }

    public List<ProbeExperiment> Experiments { get; set; } = [ProbeExperiment.KeyInspection, ProbeExperiment.DigestPath, ProbeExperiment.RawInput, ProbeExperiment.Versions];

    public static ProbeConfiguration Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ProbeConfiguration>(json, JsonOptions) ?? throw new ProbeConfigurationException(["the configuration file is empty"]);
        }
        catch (JsonException ex)
        {
            throw new ProbeConfigurationException([$"the configuration file is not valid probe JSON: {ex.Message}"]);
        }
    }

    public Uri EndpointUri => new(HsmEndpoint!.EndsWith('/') ? HsmEndpoint : HsmEndpoint + "/", UriKind.Absolute);

    /// <summary>Integer versions in ascending order with their opaque identifiers.</summary>
    public IReadOnlyList<(int Version, string Identifier)> MappedVersions =>
        [.. KeyVersionIdentifiers.Select(p => (int.Parse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture), p.Value)).OrderBy(p => p.Item1)];

    /// <summary>Resolves an integer version through the static mapping; an unmapped version is refused before any call (never "latest").</summary>
    public string ResolveVersionIdentifier(int version)
    {
        var text = version.ToString(CultureInfo.InvariantCulture);
        return version > 0 && KeyVersionIdentifiers.TryGetValue(text, out var identifier) && VersionIdentifierPattern().IsMatch(identifier)
            ? identifier
            : throw new ProbeConfigurationException([$"key version {text} has no configured Managed HSM version identifier; the probe never falls back to the latest version"]);
    }

    public string? KnownTestKeyFor(int version) =>
        KeyProvenance == ManagedHsmProbe.KeyProvenance.ImportedKnownTestKeys && KnownTestKeyByVersion.TryGetValue(version.ToString(CultureInfo.InvariantCulture), out var id) ? id : null;

    /// <summary>Validation for an HSM command. Throws with every problem found; nothing is contacted when this fails.</summary>
    public void ValidateForManagedHsm(bool forImport)
    {
        var errors = new List<string>();
        if (Mode != ProbeMode.ManagedHsm)
        {
            errors.Add("Mode must be ManagedHsm for an HSM command (the default is Local)");
        }

        if (!AllowAzureCalls)
        {
            errors.Add("AllowAzureCalls must be true for an HSM command");
        }

        errors.AddRange(ValidateEndpoint(HsmEndpoint));
        if (KeyName is null || !KeyNamePattern().IsMatch(KeyName))
        {
            errors.Add("KeyName must match ^e6a-probe-[a-z0-9-]{1,40}$ (test keys only; EnterpriseForms org-<uuid>-<purpose> names are refused)");
        }

        if (!SupportedApiVersions.Contains(ApiVersion, StringComparer.Ordinal))
        {
            errors.Add($"ApiVersion must be one of {string.Join(", ", SupportedApiVersions)}");
        }

        if (Credential is null)
        {
            errors.Add("Credential must be set explicitly (AzureCli or ManagedIdentity); there is no default or chained credential");
        }

        if (ManagedIdentityClientId is { Length: > 0 } clientId && (Credential != ProbeCredentialKind.ManagedIdentity || !Guid.TryParse(clientId, out _)))
        {
            errors.Add("ManagedIdentityClientId is allowed only with Credential=ManagedIdentity and must be a GUID");
        }

        if (OperationTimeoutSeconds is < 1 or > 60)
        {
            errors.Add("OperationTimeoutSeconds must be between 1 and 60");
        }

        if (KeyProvenance is null)
        {
            errors.Add("KeyProvenance must be set explicitly (ImportedKnownTestKeys or HsmGenerated)");
        }

        ValidateVersionTables(errors, forImport);
        if (forImport)
        {
            if (!AllowTestKeyImport)
            {
                errors.Add("AllowTestKeyImport must be true for hsm-import-test-keys");
            }

            if (KeyProvenance != ManagedHsmProbe.KeyProvenance.ImportedKnownTestKeys)
            {
                errors.Add("hsm-import-test-keys requires KeyProvenance=ImportedKnownTestKeys");
            }
        }
        else
        {
            if (Experiments.Count == 0 || Experiments.Distinct().Count() != Experiments.Count)
            {
                errors.Add("Experiments must list each experiment at most once and at least one");
            }

            if (!KeyVersionIdentifiers.ContainsKey(PrimaryKeyVersion.ToString(CultureInfo.InvariantCulture)))
            {
                errors.Add("PrimaryKeyVersion must be one of the mapped KeyVersionIdentifiers");
            }

            if (Experiments.Contains(ProbeExperiment.Versions) && KeyVersionIdentifiers.Count < 2)
            {
                errors.Add("the Versions experiment needs at least two mapped key versions");
            }
        }

        if (errors.Count > 0)
        {
            throw new ProbeConfigurationException(errors);
        }
    }

    /// <summary>HTTPS Managed HSM data-plane endpoint only: https://&lt;name&gt;.managedhsm.azure.net/ (an ordinary Key Vault is refused).</summary>
    public static IEnumerable<string> ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            yield return "HsmEndpoint must be an absolute URL";
            yield break;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            yield return "HsmEndpoint must use HTTPS";
        }

        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
        {
            yield return "HsmEndpoint must be the bare endpoint (no port, credentials, path, query or fragment)";
        }

        if (!HsmHostPattern().IsMatch(uri.Host))
        {
            yield return "HsmEndpoint host must be <name>.managedhsm.azure.net (ordinary Key Vault and other hosts are refused)";
        }
    }

    private void ValidateVersionTables(List<string> errors, bool forImport)
    {
        foreach (var (version, identifier) in KeyVersionIdentifiers)
        {
            if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 || number.ToString(CultureInfo.InvariantCulture) != version)
            {
                errors.Add($"KeyVersionIdentifiers key '{version}' must be a positive integer");
            }

            if (!VersionIdentifierPattern().IsMatch(identifier))
            {
                errors.Add($"KeyVersionIdentifiers['{version}'] must be a non-empty alphanumeric Managed HSM version identifier (at most 64 characters)");
            }
        }

        if (KeyVersionIdentifiers.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != KeyVersionIdentifiers.Count)
        {
            errors.Add("KeyVersionIdentifiers must map each integer version to a different identifier");
        }

        if (!forImport && KeyVersionIdentifiers.Count == 0)
        {
            errors.Add("KeyVersionIdentifiers must map at least one integer version");
        }

        if (KeyProvenance == ManagedHsmProbe.KeyProvenance.ImportedKnownTestKeys)
        {
            foreach (var (version, keyId) in KnownTestKeyByVersion)
            {
                if (!TestKeys.KeyHexById.ContainsKey(keyId))
                {
                    errors.Add($"KnownTestKeyByVersion['{version}'] names unknown test key '{keyId}'");
                }

                if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                {
                    errors.Add($"KnownTestKeyByVersion key '{version}' must be a positive integer");
                }
            }

            if (KnownTestKeyByVersion.Values.Distinct(StringComparer.Ordinal).Count() != KnownTestKeyByVersion.Count)
            {
                errors.Add("KnownTestKeyByVersion must give each version a different test key");
            }

            if (forImport ? KnownTestKeyByVersion.Count == 0 : KeyVersionIdentifiers.Keys.Any(v => !KnownTestKeyByVersion.ContainsKey(v)))
            {
                errors.Add(forImport ? "KnownTestKeyByVersion must name the test key for each version to import" : "KnownTestKeyByVersion must name the test key of every mapped version");
            }
        }
        else if (KnownTestKeyByVersion.Count > 0)
        {
            errors.Add("KnownTestKeyByVersion is allowed only with KeyProvenance=ImportedKnownTestKeys");
        }
    }

    [GeneratedRegex("^e6a-probe-[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyNamePattern();

    [GeneratedRegex("^[0-9A-Za-z]{1,64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex VersionIdentifierPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,22}[a-z0-9]\\.managedhsm\\.azure\\.net$", RegexOptions.CultureInvariant)]
    private static partial Regex HsmHostPattern();
}

internal sealed class ProbeConfigurationException(IReadOnlyList<string> errors) : Exception("invalid probe configuration: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
