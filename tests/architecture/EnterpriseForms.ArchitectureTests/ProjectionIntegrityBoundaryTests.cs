using System.Text.RegularExpressions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// C8 M4 S1 (Design B revision 2 §2.4–§2.5, C-117) architecture fitness functions ARCH-01…ARCH-03 and U-09, by source scan of
/// the Authority: SpiceDB decision reads only through <c>ProjectionIntegrityVerifier</c>, every consumer pins and completes,
/// the Authority-held ZedToken is used only where the protocol allows, no code path clears a quarantine, and one writer
/// advances the authorization revision (one authorization-stream journal entry per revision, which the marker relies on).
/// </summary>
public sealed partial class ProjectionIntegrityBoundaryTests
{
    [GeneratedRegex(@"\bspiceDb\.(CheckAsync|CheckBulkAsync|LookupResourcesAsync)\s*\(")]
    private static partial Regex DirectDecisionRead();

    [GeneratedRegex(@"\bverifier\.(CheckAsync|CheckBulkAsync|LookupResourcesAsync)\s*\(")]
    private static partial Regex VerifiedDecisionRead();

    [GeneratedRegex(@"\bProjectionZedToken\b")]
    private static partial Regex ZedTokenUse();

    [GeneratedRegex(@"IntegrityQuarantine(Enabled|ReasonCode)\s*=(?!=)")]
    private static partial Regex QuarantineAssignment();

    [GeneratedRegex(@"IntegrityQuarantineEnabled\s*=\s*false")]
    private static partial Regex QuarantineCleared();

    [GeneratedRegex(@"\.AuthorizationRevision\s*=(?!=)")]
    private static partial Regex RevisionAssignment();

    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static IEnumerable<(string Path, string Text)> AuthoritySources() =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(Authority, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static List<string> Matching(Regex pattern) => AuthoritySources().Where(s => pattern.IsMatch(s.Text)).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void OnlyTheVerifierIssuesSpiceDbDecisionReads()
    {
        Assert.Equal(["AuthorizationAuthority.Application/Common/ProjectionIntegrityVerifier.cs"], Matching(DirectDecisionRead()));
    }

    [Fact]
    public void EveryDecisionPathPinsAndCompletes()
    {
        var consumers = AuthoritySources().Where(s => VerifiedDecisionRead().IsMatch(s.Text)).ToList();
        Assert.Equal(
            [
                "AuthorizationAuthority.Application/Features/Authorization/PermissionCheckService.cs",
                "AuthorizationAuthority.Application/Features/Catalogue/SearchCatalogueService.cs",
                "AuthorizationAuthority.Application/Features/Content/ContentService.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/IndexedSearchService.RecordList.cs", // IX-6: My Records, one pin, one completion
                "AuthorizationAuthority.Application/Features/SearchIndex/IndexedSearchService.cs", // IX-3: search and page, each pinned and completed
            ],
            consumers.Select(c => c.Path).Order(StringComparer.Ordinal).ToList());
        foreach (var (path, text) in consumers)
        {
            var pins = Regex.Count(text, @"\bverifier\.PinAsync\s*\(");
            var completes = Regex.Count(text, @"\bverifier\.CompleteAsync\s*\(");
            Assert.True(pins > 0 && pins == completes, $"{path}: {pins} pins and {completes} completes; every pinned snapshot must be verified before its decision is used");
        }
    }

    [Fact]
    public void TheAuthorityHeldZedTokenIsOnlyAFreshnessSelector()
    {
        Assert.Equal(
            [
                "AuthorizationAuthority.Application/Common/OrganizationGate.cs",
                "AuthorizationAuthority.Application/Common/ProjectionIntegrityVerifier.cs",
                "AuthorizationAuthority.Application/Features/Projection/ProjectionIntegrityMonitor.cs",
                "AuthorizationAuthority.Application/Features/Projection/ProjectionProcessor.cs",
                "AuthorizationAuthority.Domain/Features/Organizations/OrganizationAuthorizationState.cs",
                "AuthorizationAuthority.Infrastructure/Persistence/Configurations/AuthorityEntityConfigurations.cs",
            ],
            Matching(ZedTokenUse()));
    }

    [Fact]
    public void NoCodePathClearsOrRewritesAnIntegrityQuarantine()
    {
        Assert.Empty(Matching(QuarantineCleared()));
        Assert.Equal(["AuthorizationAuthority.Application/Common/OrganizationGate.cs"], Matching(QuarantineAssignment()));
        var migration = Directory.EnumerateFiles(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations"), "*_ProjectionIntegrityS1.cs").Single();
        Assert.Contains("trg_organization_integrity_quarantine_is_permanent", File.ReadAllText(migration), StringComparison.Ordinal);
    }

    [Fact]
    public void OneWriterAdvancesTheAuthorizationRevision()
    {
        Assert.Equal(["AuthorizationAuthority.Application/Features/SecurityCommands/SecurityCommandService.cs"], Matching(RevisionAssignment()));
        var service = File.ReadAllText(Path.Combine(Authority, "AuthorizationAuthority.Application", "Features", "SecurityCommands", "SecurityCommandService.cs"));
        Assert.Single(Regex.Matches(service, @"JournalChain\.Append\(state, JournalStreamNames\.Authorization, revision,"));
    }
}
