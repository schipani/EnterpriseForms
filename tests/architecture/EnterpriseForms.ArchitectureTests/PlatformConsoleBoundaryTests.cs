using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using EnterpriseForms.PlatformConsole.Composition;
using EnterpriseForms.PlatformConsole.Infrastructure;
using NetArchTest.Rules;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// C8 M2 Stage 3: the Platform Console BFF is a separate deployable that consumes only the Authority's wire contract. It never
/// depends on business assemblies or Authority internals, only its decision service drives the evidence signer (no generic signing
/// path), and the wire values it mirrors stay equal to the Authority's.
/// </summary>
public sealed class PlatformConsoleBoundaryTests
{
    private static readonly System.Reflection.Assembly Console = typeof(PlatformConsoleOptions).Assembly;

    [Fact]
    public void TheConsoleDependsOnNoBusinessAssemblyAndNoAuthorityInternals()
    {
        var result = Types.InAssembly(Console)
            .ShouldNot()
            .HaveDependencyOnAny("EnterpriseForms.Application", "EnterpriseForms.Infrastructure", "EnterpriseForms.Api", "EnterpriseForms.Worker", "EnterpriseForms.Domain", "EnterpriseForms.Contracts",
                "AuthorizationAuthority.Application", "AuthorizationAuthority.Domain", "AuthorizationAuthority.Infrastructure", "AuthorizationAuthority.Api", "Npgsql", "Microsoft.EntityFrameworkCore", "Authzed", "Grpc")
            .GetResult();
        Assert.True(result.IsSuccessful, "Platform Console types with forbidden dependencies: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void OnlyTheDecisionServiceUsesTheEvidenceSigner()
    {
        var users = Types.InAssembly(Console).That().HaveDependencyOn(typeof(IOperatorEvidenceSigner).FullName).GetTypes()
            .Select(t => t.FullName!.Split('+')[0])
            .Where(n => n != typeof(IOperatorEvidenceSigner).FullName)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ["EnterpriseForms.PlatformConsole.Features.OperatorApprovals.OperatorDecisionService", "EnterpriseForms.PlatformConsole.Infrastructure.VaultTransitEvidenceSigner", "EnterpriseForms.PlatformConsole.Program"],
            users);
    }

    [Fact]
    public void MirroredWireValuesEqualTheAuthoritys()
    {
        Assert.Equal(PlatformDecisions.Approve, OperatorDecisions.Approve);
        Assert.Equal(PlatformDecisions.Reject, OperatorDecisions.Reject);
        Assert.Equal(PlatformDecisions.Withdraw, OperatorDecisions.Withdraw);
        Assert.Equal(AuthorityErrorCodes.PlatformOperatorNotActive, ConsoleErrorCodes.PlatformOperatorNotActive);
        Assert.Equal(AuthorityErrorCodes.ApprovalStatementChanged, ConsoleErrorCodes.ApprovalStatementChanged);
        Assert.Equal(PlatformOperatorEvidenceKinds.SyntheticDevelopmentJws, EnterpriseForms.PlatformConsole.Features.OperatorApprovals.OperatorDecisionService.EvidenceKind);
    }
}
