# Phase C5 — Architecture proposal: collaboration and field-scoped contributor writes

**Status: Revision 2 — APPROVED FOR CONTROLLED DEVELOPMENT (owner, 2026-10-22, decision C-34).** The owner approved Option A and decisions D1–D15 for implementation and testing with **synthetic data only**, under eight mandatory conditions recorded in C-34:
1. D3 single-select validity is verified at a trusted boundary, not on Business API assertions alone.
2. D4 fingerprint verification is allowed only with proof that the whole current snapshot equals the previously accepted, verified signed state; anything uncertain fails closed.
3. D14 classifies every signed column of every supported type and schema version; anything unknown fails closed.
4. D15 preservation never reuses stale operation bindings.
5. U5 commit-time authorization must be shown to hold, or the issue is escalated.
6. U7 legacy rows fail closed, with no bypass.
7. The V1 limits stay in force.
8. The switch, production guard and Level 2 review requirements stay in force.

This approval is **not** an independent cryptographic review, a production approval or a Level 2 certification, and it does not permit enabling the feature in production. Implementation status and deviations are tracked in `docs/PHASE_C5_STATUS.md`. Branch `phase-c/collaboration` from main `cf45a9e`.

What changed from revision 1:
* **Comparison rules (§5):** an explicit column-class model replaces revision 1's "every column equal except the operation binding". Every changing column is checked against an exact expected value, not ignored.
* **Preservation of unchanged content (§6):** unchanged encrypted values and document artifacts are preserved byte-for-byte, with an explanation of why this stays compatible with full-record signing.
* **Independent verification algorithm (§7):** how the Authority itself confirms that everything outside the contributor's scope is unchanged.
* **Identity-substitution proof (§8):** item, field, section, record and tenant identity changes are all rejected.
* **Findings (§9):** a resolution plan for each of C5-F1…C5-F5.
* **Concurrency and revocation (§10):** a linearization argument showing there is no check-then-commit gap.
* **Rollout switch (§11):** disabled by default everywhere, enforced at the Authority, fail-closed.
* **Negative tests (§12):** an expanded negative test catalogue.
* **V1 scope (§13):** the V1 restriction, with future functional requirements recorded but not approved.
* **Review (§19, §20):** unresolved issues for an independent reviewer, and the full wording of decisions D1–D15.

Inputs read: CLAUDE.md §4B, §4C, §4H, §4K–§4P, §4T; `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md`, `CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`, `CRYPTOGRAPHIC_SIGNING_AUTHORITY.md`; `SECURITY_GAP_ASSESSMENT.md`; `security_validation/SECURITY_PROOF_TEST_MATRIX.csv`; decisions C-05, C-12, C-17, C-28, C-32, C-33; and the code cited inline.

---

## 1. Summary

* **Root cause.** The Authority decides that an item is unchanged only when its signed canonical bytes are byte-identical to the accepted bytes (`ContentService.ClassifyMutations`, a digest comparison). By design, a new candidate never satisfies this. Every row binds the new `content_change_operation_id` and the new row revision. Value and document rows get a fresh storage key. Today, every encrypted value is also re-sealed. As a result, every item of the record classifies as `Update`, and a share contributor's write fails at the record header.
* **Recommended design (Option A, approved in principle).** Share-scoped writes add four things:
  1. The Authority binds the current snapshot to its **own** accepted leaf digests.
  2. For each item, it classifies every signed column into one of five classes — *identity*, *protected content*, *operation binding*, *storage key* or *schema framing* — and checks each class with an explicit rule.
  3. Out of scope, only items whose identity and protected content are byte-identical are accepted.
  4. Explicitly shared, identity-preserving top-level value changes are the only permitted updates.

  The business runtime **preserves** unchanged rows verbatim, including sealed ciphertext and document artifacts. It never decrypts anything outside the contributor's read scope.
* **What does not change:** RowHmac/v1, Merkle v1, the conformance vectors, the v1 "re-sign every leaf" policy (MERKLE §2.1), head CAS, the journal/checkpoint/archive gating, the SpiceDB schema, AuthorityDb and BusinessDb schemas, and owner write semantics. The owner path gains only the additive identity binding.
* **V1 scope (unchanged, D2):** change the value of an already-populated, top-level, explicitly shared field. Filling empty fields, multi-select editing and the rest are recorded as future requirements (§13).
* **Switch (D10):** `Content:ShareContributorWritesEnabled` defaults to `false` in **every** configuration. It is enforced by the Authority at prepare **and** commit. Absent, unparsable or invalid values count as disabled. A production-designated Authority refuses to start with it enabled until a reviewed code change removes that guard.
* **Review:** an independent specialist security review is mandatory at the production Level 2 gate. Ten issues are listed for the reviewer (§19).

## 2. The current write path and root cause

| # | Step | Where | Behaviour today |
|---|---|---|---|
| 1 | Authentication | BFF session → `RequestContext`; delegated assertion → `ActorResolver.BindAsync` | Local dev identity; PB-08 Entra and step-up are not implemented. |
| 2 | Record access | `UpdateFormRecordHandler` → `VerifiedRecordReader.ReadAsync(Edit)` | Authority head → staged rows of the committed operation → `VerifyRecordProjection(Edit)`. |
| 3 | Field-scoped authorization | `ContentService.VerifyProjectionAsync` | `form_record#edit` (= `effective_owner`, false for share recipients) → `discover` → bulk `field_item#edit` → `AuthorizedItems` = editable items; `IsCompleteProjection=false`. |
| 4 | Integrity verification | `VerifyStagedAgainstLeavesAsync` | Exact key set, digest, HMAC under the accepted key version, recomputed root, archived checkpoint (SEC-P12); a mismatch quarantines the head. |
| 5 | Concurrency | Handler: `ExpectedContentRevision` vs head | 409 `CONTENT_REVISION_CONFLICT`. |
| 6 | Candidate construction | `CandidateBuilder.ApplyValuesDetailed` | Rebuilds **every** value through `Carry` (decrypt → re-create → re-encrypt), with a new surrogate key per row. For partial scope it refuses out-of-scope, clear, add, multi-select and entry changes, and **skips** rules, requiredness and dependent options. |
| 7 | Envelope | `RecordCandidateStager` | Every row gets the derived operation id, `row_revision` = head + 1, the current key version and the **current** schema version (headers become `form_records` v3). |
| 8 | Prepare | `ContentService.PrepareAsync` | Idempotency, authorization-revision CAS, record active, not quarantined, content revision/root CAS, `ParseCandidate`, **digest-based `ClassifyMutations`**, `AuthorizeMutations`, protection barrier, sign every leaf, persist operation + candidate leaves (one AuthorityDb transaction under the organization row lock). |
| 9 | Business write | `StageCandidate` | Immutable staged rows (one BusinessDb transaction); invisible until accepted. |
| 10 | Commit | `ContentService.CommitAsync` | Under the organization row lock: barrier, same actor, head CAS, **re-authorization against current desired edges**, staged bytes = signed candidate, manifest + head + content journal + checkpoint (one transaction). |
| 11 | Audit | `AuditRecorder` `RecordUpdated`; Authority content journal | Business audit is a projection; the Authority journal is the trusted record. Reads are gated until the checkpoint is archived. |
| 12 | Recovery | Journal replay, head CAS, quarantine (G14) | Unchanged by this proposal. |

