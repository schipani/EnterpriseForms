# Stage B Status — Enterprise Forms Application (Accused vertical slice)

**Date:** 2026-10-18 · **Branch:** `stage-b/accused-vertical-slice` (PR #10 → `stage-a/level2-security-foundation`) ·
**Evidence commit:** `5d7da9f` (hosted run links in §4.6) · **Scope:** ADR-PB-002 Stage B — the working Accused + AccusedAddress + document
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
| **Automated test suites** (§4) | .NET unit/architecture/contract/authority **159 passed, 0 failed**; frontend lint/typecheck/vitest/build **42 vitest tests passed**; Stage B Docker integration **33 passed, 0 failed**; browser e2e **14 passed, 0 failed**; Stage A SEC-P01..P12 regression on the same stack **13 passed, 0 failed (SEC-P01..SEC-P12 + harness guard)**. GitHub-hosted runs: §4.6. |
| **Security controls** | **Unchanged and re-tested.** No new authorization path: shares, child records, documents, discovery pages, the new value types and now form permissions all go through the independent Authority (and the signed manifest for record content); SEC-P01..P12 pass on the Stage B stack. The one authority extension in the fourth round is a new command kind and a gated read (B-23); existing kinds, the SpiceDB schema and the gate semantics are unchanged. **Phase 1B (B-24..B-26) adds application-level field encryption on top of this foundation without touching the authority: encrypted values are manifest leaves like any other value, decryption happens only after the authority-verified read, and the local development KEK is refused outside Development/Testing. Production key custody (Azure Key Vault / Managed HSM, purpose-separated keys, rotation) is still open — §7.1.** No test was weakened (§5). |
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

Open **http://localhost:5173** → `/login`.

> **Development stack lifecycle (important).** The development Vault runs in dev mode, i.e. **in memory**. If the `vault`
> container restarts for any reason (Docker Desktop restart, reboot, `docker compose down` followed by `up`), the
> organization's signing key is gone while its other state survives on disk: creating records then fails with
> `SIGNING_UNAVAILABLE` / `Vault returned 400 for hmac`, and records stored earlier can no longer be verified.
> Development data is disposable: reset with `docker compose -f compose.yaml -f compose.dev.yaml --env-file .env down -v`
> and `up -d --build --wait` again (this recreates the organization and personas). Stop without losing data only by
> leaving the stack running. A persistent, auto-unsealed Vault for development is a follow-up (it touches the Stage A
> infrastructure, so it was not changed here). If the Vite terminal shows `http proxy error: /api/v1/session … ECONNREFUSED`, the API on port 5080 is not running yet: wait for the compose command above to finish (or, to use an already running security-test stack on 5180, start Vite with `API_TARGET=http://127.0.0.1:5180 npm run dev -w apps/web`). To stop: `docker compose -f compose.yaml -f compose.dev.yaml --env-file .env down`
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

### 1.3a Edit an existing field or document collection (designer persona)

1. Open the form in the designer → **Fields** tab → press **Edit** on any row (for example *Given name* or *Case documents*).
2. **Display label** changes what people see; **Field key** and **Storage type** are shown read-only: they are permanent
   (records, search and integrations rely on them; changing the type would reinterpret stored values). Add a new field if
   you need a different key or type.
3. Edit the **Description**, and — when the field is placed on the draft layout — **Help text**, **Required**, the
   validation rules for its type (length, number range, date range) and, for a document collection, **Maximum files**,
   **Maximum file size (MB)** and **Allowed content types**. Values that do not fit the type are explained in the dialog
   and refused by the server.
4. **Save changes** saves the catalog label/description and the draft layout (repeat as often as you like). Nothing
   already published changes: **Publish…** creates the next version (v2, v3, …). Existing records keep every value and
   every attachment (they reference the permanent field id); a renamed document collection keeps its documents.

### 1.3b Change who can create records after the form is saved (designer persona)

1. Open the form in the designer → **Form permissions** tab. The panel shows the Authorization Authority as its source,
   the authorization revision, who may create records and who may design.
2. **Grant permission** → choose a person and/or a team, give a reason → **Grant**. Or press **Revoke** on a row, give a
   reason → **Revoke permission**. Works for draft-only and published forms; no new form version is created.
3. The change is effective on the next request. Granting create permission does not let anyone read existing records, and
   revoking it does not remove records the person already owns. Every change is audited (My activity).

### 1.3c Configure a field's security and encryption (designer persona) — Phase 1B

1. Open a form in the designer → **Fields** tab → **Add field**. The dialog now has a **Security & Encryption** section:
   **Classification** (Public / Internal / Confidential / Restricted) and the **Application-level encryption** badge.
2. Leave the storage type as *String*: the classification defaults to **Confidential** and the badge reads
   **Enabled (required)** — there is no opt-out for Confidential/Restricted. The text under it explains the trade-off:
   *Encrypted for privacy — not searchable* (Confidential/Restricted values cannot be searched in V1, ADR-PB-004).
   Press **Add field** → the Fields table shows **Confidential · Encrypted** in the new *Protection* column.
3. Add a second field with storage type *Integer*: it defaults to **Internal** with the *Encrypt this field* checkbox
   ticked. Untick it → the badge reads **Off — plaintext** and the text warns that the value is searchable within
   authorization *and readable by a database administrator*. Press **Add field** → **Internal · Plaintext**.
4. Choose **Restricted** (or Confidential) for any type: the checkbox disappears and encryption becomes required. The
   section also shows the organization policy in force (minimum classification, whether plaintext Internal fields are
   permitted, and *development default, not yet reviewed* until the owner saves it — §1.3d). Selecting something the
   policy does not allow disables **Add field** with the reason; the server refuses the same request made directly
   (`CLASSIFICATION_BELOW_FLOOR`, `FIELD_PROTECTION_REQUIRED`, `PLAINTEXT_NOT_PERMITTED`).
5. Place both fields in the **Layout** tab, **Save draft**, **Publish…**. The published version records each field's
   protection; later catalog changes never alter it.
6. Sign in as **clerk**, create a record with both fields: the encrypted field's help text ends with *Encrypted for
   privacy — not searchable*; after **Create record** the value is shown decrypted with an **Encrypted** badge next to
   its label. Sign in as **reader** and open the same record URL: denied, no value shown.
7. Back as **designer**: **Fields** → **Edit** on the plaintext field → the **Security & Encryption** section shows the
   current state and **Strengthen protection…**. Choose a higher classification and/or switch on encryption, give a
   reason, **Apply strengthening**. The row changes to **Confidential · Encrypted**; no new version or draft change is
   created (Versions still lists only v1). Re-open **Edit**: the section shows the **encryption migration progress**
   (queued → running → completed; records re-encrypted / pending / failed, a progress bar, the last error) while the
   background worker re-seals every existing record through the Authorization Authority; when it finishes and the
   post-check finds no plaintext copy, the badge **Fully encrypted · verified** appears and the progress block
   disappears (B-28). A failed or blocked migration shows why and offers **Retry failed records** to designers. There is
   no control to make an encrypted field plaintext again; a lower classification is not offered and is refused by the
   server (encrypted → plaintext is a proposed, unapproved amendment: `docs/decisions/PROPOSED-ADR-PB-004-A1-ENCRYPTED-TO-PLAINTEXT-MIGRATION.md`).
8. To see what the database holds: `docker exec enterpriseforms-business-db-1 psql -U postgres -d enterpriseforms -c
   "select encrypted_logical_type, encode(encrypted_value_bytes,'hex') from field_encrypted_values order by created_on desc limit 3"`
   returns ciphertext only; the record's `field_string_values` rows for that field do not exist.

### 1.3d Organization data-protection policy (owner persona) — Phase 1B

1. Sign in as **owner** → **Organization** → *Organization branding* page → **Data protection policy** card. Until the
   owner saves it, the badge reads **Development default — not yet reviewed** (minimum classification Internal,
   plaintext permitted for Public/Internal fields).
2. Change **Minimum field classification** and/or untick **Permit plaintext storage for Public/Internal fields**, write
   the **Policy statement** (10–2000 characters) and **Save data protection policy**. The change is immediate for new
   fields (including the Stage A `POST /form-definitions` route), audited (`ORGANIZATION_SECURITY_POLICY_UPDATED`,
   flagged when it loosens the policy), protected by a revision check, and never changes how stored values are
   protected. Other roles see the card read-only; the designer's Add-field dialog now refuses plaintext.
3. Restore the development default afterwards if you want the Stage A SEC-P tests' plaintext Internal fields to remain
   creatable on this organization (tick the checkbox again and save with a statement).

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

Owner acceptance additions to requirement 2 (Designer workflow): editing existing fields (B-22) and form permissions after creation (B-23); see §5.1. **Document-encryption audit (2026-10-19, internal, B-27): every committed document version is AES-256-GCM ciphertext at rest regardless of the File field's classification; four gaps closed (historic-version release now authority-verified, staging plaintext purged on cancel/expiry/failure with a sweep, expired sessions refuse chunks, at-rest bytes now tested directly); report `docs/security/DOCUMENT_ENCRYPTION_AUDIT.md`.** **Phase 1B (not an ADR-PB-002 Stage B item, delivered on the same slice): application-level field encryption with designer configuration and an organization data-protection policy (B-24..B-26); steps §1.3c/§1.3d, evidence §4, open production requirements §7.1.**

## 3. What was built (changes since the first Stage B report in bold)

### 3.1 Backend (feature-owned vertical slices, `/api/v1`, 64 operations in `contracts/openapi/enterprise-forms-v1.json`)

- **Identity / BFF (B-01):** session cookie scheme beside the bearer scheme, CSRF double-submit, `/session`,
  `/session/development-personas`, `/session/development-login`, `/session/logout`, `/bootstrap`. Entra provider
  registered but fails closed (no tenant, no authority validator) — see §7.
- **FormDesigner (B-03, B-11, B-22, B-23):** drafts with revision CAS, stable options, immutable versions (layout schema v2),
  publish/restore, relationships, no-op publish refusal. **Controls `dateTime` and `guid` accepted for the new types.**
  **`PATCH …/fields/{fieldId}` edits a field's catalog label/description (revision CAS; key and type changes refused with an
  explanation); layout rules are checked against the storage type; publish snapshots catalog names into unlabelled
  elements. `GET/POST …/access` reads and changes who may create records through the Authorization Authority (new
  `UpdateFormUsers` command and gated read; no business-table copy); `GET /teams` is a display-only team directory.**
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
- **Field encryption — Phase 1B (B-24, B-25, B-26):** `field_encrypted_values` (RowHmac/v1 schema `field_encrypted_values` v1; EF
  migration `20261018235515_StageBFieldEncryption`, insert-only runtime grant, authority allow-list already present),
  `IFieldEncryptionService` → `AesGcmFieldEncryptionService` (AES-256-GCM, random per-value DEK wrapped by the
  `IEncryptionKeyProvider` KEK, AAD = tenant/record/field/item/logical type/envelope version), `FieldValueCodec.Protect`/decrypting
  `Format`, carry-forward re-protection in `CandidateBuilder`, `applicationEncrypted`/`legacyPlaintext` flags on value
  responses, `503 ENCRYPTION_UNAVAILABLE` / `423 FIELD_DECRYPTION_FAILED` problem mappings (generic messages).
  `field_definitions` gains `field_protection_mode`, `protection_policy_revision`, `protection_changed_on/by`, and the
  check constraint *Public/Internal or ApplicationEncrypted*. `FieldProtectionPolicy` (floor, required encryption,
  plaintext permission, secure defaults) runs on both field-creation routes; `GET/POST …/fields/{id}/protection`
  (status + audited strengthening, `FIELD_PROTECTION_STRENGTHENED`); `organization_security_policies` with
  `GET/PUT /organization/security-policy` (owner-only, revision CAS, `ORGANIZATION_SECURITY_POLICY_UPDATED`).
