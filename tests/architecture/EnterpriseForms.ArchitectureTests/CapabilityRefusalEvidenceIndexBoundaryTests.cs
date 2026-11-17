using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2f-3i (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5A; OD-IX-1 conditions): the source shape of the dedicated index command.
/// It is the only creator of the index, uses the migration identity only, never reaches the ordinary --migrate path, EF migrations,
/// the migration history, runtime grants, organization rows or transactions, bounds its lock waits before the build, executes the
/// build as its own statement, and never drops, rebuilds or repairs anything. The behaviour is proven on disposable PostgreSQL by
/// scripts/dev/e3-m2f3i-index-pg-gate.ps1; these checks keep the shape from silently regressing.
/// </summary>
public sealed class CapabilityRefusalEvidenceIndexBoundaryTests
{
    private const string IndexName = "ix_journal_capability_refusal_evidence";
    private const string CommandFile = "AuthorizationAuthority.Api/Composition/CapabilityRefusalEvidenceIndexCommand.cs";
    private const string DefinitionFile = "AuthorizationAuthority.Infrastructure/Persistence/CapabilityRefusalEvidenceIndex.cs";
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(Authority, relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static IEnumerable<string> AuthoritySources() =>
        Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) => Path.GetRelativePath(Authority, path).Replace('\\', '/');

    [Fact]
    public void TheIndexNameAndItsCreateStatementLiveOnlyInTheDefinition()
    {
        var named = AuthoritySources().Where(f => File.ReadAllText(f).Contains(IndexName, StringComparison.Ordinal)).Select(Relative).ToList();
        Assert.Equal([DefinitionFile], named);
        var creators = AuthoritySources().Where(f => File.ReadAllText(f).Contains("CapabilityRefusalEvidenceIndex.CreateSql", StringComparison.Ordinal)).Select(Relative).ToList();
        Assert.Equal([CommandFile], creators);
    }

    [Fact]
    public void NoEfMigrationOrModelDeclaresTheIndex()
    {
        var persistence = Path.Combine(Authority, "AuthorizationAuthority.Infrastructure", "Persistence");
        foreach (var folder in new[] { "Migrations", "FloorMigrations", "Configurations" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(persistence, folder), "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("capability_refusal", text, StringComparison.Ordinal);
                Assert.DoesNotContain("EnvelopeCapabilityRefused", text, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("AuthorityRuntimeGrants")]
    [InlineData("MigrateCommand")]
    [InlineData(".Migrate(")]
    [InlineData("IMigrator")]
    [InlineData("new AuthorityDbContext")]
    [InlineData("ContentAcceptanceFloorContext")]
    [InlineData("SaveChanges")]
    [InlineData("BeginTransaction")]
    [InlineData("LockOrganization")]
    [InlineData("ef_migrations_history")]
    [InlineData("GetConnectionString(\"AuthorityDb\")")]
    [InlineData("DROP INDEX")]
    [InlineData("REINDEX")]
    [InlineData("ALTER INDEX")]
    [InlineData("GRANT ")]
    [InlineData("REVOKE ")]
    public void TheCommandNeverReachesTheOrdinaryMigrationPathOrRepairsAnything(string forbidden)
    {
        Assert.DoesNotContain(forbidden, Source(CommandFile), StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, Source(DefinitionFile), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandUsesTheMigrationIdentityOnly()
    {
        var command = Source(CommandFile);
        Assert.Contains("GetConnectionString(\"AuthorityDbMigration\")", command, StringComparison.Ordinal);
        Assert.Single(command.Split("GetConnectionString(", StringSplitOptions.None).Skip(1));
    }

    [Fact]
    public void TheCommandBoundsLockWaitsAndTakesItsLockBeforeInspectingAndBuildsInItsOwnStatement()
    {
        var command = Source(CommandFile);
        var run = command[command.IndexOf("private static async Task<int> RunAsync", StringComparison.Ordinal)..];
        var open = run.IndexOf("connection.OpenAsync(", StringComparison.Ordinal);
        var timeout = run.IndexOf("ExecuteAsync(connection, SessionLockTimeoutSql", StringComparison.Ordinal);
        var advisory = run.IndexOf("TryProvisioningLockSql", StringComparison.Ordinal);
        var inspect = run.IndexOf("CapabilityRefusalEvidenceIndex.InspectAsync(", StringComparison.Ordinal);
        var create = run.IndexOf("new NpgsqlCommand(CapabilityRefusalEvidenceIndex.CreateSql, connection)", StringComparison.Ordinal);
        var verify = run.IndexOf("CapabilityRefusalEvidenceIndex.InspectAsync(", create, StringComparison.Ordinal);
        Assert.True(open >= 0 && timeout > open, "the lock timeout is set on the opened connection");
        Assert.True(advisory > timeout, "the provisioning lock is taken after the timeout is set");
        Assert.True(inspect > advisory, "the catalogue is inspected under the provisioning lock");
        Assert.True(create > inspect, "the build follows the inspection, as its own command");
        Assert.True(verify > create, "the result is re-inspected after the build");
        Assert.DoesNotContain(";", Source(DefinitionFile).Split("public const string CreateSql =")[1].Split(";\n")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramDispatchesTheIndexCommandBeforeTheFloorAndMigrateCommands()
    {
        var program = Source("AuthorizationAuthority.Api/Program.cs");
        var index = program.IndexOf("CapabilityRefusalEvidenceIndexCommand.TryRun(", StringComparison.Ordinal);
        var floor = program.IndexOf("ContentAcceptanceFloorCommand.TryRun(", StringComparison.Ordinal);
        var migrate = program.IndexOf("MigrateCommand.TryRun(", StringComparison.Ordinal);
        Assert.True(index >= 0 && floor > index && migrate > index, "the index command is dispatched first so its refusals of --migrate and the floor flags apply");
    }
}
