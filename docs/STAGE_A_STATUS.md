# Stage A Status — Level 2 Security Foundation

**Date:** 2026-10-18 · **Scope executed:** ADR-PB-002 Stage A (independent Authorization Authority, signing provider,
security command journal, signed row integrity and Merkle manifests, minimal authenticated record workflow, SEC-P01..SEC-P12
Level A adversarial tests, evidence). **Decision A-23 (2026-10-18): Stage A ENGINEERING COMPLETE — ACCEPTED FOR
CONTINUED DEVELOPMENT by the project owner; production Level 2 certification NOT GRANTED; Stage B AUTHORIZED TO PROCEED**
(`stage-b/accused-vertical-slice`, `docs/STAGE_B_STATUS.md`).

## Gate assessment

| Gate | Assessment |
|---|---|
| Stage A implementation (requirements 1–8, 12) | **DONE** — compiles, 138 unit/architecture/contract tests pass (Windows, Linux CI), 12/12 mandatory adversarial tests pass against the real Docker stack on the committed code (`e6df484`, clean worktree and GitHub-hosted runner) with validated evidence manifests. |
| Independent cryptographic review (requirement 11) | **NOT PERFORMED** (verified 2026-10-18: no findings document, no PR review, no external record) — package in `docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md`, attestation template in `INDEPENDENT_CRYPTO_REVIEW_ATTESTATION_TEMPLATE.md`; author-run tests and author-written vectors do not count. Status states: NOT PERFORMED → Completed — evidence pending → Completed — attested (`STAGE_A_GATE_CLOSURE.md` §3a). |
| BUILD-00 repository protection (requirement 9) | **Applied for the public-repository scope (decision A-21)** — rulesets, required checks, PR controls, signed commits, Actions hardening and scanning verified read-only (17 PASS, 0 FAIL, 6 DEFERRED). Public visibility is intentional during development; private Azure DevOps is the deferred production milestone. **Deferred, not implemented:** enforceable CODEOWNERS review, segregated environments, Entra federation (`docs/delivery/BUILD00_REPOSITORY_PROTECTION.md` §4). |
| F-05 Docker isolation (requirement 10) | **OPEN as Azure Level B** — Docker network isolation is demonstrated (SEC-P11) but is not Level 2 proof. |
| Evidence provenance (ADR-PB-012) | **Bound to `e6df484`** — GitHub `security-gate-a` run 39121925152 and a clean local worktree run, both 12/12 with image fingerprints; copies under `docs/evidence/stage-a/`. PB-12 immutable storage not provisioned. |
| **Stage A gate** | **Development: ENGINEERING COMPLETE — ACCEPTED FOR CONTINUED DEVELOPMENT** (project-owner decision A-23; owner review of the cryptographic implementation recorded, explicitly *not* an independent review; remaining development-stage risks accepted by the owner). **Production Level 2 certification: NOT GRANTED** — independent cryptographic attestation, Azure Managed HSM, Confidential Ledger, Azure Level B validation and enforced security-owner review remain outstanding and are not waived. Full requirement table and findings: `docs/STAGE_A_GATE_CLOSURE.md`. |

## 1. What was built

### 1.1 Independent Authorization Authority (`src/authority/*`)

- **Own PostgreSQL store** (schema `authority`, separate cluster/admin/runtime role): organization authorization states,
  identity bindings, resource registry, append-only security commands, desired relationship edges (with removal
  provenance), ordered projection outbox, hash-chained security journal with archive receipts, record content heads,
  content change operations + signed candidate leaves, immutable manifest commits + complete leaf index, content
  integrity checkpoints, key registrations. EF migration `InitialAuthorityStore`; runtime grants without DDL/DELETE on
  journal tables (`AuthorityRuntimeGrants`).
- **SpiceDB projection** (`SpiceDbProjectionClient`, pure managed gRPC from vendored protos): schema written by the
  authority; every delta is one `WriteRelationships` with the projection-cursor marker CAS (precondition on the old
  marker, delete old, touch new) so a stale worker cannot apply a delayed write; ambiguous outcomes are reconciled by
  reading the cursor and verifying every tuple before acknowledgement.
