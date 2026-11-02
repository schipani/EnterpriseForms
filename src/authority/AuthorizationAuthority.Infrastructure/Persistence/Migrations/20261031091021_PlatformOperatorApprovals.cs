using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformOperatorApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "request_decided_by_platform_operator_id",
                schema: "authority",
                table: "organization_membership_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "platform_journal_entries",
                schema: "authority",
                columns: table => new
                {
                    platform_journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_journal_revision = table.Column<long>(type: "bigint", nullable: false),
                    journal_event_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    journal_event_body_json = table.Column<string>(type: "text", nullable: false),
                    journal_event_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    journal_previous_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    archive_delivery_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    archive_receipt_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    archive_receipt_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    archive_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_archive_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    journal_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    journal_archived_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_journal_entries", x => x.platform_journal_entry_id);
                    table.CheckConstraint("ck_platform_journal_archive_state", "archive_delivery_state IN ('Pending','Anchored','VerificationFailed')");
                    table.CheckConstraint("ck_platform_journal_digest", "octet_length(journal_event_digest) = 32 AND platform_journal_revision > 0");
                });

            migrationBuilder.CreateTable(
                name: "platform_journal_heads",
                schema: "authority",
                columns: table => new
                {
                    platform_journal_head_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_journal_revision = table.Column<long>(type: "bigint", nullable: false),
                    latest_platform_journal_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    platform_quarantine_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    platform_quarantine_reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    head_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_journal_heads", x => x.platform_journal_head_id);
                    table.CheckConstraint("ck_platform_journal_head_revision", "platform_journal_revision >= 0 AND (latest_platform_journal_digest IS NULL OR octet_length(latest_platform_journal_digest) = 32)");
                    table.CheckConstraint("ck_platform_journal_single_head", "platform_journal_head_id = '01990000-0000-7000-8000-00000000c8a2'");
                });

            migrationBuilder.CreateTable(
                name: "platform_operators",
                schema: "authority",
                columns: table => new
                {
                    platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    operator_subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    operator_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    operator_status_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    operator_evidence_kind_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    synthetic_approval_key_identifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    synthetic_approval_public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    operator_provisioned_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    provisioned_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operator_status_changed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operator_status_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    operator_row_revision = table.Column<long>(type: "bigint", nullable: false),
                    last_platform_journal_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_operators", x => x.platform_operator_id);
                    table.CheckConstraint("ck_platform_operator_evidence_kind", "operator_evidence_kind_name IN ('SyntheticDevelopmentJws','EntraTransactionStepUp')");
                    table.CheckConstraint("ck_platform_operator_revision", "operator_row_revision > 0 AND last_platform_journal_revision >= 0");
                    table.CheckConstraint("ck_platform_operator_status", "operator_status_name IN ('Active','Suspended','Removed')");
                    table.CheckConstraint("ck_platform_operator_synthetic_key", "operator_evidence_kind_name <> 'SyntheticDevelopmentJws' OR (synthetic_approval_key_identifier IS NOT NULL AND synthetic_approval_public_key IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_platform_operator_provisioner",
                        column: x => x.provisioned_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id");
                });

            migrationBuilder.CreateTable(
                name: "platform_operator_approvals",
                schema: "authority",
                columns: table => new
                {
                    platform_operator_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_membership_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_statement_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    approval_statement_version = table.Column<int>(type: "integer", nullable: false),
                    approval_justification_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    approval_evidence_kind_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    approval_evidence_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    approval_recorded_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approval_closed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approval_closure_reason_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    consumed_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_operator_approvals", x => x.platform_operator_approval_id);
                    table.CheckConstraint("ck_platform_approval_closure", "(approval_state_name = 'Pending') = (approval_closed_on IS NULL AND approval_closure_reason_name IS NULL)");
                    table.CheckConstraint("ck_platform_approval_digests", "octet_length(approval_statement_sha256) = 32 AND octet_length(approval_evidence_sha256) = 32");
                    table.CheckConstraint("ck_platform_approval_state", "approval_state_name IN ('Pending','Consumed','Withdrawn','Invalidated')");
                    table.CheckConstraint("ck_platform_approval_statement_version", "approval_statement_version = 2");
                    table.CheckConstraint("ck_platform_approval_window", "approval_valid_until > approval_recorded_on");
                    table.ForeignKey(
                        name: "fk_platform_approval_operator",
                        column: x => x.platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_platform_approval_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_platform_approval_request",
                        column: x => x.organization_membership_request_id,
                        principalSchema: "authority",
                        principalTable: "organization_membership_requests",
                        principalColumn: "organization_membership_request_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_operator_change_proposals",
                schema: "authority",
                columns: table => new
                {
                    platform_operator_change_proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_change_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    target_subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    target_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_evidence_kind_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    target_synthetic_key_identifier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_synthetic_public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    target_row_revision = table.Column<long>(type: "bigint", nullable: false),
                    proposed_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_justification_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    proposal_statement_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    proposal_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proposal_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proposal_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confirmed_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_consumed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_operator_change_proposals", x => x.platform_operator_change_proposal_id);
                    table.CheckConstraint("ck_platform_proposal_change", "proposed_change_name IN ('Provision','Reinstate','Remove')");
                    table.CheckConstraint("ck_platform_proposal_consumed", "(proposal_state_name = 'Consumed') = (confirmed_by_platform_operator_id IS NOT NULL AND proposal_consumed_on IS NOT NULL)");
                    table.CheckConstraint("ck_platform_proposal_expiry", "proposal_expires_on > proposal_created_on");
                    table.CheckConstraint("ck_platform_proposal_state", "proposal_state_name IN ('Pending','Consumed')");
                    table.CheckConstraint("ck_platform_proposal_statement", "octet_length(proposal_statement_sha256) = 32");
                    table.CheckConstraint("ck_platform_proposal_two_operators", "confirmed_by_platform_operator_id IS NULL OR confirmed_by_platform_operator_id <> proposed_by_platform_operator_id");
                    table.ForeignKey(
                        name: "fk_platform_proposal_confirmer",
                        column: x => x.confirmed_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id");
                    table.ForeignKey(
                        name: "fk_platform_proposal_proposer",
                        column: x => x.proposed_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_operator_evidence_uses",
                schema: "authority",
                columns: table => new
                {
                    platform_operator_evidence_use_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_use_identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    evidence_use_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evidence_decision_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    evidence_used_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_operator_evidence_uses", x => x.platform_operator_evidence_use_id);
                    table.CheckConstraint("ck_platform_evidence_digest", "octet_length(evidence_use_sha256) = 32");
                    table.ForeignKey(
                        name: "fk_platform_evidence_operator",
                        column: x => x.platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_membership_requests_request_decided_by_platfor~",
                schema: "authority",
                table: "organization_membership_requests",
                column: "request_decided_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_journal_pending_archive",
                schema: "authority",
                table: "platform_journal_entries",
                column: "archive_delivery_state");

            migrationBuilder.CreateIndex(
                name: "uq_platform_journal_revision",
                schema: "authority",
                table: "platform_journal_entries",
                column: "platform_journal_revision",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_approvals_operator",
                schema: "authority",
                table: "platform_operator_approvals",
                columns: new[] { "platform_operator_id", "approval_state_name" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_approvals_request",
                schema: "authority",
                table: "platform_operator_approvals",
                columns: new[] { "organization_membership_request_id", "approval_recorded_on" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_operator_approvals_organization_id",
                schema: "authority",
                table: "platform_operator_approvals",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "uq_platform_approval_evidence",
                schema: "authority",
                table: "platform_operator_approvals",
                column: "approval_evidence_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_platform_approval_pending_request",
                schema: "authority",
                table: "platform_operator_approvals",
                column: "organization_membership_request_id",
                unique: true,
                filter: "approval_state_name = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_platform_operator_change_proposals_confirmed_by_platform_op~",
                schema: "authority",
                table: "platform_operator_change_proposals",
                column: "confirmed_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "IX_platform_operator_change_proposals_proposed_by_platform_ope~",
                schema: "authority",
                table: "platform_operator_change_proposals",
                column: "proposed_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_operator_proposals_recent",
                schema: "authority",
                table: "platform_operator_change_proposals",
                column: "proposal_created_on");

            migrationBuilder.CreateIndex(
                name: "IX_platform_operator_evidence_uses_platform_operator_id",
                schema: "authority",
                table: "platform_operator_evidence_uses",
                column: "platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "uq_platform_evidence_digest",
                schema: "authority",
                table: "platform_operator_evidence_uses",
                column: "evidence_use_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_platform_evidence_identifier",
                schema: "authority",
                table: "platform_operator_evidence_uses",
                column: "evidence_use_identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_operators_provisioned_by_platform_operator_id",
                schema: "authority",
                table: "platform_operators",
                column: "provisioned_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "uq_platform_operator_identity",
                schema: "authority",
                table: "platform_operators",
                columns: new[] { "operator_issuer_uri", "operator_subject_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_platform_operator_key",
                schema: "authority",
                table: "platform_operators",
                column: "synthetic_approval_key_identifier",
                unique: true,
                filter: "synthetic_approval_key_identifier IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_membership_request_platform_operator",
                schema: "authority",
                table: "organization_membership_requests",
                column: "request_decided_by_platform_operator_id",
                principalSchema: "authority",
                principalTable: "platform_operators",
                principalColumn: "platform_operator_id");

            // The single platform journal head (fixed id, revision 0): every platform mutation locks this row.
            migrationBuilder.Sql("INSERT INTO authority.platform_journal_heads (platform_journal_head_id, platform_journal_revision, latest_platform_journal_digest, platform_quarantine_enabled, platform_quarantine_reason_code, head_modified_on) "
                + "VALUES ('01990000-0000-7000-8000-00000000c8a2', 0, NULL, false, NULL, now()) ON CONFLICT DO NOTHING;");

            // Closed approvals never change again: an UPDATE of a closed row keeps the stored row (a concurrent second closer, e.g. a
            // suspension racing an owner cancel, is a no-op instead of a failed transaction). Pending rows change only to a closed state.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.platform_operator_approvals_keep_closed() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF OLD.approval_state_name <> 'Pending' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.platform_operator_approvals_keep_closed() FROM PUBLIC;
                CREATE TRIGGER trg_platform_operator_approvals_keep_closed BEFORE UPDATE ON authority.platform_operator_approvals
                    FOR EACH ROW EXECUTE FUNCTION authority.platform_operator_approvals_keep_closed();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_platform_operator_approvals_keep_closed ON authority.platform_operator_approvals; DROP FUNCTION IF EXISTS authority.platform_operator_approvals_keep_closed();");

            migrationBuilder.DropForeignKey(
                name: "fk_membership_request_platform_operator",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropTable(
                name: "platform_journal_entries",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "platform_journal_heads",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "platform_operator_approvals",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "platform_operator_change_proposals",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "platform_operator_evidence_uses",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "platform_operators",
                schema: "authority");

            migrationBuilder.DropIndex(
                name: "IX_organization_membership_requests_request_decided_by_platfor~",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropColumn(
                name: "request_decided_by_platform_operator_id",
                schema: "authority",
                table: "organization_membership_requests");
        }
    }
}
