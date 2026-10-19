using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// The runtime role gets explicit per-table grants (G08). A table added to the model without a grant fails closed in the
/// running stack only (503 "authority store unavailable"), so the coverage is asserted here from the EF model itself.
/// </summary>
public sealed class RuntimeGrantsTests
{
    [Fact]
    public void EveryModelTableHasARuntimeGrant()
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>()
            .UseNpgsql("Host=unit-test.invalid;Database=unused;Username=unused;Password=unused", n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName))
            .Options;
        using var context = new AuthorityDbContext(options);
        var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(t => t is not null).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(tables);
        var missing = tables.Where(t => !AuthorityRuntimeGrants.GrantedTables.Contains(t!, StringComparer.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "model tables without a runtime grant: " + string.Join(", ", missing));
    }
}
