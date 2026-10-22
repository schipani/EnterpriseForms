# Phase C4 — Record Workspace and Authorized Discovery

Branch `phase-c/record-workspace` (from main `b35516f`). **Status: implemented and verified (§7, §8); OWNER ACCEPTED FOR CONTINUED DEVELOPMENT (decision C-33, 2026-10-22, §9). Not merged at the time of writing — merging needs the owner's explicit approval.** Nothing here is an accessibility certification, an independent review or a production security certification. Decisions: `docs/decisions/PHASE_C_DECISION_LOG.md` **C-27 onward**.

## 1. Assessment of the existing implementation (before any change)

| Area | What already works | What is missing or wrong |
|---|---|---|
| Forms list (`GET /form-definitions`) | Authority-filtered: a form is listed only if the caller may design it or create records with it; no existence disclosure. | **Defect (reproduced):** two checks per form are sent in **one** authority batch, and the authority accepts at most 250 checks per call. The development organization holds 128 forms (256 checks), so the list fails with `422 LIMIT_EXCEEDED` for **every** persona, including the owner, not only non-admins. The handler also runs two queries per form (current version, draft). |
| Record discovery (`GET /form-records`) | Record ids come only from the authority's `discover` lookup; rows carry local, unverified metadata; opening a record performs the verified read. | **Defect (found in review):** the authority's lookup is capped at 1,000 results (`OptionalLimit = 1000`) and silently drops the rest — a user with more than 1,000 discoverable records would never see some of them. The authority pages by sorted id; the business layer then filtered by form **after** paging (pages can come back short or empty while more exist) and sorted only **within** a page (no deterministic order across pages). No status filter, no sort choice, no total. The lookup also lacks the gate re-read that the permission check performs. |
| My Records page | Form filter, "Load more", Refresh, View action, New record when a form is selected. | Form filter lists only forms the user may design/use (a share recipient has none). No status filter, sorting, paging indicator, record reference, per-row Edit action, or form picker for "New record". Identifying information is a raw UUID. |
| Record Overview | Verified projection (authority head → exact staged rows → complete manifest verification → authorized subset). Tabs Details / Access / Activity; partial (shared) views drop unshared fields, rules, dependencies, repeating sections, child lists server-side. | Related records and documents are mixed into the Details form; no record summary (reference, created/modified, created by, your access); no deep link to a tab (Upload Center and child pages return to the Details tab). |
| Record Editor | `If-Match` with the content revision; conflict message with "reload latest (discard)"; client validation with server re-validation; dropdowns, multi-select, repeating groups and dependent dropdowns via the shared renderer. | No unsaved-changes warning, no validation summary, server validation errors are not tied to fields, no save confirmation, and a conflict can only discard the user's work. |
| Sharing / documents / relationships / activity | Implemented in Stage B / C2 / C3 (`RecordAccessPanel`, `DocumentCollection`, `ChildRecordList`, `RecordActivity`). | Reused unchanged except for placement. |
| Contributor writes | Record-level `edit` is `effective_owner` in the authority schema, so share recipients never see Edit. | The known limitation stands: no field-scoped contributor write can pass the authority (canonical row bytes include row PK + operation id). Not addressed in C4 (would change the integrity/authority protocol). |
| G06 | Search-completeness finding open. | Unchanged — C4 adds no field-value search and makes no completeness claim (see §3). |

## 2. Scope of C4

1. **Batch authorization:** the business authority client splits any permission-check batch into calls of at most 250, requires every chunk to report the same authorization and projection revision (otherwise the whole batch is retried, then fails closed), and never returns partial results. Forms list loads versions and drafts in two queries.
2. **Complete discovery:** the authority enumerates the SpiceDB lookup with cursors instead of a 1,000 cap, fails closed with a typed error above a configured ceiling, re-reads the organization gate after the lookup (as the permission check already does), and reports an invalidated continuation token with its own error code so the business layer can restart cleanly.
3. **My Records:** server-side filtering (form, status, include child records), sorting (last changed, created, form, status; ascending/descending; record id tie-break), opaque expiring cursors bound to principal, organization, filter, sort and authorization revision (PB-06), honest totals, per-row Edit action, form list derived from the caller's own records, a "New record" picker of forms the caller may use, empty/loading/error states.
4. **Record Overview:** summary card plus sections Details · Related records · Documents · Sharing · Activity, deep-linkable by URL; only authorized content (unchanged projection rules).
5. **Record Editor:** unsaved-changes guard (in-app navigation and tab close), validation summary with links to fields, server errors mapped to fields (additive `field_keys` problem extension), save confirmation, three-way conflict handling that reapplies only non-conflicting changes and shows true conflicts for the user to resolve, Discard changes.

