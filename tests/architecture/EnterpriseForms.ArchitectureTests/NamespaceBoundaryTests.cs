using NetArchTest.Rules;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>Type-level boundary rules (NetArchTest). Complements the csproj graph test for transitive leaks.</summary>
public sealed class NamespaceBoundaryTests
{
    private static readonly string[] InfrastructureNamespaces = ["Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "Azure", "Microsoft.Data"];

    private static readonly string[] AuthorityNamespaces = ["AuthorizationAuthority.Domain", "AuthorizationAuthority.Application", "AuthorizationAuthority.Infrastructure", "AuthorizationAuthority.Contracts", "AuthorizationAuthority.Api"];

    [Fact]
    public void DomainDependsOnNothingOutward()
    {
        var result = Types.InAssembly(typeof(Domain.SharedKernel.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, .. AuthorityNamespaces, "EnterpriseForms.Application", "EnterpriseForms.Infrastructure", "EnterpriseForms.Api", "EnterpriseForms.Worker", "EnterpriseForms.Contracts"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructureOrHosts()
    {
        var result = Types.InAssembly(typeof(Application.Common.Authority.IAuthorizationAuthorityClient).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, .. AuthorityNamespaces, "EnterpriseForms.Infrastructure", "EnterpriseForms.Api", "EnterpriseForms.Worker"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void ContractsContainOnlyWireTypes()
    {
        var result = Types.InAssembly(typeof(Contracts.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, "EnterpriseForms.Domain", "EnterpriseForms.Application", "EnterpriseForms.Infrastructure"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void IntegrityLibraryIsProviderFree()
    {
        var result = Types.InAssembly(typeof(Integrity.Merkle.MerkleManifestV1).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, .. AuthorityNamespaces, "System.Net.Http", "EnterpriseForms.Domain", "EnterpriseForms.Application"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void AuthorityDomainIsIsolatedFromBusinessAndProviders()
    {
        var result = Types.InAssembly(typeof(AuthorizationAuthority.Domain.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. InfrastructureNamespaces, "EnterpriseForms.Domain", "EnterpriseForms.Application", "EnterpriseForms.Infrastructure", "EnterpriseForms.Api", "EnterpriseForms.Worker", "EnterpriseForms.Contracts"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void BusinessHostsDoNotReferenceAuthorityImplementation()
    {
        foreach (var assembly in new[] { typeof(Api.Program).Assembly, typeof(Worker.Program).Assembly, typeof(Infrastructure.Persistence.BusinessDbContext).Assembly })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(AuthorityNamespaces).GetResult();
            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? "ok" : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
