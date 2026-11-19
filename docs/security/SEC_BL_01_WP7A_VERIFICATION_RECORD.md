# SEC-BL-01 / PD-15 — WP-7A Integrated Verification Remediation and Acceptance Completion

**Status (2026-11-19):**

| Item | State |
|---|---|
| Increment | WP-7A: F-1 remediation (TR-6′), OI-DR-7 remediation (VF-15), J2 revocation-deletion assessment, AC-DR-11 scale measurement, contract-integrity and coverage reconciliation, repeated clean verification. Development/Testing only |
| Authorization | Owner decision **C-160** (2026-11-19); contract amendment **A-9** (§29) |
| Owner acceptance | **Pending.** Nothing here is a formal acceptance. M2F3-43 stays **FAILED**; AC-DR-13 pending; SEC-BL-01 and PD-15 **OPEN** |
| Not done | Stage B (not authorized; not run); any Azure resource; any persistent environment, production infrastructure or CI change; commit, push, merge or deployment |
| Baseline | `main` at `a9b32de` with every uncommitted WP-0..WP-7 change preserved |
| Review | Owner-led; prepared by the implementing assistant; **not an independent security review** and not a certification |
| Dated note (2026-11-19, C-161) | The owner **accepted WP-7A** (implementation and executed evidence) for Development/Testing and decided D-7A-1..D-7A-7 as recorded in C-161 and contract amendment A-10 (§30). F-1 and OI-DR-7 remediations accepted; L-12 accepted, with the SEC-P12 condition CN-3; F-7A-4, F-7A-5 and AC-DR-11 deferred to the pre-production backlog (PP-10, PP-08, PP-11); J2 P-1 accepted as a Development/Testing residual. D-7A-8 is still open. AC-DR-13 is **conditionally** met; M2F3-43 stays **FAILED**; SEC-BL-01 and PD-15 stay **OPEN**. Consolidation: `SEC_BL_01_DEVELOPMENT_WORKSTREAM_CONSOLIDATION.md`. The text below is unchanged |

**Summary for the owner.**
- **F-1 is remediated.** A cached receipt now answers for at most Φ = 1 s before it is re-established from the stores. Withdrawn evidence stays refused, and a head with successor evidence is refused. The original attack is demonstrated before (WP-7 runs and mutation MV-7A-01) and after (18 PostgreSQL/Azurite cases in both runs of record).
- **OI-DR-7 is remediated.** The full sweep re-reads every historical object, and all six historical attack classes are detected without a re-baseline.
- **J2:** phases 2 and 3 of revocation deletion are now refused at the gate (observed 711–821 ms in a long-lived process). Phase 1 remains a residual and needs your decision.
- **AC-DR-11 remains INCONCLUSIVE.** A 2M run is not feasible on Azurite 3.9 here; requirements and a plan are given.
- **Owner decisions:** three findings need your decision: F-7A-3 (SEC-P12 change and the outage availability consequence), F-7A-4 (a pre-existing recovery race) and F-7A-5 (the commitment-inspection cost of every sweep). D-7A-1..D-7A-8 are in §11.2.
- **Unchanged:** M2F3-43 **FAILED**; SEC-BL-01 and PD-15 **OPEN**.

Evidence tags: **[T]** executed test evidence in `docs/evidence/sec-bl-01-wp7a/`; **[T-WP7]** the WP-7 runs of record (`docs/evidence/sec-bl-01-wp6-wp7/`); **[S]** source inspection; **[I]** inferred; **[NV]** not verified.

---

## 1. Owner decisions recorded (C-160, A-9) and reconciliation with D-1..D-6

| Package decision | Reconciled with the WP-6/WP-7 record (§11.4) | Recorded |
|---|---|---|
| Accept WP-6 completion (Development/Testing) | — (WP-6 = M-6 = G08, A8-6) | C-160 |
| Accept the WP-7 executed verification evidence, not formal WP-7 acceptance | **D-6**: AC-DR-13 stays pending; the added WP-7 tests and driver are covered only as executed evidence | C-160 |
| Authorize F-1 remediation and amendment of TR-6 | **D-2**: none of options (a) accept, (b) time-to-live, (c) sentinel re-check was chosen as such. The package excludes cache expiry alone as proof and requires an explicit validity model: A-9 §29.1 | C-160; A-9 §29.1 |
| Authorize OI-DR-7 remediation | **D-3**: the contract's "optional; default off in Development" recommendation (§19) is not adopted | C-160; A-9 §29.2 |
| Authorize assessment of the J2 revocation-deletion window | **D-5**: the J2 undelivered-head exposure is **not** accepted as a residual; it is assessed (§5) | C-160; A-9 §29.3 |
| AC-DR-11 remains INCONCLUSIVE | **D-4**: the 2k/20k extrapolation is not accepted; a 2M-row measurement is to run if feasible (§6) | C-160 |
| AC-DR-13 remains pending | **D-6** | C-160 |
| M2F3-43 remains FAILED | **D-1**: no reclassification was decided; the proposed PASS is not adopted | C-160 |
| SEC-BL-01 and PD-15 remain OPEN | — | C-160 |

No other decision is assumed. In particular nothing here closes OI-DR-7 formally, accepts the F-1 residual window, or decides F-7A-4 (§8).

## 2. Contract integrity reconciliation (package §2)

Both historical prefixes are reproduced by `scripts/dev/sec-bl-01-contract-hashes.py` [T: `contract-hashes-before.txt`, `contract-hashes-after.txt`]. They hash **different byte ranges of the same §1–§20 text**; neither indicates a content change.

| Method | Bytes hashed (file before A-9) | Full SHA-256 | First used |
|---|---|---|---|
| H-WP0 | `[0, 1284)` + `[3223, 62246)`: the file without the status row at line 15 (`| Amendments | … |`, added by A-1) and without everything from the `---` separator before `## 21.`; the result ends with §20's last line and its newline. This is exactly the accepted WP-0 file | `4a8bd1b1c9f1126bf96db0817a7d662f23f21161337d71a5541e6325720ef12e` | WP-1/WP-2 record (whole-file hash of the WP-0 contract); WP-4A onwards ("the file with the A-1 status row and §21 removed") |
| H-AWK | `[0, 1284)` + `[3223, 62252)`: `awk 'NR==15{next} /^## 21\./{exit} {print}'` | `3549e3ba280fc85143c184b56f26bbfaf96ad1e3499241fb8dd71a4e6addf8f2` | WP-5b record onwards |
| Difference | `[62246, 62252)` = `\n---\n\n` (the separator A-1 inserted before §21) | — | — |

