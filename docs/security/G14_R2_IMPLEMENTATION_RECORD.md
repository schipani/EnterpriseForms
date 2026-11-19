# SEC-BL-01 G14-R2 — Implementation Record: Controlled Audit-Stream Recovery

**Status (2026-11-18):**

| Item | State |
|---|---|
| Increment | G14-R2, contract amendment **A-4, §24**:<br>- signed two-approver recovery records outside AuthorityDb;<br>- the D+ lift;<br>- stop generations;<br>- strategies SR-A and SR-C1;<br>- archive-instance pinning;<br>- the W2-S evidence pre-commitment.<br>Development/Testing only |
| Authorization | Owner package "G14-R2: Controlled Audit-Stream Recovery Implementation" (2026-11-18). As with the earlier packages, it is read as authorization (**owner to confirm**). No decision-log number was assigned.<br>**Update 2026-11-18 (C-156):** the owner accepted G14-R2 for Development/Testing and recorded it as **C-156** (contract amendment A-5, §25) |
| Not done | Not done: SR-B, SR-D, SR-E, authorization/content D+, broader G14, the production requester permission (SR-OD-05), retention (SR-OD-16), the verifier's commitment-versus-archive comparison, production resources, Stage B, commit, push, merge, deployment, and recovery on any persistent environment |
| Baseline | `main` at `a9b32de`, with every uncommitted WP-0..WP-5a.2 and G14-R1 change preserved.<br>- Contract §1–§20 byte-identical (`4a8bd1b1…ef12e`).<br>- §1–§23 byte-identical apart from the excluded status row (checked by prefix comparison).<br>- §24 appended |
| DD-10 | **Not closed**; owner decision OD-R2-2 (§6).<br>**Update 2026-11-18 (C-156):** resolved within the tested Development/Testing single-store threat model; **open for production assurance**. OD-R2-1 confirmed (fail closed when not composed) |
| SEC-BL-01 / PD-15 / M2F3-43 | OPEN / OPEN / FAILED |
| Review | Owner-led; prepared by the implementing assistant; **not an independent review** |

---

## 1. Contract reconciliation and owner decisions

Contract §24 carries the reconciliation table: seven draft rules that conflict with or are refined by the dispositions. The material conflicts were:
- **W2-S binding:** the draft had no stop generation and excluded body digests. The disposition requires the generation and the evidence identity and digest.
- **Durability of the commitment:** the draft required at least one store. Both stores are now required, because otherwise one store administrator could delete the only copy.
- **Instance change:** the draft lifted it only through SR-D. With SR-D not implemented, an instance-change stop is non-recoverable in this increment.

One draft defect was corrected: the stop generation now derives from executed, signature-verified recoveries, not from unsigned outcomes.

| Decision | Status in G14-R2 |
|---|---|
| SR-OD-01 (signed, create-only records; lift by signature), SR-OD-02 (outside AuthorityDb), SR-OD-04 (protected public-key configuration), SR-OD-06 (requester/approver separation), SR-OD-09 (generations), SR-OD-10 (VF→Pending as hint only), SR-OD-11 (W2-S), SR-OD-12 (pinning), SR-OD-13 (SR-D/SR-E deferred), SR-OD-18 (this increment) | **Approved** by the G14-R2 dispositions; authoritative in §24.1–§24.9 |
| SR-OD-03 (separate key set), SR-OD-07 (bounds: 900 s, 24 h), SR-OD-14 (permanent blocks), SR-OD-15 (rule-set version), SR-OD-17 (key removal) | **Not reviewed**: implemented as restrictive provisional defaults (§24.10); owner confirmation needed.<br>**Update 2026-11-18 (C-156):** retained by the owner **without loosening**; any loosening needs a new owner decision |
| SR-OD-05 (production requester permission), SR-OD-16 (retention) | Not reviewed; **not implemented** |
| SR-OD-08 (SR-B) | Not approved; **not implemented** |
| New: OD-R2-1 | Without the journal-verification store (production hosting until WP-8), the audit stream is now never evaluated (`DPLUS_DURABILITY_NOT_COMPOSED`). This is a change outside Development/Testing behaviour; confirm or narrow |
| New: OD-R2-2 | DD-10 status (§6) |

