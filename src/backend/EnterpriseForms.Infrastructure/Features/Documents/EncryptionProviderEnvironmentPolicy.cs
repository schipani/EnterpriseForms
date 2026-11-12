using Microsoft.Extensions.Configuration;

namespace EnterpriseForms.Infrastructure.Features.Documents;

/// <summary>
/// E0-1: the one startup rule for the configured encryption key provider, applied by every host that registers the business
/// infrastructure (Business API: <c>DocumentProviderStartupGuard</c>; Worker: <c>EncryptionProviderStartupGuard</c>). The
/// local KEK provider is accepted only in the Development and Testing environments (case-insensitive, as
/// <c>IHostEnvironment.IsDevelopment</c>/<c>IsEnvironment</c>). Anywhere else the host refuses to start; no other provider is
/// substituted. The comparison of the configured value is exact, matching the registration in
/// <see cref="BusinessInfrastructureRegistration"/>, which is what actually selects the local provider.
/// </summary>
public static class EncryptionProviderEnvironmentPolicy
{
    public static readonly IReadOnlyList<string> LocalProviderEnvironments = ["Development", "Testing"];

    public static string ConfiguredKeyProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration[$"{EncryptionOptions.SectionName}:KeyProvider"] ?? "Unconfigured";
    }

    public static bool IsPermitted(string keyProvider, string environmentName) =>
        keyProvider != EncryptionOptions.LocalDevelopmentProviderName
        || LocalProviderEnvironments.Contains(environmentName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>Throws <see cref="InvalidOperationException"/> (fail closed) when the configured provider is not permitted in <paramref name="environmentName"/>.</summary>
    public static void EnsurePermitted(IConfiguration configuration, string environmentName)
    {
        var keyProvider = ConfiguredKeyProvider(configuration);
        if (!IsPermitted(keyProvider, environmentName))
        {
            throw new InvalidOperationException($"Encryption:KeyProvider '{EncryptionOptions.LocalDevelopmentProviderName}' is forbidden in {environmentName}. Azure Key Vault/Managed HSM providers are required (not implemented in Stage B). Refusing to start.");
        }
    }
}
