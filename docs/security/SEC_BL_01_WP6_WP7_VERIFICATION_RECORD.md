# SEC-BL-01 / PD-15 — WP-6 / WP-7 Integrated Security Verification and Formal Acceptance Assessment

**Status (2026-11-18/19):**

| Item | State |
|---|---|
| Increment | WP-6 (contract §17: M-6, delivered as G08 under A-7) and WP-7 (contract §13: integrated adversarial verification, mutations, regressions, measurements). Development/Testing only |
| Authorization | Owner decision **C-159** (2026-11-18), contract amendment **A-8** (§28, dispositions only) |
| Owner acceptance | **Pending.** Nothing here is a formal acceptance; M2F3-43 is **not** reclassified (§7) |
| Not done | Stage B (not authorized; not run); WP-8 production prerequisites; any persistent environment, persistent baseline or production infrastructure; CI workflow changes; commit, push, merge or deployment |
| Baseline | `main` at `a9b32de` with every uncommitted WP-0..WP-5b, G14-R1/R2, G08 and C-155..C-158 change preserved. Contract §1–§20 unchanged (hash method of the C-157 record: `3549e3ba…f8f2`, verified before and after A-8) |
| SEC-BL-01 / PD-15 / M2F3-43 | OPEN / OPEN / **FAILED** (owner decision outstanding; proposed technical result in §7) |
| Review | Owner-led; prepared by the implementing assistant; **not an independent security review** and not a certification |
| Dated note (2026-11-19, C-160) | The owner accepted WP-6 and this record's executed evidence (not formal WP-7 acceptance) and decided D-1..D-6 as recorded in C-160. F-1 and OI-DR-7 were remediated and J2 reassessed in WP-7A (`SEC_BL_01_WP7A_VERIFICATION_RECORD.md`, contract A-9). Three characterization tests of this record (WP7-PG-04b, WP7-PG-09a, WP7-PG-09c) pinned the pre-remediation behaviour and were superseded there; their results above remain the executed pre-remediation evidence. The text below is unchanged |

Evidence tags: **[T]** executed test evidence in `docs/evidence/sec-bl-01-wp6-wp7/`; **[S]** source inspection; **[I]** inferred; **[NV]** not verified. Source inspection is not test evidence.

---

## 1. Owner decisions recorded (C-159, A-8)

| # | Decision | Where recorded |
|---|---|---|
| 1 | G08 accepted for Development/Testing | C-159; A-8 A8-1; G08 record status |
| 2 | E-6 closed for Development/Testing against the J1 runtime database credential threat model | C-159; A8-2 |
| 3 | E-7 closed for Development/Testing against the same threat model | C-159; A8-3 |
| 4 | J2, compromised-runtime (JP) and dual-store (J4 + J6) limitations retained | C-159; A8-4 |
| 5 | Qualified CF-1 (C-158) and DD-10 (A-5) dispositions retained | C-159; A8-5 |
| 6 | Combined WP-6 / WP-7 package authorized (this record) | C-159; A8-6 |

A-8 records dispositions only. §1–§20 hash verified unchanged before and after the amendment [T: `awk 'NR==15{next} /^## 21\./{exit} {print}'` → `3549e3ba280fc851…6addf8f2`].

---

## 2. Governing contract reconciliation

**Reviewed:** contract §1–§27 and A-1..A-7 (`SEC_BL_01_DR_CONTRACT_CLOSURE.md`); the original acceptance contract §13 (ADV-01..ADV-20, MV-01..MV-18, EG-1..EG-3, AC-DR-01..AC-DR-14); PD-15 (`E3_M2F3_CONTRACT_CLOSURE.md` §10.5, §14.1); the M2F3-43 failure evidence (`E3_M2F3_FU_VERIFICATION_RECORD.md` §2.3); the W-1/W-2 attack scenarios (`E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md` §10); the WP-0..WP-5b, G14-R1, G14-R2, P2 readiness and G08 records; open items OI-DR-1..OI-DR-12, OD-R2-1/2, SR-OD-*, R-DR-1..R-DR-11, R-G8-1..R-G8-8.

**No original acceptance requirement was narrowed.** Where an amendment already governs a §13 expectation, the amendment's rule is applied and stated. The contradictions and interpretations found:

| ID | Contract text | Implemented / observed behaviour | Consequence |
|---|---|---|---|
| CT-1 | Package §3 describes WP-6 as journal completeness, checkpoints, enumeration, commitments, … | Contract §17: **WP-6 = M-6** (§11), whose design A-7 §27.4 superseded and G08 delivered | WP-6 is satisfied by the G08 increment (§4). The package's WP-6 checklist covers WP-4..WP-5b properties; each is traced in §4.2. Recorded as A-8 A8-6 |
| CT-2 | ADV-01: `REVISION_GAP` "within `T_inc`" | A-1 IV-5: a change **below** the trusted checkpoint is detected only by the full sweep (≤ `T_full` + duration) | Executed in both positions: above the checkpoint within `T_inc`; below it the incremental sweep is asserted Clean and the full sweep detects. A-1 governs (amendment precedence) |
| CT-3 | EG-2 and ADV-05: expected `ANCHOR_MISMATCH` | Above the checkpoint: `ANCHOR_MISMATCH`. Below it, a re-digested row that is not re-chained is reported as `CHAIN_INTEGRITY_FAILURE/LINK_MISMATCH` at its successor (R-7); rows below `v` are not byte-compared with the archive (VC-10) | Detection holds; the code differs from §13.5's expectation. No rule change proposed |
| CT-4 | ADV-02 / ADV-07: "on a D+ stream, D+ `LINK_MISMATCH` at the next delivery" | Holds when J2 also marks the forged rows `Anchored`. When the forged rows are left `Pending`, D+ re-evaluates forged revision 6, EP-4 applies and the existing `ARCHIVE_BODY_MISMATCH` collision quarantine fires (DP-10) | Both variants executed per stream (`ADV-02`, `ADV-02-anchored`, `ADV-07`, `ADV-07-anchored`). Both fail closed; the Pending variant is stricter (permanent quarantine) |
| CT-5 | Package §5: "archive-instance changes invalidate affected assurance" | A-7 TR-6 permits a per-process receipt cache keyed by (organization, stream, revision, row digest); a cache hit does not re-run TR-2..TR-4 | **Finding F-1** (§11). The implementation matches TR-6; the package expectation is stricter. Remediation would change TR-6 → owner disposition required; not changed here |
| CT-6 | §13.1: PG layer "with Vault Transit" | The PG/Azurite gate stack has no Vault Transit. Journal delivery, D+, receipts, the verifier and recovery use no MAC provider. Vault Transit is in the SEC-P stack | Same deviation as every WP-4..G08 run; recorded, not hidden |
| CT-7 | AC-DR-11: measurements "on a 2M-row synthetic organization" | Not executed at 2M rows (§10). Measured at 2,000 and 20,000 rows; 2M figures are extrapolations | AC-DR-11 **INCONCLUSIVE** as written |
| CT-8 | ADV-11: "planted AuthorityDb hint or stale checkpoint reference" | No checkpoint channel exists in AuthorityDb (CK-11). The only AuthorityDb hints are head columns and delivery state, both tested; MV-07 (anchor from AuthorityDb) is detected | Executed in its expressible form |
| CT-9 | MV-15 / MV-17 ("verifier writes AuthorityDb", "baseline repairs a row") | Not expressible as written: neither has a write port. **Finding F-3:** no architecture test guarded the `Reconciliation` namespace | Expressible variants (a: dependency, b: read-write snapshot / archive repair) executed after a boundary test was added |
| CT-10 | ADV-19 layer "IP" only | Also executed on PostgreSQL + Azurite | Stronger than required |
| CT-11 | Package §4: "process termination during critical transitions" | Simulated by fault-injecting store and archive wrappers and cancellation at the transition boundaries, inside one test process. No OS-level process kill | Stated as such in §5 |
| CT-12 | ADV-14: "legitimate restore copy (J3)" | Row-level equivalent of a restore to an earlier revision (rows and head as of that revision); not a physical PITR | Stated as such |
| CT-13 | SM-5/SM-6: stop-record completion | Discovery (and so marker/finding completion) runs on a pass that has a pending entry of the stream | Consistent with SM-5 as implemented; tests commit a later change first |

