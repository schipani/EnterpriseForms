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
  * C-68: M1/M2 review corrections; C-69: M3 implemented;
  * C-70/C-71: Security Administrators, DR-C7-3; C-72: owner functional acceptance of Advanced Field Search and the two-column layout; C-73: M4 implemented; C-74: development incident investigated (no cleanup executed), Playwright isolation guard, missing M4 tests, resumable rounds.
- **G06, G19, G21:** OPEN (G19: M4 reconciliation implemented, review pending — §27). **G22, G23:** corrected (independent review pending). **PB-08:** not resolved (production/staging search approvals disabled). **PB-09:** objective not met under concurrency (§15.5). **AUD-C7-01 / G24:** open.
- **State (2026-10-24):**
  * M0 implemented (owner visual acceptance **pending**).
  * G22, G23 and F-C7-01 merged to `main` and into this branch (§7, §10).
  * M1 — AC-5 — implemented; final review done (§11).
  * M2 — AC-6 — implemented and verified (§12).
  * **M1/M2 review corrections and M3 — verified field-value search — implemented and verified (§15); stopped for owner functional acceptance.**
  * **2026-10-29 (later): development incident inventory and remediation plan (no remediation executed), Playwright isolation, missing adversarial tests, DR-C7-4/5/6 designs (C-74, §31–§35); stopped for the owner's approval.**
  * Field-value search runs only where the fail-closed execution gate is on (isolated Development/Testing); never in staging or production. This branch is not merged to `main`.
  * **2026-10-29: owner functional acceptance of M3 and the layout (C-72, §26); M4 — encryption migration reconciliation — implemented (§27–§29); stopped for owner functional acceptance.**

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

Hosted on `f1e54b1` (workflow_dispatch; the branch has no PR): `stage-b-integration` run 40176067535 — integration tests (real stack) **171/171**, browser end-to-end (real stack) **109 passed**, aggregate gate job success; `security-gate-a` run 40176069794 — SEC-P01..SEC-P12 **13/13**.

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

## 25. My Records search visual styling (owner reference image, presentation only)

