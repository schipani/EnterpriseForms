namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>IX-1: whether the Authority maintains the search index. Never exposes search in IX-1.</summary>
public enum SearchIndexMode
{
    /// <summary>Default. No coverage or projection rows are created and nothing is derived. An existing projection is marked ReconciliationRequired on the next content revision (INV-R).</summary>
    Disabled = 0,

    /// <summary>IX-1: the index is maintained (derivation, seals, sweeps) but no query path exists. Development/Testing only.</summary>
    Dark = 1,
}

public sealed class SearchIndexOptions
{
    public const string SectionName = "SearchIndex";

    public SearchIndexMode Mode { get; set; } = SearchIndexMode.Disabled;

    /// <summary>S-A scheduling target (C-123/C-124: a new pass at least every 15 minutes; the deadline is 1 hour).</summary>
    public int CheapSweepIntervalSeconds { get; set; } = 900;

    /// <summary>§17.10 D3: the deadline evaluation runs every 5 minutes.</summary>
    public int DeadlineEvaluationIntervalSeconds { get; set; } = 300;

    /// <summary>S-B: entries verified per organization per pass (local ES256; ordered oldest first).</summary>
    public int SealCheckBatchSize { get; set; } = 2000;

    /// <summary>§17.10 enforcement deadlines (C-124, no grace).</summary>
    public int CheapPassDeadlineSeconds { get; set; } = 3600;

    public int SealVerificationDeadlineSeconds { get; set; } = 86_400;

    public int DeepVerificationDeadlineSeconds { get; set; } = 2_592_000;

    /// <summary>§17.10.5 K8: a verification timestamp further in the future than this is Inconclusive.</summary>
    public int FutureTimestampToleranceSeconds { get; set; } = 60;
}

public enum SearchSealProviderKind
{
    Unconfigured = 0,

    /// <summary>In-process P-256 key from configuration. Unit tests and Development/Testing only.</summary>
    LocalDevelopment = 1,

    /// <summary>Vault Transit ecdsa-p256 key in the Docker stack. Local/integration only.</summary>
    VaultTransit = 2,

    /// <summary>Azure Managed HSM EC-HSM P-256 key (ES256). The only provider permitted in Production. Not implemented in IX-1.</summary>
    AzureManagedHsm = 3,
}

/// <summary>A pinned seal verification key: identified by name and version, its public key given in deployment configuration.</summary>
public sealed class PinnedSealKey
{
    public string KeyName { get; set; } = string.Empty;

    public int KeyVersion { get; set; }

    /// <summary>DER SubjectPublicKeyInfo, base64. Never read from AuthorityDb.</summary>
    public string SubjectPublicKeyInfoBase64 { get; set; } = string.Empty;
}

public sealed class SearchSealOptions
{
    public const string SectionName = "SearchIndex:Seal";

    public SearchSealProviderKind Provider { get; set; } = SearchSealProviderKind.Unconfigured;

    /// <summary>One dedicated signing key per environment; the organization is bound inside the signed message.</summary>
    public string SigningKeyName { get; set; } = "search-entry-seal-v1";

    public int SigningKeyVersion { get; set; } = 1;

    /// <summary>Every key version accepted for verification. Must include the signing version.</summary>
    public List<PinnedSealKey> PinnedKeys { get; set; } = [];

    /// <summary>LocalDevelopment only: the PKCS#8 private key (base64) of the synthetic test key. Never set outside Development/Testing.</summary>
    public string? LocalPrivateKeyPkcs8Base64 { get; set; }
}