---

## 3. Requirements traceability matrix

Status: **V** verified by executed tests in this or a referenced record; **V-PG** additionally on PostgreSQL 18 + Azurite in both runs of record; **C** characterized limit (executed, documented, not prevented); **NE** not executed; **P** production-only, not claimed.

| Requirement family | Rules | Implementation | Executed evidence | Status |
|---|---|---|---|---|
| Chain rules | R-1..R-7 | `Verification/JournalChainRules.cs` | `JournalChainRulesTests`, `JournalEnvelopeKnownAnswerTests` (WP-1); MV-04b, MV-11, MV-14 DETECTED; WP7-PG-01 (ADV-01/05/06/09/10) | V-PG |
| D+ delivery | DP-1..DP-14, DD-1..DD-11, EN-1..EN-3 | `AuditDeliveryEnforcement.cs`, `ArchiveProcessor.cs` | `DPlusDeliveryEnforcement{Audit,Content,Authorization}Tests`; PG `DPlus*`, `WP5B*`; WP7-PG-01/02/03/04; MV-10, MV-12, MV-13a DETECTED | V-PG |
| Archive identity | AS-1..AS-4, PV-1..PV-9, GP-1..GP-3 | `ArchiveIdentityCodec.cs`, `ArchiveProvisioning.cs`, D+ pinning | `ArchiveProvisioningTests`; PG `Provisioning…`; WP7-PG-04 (sentinel deleted, instance replaced); MV-13a/b DETECTED | V-PG |
| Enumeration | AE-1..AE-6 (AE-7/AE-8: §10, P) | `ArchiveCompleteness.cs`, Blob/in-memory listing | `ArchiveInventoryTests`, `JournalVerificationAzuriteIntegrationTests`, `JournalBoundedListingAzuriteTests`; WP7-PG-04 (unexpected object, gap, beyond head); MV-04a, MV-05 DETECTED | V-PG (AE-7 P) |
| Reset policy | RST-1..RST-3 | compose overlays | WP-2 record; disposable stacks in every run | V |
| Checkpoints | CK-1..CK-12, CKR-1..CKR-3 | `JournalVerificationCheckpoints.cs`, `VerificationCheckpointStores.cs` | `VerificationCheckpointTests`; WP7-PG-06 (CKR-1 detected, CKR-3 characterized); MV-08, MV-09 DETECTED; CK-6/CKR-2 production only | V-PG; CKR-3 **C**; CK-6 **P** |
| Verifier | VF-1..VF-14, IV-1..IV-9 | `JournalVerifier.cs`, `JournalStreamComparer.cs` | `JournalVerifierTests`, `JournalVerificationRaceTests` (new); PG WP-4 tests; WP7-PG-01/04/11/12/13; MV-01..03, MV-06..08, MV-15a/b, MV-16, MV-18 DETECTED | V-PG |
| Findings | §9.1–§9.3, SM-6/7 codes | `JournalVerificationFindings.cs` | Every finding code asserted in WP7-PG-01..13 or the IP suites; `FindingsAreRecordedCreateOnly…` | V-PG |
| Baseline | BL-1..BL-10, AX-1..AX-10, RB-1..RB-6 | `JournalBaselineService.cs`, `JournalAcceptedExceptions.cs` | `JournalBaselineServiceTests`, `JournalAcceptedExceptionTests`; PG `AnAcceptedGap…`; WP7-PG-04b (re-baseline detects OI-DR-7 case); WP7-PG-07 (exception manipulation); MV-17a/b DETECTED | V-PG |
| Durable stops | SM-1..SM-9, §23.3 (W1), DD-10 | `DPlusStreamStops.cs`, `ArchiveProcessor.cs` | IP D+ suites; PG `DPlusTheStopMarker…`, `WP5BAStopSurvives…`; WP7-PG-05 (marker, finding, commitment, J4+J6) | V-PG; W1 window and J4+J6 **C** |
| Recovery | RR-1..RR-4, AP-1..AP-6, ST-1..ST-4, SG-1..SG-3, EX-1..EX-8, RX-1..RX-4 | `Recovery/*`, `--journal-recovery` | `DPlusRecovery{Audit,Content,Authorization}Tests`, `JournalRecoveryCommandTests`; PG `G14R2*`, `WP5B*`; WP7-PG-08a/b/c; SR-MV-01..15 (G14-R2, WP-5b) | V-PG |
| W2-S commitments | EP-1..EP-6, VC-1..VC-11 | `DPlusEvidenceCommitments.cs`, `DPlusCommitmentInspection.cs` | `DPlusCommitmentVerificationTests`; PG `WP5BTheVerifierComparesReal…`; WP7-PG-05, WP7-PG-09c(2) (BEYOND_HEAD) | V-PG |
| Stop consequences | SC-R1..SC-R4, CE-1..CE-3, P2-1/P2-2, RD-1..RD-10 | gate, content release, readiness | P2 readiness and WP-5b records; WP7-PG-09d (concurrency, recovery under load); WP7-PG-08c | V-PG |
| Trusted receipts | TR-1..TR-8 | `TrustedJournalReceipts.cs` | `TrustedJournalReceiptTests`; PG `G08*`; WP7-PG-03/04/09a/09b/10; MV-G8-05..10 (G08) | V-PG; TR-6 limit **C** (F-1) |
| Organization gate (E-6) | GT-1..GT-6 | `OrganizationGate.cs`, `ProjectionProcessor.cs` | `AuthorizationStreamGateDPlusTests`; PG `G08ForgedDeliveryState…`; WP7-PG-01/03/09; **GT-6 had no detecting test** (MV-X1 SURVIVED) → `TheProjectorNeverAppliesAnAuthorizationEntryWhoseAnchoredStateHasNoTrustedReceipt` added, MV-X1 DETECTED | V-PG (GT-6 V in-process) |
| Content release (E-7) | CR-1..CR-3 | `ContentCheckpointReceipts.cs` | `ContentStreamDPlusReadabilityTests`; WP7-PG-10 (new, real stores); MV-G8-03/04 (G08) | V-PG |
| G08 database | G8-1..G8-6 | migration `20261119021623_AuthorityJournalImmutabilityG08`, `AuthorityRuntimeGrants.cs` | PG `G08*` (5); WP7-PG-01 (every J1 variant refused 42501); MV-G8-DB1..DB6 (G08) | V-PG |
| Isolation | ADV-15, RD-6, EX-8 | per-stream batches, keys | `TamperingInOneOrganizationAndStream…`; WP7-PG-11; cross-organization substitution WP7-PG-04 | V-PG |
| §13.2 scenarios | ADV-01..ADV-20 | — | §5.1 | V-PG (ADV-11 adapted, CT-8) |
| §13.3 mutations | MV-01..MV-18 | — | §8: all DETECTED (MV-04, MV-13, MV-15, MV-17 in variants) | V |
| §13.5 inverted gap tests | EG-1..EG-3 | `CapabilityRefusalEvidenceRemovalTests` | §7 | V |
| §13.6 acceptance | AC-DR-01..AC-DR-14 | — | §7.3 | mixed |
| M2F3-43 | PD-15 §10.5 | — | §7 | technical result proposed |
| Production prerequisites | §14 1–8, CK-6, AE-7, WP-8 | none | none | **P** (not claimed) |

