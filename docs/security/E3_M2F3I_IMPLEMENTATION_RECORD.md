# E3-M2f-3i — Capability-Refusal Evidence Index Provisioning: Implementation Record

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | E3-M2f-3i, as specified in `E3_M2F3_CONTRACT_CLOSURE.md` §5A (governing) and the owner's E3-M2f-3i authorization |
| Authorization | Owner, 2026-11-17: **OD-IX-1 approved with conditions**; code, documentation, focused tests and disposable PostgreSQL verification only. Not authorized: E3-M2f-3b, any persistent database change (OD-IX-2 open), commit, push, merge, deployment, Azure, Stage B, Envelope v2 acceptance |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `3009878f55dc4430cc90ef5092764ed01abeff80`, good SSH ED25519 signature; working tree held only the uncommitted E3-M2f-3a documentation and manifest changes, which are preserved |
| Result | Implemented for **Development/Testing**. Focused, full Authority and architecture suites green; disposable PostgreSQL gate **40/40 in two independent runs of record**. *(2026-11-17: accepted for Development/Testing by the owner; committed locally with the E3-M2f-3 closure; mutations MZ-34..MZ-36 were never executed, see `E3_M2F3G_VERIFICATION_RECORD.md` §12.2)* *(2026-11-17, FU-M2f3-02: MZ-34 (grants re-applied; build in a transaction block), MZ-35 (INVALID index treated as present) and MZ-36 (predicate literal differs from the recorder constant) executed on this gate and the 3G gate and all DETECTED; `E3_M2F3_FU_VERIFICATION_RECORD.md` §3)* |
| Environment | Development/Testing only. Fresh `postgres:18.0` containers (tmpfs, loopback, random name, port and credentials), removed after every run; no persistent database touched |
| Review | Owner-led. Not an independent security review and not a certification |

---

## 1. Scope delivered

| Item | Where |
|---|---|
| Evidence identity constants (stream `audit`, kind `EnvelopeCapabilityRefused`); constants only, nothing writes such an entry | `src/authority/AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidence.cs` (new) |
| Approved index definition (IXP-1), pinned PostgreSQL 18 renderings (IXP-8), approved lookup text (IXP-6), catalogue inspection and classification, lock-free catalogue snapshot | `src/authority/AuthorizationAuthority.Infrastructure/Persistence/CapabilityRefusalEvidenceIndex.cs` (new) |
| Dedicated privileged command `--provision-capability-refusal-evidence-index [--verify-only]` | `src/authority/AuthorizationAuthority.Api/Composition/CapabilityRefusalEvidenceIndexCommand.cs` (new) |
| Dispatch, before the floor and `--migrate` commands | `src/authority/AuthorizationAuthority.Api/Program.cs` (modified) |
| Unit tests (40) | `tests/authority/AuthorizationAuthority.Tests/CapabilityRefusalEvidenceIndexTests.cs` (new) |
| Architecture tests (21) | `tests/architecture/EnterpriseForms.ArchitectureTests/CapabilityRefusalEvidenceIndexBoundaryTests.cs` (new) |
| Disposable PostgreSQL gate | `scripts/dev/e3-m2f3i-index-pg-gate.ps1`, `tools/experiments/e3-m2f3i-index-gate/HarnessI/` (new) |
| Evidence of record | `docs/evidence/e3-m2f3i/run-1/`, `run-2/` (new) |
| Runbook | `E3_M2B1_CARV_OPERATIONS.md` §11 (appended) |

**Not delivered (as authorized):** the E3-M2f-3b recorder, retry service and endpoint filter; any change to the capability decision or the 409 contract; any EF model or migration change; any persistent rollout.

## 2. Index definition verified against the repository

| Item | Verified |
|---|---|
| Table | `authority.security_journal_entries` (EF `SecurityJournalEntryConfiguration`; created in `20261018044514_InitialAuthorityStore`) |
| Columns | `organization_id uuid NOT NULL`, `journal_stream_revision bigint NOT NULL`, `journal_stream_name varchar(30) NOT NULL`, `journal_event_kind varchar(60) NOT NULL` |
| Predicate literals | `JournalStreamNames.Audit = "audit"`; `EnvelopeCapabilityRefused` (25 characters ≤ 60) |
| DDL | Exactly the approved statement; **no correction to the definition was needed** |
| PostgreSQL 18 rendering | `CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries USING btree (organization_id, journal_stream_revision) WHERE (((journal_stream_name)::text = 'audit'::text) AND ((journal_event_kind)::text = 'EnvelopeCapabilityRefused'::text))`, confirmed by the gate (I-02) |
| Correction to the closure (documentation only) | §5A.1 listed three journal indexes. A fourth exists: `ix_journal_search_index_form_events`, a partial expression index created by the ordinary migration `20261110161016_AuthorityJournalSearchIndexFormEvents` (C-143), outside the EF model, for two other event kinds. It cannot serve the evidence lookup (different predicate) and does not change the design. The closure is corrected in place (§5A.1, dated note) |

