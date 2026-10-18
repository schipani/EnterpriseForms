# Phase 0 Status — Discovery, Gate 0 Artifacts and Repository Scaffold

**Date:** 2026-10-17 · **Scope executed:** Phase 0 only (CLAUDE.md §10 "Phase 0 — Discovery and design" plus the
scaffold requested for this milestone). **Stage A has not started** and is not authorized by this document.

**What this is not:** no business feature, migration, authority store, signing provider, SpiceDB projection, upload,
search or audit pipeline exists. No Azure resource was created. Nothing here is production readiness, Level 2 proof
or AODA/WCAG certification. All credentials in the repository are synthetic development values.

## 1. Specification and repository validation

| Check | Result |
|---|---|
| `python scripts/validate-specifications.py` (baseline) | PASS before Phase 0 (3 pending Gate 0 outputs reported) |
| `python scripts/validate-specifications.py --strict` | **PASS** after Phase 0 (107 tracked files, 14 ADRs, 69 owned pages, 0 broken references) |
| ADR-PB-001…014 present and `Accepted` in the register | Confirmed |
| Normative conflicts found | One design-level finding (§6, F-01); no security conflict requiring resolution |
| Toolchain recorded | .NET SDK 10.0.201 · Node 22.20.0 / npm 11.5.1 · Python 3.14.0 · Docker 29.1.3 / Compose v5.0.1 · Windows 11 host |

## 2. Gate 0 artifacts (BUILD-01, BUILD-02)

| Required path | Delivered | Verification |
|---|---|---|
| `docs/dynamic-forms/architecture.md` | System context, trust boundaries, deployable units, module/table ownership, ERD, key data flows (auth, write commit, verified read, upload, search, audit), assumptions, migration plan, checklist pointer | Reviewed against CLAUDE.md §4U, §4L, §4M–§4O; ownership cross-checked by `FeatureOwnershipMatrixTests` |
| `docs/dynamic-forms/row-integrity-format.md` | `DynamicForms/RowHmac/v1` byte format: domain separation, versioned signed-column registry (ordinal-sorted, absent ≠ NULL), typed encodings for Boolean/Int32/Int64/Decimal/Date/TimestampUtc/Uuid/Text/Bytes/Json, NFC text, normalized decimals, microsecond UTC instants, RFC 8785 JSON subset, encrypted-envelope binding, verification contract | `trust_authority/row-integrity-v1.test-vectors.json`: 7 positive cases, 9 decimal examples, 8 negative cases |
| `trust_authority/merkle-v1.test-vectors.json` | Protocol §8 three-item manifest plus unsorted-input, 1-, 4- and 5-leaf cases, empty root, 4 negative cases; exact leaf bytes, levels, roots, index digests | Reproduced byte-for-byte by three independent implementations |

**Cross-language agreement (reproducible):**

```sh
python scripts/integrity/verify_vectors.py      # PYTHON VECTOR VERIFICATION PASS (34 checks)
node   scripts/integrity/verify-vectors.mjs     # NODE VECTOR VERIFICATION PASS (34 checks)
dotnet test --solution EnterpriseForms.All.slnx # includes 30 C# vector/negative tests in tests/integrity
```

Independent review requirements recorded in `row-integrity-format.md` §9 and `control-traceability.csv`: human
cryptographic review by a non-author, real Vault Transit HMAC vectors (SIG tests), signed-column registry for every
protected table. **Until those are done the vectors are machine-cross-verified, not independently approved.**

## 3. Scaffold delivered

