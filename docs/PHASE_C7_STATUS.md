# Phase C7 — Advanced Field Search: status

- **Branch:** `phase-c7/advanced-field-search` (from `main` 4c00ce7). Not merged; no PR.
- **Architecture:** `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md`.
- **Decisions:**
  * C-55: checkpoint;
  * C-56: owner decisions D1–D7;
  * C-57: M0 implemented;
  * C-58: new finding G22.
- **G06, G19, G21:** OPEN. **G22:** new, OPEN.
- **State (2026-10-23):** M0 is implemented and verified (§3). It is **stopped for owner functional acceptance** and for the Authority-security architecture review of AC-5 and AC-6. AC-5/AC-6 are not implemented, and M1 is not started.

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

* **AC-5/AC-6 review:** architecture §3–§5, with open decisions OD-1..OD-8 (architecture §5).
* **G22 (new, C-58):** session CSRF double-submit is not enforced for cookie sessions (`SECURITY_GAP_ASSESSMENT.md`). It needs an owner decision on a fix.
* **PB-08 step-up** is not implemented. AC-6 approvals depend on it for production (OD-7).
* **G21:** search labels and options will come from Authority snapshots (AC-6), but the record UI still renders unsigned labels.
