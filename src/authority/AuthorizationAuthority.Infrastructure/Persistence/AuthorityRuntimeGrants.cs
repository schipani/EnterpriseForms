using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// Least-privilege grants for the authority runtime role (authority-postgres-privileges.reference.sql, G08). The runtime
/// role owns nothing, has no DDL, and cannot DELETE from the journal/command tables (append-only to the runtime).
/// Applied by the migration identity after <c>Migrate()</c>; the role itself is created by the cluster bootstrap.
/// </summary>
public static class AuthorityRuntimeGrants
{
    private static readonly string[] AppendOnlyTables =
    [
        "security_commands", "security_journal_entries", "authority_relationship_edges", "projection_outbox_events",
        "record_manifest_commits", "record_manifest_leaves", "content_integrity_checkpoints", "content_candidate_leaves",
        "search_catalogue_snapshot_entries", "published_form_versions", "protection_census_results", "protection_reconciliation_attestations",
    ];

    private static readonly string[] MutableTables =
    [
        "organization_authorization_states", "identity_bindings", "authority_resources", "record_content_heads", "content_change_operations", "integrity_key_registrations",
        "organization_protection_policies", "search_catalogue_snapshots", "authenticated_sessions", "field_search_approvals",
    ];

    /// <summary>Every table the runtime role may touch; a model table missing here is a startup defect caught by the unit test, not in Docker.</summary>
    public static IReadOnlyCollection<string> GrantedTables => [.. AppendOnlyTables, .. MutableTables];

    public static void Apply(DbContext context, string runtimeRole)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!System.Text.RegularExpressions.Regex.IsMatch(runtimeRole, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException("runtime role must be a plain lowercase identifier", nameof(runtimeRole));
        }

        var schema = AuthorityDbContext.SchemaName;
        var statements = new List<string>
        {
            $"REVOKE ALL ON SCHEMA {schema} FROM PUBLIC",
            $"GRANT USAGE ON SCHEMA {schema} TO {runtimeRole}",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA {schema} FROM {runtimeRole}",
        };
        foreach (var table in AppendOnlyTables)
        {
            statements.Add($"GRANT SELECT, INSERT, UPDATE ON {schema}.{table} TO {runtimeRole}");
        }

        foreach (var table in MutableTables)
        {
            statements.Add($"GRANT SELECT, INSERT, UPDATE ON {schema}.{table} TO {runtimeRole}");
        }

        statements.Add($"GRANT SELECT ON {schema}.ef_migrations_history TO {runtimeRole}");
        foreach (var statement in statements)
        {
#pragma warning disable EF1002 // identifiers are validated by regex above and come from configuration, never from request input
            context.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
    }
}
