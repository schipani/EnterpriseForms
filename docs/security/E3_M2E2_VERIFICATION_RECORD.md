# E3-M2e-2 — Controlled Envelope v2 Acceptance Verification: Record

**Status (2026-11-16).** Executed in **Development/Testing only**, in disposable PostgreSQL 18.0 and Vault 1.20.4 containers created and
removed by each run. **Test-only:** no production source, migration, deployed configuration, BusinessDb or vector file was changed.
Owner-led work under the owner authorization of 2026-11-16 (readiness decisions D-1..D-12); **not** an independent review, **not** a
certification, **not** production readiness. **Owner-accepted for disposable Development/Testing only** (OA-M2e2-01..06, §14); one
signed local commit of the test harness, scripts, this record, the evidence and the regenerated manifest is authorized (§14).

| Item | State |
|---|---|
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `4a71dbc6a436ec812ae0db059bd1acc4860cad6a` (E3-M2e-1, good SSH ED25519 signature), clean tree at start |
| Envelope v2 acceptance | Enabled **only** on switch objects constructed inside disposable harness processes, and in two disposable Authority host processes (§4). Never in a settings or deployment file; never against a persistent database |
| Runs of record | `record-1` and `record-2`: **138 counted checks each, 0 failed**; 5 expected-gap/limit demonstrations each, outside the totals (§5) |
| Enforcement removal | 17 mutations (13 source, 4 database): **all detected**, all restored and verified (§7) |
| New production defects | **None found** (§10). One deviation from an approved expectation (D-9) and several harness corrections are recorded |
| Carried | F-E2 (open, not remediated), BD-1, BD-2 (open, not implemented), R-M2e-*, R-A1-*, V2-IND-01 and every production/Azure gate (§10) |

Governing documents: `E3_M2E_CONTRACT_CLOSURE.md` (§6.2 is the scope), `E3_M2B_IMPLEMENTATION_CONTRACT.md` §17.3 (M2-ENV-16/18,
M2-DK-11/12, M2-RST-06), `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` §11 (test catalogue), `E3_M2E1_IMPLEMENTATION_RECORD.md`,
`F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md`.

---

## 1. Baseline verification (owner authorization §1)

| Requirement | Observed |
|---|---|
| Required HEAD | `4a71dbc6a436ec812ae0db059bd1acc4860cad6a` on `e3/m1-envelope-v2-core` |
| Signature | `Good "git" signature … ED25519 key SHA256:t6bvk1LK…` (`%G?` = `G`) |
| Clean tree | 0 porcelain lines before any work |
| CARV 3 | `ContentAcceptanceRules.CurrentVersion = 3`, `RecognizedFloors = [1, 2, 3]` |
| Ledger and floor 3 | Ordinary migration `20261116031706_AuthorityContentDataKeyBindings`; floor migrations 1, 2 and `20261116040000_AuthorityContentAcceptanceRuleFloor3` |
| Default off | `EnvelopeV2AcceptanceSwitch.Parse` admits only the literal `true`; no repository configuration file mentions `EnvelopeV2AcceptanceEnabled` (checked by the gate before and after every run: 0 and 0) |
| E3-M2e-1 closed | `E3_M2E1_IMPLEMENTATION_RECORD.md` §14 (OA-FE1-1..3) |
| F-E2 open | `F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md`: OPEN, not fixed |

## 2. Files created (test-only)

| File | Purpose |
|---|---|
| `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1` | The gate: builds, merged binaries, disposable containers, nonce/system-identifier recording, harness run, guaranteed teardown, server-log classification, evidence scrubbing |
| `scripts/dev/e3-m2e2-mutations.ps1` | Enforcement-removal driver (scratch `git archive` copy; database mutations via the harness) |
| `tools/experiments/e3-m2e2-acceptance-gate/README.md` | How to run; isolation summary |
| `tools/experiments/e3-m2e2-acceptance-gate/HarnessE2/*` | `HarnessE2.csproj`, `Program.cs` (core, drivers), `Isolation.cs` (target identity guard), `M1Core.cs` (adapter to the real M1 core), `Rows.cs` (fixtures, business encoder), `Support.cs`, and the scenario files `ScenariosSetup/LedgerSql/Positive/Negative/Concurrency/Database/Recovery/Host.cs` |
| `docs/evidence/e3-m2e2/record-1/`, `record-2/` | Runs of record (scrubbed) |
| `docs/evidence/e3-m2e2/mutations/` | Mutation results of record |
| this record | — |

The E3-M2e-1 `LegacyAgent` (`tools/experiments/e3-m2e1-ledger-gate/LegacyAgent`) is reused unchanged for the frozen builds.

**Fidelity notes (decided during implementation, for owner review):**

