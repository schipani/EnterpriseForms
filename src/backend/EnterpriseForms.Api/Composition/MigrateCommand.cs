using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseForms.Api.Composition;

/// <summary><c>dotnet EnterpriseForms.Api.dll --migrate</c>: applies business migrations with the migration credential and grants the runtime role, then exits.</summary>
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
        var connectionString = configuration.GetConnectionString("BusinessDbMigration") ?? configuration.GetConnectionString("BusinessDb");
        var runtimeRole = configuration["Database:RuntimeRoleName"] ?? "business_api_runtime";
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ConnectionStrings__BusinessDbMigration is required for --migrate");
            exitCode = 2;
            return true;
        }

        using var context = new BusinessDbContext(new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql(connectionString).Options);
        context.Database.Migrate();
        BusinessRuntimeGrants.Apply(context, runtimeRole);
        Console.WriteLine($"Business migrations applied; runtime grants applied to role '{runtimeRole}'.");
        return true;
    }
}
