# E3-M2f-3 M2F3-43 — Delivered-Entry Tamper Detection: Design Investigation

**Status (2026-11-17):**

| Item | State |
|---|---|
| Purpose | Owner-authorized **documentation-only** investigation of how M2F3-43 ("Evidence-removal: delete or alter a delivered evidence row ⇒ chain / archive verification detects it", `E3_M2F3_CONTRACT_CLOSURE.md` §10.5) can be satisfied as written. Follows the M2F3-43 failure recorded in `E3_M2F3_FU_VERIFICATION_RECORD.md` §2.3 |
| Authorization | Owner, 2026-11-17: investigate and recommend. **Not authorized:** implementing a verifier, product code, changing or weakening M2F3-43 or any security contract, G14 recovery, BD-1/BD-2 remediation |
| Nature | Source inspection at `b50264e` plus the executed FU-M2f3-01 characterization tests. Statements about the proposed design are design claims, **not verified behaviour**; nothing here was implemented or measured |
| Review | Owner-led. Not an independent security review |
| Owner decision 2026-11-17 (Option A) | Preserved as a **candidate architecture for future consideration**; not approved and not to be implemented under E3-M2f-3. Tracked with M2F3-43 (FAILED) and R-FU-1 as **SEC-BL-01** (`SECURITY_BACKLOG.md`); OD-DV-1..7 remain open |

---

## 1. Existing mechanisms (source basis, `b50264e`)

| Mechanism | What it does | What it does not do |
|---|---|---|
| **Hash chain** (`JournalChain.Append`) | Each entry's canonical envelope (`envelope_format`, `organization_id`, `stream_name`, `stream_revision`, `event_kind`, `previous_digest_hex`, `created_on_utc`, `body`) is hashed (SHA-256) into `journal_event_digest`; `previous_digest_hex` and the column `journal_previous_digest` link to the previous entry of the same stream; the organization row holds `audit_journal_revision` and `latest_audit_journal_digest` | Nothing **walks** the audit chain. The chain lives entirely in AuthorityDb, so a privileged writer (class A4) can rewrite any entry, recompute every later digest and link, and update the organization head consistently. A local chain alone cannot detect that |
| **Archive delivery** (`ArchiveProcessor`) | For an organization, reads up to 32 **pending** rows (by stream, revision); refuses a pending body whose SHA-256 no longer equals its stored digest (`JOURNAL_BODY_DIGEST_MISMATCH`, quarantine); appends the bytes to the archive; on an archive conflict quarantines (`ARCHIVE_BODY_MISMATCH`); records the receipt in the row (`Anchored`) | Never re-reads a row once `Anchored`; trusts the row's own delivery state; does not check chain links or revision contiguity; runs only when the work loop runs (the host loop waits for SpiceDB readiness, O-3G-1), so delivery latency is unbounded |
| **Archive** (`ISecurityEventArchive`: Blob, in-memory) | Create-only objects keyed `organization/stream/revision`; an append of other bytes at an existing key is a conflict; `ReadAsync` returns the archived bytes. G14 requires it to be a separately administered WORM store | No listing or range operation; no per-organization high-water mark; the in-memory and Azurite implementations are not independently administered. Receipts are stored back in AuthorityDb (forgeable there) |
| **G14 detection** (D-8, M2F3-57) | After an AuthorityDb restore, the next audit append reuses an archived revision; its delivery conflicts and quarantines that organization (`ARCHIVE_BODY_MISMATCH`), others unaffected | Fires only when a post-restore append is delivered. A restored organization that appends nothing is never flagged; the lost delivered tail is only in the archive |
| **Recorder lookup** (LK-3/LK-4, `CapabilityRefusalEvidenceLookup`) | Verifies each of the organization's newest eight evidence rows: own digest, closed body, order, clock; an unverifiable row makes the lookup Inconclusive (nothing appended) | Only the newest eight evidence rows; no chain link; no archive comparison; a re-digested forgery passes |
| **Platform journal rollback check** (`PlatformJournal.RefusalAsync`, C8 M2) | Reads the archive at `head + 1`; if it exists, the platform journal is behind the archive (restore or rollback) and platform operations are refused | Platform journal only; not applied to organization streams. It is the closest existing precedent for an archive-anchored check |
| **Confidential Ledger** | Specified by G14 and PB-12 for compact anchors | **No ledger anchoring of journal heads is implemented** in the Authority |
| **Search-access journal verifier** (`SearchAccessJournalVerifier`) | Verifies authorization-stream events one by one against a checkpoint digest (digest, envelope fields, chain link) | Authorization stream only; the checkpoint lives in AuthorityDb |

