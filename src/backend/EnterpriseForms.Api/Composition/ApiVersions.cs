namespace EnterpriseForms.Api.Composition;

public static class ApiVersions
{
    /// <summary>Route prefix for every business endpoint (ADR-PB-006).</summary>
    public const string V1Prefix = "/api/v1";

    /// <summary>OpenAPI document name; the build emits contracts/openapi/enterprise-forms-v1.json.</summary>
    public const string V1DocumentName = "v1";
}
