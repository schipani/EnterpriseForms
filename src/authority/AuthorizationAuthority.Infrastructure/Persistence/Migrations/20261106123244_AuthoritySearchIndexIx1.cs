using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchIndexIx1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "authority_search");

            migrationBuilder.CreateTable(
                name: "search_field_coverages",
                schema: "authority_search",
                columns: table => new
                {
                    search_field_coverage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_value_type_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    coverage_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    covered_field_classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    coverage_started_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    coverage_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    coverage_withdrawn_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    coverage_withdrawal_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_field_coverages", x => x.search_field_coverage_id);
                    table.CheckConstraint("ck_search_coverage_classification", "covered_field_classification IN ('Public','Internal')");
                    table.CheckConstraint("ck_search_coverage_state", "coverage_state_name IN ('Building','Complete','Withdrawn')");
                    table.CheckConstraint("ck_search_coverage_value_type", "indexed_value_type_name IN ('String','Integer','Decimal','Date','DateTime','Boolean','Option')");
                    table.CheckConstraint("ck_search_coverage_withdrawal", "(coverage_state_name = 'Withdrawn') = (coverage_withdrawn_on IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_search_field_coverage_approval",
                        column: x => x.field_search_approval_id,
                        principalSchema: "authority",
                        principalTable: "field_search_approvals",
                        principalColumn: "field_search_approval_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_search_field_coverage_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_form_states",
                schema: "authority_search",
                columns: table => new
                {
                    search_form_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    form_state_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_form_states", x => x.search_form_state_id);
                    table.CheckConstraint("ck_search_form_state_revision", "form_content_revision >= 0");
                    table.ForeignKey(
                        name: "fk_search_form_state_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_projection_states",
                schema: "authority_search",
                columns: table => new
                {
                    search_projection_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    projection_schema_version = table.Column<int>(type: "integer", nullable: false),
                    projection_status_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    projection_status_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    projection_content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    reconciliation_from_revision = table.Column<long>(type: "bigint", nullable: true),
                    verification_suspended = table.Column<bool>(type: "boolean", nullable: false),
                    suspended_scope_names = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    last_successful_cheap_pass_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_evaluation_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    projection_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    projection_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_projection_states", x => x.search_projection_state_id);
                    table.CheckConstraint("ck_search_projection_reconciliation", "(projection_status_name = 'ReconciliationRequired') = (reconciliation_from_revision IS NOT NULL)");
                    table.CheckConstraint("ck_search_projection_revision", "projection_content_integrity_revision >= 0 AND projection_schema_version >= 1");
                    table.CheckConstraint("ck_search_projection_status", "projection_status_name IN ('Active','ReconciliationRequired','Quarantined')");
                    table.CheckConstraint("ck_search_projection_suspension", "verification_suspended = (suspended_scope_names IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_search_projection_state_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_record_entries",
                schema: "authority_search",
                columns: table => new
                {
                    search_record_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    indexed_manifest_root = table.Column<byte[]>(type: "bytea", nullable: false),
                    record_status_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    derived_field_search_approval_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    indexed_posting_count = table.Column<int>(type: "integer", nullable: false),
                    indexed_anomaly_count = table.Column<int>(type: "integer", nullable: false),
                    seal_schema_version = table.Column<int>(type: "integer", nullable: false),
                    seal_key_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    seal_key_version = table.Column<int>(type: "integer", nullable: false),
                    entry_seal_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    entry_written_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seal_verified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_deep_verified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seal_attempted_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seal_attempt_outcome_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_record_entries", x => x.search_record_entry_id);
                    table.UniqueConstraint("uq_search_record_entry_operation", x => new { x.organization_id, x.form_record_id, x.indexed_operation_id });
                    table.CheckConstraint("ck_search_entry_counts", "indexed_posting_count >= 0 AND indexed_anomaly_count >= 0 AND cardinality(derived_field_search_approval_ids) >= 1");
                    table.CheckConstraint("ck_search_entry_head", "octet_length(indexed_manifest_root) = 32 AND indexed_content_revision >= 1");
                    table.CheckConstraint("ck_search_entry_seal", "octet_length(entry_seal_signature) = 64 AND seal_key_version >= 1 AND seal_schema_version = 1");
                    table.ForeignKey(
                        name: "fk_search_record_entry_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_boolean_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_boolean_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    boolean_value = table.Column<bool>(type: "boolean", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_boolean_postings", x => x.search_boolean_posting_id);
                    table.CheckConstraint("ck_search_boolean_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_boolean_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_date_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_date_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_value = table.Column<DateOnly>(type: "date", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_date_postings", x => x.search_date_posting_id);
                    table.CheckConstraint("ck_search_date_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_date_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_date_time_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_date_time_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_time_value = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_date_time_postings", x => x.search_date_time_posting_id);
                    table.CheckConstraint("ck_search_date_time_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_date_time_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_decimal_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_decimal_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decimal_value = table.Column<decimal>(type: "numeric", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_decimal_postings", x => x.search_decimal_posting_id);
                    table.CheckConstraint("ck_search_decimal_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_decimal_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_integer_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_integer_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    integer_value = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_integer_postings", x => x.search_integer_posting_id);
                    table.CheckConstraint("ck_search_integer_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_integer_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_option_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_option_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_option_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_option_postings", x => x.search_option_posting_id);
                    table.CheckConstraint("ck_search_option_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_option_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_record_field_anomalies",
                schema: "authority_search",
                columns: table => new
                {
                    search_record_field_anomaly_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_kind_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_record_field_anomalies", x => x.search_record_field_anomaly_id);
                    table.CheckConstraint("ck_search_anomaly_kind", "anomaly_kind_name IN ('EncryptedValuePresent')");
                    table.ForeignKey(
                        name: "fk_search_record_field_anomaly_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_string_postings",
                schema: "authority_search",
                columns: table => new
                {
                    search_string_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_string_value = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    source_entity_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    indexed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_search_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_canonical_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_string_postings", x => x.search_string_posting_id);
                    table.CheckConstraint("ck_search_string_posting_source", "octet_length(manifest_item_key) = 17 AND octet_length(source_canonical_bytes) > 0");
                    table.ForeignKey(
                        name: "fk_search_string_posting_entry",
                        columns: x => new { x.organization_id, x.form_record_id, x.indexed_operation_id },
                        principalSchema: "authority_search",
                        principalTable: "search_record_entries",
                        principalColumns: new[] { "organization_id", "form_record_id", "indexed_operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_boolean_postings_entry",
                schema: "authority_search",
                table: "search_boolean_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_boolean_postings_record",
                schema: "authority_search",
                table: "search_boolean_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_boolean_postings_value",
                schema: "authority_search",
                table: "search_boolean_postings",
                columns: new[] { "organization_id", "field_definition_id", "boolean_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_postings_entry",
                schema: "authority_search",
                table: "search_date_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_postings_record",
                schema: "authority_search",
                table: "search_date_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_postings_value",
                schema: "authority_search",
                table: "search_date_postings",
                columns: new[] { "organization_id", "field_definition_id", "date_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_time_postings_entry",
                schema: "authority_search",
                table: "search_date_time_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_time_postings_record",
                schema: "authority_search",
                table: "search_date_time_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_time_postings_value",
                schema: "authority_search",
                table: "search_date_time_postings",
                columns: new[] { "organization_id", "field_definition_id", "date_time_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_decimal_postings_entry",
                schema: "authority_search",
                table: "search_decimal_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_decimal_postings_record",
                schema: "authority_search",
                table: "search_decimal_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_decimal_postings_value",
                schema: "authority_search",
                table: "search_decimal_postings",
                columns: new[] { "organization_id", "field_definition_id", "decimal_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_field_coverages_form",
                schema: "authority_search",
                table: "search_field_coverages",
                columns: new[] { "organization_id", "form_definition_id" });

            migrationBuilder.CreateIndex(
                name: "uq_search_field_coverage_approval",
                schema: "authority_search",
                table: "search_field_coverages",
                column: "field_search_approval_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_search_form_state_form",
                schema: "authority_search",
                table: "search_form_states",
                columns: new[] { "organization_id", "form_definition_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_integer_postings_entry",
                schema: "authority_search",
                table: "search_integer_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_integer_postings_record",
                schema: "authority_search",
                table: "search_integer_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_integer_postings_value",
                schema: "authority_search",
                table: "search_integer_postings",
                columns: new[] { "organization_id", "field_definition_id", "integer_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_option_postings_entry",
                schema: "authority_search",
                table: "search_option_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_option_postings_record",
                schema: "authority_search",
                table: "search_option_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_option_postings_value",
                schema: "authority_search",
                table: "search_option_postings",
                columns: new[] { "organization_id", "field_definition_id", "field_option_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "uq_search_projection_state_org",
                schema: "authority_search",
                table: "search_projection_states",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_record_entries_form",
                schema: "authority_search",
                table: "search_record_entries",
                columns: new[] { "organization_id", "form_definition_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_record_entries_seal_check",
                schema: "authority_search",
                table: "search_record_entries",
                columns: new[] { "organization_id", "last_seal_verified_on" });

            migrationBuilder.CreateIndex(
                name: "uq_search_record_entry_record",
                schema: "authority_search",
                table: "search_record_entries",
                columns: new[] { "organization_id", "form_record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_record_field_anomalies_entry",
                schema: "authority_search",
                table: "search_record_field_anomalies",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "uq_search_record_field_anomaly",
                schema: "authority_search",
                table: "search_record_field_anomalies",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "anomaly_kind_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_string_postings_entry",
                schema: "authority_search",
                table: "search_string_postings",
                columns: new[] { "organization_id", "form_record_id", "indexed_operation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_string_postings_record",
                schema: "authority_search",
                table: "search_string_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_string_postings_value",
                schema: "authority_search",
                table: "search_string_postings",
                columns: new[] { "organization_id", "field_definition_id", "normalized_string_value", "form_record_id" });

            // IX-1 hand-written DDL (design R1 B.3/B.6, revision 2 5.4, C-124 18.2). Reviewed with the migration.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX ix_search_string_postings_trigram ON authority_search.search_string_postings USING gin (normalized_string_value gin_trgm_ops);");

            // RLS keyed on the transaction-local organization context (defence in depth, never authorization, CLAUDE.md 4Q.3).
            migrationBuilder.Sql("""
                CREATE FUNCTION authority_search.current_organization_id() RETURNS uuid
                LANGUAGE sql STABLE PARALLEL SAFE SET search_path = pg_catalog, pg_temp
                AS $$ SELECT nullif(current_setting('authority_search.organization_id', true), '')::uuid $$;
                """);
            foreach (var table in new[] { "search_record_entries", "search_string_postings", "search_integer_postings", "search_decimal_postings", "search_date_postings", "search_date_time_postings", "search_boolean_postings", "search_option_postings", "search_record_field_anomalies" })
            {
                migrationBuilder.Sql($"ALTER TABLE authority_search.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE authority_search.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY rls_{table}_organization ON authority_search.{table} USING (organization_id = authority_search.current_organization_id()) WITH CHECK (organization_id = authority_search.current_organization_id());");
            }

            // INV-R (C-124 18.2.2): a transaction that changes an organization's content-integrity revision must leave its search
            // projection at that revision or ReconciliationRequired. Deferred to commit; reads the final row values, so several
            // updates in one transaction are judged on the result. Organizations without a projection row are unaffected.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority_search.enforce_content_revision_synchronization() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                DECLARE
                    current_revision bigint;
                    projection_status text;
                    projection_revision bigint;
                BEGIN
                    SELECT s.content_integrity_revision INTO current_revision
                      FROM authority.organization_authorization_states s WHERE s.organization_id = NEW.organization_id;
                    SELECT p.projection_status_name, p.projection_content_integrity_revision INTO projection_status, projection_revision
                      FROM authority_search.search_projection_states p WHERE p.organization_id = NEW.organization_id;
                    IF NOT FOUND THEN
                        RETURN NULL;
                    END IF;
                    IF projection_status <> 'ReconciliationRequired' AND projection_revision IS DISTINCT FROM current_revision THEN
                        RAISE EXCEPTION 'INV-R: the content-integrity revision of an organization changed without synchronizing its search projection'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE CONSTRAINT TRIGGER trg_content_revision_search_synchronization
                AFTER UPDATE OF content_integrity_revision ON authority.organization_authorization_states
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW WHEN (OLD.content_integrity_revision IS DISTINCT FROM NEW.content_integrity_revision)
                EXECUTE FUNCTION authority_search.enforce_content_revision_synchronization();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_content_revision_search_synchronization ON authority.organization_authorization_states;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS authority_search.enforce_content_revision_synchronization();");
            migrationBuilder.Sql("DROP INDEX IF EXISTS authority_search.ix_search_string_postings_trigram;");
            foreach (var table in new[] { "search_record_entries", "search_string_postings", "search_integer_postings", "search_decimal_postings", "search_date_postings", "search_date_time_postings", "search_boolean_postings", "search_option_postings", "search_record_field_anomalies" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS rls_{table}_organization ON authority_search.{table};");
            }

            migrationBuilder.DropTable(
                name: "search_boolean_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_date_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_date_time_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_decimal_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_field_coverages",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_form_states",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_integer_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_option_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_projection_states",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_record_field_anomalies",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_string_postings",
                schema: "authority_search");

            migrationBuilder.DropTable(
                name: "search_record_entries",
                schema: "authority_search");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS authority_search.current_organization_id();");
        }
    }
}