1. **The M1 core is `internal`** (only `EnterpriseForms.Api.ContractTests` reaches it through `InternalsVisibleTo`). The harness neither
   changes production visibility nor impersonates that assembly name: `M1Core.cs` invokes the real internal API **by reflection** and
   pins the **public, provider-seeded overloads** (`FieldSealBatchV2.CreateAsync(keys, …)`, `Seal(field, item, logicalType, plaintext)`,
   `DocumentEnvelopeV2Cipher.EncryptAsync(…, keys, …)`), never the internal deterministic known-answer cores. Members exposed only as
   `ReadOnlySpan<byte>` are read from their private `byte[]` backing fields with length checks (fail loud on any shape change).
2. **Merged binaries.** The harness loads this tree's Authority and business assemblies in one process. First-party assemblies must have
   identical versions (enforced). Four third-party assemblies differ (`Microsoft.IdentityModel.*` 8.19.2 in the Authority, 8.23.0 in
   the business output); the Authority's copies are kept and recorded in `merge.txt`. No code path the harness exercises loads them
   from business code; a path that did would fail at load time.
3. **Rows.** Every Envelope v2 row comes from the real M1 core with the real key provider (wrap code 1: `LocalDevelopmentKeyProvider`
   with a per-run random KEK; wrap code 2: `VaultTransitKeyProvider` against the run's Vault, organizations provisioned by the
   repository's Vault scripts) and is encoded by `BusinessSignedSchemas.Encode`. Only the record header uses the M2e-1 harness schema,
   and v1 envelopes keep the M2e-1 synthetic v1 shape (the Authority validates v1 shape, not v1 bytes). The S-4/S-9 negatives use raw
   encodings the business registry refuses to produce.
4. **Authority in-process; host checks out of process.** Scenarios compose `ContentService` and the EF `AuthorityStore` in-process
   (the M2e-1 precedent) with a counting test-only MAC; the host checks start the real `AuthorizationAuthority.Api.dll` (§4).

## 3. Isolation and activation evidence (owner authorization §3, §4)

| Safeguard | Evidence (both runs of record) |
|---|---|
| Fresh containers, randomized names, ports and credentials | `m2e2-gate-<nonce>` and `m2e2-vault-<nonce>`, free loopback ports, random passwords/tokens; PostgreSQL `--rm` on tmpfs; both bound to `127.0.0.1` |
| Target identity | S-00: `m2e2gate` and `postgres` admitted only after nonce comment + `pg_control_system().system_identifier` (recorded by the script from inside the container) + port 5432 matched; every copy (`m2e2_floor2`, `m2e2_t0`, `m2e2host`, `m2e2noledger`) admitted the same way; S-10 and T-02 re-verify all |
| Connection-string guard | `Target.Require` on every connection (harness, child commands, legacy agents, host): `127.0.0.1`, the run's port, an admitted database. A violation is an immediate stop (`STOP` check); none occurred |
| Environment scrub | S-01: no inherited `ConnectionStrings__*`, `Content__*`, `ContentAcceptance__*`, Azure, ASP.NET Core environment settings remained (none were present) |
| No persistent database, no Azure | Only admitted databases were reachable; in-memory archive; no Azure SDK credential; SpiceDB endpoint for host checks = a closed loopback port |
| Ordinary `--migrate` not with writers (F-E2) | S-02: 0 runtime sessions when it ran; F-08 ran it again with no runtime writer; runtime-grant sentinel `G-*` unchanged at 9 points; **0 unexpected `permission denied`** in the server log (6 expected, labelled `app=m2e2-expected-42501`) |
| Teardown | Both containers removed in `finally`; container list after = before (`ContainerListUnchanged = true`); no `m2e2*` container exists |
| Evidence scrubbing | Passwords, tokens, work directory, repository path and user profile removed (case-insensitive); post-scrub leak scan = 0 |

**Controlled activation sequence (each run):** ordinary migration (S-02) → floors 1 → 2 → 3 (F-01..F-14) → Release B `RequiredFloor = 3`
guard ready (S-11) → refusals of the switch outside Development/Testing and without Release B RF3 (S-12) → `Create("true", "Testing",
ReleaseB/3)`: configured but **not effective** (S-13), and a New group refused while the policy is unrecorded (S-14) → policy recorded on
the disposable platform journal with the in-memory archive, then effective (S-15) → scenarios → Testing override off at the end (T-01);
instances are discarded with the process.

**Host-start checks (D-5).** `m2e2host` (migrated, floors 1..3) and `m2e2noledger` (migrated only to the CARV migration). The host runs from
a copy of the Release output **without `appsettings.Development.json`** (working directory = that copy), with every setting explicit:

| Check | Observed |
|---|---|
| H-01 Development, switch on, Release B RF3, in-memory archive | starts, guard passes (`configured=True`), records the policy (acceptance requested, `phase=ReleaseB requiredFloor=3`); stopped |
| H-02 Testing, same settings | runs; the in-memory archive is not permitted in Testing, so the policy is never recorded: acceptance configured but **not effective** (fail closed) |
| H-03/H-04 Staging, Production | refuse to start ("must not be enabled outside Development/Testing") |
| H-05 Development, RF2 | refuses to start (Release B RF3 required) |
| H-06 ledger migration missing | refuses to start ("data-key ledger migration is not recorded"); this check's host used the container superuser on its own disposable database |

## 4. Positive and negative totals (runs of record)

| Kind | Checks | record-1 | record-2 |
|---|---|---|---|
| Setup (identity, scrub, migration, activation, grant sentinels, teardown) | 20 | 20 / 0 | 20 / 0 |
| Floor (F-01..F-14, incl. C-14) | 14 | 14 / 0 | 14 / 0 |
| Positive (P-01..P-22) | 22 | 22 / 0 | 22 / 0 |
| Negative (S-14, N-00..N-43, R-06) | 43 | 43 / 0 | 43 / 0 |
| Concurrency (C-01..C-06) | 6 | 6 / 0 | 6 / 0 |
| CrossBuild (X-01..X-05) | 5 | 5 / 0 | 5 / 0 |
| Database (L-01..L-05, D-01..D-14) | 19 | 19 / 0 | 19 / 0 |
| Recovery (R-00, R-01) | 2 | 2 / 0 | 2 / 0 |
| Host (H-00..H-06) | 7 | 7 / 0 | 7 / 0 |
| **Counted total** | **138** | **138 passed / 0 failed** | **138 passed / 0 failed** |
| Expected-gap/limit demonstrations (not counted) | 5 | 5, all as expected | 5, all as expected |

### 4.1 Positive matrix (owner authorization §5)

| Requirement | Checks | Result (both runs) |
|---|---|---|
| New v2 field batch, wrap code 1 | P-01 (Prepare: one binding, kind 1, M1 commitment, count 3, P = 3, signed 5) / P-02 (Commit: C = 3, head +1, ledger unchanged, DK-8 tuple = ledger row) | pass |
| New v2 document, wrap code 1 | P-03 / P-04 (kind 2, count 1) | pass |
| New v2 field batch, wrap code 2 (Vault Transit) | P-05 / P-06 | pass |
| New v2 document, wrap code 2 (Vault Transit; 3 MiB + 17 bytes) | P-07 / P-08 | pass |
| DK-4/DK-5 Prepare registration | P-01, P-03, P-05, P-07, P-09 (one binding per New group, in the Prepare transaction) | pass |
| DK-6 and P-4 at Commit | P-02..P-08 (DK-6), P-10..P-16 (P-4 for carried groups) | pass |
| Non-empty DK-8 tuples; values and ordering | P-02..P-09: every archived tuple equals its ledger row column for column; P-09: four groups in one commit, RFC 9562 byte order verified independently | pass |
| Digest-chain integrity | P-21/P-22: every content-stream body delivered by the production `ArchiveProcessor` to the in-memory archive is byte-identical, its digest equals the recorded one, and each entry chains to its predecessor (stored and in the envelope); archived tuples = ledger rows of committed operations | pass |
| M2-ENV-16 through Commit | P-10: a committed two-item batch carried byte-identically commits (P-4), no new binding, DK-8 `[]` | pass |
| Carried across later commits | P-11 (three further commits, one binding) | pass |
| Partial batch, removal | P-12 (1 of 2 carried; count stays 2), P-13 (all removed; binding kept) | pass |
| Re-seal of one item | P-14 (new group count 1; sibling carried under the old binding) | pass |
| Carried v2 with acceptance off | P-15 (Testing override off), P-16 (instance configured off): carry commits, New refused | pass |
| Historical v1 compatibility | P-17 (v1-only record, DK-8 `[]`, no binding), P-18 (v1 item updated while v2 carried) | pass |
| Idempotent replay | P-19 (Prepare replay: same operation, no binding/operation added), P-20 (Commit replay: same revision, no journal entry or binding) | pass |

### 4.2 Negative matrix (owner authorization §6)

Every Prepare refusal is checked for its reason **and** for "nothing signed, no operation, no binding, record unchanged"; every forged
Commit for "abort reason, one identifier-free incident, content-integrity revision +1, content head not advanced".

| Requirement | Checks | Observed reason |
|---|---|---|
| Reuse: committed | N-01 (M2-REP-01, own item id), N-01b (same envelope, new item: DK-4) | `ITEM_IDENTITY_REUSED` (item-identity rule precedes DK-4); `DATA_KEY_IDENTIFIER_REUSED` |
| Reuse: cross-record, cross-organization | N-02, N-03 | `DATA_KEY_IDENTIFIER_REUSED` |
| Reuse: Prepared, aborted, expired reservations (permanent) | N-04/N-05, N-08 (switch-off abort), N-10/N-11 (expired: `OPERATION_EXPIRED` then reuse refused) | `DATA_KEY_IDENTIFIER_REUSED` |
| Concurrent uniqueness | C-01 (same org: serialized, loser refused before signing), C-02 (cross-org: index backstop, 23505 → 422, L-3 rollback) | see §6 |
| DK-3 batch joining; mixed kinds; changed Content column | N-13; N-14; N-15 | `DATA_KEY_IDENTIFIER_REUSED`; `ENVELOPE_BATCH_INCONSISTENT`; `DATA_KEY_IDENTIFIER_REUSED` |
| Forged operations / binding mismatches | N-20 (no binding), N-21 (commitment), N-22/N-23 (count ±1), N-24 (kind), N-25 (foreign record), N-26 (uncommitted binding operation), N-27 (more rows than the binding), N-28 (substituted commitment in a carried row) | `DATA_KEY_BINDING_MISMATCH` 409; Aborted; incident +1 (reason only, no identifier); integrity +1; **head not advanced** |
| Commitment substitution between Prepare and Commit | N-29 | `CANDIDATE_MISMATCH` 409; operation stays Prepared; nothing accepted |
| Binding mutation and deletion | N-30, L-04, L-05 | runtime 42501 ×3 (no privilege); superuser 23514 `trg_content_data_key_binding_permanent` ×3; binding intact |
| Forged inserts (P-1), duplicate identifier | N-31, L-02, L-03 | 23514 guard; 23505 unique index |
| Stale revision | N-32; C-03 | `CONTENT_REVISION_CONFLICT` 409 before signing; head CAS abort |
| Switch off between Prepare and Commit (M2-ENV-18) | N-06 (override), N-07 (instance off) | `ENVELOPE_VERSION_NOT_ENABLED`; Aborted `…_AT_COMMIT`; no incident; reservation kept |
| Missing policy observation | S-14, N-33 | New refused until the exact policy is recorded; only the most recent record is trusted |
| Floor missing/lowered/weakened/duplicated/unrecorded, restore copy at floor 2 | D-08..D-13 | `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` 503 with the fence reason; nothing persisted; restored; writes resume |
| Ledger objects weakened (L-13) | D-01..D-07 | `CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED` 503 before signing; a Commit leaves the operation Prepared |
| CARV mismatch | F-14, X-01..X-05 | frozen builds refused by `ck_content_operation_rule_floor_v3`; CARV-3 operations committed by CARV-1/2 builds: `OPERATION_RULES_OUTDATED`, Aborted |
| S-4, S-9 | N-34/N-35, N-36/N-37 (Prepare); N-38/N-39 (forged, Commit) | `ENVELOPE_SCHEMA_MISMATCH`; `UNSUPPORTED_SIGNED_SCHEMA`; Aborted `ENVELOPE_SCHEMA_MISMATCH_AT_COMMIT` / `SIGNED_SCHEMA_INVALID_AT_COMMIT` |
| Envelope downgrade | F-05, N-40 | `ENVELOPE_DOWNGRADE` (the CARV-1 acceptance on floor 1, F-04, is the explicitly expected historical C-14 demonstration) |
| Share-edit refusal | N-41 (control: carry v2, update v1: OK), N-42 (v1 → New v2), N-43 (v2 re-seal) | N-42: `SHARE_ITEM_FRAMING_CHANGED` 403 (**D-9 deviation**, §10.2); N-43: `SHARE_ENVELOPE_NOT_ALLOWED` 403 |

## 5. Runs of record

Both runs: `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1`, full fresh builds (this tree's Authority and business API, `git archive` builds of
`a1127a1` and `8915f77`), fresh containers, all groups.

| Item | record-1 | record-2 |
|---|---|---|
| Positive | 22 / 0 | 22 / 0 |
| Negative | 43 / 0 | 43 / 0 |
| Concurrency | 6 / 0 | 6 / 0 |
| Floor | 14 / 0 | 14 / 0 |
| Database (incl. group L) | 19 / 0 | 19 / 0 |
| Mutation results | §7 (separate runs of record) | — |
| PostgreSQL deadlocks | 0 | 0 |
| Lock timeouts | 0 | 0 |
| Unexpected permission errors | 0 (6 expected, labelled) | 0 (6 expected, labelled) |
| Recovery reconstruction (M2-RST-06, R-01) | reconstructed 8 = committed ledger 8, column for column | same |
| BD-1 expected-gap demonstration (R-07) | `EXPECTED_GAP_BD1`, as expected | `EXPECTED_GAP_BD1`, as expected |
| Containers removed / list unchanged | true / true | true / true |
| Evidence complete; leaks after scrub | yes; 0 | yes; 0 |
| PostgreSQL | 18.0 (Debian 18.0-1.pgdg13+3) | same |

Expected-gap demonstrations are reported only in §8 and in each run's `gapDemonstrations` array; they never enter the counted totals.

## 6. Concurrency and floor evidence

| Check | Observed (both runs) |
|---|---|
| C-01 M2-CON-01 | two concurrent Prepares in one organization: 1 OK, 1 `DATA_KEY_IDENTIFIER_REUSED`; **3 rows signed** (the winner only: the loser was refused by DK-4 under the organization lock, before signing); 1 binding |
| C-02 M2-CON-02 | deterministic interleaving (EF interceptors): A inserted its binding and held its transaction; B passed DK-4 (A's row invisible), signed, reached its INSERT and blocked on the unique index; A committed; B refused 23505 → `422 DATA_KEY_IDENTIFIER_REUSED`; 6 rows signed (both); 1 binding (A's); B's organization has no new operation (L-3 rollback) |
| C-03 M2-CON-03 | two edits on one head: first OK; second `CONTENT_REVISION_CONFLICT` 409, Aborted; loser's reservation kept; re-staging its batch refused; a fresh batch commits |
| C-04/C-05 M2-CON-04 | fault injector `CrashAfterPrepareCommit`: durable Prepared operation with its binding; replay of the same Prepare returns it with no new binding and commits; a NEW operation with the same batch is refused; a fresh batch commits |
| C-06 stress | 4 organizations × 8 Release B floor-3 writes (New batches and carries interleaved): 32 OK; bindings per organization 4 = New groups; committed P = C |
| F-12 floor-3 raise under 4 writers of this (A-1) build | exit 0; exact definition; three floor history rows; 16 writes OK; 0 deadlocks in the server log |
| F-09..F-11, F-13 | floor 3 refused with a `carv-v2` session, an in-flight P = 2 operation, a weakened ledger; monotonic |
| F-04/F-07 (C-14 re-run, D-10) | CARV-1 build accepts the v2 → v1 owner downgrade on floor 1 (expected historical demonstration) and is refused on floor 2 |
| R-A1-1 | no A-1-incompatible frozen build ran concurrently with any floor raise (frozen builds ran only in F-04/F-07/F-14 and group X, sequentially) |

## 7. Enforcement-removal results (owner authorization §9)

Driver: `scripts/dev/e3-m2e2-mutations.ps1`. Source mutations are applied to a **scratch `git archive` copy of HEAD**, never to the
repository; the scratch file's sha256 is checked equal to the repository file before mutation, the scratch Authority is built (Release)
and the focused gate groups run against it in fresh containers; the scratch file is then restored from its backup and **both** the
scratch file's and the repository file's sha256 are verified equal to the originals. Database mutations weaken one control in the run's
disposable database after activation and restore it before teardown; the harness verifies the ledger/floor catalog digest and the runtime
grants (M-RESTORE). Results of record: `docs/evidence/e3-m2e2/mutations/`.

| ID | Control removed | Groups run | Detected by (designated checks that failed) | Restored and verified |
|---|---|---|---|---|
| MX-01 | DK-4 ledger lookup removed (PrepareViolation sees no binding) | N,C | **detected**: N-01b, N-02, N-03, N-05, N-08, N-11, C-01 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-02 | DK-5 binding insert removed | P | **detected**: P-01, P-02, P-03, P-05, P-07, P-09 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-03 | DK-6 tuple comparison removed | N | **detected**: N-21, N-22, N-23, N-24 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-04 | P-4 carried-group verification removed | N | **detected**: N-25, N-26, N-27, N-28 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-05 | Commit-time switch re-check removed | N | **detected**: N-06, N-07 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-06 | Prepare-time switch check removed | P,N | **detected**: S-14, P-15, P-16, N-33 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-07 | DK-8 binding tuples dropped from the ContentHeadCommitted body | P,R | **detected**: P-02, P-09, P-21, R-01 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-08 | v2 -> v1 downgrade guard removed | N | **detected**: N-40 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-09 | S-4 schema/envelope tie removed | N | **detected**: N-34, N-35, N-38 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-10 | L-13 ledger verification made a no-op | D | **detected**: D-01, D-02, D-03, D-04, D-05, D-06, D-07 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-11 | policy-observation gate removed (acceptance effective without a recorded policy) | N | **detected**: S-13, S-14, N-33 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-12 | 23505 ledger refusal translation removed | C | **detected**: C-02 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MX-13 | Release B transactional floor fence made a no-op | D | **detected**: D-08, D-09, D-10, D-11, D-12, D-13 | scratch file sha256 equal; repository file unchanged; containers removed: true |
| MD-01 | database: P-1 binding guard trigger disabled | L | **detected**: L-02 | M-RESTORE: catalog digest and runtime grants equal; containers removed: true |
| MD-02 | database: P-2 UPDATE/DELETE immutability trigger disabled | L | **detected**: L-05 | M-RESTORE: catalog digest and runtime grants equal; containers removed: true |
| MD-03 | database: global unique index replaced by a non-unique index | L | **detected**: L-03 | M-RESTORE: catalog digest and runtime grants equal; containers removed: true |
| MD-04 | database: runtime role granted UPDATE and DELETE on the ledger | L | **detected**: L-04 | M-RESTORE: catalog digest and runtime grants equal; containers removed: true |

**Totals: 17 mutations; 17 detected; 0 survived; 0 not restored.**

Every mutation was detected by at least one designated check; none survived. Detection never relied only on an unrelated failure: each
row lists the designated checks that failed.

**MX-01 (DK-4 removed) is masked externally.** With DK-4 removed, every reuse is still refused with the identical response
(`ENVELOPE_INVALID: DATA_KEY_IDENTIFIER_REUSED`, 422, nothing persisted) by the unique-index backstop at the DK-5 insert (L-3 rollback). The
only observable difference is that the candidate rows are **signed before** the refusal (for example N-02: signatures 13 → 16; C-01:
6 rows signed instead of 3). The test-only signing counter is therefore the detector of DK-4's placement (OD-M2e-12: no signature for a
refused reuse); response-level tests alone cannot tell DK-4 from the backstop.

