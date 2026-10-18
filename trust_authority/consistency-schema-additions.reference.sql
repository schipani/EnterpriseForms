-- LEVEL 2 CONSISTENCY — additive reference, NOT a production-ready migration.
-- Apply only to isolated authority PostgreSQL after reviewing existing records,
-- backfill requirements, privilege separation and tested SpiceDB protocol.
-- All columns descriptive multiword snake_case. UUIDv7 in PostgreSQL 18.

BEGIN;

-- Desired versus applied revisions. If unequal, all protected access for
-- the organization MUST fail closed (including previously granted users).
ALTER TABLE authorization.organization_authorization_states
    ADD COLUMN checkpoint_anchored_revision BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN checkpoint_enforcement_mode_name VARCHAR(30) NOT NULL DEFAULT 'Asynchronous',
    ADD COLUMN projection_worker_fencing_epoch BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN projection_worker_lease_until TIMESTAMPTZ NULL,
    ADD COLUMN integrity_quarantine_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN projection_verified_state_digest BYTEA NULL;

ALTER TABLE authorization.organization_authorization_states
    ADD CONSTRAINT ck_checkpoint_revision_bound
      CHECK (checkpoint_anchored_revision <= authorization_revision
             AND checkpoint_anchored_revision >= 0),
    ADD CONSTRAINT ck_checkpoint_enforcement_mode
      CHECK (checkpoint_enforcement_mode_name IN ('Asynchronous','RequiredBeforeEffective'));

-- Track exactly which projection was verified and acknowledged. This event
-- has an immutable target revision; the authority checks contiguous revision
-- and an external SpiceDB revision/step-marker CAS before acknowledging it.
ALTER TABLE authorization.projection_outbox_events
    ADD COLUMN projection_confirmed_zed_token TEXT NULL,
    ADD COLUMN projection_verified_state_digest BYTEA NULL,
    ADD COLUMN projection_applied_on TIMESTAMPTZ NULL,
    ADD COLUMN claimed_worker_fencing_epoch BIGINT NULL,
    ADD COLUMN last_projection_error_code VARCHAR(100) NULL;

CREATE UNIQUE INDEX uq_projection_org_revision
ON authorization.projection_outbox_events (organization_id, target_authorization_revision);

CREATE INDEX ix_projection_org_pending_revision
ON authorization.projection_outbox_events
(organization_id, target_authorization_revision)
WHERE projection_delivery_state <> 'Delivered';

-- Independent content head must address a unique immutable staged candidate,
-- not just a revision: concurrent candidates may request the same revision.
ALTER TABLE authorization.record_content_heads
    ADD COLUMN committed_operation_id UUID NULL,
    ADD COLUMN content_head_quarantined BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE authorization.record_content_heads
    ADD CONSTRAINT fk_committed_content_operation
      FOREIGN KEY (committed_operation_id)
      REFERENCES authorization.content_change_operations (content_change_operation_id);

-- Backfill all existing nonzero content heads before VALIDATE CONSTRAINT.
ALTER TABLE authorization.record_content_heads
    ADD CONSTRAINT ck_committed_operation_identity
    CHECK (
      (committed_content_revision = 0 AND committed_operation_id IS NULL)
      OR (committed_content_revision > 0 AND committed_operation_id IS NOT NULL)
    ) NOT VALID;

ALTER TABLE authorization.content_change_operations
    ADD COLUMN candidate_staging_verified_on TIMESTAMPTZ NULL,
    ADD COLUMN prepared_content_digest BYTEA NULL,
    ADD COLUMN candidate_signed_item_set_digest BYTEA NULL,
    ADD COLUMN operation_expires_on TIMESTAMPTZ NULL,
    ADD COLUMN operation_quarantine_reason_code VARCHAR(100) NULL;

CREATE INDEX ix_content_ops_org_record_state
ON authorization.content_change_operations
(organization_id, form_record_id, operation_state_name, operation_created_on);

-- DB worker claims/epochs and audit records do NOT, by themselves, prevent
-- a zombie worker's later external write. Use a SpiceDB-held marker CAS with
-- verified exact semantics on EACH external mutation (including multi-batch).
CREATE TABLE authorization.projection_worker_attempts
(
    projection_worker_attempt_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    projection_outbox_event_id UUID NOT NULL,
    projection_worker_identity VARCHAR(200) NOT NULL,
    claimed_worker_fencing_epoch BIGINT NOT NULL,
    attempted_authorization_revision BIGINT NOT NULL,
    projection_attempt_state_name VARCHAR(30) NOT NULL
        CHECK (projection_attempt_state_name IN
            ('Started','Succeeded','Ambiguous','Failed','Reconciled')),
    projection_attempt_started_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    projection_attempt_finished_on TIMESTAMPTZ NULL,
    projection_attempt_error_code VARCHAR(100) NULL,
    CONSTRAINT fk_projection_attempt_org FOREIGN KEY (organization_id)
        REFERENCES authorization.organization_authorization_states (organization_id),
    CONSTRAINT fk_projection_attempt_event FOREIGN KEY (projection_outbox_event_id)
        REFERENCES authorization.projection_outbox_events (projection_outbox_event_id)
);

CREATE INDEX ix_projection_worker_attempts_revision
    ON authorization.projection_worker_attempts
    (organization_id, attempted_authorization_revision, projection_attempt_started_on);

COMMIT;

-- POST-MIGRATION REQUIRED:
-- 1. Backfill and validate ck_committed_operation_identity, same-tenant FK checks.
-- 2. Create authority-only DML grants + security immutable journal/ledger controls.
-- 3. Implement authority CAS transactions and exact idempotency handling.
-- 4. Add immutable staged business rows keyed by operation ID + per-item identity;
--    never trust business DB local published status.
-- 5. Write and test SpiceDB projection revision/step-marker schema and CAS.
-- 6. Real integration/chaos/recovery tests before deployment.
