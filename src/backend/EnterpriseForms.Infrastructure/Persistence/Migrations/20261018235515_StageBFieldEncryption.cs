using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBFieldEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "field_protection_mode",
                table: "field_definitions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<Guid>(
                name: "protection_changed_by_user_id",
                table: "field_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "protection_changed_on",
                table: "field_definitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "protection_policy_revision",
                table: "field_definitions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "field_encrypted_values",
                columns: table => new
                {
                    field_encrypted_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    encrypted_value_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    encryption_nonce_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    encryption_auth_tag_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_data_key_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    encryption_algorithm_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    encryption_key_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    encryption_envelope_version = table.Column<int>(type: "integer", nullable: false),
                    encrypted_logical_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
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
                    table.PrimaryKey("PK_field_encrypted_values", x => x.field_encrypted_value_id);
                    table.CheckConstraint("ck_field_encrypted_values_envelope_positive", "encryption_envelope_version >= 1");
                    table.CheckConstraint("ck_field_encrypted_values_logical_type", "encrypted_logical_type IN ('String','Integer','Decimal','Date','DateTime','Boolean','Guid','Option')");
                    table.CheckConstraint("ck_field_encrypted_values_nonce_length", "octet_length(encryption_nonce_bytes) = 12");
                    table.CheckConstraint("ck_field_encrypted_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_encrypted_values_tag_length", "octet_length(encryption_auth_tag_bytes) = 16");
                    table.CheckConstraint("ck_field_encrypted_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_encrypted_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_encrypted_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_encrypted_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_security_policies",
                columns: table => new
                {
                    organization_security_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minimum_field_classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    plaintext_internal_fields_permitted = table.Column<bool>(type: "boolean", nullable: false),
                    policy_statement_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    policy_reviewed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    policy_reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    security_policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    policy_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    policy_modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_security_policies", x => x.organization_security_policy_id);
                    table.CheckConstraint("ck_organization_security_policies_minimum", "minimum_field_classification IN ('Public','Internal','Confidential','Restricted')");
                    table.CheckConstraint("ck_organization_security_policies_revision", "security_policy_revision >= 1");
                    table.ForeignKey(
                        name: "fk_organization_security_policies_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Documents were always application-encrypted (B-05); record that on existing File fields before the floor constraint applies.
            migrationBuilder.Sql("UPDATE field_definitions SET field_protection_mode = 'ApplicationEncrypted' WHERE field_data_type = 'File'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_definitions_protection_floor",
                table: "field_definitions",
                sql: "field_classification IN ('Public','Internal') OR field_protection_mode = 'ApplicationEncrypted'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_definitions_protection_mode",
                table: "field_definitions",
                sql: "field_protection_mode IN ('Standard','ApplicationEncrypted')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_definitions_protection_revision_positive",
                table: "field_definitions",
                sql: "protection_policy_revision >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_field_encrypted_values_field_definition_id",
                table: "field_encrypted_values",
                column: "field_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_encrypted_values_record_operation",
                table: "field_encrypted_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_encrypted_values_tenant_id",
                table: "field_encrypted_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_encrypted_values_occurrence",
                table: "field_encrypted_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "uq_organization_security_policies_tenant",
                table: "organization_security_policies",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_encrypted_values");

            migrationBuilder.DropTable(
                name: "organization_security_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_definitions_protection_floor",
                table: "field_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_definitions_protection_mode",
                table: "field_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_definitions_protection_revision_positive",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "field_protection_mode",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "protection_changed_by_user_id",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "protection_changed_on",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "protection_policy_revision",
                table: "field_definitions");
        }
    }
}