**Mutation-driven harness corrections (before the runs of record).** The first full mutation pass found that the database mutations
(P-1, P-2, unique index) were caught only indirectly: L-13 refuses every ledger-using write once any ledger object is weakened, so the
harness stopped before the direct SQL checks N-30/N-31. Group L (raw SQL, no ledger-using application path) was added so that each
database control is evidenced by its own refusal; the MD-03 restore was extended to remove the duplicate row the weakened index had
admitted (otherwise the unique index cannot be recreated in place), and a PowerShell 5.1 array-unwrapping bug that had hidden that failed
restore was fixed. The ME-13 gap of E3-M2e-1 (DK-8 tuples dropped, detected only by source shape) is now closed behaviourally (MX-07).

## 8. DK-8 reconstruction (M2-RST-06) and BD-1 / BD-2 findings (owner authorization §8)

Group R uses its own organization. Five groups are accepted before the restore point T0; T0 is a disposable `CREATE DATABASE … TEMPLATE`
copy; K1, K2, K3 are accepted after T0; three reservations never commit (one Prepared, one aborted at Commit by the switch, one expired).

| Item | Result (both runs) | Classification |
|---|---|---|
| **R-01 reconstructible committed bindings** | the ledger rebuilt deterministically from the **archived** `ContentHeadCommitted` bodies (production `ArchiveProcessor`, in-memory archive, chain verified) equals all **8** bindings of committed operations, column for column (binding id, identifier, organization, record, kind, commitment, count, operation, bound-on) | counted check (pass) |
| **R-02 unrecoverable uncommitted reservations** | **3** (Prepared, Aborted, Aborted) in the live ledger, **0** in any archived body | `EXPECTED_LIMIT` (OD-M2e-03 accepted loss) |
| **R-03 missing operation dependencies (BD-2, read-only on T0)** | the **3** post-T0 bindings are absent from the restored ledger **and** their **3** committed operations are absent (FK RESTRICT); the restored guard function admits a binding only for a `Prepared` operation; nothing was inserted | `EXPECTED_LIMIT` |
| **R-04 archive completeness** | with one archived body removed, the reconstruction silently yields 7 of 8 tuples and raises nothing; only the digest chain over the archive shows the gap (revision gap) | `EXPECTED_LIMIT` |
| **R-05 commitment validation** | an altered commitment in an archived tuple is indistinguishable in the archive alone; content bytes verified against the Authority leaves (digest equality) yield the true commitment (= ledger, ≠ altered); the Authority can never check the commitment against the seed | `EXPECTED_LIMIT` |
| **R-06 contrast (primary, not restored)** | K1 transplanted to another record is refused (`DATA_KEY_IDENTIFIER_REUSED`) | counted check (pass) |
| **R-07 BD-1 (T0 copy only)** | an instance with acceptance on, attached to the restored database, accepts a New group that re-uses the lost identifier K1 (Prepare OK, Commit OK, K1 bound again); readiness reports ready (`v3 (floor 3); phase=ReleaseB requiredFloor=3`); **the acceptance policy record was itself restored** with the database (`AlreadyRecorded`), so the policy-observation gate did not help | **`EXPECTED_GAP_BD1`** — the gap BD-1 must close; **not** a passing security property |

