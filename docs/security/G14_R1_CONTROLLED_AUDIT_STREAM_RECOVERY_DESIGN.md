# G14-R1 — Controlled Audit-Stream Recovery: Design (DRAFT)

**Status (2026-11-18):**

| Item | State |
|---|---|
| Document | **DRAFT for owner review.** Design and documentation only. It is not a contract and not an authorization to implement |
| Authorization | Owner package "G14-R1: Controlled Audit-Stream Recovery Design" (2026-11-18): design and documentation only. Recovery must not be implemented until the owner reviews and explicitly approves the proposed contract |
| Companion drafts | `G14_R1_DRAFT_AMENDMENT_A4.md` (proposed contract amendment), `G14_R1_RECOVERY_STATE_MACHINE.md`, `G14_R1_THREAT_MODEL.md`, `G14_R1_TEST_AND_ACCEPTANCE_MATRIX.md` |
| Implementation | **None.** No code, test, configuration, compose, CI or data change. No recovery was executed anywhere |
| Governing contract | `SEC_BL_01_DR_CONTRACT_CLOSURE.md`, including A-1 (§21), A-2 (§22), its correction §22.8 and **A-3 (§23), which is authoritative**. `SEC_BL_01_A3_DURABLE_STOP_DRAFT.md` is a historical draft and is **not** authoritative (its own status row says so). Nothing here changes accepted contract text |
| SEC-BL-01 / PD-15 / M2F3-43 / DD-10 | OPEN / OPEN / FAILED / **narrowed, not closed** |
| Review | Owner-led; prepared by the implementing assistant; **not an independent review** |
| Baseline | `main` at `a9b32de`, with every uncommitted WP-0..WP-5a.2 change preserved |

**Identifiers introduced** (prefix `SR`, unused in the repository at the time of writing): findings SR-F1..SR-F12; conflicts C-SR-1..C-SR-6; eligibility rules SR-EL-1..SR-EL-8; strategies SR-A..SR-E; permanent blocks SR-PB-1..SR-PB-8; owner decisions SR-OD-01..SR-OD-18. Risks R-SR-1..R-SR-12 are listed in the threat model (§4). The amendment, state machine, threat model and test matrix add further `SR-` identifiers. They are unrelated to G14's DR-01..DR-36 tests, A-2's DD-1..DD-11 and A-3's SM-1..SM-9.

---

## 0. Summary

A D+ stream stop (A-3) is now durable once a marker or a finding exists. **Nothing can lift it**: SM-8 requires an authorized recovery action, and none exists. This design proposes one, narrow enough to implement for the **audit stream in Development/Testing** without building the whole G14 disaster-recovery control plane.

The core idea is that **only cryptographic approvals lift a stop. No state lifts it.**
- **Signed recovery authorization.** A recovery is authorized by a statement built by a recovery tool from the observed evidence. Two distinct approvers each sign it (ES256 JWS), with public keys pinned in protected deployment configuration. This reuses the existing DR-C7-3 verifier contract with a new statement kind. The Authority holds public keys only, so the runtime cannot approve its own recovery.
- **Exact binding.** The statement binds the organization, the stream, the failed revision, the exact set of stop records (by name and SHA-256), the evidence snapshot, the strategy and the expected post-recovery evidence. It is useless for any other stop.
- **Durable, append-only records.** All recovery records are create-only objects in the journal-verification store, mirrored in the archive's `meta/` prefix. Nothing is ever deleted or overwritten, and AuthorityDb is never trusted for recovery state.
- **D+ lift check on every pass.** D+ discovery (SM-5) treats a stop record as resolved only when a valid authorization and an execution record cover it exactly. Deleting a recovery record re-stops the stream: that fails closed. Forging one requires approver private keys.
- **Stop generations.** After a recovery, a new failure gets a new marker and finding identity (generation `n + 1`). A recovery never hides a later failure at the same revision.
- **Five strategies, with blocked cases named.** The strategies (SR-A..SR-E) range from "resume after a verified false alarm" to "continue with a recorded, permanently Reduced discontinuity". New stream epochs, archive-instance loss without a verified copy, organization integrity quarantine and any authorization/content stream stay **permanently blocked** pending the full G14 process (§6.2).
- **DD-10.** An **evidence pre-commitment** (W2-S, §7) writes a create-only commitment to the exact evidence *before* D+ evaluates it. This turns the A-3 residual window into the existing R-DR-1 class plus the existing "remove both records" known limit, without stopping the stream on every unclean termination. It is a proposal; **DD-10 is not closed**.

Recommended next increment (§10 of the completion report): implement the authorization records, the D+ lift check and strategies SR-A and SR-C1 for the audit stream in Development/Testing, plus the W2-S pre-commitment. SR-B, SR-D and SR-E follow separately.

---

## 1. Baseline: existing G14 and contract findings

