using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchAccessIx5b : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "consumed_content_integrity_revision",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "consumed_content_journal_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "consumed_coverage_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "search_access_value_postings",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_value_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_principal_type_name = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    access_principal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_type_name = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    normalized_string_value = table.Column<string>(type: "text", nullable: true),
                    integer_value = table.Column<long>(type: "bigint", nullable: true),
                    decimal_value = table.Column<decimal>(type: "numeric", nullable: true),
                    date_value = table.Column<DateOnly>(type: "date", nullable: true),
                    date_time_value = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    boolean_value = table.Column<bool>(type: "boolean", nullable: true),
                    field_option_id = table.Column<Guid>(type: "uuid", nullable: true),
                    derived_content_integrity_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_value_postings", x => x.search_access_value_posting_id);
                    table.CheckConstraint("ck_search_access_value_posting_one_value", "(value_type_name = 'String') = (normalized_string_value IS NOT NULL) AND (value_type_name = 'Integer') = (integer_value IS NOT NULL) AND (value_type_name = 'Decimal') = (decimal_value IS NOT NULL) AND (value_type_name = 'Date') = (date_value IS NOT NULL) AND (value_type_name = 'DateTime') = (date_time_value IS NOT NULL) AND (value_type_name = 'Boolean') = (boolean_value IS NOT NULL) AND (value_type_name = 'Option') = (field_option_id IS NOT NULL)");
                    table.CheckConstraint("ck_search_access_value_posting_principal", "access_principal_type_name IN ('user','team')");
                    table.ForeignKey(
                        name: "fk_search_access_value_posting_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_access_checkpoint_content_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                sql: "consumed_content_integrity_revision >= 0 AND (consumed_content_integrity_revision = 0) = (consumed_content_journal_digest IS NULL) AND (consumed_content_journal_digest IS NULL OR octet_length(consumed_content_journal_digest) = 32)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_access_checkpoint_coverage_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                sql: "consumed_coverage_digest IS NULL OR octet_length(consumed_coverage_digest) = 32");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_boolean",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "boolean_value" },
                filter: "value_type_name = 'Boolean'");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_date",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "date_value" },
                filter: "value_type_name = 'Date'");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_date_time",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "date_time_value" },
                filter: "value_type_name = 'DateTime'");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_decimal",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "decimal_value" },
                filter: "value_type_name = 'Decimal'");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_integer",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "integer_value" },
                filter: "value_type_name = 'Integer'");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_option",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "field_option_id" },
                filter: "value_type_name = 'Option'");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_record",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_access_value_postings_string",
                schema: "authority_search_access",
                table: "search_access_value_postings",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "field_definition_id", "normalized_string_value" },
                filter: "value_type_name = 'String'");
            // IX-5b hand-written DDL (C-148): forced RLS on the bound organization, as for the other Tier 2 tables.
            migrationBuilder.Sql("ALTER TABLE authority_search_access.search_access_value_postings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE authority_search_access.search_access_value_postings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("CREATE POLICY rls_search_access_value_postings_organization ON authority_search_access.search_access_value_postings USING (organization_id = authority_search_access.current_organization_id()) WITH CHECK (organization_id = authority_search_access.current_organization_id());");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS rls_search_access_value_postings_organization ON authority_search_access.search_access_value_postings;");
            migrationBuilder.DropTable(
                name: "search_access_value_postings",
                schema: "authority_search_access");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_access_checkpoint_content_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_access_checkpoint_coverage_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints");

            migrationBuilder.DropColumn(
                name: "consumed_content_integrity_revision",
                schema: "authority_search_access",
                table: "search_access_checkpoints");

            migrationBuilder.DropColumn(
                name: "consumed_content_journal_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints");

            migrationBuilder.DropColumn(
                name: "consumed_coverage_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints");
        }
    }
}