**Findings for BD-1 / BD-2 design (not implemented):**

- BD-1: none of the controls present today (switch, policy record, Release B fence, floors, readiness, L-13) detects a ledger restored to
  an earlier point. The policy-observation record lives on the platform journal **in the same database** and is rolled back with it; any
  barrier must be anchored outside AuthorityDb (RB2-3).
- BD-2: reconstruction from complete archived bodies is exact for committed bindings, but (a) uncommitted reservations are not
  reconstructible, (b) the committed operations they reference are missing after a full restore, and (c) P-1 refuses inserts for
  non-Prepared operations, so a recovery-plane path is required. No reconstructed binding was inserted anywhere.
- Archive completeness and commitment correctness cannot be established from the archived tuples alone (R-04, R-05; closure §3.6).

## 9. Development history (harness correctness; owner authorization §11)

Focused development runs preceded the runs of record. Every correction below changed the **harness or tooling only**; no production
source was changed. Expected outcomes that changed are listed with their reason.

| Run | Finding | Correction |
|---|---|---|
| dev-1 | The gate script stopped: PowerShell 5.1 turns the Vault readiness probe's expected stderr into a terminating error (containers removed by `finally`) | Readiness and provisioning calls wrapped with exit-code checks |
| dev-2 | `AmbiguousMatchException`: `FieldSealBatchV2.Seal` has an internal deterministic overload | Reflection now selects the public provider-seeded overloads by exact signature (fidelity note 1) |
| dev-3 | N-01 refused by `ITEM_IDENTITY_REUSED` (item-identity rule precedes DK-4); N-42 refused by `SHARE_ITEM_FRAMING_CHANGED`; digest query `text || "char"` (42725) | N-01 asserts the observed earlier rule and **N-01b** added for DK-4; N-42 asserts the observed code and is recorded as a **D-9 deviation** (§10.2); `tgenabled::text` |
| dev-4 | D-10 restore re-added the floor from its rendered text, which PostgreSQL re-renders differently; cascaded to D-11/D-12/D-14 and group R | D-10 restores through the product's own `--reinstate-recorded-floor` |
| dev-5 | groups D, R, H: 41 / 0 | — |
| dev-6 | all groups: 133 / 0 | Loosely matched expectations then **tightened** to the exact observed reasons (N-34..N-39, X-03..X-05) |
| mutation pass 1 | MD-01..MD-03 not detected by their designated checks (§7) | Group L added; MD restores extended; driver bugs fixed (`-Only` parsing, empty selection, PS 5.1 unwrapping) |