Reproduce: `python scripts/dev/sec-bl-01-contract-hashes.py` (read-only; refuses CR bytes rather than normalizing; the file is LF-only). After A-9 both values are unchanged (byte offsets after line 15 shift by the length change of the status row only). §21–§27 per-section SHA-256 are unchanged; §28's original 2,737 bytes still hash to `57caf5018fb85506f9d1d964b34ebea672bd9716ce9342872b3e38e6db79d75b`, and its range now additionally contains the `\n---\n\n` separator before §29. No earlier per-amendment hash had been recorded, so §21–§28 could be verified only against the state at the start of this increment (recorded in `contract-hashes-before.txt`), not against a historical value. No discrepancy was found; no contract change was stopped.

## 3. F-1: trusted-receipt validity (TR-6′, A-9 §29.1)

### 3.1 Cause and remediation

A-7 TR-6 cached a verified receipt per process, keyed by (organization, stream, revision, row digest). A cache hit skipped TR-2..TR-4, so an archive-instance replacement, an archived-object deletion or a commitment change left the gate `Ready` and released content released for the same, unchanged head, for the process lifetime (WP-7 F-1). `TrustedJournalReceipts` now implements RV-1..RV-9 [S: `src/authority/AuthorizationAuthority.Application/Features/Journal/TrustedJournalReceipts.cs`]:

- every verified attestation answers for at most Φ after its establishment **started**, and is then re-established from the stores;
- an establishment reads object, pin, sentinel, both commitment copies and stop markers in parallel, plus, for a head, the successor object and successor commitments;
- evidence that verified a key and later fails or changes is withdrawn for the process lifetime;
- the gate asks for a **stream-head** receipt (`VerifyStreamHeadAsync`); content release and projection ask for entry receipts.

The port `IJournalReceiptVerifier` gained `VerifyStreamHeadAsync`. The host reads Φ from `JournalReceipts:CurrentStateMaxAgeMilliseconds` (default 1 000). Production compositions are unchanged otherwise.

### 3.2 Package §3 items

| Item | Rule | Behaviour | Evidence |
|---|---|---|---|
| Archive-instance substitution | RV-6 (instance, pin) | Refused, then withdrawn, at the first establishment after the change (≤ Φ) | [T IP `F1…(instance-replaced, sentinel-deleted)` ×3 streams; PG `WP7AF1…(instance-replaced)` ×3] |
| Archived-object replacement | RV-6 (TR-2) | Same | [T IP/PG `object-replaced`] |
| Archived-object deletion | RV-6 (TR-2) | Same | [T IP/PG `object-deleted`] |
| Commitment modification or deletion | RV-6 (TR-4) | Same, either store, one or both copies | [T IP `commitment-archive-deleted`, `commitment-store-deleted`, `commitment-modified`; PG `archive-commitment-deleted`, `store-commitment-changed`] |
| Recovery-generation change | RV-5, RV-6 | A substituted generation (no durable stop of g − 1) never verifies, at first sight or after a restart; a changed generation of an established key is withdrawn; a genuine G14-R2 recovery generation verifies | [T IP/PG `generation-substituted`; PG `WP7AANewStopClosesACachedReceiptAndARealRecoveryGenerationVerifies` ×2] |
| Newly established durable stop | RV-5, RV-4 | A stop at or below the entry is `RECEIPT_STOP_UNRESOLVED` until a later-generation commitment in both stores resolves it; above a head it is successor evidence | [T IP `ANewlyEstablishedStop…` ×3; PG WP7A-PG-02] |
| Missing or conflicting evidence | TR-2..TR-4, RV-6 | Never verified; if it had verified earlier, withdrawn; never verified before, not withdrawn | [T IP `EvidenceThatNeverVerified…`; existing `TrustedJournalReceiptTests`] |
| Store outage | TR-5, RV-3 | Honoured only within Φ of the last good establishment, then `Unavailable`; never reused, never withdrawn | [T IP `AnOutageIsNeverLastKnownGood` ×3] |
| Unchanged head | RV-1, RV-2 | Re-established after every bound, however long it is unchanged | [T IP `AnUnchangedHeadIsReEstablishedAfterEveryBound` ×3] |
| Process restart | RV-9 | Empty; decides from the stores only; restored evidence verifies again (limit L-9) | [T IP `RestoredEvidenceStaysRefusedUntilARestart` ×3] |
| Concurrent requests and delivery | RV-7, GT-5 | One establishment shared by 32 concurrent accesses; a new head is a new key; the superseded head is successor-refused | [T IP `ConcurrentAccessesShare…` ×3; PG `WP7ConcurrentPermissionsDeliveryAndRecovery…`] |

### 3.3 The original F-1 attack, before and after

- **Before** (pre-remediation assemblies, WP-7 runs of record) [T-WP7: `WP7CachedReceiptsNeverAnswerANewHead…` ×3 variants, both runs]: one process; after the J4/J5/J6 change the cached process gate stayed `Ready` (characterized), a restarted process was `AwaitingArchive`.
- **Before, reproduced on the current tree**: mutation **MV-7A-01** restores the pre-remediation semantics (an attestation answers for the process lifetime); it is detected (§7).
- **After** [T: `WP7AF1ChangedEvidenceOfAnUnchangedHeadIsRefusedPastTheFreshnessBound`, 18 cases on PostgreSQL + Azurite, both runs of record; IP 27 + 27 cases]: with a controlled clock, the earlier attestation still answers at Φ − 1 ms (the bounded staleness RV-2 permits); at Φ + 1 ms the receipt is re-established and refused with `RECEIPT_EVIDENCE_WITHDRAWN`, for the head and the entry, and the real gate is `AwaitingArchive`. A fresh process is refused as well. With Φ = 0 the next access is refused [T `WP7AF1WithAZeroBound…` ×3].

### 3.4 Achieved guarantee (Development/Testing) and the production requirement

As A-9 §29.1. A running process honours a changed piece of evidence for at most Φ (1 s by default) plus one establishment's duration (measured in §9). The change is not detected *immediately*, and a restart forgets withdrawn evidence (L-9). An immediate guarantee needs locked version-level immutability of the archive and the verification store (so the change cannot happen at all), an independently anchored instance identity, and, below Φ, change notification bound to the gate. None is available in Development/Testing.

## 4. OI-DR-7: historical archive verification (VF-15, A-9 §29.2)

