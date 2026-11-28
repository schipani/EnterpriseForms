using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityC10AttachmentVersionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attachment_version_number",
                schema: "authority",
                table: "record_manifest_leaves",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "attachment_version_number",
                schema: "authority",
                table: "content_candidate_leaves",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attachment_version_number",
                schema: "authority",
                table: "record_manifest_leaves");

            migrationBuilder.DropColumn(
                name: "attachment_version_number",
                schema: "authority",
                table: "content_candidate_leaves");
        }
    }
}