## 2. Precise current coverage (seven cases)

"Detected" means a product mechanism produces a quarantine, a refusal or an operational signal; the 3G harness oracle is not product code. Evidence: FU-M2f3-01 tests (`CapabilityRefusalEvidenceRemovalTests`) where noted; otherwise source inspection.

| # | Case | Detected? | By what, and when | Gap |
|---|---|---|---|---|
| 1a | **Alteration before delivery**, digest kept (pending row) | **Yes** | `ArchiveProcessor` digest check at the next delivery pass: `JOURNAL_BODY_DIGEST_MISMATCH`, row `VerificationFailed`, organization quarantined (`ArchiveProcessorTests`). If among the newest eight evidence rows, the recorder lookup is also Inconclusive | Latency = until the next delivery pass (unbounded while the work loop is idle) |
| 1b | **Alteration before delivery, re-digested** (body and digest rewritten consistently) | **No** | The forged bytes are delivered and **become the archived original**; nothing compares the successor's chain link | Covered only by a chain check at or after delivery; within the accepted undelivered-tail risk R-M2f3-6 |
| 2 | **Alteration followed by redelivery** (delivered row altered and made pending again) | **Yes** | Digest kept: `JOURNAL_BODY_DIGEST_MISMATCH`; re-digested: archive conflict, `ARCHIVE_BODY_MISMATCH`; both quarantine the organization (FU tests, detected) | Requires the attacker to make the row pending again |
| 3 | **Alteration of a recent undelivered entry** | Partly | As 1a (digest kept) or 1b (re-digested). Deletion of an undelivered entry leaves a revision gap that nothing checks and is never archived | Undelivered-tail removal or forgery is R-M2f3-6 (accepted residual) |
| 4 | **Deletion of a delivered entry** | **No** | Nothing re-reads delivered rows or checks contiguity; the recorder records the key again in the same bucket at a new revision; the archive then holds two entries for one key and bucket (FU expected-gap test) | Needs archive-anchored verification |
| 5 | **Re-digested alteration of a delivered entry, never resent** | **No** | The lookup accepts the forgery (it checks the row's own digest, not the chain); no quarantine (FU expected-gap test) | Needs archive-anchored verification |
| 6 | **Alteration outside the newest-eight window** (delivered, not resent) | **No** | Never read by any product path (FU expected-gap test) | Needs archive-anchored verification |
| 7 | **AuthorityDb restore with archived evidence retained** | Partly | Detected only when a post-restore audit append is delivered and collides (G14 detection, M2F3-57; 3G G-01..G-03). The archived pre-restore evidence stays intact | No detection while the restored organization appends nothing; restore and tail deletion look the same (case 4 on the suffix); G14 recovery not implemented |

**Conclusion.** M2F3-43 as written ("delete **or** alter a **delivered** row ⇒ detected") is met today only for case 2. Cases 4, 5 and 6 are not detected, and case 7 is detected only incidentally and with unbounded latency.

## 3. What M2F3-43 requires

1. **An independent reference.** The attacker class can rewrite AuthorityDb consistently (rows, digests, links, organization head, delivery state and stored receipts). Detection therefore needs a reference AuthorityDb cannot change: the independently administered archive (G14, PB-10/G08), optionally the Confidential Ledger.
2. **A trigger that reaches delivered rows.** Today nothing re-reads a delivered row. Some scheduled or event-driven process must compare delivered AuthorityDb state with the reference.
3. **Coverage by revision, not by event kind.** A deleted evidence row is only visible as a missing revision of the whole audit stream (evidence rows are interleaved with other audit events), so verification must cover the audit stream's revision sequence, not only `EnvelopeCapabilityRefused` rows.
4. **No trust in AuthorityDb's own delivery metadata.** "Delivered" must be established from the archive (does the key exist), not from `archive_delivery_state` or the stored receipt.

## 4. Candidate designs

| | Design | Detects | Per-sweep cost (organization with N delivered audit revisions, n new) | Weakness |
|---|---|---|---|---|
| **A** | **Full archive reconciliation:** for every revision ≤ archive head, read the archive object and compare with the row | 4, 5, 6, 7 (suffix) | N archive reads + N row reads | Archive reads scale with history (millions per mature organization per sweep); slow and costly at the bound |
| **B** | **Read-path verification:** the recorder (or every audit reader) compares the rows it reads with the archive | 5, 6 only within what is read | Archive reads on the request path | Violates bounded per-request work; never sees deletions of rows it does not read; rejected |
| **C** | **Ledger-anchored head digests only** | Rewrites of anchored heads | 1 ledger read | No ledger implementation exists; a head anchor without walking the chain detects nothing below the head |
| **D (recommended)** | **Archive-anchored chain verification** (below): walk the AuthorityDb audit chain and anchor its ends in archived bytes | 4, 5, 6, 7 (suffix), and interior gaps | Incremental: n row reads + O(log N) archive reads; periodic full: N sequential row reads + 1-2 archive reads | Needs a periodic full walk to catch later alteration of old rows; undelivered tail still uncovered (R-M2f3-6) |
| **D+** | D plus a chain check at delivery (`ArchiveProcessor` verifies a pending entry links to the archived predecessor) | Additionally raises the bar for 1b (the forger must re-chain the whole undelivered suffix) | One digest comparison per delivery | Changes `ArchiveProcessor` (product); does not close R-M2f3-6 |

### 4.1 Why D works

The envelope digest of entry *k* covers `previous_digest_hex`, which is the digest of entry *k − 1*, and so on: **the digest of entry *k* commits to every earlier entry of the stream.** If AuthorityDb's rows 1..k form a valid chain (each stored body hashes to its stored digest, each envelope names the right organization, stream and revision, each link equals the previous digest, revisions are contiguous) **and** the digest of row *k* equals SHA-256 of the archived object at revision *k*, then rows 1..k are exactly the rows that existed when *k* was delivered (SHA-256 collision resistance assumed). One trusted archive read anchors the whole prefix. Any deletion, insertion, renumbering or alteration of a delivered row, however consistently re-digested and re-chained, changes the row-*k* digest or breaks the walk.

### 4.2 Recommended design D (not implemented)

| Element | Specification (proposal) |
|---|---|
| Component | A scheduled **delivered-entry verifier**, one organization at a time, outside every request path; per organization, per stream (audit first; the same code serves authorization and content streams if decided) |
| Trusted reference | The independently administered archive. "Delivered" ⇔ the archive holds the key. AuthorityDb delivery state and receipts are not trusted |
| Archive head | Galloping search with `ReadAsync` from the larger of (a) the last **verification checkpoint** (below) and (b) the AuthorityDb head, to find `h_a`, the highest archived revision: O(log N) reads. No listing API is needed |
| Suffix check | If the archive holds `h_db + 1` (AuthorityDb head + 1): **BEHIND_ARCHIVE** (restore or tail deletion; §5). Same pattern as `PlatformJournal.RefusalAsync` |
| Incremental walk (every `T_inc`) | Rows `k0..h_a`, where `k0` is the last checkpointed revision: chain-valid walk; digest(row `k0`) = SHA-256(archive[`k0`]) and digest(row `h_a`) = SHA-256(archive[`h_a`]); 2 archive reads |
| Full walk (every `T_full`) | Rows `1..h_a`: chain-valid walk anchored at `h_a`; 1 archive read. Catches alteration of old rows after their last check |
| Localization | On a mismatch, binary search with archive reads (O(log N)) to name the first divergent revision for forensics; no repair |
| Verification checkpoint | After a successful walk, a **create-only** checkpoint object `(organization, stream, verified revision, its digest, walk kind, UTC time)` in a separate archive namespace (or a Confidential Ledger entry). Checkpoints only raise the floor of what must exist; a privileged AuthorityDb writer cannot move them. AuthorityDb may cache them as a hint only |
| Findings | A create-only finding object in the archive namespace plus a Critical alert and a readiness signal. Organization identifier, stream, revision and class only; no event body. **Never repairs, re-signs, deletes or rewrites** |
| Response authority | Alert-only by default; quarantine only by explicit owner decision per finding class (§6, OD-DV-3). Never cross-organization |
| Delivery delay | Rows above `h_a` are pending and outside M2F3-43 ("delivered"). The walk still checks their chain consistency (cheap, untrusted) and reports a revision gap; the verifier never treats an undelivered row as delivered |

### 4.3 Guarantees claimed by D (to be verified if implemented)

| Property | Claim |
|---|---|
| Completeness | Every audit revision ≤ `h_a` is covered by each full walk; every revision delivered since the previous checkpoint by each incremental walk |
| Detection | Deletion, insertion, renumbering or alteration (including consistent re-digesting and re-chaining) of any delivered row; an interior revision gap; AuthorityDb behind the archive. Assumes SHA-256 collision resistance and an archive the attacker cannot alter |
| Latency | Rows delivered after the last checkpoint: ≤ `T_inc` + walk duration after delivery. Alteration of a row already checked: ≤ `T_full` + walk duration. Suffix loss: ≤ `T_inc`. Delivery itself has no latency bound today (work loop and SpiceDB readiness), so "delivered" may lag |
| Bounded work | No request-path work. Incremental: O(n) rows + O(log N) archive reads. Full: O(N) sequential rows (the `uq_journal_stream_revision` index gives an ordered range scan) + O(1) archive reads. Batches with a statement timeout; no organization-row lock is needed (read-only, consistent snapshot) |
| Isolation | Per organization; a finding affects only that organization; no state of any other organization is read or written |
| Not covered | The undelivered tail (R-M2f3-6); a compromised archive or archive administrator; a compromised verifier identity; evidence never written (crash windows, D-5) |

### 4.4 Privileges, storage, operations

| Item | Requirement |
|---|---|
| AuthorityDb | Read-only: `security_journal_entries` and the organization head columns. The runtime role already has SELECT; **no new grant**. A separate read-only role is preferable if the verifier runs as its own workload |
| Archive | Read on organization streams; **create-only** write to a separate verification namespace for checkpoints and findings. Preferably a verifier identity distinct from the Authority runtime (the threat is a privileged AuthorityDb writer; a separate identity also keeps a compromised runtime from forging checkpoints) |
| Storage | One checkpoint object per organization and stream per successful walk (about 200 bytes); findings rare. Retention follows the archive's legal-hold and retention policy |
| Operational cost | Not measured. A full walk reads N audit rows per organization (millions for the mature-organization case of E3-M2f-3G); archive reads O(log N) per sweep. Must be benchmarked on disposable PostgreSQL and Azure before periods are fixed |
| Monitoring | Sweep lag, last verified revision per organization, findings, archive unavailability (the walk is inconclusive, never "verified", when the archive cannot be read) |

## 5. Tampering versus legitimate restore divergence

The verifier **cannot** tell a legitimate AuthorityDb restore from tampering without a policy and a trusted record of the restore. Classification proposal:

| Observation | Possible causes | Classification without G14 recovery | With a G14 recovery epoch (future) |
|---|---|---|---|
| Body, digest or chain mismatch at a revision ≤ `h_a`, prefix below it consistent | Tampering; or a post-restore append that reused an archived revision (already quarantined by `ArchiveProcessor` on delivery) | **TAMPER_OR_RESTORE_COLLISION**: security finding | Restore collision if a signed recovery operation covers that organization and revision range; otherwise tampering |
| Interior gap or chain break below the AuthorityDb head | Tampering only (a restore truncates a suffix; it does not punch holes) | **TAMPER**: security finding | Same |
| AuthorityDb head < `h_a` (suffix missing), prefix consistent | Restore to an earlier point; or deletion of the delivered tail | **BEHIND_ARCHIVE**: ambiguous; security finding until explained | Restore if a recovery epoch records it; otherwise tail deletion |
| AuthorityDb ahead of the archive | Normal delivery delay | Not a finding (pending) | Same |

No outcome lifts a quarantine, rewrites a row, re-inserts archived evidence or selects a "trusted" head; those are G14 recovery-plane functions, not the verifier's.

## 6. Dependencies

| Dependency | Relation |
|---|---|
| **G14 recovery** (`trust_authority/COORDINATED_DISASTER_RECOVERY.md` §4, §5) | Needed to classify BEHIND_ARCHIVE and restore collisions as legitimate (recovery epoch) and to resolve any finding. G14 already lists "audit archive continuity" as a recovery check; D would provide that check online. Without G14 recovery, findings can only alert (or quarantine) |
| **Archive independence** (G14, PB-10, G08) | D's guarantee rests on an archive the AuthorityDb administrator cannot alter. The development archives (in-memory, Azurite) are not independent; production needs the separately administered WORM store |
| **BD-1** (restore barrier, `E3_M2E_CONTRACT_CLOSURE.md` §4.2) | No hard dependency. BD-1 must say "which state is anchored outside AuthorityDb"; D's archive-head probe and checkpoints are candidate inputs, to be evaluated by BD-1, not assumed. BD-1 is transactional and per write; D is scheduled, so D cannot serve as the barrier itself |
| **BD-2** (reconstruction insertion) | None for detection. Re-inserting archived evidence after a restore would be a BD-2-class reconstruction and stays out of scope; D never inserts |
| **R-M2f3-6** (undelivered tail) | Unchanged by D; narrowed by D+ |
| **Confidential Ledger** (PB-12) | Optional alternative or second anchor for checkpoints; not implemented |
| **OD-IX-2, persistent use** | D is not a prerequisite for OD-IX-2, but M2F3-43 (and so PD-15) cannot pass without D or an equivalent |

## 7. Recommendation

Adopt **design D** (archive-anchored chain verification of the audit stream, scheduled, read-only on AuthorityDb, checkpoints and findings create-only in an independent archive namespace, alert-only until the owner grants quarantine authority), as a separately authorized implementation increment with its own contract closure, tests and disposable PostgreSQL gate. D satisfies M2F3-43 as written for every delivered row (cases 4, 5, 6 and the suffix part of 7) without request-path work and without weakening the requirement. Consider **D+** separately as hardening of the undelivered tail. Do **not** adopt B (request path) or C alone.

The FU-M2f3-01 expected-gap tests would become detection tests against D; a D gate would add delivered-row deletion, consistent re-chaining, interior gaps, restore suffixes, archive outage (inconclusive), checkpoint replay and organization isolation scenarios, plus mutations removing the end anchor, trusting AuthorityDb delivery state, or skipping the full walk.

## 8. Owner decisions requested

| ID | Decision |
|---|---|
| OD-DV-1 | Adopt design D for M2F3-43 (or choose A, or another design) and authorize a contract closure for it |
| OD-DV-2 | Scope: audit stream only, or also the authorization and content streams |
| OD-DV-3 | Response per finding class (TAMPER, TAMPER_OR_RESTORE_COLLISION, BEHIND_ARCHIVE): alert-only, or organization quarantine (a new quarantine trigger; not to be expanded silently) |
| OD-DV-4 | Restore-divergence policy until G14 recovery exists (§5): treat BEHIND_ARCHIVE as a security finding pending manual triage, or suppress while a documented restore is in progress |
| OD-DV-5 | Deployment and identity: inside the Authority (shared identity) or a separate verifier workload with its own read-only database role and archive identity; checkpoint anchor in the archive, the Confidential Ledger, or both |
| OD-DV-6 | Periods and targets: `T_inc`, `T_full`, maximum walk duration, and the detection-latency objective, after measurement |
| OD-DV-7 | Whether to pursue D+ (chain check at delivery) as separate hardening of R-M2f3-6 |

## 9. Not claimed

No implementation, test or measurement of design D or D+; no change to M2F3-43, any security contract or any product code; no G14 recovery, BD-1 or BD-2 design; no claim that the development archives are independent; no Azure, persistent or production evidence; no independent review.