* **Layout:** two cards side by side, "Find and filter records" and "Advanced Search", at 3fr/2fr with a 16px gap (measured 60.0/40.0 at 1440 px), top-aligned and not forced to equal height; one shared results table below. The split is a container query on the search area (52rem), so it follows available width; below it the panels stack (filters first). Advanced Search is no longer a disclosure on My Records: it is always visible, under a heading with the sliders icon (`ActionIcons.filters`). The gallery preview keeps its disclosure (`collapsible` prop). In the stacked panel Field, Condition and Value are one column, Add condition and Search records are full width, and a lone condition has no frame (its legend is screen-reader only).
* **Styling:** scoped under `.records-search` / `.records-results` in `apps/web/src/app/styles.css` (no other screen changes): Inter-first stack, 16px panel headings, 13px labels, 14px control text, 12px help, 40px controls and buttons, 8px control radius, 12px card radius, 20px card padding, 13px/600 table headings. Colours still come from the theme tokens.
* **Deliberate differences from the reference:** the input border stays the validated `--ui-input-border` (#64748b), not #D1D5DB (1.5:1, fails WCAG 1.4.11); secondary/placeholder text stays #334155 (token) rather than #6B7280/#9CA3AF; Inter is listed first but not bundled (falls back to the existing stack); the left field keeps its label "Search records" and its help text (existing tests and the label/help standard); a "related records" checkbox and an archive explanation remain; the table keeps its Record/Status/Created/Last changed columns.
* **Evidence:** `docs/ui/my-records-search-1440.png` and `docs/ui/my-records-search-320.png` (real browser, development stack). 320 px: no horizontal overflow. Frontend: lint, typecheck, vitest 207/207. Browser (dev stack): advanced-field-search 6/6, record-lookup and responsive specs pass; the field-search-approval/execution specs (updated to drop the disclosure click) were not re-run here. No Authority, search, API or contract change. C7 not merged; M4 not started.

## 26. Owner functional acceptance of Advanced Field Search (C-72)

The owner personally tested Advanced Field Search and confirmed it works correctly, and accepted the two-column My Records search layout (§25). Recorded as functional acceptance for continued development only (C-72). M4 preserves that design and every M3 behaviour; no My Records or search code was changed.

## 27. M4 — encryption migration reconciliation (C-73)

Normative document: `docs/security/FIELD_PROTECTION_RECONCILIATION.md` (design, categories, residual exposure, runbook, test map). Architecture §6F.

### 27.1 Checkpoint result

* Execution of approved strengthening (B-28) and governed removal (A1) already existed and is unchanged: reprotection operations through the Authority's content protocol under the decision's command. No second migration engine.
* Missing and now built: an independent expected population (AC-5 migration catalogue, verifier workload), an Authority **census** that verifies each accepted record and counts the field's rows from the verified bytes, an Authority **attestation** that decides completeness from Authority state only, organization **coverage**, the Worker job, status, request and UI.
* Not built (needs decisions): retained-history disposal (DR-C7-4), Security Administrator coverage view (DR-C7-5). The census/attestation/coverage are verify-and-count operations within D1(7)/OD-1/OD-8; the only state change they make is the existing quarantine-on-tamper rule.

### 27.2 What was built

| Layer | Change |
|---|---|
| Authority | `ContentService.VerifyForProtectionCensusAsync` (shared verification core + counts), `ProtectionReconciliationService` (census, attestation, coverage), verifier routes `…/census`, `…/attestation`, `/verifier/protection-coverage`; catalogue pages carry the held census outcome; migration `20261029122643_ProtectionReconciliation`; `RowCanonicalColumn.AsBytes()` (shared, additive) |
| Business | `IProtectionVerifierAuthority` + `ProtectionVerifierHttpClient` (Worker only); `FieldProtectionReconciliationService` + `FieldProtectionReconciliationWorker`; migration state `Verifying`; `field_protection_reconciliations` (migration `PhaseC7ProtectionReconciliation`); status `reconciliation` / `authorityReconciledComplete`; `POST …/protection/reconciliations` |
| Frontend | Security & Encryption panel: Authority-verified badge, attestation counts, worker observations, basis, Verify completeness… |
| Infrastructure | security-test Worker verifier credential; dev-persistent opt-in (`devstack.py protection-verifier enable`) |

### 27.3 Defect found and fixed during verification

The first verify-only implementation reused a census the Authority already held for an unchanged head, so business rows tampered with after an earlier census were vouched for. Every reconciliation's first round now re-verifies (`reverify`); reuse is limited to catch-up rounds of the same reconciliation. Covered by the rollback integration test and an Authority unit test.

## 28. Verification, performance and residual exposure

### 28.1 Tests (local, isolated security-test stack; hosted results in §28.4)

| Suite | Result |
|---|---|
| Authority unit `ProtectionReconciliationTests` (new, 12) | pass — census counts from verified bytes (repeating occurrences), registry population (undiscoverable, pending, quarantined, retired), tampered/missing/rolled-back rows quarantined, earlier-head census never counts, reuse vs `reverify`, snapshot ownership, superseded decision, envelope shape and nonce reuse, retired key, active approval, Decrypt direction, audit journal without record ids, coverage across forms, cross-organization |
| Contract `ProtectionVerifierBoundaryTests` (new, 6) | pass — API never registers the verifier; partial/reused credentials refused; unconfigured fails closed |
| Stage B `ProtectionReconciliationTests` (8 at C-73; 14 since C-74, §32) | pass — complete population with archived, older-layout and pending records, decrypt checks, kept history, journal and log scans; concealed record (moved + relabelled) blocks a false completion while the business-side post-check alone passed; forged business row ignored; post-completion rollback detected by verify-only, quarantined, never repaired; Authority outage → Blocked → resumes; worker restart mid-run; concurrent edits caught up; permissions and verifier-only routes; organization coverage across forms; benchmark |
| Stage B regression (FieldProtectionMigration, FieldProtectionDowngrade, Encryption, SearchCatalogue, FieldSearchApproval) | 36/36 |
| .NET CI script (architecture, Authority, contracts, integrity) | 594/594 |
| Frontend lint, typecheck, vitest | pass; 207/207 |
| Browser (Playwright) | **not verified** — the local runs hit the development stack (§28.5); hosted run pending |

Owner §11 list → tests: `docs/security/FIELD_PROTECTION_RECONCILIATION.md` §10. The four tests pending at C-73 (policy change, missing key, 50,000 limit, archive outage) were added in C-74 (§32).

### 28.2 Performance (PB-09 engineering measurement on the developer laptop; not a capacity claim)

`docs/evidence/phase-c7/c7-m4-benchmark-local.json`:

| Records | Migration + completion reconciliation | Completion reconciliation | Verified records/s | Verify-only full re-verification (min / p50 / max) | Worker restart mid-run → complete | Authority calls per round |
|---|---|---|---|---|---|---|
| 100 | 22.3 s | 3.3 s | 30.6 | 4.2 / 4.3 / 4.3 s | 9.1 s | 6 |
| 250 | 49.3 s | 8.9 s | 28.2 | 7.4 / 8.5 / 8.5 s | 14.2 s | 9 |

* Linear in records; ≈ 30 verified records/s end to end (the worker reads each record's staged rows, decrypt-checks, and the Authority verifies every HMAC through Vault Transit). The search path verifies ≈ 130 records/s because it does not decrypt; reconciliation is a background job, not an interactive operation, and completeness is never traded for speed.
* At this rate the 50,000-record migration catalogue ceiling would take ≈ 28 minutes per round, beyond the 15-minute migration snapshot lifetime. Superseded by C-74 (§33): rounds now resume after a snapshot expiry, so such a form completes over several rounds instead of ending `Incomplete`.
* CPU/memory: not instrumented separately; the Authority (HMAC verification) and the worker (decryption) are the expected costs. Hosted measurements in §28.4.

### 28.3 Residual plaintext and historical exposure

See `docs/security/FIELD_PROTECTION_RECONCILIATION.md` §7. In short: only current accepted versions are migrated and attested. Earlier revisions (and their indexed plaintext rows), abandoned candidates, retired records, WAL, replicas, backups and exports keep the old form; they are counted and reported, never deleted, never claimed (DR-C7-4).

### 28.4 Hosted results

**Superseded by §37** (hosted runs on e41b1d0, all successful). At C-73: **not run.** The `gh` CLI is not installed in this session, and dispatching through the GitHub API with the stored credential was refused by the session's permission policy (not bypassed). Manual dispatch steps: §34.

### 28.5 Incident — browser tests ran against the persistent development stack (2026-10-29)

* **What happened:** a Vite dev server already listening on `127.0.0.1:5173` (started 07:50 local, proxying to the persistent development API) was reused by Playwright (`reuseExistingServer: true`). Both local Playwright runs of this session — the two encryption specs and the full suite — therefore ran against the **owner's development organization**, not the isolated stack. The persistent stack runs the pre-M4 images (no verifier), so no M4 code ran there.
* **Effect (read-only audit query, 13:49:37–14:09:06 UTC):** 428 audit events; 30 synthetic Playwright forms and 45 synthetic records created (**corrected in C-74: 28 forms and 41 records**; the other two forms predate the runs — exact inventory in `docs/security/C7_M4_DEVELOPMENT_INCIDENT_2026-10-29.md`) (with uploads, shares, lifecycle changes, two strengthenings and two encryption removals on those test forms only); **no event references any form or record that existed before** the runs; the organization data-protection policy was toggled eight times and ended with the same values as before (revision 10 → 18, review note now "Playwright … restored the development default"); **the organization logo was replaced** by the spec's one-pixel PNG (branding revision 7); one shared option list was created. Nothing was reset or deleted; Vault, keys, memberships and grants were not changed.
* **Not repaired:** no corrective change was made to the development environment; restoring the logo or removing the synthetic forms needs the owner's decision.
* **Consequence for evidence:** the browser results of this session are **invalid** (92 passed / 3 failed / 7 skipped were against the development stack; the three failures come from its missing Security Administrator persona). Browser verification of M4 on an isolated stack has **not** been done; it is part of the hosted Stage B run.
* **Prevention:** permanent guard implemented in C-74 (§31).

## 29. Acceptance instructions — synthetic migration and reconciliation

Nothing here touches existing forms or records. Use only a **new synthetic form**.

**Option A — isolated security-test stack (verifier configured):**

1. `infrastructure/docker`: `docker compose -p enterpriseforms-sectest -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait`; `cd src/frontend; $env:API_TARGET="http://127.0.0.1:5180"; npm run dev -w apps/web -- --host 127.0.0.1`; open `http://127.0.0.1:5173`.
2. **Owner (Avery Owner, designer):** create a form "M4 acceptance" with a plaintext Internal text field "Note" and a number; publish; give Casey Clerk create permission.
3. **Clerk:** create three records with distinct notes. Archive one of them (record page → Archive).
4. **Owner:** Fields → Edit "Note" → Security & Encryption → Strengthen protection → Confidential, reason → Apply. Watch: *migration running* → *migration verifying with the Authorization Authority* → **Authority-verified: every record's current version encrypted** and **Authority reconciliation complete**.
5. Open **Authority attestation**: 3 registered, 3 verified encrypted, 1 archived (included), 0 still in the old form, 0 quarantined, retained earlier revisions ≥ 3 (history is kept and not migrated — read the coverage statement). **Worker observations**: 3 decrypt-checked, 0 failures, 0 unknown/missing rows, old-form rows kept outside current versions ≥ 3.
6. **Verify completeness…** → reason → Start verification: a new *verify only* reconciliation runs and completes; record values and history are unchanged (open a record as the clerk: the note is still readable; History lists the earlier revisions).
7. Optional tamper check (needs the attacker container; see `ProtectionReconciliationTests`): after a DBA rewrite of one record's encrypted row, Verify completeness ends **Authority reconciliation incomplete** — "some records are quarantined"; nothing is repaired.
8. As Riley Reader: no Verify completeness button and no record access. 320 px: the panel stacks; the details are keyboard reachable.

**Option B — persistent development stack (`http://127.0.0.1:8091`):**

1. `py scripts/devstack/devstack.py backup` (recommended), then `py scripts/devstack/devstack.py rebuild authority api worker web` (additive migrations only; data untouched).
2. Without the verifier, a new synthetic strengthening ends *Independent reconciliation unavailable* and keeps the pre-M4 "All listed records encrypted" badge and caveat — that is the expected fail-closed behaviour.
3. To try the real reconciliation: `py scripts/devstack/devstack.py protection-verifier enable` (development credential in the custody directory; recreates the Authority and Worker; no data change, no migration started). Then repeat Option A steps 2–6 on a **new** form. Do not strengthen fields of existing forms unless you intend that migration; if you do, request **Verify completeness** first only after its migration finished and review the report.

## 30. Outstanding decisions and blockers (superseded by §35)

* **Owner functional acceptance of M4** (§29).
* **Owner decision on the development-environment incident (§28.5):** whether to restore the logo and remove the 30 synthetic Playwright forms.
* **Hosted verification:** dispatch `stage-b-integration.yml` and `security-gate-a.yml` (browser suite included); not run in this session.
* **G19 OPEN:** independent review of the Authority census/attestation/coverage and the verifier boundary; Azure staging evidence; a production verifier identity (Entra workload identity + mTLS; today a Stage A HS256 credential like every workload credential).
* **DR-C7-4:** retained plaintext history, WAL, replicas, backups — disposal policy and any new Authority transition (not implemented).
* **DR-C7-5:** a read-only coverage view for Security Administrators (coverage is verifier-only today).
* **Scale:** forms above ≈ 25,000 records cannot finish a round within the 15-minute snapshot lifetime at the measured rate (§28.2).
* **AUD-C7-01 / G24:** archival durability of mandatory evidence (the attestation journal entry included) unverified.
* Unchanged: G06, G21, PB-08, PB-09, DR-C7-1, DR-C7-2, G25/G26 reviews. No Level 2 claim. C7 not merged; no further milestone started.

## 31. Development incident — inventory, remediation plan and permanent fix (C-74)

**Inventory** (read-only; Authority and business databases compared; backup 20261025-083213 read in a throwaway, network-less container): `docs/security/C7_M4_DEVELOPMENT_INCIDENT_2026-10-29.md`, identity lists in `docs/evidence/phase-c7/dev-incident-2026-10-29/`. In the window 13:49:37–14:09:06 UTC: 28 forms, 41 records, 7 shares (2 revoked), 59 protection decisions on those forms, 4 attachments, 1 option list, 8 policy updates (revision 10 → 18, values unchanged), the organization logo replaced (branding revision 7), 428 audit events. The Authority confirms that **no pre-existing record** received a manifest commit or a head change in the window. Earlier occurrences: 97 Playwright forms on 2026-10-23; the logo was already the spec's one-pixel PNG in the 2026-10-25 backup (byte-identical); the policy note was already Playwright text before today. Whether a genuine logo ever existed (branding revision 5, unaudited — F-C7-03) cannot be determined.

**Remediation:** R1–R8 in the incident document, each an existing audited operation (archive records, deactivate the option list, revoke shares, a new policy revision with the owner's statement, upload of the owner's logo). **Nothing was executed.** There is no supported archive/delete for forms and no logo removal; those need decisions.

**Permanent fix — Playwright can no longer reach a persistent stack:**

| Layer | Guard |
|---|---|
| Configuration | Dedicated port 5199 (`--strictPort`), `reuseExistingServer: false`; base URL or API target on 8091, 5280 or 5080 refused when the configuration loads |
| Global setup (before any test) | GET-only proof through the browser's own base URL: `/api/v1/testing/stack-identity` (mapped only in the `Testing` environment; refuses to start elsewhere) must report API **and** Authority identity `enterpriseforms-security-test` (the Authority answers its own Testing-only endpoint under workload authentication); the served organizations must not include any persistent organization id from the backup manifests |
| Every sign-in | `signIn` refuses an origin that global setup did not verify |
| CI | regression step + `E2E_EXPECTED_STACK_IDENTITY` in `stage-b-integration.yml` |

**Evidence:** `e2e/isolation.unit.test.ts` 12/12; `scripts/e2e-isolation-regression.mjs` reproduces (A) a base URL pointing at a fake persistent-looking server, (B) an occupied test port, (C) an API target on 5280 — each run stops before its first test; the fake server recorded **0 non-GET requests**. Contract test `TestStackIdentityBoundaryTests` (endpoint absent outside Testing). Stage B `TheIsolatedStackIdentifiesItselfAndTheAuthorityConfirmsIt`.

## 32. Missing M4 adversarial tests (C-74)

Isolated security-test stack; `ProtectionReconciliationTests` 14/14 (the 8 from C-73 plus):

| Test | Shows |
|---|---|
| `ADecisionChangeDuringAReconciliationSupersedesItWhileAnOrganizationPolicyChangeDoesNot` | a new protection decision mid-run supersedes the reconciliation (no attestation of the old decision); an organization policy change does not invalidate a running one |
| `AnUnavailableEncryptionKeyBlocksTheReconciliationAndNeverCompletesIt` | worker without its key provider → decrypt check fails → `Blocked`, never Complete; recovers after the key returns |
| `TheCatalogueCeilingEndsIncompleteAndExpiredSnapshotsAreRenewedSafely` | above the ceiling → `Incomplete` (`RECONCILIATION_LIMIT`), nothing partial; with a 2 s snapshot lifetime: Complete after 3 rounds, each of 160 records verified exactly once, 160 decrypt checks, 8 s |
| `AnArchiveOutageNeverYieldsACompletionWithoutAuthorityEvidence` | archive outage: completion only with a journaled attestation; AUD-C7-01 stays open |
| `ARecordChangedAfterTheAttestationIsVerifiedByTheNextReconciliation` | a change after attestation is re-verified by the next verify-only run |
| `TheIsolatedStackIdentifiesItselfAndTheAuthorityConfirmsIt` | §31 |

Authority unit: `TheMigrationCatalogueAcceptsExactly50000RecordsAndRefuses50001WithNothingPartial`, reuse test renamed `…ReusedOnlyWithinTheSameReconciliation` (13/13).

**Defect found and fixed:** catch-up rounds could reuse a census held from an **earlier** reconciliation for records not yet reached in the current one. The census request now names `reuseSinceSnapshotId` (this reconciliation's first snapshot); the Authority reuses a held result only if it was verified at or after that snapshot was issued (same verifier, decision and form; a re-verification refreshes the time). Replaces the C-73 `reverify` flag (Authority internal OpenAPI regenerated; business contract unchanged).

Regression: .NET CI script 598/598 (Authority 191, contracts 306, architecture 67, integrity 34); frontend lint, typecheck, vitest 219/219. Benchmark refreshed (`docs/evidence/phase-c7/c7-m4-benchmark-local.json`): 100 records completion 2.8 s (36 rec/s), 250 records 6.6 s (38 rec/s); verify-only p50 3.2 / 6.2 s; restart recovery 8.8 / 12.9 s. Browser suite on the isolated stack: not run locally in this session; part of the hosted Stage B run.

## 33. Catalogue lifetime and DR-C7-4 / DR-C7-5 / DR-C7-6 (C-74)

* **15-minute lifetime:** no Authority protocol extension was needed. Worker rounds resume after an expiry (resume position, checkpoints at every page end and on a mid-page expiry, per-50 batches), a round without new verified records ends `Incomplete`, at most 20 rounds. Not done on purpose: longer lifetime, higher ceiling, weaker completeness. Additive business migrations `PhaseC7ReconciliationReuseCutoff`, `PhaseC7ReconciliationResumableRounds`. **DR-C7-6:** approval requested before production use (`FIELD_PROTECTION_RECONCILIATION.md` §11).
* **DR-C7-4 (unresolved):** options A–E in §12 of that document. Recommendation: keep and disclose now (A), restrict history reads of strengthened fields once PB-08 step-up exists (B); re-seal and retention purge (C, D) only with an approved retention policy and an independently reviewed new Authority transition. No immediate restriction is needed: retained plaintext is not reachable through search, and history is open only to full readers who can already read the current value.
* **DR-C7-5 (designed, not built):** §13 of that document — read-only, count-only coverage view for Security Administrators through a new user-plane Authority read; never record ids or values.

## 34. Hosted verification — manual dispatch

1. GitHub → `schipani/EnterpriseForms` → **Actions** → **Stage B integration** → **Run workflow** → branch `phase-c7/advanced-field-search` → **Run workflow**.
2. Same for **Security gate A** (`security-gate-a.yml`).
3. Expected: the new isolation regression step passes before the browser suite; integration includes the 14 reconciliation tests; browser suite against the isolated CI stack.

## 35. Outstanding decisions and blockers (superseded by §39)

* **Owner approval of the incident remediation** (R1–R8, exact identities) — nothing executed; logo source (or confirmation there was none); decision on forms (no archive operation exists).
* **Owner functional acceptance of M4** (§29) — not given; M4 not owner-accepted.
* **Hosted verification** (§34).
* **DR-C7-4** unresolved; **DR-C7-5** not built; **DR-C7-6** approval for production.
* **F-C7-03:** organization logo uploads are not audited.
* **G19 OPEN** (independent review, Azure staging, production verifier identity, AUD-C7-01/G24). Unchanged: G06, G21, PB-08, PB-09, DR-C7-1, DR-C7-2, G25/G26. No Level 2 claim. C7 not merged; no further milestone started.

## 36. Owner functional acceptance of M4 current-version reconciliation (C-75)

The owner ran the synthetic acceptance of §29 (Option A, isolated security-test stack) and reported: 3 Authority-registered records, 3 with accepted content, 3 verified encrypted, 1 archived record included, 0 still in the old form, 0 quarantined, 0 unverified, 0 envelope issues; worker: 3 decrypt-checked, 0 decrypt or type failures; 4 retained historical revisions outside the migration scope. Attestation `01a188ea-4bcf-76c0-80e9-85f10553c792`.

**Recorded as functional acceptance, for continued development only, of the reconciliation of each record's _current accepted version_** (C-75). It is not acceptance that historical plaintext was removed (it was not: DR-C7-4), not an independent security review, and not production evidence. G19 stays OPEN.

**Read-back from Authority state** (read-only, `BEGIN READ ONLY … ROLLBACK`, security-test Authority database): the attestation row exists with exactly the reported values — direction Encrypt, expected 3, accepted 3, pending 0, quarantined 0, compliant 3, not applicable 0, old form 0, unverified 0, not yet censused 0, envelope issues 0, archived 1, retired 0, retained history revisions 4, active search approvals 0, `attestation_complete = true`, requesting workload `field-protection-verifier`, attested 2026-10-29 18:26:58.639 UTC (matching the UUIDv7 timestamp), digest `20e797b8e7b5ee4de2d15c374e48014d0d671703af7827249926c98f56280ef4`, Authority audit-journal revision 4. Organization `01a188e2-c62a-79ee-ba61-cbc557d56414` is a freshly bootstrapped security-test organization, not the development organization. A second complete attestation exists for the same field (the step-6 verify-only run). The development Authority database has no attestation table (it still runs pre-M4 images), so no acceptance step touched the development stack.

**Consistency with the trusted completion protocol** (`ProtectionReconciliationService.AttestAsync`):

| Identity | Values | Holds |
|---|---|---|
| expected = accepted + pending + quarantined | 3 = 3 + 0 + 0 | yes |
| accepted = compliant + not applicable + old form + envelope + unverified + not censused | 3 = 3 + 0 + 0 + 0 + 0 + 0 | yes |
| complete ⇔ quarantined = unverified = not censused = old form = envelope = active approvals = 0 | all 0 | yes |
| retained history = Σ (manifest commits − 1) over accepted records | 3 records × (creation + migration) = 3, plus the archive commit of the archived record = 4 | yes |
| worker decrypt checks cover the Authority's target-form occurrences, no failures | 3 checked, 0 / 0 | yes |

The archived record is in the population (its accepted content carries the Archived status) and counts as compliant. Retained history counts revisions, not values; every one of the four retained revisions still holds the field in plaintext.

## 37. Hosted verification on e41b1d0 (PR #29)

Commit `e41b1d0ac4bf0a433445140bdc22ff84d859db50` (C-74). Run metadata read from the public GitHub API (GET only, no credential).

| Workflow | Trigger | Run | Result | Jobs |
|---|---|---|---|---|
| `ci` | pull_request | [40915568668](https://github.com/schipani/EnterpriseForms/actions/runs/40915568668) | success | .NET build + architecture/contract/Authority/integrity tests; frontend lint/typecheck/test/build/client drift; compose profiles; specification manifest + vectors |
| `stage-b-integration` | pull_request | [40915568448](https://github.com/schipani/EnterpriseForms/actions/runs/40915568448) | success | integration (real stack); browser e2e (real stack) — the isolation regression step ran and passed before the browser suite; gate job |
| `security-gate-a` | workflow_dispatch | [40927150366](https://github.com/schipani/EnterpriseForms/actions/runs/40927150366) | success | SEC-P01..SEC-P12 + harness guard; evidence manifest shape/digests |
| `level3-script` | pull_request | [40915568279](https://github.com/schipani/EnterpriseForms/actions/runs/40915568279) | success | Level 3 developer script on an isolated runner |
| CodeQL | pull_request | — | success | no new alerts in the changed code |

Per-test totals are in the run logs and the `stage-b-evidence`, `stage-b-browser-evidence` and `gate-a-evidence` artifacts, which need an authenticated GitHub session; they were not read in this session and no count is claimed here. The workflows fail on zero discovered tests, missing or extra TRX files, and skipped or cancelled gate jobs. The only annotations are the Node.js 20 deprecation warning for `actions/upload-artifact`.

The C-75 wording commit (§38) changes UI/server text and browser assertions; `ci`, `stage-b-integration` and `level3-script` re-run on it automatically through the PR, `security-gate-a` needs a manual dispatch (§34).

## 38. Final C7 review (C-75)

**Scope:** the complete branch against main (M0 → M4, C-55 … C-74; 251 files, mostly EF migration designers). The recorded milestone reviews, decisions and evidence were reconciled with the code, and the changes after the owner-accepted M3 state (`e0d0791` → `e41b1d0`) were re-read in full; this was not a second line-by-line audit of M0–M3. `main` has not moved since its last merge into the branch (0 commits behind; GitHub reports the PR mergeable).

* **Wording (corrected):** (1) the encrypted search-impact text (server and client) said "no plaintext copy exists in the business database"; it now says a value saved while encryption is on has no plaintext copy, and that a field strengthened from plaintext keeps the old plaintext in its retained earlier versions. (2) The strengthening help said existing records are "rewritten as ciphertext when each is next saved" — stale since B-28 — and was silent on history; it now describes the background migration of current versions and states that earlier versions are not migrated. (3) Every "Authority-verified …" and "All listed records …" badge is now accompanied by "Current versions only. N earlier version(s) kept as record history are not migrated and may still hold the field in plaintext; so may write-ahead logs, replicas and backups. They are kept, not deleted (decision DR-C7-4 is open)." (N from the Authority attestation when there is one.) (4) Migration progress and the worker's last check say "in their current version"; the worker count is labelled "Old-form rows in retained earlier versions (not migrated)". (5) The pre-attestation completion audit description says "in those current versions (retained history not migrated)". No count, state, contract or behaviour changed. The browser spec asserts the new history note and help text.
* **Trusted completion protocol:** consistent (§36). Migration completion is reachable only through a journaled Authority attestation computed under the organization lock from the Authority registry, current heads and census results; the business copy is display only.
* **Field-search permissions and historical-record access:** no regression. After M3 (`e0d0791`) no user-plane search, history, restore or projection code changed: the user search catalogue is unchanged, the migration catalogue (verifier workload only) gained a census hint that the attestation never trusts, and the `ContentService` changes are additive (no line removed). The hosted Stage B run on e41b1d0 includes the FieldSearch approval/execution and RecordRestore/history suites.
* **Playwright isolation:** confirmed — port 5199 with `--strictPort`, `reuseExistingServer: false`, persistent ports refused, Testing-only stack-identity proof of API **and** Authority before any test, verified origin at every sign-in; the hosted regression step passed on e41b1d0. A Vite server is listening on 5173 again; the guard ignores it, and a base URL pointed at it would fail the identity proof. **Development organization unchanged since the incident:** read-only query of the development business database — no audit event after 14:09:06.4 UTC (the last event of the incident window), no form and no record created since. Remediation R1–R8 still not executed.
* **Migrations:** Authority `20261029122643_ProtectionReconciliation`; business `PhaseC7ProtectionReconciliation`, `PhaseC7ReconciliationReuseCutoff`, `PhaseC7ReconciliationResumableRounds`, plus the M0–M3 migrations already recorded. All additive and exercised by the hosted from-empty stacks. New tables are in the runtime grant lists; the Authority runtime may INSERT and UPDATE census rows (no DELETE), the established pattern — only `census_verified_on` is ever updated (§32 reuse cutoff).
* **Architecture:** verifier credential registered only in the Worker (`ProtectionVerifierBoundaryTests`); Testing-only endpoints absent outside `Testing` (`TestStackIdentityBoundaryTests`); no new business authority over grants, keys or signing.
* **Security findings:** none new; open findings unchanged and classified in §39.
* **Checks for the wording commit (local):** frontend lint, typecheck, vitest 219/219; API and Worker Release builds, 0 warnings. The browser spec was not run locally (the only isolated stack holds the owner's acceptance data); the hosted run on the PR covers it.

## 39. Outstanding items and merge readiness

**Engineering merge into main — blockers:**

1. Hosted green on the **final** commit (the C-75 wording commit): `ci`, `stage-b-integration` (browser suite with the changed assertions) and `level3-script` run on the PR automatically; `security-gate-a` needs the owner's manual dispatch.
2. The owner's explicit merge approval: PR #29 is a draft titled "do not merge" and stays so until the owner decides.

No code, test, migration or architecture blocker.

**Not merge blockers (owner decisions on the development environment):** incident remediation R1–R8 (logo source; synthetic forms — no form archive operation exists). After a merge the persistent development stack needs `devstack.py backup`, then `rebuild` to pick up M4 (additive migrations); its verifier stays off until `protection-verifier enable`.

**Production Level 2 release — blockers (all OPEN; none closed by C7):**

| Item | Classification |
|---|---|
| G19 — independent review of census/attestation/coverage and the verifier boundary; Azure staging evidence; production verifier identity (Entra workload identity + mTLS) | P1 security finding, open |
| DR-C7-4 — retained plaintext in earlier versions, WAL, replicas, backups | decision required, unresolved |
| DR-C7-5 — Security Administrator coverage view | designed, not built |
| DR-C7-6 — resumable reconciliation rounds | approval required before production use |
| AUD-C7-01 / G24 — archival durability of mandatory evidence (attestation journal entry included) | open |
| G06 search completeness; G21; G25/G26 independent reviews (PIM-backed approver identities for DR-C7-3) | open |
| PB-08 step-up (Security Administrator changes and approvals cannot run in staging/production) | open |
| PB-09 / DR-C7-2 — verified search ≈ 130 records/s hosted, reconciliation ≈ 30–38 records/s laptop; forms above ≈ 25,000 records need several rounds | open |
| DR-C7-1 — terminal state for abandoned creations | decision required |
| F-C7-03 — organization logo uploads not audited | audit gap, open |
| No Azure staging evidence for any C7 Authority change; no Level 2 claim | release gate |
