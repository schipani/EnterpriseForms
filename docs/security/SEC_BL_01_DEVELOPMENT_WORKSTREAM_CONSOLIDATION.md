# SEC-BL-01 / PD-15 — Development Workstream Consolidation

**Status (2026-11-19):**

| Item | State |
|---|---|
| Increment | Owner package "EnterpriseForms — WP-8 Development Workstream Consolidation". **Documentation only.** It records the owner's WP-7A decisions, reconciles them with D-7A-1..D-7A-8, consolidates the SEC-BL-01 / PD-15 acceptance matrix and creates the pre-production backlog PP-01..PP-15. It is **not** WP-8 of contract §17 (production prerequisites), which is not started (CN-7) |
| Authorization | Owner decision **C-161** (2026-11-19); contract amendment **A-10** (`SEC_BL_01_DR_CONTRACT_CLOSURE.md` §30; dispositions only, no rule changed) |
| Development workstream | **Concluded** for Development/Testing. Outstanding requirements are **deferred** to the pre-production backlog (§6), not waived |
| SEC-BL-01 | **OPEN.** Mandatory criteria unmet: AC-DR-11 (INCONCLUSIVE, deferred), AC-DR-13 (conditional only), M2F3-43 (FAILED), production prerequisites (§14 of the contract) |
| PD-15 | **OPEN.** M2F3-43 plus the other PD-15 gaps (`E3_M2F3_CONTRACT_CLOSURE.md` §14.1, §14.3) |
| M2F3-43 | **FAILED** (retained). Technical verification of delivered-row tamper detection succeeded within its Development/Testing scope (§3) |
| Not done | No product code, build, test, mutation test, Docker environment, Stage B, infrastructure, persistent environment change, commit, push, merge or deployment. Executed checks: strict specification validation and the read-only contract hash script only |
| Review | Owner-led; prepared by the implementing assistant; **not an independent security review** and not a certification |

Evidence tags as in the earlier records: **[T]** executed test evidence in the named evidence directory; **[S]** source inspection; **[I]** inferred; **[NV]** not verified.

---

## 1. Owner decisions recorded (C-161) and reconciliation with D-7A-1..D-7A-8

| # | Owner decision (C-161) | Answers | Recorded result | Reconciliation |
|---|---|---|---|---|
| 1 | Accept F-1 remediation with the documented one-second assurance window | D-7A-1 | TR-6′ (A-9 §29.1, RV-1..RV-9) **accepted for Development/Testing** with Φ = 1 s (the default of `JournalReceipts:CurrentStateMaxAgeMilliseconds`). F-1 **remediated and accepted** | The documented window is Φ **plus one establishment's duration**, not 1 s flat; L-9 (restart forgets withdrawn evidence) and L-10 remain documented limits, not closed (CN-5) |
| 2 | Accept OI-DR-7 remediation | D-7A-2 | VF-15 (A-9 §29.2) **accepted**. OI-DR-7 recorded as **resolved for Development/Testing**; production additionally relies on locked immutability (§14.1); the full-sweep cost goes to PP-08 | The decision text does not say "close"; D-7A-2 asked for formal closure. Recorded as "resolved for Development/Testing", the form used for DD-10 (CN-8) |
| 3 | Accept fail-closed outage behaviour under F-7A-3; preserve any stricter original SEC-P12 obligations | D-7A-6 | L-12 **accepted**: an archive or verification-store outage longer than Φ closes the gate and refuses commands (`AUTHORITY_NOT_READY`); **no grace period**. The SEC-P12 test change is accepted **only** as preserving the original obligation, with the condition of CN-3 | Original matrix obligation preserved; the stricter accepted-and-held path is demonstrated for the authorization leg by the run of record and for content by the E-7 suites; staging must demonstrate both paths deterministically (CN-3, PP-15) |
| 4 | Defer F-7A-4 recovery-race remediation | D-7A-4 | Remediation **deferred** to PP-10. Interim documented remedy: the A-4 EX-1 operator re-run of the executor. The failure is closed (no Allow, no delivery, no quarantine) | None. No A-3/A-4 rule changed |
| 5 | Transfer F-7A-5 sweep performance to the mandatory pre-production backlog; the 5-minute incremental cadence must be satisfied before production acceptance | D-7A-5 | **No A-6 amendment now**: every sweep keeps inspecting every W2-S commitment of the stream (A-6 §26.6 unchanged). PP-08 is **mandatory before production acceptance**, with acceptance criterion "incremental sweep within `T_inc` = 5 min" | The owner sets a production criterion that OD-DV-6 had left open ("measurement defaults, not commitments"); a tightening, not a conflict. Development/Testing already misses the cadence at 10k D+ entries (CN-4) |
| 6 | Accept J2 phase-1 pre-commitment deletion as a documented residual risk, subject to reconciliation with the original governing contract; no authorization architecture redesign | D-7A-3, option (c) | J2 P-1 (A-9 §29.3) **accepted as a documented Development/Testing residual** of the R-DR-1 / R-M2f3-6 class (L-11). Options (a) and (b) are **not** authorized | Consistent with the original contract (§2.1, §18 R-DR-1 = R-M2f3-6, "remove an undelivered tail"). A-7's "detection-only" wording overstates P-1 coverage (CN-1). Production: acceptable only with independently administered AuthorityDb (CN-2, PP-03) |
| 7 | Defer AC-DR-11 two-million-row testing; retain INCONCLUSIVE | D-7A-7 | AC-DR-11 **INCONCLUSIVE**, deferred to PP-11. No extrapolation is accepted as a substitute (unchanged from C-160 D-4) | D-7A-8 (equivalence of the accelerated seed) is **not decided** and stays open as a PP-11 prerequisite (CN-6) |
| 8 | Accept WP-7A implementation and executed evidence; do not represent AC-DR-13 as unconditional acceptance of unmet requirements | (C-160 D-6) | WP-7A **accepted for Development/Testing** (`SEC_BL_01_WP7A_VERIFICATION_RECORD.md`, evidence `docs/evidence/sec-bl-01-wp7a/`). AC-DR-13 **conditionally met** (§4.1) | AC-DR-13 covers the executed Development/Testing evidence only; it does not accept AC-DR-11, any production criterion or any other unmet requirement (CN-9) |
| 9 | Accept the contract hash reconciliation | WP-7A §2 | **Accepted.** H-WP0 `4a8bd1b1c9f1…5720ef12e` and H-AWK `3549e3ba280f…6addf8f2` are two byte ranges of the same unchanged §1–§20 | Both reproduced before and after A-10 (§7) |
| — | M2F3-43: record technical verification; reclassify only if the governing formal criteria permit | C-159/C-160 D-1 | **FAILED retained**; technical verification recorded (§3) | The governing criteria (E3 closure §14.2: "acceptance by AC-DR-01..AC-DR-14") are not all met |

