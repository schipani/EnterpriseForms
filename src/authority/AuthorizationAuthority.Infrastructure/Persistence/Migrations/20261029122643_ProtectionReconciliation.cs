using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectionReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "protection_census_results",
                schema: "authority",
                columns: table => new
                {
                    protection_census_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scoping_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    census_outcome_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    field_occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    old_form_occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    envelope_issue_count = table.Column<int>(type: "integer", nullable: false),
                    verified_record_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    requesting_workload_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    census_verified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protection_census_results", x => x.protection_census_result_id);
                    table.CheckConstraint("ck_protection_census_counts", "committed_content_revision > 0 AND field_occurrence_count >= 0 AND old_form_occurrence_count >= 0 AND old_form_occurrence_count <= field_occurrence_count AND envelope_issue_count >= 0");
                    table.ForeignKey(
                        name: "fk_protection_census_command",
                        column: x => x.scoping_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_protection_census_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "protection_reconciliation_attestations",
                schema: "authority",
                columns: table => new
                {
                    protection_reconciliation_attestation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scoping_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protection_direction_name = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    search_catalogue_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_record_count = table.Column<int>(type: "integer", nullable: false),
                    accepted_record_count = table.Column<int>(type: "integer", nullable: false),
                    pending_creation_record_count = table.Column<int>(type: "integer", nullable: false),
                    quarantined_record_count = table.Column<int>(type: "integer", nullable: false),
                    compliant_record_count = table.Column<int>(type: "integer", nullable: false),
                    not_applicable_record_count = table.Column<int>(type: "integer", nullable: false),
                    old_form_remaining_record_count = table.Column<int>(type: "integer", nullable: false),
                    old_form_remaining_occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    target_form_occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    unverified_record_count = table.Column<int>(type: "integer", nullable: false),
                    not_yet_censused_record_count = table.Column<int>(type: "integer", nullable: false),
                    envelope_issue_record_count = table.Column<int>(type: "integer", nullable: false),
                    archived_record_count = table.Column<int>(type: "integer", nullable: false),
                    retired_record_count = table.Column<int>(type: "integer", nullable: false),
                    retained_history_revision_count = table.Column<long>(type: "bigint", nullable: false),
                    active_search_approval_count = table.Column<int>(type: "integer", nullable: false),
                    attestation_complete = table.Column<bool>(type: "boolean", nullable: false),
                    attestation_incomplete_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    attestation_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    attested_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    attested_content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    attested_audit_journal_revision = table.Column<long>(type: "bigint", nullable: false),
                    requesting_workload_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    attested_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protection_reconciliation_attestations", x => x.protection_reconciliation_attestation_id);
                    table.CheckConstraint("ck_protection_attestation_complete", "NOT attestation_complete OR (attestation_incomplete_reason IS NULL AND old_form_remaining_record_count = 0 AND quarantined_record_count = 0 AND unverified_record_count = 0 AND not_yet_censused_record_count = 0 AND envelope_issue_record_count = 0 AND active_search_approval_count = 0)");
                    table.CheckConstraint("ck_protection_attestation_digest", "octet_length(attestation_digest) = 32");
                    table.CheckConstraint("ck_protection_attestation_direction", "protection_direction_name IN ('Encrypt','Decrypt')");
                    table.ForeignKey(
                        name: "fk_protection_attestation_command",
                        column: x => x.scoping_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_protection_attestation_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_protection_census_command",
                schema: "authority",
                table: "protection_census_results",
                columns: new[] { "organization_id", "scoping_security_command_id" });

            migrationBuilder.CreateIndex(
                name: "uq_protection_census_operation",
                schema: "authority",
                table: "protection_census_results",
                columns: new[] { "scoping_security_command_id", "form_record_id", "committed_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_protection_attestation_command",
                schema: "authority",
                table: "protection_reconciliation_attestations",
                columns: new[] { "organization_id", "scoping_security_command_id", "attested_audit_journal_revision" });

            migrationBuilder.CreateIndex(
                name: "IX_protection_reconciliation_attestations_scoping_security_com~",
                schema: "authority",
                table: "protection_reconciliation_attestations",
                column: "scoping_security_command_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "protection_census_results",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "protection_reconciliation_attestations",
                schema: "authority");
        }
    }
}
