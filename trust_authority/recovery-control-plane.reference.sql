-- G14 coordinated disaster recovery — schema REFERENCE ONLY, PostgreSQL 18.
-- Deploy ONLY in independently administered Authorization PostgreSQL, NOT Business PostgreSQL.
-- Requires authority command, journal, HSM and recovery epoch services: SQL constraints are NOT the trust boundary.
-- Never enable public application access to this schema. All non-key columns use descriptive multiword names.
-- Review privileges, tenant-bound FKs, hash format, and legal retention before production migration.

CREATE SCHEMA IF NOT EXISTS recovery;

CREATE TABLE recovery.recovery_epochs
(
    recovery_epoch_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_epoch_number BIGINT NOT NULL UNIQUE CHECK (recovery_epoch_number > 0),
    recovery_epoch_status VARCHAR(32) NOT NULL
        CHECK (recovery_epoch_status IN ('Prepared','Fenced','Active','Retired','Quarantined')),
    independent_fence_reference TEXT NOT NULL,
    epoch_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    epoch_activated_on TIMESTAMPTZ NULL,
    epoch_retired_on TIMESTAMPTZ NULL
);

CREATE TABLE recovery.recovery_operations
(
    recovery_operation_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_epoch_id UUID NOT NULL
        REFERENCES recovery.recovery_epochs(recovery_epoch_id),
    organization_id UUID NULL, -- null = global recovery; apply organization binding through trusted authority
    incident_reference VARCHAR(200) NOT NULL,
    recovery_operation_state VARCHAR(40) NOT NULL
        CHECK (recovery_operation_state IN
        ('Isolated','RestoringCandidate','VerifyingSecurity','VerifyingKeys',
         'VerifyingContent','RebuildingProjection','ReadyForApproval',
         'Active','Quarantined','Cancelled')),
    operation_reason_text TEXT NOT NULL,
    operation_idempotency_key VARCHAR(200) NOT NULL,
    expected_security_revision BIGINT NULL,
    expected_content_revision BIGINT NULL,
    initiated_by_user_id UUID NOT NULL,
    initiated_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    completed_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_recovery_idempotency UNIQUE (operation_idempotency_key)
);

CREATE TABLE recovery.recovery_checkpoints
(
    recovery_checkpoint_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_epoch_id UUID NOT NULL REFERENCES recovery.recovery_epochs(recovery_epoch_id),
    organization_id UUID NOT NULL,
    accepted_authorization_revision BIGINT NOT NULL CHECK (accepted_authorization_revision >= 0),
    accepted_content_revision BIGINT NOT NULL CHECK (accepted_content_revision >= 0),
    accepted_authorization_digest BYTEA NOT NULL CHECK (octet_length(accepted_authorization_digest) = 32),
    accepted_manifest_index_digest BYTEA NOT NULL CHECK (octet_length(accepted_manifest_index_digest) = 32),
    archived_security_event_digest BYTEA NOT NULL CHECK (octet_length(archived_security_event_digest) = 32),
    archived_content_commit_digest BYTEA NOT NULL CHECK (octet_length(archived_content_commit_digest) = 32),
    object_catalogue_digest BYTEA NOT NULL CHECK (octet_length(object_catalogue_digest) = 32),
    required_key_inventory_digest BYTEA NOT NULL CHECK (octet_length(required_key_inventory_digest) = 32),
    mandatory_policy_floor_digest BYTEA NOT NULL CHECK (octet_length(mandatory_policy_floor_digest) = 32),
    checkpoint_schema_version INTEGER NOT NULL CHECK (checkpoint_schema_version > 0),
    ledger_transaction_reference TEXT NOT NULL,
    ledger_receipt_digest BYTEA NOT NULL CHECK (octet_length(ledger_receipt_digest) = 32),
    independent_archive_reference TEXT NOT NULL,
    checkpoint_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_recovery_checkpoint_revision UNIQUE
        (organization_id, recovery_epoch_id, accepted_authorization_revision, accepted_content_revision)
);

