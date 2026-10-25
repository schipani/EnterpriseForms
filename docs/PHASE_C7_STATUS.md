# Phase C7 — Advanced Field Search: status

- **Branch:** `phase-c7/advanced-field-search` (from `main` 4c00ce7). Not merged; no PR.
- **Architecture:** `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md`.
- **Decisions:**
  * C-55: checkpoint;
  * C-56: owner decisions D1–D7;
  * C-57: M0 implemented;
  * C-58: G22 found;
  * C-59: G22 corrected;
  * C-60: OD-1..OD-8 approved;
  * C-61: G22 hotfix merged;
  * C-62: M1 implemented;
  * C-63/C-64: G23 hotfix (merged to `main`, integrated here);
  * C-65/C-66: F-C7-01 correction (merged to `main`, integrated here); final AC-5 review;
  * C-67: M2 (AC-6) implemented;
  * C-68: M1/M2 review corrections; C-69: M3 implemented.
- **G06, G19, G21:** OPEN. **G22, G23:** corrected (independent review pending). **PB-08:** not resolved (production/staging search approvals disabled). **PB-09:** objective not met under concurrency (§15.5). **AUD-C7-01 / G24:** open.
- **State (2026-10-24):**
  * M0 implemented (owner visual acceptance **pending**).
  * G22, G23 and F-C7-01 merged to `main` and into this branch (§7, §10).
  * M1 — AC-5 — implemented; final review done (§11).
  * M2 — AC-6 — implemented and verified (§12).
  * **M1/M2 review corrections and M3 — verified field-value search — implemented and verified (§15); stopped for owner functional acceptance.**
  * Field-value search runs only where the fail-closed execution gate is on (isolated Development/Testing); never in staging or production. M4 is not started. This branch is not merged to `main`.

## 1. History

1. **C-55 (9edddb7):** the assessment found that the Authority cannot supply a form-scoped catalogue with content heads at a coherent snapshot, nor hold search approval. Work stopped at the security checkpoint.
2. **C-56:** the owner approved D1–D7 (architecture §1). AC-5 and AC-6 were approved as directions, Option B was rejected, D1 and D5 were modified, and M0 was authorized.
3. **C-57:** M0 implemented. AC-5 and AC-6 were refined into reviewable designs (architecture §3–§5).

## 2. What M0 delivers

* **API** (`Search` feature):
  * `GET /api/v1/form-records/search-capabilities` — server-authoritative; in M0 it says unavailable for every form and offers no fields;
  * `POST /api/v1/form-records/field-search` and `POST /api/v1/form-records/field-search/page` — full request validation, then the server gate, `503 SEARCH_VERIFICATION_UNAVAILABLE` / `TRUSTED_SEARCH_NOT_ENABLED`, `retryable: false`.
  * No business-database access in the Search feature (architecture test).
  * Provisional limits are configurable (`FieldSearch` section) and validated at startup.
* **UI:**
  * My Records: reference/ID search on top; Form | Status and Sort by | Rows per page in two columns; related/archived options; an Advanced Search disclosure in the same card. With a form selected, its server capabilities are loaded and the section says "Not available yet", with every control disabled and the reason stated.
  * The same table shows verified results once execution exists; typed failures are never shown as "no records".
  * PG-069 gallery: a clearly labelled synthetic preview of the enabled layout (Field | Condition, Value beneath, typed controls, up to five AND conditions). It sends nothing.
* **Not delivered** (dependent on AC-6/M2): Form Designer "Search" section; organization search-policy UI.

## 3. Verification

All runs below are on the M0 implementation commit c7604ab; local unless marked hosted.

| Suite | Result |
|---|---|
| .NET solution build (warnings as errors; OpenAPI regenerated, additive only) | Pass |
| .NET tests (`scripts/ci/run_dotnet_tests.py`): Architecture 67, Authority 108, API contract 229 (new: 49 in `PhaseC7FieldSearchTests`), Integrity 34 | **438 / 438 passed** |
| Vitest (all workspaces) — new: condition model 9, failure messages 5, component 7 | **205 / 205 passed** (25 files) |
| ESLint, TypeScript (all workspaces), production web build | Pass |
| Playwright `advanced-field-search.spec.ts` (6) + `responsive.spec.ts` (2, 320 px) against the dev API (5280) with the working-tree frontend | **8 / 8 passed** |
| Playwright `form-control-layout.spec.ts` (updated filter-layout test + 320 px search test) | 2 / 2 passed |
| Playwright `pb14-accessibility.spec.ts` (axe at all widths, includes My Records) | 3 / 3 passed |
| **Hosted** `stage-b-integration` run 40051180610 on c7604ab (isolated stack): integration tests | **100 / 100 passed** |
| **Hosted** same run: full browser suite (desktop + 320 px projects; includes `records-workspace`, `record-lookup`, `form-control-layout`, `pb14-accessibility` and the new specs) | **93 / 93 passed** |
| **Hosted** `security-gate-a` run 40051183432 on c7604ab: SEC-P01..SEC-P12 against the isolated Docker stack | **13 / 13 passed** |
| Playwright M0 specs against the rebuilt development container (`http://127.0.0.1:8091`) | 8 / 8 passed |

Tests that write data were **not** run against the development environment. They run on the isolated hosted stack.

### 3.1 Coverage of the M0 test list (owner §5)

| # | Requirement | Test |
|---|---|---|
| 1 | Approved layout | e2e "My Records keeps reference search on top…", gallery geometry test, `form-control-layout` |
| 2 | Reference/UUID lookup works | e2e (REC lookup beside Advanced Search); `record-lookup.spec.ts` (hosted) |
| 3 | Form and Status filters | `records-workspace.spec.ts`, `record-lookup.spec.ts` (hosted) |
| 4 | Sorting and pagination | `records-workspace.spec.ts` (hosted) |
| 5 | Field and Condition associated | component test (labels inside each "Condition N" group); gallery e2e |
| 6 | Value and messages aligned | gallery e2e: From/To aligned with Field; error below without moving controls |
| 7 | Add condition up to the limit | component test; gallery e2e (five groups, button disabled) |
| 8 | More than five rejected | .NET (handler and HTTP 422), e2e (API 422), model test |
| 9 | Not executable before AC-5/AC-6 | .NET (every well-formed request → 503 gate), component test, e2e (disabled, nothing sent) |
| 10 | No BusinessDb fallback | architecture test `FieldSearchHasNoDirectBusinessDatabaseAccess`; constructor-dependency test |
| 11 | API cannot bypass the gate | .NET HTTP test with a real bearer token; e2e direct API calls with the session |
| 12 | Invalid operator/field handled safely | .NET theory (27 malformed cases, values never echoed); e2e |
| 13 | No predicate values in URLs or logs | .NET captured-log test (Trace level) and response-body check; e2e URL and body checks |
| 14 | Accessibility and keyboard | component tests; e2e keyboard + axe (records and gallery); PB-14 axe suite |
| 15 | Desktop and 320 px | e2e at 1440; `responsive.spec.ts` at 320 |

## 4. Browser preview and acceptance instructions (development environment)

The development application at `http://127.0.0.1:8091` was rebuilt with `devstack.py rebuild api web`. Only those two application containers were recreated; Vault, both databases, SpiceDB, Azurite, keys and data are untouched.

1. Sign in at `http://127.0.0.1:8091` as the owner persona and open **My Records**.
2. **Layout.**
   * The **Search records** box and its **Search** button share a row.
   * Below it, **Form** and **Status** sit side by side; below those, **Sort by** and **Rows per page**.
   * Then the related/archived options, then **Advanced Search** (collapsed).
   * There is one results table.
3. **Exact search still works.** Search a reference such as `REC-017EF770` (one of your acceptance records) or a full id: the table narrows to that record. Clear restores the list.
4. **Advanced Search without a form.** Expand **Advanced Search**: "Choose a form first", and **Search records** is disabled.
5. **Advanced Search with a form.** Choose a form, e.g. *M1 Acceptance – Case intake*:
   * a "Not available yet" badge, and an explanation that searching inside records stays off until the Authority can verify complete results;
   * Field ("No searchable fields"), Condition, Value, **Add condition** and **Search records** are all disabled;
   * no field of your form is offered — the server offers none in M0.
