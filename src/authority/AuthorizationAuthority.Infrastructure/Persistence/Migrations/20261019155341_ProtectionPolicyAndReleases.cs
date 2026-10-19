using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectionPolicyAndReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_protection_policies",
                schema: "authority",
                columns: table => new
                {
                    organization_protection_policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minimum_field_classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    plaintext_internal_fields_permitted = table.Column<bool>(type: "boolean", nullable: false),
                    plaintext_downgrade_permitted = table.Column<bool>(type: "boolean", nullable: false),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_protection_policies", x => x.organization_protection_policy_id);
                    table.CheckConstraint("ck_protection_policy_classification", "minimum_field_classification IN ('Public','Internal','Confidential','Restricted')");
                    table.ForeignKey(
                        name: "fk_protection_policy_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_protection_policy_org",
                schema: "authority",
                table: "organization_protection_policies",
                column: "organization_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_protection_policies",
                schema: "authority");
        }
    }
}
