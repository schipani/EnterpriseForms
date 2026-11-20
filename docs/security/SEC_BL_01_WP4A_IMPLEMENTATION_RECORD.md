# SEC-BL-01 WP-4A — Implementation Record: Stabilization (accepted exceptions, re-baseline continuity, bounded incremental verification, OBS-1)

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | WP-4A stabilization of the WP-3 baseline and the WP-4 verifier (`SEC_BL_01_DR_CONTRACT_CLOSURE.md` amendment **A-1**, §21) |
| Authorization | Owner decision package "WP-4A Stabilization", 2026-11-17 (dispositions A–F, §2), Development/Testing and synthetic data only. **Not authorized and not done:** WP-5 D+, WP-6 G08 hardening, WP-7, WP-8, G14 recovery, database role or grant changes, CI changes, production resources, deployment, commit, push, merge, Stage B, closure of SEC-BL-01/PD-15/M2F3-43, changes to persistent organization data or existing volumes |
| Baseline | `main` at `a9b32de`; all uncommitted WP-0..WP-4 work preserved. The accepted contract's §1–§20 are byte-identical to the accepted WP-0 text. The SHA-256 of the file with the A-1 status row and §21 removed is `4a8bd1b1…ef12e`, reproduced |
| Result | Implemented and tested in Development/Testing. Owner acceptance pending. **Not committed** |
| SEC-BL-01 / PD-15 / M2F3-43 | **OPEN / OPEN / FAILED (not re-evaluated)** |
| Review | Owner-led; prepared by the implementing assistant. **Not an independent security review** |

---

## 1. Files

**Application (Authority)**

| File | Change |
|---|---|
| `Common/Ports/IJournalAcceptedExceptionStore.cs` | New (A): create-only record port; documents the J5/J6 limitation |
| `Common/Ports/ISecurityEventArchiveInventory.cs` | `ListEntriesAboveAsync` added (C) |
| `Features/Journal/Verification/ArchiveCompleteness.cs` | `CollectAboveAsync` added (C) |
| `Features/Journal/Verification/JournalVerificationCheckpoints.cs` | `CheckSuccessor`: a `Baseline` successor keeps the archive instance, never drops an exception and never upgrades `Reduced` (B, RB-3) |
| `Features/Journal/Reconciliation/JournalAcceptedExceptions.cs` | New (A): record, canonical codec, evidence digest, re-verification, exact coverage |
| `Features/Journal/Reconciliation/JournalVerificationFindings.cs` | Two security codes and `DISPOSITION_INVALID` (A) |
| `Features/Journal/Reconciliation/JournalStreamComparer.cs` | Bounded listing, contiguity across accepted gaps only, segment-wise archive chain (A, C) |
| `Features/Journal/Reconciliation/JournalBaselineService.cs` | Rewritten for records, dispositions with timestamps, re-baseline continuation, report v2 (A, B) |
| `Features/Journal/Reconciliation/JournalVerifier.cs` | Record re-verification, accepted observations, bounded incremental, last-complete tracking (A, C) |

**Infrastructure and host (Authority)**

| File | Change |
|---|---|
| `Infrastructure/Archive/BlobSecurityEventArchive.cs` | OBS-1: `AggregateException` and read-back timeout classification in `AppendAsync`/`ReadAsync` only (D). New inventory method `ListEntriesAboveAsync` for Blob, in-memory and unavailable (C; no delivery path uses it, which the architecture tests confirm) |
| `Infrastructure/Archive/VerificationCheckpointStores.cs` | Blob and in-memory accepted-exception stores (A) |
| `Api/Composition/JournalVerificationComposition.cs` | Registers the exception store (Development/Testing composition only) |
| `Api/Composition/JournalBaselineCommand.cs` | `--rebaseline` |

**Tests, script, evidence and documentation**

