using Microsoft.EntityFrameworkCore;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// Least-privilege grants for the authority runtime role (authority-postgres-privileges.reference.sql, G08). The runtime
/// role owns nothing, has no DDL, and cannot DELETE from the journal/command tables (append-only to the runtime).
/// Applied by the migration identity after <c>Migrate()</c>; the role itself is created by the cluster bootstrap.
/// </summary>
public static class AuthorityRuntimeGrants
{
    /// <summary>R-53 (C-148): tables the runtime may only read and insert (no UPDATE, no DELETE). E3-M2e (L-8): the data-key ledger.</summary>
    public static readonly string[] InsertOnlyTables = ["content_item_identities", "content_data_key_bindings"];

    private static readonly string[] AppendOnlyTables =
    [
        "security_commands", "security_journal_entries", "authority_relationship_edges", "projection_outbox_events",
        "record_manifest_commits", "record_manifest_leaves", "content_integrity_checkpoints", "content_candidate_leaves",
        "search_catalogue_snapshot_entries", "published_form_versions", "protection_census_results", "protection_reconciliation_attestations",
        "platform_journal_entries", "platform_operator_evidence_uses",
    ];

    private static readonly string[] MutableTables =
    [
        "organization_authorization_states", "identity_bindings", "authority_resources", "record_content_heads", "content_change_operations", "integrity_key_registrations",
        "organization_protection_policies", "search_catalogue_snapshots", "authenticated_sessions", "field_search_approvals",
        "organization_role_change_proposals", "organization_membership_requests",
        "platform_operators", "platform_operator_change_proposals", "platform_operator_approvals", "platform_journal_heads",
        "organization_onboarding_requests", "organization_onboarding_owners", "organization_onboarding_approvals",
        "organization_owner_recovery_cases", "organization_owner_recovery_subjects", "organization_owner_recovery_approvals", "organization_recovered_owner_probations",
    ];

    /// <summary>IX-1: the NOLOGIN read-only role of the future indexed-search query path (IX-3). Created here; holds no credential.</summary>
    public const string SearchReaderRole = "authority_search_reader";

    /// <summary>
    /// IX-1/IX-2: columns of search_record_entries the runtime may update — the S-B and (IX-2) S-C scope timestamps and their
    /// observability attempts only; everything else is delete-and-insert.
    /// </summary>
    public static readonly string[] SearchEntryUpdatableColumns = ["last_seal_verified_on", "last_seal_attempted_on", "last_seal_attempt_outcome_code", "last_deep_verified_on", "last_deep_attempted_on", "last_deep_attempt_outcome_code"];

    /// <summary>Tables of schema authority that the search reader may read (design §5.1).</summary>
    public static readonly string[] SearchReaderAuthorityTables =
    [
        "record_content_heads", "authority_resources", "record_manifest_commits", "record_manifest_leaves", "organization_authorization_states", "field_search_approvals", "authority_relationship_edges",
    ];

    /// <summary>Every table the runtime role may touch; a model table missing here is a startup defect caught by the unit test, not in Docker.</summary>
    public static IReadOnlyCollection<string> GrantedTables => [.. AppendOnlyTables, .. InsertOnlyTables, .. MutableTables, .. Configurations.SearchIndexSchema.DerivedTables, .. Configurations.SearchIndexSchema.StateTables, .. Configurations.SearchIndexSchema.ResultTables, .. Configurations.SearchAccessSchema.AllTables];

