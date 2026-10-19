using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAuthorityStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "authority");

            migrationBuilder.CreateTable(
                name: "organization_authorization_states",
                schema: "authority",
                columns: table => new
                {
                    organization_authorization_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    projection_applied_revision = table.Column<long>(type: "bigint", nullable: false),
                    projection_zed_token = table.Column<string>(type: "text", nullable: true),
                    content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint_enforcement_mode_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    projection_worker_fencing_epoch = table.Column<long>(type: "bigint", nullable: false),
                    integrity_quarantine_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    integrity_quarantine_reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    latest_security_journal_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    latest_content_journal_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    state_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_authorization_states", x => x.organization_authorization_state_id);
                    table.UniqueConstraint("AK_organization_authorization_states_organization_id", x => x.organization_id);
                    table.CheckConstraint("ck_authorization_revision_non_negative", "authorization_revision >= 0 AND projection_applied_revision >= 0 AND content_integrity_revision >= 0");
                    table.CheckConstraint("ck_checkpoint_enforcement_mode", "checkpoint_enforcement_mode_name IN ('Asynchronous','RequiredBeforeEffective')");
                    table.CheckConstraint("ck_projection_never_ahead", "projection_applied_revision <= authorization_revision");
                });

            migrationBuilder.CreateTable(
                name: "authority_resources",
                schema: "authority",
                columns: table => new
                {
                    authority_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    resource_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_lifecycle_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parent_resource_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    registered_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_registered_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_resources", x => x.authority_resource_id);
                    table.CheckConstraint("ck_resource_lifecycle_state", "resource_lifecycle_state IN ('Registered','Active','Revoked','Quarantined')");
                    table.ForeignKey(
                        name: "fk_resource_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_integrity_checkpoints",
                schema: "authority",
                columns: table => new
                {
                    content_integrity_checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_integrity_revision = table.Column<long>(type: "bigint", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    committed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_root_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    previous_checkpoint_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    checkpoint_event_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    checkpoint_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_integrity_checkpoints", x => x.content_integrity_checkpoint_id);
                    table.CheckConstraint("ck_checkpoint_digest_lengths", "octet_length(manifest_root_digest) = 32 AND octet_length(checkpoint_event_digest) = 32");
                    table.ForeignKey(
                        name: "fk_content_integrity_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_bindings",
                schema: "authority",
                columns: table => new
                {
                    identity_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_issuer_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    identity_subject_identifier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    identity_binding_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    identity_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_bindings", x => x.identity_binding_id);
                    table.ForeignKey(
                        name: "fk_identity_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "integrity_key_registrations",
                schema: "authority",
                columns: table => new
                {
                    integrity_key_registration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_purpose_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    provider_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider_key_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    current_signing_key_version = table.Column<int>(type: "integer", nullable: false),
                    minimum_verification_key_version = table.Column<int>(type: "integer", nullable: false),
                    key_usage_paused = table.Column<bool>(type: "boolean", nullable: false),
                    key_registered_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    key_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_key_registrations", x => x.integrity_key_registration_id);
                    table.ForeignKey(
                        name: "fk_integrity_key_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_journal_entries",
                schema: "authority",
                columns: table => new
                {
                    security_journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_stream_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    journal_stream_revision = table.Column<long>(type: "bigint", nullable: false),
                    journal_event_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    journal_event_body_json = table.Column<string>(type: "jsonb", nullable: false),
                    journal_event_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    journal_previous_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    archive_delivery_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    archive_receipt_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    archive_receipt_digest = table.Column<byte[]>(type: "bytea", nullable: true),
                    archive_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_archive_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    journal_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    journal_archived_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_journal_entries", x => x.security_journal_entry_id);
                    table.CheckConstraint("ck_journal_archive_state", "archive_delivery_state IN ('Pending','Anchored','VerificationFailed')");
                    table.CheckConstraint("ck_journal_digest_length", "octet_length(journal_event_digest) = 32");
                    table.ForeignKey(
                        name: "fk_journal_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_change_operations",
                schema: "authority",
                columns: table => new
                {
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operation_request_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    expected_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    expected_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    candidate_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    candidate_key_version = table.Column<int>(type: "integer", nullable: false),
                    candidate_manifest_root = table.Column<byte[]>(type: "bytea", nullable: false),
                    candidate_leaf_index_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    candidate_leaf_count = table.Column<long>(type: "bigint", nullable: false),
                    operation_state_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    operation_quarantine_reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    correlation_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_expires_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_committed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_change_operations", x => x.content_change_operation_id);
                    table.CheckConstraint("ck_content_operation_state", "operation_state_name IN ('Prepared','Committed','Aborted','Quarantined')");
                    table.ForeignKey(
                        name: "fk_content_op_actor",
                        column: x => x.actor_identity_binding_id,
                        principalSchema: "authority",
                        principalTable: "identity_bindings",
                        principalColumn: "identity_binding_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_content_op_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_commands",
                schema: "authority",
                columns: table => new
                {
                    security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_identity_binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_kind_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    command_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    command_request_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    expected_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    committed_authorization_revision = table.Column<long>(type: "bigint", nullable: true),
                    command_state_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    command_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    command_reason_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    rejection_reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    correlation_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    command_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    command_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    command_effective_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_commands", x => x.security_command_id);
                    table.CheckConstraint("ck_security_command_state", "command_state_name IN ('Pending','CommittedAwaitingProjection','Effective','Rejected','FailedReconciliation')");
                    table.ForeignKey(
                        name: "fk_security_command_actor",
                        column: x => x.actor_identity_binding_id,
                        principalSchema: "authority",
                        principalTable: "identity_bindings",
                        principalColumn: "identity_binding_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_command_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_candidate_leaves",
                schema: "authority",
                columns: table => new
                {
                    content_candidate_leaf_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_change_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    manifest_item_kind_code = table.Column<byte>(type: "smallint", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mutation_kind_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    canonical_payload_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    leaf_digest = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_candidate_leaves", x => x.content_candidate_leaf_id);
                    table.CheckConstraint("ck_candidate_leaf_digest_lengths", "octet_length(canonical_payload_digest) = 32 AND octet_length(row_signature) = 32 AND octet_length(leaf_digest) = 32");
                    table.CheckConstraint("ck_candidate_leaf_key_length", "octet_length(manifest_item_key) = 17");
                    table.ForeignKey(
                        name: "fk_candidate_leaf_operation",
                        column: x => x.content_change_operation_id,
                        principalSchema: "authority",
                        principalTable: "content_change_operations",
                        principalColumn: "content_change_operation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "record_manifest_commits",
                schema: "authority",
                columns: table => new
                {
                    record_manifest_commit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    manifest_schema_version = table.Column<int>(type: "integer", nullable: false),
                    manifest_algorithm_version = table.Column<int>(type: "integer", nullable: false),
                    row_signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    manifest_leaf_count = table.Column<long>(type: "bigint", nullable: false),
                    manifest_root_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    manifest_leaf_index_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    manifest_committed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_manifest_commits", x => x.record_manifest_commit_id);
                    table.CheckConstraint("ck_manifest_leaf_count", "manifest_leaf_count >= 1");
                    table.CheckConstraint("ck_manifest_root_lengths", "octet_length(manifest_root_digest) = 32 AND octet_length(manifest_leaf_index_digest) = 32");
                    table.ForeignKey(
                        name: "fk_manifest_content_op",
                        column: x => x.committed_operation_id,
                        principalSchema: "authority",
                        principalTable: "content_change_operations",
                        principalColumn: "content_change_operation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "authority_relationship_edges",
                schema: "authority",
                columns: table => new
                {
                    authority_relationship_edge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    resource_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    subject_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_relation_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relationship_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_by_security_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    relationship_removed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_relationship_edges", x => x.authority_relationship_edge_id);
                    table.ForeignKey(
                        name: "fk_authority_edge_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_edge_command",
                        column: x => x.created_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_edge_removing_command",
                        column: x => x.removed_by_security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id");
                });

            migrationBuilder.CreateTable(
                name: "projection_outbox_events",
                schema: "authority",
                columns: table => new
                {
                    projection_outbox_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_authorization_revision = table.Column<long>(type: "bigint", nullable: false),
                    relationship_delta_json = table.Column<string>(type: "jsonb", nullable: false),
                    projection_delivery_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    delivery_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_delivery_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    projection_confirmed_zed_token = table.Column<string>(type: "text", nullable: true),
                    projection_applied_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_worker_fencing_epoch = table.Column<long>(type: "bigint", nullable: true),
                    last_projection_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    projection_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projection_outbox_events", x => x.projection_outbox_event_id);
                    table.CheckConstraint("ck_projection_delivery_state", "projection_delivery_state IN ('Pending','Delivered','Failed')");
                    table.ForeignKey(
                        name: "fk_projection_command",
                        column: x => x.security_command_id,
                        principalSchema: "authority",
                        principalTable: "security_commands",
                        principalColumn: "security_command_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_projection_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "record_content_heads",
                schema: "authority",
                columns: table => new
                {
                    record_content_head_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_content_revision = table.Column<long>(type: "bigint", nullable: false),
                    committed_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_record_manifest_commit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    committed_manifest_root = table.Column<byte[]>(type: "bytea", nullable: false),
                    signature_key_version = table.Column<int>(type: "integer", nullable: false),
                    manifest_schema_version = table.Column<int>(type: "integer", nullable: false),
                    content_head_quarantined = table.Column<bool>(type: "boolean", nullable: false),
                    quarantine_reason_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    content_head_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_content_heads", x => x.record_content_head_id);
                    table.CheckConstraint("ck_committed_operation_identity", "(committed_content_revision = 0 AND committed_operation_id IS NULL AND current_record_manifest_commit_id IS NULL) OR (committed_content_revision > 0 AND committed_operation_id IS NOT NULL AND current_record_manifest_commit_id IS NOT NULL)");
                    table.CheckConstraint("ck_content_head_root_length", "octet_length(committed_manifest_root) = 32");
                    table.ForeignKey(
                        name: "fk_committed_content_operation",
                        column: x => x.committed_operation_id,
                        principalSchema: "authority",
                        principalTable: "content_change_operations",
                        principalColumn: "content_change_operation_id");
                    table.ForeignKey(
                        name: "fk_content_head_org",
                        column: x => x.organization_id,
                        principalSchema: "authority",
                        principalTable: "organization_authorization_states",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_current_record_manifest_commit",
                        column: x => x.current_record_manifest_commit_id,
                        principalSchema: "authority",
                        principalTable: "record_manifest_commits",
                        principalColumn: "record_manifest_commit_id");
                });

            migrationBuilder.CreateTable(
                name: "record_manifest_leaves",
                schema: "authority",
                columns: table => new
                {
                    record_manifest_leaf_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_manifest_commit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_leaf_position = table.Column<long>(type: "bigint", nullable: false),
                    manifest_item_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    manifest_item_kind_code = table.Column<byte>(type: "smallint", nullable: false),
                    manifest_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_payload_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    row_signature = table.Column<byte[]>(type: "bytea", nullable: false),
                    manifest_leaf_digest = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_manifest_leaves", x => x.record_manifest_leaf_id);
                    table.CheckConstraint("ck_manifest_leaf_digest_lengths", "octet_length(canonical_payload_digest) = 32 AND octet_length(row_signature) = 32 AND octet_length(manifest_leaf_digest) = 32");
                    table.CheckConstraint("ck_manifest_leaf_key_length", "octet_length(manifest_item_key) = 17");
                    table.ForeignKey(
                        name: "fk_manifest_leaf_commit",
                        column: x => x.record_manifest_commit_id,
                        principalSchema: "authority",
                        principalTable: "record_manifest_commits",
                        principalColumn: "record_manifest_commit_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authority_edges_subject",
                schema: "authority",
                table: "authority_relationship_edges",
                columns: new[] { "organization_id", "subject_type_name", "subject_entity_id", "relationship_name" });

            migrationBuilder.CreateIndex(
                name: "IX_authority_relationship_edges_created_by_security_command_id",
                schema: "authority",
                table: "authority_relationship_edges",
                column: "created_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_relationship_edges_removed_by_security_command_id",
                schema: "authority",
                table: "authority_relationship_edges",
                column: "removed_by_security_command_id");

            migrationBuilder.CreateIndex(
                name: "uq_authority_relationship_active",
                schema: "authority",
                table: "authority_relationship_edges",
                columns: new[] { "organization_id", "resource_type_name", "resource_entity_id", "relationship_name", "subject_type_name", "subject_entity_id", "subject_relation_name" },
                unique: true,
                filter: "removed_by_security_command_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_authority_resource",
                schema: "authority",
                table: "authority_resources",
                columns: new[] { "organization_id", "resource_type_name", "resource_entity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_candidate_leaf_key",
                schema: "authority",
                table: "content_candidate_leaves",
                columns: new[] { "content_change_operation_id", "manifest_item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_change_operations_actor_identity_binding_id",
                schema: "authority",
                table: "content_change_operations",
                column: "actor_identity_binding_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_ops_org_record_state",
                schema: "authority",
                table: "content_change_operations",
                columns: new[] { "organization_id", "form_record_id", "operation_state_name", "operation_created_on" });

            migrationBuilder.CreateIndex(
                name: "uq_content_op_idempotency",
                schema: "authority",
                table: "content_change_operations",
                columns: new[] { "organization_id", "operation_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_content_integrity_org_revision",
                schema: "authority",
                table: "content_integrity_checkpoints",
                columns: new[] { "organization_id", "content_integrity_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_authority_user_per_org",
                schema: "authority",
                table: "identity_bindings",
                columns: new[] { "organization_id", "authority_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_identity_subject",
                schema: "authority",
                table: "identity_bindings",
                columns: new[] { "organization_id", "identity_issuer_uri", "identity_subject_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_integrity_key_org_purpose",
                schema: "authority",
                table: "integrity_key_registrations",
                columns: new[] { "organization_id", "key_purpose_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_authorization_states_organization_id",
                schema: "authority",
                table: "organization_authorization_states",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_projection_command",
                schema: "authority",
                table: "projection_outbox_events",
                column: "security_command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_projection_org_revision",
                schema: "authority",
                table: "projection_outbox_events",
                columns: new[] { "organization_id", "target_authorization_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_record_content_heads_committed_operation_id",
                schema: "authority",
                table: "record_content_heads",
                column: "committed_operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_record_content_heads_current_record_manifest_commit_id",
                schema: "authority",
                table: "record_content_heads",
                column: "current_record_manifest_commit_id");

            migrationBuilder.CreateIndex(
                name: "uq_record_content_head",
                schema: "authority",
                table: "record_content_heads",
                columns: new[] { "organization_id", "form_record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_record_manifest_commits_committed_operation_id",
                schema: "authority",
                table: "record_manifest_commits",
                column: "committed_operation_id");

            migrationBuilder.CreateIndex(
                name: "uq_manifest_committed_operation",
                schema: "authority",
                table: "record_manifest_commits",
                columns: new[] { "organization_id", "form_record_id", "committed_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_manifest_record_revision",
                schema: "authority",
                table: "record_manifest_commits",
                columns: new[] { "organization_id", "form_record_id", "committed_content_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_manifest_leaf_key",
                schema: "authority",
                table: "record_manifest_leaves",
                columns: new[] { "record_manifest_commit_id", "manifest_item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_manifest_leaf_position",
                schema: "authority",
                table: "record_manifest_leaves",
                columns: new[] { "record_manifest_commit_id", "manifest_leaf_position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_commands_actor_identity_binding_id",
                schema: "authority",
                table: "security_commands",
                column: "actor_identity_binding_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_commands_org_revision",
                schema: "authority",
                table: "security_commands",
                columns: new[] { "organization_id", "committed_authorization_revision" });

            migrationBuilder.CreateIndex(
                name: "uq_security_command_idempotency",
                schema: "authority",
                table: "security_commands",
                columns: new[] { "organization_id", "command_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_pending_archive",
                schema: "authority",
                table: "security_journal_entries",
                columns: new[] { "organization_id", "archive_delivery_state" });

            migrationBuilder.CreateIndex(
                name: "uq_journal_stream_revision",
                schema: "authority",
                table: "security_journal_entries",
                columns: new[] { "organization_id", "journal_stream_name", "journal_stream_revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authority_relationship_edges",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "authority_resources",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "content_candidate_leaves",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "content_integrity_checkpoints",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "integrity_key_registrations",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "projection_outbox_events",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "record_content_heads",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "record_manifest_leaves",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "security_journal_entries",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "security_commands",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "record_manifest_commits",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "content_change_operations",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "identity_bindings",
                schema: "authority");

            migrationBuilder.DropTable(
                name: "organization_authorization_states",
                schema: "authority");
        }
    }
}
