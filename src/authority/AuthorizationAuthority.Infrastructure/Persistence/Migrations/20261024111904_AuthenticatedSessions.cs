using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthenticatedSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authenticated_sessions",
                schema: "authority",
                columns: table => new
                {
                    authenticated_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_secret_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    session_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_last_seen_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_idle_timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    session_revoked_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    session_revocation_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_by_workload_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authenticated_sessions", x => x.authenticated_session_id);
                    table.CheckConstraint("ck_session_digest_length", "octet_length(session_secret_digest) = 32");
                    table.CheckConstraint("ck_session_expiry_order", "session_expires_on > session_created_on AND session_idle_timeout_seconds > 0");
                    table.CheckConstraint("ck_session_revocation", "(session_revoked_on IS NULL AND session_revocation_reason IS NULL) OR (session_revoked_on IS NOT NULL AND session_revocation_reason IN ('LOGOUT','REPLACED_BY_NEW_SIGN_IN'))");
                    table.ForeignKey(
                        name: "fk_session_identity",
                        column: x => x.identity_binding_id,
                        principalSchema: "authority",
                        principalTable: "identity_bindings",
                        principalColumn: "identity_binding_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_session_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authenticated_sessions_identity_binding_id",
                schema: "authority",
                table: "authenticated_sessions",
                column: "identity_binding_id");

            migrationBuilder.CreateIndex(
                name: "IX_authenticated_sessions_organization_id",
                schema: "authority",
                table: "authenticated_sessions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "uq_session_secret_digest",
                schema: "authority",
                table: "authenticated_sessions",
                column: "session_secret_digest",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authenticated_sessions",
                schema: "authority");
        }
    }
}
