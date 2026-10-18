# Stage B Status — Enterprise Forms Application (Accused vertical slice)

**Date:** 2026-10-18 · **Branch:** `stage-b/accused-vertical-slice` (on top of Stage A `7e00680`) · **Scope executed:**
ADR-PB-002 Stage B — the working Accused + AccusedAddress + document workflow from Form Designer to browser, on the
unchanged Stage A Level 2 foundation.

> **Stage A status (decision A-23, 2026-10-18):** ENGINEERING COMPLETE — **ACCEPTED FOR CONTINUED DEVELOPMENT** by the
> project owner, whose personal review of the cryptographic implementation is recorded as an **owner review, not an
> independent third-party review**; Stage B is **AUTHORIZED TO PROCEED**. **Production Level 2 certification is NOT
> granted:** independent cryptographic attestation (A-F1), Azure Managed HSM, Confidential Ledger, Azure Level B
> validation and enforced security-owner review remain outstanding and unwaived (`docs/STAGE_A_GATE_CLOSURE.md`).
> Nothing below claims production readiness, Level 2 certification, or AODA/WCAG certification. Stage B is a feature
> milestone (ADR-PB-002 §Boundary).

## Gate assessment

| Gate | Assessment |
|---|---|
| Stage B functionality (requirements 2–8) | **DONE for the Accused slice** — designer-created Accused and AccusedAddress forms, typed values, 4/2/1 responsive layout, child-record lists, concurrent resumable uploads with real ClamAV verdicts and server-side AES-256-GCM, verified download, version history, audit timeline, role-based dashboard, branding/text size, component gallery. Details in §1–§3. |
| Stage B tests (requirement 9) | **Executed, see §4.** .NET unit/architecture/contract **149/149**; frontend unit/component **35/35**; Stage B Docker integration **12/12** (designer persistence/versioning, parent-child, multi-document upload incl. EICAR rejection, authorization/cross-tenant, tamper regression); browser e2e: **see §4.4**; Stage A SEC-P01..P12 rerun on the same stack: **see §4.5**. |
| Security foundation | **Unchanged.** No new authorization path: every Stage B read/write is decided by the independent Authority (child ownership derives from the parent, documents are leaves of the signed record manifest, designer writes require the form `design` permission). Defects found by the Stage B tests were fixed in code, never by weakening a test (§5). |
| Entra ID sign-in (requirement 3) | **Not operable** (B-01). The BFF session, CSRF double-submit and provider abstraction exist; the Entra provider fails closed at startup. Development personas are the only sign-in and are refused outside Development/Testing by construction. |
| **Stage B gate** | **Development: AUTHORIZED TO PROCEED (A-23); vertical slice demonstrated and evaluated.** Still required before the Stage B *feature milestone* is signed off: PB-14 human AT review + product/design sign-off, the `stage-b-integration` workflow green on a GitHub-hosted runner (needs the workflow on the default branch), and the open items in §6. Production requirements (§6 first bullet) are unchanged. |

## 1. How to run it

### 1.1 Development stack (persistent volumes, API on 5080)

```bash
cd infrastructure/docker && cp env.example .env            # synthetic credentials only
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --wait
#   services: business-db, authority-db, spicedb(+db), vault(+transit init), archive-store, azurite,
#             clamav (≈60–90 s to become healthy), authority(-migrate), api, business-migrate, worker, dev-seed
cd ../../src/frontend && npm ci && npm run dev -w apps/web   # Vite on http://localhost:5173, /api proxied to :5080
```

Browser URL: **http://localhost:5173** → redirects to `/login`.

### 1.2 Development login (Development/Testing only)

`/login` lists the personas that `dev-seed` bootstrapped through the **authority operator plane** into the synthetic
organization "Northwind Crown Office (synthetic)":

| Persona key | Roles | Use |
|---|---|---|
| `owner` | organization_owner + designer | branding, text size, designing, everything an owner can do |
| `designer` | designer | create forms, fields, relationships, publish, restore |
| `clerk` | form user (granted per form by the designer) | create Accused records, addresses, upload documents |
| `reader` | member with no grants | demonstrates default-deny: sees zero records |

