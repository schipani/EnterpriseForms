using System.Reflection;

namespace EnterpriseForms.Api.Composition;

/// <summary>Build-time OpenAPI generation starts the host inside the GetDocument.Insider tool; startup guards skip only there.</summary>
public static class HostExecutionMode
{
    public static bool IsOpenApiDocumentGeneration { get; } =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);
}
