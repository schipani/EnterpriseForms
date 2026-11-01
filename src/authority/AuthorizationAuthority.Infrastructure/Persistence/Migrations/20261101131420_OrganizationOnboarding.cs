using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "organization_onboarding_request_id",
                schema: "authority",
                table: "organization_authorization_states",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_onboarding_request_id",
                schema: "authority",
                table: "identity_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_onboarding_requests",
                schema: "authority",
                columns: table => new
                {
                    organization_onboarding_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reserved_organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    organization_normalized_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    identity_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    onboarding_state_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    request_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    create_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    create_request_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    onboarding_statement_json = table.Column<string>(type: "text", nullable: false),
                    onboarding_statement_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    integrity_key_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    onboarding_approved_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    key_custody_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    onboarding_initialized_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    initialized_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initialized_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    onboarding_closed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_platform_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    onboarding_closure_reason_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    closure_justification_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_initialization_failure_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    last_initialization_failure_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    onboarding_row_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_onboarding_requests", x => x.organization_onboarding_request_id);
                    table.CheckConstraint("ck_onboarding_approved", "(onboarding_state_name IN ('AwaitingKeyCustody','Initialized')) <= (onboarding_approved_on IS NOT NULL AND key_custody_expires_on IS NOT NULL)");
                    table.CheckConstraint("ck_onboarding_closed", "(onboarding_state_name IN ('Rejected','Cancelled','Expired')) = (onboarding_closed_on IS NOT NULL AND onboarding_closure_reason_name IS NOT NULL)");
                    table.CheckConstraint("ck_onboarding_expiry", "request_expires_on > request_created_on");
                    table.CheckConstraint("ck_onboarding_initialized", "(onboarding_state_name = 'Initialized') = (onboarding_initialized_on IS NOT NULL AND initialized_by_platform_operator_id IS NOT NULL AND initialized_by_security_command_id IS NOT NULL)");
                    table.CheckConstraint("ck_onboarding_revision", "onboarding_row_revision > 0");
                    table.CheckConstraint("ck_onboarding_state", "onboarding_state_name IN ('PendingApproval','AwaitingKeyCustody','Initialized','Rejected','Cancelled','Expired')");
                    table.CheckConstraint("ck_onboarding_statement", "octet_length(onboarding_statement_sha256) = 32 AND octet_length(create_request_digest) = 32");
                    table.ForeignKey(
                        name: "fk_onboarding_closer",
                        column: x => x.closed_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_onboarding_creator",
                        column: x => x.created_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_onboarding_initializer",
                        column: x => x.initialized_by_platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_onboarding_approvals",
                schema: "authority",
                columns: table => new
                {
                    organization_onboarding_approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_onboarding_request_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_organization_onboarding_approvals", x => x.organization_onboarding_approval_id);
                    table.CheckConstraint("ck_onboarding_approval_closed", "(approval_state_name IN ('Withdrawn','Invalidated')) = (approval_closed_on IS NOT NULL AND approval_closure_reason_name IS NOT NULL)");
                    table.CheckConstraint("ck_onboarding_approval_digests", "octet_length(approval_statement_sha256) = 32 AND octet_length(approval_evidence_sha256) = 32");
                    table.CheckConstraint("ck_onboarding_approval_state", "approval_state_name IN ('Valid','Consumed','Withdrawn','Invalidated')");
                    table.ForeignKey(
                        name: "fk_onboarding_approval_operator",
                        column: x => x.platform_operator_id,
                        principalSchema: "authority",
                        principalTable: "platform_operators",
                        principalColumn: "platform_operator_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_onboarding_approval_request",
                        column: x => x.organization_onboarding_request_id,
                        principalSchema: "authority",
                        principalTable: "organization_onboarding_requests",
                        principalColumn: "organization_onboarding_request_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_onboarding_owners",
                schema: "authority",
                columns: table => new
                {
                    organization_onboarding_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_onboarding_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_ordinal_number = table.Column<int>(type: "integer", nullable: false),
                    reserved_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    owner_subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    owner_display_name_hint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_verification_method_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    owner_verification_reference_text = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_onboarding_owners", x => x.organization_onboarding_owner_id);
                    table.CheckConstraint("ck_onboarding_owner_method", "owner_verification_method_name IN ('SupportCallback','ContractSignatory','CustomerAdministratorAttestation')");
                    table.CheckConstraint("ck_onboarding_owner_ordinal", "owner_ordinal_number BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "fk_onboarding_owner_request",
                        column: x => x.organization_onboarding_request_id,
                        principalSchema: "authority",
                        principalTable: "organization_onboarding_requests",
                        principalColumn: "organization_onboarding_request_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_organization_onboarding_request",
                schema: "authority",
                table: "organization_authorization_states",
                column: "organization_onboarding_request_id",
                unique: true,
                filter: "organization_onboarding_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_identity_bindings_organization_onboarding_request_id",
                schema: "authority",
                table: "identity_bindings",
                column: "organization_onboarding_request_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_binding_single_provenance",
                schema: "authority",
                table: "identity_bindings",
                sql: "organization_membership_request_id IS NULL OR organization_onboarding_request_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_approvals_request",
                schema: "authority",
                table: "organization_onboarding_approvals",
                columns: new[] { "organization_onboarding_request_id", "approval_recorded_on" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_onboarding_approvals_platform_operator_id",
                schema: "authority",
                table: "organization_onboarding_approvals",
                column: "platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_approval_evidence",
                schema: "authority",
                table: "organization_onboarding_approvals",
                column: "approval_evidence_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_approval_evidence_id",
                schema: "authority",
                table: "organization_onboarding_approvals",
                column: "approval_evidence_identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_approval_operator",
                schema: "authority",
                table: "organization_onboarding_approvals",
                columns: new[] { "organization_onboarding_request_id", "platform_operator_id" },
                unique: true,
                filter: "approval_state_name IN ('Valid','Consumed')");

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_owner_identity",
                schema: "authority",
                table: "organization_onboarding_owners",
                columns: new[] { "organization_onboarding_request_id", "owner_issuer_uri", "owner_subject_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_owner_ordinal",
                schema: "authority",
                table: "organization_onboarding_owners",
                columns: new[] { "organization_onboarding_request_id", "owner_ordinal_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_owner_user",
                schema: "authority",
                table: "organization_onboarding_owners",
                column: "reserved_authority_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_requests_recent",
                schema: "authority",
                table: "organization_onboarding_requests",
                columns: new[] { "request_created_on", "organization_onboarding_request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_onboarding_requests_closed_by_platform_operato~",
                schema: "authority",
                table: "organization_onboarding_requests",
                column: "closed_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_onboarding_requests_initialized_by_platform_op~",
                schema: "authority",
                table: "organization_onboarding_requests",
                column: "initialized_by_platform_operator_id");

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_create_idempotency",
                schema: "authority",
                table: "organization_onboarding_requests",
                columns: new[] { "created_by_platform_operator_id", "create_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_integrity_key",
                schema: "authority",
                table: "organization_onboarding_requests",
                column: "integrity_key_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_open_name",
                schema: "authority",
                table: "organization_onboarding_requests",
                column: "organization_normalized_name",
                unique: true,
                filter: "onboarding_state_name IN ('PendingApproval','AwaitingKeyCustody')");

            migrationBuilder.CreateIndex(
                name: "uq_onboarding_reserved_organization",
                schema: "authority",
                table: "organization_onboarding_requests",
                column: "reserved_organization_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_identity_onboarding_request",
                schema: "authority",
                table: "identity_bindings",
                column: "organization_onboarding_request_id",
                principalSchema: "authority",
                principalTable: "organization_onboarding_requests",
                principalColumn: "organization_onboarding_request_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_organization_onboarding_request",
                schema: "authority",
                table: "organization_authorization_states",
                column: "organization_onboarding_request_id",
                principalSchema: "authority",
                principalTable: "organization_onboarding_requests",
                principalColumn: "organization_onboarding_request_id",
                onDelete: ReferentialAction.Restrict);

            // C8 onboarding (C-104): closed or initialized requests and closed approvals never change again. An UPDATE of such a row keeps
            // the stored row (a replayed identical row or a concurrent second closer is a no-op instead of a failed transaction).
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.organization_onboarding_requests_keep_final() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF OLD.onboarding_state_name IN ('Initialized', 'Rejected', 'Cancelled', 'Expired') THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.organization_onboarding_requests_keep_final() FROM PUBLIC;
                CREATE TRIGGER trg_organization_onboarding_requests_keep_final BEFORE UPDATE ON authority.organization_onboarding_requests
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_onboarding_requests_keep_final();
                CREATE FUNCTION authority.organization_onboarding_approvals_keep_closed() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF OLD.approval_state_name <> 'Valid' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.organization_onboarding_approvals_keep_closed() FROM PUBLIC;
                CREATE TRIGGER trg_organization_onboarding_approvals_keep_closed BEFORE UPDATE ON authority.organization_onboarding_approvals
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_onboarding_approvals_keep_closed();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_organization_onboarding_requests_keep_final ON authority.organization_onboarding_requests; DROP FUNCTION IF EXISTS authority.organization_onboarding_requests_keep_final(); "
                + "DROP TRIGGER IF EXISTS trg_organization_onboarding_approvals_keep_closed ON authority.organization_onboarding_approvals; DROP FUNCTION IF EXISTS authority.organization_onboarding_approvals_keep_closed();");

            migrationBuilder.DropForeignKey(
                name: "fk_identity_onboarding_request",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropForeignKey(
                name: "fk_organization_onboarding_request",
                schema: "authority",
                table: "organization_authorization_states");

            migrationBuilder.DropTable(
                name: "organization_onboarding_approvals",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_onboarding_owners",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_onboarding_requests",
                schema: "authority");

            migrationBuilder.DropIndex(
                name: "uq_organization_onboarding_request",
                schema: "authority",
                table: "organization_authorization_states");

            migrationBuilder.DropIndex(
                name: "IX_identity_bindings_organization_onboarding_request_id",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_binding_single_provenance",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropColumn(
                name: "organization_onboarding_request_id",
                schema: "authority",
                table: "organization_authorization_states");

            migrationBuilder.DropColumn(
                name: "organization_onboarding_request_id",
                schema: "authority",
                table: "identity_bindings");
        }
    }
}