`POST /api/v1/session/development-login` mints a BFF session cookie for a seeded persona. It is served only when
`DevelopmentIdentity:Enabled` **and** the identity provider is `TestIdentityProvider` **and** the environment is
Development/Testing; any other configuration returns 404 and the Entra provider refuses to start. This is a usable
development login, not a production bypass (B-01, verified by `AuthorizationAndTamperTests.SessionSurface…`).

### 1.3 Create and publish the Accused form (designer persona)

1. **Forms → New form**: name `AccusedAddress`, tick the clerk as a form user → Create.
2. **Fields** tab → Add field: `Street address` (String), `City` (String), `Address kind` (Option: Home / Work).
3. **Layout** tab → *Add field* each one; set widths (2 of 4 …), *Save draft* → **Publish…** with a change reason.
4. **Forms → New form**: `Accused`, tick the clerk → Create. Add fields `Given name`, `Family name` (String),
   `Date of birth` (Date), `In custody` (Boolean), `Case status` (Option), `Case documents` (**File**).
5. **Related forms** tab → *Add relationship*: child form `AccusedAddress`, list name `Addresses`.
6. **Layout** tab → place the fields (Given/Family name at 2 of 4 on one row, required), *Add related list*
   `Addresses`, *Add document collection* `Case documents`; use the phone/tablet/desktop preview; *Save draft* →
   **Publish…**. **Versions** tab lists every immutable version; *Restore to draft* copies an older layout into the
   draft for a new version (publishing an unchanged draft is refused, B-11).

### 1.4 Records, addresses, documents (clerk persona)

- **Forms → New record** on `Accused` (or Dashboard → Permitted Forms). Client validation mirrors the server rules;
  the server re-validates typed values (`YYYY-MM-DD`, `true/false`, option keys, length/pattern/range).
- The record page shows the **verified** projection (`Integrity: Verified`, content revision, layout version),
  **Addresses (n)** with *Add AccusedAddress* / View / Edit / Remove (unlink keeps the child and its history), the
  **Case documents** collection, the **Access** tab (authority-decided permissions) and the **Activity** tab.
- **Edit** uses `If-Match`/expected content revision; a concurrent change shows a conflict with a reload action.
- **Documents**: drop or choose several files; each shows transfer progress, then *Processing (ScanPending…)*,
  *Committing*, and **Ready** only after the server committed the signed version. Navigate anywhere (e.g.
  **Uploads** = Upload Center) and back: uploads continue because the upload manager is owned by the app shell.
  Download streams the server-decrypted bytes with `attachment` + `no-store`. **Versions** shows history; **Remove**
  unlinks with a reason. An EICAR file is rejected by clamd and never becomes a document.

### 1.5 Tests

```bash
# .NET unit / architecture / contract / authority (no Docker)
dotnet test --solution EnterpriseForms.All.slnx -c Debug -- --minimum-expected-tests 10

# Frontend
cd src/frontend && npm run lint && npm run typecheck && npm run test && npm run build

# Docker gates (shared disposable stack, API on 5180)
cd infrastructure/docker && docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait
dotnet test --project tests/integration/EnterpriseForms.StageBTests/EnterpriseForms.StageBTests.csproj -- --minimum-expected-tests 10
dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -- --minimum-expected-tests 13
cd src/frontend && API_TARGET=http://127.0.0.1:5180 npx playwright test      # browser e2e (installs: npx playwright install chromium)
```

## 2. What was built

### 2.1 Backend (feature-owned vertical slices, `/api/v1`, 49 operations in `contracts/openapi/enterprise-forms-v1.json`)

- **Identity / BFF (B-01):** `Session` cookie scheme beside the bearer scheme, CSRF double-submit middleware,
  `/session`, `/session/development-personas`, `/session/development-login`, `/session/logout`, `/bootstrap`
  (user, capabilities, branding, preferences, authorization revision). `DevelopmentIdentityProvider` mints the
  TestIdentityProvider assertion for seeded personas; `EnterpriseForms.DevSeed` tool + `dev-seed` container bootstrap
  the organization through the operator plane (the API never holds operator credentials).
- **FormDesigner (B-03):** `form_drafts` (revision CAS), `field_options` (stable ids, deactivate not delete),
  immutable `form_versions` (layout schema v2: field / relatedFormList / documentCollection, 4-column placement,
  validation rules, upload policy), publish with draft+version CAS and no-op refusal (B-11), restore-to-draft with
  lineage, relationship definitions with cycle/capacity rules. Layout validation rejects foreign fields, control/type
  mismatches, spans outside 1–4, non-4 grids, `<`/`>` in text, oversized upload policies.