- **Security command service**: workload + delegated-user validation, actor binding, idempotency (key + canonical body
  minus the CAS precondition), revision CAS under `FOR UPDATE`, kind-specific planners (RegisterTeam, Add/RemoveTeamMember,
  RegisterFormDefinition, RegisterRecord, TransferOwnership, CreateShare with bounded delegation, RevokeShare), one
  transaction for desired state + command + outbox + journal, rejected commands journaled.
- **Effectiveness pipeline**: journal commit → independent archive receipt (Azurite create-only blob, test double) →
  strictly ordered projection → `Effective`. Per-organization gate closes on any pending archive/projection/quarantine;
  checks fail closed with `AUTHORITY_NOT_READY`.
- **Signing**: `IIntegrityMacProvider` with `LocalHmacProvider` (Dev/Test) and `VaultTransitHmacProvider` (Docker, HMAC
  keys `type=hmac`, non-exportable, per organization, version-pinned, `vault:vN:` normalized to 32 raw bytes). Provider
  chosen only by deployment configuration; Production refuses everything but AzureManagedHsm, which is **not implemented**
  (readiness reports it). No sign-any-bytes endpoint; signatures are issued only inside `PrepareContentChange` after
  actor/tenant/revision/root/delta/key-version validation.
- **Content protocol**: prepare validates the full candidate snapshot (RowHmac/v1 framing, organization and key-version
  binding, entity-kind allow-list, exactly one header, owner/share edit scope), signs each leaf, stores the candidate
  leaf index and root; commit proves the read-back staged items equal the candidate (set, digests, signatures verified
  through the provider, recomputed root), re-checks permission, CAS-advances the head, writes the immutable manifest and a
  hash-chained content checkpoint; `VerifyRecordProjection` compares the observed set with the complete accepted leaf
  index, verifies every signature and the root, requires the checkpoint receipt, quarantines the head and journals an
  incident on any mismatch, and returns the authorized subset.
- **Operator plane** (separate credential): organization bootstrap (state, bindings, roles, Vault key provisioning,
  revision 1), gate status, Testing-only fault injection.

### 1.2 Business plane (`src/backend/*`)

- Schema (EF migration `InitialStageASchema`): tenants, users, teams, team_memberships, form_definitions,
  form_versions, field_definitions, form_records (owner columns = untrusted projection), form_record_revisions and
  field_string_values as immutable per-operation staged copies, record_shares, record_share_fields. All names
  multiword snake_case; naming validator and signed-column inventory in CI. Runtime role: SELECT/INSERT on signed
  tables, no DELETE/DDL anywhere.
- Vertical slices under `/api/v1`: `users/me`, `form-definitions` (create), `form-definitions/{id}/records` (create),
  `form-records/{id}` (verified GET, PATCH), `transfer-ownership`, `shares` (create/revoke), `teams` (create, members).
  Every protected read goes authority head → exact staged rows by committed operation id → `VerifyRecordProjection` →
  authorized subset; local owner/share/team rows are never consulted.
- `AuthorityHttpClient`: fresh 2-minute workload token per call, user assertion forwarded verbatim, every non-2xx mapped
  to a fail-closed Problem Details response. Browser authentication: TestIdentityProvider bearer (HS256) refused outside
  Development/Testing (PB-08 Entra BFF is the production replacement).

### 1.3 Infrastructure and delivery

- `compose.yaml` + `compose.security-test.yaml`: two PostgreSQL 18 clusters with init-script runtime roles, SpiceDB,
  Vault dev + transit init, `archive-store` (authority-net), one-shot `authority-migrate`/`business-migrate`, Testing
  environment with fault injection and `restart: unless-stopped`, loopback ports 5180/5190, `attacker-sql` on
  business-net only. All three override sets validate.
- CI: `ci.yml` builds the security test project and enforces `--minimum-expected-tests 10` per test executable plus
  `scripts/ci/assert_test_totals.py` (≥130 executed, 4 result files; A-19); `security-gate-a.yml`
  brings up the stack, runs `--minimum-expected-tests 13`, validates the manifest, uploads evidence/logs.
  `.github/rulesets/main-branch-protection.json` is importable (BUILD-00 §2).