| Area | Content | Proof |
|---|---|---|
| Business .NET 10 solution | `EnterpriseForms.{Domain,Application,Infrastructure,Contracts,Api,Worker}`; `/api/v1` Minimal API composition; `/health/live`, `/health/ready` (fails closed without reachable Authority); feature-owner endpoint metadata; worker startup guard; `IAuthorizationAuthorityClient` port; `BusinessDbContext` shell + naming rules | `dotnet build` succeeded (warnings as errors) |
| Independent Trust Authority | `AuthorizationAuthority.{Domain,Application,Infrastructure,Contracts,Api}`; `/internal/v1/health/*`; signing-provider policy: `Unconfigured` refuses start, `LocalDevelopment` Dev/Testing only, `VaultTransit` never Staging/Prod, Production requires `AzureManagedHsm` | `AuthoritySigningPolicyTests` (5 tests) |
| Shared integrity library | `EnterpriseForms.Integrity` (RowHmac v1 canonicalizer, HMAC helper, Merkle v1 builder; no keys, HTTP or persistence) | `tests/integrity` 30 tests; NetArchTest provider-free rule |
| OpenAPI generation | Build-time documents `contracts/openapi/enterprise-forms-v1.json` and `authorization-authority-internal-v1.json` from compiled endpoints; served document equals committed snapshot | `OpenApiSnapshotTests`; CI `git diff --exit-code contracts/openapi` |
| Architecture tests | csproj dependency allow-list; NetArchTest namespace rules; business↔authority isolation; no local version pins; DB naming rules + model validator; 69-page ownership matrix agreement | `tests/architecture` 69 tests |
| Frontend | npm workspace: `apps/web` (Vite 8, React 19, Tailwind v4, TanStack Router + Query, accessible shell), `@enterpriseforms/design-tokens` (approved tokens + WCAG contrast validator, 7 tests), `@enterpriseforms/api-client` (generated from committed OpenAPI); ESLint boundary rules (no raw fetch, public entrypoints only, UI packages business-free) | `npm run check` passed: lint, typecheck, 7/7 tests, production build |
| Docker Compose | `compose.yaml` + `compose.dev.yaml` / `compose.integration.yaml` / `compose.security-test.yaml`; two PostgreSQL 18 instances with different credentials, SpiceDB + datastore, Vault Transit (dev), Azurite, API, worker, Authority, web; four networks emulating the trust boundary; `attacker-sql` only on `business-net` | `docker compose config` PASS for all three sets; live stack result in §4 |
| Dockerfiles | api, worker, authority (multi-stage, non-root, chiseled-extra runtime with ICU), web (unprivileged nginx, `/api` no-SPA-fallback) | built in §4 |
| CI | `.github/workflows/ci.yml` (spec strict, vectors ×3, .NET build/test, contract drift, frontend check, client drift, compose config); `security-gate-a.yml` (fails BLOCKED until Stage A tests exist); Dependabot; CODEOWNERS placeholders; actions pinned to commit SHAs | YAML authored; not yet executed on GitHub (repository not pushed) |
| Developer experience | `global.json`, `Directory.Build.props`, central package pinning, `.editorconfig`, `.gitignore`, `.vscode/{launch,tasks,extensions}.json` | — |
| Governance docs | `docs/architecture/ADR-ARCH-001-feature-vertical-slices.md`, `docs/decisions/PHASE0_DECISION_LOG.md` (17 decisions), updated `FEATURE_OWNERSHIP_MATRIX.md`, `PREBUILD_OPEN_ITEMS.md`, `docs/dynamic-forms/control-traceability.csv` (108 controls: 42 SEC, 18 G, 42 SIM, 6 ADR-SEC decisions) | manifest regenerated; strict validation PASS |

## 4. Builds and tests actually run (local, Windows host)

| Command | Result |
|---|---|
| `dotnet restore EnterpriseForms.All.slnx` | OK |
| `dotnet build EnterpriseForms.All.slnx -c Debug` | **Build succeeded**, 0 warnings (TreatWarningsAsErrors) |
| `dotnet test --solution EnterpriseForms.All.slnx` | **108 passed, 0 failed, 0 skipped** (integrity 30, architecture 69, API contracts 9) |
| `python scripts/integrity/verify_vectors.py` | PASS (34 checks) |
| `node scripts/integrity/verify-vectors.mjs` | PASS (34 checks) |
| `python scripts/validate-specifications.py --strict` | PASS |
| `npm run check` (src/frontend) | PASS: eslint clean, tsc clean (3 workspaces), vitest 7/7, vite build (dist produced) |
| `docker compose … config` ×3 override sets | PASS; network map confirms `attacker-sql` → `business-net` only, Authority plane isolated |
| `docker compose … up -d --build --wait` (dev set) | **PASS** — 9 services healthy, readiness chain verified (§4.1) |

### 4.1 Live Compose verification

Recorded from `infrastructure/docker` after `cp env.example .env`:

