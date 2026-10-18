-- LEVEL 2 AUTHORIZATION AUTHORITY — REFERENCE PostgreSQL 18 SCHEMA
-- Separate isolated PostgreSQL cluster administered independently of the business DB.
-- Reference ONLY: finalize constraints/roles, schema migrations and threat tests before use.
-- All columns are descriptive multiword snake_case; primary keys use singular entity + _id.
-- UUIDv7 requires PostgreSQL 18; if on earlier versions, create UUIDv7 in trusted .NET code.
-- Application business DB credentials must have NO access to any table in this schema.

CREATE SCHEMA IF NOT EXISTS authorization;

CREATE TABLE authorization.organization_authorization_states
(
    organization_authorization_state_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL UNIQUE,
    authorization_revision BIGINT NOT NULL DEFAULT 0 CHECK (authorization_revision >= 0),
    projection_applied_revision BIGINT NOT NULL DEFAULT 0 CHECK (projection_applied_revision >= 0),
    projection_zed_token TEXT NULL,
    latest_security_journal_digest BYTEA NULL,
    latest_checkpoint_digest BYTEA NULL,
    state_modified_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT ck_projection_never_ahead CHECK (projection_applied_revision <= authorization_revision)
);

-- This table independently binds the verified OIDC identity to scoped authority principals.
-- Never authorize using a business database users.email_address or issuer/subject copy.
CREATE TABLE authorization.identity_bindings
(
    identity_binding_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    authority_user_id UUID NOT NULL,
    identity_issuer_uri TEXT NOT NULL,
    identity_subject_identifier TEXT NOT NULL,
    identity_binding_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    identity_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_identity_subject UNIQUE
        (organization_id, identity_issuer_uri, identity_subject_identifier),
    CONSTRAINT uq_authority_user_per_org UNIQUE (organization_id, authority_user_id),
    CONSTRAINT fk_identity_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id)
);

-- Independent resource registry stops a forged SQL row from becoming a valid resource.
CREATE TABLE authorization.authority_resources
(
    authority_resource_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    resource_type_name TEXT NOT NULL,
    resource_entity_id UUID NOT NULL,
    resource_lifecycle_state TEXT NOT NULL CHECK (resource_lifecycle_state IN
        ('Registered','Active','Revoked','Quarantined')),
    resource_registered_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_authority_resource UNIQUE
        (organization_id, resource_type_name, resource_entity_id),
    CONSTRAINT fk_resource_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id)
);

CREATE TABLE authorization.security_commands
(
    security_command_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    actor_identity_binding_id UUID NOT NULL,
    command_kind_name TEXT NOT NULL,
    command_idempotency_key TEXT NOT NULL,
    command_request_digest BYTEA NOT NULL,
    expected_authorization_revision BIGINT NOT NULL,
    committed_authorization_revision BIGINT NULL,
    command_state_name TEXT NOT NULL CHECK (command_state_name IN
        ('Pending','CommittedAwaitingProjection','Effective',
         'Rejected','FailedReconciliation')),
    command_payload_json JSONB NOT NULL,
    command_reason_text TEXT NULL,
    command_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    command_modified_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    command_effective_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_security_command_idempotency UNIQUE
        (organization_id, command_idempotency_key),
    CONSTRAINT fk_security_command_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id),
    CONSTRAINT fk_security_command_actor FOREIGN KEY (actor_identity_binding_id)
        REFERENCES authorization.identity_bindings (identity_binding_id)
);

-- Projected edges are derived from these independently held authoritative edges.
-- Only the authority command service may write this table (no generic grant endpoint).
CREATE TABLE authorization.authority_relationship_edges
(
    authority_relationship_edge_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    resource_type_name TEXT NOT NULL,
    resource_entity_id UUID NOT NULL,
    relationship_name TEXT NOT NULL,
    subject_type_name TEXT NOT NULL,
    subject_entity_id UUID NOT NULL,
    subject_relation_name TEXT NOT NULL DEFAULT '',
    created_by_security_command_id UUID NOT NULL,
    relationship_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_authority_relationship UNIQUE
        (organization_id, resource_type_name, resource_entity_id,
         relationship_name, subject_type_name, subject_entity_id,
         subject_relation_name),
    CONSTRAINT fk_authority_edge_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id),
    CONSTRAINT fk_edge_command FOREIGN KEY (created_by_security_command_id)
        REFERENCES authorization.security_commands (security_command_id)
);