## 2. Verification actually performed (local Windows 11 host, Docker Desktop 29.1.3; GitHub ubuntu-24.04 runners)

| Command | Result |
|---|---|
| `dotnet build EnterpriseForms.All.slnx -c Debug/Release` | Build succeeded, 0 warnings (warnings as errors) |
| `dotnet test --solution EnterpriseForms.All.slnx -- --minimum-expected-tests 10` + `assert_test_totals.py --min-total 130 --min-files 4` | **138 passed, 0 failed** on Windows, in the clean worktree and on the Linux `ci` runner (integrity 34, architecture 64, API contracts 19, authority unit 21 incl. `ArchiveProcessorTests`) |
| `python scripts/integrity/verify_vectors.py` / `node scripts/integrity/verify-vectors.mjs` | PASS (34 checks each) |
| `docker compose … config` ×3 | PASS |
| `docker compose -f compose.yaml -f compose.security-test.yaml up -d --build --wait …` | 10 services healthy; authority readiness requires Vault + AuthorityDb + SpiceDB schema + archive |
| `dotnet test --project tests/security/… -- --minimum-expected-tests 13` | **13 passed, 0 failed** (12 mandatory + harness guard) on `e6df484`: clean worktree 2 min 05 s and GitHub run 39121925152. The clean-worktree run on `5e56238` **failed SEC-P12** (A-F12) — fixed, not retried blindly. |
| `python security_validation/validate_security_evidence.py artifacts/security/gate-a-evidence.json` | PASS (manifest shape + digests only; not an attestation) |
| Frontend `npm run generate:api-client` + `npm run check` | client regenerated without typecheck errors; lint/typecheck/7 tests/build PASS |

Four local passes preceded the commit: pass 1 exposed a protocol defect (authority-issued vs caller-bound operation id,
fixed by A-03), pass 2 a retry-identity defect (expected revision inside the idempotency digest, fixed by A-04) and
harness errors, pass 3 a gate-reporting race. The gate-closure rerun in a clean worktree then exposed A-F12 (journal body
`jsonb` normalization breaking the body↔digest binding; fixed by A-18 in `e6df484`). Every defect was fixed in code, not
by weakening a test.

### 2.1 SEC-P01..SEC-P12 — recorded runs (`docs/evidence/stage-a/2026-10-18-github-run-39121925152/`, `…/2026-10-18-local-e6df484/`)

| Test | External effect proven (Level A) | Authority rev. at end |
|---|---|---|
| SEC-P01 | DBA sets `form_records.owner_user_id` to the attacker: attacker read 404 before and after; attacker transfer attempt rejected; owner read 200 complete; authority revision unchanged. | 3 |
| SEC-P02 | DBA inserts `record_shares` (Manager) + `record_share_fields` for every item: attacker read 404; attacker delegation attempt rejected; revision unchanged; a legitimate share returns exactly `given_name` to the reader. | 4 |
| SEC-P03 | Team-owned record; DBA inserts `team_memberships` for the attacker: read 404; authority AddTeamMember → 200 complete; RemoveTeamMember → 404 although the local row persists. | 7 |
| SEC-P04 | DBA edits `string_value` (signature intact) → 423 INTEGRITY_VERIFICATION_FAILED, no bytes (tampered or original) returned, edit on quarantined head 423; signature bit-flip variant on a second record → 423. | 3 |
| SEC-P05 | Revision-1 signed row replayed into the revision-2 slot → 423; one accepted row deleted → 423; header + values deleted → 423 (no fallback to a prior revision). | 3 |
| SEC-P06 | Reader allowed; SpiceDB paused; revocation committed through the authority (202 CommittedAwaitingProjection); reader's cached token during the closed barrier → not 200, gate ≠ Ready; after barrier → 404. | 5 |
| SEC-P07 | SpiceDB paused: owner read 503; CreateShare committed (202); gate AwaitingProjection with revision > applied; reader not allowed, owner 503 (organization-wide gate); after unpause reader sees exactly `given_name`. | 4 |
| SEC-P08 | Vault paused: create 503 SIGNING_UNAVAILABLE, update 503, verified read 503 (no unverified release); after unpause the same idempotency key creates exactly one record (one staged operation), duplicate retry returns the same record; revision advanced by exactly 1. | 4 |
| SEC-P09 | Crash after journal commit: API 503; after restart the authority recovered the command alone (gate Ready, revision +1), same-key retry returned 201 with the same share; crash after the SpiceDB write before ack: reconciled by cursor + tuple verification, revision +2 exactly, retry Effective, reader denied. | 5 |
| SEC-P10 | `pg_dump` before revoke+update; TRUNCATE all + `pg_restore` (share row Active again, revision-2 rows gone): revoked reader 404; owner read 423 (committed operation rows missing, no stale revision served); authority state unchanged. | 5 |
| SEC-P11 | attacker-sql cannot resolve or reach vault/spicedb/authority-db/archive-store (names and authority-net IPs); workload token refused on the operator plane (403); sign-any-bytes probes 404; attacker prepare of a forged header → 403 without signatures; owner candidate bound to another organization → 422; forged user assertions → 401 on both planes; api container environment carries no Vault/SpiceDB/archive/AuthorityDb/operator markers. | 3 |
| SEC-P12 | archive-store paused: share 202 CommittedAwaitingProjection, gate AwaitingArchive, reader not allowed, owner 503; after unpause Effective and readable; content update committed while archive paused is unreadable until its checkpoint receipt lands. | 4 |

