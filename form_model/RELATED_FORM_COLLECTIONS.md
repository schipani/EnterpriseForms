# Reusable Related Forms and Document Collections — Normative Design

**Status:** Required core design extension; not implemented or production-verified. This document supplements `CLAUDE.md` and must be read alongside its sharing, encryption, audit, Merkle, authority, and disaster-recovery protocols. Existing published form layouts and Merkle v1 vectors remain compatible.

## 1. Goal and terminology

A Designer can create a reusable `AccusedAddress` form definition (with its own stable field definitions, draft/publish versions, validation, localized labels, typed data and security policies) and include a multi-record list of addresses in an `Accused` form. The `Accused` parent and each `AccusedAddress` are **separate FormRecords**, not repeated groups of the parent. Each associated address carries its own `FormRecordId`, `CreatedFormVersionId`, content revision, field values, audit trail, and independent Merkle content head. A parent may include several named lists of the *same* child form definition with different roles (e.g. current addresses versus historical addresses) using distinct relationship-definition identities.

Existing `GroupDefinitions`/`FormRecordGroups` continue to support inline repeated groups **inside a single record**. A reusable child form list uses `FormRelationshipDefinitions` and `FormRecordRelationships` instead. Do not represent child data as JSON arrays, reuse the parent's `FormRecordId` for each child, or copy child fields into the parent's typed value rows.

Two attachment experiences share the existing `FileAttachments` storage engine:
- **Document Collection field:** one `FieldDefinition` of storage type `File`, rendered with `control: "documentCollection"` (or compatible `fileUpload` with `allowMultiple: true`), associating zero or more attachments with the parent or child record. Preserve individual `FileAttachmentId` / immutable versions / scanning / encryption / share metadata. Never introduce a second file-bytes store.
- **Related Document Form List:** a `Document` child form containing metadata such as `DocumentType`, `DocumentDate`, `Notes`, plus a File field. Each document row is a separate `FormRecord`, and its file is associated with that child record. Use when an uploaded document needs its own reusable, versioned business metadata.

## 2. Logical relational schema

Use PostgreSQL 18, UUID v7, snake_case and only descriptive multiword columns. Every surrogate primary key is singular entity name + Id in C#; database names are snake_case. Role-qualified foreign keys such as `ParentFormRecordId` and `ChildFormRecordId` are necessary because both reference the same table; do not replace them with ambiguous `RecordId`.

### FormRelationshipDefinitions (stable relationship identity)
- `FormRelationshipDefinitionId` primary key
- `TenantId`; `ParentFormDefinitionId` and `ChildFormDefinitionId` (both reference `FormDefinitions` in the same tenant)
- `RelationshipDefinitionKey` immutable unique within `(TenantId, ParentFormDefinitionId)`; `RelationshipDisplayName`
- `RelationshipLifecycleStatus` (Available/Retired), `RelationshipCreatedOn`, `RelationshipCreatedByUserId`, `RelationshipRevision`
- Relationship identities persist across parent form versions. Do not change the child form identity of a previously used relationship; create a new relationship definition and controlled migration instead.
- A published **parent** `FormVersions.LayoutJson` snapshot, not mutable relationship rows, defines the UI label/translation keys, placement in the four-column layout, minimum and maximum child count, summary-column identifiers, reorder and add/remove actions, allowed create/link-existing modes, child-form reference, and edit policy. Published child form versions remain independently immutable.
- Enforce unique `(tenant_id, parent_form_definition_id, relationship_definition_key)`; same-tenant parent/child FKs; reject self/indirect cycles in form-definition dependency graph (bounded-depth cycle check at publish). Limit depth, number of child items, and total authorized projection size per tenant/system policy. Do not derive a nested child definition merely from a mutable label.

