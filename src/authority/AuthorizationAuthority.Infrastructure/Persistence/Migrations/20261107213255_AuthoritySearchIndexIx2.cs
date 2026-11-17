using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchIndexIx2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_search_projection_status",
                schema: "authority_search",
                table: "search_projection_states");

            migrationBuilder.AddColumn<string>(
                name: "last_deep_attempt_outcome_code",
                schema: "authority_search",
                table: "search_record_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_deep_attempted_on",
                schema: "authority_search",
                table: "search_record_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "coverage_attested_journal_entry_id",
                schema: "authority_search",
                table: "search_field_coverages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_projection_status",
                schema: "authority_search",
                table: "search_projection_states",
                sql: "projection_status_name IN ('Active','ReconciliationRequired','Quarantined','Rebuilding')");

            migrationBuilder.CreateIndex(
                name: "ix_search_field_coverages_attestation",
                schema: "authority_search",
                table: "search_field_coverages",
                column: "coverage_attested_journal_entry_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_coverage_attestation",
                schema: "authority_search",
                table: "search_field_coverages",
                sql: "coverage_state_name <> 'Complete' OR (coverage_attested_journal_entry_id IS NOT NULL AND coverage_completed_on IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_search_field_coverage_attestation",
                schema: "authority_search",
                table: "search_field_coverages",
                column: "coverage_attested_journal_entry_id",
                principalSchema: "authority",
                principalTable: "security_journal_entries",
                principalColumn: "security_journal_entry_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_search_field_coverage_attestation",
                schema: "authority_search",
                table: "search_field_coverages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_projection_status",
                schema: "authority_search",
                table: "search_projection_states");

            migrationBuilder.DropIndex(
                name: "ix_search_field_coverages_attestation",
                schema: "authority_search",
                table: "search_field_coverages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_coverage_attestation",
                schema: "authority_search",
                table: "search_field_coverages");

            migrationBuilder.DropColumn(
                name: "last_deep_attempt_outcome_code",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropColumn(
                name: "last_deep_attempted_on",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropColumn(
                name: "coverage_attested_journal_entry_id",
                schema: "authority_search",
                table: "search_field_coverages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_projection_status",
                schema: "authority_search",
                table: "search_projection_states",
                sql: "projection_status_name IN ('Active','ReconciliationRequired','Quarantined')");
        }
    }
}
