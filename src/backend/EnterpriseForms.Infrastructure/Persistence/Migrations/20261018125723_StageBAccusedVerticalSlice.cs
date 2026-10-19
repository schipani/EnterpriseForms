using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageBAccusedVerticalSlice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_form_records_single_owner",
                table: "form_records");

            migrationBuilder.AddColumn<Guid>(
                name: "form_relationship_definition_id",
                table: "form_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_form_record_id",
                table: "form_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "form_relationship_definition_id",
                table: "form_record_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_form_record_id",
                table: "form_record_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "relationship_sort_order",
                table: "form_record_revisions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    audit_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audit_sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_occurred_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    event_action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    event_outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_attachment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    target_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    event_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    event_metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    event_schema_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.audit_event_id);
                    table.UniqueConstraint("uq_audit_events_sequence", x => x.audit_sequence);
                    table.CheckConstraint("ck_audit_events_outcome", "event_outcome IN ('Success','Denied','Failed')");
                    table.ForeignKey(
                        name: "fk_audit_events_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_boolean_values",
                columns: table => new
                {
                    field_boolean_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    boolean_value = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_boolean_values", x => x.field_boolean_value_id);
                    table.CheckConstraint("ck_field_boolean_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_boolean_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_boolean_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_boolean_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_boolean_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_date_values",
                columns: table => new
                {
                    field_date_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_value = table.Column<DateOnly>(type: "date", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_date_values", x => x.field_date_value_id);
                    table.CheckConstraint("ck_field_date_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_date_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_date_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_date_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_date_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_decimal_values",
                columns: table => new
                {
                    field_decimal_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decimal_value = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_decimal_values", x => x.field_decimal_value_id);
                    table.CheckConstraint("ck_field_decimal_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_decimal_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_decimal_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_decimal_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_decimal_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_integer_values",
                columns: table => new
                {
                    field_integer_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    integer_value = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_integer_values", x => x.field_integer_value_id);
                    table.CheckConstraint("ck_field_integer_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_integer_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_integer_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_integer_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_integer_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_options",
                columns: table => new
                {
                    field_option_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    option_display_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    option_sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_options", x => x.field_option_id);
                    table.ForeignKey(
                        name: "fk_field_options_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_attachments",
                columns: table => new
                {
                    file_attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    current_version_number = table.Column<int>(type: "integer", nullable: false),
                    attachment_lifecycle_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attachment_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attachment_created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attachment_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attachment_modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_attachments", x => x.file_attachment_id);
                    table.CheckConstraint("ck_file_attachments_lifecycle", "attachment_lifecycle_state IN ('Ready','Removed')");
                    table.ForeignKey(
                        name: "fk_file_attachments_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_file_attachments_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_file_attachments_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_drafts",
                columns: table => new
                {
                    form_draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    based_on_form_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    layout_schema_version = table.Column<int>(type: "integer", nullable: false),
                    layout_json = table.Column<string>(type: "jsonb", nullable: false),
                    draft_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_drafts", x => x.form_draft_id);
                    table.CheckConstraint("ck_form_drafts_revision_positive", "draft_revision >= 1 AND layout_schema_version > 0");
                    table.ForeignKey(
                        name: "fk_form_drafts_based_on_version",
                        column: x => x.based_on_form_version_id,
                        principalTable: "form_versions",
                        principalColumn: "form_version_id");
                    table.ForeignKey(
                        name: "fk_form_drafts_definition",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_relationship_definitions",
                columns: table => new
                {
                    form_relationship_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_form_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_definition_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relationship_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    minimum_child_count = table.Column<int>(type: "integer", nullable: false),
                    maximum_child_count = table.Column<int>(type: "integer", nullable: false),
                    lifecycle_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    definition_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_relationship_definitions", x => x.form_relationship_definition_id);
                    table.CheckConstraint("ck_form_relationship_definitions_bounds", "minimum_child_count >= 0 AND maximum_child_count >= 1 AND maximum_child_count <= 250 AND minimum_child_count <= maximum_child_count");
                    table.CheckConstraint("ck_form_relationship_definitions_lifecycle", "lifecycle_status IN ('Active','Retired')");
                    table.CheckConstraint("ck_form_relationship_definitions_no_self", "parent_form_definition_id <> child_form_definition_id");
                    table.ForeignKey(
                        name: "fk_form_relationship_definitions_child_form",
                        column: x => x.child_form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_relationship_definitions_parent_form",
                        column: x => x.parent_form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "form_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_relationship_definitions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_brandings",
                columns: table => new
                {
                    organization_branding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    organization_logo_png = table.Column<byte[]>(type: "bytea", nullable: true),
                    logo_alternative_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    primary_button_background_color = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    primary_button_foreground_color = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    secondary_button_background_color = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    secondary_button_foreground_color = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    secondary_button_border_color = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    organization_default_text_scale_percent = table.Column<decimal>(type: "numeric(5,1)", nullable: false),
                    organization_color_mode = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    branding_revision = table.Column<long>(type: "bigint", nullable: false),
                    branding_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    branding_modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_brandings", x => x.organization_branding_id);
                    table.CheckConstraint("ck_organization_brandings_color_mode", "organization_color_mode IN ('Light','Dark')");
                    table.CheckConstraint("ck_organization_brandings_logo_size", "organization_logo_png IS NULL OR octet_length(organization_logo_png) <= 2097152");
                    table.CheckConstraint("ck_organization_brandings_text_scale", "organization_default_text_scale_percent IN (100.0, 112.5, 125.0, 150.0)");
                    table.ForeignKey(
                        name: "fk_organization_brandings_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    upload_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replaces_file_attachment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    declared_content_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    declared_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    chunk_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    expected_chunk_count = table.Column<int>(type: "integer", nullable: false),
                    received_chunk_bitmap = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_bytes = table.Column<long>(type: "bigint", nullable: false),
                    staging_object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    session_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_reason_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    session_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_completed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    session_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    produced_file_attachment_version_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_sessions", x => x.upload_session_id);
                    table.CheckConstraint("ck_upload_sessions_sizes", "declared_size_bytes >= 1 AND chunk_size_bytes >= 1 AND expected_chunk_count >= 1 AND received_bytes >= 0");
                    table.CheckConstraint("ck_upload_sessions_state", "session_state IN ('Open','Completed','Processing','Processed','Committed','Rejected','Failed','Cancelled','Expired')");
                    table.ForeignKey(
                        name: "fk_upload_sessions_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_upload_sessions_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_upload_sessions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_ui_preferences",
                columns: table => new
                {
                    user_ui_preference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text_scale_preference_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    explicit_text_scale_percent = table.Column<decimal>(type: "numeric(5,1)", nullable: true),
                    preference_revision = table.Column<long>(type: "bigint", nullable: false),
                    preference_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_ui_preferences", x => x.user_ui_preference_id);
                    table.CheckConstraint("ck_user_ui_preferences_explicit", "(text_scale_preference_mode = 'Explicit') = (explicit_text_scale_percent IS NOT NULL)");
                    table.CheckConstraint("ck_user_ui_preferences_mode", "text_scale_preference_mode IN ('OrganizationDefault','BrowserDefault','Explicit')");
                    table.CheckConstraint("ck_user_ui_preferences_scale", "explicit_text_scale_percent IS NULL OR explicit_text_scale_percent IN (100.0, 112.5, 125.0, 150.0)");
                    table.ForeignKey(
                        name: "fk_user_ui_preferences_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_ui_preferences_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "field_option_values",
                columns: table => new
                {
                    field_option_value_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_option_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_value_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_option_values", x => x.field_option_value_id);
                    table.CheckConstraint("ck_field_option_values_signature_length", "octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_field_option_values_versions_positive", "row_revision >= 1 AND row_signature_key_version >= 1 AND row_signature_schema_version >= 1 AND content_revision >= 1");
                    table.ForeignKey(
                        name: "fk_field_option_values_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_option_values_option",
                        column: x => x.field_option_id,
                        principalTable: "field_options",
                        principalColumn: "field_option_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_option_values_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_field_option_values_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "file_attachment_versions",
                columns: table => new
                {
                    file_attachment_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    detected_content_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    plaintext_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    plaintext_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    ciphertext_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    ciphertext_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    storage_provider_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    storage_object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    encryption_algorithm_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    encryption_key_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    encryption_envelope_version = table.Column<int>(type: "integer", nullable: false),
                    wrapped_data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    encryption_nonce_prefix = table.Column<byte[]>(type: "bytea", nullable: false),
                    encryption_segment_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    scan_verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scanner_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scan_signature_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    scanned_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_revision = table.Column<long>(type: "bigint", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_schema_version = table.Column<int>(type: "integer", nullable: false),
                    version_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_attachment_versions", x => x.file_attachment_version_id);
                    table.CheckConstraint("ck_file_attachment_versions_digests", "octet_length(plaintext_sha256) = 32 AND octet_length(ciphertext_sha256) = 32");
                    table.CheckConstraint("ck_file_attachment_versions_scan", "scan_verdict IN ('Clean','Infected','ScanError')");
                    table.CheckConstraint("ck_file_attachment_versions_signature_length", "(content_change_operation_id = '00000000-0000-0000-0000-000000000000' AND octet_length(row_signature) = 0) OR octet_length(row_signature) = 32");
                    table.CheckConstraint("ck_file_attachment_versions_state", "version_state IN ('Processed','Committed','Superseded','Removed')");
                    table.ForeignKey(
                        name: "fk_file_attachment_versions_attachment",
                        column: x => x.file_attachment_id,
                        principalTable: "file_attachments",
                        principalColumn: "file_attachment_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_file_attachment_versions_field",
                        column: x => x.field_definition_id,
                        principalTable: "field_definitions",
                        principalColumn: "field_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_file_attachment_versions_record",
                        column: x => x.form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_file_attachment_versions_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_record_relationships",
                columns: table => new
                {
                    form_record_relationship_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_relationship_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_sort_order = table.Column<long>(type: "bigint", nullable: false),
                    link_lifecycle_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    authority_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    link_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    link_created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_removed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    link_removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_record_relationships", x => x.form_record_relationship_id);
                    table.CheckConstraint("ck_form_record_relationships_lifecycle", "link_lifecycle_state IN ('Active','Removed')");
                    table.CheckConstraint("ck_form_record_relationships_no_self", "parent_form_record_id <> child_form_record_id");
                    table.ForeignKey(
                        name: "fk_form_record_relationships_child",
                        column: x => x.child_form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_record_relationships_definition",
                        column: x => x.form_relationship_definition_id,
                        principalTable: "form_relationship_definitions",
                        principalColumn: "form_relationship_definition_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_record_relationships_parent",
                        column: x => x.parent_form_record_id,
                        principalTable: "form_records",
                        principalColumn: "form_record_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_record_relationships_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_processing_jobs",
                columns: table => new
                {
                    document_processing_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_owner_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    job_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    job_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_processing_jobs", x => x.document_processing_job_id);
                    table.CheckConstraint("ck_document_processing_jobs_state", "job_state IN ('Queued','Leased','ScanPending','Processed','Rejected','Failed')");
                    table.ForeignKey(
                        name: "fk_document_processing_jobs_session",
                        column: x => x.upload_session_id,
                        principalTable: "upload_sessions",
                        principalColumn: "upload_session_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_document_processing_jobs_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_form_records_parent_relationship",
                table: "form_records",
                columns: new[] { "parent_form_record_id", "form_relationship_definition_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_records_parent_pair",
                table: "form_records",
                sql: "(parent_form_record_id IS NULL) = (form_relationship_definition_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_records_single_owner",
                table: "form_records",
                sql: "(parent_form_record_id IS NOT NULL) OR (owner_user_id IS NOT NULL AND owner_team_id IS NULL) OR (owner_user_id IS NULL AND owner_team_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_record_revisions_parent_pair",
                table: "form_record_revisions",
                sql: "(parent_form_record_id IS NULL) = (form_relationship_definition_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_record_time",
                table: "audit_events",
                columns: new[] { "form_record_id", "event_occurred_on" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_time",
                table: "audit_events",
                columns: new[] { "tenant_id", "event_occurred_on", "audit_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_document_processing_jobs_due",
                table: "document_processing_jobs",
                columns: new[] { "job_state", "next_attempt_on" });

            migrationBuilder.CreateIndex(
                name: "IX_document_processing_jobs_tenant_id",
                table: "document_processing_jobs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_document_processing_jobs_session",
                table: "document_processing_jobs",
                column: "upload_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_boolean_values_lookup",
                table: "field_boolean_values",
                columns: new[] { "field_definition_id", "boolean_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_boolean_values_record_operation",
                table: "field_boolean_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_boolean_values_tenant_id",
                table: "field_boolean_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_boolean_values_occurrence",
                table: "field_boolean_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_field_date_values_lookup",
                table: "field_date_values",
                columns: new[] { "field_definition_id", "date_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_date_values_record_operation",
                table: "field_date_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_date_values_tenant_id",
                table: "field_date_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_date_values_occurrence",
                table: "field_date_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_field_decimal_values_lookup",
                table: "field_decimal_values",
                columns: new[] { "field_definition_id", "decimal_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_decimal_values_record_operation",
                table: "field_decimal_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_decimal_values_tenant_id",
                table: "field_decimal_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_decimal_values_occurrence",
                table: "field_decimal_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_field_integer_values_lookup",
                table: "field_integer_values",
                columns: new[] { "field_definition_id", "integer_value", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_integer_values_record_operation",
                table: "field_integer_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_integer_values_tenant_id",
                table: "field_integer_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_integer_values_occurrence",
                table: "field_integer_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_field_option_values_field_option_id",
                table: "field_option_values",
                column: "field_option_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_option_values_lookup",
                table: "field_option_values",
                columns: new[] { "field_definition_id", "field_option_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "ix_field_option_values_record_operation",
                table: "field_option_values",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_option_values_tenant_id",
                table: "field_option_values",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_field_option_values_occurrence",
                table: "field_option_values",
                columns: new[] { "content_change_operation_id", "form_record_id", "field_definition_id", "form_record_group_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "uq_field_options_field_key",
                table: "field_options",
                columns: new[] { "field_definition_id", "option_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_attachment_versions_field_definition_id",
                table: "file_attachment_versions",
                column: "field_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_attachment_versions_record_operation",
                table: "file_attachment_versions",
                columns: new[] { "form_record_id", "content_change_operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_attachment_versions_tenant_id",
                table: "file_attachment_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_file_attachment_versions_number_operation",
                table: "file_attachment_versions",
                columns: new[] { "file_attachment_id", "version_number", "content_change_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_attachments_field_definition_id",
                table: "file_attachments",
                column: "field_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_attachments_record_field",
                table: "file_attachments",
                columns: new[] { "form_record_id", "field_definition_id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_attachments_tenant_id",
                table: "file_attachments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_drafts_based_on_form_version_id",
                table: "form_drafts",
                column: "based_on_form_version_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_drafts_definition",
                table: "form_drafts",
                column: "form_definition_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_form_record_relationships_form_relationship_definition_id",
                table: "form_record_relationships",
                column: "form_relationship_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_record_relationships_parent_list",
                table: "form_record_relationships",
                columns: new[] { "parent_form_record_id", "form_relationship_definition_id", "list_sort_order" });

            migrationBuilder.CreateIndex(
                name: "IX_form_record_relationships_tenant_id",
                table: "form_record_relationships",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_record_relationships_active_child",
                table: "form_record_relationships",
                column: "child_form_record_id",
                unique: true,
                filter: "link_lifecycle_state = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_form_relationship_definitions_child_form_definition_id",
                table: "form_relationship_definitions",
                column: "child_form_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_form_relationship_definitions_tenant_id",
                table: "form_relationship_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_form_relationship_definitions_parent_key",
                table: "form_relationship_definitions",
                columns: new[] { "parent_form_definition_id", "relationship_definition_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_organization_brandings_tenant",
                table: "organization_brandings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_field_definition_id",
                table: "upload_sessions",
                column: "field_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_record_state",
                table: "upload_sessions",
                columns: new[] { "form_record_id", "session_state" });

            migrationBuilder.CreateIndex(
                name: "uq_upload_sessions_owner_idempotency",
                table: "upload_sessions",
                columns: new[] { "tenant_id", "owner_user_id", "session_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_ui_preferences_tenant_id",
                table: "user_ui_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_user_ui_preferences_user",
                table: "user_ui_preferences",
                column: "user_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_form_records_parent_record",
                table: "form_records",
                column: "parent_form_record_id",
                principalTable: "form_records",
                principalColumn: "form_record_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_form_records_parent_record",
                table: "form_records");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "document_processing_jobs");

            migrationBuilder.DropTable(
                name: "field_boolean_values");

            migrationBuilder.DropTable(
                name: "field_date_values");

            migrationBuilder.DropTable(
                name: "field_decimal_values");

            migrationBuilder.DropTable(
                name: "field_integer_values");

            migrationBuilder.DropTable(
                name: "field_option_values");

            migrationBuilder.DropTable(
                name: "file_attachment_versions");

            migrationBuilder.DropTable(
                name: "form_drafts");

            migrationBuilder.DropTable(
                name: "form_record_relationships");

            migrationBuilder.DropTable(
                name: "organization_brandings");

            migrationBuilder.DropTable(
                name: "user_ui_preferences");

            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropTable(
                name: "field_options");

            migrationBuilder.DropTable(
                name: "file_attachments");

            migrationBuilder.DropTable(
                name: "form_relationship_definitions");

            migrationBuilder.DropIndex(
                name: "ix_form_records_parent_relationship",
                table: "form_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_records_parent_pair",
                table: "form_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_records_single_owner",
                table: "form_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_form_record_revisions_parent_pair",
                table: "form_record_revisions");

            migrationBuilder.DropColumn(
                name: "form_relationship_definition_id",
                table: "form_records");

            migrationBuilder.DropColumn(
                name: "parent_form_record_id",
                table: "form_records");

            migrationBuilder.DropColumn(
                name: "form_relationship_definition_id",
                table: "form_record_revisions");

            migrationBuilder.DropColumn(
                name: "parent_form_record_id",
                table: "form_record_revisions");

            migrationBuilder.DropColumn(
                name: "relationship_sort_order",
                table: "form_record_revisions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_form_records_single_owner",
                table: "form_records",
                sql: "(owner_user_id IS NOT NULL AND owner_team_id IS NULL) OR (owner_user_id IS NULL AND owner_team_id IS NOT NULL)");
        }
    }
}
