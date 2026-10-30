using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormDefinitionRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "form_definition_revision",
                table: "form_definitions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "form_definition_revision",
                table: "form_definitions");
        }
    }
}
