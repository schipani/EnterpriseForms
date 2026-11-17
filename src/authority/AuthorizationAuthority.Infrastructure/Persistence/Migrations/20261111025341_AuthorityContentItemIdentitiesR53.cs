using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityContentItemIdentitiesR53 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_item_identities",
                schema: "authority",
                columns: table => new
                {
                    content_item_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_kind_code = table.Column<byte>(type: "smallint", nullable: false),
                    bound_field_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_group_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bound_form_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_committed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_bound_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_item_identities", x => x.content_item_identity_id);
                    table.CheckConstraint("ck_content_item_identity_anchor", "(manifest_item_kind_code IN (3, 4, 5)) = (bound_field_definition_id IS NOT NULL) AND (manifest_item_kind_code = 2) = (bound_group_definition_id IS NOT NULL) AND (manifest_item_kind_code = 1) = (bound_form_definition_id IS NOT NULL) AND (bound_form_record_group_id IS NULL OR manifest_item_kind_code IN (3, 4))");
                    table.CheckConstraint("ck_content_item_identity_kind", "manifest_item_kind_code BETWEEN 1 AND 6");
                    table.ForeignKey(
                        name: "fk_content_item_identity_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_manifest_leaf_item",
                schema: "authority",
                table: "record_manifest_leaves",
                column: "manifest_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_item_identities_record",
                schema: "authority",
                table: "content_item_identities",
                columns: new[] { "organization_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "uq_content_item_identity_item",
                schema: "authority",
                table: "content_item_identities",
                column: "manifest_item_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_item_identities",
                schema: "authority");

            migrationBuilder.DropIndex(
                name: "ix_manifest_leaf_item",
                schema: "authority",
                table: "record_manifest_leaves");
        }
    }
}