**Reproduction and cause.** In WP-7 a J4 replacement of an archived object below the trusted checkpoint passed every sweep and was found only by a re-baseline [T-WP7: `WP7OiDr7ArchivedObjectsBelowTheCheckpointAreNotReReadByTheFullSweep` ×3, both runs]. The cause was the governing contract, not a defect: VF-12/VF-13 enumerate every key and re-walk the AuthorityDb chain against the checkpoint digest, VF-3 reads only the object at `v`, and A-6 VC-10 states that archived objects at or below the checkpoint are not re-read. The full sweep passed `ByteCompareFromRevision = v + 1` to the comparer [S: `JournalVerifier.cs`, pre-WP-7A].

**Remediation.** The full sweep now byte-compares from revision 1 (VF-15) [S: `JournalVerifier.cs`]. Every archived object is read and compared byte for byte with its chain-verified row (VF-7), the archive-side chain (R-1..R-7) runs over all archived bytes, and the W2-S commitments are compared with the archived digests at every revision. The incremental sweep is unchanged (bounded listing and reads above `v`, IV-1..IV-6). Cadence (`T_inc` 5 min, `T_full` 24 h) and the checkpoint trust model are unchanged. Archive reads within a batch are prefetched with bounded concurrency (8) and processed in revision order [S: `JournalStreamComparer.PrefetchAsync`]. The baseline uses the same reader, so it benefits too.

