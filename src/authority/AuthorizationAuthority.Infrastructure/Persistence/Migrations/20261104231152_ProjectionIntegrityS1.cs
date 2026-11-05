using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// C8 M4 S1 (Design B revision 2, C-117).
    /// <list type="number">
    /// <item>Rollback fence: the Authority-held ZedToken moves to <c>projection_snapshot_zed_token</c>; the pre-S1 column
    /// <c>projection_zed_token</c> is emptied and constrained to NULL. A pre-S1 Authority build then reads no token (its gate stays
    /// closed) and cannot store one (its projection commit violates the constraint): a rollback fails closed, never silently.</item>
    /// <item>Permanent quarantine (B-Q7): once <c>integrity_quarantine_enabled</c> is true, no UPDATE may clear it or change its
    /// reason. A future G14 recovery process lifts it only through its own reviewed migration that replaces this function under
    /// the separately controlled migration identity (and records the recovery); the runtime role cannot alter functions.</item>
    /// </list>
    /// </summary>
    public partial class ProjectionIntegrityS1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "projection_snapshot_zed_token",
                schema: "authority",
                table: "organization_authorization_states",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE authority.organization_authorization_states
                   SET projection_snapshot_zed_token = projection_zed_token,
                       projection_zed_token = NULL
                 WHERE projection_zed_token IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_legacy_projection_token_retired",
                schema: "authority",
                table: "organization_authorization_states",
                sql: "projection_zed_token IS NULL");

            migrationBuilder.Sql("""
                CREATE FUNCTION authority.organization_integrity_quarantine_is_permanent() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
                BEGIN
                    IF OLD.integrity_quarantine_enabled
                       AND (NOT NEW.integrity_quarantine_enabled
                            OR NEW.integrity_quarantine_reason_code IS DISTINCT FROM OLD.integrity_quarantine_reason_code) THEN
                        RAISE EXCEPTION 'integrity quarantine is permanent under normal operation (organization %)', OLD.organization_id
                            USING ERRCODE = 'P0001';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                REVOKE ALL ON FUNCTION authority.organization_integrity_quarantine_is_permanent() FROM PUBLIC;
                CREATE TRIGGER trg_organization_integrity_quarantine_is_permanent BEFORE UPDATE ON authority.organization_authorization_states
                    FOR EACH ROW EXECUTE FUNCTION authority.organization_integrity_quarantine_is_permanent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Development/Testing only: never roll a production Authority back below S1 (minimum safe version, C-117).
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_organization_integrity_quarantine_is_permanent ON authority.organization_authorization_states;
                DROP FUNCTION IF EXISTS authority.organization_integrity_quarantine_is_permanent();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_legacy_projection_token_retired",
                schema: "authority",
                table: "organization_authorization_states");

            migrationBuilder.Sql("""
                UPDATE authority.organization_authorization_states
                   SET projection_zed_token = projection_snapshot_zed_token
                 WHERE projection_snapshot_zed_token IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "projection_snapshot_zed_token",
                schema: "authority",
                table: "organization_authorization_states");
        }
    }
}