## 2. Contract conflicts and reconciliations (CN-1..CN-9)

| ID | Materiality | Finding | Reconciled position (no rule changed) |
|---|---|---|---|
| **CN-1** | **Material (documentation)** | A-7 §27.4 "Limit" ("J2 deleting or rewriting the journal tail is detected … not prevented"), §27.5 **L-4** ("J2 tail deletion before delivery … is detection-only") and `SEC_BL_01_G08_E6_E7_IMPLEMENTATION_RECORD.md` **R-G8-1** claim detection of J2 pre-delivery tail deletion. A-9 §29.3 (executed, both runs of record) shows that deletion **before the W2-S commitment** (P-1) is **neither prevented nor detected**: no evidence of the entry exists outside AuthorityDb | The original governing contract governs: removal of an undelivered tail is **R-M2f3-6 = R-DR-1**, an accepted residual (§18; `E3_M2F3_CONTRACT_CLOSURE.md` §11.3). L-4, the §27.4 limit and R-G8-1 are read as applying to **P-2 and P-3 only** (evidence outside AuthorityDb exists); P-1 is covered by L-11. Recorded in A-10 (A10-6); §27 text preserved unchanged |
| **CN-2** | **Material for production** (not for Development/Testing) | J2 P-1 lets a privileged AuthorityDb administrator erase a committed but not yet effective revocation without trace. ADR-PB-009 sets "zero accepted authorization rollback", and CLAUDE.md §4R requires that authority revocations never roll back | The Level 2 threat model (CLAUDE.md §4M) assumes the separately operated Authority and its database administration are **not** compromised; J2 is outside it. The residual is acceptable for production only with independently administered AuthorityDb under PIM/JIT with independent audit (PP-03). No production record may represent the PB-09 zero-rollback target as met **against J2**. No redesign is authorized (decision 6) |
| **CN-3** | **Material (gate test)** | SEC-P12 (ADR-PB-003 gate). Original obligation (`security_validation/SECURITY_PROOF_TEST_MATRIX.csv`): "Operation requiring independently durable evidence remains non-effective or quarantined; never releases protected bytes without required receipt". WP-7A changed the test from **requiring** the share command to be accepted (`202 CommittedAwaitingProjection`) during the outage to accepting either that or `503 AUTHORITY_NOT_READY` | **No weakening of the matrix obligation:** both branches still assert the gate `AwaitingArchive`, the reader and owner reads refused during the outage and reads only after the receipt [S: `DistributedConsistencyTests.cs` diff]. **Stricter path:** in the refused branch "an accepted operation remains non-effective" is vacuous. In the WP-7A run of record the authorization leg took the **accepted** branch (202, gate `AwaitingArchive`, reader 503, readable after the receipt) and the content leg the **refused** branch [T: `docs/evidence/sec-bl-01-wp7a/sec-p/SEC-P12-evidence.json`]. Committed-but-unreceipted content is held by the deterministic E-7 suites (`E7ForgedDeliveryStateAndReceiptFieldsNeverReleaseTheRevision`, `E7AReceiptOfAnotherHeadNeverReleasesThisHead`, `AStoppedContentRevisionIsNeverReleasedByRestartForgedResetOrOutage`) [T: G08 and WP-7A runs]. **Condition:** SEC-P12 counts as passed only while a run of record demonstrates the accepted-and-held authorization path; the Azure staging SEC-P12 must demonstrate the accepted-and-held path for **both** streams deterministically (PP-15). The pre-A-9 pass/fail history is not rewritten |
| CN-4 | Moderate | Decision 5 makes `T_inc` = 5 min a production acceptance criterion; OD-DV-6 had recorded 5 min as a Development/Testing measurement default. Measured: idle incremental sweep 374.1 s at 10k D+ entries (> 300 s) [T: WP-7A §6.6] | A tightening owner decision under §14.6, not a conflict. Consequence: the M2F3-43 latency condition "within `T_inc` + sweep duration" holds as a formula, but at ≥ 10k D+ entries on this rig the 5-minute cadence itself cannot be kept. No record may state "detected within 5 minutes" for such histories |
| CN-5 | Minor (wording) | "One-second assurance window" | Documented window = Φ (1 s) + one establishment's duration. Observed: cold establishment p99 170.3 ms (single); 3,198 ms p99 under 16-way cold concurrency on Azurite; J2 P-3 window 711–821 ms [T: WP-7A §5, §9]. Plus L-9 across a restart |
| CN-6 | Minor (open decision) | D-7A-8 (accept the accelerated AC-DR-11 seed as equivalent) was not decided | Stays open; prerequisite of PP-11 |
| CN-7 | Minor (identifier) | The package title uses "WP-8"; contract §17 defines **WP-8** as the production prerequisites (§14), and §24.10 refers to "production until WP-8" | This consolidation is documentation only and does **not** start contract WP-8. PP-01..PP-15 are the input to contract WP-8, which needs its own authorization |
| CN-8 | Minor (reading) | Decision 2 accepts the OI-DR-7 remediation without the word "close" | Recorded as **resolved for Development/Testing**, open for production; a one-row change if the owner intended otherwise |
| CN-9 | Minor (clarity) | AC-DR-13 is "Owner acceptance; claims limited to Development/Testing"; decision 8 forbids presenting it as unconditional | Recorded as **conditionally met**: covers the executed Development/Testing evidence of WP-1..WP-7A only; excludes AC-DR-11, production and every item in §6 |