6. **Enabled layout preview.** Open **Component gallery** (navigation) → **Advanced Search (synthetic preview)**. Sample data only; nothing is sent.
   * Choose a Field and a Condition; check that the Value control matches the type (text, number, date, option list, or "No value is needed" for Yes/No).
   * **Between** shows **From** and **To** side by side.
   * **Add condition** up to five; it then disables and says "You can use up to 5 conditions".
   * **Remove** works.
   * **Search records** with an incomplete condition shows the error beneath the control without moving it; a complete search reports "Preview: a search with N conditions would be sent. Nothing was searched."
7. **Keyboard.** Tab to **Advanced Search**, then press Enter or Space to open and close it. In the gallery, open a Field list with Enter, choose with the arrow keys and Enter, and Tab to Condition.
8. **Narrow width.** At 320 px (browser responsive mode), the filters and the condition controls stack into one column without horizontal scrolling.

## 5. Commits and hosted runs

| Commit | Content |
|---|---|
| 9edddb7 | C-55 assessment (docs) |
| c7604ab | M0 implementation; docs C-56/C-57/C-58 |
| *(this docs commit)* | hosted results recorded |

Hosted runs on c7604ab: `stage-b-integration` 40051180610 (success), `security-gate-a` 40051183432 (success). `ci.yml` runs only on pull requests and `main`; its steps (build with OpenAPI diff, architecture/contract/Authority/integrity tests, lint, typecheck, Vitest, web build) were run locally with the results in §3.

## 6. Outstanding decisions and blockers

* Superseded by §9 (current outstanding items).

## 7. G22 security hotfix (C-59, C-61)

* **Defect:** the CSRF middleware ran before the session cookie was authenticated (the default scheme is the bearer), so unsafe cookie-session requests were accepted without `X-CSRF-Token`.
* **Fix:** for non-safe methods the middleware authenticates the session scheme itself. When a valid session cookie is present, it requires that session's own token, also when a bearer is present. No route exemptions.
* **Delivery:** PR #20 (`hotfix/g22-session-csrf`, only this change) squash-merged as `main` **65d25fa**, then merged into this branch (43790b8).
* **Evidence:**
  * required ci 4/4 and CodeQL;
  * integration **106/106** and browser **87/87** (run 40063305131);
  * SEC-P **13/13** (run 40063303932);
  * 22 in-process pipeline cases, of which 18 fail on the unfixed middleware.
* **Development stack:** a cookie-session POST without the token now gets 403.
* **Details:** `docs/security/G22_SESSION_CSRF_HOTFIX.md`.
* **Residual G23 (new, open):** logout only clears the browser cookie; stateless tickets stay valid until they expire. Fixing it needs server-side session state, which is an owner decision.

## 8. M1 — AC-5 Authority record catalogue (C-62)

### 8.1 What was built

See architecture §6A for the full description and the differences from the reviewed design. In summary:

* **Authority:**
  * user-scoped search catalogue;
  * Authority-held snapshots, validated only against current state (OD-5);
  * privileged migration catalogue for the separate `field-protection-verifier` workload (OD-1; never reachable by the business workload; unconfigured ⇒ always denied);
  * batch verification of 1..50 records over the unchanged single-record verification core (OD-6);
  * non-gating, archived `audit` journal stream (OD-8);
  * additive migrations `SearchCatalogueAndAuditStream` and `JournalContentOperationIndex`.
* **Business:** `TrustedScopeVerifier` (D3 fail whole scope, D4 limits), Authority client methods, and a Testing-environment-only proof route. Public field-value search stays gated (`TRUSTED_SEARCH_NOT_ENABLED`).
* **Performance fix found by the benchmark:** the Authority located a record's archive checkpoint by walking back through every content revision of the organization (one query per revision) on every verified read. That made a scope O(N²): before the fix, 150 records took 4.46 s and 480 records exceeded the 15 s budget (correctly refused with `SEARCH_VERIFICATION_LIMIT`, time). It is now one indexed lookup with the same result. This was pre-existing and also affected every ordinary verified record read in large organizations.

### 8.2 Commits

| Commit | Content |
|---|---|
| 906af67 | M1 implementation |
| 03a0c95, 9b187d5, 5181534, 224a53c, fc20b8a | Real-stack test corrections (see §8.5) |
| b146907 | Checkpoint lookup by operation (O(1)), index migration; benchmark drains the archive backlog |
| *(docs commit)* | This documentation |

### 8.3 Verification (final code b146907)

| Suite | Result |
|---|---|
| .NET local (`run_dotnet_tests.py`): Architecture 67, Authority 126 (+18 AC-5 unit), API contract 272 (+16 scope verifier, +4 verifier boundary, +1 Testing-route absence), Integrity 34 | **499 / 499** |
| Hosted `stage-b-integration` run 40075513114: integration (incl. 8 AC-5 real-stack proof tests and the benchmark) | **115 / 115** |
| Same run: full browser suite | **94 / 94** |
| Hosted `security-gate-a` run 40075514763: SEC-P01..SEC-P12 | **13 / 13** |
| Playwright M0 specs against the rebuilt development stack (:8091) | 8 / 8 |

No frontend code changed in M1.

### 8.4 AC5-T01..T15 coverage

| Test | Status | Where |
|---|---|---|
| T01 deleted rows | Pass (real stack) | `SearchCatalogueTests.DeletedValueRowsFailTheWholeScope…` |
| T02 altered value | Pass (real stack) | `…AlteredAndRolledBackValuesFailTheScope` |
| T03 removed from business listing/columns | Pass (real stack) | `…BusinessMetadataManipulation…` |
| T04 injected record / extra row | Pass (real stack) | `…BusinessMetadataManipulation…` |
| T05 rolled-back rows | Pass (real stack) | `…AlteredAndRolledBack…` |
| T06 moved form in business columns | Pass (real stack) | `…BusinessMetadataManipulation…` |
| T07 revocation | Pass (real stack + unit) | `…SnapshotsDieOnRevocation…` |
| T08 edits in / out of scope; new record | Pass (real stack + unit) | `…SnapshotsDieOnRevocation…`, `SearchCatalogueTests` (unit) |
| T09 limit | Pass (real stack + unit) | `…LimitsRefuseWithoutTruncation…` |
| T10 foreign / unknown / expired snapshot | Pass (real stack + unit) | `…LimitsRefuse…`, unit `ExpiredForeignAndUnknown…` |
| T11 business workload on migration catalogue | Pass (real stack + host pipeline) | `…OnlyTheVerifierWith…`, `VerifierBoundaryTests` |
| T12 verifier without a scoping command | Pass (real stack + unit, denial journaled) | same |
| T13 records no user can discover | Pass (real stack + unit) | same |
| T14 lookup paging, projection lag, gate closed | Partial: complete-lookup reuse of C-28 paging (stack page size 20); gate/not-ready mapped (unit); no real Authority outage injected during a search | `PhaseC7ScopeVerificationTests` |
| T15 quarantined head in scope | Pass (unit + real stack: the deleted-row test re-runs on the quarantined record) | `…DeletedValueRows…` |
| Field-value matching, paging, sorting of results | **Pending: M3** (no executor exists) | — |

### 8.5 Test corrections made during hosted verification (tests, not product code)

1. Text fields are Confidential and encrypted by default, so the first tamper attacks hit empty plaintext tables and the verification correctly passed. The tests now attack plaintext date rows and first prove the rows exist.
2. The benchmark's concurrent record creation met the Authority's correct fail-closed back-pressure and revision conflicts. After the idempotency problem in finding F-C7-01 (§8.7), it now creates records sequentially.

### 8.6 Benchmark (PB-09; provisional, not a capacity claim)

Hosted isolated stack (GitHub runner, Docker Compose, Vault Transit, SpiceDB, PostgreSQL ×2), Level 2 controls on, one user scope per form, Accused records of about 3.7 KB of signed rows each. The figure is the server-side elapsed time of the complete verification (catalogue + staged reads + batches + snapshot validation). Raw data: `docs/evidence/phase-c7/c7-m1-benchmark-b146907.json`.