**Why contributors are refused.** `AuthorizeMutations` already allows a non-owner to `Update` shared `FieldValue` items. It never helps, because classification is a digest comparison and the digests of *all* items differ (step 7). The header and unshared items therefore look like updates, and the actor may not make them. MERKLE §2.1 requires the per-leaf re-signing on purpose (*"to prevent ambiguous replay of unchanged rows across operations"*). The fault lies in authorization reading "different bytes" as "different content", not in the signing.

**Why the Authority cannot simply ignore the changing columns.** The comparator the Authority already uses for encryption migrations (`ReprotectionRules.SameRowExceptOperation`) skips the operation column, the storage key and the row revision **without checking them**. That is acceptable for a workload operation bound to a single field command. It is **not sufficient** for share-scoped user writes, and this proposal does not reuse it as-is (§5).

## 3. What changes, and what does not

| Component | Change | Trust-relevant |
|---|---|---|
| Authority `ContentService` (prepare/commit) | Share-scoped branch: switch check, current-snapshot binding, `ContentDeltaRules` classification, share-editor authorization; identity binding for all writes | **Yes** |
| Authority `ContentDeltaRules` (new) | Column-class rules of §5, per entity kind and schema version, fail-closed for unknown kinds and versions | **Yes** |
| Authority `VerifyRecordProjectionResult` | Additive `EditableItems` on partial Read projections (D7) | Additive, private contract |
| Authority configuration + startup validation | Switch and production guard (§11) | **Yes** |
| Contracts | `PrepareContentChangeRequest.CurrentItems` already exists (reprotection); documented for share-scoped user operations | Documentation |
| Business partial-scope write path | `SharedFieldWriteBuilder`: verbatim preservation, edited items only, eligibility R1/R2, unified refusal, preserve schema version | Defense in depth |
| Business projections / UI | `authorized-edit` projection, Sharing and Edit UI | No |
| SpiceDB schema, RowHmac/v1, Merkle v1, vectors, providers, AuthorityDb/BusinessDb schemas | **None** | — |
| Normative docs | Clarifying amendment to MERKLE §4 and CROSS_SYSTEM §3 (D12) | Owner approval |

## 4. Design options (unchanged conclusion)

| Option | Summary | Why not chosen |
|---|---|---|
| **A** (approved in principle) | Authority-verified column-class delta over a digest-bound current snapshot; full re-sign retained | — |
| A2 | As A, but the Authority stores per-leaf "semantic digests" so `CurrentItems` is unnecessary | AuthorityDb migration, a new digest specification and vectors, backfill, a low-entropy digest review; keep as a later PB-09 optimization |
| B | Revision-stable item envelopes (unchanged items keep their signatures) | RowHmac change, Merkle v2, multi-operation staging, replay re-proof; MERKLE §2.1 requires formal review first |
| C | Per-field write capabilities, or a field-patch overlay | Bearer-like revocation window (CONS); a second source of truth for content |
| D | The Authority builds the candidate rows itself | Moves business row construction and key choice into the Authority; closer to a signing-oracle shape (SEC-P11) |
| E | Suggest-changes workflow | Does not let contributors modify; adds a value store |

## 5. Canonical logical identity and comparison rules

### 5.1 Notation

The head accepted by the Authority is `(r, op_h, root_h, kv_h)`: committed content revision, committed operation id, manifest root, and signature key version. The candidate operation is `O`, with candidate revision `r' = r + 1` and current signing key version `kv'`. The request carries organization `T` and record `R`. `E` is the actor's edit set: the field items reachable through contributor/manager shares, read from the Authority's **desired-state edges** in the same locked transaction (`DesiredStateGraph.EditableFieldItemsViaShares`).

Each item has a **logical identity** `(kind, itemId)` — the Merkle item key — and is represented by exactly one current row (digest-bound) and exactly one candidate row.

### 5.2 Column classes

Every signed byte of a canonical row falls into exactly one class. Nothing is "ignored": each class has a rule, checked for every item.

| Class | Meaning | Rule for every item | Rule for share-scoped writes |
|---|---|---|---|
| **F — framing** | Domain, entity kind, schema version, ordered column-name list, organization id in the header | Organization = `T` (both rows); current key version = `kv_h`, candidate key version = `kv'` | Entity kind, schema version and column-name list **equal** between current and candidate (no schema upgrade, no storage-type change) |
| **K — identity** | Columns that say *what* the item is and *where it belongs* | Bound to the manifest key and request (I1–I3, §5.4) | **Equal** between current and candidate |
| **B — operation binding / expected revision** | `content_change_operation_id` column and header `row_revision` | Current = `(op_h, r)`; candidate = `(O, r')` — checked as **exact expected values** | Same |
| **S — storage key** | Header primary key, where it is a per-operation surrogate (value rows, attachment versions) | Candidate surrogate is unique across the candidate and differs from every current surrogate | Same |
| **C — protected content** | The data the item carries | — | **Equal** for every item, except an authorized `Update` (§5.5) |

The header primary key is class **K**, not S, for kinds whose primary key *is* the logical identity (`form_records`, `form_record_groups`).

### 5.3 Per-item-type assignment (current signed schemas, `BusinessSignedSchemas`)

