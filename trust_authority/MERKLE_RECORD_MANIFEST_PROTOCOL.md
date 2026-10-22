# Level 2 — Complete Record Manifest and Anti-Replay Protocol

> **Security simplification cross-reference:** For duplicate audit-writer consolidation, confidentiality defaults, trusted search completeness, high-assurance step-up and future HMAC/Merkle optimization, see `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` (ADR-SEC-2026-10-16-01). This document remains normative for its own trust boundary and state machine; no prior anti-rollback/recovery requirement is weakened.


**Status:** Normative implementation specification, **not implemented or verified**. Security gap G03 (content membership/completeness/anti-replay). Must be used with `TRUST_AUTHORITY_ARCHITECTURE.md`, `CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`, Section 4L of `CLAUDE.md`, and the independent Authorization Authority. This document is a security design, not a certificate that any vendor product or application is secure.

## 0. Scope, threat, and boundaries

An attacker may read/write/delete/restore *any* row in **business PostgreSQL**, change owner/team/share projections, restore previous valid signed rows, alter indexes and search projections, and tamper with business-side staged content. They cannot administer the independently protected authority PostgreSQL, the authority deployment, SpiceDB, Managed HSM, ledger, or trusted business-application *code/runtime* (the latter is a Level 2 assumption). They may read standard unencrypted typed data, which Merkle trees do **not** conceal.

**Goals**: detect substitution, unauthorized addition, deletion/omission, replay, wrong tenant/record/group/field/document association, and older content revisions on a protected record read. Detect orphaned/unregistered business records and unauthorized documents before release. Preserve old form versions; dynamically added fields do not break previously accepted content.

**Non-goals**: Proving completeness of an *arbitrary full-text search* over compromised business PostgreSQL; protecting already downloaded bytes; preventing a trusted authority/KMS administrator attack; protecting non-encrypted values from a DBA with SELECT. Form-design metadata, authorization relationships/graph state, audit history, and organization policies need their own independently protected registries/checkpoints; this record-content manifest does not magically attest those other datasets.

## 1. Explicit architecture decision

Use a **deterministically ordered binary Merkle tree** of the complete set of immutable persisted content items for one `(organization_id, form_record_id, committed_content_revision, committed_operation_id)`. Authoritative manifest descriptor, accepted Merkle root, complete leaf **identity-and-digest index**, count, and accepted content head live in the independently administered **authority store**. Business PostgreSQL stores signed candidate field/group/attachment metadata and optionally cached proof nodes; ciphertext file bytes live in object storage. The business copy is never the source of trust.

**Why retain the authoritative leaf index?** A Merkle membership proof can prove that a returned item belongs, but cannot by itself prove that a returned subset is *all* eligible fields. The authority therefore retains the expected complete leaf identity set (or an equivalently verified complete-index structure) and derives allowed subsets server-side. For the first secure release, prefer correctness over optimizing away the trusted index. Later optimized batched/range proofs require independent adversarial review and tests before replacing this invariant.

For every accepted revision, the authority stores all current leaf identifiers and hashes as an immutable snapshot and atomically advances its **single latest content head**. Historical snapshots are retained per policy. If size becomes prohibitive, a trusted immutable manifest blob in independently administered object storage plus its root/count/index can replace row-per-leaf snapshots, but *never* a manifest only in business PostgreSQL.

## 2. Integrity scopes and leaves

Each logical content item has a unique, version-independent `item_key` constructed from a **fixed, typed tuple**: `(item_kind, immutable_item_id)` plus the tenant/record bindings in the encoded leaf. Use a unique immutable ID for every repeated group instance, selection, typed-value item and attachment version. The definition of a value is immutable once assigned; its payload can change through revisions, but an item ID must never be reassigned to an unrelated field, group or tenant.

Initial item kinds:

| Kind | Identity | Signed payload / commitment | Notes |
|---|---|---|---|
| `RecordHeader` | `form_record_id` | creator, form/version refs, logical record status, any content metadata not otherwise independently attested | Mandatory exactly once per nonempty record |
| `RepeatedGroup` | `form_record_group_id` | stable group definition, parent group ID, instance order/identity, version and audit-relevant metadata | Also signs the group hierarchy / parent pointers |
| `FieldValue` | stable typed value row ID | field ID, logical type, group ID/NULL, exact stored canonical value or encrypted value envelope, `row_revision` and `row_signature` | One leaf for each *persisted* value (not for a field newly defined but still absent) |
| `FieldSelection` | stable selection ID | field and option IDs, group ID and selection metadata | Multiples handled as distinct item IDs |
| `Attachment` | attachment/version ID | record, field/group IDs, original metadata under classification policy, storage blob immutable version/generation, size, content digest, algorithm, encryption envelope/AAD/key reference, scan/availability policy state | May be present only when scanned/accepted; uncommitted uploads are never visible |
| `AttachmentLifecycle` | stable event/item ID when separately persisted | explicit remove/replace relationship or tombstone if retention requires | Deleting a document removes it from *current* leaf set only after an authorized committed revision; retained historical tree still references old version |

