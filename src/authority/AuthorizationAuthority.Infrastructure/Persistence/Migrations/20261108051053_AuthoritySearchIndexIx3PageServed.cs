using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchIndexIx3PageServed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_page_served_on",
                schema: "authority_search",
                table: "search_result_snapshots",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_page_served_on",
                schema: "authority_search",
                table: "search_result_snapshots");
        }
    }
}