---

## 4. WP-6

### 4.1 M-6 / G08 (contract §17 definition)

| §11 / A-7 §27.4 item | State | Evidence |
|---|---|---|
| Immutable fields (G8-1) | Implemented, accepted (A8-1) | G08 record §5, §8; re-executed: `G08TheRuntimeCredentialCannot…`, `G08TheTriggersStopTheAdministrator…` in both runs of record; every J1 attempt of WP7-PG-01 (body, digest, revision, stream, delete) refused `42501` [T] |
| Delivery-state transitions (G8-2) | Implemented | `G08LegitimateDeliveryStateTransitions…` (both runs) [T] |
| No delete/truncate (G8-3) | Implemented | `G08TheTriggersStopTheAdministrator…` [T] |
| No INSERT trigger (G8-4) | As designed | WP7-PG-03: J1 inserts a forged `Anchored` row; it is a `FAKE_RECEIPT`, opens nothing, and the next delivery is refused [T] |
| Column-level runtime grants (G8-5) | Implemented | G08 record; MV-G8-DB5 DETECTED (G08) |
| Read-only verifier identity (G8-6) | Implemented | `G08TheDedicatedVerifierLogin…` (both runs) [T] |

**Nothing of WP-6 as defined by the contract remains open in Development/Testing.** It does not constrain J2 (A8-4).

### 4.2 Package §3 assessment items

| Item | Governing rules | Verified property (Development/Testing) | Residual |
|---|---|---|---|
| Journal completeness | R-6, VF-6, VF-13, IV-5 | Gaps, deletions and renumbering detected: above the checkpoint within `T_inc`, below it within `T_full` [T WP7-PG-01] | Undelivered-tail deletion by J2 is not detectable (R-DR-1 class; §6.2) |
| Checkpoint-chain validity | CK-2, CK-5, CK-9, CKR-1 | Corruption detected by the full sweep on every stream [T WP7-PG-06] | — |
| Checkpoint rollback | CK-8, CKR-2, CKR-3 | Detected within a process; **not across a restart** [T WP7-PG-06] | CKR-3 Development/Testing limit (R-DR-7); CKR-2 production only |
| Archive enumeration | AE-1..AE-6, IV-1 | Unexpected object, gap, beyond-head (gap and contiguous) detected on every stream [T WP7-PG-04] | AE-7 (Azure listing consistency) [NV] |
| Evidence commitments | EP-1..EP-6, VC-1..VC-11, TR-4 | Store copy changed → `CONFLICTING_COPIES`; orphaned commitment → `BEYOND_HEAD`; receipts fail closed on a changed commitment in a fresh process [T WP7-PG-05, -09a, -09c] | J4+J6 removal of both copies (A-3 known limit) |
| Accepted historical exceptions | AX-1..AX-10 | Widened scope → record invalid and exception no longer applied; evidence change → `EVIDENCE_CHANGED`; deleted record → invalid [T WP7-PG-07] | AX-9: a store administrator can forge records consistently (CKR-3 class) |
| Durable stops | SM-1..SM-9, GP-1..GP-3 | Marker deletion (J4) and finding deletion (J6) leave the stream stopped and are completed or reported [T WP7-PG-05] | §23.3 window (W1); J4+J6 together |
| Recovery records | RR-1..RR-4, AP-1..AP-6, EX-1..EX-8 | Forged keys, cross-stream and cross-recovery replay, token reuse and single approval never authorize; termination before each write lifts nothing; evidence replaced after termination fails the recovery; marker substitution after a lift re-stops the stream [T WP7-PG-08a/b] | Development/Testing synthetic keys only (AP-1); not two-human evidence |
| Trusted receipts | TR-1..TR-8, GT-1..GT-6, CR-1..CR-3 | Forged state never opens the gate, never releases content, never projects [T WP7-PG-03, -09, -10; MV-X1] | F-1 (TR-6 cache) |
| Archive-instance identity | AS-2, AS-3, GP-2, TR-3 | Instance replacement: verifier `ARCHIVE_REGRESSION`, D+ durable `INSTANCE_CHANGED`, fresh-process receipts refused [T WP7-PG-04, -10] | Not recoverable in this increment (SC-R3); cached receipts (F-1) |
| Verification findings | §9, DP-12, DD-9 | Create-only; alert-only; never quarantine [T every WP7 test asserts no quarantine from the verifier] | Store is configuration-separated only (PV-9) |
| Verifier restart | CK-4, CK-5, ADV-13 | A killed sweep writes nothing; a restarted verifier completes and advances exactly once; outages are Inconclusive [T WP7-PG-13] | After restart, CKR-3 (above) |

### 4.3 OI-DR-7 (assessed against VC-10 and IV-5; cadence and trust model unchanged)

Archived objects at or below the trusted checkpoint are never re-read by any sweep: the incremental sweep lists and reads only above `v` (IV-1); the full sweep enumerates every key (VF-12) and re-walks the AuthorityDb chain against the checkpoint digest (VF-13), but reads only the object at `v` (VF-3) and the objects above it.

| J4 change below the checkpoint | Detected by | Evidence |
|---|---|---|
| Object **deleted** | Full sweep (`ARCHIVE_DISCONTINUITY`, AE-4) | [T WP7-PG-04b, all streams] |
| Object **replaced** with other bytes | **Not by any sweep.** Detected by a re-baseline (BL-1 reads and byte-compares every object) or when the object is next read (D+ predecessor read, recovery, DR replay) | [T WP7-PG-04b: full sweep Clean; re-baseline FAILED with a finding at the replaced revision] |
| Object at `v` replaced | Every sweep (`ARCHIVE_REGRESSION`) | [T WP7-PG-04 `anchor-overwritten`] |

**Assurance consequence.** Against J4 in Development/Testing, the archive's historical bodies below the checkpoint are not continuously verified. A replaced historical body cannot open a gate or release content: receipts are established only for current heads, and only from objects that equal the row digest. But it corrupts the evidence that recovery (SR-C1 copies, DR replay) and investigation would rely on, until a re-baseline or a read detects it. In production this is covered by locked immutability (§14). The disposition recommended in §19 ("optional; default off in Development") is unchanged; whether to enable a periodic re-read is an owner decision (D-3).