No conflict requires a rule change. None was made.

## 3. M2F3-43 determination

**Technical verification (recorded).** Within its demonstrated Development/Testing scope, delivered-row tamper detection is verified:

| Case | FU-M2f3-01 (2026-11-17) | Development/Testing result | Evidence |
|---|---|---|---|
| EG-1 deletion of a delivered evidence row | Not detected | Detected `REVISION_GAP` (full sweep) | [T: `M2F343Verifier…` tests; WP-7 and WP-7A runs of record] |
| EG-2 consistently re-digested, never re-sent | Not detected | Detected `LINK_MISMATCH` at the successor | same |
| EG-3 altered outside the newest eight | Not detected | Detected `BODY_DIGEST_MISMATCH` | same |
| ADV-01..ADV-10 on delivered rows, all three streams | — | Detected, PostgreSQL 18 + Azurite, two runs of record each in WP-7 (164/167) and WP-7A (199/203, 4 opt-in skips) | `docs/evidence/sec-bl-01-wp6-wp7/`, `docs/evidence/sec-bl-01-wp7a/` |
| Historical replacement below the checkpoint (VF-15) | — | Detected by the full sweep without a re-baseline | WP-7A §4 |

Scope: detection, not prevention; alert-only; latency `T_inc` + incremental duration above the checkpoint and `T_full` + full duration below it (CN-4); residuals R-DR-1, R-DR-2, J2 P-1, CKR-3, J4 + J6, J2 + J5, JP; Development/Testing stores configuration-separated only (PV-9).

**Status: FAILED (retained).** The governing formal criteria do not yet permit reclassification:

| Criterion | Source | State |
|---|---|---|
| "Delete or alter a delivered evidence row ⇒ chain / archive verification detects it" | `E3_M2F3_CONTRACT_CLOSURE.md` §10.5 | Met technically for delivered rows in Development/Testing |
| "M2F3-43 passes as written for delivered rows (EG-1..EG-3, ADV-01..ADV-10)", PostgreSQL in two runs of record | D-R contract §13.4, §13.1; AC-DR-02 | Met |
| "Acceptance by AC-DR-01..AC-DR-14" | `E3_M2F3_CONTRACT_CLOSURE.md` §14.2 | **Not met:** AC-DR-11 INCONCLUSIVE (deferred); AC-DR-13 conditional only |
| Reclassification is an owner decision on the WP-7 record | A-8 A8-7; A-9 §29.5 | Permitted only if the above are met (decision text) |

**Outstanding criteria for reclassification:**
1. AC-DR-11 executed on the 2M-entry organization, with detection latencies measured against §8.6 (PP-11). Meaningful only after F-7A-5 is resolved (PP-08), because the incremental cadence is currently unmet at scale (CN-4).
2. AC-DR-13 given without conditions for the Development/Testing claims, after item 1.

The only other route would be an explicit owner amendment decoupling M2F3-43 from AC-DR-11. It is not proposed and not made here. PD-15 remains OPEN regardless of M2F3-43 (§4.4).