| Item kind / entity kind (schema) | K — identity | C — protected content | B | S | Share-scoped write |
|---|---|---|---|---|---|
| `RecordHeader` / `form_records` v2, v3 | header PK = `form_record_id` = `R` = itemId; `tenant_id` = `T`; `form_definition_id`; v3: `parent_form_record_id`, `form_relationship_definition_id` | `record_status`, `submitted_on`, `created_by_user_id`, `created_form_version_id`; v3: `relationship_sort_order` | op column, `row_revision` | — | Must be **Unchanged** |
| `FieldValue` / `field_string_values`, `field_integer_values`, `field_decimal_values`, `field_date_values`, `field_date_time_values`, `field_boolean_values`, `field_guid_values` (v1) | entity kind; `field_value_item_id` = itemId; `field_definition_id`; `form_record_id` = `R`; `form_record_group_id`; `tenant_id` = `T` | the single typed value column (`string_value`, `integer_value`, …) | op column, `row_revision` | header PK (per-row surrogate) | Unchanged, or `Update` if itemId ∈ `E` (§5.5) |
| `FieldValue` / `field_encrypted_values` (v1) | as above, plus `encrypted_logical_type` | **sealed envelope, compared as one unit**: `encrypted_value_bytes`, `encryption_auth_tag_bytes`, `encryption_nonce_bytes`, `wrapped_data_key_bytes`, `encryption_key_reference`, `encryption_algorithm_name`, `encryption_envelope_version` | op column, `row_revision` | header PK | Unchanged (envelope byte-identical), or `Update` with a wholly new envelope (§5.5) |
| `FieldSelection` / `field_option_values` (v1) | as plaintext values | `field_option_id` | op column, `row_revision` | header PK | Unchanged, or `Update` if D3 is approved (single-select only; §5.5) |
| `RepeatedGroup` / `form_record_groups` (v1) | header PK = `form_record_group_id` = itemId; `form_record_id` = `R`; `tenant_id` = `T`; `group_definition_id`; `parent_form_record_group_id` | `group_sort_order` | op column, `row_revision` | — | Must be **Unchanged** |
| `Attachment` / `file_attachment_versions` (v1) | `file_attachment_id` = itemId; `form_record_id` = `R`; `tenant_id` = `T`; `field_definition_id`; `version_number` | every other column: `storage_object_key`, `storage_provider_name`, `ciphertext_sha256`, `ciphertext_size_bytes`, `plaintext_sha256`, `plaintext_size_bytes`, `wrapped_data_key`, `encryption_nonce_prefix`, `encryption_segment_size_bytes`, `encryption_key_reference`, `encryption_algorithm_name`, `encryption_envelope_version`, `detected_content_type`, `original_file_name`, `scan_verdict`, `scan_signature_name`, `scanner_name`, `scanned_on`, `uploaded_by_user_id`, `uploaded_on` | op column, `row_revision` | header PK (`file_attachment_version_id` copy) | Must be **Unchanged** |
| `AttachmentLifecycle` / `file_attachment_lifecycle_events` | (no business producer exists today) | — | — | — | Must be **byte-equal except B**; any other difference refused |
| Any other entity kind or schema version | — | — | — | — | **Refused** (fail closed until the rule table is extended by a reviewed change) |

The rule table lives in Authority code keyed by `(entity kind, schema version)`. A new signed schema version is not share-writable until its row is added and reviewed. The architecture test that checks the signed-column registry is extended so that every registered business schema has a matching Authority class assignment, and every column has exactly one class.

### 5.4 Identity binding I1–I3 (all user writes, owners included; fixes C5-F1)

| Rule | Check (at prepare, on candidate bytes) |
|---|---|
| I1 | Value and selection rows: `field_value_item_id` = manifest itemId; `form_record_id` = `R`; `tenant_id` = `T`. |
| I2 | Header and group rows: header PK = itemId (header: = `R`, already checked); `form_record_id` = `R` (groups); `tenant_id` = `T`. |
| I3 | Attachment rows: `file_attachment_id` = itemId; `form_record_id` = `R`; `tenant_id` = `T`. |

Owner candidates satisfy I1–I3 by construction (`ISignedContentRow.ItemId`, `TenantId`). The regression suite must confirm it.

### 5.5 Permitted change for share editors (`Update`)

An item is `Update` only if **all** of the following hold. Otherwise the whole operation is refused.

1. Kind is `FieldValue`, or `FieldSelection` if D3 is approved.
2. itemId ∈ `E` (desired-state edges, same transaction).
3. F equal; K equal (including `form_record_group_id` **= NULL**: top-level only in V1); B and S per §5.2.
4. Only class C differs. For plaintext kinds this is the single typed value column. For `field_selection`, `field_option_id`.
5. For `field_encrypted_values`, the **whole** envelope is new:
   * `encryption_nonce_bytes` differs from current;
   * `wrapped_data_key_bytes` differs from current;
   * `encryption_algorithm_name` and `encryption_envelope_version` are in the Authority's allow-list (AES-256-GCM, envelope v1);
   * `encrypted_logical_type` is equal (it is K).

   The Authority cannot decrypt. It enforces structure, and the business enforces AEAD with the AAD binding (tenant, record, field, item, logical type).
6. Entity kind is unchanged. A contributor can never switch an item between plaintext and encrypted storage; the protection barrier still applies on top of this.

At least one item must be `Update`. A share-scoped operation with no update is refused as `NO_CHANGE` (422), so no-op revisions cannot churn the head.

## 6. Preserving verified, unchanged encrypted values and artifacts

**Rule:** in a share-scoped write, every item that is not edited is **copied verbatim** from the verified current snapshot:
* class F, K and C bytes are byte-identical;
* the sealed ciphertext envelope (ciphertext, tag, nonce, wrapped DEK, key reference, algorithm, envelope version) is copied unchanged;
* attachment metadata is copied unchanged, including the storage object key and both digests;
* only the B columns (new operation id, new revision) and the S column (new surrogate key for the new physical staged row) are set by the stager.

The schema version of the copied row is preserved. The partial path does **not** upgrade a `form_records` v2 header to v3, because schema upgrades are owner-path only.

**No decryption outside scope.** The business runtime decrypts only items the actor is authorized to *read*: for display, and for R1 rule inputs (§9, C5-F4). It never decrypts an item outside that scope. An edited item is encrypted fresh; its old ciphertext is not needed. Document blobs are not read, decrypted or rewritten, because the attachment row points to the same immutable ciphertext object.

**Compatibility with full-record signing (MERKLE §2.1):**
1. The Authority still signs **every** candidate leaf under `O`. The preserved ciphertext is embedded in new canonical bytes whose B columns bind `(O, r')`, so the new HMAC binds the old ciphertext to the new operation. A row from an earlier revision cannot be replayed into this one, because its B columns are `(op_h, r)` and its digest is not in the new index.
2. The Merkle leaf for each item binds `(signature, digest, r', O)`. The accepted root therefore commits to the exact preserved bytes at the new revision.
3. AEAD validity is unaffected. The value AAD binds `(tenant, record, field, item, logical type)` — none of which changes — and never the operation id, revision or surrogate key. The document AAD (`AesGcmFileEncryptionService.Aad`) binds envelope version, tenant, record, `file_attachment_id`, `version_number`, segment counter and final flag. All of these are preserved; the per-operation `file_attachment_version_id` copy key is not part of it.
4. Historic manifests still reference the earlier copies. Historic verification (`historic_committed_operation_id`) is unaffected.

**What the Authority can and cannot prove about preserved ciphertext:** it proves that the bytes are exactly those it accepted at revision `r`, and it signs them again at `r'`. It cannot prove that those bytes decrypt — no component outside the business AEAD can. That is unchanged from today.

