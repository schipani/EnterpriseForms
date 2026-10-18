-- REFERENCE ONLY - NOT A PRODUCTION MIGRATION.
-- PostgreSQL 18. Existing audit_events and RLS policies must be reviewed first.
-- Creates compatibility VIEWS, not new authoritative security/share event tables.
-- Run only after verifying underlying columns, RLS, grants, and tenant policy.
-- PostgreSQL 15+ supports security_invoker views. Preserve RLS of calling user.

CREATE OR REPLACE VIEW security_audit_events
WITH (security_barrier = true, security_invoker = true)
AS
SELECT
    audit_event_id,
    tenant_id,
    event_occurred_on,
    event_category,
    event_action,
    event_outcome,
    actor_user_id,
    form_record_id,
    correlation_identifier,
    event_metadata_json
FROM audit_events
WHERE event_category IN
    ('Authorization', 'Security', 'PermissionChange', 'OwnershipChange');

CREATE OR REPLACE VIEW record_share_events
WITH (security_barrier = true, security_invoker = true)
AS
SELECT
    audit_event_id AS record_share_event_id,
    tenant_id,
    event_occurred_on,
    event_action,
    event_outcome,
    actor_user_id,
    form_record_id,
    correlation_identifier,
    event_metadata_json
FROM audit_events
WHERE event_action IN
    ('RECORD_ACCESS_GRANTED', 'RECORD_ACCESS_REVOKED',
     'RECORD_SHARE_CREATED', 'RECORD_SHARE_UPDATED',
     'RECORD_SHARE_REVOKED', 'RECORD_SHARE_DELEGATED');

CREATE OR REPLACE VIEW ownership_transfer_events
WITH (security_barrier = true, security_invoker = true)
AS
SELECT
    audit_event_id AS ownership_transfer_event_id,
    tenant_id,
    event_occurred_on,
    actor_user_id,
    form_record_id,
    event_outcome,
    correlation_identifier,
    event_metadata_json
FROM audit_events
WHERE event_action = 'RECORD_OWNERSHIP_TRANSFERRED';

-- IMPORTANT:
-- * These views are READ-ONLY projections; do not write authoritative events
--   to them, and do not grant PUBLIC access. Apply explicit tenant-scoped grants.
-- * Security-command source-of-truth remains the independent Authority journal.
-- * AuditEvents is rebuildable search projection, not sole durable evidence.
-- * Verify that user-scoped RLS underlying audit_events is enforced through
--   `security_invoker`; a view must never be a system-wide audit bypass.
-- * Use an independently governed durable intake for security-sensitive reads;
--   outbox exclusively in BusinessDb is insufficient against malicious DBA.
-- * Legacy physical tables must be migrated, reconciled, reviewed, and dropped
--   only in a later approved migration; never overwrite them with these views.
