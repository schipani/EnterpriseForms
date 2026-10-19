# Stage A Change-Set Review (pre-merge)

**Reviewed:** the complete uncommitted Stage A working tree on 2026-10-18 before it was placed on the
`stage-a/level2-security-foundation` branch (43 modified tracked paths, 53 untracked paths/directories, 97 after
line-ending normalization). Method: path-by-path classification against the Stage A authorization, a pattern scan for
secrets and development shortcuts, `git status --ignored` for leakage of build output or `.env`, and a missing-test
pass per capability. This is an author self-review; it does not replace the CODEOWNERS security-owner review on the PR.

## 1. Scope classification

| Area | Paths | Verdict |
|---|---|---|
| Authority store, commands, projection, journal/archive, signing, content protocol, operator plane, host | `src/authority/**` (29 modified + new) | In scope (requirements 1–5). |
| Business minimal workflow, authority HTTP adapter, auth, schema, migration | `src/backend/**` (30) | In scope (requirement 5). No UI, no Accused pages. |
| Shared integrity: `RowCanonicalHeader`, new error code | `src/shared/EnterpriseForms.Integrity/**` (2) | In scope (binding checks); vectors unchanged. |
| Compose stack, DB init scripts, env example, Dockerfile use | `infrastructure/docker/**` (7+2) | In scope (requirement 6). |
| Tests: security suite, authority unit tests, architecture/contract updates | `tests/**` | In scope (requirements 6, 7, 8). |
| CI: `ci.yml`, `security-gate-a.yml`, ruleset JSON | `.github/**` | In scope (requirements 8, 9). |
| Contracts: compiled OpenAPI (both planes), generated TS client, reference yaml note | `contracts/**`, `src/frontend/packages/api-client/**`, `trust_authority/trust-authority.openapi.yaml` | In scope; generated from compiled endpoints, no hand edits. |
| Docs: status, decision log, review package, BUILD-00, open items, ownership matrix, traceability register, row-integrity §7/§9, evidence copy | `docs/**`, `scripts/integrity/generate_control_checklist.py`, `spec-files.json`, `SHA256SUMS.txt` | In scope (requirements 9–12). |
| `.config/dotnet-tools.json` (dotnet-ef 10.0.13), `.gitattributes`, `Directory.Packages.props`, `EnterpriseForms.All.slnx` | repository plumbing | In scope / required for reproducibility. |

**Out-of-scope changes found:** none. No Stage B feature, UI page, Azure resource, or production configuration was added.

## 2. Secrets and development shortcuts

| Finding | Location | Assessment |
|---|---|---|
| Synthetic credentials (`*-dev-only-change-me`, `dev-only-token`, base64 HS256 keys, Azurite well-known key) | `infrastructure/docker/env.example`, `appsettings.Development.json` (both hosts) | Synthetic, labelled, local-only. `.env` is git-ignored and untracked. No cloud, IdP or registry credential present. |
| `sslmode=disable` on the SpiceDB datastore connection | `compose.yaml` | Pre-existing Phase 0 local shortcut inside the internal authority network; production uses Azure PostgreSQL TLS (PB-05). Recorded as A-F7-class limitation. |
| `LocalDevelopment` MAC provider | authority | Registered only in Development/Testing; Production refuses it at host start (tested). |
| TestIdentityProvider (HS256 user assertions) | both hosts | Refused outside Development/Testing by startup guards (tested in contract tests). |
| Fault injection (process crash points) | authority | Bound to `NoOpFaultInjector` unless `FaultInjection:Enabled` AND environment is Testing; operator credential required to arm. |
| `InMemory` archive provider | authority | Development only; Testing/Staging/Production get the fail-closed `UnavailableSecurityEventArchive` unless `Blob` is configured. |
| `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` | `CanonicalJson` (journal bodies/digests) | Not an HTML context; keeps canonical bytes stable across platforms. Reviewer question R8 covers it. |
| Plain HTTP between planes inside Compose | compose | Local emulation; mTLS/workload identity are PB-05/PB-08 requirements before Staging. |
| `TODO` markers | vendored `openapiv2.proto` only (third-party) | Not project code. |

No private keys, bearer tokens, connection strings with real hosts, or `AllowAnonymous` endpoints were found. The
business API container's environment carries no Vault, SpiceDB, archive, AuthorityDb or operator settings (asserted by
SEC-P11 step 7).

## 3. Defects found during closure review and fixed in this branch

| ID | Defect | Fix |
|---|---|---|
| R-01 | Phase 0 CI on GitHub failed: `spec-files.json` digests were computed over CRLF working copies (`core.autocrlf=true`) while Linux checkouts are LF; strict validation reported 15 digest mismatches. | Added `.gitattributes` (`* text=auto eol=lf`), renormalized the index and working tree, made every generator write LF bytes explicitly, and the manifest generator now refuses CRLF inputs. |
| R-02 | Phase 0 CI `.NET` job failed on Linux in `UnconfiguredProviderRefusesStartupInEveryEnvironment` (host-start exception surfaced differently than on Windows). | The Stage A contract tests already flatten the exception chain; the Linux container run of the solution (see `STAGE_A_GATE_CLOSURE.md` §3) is the verification. |
| R-03 | Generator scripts wrote platform line endings (`Path.write_text`, `csv.writer` default terminator). | All three generators write bytes with `chr(10)`. |

## 4. Missing-test pass

| Capability | Tests present | Gap |
|---|---|---|
| Command authorization/planner | `CommandPlannerTests` (6) | UpdateShareScope/GrantSystemRole/etc. are unimplemented (422) — no tests by design. |
| Canonical binding, providers, journal chain, SpiceDB schema | `IntegrityBindingTests` (8) | Vault adapter exercised only in Docker (SEC-P04/P05/P08/P11) and by the SIG-11 vector file; no unit mock. |
| Projection processor reconciliation | SEC-P07, SEC-P09 (Docker) | No in-process unit test of `ProjectionProcessor` with a fake SpiceDB; recommended follow-up (CONS-06/08/11 unit coverage). |
| Business authority adapter error mapping | contract tests (readiness, 401) | No unit test of `AuthorityHttpClient` status→code mapping; low risk (fail-closed default is 503). |
| Signed-column registry | `SignedColumnRegistryTests` (3) | Covers both Stage A tables; future tables must be added. |
| Audit projection (`audit_events`) | none | Not implemented in Stage A (Phase 1C); recorded as A-F9. |

Nothing in this review waives a mandatory control. Items marked follow-up are tracked in `STAGE_A_GATE_CLOSURE.md`.
