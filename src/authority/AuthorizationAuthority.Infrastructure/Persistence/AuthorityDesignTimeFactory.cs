using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>; never connects (model-only placeholder connection string).</summary>
public sealed class AuthorityDesignTimeFactory : IDesignTimeDbContextFactory<AuthorityDbContext>
{
    public AuthorityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthorityDbContext>()
            .UseNpgsql("Host=design-time.invalid;Database=unused;Username=unused;Password=unused", n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName))
            .Options;
        return new AuthorityDbContext(options);
    }
}
