using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserProfilesAndPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "identity_display_name",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "is_display_name_overridden",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "job_title",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preferred_name",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "profile_modified_by_user_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "profile_modified_on",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "profile_revision",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            // Existing rows: the shown name so far is the identity provider's name (nothing was editable before this migration).
            migrationBuilder.Sql("UPDATE users SET identity_display_name = display_name WHERE identity_display_name = ''");

            migrationBuilder.CreateTable(
                name: "user_presences",
                columns: table => new
                {
                    user_presence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_heartbeat_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_interaction_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    presence_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_presences", x => x.user_presence_id);
                    table.ForeignKey(
                        name: "fk_user_presences_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_presences_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_presences_tenant_id",
                table: "user_presences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_user_presences_user",
                table: "user_presences",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_presences");

            migrationBuilder.DropColumn(
                name: "identity_display_name",
                table: "users");

            migrationBuilder.DropColumn(
                name: "is_display_name_overridden",
                table: "users");

            migrationBuilder.DropColumn(
                name: "job_title",
                table: "users");

            migrationBuilder.DropColumn(
                name: "preferred_name",
                table: "users");

            migrationBuilder.DropColumn(
                name: "profile_modified_by_user_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "profile_modified_on",
                table: "users");

            migrationBuilder.DropColumn(
                name: "profile_revision",
                table: "users");
        }
    }
}
