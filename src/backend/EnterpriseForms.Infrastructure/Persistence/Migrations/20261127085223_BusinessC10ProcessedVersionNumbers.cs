using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessC10ProcessedVersionNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_file_attachment_versions_number_operation",
                table: "file_attachment_versions");

            migrationBuilder.CreateIndex(
                name: "uq_file_attachment_versions_number_operation",
                table: "file_attachment_versions",
                columns: new[] { "file_attachment_id", "version_number", "content_change_operation_id" },
                unique: true,
                filter: "content_change_operation_id <> '00000000-0000-0000-0000-000000000000'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_file_attachment_versions_number_operation",
                table: "file_attachment_versions");

            migrationBuilder.CreateIndex(
                name: "uq_file_attachment_versions_number_operation",
                table: "file_attachment_versions",
                columns: new[] { "file_attachment_id", "version_number", "content_change_operation_id" },
                unique: true);
        }
    }
}