| Records | Runs | p50 | p95 | p99 (= max, small n) | Authority calls | Batches | Verified bytes |
|---|---|---|---|---|---|---|---|
| 50 | 15 | 0.59 s | 0.67 s | 0.67 s | 3 | 1 | 0.18 MB |
| 150 | 12 | 1.82 s | 1.85 s | 1.85 s | 5 | 3 | 0.55 MB |
| 480 | 8 | 6.12 s | 6.15 s | 6.15 s | 12 | 10 | 1.76 MB |

Findings:

* **Cost is linear** after the fix, at about 12.7 ms per record. The 500-record ceiling fits the 15 s budget, extrapolating to about 6.4 s.
* **The PB-09 target of < 3 s** for a verified search is met only up to about 230 records on this infrastructure. D4's 500 records / 15 s are workable engineering limits, but not a < 3 s product experience. The owner should decide before M3 whether to lower the record limit, accept slower complete searches, or invest in a V2 trusted index (separate ADR).
* **Bytes are far below 64 MiB** (≈ 3.7 KB per record), so the byte limit is not the binding constraint for this form shape.
* **Failure behaviour was observed directly:** over budget ⇒ `SEARCH_VERIFICATION_LIMIT` (time); a scope over 500 ⇒ `SEARCH_VERIFICATION_LIMIT` (records). Neither returned a partial result.
* **Not measured:** larger forms (250 fields), many child records or documents, concurrent searches, Azure Managed HSM latency (Vault Transit here), production hardware.

### 8.7 Findings

* **F-C7-01 (pre-existing robustness defect, not security):** a record creation that registered the record but then hit a transient Authority answer (for example `AwaitingArchive`) cannot be completed by an idempotent retry. The record id is derived from the key, but the retry re-registers with the current authorization revision and a fresh header id, so it gets `IDEMPOTENCY_CONFLICT`, and the record may stay registered without content (revision 0). The SPA coordinator retries writes on 503 with the same key, so a user could see this. It fails closed. Not changed in M1; the owner should decide on a fix (for example, deterministic header id and the expected revision stored with the idempotency key).
* **Performance defect (pre-existing):** fixed in b146907 (§8.1).
* **G23 (new):** see §7.

### 8.8 Browser checks to confirm M0 was unaffected (development environment)

The development stack at `http://127.0.0.1:8091` was backed up first (`Documents\EnterpriseForms-dev-backups\enterpriseforms-dev\20261024-014923`) and then rebuilt with `devstack.py rebuild authority api web`. Only those three containers were recreated, and the additive Authority migrations were applied. Vault, the databases, SpiceDB, Azurite, keys and data are untouched.

* Repeat §4 steps 1–8 (M0 layout, exact search, disabled Advanced Search with the explanation, gallery preview, keyboard, 320 px). Nothing in the UI changed in M1.
* G22: everything you do in the browser (editing, submitting, sharing, uploads, publishing, signing out) must keep working. A rejection would show as a 403 "CSRF_TOKEN_INVALID" error.

## 9. Outstanding decisions and blockers (superseded by §14)

See §14.

## 10. G23 and F-C7-01 (C-63..C-66)

### 10.1 G23 — server-side session revocation

* Hotfix PR #21, merged to `main` as ce6c365 and into this branch (e7ac3be). Session state is held by the independent Authority and checked on every cookie request; sign-out revokes it first (`docs/security/G23_SESSION_REVOCATION_HOTFIX.md`, C-63/C-64).
* Hosted on cf100d5: `ci` .NET 430/430, Stage B integration 113/113, browser 89 passed, SEC-P01..SEC-P12 passed.
* **Review requirement:** independent review of the Authority session store and the BFF validation path; session events into the audit stream; production key-ring storage; Entra CAE. G23 is corrected for development, not independently reviewed.

### 10.2 F-C7-01 — partial record creation (C-65)