| Area | Change |
|---|---|
| New tests | `tests/authority/.../JournalAcceptedExceptionTests.cs`, `JournalBoundedListingAzuriteTests.cs` |
| Modified tests | `ArchiveDeliveryOutageCharacterizationTests.cs` (rewritten as OBS-1 regression), `JournalVerificationWorld.cs`, `JournalBaselineServiceTests.cs`, `VerificationCheckpointTests.cs`, `JournalVerificationPostgresIntegrationTests.cs`, `JournalVerificationAzuriteIntegrationTests.cs` (helper only); `tests/architecture/.../JournalVerificationBoundaryTests.cs` (one rule extended, one added) |
| Gate | `scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1`: adds the bounded-listing class, a checkpoint-count parameter, and an absolute evidence path (§8) |
| Evidence | `docs/evidence/sec-bl-01-wp4a/` (`run-1`, `run-2`, `obs1`, `sec-p`, `regression`; path-scrubbed, no secrets) |
| Documentation | Contract amendment A-1 (§21 plus one status row); this record; a progress row in `SECURITY_BACKLOG.md`; a dated note in `SEC_BL_01_WP3_WP4_IMPLEMENTATION_RECORD.md`; `SHA256SUMS.txt` and `spec-files.json` regenerated |

## 2. Owner dispositions (package of 2026-11-17) and status

| # | Disposition | Status | Remaining limitation |
|---|---|---|---|
| A | Accepted-exception records: create-only, exact-match reclassification by code, scope, provenance and evidence digest; checkpoint advances past unchanged ones while staying Reduced; no silent upgrade and no automatic acceptance; Development/Testing store limitation documented | **Implemented** (A-1 §21.1; tests §3) | The Development/Testing store is not protected against J5/J6 (AX-9); exception scopes are at most 10,000 revisions |
| B | Re-baseline continues the chain (OBS-4); never restarts, overwrites, discards, regresses or upgrades; keeps links; rejects conflicting or inconsistent attempts | **Implemented** (A-1 §21.2; tests §4) | A chain that is invalid, regressed or lost cannot be continued; how verification is re-established is open (OI-DR-9). CKR-3 is unchanged |
| C | Bounded incremental verification: anchor every sweep; complete enumeration above `v`; mismatch detection in the new range; 24 h full sweep; last complete verification tracked separately; latency trade-off documented; no unbounded memory | **Implemented** (A-1 §21.3; tests §4.2; measurement §5) | Changes below `v` are detected only by the full sweep (IV-6). Full-sweep memory stays proportional to history (IV-8). Checkpoint discovery still lists the checkpoint prefix (§7) |
| D | OBS-1: correct the `AggregateException` handling in `AppendAsync`/`ReadAsync` only; preserve ordering, retry, idempotency, cancellation, no false success, no quarantine, unchanged digests; focused tests | **Implemented** (A-1 §21.4; tests §6) | The real-endpoint reproduction is timing-dependent and opt-in |
| E | SEC-P01..SEC-P12 on the disposable stack with synthetic data; Authority, architecture and contract suites | **Executed: all pass** (§6, §7) | Development/Testing only; one run |
| F | Measure checkpoint rate and growth; propose a policy; no deletion, compaction or retention change | **Measured and proposed** (§7) | Policy not decided (OI-DR-10); nothing deleted or compacted |

## 3. Accepted-exception behaviour and tests

**Behaviour.** The baseline accepts a finding only through an explicit owner disposition. The disposition must carry an identifier, an owner reference and a disposition timestamp; its scope must match exactly; and a malformed one makes the run INCONCLUSIVE with `DISPOSITION_INVALID`. Before writing the checkpoint, the baseline writes one create-only record per applied disposition at `exceptions/{org}/{stream}/{id}.json`, holding the evidence digest of the accepted range. Every later sweep and re-baseline re-verifies each record the head names:

- form and scope;
- provenance (archive instance, and a baseline sequence at or below the head);
- the evidence digest, recomputed.

A covered finding becomes an *accepted observation*; anything else stays a finding. An accepted `ARCHIVE_DISCONTINUITY` is the only thing that lets the contiguous head cross that exact gap. The chain stays `Reduced` and keeps the identifiers.

**Tests (all passed).** `JournalAcceptedExceptionTests`, in memory:

| Test | What it proves |
|---|---|
| Accepted legacy gap | Record fields and provenance; create-only (conflict and idempotent retry); checkpoint crosses the gap, Reduced |
| Later sweeps | Incremental and full sweeps are Clean with one accepted observation; the head stays Reduced and keeps the ids; no alert |
| Changed evidence | An archive object placed into the gap, or an AuthorityDb row edited, gives `ACCEPTED_EXCEPTION_EVIDENCE_CHANGED`, no checkpoint, a Critical alert |
| Missing or foreign record | Deleted record, record naming another archive instance, record with a widened scope ⇒ invalid or evidence changed; the gap is reported again |
| Additional finding | A new gap above the checkpoint is a finding (bounded sweep) |
| No disposition | FAILED; no record; no checkpoint |
| Malformed disposition (six cases) | Missing time, future time, duplicate id, invalid id, oversized scope, missing reference ⇒ INCONCLUSIVE, nothing written |
| Conflicting record | A different record under the same id ⇒ `EXCEPTION_RECORD_CONFLICT`, no checkpoint |
| Codec | Canonical; strict; always Reduced |

On PostgreSQL 18 + Azurite, `AnAcceptedGapAndARebaselineWorkOnRealStoresWithoutWrites` runs as the runtime role with the record in Blob: accepted gap, bounded incremental, full sweep with the observation, re-baseline to checkpoint 4. The md5 of the AuthorityDb rows is unchanged. The architecture rule `OnlyTheBaselineCreatesAcceptedExceptionRecordsTheVerifierOnlyReadsThem` also holds: the verifier never creates records, and the port has no delete or replace operation.

## 4. Re-baseline behaviour and tests

### 4.1 Re-baseline

**Behaviour.** A re-baseline requires `--rebaseline` and `--create-checkpoint`. It runs the complete baseline comparison and writes checkpoint `head + 1`, walk kind `Baseline`, linked to the head digest. It carries every exception, re-verified, and may only add new ones. Without the flag, the run only proposes the continuation (`REBASELINE_NOT_REQUESTED`). The following are rejected, and in every case nothing is written:

- `REBASELINE_ARCHIVE_INSTANCE_CHANGED`;
- `REBASELINE_REVISION_REGRESSION`;
- `REBASELINE_CONFLICT`;
- a carried record failing re-verification (FAILED).

The chain rule `CheckSuccessor` enforces the same constraints for every reader.

**Tests (all passed).**

| Test | What it proves |
|---|---|
| Continuation | Sequence 2 is linked to checkpoint 1; full-chain discovery is valid; checkpoint 1 is untouched |
| Carries, never upgrades, may add | Reduced is carried with `BX-GAP-0003`; a later re-baseline adds `BX-GAP-0010` (ids are only ever added) |
| Regression rejected | The archive tail disappears ⇒ `REBASELINE_REVISION_REGRESSION`, one checkpoint |
| Other archive instance | `REBASELINE_ARCHIVE_INSTANCE_CHANGED` |
| Concurrent next checkpoint | `REBASELINE_CONFLICT`; the other writer's bytes stay |
| Changed carried record | FAILED, nothing written |
| Proposal only | `JournalBaselineServiceTests`: an existing chain without the flag is only proposed |
| Chain rules | `VerificationCheckpointTests`: a `Baseline` successor may add exceptions, but switching instance, dropping an id, or `Reduced` → `Full` is refused |

### 4.2 Bounded incremental

| Test | What it proves |
|---|---|
| Listing only above `v` | 3 objects listed (unbounded: 46) and the anchor still read |
| Deletion below `v` | Invisible to the bounded incremental sweep (documented trade-off), detected by the full sweep |
| Above `v` | A changed anchor gives `ARCHIVE_REGRESSION`; a row/archive mismatch gives `ANCHOR_MISMATCH` |
| Last complete verification | Tracked separately: the baseline time after an incremental; null after a restart with an Incremental head; set by a Clean full sweep |
| Decimal prefixes | Cover exactly the revisions above `v` (11 boundary values, at most 109 prefixes) |
| Azurite paging | Exact, ordered, complete across pages and prefix boundaries for seven (after, page size) combinations; another stream is excluded; a non-canonical name inside a listed prefix is reported; a missing container is `ContainerAbsent` |
| Azurite store | The Blob exception store is create-only |