**Owner writes** keep re-sealing every encrypted value, because the owner path performs legacy-plaintext backfill. Applying verbatim preservation to owner writes would reduce KMS work, but it is a separate decision and is not proposed for C5 (D15 records it as future work).

## 7. How the Authority independently verifies that out-of-scope items are unchanged

All of the following runs inside the Authority, in the prepare transaction that holds the organization row lock. Nothing is taken on trust from the business runtime or BusinessDb.

```text
SharePrepare(actor, req):                         // req.ExpectedContentRevision = r, req.OperationId = O
  require Switch.Enabled                          // §11, else 403 CONTRIBUTOR_WRITES_DISABLED
  existing checks: idempotency(key, digest, actor); req.ExpectedAuthorizationRevision == state.AuthorizationRevision;
                   gate Ready; record resource Active; head not quarantined;
                   (r, root) == (head.r, head.root); key available; O unused
  require !IsRecordOwner(actor, R)                // owners take the unchanged owner path
  E    := EditableFieldItemsViaShares(actor, R)   // desired-state edges read in this transaction
  require E ≠ ∅
  L    := accepted leaf index of head             // AuthorityDb: key → (digest, kind, itemId)
  Cur  := req.CurrentItems
  require keys(Cur) == keys(L), no duplicates     // completeness of the current snapshot
  for k in Cur: require SHA256(bytes) == L[k].digest      // bytes are exactly the accepted bytes (D4)
               decode; require org == T, keyVersion == kv_h, row_revision == r, op column == op_h
  Cand := ParseCandidate(req)                     // existing: org, kv', allowed entity kinds, one header
  require keys(Cand) == keys(L)                   // no Add, no Remove
  for k in L:
      cur, cand := Cur[k], Cand[k]
      require row_revision(cand) == r' and op column(cand) == O
      require I1..I3 on cand                      // §5.4
      rule := RuleTable[(entityKind, schemaVersion)] else refuse
      require F(cur) == F(cand) and K(cur) == K(cand)
      require S(cand) unique in Cand and ∉ { S(x) : x ∈ Cur }
      if C(cur) == C(cand):            m[k] := Unchanged
      elif PermittedUpdate(k, cur, cand, E): m[k] := Update      // §5.5
      else refuse SHARE_MUTATION_OUT_OF_SCOPE
  require ∃ k: m[k] == Update
  ProtectionBarrier(Cand)                         // existing
  sign every candidate leaf under O; build manifest; persist operation + leaves with m[k]   // existing
```

**Commit** (unchanged code path plus the switch check). Under the organization row lock:
* it requires the switch still enabled;
* it requires the head still at `r` (CAS), so the snapshot classified at prepare is still the accepted state;
* it re-authorizes every stored `Update` against `E` recomputed from **current** desired edges, so a revocation since prepare aborts the operation;
* it requires the read-back staged bytes to have exactly the candidate digests that were classified, so nothing classified at prepare can be swapped before acceptance.

The classification is therefore bound three ways: to the AuthorityDb digests of the current state, to the candidate digests it signed, and to the head it CAS-advances.

## 8. Why identity substitution is always rejected

| Attempt (crafted below the API, e.g. by a modified runtime) | Rejected by |
|---|---|
| Write under a new itemId | Key not in `L` → `Add` → refused (§7: `keys(Cand) == keys(L)`) |
| Keep the key, change `field_value_item_id` column | I1 (column ≠ itemId) and K inequality |
| Swap whole rows between two items (row of A under key B) | I1 (`field_value_item_id` ≠ B) |
| Change `field_definition_id` (write into another field through a shared item) | K inequality |
| Move a value into or out of a repeating entry (`form_record_group_id`) | K inequality; V1 also requires NULL for `Update` |
| Change the item kind (FieldValue ↔ FieldSelection) under the same itemId | Different Merkle key → `Add` + `Remove` → refused |
| Change entity kind (e.g. string ↔ encrypted, string ↔ integer) | F inequality |
| Change `encrypted_logical_type` | K inequality |
| Use another record's item (record identity) | Key not in this record's `L`; I1–I3 `form_record_id` ≠ `R`; the header item must equal `R` (existing) |
| Re-parent the record, change its form or relationship | Header K inequality; header must be Unchanged |
| Use another tenant's row, or set `tenant_id` / header organization to another tenant | `ParseCandidate` org check (existing); I1–I3 `tenant_id` = `T`; key not in `L`; the signing key is per organization |
| Change a group's definition, parent or order | Group K/C inequality; groups must be Unchanged |
| Change an attachment (object key, digests, scan verdict, version) | Attachment C inequality; attachments must be Unchanged |
| Present an older valid row as "current" | Digest ≠ `L[k].digest` (current binding) |
| Present the accepted row with a forged B column | Digest ≠ accepted digest (current); candidate B must equal `(O, r')` |
| Duplicate a key | `ParseCandidate` duplicate check (existing); current-set duplicate check |

Each row is a mandatory negative test (§12).

## 9. Findings C5-F1 … C5-F5 — resolution

| ID | Finding | Resolution in this design | Where enforced | Residual |
|---|---|---|---|---|
| C5-F1 | `Update` was not bound to the item's identity | I1–I3 for **all** user writes, plus K-class equality for share editors | Authority prepare | None identified; reviewer to confirm the class table (U2) |
| C5-F2 | The partial-scope path decrypted every out-of-scope encrypted value on each attempt | `SharedFieldWriteBuilder` copies unedited rows verbatim and never calls `Decrypt` outside the read scope; edited items are encrypted fresh; a decryption-spy test asserts decrypted items ⊆ readable items (C5-SEC-EP-03). Until implemented, the current path stays as it is; its attempts are refused, but the decryption still happens (documented) | Business | Readable inputs are decrypted for display/R1: these are authorized reads |
| C5-F3 | Field-key existence oracle (422 for unknown keys vs 403 for unshared keys) | Any key outside the actor's editable projection — unknown, unshared, read-only, ineligible — returns one response: `403 FIELD_NOT_EDITABLE`, a constant message, `field_keys` echoing only the supplied key. Ordering: the scope check runs before any per-field validation | Business | Timing differences between paths are not normalized (U6) |
| C5-F4 | Partial-scope writes skipped rule, requiredness and dependent-option validation | Eligibility (D6). **R1 inputs readable:** every visibility/required/read-only condition and option filter that affects the edited field reads only fields the actor can read; it is evaluated over the actor's readable verified values; a field hidden by such a rule cannot be changed (`FIELD_HIDDEN_BY_RULE` is safe to return, because the actor can see every input). **R2 effects contained:** if the edited field is an input of any rule or filter, every affected field must be in `E`, and the evaluated result must not require clearing, adding or invalidating any value; otherwise refused with the generic "This change affects fields you cannot edit." Read-only enforcement as today. Type, length and active-option validation as today. Requiredness cannot be broken, because a contributor cannot clear or remove | Business (static from the effective edit layout + readable values) | The Authority does not evaluate form rules, for owners or contributors (U4) |
| C5-F5 | The UI promised edit; share coverage silently ends on owner re-entry | Role copy reflects the switch state (when disabled: "Contributor — view now; editing is not enabled"); per-field **View only** badges with reasons; an explanation that a share follows the current value of a field and that clearing or re-entering it by the owner ends coverage | Frontend | Item-scoped share semantics remain (future FR-C5-01) |