| ID | Finding | Source | Consequence for R1 |
|---|---|---|---|
| SR-F1 | G14 is a **whole-system** disaster-recovery protocol: operations, epochs, checkpoints, replay of security events, key recovery, SpiceDB rebuild, two-person release. It is a normative design marked P0 OPEN and **not implemented** (no recovery-epoch code, E-8; no ledger, E-14) | `trust_authority/COORDINATED_DISASTER_RECOVERY.md` §5, §9, §11; contract §3 | R1 defines a **scoped** recovery operation, one organization, one stream and one stop set, that obeys G14's principles without implementing G14 |
| SR-F2 | G14's principles bind R1: separate recovery identity; no public restore or "change trusted head" endpoint; no caller-asserted `validated=true`; two distinct human approvers; idempotency and CAS; evidence outside Business PostgreSQL; fail closed on missing evidence; never re-sign or repair | G14 §9, §10.5; reference contracts | Adopted unchanged (§3, §4) |
| SR-F3 | G14 assumes archive keys carry a **stream epoch** ("keyed by (organization, stream epoch, revision)"). The implemented archive has no epoch: keys are `org/{o}/{stream}/{revision:D12}.json` (E-17), the envelope `DynamicForms/SecurityJournal/v1` has none, and R-5/R-6 require genesis at 1 and contiguity | G14 §3 step 3; contract §3 E-17, §4 | **C-SR-1.** A new stream epoch needs a journal/archive format change. R1 **defers** it (SR-PB-3) |
| SR-F4 | G14's reference recovery schema puts recovery operations, checkpoints and approvals in **AuthorityDb** (`recovery` schema). The G14 threat model assumes Authority administrators are trusted; D-R explicitly attacks AuthorityDb (J1, J2) | `trust_authority/recovery-control-plane.reference.sql`; contract §2.2 | **C-SR-2.** For SEC-BL-01 the stricter D-R model governs (contract precedence 1). Recovery state for D+ stops therefore lives in create-only, signed records outside AuthorityDb. AuthorityDb holds at most a hint, never trusted (SM-8 spirit) |
| SR-F5 | The existing **DR-C7-3** dual control (`OperatorApprovalVerifier`) verifies two ES256 compact JWS from different approver ids over an exact statement, with lifetime ≤ 900 s. Public keys come from deployment configuration only (`OperatorApprovals:Approvers[]`). Mode `Unavailable` is the default and the only mode outside Development/Testing; `SyntheticDevelopment` is "never two-human approval evidence" | `OperatorApprovalVerifier.cs`; `PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md` F-3, §5 | Reused as the approval primitive with a new statement kind (SR-OD-03). Production stays `Unavailable` until Entra step-up evidence exists (SR-PB-6) |
| SR-F6 | The Platform Operator **registry** (C-98) is in AuthorityDb, with a platform journal stream. A J1/J2 actor could insert an operator row | C8 M2 design §3, §8 | The registry may identify a **requester**, but **approver keys must not be trusted from AuthorityDb**: they come from deployment configuration (SR-OD-04) |
| SR-F7 | The D-R contract defers recovery everywhere it matters: §5.3 ("Recovery: not provided"), SM-8, OI-DR-1 (recovery demonstrated before P2), OI-DR-9 (a lost checkpoint chain is a G14 recovery event, adopted, not implemented), RB-4/RB-6 (an archive-instance change or a lost chain cannot be re-baselined), §9.4 (`ARCHIVE_CHAIN_BREAK` reserved), E-8 (quarantine permanent) | contract | R1 addresses SM-8 and part of OI-DR-1/OI-DR-9 for the audit stream only. It does **not** touch E-8 quarantine, `ARCHIVE_CHAIN_BREAK` or authorization/content |
| SR-F8 | **Discovery has no "resolved" concept.** SM-5 stops the stream on any valid marker, stream-stopping finding or invalid marker object; SM-9 forbids clearing. A lift therefore cannot be implemented by deleting or flagging records, only by an additional record that discovery honours | §23.1 | Lift = additional signed record (§3.3) |
| SR-F9 | **Marker and finding identities are deterministic by revision** (SM-1; finding identity by organization, stream, code, subtype, revision). After a recovery, a second failure at the same revision would collide with the old marker (`DPLUS_STOP_MARKER_CONFLICT`) instead of being recorded as a new stop | §23.1 SM-1..SM-3 | **C-SR-3.** It needs a stop **generation** in new keys and identities (amendment SR-SM-1) |
| SR-F10 | **DD-11:** an archive-level refusal (`ARCHIVE_REGRESSION/*`) writes no marker and delivery resumes once a canonical sentinel exists. **D+ does not compare the sentinel's instance identifier with anything** (only the verifier does, AS-3). A J4 actor could recreate the sentinel with a different instance and D+ would pass the sentinel check. The chain checks and the surviving finding-store records still apply | §22.3 DD-1, §22.8 DD-11; WP-5a.2 code | **C-SR-4 (tension, not a contradiction of an accepted decision).** Proposal SR-OD-12: D+ pins the instance from the provisioning record or the trusted checkpoint head; an instance change becomes strategy SR-D |
| SR-F11 | **The work loop fetches only `Pending` rows** (E-2). `VerificationFailed` is treated as terminal; M-6 (not implemented) proposes making terminal states final in a trigger | contract §3 E-2, §11 | **C-SR-5.** Delivering a recovered `VerificationFailed` row needs a new transition `VerificationFailed → Pending` by the recovery executor, which conflicts with M-6 v1 as designed (SR-OD-10) |
| SR-F12 | **Accepted exceptions (A-1 AX) are a verifier concept; D+ never consults them** (DP-9: never deliver an entry whose predecessor is not present and linked) | §21.1; DP-9 | **C-SR-6.** A continuation across a gap or a broken link (SR-B) amends DP-9 for a bound scope; that is a new security decision (SR-OD-08) |