Do **not** mix ACL grants and role/team memberships into this content tree. Those remain authoritative only in the independent authorization desired-state/journal and SpiceDB, bound by auth revision. Do not infer permissions from the existence of a leaf. The renderer must authorize fields/documents separately.

Form-version changes alone do **not** re-sign old records or add synthetic `NULL` leaves. A new persisted field creates a new leaf. A record shown with Latest/Hybrid form layouts must still use its own accepted **content** manifest for persisted values, while form-definition and security-floor integrity are checked separately. Removing a field from the layout does not delete the leaf. A legitimate explicit data deletion updates the manifest in a new revision and preserves historic evidence.

### 2.1 Exact leaf binding

Let `CanonicalItem` be the versioned, deterministic binary encoding already required by CLAUDE.md §4L, including domain, entity/table kind, organization ID, form record ID, immutable item key, content revision, committed or prepared operation ID, immutable parent/field/document identifiers, row signature schema/version, row revision, all signed payload columns (including typed nulls), and ciphertext envelope metadata when relevant.

Use the independently authorized signing service to produce `row_signature = HMAC-SHA-256(org_integrity_key_version, CanonicalItem)` only after validating the command. The signature is for the **precise immutable candidate**; it is not a freely regenerable checksum.

Construct the leaf input using a **normative byte encoding**, never delimiter-concatenated strings:

```text
leaf_bytes = 0x00
  || ASCII("DynamicForms/MerkleLeaf/v1")
  || UInt16BE(17)
  || item_key[17]                       -- 1-byte kind code + RFC 4122 UUID bytes
  || verified_row_signature[32]        -- HMAC-SHA-256 tag
  || canonical_payload_digest[32]      -- SHA-256 over exact canonical item bytes
  || item_kind_code[1]
  || UInt64BE(committed_content_revision)
  || committed_operation_uuid[16]      -- RFC 4122/network byte order
leaf_hash = SHA256(leaf_bytes)
```

Kind codes: `1=RecordHeader`, `2=RepeatedGroup`, `3=FieldValue`, `4=FieldSelection`, `5=Attachment`, `6=AttachmentLifecycle`. The kind code inside `item_key` MUST equal the trailing `item_kind_code`. `CanonicalItem` and the `row_signature` are themselves bound to organization, form record, item identity, operation, revision, field/group/document IDs and other signed columns. `UInt16BE` and `UInt64BE` are unsigned big-endian. Only exact 32-byte signatures/digests and exact 16-byte UUIDs are valid; reject overflow and invalid lengths. The canonical payload digest is **computed from exactly the same canonical bytes the HMAC authenticates**, never from display-formatted or decrypted user-visible text. For encrypted fields, those bytes contain the **ciphertext envelope**, not the sensitive plaintext. Never expose internal digests or signatures as public search tokens.

This Merkle leaf framing is distinct from the canonical row/HMAC format in `row-integrity-format.md`; both formats need independent versioned test vectors. The initial Merkle algorithm uses SHA-256 (32 bytes). Unknown algorithm and serialization versions fail closed.

A field update creates a new staged signed candidate/leaf bound to the next content revision, preserving the previous accepted candidate. Untouched content items must be represented with revision-stable signed *item envelopes* OR explicitly re-signed under the candidate operation in a controlled batch. **Chosen v1 policy: re-issue all leaf signatures for the candidate snapshot in one trusted batch**, to prevent ambiguous replay of unchanged rows across operations and simplify verification. Optimize to stable versioned immutable item envelopes only after formal design review and proof tests. This is an intentionally expensive initial policy; measure costs and cap maximum items per record.

### 2.2 Merkle tree construction (v1)