## 10. Stale-record protection, revocation timing, authorization consistency, concurrency

**Linearization point.** Content prepare, content commit, verified reads and **every security command** (grant, revoke, team membership, ownership transfer) start by taking `SELECT … FOR UPDATE` on the organization's `organization_authorization_states` row (`AuthorityStore.LockOrganizationAsync`). For a commit, the authorization check (desired edges), the head CAS and the head advance all happen **inside one transaction holding that lock**. So for any revocation `V` and contributor commit `W`:
* if `V` commits first, `W` sees the removed edge when it re-authorizes, and aborts (`PERMISSION_REVOKED_BEFORE_COMMIT`); also, until SpiceDB catches up, the gate is `AwaitingProjection` and `W` fails closed regardless;
* if `W` commits first, the edit was authorized at the instant it became the accepted state — a legitimate edit made before the revocation.

There is no interval in which the check passes on stale state and the commit applies afterwards. That is the check-then-commit gap the owner asked to exclude.

**Authorization revision consistency:**
* Prepare requires `ExpectedAuthorizationRevision == AuthorizationRevision`. The business's verified view must be current, or the write is refused (409), and the editor reloads.
* Commit does **not** require the revision to be unchanged since prepare. Requiring it would abort writes on any unrelated security change in the organization. Instead commit re-authorizes exactly against current desired edges. The stricter alternative is offered to the reviewer (U5).
* SpiceDB (used for read/edit *flags*) and desired edges (used for mutation *authorization*) can only disagree while the projection lags. During lag the gate is closed and every protected operation fails. `VerifyRecordProjection` re-reads the state after its checks and aborts if the revision moved.

**Stale-record protection:**
* API `If-Match: "<revision>-<operationId>"` (C4);
* prepare CAS on `(revision, root)`;
* commit CAS on revision;
* candidate revision `r + 1`;
* operations expire after 10 minutes;
* `O` is single-use;
* the idempotency key is bound to request digest + actor.

Several prepared operations may coexist; the first to commit wins and the others abort with 409. **No automatic merge** in V1 (D13). Editor flags are advisory: the Authority decides at commit.

**Identity and session revocation:** the delegated assertion is bound on every call (prepare and commit). An identity binding removed between them fails `ActorResolver`. A commit must be made by the preparing actor.

**Switch toggled between prepare and commit:** commit refuses (§11).

## 11. Rollout switch — disabled by default, fail-closed

| Aspect | Specification |
|---|---|
| Name | `Content:ShareContributorWritesEnabled` (Authority deployment configuration). Not an organization, form or user setting, and not settable through any API (consistent with CLAUDE.md §4P "resolved by trusted deployment configuration"). |
| Default | `false` in `appsettings.json`, `appsettings.Development.json`, every compose profile and every production-designated configuration. The dev profile is **not** enabled by default. Integration and security tests enable it explicitly in their own overlay (`compose.security-test.yaml`), and also prove the disabled behaviour (C5-SEC-SW-*). |
| Fail-closed parsing | Bound through validated options. Absent, empty, unparsable or non-boolean values count as **disabled**; only the literal `true` enables. |
| Production guard | An Authority started with a production-designated environment refuses to start with the switch `true` (startup validation error), until a reviewed code change removes the guard after the §16 review. A configuration edit alone cannot enable contributor writes in production. |
| Where enforced | **Authority, authoritatively:** at prepare (before any classification or signing) and again at commit (before re-authorization), both under the organization lock. **Business, advisory:** a mirrored setting hides the UI and short-circuits before calling the Authority. Writes need **both** enabled. If they disagree, the result is refusal (business on, Authority off → clear error on save) or no UI (business off). |
| Behaviour when disabled | Non-owner content mutations are refused exactly as today (`403 CONTRIBUTOR_WRITES_DISABLED`, mapped to the generic user message). Owner writes, reads and shares are unaffected. |
| Rollback | Setting it to `false` stops new contributor writes immediately (in-flight prepared operations fail at commit). Contributor revisions already committed remain ordinary, valid revisions. |

## 12. Negative security test catalogue

Layers: **U** unit (Authority/business); **I** Docker integration (real PostgreSQL, SpiceDB, Vault Transit, Authority); **H** hostile BusinessDb (direct SQL); **E** Playwright.

Every negative test asserts all four of the following:
* no new accepted head (revision and root unchanged at the Authority);
* no released plaintext;
* no signature returned for refused prepares;
* the expected error code — while the user-visible body leaks no unshared key, label, value or count.

