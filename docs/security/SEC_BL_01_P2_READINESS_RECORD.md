# SEC-BL-01 — P2 Readiness Record: Authorization/Content D+ Gate (OI-DR-1)

**Status (2026-11-18):**

| Item | State |
|---|---|
| Purpose | The separate P2 readiness record required by OI-DR-1 (contract §19). It defines the "demonstrated recovery behaviour" that the P2 gate (§5.6) requires and assesses G14-R2 (§24) against it |
| Authority | Owner package "WP-5b: P2 Gate and Authorization/Content D+ Enforcement", authorized 2026-11-18 "as written, subject to the mandatory P2 owner gate" (decision log **C-156**) |
| Update (2026-11-18, C-157) | The owner approved P2-D1..P2-D5 (C-157; contract amendment **A-6**, §26). RD-1..RD-10 are the formal OI-DR-1 definition. Implementation and results: `SEC_BL_01_WP5B_IMPLEMENTATION_RECORD.md`. The outcome row below is the assessment as it stood before C-157 |
| Outcome (before C-157) | **P2 is NOT READY. WP-5b implementation was not started.** The approved contract forbids authorization/content recovery (§24.3 ST-4, §24.8), and the owner has just retained that restrictive default (SR-OD-14, C-156). P2 therefore needs a contract amendment and owner acceptance of the stop consequences (§5). These are the outstanding decisions P2-D1..P2-D5 (§6) |
| Code, tests, configuration | **None changed** by this record |
| SEC-BL-01 / PD-15 / M2F3-43 / DD-10 / CF-1 | OPEN / OPEN / FAILED / resolved for Development/Testing single-store only, open for production (C-156) / OPEN |
| Review | Owner-led. Prepared by the implementing assistant. **Not an independent security review** |

**Evidence tags** as in the contract: **[S]** source inspection, **[T]** executed test evidence, **[I]** inferred, **[NV]** not verified. Source inspection is not test evidence. Line numbers refer to the uncommitted working tree on `main` at `a9b32de`.

---

## 1. Gate requirements (from the approved contract)

| Source | Requirement |
|---|---|
| §5.6 P2 | A separate owner gate, only after recovery behaviour for a **stopped stream** is demonstrated (G14 or an approved recovery procedure) **and** the stop consequences (§5.3) are accepted |
| §5.3 | For authorization/content, a D+ stop keeps the existing gates closed (E-6, E-7): "a de facto permanent block of protected access for that organization until recovery" |
| §5.7 / OD-DV-7 | Once enabled, D+ applies in full and permanently; no bypass or configuration switch |
| §14 item 5 | G14 recovery and recovery epoch before P2 (production prerequisite) |
| §19 OI-DR-1 | Define the demonstration in a separate P2 readiness record; do not start P2 work before |
| §24.3 ST-4 | A recovery statement is refused when "the stream is not audit" |
| §24.8 | "No §24 path … acts on an authorization or content stream" (permanent block, SR-OD-14) |
| C-156 | §24.10 defaults, SR-OD-14 included, retained **without loosening** |

---

## 2. Proposed definition of OI-DR-1 (for owner approval, P2-D1)

Recovery behaviour is "demonstrated" for a stream when **all** of RD-1..RD-10 hold, by executed tests in Development/Testing (in-process plus disposable PostgreSQL 18 and Azurite). Source inspection does not count.

| # | Demonstration |
|---|---|
| RD-1 | **Stop classification.** Every D+ refusal code on the stream is classified as either recoverable (SR-A or SR-C1) or **non-recoverable in this increment**, and each non-recoverable class has an owner-accepted consequence (P2-D3) |
| RD-2 | **End-to-end recovery.** SR-A and SR-C1 run through propose, decide (two distinct approvers), execute and outcome on that stream. They are restart-safe at every executor step, replay-safe, generation-specific, and re-verified on every pass (EX-1..EX-8 per stream) |
| RD-3 | **No state change beyond the approved operation.** The only AuthorityDb write is the EX-2 row hint. No authorization relationship, revision, projection, content head, manifest or quarantine value changes. Proved by statement trace and mutation |
| RD-4 | **Revocation preservation (authorization).** A revocation committed while the stream is stopped is effective at the first instant protected Allow becomes possible again. No interval exists in which a revoked grant is allowed, and no recovery reinstates a revoked relationship |
| RD-5 | **Content integrity (content).** After recovery, no content is readable unless its checkpoint journal entry is `Anchored` with the expected digest. An SR-C1 copy never substitutes for, or rolls back, a newer accepted content revision. Content heads are unchanged by recovery |
| RD-6 | **Isolation.** Stops, commitments, recoveries and lifts are scoped to (organization, stream). They affect no other stream or organization, and a stopped stream starves no other stream's delivery |
| RD-7 | **Outage versus corruption.** An unavailable archive, verification store or recovery-record store makes the stream wait (gate closed). It never delivers, lifts or records a finding as corruption. Confirmed integrity failures stop the stream durably |
| RD-8 | **Quarantine.** No recovery path lifts or changes an organization quarantine (E-8) |
| RD-9 | **Mutations.** Stream-specific variants of SR-MV-01..15 are DETECTED |
| RD-10 | **Regression.** The G14-R2 audit-stream results are unchanged. SEC-P01..SEC-P12 pass. The Authority, architecture and contract suites pass |