## 3. Command design

**Sequence (one connection, autocommit, application name `authority-capability-evidence-index`):**

1. Flags: refuses `--migrate`, `--apply-content-acceptance-floor`, `--reinstate-recorded-floor` and unknown options (exit 2).
2. Identity: `ConnectionStrings:AuthorityDbMigration` only; no fallback to `AuthorityDb` (exit 2 if absent).
3. `SET lock_timeout = '5s'`, `SET statement_timeout = 0`.
4. `pg_try_advisory_lock(1161055538, 1177774385)`: session-level, no table or row lock; a concurrent invocation exits 10.
5. Target check: table exists; `pg_has_role(current_user, table owner, 'USAGE')`. Provisioning by a non-owner is refused (exit 7) whatever the index state.
6. Inspection (`FactsSql`, `Classify`): Absent, ValidMatching, Invalid, BuildInProgress, DefinitionMismatch. Every deviation is named (relation kind, table, access method, unique/primary/exclusion, expressions, INCLUDE columns, column order, ordering options, operator classes, predicate, `pg_get_indexdef`, owner, ACL).
7. Only if Absent: lock-free catalogue snapshot; the single statement IXP-1 as its own command (`CommandTimeout = 0`); on a PostgreSQL error, re-inspection and report (55P03 → exit 8; 42501 → exit 7; otherwise exit 4 if an invalid index was left, else 3).
8. Re-inspection must be ValidMatching; second snapshot; the difference must be exactly the index relation line and its valid index row (`IsExactlyTheCreation`), else exit 9.
9. One JSON report line (no connection details or credentials).

**No repair path.** The command contains no `DROP`, `REINDEX`, `ALTER`, `GRANT` or `REVOKE` (architecture test). Invalid and mismatched indexes are reported and left exactly as found.

## 4. Privilege and concurrency analysis

