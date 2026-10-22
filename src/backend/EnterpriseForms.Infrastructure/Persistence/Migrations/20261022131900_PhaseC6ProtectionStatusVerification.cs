using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC6ProtectionStatusVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_form_records_revision_positive",
                table: "form_records");

            migrationBuilder.RenameIndex(
                name: "IX_field_protection_migration_items_form_record_id",
                table: "field_protection_migration_items",
                newName: "ix_field_protection_migration_items_form_record");

            migrationBuilder.AddColumn<int>(
                name: "unverified_record_count",
                table: "field_protection_migrations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verification_checked_on",
                table: "field_protection_migrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "verified_record_count",
                table: "field_protection_migrations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "verified_remaining_record_count",
                table: "field_protection_migrations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "verified_remaining_value_count",
                table: "field_protection_migrations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "completed_by_operation_kind",
                table: "field_protection_migration_items",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "verified_head_content_revision",
                table: "field_protection_migration_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "verified_head_operation_id",
                table: "field_protection_migration_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "verified_old_form_value_count",
                table: "field_protection_migration_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_on",
                table: "field_protection_migration_items",
                type: "timestamp with time zone",
                nullable: true);

            // Phase C6 M0 data step (status evidence for migrations recorded before this change):
            // (1) A migration that completed through the worker's post-check (it always tracked at least one record) ran the same
            //     Authority-head check as now; completion implied nothing remaining and no unreadable tracked record. Record that
            //     (the number of records checked was not stored before Phase C6 and stays NULL).
            migrationBuilder.Sql(@"UPDATE field_protection_migrations
SET verification_checked_on = migration_verified_on, verified_remaining_record_count = 0, verified_remaining_value_count = 0, unverified_record_count = 0
WHERE migration_state = 'Completed' AND migration_verified_on IS NOT NULL AND total_record_count > 0 AND verification_checked_on IS NULL;");

            // (2) A migration created with no record to convert was marked Completed and verified at creation, from the local
            //     estimate alone, without any Authority post-check. Re-queue the latest such migration of each field so the worker
            //     verifies every listed record against its Authority-accepted head; the claim returns only if that check passes.
            migrationBuilder.Sql(@"UPDATE field_protection_migrations m
SET migration_state = 'Queued', migration_completed_on = NULL, migration_verified_on = NULL, next_attempt_on = now(), lease_owner_name = NULL, lease_expires_on = NULL, migration_modified_on = now()
WHERE m.migration_state = 'Completed' AND m.total_record_count = 0
  AND m.protection_policy_revision = (SELECT max(o.protection_policy_revision) FROM field_protection_migrations o WHERE o.field_definition_id = m.field_definition_id);");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_records_revision_projection",
                table: "form_records",
                sql: "record_revision >= 1 OR (record_revision = 0 AND accepted_content_change_operation_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migrations_verified_counts",
                table: "field_protection_migrations",
                sql: "(verified_record_count IS NULL OR verified_record_count >= 0) AND (verified_remaining_record_count IS NULL OR verified_remaining_record_count >= 0) AND (verified_remaining_value_count IS NULL OR verified_remaining_value_count >= 0) AND (unverified_record_count IS NULL OR unverified_record_count >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migration_items_completion_kind",
                table: "field_protection_migration_items",
                sql: "completed_by_operation_kind IS NULL OR completed_by_operation_kind IN ('MigrationWorker','OtherMigration','UserOperation','NoAcceptedContent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_protection_migration_items_verified_counts",
                table: "field_protection_migration_items",
                sql: "(verified_old_form_value_count IS NULL OR verified_old_form_value_count >= 0) AND (verified_head_content_revision IS NULL OR verified_head_content_revision >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_form_records_revision_projection",
                table: "form_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migrations_verified_counts",
                table: "field_protection_migrations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migration_items_completion_kind",
                table: "field_protection_migration_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_protection_migration_items_verified_counts",
                table: "field_protection_migration_items");

            migrationBuilder.DropColumn(
                name: "unverified_record_count",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "verification_checked_on",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "verified_record_count",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "verified_remaining_record_count",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "verified_remaining_value_count",
                table: "field_protection_migrations");

            migrationBuilder.DropColumn(
                name: "completed_by_operation_kind",
                table: "field_protection_migration_items");

            migrationBuilder.DropColumn(
                name: "verified_head_content_revision",
                table: "field_protection_migration_items");

            migrationBuilder.DropColumn(
                name: "verified_head_operation_id",
                table: "field_protection_migration_items");

            migrationBuilder.DropColumn(
                name: "verified_old_form_value_count",
                table: "field_protection_migration_items");

            migrationBuilder.DropColumn(
                name: "verified_on",
                table: "field_protection_migration_items");

            migrationBuilder.RenameIndex(
                name: "ix_field_protection_migration_items_form_record",
                table: "field_protection_migration_items",
                newName: "IX_field_protection_migration_items_form_record_id");

            // Revision 0 ("no accepted content") is not representable before Phase C6: fall back to the creation revision.
            migrationBuilder.Sql("UPDATE form_records SET record_revision = 1 WHERE record_revision = 0;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_records_revision_positive",
                table: "form_records",
                sql: "record_revision >= 1");
        }
    }
}