-- Parent-share lineage is independently validated by the authority command service.
CREATE TABLE authorization.share_delegation_edges
(
    share_delegation_edge_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    parent_record_share_id UUID NOT NULL,
    child_record_share_id UUID NOT NULL,
    delegating_security_command_id UUID NOT NULL,
    delegation_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_child_share_parent UNIQUE (organization_id, child_record_share_id),
    CONSTRAINT ck_no_self_delegation CHECK
        (parent_record_share_id <> child_record_share_id),
    CONSTRAINT fk_delegation_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id),
    CONSTRAINT fk_delegation_command FOREIGN KEY (delegating_security_command_id)
        REFERENCES authorization.security_commands (security_command_id)
);

CREATE TABLE authorization.projection_outbox_events
(
    projection_outbox_event_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    security_command_id UUID NOT NULL,
    target_authorization_revision BIGINT NOT NULL,
    relationship_delta_json JSONB NOT NULL,
    projection_delivery_state TEXT NOT NULL DEFAULT 'Pending'
        CHECK (projection_delivery_state IN ('Pending','Delivered','Failed')),
    delivery_attempt_count INTEGER NOT NULL DEFAULT 0,
    last_delivery_attempt_on TIMESTAMPTZ NULL,
    projection_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_projection_command UNIQUE (security_command_id),
    CONSTRAINT fk_projection_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id),
    CONSTRAINT fk_projection_command FOREIGN KEY (security_command_id)
        REFERENCES authorization.security_commands (security_command_id)
);

CREATE TABLE authorization.record_content_heads
(
    record_content_head_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    form_record_id UUID NOT NULL,
    committed_content_revision BIGINT NOT NULL DEFAULT 0 CHECK (committed_content_revision >= 0),
    committed_manifest_root BYTEA NOT NULL,
    signature_key_version INTEGER NOT NULL CHECK (signature_key_version > 0),
    manifest_schema_version INTEGER NOT NULL CHECK (manifest_schema_version > 0),
    content_head_modified_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_record_content_head UNIQUE (organization_id, form_record_id),
    CONSTRAINT fk_content_head_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id)
);

CREATE TABLE authorization.content_change_operations
(
    content_change_operation_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    form_record_id UUID NOT NULL,
    actor_identity_binding_id UUID NOT NULL,
    operation_idempotency_key TEXT NOT NULL,
    expected_authorization_revision BIGINT NOT NULL,
    expected_content_revision BIGINT NOT NULL,
    candidate_content_revision BIGINT NOT NULL,
    proposed_item_delta_digest BYTEA NOT NULL,
    candidate_manifest_root BYTEA NULL,
    operation_state_name TEXT NOT NULL CHECK (operation_state_name IN
        ('Prepared','Committed','Aborted','Quarantined')),
    operation_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    operation_committed_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_content_op_idempotency UNIQUE
        (organization_id, operation_idempotency_key),
    CONSTRAINT fk_content_op_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id),
    CONSTRAINT fk_content_op_actor FOREIGN KEY (actor_identity_binding_id)
        REFERENCES authorization.identity_bindings (identity_binding_id)
);

CREATE TABLE authorization.integrity_checkpoint_events
(
    integrity_checkpoint_event_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    authorization_revision BIGINT NOT NULL,
    checkpoint_journal_digest BYTEA NOT NULL,
    checkpoint_previous_digest BYTEA NULL,
    checkpoint_ledger_receipt BYTEA NULL,
    checkpoint_archive_state TEXT NOT NULL CHECK (checkpoint_archive_state IN
        ('Pending','Anchored','VerificationFailed')),
    checkpoint_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    checkpoint_anchored_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_checkpoint_revision UNIQUE (organization_id, authorization_revision),
    CONSTRAINT fk_checkpoint_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id)
);

CREATE INDEX ix_security_commands_org_revision
    ON authorization.security_commands (organization_id, committed_authorization_revision);
CREATE INDEX ix_security_commands_org_created
    ON authorization.security_commands (organization_id, command_created_on);
CREATE INDEX ix_authority_edges_subject
    ON authorization.authority_relationship_edges
    (organization_id, subject_type_name, subject_entity_id, relationship_name);
CREATE INDEX ix_projection_pending
    ON authorization.projection_outbox_events
    (projection_delivery_state, projection_created_on)
    WHERE projection_delivery_state <> 'Delivered';

-- REQUIRED BEFORE DEPLOYMENT:
-- 1. Separate least-privileged SQL roles: read-only decision-service roles,
--    command writer, projection worker, migration role, and independent DBA.
--    Do NOT grant business API managed identities or business DBAs any of these.
-- 2. Finalize composite same-org constraints, identity resource references, legal
--    retention, strong revision CAS, actor reason and approval auditing.
-- 3. Authority DB UPDATE/DELETE permissions only through trusted controlled
--    code paths; no direct SQL by regular applications. Do not rely on RLS alone.
-- 4. Implement append-only protected command/audit journal and ledger checkpoint
--    delivery with external verification; schema alone is not anti-tamper proof.
