using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessRecordAcceptedOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "accepted_content_change_operation_id",
                table: "form_records",
                type: "uuid",
                nullable: true);

            // Best-effort projection backfill for records written before the column existed: the revision that matches the
            // record's projected revision, otherwise the highest staged one. It is an estimate only — reads and migration
            // post-checks verify against the authority head — and every later accepted commit overwrites it.
            migrationBuilder.Sql(@"UPDATE form_records f SET accepted_content_change_operation_id = (
    SELECT r.content_change_operation_id FROM form_record_revisions r
    WHERE r.form_record_id = f.form_record_id
    ORDER BY (r.content_revision = f.record_revision) DESC, r.content_revision DESC, r.staged_on DESC
    LIMIT 1)
WHERE f.accepted_content_change_operation_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accepted_content_change_operation_id",
                table: "form_records");
        }
    }
}