    /// <param name="searchReaderLogin">IX-3: the LOGIN user of the indexed-search query path (created by the cluster bootstrap with its
    /// own credential, never by a migration); it is made a member of <see cref="SearchReaderRole"/> and gets the reader's role settings.
    /// Null or empty: no reader login (the query path is then unconfigured and fails closed).</param>
    public static void Apply(DbContext context, string runtimeRole, string? searchReaderLogin = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!System.Text.RegularExpressions.Regex.IsMatch(runtimeRole, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException("runtime role must be a plain lowercase identifier", nameof(runtimeRole));
        }

        if (!string.IsNullOrEmpty(searchReaderLogin) && (!System.Text.RegularExpressions.Regex.IsMatch(searchReaderLogin, "^[a-z_][a-z0-9_]{0,62}$") || searchReaderLogin == runtimeRole || searchReaderLogin == SearchReaderRole))
        {
            throw new ArgumentException("the search reader login must be a plain lowercase identifier distinct from the runtime role and the reader role", nameof(searchReaderLogin));
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
        foreach (var table in InsertOnlyTables)
        {
            statements.Add($"GRANT SELECT, INSERT ON {schema}.{table} TO {runtimeRole}"); // R-53: never updated or deleted
        }
        statements.AddRange(SearchIndexStatements(runtimeRole));
        statements.AddRange(SearchAccessStatements(runtimeRole));
        if (!string.IsNullOrEmpty(searchReaderLogin))
        {
            statements.AddRange(SearchReaderLoginStatements(searchReaderLogin));
        }
        foreach (var statement in statements)
        {
#pragma warning disable EF1002 // identifiers are validated by regex above and come from configuration, never from request input
            context.Database.ExecuteSqlRaw(statement);
#pragma warning restore EF1002
        }
    }

    /// <summary>
    /// IX-5a (C-147): grants on schema authority_search_access. The runtime (the maintainer) inserts and deletes derived rows and
    /// inserts and compare-and-set updates checkpoints; nothing is updated in place otherwise, and it holds no DDL. The search
    /// reader gets nothing in this schema: no search path can read Tier 2 rows in IX-5a.
    /// </summary>
    public static IEnumerable<string> SearchAccessStatements(string runtimeRole)
    {
        var access = Configurations.SearchAccessSchema.Name;
        yield return $"REVOKE ALL ON SCHEMA {access} FROM PUBLIC";
        yield return $"GRANT USAGE ON SCHEMA {access} TO {runtimeRole}";
        yield return $"REVOKE ALL ON ALL TABLES IN SCHEMA {access} FROM {runtimeRole}";
        foreach (var table in Configurations.SearchAccessSchema.DerivedTables)
        {
            yield return $"GRANT SELECT, INSERT, DELETE ON {access}.{table} TO {runtimeRole}";
        }

        foreach (var table in Configurations.SearchAccessSchema.StateTables)
        {
            yield return $"GRANT SELECT, INSERT, UPDATE ON {access}.{table} TO {runtimeRole}";
        }

        // IX-5 M1/M2: the verification state is replaced in place by the sweep and deleted by rebuild and quarantine.
        foreach (var table in Configurations.SearchAccessSchema.VerificationTables)
        {
            yield return $"GRANT SELECT, INSERT, UPDATE, DELETE ON {access}.{table} TO {runtimeRole}";
        }

        // IX-5c sampling evidence: appended and purged by retention; never updated.
        foreach (var table in Configurations.SearchAccessSchema.EvidenceTables)
        {
            yield return $"GRANT SELECT, INSERT, DELETE ON {access}.{table} TO {runtimeRole}";
        }

        yield return $"REVOKE ALL ON FUNCTION {access}.current_organization_id() FROM PUBLIC";
        yield return $"GRANT EXECUTE ON FUNCTION {access}.current_organization_id() TO {runtimeRole}";
        yield return $"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{SearchReaderRole}') THEN EXECUTE 'REVOKE ALL ON SCHEMA {access} FROM {SearchReaderRole}'; EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA {access} FROM {SearchReaderRole}'; END IF; END $$";
    }

    /// <summary>
    /// IX-1 (design §5.1, R1 §B.6): grants on schema authority_search. The runtime may insert and delete derived rows but never
    /// update them (except the S-B timestamps), may maintain state rows, and holds no DDL. The search reader is SELECT-only and
    /// read-only by default. Neither role's statements, bind parameters or error details reach the server log (§5.3, §17.4).
    /// </summary>
    public static IEnumerable<string> SearchIndexStatements(string runtimeRole)
    {
        var search = Configurations.SearchIndexSchema.Name;
        var reader = SearchReaderRole;
        yield return $"REVOKE ALL ON SCHEMA {search} FROM PUBLIC";
        yield return $"GRANT USAGE ON SCHEMA {search} TO {runtimeRole}";
        yield return $"REVOKE ALL ON ALL TABLES IN SCHEMA {search} FROM {runtimeRole}";
        foreach (var table in Configurations.SearchIndexSchema.DerivedTables)
        {
            yield return $"GRANT SELECT, INSERT, DELETE ON {search}.{table} TO {runtimeRole}";
        }

        yield return $"GRANT UPDATE ({string.Join(", ", SearchEntryUpdatableColumns)}) ON {search}.search_record_entries TO {runtimeRole}";
        foreach (var table in Configurations.SearchIndexSchema.StateTables)
        {
            yield return $"GRANT SELECT, INSERT, UPDATE ON {search}.{table} TO {runtimeRole}";
        }

        // IX-3 (§18.4): result snapshots are inserted and deleted (expiry, invalidation, eviction). The only update is the page's
        // served-on timestamp (D-IX3-2), which also gives the row-lock privilege the purge's FOR UPDATE SKIP LOCKED needs.
        foreach (var table in Configurations.SearchIndexSchema.ResultTables)
        {
            yield return $"GRANT SELECT, INSERT, DELETE ON {search}.{table} TO {runtimeRole}";
        }

        yield return $"GRANT UPDATE (last_page_served_on) ON {search}.search_result_snapshots TO {runtimeRole}";

        yield return $"REVOKE ALL ON FUNCTION {search}.current_organization_id() FROM PUBLIC";
        yield return $"GRANT EXECUTE ON FUNCTION {search}.current_organization_id() TO {runtimeRole}";
        yield return $"REVOKE ALL ON FUNCTION {search}.enforce_content_revision_synchronization() FROM PUBLIC";

        yield return $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{reader}') THEN CREATE ROLE {reader} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS; END IF; END $$";
        yield return $"GRANT USAGE ON SCHEMA {search} TO {reader}";
        yield return $"GRANT USAGE ON SCHEMA {AuthorityDbContext.SchemaName} TO {reader}";
        yield return $"REVOKE ALL ON ALL TABLES IN SCHEMA {search} FROM {reader}";
        yield return $"GRANT SELECT ON ALL TABLES IN SCHEMA {search} TO {reader}";
        foreach (var table in SearchReaderAuthorityTables)
        {
            yield return $"GRANT SELECT ON {AuthorityDbContext.SchemaName}.{table} TO {reader}";
        }

        yield return $"GRANT EXECUTE ON FUNCTION {search}.current_organization_id() TO {reader}";
        yield return $"ALTER ROLE {reader} SET default_transaction_read_only = on";
        foreach (var role in new[] { runtimeRole, reader })
        {
            yield return $"ALTER ROLE {role} SET log_parameter_max_length = 0";
            yield return $"ALTER ROLE {role} SET log_parameter_max_length_on_error = 0";
            yield return $"ALTER ROLE {role} SET log_error_verbosity = 'terse'";
            yield return $"ALTER ROLE {role} SET log_statement = 'none'";
        }
    }

    /// <summary>
    /// IX-3 (design §5.1, D-IX1-4): the reader's LOGIN user. Role settings are per login role, so the read-only default and the
    /// logging settings are applied to the login user too. It must not hold any other role (checked by the privilege test).
    /// </summary>
    public static IEnumerable<string> SearchReaderLoginStatements(string searchReaderLogin)
    {
        yield return $"GRANT {SearchReaderRole} TO {searchReaderLogin}";
        yield return $"ALTER ROLE {searchReaderLogin} SET default_transaction_read_only = on";
        yield return $"ALTER ROLE {searchReaderLogin} SET log_parameter_max_length = 0";
        yield return $"ALTER ROLE {searchReaderLogin} SET log_parameter_max_length_on_error = 0";
        yield return $"ALTER ROLE {searchReaderLogin} SET log_error_verbosity = 'terse'";
        yield return $"ALTER ROLE {searchReaderLogin} SET log_statement = 'none'";
    }
}