---

## 5. WP-7 integrated adversarial verification

Layers: **IP** in-process; **PG** PostgreSQL 18.0 + two Azurite 3.9.0 instances, executed in both runs of record (§5.3). "Prevented" = refused before any state change; "Detected" = reported (alert-only) and, where applicable, fail-closed; "Characterized" = executed, not prevented and not detected, documented as a limit.

### 5.1 Contract scenarios ADV-01..ADV-20

| ID | Class | Streams (PG) | Outcome | Evidence |
|---|---|---|---|---|
| ADV-01 | J2 (J1 refused) | all three | J1 **prevented** (42501). J2 **detected**: `REVISION_GAP` within `T_inc` above the checkpoint; within `T_full` below it | WP7-PG-01 `ADV-01-above/-below`; IP `AModifiedDeliveredRow…` |
| ADV-02 | J2 | all three | **Detected** `ANCHOR_MISMATCH` + `BEHIND_ARCHIVE`; next delivery: `ARCHIVE_BODY_MISMATCH` quarantine (rows Pending) or durable `LINK_MISMATCH` stop (rows forged Anchored); gate closed (CT-4) | WP7-PG-01 `ADV-02`, `ADV-02-anchored` |
| ADV-03 | J2 | all three | **Detected** `BEHIND_ARCHIVE` | WP7-PG-01 `ADV-03`; PG `AdministratorSqlTampering(truncate)` |
| ADV-04 | J2/J3 | all three | Existing `ARCHIVE_BODY_MISMATCH` quarantine unchanged; gate `Quarantined` | WP7-PG-02 |
| ADV-05 | J1 refused; J2 | all three | J1 **prevented**. J2 **detected**: `ANCHOR_MISMATCH` above; `LINK_MISMATCH` below (CT-3) | WP7-PG-01 `ADV-05-above/-below` |
| ADV-06 | J1 refused; J2 | all three | J1 **prevented**. J2 **detected** `BODY_DIGEST_MISMATCH` | WP7-PG-01 `ADV-06-above/-below` |
| ADV-07 (W-1, original rewrite-and-extend) | J1 refused; J2 | all three | J1 **prevented**. J2 **detected** `ANCHOR_MISMATCH`; the genuine extension is never delivered (quarantine or durable stop) | WP7-PG-01 `ADV-07`, `ADV-07-anchored`; IP `RewriteAndExtend…`, `ARechainedRow…` |
| ADV-08 (original forged anchored receipt) | **J1** (runtime credential) | all three | Not prevented (INSERT has no trigger, G8-4). **Detected** `FAKE_RECEIPT`; opens no gate, releases nothing, never projected (MV-X1 test); next delivery durable `PREDECESSOR_MISSING`. Future-dated creation time ignored | WP7-PG-03; IP `AForgedAnchoredRowIsAFakeReceipt` |
| ADV-09 | J1 refused; J2 | all three | J1 **prevented**. J2 **detected** `ENVELOPE_COLUMN_MISMATCH` | WP7-PG-01 `ADV-09` |
| ADV-10 | J1 refused; J2 | all three | J1 **prevented**. J2 **detected**: source `REVISION_GAP`, destination `ENVELOPE_COLUMN_MISMATCH` | WP7-PG-01 `ADV-10` |
| ADV-11 | J1 | — | No AuthorityDb checkpoint channel exists (CT-8); head-column disagreement reported; anchor from AuthorityDb (MV-07) detected | IP `AnOrganizationHeadThatDisagrees…`; MV-07 |
| ADV-12 | J4 | all three | **Detected**: sentinel deleted / instance replaced → `ARCHIVE_REGRESSION` (D+ waits, or durable `INSTANCE_CHANGED`); object overwritten above `v` → `ANCHOR_MISMATCH`; anchor overwritten → `ARCHIVE_REGRESSION` | WP7-PG-04 |
| ADV-13 | outage / kill | all three | Inconclusive, no checkpoint; killed sweep writes nothing; restart resumes exactly once | WP7-PG-13; IP `ArchiveAndCheckpointStoreOutages…`, `ASweepCancelledMidway…` |
| ADV-14 | J3 | all three | **Detected** `BEHIND_ARCHIVE` (security finding, not suppressed); next append quarantines | WP7-PG-01 `ADV-14` (row-level restore, CT-12) |
| ADV-15 | any | all three, two organizations | Only the tampered (organization, stream) has findings; every other stream stays Clean and keeps delivering | WP7-PG-11; IP `TamperingInOne…` |
| ADV-16 | — | all three | `DELIVERY_LAG` only (operational) | WP7-PG-12 |
| ADV-17 | — | all three | Baseline, incremental and full sweeps: zero AuthorityDb writes, only `SELECT`s in read-only snapshots | WP7-PG-11; PG `TheSnapshotIsServerEnforcedReadOnly…` |
| ADV-18 | J1 as the runtime role, with M-6 | all three | Every immutable-field J1 change **prevented**; the J1-permitted forgery (ADV-08) detected | WP7-PG-01, WP7-PG-03 |
| ADV-19 | J6 | all three | Within the process `CHECKPOINT_STORE_REGRESSION`; after a restart **not detected** (CKR-3) | WP7-PG-06; IP `ACheckpointRollback…` |
| ADV-20 | J1/J2 | — | `GENESIS_VIOLATION` (D+ and verifier) | IP `AGenesisRowCarryingAPreviousDigest…`, `JournalChainRulesTests` |

### 5.2 Package §4 attacks not named in §13.2

| Attack | Class | Outcome | Evidence |
|---|---|---|---|
| Archive predecessor deletion | J4 | Verifier `FAKE_RECEIPT`; D+ durable `PREDECESSOR_MISSING`; fresh receipts refused; gate closed | WP7-PG-04 `predecessor-deleted` |
| Archive substitution across organizations | J4 | `ANCHOR_MISMATCH` + `ENVELOPE_COLUMN_MISMATCH` (R-3); receipt refused | WP7-PG-04 `cross-organization` |
| Pre-emption of the next revision | J4 | `ANCHOR_MISMATCH`; next delivery collides: quarantine (R-DR-6 denial of service, existing) | WP7-PG-04 `beyond-head-contiguous` |
| Forged database delivery states | J1, J2 | Never a receipt; gate, release and projection closed | WP7-PG-01 `*-anchored`, -03, -10; PG `G08Forged…` |
| Forged authorization and content receipts | J1 | Same | WP7-PG-03, WP7-PG-10 |
| Stop-marker deletion | J4 | Stream stays stopped; marker re-created; `DPLUS_STOP_RECORD_INCOMPLETE` | WP7-PG-05 |
| Finding deletion | J6 | Stream stays stopped; finding completed from the marker | WP7-PG-05 |
| Evidence commitment modification | J6 | Verifier `CONFLICTING_COPIES`; fresh receipts refused | WP7-PG-05; PG `WP5BTheVerifierCompares…`; WP7-PG-09a |
| Accepted-exception manipulation | J6, J4 | Record invalid / evidence changed; exception not applied | WP7-PG-07 |
| Forged and replayed recovery approvals | — | Never counted or authorizing | WP7-PG-08a |
| Recovery-generation substitution | J4 | Lift removed; stream stopped again | WP7-PG-08b (5) |
| Termination during critical transitions; evidence replacement afterwards | fault injection | Nothing lifted; resumption idempotent; replaced evidence fails the recovery | WP7-PG-08b (1)–(3); PG `G14R2EvidenceCommitmentsAndTheDD10Sequence…` (D+ side) |
| Cross-stream attacks | J2, J4 | Row moved to another stream detected on both streams; approval of another stream not counted; receipts never reused across streams | WP7-PG-01 `ADV-10`, -08a; IP `EvidenceIsNeverReusedAcrossOrganizationsOrStreams` |
| Both stop records and both commitment copies removed | **J4 + J6 (out of scope)** | Not detected as tampering; in this scenario D+ re-derives the refusal from the archive (predecessor still absent) | WP7-PG-05 (characterized); IP `KnownLimitRemovingBoth…` (delivery resumes when the evidence is also restored) |

