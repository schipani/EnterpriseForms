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
        "form_record_revisions", "field_string_values", "field_integer_values", "field_decimal_values", "field_date_values", "field_date_time_values", "field_guid_values", "field_boolean_values", "field_option_values", "field_encrypted_values",
        "form_versions", "field_definitions", "audit_events", "file_attachment_versions", "group_definitions", "form_record_group_revisions",
        "form_record_creation_attempts",
        // Phase C8 (C8-M1-01/02): durable team-membership operations and the per-pair roster watermark. Insert plus the
        // column-level UPDATE below only: identity columns are never updatable by the runtime role, and nothing is deletable.
        "authority_command_operations", "team_membership_projection_states",
    ];

    private static readonly string[] ProjectionTables =
    [
        "tenants", "users", "teams", "team_memberships", "form_definitions", "form_records", "record_shares", "record_share_fields",
        "form_drafts", "field_options", "option_lists", "option_list_items", "form_relationship_definitions", "form_record_relationships",
        "upload_sessions", "file_attachments", "document_processing_jobs",
        "organization_brandings", "user_ui_preferences", "organization_security_policies",
        "field_protection_migrations", "field_protection_migration_items", "field_protection_reconciliations",
    ];

    /// <summary>
    /// Narrow column-level UPDATE on otherwise insert-only tables (G08 least privilege): a field's catalog label and
    /// description are editable metadata; its key, data type, classification and identity can never be changed by the
    /// runtime role, even by a compromised API process.
    /// </summary>
    private static readonly Dictionary<string, string[]> ColumnUpdateGrants = new(StringComparer.Ordinal)
    {
        // Phase C8: mutable state of an operation; schema-owned triggers additionally freeze completion fields once terminal and
        // allow authority_command_id / committed_authorization_revision to change only from NULL to a value.
        ["authority_command_operations"] =
        [
            "authority_command_id", "committed_authorization_revision", "operation_state_name", "operation_outcome_code", "rejection_reason_code",
            "evaluated_projection_revision", "projected_membership_revision", "projected_membership_present", "superseded_by_later_revision",
            "next_attempt_on", "attempt_count", "consecutive_error_count", "lease_owner_name", "lease_expires_on", "first_not_found_on",
            "needs_attention_flagged_on", "last_reconciliation_error_code", "late_commit_sweep_until", "late_commit_detected_on",
            "operation_reviewed_on", "operation_reviewed_by_user_id", "operation_completed_on", "operation_modified_on",
        ],
        // Phase C8 (R-2): the three state columns and the modification time; a schema-owned trigger refuses lowering the revision (I-3).
        ["team_membership_projection_states"] = ["projected_membership_present", "projected_membership_revision", "last_authority_command_operation_id", "projection_state_modified_on"],
        ["field_definitions"] = ["field_display_name", "field_description", "field_definition_revision", "modified_on", "modified_by_user_id", "field_classification", "field_protection_mode", "protection_policy_revision", "protection_changed_on", "protection_changed_by_user_id", "protection_authority_command_id"],
    };

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
        statements.AddRange(ColumnUpdateGrants.Select(grant => $"GRANT UPDATE ({string.Join(", ", grant.Value)}) ON public.{grant.Key} TO {runtimeRole}"));
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
