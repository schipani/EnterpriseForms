using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SearchCatalogueAndAuditStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "audit_journal_revision",
                schema: "authority",
                table: "organization_authorization_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "latest_audit_journal_digest",
                schema: "authority",
                table: "organization_authorization_states",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "search_catalogue_snapshots",
                schema: "authority",
                columns: table => new
                {
                    search_catalogue_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_scope_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    requesting_authority_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requesting_workload_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    scoping_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_projection_revision = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    catalogue_entry_count = table.Column<int>(type: "integer", nullable: false),
                    catalogue_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    snapshot_issued_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_invalidated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    snapshot_invalidation_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_catalogue_snapshots", x => x.search_catalogue_snapshot_id);
                    table.CheckConstraint("ck_search_catalogue_entry_count", "catalogue_entry_count >= 0 AND octet_length(catalogue_digest) = 32");
                    table.CheckConstraint("ck_search_catalogue_scope_actor", "(snapshot_scope_kind = 'UserSearch' AND requesting_authority_user_id IS NOT NULL AND scoping_security_command_id IS NULL) OR (snapshot_scope_kind = 'MigrationVerification' AND requesting_workload_name IS NOT NULL AND scoping_security_command_id IS NOT NULL AND requesting_authority_user_id IS NULL)");
                    table.CheckConstraint("ck_search_catalogue_scope_kind", "snapshot_scope_kind IN ('UserSearch','MigrationVerification')");
                    table.ForeignKey(
                        name: "fk_search_catalogue_snapshot_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "search_catalogue_snapshot_entries",
                schema: "authority",
                columns: table => new
                {
                    search_catalogue_snapshot_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    search_catalogue_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalogue_entry_position = table.Column<int>(type: "integer", nullable: false),
                    committed_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    committed_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_head_quarantined = table.Column<bool>(type: "boolean", nullable: false),
                    resource_lifecycle_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_catalogue_snapshot_entries", x => x.search_catalogue_snapshot_entry_id);
                    table.CheckConstraint("ck_search_catalogue_entry_revision", "committed_content_revision >= 0 AND catalogue_entry_position >= 0");
                    table.ForeignKey(
                        name: "fk_search_catalogue_entry_snapshot",
                        column: x => x.search_catalogue_snapshot_id,
                        principalSchema: "authority",
                        principalTable: "search_catalogue_snapshots",
                        principalColumn: "search_catalogue_snapshot_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authority_resources_form_scope",
                schema: "authority",
                table: "authority_resources",
                columns: new[] { "organization_id", "resource_type_name", "parent_resource_entity_id", "resource_lifecycle_state" });

            migrationBuilder.CreateIndex(
                name: "uq_search_catalogue_entry_position",
                schema: "authority",
                table: "search_catalogue_snapshot_entries",
                columns: new[] { "search_catalogue_snapshot_id", "catalogue_entry_position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_search_catalogue_entry_record",
                schema: "authority",
                table: "search_catalogue_snapshot_entries",
                columns: new[] { "search_catalogue_snapshot_id", "form_record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_catalogue_snapshots_expiry",
                schema: "authority",
                table: "search_catalogue_snapshots",
                columns: new[] { "organization_id", "snapshot_expires_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_catalogue_snapshot_entries",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "search_catalogue_snapshots",
                schema: "authority");

            migrationBuilder.DropIndex(
                name: "ix_authority_resources_form_scope",
                schema: "authority",
                table: "authority_resources");

            migrationBuilder.DropColumn(
                name: "audit_journal_revision",
                schema: "authority",
                table: "organization_authorization_states");

            migrationBuilder.DropColumn(
                name: "latest_audit_journal_digest",
                schema: "authority",
                table: "organization_authorization_states");
        }
    }
}