### 5.3 Runs of record (contract §13.1)

| Run | Stack | Result |
|---|---|---|
| `pg-run-1/` | fresh postgres:18.0 + two azurite:3.9.0, new credentials, removed after | **167 total, 164 passed, 3 skipped, 0 failed**; 2026-11-19 04:56–05:08 UTC |
| `pg-run-2/` | a second, independent fresh stack | **167 total, 164 passed, 3 skipped, 0 failed**; 2026-11-19 05:08–05:18 UTC |

Both runs: 106 WP-7 cases, G08 5, G14-R2 8, WP-5b 9, D+ 8, WP-4/WP-4A baseline, sweep and tamper tests, bounded listing 9, Azurite inventory 8. Skipped: the two opt-in measurements (run separately, §10) and the Azurite persistence phase (needs a store restart between phases). The executed assemblies' SHA-256 are identical in both runs (`run-summary.json`). Image digests and tool versions: `environment.txt`. Synthetic data only; the in-class 20k-row measurement ran at 2,000 rows (functional), the 20k-row measurement is §10.

---

## 6. Authorization and content assurance (package §5)

### 6.1 Demonstrations

| Requirement | Result | Evidence |
|---|---|---|
| Forged Anchored states cannot establish protected access | Gate `AwaitingArchive` for J1 and J2 forgeries on PostgreSQL; the projector never applies an entry without a trusted receipt | WP7-PG-01/03; PG `G08Forged…`; `TheProjectorNeverApplies…` (new; MV-X1) |
| Forged Anchored states cannot release content | Not released (CR-2) for a J1-forged checkpoint on real stores | WP7-PG-10; IP `E7Forged…` |
| Revocations effective during delivery, containment and recovery | Under concurrent changes, two processors, a stop and an SR-C1 recovery: every `Ready` evaluation had an archived head (0 violations) | WP7-PG-09d; IP `RevocationsStayEffective…`; PG `WP5BTheRealGateStaysClosed…` |
| Cached receipts cannot bypass newly established stops | A new head (the change behind a stop) is never answered from the cache (GT-5) | WP7-PG-09a |
| Cached evidence cannot bypass recovery-generation changes | The recovered entry was never verified before its recovery (no cached receipt can exist); its receipt is established at generation 1 | WP7-PG-09b |
| Archive-instance changes invalidate affected assurance | Fresh process: refused. Next head: refused, D+ durable stop. **Same process, unchanged head: the cached receipt stands** | WP7-PG-04, -09a, -10 → **F-1** |
| New authorization heads cannot be bypassed with old cached evidence | Holds (GT-5) | WP7-PG-09a |
| Concurrency of permissions, delivery and recovery | No unauthorized `Ready` | WP7-PG-09d |
| Content evidence bound to record, revision, operation and manifest root | Another record, revision, operation or root never releases; operation column immutable to J1 | WP7-PG-10 |
| Recovery cannot lift organization quarantine | Refused `RECOVERY_ORGANIZATION_QUARANTINED`; gate stays `Quarantined` | WP7-PG-08c |

### 6.2 J2 deletion exposure window (authorization stream; PostgreSQL)

| J2 action | Gate after the action | Detection | Exposure |
|---|---|---|---|
| Deletes an **undelivered** head (a pending revocation) before any D+ pass, resets the head | **`Ready`** on the previous, genuinely delivered state | **None.** Full sweep Clean: no evidence of the entry exists outside AuthorityDb | **Unbounded and undetected.** The revocation never becomes effective. The revoked access is the access that existed before it, not a new grant. Same class as R-DR-1 (§5.4) and A-7 L-4 |
| Deletes an undelivered head **after** D+ committed its W2-S evidence (archive append interrupted) | **`Ready`** | Incremental sweep: `DPLUS_COMMITMENT_MISMATCH/BEYOND_HEAD` | Until the next incremental sweep (≤ `T_inc` + duration, 5 min default) |
| Deletes a **delivered** head (an effective revocation), resets the head | **`Ready`** on revision `k − 1` | Incremental sweep: `BEHIND_ARCHIVE` | Until detection. Whether access is regained also depends on SpiceDB, which is separately administered and is not reverted by J2 **[I; no SpiceDB in the gate stack]**. The next authorization change collides with the archived revision: permanent `ARCHIVE_BODY_MISMATCH` quarantine [T] |

Evidence: `WP7J2DeletionOfTheAuthorizationHeadExposureWindowIsCharacterized` [T, both runs]. Detection is alert-only (OD-DV-3). Closing the first row needs evidence of an entry outside AuthorityDb before the request is acknowledged as committed, which is outside D-R and a production question (separated identities; G14). **This is not prevented, and in the first row not detected.**

---

## 7. M2F3-43 formal verification

### 7.1 Requirement and procedure

M2F3-43 (PD-15 §10.5): "delete or alter a delivered evidence row ⇒ chain / archive verification detects it". It failed as written on 2026-11-17: deletion, a re-digested unsent forgery, and an alteration outside the newest eight were undetected (FU record §2.3). Governing WP-7 procedure: §13.4 "M2F3-43 passes as written for delivered rows (EG-1..EG-3, ADV-01..ADV-10)", PG in two runs of record (§13.1), owner acceptance (AC-DR-13).

### 7.2 Results

| Case | Original FU outcome | WP-7 outcome | Evidence |
|---|---|---|---|
| EG-1 deletion of a delivered evidence row | Not detected | **Detected** `REVISION_GAP` (full sweep; incremental Clean, IV-5); the recorder's re-recording remains (R-DR-2) | `M2F343VerifierDeletingADeliveredRow…` [T] |
| EG-2 consistently re-digested, never re-sent | Not detected | **Detected** `LINK_MISMATCH` at the successor (CT-3); the recorder's `AlreadyRecorded` remains (R-DR-2) | `M2F343VerifierAConsistentlyRedigested…` [T] |
| EG-3 altered outside the newest eight | Not detected | **Detected** `BODY_DIGEST_MISMATCH` | `M2F343VerifierAnAlteredDeliveredRowOutside…` [T] |
| ADV-01..ADV-10 on delivered rows | — | All detected on PostgreSQL + Azurite in both runs of record, on all three streams (§5.1) | `pg-run-1/`, `pg-run-2/` |
| Re-sent alterations (existing Detected tests) | Detected | Unchanged | `CapabilityRefusalEvidenceRemovalTests` (4) |

