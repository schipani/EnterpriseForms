-- LEVEL 2 G03 Merkle manifest — isolated AUTHORITY PostgreSQL ONLY.
-- Reference migration, NOT deployable without cross-store CAS, tenant FK,
-- append-only permissions, tested signing verification and read integration.
-- Business PostgreSQL roles/DBAs must NEVER have grants on this database.
-- PostgreSQL 18 uuidv7() used; adjust if an earlier supported version.

BEGIN;

-- Separate monotonic CONTENT integrity sequence is necessary: a normal field
-- edit does not necessarily increment the organization's ACL authorization
-- revision. Anchor these two independent streams separately.
ALTER TABLE authorization.organization_authorization_states
  ADD COLUMN content_integrity_revision BIGINT NOT NULL DEFAULT 0
    CHECK (content_integrity_revision >= 0);

CREATE TABLE authorization.content_integrity_checkpoints
(
    content_integrity_checkpoint_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    content_integrity_revision BIGINT NOT NULL
      CHECK (content_integrity_revision > 0),
    form_record_id UUID NOT NULL,
    committed_content_revision BIGINT NOT NULL
      CHECK (committed_content_revision > 0),
    committed_operation_id UUID NOT NULL,
    manifest_root_digest BYTEA NOT NULL
      CHECK (octet_length(manifest_root_digest) = 32),
    previous_checkpoint_digest BYTEA NULL
      CHECK (previous_checkpoint_digest IS NULL OR
             octet_length(previous_checkpoint_digest) = 32),
    checkpoint_event_digest BYTEA NOT NULL
      CHECK (octet_length(checkpoint_event_digest) = 32),
    checkpoint_ledger_receipt BYTEA NULL,
    checkpoint_archive_state VARCHAR(30) NOT NULL DEFAULT 'Pending'
      CHECK (checkpoint_archive_state IN
        ('Pending','Anchored','VerificationFailed')),
    checkpoint_created_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    checkpoint_anchored_on TIMESTAMPTZ NULL,
    CONSTRAINT uq_content_integrity_org_revision
      UNIQUE (organization_id, content_integrity_revision),
    CONSTRAINT fk_content_integrity_org
      FOREIGN KEY (organization_id)
      REFERENCES authorization.organization_authorization_states (organization_id)
);

CREATE TABLE authorization.record_manifest_commits
(
    record_manifest_commit_id UUID PRIMARY KEY DEFAULT uuidv7(),
    organization_id UUID NOT NULL,
    form_record_id UUID NOT NULL,
    committed_operation_id UUID NOT NULL,
    committed_content_revision BIGINT NOT NULL
      CHECK (committed_content_revision > 0),
    manifest_schema_version INTEGER NOT NULL
      CHECK (manifest_schema_version > 0),
    manifest_algorithm_version INTEGER NOT NULL
      CHECK (manifest_algorithm_version > 0),
    row_signature_key_version INTEGER NOT NULL
      CHECK (row_signature_key_version > 0),
    manifest_leaf_count BIGINT NOT NULL
      CHECK (manifest_leaf_count >= 1),
    manifest_root_digest BYTEA NOT NULL
      CHECK (octet_length(manifest_root_digest) = 32),
    manifest_leaf_index_digest BYTEA NOT NULL
      CHECK (octet_length(manifest_leaf_index_digest) = 32),
    manifest_committed_on TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT uq_manifest_record_revision
      UNIQUE (organization_id, form_record_id, committed_content_revision),
    CONSTRAINT uq_manifest_committed_operation
      UNIQUE (organization_id, form_record_id, committed_operation_id),
    CONSTRAINT fk_manifest_content_head
      FOREIGN KEY (organization_id, form_record_id)
      REFERENCES authorization.record_content_heads (organization_id, form_record_id),
    CONSTRAINT fk_manifest_content_op
      FOREIGN KEY (committed_operation_id)
      REFERENCES authorization.content_change_operations (content_change_operation_id)
);

CREATE TABLE authorization.record_manifest_leaves
(
    record_manifest_leaf_id UUID PRIMARY KEY DEFAULT uuidv7(),
    record_manifest_commit_id UUID NOT NULL,
    manifest_leaf_position BIGINT NOT NULL
      CHECK (manifest_leaf_position >= 0),
    manifest_item_key BYTEA NOT NULL
      CHECK (octet_length(manifest_item_key) = 17),
    manifest_item_kind VARCHAR(40) NOT NULL
      CHECK (manifest_item_kind IN
        ('RecordHeader','RepeatedGroup','FieldValue','FieldSelection',
         'Attachment','AttachmentLifecycle')),
    manifest_item_id UUID NOT NULL,
    manifest_leaf_digest BYTEA NOT NULL
      CHECK (octet_length(manifest_leaf_digest) = 32),
    CONSTRAINT fk_manifest_leaf_commit
      FOREIGN KEY (record_manifest_commit_id)
      REFERENCES authorization.record_manifest_commits (record_manifest_commit_id),
    CONSTRAINT uq_manifest_leaf_key
      UNIQUE (record_manifest_commit_id, manifest_item_key),
    CONSTRAINT uq_manifest_leaf_position
      UNIQUE (record_manifest_commit_id, manifest_leaf_position),
    CONSTRAINT uq_manifest_leaf_item
      UNIQUE (record_manifest_commit_id, manifest_item_kind, manifest_item_id)
);

CREATE INDEX ix_manifest_commits_record_revision
    ON authorization.record_manifest_commits
        (organization_id, form_record_id, committed_content_revision DESC);
CREATE INDEX ix_manifest_leaves_commit_position
    ON authorization.record_manifest_leaves
        (record_manifest_commit_id, manifest_leaf_position);

-- Current head binds to exact committed manifest commit, in addition to
-- its existing operation/revision/root columns. Set with a CAS transaction
-- which first inserts the complete verified manifest and its immutable leaves.
ALTER TABLE authorization.record_content_heads
  ADD COLUMN current_record_manifest_commit_id UUID NULL;

ALTER TABLE authorization.record_content_heads
  ADD CONSTRAINT fk_current_record_manifest_commit
    FOREIGN KEY (current_record_manifest_commit_id)
    REFERENCES authorization.record_manifest_commits (record_manifest_commit_id);

-- Add this constraint only after correct backfill for all nonzero heads:
ALTER TABLE authorization.record_content_heads
  ADD CONSTRAINT ck_content_head_manifest_identity
  CHECK (
    (committed_content_revision = 0 AND current_record_manifest_commit_id IS NULL)
    OR
    (committed_content_revision > 0 AND current_record_manifest_commit_id IS NOT NULL)
  ) NOT VALID;

COMMIT;

-- NOT ENFORCED BY THESE TABLE DEFINITIONS ALONE:
-- * manifest rows/leaves immutable after trusted commit;
-- * commit leaf_count == COUNT(leaves), all positions 0..N-1;
-- * leaf_index_digest and Merkle root recompute exactly;
-- * head's revision, root and operation equal the referenced commit;
-- * operation belongs to same organization/record and is approved;
-- * only the independently authenticated authority committer has write grants;
-- * monotonic content_integrity_revision increments in the SAME authority
--   transaction as manifest commit/head CAS, with checkpoint digest chained.
-- * external anchored accepted checkpoint is newer than restores.
-- Enforce in serialized authority COMMIT transaction and hardened service,
-- audited immutable journal, backfilled constraints and integration tests.
-- Do NOT permit direct DML by business runtime; signatures are not sufficient.
-- Business DB may cache proof nodes/index; they are untrusted hints only.
