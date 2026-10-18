# Stage B Status — Enterprise Forms Application (Accused vertical slice)

**Date:** 2026-10-18 · **Branch:** `stage-b/accused-vertical-slice` (PR #10 → `stage-a/level2-security-foundation`) ·
**Evidence commit:** `d3f144c` (GitHub run links in §4.6) · **Scope:** ADR-PB-002 Stage B — the working Accused + AccusedAddress + document
workflow from Form Designer to browser, on the unchanged Stage A Level 2 foundation.

> **Stage A status (decision A-23, 2026-10-18):** ENGINEERING COMPLETE — **ACCEPTED FOR CONTINUED DEVELOPMENT** by the
> project owner, whose personal review of the cryptographic implementation is recorded as an **owner review, not an
> independent third-party review**; Stage B is **AUTHORIZED TO PROCEED**. **Production Level 2 certification is NOT
> granted:** independent cryptographic attestation (A-F1), Azure Managed HSM, Confidential Ledger, Azure Level B
> validation and enforced security-owner review remain outstanding and unwaived (`docs/STAGE_A_GATE_CLOSURE.md`).
> Nothing below claims production readiness, Level 2 certification, or AODA/WCAG certification. Stage B is a feature
> milestone (ADR-PB-002 §Release boundaries).

## Gate assessment

| Gate | Assessment |
|---|---|
| **Mandatory Stage B functionality** (ADR-PB-002 §Stage B items 1–8, §2 below) | **Implemented and automatically tested** for the Accused slice: every item has a working UI path, backend slice, authority hook, audit behaviour and an executed test. |
| **Automated test suites** (§4) | .NET unit/architecture/contract/authority **150 passed, 0 failed**; frontend lint/typecheck/vitest/build **37 vitest tests passed**; Stage B Docker integration **15 passed, 0 failed**; browser e2e **7 passed, 0 failed**; Stage A SEC-P01..P12 regression on the same stack **13 passed, 0 failed (SEC-P01..SEC-P12 + harness guard)**. GitHub-hosted runs: §4.6. |
| **Security controls** | **Unchanged and re-tested.** No new authorization path: shares, child records, documents, discovery pages and the new value types all go through the independent Authority and the signed manifest; SEC-P01..P12 pass on the Stage B stack. No test was weakened (§5). |
| **PB-14 design validation** | Automated part **executed** (axe WCAG 2.2 AA + reflow at 320/768/1440 px and 200%/400% zoom equivalents, keyboard-only flows, forced colours — §4.4, §6). **Human review PENDING for the owner/reviewers:** NVDA, VoiceOver, real browser zoom, product/design sign-off — `docs/ui/PB14_MANUAL_ACCEPTANCE_CHECKLIST.md` §2. |
| **Stage B feature milestone** | **Ready for owner acceptance review** once the human PB-14 items are recorded. Features intentionally deferred to later gates are listed in §7 and are not Stage B defects. Production requirements (§7 first row) are unchanged. |

## 1. How to run it (exact local startup)

Prerequisites: Docker Desktop (Linux containers, ≥ 8 GB for the stack), .NET 10 SDK, Node 22 + npm 10, Git Bash or
PowerShell. Everything below uses synthetic credentials from `infrastructure/docker/env.example`; no real identity
provider, key vault or customer data is involved.

### 1.1 Development stack (persistent volumes, API on 5080)

```bash
# From an existing checkout, skip the clone (do not clone inside the repository folder).
git clone https://github.com/schipani/EnterpriseForms && cd EnterpriseForms
git checkout stage-b/accused-vertical-slice
cd infrastructure/docker && cp env.example .env
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --wait
#   services: business-db, authority-db, spicedb(+db), vault(+transit init), archive-store, azurite,
#             clamav (≈60–90 s to become healthy), authority(-migrate), api, business-migrate, worker, dev-seed
#   first build ≈ 5–10 min; "--wait" returns when api is healthy and dev-seed has finished
cd ../../src/frontend && npm ci && npm run dev -w apps/web      # Vite on http://localhost:5173, /api proxied to :5080
```

The two compose profiles pin the same fixed subnets (172.30.0.0/24, 172.30.1.0/24), so only one of them can run at a time: if `up` fails with `Pool overlaps with other one on this address space`, stop the other profile first (`docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env down -v`).

Open **http://localhost:5173** → `/login`. If the Vite terminal shows `http proxy error: /api/v1/session … ECONNREFUSED`, the API on port 5080 is not running yet: wait for the compose command above to finish (or, to use an already running security-test stack on 5180, start Vite with `API_TARGET=http://127.0.0.1:5180 npm run dev -w apps/web`). To stop: `docker compose -f compose.yaml -f compose.dev.yaml --env-file .env down`
(add `-v` to discard data).

### 1.2 Development login (Development/Testing only)

`/login` lists the personas that `dev-seed` bootstrapped through the **authority operator plane** into the synthetic
organization "Northwind Crown Office (synthetic)":

| Persona key | Roles | Use |
|---|---|---|
| `owner` | organization_owner + designer | branding, text size, designing, everything an owner can do |
| `designer` | designer | create forms, fields, relationships, publish, restore |
| `clerk` | form user (granted per form by the designer) | create Accused records, addresses, upload documents, share |
| `reader` | member with no grants | demonstrates default-deny and the recipient side of a share |

`POST /api/v1/session/development-login` mints a BFF session cookie for a seeded persona. It is served only when
`DevelopmentIdentity:Enabled` **and** the identity provider is `TestIdentityProvider` **and** the environment is
Development/Testing; any other configuration returns 404 and the Entra provider refuses to start. This is a usable
development login, not a production bypass (B-01, verified by `AuthorizationAndTamperTests.SessionSurface…`).

### 1.3 Create and publish the Accused form (designer persona)

1. **Forms → New form**: name `AccusedAddress`, tick the clerk as a form user → Create.
2. **Fields** tab → Add field: `Street address` (String), `City` (String), `Address kind` (Option: Home / Work).
3. **Layout** tab → *Add field* each one; set widths (2 of 4 …), *Save draft* → **Publish…** with a change reason.
4. **Forms → New form**: `Accused`, tick the clerk → Create. Add fields `Given name`, `Family name` (String),
   `Date of birth` (Date), `In custody` (Boolean), `Arrest time` (**DateTime**), `External reference` (**Guid**),
   `Case status` (Option). Then **Add document collection** (the second button in the Field catalog) named
   `Case documents` — this creates the `File` field behind the collection; the same storage type is also listed last
   in the *Add field* dialog as "File (document collection)".
5. **Related forms** tab → *Add relationship*: child form `AccusedAddress`, list name `Addresses`.
6. **Layout** tab → place the fields (Given/Family name at 2 of 4 on one row, required), *Add related list*
   `Addresses`, *Add document collection* `Case documents`, then set its upload restrictions on the placed element:
   **Maximum files**, **Maximum file size (MB)** and **Allowed content types** (e.g. `application/pdf, text/plain`;
   empty allows any type the server accepts);
   use the phone/tablet/desktop preview; *Save draft* → **Publish…**. **Versions** tab lists every immutable version;
   *Restore to draft* copies an older layout into the draft for a new version (publishing an unchanged draft is refused).

### 1.4 Records, addresses, documents, sharing (clerk persona, then reader)

- **Forms → New record** on `Accused`. Client validation mirrors the server rules; the server re-validates typed values
  (`YYYY-MM-DD`; ISO instant with offset for DateTime — the control edits local time and stores UTC; canonical UUID;
  `true/false`; option keys; length/pattern/range).
- The record page shows the **verified** projection (`Integrity: Verified`, content revision, layout version),
  **Addresses (n)** with *Add AccusedAddress* / View / Edit / Remove (unlink keeps the child and its history), the
  **Case documents** collection, the **Access** tab and the **Activity** tab.
- **Edit** uses the expected content revision; a concurrent change shows a conflict with a reload action.
- **Documents**: drop or choose several files; each shows transfer progress with **Pause/Resume/Cancel**, then
  *Processing (ScanPending…)*, *Committing*, and **Ready** only after the server committed the signed version. Navigate
  anywhere (e.g. **Uploads** = Upload Center) and back: uploads continue because the upload manager is owned by the app
  shell. **Download** streams the server-decrypted bytes with `attachment` + `no-store`. **Replace** uploads a new
  immutable version of the same document; **Versions** shows history; **Remove** unlinks with a reason. An EICAR file is
  rejected by clamd and never becomes a document.
- **Access tab → Share fields**: choose *Riley Reader*, a role, tick the fields → Share. The share is an explicit field
  allow-list committed by the Authority; the table lists shares with **Revoke**. Sign in as `reader`: the record is
  discoverable, shows *Partial view (shared fields only)*, no addresses, no documents, no Share button; after the
  clerk revokes, the reader is denied again immediately.
- **Records** list: **Load more** pages through the authority's opaque continuation tokens.

### 1.5 Tests

```bash
# .NET unit / architecture / contract / authority (no Docker) — run from PowerShell or cmd, see note
dotnet test --solution EnterpriseForms.All.slnx -c Debug -- --minimum-expected-tests 10

# Frontend
cd src/frontend && npm run lint && npm run typecheck && npm run test && npm run build

# Docker gates (shared disposable stack, API on 5180)
cd infrastructure/docker && docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait
dotnet test --project tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj -- --minimum-expected-tests 10
dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -- --minimum-expected-tests 13
cd src/frontend && npx playwright install chromium && API_TARGET=http://127.0.0.1:5180 npx playwright test
```

Note: under Git Bash on Windows, `dotnet test` reports "Zero tests ran" because MSYS rewrites the testing-platform
arguments; use PowerShell/cmd (or run the test `.dll` directly). CI runs on Linux and is unaffected.

## 2. Mandatory Stage B requirements (ADR-PB-002 §Stage B) — implementation and evidence

| # | Requirement (abridged) | Implemented as | Evidence |
|---|---|---|---|
| 1 | Login, organization context, role-specific dashboard with independently loaded/refreshable cards | BFF session + CSRF + development personas (B-01); `/bootstrap` capabilities; dashboard widgets with per-card queries and Refresh | e2e sign-in/out per persona; `DashboardPage` cards; PB14-19 automated axe + manual item |
| 2 | Designer defines/publishes `Accused` + reusable child `AccusedAddress` linked by a relationship; multiple children created/edited independently with correct inheritance/discovery/share | FormDesigner slice (drafts, options, publish/restore, relationship definitions, layout v2); RelatedForms slice (authority `parent` edge → `effective_owner`, unlink) | `DesignerTests` (3), `ParentChildTests` (2), e2e test 1 + test 2 (two addresses) |
| 3 | Create/view/edit/save/audit an Accused record through the real UI/API/typed storage; immutable versions; concurrency | FormRecords slice: typed signed value tables String/Integer/Decimal/Date/**DateTime**/**Guid**/Boolean/Option (B-02, B-16), verified projection, expected-revision edits, audit projection | `DesignerTests.TypedValuesAreValidatedServerSideAndRoundTrip`, `StageBUnitTests` codec/schema tests, e2e test 2 (validation, create, edit → revision 2, activity) |
| 4 | Multiple documents through the shared upload model; survive SPA transitions; progress, pause/cancel, interruption/resume, scanning, quarantine, encryption, signed metadata, authorization, safe download; no promise after all tabs close | Documents slice (B-05): chunked sessions → Azurite → leased worker → **real ClamAV** → segmented AES-256-GCM → user-session commit → verified download; upload manager with pause/resume/cancel/retry and **Replace** (B-19); Upload Center re-select-to-resume | `DocumentTests` (4: concurrent uploads, EICAR, interrupted resume, tamper), upload-manager vitest (7), e2e test 2 (navigate away/back, download, EICAR) |
| 5 | Authorized share, effective access verification, revocation, one child-specific visibility case; another principal cannot discover unshared children or download their files | Sharing slice: create/list/revoke (B-17) + Access tab UI; recipient sees the explicit field allow-list only | `SharingTests.ShareListRevokeAndChildDocumentDenialFollowTheAuthority`, `ParentChildTests.ChildAccessFollowsTheAuthorityNotTheDatabaseLink`, e2e test 2 (share → partial view → revoke → denied) |
| 6 | Tenant logo and validated WCAG 2.2 AA colours/typography, black default primary buttons, accessible checkboxes, responsive layouts, keyboard operation | Organizations branding (contrast floor on server and client), `packages/ui`/`design-tokens`, 4/2/1 renderer, keyboard designer | `AuthorizationAndTamperTests` (branding owner-only + contrast), `contrast.test.ts`, `pb14-accessibility.spec.ts`, `responsive.spec.ts` |
| 7 | Every feature owns components, handlers, contracts, validation, persistence, authority hooks, tests, audit; no scaffolding of non-milestone pages | Feature-owned slices under `Features/<Name>` and `apps/web/src/features/<name>`; generated client from the compiled OpenAPI; lint/architecture fitness functions | `ArchitectureTests`, ESLint boundary rules, `OpenApiSnapshotTests`; no PG-### page beyond the three flows + gallery |
| 8 | Navigation-during-upload, conflict handling, error/loading/empty states, independent widget refresh, recovery from interrupted transfers | As above; 409 conflict UI with reload; empty/loading/error states on every list; interrupted-upload resume; commit-race retry (B-12) | e2e test 2; `DocumentTests`; `uploadManager.test.ts` (resume after failed chunk, pause/resume, commit retry) |

## 3. What was built (changes since the first Stage B report in bold)

### 3.1 Backend (feature-owned vertical slices, `/api/v1`, 56 operations in `contracts/openapi/enterprise-forms-v1.json`)

- **Identity / BFF (B-01):** session cookie scheme beside the bearer scheme, CSRF double-submit, `/session`,
  `/session/development-personas`, `/session/development-login`, `/session/logout`, `/bootstrap`. Entra provider
  registered but fails closed (no tenant, no authority validator) — see §7.
- **FormDesigner (B-03, B-11):** drafts with revision CAS, stable options, immutable versions (layout schema v2),
  publish/restore, relationships, no-op publish refusal. **Controls `dateTime` and `guid` accepted for the new types.**
- **FormRecords (B-02, B-07, B-16, B-18):** typed signed value tables — **`field_date_time_values` v1 and
  `field_guid_values` v1 added** (EF migration `20261018184528_StageBDateTimeGuidValues`, registry entries, insert-only
  runtime grants, authority allow-list already present) — header schema v3, `FieldValueCodec` invariant wire values,
  verified projection, authority-driven discovery with **continuation-token paging**.
- **RelatedForms (B-04):** child creation under a parent, per-child verified list (unauthorized children omitted without
  a count), unlink.
- **Documents (B-05, B-12, B-13):** chunked sessions, Azurite staging, leased worker, ClamAV INSTREAM verdicts,
  AES-256-GCM segments with per-version DEK, user-session commit, verified download, versions, remove, replace.
- **Sharing (B-17):** `POST /form-records/{id}/shares`, **`GET /form-records/{id}/shares` (new; manage-access only,
  concealment-safe 404 otherwise)**, `POST …/shares/{shareId}/revoke`.
- **Audit (B-06), Organizations/UserPreferences (B-08), Dashboard:** unchanged.

### 3.2 Frontend (`src/frontend`, React 19 + TypeScript + Vite 8 + Tailwind 4 + Radix)

- Packages: `work-queue`, `api-client` (regenerated: `shares.list`, new types), `upload-manager` (**pause/resume,
  replace**), `form-renderer` (**DateTime control editing local time → UTC instant, Guid control, validation**),
  `form-designer`, `ui`, `app-runtime`, `design-tokens`.
- Features: identity, dashboard, form-catalog, form-designer, form-records (**Load more paging**), related-forms,
  documents (**Replace, Pause/Resume**), jobs (Upload Center with **Pause/Resume**), audit, organizations,
  user-preferences, support, **sharing (new: `RecordAccessPanel` on the record's Access tab)**.
- Lint fitness functions pass: no `fetch` in features/packages, no cross-feature internals, UI packages free of
  business/API imports.

### 3.3 Infrastructure / CI

`compose.yaml` (clamav, dev-seed, worker, Azurite), `compose.dev.yaml`, `compose.security-test.yaml` unchanged.
`.github/workflows/stage-b-integration.yml` now also runs on **pull requests targeting
`stage-a/level2-security-foundation` and `main`** (Docker integration tests + Playwright on a hosted runner);
`ci.yml` compiles the Stage B test project and runs the frontend suite.

## 4. Test evidence (actual results, commit `d3f144c`, 2026-10-18)

### 4.1 .NET unit / architecture / contract / authority — `dotnet test --solution EnterpriseForms.All.slnx`

**150 passed, 0 failed** (4 executables; includes the OpenAPI snapshot, the signed-column registry now covering
`field_date_time_values`/`field_guid_values`, SpiceDB schema assertions, child-record planner, layout/codec (incl.
DateTime offset normalization, sub-second rejection, Guid canonical form)/AES-GCM/ClamAV parser/contrast tests).

### 4.2 Frontend — `npm run lint && npm run typecheck && npm run test && npm run build`

Lint clean, typecheck clean, **37 vitest tests passed** (adds pause/resume and replace tests for the upload manager), production
build OK (lazy route chunks per feature).

### 4.3 Stage B Docker integration — `tests/integration/EnterpriseForms.StageBTests` (15 tests)

**15 passed, 0 failed** (TRX in `docs/evidence/stage-b/2026-10-18-stage-b-final/`). Coverage as before (designer CAS/publish/restore,
typed values, parent-child, documents incl. EICAR and tamper, authorization/cross-tenant) **plus**: the Accused form now
carries `arrest_time` (DateTime) and `external_reference` (Guid) through create, sign, manifest, verify and edit;
`SharingTests` — share list empty → create → listed with field keys → recipient sees exactly the shared fields, no
documents, cannot list shares/open the child/download/re-share → revoke → denied and undiscoverable; records list pages
with `pageSize=1` over three records with no duplicates and rejects a forged continuation token; `BrandingTests` — owner uploads a PNG logo (`image/png`) and it is served back byte-exact, `application/octet-stream` → 415, a fake PNG → 422, non-owner denied.

### 4.4 Browser end-to-end (Playwright, Chromium) — `src/frontend/e2e`

**7 passed, 0 failed** (`docs/evidence/stage-b/2026-10-18-stage-b-final/playwright-junit.xml`, run log alongside).
1. *Designer* publishes `AccusedAddress` and `Accused` (7 placed fields incl. DateTime and Guid, plus the related
   list and the document collection): confirms "File (document collection)" is offered in *Add field*, creates
   `Case documents` through **Add document collection**, sets Maximum files / Maximum file size / Allowed content types
   on the placed collection; the clerk then sees `Case documents (0 of 10)` with those limits and uploads two files.
5. *Owner* uploads a PNG logo on the branding page; it appears top-left with non-empty alt text; a fake PNG is refused
   with the server's message (regression for the 415 defect below).
2. *Clerk* creates an Accused (client validation incl. UUID format, DateTime stored as UTC and displayed with the
   instant), two addresses, edit → revision 2, two uploads across SPA navigation → Ready/Clean, download, EICAR
   rejected, activity; *Reader* denied; **clerk shares `Given name` with the reader → reader sees the partial view
   only (no DOB/family name/addresses/documents, no Share button, one record in the list) → clerk revokes → reader
   denied and sees zero records.**
3. *PB-14 automated:* axe WCAG 2.2 AA (+best-practice) with zero violations and no horizontal overflow at
   320/768/1440 px and at the 200%/400% zoom equivalents on login, dashboard, records, record details/access, Upload
   Center, gallery, designer fields/layout; keyboard-only: skip link, record tabs by arrow keys, designer Add-field
   dialog focus trap + Escape returns focus, keyboard reorder; forced-colours/reduced-motion focus visibility.
4. *Phone project (320 px):* gallery reflow, tab order, visible focus, checkbox toggles with Space.

### 4.5 Stage A SEC-P01..SEC-P12 regression on the same stack

**13 passed, 0 failed (SEC-P01..SEC-P12 + harness guard)** on the identical stack (same images with ClamAV, worker and dev-seed) that ran §4.3/§4.4.
Evidence: `docs/evidence/stage-b/2026-10-18-stage-b-final/sec-p-rerun/`. This is a regression run on the committed code; the
binding Stage A evidence bound to `e6df484`/`7e00680` is unaffected, and the Stage A gate is not reopened.

### 4.6 GitHub-hosted runs

`ci` **success** (run 39170555542) and `stage-b-integration` — Docker integration tests + Playwright on a GitHub-hosted runner — **success** (run 39170555532) for head `d3f144c`: https://github.com/schipani/EnterpriseForms/actions/runs/39170555542 and https://github.com/schipani/EnterpriseForms/actions/runs/39170555532. Earlier heads: `14900b9` (39160072571 / 39160072502) and `3f96e24` (39164449381 / 39164449402) green; the first hosted integration run on `c349d84` caught a 320 px overflow that was fixed. `security-gate-a` passed on a hosted runner for `2311e6b` (run 39135823037) and `93df639` (run 39136591074); the SEC-P regression for this change set is the local run in §4.5 on the identical images.

## 5. Defects found and fixed during Stage B (none fixed by weakening a test)

| Defect | Found by | Fix |
|---|---|---|
| Signed attachment timestamps carried 100 ns ticks → canonicalizer rejects on the real commit path | unit test | `SignedTimestamp.Truncate` before persisting `scanned_on`/`uploaded_on` |
| `MapSnakeCase` skipped `DateOnly` → naming violation on `field_date_values` | registry test | naming helper maps `DateOnly`/`TimeOnly` |
| SpiceDB schema tests expected `read = owner` after the parent edge | authority tests | expectations updated to `effective_owner` (schema change is deliberate, B-04) |
| Rejected upload sessions reported no scanner verdict | integration test | verdict derived from the recorded worker failure code (B-13) |
| Concurrent document commits to one record raced on the content revision | integration test | browser serializes commits per record + retries on 409 (B-12); server keeps the clean conflict |
| Reads right after a commit are 503 until the archive receipt lands | integration test | clients wait/retry only on 503 (B-12) |
| Republishing an unchanged draft created duplicate versions | integration test | `409 NO_DRAFT_CHANGES` (B-11) |
| `dev-seed` could not write the personas file (root-owned named volume vs non-root runtime user) | browser e2e | seed directory ownership baked into the image |
| User directory listed only principals who had already signed in | browser e2e | development login projects every seeded persona (display data only; Development/Testing path) |
| rolldown/lightningcss native bindings dropped by npm (optional-dependency bug) | frontend CI | platform bindings pinned as optional dependencies |
| Owner testing: the *Add field* storage-type list clipped its last entry, so **File** looked absent and the documented "Case documents" step could not be followed | owner's manual test | select list no longer clips; a dedicated **Add document collection** button creates the File field directly; upload limits (max files, max size, types) editable on the placed collection; browser test follows the exact steps |
| Organization logo upload answered **HTTP 415**: the client sent every binary body as `application/octet-stream`, the endpoint accepts only `image/png` | owner's manual test | binary operations declare their media type (`binaryContentType`); API + browser tests cover PNG accepted, octet-stream 415, fake PNG 422, non-owner denied |
| Header logo could render with an empty `alt` when the owner cleared the alternative text | logo test | falls back to "<organization> logo" |
| `web` image (node:22-alpine) failed to build: the lockfile carried only glibc/Windows bindings, so `npm ci` on musl had no rolldown/lightningcss binary | owner's first local `compose.dev.yaml` run | musl bindings pinned and locked alongside the gnu/win32 ones |
| **This round, found by the new browser/axe tests and fixed in product code:** (1) a share-scoped (partial) projection returned the full layout and field catalog, so a recipient saw the labels of unshared fields, the address list and the document collection as empty placeholders — the server now serializes only authorized field definitions, layout elements, non-empty sections and relationship metadata (§4H), with a unit test and an integration assertion; (2) the table scroll container was not keyboard focusable (axe `scrollable-region-focusable` at 320 px) — now a labelled, focusable region; (3) dialogs opened from state did not return focus to their opener on close — the shared Dialog now records and restores the opener. Test-only corrections: field counts in `DesignerTests` after adding two fields, a unit test building signed rows with `row_revision = 0`, and `SharingTests` initially expecting the child list to be denied where the documented behaviour is an empty, count-free list for a parent-only recipient (B-04) | | |

## 6. Representative screens (ADR-PB-014)

| Flow | Pages | Status |
|---|---|---|
| Role-based dashboard | PG-006 | Working against real endpoints; cards load/refresh independently; widgets filtered by server capabilities. |
| Accused workspace | PG-010/014/016/017/018/019/020/021/025 | Working: list (paged), create, verified view, edit, addresses, documents (replace/pause), Access tab sharing, Upload Center, activity. |
| Form Designer | PG-029/033/037 | Working: catalog, canvas with preview, versions/restore; DateTime/Guid controls. |
| Component gallery | PG-069 | Internal, synthetic data only. |

Automated PB-14 evidence: §4.4 items 3–4 (register rows PB14-10..15, 22, 23, 25, 28, 29, 30, 32). **Human review
pending (owner/reviewers):** PB14-16 NVDA, PB14-17 VoiceOver, real-zoom PB14-13/14, PB14-07/09/19/20/24/26/27/31/33/34
and the product/design sign-off — `docs/ui/PB14_MANUAL_ACCEPTANCE_CHECKLIST.md` §2. Dark mode is not offered.

## 7. Intentionally deferred (not Stage B defects) and open production requirements

| Item | Why deferred from Stage B | Owning gate |
|---|---|---|
| **Production Level 2 certification** — independent cryptographic attestation, Azure Managed HSM, Confidential Ledger, Azure Level B validation, enforced security-owner review | Not a Stage B deliverable; A-23 accepted Stage A for development only | PB-03 Azure staging, PB-12 evidence governance, `docs/STAGE_A_GATE_CLOSURE.md` |
| **Operable Entra ID sign-in** (BFF, CSRF, provider abstraction exist; Entra fails closed) | Requires an Entra tenant, Conditional Access authentication-strength policy and an authority assertion validator — "Pilot requires separately agreed identity" (ADR-PB-002) | PB-08 Azure staging |
| **Application-encrypted field values** (`field_encrypted_values`) | Needs the managed KEK provider and key lifecycle before any ciphertext is persisted; the authority allow-list and schema slot exist | Phase 1B / PB-05 key custody |
| **Repeating groups** (`form_record_groups`) | Stage B models multiplicity as independent child records per ADR-PB-002; same-record repeating occurrences are a Phase 3 slice | Phase 3 |
| **Trusted search** (field predicates, global text) | V1 bounded verified search is a separate gate with 18 `ENC-SR-##` tests; the list remains authority-driven discovery (B-07) | Phase 4 / G06 / ADR-PB-004 |
| **Team recipients in the share UI**, delegation lineage views | API accepts teams; Teams feature UI is not part of the Accused slice | Sharing/Teams features |
| **Azure Key Vault / Managed HSM KEK**, retention/purge, signature refresh (`freshclam`) | Production trust services; local provider is refused outside Development/Testing | PB-05 / PB-10 |
| **Canonical audit catalog, outbox to immutable archive, SIEM** | `audit_events` is a projection (B-06) | Phase 1C |
| **PB-09 benchmarks**, dark mode, 69-screen backlog | Out of Stage B scope by ADR-PB-002 §Explicit deferrals | PB-09 / PB-14 follow-on |