1. Verify leaf input signatures and exact canonical format before tree construction.
2. Reject duplicate leaf keys, mismatched organization/record ID, cycles or dangling group parents, foreign field IDs and invalid attachment references.
3. Sort leaves lexicographically by the **unsigned bytes** of canonical `item_key` (stable across languages). The canonical key consists of `item_kind_code` (1 byte) + fixed-length UUID bytes in RFC 4122 network order, with an explicit v1 length prefix where needed. A key must not vary with user locale, field label or client order.
4. Leaf hash: as above with a distinct `0x00` prefix. Internal node: `SHA256(0x01 || UTF8("DynamicForms/MerkleNode/v1") || left_hash || right_hash)`. For an odd number at a level, promote the final unpaired node **without duplicating** it. Reject non-canonical sibling paths and index/order inconsistencies. Empty set root: `SHA256(0x02 || UTF8("DynamicForms/MerkleEmpty/v1"))`, though a valid persisted form record has exactly one `RecordHeader` leaf.
5. Compute `leaf_index_digest = SHA256(0x03 || ASCII("DynamicForms/ManifestIndex/v1") || UInt64BE(leaf_count) || concat_for_sorted_leaves(UInt16BE(17) || item_key[17] || leaf_hash[32]))`. Commit `(organization_id, form_record_id, content_revision, committed_operation_id, manifest_schema_version, algorithm_version, leaf_count, merkle_root, leaf_index_digest, key_version)` to the **authority store** atomically with accepted content-head CAS. `leaf_index_digest` is the digest of the complete, sorted `(item_key, leaf_hash)` vector using domain-separated framing. The authority index and Merkle root must agree; preserve all historical heads by revision.
6. A content edit **does not necessarily change authorization_revision**. Every content-head commit therefore increments a **separate per-organization `content_integrity_revision`** in the same authority transaction, writes an independently hash-chained `content_integrity_checkpoints` event containing record ID, content revision, accepted operation ID and root, and forwards that content journal to Confidential Ledger. Do not reuse an authorization-revision-only checkpoint as proof of content currency. Recovery compares *both* externally anchored streams. A ledger receipt does not replace database transaction/outbox and full verification.

**Note:** RFC 9162 specifies Merkle tree proofs for an *append-only log*. This is a versioned map snapshot with ordered leaves and separate old/new roots, **not** the RFC 9162 append-only log consistency protocol. Do not claim RFC 9162 consistency proofs directly establish map-snapshot anti-rollback. The independent authority head and ledger checkpoint establish which snapshot is current.

## 3. Authority record registry and completeness

- Register each record in the authority with authenticated creation and initial owner **before** exposing any business record. Its `organization_id`, `form_record_id`, allowed form references, and lifecycle state are immutable/authoritatively revised, including soft delete/restore.
- Authority holds one accepted content head per registered record and its complete ordered leaf index. Existing SQL rows not registered or not in the accepted leaf index are **never released**. A missing registered record or leaf fails closed; never assume absence proves deletion was authorized.
- On a **full record read**, authority determines the expected leaf keys from its independent index. Application retrieves exact staged rows by accepted `committed_operation_id` and keys and verifies **all** expected rows, duplicate count, HMACs, recomputed root and file digests/metadata before releasing any bytes. If the business DB silently omits one expected row, verification fails. An injected unregistered row cannot be returned. An optional background reconciliation scans raw business tables for *extra uncommitted rows*, because an unrequested extra row is not detectable simply by validating the expected subset.
- On a **partial share/view**, authority first resolves current authorization, field policy and scope; it then provides internally to the verified projection worker only the expected authorized leaf keys and commitments. The response contains **only** the authorized projected layout/values; hidden IDs, paths, counts, validation rules and metadata must not reach the client. Each returned item must belong to the accepted manifest and match authorized expected keys. Missing *authorized* expected items fails closed. Internal structural support leaves (record header and ancestor groups) may be required to validate the projection; verify those inside the trusted service without returning their contents or their existence to a partially authorized client. A share may cover individual repeated-group instances or document versions; verify exact parent/child identities rather than solely `FieldDefinitionId`.
- The v1 authority API may return a batch-scoped opaque verification handle tied to `(org, record, accepted content revision/root, authorization revision, caller, action, exact expected key set)`; use only with an authenticated internal verifier, short TTL, no client-readable manifest. Never accept a caller-provided root, leaf-count, authorized-key list or revision as trusted evidence.
- **Membership vs completeness:** A Merkle inclusion proof verifies a particular item against a trusted root; to prove a *complete* projection, compare against the complete independently enumerated expected set. Do not claim that sibling proofs alone prove no omitted items. For v1, the independent index comparison is mandatory.
- Rendering a record is not database discovery. Record discovery is sourced from independent authority; using business PostgreSQL alone to enumerate record IDs is insecure. Search results depend on a separate independently verifiable search strategy (G06).

