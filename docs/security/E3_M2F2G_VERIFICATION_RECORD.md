# E3-M2f-2G — Controlled PostgreSQL Verification of the Envelope Capability Floor: Verification Record

**Status (2026-11-16; owner acceptance recorded 2026-11-17, §20.10):**

| Item | State |
|---|---|
| Increment | E3-M2f-2G, as specified in `E3_M2F_CONTRACT_CLOSURE.md` §12.3 (governing), tests M2F-34..M2F-40, and the owner's E3-M2f-2G authorization |
| Authorization | Owner, 2026-11-16: implementation and execution against isolated, disposable Development/Testing resources only, including Envelope v2 acceptance **inside the disposable environment only** (closes OPEN-M2f-4 for this run). No commit, push, merge, deployment, Azure call, Stage B run or persistent database change |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `af320fc4e9d3f95c640f40dea31c60e3c44ccc77` (E3-M2f-2), good SSH ED25519 signature, clean tree |
| Commit | Owner review 2026-11-17 (§20): results conditionally accepted (OA-M2f2G-01); the signed commit then not yet authorized (OA-M2f2G-02). Final owner acceptance 2026-11-17 (§20.10): **exactly one signed local commit authorized (OA-M2f2G-13)**, containing this record; its SHA is reported in the completion report, since a commit cannot contain its own hash. Not pushed or merged |
| Follow-up (2026-11-17) | MG-17 correction (§20): test harness and mutation driver only; MG-17c DETECTED by its declared detectors GS-01 and GS-02, which pass on the restored baseline |
| Environment | Development/Testing. Fresh `postgres:18.0` and `hashicorp/vault:1.20.4` containers per run (tmpfs, loopback, random names, ports and credentials), removed after every run |
| Review | Owner-led. Not an independent security review and not a certification. V2-IND-01 is still required before production |
| Production source | **Unchanged.** No product source, migration, CARV, floor, capability enforcement, cryptographic algorithm or configuration file was modified. Only test harnesses, gate scripts, documentation and evidence |

---

## 1. Baseline verification

