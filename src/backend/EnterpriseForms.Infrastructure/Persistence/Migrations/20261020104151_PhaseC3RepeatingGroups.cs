using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC3RepeatingGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "group_definition_id",
                table: "field_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "group_definitions",
                columns: table => new
                {
                    group_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    group_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    lifecycle_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_definitions", x => x.group_definition_id);
                    table.CheckConstraint("ck_group_definitions_lifecycle", "lifecycle_status IN ('Available','Deprecated','Retired')");
                    table.ForeignKey(
                        name: "fk_group_definitions_definition",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_record_group_revisions",
                columns: table => new
                {
                    form_record_group_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_sort_order = table.Column<long>(type: "bigint", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_record_group_revisions", x => x.form_record_group_revision_id);
                    table.CheckConstraint("ck_form_record_group_revisions_no_nesting", "parent_form_record_group_id IS NULL");
                    table.CheckConstraint("ck_form_record_group_revisions_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_form_record_group_revisions_sort_order", "group_sort_order >= 0");
                    table.CheckConstraint("ck_form_record_group_revisions_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_form_record_group_revisions_group",
                        column: x => x.group_definition_id,
                        principalTable: "group_definitions",
                        principalColumn: "group_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_record_group_revisions_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_record_group_revisions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_definitions_group_definition_id",
                table: "field_definitions",
                column: "group_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_record_group_revisions_group_definition_id",
                table: "form_record_group_revisions",
                column: "group_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_record_group_revisions_record_operation",
                table: "form_record_group_revisions",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_form_record_group_revisions_tenant_id",
                table: "form_record_group_revisions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_record_group_revisions_occurrence",
                table: "form_record_group_revisions",
                columns: new[] { "content_change_operation_id", "form_record_id", "form_record_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_form_record_group_revisions_position",
                table: "form_record_group_revisions",
                columns: new[] { "content_change_operation_id", "form_record_id", "group_definition_id", "group_sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_group_definitions_form_key",
                table: "group_definitions",
                columns: new[] { "form_definition_id", "group_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_field_definitions_group",
                table: "field_definitions",
                column: "group_definition_id",
                principalTable: "group_definitions",
                principalColumn: "group_definition_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_field_definitions_group",
                table: "field_definitions");

            migrationBuilder.DropTable(
                name: "form_record_group_revisions");

            migrationBuilder.DropTable(
                name: "group_definitions");

            migrationBuilder.DropIndex(
                name: "IX_field_definitions_group_definition_id",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "group_definition_id",
                table: "field_definitions");
        }
    }
}