RD-1..RD-10 are a Development/Testing demonstration only. They are not two-human evidence (AP-1 `SyntheticDevelopment`) and not production recovery (§14).

---

## 3. Assessment of G14-R2 against RD-1..RD-10

| # | Finding | Tag |
|---|---|---|
| A-1 | **Recovery is prohibited on authorization/content.** ST-4 refuses any non-audit statement, and §24.8 forbids any §24 path from acting on those streams. RD-2 therefore cannot be demonstrated without an amendment | [S] contract §24.3, §24.8 |
| A-2 | **Authorization stop ⇒ whole-organization denial.** The gate counts authorization rows not `Anchored` in mode `RequiredBeforeEffective` (the default) and returns `AwaitingArchive`. About 30 call sites evaluate the gate, most through `RequireOpen`: permission checks, content reads/writes, search, index building and rosters. A stop on revision `k` holds every later revision `Pending`, so the gate stays closed until recovery | [S] `OrganizationGate.cs` L22–L26; `OrganizationAuthorizationState.cs` L59; `AuthorityStore.cs` L342–L343 |
| A-3 | **Revocation preservation is structurally plausible but untested.** Security commands are accepted while the gate is closed (only quarantine is checked) and journal `Pending` entries. Recovery writes only the row hint. The gate reopens only when every authorization entry, revocations included, is `Anchored`. So a revocation committed during a stop becomes effective at the reopening, and recovery cannot restore a revoked grant | [S] `SecurityCommandService.cs` L67–L69; contract EX-2; **[NV]** no test (RD-4) |
| A-4 | **Content stop ⇒ progressive unreadability, writes still accepted.** Content reads require the head's checkpoint entry to be `Anchored` (`AWAITING_ARCHIVE` otherwise). Content writes depend only on the authorization-stream gate. While the content stream is stopped, writes are committed, and every record written after the stop becomes unreadable. Records whose checkpoint is already anchored stay readable. No unverified content is released | [S] `ContentService.cs` L1041–L1044, L1139–L1142; L100, L345; [I] write continuation |
| A-5 | **Non-recoverable classes on these streams.** With SR-B, SR-D and SR-E not implemented: link or chain failures from an adversarially forged row (SR-A requires a non-adversarial cause and passing D+); archive-instance change and contradictory anchors (GP-2); and evidence-changed-after-commitment with an adversarial cause. On the authorization stream each is a **permanent loss of all protected access for that organization** in this increment. A J1 actor can already cause permanent lockout by setting the quarantine (E-8), so this is not a new capability class, but it adds a new trigger | [S] §24.4 SG-1..SG-3, §24.6 GP-2; [I] J1 comparison |
| A-6 | **New availability dependencies.** Under W2-S (EP-2) and pinning (GP-2), an outage of the journal-verification store or of the pin anchors would hold the authorization stream, which closes the organization gate (today, only an archive outage does this). With OD-R2-1 (fail closed when not composed), any host without the verification store would never deliver authorization entries, closing **every** organization's gate | [S] §24.7 EP-2, §24.10; [I] consequence |
| A-7 | **Persistent Development stack.** Its archive is non-empty, has no sentinel and is unpinned (WP-5a.1/G14-R2 records). With authorization D+, a rebuilt Authority there would deny all protected access until an operator baseline/sentinel/checkpoint is run. That conflicts with the restriction against persistent baselines unless the stack is not rebuilt with WP-5b code | [S] WP-5a.1, G14-R2 records; [I] |
| A-8 | **Delivery-loop starvation (implementation item, not a decision).** Authorization and content share one batch ordered by stream name, and refusals `break` the pass. A stopped authorization stream would starve content delivery, as audit once did before WP-5a. WP-5b must use per-stream batches (RD-6) | [S] `ArchiveProcessor.cs` L103–L139 |
| A-9 | **Reusable without change:** signature verification (AP-1..AP-6, which already bind `stream_name`), create-only records in both stores (RR-1..RR-4), stop generations (GP-1), the W2-S commitment format (EP-1, which binds the stream), discovery and per-(organization, stream) isolation (EX-8). Stream-specific content is limited to the evidence snapshot and the RD-4/RD-5 invariants | [S] §24.1–§24.7 |
| A-10 | **Recovery approval scope.** Approvals bind organization, stream, failed revision and generation (AP-2), so an audit approval cannot lift an authorization stop. In Development/Testing the approvers are synthetic keys, not two humans (AP-1) | [S] |
| A-11 | **Confidentiality of recovery evidence.** `evidence/row.json` copies the failed row body into archive `meta/`. Authorization bodies hold principal and relationship identifiers, and content bodies hold manifest commitments. The classification is the same as the archive entry itself, so there is no new exposure class | [S] RR-3; [I] |