### 2.2 Frontend

`npm run generate:api-client` regenerated `packages/api-client/src/generated/enterprise-forms-v1.ts` from the new
business contract (12 paths); `npm run check` passed (eslint clean, tsc clean, vitest 7/7, vite build). No UI feature
pages were added (Stage B).

## 3. Contract and specification changes

Recorded in `trust_authority/trust-authority.openapi.yaml` (header note) and `docs/decisions/STAGE_A_DECISION_LOG.md`
A-01…A-17: schema name `authority`; full-snapshot prepare with caller-bound operation id and read-back commit evidence;
`verify-record-projection` replacing `verify-batch`; registration command kinds; operator plane; gate state in
`OperationStatus`; `X-Delegated-User-Assertion`. Compiled contracts: `contracts/openapi/authorization-authority-internal-v1.json`
(14 paths) and `contracts/openapi/enterprise-forms-v1.json` (12 paths). F-03 decided (integer-only JSON v1).

## 4. Unresolved findings and open items

Superseded by the severity/impact/owner/gate/remediation table in `docs/STAGE_A_GATE_CLOSURE.md` §4 (A-F1..A-F17).
Blocking for Stage A: A-F1 (independent review), A-F14 (security-owner review not machine-enforceable; manual recorded
review required). A-F3 closed for the public-repository scope and A-F13 accepted by decision A-21 (public during development). Fixed during closure: A-F12 (journal body canonical storage), A-F15 (CI test floor), A-F5 (Vault vectors).

## 5. Reproduction

```sh
dotnet build EnterpriseForms.All.slnx -c Release
dotnet test --solution EnterpriseForms.All.slnx --no-build -c Release --results-directory artifacts/test-results --report-xunit-trx -- --minimum-expected-tests 10
python scripts/ci/assert_test_totals.py artifacts/test-results --min-total 130 --min-files 4
cd infrastructure/docker && cp env.example .env
docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait \
  business-db authority-db spicedb vault vault-transit-init archive-store azurite authority-migrate authority business-migrate api attacker-sql
cd ../.. && dotnet build tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -c Release
dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj --no-build -c Release -- --minimum-expected-tests 13
python security_validation/validate_security_evidence.py artifacts/security/gate-a-evidence.json
cd infrastructure/docker && docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env down -v --remove-orphans
```

Windows: the .NET 10 SDK requires `--project` for a single test project; a checkout under a very long path can make
the runner report zero tests — `--minimum-expected-tests` turns that into a failure instead of a silent pass.

**Stop point:** Stage A implementation and Level A validation are complete on `e6df484`; development acceptance was
granted by decision A-23; production Level 2 certification remains pending `docs/STAGE_A_GATE_CLOSURE.md` §9. Stage B is
authorized and in progress on `stage-b/accused-vertical-slice`.