```text
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --wait   business-db authority-db spicedb vault azurite authority api worker
up exit=0 elapsed=22s (after images were built)

SERVICE        STATUS
api            Up (healthy)      authority      Up (healthy)      worker       Up
business-db    Up (healthy)      authority-db   Up (healthy)      spicedb      Up (healthy)
spicedb-db     Up (healthy)      vault          Up (healthy)      azurite      Up

api  /health/live                 -> 200
api  /health/ready                -> 200   (reached the live Authority over trust-edge)
auth /internal/v1/health/live     -> 200
auth /internal/v1/health/ready    -> 200   {"probeStatus":"Ready"} (VaultTransit reachable, DEV ONLY)

network isolation (DNS across planes must fail):
  ok: business-db cannot resolve authority-db
  ok: business-db cannot resolve vault
  ok: authority-db cannot resolve business-db
  ok: business-db resolves api (same plane)
probe mode: loopback probe exit=0; non-loopback probe exit=2 (refused, as designed)
docker compose ... down -v  -> clean teardown
```

Three defects were found and fixed by this run, all recorded in `PHASE0_DECISION_LOG.md` P0-17: `global.json`
needed `rollForward: latestFeature` (the SDK image ships 10.0.402); a root `.dockerignore` was missing (host `obj/`
folders poisoned the Linux restore); the dev Azurite host port moved to 10010 because 10000 was taken on the host.
The web image was not built in this run (it depends on the API only for the proxy and is exercised by `npm run build`).
This is a development emulation of the trust boundary, not Level 2 isolation evidence (ADR-PB-003 Level B).

### 4.2 Database isolation demonstration (2026-10-18, dev set)

Requested demonstration: documented startup commands work, automated tests run, business and authority databases are
independently isolated. Reproduced from `infrastructure/docker` with the committed configuration.

```text
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --wait ...   -> 9 services healthy
api /health/ready -> 200 ; authority /internal/v1/health/ready -> 200

separate clusters:   business-db  cluster_id=7697835376393158698 user=business_admin_dev  db=enterpriseforms_business
                     authority-db cluster_id=7697835376293871657 user=authority_admin_dev db=enterpriseforms_authority
login roles:         business-db: business_admin_dev only ; authority-db: authority_admin_dev only
cross credentials:   authority-db: FATAL role "business_admin_dev" does not exist
                     business-db:  FATAL role "authority_admin_dev" does not exist
DNS:                 business-db cannot resolve authority-db or vault; authority-db cannot resolve business-db
layer 1 (network):   business-db -> authority-db by IP, correct password: psql exit=124 (no TCP path; authority-net internal)
layer 2 (pg_hba):    gateway-sourced connection (host via published port), correct password:
                     FATAL: pg_hba.conf rejects connection for host "172.30.1.1", user "authority_admin_dev"
controls:            own-subnet clients connect (client=172.30.1.4 / 172.30.0.3); wrong password -> authentication failed
```

**Finding F-05 (fixed).** The first demonstration showed that on this Docker Desktop (WSL2, iptables backend) host,
containers on different *non-internal* bridge networks could reach each other **by IP address** even though DNS did not
resolve across networks; `psql` from `business-db` connected to `authority-db` with the authority password, and PostgreSQL
saw the client as the authority network's gateway `172.30.1.1` (masqueraded). Name-only separation is therefore not
isolation on this host. Fixes committed: `authority-net` is `internal: true` (verified to block the path), both subnets
are pinned, and both databases run with a `pg_hba.conf` that rejects the gateway address, allows only the own pinned
subnet and rejects everything else. Dev host-port publishing for databases, Vault and SpiceDB was removed because host
traffic also arrives as the gateway; use `docker compose … exec <db> psql`. A plain-Docker reproduction later did *not*
show the cross-bridge path, so the exact daemon condition is not fully characterized; the two layers do not depend on it.
This remains a development emulation and is not Level 2 isolation evidence (ADR-PB-003 Level B, ADR-PB-005 §4).

## 5. Control traceability (CLAUDE.md §0: SEC-01..42, G01..18, SIM-01..42, ADR-SEC decisions)

`docs/dynamic-forms/control-traceability.csv` — 108 controls. Status today: **1 Tested** (SEC-38 canonicalization
mismatch, by the three-way vector verification), **107 Designed**. Controls with Phase 0 evidence beyond normative
text: SEC-38, SEC-42, G03, G04, G05, G15, G18, SIM-38. No control is Implemented or Waived. Regenerate with
`python scripts/integrity/generate_control_checklist.py`.

## 6. Findings requiring owner decision (not resolved unilaterally)

