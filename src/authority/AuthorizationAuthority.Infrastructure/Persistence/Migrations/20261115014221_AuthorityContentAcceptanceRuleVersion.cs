using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// E3-M2b-1 CARV (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §19.5): two nullable stamp columns on
    /// <c>content_change_operations</c>, two strictly two-valued CHECKs (commit stamp equals the Prepare stamp, or both NULL for a
    /// legacy operation; no commit stamp outside Committed) and the state-guard trigger (insert only Prepared without a commit
    /// stamp; terminal rows immutable; Prepare stamp immutable). Expand-only: a pre-CARV build keeps working (its EF model maps
    /// neither column) until the separate floor migration E3-M2b-1F, which is NOT part of this migration.
    /// <para>The whole Up runs in the migration's own transaction with <c>lock_timeout = 5s</c>: a timeout applies nothing.</para>
    /// <para>Down is Development/Testing only and guarded (refuses once a floor exists or while CARV-prepared operations are in flight).</para>
    /// </summary>
    public partial class AuthorityContentAcceptanceRuleVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceRuleSql.UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentAcceptanceRuleSql.DownSql);
        }
    }
}
