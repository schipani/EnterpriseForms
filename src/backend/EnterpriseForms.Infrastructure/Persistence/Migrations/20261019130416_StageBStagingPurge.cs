using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBStagingPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staging_object_purged_on",
                table: "upload_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_staging_purge",
                table: "upload_sessions",
                columns: new[] { "session_state", "session_expires_on" },
                filter: "staging_object_purged_on IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_upload_sessions_staging_purge",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "staging_object_purged_on",
                table: "upload_sessions");
        }
    }
}