The three expected-gap tests are inverted per §13.5: attack steps unchanged, non-detection assertions replaced. The historical FU-M2f3-01 outcomes are not rewritten.

### 7.3 Acceptance criteria AC-DR-01..AC-DR-14

| ID | Status |
|---|---|
| AC-DR-01 ADV-01..ADV-20 at their layers, PG in two runs of record | **Met** (ADV-11 in expressible form, CT-8) |
| AC-DR-02 EG-1..EG-3 inverted; M2F3-43 passes as written for delivered rows | **Met** |
| AC-DR-03 MV-01..MV-18 DETECTED | **Met** (§8; MV-15/MV-17 in expressible variants, CT-9) |
| AC-DR-04 §13.4 regressions, SEC-P01..SEC-P12 | **Met** (§9; M2F3-31, -54, -57 regressions within the D+/collision suites; the existing TS-M2f-01 and the unidentified 3G contract failure stay separate, unattributed) |
| AC-DR-05 isolation | **Met** |
| AC-DR-06 restart, restore, replay, idempotent retry, checkpoint rollback | **Met** (CKR-3 characterized as specified) |
| AC-DR-07 enumeration completeness | **Met** |
| AC-DR-08 baseline outcomes | **Met** (WP-3/WP-4A records; re-executed in both runs) |
| AC-DR-09 fail closed | **Met** |
| AC-DR-10 no AuthorityDb write by verifier or baseline | **Met** |
| AC-DR-11 measured at a 2M-row organization | **INCONCLUSIVE**: measured at 2k/20k rows; 2M extrapolated only (CT-7, §10) |
| AC-DR-12 R-FU-1 restated with residuals | **Met** in §11.3 |
| AC-DR-13 owner acceptance; claims limited to Development/Testing | **Pending (owner)** |
| AC-DR-14 strict specification validation | **Met** (§9) |

### 7.4 Proposed result

**Proposed technical result for M2F3-43: PASS, for delivered rows in Development/Testing**, on the strength of EG-1..EG-3 and ADV-01..ADV-10 in-process and in two independent PostgreSQL 18 + Azurite runs of record, with the conditions:
- detection, not prevention; alert-only;
- latency: changes above the trusted checkpoint within `T_inc` (5 min) plus sweep duration, changes below it within `T_full` (24 h) plus sweep duration (A-1 IV-6);
- the residuals of §11.3 (R-DR-1, R-DR-2, J2 undelivered-head deletion, CKR-3, OI-DR-7, F-1, J4+J6, JP).

M2F3-43's requirement itself names no production infrastructure and no independent review. The SEC-BL-01 acceptance contract it now rests on does require owner acceptance (AC-DR-13) and leaves AC-DR-11 open. Therefore: **technical verification is complete for M2F3-43 as written; formal reclassification from FAILED is an owner decision (D-1) and has not been made. M2F3-43 remains FAILED in every record.** PD-15 remains open regardless (§15: M2F3-33, -28, -29, -31, -42, -44, -78; OPEN-M2f-4; TS-M2f-01).

---

## 8. Mutations

Driver `scripts/dev/wp7-mutations.py`, scratch clone (`git clone --local` plus every uncommitted working-tree file), `--no-incremental` builds, anchors verified unique, originals restored and SHA-256 verified after every mutation. In-process checks: 18 declared classes (338 tests); architecture: `JournalVerificationBoundaryTests` (13); PostgreSQL: the gate with `-MethodFilter '*ReadOnly*'` on a fresh stack built from the mutated clone. Results: `mutations/`.

| ID | Mutation | Verdict | Detected by (examples) |
|---|---|---|---|
| MV-01 | Drop the anchor at `v` | DETECTED | `AConsistentChainRewriteIsDetectedAtTheCheckpointAnchor`, `RewriteAndExtend…` |
| MV-02 | Drop row-versus-archive byte comparison | DETECTED (5) | verifier and baseline tests |
| MV-03 | Trust Anchored state as archive presence | DETECTED (5) | `AForgedAnchoredRowIsAFakeReceipt`, … |
| MV-04a / MV-04b | Skip the archive gap check / the row gap check | DETECTED (11) / (5) | enumeration and chain tests |
| MV-05 | Archive head by probing | DETECTED (2) | `MissingArchiveObjects…`, race test |
| MV-06 | Unavailable archive treated as verified | DETECTED (1) | `ArchiveAndCheckpointStoreOutagesAreInconclusive…` |
| MV-07 | Anchor digest from AuthorityDb | DETECTED (2) — attempt 2 (attempt 1 did not build: analyzer CA1826 in the mutation text) | anchor tests |
| MV-08 | Checkpoint despite findings | DETECTED (17) | |
| MV-09 | Non-monotonic checkpoint | DETECTED (2) | `ASuccessorMustLink…` |
| MV-10 | D+ skips predecessor existence | DETECTED (57) | D+ suites |
| MV-11 | R-4 column/envelope comparison dropped | DETECTED (1) | `R4APreviousDigestColumn…` |
| MV-12 | D+ outage read as absent | DETECTED (3) | outage tests |
| MV-13a / MV-13b | Skip the sentinel check (D+ / verifier) | DETECTED (3) / (1) | sentinel tests |
| MV-14 | Skip the genesis rule | DETECTED (5) | |
| MV-15a / MV-15b | Verifier gains a write dependency / snapshot READ WRITE | DETECTED (arch 2) / DETECTED (PG 2) | new boundary test; `TheSnapshotIsServerEnforcedReadOnly…`, `WP7AllThreeStreams…` |
| MV-16 | Full sweep skipped | DETECTED (10) | EG-1..EG-3, below-checkpoint tests |
| MV-17a / MV-17b | Baseline gains a write dependency / repairs missing evidence | DETECTED (arch 2) / DETECTED (13) | new boundary test; baseline tests |
| MV-18 | `FAKE_RECEIPT` without the second read | DETECTED (1) — **only by the new race test** | `AnEntryDeliveredBetweenTheListingAndTheRowWalk…` |
| **MV-X1** (integrated) | Projector applies an entry on its row state (GT-6) | **SURVIVED in attempt 1; DETECTED in attempt 2** by the new GT-6 test | `TheProjectorNeverApplies…` |

Baselines: BASELINE PASS (337 tests in attempt 1, 338 in attempt 2 with the GT-6 test); BASELINE-PG PASS; RESTORED PASS. **Attempt 1's BASELINE-ARCH failed**: the new boundary test's type selector matched zero types (its own count assertion caught it, F-5); MV-15a and MV-17a of attempt 1 are therefore invalid and were re-run in attempt 2 after the fix (BASELINE-ARCH PASS). The G08 (MV-G8-01..10, DB1..DB6), WP-5b (MV-5B-01..12) and G14-R2 (SR-MV-01..15) mutation sets were not re-run; no code they target changed.

---

## 9. Regressions

