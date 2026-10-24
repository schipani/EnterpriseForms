using NetArchTest.Rules;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>Type-level boundary rules (NetArchTest). Complements the csproj graph test for transitive leaks.</summary>
public sealed class NamespaceBoundaryTests
{
    private static readonly string[] InfrastructureNamespaces = ["Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "Azure", "Microsoft.Data"];

    private static readonly string[] AuthorityNamespaces = ["AuthorizationAuthority.Domain", "AuthorizationAuthority.Application", "AuthorizationAuthority.Infrastructure", "AuthorizationAuthority.Contracts", "AuthorizationAuthority.Api"];

    /// <summary>Trust-plane SDK surfaces that must never appear in business assemblies (SIG-04/SIG-05: no SpiceDB writer, no HSM/Vault client, no key material).</summary>
    private static readonly string[] TrustPlaneNamespaces = ["Authzed", "Grpc", "Azure.Security.KeyVault", "AuthorizationAuthority.Infrastructure.Signing", "AuthorizationAuthority.Infrastructure.SpiceDb"];

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

    /// <summary>
    /// Phase C7 (ADR-PB-004 §4, C-56 D7): field search never reads the business store directly, so it cannot become a
    /// BusinessDb-only search. Business rows may reach it only through the FormRecords verified-read port, checked against
    /// the Authority-supplied catalogue (AC-5); in M0 it has no record dependency at all.
    /// </summary>
    [Fact]
    public void FieldSearchHasNoDirectBusinessDatabaseAccess()
    {
        var result = Types.InAssembly(typeof(Application.Common.Authority.IAuthorizationAuthorityClient).Assembly)
            .That().ResideInNamespace("EnterpriseForms.Application.Features.Search")
            .ShouldNot()
            .HaveDependencyOnAny(["EnterpriseForms.Application.Common.Persistence", "EnterpriseForms.Domain.Features.FormRecords", "EnterpriseForms.Application.Features.FormRecords.ListRecords"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
        Assert.Contains(typeof(Application.Features.Search.FieldSearch.FieldSearchHandler), Types.InAssembly(typeof(Application.Common.Authority.IAuthorizationAuthorityClient).Assembly).That().ResideInNamespace("EnterpriseForms.Application.Features.Search").GetTypes());
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
        foreach (var assembly in new[] { typeof(Api.Program).Assembly, typeof(Worker.Program).Assembly, typeof(Infrastructure.Persistence.BusinessDbContext).Assembly, typeof(Application.Common.Authority.IAuthorizationAuthorityClient).Assembly })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny([.. AuthorityNamespaces, .. TrustPlaneNamespaces]).GetResult();
            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    [Fact]
    public void AuthorityApplicationHoldsNoProviderSdkTypes()
    {
        // The signing/projection SDKs live only behind Infrastructure adapters; the command service cannot reach them directly.
        var result = Types.InAssembly(typeof(AuthorizationAuthority.Application.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(["Authzed", "Grpc", "Azure", "Microsoft.EntityFrameworkCore", "Npgsql", "System.Net.Http"])
            .GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void NoBusinessTypeExposesASignAnyBytesOperation()
    {
        foreach (var assembly in new[] { typeof(Api.Program).Assembly, typeof(Infrastructure.Persistence.BusinessDbContext).Assembly, typeof(Application.Common.Authority.IAuthorizationAuthorityClient).Assembly })
        {
            var offenders = assembly.GetTypes().SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                .Where(m => !m.IsSpecialName && m.Name.StartsWith("Sign", StringComparison.Ordinal))
                .Select(m => m.DeclaringType!.FullName + "." + m.Name).ToList();
            Assert.True(offenders.Count == 0, "business assemblies must not define row-signing operations: " + string.Join(", ", offenders));
        }
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? "ok" : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