## 4. Commit protocol integrated with G02

| Step | Trusted side effect | Visibility |
|---|---|---|
| 1. `PrepareContentChange` | Authority validates actor, tenant, auth revision, scope, challenge and expected old content head, authorizes exact add/update/remove delta and reserves unique idempotent operation ID | Old revision still visible |
| 2. Stage candidate | Trusted application stages **immutable complete candidate snapshot** and any encrypted file blobs under operation ID; authority-controlled signer produces bound row signatures; download does not expose candidate | Old revision still visible |
| 3. Verify candidate | Authority integrity verifier reads back staged candidate (untrusted DB/storage), authenticates each signed item and blob commitment, computes sorted leaf set and root, compares against expected old index transformed by allowed delta, and records verified descriptor in authority operation | Old revision still visible |
| 4. `CommitContentChange` | Under authority transaction lock/CAS: revalidate **current** permissions, mandatory security policy and expected head; atomically store immutable complete leaf index/snapshot, manifest commitment, accepted `committed_operation_id`, new head and audit/outbox; commit authority state | New revision becomes the only eligible revision |
| 5. Read new revision | Obtain accepted head from authority; enforce organization projection gate and field permissions; fetch exact immutable candidate and validate complete authorized content | New revision only if consistent, otherwise quarantine |
| 6. Reconcile | Independently detect uncommitted candidates, missing blobs/rows, orphaned business rows and authority projection drift. Never rewrite accepted root to match business PostgreSQL | No silent downgrade |

The authority must not trust a `candidate_manifest_root` submitted by the business service without recomputing it from a verified complete candidate and the independently expected item set. The HSM/signing service must not sign a candidate unless the approved operation binds the **exact** allowed delta, all changed identities and current security policy. The one accepted head is the final authority commit point; there is no XA transaction with business PostgreSQL or blob storage. If a crash follows authority commit, the candidate remains the head even if the business local `published` flag is false. If candidate bytes disappear, fail closed instead of showing the previous root.

Do not modify a candidate in place after staging; create new operation and re-stage on reattempt if necessary. Aborted candidates may be garbage-collected only after checking authoritative operation/head status; never delete ambiguous or committed content. Concurrent prepares may be allowed, but accepted-head CAS allows only one commit; losers must rebase/reprepare. Every state transition emits non-sensitive audit evidence.

**Share-scoped writes (Phase C5 clarification, decision D12; does not change the v1 signing or manifest policy).** For a non-owner editing through a contributor/manager share, step 1 classifies the delta with column classes instead of whole-row digests: the request carries the complete current snapshot, which the authority binds to its accepted head (exact key coverage, per-item SHA-256 equal to the accepted digests, header bindings, recomputed root and leaf-index digest). Each candidate item is then compared with its bound current item by class: identity and framing must be equal; the operation binding must equal the new operation and revision exactly; per-row surrogate storage keys must be fresh; protected content must be byte-identical except for an identity-preserving update of an explicitly shared, top-level, single-item value. Every leaf is still re-signed under the new operation (§2.1), and step 4 re-authorizes the stored mutation kinds against the current desired edges. Normative detail: `docs/architecture/PHASE_C5_COLLABORATION_CONTRIBUTOR_WRITES.md` §5–§7; decisions C-34…C-40.

### 4.1 Authorized deletions and tombstones

A legitimate item removal produces a **new** full manifest without its current leaf (or with a separate signed tombstone for retention/history when policy requires). It does not delete prior immutable snapshots or historical blob versions until retention permits. The authority content-integrity journal retains the authorized removal event and old root. A direct SQL `DELETE` has no new authority head and is detected as missing item on subsequent protected reads.

### 4.2 Attachment integrity

The attachment leaf binds `file_attachment_id`, immutable attachment version, target field/group, blob logical key, provider object *version/generation ID*, full byte length, plaintext content hash only if safe/needed and access-controlled, **ciphertext SHA-256 digest**, AEAD algorithm/nonce/tag or authenticated chunk-manifest digest, key reference/version, scan disposition, object ETag only as extra metadata, and logical lifecycle. Do not treat an ETag as a cryptographic content hash. On download, authorize and verify the accepted attachment leaf first; verify ciphertext identity/integrity and AEAD tag while decrypting. Do not release plaintext chunks before they are authenticated; use an authenticated framed/chunked design for large downloads. A file field's presence does not implicitly share all historical or future versions.