Out of scope (documented, not attempted): field-value search (PB-04 V1 verified scans, G06), saved queries, exports, Designer changes, field-scoped contributor writes, Submit/Archive workflows (every record is currently `Draft`).

## 3. Trust-boundary implications

- **No change** to the authority's security protocol, command journal, RowHmac v1, Merkle v1 or encryption. The authority change in §2.2 is an implementation correction of the existing discover operation: same permission (`discover`), same registry intersection, same authority-held ZedToken; it now returns all results or fails, re-reads the gate like a permission check, and adds one internal error code. It still requires the same production security review as other authority changes (C-12 precedent).
- List rows remain **unverified pointers**: the authorized *set* comes from the authority; the *metadata* used to filter and sort (form, status, dates, creator) is the business projection, which a hostile business-database administrator can alter (for example to move a record out of a filter). The UI says so; opening a record performs the verified read. A record the authority lists but the business database has no row for is shown as "Information unavailable", not dropped.
- Totals count only records the caller may discover; no count is derived from anything else.
- No new metadata is released below `read`: list rows show form, reference, status, created/modified time and creator — the allowlisted discovery metadata — never field values.

## 4. What was built

| # | Requirement | Where / how |
|---|---|---|
| 1 | Batch authorization above 250 checks | `PermissionCheckBatching` in the business authority client splits batches into calls of ≤ 250 and combines them only at one authorization + projection revision (retry, then fail closed); failures never become partial or "allowed" (C-27). The Forms list now loads for every persona on the development data (128 forms, 256 checks) — before the fix it returned `422 LIMIT_EXCEEDED` for all of them. |
| 2 | Complete discovery | Authority lookup enumerated with SpiceDB cursors, ceiling 50,000 → `LIMIT_EXCEEDED`, gate re-read after the lookup, `CONTINUATION_INVALIDATED` for stale continuations (C-28). |
| 3 | My Records | `/app/records`: Form / Status / Sort by / Rows per page, "Include related (child) records", Clear filters, "Showing x–y of N records", Previous / Next page, actions **Open** and (only when the authority allows) **Edit** in the right-hand Actions column, "New record" menu of forms the user may use, empty states with the next step, a notice when the list restarts because access changed, a warning listing records the authority grants but the application database cannot describe (C-29). URL keeps filters and sort (`?form=…&status=…&sort=…&size=…&children=true`). |
| 4 | Record Overview | Record summary card; sections Details · Related records · Documents · Sharing and access · Activity and history, deep-linkable with `?tab=` (C-30). |
| 5 | Record Editor | Snapshot-bound `If-Match`, conflict → reapply or discard, field conflicts resolved explicitly, unsaved-changes dialog and tab-close warning, validation summary with focus, server `field_keys`, "Changes saved" confirmation, "no changes to save", revoked-access handling (C-31). |

## 5. Browser testing instructions (owner-reproducible)