## 10. Defects and remaining risks

### 10.1 New production defects

**None found.** No invariant failed, no stop condition was hit, no bypass of ledger uniqueness or binding checks was found, no downgrade
was accepted outside the expected C-14 demonstration, no floor or CARV invariant was weakened, and no mutation survived.

### 10.2 Deviations and observations (owner decisions requested)

| ID | Item | Detail |
|---|---|---|
| **O-1 (D-9 deviation)** | Share edit replacing a v1 item with a New v2 envelope | Approved expectation: `SHARE_ENVELOPE_NOT_ALLOWED`. Observed: `SHARE_MUTATION_OUT_OF_SCOPE: SHARE_ITEM_FRAMING_CHANGED` (403), an earlier share-scope rule (signed schema 1 → 2 is a framing change). Refused before signing, nothing persisted. A v2 → v2 re-seal (N-43) reaches `SHARE_ENVELOPE_NOT_ALLOWED` as approved. Not a security weakness; the expected code in D-9 is not the first refusal for this shape |
| **O-2** | M2-REP-01 (historical row under its own item id) | Refused by the permanent item-identity rule (R-53) before DK-4; DK-4 for an accepted identifier is evidenced by N-01b, N-02, N-03 |
| **O-3** | Reflection adapter to the internal M1 core | Fidelity note 1; owner acceptance requested as the evidence basis for "real M1 core" |
| **O-4** | `Microsoft.IdentityModel.*` version difference between the Authority (8.19.2) and business (8.23.0) outputs | Harness-only merge observation (fidelity note 2); a dependency-alignment question for the business/Authority build, not for E3-M2e |
| **O-5** | BD-1 also defeats the policy-observation gate | §8 R-07 |

