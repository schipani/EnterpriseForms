using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBFieldProtectionDowngrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations");

            migrationBuilder.AddColumn<Guid>(
                name: "authority_command_id",
                table: "organization_security_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "plaintext_downgrade_permitted",
                table: "organization_security_policies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "migration_cancelled_on",
                table: "field_protection_migrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "migration_direction",
                table: "field_protection_migrations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Encrypt");

            migrationBuilder.AddColumn<Guid>(
                name: "rollback_of_migration_id",
                table: "field_protection_migrations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migrations_direction",
                table: "field_protection_migrations",
                sql: "migration_direction IN ('Encrypt','Decrypt')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations",
                sql: "migration_state IN ('Queued','Running','Completed','Failed','Blocked','Cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migrations_direction",
                table: "field_protection_migrations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "authority_command_id",
                table: "organization_security_policies");

            migrationBuilder.DropColumn(
                name: "plaintext_downgrade_permitted",
                table: "organization_security_policies");

            migrationBuilder.DropColumn(
                name: "migration_cancelled_on",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "migration_direction",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "rollback_of_migration_id",
                table: "field_protection_migrations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migrations_state",
                table: "field_protection_migrations",
                sql: "migration_state IN ('Queued','Running','Completed','Failed','Blocked')");
        }
    }
}
