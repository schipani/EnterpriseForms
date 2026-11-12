using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Infrastructure.Features.Documents;

public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>The configuration value that selects <see cref="LocalDevelopmentKeyProvider"/> (Development/Testing only, E0-1).</summary>
    public const string LocalDevelopmentProviderName = "LocalDevelopment";

    /// <summary>The configuration value that selects <see cref="VaultTransitKeyProvider"/> (Development/Testing only, E-2).</summary>
    public const string VaultTransitProviderName = VaultTransitKeyProvider.ProviderNameValue;

    /// <summary>"LocalDevelopment" (local keyring), "VaultTransit" (local Docker Vault Transit) — both Development/Testing only — or "Unconfigured" (fail closed). Azure Key Vault/Managed HSM providers are not implemented.</summary>
    public string KeyProvider { get; set; } = "Unconfigured";

    /// <summary>E-2: settings of the Vault Transit provider (used only when <see cref="KeyProvider"/> is "VaultTransit").</summary>
    public VaultTransitEncryptionOptions VaultTransit { get; set; } = new();

    /// <summary>
    /// The legacy development KEK (32 bytes, base64). It opens data wrapped before E-1 under <see cref="LocalDevelopmentKeyReference"/>
    /// and, unless <see cref="LocalDevelopmentKeyringOptions.RootKeyBase64"/> is set, is the input from which the keyring's root is extracted.
    /// </summary>
    public string? LocalDevelopmentKekBase64 { get; set; }

    /// <summary>The single legacy key reference (pre-E-1 data). Decryption only: new data is never wrapped under it.</summary>
    public string LocalDevelopmentKeyReference { get; set; } = "local-dev-kek-v1";

    /// <summary>Whether the legacy reference may still be unwrapped. Turn off once no legacy-wrapped data remains.</summary>
    public bool LocalDevelopmentLegacyKeyDecryptionEnabled { get; set; } = true;

    public LocalDevelopmentKeyringOptions LocalDevelopmentKeyring { get; set; } = new();
}

/// <summary>E1-6: the local development keyring — organization- and purpose-scoped, versioned key-encryption keys.</summary>
public sealed class LocalDevelopmentKeyringOptions
{
    /// <summary>Optional separate keyring root (32 bytes, base64). Absent: the root is extracted from the legacy KEK (domain-separated).</summary>
    public string? RootKeyBase64 { get; set; }

    /// <summary>Key versions per purpose name (FieldValue, Document, AuditorReasonNote, IdempotencyProtection), for every organization. Absent: current version 1 only.</summary>
    public Dictionary<string, LocalKeyVersionOptions> Purposes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Optional per-organization tables (organization id → purpose name → versions), replacing the purpose table for that organization.</summary>
    public Dictionary<string, Dictionary<string, LocalKeyVersionOptions>> Organizations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One scope's key-version table (current, retained and disabled versions), shared by every key provider (E-1, E-2).</summary>
public class KeyVersionOptions
{
    /// <summary>The version new data is wrapped under.</summary>
    public int CurrentVersion { get; set; } = 1;

    /// <summary>Earlier versions that remain usable for unwrapping only.</summary>
    public List<int> RetainedVersions { get; set; } = [];