## 4. Consolidated acceptance matrix

### 4.1 Acceptance criteria AC-DR-01..AC-DR-14 (contract §13.6) after C-161

| ID | Criterion | Status | Evidence / note |
|---|---|---|---|
| AC-DR-01 | ADV-01..ADV-20 at their layers; PostgreSQL in two runs of record | **Met** (Dev/Testing) | WP-6/WP-7 record §5; WP-7A runs of record. ADV-11 in expressible form (CT-8) |
| AC-DR-02 | EG-1..EG-3 inverted; M2F3-43 passes as written for delivered rows | **Met** (Dev/Testing) | §3 |
| AC-DR-03 | MV-01..MV-18 DETECTED | **Met** | WP-7 §8; re-run on WP-7A code, all DETECTED (WP-7A §7). MV-15/MV-17 in expressible variants (CT-9) |
| AC-DR-04 | §13.4 regressions; SEC-P01..SEC-P12 | **Met with condition** | SEC-P 13/13 (WP-7A attempt 2). SEC-P12 under CN-3. TS-M2f-01 and the unidentified 3G contract failure stay separate and unattributed |
| AC-DR-05 | Organization and stream isolation | **Met** | ADV-15 |
| AC-DR-06 | Restart, restore, replay, idempotent retry, checkpoint rollback | **Met** | CKR-3 characterized as specified; restart pair 9/9 + 1/1 (WP-7A §10) |
| AC-DR-07 | Enumeration completeness | **Met** | AE-3..AE-6 |
| AC-DR-08 | Baseline outcomes | **Met** | WP-3/WP-4A records; re-executed |
| AC-DR-09 | Fail closed | **Met** | Including L-12 (decision 3) |
| AC-DR-10 | No AuthorityDb write by verifier or baseline | **Met** | ADV-17 |
| AC-DR-11 | 2M-row measurement | **INCONCLUSIVE — deferred (PP-11)** | Executed at 2k/10k/20k only; Azurite 3.9 infeasible (WP-7A §6) |
| AC-DR-12 | R-FU-1 restated with residuals | **Met** | WP-6/WP-7 §11.3, with CN-1 (P-1 is the R-DR-1 class) |
| AC-DR-13 | Owner acceptance; Development/Testing claims | **Conditionally met** (C-161) | CN-9 |
| AC-DR-14 | Strict specification validation | **Met** | Re-run for this record (§7) |

### 4.2 SEC-BL-01 findings, open items and limits

| Item | State after C-161 | Where |
|---|---|---|
| F-1 (TR-6 cache) | **Remediated; accepted** (Dev/Testing), Φ = 1 s | A-9 §29.1; A10-1 |
| OI-DR-7 (historical archive re-read) | **Resolved for Dev/Testing** (VF-15); production open | A10-2; CN-8 |
| J2 P-2 / P-3 | Prevented at the gate (RV-4); detected by the incremental sweep; Dev/Testing | A-9 §29.3 |
| J2 P-1 | **Accepted residual** (Dev/Testing), R-DR-1 / R-M2f3-6 class, L-11; production only with PP-03 | A10-6; CN-1, CN-2 |
| F-7A-3 / L-12 (outage availability) | **Accepted**; no grace period | A10-3 |
| F-7A-4 (G14-R2 recovery race) | **Deferred** (PP-10); EX-1 re-run interim remedy | A10-4 |
| F-7A-5 (commitment inspection on every sweep) | **Deferred, mandatory pre-production** (PP-08) | A10-5 |
| F-7A-6 / AC-DR-11 | **Deferred** (PP-11) | A10-7 |
| F-7A-1, F-7A-2 | Closed (test-harness fixes; no product change) | WP-7A §11.1 |
| D-7A-8 | **Open** | CN-6 |
| E-6 / E-7 | Closed for Dev/Testing against J1 (C-159) | A-8 |
| G08 | Accepted for Dev/Testing (C-159); production → PP-03 | A-8 |
| CF-1 | Closed for Dev/Testing, delivery-integrity scope (C-158) | — |
| DD-10 | Resolved for the Dev/Testing single-store threat model; **open for production** (PP-01) | A-5 |
| OI-DR-1 | Defined as RD-1..RD-10 (C-157) | A-6 |
| OI-DR-2 | Superseded by G08's dedicated read-only verifier role (Dev/Testing); production separation → PP-03 | A-7 G8-6 |
| OI-DR-3, OI-DR-4 | Open; before production → PP-13 | §19 |
| OI-DR-5 | Open; needed for any EXCEPTION_ACCEPTED baseline → PP-06 | §19 |
| OI-DR-6 | Dataset fixed; run deferred → PP-11 | §19 |
| OI-DR-8 | Open verification task → PP-01, PP-12 | §19 |
| OI-DR-9 | Disposition adopted (G14 recovery event); **not implemented** → PP-05 | A-2 §22.5 |
| OI-DR-10 | Disposition adopted; **implementation deferred** → PP-07 | A-2 §22.5 |
| OI-DR-11 | Adopted (no sampling below the checkpoint); reconsider with PP-08 | A-2 §22.5 |
| OI-DR-12 | Implemented as M2 (A-3); DD-10 as above | A-3 |
| SC-R3 (permanent lockout never an acceptable final production capability) | **Open production blocker** → PP-05 | A-6 §26.4 |
| §24.10 provisional defaults (SR-OD-03/07/14/15/17), SR-OD-05, SR-OD-16 | Retained without loosening (A-5); production decisions → PP-04, PP-13 | A-4, A-5 |
| SR-B, SR-D, SR-E | Not implemented → PP-05 | A-4 §24.4 |
| R-DR-1..R-DR-11 | R-DR-1, R-DR-4 accepted residuals; R-DR-2 inherent (bounded by sweep latency); R-DR-3, R-DR-6 → PP-01/PP-03; R-DR-5 → PP-05; R-DR-7 → PP-02; R-DR-8 → PP-08/PP-11/PP-12; R-DR-9 tested in WP-7; R-DR-10 closed with CF-1 (Dev/Testing); R-DR-11 operational | §18 |
| R-G8-1..R-G8-8 | R-G8-1 read per CN-1; R-G8-2 → PP-01; R-G8-3 → PP-06; R-G8-5, R-G8-7 → PP-03/PP-12; R-G8-8 → PP-09 | G08 record |

