# G14-R1 — Recovery State Machine Specification (DRAFT)

**Status (2026-11-18):** **DRAFT for owner review; not authoritative; not implemented.** It specifies the states of the proposed amendment A-4 (`G14_R1_DRAFT_AMENDMENT_A4.md`); the rationale is in `G14_R1_CONTROLLED_AUDIT_STREAM_RECOVERY_DESIGN.md`. Owner-led; not an independent review.

## 1. Principle: states are derived, never stored

There is **no mutable recovery state field** anywhere: not in AuthorityDb, not in a store. Each state below is a **predicate over append-only, create-only records** (A-3 stop records, A-4 recovery records, W2-S pre-commitments, archived entries). Moving to a new state means **creating** a record. A state can never be reached by editing or deleting one. Consequences:
- Deleting a record can only move the derived state *backwards towards stopped*. It never moves it towards delivery.
- AuthorityDb `archive_delivery_state` and `last_archive_error_code` are hints for the work loop's fetch only (SM-8, SR-DL-5).

Scope of one machine: one organization, the audit stream, one stop set (one failed revision at one generation).

## 2. States

| State | Predicate (derived from records) | Delivery of the stream |
|---|---|---|
| **N — Normal delivery** | No unresolved stop record for the stream | Delivers |
| **F — Integrity failure detected** | D+ holds a stream-stopping verdict in memory and neither record is confirmed (`DPLUS_DURABLE_STOP_NOT_ESTABLISHED`). With W2-S, the commitment for `k` is durable | Held in memory. Without W2-S this is the §23.3 residual; with W2-S, re-derivation from the committed evidence is deterministic |
| **S — Durable stop established** | At least one stop record (marker, finding or invalid object) is unresolved and no investigation record exists for it | Stopped (SM-5) |
| **I — Investigation pending** | An investigation record exists for the scope and no statement references it | Stopped |
| **P — Recovery proposed** | A statement exists with fewer than two valid approvals, no valid rejection or cancellation, and no execution record for its stop set | Stopped |
| **A — Recovery approved** | The statement is authorized (SR-AU-3), with no rejection or cancellation and no execution record | Stopped |
| **X — Recovery executing** | The execution record for the stop set names this recovery, and the recovered revision is not yet archived with the expected digest | Lifted (SR-DL-1); D+ applies the strategy rule (SR-DL-3) |
| **C — Recovery completed** | X holds, and the recovered revision (or `k + 1` under SR-B-UNDELIVERABLE) is archived with the expected digest; the outcome record `Completed` exists or can be written | Delivers (the stop stays resolved on every pass) |
| **R — Recovery rejected** | A valid rejection exists for the statement | Stopped (back to I/S for a new statement) |
| **K — Recovery cancelled** | A valid cancellation exists and no execution record exists | Stopped |
| **E — Recovery expired** | The window has passed and no execution record exists | Stopped |
| **Fx — Recovery failed** | Execution preconditions failed (nothing written; outcome `Failed`), **or** D+ refused the recovered revision under the strategy rule (`DPLUS_RECOVERY_EVIDENCE_MISMATCH`, a new stop at generation `g + 1`) | Stopped |

R, K, E and Fx are terminal **for that statement**. The stream remains stopped, and a new statement with a new `recovery_id` can be proposed (Fx with a new stop: for the new stop set).

## 3. Transitions

```mermaid
stateDiagram-v2
    [*] --> N
    N --> F: D+ stream-stopping verdict (in memory)
    F --> S: marker or finding confirmed (SM-4)
    F --> N: termination + evidence replaced (A-3 residual; closed by W2-S)
    N --> S: W2-S commitment differs (DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT)
    S --> I: create investigation record
    I --> P: create statement (tool-built, SR-ST-2)
    P --> A: second valid approval stored
    P --> R: valid rejection stored
    A --> R: valid rejection stored
    P --> K: valid cancellation stored
    A --> K: valid cancellation stored
    P --> E: window passes
    A --> E: window passes
    A --> X: execution record created (SR-AU-7 preconditions pass)
    A --> Fx: execution preconditions fail (nothing written)
    X --> C: recovered revision archived with the expected digest
    X --> Fx: strategy rule refuses (new stop, generation g+1)
    C --> N: all stop records resolved
    R --> I: new statement possible
    K --> I: new statement possible
    E --> I: new statement possible
    Fx --> I: new statement possible (for the current stop set)
    X --> S: an authorization record becomes unverifiable (deleted, key removed)
    C --> S: an authorization record becomes unverifiable (re-stop, fail closed)
```