| Suite | Result |
|---|---|
| PostgreSQL 18 + Azurite gate (all journal-verification classes) | Two runs of record, 164/167 each, 0 failed (§5.3) [T] |
| Authority (in-process, Debug) | **1,914 total, 1,739 passed, 175 skipped** (PG/Azurite-gated and opt-in), **0 failed** [T `authority-full.txt`]. +111 over G08: 106 WP-7 PG cases, 4 race cases, 1 GT-6 test; EG-1..EG-3 replaced the three expected-gap tests |
| Architecture | **227/227** [T `architecture.txt`] (+1: the Reconciliation boundary test) |
| Contract | **958 total, 946 passed, 12 skipped, 0 failed** [T `contract.txt`] |
| SEC-P01..SEC-P12 | **13/13** [T `sec-p/secp.txt`] on a fresh disposable security-test stack (`enterpriseforms-sectest`, `down -v` before and after) built from these sources, 2026-11-19 05:30 UTC. The five `enterpriseforms/*:local` tags were saved as `:pre-wp7-saved`, re-pointed afterwards with image IDs verified equal (`image-restore.txt`), temporary tags removed; `:dev` tags untouched. The supplementary post-run journal-state query failed on a SQL error in the query itself (`GROUP BY` over an aggregate; kept as run in `stack-state.txt`), so post-run delivery states were not captured |
| G14 recovery regressions | `DPlusRecovery*Tests` (IP), PG `G14R2*` 8/8 in both runs [T] |
| WP-5b, G08, E-6/E-7 regressions | PG `WP5B*` 9/9 and `G08*` 5/5 in both runs; IP suites green [T] |
| Strict specification validation | `scripts/validate-specifications.py --strict` passes after the manifest digests of the amended tracked documents were refreshed and this record was added (`spec-validation.txt`) [T] |
| Stage B | **Not run** (not authorized) |

---

## 10. Performance and operational assessment

All figures are Development/Testing: disposable PostgreSQL 18.0 and two Azurite 3.9.0 instances on loopback, one process, synthetic data. Azure behaviour, latency and listing consistency are **[NV]**. "Observed" = executed measurement; "extrapolated" = arithmetic from observed points, not a measurement.

**This run** (`measurement/`, 2026-11-19 05:35–06:10 UTC, machine otherwise idle; existing harness: `MeasureBaselineIncrementalAndFullSweep`, `MeasureCheckpointGrowth`, `WP5BDPlusDeliveryMeasurement`, `G08ReceiptMeasurement`; 4/4 passed) and the earlier measurements it consolidates (WP-4A `run-1/`, WP-5b `measurement-1..3/`, G08 `measurement/`).

| Metric | Observed (this run) | Earlier observations | Notes |
|---|---|---|---|
| All-three-stream D+ delivery throughput (drain, backlog B per stream) | B = 32: **10.3** entries/s (97.3 ms/entry); B = 256: **10.1** (98.9); B = 2,048: **8.1** (123.5) | WP-5b: 18.7 / 23.5 / 13.6; G08 session: 9.0 / 10.7 / 7.0 | Store operations per entry identical in every run (5.219 / 5.246 / 5.250). Throughput varies about 2× between sessions on the same rig: the environment, not the code |
| Delivery steady state (one entry per stream per pass) | pass p50 **645** / p95 1,390 / max 1,436 ms; 39 store operations per pass | WP-5b 475 / 822 / 941 ms; G08 472–484 ms p50 | Per-pass work is not amortized at one entry per stream |
| Authorization request (gate evaluation, open scope; 200 samples) | warm (cached receipt) p50 **3.64** / p95 4.54 / p99 4.78 ms; cold (receipt established) p50 **110.7** / p95 134.2 / p99 156.8 ms; pending-count-only reference p50 1.74 ms | G08: warm 7.70, cold 130.0 ms p50 | Cold: 6 store operations, once per new head per process |
| New head refresh (20 heads) | first evaluation after delivery p50 115.2 / p95 805.3 ms; second p50 3.80 ms | G08: 134.3 / 174.3; 7.65 | Each new head is `AwaitingArchive` until delivered (asserted) |
| Concurrent first check (16 workers × 25) | cold: **239.2** evaluations/s, p50 5.07 / p99 **1,388.8 ms**; warm: 1,053 evaluations/s, p99 13.0 ms | G08: cold p99 1,245.8 ms | No single-flight: every worker establishes the same receipt (R-G8-8). Correctness unaffected |
| Content retrieval (release decision: checkpoint lookup + receipt; 200 samples) | warm p50 **1.88** / p95 2.22 ms; cold p50 **111.1** / p95 135.9 ms; CR-1 binding in-process p50 0.002 ms | G08: warm 3.98, cold 126.7 ms | Excludes HMAC/manifest verification of the content itself (unchanged) |
| Evidence-commitment storage growth | **≈ 1,490 bytes per delivered entry** in total (≈ 745 per store; one object per entry in each store) | WP-5b/G08: 1,480–1,496 | 6,144 entries → 6,144 objects and ≈ 4.6 MB per store |
| Checkpoint growth | 620.9 bytes mean (622 max) per checkpoint; 2,016 checkpoints = 1.25 MB | WP-4A, G08: identical | At the default rate of 289 checkpoints per stream per day: ≈ 0.54 MB per organization per day for three streams, **≈ 196 MB per organization per year** [extrapolated] |
| Checkpoint discovery | head and predecessor: **374 ms**; full chain (2,016): **13.1 s** | 231–255 ms; 10.6–12.7 s | Full-chain discovery runs on every full sweep and is linear in chain length: one year (≈ 105,000 per stream) ≈ **11 min per stream per full sweep** [extrapolated]. OI-DR-10's adopted rate rules are not implemented (deferred, unchanged) |
| Recovery-history discovery | clean stream pass p50 **234** ms (13 operations); after one executed recovery (7 recovery records, 1 stop) p50 **503** / p95 1,234 ms | WP-5b: 177→292, 162→341 ms; G08 282→566 ms | Every recovery record and signature is re-verified on every pass, so the cost grows with recovery history [I; only 0 and 1 recoveries measured] |
| Baseline (20,000 rows) | **427.8 s** (20,000 archive reads; 21.4 ms per object) | WP-4A: 460.7 s | Dominated by one archive read per object |
| Incremental sweep (500 new rows over 20,000) | bounded **12.0 / 12.5 s**; unbounded 13.8 s (21,000 keys listed) | WP-4A: 12.7 / 12.9 / 14.8 s | Bounded cost is independent of history (IV-1); ≈ 24 ms per new row, dominated by object reads |
| Full sweep (21,500 rows) | **5.6 s**; 43,000 keys listed, 1 object read | WP-4A: 5.6 s | Reads only the object at `v` (OI-DR-7) |
| Memory | peak working set 232 MB; 1.52 GB managed allocations over baseline + sweeps | WP-4A: 262 MB | Full-sweep listing is held in memory, proportional to history (R-W4A-7) |
| Integrated concurrent workload (correctness) | `WP7ConcurrentPermissionsDeliveryAndRecovery…`: 3 gate evaluators, 2 processors, 30 changes, a stop and an SR-C1 recovery under load: 0 violations in both runs of record (28.6 s / 38.8 s) | — | A correctness test, not a throughput measurement |