## 5. Incremental verification: before and after

**Setup.** One disposable run of record (`docs/evidence/sec-bl-01-wp4a/run-1/measurement.json`), with the same setup as the WP-3/WP-4 run:

- synthetic organization of 20,000 archived audit rows;
- postgres:18.0 and two Azurite 3.9.0 instances on Windows Docker Desktop;
- sequential archive reads.

Each incremental sweep follows an increment of 500 newly delivered rows, on the same organization. The "WP-4 behaviour" column is measured in the same run with `BoundedIncrementalListing = false`.

| Sweep | Duration | Rows read | Archive objects listed | Archive objects read |
|---|---|---|---|---|
| **Before**: WP-3/WP-4 run of record (20,000 → 20,500) | 15.7 s | 501 | 20,500 | 501 |
| WP-4 behaviour in this run (20,500 → 21,000) | 14.8 s | 501 | 21,000 | 501 |
| **After**: bounded A (20,000 → 20,500) | 12.7 s | 501 | **500** | 501 |
| **After**: bounded B (21,000 → 21,500) | 12.9 s | 501 | **500** | 501 |
| Full sweep (unchanged, 21,500) | 5.6 s | 21,500 | 43,000 | 1 |
| Baseline (unchanged) | 460.7 s | 20,000 | 40,000 | 20,000 |

**Reading.** Listing cost no longer grows with history: 500 names instead of 21,000. Bounded A and bounded B are equal although the history grew, while the unbounded listing grew from 20,500 to 21,000. The wall-clock gain at 20,000 rows is modest (about 2 s, 14 %), because the sweep is dominated by the 501 sequential archive reads of the newly delivered entries, which bounding does not change.

**Resources.** Peak working set 262 MB (before: 281 MB). Managed allocation across all runs 1.52 GB. Seeding took 512 s.

**Run-2.** A functional rerun with 2,000 rows (`run-2/`) passed 19/19; its timings are not a performance measurement.

**Not claimed.** No mature-organization performance claim; this is not WP-7 acceptance. The latency trade-off is in A-1 IV-6: changes below `v` can stay undetected for up to `T_full` (24 h) plus the full-sweep duration.

## 6. OBS-1 correction and regression

**Correction.** Blob `AppendAsync` and `ReadAsync` now map an `AggregateException` (exhausted SDK retries with mixed failures) to `ArchiveUnavailableException`, unless the caller cancelled. `AppendAsync`'s read-back after 409/412 maps its `AggregateException` and its timeout the same way. Before the fix, a read-back timeout escaped as `OperationCanceledException`. Names, conditions, metadata, receipts, digests and canonical bytes are unchanged; there is no retry, re-ordering or quarantine in the adapter.

**Regression tests.** `ArchiveDeliveryOutageCharacterizationTests`:

| Test | Result |
|---|---|
| Real unreachable endpoint (`127.0.0.1:9`), 3 s and 8 s timeouts: both methods throw `ArchiveUnavailableException` (the timeouts where `AggregateException` escaped before, WP-3/WP-4 record §7) | Passed |
| `ArchiveProcessor` returns 0, the entry stays `Pending` with an error code, no quarantine | Passed |
| Deterministic recovery: during an outage nothing is delivered or claimed; when the archive returns, the next pass delivers both pending entries **in ascending revision order** and anchors them; no quarantine | Passed |

**Commands and results.**

| Command | Result |
|---|---|
| `SECBL01_OBS1_CHARACTERIZE=1 AuthorizationAuthority.Tests.exe --filter-class AuthorizationAuthority.Tests.ArchiveDeliveryOutageCharacterizationTests` | **4/4 passed** (`docs/evidence/sec-bl-01-wp4a/obs1/console.txt`) |
| Without the opt-in variable | 1 passed, 3 skipped |

The deterministic test first failed because its expected revisions were hard-coded; the evidence world pre-seeds 40 audit revisions. The test was corrected to compare against the journal's own ascending revisions; the product was not changed.

## 7. Checkpoint growth (measured; policy proposed, not implemented)

