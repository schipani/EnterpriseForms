using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoritySearchAccessIx5a : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "authority_search_access");

            migrationBuilder.CreateTable(
                name: "search_access_checkpoints",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_projection_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    consumed_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    consumed_journal_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    checkpoint_revision = table.Column<long>(type: "bigint", nullable: false),
                    access_projection_protocol_version = table.Column<int>(type: "integer", nullable: false),
                    quarantine_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    checkpoint_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_checkpoints", x => x.search_access_checkpoint_id);
                    table.CheckConstraint("ck_search_access_checkpoint_digest", "(consumed_authorization_revision = 0) = (consumed_journal_digest IS NULL) AND (consumed_journal_digest IS NULL OR octet_length(consumed_journal_digest) = 32)");
                    table.CheckConstraint("ck_search_access_checkpoint_quarantine", "(access_projection_state_name = 'Quarantined') = (quarantine_reason_code IS NOT NULL)");
                    table.CheckConstraint("ck_search_access_checkpoint_revision", "consumed_authorization_revision >= 0 AND checkpoint_revision >= 1 AND access_projection_protocol_version >= 1");
                    table.CheckConstraint("ck_search_access_checkpoint_state", "access_projection_state_name IN ('Maintaining','Quarantined')");
                    table.ForeignKey(
                        name: "fk_search_access_checkpoint_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_access_edges",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_edge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    resource_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_relation_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    mirrored_authorization_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_edges", x => x.search_access_edge_id);
                    table.CheckConstraint("ck_search_access_edge_kind", "(resource_type_name, relationship_name) IN (('form_record','owner'),('form_record','parent'),('form_record','shared_via'),('record_share','reader'),('record_share','contributor'),('record_share','manager'),('field_item','shared_via'),('team','member')) AND mirrored_authorization_revision >= 0");
                    table.ForeignKey(
                        name: "fk_search_access_edge_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_access_field_item_grants",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_field_item_grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_principal_type_name = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    access_principal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_role_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    derived_authorization_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_field_item_grants", x => x.search_access_field_item_grant_id);
                    table.CheckConstraint("ck_search_access_item_grant_principal", "access_principal_type_name IN ('user','team')");
                    table.CheckConstraint("ck_search_access_item_grant_role", "share_role_name IN ('reader','contributor','manager')");
                    table.ForeignKey(
                        name: "fk_search_access_item_grant_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_access_record_grants",
                schema: "authority_search_access",
                columns: table => new
                {
                    search_access_record_grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_principal_type_name = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    access_principal_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_kind_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    record_share_id = table.Column<Guid>(type: "uuid", nullable: true),
                    share_role_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    derived_authorization_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_access_record_grants", x => x.search_access_record_grant_id);
                    table.CheckConstraint("ck_search_access_record_grant_kind", "(access_kind_name = 'EffectiveOwner' AND record_share_id IS NULL AND share_role_name IS NULL) OR (access_kind_name = 'ShareRecipient' AND record_share_id IS NOT NULL AND share_role_name IN ('reader','contributor','manager'))");
                    table.CheckConstraint("ck_search_access_record_grant_principal", "access_principal_type_name IN ('user','team')");
                    table.ForeignKey(
                        name: "fk_search_access_record_grant_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_search_access_checkpoint_org",
                schema: "authority_search_access",
                table: "search_access_checkpoints",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_access_edges_subject",
                schema: "authority_search_access",
                table: "search_access_edges",
                columns: new[] { "organization_id", "subject_type_name", "subject_entity_id", "resource_type_name", "relationship_name" });

            migrationBuilder.CreateIndex(
                name: "uq_search_access_edge_tuple",
                schema: "authority_search_access",
                table: "search_access_edges",
                columns: new[] { "organization_id", "resource_type_name", "resource_entity_id", "relationship_name", "subject_type_name", "subject_entity_id", "subject_relation_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_access_item_grants_principal",
                schema: "authority_search_access",
                table: "search_access_field_item_grants",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "form_record_id", "manifest_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_access_item_grants_record",
                schema: "authority_search_access",
                table: "search_access_field_item_grants",
                columns: new[] { "organization_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_access_record_grants_principal",
                schema: "authority_search_access",
                table: "search_access_record_grants",
                columns: new[] { "organization_id", "access_principal_type_name", "access_principal_entity_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_access_record_grants_record",
                schema: "authority_search_access",
                table: "search_access_record_grants",
                columns: new[] { "organization_id", "form_record_id" });

            // IX-5a hand-written DDL (C-147). Forced RLS on the transaction-local organization context (defence in depth, never
            // authorization; CLAUDE.md 4Q.3). Separate function and setting from authority_search: the search reader has no grant here.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority_search_access.current_organization_id() RETURNS uuid
                LANGUAGE sql STABLE PARALLEL SAFE SET search_path = pg_catalog, pg_temp
                AS $$ SELECT nullif(current_setting('authority_search_access.organization_id', true), '')::uuid $$;
                """);
            foreach (var table in new[] { "search_access_checkpoints", "search_access_edges", "search_access_record_grants", "search_access_field_item_grants" })
            {
                migrationBuilder.Sql($"ALTER TABLE authority_search_access.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE authority_search_access.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY rls_{table}_organization ON authority_search_access.{table} USING (organization_id = authority_search_access.current_organization_id()) WITH CHECK (organization_id = authority_search_access.current_organization_id());");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "search_access_checkpoints", "search_access_edges", "search_access_record_grants", "search_access_field_item_grants" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS rls_{table}_organization ON authority_search_access.{table};");
            }
            migrationBuilder.DropTable(
                name: "search_access_checkpoints",
                schema: "authority_search_access");

            migrationBuilder.DropTable(
                name: "search_access_edges",
                schema: "authority_search_access");

            migrationBuilder.DropTable(
                name: "search_access_field_item_grants",
                schema: "authority_search_access");

            migrationBuilder.DropTable(
                name: "search_access_record_grants",
                schema: "authority_search_access");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS authority_search_access.current_organization_id();");
        }
    }
}