## 5. Search, auditing, performance, availability

**Search remains an open critical/High assurance problem (G06).** A compromised SQL index can omit matches, invent untrusted counts, manipulate sort/filter predicates or leak hidden-field predicates even if each returned record passes Merkle verification. In v1, authority-only record discovery returns an authenticated authorized candidate set; searchable field predicates must be evaluated only over integrity-verified authorized field data or an independently trusted indexed search projection. Until one of those approaches is implemented and tested, do not represent search output as complete/accurate under hostile-business-DB writes. For small tenants, a bounded verified scan may be an acceptable secure baseline with quotas; at scale choose a separately protected signed search index and consistency protocol. Do not silently broaden authorized search scopes or return DB-computed unverified counts.

**Audit:** Log verification success/failure summaries, missing/extra/replayed item identifiers where authorized, actor binding, accepted revision/root digest, operation, outcome, and correlation ID to independent security journal. No plaintext, PIN, search secrets, full ciphertext, secret key or protected field values in logs. Tamper incidents quarantine target resources, raise alerts, preserve evidence, and require independently authorized recovery/forward correction.

**Performance:** Initial policy verifies every expected leaf for a protected read and re-signs candidate snapshots. Provide batch signer/verification, bounded per-record item counts, memory-safe streaming, caching only with immutable committed root + auth revision and revocation-fenced handles, and metrics for leaf count, tree build time, HSM calls, read p95/p99 and storage amplification. No bypass of full verification under latency pressure. Backpressure or fail closed if caps exceeded. Benchmark huge repeating sections and thousands of attachments before optimizing to incremental trees/proofs.

**Availability/recovery:** If the authority, accepted index, HSM verification, external required checkpoint, trusted encryption policy, blob hash, or record projection is unavailable/inconsistent, deny release. Restoring business DB to an old snapshot without a matching accepted head is rejected. Restoring authority DB behind its independently anchored **authorization or content-integrity checkpoint** requires supervised recovery; never roll back accepted revisions solely to match business DB. Key rotation keeps versioned verification ability and does not silently resign tampered rows.

## 6. Release-gating acceptance tests

| ID | Adversarial scenario | Required outcome |
|---|---|---|
| MRK-01 | Change signed plaintext typed-value contents | HMAC mismatch; no data released |
| MRK-02 | Delete one accepted typed-value row | Missing expected leaf; fail closed |
| MRK-03 | Add rogue typed-value row or file attachment | Not rendered; independent raw-table reconciliation raises incident |
| MRK-04 | Replay an old valid signed value and signature | Accepted operation/revision/root mismatch; reject |
| MRK-05 | Restore a whole prior business DB snapshot | Authority head disagrees; fail closed |
| MRK-06 | Delete registered form-record header | No authorized read or search release |
| MRK-07 | Invent unregistered form record with valid-looking UUID | Not discoverable; no authority registration |
| MRK-08 | Swap field ID/group ID or record ID across signed items | Canonical HMAC binding fails |
| MRK-09 | Remove child repeated group or create orphan/cycle | Membership/graph validation fails |
| MRK-10 | Replace selected dropdown option row | Field selection signature/root mismatch |
| MRK-11 | Modify or omit accepted attachment metadata | Integrity failure; no download |
| MRK-12 | Swap blob object version with different ciphertext | Stored digest/version or AEAD verification fails |
| MRK-13 | Leave an uploaded uncommitted candidate after crash | Never rendered/downloaded; safe reconciliation |
| MRK-14 | Crash after authority commits, before local published flag | New accepted candidate remains authoritative; no fallback |
| MRK-15 | Concurrent prepares and commits against same head | Only one CAS succeeds; other rejected |
| MRK-16 | Revoke access during verified read | Fresh authorization/revocation rules enforced; no stale allowed cache |
| MRK-17 | Partial share selects two out of ten fields | Exactly those fields returned; hidden identifiers/metadata not returned |
| MRK-18 | One authorized selected field row missing | Fail closed; no partial misleading response |
| MRK-19 | Designer adds a field with no stored value | Old content head remains valid; no synthetic null leaf |
| MRK-20 | Authorized edit adds a new field-value item | New committed root includes new item; old immutable revision preserved |
| MRK-21 | Designer hides a previously stored field | Data remains protected in manifest and historical versions |
| MRK-22 | HMAC key rotation, old historical record | Old signed revision verifies with versioned key; unauthorized resign denied |
| MRK-23 | Duplicate key/order ambiguity or invalid canonical data | Deterministic rejection in C# and cross-language vector tests |
| MRK-24 | Unauthorized change to business root/leaf-count cache | Authority root/index wins; tampering detected or ignored |
| MRK-25 | Search index removes a legitimate matching record | System does **not** claim completeness; verified-search strategy detects or blocks under Level 2 guarantee |
| MRK-26 | Authority leaf-index row deleted or authority backup behind checkpoint | Trusted service quarantines; external checkpoint/recovery required |
| MRK-27 | Valid row copied from another tenant | Organization/record/item binding rejected |
| MRK-28 | Re-sign attempt for illegal delta | Authority denies; no generic signature endpoint |
| MRK-29 | Very large record and simultaneous writers | Bounds enforced; no unsafe skip or partial commits; measured SLO |
| MRK-30 | Entire-record share followed by new field or document | New item not automatically shared; share metadata remains explicit |
| MRK-31 | Forge business `published` flag or staging operation ID | Only authority-accepted operation is readable |
| MRK-32 | Crash at each prepare/stage/verify/commit/reconcile transition | No unauthorized visibility or rollback; idempotent recovery |