### 4.3 Increment acceptance ledger (Development/Testing only)

| Increment | Contract | Owner acceptance |
|---|---|---|
| WP-0 contract | §1–§20 | Accepted |
| WP-1, WP-2 | — | Accepted |
| WP-3, WP-4 | — | Accepted |
| WP-4A | A-1 §21 | Accepted (WP-5a package) |
| WP-5a | A-2 §22 | Conditionally accepted; corrected by WP-5a.1 |
| WP-5a.1 | §22.8 | Accepted |
| WP-5a.2 | A-3 §23 | No separate acceptance entry found in the repository **[NV]**; its rules underlie G14-R2 (accepted, C-156) and are exercised by the accepted WP-7A runs of record |
| G14-R1 (design), G14-R2 | A-4 §24, A-5 §25 | Accepted (C-156) |
| WP-5b | A-6 §26 | Accepted (C-158) |
| G08 / E-6 / E-7 | A-7 §27, A-8 §28 | Accepted (C-159) |
| WP-6 | A-8 | Accepted (C-160) |
| WP-7 | A-8 | Executed evidence accepted, not formal acceptance (C-160); its scope was re-executed in the WP-7A runs of record |
| WP-7A | A-9 §29, A-10 §30 | **Accepted (C-161)** |

### 4.4 PD-15 (82 closure tests; `E3_M2F3_CONTRACT_CLOSURE.md` §14.1)

| Class | Count | Tests | Change under C-161 |
|---|---|---|---|
| Passed | 74 | — | None |
| Passed in reduced form | 5 | M2F3-28 (binding inserts), -29 (two buckets), -31 (host archive loop; re-verified under D+ §13.4, scope unchanged), -42, -78 | None |
| Failed | 1 | **M2F3-43** | FAILED retained; technical verification recorded (§3) |
| Inconclusive | 1 | M2F3-44 (TS-M2f-01; unidentified 3G contract failure) | None |
| Not executed | 1 | M2F3-33 (bound organization, OPEN-M2f-4; no prohibited data-key binding may be inserted) | None |
| Mutations MZ-01..MZ-36 | 36 | 34 detected; MZ-22 equivalent survivor (accepted); MZ-03 not executable as written | None |

**PD-15: OPEN.** Closing SEC-BL-01 is necessary but not sufficient (contract §15).

### 4.5 Evidence index

`docs/evidence/sec-bl-01-wp3-wp4/`, `sec-bl-01-wp4a/`, `sec-bl-01-wp5a/`, `sec-bl-01-wp5a1/`, `sec-bl-01-wp5a2/`, `g14-r2/`, `sec-bl-01-wp5b/`, `sec-bl-01-g08/`, `sec-bl-01-wp6-wp7/`, `sec-bl-01-wp7a/` (runs of record, SEC-P, mutations, measurements, AC-DR-11 probe). Records: `SEC_BL_01_WP1_WP2_IMPLEMENTATION_RECORD.md` through `SEC_BL_01_WP7A_VERIFICATION_RECORD.md`, `G14_R2_IMPLEMENTATION_RECORD.md`, `SEC_BL_01_P2_READINESS_RECORD.md`. All evidence is synthetic, from disposable stacks.

## 5. Final acceptance status

| Item | Status |
|---|---|
| Development/Testing workstream (WP-1..WP-7A) | **Concluded.** Implementation and executed evidence accepted for Development/Testing (§4.3) |
| SEC-BL-01 | **OPEN.** Not closed while AC-DR-11 is INCONCLUSIVE, AC-DR-13 conditional, M2F3-43 FAILED and §6 production-blocking items are outstanding |
| PD-15 | **OPEN** (§4.4) |
| M2F3-43 | **FAILED** (technically verified in Dev/Testing; §3) |
| AC-DR-11 | **INCONCLUSIVE** (deferred) |
| AC-DR-13 | **Conditionally met** |
| G14 | **OPEN** (SR-B/SR-D/SR-E, SC-R3, OI-DR-9, recovery epoch) |
| DD-10 | Dev/Testing resolved; **production open** |
| Production assurance | **None established** |