### FormRecordRelationships (stable individual link)
- `FormRecordRelationshipId` primary key
- `TenantId`, `FormRelationshipDefinitionId`, `ParentFormRecordId`, `ChildFormRecordId`
- `ChildRecordSortOrder`, `RelationshipLifecycleStatus` (Pending/Active/Removed), `RelationshipRevision`
- `RelationshipCreatedOn`, `RelationshipCreatedByUserId`, nullable `RelationshipRemovedOn`, `RelationshipRemovedByUserId`, `RelationshipRemovalReason`
- signed integrity metadata / accepted link-operation ID and audit correlation; audited lifecycle and historic link versions. Database projections are **not** authoritative access grants.
- Enforce same-tenant parent/child, exact expected form definitions, no child=self, no cycles, one active parent relationship per child for v1 composition, unique active `(parent_record, relationship_definition, child_record)` and unique active child linkage. Enforce via indexes/triggers plus authority verification; PostgreSQL FKs alone cannot prove matching form types or absence of multi-level cycles.
- Distinguish **unlink** from child record archive/delete. Default unlink preserves child record data and history but removes it from parent results and inherited access. No hard cascading delete, and no implicit cross-parent reparenting. An explicit authorized reparenting workflow creates a new accepted link and removes the old one atomically at the logical-authority level; never use raw FK updates.
- Index `(tenant_id, parent_form_record_id, form_relationship_definition_id, relationship_lifecycle_status, child_record_sort_order, form_record_relationship_id)` and a tenant-safe child-lookup index. Use constrained `SELECT ... FOR UPDATE` on parent/link rows as needed to enforce cardinality and ordering during concurrent modifications; check limits again when committing.

### Attachment relationships
- For `documentCollection`/`fileUpload`, existing `FileAttachments.FormRecordId`, `FieldDefinitionId`, optional `FormRecordGroupId`, versions and object references associate files with the relevant parent or child FormRecord. Multiple files are separate `FileAttachmentId` rows; use per-field configuration (`maxFiles`, allowed MIME/extensions, size limits, encryption, scan). New attachments are not shared by old snapshot shares.
- No ambiguous implicit record-wide attachment relationship bypassing the configured File field. If a record-wide Documents list is required, render a dedicated hidden/system-managed `File` field definition with an explicit stable ID and protected policy; do not introduce an unrestricted backdoor for generic attachments.

## 3. Form designer / rendering contract

`FormRelationshipDefinitions` is created by a Designer authorized for the parent and authorized to reference the selected child FormDefinition. Child form design permissions are separate: designing an `Accused` form does not automatically permit editing `AccusedAddress`.

Parent published layout example (illustrative IDs are placeholders, not literal production GUIDs):

```json
{
  "layoutSchemaVersion": 2,
  "sections": [{
    "sectionKey": "accused-details",
    "title": "Accused Details",
    "gridColumns": 4,
    "elements": [
      {"kind": "field", "fieldDefinitionId": "<accused-name-field-uuid>", "layout": {"columnSpan": 2}},
      {
        "kind": "relatedFormList",
        "formRelationshipDefinitionId": "<accused-address-relationship-uuid>",
        "childFormDefinitionId": "<accused-address-form-uuid>",
        "label": "Addresses",
        "layout": {"columnSpan": 4, "startOnNewRow": true},
        "listSettings": {
          "minimumItems": 1, "maximumItems": 10,
          "allowCreate": true, "allowLinkExisting": false,
          "allowReorder": true,
          "summaryFieldDefinitionIds": ["<address-kind-field-uuid>", "<city-field-uuid>"]
        }
      },
      {
        "kind": "field", "fieldDefinitionId": "<accused-files-field-uuid>",
        "control": "documentCollection", "layout": {"columnSpan": 4},
        "settings": {"allowMultiple": true, "maxFiles": 100}
      }
    ]
  }]
}
```

The Designer selects **Related Form List**, selects an eligible child form, names the list (`AccusedAddressList`), configures min/max, child creation, summary columns, user-visible permissions, sorting, and its grid placement. Parent published layout versions preserve the relationship reference and UI configuration. Child records resolve Historical/Latest/Hybrid using the **child form's own** `VersionBehavior`. Historic parent rendering must use the parent snapshot; nested child rendering must use the appropriate child version, not blindly flatten the child fields into parent JSON.

UI: on mobile show related lists as accessible cards/accordion sections; on desktop as a bounded table with authorized summary columns and Add/Edit/Remove/Reorder actions. Use independent pagination and limits for large lists. Never fetch all children automatically or assume hidden fields can be sent and concealed on the frontend. Localized labels use child form translations; stored data and documents are never machine translated.

## 4. Ownership, scoped sharing and authorization (Level 2)

**V1 ownership policy: composition / inherited ownership.** A child created through a parent relationship inherits the parent's authoritative owner (user or team), regardless of which authorized Contributor creates the child. `CreatedByUserId` still records the actual creator. Ownership must be resolved from the **Authorization Authority** parent-child composition graph; a direct update to business PostgreSQL cannot change it. An ownership transfer of the parent automatically changes effective child ownership. The child's local `OwnerUserId`/`OwnerTeamId` projections, if present, are derived and are not security authority.