**Confirmation.** §23 of the contract is the authoritative text of A-3. `SEC_BL_01_A3_DURABLE_STOP_DRAFT.md` is a non-authoritative historical draft: its status row and the contract's §23 preamble both say so. No accepted decision is reopened here. C-SR-1..C-SR-6 are concrete gaps or tensions, each resolved by a proposal awaiting an owner decision.

---

## 2. Recovery eligibility

### 2.1 Classification of stop and non-stop conditions

"Stop" means a durable A-3 stop (marker and/or finding). Conditions that are not stops never need recovery, and **recovery must not be usable for them**.

| Condition | How it appears today | Class | Eligible strategies | Never |
|---|---|---|---|---|
| **Confirmed journal corruption of the failed row** (row `k` fails R-2..R-5: `CHAIN_INTEGRITY_FAILURE/ENVELOPE_*`, `GENESIS_VIOLATION`) | Marker + finding; row `VerificationFailed` | Integrity, row-local | SR-B (undeliverable-entry variant, `k` is never archived); SR-PB-3 for an epoch | SR-A, SR-C (the row can never be delivered as it is; no repair, BL-2) |
| **Row body digest mismatch** (R-1, `JOURNAL_BODY_DIGEST_MISMATCH`) and `ARCHIVE_BODY_MISMATCH` | Existing **organization quarantine** (DP-10, E-8) | Organization integrity quarantine | **None in R1** (SR-PB-1) | Any R1 strategy |
| **Missing archived predecessor** (`PREDECESSOR_MISSING`, sentinel present) | Marker + finding | Integrity, archive-side | SR-A (false alarm, evidence unchanged and D+ now passes), **SR-C1** (anchored restore of the absent predecessor), SR-B (accepted gap) | Resumption because the predecessor reappeared without a recovery |
| **Invalid predecessor link** (`LINK_MISMATCH`) | Marker + finding | Integrity: either row `k` is forged or altered (J1) or the archived `k − 1` was replaced (J4) | SR-B (continuation with discontinuity); SR-C1 only when the archived `k − 1` is **absent** (overwriting is prohibited) | Overwriting an archived object; SR-A with changed evidence |
| **Archived predecessor present but itself invalid** (`CHAIN_INTEGRITY_FAILURE` on `k − 1`) | Marker + finding | Integrity, archive-side | SR-B (the corrupt object stays, the continuation is recorded) | Overwriting the corrupt object |
| **Missing or altered sentinel / missing container** (`ARCHIVE_REGRESSION/SENTINEL_*`, `CONTAINER_ABSENT`) | Finding, no marker (DD-1, DD-11); resumes once a canonical sentinel exists | Archive-instance condition | Same instance restored: DD-11 unchanged, plus a verifier full sweep. Instance changed: **SR-D** (only with a byte-verified copy) or SR-PB-3 | Accepting a different instance silently (C-SR-4, SR-OD-12) |
| **Archive regression found by the verifier** (`ARCHIVE_REGRESSION`: `archive[v]` absent or ≠ `d_v`, instance different) | Verifier finding (alert only); D+ stops later via `PREDECESSOR_MISSING` if the frontier is affected | Archive-instance condition | SR-D; SR-PB-3 | — |
| **Missing or conflicting stop marker** (`DPLUS_STOP_RECORD_INCOMPLETE`, `DPLUS_STOP_MARKER_CONFLICT`, `DPLUS_STOP_MARKER_INVALID`) | Security finding layered on the original stop; stream stays stopped | Stop-record integrity | Only **together with** the underlying stop: the statement must enumerate every such record, including invalid objects by name and digest (SR-EL-4). Containment must be attested first (SR-EL-6) | Removing or rewriting the invalid object or the conflicting marker |
| **Missing or inconsistent finding** (a marker without its finding: completion; a finding naming a removed marker: `DPLUS_STOP_RECORD_INCOMPLETE`) | Completion (operational) or security finding | Stop-record integrity | As above | — |
| **Temporary archive unavailability** (DP-7) | Entry stays Pending; pass ends; `DELIVERY_LAG` | Operational | **Not a recovery case** (SR-EL-7) | A recovery statement may not be built for a scope without a durable stop |
| **Temporary finding-store unavailability** (SM-5 inconclusive; `DPLUS_FINDING_STORE_UNAVAILABLE`) | Audit stream waits; resumes when the store is readable (no finding exists) | Operational / inconclusive | **Not a recovery case**. If a marker exists, the stop is real and recovery applies to the underlying refusal | — |
| **`DPLUS_DURABLE_STOP_NOT_ESTABLISHED`** (both writes failed; in-memory hold) | Held for the process lifetime | Integrity, not durable (§23.3 residual) | None: nothing durable exists to recover. W2-S (§7) is the remedy | — |
| **Invalid checkpoint chain** (`CHECKPOINT_CHAIN_INVALID`, `CHECKPOINT_STORE_REGRESSION`) | Verifier finding; not a D+ stop | Verification-anchor loss | **SR-E** (new verification chain, OI-DR-9). It makes the anchored strategies (SR-C1, SR-D) **ineligible** while it lasts (SR-PB-8) | Using a checkpoint digest from an invalid chain as an anchor |
| **Accepted historical exception** (A-1 AX record) | Verifier observation, Reduced assurance; D+ unaffected | Accepted, Reduced | Not a stop. When the accepted gap is at the delivery frontier, D+ stops there and SR-B applies | Treating an AX record as a D+ lift (DP-9 unchanged without SR-B) |