### 10.3 F-E2

F-E2 did not affect these runs: the ordinary `--migrate` never ran with runtime writers (S-02, F-08), the runtime grants equalled their
baseline at every sentinel, and the server logs contain no unexpected `permission denied`. F-E2 was **not** exercised and **not**
remediated; it remains OPEN. This record does not claim that replay resolves every ambiguous post-Commit failure.

### 10.4 Carried forward (none closed by this record)

- **BD-1, BD-2** (blocking for persistent activation; demonstrated, not implemented); **R-M2e-1, R-M2e-2** unchanged.
- **R-M2e-14** (DK-8 non-empty and positive carried Commit untested): **now evidenced** in disposable Development/Testing (P-02..P-10,
  P-21, R-01, MX-07); not production evidence.
- **R-M2e-15 / F-E2**: open. **R-A1-1..R-A1-5**: unchanged (R-A1-1 respected by scheduling, §6).
- R-M2e-3, -4, -6..-12; R-M2d-1..8; R-M2c-2, -6, -11; R-S4-1/-3/-4; R-M2b2-1..10; R-CARV-11, -12, -18, -21..-27; R-M1-1..8; R-M2a-1..6.
- **V2-IND-01** (independent review), PB-03 Azure staging evidence, PB-09 benchmarks (no performance claim here), PB-11/PB-12 governance
  and protected configuration, RB-CARV-1 production closure, OA-FE1-2 (verified A-1 deployment inventory before any persistent floor
  raise), Azure PostgreSQL definition verification (R-CARV-18/23), M2f, M2g, E3-M3/M4.
