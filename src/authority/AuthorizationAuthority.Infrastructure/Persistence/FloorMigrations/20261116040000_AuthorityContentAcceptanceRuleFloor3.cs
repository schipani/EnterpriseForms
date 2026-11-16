using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.FloorMigrations
{
    /// <summary>
    /// E3-M2e CARV floor 3 (docs/security/E3_M2E_CONTRACT_CLOSURE.md AM-1, OD-M2e-06). Belongs to <see cref="ContentAcceptanceFloorContext"/>
    /// only, so the ordinary Authority <c>--migrate</c> never applies it (the data-key ledger migration installs no floor); applied only by
    /// <c>--apply-content-acceptance-floor 3</c>, which migrates the floor context to exactly this migration and only after floor 2. One
    /// transaction: lock timeout, ACCESS EXCLUSIVE lock, preconditions under the lock (including the data-key ledger objects), then floor 3
    /// added (NOT VALID) and floor 2 dropped. Down always refuses: floors are never lowered. E3-M2e-1 exercises it only in disposable
    /// PostgreSQL; it is not installed in any persistent environment.
    /// </summary>
    public partial class AuthorityContentAcceptanceRuleFloor3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceFloor3Sql.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceFloor3Sql.DownSql);
        }
    }
}