- **FormRecords (B-02):** typed signed value tables (`field_string/integer/decimal/date/boolean/option_values`,
  RowHmac/v1), `form_records` header schema **v3** (parent binding), `FieldValueCodec` invariant wire values,
  `CandidateBuilder` edit-scope rules, verified projection endpoint with ETag, authority-driven list (B-07).
- **RelatedForms (B-04):** child creation under a parent (authority `RegisterRecord` with parent edge →
  `effective_owner` in SpiceDB), per-child verified list, `UnlinkChildRecord` command (data preserved).
- **Documents (B-05):** 4 MiB chunked upload sessions (bitmap inventory, idempotent chunks, complete/cancel),
  Azure Block Blob staging (Azurite locally), durable leased processing job → **ClamAV clamd INSTREAM** verdict →
  segmented AES-256-GCM with per-version DEK wrapped by the (development-only) KEK → processed version row →
  **user-session commit** binds the signed `file_attachment_versions` leaf into the record manifest → Ready.
  Download re-verifies the projection and decrypts server-side (`attachment`, `no-store`, `nosniff`). Version history,
  remove (unlink with reason), `my-upload-jobs`. Worker runs the processing loop (in-process in Development).
- **Audit (B-06):** `audit_events` local projection + `/form-records/{id}/activity`, `/my-activity` (trust note states it
  is a projection, not the security journal).
- **Organizations / UserPreferences (B-08):** branding (validated colour pairs, logo PNG, default text scale),
  per-user text scale; **Dashboard:** capability-filtered widget definitions and per-widget counts.
- **Integrity fix found by tests:** signed `scanned_on`/`uploaded_on` are truncated to microseconds before signing
  (`SignedTimestamp`) — the system clock's 100 ns ticks would otherwise have been rejected by the canonicalizer on the
  real commit path.

### 2.2 Frontend (`src/frontend`, React 19 + TypeScript + Vite 8 + Tailwind 4 + Radix)

- **Packages:** `work-queue` (Dedicated Web Worker request coordinator: lane budgets, per-resource write
  serialization, aging, backpressure, retry policy, CSRF/If-Match/Idempotency headers, compiled endpoint registry —
  no arbitrary URLs; main-thread adapter with identical semantics), `api-client` (generated OpenAPI types + typed
  client over the coordinator only), `upload-manager` (tab-durable multi-upload orchestration, resume from the
  server chunk inventory, commit retry on revision conflict, 2 concurrent per user), `form-renderer` (zod mirror of
  layout schema v2, deterministic 4/2/1 packer, controls, client validation mirroring server rules),
  `form-designer` (canvas: spans, row breaks, keyboard move up/down, required/label/control, live preview at 320/768/
  desktop), `ui` (black `#111111` primary buttons, Radix checkbox with obvious checked state, select, dialog, tabs,
  table, status messages that never rely on colour alone), `app-runtime` (runtime context, BFF session lifecycle,
  branding → validated CSS tokens, Problem Details presentation), `design-tokens` (unchanged).
- **App shell:** logo top-left (or initials), organization name, role-based primary navigation from server
  capabilities, account menu, skip link, session-expired handling (queue cleared, re-sign-in prompt), 503-only query
  retry while an archive receipt is pending.
- **Features (one owner per page, public `index.ts` only):** identity (login), dashboard (independent cards with
  per-card refresh), form-catalog, form-designer, form-records (list / create / verified view / edit with conflict
  handling), related-forms (child list + child creation), documents (collection), jobs (Upload Center with
  re-select-to-resume), audit (timelines), organizations (branding), user-preferences (text size), support (gallery
  PG-069, not-found, no-permission, error panel).
- Lint fitness functions pass: no `fetch` in features/packages, no cross-feature internals, UI packages free of
  business/API imports.

### 2.3 Infrastructure

`compose.yaml`: `clamav` (clamav/clamav:1.4, 600 MiB stream limit, healthcheck), `dev-seed` (one-shot, operator key only
in that container; fixed: the seed volume is now owned by the non-root runtime user), API/worker environment for Storage
(Azurite documents account), MalwareScanner (clamd), Encryption (LocalDevelopment KEK, Development/Testing only),
DevelopmentIdentity. `compose.dev.yaml`: clamd on 127.0.0.1:3310, Azurite on 10010. New workflow
`.github/workflows/stage-b-integration.yml` (Docker integration tests + Playwright); `ci.yml` compiles the Stage B test
project and runs the frontend suite.

