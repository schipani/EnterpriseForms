using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySecurityCommandTimelineIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_journal_org_created",
                schema: "authority",
                table: "security_journal_entries",
                columns: new[] { "organization_id", "journal_created_on", "journal_stream_name", "journal_stream_revision" });

            migrationBuilder.CreateIndex(
                name: "ix_security_commands_org_created",
                schema: "authority",
                table: "security_commands",
                columns: new[] { "organization_id", "command_created_on", "security_command_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_journal_org_created",
                schema: "authority",
                table: "security_journal_entries");

            migrationBuilder.DropIndex(
                name: "ix_security_commands_org_created",
                schema: "authority",
                table: "security_commands");
        }
    }
}
