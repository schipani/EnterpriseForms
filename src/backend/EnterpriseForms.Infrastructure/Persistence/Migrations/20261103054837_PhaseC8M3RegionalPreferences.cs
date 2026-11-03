using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC8M3RegionalPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_settings",
                columns: table => new
                {
                    organization_setting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    default_iana_time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    default_language_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    default_date_format_preset_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    default_number_format_preset_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    organization_setting_revision = table.Column<long>(type: "bigint", nullable: false),
                    setting_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    setting_created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    setting_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    setting_modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_settings", x => x.organization_setting_id);
                    table.CheckConstraint("ck_organization_settings_revision", "organization_setting_revision >= 1");
                    table.ForeignKey(
                        name: "fk_organization_settings_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_preferences",
                columns: table => new
                {
                    user_preference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_zone_preference_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    iana_time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    date_format_preference_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    date_format_preset_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    number_format_preference_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number_format_preset_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    language_preference_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    language_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    preference_revision = table.Column<long>(type: "bigint", nullable: false),
                    preference_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    preference_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_preferences", x => x.user_preference_id);
                    table.CheckConstraint("ck_user_preferences_date_format", "date_format_preference_mode IN ('OrganizationDefault','BrowserDefault','Explicit') AND (date_format_preference_mode = 'Explicit') = (date_format_preset_key IS NOT NULL)");
                    table.CheckConstraint("ck_user_preferences_language", "language_preference_mode IN ('OrganizationDefault','BrowserDefault','Explicit') AND (language_preference_mode = 'Explicit') = (language_code IS NOT NULL)");
                    table.CheckConstraint("ck_user_preferences_number_format", "number_format_preference_mode IN ('OrganizationDefault','BrowserDefault','Explicit') AND (number_format_preference_mode = 'Explicit') = (number_format_preset_key IS NOT NULL)");
                    table.CheckConstraint("ck_user_preferences_revision", "preference_revision >= 1");
                    table.CheckConstraint("ck_user_preferences_time_zone", "time_zone_preference_mode IN ('OrganizationDefault','BrowserDefault','Explicit') AND (time_zone_preference_mode = 'Explicit') = (iana_time_zone_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_user_preferences_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_preferences_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_organization_settings_tenant",
                table: "organization_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_preferences_user_id",
                table: "user_preferences",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "uq_user_preferences_tenant_user",
                table: "user_preferences",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_settings");

            migrationBuilder.DropTable(
                name: "user_preferences");
        }
    }
}