## 2. Files changed

**New:**
- `Application/Common/Ports/IDPlusRecordStores.cs`: the `IArchiveMetaRecords` and `IJournalVerificationRecordStore` ports.
- `Application/Features/Journal/Recovery/`:
  - `DPlusRecoveryRecords.cs`: names, records, codec.
  - `DPlusRecoveryApprovals.cs`: options, ES256 verifier, authorization.
  - `DPlusRecoveryLedger.cs`: derived state, lift, generations.
  - `DPlusEvidenceCommitments.cs`: durability bundle, W2-S, instance pin.
  - `DPlusStreamRecoveryService.cs`: propose, decide, execute, outcome, status.
- `Api/Composition/JournalRecoveryCommand.cs`: the CLI.
- `scripts/dev/g14-r2-mutations.py`.
- Tests:
  - `DPlusRecoveryTests.cs` (57), `JournalRecoveryCommandTests.cs` (3);
  - `DPlusTestSupport.cs`;
  - `JournalVerificationPostgresIntegrationTests.G14R2.cs` (8 PG/Azurite).
- `docs/security/G14_R2_IMPLEMENTATION_RECORD.md`.
- `docs/evidence/g14-r2/`.

**Modified:**
- `ArchiveProcessor.cs`: lift-aware discovery, generations, pin, W2-S, hold.
- `AuditDeliveryEnforcement.cs`: gather → commit → evaluate; pin.
- `DPlusStreamStops.cs`: v2 markers, generation names, new stopping codes.
- `JournalVerificationFindings.cs`: optional `StopGeneration`; codes.
- `JournalStreamComparer.cs`: the verifier recognizes recovery records, commitments and generation markers.
- `BlobSecurityEventArchive.cs`: `IArchiveMetaRecords` for Blob, in-memory and unavailable archives; test hook.
- `VerificationCheckpointStores.cs`: Blob and in-memory record stores.
- `JournalVerificationComposition.cs`: registrations; key-set validation.
- `Program.cs`: CLI wiring.
- Test rigs: `AuditDeliveryEnforcementTests.cs`, `CapabilityRefusalEvidence*`, `ArchiveDeliveryOutageCharacterizationTests.cs`, `JournalVerificationPostgresIntegrationTests.cs`, `JournalVerificationAzuriteIntegrationTests.cs`.
- Contract (§24 appended; status row).
- `G14_R1_DRAFT_AMENDMENT_A4.md` (status row).
- `SECURITY_BACKLOG.md`.
- Manifests.

## 3. Recovery records and approvals

**Records:**
- create-only, canonical (unknown members refused) and in both stores (§24.1);
- AuthorityDb holds no recovery state;
- the state is derived on every pass by `DPlusRecoveryLedger` from the copies in both stores;
- a contradictory pair of copies is unusable and reported (`SR_RECORD_CONTRADICTORY`).

**Approvals:**
- compact ES256 JWS with an exact claim set, verified against `JournalRecovery:Approvers` public keys from configuration;
- at startup:
  - the synthetic mode is refused outside Development/Testing;
  - fewer than two approvers, non-P-256 keys, duplicates and keys shared with `OperatorApprovals` are refused;
- authorization requires two distinct approvers other than the requester and the analyst, and no verified rejection or cancellation.

**Historical keys:**
- a retired key verifies only earlier decisions;
- a removed key verifies nothing, so its lifts disappear (fail closed).

**No runtime identity can approve:** the Authority and the tool hold public keys only, and the CLI has no signing verb.

## 4. SR-A and SR-C1

