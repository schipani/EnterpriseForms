using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionRevocationMembershipDisabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_revocation",
                schema: "authority",
                table: "authenticated_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_revocation",
                schema: "authority",
                table: "authenticated_sessions",
                sql: "(session_revoked_on IS NULL AND session_revocation_reason IS NULL) OR (session_revoked_on IS NOT NULL AND session_revocation_reason IN ('LOGOUT','REPLACED_BY_NEW_SIGN_IN','MEMBERSHIP_DISABLED'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_revocation",
                schema: "authority",
                table: "authenticated_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_revocation",
                schema: "authority",
                table: "authenticated_sessions",
                sql: "(session_revoked_on IS NULL AND session_revocation_reason IS NULL) OR (session_revoked_on IS NOT NULL AND session_revocation_reason IN ('LOGOUT','REPLACED_BY_NEW_SIGN_IN'))");
        }
    }
}