### 2.2 Eligibility rules

| # | Rule |
|---|---|
| SR-EL-1 | A recovery may be proposed only for a scope (organization, stream, failed revision) that has at least one **durable** stop record (A-3 marker or stream-stopping D+ finding), or a W2-S evidence-change stop (§7) |
| SR-EL-2 | **Lifting causes.** No retry, restart, finding-store recovery, AuthorityDb change (state, code, receipt, head) or archive evidence change ever lifts a stop. The only lifting cause is an executed, signature-verified recovery authorization that covers the stop record exactly (amendment SR-DL-1) |
| SR-EL-3 | R1 covers the **audit** stream only. Authorization and content streams are not D+-enabled (CF-1, WP-5b) and are out of scope (SR-PB-2) |
| SR-EL-4 | **Exact coverage.** The statement must enumerate the stream's complete set of unresolved stop records (markers, findings, invalid objects under the marker prefix) at proposal time, by object name and SHA-256. A record that appears later, or that is not enumerated, is not covered and keeps the stream stopped |
| SR-EL-5 | **Mandatory evidence.** Every mandatory evidence item (§5) must be Observed or Verified; none may be Unavailable or Contradictory. Otherwise no proposal can be built (SR-PB-7) |
| SR-EL-6 | **Containment.** If any stop record is a security finding about the stop records themselves (`DPLUS_STOP_RECORD_INCOMPLETE`, `DPLUS_STOP_MARKER_CONFLICT`, `DPLUS_STOP_MARKER_INVALID`), the investigation record must attest containment, for example rotation of the archive or store credential, before a proposal is accepted. Development/Testing cannot attest containment meaningfully (shared keys, PV-9): the attestation is recorded, not verified |
| SR-EL-7 | Operational conditions (archive or store outage, inconclusive discovery, delivery lag) are never eligible |
| SR-EL-8 | **Environment.** Recovery executes only where the recovery approval mode is not `Unavailable`: Development/Testing with synthetic keys (not two-human evidence), and production only after production approval evidence (Entra step-up) and WP-8 exist (SR-PB-6) |

---

## 3. Proposed recovery architecture

### 3.1 Components

| Component | Role | Development/Testing | Production (not available) |
|---|---|---|---|
| **Recovery tool** | Builds evidence snapshots, investigation records and statements from **observed** state; verifies and records approvals; executes | Authority host CLI mode (like `--journal-baseline`), run by an operator. **No HTTP endpoint** | Separate recovery workload and identity (G14 §9, PB-11) |
| **Approvers** | Sign the exact statement (approve, or reject) | Synthetic ES256 keys (DR-C7-3 pattern), one person typically holds both: **not** two-human evidence | Two distinct humans, Entra PIM plus fresh FIDO2 step-up evidence bound to the statement digest (C8 M2 `EntraTransactionStepUp` pattern, not implemented) |
| **Approver key set** | Public keys only, in protected deployment configuration | New section `JournalRecovery:Approvers[]` (SR-OD-03); refused outside Development/Testing in synthetic mode | Pinned Entra tenant keys and PIM evidence; never AuthorityDb |
| **Record stores** | Create-only recovery records | Journal-verification store (primary) and archive `meta/dplus-recoveries/` (mirror) | Immutable stores with independent identities and ledger anchoring (WP-8) |
| **D+ lift check** | Inside discovery (SM-5) on every pass | ArchiveProcessor | Same, plus ledger-anchored record verification |
| **Verifier** | Recognizes recovery and intent prefixes (SR-SM-3); validates; never lifts anything | Authority-hosted verifier | Separate workload |

### 3.2 Flow

```mermaid
sequenceDiagram
    autonumber
    participant DP as D+ (work loop)
    participant ST as Stores (verification store + archive meta)
    participant RT as Recovery tool
    participant A1 as Approver 1
    participant A2 as Approver 2
    DP->>ST: refusal → marker + finding (A-3)
    Note over DP: stream stopped on every pass (SM-5)
    RT->>ST: read stop records, archive, sentinel, checkpoint head, row k (read-only)
    RT->>ST: create investigation record + evidence bytes (create-only)
    RT->>ST: create statement (binds stop set, evidence, strategy, expectation)
    A1->>RT: review statement fingerprint → ES256 approval
    A2->>RT: review statement fingerprint → ES256 approval
    RT->>ST: store approvals (create-only, keyed by jti)
    RT->>ST: strategy action (SR-C1 only: create the absent predecessor, bytes bound in the statement)
    RT->>DP: row k VerificationFailed → Pending (AuthorityDb hint only; the stop is still unresolved)
    RT->>ST: create execution record LAST (create-only, one per stop set): the lift takes effect
    DP->>ST: discovery: stop covered by valid authorization + execution → apply strategy rule
    DP->>ST: deliver k if the evidence equals the bound expectation; otherwise a new stop (generation n+1)
    RT->>ST: outcome record (Completed / Failed)
```

### 3.3 What lifts a stop (the trust decision)