| Path | Result |
|---|---|
| **SR-A** (stop, predecessor reappears unchanged, triaged) | D+ alone stays stopped. With two approvals and execution, the hint is written, the next pass delivers `k`, the outcome is `Completed`, and every original marker and finding is byte-identical (IP SRT-01; PG `G14R2AuthorizedFalseAlarmRecoveryOnRealStores`). Statement trace on PostgreSQL: the only AuthorityDb change by recovery is row `k`'s state and code (the hint) |
| **SR-C1** (absent predecessor, anchored copy) | Requires a full-chain-valid checkpoint verifying exactly `k − 1` in the pinned instance, with an equal digest. The predecessor is created create-only and `k` is delivered (IP SRT-02, PG). Refused when the digest, revision or chain is wrong (SRT-19), or the predecessor is present (SRT-20, never overwritten); a write conflict fails without a lift (SRT-31) |
| Later failure | After a recovery, a new failure at the same revision is a generation-1 stop: `{k}.r0001.json`, schema v2, and a generation-1 finding that the old authorization does not cover (SRT-15; PG). A mismatch stop (`DPLUS_RECOVERY_EVIDENCE_MISMATCH`) is SR-A-eligible only (PG generation-one test) |
| No fabricated continuity | Nothing is re-signed. No checkpoint changes and assurance stays unchanged. The executor writes only the bound bytes (SR-C1) and the hint |

## 5. Archive-instance pinning

| Case | Result |
|---|---|
| Sentinel names the pinned instance (provisioning record or first checkpoint) | Delivery proceeds |
| Substituted sentinel (new instance) | `ARCHIVE_REGRESSION/INSTANCE_CHANGED`: durable marker and finding, row marked. Restoring the original sentinel does **not** resume, and the proposal is refused (`RECOVERY_ARCHIVE_INSTANCE_BLOCKED`). IP SRT-36; PG `G14R2ArchiveInstanceSubstitutionOnRealStores` |
| Anchors disagree or are invalid | `ARCHIVE_REGRESSION/INSTANCE_ANCHOR_CONTRADICTORY`, durable (IP pinning test) |
| No anchor | `ARCHIVE_REGRESSION/INSTANCE_UNPINNED`: no marker, the entry stays Pending; it resumes once a first checkpoint pins the instance (IP) |
| Anchors unreadable | `DPLUS_ARCHIVE_PIN_UNAVAILABLE`, the stream waits |

**Operational note:** an archive established by a baseline must be pinned by its first checkpoint (`--journal-baseline --create-sentinel --create-checkpoint`). Otherwise its audit delivery stays `INSTANCE_UNPINNED`. This applies to the persistent development stack, where nothing was run.

## 6. W2-S failure-window analysis (DD-10)

| Window | Before (A-3, §23.3) | With W2-S (§24.7) | Evidence |
|---|---|---|---|
| Detection, termination before any stop record, evidence replaced, restart | Delivered (R-W5A2-1) | **Durable stop** `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT`: the commitment was durable in both stores **before** evaluation | IP `W2SDetectsEvidenceReplaced…` (inverted from the former residual test); PG `G14R2EvidenceCommitmentsAndTheDD10SequenceOnRealStores` |
| The same termination with unchanged evidence | Re-derived | Re-derived; no recovery needed | IP SRT-41; PG (same test) |
| Termination between the two commitment writes | — | Nothing was evaluated. The next pass compares the surviving copy and creates the missing one, or stops on a difference | IP (commitment outage), code path |
| Commitment store unavailable | — | No evaluation; `DPLUS_EVIDENCE_COMMITMENT_UNAVAILABLE` | IP SRT-42 |
| Concurrent writers with different evidence | — | Create-only conflict, so a change and a stop | Code path; IP SRT-43 (contradictory copies) |
| Evidence altered before its first commitment | Undetected (R-DR-1) | Unchanged (R-DR-1) | — |
| Marker and finding removed (both stores) | Resumed (A-3 known limit) | **Stopped**: the commitment detects the change | IP `RemovingBothStopRecordsNoLongerResumes…` |
| Marker, finding **and** both commitment copies removed | — | Resumed: the narrowed known limit, R-SR-2 | IP SRT-44; `KnownLimitRemovingBothStopRecordsAndBothCommitmentCopiesLeavesNoRecord` |
| Re-sent delivered row altered after delivery | Quarantine (`ARCHIVE_BODY_MISMATCH`) | Unchanged: the EP-4 exception keeps the existing quarantine | `CapabilityRefusalEvidenceRemovalTests` |
| Compromised runtime (JP) | Out of scope | Out of scope | — |