CREATE TABLE recovery.recovery_component_artifacts
(
    recovery_component_artifact_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_operation_id UUID NOT NULL REFERENCES recovery.recovery_operations(recovery_operation_id),
    component_name VARCHAR(100) NOT NULL,
    artifact_kind VARCHAR(100) NOT NULL,
    artifact_immutable_reference TEXT NOT NULL,
    artifact_digest BYTEA NOT NULL CHECK (octet_length(artifact_digest) = 32),
    artifact_verification_state VARCHAR(30) NOT NULL
        CHECK (artifact_verification_state IN ('Pending','Verified','Rejected','Unavailable')),
    component_verified_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_recovery_artifact UNIQUE
        (recovery_operation_id, component_name, artifact_kind, artifact_immutable_reference)
);

CREATE TABLE recovery.recovery_operation_steps
(
    recovery_operation_step_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_operation_id UUID NOT NULL REFERENCES recovery.recovery_operations(recovery_operation_id),
    step_code VARCHAR(100) NOT NULL,
    step_state VARCHAR(30) NOT NULL
        CHECK (step_state IN ('Pending','Running','Passed','Failed','Blocked')),
    step_attempt_number INTEGER NOT NULL DEFAULT 0 CHECK (step_attempt_number >= 0),
    step_evidence_digest BYTEA NULL CHECK
        (step_evidence_digest IS NULL OR octet_length(step_evidence_digest) = 32),
    step_completed_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_recovery_operation_step UNIQUE (recovery_operation_id, step_code)
);

CREATE TABLE recovery.recovery_release_approvals
(
    recovery_release_approval_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_operation_id UUID NOT NULL REFERENCES recovery.recovery_operations(recovery_operation_id),
    approver_authority_user_id UUID NOT NULL,
    approval_decision VARCHAR(20) NOT NULL
        CHECK (approval_decision IN ('Approved','Rejected')),
    approval_evidence_digest BYTEA NOT NULL CHECK (octet_length(approval_evidence_digest) = 32),
    approval_recorded_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT uq_recovery_approval_person UNIQUE
        (recovery_operation_id, approver_authority_user_id)
);

CREATE TABLE recovery.recovery_release_attestations
(
    recovery_release_attestation_id UUID PRIMARY KEY DEFAULT uuidv7(),
    recovery_operation_id UUID NOT NULL UNIQUE
        REFERENCES recovery.recovery_operations(recovery_operation_id),
    selected_recovery_checkpoint_id UUID NOT NULL
        REFERENCES recovery.recovery_checkpoints(recovery_checkpoint_id),
    attested_authorization_revision BIGINT NOT NULL,
    attested_content_revision BIGINT NOT NULL,
    attested_release_digest BYTEA NOT NULL CHECK (octet_length(attested_release_digest) = 32),
    external_receipt_reference TEXT NOT NULL,
    release_activated_on TIMESTAMPTZ NULL,
    attestation_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX ix_recovery_checkpoints_organization_revision
    ON recovery.recovery_checkpoints
        (organization_id, accepted_authorization_revision DESC, accepted_content_revision DESC);
CREATE INDEX ix_recovery_operations_state
    ON recovery.recovery_operations (recovery_operation_state, initiated_on);
CREATE INDEX ix_recovery_steps_operation_state
    ON recovery.recovery_operation_steps (recovery_operation_id, step_state);

-- CRITICAL NON-SQL invariants (must be verified in independent recovery controller):
-- 1. Only externally fenced recovery leader can advance state or create attestations.
-- 2. NEVER select old accepted security state based on Business PostgreSQL timestamp.
-- 3. A trust checkpoint is accepted ONLY after verifying actual external ledger receipt,
--    archive data payloads, independent key inventory and replayed authority state.
-- 4. Two human approvers MUST be different people AND different from operation initiator
--    when policy requires; DB row presence cannot stand in for authenticated approval.
-- 5. Missing event body, key version, ciphertext, data leaf or privilege evidence -> quarantine.
-- 6. A DB administrator who updates these tables must not affect actual trusted release.
-- 7. Never assume physical backups for separate PostgreSQL clusters are atomic.
-- 8. Audit and externalize release attestation before allowing protected egress.