    /// <summary>Versions that must not be used at all (unwrapping them fails closed).</summary>
    public List<int> DisabledVersions { get; set; } = [];
}

/// <summary>
/// The local keyring's version table (E1-6): the shape of <see cref="KeyVersionOptions"/> plus, since E-4, the explicit set of
/// provisioned versions. A local key version is derived from the keyring root, so nothing outside this configuration says that a
/// version "exists"; <see cref="ProvisionedVersions"/> is that statement, made separately from activation, as a Vault key version
/// exists in Vault before configuration makes it current.
/// </summary>
public sealed class LocalKeyVersionOptions : KeyVersionOptions
{
    /// <summary>
    /// E4-5: the versions provisioned for this scope (absent: version 1 only). Provisioning a version makes it available but never
    /// current: <see cref="KeyVersionOptions.CurrentVersion"/> and every <see cref="KeyVersionOptions.RetainedVersions"/> entry must be
    /// provisioned, or the keyring is invalid and every operation fails closed (and the API and Worker refuse to start).
    /// </summary>
    public List<int>? ProvisionedVersions { get; set; }
}

/// <summary>
/// E-1 Development/Testing key provider (refused outside Development/Testing at startup, E0-1). Two kinds of reference:
/// <list type="bullet">
/// <item><b>Scoped</b> (all new data): <c>localdev:{organization}:{purpose token}:{version}</c>, canonical and unique per key
/// version. Its KEK is HKDF-SHA256-Expand(root, "DynamicForms/LocalKeyring/Kek/v1" ‖ organization ‖ purpose ‖ version), so
/// keys of different organizations, purposes and versions are distinct keys; the wrap is AES-256-GCM with the AAD
/// "DynamicForms/LocalKeyring/DekWrap/v1" ‖ organization ‖ purpose ‖ version, so a wrapped key also authenticates its scope.
/// The wrapped-key layout is unchanged: nonce(12) ‖ ciphertext ‖ tag(16).</item>
/// <item><b>Legacy</b> (<c>local-dev-kek-v1</c>, pre-E-1 data, all purposes): resolves only to the configured legacy KEK with the
/// original AAD "DynamicForms/DekWrap/v1:" + reference; unwrap only.</item>
/// </list>
/// Only configured versions resolve; unknown and disabled versions, foreign organizations and purposes fail closed, and no
/// key is ever generated on demand. Key material never appears in exceptions or logs.
/// </summary>
public sealed class LocalDevelopmentKeyProvider : IEncryptionKeyProvider
{
    public const string ScopedReferencePrefix = "localdev";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string LegacyWrapAadPrefix = "DynamicForms/DekWrap/v1:";
    private static readonly byte[] RootSalt = Encoding.ASCII.GetBytes("DynamicForms/LocalKeyring/Root/v1");
    private static readonly byte[] DerivationDomain = Encoding.ASCII.GetBytes("DynamicForms/LocalKeyring/Kek/v1");
    private static readonly byte[] WrapDomain = Encoding.ASCII.GetBytes("DynamicForms/LocalKeyring/DekWrap/v1");

    private readonly byte[]? legacyKek;
    private readonly string legacyReference;
    private readonly bool legacyDecryptionEnabled;
    private readonly byte[]? rootPrk;
    private readonly KeyVersionTables tables = new();
    private readonly string? configurationError;

    public LocalDevelopmentKeyProvider(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        legacyReference = value.LocalDevelopmentKeyReference ?? string.Empty;
        legacyDecryptionEnabled = value.LocalDevelopmentLegacyKeyDecryptionEnabled;
        try
        {
            legacyKek = DecodeKey(value.LocalDevelopmentKekBase64, "the legacy development KEK");
            var root = DecodeKey(value.LocalDevelopmentKeyring?.RootKeyBase64, "the keyring root key") ?? legacyKek;
            rootPrk = root is null ? null : HKDF.Extract(HashAlgorithmName.SHA256, root, RootSalt);
            if (legacyReference.Length == 0 || legacyReference.StartsWith(ScopedReferencePrefix + ":", StringComparison.Ordinal))
            {
                throw new KeyConfigurationException("the legacy key reference must be set and must not use the scoped reference form");
            }

            LoadKeyring(value.LocalDevelopmentKeyring ?? new LocalDevelopmentKeyringOptions(), tables);
        }
        catch (KeyConfigurationException ex)
        {
            configurationError = ex.Message;
            rootPrk = null;
        }
    }

    public string ProviderName => EncryptionOptions.LocalDevelopmentProviderName;

    public bool IsConfigured => configurationError is null && rootPrk is not null;