On every pass, D+ discovery lists the stream's stop records (A-3), recovery records and (with W2-S) evidence pre-commitments. A stop record is **resolved** only if every one of these holds:
1. A statement exists, in either store, whose stop set contains that record's name and SHA-256 exactly.
2. Two approvals verify under the configured key set: distinct key ids, `decision = approve`, the same statement digest, signature valid, the key active or verify-only with `iat` at or before its retirement.
3. No valid signed rejection or cancellation exists for that statement.
4. An execution record exists for the stop set and names that recovery.

Otherwise the record keeps the stream stopped. Every record is self-authenticating or bound by digest into the signed statement. Deleting one therefore re-stops the stream, and forging one requires approver private keys. **No AuthorityDb value participates.**

When all stop records are resolved and the first pending entry is the statement's revision, D+ applies the **strategy rule** (amendment SR-DL-3). Evidence that differs from the bound expectation is refused as a new stop of generation `n + 1` (`DPLUS_RECOVERY_EVIDENCE_MISMATCH`), and the recovery's outcome is Failed.

---

## 4. Authorization and evidence requirements (summary; normative text in the draft amendment)

| Topic | Proposal |
|---|---|
| Who may request | A registered Platform Operator with a new platform permission (`journal.recovery.request`, SR-OD-05). Development/Testing: an operator label passed to the CLI. The requester builds no part of the statement by hand: the tool derives it from observed evidence, and the requester supplies only the strategy choice, reason and investigation narrative |
| Who may approve | Two holders of **recovery approver keys** (`JournalRecovery:Approvers[]`), a set separate from the DR-C7-3 membership approvers (SR-OD-03) |
| Separation of duties | The two approvers are distinct key ids **and** distinct identities. Neither is the requester, nor the investigator when that person differs from the requester (SR-OD-06). The Authority runtime holds no approver key. Development/Testing synthetic keys give **no** real separation |
| Required evidence and investigation | Investigation record (cause classification, containment attestation, analyst label, narrative digest) plus the evidence snapshot of §5. Both are bound into the statement by digest |
| Scope | Exactly one organization, the audit stream, one failed revision, one stop set, one strategy. A multi-scope statement is refused |
| Expiry | Each approval lives at most 900 s (DR-C7-3). The statement carries an execution window `not_before`/`not_after`, at most 24 h (SR-OD-07). The executor refuses outside the window and after any valid rejection or cancellation. After execution, the authorization is permanent evidence (R-SR-6 for the limits of "after") |
| Replay protection | Single-use `recovery_id` (UUIDv7) and `jti`, each a create-only object key. The statement binds the stop set digest and evidence digest, so it is useless for any other stop or changed evidence. There is one execution record per stop set (create-only), so a second recovery of the same stop conflicts |
| Idempotency | Deterministic keys. Identical bytes on retry are idempotent; different bytes are a conflict and fail closed. The executor resumes from the records it finds |
| Revocation and cancellation | Before execution: one signed cancellation (requester or approver) or one signed rejection makes the statement permanently non-executable. After the recovered revision is archived, cancellation has no effect. **Key compromise:** removing a key from the configuration invalidates every authorization it signed, so those streams re-stop (fail closed). **Key rotation:** a retired key stays verify-only for approvals issued before its retirement time |
| Cryptographic binding | `statement_sha256` over the canonical statement (`DynamicForms/DPlusStreamRecoveryStatement/v1`, RFC 8785-style canonical JSON, unknown members refused) is signed in each JWS together with the decision, the justification digest, `jti`, `iat` and `exp`. The statement binds every evidence digest, the expected post-recovery evidence and the strategy parameters |
| Runtime self-approval | Impossible by construction: the runtime holds public keys only. A compromised runtime (JP) can still skip D+ entirely; that is out of D-R scope (contract §2.2) |

---

## 5. Evidence preservation

| Item | Preserved as | Class if untrusted | Notes |
|---|---|---|---|
| Failed journal row `k` | Full canonical envelope bytes stored create-only under the recovery's evidence prefix **in the archive** (same confidentiality domain as archived audit bodies); its journal digest and the canonical row-field digest are in the statement | Observed (J1/J2 can alter AuthorityDb) | The row stays in AuthorityDb; recovery never deletes or edits it except the delivery-state transition (C-SR-5) |
| Archived predecessor `k − 1` | SHA-256 of its bytes if present (the object stays in place, never overwritten); if absent, **absence evidence**: the complete listing result of every prefix that could hold `k − 1`, its digest, the sentinel digest and the archive instance at listing time | Observed; Verified only when it equals a trusted checkpoint digest | Absence is never cryptographically provable: it is recorded as "absent in a complete listing by tool version X at time T" |
| Durable stop marker(s) | Name and SHA-256 in the stop set; the object stays | Observed | Never deleted, rewritten or superseded (SM-3, SM-9) |
| Security finding(s) | Identity and SHA-256 in the stop set | Observed | Same |
| Invalid objects under the marker prefix | Name and SHA-256 in the stop set | Observed | They stay in place forever; they are covered, not removed |
| Archive instance identity | Sentinel bytes digest and instance id; the provisioning record digest if one exists | Observed | Change of instance ⇒ SR-D |
| Checkpoint-chain position | Trusted head: sequence, verified revision, digest, assurance level, chain verification result (CK-5) | Verified in Development/Testing only within CKR-3 limits | An invalid chain ⇒ SR-PB-8 for the anchored strategies |
| Approvals | The JWS objects as signed (synthetic: no personal data). Production Entra evidence goes to Restricted evidence storage (decision D8-35 pattern, SR-OD-16) | Self-authenticating | |
| Recovery action and result | Execution record; strategy action record (for SR-C1, the created object name and digest); outcome record (Completed with the archive receipt of `k`, or Failed with the refusal) | Observed | Create-only |