| Attack below `v` (J4 unless stated) | Incremental sweep | Full sweep | Evidence |
|---|---|---|---|
| Object replaced (other bytes) | Clean (IV-5, unchanged) | `ANCHOR_MISMATCH` at the revision | [T PG `WP7AOiDr7…(replaced)` ×3; IP `AHistoricalArchivedObjectReplaced…` ×3] |
| Object deleted | Clean | `ARCHIVE_DISCONTINUITY` (unchanged, AE-4) | [T PG `(deleted)` ×3] |
| Digest substitution (another genuine entry's bytes at the key) | Clean | `ANCHOR_MISMATCH` (+ archive-side chain findings) | [T PG `(digest-substituted)` ×3] |
| Wrong object identity (another organization's genuine entry of the same revision) | Clean | `ANCHOR_MISMATCH` (+ envelope identity findings) | [T PG `(identity-substituted)` ×3] |
| Journal/archive disagreement (J2 rewrites the row, not re-chained) | — (row below `v` not walked) | `CHAIN_INTEGRITY_FAILURE` | [T PG `(row-disagrees)` ×3] |
| Commitment disagreement (J4 + J6, both copies consistently) | — | `DPLUS_COMMITMENT_MISMATCH` | [T PG `(commitment-disagrees)` ×3] |

Every case is a finding: no checkpoint advance and no quarantine (the verifier stays alert-only). No re-baseline is needed. Detection latency below the checkpoint is `T_full` + the full sweep's duration.

**Additional full-verification cost** (measured, §9). The full sweep now reads every archived object, where before it read only the object at `v`. Its cost becomes linear in history and comparable to a baseline, partly offset by the read-ahead.

## 5. J2 revocation-deletion assessment (package §5, A-9 §29.3)

The attack: a privileged AuthorityDb administrator (J2, triggers bypassed with `session_replication_role = replica`) deletes the newest authorization-stream entry, a revocation, and resets the organization's head columns so that AuthorityDb is internally consistent. Executed on PostgreSQL 18 + Azurite with the real gate, AuthorityStore runtime role and trusted receipts [T: `WP7AJ2RevocationDeletionPhasesOnTheRealGate`, both runs of record; `wp7a-observations.log`].

| | Phase 1: before the W2-S commitment | Phase 2: after the commitment, before delivery | Phase 3: after verified delivery |
|---|---|---|---|
| Protected access reopens? | **Yes**: the gate is `Ready` on the previous, genuinely delivered state | **No**: `AwaitingArchive` (RV-4: the commitment at r + 1 is successor evidence) | **No** for every evaluation whose establishment starts after the deletion (RV-4: the archived object at r + 1). A process holding a fresh attestation of the previous head answers for at most Φ + establishment: observed 821 ms and 711 ms (runs of record 1 and 2) |
| Revoked permission effective again? | The revocation **never became effective**: it was never delivered or projected, so the access that existed before it continues. This is not a newly regained permission | No. The gate stays closed, and SpiceDB (separately administered, not reverted by J2) is not consulted while closed [I] | No. The revocation was projected to SpiceDB before the deletion; J2 cannot revert SpiceDB [I; no SpiceDB in the gate stack], and the gate stays closed (above) |
| Prevented or detected | Neither | Prevented at the gate; detected by the incremental sweep (`DPLUS_COMMITMENT_MISMATCH/BEYOND_HEAD`) | Prevented at the gate (bounded as above); detected by the incremental sweep (`BEHIND_ARCHIVE`); the next change collides (permanent `ARCHIVE_BODY_MISMATCH` quarantine, unchanged) |
| Maximum demonstrated exposure | **Unbounded** (no external evidence ever exists) | None at the gate (fresh evaluation refused) | 821 ms and 711 ms (runs of record 1 and 2) in a long-lived process (bound Φ = 1 s + establishment); none for a fresh evaluation |
| Detection guaranteed? | No | Yes, within `T_inc` + incremental duration, while the commitment persists (J4 + J6 deleting it too is out of scope) | Yes, within `T_inc` + duration, while the archived object persists (J4 deleting it is out of scope) |
| Trusted evidence outside AuthorityDb used | None exists | W2-S commitment in both stores | Archived object (and commitments) |
| In the contract's scope? | J2 is in scope for detection only (§2, §27.4 limit, L-4); P-1 is the documented residual R-DR-1 / L-4 | Yes (RV-4, A-9) | Yes (RV-4, A-9) |

**Before A-9** (WP-7 runs of record): phases 2 and 3 reopened the gate until the next incremental sweep (≤ `T_inc` + duration; phase 3 additionally regained `Ready` on revision k − 1) [T-WP7: `WP7J2DeletionOfTheAuthorizationHeadExposureWindowIsCharacterized`]. RV-4 uses only stores the receipt model already trusts (archive, verification store); it adds no protocol step, service or trust assumption.

**Options for phase 1 (not implemented; owner decision D-7A-3).** Each requires evidence of the entry outside AuthorityDb **before** the authorization command is acknowledged:

| Option | What it would take | Why it is not within the approved contract |
|---|---|---|
| (a) Externally trusted authorization-change commitment | Write a W2-S-like intent to both stores before the command transaction commits; the gate refuses a head below the highest intent | A new commit-path step across stores, with failure states (intent written but transaction rolled back) that need reconciliation: a distributed protocol. Excluded by the package without explicit approval |
| (b) Independently established authorization freshness | An externally anchored authorization head (for example Confidential Ledger, CK-6) that the gate requires before `Ready` | A new trust dependency and production service; CK-6 is a production prerequisite (§14) |
| (c) Accept as residual for Development/Testing | Keep L-4 / R-DR-1 with production separation of identities (G14, PV-9) as the prerequisite | Owner decision; not assumed |

## 6. AC-DR-11 (package §6): the two-million-row measurement

### 6.1 Governing requirement

AC-DR-11 (§13): "Measured and recorded: D+ added latency per delivery; incremental and full sweep duration and archive reads/enumeration **on a 2M-row synthetic organization**; observed detection latencies against §8.6". OI-DR-6 fixes the dataset pattern: the E3-M2f-3G organization of 2,000,000 audit entries, plus 500,000 over four other organizations. A delivered organization under D+ (A-6) also carries a W2-S commitment per entry in both stores, so the dataset needs about 2.5M archived entries, 2.5M archive commitments and 2.5M store commitments: **7.5M blobs**.

### 6.2 The harness, and whether seeding can be accelerated equivalently

The pre-existing harness (`MeasureBaselineIncrementalAndFullSweep`) seeds through sequential archive appends without commitments: about 21 ms per row at 20k (WP-4A, WP-7), about 15 h for 2M, and not the D+ dataset. The new opt-in harness `MeasureAcDr11ScaleOrganization` [S: `JournalVerificationPostgresIntegrationTests.AcDr11.cs`; runner `scripts/dev/sec-bl-01-acdr11-scale-run.ps1`] produces every entry exactly as D+ leaves it:

- the Authority's `JournalChain.Append`;
- the real Blob adapter's create-only append with its digest check;
- D+'s own `DPlusEvidenceCommitments.Build`, generation 0, written create-only to both stores;
- an `Anchored` row with the archive's receipt.

Writes are issued with bounded concurrency in chunks, the only difference from D+, which delivers one entry at a time per pass. The baseline then re-verifies every row and object, which is the equivalence check of the seed. Measurements use the unmodified product code. The dataset characteristics, chain distribution, cryptographic operations, archive evidence and verification coverage are unchanged. I judge this accelerated setup equivalent; that judgement is the owner's to confirm.

### 6.3 Feasibility on this rig (executed probe)

Disposable stack (postgres:18.0, azurite:3.9.0 ×2, 16 CPUs, Docker memory 15.24 GiB, 55 GB free disk), run alone [T: `acdr11-probe-100k/` with `manual-samples.log`, `acdr11-progress.log`]:

| Observation | Value |
|---|---|
| Attempt 1 (32 concurrent writers, run concurrently with mutations; timings not of record) | Archive appends hit the adapter timeout in the first chunk (`ArchiveUnavailableException: archive timeout`); failed [T: scratch attempt, recorded in §12] |
| Attempt 2 (8 writers, alone): chunk 1 (10k rows = 30k blob writes, each also preceded by a create-container request) | ≈ 5 min ≈ 33 rows/s |
| Chunk 2 (rows 10,001–20,000) | ≈ 11 min ≈ 15 rows/s (degrading) |
| Archive Azurite CPU | 100–119 % (one Node.js thread: the bottleneck) |
| Azurite metadata (LokiJS, all in one in-memory database, persisted as one JSON file) | 36.85 MB for 40,167 archive blobs (≈ 917 bytes per blob); 17.4 MB for ≈ 20k store blobs |
| Stopped | At 20,000 target rows, by the implementer, on the evidence below |

**Not feasible on Azurite 3.9 on this rig.**
- For 7.5M blobs the archive's metadata file would be ≈ 4.6 GB and the store's ≈ 2.2 GB [extrapolated from the measured bytes per blob]. LokiJS serializes each database to a single string. A V8 string is limited to about 2^29 characters (≈ 0.5 GB), so persistence fails well before 2M rows (from about 0.55M archive blobs ≈ 0.27M rows) [I: V8 limit, not exercised].
- Throughput falls as the database grows, because every save rewrites the whole file: 33 → 15 rows/s between the first two chunks [T].
- Even at the first chunk's rate, seeding 2.5M rows needs ≈ 21 h.
- Independently of the emulator, F-7A-5 (§6.5) means one incremental sweep of a 2M-entry D+ organization reads ≈ 4M commitment objects sequentially, ≈ 23 h at the measured 21 ms per read [I from the measured per-read cost].

### 6.4 Requirements and plan for an executed 2M measurement (not started)

| Item | Requirement |
|---|---|
| Blob store | Not Azurite 3.9 with LokiJS. Either (a) an Azurite release with an external SQL metadata store (`AZURITE_DB`, MySQL) in Docker, or (b) a real Azure Storage account in Canada Central (**not authorized**: paid Azure resource). (a) changes the emulator configuration from the gate's pinned image and needs owner approval as the reference environment |
| Machine | 16 CPUs adequate; ≥ 32 GB RAM recommended for (a) (MySQL, PostgreSQL ≈ 3 GB of journal rows, the test process with full-sweep listings ≈ 1 GB [I]); ≥ 60 GB free disk (blobs ≈ 7 GB, MySQL ≈ 10 GB, PostgreSQL ≈ 5 GB, headroom); this rig has 15.24 GiB for Docker and 55 GB free |
| Duration (this rig, (a)) [extrapolated, not measured] | Seeding ≈ 7–21 h (300 → 100 writes/s for 7.5M writes); baseline and full sweep ≈ 3–4 h each at the measured ≈ 190 reads/s with read-ahead (2.0M entries), plus the commitment reads; each incremental sweep ≈ 20 h of commitment reads at the per-object cost observed at 10k rows (≈ 18.7 ms; F-7A-5); total ≈ 1.5–2 days, resumable only per phase (the seed is not resumable mid-chunk) |
| Cost of (b) [estimate; Azure list prices not verified in this session] | ≈ 7.5M write operations + ≈ 20M read and list operations: of the order of USD 50–150 in operations, negligible storage; plus a run host |
| Plan | 1. Approve the store option. 2. `sec-bl-01-acdr11-scale-run.ps1 -Rows 2000000 -Concurrency 8` with the chosen store. 3. Keep the evidence directory (progress log, stats, manifest with assembly hashes). 4. If F-7A-5 is remediated first, the incremental figures change. Measure after the remediation decision |

### 6.5 Finding F-7A-5: every sweep reads every W2-S commitment of the stream

`DPlusCommitmentInspection.LoadAsync` lists the stream's whole commitment prefix in both stores and reads every object, one at a time, on **every** sweep, incremental included [S: `BlobSecurityEventArchive.ListMetaAsync`, `BlobJournalVerificationRecordStore.ListAsync`]. The incremental sweep's fixed cost is therefore linear in delivered history. This contradicts the intent of IV-1 (incremental cost bounded by new rows) once a stream has D+ history. The 20k measurements of WP-4A and WP-7 did not show it, because their seeded history had no commitments. Measured on executed data points: §6.6.

Remediating it means inspecting only commitments above the trusted checkpoint in the incremental sweep, with the full sweep covering the rest. That changes A-6 §26.7 (EP-5, VC-1..VC-9 scope). It is a contract change, so it is not made here (owner decision D-7A-5).

### 6.6 Executed data points (not the acceptance measurement)

Same harness at small sizes, final assemblies, run alone (except the 10k run's last two detection steps, which overlapped the final in-process suites) [T: `acdr11-2000/`, `acdr11-10000/`, each with `acdr11-progress.log`, `run-summary.json`]. Each dataset is a D+-delivered audit stream with W2-S commitments, plus a quarter of the target row count over four other organizations, in the same archive. Every seed passed a clean baseline.

| AC-DR-11 quantity | 2,000 rows | 10,000 rows | Notes |
|---|---|---|---|
| Seeding (target) | 28.8 s (69 rows/s) | 300 s (33 rows/s) | The emulator slows as it grows (§6.3) |
| Baseline (BL-1) | 39.8 s | 677.8 s | Clean: the seed's equivalence check |
| **Idle incremental sweep** (nothing new) | **24.2 s** (1 entry read; the rest commitment reads) | **374.1 s** | **F-7A-5:** linear-or-worse in history (15× for 5× the rows); exceeds `T_inc` = 5 min at 10k already |
| D+ added latency per delivery (200 new entries on the large organization) | 71.0 ms per entry (7 passes) | 143.5 ms per entry (7 passes) | D+ per-entry work is constant (5.2 operations); the growth is the emulator's |
| Incremental sweep (+200) | 28.6 s (200 listed, 201 read) | 388.4 s | Dominated by the commitment reads (F-7A-5) |
| Full sweep (VF-15) | 53.9 s (2,201 read, 4,400 listed) | 835.4 s (10,201 read, 20,400 listed) | |
| Detection above the checkpoint (replaced newest object) | 27.8 s (incremental; `ANCHOR_MISMATCH` + `DPLUS_COMMITMENT_MISMATCH/ROW_DIGEST`) | 380.6 s | Latency = `T_inc` + this duration |
| Detection below the checkpoint (replaced middle revision) | 54.3 s (full; `ANCHOR_MISMATCH` + `LINK_MISMATCH` at the successor) | 865.0 s (full; `ANCHOR_MISMATCH` + `LINK_MISMATCH`) | Latency = `T_full` + this duration; requires VF-15 |
| Forged receipt (J2 `Anchored` without an object) | 27.9 s (incremental; `FAKE_RECEIPT`) | not run (stopped by the implementer after the previous step; not essential) | |
| Peak working set | 266 MB | 329 MB | |

**AC-DR-11 remains INCONCLUSIVE.** No 2M-row measurement was executed, and no extrapolation is offered as a substitute.

## 7. Mutations (package §7)

Driver `scripts/dev/wp7-mutations.py` (the WP-7 driver plus MV-7A-01..10 and three PostgreSQL baselines). It runs on a scratch clone of the working tree; each edit is anchored exactly once, the build is clean, the declared check runs, and the original bytes are restored and SHA-256-verified [T: `mutations/summary.md`, `attempt-1/`, `attempt-2/`, 51 executions, all `restored: true`].

| ID | Mutation | Check | Verdict |
|---|---|---|---|
| MV-01..MV-18, MV-X1 | The WP-7 set, re-run on the WP-7A product code (MV-16 anchor shortened, edit unchanged) | IP / arch / PG | All DETECTED |
| MV-7A-01 | RV-2 ignored: an attestation answers for the process lifetime (the pre-remediation TR-6) | IP | DETECTED (9 tests) |
| MV-7A-01-PG | Same, on real stores | PG `*WP7AF1*` | DETECTED |
| MV-7A-02 | RV-6: withdrawn evidence not remembered | IP | DETECTED |
| MV-7A-03 | RV-6: a changed evidence identity accepted when the new evidence verifies | IP | SURVIVED in attempt 1 → test added (`generation-substituted-with-stop`) → DETECTED |
| MV-7A-04 | RV-3: a refusal or outage reused within the bound | IP | Attempt 1 was an equivalent mutant (refusals are also removed from the cache) → redefined to disable both guards → DETECTED |
| MV-7A-05 / -05-PG | RV-4: successor evidence above a head ignored | IP / PG `*WP7AJ2*` | DETECTED |
| MV-7A-06 | The gate asks for an entry receipt instead of a stream-head receipt | IP | SURVIVED in attempt 1 → gate test added (`TheGateRefusesAHeadWhoseSuccessorIsArchived`) → DETECTED |
| MV-7A-07 | RV-5: stop markers ignored | IP | DETECTED |
| MV-7A-08 | RV-5: generation g ≥ 1 accepted without a stop of g − 1 | IP | DETECTED |
| MV-7A-09 | RV-2: the 30 s ceiling not enforced | IP | DETECTED |
| MV-7A-10 / -10-PG | VF-15: the full sweep byte-compares only above the checkpoint (pre-remediation OI-DR-7) | IP / PG `*WP7AOiDr7*` | DETECTED |

Baselines: in-process (417, then 424 tests), architecture and RESTORED PASS. BASELINE-PG (`*ReadOnly*`) **failed in attempt 1**: a pre-existing test-order dependency in `G08TheDedicatedVerifierLoginIsReadOnlyAndRunsTheVerifier` (the schema is missing under a method filter), now fixed in the test. Attempt 1's MV-15b verdict is therefore invalid; it was re-run in attempt 2 with BASELINE-PG, BASELINE-PG-F1 (21), -J2 (1) and -OIDR7 (18) all PASS: DETECTED.

## 8. Finding F-7A-4: a pre-existing G14-R2 recovery race, exposed by changed timing (owner disposition)

**Observed.** After the WP-7A changes, `AuthorizationStreamGateDPlusTests.ConcurrentPermissionChangesRecoveryAndDeliveryIntroduceNoUnauthorizedAccess` failed intermittently: 4 of 6 full-suite runs; 0 of 40 when run alone, even under 8 parallel processes. Its security assertions held: no unauthorized `Ready`, no quarantine. Its liveness assertion failed: the recovered revision stayed `VerificationFailed` / `DPLUS_PREDECESSOR_MISSING` (attempt count 2), the stream stayed stopped, and no generation-1 stop or finding existed [T: `flaky` diagnostics in this record's attempt history].

**Bisect** [T]:

| Configuration | Full-suite failures |
|---|---|
| Archive read concurrency 1 | 3 of 8 |
| Receipt cache "forever" and the gate on entry receipts (pre-WP-7A behaviour) | 0 of 8 |
| Real WP-7A code, the new validity test class excluded | 0 of 8 |

The new receipt code only reads the stores. It changes timing: the gate's evaluation holds the organization row lock longer while it re-establishes.

**Mechanism** [S + deterministic test]:

1. The G14-R2 executor writes the row hint (A-4 EX-1 step 5).
2. A concurrent D+ pass's stop discovery then loads the ledger before the execution record exists (step 6), so the stop is still unresolved. SM-4 re-marks the hinted row with its recorded classification.
3. The executor writes the execution record and reports `Done`.
4. EX-6 now holds the stream, because the lifted revision is not archived and is no longer pending.

The failure is closed: no Allow, no delivery, no quarantine. The contract's remedy, a re-run of the executor (EX-1: "a re-run resumes idempotently"), resumes the recovery and delivery completes.

**Evidence.**
- New deterministic test `F7A4ADeliveryPassBetweenTheRowHintAndTheExecutionRecordHoldsTheStreamUntilTheExecutorIsReRun` (runs a D+ pass exactly at the step-6 boundary).
- The concurrent test now applies the EX-1 re-run only when the race occurred and reports it. Its security assertions are unchanged.

**Not changed.** Fixing the race needs one of:
- a different executor order;
- SM-4 skipping rows whose hint names a decided recovery;
- D+ re-scheduling an executed lift's revision.

Each changes G14-R2 (A-4 EX-1/EX-6 or A-3 SM-4) and needs an owner decision (D-7A-4).

## 9. Performance (package §8, outstanding deliverable of the WP-6/WP-7 package)

The WP-7A package text ends at §8 "Consolidate integrated…"; the WP-6/WP-7 package's §8 ended at "Distinguish observed measurements…". Both were read as: consolidate the integrated measurements of the representative workload (§8 items of the WP-6/WP-7 package) on the final WP-7A code and distinguish observed values from extrapolations. If further text existed, it was not received.

Measurement runs on the final WP-7A assemblies, disposable PostgreSQL 18 + Azurite, executed alone [T: `measurement/` (gate `-Measure -MethodFilter *Measure* -PerformanceRows 20000 -CheckpointCount 2016`: 4 passed, 1 opt-in skip), `measurement-maxage0/` (`SECBL01_RECEIPT_MAX_AGE_MS=0`, receipt measurement only: 1 passed), `measurement-host.log`]. Earlier figures are the WP-7 measurement run [T-WP7]. "Observed" = executed now; [extrapolated] = arithmetic on observed values, not a measurement.

| Metric | Observed (WP-7A) | WP-7 (before) | Notes |
|---|---|---|---|
| D+ delivery, all three streams (drain throughput, backlog B per stream) | B = 32: **9.5** entries/s; 256: **9.4**; 2,048: **7.7** | 10.3 / 10.1 / 8.1 | Unchanged by WP-7A (D+ path not modified); 5.22–5.25 store operations per entry, as before |
| Delivery steady state (one entry per stream per pass) | pass p50 **668** / p95 1,503 ms | 645 / 1,390 | Unchanged |
| Gate evaluation, Φ = 1 s: within the bound ("warm") | p50 **4.12** / p95 5.04 / p99 5.83 ms | 3.64 / 4.54 / 4.78 | Pending-count-only reference 1.97 ms |
| Gate evaluation, Φ = 1 s: establishment by a fresh verifier ("cold") | p50 **126.2** / p95 150.1 / p99 170.3 ms; **10** store operations (was 6: + successor object, + two successor commitment listings, + stop markers) | 110.7 / 134.2 / 156.8 | The fresh-verifier figure includes new HTTP connections to Azurite |
| Gate evaluation, **Φ = 0** (re-established on every access, same verifier) | p50 **21.5** / p95 28.1 / p99 62.9 ms; 803 establishments for 803 accesses | — | The cost of strict per-access validation: ≈ 19 ms over the count-only reference (reads are parallel) |
| New head (20 heads), Φ = 1 s | first evaluation p50 126.3 / p99 849.7 ms; second p50 3.52 ms | 115.2 / 805.3; 3.80 | GT-5 unchanged |
| Concurrent first check, 16 workers × 25, Φ = 1 s | warm: 947.8 evaluations/s, p99 13.6 ms; cold (fresh verifier per worker): **7.9** evaluations/s, p50 1,947 / p99 **3,198** ms | warm 1,053/s p99 13.0; cold 239/s, p99 1,389 | Cold is slower than WP-7: 10 operations per establishment, no single-flight across verifiers (R-G8-8) |
| Concurrent, Φ = 0 | 74.5 evaluations/s, p50 199.9 / p99 340 ms (warm and cold alike) | — | Every access reaches Azurite: the single-threaded emulator bounds throughput |
| Content release decision (checkpoint lookup + receipt) | Φ = 1 s: warm p50 **1.97** ms, cold p50 **89.9** ms; Φ = 0: p50 **11.6** ms | 1.88 / 111.1 | Excludes HMAC/manifest verification of the content (unchanged); CR-1 binding 0.002 ms |
| Evidence-commitment growth | unchanged: one object per delivered entry in each store (WP-7: ≈ 1,490 bytes per entry in total); 2 commitment objects per entry were also measured in §6.3 at ≈ 900 bytes of emulator metadata each | 1,480–1,496 B | Not re-measured as a separate figure |
| Checkpoint growth | 620.9 bytes mean per checkpoint; 2,016 = 1.25 MB; ≈ 196 MB per organization per year at 289 per stream per day [extrapolated] | identical | Unchanged |
| Checkpoint discovery | head + predecessor **895 ms**; full chain (2,016) **23.1 s** | 374 ms; 13.1 s | Same code; run-to-run environment variance on this rig (both linear in chain length) |
| Recovery-history discovery (D+ pass) | clean stream p50 **236.9** ms; after one executed recovery p50 **515** / p95 1,384 ms | 234; 503 / 1,234 | Unchanged |
| Baseline (20,000 rows) | **407.9 s** (20,000 reads; ≈ 20.4 ms per object) | 427.8 s | Read-ahead (8) gives no speedup at this size: the emulator is the bottleneck. At 2,000 rows the same code took 10.7 s (5.3 ms per object) [T dev run 4] |
| Incremental sweep, 500 new rows over 20,000 | bounded **11.4 / 12.6 s**; unbounded 14.2 s | 12.0 / 12.5; 13.8 | IV-1 unchanged. Seeded history has no commitments (F-7A-5 not visible here; see §6.6) |
| **Full sweep (21,500 rows), VF-15** | **461.3 s**; 43,000 keys listed, **21,501 objects read** | 5.6 s; 1 object read | **The additional full-verification cost of OI-DR-7:** ≈ 456 s at 21.5k rows (≈ 21 ms per historical object), linear in history; at the 24 h cadence a full sweep stays far below `T_full` at this size; 2M rows ≈ 12 h on this rig [extrapolated] |
| Memory | peak working set 276 MB; 2.03 GB managed allocations over baseline + sweeps | 232 MB; 1.52 GB | Full sweep now allocates every object body transiently |
| Detection latency (§8.6) | above the checkpoint ≤ 5 min + incremental duration (≈ 12 s here); below it ≤ 24 h + full duration (461 s here), now including replaced objects; J2 phase 3 at the gate ≤ Φ (711–821 ms observed) | — | Configuration plus measured durations |

## 10. Regressions and verification coverage (package §7)

### 10.1 Suites (final assemblies)

| Suite | Result | Evidence |
|---|---|---|
| PostgreSQL 18 + Azurite runs of record (all journal-verification PG/Azurite classes; both F-1, OI-DR-7, J2 tests included; original W-1/W-2 scenarios via EG-1..EG-3 and ADV-01..ADV-20) | Run 1: 203 total, 199 passed, 0 failed, 4 opt-in skipped (10:49–10:56 UTC); run 2 identical (10:56–11:09 UTC). Same executed assemblies: Tests `7630dfaa…`, Application `c8200792…`, Infrastructure `4a7f5867…`, Domain `e298b4c9…` | [T `pg-run-1/`, `pg-run-2/` with `run-summary.json`, `console.txt`, `wp7a-observations.log`] |
| Restart-phase pair (store restart between phases) | before-restart 9/9, after-restart 1/1 | [T `restart-pair/`] |
| SEC-P01..SEC-P12 (security-test stack, Vault Transit) | Attempt 1: 12/13 (SEC-P12, F-7A-3); attempt 2: **13/13**; images restored and verified (5/5); stack state 0 of 16 organizations quarantined, G08 triggers present | [T `sec-p-attempt-1/`, `sec-p/`] |
| Full Authority suite (in-process; final assemblies, the same as the runs of record) | 2,036 total, 1,825 passed, **0 failed**, 211 skipped (§10.2) | [T `authority-full.txt`] |
| Architecture | 227/227 | [T `architecture.txt`] |
| Contract | 958 total, 946 passed, 0 failed, 12 skipped (Vault opt-in, unchanged) | [T `contract.txt`] |
| E5 Vault and OBS-1 opt-in | 4/4 and 4/4 | [T `opt-in/`] |
| Strict specification validation | `scripts/validate-specifications.py --strict`: PASS after refreshing the manifest digests of the amended tracked documents and adding this record | [T `spec-validation.txt`] |
| Mutations | All DETECTED against passing baselines (§7) | [T `mutations/`] |

### 10.2 Skipped tests: 69 (G08) → 175 (WP-7) → 211 (WP-7A)

In the plain Authority suite, tests that need a disposable stack or an opt-in variable skip; they are executed by the dedicated runs. The increase from 69 to 175 is exactly the 106 WP-7 PostgreSQL/Azurite cases (`JournalVerificationPostgresIntegrationTests` 43 → 149; every other category unchanged) [T: G08 `authority-final.txt` vs WP-7 `authority-full.txt`, per-class counts]. WP-7A: 149 − 7 superseded WP-7 cases + 42 WP-7A cases + 1 opt-in AC-DR-11 = 185 PostgreSQL cases.

| Category (plain run) | Count | Reason | Executed by | Affects acceptance? |
|---|---|---|---|---|
| `JournalVerificationPostgresIntegrationTests` | 185 | `SECBL01_*` not set | Both PG/Azurite runs of record: 199 passed of 203; the 4 skips there are below | No: executed |
| `JournalBoundedListingAzuriteTests`, `JournalVerificationAzuriteIntegrationTests` | 9 + 9 | `SECBL01_*` not set | The runs of record (these classes included), except one restart phase | No: executed |
| `JournalVerificationAzuritePersistenceTests` + `PersistenceFixtureIsWrittenBeforeTheRestart` (the restart-phase pair) | 1 + (1) | Two-phase test needing a store restart (`SECBL01_PHASE`) | `scripts/dev/sec-bl-01-wp2-azurite-integration.ps1`: before-restart 9/9 (the fixture included), after-restart 1/1 [T `restart-pair/`] | **Required** (archive, sentinel and checkpoint survival across a store restart underpins CK-4/restart claims); it was skipped in the WP-7 runs of record and is now executed |
| `IntegrityKeyActivationVaultIntegrationTests` | 4 | `E5_VAULT_*` | `scripts/dev/e5-vault-hmac-integration.ps1`: 4/4 [T `opt-in/e5-vault.txt`] | Not SEC-BL-01 scope (E-5); executed for completeness |
| `ArchiveDeliveryOutageCharacterizationTests` | 3 | Opt-in timing-dependent (`SECBL01_OBS1_CHARACTERIZE=1`) | Executed with the variable: 4/4 [T `opt-in/obs1-characterization.txt`] | Characterization (A-1 OBS-1); executed |
| Runs of record: `WP5BDPlusDeliveryMeasurement`, `G08ReceiptMeasurement`, `MeasureAcDr11ScaleOrganization` | 3 | Opt-in measurements | The measurement runs (§9) | Measurements, not acceptance tests |

No mandatory verification is excluded.

## 11. Findings and owner decisions

### 11.1 Findings

| ID | Finding | Action | Owner decision? |
|---|---|---|---|
| F-1 (WP-7) | TR-6 cache survived evidence changes for an unchanged head | **Remediated**: TR-6′ (A-9 §29.1), §3 | Residual window Φ + establishment and L-9 (D-7A-1) |
| OI-DR-7 (WP-7 O-3) | Historical replacements below `v` were not detected by sweeps | **Remediated**: VF-15 (A-9 §29.2), §4 | Formal closure of OI-DR-7 (D-7A-2) |
| O-1/O-2 (WP-7) | J2 revocation deletion | Phases 2 and 3 now **prevented at the gate** (RV-4); phase 1 unchanged, §5 | Phase 1 option (D-7A-3) |
| F-7A-1 | Per-test `JournalVerificationReader` pools were never disposed; once the run became 3× faster, PostgreSQL refused connections (53300: 33 failures in development run 3) | Test harness fixed (one shared reader; observer readers disposed). No product change | No |
| F-7A-2 | `G08TheDedicatedVerifierLoginIsReadOnly…` depended on another test creating the schema (failed under a method filter) | Test fixed | No |
| F-7A-3 | SEC-P12 expected a share command to be **accepted** during an archive outage. Under A-9 RV-3 an outage longer than Φ closes the gate, and the command is refused (`503 AUTHORITY_NOT_READY`) instead of accepted on stale evidence | SEC-P12 updated to assert the security property under either outcome and to re-issue a refused command after recovery; both branches observed [T `sec-p-attempt-1/`, `sec-p/`]. **Availability consequence:** an archive or verification-store outage longer than Φ now blocks every protected access and command of the organizations whose gate is evaluated, where the cached receipt previously kept unchanged heads open | **Yes (D-7A-6)**: accept the availability consequence, or approve a different outage policy (any grace period re-opens F-1 for its duration) |
| F-7A-4 | Pre-existing G14-R2 race (hint vs execution record; SM-4) holds the stream until the executor is re-run | Characterized (deterministic test); concurrent test applies the EX-1 remedy only when the race occurs, visibly. No product change, §8 | **Yes (D-7A-4)** |
| F-7A-5 | Every sweep, incremental included, reads every W2-S commitment of the stream (cost linear in history) | Characterized, §6.5; not changed (A-6 §26.7 scope) | **Yes (D-7A-5)** |
| F-7A-6 | The AC-DR-11 harness cannot reach 2M on Azurite 3.9 (single-file metadata; throughput degrading) | Probe executed and stopped; requirements and plan §6.4 | **Yes (D-7A-7)**: approve a store option for a 2M run |

### 11.2 Owner decisions requested

| ID | Decision |
|---|---|
| D-7A-1 | Accept TR-6′ (A-9 §29.1) for Development/Testing with Φ = 1 s (or choose another Φ ≤ 30 s, including 0), the residual window Φ + establishment duration, and L-9 (restart forgets withdrawn evidence) |
| D-7A-2 | Accept VF-15 and formally close OI-DR-7 for Development/Testing |
| D-7A-3 | J2 phase 1 (§5): option (a) external pre-acknowledgement commitment, (b) externally anchored authorization freshness, or (c) accept as a Development/Testing residual with production separation as the prerequisite |
| D-7A-4 | F-7A-4: remediate (executor order, SM-4 exception for decided recoveries, or D+ re-scheduling an executed lift), or accept the operator re-run as the documented remedy |
| D-7A-5 | F-7A-5: bound commitment inspection in incremental sweeps to revisions above the trusted checkpoint (A-6 amendment), or keep the full inspection |
| D-7A-6 | F-7A-3: the availability consequence of RV-3 during store outages, and the SEC-P12 test change |
| D-7A-7 | AC-DR-11: authorize a 2M-row run with an approved blob store (§6.4), or keep AC-DR-11 INCONCLUSIVE |
| D-7A-8 | Accept the equivalence of the accelerated AC-DR-11 seed (§6.2) |
| — | Unchanged and not requested again here: M2F3-43 reclassification (D-1), AC-DR-13 (D-6) |

SEC-BL-01 and PD-15 remain **OPEN**; M2F3-43 remains **FAILED**; AC-DR-11 **INCONCLUSIVE**; AC-DR-13 pending.

## 12. Attempt history (preserved)

| Attempt | What happened |
|---|---|
| Development run 1 (PG) | Invalid parameter `-PerformanceRows 0` (the measurement test needs rows: a wrong invocation) and 53300 connection exhaustion (F-7A-1) |
| Development run 2 | My `-Class` list was passed through `powershell -File` as one string and the console redirect collided with the script's own `console.txt`; no tests executed; containers removed |
| Development run 3 | 202 total, 33 failed, all 53300 (F-7A-1) |
| In-process suite after the receipt change | 3 legitimate-recovery tests failed: my first RV-5 rule required the stop at the same revision; recoveries give every later commitment the next generation. Rule corrected to "a stop of g − 1 at or below the revision" (consistent with VC-5) |
| Full suite, repeated | `ConcurrentPermissionChanges…` failed intermittently (F-7A-4): bisect, diagnostics, deterministic test |
| Development run 4 | 202 / 199 / 0 failed / 3 skipped |
| Mutations attempt 1 | BASELINE-PG failed (F-7A-2), MV-15b invalid; MV-7A-03, -04 (equivalent), -06 survived |
| Mutations attempt 2 | All DETECTED, baselines PASS |
| AC-DR-11 probe 1 (32 writers, concurrent with mutations) | Archive append timeouts in the first chunk; failed; scratch only |
| AC-DR-11 probe 2 (8 writers, alone) | Stopped by me at 20,000 rows on the infeasibility evidence (§6.3) |
| Mutation driver | MV-7A-01 as first written would not have built (CA1822: the method no longer read instance state); corrected before running |
| Scale runner | My first patch left the frozen-copy variable pointing at the repository's build output, and its cleanup would have deleted it; caught by inspection before any run, fixed, and a guard added (the copy must be under the temporary directory) |
| SEC-P attempt 1 | 12/13: SEC-P12 (F-7A-3) |
| SEC-P attempt 2 | 13/13 |

## 13. Not claimed

- No production assurance, Azure evidence, store immutability or independence, independent anchoring, independent review or certification.
- No immediate detection of evidence changes (Φ-bounded only), and no memory of withdrawn evidence across a restart.
- No prevention or detection of J2 phase 1; no protection against JP or J4 + J6 collusion.
- No 2M-row measurement: AC-DR-11 INCONCLUSIVE.
- No reclassification of M2F3-43, no closure of SEC-BL-01, PD-15, G14, OI-DR-7 (formal), OI-DR-9, DD-10 (production) or AC-DR-13.
- No Stage B run; no persistent environment touched; no commit, push, merge or deployment.