## 3. Representative screens (ADR-PB-014)

| Flow | Pages | Status |
|---|---|---|
| Role-based dashboard | PG-006 | Working against real endpoints; cards load/refresh independently; widgets filtered by server capabilities. |
| Accused workspace | PG-010/014/016/017/018/019/020/021/025 | Working: list, create, verified view, edit, addresses, documents, Upload Center, activity. |
| Form Designer | PG-029/033/037 | Working: catalog, canvas with preview, versions/restore. |
| Component gallery | PG-069 | Internal, synthetic data only. |

Automated accessibility checks executed: 320 px reflow without horizontal scroll, keyboard reachability and visible focus
on the gallery (Playwright), labelled controls/aria wiring in component tests, contrast validation of every branding
pair on client and server. **Not done:** NVDA/VoiceOver review, 200%/400% zoom review by a human, product/design
sign-off — PB-14 remains open.

## 4. Test evidence (actual results, this machine, 2026-10-18)

### 4.1 .NET unit / architecture / contract / authority — `dotnet test --solution EnterpriseForms.All.slnx`

**149 passed, 0 failed** (4 executables; includes the OpenAPI snapshot, signed-column registry with the new value and
attachment tables, SpiceDB schema `effective_owner` assertions, child-record planner, layout/codec/AES-GCM/ClamAV
parser/contrast unit tests). Earlier in the session the same suite failed on two real defects that were fixed in code
(microsecond truncation of signed timestamps; `AuthenticationTagMismatchException` assertion).

### 4.2 Frontend — `npm run lint && npm run typecheck && npm run test && npm run build`

Lint clean, typecheck clean, **35 vitest tests passed** (scheduler fairness/budgets/backpressure, executor transport
policy incl. unregistered-endpoint refusal and CSRF/If-Match headers, upload manager chunk/resume/EICAR/commit-retry/
concurrency, packer, validation, FormRenderer component, designer edits, branding fallback), production build OK
(lazy route chunks per feature).

### 4.3 Stage B Docker integration — `tests/integration/EnterpriseForms.StageBTests` (12 tests)

**12 passed, 0 failed** (final run, TRX in `artifacts/stage-b/test-results/`; the three earlier failing iterations are
kept in `iterations/`). Coverage: draft revision CAS, layout rejection (foreign field, script text), publish v1→v2→
restore→v3 with lineage and unchanged history, no-op publish refusal, stable option ids with deactivation blocking new
selection only, typed-value validation and round trip, If-Match edit conflict, audit events; two addresses as
independent child records with verified summaries, child projection parent binding, includeChildren listing, unlink,
capacity bound, reader default-deny, parent share ≠ child access, forged link row never listed; three concurrent clean
uploads (64 KiB, 9 MiB = 3 chunks, text) → Processed → serialized commits → Ready with `inAcceptedManifest`, download
byte-equality and headers, ciphertext ≠ plaintext digest in the DB, version history, non-owner/reader denial, size and
content-type limits, removal; concurrent commit race → clean 409 + retry; **EICAR → `Rejected`/`MALWARE_DETECTED`/
`Infected` from the real clamd**, not committable, no version row; interrupted upload resumed from the server inventory
with an early `complete` refused; cross-tenant outsider denied on every surface; clerk cannot design; designer cannot
read record content; branding owner-only with contrast floor; development login refuses unknown personas; hostile SQL
on `field_date_values`, `file_attachment_versions.ciphertext_sha256` and `field_boolean_values` → `423
INTEGRITY_VERIFICATION_FAILED` / failed-closed downloads with no plaintext.

### 4.4 Browser end-to-end (Playwright, Chromium) — `src/frontend/e2e`

**3 passed, 0 failed** (Chromium; `docs/evidence/stage-b/2026-10-18-local-stage-b/playwright-junit.xml`, run log alongside).
1. *Designer* signs in with the development persona, creates `AccusedAddress` (3 fields, option list), places and
   publishes it; creates `Accused` (5 fields incl. File), adds the `Addresses` relationship, places fields with 2-of-4
   spans and required flags, the related list and the document collection (allowed types edited), saves the draft,
   publishes, signs out.