**Unavailable or attacker-controlled evidence.** Each item is classified **Verified** (equals an independent anchor), **Observed** (read from a store some attacker class controls; digest recorded), **Unavailable** (read failed) or **Contradictory** (two copies or two reads differ). Unavailable or Contradictory mandatory items block the proposal (SR-EL-5). Observed items are bound by digest, so later substitution is detectable.

**No fabricated clean chain.** Recovery never:
- writes an archive object whose bytes are not bound in the signed statement;
- overwrites or deletes anything;
- re-signs;
- changes a checkpoint;
- upgrades assurance;
- describes a discontinuity as continuity.

SR-B and SR-D results are permanently labelled Reduced (§6).

---

## 6. Recovery strategies

### 6.1 Strategy table

| | SR-A Resume after a verified operational false alarm | SR-B Controlled continuation with recorded discontinuity | SR-C Missing or corrupted archived predecessor | SR-D Archive-instance regression | SR-E Invalid checkpoint chain |
|---|---|---|---|---|---|
| **Typical cause** | A D+ defect later fixed; a Development reset triaged (RST-2); a W2-S evidence-change stop shown benign | A genuine, unrepairable break the owner accepts (lost object; forged or altered row whose later evidence must keep flowing) | C1: `k − 1` **absent** and a genuine copy exists. C2: `k − 1` **present but corrupt** | Container wiped or replaced; sentinel instance changed | Checkpoint store corrupted, regressed or lost (CK-8..CK-10) |
| **Preconditions** | Evidence now equals the snapshot bound in the statement **and** D+ passes on it **unchanged** (no evidence change is allowed under SR-A); investigation classifies the cause as non-adversarial | Evidence equals the snapshot; a re-baseline with an owner disposition (A-1 AX-2) referencing the recovery id is prepared; the scope is bounded to the break at `k` (and, in the undeliverable variant, to `k` itself) | C1: the copy's SHA-256 equals the **trusted checkpoint digest** `d_v` with `v = k − 1` (CK-5 chain valid), or a production ledger anchor. C2: routed to SR-B | A complete copy of the old archive whose objects verify byte-for-byte against the trusted checkpoint chain up to `v`, plus the old instance's sentinel | Investigation; the old chain is preserved; a new baseline is possible |
| **Approvals** | Two (SR-AU) | Two **plus** the owner disposition for the AX record (SR-OD-08) | Two | Two plus owner (SR-OD-13) | Two plus owner (OI-DR-9 adopted disposition) |
| **May the original stream continue?** | Yes | Yes, from row `k` (or `k + 1`) with an accepted break | C1: yes. C2: no, use SR-B | Only if the old instance is restored byte-identically (DD-11 path). A new instance needs succession rules (deferred) | D+ is unaffected (it does not use checkpoints); the verification chain restarts |
| **New stream epoch or identity?** | No | No (an epoch would be better, SR-PB-3) | No | New **archive instance** identity, or SR-PB-3 | New **verification chain** (new sequence, referencing the lost one) |
| **Cryptographic continuity** | Intact | **Broken at `k`**: the chain continues from row `k`'s stored previous digest (pinned in the statement), like AX-7 | C1: intact up to `v`; the restored object is verified against `d_v` | Intact to `v` if verified; above `v` unanchored | Unchanged for the journal; the verification anchor restarts |
| **Resulting assurance** | Unchanged (Full stays Full; Reduced stays Reduced) | **Reduced, permanently** (AX-8); rows ≥ `k` carry no continuity assurance | C1: unchanged | Reduced above `v` | New chain: Full only from its baseline forward; the report references the lost chain; history before it is unattested (Q-7) |
| **Rollback / failure** | No rollback; a mismatch at delivery ⇒ new stop (generation `n + 1`), outcome Failed | Same | C1: if the create-only write conflicts (something already exists at `k − 1`), fail before any lift; the object is not overwritten | Fail closed; blocked | The baseline writes nothing on any finding (RB-4) |
| **R1 status** | **Recommended first** | Second increment, after SR-OD-08 | **C1 recommended first**; C2 = SR-B | **Deferred** except the byte-identical restore | **Deferred** (needs an amendment to RB-6) |

**SR-B undeliverable-entry variant.** If row `k` itself fails R-2..R-5, it is never archived. The statement covers `k` as `UNDELIVERABLE` and authorizes `k + 1` to continue with predecessor `k` absent. Row `k` stays `VerificationFailed` permanently, and the verifier needs AX records for the resulting gap.

### 6.2 Permanently blocked pending an independent recovery process

