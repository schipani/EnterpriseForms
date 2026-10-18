using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBFieldDefinitionEdit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "field_definition_revision",
                table: "field_definitions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "modified_by_user_id",
                table: "field_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "modified_on",
                table: "field_definitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_definitions_revision_positive",
                table: "field_definitions",
                sql: "field_definition_revision >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_definitions_revision_positive",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "field_definition_revision",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "modified_by_user_id",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "modified_on",
                table: "field_definitions");
        }
    }
}