| ID | Category | Scenario | Expected | Layer |
|---|---|---|---|---|
| SUB-01 | Substitution | Candidate under a new itemId for a shared field | Refused (Add) | U |
| SUB-02 | Substitution | Same key, `field_value_item_id` column changed | Refused (I1) | U |
| SUB-03 | Substitution | Rows of shared item A and unshared item B swapped | Refused (I1/K) | U |
| SUB-04 | Substitution | Shared item's row carries an unshared `field_definition_id` | Refused (K) | U |
| SUB-05 | Substitution | Value moved into a repeating entry (`form_record_group_id`) | Refused (K) | U |
| SUB-06 | Substitution | FieldValue ↔ FieldSelection under the same itemId | Refused (Add + Remove) | U |
| SUB-07 | Substitution | Entity kind changed (string → encrypted, string → integer) | Refused (F) | U |
| SUB-08 | Substitution | `encrypted_logical_type` changed | Refused (K) | U |
| SUB-09 | Substitution | Header re-parented / form changed / status changed | Refused (header) | U |
| SUB-10 | Substitution | Group order, definition or parent changed | Refused | U |
| SUB-11 | Substitution | Attachment object key, digest or scan verdict changed | Refused | U |
| SUB-12 | Substitution | Candidate surrogate key reused (duplicate, or equal to a current surrogate) | Refused (S) | U |
| SUB-13 | Substitution | Candidate B column ≠ `(O, r')` | Refused (B) | U |
| TAM-01 | Tampering | `CurrentItems` value altered in BusinessDb between verified read and prepare | 409 `CURRENT_SNAPSHOT_MISMATCH`, incident journaled, nothing signed | H |
| TAM-02 | Tampering | `CurrentItems` missing one item / extra item / duplicate | 422 / 409 | U |
| TAM-03 | Tampering | Staged candidate row altered before commit | 409 `CANDIDATE_MISMATCH`, head unchanged | H |
| TAM-04 | Tampering | Out-of-scope encrypted envelope altered in the candidate (one byte) | Refused (C) | U |
| TAM-05 | Tampering | Forged contributor share row / team membership inserted in BusinessDb | No edit right (SEC-P02/P03) | H |
| TAM-06 | Tampering | Unknown entity kind or schema version in the candidate | Refused (rule table) | U |
| RPL-01 | Replay | An older valid signed row of an out-of-scope item presented as current | `CURRENT_SNAPSHOT_MISMATCH` (SEC-P05) | H |
| RPL-02 | Replay | Prepare retried: same key, same payload | Same prepared operation | I |
| RPL-03 | Replay | Same key, different payload or different actor | 409 `IDEMPOTENCY_CONFLICT` | I |
| RPL-04 | Replay | Operation id reused after commit or abort | 409 | I |
| RPL-05 | Replay | Commit replayed after success | Idempotent success, no second revision | I |
| RPL-06 | Replay | An earlier committed candidate re-staged and committed against a newer head | 409 (CAS) | I |
| HID-01 | Hidden field | Write to an unshared key; write to an unknown key | Identical `403 FIELD_NOT_EDITABLE` | U, I |
| HID-02 | Hidden field | Write to a field hidden by a rule over readable inputs | `FIELD_HIDDEN_BY_RULE` | I |
| HID-03 | Hidden field | Write to a field whose rule reads an unshared field (R1) | Generic refusal; response independent of the unshared value | I |
| HID-04 | Hidden field | Change a dependent-dropdown parent whose child is unshared (R2) | Generic refusal; child unchanged | I |
| HID-05 | Hidden field | Read-only field in scope | `FIELD_READ_ONLY` | I |
| HID-06 | Hidden field | Restricted field (D5) | Refused | I |
| HID-07 | Hidden field | Clear a value; fill an empty field; change a multi-selection; change an entry | Refused (V1 scope) | I |
| HID-08 | Hidden field | Child record of a shared parent | No edit | I |
| XT-01 | Cross-tenant | Candidate header or `tenant_id` of another organization | Refused | U |
| XT-02 | Cross-tenant | Share recipient attempts a record of another tenant with the same itemIds | Not found / refused | I |
| XT-03 | Cross-record | Item of record A submitted in record B's candidate | Refused (key ∉ `L`, I1) | U |
| RV-01 | Revocation | Share revoked between verified read and prepare | 409 stale authorization revision | I |
| RV-02 | Revocation | Share revoked between prepare and commit | Commit aborted `PERMISSION_REVOKED_BEFORE_COMMIT` | I |
| RV-03 | Revocation | Recipient removed from the recipient team between prepare and commit | Aborted | I |
| RV-04 | Revocation | Manager's upstream share revoked; a delegated contributor writes | Refused | I |
| RV-05 | Revocation | 100 concurrent contributor commits racing one revocation | Zero commits accepted at or after the revocation's revision (linearization, §10) | I |
| RV-06 | Revocation | Projection lag after a revocation | Commits fail closed (`AwaitingProjection`) until caught up | I |
| RV-07 | Revocation | Identity binding disabled between prepare and commit | Commit refused | I |
| CW-01 | Concurrent | Owner and contributor commit concurrently | Exactly one accepted; other 409; no lost update | I |
| CW-02 | Concurrent | Two contributors edit disjoint fields concurrently | One accepted; other 409; reload shows the first | I |
| CW-03 | Concurrent | Contributor write racing a reprotection operation | One accepted; other 409 / `FIELD_PROTECTION_CHANGED` | I |
| CW-04 | Concurrent | Stale If-Match | 409/412 | I |
| EP-01 | Preservation | Contributor edits one plaintext field | Every other item's F/K/C bytes equal to before; ciphertext of encrypted items byte-identical; attachment object keys unchanged | I |
| EP-02 | Preservation | Contributor edits one encrypted field | Only that envelope changes; new nonce and wrapped DEK; value decrypts under AAD | I |
| EP-03 | Preservation | Decryption spy during a contributor write | Decrypted item set ⊆ actor-readable items | I |
| EP-04 | Preservation | Verified read and historic document verification after a contributor commit | Pass; earlier manifests still verify | I |
| SW-01 | Switch | Switch absent / `false` / malformed | Contributor writes refused, owner writes unaffected | I |
| SW-02 | Switch | Switch turned off between prepare and commit | Commit refused | I |
| SW-03 | Switch | Production-designated environment with switch `true` | Authority startup fails | U |
| REG-01 | Regression | Owner writes with I1–I3 enabled; full existing suites; SEC-P01…P12 | Unchanged results | U, I |

## 13. V1 scope and future functional requirements

**V1 (D2, preserved):** a contributor or manager may change the value of an **existing, top-level** field item that is explicitly shared with them, is eligible (§9 R1/R2, not read-only, not Restricted, not multi-select, not a File field), and is not in a repeating entry. They may not fill empty fields, clear values, add/remove/change multi-selections, add/remove/reorder/edit entries, replace or add documents, edit child records, or change record status.

**Future functional requirements — recorded, not approved, not designed here:**

| ID | Requirement | Why it needs separate design |
|---|---|---|
| FR-C5-01 | Fill empty shared fields | Shares bind to existing item ids; needs a field-slot share scope (record × field definition), i.e. a new Authority resource type and `Add` authorization |
| FR-C5-02 | Edit multi-selections | Needs `Add`/`Remove` of selection items within a field-scoped grant |
| FR-C5-03 | Clear shared values | `Remove` authorization and requiredness interaction |
| FR-C5-04 | Edit values inside repeating entries | Field-shareability of entries (C-17) |
| FR-C5-05 | Contributor document upload/replace | `document_item#replace`; attachment `Add`/`Update` rules |
| FR-C5-06 | Team recipients in the share UI | Authority already supports team shares |
| FR-C5-07 | Automatic rebase of disjoint concurrent edits | Merge semantics against the head CAS |

## 14. Collaboration UX (V1)

Built inside the Record page's existing **Sharing and access** and **Activity and history** tabs and the C4 editor. There is no redesign.

