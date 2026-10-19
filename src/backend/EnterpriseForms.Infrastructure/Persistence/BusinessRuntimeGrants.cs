using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// Least-privilege grants for the business API runtime role (business-postgres-privileges.reference.sql, G08).
/// Signed staged content is insert-only for the runtime; projection tables allow update; nothing allows DELETE or DDL.
/// A compromised runtime credential therefore cannot erase staged revisions; a DBA still can (and is detected by the
/// authority head/manifest, never prevented — PRIVILEGED_POSTGRESQL_ACCESS.md §1).
/// </summary>
public static class BusinessRuntimeGrants
{
    private static readonly string[] InsertOnlyTables = ["form_record_revisions", "field_string_values", "form_versions", "field_definitions"];

    private static readonly string[] ProjectionTables = ["tenants", "users", "teams", "team_memberships", "form_definitions", "form_records", "record_shares", "record_share_fields"];

    public static void Apply(DbContext context, string runtimeRole)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!System.Text.RegularExpressions.Regex.IsMatch(runtimeRole, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException("runtime role must be a plain lowercase identifier", nameof(runtimeRole));
        }

        var statements = new List<string>
        {
            "REVOKE CREATE ON SCHEMA public FROM PUBLIC",
            $"GRANT USAGE ON SCHEMA public TO {runtimeRole}",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA public FROM {runtimeRole}",
        };
        statements.AddRange(InsertOnlyTables.Select(t => $"GRANT SELECT, INSERT ON public.{t} TO {runtimeRole}"));
        statements.AddRange(ProjectionTables.Select(t => $"GRANT SELECT, INSERT, UPDATE ON public.{t} TO {runtimeRole}"));
        statements.Add($"GRANT SELECT ON public.\"__EFMigrationsHistory\" TO {runtimeRole}");
        foreach (var statement in statements)
        {
#pragma warning disable EF1002 // identifiers validated above; never request input
            context.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations</c>; never connects.</summary>
public sealed class BusinessDesignTimeFactory : IDesignTimeDbContextFactory<BusinessDbContext>
{
    public BusinessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>()
            .UseNpgsql("Host=design-time.invalid;Database=unused;Username=unused;Password=unused")
            .Options;
        return new BusinessDbContext(options);
    }
}
