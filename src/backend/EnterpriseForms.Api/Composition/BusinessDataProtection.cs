using Microsoft.AspNetCore.DataProtection;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// Data Protection key ring for the session ticket and cursors. G23: every API instance behind one front end must decrypt
/// the same cookies, so instances share one key ring (<c>DataProtection:KeyRingDirectory</c>) under one application name.
/// Without the setting each process keeps its own ring, as before. The key ring only makes a ticket readable: it never
/// makes a session valid — that is decided by the Authority on every request.
/// <para>
/// Production key storage (Azure Blob + Key Vault key encryption) is part of the Azure deployment and is not configured
/// here; a file-system ring is for Docker integration and development.
/// </para>
/// </summary>
public static class BusinessDataProtection
{
    public const string SectionName = "DataProtection";
    public const string ApplicationName = "EnterpriseForms.Api";

    public static IServiceCollection AddBusinessDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (configuration[$"{SectionName}:KeyRingDirectory"] is { Length: > 0 } directory)
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(directory));
        }

        return services;
    }
}