**Conclusion.** The detection-to-persistence window of §23.3 no longer exists for the evidence: D+ never detects before the commitment is durable in both stores. A later replacement is therefore detected after any termination, unless an actor removes the commitment from **both** stores. **DD-10 is not fully closed:**
- its protection rests on two configuration-separated Development/Testing stores (PV-9) and fails against an actor controlling both (J4/J5 + J6);
- R-DR-1 remains;
- production requires immutable, independent stores and ledger anchoring (WP-8).

**Recommendation (OD-R2-2):** record DD-10 as **closed for Development/Testing behaviour against single-store actors**, with R-DR-1 and R-SR-2 as accepted residuals, and open for production until WP-8. Do not record it as closed outright.

## 7. Restart, concurrency and adversarial results

**Restart:** derived state identical across restarts at every state (SRT-27); a restarted processor re-reads commitments (SRT-40/41).

**Interruption:** a termination at the hint leaves no lift; the re-run completes idempotently, treating its own predecessor write as the approved absent state (SRT-26).

**Concurrency:**
- same recovery executed concurrently: one execution record (IP SRT-25; PG on Azurite create-only);
- two recoveries of one stop set: one executes, the other `Failed` (SRT-14).

**Adversarial, each denied or detected:**
- AuthorityDb reset, forged hint code or forged Anchored (SRT-32; PG);
- archive `k` created directly (SRT-33; PG);
- records copied to another organization (SRT-12);
- edited statement (SRT-05), impostor key (SRT-04), reused `jti` (SRT-13);
- record deletion from one store (lift kept) or both stores (re-stop) (SRT-34; PG);
- key removal (SRT-10);
- stop record not in the statement (SRT-24);
- quarantine (SRT-37), other streams and deferred strategies (SRT-38).

**Pinned residual:** R-SR-6, a cancellation deleted from both stores plus a forged execution record, lifts (SRT-35).

## 8. SRT and mutation results

**SRT matrix (layers: IP in-process; PG disposable PostgreSQL 18 + two Azurite):**

| Tests | Layer | Result |
|---|---|---|
| SRT-01, 02, 07, 15, 21, 25, 30, 32, 33, 34, 36, 39, 40, 41 | IP + PG | Pass |
| SRT-03..06, 08..14, 16..20, 22..24, 26..29, 31, 35, 37, 38, 42..44, 46 | IP | Pass |
| SRT-45 | IP | Pass, **adapted**: SR-OD-15 is unreviewed, so the restrictive default (a version difference is a change) is tested instead of "re-evaluate and record both versions" |
| SRT-01 | — | **Adapted**: there is no D+ rule toggle in production code. The false alarm is a predecessor that disappears and reappears unchanged (a triaged Development reset) |
| SRT-47 | — | **Not applicable**: SR-B is not implemented |
| Additional | IP | Pinning (unpinned, first-checkpoint pin, contradictory anchors); no durability means no evaluation; codec and marker-name strictness; generation identity; CLI parsing and refusals (`JournalRecoveryCommandTests`, 3) |

Totals:
- `DPlusRecoveryTests`: 57.
- PG: 8 G14-R2 tests within a 36-test gate.

**Mutations** (`scripts/dev/g14-r2-mutations.py`, scratch clone, `--no-incremental`, classes `DPlusRecoveryTests` + `AuditDeliveryEnforcementTests`, 94 tests):
- BASELINE and RESTORED: PASS.
- **SR-MV-01..SR-MV-15: all 15 DETECTED, every file restored by SHA-256.**
- The first SR-MV-10 attempt did not build (the edit dropped a null-forgiving operator). It was corrected (`ledger!.Generation * 0`) and re-run: DETECTED.
- Detecting tests: `docs/evidence/g14-r2/mutations/summary.json`.
- Observation: SR-MV-05 (treat the AuthorityDb hint as a lift) is caught by SRT-24 and SRT-26. In SRT-32 the W2-S commitment independently stops the forged lift, so the defence is layered.