    /// <summary>The canonical scoped reference of one key version.</summary>
    public static string ScopedReference(DataKeyScope scope, int version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return string.Join(':', ScopedReferencePrefix, scope.OrganizationId.ToString("D"), scope.Purpose.Token, version.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The current key version of a scope (diagnostics and tests; never key material).</summary>
    public int CurrentVersion(DataKeyScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConfigured();
        return TableOf(scope).Current;
    }

    public Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken) =>
        WrapDataKeyAsync(scope, dataKey, RandomNumberGenerator.GetBytes(NonceSize), cancellationToken);

    /// <summary>Deterministic core (the public overload supplies a random wrap nonce; tests reach this one for the known-answer vector).</summary>
    internal Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, byte[] wrapNonce, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(wrapNonce);
        cancellationToken.ThrowIfCancellationRequested();
        RequireConfigured();
        if (dataKey.Length != KeySize || wrapNonce.Length != NonceSize)
        {
            throw new ArgumentException("a 256-bit data key and a 96-bit wrap nonce are required");
        }

        var version = TableOf(scope).Current;
        var kek = DeriveKek(scope, version);
        try
        {
            var cipher = new byte[dataKey.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(kek, TagSize))
            {
                aes.Encrypt(wrapNonce, dataKey.Span, cipher, tag, ScopedWrapAad(scope, version));
            }

            return Task.FromResult(new WrappedDataKey(ScopedReference(scope, version), [.. wrapNonce, .. cipher, .. tag]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(keyReference);
        cancellationToken.ThrowIfCancellationRequested();
        if (configurationError is not null)
        {
            throw new EncryptionUnavailableException("the local development keyring configuration is invalid: " + configurationError);
        }

        if (wrappedDataKey.Length < NonceSize + TagSize + 1)
        {
            throw new CryptographicException("wrapped key too short");
        }

        if (string.Equals(keyReference, legacyReference, StringComparison.Ordinal))
        {
            // E1-5: pre-E-1 data. The legacy wrap carries no organization or purpose; the payload AAD (tenant, record, item)
            // and the row HMAC keep such data bound to its owner. Resolves only to the configured legacy KEK, never to a scoped key.
            if (legacyKek is null || !legacyDecryptionEnabled)
            {
                throw new EncryptionUnavailableException("the legacy development key is not configured for decryption");
            }

            return Task.FromResult(Open(legacyKek, wrappedDataKey.Span, Encoding.ASCII.GetBytes(LegacyWrapAadPrefix + legacyReference)));
        }

        if (!TryParseScopedReference(keyReference, out var organization, out var purpose, out var version))
        {
            throw new EncryptionUnavailableException("unknown key reference");
        }

        if (organization != scope.OrganizationId || !ReferenceEquals(purpose, scope.Purpose))
        {
            throw new CryptographicException("the key reference belongs to another organization or purpose");
        }

        RequireConfigured();
        switch (TableOf(scope).Resolve(version))
        {
            case KeyVersionState.Disabled:
                throw new EncryptionUnavailableException("the key version is disabled");
            case KeyVersionState.Unknown:
                throw new EncryptionUnavailableException("the key version is not retained");
        }

        var kek = DeriveKek(scope, version);
        try
        {
            return Task.FromResult(Open(kek, wrappedDataKey.Span, ScopedWrapAad(scope, version)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Parses a canonical scoped reference; any other spelling (case, padding, leading zeros) is not a reference.</summary>
    public static bool TryParseScopedReference(string reference, out Guid organizationId, out EncryptionKeyPurpose purpose, out int version)
    {
        organizationId = Guid.Empty;
        purpose = null!;
        version = 0;
        var parts = (reference ?? string.Empty).Split(':');
        if (parts.Length != 4 || parts[0] != ScopedReferencePrefix
            || !Guid.TryParseExact(parts[1], "D", out organizationId) || organizationId == Guid.Empty || parts[1] != organizationId.ToString("D")
            || EncryptionKeyPurpose.All.FirstOrDefault(p => p.Token == parts[2]) is not { } parsed
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1 || parts[3] != version.ToString(CultureInfo.InvariantCulture))
        {
            organizationId = Guid.Empty;
            version = 0;
            return false;
        }

        purpose = parsed;
        return true;
    }

    internal static byte[] ScopedWrapAad(DataKeyScope scope, int version) => KeyScopeEncoding.Context(WrapDomain, scope, version);

    /// <summary>
    /// E-4: the first problem of the keyring's version configuration (tables and provisioning), or null when it is valid. Never
    /// mentions key material (the keyring options hold none). Used by the API and Worker startup rule, so that an activation of an
    /// unprovisioned version, or any other invalid table, stops the host instead of starting a provider that refuses every operation.
    /// </summary>
    public static string? KeyVersionProblem(LocalDevelopmentKeyringOptions? keyring)
    {
        try
        {
            LoadKeyring(keyring ?? new LocalDevelopmentKeyringOptions(), new KeyVersionTables());
            return null;
        }
        catch (KeyConfigurationException ex)
        {
            return ex.Message;
        }
    }

    private static void LoadKeyring(LocalDevelopmentKeyringOptions keyring, KeyVersionTables into)
    {
        var purposes = keyring.Purposes ?? [];
        var organizations = keyring.Organizations ?? [];
        into.Load(
            purposes.ToDictionary(p => p.Key, KeyVersionOptions (p) => p.Value, StringComparer.Ordinal),
            organizations.ToDictionary(o => o.Key, o => o.Value.ToDictionary(p => p.Key, KeyVersionOptions (p) => p.Value, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase),
            "keyring");

        // E4-5: a version is current or retained only once it has been provisioned (absent list: version 1 only, the E-1 default).
        foreach (var (purposeName, table) in purposes.Concat(organizations.SelectMany(o => o.Value)))
        {
            RequireProvisioned(table, purposeName);
        }
    }

    private static void RequireProvisioned(LocalKeyVersionOptions? table, string purposeName)
    {
        if (table is null)
        {
            return;
        }

        var provisioned = (table.ProvisionedVersions ?? [1]).ToHashSet();
        if (provisioned.Any(v => v < 1))
        {
            throw new KeyConfigurationException("provisioned key versions of " + purposeName + " must be positive");
        }

        if (!provisioned.Contains(table.CurrentVersion))
        {
            throw new KeyConfigurationException("the current key version of " + purposeName + " is not provisioned");
        }

        if ((table.RetainedVersions ?? []).Any(v => !provisioned.Contains(v)))
        {
            throw new KeyConfigurationException("a retained key version of " + purposeName + " is not provisioned");
        }
    }

    private static byte[] Open(byte[] kek, ReadOnlySpan<byte> wrapped, byte[] aad)
    {
        var plain = new byte[wrapped.Length - NonceSize - TagSize];
        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(wrapped[..NonceSize], wrapped[NonceSize..^TagSize], wrapped[^TagSize..], plain, aad);
        return plain;
    }

    private static byte[]? DecodeKey(string? base64, string what)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == KeySize ? bytes : throw new KeyConfigurationException(what + " is not 32 bytes");
        }
        catch (FormatException)
        {
            throw new KeyConfigurationException(what + " is not valid base64");
        }
    }

    private byte[] DeriveKek(DataKeyScope scope, int version) => HKDF.Expand(HashAlgorithmName.SHA256, rootPrk!, KeySize, KeyScopeEncoding.Context(DerivationDomain, scope, version));

    private KeyVersionTable TableOf(DataKeyScope scope) => tables.For(scope);

    private void RequireConfigured()
    {
        if (configurationError is not null)
        {
            throw new EncryptionUnavailableException("the local development keyring configuration is invalid: " + configurationError);
        }

        if (rootPrk is null)
        {
            throw new EncryptionUnavailableException("local development key material is not configured");
        }
    }
}

/// <summary>Registered when no permitted key provider is configured: wrapping and unwrapping fail closed.</summary>
public sealed class UnavailableKeyProvider(string reason) : IEncryptionKeyProvider
{
    public string ProviderName => "Unavailable";

    public bool IsConfigured => false;

    public Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken) => Task.FromException<WrappedDataKey>(new EncryptionUnavailableException(reason));

    public Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken) => Task.FromException<byte[]>(new EncryptionUnavailableException(reason));
}

/// <summary>The scope context both key providers authenticate: domain ‖ organization (16 bytes, big-endian UUID) ‖ Int32BE(token length) ‖ purpose token ‖ Int32BE(version).</summary>
internal static class KeyScopeEncoding
{
    public static byte[] Context(byte[] domain, DataKeyScope scope, int version)
    {
        var token = Encoding.ASCII.GetBytes(scope.Purpose.Token);
        var bytes = new byte[domain.Length + 16 + 4 + token.Length + 4];
        domain.CopyTo(bytes, 0);
        var offset = domain.Length;
        scope.OrganizationId.TryWriteBytes(bytes.AsSpan(offset, 16), bigEndian: true, out _);
        offset += 16;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), token.Length);
        offset += 4;
        token.CopyTo(bytes, offset);
        offset += token.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), version);
        return bytes;
    }

