using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Persistence.Configurations;
using EnterpriseForms.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// IX-1 (C-124, C-126) architecture fitness functions for the Authority search index, by reflection and source scan:
/// INV-R has one writer and four call sites (W1-W4); index writes happen only on the successful commit path; the seal
/// verifier reads no key from the database and the signer is reached only through the pin-checking gate; only the search
/// index feature touches the search schema; the new tables follow the naming rules and are under forced RLS.
/// </summary>
public sealed partial class SearchIndexBoundaryTests
{
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static IEnumerable<(string Path, string Text)> AuthoritySources() =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(Authority, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static List<string> Matching(Regex pattern) => AuthoritySources().Where(s => pattern.IsMatch(s.Text)).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"\.AdvanceContentIntegrityRevision\s*\(")]
    private static partial Regex RevisionAdvance();

    [GeneratedRegex(@"\brevisions\.(AdvanceForCommitAsync|AdvanceWithoutIndexChangeAsync)\s*\(")]
    private static partial Regex CoordinatorCall();

    [GeneratedRegex(@"\bISearchSealSigner\b")]
    private static partial Regex SignerUse();

    [GeneratedRegex(@"(?<!Features)\.SearchIndex\b(?![A-Za-z])")]
    private static partial Regex SearchIndexAccess();

    [Fact]
    public void ContentIntegrityRevisionIsInitOnlyAndAdvancedOnlyByTheCoordinator()
    {
        var setter = typeof(OrganizationAuthorizationState).GetProperty(nameof(OrganizationAuthorizationState.ContentIntegrityRevision))!.SetMethod!;
        Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers()); // ++ or = outside an initializer does not compile
        Assert.Equal(["AuthorizationAuthority.Application/Features/SearchIndex/ContentRevisionCoordinator.cs"], Matching(RevisionAdvance()));
    }

    [Fact]
    public void TheFourRevisionSitesAreTheOnlyCallersOfTheCoordinator()
    {
        Assert.Equal(["AuthorizationAuthority.Application/Features/Content/ContentService.cs"], Matching(CoordinatorCall()));
        var service = AuthoritySources().Single(s => s.Path.EndsWith("Features/Content/ContentService.cs", StringComparison.Ordinal)).Text;
        var calls = CoordinatorCall().Matches(service).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(1, calls.Count(c => c == "AdvanceForCommitAsync"));          // W1
        Assert.Equal(3, calls.Count(c => c == "AdvanceWithoutIndexChangeAsync")); // W2, W3, W4
        Assert.DoesNotMatch(@"ContentIntegrityRevision\s*(\+\+|\+=|-=)", service);
    }

    [Fact]
    public void IndexWritesHappenOnlyOnTheSuccessfulCommitPath()
    {
        var service = AuthoritySources().Single(s => s.Path.EndsWith("Features/Content/ContentService.cs", StringComparison.Ordinal)).Text;
        var start = service.IndexOf("public async Task<ContentRevision> CommitAsync(", StringComparison.Ordinal);
        var end = service.IndexOf("public async Task<ContentRevision> GetHeadAsync(", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var body = service[start..end];
        var derive = body.IndexOf("revisions.AdvanceForCommitAsync(", StringComparison.Ordinal);
        Assert.True(derive > body.IndexOf("VerifyStagedAgainstLeavesAsync(request.StagedItems", StringComparison.Ordinal), "derivation must follow the staged-byte verification");
        Assert.True(derive > body.IndexOf("RECORD_LOCKED_BEFORE_COMMIT", StringComparison.Ordinal), "derivation must follow every abort path");
        Assert.True(derive > body.IndexOf("head.CommittedOperationId = operationId;", StringComparison.Ordinal), "derivation must see the new head");
        Assert.True(derive < body.LastIndexOf("await scope.CommitAsync(cancellationToken)", StringComparison.Ordinal), "derivation must precede the final commit");
        Assert.Equal(1, Regex.Count(body, @"revisions\.AdvanceForCommitAsync\("));
    }

    [Fact]
    public void TheSealVerifierReadsNoKeyFromTheDatabase()
    {
        var forbidden = new[] { typeof(IAuthorityStore), typeof(IAuthorityScope), typeof(ISearchIndexStore), typeof(DbContext), typeof(ISearchSealSigner) };
        var constructor = Assert.Single(typeof(SearchSealVerifier).GetConstructors());
        Assert.Equal([typeof(SearchSealOptions)], constructor.GetParameters().Select(p => p.ParameterType));
        var fields = typeof(SearchSealVerifier).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f => forbidden.Any(t => t.IsAssignableFrom(f.FieldType)));
        var source = AuthoritySources().Single(s => s.Path.EndsWith("Features/SearchIndex/SearchSealVerifier.cs", StringComparison.Ordinal)).Text;
        var verifierClass = source[..source.IndexOf("public sealed class SearchSealSigningGate", StringComparison.Ordinal)];
        Assert.DoesNotMatch(@"IAuthorityStore|ISearchIndexStore|DbContext|FindKeyRegistration|GetPublicKeyAsync", verifierClass);
    }

    [Fact]
    public void TheSealSignerIsReachedOnlyThroughThePinCheckingGate()
    {
        Assert.Equal(
            [
                "AuthorizationAuthority.Api/Composition/SearchIndexComposition.cs",
                "AuthorizationAuthority.Application/Common/Ports/ISearchSealSigner.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchSealVerifier.cs",
                "AuthorizationAuthority.Infrastructure/Signing/SearchSealSigners.cs",
            ],
            Matching(SignerUse()));
        var gateConstructor = Assert.Single(typeof(SearchSealSigningGate).GetConstructors());
        Assert.Contains(typeof(SearchSealVerifier), gateConstructor.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void OnlyTheSearchIndexFeatureTouchesTheSearchSchema()
    {
        Assert.Equal(
            [
                "AuthorizationAuthority.Application/Features/SearchIndex/ContentRevisionCoordinator.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchIndexSweepService.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchIndexWriter.cs",
            ],
            Matching(SearchIndexAccess()));
    }

    [Fact]
    public void SearchSchemaTablesFollowTheNamingRules()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new AuthorityDbContext(options);
        var searchTables = context.Model.GetEntityTypes().Where(e => e.GetSchema() == SearchIndexSchema.Name).Select(e => e.GetTableName()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SearchIndexSchema.DerivedTables.Concat(SearchIndexSchema.StateTables).Order(StringComparer.Ordinal), searchTables.Order(StringComparer.Ordinal));
        var violations = ModelNamingValidator.Validate(context.Model).Where(v => searchTables.Any(t => v.ObjectName == t || v.ObjectName.StartsWith(t + ".", StringComparison.Ordinal))).ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Select(v => $"{v.ObjectName}: {v.RuleCode} ({v.Detail})")));
    }

    [Fact]
    public void TheMigrationForcesRowLevelSecurityOnEveryDerivedTableAndInstallsTheInvariantTrigger()
    {
        var migration = File.ReadAllText(Directory.EnumerateFiles(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations"), "*_AuthoritySearchIndexIx1.cs").Single());
        Assert.Contains("FORCE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE POLICY rls_{table}_organization", migration, StringComparison.Ordinal);
        foreach (var table in SearchIndexSchema.DerivedTables)
        {
            Assert.Contains($"\"{table}\"", migration, StringComparison.Ordinal);
        }

        Assert.Contains("CREATE CONSTRAINT TRIGGER trg_content_revision_search_synchronization", migration, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeRoleCannotUpdateDerivedRowsExceptTheSealTimestamps()
    {
        var statements = AuthorityRuntimeGrants.SearchIndexStatements("runtime_role").ToList();
        foreach (var table in SearchIndexSchema.DerivedTables)
        {
            Assert.Contains($"GRANT SELECT, INSERT, DELETE ON authority_search.{table} TO runtime_role", statements);
        }

        Assert.Single(statements, s => s.StartsWith("GRANT UPDATE", StringComparison.Ordinal) || s.Contains(", UPDATE", StringComparison.Ordinal) && s.Contains("search_record_entries", StringComparison.Ordinal));
        Assert.Contains("GRANT UPDATE (last_seal_verified_on, last_seal_attempted_on, last_seal_attempt_outcome_code) ON authority_search.search_record_entries TO runtime_role", statements);
        Assert.DoesNotContain(statements, s => s.Contains("DELETE", StringComparison.Ordinal) && SearchIndexSchema.StateTables.Any(t => s.Contains(t, StringComparison.Ordinal)));
        Assert.Contains("ALTER ROLE authority_search_reader SET default_transaction_read_only = on", statements);
        Assert.DoesNotContain(statements, s => s.Contains("authority_search_reader", StringComparison.Ordinal) && Regex.IsMatch(s, @"\b(INSERT|UPDATE|DELETE)\b"));
    }
}
