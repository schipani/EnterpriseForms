using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// <c>dotnet AuthorizationAuthority.Api.dll --migrate</c>: applies EF migrations with the migration/admin credential and
/// exits. The runtime container never runs with DDL rights (PRIVILEGED_POSTGRESQL_ACCESS.md §3): Compose runs this as a
/// one-shot service with the admin connection string, then starts the API with the runtime role.
/// </summary>
public static class MigrateCommand
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (Array.IndexOf(args, "--migrate") < 0)
        {
            return false;
        }

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        var connectionString = configuration.GetConnectionString("AuthorityDbMigration") ?? configuration.GetConnectionString("AuthorityDb");
        var runtimeRole = configuration["Database:RuntimeRoleName"] ?? "authority_runtime";
        var searchReaderLogin = configuration["Database:SearchReaderLoginName"]; // IX-3; absent: no indexed query path
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ConnectionStrings__AuthorityDbMigration is required for --migrate");
            exitCode = 2;
            return true;
        }

        var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(connectionString, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
        using var context = new AuthorityDbContext(options);
        context.Database.Migrate();
        AuthorityRuntimeGrants.Apply(context, runtimeRole, searchReaderLogin);
        Console.WriteLine($"Authority migrations applied; runtime grants applied to role '{runtimeRole}'" + (string.IsNullOrEmpty(searchReaderLogin) ? "." : $"; search reader login '{searchReaderLogin}'."));
        return true;
    }
}
