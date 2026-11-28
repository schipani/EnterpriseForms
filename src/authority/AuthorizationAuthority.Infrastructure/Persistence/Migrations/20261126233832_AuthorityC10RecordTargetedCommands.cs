using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityC10RecordTargetedCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "target_form_record_id",
                schema: "authority",
                table: "security_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_security_command",
                schema: "authority",
                table: "security_journal_entries",
                columns: new[] { "organization_id", "security_command_id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_commands_target_record",
                schema: "authority",
                table: "security_commands",
                columns: new[] { "organization_id", "target_form_record_id", "command_created_on" });

            // C10 D6: backfill from each record-scoped command's own committed payload (Development/Test databases only; running this on
            // a persistent environment needs its own authorization). Rejected share commands hold "{}" and stay null.
            migrationBuilder.Sql("""
                UPDATE authority.security_commands
                SET target_form_record_id = (command_payload_json ->> 'form_record_id')::uuid
                WHERE target_form_record_id IS NULL
                  AND command_kind_name IN ('RegisterRecord', 'TransferOwnership', 'CreateShare', 'UpdateShareScope', 'RevokeShare', 'ExpireShare', 'UnlinkChildRecord')
                  AND (command_payload_json ->> 'form_record_id') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
                """);

            // The target is fixed when the command row is created: an update may never set, change or clear it.
            migrationBuilder.Sql("""
                CREATE FUNCTION authority.guard_security_command_target_record() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp
                AS $$
                BEGIN
                    IF NEW.target_form_record_id IS DISTINCT FROM OLD.target_form_record_id THEN
                        RAISE EXCEPTION 'C10 D6: the target record of a security command is immutable' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER trg_security_command_target_record BEFORE UPDATE ON authority.security_commands
                    FOR EACH ROW EXECUTE FUNCTION authority.guard_security_command_target_record();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_security_command_target_record ON authority.security_commands;
                DROP FUNCTION IF EXISTS authority.guard_security_command_target_record();
                """);

            migrationBuilder.DropIndex(
                name: "ix_journal_security_command",
                schema: "authority",
                table: "security_journal_entries");

            migrationBuilder.DropIndex(
                name: "ix_security_commands_target_record",
                schema: "authority",
                table: "security_commands");

            migrationBuilder.DropColumn(
                name: "target_form_record_id",
                schema: "authority",
                table: "security_commands");
        }
    }
}