| Concern | Analysis | Evidence |
|---|---|---|
| Identity | Migration identity only; `CREATE INDEX` requires table ownership, and the command checks ownership before trying | I-18 (runtime role and an unrelated login refused, exit 7, nothing changed); unit tests (no fallback) |
| Privilege expansion | No grant, revoke, role or membership statement; indexes carry no privileges; `AuthorityRuntimeGrants.Apply` is never called, so there is no F-E2 window | I-19 (privilege matrix of the runtime role and the login, role attributes and memberships unchanged; index ACL empty); I-09 (trace); architecture tests |
| Migration history and Release B | No EF migration, no `ef_migrations_history` read or lock, no `content_change_operations` lock | I-22 (completes while another session holds `ef_migrations_history` and `organization_authorization_states` ACCESS EXCLUSIVE); I-09 (no statement names either table); I-04/I-24 (migration rows unchanged) |
| Organization rows | No organization-row or organization-table lock | I-22, I-09 |
| Transactions | No explicit transaction; `CREATE INDEX CONCURRENTLY` alone in its command | I-09 (no BEGIN/COMMIT/ROLLBACK/SAVEPOINT); architecture tests |
| Concurrent invocations | Session advisory lock: the second invocation exits 10 before inspecting | I-14 |
| Concurrent non-command build | Classified BuildInProgress (progress view or another session's SHARE UPDATE EXCLUSIVE on the table) | I-15 |
| Lock waits | `lock_timeout = 5s` bounds both the table lock (nothing created) and the build's waits for older transactions (invalid index left, reported) | I-20, I-21 |
| Live writers | Concurrent build with autocommit writers, organization-locked writer transactions and archive-state updates: no writer error, no deadlock, the index contains exactly the matching rows | I-30; `DeadlockLines = 0` in both runs |
| R-A1-3 / A-1 | The build takes only SHARE UPDATE EXCLUSIVE on the journal and waits for other transactions' completion; it holds no organization row and no history lock, so it cannot form the organization-versus-history cycle | I-22, I-30 |

**Finding F-3i-1 (found and fixed during the gate, before the runs of record).** PostgreSQL 18's deparsing functions take ACCESS SHARE on the underlying table. A disposable probe confirmed it for `pg_get_indexdef` (partial index), `pg_get_expr`, `pg_get_constraintdef` (CHECK) and `pg_get_triggerdef` (trigger with WHEN), while raw `pg_node_tree` text, `regclass` and `regprocedure` output take none. The first catalogue snapshot used the deparsing functions, so the command waited on the organization table and failed with 55P03 (first trial, I-22). The snapshot now reads raw catalogue columns only, and `pg_get_indexdef` / `pg_get_expr` in the inspection run only for an index on the journal itself. A unit test pins this. This was an implementation correction, not a change to the index definition.

**Finding F-3i-2 (found and fixed during the gate).** `text || "char"` is ambiguous in PostgreSQL (42725); the snapshot now casts `"char"` and boolean columns to text explicitly.

## 5. Disposable PostgreSQL verification

Gate: `powershell -File scripts\dev\e3-m2f3i-index-pg-gate.ps1 -Evidence <folder>`. One fresh container per run (`postgres:18.0`, PostgreSQL 18.0, autovacuum off, `log_statement = all`), one migrated template database, one throwaway database per scenario. The command always runs as a separate process of this tree's Release build, exactly as an operator would run it. The harness verifies with its own catalogue queries, not the command's.

| Run of record | Checks | Failed | Container removed | New Docker volumes | Deadlock lines | Elapsed |
|---|---|---|---|---|---|---|
| run-1 | 40 | 0 | yes | 0 | 0 | 88 s |
| run-2 | 40 | 0 | yes | 0 | 0 | 89 s |

| Owner item | Checks (both runs pass) |
|---|---|
| 1 Fresh creation | I-01, I-23 |
| 2 Repeated invocation, idempotency | I-07, I-08, I-15 (AlreadyValid after another session's build) |
| 3 Existing invalid index | I-11, I-17 |
| 4 Same name, wrong definition | I-13.1 column order, .2 unique, .3 kind-only predicate, .4 no predicate, .5 descending revision, .6 other table, .7 not an index |
| 5 Concurrent invocation | I-14 (Busy), I-15 (build in progress elsewhere) |
| 6 Interrupted concurrent build | I-11 (another session's build cancelled), I-16 (the command's own build cancelled, SQLSTATE 57014, invalid index left with the same oid), I-12 (manual recovery) |
| 7 Insufficient privileges | I-18 |
| 8 Lock timeout | I-20 (table lock; nothing created), I-21 (wait for an old snapshot; invalid index reported and left) |
| 9 Catalogue verification | I-02, I-03 (predicate semantics: usable only for stream `audit` and kind `EnvelopeCapabilityRefused`), I-04, I-06 |
| 10 Query-plan use | I-25..I-29 (below) |
| 11 Large unrelated history | I-23 (2,053,500 journal rows: 1,050,350 for one mature organization with no evidence; 5,000 evidence rows; concurrent build about 1.0–1.2 s; index 221,184 bytes against about 669 MB of table and indexes) |
| 12 No authorization, content, ledger or quarantine change | I-05, I-24 (row count and content hash of all 61 Authority tables, including the 2M-row journal) |
| 13 No runtime privilege expansion | I-19 |
| 14 No Release B / migration-history interaction | I-22, I-09, I-04 |
| 15 Clean disposal | Summary: container removed, no new volume, work directories removed, evidence scrubbed of paths and credentials |

**Query-plan evidence** (approved lookup IXP-6 with literal stream and kind, executed as the runtime role; JSON plans in `plans/`):

| Condition | Mature organization, no evidence | Organization with 5,000 evidence rows |
|---|---|---|
| No statistics | `Limit > Index Scan Backward` on IX-B, 0 rows, 2 buffers | Same plan, 8 rows, 9 buffers |
| After ANALYZE | Same, 0 rows, 2 buffers | Same, 8 rows, 9 buffers |
| Dead index entries (archive-state updates, no vacuum) | Same, 0 rows, 2 buffers | Same, 8 rows, 26 buffers |
| After VACUUM ANALYZE | Same, 0 rows, 2 buffers | Same, 8 rows, 3 buffers |
| Generic prepared plan (`force_generic_plan`) | — | Same plan, 8 rows, 3 buffers (I-29) |

No plan used another index, a sequential scan, a bitmap scan or a sort. **Demonstration D-1 (outside pass totals):** with the stream and kind passed as bind parameters, the generic plan uses `uq_journal_stream_revision` with a filter, not IX-B (28–29 buffers here, growing with the organization's history). This confirms that the literals in IXP-6 are binding on E3-M2f-3b.

**Not established:** the plan on Azure PostgreSQL, on other PostgreSQL versions, or with production data distributions. Planner behaviour on this container does not guarantee the same plan elsewhere (TQ-18).

## 6. Test results

| Suite | Result |
|---|---|
| `CapabilityRefusalEvidenceIndexTests` (unit) | 40/40 |
| `CapabilityRefusalEvidenceIndexBoundaryTests` (architecture) | 21/21 |
| Full Authority suite | 1148 total, 1144 passed, 0 failed, 4 skipped (baseline 1108 + 40) |
| Full architecture suite | 194/194 (baseline 173 + 21) |
| Disposable PostgreSQL gate | 40/40, two runs of record |

Not run (not authorized or not affected): contract suite (unchanged; TS-M2f-01 remains the recorded open failure), Stage B, integrity and console suites. No mutation driver was run for this increment.

## 7. Closure test mapping

| Closure test | Status |
|---|---|
| M2F3-73 creation, idempotency, pinned rendering | Executed: I-01, I-02, I-07 |
| M2F3-74 plan on a mature journal | Executed: I-25, I-26 |
| M2F3-75 plan stability | Executed: I-27, I-28 |
| M2F3-76 prepared statement and parameter demonstration | Executed: I-29, D-1 |
| M2F3-77 contract: literals and pinned definition | Executed: unit tests |
| M2F3-78 T2-8 refusals | Partly: the classification is unit-tested for every deviation and exercised on PostgreSQL (I-11, I-13, I-16); the T2-8 runtime check itself belongs to E3-M2f-3b |
| M2F3-79 interrupted build and cleanup | Executed: I-11, I-12, I-16, I-17 |
| M2F3-80 no grant re-application, privileges unchanged | Executed: I-19, I-09, architecture tests |
| M2F3-81 outside a transaction, lock timeout, migration identity | Executed: I-09, I-20, I-21, unit and architecture tests |
| M2F3-82 concurrent build with live writers | Executed: I-30 (disposable only; does not approve live-writer rollout) |
| M2F3-83, M2F3-84 | Require the E3-M2f-3b recorder; executed in E3-M2f-3G (2026-11-17, `E3_M2F3G_VERIFICATION_RECORD.md`: H-09, G-05, L-09..L-12) |

## 8. Remaining technical questions and risks

| ID | Item | Status |
|---|---|---|
| TQ-18 | Planner choice on Azure PostgreSQL and with production statistics | Open (Azure later). On PostgreSQL 18 here every variant used IX-B |
| TQ-19 | `pg_get_indexdef` rendering on Azure PostgreSQL | Open; PostgreSQL 18.0 rendering pinned |
| TQ-20 | Command construction and model exclusion | Resolved: raw Npgsql command; architecture tests pin that no EF model or migration declares the index |
| TQ-21 | G08 inventory expected outputs | Open: the G08 ownership and privilege inventories are not updated by this increment; the new index is owned by the table owner and carries no ACL (I-02, I-19) |
| TQ-23 | Build duration and waits | Measured on disposable data (about 1 s for 2M rows; 5 s lock-timeout behaviour verified). Persistent-scale behaviour remains OD-IX-2 |
| R-3i-1 | `BuildInProgress` also reports a session holding SHARE UPDATE EXCLUSIVE for VACUUM or ANALYZE (conservative; the operator re-runs) | Accepted |
| R-3i-2 | An invalid index after exit 4 or 8 needs a privileged `DROP INDEX CONCURRENTLY` (runbook); in a persistent environment that needs its own approval | Accepted; OD-IX-2 |
| R-3i-3 | The ownership check uses `pg_has_role(..., 'USAGE')`; on Azure, the migration identity's membership in the owning role must be confirmed | Open (Azure) |
| R-3i-4 | `--verify-only` runs with any identity that can connect (read-only catalogue inspection) | Accepted |
| R-3i-5 | Release builds are not byte-reproducible; the evidence records hashes of the binaries used, not provenance | As earlier gates |

Carried forward unchanged: OD-IX-2, E3-M2f-3b/3G, B-M2f3 conditions, BD-1, BD-2, G14 recovery, F-E2, R-A1-1..5, R-M2f-1..10, UQ-4, R-CARV-12 for other classes, TS-M2f-01, FU-M2E2-MUT-01, V2-IND-01, PB-11, PB-12, Azure verification.

## 9. Recommendation

Accept E3-M2f-3i for Development/Testing. The command meets every OD-IX-1 condition that can be verified locally, and both runs of record pass. Two items are left for later and do not block E3-M2f-3b design:
- Azure behaviour (TQ-18, TQ-19, R-3i-3).
- The G08 inventory update (TQ-21).

Persistent rollout stays blocked on OD-IX-2.

## 10. Not claimed

- No persistent database was changed; the command was not run against the developer stack, the security-test stack, staging or production.
- No E3-M2f-3b functionality exists; no evidence is recorded by any build.
- No Azure, staging or production evidence; no claim about Azure planner behaviour.
- No independent review, certification or production readiness.