## 6. Consolidated pre-production backlog (PP-01..PP-15)

Every item is **production-blocking**: none may be waived by a business exception (CLAUDE.md §4X, PB-12), and each needs its own owner authorization before work starts. Priorities: **P0** trust-boundary foundation: no production D-R security claim exists without it. **P1** mandatory acceptance evidence or an owner-set production criterion. **P2** operational hardening required before production.

### 6.1 Summary

| ID | Item | Priority | Depends on | Azure needed? | Blocks |
|---|---|---|---|---|---|
| PP-01 | Independent immutable archive and verification stores | P0 | PP-03; PB-05, PB-10, PB-11 | Yes | Production; DD-10 production; R-DR-3/-6 |
| PP-02 | Independent monotonic anchoring | P0 | PP-01, PP-03 | Yes | Production; CKR-2 |
| PP-03 | Production identity separation and privileged-access controls | P0 | PB-08, PB-11, G08 on Azure | Yes | Production; CN-2 |
| PP-04 | Two-human recovery authorization | P0 | PP-01, PP-03; PB-08 step-up; HSM key custody | Yes | Production recovery |
| PP-05 | Broader G14 recovery | P0 | PP-01..PP-04 | Partly (design and local rehearsal: no) | Production; SC-R3; G14 |
| PP-06 | Historical-entry compatibility and controlled baselines | P1 | PP-01, PP-03; OI-DR-5 decision | Partly | Production use of any store with history |
| PP-07 | Checkpoint growth | P2 | PP-02, PP-13 | Partly | Production |
| PP-08 | Full and incremental verification performance (F-7A-5) | P1 | Owner-authorized A-6 amendment | No (implementation); yes (production measurement, PP-12) | Production acceptance (decision 5); PP-11 |
| PP-09 | Delivery and trusted-receipt performance | P2 | PP-01 | Yes (measurement) | Production |
| PP-10 | Recovery-race remediation (F-7A-4) | P2 | Owner-authorized A-3/A-4 amendment; PP-05 coordination | No | Production |
| PP-11 | AC-DR-11 two-million-row measurement | P1 | PP-08; D-7A-8; approved store option | Only if the Azure store option is chosen | SEC-BL-01 closure; M2F3-43 reclassification |
| PP-12 | Azure performance validation | P1 | PP-01, PP-02, PP-03, PP-08, PP-09; staging authorization | Yes | Production; §14.6, §14.7 |
| PP-13 | Monitoring and retention policies | P1 | PP-01, PP-03 | Yes | Production; PB-10 |
| PP-14 | Independent security assessment | P0 | PP-01..PP-13 in staging | Yes | Production; §14.8 |
| PP-15 | Outstanding formal security acceptance | P0 | PP-01..PP-14 | — | SEC-BL-01, PD-15, Level 2 production |

Suggested order: PP-03 → PP-01 → PP-02 → PP-04 and PP-13 → PP-05 and PP-06 → PP-08 → PP-11 → PP-09 and PP-12 → PP-07 → PP-14 → PP-15. PP-10 can be done at any time. Items that need no Azure resource (PP-08, PP-10, the PP-05 design, the PP-06 procedure and the PP-11 run with an approved local store) can proceed in Development/Testing once authorized.

### 6.2 Scope and acceptance criteria

