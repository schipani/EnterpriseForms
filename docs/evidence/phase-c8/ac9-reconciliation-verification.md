# Phase C8 — AC-9, C8-M1-01 and C8-M1-02: implementation verification evidence (C-87)

- **Branch:** `phase-c8/ac9-durable-reconciliation`, created from the PR #31 head `69a6717` (PR #31 itself untouched).
- **Commits:** `a75827a` (stages 1–2), `c600288` (stages 3–5), `bc8fe05` (stage 6, incl. one bug fix found on the live stack), plus the documentation commit that records this file.
- **Who verified:** the implementation agent (Claude), on the owner's workstation. **This is not an independent review** and is not evidence of one (package §11).
- **Environment:** the isolated security-test stack (`enterpriseforms-sectest`, `ASPNETCORE_ENVIRONMENT=Testing`), rebuilt from scratch (`down -v`, `up --build`) on this branch; local subnet overlay 172.32.x (workstation-only). Synthetic data and synthetic keys only. What this evidence can and cannot prove: package §10 (logic and database-privilege properties — not transport, production identity or key custody).

## 1. Results

| Suite | Scope | Result |
|---|---|---|
| `SecurityCommandOutcomeServiceTests` (Authority unit) | AC-9 binding, pair evaluation at P, uniform not-found, rejection/quarantine status-only, read-only snapshot, fail-closed on inconsistent history | 15 / 15 |
| `ReconcilerBoundaryTests` (Authority host pipeline) | RC-18 credential negatives, other endpoints unreachable with the reconciler token, assertion refusal, 400/422 before the store, RC-27 rate limit, RC-28 bounds, C-1, C-2 | 19 / 19 |
| `TeamMembershipReconciliationUnitTests` | RC-22 every §1.3 rule, RC-28 D8-16 bounds and Testing-only shortening, RC-26 API never registers the port, C-1/C-2 Worker registration, client failure mapping and headers | 61 / 61 |
| `TeamMembershipReconcilerServiceTests` | reconciler end to end on a transactional in-memory store: §4.2 + R-6, RC-10, RC-11 stale race, RC-23, Q-5 drift, RC-22 through the service, RC-24, RC-25, RC-27, RC-07 + late commit, RC-08/09, D8-20 | 21 / 21 |
| `TeamMembershipSingleWriterTests` (architecture) | RC-26: no submit/user dependency in the reconciler, only the reconciler writes `team_memberships`, credential in the Worker only | included in 71 / 71 |
| `TeamMembershipReconciliationTests` (Stage B, live stack) | RC-01…RC-19, RC-21…RC-27 | **31 / 31** (run 3, 8 m 05 s) |
| Stage B regression (live stack, same images) | `PendingAuthorityCommandTests` (updated to Design R), `AdministrationTests`, `FormAccessTests`, `SecurityAdministratorTests`, `FieldSearchExecutionTests` | **35 / 35** (20 m 54 s) |
| Full suites at `bc8fe05` | `EnterpriseForms.Api.ContractTests` 407/407, `ArchitectureTests` 71/71, `AuthorizationAuthority.Tests` 206/206, business and Authority builds with `-warnaserror` 0 warnings, frontend lint/typecheck clean, Vitest 233/233 | passed |

### 1.1 RC closure set (D8-22) — where each test runs

