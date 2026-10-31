using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembershipRequestEditCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_request_state",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.AddColumn<Guid>(
                name: "replaces_membership_request_id",
                schema: "authority",
                table: "organization_membership_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_cancellation_reason_text",
                schema: "authority",
                table: "organization_membership_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "request_cancelled_by_authority_user_id",
                schema: "authority",
                table: "organization_membership_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "request_cancelled_on",
                schema: "authority",
                table: "organization_membership_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_membership_request_replaces",
                schema: "authority",
                table: "organization_membership_requests",
                column: "replaces_membership_request_id",
                unique: true,
                filter: "replaces_membership_request_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_request_cancellation",
                schema: "authority",
                table: "organization_membership_requests",
                sql: "(request_state_name IN ('Cancelled','Replaced')) = (request_cancelled_on IS NOT NULL AND request_cancelled_by_authority_user_id IS NOT NULL AND request_cancellation_reason_text IS NOT NULL AND decided_by_security_command_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_request_lineage",
                schema: "authority",
                table: "organization_membership_requests",
                sql: "replaces_membership_request_id IS DISTINCT FROM organization_membership_request_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_request_state",
                schema: "authority",
                table: "organization_membership_requests",
                sql: "request_state_name IN ('Pending','Approved','Rejected','Expired','Cancelled','Replaced')");

            migrationBuilder.AddForeignKey(
                name: "fk_membership_request_replaces",
                schema: "authority",
                table: "organization_membership_requests",
                column: "replaces_membership_request_id",
                principalSchema: "authority",
                principalTable: "organization_membership_requests",
                principalColumn: "organization_membership_request_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_membership_request_replaces",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropIndex(
                name: "uq_membership_request_replaces",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_request_cancellation",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_request_lineage",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_request_state",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropColumn(
                name: "replaces_membership_request_id",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropColumn(
                name: "request_cancellation_reason_text",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropColumn(
                name: "request_cancelled_by_authority_user_id",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.DropColumn(
                name: "request_cancelled_on",
                schema: "authority",
                table: "organization_membership_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_request_state",
                schema: "authority",
                table: "organization_membership_requests",
                sql: "request_state_name IN ('Pending','Approved','Rejected','Expired')");
        }
    }
}