- Business mapping of `DATA_KEY_IDENTIFIER_REUSED` (OD-M2e-14) and the business half of M2-DK-11: not testable before M4.
- AM-9 outside Development/Testing: cannot be exercised with acceptance on (unit-tested only).

## 11. Final repository status

Untracked, test-only additions only (§2), plus this record, the evidence and the regenerated specification manifest. No tracked file was
modified other than `spec-files.json` and `SHA256SUMS.txt` (manifest regeneration). No commit, push, merge or deployment; no Azure call;
Stage B not run.

## 12. Signed-commit readiness

**Superseded by §14:** the owner accepted the evidence and authorized one signed local commit (2026-11-16). Original text retained:

**Recommendation: ready for owner review; a signed local commit is not authorized by this record.** If the owner accepts the evidence,
one signed local commit could contain exactly: the harness and README, the two gate scripts, this record, `docs/evidence/e3-m2e2/` and
the regenerated manifest. Owner decisions requested before any commit:

1. Accept O-1 (D-9 deviation: `SHARE_ITEM_FRAMING_CHANGED` for v1 → New v2 share edits) or require a change of expectation.
2. Accept O-3 (reflection adapter) as the basis for "real M1 core" evidence.
3. Note O-4 for the build owners (dependency alignment, outside E3-M2e).
4. Confirm that acceptance stays **prohibited** in every persistent environment (BD-1, BD-2 and the class 2/3 prerequisites unchanged).

