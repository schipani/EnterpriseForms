using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// RB-CARV-1 amendment A-1 (F-E1, docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §22.14): the source shape of the lock order. The
/// Release B write fence locks the EF migration history (ACCESS SHARE) before the operations table, as every EF migration does, and
/// reads the facts only after both locks; it is the only place that takes the fence's operations lock. The floor command sets its
/// session lock timeout on the open connection before EF's migration lock. The behaviour itself is proven against real PostgreSQL by
/// the F-E1 gate (scripts/dev/e3-m2e1-ledger-pg-gate.ps1 -Mode fe1); these checks keep the shape from silently regressing.
/// </summary>
public sealed class FloorLockOrderTests
{
    private static readonly string Authority = RepositoryPaths.Combine("src", "authority");

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(Authority, relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text[from..to];
    }

    [Fact]
    public void TheFenceLocksTheHistoryBeforeTheOperationsTableAndReadsTheFactsAfterBoth()
    {
        var fence = Between(Source("AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs"),
            "public async Task RequireContentAcceptanceFloorAsync", "public async Task RequireDataKeyLedgerAsync");
        var savepoint = fence.IndexOf("CreateSavepointAsync(FenceSavepoint", StringComparison.Ordinal);
        var history = fence.IndexOf("ExecuteSqlRawAsync(ContentAcceptanceWriteFence.HistoryLockSql", StringComparison.Ordinal);
        var operations = fence.IndexOf("ExecuteSqlRawAsync(ContentAcceptanceWriteFence.LockSql", StringComparison.Ordinal);
        var facts = fence.IndexOf("ContentAcceptanceWriteFence.ReadAsync(", StringComparison.Ordinal);
        Assert.True(savepoint >= 0 && history > savepoint, "the history lock is taken inside the fence savepoint");
        Assert.True(operations > history, "the operations lock follows the history lock");
        Assert.True(facts > operations, "the facts are read after both locks");
    }

    [Fact]
    public void OnlyTheFenceTakesTheFenceOperationsLock()
    {
        var users = Directory.EnumerateFiles(Authority, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ContentAcceptanceWriteFence.LockSql", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Authority, f).Replace('\\', '/'))
            .ToList();
        Assert.Equal(["AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs"], users);
    }

    [Fact]
    public void TheFloorCommandBoundsTheMigrationLockWaitBeforeMigrating()
    {
        var command = Source("AuthorizationAuthority.Api/Composition/ContentAcceptanceFloorCommand.cs");
        var open = command.IndexOf("context.Database.OpenConnection();", StringComparison.Ordinal);
        var timeout = command.IndexOf("context.Database.ExecuteSqlRaw(SessionLockTimeoutSql);", StringComparison.Ordinal);
        var migrate = command.IndexOf(".Migrate(target.MigrationId)", StringComparison.Ordinal);
        Assert.True(open >= 0 && timeout > open, "the session lock timeout is set on the opened connection");
        Assert.True(migrate > timeout, "the lock timeout is set before EF takes its migration lock");
    }
}
