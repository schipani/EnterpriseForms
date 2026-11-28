using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityC10AttachmentContentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "attachment_content_fingerprint",
                schema: "authority",
                table: "record_manifest_leaves",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "accepted_attachment_content_fingerprint",
                schema: "authority",
                table: "content_candidate_leaves",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "accepted_attachment_version_number",
                schema: "authority",
                table: "content_candidate_leaves",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_manifest_leaf_attachment_fingerprint_length",
                schema: "authority",
                table: "record_manifest_leaves",
                sql: "attachment_content_fingerprint IS NULL OR octet_length(attachment_content_fingerprint) = 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_candidate_leaf_accepted_attachment_evidence",
                schema: "authority",
                table: "content_candidate_leaves",
                sql: "(accepted_attachment_version_number IS NULL AND accepted_attachment_content_fingerprint IS NULL) OR (accepted_attachment_version_number > 0 AND octet_length(accepted_attachment_content_fingerprint) = 32)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_manifest_leaf_attachment_fingerprint_length",
                schema: "authority",
                table: "record_manifest_leaves");

            migrationBuilder.DropCheckConstraint(
                name: "ck_candidate_leaf_accepted_attachment_evidence",
                schema: "authority",
                table: "content_candidate_leaves");

            migrationBuilder.DropColumn(
                name: "attachment_content_fingerprint",
                schema: "authority",
                table: "record_manifest_leaves");

            migrationBuilder.DropColumn(
                name: "accepted_attachment_content_fingerprint",
                schema: "authority",
                table: "content_candidate_leaves");

            migrationBuilder.DropColumn(
                name: "accepted_attachment_version_number",
                schema: "authority",
                table: "content_candidate_leaves");
        }
    }
}
