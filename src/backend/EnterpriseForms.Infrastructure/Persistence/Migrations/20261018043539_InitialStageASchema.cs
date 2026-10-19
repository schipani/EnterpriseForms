using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialStageASchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tenant_lifecycle_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "form_definitions",
                columns: table => new
                {
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    form_definition_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    current_form_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version_behavior = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    lifecycle_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_definitions", x => x.form_definition_id);
                    table.CheckConstraint("ck_form_definitions_lifecycle", "lifecycle_status IN ('Active','Archived','Deleted')");
                    table.CheckConstraint("ck_form_definitions_version_behavior", "version_behavior IN ('Historical','Latest','Hybrid')");
                    table.ForeignKey(
                        name: "fk_form_definitions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.team_id);
                    table.ForeignKey(
                        name: "fk_teams_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    external_subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_users_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_definitions",
                columns: table => new
                {
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    field_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    field_description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    field_data_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    field_classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_searchable = table.Column<bool>(type: "boolean", nullable: false),
                    lifecycle_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_definitions", x => x.field_definition_id);
                    table.CheckConstraint("ck_field_definitions_classification", "field_classification IN ('Public','Internal','Confidential','Restricted')");
                    table.CheckConstraint("ck_field_definitions_data_type", "field_data_type IN ('String','Integer','Decimal','Date','DateTime','Boolean','Guid','Option','File')");
                    table.ForeignKey(
                        name: "fk_field_definitions_definition",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_versions",
                columns: table => new
                {
                    form_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    layout_schema_version = table.Column<int>(type: "integer", nullable: false),
                    layout_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    restored_from_form_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_versions", x => x.form_version_id);
                    table.CheckConstraint("ck_form_versions_number_positive", "version_number > 0 AND layout_schema_version > 0");
                    table.ForeignKey(
                        name: "fk_form_versions_definition",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_versions_restored_from",
                        column: x => x.restored_from_form_version_id,
                        principalTable: "form_versions",
                        principalColumn: "form_version_id");
                });

            migrationBuilder.CreateTable(
                name: "team_memberships",
                columns: table => new
                {
                    team_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_memberships", x => x.team_membership_id);
                    table.ForeignKey(
                        name: "fk_team_memberships_team",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "team_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_memberships_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_memberships_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_records",
                columns: table => new
                {
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_form_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_edited_form_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    record_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    record_revision = table.Column<long>(type: "bigint", nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_records", x => x.form_record_id);
                    table.CheckConstraint("ck_form_records_revision_positive", "record_revision >= 1");
                    table.CheckConstraint("ck_form_records_single_owner", "(owner_user_id IS NOT NULL AND owner_team_id IS NULL) OR (owner_user_id IS NULL AND owner_team_id IS NOT NULL)");
                    table.CheckConstraint("ck_form_records_status", "record_status IN ('Draft','Submitted','Archived','Deleted')");
                    table.ForeignKey(
                        name: "fk_form_records_created_version",
                        column: x => x.created_form_version_id,
                        principalTable: "form_versions",
                        principalColumn: "form_version_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_records_definition",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_records_last_edited_version",
                        column: x => x.last_edited_form_version_id,
                        principalTable: "form_versions",
                        principalColumn: "form_version_id");
                    table.ForeignKey(
                        name: "fk_form_records_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_string_values",
                columns: table => new
                {
                    field_string_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    string_value = table.Column<string>(type: "text", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_string_values", x => x.field_string_value_id);
                    table.CheckConstraint("ck_field_string_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_string_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_string_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_string_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_string_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_record_revisions",
                columns: table => new
                {
                    form_record_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_form_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    record_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    submitted_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    candidate_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    staged_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_record_revisions", x => x.form_record_revision_id);
                    table.CheckConstraint("ck_form_record_revisions_candidate_state", "candidate_state IN ('Staged','Committed','Aborted')");
                    table.CheckConstraint("ck_form_record_revisions_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_form_record_revisions_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_form_record_revisions_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_record_revisions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "record_shares",
                columns: table => new
                {
                    record_share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shared_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    share_scope = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    share_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    share_revision = table.Column<long>(type: "bigint", nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    share_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    share_revoked_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    share_revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_shares", x => x.record_share_id);
                    table.CheckConstraint("ck_record_shares_role", "recipient_role IN ('Reader','Contributor','Manager')");
                    table.CheckConstraint("ck_record_shares_single_recipient", "(recipient_user_id IS NOT NULL AND recipient_team_id IS NULL) OR (recipient_user_id IS NULL AND recipient_team_id IS NOT NULL)");
                    table.CheckConstraint("ck_record_shares_status", "share_status IN ('Active','Revoked','Expired')");
                    table.ForeignKey(
                        name: "fk_record_shares_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_record_shares_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "record_share_fields",
                columns: table => new
                {
                    record_share_field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_share_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    can_view_field = table.Column<bool>(type: "boolean", nullable: false),
                    can_edit_field = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_share_fields", x => x.record_share_field_id);
                    table.CheckConstraint("ck_record_share_fields_edit_implies_view", "NOT can_edit_field OR can_view_field");
                    table.ForeignKey(
                        name: "fk_record_share_fields_share",
                        column: x => x.record_share_id,
                        principalTable: "record_shares",
                        principalColumn: "record_share_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_field_definitions_form_key",
                table: "field_definitions",
                columns: new[] { "form_definition_id", "field_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_string_values_lookup",
                table: "field_string_values",
                columns: new[] { "field_definition_id", "string_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_string_values_record_operation",
                table: "field_string_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_string_values_tenant_id",
                table: "field_string_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_string_values_occurrence",
                table: "field_string_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_form_definitions_tenant_name",
                table: "form_definitions",
                columns: new[] { "tenant_id", "form_definition_name" });

            migrationBuilder.CreateIndex(
                name: "IX_form_record_revisions_tenant_id",
                table: "form_record_revisions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_record_revisions_operation",
                table: "form_record_revisions",
                columns: new[] { "form_record_id", "content_change_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_records_created_form_version_id",
                table: "form_records",
                column: "created_form_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_records_form_definition_id",
                table: "form_records",
                column: "form_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_records_last_edited_form_version_id",
                table: "form_records",
                column: "last_edited_form_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_records_tenant_definition",
                table: "form_records",
                columns: new[] { "tenant_id", "form_definition_id" });

            migrationBuilder.CreateIndex(
                name: "IX_form_versions_restored_from_form_version_id",
                table: "form_versions",
                column: "restored_from_form_version_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_versions_definition_number",
                table: "form_versions",
                columns: new[] { "form_definition_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_record_share_fields_item",
                table: "record_share_fields",
                columns: new[] { "record_share_id", "field_value_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_record_shares_form_record_id",
                table: "record_shares",
                column: "form_record_id");

            migrationBuilder.CreateIndex(
                name: "ix_record_shares_tenant_record_status",
                table: "record_shares",
                columns: new[] { "tenant_id", "form_record_id", "share_status" });

            migrationBuilder.CreateIndex(
                name: "IX_team_memberships_tenant_id",
                table: "team_memberships",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_memberships_user_id",
                table: "team_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "uq_team_memberships_active",
                table: "team_memberships",
                columns: new[] { "team_id", "user_id" },
                unique: true,
                filter: "removed_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_teams_tenant_id",
                table: "teams",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_users_tenant_subject",
                table: "users",
                columns: new[] { "tenant_id", "external_issuer_uri", "external_subject_identifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_string_values");

            migrationBuilder.DropTable(
                name: "form_record_revisions");

            migrationBuilder.DropTable(
                name: "record_share_fields");

            migrationBuilder.DropTable(
                name: "team_memberships");

            migrationBuilder.DropTable(
                name: "field_definitions");

            migrationBuilder.DropTable(
                name: "record_shares");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "form_records");

            migrationBuilder.DropTable(
                name: "form_versions");

            migrationBuilder.DropTable(
                name: "form_definitions");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
