using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordCreationAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "form_record_creation_attempts",
                columns: table => new
                {
                    form_record_creation_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creation_request_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    fingerprint_key_envelope_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_record_creation_attempts", x => x.form_record_creation_attempt_id);
                    table.CheckConstraint("ck_form_record_creation_attempts_fingerprint", "octet_length(creation_request_fingerprint) = 32");
                    table.ForeignKey(
                        name: "fk_form_record_creation_attempts_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_form_record_creation_attempts_record",
                table: "form_record_creation_attempts",
                columns: new[] { "tenant_id", "form_record_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "form_record_creation_attempts");
        }
    }
}