| Task | V1 experience |
|---|---|
| Share with a user | Existing dialog: Person, Role, fields. Role copy: Reader "View the selected fields"; Contributor "View the selected fields and change their values" (when the switch is off: "View now; editing is not enabled"); Manager "Contributor, and can share these fields". |
| Read vs edit | The role decides: Reader, Contributor and Manager map 1:1 to Authority roles. |
| Select permitted fields | Only fields with a value are listed ("Empty fields can't be shared yet"). For Contributor/Manager, ineligible fields show a **View only** badge with a reason: Read-only, Depends on fields not shared, Restricted, Multi-select. |
| View existing access | Existing table plus **Can change values** and **Shared by / on**. |
| Revoke | Existing dialog; adds "Changes they already saved stay in the record history." |
| Collaboration history | A compact **Sharing history** on the Sharing tab: grants, revocations and edits by people with shared access (who, when, field labels the viewer may see; never values), from the existing audit projection under existing audit permissions. |
| Edit a shared record | **Edit shared fields** appears when the projection has at least one editable field. The partial editor shows editable inputs, read-only shared values with a View only badge, nothing else, and the banner "You can change N shared fields. Other parts of this record are not shown." C4 unsaved-change and 409 handling. Generic errors. |
| My Records | No change (no list-level edit for contributors; the scalability item stays separate). |

PB-14 rules apply (keyboard, 320/768/1440 px, 200/400 % zoom, axe, Playwright). The manual NVDA/VoiceOver review stays pending.

## 15. Performance and scalability

N = items in the record (≤ 500); F = field items; E_enc = encrypted items.

| Cost per write | Owner today | Contributor (A) |
|---|---|---|
| Authority round trips | 4 | 4 |
| HMAC verify (verified read + commit proof) | 2N | 2N |
| HMAC sign | N | N |
| Extra Authority CPU | — | N SHA-256 + 2N decodes + column-class comparisons |
| Prepare payload | 1× snapshot | 2× snapshot (≤ ≈1 MB at the ceiling) |
| Field-encryption operations | E_enc decrypt + E_enc encrypt | edited items only (+ decrypts of readable rule inputs) |
| SpiceDB | 1–2 checks | discover + F bulk edit checks |

No additional round trips, MAC operations equal to owner writes, and whole-record verification unchanged. My Records is unaffected.

**Benchmark plan** (sectest stack with Vault Transit; Managed HSM in staging later; reported against PB-09 write p95 < 2 s):
1. 10/100/250/500 items, 0 % and 50 % encrypted, owner vs contributor single-field edit: p50/p95/p99 per stage, MAC calls, payload bytes, Authority classification CPU.
2. Contention between 1 owner and 1–10 contributors.
3. The revocation race in RV-05.
4. Editor load with and without D7.
5. Owner latency before and after I1–I3.

## 16. Migration, compatibility and independent review

* **No data or schema migration.** Formats are unchanged, so all existing records, historic manifests and backups verify as before. Records whose current header is `form_records` v2 are share-writable with the header preserved at v2.
* **Rollout order:** first the Authority (switch `false` → identical behaviour; I1–I3 active for owners), then the business, then the UI. Rollback: switch off or revert.
* **Tests whose meaning changes:**
  * `RulesTests` — in-scope edit now succeeds when the switch is on; read-only and out-of-scope edits remain refused;
  * `PhaseC2RulesAndOptionsTests.ASharedContributorCannotChange…` — remains a refusal test;
  * `PhaseC3RepeatingGroupsTests.ASharedContributorCannotChangeEntries` — remains;
  * out-of-scope error code becomes `FIELD_NOT_EDITABLE`.
* **Documents updated on implementation:**
  * MERKLE §4 and CROSS_SYSTEM §3 clarifications (D12);
  * private OpenAPI/C# notes;
  * a new `SECURITY_GAP_ASSESSMENT.md` production-review item;
  * decisions C-34 onward;
  * `docs/PHASE_C5_STATUS.md`;
  * spec manifest.
* **Independent specialist security review:**
  * **Mandatory and non-waivable at the production Level 2 gate.** This changes how the Authority authorizes content mutations — the SEC-P11 and hostile-DBA surface. It joins C-12 and C-28 as an open production review.
  * **Before implementation: recommended.** Required only if the owner does not accept development risk as for C-12/C-28, or if Option B is chosen.
  * The reviewer should receive §5–§8, §10, §11 and the unresolved issues in §19.

## 17. Implementation milestones (only after authorization)

| Milestone | Content | Exit criteria |
|---|---|---|
| M0 | Owner decisions D1–D15; C-34+ recorded; optional pre-implementation review | Approvals recorded |
| M1 | Authority: switch + production guard, I1–I3, current-snapshot binding, `ContentDeltaRules` with the per-kind table, share branch, D7 | SUB/TAM-02/04/06/XT-01/03/SW-03 unit tests; existing Authority suites green |
| M2 | Business: `SharedFieldWriteBuilder` (verbatim preservation, schema version preserved), eligibility R1/R2, unified refusal, `authorized-edit` projection, audit `editBasis=share` | Contract/integration tests; EP-03 spy |
| M3 | Hostile-DB, replay, revocation and concurrency tests; SEC-P01…P12 on the implementation commit | All §12 I/H tests green; SEC-P 13/13 |
| M4 | UI (§14) | Playwright + axe at all PB-14 sizes; existing E2E green |
| M5 | Benchmarks; normative clarifications; status doc; gap-register entry | Numbers recorded; owner functional acceptance |

## 18. My Records and other open items

No effect on discovery or list cost. The My Records O(N²/250) concern and the C-28 review stay separate and unchanged.

## 19. Unresolved issues requiring an independent security reviewer

| ID | Issue | Proposed position |
|---|---|---|
| U1 | Is digest equality against AuthorityDb a sufficient proof that `CurrentItems` are the accepted bytes, or must each current item's HMAC also be re-verified (+N MAC)? | Digest binding suffices (AuthorityDb is the trust anchor and holds the digests); confirm (D4) |
| U2 | Completeness and correctness of the column-class table (§5.3), especially which header and attachment columns are K vs C, and the fail-closed rule for new schema versions | As §5.3; confirm |
| U3 | Preserving ciphertext across revisions: confirm AEAD/AAD analysis (§6), the envelope allow-list, and the nonce/DEK-differ checks on `Update` | Confirm |
| U4 | Form-rule enforcement (R1/R2) is business-side only; the Authority does not evaluate rules for any writer | Acceptable (business semantics, not an authorization boundary); confirm |
| U5 | Commit re-authorizes against current edges rather than requiring an unchanged authorization revision since prepare | Re-authorization under the lock is exact; the stricter variant is available |
| U6 | Timing and error-shape side channels between refusal reasons (unknown vs unshared vs ineligible keys) | Error shape unified; timing not normalized; assess |
| U7 | Legacy data: current rows whose B columns do not equal `(op_h, r)` (for example Stage A rows) would make share-scoped writes fail closed | Fail closed is correct; verify existing data at implementation |
| U8 | Interaction with manager delegation chains and team shares at commit-time re-authorization | Uses the same desired-state graph as share commands; confirm (relates to C-12) |
| U9 | `CURRENT_SNAPSHOT_MISMATCH` journals an incident but does not quarantine (quarantine stays with verified reads) | Avoids business-induced quarantine DoS; confirm |
| U10 | Single-select `FieldSelection` updates (D3) | Cardinality-preserving and identity-bound; confirm |