- **Encryption migration (B-28):** `PublishFormSecurityRevision` authority command (planner, registry resource, journaled), workload
  reprotection operations on the existing prepare/commit protocol (`reprotection_command_id` + `current_items`, `ReprotectionRules`,
  `RowCanonicalDecoder` in the shared Integrity library, authority migration `ReprotectionOperations`), `field_protection_migrations` +
  `field_protection_migration_items` (migration `StageBFieldProtectionMigration`), `FieldProtectionMigrationService` hosted by the Worker
  (and the API in-process host), `GET …/protection` with `fullyEncrypted` + `migration` progress, `POST …/protection/retry`.
- **Document-encryption audit fixes (B-27):** historic-version downloads verified by the authority against the manifest of their own
  operation (optional `historic_committed_operation_id` on `VerifyRecordProjection`); staging deletion discards uncommitted blocks;
  `SweepStagingAsync` expires and purges staging (`upload_sessions.staging_object_purged_on`, migration `20261019130416_StageBStagingPurge`);
  `409 UPLOAD_SESSION_EXPIRED` on chunk/complete of an expired session.
- **Audit (B-06), Organizations/UserPreferences (B-08), Dashboard:** unchanged.

### 3.2 Frontend (`src/frontend`, React 19 + TypeScript + Vite 8 + Tailwind 4 + Radix)

