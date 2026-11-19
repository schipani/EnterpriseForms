# SEC-BL-01 WP-5b — Implementation Record: P2, Authorization/Content D+ and Controlled Recovery

**Status (2026-11-18):**

| Item | State |
|---|---|
| Increment | WP-5b, phase **P2** (contract amendment **A-6**, §26): D+ on the content stream, then on the authorization stream; per-stream delivery batches; SR-A/SR-C1 recovery on both streams; SC-5 readiness; the verifier's commitment-versus-archive comparison (EP-5); performance measurement. Development/Testing only |
| Authorization | Owner decisions **C-156** (G14-R2 accepted; OD-R2-1, OD-R2-2; §24.10 retained) and **C-157** (P2-D1..P2-D5 with conditions), both 2026-11-18 |
| Owner acceptance | **Accepted for Development/Testing (C-158, 2026-11-18).** CF-1 closed for Development/Testing within its delivery-integrity scope. E-6 and E-7 remain open (remediation authorized as G08/E-6/E-7; implemented, owner acceptance pending: `SEC_BL_01_G08_E6_E7_IMPLEMENTATION_RECORD.md`, contract A-7 §27) |
| Not done | SR-B, SR-D, SR-E; broader G14; G08 (WP-6); formal WP-7 acceptance; WP-8; any persistent environment, persistent baseline or production infrastructure; CI workflow changes; Stage B; commit, push, merge or deployment |
| Baseline | `main` at `a9b32de`, with every uncommitted WP-0..WP-5a.2, G14-R1/R2 and C-156 change preserved. Contract §1–§20 unchanged (prefix hash method of §A below) |
| SEC-BL-01 / PD-15 / M2F3-43 / DD-10 / CF-1 | OPEN / OPEN / FAILED (pending WP-7) / qualified Development/Testing resolution, production open (C-156) / **closed for Development/Testing (C-158)** |
| Review | Owner-led; prepared by the implementing assistant; **not an independent review** |

Evidence tags: **[T]** executed test evidence in `docs/evidence/sec-bl-01-wp5b/`; **[S]** source inspection; **[I]** inferred. Source inspection is not test evidence.

---

## 1. Owner decisions and contract amendment

- **C-156:** G14-R2 accepted for Development/Testing; OD-R2-1 confirmed (fail closed when durability infrastructure is absent); OD-R2-2: DD-10 resolved within the tested Development/Testing single-store threat model, open for production; §24.10 defaults retained without loosening. Recorded as contract amendment **A-5** (§25, dispositions only).
- **C-157:** P2-D1 (RD-1..RD-10 is OI-DR-1), P2-D2 (A-6: SR-A/SR-C1 on authorization/content; SR-OD-14 reversed for those streams only), P2-D3 (SC-1..SC-7 with the SC-2/SC-5/SC-6 conditions), P2-D4 (content first), P2-D5 (EP-5; audit-only baseline first).
- **A-6 (§26):** rules P2-1/2, EN-1..EN-4, RX-1..RX-4, SC-R1..SC-R4, CE-1..CE-3, VC-1..VC-11, known limits §26.7. §1–§25 unchanged.

## 2. P2 gate results (summary)

| Gate | Result |
|---|---|
| Audit-only baseline measured before any stream was added (P2-D5) | **Done** [T] (`measurement-1-audit-only/`) |
| Content: RD-1..RD-10 | **Passed** [T] (§3, §5). One residual characterized, not closed: E-7 (§10) |
| Authorization: RD-1..RD-10, started only after the content gate | **Passed** [T] (§4, §5). One residual characterized, not closed: E-6 (§10) |

## 3. Content stage (P2-D4 step 1)

**Implementation:**
- `AuditDeliveryEnforcement.EnforcedStreams` = `audit`, `content` (fixed in code).
- `ArchiveProcessor`: every stream in its own bounded batch (EN-3); the not-yet-enabled authorization stream kept its pre-WP-5a delivery for the duration of this stage.
- Recovery: ST-4 refuses streams that are not D+-enabled (RX-1); the command line takes `--stream` (RX-4).
- Readiness (CE-1): `DPlusReadiness.Check` reports `DPLUS_DURABILITY_NOT_COMPOSED`.
- EP-5 verifier comparison (§7).