## 20. Decisions D1–D15 (full wording)

Each decision below needs **explicit owner approval before implementation**. "Reviewer" marks decisions where an independent security reviewer's confirmation is also requested (mandatory before production Level 2).

* **D1 — Approach.** *Field-scoped contributor writes are implemented with Option A: the Authority binds the share writer's current snapshot to its own accepted leaf digests, classifies every candidate item with the column-class rules of §5, permits only identity-preserving updates of explicitly shared items, and keeps the v1 full re-signing policy, RowHmac/v1 and Merkle v1 unchanged. Options A2, B, C, D and E are not pursued in C5.*
  * Recommendation: approve (already approved in principle).
  * Risk: Authority authorization-rule change (production review). Reviewer.
* **D2 — V1 scope.** *In V1 a contributor or manager may only change the value of an existing, top-level field item explicitly shared with them that passes the eligibility rules. Filling empty fields, clearing values, multi-select changes, repeating-entry changes, document changes, child-record changes and record-status changes are excluded; FR-C5-01…07 are recorded as future requirements and are not approved.*
  * Recommendation: approve.
  * Risk: low (narrowest scope); usability limits are explained in the UI.
* **D3 — Single-select dropdowns.** *A share writer may change the selected option of an existing single-select field item (FieldSelection `Update` with the same item id and field); multi-select changes remain owner-only.*
  * Recommendation: approve.
  * Risk: low; relies on business single-select cardinality enforcement, as owner writes do. Reviewer (U10).
* **D4 — Current-snapshot proof.** *At prepare, the Authority accepts the share writer's current snapshot only if its key set equals the accepted leaf index and every item's SHA-256 equals the accepted digest held in AuthorityDb, with B columns equal to the accepted operation and revision; HMAC re-verification of current items is not required.*
  * Recommendation: approve, subject to reviewer confirmation.
  * Risk: if the reviewer requires HMAC re-verification, cost rises by N MAC calls per write. Reviewer (U1).
* **D5 — Restricted fields.** *Fields classified Restricted are not editable by share writers until PB-08 fresh step-up assurance is implemented and approved.*
  * Recommendation: approve.
  * Risk: none (fail closed).
* **D6 — Rule eligibility.** *A shared field is editable by a share writer only if every rule and option filter affecting it reads only fields the writer can read (R1), and every rule or filter it drives affects only fields the writer can edit without requiring any value to be cleared, added or invalidated (R2); otherwise the save is refused with one generic message.*
  * Recommendation: approve.
  * Risk: some fields become view-only for contributors (explained in the UI); rules stay business-enforced. Reviewer (U4).
* **D7 — Editor flags.** *The private `VerifyRecordProjection` result gains an additive list of editable items for partial Read projections, so the editor obtains read and edit flags from one verification.*
  * Recommendation: approve.
  * Risk: additive private contract change; flags are advisory only.
* **D8 — Identity binding.** *Identity binding I1–I3 (item id, record and tenant columns bound to the manifest key and request) is enforced by the Authority for all user content writes, owners included.*
  * Recommendation: approve.
  * Risk: a latent owner-path defect would surface as a refusal; mitigated by the regression run. Reviewer (U2).
* **D9 — Unified refusal.** *Any value key outside the writer's editable projection — unknown, unshared, read-only or ineligible — is refused with the same `403 FIELD_NOT_EDITABLE` response, checked before any per-field validation.*
  * Recommendation: approve.
  * Risk: changes the error code clients see; timing not normalized (U6).
* **D10 — Rollout switch.** *Contributor writes are controlled by the Authority deployment setting `Content:ShareContributorWritesEnabled`, which defaults to false in every configuration including development and production-designated ones, treats absent or invalid values as false, is enforced by the Authority at prepare and at commit, and cannot be enabled in a production-designated Authority until a reviewed code change removes the startup guard after the independent review.*
  * Recommendation: approve.
  * Risk: none (fail closed); test overlays must enable it explicitly.
* **D11 — Recipients.** *The V1 share dialog offers individual people only; team recipients remain future requirement FR-C5-06.*
  * Recommendation: approve.
  * Risk: none.
* **D12 — Normative clarification.** *On implementation, MERKLE_RECORD_MANIFEST_PROTOCOL §4 and CROSS_SYSTEM_CONSISTENCY_PROTOCOL §3 are amended to state that share-scoped user operations carry the current snapshot and are classified by the column-class rules, without changing the v1 signing or manifest policy.*
  * Recommendation: approve.
  * Risk: normative change (documented, reviewable).
* **D13 — Concurrency.** *Contributor writes use record-level content-revision CAS with no automatic merging; a losing writer receives a conflict and must reload.*
  * Recommendation: approve.
  * Risk: occasional 409s under contention (FR-C5-07 later).
* **D14 — Column-class model (new).** *The Authority's classification uses the five column classes and the per-entity-kind, per-schema-version assignment of §5.3; an entity kind or schema version without an assignment is not share-writable; every registered business signed schema must have an assignment, enforced by an architecture test.*
  * Recommendation: approve.
  * Risk: an incorrect assignment is the main residual risk. Reviewer (U2).
* **D15 — Preservation scope (new).** *In share-scoped writes, every unedited item is preserved byte-for-byte (including sealed ciphertext and document artifacts) and nothing outside the writer's read scope is decrypted; owner writes keep their current re-sealing behaviour, and applying preservation to owner writes is future work.*
  * Recommendation: approve.
  * Risk: legacy-plaintext fields with a pending protection migration block share writes until migrated (fail closed). Reviewer (U3).

## 21. Preserved, unchanged open items

These remain exactly as open as before:
* G06 (protected read and search completeness);
* G19 (field-protection migration completeness);
* the PB-14 manual accessibility reviews;
* the C-12 and C-28 Authority production reviews;
* the My Records scalability concern;
* the Form Designer usability debt;
* PB-08 step-up;
* every production Level 2 gate (independent cryptographic attestation, Managed HSM, Confidential Ledger, Azure staging, DR rehearsal).

C5-F1…C5-F5 are resolved only by implementation of this design. Until then they remain open (C5-F2 occurs on every refused contributor attempt today).