- Packages: `work-queue`, `api-client` (regenerated: `shares.list`, new types), `upload-manager` (**pause/resume,
  replace**), `form-renderer` (**DateTime control editing local time → UTC instant, Guid control, validation**),
  `form-designer`, `ui`, `app-runtime`, `design-tokens`.
- Features: identity, dashboard, form-catalog, form-designer (**Edit field dialog, Form permissions tab**), form-records (**Load more paging**), related-forms,
  documents (**Replace, Pause/Resume**), jobs (Upload Center with **Pause/Resume**), audit, organizations,
  user-preferences, support, **sharing (new: `RecordAccessPanel` on the record's Access tab)**.
- Lint fitness functions pass: no `fetch` in features/packages, no cross-feature internals, UI packages free of
  business/API imports.

### 3.3 Infrastructure / CI

`compose.yaml` (clamav, dev-seed, worker, Azurite), `compose.dev.yaml`, `compose.security-test.yaml` unchanged.
`.github/workflows/stage-b-integration.yml` now also runs on **pull requests targeting
`stage-a/level2-security-foundation` and `main`** (Docker integration tests + Playwright on a hosted runner);
`ci.yml` compiles the Stage B test project and runs the frontend suite.

## 4. Test evidence (actual results, 2026-10-18/19; encryption-migration run in `docs/evidence/stage-b/2026-10-19-encryption-migration/`; Phase 1B run in `docs/evidence/stage-b/2026-10-18-field-encryption/`)

### 4.1 .NET unit / architecture / contract / authority — `dotnet test --solution EnterpriseForms.All.slnx`

**154 passed, 0 failed** (4 executables; Phase 1B adds `FieldEncryptionSealsCanonicalValuesBindsIdentityAndFailsClosed` — every logical type round-trips, ciphertext ≠ plaintext, tampered tag/ciphertext and a transplanted record/field/tenant are rejected, unconfigured provider fails closed — and `FieldProtectionPolicyDefaultsFreeTextToEncryptionEnforcesTheFloorAndNeverDowngrades`, plus the signed-column registry entry for `field_encrypted_values`; earlier rounds added two authority planner tests for `UpdateFormUsers` — only `form_user` edges change, designer/administrator-only, empty/contradictory/unknown/already-applied changes rejected — and includes both regenerated OpenAPI snapshots (business and authority), the signed-column registry now covering
`field_date_time_values`/`field_guid_values`, SpiceDB schema assertions, child-record planner, layout/codec (incl.
DateTime offset normalization, sub-second rejection, Guid canonical form)/AES-GCM/ClamAV parser/contrast tests).

### 4.2 Frontend — `npm run lint && npm run typecheck && npm run test && npm run build`

Lint clean, typecheck clean, **42 vitest tests passed** (adds pause/resume and replace tests for the upload manager and five field-edit model tests: locate, round-trip, document-collection binding, type-fit, incompatible-value messages), production
build OK (lazy route chunks per feature).

### 4.3 Stage B Docker integration — `tests/integration/EnterpriseForms.StageBTests` (24 tests)

**24 passed, 0 failed** (TRX in `docs/evidence/stage-b/2026-10-18-field-encryption/stage-b-integration/`; the previous round's 21 in `docs/evidence/stage-b/2026-10-18-form-management/`). **Encryption migration (B-28) — `FieldProtectionMigrationTests` (4):** (a) 6 plaintext records: strengthening reports total 6 records / 6 values, the authority command id is recorded on the field, a second strengthening is refused while running; the worker re-protects every record (each exactly one revision ahead, the Integer field untouched, historic plaintext rows kept as immutable history), the status reports `Encrypted` + `fullyEncrypted` only when complete and verified, every record verifies and decrypts for its owner, the reader stays denied, published v1 unchanged, audit holds start, six `FIELD_VALUES_REPROTECTED` and completion with no plaintext. (b) Direct calls to the authority: prepare with no user and no command → 401, with a forged command id → 403 (also for the head read), a real command **plus** a delegated user → 422, another organization's record under the command → 403; the genuine migration still completes. (c) 24 records with the clerk editing two of them mid-migration and `docker compose restart worker`: no edit lost, both encrypted, the user-edited record completed by the user commit, 24/24 complete. (d) Adversarial: a DBA-forged plaintext row before the migration; strengthening while the authority is paused fails (no command, no local change); the migration goes `Blocked` while the authority is paused and resumes; the tampered record fails after bounded attempts (the authority refuses to build on rows that do not verify and quarantines the record), the other four complete, the field is **not** fully encrypted; a forged `Completed` row in the business database does not change that; the clerk cannot retry, the designer can, and the tampered record stays refused. **Phase 1B — `EncryptionTests` (3):** (a) defaults and refusals through the direct API (String → Confidential/encrypted, Integer → Internal/plaintext, Restricted → encrypted, explicit Internal+encrypted Date; Confidential/Restricted+Standard → `FIELD_PROTECTION_REQUIRED`, Public → `CLASSIFICATION_BELOW_FLOOR`, unknown classification 422), published layout carries each field's `protection`; a record with three encrypted and one plaintext value → `attacker-sql` finds **no** `field_string_values`/`field_date_values` rows, three `field_encrypted_values` rows whose hex dump contains neither the plaintext nor its UTF-8 hex (key *reference* only), plaintext for the Standard integer; owner reads decrypted values with `applicationEncrypted=true`, reader/designer/another organization denied, a field-scoped share releases exactly the shared encrypted value, an edit re-seals, activity and validation errors carry no plaintext, a flipped ciphertext byte → `423` with no plaintext in the body. (b) strengthening: `GET …/protection` reports `Plaintext`; clerk refused, short reason 422, no-op `NO_PROTECTION_CHANGE`, Public target `FIELD_PROTECTION_DOWNGRADE_REFUSED`; after strengthening `EncryptionPendingRewrite` with one legacy record, the legacy value stays readable with `legacyPlaintext=true`, published v1 still says `Standard`, the next save writes ciphertext only (`Encrypted`, zero legacy), a downgrade and a catalog `PATCH` restating plaintext both leave the field encrypted, `FIELD_PROTECTION_STRENGTHENED` audited. (c) policy: unreviewed default read, clerk refused, short statement 422, owner tightens → plaintext refused on the designer route (explicit and defaulted Integer) **and** on the Stage A route (`PLAINTEXT_NOT_PERMITTED`), encrypted Integer accepted, owner loosens (audited), stale revision 409, two `ORGANIZATION_SECURITY_POLICY_UPDATED` events. Because the harness's String fields now default to Confidential/encrypted, every earlier scenario (designer, parent/child, documents, sharing, form access, field edit, tamper) now runs with encrypted free-text values as well. Coverage as before (designer CAS/publish/restore,
typed values, parent-child, documents incl. EICAR and tamper, authorization/cross-tenant) **plus**: the Accused form now
carries `arrest_time` (DateTime) and `external_reference` (Guid) through create, sign, manifest, verify and edit;
`SharingTests` — share list empty → create → listed with field keys → recipient sees exactly the shared fields, no
documents, cannot list shares/open the child/download/re-share → revoke → denied and undiscoverable; records list pages
with `pageSize=1` over three records with no duplicates and rejects a forged continuation token; `BrandingTests` — owner uploads a PNG logo (`image/png`) and it is served back byte-exact, `application/octet-stream` → 415, a fake PNG → 422, non-owner denied. **New this round:** `FormAccessTests` (4) — draft-only and published forms; grant/revoke immediate and audited; no new version or draft; create permission never exposes existing records (not to the grantee, not to the clerk, not to the designer); owned records survive revocation; clerk/reader/other-organization callers refused; empty, short-reason, duplicate, contradictory, unknown-principal, unknown-team and unknown-form changes rejected; teams granted/revoked through membership. `FieldEditTests` (2) — key/type changes refused (`FIELD_KEY_IMMUTABLE`, `FIELD_TYPE_IMMUTABLE`), stale revision 409, clerk and cross-tenant refused, repeated renames keep id/key/type, published v1 unchanged, draft saved repeatedly then published as v2, existing record values, the original attachment (byte-identical download) and a new upload under the renamed document collection all intact, rule/type incompatibilities rejected with explanations, label snapshot, edits audited.

### 4.4 Browser end-to-end (Playwright, Chromium) — `src/frontend/e2e`

**14 passed, 0 failed** on the fresh security-test stack (`docs/evidence/stage-b/2026-10-18-field-encryption/playwright-junit.xml`, `docker-chain.log`) and again on the clean development stack after it was restored (`playwright-dev-stack.log`); the suite also runs on a hosted runner in `stage-b-integration` (§4.6).
6. **`encryption` (Phase 1B, 4 tests)** — *Designer*: Add field shows the Security & Encryption section with Confidential / *Enabled (required)* / *not searchable* for free text and no opt-out, an Integer field defaults to Internal and can be made plaintext with the DBA warning, Restricted removes the opt-out; both fields placed and published. *Clerk*: the create form shows the encrypted help text, the record shows the decrypted value with the **Encrypted** badge; *Reader* is denied without the value. *Designer*: Edit field → Strengthen protection (reason required) → row shows Confidential · Encrypted, reopened dialog shows the pending-rewrite count, no downgrade offered, Versions still only v1. *Owner*: tightens the data-protection policy on the Organization page → the designer's dialog shows *not permitted*, disables **Add field** for plaintext and re-enables it with encryption; the policy is restored at the end. `pb14-accessibility` additionally scans the Add-field dialog with the new section and the owner's policy card at every width/zoom.
1. `accused-workflow` — *Designer* publishes `AccusedAddress` and `Accused` (7 placed fields incl. DateTime and Guid, the related list and the document collection created through **Add document collection**, with Maximum files / size / content types set); *Clerk* creates an Accused (client validation incl. UUID format, DateTime stored as UTC), two addresses, edit → revision 2, uploads across SPA navigation → Ready/Clean, download, EICAR rejected, activity; share → partial view → revoke (reader denied).
2. `branding` — *Owner* uploads a PNG logo (top-left, non-empty alt text); a fake PNG is refused with the server's message.
3. `form-management` (owner acceptance defects 3 and 4) — *Designer* builds a form with a document collection, *clerk* creates a record and uploads a document; *designer* edits an existing field in the **Edit field** dialog (key and type read-only, incompatible values explained, label/description/help text/length saved), edits and renames the document collection (restrictions changed), saves the draft repeatedly and publishes **v2** (Versions lists v1 and v2); the clerk's existing record shows the new labels, the stored value, the original attachment (download works) and accepts another upload under the renamed collection. *Designer* then opens **Form permissions** (authority source and revision shown), grants *Riley Reader* create permission with a reason (published version unchanged), the reader creates only their own record and cannot open the clerk's; the designer revokes with a reason; the reader no longer sees the form, `/records/new` is denied, their own record still opens, and the clerk is unaffected.
4. `pb14-accessibility` — axe WCAG 2.2 AA (+best-practice) with zero violations and no horizontal overflow at 320/768/1440 px and the 200%/400% zoom equivalents on login, dashboard, records, record details/access, Upload Center, gallery, designer fields/layout **and now the Form permissions tab and the Edit field dialog**; keyboard-only: skip link, record tabs by arrow keys, designer Add-field dialog focus trap + Escape returns focus, keyboard reorder; forced-colours/reduced-motion focus visibility.
5. `responsive` (320 px phone project) — gallery reflow, tab order, visible focus, checkbox toggles with Space.

### 4.5 Stage A SEC-P01..SEC-P12 regression on the same stack

**13 passed, 0 failed (SEC-P01..SEC-P12 + harness guard)** on the identical stack (same images with ClamAV, worker and dev-seed) that ran §4.3/§4.4 — the Stage A route still creates plaintext Internal fields under the development policy, so the Stage A tamper/rollback/replay scenarios are unchanged.
Evidence: `docs/evidence/stage-b/2026-10-18-field-encryption/sec-p-rerun/` (previous round: `docs/evidence/stage-b/2026-10-18-form-management/sec-p-rerun/`). This is a regression run on the committed code; the
binding Stage A evidence bound to `e6df484`/`7e00680` is unaffected, and the Stage A gate is not reopened.

### 4.6 GitHub-hosted runs

**Document-encryption audit head `b597bfb`:** `ci` **success** (run 39259813854), `stage-b-integration` **success** (run 39259813842): https://github.com/schipani/EnterpriseForms/actions/runs/39259813854 and https://github.com/schipani/EnterpriseForms/actions/runs/39259813842.

**Phase 1B head `d4da719`:** `ci` **success** (run 39187664563) and `stage-b-integration` — Docker integration (24) + Playwright (14) on a GitHub-hosted runner — **success** (run 39187664589): https://github.com/schipani/EnterpriseForms/actions/runs/39187664563 and https://github.com/schipani/EnterpriseForms/actions/runs/39187664589. The two preceding Phase 1B heads (`5a05b4c`, `d604bef`) passed `ci` but failed `stage-b-integration` on one PB14-10 check only — a 20 px horizontal overflow of the branding page at 320 px under Linux fonts (the pre-existing Save/Discard row did not wrap; the owner persona's page had never been scanned at 320 px before). Fixed by letting the row and the header account button wrap; no test was changed.

`ci` **success** (run 39175837148) and `stage-b-integration` — Docker integration tests (21) + Playwright (10) on a GitHub-hosted runner — **success** (run 39175837149) for head `5d7da9f`: https://github.com/schipani/EnterpriseForms/actions/runs/39175837148 and https://github.com/schipani/EnterpriseForms/actions/runs/39175837149. Earlier heads green: `d3f144c` (39170555542 / 39170555532), `14900b9` (39160072571 / 39160072502). `security-gate-a` passed on a hosted runner for `2311e6b` (run 39135823037) and `93df639` (run 39136591074); the SEC-P regression for this change set is the local run in §4.5 on the identical images.

## 5. Defects found and fixed during Stage B (none fixed by weakening a test)

| Defect | Found by | Fix |
|---|---|---|
| Signed attachment timestamps carried 100 ns ticks → canonicalizer rejects on the real commit path | unit test | `SignedTimestamp.Truncate` before persisting `scanned_on`/`uploaded_on` |
| **Document audit:** historic document versions were released from an unverified business row (metadata edits or a promoted never-committed version undetected) | internal document-encryption audit | authority verifies the historic operation's complete snapshot before release; `DocumentEncryptionAuditTests` 2 and 3 |
| **Document audit:** cancelled/expired uploads left plaintext uncommitted blocks in staging, no expiry sweep existed, permanently failed sessions kept plaintext, expired sessions accepted chunks | internal document-encryption audit | empty-block-list purge, staging sweep with purge timestamp, expiry check; `DocumentEncryptionAuditTests` 4 |
| **Document audit:** no test read the bytes actually stored | internal document-encryption audit | `AzuriteProbe`; `DocumentEncryptionAuditTests` 1, 2, 4, 5 |
| **Phase 1B:** creating the organization's default data-protection policy row committed the caller's transaction, so the first field creation of a fresh organization failed with 500 (`NpgsqlTransaction has completed`) | `EncryptionTests` first Docker run (21/24 red) | the default row is only *added* to the caller's scope; every caller commits itself |
| **Phase 1B:** `FieldProtectionPolicy.Resolve` silently forced encryption when Confidential was requested with `Standard` | unit test | explicit plaintext for Confidential/Restricted is refused (`FIELD_PROTECTION_REQUIRED`), never corrected silently |
| **Phase 1B:** the Stage A `POST /form-definitions` route created plaintext fields regardless of the organization policy | `EncryptionTests` policy test | the route resolves its explicit Internal/Standard fields through the same policy (`PLAINTEXT_NOT_PERMITTED` when the owner has turned plaintext off) |
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
| **Owner acceptance 3:** existing fields (including a File field) could not be renamed or edited — the Fields tab was read-only, no edit route existed, `field_definitions` was insert-only for the runtime role and unplaced fields had no editor | owner's manual test | Edit field workflow (B-22): catalog label/description (revision CAS, column-level grant), draft-layout presentation, permanent key/type with explanations, rule/type checks, label snapshot at publish; browser, integration and unit tests |
| **Owner acceptance 4:** who may create records could only be chosen when the form was created — the authority had no command to change `form_user` and no read of the current state | owner's manual test | `UpdateFormUsers` authority command + gated access read, `GET/POST /form-definitions/{id}/access`, Form permissions tab (B-23); browser, integration and planner tests |
| **Found while verifying:** after the verification stacks were swapped with `down` (no `-v`), the dev stack's in-memory Vault lost the organization's signing key while the organization survived on disk: record creation returned `SIGNING_UNAVAILABLE` (`Vault returned 400 for hmac`) and the earlier claim that dev data was "kept" was wrong — it was unusable | browser suite on the restored dev stack | dev stack reset (`down -v`); lifecycle hazard documented in §1.1; verification chains now end by recreating a clean dev stack. A persistent, auto-unsealed development Vault is a follow-up |
| `web` image (node:22-alpine) failed to build: the lockfile carried only glibc/Windows bindings, so `npm ci` on musl had no rolldown/lightningcss binary | owner's first local `compose.dev.yaml` run | musl bindings pinned and locked alongside the gnu/win32 ones |
| **This round, found by the new browser/axe tests and fixed in product code:** (1) a share-scoped (partial) projection returned the full layout and field catalog, so a recipient saw the labels of unshared fields, the address list and the document collection as empty placeholders — the server now serializes only authorized field definitions, layout elements, non-empty sections and relationship metadata (§4H), with a unit test and an integration assertion; (2) the table scroll container was not keyboard focusable (axe `scrollable-region-focusable` at 320 px) — now a labelled, focusable region; (3) dialogs opened from state did not return focus to their opener on close — the shared Dialog now records and restores the opener. Test-only corrections: field counts in `DesignerTests` after adding two fields, a unit test building signed rows with `row_revision = 0`, and `SharingTests` initially expecting the child list to be denied where the documented behaviour is an empty, count-free list for a parent-only recipient (B-04) | | |

### 5.1 Owner acceptance findings (manual testing of the running application)

| # | Finding reported by the owner | Root cause | Resolution and evidence |
|---|---|---|---|
| 1 | No *File* type when creating a field, so the documented "Case documents" step could not be followed | The storage-type list clipped its last entry; document collections had no dedicated entry point | Dedicated **Add document collection** button, list no longer clips, upload limits editable (max files, size, types); `form-management` + `accused-workflow` browser tests, §1.3 steps |
| 2 | Organization logo upload failed with HTTP 415 | The request coordinator sent every binary body as `application/octet-stream`; the logo endpoint only accepts `image/png` | Binary operations declare their media type; `BrandingTests` (PNG accepted/served, octet-stream 415, fake PNG 422, non-owner denied) + `branding` browser test |
| 3 | **Existing fields cannot be renamed or edited** (including a File field) | No edit route or UI; catalog rows insert-only; presentation editable only per placed element in the Layout tab | **B-22**: Edit field dialog and `PATCH` route; permanent id/key/type, editable label/description/help text/validation/limits; drafts saved repeatedly, published versions immutable (next version via **Publish…**); File-field rename preserves the document collection and every attachment. `FieldEditTests` (2), 5 vitest model tests, `form-management` browser test 2 |
| 4 | **Cannot change form creation permissions after saving** | Form users were only settable inside `RegisterFormDefinition`; the authority had no command or read for them | **B-23**: Form permissions tab; authority `UpdateFormUsers` command and gated read are the only source of truth; immediate, audited, no new version, no record disclosure, users and teams. `FormAccessTests` (4), 2 authority planner tests, `form-management` browser test 3, SEC-P01..P12 13/13 |

Both findings 3 and 4 were verified in a real browser against the running stack before this section was written; the integration
tests drive the same API. Known limits recorded rather than hidden: the options of an existing Option field are still edited
through the API only (no dropdown-options editor yet), an explicit key/type migration workflow does not exist (the dialog refuses
and explains), and form permission management is limited to designers of the form and organization administrators.

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
| **Repeating groups** (`form_record_groups`) | Stage B models multiplicity as independent child records per ADR-PB-002; same-record repeating occurrences are a Phase 3 slice | Phase 3 |
| **Trusted search** (field predicates, global text) | V1 bounded verified search is a separate gate with 18 `ENC-SR-##` tests; the list remains authority-driven discovery (B-07) | Phase 4 / G06 / ADR-PB-004 |
| **Dropdown options editor** for existing Option fields (add, relabel, reorder, deactivate) — the API (`PUT …/options`) exists, the UI does not | Not an ADR-PB-002 Stage B acceptance item; found during owner testing | Designer follow-up |
| **Explicit key/type migration workflow** and field deprecate/retire | The Edit field dialog refuses key/type changes with an explanation; a deliberate, audited value migration is a separate feature | Phase 2/3 |
| **Per-form administrators** distinct from designers and organization administrators; **persistent auto-unsealed development Vault** | Out of scope here; the Vault change touches the Stage A infrastructure (see §1.1) | Teams/Sharing; PB-05 |
| **Team recipients in the share UI**, delegation lineage views | API accepts teams; Teams feature UI is not part of the Accused slice | Sharing/Teams features |
| **Azure Key Vault / Managed HSM KEK** (fields and documents), retention/purge, signature refresh (`freshclam`) | Production trust services; local provider is refused outside Development/Testing — see §7.1 for the full Phase 1B list | PB-05 / PB-10 |
| **Canonical audit catalog, outbox to immutable archive, SIEM** | `audit_events` is a projection (B-06) | Phase 1C |
| **PB-09 benchmarks**, dark mode, 69-screen backlog | Out of Stage B scope by ADR-PB-002 §Explicit deferrals | PB-09 / PB-14 follow-on |

### 7.1 Phase 1B — remaining key-management and Azure production requirements (not implemented, not waived)

Field encryption is implemented and tested on the local stack with the **development** key provider only. Before any
organization's protected data is held in Azure, the following are required; none is claimed here.

| Requirement | State today | Owning gate |
|---|---|---|
| **Managed KEK provider**: `IEncryptionKeyProvider` backed by Azure Key Vault / Managed HSM (`WrapKey`/`UnwrapKey` with a non-exportable key, managed identity with wrap/unwrap only, separate provisioning identity, key catalog) | Only `LocalDevelopment` (32-byte KEK from configuration, Development/Testing only; the API refuses to start with it elsewhere) and `UnavailableKeyProvider` (fails closed) exist | PB-05 §key custody, PB-10 (Canadian HSM, manual Canada-only recovery) |
| **Purpose-separated keys**: distinct KEKs (or HSM keys) for field values, documents and signing; per-organization keys | Fields and documents share the single development KEK; separation is by DEK and AAD domain only (`DynamicForms/FieldValue/v1` vs the document envelope); HMAC keys are already separate (Vault Transit / Managed HSM) | PB-05 / G04 |
| **Key rotation and re-wrap**: new KEK version for new DEKs, background re-wrap of stored wrapped DEKs, retired-version retention for backups, incident re-key | `encryption_key_reference` is recorded per value/version; no rotation job exists | PB-05, G14 (historic key versions in recovery) |
| ~~Bulk re-encryption job~~ **Implemented (B-28, 2026-10-19):** authority-governed background re-protection of every record after a strengthening, with verified completion, retry and progress UI | Done; see §4.3/§4.4 and decision B-28 | — |
| **Encrypted → plaintext migration** (controlled removal of encryption) | **Proposed, not approved, not implemented**: `docs/decisions/PROPOSED-ADR-PB-004-A1-ENCRYPTED-TO-PLAINTEXT-MIGRATION.md`; no-downgrade enforcement stays | Owner approval of the amendment |
| **Authority-held organization data-protection policy** (`UpdateMandatoryPolicy`) so the authority, not a business row, decides whether plaintext is permitted | Business row only today | Amendment A1 prerequisite |
| **Production organization policy**: a reviewed default (`PlaintextInternalFieldsPermitted=false` or an approved statement) set by the organization's owner/security owner before onboarding; approval evidence | Development default permits plaintext Internal fields and says so in the UI | PB-12, PB-13 onboarding |
| **Per-record challenge unlock**, blind-index exact match for encrypted values | Not implemented (ADR-PB-004: no encrypted search in V1) | §4C.5 / future ADR |
| **Confidential/Restricted search** | Explicitly unsupported in V1; designer dialog states it | Phase 4 / G06 |
| **Staging proof**: SEC-P + `EncryptionTests` with the managed provider, key outage fail-closed, backup restore with historic key versions | Local Docker evidence only | PB-03 Azure staging, PB-12 evidence chain |

### 7.2 Document encryption — outstanding after the 2026-10-19 internal audit

Document bytes are encrypted with AES-256-GCM per version on the local stack (verified in §4 and
`docs/evidence/stage-b/2026-10-19-document-encryption-audit/`). Outstanding, not waived: managed KEK in Azure Key Vault / Managed HSM; separate KEKs for documents and field
values per organization; rotation and re-wrap; storage managed identity instead of the Shared Key, separately permissioned
staging, a lifecycle rule as backstop and blob versioning/immutability on `documents`; TLS inside the platform (API → storage,
worker → clamd); legal-hold-aware purge of removed and never-committed ciphertext; download completed/failed audit events;
authority-verified metadata in the version history list; independent cryptographic review and Azure staging proof. Details:
`docs/security/DOCUMENT_ENCRYPTION_AUDIT.md` §5.

### 7.3 Encryption migration — explicitly not done (2026-10-19)

- **Encrypted → plaintext** conversion: proposed only (`PROPOSED-ADR-PB-004-A1-ENCRYPTED-TO-PLAINTEXT-MIGRATION.md`), awaiting the owner's decision; nothing in the code weakens a field.
- **Fail-closed scope during an incomplete migration:** authorized reads of a not-yet-migrated value continue (flagged `legacyPlaintext`); the field is not reported as encrypted, and no search/export surface exists yet that would have to refuse it. If a future surface depends on "fully encrypted", it must consult `fullyEncrypted`, never the migration row.
- **Historic plaintext revisions** remain immutable history in Business PostgreSQL (and backups); purging them is the retention/purge workflow, not this migration.
- **Fields strengthened before this build** (no authority command recorded) keep rewrite-on-save; they can be migrated by raising their classification once (which issues the command and queues the migration).
