namespace EnterpriseForms.Api.Composition;

/// <summary>
/// Declares the single owning feature of an endpoint (ADR-ARCH-001). The contract tests reject any routed
/// endpoint without this metadata, so a route cannot be published without a named owner.
/// </summary>
public sealed record FeatureOwnerMetadata(string FeatureName);

public static class FeatureOwnerEndpointExtensions
{
    public static TBuilder WithFeatureOwner<TBuilder>(this TBuilder builder, string featureName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return builder.WithMetadata(new FeatureOwnerMetadata(featureName));
    }
}
