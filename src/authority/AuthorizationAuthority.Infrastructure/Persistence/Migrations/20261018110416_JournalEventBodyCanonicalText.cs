using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A-F12: jsonb normalized the stored journal body so it no longer matched JournalEventDigest; the column becomes
    /// text so bytes are stored exactly as digested. Rows written while the column was jsonb keep their normalized
    /// form; ArchiveProcessor refuses to archive them (JOURNAL_BODY_DIGEST_MISMATCH, organization quarantined) rather
    /// than re-signing. No deployed authority store existed before this migration (Stage A, Docker-only).
    /// </summary>
    public partial class JournalEventBodyCanonicalText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "journal_event_body_json",
                schema: "authority",
                table: "security_journal_entries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "journal_event_body_json",
                schema: "authority",
                table: "security_journal_entries",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