Default sharing is DENY beyond the owner: granting a partial share of the parent does not automatically expose all children. A share must explicitly enumerate permitted relationship identities, child link/item IDs and fields/documents, or an approved deliberately bounded scope. For a selected child projection, the authority checks BOTH the authorized parent relationship and each eligible child field/document action; do not rely on only one check. New child records, new child fields and documents are excluded from existing snapshot shares. `Manager` may delegate only access they possess and are allowed to delegate. Removing the parent link or revoking any upstream share immediately removes the corresponding path, even if old URL, search index or business projection remains.

Optional **direct child access** must be explicitly allowed by organization/relationship policy and granted through the Authorization Authority; it must not implicitly authorize navigation to the parent or sibling records. Separate, explicit `DetachToStandaloneRecord` / `TransferChildOwnership` commands are required to give a child its own owner. Keep direct child sharing OFF by default in v1 to simplify the invariant.

Record access challenges and encryption floors continue to apply to both parent and child. If the parent is challenge-protected, every inherited-access operation on its children must satisfy that challenge. A child may impose stronger protection; the combined access path must satisfy both. Partial projections strip hidden links, child identities, fields, documents, labels and calculated/conditional dependencies. Search over child values must first authorize discoverability of the relationship, child item, and searched field. Counts, sorting, summaries and suggestions must not disclose unauthorized siblings or values. Encrypted child data is non-searchable by default.

Business-DB SQL links have **zero authorization effect** until the independent authority accepts the link and its current revision. Add relationship/grant operations and authority-visible link status to SpiceDB/security journal via vetted commands. Validate the authorization graph and resource membership from authority state on every protected read; stale projections fail closed.

## 5. Integrity, audit and recovery

- Every relationship row receives canonical HMAC integrity protection and an immutable `FormRecordRelationshipId`, binding tenant, parent/child identities, definition, order, lifecycle and revision. Link memberships require an **independently authoritative complete child-link index** with revision/rollback protection, just as RecordShareFields require authoritative membership.
- Extend the parent Merkle manifest with a new item kind `RelatedRecordLink` that binds the accepted `FormRecordRelationshipId`, definition, child record ID, relationship status and order. Do NOT change Merkle-v1 kind codes or previously published test vectors in place. Introduce a documented **Merkle manifest schema v2** with a new domain/version and a distinct leaf kind code (proposed code `7`) plus migration/test vectors. Each child retains its **own** independently verified content root; do not require re-signing every parent/ancestor merely because a child's field value changes. The parent commits changes only when its relationship collection changes.
- A parent link is not proof a child content snapshot is current: verification must query the independent accepted content head for that child. A partial share must receive an exact authorized edge/child set from the authority, not merely a Merkle inclusion proof for a subset.
- Link/unlink/reorder operations use a revision-bound authorized command, immutable staged candidate and authority CAS; update parent manifest and authority link index consistently before making new links visible. Child create and parent link is a multi-entity saga, not an assumed cross-system distributed transaction. `Pending` orphans are inaccessible, idempotently finalized or cleaned up through a time-bounded recovery policy. If authority commits but local DB projection fails, deny affected reads/reconcile; never infer authority from DB rows.
- Audit parent and child form designs separately; record creation, list insert, reorder, unlink, archive, reparent, individual document upload/download, and effective-share changes. Include correlation IDs, actor, parent/child identifiers and redacted metadata. Do not log encrypted values, passphrases or plaintext document content.
- Recover parent, child, and relationship state from independently verified authority events, content snapshots and object versions. Never restore a deleted/revoked relationship from an old business DB backup. Reconcile graph traversal across restored heads; quarantine missing children or unverified links. Legal holds/retention apply to child records and documents individually; no implicit cascade purge.

## 6. API contracts

Suggested endpoints (adapt to existing project conventions):

| HTTP | Route | Operation |
|---|---|---|
| GET | `/api/form-definitions/{formDefinitionId}/relationships` | Eligible child relationship definitions |
| POST | `/api/form-definitions/{formDefinitionId}/relationships` | Define a reusable child collection (Designer only) |
| GET | `/api/form-records/{formRecordId}/relationships/{formRelationshipDefinitionId}` | Authorized, paginated child list |
| POST | `/api/form-records/{formRecordId}/relationships/{formRelationshipDefinitionId}/children` | Create authorized child and link |
| PATCH | `/api/form-record-relationships/{formRecordRelationshipId}` | Authorized reorder/status update with revision checks |
| DELETE | `/api/form-record-relationships/{formRecordRelationshipId}` | Unlink (soft), not delete child content |
| GET | `/api/form-records/{formRecordId}/related-content` | Authorized, bounded composite renderer projection |

