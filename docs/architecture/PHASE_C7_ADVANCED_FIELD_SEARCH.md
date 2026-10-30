# Phase C7 — Advanced Field Search: architecture

- **Status (2026-10-24):**
  * Owner decisions D1–D7 approved (C-56, §1); OD-1..OD-8 approved (C-60, §1A).
  * **M0 implemented** (C-57, §6): approved UI, versioned contracts, request validation, typed errors, server-side gate. Field-value execution is disabled. Owner visual acceptance pending.
  * **G22 hotfix merged** to `main` (C-59/C-61) and into this branch.
  * **M1 implemented — AC-5 Authority catalogue** (C-62, §6A): user-scoped catalogue, privileged migration catalogue, Authority-held snapshots, batch verification, audit stream. Field-value search is **still disabled**.
  * AC-6 (§4) is designed, **not implemented**; M2–M4 are not authorized.
- **Branch:** `phase-c7/advanced-field-search` (from `main` 4c00ce7). Not merged.
- **Binding sources:** `CLAUDE.md` §§4C.6, 4H, 4L.5, 4M–4R, 4T.1, 4T.4 and Phase 4; `docs/decisions/ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md` (+ Amendment A1); `security_hardening/SEARCH_CAPABILITY_MATRIX.csv`; `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md` (ENC-SR-01..18); `SECURITY_GAP_ASSESSMENT.md` G06, G19, G21; `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md`; `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`; `docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md`; `docs/PHASE_C6_STATUS.md` §16.5.
- **Not claimed:**
  * G06, G19 and G21 stay **OPEN**.
  * No independent security review has taken place.
  * Nothing here is production Level 2 evidence.

## 0. Checkpoint summary (C-55)

The Authority enumerates the caller's complete discoverable set at one authorization revision (`DiscoverAsync`, C-28). It also verifies one record's complete leaf set (`VerifyRecordProjection`). It does not provide:

* form-scoped trusted enumeration;
* accepted content heads at a coherent snapshot;
* Authority-held search approval.

`field_definitions.is_searchable` and `field_classification` are unsigned business rows, and `is_searchable` is always written `false`. No BusinessDb-only substitute was built.

## 1. Approved decisions (C-56, recorded as approved by the owner)

