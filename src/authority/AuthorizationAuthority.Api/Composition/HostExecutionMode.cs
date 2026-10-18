using System.Reflection;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) starts the host inside the
/// <c>GetDocument.Insider</c> tool process. Startup guards that require deployment configuration skip in that
/// mode only; every real host start (dotnet run, container, WebApplicationFactory) still fails closed.
/// </summary>
public static class HostExecutionMode
{
    public static bool IsOpenApiDocumentGeneration { get; } =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);
}
