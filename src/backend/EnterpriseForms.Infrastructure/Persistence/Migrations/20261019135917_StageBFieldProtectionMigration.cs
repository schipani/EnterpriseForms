using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBFieldProtectionMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "protection_authority_command_id",
                table: "field_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "field_protection_migrations",
                columns: table => new
                {
                    field_protection_migration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protection_policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    migration_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_record_count = table.Column<int>(type: "integer", nullable: false),
                    total_value_count = table.Column<int>(type: "integer", nullable: false),
                    completed_record_count = table.Column<int>(type: "integer", nullable: false),
                    failed_record_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_owner_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    migration_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    migration_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    migration_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    migration_verified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_protection_migrations", x => x.field_protection_migration_id);
                    table.CheckConstraint("ck_field_protection_migrations_counts", "total_record_count >= 0 AND total_value_count >= 0 AND completed_record_count >= 0 AND failed_record_count >= 0 AND completed_record_count + failed_record_count <= total_record_count");
                    table.CheckConstraint("ck_field_protection_migrations_state", "migration_state IN ('Queued','Running','Completed','Failed','Blocked')");
                    table.ForeignKey(
                        name: "fk_field_protection_migrations_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_protection_migrations_form",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_protection_migrations_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_protection_migration_items",
                columns: table => new
                {
                    field_protection_migration_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_protection_migration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_content_revision = table.Column<long>(type: "bigint", nullable: true),
                    completed_by_user_commit = table.Column<bool>(type: "boolean", nullable: false),
                    item_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_protection_migration_items", x => x.field_protection_migration_item_id);
                    table.CheckConstraint("ck_field_protection_migration_items_state", "item_state IN ('Pending','Completed','Failed')");
                    table.ForeignKey(
                        name: "fk_field_protection_migration_items_migration",
                        column: x => x.field_protection_migration_id,
                        principalTable: "field_protection_migrations",
                        principalColumn: "field_protection_migration_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_protection_migration_items_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_protection_migration_items_form_record_id",
                table: "field_protection_migration_items",
                column: "form_record_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_protection_migration_items_state",
                table: "field_protection_migration_items",
                columns: new[] { "field_protection_migration_id", "item_state" });

            migrationBuilder.CreateIndex(
                name: "uq_field_protection_migration_items_record",
                table: "field_protection_migration_items",
                columns: new[] { "field_protection_migration_id", "form_record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_protection_migrations_due",
                table: "field_protection_migrations",
                columns: new[] { "migration_state", "next_attempt_on" });

            migrationBuilder.CreateIndex(
                name: "IX_field_protection_migrations_form_definition_id",
                table: "field_protection_migrations",
                column: "form_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_protection_migrations_tenant_id",
                table: "field_protection_migrations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_protection_migrations_field_revision",
                table: "field_protection_migrations",
                columns: new[] { "field_definition_id", "protection_policy_revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_protection_migration_items");

            migrationBuilder.DropTable(
                name: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "protection_authority_command_id",
                table: "field_definitions");
        }
    }
}
