# G14-R1 — Test and Acceptance Matrix for Audit-Stream Recovery (DRAFT)

**Status (2026-11-18):** **DRAFT for owner review. No test is implemented or executed.** This is a test design for the proposed amendment A-4 (`G14_R1_DRAFT_AMENDMENT_A4.md`). Specifications are not tests (ADR-PB-003). Stage B is not part of any layer below. Owner-led; not an independent review.

## 1. Layers

| Layer | Meaning |
|---|---|
| **IP** | In-process: transactional AuthorityDb double, in-memory archive and verification store with fault points, synthetic ES256 approver keys generated per test |
| **PG** | Disposable PostgreSQL 18 with two Azurite instances (archive; verification store with a generated key), following the existing gate script pattern (`sec-bl-01-wp4-pg-azurite-gate.ps1`): fresh containers, removed afterwards, evidence scrubbed, no persistent stack, never the `enterpriseforms-dev` project |
| **FUT** | Future, production-grade: immutable stores, independent identities, ledger, Entra step-up, independent adversarial review. Not executable in Development/Testing |

Every IP/PG result demonstrates **Development/Testing behaviour only**.

## 2. Functional and adversarial tests

| ID | Scenario | Expected | Layer |
|---|---|---|---|
| **Authorization** | | | |
| SRT-01 | Authorized SR-A recovery: stop from a D+ defect simulated by a rule toggle in a test build; evidence unchanged; statement, two approvals, execute | Stop resolved; `k` delivered; outcome `Completed`; old marker and finding remain | IP, PG |
| SRT-02 | Authorized SR-C1: `k − 1` absent; copy equals the checkpoint digest at `v = k − 1` | Predecessor created create-only; `k` delivered; assurance unchanged | IP, PG |
| SRT-03 | Unauthorized: an execution record without a statement, or a statement without approvals | Stream stays stopped; `SR_RECORD_INVALID` where malformed | IP, PG |
| SRT-04 | One approval only; two approvals with the same `kid`; a valid approval plus one by an unconfigured key | Not authorized; stopped | IP |
| SRT-05 | An approval over a different `statement_sha256` (the statement edited after signing) | Not authorized; `SR_RECORD_INVALID` | IP |
| SRT-06 | Expired approval (`exp − iat` > 900 s, or execution after `exp`); execution after `not_after_utc` | Executor refuses (`Failed`); nothing else written | IP |
| SRT-07 | Signed rejection before execution; signed cancellation before execution | Never executable; stopped | IP, PG |
| SRT-08 | Separation of duties: approver id equals the requester's id | Refused | IP |
| SRT-09 | Mode `Unavailable` (default), and `SyntheticDevelopment` configured in a non-Development environment | Every execution refused; startup refused for the latter | IP |
| SRT-10 | Key removed from the configuration after a completed recovery | Discovery re-stops the stream (fail closed), alert raised | IP |
| SRT-11 | Key retired (verify-only) with `RetiredOnUtc` after the approvals' `iat` | Recovery stays valid; a new approval by that key is refused | IP |
| **Replay and scope** | | | |
| SRT-12 | Reuse the approvals and statement of organization A for organization B, for another stream, or for another revision | Not resolved (stop set and scope bound); stopped | IP, PG |
| SRT-13 | Re-store an approval under a new name, or reuse a `jti` | Rejected (`jti` is the key; duplicate ⇒ conflict) | IP |
| SRT-14 | A second recovery of the same stop set (new `recovery_id`, new approvals) after the first executed | Execution conflict; `Failed`; no effect | IP |
| SRT-15 | New failure at the recovered revision after recovery (predecessor deleted again before `k` is delivered) | New marker `{k}.r0001.json` and generation-1 finding; old authorization does not cover it; stopped | IP, PG |
| SRT-16 | Organization A and organization B, and audit versus the other streams, with recovery in A only | B and the other streams are unaffected (delivery, records, findings) | IP, PG |
| **Evidence** | | | |
| SRT-17 | Evidence changed between the statement and execution: row `k` rewritten, predecessor created, sentinel replaced | Executor refuses (snapshot mismatch); stopped | IP, PG |
| SRT-18 | Evidence changed after execution, before delivery | D+ `DPLUS_RECOVERY_EVIDENCE_MISMATCH` at generation `g + 1`; outcome `Failed` | IP, PG |
| SRT-19 | SR-C1 copy whose digest ≠ checkpoint `d_v`, or `v ≠ k − 1`, or the checkpoint chain invalid | Statement refused (no anchor; SR-PB-8) | IP |
| SRT-20 | SR-C1 where `k − 1` is present but corrupt | SR-C1 refused (no overwrite, SR-PB-4); only SR-B eligible | IP |
| SRT-21 | Preservation: after any recovery (completed, failed, rejected), every original marker, finding, invalid object, archived object and earlier record is byte-identical; row `k`'s envelope is held in the evidence prefix | Asserted byte-identical | IP, PG |
| SRT-22 | Unavailable or contradictory mandatory evidence (listing fails; the two stores' copies differ) | No statement (SR-EL-5) or `SR_RECORD_CONTRADICTORY` | IP |
| SRT-23 | Stop-record-integrity finding present (`DPLUS_STOP_RECORD_INCOMPLETE`) without a containment attestation | Statement refused (SR-PB-5) | IP |
| SRT-24 | An invalid object under the marker prefix not enumerated in the stop set | Stream stays stopped after execution of a statement that omits it | IP |
| **Concurrency, interruption, restart** | | | |
| SRT-25 | Two operators execute concurrently (same recovery; different recoveries of the same stop set) | Same recovery: idempotent. Different: one execution, the other `Failed` | IP, PG |
| SRT-26 | Executor terminated after each step (after the action, after the hint, after the execution record, before the outcome) | Stopped until the execution record exists; re-run completes idempotently; no partial lift | IP, PG |
| SRT-27 | Authority restarted at each recovery state | The derived state is identical before and after the restart | IP, PG |
| SRT-28 | Work loop fetches `k + 1` before `k` under a lift | SR-DL-9 hold; no spurious stop | IP |
| **Outages** | | | |
| SRT-29 | Archive outage during proposal, execution and the post-execution pass | Proposal and execution write nothing; D+ ends the pass (DP-7); no fabricated finding | IP, PG |
| SRT-30 | Verification-store outage during proposal, execution and discovery | Executor refuses; discovery inconclusive; the audit stream waits; other streams continue | IP, PG |
| SRT-31 | Failed recovery "rollback": execution refused at the predecessor write (conflict) | Nothing after the conflict is written; stopped; the created objects (if any) are bound and genuine | IP, PG |
| **Attacker actions** | | | |
| SRT-32 | AuthorityDb row reset to `Pending`, code set to `DPLUS_RECOVERY_AUTHORIZED:*`, row marked `Anchored` with a forged receipt (J1) | Stopped | IP, PG |
| SRT-33 | Archive `k` created directly without recovery (J4) | Stopped; `k + 1` is not delivered | IP, PG |
| SRT-34 | Delete the statement, approvals or execution record from one store / both stores | One store: lift kept (valid copy). Both: re-stop (fail closed) | IP, PG |
| SRT-35 | **Residual R-SR-6:** delete a cancellation from both stores and forge an execution record | Lift happens (pinned as a residual, documented) | IP |
| SRT-36 | Archive instance substitution with a new sentinel (with SR-SM-4) | `ARCHIVE_REGRESSION/INSTANCE_CHANGED`; stopped | IP, PG |
| SRT-37 | Organization quarantine present (`JOURNAL_BODY_DIGEST_MISMATCH`) | No R1 statement possible (SR-PB-1) | IP |
| SRT-38 | Statement for an authorization or content stream | Refused (SR-PB-2) | IP |
| **W2-S evidence pre-commitment** | | | |
| SRT-39 | Normal delivery writes one byte-identical commitment per revision; retries are idempotent | One object per revision per store | IP, PG |
| SRT-40 | **DD-10 sequence:** terminate after the commitment and the detection, before any stop record; replace the evidence; restart | `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT`; durable stop. This inverts `AcceptedResidualTerminationBeforeAnyDurableRecordThenEvidenceReplacementResumesDelivery` | IP, PG |
| SRT-41 | Same termination with unchanged evidence | Re-evaluation; the same verdict recorded (refusal) or delivery (pass); no recovery needed | IP, PG |
| SRT-42 | Commitment store unavailable | No evaluation; the audit stream waits | IP |
| SRT-43 | Commitments differ between the two stores | `SR_RECORD_CONTRADICTORY`; stopped | IP |
| SRT-44 | Known limit: delete the commitment from both stores and replace the evidence | Resumes (pinned as a residual, R-SR-2) | IP |
| SRT-45 | Rule-set version differs at re-evaluation with unchanged evidence | Re-evaluated; both versions recorded (SR-OD-15) | IP |
| **Verifier** | | | |
| SRT-46 | Verifier full sweep with recovery records and commitments present | Recognized and validated; invalid ones reported; no `ARCHIVE_UNEXPECTED_OBJECT` for canonical objects | IP, PG |
| SRT-47 | SR-B continuation followed by a re-baseline with a disposition referencing the recovery | AX record created; chain Reduced permanently; the verifier observes, does not report, the accepted break | IP, PG |

## 3. Mutations (each must be DETECTED by the tests above)

| ID | Mutation | Detected by |
|---|---|---|
| SR-MV-01 | Lift on the execution record alone, without signature verification | SRT-03, SRT-34 |
| SR-MV-02 | Verify signatures only at execution, not on every pass | SRT-10, SRT-34 |
| SR-MV-03 | Accept two approvals from the same `kid` | SRT-04 |
| SR-MV-04 | Ignore the stop-set binding (match on organization and stream only) | SRT-12, SRT-15, SRT-24 |
| SR-MV-05 | Use AuthorityDb state or code as a lift | SRT-32 |
| SR-MV-06 | Treat archive presence of `k` as resolved | SRT-33 |
| SR-MV-07 | Skip the fresh evidence check at execution | SRT-17 |
| SR-MV-08 | Skip the `expected` comparison at delivery | SRT-18 |
| SR-MV-09 | Write the execution record before the action or the hint | SRT-26 |
| SR-MV-10 | Reuse a generation-0 marker key after recovery | SRT-15 |
| SR-MV-11 | W2-S: evaluate before the commitment is confirmed | SRT-40 |
| SR-MV-12 | W2-S: overwrite a differing commitment | SRT-40, SRT-43 |
| SR-MV-13 | Ignore cancellations and rejections | SRT-07 |
| SR-MV-14 | Allow an overwrite in SR-C1 | SRT-20, SRT-31 |
| SR-MV-15 | Accept `SyntheticDevelopment` outside Development/Testing | SRT-09 |

## 4. Future adversarial and production verification (FUT; not executable now)

| ID | Requirement |
|---|---|
| SRF-1 | Real immutable archive and verification store: deletion of markers, findings, cancellations, recovery records and commitments refused by the platform |
| SRF-2 | Ledger anchoring of statements, execution records and commitment digests; verification of receipts; detection of store rollback (CKR-2) |
| SRF-3 | Entra step-up approval evidence: two distinct humans, PIM, `nonce` bound to `statement_sha256`; forged and replayed tokens rejected |
| SRF-4 | Separate recovery workload identity: it cannot write AuthorityDb beyond the hint, cannot sign, cannot read approver private material |
| SRF-5 | Penetration tests of the recovery CLI or workload (argument injection, path traversal in object names, oversized records) |
| SRF-6 | Independent security review of A-4 and its implementation |
| SRF-7 | PB-12 evidence chain for the release containing the recovery feature |

## 5. Acceptance criteria (for a future implementation increment)

| ID | Criterion |
|---|---|
| SR-AC-1 | SRT-01..SRT-47 pass at their layers; PG in two runs of record on fresh stacks |
| SR-AC-2 | SR-MV-01..SR-MV-15 DETECTED (driver built `--no-incremental`, scratch clone, restore verified) |
| SR-AC-3 | Residual tests SRT-35 and SRT-44 pinned and documented, not hidden |
| SR-AC-4 | Regressions: D+ (`AuditDeliveryEnforcementTests`), the WP-5a.2 PG gate, SEC-P01..SEC-P12 on a disposable stack (N-4; **not Stage B**), full Authority, architecture and contract suites |
| SR-AC-5 | No AuthorityDb write by recovery except the documented hint (statement trace) |
| SR-AC-6 | Specification validation passes (`scripts/validate-specifications.py --strict`) |
| SR-AC-7 | Owner acceptance; claims limited to Development/Testing; DD-10 status decided by the owner, not inferred |