**Verification at the content stage (runs of record):**

| Layer | Result |
|---|---|
| D+ enforcement suite and G14-R2 recovery suite, audit and content runs, phase and processor tests | **193/193** [T] |
| Content release through the real `ContentService` (`ContentStreamDPlusReadabilityTests`) | **3/3** [T] |
| EP-5 verifier tests (all three streams) with the verifier and baseline suites | **164/164** [T] |
| PostgreSQL 18 + Azurite, WP-5b content methods | Attempt 1: 3/4, one failure (test helper did not advance the organization's content head column; baseline correctly reported `ORGANIZATION_HEAD_MISMATCH`) — preserved in `pg-content-stage/`. Run 2: **4/4** [T] (`pg-content-stage-run2/`) |
| Full Authority suite | 1,669 total, **1,607 passed, 62 skipped, 0 failed** [T] (`authority-content-stage.txt`) |
| SEC-P01..SEC-P12, fresh security-test stack built from the content-stage sources | **13/13** [T] (`sec-p-content-stage/`). The post-run journal-state query failed (no password for psql) and the stack was then removed, so this run's per-stream delivery states are **not captured** |
| Measurement with content enabled | Recorded [T] (`measurement-2-audit-content/`) |

## 4. Authorization stage (P2-D4 step 2)

**Implementation:**
- `EnforcedStreams` = `audit`, `authorization`, `content` (EN-1).
- The pre-WP-5a delivery path is **removed** (EN-2): `archive.AppendAsync` occurs once in `ArchiveProcessor`, inside the D+ path (architecture test). A row of an unknown stream name is never delivered.
- Recovery applies to authorization with the same records, approvals, generations, pinning and W2-S (RX-3).

**Verification at the authorization stage:**

| Layer | Result |
|---|---|
| All WP-5b in-process suites: enforcement ×3 streams, recovery ×3 streams, phase, EP-5, readiness, authorization gate, content release, processor | **310/310** [T] (a first run had 2 failures, both test-assertion errors: an archive placeholder that does implement the inventory port, and an assertion that wrongly forbade the other organization's own commitments) |
| PostgreSQL 18 + Azurite full gate (20,000 rows; WP-4/WP-4A/WP-5a/G14-R2 tests and the 9 WP-5b methods) | 46 total, **45 passed, 1 skipped (the opt-in measurement), 0 failed** [T] (`pg-azurite-final-run-1/`). Ran concurrently with the mutation runs, so its timings (20k-row measurement 28 min) are not performance evidence. G14-R2's run of record had 36 tests |
| Full Authority suite | 1,769 total, **1,706 passed, 63 skipped, 0 failed** [T] (`authority-final.txt`) |
| Architecture | **224/224** [T] (a first run failed 1 test: the audit-only phase assertion, updated to the A-6 stream set and strengthened with the single-append check) |
| Contract | 958 total, **946 passed, 12 skipped, 0 failed** [T] |
| SEC-P01..SEC-P12, fresh security-test stack built from the final sources | **13/13** [T] (`sec-p-final/`). Afterwards **every** journal entry was delivered through D+: audit 16, authorization 60, content 25 `Anchored`; 0 of 16 organizations quarantined; `down -v` removed the stack |
| Strict specification validation | `validate-specifications.py --strict`: **STRICT VALIDATION PASS** (295 tracked files, after regenerating the manifest) [T] |

## 5. RD-1..RD-10 by stream

| RD | Content | Authorization |
|---|---|---|
| RD-1 stop classification | PREDECESSOR_MISSING → SR-C1; false alarms → SR-A; archive-instance stops, contradictory evidence and adversarial causes refused (SRT-19..24, SRT-38 on content) [T] | Same suite on authorization [T] |
| RD-2 end-to-end recovery | SRT-01..SRT-46 on content; `ContentStreamDPlusReadabilityTests` (SR-C1 through the real `ContentService`); PG `WP5BAnAuthorizedAnchoredRestore…(content)` [T] | SRT suite on authorization; `AuthorizationStreamGateDPlusTests`; PG `WP5BAnAuthorizedAnchoredRestore…(authorization)` and `WP5BTheRealGate…` [T] |
| RD-3 no state change beyond the hint | Content head, operation and manifest root unchanged by recovery; authorization revision and quarantine unchanged [T] | Authorization revision, applied projection, ZedToken and quarantine unchanged by recovery, in-process and on PostgreSQL [T] |
| RD-4 revocation preservation | n/a | Revocations committed during containment are held Pending behind the stop; the gate is `AwaitingArchive` throughout; after recovery the gate stays closed while any later revocation is undelivered (injected append outage) and opens only when all are delivered [T] |
| RD-5 content integrity | A refused checkpoint is never released through normal access, restart, forged reset or an archive outage; released after SR-C1 with exactly the anchored predecessor bytes and an unchanged head [T]. **E-7 residual** (§10) | n/a |
| RD-6 isolation | Per-stream batches: a stopped stream with more than one batch of rows never holds another stream; cross-organization SRT-12/16; PG isolation [T] | Same, plus the other organization's gate stays Ready [T] |
| RD-7 outage vs corruption | Archive, finding-store, record-store and pin outages wait without findings (enforcement suite, SRT-29/30, PG) [T] | Same suite on authorization [T]; the gate stays closed during outages [T] |
| RD-8 quarantine | SRT-37 on content [T] | SRT-37 on authorization; `RecoveryNeverLiftsAQuarantine` [T] |
| RD-9 mutations | MV-5B-01, -03, -05, -06 detected by content tests; SR-MV-01..15 detected in the content run [T] | MV-5B-02, -04, -05, -06, -07 detected by authorization tests; SR-MV-01..15 detected in the authorization run [T] |
| RD-10 regression | Content stage: Authority 1,669/0 failed; SEC-P 13/13 [T] | Final stage: §4 [T] |

## 6. Revocation and content-integrity evidence

**Authorization (RD-4), in-process** (`AuthorizationStreamGateDPlusTests`):
- A grant (41) is delivered and the gate is Ready. J4 deletes the archived 41, and a revocation (42) is committed. The gate is `AwaitingArchive` **at once**, before any delivery attempt. D+ refuses 42 (`PREDECESSOR_MISSING`).
- A second revocation (43), committed during containment, is held Pending (`DPLUS_STREAM_STOPPED`). A restart rediscovers the durable stop.
- An authorized SR-C1 recovery changes no authorization revision, projection, ZedToken or quarantine value. The gate is still `AwaitingArchive`: the row hint is not delivery.
- A pass with the append of 43 failing delivers 42 only, and the gate stays `AwaitingArchive`. The next pass delivers 43, and only then is the gate Ready. Every archived object equals its row byte for byte.
- **Concurrency:** 20 permission changes, two processors (15 passes each) and an executing recovery run concurrently with a gate observer. There are **0** observations of Ready while any authorization entry was undelivered, every entry was delivered once in order (41..62), and nothing was quarantined.
- **PostgreSQL** (`WP5BTheRealGateStaysClosedUntilEveryAuthorizationEntryIsDelivered`): the same sequence through the real `AuthorityStore` as the runtime role and the real `OrganizationGate`.

**Content (RD-5)** (`ContentStreamDPlusReadabilityTests`): a revision committed through the real `ContentService` is unreleased (`AwaitingArchive`) before delivery and released after it. With a J4-deleted predecessor, the next revision's checkpoint is refused and stays unreleased through a restart, a forged reset to Pending and an archive outage. An authorized SR-C1 recovery (one approval refused first) delivers it, the head and manifest are unchanged, and a replayed execution changes nothing.

**Scope [S]:** these demonstrations exercise the gate and release preconditions (delivery state of the journal rows). They do not exercise SpiceDB permission semantics, which are unchanged by WP-5b.

## 7. Independent commitment verification (EP-5)

`DPlusCommitmentInspection` (VC-1..VC-9) runs inside each stream's sweep. Comparison source: the archived bytes read in the sweep, and, at or below the checkpoint, the AuthorityDb rows that the full sweep re-verifies against the checkpoint digest (VC-10).

| Required case | Test (each stream: audit, content, authorization) | Result |
|---|---|---|
| Missing commitment where required | both copies removed → `MISSING` | [T] |
| Malformed | `{}` and a non-canonical name → `MALFORMED` | [T] |
| Conflicting copies | archive copy differs → `CONFLICTING_COPIES`; on real Azurite, a replaced store copy (J6) → `CONFLICTING_COPIES` | [T] |
| One copy missing | store copy removed → `COPY_MISSING`; without the store composed, not applied | [T] |
| Wrong scope / revision | another organization; another revision's commitment under the key → `MALFORMED` | [T] |
| Wrong archive instance | → `WRONG_INSTANCE` | [T] |
| Digest mismatch | row digest, row identity, predecessor digest, predecessor recorded absent, genesis with predecessor → `ROW_DIGEST` / `WRONG_ROW` / `PREDECESSOR_DIGEST` | [T] |
| Invalid stop generation | generation 1 without a generation-0 marker → `INVALID_GENERATION`; with the marker it supersedes generation 0 cleanly | [T] |
| Unauthorized replacement | both copies replaced consistently → `ROW_DIGEST` | [T] |
| Evidence changed after commitment | J4 replaces a delivered entry above the checkpoint → `ROW_DIGEST` | [T] |
| Beyond the head | → `BEYOND_HEAD` | [T] |
| Store outage | sweep inconclusive, no finding | [T] |

**Limits [S]:** archived objects at or below the checkpoint are not re-read (OI-DR-7), so a J4 replacement there is not seen by EP-5. An actor who replaces both commitment copies and the archived entry consistently, or who removes a commitment together with its revision's evidence, is not detected. Incremental sweeps list all of a stream's commitments, which is O(history) per sweep (same class as OI-DR-10).

## 8. Performance (P2-D5)

Same harness at every code state (`JournalVerificationPostgresIntegrationTests.WP5BMeasurement.cs`): disposable PostgreSQL 18 and two Azurite 3.9.0 instances on loopback, one process, counting proxies on every archive and store port. Backlog B pending rows in **each** of the three streams; steady state one row per stream per pass; discovery on the audit stream before and after one executed recovery.

**Drain throughput** [T] (all three streams, B rows each, so 3B entries in total):

| Enforced streams | B = 32 | B = 256 | B = 2,048 | Passes (B = 2,048) |
|---|---|---|---|---|
| audit (baseline, before WP-5b) | 30.5 entries/s (32.8 ms/entry) | 39.5 (25.3) | 32.6 (30.7) | 128 |
| audit + content | 27.3 (36.6) | 31.4 (31.8) | 21.4 (46.7) | 64 |
| audit + authorization + content | 18.7 (53.4) | 23.5 (42.6) | **13.6 (73.7)** | 64 |

**Store operations per entry** [T]:
- **1.0** without D+ (one append).
- **≈ 5.25** with D+: one append; the W2-S commitment as an archive read and create plus a store read and create (4); and about 0.25 of per-pass work amortized over a batch of 32 (sentinel, pin record, first checkpoint, stop-marker, finding and recovery-record listings, one predecessor read).
- Averages: 2.41, then 3.83, then 5.25, matching 1, 2 and 3 D+ streams out of 3.

**Commitment growth** [T]: one object per delivered D+ entry in **each** store, about 740 bytes each, so ≈ 1,480 bytes per entry in total. For example, the 6,144 entries of the B = 2,048 run produced 6,144 objects and 4.6 MB per store.

**Steady state** [T] (one new entry per stream per pass, 40 passes), pass p50 / p95 / max:

| Enforced streams | p50 / p95 / max (ms) | Operations per pass |
|---|---|---|
| audit | 188 / 343 / 454 | 15 |
| audit + content | 275 / 377 / 473 | 27 |
| all three | 475 / 822 / 941 | 39 |

Each D+ entry costs 13 store operations in a pass of one entry (the per-pass work is not amortized). This is about 90–140 ms per stream at p50 on this loopback rig.

**Stop and recovery discovery overhead** [T] (audit stream, one new entry per pass):

| State | Clean stream | After one executed recovery (7 recovery records, 1 stop) |
|---|---|---|
| p50, baseline run | 177 ms | 292 ms |
| p50, all-three run | 162 ms | 341 ms |

Discovery re-verifies every recovery record and signature on each pass, so its cost grows with the stream's recovery history. Caveat: the recovered variant does not count the provisioning and checkpoint ports, so its operation count (12) is not comparable with the clean variant's (13).

**Interpretation:**
- [T] Per-stream batches doubled the entries per pass (passes halved from 128 to 64), because a pass now delivers up to 32 entries per stream.
- [T] The operation count per entry is constant across backlog sizes.
- [I] The time per entry rises with backlog size (42.6 ms at B = 256 against 73.7 ms at B = 2,048), which points to store-side latency growth rather than more operations. This is not attributed by measurement.
- [S] No security check was relaxed. Checkpoint-growth optimization (OI-DR-10) was not started.
- These figures come from a single-machine loopback rig, not Azure, and are not production targets (PB-09).

## 9. Mutation and adversarial results

Scratch clone (`git clone --local` plus every uncommitted working-tree file); `--no-incremental` builds; original bytes restored and SHA-256 verified after each mutation; BASELINE and RESTORED pass in every set [T] (`mutations/summary.json`).

**WP-5b set** (`scripts/dev/wp5b-mutations.py`, 12 classes, 310 tests): **12/12 DETECTED**.

| ID | Mutation | Failing tests | Detected by |
|---|---|---|---|
| MV-5B-01 | content not D+-enabled | 85 | content enforcement/recovery suites, content release, phase |
| MV-5B-02 | authorization not D+-enabled | 91 | authorization suites, gate tests, processor tests |
| MV-5B-03 | one shared batch for all streams | 3 | the beyond-one-batch isolation test, each stream |
| MV-5B-04 | a refusal ends the pass for every stream | 3 | isolation tests (audit and authorization runs; content is the last stream, so ending its pass holds nothing), phase |
| MV-5B-05 | recovery accepted for any stream name | 3 | SRT-38, each stream |
| MV-5B-06 | the recovery hint marks the row Anchored | 51 | recovery suites ×3, gate tests, content release |
| MV-5B-07 | the gate ignores undelivered authorization entries | 5 | `AuthorizationStreamGateDPlusTests` |
| MV-5B-08 | EP-5 row-digest comparison skipped | 1 | EP-5 defect matrix |
| MV-5B-09 | EP-5 copy comparison skipped | 1 | EP-5 defect matrix |
| MV-5B-10 | EP-5 missing-commitment check skipped | 1 | EP-5 defect matrix |
| MV-5B-11 | the verifier never inspects the store copies | 2 | EP-5 single-copy tests. The first attempt did not compile (CS9113: the unread parameter is an error), so the mutation was rewritten to keep reading it and rerun with its own BASELINE/RESTORED: DETECTED |
| MV-5B-12 | missing D+ composition reported ready | 1 | `DPlusReadinessTests` |

**G14-R2 set** (`scripts/dev/g14-r2-mutations.py`, now over the six per-stream recovery and enforcement suites, 279 tests): **SR-MV-01..SR-MV-15 15/15 DETECTED**. Each was detected in the audit, content **and** authorization runs; SR-MV-12 was also detected by the enforcement suites.

**Adversarial coverage** (in-process, per stream, [T]):
- genesis violations and envelope/column mismatches;
- missing, altered, malformed and re-revisioned predecessors;
- re-chained rows with recomputed digests;
- incorrect links;
- forged `Anchored` predecessors and forged resets;
- archive outages at each read and write point;
- crash between the archive write and the row update;
- archive conflict (quarantine kept);
- held rows behind a stop;
- absent sentinel;
- every durable-stop store-outage combination;
- W2-S evidence replacement after a termination;
- marker removal, alteration and conflict;
- the known limit of removing both stop records and both commitment copies;
- discovery failures;
- repeated passes and processors;
- concurrent passes;
- beyond-one-batch isolation.

The whole G14-R2 SRT suite (approvals, bindings, single use, generations, evidence changes, SR-C1 anchoring, containment, restart, concurrent execution, key removal and retirement, quarantine) ran per stream, as did the PostgreSQL real-store tests (§4).

## 10. Remaining risks and production blockers

| ID | Risk |
|---|---|
| E-6 (pre-existing) | The organization gate trusts the rows' delivery state: a J1 actor who marks undelivered authorization rows `Anchored` reopens the gate. D+ does not trust it (nothing delivered, stop durable, the next change closes the gate again); the verifier reports `FAKE_RECEIPT`. **Characterized by test, not closed.** Closing it needs a separate decision (e.g., archive-presence verification of the authorization head) |
| E-7 (pre-existing) | Content release trusts the checkpoint row's delivery state: the same J1 forgery releases a revision whose checkpoint is not archived. Content integrity (HMAC, manifest) is still verified. **Characterized by test, not closed** |
| SC-R1 / SC-2 | An authorization stop closes the organization gate until recovery; classes SR-A/SR-C1 cannot resolve (forged rows, archive-instance change, contradictory anchors, adversarially changed evidence) are a **permanent lockout in this increment** and an outstanding broader-G14 **production blocker** |
| SC-5 | Without the journal-verification store no D+ stream is delivered: in any host outside Development/Testing (where the composition is refused), the Authority is never ready and no organization has protected access. **Production blocker until WP-8** |
| SC-6 | The persistent Development stack (non-empty, unprovisioned archive) would deny all protected access if started on WP-5b code. Not touched. The local `enterpriseforms/*:local` image tags were rebuilt by the SEC-P runs and are restored to their pre-WP-5b images afterwards (§13; image IDs verified equal) |
| Availability | Every authorization-stream delivery now also depends on the journal-verification store and the pin anchors (an outage closes the gate for organizations with pending authorization changes) |
| R-SR-1..R-SR-11, R-DR-1, JP, PV-9 | Unchanged |
| Synthetic approvers | Development/Testing recovery approval is not two-human evidence (SC-7) |

## 11. CF-1

CF-1 is the P1 conflict that "a J1 actor can make the Authority archive an authorization or content entry that links to a forged predecessor". It "closes only with P2" (§5.6).

With A-6, every authorization and content entry passes D+ before its append. The per-stream enforcement suite shows the following for both streams, in-process and on real stores:
- an altered, malformed or re-revisioned predecessor is refused;
- a re-chained row with recomputed digests is refused;
- an incorrect link is refused;
- a forged `Anchored` predecessor is refused;
- forged AuthorityDb delivery state never resumes delivery.

**Recommendation:** close CF-1 for Development/Testing on owner acceptance of WP-5b. E-6 and E-7 are separate. They concern trusting delivery state at the gate and at release, not delivery establishing trust in a forged predecessor, and they remain open.

## 12. Files changed

All changes are uncommitted on `main` (`a9b32de`), on top of the earlier uncommitted SEC-BL-01 work.

**Source (Authority):**
- `Application/Features/Journal/AuditDeliveryEnforcement.cs`: `EnforcedStreams` = audit, authorization, content; `KnownStreams`.
- `Application/Features/Journal/ArchiveProcessor.cs`: per-stream batches; the pre-WP-5a delivery path removed; wording generalized from "audit stream" to "stream".
- `Application/Features/Journal/Recovery/DPlusStreamRecoveryService.cs`: refusal text for non-D+ streams (behaviour follows `IsEnforced`).
- `Application/Features/Journal/Reconciliation/DPlusCommitmentInspection.cs` (**new**, 210 lines): EP-5, VC-1..VC-9.
- `Application/Features/Journal/Reconciliation/JournalStreamComparer.cs`, `JournalVerifier.cs`, `JournalVerificationFindings.cs`: commitment loading, comparison source (VC-10), `DPLUS_COMMITMENT_MISMATCH`.
- `Api/Composition/JournalRecoveryCommand.cs`: `--stream`.
- `Api/Composition/JournalVerificationComposition.cs`: the verifier receives the record store.
- `Api/Composition/SigningProviderPolicy.cs`: `DPlusReadiness` and the readiness check (CE-1).

**Tests:**
- `DPlusDeliveryEnforcementTests.cs`: renamed from `AuditDeliveryEnforcementTests.cs`; an abstract per-stream base with Audit, Content and Authorization subclasses, plus `DPlusStreamPhaseTests`.
- `DPlusRecoveryTests.cs`: an abstract per-stream base with three subclasses; SRT-12/16/38/46 generalized.
- New: `DPlusCommitmentVerificationTests.cs`, `DPlusReadinessTests.cs`, `AuthorizationStreamGateDPlusTests.cs`, `ContentStreamDPlusReadabilityTests.cs`, `JournalVerificationPostgresIntegrationTests.WP5B.cs` (real stores), `JournalVerificationPostgresIntegrationTests.WP5BMeasurement.cs` (measurement harness, counting proxies).
- Changed:
  - `CapabilityRefusalEvidenceWorld.cs`: non-audit stream history and seeding under the organization row lock; the gate's pending count.
  - `InMemoryAuthorityStore.cs`: opt-in `RealJournalDelivery`.
  - `ContentHistoryTests.cs`: the harness is internal; `AnchorAfterCommit`.
  - `ArchiveProcessorTests.cs`: rigs compose D+ durability.
  - Architecture `JournalVerificationBoundaryTests.cs`: the A-6 stream set and the single-append check.

**Scripts:**
- `scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1`: `-Class`, `-MethodFilter`, `-Measure`, `-MeasureBacklogs`.
- `scripts/dev/wp5b-mutations.py` (**new**).
- `scripts/dev/g14-r2-mutations.py`: test classes updated to the per-stream suites.

**Documents:**
- `docs/decisions/PHASE_C_DECISION_LOG.md`: C-156, C-157.
- `SEC_BL_01_DR_CONTRACT_CLOSURE.md`: A-5 (§25), A-6 (§26), status row.
- `SEC_BL_01_P2_READINESS_RECORD.md` (**new**).
- This record (**new**).
- `G14_R2_IMPLEMENTATION_RECORD.md`: status rows.
- `SECURITY_BACKLOG.md`: progress rows.
- `SHA256SUMS.txt` and `spec-files.json`: regenerated.
- `docs/evidence/sec-bl-01-wp5b/` (**new**).

## 13. Evidence

`docs/evidence/sec-bl-01-wp5b/`:
- `measurement-1-audit-only/`, `measurement-2-audit-content/`, `measurement-3-all-streams/`: the JSON results and console logs of the same harness at the three code states.
- `pg-content-stage/` (failed attempt, preserved), `pg-content-stage-run2/`, `pg-azurite-final-run-1/` (with the WP-4A measurement and checkpoint-growth JSON).
- `sec-p-content-stage/`, `sec-p-final/`.
- `authority-content-stage.txt`, `authority-final.txt`, `architecture.txt`, `contract.txt`: failures, skips and summaries.
- `mutations/summary.json`: every mutation, with its anchor files, original SHA-256, restoration check and failing tests.

Local paths are scrubbed. Synthetic data and synthetic approver keys only; Development/Testing evidence, not production evidence.

**Local images:** the SEC-P runs rebuild `enterpriseforms/{authority,api,worker,dev-seed,platform-console}:local`. The images present before WP-5b were tagged `:pre-wp5b-saved` first, and the `:local` tags are re-pointed to them after the runs, so no stack starts on WP-5b code by accident (SC-6). Verified: the restored `:local` image IDs equal the saved ones; the temporary tags were then removed. The `:dev` tags were not touched.

## 14. Not claimed

- No closure of SEC-BL-01, PD-15, G14, OI-DR-9, E-6 or E-7. CF-1 closure is an owner decision. DD-10 keeps its qualified Development/Testing resolution. M2F3-43 stays FAILED pending formal WP-7 verification.
- No production enablement, two-human assurance, store independence, Azure evidence or independent review.
- No recovery on any persistent environment; the persistent Development stack, its data and its baseline were not touched. Stage B was not run.

### A. Contract preservation

The contract's §1–§20 (file without the status row at line 15, up to "## 21.") hash to `3549e3ba…f8f2` before A-5 and after A-6 (method used in this session; the WP-0 record's `4a8bd1b1…` uses a different exclusion).