## 9. SEC-P and regression results

**PostgreSQL + Azurite gate** (`sec-bl-01-wp4-pg-azurite-gate.ps1`, 20,000 rows, fresh containers removed afterwards):
- **Run 1: 36/36. Run 2: 36/36.**
- Performance matches WP-4A at 20k rows: baseline 423 s and 429 s against 461 s; incremental sweeps 12–14 s.
- A first attempt had 2 failures, both test-design errors fixed before the runs of record (`pg-azurite-attempt-1-failed/`):
  - a mismatch stop is SR-A-only;
  - an unreachable finding store also blocks discovery, so the DD-10 scenario needs write-only failure.

**SEC-P01..SEC-P12:** fresh `enterpriseforms-sectest` stack, provisioner `Provisioned`, **13/13**. Afterwards audit 16, authorization 60 and content 25 entries were Anchored, and 0 of 16 organizations were quarantined. `down -v` left no containers or volumes.

**Regressions:**
- Authority: 1,548 total, **1,495 passed, 53 skipped, 0 failed** (WP-5a.2: 1,480).
- Architecture: **224/224**.
- Contract: 958 total, **946 passed, 12 skipped**.
- Stage B was not run.

**Existing tests changed by the new semantics, with reasons:**
- The former DD-10 residual test is inverted (W2-S).
- The known-limit test now removes both commitment copies.
- The sentinel-resumption test uses a sentinel naming the pinned instance.
- Test rigs compose `DPlusDurability`.

## 10. Remaining risks and open decisions

| ID | Risk or decision |
|---|---|
| R-SR-1 / R-SR-2 | An actor controlling both stores can remove stop records and commitments, and delivery resumes (accepted in Development/Testing; WP-8) |
| R-SR-3 | The stores are configuration-separated only (PV-9) |
| R-SR-4 | Checkpoint rollback (CKR-3) could affect the SR-C1 anchor across restarts; production needs the ledger |
| R-SR-5 | Synthetic approver keys, typically held by one person: not two-human evidence |
| R-SR-6 | Approved-but-cancelled execution by a two-store actor (pinned, SRT-35) |
| R-SR-7 | Removing a key re-stops every stream it lifted (availability) |
| R-SR-9 | The deployment configuration is the root of trust (PB-11) |
| R-SR-11 | W2-S adds two create-only writes and two reads per evaluated audit revision, and doubles the audit-stream meta objects. Not measured at scale on the delivery path |
| R-DR-1, JP | Unchanged |
| OD-R2-1 | Without the verification store (production until WP-8) the audit stream is never evaluated: confirm or narrow |
| OD-R2-2 | DD-10 status (§6) |
| §24.10 | Confirm the provisional defaults: SR-OD-03, -07, -14, -15, -17 |
| Not implemented | SR-OD-05, SR-OD-16, SR-B (SR-OD-08), SR-D, SR-E, the verifier's commitment-versus-archive comparison (EP-5) |

## 11. Evidence

`docs/evidence/g14-r2/`:
- `pg-azurite-run-1/`, `pg-azurite-run-2/`, `pg-azurite-attempt-1-failed/`;
- `sec-p/`;
- `mutations/summary.json`;
- `authority.txt`, `architecture.txt`, `contract.txt`.

Local paths are scrubbed. Synthetic data only; Development/Testing evidence, not production evidence.

## 12. Not claimed

- No closure of SEC-BL-01, PD-15, DD-10, CF-1, G14, OI-DR-1 or OI-DR-9; M2F3-43 stays FAILED.
- No production enablement, two-human assurance, store independence or independent review.
- No recovery executed on any persistent environment; the persistent development stack was not touched.