**Release gate:** Implement actual migrations, executable C# canonicalization and crypto code with published test vectors, a deterministic Merkle builder, a trusted manifest-index service, complete reader/projection verifier, upload integration, reconciliation and independent authority checkpoint verification. Run container integration/fault-injection tests against separate business PostgreSQL and authority PostgreSQL plus a controlled SpiceDB instance; run Azure staging tests with actual KMS and independent role grants. Independent security review is required. Passing prose checklists is not evidence. Mark G03 **design specified, implementation/verification OPEN** until test artifacts exist.

## 7. Reference standards

- [RFC 9162 Certificate Transparency v2](https://www.rfc-editor.org/rfc/rfc9162.html) — useful domain-separated tree proof reference, but its append-only log differs from this versioned map-snapshot protocol.
- [RFC 8785 JSON Canonicalization Scheme](https://www.rfc-editor.org/rfc/rfc8785.html) — only for suitable I-JSON data; decimal/date/ciphertext types need explicit binary encoding (do not round to JS doubles).
- [NIST SP 800-107 Rev.1](https://csrc.nist.gov/pubs/sp/800/107/r1/final) — hash applications and domain separation considerations.

## 8. Conformance vectors

`merkle-v1.test-vectors.json` contains fixed artificial three-item input, exact byte framing, all leaf digests, bottom-up levels, the expected Merkle root and complete-index digest, plus empty-root reference. The row-signature bytes are **invented fixed 32-byte fixtures**, not examples of correctly computed HMAC tags or permission to bypass signature verification. Production test suites must also generate real HMAC test vectors with a disposable test key and the actual canonical row serializer. Node framing/order are deterministic; the supplied vectors must match independent C#, Python and any TypeScript verification implementation exactly.


## G14 recovery note

The complete committed manifest is a **verification contract**, not a data backup. Ensure immutable candidate source bytes are independently recoverable for all accepted leaf identities (including repeated groups and document versions); otherwise a valid root can detect loss but cannot reconstruct it. Restore only the independently accepted current content head; missing committed bytes => quarantine. Validate referenced old HMAC and encryption key versions, object hashes and geo-restored logical object mapping. Refer to `COORDINATED_DISASTER_RECOVERY.md`; DR-11 through DR-16.

## Future-version extension: reusable child-record relationships (not Merkle v1)

`form_model/RELATED_FORM_COLLECTIONS.md` defines an approved **Merkle manifest schema v2** proposal to commit parent-to-child `FormRecordRelationships` with a `RelatedRecordLink` leaf (proposed new kind code 7). **This does not modify or reinterpret the Merkle-v1 leaf framing, kind-code domain, test vectors or accepted v1 roots.** V2 uses explicitly versioned serialization, domain separation, link membership registry and independently accepted parent/child heads. Parent leaves bind relationship identity, definition, tenant, child identity, link ordering and lifecycle; child's **content** is verified against its own latest trusted head rather than recursively hashed into every ancestor root. Introduce v2 only with a documented migration, deterministic interop vectors and the REL security tests. See `form_model/RELATED_FORM_COLLECTIONS.md` for the full rules.
