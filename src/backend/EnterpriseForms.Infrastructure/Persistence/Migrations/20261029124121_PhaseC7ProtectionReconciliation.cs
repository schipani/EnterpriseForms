using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC7ProtectionReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations");

            migrationBuilder.CreateTable(
                name: "field_protection_reconciliations",
                columns: table => new
                {
                    field_protection_reconciliation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_protection_migration_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    migration_direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reconciliation_mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reconciliation_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reconciliation_requested_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reconciliation_round_count = table.Column<int>(type: "integer", nullable: false),
                    transient_failure_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    catalogue_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    catalogue_entry_count = table.Column<int>(type: "integer", nullable: true),
                    census_next_offset = table.Column<int>(type: "integer", nullable: false),
                    census_verified_record_count = table.Column<int>(type: "integer", nullable: false),
                    census_reused_record_count = table.Column<int>(type: "integer", nullable: false),
                    census_failed_record_count = table.Column<int>(type: "integer", nullable: false),
                    business_only_record_count = table.Column<int>(type: "integer", nullable: true),
                    business_missing_record_count = table.Column<int>(type: "integer", nullable: true),
                    decrypt_checked_value_count = table.Column<int>(type: "integer", nullable: false),
                    decrypt_failed_value_count = table.Column<int>(type: "integer", nullable: false),
                    logical_type_mismatch_count = table.Column<int>(type: "integer", nullable: false),
                    retained_old_form_row_count = table.Column<int>(type: "integer", nullable: true),
                    authority_attestation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attestation_digest_hex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attestation_audit_journal_revision = table.Column<long>(type: "bigint", nullable: true),
                    attested_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attestation_complete = table.Column<bool>(type: "boolean", nullable: true),
                    attestation_incomplete_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    attestation_report_json = table.Column<string>(type: "jsonb", nullable: true),
                    reconciliation_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciliation_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_protection_reconciliations", x => x.field_protection_reconciliation_id);
                    table.CheckConstraint("ck_field_protection_reconciliations_complete", "reconciliation_state <> 'Complete' OR (attestation_complete = true AND authority_attestation_id IS NOT NULL AND decrypt_failed_value_count = 0 AND logical_type_mismatch_count = 0)");
                    table.CheckConstraint("ck_field_protection_reconciliations_counts", "census_next_offset >= 0 AND census_verified_record_count >= 0 AND census_reused_record_count >= 0 AND census_failed_record_count >= 0 AND decrypt_checked_value_count >= 0 AND decrypt_failed_value_count >= 0 AND logical_type_mismatch_count >= 0 AND reconciliation_round_count >= 0 AND transient_failure_count >= 0");
                    table.CheckConstraint("ck_field_protection_reconciliations_direction", "migration_direction IN ('Encrypt','Decrypt')");
                    table.CheckConstraint("ck_field_protection_reconciliations_mode", "reconciliation_mode IN ('MigrationCompletion','VerifyOnly')");
                    table.CheckConstraint("ck_field_protection_reconciliations_state", "reconciliation_state IN ('Queued','Running','Blocked','Complete','Incomplete','Unavailable','Superseded')");
                    table.ForeignKey(
                        name: "fk_field_protection_reconciliations_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_protection_reconciliations_form",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_protection_reconciliations_migration",
                        column: x => x.field_protection_migration_id,
                        principalTable: "field_protection_migrations",
                        principalColumn: "field_protection_migration_id");
                    table.ForeignKey(
                        name: "fk_field_protection_reconciliations_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations",
                sql: "migration_state IN ('Queued','Running','Verifying','Completed','Failed','Blocked','Cancelled')");

            migrationBuilder.CreateIndex(
                name: "ix_field_protection_reconciliations_due",
                table: "field_protection_reconciliations",
                columns: new[] { "reconciliation_state", "next_attempt_on" });

            migrationBuilder.CreateIndex(
                name: "ix_field_protection_reconciliations_field",
                table: "field_protection_reconciliations",
                columns: new[] { "field_definition_id", "reconciliation_requested_on" });

            migrationBuilder.CreateIndex(
                name: "IX_field_protection_reconciliations_field_protection_migration~",
                table: "field_protection_reconciliations",
                column: "field_protection_migration_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_protection_reconciliations_form_definition_id",
                table: "field_protection_reconciliations",
                column: "form_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_protection_reconciliations_tenant_id",
                table: "field_protection_reconciliations",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_protection_reconciliations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations",
                sql: "migration_state IN ('Queued','Running','Completed','Failed','Blocked','Cancelled')");
        }
    }
}