    /// <summary>Parses the canonical reference <c>{prefix}:{organization}:{purpose token}:{version}</c>; any other spelling (case, padding, leading zeros) is not a reference.</summary>
    public static bool TryParseReference(string? reference, string prefix, out Guid organizationId, out EncryptionKeyPurpose purpose, out int version)
    {
        organizationId = Guid.Empty;
        purpose = null!;
        version = 0;
        var parts = (reference ?? string.Empty).Split(':');
        if (parts.Length != 4 || parts[0] != prefix
            || !Guid.TryParseExact(parts[1], "D", out organizationId) || organizationId == Guid.Empty || parts[1] != organizationId.ToString("D")
            || EncryptionKeyPurpose.All.FirstOrDefault(p => p.Token == parts[2]) is not { } parsed
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1 || parts[3] != version.ToString(CultureInfo.InvariantCulture))
        {
            organizationId = Guid.Empty;
            version = 0;
            return false;
        }

        purpose = parsed;
        return true;
    }

    public static string Reference(string prefix, DataKeyScope scope, int version) =>
        string.Join(':', prefix, scope.OrganizationId.ToString("D"), scope.Purpose.Token, version.ToString(CultureInfo.InvariantCulture));
}

internal enum KeyVersionState
{
    Usable,
    Disabled,
    Unknown,
}

/// <summary>One scope's validated version table.</summary>
internal sealed record KeyVersionTable(int Current, IReadOnlySet<int> Retained, IReadOnlySet<int> Disabled)
{
    public static KeyVersionTable From(KeyVersionOptions? options, string purposeName)
    {
        var current = options?.CurrentVersion ?? 1;
        var retained = (options?.RetainedVersions ?? []).ToHashSet();
        var disabled = (options?.DisabledVersions ?? []).ToHashSet();
        if (current < 1 || retained.Any(v => v < 1) || disabled.Any(v => v < 1))
        {
            throw new KeyConfigurationException("key versions of " + purposeName + " must be positive");
        }

        if (disabled.Contains(current) || retained.Overlaps(disabled))
        {
            throw new KeyConfigurationException("a key version of " + purposeName + " is both usable and disabled");
        }

        retained.Remove(current);
        return new KeyVersionTable(current, retained, disabled);
    }