**Rate.** Every Clean sweep creates one checkpoint (VF-11). At the defaults that is up to 288 incremental plus 1 full checkpoint per (organization, stream) per day, about 105,500 per stream per year.

**Measured** (`run-1/checkpoint-growth.json`, a 2,016-checkpoint chain, one week at the defaults, Reduced with one exception id, Blob store):

| Measure | Value |
|---|---|
| Mean checkpoint size | 620 bytes (maximum 621) |
| Storage for one week, one stream | 1.25 MB |
| Head-and-predecessor discovery (every sweep) | 255 ms; lists all 2,016 objects |
| Full-chain discovery (every full sweep) | 10.6 s; reads all 2,016 |

**Projection** (linear in the number of checkpoints, from the measurement):

| Horizon | Per stream | Per organization (3 streams) | 25 organizations (PB-09 target) | Head discovery | Full-chain discovery |
|---|---|---|---|---|---|
| 1 week | 1.25 MB | 3.7 MB | 94 MB | 0.26 s | 10.6 s |
| 1 year | 65 MB | 196 MB | 4.9 GB | ~13 s | ~9.3 min |

Storage is small. **Discovery cost** is the growth problem: listing the checkpoint prefix on every sweep reintroduces, for checkpoints, the history-proportional cost that IV-1 removed for the archive.

**Proposal for a future owner decision (OI-DR-10).** Nothing here is implemented, and nothing is deleted or compacted.

1. **Frequency:** create a checkpoint when the verified revision advances. Create an unchanged-revision checkpoint at most once per staleness window (for example hourly), so that `VERIFICATION_STALE` keeps working. This reduces idle streams from 288 to at most 24 per day.
2. **Discovery:** use CK-4's exponential/binary search over sequence numbers for incremental discovery (O(log n) reads), keeping the complete listing and full-chain walk for the full sweep. Before adopting it, this needs a CKR analysis: probing cannot see sequence gaps, which the full sweep must then catch.
3. **Full-chain cost:** verify the full chain in segments, from a segment-anchor checkpoint whose digest is independently anchored (production: Confidential Ledger, CK-6). Without an independent anchor this weakens CKR-1 detection, so it is **not** proposed for Development/Testing.
4. **Retention:** none in Development/Testing beyond the RST rules. In production, immutable time-based retention aligned with the archive's retention and legal hold (OI-DR-4, PB-10); deletion only of checkpoints older than both the retention period and the newest ledger-anchored segment anchor.

## 8. Security, regression and gate results

| Command (Release unless noted) | Result | Evidence |
|---|---|---|
| `AuthorizationAuthority.Tests.exe --filter-class` the seven journal-verification classes | 161 total, 157 passed, 1 failed, 3 skipped on the first run. The failure was the OBS-1 test defect (§6). After the fix, every one of these tests passes or skips as designed in the full Authority suite below (0 failed) | — |
| `powershell -File scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -EvidenceDirectory docs/evidence/sec-bl-01-wp4a/run-1` (Debug test build; 20,000 rows; 2,016 checkpoints) | 19 total, **18 passed, 1 failed**, exit 2; containers and anonymous volumes removed. The failure was a test-only assertion (a tuple holding a `byte[]` compared by reference) in `TheBlobAcceptedExceptionStoreIsCreateOnly`; all measurements passed. The measurement JSON was written relative to the test directory and moved into `run-1/`; the script now resolves the path | `run-1/` |
| Same gate, `-PerformanceRows 2000 -CheckpointCount 288 -EvidenceDirectory docs/evidence/sec-bl-01-wp4a/run-2`, after the fixes | **19/19 passed**, exit 0; containers removed | `run-2/` |
| `docker compose -p enterpriseforms-sectest -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait business-db authority-db spicedb vault vault-transit-init archive-store azurite authority-migrate authority business-migrate api attacker-sql` | All services healthy (fresh project; no prior containers or volumes) | `sec-p/sectest-up.txt` |
| `dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj --no-build -c Release -- --minimum-expected-tests 13` | **13 total, 13 passed, 0 failed, 0 skipped**: SEC-P01..SEC-P12 including **SEC-P12** (archive outage keeps commands non-effective and content unreleased), plus the fixture fingerprint test | `sec-p/secp.txt`, `sec-p/test-list.txt` |
| `docker compose -p enterpriseforms-sectest ... down -v --remove-orphans` | Removed; no containers or volumes of the project remain. No other project touched | — |
| Authority suite (`dotnet build --no-incremental`; `dotnet test --project tests/authority/AuthorizationAuthority.Tests`) | **1421 total, 1385 passed, 0 failed, 36 skipped**; 0 warnings, 0 errors | `regression/authority.txt` |
| Architecture suite (same method) | **223/223 passed** | `regression/architecture.txt` |
| Contract suite (same method) | **954 total, 942 passed, 0 failed, 12 skipped** (unchanged) | `regression/contract.txt` |

