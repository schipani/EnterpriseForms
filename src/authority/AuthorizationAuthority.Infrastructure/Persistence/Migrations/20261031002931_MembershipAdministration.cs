using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembershipAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "identity_disabled_by_security_command_id",
                schema: "authority",
                table: "identity_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "identity_disabled_on",
                schema: "authority",
                table: "identity_bindings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "identity_enabled_by_security_command_id",
                schema: "authority",
                table: "identity_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_membership_request_id",
                schema: "authority",
                table: "identity_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_membership_requests",
                schema: "authority",
                columns: table => new
                {
                    organization_membership_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    requested_subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    requested_display_name_hint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    requested_initial_role_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    requested_by_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    request_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    request_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    request_decided_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    request_decided_by_operator_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    request_decision_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    decided_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_membership_requests", x => x.organization_membership_request_id);
                    table.CheckConstraint("ck_membership_request_approved", "request_state_name <> 'Approved' OR decided_by_security_command_id IS NOT NULL");
                    table.CheckConstraint("ck_membership_request_expiry", "request_expires_on > request_created_on");
                    table.CheckConstraint("ck_membership_request_role", "requested_initial_role_name IS NULL OR requested_initial_role_name = 'designer'");
                    table.CheckConstraint("ck_membership_request_state", "request_state_name IN ('Pending','Approved','Rejected','Expired')");
                    table.ForeignKey(
                        name: "fk_membership_request_command",
                        column: x => x.organization_membership_request_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_membership_request_decision",
                        column: x => x.decided_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id");
                    table.ForeignKey(
                        name: "fk_membership_request_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_role_change_proposals",
                schema: "authority",
                columns: table => new
                {
                    organization_role_change_proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_change_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    proposed_by_authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_reason_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    proposal_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proposal_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proposal_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    consumed_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_consumed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_role_change_proposals", x => x.organization_role_change_proposal_id);
                    table.CheckConstraint("ck_role_change_proposal_change", "proposed_change_name IN ('GrantOwner','RevokeOwner','DisableOwner','EnableOwner')");
                    table.CheckConstraint("ck_role_change_proposal_expiry", "proposal_expires_on > proposal_created_on");
                    table.CheckConstraint("ck_role_change_proposal_not_self", "proposed_by_authority_user_id <> target_authority_user_id");
                    table.CheckConstraint("ck_role_change_proposal_state", "(proposal_state_name = 'Pending' AND consumed_by_security_command_id IS NULL) OR (proposal_state_name = 'Consumed' AND consumed_by_security_command_id IS NOT NULL AND proposal_consumed_on IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_role_change_proposal_command",
                        column: x => x.organization_role_change_proposal_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_change_proposal_consumer",
                        column: x => x.consumed_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id");
                    table.ForeignKey(
                        name: "fk_role_change_proposal_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_binding_disabled_state",
                schema: "authority",
                table: "identity_bindings",
                sql: "(identity_binding_enabled AND identity_disabled_on IS NULL) OR (NOT identity_binding_enabled AND identity_disabled_on IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_membership_requests_recent",
                schema: "authority",
                table: "organization_membership_requests",
                columns: new[] { "organization_id", "request_created_on" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_membership_requests_decided_by_security_comman~",
                schema: "authority",
                table: "organization_membership_requests",
                column: "decided_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "uq_membership_request_pending_identity",
                schema: "authority",
                table: "organization_membership_requests",
                columns: new[] { "organization_id", "requested_issuer_uri", "requested_subject_identifier" },
                unique: true,
                filter: "request_state_name = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "uq_membership_request_user",
                schema: "authority",
                table: "organization_membership_requests",
                columns: new[] { "organization_id", "requested_authority_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_role_change_proposals_consumed_by_security_com~",
                schema: "authority",
                table: "organization_role_change_proposals",
                column: "consumed_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_change_proposals_recent",
                schema: "authority",
                table: "organization_role_change_proposals",
                columns: new[] { "organization_id", "proposal_created_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_membership_requests",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_role_change_proposals",
                schema: "authority");

            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_binding_disabled_state",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropColumn(
                name: "identity_disabled_by_security_command_id",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropColumn(
                name: "identity_disabled_on",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropColumn(
                name: "identity_enabled_by_security_command_id",
                schema: "authority",
                table: "identity_bindings");

            migrationBuilder.DropColumn(
                name: "organization_membership_request_id",
                schema: "authority",
                table: "identity_bindings");
        }
    }
}
