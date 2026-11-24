using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityC188RetainedFieldSearchApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approval_scope_name",
                schema: "authority",
                table: "field_search_approvals",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "CurrentVersion");

            migrationBuilder.AddColumn<int>(
                name: "source_form_version_number",
                schema: "authority",
                table: "field_search_approvals",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "field_form_bindings",
                schema: "authority",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    binding_source_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    bound_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_form_bindings", x => new { x.organization_id, x.field_definition_id });
                    table.CheckConstraint("ck_field_form_binding_command", "binding_source_name = 'Backfill' OR bound_by_security_command_id IS NOT NULL");
                    table.CheckConstraint("ck_field_form_binding_source", "binding_source_name IN ('Backfill','VersionRegistration','ProtectionCommand')");
                    table.ForeignKey(
                        name: "fk_field_form_binding_command",
                        column: x => x.bound_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id");
                    table.ForeignKey(
                        name: "fk_field_form_binding_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_search_approval_scope",
                schema: "authority",
                table: "field_search_approvals",
                sql: "approval_scope_name IN ('CurrentVersion','RetainedField')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_search_approval_source_version",
                schema: "authority",
                table: "field_search_approvals",
                sql: "(approval_scope_name = 'RetainedField') = (source_form_version_number IS NOT NULL) AND (source_form_version_number IS NULL OR source_form_version_number >= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_field_form_bindings_bound_by_security_command_id",
                schema: "authority",
                table: "field_form_bindings",
                column: "bound_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_form_bindings_form",
                schema: "authority",
                table: "field_form_bindings",
                columns: new[] { "organization_id", "form_definition_id" });

            // C-188 (D-H7, D-H17): bind every field the Authority already knows to its one form - from registered manifests and from the
            // parents of its protection resources. If any field is already associated with more than one form, the migration stops (the
            // whole transaction rolls back; nothing is rewritten or repaired) and the conflict goes to owner review.
            migrationBuilder.Sql("""
                DO $$
                DECLARE conflicting integer;
                BEGIN
                    WITH candidates AS (
                        SELECT v.organization_id, (e->>'field_definition_id')::uuid AS field_definition_id, v.form_definition_id
                        FROM authority.published_form_versions v CROSS JOIN LATERAL jsonb_array_elements(v.field_manifest_json) e
                        UNION
                        SELECT r.organization_id, r.resource_entity_id, r.parent_resource_entity_id
                        FROM authority.authority_resources r
                        WHERE r.resource_type_name IN ('field_protection_requirement', 'field_protection_release', 'field_reclassification')
                          AND r.parent_resource_entity_id IS NOT NULL)
                    SELECT count(*) INTO conflicting FROM (
                        SELECT organization_id, field_definition_id FROM candidates GROUP BY organization_id, field_definition_id
                        HAVING count(DISTINCT form_definition_id) > 1) conflicts;
                    IF conflicting > 0 THEN
                        RAISE EXCEPTION 'C-188: % field definition(s) are associated with more than one form; migration stopped for owner review, nothing was changed', conflicting
                            USING ERRCODE = 'check_violation';
                    END IF;

                    INSERT INTO authority.field_form_bindings (organization_id, field_definition_id, form_definition_id, binding_source_name, bound_by_security_command_id, bound_on)
                    SELECT organization_id, field_definition_id, min(form_definition_id::text)::uuid, 'Backfill', NULL, now()
                    FROM (
                        SELECT v.organization_id, (e->>'field_definition_id')::uuid AS field_definition_id, v.form_definition_id
                        FROM authority.published_form_versions v CROSS JOIN LATERAL jsonb_array_elements(v.field_manifest_json) e
                        UNION
                        SELECT r.organization_id, r.resource_entity_id, r.parent_resource_entity_id
                        FROM authority.authority_resources r
                        WHERE r.resource_type_name IN ('field_protection_requirement', 'field_protection_release', 'field_reclassification')
                          AND r.parent_resource_entity_id IS NOT NULL) candidates
                    GROUP BY organization_id, field_definition_id;
                END
                $$;
                """);

            // C-188 compatibility fence (D-H11): a RetainedField approval is visible only to sessions that declare they understand it
            // (FieldSearchScopeConnection: per-connection startup option, never a role default). An Authority binary built before C-188
            // never declares it, so it cannot see, honour, index, convert or update a retained approval - it fails closed. Existing
            // (CurrentVersion) rows stay visible to every session, so older binaries keep working on them unchanged.
            migrationBuilder.Sql("""
                ALTER TABLE authority.field_search_approvals ENABLE ROW LEVEL SECURITY;
                ALTER TABLE authority.field_search_approvals FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_field_search_approvals_scope_fence ON authority.field_search_approvals
                    USING (approval_scope_name = 'CurrentVersion' OR current_setting('authority.field_search_scope_aware', true) = 'on')
                    WITH CHECK (approval_scope_name = 'CurrentVersion' OR current_setting('authority.field_search_scope_aware', true) = 'on');
                """);

            // C-188 (D-H15): no revival. A Withdrawn or Invalidated approval is final (no column may change); an Active approval keeps its
            // identity and approved snapshot (only its state, reason, change stamps, carried-over version and digest, and - for a retained
            // approval returning unchanged - its scope may change); a current-version approval never becomes a retained one.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.guard_field_search_approval_transition() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF OLD.approval_state_name <> 'Active' THEN
                        RAISE EXCEPTION 'C-188: a % field search approval is final and cannot change', OLD.approval_state_name USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.field_search_approval_id IS DISTINCT FROM OLD.field_search_approval_id
                       OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
                       OR NEW.form_definition_id IS DISTINCT FROM OLD.form_definition_id
                       OR NEW.field_definition_id IS DISTINCT FROM OLD.field_definition_id
                       OR NEW.approved_field_data_type IS DISTINCT FROM OLD.approved_field_data_type
                       OR NEW.approved_field_classification IS DISTINCT FROM OLD.approved_field_classification
                       OR NEW.approved_protection_mode IS DISTINCT FROM OLD.approved_protection_mode
                       OR NEW.approved_operators_json IS DISTINCT FROM OLD.approved_operators_json
                       OR NEW.approved_field_label_snapshot IS DISTINCT FROM OLD.approved_field_label_snapshot
                       OR NEW.approved_option_snapshot_json IS DISTINCT FROM OLD.approved_option_snapshot_json
                       OR NEW.approval_reference_text IS DISTINCT FROM OLD.approval_reference_text
                       OR NEW.approved_by_security_command_id IS DISTINCT FROM OLD.approved_by_security_command_id
                       OR NEW.approved_on IS DISTINCT FROM OLD.approved_on THEN
                        RAISE EXCEPTION 'C-188: the identity and approved snapshot of a field search approval are immutable' USING ERRCODE = 'check_violation';
                    END IF;
                    IF OLD.approval_scope_name = 'CurrentVersion' AND NEW.approval_scope_name <> 'CurrentVersion' THEN
                        RAISE EXCEPTION 'C-188: a current-version approval never becomes a retained one' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.approval_scope_name = 'RetainedField' AND NEW.source_form_version_number IS DISTINCT FROM OLD.source_form_version_number THEN
                        RAISE EXCEPTION 'C-188: the source version of a retained approval is immutable' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER trg_field_search_approval_transition
                    BEFORE UPDATE ON authority.field_search_approvals
                    FOR EACH ROW EXECUTE FUNCTION authority.guard_field_search_approval_transition();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Development/Test only. Production rollback never runs Down (journal and audit history reference these rows); it disables
            // retained approvals by configuration and withdraws them through audited commands (docs/PHASE_C9_STATUS.md, C-188 rollback).
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_field_search_approval_transition ON authority.field_search_approvals;
                DROP FUNCTION IF EXISTS authority.guard_field_search_approval_transition();
                DROP POLICY IF EXISTS rls_field_search_approvals_scope_fence ON authority.field_search_approvals;
                ALTER TABLE authority.field_search_approvals NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE authority.field_search_approvals DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(
                name: "field_form_bindings",
                schema: "authority");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_search_approval_scope",
                schema: "authority",
                table: "field_search_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_search_approval_source_version",
                schema: "authority",
                table: "field_search_approvals");

            migrationBuilder.DropColumn(
                name: "approval_scope_name",
                schema: "authority",
                table: "field_search_approvals");

            migrationBuilder.DropColumn(
                name: "source_form_version_number",
                schema: "authority",
                table: "field_search_approvals");
        }
    }
}