**Delta against WP-3/WP-4.** The Authority suite was 1383 total, 1359 passed, 24 skipped. WP-4A adds 38 tests:

- 26 run in the default suite: 25 in `JournalAcceptedExceptionTests` and the deterministic OBS-1 recovery test;
- 12 skip there because they need the disposable stores: 9 Azurite bounded-listing tests, 2 PostgreSQL tests, and 1 more opt-in OBS-1 test.

Architecture: 222 → 223. **Not run:** Stage B, the WP-7 mutation campaign, a second run of record.

## 9. Remaining risks and open decisions

| ID | Risk | Status |
|---|---|---|
| R-W4A-1 | Changes below the checkpoint are detected only by the full sweep: up to 24 h plus its duration (IV-6) | Accepted by disposition C; documented |
| R-W4A-2 | Checkpoint discovery lists the whole checkpoint prefix on every sweep; cost grows linearly (§7) | Open; OI-DR-10 |
| R-W4A-3 | Each accepted exception is re-verified on every sweep (up to 10,000 rows and archive reads per record) | Bounded per record; the number of exceptions is owner-controlled |
| R-W4A-4 | Accepted-exception records, checkpoints and findings in Development/Testing are not protected against J5/J6 or anyone holding the store key (AX-9); CKR-3 unchanged | Accepted Development/Testing limitation |
| R-W4A-5 | Re-establishing verification after an invalid or lost chain is undefined (RB-6) | Open; OI-DR-9 |
| R-W4A-6 | The last complete verification is process-local; after a restart it is unknown until the first full sweep (the schedule runs a full sweep first) | Accepted |
| R-W4A-7 | Full-sweep memory is proportional to history (listing held in memory), unchanged from WP-4 | Open (WP-7 scale question) |
| R-W4A-8 | The in-memory archive test double omits non-canonical names from the bounded listing; Blob reports them (Azurite-tested) | Test-double difference; recorded |
| — | Unchanged: R-W4-1 (runtime credential), R-W4-5 (operational findings block advance), R-W4-6 (process-local alert de-duplication), R-W4-7 (no independence), R-DR-1/R-DR-2/R-DR-4 | Unchanged |

**Open owner decisions:**

- OI-DR-9: re-establishing verification after an invalid or lost chain.
- OI-DR-10: checkpoint frequency, discovery and retention (§7 proposal).
- OI-DR-11: sampling below `v`.
- OI-DR-2: dedicated read-only login (G08).
- OI-DR-3: finding presentation.
- WP-5a authorization.

**Dated note (2026-11-17, WP-5a):** the owner accepted WP-4A and amendment A-1 in the WP-5a package and adopted dispositions for OI-DR-9, OI-DR-10 (implementation deferred), OI-DR-11 and OI-DR-2 (amendment A-2 §22.5). WP-5a is recorded in `SEC_BL_01_WP5A_IMPLEMENTATION_RECORD.md`. The text and results above are preserved as executed.

## 10. Not claimed

- No D+ (WP-5), M-6 (WP-6), WP-7, WP-8 or G14 work; no role, grant, migration, CI or deployment change.
- No baseline on persistent organization data; no persistent volume touched.
- No production protection of records or checkpoints, and no checkpoint freshness in Development/Testing.
- No full-history guarantee from an incremental sweep.
- No closure or re-evaluation of SEC-BL-01, PD-15, M2F3-43 or R-FU-1.
- No independent review.