**AC-DR-11 at 2M rows: not measured.** Linear extrapolation from the 20k-row run, assuming the same loopback rig [extrapolated, not measurements]: baseline ≈ 2M × 21.4 ms ≈ **11.9 h**; full sweep ≈ **9 min** (row walk and listing of ≈ 4M keys), with listing memory of the order of 1 GB [I]; bounded incremental sweeps unchanged (proportional to new rows only); seeding a 2M-row synthetic organization through the archive ≈ 15 h on this rig. Detection latency targets (§8.6) are configuration plus these durations: above the checkpoint ≤ 5 min + incremental duration; below it ≤ 24 h + full-sweep duration. None of these is a production target (PB-09), and Azure latency is unverified.

---

## 11. Findings, observations and owner decisions

### 11.1 Findings

| ID | Finding | Action taken | Owner decision? |
|---|---|---|---|
| **F-1** | TR-6 receipt cache: after a receipt is cached, a J4/J5 archive-instance replacement, a J4 archived-object deletion, or a J6 commitment change does not invalidate the cached receipt of the **same, unchanged** head for the process lifetime. The gate stays `Ready` and released content stays released on that head; a restart, and every later head, fail closed [T WP7-PG-09a, three variants] | None (matches A-7 TR-6; characterization test pins it) | **Yes (D-2).** Options: (a) accept for Development/Testing (J4/J5 are production immutability questions, §14); (b) bound the cache (time-to-live); (c) re-check the sentinel on each cache hit (+1 archive read per gate evaluation, contract change to TR-6) |
| F-2 | GT-6 (projection requires a trusted receipt) had no detecting test: MV-X1 survived | Test added (`TheProjectorNeverApplies…`); MV-X1 detected. No product change | No |
| F-3 | No architecture test guarded `Journal.Reconciliation` (verifier, baseline, comparer) against write, quarantine, signing, gate or entry-archive dependencies; MV-15/MV-17 variants would have survived | Boundary test added; MV-15a/17a detected. No product change | No |
| F-4 | The VF-8 second read (`ARCHIVE_CHANGED_DURING_SWEEP`) had no test: MV-18 would have survived | Race test added (3 streams + negative control); MV-18 detected. No product change | No |
| F-5 | The new boundary test, as first written, matched zero types (a vacuous pass); caught by its own count assertion in the mutation baseline | Fixed (`HaveNameMatching`); attempt-1 arch verdicts discarded | No |

No product code was changed in this increment. No defect was found in the implementation of an approved requirement.

### 11.2 Observations

| ID | Observation |
|---|---|
| O-1 | J2 undelivered-head deletion is undetectable and reopens the gate on the previous state (§6.2 row 1). Existing residual class (R-DR-1, A-7 L-4), now demonstrated on the authorization gate |
| O-2 | J2 delivered-head deletion reopens the gate until `BEHIND_ARCHIVE` (≤ `T_inc`); SpiceDB not reverted [I] |
| O-3 | OI-DR-7: replaced archived bodies below the checkpoint are not detected by sweeps (§4.3) |
| O-4 | Below-checkpoint detection latency is up to `T_full` (24 h) + duration (A-1 IV-6), now demonstrated for EG-1..EG-3 |
| O-5 | ADV-02/ADV-07 next-delivery outcome depends on the forged rows' state (CT-4); both fail closed |
| O-6 | Stop-record completion requires a pending entry (CT-13) |
| O-7 | Cold receipts have no single-flight (R-G8-8, unchanged) |

### 11.3 R-FU-1 restated (AC-DR-12)

R-FU-1 ("a privileged AuthorityDb writer can delete or consistently forge a delivered evidence row without product detection") **no longer holds for delivered rows in Development/Testing**: J1 cannot alter delivered rows at all (G08), and J1/J2 alterations, insertions and deletions of delivered rows are detected (§7.2). Remaining: forgery or deletion of **undelivered** rows (R-DR-1; O-1); the recorder acting on forged rows until detection (R-DR-2), now bounded by the sweep latency (O-4); history before the baseline (R-DR-4); CKR-3 (R-DR-7); OI-DR-7 (O-3); F-1; J4 + J6, J2 + J5, J6 alone and JP (R-DR-3).

### 11.4 Owner decisions requested

| ID | Decision |
|---|---|
| D-1 | Reclassify M2F3-43 on the basis of §7 (proposed: PASS, Development/Testing, delivered rows, with the §7.4 conditions), or keep it FAILED |
| D-2 | Disposition of F-1 (§11.1 options a/b/c) |
| D-3 | OI-DR-7: keep periodic archive re-read off in Development/Testing (contract recommendation), or enable it |
| D-4 | AC-DR-11: accept the 2k/20k measurements with the stated extrapolation, or authorize a 2M-row measurement run (several hours on this rig, §10) |
| D-5 | O-1: accept the J2 undelivered-head deletion exposure as an R-DR-1-class residual for Development/Testing, with production separation as the prerequisite |
| D-6 | Acceptance of WP-6/WP-7 (AC-DR-13) and of the added tests and driver |

SEC-BL-01 and PD-15 remain **OPEN**; nothing here closes them.

---

## 12. Attempt history (preserved)

| Attempt | What happened |
|---|---|
| WP7 PG development run 1 (scratch) | 97 cases, 88 passed. Failures: ADV-02/ADV-07 on audit and content (expected D+ refusal, observed collision quarantine: CT-4, a wrong test expectation); stop-records test (discovery needs a pending entry: CT-13, test fix); termination test (my helper re-created the anchor checkpoint with a new timestamp and correctly got `Conflict`: test fix) |
| Development run 2 | 93/97: the four ADV-02/ADV-07 cases, with full row dumps confirming CT-4 |
| Development run 3 | Row attacks split into Pending and forged-Anchored variants: 42/42 |
| Development run 4 | OI-DR-7 test added: 3/3 |
| Run of record 1, first start | Stopped by the implementer before any test executed (containers still starting), to add the OI-DR-7 test; no results produced; containers removed |
| Mutations attempt 1 | BASELINE-ARCH failed (F-5) → MV-15a/MV-17a invalid; MV-07 did not build (CA1826); MV-X1 survived (F-2) |
| Mutations attempt 2 | Corrected: all DETECTED, baselines PASS |
| During run of record 2 | The implementer started a build in the main repository; it failed to copy over the locked, loaded assemblies. The executed assemblies' SHA-256 equal those recorded at the start of both runs and afterwards (no effect). Subsequent builds were made only after the run |

---

## 13. Not claimed

- No production assurance, Azure evidence, store independence, locked immutability, Confidential Ledger anchoring (CK-6, CKR-2), independent review or certification.
- No reclassification of M2F3-43; no closure of SEC-BL-01, PD-15, G14 (SR-B, SR-D, SR-E, SC-R3), OI-DR-7, OI-DR-9, DD-10 (production), R-FU-1 in full, or any production gate.
- No protection against J2 beyond detection, against JP, against J4 + J6, J2 + J5 or J6 alone; no detection of undelivered-row forgery or deletion.
- No Stage B run; no persistent environment touched; no commit, push, merge or deployment.
- No 2M-row measurement (AC-DR-11 inconclusive).
