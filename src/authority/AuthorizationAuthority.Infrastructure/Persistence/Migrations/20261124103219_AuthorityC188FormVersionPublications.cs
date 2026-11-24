using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityC188FormVersionPublications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "form_version_publications",
                schema: "authority",
                columns: table => new
                {
                    form_version_publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_version_number = table.Column<int>(type: "integer", nullable: false),
                    business_form_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    field_manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    form_title_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    publication_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    prepared_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prepared_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_version_publications", x => x.form_version_publication_id);
                    table.CheckConstraint("ck_form_version_publication_digest", "octet_length(layout_digest) = 32");
                    table.CheckConstraint("ck_form_version_publication_number", "form_version_number >= 1");
                    table.CheckConstraint("ck_form_version_publication_resolution", "(publication_state_name = 'Pending') = (resolved_by_security_command_id IS NULL AND resolved_on IS NULL)");
                    table.CheckConstraint("ck_form_version_publication_state", "publication_state_name IN ('Pending','Registered','Abandoned','Superseded')");
                    table.ForeignKey(
                        name: "fk_form_version_publication_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_version_publication_prepared_command",
                        column: x => x.prepared_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_version_publication_resolved_command",
                        column: x => x.resolved_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_form_version_publication_pending",
                schema: "authority",
                table: "form_version_publications",
                columns: new[] { "organization_id", "form_definition_id" },
                filter: "publication_state_name = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_form_version_publications_prepared_by_security_command_id",
                schema: "authority",
                table: "form_version_publications",
                column: "prepared_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_version_publications_resolved_by_security_command_id",
                schema: "authority",
                table: "form_version_publications",
                column: "resolved_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_version_publication_business_version",
                schema: "authority",
                table: "form_version_publications",
                columns: new[] { "organization_id", "business_form_version_id" },
                unique: true);

            // C-188 BF-1: a resolved (Registered, Abandoned, Superseded) publication is final, and a Pending one may change only to a terminal
            // state with its resolution stamps - the fail-closed state can never be re-entered or rewritten, and the staged manifest, digest and
            // identities are immutable.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.guard_form_version_publication_transition() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF OLD.publication_state_name <> 'Pending' THEN
                        RAISE EXCEPTION 'C-188 BF-1: a % form version publication is final and cannot change', OLD.publication_state_name USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.publication_state_name = 'Pending' THEN
                        RAISE EXCEPTION 'C-188 BF-1: a pending form version publication changes only by being resolved or superseded' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NEW.form_version_publication_id IS DISTINCT FROM OLD.form_version_publication_id
                       OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
                       OR NEW.form_definition_id IS DISTINCT FROM OLD.form_definition_id
                       OR NEW.form_version_number IS DISTINCT FROM OLD.form_version_number
                       OR NEW.business_form_version_id IS DISTINCT FROM OLD.business_form_version_id
                       OR NEW.layout_digest IS DISTINCT FROM OLD.layout_digest
                       OR NEW.field_manifest_json IS DISTINCT FROM OLD.field_manifest_json
                       OR NEW.form_title_snapshot IS DISTINCT FROM OLD.form_title_snapshot
                       OR NEW.prepared_by_security_command_id IS DISTINCT FROM OLD.prepared_by_security_command_id
                       OR NEW.prepared_on IS DISTINCT FROM OLD.prepared_on THEN
                        RAISE EXCEPTION 'C-188 BF-1: the staged publication is immutable' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER trg_form_version_publication_transition
                    BEFORE UPDATE ON authority.form_version_publications
                    FOR EACH ROW EXECUTE FUNCTION authority.guard_form_version_publication_transition();

                CREATE FUNCTION authority.check_single_pending_form_version_publication() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF (SELECT count(*) FROM authority.form_version_publications p
                        WHERE p.organization_id = NEW.organization_id AND p.form_definition_id = NEW.form_definition_id AND p.publication_state_name = 'Pending') > 1 THEN
                        RAISE EXCEPTION 'C-188 BF-1: a form has at most one pending publication' USING ERRCODE = 'unique_violation';
                    END IF;
                    RETURN NULL;
                END
                $$;
                CREATE CONSTRAINT TRIGGER trg_form_version_publication_single_pending
                    AFTER INSERT OR UPDATE ON authority.form_version_publications
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION authority.check_single_pending_form_version_publication();
                REVOKE ALL ON FUNCTION authority.guard_form_version_publication_transition() FROM PUBLIC;
                REVOKE ALL ON FUNCTION authority.check_single_pending_form_version_publication() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Development/Test only: dropping the table removes every pending publication and therefore the BF-1 barrier. A production
            // rollback never runs Down; see docs/PHASE_C9_STATUS.md (C-188 BF-1).
            migrationBuilder.DropTable(
                name: "form_version_publications",
                schema: "authority");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS authority.guard_form_version_publication_transition();
                DROP FUNCTION IF EXISTS authority.check_single_pending_form_version_publication();
                """);
        }
    }
}
