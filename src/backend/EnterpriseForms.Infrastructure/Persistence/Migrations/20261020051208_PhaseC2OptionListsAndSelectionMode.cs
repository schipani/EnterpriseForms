using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC2OptionListsAndSelectionMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_field_option_values_occurrence",
                table: "field_option_values");

            migrationBuilder.DropIndex(
                name: "uq_field_encrypted_values_occurrence",
                table: "field_encrypted_values");

            migrationBuilder.AddColumn<Guid>(
                name: "option_list_item_id",
                table: "field_options",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "option_list_id",
                table: "field_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "option_selection_mode",
                table: "field_definitions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Single");

            migrationBuilder.CreateTable(
                name: "option_lists",
                columns: table => new
                {
                    option_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_list_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    option_list_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    lifecycle_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    option_list_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_lists", x => x.option_list_id);
                    table.CheckConstraint("ck_option_lists_lifecycle", "lifecycle_status IN ('Active','Archived')");
                    table.CheckConstraint("ck_option_lists_revision_positive", "option_list_revision >= 1");
                    table.ForeignKey(
                        name: "fk_option_lists_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "option_list_items",
                columns: table => new
                {
                    option_list_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    option_display_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    option_sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_list_items", x => x.option_list_item_id);
                    table.ForeignKey(
                        name: "fk_option_list_items_list",
                        column: x => x.option_list_id,
                        principalTable: "option_lists",
                        principalColumn: "option_list_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_options_option_list_item_id",
                table: "field_options",
                column: "option_list_item_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_option_values_occurrence",
                table: "field_option_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id", "field_value_item_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "uq_field_option_values_option",
                table: "field_option_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id", "field_option_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "uq_field_encrypted_values_occurrence",
                table: "field_encrypted_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id", "field_value_item_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_field_definitions_option_list_id",
                table: "field_definitions",
                column: "option_list_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_definitions_option_selection_mode",
                table: "field_definitions",
                sql: "option_selection_mode IN ('Single','Multiple')");

            migrationBuilder.CreateIndex(
                name: "uq_option_list_items_list_key",
                table: "option_list_items",
                columns: new[] { "option_list_id", "option_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_option_lists_tenant_name",
                table: "option_lists",
                columns: new[] { "tenant_id", "option_list_name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_field_definitions_option_list",
                table: "field_definitions",
                column: "option_list_id",
                principalTable: "option_lists",
                principalColumn: "option_list_id");

            migrationBuilder.AddForeignKey(
                name: "fk_field_options_list_item",
                table: "field_options",
                column: "option_list_item_id",
                principalTable: "option_list_items",
                principalColumn: "option_list_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_field_definitions_option_list",
                table: "field_definitions");

            migrationBuilder.DropForeignKey(
                name: "fk_field_options_list_item",
                table: "field_options");

            migrationBuilder.DropTable(
                name: "option_list_items");

            migrationBuilder.DropTable(
                name: "option_lists");

            migrationBuilder.DropIndex(
                name: "IX_field_options_option_list_item_id",
                table: "field_options");

            migrationBuilder.DropIndex(
                name: "uq_field_option_values_occurrence",
                table: "field_option_values");

            migrationBuilder.DropIndex(
                name: "uq_field_option_values_option",
                table: "field_option_values");

            migrationBuilder.DropIndex(
                name: "uq_field_encrypted_values_occurrence",
                table: "field_encrypted_values");

            migrationBuilder.DropIndex(
                name: "IX_field_definitions_option_list_id",
                table: "field_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_definitions_option_selection_mode",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "option_list_item_id",
                table: "field_options");

            migrationBuilder.DropColumn(
                name: "option_list_id",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "option_selection_mode",
                table: "field_definitions");

            migrationBuilder.CreateIndex(
                name: "uq_field_option_values_occurrence",
                table: "field_option_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "uq_field_encrypted_values_occurrence",
                table: "field_encrypted_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
