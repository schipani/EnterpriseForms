using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// E3-M2b-2 (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §9.3 as corrected by owner decision D-1; §23): signed
    /// schema 2 groundwork. Expand-only: three nullable Envelope v2 metadata columns on the two envelope tables, no defaults,
    /// no backfill, no index; the closed envelope-version CHECK replaces the open <c>envelope_positive</c> CHECK, plus the
    /// schema/envelope tie (S-11) and the v2 metadata CHECK. Existing envelope-1 rows (NULL metadata, schema 1) satisfy every
    /// new CHECK unchanged, and so do inserts by a pre-E3-M2b-2 business build (its EF model does not map the columns).
    /// Down is guarded: it refuses while any envelope-2 row exists, so it can never discard v2 metadata (Development/Testing only).
    /// </summary>
    public partial class EnvelopeV2SignedColumns : Migration
    {
        /// <summary>The fixed, identifier-free message of the guarded Down.</summary>
        public const string DownRefusedMessage = "E3-M2b-2 Down refused: envelope version 2 rows exist";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A bounded wait for the table locks taken by ALTER TABLE: an open transaction on either table makes the whole
            // migration fail with 55P03 and apply nothing (the migration runs in one transaction).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_encrypted_values_envelope_positive",
                table: "field_encrypted_values");

            migrationBuilder.AddColumn<byte[]>(
                name: "data_key_commitment_bytes",
                table: "file_attachment_versions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "data_key_identifier",
                table: "file_attachment_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "key_wrap_algorithm_code",
                table: "file_attachment_versions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "data_key_commitment_bytes",
                table: "field_encrypted_values",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "data_key_identifier",
                table: "field_encrypted_values",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "key_wrap_algorithm_code",
                table: "field_encrypted_values",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_attachment_versions_envelope_metadata",
                table: "file_attachment_versions",
                sql: "(encryption_envelope_version = 1 AND data_key_identifier IS NULL AND data_key_commitment_bytes IS NULL AND key_wrap_algorithm_code IS NULL) OR (encryption_envelope_version = 2 AND data_key_identifier IS NOT NULL AND data_key_commitment_bytes IS NOT NULL AND octet_length(data_key_commitment_bytes) = 32 AND key_wrap_algorithm_code IS NOT NULL AND key_wrap_algorithm_code IN (1, 2))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_attachment_versions_envelope_version",
                table: "file_attachment_versions",
                sql: "encryption_envelope_version IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_attachment_versions_schema_envelope_tie",
                table: "file_attachment_versions",
                sql: "row_signature_schema_version = encryption_envelope_version");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_encrypted_values_envelope_metadata",
                table: "field_encrypted_values",
                sql: "(encryption_envelope_version = 1 AND data_key_identifier IS NULL AND data_key_commitment_bytes IS NULL AND key_wrap_algorithm_code IS NULL) OR (encryption_envelope_version = 2 AND data_key_identifier IS NOT NULL AND data_key_commitment_bytes IS NOT NULL AND octet_length(data_key_commitment_bytes) = 32 AND key_wrap_algorithm_code IS NOT NULL AND key_wrap_algorithm_code IN (1, 2))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_encrypted_values_envelope_version",
                table: "field_encrypted_values",
                sql: "encryption_envelope_version IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_encrypted_values_schema_envelope_tie",
                table: "field_encrypted_values",
                sql: "row_signature_schema_version = encryption_envelope_version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DO $$ BEGIN "
                + "IF EXISTS (SELECT 1 FROM field_encrypted_values WHERE encryption_envelope_version = 2) "
                + "OR EXISTS (SELECT 1 FROM file_attachment_versions WHERE encryption_envelope_version = 2) THEN "
                + "RAISE EXCEPTION '" + DownRefusedMessage + "'; "
                + "END IF; END $$;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_attachment_versions_envelope_metadata",
                table: "file_attachment_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_attachment_versions_envelope_version",
                table: "file_attachment_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_attachment_versions_schema_envelope_tie",
                table: "file_attachment_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_encrypted_values_envelope_metadata",
                table: "field_encrypted_values");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_encrypted_values_envelope_version",
                table: "field_encrypted_values");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_encrypted_values_schema_envelope_tie",
                table: "field_encrypted_values");

            migrationBuilder.DropColumn(
                name: "data_key_commitment_bytes",
                table: "file_attachment_versions");

            migrationBuilder.DropColumn(
                name: "data_key_identifier",
                table: "file_attachment_versions");

            migrationBuilder.DropColumn(
                name: "key_wrap_algorithm_code",
                table: "file_attachment_versions");

            migrationBuilder.DropColumn(
                name: "data_key_commitment_bytes",
                table: "field_encrypted_values");

            migrationBuilder.DropColumn(
                name: "data_key_identifier",
                table: "field_encrypted_values");

            migrationBuilder.DropColumn(
                name: "key_wrap_algorithm_code",
                table: "field_encrypted_values");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_encrypted_values_envelope_positive",
                table: "field_encrypted_values",
                sql: "encryption_envelope_version >= 1");
        }
    }
}
