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

    [GeneratedRegex(@"\.DeriveForBackfillAsync\s*\(")]
    private static partial Regex BackfillDerivation();

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
                "AuthorizationAuthority.Infrastructure/Signing/ManagedHsmSearchSealSigner.cs", // IX-7: the Managed HSM provider (an implementation)
                "AuthorizationAuthority.Infrastructure/Signing/SearchSealSigners.cs",
            ],
            Matching(SignerUse()));
        var gateConstructor = Assert.Single(typeof(SearchSealSigningGate).GetConstructors());
        Assert.Contains(typeof(SearchSealVerifier), gateConstructor.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void OnlyTheSearchIndexFeatureTouchesTheSearchSchema()
    {
        // IX-2 adds the builder plane (backfill, attestation, deep sweep) and SearchProjectionRebuild, both in the same feature.
        Assert.Equal(
            [
                "AuthorizationAuthority.Application/Features/SearchIndex/ContentRevisionCoordinator.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/IndexedSearchService.cs", // IX-3: result snapshots and projection-state checks
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchIndexBuilderService.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchIndexSweepService.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchIndexWriter.cs",
                "AuthorizationAuthority.Application/Features/SearchIndex/SearchProjectionRebuildService.cs",
            ],
            Matching(SearchIndexAccess()));
    }

    /// <summary>IX-2 (IX-D1 (b), IX-D6): a backfill derives only from bytes the Authority has just verified against the head, in the builder service alone.</summary>
    [Fact]
    public void BackfillDerivesOnlyAfterTheWorkloadVerificationAndOnlyInTheBuilderService()
    {
        Assert.Equal(["AuthorizationAuthority.Application/Features/SearchIndex/SearchIndexBuilderService.cs"], Matching(BackfillDerivation()));
        var service = AuthoritySources().Single(s => s.Path.EndsWith("Features/SearchIndex/SearchIndexBuilderService.cs", StringComparison.Ordinal)).Text;
        var start = service.IndexOf("public async Task<SearchIndexBackfillResponse> BackfillAsync(", StringComparison.Ordinal);
        var end = service.IndexOf("public async Task<SearchIndexAttestation> AttestAsync(", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var body = service[start..end];
        var verify = body.IndexOf("content.VerifyWorkloadObservedContentAsync(", StringComparison.Ordinal);
        var refusal = body.IndexOf("if (!verification.Verified)", StringComparison.Ordinal);
        var derive = body.IndexOf("writer.DeriveForBackfillAsync(", StringComparison.Ordinal);
        Assert.True(verify > 0 && refusal > verify && derive > refusal, "derivation must follow the verification and its refusal branch");
        Assert.Equal(1, Regex.Count(body, @"writer\.DeriveForBackfillAsync\("));
        Assert.Contains("verification.Head!", body, StringComparison.Ordinal); // the head is the verified one, never a builder value
    }

    /// <summary>IX-2: the builder's requests carry identifiers and bytes only — never a posting, entry, seal, coverage state or value.</summary>
    [Fact]
    public void BuilderRequestsCarryNoIndexRowsSealsOrStates()
    {
        var requests = typeof(AuthorizationAuthority.Contracts.V1.SearchIndexBackfillRequest).Assembly.GetTypes()
            .Where(t => t.Namespace == "AuthorizationAuthority.Contracts.V1" && t.Name.StartsWith("SearchIndex", StringComparison.Ordinal) && (t.Name.EndsWith("Request", StringComparison.Ordinal) || t.Name.EndsWith("Item", StringComparison.Ordinal)) && !t.Name.EndsWith("WorkItem", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["SearchIndexAttestationRequest", "SearchIndexBackfillItem", "SearchIndexBackfillRequest", "SearchIndexDeepSweepWorkRequest", "SearchIndexDeepVerifyRequest", "SearchIndexPopulationRequest"], requests.Select(t => t.Name).Order(StringComparer.Ordinal));
        var allowed = new[] { typeof(Guid), typeof(Guid?), typeof(int), typeof(IReadOnlyList<AuthorizationAuthority.Contracts.V1.StagedContentItem>), typeof(IReadOnlyList<AuthorizationAuthority.Contracts.V1.SearchIndexBackfillItem>) };
        foreach (var property in requests.SelectMany(t => t.GetProperties()))
        {
            Assert.True(allowed.Contains(property.PropertyType), $"{property.DeclaringType!.Name}.{property.Name} has type {property.PropertyType.Name}");
            Assert.DoesNotMatch("(?i)posting|entry|seal|coverage|value|status|complete", property.Name);
        }
    }

    /// <summary>IX-2: the builder plane demands the builder credential, and the builder port is registered by the Worker host alone.</summary>
    [Fact]
    public void TheBuilderPlaneIsSeparatedLikeTheVerifierPlane()
    {
        var endpoints = AuthoritySources().Single(s => s.Path.EndsWith("Features/SearchIndex/SearchIndexBuilderEndpoints.cs", StringComparison.Ordinal)).Text;
        Assert.Contains("MapGroup(InternalApi.Prefix + \"/search-index-builder\").WithTags(\"SearchIndex\").RequireAuthorization(InternalAuthentication.SearchIndexBuilderPolicy)", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapGroup(InternalApi.Prefix + \"/search-index\").WithTags(\"SearchIndex\").RequireAuthorization(InternalAuthentication.WorkloadPolicy)", endpoints, StringComparison.Ordinal);
        var backend = RepositoryPaths.Combine("src", "backend");
        var registrations = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(".AddSearchIndexBuilder(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(backend, f).Replace('\\', '/')).ToList();
        Assert.Equal(["EnterpriseForms.Worker/Program.cs"], registrations);
    }

    /// <summary>
    /// C-135 (PD-9, design §8.2, §22.4): no posting-table index in the model is keyed on a value column. Every posting index must lead
    /// with (organization_id, form_record_id), so the per-record candidate probe cannot be served by a skip scan of a value index.
    /// The migration AuthoritySearchIndexIx4DropValueIndexes is forward-only and also drops the raw-SQL trigram index.
    /// </summary>
    [Fact]
    public void NoPostingIndexIsKeyedOnAValueColumn()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new AuthorityDbContext(options);
        var postings = context.Model.GetEntityTypes().Where(e => e.GetSchema() == SearchIndexSchema.Name && (e.GetTableName() ?? string.Empty).EndsWith("_postings", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, postings.Count);
        foreach (var entity in postings)
        {
            foreach (var index in entity.GetIndexes())
            {
                var columns = index.Properties.Select(p => p.GetColumnName()).ToList();
                Assert.True(columns.Count >= 2 && columns[0] == "organization_id" && columns[1] == "form_record_id", $"{entity.GetTableName()}.{index.GetDatabaseName()}: ({string.Join(", ", columns)})");
                Assert.DoesNotContain(columns, c => c is "normalized_string_value" or "integer_value" or "decimal_value" or "date_value" or "date_time_value" or "boolean_value" or "field_option_id");
            }
        }

        var migration = File.ReadAllText(Directory.EnumerateFiles(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations"), "*_AuthoritySearchIndexIx4DropValueIndexes.cs").Single());
        Assert.Contains("ix_search_string_postings_trigram", migration, StringComparison.Ordinal);
        Assert.Contains("throw new NotSupportedException", migration, StringComparison.Ordinal); // forward-only
    }

    [Fact]
    public void SearchSchemaTablesFollowTheNamingRules()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new AuthorityDbContext(options);
        var searchTables = context.Model.GetEntityTypes().Where(e => e.GetSchema() == SearchIndexSchema.Name).Select(e => e.GetTableName()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SearchIndexSchema.DerivedTables.Concat(SearchIndexSchema.StateTables).Concat(SearchIndexSchema.ResultTables).Order(StringComparer.Ordinal), searchTables.Order(StringComparer.Ordinal));
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

        Assert.Single(statements, s => (s.StartsWith("GRANT UPDATE", StringComparison.Ordinal) || s.Contains(", UPDATE", StringComparison.Ordinal)) && SearchIndexSchema.DerivedTables.Any(t => s.Contains("." + t + " ", StringComparison.Ordinal)));
        // IX-3 (D-IX3-2): the only other UPDATE in the search schema is the result snapshot's served-on column.
        Assert.Equal(
            ["GRANT UPDATE (last_seal_verified_on, last_seal_attempted_on, last_seal_attempt_outcome_code, last_deep_verified_on, last_deep_attempted_on, last_deep_attempt_outcome_code) ON authority_search.search_record_entries TO runtime_role",
             "GRANT UPDATE (last_page_served_on) ON authority_search.search_result_snapshots TO runtime_role"],
            statements.Where(s => s.StartsWith("GRANT UPDATE", StringComparison.Ordinal)).ToList());
        Assert.DoesNotContain(statements, s => s.Contains(", UPDATE", StringComparison.Ordinal) && SearchIndexSchema.ResultTables.Any(t => s.Contains("." + t + " ", StringComparison.Ordinal)));
        // IX-2 adds the S-C scope timestamp and its observability attempt (§17.10 R3) to the column-limited grant; nothing else.
        Assert.Contains("GRANT UPDATE (last_seal_verified_on, last_seal_attempted_on, last_seal_attempt_outcome_code, last_deep_verified_on, last_deep_attempted_on, last_deep_attempt_outcome_code) ON authority_search.search_record_entries TO runtime_role", statements);
        Assert.DoesNotContain(statements, s => s.Contains("DELETE", StringComparison.Ordinal) && SearchIndexSchema.StateTables.Any(t => s.Contains(t, StringComparison.Ordinal)));
        Assert.Contains("ALTER ROLE authority_search_reader SET default_transaction_read_only = on", statements);
        Assert.DoesNotContain(statements, s => s.Contains("authority_search_reader", StringComparison.Ordinal) && Regex.IsMatch(s, @"\b(INSERT|UPDATE|DELETE)\b"));
    }
    /// <summary>
    /// IX-3 (§5.1, §8.2, IX-56): the query path's SQL runs only on the reader login (its own connection string, refused when equal to
    /// the runtime one); the candidate statement exists only in the reader; the service that runs page checks holds no
    /// instance-local cache or mutable static state, so an invalidation committed through any instance applies everywhere.
    /// </summary>
    [Fact]
    public void TheIndexedQueryUsesTheReaderLoginAndHoldsNoInstanceLocalState()
    {
        var composition = File.ReadAllText(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Api", "Composition", "SearchIndexComposition.cs"));
        Assert.Contains("GetConnectionString(\"AuthoritySearchReader\")", composition, StringComparison.Ordinal);
        Assert.Contains("distinct from the runtime connection", composition, StringComparison.Ordinal);
        Assert.Equal(["AuthorizationAuthority.Api/Composition/SearchIndexComposition.cs"], Matching(new Regex(@"new SearchQueryReader\(")));
        Assert.Equal(["AuthorizationAuthority.Infrastructure/Persistence/SearchQueryReader.cs"], Matching(new Regex(@"unnest\(@scope::uuid\[\]\)")));
        Assert.Contains("AuthorizationAuthority.Infrastructure/Persistence/SearchQueryReader.cs", Matching(new Regex(@"SET TRANSACTION READ ONLY")));

        var service = typeof(IndexedSearchService);
        var instanceFields = service.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.All(instanceFields, f => Assert.True(f.IsInitOnly || f.Name.StartsWith('<'), $"{f.Name} is mutable instance state"));
        Assert.DoesNotContain(instanceFields, f => f.FieldType.Name.Contains("Dictionary", StringComparison.Ordinal) || f.FieldType.Name.Contains("Cache", StringComparison.Ordinal) || f.FieldType.Name.Contains("Concurrent", StringComparison.Ordinal));
        var staticFields = service.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Where(f => !f.IsLiteral).ToList();
        Assert.All(staticFields, f => Assert.True(f.IsInitOnly && (f.FieldType == typeof(string[]) || f.FieldType == typeof(string)), $"{f.Name} is mutable static state"));
        Assert.DoesNotContain(service.GetConstructors().Single().GetParameters(), p => p.ParameterType.Name.Contains("Cache", StringComparison.Ordinal));
    }

    /// <summary>IX-3 (FD-2 X1/X2, D-IX3-3): the invalidating events delete result snapshots in their own transactions, by trigger, on the exact tables.</summary>
    [Fact]
    public void TheIx3MigrationInstallsTheSnapshotInvalidationTriggersOnTheInvalidatingTables()
    {
        var migration = File.ReadAllText(Directory.EnumerateFiles(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "Migrations"), "*_AuthoritySearchIndexIx3.cs").Single());
        foreach (var (trigger, table) in new[]
                 {
                     ("trg_search_result_snapshots_organization_state", "ON authority.organization_authorization_states"),
                     ("trg_search_result_snapshots_projection_state", "ON authority_search.search_projection_states"),
                     ("trg_search_result_snapshots_approval", "ON authority.field_search_approvals"),
                     ("trg_search_result_snapshots_coverage", "ON authority_search.search_field_coverages"),
                     ("trg_search_result_snapshots_session", "ON authority.authenticated_sessions"),
                 })
        {
            var at = migration.IndexOf("CREATE TRIGGER " + trigger, StringComparison.Ordinal);
            Assert.True(at >= 0, trigger);
            Assert.Contains(table, migration.Substring(at, 400), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("SECURITY DEFINER", migration, StringComparison.Ordinal);
        Assert.Contains("UPDATE authority_search.search_field_coverages SET coverage_state_name = 'Building' WHERE coverage_state_name = 'Complete'", migration, StringComparison.Ordinal);
    }
}
