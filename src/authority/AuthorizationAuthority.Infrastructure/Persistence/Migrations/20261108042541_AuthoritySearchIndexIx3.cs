using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchIndexIx3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "created_by_business_user_id",
                schema: "authority_search",
                table: "search_record_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "entry_derivation_version",
                schema: "authority_search",
                table: "search_record_entries",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<byte[]>(
                name: "header_source_canonical_bytes",
                schema: "authority_search",
                table: "search_record_entries",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_form_record_id",
                schema: "authority_search",
                table: "search_record_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "search_result_snapshots",
                schema: "authority_search",
                columns: table => new
                {
                    search_result_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requesting_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authenticated_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_engine_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    search_tier_name = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    snapshot_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_form_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    predicate_field_definition_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    predicate_field_search_approval_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    predicate_operator_names = table.Column<string[]>(type: "text[]", nullable: false),
                    query_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    result_entry_count = table.Column<int>(type: "integer", nullable: false),
                    snapshot_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    creating_instance_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_result_snapshots", x => x.search_result_snapshot_id);
                    table.CheckConstraint("ck_search_result_snapshot_counts", "result_entry_count BETWEEN 0 AND 10000 AND snapshot_expires_on > snapshot_created_on");
                    table.CheckConstraint("ck_search_result_snapshot_digest", "octet_length(query_digest) = 32");
                    table.CheckConstraint("ck_search_result_snapshot_engine", "search_engine_name = 'Indexed' AND search_tier_name = 'Tier1'");
                    table.CheckConstraint("ck_search_result_snapshot_predicates", "cardinality(predicate_field_definition_ids) BETWEEN 1 AND 5 AND cardinality(predicate_field_search_approval_ids) = cardinality(predicate_field_definition_ids) AND cardinality(predicate_operator_names) = cardinality(predicate_field_definition_ids)");
                    table.ForeignKey(
                        name: "fk_search_result_snapshot_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_result_snapshot_entries",
                schema: "authority_search",
                columns: table => new
                {
                    search_result_snapshot_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_result_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_entry_position = table.Column<int>(type: "integer", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_result_snapshot_entries", x => x.search_result_snapshot_entry_id);
                    table.CheckConstraint("ck_search_result_snapshot_entry_position", "result_entry_position >= 0");
                    table.ForeignKey(
                        name: "fk_search_result_snapshot_entry_snapshot",
                        column: x => x.search_result_snapshot_id,
                        principalSchema: "authority_search",
                        principalTable: "search_result_snapshots",
                        principalColumn: "search_result_snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_entry_derivation",
                schema: "authority_search",
                table: "search_record_entries",
                sql: "entry_derivation_version IN (1, 2) AND (entry_derivation_version = 1 OR (octet_length(header_source_canonical_bytes) > 0 AND created_by_business_user_id IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "uq_search_result_snapshot_entry_position",
                schema: "authority_search",
                table: "search_result_snapshot_entries",
                columns: new[] { "search_result_snapshot_id", "result_entry_position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_result_snapshots_expiry",
                schema: "authority_search",
                table: "search_result_snapshots",
                column: "snapshot_expires_on");

            migrationBuilder.CreateIndex(
                name: "ix_search_result_snapshots_form",
                schema: "authority_search",
                table: "search_result_snapshots",
                columns: new[] { "organization_id", "form_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_result_snapshots_instance",
                schema: "authority_search",
                table: "search_result_snapshots",
                column: "creating_instance_name");

            migrationBuilder.CreateIndex(
                name: "ix_search_result_snapshots_revision",
                schema: "authority_search",
                table: "search_result_snapshots",
                columns: new[] { "organization_id", "snapshot_authorization_revision" });

            migrationBuilder.CreateIndex(
                name: "ix_search_result_snapshots_session",
                schema: "authority_search",
                table: "search_result_snapshots",
                columns: new[] { "authenticated_session_id", "snapshot_created_on" });

            // D-IX3-1: entries derived before IX-3 carry no verified header facts. Every Complete coverage returns to Building, so
            // no indexed query can run until the builder has re-derived every entry (IsIndexedAtHead requires derivation version 2)
            // and the Authority has attested the coverage again. Derived state only; no entry, posting or business row is deleted.
            migrationBuilder.Sql("UPDATE authority_search.search_field_coverages SET coverage_state_name = 'Building' WHERE coverage_state_name = 'Complete';");

            // IX-3 (design §17.9.2, FD-2 X1/X2; D-IX3-3): result snapshots are deleted in the very transaction of every invalidating
            // event, by triggers, so no writer can forget it and a failed deletion fails that transaction. Page checks are the second
            // line. SECURITY INVOKER (the writer needs DELETE on the snapshot tables; the runtime role has it).
            migrationBuilder.Sql("""
                CREATE FUNCTION authority_search.invalidate_result_snapshots_for_organization_state() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF NEW.integrity_quarantine_enabled AND NOT OLD.integrity_quarantine_enabled THEN
                        DELETE FROM authority_search.search_result_snapshots WHERE organization_id = NEW.organization_id;
                    ELSIF NEW.authorization_revision > OLD.authorization_revision THEN
                        DELETE FROM authority_search.search_result_snapshots
                        WHERE organization_id = NEW.organization_id AND snapshot_authorization_revision < NEW.authorization_revision;
                    END IF;
                    RETURN NULL;
                END
                $$;
                CREATE TRIGGER trg_search_result_snapshots_organization_state
                    AFTER UPDATE OF authorization_revision, integrity_quarantine_enabled ON authority.organization_authorization_states
                    FOR EACH ROW
                    WHEN (NEW.authorization_revision IS DISTINCT FROM OLD.authorization_revision OR NEW.integrity_quarantine_enabled IS DISTINCT FROM OLD.integrity_quarantine_enabled)
                    EXECUTE FUNCTION authority_search.invalidate_result_snapshots_for_organization_state();

                CREATE FUNCTION authority_search.invalidate_result_snapshots_for_projection_state() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF NEW.projection_status_name <> 'Active' OR NEW.verification_suspended THEN
                        DELETE FROM authority_search.search_result_snapshots WHERE organization_id = NEW.organization_id;
                    END IF;
                    RETURN NULL;
                END
                $$;
                CREATE TRIGGER trg_search_result_snapshots_projection_state
                    AFTER UPDATE OF projection_status_name, verification_suspended ON authority_search.search_projection_states
                    FOR EACH ROW
                    WHEN (NEW.projection_status_name IS DISTINCT FROM OLD.projection_status_name OR NEW.verification_suspended IS DISTINCT FROM OLD.verification_suspended)
                    EXECUTE FUNCTION authority_search.invalidate_result_snapshots_for_projection_state();

                CREATE FUNCTION authority_search.invalidate_result_snapshots_for_searchability() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    -- Any approval change of the form (withdrawal, invalidation, operators, classification, a new approval) and
                    -- any coverage leaving Complete: a conservative superset of the searchability events of design 17.9.2.
                    DELETE FROM authority_search.search_result_snapshots
                    WHERE organization_id = NEW.organization_id AND form_definition_id = NEW.form_definition_id;
                    RETURN NULL;
                END
                $$;
                CREATE TRIGGER trg_search_result_snapshots_approval
                    AFTER INSERT OR UPDATE ON authority.field_search_approvals
                    FOR EACH ROW EXECUTE FUNCTION authority_search.invalidate_result_snapshots_for_searchability();
                CREATE TRIGGER trg_search_result_snapshots_coverage
                    AFTER UPDATE OF coverage_state_name ON authority_search.search_field_coverages
                    FOR EACH ROW
                    WHEN (OLD.coverage_state_name = 'Complete' AND NEW.coverage_state_name <> 'Complete')
                    EXECUTE FUNCTION authority_search.invalidate_result_snapshots_for_searchability();

                CREATE FUNCTION authority_search.invalidate_result_snapshots_for_session() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    DELETE FROM authority_search.search_result_snapshots WHERE authenticated_session_id = NEW.authenticated_session_id;
                    RETURN NULL;
                END
                $$;
                CREATE TRIGGER trg_search_result_snapshots_session
                    AFTER UPDATE OF session_revoked_on ON authority.authenticated_sessions
                    FOR EACH ROW
                    WHEN (NEW.session_revoked_on IS NOT NULL AND OLD.session_revoked_on IS NULL)
                    EXECUTE FUNCTION authority_search.invalidate_result_snapshots_for_session();

                REVOKE ALL ON FUNCTION authority_search.invalidate_result_snapshots_for_organization_state() FROM PUBLIC;
                REVOKE ALL ON FUNCTION authority_search.invalidate_result_snapshots_for_projection_state() FROM PUBLIC;
                REVOKE ALL ON FUNCTION authority_search.invalidate_result_snapshots_for_searchability() FROM PUBLIC;
                REVOKE ALL ON FUNCTION authority_search.invalidate_result_snapshots_for_session() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_search_result_snapshots_session ON authority.authenticated_sessions;
                DROP TRIGGER IF EXISTS trg_search_result_snapshots_coverage ON authority_search.search_field_coverages;
                DROP TRIGGER IF EXISTS trg_search_result_snapshots_approval ON authority.field_search_approvals;
                DROP TRIGGER IF EXISTS trg_search_result_snapshots_projection_state ON authority_search.search_projection_states;
                DROP TRIGGER IF EXISTS trg_search_result_snapshots_organization_state ON authority.organization_authorization_states;
                DROP FUNCTION IF EXISTS authority_search.invalidate_result_snapshots_for_session();
                DROP FUNCTION IF EXISTS authority_search.invalidate_result_snapshots_for_searchability();
                DROP FUNCTION IF EXISTS authority_search.invalidate_result_snapshots_for_projection_state();
                DROP FUNCTION IF EXISTS authority_search.invalidate_result_snapshots_for_organization_state();
                """);

            migrationBuilder.DropTable(
                name: "search_result_snapshot_entries",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_result_snapshots",
                schema: "authority_search");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_entry_derivation",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropColumn(
                name: "created_by_business_user_id",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropColumn(
                name: "entry_derivation_version",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropColumn(
                name: "header_source_canonical_bytes",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropColumn(
                name: "parent_form_record_id",
                schema: "authority_search",
                table: "search_record_entries");
        }
    }
}