* **Cause:** `RegisterRecord` was already idempotent. The content step was not: a retry rebuilt a candidate that can never match the first (new item ids, timestamp, AEAD nonces) under the same deterministic operation and key, so the Authority answered `IDEMPOTENCY_CONFLICT` and the registered record could never receive content.
* **Fix (no Authority protocol change):** one insert-only `form_record_creation_attempts` row per derived record id holds an HMAC-SHA-256 fingerprint of the canonical request under a random per-attempt key sealed as a field-encryption envelope (no values, no testable digest in BusinessDb). A retry with the same request completes what exists (accepted head ⇒ the original outcome; staged candidate ⇒ committed as staged and verified by the Authority; expired or aborted ⇒ a fresh, separately keyed attempt); a different request with the same key ⇒ `409 IDEMPOTENCY_CONFLICT`. Nothing is deleted or repaired from BusinessDb data. `docs/architecture/RECORD_CREATION_IDEMPOTENCY.md`.
* **Outcomes 1–9 of the owner's list:** one accepted record; same request ⇒ same outcome; transient failure retryable; genuine conflict rejected; no second Authority version for the same operation; no usable incomplete record; existing content intact; orphans left for approved reconciliation (not built); no deletion or repair from unverified BusinessDb data.
* **Tests (real stack, `RecordCreationIdempotencyTests`, 16):** fault injection at all five checkpoints; request interruption; BusinessDb and Authority failure; commit followed by a lost response; ambiguous retry; concurrent identical and conflicting requests; API restart; missing business data; no unauthorized exposure; error codes distinguishing retryable failures from conflicts. Plus 4 unit tests.
* **Hosted (PR #22):** first head 799f31f — `ci` passed, SEC-P01..SEC-P12 passed (run 40106822212), Stage B integration failed once on the known `RecordHistoryTests` AwaitingArchive timing (a fail-closed retryable read immediately after a write). Test-only fix fd6c257 (the history test retries the retryable read, like a client): `ci` .NET 434/434, Stage B integration **129/129**, browser **89 passed**, level3 passed. Merged as `main` **216a04b**; merged into this branch (fe071ae).
* **Residual:** a registered record whose client never retries stays registered without content (fails closed, never usable); an operator reconciliation listing is a later item. The fingerprint table needs a retention rule (PB-13).

## 11. Final AC-5 (M1) review (C-66)

### 11.1 Snapshot completeness

| Owner case | Test | Result |
|---|---|---|
| New registration | `ANewlyGrantedDiscoverableRecord…` (unit), real-stack snapshot test | New record ⇒ old snapshot invalid; next catalogue includes it |
| Record moving into or out of a form | `ARecordMovingBetweenFormsInTheRegistry…` (unit) | Registry parent change invalidates; the record appears only under its current form |
| Newly granted discoverable record | real-stack snapshot test (reader's pre-grant snapshot invalidated) | Pass |
| Revocation | `…SnapshotsDieOnRevocation…` | Pass |
| New head | content-head change tests (unit + real stack) | Pass |
| Missing or quarantined records | quarantined/revision-0 entries listed with state, never dropped; deleted rows fail the scope | Pass |
| Cross-organization movement or tampering | `ResourcesOfAnotherOrganizationNeverEnter…` (unit), `CrossOrganizationBusinessTamperingCannotMove…` (real stack) | Pass |
| Full migration population | migration-catalogue tests (Registered + undiscoverable records included) | Pass |

Local real-stack run of `SearchCatalogueTests` + `FieldSearchApprovalTests`: 17/17.

### 11.2 Audit reliability — the "never blocks reads" wording

The earlier description ("non-gating audit stream") was imprecise and is corrected here:

* **Issuance blocks on audit.** A catalogue is issued only together with its `SearchCatalogueIssued` event, in one AuthorityDb transaction. If the event cannot be written, no catalogue exists.
* **Refusals are journaled before they are returned** (`SearchCatalogueRefused`, `MigrationCatalogueDenied`, `MigrationCatalogueRefused`), each in its own committed transaction (c7 commit 21efdf2; `RefusedUserAndMigrationCataloguesAreJournaled` unit test, `EveryIssuedOrRefusedCatalogueHasDurableAuditEvidenceThatReachesTheArchive` real-stack test).
* **What is non-gating is the archive copy:** audit entries are archived and anchored asynchronously by the existing processor; archive lag of the audit stream does not close the organization gate (security commands, by contrast, cannot become Effective before archival).
* **Residual — explicit blocker AUD-C7-01 (production):** between commit and archival, catalogue evidence exists only in AuthorityDb (independently administered, but a loss of AuthorityDb in that window loses it). Before production, either mandatory search-audit events must gate on archival as security commands do, or a measured and monitored archive-lag objective must be independently accepted. No competing audit system was added.

### 11.3 Performance (PB-09; engineering measurement, not a capacity claim)

The 500-record ceiling is unchanged. Benchmark `BenchmarkA200RecordScopeSequentiallyAndUnderConcurrency`: 200 Accused records, background edits on another form every 250 ms, concurrency 1/4/8. Typed refusals are counted, never hidden; no run returned a partial scope.

**Local (developer laptop, isolated Docker stack):**

| Concurrency | Attempts | Verified | Retryable refusals | Time-limit refusals | p50 | p95 | p99 (= max) |
|---|---|---|---|---|---|---|---|
| 1 | 21 | 12 | 8 | 0 | 5.94 s | 6.30 s | 6.30 s |
| 4 | 28 | 10 | 12 | 3 | 13.66 s | 14.98 s | 14.98 s |
| 8 | 40 | 0 | 16 | 24 | — | — | — |

**Hosted (GitHub runner, run 40118191733 on 51f2658; raw data `docs/evidence/phase-c7/c7-ac5-benchmark-200-51f2658.json`; the first round of each level is a warm-up and is not measured):**

| Concurrency | Attempts | Verified (measured) | Retryable refusals | Time-limit refusals | p50 | p95 | p99 (= max) |
|---|---|---|---|---|---|---|---|
| 1 | 21 | 20 | 0 | 0 | 2.60 s | 2.69 s | 2.69 s |
| 4 | 28 | 15 | 9 | 0 | 6.73 s | 7.67 s | 7.67 s |
| 8 | 40 | 11 | 25 | 0 | 13.14 s | 15.00 s | 15.00 s |

Findings:

* **Is 3 s achievable at 200 records?** For a single search on the hosted runner, yes, narrowly: p99 2.69 s (≈ 13 ms per record, consistent with M1), with about 0.3 s of headroom. Under concurrency, no: 4 concurrent searches take p50 6.7 s and 8 take p50 13.1 s with p95 at the 15 s budget. On the developer laptop even a single search takes ≈ 6 s. **3 s at 500 records is not achievable** with the V1 per-record verification design and must not be implied.
* **Concurrency degrades roughly linearly with the number of concurrent searches** (verification work is serialized at the Authority and Vault Transit). Hosted, 8 concurrent searches reach the 15 s budget; locally every one of them was refused with `SEARCH_VERIFICATION_LIMIT` (time). No run returned a truncated scope.
* **Unrelated writes cause retryable refusals:** while other records' changes are archiving, the gate is briefly closed and a verification is refused `SEARCH_SNAPSHOT_CHANGED`/`SEARCH_VERIFICATION_UNAVAILABLE` (retryable) — hosted 9 of 28 attempts at concurrency 4 and 25 of 40 at concurrency 8. For M3 this means a bounded retry policy and a user-facing "busy, try again" state, and it should be weighed in the performance decision.
* G06 and G19 stay OPEN.

## 12. M2 — AC-6 search approval (C-67)

### 12.1 What was built

See architecture §6B. In short:

* **Authority:** published-version registration (OD-3), approvals bound to the registered version (only `administer_security`, OD-2 A), Public/Internal plaintext only, operators per type, `field_searcher` query permission (OD-4 A), invalidation on strengthening, republish and policy change, the environment-locked synthetic step-up gate (OD-7), capabilities, and journaled rejections (OD-8). Additive migration `FieldSearchApprovals`.
* **Business:** publish registers the version; search-policy endpoints relay security commands; capabilities and per-condition authorization come from the Authority; execution still refused (`TRUSTED_SEARCH_NOT_ENABLED`); `plaintextSearchPermitted` on the organization policy (additive migration `PhaseC7FieldSearchPolicy`).
* **Frontend:** Configure Field → **Search**; Form permissions → **Who can search field values** (security administrators); Organization settings toggle; Advanced Search fields and conditions from the capabilities with **Search records disabled**.
* **Dev seed:** a "security" persona (Sam Security, System Administrator) for organizations bootstrapped from now on. There is deliberately no operator path that adds identities to an existing organization, so the persistent development organization does **not** get it (§13).

### 12.2 Defects found and fixed during verification

* **False invalidation on republish:** the approval's option snapshot is stored as jsonb, which PostgreSQL reformats; it was compared as JSON text, so every Option approval was invalidated by the next version. Now compared by value (unit regression test simulating the reformatting).
* **Option order:** options were stored sorted by id; they now keep the published order.
* **Cross-organization configuration read:** another organization's security administrator received an empty configuration (200) for this form's id. No data was disclosed, but it differed from an unknown form. The Authority now answers configuration only for an Active form of the caller's own organization (otherwise the same empty answer ⇒ 404).
* **Architecture rule:** search-approval administration may write audit events and read directory display names through the business store, never records (rule narrowed and extended, not removed).

### 12.3 Test coverage (owner's list and AC6-T01..T12)

| # | Owner test | Where |
|---|---|---|
| 1 | Authorized admin approval | Authority unit; `FieldSearchApprovalTests.ASecurityAdministratorApprovesAPublicField…` (real stack); `field-search-approval.spec.ts` |
| 2 | Designer denial | unit; real stack (business API **and** direct Authority command) |
| 3 | Organization owner without security permission denied | unit; real stack (Designer persona = owner + designer) |
| 4 | Cross-organization denial | unit; real stack |
| 5 | Confidential/Restricted denial | unit; real stack |
| 6 | Encrypted field denial | unit; real stack; browser (no approval control, "Encrypted for privacy — not searchable.") |
| 7 | Unsupported operator | unit; real stack (approval and search request) |
| 8 | Form-version substitution | unit; real stack (approval of another form's field; forged registration with mismatched protection refused) |
| 9 | Field identity substitution | real stack (unregistered field ⇒ `FORM_VERSION_UNTRUSTED`) |
| 10 | Tampered dropdown/layout metadata | real stack (DBA relabels, swaps an option id, marks an encrypted field Public/searchable: capabilities unchanged, injected option refused) |
| 11 | Strengthening and invalidation | unit; real stack (`FIELD_STRENGTHENED`, `FORM_VERSION_CHANGED`, `ORGANIZATION_POLICY_CHANGED`) |
| 12 | Search-permission revocation | real stack; browser (withdrawal) |
| 13 | Unauthorized capability discovery | real stack (reader, record owner, designer get no fields; configuration only for designers/admins; searcher list only for admins) |
| 14 | Forged API requests | real stack (forged value type, unapproved field, injected option, direct Authority commands) |
| 15 | Dev-only isolation | unit (`TheDevelopmentStepUpPathCannotBeEnabledInStagingOrProductionOrWithARealIdentityProvider`): startup refused outside Development/Testing or with a real IdP |
| 16 | Production/staging step-up enforcement | unit (default gate refuses every approval `STEP_UP_REQUIRED`); real stack (approval without the step-up and with a forged marker refused) |
| 17 | Correct audit evidence | real stack (committed commands in the authorization stream; `FieldSearchCommandRejected` in the audit stream; business `FIELD_SEARCH_*` events) |
| 18 | My Records and Form Designer regression | full local integration and browser suites; hosted Stage B |

AC6-T01..T08, T10 and T12 are covered as above. **AC6-T09** (revocation mid-search) and **AC6-T11** (forged value rows under another field's id) concern execution and remain M3 tests.

### 12.4 Verification (merged tree: C7 + F-C7-01 + M2)

* **Local:** .NET Authority 159/159, contract 285/285, architecture 67/67, integrity 34/34; frontend lint, typecheck, 206/206 unit tests, build, no generated-client or OpenAPI drift; real-stack integration **145/145** (one container-recreating test excluded locally only, as before); browser **97 passed, 4 skipped** (pre-existing skips).
* **Hosted (dispatched on 51f2658):** `stage-b-integration` run 40118191733 — integration **146/146** (incl. `FieldSearchApprovalTests`, `RecordCreationIdempotencyTests`, the AC-5 completeness, audit and 200-record benchmark tests), browser **101 passed** (incl. `field-search-approval.spec.ts`); `security-gate-a` run 40118194188 — SEC-P01..SEC-P12 **13/13**.
* The C7 branch has no pull request, so the `ci` workflow (pull_request/main only) does not run on it; its checks were run locally as listed.

## 13. Browser acceptance instructions (M0 and M2)

**Development environment (`http://127.0.0.1:8091`)** — needs `scripts/devstack/devstack.py rebuild authority api web` (additive migrations only; Vault, databases, SpiceDB, Azurite, keys and data untouched; back up first as before). After G23 everyone signs in again.

1. **M0 layout (unchanged):** My Records — reference/ID search on top; Form | Status; Sort by | Rows per page; Advanced Search collapsed; one results table. Exact reference search works.
2. **Policy:** as Avery Owner, Organization settings → tick "Permit search approvals for plaintext Public/Internal fields", save.
3. **Republish:** forms published before M2 are not registered with the Authority (OD-3). Publish a form again; the Search tab then shows "published version N".
4. **Search tab:** Form Designer → a field → Configure Field → **Search**. Encrypted fields read "Encrypted for privacy — not searchable." Plaintext Public/Internal fields read "Can be approved", list their conditions, and say approving is done by a security administrator. No control changes encryption.
5. **Approval in the development organization is not possible:** the persistent organization has no system administrator persona (it predates the security persona, and no operator path adds one). This is the expected, documented state; Advanced Search there shows "No searchable fields".
6. **Approval end to end — isolated security-test stack** (disposable; never the development stack): start it from `infrastructure/docker` with `compose.yaml` + `compose.security-test.yaml`, then serve the web app with `cd src/frontend; $env:API_TARGET="http://127.0.0.1:5180"; npm run dev -w apps/web -- --host 127.0.0.1` and open `http://127.0.0.1:5173`. As Avery Owner, permit search in Organization settings, create and publish a form with a plaintext Internal text field, and grant Sam Security create-record permission (Form permissions) so Sam can open it. Sign in as **Sam Security (System Administrator)**, open the form, Configure Field → Search: choose conditions, enter an approval reference, tick "Confirm with the development-only step-up", Approve. Form permissions → "Who can search field values" → grant Casey Clerk. As Casey Clerk, My Records → choose the form → Advanced Search: the approved field and its conditions can be chosen; **Search records stays disabled**. Withdraw as Sam: the field disappears. (`field-search-approval.spec.ts` automates this.)
7. **Gallery** (`/app/gallery`): the synthetic preview is unchanged.
8. Keyboard and 320 px: the Search tab and Advanced Search remain operable.

## 14. Outstanding decisions and blockers (M2; superseded by §18)

* **Owner review and acceptance of M2** (§12, architecture §6B) and of the AC-5 review (§11); M0 visual acceptance (§13).
* **Performance before M3 (§11.3):** < 3 s is not met at 200 records under concurrency and is not achievable at 500; concurrency degrades sharply; retryable refusals during unrelated writes. Options: lower the limit, accept slower complete searches, or a V2 trusted index (separate ADR).
* **AUD-C7-01 (production blocker):** archive copy of mandatory search-audit events is asynchronous (§11.2).
* **PB-08:** production and staging approvals stay disabled until real step-up evidence exists.
* **Security administration UX:** a security administrator needs designer or form-user access to reach a form's Search tab; a dedicated list is not in M2.
* **G06** (execution, M3), **G19** (M4), **G21** (search part addressed for versions published since M2; editing still reads unsigned metadata): OPEN.
* **Independent review** of the Authority changes (catalogue, verifier credential, batch verification, audit stream, session store, AC-6 commands and capabilities) and of G22/G23. No Azure staging evidence; no Level 2 claim.
* Not started: M3, M4. C7 is not merged to `main`.

## 15. M1/M2 review corrections (C-68) and M3 — verified field-value search (C-69)

Architecture §6C has the design. Implementation commit **574ddef** (on `phase-c7/advanced-field-search`); test-only correction of the phone-layout spec and documentation in the following commits.

### 15.1 Review findings and corrections

| # | Finding | Correction |
|---|---|---|
| R1 | A security administrator needed unrelated Designer or form-user access to reach a form's search configuration. | Authority administration listing (`administer_security` only; forms with a registered version, registered title, counts) → `GET /api/v1/organization/search-approvals` → "Search approvals" page; capability `security.administer`. No Designer or record access is granted. |
| R2 | Abandoned creations (accepted revision 0) counted against the 500-record verification limit, so enough of them could make a form unsearchable. | Pending records stay listed (inventory complete) but have their own ceiling (2000, `PENDING_CEILING`); they never block an unrelated search. |
| R3 | The record-creation categories were not defined. | Defined (architecture §6C.2): pending → cannot match, reported; accepted but missing/unverifiable → whole search fails, record quarantined; ineligible → verified header and registry state only. Retention of `form_record_creation_attempts` defined (pending: keep; accepted: at least 30 days; privileged purge only, not automated). |
| R4 | A terminal state for abandoned creations needs a new Authority transition. | **Not implemented — decision DR-C7-1 requested** (architecture §6C.2). |
| R5 | Repeating-section fields could be approved although V1 cannot search them correctly. | Registration records `in_repeating_group`; such fields are `RepeatingGroupNotSearchable`; moving an approved field into a repeating section invalidates it. |
| R6 | Approved operators were returned in sorted storage order. | Business returns them in vocabulary order (UX). |
| R7 | "Search records" was enabled for a form with no searchable fields. | Disabled when no field can be searched. |
| — | AC-5 completeness, AUD-C7-01, G23 session-event auditing | AC-5 guarantees unchanged and re-tested; AUD-C7-01 and G23 session-event auditing remain **open production blockers**. |

### 15.2 M3 summary

Fail-closed execution gate (`FieldSearch:ExecutionEnabled`; startup refused outside Development/Testing); conditions authorized against the Authority's approvals; admission (4 per instance, 1 per user, busy refusal); the AC-5 catalogue and batch verification of every contentful record (500 records / 64 MiB / 15 s, approved ceilings that configuration can only lower); predicates evaluated on the decoded content of the verified rows, only on values the caller may read; verified status/parent filters; sort by Authority-held times; at most one automatic retry on a changed snapshot; mandatory Authority execution evidence; snapshot-bound result cursors re-validated on every page; results in the My Records table with `projectionVerified`. The Advanced Search UI keeps the approved M0 layout.

### 15.3 Tests (owner list §6, ENC-SR, AC6-T09/T11)

| Owner item | Where (real stack unless noted) |
|---|---|
| Approved equality; allowed/disallowed operators; multiple AND; date, numeric, dropdown, boolean | `FieldSearchExecutionTests.ApprovedPredicatesOfEveryType…`; unit `PhaseC7FieldSearchExecutionTests` |
| Conditional capability visibility; unauthorized field and record discovery | `…UnauthorizedRecordsAndUnreadableFieldValues…`, `FieldSearchApprovalTests` (M2) |
| Approval revocation; search-permission revocation mid-query (**AC6-T09**); pagination revocation | `…RevokingSearchPermissionOrWithdrawingApproval…` (open page refused after revocation) |
| Missing accepted record; BusinessDb deletion; tampered values; rollback; field substitution and forged search rows (**AC6-T11**) | `…AHostileChangeToOneAcceptedRecordFailsTheWholeSearch…` × altered / deleted / field-substituted / forged-row / rolled-back |
| BusinessDb injection | `…RowsInjectedForAnUnregisteredRecordNeverEnterTheResult` (+ M1 ENC-SR-10/11) |
| Form/version/field substitution | `FieldSearchApprovalTests.VersionFieldAndMetadataSubstitution…` (M2) |
| Snapshot change during execution | `…SearchesDuringConcurrentWritesAreCompleteOrTypedNeverPartial` |
| Size and time limits; concurrency overload | benchmark (505 records ⇒ `records` limit; time-limit refusals at 480 × 8); `…ConcurrentSearchesOfOneUser…Busy` |
| No partial results after verification failure | tampering tests, benchmark assertions, unit tests |
| No protected values in URLs/logs | `…PredicateValuesNeverReachUrlsErrorsOrLogs` (API and Authority logs, journal); browser specs (URL) |
| Exact-reference search regression | `RecordLookupTests`, `advanced-field-search.spec.ts` |
| Pending vs accepted completeness; abandoned creation; resumption; concurrent retry | `…APendingCreationNeverBlocksSearchesAndIsFoundOnceResumed` |
| Dev-only isolation; production/staging gating | unit: gate and step-up startup refusals (`TheExecutionGateIsADevelopmentCapability…`, M2 gate test) |

ENC-SR status is in `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md`; no case is marked passed without independent review.

### 15.4 Verification

* **Local (developer laptop):** .NET Authority 163/163, contract 297/297, architecture 67/67, integrity 34/34; frontend lint, typecheck, 207/207, build, no generated-contract drift; real-stack search classes 29/31 on the first run (one wrong test expectation, corrected and re-run green; the M1 480-record benchmark exceeded the 15 s budget on this laptop — the typed refusal, see §15.5); browser specs M3 6/6, M2 5/5, M0 8/8, responsive 2/2.
* **Hosted (dispatched on 574ddef):** `stage-b-integration` run 40150640937 — integration **160/160** (including the M3 adversarial suite and both benchmarks), browser **106 passed, 1 failed** (`responsive.spec.ts` still expected the M0 "not available yet" text; test corrected afterwards, see §15.6); `security-gate-a` run 40150642998 — SEC-P01..SEC-P12 **13/13**.
* **Hosted final (dispatched on 95a3729; code identical to 574ddef plus the test-only spec fix and documentation):** `stage-b-integration` run 40153566849 — integration **160/160**, browser **107/107**.

### 15.5 Performance (PB-09; engineering measurement, not a capacity claim)

Complete M3 query (capabilities, authorization, admission, catalogue, verification of every record, validation, evaluation, sort, evidence, first page; second page measured separately). Two- and three-condition queries over String, Integer, Option, Boolean and Date fields; five team members alternating two API instances; edits of another form every 400 ms; one grant (concurrency 4) and one revocation (concurrency 8) mid-level. Hosted run 40150640937; raw data `docs/evidence/phase-c7/c7-m3-benchmark-574ddef.json`.

| Records | Verified bytes | Authority calls per search | 1 search p50 / p95 / p99 | 4 concurrent p50 / p99 | 8 concurrent p50 / p99 | Page 2 p50 | Refusals (8 concurrent) |
|---|---|---|---|---|---|---|---|
| 50 | 0.15 MB | 6 | 0.48 / 0.57 / 0.57 s | 1.75 / 1.89 s | 3.00 / 3.03 s | 13–38 ms | 2 snapshot changed |
| 150 | 0.44 MB | 8 | 1.39 / 1.50 / 1.50 s | 4.34 / 4.52 s | 8.40 / 8.50 s | 23–66 ms | 2 snapshot changed |
| 200 | 0.59 MB | 9 | 1.89 / 1.95 / 1.95 s | 6.02 / 6.05 s | 11.57 / 11.68 s | 25–47 ms | 2 snapshot changed |
| 480 | 1.42 MB | 15 | 4.68 / 4.87 / 4.87 s | 13.64 / 13.79 s | — (all refused) | 29–39 ms | 18 time limit, 6 snapshot changed |
| 505 | — | — | typed `SEARCH_VERIFICATION_LIMIT` (`records`) | | | | |

* **Load at 8 concurrent:** Authority container 78–92 % CPU, Vault 44–58 %, SpiceDB 14–20 %, business PostgreSQL 0–38 %, Authority PostgreSQL 13–16 %; memory 70–300 MB per container. Database load per level is in the raw data (transactions and tuples returned).
* **Throughput:** about 105–140 verified records per second in total, independent of concurrency — latency grows linearly with concurrent searches; the single Authority verification path is the bottleneck.
* **Three-second objective:** met for a single search up to about 300 records on this runner (p99 1.95 s at 200, 4.87 s at 480). **Not met under concurrency** (200 records: 6.0 s at 4, 11.6 s at 8). **PB-09 remains an open performance blocker.** The recommended operational scope of about 200 records works for a few concurrent users; 480 records with eight concurrent searches is refused (time limit) every time.
* **Developer laptop:** about twice as slow (≈ 26 ms per record); at 480 records every search reached the 15 s budget and was refused — typed, never partial.
* **Scaling proposal (decision required, DR-C7-2):** see §18.

### 15.6 Commits

* `574ddef` — M1/M2 review corrections and M3 implementation and tests.
* Following commits — phone-layout spec expectation (test only), documentation and evidence.

### 15.7 Development environment

Untouched in this round: the development stack (`http://127.0.0.1:8091`), its Vault, databases, SpiceDB, Azurite, keys, forms and records. All destructive and adversarial testing ran on the disposable security-test stack.

## 16. Browser acceptance instructions (M3)

**Where:** an isolated organization with synthetic identities. The persistent development organization predates the "Sam Security" persona and cannot approve fields (no operator path injects privileged identities into an existing organization, by design). Use the disposable security-test stack:

1. `infrastructure/docker`: `docker compose -p enterpriseforms-sectest -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait`; then `cd src/frontend; $env:API_TARGET="http://127.0.0.1:5180"; npm run dev -w apps/web -- --host 127.0.0.1` and open `http://127.0.0.1:5173`.
2. **Owner (Avery Owner):** Organization settings → tick "Permit search approvals for plaintext Public/Internal fields". Create a form with a plaintext Internal text field, a number, a date and a dropdown; publish; give Casey Clerk create permission.
3. **Clerk:** create several records with different values.
4. **Security administrator (Sam Security):** navigation **Search approvals** → the form → Review fields → for each field choose conditions, enter an approval reference, tick the development-only step-up, Approve; "Who can search field values" → grant Casey Clerk. Check that Sam has no record access (My Records shows none of the clerk's records).
5. **Clerk, My Records:** reference/ID search on top; Form | Status; sorting and rows per page; Advanced Search collapsed.
   * Choose the Form → expand Advanced Search: only approved fields are offered; each field offers its own conditions; the value control matches the type.
   * Search text (Is exactly / Starts with / Contains), a number range, a date, a dropdown value; add a second condition (AND).
   * Results appear in the same My Records table (Actions rightmost, statuses, paging); change Rows per page and use Next page; Clear advanced search returns to the list.
   * The address bar never contains the values.
6. **Unauthorized records:** sign in as Riley Reader (grant search access in step 4 if you like): no clerk record appears.
7. **Failure states:** these occur only under database tampering or overload (proven by the integration tests); `field-search-execution.spec.ts` shows them with replaced responses: "A record could not be verified" (no retry), "Search is busy" (retry), "Records changed during the search" (Run search again) — never "No records match", and the conditions stay.
8. **320 px and keyboard:** narrow the window; tab through the filters, conditions and buttons.
9. **Regression:** open, edit and save a record; the record editor and My Records list behave as before.
10. **Development stack (`http://127.0.0.1:8091`), optional:** `scripts/devstack/devstack.py rebuild authority api web` (back up first; additive migrations only). Search approvals cannot be granted there (no security persona), so Advanced Search shows no searchable fields; everything else must be unchanged.

## 17. Production gating

* Execution: `FieldSearch:ExecutionEnabled` refuses startup outside Development/Testing.
* Approvals: production and staging stay disabled until PB-08 step-up evidence exists; the synthetic path refuses startup outside Development/Testing or with a real identity provider.
* A running development search is not evidence that production use may be enabled. No Level 2 claim.

## 18. Outstanding decisions and blockers (M3; superseded by §21)

* **Owner functional acceptance of M3** (§16) and of the review corrections (§15.1). M0/M2 visual acceptance not yet confirmed by the owner.
* **DR-C7-1 (decision):** terminal state for abandoned record creations (`AbandonRecordRegistration`, architecture §6C.2).
* **DR-C7-2 (decision, PB-09):** scaling the verified search. Measured throughput is about 130 verified records per second per Authority. Options for an independently reviewed design:
  1. **Authority-side verified-head digest cache:** HMAC-verify each accepted head's leaves once, keep the verified leaf digests bound to (record, committed operation, manifest root, key version); later searches compare the business bytes' SHA-256 to those digests instead of re-running HMAC. Large reduction of Vault/HSM work; needs cache invalidation proof and review.
  2. **Horizontal verification:** several Authority verification workers behind the same independently administered store and HSM, with admission sized to them.
  3. **V2 independently maintained search projection** of approved fields, revision-fenced (ADR-PB-004 option; separate ADR).
  Until decided: keep the 500/64 MiB/15 s limits, recommend about 200 records, and keep PB-09 open.
* **AUD-C7-01 / G24:** asynchronous archive copy of mandatory search-audit events. **G23 session-event auditing:** open.
* **PB-08:** production/staging approvals disabled.
* **G06** (adversarial evidence exists on the isolated stack; independent review and Azure staging evidence outstanding), **G19** (M4), **G21** (record editing reads unsigned metadata): OPEN.
* **Residuals:** conditions refused before a snapshot exists (not allowed) are not Authority-journaled; failed-search evidence after issuance is best effort; display labels (form and creator names) come from the unsigned directory, as in My Records.
* **Independent review** of all Authority changes in C7 and of the executor; no Azure staging evidence. M4 not started. C7 not merged.

## 19. Security Administrator management (owner message 7, C-70)

Normative description: `docs/security/SECURITY_ADMINISTRATOR_MANAGEMENT.md`; architecture §6D.

### 19.1 Inspection

* "Security Administrator" is the Authority relation `system_administrator` (`administer_security`, capability `security.administer`). There is one role system; no second one was added.
* The development organization has none because organization roles were granted only by the one-shot operator bootstrap at creation, and the organization predates the Sam Security persona. `GrantSystemRole` / `RevokeSystemRole` existed only as unimplemented command kinds. **No approved operation can appoint a first Security Administrator in an existing organization.**

### 19.2 What was built

* Authority: `GrantSystemRole` / `RevokeSystemRole` (Security Administrator only, same organization, `system_administrator` only, enabled verified binding only, reason, step-up via the shared gate, last-administrator protection, refusals on the audit stream); roster read `POST /internal/v1/security-administrators`.
* Separation of duties (F-C7-02 → G25, corrected): no ownership override to the administrator or their team; no team self-join by a Security Administrator; mandatory policy owner-only.
* Business (Organizations): `GET/POST /api/v1/organization/security-administrators`, `POST …/{userId}/remove` (204 once Effective). Every attempt goes to the Authority (no business pre-check), so refusals are independently audited.
* UI: Organization settings → Security Administrators (list with verified identities, Add from verified members, Remove with confirmation, last-administrator rule, read-only for owners, empty-state and step-up notices). Organization navigation for owners or Security Administrators.

### 19.3 Not built — decision DR-C7-3 (G26)

The first or a recovery Security Administrator for an **existing** organization needs a new privileged operator contract. Proposal, security implications and recovery: `SECURITY_ADMINISTRATOR_MANAGEMENT.md` §6. Not implemented, by instruction; owner decision required.

### 19.4 Defect found and fixed during verification

* Self-removal (allowed while another administrator remains) committed, but the handler then re-read the roster the caller could no longer see and answered 403. Appoint and remove now return 204; the UI refetches.

### 19.5 Tests (owner list §7)

| # | Test | Where |
|---|---|---|
| 1, 5 | administrator appoints a verified member; appointee appoints/removes | integration `ASecurityAdministratorAppointsAMember…`, `RemovalTakesEffect…`; browser |
| 2, 3 | owner self-elevation and owner/designer/member appointments refused, through the API and directly at the Authority; repeated bootstrap creates nobody | integration `OwnersDesignersAndMembersCannotAppoint…`; unit |
| 4 | cross-organization appointment, removal and roster refused | integration `AnotherOrganizationsAdministrator…` |
| 6, 7 | removal; last administrator `409` | integration, unit, browser |
| 8 | appointee uses Search approvals at once | integration, browser |
| 9, 10 | no record content; no Form Designer rights (no `forms.design`, designer create 403, draft refused) | integration |
| 11, 12 | removal ends privileges on the same bearer token and on live browser sessions | integration `RemovalTakesEffect…`, `BrowserSessions…` |
| 13 | committed commands, Authority refusal events (`SecurityAdministratorCommandRejected`), business audit events | integration |
| 14 | missing, forged, expired credentials → 401; step-up missing or wrong → `STEP_UP_REQUIRED` | integration `StepUpAndAValidCredentialAreRequired` |
| 15, 16 | G22 CSRF on the new endpoints; G23 signed-out cookie replay refused | integration `BrowserSessions…` |
| 17 | synthetic step-up cannot start in staging/production or with a real IdP; Unavailable gate refuses | unit (shared gate) |
| 18 | My Records and Advanced Search unchanged | integration search/workspace classes; full browser suite |

### 19.6 Verification

Local (laptop; disposable security-test stack rebuilt from this tree):

* .NET unit/contract: Authority 171/171 (8 Security Administrator tests), API contract 297/297, architecture 67/67, integrity 34/34; solution build 0 warnings, 0 errors.
* Frontend: typecheck, lint, 207/207 unit tests; OpenAPI and generated client regenerated; spec manifest regenerated, strict validation pass.
* Real stack: `SecurityAdministratorTests` 6/6 and `RecordHistoryTests` 5/5 after the last fix; related classes (search approval and execution, CSRF, session revocation, record workspace, authorization/tamper, sharing, form access, branding, search catalogue) 59/60 — the single failure is the 480-record catalogue benchmark exceeding the 15 s time limit on the laptop (known ≈2× slower than hosted; it passes hosted), not a functional change.
* Browser: full suite 105 passed, 4 skipped (contributor-writes specs; that switch is off by default), including `security-administrators.spec.ts` 2/2.

Hosted on `4685637` (workflow_dispatch, branch has no PR): `stage-b-integration` run 40165973262 — integration tests (real stack) **166/166**, browser end-to-end (real stack) **109 passed**, aggregate gate job success; `security-gate-a` run 40165974987 — SEC-P01..SEC-P12 against the isolated Docker stack **13/13**.

### 19.7 Development environment

Not changed: no membership, role, grant, key, form or record change in the development organization (`http://127.0.0.1:8091`); its Vault, databases, SpiceDB and Azurite untouched. All tests ran on the disposable security-test stack.

## 20. Browser acceptance instructions (Security Administrators and Advanced Search)

**Where:** the disposable security-test stack (its organization is bootstrapped with the Sam Security persona). The development organization cannot be used until DR-C7-3 is decided and implemented.

1. Start: `infrastructure/docker`: `docker compose -p enterpriseforms-sectest -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait`; then `cd src/frontend; $env:API_TARGET="http://127.0.0.1:5180"; npm run dev -w apps/web -- --host 127.0.0.1`; open `http://127.0.0.1:5173`.
2. **Avery Owner → Organization:** the Security Administrators section lists Sam Security, read-only (no Add, no Remove).
3. **Sam Security → Organization:** Remove is disabled for the only administrator. *Add administrator* → choose Casey Clerk → reason → tick the development-only step-up → Appoint. Casey appears.
4. **Casey Clerk:** navigation shows Search approvals and Organization; Casey sees no other records and cannot design forms.
5. **Advanced Search:** as Avery, permit plaintext search in the security policy and publish a form with plaintext Internal fields (as §16 step 2); as Casey or Sam, Search approvals → the form → approve fields with conditions, reference and step-up → grant Riley Reader (or Casey) search access; as that person, My Records → choose the form → Advanced Search (§16 steps 5–9).
6. **Sam Security:** Remove Casey (reason, step-up). Casey's next page load loses Search approvals and Organization.
7. 320 px and keyboard: the section, its table and both dialogs.

## 21. Outstanding decisions and blockers (superseded by §24)

* **Owner functional acceptance** of M3 (§16), the review corrections (§15.1) and Security Administrator management (§20). M0/M2 visual acceptance not yet confirmed by the owner.
* **DR-C7-3 (G26):** first and recovery Security Administrator for existing organizations — required before the development organization can approve fields.
* **DR-C7-1**, **DR-C7-2 (PB-09)**: unchanged (§18).
* **G25:** independent review of the separation-of-duties correction.
* **PB-08:** no step-up, so Security Administrators and approvals cannot be changed in staging or production.
* **G06, G19, G21, G24:** OPEN. Independent review of all C7 Authority changes; no Azure staging evidence; no Level 2 claim. M4 not started. C7 not merged.

## 22. DR-C7-3 — first and recovery Security Administrator (C-71)

Normative description: `docs/security/SECURITY_ADMINISTRATOR_MANAGEMENT.md` §6–§7; architecture §6E.

### 22.1 What was built

* **Authority operator plane** (operator credential only): `GET …/organizations/{id}/security-administrators` (members and administrators, identities only), `POST …/first-appointment`, `POST …/recovery`. Dry run first; execution adds or removes only the `system_administrator` relation through one journaled security command (`ProvisionFirstSecurityAdministrator` / `RecoverSecurityAdministrator`), inside the organization lock, single-use operation id, idempotent replay.
* **Dual control** (`OperatorApprovalVerifier`): exactly two ES256 approvals from different registered approver keys, bound to the statement digest (organization, member identity, role, kind, justification, incident, recovery scope, operation), unexpired, lifetime ≤ 15 minutes. The Authority holds public keys only. Mode `Unavailable` (default; the only mode outside Development/Testing) refuses execution `OPERATOR_DUAL_CONTROL_UNAVAILABLE`; `SyntheticDevelopment` is refused at startup outside Development/Testing and is recorded as not two-human evidence.
* **Recovery**: separate endpoint, incident reference, explicit removals, refused where no administrator exists (`RECOVERY_NOT_APPLICABLE`), critical alert.
* **Evidence**: authorization-stream command (approver ids and approval digests, never the approvals); audit-stream `SecurityAdministratorFirstAppointed` / `SecurityAdministratorRecovered`, `SecurityAlertRaised`, `SecurityAdministratorProvisioningRejected`; structured alert log event 9100.
* **Development procedure**: `devstack.py security-admin status | enable-approvals | appoint | recover` driving new dev-seed modes inside the stack network; per-environment synthetic approver keys in the custody folder; typed confirmation of organization id and `APPOINT`.
* No business API route, UI or session can reach these operations. No migration.

### 22.2 Tests (owner list §7)

| # | Test | Where |
|---|---|---|
| 1 | first appointment where none exists | integration `TwoIndependentApprovalsAppoint…`; unit |
| 2 | refused when an administrator exists (and after the first one) | integration, unit |
| 3, 4 | two distinct approvals; same approver twice refused | integration `ForgedExpiredMisbound…`; unit |
| 5, 6 | forged (other key under a registered id, unregistered approver), expired, too long-lived | integration, unit |
| 7, 8 | approval for the wrong organization / wrong member / tampered claim | integration, unit |
| 9 | replayed operation returns the recorded outcome; same id for someone else is a conflict | integration, unit |
| 10 | concurrent first appointments → exactly one administrator | integration `ConcurrentFirstAppointments…` |
| 11, 12 | non-member, other organization's member, identity mismatch, mismatched confirmation, unknown organization | integration `NonMembersOtherOrganizations…`; unit |
| 13 | owner self-elevation still refused in an organization without an administrator | integration |
| 14 | business workload, business workload + user assertion, user token, no credential refused on both operations; no business API route | integration |
| 15, 16 | recovery: incident required, first-appointment approvals rejected, named administrator removed, audit and critical alert (journal and authority log) | integration `RecoveryNeedsAnIncident…`; unit |
| 17 | synthetic approvals refused at startup in Production/Staging/Preproduction; Unavailable mode refuses execution | unit |
| 18 | development path works only with the synthetic mode on the isolated stack; devstack procedure end to end on the disposable stack | integration; devstack shim (§22.3) |
| 19 | appointee's existing token gains the role at once; removed administrator's token loses it at once | integration |
| 20 | appointee: no `forms.design`, no designer create, no record content | integration |
| 21 | G25 regression | `SecurityAdministratorTests` (unit and integration) |
| 22 | Search approvals and Advanced Field Search preserved | `FieldSearchApprovalTests`, `FieldSearchExecutionTests`, browser suite |

### 22.3 Verification

Local (laptop; disposable security-test stack rebuilt from this tree with the synthetic approvers of `compose.security-test.yaml`):

* .NET: Authority unit 178/178 (7 new DR-C7-3 tests), API contract 297/297, architecture 67/67, integrity 34/34; solution build 0 warnings, 0 errors.
* Real stack: `SecurityAdministratorProvisioningTests` 5/5; regression classes (Security Administrators/G25, search approval and execution, CSRF, session revocation, authorization/tamper, sharing, form access, record workspace, record history) 58/58; SEC-P01..SEC-P12 13/13.
* Browser: full suite 103 passed, 4 skipped (contributor-writes switch off), 1 failed — `dropdowns-rules.spec.ts` clerk round-trip (Phase C2; no frontend change in C-71); rerun of that spec 5/5 passed.
* Development procedure: `devstack.py security-admin` exercised end to end against the disposable stack with a test shim that only redirects the Compose project (status, appoint dry run and refusal, wrong organization id and missing `APPOINT` change nothing, confirmed recovery executes and is shown by status, approver-key generation) — 9/9.
* Frontend unchanged in C-71 (C-70 results stand: 207/207, typecheck, lint).

Hosted: HOSTED_PLACEHOLDER_C71

### 22.4 Development organization

Not changed. No dev image was rebuilt, no approver key generated, no appointment made. Procedure for the owner: §23.

## 23. Instructions — appoint your existing account in the development organization

From the repository root, with the development stack running (`python scripts/devstack/devstack.py up`):

1. `python scripts/devstack/devstack.py backup` (recommended; stops and restarts the stack).
2. `python scripts/devstack/devstack.py rebuild authority api web` (new images; data untouched; no migration).
3. `python scripts/devstack/devstack.py security-admin status` — check that **owner / Avery Owner (Organization Owner + Designer)**, subject `dev-owner`, is listed as a member and that there is no Security Administrator.
4. `python scripts/devstack/devstack.py security-admin enable-approvals` (once).
5. `python scripts/devstack/devstack.py security-admin appoint --persona owner --justification "First Security Administrator for the development organization"` — read the dry run (organization, member, identity, role, consequences).
6. Repeat step 5 with `--execute`; type the organization id shown, then `APPOINT`.
7. Browser `http://127.0.0.1:8091`: sign out and in as Avery Owner. **Organization** shows the Security Administrators section with you listed; **Search approvals** appears.
8. Search: Organization → security policy → permit plaintext search for Public/Internal fields; publish a form with plaintext Internal fields; Search approvals → the form → Review fields → approve (conditions, reference, development step-up) → "Who can search field values" → grant Casey Clerk (or yourself); as that person, My Records → choose the form → Advanced Search.
9. Optional: Organization → Security Administrators → Add administrator → choose Casey Clerk (reason, step-up); then remove (last-administrator rule shown when only one remains).

If any step stops, nothing was changed; report the message.

## 24. Outstanding decisions and blockers

* **Owner acceptance** of M3 (§16), the review corrections (§15.1), Security Administrator management (§20) and DR-C7-3 in the development organization (§23). M0/M2 visual acceptance not yet confirmed.
* **G26:** DR-C7-3 implemented; production needs PIM-backed approver identities (not implemented — production fails closed) and independent review.
* **G25:** independent review of the separation-of-duties correction.
* **DR-C7-1**, **DR-C7-2 (PB-09)**: unchanged (§18).
* **PB-08:** no step-up, so Security Administrators and approvals cannot be changed in staging or production.
* **G06, G19, G21, G24:** OPEN. Independent review of all C7 Authority changes; no Azure staging evidence; no Level 2 claim. M4 not started. C7 not merged.