| ID | Scope (sources) | Acceptance criteria |
|---|---|---|
| PP-01 | Contract §14.1–§14.3; PV-9; DD-10 (production open, A-5); A-7 L-3 / R-G8-2 (J4 + J6 fabricate receipts); R-DR-3, R-DR-6; J4/J5/J6 rows of §2.2; A-9 §29.1 production requirement; OI-DR-8; PB-10 | Separately administered Azure Storage for archive, journal-verification store and checkpoint store in approved Canadian regions; locked version-level time-based immutability (WORM); shared-key access disabled; Entra RBAC with create-only writer identities. Executed Azure tests: J4 overwrite and delete refused; J5 immutability or container removal refused; enumeration paging consistent (AE-1..AE-8, OI-DR-8); receipt fabrication requires two independently administered identities. Owner closes DD-10 for production |
| PP-02 | CK-6, CKR-2, R-DR-7 (CKR-3), Q-4; A-9 §29.1 (independently anchored instance identity); E-14 (no ledger code); PB-12 | Every checkpoint's digest, organization, stream and sequence appended to Azure Confidential Ledger with retained receipts; the ledger-anchored head retrieved and verified on every sweep; `CHECKPOINT_STORE_REGRESSION` after a restart (ADV-19, production form); anchored archive-instance identity; ledger outage ⇒ inconclusive, fail closed; Canadian replication verified (PB-10). An anchored **authorization** head (J2 P-1 option (b)) is **not** in scope (decision 6) |
| PP-03 | §14.2, §14.4; OD-DV-5, Q-8 (verifier Authority-hosted only in Dev/Testing); G08 on Azure (CLAUDE.md §4Q; R-G8-5 F-E2 grant window; R-G8-7 `azure_pg_admin`); J2 (CN-1, CN-2); PB-08, PB-11 | Distinct Entra workload identities: Authority writer (create-only entries), verifier (read-only entries; create-only checkpoints and findings; ledger append), recovery approvers, and store and AuthorityDb administrators, each separate from one another and from business deployers. AuthorityDb administration under PIM/JIT with independent audit (pgAudit to an independent sink). Verifier as a separate workload and pipeline. G8-1..G8-6 re-verified on Azure PostgreSQL including `azure_pg_admin` behaviour; F-E2 window closed or accepted by the owner; the relevant PG-01..PG-20 negative tests executed. Production records state J2 P-1 as residual under this separation (CN-2) |
| PP-04 | A-4 §24.2 (ES256 approver keys from deployment configuration); §24.10 provisional defaults retained (A-5); SR-OD-05 (requester permission), SR-OD-16; "no two-human assurance" (§24.11, §26.8); ADR-PB-008 two-person recovery; CLAUDE.md §4R item 6 | Approver keys non-exportable in Managed HSM (or an approved equivalent) under distinct human identities with fresh FIDO2 step-up; requester, approvers and executor separated by identity, not configuration; SR-OD-03/05/07/14/15/16/17 confirmed or replaced by owner decision; recovery records in the PP-01 independent store; executed staging tests of forged, single, duplicate, expired and removed-key approvals (G14-R1 matrix); audit evidence retained |
| PP-05 | SC-R3; SR-B, SR-D, SR-E; OI-DR-9 (lost checkpoint chain); J3 restore classification and recovery epoch (§12, §14.5); Q-3 (`ARCHIVE_CHAIN_BREAK` reserved); OD-DV-4; R-DR-5; `trust_authority/COORDINATED_DISASTER_RECOVERY.md` DR-01..DR-36; BD-1, BD-2 | An approved contract for SR-B/SR-D/SR-E and OI-DR-9; recovery epoch implemented; every SC-R3 stop class resolvable without permanent lockout and without trusting an old snapshot; DR-01..DR-36 executed, including non-restoration of a revoked grant (CLAUDE.md §4R example) and ADV-14 classification; owner approval |
| PP-06 | A-7 L-2 / R-G8-3 (entries without W2-S commitments fail closed); persistent Development stack (no sentinel; SC-R4); E-12 (jsonb-era rows); Q-7 / R-DR-4; BL-1..BL-10; A-1 §21.1 accepted exceptions; OI-DR-5; SR-OD-15; RST-1..RST-3 | An owner-approved procedure for bringing any non-disposable store under D+: baseline to CLEAN or EXCEPTION_ACCEPTED with per-class owner dispositions (OI-DR-5); sentinel with new-instance evidence; handling of pre-W2-S heads; no suppressed finding and no rewritten row; Reduced assurance carried and reported. Rehearsed on a copy, never on live data; executed tests for jsonb-era rows, pre-W2-S heads and a rule-set version change |
| PP-07 | OI-DR-10 (disposition adopted, implementation deferred); §21.5; measured 620.9 B per checkpoint, ≈ 196 MB per organization per year [extrapolated], full-chain discovery 23.1 s at 2,016 checkpoints (WP-7A §9); OI-DR-4 | OI-DR-10 rules implemented (at most one unchanged-revision checkpoint per hour; append-only; no deletion or compaction); head discovery bounded and measured at one year and at the approved retention volume within an owner-approved budget; CKR-3 limits restated; executed tests |
| PP-08 | F-7A-5; decision 5; A-6 §26.6 (VC-1..VC-11 scope); IV-1; VF-15 cost (full sweep 461.3 s at 21.5k rows; ≈ 12 h at 2M [extrapolated]); OI-DR-11; 2.03 GB managed allocations per baseline and sweeps; R-DR-8 | An owner-authorized A-6 amendment; the incremental sweep (idle and with new entries) completes within **`T_inc` = 5 min** at the PB-09 envelope and on the AC-DR-11 2M-entry organization on the production store type, so the cadence holds without overlap; commitments at or below the checkpoint covered by the full sweep with no loss of detection (mutation evidence for VC-1..VC-9 above and below the checkpoint); the full sweep within `T_full` = 24 h with bounded memory; detection latencies against §8.6 measured. **Mandatory before production acceptance** |
| PP-09 | D+ drain 7.7–9.5 entries/s and pass p50 668 ms (Azurite); cold establishment p50 126 ms (10 store operations); 16-way cold p99 3.2 s; R-G8-8 (no single-flight across verifiers); Φ = 0 p50 21.5 ms; content-release cold p50 89.9 ms; L-12; PB-09 targets | On Azure staging: D+ delivery lag below the 15-minute threshold at the PB-09 sustained load (50 requests/s); gate and content-release p95 within the PB-09 read budget with a documented Φ (≤ 30 s, no grace period, L-12 kept); single-flight implemented or cold-establishment latency accepted on measurement; store-outage behaviour monitored and runbooked |
| PP-10 | F-7A-4; A-4 EX-1/EX-6; A-3 SM-4; deterministic test `F7A4ADeliveryPassBetweenTheRowHintAndTheExecutionRecordHoldsTheStreamUntilTheExecutorIsReRun` | One remediation approved and implemented (executor order, an SM-4 exception for decided recoveries, or D+ re-scheduling an executed lift); the deterministic test inverted to assert completion without a re-run; the concurrent test repeated across full-suite runs without needing the re-run, security assertions unchanged; mutations DETECTED. Alternatively, an explicit owner acceptance of the EX-1 re-run as the production remedy, with an alert and a runbook (PP-13) |
| PP-11 | AC-DR-11; OI-DR-6 dataset; D-7A-7 (deferred); D-7A-8 (open); WP-7A §6.4 plan; F-7A-6 | D-7A-8 decided; store option approved (Azurite with an external SQL metadata store, or Azure Storage in Canada Central, a paid resource); executed 2M-entry synthetic organization plus 500k over four organizations with W2-S commitments; D+ added latency per delivery, incremental and full sweep duration, archive reads and enumeration, and detection latencies against §8.6 measured and recorded. No extrapolation as a substitute. Run after PP-08 |
| PP-12 | Contract §13.6 "not proven" list; §14.6, §14.7; OI-DR-8; R-G8-7; PB-09 benchmark matrix; ADR-PB-003 staging reproduction; PB-10; Managed HSM throughput (G04) | Azure staging benchmarks with every Level 2 control enabled; Blob enumeration and paging consistency, immutability semantics, ledger behaviour and Azure PostgreSQL trigger and grant semantics verified; PB-09 p50/p95/p99 recorded; production targets approved by the owner (§14.6) |
| PP-13 | OD-DV-3 (alert-only verifier: alert routing is the response control); OI-DR-3; OD-DV-6 thresholds (delivery lag 15 min, staleness 15 min); OI-DR-4; OI-DR-10 retention; SR-OD-16; PB-10 legal hold; PB-12 evidence retention; PD-14 monitoring routes; L-12; F-7A-4 held stream | Approved routes for every security and operational finding (§9) to an independently administered SIEM with named on-call ownership; staleness, lag, held-stream and store-outage alarms; approved retention and legal-hold policy (Canadian locations) for archive, verification store, checkpoints, findings, recovery records and evidence; executed alert-delivery tests with no loss of mandatory alerts |
| PP-14 | Contract §14.8; V2-IND-01; ADR-PB-003, PB-12 independent Security Owner review | A reviewer independent of the implementer and of the owner reviews D-R A-1..A-10, the implementation, the evidence and the production configuration; findings dispositioned; report retained as PB-12 evidence |
| PP-15 | AC-DR-11; AC-DR-13 (conditional); M2F3-43; SEC-BL-01; PD-15 (M2F3-33, -28, -29, -31, -42, -44, -78; OPEN-M2f-4; TS-M2f-01; the unidentified 3G contract failure; MZ-22, MZ-03 dispositions); DD-10 production; SC-R3; §24.10 decisions; D-7A-8; CN-3 SEC-P12 condition; SEC-P01..SEC-P12 in Azure staging (ADR-PB-003); PB-12 release-evidence chain; Stage B (release-only, not authorized) | Every item resolved by executed evidence or an explicit owner decision; M2F3-43 reclassified only under §3; SEC-BL-01 closed by the owner with AC-DR-01..AC-DR-14 all met; PD-15 closed under `E3_M2F3_CONTRACT_CLOSURE.md` §10 and §14.1; staging SEC-P12 demonstrating the accepted-and-held path for both streams; Level 2 production certification remains a separate gate |

