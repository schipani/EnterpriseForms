# F-E2 — Ordinary `--migrate` Runtime-Grant Window and Post-Commit Response Misreport: Defect Record

| Field | Value |
|---|---|
| Defect | **F-E2** (risk register: R-M2e-15) |
| Status | **OPEN.** Recorded, not investigated beyond the evidence below, **not fixed** (owner decision OA-FE1-3, 2026-11-16) |
| Class | Availability and response consistency. **Fails closed:** no observed path accepts content, lowers a floor or widens a permission |
| Found | 2026-11-16, while verifying F-E1 / RB-CARV-1 amendment A-1 (stress sub-check `FM`, `E3_M2E1_IMPLEMENTATION_RECORD.md` §13) |
| Environment | Development/Testing only: disposable PostgreSQL 18.0 container, harness `tools/experiments/e3-m2e1-ledger-gate` (`-Mode fe1`). No persistent database, staging or Azure environment |
| Component | `AuthorityRuntimeGrants.Apply`, called by `MigrateCommand` (the ordinary Authority `--migrate`); Commit response construction in `ContentService` |
| Blocks | **Does not block** the E3-M2e-1 local commit (OA-FE1-3). It is an open item for any deployment that runs `--migrate` while Release B writers are live (see §6) |
| Relation to F-E1 | Independent of F-E1 and of A-1: observed with and without A-1. It is not part of RB-CARV-1's floor mechanism. Closing F-E1 does not close F-E2 |

Owner-led record; not an independent review.

---

## 1. Description

After applying its migrations, the ordinary `--migrate` re-applies the runtime role's grants (`AuthorityRuntimeGrants.Apply`). It builds a list of statements and executes each with `context.Database.ExecuteSqlRaw`, **outside any transaction**, so each statement auto-commits on its own. The list starts with:

1. `REVOKE ALL ON SCHEMA authority FROM PUBLIC`
2. `GRANT USAGE ON SCHEMA authority TO <runtime>`
3. `REVOKE ALL ON ALL TABLES IN SCHEMA authority FROM <runtime>`

and only then grants table privileges back, table by table, followed by the search-index and search-access schema statements.

Between statement 3 and the grant of a given table, the runtime role has **no privilege** on that table. This is a real, committed permission gap, visible to every concurrent runtime session. The command runs it even when no migration is pending.

## 2. Observed consequences

### 2.1 Concurrent operations are refused (fail closed)

Live Authority operations that touch a table inside the gap fail:

- with `42501 permission denied` raised from the statement that touches the table, or
- with `503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED`, reason `FloorUnverifiable`, when the denied statement is the Release B fence's own history lock or facts read (a Critical alert is raised).

These refusals write nothing and are fail-closed. They are an availability fault during the window.

### 2.2 A durable Commit can be reported as failed

`ContentService.CommitCoreAsync` commits its transaction (`scope.CommitAsync`) and **then** builds the response with `ToRevisionAsync`, which reads `integrity_key_registrations` (`FindKeyRegistrationAsync`) and the content journal in `security_journal_entries` (`FindContentJournalForHeadAsync`). If the grant window opens between the commit and those reads, the read fails with `42501` and the caller receives an error **although the new content head is durable**.

A client that trusts that error believes the write failed. Its next write on the same record is then based on a stale head and is refused with `409 CURRENT_SNAPSHOT_MISMATCH`, repeatedly, until it re-reads the head.

## 3. Reproduction evidence

Evidence: `docs/evidence/e3-m2e1/fe1/a1/` (A-1 build, 20 stress iterations) and `docs/evidence/e3-m2e1/fe1/pre-a1/` (pre-A-1 build, 6 iterations). Sub-check `FM` in `FloorDeadlockScenarios.StressIterationAsync`: four Release B (floor 3) writers loop on distinct records (round-robin over four organizations) while the ordinary `--migrate` runs with nothing pending.

| Observation | Source | Result |
|---|---|---|
| `--migrate` outcome | `fe1-log.txt`, `FM:` lines | Exit 0 and floor 3 kept in **20 of 20** A-1 iterations |
| Refusals in the window | same | Every A-1 iteration: about 47–59 `42501` and 55–59 `FloorUnverifiable` refusals per iteration, besides 91–143 successful writes |
| Deadlocks | `summary.json`, `pg_stat_database` | **0** in the A-1 run (F-E1 is not involved) |
| Post-commit misreport | `fe1-log.txt` | **8 of 20** A-1 iterations (3, 4, 10, 11, 13, 14, 18, 19) fail `FM` because writers then receive `409 CURRENT_SNAPSHOT_MISMATCH` (19–79 per failing iteration). Each writer is the only writer of its record, so a snapshot mismatch means an earlier write of that writer took effect although it was reported as failed |
| Correlation with the post-commit reads | `postgres-server.log` | `permission denied` on `integrity_key_registrations` or `security_journal_entries` (the two tables `ToRevisionAsync` reads) occurs in **exactly** the same 8 iterations (S03, S04, S10, S11, S13, S14, S18, S19) and in no other iteration |
| Denials by table, A-1 run | `postgres-server.log` | `ef_migrations_history` 1,143; `organization_authorization_states` 895; `search_projection_states` 114; `security_journal_entries` 11; `content_change_operations` 10; `integrity_key_registrations` 3; `content_candidate_leaves` 2; `record_manifest_commits` 1; `authority_resources` 1 |
| Pre-A-1 | `pre-a1/fe1-log.txt` | `42501` refusals in all 6 iterations, but the floors were already missing (F-E1 cascade), so nearly every write was refused by the fence. Not a clean F-E2 observation; the misreport could not occur |

