using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.FloorMigrations
{
    /// <summary>
    /// E3-M2d CARV floor 2 (owner decisions D-3, D-4). Belongs to <see cref="ContentAcceptanceFloorContext"/> only, so the ordinary
    /// Authority <c>--migrate</c> never applies it; applied only by <c>--apply-content-acceptance-floor 2</c>, which migrates the
    /// floor context to exactly this migration and only after floor 1. One transaction: lock timeout, ACCESS EXCLUSIVE lock,
    /// preconditions under the lock, then floor 2 added (NOT VALID) and floor 1 dropped. Down always refuses: floors are never
    /// lowered. E3-M2d exercises it only in disposable PostgreSQL; it is not installed in any persistent environment.
    /// </summary>
    public partial class AuthorityContentAcceptanceRuleFloor2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceFloor2Sql.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceFloor2Sql.DownSql);
        }
    }
}
