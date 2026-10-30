using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FieldSearchApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "plaintext_search_permitted",
                schema: "authority",
                table: "organization_protection_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "search_policy_revision",
                schema: "authority",
                table: "organization_protection_policies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "field_search_approvals",
                schema: "authority",
                columns: table => new
                {
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_form_version_number = table.Column<int>(type: "integer", nullable: false),
                    approved_layout_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    approved_field_data_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_field_classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_protection_mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    approved_operators_json = table.Column<string>(type: "jsonb", nullable: false),
                    approved_field_label_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    approved_option_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    approval_reference_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approval_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    invalidation_reason_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    approved_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state_changed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_search_approvals", x => x.field_search_approval_id);
                    table.CheckConstraint("ck_field_search_approval_classification", "approved_field_classification IN ('Public','Internal')");
                    table.CheckConstraint("ck_field_search_approval_digest", "octet_length(approved_layout_digest) = 32");
                    table.CheckConstraint("ck_field_search_approval_protection", "approved_protection_mode = 'Standard'");
                    table.CheckConstraint("ck_field_search_approval_state", "approval_state_name IN ('Active','Withdrawn','Invalidated')");
                    table.ForeignKey(
                        name: "fk_field_search_approval_command",
                        column: x => x.approved_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_search_approval_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "published_form_versions",
                schema: "authority",
                columns: table => new
                {
                    published_form_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_version_number = table.Column<int>(type: "integer", nullable: false),
                    layout_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    layout_digest_scheme = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    field_manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    registered_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_form_versions", x => x.published_form_version_id);
                    table.CheckConstraint("ck_published_form_version_digest", "octet_length(layout_digest) = 32");
                    table.CheckConstraint("ck_published_form_version_number", "form_version_number >= 1");
                    table.ForeignKey(
                        name: "fk_published_form_version_command",
                        column: x => x.registered_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_published_form_version_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_search_approvals_approved_by_security_command_id",
                schema: "authority",
                table: "field_search_approvals",
                column: "approved_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_search_approvals_field",
                schema: "authority",
                table: "field_search_approvals",
                columns: new[] { "organization_id", "form_definition_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "uq_field_search_approval_active",
                schema: "authority",
                table: "field_search_approvals",
                columns: new[] { "organization_id", "field_definition_id" },
                unique: true,
                filter: "approval_state_name = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_published_form_versions_registered_by_security_command_id",
                schema: "authority",
                table: "published_form_versions",
                column: "registered_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "uq_published_form_version_number",
                schema: "authority",
                table: "published_form_versions",
                columns: new[] { "organization_id", "form_definition_id", "form_version_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_search_approvals",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "published_form_versions",
                schema: "authority");

            migrationBuilder.DropColumn(
                name: "plaintext_search_permitted",
                schema: "authority",
                table: "organization_protection_policies");

            migrationBuilder.DropColumn(
                name: "search_policy_revision",
                schema: "authority",
                table: "organization_protection_policies");
        }
    }
}
