using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>
/// Least-privilege grants for the business API runtime role (business-postgres-privileges.reference.sql, G08).
/// Signed staged content and audit events are insert-only for the runtime; projection tables allow update; nothing
/// allows DELETE or DDL. A compromised runtime credential therefore cannot erase staged revisions or audit rows; a DBA
/// still can (and is detected by the authority head/manifest, never prevented — PRIVILEGED_POSTGRESQL_ACCESS.md §1).
/// </summary>
public static class BusinessRuntimeGrants
{
    private static readonly string[] InsertOnlyTables =
    [
        "form_record_revisions", "field_string_values", "field_integer_values", "field_decimal_values", "field_date_values", "field_date_time_values", "field_guid_values", "field_boolean_values", "field_option_values",
        "form_versions", "field_definitions", "audit_events", "file_attachment_versions",
    ];

    private static readonly string[] ProjectionTables =
    [
        "tenants", "users", "teams", "team_memberships", "form_definitions", "form_records", "record_shares", "record_share_fields",
        "form_drafts", "field_options", "form_relationship_definitions", "form_record_relationships",
        "upload_sessions", "file_attachments", "document_processing_jobs",
        "organization_brandings", "user_ui_preferences",
    ];

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
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {runtimeRole}",
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