**What the evidence establishes:** the permission gap is real, live writes are refused during it, and a committed Commit is reported as failed in a reproducible fraction of runs. The mapping from the 409 cascade to a post-commit read is strong (exact iteration correlation, one writer per record), but it is an inference from logs; no individual operation was traced end to end from its `COMMIT` to its `42501`.

## 4. Retry behaviour: what is and is not evidenced

- **Source:** `CommitCoreAsync` returns `ToRevisionAsync` for an operation already in state `Committed` (the idempotent replay branch). A retried Commit of the same operation therefore returns the committed head instead of committing again.
- **Tests:** the replay branch is covered by in-memory unit tests (for example `ACommitReplayOfAHistoricalUnstampedCommittedOperationReturnsTheHeadAndWritesNothing`, `S4IsReappliedAtCommitOnTheVerifiedStagedBytesAndAReplayChangesNothing`, `ACommittedCommandReportsItsPhasesAndAReplayAndARefusalReportTheirOutcomes`).
- **Not evidenced:** **no F-E2 run retried a misreported Commit.** The harness writers do not retry; they start a new write. That a replay after an F-E2 misreport returns the committed result in PostgreSQL is therefore an expectation from source and unit tests, not an observed result.
- The replay passes through the same fence, actor resolution and `ToRevisionAsync` reads, so it can only succeed after the grant window has closed. A replay while the floor is not enforced is refused (`ACommitReplayIsRefusedWhileTheFloorIsNotEnforced`).
- **Not claimed:** that every post-Commit failure path is idempotently recoverable. Other post-commit steps (for example fault-injection points, outbox or archive follow-up, and Prepare's response construction) were not examined for this defect.

## 5. Impact

- **Security:** none observed. Every observed failure is a refusal or a misreported success; no content was accepted outside the fence, no floor was lowered, no runtime privilege was widened. The gap removes privileges; it never adds them.
- **Availability:** every content write, and any other runtime operation touching an affected table, can fail for the duration of the grant re-application, on every `--migrate` run, including runs with nothing pending.
- **Response consistency:** a client can be told that a durable Commit failed. Without a replay of the same operation or a head re-read, it diverges from the Authority (stale base, repeated `409`), and a user can be shown a false failure for saved content.
- **Alerting noise:** `FloorUnverifiable` Critical alerts are raised by an ordinary maintenance command, which can mask a real floor incident.
- **Scope uncertainty:** the business API, the worker and the search reader were not exercised against the window; their behaviour is unknown.

## 6. Operational mitigation requirement (until fixed)

Mandatory for any environment beyond disposable Development/Testing databases:

1. **Do not run the ordinary `--migrate` while Release B (or any) Authority runtime instances serve writes.** Run it in a write pause (instances drained or stopped), as already stated in `E3_M2B1_CARV_OPERATIONS.md` §9.
2. Treat `FloorUnverifiable` alerts and `42501` errors raised during a `--migrate` window as expected F-E2 symptoms only when the window is confirmed from the deployment log; otherwise investigate as a floor incident.
3. Clients and operators must resolve an ambiguous Commit failure by **replaying the Commit of the same operation** (or re-reading the head) after the window, never by preparing a new operation on an assumed stale base.
4. Record every production-path `--migrate` run with its start and end time, so refusals can be attributed.

## 7. Proposed investigation scope (not authorized; no fix in E3-M2e-1)

To be scoped by the owner as a separate increment:

- Make grant re-application atomic (one transaction), or non-revoking (grant-only with an explicit, reviewed revoke list), or skip it when the grants already match; assess interaction with EF's migration lock and with A-1 lock ordering.
- Decide whether `--migrate` must refuse to run while runtime sessions are connected, or take a lock that excludes them.
- Decouple the Commit response from post-commit reads that can fail, or classify a post-commit read failure as "committed, response unavailable" (retryable replay) rather than a generic failure.
- Inventory every post-commit read and every post-Commit failure path (Prepare, Commit, record registration, archive and outbox follow-up) and test replay after each in PostgreSQL.
- Exercise the business API, worker and search reader against the window.
- Add a PostgreSQL gate check that retries a misreported Commit and asserts the committed result.

## 8. Not claimed

- Not fixed; no code was changed for F-E2.
- No evidence from staging, Azure PostgreSQL or production.
- No claim that all post-Commit failures are idempotently recoverable.
- Not an independent review.

## References

- `E3_M2E1_IMPLEMENTATION_RECORD.md` §11 (R-M2e-15), §13, §14 (owner decision OA-FE1-3)
- `E3_M2B_IMPLEMENTATION_CONTRACT.md` §22.14.7, §22.14.8
- `E3_M2B1_CARV_OPERATIONS.md` §9
- `src/authority/AuthorizationAuthority.Infrastructure/Persistence/AuthorityRuntimeGrants.cs` (`Apply`)
- `src/authority/AuthorizationAuthority.Api/Composition/MigrateCommand.cs`
- `src/authority/AuthorizationAuthority.Application/Features/Content/ContentService.cs` (`CommitCoreAsync`, `ToRevisionAsync`)
- `docs/evidence/e3-m2e1/fe1/a1/`, `docs/evidence/e3-m2e1/fe1/pre-a1/`