## 13. Not claimed

- No persistent activation; no production, Staging or Azure evidence; no independent review; no certification.
- No BD-1, BD-2, F-E2 or M2f implementation.
- No claim that archived tuples alone prove ledger completeness or commitment correctness.
- No performance or availability claim.

## 14. Owner acceptance (2026-11-16)

Owner-led acceptance of the disposable Development/Testing verification. **Not** an independent review, **not** a certification, **not**
production readiness; no production, Staging, Azure or independent-review gate is closed by it.

| ID | Decision |
|---|---|
| **OA-M2e2-01** | E3-M2e-2 verification accepted for **disposable Development/Testing** only |
| **OA-M2e2-02** | O-1 accepted: the earlier share-item framing refusal. Exact outcomes: a v1 item replaced with a New v2 envelope is refused **403 `SHARE_ITEM_FRAMING_CHANGED`** (N-42); an existing v2 item re-sealed is refused **`SHARE_ENVELOPE_NOT_ALLOWED`** (N-43). Production code is **not** changed to standardize these responses |
| **OA-M2e2-03** | O-2 accepted: the item-identity refusal (`ITEM_IDENTITY_REUSED`) precedes the data-key check for M2-REP-01; DK-4 is evidenced by N-01b, N-02, N-03 |
| **OA-M2e2-04** | O-3 accepted: reflection-based invocation of the internal M1 core (`M1Core.cs`) is accepted **for test evidence only**; it changes no production visibility and is not a production integration path |
| **OA-M2e2-05** | O-4 accepted as a documented **harness fidelity limitation** (`Microsoft.IdentityModel.*` 8.19.2 Authority vs 8.23.0 business; Authority copies kept in the merged harness) |
| **OA-M2e2-06** | Envelope v2 acceptance remains **prohibited in every persistent environment**; BD-1, BD-2 and the class 2/3 prerequisites are unchanged |

**Evidence qualifications preserved by the acceptance:**

- Two independent runs of record (`record-1`, `record-2`), **138/138 counted checks each**.
- **Five** expected-gap/limit demonstrations per run (R-02..R-05 `EXPECTED_LIMIT`, R-07 `EXPECTED_GAP_BD1`), **outside** the ordinary pass totals.
- **17/17** enforcement-removal mutations detected, all restored and verified.
- No unexpected deadlocks, lock timeouts or permission errors (6 expected, labelled `42501` per run).
- DK-8 reconstruction matched **8/8** committed bindings, column for column.
- **Three** uncommitted reservations were **not** reconstructible (accepted loss, OD-M2e-03).
- **BD-1** remains an explicitly demonstrated security gap (R-07), not a passing property; it also defeats the policy-observation gate (O-5).
- **BD-2** remains unresolved.
- The archive reconstruction is **not** proof of complete G14 recovery (R-04, R-05).
- The reflection adapter and the merged IdentityModel dependencies are **test-harness limitations**.
- **F-E2** remains OPEN and was **not** exercised.

**Authorized:** exactly one signed local commit on `e3/m1-envelope-v2-core` (parent `4a71dbc6a436ec812ae0db059bd1acc4860cad6a`)
containing the harness and README, the two gate scripts, this record, `docs/evidence/e3-m2e2/` and the regenerated specification
manifest. **Not authorized:** push, merge, deployment, Azure calls, persistent database or floor changes, persistent acceptance, Stage B,
production source/configuration changes, F-E2, BD-1, BD-2, E3-M2f or later milestones, closure of any independent-review or production
gate.