    public KeyVersionState Resolve(int version) =>
        version == Current || Retained.Contains(version) ? KeyVersionState.Usable : Disabled.Contains(version) ? KeyVersionState.Disabled : KeyVersionState.Unknown;
}

/// <summary>The version tables of every scope: one per purpose (default current version 1), optionally replaced per organization.</summary>
internal sealed class KeyVersionTables
{
    private readonly Dictionary<EncryptionKeyPurpose, KeyVersionTable> purposeTables = [];
    private readonly Dictionary<(Guid Organization, EncryptionKeyPurpose Purpose), KeyVersionTable> organizationTables = [];

    /// <summary>Validates and loads the tables; throws <see cref="KeyConfigurationException"/> (never with key material) when they are invalid.</summary>
    public void Load(IReadOnlyDictionary<string, KeyVersionOptions> purposes, IReadOnlyDictionary<string, Dictionary<string, KeyVersionOptions>> organizations, string label)
    {
        RejectUnknownPurposes(purposes.Keys, label);
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            purposeTables[purpose] = KeyVersionTable.From(purposes.GetValueOrDefault(purpose.Name), purpose.Name);
        }

        foreach (var (organizationText, tables) in organizations)
        {
            if (!Guid.TryParse(organizationText, out var organization) || organization == Guid.Empty)
            {
                throw new KeyConfigurationException("an organization key table is not keyed by an organization id");
            }

            RejectUnknownPurposes(tables.Keys, label);
            foreach (var (purposeName, table) in tables)
            {
                organizationTables[(organization, EncryptionKeyPurpose.All.Single(p => p.Name == purposeName))] = KeyVersionTable.From(table, purposeName);
            }
        }
    }

    public KeyVersionTable For(DataKeyScope scope) =>
        organizationTables.TryGetValue((scope.OrganizationId, scope.Purpose), out var table) ? table : purposeTables[scope.Purpose];

    private static void RejectUnknownPurposes(IEnumerable<string> names, string label)
    {
        if (names.FirstOrDefault(n => EncryptionKeyPurpose.All.All(p => p.Name != n)) is { } unknown)
        {
            throw new KeyConfigurationException("unknown key purpose '" + unknown + "' in the " + label + " configuration");
        }
    }
}

/// <summary>An invalid key-provider configuration (message never contains key material or credentials).</summary>
internal sealed class KeyConfigurationException(string message) : Exception(message);
