# SEC-BL-01 WP-3 + WP-4 — Implementation Record: Baseline Reconciliation and Scheduled Verifier

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | WP-3 (read-only baseline reconciliation, `SEC_BL_01_DR_CONTRACT_CLOSURE.md` §10) and WP-4 (scheduled verifier for the audit, authorization and content streams, §8, §9) |
| Authorization | Owner direction of 2026-11-17 for WP-3 and WP-4, Development/Testing only, on the accepted WP-0/WP-1/WP-2 foundation (read by the implementer as the authorization the brief refers to; the owner should confirm). **Not authorized and not done:** WP-5 D+, WP-6 G08 hardening, WP-7 adversarial gate, WP-8 production, G14 recovery, migrations, grants, CI changes, deployment, push, merge, Stage B, commit |
| Baseline | `main` at `a9b32de`; the uncommitted WP-0, WP-1 and WP-2 work preserved (accepted contract SHA-256 unchanged, `4a8bd1b1…ef12e`) |
| Result | Implemented and tested in Development/Testing. Owner acceptance pending. **Not committed** |
| Not changed | Journal recording (`JournalChain`, `AuditJournal`), archive delivery (`ArchiveProcessor`, archive `AppendAsync`/`ReadAsync`), `PlatformJournal`, request paths, quarantine triggers, key purposes, migrations, grants, CI workflows, the base compose files (only a new opt-in overlay) |
| SEC-BL-01 / PD-15 / M2F3-43 | **OPEN / OPEN / FAILED (not re-evaluated).** The delivered-row M2F3-43 cases are now detected in Development/Testing tests; re-evaluation needs WP-7 and owner acceptance |
| Review | Owner-led; prepared by the implementing assistant. Not an independent security review |

---

## 1. Files

| Area | File | Kind |
|---|---|---|
| Ports | `src/authority/AuthorizationAuthority.Application/Common/Ports/IJournalVerificationReader.cs` (read-only snapshot port), `IJournalVerificationFindingStore.cs` | new |
| Shared comparison | `.../Features/Journal/Reconciliation/JournalStreamComparer.cs` | new |
| Findings model | `.../Features/Journal/Reconciliation/JournalVerificationFindings.cs` | new |
| WP-3 | `.../Features/Journal/Reconciliation/JournalBaselineService.cs` | new |
| WP-4 | `.../Features/Journal/Reconciliation/JournalVerifier.cs` (verifier, schedule, coordinator) | new |
| PostgreSQL reader | `src/authority/AuthorizationAuthority.Infrastructure/Persistence/JournalVerificationReader.cs` | new |
| Finding stores | `src/authority/AuthorizationAuthority.Infrastructure/Archive/VerificationCheckpointStores.cs` (Blob and in-memory finding stores appended) | modified |
| Host | `src/authority/AuthorizationAuthority.Api/Composition/JournalVerificationComposition.cs` (composition + alert-only loop), `JournalBaselineCommand.cs`, `Program.cs` (three dispatch/registration lines) | new / modified |
| Development configuration | `infrastructure/docker/compose.journal-verification.yaml` (opt-in overlay) | new |
| Tests | `tests/authority/AuthorizationAuthority.Tests/JournalVerificationWorld.cs`, `JournalBaselineServiceTests.cs`, `JournalVerifierTests.cs`, `JournalVerificationPostgresIntegrationTests.cs`, `ArchiveDeliveryOutageCharacterizationTests.cs`; `tests/architecture/.../JournalVerificationBoundaryTests.cs` (one WP-2 rule superseded, four added) | new / modified |
| Gate | `scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1` (disposable PostgreSQL 18.0 + two Azurite) | new |
| Evidence | `docs/evidence/sec-bl-01-wp3-wp4/run-1/` (`console.txt` path-scrubbed, `measurement.json`) | new |
| Documentation | this record; dated notes in `SEC_BL_01_WP1_WP2_IMPLEMENTATION_RECORD.md` §7, a progress row in `SECURITY_BACKLOG.md`, a line in `docs/infrastructure/DEV_ENVIRONMENT_PERSISTENT_VAULT.md` §7; OBS-2 clarification in `scripts/dev/check-journal-store-coupling.py` | modified |

