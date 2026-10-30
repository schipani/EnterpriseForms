using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC7ReconciliationReuseCutoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_reconciliations_counts",
                table: "field_protection_reconciliations");

            migrationBuilder.AddColumn<Guid>(
                name: "first_round_snapshot_id",
                table: "field_protection_reconciliations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "round_start_verified_record_count",
                table: "field_protection_reconciliations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_reconciliations_counts",
                table: "field_protection_reconciliations",
                sql: "census_next_offset >= 0 AND round_start_verified_record_count >= 0 AND census_verified_record_count >= 0 AND census_reused_record_count >= 0 AND census_failed_record_count >= 0 AND decrypt_checked_value_count >= 0 AND decrypt_failed_value_count >= 0 AND logical_type_mismatch_count >= 0 AND reconciliation_round_count >= 0 AND transient_failure_count >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_reconciliations_counts",
                table: "field_protection_reconciliations");

            migrationBuilder.DropColumn(
                name: "first_round_snapshot_id",
                table: "field_protection_reconciliations");

            migrationBuilder.DropColumn(
                name: "round_start_verified_record_count",
                table: "field_protection_reconciliations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_reconciliations_counts",
                table: "field_protection_reconciliations",
                sql: "census_next_offset >= 0 AND census_verified_record_count >= 0 AND census_reused_record_count >= 0 AND census_failed_record_count >= 0 AND decrypt_checked_value_count >= 0 AND decrypt_failed_value_count >= 0 AND logical_type_mismatch_count >= 0 AND reconciliation_round_count >= 0 AND transient_failure_count >= 0");
        }
    }
}