`IFormRelationshipService` handles schema/cardinality/transactional linkage, `IRelatedRecordProjectionService` obtains current authority-scoped link and field permissions before calling existing record data and manifest verifiers, `IRecordAuthorizationService` remains the enforcement abstraction. Do not duplicate authorization engines or introduce an independently trusted relationship list in business PostgreSQL.

For each command: use authenticated principal + tenant; current parent/child form versions, expected parent/child/link/auth/content revisions, idempotency keys, and auditable action context. Refuse cross-tenant links, unsupported child form versions, cycles, race-lost capacity changes, and stale delegated access. List APIs return only authorized summary fields with independent pagination; avoid direct ORM eager-loading of entire child graphs.

## 7. Tests and release gates

Minimum acceptance/adversarial tests (REL-01–REL-28, extended by REL-29–REL-36 in the implementation plan):

1. Create `Accused` and reusable `AccusedAddress`; publish both; add two child records; fields remain typed and searchable under permitted scopes.
2. Create a second list on the same parent form referencing the same child form with a different relationship definition; verify stable link identity and separate list placement.
3. One child uses historical layout while parent uses latest; correct version and locale label rendering.
4. Add a field to `AccusedAddress` V2; prior address records remain valid, values absent until added, existing field identities retained.
5. Create parent document collection with three individually versioned/encrypted/scanned attachments; no second storage engine.
6. Create `AccusedDocument` child form with document metadata and file field; verify record/document association.
7. Anonymous, unrelated and other-tenant actors cannot enumerate or link child records.
8. Parent Contributor creates child but does not become child Owner; owner authority comes from parent.
9. Parent Owner transfers to team; children immediately resolve to team owner after authority commit and SpiceDB barrier.
10. Parent Reader with no related-item share sees no list/item existence or count.
11. Share one address with only two authorized child fields; sibling addresses and hidden child fields are absent from API and UI.
12. Share a single attachment from a child without exposing other attachments or unauthorized child metadata.
13. Revocation/upstream delegation removal immediately denies inherited child access and child document downloads.
14. Parent challenge and stronger child challenge both enforced; no child endpoint bypass.
15. Cross-form child search filters, sort, count and autocomplete do not leak unauthorized child data.
16. Designer cannot select child form from other tenant or edit child form without permission.
17. Publish blocks form dependency cycles, unknown IDs, invalid child definition, unreasonable nesting and invalid list bounds.
18. Concurrent child additions at maximum capacity: exactly permitted count commits; retries are idempotent.
19. Unlink preserves child bytes and history; parent listing excludes link; no cascade erase.
20. Reorder saves stable UUIDs and deterministic order; old snapshots/audit remain verifiable.
21. DBA forges, deletes, replays or reassigns `form_record_relationships`: authority prevents accepted access; reconciliation detects tampering.
22. Business DB restores old link after revocation: external authority revision prevents rollback.
23. Merkle-v1 old records still verify after introduction of v2 relationship-link leaves; deterministic v2 vectors pass.
24. Parent link in manifest with missing/tampered child value or file causes child read to fail closed/quarantine.
25. Application crashes between child creation, authority acceptance and local projection; no orphan becomes visible/authorized.
26. Remove related-list control from parent V2 and restore in V3: links and child data retained and recoverable.
27. Detached child cannot be accessed through old parent path; explicit direct-share/owner migration follows authority.
28. Full disaster recovery restores only independently accepted parent-child links and committed child content.

**Security gate:** This feature is NOT production-ready until relationship authorization, Merkle-v2 migration, cross-system consistency, historical child compatibility, and database tampering tests pass in a production-equivalent isolated environment. Existing G02/G03/G04/G08/G14 gates remain open until their separate evidence exists.

## 8. Implementation handoff (addendum)

The normative design above is now accompanied by an explicit implementation plan at `RELATED_FORM_IMPLEMENTATION_PLAN.md`, a PostgreSQL 18 migration **template** at `related-form-collections.migration.template.sql`, .NET 10 **reference** service contracts at `RELATED_FORM_DOTNET_CONTRACTS.reference.cs`, and a business REST API contract at `related-form-collections.openapi.yaml`. The template adds composite same-tenant and form-identity foreign keys, a current-child uniqueness reservation and a database-side composition cycle defense. These checks are defense-in-depth: the independent authority remains decisive. Execute REL-01..REL-28 above and REL-29..REL-36 from the implementation plan before production approval.