## 2. Shared comparison (`JournalStreamComparer`)

One comparison of an AuthorityDb stream with the archive, used by the baseline, the incremental and the full sweep (no duplicated chain logic; R-1..R-7 come from WP-1 `JournalChainRules`, listing and completeness from WP-2 `ArchiveCompleteness`):

1. Stream head (organization head column and highest row); a disagreement is `CHAIN_INTEGRITY_FAILURE/ORGANIZATION_HEAD_MISMATCH`.
2. **Complete** archive listing of the stream prefix (and the organization prefix where requested); gaps, duplicates, unexpected objects, objects above the AuthorityDb head (`BEHIND_ARCHIVE`); an unavailable listing is Inconclusive with an operational `ARCHIVE_UNAVAILABLE`.
3. Trusted anchor (sweeps): `archive[v]` must hash to the checkpoint digest (`ARCHIVE_REGRESSION`); AuthorityDb below `v` is a gap.
4. AuthorityDb rows in **bounded batches** (default 1,000; chained across batches by the previous batch's last digest; cap 5,000,000 rows per run ⇒ Inconclusive): R-1..R-7; the row at `v` must equal the checkpoint digest (`ANCHOR_MISMATCH`).
5. For archived revisions in range: byte comparison row versus archive (`ANCHOR_MISMATCH`) and archive-side R-1..R-7 linked to the previous archived digest.
6. Rows above the contiguous archive head and absent from the listing: `Anchored` or with a receipt ⇒ second read ⇒ `FAKE_RECEIPT`; `VerificationFailed` ⇒ `DELIVERY_REFUSED` (operational); `Pending` older than the lag threshold ⇒ `DELIVERY_LAG` (operational).
7. Malformed or unsupported envelopes are Inconclusive (contract R-2); a confirmed security finding is never masked by an Inconclusive in the same run (outcome Findings / FAILED, reasons still listed).

## 3. WP-3 — baseline (`JournalBaselineService`, `--journal-baseline`)

| Rule | Implementation |
|---|---|
| BL-1 | Sentinel read; created only with `--create-sentinel` (otherwise Inconclusive `ARCHIVE_SENTINEL_ABSENT`); a non-canonical sentinel is `ARCHIVE_REGRESSION`. Full comparison from revision 1 (rows and every archived object) |
| BL-2 | Read-only on AuthorityDb (port has no write; PostgreSQL snapshot is `READ ONLY`); never repairs, re-delivers or rewrites |
| BL-3..BL-6 | CLEAN; EXCEPTION_ACCEPTED (every security finding covered by an explicit owner disposition with exactly matching organization, stream, code, subtype and revision range); FAILED (any undispositioned security finding, even if something else was undecidable); INCONCLUSIVE. Operational findings are reported but do not decide the outcome |
| BL-7 | Applied exception record: id, class, scope, owner disposition reference, tool version, source commit, operator, time, archive instance, reduced-assurance statement. A `BODY_DIGEST_MISMATCH` on a never-archived row is marked as a **candidate** `LEGACY_UNVERIFIABLE`, never accepted automatically; unused dispositions are listed |
| BL-8 | Deterministic canonical report (`DynamicForms/JournalBaselineReport/v1`; sorted findings; same inputs and clock ⇒ same bytes). The command writes it to a new file (never overwritten) |
| BL-9 | Mandatory statement in every report |
| CK-1, BL-3/BL-4 | Checkpoint 1 (Baseline; Full or Reduced with the exception ids) only with `--create-checkpoint`, only for CLEAN or EXCEPTION_ACCEPTED, only when no checkpoint exists, never when the archive is ahead of AuthorityDb; verified digest = SHA-256 of the **archived** bytes at the archive head |
| BL-10 / OBS-4 | If a chain exists, nothing is written: the report states `NotCreated:REBASELINE_NOT_AUTHORIZED` and proposes continuation (next sequence, walk kind Baseline, link to the head digest). **No re-baseline was performed** |
| Concurrency | The baseline takes the same sweep lock; a concurrent sweep makes it Inconclusive |

## 4. WP-4 — verifier (`JournalVerifier`, `JournalVerificationCoordinator`, `JournalVerificationLoop`)

| Item | Implementation |
|---|---|
| Sweeps | Incremental: checkpoint head + predecessor (CK-5), rows `v..h_db`, archive `v+1..h_a`, stream listing. Full: whole checkpoint chain, rows `1..h_db` anchored at `v`, stream listing (and the organization prefix for the audit stream's full sweep). All three streams |
| Outcomes | Clean ⇒ next checkpoint (Incremental/Full walk kind, assurance and exceptions carried); Findings; Inconclusive (including `NOT_BASELINED`); Skipped |
| Checkpoint continuity | Discovery Invalid ⇒ `CHECKPOINT_CHAIN_INVALID`; a head below the highest this process saw ⇒ `CHECKPOINT_STORE_REGRESSION` (CKR-3: undetectable after a restart, tested and documented); a conflict at the next sequence ⇒ `CHECKPOINT_CHAIN_INVALID/CHECKPOINT_CONFLICT` |
| Sentinel | Absent or different from the checkpoint's archive instance ⇒ `ARCHIVE_REGRESSION/ARCHIVE_IDENTITY_MISMATCH` |
| Staleness | Not Clean and newest checkpoint older than 15 min ⇒ `VERIFICATION_STALE` (operational) |
| Findings | Structured: organization, stream, code, subtype, severity, revision range, run id, UTC time, evidence references (`row:`/`archive-revision:`/`archive:`/`checkpoint:`), detail, run outcome, checkpoint reference. Create-only records keyed by finding identity (a re-observation is not stored again) in the separate checkpoint store (`findings/...`); no body, digest of a body, secret or credential |
| Alerts | Security findings raise one `ISecurityAlertSink` alert per finding identity per process; operational findings are logged only. **Alert-only**: no quarantine, repair, deletion, re-signing, re-delivery or replay (architecture-tested) |
| Non-overlap | In-process guard per (organization, stream) plus the snapshot's PostgreSQL transaction-scoped advisory lock (class `0x5ECB101`) across processes; a held lock ⇒ Skipped |
| Schedule | `JournalVerificationSchedule.Due`: Full first after start (re-verifies history after a restart), then Incremental every 5 min, Full every 24 h (OD-DV-6 defaults). Deterministic (`TimeProvider`) |
| Loop | Authority-hosted `BackgroundService`, tick 30 s, independent of SpiceDB readiness; each tick's failures are logged (9403) and the loop continues; run summaries and findings logged (9400–9402) with identifiers, codes, ranges and counts only |
| Hosting | `JournalVerificationComposition`: registers nothing unless `JournalVerificationCheckpointStore:Provider` is set; **refuses to start** outside Development/Testing; loop only with `JournalVerification:Enabled=true`. Opt-in overlay `compose.journal-verification.yaml` (fails to render without a non-default key; base and security-test renderings unchanged) |
| Identity | `ConnectionStrings:AuthorityDbJournalVerification` if set, else the **Authority runtime credential** (no dedicated read-only login exists; creating one is a G08 change, not authorized). Read-only behaviour rests on the `READ ONLY` transaction, the port and the statement set (statement-trace tested) |

## 5. Tests (Development/Testing; exact results)

| Command | Result |
|---|---|
| `AuthorizationAuthority.Tests.exe --filter-class` `JournalBaselineServiceTests`, `JournalVerifierTests` | **35/35 passed** (first run 32/35: one test defect, a byte-array tuple comparison; two product defects fixed, §8) |
| `powershell -File scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -EvidenceDirectory docs/evidence/sec-bl-01-wp3-wp4/run-1` | **8/8 passed**, exit 0, all containers and anonymous volumes removed. Real migrations and runtime grants on postgres:18.0; verifier as the runtime role |
| `SECBL01_OBS1_CHARACTERIZE=1 ... --filter-class ArchiveDeliveryOutageCharacterizationTests` | 2/2 passed (characterization of the existing defect, §7) |
| Architecture `JournalVerificationBoundaryTests` | 8/8 |
| Full Authority suite (rebuilt `--no-incremental`) | **1383 total, 1359 passed, 0 failed, 24 skipped** (+45 tests; the 10 new skips are the PostgreSQL and opt-in OBS-1 classes) |
| Full architecture suite | **222/222** |
| Full contract suite | **954 total, 942 passed, 0 failed, 12 skipped** (unchanged; the composition registers nothing in these hosts) |
| Builds | 0 errors, 0 warnings |
| Not run | Stage B; security-test stack; WP-7 mutation campaign; second run of record (WP-7) |

Owner test list coverage: clean baseline; legacy exception (failed without, accepted with exact disposition, unused disposition); failed and inconclusive baselines; missing archive objects and discontinuity; recomputed digests; consistent chain rewrite (anchor); rewrite-and-extend W-1 (anchor and archive-side link); forged Anchored rows (in-process and PostgreSQL); archive and checkpoint outages; checkpoint rollback within process and its CKR-3 limit after restart; corrupt checkpoint chain; database restore and tail truncation (in-process and PostgreSQL); organization head mismatch; cross-organization and cross-stream isolation; restart during a sweep (cancelled mid-sweep, no checkpoint, rerun completes); cancellation; concurrent sweep prevention (in-process, cross-instance, PostgreSQL advisory lock); no AuthorityDb writes (in-memory fingerprint; PostgreSQL md5 of rows and organization row; server-side `25006` refusal of a write in the snapshot; SELECT-only statement trace); no quarantine changes; no request-path change (contract suite unchanged; no request-path file touched).

## 6. Performance (one disposable run; Development/Testing; not WP-7 acceptance)

Synthetic organization, 20,000 audit rows all archived, then 500 more; Azurite on Windows Docker Desktop, sequential archive reads (`docs/evidence/sec-bl-01-wp3-wp4/run-1/measurement.json`):

| Run | Duration | AuthorityDb rows | Archive objects listed | Archive objects read |
|---|---|---|---|---|
| Baseline | 451 s | 20,000 (20 batches) | 40,000 (stream + organization prefix) | 20,000 |
| Incremental (500 new rows) | 15.7 s | 501 | 20,500 | 501 |
| Full | 6.2 s | 20,500 | 41,000 | 1 |

Managed allocation across the three runs 1.38 GB; peak working set of the test process 281 MB (bounded: rows are read in batches, not loaded whole). Seeding including 20,000 archive uploads took 497 s.

Observations: the baseline is dominated by one archive read per object (~22 ms each here); the **incremental sweep lists the whole stream prefix** every time (20,500 keys for 501 new rows), so its cost grows with history (§9, R-W4-2). Detection latency is bounded by the schedule plus these durations (contract §8.6); not measured end to end. **No mature-organization performance claim** (WP-7).

## 7. Observations

| ID | Disposition in this increment |
|---|---|
| **OBS-1** | **Reproduced by executed test [T]**, delivery code unchanged. Against an unreachable archive the pre-existing `ReadAsync` and `AppendAsync` let `AggregateException` escape at an 8 s timeout (read only at 3 s; not at 1, 2 and 5 s, the default being 5 s: timing-dependent). `ArchiveProcessor.ProcessOrganizationAsync` then throws `AggregateException`; the entry stays `Pending`; no quarantine (fail closed). `AuthorityWorkLoop`'s catch list does not include `AggregateException`, so the background service would fault **[I, source]**. Characterization tests are opt-in (`SECBL01_OBS1_CHARACTERIZE=1`). Recommended: a separately authorized delivery-path fix |
| **OBS-2** | Documentation-only clarification in `check-journal-store-coupling.py`: creation-time comparison only, no content check |
| **OBS-3** | Unchanged (CI not modified) |
| **OBS-4** | Re-baseline proposed (chain continuation), not performed; owner decision open |
| **OBS-5** | Unchanged |

## 8. Defects found and fixed during this increment (in new code)

1. A sweep with a confirmed security finding and an Inconclusive reason reported Inconclusive: detection was masked. Now Findings (verifier) and FAILED (baseline) take precedence, reasons still listed.
2. Rows archived beyond a gap were treated as fake-receipt candidates and made the run Inconclusive. Now only rows with no listed archive object are candidates; the gap itself is `ARCHIVE_DISCONTINUITY`.

## 9. Contract compliance and residual risks

| Contract | Status |
|---|---|
| §8 VF-1..VF-14 | Implemented. VF-10 (delivery state never evidence), VF-11 (checkpoint only after Clean), VF-14 (alert-only) tested |
| §9 findings | All §9.1/§9.2 codes emitted; additions `DELIVERY_REFUSED` (operational) and subtype `ORGANIZATION_HEAD_MISMATCH` |
| §10 BL-1..BL-10 | Implemented; BL-10 re-baseline only proposed (OBS-4) |
| §7 CK-1..CK-5, CK-7..CK-11 | Used; CK-6 (ledger) not implemented (WP-8); CK-12 identity separation not met in Development/Testing |
| §8.5 hosting | Authority-hosted, Development/Testing only, refused elsewhere |
| §5 D+, §11 M-6, §13 WP-7, §14 | Not implemented |

| ID | Residual risk |
|---|---|
| R-W4-1 | Runtime credential (J1) is the verifier's database identity; read-only rests on the transaction and code, which a compromised Authority process could bypass. Dedicated SELECT-only login needs G08 authorization |
| R-W4-2 | Incremental sweeps list the whole stream prefix (cost grows with history); prefix-narrowed listing is a WP-7 optimization question |
| R-W4-3 | Findings within an accepted baseline-exception scope are re-reported by later sweeps and block checkpoint advance (fail-safe, never concealed); how sweeps should treat accepted exception scopes is an **open owner decision** (the contract keeps only exception ids in checkpoints) |
| R-W4-4 | A checkpoint is created on every Clean sweep (VF-11): about 288 per stream per day at the defaults; growth and retention (OI-DR-4) are open |
| R-W4-5 | `DELIVERY_LAG` and other operational findings block checkpoint advance (strict VF-11); an archive outage therefore leads to staleness alerts, by design |
| R-W4-6 | Process-local alert de-duplication: a restart re-alerts open findings once |
| R-W4-7 | Everything in Development/Testing: no independence (J4/J5/J6), CKR-3, no production identities, single run (not WP-7's two runs of record) |
| — | Unchanged: undelivered-tail forgery (R-DR-1), recorder suppression window (R-DR-2), pre-baseline history (R-DR-4) |

## 10. Open owner decisions

OBS-4 re-baseline interpretation; R-W4-3 treatment of accepted exception scopes in sweeps; OI-DR-2 dedicated read-only login (G08); OBS-1 delivery-path fix authorization; R-W4-4 checkpoint growth and retention; R-W4-2 incremental listing cost (WP-7); finding presentation beyond logs and create-only records (OI-DR-3).

**Dated note (2026-11-17, WP-4A):** the owner accepted WP-3/WP-4 as the Development/Testing implementation baseline and decided OBS-1, OBS-4, R-W4-2, R-W4-3 and R-W4-4 in the WP-4A package (dispositions A–F). Their implementation, the contract amendment A-1 and the new results are in `SEC_BL_01_WP4A_IMPLEMENTATION_RECORD.md`. Changes since this record: `REBASELINE_NOT_AUTHORIZED` is replaced by `REBASELINE_NOT_REQUESTED`; the report schema is v2; Blob `AppendAsync`/`ReadAsync` changed for OBS-1 only. The text and results above are preserved as executed.

## 11. Not claimed

No D+, no G08 change, no migration, no deployment, no CI change, no G14 recovery, no re-baseline, no baseline on persistent organization data; no closure or re-evaluation of SEC-BL-01, PD-15, M2F3-43 or R-FU-1; no production assurance; no independent review.