| ID | Finding | Action taken | Decision needed |
|---|---|---|---|
| F-01 | PB-14 reference token `--ui-border: #cbd5e1` has 1.48:1 contrast on white; WCAG 2.2 SC 1.4.11 requires ≥3:1 for a form-control boundary. The reference focus colour on the black primary button is 2.82:1. | Validator kept strict. Added `--ui-input-border: #64748b` (4.76:1) for control boundaries; `--ui-border` retained for decorative dividers only; focus ring evaluated against page/surface because it renders with a 2px offset. Tested in `contrast.test.ts`. | Product/design approval of the new token and of the focus-ring evaluation rule (ADR-PB-014 reviewer). |
| F-02 | `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md` §8 says the vector file "contains fixed artificial three-item input"; the delivered file also contains 1/4/5-leaf and negative cases. | Added as extra cases; the normative three-item case is `MRKV1-3LEAF-NORMATIVE`. | Confirm the extended vector set is acceptable, or trim to the three-item case. |
| F-03 | Row canonical format v1 restricts JSON columns to integers and ASCII keys to guarantee JSONB/JCS agreement. | Documented in `row-integrity-format.md` §3.6. | Confirm no planned JSONB column needs floats; otherwise a v2 JSON subset must be designed before Phase 1. |
| F-04 | CODEOWNERS uses placeholder teams; CI has never run on GitHub; no SBOM/signing/SAST/secret-scan jobs yet. | Documented as BUILD-00/BUILD-05 pending. | Create the private repository and reviewer teams (PB-001/011). |

## 7. Remaining blockers before Stage A can be declared complete (not started)

1. Independent review and sign-off of `row-integrity-format.md` and both vector files (Gate 0 §9 items).
2. BUILD-00: push to the private GitHub repository, protected `main`, real CODEOWNERS teams, Entra OIDC federation.
3. Stage A implementation (ADR-PB-002/003): authority store migrations from the reference SQL, security command
   journal and projection barrier, `IIntegrityMacProvider` (Local/Vault/HSM) inside the Authority, record registry
   and manifest commit, business authority client adapter, SEC-P01..SEC-P12 against the Compose security-test stack,
   evidence manifest. `security-gate-a.yml` fails BLOCKED until then by design.
4. Phase 1 database schema with the naming validator and signed-column registry enforced in CI.
5. PB-11 build/sign/promote pipeline and PB-12 evidence chain (none exist).

## 8. Reproduction commands (fresh clone, Windows or Linux)

```sh
# specifications and Gate 0 vectors
python scripts/validate-specifications.py --strict
python scripts/integrity/verify_vectors.py && node scripts/integrity/verify-vectors.mjs
python scripts/integrity/generate_vectors.py && git diff --exit-code trust_authority/*.test-vectors.json

# .NET (requires SDK 10.0.201 per global.json)
dotnet restore EnterpriseForms.All.slnx
dotnet build EnterpriseForms.All.slnx -c Debug
git diff --exit-code contracts/openapi          # build regenerated the committed contracts without drift
dotnet test --solution EnterpriseForms.All.slnx --no-build -c Debug

# frontend (Node 22)
cd src/frontend && npm ci --ignore-scripts && npm run generate:api-client && git diff --exit-code packages/api-client/src/generated && npm run check

# containers
cd infrastructure/docker && cp env.example .env
for o in compose.dev.yaml compose.integration.yaml compose.security-test.yaml; do docker compose -f compose.yaml -f "$o" --env-file .env config --quiet && echo "ok $o"; done
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --wait business-db authority-db spicedb vault azurite authority api worker
curl -i http://127.0.0.1:5080/health/ready     # 200 only when the Authority is live
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env down -v
```

## 9. Change inventory

Uncommitted on branch `main` (one prior commit). New top-level entries: `.editorconfig`, `.github/`, `.gitignore`,
`.vscode/`, `Directory.Build.props`, `Directory.Packages.props`, `EnterpriseForms.All.slnx`, `contracts/`,
`global.json`, `infrastructure/`, `src/`, `tests/`, `scripts/integrity/`, `docs/dynamic-forms/`. Modified:
`docs/PREBUILD_OPEN_ITEMS.md`, `docs/architecture/FEATURE_OWNERSHIP_MATRIX.md`, `spec-files.json`, `SHA256SUMS.txt`.
No commit was made; review and commit are the owner's call.

**Phase 0 completion gate: reached.** Awaiting authorization to begin Stage A.
