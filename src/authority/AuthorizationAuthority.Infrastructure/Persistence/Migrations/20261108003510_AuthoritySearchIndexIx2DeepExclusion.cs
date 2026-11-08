using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchIndexIx2DeepExclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "deep_deadline_excluded_entry_count",
                schema: "authority_search",
                table: "search_projection_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_projection_deep_exclusions",
                schema: "authority_search",
                table: "search_projection_states",
                sql: "deep_deadline_excluded_entry_count >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_search_projection_deep_exclusions",
                schema: "authority_search",
                table: "search_projection_states");

            migrationBuilder.DropColumn(
                name: "deep_deadline_excluded_entry_count",
                schema: "authority_search",
                table: "search_projection_states");
        }
    }
}
