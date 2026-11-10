using System.Text.RegularExpressions;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Persistence.Configurations;
using EnterpriseForms.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// IX-5a (C-147) architecture fitness functions for the dark Tier 2 access projection, by source scan and model inspection:
/// <list type="bullet">
/// <item>no search path, security command or content commit touches it (FD-5-ALT: no Tier 2 write in a Tier 1 transaction; dark);</item>
/// <item>the authorization journal and the canonical edges each have one writer (journal completeness, C-147 §3);</item>
/// <item>its schema is separate from authority_search, granted to the runtime only (never the search reader), forced-RLS, and named by the rules.</item>
/// </list>
/// </summary>
public sealed partial class SearchAccessBoundaryTests
{
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static IEnumerable<(string Path, string Text)> AuthoritySources() =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(Authority, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static List<string> Matching(Regex pattern) => AuthoritySources().Where(s => pattern.IsMatch(s.Text)).Select(s => s.Path).Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"\bSearchAccess(Store|Projection|Checkpoint|Edge|RecordGrant|FieldItemGrant|Derivation|Options|Status|Testing|Maintenance|Composition|Boundary|Kinds|ReasonCodes|IntegrityException|JournalVerifier)?\b|authority_search_access|\.SearchAccess\b")]
    private static partial Regex SearchAccessUse();

    [GeneratedRegex(@"JournalChain\.Append\s*\(\s*\w+\s*,\s*JournalStreamNames\.Authorization\b")]
    private static partial Regex AuthorizationStreamAppend();

    [GeneratedRegex(@"\bscope\.AddEdge\s*\(")]
    private static partial Regex CanonicalEdgeAdd();

    [GeneratedRegex(@"\.RemovedBySecurityCommandId\s*=(?!=)")]
    private static partial Regex CanonicalEdgeRemove();

    [Fact]
    public void OnlyTheAccessProjectionFeatureItsStoreAndItsTestingEndpointsTouchTier2()
    {
        var allowed = new[]
        {
            "AuthorizationAuthority.Api/Composition/SearchAccessComposition.cs",
            "AuthorizationAuthority.Api/Features/SearchIndex/SearchAccessTestingEndpoints.cs",
            "AuthorizationAuthority.Api/Program.cs",
            "AuthorizationAuthority.Application/Common/Ports/IAuthorityStore.cs",
            "AuthorizationAuthority.Application/Common/Ports/ISearchAccessStore.cs",
            "AuthorizationAuthority.Application/Features/SearchAccess/SearchAccessDerivation.cs",
            "AuthorizationAuthority.Application/Features/SearchAccess/SearchAccessJournalVerifier.cs",
            "AuthorizationAuthority.Application/Features/SearchAccess/SearchAccessProjectionService.cs",
            "AuthorizationAuthority.Domain/Features/SearchAccess/SearchAccessEntities.cs",
            "AuthorizationAuthority.Infrastructure/Persistence/AuthorityDbContext.cs",
            "AuthorizationAuthority.Infrastructure/Persistence/AuthorityRuntimeGrants.cs",
            "AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs",
            "AuthorizationAuthority.Infrastructure/Persistence/Configurations/SearchAccessConfigurations.cs",
            "AuthorizationAuthority.Infrastructure/Persistence/SearchAccessStore.cs",
        };
        var users = Matching(SearchAccessUse());
        Assert.True(users.All(allowed.Contains), "unexpected Tier 2 users: " + string.Join(", ", users.Except(allowed)));
    }

    [Fact]
    public void NoSearchPathCommandOrContentTransactionReachesTier2()
    {
        // IX-5a is dark: no query path reads the rows; FD-5-ALT: no Tier 2 write inside a security-command or content transaction.
        var forbidden = AuthoritySources().Where(s =>
                s.Path.StartsWith("AuthorizationAuthority.Application/Features/SearchIndex/", StringComparison.Ordinal)
                || s.Path.StartsWith("AuthorizationAuthority.Application/Features/SecurityCommands/", StringComparison.Ordinal)
                || s.Path.StartsWith("AuthorizationAuthority.Application/Features/Content/", StringComparison.Ordinal)
                || s.Path.StartsWith("AuthorizationAuthority.Application/Features/Catalogue/", StringComparison.Ordinal)
                || s.Path.EndsWith("SearchQueryReader.cs", StringComparison.Ordinal)
                || s.Path.EndsWith("SearchIndexStore.cs", StringComparison.Ordinal))
            .Where(s => SearchAccessUse().IsMatch(s.Text)).Select(s => s.Path).ToList();
        Assert.Empty(forbidden);
    }

    [Fact]
    public void TheAuthorizationJournalAndTheCanonicalEdgesHaveOneWriter()
    {
        // Journal completeness (C-147 §3): every canonical edge change is made by SecurityCommandService.ApplyPlan, which journals the
        // identical delta in the same transaction; nothing else appends the authorization stream.
        Assert.Equal(["AuthorizationAuthority.Application/Features/SecurityCommands/SecurityCommandService.cs"], Matching(AuthorizationStreamAppend()));
        Assert.Equal(["AuthorizationAuthority.Application/Features/SecurityCommands/SecurityCommandService.cs"], Matching(CanonicalEdgeAdd()));
        Assert.Equal(["AuthorizationAuthority.Application/Features/SecurityCommands/SecurityCommandService.cs"], Matching(CanonicalEdgeRemove()));
        var service = File.ReadAllText(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Application", "Features", "SecurityCommands", "SecurityCommandService.cs"));
        var applyPlan = service[service.IndexOf("internal static void ApplyPlan(", StringComparison.Ordinal)..];
        applyPlan = applyPlan[..applyPlan.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("scope.AddEdge(edge);", applyPlan, StringComparison.Ordinal);
        Assert.Contains("edge.RemovedBySecurityCommandId = command.SecurityCommandId;", applyPlan, StringComparison.Ordinal);
        Assert.Contains("delta.Select(d => $\"{d.Operation} {d.Tuple.Canonical}\")", applyPlan, StringComparison.Ordinal);
        Assert.Contains("JournalChain.Append(state, JournalStreamNames.Authorization, revision, \"SecurityCommandCommitted\"", applyPlan, StringComparison.Ordinal);
    }

    [Fact]
    public void Tier2HasItsOwnForcedRlsSchemaFollowingTheNamingRules()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new AuthorityDbContext(options);
        var tables = context.Model.GetEntityTypes().Where(e => e.GetSchema() == SearchAccessSchema.Name).Select(e => e.GetTableName()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SearchAccessSchema.AllTables.Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
        Assert.NotEqual(SearchIndexSchema.Name, SearchAccessSchema.Name);
        var violations = ModelNamingValidator.Validate(context.Model).Where(v => tables.Any(t => v.ObjectName == t || v.ObjectName.StartsWith(t + ".", StringComparison.Ordinal))).ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Select(v => $"{v.ObjectName}: {v.RuleCode} ({v.Detail})")));

        var migration = File.ReadAllText(Directory.EnumerateFiles(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations"), "*_AuthoritySearchAccessIx5a.cs").Single());
        Assert.Contains("FORCE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE POLICY rls_{table}_organization ON authority_search_access.{table}", migration, StringComparison.Ordinal);
        foreach (var table in SearchAccessSchema.AllTables)
        {
            Assert.Contains($"\"{table}\"", migration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRuntimeAloneIsGrantedTier2AndNeverUpdatesDerivedRows()
    {
        var statements = AuthorityRuntimeGrants.SearchAccessStatements("runtime_role").ToList();
        foreach (var table in SearchAccessSchema.DerivedTables)
        {
            Assert.Contains($"GRANT SELECT, INSERT, DELETE ON authority_search_access.{table} TO runtime_role", statements);
        }

        Assert.Equal(["GRANT SELECT, INSERT, UPDATE ON authority_search_access.search_access_checkpoints TO runtime_role"], statements.Where(s => s.Contains("UPDATE", StringComparison.Ordinal) && s.StartsWith("GRANT", StringComparison.Ordinal)).ToList());
        Assert.DoesNotContain(statements, s => s.StartsWith("GRANT", StringComparison.Ordinal) && s.Contains(AuthorityRuntimeGrants.SearchReaderRole, StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains($"REVOKE ALL ON SCHEMA authority_search_access FROM {AuthorityRuntimeGrants.SearchReaderRole}", StringComparison.Ordinal));
        // The Tier 1 reader's blanket grant is on authority_search only.
        Assert.DoesNotContain(AuthorityRuntimeGrants.SearchIndexStatements("runtime_role"), s => s.Contains("authority_search_access", StringComparison.Ordinal));
    }
}