| ID | Case | Why |
|---|---|---|
| SR-PB-1 | Organization integrity quarantine (`JOURNAL_BODY_DIGEST_MISMATCH`, `ARCHIVE_BODY_MISMATCH`, S1 projection mismatch) | E-8 and C-117: lifting is reserved for the G14 recovery control plane, via the migration identity with two-person approval |
| SR-PB-2 | Any authorization or content stream stop | D+ not enabled there (CF-1); a stop gates protected access (§5.3); P2 needs its own gate (OI-DR-1) |
| SR-PB-3 | Any case requiring a **new stream epoch** (archive lost without a verified copy; an instance change without succession rules) | C-SR-1: no epoch in the envelope or keys; needs a journal and archive format change with ledger anchoring |
| SR-PB-4 | Overwriting or deleting any archived object, marker, finding or recovery record | SM-3, SM-9, BL-2; production immutability |
| SR-PB-5 | Stop-record-integrity findings without a containment attestation (SR-EL-6) | The recovery evidence itself would be in attacker control |
| SR-PB-6 | Any recovery outside Development/Testing until production approval evidence (Entra PIM step-up) and WP-8 independence exist | DR-C7-3 `Unavailable` mode; G14 §9 |
| SR-PB-7 | Unavailable or Contradictory mandatory evidence | SR-EL-5 |
| SR-PB-8 | Anchored strategies (SR-C1, SR-D) while the checkpoint chain is invalid or regressed | No trusted anchor; SR-E first |

---

## 7. DD-10 interaction: W2-S evidence pre-commitment

### 7.1 The remaining sequence (§23.3)

1. D+ detects corruption.
2. Neither the marker nor the finding is confirmed.
3. The Authority terminates.
4. An attacker replaces the evidence.
5. The Authority restarts, re-derives D+ and delivers.

### 7.2 Why the original W2 was costly

W2 (draft §4) wrote an intent before each pass and an outcome after it, and treated any orphan intent as a stop needing recovery. Every crash, kill or deployment during an audit pass would then stop audit delivery until a two-person recovery.

### 7.3 W2-S: commit to the evidence, not to the attempt

Rules (normative text: amendment SR-EP-1..SR-EP-9):
- **Read, then commit, then evaluate.** For every pending audit revision `k` that D+ is about to evaluate, the tool first reads the evidence: row `k`'s journal digest and canonical row-field digest, the predecessor's digest or its absence, and the sentinel instance. It then writes a create-only **evidence pre-commitment** `meta/dplus-intents/{org}/{stream}/{k:D12}.json`, mirrored in the verification store. The pre-commitment holds those digests and the D+ rule-set version, and **no time** in its content, so retries are byte-identical. **Only after it is confirmed durable in at least one store does D+ evaluate.** Detection therefore never precedes a durable commitment.
- **One pre-commitment per revision.** Each pass first reads the existing pre-commitment for `k`, if any:
  - If none exists: write it, then evaluate.
  - If it exists and **equals** the freshly read evidence: evaluate. Evaluation is a pure function of the evidence, so the verdict equals whatever a terminated earlier pass computed. A refusal is recorded now; a pass delivers.
  - If it exists and **differs**: the evidence of an undelivered entry changed. In honest operation that never happens, because rows are append-only and archive objects are create-only. It is a stream-stopping refusal `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT` (security), recorded with A-3 marker and finding, and lifted only by recovery (SR-A after investigation, or SR-B).
- **Outages.** Pre-commitment store unavailable: D+ does not evaluate (the audit stream waits), which is an availability cost. Pre-commitment conflict (different bytes at the key): the same as "differs".
- **Benign crashes need no recovery.** Restarting with unchanged evidence just re-evaluates. This is the cost difference from W2.

### 7.4 What W2-S changes in the residual

| Residual path | Before (A-3) | With W2-S |
|---|---|---|
| Terminate after detection, before any record, then evidence replaced | Delivery resumes (R-W5A2-1) | **Closed**: the pre-commitment predates detection; changed evidence ⇒ durable stop |
| Evidence altered before D+ first reads it | Undetected (R-DR-1, accepted) | Unchanged (R-DR-1) |
| Termination before the pre-commitment is durable | n/a | Nothing was evaluated, so nothing was detected. Equivalent to R-DR-1 |
| Removal of the pre-commitment from **both** stores (J4/J5 and J6) between termination and restart, plus evidence replacement | n/a | Resumes. Same class as the A-3 known limit (both records removed) |
| Compromised runtime (JP) | Out of scope | Out of scope |

### 7.5 Startup and recovery mechanism

The Authority does not need a special start-up mode. Discovery (SM-5, extended) reads pre-commitments on every pass, so a restart is just another pass. The `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT` stop is lifted only through §3.3.

### 7.6 Trusted persistence assumptions

- The pre-commitment survives in at least one of two stores whose administrators are not the evidence-changing actor.
- In Development/Testing all stores are configuration-separated only (PV-9), so W2-S **demonstrates behaviour only**.
- Production protection requires immutable, independently administered stores and ideally ledger anchoring of pre-commitment digests (WP-8, G08).

### 7.7 Costs

- One additional create-only write per store per delivered audit revision; the object count of the audit stream doubles in the archive.
- One read per evaluated revision.
- An availability dependency on the pre-commitment store.
- To be measured (AC-DR-11).

**DD-10 status.** W2-S would reduce DD-10 to residuals that already exist and are accepted (R-DR-1) or known (the A-3 two-store limit). **DD-10 is not closed by this design**; closure needs implementation, tests, the owner's acceptance of those residuals and, for production, WP-8.

---

## 8. Dependency analysis