2. *Clerk* signs in, creates an Accused (client validation blocks first, then real create → `Integrity: Verified`),
   adds two addresses through the child list, edits the record (revision 2), uploads two documents, navigates to the
   Upload Center and back through the SPA (uploads continue), sees both documents **Ready/Clean**, downloads one
   (browser download event, original filename), uploads EICAR → `MALWARE_DETECTED` shown and no document row, reads
   the Activity tab (record created / updated), signs out. *Reader* cannot open the record (concealed) and sees zero
   records.
3. *Phone project (320 px):* gallery renders without horizontal scroll, the renderer is in its one-column breakpoint,
   Tab reaches the skip link and every control, the focused checkbox shows an outline and toggles with Space.

Seven earlier runs failed and were fixed in product code or test selectors (dev-seed volume ownership, user directory
projection, duplicate heading/text selectors, document type policy, full-reload vs SPA navigation, concealed 404 rendered
as a status) — see §5.

### 4.5 Stage A SEC-P01..SEC-P12 rerun on the same stack

**13 passed, 0 failed** (SEC-P01..SEC-P12 + harness guard, 2 min 13 s) on the identical stack that ran the Stage B
integration tests and the browser e2e — the same images with ClamAV, worker and dev-seed added. Evidence manifest
validated (`validate_security_evidence.py`: structure/digests PASS, which is not a security attestation):
`docs/evidence/stage-b/2026-10-18-local-stage-b/sec-p-rerun/`. **Provenance caveat:** this is a *provisional* local run
on a dirty worktree whose HEAD was Stage A `7e00680`. **Binding run:** GitHub `security-gate-a` dispatched on this branch at commit `2311e6b` →
**success** (https://github.com/schipani/EnterpriseForms/actions/runs/39135823037, 12/12 + guard on a hosted runner).
`stage-b-integration` cannot be dispatched until the workflow file exists on the default branch. The Stage A evidence
bound to `e6df484`/`7e00680` is unaffected.

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
| User directory listed only principals who had already signed in, so a designer could not grant form use to the clerk | browser e2e | development login projects every seeded persona of the organization (display data only; Development/Testing path) |
| rolldown/lightningcss native bindings dropped by npm (optional-dependency bug) | frontend CI | platform bindings pinned as optional dependencies |

## 6. Unresolved findings, limitations and deferred items

- **Production Level 2 certification not granted** (decision A-23 accepted Stage A for development only): independent
  cryptographic attestation, Azure Managed HSM, Confidential Ledger, Azure Level B validation and enforced security-owner
  review remain outstanding. Stage B cannot close any of them.
- **Entra ID sign-in not operable** (B-01); only development personas can sign in. PB-08 Conditional Access/FIDO2
  verification untouched.
- **Field types:** DateTime, Guid and application-encrypted (`field_encrypted_values`) values are not implemented;
  repeating groups (`form_record_groups`) are not implemented. All Stage B fields are `Internal`; documents are
  `Confidential` (encrypted) by the organization floor.
- **Documents:** the KEK is a local development key (`LocalDevelopmentKeyProvider`); Azure Key Vault / Managed HSM
  providers are not implemented (startup guard forbids the local provider outside Development/Testing). No malware
  signature refresh (`CLAMAV_NO_FRESHCLAMD`), no replace-version UI (API supports `replacesFileAttachmentId`), no
  retention/purge workflow, logo stored as uploaded PNG without re-encoding.
- **Search** is not implemented (G06); the records list is authority-driven discovery with unverified rows (B-07).
- **Lists** are not paginated in the UI beyond the first page (API supports continuation tokens).
- **Accessibility:** automated checks only; human NVDA/VoiceOver, 200%/400% zoom review and product sign-off are
  outstanding (PB-14). Dark mode is not offered (Light only).
- **Audit** is a local projection (B-06): no canonical event catalog, outbox to immutable archive, or SIEM delivery
  (Phase 1C).
- **CI:** `security-gate-a` passed on a GitHub-hosted runner for `2311e6b`; the `stage-b-integration` workflow (Docker
  integration + Playwright) cannot be dispatched until merged to the default branch, so its evidence in §4.3/§4.4 is from
  this developer machine on the committed code.
- **Performance:** no PB-09 benchmark was executed.