| # | Decision | Approved as |
|---|---|---|
| **D1** | **Approve AC-5.** Build an independently verifiable Authority-owned record catalogue. **Option B is rejected** (scanning all accessible records with today's contracts; does not resolve G19). | AC-5 must support: (1) trusted records grouped by organization and form; (2) accepted record versions/content heads; (3) authorization revision information; (4) independently verifiable completeness; (5) authorized search enumeration; (6) snapshot consistency and invalidation; (7) a privileged completeness-verification mechanism suitable for encryption migration. **Modification:** a per-user discoverable list is not, by itself, sufficient for G19. The independently maintained registry must also let a **separately authorized administrative/migration operation** establish the **entire expected organizational record population**, including records inaccessible to any ordinary user. Privileged registry contents are never exposed through user search APIs. User-scoped search enumeration and organization-scoped migration verification have **distinct authorization boundaries**. G19 is not resolved merely because AC-5 allows searching by form. |
| **D2** | **Approve AC-6.** Searchability approval is independently maintained and enforced by the Authorization Authority. An authorized organization owner or security administrator may approve search of permitted fields, subject to explicit policy. | The Authority rejects approval when: the field is Confidential or Restricted; its data is application-encrypted; the organization floor prohibits plaintext indexing; the actor lacks permission; the applicable published form-version policy is not trustworthy; or previously encrypted data would be silently exposed. Approvals bind to stable field identity, the correct published form version, field type, classification and supported operators. G21 is considered explicitly: tampering with unsigned form or dropdown metadata must not substitute a different field or altered policy under an existing approval. Revocation and invalidation on policy changes are defined. Approvals never make encrypted historical values searchable. A client Searchable checkbox is never proof of authorization. |
| **D3** | **Fail the entire search.** | If any record in the eligible verified scope cannot be authenticated or its required contents verified, the whole search fails. This covers missing records, invalid signatures/manifests, stale accepted versions, broken relationships required by the predicate, inconsistent authorization or snapshot state, Authority unavailability and exceeded limits. The response is the approved typed failure, never a partial result and never "zero matches". |
| **D4** | **Provisional limits, as initial engineering limits only.** | 500 eligible records; 64 MiB verified data; 15-second execution budget; five AND conditions. All are configurable and enforced server-side. They must be measured (p50/p95/p99, query cost, Authority calls, verification time, failure behaviour). They are not production limits until benchmarks and review support them. Exceeding one returns `SEARCH_VERIFICATION_LIMIT` with no partial results. |
| **D5** | **Searchable-field visibility, approved with modification.** | **Not** a blanket "anyone with form access sees every searchable field". A field is shown as a predicate only when authoritative policy lets the caller discover **and** query it. That requires organization access, form access, field-level discovery/query authorization, approved classification and searchability, and the relevant role permissions. Designers and organization administrators get broader field-configuration access only when explicitly authorized. Record ownership does not override organizational field policy or classification. Capability responses never expose hidden field names, existence, values or metadata. The server enforces this independently of the UI. |
| **D6** | **V1 scope.** | Approved current-version scalar typed field values; operators from server capabilities; AND only. Excluded: repeating-group items, related-form/child-record predicates, document-content search, sorting by arbitrary field values, facets and aggregates, export from advanced search, semantic/vector search, regex, Confidential/Restricted values, and blind indexes without separate approval. Existing metadata sorting and My Records features remain. |
| **D7** | **Authorize M0.** | Approved UI, versioned API and capability contracts, request validation and typed errors, execution clearly disabled until AC-5 and AC-6 are ready, focused tests and documentation. No unsafe temporary execution, and no field-value search by querying Business PostgreSQL. |

## 1A. Approved open decisions (C-60, recorded as approved by the owner)

| # | Decision | Approved as |
|---|---|---|
| **OD-1** | Migration workload identity | **A** — a separate workload identity `field-protection-verifier` with independently controlled credentials and narrowly scoped permissions. The normal business API can never invoke privileged organization-wide migration enumeration. |
| **OD-2** | Search approval administrators | **A for V1** — only principals explicitly granted `administer_security` may authorize plaintext field-value search. An organization owner with ordinary `manage_configuration` cannot approve searchable fields through ownership. Organization and tenant boundaries are kept; global application administration is not unlimited access to every organization's data. Two-person approval may come with a later policy tier. |
| **OD-3** | Published form integrity | **A** — the Authority records a trustworthy published form-version number and a deterministic, versioned canonical layout digest. Approvals bind to independently authenticated field identities, types, classifications, versions and applicable option-catalogue metadata. An unsigned BusinessDb copy is never the source of an approved definition. Historical unsigned definitions are not backfilled or approved without independent verification. G21 remains open until all relevant metadata-integrity issues are addressed and reviewed. |
| **OD-4** | Query permission | **A** — an explicit per-form `field_searcher` relation. Form-user, record-owner or designer access is not query permission; actual field- and record-level authorization is enforced. |
| **OD-5** | Snapshot invalidation | **A** — in V1 any relevant organization authorization revision change invalidates the snapshot; no weaker cached discoverable set without independent revalidation. |
| **OD-6** | Batch verification | **A** — an independently authorized Authority batch verification of up to 50 records per call; each record fully and independently verified against its accepted revision, manifest and authorized readable scope; batching never weakens per-record checks; one failure in a required verified scope fails the future search. |
| **OD-7** | Step-up authentication | **Environment-specific.** Production and staging: AC-6 approval operations stay unavailable until real PB-08 step-up evidence is implemented and verified. Isolated development/test: a narrowly restricted synthetic approval path with a mandatory governance reference may be added later for AC-6 testing, structurally impossible to activate in production or staging; if that isolation cannot be demonstrated, approvals are rejected in all environments until PB-08 is ready. M1 implements no approvals. |
| **OD-8** | Independent search auditing | **B** — sensitive search approvals, denials and executions produce independently protected Authority audit evidence (established journal infrastructure), never predicate values, protected contents or unauthorized metadata. |

## 2. Threat model summary (unchanged in substance; refined by D1/D2/D5)

| ID | Threat | Control | Test |
|---|---|---|---|
| T1 | Handwritten request with a Confidential/Restricted/unknown field | Capability list computed from AC-6 + D5 before any record is touched; single indistinguishable `SEARCH_FIELD_NOT_ALLOWED` | ENC-SR-05/06, AC6-T05 |
| T2 | Predicate on a field the caller may not query on visible records | D5 query authorization (form-level) + per-record readable-set exclusion before evaluation | ENC-SR-07, AC6-T08 |
| T3 | Business DBA suppresses, injects, rolls back or alters a match | AC-5 catalogue + per-record full manifest verification + snapshot validation | ENC-SR-08..11, AC5-T01..06 |
| T4 | Scope too large → silent truncation | Catalogue-size check before any work; typed limit error | ENC-SR-12, AC5-T09 |
| T5 | Authority/SpiceDB/manifest unavailable → SQL fallback | No fallback path (M0 architecture test); typed 503 | ENC-SR-13, M0 tests |
| T6 | Revocation mid-search or stale cursor | Snapshot validated at the Authority before every page | ENC-SR-14, AC5-T07 |
| T7 | Content changed mid-search | Snapshot content-revision vector re-validated | ENC-SR-15, AC5-T08 |
| T8 | Cross-tenant or child-record leakage | Organization-bound catalogue; child predicates out of V1 (D6) | ENC-SR-16 |
| T9 | Strengthened classification → old plaintext searchable | AC-6 approval revoked in the same Authority revision as strengthening; encrypted leaf ⇒ failure | ENC-SR-17, AC6-T10 |
| T10 | Predicate values in logs, telemetry, URLs, audit | POST bodies only; values never logged or echoed | ENC-SR-18, M0 tests |
| T11 | Unsigned label/option metadata substituted under an approval (G21) | AC-6 binds approval to field id, type, classification, operators and Authority-held label/option snapshots | AC6-T06/T07 |
| T12 | Privileged migration catalogue reached by a user identity | Separate workload identity, command-bound, not routed through the business API | AC5-T11/T12 |
| T13 | Search used as a decryption or verification oracle | Never decrypts; failures quarantine and fail the whole search | C7-SEC-01 |
| T14 | Expensive repeated scans | Concurrency limits; the coordinator does not retry non-retryable search failures | C7-API-07 |

Residual risks, documented and not mitigated:

* a business DBA can read approved Public/Internal plaintext (ADR-PB-004 §3);
* a delivered match has already been seen;
* the Authority runtime and its database administrators are trusted (§4M).

## 3. AC-5 — Authority record catalogue (design; implemented in M1 — see §6A for what was built and where it differs)

### 3.1 Registry schema and ownership

The Authority already owns the trusted registry in its independently administered PostgreSQL (schema `authority`):

* `authority_resources`: one row per registered resource. For `form_record`, `parent_resource_entity_id` is the registered form definition and `resource_lifecycle_state` is the lifecycle.
* `record_content_heads`: one row per record — accepted revision, committed operation, manifest root, quarantine flag.
* `organization_authorization_states`: per organization, the `authorization_revision`, `projection_applied_revision`, `content_integrity_revision`, gate and journal digests.

Rows are written only by journaled security commands (`RegisterRecord`, …) and accepted content commits. Every write appends to the archived, checkpoint-anchored security or content journal (G02/G14).

AC-5 adds (owner: Authority `Catalogue` feature; descriptive multiword snake_case, UUID v7 keys):

* Index `ix_authority_resources_form_scope (organization_id, resource_type_name, parent_resource_entity_id, resource_lifecycle_state)`. Form-scoped enumeration must not scan the organization.
* Table `search_catalogue_snapshots`:
  * `search_catalogue_snapshot_id` PK, `organization_id`;
  * `snapshot_scope_kind` (`UserSearch` | `MigrationVerification`), `requesting_authority_user_id` (UserSearch) or `requesting_workload_name` + `security_command_id` (MigrationVerification);
  * `form_definition_id`;
  * `snapshot_authorization_revision`, `snapshot_content_integrity_revision`, `snapshot_projection_revision`;
  * `catalogue_entry_count`, `catalogue_digest` (SHA-256 over the canonical sorted entry list), `snapshot_issued_on`, `snapshot_expires_on`, `snapshot_invalidated_on`, `snapshot_invalidation_reason`.
  * Retention: expired rows purged after 24 h. They hold no record contents.
* Table `search_catalogue_snapshot_entries` (`search_catalogue_snapshot_entry_id`, `search_catalogue_snapshot_id`, `form_record_id`, `committed_content_revision`, `committed_operation_id`): the exact head vector the snapshot fixed. It is needed for the content check when the organization content revision moved.

### 3.2 Two enumeration boundaries (D1 modification)

| | **User-scoped search enumeration** | **Organization-scoped migration verification** |
|---|---|---|
| Caller | Business API with the delegated user assertion (existing workload credential + DUA) | A separate workload identity `field-protection-verifier`, used by the worker only, with its own credential, network policy and Authority role. The business API runtime cannot use it. |
| Authorization | Organization gate open; SpiceDB `discover` for the actor at the Authority-held ZedToken; AC-6/D5 query policy checked by the business before the call and **again by the Authority** for each predicate field id the request names | An accepted `ReprotectionOperation`/`FieldProtectionMigration` security command (or an A1 downgrade command) naming the form and field. No user assertion. Never callable "for any form". |
| Population returned | `Active` `form_record` resources with `parent = form` **∩** the actor's discover set, each with its accepted head | **All** `Active` and `Quarantined` `form_record` resources with `parent = form`, including records no user can discover, with head and quarantine state |
| Endpoint | `POST /internal/v1/search-catalogue` (private Authority API) | `POST /internal/v1/operator/migration-catalogue` (operator plane, distinct route group and authentication policy) |
| Exposed to users | Only as verified search results (records the user may discover) | **Never**. Consumed by the worker's post-check and reconciliation only. Results are not projected into any user API, count or UI. |
| Audit | Search audit event (field ids, operators, counts) | Journaled `MigrationCatalogueIssued` with command id and entry count |

The migration catalogue gives G19 its expected-population basis. **G19 stays open** until the worker reconciles against it in both directions (an Authority record missing from business rows, and a business row not in the registry), and REL/G19 adversarial tests pass.

### 3.3 Catalogue request and response (user scope)

`POST /internal/v1/search-catalogue`

* Request: `{ organization_id, form_definition_id, maximum_entries }`. The business API passes the configured `MaximumEligibleRecords` (D4) and never chooses a larger one; the Authority enforces its own ceiling.
* Response: `{ search_catalogue_snapshot_id, authorization_revision, projection_revision, content_integrity_revision, entries: [{ form_record_id, committed_content_revision, committed_operation_id, manifest_root_base64, content_head_quarantined }], records_without_content, snapshot_expires_on }`.

The Authority builds the response in **one read transaction**:

1. Bind the actor and read the organization state (gate must be open).
2. Run the SpiceDB `discover` lookup at the state's ZedToken, cursor-paged to completion (C-28).
3. Intersect with registry rows `parent = form`, `Active`.
4. Read every intersected head in the same transaction.
5. Re-read the organization state. If the authorization, projection or content revision moved, fail `NOT_READY` (retryable). The business API maps this to `SEARCH_SNAPSHOT_CHANGED`.
6. Persist the snapshot row and its entry vector, then return.

Rules:

* More than `maximum_entries` → `LIMIT_EXCEEDED` (422) with **no entries and no count** (D3/D4).
* A quarantined head is returned with its flag. The business API then fails the search with `SEARCH_VERIFICATION_UNAVAILABLE` (D3), not silently skipped.
* Records with revision 0 (never committed) cannot match any predicate. They are excluded and only their count is returned. That count reveals nothing new: these are the caller's own discoverable records.

### 3.4 Snapshot validation (genuine consistency, not a signed token)

The snapshot reference the business receives is only an id. **Validity is always decided by re-reading Authority state**; nothing is trusted from the token itself. A signed token without revision validation is insufficient.

`POST /internal/v1/search-catalogue/{snapshotId}/validate` → `{ valid, invalidation_reason }`. The snapshot is valid iff all of the following hold:

1. It exists, belongs to this organization and **this actor** (same authority user id from the delegated assertion), is not expired (default 5 minutes, maximum 15) and is not invalidated.
2. The organization gate is open and `authorization_revision == snapshot_authorization_revision`. Any security change — revocation, share, team, role or policy — invalidates. V1 is deliberately conservative; a later optimization may re-run the discover lookup and compare, but only with tests.
3. Content check:
   * **Fast path:** `content_integrity_revision == snapshot_content_integrity_revision`. The content revision advances on every accepted content commit and every integrity incident, and each step is journaled and archived, so nothing in the organization changed.
   * **Slow path** (the organization's content revision moved): every `(form_record_id, committed_content_revision, committed_operation_id)` in the snapshot's entry vector still equals the current head, and none is quarantined. Records committed elsewhere in the organization do not invalidate the snapshot.
4. AC-6 search policy revision for the form is unchanged (§4.6).

Validation happens:

* after the business has verified every entry (end of execution);
* before serving any further page from the business API cache;
* before re-executing on a cache miss.

Invalid → `SEARCH_SNAPSHOT_CHANGED` (409). The client restarts from page 1. Results are never combined across snapshots.

### 3.5 Business execution against the catalogue (M3, for completeness)

1. For each entry, read staged rows of `committed_operation_id` and call batch verification (§3.7, OD-6) with `ReadSearch` semantics: complete leaf-set, HMAC and root check, and the caller's readable item set.
2. Every verification must report the snapshot's authorization revision and the entry's head; otherwise fail `SEARCH_SNAPSHOT_CHANGED`.
3. Any failure, quarantine, missing row or extra row fails the whole search (D3).
4. Records where a predicate field is not readable are excluded before evaluation (D5 / §4.7).
5. Predicates are evaluated only over verified plaintext typed values. An encrypted leaf for a predicate field fails the search (`PREDICATE_VALUE_ENCRYPTED`). Search never decrypts.

### 3.6 Failure behaviour (business records versus the catalogue)

| Situation | Detected by | Outcome |
|---|---|---|
| Catalogue record has no business rows (deleted) | Staged read empty → leaf-set mismatch | Whole search fails `SEARCH_VERIFICATION_UNAVAILABLE`; Authority quarantines the head (AC-4) |
| A value row deleted, altered or index manipulated | HMAC or leaf-set mismatch | Same |
| Rows rolled back to an earlier valid signed revision | Rows of the old operation do not match the head's manifest | Same |
| Business row with no registry entry (injected) | Never read: the catalogue drives | Not shown; reported by the migration reconciliation (G19) |
| Business row moved to another form | Signed header + registry parent | No effect on scope |
| Head changed after the catalogue | Verification revision ≠ entry; snapshot validation | `SEARCH_SNAPSHOT_CHANGED` |
| Head quarantined | Catalogue flag or verification | `SEARCH_VERIFICATION_UNAVAILABLE` |
| Authority, SpiceDB or archive gate unavailable | Gate check | `SEARCH_VERIFICATION_UNAVAILABLE` (retryable) |
| More than the limit of entries, bytes or time | Catalogue / executor | `SEARCH_VERIFICATION_LIMIT` (limit_kind) |

### 3.7 Performance and scalability constraints

* Catalogue cost: one SpiceDB lookup (existing, paged), one indexed registry range, one head join, one snapshot insert. It is O(discoverable ∩ form) and bounded by the 500 limit.
* Verification cost today is one `GetCommittedContentRevision` plus one `VerifyRecordProjection` per record. At 500 records that is about 1,000 internal calls, which is unlikely to fit 15 s.
  * **OD-6:** add a batch verification operation (`verify-record-projections`, up to 50 records per call, each verified fully and independently, results per record).
  * Staged reads are batched on the business side.
* Measure per PB-09 with Level 2 controls on: 100/250/500 records × 10/50 fields; p50/p95/p99; Authority calls; HSM/Vault MAC operations; bytes; time to first failure. The D4 limits may have to drop.
* Snapshot rows are small, so expired-row purge is cheap.

### 3.8 AC-5 adversarial proof tests (to be implemented with M1; real Authority, PostgreSQL ×2, SpiceDB, Vault)

| Test | Attack / condition | Pass |
|---|---|---|
| AC5-T01 | Business DBA deletes a matching record's rows | Search fails closed; never "0 matches" (ENC-SR-08) |
| AC5-T02 | DBA alters a matching value so it would not match | HMAC/leaf mismatch → failure (ENC-SR-09) |
| AC5-T03 | DBA removes the record from business listings and indexes | Still in the catalogue; evaluated or failure (ENC-SR-10) |
| AC5-T04 | DBA injects a record row and value rows for the form | Not in the catalogue; never returned (ENC-SR-11) |
| AC5-T05 | DBA restores an earlier signed revision's rows | Mismatch → failure |
| AC5-T06 | DBA changes `form_records.form_definition_id` | Catalogue scope unchanged |
| AC5-T07 | Share revoked between catalogue and page 2 | `SEARCH_SNAPSHOT_CHANGED`; nothing from the revoked record served (ENC-SR-14) |
| AC5-T08 | Record edited during execution; another record of the org edited (slow path) | First → snapshot changed; second → snapshot still valid (ENC-SR-15) |
| AC5-T09 | Catalogue larger than the limit | `LIMIT_EXCEEDED` with no entries/count; business 422 (ENC-SR-12) |
| AC5-T10 | Snapshot id from another user / organization / expired / forged | Invalid; no data |
| AC5-T11 | Business API workload calls the migration catalogue | Rejected by authentication policy |
| AC5-T12 | Verifier workload without a matching accepted command | Rejected; journaled denial |
| AC5-T13 | Record no user can discover | Absent from every user catalogue; present in the migration catalogue |
| AC5-T14 | SpiceDB lookup > 1,000 pages; projection lag; gate closed | Complete or `NOT_READY`; never partial |
| AC5-T15 | Quarantined head in scope | Whole search fails |

## 4. AC-6 — Authority-held field search policy (design; implemented in M2 — see §6B for what was built and where it differs)

### 4.1 Principals

| Action | Allowed principals (SpiceDB) | Additional requirement |
|---|---|---|
| Set organization search policy (`plaintext_search_permitted`) | `organization#administer_security` (system administrator) or organization owner via `manage_configuration` — **OD-2** decides which | Fresh step-up per PB-08 (dependency: PB-08 step-up evidence is not implemented — **OD-7**) |
| Approve / withdraw search of a field | Same principals as the organization search policy | Governance approval reference; step-up |
| Grant query access to a form's approved fields (D5) | Organization owner / security administrator; form designer **only if explicitly granted** `manage_field_search` | Journaled command |
| Query an approved field | Holders of the form's `field_searcher` relation (user or team#participant), per D5 | Plus per-record field readability at execution |
| Designer | Can see a field's search *status* in Configure Field only with `design` on the form; cannot approve unless separately granted | — |

Record owners gain **nothing** from ownership here (D5). A user who owns records of a form but is not a `field_searcher` sees no searchable fields.

### 4.2 Organization policy floors

`organization_protection_policies` gains `plaintext_search_permitted` (default **false**) and `search_policy_revision`, both changed only by `UpdateMandatoryPolicy`. Approval requires all of:

* `plaintext_search_permitted = true`;
* the field's classification is `Public`, or `Internal` with `plaintext_internal_fields_permitted = true`, and is at or above `minimum_field_classification`.

Turning `plaintext_search_permitted` off withdraws **all** approvals of the organization in the same revision.

### 4.3 Binding (form version, field identity, type, classification, operators) and G21

An approval is an Authority resource `field_search_approval` (entity = field definition id, parent = form), with `field_search_approvals` rows storing:

* `form_definition_id`, `field_definition_id`;
* `approved_field_data_type` (String/Integer/Decimal/Date/DateTime/Boolean/Option);
* `approved_field_classification`;
* `approved_operators` (array);
* `approved_form_version_number` — the published version the approval was made against;
* `approved_field_label_snapshot` (display text frozen at approval);
* for Option fields, `approved_option_snapshot` (option id → label pairs frozen at approval);
* `approval_reference`, `approved_by_security_command_id`, `approval_state` (`Active`/`Withdrawn`/`Invalidated`), `invalidation_reason`.

How each G21 substitution is defeated:

* **Different field under the same approval.** Execution matches predicates by `field_definition_id`. Each verified value row carries its signed `field_definition_id` and signed row schema, which fixes the storage type. A DBA cannot make field B's values answer for field A's approval.
* **Changed type.** Execution requires the verified row schema to equal `approved_field_data_type`. A mismatch fails the search.
* **Relabelled field or option.** The capability response takes labels and option lists from the **Authority snapshots**, not from unsigned `form_versions` / `field_options` rows. A DBA relabelling business rows cannot change what the searcher is told they are searching. Option predicates use the stable `field_option_id` (M0 contract), checked against the approved option snapshot.
* **Injected option (G21 U11).** An option id not in `approved_option_snapshot` is rejected (`SEARCH_OPERATOR_NOT_SUPPORTED`, value not allowed).
* **"Published form-version policy not trustworthy" (D2).** Form-version layouts are unsigned today (G21/G18). Approval therefore relies only on Authority-held facts:
  * the field must have **no** `field_protection_requirement` and no pending reprotection or reclassification;
  * it must have a recorded type and classification from an Authority command;
  * its current published version number must be the one the Authority recorded.
  
  The Authority records published versions only through `PublishFormSecurityRevision` today, so **OD-3** decides whether publishing must register the version number and a layout digest with the Authority for every published version. Until then, approval is refused for fields whose type or classification the Authority has never recorded.

**Residual (G21, documented):** the record-editing UI still renders labels from unsigned rows, which is outside search. G21 remains open.

### 4.4 Approval, denial, withdrawal and invalidation flows

| Event | Effect |
|---|---|
| `ApproveFieldSearch` accepted | `Active` approval at a new authorization revision; `search_policy_revision` increments |
| `ApproveFieldSearch` rejected | Journaled rejection with reason: `FIELD_ENCRYPTED`, `CLASSIFICATION_NOT_PERMITTED`, `ORGANIZATION_SEARCH_DISABLED`, `ACTOR_NOT_PERMITTED`, `FORM_VERSION_UNTRUSTED`, `PROTECTION_PENDING`, `OPERATOR_NOT_APPLICABLE` |
| `WithdrawFieldSearch` | `Withdrawn` |
| Strengthening (`PublishFormSecurityRevision` to ApplicationEncrypted, or reclassification to Confidential/Restricted) | **Same command and revision** sets the approval `Invalidated` (`FIELD_STRENGTHENED`). Search of historical plaintext is never re-enabled; re-approval is impossible while the field is encrypted. |
| A1 downgrade (encrypted → plaintext) completes | Does **not** create an approval. A new explicit approval is required after the migration's verified completion (which itself depends on the AC-5 migration catalogue). |
| Organization turns `plaintext_search_permitted` off, or raises `minimum_field_classification` | All non-conforming approvals `Invalidated` in the same revision |
| New form version published with a different field type or classification (impossible for type; classification only strengthens) | Approval `Invalidated` (`FORM_VERSION_CHANGED`) — **OD-3** |
| Field retired | `Invalidated` |

Every change advances the authorization revision, so every outstanding search snapshot dies (§3.4).

### 4.5 Field-level predicate authorization

There are two layers, both from the Authority:

1. **Query permission (D5)**, checked once per request before any record is read. For each predicate field id, the caller must have SpiceDB `form_definition#query_fields` (= `field_searcher` relation) **and** the field must have an `Active` approval with the operator in `approved_operators`. Any failure is the single `SEARCH_FIELD_NOT_ALLOWED`, identical for unknown, hidden, encrypted or unapproved fields.
2. **Per-record readability**, checked at execution. A record is evaluated only if the caller can read every value leaf of every predicate field on that record (`readable_items`). Otherwise the record is excluded before evaluation, independently of its value. The scope statement says so.

### 4.6 Complete capability-response design (D5)

`GET /api/v1/form-records/search-capabilities?formDefinitionId=` (M0 contract, unchanged when enabled):

* `fieldValueSearchAvailable`, `unavailableReasonCode`, `maximumConditions`, `conditionJoin` (`And`), `policyRevision` (the Authority `search_policy_revision`);
* `fields`: only fields with an `Active` approval **and** caller `query_fields` on the form. Each has its label from the Authority snapshot, the approved type and the approved operators, and for Option fields the approved option snapshot. Text length limits come from configuration;
* `notSearchableFields`: returned **only** to callers with `design` on the form or `administer_security` (configuration access, D5), listing the fields' encryption or approval status (`ENCRYPTED`, `NOT_APPROVED`). Ordinary searchers get an empty list, so they learn nothing about fields they cannot query;
* a caller with no form access gets the same "no fields" response as for a non-existent form.

M0 returns `fieldValueSearchAvailable=false`, reason `TRUSTED_SEARCH_NOT_ENABLED`, and empty lists for every form (§6).

### 4.7 Classification strengthening and migration rules

* Approval is impossible while a field has a `field_protection_requirement`, a pending reprotection or a pending reclassification.
* Strengthening invalidates the approval atomically (§4.4). Business typed plaintext indexes for that field are dropped by the migration (ENC-SR-17).
* Historical plaintext revisions remain reachable only through authorized historic reads (C6 M2), which are not a search surface.
* An encrypted leaf encountered during evaluation fails the search. This covers a record migrated between catalogue and verification, or a concealed record (G19).

### 4.8 AC-6 adversarial tests (with M2)

| Test | Attack / condition | Pass |
|---|---|---|
| AC6-T01 | Approve a Confidential/Restricted field; an ApplicationEncrypted field; with search disabled by policy | Rejected with the typed reason; journaled |
| AC6-T02 | Designer without explicit grant approves | Rejected |
| AC6-T03 | Business DBA sets `is_searchable=true` / `field_classification='Public'` | No capability appears; predicate → `SEARCH_FIELD_NOT_ALLOWED` |
| AC6-T04 | Client sends the field id of an unapproved field | Same single denial, same timing class |
| AC6-T05 | Unknown id, hidden field, other form's field | Indistinguishable denials (ENC-SR-05/07) |
| AC6-T06 | DBA relabels an approved field or its options | Capability labels unchanged (Authority snapshot) |
| AC6-T07 | DBA injects an option and submits its id | Rejected |
| AC6-T08 | Record owner without `field_searcher` | No fields offered; predicates denied |
| AC6-T09 | Revoke `field_searcher` mid-search | Snapshot invalid → `SEARCH_SNAPSHOT_CHANGED` |
| AC6-T10 | Strengthen an approved field | Approval invalidated in the same revision; search denied; history not searchable (ENC-SR-17) |
| AC6-T11 | Value rows of field B presented under field A's id (forged) | HMAC/manifest failure |
| AC6-T12 | Turning `plaintext_search_permitted` off | All approvals invalidated |

## 5. Open decisions OD-1..OD-8 (resolved by C-60, §1A; the options below are kept for the record)

| # | Question | Options | Recommendation |
|---|---|---|---|
| OD-1 | Identity for the migration catalogue | (a) new workload `field-protection-verifier` with its own credential; (b) reuse the worker's existing Authority credential with a role claim | **(a)** — distinct boundary per D1 |
| OD-2 | Who may set organization search policy and approve fields | (a) system administrator (`administer_security`) only; (b) also organization owner (`manage_configuration`); (c) two-person: owner proposes, security administrator approves | **(c)** for Restricted-adjacent orgs, otherwise **(a)** — owner decision |
| OD-3 | Trust of published form versions for approval binding | (a) Authority records each published version number + layout digest (partial G21/G18 remediation); (b) approve only fields whose type and classification the Authority recorded, and invalidate on any new version | **(a)**; (b) as the interim rule |
| OD-4 | Query grant model (D5) | (a) explicit `field_searcher` relation per form; (b) reuse `form_user` (create-record) | **(a)** — D5 forbids blanket form access |
| OD-5 | Snapshot invalidation on unrelated authorization changes | (a) any authorization revision invalidates (V1); (b) re-run the discover lookup and compare sets | **(a)** first; measure restart rate |
| OD-6 | Batch verification operation in the Authority | (a) add `verify-record-projections` (≤50 per call); (b) keep per-record calls and lower the record limit | **(a)** — needed for D4 |
| OD-7 | Step-up for approvals | (a) block approvals until PB-08 step-up evidence exists; (b) allow with a recorded governance reference only, in development | **(a)** for production; (b) for development only |
| OD-8 | Search audit sink | (a) business `AuditEvents` projection only; (b) also the Authority's independent intake for sensitive read evidence | **(b)** for denials and executions |

## 6. M0 — implemented (C-57)

### 6.1 Backend (`Search` feature)

* `Application/Features/Search/FieldSearch/`:
  * `FieldSearchHandler` — capabilities, search, page;
  * `FieldSearchRequestValidator` — request shape only;
  * `FieldSearchLimits` — D4, configuration section `FieldSearch`, validated at startup; `MaximumConditions` may be lowered, never raised above 5;
  * `FieldSearchVocabulary` — operators, value types, error codes;
  * `SearchException`.
* `Api/Features/Search/SearchEndpoints.cs`: feature owner `Search`; responses `no-store`.
* **Gate:** every well-formed search or page request → `503 SEARCH_VERIFICATION_UNAVAILABLE` with problem extensions `unavailable_reason: TRUSTED_SEARCH_NOT_ENABLED` and `retryable: false`. It is never an empty 200.
* `FieldSearchHandler` depends only on `FieldSearchLimits`. An architecture test forbids `Application.Features.Search` from depending on `Application.Common.Persistence` or FormRecords' domain or listing types.

### 6.2 Endpoints (`/api/v1`)

| Route | Request | M0 response |
|---|---|---|
| `GET /form-records/search-capabilities?formDefinitionId=` | — | 200 `{fieldValueSearchAvailable:false, unavailableReasonCode:"TRUSTED_SEARCH_NOT_ENABLED", maximumConditions:5, conditionJoin:"And", fields:[], notSearchableFields:[], policyRevision:null}` for every form; 422 for an empty or invalid id |
| `POST /form-records/field-search` | `FieldSearchRequest` | 422 `SEARCH_REQUEST_INVALID` / `SEARCH_OPERATOR_NOT_SUPPORTED` for malformed requests; otherwise 503 gate |
| `POST /form-records/field-search/page` | `{cursor}` | 422 if no cursor; otherwise 503 gate |

### 6.3 Request contract

* `formDefinitionId` (required).
* `conditions[1..5]`, each `{ fieldDefinitionId, operator, valueType, value?, lowerBound?, upperBound? }`. Values are invariant text:
  * String, NFC, 1–200 characters; contains/startsWith at least 3;
  * Integer `-?digits`;
  * Decimal `-?digits[.digits]`, no exponent or separators;
  * Date `yyyy-MM-dd`;
  * DateTime ISO 8601 **with offset**;
  * Option: a field option **id** (never key or label, G21).
* `between` takes both bounds (lower ≤ upper); `isTrue`/`isFalse` take nothing; every other operator takes `value`.
* Metadata filters: `recordStatus`, `includeArchived`, `includeChildren`, `sortBy` (modifiedOn/createdOn/formName/recordStatus only; no field-value sort, D6), `sortDirection`, `pageSize` ∈ {10, 25, 50, 100}.

### 6.4 Operators per value type

| Value type | Operators |
|---|---|
| String | equals, startsWith, contains |
| Integer, Decimal | equals, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual, between |
| Date | equals, before, after, between |
| DateTime | before, after, between |
| Boolean | isTrue, isFalse |
| Option | is, includes |

Whether a particular field offers an operator will come from AC-6.

### 6.5 Response and error codes

**Response (when enabled):** `{ items: RecordListItemResponse[], totalCount, pageStart, pageSize, snapshotReference, authorizationRevision, completeness:"Complete", scopeStatement, continuationToken?, previousToken? }`. Rows reuse the My Records item, so results go into the same table.

**Error mapping** (RFC 9457; `title` = code; details name conditions by position only and never echo values):

| errorCode | HTTP | retryable | Notes |
|---|---|---|---|
| `SEARCH_REQUEST_INVALID` | 422 | false | Shape errors (count, value presence/format, filters, page size) |
| `SEARCH_OPERATOR_NOT_SUPPORTED` | 422 | false | Unknown operator, or operator not applicable to the value type |
| `SEARCH_FIELD_NOT_ALLOWED` | 422 | false | (AC-6) any field not in the capability list; single indistinguishable response |
| `SEARCH_VERIFICATION_LIMIT` | 422 | false | (AC-5) `limit_kind` records/bytes/time |
| `SEARCH_SNAPSHOT_CHANGED` | 409 | true | (AC-5) restart from page 1 |
| `SEARCH_VERIFICATION_UNAVAILABLE` | 503 | M0 false / otherwise true | `unavailable_reason` (M0: `TRUSTED_SEARCH_NOT_ENABLED`) |

### 6.6 Frontend

* **`features/search`** (PG-015 owner):
  * `AdvancedFieldSearch` — collapsible disclosure; each condition is a fieldset "Condition N" with Field | Condition side by side and Value beneath; typed value controls; Add condition and Search records with Lucide icons;
  * `conditionModel` — AND conditions, ≤5, request builder;
  * `searchFailure` — typed messages, never "no records".
* **My Records** (`RecordsPage`):
  * reference/ID search on top;
  * Form | Status, then Sort by | Rows per page, in two columns;
  * related/archived checkboxes and Clear filters;
  * Advanced Search below, in the same card;
  * capabilities fetched from the server for the selected form;
  * `onSearch` is wired only when the server reports field-value search available, so in M0 Search records is disabled and nothing is sent;
  * a failure shows its typed explanation instead of the table, and a result renders in the **same** My Records table. Predicate values never enter the URL.
* **PG-069 gallery:** "Advanced Search (synthetic preview)" with synthetic capabilities, clearly labelled, sending nothing. It lets the enabled layout be reviewed.
* **Coordinator:** endpoints `search.capabilities` (GET), `search.fieldSearch` and `search.fieldSearchPage` (POST, interactive-read lane, not idempotency-keyed, so failures are final and not retried).
* **Not in M0:** Form Designer "Search" section and organization search-policy UI (depend on AC-6/M2). DateTime predicates convert from the browser's zone. Conversion via the user's effective IANA zone, with DST gap/fold handling, is an M3 item.

## 6A. M1 — AC-5 implemented (C-62)

### 6A.1 Authority (independently administered; AuthorityDb, SpiceDB, Vault/HSM)

| Element | Implementation |
|---|---|
| Registry | Reuses `authority_resources` (form_record, `parent_resource_entity_id` = form, written only by journaled security commands) and `record_content_heads`. New index `ix_authority_resources_form_scope`. No second source of truth. |
| Snapshots | `search_catalogue_snapshots` (scope kind, requesting user **or** workload + scoping command, form, authorization/projection/content-integrity revision, entry count, SHA-256 digest of the canonical sorted entries, issue/expiry/invalidation) and `search_catalogue_snapshot_entries` (record, position, accepted revision, committed operation, head quarantine flag, registry lifecycle). Check constraints tie each scope kind to its actor. The runtime role may INSERT/UPDATE but never DELETE. Expired rows are marked, not deleted (purge is a maintenance task, not a runtime privilege). |
| User catalogue | `POST /internal/v1/search-catalogue` (business workload + delegated user). Registry rows of the form in `Active`/`Quarantined` ∩ the complete SpiceDB `discover` lookup at the Authority-held token (C-28 paging). Heads read in the same transaction. Under the organization lock, the authorization, projection and content-integrity revisions and the quarantine flag must be unchanged (otherwise `AUTHORITY_NOT_READY`). More than `maximum_entries` (≤ `Catalogue:MaximumUserEntries`, 500) → `422 LIMIT_EXCEEDED` with no entries and no count. Records with revision 0 and quarantined heads/resources are listed with their state, never dropped. |
| Snapshot validation | `POST /internal/v1/search-catalogue/{id}/validate` — the caller's own snapshot only (anyone else: 404). Decided only from current state, in this order: recorded invalidation; expiry; gate closed (reported as transient, not recorded); authorization revision change (OD-5); projection revision change; scoping command superseded (migration scope); then, only if the content-integrity revision moved, every entry's head (revision, operation), quarantine flag and registry lifecycle/parent. An invalidation is recorded and never reverts. |
| Migration catalogue | `POST /internal/v1/verifier/migration-catalogue`, `GET …/{id}/entries?offset=`, `POST …/{id}/validate` under the separate **Verifier** scheme and `field-protection-verifier` role (OD-1). Scoped by an Effective PublishFormSecurityRevision command of the requested form that is still the field's current protection decision (the shared `ProtectionCommandResolver`, the same rule as reprotection writes). Population: `Active`, `Quarantined` and `Registered` records of the form — including records no user can discover. Paged (`Catalogue:MigrationPageSize`, 1000), ceiling `MaximumMigrationEntries` 50,000. Snapshots are bound to the issuing workload name. Not called by any business component in M1 (G19 reconciliation is M4). |
| Verifier credential | `Identity:Verifier` (issuer, audience, ≥256-bit key). Unconfigured: no scheme is registered and the policy denies everything, so the routes stay in the OpenAPI document but nothing passes. Partial configuration, or a key or issuer reused from the workload/operator/user-assertion credentials, refuses startup. Configured only on the isolated test stack (synthetic key); the development and other stacks set nothing. |
| Batch verification | `POST /internal/v1/integrity/verify-record-projections` (business workload + delegated user), 1..50 distinct records, read action. Each record goes through the **unchanged** core of `VerifyRecordProjection` (registry, discover/read, quarantine, archive receipt, complete leaf set, every HMAC, root, readable field items), now shared by both operations, against the head the caller expects. A different current head → `HEAD_CHANGED` (nothing verified, nothing quarantined). Per-record typed results: `RECORD_UNAVAILABLE` (concealment-safe), `QUARANTINED`, `AWAITING_ARCHIVE`, `NO_COMMITTED_CONTENT`, `KEY_UNAVAILABLE` and the integrity codes (record quarantined, incident journaled). Provider outages fail the whole call. If the authorization state moved during the batch, the tamper evidence is committed and the call fails `AUTHORITY_NOT_READY`. |
| Audit (OD-8) | New non-gating `audit` journal stream (`audit_journal_revision`, `latest_audit_journal_digest`; hash-chained, archived and anchored like the other streams by the unchanged archive processor). It never advances the content-integrity revision and never closes the gate. Events: `SearchCatalogueIssued`, `MigrationCatalogueIssued`, `MigrationCatalogueDenied` — snapshot id, scope, form, actor (user or workload + command), entry count, digest, revisions, correlation; never record ids, field values or predicates. |
| Limits | `Catalogue` section (startup-validated): user ceiling 500, migration 50,000, page 1000, snapshot lifetimes 300 s (user) / 900 s (migration), at most 900 s. |
| Migrations | `20261024051111_SearchCatalogueAndAuditStream` — additive only (2 tables, 2 columns, 4 indexes). `20261024074555_JournalContentOperationIndex` — one index. |
| Checkpoint lookup (performance fix) | Verification finds the record head's archive checkpoint by its committed operation in one indexed query (`FindContentCommitJournalEntryAsync`, index `ix_journal_content_operation`). It previously walked back through every content revision of the organization on every verified read, making a scope O(N²). Same result (the latest ContentHeadCommitted entry of the operation). Measured effect in §6A.5. |

### 6A.2 Business (no field-value search)

* `Search/Verification/TrustedScopeVerifier`: catalogue → staged rows of each entry's committed operation through the FormRecords port `IAcceptedRecordItemsReader` → batches of 50 → final snapshot validation.
  * **D3:** any failure fails the whole scope. A quarantined entry or record, an unverifiable record → `SEARCH_VERIFICATION_UNAVAILABLE` (`RECORD_QUARANTINED` / `RECORD_VERIFICATION_FAILED`). A changed head, an unavailable record or an invalid snapshot → `SEARCH_SNAPSHOT_CHANGED`. The Authority not ready → `SEARCH_SNAPSHOT_CHANGED`; the Authority unavailable → retryable `SEARCH_VERIFICATION_UNAVAILABLE`.
  * **D4:** `LIMIT_EXCEEDED` → `SEARCH_VERIFICATION_LIMIT` (`records`); staged bytes above 64 MiB → `bytes`, checked before anything is sent; the 15 s budget → `time`.
  * It evaluates no predicate and returns no values.
* Authority client methods for the catalogue, its validation and batch verification.
* `POST /api/v1/testing/search-scope-verification` — mapped **only** in the `Testing` environment (isolated Docker stack), not in the published OpenAPI. It returns counts, record ids of the caller's own verified scope and costs, so the real-stack hostile-database, limit and benchmark tests can drive the verification path. The public `field-search` endpoints keep refusing with `TRUSTED_SEARCH_NOT_ENABLED` (M0 gate unchanged).

### 6A.3 Differences from the reviewed design (§3)

* The contract entry carries no manifest root. The business side never needs it: verification happens at the Authority against the head it fixed.
* The migration population also includes `Registered` records (registration not yet effective), so a pending record cannot be missed by migration verification.
* Expired snapshots are marked, not purged by the runtime (no DELETE privilege).
* Gate-closed validation is reported as transient and not recorded as an invalidation.
* User catalogue issuance writes one audit event per catalogue. The single execution event of M3 (field ids, operators, counts) is still to come.

### 6A.5 Measured cost (PB-09, provisional; `docs/PHASE_C7_STATUS.md` §8.6)

| Records | p50 | p95 | Authority calls | Verified bytes |
|---|---|---|---|---|
| 50 | 0.59 s | 0.67 s | 3 | 0.18 MB |
| 150 | 1.82 s | 1.85 s | 5 | 0.55 MB |
| 480 | 6.12 s | 6.15 s | 12 | 1.76 MB |

Before the checkpoint-lookup fix: 50 → 0.84 s, 150 → 4.46 s, 480 → over the 15 s budget.

* Cost is now linear (≈ 12.7 ms per record), so 500 records fit the D4 budget.
* The PB-09 < 3 s target holds only up to ≈ 230 records on this infrastructure. This is a decision for the owner before M3.

### 6A.4 What M1 does not establish

* No field-value search, capability, approval or Designer change (M0 gate unchanged).
* **G06 stays OPEN:** the verified scope is the precondition of a complete result; predicate evaluation, ordering, paging and the ENC-SR execution tests come with M3.
* **G19 stays OPEN:** the migration catalogue exists, but the worker does not use it yet (M4).
* **G21 stays OPEN.**
* No independent review of the Authority changes, no Azure staging evidence, no production limits.

## 6B. M2 — AC-6 implemented (C-67)

### 6B.1 Authority (independently administered)

| Element | Implementation |
|---|---|
| Published versions (OD-3) | `RegisterPublishedFormVersion` security command, submitted by the business publish path after its commit (key `form-version:{versionId}`). The Authority records `published_form_versions`: form, version number (strictly increasing), SHA-256 of the canonical layout (`layout-digest/v1`), and a field manifest (id, type, classification, protection, label, Option snapshot) — the Authority's own record of what was published. Only a designer of the form or an organization administrator may register. An entry claiming plaintext for a field the Authority holds an encryption requirement for is refused `FORM_VERSION_UNTRUSTED`. Versions published before M2 are never registered after the fact (OD-3): the designer publishes again. |
| Approvals | `ApproveFieldSearch` (`administer_security` only — `organization#administer_security = system_administrator`, OD-2 A). Binds `field_search_approvals` to the **latest registered version**: version number, layout digest, type, classification, protection, label and option snapshot are copied from the Authority's manifest, never from the request. Requires: the organization policy published with `plaintext_search_permitted`; the field `Eligible` (plaintext, Public or Internal at or above the floor and allowed by `plaintext_internal_fields_permitted`, no Authority-held encryption requirement or pending change, searchable type); operators a non-empty, distinct subset of the type's vocabulary; a governance reference (1..200); and the step-up gate. At most one `Active` approval per field (filtered unique index). |
| Step-up gate (OD-7) | `FieldSearch:ApprovalStepUp`: `Unavailable` (default; every approval refused `STEP_UP_REQUIRED`, capability reason `PB08_STEP_UP_NOT_AVAILABLE`) or `SyntheticDevelopment`. The synthetic mode is accepted only when the environment is `Development` or `Testing` **and** delegated users come from the synthetic `TestIdentityProvider`; any other combination **refuses startup**. The request must carry the fixed marker `synthetic-development-step-up`; it proves nothing about the person. It is set only in `compose.security-test.yaml` and `compose.dev-persistent.yaml`. **PB-08 is not resolved.** |
| Withdrawal and query grants | `WithdrawFieldSearch` (reason required) and `UpdateFieldSearchers` (the `field_searcher` relation on the form; users or `team#participant`; 1..100 changes) — `administer_security` only. SpiceDB `permission query_fields = field_searcher` (OD-4 A); no other relation implies it. |
| Invalidation | In the command that causes it: `PublishFormSecurityRevision` to ApplicationEncrypted (`FIELD_STRENGTHENED`); a newly registered version that changes the field's type, classification, protection, label or options, or removes it (`FORM_VERSION_CHANGED`; unchanged fields keep their approval); `UpdateMandatoryPolicy` turning search off or raising the floors (`ORGANIZATION_POLICY_CHANGED`). Each advances `search_policy_revision`. |
| Capabilities | `POST /internal/v1/field-search/capabilities`: one batch permission check (`query_fields`, `design`, `administer_security`) at one authorization revision. `fields` only with query permission and the policy switch on, with labels and options from the approval snapshots. `configuration` (per-field status, eligibility, permitted and approved operators, approval state and reason, registered version, `canApprove`, `approvalOperational`) only for designers of the form and security administrators; the current `field_searcher` subjects only for security administrators. Fails closed (`AUTHORITY_NOT_READY`) if the revision moved during the read. |
| Audit (OD-8) | Accepted approvals, withdrawals, grants and registrations are committed security commands in the hash-chained authorization stream. Rejected AC-6 commands (actor not permitted, not eligible, untrusted version, step-up missing) append `FieldSearchCommandRejected` to the archived audit stream in their own transaction (reason code; detail only for eligibility; never values). |
| Migration | `*_FieldSearchApprovals` — additive: `published_form_versions`, `field_search_approvals`, two policy columns (`plaintext_search_permitted`, `search_policy_revision`). |

### 6B.2 Business

* **Publish:** after the version commits, the handler registers it with the Authority (retrying a stale revision with the same key) and records `FORM_VERSION_AUTHORITY_REGISTERED` or `FORM_VERSION_AUTHORITY_REGISTRATION_FAILED`. The response carries `authorityRegistered`. A failed registration leaves the version published but not approvable.
* **Search policy** (`FieldSearchPolicyHandlers`, owner `Search`): `GET /api/v1/form-definitions/{id}/field-search` (404 for anyone without configuration access, like an unknown form), `POST …/fields/{fieldId}/search-approval`, `POST …/search-approval/withdraw`, `PUT …/field-searchers`. Each relays a security command; the business pre-check (`administer_security`) only spares a pointless command — the Authority decides. Business audit actions `FIELD_SEARCH_APPROVED`, `FIELD_SEARCH_WITHDRAWN`, `FIELD_SEARCHERS_CHANGED`.
* **Capabilities and the execution gate:** `search-capabilities` returns the Authority's fields; `field-search` authorizes every condition (field, value type, operator, and Option id against the frozen snapshot → `SEARCH_FIELD_NOT_ALLOWED` / `SEARCH_OPERATOR_NOT_SUPPORTED`) and then still refuses with `TRUSTED_SEARCH_NOT_ENABLED`. Nothing reads BusinessDb records.
* **Organization policy:** `plaintextSearchPermitted` on the security policy, published to the Authority with the rest of the policy.

### 6B.3 Frontend

* **Configure Field → Search** (new tab, field elements only): classification, protection ("Encrypted for privacy — not searchable." for encrypted fields), eligibility with its reason, permitted conditions, approval state (approved conditions and version, or why the approval is no longer valid). Viewing is separate from granting: approval and withdrawal controls appear only to security administrators; where the Authority cannot accept approvals the section says so and the controls are inert. No control changes encryption. The development step-up is an explicit, labelled checkbox.
* **Form permissions → Who can search field values:** shown only to security administrators (from the Authority's list); grant and remove people and teams.
* **Organization settings:** "Permit search approvals for plaintext Public/Internal fields".
* **Advanced Search:** the Field and Condition controls are populated from the capability response, so approved fields can be chosen and conditions composed. **Search records stays disabled** and the panel says nothing is searched until trusted search is turned on. Layout unchanged (reference/ID search on top; Form | Status; Field | Condition with Value beneath; Add condition and Search records; one results table). The gallery preview is unchanged.
* A security administrator reaches Configure Field through the read-only designer only if they can open the form (designer or form user). A dedicated security-administration list of forms is not part of M2.

### 6B.4 Differences from the reviewed design (§4)

* Query grants are restricted to `administer_security` (the design also allowed organization owners and explicitly granted designers) — stricter, per OD-2 A.
* Released (downgraded) fields stay `ProtectionPending` for approval purposes until the G19/M4 reconciliation can verify the migration's completeness.
* There is no "request approval" workflow: designers see the status and the reasons; approving is a security administrator's action.
* AC6-T09 (revocation mid-search) and AC6-T11 (forged value rows) concern execution and remain M3 tests; M2 proves the capability and predicate-authorization layers they depend on.

### 6B.5 What M2 does not establish

* No field-value search runs (M3). **G06 OPEN.**
* **G19 OPEN** (M4). **G21 OPEN:** approvals no longer depend on unsigned business metadata, but record editing still renders unsigned labels, and only versions published since M2 are registered.
* **PB-08 not resolved:** production and staging approvals stay disabled; the synthetic path is development/test only.
* No independent review of the Authority changes and no Azure staging evidence.

## 6C. M1/M2 review corrections (C-68) and M3 — verified execution (C-69)

### 6C.1 Search-approval administration without Designer or record access (review A)

* **Authority:** `POST /internal/v1/field-search/administration` (`administer_security` only): the organization's forms that have a registered published version and whose form resource is Active — the latest registered version, the **registered title** (new: `form_title` in `RegisterPublishedFormVersion`, additive column `form_title_snapshot`), field count, eligible fields, active and invalidated approvals. Nothing from business rows. Per-form configuration (§6B) was already answered to `administer_security` holders; it is now answered **only for an Active form of the caller's organization** (another organization's administrator gets the unknown-form answer — found in M2 verification).
* **Business:** `GET /api/v1/organization/search-approvals` (403 for everyone else), capability `security.administer` in the session.
* **UI:** "Search approvals" (navigation for security administrators): published forms → Review fields → the same Search section per field (approve with conditions, reference and the development step-up; withdraw) and "Who can search field values". The reusable approval UI moved from the form designer to the Search feature; Configure Field → Search remains for designers' inspection. No request workflow (deferred).
* **Repeating groups:** published field entries carry `in_repeating_group`; such fields are `RepeatingGroupNotSearchable` (V1 excludes repeating-group conditions) and moving an approved field into a repeating section invalidates it (`FORM_VERSION_CHANGED`).

### 6C.2 Record-creation categories and pending reconciliation (review B)

| Category | Authority state | Search behaviour |
|---|---|---|
| 1. Creation never accepted (pending; abandoned when the client never retries) | Record registered, accepted content revision 0 | Listed in the user and migration catalogues (inventory complete for G19), never verified, **cannot match**; reported in the scope statement ("N pending creation(s)"). Pending records **do not count against the 500-record verification limit**; they have their own ceiling (`Catalogue:MaximumPendingUserEntries`, 2000; refusal journaled `PENDING_CEILING`), so abandoned creations cannot make an unrelated form unsearchable. |
| 2. Accepted, but business content missing or unverifiable | Accepted head (revision ≥ 1) | Batch verification fails → the Authority quarantines the head → **the whole search fails** (`SEARCH_VERIFICATION_UNAVAILABLE`, `RECORD_VERIFICATION_FAILED`, then `RECORD_QUARANTINED`), never skipped, until privileged reconciliation. |
| 3. Accepted, no longer eligible | Accepted head; registry lifecycle and signed header | Decided only on verified facts: the catalogue contains Active/Quarantined registry entries only; the **signed header** status (Archived hidden unless asked) and parent (related records unless included). |

* **Resumption (category 1):** the same request with the same key completes the creation (F-C7-01, idempotent, fingerprint-checked); a different request with that key is a conflict.
* **Terminal state — decision required (DR-C7-1, not implemented):** marking an abandoned registration terminal is a new Authority state transition. Proposed: an operator/worker security command `AbandonRecordRegistration` allowed only for a record with accepted revision 0 older than a threshold (for example 24 h, well beyond the 10-minute prepared-operation expiry); it moves the resource to `Abandoned` (excluded from user catalogues, kept in the migration population and audit), is idempotent, journaled and archived; a later resumption of that key is refused with a typed error and the client creates anew. Until decided, pending records remain registered at revision 0; they are harmless to search (above) and carry no content.
* **Retention of `form_record_creation_attempts`:** a row is needed while its record has no accepted head (resumption) and, after acceptance, to tell a replay from a conflicting reuse of the key. Rule: keep while the record is pending; after acceptance keep at least 30 days (beyond any client idempotency retry window); purge is a privileged maintenance task (the runtime role cannot delete), never for pending records, never automated in M3.
* No registered record is deleted and no accepted content is repaired from business data.

### 6C.3 Snapshot completeness and audit (review C)

AC-5 guarantees are unchanged: new eligible records, grants and revocations (any authorization change, OD-5) and head changes invalidate snapshots; missing or corrupted accepted records fail; quarantined records fail. Unrelated changes may cause retryable snapshot refusals (measured, §PHASE_C7_STATUS §15). **AUD-C7-01** (asynchronous archive copy of mandatory search-audit events) and **G23 session-event auditing** remain open production blockers.

### 6C.4 M3 executor (owner performance decision C-69)

| Step | Implementation |
|---|---|
| Gate | `FieldSearch:ExecutionEnabled` (default off; the API refuses to start with it outside Development/Testing; on only in `compose.security-test.yaml` and `compose.dev-persistent.yaml`). Off ⇒ `TRUSTED_SEARCH_NOT_ENABLED`. Production approvals stay impossible until PB-08 regardless. |
| Authorization | Capabilities from the Authority (AC-6): every condition's field, value type, operator and Option id must match the caller's approved, `field_searcher`-granted fields — the single denials `SEARCH_FIELD_NOT_ALLOWED` / `SEARCH_OPERATOR_NOT_SUPPORTED`. |
| Admission | At most 4 verified searches per API instance and 1 per user (configurable down); a request that cannot get a slot within 1.5 s is refused `SEARCH_VERIFICATION_UNAVAILABLE` / `SEARCH_CAPACITY_EXCEEDED` (retryable) — never queued without bound. |
| Scope | AC-5 catalogue of the caller's discoverable records of the form (500 contentful records max), every contentful record read from its accepted operation and verified in batches of 50 (HMAC, complete leaf set, root) within 64 MiB and 15 s, snapshot re-validated. One failure fails the search. |
| Values | Evaluated on the decoded content of the very row objects whose canonical bytes the Authority verified (FormRecords port `ReadAcceptedContentAsync`), top-level values only; a record is evaluated only if the caller may read every value item of every predicate field on it (otherwise left out, independent of its values). |
| Semantics | Missing value never matches. Text: NFC-normalized, trimmed, case-insensitive (`equals` whole value, `startsWith`, `contains`). Numbers/dates/instants by value, `between` inclusive, `before`/`after` strict. Option `is` = exactly that option, `includes` = contains it. Booleans `isTrue`/`isFalse`. AND only. |
| Metadata | Status, archived and related filters on the signed header; sort by Authority-held times (registered = created, head change = modified) or verified status; record id as final key. No field-value sort. |
| Retry | At most **one** automatic retry, only for `SEARCH_SNAPSHOT_CHANGED`, only with at least half of the 15 s budget left and a free slot. Limits, denials and integrity failures are never retried. |
| Evidence | Each execution is journaled by the Authority (`FieldSearchExecuted`: field ids, operators, counts, outcome, duration — never values or record ids); a completed search whose evidence cannot be recorded returns no result (`EXECUTION_EVIDENCE_UNAVAILABLE`, retryable). Failures after the catalogue was issued are journaled best effort. Conditions refused before any snapshot (not allowed) are not Authority-journaled (residual). |
| Pagination | The result (sorted matches with verified display facts) travels in an opaque Data Protection cursor bound to organization, user, form, Authority snapshot and authorization revision, expiring with the snapshot (≤ 5 min). Each page re-validates the snapshot with the Authority: any authorization change (revocation included) or change to a record in scope ⇒ `SEARCH_SNAPSHOT_CHANGED`; nothing is re-evaluated or re-read from business rows. |
| Response | The My Records row shape with `projectionVerified = true`, `completeness = "Complete"` and a scope statement; display labels (form and creator names) come from the directory as in My Records. |

### 6C.5 What M3 does not establish

* **G06 OPEN:** adversarial evidence exists on the isolated stack, but no independent review, no Azure staging evidence, and the PB-09 objective is not met under concurrency (§PHASE_C7_STATUS §15).
* **G19 OPEN** (M4), **G21 OPEN** (record editing reads unsigned metadata), **PB-08 OPEN**, **AUD-C7-01 OPEN**.
* Field-value sort, OR, repeating groups, related forms, documents, encrypted values, exports: out of V1.

## 6D. Security Administrator management (owner message 7, C-70)

Normative detail: `docs/security/SECURITY_ADMINISTRATOR_MANAGEMENT.md`.

| Layer | Implementation |
|---|---|
| Role | The existing Authority relation `system_administrator` (`administer_security`); no second role system. |
| Authority commands | `GrantSystemRole` / `RevokeSystemRole` (previously not implemented): actor must be a Security Administrator of the same organization; role fixed to `system_administrator`; target an enabled identity binding; reason; step-up through the shared `FieldSearchApprovalGate` (unavailable in staging/production; synthetic marker only in Development/Testing with the TestIdentityProvider); last administrator protected (`409 LAST_SECURITY_ADMINISTRATOR`). Refusals append `SecurityAdministratorCommandRejected` to the audit stream. |
| Authority read | `POST /internal/v1/security-administrators`: roster from desired state + identity bindings; candidates only for administrators; owners read-only; anyone else 404. |
| Separation of duties (F-C7-02) | Administrator ownership override cannot target the administrator or a team containing them; a Security Administrator cannot add themself to a team; `UpdateMandatoryPolicy` is owner-only. |
| Business | Organizations feature: `GET/POST /api/v1/organization/security-administrators`, `POST …/{userId}/remove` (204 once Effective). No business pre-check: every attempt reaches the Authority. Local audit `SECURITY_ADMINISTRATOR_APPOINTED/REMOVED`. |
| Frontend | Organization settings → Security Administrators card (list, Add, Remove, confirmations, last-administrator rule, empty/denied/loading states, step-up notice). Organization navigation for owners or Security Administrators. |
| First administrator | Only the operator bootstrap at organization creation. For existing organizations: decision **DR-C7-3** (operator-plane first appointment / recovery with dual control) — presented, not implemented. |

## 6E. DR-C7-3 — first and recovery Security Administrator (C-71)

| Layer | Implementation |
|---|---|
| Operator plane | `GET /internal/v1/operator/organizations/{id}/security-administrators`, `POST …/first-appointment`, `POST …/recovery` (operator credential; business workload and user tokens refused). |
| Dual control | `OperatorApprovalVerifier`: two ES256 approvals (compact JWS) from different registered approver keys over the statement digest; public keys only in the Authority; `Unavailable` everywhere but Development/Testing; `SyntheticDevelopment` recorded as not two-human evidence. |
| Service | `SecurityAdministratorProvisioningService`: dry-run preview; inside the organization lock: idempotency on the operation id, approval verification, preconditions (first: no usable administrator; recovery: administrators exist, incident, explicit removals), one journaled command, audit events, alert. |
| Alerts | Port `ISecurityAlertSink`; `LoggingSecurityAlertSink` (event 9100); the alert is journaled on the audit stream first. |
| Development | dev-seed modes `generate-approver-keys` and `operator-security-admin`; `devstack.py security-admin`. |

## 6F. M4 — G19 encryption migration reconciliation (C-73)

Normative detail, residual-exposure inventory, runbook and test map: `docs/security/FIELD_PROTECTION_RECONCILIATION.md`.

| Layer | Implementation |
|---|---|
| Execution | **Unchanged** B-28/A1 reprotection operations; no second migration engine, no bulk SQL, no new write path. A migration whose business-side post-check passes becomes `Verifying` and completes only through the reconciliation. |
| Authority (verifier scheme only) | `POST …/verifier/migration-catalogue/{id}/census` (1..50 entries of the verifier's own snapshot; the shared verification core; counts of the decision's field rows in the old/target form, envelope shape and nonce reuse from the verified bytes; tampering quarantines; first round of every reconciliation re-verifies); `…/{id}/attestation` (computed under the organization lock from the registry population, current heads and census results of those heads; journaled `ProtectionReconciliationAttested`; complete only with nothing quarantined, unverified, uncensused, in the old form or approved for search); `POST …/verifier/protection-coverage` (every current protection decision with its latest attestation). Tables `protection_census_results`, `protection_reconciliation_attestations` (additive migration, no DELETE). Catalogue pages carry the held census outcome (work-list hint only). |
| Business | Worker-only `IProtectionVerifierAuthority` (separate credential, never registered by the API); leased, checkpointed job `field_protection_reconciliations` (MigrationCompletion / VerifyOnly; rounds with catch-up; hand-back of old-form records to the migration; AEAD decrypt check of every target-form ciphertext row; bidirectional business/registry comparison reported as untrusted). Status: `reconciliation`, `authorityReconciledComplete`. Request: `POST …/fields/{field}/protection/reconciliations` (verify-only; same permission as retry; 409 while migrating). |
| Frontend | Edit field → Security & Encryption: Authority-verified badge only on a complete attestation under the current decision; attestation counts, worker observations, basis; **Verify completeness…** with a reason. |

**What M4 does not establish:** G19 stays **OPEN** (independent review, Azure staging, production verifier identity); retained history, retired records, WAL, replicas and backups are not migrated (DR-C7-4); the organization coverage view is verifier-only (DR-C7-5); AUD-C7-01 archival durability unverified.

## 7. Proposed milestones

| Milestone | Content | Status |
|---|---|---|
| M0 | UI, contracts, validation, typed errors, server gate, tests | **Implemented (C-57); owner functional acceptance with M3 (C-72)** |
| M1 | AC-5 Authority catalogue (user scope + migration scope), snapshot validation, batch verification (OD-6), AC5-T01..T15 | **Implemented (C-62, §6A); awaiting owner review** |
| M2 | AC-6 policy + approvals + `field_searcher` grants + Designer "Search" section + organization policy UI, AC6-T01..T12 | **Implemented (C-67, §6B); awaiting owner review** |
| M3 | Trusted executor, paging cursor, execution evidence, ENC-SR-01..18, browser acceptance with real results, performance measurement | **Implemented (C-69, §6C); owner functional acceptance (C-72)** |
| M4 | Worker G19 reconciliation against the migration catalogue (census, attestation, coverage; §6F) | **Implemented (C-73); awaiting owner functional acceptance** |

**Next:** owner functional acceptance of M4. Production approvals still depend on PB-08 step-up; no further milestone is started.

## 8. Open risks

* G06, G19 and G21 remain **OPEN**.
* AC-5, AC-6 and the M4 census/attestation/coverage are Authority changes and need independent production review (like C-12, C-28, AC-1, AC-2).
* Retained plaintext history, retired records, WAL, replicas and backups are outside the M4 scope (DR-C7-4); the verifier credential is a Stage A HS256 credential (production needs a workload identity).
* The D4 limits have not been measured.
* **G22** (C-58): corrected by the hotfix (C-59/C-61); independent review of the authentication pipeline is still required for production assurance.
* **G23** (new): logout is client-side only; stateless session tickets stay valid until they expire. Needs an owner decision.
* Approved plaintext stays readable to a business DBA (ADR-PB-004 §3).
* Unverified display metadata (form name, creator) in result rows keeps the existing caveat.