## 7. Validation of this increment

| Check | Result |
|---|---|
| Contract §1–§20 (read-only `scripts/dev/sec-bl-01-contract-hashes.py`, before and after A-10) | H-WP0 `4a8bd1b1…ef12e` and H-AWK `3549e3ba…f8f2` unchanged; §21–§28 per-section hashes unchanged; §29's original bytes unchanged (its range now also contains the separator before §30) |
| Strict specification validation | `scripts/generate-spec-manifest.py` (manifest digests refreshed for the edited and new documents; 304 files), then `scripts/validate-specifications.py --strict`: **STRICT VALIDATION PASS** (tracked 304; approved ADRs 14; owned pages 75) |
| Builds, tests, mutations, Docker, Stage B | Not run (not authorized) |

## 8. Not claimed

- No closure of SEC-BL-01, PD-15, G14, DD-10 (production), OI-DR-9, SC-R3 or any production gate; no reclassification of M2F3-43.
- No unconditional AC-DR-13; no AC-DR-11 result; no extrapolation accepted as measurement.
- No production assurance, Azure evidence, store immutability or independence, independent anchoring, two-human assurance, independent review or certification.
- No start of contract §17 WP-8 and no authorization of any PP item.
- No product code, test, configuration, infrastructure or persistent environment change; no Stage B; no commit, push, merge or deployment.
