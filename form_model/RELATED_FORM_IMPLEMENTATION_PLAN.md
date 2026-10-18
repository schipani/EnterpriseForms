# Related Form Collections — Implementation Plan and Security Gate

**Status:** Implementation-ready reference package; application source repository has not been supplied. Do not represent contracts or SQL templates as deployed functionality.

## Ordered vertical slices

1. **Schema and migrations.** Reconcile `related-form-collections.migration.template.sql` with existing entity keys, tenant FKs, migration framework, RLS policies, privilege grants, and integrity/audit triggers. Populate no implicit relationships for existing data. Validate upgrade + rollback on an isolated PostgreSQL 18 instance.
2. **Designer metadata.** Add authorized relationship-definition CRUD; publish `relatedFormList` controls in immutable parent layout schema v2. Validate configured child-form identity, distinct list key, summaries, permissions, max counts and dependency DAG. Do not alter legacy repeating `FormRecordGroups`.
3. **Authority integration.** Add independent parent-child composition relationships and accepted relationship-operation journal. Specify authority commands for CreateChild, LinkExistingChild (off by default), Reorder, Unlink, Detach, Reparent. Authority validates tenant, existing parent and child types, membership, cycle freedom, role/delegation scope and monotonic revisions. Business DB is projection only.
4. **Transactional orchestration.** Implement idempotent multi-entity saga for create + link; reserve capacity, stage child values and documents, commit authority command and manifest v2 root, wait for SpiceDB barrier, project Business DB, verify, then publish visible child. Any failure leaves the child inaccessible pending reconciliation, not implicitly owned by creator.
5. **Authorization-safe reads.** Related collections must be queried through the authority's exact allowed child IDs and field/document scopes. Verify accepted link identities and each child's latest content manifest. Page before materializing. Return a backend projection with no hidden child IDs, fields, documents, totals or condition dependencies.
6. **Documents.** Expose a multi-attachment `documentCollection` control backed by existing `FileAttachments` and `IFileStorageProvider`. For metadata-rich documents, support separate Document child forms; no new raw blob engine.
7. **UI.** Use four-column mobile-first layout: collection consumes configured span, rendered as cards on mobile and authorized paged table on larger screens; only show actions allowed by current authority decision.
8. **Audit, integrity, recovery.** Emit transaction/correlation-linked events, Merkle v2 link leaves, archive accepted membership revisions, and reconcile current graph after DR restore; never silently restore revoked edges.

## API validation and error semantics

- `401` for unauthenticated; `403`/concealed `404` for denied access without disclosure; `409` for stale revision, capacity race or idempotency collision; `422` for invalid child form, cycle, bad bounds or values; `423` for integrity quarantine; `503` when required authority checks are unavailable.
- The caller cannot choose `TenantId`, `CreatedByUserId`, accepted authority revision, or signing key ID. Derive identity from validated server context and trusted authority.
- Use per-command idempotency keys and typed expected revisions. Do not claim a child is visible until the accepted graph revision, data manifest and projection are consistent.
- Avoid relation counts (including `0`) for recipients lacking list discovery permission; list presence itself may be confidential.

## Minimum tests and evidence

Run existing REL-01..REL-28 from `RELATED_FORM_COLLECTIONS.md`, plus:

- REL-29: Composite foreign keys reject mismatched parent/child form types and cross-tenant attempts, including manually crafted SQL.
- REL-30: A pending child link reserves the child identity; conflicting concurrent link fails cleanly.
- REL-31: Forged local sort order and hidden list membership cannot affect returned authorized order/items.
- REL-32: Disabling an option used by a child form preserves historic display but rejects attempted editable value save according to organization policy.
- REL-33: A user's authorized list summary never includes child fields newly added after the share snapshot.
- REL-34: Long parent-child graph and excessive list cardinality hit configured bounds rather than consuming unbounded memory.
- REL-35: Malicious child-form label/help text or file name does not become executable script in the renderer.
- REL-36: Authority outage during child upload/link does not expose pending document or child data and retries do not duplicate children.

**Production release gate:** PostgreSQL 18 migrations, .NET compile, running end-to-end child CRUD, SpiceDB role/relationship tests, Merkle v2 determinism, cross-tenant/RLS/adversarial DB changes and disaster-recovery tests must all pass. Existing G02/G03/G04/G08/G14 gates are independent and remain open without evidence.