| Test | Live stack | Without a stack |
|---|---|---|
| RC-01, RC-02 browser closed (add / remove) | ✔ | |
| RC-03 API restart while pending | ✔ | |
| RC-04 Worker absent, then started | ✔ | |
| RC-05 Worker crash mid-lease (lease honoured until expiry) | ✔ | |
| RC-06 commit before T2, found by key | ✔ | |
| RC-07 NotCommitted, never re-sent | ✔ | ✔ (+ late-commit sweep) |
| RC-08 rejected (G25 self-add, unknown user, already a member) | ✔ | ✔ |
| RC-09 FailedReconciliation → AuthorityFailed | ✔ (simulated Authority state) | ✔ |
| RC-10 C8-M1-02 add r1 / remove r2, both orders | ✔ (2 cases) | ✔ (2 cases) |
| RC-11 three Workers racing on one pair; benign stale answer re-looked-up, not alerted; fault inside the apply transaction rolls back roster, watermark and completion | ✔ (3 tests) | ✔ |
| RC-12 same key, different request | ✔ | |
| RC-13 tampered row (member, team, other-organization team) | ✔ (3 cases); kind/prefix mismatch is prevented by a CHECK constraint and covered without a stack | ✔ |
| RC-14 cross-organization key → uniform not found | ✔ | ✔ |
| RC-15 initiator loses the role while pending | ✔ | |
| RC-16 second administrator sees Pending → result by reading | ✔ | |
| RC-17 attention flag; "check again" audited, never submits; "mark reviewed" path | ✔ | |
| RC-18 AC-9 negative suite, no state change, key never logged | ✔ | ✔ |
| RC-19 grants/triggers against the runtime role; migration down and up on a scratch database | ✔ (2 tests) | |
| RC-21 add effective while remove pending | ✔ | ✔ (Authority unit) |
| RC-22 invalid/forged responses | | ✔ (validator, client boundary and the whole service) |
| RC-23 watermark raised by SQL; lowering refused by the trigger | ✔ | ✔ |
| RC-24 integrity quarantine | ✔ (simulated quarantine and clearance) | ✔ |
| RC-25 rotated reconciler credential | ✔ | ✔ |
| RC-26 architecture and deployment boundaries | ✔ (running containers) | ✔ |
| RC-27 rate limit | ✔ | ✔ |
| RC-28 bounds, Testing-only shortening, C-1 | | ✔ |

**Simulated states (labelled, test-only).** Stage A has no product path that quarantines an organization, clears a quarantine, or marks a command `FailedReconciliation` on demand. RC-09 and RC-24 therefore write those states into the disposable AuthorityDb from inside its own container. This proves how the business side reacts to them; it does not test the Authority's own divergence detection, which has its own suites.

## 2. Defects found during verification

| # | Defect | Found by | Fix |
|---|---|---|---|
| V-1 | Failure audits used outcome `Failure`; `ck_audit_events_outcome` allows only `Success`/`Denied`/`Failed`. Every failure path rolled back and retried instead of reaching `ReconciliationError`/`NotCommitted`/`AuthorityFailed`. The in-memory service tests could not see it (no constraint). | live RC run 1 | `Failed`; service tests now assert the constrained vocabulary (`bc8fe05`) |
| V-2 | Test harness issues only (no product change): worker start detection when already running; RC-09 simulation overwritten by the Authority's own projection when applied before the archive resumed; jsonb `LIKE` without a cast; the from-empty migration script fails inside an older, unrelated migration (RC-19 now starts from a schema dump of the live migrated database). | live RC runs 1–2 | test code |

Run history: run 1 stopped after V-1 was diagnosed; run 2 24/31 (all 7 failures test-side, V-2); run 3 31/31.

## 3. Not run / not proven

- **The rest of the Stage B suite** (document, encryption, search, lifecycle and other classes not touching team membership) — not re-run in this session; the full suite takes over an hour on this workstation. Required with the hosted run (D8-19).
- **Hosted CI** (`ci`, `stage-b-integration`, `security-gate-a`, `level3-script`) — not run: no `gh` credential here and no PR for this branch. Required by D8-19 before closure.
- **Playwright** `organization-administration.spec.ts` (UI Pending → Effective) — not run in this session (browser runs need the isolated web origin; the owner's dev Vite and dev stack were left untouched). The component logic is covered by Vitest; the spec's assertions still match the new page (Pending text, session key, Effective notice).
- **Manual NVDA/VoiceOver** of the changed team page — not performed.
- **Transport and identity** (mTLS, managed identities, Managed HSM, Azure) — not provable on this stack (package §10); C-1 only makes them a start condition.
- **Independent security review of AC-9** — not performed. The owner and Claude are not independent reviewers.