| Dependency | What R1 needs from it | Without it |
|---|---|---|
| **G08** (privileged database isolation; M-6) | Independent identities for the record stores and the recovery executor, separate from the Authority runtime; M-6 must allow the recovery-only transition `VerificationFailed → Pending` (C-SR-5) or, alternatively, the processor must fetch recovered rows by scope | Development/Testing: configuration separation only. No production claim. M-6 as designed would block SR-A/SR-C1 delivery (SR-OD-10) |
| **WP-8** (production prerequisites) | Immutable archive and verification store (no deletion of recovery records, rejections, cancellations or pre-commitments); Confidential Ledger anchoring of statement, execution and pre-commitment digests (closes R-SR-6); separate recovery workload; Entra step-up approval evidence | R-SR-3 and R-SR-6 remain; recovery stays `Unavailable` in production (SR-PB-6) |
| **DD-10** | W2-S (§7) uses the recovery protocol to resolve `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT` stops | Without recovery, W2-S stops would be permanent; without W2-S, R-W5A2-1 remains |
| **WP-5b** (P2: authorization/content D+) | OI-DR-1 requires demonstrated recovery behaviour. R1 on audit demonstrates the protocol but **not** the gate semantics. An authorization or content stop closes the organization gate (E-6, E-7) and interacts with SpiceDB projection and quarantine. A P2 readiness record must define the additional recovery (G14 epochs, gate reopening) | P2 stays gated (unchanged) |
| **G14 full** | R1 is a scoped subset consistent with G14 §9. Epochs (SR-PB-3), quarantine lifting (SR-PB-1), checkpoint-chain re-establishment (SR-E) and archive succession (SR-D) belong to it | Those cases stay blocked |
| **A-1 (AX records, re-baseline)** | SR-B uses a re-baseline with an owner disposition whose reference is the recovery id | Without it, SR-B would leave permanent verifier findings |
| **OI-DR-10** (checkpoint growth) | None directly; W2-S adds archive growth that should be considered alongside it | — |
| **DR-C7-3 verifier** | Reuse of the ES256 approval verification (new statement kind, new key set) | A new approval primitive would be needed |

---

## 9. Owner decisions required

| ID | Decision | Recommendation |
|---|---|---|
| SR-OD-01 | Adopt the scoped recovery model: signed authorization, create-only records outside AuthorityDb, lift by covering signature only (§3) | Approve |
| SR-OD-02 | Resolve C-SR-2: for SEC-BL-01, recovery state is never trusted from AuthorityDb, overriding the G14 reference schema's placement for this scope | Approve |
| SR-OD-03 | Approval primitive: reuse the DR-C7-3 verifier contract with a new statement kind and a **separate** key set `JournalRecovery:Approvers[]` | Approve the separate key set |
| SR-OD-04 | Approver keys only from protected deployment configuration, never from the Platform Operator registry or AuthorityDb | Approve |
| SR-OD-05 | Requester: a Platform Operator with a new `journal.recovery.request` permission (production); a CLI operator label in Development/Testing | Approve |
| SR-OD-06 | Separation of duties: two distinct approvers, neither the requester nor a distinct investigator | Approve (it is mandatory in production; synthetic in Development/Testing) |
| SR-OD-07 | Execution window ≤ 24 h; approval lifetime ≤ 900 s; one signed cancellation or rejection is final | Approve |
| SR-OD-08 | **SR-B** (continuation with a recorded discontinuity) amends DP-9 for a bound scope and leaves Reduced assurance permanently | Approve in principle; implement after SR-A/SR-C1 |
| SR-OD-09 | Stop generations (C-SR-3): new marker keys `{rev:D12}.r{n:D4}.json` and generation in the finding identity for stops after a recovery; generation-0 keys unchanged | Approve |
| SR-OD-10 | Delivery of a recovered row (C-SR-5): the recovery executor transitions `VerificationFailed → Pending` (one delivery path), with M-6 to permit this for the recovery identity only | Approve, and record the constraint on M-6 |
| SR-OD-11 | **W2-S evidence pre-commitment** (§7) as the DD-10 mechanism; new code `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT` | Approve for design; implement with or after the first R1 increment |
| SR-OD-12 | C-SR-4: D+ pins the archive instance (provisioning record or trusted checkpoint head); an instance change becomes SR-D instead of the DD-11 path | Approve as a separate small increment |
| SR-OD-13 | SR-D (archive-instance succession) and SR-E (new verification chain, amending RB-6): defer to a later increment | Defer |
| SR-OD-14 | Permanent blocks SR-PB-1..SR-PB-8 | Approve |
| SR-OD-15 | W2-S rule-set version change between a pre-commitment and re-evaluation: re-evaluate under the new rules, and record the version in the resulting finding or the delivery | Approve |
| SR-OD-16 | Retention and storage class of recovery evidence (audit envelopes in archive `meta/`; production approval tokens in Restricted evidence storage); link to OI-DR-4 | Decide before production |
| SR-OD-17 | Key-compromise response: removing an approver key re-stops every stream it recovered (fail closed) instead of grandfathering | Approve |
| SR-OD-18 | Authorize the next increment (§10 of the completion report) | Owner choice |

---

## 10. Not claimed

- No recovery, D+ change, verifier change, store layout or key set is implemented, tested or deployed.
- No recovery was executed in any environment. No baseline was run on the persistent Development stack, and no persistent data was read or changed.
- No closure of SEC-BL-01, PD-15, DD-10, CF-1, G14, OI-DR-1 or OI-DR-9. M2F3-43 stays FAILED.
- No independence of Development/Testing stores or approvers; synthetic approvals are not two-human evidence.
- No production assurance; no independent review.
