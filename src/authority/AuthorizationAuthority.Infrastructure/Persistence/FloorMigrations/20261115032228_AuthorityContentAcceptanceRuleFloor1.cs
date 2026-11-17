using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.FloorMigrations
{
    /// <summary>
    /// E3-M2b-1F CARV floor 1 (Release B; contract §21). Belongs to <see cref="ContentAcceptanceFloorContext"/> only, so the
    /// ordinary Authority <c>--migrate</c> never applies it; applied only by <c>--apply-content-acceptance-floor 1</c>. One
    /// transaction: lock timeout, ACCESS EXCLUSIVE lock, preconditions under the lock, then the permanently NOT VALID floor CHECK
    /// covering Prepared and Committed rows. Down always refuses: floors are never lowered.
    /// </summary>
    public partial class AuthorityContentAcceptanceRuleFloor1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceFloorSql.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceFloorSql.DownSql);
        }
    }
}
