using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBDateTimeGuidValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "field_date_time_values",
                columns: table => new
                {
                    field_date_time_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_time_value = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_field_date_time_values", x => x.field_date_time_value_id);
                    table.CheckConstraint("ck_field_date_time_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_date_time_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_date_time_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_date_time_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_date_time_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_guid_values",
                columns: table => new
                {
                    field_guid_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guid_value = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_field_guid_values", x => x.field_guid_value_id);
                    table.CheckConstraint("ck_field_guid_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_guid_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_guid_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_guid_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_guid_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_field_date_time_values_lookup",
                table: "field_date_time_values",
                columns: new[] { "field_definition_id", "date_time_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_date_time_values_record_operation",
                table: "field_date_time_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_date_time_values_tenant_id",
                table: "field_date_time_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_date_time_values_occurrence",
                table: "field_date_time_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_field_guid_values_lookup",
                table: "field_guid_values",
                columns: new[] { "field_definition_id", "guid_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_guid_values_record_operation",
                table: "field_guid_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_guid_values_tenant_id",
                table: "field_guid_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_guid_values_occurrence",
                table: "field_guid_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_date_time_values");

            migrationBuilder.DropTable(
                name: "field_guid_values");
        }
    }
}
