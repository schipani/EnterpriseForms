# G14-R1 — Threat Model and Trust Boundaries for Audit-Stream Recovery (DRAFT)

**Status (2026-11-18):** **DRAFT for owner review; not authoritative; nothing implemented.**
- Attacker classes are J1–J6 and JP of contract §2.2, used unchanged.
- The mechanisms are those of `G14_R1_DRAFT_AMENDMENT_A4.md`.
- Owner-led; not an independent review.

## 1. Trust boundaries

| Asset | Where it lives | Who can change it (Development/Testing) | Who can change it (production, with WP-8 and G08; **not available**) | Trusted for |
|---|---|---|---|---|
| Journal rows and delivery state | AuthorityDb | J1 (INSERT, UPDATE), J2 (everything) | Same, narrowed by M-6 for J1 | **Nothing** in recovery decisions (SR-INV-3) |
| Archived entries, sentinel, stop markers, recovery mirror, pre-commitments | Archive (Azurite, public development key, E-11) | J4, J5, the Docker administrator | Immutable, create-only identity; J4 can still **create** at unused keys (R-DR-6); J5 is constrained by locked immutability | Evidence, bound by digest; markers and records, self-authenticating or digest-bound |
| Findings, checkpoints, AX records, recovery records (primary), pre-commitment mirror | Journal-verification store (second Azurite, generated key) | J6, the Docker administrator | Immutable store, verifier and recovery identities only, ledger anchoring | As above |
| Approver public keys and mode | Protected deployment configuration | Whoever controls the deployment (outside J1–J6) | Protected release pipeline (PB-11), independent security approval | **Root of recovery trust** |
| Approver private keys | Outside the system (Development: one person's synthetic keys; production: Entra step-up per human) | The key holder | Each human approver | Approval |
| Recovery tool and executor | Development: Authority host CLI under the Authority composition. Production: separate workload | Development: same administrator as everything | Separate identity (G14 §9) | Executes checks; **cannot approve** |
| Authority runtime process | Container | JP (out of scope) | Same | Applies D+ and the lift check |

**Central claim.** A stop is lifted only by two signatures from keys whose public halves are pinned outside AuthorityDb and outside both stores. No single store administrator, AuthorityDb actor, the runtime credential or the recovery tool can produce such a lift without the approver private keys. **This claim holds only outside JP and only if the deployment configuration is protected.**

## 2. Attacks

| # | Attack | Actor | Mechanism | Outcome (designed) | Development/Testing demonstrable? | Production needs |
|---|---|---|---|---|---|---|
| SR-T1 | Reset the failed row to `Pending`, clear its code, mark it `Anchored`, forge a receipt | J1/J2 | SR-DL-2: AuthorityDb never resolves a stop; discovery is record-based (SM-5, SM-8) | Stream stays stopped | Yes (PG) | — |
| SR-T2 | Forge a recovery-authorizing state in AuthorityDb (a "recovery" table or a hint code `DPLUS_RECOVERY_AUTHORIZED:*`) | J1/J2 | The hint is a fetch hint only; the lift needs SR-DL-1 records | Stopped | Yes | — |
| SR-T3 | Delete or replace archived evidence (predecessor, row copy) after a stop | J4/J5 | The stop persists (A-3). After recovery, the evidence is compared with the statement's `expected` (SR-DL-3/4) | Stopped, or a new stop at generation `g + 1` | Yes | Immutability prevents replacement; creation at unused keys remains (R-DR-6) |
| SR-T4 | Restore the missing predecessor to trigger resumption without recovery | J4 | SR-DL-2 and SR-EP-6: changed evidence never lifts | Stopped | Yes | — |
| SR-T5 | Create archive `k` directly (bypass by presence) | J4 | SR-DL-2: presence does not resolve a stop; the stop at `k` stays unresolved, so `k + 1` is not delivered | Stopped | Yes | — |
| SR-T6 | Delete stop markers | J4/J5 | A-3 SM-6 (finding survives; completion or `DPLUS_STOP_RECORD_INCOMPLETE`) | Stopped | Yes (existing) | — |
| SR-T7 | Modify or delete findings | J6 | A-3 SM-6 (marker survives) | Stopped | Yes (existing) | — |
| SR-T8 | Delete both the marker and the finding before any recovery | J4/J5 + J6 | **Known limit** (§23.4) | Resumes | Pinned (existing) | Independent stores and identities (WP-8) |
| SR-T9 | Delete recovery records (statement, approvals, execution) | J4 and/or J6 | SR-INV-6: deletion moves towards stopped; one valid copy in either store suffices | Stopped (denial of service only) | Yes | — |
| SR-T10 | Forge a statement or approval | J1/J4/J6, the recovery tool, the runtime credential | ES256 over `statement_sha256` with pinned keys; the runtime holds public keys only | Rejected (`SR_RECORD_INVALID`, alert) | Yes | — |
| SR-T11 | Replay an approval or authorization to another stop, stream, organization or generation | Any store actor | The statement binds the stop set (names + SHA-256), scope, generation and evidence; single-use `jti` and `recovery_id`; one execution per stop set | Rejected | Yes | — |
| SR-T12 | Replay an old authorization after a new failure at the same revision | Store actor | SR-SM-1 generation: the new stop has a new name and identity, not in the old stop set | Stopped | Yes | — |
| SR-T13 | Execute an approved but cancelled, rejected or expired statement by deleting the cancellation or rejection and forging an execution record | J4 + J6 (both copies) | Development/Testing: possible when the evidence still equals the snapshot. Approvers approved exactly this effect; the cancellation is lost | **Accepted residual R-SR-6** | Pinned as a residual | Immutable stores (cancellations undeletable) plus ledger-anchored execution timestamps |
| SR-T14 | Contradictory copies of a record in the two stores | Store actor | `SR_RECORD_CONTRADICTORY` invalidates that record | Stopped | Yes | — |
| SR-T15 | Compromised Authority runtime | JP | Can skip D+ and the lift check entirely | **Out of scope** | No | Out of D-R scope |
| SR-T16 | Recovery tool or executor compromise (no keys) | Tool | Cannot produce approvals. Can refuse to execute (DoS) or execute exactly what was approved | No unauthorized lift | Yes (negative tests) | Separate identity |
| SR-T17 | Approver key compromise | Key thief | Can approve alone only with **two** keys. With both keys: arbitrary lifts | One key: none. Two keys: total failure of the control; response is removal (SR-OD-17), which re-stops affected streams | Partly (removal test) | Human SoD, PIM, step-up |
| SR-T18 | One person holds both synthetic keys | Development reality | Not two-human evidence (DR-C7-3) | Recorded as synthetic | Labelled | Two humans |
| SR-T19 | Deployment configuration altered to add an attacker key | Deployment actor | Outside J1–J6; this is the root of trust | Total failure | No | PB-11 independent approval of security configuration |
| SR-T20 | Concurrent recovery requests | Operators | One execution record per stop set (create-only); the loser writes `Failed` | One effect | Yes | — |
| SR-T21 | Interrupted recovery (termination at each step) | Fault | SR-AU-7 order (execution record last); idempotent re-run | Stopped until complete; no partial lift | Yes | — |
| SR-T22 | Checkpoint rollback to make an older digest the "anchor" for SR-C1 | J6 | The anchor comes from the discovered checkpoint head; rollback is undetectable across restarts in Development/Testing (CKR-3) | **Residual R-SR-4** (Development/Testing). An older checkpoint pins an older revision, `v ≠ k − 1`, so SR-C1 is ineligible unless the rollback lands exactly at `k − 1` with the genuine digest (then harmless) | Pinned | Ledger-anchored head (CKR-2) |
| SR-T23 | Archive-instance substitution (new sentinel, consistent forged copy) | J4/J5 | Today D+ does not pin the instance (C-SR-4); surviving findings keep stopped streams stopped; the verifier reports `ARCHIVE_REGRESSION`. With SR-SM-4: `INSTANCE_CHANGED` stops D+ | Today: findings and stops survive in the verification store. Proposed: stopped until SR-D | Yes | Immutability plus ledger |
| SR-T24 | Restore AuthorityDb to before a recovery (J3) | J3 | Records are outside AuthorityDb; the lift does not depend on it | Unchanged | Yes | — |
| SR-T25 | Restore a store to before a recovery (rollback of records) | J4/J6 | The lift disappears, so the stream re-stops (fail closed). Rollback of **pre-commitments** combined with evidence replacement resumes delivery (the A-3 known-limit class) | Fail closed / known limit | Yes | Immutability plus ledger |
| SR-T26 | Evidence altered before first evaluation | J1 | R-DR-1 (accepted) | Delivered as genuine | Pinned (existing) | — |
| SR-T27 | Low-entropy disclosure through recovery records | Reader of the verification store | Only envelope/journal digests and names, no body digests (SR-EP-1, SR-RC-3); envelopes only in the archive, which already holds them | No new disclosure | Inspection | — |

## 3. What Development/Testing can and cannot demonstrate

**Can demonstrate (behaviour only):**
- The lift logic.
- Fail-closed behaviour against J1, J2, J3, and against J4, J5 or J6 acting **alone**.
- Signature, binding, replay and generation rules.
- Ordering and idempotency under interruption.
- Concurrency.
- Isolation.
- W2-S detection of changed evidence after termination.

**Cannot demonstrate:**
- Independence of the stores (PV-9; one Docker administrator).
- Two-human approval (synthetic keys).
- Protection of cancellations against deletion (R-SR-6).
- Checkpoint freshness (CKR-3).
- Protection against J4+J6 collusion or against JP.
- Production Entra step-up.
- Immutability semantics.
- Ledger anchoring.

## 4. Residual risks (if A-4 were implemented)

| ID | Risk | Disposition proposed |
|---|---|---|
| R-SR-1 | J4/J5 + J6 removing both stop records before recovery (A-3 known limit, unchanged) | Accept in Development/Testing; WP-8 |
| R-SR-2 | J4/J5 + J6 removing a W2-S pre-commitment from both stores, then replacing evidence | Same class as R-SR-1 |
| R-SR-3 | All stores configuration-separated only in Development/Testing | Accepted (PV-9) |
| R-SR-4 | Checkpoint rollback (CKR-3) undermines SR-C1 anchors across restarts | Accept in Development/Testing; SR-C1 production claims need the ledger |
| R-SR-5 | Approver keys synthetic, held by one person | Accepted, labelled; production `Unavailable` |
| R-SR-6 | An approved but cancelled or expired statement can be executed by a store administrator able to delete the cancellation from both stores | Accept in Development/Testing; production immutability plus ledger timestamps |
| R-SR-7 | Removing a compromised key re-stops every stream it recovered (availability) | Accept (fail closed) |
| R-SR-8 | SR-B archives rows ≥ `k` whose authenticity is unanchored (Reduced, permanently) | Accept only with the per-recovery owner disposition |
| R-SR-9 | The deployment configuration is the root of trust | PB-11 |
| R-SR-10 | Verification-store listing outage stops audit delivery (now including recovery and pre-commitment listings) | Accepted availability cost (as R-W5A2-4) |
| R-SR-11 | W2-S doubles audit-stream object count and adds a write per revision | Measure (AC-DR-11) |
| R-SR-12 | Compromised runtime (JP) bypasses everything | Out of scope |
