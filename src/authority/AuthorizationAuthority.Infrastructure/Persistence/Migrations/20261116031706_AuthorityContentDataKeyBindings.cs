using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// E3-M2e-1 (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3; OD-M2e-01..04, AM-1): the Authority-owned data-key ledger
    /// <c>content_data_key_bindings</c>. Expand-only: a new table with its CHECKs, global unique identifier index, lookup indexes and
    /// RESTRICT foreign keys, then (SQL) the P-1 binding-guard and P-2 immutability triggers. <b>It installs no content acceptance floor</b>
    /// (AM-1: floor 3 is the separate floor-context migration). The runtime role gets SELECT and INSERT only (AuthorityRuntimeGrants).
    /// <para>The whole Up runs in the migration's own transaction with <c>lock_timeout = 5s</c>: a timeout applies nothing.</para>
    /// <para>Down is Development/Testing only and guarded: it refuses while any binding exists.</para>
    /// </summary>
    public partial class AuthorityContentDataKeyBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentDataKeyLedgerSql.LockTimeoutSql);
            migrationBuilder.CreateTable(
                name: "content_data_key_bindings",
                schema: "authority",
                columns: table => new
                {
                    content_data_key_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_key_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_payload_kind_code = table.Column<short>(type: "smallint", nullable: false),
                    data_key_commitment_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    binding_item_count = table.Column<int>(type: "integer", nullable: false),
                    binding_content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_key_bound_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_data_key_bindings", x => x.content_data_key_binding_id);
                    table.CheckConstraint("ck_content_data_key_binding_commitment", "octet_length(data_key_commitment_bytes) = 32");
                    table.CheckConstraint("ck_content_data_key_binding_count", "binding_item_count >= 1 AND (envelope_payload_kind_code = 1 OR binding_item_count = 1)");
                    table.CheckConstraint("ck_content_data_key_binding_identifier_v7", "substring(data_key_identifier::text FROM 15 FOR 1) = '7' AND substring(data_key_identifier::text FROM 20 FOR 1) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_content_data_key_binding_kind", "envelope_payload_kind_code IN (1, 2)");
                    table.ForeignKey(
                        name: "fk_content_data_key_binding_operation",
                        column: x => x.binding_content_change_operation_id,
                        principalSchema: "authority",
                        principalTable: "content_change_operations",
                        principalColumn: "content_change_operation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_content_data_key_binding_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_content_data_key_bindings_operation",
                schema: "authority",
                table: "content_data_key_bindings",
                column: "binding_content_change_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_data_key_bindings_record",
                schema: "authority",
                table: "content_data_key_bindings",
                columns: new[] { "organization_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "uq_content_data_key_binding_identifier",
                schema: "authority",
                table: "content_data_key_bindings",
                column: "data_key_identifier",
                unique: true);

            migrationBuilder.Sql(ContentDataKeyLedgerSql.TriggersSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ContentDataKeyLedgerSql.DownGuardSql);
            migrationBuilder.DropTable(
                name: "content_data_key_bindings",
                schema: "authority");
        }
    }
}