**Conclusion:** G14-R2 is a sound base for P2 (A-9, A-10, A-3). But RD-2 is blocked by the approved contract (A-1), and the §5.3 consequences are larger than "until recovery" for the classes in A-5 and A-6. Under §5.6 and the package's own stop rule, **P2 cannot pass without owner decisions**.

---

## 4. Items 4 and 5 of the package (not started)

| Item | Status |
|---|---|
| 4. Independent commitment verification (EP-5) | Listed as an open decision in §24.11. Not gated by P2 (the verifier is alert-only and the commitments already exist on the audit stream). It was **not started**, because the owner instructed a stop at the first mandatory P2 decision. See P2-D5 |
| 5. Performance (W2-S and D+ incremental cost) | Needs D+ on authorization/content for the "after" measurement. A baseline for the audit stream alone could be measured now. Not started; see P2-D5 |

---

## 5. Stop consequences requiring acceptance (input to P2-D3)

| # | Consequence (Development/Testing) |
|---|---|
| SC-1 | An authorization-stream stop denies all protected access for that organization (permission checks, content, search, rosters) until an executed two-approver recovery |
| SC-2 | The stop classes of A-5 have **no** recovery path in this increment. On the authorization stream this means a permanent loss of protected access for that organization, until SR-B/SR-D/SR-E or broader G14 is separately approved |
| SC-3 | A content-stream stop makes every record written after the stop unreadable, while writes continue to be accepted. Earlier records stay readable |
| SC-4 | An outage of the journal-verification store or of the pin anchors closes the gate of affected organizations for its duration (A-6) |
| SC-5 | With OD-R2-1, a host without the verification store denies protected access to every organization |
| SC-6 | The persistent Development stack must not run WP-5b code until an operator baseline is separately authorized (A-7) |
| SC-7 | Recovery approval in Development/Testing is synthetic, not two-human evidence (AP-1) |

---

## 6. Outstanding owner decisions

| ID | Decision needed | Recommended disposition |
|---|---|---|
| **P2-D1** | Adopt RD-1..RD-10 (§2) as the definition of OI-DR-1 | Adopt |
| **P2-D2** | **Contract amendment A-6** (A-5 records C-156). It would lift the ST-4 "not audit" refusal and the §24.8 authorization/content block for **SR-A and SR-C1 only**, in Development/Testing only. This changes the SR-OD-14 default that C-156 retained. All other §24.8 blocks stay (no quarantine lift, no instance-change recovery, no SR-B/D/E, no overwrite or delete, no persistent environment) | Approve, limited as stated |
| **P2-D3** | Accept stop consequences SC-1..SC-7 (§5) for authorization/content D+. SC-2 and SC-5 are the material ones | Accept SC-1, SC-3, SC-4, SC-6, SC-7. For SC-2 and SC-5, decide explicitly (accept, or defer P2 until SR-B/SR-D are designed) |
| **P2-D4** | Sequencing: implement P2 recovery and D+ for authorization and content together, or the content stream first. Content's consequence (SC-3) is narrower than authorization's (SC-1, SC-2) | Content first, then authorization after RD-1..RD-10 pass for content |
| **P2-D5** | Whether items 4 (EP-5 verifier comparison) and 5 (audit-only performance baseline) proceed now, independently of P2 | Proceed now. Both are alert-only or measurement work, with no product behaviour change |

---

## 7. Not claimed

- No WP-5b code, test, configuration or infrastructure change, and no test execution.
- No closure of SEC-BL-01, PD-15, CF-1, G14, OI-DR-1 or OI-DR-9. DD-10 is resolved for Development/Testing only, with production open (C-156). M2F3-43 stays FAILED.
- No production assurance, no two-human assurance, no independence of stores, no independent review.

---

## 8. Disposition (2026-11-18, C-157)

| Decision | Disposition |
|---|---|
| P2-D1 | Approved: RD-1..RD-10 is the OI-DR-1 definition; executed tests only |
| P2-D2 | Approved: amendment A-6 (§26) extends SR-A and SR-C1 to authorization and content in Development/Testing only; all other §24.8 blocks unchanged; SR-B, SR-D and SR-E deferred |
| P2-D3 | Approved with conditions (SC-2: no containment bypass; unresolved stops are broader-G14 production blockers; permanent lockout is never an acceptable final production capability. SC-5: fail closed, detect at startup or readiness, no fallback, distinguish configuration, outage and integrity. SC-6 authorizes no change to the persistent Development stack) |
| P2-D4 | Approved: content first, authorization only after the content demonstrations pass |
| P2-D5 | Approved: EP-5 verifier comparison; audit-only performance baseline measured before enabling further streams |

The RD-1..RD-10 results per stream are in `SEC_BL_01_WP5B_IMPLEMENTATION_RECORD.md` §5.
