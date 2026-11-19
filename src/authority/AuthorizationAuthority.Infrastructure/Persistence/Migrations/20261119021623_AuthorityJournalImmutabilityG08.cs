using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SEC-BL-01 G08 (C-158, contract §11 M-6 as amended by A-7 §27): security-journal immutability triggers (immutable fields, delivery-state
    /// transitions, no delete or truncate; see <see cref="JournalImmutabilitySql"/>). No table or column change. The column-level runtime
    /// UPDATE grant is applied by <c>AuthorityRuntimeGrants</c> after migration. Down (Development/Testing) drops the triggers only.
    /// </summary>
    public partial class AuthorityJournalImmutabilityG08 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(JournalImmutabilitySql.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(JournalImmutabilitySql.DownSql);
        }
    }
}
