using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC8TeamMembershipReconciliation : Migration
    {
        private const string GuardFunctionsSql = """
            CREATE FUNCTION public.authority_command_operations_guard() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $guard$
            BEGIN
                IF NEW.authority_command_operation_id IS DISTINCT FROM OLD.authority_command_operation_id
                   OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                   OR NEW.initiated_by_user_id IS DISTINCT FROM OLD.initiated_by_user_id
                   OR NEW.command_kind_name IS DISTINCT FROM OLD.command_kind_name
                   OR NEW.team_id IS DISTINCT FROM OLD.team_id
                   OR NEW.member_user_id IS DISTINCT FROM OLD.member_user_id
                   OR NEW.authority_idempotency_key IS DISTINCT FROM OLD.authority_idempotency_key
                   OR NEW.request_correlation_identifier IS DISTINCT FROM OLD.request_correlation_identifier
                   OR NEW.operation_created_on IS DISTINCT FROM OLD.operation_created_on THEN
                    RAISE EXCEPTION 'AUTHORITY_COMMAND_OPERATION_IDENTITY_IMMUTABLE' USING ERRCODE = 'check_violation';
                END IF;
                IF (OLD.authority_command_id IS NOT NULL AND NEW.authority_command_id IS DISTINCT FROM OLD.authority_command_id)
                   OR (OLD.committed_authorization_revision IS NOT NULL AND NEW.committed_authorization_revision IS DISTINCT FROM OLD.committed_authorization_revision) THEN
                    RAISE EXCEPTION 'AUTHORITY_COMMAND_OPERATION_COMMAND_IMMUTABLE' USING ERRCODE = 'check_violation';
                END IF;
                IF OLD.operation_state_name IN ('Projected', 'Rejected', 'AuthorityFailed', 'NotCommitted')
                   AND (NEW.operation_state_name IS DISTINCT FROM OLD.operation_state_name
                        OR NEW.operation_outcome_code IS DISTINCT FROM OLD.operation_outcome_code
                        OR NEW.rejection_reason_code IS DISTINCT FROM OLD.rejection_reason_code
                        OR NEW.authority_command_id IS DISTINCT FROM OLD.authority_command_id
                        OR NEW.committed_authorization_revision IS DISTINCT FROM OLD.committed_authorization_revision
                        OR NEW.evaluated_projection_revision IS DISTINCT FROM OLD.evaluated_projection_revision
                        OR NEW.projected_membership_revision IS DISTINCT FROM OLD.projected_membership_revision
                        OR NEW.projected_membership_present IS DISTINCT FROM OLD.projected_membership_present
                        OR NEW.superseded_by_later_revision IS DISTINCT FROM OLD.superseded_by_later_revision
                        OR NEW.operation_completed_on IS DISTINCT FROM OLD.operation_completed_on) THEN
                    RAISE EXCEPTION 'AUTHORITY_COMMAND_OPERATION_TERMINAL' USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;
            END
            $guard$;

            CREATE TRIGGER trg_authority_command_operations_guard BEFORE UPDATE ON authority_command_operations
                FOR EACH ROW EXECUTE FUNCTION public.authority_command_operations_guard();

            CREATE FUNCTION public.team_membership_projection_states_guard() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $guard$
            BEGIN
                IF NEW.team_membership_projection_state_id IS DISTINCT FROM OLD.team_membership_projection_state_id
                   OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                   OR NEW.team_id IS DISTINCT FROM OLD.team_id
                   OR NEW.member_user_id IS DISTINCT FROM OLD.member_user_id THEN
                    RAISE EXCEPTION 'TEAM_MEMBERSHIP_PROJECTION_STATE_IDENTITY_IMMUTABLE' USING ERRCODE = 'check_violation';
                END IF;
                IF NEW.projected_membership_revision < OLD.projected_membership_revision THEN
                    RAISE EXCEPTION 'TEAM_MEMBERSHIP_PROJECTION_WATERMARK_LOWERED' USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;
            END
            $guard$;

            CREATE TRIGGER trg_team_membership_projection_states_guard BEFORE UPDATE ON team_membership_projection_states
                FOR EACH ROW EXECUTE FUNCTION public.team_membership_projection_states_guard();

            REVOKE ALL ON FUNCTION public.authority_command_operations_guard() FROM PUBLIC;
            REVOKE ALL ON FUNCTION public.team_membership_projection_states_guard() FROM PUBLIC;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authority_command_operations",
                columns: table => new
                {
                    authority_command_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_correlation_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    committed_authorization_revision = table.Column<long>(type: "bigint", nullable: true),
                    operation_state_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    operation_outcome_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    rejection_reason_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    evaluated_projection_revision = table.Column<long>(type: "bigint", nullable: true),
                    projected_membership_revision = table.Column<long>(type: "bigint", nullable: true),
                    projected_membership_present = table.Column<bool>(type: "boolean", nullable: true),
                    superseded_by_later_revision = table.Column<bool>(type: "boolean", nullable: true),
                    next_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    consecutive_error_count = table.Column<int>(type: "integer", nullable: false),
                    lease_owner_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_not_found_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    needs_attention_flagged_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reconciliation_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    late_commit_sweep_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    late_commit_detected_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    operation_reviewed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    operation_reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    operation_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_command_operations", x => x.authority_command_operation_id);
                    table.CheckConstraint("ck_authority_command_operations_counts", "attempt_count >= 0 AND consecutive_error_count >= 0");
                    table.CheckConstraint("ck_authority_command_operations_key_prefix", "(command_kind_name = 'AddTeamMember' AND authority_idempotency_key LIKE 'team-add:%') OR (command_kind_name = 'RemoveTeamMember' AND authority_idempotency_key LIKE 'team-remove:%')");
                    table.CheckConstraint("ck_authority_command_operations_kind", "command_kind_name IN ('AddTeamMember','RemoveTeamMember')");
                    table.CheckConstraint("ck_authority_command_operations_outcome", "operation_outcome_code IS NULL OR operation_outcome_code IN ('MembershipPresentProjected','MembershipAbsentProjected','ProjectionUnchanged','DriftCorrected')");
                    table.CheckConstraint("ck_authority_command_operations_projected_complete", "operation_state_name <> 'Projected' OR (authority_command_id IS NOT NULL AND committed_authorization_revision IS NOT NULL AND evaluated_projection_revision IS NOT NULL AND projected_membership_revision IS NOT NULL AND projected_membership_present IS NOT NULL AND operation_outcome_code IS NOT NULL AND operation_completed_on IS NOT NULL)");
                    table.CheckConstraint("ck_authority_command_operations_revisions", "(committed_authorization_revision IS NULL OR committed_authorization_revision >= 1) AND (evaluated_projection_revision IS NULL OR evaluated_projection_revision >= 0) AND (projected_membership_revision IS NULL OR projected_membership_revision >= 0)");
                    table.CheckConstraint("ck_authority_command_operations_state", "operation_state_name IN ('AwaitingCommit','AwaitingEffective','AwaitingQuarantineClearance','ReconciliationError','Projected','Rejected','AuthorityFailed','NotCommitted')");
                    table.ForeignKey(
                        name: "fk_authority_command_operations_initiator",
                        column: x => x.initiated_by_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_authority_command_operations_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_membership_projection_states",
                columns: table => new
                {
                    team_membership_projection_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    projected_membership_present = table.Column<bool>(type: "boolean", nullable: false),
                    projected_membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    last_authority_command_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    projection_state_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_membership_projection_states", x => x.team_membership_projection_state_id);
                    table.CheckConstraint("ck_team_membership_projection_states_revision", "projected_membership_revision >= 0");
                    table.ForeignKey(
                        name: "fk_team_membership_projection_states_member",
                        column: x => x.member_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_membership_projection_states_operation",
                        column: x => x.last_authority_command_operation_id,
                        principalTable: "authority_command_operations",
                        principalColumn: "authority_command_operation_id");
                    table.ForeignKey(
                        name: "fk_team_membership_projection_states_team",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_membership_projection_states_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authority_command_operations_due",
                table: "authority_command_operations",
                columns: new[] { "operation_state_name", "next_attempt_on" });

            migrationBuilder.CreateIndex(
                name: "IX_authority_command_operations_initiated_by_user_id",
                table: "authority_command_operations",
                column: "initiated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_authority_command_operations_team",
                table: "authority_command_operations",
                columns: new[] { "tenant_id", "team_id", "operation_state_name" });

            migrationBuilder.CreateIndex(
                name: "uq_authority_command_operations_key",
                table: "authority_command_operations",
                columns: new[] { "tenant_id", "authority_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_membership_projection_states_last_authority_command_op~",
                table: "team_membership_projection_states",
                column: "last_authority_command_operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_membership_projection_states_member_user_id",
                table: "team_membership_projection_states",
                column: "member_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_membership_projection_states_team_id",
                table: "team_membership_projection_states",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "uq_team_membership_projection_states_pair",
                table: "team_membership_projection_states",
                columns: new[] { "tenant_id", "team_id", "member_user_id" },
                unique: true);

            // Phase C8 (D8-18, D8-21, I-3): schema-owned guards against the runtime role. Owned by the migration identity and
            // never executable by PUBLIC; a BusinessDb administrator can still disable them (F-4, accepted §4M boundary).
            migrationBuilder.Sql(GuardFunctionsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_team_membership_projection_states_guard ON team_membership_projection_states; DROP TRIGGER IF EXISTS trg_authority_command_operations_guard ON authority_command_operations; DROP FUNCTION IF EXISTS public.team_membership_projection_states_guard(); DROP FUNCTION IF EXISTS public.authority_command_operations_guard();");

            migrationBuilder.DropTable(
                name: "team_membership_projection_states");

            migrationBuilder.DropTable(
                name: "authority_command_operations");
        }
    }
}