| Check | Result |
|---|---|
| Branch and HEAD | `e3/m1-envelope-v2-core` at `af320fc4e9d3f95c640f40dea31c60e3c44ccc77` |
| Signature | `G`: good `git` signature, ED25519 `SHA256:t6bvk1LKAW33NACJ+JrCxzj1JcgKGHY/RYT4XdX9HJg` |
| Working tree | Clean at the start |
| Closure records | `E3_M2F_CONTRACT_CLOSURE.md` (ba7b957), `E3_M2F1_IMPLEMENTATION_RECORD.md` (6e0ee1d), `E3_M2F2_IMPLEMENTATION_RECORD.md` (af320fc) present |
| CARV and floors | `ContentAcceptanceRules.CurrentVersion = 3`; recognized floors 1, 2, 3 (also reported by the harness's evidence header) |
| Release B enforcement | `RequireContentAcceptanceFloorAsync` first in Prepare and Commit (also observed on PostgreSQL: the statement trace of every refused Prepare and Commit is `org-lock > fence > fence > ledger-exists`) |
| Data-key ledger and DK protections | Unchanged; the E3-M2e-2 ledger groups (L, P, N, C, D, R) pass unchanged (§6) |
| Capability declaration | `EnvelopeCapability.Declare` in the three business clients; on the wire every request of the current build carried exactly one header `2` (GH-01, 41 requests) |
| Capability enforcement | `RequireAsync` at the seven entry points; exercised at all seven on PostgreSQL (§7) and through the real host (§9) |
| Envelope v2 acceptance | Default off; no repository configuration file mentions `EnvelopeV2AcceptanceEnabled` (checked by the gate before and after every run: 0 / 0) |

## 2. Files changed

**Modified (test harness and gate script only):**

| File | Change |
|---|---|
| `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1` | Builds the historical business builds (`git archive` 11e15ee, 0be5f52) and the baseline Authority (`git archive` 0be5f52), builds one BusinessAgent per business build and one AuthorityAgent per Authority build, creates the Authority Transit mount and a token holding only `authority-transit-policy.hcl` in the disposable Vault, passes the agents to the harness, copies agent outputs (never inputs) into the evidence, reports the E3-M2e-2 regression and the M2f-2G totals separately, counts logged lock waits, and canonicalizes the work and evidence paths before scrubbing |
| `tools/experiments/e3-m2e2-acceptance-gate/HarnessE2/Program.cs` | Capability-aware request helpers (`BusinessBuild`), the new groups G, GC, GR, GH, separate reporting, the agent inputs, `journalDigest` on seeded organizations, PostgreSQL diagnostics |
| `.../HarnessE2/Support.cs` | `BusinessBuild` (the build a request models and its declaration); `Factory.For` accepts several interceptors |
| `.../HarnessE2/Rows.cs` | `StoredRow`: the stored business row a staged row came from, so another build can encode it |
| `.../HarnessE2/ScenariosConcurrency.cs`, `ScenariosNegative.cs`, `ScenariosPositive.cs` | The six direct service calls declare the current build explicitly (no assertion changed) |
| `tools/experiments/e3-m2e2-acceptance-gate/README.md` | E3-M2f-2G section |

**New:**

| File | Purpose |
|---|---|
| `.../HarnessE2/CapabilitySupport.cs` | Statement and transaction tracing, `PauseAfter` and `FailCommit` interceptors, SpiceDB double |
| `.../HarnessE2/ScenariosCapability.cs` | Group G |
| `.../HarnessE2/ScenariosCapabilityConcurrency.cs` | Group GC |
| `.../HarnessE2/ScenariosCapabilityRecovery.cs` | Group GR |
| `.../HarnessE2/ScenariosCapabilityHost.cs` | Group GH |
| `.../HarnessE2/CapabilityDiagnostics.cs` | PostgreSQL diagnostics file |
| `.../HarnessE2/ScenariosCapabilityShortCircuit.cs` (follow-up, §20) | Group GS: the self-contained MG-17 detector (GS-00..GS-03) |
| `tools/experiments/e3-m2f2g-capability-gate/BusinessAgent/` (`Program.cs`, `BusinessAgent.csproj`) | Historical business agent |
| `tools/experiments/e3-m2f2g-capability-gate/AuthorityAgent/` (`Program.cs`, `AuthorityAgent.csproj`) | Baseline / control Authority agent |
| `tools/experiments/e3-m2f2g-capability-gate/README.md` | Agent description |
| `scripts/dev/e3-m2f2g-mutations.ps1` | PostgreSQL-backed enforcement-removal driver (`-DryRun` anchor check) |
| `docs/security/E3_M2F2G_VERIFICATION_RECORD.md` | This record |
| `docs/evidence/e3-m2f2g/record-1/`, `record-2/`, `mutations/` | Sanitized evidence |
| `docs/evidence/e3-m2f2g/followup-baseline/`, `followup-mg17/` (follow-up, §20) | Sanitized evidence of the targeted follow-up |

`spec-files.json` and `SHA256SUMS.txt` are regenerated (§16). No file under `src/`, no migration, no configuration file, no CI workflow and no
product test project changed.

## 3. Disposable environment and isolation

Every run (two runs of record, every iteration and every mutation run) used:

| Control | Evidence |
|---|---|
| Fresh PostgreSQL 18.0 container, `--rm`, tmpfs, `127.0.0.1` only, random name (`m2e2-gate-<nonce>`), random free port, random admin and runtime passwords | `postgres-version.txt`, `postgres-image.txt` per run |
| Fresh Vault 1.20.4 dev container, `--rm`, `127.0.0.1` only, random name, port, root, business and authority tokens | `vault-image.txt` per run |
| Every database admitted only after its per-run nonce comment, the cluster system identifier (read inside the container) and server port 5432 were verified; every connection string passes `Target.Require`; the Vault address passes `Target.RequireVault`; the agents refuse any non-loopback target | `m2e2-evidence.json` → `isolation.databases`: 12 admitted databases per run (`m2e2gate`, `postgres`, the E3-M2e-2 copies and host databases `m2e2_floor2`, `m2e2_t0`, `m2e2host`, `m2e2noledger`, and the M2f-2G copies `m2f2gcf`, `m2f2gbase`, `m2f2grst`, `m2f2gbd2`, `m2f2gbd2p` and host database `m2f2ghost`), every one created in the run's own container |
| Inherited `ConnectionStrings__*`, `Content__*`, `ContentAcceptance__*`, Azure, ASP.NET Core environment, signing, SpiceDB, identity, search-index, Vault and `PG*` settings scrubbed before anything ran | S-01 (removed `[]`, remaining `[]`) |
| The host copy of this tree's Authority has no `appsettings.Development.json`; every host setting is explicit | H-00..H-06, GH-00 |
| Persistent stacks untouched: the running `enterpriseforms-sectest-authority-1` (127.0.0.1:5190) and other local containers were never addressed; the container list after each run equals the list before it | `summary.json`: `ContainerListUnchanged = true` |
| Isolation violations | None (no STOP check in any run) |

No Azure endpoint, persistent database, persistent Vault, SpiceDB or Stage B stack was contacted.

## 4. Acceptance activation and shutdown

Unchanged from E3-M2e-2 (closure §6.2, OD-M2e-16), with the owner's E3-M2f-2G authorization:

| Step | Evidence (each run of record) |
|---|---|
| Floors 1 → 2 → 3, Release B `RequiredFloor = 3` verified, ledger migration recorded | F-01..F-15, S-11 |
| Switch refused outside Development/Testing and without Release B floor 3 | S-12 |
| Acceptance configured on only on in-process switch objects (literal Testing configuration) and effective only after the exact policy is recorded on the disposable platform journal | S-13..S-15 |
| Host (group H, GH): acceptance effective only after the host itself recorded the policy (Development, in-memory archive); refused in Staging, Production, Release B floor 2 and without the ledger migration | H-01..H-06; GH-00 (`policy True`) |
| Shutdown: the switch objects and the host processes end with the harness; every container removed; no repository configuration enables acceptance before or after | `ContainersRemoved = true`; `ConfigEnablersBefore/After = 0/0` |

## 5. Capability-aware harness (requirement §4)

| Requirement | Implementation |
|---|---|
| Current-build requests declare level 2 | Every in-process request names the business build it models (`BusinessBuild`). The E3-M2e-2 scenarios test Authority rules with a business that represents schema 2 faithfully, so their helpers default to `BusinessBuild.Current` and their six direct service calls pass `BusinessBuild.Current.Declared` explicitly |
| Legacy-build requests omit the header or declare level 1 | `BusinessBuild.Undeclared` (no header; the in-process argument `null`, exactly what the Api passes for no header) and `BusinessBuild.DeclaresOne` (`1`), used only where a check is about older builds; real older builds run as separate processes and send what their own clients send (GH-01: 11e15ee and 0be5f52 sent no header on all 26 requests) |
| Invalid, duplicate and unknown declarations | Through the real host's header parsing (GH-06): absent, `1`, `3`, `02`, empty, `two`, `1,2`, `+2`, two instances `1` and `2`, two instances `2` and `2`, `2.0`: all refused; `2` admitted |
| Not blindly level 2 | Group G and GC use `Undeclared`/`DeclaresOne` for every legacy request; the frozen CARV-1/CARV-2 agents are Authority builds (no header concept) and are unchanged |
| Original E3-M2e-2 assertions preserved | No `Record(...)` expected value, pass condition or check id of the E3-M2e-2 groups changed (the diff of those files changes only call arguments); 138 / 138 pass in both runs of record |
| Five expected-gap demonstrations preserved | R-02, R-03, R-04, R-05 (`EXPECTED_LIMIT`) and R-07 (`EXPECTED_GAP_BD1`) behave as before in both runs |

## 6. Original E3-M2e-2 regression (against the E3-M2f-2 Authority)

| Run | Counted | Passed | Failed | Expected-gap demonstrations (not counted) |
|---|---|---|---|---|
| Preliminary (harness capability-aware only, before the M2f-2G groups existed) | 138 | 138 | 0 | 5 (as expected 5) |
| Record 1 (groups S, F, P, N, C, X, D, L, R, H, setup, teardown) | 138 | 138 | 0 | 5 (as expected 5) |
| Record 2 | 138 | 138 | 0 | 5 (as expected 5) |

The E3-M2e-2 totals are reported separately from the M2f-2G totals (`m2e2Regression` and `m2f2g` in `m2e2-evidence.json`).

## 7. G-2 at the seven entry points (requirement §6, M2F-10..16 on PostgreSQL)

Organization with bindings and schema-2 heads produced by the real E3-M1 core (field batches, and one document). The level-1 bytes come from
the **real 11e15ee business build** (BusinessAgent): its registry re-encodes exactly the schema-2 rows as schema 1 because it does not know
the v2 columns (G-03: `DataKeyIdentifier`, `DataKeyCommitmentBytes`, `KeyWrapAlgorithmCode` ignored; G-01 and G-02 show the current and
0be5f52 builds re-encode byte-identically).

| Entry point | Refusal (no header and declared `1`) | Statement trace of the refused transaction | Level-2 control | Counterfactual on a copy (same bytes declared `2`) | Baseline 0be5f52 (`EXPECTED_GAP_G2`) |
|---|---|---|---|---|---|
| Prepare | G-10: 409 x2, plus the carry shape | `org-lock > fence > fence > ledger-exists > rollback` | G-10c: prepared | G-20: `CURRENT_SNAPSHOT_MISMATCH` journaled, integrity revision +1 (removal shape); G-20a: carry shape refused 422 by S-4 first, no incident | GG-01: same as G-20 |
| Commit | G-11: 409 x2, pending operation still Prepared | `org-lock > fence > fence > ledger-exists > rollback` | G-11c: committed | G-21: `CANDIDATE_MISMATCH` 409 after 2 MAC verifications; operation still Prepared; no incident | GG-02: same as G-21 |
| Verify | G-12: 409 x2 | `org-lock > ledger-exists > rollback` | G-12c: verified | G-22: head quarantined, incident, integrity +1 | GG-03: quarantined, incident |
| Batch Verify | G-13: 409 x2 | `org-lock > ledger-exists > rollback` | G-13c: verified | G-23: quarantined, incident | GG-04: quarantined, incident |
| Census | G-14: 409 x2 | `snapshot lookup > org-lock > ledger-exists > rollback` | G-14c: Verified | G-24: quarantined, incident | GG-05: quarantined, incident |
| Backfill | G-15: 409 x2 | `org-lock > ledger-exists > rollback` | G-15c: Indexed | G-25: quarantined, incident | GG-06: quarantined, incident |
| Deep verify | G-16: 409 x2 | `org-lock > ledger-exists > rollback` | G-16c: Pass | G-26: `BUSINESS_BYTES_MISMATCH`, quarantined, incident | GG-07: quarantined, incident |

For every refusal (G-10..G-16), in both runs: `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, 409, `retryable = false`; **0 HMAC verifications and
0 signatures** (the deterministic test MAC counts every call), so the refusal precedes signed-content verification; **0 statements after the
boolean existence query**; no transaction commit; no lock held afterwards (`pg_locks`); the head not quarantined; no
`IntegrityVerificationFailed` or `CurrentSnapshotMismatch`; the content-integrity revision unchanged; the pending operation unchanged; and
**every Authority row of the organization unchanged** (a digest of every row of every authority table with an `organization_id` column, plus
the row count of every other authority table). G-30: the AuthorityAgent source compiled against this tree, with no capability argument, is
refused on the same copy where the baseline build is not.

**Demonstrated harm by entry point (O-1, OA-M2f2G-06).** Without the floor, the false integrity outcome of G-2 was demonstrated for Verify,
batch Verify, census, backfill and deep verify (false quarantine and `IntegrityVerificationFailed`) and for Prepare only with a valid
candidate and a re-encoded observed head (`CurrentSnapshotMismatch` and an integrity revision; a full legacy re-stage is refused earlier by
S-4 with no incident). At Commit the legacy bytes are rejected by staged verification (`CANDIDATE_MISMATCH`) with no incident and no abort.
This narrows what closure §6.3 states, not what it requires: **the capability check remains required at all seven entry points (CAP-O3,
CAP-O4, OD-M2f-08)**, ahead of signed-content verification, and it is verified there (G-10..G-16, GH-02, GH-03).

## 8. First binding and concurrency (requirement §5, M2F-34)

Real PostgreSQL, transactions held open at exact statements (`PauseAfter`), waits read from `pg_stat_activity` and `pg_blocking_pids`, locks
from `pg_locks`:

| Check | Result (both runs) |
|---|---|
| GC-01 FB-1, binding committed | The first-binding Prepare, paused after inserting its binding, holds the organization row locked `FOR UPDATE` (`RowShareLock` on `organization_authorization_states`) and `RowExclusiveLock` on the ledger; a level-1 Verify **waits on the organization lock** (`Lock/transactionid`, blocked by the paused Prepare), the uncommitted binding is invisible to others; after the commit the Verify is refused |
| GC-02 FB-1, binding rolled back | Same, but the Prepare's commit fails (`FailCommit`); the waiting Verify then decides "pre-binding" and is admitted and verified; no binding and no operation survive |
| GC-03 reverse order, no stale negative | A level-1 Verify paused right after its existence query holds the organization row and only `AccessShareLock` on the ledger; the first-binding Prepare waits for it; the Verify is admitted; afterwards the **same node** and a second node are refused (nothing cached) |
| GC-04 Commit vs first binding (closure §4.2 point 4) | A level-1 Prepare admitted before the first binding; its level-1 Commit waits on the first-binding Prepare's lock and is then refused; the operation stays `3/NULL Prepared`; the head does not move; the current build commits it afterwards |
| GC-05 isolation | While one organization's first-binding Prepare is paused, level-1 Verifies in another unbound (admitted) and another bound (refused) organization complete without waiting |
| GC-06 mixed stress | 4 rounds x 6 concurrent requests across two organizations: 24 outcomes all in their expected sets; `pg_stat_database.deadlocks` unchanged |
| G-40..G-51 (CAP-F1/F3 matrix, M2F-07/38) | Verify with C in {none, 1, 2} x D in {1, 2} x O in {1, 2}: refused exactly when C < max(D, O); the existence query runs only when D <= C < 2 (statement count from the trace) |
| G-55..G-57 (CAP-F2, M2F-08) | A binding whose operation is Prepared, Aborted (switch off at Commit) or expired raises the organization floor |
| G-60 (AM-F1 #1) | The only ledger statement of an admitted level-1 Verify: `SELECT EXISTS ( SELECT 1 FROM authority.content_data_key_bindings AS c WHERE c.organization_id = @organizationId)`, one parameter, after the organization lock |
| G-61 | With sequential scans disabled the planner answers it from `ix_content_data_key_bindings_record`; the default plan on the small disposable ledger is a sequential scan (diagnostic) |
| G-62 | Runtime role privileges on the ledger: SELECT and INSERT only (unchanged, closure L-8) |
| G-63 (CAP-F5, M2F-24) | With the runtime role's SELECT on the ledger revoked (superuser; restored at once; the 42501 is labelled expected), level 1 is refused `AUTHORITY_NOT_READY` 503, never admitted; level 2 is unaffected |
| Lock order | The capability decision takes only `AccessShareLock` on the ledger after the organization row lock (and after the Release B fence locks for Prepare and Commit); no run logged a deadlock, a lock timeout or a lock wait longer than `deadlock_timeout` |

## 9. Historical-client compatibility (requirement §7; M2F-35, M2F-36, M2F-38)

**Historical builds (unmodified, `git archive` + Release build):**

| Build | Commit | Role | Limitations |
|---|---|---|---|
| Business 11e15ee | `11e15ee7d7c3d9e9be0685c2330ec006503bf1ec` (RB-CARV-1, before E3-M2b-2) | Pre-schema-2 registry, no header | Built from `src/backend/EnterpriseForms.Api`; the agent drives its Authority, verifier and builder clients (the same Infrastructure assembly the Worker uses), not the Worker host process; it encodes stored row values handed over by the harness instead of reading BusinessDb |
| Business 0be5f52 | `0be5f522456acfa2102cf3e709ee9de1774a3d3a` (E3-M2e-2) | Faithful schema-2 registry, no header | Same |
| Business (current) | this tree (af320fc + harness changes; no product change) | Declares 2 | Same |
| Authority 0be5f52 | `0be5f522456acfa2102cf3e709ee9de1774a3d3a` | CARV 3 baseline without E3-M2f-2 | Driven in-process by the AuthorityAgent with the test MAC and a SpiceDB double |

The record header encoding is the harness's (form_records schema 3, identical in all three builds); every value and attachment row is encoded
by the build under test.

**Results through the real Authority host** (this tree's Release output, Development, Vault Transit signer, Release B floor 3, acceptance
configured on; requests sent by each build's own clients):

| Check | Result (both runs) |
|---|---|
| GH-01 | On the wire: 11e15ee 14 requests, 0be5f52 12 requests, all **without** the header; current build 41 requests, all with exactly `[2]` |
| GH-02 (M2F-36) | 0be5f52 in an organization with a binding: all seven endpoints refused, fixed 409 non-retryable as seen by its own client; organization rows unchanged |
| GH-03 (M2F-35) | 11e15ee's own requests (schema 2 re-encoded as schema 1): all seven refused before verification; no quarantine, no incident; organization rows unchanged |
| GH-04 | Level-1 organizations stay compatible: both builds Prepare + Commit v1 changes; census Verified; backfill Indexed; deep verify Pass; user Verify and batch Verify pass the capability check and then stop as not ready on the unreachable SpiceDB (not the capability refusal) |
| GH-05 | Mixed clients: a current-build operation cannot be committed by 0be5f52 (refused, still Prepared; the current build commits it); an operation 11e15ee prepared before the organization's first binding cannot be committed by 11e15ee afterwards (refused, still Prepared; the current build finishes it) |
| GH-08 (M2F-38) | Host with `Content:MinimumBusinessEnvelopeCapability = 2`: 0be5f52 write and 11e15ee census refused in an organization without a binding (rows unchanged); the current build writes; startup log names deployment floor 2 |
| GH-09 (M2F-29) | `= "3"` refuses the host start; the output names the configuration key |
| GH-07 | Startup log names deployment floor 1 when the key is absent |
| No false quarantine before refusal | No historical client produced a quarantine, incident or integrity-revision change in a bound organization of this tree (G-10..G-16, GH-02, GH-03) |

## 10. Restore and rollback (requirement §8; M2F-39, M2F-40)

Demonstrated only on disposable copies (`CREATE DATABASE ... TEMPLATE`). Classified, never counted as passes:

| Id | Classification | What was observed (both runs) |
|---|---|---|
| GR-01 (M2F-39, BD-1, R-M2f-4) | `EXPECTED_GAP_RESTORE` | A copy restored to before the organization's first binding has 0 bindings (live 2) while the business holds schema-2 rows written after the restore point: expected floor 2, computed 1; a level-1 Verify is **admitted** |
| GR-02 | Counted (Security) | The deployment floor is the restore-independent backstop: on the same copy D = 2 refuses level 1 without reading the ledger and admits level 2 |
| GR-03 | `INCONCLUSIVE` | On the restored copy the business is ahead of the Authority for the post-restore records: a level-1 client (legacy bytes) and a level-2 client (faithful bytes) are both rejected (`ITEM_DIGEST_MISMATCH`, heads quarantined); a G-2 effect cannot be isolated from the restore discrepancy |
| GR-04 | Counted (Security) | Partially reconstructed ledger (one of two bindings removed on a copy): any surviving binding keeps the floor at 2; the 11e15ee Verify is refused, no quarantine |
| GR-05 | Counted (Security) | Ledger with every binding of the organization removed (copy): D = 2 refuses the 11e15ee Verify, no quarantine |
| GR-06 (BD-2) | `EXPECTED_GAP_BD2` | Same copy with D = 1: the floor computes 1 although the accepted head is schema 2; the 11e15ee Verify is admitted and its re-encoded bytes **falsely quarantine** the head (G-2 reproduced) |
| GG-INV (M2F-40, R-M2f-3) | `EXPECTED_GAP_INVENTORY` | The baseline 0be5f52 Authority (CARV 3, admitted by floor 3) admits the 0be5f52 business build's level-1 Verify in a bound organization and verifies it; floor 3 and CARV markers cannot distinguish it from an E3-M2f-2 build (INV-3) |
| GG-01..GG-07 | `EXPECTED_GAP_G2` | §7 last column |
| GH-07r | `INCONCLUSIVE` | The composed readiness text (INV-4) is not observable: readiness needs SpiceDB, which the run does not provide (503 `SpiceDbUnavailable`) |

BD-1 and BD-2 were not remedied. No restore touched a persistent database.

## 11. Enforcement-removal mutations (requirement §9)

Driver: `scripts/dev/e3-m2f2g-mutations.ps1` (base work directory: run of record 1). Each mutation was applied with an exact single-match
anchor (`-DryRun` confirms every anchor occurs once) to a `git archive` scratch copy of HEAD only, built with `--no-incremental`, and run
through the gate (`-NewBinaries`: the in-process Authority **and** the host copy) against fresh disposable containers with the focused groups
below; the scratch file was then restored, touched and sha256-verified, and the repository file's sha256 was verified unchanged. A
mutation counts as detected only when at least one of its **pre-declared** detecting checks fails.

| Id | Requirement §9 class | Mutation | Groups | Detected by (declared) | All failing checks |
|---|---|---|---|---|---|
| MG-01 | Missing enforcement | Check removed from Prepare | G | G-10 | G-10 |
| MG-02 | Missing enforcement | Check removed from Commit | G | G-11 | G-11 |
| MG-03 | Missing enforcement | Check removed from Verify | G | G-12 | G-12, G-12c, G-30, G-40, G-41, G-43, G-44, G-46, G-47, G-49, G-50, G-55..G-57, G-60, G-63 |
| MG-04 | Missing enforcement | Check removed from batch Verify | G | G-13 | G-13, G-13c |
| MG-05 | Missing enforcement | Check removed from the census | G | G-14 | G-14, G-14c |
| MG-06 | Missing enforcement | Check removed from backfill | G | G-15 | G-15, G-15c |
| MG-07 | Missing enforcement | Check removed from deep verify | G | G-16 | G-16, G-16c |
| MG-08 | Enforcement after verification | Verify check moved after `VerifyRecordCoreAsync` | G | G-12 | G-12 (still refused, but 4 MAC verifications and verification statements before the existence query) |
| MG-09 | Incorrect organization-floor evaluation | Existence query not keyed on the organization | G | G-43, G-44, G-60 | G-43, G-44, G-55..G-57, G-60 |
| MG-10 | Incorrect organization-floor evaluation | Existence result never refuses | G | G-10, G-12, G-40 | G-10..G-16 and their controls, G-30, G-40, G-41, G-55..G-57 |
| MG-11 | Missing deployment-floor enforcement | `C < D` no longer refuses | G, GR | G-46, G-49, G-50, GR-02, GR-05 | G-46, G-47, G-49, G-50, GR-02, GR-05 |
| MG-12 | Malformed header accepted as 2 | Lenient parser (padded, signed, zero-prefixed, listed) | GH | GH-06 | GH-06 |
| MG-13 | Missing header accepted as 2 / historical-client bypass | Api treats a request without the header as level 2 | GH | GH-02, GH-03, GH-05, GH-06 | GH-02, GH-03, GH-05, GH-06, GH-08 |
| MG-14 | Failure to serialize against the first binding | Verify decides before the organization lock | GC | GC-01 | GC-01, GC-03 |
| MG-15 | (no stale negative) | Negative existence result cached across requests | GC | GC-03 | GC-03, GC-04 |
| MG-16 | Unauthorized ledger reads | Existence check materializes binding rows | G | G-60 | G-60 |
| MG-17 | Unauthorized ledger reads | Level-2 short-circuit removed (level 2 reads the ledger) | G | **none (see below)** | HARNESS (setup aborted) |
| MG-17b | (same mutation) | Same, groups P and GC | P, GC | **none (see below)** | P-02, P-03, P-06, P-07, P-09, HARNESS |
| MG-18 | Incorrect refusal rollback | Commit refusal journals an incident and commits | G | G-11 | G-11 |
| MG-19 | Deployment configuration | Invalid deployment floor accepted as 1 | GH | GH-09 | GH-09 |

**Totals: 18 of 19 mutations detected by their declared checks; every scratch file restored; every repository file unchanged; every
container removed.** Under OA-M2f2G-03 the attempt-3 results of MG-17 and MG-17b are classified **INCONCLUSIVE** (no declared detector
executed). The targeted follow-up MG-17c is DETECTED by its declared detectors (§20).

**MG-17 is not counted as detected by its declared checks.** Removing the level-2 short-circuit makes level-2 requests read the ledger and
be refused in a bound organization, so the current build's own Commit is refused as soon as a binding exists. In group G that breaks the
setup itself: the harness aborted while seeding the census fixture (`Sequence contains no elements`) before any declared check (G-10c, G-12c,
G-42) ran. MG-17b re-ran the same mutation against groups P and GC with detectors declared before the run (P-01, GC-03, GC-06): P-02, P-03,
P-06, P-07 and P-09 failed with `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT` on the current build's level-2 Commit and Prepare, and the harness
aborted again before GC; P-01 (the Prepare before the first binding) still passes, so the declared detector was a wrong prediction. The
mutation therefore did not pass unnoticed: both runs failed with specific counted failures that show exactly its effect, but neither reached a
declared detector. The detectors were not redefined after the fact. Recorded as O-9.

**Build isolation proof.** Each mutation's result records which `AuthorizationAuthority.*` assemblies differ from the clean build of the
scratch copy. The mutated project's assembly always differs. For the three mutations that follow one in a *different* project, the earlier
project's assembly equals the clean build: MG-09 and MG-16 (Infrastructure) have the clean `Application.dll`, MG-13 (Api) has the clean
`Application.dll` and `Infrastructure.dll`; so no earlier mutation was carried in. After all mutations a full rebuild reproduced the clean
`Application`, `Contracts`, `Domain` and `Infrastructure` assemblies byte for byte; `AuthorizationAuthority.Api.dll` differs between two clean
builds of identical source (it differs in every mutation run for the same reason), so the driver's final all-assemblies equality reports
`False` (O-8). Evidence: `docs/evidence/e3-m2f2g/mutations/` (per mutation `m2e2-evidence.json`, `summary.json`, `merge.txt`;
`mutation-results.json`, `mutation-results-MG-17b.json`, `clean-build.txt`, `clean-build-MG-17b.txt`, `provenance.txt`).

**Discarded attempts.** Attempt 1 built incrementally and was stopped after MG-09 showed a stale MG-08 build (O-7); its two disposable
containers were verified as the gate's own and removed. Attempt 2 (fixed driver) stopped at MG-13, whose first form (`ContainsKey`) is
rejected by analyzer rule CA1854 (warnings are errors); MG-13 was rewritten without `ContainsKey` and the driver now records a build failure
instead of stopping. Attempt 3 (all 19, fresh scratch copy) is the evidence above. MG-01..MG-12 gave the same detections in all attempts in
which they ran.

## 12. Runs of record (requirement §11)

Each run used freshly created containers, databases, Vault tokens and builds (no cached build).

| | Record 1 | Record 2 |
|---|---|---|
| E3-M2e-2 regression: passed / failed | 138 / 0 | 138 / 0 |
| M2f-2G counted: passed / failed | 71 / 0 | 71 / 0 |
| M2f-2G by kind (passed / total) | Security 43/43, Premise 12/12, Control 9/9, Setup 7/7 | same |
| Inconclusive (not counted) | 2 (GR-03, GH-07r) | 2 |
| Expected gaps (not counted) | M2f-2G: EXPECTED_GAP_G2 7, EXPECTED_GAP_INVENTORY 1, EXPECTED_GAP_RESTORE 1, EXPECTED_GAP_BD2 1; E3-M2e-2: EXPECTED_LIMIT 4, EXPECTED_GAP_BD1 1 | same |
| Expected permission refusals (42501, labelled `m2e2-expected-42501`) | 7 (6 from E3-M2e-2, 1 from G-63) | 7 |
| Unexpected permission errors | 0 | 0 |
| Deadlocks (server log / `pg_stat_database`) | 0 / 0 | 0 / 0 |
| Lock timeouts | 0 | 0 |
| Lock waits logged (> `deadlock_timeout`) | 0 | 0 |
| Containers removed; container list unchanged | yes; yes | yes; yes |
| Configuration enablers before / after | 0 / 0 | 0 / 0 |
| Evidence leaks after scrub | 0 | 0 |
| Harness duration | about 6 minutes (plus builds) | about 6 minutes |

The first run identified no security defect, so the second was run as specified.

**Executed versus final files.** The harness and both agent sources executed in each run of record (the copies in the runs' work
directories) are content-identical to the files of §2, apart from line endings (normalized to LF afterwards, as `.gitattributes` requires).
The gate script used by both runs of record differs from the final one only by the two lines that canonicalize the work and evidence paths
before the scrub (§14); the enforcement-removal runs (§11) used the final script.

## 13. PostgreSQL diagnostics

`postgres-diagnostics.txt` (each run): PostgreSQL 18.0, `default_transaction_isolation = read committed`, `deadlock_timeout = 1000`,
`lock_timeout = 0`, `log_lock_waits = on`; per-database commits, rollbacks, deadlocks (0 in every database), conflicts (0); at the end no
harness session holds or awaits a lock and none is idle in a transaction. `postgres-server.log` (each run) is classified by the gate (deadlocks,
lock timeouts, expected and unexpected permission errors). Existence-query plans are logged as `DIAGNOSTIC` lines in `m2e2-log.txt`. Lock and
wait evidence of the concurrency checks is in the observed text of GC-01..GC-05.

## 14. Cleanup and evidence sanitization

| Item | Result |
|---|---|
| Containers | Both containers of every run removed in `finally`; the container list after equals the list before (both runs of record and all 20 runs of mutation attempt 3). The stopped mutation attempt 1 left its two disposable containers running; they were identified (gate names, `--rm`, tmpfs, gate images, created by that run) and removed; no harness, agent or host process remained |
| Temporary credentials | Random per run; passed only through environment variables removed in `finally`; never written to the evidence |
| Agent inputs | Contain test-only workload and user-assertion keys and the run's seal key: kept in the work directory, never copied to the evidence |
| Scrub | The gate replaces passwords, tokens, the work directory, the repository path and the user profile in every evidence file. In both runs of record the work directory reached the gate with `/` separators, so the host logs' `Content root path` line kept the scratch directory after the profile prefix: the copied evidence was post-scrubbed (4 host logs per run) and the gate now canonicalizes both paths before scrubbing |
| Final check | No password, token, key, user name, profile path or scratch path in `docs/evidence/e3-m2f2g/` |

## 15. Observations for owner review

| Id | Observation |
|---|---|
| **O-1** | **The harm of G-2 at Prepare and Commit is narrower than closure §6.3 states.** A 11e15ee Prepare that re-stages every carried row is refused by candidate validation (S-4, `ENVELOPE_SCHEMA_MISMATCH`, 422) before snapshot binding, so no incident follows (G-20a). The journaled `CurrentSnapshotMismatch` occurs for a request whose candidate is valid while its observed head is re-encoded, for example a removal of the encrypted values (G-20, GG-01). At Commit, re-encoded staged bytes are rejected by staged verification (`CANDIDATE_MISMATCH`, 409) with no incident and no abort (G-21, GG-02). The false quarantine of §6.3 is confirmed for Verify, batch Verify, census, backfill and deep verify. The capability check precedes all of these. Suggest a dated note in closure §6.3 (not edited here) |
| **O-2** | **A declaration padded with HTTP optional whitespace (`" 2 "`) is admitted.** Kestrel removes optional whitespace before the Api reads the header (RFC 9110 §5.5), so the parser receives `2`. The E3-M2f-2 record (§5) noted this as untested. The header is self-asserted (CAP-S7); this admits no build that could not send `2` anyway. Owner decision: accept, or require a raw-header check |
| O-3 | CAP-F5 refusals are `AUTHORITY_NOT_READY` 503 with `retryable = true` (G-63): fail closed, but a business build may retry while the ledger is unreadable. Consistent with the E3-M2f-2 implementation; recorded |
| O-4 | The existence query's plan on the small disposable ledger is a sequential scan; the index is usable (G-61). The plan at production scale and on Azure PostgreSQL is not verified (R-CARV-18, PB-09) |
| O-5 | The composed readiness text (INV-4) could not be observed (GH-07r); only the startup log line is evidence |
| O-6 | Multiple header values sent by `HttpClient` are joined into one field line (`1, 2`); two separate field lines were not sent on the wire. The parser's handling of two instances is covered in-process by the E3-M2f-2 host tests |
| **O-7** | **Mutation-driver build isolation (test tooling, not product).** The first E3-M2f-2G mutation attempt restored each file with `Copy-Item` (which keeps the backup's older timestamp) and built incrementally, so a later mutation in a *different* project could be built on top of the previous mutation's stale assembly: MG-09's build still contained MG-08 (its G-12 trace showed verification before the existence query). That attempt was stopped and discarded; its two orphaned disposable containers were verified as the gate's own and removed. The driver now builds with `--no-incremental`, touches every restored file and records which Authority assemblies differ from a clean build for each mutation (§11). **The committed E3-M2e-2 driver (`scripts/dev/e3-m2e2-mutations.ps1`) has the same pattern**: by the same analysis its MX-10 (after MX-09) and MX-12, MX-13 (after MX-11) cross from the Application to the Infrastructure project and may have run with the preceding mutation still compiled in. Their reported detections are plausible but not proven clean. Not changed here; owner decision whether to re-run them |
| **O-8** | **`AuthorizationAuthority.Api.dll` is not byte-reproducible**: two clean Release builds of identical source differ, while the Application, Contracts, Domain and Infrastructure assemblies are identical. The cause was not investigated (the Api project generates its OpenAPI document on build). Relevant to PB-11 reproducible builds and digest promotion; not to the capability floor |
| **O-9** | **MG-17 reached no declared detector** (§11): the mutation breaks the current build's own level-2 path, which aborts the harness during setup instead of failing recorded checks. Harness limitation: setup failures in groups G and P throw. Owner decision: accept MG-17/MG-17b as detected by the counted failures they did produce, or require a follow-up that records setup failures as checks and re-runs MG-17 |

## 16. Specification manifest

`python scripts/generate-spec-manifest.py` → 259 files (257 at E3-M2f-2; added: this record and `scripts/dev/e3-m2f2g-mutations.ps1`;
changed digest: `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1`). `python scripts/validate-specifications.py --strict` → `STRICT VALIDATION
PASS` (tracked 259; approved ADRs 14; owned pages 75). `docs/evidence/**` and `tools/**` are outside the manifest by design. The manifest was
regenerated after the final edit of this record, again after the follow-up of §20 (still 259 files; new digests for this record and
`scripts/dev/e3-m2f2g-mutations.ps1`; group GS and the evidence lie outside the manifest), with strict validation passing.

## 17. Known limitations

- SpiceDB is not part of the disposable environment: in-process user Verify uses a double that reports the expected projection marker and allows every check; through the host, user Verify stops as not ready after the capability check. SpiceDB is not under test (the capability check precedes every SpiceDB call).
- The historical builds' Worker host processes were not run; their clients were (§9). The agents encode stored row values handed over by the harness instead of reading BusinessDb.
- Organizations, keys and identity bindings are seeded with an ordinary admin session (`session_replication_role = replica`), as in E3-M2e-2; census decisions, snapshots and search approvals are written through the real runtime store.
- In-process Authority instances use a deterministic test MAC; the host uses Vault Transit. Neither is the production Managed HSM.
- Evidence is from Windows with Docker Desktop and local PostgreSQL 18.0; not Azure PostgreSQL, not staging.
- Refusal evidence remains non-durable (logs and metric only) until E3-M2f-3 (R-M2f-9).

## 18. Remaining risks and production gates

Unchanged by this increment: BD-1 and BD-2 (now demonstrated: GR-01, GR-06), R-M2f-1..R-M2f-10, F-E2, R-A1-1..R-A1-5, TS-M2f-01,
OPEN-M2f-1..3, E3-M2f-3 refusal evidence (RE-1..RE-6), INV-1/INV-2 deployment inventory (INV-3 demonstrated: GG-INV), OA-FE1-2, RB-CARV-1
production closure, V2-IND-01, PB-08, PB-11, PB-12, Azure PostgreSQL verification and staging. Persistent Envelope v2 acceptance remains
prohibited (closure §11.5).

**Recommendation.** One separate signed local commit containing only the files of §2 (harness, agents, scripts, this record, sanitized
evidence, regenerated manifest). After the follow-up (§20) no verification item of E3-M2f-2G remains open; FU-M2E2-MUT-01 belongs to
E3-M2e-2 and does not block this commit. Not pushed or merged. Authorized by OA-M2f2G-13 (§20.10).

## 19. Not claimed

- No push, merge, deployment, Azure call, Stage B run or persistent database change. The only commit is the single signed local commit authorized by OA-M2f2G-13.
- No Envelope v2 acceptance outside the disposable environment; no persistent activation.
- No closure of BD-1, BD-2, F-E2, R-CARV-21, E3-M2f-3 or any production gate.
- No independent review, certification or production-readiness claim.

## 20. Owner review follow-up (2026-11-17)

### 20.1 Owner decisions

Owner-led review of §1–§19 (not an independent review). The decision texts below are recorded from the owner's follow-up instruction of
2026-11-17.

| Id | Decision |
|---|---|
| **OA-M2f2G-01** | The reported disposable PostgreSQL verification results (§6–§10, §12) are **conditionally accepted** for Development/Testing |
| **OA-M2f2G-02** | The E3-M2f-2G signed local commit is **not yet authorized** |
| **OA-M2f2G-03** | **MG-17 is the sole milestone-blocking verification item.** Its attempt-3 result (§11) is treated as not detected by a declared detector |
| **OA-M2f2G-04** | MG-17 correction scope: only the test harness and the mutation driver may change, to separate setup failures from security assertions and to make a designated detector execute against the mutated build; the detector may not be weakened and an unrelated setup failure may not be counted as a detection. If the detector cannot be exercised, MG-17 is INCONCLUSIVE and work stops |
| **OA-M2f2G-05** | The E3-M2e-2 mutation-driver contamination concern (O-7: MX-10, MX-12, MX-13) is documented as a separate follow-up item; the E3-M2e-2 record is not changed and its driver is not re-run or remediated under this authorization |
| **OA-M2f2G-06** | Documentation corrections: the narrower demonstrated Prepare and Commit G-2 harm with the seven-entry-point requirement preserved (O-1), HTTP whitespace normalization (O-2), mutation contamination and the excluded attempt (O-7), PB-11 build reproducibility (O-8), MG-17's original status and the targeted outcome; the two runs of record, their classifications and BD-1/BD-2 as expected security gaps are preserved |
| **OA-M2f2G-07** | Regression scope: the targeted MG-17 verification and the relevant baseline checks; a complete M2f-2G rerun only if the correction changes shared harness behaviour or isolation cannot be established, and then only after stopping to explain |

### 20.2 Root cause of the MG-17 setup failure

MG-17 removes the level-2 short-circuit (`level >= max(D, 2)` admits without the ledger), so a level-2 request also runs the existence query
and is refused in any organization with a binding. Group G builds its fixtures with the **current build's level-2 writes**: each record's
Prepare is admitted while the organization is still unbound and inserts the first binding, and the following level-2 Commit is then refused
(`CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, 8 of 16 writes and the pending Prepare). The write outcomes were recorded only later in G-00, so
the next fixture step (`SeedCensusAsync`) took `.First()` of the empty encrypted-item list and the harness aborted (`Sequence contains no
elements`) before any declared detector (G-10c, G-12c, G-42) ran. Group G's setup depends on the very property MG-17 removes; MG-17b (groups P
and GC) hit the same dependency in the E3-M2e-2 positive matrix.

### 20.3 Correction (test harness and mutation driver only)

| Change | Purpose |
|---|---|
| `ScenariosCapability.cs`: right after the fixture writes, a failed write or pending Prepare is recorded as **G-00 (Setup, failed)** and group G stops | A setup failure is a Setup check, never a security result, and no longer aborts the harness. Not reachable on a correct build (§20.6) |
| New `ScenariosCapabilityShortCircuit.cs`, group **GS** | The designated MG-17 detector. Self-contained fixtures that need no level-2 Commit in a bound organization: a v1 head written while the organization is unbound (anchored); then a level-2 Prepare of a New v2 group left Prepared, whose binding makes the organization bound (CAP-F2). A failed setup is recorded as GS-00 / GS-SKIPPED and stops the group |
| GS-03 (Control) | The organization is bound and the level-1 path does run the existence query: level-1 Verify is refused after exactly one `SELECT EXISTS` |
| **GS-01** (Security; declared detector) | Level-2 Verify and level-2 batch Verify in the bound organization execute **zero** statements on `authority.content_data_key_bindings` (statement trace) and verify |
| **GS-02** (Security; declared detector) | Server-enforced: with SELECT on the ledger revoked from the runtime role (restored at once; the 42501 labelled expected), level-2 Verify and batch Verify still verify, so they read nothing from the ledger; level-1 Verify on the same node is refused as not ready (the revocation is in force on that path) |
| `Program.cs` | Runs group GS (also part of `all`) |
| `scripts/dev/e3-m2f2g-mutations.ps1` | MG-17c (same mutation; groups G, GS; declared detectors GS-01, GS-02, declared before the run). Classification: **DETECTED** only if a declared detector executed and failed; **INCONCLUSIVE** if none executed; **SURVIVED** if they executed and passed; setup failures and assertion failures listed separately. The gate's `new-sha256.txt` must equal the mutant build's assemblies. `-VerifyRestored`: after the restore, a full rebuild and a re-run of the same groups, where the declared detectors must pass and the reproducible assemblies must equal the clean build. Summary reported by classification |

No product source, migration, CARV, floor, ledger, acceptance default or enforcement code changed (`git status` and `git diff` of `src/`,
`contracts/`, `infrastructure/` and `.github/`: no change).

### 20.4 Targeted results

Fresh `git archive` scratch copy of `af320fc`; every build `--no-incremental`; every run in fresh disposable PostgreSQL and Vault containers.

| Run | Classification | Declared detectors executed | Failing checks | Tested assemblies | Restored baseline (same groups) |
|---|---|---|---|---|---|
| MG-17 (groups G; detectors G-10c, G-12c, G-42) | **INCONCLUSIVE** | none | G-00 (Setup) only: "writes commit: CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT … (8 of 16 failed); pending … CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT"; no harness abort | = mutant build | G-10c, G-12c, G-42 pass; no failing check |
| **MG-17c** (groups G, GS; detectors GS-01, GS-02) | **DETECTED** | GS-01, GS-02 | Assertion failures exactly GS-01 and GS-02; Setup failure G-00 (group G, as above) | = mutant build | GS-01, GS-02 pass; no failing check |

**GS-01 under MG-17c (failed):** "Verify: CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT 409; ledger statements 1 [`SELECT EXISTS ( SELECT 1 FROM
authority.content_data_key_bindings AS c WHERE c.organization_id = @organizationId) [params 1]`]; shape [tx-begin > org-lock > ledger-exists
> tx-rollback]", and the same for BatchVerify. **GS-02 under MG-17c (failed):** "level-2 Verify AUTHORITY_NOT_READY 503; level-2
BatchVerify AUTHORITY_NOT_READY 503; level 1 AUTHORITY_NOT_READY 503". **Restored baseline:** GS-01 "Verify: OK: verified True; ledger
statements 0", likewise BatchVerify; GS-02 "level-2 Verify OK: verified True; level-2 BatchVerify OK: verified True; level 1
AUTHORITY_NOT_READY 503". GS-00 and GS-03 passed in all four runs: the detector's setup does not depend on the mutated property.

**Source-state proof.** `AuthorizationAuthority.Application.dll` (the assembly MG-17 changes): clean build `63c0ac441155c501…`, mutant
`6ed78fbf18439099…`, restored `63c0ac441155c501…` (equal to clean). In each run the gate's own `new-sha256.txt` equals the build under test
(mutant, then restored). After the restore the Application, Contracts, Domain and Infrastructure assemblies equal the clean build byte for
byte; `AuthorizationAuthority.Api.dll` differs between any two builds (O-8), so the driver's all-assemblies final equality is `False` for that
assembly only. Scratch file restored and sha256-verified; repository file unchanged. Evidence: `docs/evidence/e3-m2f2g/followup-mg17/`
(per run: `mutant/` and `restored/` gate evidence and `new-sha256.txt`, `result.json`; `mutation-results.json`, `clean-build.txt`,
`provenance.txt`; the driver's final summary line of that run still used the earlier detected/survived wording and was corrected afterwards;
the classifications in `mutation-results.json` are authoritative).

### 20.5 Updated mutation count and classifications

| Mutations | Classification |
|---|---|
| MG-01..MG-16, MG-18, MG-19 (attempt 3, §11) | DETECTED by declared detectors (18) |
| MG-17 (attempt 3, §11) and MG-17b | INCONCLUSIVE (setup dependency; no declared detector executed). Preserved as reported; not re-labelled |
| MG-17 (follow-up rerun) | INCONCLUSIVE (setup failure now recorded as G-00; no abort) |
| **MG-17c** (follow-up) | **DETECTED** by GS-01 and GS-02; both pass on the restored baseline |

**Every one of the 19 enforcement-removal classes has a PostgreSQL-backed detection by a declared detector**: 18 in attempt 3 and the
level-2 short-circuit class through MG-17c. The attempt-1 and attempt-2 runs remain excluded (§11, O-7).

### 20.6 Regression

| Run | Result |
|---|---|
| Baseline (this tree, unmutated), groups G and GS, fresh containers | 73 counted, 0 failed (setup and acceptance 20/20; M2f-2G 53/53: group G 48, as in both runs of record, and GS 5); expected-gap demonstrations 8 (EXPECTED_GAP_G2 7, EXPECTED_GAP_INVENTORY 1), as before; expected permission refusals 5 (labelled), unexpected 0; deadlocks 0; lock timeouts 0; containers removed; evidence `docs/evidence/e3-m2f2g/followup-baseline/` |
| Restored-baseline runs | Above (§20.4) |
| Complete M2f-2G gate | **Not re-run** (OA-M2f2G-07). The correction changes no shared harness helper and no existing check: group G's new branch runs only when its own setup fails (never on a correct build, shown by the baseline run), and group GS is new and self-contained. Isolation is established by the targeted evidence (§20.4). The two runs of record (§12) stand as recorded; they did not include group GS |

### 20.7 Disposable resources and cleanup

Every follow-up run created and removed its own `postgres:18.0` and `hashicorp/vault:1.20.4` containers; the container list after each
run equals the list before; no harness, agent or host process remained; no persistent database, Vault, SpiceDB, Stage B stack or Azure
endpoint was contacted. Evidence is scrubbed (no password, token, key, user name, profile or scratch path).

### 20.8 Follow-up item for separate owner authorization

| Id | Item | State |
|---|---|---|
| **FU-M2E2-MUT-01** | The committed E3-M2e-2 mutation driver (`scripts/dev/e3-m2e2-mutations.ps1`) restores files with `Copy-Item` (older timestamp) and builds incrementally. MX-10 (after MX-09) and MX-12, MX-13 (after MX-11) cross from the Application to the Infrastructure project and may have been built with the preceding mutation still compiled in. Their recorded detections in `E3_M2E2_VERIFICATION_RECORD.md` and `docs/evidence/e3-m2e2/mutations/` are plausible but not proven clean. Proposed remedy: `--no-incremental` builds, touching restored files, and per-run assembly hashes, then a re-run of MX-10, MX-12 and MX-13 | **OPEN.** Not remediated or re-run; the E3-M2e-2 record and evidence are unchanged. Needs separate owner authorization |

### 20.9 Documentation corrections made

- §7: a statement of the demonstrated harm per entry point (narrower at Prepare and Commit, O-1), with the seven-entry-point enforcement requirement explicitly preserved.
- §11: MG-17's attempt-3 classification stated as INCONCLUSIVE and linked here; the attempt-3 table and totals are unchanged.
- §15 (O-1, O-2, O-7, O-8, O-9) unchanged in substance; O-9 resolved here.
- §2, §18 and this section updated. §6, §8, §9, §10 and §12 (the runs of record, their classifications, BD-1 and BD-2 as expected security gaps) are unchanged.

### 20.10 Final owner acceptance (2026-11-17)

Owner-led acceptance (not an independent review). The decision texts below are recorded from the owner's acceptance instruction of
2026-11-17. They accept E3-M2f-2G for disposable Development/Testing verification only; they do not authorize persistent Envelope v2
acceptance, deployment, a production-readiness claim or any subsequent milestone.

| Id | Decision |
|---|---|
| **OA-M2f2G-08** | The root cause of the MG-17 setup failure (§20.2) is accepted |
| **OA-M2f2G-09** | The independent detection of the level-2 short-circuit removal by GS-01 and GS-02 (§20.3, §20.4) is accepted |
| **OA-M2f2G-10** | MG-17c is classified **DETECTED**; the original MG-17 (attempt 3 and the follow-up rerun) and MG-17b remain **INCONCLUSIVE** |
| **OA-M2f2G-11** | The targeted regression (§20.6: groups G and GS, 73 counted, 0 failed, and the restored-baseline runs) is accepted without a complete-gate rerun |
| **OA-M2f2G-12** | FU-M2E2-MUT-01 (§20.8) remains **OPEN**, separate, and non-blocking for this commit |
| **OA-M2f2G-13** | Exactly one signed local commit of E3-M2f-2G is authorized. Not a push, merge or deployment |

**Mutation evidence as accepted.** All 19 enforcement-removal mutation classes now have PostgreSQL-backed detection evidence by declared
detectors: MG-01..MG-16, MG-18 and MG-19 in attempt 3 (§11), and the level-2 short-circuit class through MG-17c (§20.4). The original MG-17
attempts (attempt 3 and the follow-up rerun) and MG-17b remain INCONCLUSIVE as executed and are not re-labelled as detections or passes.

**Preserved as executed.** The two runs of record (§12: 209 counted checks, 0 failed, each; E3-M2e-2 regression 138/138; M2f-2G 71/71) and
their expected-gap and INCONCLUSIVE classifications; BD-1 and BD-2 as expected security gaps (GR-01, GR-06); observations O-1, O-2, O-7 and
O-8 (§15); the limitations of §17 (SpiceDB double, historical Worker hosts not run, test MAC and Vault Transit instead of Managed HSM, no Azure
PostgreSQL or staging); and the remaining gates of §18. None of these is closed by this acceptance.
