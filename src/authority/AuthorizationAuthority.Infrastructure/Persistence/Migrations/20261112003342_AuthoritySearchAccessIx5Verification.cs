using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchAccessIx5Verification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "derived_schema_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "search_access_shadow_samples",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_shadow_sample_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sample_compared_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sample_classification_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sample_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tier1_match_count = table.Column<int>(type: "integer", nullable: false),
                    tier2_match_count = table.Column<int>(type: "integer", nullable: true),
                    only_in_tier1_count = table.Column<int>(type: "integer", nullable: false),
                    only_in_tier2_count = table.Column<int>(type: "integer", nullable: false),
                    excluded_by_metadata_count = table.Column<int>(type: "integer", nullable: false),
                    condition_count = table.Column<int>(type: "integer", nullable: false),
                    predicate_shape_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tier1_result_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    tier2_result_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    sample_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    sample_content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    derivation_protocol_version = table.Column<int>(type: "integer", nullable: false),
                    derivation_schema_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    is_canary_organization = table.Column<bool>(type: "boolean", nullable: false),
                    comparison_elapsed_milliseconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_shadow_samples", x => x.search_access_shadow_sample_id);
                    table.CheckConstraint("ck_search_access_shadow_sample_classification", "sample_classification_name IN ('Match','Mismatch','NotComparable','Cancelled','Failed')");
                    table.CheckConstraint("ck_search_access_shadow_sample_counts", "tier1_match_count >= 0 AND (tier2_match_count IS NULL OR tier2_match_count >= 0) AND only_in_tier1_count >= 0 AND only_in_tier2_count >= 0 AND excluded_by_metadata_count >= 0 AND condition_count >= 0");
                    table.CheckConstraint("ck_search_access_shadow_sample_digests", "octet_length(tier1_result_digest) = 32 AND (tier2_result_digest IS NULL OR octet_length(tier2_result_digest) = 32) AND octet_length(derivation_schema_digest) = 32");
                    table.ForeignKey(
                        name: "fk_search_access_shadow_sample_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_access_verification_states",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_verification_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_verified_revision = table.Column<long>(type: "bigint", nullable: false),
                    access_verified_journal_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    full_verification_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    full_verification_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    spice_db_sweep_cursor_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    spice_db_sweep_started_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    spice_db_sweep_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    spice_db_sweep_records_checked = table.Column<long>(type: "bigint", nullable: false),
                    last_verification_outcome_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    verification_state_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_verification_states", x => x.search_access_verification_state_id);
                    table.CheckConstraint("ck_search_access_verification_state_digest", "(access_verified_revision = 0) = (access_verified_journal_digest IS NULL) AND (access_verified_journal_digest IS NULL OR octet_length(access_verified_journal_digest) = 32)");
                    table.CheckConstraint("ck_search_access_verification_state_revisions", "access_verified_revision >= 0 AND full_verification_authorization_revision >= 0 AND spice_db_sweep_records_checked >= 0");
                    table.ForeignKey(
                        name: "fk_search_access_verification_state_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_access_checkpoint_schema_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                sql: "derived_schema_digest IS NULL OR octet_length(derived_schema_digest) = 32");

            migrationBuilder.CreateIndex(
                name: "ix_search_access_shadow_samples_org_time",
                schema: "authority_search_access",
                table: "search_access_shadow_samples",
                columns: new[] { "organization_id", "sample_compared_on" });

            migrationBuilder.CreateIndex(
                name: "uq_search_access_verification_state_org",
                schema: "authority_search_access",
                table: "search_access_verification_states",
                column: "organization_id",
                unique: true);

            // IX-5: both tables are organization-bound under forced RLS, as every Tier 2 table (the runtime grants are applied by AuthorityRuntimeGrants).
            foreach (var table in new[] { "search_access_verification_states", "search_access_shadow_samples" })
            {
                migrationBuilder.Sql($"ALTER TABLE authority_search_access.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE authority_search_access.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY rls_{table}_organization ON authority_search_access.{table} USING (organization_id = authority_search_access.current_organization_id()) WITH CHECK (organization_id = authority_search_access.current_organization_id());");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS rls_search_access_verification_states_organization ON authority_search_access.search_access_verification_states;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS rls_search_access_shadow_samples_organization ON authority_search_access.search_access_shadow_samples;");
            migrationBuilder.DropTable(
                name: "search_access_shadow_samples",
                schema: "authority_search_access");

            migrationBuilder.DropTable(
                name: "search_access_verification_states",
                schema: "authority_search_access");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_access_checkpoint_schema_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints");

            migrationBuilder.DropColumn(
                name: "derived_schema_digest",
                schema: "authority_search_access",
                table: "search_access_checkpoints");
        }
    }
}
