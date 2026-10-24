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
  * C-62: M1 implemented.
- **G06, G19, G21:** OPEN. **G22:** corrected (independent review pending). **G23:** new, OPEN.
- **State (2026-10-24):**
  * M0 implemented (owner visual acceptance **pending**).
  * G22 hotfix merged to `main` and into this branch (§7).
  * **M1 — AC-5 — implemented and verified (§8); stopped for owner review and acceptance.**
  * Field-value search is still disabled.
  * AC-6 (M2), M3 and M4 are not started.

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

## 9. Outstanding decisions and blockers

* **Owner review and acceptance of M1** (this section and architecture §6A); M0 visual acceptance.
* **Before M3:** the performance trade-off of §8.6 (500 records ≈ 6 s versus the < 3 s PB-09 target).
* **F-C7-01:** idempotent completion of partially failed record creation.
* **G23:** server-side session revocation.
* **AC-6 (M2):** depends on PB-08 step-up for production approvals (OD-7) and on OD-3 form-version registration in the Authority.
* **G06, G19, G21:** open. M1 is a precondition, not a closure. G19 needs M4.
* **Independent review:** the Authority changes (catalogue, verifier credential, batch verification, audit stream, checkpoint lookup) and the G22 fix need independent security review, like C-12, C-28, AC-1 and AC-2. There is no Azure staging evidence.
