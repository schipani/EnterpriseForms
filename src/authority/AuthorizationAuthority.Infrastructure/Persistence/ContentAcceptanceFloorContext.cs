using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// E3-M2b-1F: a model-less context that owns ONLY the content acceptance rule floor migrations (folder <c>FloorMigrations</c>).
/// EF discovers a migration for a context by its <c>[DbContext]</c> attribute, so <see cref="AuthorityDbContext"/> — and with it
/// the ordinary <c>--migrate</c> runner, the host and <c>dotnet ef</c> for the Authority context — can never see or apply a floor.
/// The floor is applied only by the explicit operator command (<c>--apply-content-acceptance-floor</c>). It shares the Authority's
/// history table, so the runtime role (which already reads that table) can see that a floor was recorded.
/// </summary>
public sealed class ContentAcceptanceFloorContext(DbContextOptions<ContentAcceptanceFloorContext> options) : DbContext(options)
{
    public static DbContextOptions<ContentAcceptanceFloorContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<ContentAcceptanceFloorContext>()
            .UseNpgsql(connectionString, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName))
            .Options;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(AuthorityDbContext.SchemaName); // no entities: the floor migrations are SQL only
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations ... --context ContentAcceptanceFloorContext</c>; never connects.</summary>
public sealed class ContentAcceptanceFloorDesignTimeFactory : IDesignTimeDbContextFactory<ContentAcceptanceFloorContext>
{
    public ContentAcceptanceFloorContext CreateDbContext(string[] args) =>
        new(ContentAcceptanceFloorContext.OptionsFor("Host=design-time.invalid;Database=unused;Username=unused;Password=unused"));
}
