using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_binding_single_provenance",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.AddColumn<Guid>(
                name: "organization_owner_recovery_case_id",
                schema: "authority",
                table: "identity_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_owner_recovery_cases",
                schema: "authority",
                columns: table => new
                {
                    organization_owner_recovery_case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recovery_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    incident_reference_text = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recovery_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    opened_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    create_request_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    recovery_statement_json = table.Column<string>(type: "text", nullable: false),
                    recovery_statement_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    owner_set_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    case_opened_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    waiting_period_ends_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    case_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recovered_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    executed_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    objected_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    objected_by_authority_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    objection_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    case_closed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    case_closure_reason_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    closure_justification_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_execution_failure_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    last_execution_failure_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    case_row_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_owner_recovery_cases", x => x.organization_owner_recovery_case_id);
                    table.CheckConstraint("ck_owner_recovery_closed", "(recovery_state_name = 'Open') = (case_closed_on IS NULL)");
                    table.CheckConstraint("ck_owner_recovery_digests", "octet_length(recovery_statement_sha256) = 32 AND octet_length(create_request_digest) = 32 AND octet_length(owner_set_sha256) = 32");
                    table.CheckConstraint("ck_owner_recovery_objected", "(recovery_state_name = 'Objected') = (objected_on IS NOT NULL AND objected_by_authority_user_id IS NOT NULL AND objection_reason_text IS NOT NULL)");
                    table.CheckConstraint("ck_owner_recovery_recovered", "(recovery_state_name = 'Recovered') = (recovered_on IS NOT NULL AND executed_by_platform_operator_id IS NOT NULL AND executed_by_security_command_id IS NOT NULL)");
                    table.CheckConstraint("ck_owner_recovery_revision", "case_row_revision > 0");
                    table.CheckConstraint("ck_owner_recovery_state", "recovery_state_name IN ('Open','Objected','Recovered','Cancelled','Expired','Invalidated')");
                    table.CheckConstraint("ck_owner_recovery_times", "waiting_period_ends_on > case_opened_on AND case_expires_on > waiting_period_ends_on");
                    table.ForeignKey(
                        name: "fk_owner_recovery_closer",
                        column: x => x.closed_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_owner_recovery_executor",
                        column: x => x.executed_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_owner_recovery_opener",
                        column: x => x.opened_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_owner_recovery_organization",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_owner_recovery_approvals",
                schema: "authority",
                columns: table => new
                {
                    organization_owner_recovery_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_owner_recovery_case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_statement_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    approval_justification_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    approval_evidence_kind_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    approval_evidence_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    approval_evidence_identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    approval_recorded_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approval_closed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approval_closure_reason_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_owner_recovery_approvals", x => x.organization_owner_recovery_approval_id);
                    table.CheckConstraint("ck_owner_recovery_approval_closed", "(approval_state_name IN ('Withdrawn','Invalidated')) = (approval_closed_on IS NOT NULL AND approval_closure_reason_name IS NOT NULL)");
                    table.CheckConstraint("ck_owner_recovery_approval_digests", "octet_length(approval_statement_sha256) = 32 AND octet_length(approval_evidence_sha256) = 32");
                    table.CheckConstraint("ck_owner_recovery_approval_state", "approval_state_name IN ('Valid','Consumed','Withdrawn','Invalidated')");
                    table.ForeignKey(
                        name: "fk_owner_recovery_approval_case",
                        column: x => x.organization_owner_recovery_case_id,
                        principalSchema: "authority",
                        principalTable: "organization_owner_recovery_cases",
                        principalColumn: "organization_owner_recovery_case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_owner_recovery_approval_operator",
                        column: x => x.platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_owner_recovery_subjects",
                schema: "authority",
                columns: table => new
                {
                    organization_owner_recovery_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_owner_recovery_case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recovery_subject_role_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_ordinal_number = table.Column<int>(type: "integer", nullable: false),
                    subject_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject_display_name_hint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_new_identity = table.Column<bool>(type: "boolean", nullable: false),
                    verification_method_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    verification_reference_text = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    unavailability_reason_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    evidence_reference_text = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    evidence_summary_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_owner_recovery_subjects", x => x.organization_owner_recovery_subject_id);
                    table.CheckConstraint("ck_owner_recovery_subject_declared", "recovery_subject_role_name <> 'DeclaredUnavailable' OR (NOT is_new_identity AND unavailability_reason_name IS NOT NULL AND evidence_reference_text IS NOT NULL AND evidence_summary_text IS NOT NULL)");
                    table.CheckConstraint("ck_owner_recovery_subject_replacement", "recovery_subject_role_name <> 'Replacement' OR (verification_method_name IS NOT NULL AND verification_reference_text IS NOT NULL)");
                    table.CheckConstraint("ck_owner_recovery_subject_role", "recovery_subject_role_name IN ('Replacement','DeclaredUnavailable')");
                    table.ForeignKey(
                        name: "fk_owner_recovery_subject_case",
                        column: x => x.organization_owner_recovery_case_id,
                        principalSchema: "authority",
                        principalTable: "organization_owner_recovery_cases",
                        principalColumn: "organization_owner_recovery_case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_recovered_owner_probations",
                schema: "authority",
                columns: table => new
                {
                    organization_recovered_owner_probation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_owner_recovery_case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probation_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    probation_ends_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_recovered_owner_probations", x => x.organization_recovered_owner_probation_id);
                    table.CheckConstraint("ck_recovered_owner_probation_period", "probation_ends_on > probation_created_on");
                    table.ForeignKey(
                        name: "fk_recovered_owner_probation_case",
                        column: x => x.organization_owner_recovery_case_id,
                        principalSchema: "authority",
                        principalTable: "organization_owner_recovery_cases",
                        principalColumn: "organization_owner_recovery_case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_identity_bindings_organization_owner_recovery_case_id",
                schema: "authority",
                table: "identity_bindings",
                column: "organization_owner_recovery_case_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_binding_single_provenance",
                schema: "authority",
                table: "identity_bindings",
                sql: "num_nonnulls(organization_membership_request_id, organization_onboarding_request_id, organization_owner_recovery_case_id) <= 1");

            migrationBuilder.CreateIndex(
                name: "IX_organization_owner_recovery_approvals_platform_operator_id",
                schema: "authority",
                table: "organization_owner_recovery_approvals",
                column: "platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_approval_evidence",
                schema: "authority",
                table: "organization_owner_recovery_approvals",
                column: "approval_evidence_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_approval_evidence_id",
                schema: "authority",
                table: "organization_owner_recovery_approvals",
                column: "approval_evidence_identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_approval_operator",
                schema: "authority",
                table: "organization_owner_recovery_approvals",
                columns: new[] { "organization_owner_recovery_case_id", "platform_operator_id" },
                unique: true,
                filter: "approval_state_name IN ('Valid','Consumed')");

            migrationBuilder.CreateIndex(
                name: "IX_organization_owner_recovery_cases_closed_by_platform_operat~",
                schema: "authority",
                table: "organization_owner_recovery_cases",
                column: "closed_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_owner_recovery_cases_executed_by_platform_oper~",
                schema: "authority",
                table: "organization_owner_recovery_cases",
                column: "executed_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "ix_owner_recovery_cases_recent",
                schema: "authority",
                table: "organization_owner_recovery_cases",
                columns: new[] { "case_opened_on", "organization_owner_recovery_case_id" });

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_create_idempotency",
                schema: "authority",
                table: "organization_owner_recovery_cases",
                columns: new[] { "opened_by_platform_operator_id", "create_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_open_case",
                schema: "authority",
                table: "organization_owner_recovery_cases",
                column: "organization_id",
                unique: true,
                filter: "recovery_state_name = 'Open'");

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_subject_identity",
                schema: "authority",
                table: "organization_owner_recovery_subjects",
                columns: new[] { "organization_owner_recovery_case_id", "subject_issuer_uri", "subject_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_owner_recovery_subject_user",
                schema: "authority",
                table: "organization_owner_recovery_subjects",
                columns: new[] { "organization_owner_recovery_case_id", "recovery_subject_role_name", "subject_authority_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_recovered_owner_probations_organization_owner_~",
                schema: "authority",
                table: "organization_recovered_owner_probations",
                column: "organization_owner_recovery_case_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovered_owner_probations_active",
                schema: "authority",
                table: "organization_recovered_owner_probations",
                columns: new[] { "organization_id", "probation_ends_on" });

            migrationBuilder.CreateIndex(
                name: "uq_recovered_owner_probation",
                schema: "authority",
                table: "organization_recovered_owner_probations",
                columns: new[] { "organization_id", "authority_user_id", "organization_owner_recovery_case_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_identity_owner_recovery_case",
                schema: "authority",
                table: "identity_bindings",
                column: "organization_owner_recovery_case_id",
                principalSchema: "authority",
                principalTable: "organization_owner_recovery_cases",
                principalColumn: "organization_owner_recovery_case_id",
                onDelete: ReferentialAction.Restrict);

            // C-105: closed cases and approvals never change again; subjects and probations never change at all.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.organization_owner_recovery_cases_keep_final() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF OLD.recovery_state_name <> 'Open' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.organization_owner_recovery_cases_keep_final() FROM PUBLIC;
                CREATE TRIGGER trg_organization_owner_recovery_cases_keep_final BEFORE UPDATE ON authority.organization_owner_recovery_cases
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_owner_recovery_cases_keep_final();
                CREATE FUNCTION authority.organization_owner_recovery_approvals_keep_closed() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF OLD.approval_state_name <> 'Valid' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.organization_owner_recovery_approvals_keep_closed() FROM PUBLIC;
                CREATE TRIGGER trg_organization_owner_recovery_approvals_keep_closed BEFORE UPDATE ON authority.organization_owner_recovery_approvals
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_owner_recovery_approvals_keep_closed();
                CREATE FUNCTION authority.organization_owner_recovery_rows_immutable() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    RETURN OLD;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.organization_owner_recovery_rows_immutable() FROM PUBLIC;
                CREATE TRIGGER trg_organization_owner_recovery_subjects_immutable BEFORE UPDATE ON authority.organization_owner_recovery_subjects
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_owner_recovery_rows_immutable();
                CREATE TRIGGER trg_organization_recovered_owner_probations_immutable BEFORE UPDATE ON authority.organization_recovered_owner_probations
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_owner_recovery_rows_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_organization_owner_recovery_cases_keep_final ON authority.organization_owner_recovery_cases; DROP FUNCTION IF EXISTS authority.organization_owner_recovery_cases_keep_final(); "
                + "DROP TRIGGER IF EXISTS trg_organization_owner_recovery_approvals_keep_closed ON authority.organization_owner_recovery_approvals; DROP FUNCTION IF EXISTS authority.organization_owner_recovery_approvals_keep_closed(); "
                + "DROP TRIGGER IF EXISTS trg_organization_owner_recovery_subjects_immutable ON authority.organization_owner_recovery_subjects; "
                + "DROP TRIGGER IF EXISTS trg_organization_recovered_owner_probations_immutable ON authority.organization_recovered_owner_probations; DROP FUNCTION IF EXISTS authority.organization_owner_recovery_rows_immutable();");

            migrationBuilder.DropForeignKey(
                name: "fk_identity_owner_recovery_case",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropTable(
                name: "organization_owner_recovery_approvals",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_owner_recovery_subjects",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_recovered_owner_probations",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_owner_recovery_cases",
                schema: "authority");

            migrationBuilder.DropIndex(
                name: "IX_identity_bindings_organization_owner_recovery_case_id",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_binding_single_provenance",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropColumn(
                name: "organization_owner_recovery_case_id",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_binding_single_provenance",
                schema: "authority",
                table: "identity_bindings",
                sql: "organization_membership_request_id IS NULL OR organization_onboarding_request_id IS NULL");
        }
    }
}