The development stack (`enterpriseforms`, API on 5080) was updated **in place** — `docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --no-deps authority api worker web` (in `infrastructure/docker`). No database migration was needed. Vault, the databases, SpiceDB and their volumes were **not** restarted: your development data and signing key are intact. Your own Vite server (http://localhost:5173) picks up the frontend changes by itself; otherwise run `npm run dev -w apps/web` in `src/frontend`, or use the rebuilt web container on http://127.0.0.1:8081. Existing browser sessions ended when the API restarted — sign in again.

1. **The batch defect.** Sign in as **clerk** (or any persona) → **Forms**. The list loads (it previously showed "1..250 checks per call · Code LIMIT_EXCEEDED").
2. **My Records.** Open **My Records** in the navigation.
   * The heading reads "My Records"; below the table: "Showing 1–25 of N records" and **Previous page / Next page**. Page through; the order stays stable across pages.
   * **Sort by** → "Created (oldest first)", then "Form (A to Z)"; **Rows per page** → 10. The URL keeps your choices; reload the page and they stay.
   * **Form** → pick one form: only that form's records, with an exact total. **Status** → "Submitted": "No records match these filters" (every record is a draft today) → **Clear filters**.
   * Tick **Include related (child) records**: child records (for example AccusedAddress) appear with "related to REC-…" and an "Open parent record" action in the "…" menu.
   * Each row shows the form, a short reference (REC-XXXXXXXX), status, created (with "by you" or the creator's name) and last changed. Actions on the right: **Open**, and **Edit** only on records you own.
   * **New record** (top right) lists the forms you may create records with.
3. **Permission boundaries.** As **clerk**, open one of your records → **Sharing and access** → share a field with Riley Reader. Sign in as **reader** → **My Records**: only the shared record(s) appear, with **Open** and no **Edit**. Open it: "Partial view (shared fields only)"; the summary says "Shared fields only"; there is no Related records or Documents section and no Edit button.
4. **Record Overview.** As **clerk**, open an Accused record: the **Record summary** card, then the sections. **Related records** holds the address list ("Add AccusedAddress"), **Documents** the case documents; in **Details** those places show "View in Related records" / "View in Documents". Click a section and note `?tab=` in the URL; the Upload Center's file links open the Documents section.
5. **Editing.** Click **Edit**.
   * Change a value: "You have unsaved changes." Click **Cancel** (or another menu item): "Leave without saving?" → **Stay on this page** keeps the change. Closing or reloading the browser tab also warns.
   * Clear a required field → **Save changes**: a summary "1 problem needs your attention before saving" appears; clicking the message puts the cursor in that field.
   * Fill it, Save: the record page shows "Changes saved" and the next revision.
   * On a record with a repeating section and dependent dropdowns (Phase C3 forms): add an entry, choose Country then Province (only matching provinces are offered), Save.
6. **Concurrent edits.** Open the same record's **Edit** page in two browser tabs (same clerk). In tab A change one field and save. In tab B change a *different* field and save: "Someone else saved this record while you were editing" → **Review and reapply my changes** → "Your change was carried onto the latest version." → Save: both changes are kept. Repeat, this time changing the *same* field in both tabs: tab B shows "Choose a value for 1 field you both changed" with both values → pick **Keep my value** or **Use their value** → Save (Save is held until you choose; if nothing differs any more, "There are no changes to save").

## 6. Reproduction of the batch defect (before / after)

On the development stack (128 forms in the seeded organization), signed in as each development persona and calling the endpoint the Forms page uses:

| `GET /api/v1/form-definitions` | Before (main `b35516f`) | After (this branch) |
|---|---|---|
| clerk | `422 LIMIT_EXCEEDED` "1..250 checks per call" | 200, 121 forms |
| owner | `422 LIMIT_EXCEEDED` | 200, 6 forms |
| designer | — | 200, 123 forms |
| reader | — | 200, 0 forms |

No development data was deleted or reset; the stack was updated in place (§5).

## 7. Verification record

Local (Windows, this branch, development stack on 5080 updated in place):

| Check | Result |
|---|---|
| .NET solution tests (Release: architecture, contract incl. 16 new `PhaseC4RecordWorkspaceTests`, authority unit tests incl. 6 new `DiscoveryCompletenessTests`) | **242 / 242** passed, 0 warnings; test floor script PASS |
| Stage B integration project (incl. new `RecordWorkspaceTests`, 5 cases) | **builds**; not run locally — it needs the security-test stack, which cannot run beside the development stack (shared pinned subnets) and the development stack must not be stopped. Runs in hosted `stage-b-integration` (§8). |
| Frontend typecheck, unit tests (Vitest), build | clean; **163 / 163** (incl. new `editMerge.test.ts`) |
| Playwright `records-workspace.spec.ts` (new) against the development stack | **4 / 4** passed (incl. axe scans of My Records and the editor's validation summary) |
| Full Playwright suite against the development stack (both projects) | first run 43 / 47 — four failures, analysed and fixed (below); **final run on the committed code: 47 / 47 passed** |
| OpenAPI / generated client | regenerated; additive changes only (business contract); the internal Authority contract is unchanged |

Failures found by the full suite and fixed:
1. *Regression (fixed):* the editor's new "no changes to save" check ran before validation, so an untouched record that holds a deactivated option was no longer told to replace it (`dropdowns-rules`, deactivated option). Validation now runs first.
2. *Accessibility defect (fixed):* the "View in Related records / Documents" pointer had an accessible name that did not contain its visible text (axe `label-content-name-mismatch`, WCAG 2.5.3, at 320 px). The name now starts with the visible text.
3. *Unsaved-changes guard (fixed):* it blocked the navigation to the sign-in page after signing out of an unsaved editor; the session has already ended there, so that navigation is no longer blocked.
4. *Test assumptions (test fixes, no product change):* a test locator for "Draft" now scopes to the Details section (the summary shows the status too); the PB-14 helpers now find the latest Accused record through the list API's cursors (with a correct global sort it need not be on the first page of a long-lived stack) and the first Accused form the designer may actually design (the development organization has a seeded "Accused" form the designer may only use).

## 8. Hosted verification (isolated, disposable runners)

On commit `e4bc875` (the implementation commit of this PR):

| Gate | Run | Result |
|---|---|---|
| ci — specifications/manifest/integrity vectors, .NET build + architecture + contract tests, frontend lint/typecheck/tests/build/client drift, Compose profiles | 39711625275 | **success** |
| stage-b-integration — Stage B integration tests on the real stack | 39711625289 | **success: 58 / 58**, including all five new `RecordWorkspaceTests` (TRX: `FormsCatalogIsCompleteAndExactBeyondTheAuthorityBatchLimit`, `MyRecordsListsTheCompleteAuthorizedSetAcrossLookupPages…`, `TheRecordSummaryCarriesOnlyAllowlistedMetadata`, `ValidationFailuresNameTheFieldsWithoutDisclosingValues`, `ListsFailClosedWhileTheAuthorityIsUnavailable` — all Passed) |
| stage-b-integration — browser end-to-end (all Playwright projects) | 39711625289 | **success: 47 / 47**, including the four `records-workspace.spec.ts` tests |
| security-gate-a — SEC-P01..SEC-P12 (dispatched on the branch) | 39711633299 | **success: 13 / 13** |
| level3-script — the Level 3 script, unchanged, on its own clean stack (triggered by the `compose.security-test.yaml` change) | 39711625343 | **success** |
| CodeQL | 39711623607 | **success** |

The commit that adds this section changes documentation only (`docs/PHASE_C4_STATUS.md`, spec manifest); its own CI run is recorded on the PR.

### What this does not show

No manual screen-reader or real browser-zoom review of the new pages (PB-14 manual review stays open), no independent security review of the C-27/C-28 changes (C-28 changes Authority code and needs the production security review), no performance benchmark at PB-09 scale (C-29 notes the known O(N²/250) discovery cost), no Azure staging evidence and no production certification of any kind. G06, G19 and every production Level 2 gate remain open.

## 9. Closure (owner decision C-33, 2026-10-22)

**Owner acceptance.** The project owner personally tested Phase C4 and reports that everything appears to work well: My Records, Record Overview, Record Editing, sorting, filtering and pagination, unsaved-change warnings and concurrency handling. This is **functional acceptance for continued development**. It is an owner review — not an independent review, not formal accessibility acceptance, and not production security certification.

**Verified state of PR #14** (checked 2026-10-22 against the GitHub API): head `8b8f053` (documentation-only on top of the implementation commit `e4bc875`). The four required check contexts — Specifications/manifest/integrity vectors, .NET build/architecture/contract tests, Frontend lint/typecheck/tests/build/client drift, Docker Compose profiles — are `success` on that head, as are stage-b-integration (integration and browser jobs), level3-script and CodeQL. This closure commit is documentation-only and its own checks are recorded on the PR.

**Which commit each result belongs to.**

| Evidence | Ran on | Note |
|---|---|---|
| SEC-P01..SEC-P12 (`security-gate-a`, 13 / 13) | `e4bc875` only (manual dispatch, run 39711633299) | The docs-only commits `8b8f053` and this closure commit changed no code, contract or workflow, so SEC-P was **not** re-run on them and is not claimed for them. SEC-P is not part of the automatic pull-request checks. |
| Stage B integration 58 / 58, browser e2e 47 / 47, level3-script, CodeQL | `e4bc875`; ci, stage-b-integration, level3-script and CodeQL again on `8b8f053` | |
| Local .NET 242, Vitest 163, Playwright 47 / 47 | pre-commit working tree identical in code to `e4bc875` | |

**Unresolved items carried forward (nothing below was closed by C4 or by this acceptance).**

1. **My Records scalability (C-29).** Every request re-enumerates the user's complete discoverable set from the Authority, and every Authority discover page re-runs the full SpiceDB lookup, so listing N records costs about N²/250 lookup results per full listing. It is acceptable for development-scale data and has not been benchmarked against PB-09 (25 organizations, 100K records per organization). The business layer refuses more than 10,000 records and the Authority more than 50,000 (typed errors, never truncation). A scalable design (an Authority-side cursor or an independently maintained, revision-fenced listing index) changes the Authority contract and needs its own design review before large tenants.
2. **Authority discovery change needs independent security review (C-28).** `DiscoverAsync` and the SpiceDB `LookupResources` client changed (cursor-paged enumeration, 50,000 ceiling, gate re-read, `CONTINUATION_INVALIDATED`). It is an implementation correction, not a protocol change, but it is a trust-boundary component change made without independent review, in the same position as C-12. It must be covered by the production security review (with SIM/CONS/MRK regression on Azure staging) before any Level 2 claim.
3. **G06 — search completeness.** My Records filters and sorts on business-database metadata; it is not an independently verified search and makes no completeness claim.
4. **G19 — migration completeness**, **PB-14 manual accessibility review** (NVDA, VoiceOver, real browser zoom, product/design sign-off — nothing of C4 was reviewed manually), the **field-scoped contributor-write limitation**, the **Form Designer's not-fully-accepted usability**, and **every production Level 2 gate** (independent cryptographic attestation, Managed HSM, Confidential Ledger, Azure staging, DR rehearsal) remain open and unwaived.
