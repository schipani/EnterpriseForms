using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityC10ShareExpiryCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_authority_resource",
                schema: "authority",
                table: "authority_resources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_resource_lifecycle_state",
                schema: "authority",
                table: "authority_resources");

            migrationBuilder.AddColumn<int>(
                name: "maximum_delegation_depth",
                schema: "authority",
                table: "organization_protection_policies",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_security_command_organization_command",
                schema: "authority",
                table: "security_commands",
                columns: new[] { "organization_id", "security_command_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "uq_authority_resource",
                schema: "authority",
                table: "authority_resources",
                columns: new[] { "organization_id", "resource_type_name", "resource_entity_id" });

            migrationBuilder.CreateTable(
                name: "share_resource_metadata",
                schema: "authority",
                columns: table => new
                {
                    share_resource_metadata_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "record_share"),
                    share_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    share_revision = table.Column<long>(type: "bigint", nullable: false),
                    parent_record_share_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delegation_depth = table.Column<int>(type: "integer", nullable: false),
                    expiry_source_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    share_invalidation_reason_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    legacy_lineage_unresolved = table.Column<bool>(type: "boolean", nullable: false),
                    share_metadata_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    share_metadata_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_resource_metadata", x => x.share_resource_metadata_id);
                    table.UniqueConstraint("ak_share_resource_metadata_organization_share", x => new { x.organization_id, x.record_share_id });
                    table.CheckConstraint("ck_share_delegation_depth", "delegation_depth BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_share_depth_parent_coherence", "(delegation_depth = 1 AND parent_record_share_id IS NULL) OR (delegation_depth > 1 AND parent_record_share_id IS NOT NULL)");
                    table.CheckConstraint("ck_share_expiry_source_coherence", "(share_expires_on IS NULL) = (expiry_source_security_command_id IS NULL)");
                    table.CheckConstraint("ck_share_invalidation_reason", "share_invalidation_reason_code IS NULL OR share_invalidation_reason_code IN ('UpstreamExpired','UpstreamRevoked','UpstreamNarrowed','GrantorPathLost','OwnershipTransferred','LineageUnresolvable','DelegationDepthExceeded')");
                    table.CheckConstraint("ck_share_metadata_registry_type", "resource_type_name = 'record_share'");
                    table.CheckConstraint("ck_share_revision_positive", "share_revision >= 1");
                    table.ForeignKey(
                        name: "fk_share_resource_metadata_expiry_source",
                        columns: x => new { x.organization_id, x.expiry_source_security_command_id },
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumns: new[] { "organization_id", "security_command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_share_resource_metadata_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_share_resource_metadata_parent",
                        columns: x => new { x.organization_id, x.parent_record_share_id },
                        principalSchema: "authority",
                        principalTable: "share_resource_metadata",
                        principalColumns: new[] { "organization_id", "record_share_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_share_resource_metadata_registry",
                        columns: x => new { x.organization_id, x.resource_type_name, x.record_share_id },
                        principalSchema: "authority",
                        principalTable: "authority_resources",
                        principalColumns: new[] { "organization_id", "resource_type_name", "resource_entity_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "share_document_bindings",
                schema: "authority",
                columns: table => new
                {
                    share_document_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    can_view_document = table.Column<bool>(type: "boolean", nullable: false),
                    can_download_document = table.Column<bool>(type: "boolean", nullable: false),
                    can_replace_document = table.Column<bool>(type: "boolean", nullable: false),
                    shared_through_version_number = table.Column<int>(type: "integer", nullable: false),
                    bound_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_bound_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_document_bindings", x => x.share_document_binding_id);
                    table.CheckConstraint("ck_share_document_version_positive", "shared_through_version_number >= 1");
                    table.CheckConstraint("ck_share_document_view_implied", "(NOT can_download_document OR can_view_document) AND (NOT can_replace_document OR can_view_document)");
                    table.ForeignKey(
                        name: "fk_share_document_binding_command",
                        columns: x => new { x.organization_id, x.bound_by_security_command_id },
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumns: new[] { "organization_id", "security_command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_share_document_binding_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_share_document_binding_share",
                        columns: x => new { x.organization_id, x.record_share_id },
                        principalSchema: "authority",
                        principalTable: "share_resource_metadata",
                        principalColumns: new[] { "organization_id", "record_share_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_protection_policy_max_depth",
                schema: "authority",
                table: "organization_protection_policies",
                sql: "maximum_delegation_depth BETWEEN 1 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "ck_resource_lifecycle_state",
                schema: "authority",
                table: "authority_resources",
                sql: "resource_lifecycle_state IN ('Registered','Active','Revoked','Quarantined') OR (resource_type_name = 'record_share' AND resource_lifecycle_state IN ('Expired','Invalidated'))");

            migrationBuilder.CreateIndex(
                name: "ix_share_document_bindings_org",
                schema: "authority",
                table: "share_document_bindings",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_share_document_bindings_organization_id_bound_by_security_c~",
                schema: "authority",
                table: "share_document_bindings",
                columns: new[] { "organization_id", "bound_by_security_command_id" });

            migrationBuilder.CreateIndex(
                name: "uq_share_document_binding",
                schema: "authority",
                table: "share_document_bindings",
                columns: new[] { "organization_id", "record_share_id", "file_attachment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_share_resource_metadata_expiry",
                schema: "authority",
                table: "share_resource_metadata",
                columns: new[] { "organization_id", "share_expires_on" },
                filter: "share_expires_on IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_share_resource_metadata_legacy_unresolved",
                schema: "authority",
                table: "share_resource_metadata",
                columns: new[] { "organization_id", "legacy_lineage_unresolved" },
                filter: "legacy_lineage_unresolved");

            migrationBuilder.CreateIndex(
                name: "IX_share_resource_metadata_organization_id_expiry_source_secur~",
                schema: "authority",
                table: "share_resource_metadata",
                columns: new[] { "organization_id", "expiry_source_security_command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_share_resource_metadata_organization_id_resource_type_name_~",
                schema: "authority",
                table: "share_resource_metadata",
                columns: new[] { "organization_id", "resource_type_name", "record_share_id" });

            migrationBuilder.CreateIndex(
                name: "ix_share_resource_metadata_parent",
                schema: "authority",
                table: "share_resource_metadata",
                columns: new[] { "organization_id", "parent_record_share_id" },
                filter: "parent_record_share_id IS NOT NULL");
            // Reject terminal share revival even on updates through the migration identity.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.guard_share_terminal_state_transition() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    -- Only apply to record_share resources.
                    IF OLD.resource_type_name <> 'record_share' THEN
                        RETURN NEW;
                    END IF;

                    -- Terminal states: Expired and Invalidated cannot change to anything else.
                    IF OLD.resource_lifecycle_state IN ('Expired', 'Invalidated') THEN
                        IF NEW.resource_lifecycle_state IS DISTINCT FROM OLD.resource_lifecycle_state THEN
                            RAISE EXCEPTION 'C10 M1-WP1: a % record_share resource is terminal and cannot change state', OLD.resource_lifecycle_state USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;

                    -- Identity and registration columns are always immutable for record_share resources.
                    IF NEW.authority_resource_id IS DISTINCT FROM OLD.authority_resource_id
                       OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
                       OR NEW.resource_type_name IS DISTINCT FROM OLD.resource_type_name
                       OR NEW.resource_entity_id IS DISTINCT FROM OLD.resource_entity_id
                       OR NEW.parent_resource_entity_id IS DISTINCT FROM OLD.parent_resource_entity_id
                       OR NEW.registered_by_security_command_id IS DISTINCT FROM OLD.registered_by_security_command_id
                       OR NEW.resource_registered_on IS DISTINCT FROM OLD.resource_registered_on THEN
                        RAISE EXCEPTION 'C10 M1-WP1: the identity and registration of a record_share resource are immutable' USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER trg_share_terminal_state_transition
                    BEFORE UPDATE ON authority.authority_resources
                    FOR EACH ROW
                    WHEN (OLD.resource_type_name = 'record_share')
                    EXECUTE FUNCTION authority.guard_share_terminal_state_transition();
                REVOKE ALL ON FUNCTION authority.guard_share_terminal_state_transition() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_share_terminal_state_transition ON authority.authority_resources;
                DROP FUNCTION IF EXISTS authority.guard_share_terminal_state_transition();
                """);

            migrationBuilder.DropTable(
                name: "share_document_bindings",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "share_resource_metadata",
                schema: "authority");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_security_command_organization_command",
                schema: "authority",
                table: "security_commands");

            migrationBuilder.DropCheckConstraint(
                name: "ck_protection_policy_max_depth",
                schema: "authority",
                table: "organization_protection_policies");

            migrationBuilder.DropUniqueConstraint(
                name: "uq_authority_resource",
                schema: "authority",
                table: "authority_resources");

            migrationBuilder.DropCheckConstraint(
                name: "ck_resource_lifecycle_state",
                schema: "authority",
                table: "authority_resources");

            migrationBuilder.DropColumn(
                name: "maximum_delegation_depth",
                schema: "authority",
                table: "organization_protection_policies");

            migrationBuilder.CreateIndex(
                name: "uq_authority_resource",
                schema: "authority",
                table: "authority_resources",
                columns: new[] { "organization_id", "resource_type_name", "resource_entity_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_resource_lifecycle_state",
                schema: "authority",
                table: "authority_resources",
                sql: "resource_lifecycle_state IN ('Registered','Active','Revoked','Quarantined')");
        }
    }
}