| # | From → To | Trigger | Guard | Action (create-only) | Actor |
|---|---|---|---|---|---|
| SR-TR-1 | N → F | D+ refusal | Stream-stopping classification | Critical alert | D+ |
| SR-TR-2 | F → S | Marker or finding confirmed | — | Marker, finding (A-3 order) | D+ |
| SR-TR-3 | N → S | W2-S commitment differs | SR-EP-3 | Marker, finding | D+ |
| SR-TR-4 | S → I | Investigation opened | SR-EL-1, SR-EL-6 (containment where required) | `investigation.json`, evidence bytes | Recovery tool (requester) |
| SR-TR-5 | I → P | Proposal | SR-EL-4, SR-EL-5, SR-ST-2 | `statement.json` | Recovery tool |
| SR-TR-6 | P → A | Second approval | SR-AU-3, SR-AU-4 | `approvals/{jti}.jws` | Approvers (via the tool) |
| SR-TR-7 | P/A → R | Rejection | SR-AU-5 | `rejections/{jti}.jws` | Any approver |
| SR-TR-8 | P/A → K | Cancellation | SR-AU-5, before execution | `cancellations/{jti}.jws` | Requester or approver |
| SR-TR-9 | P/A → E | Time | `now > not_after_utc` | none (derived); optional outcome `Expired` | — |
| SR-TR-10 | A → X | Execute | SR-AU-7 (fresh evidence equals snapshot; stop set unchanged; window; anchors) | In order: SR-C1 `action.json` and the create-only predecessor object; row hint (SR-DL-5); **then** `executions/{stop_set_sha256}.json`, which takes effect last | Recovery tool (executor) |
| SR-TR-11 | A → Fx | Execute refused | Any SR-AU-7 check fails | `outcome.json` = `Failed` (reason) | Executor |
| SR-TR-12 | X → C | Delivery | Archive holds the recovered revision with the expected digest | `outcome.json` = `Completed` (archive receipt) | Executor (finalize) or derived |
| SR-TR-13 | X → Fx | Strategy rule refuses | SR-DL-4 | New marker and finding (generation `g + 1`); `outcome.json` = `Failed` | D+ (stop records), executor (outcome) |
| SR-TR-14 | X/C → S | An authorization record becomes unverifiable | Deleted or contradictory copies in both stores, or a key removed | none (fail closed; alert `JournalDeliveryRecoveryRecordInvalid`) | D+ |

## 4. Interaction with the existing delivery states

| Delivery state of row `k` | In stop states S..E, R, K, Fx | In X | In C |
|---|---|---|---|
| `Pending` (marker-only stops, or rows after `k`) | Unchanged; not delivered (SM-5) | Delivered in order once the strategy rule passes | `Anchored` |
| `VerificationFailed` (both records confirmed) | Unchanged, except that during SR-TR-10 the executor sets `Pending` with `DPLUS_RECOVERY_AUTHORIZED:{recovery_id}` just before the execution record (SR-DL-5); the stop is still unresolved then | `Pending` until delivered; SR-B-UNDELIVERABLE: stays `VerificationFailed` permanently | `Anchored` (or `VerificationFailed`, undeliverable) |
| `Anchored` | Not applicable for `k` (an anchored `k` is never evaluated). A forged `Anchored` on an unarchived `k` changes nothing (DP-9, SR-DL-2) | — | — |

The old stop records, the old finding codes and the history of each row stay in the stores. The AuthorityDb code of row `k` is overwritten by the delivery path (it is a projection). The durable history is the records, not the row.

## 5. Concurrency, interruption and restart

| Situation | Behaviour |
|---|---|
| Two proposals for the same stop set | Both may exist; approvals bind one statement digest each. The first execution record (create-only, keyed by `stop_set_sha256`) wins; the second executor gets a conflict, writes `Failed` (`EXECUTION_CONFLICT`) for its own recovery and changes nothing else |
| Two executors of the same recovery | Identical execution bytes are idempotent; both continue idempotently (create-only predecessor write, idempotent hint, idempotent outcome) |
| Executor terminates after the SR-C1 predecessor write, before the execution record | The stop is still unresolved, so the stream stays stopped. The predecessor object stays; it is bound in the authorized statement and equals the trusted anchor. The re-run finds it (identical bytes, idempotent) and continues |
| Executor terminates after the row hint, before the execution record | The work loop fetches `k`, discovery finds the stop unresolved, and the stream stays stopped. The re-run creates the execution record |
| The work loop fetches `k + 1` before `k` | SR-DL-9: no revision above a lifted, unarchived revision is evaluated; no spurious stop |
| Work loop terminates while delivering the recovered revision | DP-8 unchanged: idempotent append on the next pass. With W2-S, the commitment for `k` already exists and must still equal the evidence |
| Restart at any point | No state is held in memory except F. All other states are re-derived from records on the next pass |
| Archive outage during recovery | Listing or reading fails ⇒ the executor writes nothing (SR-AU-7) and D+ ends the pass (DP-7) |
| Verification-store outage | Executor refuses (both copies needed for the evidence check), D+ inconclusive (SM-5) ⇒ the audit stream waits |
| Rejection or cancellation arriving after the execution record | No effect on the lift, which is decided by the execution record, but it is recorded and reported (`LATE_REVOCATION`). Store compromise could hide an earlier cancellation (R-SR-6) |

## 6. Invariants (to be tested; see the test matrix)

| # | Invariant |
|---|---|
| SR-INV-1 | Delivery of a stopped stream resumes only in X or C, and only through SR-DL-1 |
| SR-INV-2 | No transition deletes or rewrites a record; every transition creates records only |
| SR-INV-3 | No AuthorityDb value is part of any guard |
| SR-INV-4 | A statement is effective for exactly one stop set of one (organization, stream) |
| SR-INV-5 | A later failure at a recovered revision is recorded at a new generation, not hidden |
| SR-INV-6 | Removing any recovery record moves the derived state towards S, never towards N |
| SR-INV-7 | Assurance never increases through any transition; SR-B leaves it Reduced permanently |
