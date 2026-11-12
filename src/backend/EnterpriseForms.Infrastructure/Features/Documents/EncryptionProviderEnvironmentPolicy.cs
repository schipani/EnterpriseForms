using Microsoft.Extensions.Configuration;

namespace EnterpriseForms.Infrastructure.Features.Documents;

/// <summary>
/// E0-1: the one startup rule for the configured encryption key provider, applied by every host that registers the business
/// infrastructure (Business API: <c>DocumentProviderStartupGuard</c>; Worker: <c>EncryptionProviderStartupGuard</c>). The
/// local KEK provider and (E-2) the local Docker Vault Transit provider are accepted only in the Development and Testing
/// environments (case-insensitive, as <c>IHostEnvironment.IsDevelopment</c>/<c>IsEnvironment</c>). Anywhere else the host refuses
/// to start; no other provider is substituted. A selected Vault Transit provider with incomplete or invalid settings is refused
/// too (E2-5), as is a selected local keyring whose key-version tables are invalid, including an activated but unprovisioned version
/// (E-4). The comparison of the configured value is exact, matching the registration in
/// <see cref="BusinessInfrastructureRegistration"/>, which is what actually selects the provider.
/// </summary>
public static class EncryptionProviderEnvironmentPolicy
{
    public static readonly IReadOnlyList<string> LocalProviderEnvironments = ["Development", "Testing"];

    /// <summary>The providers that exist only for local development and testing.</summary>
    public static readonly IReadOnlyList<string> LocalOnlyProviders = [EncryptionOptions.LocalDevelopmentProviderName, EncryptionOptions.VaultTransitProviderName];

    public static string ConfiguredKeyProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration[$"{EncryptionOptions.SectionName}:KeyProvider"] ?? "Unconfigured";
    }

    public static bool IsPermitted(string keyProvider, string environmentName) =>
        !LocalOnlyProviders.Contains(keyProvider, StringComparer.Ordinal)
        || LocalProviderEnvironments.Contains(environmentName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>Throws <see cref="InvalidOperationException"/> (fail closed) when the configured provider is not permitted in <paramref name="environmentName"/> or its settings are incomplete.</summary>
    public static void EnsurePermitted(IConfiguration configuration, string environmentName)
    {
        var keyProvider = ConfiguredKeyProvider(configuration);
        if (!IsPermitted(keyProvider, environmentName))
        {
            throw new InvalidOperationException($"Encryption:KeyProvider '{keyProvider}' is forbidden in {environmentName}. Azure Key Vault/Managed HSM providers are required (not implemented in Stage B). Refusing to start.");
        }

        if (keyProvider == EncryptionOptions.LocalDevelopmentProviderName)
        {
            // E-4: an invalid key-version table, including activation of an unprovisioned version, stops the host (fail closed early).
            var keyring = configuration.GetSection($"{EncryptionOptions.SectionName}:{nameof(EncryptionOptions.LocalDevelopmentKeyring)}").Get<LocalDevelopmentKeyringOptions>();
            if (LocalDevelopmentKeyProvider.KeyVersionProblem(keyring) is { } problem)
            {
                throw new InvalidOperationException($"Encryption:KeyProvider 'LocalDevelopment' is selected but its key versions are invalid: {problem}. Refusing to start.");
            }
        }

        if (keyProvider == EncryptionOptions.VaultTransitProviderName)
        {
            var settings = configuration.GetSection($"{EncryptionOptions.SectionName}:VaultTransit").Get<VaultTransitEncryptionOptions>() ?? new VaultTransitEncryptionOptions();
            if (settings.Problem() is { } problem)
            {
                throw new InvalidOperationException($"Encryption:KeyProvider 'VaultTransit' is selected but its settings are invalid: {problem}. Refusing to start.");
            }
        }
    }
}
