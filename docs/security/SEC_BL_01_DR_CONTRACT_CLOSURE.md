# SEC-BL-01 — Design D-R Contract Closure: Archive-Anchored Verification of Delivered Journal Entries

**Status (2026-11-17):**

| Item | State |
|---|---|
| This document | The **authoritative contract** for SEC-BL-01 design D-R (WP-0). **Documentation only.** It changes no code, test, migration, permission, configuration, infrastructure or database. Not committed (commit not authorized) |
| Owner decisions | D-R architectural dispositions **approved by the owner on 2026-11-17** and recorded as **C-155** (`docs/decisions/PHASE_C_DECISION_LOG.md`). These are approved **directions and contract requirements**, not authorization to implement any code or infrastructure |
| Implementation | **None.** WP-1..WP-8 (§17) each need separate owner authorization |
| SEC-BL-01 | **OPEN** (`SECURITY_BACKLOG.md`) |
| M2F3-43 | **FAILED** under the original PD-15 contract; requirement unchanged (`E3_M2F3_CONTRACT_CLOSURE.md` §10.5) |
| PD-15 | **OPEN.** Closing SEC-BL-01 is necessary but not sufficient (§15) |
| Assurance | No production assurance is established or claimed. Development/Testing behaviour only, once implemented and verified (§13.6) |
| Review | Owner-led. Prepared by the implementing assistant. **Not an independent security review** and not a certification |
| Amendments | **A-1 (dated 2026-11-17, WP-4A)**, §21: accepted-exception records, re-baseline continuity, bounded incremental verification, OBS-1 classification. **A-2 (dated 2026-11-17, WP-5a)**, §22: audit-stream D+ (phase P1), sentinel provisioning, adopted OI-DR dispositions; **corrected by §22.8 (dated 2026-11-17, WP-5a.1)**: explicit provisioning, durable findings. **A-3 (dated 2026-11-18, WP-5a.2)**, §23: durable audit-stream stop marker (M2, disposition W1; DD-10 narrowed, not closed). **A-4 (dated 2026-11-18, G14-R2)**, §24: controlled audit-stream recovery (SR-A, SR-C1; two-approver signed records outside AuthorityDb), stop generations, archive-instance pinning, W2-S evidence pre-commitment; the provisional defaults of §24.10 are not authoritative. **A-5 (dated 2026-11-18, C-156)**, §25: owner dispositions only (G14-R2 accepted for Development/Testing; OD-R2-1 confirmed; DD-10 resolved for the Development/Testing single-store threat model, open for production; §24.10 defaults retained without loosening); no rule changed; P2 not passed at that date. **A-6 (dated 2026-11-18, C-157)**, §26: P2 — D+ on all three streams with per-stream batches, SR-A/SR-C1 recovery extended to authorization and content (Development/Testing), SC-5 readiness, verifier commitment comparison (EP-5); E-6/E-7 unchanged. **A-7 (dated 2026-11-18, C-158)**, §27: G08 journal immutability (immutable fields, delivery-state transitions, no delete/truncate, column-level runtime UPDATE, read-only verifier role) and trusted delivery receipts outside AuthorityDb for the organization gate (E-6) and content release (E-7); J2 unconstrained. **A-8 (dated 2026-11-18, C-159)**, §28: dispositions only (G08 accepted; E-6/E-7 closed for Development/Testing against J1; limitations, CF-1 and DD-10 retained; WP-6/WP-7 authorized; M2F3-43 stays FAILED). **A-9 (dated 2026-11-19, C-160)**, §29: trusted-receipt validity model TR-6′ (RV-1..RV-9, F-1), historical archive verification by the full sweep VF-15 (OI-DR-7), J2 revocation-deletion assessment; M2F3-43 stays FAILED. **A-10 (dated 2026-11-19, C-161)**, §30: dispositions only (WP-7A accepted; TR-6′ with Φ = 1 s, VF-15, L-12 accepted; F-7A-4, F-7A-5 and AC-DR-11 deferred to the pre-production backlog; J2 P-1 accepted as a Development/Testing residual; AC-DR-13 conditional; M2F3-43 stays FAILED; Development/Testing workstream concluded, SEC-BL-01 and PD-15 OPEN). §1–§20 are preserved unchanged; where they conflict, the amendments govern |

**Inputs:** the Design D Security Review and the D-R Contract Readiness Report (owner-commissioned, 2026-11-17, delivered in session; not stored in the repository, like earlier E3 readiness reviews); `E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md` (design D, candidate); `E3_M2F3_FU_VERIFICATION_RECORD.md` §2.3 (M2F3-43 executed, failed as written); `E3_M2F3_CONTRACT_CLOSURE.md` (PD-15, §10, §11.3, §14.1); `SECURITY_BACKLOG.md` (SEC-BL-01); `trust_authority/COORDINATED_DISASTER_RECOVERY.md` (G14); `F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md`; source inspection at `a9b32dee5853b8178f7d1146a0817829e8404d65` (§3).

**Precedence:**
1. This document governs SEC-BL-01 design D-R. Where it differs from `E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md` §4–§8, this document governs; design D as written there is superseded by D-R.
2. It **does not amend** M2F3-43, PD-15, any E3-M2f-3 requirement, or any other existing security or contract gate. Nothing here relaxes an existing check or quarantine trigger.
3. Where this document is silent, `E3_M2F3_CONTRACT_CLOSURE.md`, then the earlier E3 contracts, govern as before.

**Evidence tags:** **[S]** confirmed by source inspection; **[T]** confirmed by executed test evidence in the repository; **[I]** inferred or theoretical; **[Inc]** inconclusive; **[NV]** not verified. Source inspection is not test evidence.

**Identifiers introduced:** attacker classes J1–J6 and JP (runtime process); chain rules R-1..R-7; delivery rules DP-1..DP-14; archive rules AS-1..AS-4 (sentinel) and AE-1..AE-8 (enumeration); Development reset rules RST-1..RST-3 (distinct from the G14 tests DR-01..DR-36); checkpoint rules CK-1..CK-12 and rollback classes CKR-1..CKR-3; verifier rules VF-1..VF-14; finding codes (§9); baseline outcomes and rules BL-1..BL-10; scenarios ADV-01..ADV-20; mutations MV-01..MV-18; inverted gap tests EG-1..EG-3; acceptance criteria AC-DR-01..AC-DR-14; risks R-DR-1..R-DR-11; open items OI-DR-1..OI-DR-8; conflict CF-1. Chain rules R-1..R-7 are unrelated to the risk identifiers (R-FU-1, R-M2f3-n, R-DR-n). The review's documentation findings D-1..D-5 (§16) are unrelated to the durability statements D-1..D-9 of `E3_M2F3_CONTRACT_CLOSURE.md` §4.

---

## 1. Owner decision register (C-155, 2026-11-17)

Every entry is an **approved direction** recorded for this contract. None authorizes implementation.

| ID | Approved direction | Contract effect | Where |
|---|---|---|---|
| **OD-DV-1** | Adopt D-R for SEC-BL-01 | Design D as written is superseded by D-R | all |
| **OD-DV-2** | Scheduled verification covers the **audit, authorization and content** streams. D+ is introduced in phases, beginning with **audit**. D+ on authorization/content requires a **separate owner gate** after recovery behaviour is demonstrated | Interim detection-only limitation and conflict CF-1 documented | §5.6, §5.7 |
| **OD-DV-3** | Verifier is **alert-only**. D+ **stops delivery** for the affected stream on a confirmed integrity failure. **No new automatic organization quarantine trigger** | Existing quarantine triggers unchanged | §5, §9 |
| **OD-DV-4** | Restore divergence is a **security finding** requiring manual investigation; never suppressed because a restore is claimed to be legitimate | No suppression mechanism exists or may be added in AuthorityDb | §9.1 |
| **OD-DV-5** | Dedicated verifier identity and separately controlled checkpoint storage. Development/Testing may use an Authority-hosted verifier under documented limitations; production requires a separate workload and independently controlled identities | | §8.5, §14 |
| **OD-DV-6** | Development/Testing measurement defaults: incremental **5 min**, full **24 h**, delivery-lag threshold **15 min**, verification staleness **15 min**. Production targets remain subject to measurement and approval | Values are measurement defaults, not commitments | §8.6 |
| **OD-DV-7** | D+ is **mandatory for every stream once that stream is authorized** for D+ enforcement | No partial or relaxed D+ on an enabled stream | §5.7 |
| **Q-1** | Adopt M-1..M-5 | §5–§10 | |
| **Q-2** | Adopt attacker classes J1–J6. M-6 remains a **separate G08 increment** | §2, §11 | |
| **Q-3** | No new automatic quarantine. `ARCHIVE_CHAIN_BREAK` is **reserved, not activated** | §9.4 | |
| **Q-4** | Separately controlled Development/Testing checkpoint store; production checkpoint anchoring through **Azure Confidential Ledger** | §7 | |
| **Q-5** | All three streams verified; D+ phased | §5.6 | |
| **Q-6** | Dated documentation corrections D-1..D-5, preserving historical records | §16 | |
| **Q-7** | Assurance begins at the established baseline, with recorded historical limitations | §10, §2.4 | |
| **Q-8** | Development/Testing may use an Authority-hosted verifier with documented identity limitations; production requires separate hosting | §8.5 | |
| **N-1** | Persistent Development archive plus a documented coordinated reset policy | §6.3 | |
| **N-2** | Archive prefix enumeration in the archive contract | §6.2 | |
| **N-3** | Archive identity sentinel | §6.1 | |
| **N-4** | SEC-P01..SEC-P12 regression verification when affected behaviour is implemented (not Stage B) | §13.4 | |
| **N-5** | Decision-log entry with the next available verified identifier | Recorded as **C-155** (no prior use of C-155 anywhere in the repository at `a9b32de`) | |

### 1.1 Owner contract corrections incorporated (2026-11-17)

| Correction | Where incorporated |
|---|---|
| A checkpoint hash chain does not prove that its latest checkpoint still exists; distinguish corruption, anchored rollback detection, undetectable rollback | §7.3 (CKR-1..CKR-3), §2.3 |
| Archive completeness requires enumeration (unexpected objects, gaps, duplicates, objects beyond the head); probing alone is insufficient | §6.2 (AE-1..AE-8) |
| An accepted legacy exception is not a clean result; outcomes CLEAN / EXCEPTION_ACCEPTED / FAILED / INCONCLUSIVE | §10 |
| D+ on authorization/content gated on demonstrated recovery and separate approval; phasing never weakens checks on an enabled stream | §5.6, §5.7 |
| Threat-model limitations (post-archive detection versus original authenticity; credential versus process compromise; archive/verifier compromise; collusion) | §2 |

---

## 2. Threat model and assurance boundaries

### 2.1 What D-R protects, and what it does not

| Property | D-R position |
|---|---|
| **Detection of changes made after an event was archived** | The **only** property D-R provides: any later deletion, insertion, reordering, renumbering or alteration of a delivered row in AuthorityDb, and any gap, unexpected object or regression in the archive, is detected within the sweep latency (§8.6), **provided** the archive and the trusted checkpoint head are outside the attacker's control |
| **Authenticity of an event when originally recorded** | **Not provided.** An event forged or altered **before** delivery (while undelivered) and then delivered becomes the archived original and is indistinguishable from a genuine one (R-DR-1, existing R-M2f3-6) |
| **Unkeyed SHA-256** | The journal digest is unkeyed (E-1). **It does not prevent any attacker who can write AuthorityDb from recomputing digests, links and heads consistently.** Every D-R guarantee comes solely from comparing AuthorityDb with objects the attacker class cannot change (the archive and the independently anchored checkpoint head) |
| **Prevention** | D-R detects; it does not prevent tampering. The recorder may act on a forged row until detection (R-DR-2) |
| **Protection against an attacker controlling both the evidence and its trusted anchor** | **None. Not claimed.** |

### 2.2 Attacker classes (replaces the ambiguous "A4" for SEC-BL-01; correction D-1)

| Class | Capabilities | D-R guarantee (Development/Testing, once implemented and verified) | D-R guarantee (production, only with §14 prerequisites) | Not covered |
|---|---|---|---|---|
| **J1** Compromised Authority runtime **database credential** (credential only; the Authority process is honest) | SELECT, INSERT, UPDATE on `security_journal_entries` and the organization head (E-4); no DELETE | Detection of: altered delivered rows; forged rows below a delivered anchor; fake delivery state; archive extension with forged links on D+-enabled streams (refusal); on streams without D+, detection after the fact (CF-1) | Same, against an independent archive and anchored checkpoints | Undelivered-tail forgery (R-DR-1); suppression window (R-DR-2) |
| **J2** Privileged AuthorityDb administrator | J1 plus DELETE, TRUNCATE, trigger and grant changes | Detection of deletion (gaps, `BEHIND_ARCHIVE`) and consistent re-chaining (`ANCHOR_MISMATCH`) | Same | As J1. M-6 (§11) does not constrain J2 |
| **J3** Database restore / rollback actor | Restores AuthorityDb to an earlier point | `BEHIND_ARCHIVE` (security finding, OD-DV-4); existing collision quarantine `ARCHIVE_BODY_MISMATCH` (M2F3-57) | Classification of a legitimate restore needs the G14 recovery epoch (not implemented) | Distinguishing restore from tail deletion before G14 |
| **J4** Archive **credential** compromise | Development: create, overwrite, delete (shared account key, E-10/E-11). Production: whatever the stolen data-plane identity allows | Development: archive overwrite/deletion detectable only relative to a checkpoint held in the separately keyed store and only while that store's head is intact (§7.3: not a freshness proof) | With locked immutability: no overwrite or deletion; creation at unused keys remains possible (pre-emption, R-DR-6) | **J1 + J4** defeats D-R |
| **J5** Archive **administrator** | Remove immutability (if not locked), delete or replace containers | Not demonstrable (one Docker administrator controls everything in Development/Testing) | Locked immutability plus Confidential Ledger anchoring make deletion impossible before retention or detectable afterwards | **J2 + J5** defeats D-R |
| **J6** Verifier identity or checkpoint-store compromise | Forge, suppress or roll back checkpoints and findings | **None** | Ledger-anchored checkpoint head detects a rolled-back store (CKR-2); **a forged-forward checkpoint written by J6 is not detected** | J6 must be separated from J1, J2, J4 and J5 |
| **JP** Compromise of the Authority runtime **process** (code execution in the Authority) | Equivalent to J1 + J4, plus the ability to forge D+ decisions | **None** | **None** | Out of D-R scope |

**Collusion or combined control:** any combination that controls both a piece of evidence and its trusted reference (J1+J4, J2+J5, J6 with any of J1/J2/J4/J5, JP) defeats D-R. D-R claims nothing against such combinations.

### 2.3 Relationship to existing records

- R-FU-1 is a violation of **assumption A4** of `E3_M2F3_CONTRACT_CLOSURE.md` §5.4 ("no privileged actor inserts, deletes, reorders or edits journal rows"). It is **not** the attacker class "A4" of `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` §7 (compromised provider credential). For SEC-BL-01 the attacker is J1 or J2 (alteration/insertion need only J1; deletion needs J2; D-4).
- The seven-event bound of `E3_M2F3_CONTRACT_CLOSURE.md` §5.4 remains conditional on assumption A4; D-R does not restore the bound against J1/J2, it detects their changes.

### 2.4 Historical limitation (Q-7)

Assurance begins at the established baseline (§10). Nothing proves that history before the baseline was genuine: rows forged before delivery, undelivered rows deleted before the baseline, evidence never written (D-5 crash windows), restores performed before G14 exists, and everything ever archived in the Development/Testing archives (publicly known Azurite development key, E-11) carry **no independent assurance**.

---

## 3. Source baseline (inspection at `a9b32dee`)

| # | Fact | Tag | Location |
|---|---|---|---|
| E-1 | Journal digest = unkeyed SHA-256 over the canonical envelope (`envelope_format`, organization, stream, revision, kind, previous digest hex, instance-clock time, body); previous digest null at revision 1 | [S] | `JournalChain.cs` |
| E-2 | `ArchiveProcessor` reads `Pending` rows only (32 per pass, by stream then revision), checks the body against the row's **own** digest only; no link, predecessor, envelope-versus-column or contiguity check; archive unavailable ⇒ break, row stays `Pending` | [S] | `ArchiveProcessor.cs` L23–L41 |
| E-3 | After a `VerificationFailed` row, a later pass still delivers the next revision, which can leave an archive gap today | [I] | same |
| E-4 | Runtime role: `GRANT SELECT, INSERT, UPDATE` on the whole `security_journal_entries` table (listed as "AppendOnly"); no DELETE; no journal immutability trigger; only CHECKs `ck_journal_digest_length`, `ck_journal_archive_state`; not covered by `trust_authority/authority-postgres-privileges.reference.sql` | [S] | `AuthorityRuntimeGrants.cs` L15–L19, L74–L77; `AuthorityEntityConfigurations.cs` `SecurityJournalEntryConfiguration` |
| E-5 | Only writers of the journal delivery columns: `JournalChain` (insert) and `ArchiveProcessor` | [S] | grep of Application/Infrastructure |
| E-6 | Organization gate (`RequiredBeforeEffective`) counts authorization-stream rows **not `Anchored`**; a falsely `Anchored` row opens it; `Asynchronous` mode does not wait for the archive | [S]; exploit [I] | `OrganizationGate.cs` |
| E-7 | Content checkpoint readability (SEC-P12) trusts `ArchiveDeliveryState == Anchored` | [S]; exploit [I] | `ContentService.cs` L1042, L1140 |
| E-8 | Integrity quarantine is permanent (database trigger; first reason kept); lifting is reserved for G14 recovery, which is **not implemented** (no recovery-epoch code) | [S] | `OrganizationGate.cs`; migration `20261104231152_ProjectionIntegrityS1` |
| E-9 | Each stream's revision advances only with a journal append: authorization (one site), content (five sites), audit (`AuditJournal.Append`) ⇒ per-stream contiguity expected | [S]; no test | `SecurityCommandService.cs` L303–L323; `ContentService.cs` L528/611/1064/1188/1544; `ContentRevisionCoordinator.cs` |
| E-10 | Blob archive: shared-key credential (full control); "create-only" is a client-side `If-None-Match`; `ReadAsync` returns null for a missing object **or a missing container**; no listing operation | [S] | `BlobSecurityEventArchive.cs` L51, L71, L118 |
| E-11 | Development archive = Azurite with the publicly known development account key (`infrastructure/docker/.env`); default compose: archive not persistent while AuthorityDb has a volume; security-test overlay: archive on tmpfs; only `compose.dev-persistent.yaml` persists it | [S] | `infrastructure/docker/compose.yaml` L135–L140, L222–L225; overlays |
| E-12 | Rows written while the body column was `jsonb` do not match their digests (by design; refused by `ArchiveProcessor`, organization quarantined) | [S] | migration `20261018110416_JournalEventBodyCanonicalText` |
| E-13 | Archive delivery runs in `AuthorityWorkLoop` only after the SpiceDB schema is ready | [S] | `BackgroundProcessors.cs` L67–L79 |
| E-14 | No Confidential Ledger implementation | [S] | grep |
| E-15 | Delivered-row deletion, re-digested alteration never resent, and out-of-window alteration are undetected; re-sent alterations quarantine | [T] in-process double only; [NV] on PostgreSQL | `CapabilityRefusalEvidenceRemovalTests.cs` |
| E-16 | M2F3-54: T2 = one INSERT and one UPDATE; "only the quarantine-permanence trigger fires" | [S] | `E3_M2F3_CONTRACT_CLOSURE.md` §10.5 |
| E-17 | Stream names `authorization`, `content`, `audit`; archive keys `org/{organization}/{stream}/{revision:D12}.json` (Blob) and `{organization}/{stream}/{revision}` (in-memory); the platform journal uses scope `Guid.Empty`, stream `platform-operators` in the same archive | [S] | `SecurityJournalEntry.cs`; `BlobSecurityEventArchive.cs`; `PlatformJournal.cs` L60, L68 |

---

## 4. Chain rules R-1..R-7 (one shared library: D+, verifier, baseline)

For an entry at key (organization `o`, stream `s`, revision `k`), applied identically to an AuthorityDb row and to archived bytes:

| Rule | Requirement | Failure subtype |
|---|---|---|
| **R-1** | `SHA-256(bytes)` equals the stored digest (row column; for archived bytes, the digest the comparison expects) | `BODY_DIGEST_MISMATCH` |
| **R-2** | Envelope format is exactly `DynamicForms/SecurityJournal/v1`. Any other or unparseable format makes the check **Inconclusive**, never valid | (Inconclusive: `ENVELOPE_UNSUPPORTED`) |
| **R-3** | Envelope `organization_id`, `stream_name`, `stream_revision`, `event_kind` equal the storage key **and** the row columns | `ENVELOPE_COLUMN_MISMATCH` |
| **R-4** | Envelope `previous_digest_hex` equals hex of the `journal_previous_digest` column (row checks) | `ENVELOPE_COLUMN_MISMATCH` |
| **R-5** | **Genesis:** `k = 1` ⇔ previous digest null. Null at `k > 1`, or non-null at `k = 1`, fails | `GENESIS_VIOLATION` |
| **R-6** | Revisions contiguous per (`o`, `s`), starting at 1 | `REVISION_GAP` |
| **R-7** | Link: for `k > 1`, previous digest equals `SHA-256(bytes of k − 1)` | `LINK_MISMATCH` |

`created_on_utc` is informational and never used for ordering or trust.

---

## 5. M-1: delivery-time chain enforcement (D+)

### 5.1 Rules

| # | Rule |
|---|---|
| DP-1 | D+ runs inside `ArchiveProcessor` for every pending entry of a **D+-enabled stream** (§5.6), in revision order, before `AppendAsync` |
| DP-2 | The entry must satisfy R-1..R-5 against its own row. Failure ⇒ stream stop (§5.3) with the subtype as reason |
| DP-3 | **Genesis:** for `k = 1` no predecessor is read; R-5 applies. The archive sentinel (§6.1) must be present (it is created at baseline, §10) |
| DP-4 | **Predecessor, `k > 1`:** for the first entry of the stream in a pass, read `archive[k − 1]`; inside the same pass, the predecessor is the byte array just confirmed by `AppendAsync` for `k − 1` |
| DP-5 | Before interpreting an absent predecessor, confirm the sentinel. Sentinel absent ⇒ `ARCHIVE_REGRESSION` (stream stop), never `PREDECESSOR_MISSING` |
| DP-6 | Predecessor absent, sentinel present ⇒ `PREDECESSOR_MISSING`. Predecessor present but its own R-1/R-2/R-3/R-5 fail ⇒ `CHAIN_INTEGRITY_FAILURE` with subtype. `SHA-256(predecessor) ≠` entry previous digest ⇒ `LINK_MISMATCH`. Each ⇒ stream stop |
| DP-7 | **Archive outage** (any read or write error other than "absent", or timeout): the entry stays `Pending`, the pass ends for that organization (existing ordering behaviour), no finding; `DELIVERY_LAG` applies after the threshold (§9.2). An outage is **never** treated as "absent" |
| DP-8 | **Retries and idempotency:** a crash after the archive write and before the AuthorityDb update leaves the row `Pending`; the next pass re-runs D+ (predecessor present) and `AppendAsync` finds identical bytes and records the receipt (existing idempotency). A crash before the archive write repeats the attempt unchanged |
| DP-9 | **No trust establishment by delivery:** D+ never delivers an entry whose predecessor is not present and linked in the archive. A delivered entry therefore links to an archived predecessor. D+ never uses AuthorityDb rows, receipts or delivery state as evidence about the archive |
| DP-10 | Existing checks and their responses are unchanged: `JOURNAL_BODY_DIGEST_MISMATCH` and `ARCHIVE_BODY_MISMATCH` keep their existing organization quarantine (preserved gate; not a new trigger) |
| DP-11 | **No new quarantine** (OD-DV-3, Q-3). D+ never sets `integrity_quarantine_enabled` |
| DP-12 | **Finding before state:** a D+ refusal first writes a create-only finding object (§9.3) and raises a Critical alert, then records the row state. A J1 actor later resetting the row state does not erase the finding; the stop is re-derived from the archive on every pass (DP-13) |
| DP-13 | Stop re-derivation: a stopped stream is not trusted from AuthorityDb state alone; each pass re-applies DP-2..DP-6, which refuse again while the condition holds |
| DP-14 | D+ does not change T2, the recorder, or any request path |

### 5.2 Interaction with existing restore detection

After an AuthorityDb restore to revision `r` (J3), the append at `r + 1` passes D+ (row `r` is genuine and linked) and then collides in `AppendAsync`, giving the existing `ARCHIVE_BODY_MISMATCH` quarantine (M2F3-57) **[I]**; this must be re-verified (§13.4).

### 5.3 Stream stop

| Aspect | Contract |
|---|---|
| Effect | The refused entry is marked `VerificationFailed` with `last_archive_error_code` = the D+ reason; no later revision of that (organization, stream) is delivered while the condition holds (DP-13). Rows after it stay `Pending` (they are not cascaded to `VerificationFailed`) |
| Scope | One organization, one stream. Other streams and organizations are unaffected |
| Audit stream | Non-gating: delivery of audit evidence stops; access is not blocked |
| Authorization / content streams (only once enabled, §5.6) | Existing gates stay closed (E-6, E-7): **a de facto permanent block** of protected access for that organization until recovery. This is why their enablement is gated |
| Recovery | **Not provided by D-R.** Resumption requires a separately approved recovery process (G14). D-R never repairs, re-delivers, re-signs or skips an entry |

### 5.4 Not caught by D+

An undelivered entry altered before delivery while keeping its link (and the organization head updated so the next append chains onto it) is delivered as if genuine (R-DR-1).

### 5.5 Performance

At most one archive read per (organization, stream) per pass, plus the existing appends. To be measured (AC-DR-11).

### 5.6 Phases and gates (OD-DV-2, Q-5)

| Phase | Streams with D+ | Streams with verification | Gate |
|---|---|---|---|
| **P1** | `audit` | `audit`, `authorization`, `content` (alert-only verifier) | WP-5a authorization (separate) |
| **P2** | adds `authorization`, `content` | all three | **Separate owner gate**, only after recovery behaviour for a stopped stream is demonstrated (G14 or an approved recovery procedure) and the stop consequences (§5.3) are accepted |

**Interim limitation and conflict CF-1.** During P1, delivery on the authorization and content streams continues **without** D+. A J1 actor can make the Authority archive an authorization or content entry that links to a forged predecessor. This is **detected after the fact** by the verifier (archive-side R-7 within the incremental latency) and recorded as a security finding, but it is **not prevented**. This conflicts with M-1 ("do not allow delivery to silently establish trust in a forged predecessor") for those two streams. The owner accepted the phased approach (OD-DV-2); the conflict is recorded, not resolved, and closes only with P2.

### 5.7 No weakening on an enabled stream (OD-DV-7)

Once a stream is D+-enabled, DP-1..DP-14 apply in full and permanently for that stream. There is no partial, sampled, warn-only or bypass mode, and no configuration that disables D+ for an enabled stream without a new owner decision.

---

## 6. Archive contract

### 6.1 Identity sentinel (N-3)

| # | Rule |
|---|---|
| AS-1 | A create-only object at a fixed reserved key (for example `meta/archive-identity.json`) holding an archive-instance identifier and creation time, created once per archive instance at baseline (§10) |
| AS-2 | D+ and the verifier confirm the sentinel before interpreting any "absent" result. Sentinel absent ⇒ `ARCHIVE_REGRESSION` (container wiped, replaced or misconfigured) |
| AS-3 | The checkpoint record (§7.1) carries the archive-instance identifier; a different identifier ⇒ `ARCHIVE_REGRESSION` |
| AS-4 | **The sentinel proves only that the expected archive instance is being read. It does not prove completeness, freshness of the archive, or freshness of any checkpoint.** |

### 6.2 Enumeration (N-2) — mandatory for completeness

| # | Rule |
|---|---|
| AE-1 | The archive port gains a complete enumeration operation over a key prefix (organization, organization+stream), with paging, returning every object key under the prefix. Blob: list by prefix; in-memory: scan. A partial or failed enumeration is **Inconclusive**, never "complete" |
| AE-2 | **Probing alone (reading guessed keys) never establishes completeness.** Every full sweep enumerates the complete `org/{o}/` prefix; every incremental sweep enumerates at least the complete stream prefix range above the trusted checkpoint revision |
| AE-3 | **Unexpected object:** a key under `org/{o}/` that is not a canonical entry key (unknown stream name, non-canonical revision encoding, extra path segment) ⇒ `ARCHIVE_UNEXPECTED_OBJECT`. The platform journal scope (`Guid.Empty`, stream `platform-operators`, E-17) and the reserved sentinel and checkpoint prefixes are excluded by exact match only |
| AE-4 | **Gap:** a missing revision below the highest archived revision of the stream ⇒ `ARCHIVE_DISCONTINUITY` |
| AE-5 | **Duplicate:** two objects whose envelopes claim the same (organization, stream, revision), or an envelope whose identity differs from its key ⇒ `ARCHIVE_DUPLICATE` / `ENVELOPE_COLUMN_MISMATCH` |
| AE-6 | **Beyond the expected head:** an archived revision above the AuthorityDb head: contiguous ⇒ `BEHIND_ARCHIVE`; after a gap ⇒ `ARCHIVE_DISCONTINUITY` |
| AE-7 | Enumeration consistency of the production store (listing after create) must be verified on Azure before production claims **[NV]** |
| AE-8 | Enumeration cost is part of the measurement (AC-DR-11). Enumeration reads keys only; object bodies are read per §8 |

### 6.3 Development persistence and coordinated reset (N-1)

| # | Rule |
|---|---|
| RST-1 | Development/Testing stacks must keep the archive, the checkpoint store and AuthorityDb with **the same persistence lifetime** (all persistent, or all disposable). Today the default compose persists AuthorityDb but not the archive (E-11): changing that is an infrastructure task (WP-2), not done here |
| RST-2 | **Coordinated reset policy:** AuthorityDb, the archive and the checkpoint store are reset **together or not at all**. A partial reset produces genuine findings (`ARCHIVE_REGRESSION`, `BEHIND_ARCHIVE`, `CHECKPOINT_STORE_REGRESSION`) that are **not suppressed** (OD-DV-4); they are triaged as "development reset" by a human and recorded |
| RST-3 | Disposable gate stacks (tmpfs, `down -v`) satisfy RST-1 by construction; every new stack is baselined before verification starts |

---

## 7. M-3: trusted verification progress (checkpoints)

### 7.1 Record

Create-only object at `verification/{organization}/{stream}/{sequence:D12}` in the checkpoint store, schema `DynamicForms/JournalVerification/v1`:

| Field | Meaning |
|---|---|
| `organization_id`, `stream_name`, `checkpoint_sequence` | Identity; sequence starts at 1 (baseline) |
| `verified_revision`, `verified_digest` | Highest revision verified and `SHA-256(archive[verified_revision])` |
| `previous_checkpoint_digest` | SHA-256 of checkpoint `sequence − 1` (null at 1) |
| `archive_instance_id` | From the sentinel (AS-3) |
| `walk_kind` | `Baseline`, `Incremental`, `Full` |
| `assurance_level` | `Full` or `Reduced` (carried from the baseline, §10) |
| `baseline_exception_ids` | Exception identifiers inherited from the baseline (empty when CLEAN) |
| `verifier_identity_label` | Non-secret label of the verifying identity |
| `verified_at_utc` | Verifier clock; informational |

### 7.2 Rules

| # | Rule |
|---|---|
| CK-1 | Created only after a sweep with **no security finding and no Inconclusive outcome** in its scope (VF-11) |
| CK-2 | `checkpoint_sequence` and `verified_revision` never decrease; a new checkpoint is valid only if checkpoint `sequence − 1` exists and its digest equals `previous_checkpoint_digest` |
| CK-3 | `assurance_level` and `baseline_exception_ids` are carried forward unchanged; they never upgrade automatically (only a new owner-approved baseline can change them) |
| CK-4 | **Discovery:** exponential then binary search over `checkpoint_sequence` (or enumeration of the checkpoint prefix, AE-1); a process-local cache may supply a lower bound; **AuthorityDb is never consulted** |
| CK-5 | On every verifier start: verify the discovered head's link to its predecessor; on every full sweep: verify the entire checkpoint chain back to the baseline |
| CK-6 | **Production head anchoring:** every checkpoint's digest, organization, stream and sequence is appended to Azure Confidential Ledger and the receipt retained. The trusted checkpoint head is the **ledger-anchored** head, retrieved and verified (receipt and ledger identity) on every sweep |
| CK-7 | Checkpoint store unavailable ⇒ sweep Inconclusive (`CHECKPOINT_STORE_UNAVAILABLE`) |
| CK-8 | A discovered head below the trusted head (CK-6, or within a process lifetime the cached head) ⇒ `CHECKPOINT_STORE_REGRESSION` |
| CK-9 | Checkpoint whose bytes, schema or link do not verify ⇒ `CHECKPOINT_CHAIN_INVALID` |
| CK-10 | **Recovery:** a lost, corrupted or regressed checkpoint store is a security finding; verification for the affected scope is **not** reported as verified until a new baseline (§10) is established and approved. Never restart silently from scratch as "verified" |
| CK-11 | Checkpoints and findings are never written to AuthorityDb (a cache in AuthorityDb, if ever added, is a hint only and never trusted) |
| CK-12 | The checkpoint store's identity and administration are separate from the entry archive's writer (OD-DV-5); the Authority runtime identity has no write access to it in production |

### 7.3 Rollback classes (owner correction)

| Class | What happens | Detected by | Assurance |
|---|---|---|---|
| **CKR-1 Corruption** | A checkpoint object altered, or a link between checkpoints broken | Internal chain verification (CK-5, CK-9) | Detected wherever the checkpoints are readable |
| **CKR-2 Rollback with an independent anchor** | Newest checkpoints deleted or the store replaced by an older copy, while an independently trusted monotonic anchor of the head exists | Comparison of the discovered head with the **ledger-anchored head** (CK-6, CK-8) | **Production only**, with Confidential Ledger implemented and its retrieval verified |
| **CKR-3 Rollback without an independent anchor** | Same, in a configuration with no independent anchor (Development/Testing) | **Not detectable** across verifier restarts. A process-local cache detects regression only within one process lifetime | **No freshness assurance.** A checkpoint hash chain does not prove that its latest checkpoint still exists; the sentinel, a process-local cache and a separately keyed Azurite instance **do not** prove checkpoint freshness |

### 7.4 Development/Testing and production

| Environment | Checkpoint store | Freshness |
|---|---|---|
| Development/Testing | A second Azurite instance with its own non-default account key, reachable only by the verifier, persistence per RST-1 | CKR-3: none. Demonstrates behaviour only; the same Docker administrator controls everything |
| Production | Immutable container with a locked time-based retention policy, verifier-only managed identity (create, no delete), **plus** Confidential Ledger head anchoring | CKR-2: anchored, once implemented and verified on Azure **[NV]** |

---

## 8. M-2: scheduled verifier

### 8.1 Inputs

The trusted checkpoint head `P = (v, d_v)` per (organization, stream) (§7); the archive (sentinel, enumeration, objects); AuthorityDb **read-only**, one consistent snapshot per sweep (repeatable-read transaction), journal rows and organization head columns only.

### 8.2 Incremental sweep (every `T_inc`)

| # | Step |
|---|---|
| VF-1 | Load and verify `P` (CK-4, CK-5; production CK-6). Unavailable ⇒ Inconclusive |
| VF-2 | Confirm the sentinel and its identifier (AS-2, AS-3) |
| VF-3 | `SHA-256(archive[v]) = d_v`, else `ARCHIVE_REGRESSION` |
| VF-4 | Enumerate the stream prefix above `v` completely (AE-1, AE-2); classify unexpected objects, gaps, duplicates (AE-3..AE-5); let `h_a` be the highest contiguous archived revision |
| VF-5 | Read every archived object `v+1..h_a`; apply R-1..R-7 to archived bytes |
| VF-6 | Read AuthorityDb rows `v..h_db`; apply R-1..R-7 to rows; row `v`'s digest must equal `d_v` (`ANCHOR_MISMATCH`) |
| VF-7 | For every `k ≤ h_a` in range: row bytes must equal archived bytes **byte for byte** (`ANCHOR_MISMATCH`) |
| VF-8 | Rows `h_a < k ≤ h_db`: `Anchored` or receipt present ⇒ `FAKE_RECEIPT` (after a second read, with sentinel present and archive reachable); `Pending` older than the lag threshold ⇒ `DELIVERY_LAG`; `VerificationFailed` ⇒ reference the existing finding |
| VF-9 | Archive objects above `h_db` ⇒ AE-6 |
| VF-10 | Never treat AuthorityDb delivery state or receipts as evidence that an object exists |

### 8.3 Full sweep (every `T_full`)

| # | Step |
|---|---|
| VF-12 | Enumerate the complete `org/{o}/` prefix (AE-2, AE-3) |
| VF-13 | Walk AuthorityDb rows `1..h_db` with R-1..R-7 and anchor at the trusted checkpoint (`v`, `d_v`); verify the full checkpoint chain (CK-5). Catches non-re-chained edits below `v`; re-chained edits change row `v` and fail VF-6 |

### 8.4 Outcomes and invariants

| # | Rule |
|---|---|
| VF-11 | Sweep outcome: **Clean** (no finding, nothing inconclusive) ⇒ new checkpoint (CK-1); **Findings** ⇒ no checkpoint advance; **Inconclusive** ⇒ no checkpoint advance |
| VF-14 | The verifier **never** writes AuthorityDb, repairs, re-delivers, re-signs, deletes, changes quarantine, or acts on another organization; findings and alerts only (OD-DV-3) |

### 8.5 Hosting and identity (OD-DV-5, Q-8)

| Environment | Hosting | AuthorityDb identity | Archive identity | Checkpoint identity | Documented limitation |
|---|---|---|---|---|---|
| Development/Testing | Background service in the Authority host, **independent of SpiceDB readiness** (E-13) | Dedicated read-only login (SELECT on the journal and organization head columns only); creating it is a G08 privilege change in WP-4, needing that increment's authorization (OI-DR-2) | Shared Azurite key (E-11) | Separate Azurite key (§7.4) | Shares the process with the Authority (JP collapses everything); Azurite keys are known; no independence; demonstrates detection logic against J1, J2, J3 only |
| Production | **Separate workload**, separate pipeline (PB-11) | Dedicated read-only managed identity | Read-only (entries) | Create-only on checkpoints and findings, plus ledger append | Requires §14 |

### 8.6 Periods and latency (OD-DV-6)

| Parameter | Development/Testing measurement default | Production |
|---|---|---|
| `T_inc` | 5 min | After measurement; owner approval |
| `T_full` | 24 h | same |
| Delivery-lag threshold `L` | 15 min | same |
| Verification staleness | 15 min | same |

Detection latency targets (to be measured, AC-DR-11): newly delivered rows and archive anomalies ≤ `T_inc` + sweep duration; non-re-chained edits below the checkpoint ≤ `T_full` + sweep duration; `FAKE_RECEIPT` ≤ `T_inc` + sweep duration after the row is written. Delivery itself has no latency bound (E-13).

---

## 9. M-4: findings

### 9.1 Security findings (Critical alert + create-only finding object; no automatic response)

| Code | Exact trigger |
|---|---|
| `CHAIN_INTEGRITY_FAILURE` (`BODY_DIGEST_MISMATCH`, `ENVELOPE_COLUMN_MISMATCH`, `GENESIS_VIOLATION`, `REVISION_GAP`, `LINK_MISMATCH`) | R-1, R-3..R-7 fail on a row or an archived object |
| `ANCHOR_MISMATCH` | Row bytes ≠ archived bytes at `k ≤ h_a`, or row `v` ≠ `d_v` |
| `FAKE_RECEIPT` | Row `Anchored` or with a receipt; sentinel present; archive reachable; object absent on a second read |
| `ARCHIVE_REGRESSION` | Sentinel absent or different; `archive[v]` absent or ≠ `d_v` |
| `ARCHIVE_DISCONTINUITY` | A gap below the highest archived revision (AE-4, AE-6) |
| `ARCHIVE_UNEXPECTED_OBJECT` | AE-3 |
| `ARCHIVE_DUPLICATE` | AE-5 |
| `BEHIND_ARCHIVE` | Archive holds `h_db + 1` contiguously. **Restore divergence: always a security finding requiring manual investigation, never suppressed because a restore is claimed to be legitimate** (OD-DV-4) |
| `PREDECESSOR_MISSING`, `LINK_MISMATCH` (D+) | §5.1 |
| `CHECKPOINT_STORE_REGRESSION` | CK-8 |
| `CHECKPOINT_CHAIN_INVALID` | CK-9 |

### 9.2 Operational findings (warning alert)

| Code | Trigger | Escalation |
|---|---|---|
| `DELIVERY_LAG` | Oldest `Pending` row older than `L` with the archive reachable | Repeated alert after 4 × `L`; it is never reclassified as a security finding without a security trigger |
| `ARCHIVE_UNAVAILABLE` | Archive read or enumeration errors or timeouts (not "absent") | Sweep Inconclusive |
| `CHECKPOINT_STORE_UNAVAILABLE` | Same for the checkpoint store | Sweep Inconclusive |
| `VERIFICATION_STALE` | No Clean sweep for longer than the staleness threshold | |

### 9.3 Finding records

Create-only objects in a findings namespace beside the checkpoints (same separately controlled store), keyed by (organization, stream, revision or range, code, detection sequence); content: code, subtype, revision range, archive-instance identifier, verifier identity label, UTC time. **No event body, no protected data.** D+ findings are written by the delivering identity (DP-12) to a findings namespace it can create in but not delete from (production).

### 9.4 Reserved

`ARCHIVE_CHAIN_BREAK` is a **reserved** quarantine reason code. It is **not activated**: no code path may set it. Activation requires a new owner decision, an implemented G14 recovery path and its own acceptance criteria (Q-3).

---

## 10. M-5: baseline reconciliation

| # | Rule |
|---|---|
| BL-1 | Per (organization, stream), before the first checkpoint: write the sentinel if the archive instance has none; complete enumeration (AE-1..AE-6); read every archived object; apply R-1..R-7 to both sides; byte-compare every row with its archived object |
| BL-2 | Read-only on AuthorityDb; never repairs, re-delivers, rewrites, deletes or re-signs historical evidence |
| BL-3 | **Outcome CLEAN:** no discrepancy, nothing inconclusive ⇒ checkpoint 1 with `assurance_level = Full` |
| BL-4 | **Outcome EXCEPTION_ACCEPTED:** every discrepancy belongs to a recorded exception that has an **explicit owner disposition** ⇒ checkpoint 1 with `assurance_level = Reduced` and the exception identifiers. **Not equivalent to CLEAN**; the reduced assurance is carried forward (CK-3) and reported |
| BL-5 | **Outcome FAILED:** any discrepancy without an owner disposition ⇒ no checkpoint; security findings raised |
| BL-6 | **Outcome INCONCLUSIVE:** incomplete enumeration, unavailable store, unsupported envelope, timeout ⇒ no checkpoint |
| BL-7 | **Exception record:** identifier; organization; stream; revision range; class (for example `LEGACY_UNVERIFIABLE` for `jsonb`-era rows, E-12); evidence; provenance (tool version, source commit, operator, UTC time, archive-instance identifier); owner disposition reference; reduced-assurance statement |
| BL-8 | **Baseline report:** scope, outcome, every finding and exception, provenance as BL-7; retained with the checkpoint |
| BL-9 | **Statement (mandatory in every report):** the baseline proves only that AuthorityDb equalled the archive at the baseline time. It does **not** prove that history before the baseline was genuine (§2.4) |
| BL-10 | A re-baseline (after CK-10 or a coordinated reset) starts a new checkpoint sequence and is reported as such; prior findings stay on record |

---

## 11. M-6: journal immutability hardening — separate G08 increment

M-6 is **not part of D-R's implementation scope**. It is a separate G08 increment, approved for **design analysis only** (owner, 2026-11-17). Its design input, recorded here so that D-R and M-6 stay consistent:

| Item | Design input |
|---|---|
| Current state | E-4: table-wide UPDATE and INSERT for the runtime role; no trigger |
| UPDATE | Column-level grant limited to the delivery metadata written by `ArchiveProcessor` (E-5): `archive_delivery_state`, `archive_attempt_count`, `archive_receipt_reference`, `archive_receipt_digest`, `journal_archived_on`, `last_archive_error_code` (exact physical names to be confirmed against the model in the M-6 increment) |
| Immutable fields | BEFORE UPDATE trigger refusing changes to body, digest, previous digest, organization, stream, revision, kind, command and operation identifiers and creation time; state transitions only `Pending → Anchored` (receipt required) and `Pending → VerificationFailed`; terminal states final |
| DELETE / TRUNCATE | Triggers that raise (defense in depth; J2 can disable them) |
| INSERT | Required for appends; **no INSERT trigger in M-6 v1**: it would fire inside T2 and contradict M2F3-54 ("only the quarantine-permanence trigger fires", E-16). A forged insertion by J1 stays possible and is detected by D-R |
| Constraints | F-E2 (`--migrate` re-applies grants in autocommit statements after `REVOKE ALL`): column grants fall inside the same window; architecture tests pinning grants and the G08 inventories (TQ-21 class) change; trigger functions owned by the schema owner, `SECURITY INVOKER`, fixed `search_path` |
| Effect | Narrows J1 (alteration of immutable fields, fake terminal-state changes); **does not constrain J2**; does not replace D-R |

---

## 12. Dependencies

| Dependency | Relation |
|---|---|
| **G14 recovery** (`trust_authority/COORDINATED_DISASTER_RECOVERY.md`) | Required to classify `BEHIND_ARCHIVE` and restore collisions, to resolve findings, to resume a stopped stream (§5.3) and before any quarantine activation (§9.4). **Not implemented** (E-8). P2 depends on demonstrated recovery behaviour |
| Archive independence (G14, PB-10, G08) | All production guarantees (§14) |
| BD-1, BD-2 | No hard dependency. D-R never inserts or reconstructs evidence |
| M-6 / G08 / F-E2 | §11 |
| SEC-P01..SEC-P12 (ADR-PB-003) | Regression when D+ (WP-5) or M-6 is implemented (N-4; §13.4). Not Stage B |
| PB-09, PB-10, PB-11, PB-12 | Production targets, Canadian residency of archive, checkpoint store and ledger, separate verifier pipeline, release evidence |
| R-M2f3-6 | Unchanged (R-DR-1) |

---

## 13. Acceptance contract

### 13.1 Layers and runs of record

- **IP:** in-process; transactional AuthorityDb double, in-memory archive, separately keyed in-memory checkpoint store.
- **PG:** **two independent runs of record** on fresh disposable PostgreSQL 18.0 with **two Azurite instances** (archive; checkpoint store with a different key) and Vault Transit, following the E3-M2f-3G pattern (containers removed afterwards, evidence scrubbed, no persistent database, no Stage B).

Every scenario below demonstrates **Development/Testing behaviour only** (§13.6).

### 13.2 Adversarial scenarios

| ID | Scenario (actor) | Expected | Layer |
|---|---|---|---|
| ADV-01 | Delete a delivered interior row, no re-chain (J2) | `REVISION_GAP` within `T_inc` | IP, PG |
| ADV-02 | Delete a delivered interior row with consistent re-chain and head update (J2) | `ANCHOR_MISMATCH`; on a D+ stream, D+ `LINK_MISMATCH` at the next delivery | IP, PG |
| ADV-03 | Truncate the delivered tail and reset the head; organization then quiet (J2) | `BEHIND_ARCHIVE` | IP, PG |
| ADV-04 | Truncate the tail, then a new append (J2/J3) | Existing `ARCHIVE_BODY_MISMATCH` quarantine unchanged (M2F3-57) | PG |
| ADV-05 | Re-digested alteration of a delivered row, never resent, inside and outside the newest eight (**J1 as runtime role**) | `ANCHOR_MISMATCH` | IP, PG |
| ADV-06 | Digest-kept alteration of a delivered row outside the window (J1) | `BODY_DIGEST_MISMATCH` | IP, PG |
| ADV-07 | Rewrite-and-extend: consistent rewrite of delivered rows, then a genuine append delivered (J1) | D+ refusal (audit, P1); verifier `ANCHOR_MISMATCH`; on non-D+ streams, verifier archive-side `LINK_MISMATCH` (CF-1) | IP, PG |
| ADV-08 | Inserted forged row marked `Anchored`, including a future-dated evidence time (J1) | `FAKE_RECEIPT`; D+ `PREDECESSOR_MISSING` at the next delivery; recorder suppression recorded as residual | IP, PG |
| ADV-09 | Swap or renumber revisions (J1) | `ENVELOPE_COLUMN_MISMATCH` / `LINK_MISMATCH` | IP, PG |
| ADV-10 | Move a row to another organization or stream (J1) | `ENVELOPE_COLUMN_MISMATCH` | IP, PG |
| ADV-11 | Planted AuthorityDb hint or stale checkpoint reference (J1) | Ignored; trusted head used; no false Clean | IP |
| ADV-12 | Archive container wiped or replaced; archived object overwritten (J4, Development) | `ARCHIVE_REGRESSION` | IP, PG |
| ADV-13 | Archive or checkpoint-store outage mid-sweep; verifier killed mid-sweep and restarted | Inconclusive; no checkpoint advance; correct resumption | IP, PG |
| ADV-14 | Legitimate restore copy (J3) | `BEHIND_ARCHIVE` as a security finding, not suppressed | PG |
| ADV-15 | Tampering in organization A or one stream | Organization B and the other streams verified Clean; no side effect on them | IP, PG |
| ADV-16 | Work loop stopped beyond `L` | `DELIVERY_LAG` only (operational) | IP, PG |
| ADV-17 | Statement trace of verifier and baseline tool | Zero writes to AuthorityDb; no quarantine change | PG |
| ADV-18 | ADV-05/07/08 executed as the runtime role on PostgreSQL (and, only if M-6 is authorized and implemented, with M-6) | Verifier detects; with M-6 immutable-field changes refused | PG |
| ADV-19 | Checkpoint-store rollback (newest checkpoints deleted) (J6, Development) | Within one process lifetime `CHECKPOINT_STORE_REGRESSION`; after restart **not detected** (CKR-3), recorded as the Development/Testing limitation; CKR-2 is a production-only claim | IP |
| ADV-20 | Genesis forgery: null previous digest at `k > 1`, or non-null at `k = 1` | `GENESIS_VIOLATION` (D+ and verifier) | IP |

Enumeration-specific cases are part of ADV-12, ADV-03 and ADV-01 runs: an unexpected key (AE-3), a gap (AE-4), a duplicate (AE-5) and an object beyond the head (AE-6) are each seeded in the archive and must produce their code.

### 13.3 Mutations (each must be DETECTED by declared tests; driver built `--no-incremental`, run in a scratch clone, restore verified)

| ID | Mutation |
|---|---|
| MV-01 | Drop the anchor at `v` (VF-6) |
| MV-02 | Drop the row-versus-archive byte comparison (VF-7) |
| MV-03 | Trust AuthorityDb delivery state as evidence of archive presence (VF-10) |
| MV-04 | Skip the gap check (R-6 / AE-4) |
| MV-05 | Determine the archive head by probing instead of enumeration (AE-2) |
| MV-06 | Treat an unavailable archive as verified (VF-11) |
| MV-07 | Read the checkpoint from AuthorityDb (CK-11) |
| MV-08 | Write a checkpoint before the sweep completes or despite findings (CK-1) |
| MV-09 | Allow a non-monotonic checkpoint (CK-2) |
| MV-10 | D+ skips the predecessor-existence check (DP-6) |
| MV-11 | D+ compares only the column or only the envelope previous digest (R-4, DP-2) |
| MV-12 | D+ treats an archive outage as "absent" (DP-7) |
| MV-13 | Skip the sentinel check (AS-2, DP-5) |
| MV-14 | Skip the genesis rule (R-5) |
| MV-15 | Verifier writes AuthorityDb or sets quarantine (VF-14) |
| MV-16 | Skip the full sweep (VF-12, VF-13) |
| MV-17 | Baseline repairs or rewrites a row (BL-2) |
| MV-18 | `FAKE_RECEIPT` raised without the second read (VF-8) |

### 13.4 Regressions

| Item | Requirement |
|---|---|
| Suites | Full Authority, architecture and contract suites green (existing open items TS-M2f-01 and the unidentified 3G contract failure reported separately, not attributed) |
| **SEC-P01..SEC-P12** | Re-run on a disposable stack whenever D+ (WP-5) or M-6 is implemented (N-4); **not Stage B** |
| **M2F3-31** | Archive delivery of evidence and archive outage behaviour re-verified on PG with D+ |
| **M2F3-43** | Passes **as written** for delivered rows (EG-1..EG-3, ADV-01..ADV-10) |
| **M2F3-54** | Unchanged: T2 statement set and trigger set identical (verifier and baseline sessions are distinguishable by their dedicated identity) |
| **M2F3-57** | Restore collision detection, organization isolation and preservation unchanged (§5.2) |
| T2 footprint | No request-path change (DP-14, VF-14) |

### 13.5 Inverted gap tests

| ID | Existing test (`CapabilityRefusalEvidenceRemovalTests`) | Inversion |
|---|---|---|
| EG-1 | `ExpectedGapDeletingADeliveredRowIsNotDetectedAndTheKeyIsRecordedAgain` | Attack steps unchanged; "not detected" assertions replaced by a verifier `REVISION_GAP` finding; the recorder's re-recording stays asserted as residual characterization (R-DR-2) |
| EG-2 | `ExpectedGapAConsistentlyRedigestedDeliveredRowThatIsNotSentAgainIsNotDetected` | Same; expected `ANCHOR_MISMATCH`; the recorder's `AlreadyRecorded` for the forged key stays asserted as residual |
| EG-3 | `ExpectedGapAnAlteredDeliveredRowOutsideTheNewestEightIsNotDetected` | Same; expected `BODY_DIGEST_MISMATCH` / `ANCHOR_MISMATCH` |

They are reported as M2F3-43 detection tests in a new record; the historical FU-M2f3-01 outcomes (`E3_M2F3_FU_VERIFICATION_RECORD.md` §2.3) are not rewritten.

### 13.6 Acceptance criteria

| ID | Criterion |
|---|---|
| AC-DR-01 | ADV-01..ADV-20 pass at their layers; PG in two runs of record |
| AC-DR-02 | EG-1..EG-3 inverted and passing; M2F3-43 passes as written for delivered rows |
| AC-DR-03 | MV-01..MV-18 DETECTED |
| AC-DR-04 | §13.4 regressions green, including SEC-P01..SEC-P12 when triggered |
| AC-DR-05 | Organization and stream isolation (ADV-15) |
| AC-DR-06 | Restart, restore, replay, idempotent D+ retry and checkpoint rollback behaviour (ADV-13, ADV-14, ADV-19, DP-8) |
| AC-DR-07 | Archive enumeration completeness: unexpected objects, gaps, duplicates and beyond-head objects detected (AE-3..AE-6) |
| AC-DR-08 | Baseline outcomes CLEAN, EXCEPTION_ACCEPTED, FAILED and INCONCLUSIVE each demonstrated, with reports per BL-7/BL-8 |
| AC-DR-09 | Fail-closed: no unavailable or partial input ever yields Clean or a checkpoint |
| AC-DR-10 | No AuthorityDb write, repair, quarantine or cross-organization effect by the verifier or the baseline tool (ADV-17) |
| AC-DR-11 | Measured and recorded: D+ added latency per delivery; incremental and full sweep duration and archive reads/enumeration on a 2M-row synthetic organization; observed detection latencies against §8.6 |
| AC-DR-12 | R-FU-1 restated with its residual (R-DR-1, R-DR-2, pre-baseline history, J-class limits) |
| AC-DR-13 | Owner acceptance; claims limited to Development/Testing |
| AC-DR-14 | Specification validation passes (`scripts/validate-specifications.py --strict`) |

**Demonstrated by the above (once implemented):** Development/Testing behaviour against J1, J2 and J3 and the Development forms of J4 and J6 as stated.

**Not proven by any test above (production):** archive and checkpoint-store independence (J4, J5, J6); locked immutability; Confidential Ledger anchoring and CKR-2; Azure Blob enumeration consistency and performance; Azure PostgreSQL behaviour; G14 classification and recovery; Canadian residency; independent review.

---

## 14. Production prerequisites

1. Entry archive: locked time-based immutability, Entra RBAC, shared-key access disabled, Canadian regions (PB-10).
2. Distinct identities: Authority writer (create-only on entries), verifier (read-only on entries; create-only on checkpoints and findings; ledger append), administrators separate from AuthorityDb administrators.
3. Checkpoint store: immutable container plus Confidential Ledger head anchoring with verified retrieval (CK-6).
4. Separate verifier workload and pipeline (PB-11); release evidence (PB-12).
5. G14 recovery and recovery epoch; before P2 and before any activation of `ARCHIVE_CHAIN_BREAK`.
6. Measured production targets (PB-09) approved by the owner.
7. Azure verification of enumeration consistency, immutability semantics and ledger behaviour.
8. Independent security review.

None of these exists today. **No production assurance is established.**

---

## 15. PD-15 closure dependencies

SEC-BL-01 remediation is **necessary but not sufficient** for PD-15. After SEC-BL-01 is closed, PD-15 still requires (unchanged, `E3_M2F3_CONTRACT_CLOSURE.md` §14.1):

| Item | State |
|---|---|
| M2F3-33 | Not executed; needs a bound organization (OPEN-M2f-4). **No prohibited data-key binding may be inserted to make a test pass** |
| M2F3-28 | Reduced scope (binding-inserting Prepares; OPEN-M2f-4) |
| M2F3-29, -31, -42, -78 | Reduced scope as recorded (M2F3-31 is re-verified under §13.4 but its reduced host-loop scope is unchanged unless separately extended) |
| M2F3-44 | Inconclusive; TS-M2f-01 open; the unidentified 3G contract failure unattributed |
| MZ-22, MZ-03 | Owner dispositions as recorded (equivalent survivor; not executable as written) |
| OPEN-M2f-4, TS-M2f-01 | Open |

---

## 16. Documentation corrections (Q-6; dated notes only, historical records preserved)

| ID | Finding | Applied where (2026-11-17) |
|---|---|---|
| D-1 | "A4" ambiguity (assumption A4 of closure §5.4 versus attacker class A4 of the E-3 design §7) | Notes in `E3_M2F3_CONTRACT_CLOSURE.md` §5.4 and §11.3 (R-M2f3-6, R-FU-1), `SECURITY_BACKLOG.md`, design doc §1, FU record §2.3; this document §2.2–§2.3 |
| D-2 | Stale "still open" FU entries and the 3G record's "no dedicated test" for M2F3-43 | Notes in `E3_M2F3_CONTRACT_CLOSURE.md` §14 and `E3_M2F3G_VERIFICATION_RECORD.md` §12 |
| D-3 | R-FU-1 understates suppression: a future-dated forged evidence row makes later lookups Inconclusive (LK-6) until the database clock passes it (source inspection; not executed) | Notes on R-FU-1 in the closure, backlog and FU record |
| D-4 | Alteration and insertion need only J1 (runtime credential); deletion needs J2 | Same places |
| D-5 | Design D §4.3 full-walk detection claim needs D+ (weakness W-1); §4.4 omits the runtime role's UPDATE/INSERT; W-2 (forged row never delivered below a delivered anchor) | Notes in the design doc §4.3, §4.4 and new §10 |

---

## 17. Work packages (each needs separate owner authorization)

| WP | Content | Depends on | Type |
|---|---|---|---|
| **WP-0** | This contract and the dated corrections | C-155 | Documentation (**this increment**) |
| WP-1 | Chain-rules library R-1..R-7; known-answer vectors from the real journal format | WP-0 accepted | Code (pure) |
| WP-2 | Archive port: sentinel, enumeration, checkpoint and findings store ports; Blob and in-memory implementations; Development second Azurite instance; RST-1 persistence change | WP-0 | Code and infrastructure |
| WP-3 | Baseline tool (read-only) and report format | WP-1, WP-2 | Code and runbook |
| WP-4 | Verifier, alert-only, all three streams; dedicated read-only database identity (G08 change) | WP-3 | Code and privileges |
| WP-5a | D+ on the `audit` stream (P1) | Clean or EXCEPTION_ACCEPTED baseline per organization | Product behaviour change |
| WP-5b | D+ on `authorization` and `content` (P2) | Separate owner gate (§5.6) | Product behaviour change |
| WP-6 | M-6 (§11) | Separate G08 authorization | Migration |
| WP-7 | Verification (§13) | WP-4, WP-5a (WP-6 if implemented) | Tests and gates |
| WP-8 | Production prerequisites (§14) | Azure; PB-10/11/12 | Outside Development/Testing |

---

## 18. Risks

| ID | Risk | Status |
|---|---|---|
| R-DR-1 | Forgery of an undelivered entry is never detectable (= R-M2f3-6) | Accepted residual (existing) |
| R-DR-2 | The recorder acts on forged rows until detection (suppression window ≤ sweep latency) | Open; inherent to detection-only design |
| R-DR-3 | J1+J4, J2+J5, J6 and JP defeat D-R | Inherent; mitigated only by separation (§14) |
| R-DR-4 | History before the baseline is unattested (§2.4) | Accepted by Q-7 |
| R-DR-5 | A D+ stop on authorization/content is a de facto permanent block until recovery | Gated by P2 |
| R-DR-6 | Archive pre-emption by J4 (objects created at future keys) causes collision quarantine (denial of service) | Open; production identity separation |
| R-DR-7 | Checkpoint rollback undetectable in Development/Testing (CKR-3) | Accepted Development/Testing limitation |
| R-DR-8 | Performance at mature-organization scale **[Inc]** | AC-DR-11 |
| R-DR-9 | Stream contiguity rests on source inspection (E-9), not test | To be tested in WP-7 (gap-free generation check) |
| R-DR-10 | CF-1: in P1, authorization/content archive entries linking to a forged predecessor are detected only after the fact | Accepted interim (OD-DV-2) |
| R-DR-11 | Partial resets of Development stacks raise genuine findings (RST-2) | Operational; triaged, not suppressed (RST-2) |

---

## 19. Open items (recommended dispositions; none is decided here)

| ID | Open issue | Recommended disposition | Needs owner decision? |
|---|---|---|---|
| OI-DR-1 | Exact recovery behaviour that must be "demonstrated" before the P2 gate (G14 itself, or a narrower approved procedure for a stopped stream) | Define it in a separate P2 readiness record; do not start P2 work before | Yes, at the P2 gate |
| OI-DR-2 | Dedicated read-only verifier login in Development/Testing is a G08 privilege change | Authorize it together with WP-4; if not authorized, run under the runtime credential with a statement-trace proof and record the reduced separation | Yes, with WP-4 |
| OI-DR-3 | Where Development/Testing findings and checkpoints are surfaced to operators (alerts only, or also a read-only diagnostic) | Alerts plus the create-only objects; no new UI or API in D-R | Yes, if a UI/API is wanted |
| OI-DR-4 | Retention of checkpoints, findings and baseline reports | Follow the archive's retention and legal-hold policy (PB-10) | Yes, before production |
| OI-DR-5 | Owner disposition procedure for baseline exceptions (one decision per class or per organization) | One decision per exception class, listing the affected organizations and ranges | Yes, before EXCEPTION_ACCEPTED is used |
| OI-DR-6 | Synthetic dataset size for AC-DR-11 | Reuse the E3-M2f-3G 2M-row synthetic organization pattern | No (test design), unless changed |
| OI-DR-7 | Whether full sweeps also re-read every archived object periodically (archive-side re-verification against J4 in Development) | Optional; default off in Development; production relies on immutability | Yes, if enabled |
| OI-DR-8 | Production enumeration paging and listing consistency on Azure | Verify in WP-8 before any production claim | No (verification task) |

---

## 20. Not claimed

- No implementation, test execution, measurement or deployment of D-R, D+, the verifier, the baseline, the checkpoint store, M-6 or any port change.
- No change to M2F3-43, PD-15, any E3 contract requirement, any existing quarantine trigger or any historical test outcome.
- No closure of SEC-BL-01, PD-15, R-FU-1, R-M2f3-6, OPEN-M2f-4, TS-M2f-01, G14, BD-1, BD-2, F-E2, OD-IX-2 or any production gate.
- No production, Azure, persistent-database or staging evidence; no independence of any Development/Testing store; no checkpoint freshness assurance in Development/Testing.
- No independent security review or certification.

---

## 21. Amendment A-1 (dated 2026-11-17): WP-4A stabilization

**Authority:** owner decision package "WP-4A Stabilization", 2026-11-17, dispositions A–F (recorded with their implementation status in `SEC_BL_01_WP4A_IMPLEMENTATION_RECORD.md` §2). No new decision-log number was assigned. **Scope:** Development/Testing behaviour of the WP-3 baseline and the WP-4 verifier. A-1 amends this contract only where it says so below. §1–§20 are preserved unchanged as the historical record; where they conflict with A-1, **A-1 governs**. A-1 does not amend M2F3-43, PD-15, any E3 requirement, D+ (§5), M-6 (§11), WP-7, WP-8 or G14, and it relaxes no check.

### 21.1 Accepted-exception records (amends BL-4 and BL-7; adds to §7 and §9)

| # | Rule |
|---|---|
| AX-1 | **Record.** Schema `DynamicForms/JournalAcceptedException/v1`, a create-only object at `exceptions/{organization}/{stream}/{exception_id}.json` in the checkpoint store (same separately keyed store and refusal rules as §7.4). Fields: stable exception identifier (1–64 of `A–Z a–z 0–9 . _ -`); exception class; organization; stream; first and last revision of the accepted range (at most 10,000 revisions); finding code and subtype (the exact accepted scope); evidence digest (AX-4); owner disposition reference; disposition timestamp (UTC); baseline run identifier and the checkpoint sequence of the baseline that accepted it; archive instance identifier; tool version; source commit; operator label; acceptance time (UTC); assurance classification, always `Reduced`. Canonical JSON with unknown members refused. Identical bytes on retry are idempotent; other bytes under the same identifier are a conflict, and nothing is overwritten |
| AX-2 | **Creation.** Only a baseline (first baseline or re-baseline, §21.2) creates records, and only for an **explicit owner disposition** whose organization, stream, finding code and subtype equal the finding's and whose range contains it. Each disposition must carry its identifier, owner reference and disposition timestamp, and the timestamp may not be later than the run. Duplicate identifiers or an oversized scope also count as malformed. A malformed disposition makes the baseline INCONCLUSIVE (`DISPOSITION_INVALID`), and nothing is written. **No finding is ever accepted without a disposition**; candidate classes (for example `LEGACY_UNVERIFIABLE`) remain candidates only |
| AX-3 | **Order.** Records are written before the checkpoint that names them. A conflict on any record ⇒ no checkpoint (`EXCEPTION_RECORD_CONFLICT`). A checkpoint never names an identifier whose record was not stored |
| AX-4 | **Evidence digest.** SHA-256 over the canonical document `DynamicForms/JournalExceptionEvidence/v1`. For every revision of the accepted range it holds: whether the AuthorityDb row exists, its stored digest, the SHA-256 of its body bytes, the archive read status, and the SHA-256 of the archived bytes. Any added, removed or changed row or archive object in the range changes the digest. Event bodies are never stored in the record |
| AX-5 | **Re-verification.** Every incremental sweep, every full sweep and every re-baseline loads each record the current checkpoint names. It verifies: canonical form; same identifier, organization and stream; **provenance** (the archive instance equals the chain's; the baseline sequence is at or below the head); and the **evidence digest, recomputed now**. If any of these fails, the result is `ACCEPTED_EXCEPTION_RECORD_INVALID` (missing, malformed, foreign, inconsistent provenance) or `ACCEPTED_EXCEPTION_EVIDENCE_CHANGED`. Both are security findings, and the exception is then **not** applied. An unreadable store makes the sweep INCONCLUSIVE (CK-7) |
| AX-6 | **Classification.** A finding is "previously accepted" only when a re-verified record covers it **exactly**: same code and subtype, and revision range inside the accepted range. It is reported as an accepted observation (exception identifier, code, subtype, range), not as a finding. Any other finding stays a finding, including a different code in the same range, a new range or changed evidence (AX-5) |
| AX-7 | **Advancing past an accepted gap.** A verified record with code `ARCHIVE_DISCONTINUITY` lets the archive's contiguous head cross **that exact gap**, and nothing else. After the gap, the archive-side chain continues from the AuthorityDb row's stored digest at the gap, which the evidence digest pins. Without such a record, a gap stops the contiguous head as before |
| AX-8 | **Assurance.** A chain with an accepted exception stays `Reduced` and names every accepted identifier in every later checkpoint (CK-3). Nothing upgrades it to `Full`; there is no path from EXCEPTION_ACCEPTED to CLEAN on that chain |
| AX-9 | **Limitation (mandatory statement).** In Development/Testing the checkpoint store, including these records, is **not independently protected against a sufficiently privileged administrator** (J6, and anyone holding its Azurite key). That administrator can delete, replace or forge records, checkpoints and findings consistently. A record proves only what that administrator left in place, and CKR-3 applies (§7.3). Production protection requires §14 (immutable store and an independently anchored head) and is not claimed |
| AX-10 | **New finding codes (security):** `ACCEPTED_EXCEPTION_RECORD_INVALID`, `ACCEPTED_EXCEPTION_EVIDENCE_CHANGED`. **New inconclusive reason:** `DISPOSITION_INVALID` |

### 21.2 Re-baseline continuity, OBS-4 (replaces BL-10 and the re-baseline clause of CK-10)

| # | Rule |
|---|---|
| RB-1 | A re-baseline **continues the existing checkpoint chain**. It runs the complete baseline comparison of §10 (BL-1, BL-2) and writes checkpoint `head + 1` with walk kind `Baseline`, linked to the digest of the head. It never restarts the sequence, never overwrites a checkpoint, never deletes or rewrites a finding or record, and never moves the verified revision backward |
| RB-2 | It runs only on explicit request (`--rebaseline`, together with `--create-checkpoint`). Otherwise a baseline over an existing chain writes nothing and only proposes the continuation (`REBASELINE_NOT_REQUESTED`) |
| RB-3 | It carries forward **every** accepted exception of the chain, re-verified under AX-5, and may add newly accepted ones (AX-2). Identifiers are only ever added and assurance is never upgraded: a `Reduced` chain stays `Reduced`. The checkpoint-chain rules enforce this for every reader. A `Baseline` successor keeps the archive instance, contains every predecessor identifier, and is not `Full` after `Reduced` |
| RB-4 | **Rejected (nothing written):** another archive instance (`REBASELINE_ARCHIVE_INSTANCE_CHANGED`; a replaced or restored archive is recovery, G14, not a re-baseline); a contiguous archive revision below the head's verified revision (`REBASELINE_REVISION_REGRESSION`); another checkpoint already stored at `head + 1` (`REBASELINE_CONFLICT`); any undispositioned security finding, including a carried record failing AX-5 (outcome FAILED); any INCONCLUSIVE reason |
| RB-5 | The report (`DynamicForms/JournalBaselineReport/v2`) states that the run is a re-baseline. It also names the previous checkpoint (sequence, revision, digest), the carried exception identifiers and the records written. BL-9 applies unchanged |
| RB-6 | A checkpoint chain that is invalid, regressed or lost (CK-8..CK-10) **cannot be continued**: discovery fails and a re-baseline writes nothing. How verification is re-established after such an event remains an open owner decision (OI-DR-9). A baseline over an empty store creates checkpoint 1; in Development/Testing a wiped store cannot be told apart from a new one (CKR-3) |

### 21.3 Bounded incremental verification (amends VF-4, VF-5 and §8.6; VF-12/VF-13 unchanged)

| # | Rule |
|---|---|
| IV-1 | The incremental sweep lists **only the archive range above the trusted revision `v`**. For the 12-digit revision space this is the union of at most 109 decimal prefixes (`v + 1` itself, then every greater digit at each position after the shared prefix). Each prefix is listed completely (every page, AE-1/AE-2), and a failed or bounded-out listing is unavailability ⇒ INCONCLUSIVE. Its cost is independent of history below `v` |
| IV-2 | VF-3 is unchanged: on every incremental sweep the archive object at `v` must hash to the checkpoint digest (`ARCHIVE_REGRESSION`), and AuthorityDb row `v` must equal it (`ANCHOR_MISMATCH`) |
| IV-3 | Within the range above `v`: gaps (`ARCHIVE_DISCONTINUITY`), duplicates, objects above the AuthorityDb head (`BEHIND_ARCHIVE`), and non-canonical names inside the listed prefixes (`ARCHIVE_UNEXPECTED_OBJECT`) are findings, as before |
| IV-4 | VF-5..VF-8 are unchanged for the newly verified range: byte comparison of rows with archived objects, both chains, and fake receipts |
| IV-5 | **Detected only by the full sweep** (VF-12/VF-13; every `T_full`, 24 h by default; complete enumeration from revision 1; full checkpoint-chain verification): archive deletions, insertions or replacements **below `v`** other than at `v` itself; unexpected objects outside the listed prefixes; and non-re-chained AuthorityDb edits below `v` |
| IV-6 | **Latency trade-off (mandatory statement).** Newly delivered rows and anomalies above `v`: within `T_inc` plus the incremental duration. Anomalies at `v`: the same. **Changes below `v`: up to `T_full` plus the full-sweep duration**, with the default 24 h. An incremental sweep is **never** a full-history verification. Its run carries `BoundedListing = true` |
| IV-7 | **Last complete verification** is tracked separately from incremental progress: the time of the newest `Baseline` or `Full` checkpoint this process has seen, or of its last Clean full sweep. Null means none is known in this process; for example, after a restart whose head is `Incremental`. Incremental progress never updates it |
| IV-8 | Memory: the incremental sweep holds only the names above `v` and the rows of one batch. The full sweep holds the complete stream listing as in WP-4 (proportional to history; unchanged by A-1). Accepted-exception evidence is bounded to 10,000 revisions per record |
| IV-9 | Fail-closed and INCONCLUSIVE semantics are unchanged (VF-11, CK-1). A Development/Testing option (`BoundedIncrementalListing`, default `true`) restores the WP-4 full listing **for measurement only** |

### 21.4 OBS-1 archive error classification (delivery adapter; D+ unaffected)

Blob `AppendAsync` and `ReadAsync` now classify an `AggregateException` from exhausted SDK retries as `ArchiveUnavailableException` (archive unavailable), unless the caller cancelled. `AppendAsync`'s read-back after a 409/412 also classifies its timeout and `AggregateException` this way. Nothing else changes: entry names, conditions, metadata, receipts and digests are the same; nothing is retried, re-ordered or quarantined in the adapter; `ArchiveProcessor` keeps the entry `Pending` and retries it on the next pass. This supersedes the "delivery code unchanged" statement of the WP-3/WP-4 record for these two methods only.

### 21.5 Checkpoint growth (no deletion, compaction or retention change)

The proposal is recorded in `SEC_BL_01_WP4A_IMPLEMENTATION_RECORD.md` §7 for a future owner decision (OI-DR-4, OI-DR-10). No checkpoint, record or finding is ever deleted or compacted under A-1.

### 21.6 Identifiers and open items added by A-1

AX-1..AX-10, RB-1..RB-6, IV-1..IV-9; finding codes `ACCEPTED_EXCEPTION_RECORD_INVALID`, `ACCEPTED_EXCEPTION_EVIDENCE_CHANGED`; inconclusive reason `DISPOSITION_INVALID`; baseline actions `REBASELINE_NOT_REQUESTED`, `REBASELINE_ARCHIVE_INSTANCE_CHANGED`, `REBASELINE_REVISION_REGRESSION`, `REBASELINE_CONFLICT`, `EXCEPTION_RECORD_CONFLICT` (`REBASELINE_NOT_AUTHORIZED` of WP-3 is retired).

| ID | Open issue | Recommended disposition | Needs owner decision? |
|---|---|---|---|
| OI-DR-9 | Re-establishing verification after an invalid, regressed or lost checkpoint chain (RB-6), which cannot be continued | Treat it as a recovery event under G14, with an owner-approved new chain whose report references the lost one; do not automate it | Yes |
| OI-DR-10 | Checkpoint frequency and retention (§21.5) | See the WP-4A record §7 proposal | Yes, before production |
| OI-DR-11 | Whether incremental sweeps should also sample below `v` to shorten the IV-6 latency | Not in Development/Testing; reconsider in WP-7 with measurements | Yes, if wanted |

### 21.7 Not claimed by A-1

No production protection of accepted-exception records or checkpoints; no checkpoint freshness in Development/Testing (CKR-3); no full-history guarantee from an incremental sweep; no closure or re-evaluation of SEC-BL-01, PD-15 or M2F3-43; no D+, M-6, WP-7, WP-8 or G14 work; no independent review.

---

## 22. Amendment A-2 (dated 2026-11-17): WP-5a, audit-stream D+

**Authority:** owner authorization package "WP-5a: Audit-Stream D+ Delivery Enforcement", 2026-11-17. It accepts WP-4A and A-1, adopts the dispositions of OI-DR-9, OI-DR-10, OI-DR-11 and OI-DR-2 (§22.5), and authorizes WP-5a in Development/Testing. The sentinel provisioning rule (§22.2) is the owner's answer of the same day to the question raised during WP-5a ("empty archive only"). No new decision-log number was assigned. §1–§21 are preserved unchanged. Where they conflict with A-2, **A-2 governs**. A-2 does not enable D+ on the authorization or content streams, implements no G14 recovery and relaxes no check.

### 22.1 Phase P1 enabled (§5.6)

D+ (DP-1..DP-14) applies to the **audit** stream in `ArchiveProcessor`, fixed in code with no configuration (§5.7). The rules are WP-1's `JournalChainRules` (R-1..R-7), and no chain logic is duplicated. Authorization and content delivery are unchanged, so CF-1 remains open for them until the separate P2 owner gate (§5.6), which also requires demonstrated recovery behaviour (OI-DR-1).

### 22.2 Sentinel provisioning (amends AS-1 and DP-3)

| # | Rule |
|---|---|
| SP-1 | The sentinel is created at baseline (BL-1, unchanged) **or**, in Development and Testing only, by the Authority host at start-up, before the work loop delivers anything. The host creates it create-only, and only when a complete listing of the whole archive finds no object other than platform-journal entries (`Guid.Empty`, stream `platform-operators`), or when the container does not exist |
| SP-2 | Platform-journal entries are excluded because the platform journal delivers synchronously at command time and D+ does not govern it; AE-3 already excludes that scope by exact match. A strict "no object at all" test would race with it on a fresh stack |
| SP-3 | A non-empty archive without a sentinel is **never stamped**; a baseline (`--journal-baseline --create-sentinel`) is required. The delivery path never creates a sentinel. Staging and production create it only through the baseline |
| SP-4 | **Residual (accepted with the decision):** an archive wiped down to its platform entries and restarted gets a new sentinel. A wipe is then reported by D+ as `PREDECESSOR_MISSING` for every stream with history, instead of `ARCHIVE_REGRESSION`, and by the verifier as `ARCHIVE_REGRESSION` (AS-3, a different instance) |

### 22.3 Interpretations made precise (DP-5, DP-12, §5.3, §5.5)

| # | Rule |
|---|---|
| DD-1 | **Sentinel absent or not canonical (DP-5):** audit delivery is refused with `ARCHIVE_REGRESSION` (`SENTINEL_ABSENT` / `SENTINEL_INVALID`) and a finding and Critical alert are produced. The entry is **not** marked `VerificationFailed`: it is an archive-level condition, re-derived on every pass, so delivery resumes once a baseline provides the sentinel. The same applies to a missing container after the sentinel was read (`CONTAINER_ABSENT`). D+ confirms the sentinel on every pass that has a pending audit entry, not only before interpreting an absence. This is stricter than DP-5 and costs at most one extra read per pass |
| DD-2 | **Marking (§5.3):** a refusal about the entry's own chain is marked `VerificationFailed`, with `last_archive_error_code = DPLUS_{code}[:{subtype}]`. These refusals are: the row's R-2..R-5; the archived predecessor's R-2/R-3/R-5; `PREDECESSOR_MISSING`; `LINK_MISMATCH`. The existing R-1 check of the row and its quarantine response come first and are unchanged (DP-10) |
| DD-3 | **Held rows (§5.3, DP-13):** an entry whose immediate predecessor row is already `VerificationFailed` and whose predecessor is absent from the archive stays `Pending` with code `DPLUS_STREAM_STOPPED`. It adds no new finding; this is not a cascade. If the predecessor row is reset to Pending, D+ refuses it again. If it is falsely marked `Anchored`, the next entry is refused as `PREDECESSOR_MISSING`. Both follow from the archive (DP-9) |
| DD-4 | **Finding before state (DP-12):** when a finding store is composed (the Development/Testing verification composition; production WP-8), the create-only finding object is written first. If it cannot be stored, the row state is not changed and the refusal repeats on the next pass. When no store is composed, the Critical alert and the structured log are the only finding record. **This is reduced DP-12 assurance:** there is no tamper-resistant finding object, but the stop still re-derives from the archive |
| DD-5 | **Stream isolation:** the audit stream is fetched in its own batch, and the other streams in a separate batch. A stopped audit stream keeps its rows Pending, and must not consume the batch that authorization and content delivery use. A D+ refusal ends only the audit stream for the pass. Archive unavailability, a body mismatch and an archive conflict end the whole pass, as before (DP-7, DP-10). The two batches let more non-audit rows through per pass when audit rows are pending; ordering and failure handling of those streams are unchanged |
| DD-6 | **Cost (§5.5):** at most two archive reads per pass for the audit stream (sentinel, and the predecessor of the first pending entry), plus the appends. Not measured at scale (AC-DR-11, WP-7) |
| DD-7 | No quarantine (DP-11); no repair, re-delivery, re-signing or skipping (§5.3); no change to T2, the recorder or any request path (DP-14) |

### 22.4 Finding codes

`PREDECESSOR_MISSING` and `LINK_MISMATCH` (§9.1, D+) are emitted as top-level codes. `CHAIN_INTEGRITY_FAILURE` carries the WP-1 subtypes, including `ENVELOPE_MALFORMED` and `ENVELOPE_UNSUPPORTED` for an archived predecessor or a row whose envelope cannot be read. `ARCHIVE_REGRESSION` carries the subtypes `SENTINEL_ABSENT`, `SENTINEL_INVALID` and `CONTAINER_ABSENT`. Alert kind: `JournalDeliveryRefused`, Critical, de-duplicated per process by finding identity.

### 22.5 Owner dispositions adopted with the WP-5a package

| ID | Disposition | Status |
|---|---|---|
| OI-DR-9 | Re-establishing verification after an invalid, regressed or lost checkpoint chain is a G14 recovery event: an owner-approved new chain whose report references the lost one, never automated | **Adopted**; not implemented (G14 not authorized). RB-6 stands |
| OI-DR-10 | Checkpoint frequency and discovery rules: a checkpoint when the verified revision advances; at most one unchanged-revision checkpoint per hour unless assurance-relevant state changes; append-only; never delete, overwrite or compact; sequence and digest continuity kept; bounded head discovery with stated CKR-3 limits; never trust an unverified latest checkpoint | **Adopted; implementation deferred** as a separate follow-up. It is materially independent of D+ (WP-5a package §5), so WP-5a does not expand into it |
| OI-DR-11 | No sampling below the checkpoint in Development/Testing; reconsider in WP-7 with measurements | **Adopted**; nothing to implement |
| OI-DR-2 | No dedicated read-only verifier login is created (a G08 change, not authorized). The verifier keeps running under the Authority runtime credential, with the server-enforced read-only transaction and the statement-trace proof; the reduced separation is recorded (R-W4-1) | **Adopted**; unchanged |

### 22.6 Identifiers added by A-2

SP-1..SP-4, DD-1..DD-7. Row codes `DPLUS_{code}[:{subtype}]`, `DPLUS_STREAM_STOPPED`, `DPLUS_ARCHIVE_INVENTORY_UNAVAILABLE`, and the suffix ` (finding not recorded)`. Log events 9410–9412 (sentinel bootstrap).

### 22.7 Not claimed by A-2

No D+ on authorization or content; no closure of CF-1, SEC-BL-01, PD-15 or M2F3-43; no production or staging enablement; no tamper-resistant D+ finding object without a composed store; no G14 recovery; no independent review.

### 22.8 Correction to A-2 (dated 2026-11-17, WP-5a.1)

**Authority:** the owner decision package "WP-5a.1: Sentinel Provisioning and Durable D+ Findings" (2026-11-17), dispositions 1–6. WP-5a was conditionally accepted pending correction of two deviations from this contract. §22.1–§22.7 are preserved as approved and executed; where they conflict with §22.8, **§22.8 governs**. SP-1..SP-4 and DD-4 are superseded.

**Root causes.**
1. **SP-1/SP-4.** Automatic sentinel creation treated "no objects other than platform-journal entries" as proof of a fresh archive. That is absence of evidence: a fully wiped archive looks the same.
2. **DD-4.** DP-12 was made conditional on a finding store being composed. That store existed only in the opt-in verifier overlay. Without it a confirmed refusal was marked `VerificationFailed` with only an alert and a log line, so logging stood in for a durable finding.

**Provisioning lifecycle (replaces SP-1..SP-4).**

| # | Rule |
|---|---|
| PV-1 | No host creates an archive sentinel automatically. The WP-5a start-up bootstrap is removed. Sentinels come only from the baseline (§10) or from PV-2 |
| PV-2 | `--journal-archive-provision` is an explicit, create-only provisioning step. It is accepted only in Development/Testing and only when the stack declares itself disposable (`JournalArchiveProvisioning:DisposableStack=true`); that is set only by the security-test and integration overlays, where the step runs before the Authority starts. A persistent development archive or a production archive is never initialized by it |
| PV-3 | **New-instance evidence (all required; none suffices alone):** no provisioning record in the separately configured journal-verification store; no archive sentinel; the archive container has never been created (complete listing); AuthorityDb holds no security-journal or platform-journal row; the journal-verification store holds no object at all |
| PV-4 | **Order and identity:** the record (`DynamicForms/ArchiveProvisioning/v1`, `provisioning/archive-provisioning.json`, create-only) is written first. The sentinel then carries the record's instance identifier. The record lives outside the archive, so deleting archive objects or the whole container cannot recreate or remove it |
| PV-5 | **Refusals (nothing written):** `ARCHIVE_SENTINEL_WITHOUT_RECORD` (record lost, or the archive was established by a baseline); `ARCHIVE_CONTAINER_EXISTS` (an established, partially or fully wiped archive, including one holding platform entries only); `JOURNAL_ACTIVITY_PRESENT`; `VERIFICATION_STORE_NOT_FRESH`; `ARCHIVE_LOST_AFTER_PROVISIONING` (a record exists and the sentinel is gone while the container or journal rows exist); `ARCHIVE_IDENTITY_MISMATCH`; `SENTINEL_INVALID`; `PROVISIONING_RECORD_INVALID` |
| PV-6 | **Ambiguity fails closed:** a record without a sentinel while the archive was never created and no journal row exists could be an interrupted provisioning, a concurrent provisioner still working, or the loss of an archive that held only its sentinel. It is refused as `PROVISIONING_INCOMPLETE` and needs an operator decision. Only the run that has just written the record creates the sentinel |
| PV-7 | **Concurrency:** both writes are create-only. A concurrent loser converges on the winner's record and succeeds only once the winner's sentinel exists; otherwise it fails closed. A run on a provisioned stack returns `AlreadyProvisioned` (record and sentinel agree), so restarting a disposable stack is idempotent |
| PV-8 | Unavailability or an incomplete listing of the archive, the store or AuthorityDb is `Unavailable` (exit 5), never "empty" |
| PV-9 | **Trust boundary:** the evidence is configuration-separated, not independent. An actor who controls both Azurite instances and AuthorityDb, and removes the archive container, the record, every store object and every journal row, leaves nothing to compare, and the stack is indistinguishable from a new one. That leaves no history to protect, and it is out of scope for Development/Testing |

**Durable findings (replaces DD-4).**

| # | Rule |
|---|---|
| DD-8 | The journal-verification store is **required** whenever D+ can refuse. A Development/Testing Authority that delivers to an archive refuses to start without it: a Blob archive requires the Blob store (durable, same lifetime, RST-1), while the Development-only in-memory archive may pair with the in-memory store. This is checked at registration and again at host start, before the work loop. Every compose stack runs the store, with a key generated per stack into its own volume; there is no `.env` value and no public default credential. An unreadable key file is a startup failure. Production hosting is unchanged (WP-8) |
| DD-9 | **Finding before state:** a confirmed refusal raises the Critical alert. Its finding (deterministic identity: organization, stream, code, subtype, revision; create-only) must then be confirmed stored **before** the entry is marked `VerificationFailed`. Without a store, or while it is unavailable, the entry stays `Pending` with `DPLUS_FINDING_STORE_UNAVAILABLE` and a separate Critical alert (`JournalDeliveryFindingStoreUnavailable`) is raised. The refusal is then **held**: D+ is not re-evaluated, nothing of that audit stream is delivered (even if the archive evidence changes), and each pass retries the original finding. Once it is stored, the original classification is applied to the original revision. Logging and alerting are never a substitute |
| DD-10 | **Limitation (owner decision required, OI-DR-12):** the hold of DD-9 lasts for the process lifetime only. After a restart, an unrecorded refusal is re-derived from the archive. If the evidence still fails, it is refused again; if the evidence was replaced in the meantime, delivery resumes. A durable stop marker would close this, but it is a new state-machine or storage contract, so per the package it is proposed, not implemented (WP-5a.1 record §5). A characterization test pins the current behaviour |
| DD-11 | DD-1 is unchanged: a sentinel-absent refusal is recorded like any other finding and is not marked, and delivery resumes once a baseline provides the sentinel. That resumption follows an operator action that restores the archive identity; it is not a replaced item of integrity evidence |

**Existing data.** Metadata inspection on 2026-11-17 established the following. The persistent development stack (`enterpriseforms-dev`) last ran 12 days earlier, with an Authority image built on 2026-11-01, before any SEC-BL-01 code. The WP-5a code ran only in the disposable `enterpriseforms-sectest` project, whose volumes were removed. No persistent organization data was affected; no migration or repair is required (disposition 5). No persistent data was read or changed.

**Identifiers added by §22.8:** PV-1..PV-9, DD-8..DD-11; provisioning reasons as in PV-5/PV-6; row code `DPLUS_FINDING_STORE_UNAVAILABLE`; alert kind `JournalDeliveryFindingStoreUnavailable`; open item OI-DR-12 (durable stop marker). Log events 9410–9412 of §22.6 are retired with the bootstrap.

**Not claimed by §22.8:** no durable hold across restarts (DD-10); no independence of the provisioning evidence (PV-9); no production finding store; no closure of SEC-BL-01, PD-15 or M2F3-43.

---

## 23. Amendment A-3 (dated 2026-11-18): durable audit-stream stop (OI-DR-12, design M2)

**Authority:** owner decision package "WP-5a.2: Durable Audit Stream Stop — OI-DR-12" (2026-11-18). It accepts the WP-5a.1 corrections for Development/Testing, selects M2, and excludes an AuthorityDb `Held` state, any state-machine change, G14 and P2. The owner chose disposition **W1 (accept and narrow)** on 2026-11-18, after the implementer reported that the package's invariant cannot be guaranteed across the detection-to-persistence window. The design text was drafted in `SEC_BL_01_A3_DURABLE_STOP_DRAFT.md` and is preserved there. §1–§22 are unchanged; where they conflict with A-3, **A-3 governs**. A-3 supersedes the process-lifetime-only hold of §22.8 DD-9/DD-10, outside the residual window of §23.3.

### 23.1 Rules

| # | Rule |
|---|---|
| SM-1 | **Marker key.** `meta/dplus-stops/{organization:D}/{stream}/{revision:D12}.json`, create-only, in the security-event archive. It sits under the reserved `meta/` prefix, outside every `org/` entry prefix, so it never collides with a journal revision |
| SM-2 | **Content** (`DynamicForms/DPlusStreamStop/v1`, canonical JSON, unknown members refused): organization; stream; failed revision; finding code and subtype; the matching finding's deterministic identity; the journal entry identifier; the archive instance identifier; detection time (UTC); tool version. No body or digest of a body. Only stream-stopping classifications are valid: `PREDECESSOR_MISSING`, `LINK_MISMATCH`, `CHAIN_INTEGRITY_FAILURE/*` |
| SM-3 | **Writes.** The key is deterministic. Identical bytes on retry are idempotent; different bytes are a conflict (`DPLUS_STOP_MARKER_CONFLICT`, security) and are never overwritten. D+ never deletes, rewrites or supersedes a marker and never repairs archived evidence |
| SM-4 | **On a confirmed stream-stopping refusal:** Critical alert, then marker, then finding; the finding references the marker when the marker was confirmed first. The row is marked `VerificationFailed` only when **both** are confirmed. **One confirmed record establishes the durable stop.** Row codes: `DPLUS_FINDING_STORE_UNAVAILABLE` (marker only), `DPLUS_STOP_MARKER_UNAVAILABLE` (finding only), `DPLUS_DURABLE_STOP_NOT_ESTABLISHED` (neither; the refusal is then held in memory, retrying both records each pass). Archive-level refusals (`ARCHIVE_REGRESSION/*`) write no marker, and DD-1/DD-11 are unchanged |
| SM-5 | **Discovery on every pass**, after the first entry's existing R-1 check and before D+ evaluates anything. It lists the stream's markers completely (archive) and the stream's D+ refusal findings (journal-verification store; read operation added). A valid marker, a stream-stopping D+ finding, or an invalid marker object each stops the stream; AuthorityDb delivery state never lifts it. If the marker listing fails, that is archive unavailability: the pass ends and the entry stays Pending (DP-7). If the finding listing fails, the result is inconclusive: **the audit stream waits** (`DPLUS_FINDING_STORE_UNAVAILABLE`), other streams continue, and no finding is fabricated |
| SM-6 | **Cross-check and completion (create-only, never clearing).** A marker without its finding: the finding is recorded from the marker (same identity), as operational completion, because a store outage and J6 deletion cannot be told apart. A finding without its marker: the marker is created from the finding. If the finding records that its marker had been stored, the marker was removed (J4/J5), which is `DPLUS_STOP_RECORD_INCOMPLETE` (security). An invalid, non-canonical or foreign marker object under the stream's prefix is `DPLUS_STOP_MARKER_INVALID` (security) and keeps the stream stopped. Once both records are durable, the failed revision's row is marked from the recorded classification |
| SM-7 | **Verifier.** For the audit stream, the full sweep and the baseline enumerate the whole `meta/` prefix completely. They recognize `meta/archive-identity.json` and canonical stop markers, validate this organization's markers (`DPLUS_STOP_MARKER_INVALID`), and report every other `meta/` object as `ARCHIVE_UNEXPECTED_OBJECT`. Nothing is silently ignored. Per-organization entry enumeration (AE-1..AE-3) is unchanged |
| SM-8 | **Recovery.** Resuming a stopped stream requires an authorized recovery action (G14 or an owner-approved procedure). It is neither defined nor implemented by A-3 |
| SM-9 | Authorization and content delivery are unchanged (CF-1 open). No quarantine (DP-11) |

### 23.2 Durability invariant and its proven scope

Once **either** record of a confirmed refusal is durable, delivery of that audit stream does not resume when any of the following happens:
- the process restarts;
- the finding store recovers;
- AuthorityDb delivery state is reset or forged;
- the archive evidence is replaced;
- a retry observes valid-looking evidence.

The stream stays stopped until an authorized recovery action. This is demonstrated by durable evidence in tests, in memory and on PostgreSQL 18 + Azurite (WP-5a.2 record §5).

### 23.3 Accepted residual window (owner disposition W1)

The invariant is **not** guaranteed when all of the following happen in order:
1. D+ detects a stream-stopping refusal.
2. Before either record is confirmed, the process terminates, or both stores reject the writes and the process then terminates, or shutdown cancels the writes.
3. The evidence is replaced before the next start.

In that sequence, the restarted process re-derives D+ from the replaced evidence and delivers. This is inherent to any design that writes after detecting. It is pinned by the test `AcceptedResidualTerminationBeforeAnyDurableRecordThenEvidenceReplacementResumesDelivery`. **DD-10 is narrowed, not closed.** Closing it needs either W2 (fail closed on unclean termination; requires recovery, G14) or production immutability with an independent anchor (WP-8).

### 23.4 Threat-model limits

- J4/J5 removing a marker are detected through the finding (SM-6), and J6 removing a finding through the marker. An actor who removes **both** (J4/J5 and J6) leaves no record, and delivery resumes; this is pinned by `KnownLimitRemovingBothTheMarkerAndTheFindingLeavesNoRecord`.
- Markers live in the same archive as the evidence: they are not independent of J5.
- Development/Testing stores are configuration-separated only (PV-9).

### 23.5 Identifiers

SM-1..SM-9; finding codes `DPLUS_STOP_MARKER_INVALID`, `DPLUS_STOP_RECORD_INCOMPLETE`, `DPLUS_STOP_MARKER_CONFLICT`; row codes `DPLUS_STOP_MARKER_UNAVAILABLE`, `DPLUS_DURABLE_STOP_NOT_ESTABLISHED`; alert kinds `JournalDeliveryStopMarkerUnavailable`, `JournalDeliveryDurableStopNotEstablished`, `JournalDeliveryStopRecord`.

### 23.6 Not claimed

No closure of DD-10 (§23.3); no recovery or marker clearing; no independence of markers from the archive administrator; no change to authorization/content delivery; no production enablement; no closure of SEC-BL-01, PD-15 or M2F3-43.

---

## 24. Amendment A-4 (dated 2026-11-18): G14-R2, controlled audit-stream recovery

**Authority:** owner package "G14-R2: Controlled Audit-Stream Recovery Implementation" (2026-11-18). It accepts G14-R1 as design work and authorizes, in Development/Testing only:
- recovery infrastructure;
- strategies SR-A and SR-C1;
- archive-instance pinning;
- the W2-S evidence pre-commitment.

It excludes SR-B, SR-D, SR-E, authorization/content D+, broader G14 recovery and any persistent environment. No decision-log number was assigned.

**Basis.** The G14-R1 drafts (`G14_R1_*`, design, not authoritative) are the basis. Only the provisions in §24.1–§24.9 are authoritative.
- §24.10 lists the implemented **provisional defaults** for decisions the owner has not reviewed. These are not authoritative and stay open in §24.11.
- §1–§23 are preserved unchanged.
- Where they conflict with §24 for the audit stream, **§24 governs**: §5 DP-9, §22.3 DD-1, §22.8 DD-11 and §23.1 SM-1, SM-3, SM-5, SM-7 and SM-8. Nothing else is relaxed.

**Reconciliation with the owner dispositions:**

| Draft A-4 rule | Disposition (G14-R2) | Resolution |
|---|---|---|
| SR-EP-1: commitment without stop generation and without body digests | Bind organization, stream, revision, archive instance, evidence identity and digest, and the stop generation | Disposition governs: §24.7 binds all six. The verification store already holds entry digests in checkpoints, so this is no new class of data there |
| SR-EP-2: commitment confirmed in at least one store | Do not proceed unless the commitment is durable | Both stores are required (§24.7 EP-2). Otherwise one store administrator could delete the only copy |
| SR-SM-4: instance change lifted through SR-D | No recovery across instance replacement in this increment; SR-D not implemented | An instance-change stop is durable and **not recoverable** in this increment (§24.6, §24.8) |
| SR-SM-1: generation from `Completed` outcomes | (none) | **Correction of a draft defect:** outcomes are unsigned and never trusted. The generation derives from executed, signature-verified recoveries (§24.5) |
| SR-RC-2: separate approval, rejection and cancellation folders | (none) | One `decisions/` folder. Approve, reject and cancel are classified from the signed claims, so moving a file cannot change its meaning |
| SR-AU-5: cancellation by the requester's key | (none) | The requester has no key in Development/Testing, so cancellations come from configured approver keys only |
| SR-OD-08 (SR-B, DP-9 change) | SR-B not implemented | Not approved. DP-9 is unchanged except for the executed SR-A and SR-C1 rules of §24.5 |

### 24.1 Records and stores

| # | Rule |
|---|---|
| RR-1 | Recovery records are create-only and live in **both** stores:<br>- journal-verification store: `recovery/{organization:D}/{stream}/…`<br>- archive: `meta/dplus-recoveries/{organization:D}/{stream}/…`<br>Relative names are identical in both stores. |
| RR-2 | A record is usable when a copy exists in either store. Two different copies are `SR_RECORD_CONTRADICTORY` and the record is unusable. |
| RR-3 | Per recovery (`{recovery_id:D}`, UUIDv7):<br>- `investigation.json`, `evidence.json`, `statement.json`;<br>- `decisions/{jti:D}.jws`;<br>- `action.json` (SR-C1);<br>- `outcome.json`;<br>- archive only: `evidence/row.json` (the failed row's body) and `evidence/restore.json` (SR-C1 copy).<br>Per stream: `executions/{stop_set_sha256}.json`, one per stop set. |
| RR-4 | Records are canonical: CanonicalJson re-serialization equality, unknown members refused. Identical bytes on retry are idempotent; different bytes are a conflict. Nothing is deleted, overwritten or superseded, and no mutable recovery state exists. AuthorityDb holds none. |

### 24.2 Approvals and separation of duties

| # | Rule |
|---|---|
| AP-1 | Public keys come only from the protected deployment configuration section `JournalRecovery` (`Mode`, `Approvers[]`).<br>- The Authority holds no private key, so no runtime identity, record store or the recovery tool can approve.<br>- Mode `Unavailable` (the default) verifies nothing, so nothing is ever lifted.<br>- `SyntheticDevelopment` is accepted in Development/Testing only (startup refusal elsewhere) and is **not two-human evidence**. |
| AP-2 | A decision is a compact JWS (ES256, `kid` = approver id) whose claims are exactly:<br>- `iss`, `aud`, `sub = kid`, `jti`, `iat`, `nbf`, `exp`;<br>- `approval_kind` (`DPlusStreamRecovery` or `DPlusStreamRecoveryCancellation`);<br>- `decision` (`approve`, `reject` or `cancel`);<br>- `statement_sha256`, `recovery_id`, `organization_id`, `stream_name`, `failed_revision`, `stop_generation`;<br>- `justification_sha256`.<br>Any other claim is refused. |
| AP-3 | A statement is **authorized** when:<br>- two verified approvals come from distinct approver ids, neither equal to the requester label or the analyst label;<br>- no verified rejection or cancellation exists.<br>A rejection or cancellation is final. |
| AP-4 | **Historical key verification:** a key with `RetiredOnUtc` verifies only decisions issued at or before that instant. A key removed from the configuration verifies nothing, so its lifts disappear (fail closed). |
| AP-5 | **Freshness and window:**<br>- `exp − iat` is at most the configured bound.<br>- At execution, the window must be open (it is at most 24 hours long), and two distinct approvals must be unexpired.<br>- D+ does not re-check `exp` after execution; it re-verifies signatures and binding on every pass. |
| AP-6 | **Single use:** `jti` is the object key; a reused `jti` with other bytes conflicts. A decision stored under another identifier is not counted. |

### 24.3 Statement, evidence and eligibility

| # | Rule |
|---|---|
| ST-1 | The tool builds the statement from observed state. The requester supplies only:<br>- the strategy;<br>- the reason;<br>- the investigation (cause classification, containment attestation, analyst, narrative; the narrative itself stays outside and only its digest is bound);<br>- the window. |
| ST-2 | The statement binds the stop set: the complete set of **unresolved** stop records (markers, invalid marker objects, stream-stopping findings), by name and SHA-256, all at one revision and at the stream's current generation. It also binds:<br>- its digest;<br>- the strategy and eligibility class;<br>- the evidence digest;<br>- the expectation (§24.5);<br>- the investigation and reason digests;<br>- the window, tool version and source commit. |
| ST-3 | The evidence snapshot is deterministic (no time, no AuthorityDb delivery state, no checkpoint head sequence). It holds:<br>- row `k` identity and digests;<br>- predecessor identity, state and digest;<br>- pinned and sentinel instances, the sentinel digest and the provisioning-record digest;<br>- the W2-S commitment of `(k, g)`;<br>- the SR-C1 anchor;<br>- the restore digest;<br>- the stop-record integrity codes;<br>- the D+ verdict;<br>- the rule-set version. |
| ST-4 | **Refused (nothing written)** when:<br>- the approval mode is unavailable;<br>- the stream is not audit;<br>- the strategy is not SR-A or SR-C1;<br>- the organization is quarantined or unknown;<br>- no unresolved durable stop exists, or the stop set is not single-scope;<br>- the stop is an archive-instance stop, or the instance is unpinned;<br>- the failed row is missing or fails R-1;<br>- commitment copies are contradictory;<br>- stop-record integrity findings exist without an attested containment;<br>- the requester or analyst is an approver;<br>- a strategy precondition fails (§24.4).<br>Store unavailability is `Unavailable` and is never "absent". |

### 24.4 Strategies (operational: SR-A, SR-C1 only)

| # | Rule |
|---|---|
| SG-1 | **SR-A** (false alarm). Requires:<br>- a non-adversarial cause classification;<br>- D+ passes on the **unchanged current** evidence.<br>The expectation is the current evidence. |
| SG-2 | **SR-C1** (anchored restore). Requires:<br>- the stop is `PREDECESSOR_MISSING`;<br>- the predecessor is **absent** (nothing is ever overwritten);<br>- a full-chain-valid checkpoint chain whose head and anchor verify exactly `k − 1` in the pinned instance;<br>- the copy's SHA-256 equals the anchor's verified digest;<br>- D+ passes with the copy as predecessor.<br>The action is the create-only append of exactly the bound bytes. |
| SG-3 | SR-B, SR-B-UNDELIVERABLE, SR-C2, SR-D and SR-E are **non-operational**. A statement naming them is structurally invalid and never authorizes anything. |

### 24.5 Execution and the D+ lift

| # | Rule |
|---|---|
| EX-1 | **Executor order:**<br>1. Authorization: a terminal outcome, rejection or cancellation fails the recovery.<br>2. Window, then freshness.<br>3. The freshly re-observed stop set, then the evidence digest, must equal the statement's; a difference records outcome `Failed`.<br>4. SR-C1 only: the action record, then the create-only predecessor write. A conflict means `Failed` and nothing is overwritten.<br>5. The row hint.<br>6. **The execution record, last.**<br>A termination between steps lifts nothing; a re-run resumes idempotently. |
| EX-2 | The **row hint** sets row `k` from `VerificationFailed` to `Pending` with code `DPLUS_RECOVERY_AUTHORIZED:{recovery_id}`. It is a scheduling hint only and never trusted, and it is the only AuthorityDb write of recovery. |
| EX-3 | **Lift:** on every pass, discovery loads the recovery records of both stores and re-verifies every signature and binding. A stop record is **resolved** only when an executed recovery (authorized, plus an execution record for its stop set naming it) covers it exactly: same name, SHA-256, kind and generation.<br>Nothing else resolves a record: no AuthorityDb state, code or receipt, no archive presence of `k`, and no restart or store recovery. Resolved records are never completed, marked or cleared. |
| EX-4 | **Generation:** `g` is one plus the highest stop generation resolved by an executed recovery, or 0. New stops, findings and commitments are written at `g`. |
| EX-5 | **Expectation:** for the recovered revision, the committed evidence (instance, row digest, predecessor state and digest) must equal the statement's expectation. Otherwise `DPLUS_RECOVERY_EVIDENCE_MISMATCH` is a stream-stopping refusal at generation `g`. |
| EX-6 | **Hold (SR-DL-9):** while the latest lift's revision is not archived, no revision above it is evaluated (`DPLUS_STREAM_STOPPED`). |
| EX-7 | **Outcome:** `Completed` is recorded when the recovered revision is archived with the expected digest. `Failed` is recorded on a later-generation stop at it, or on a definitive executor failure. Outcomes are never consulted by D+. |
| EX-8 | **Isolation:** records under one (organization, stream) never affect another. |

### 24.6 Stop generations and archive-instance pinning (amends SM-1, SM-3, DD-1, DD-11 for the audit stream)

| # | Rule |
|---|---|
| GP-1 | **Stop generations:**<br>- generation 0 keeps the A-3 marker key and v1 bytes;<br>- generation `g ≥ 1` uses `{revision:D12}.r{g:D4}.json`, schema `DynamicForms/DPlusStreamStop/v2` (`stop_generation`), with the finding identity and record carrying `g`.<br>Any other name under the prefix remains `DPLUS_STOP_MARKER_INVALID`. |
| GP-2 | **Pinning:** D+ compares the sentinel's instance with the trusted expected instance, taken from the provisioning record or the stream's first checkpoint (never from the archive itself):<br>- **Equal:** proceed.<br>- **Different:** `ARCHIVE_REGRESSION/INSTANCE_CHANGED`.<br>- **Invalid or disagreeing anchors:** `ARCHIVE_REGRESSION/INSTANCE_ANCHOR_CONTRADICTORY`.<br>Both are stream-stopping and durable. **Neither is recoverable in this increment.**<br>- **No anchor:** `ARCHIVE_REGRESSION/INSTANCE_UNPINNED`, archive-level and re-derived; the stream does not proceed.<br>- **Anchors unreadable:** the stream waits. |
| GP-3 | DD-11 is amended for the audit stream: delivery resumes after a sentinel outage only once a canonical sentinel **naming the expected instance** exists. |

### 24.7 W2-S evidence pre-commitment (DD-10)

| # | Rule |
|---|---|
| EP-1 | **Location:** `intents/{organization:D}/{stream}/{revision:D12}[.r{g:D4}].json` (verification store) and `meta/dplus-intents/…` (archive), schema `DynamicForms/DPlusEvidenceCommitment/v1`.<br>**Content:**<br>- organization, stream, revision, stop generation;<br>- archive instance;<br>- row identity, body digest and previous digest;<br>- predecessor identity, state and digest;<br>- rule-set version.<br>No time, so retries are byte-identical. |
| EP-2 | **Order:** gather the evidence, then commit it create-only to **both** stores, then evaluate D+ on exactly the gathered evidence. If either store is unavailable, nothing is evaluated (`DPLUS_EVIDENCE_COMMITMENT_UNAVAILABLE`) and the audit stream waits. |
| EP-3 | **An existing commitment that differs** from the fresh evidence (after a restart, a replacement, or a concurrent writer's create-only conflict) is `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT`, stream-stopping. Two differing copies are its subtype `COMMITMENT_CONTRADICTORY`. Such stops are lifted only through §24.5. |
| EP-4 | **Exception (preserves DP-10):** a commitment differs because the row was altered after delivery, and the archive already holds that revision with exactly the committed row bytes. Then evaluation and the create-only append proceed, so the existing `ARCHIVE_BODY_MISMATCH` quarantine applies. |
| EP-5 | **Commitments are never deleted.** The verifier recognizes and validates them canonically. Comparing delivered commitments with archived entries (draft SR-EP-5) is **not implemented**. |
| EP-6 | **DD-10 status: not closed by A-4.** In Development/Testing, the §23.3 sequence (detection, termination before any stop record, then evidence replacement) is detected durably. Remaining:<br>- evidence altered before its first commitment (R-DR-1);<br>- removal of a commitment from both stores (with the stop records), the A-3 known-limit class;<br>- a compromised runtime (JP);<br>- configuration-separated stores only (PV-9).<br>Whether DD-10 is recorded as closed with these residuals is an owner decision (§24.11). |

### 24.8 Permanent blocks (this increment)

No §24 path:
- lifts an organization quarantine;
- acts on an authorization or content stream;
- recovers across an archive-instance change or creates a stream epoch;
- overwrites or deletes any object;
- proceeds without containment where required;
- runs outside Development/Testing;
- proceeds on unavailable or contradictory mandatory evidence;
- uses an anchor from an invalid checkpoint chain.

### 24.9 Command line and identifiers

The recovery tool is the Development/Testing host command `--journal-recovery propose|decide|execute|outcome|status`. There is **no HTTP endpoint**.

**Codes:**
- `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT`, `DPLUS_RECOVERY_EVIDENCE_MISMATCH`, `SR_RECORD_INVALID`, `SR_RECORD_CONTRADICTORY`;
- subtypes `INSTANCE_CHANGED`, `INSTANCE_ANCHOR_CONTRADICTORY`, `INSTANCE_UNPINNED`;
- row codes `DPLUS_DURABILITY_NOT_COMPOSED`, `DPLUS_EVIDENCE_COMMITMENT_UNAVAILABLE`, `DPLUS_ARCHIVE_PIN_UNAVAILABLE`, `DPLUS_RECOVERY_RECORDS_UNAVAILABLE`, `DPLUS_RECOVERY_AUTHORIZED:{id}`;
- the `RECOVERY_*` refusal codes of the tool.

**Rule identifiers:** RR-1..4, AP-1..6, ST-1..4, SG-1..3, EX-1..8, GP-1..3, EP-1..6.

### 24.10 Provisional defaults (implemented fail-closed; NOT approved; owner decisions open)

| Decision | Implemented default | Why it is the restrictive choice |
|---|---|---|
| SR-OD-03 separate key set | `JournalRecovery:Approvers` is separate; a key shared with `OperatorApprovals` is refused at startup | No reuse of membership approvers |
| SR-OD-07 bounds | Approval lifetime at most 900 s; window at most 24 h | The tightest of the drafted values |
| SR-OD-14 permanent blocks | §24.8, implemented as refusals | Refuses rather than permits |
| SR-OD-15 rule-set version | A commitment with another rule-set version is a change, so the stream stops and needs SR-A | Never re-evaluates silently under new rules |
| SR-OD-17 key removal | Removing a key re-stops every stream it lifted | Fail closed instead of grandfathering |
| Not composed (production until WP-8) | Without the journal-verification store, the audit stream is never evaluated (`DPLUS_DURABILITY_NOT_COMPOSED`) | "Do not proceed without a durable commitment"; changes non-Development hosting behaviour |

### 24.11 Open decisions and not claimed

**Open decisions:**
- OD-R2-1: confirm or narrow the not-composed fail-closed behaviour of §24.10.
- OD-R2-2: the DD-10 status (§24.7 EP-6).
- SR-OD-03, SR-OD-07, SR-OD-14, SR-OD-15 and SR-OD-17: confirm the defaults of §24.10.
- SR-OD-05: the production requester permission (not implemented).
- SR-OD-08: SR-B.
- SR-OD-16: retention.
- SR-D and SR-E.
- The verifier's commitment-versus-archive comparison (EP-5).

**Not claimed:** no closure of SEC-BL-01, PD-15, DD-10, CF-1, G14, OI-DR-1 or OI-DR-9; M2F3-43 stays FAILED. No production enablement, no two-human assurance, no independence of the stores, no independent review.

---

## 25. Amendment A-5 (dated 2026-11-18): G14-R2 owner dispositions (C-156)

**Authority:** owner authorization of the package "WP-5b: P2 Gate and Authorization/Content D+ Enforcement" (2026-11-18), decision log **C-156**. A-5 **records dispositions only**. It changes no rule in §1–§24 and grants no additional authority.

| # | Disposition |
|---|---|
| A5-1 | **G14-R2 accepted for Development/Testing.** §24.1–§24.9 stand as implemented. A-4's open item "no decision-log number was assigned" is closed by C-156 |
| A5-2 | **OD-R2-1 confirmed.** Without the journal-verification store, D+ refuses to evaluate (`DPLUS_DURABILITY_NOT_COMPOSED`); fail closed when required durability infrastructure is absent. Not narrowed |
| A5-3 | **OD-R2-2 decided.** DD-10 is **resolved within the tested Development/Testing single-store threat model**, with the residuals of §24.7 EP-6 recorded. It **remains open for production assurance** (separate stores, identities and immutability, §14). This is not a production closure |
| A5-4 | **§24.10 defaults retained without loosening.** SR-OD-03 (separate key set), SR-OD-07 (900 s / 24 h), SR-OD-14 (§24.8 permanent blocks), SR-OD-15 (a rule-set version change stops the stream) and SR-OD-17 (key removal re-stops) stay as implemented. **Wording reconciliation:** §24.10 calls them "NOT approved", and they are now owner-retained restrictive defaults. Any loosening needs a new owner decision, and no rule text changes. In particular, the §24.8 block on authorization and content streams **remains in force** |
| A5-5 | **Unchanged:** SR-OD-05, SR-OD-08, SR-OD-16, SR-D, SR-E and the EP-5 comparison stay open (§24.11). SEC-BL-01, PD-15, CF-1, G14 and OI-DR-9 stay OPEN, and M2F3-43 stays FAILED |
| A5-6 | **P2:** the readiness assessment and the proposed OI-DR-1 definition are in `SEC_BL_01_P2_READINESS_RECORD.md`. P2 is not passed. Authorization/content D+ and recovery need a further amendment and owner decisions P2-D1..P2-D5 |

---

## 26. Amendment A-6 (dated 2026-11-18): P2, D+ and controlled recovery on the authorization and content streams (C-157)

**Authority:** owner decisions **C-157** (P2-D1..P2-D5, with conditions), Development/Testing only. §1–§25 are preserved unchanged. For the authorization and content streams, §26 governs where it conflicts with §5.3, the P2 row of §5.6, §24.3 ST-4, the first block of §24.8 and the EP-5 item of §24.7/§24.11. Nothing else is relaxed.

### 26.1 OI-DR-1 and the P2 gate

| # | Rule |
|---|---|
| P2-1 | OI-DR-1 is defined by RD-1..RD-10 (`SEC_BL_01_P2_READINESS_RECORD.md` §2). Each demonstration needs executed tests; source inspection does not count |
| P2-2 | Sequencing (P2-D4): the content stream first; the authorization stream only after RD-1..RD-10 pass for content. Passing for content proves nothing about authorization |

### 26.2 Phase P2 enabled

| # | Rule |
|---|---|
| EN-1 | D+ (DP-1..DP-14 as amended by §22–§24, including §23 durable stops and §24.5–§24.7 lift, generations, pinning and W2-S) applies to **every** journal stream. The enforced set is fixed in code: `audit`, `authorization`, `content` (§5.7). There is no partial, sampled, warn-only or bypass mode and no configuration switch |
| EN-2 | No delivery path without D+ remains. A row whose stream name is not a known journal stream is never delivered |
| EN-3 | **Batching:** every stream is fetched in its own bounded batch. A D+ refusal or wait ends only its own stream for the pass; an archive outage (DP-7), a body-digest mismatch or an archive conflict (DP-10) ends the pass for the organization, as before |
| EN-4 | Wording: in §23 and §24, "the audit stream" reads "the stream" for every D+-enabled stream; GP-3 (DD-11) applies to each |

### 26.3 Recovery extension (reverses the authorization/content portion of SR-OD-14)

| # | Rule |
|---|---|
| RX-1 | ST-4's refusal "the stream is not audit" reads "the stream is not D+-enabled". The first block of §24.8 ("acts on an authorization or content stream") is lifted **for SR-A and SR-C1 only** |
| RX-2 | Every other block of §24.8 stays: no quarantine lift, no recovery across an archive-instance change or stream epoch, no overwrite or delete, no proceeding without required containment, nothing outside Development/Testing, nothing on unavailable or contradictory mandatory evidence, no anchor from an invalid checkpoint chain. SG-3 is unchanged: SR-B, SR-B-UNDELIVERABLE, SR-C2, SR-D and SR-E stay non-operational |
| RX-3 | §24.1–§24.7 apply per (organization, stream) unchanged. Approvals bind the stream (AP-2), so no decision for one stream counts for another |
| RX-4 | The recovery command line takes `--stream audit|content|authorization` (default `audit`); any other value is refused |

### 26.4 Stop consequences (SC-1..SC-7, accepted with conditions)

| # | Rule |
|---|---|
| SC-R1 | An authorization-stream stop closes the organization gate (`AwaitingArchive`, mode `RequiredBeforeEffective`) until an executed recovery and delivery of the stopped entry and of every later entry. Containment is never bypassed to restore availability |
| SC-R2 | A content-stream stop leaves every revision whose checkpoint entry is not delivered unreleased (`AwaitingArchive`); content writes continue |
| SC-R3 | A stop that SR-A and SR-C1 cannot resolve (an adversarial row forgery, an archive-instance change or contradictory anchors, adversarially changed evidence) is an **outstanding broader-G14 requirement and a production blocker**. Permanent organization lockout is never an acceptable final production recovery capability |
| SC-R4 | Accepting SC-6 does not authorize modifying or baselining the persistent Development stack |

### 26.5 Configuration errors, outages and integrity failures (SC-5)

| # | Rule |
|---|---|
| CE-1 | Configuration: a host whose archive is usable but whose D+ durability is not composed is **not ready** with `DPLUS_DURABILITY_NOT_COMPOSED` (Authority readiness). In Development/Testing such a host already refuses to start (WP-5a.1). No automatic fallback, downgrade or bypass |
| CE-2 | Outage: an unavailable archive, journal-verification store, pin anchor or recovery-record listing makes the stream wait with its outage code; nothing is delivered, lifted or recorded as a finding |
| CE-3 | Integrity: a confirmed refusal writes the durable stop records (§23) and stops the stream |

### 26.6 Verifier comparison of evidence commitments (EP-5; P2-D5)

| # | Rule |
|---|---|
| VC-1..VC-9 | For the stream it sweeps, the verifier lists the stream's W2-S commitments in both stores and reports `DPLUS_COMMITMENT_MISMATCH` (security, alert-only) with subtype `MALFORMED`, `CONFLICTING_COPIES`, `COPY_MISSING`, `WRONG_INSTANCE`, `INVALID_GENERATION`, `ROW_DIGEST`, `WRONG_ROW`, `PREDECESSOR_DIGEST`, `MISSING` (an archived revision at or above the stream's first committed revision without a commitment) or `BEYOND_HEAD`. Only the highest generation of a revision is compared with the archive; a generation g ≥ 1 needs a stop marker of generation g − 1 |
| VC-10 | Comparison source: the archived bytes the sweep reads (above the trusted checkpoint), and, for revisions at or below it, the AuthorityDb rows the full sweep re-verifies against the checkpoint digest. Archived objects at or below the checkpoint are not re-read (OI-DR-7 unchanged) |
| VC-11 | A stream without any commitment is not inspected. A store outage makes the sweep inconclusive. The verifier never repairs, re-commits or lifts |

### 26.7 Known limits (unchanged by A-6)

- **E-6 / E-7:** the organization gate and content release still trust the rows' delivery state. A J1 actor who marks undelivered rows `Anchored` reopens the gate or releases a revision. D+ never trusts that state: nothing is delivered, the stop stays durable, the next committed change is held again, and the verifier reports `FAKE_RECEIPT`. Closing E-6/E-7 needs a separate owner decision.
- R-DR-1 (alteration before the first commitment), removal of both commitment copies with the stop records (A-3 known limit), a compromised runtime (JP) and configuration-separated stores (PV-9) are unchanged.

### 26.8 Not claimed

No closure of SEC-BL-01, PD-15, G14 or OI-DR-9; CF-1 closure is an owner decision on the WP-5b record. DD-10 keeps its qualified Development/Testing resolution (A-5). M2F3-43 stays FAILED pending formal WP-7 verification. No production enablement, two-human assurance, store independence or independent review.

---

## 27. Amendment A-7 (dated 2026-11-18): G08 journal immutability and trusted delivery receipts (E-6, E-7; C-158)

**Authority:** owner decisions **C-158** (WP-5b accepted; CF-1 closed for Development/Testing in delivery-integrity scope; G08 and E-6/E-7 remediation authorized), Development/Testing only. §1–§26 are preserved unchanged. §27 governs where it conflicts with §11 (M-6 design input), the E-6/E-7 rows of §3, the AwaitingArchive consequences of §5.6 and §26.4 (SC-R1, SC-R2) as to **what counts as delivered**, and the known limit §26.7 first item. The J1–J6 attacker classes (§2) are unchanged: database enforcement narrows J1 and does not constrain J2. Results: `SEC_BL_01_G08_E6_E7_IMPLEMENTATION_RECORD.md`.

### 27.1 Trusted delivery receipt (TR-1..TR-8)

An AuthorityDb journal row's delivery fields (`archive_delivery_state`, `archive_receipt_reference`, `archive_receipt_digest`, `journal_archived_on`, `last_archive_error_code`, `archive_attempt_count`) are a **claim**, never proof. An entry counts as delivered only when `IJournalReceiptVerifier` returns `Verified`:

| # | Rule |
|---|---|
| TR-1 | The row is `Anchored` (pre-filter only; never sufficient) and its canonical body hashes to its own digest |
| TR-2 | The archive object at (organization, stream, revision) is present and its SHA-256 equals the row digest. The archive is create-only and not writable by an AuthorityDb actor |
| TR-3 | The archive sentinel's instance equals the **trusted expected instance** (provisioning record, else the stream's first checkpoint; §24.6 pinning). Unpinned ⇒ not verified |
| TR-4 | The entry's W2-S evidence commitment (§24.7) exists in **both** stores at the revision's highest stop generation, both copies are byte-identical, and it names exactly this organization, stream, revision, generation, row identity, row digest, previous digest (contiguous position) and the pinned instance. Any malformed key under the revision is contradictory |
| TR-5 | A store outage is `Unavailable`, which is not delivered. It is never read as "absent" and never as delivered |
| TR-6 | Verified receipts may be cached per process, keyed by (organization, stream, revision, row digest). Journal rows are immutable (§27.4) and the archive and commitments are create-only, so a receipt cannot become stale for the same key; a changed row has a different digest and misses. A restart starts with an empty cache |
| TR-7 | The cache is bounded (default 200 000 receipts; cleared when full) |
| TR-8 | No other input is consulted: receipt reference, receipt digest, archived-on time, error code, attempt count and the organization's enforcement mode are ignored |

An entry without a W2-S commitment (delivered before W2-S, §24.7) is not verified: fail closed. There is no fallback to the row state, no bypass and no configuration switch. A host without a composed verifier (`RECEIPT_VERIFIER_NOT_COMPOSED`) releases nothing.

### 27.2 Organization gate (E-6; GT-1..GT-6)

| # | Rule |
|---|---|
| GT-1 | The gate reads the organization's latest authorization-stream entry from the journal itself (highest revision), not from the mutable organization revision column |
| GT-2 | The gate is `AwaitingArchive` unless that entry has a verified receipt (§27.1). Under D+ an entry is archived only after its predecessor has been archived and checked (DP-9), so a verified head implies a delivered prefix |
| GT-3 | The pending-row count is kept as a cheap pre-check; it can close the gate, never open it |
| GT-4 | The enforcement-mode column is mutable AuthorityDb state and can no longer waive the archive requirement (`Asynchronous` no longer opens the gate) |
| GT-5 | The head is read on every evaluation. Only a new head reaches the external stores, so a newly committed revocation (a new head) is never answered from a cached receipt; until its receipt exists the gate is closed |
| GT-6 | The projection processor applies an authorization entry only when that entry has a verified receipt (same rule) |

Quarantine and the projection check are unchanged. SC-R1 (§26.4) stands: an authorization-stream stop keeps the gate closed until executed recovery and delivery.

### 27.3 Content release (E-7; CR-1..CR-3)

| # | Rule |
|---|---|
| CR-1 | The checkpoint entry binds to exactly the head being released: content stream, kind `ContentHeadCommitted`, the head's committed operation (column and body), and the canonical body's organization, stream name, stream revision, record, committed content revision, committed operation and manifest root equal the head's and the row's. A receipt for another record, revision, operation or body never releases this head |
| CR-2 | The checkpoint entry has a verified receipt (§27.1) |
| CR-3 | Otherwise the revision is not released (`AwaitingArchive` / `AWAITING_ARCHIVE`), including on a store outage. Content integrity (row HMAC, manifest root) is verified afterwards as before |

`ContentRevision.ArchiveState` in Authority responses remains an informational copy of the row state; it is not a release decision and must not be used as one.

### 27.4 G08 database enforcement (G8-1..G8-6; supersedes the §11 design input where they differ)

| # | Rule |
|---|---|
| G8-1 | **Immutable fields** (row `BEFORE UPDATE` trigger): identity, organization, stream, revision, kind, command and operation identifiers, body, digest, previous digest and creation time never change |
| G8-2 | **Delivery-state transitions:** `Pending → Pending` (attempt count and error code only; no receipt fields); `Pending → Anchored` (receipt reference, receipt digest and archived-on time all required); `Pending → VerificationFailed` (no receipt fields). `Anchored` is final. The attempt count never decreases. **Exception to §11 "terminal states final":** `VerificationFailed → Pending` is allowed only for the G14-R2 recovery scheduling hint (error code `DPLUS_RECOVERY_AUTHORIZED:…`, no receipt fields, same attempt count; A-4 EX-2). Every trigger condition is NULL-safe |
| G8-3 | **No deletion:** row `BEFORE DELETE` and statement `BEFORE TRUNCATE` triggers always raise. (A plain `TRUNCATE` is already refused by the foreign keys referencing the journal; `TRUNCATE … CASCADE` reaches the trigger.) |
| G8-4 | **No INSERT trigger** (M2F3-54, §11). A forged insertion stays possible for J1; it has no trusted receipt, so it closes the gate or withholds release (fail closed) and D-R detects it |
| G8-5 | **Grants:** the runtime role has `SELECT, INSERT` on the journal and `UPDATE` only on the six delivery columns; no `DELETE`, `TRUNCATE`, `TRIGGER` or table-wide `UPDATE`. Grants are re-applied by `--migrate` (F-E2 window unchanged) |
| G8-6 | **Verifier identity:** a dedicated `NOLOGIN` role `authority_journal_verifier` with `SELECT` on the journal tables and on the organization head columns only, and an optional login (`Database:JournalVerifierLoginName`) that inherits it with `default_transaction_read_only = on`. Trigger functions are plpgsql, `SECURITY INVOKER`, fixed `search_path = pg_catalog, pg_temp`, owned by the migration identity, with `PUBLIC` execute revoked |

**Limit (unchanged J2):** an AuthorityDb administrator or superuser can disable or drop the triggers, change grants or set `session_replication_role = replica`. G08 does **not** constrain J2. J2 still cannot create a trusted receipt (archive and verification store are outside AuthorityDb), so J2 cannot open the gate or release content for an undelivered entry; J2 deleting or rewriting the journal tail is detected (D-R `BEHIND_ARCHIVE` / gaps), not prevented.

### 27.5 Consequences and limits

| # | Item |
|---|---|
| L-1 | E-6 and E-7 as attacks by J1 (forged `Anchored`, forged receipt fields or timestamps, forged positions, forged enforcement mode) no longer open the gate or release content. Executed evidence: `SEC_BL_01_G08_E6_E7_IMPLEMENTATION_RECORD.md` |
| L-2 | Journal entries without W2-S commitments fail closed. An existing store holding such entries as its latest authorization or content checkpoint (for example the persistent Development stack) stays closed for them until a separately authorized action; nothing in this amendment changes that stack |
| L-3 | A compromised runtime process (JP) holds the stores' credentials and is out of scope as before. An actor controlling both the archive and the verification store (J4 + J6) can fabricate receipts; store independence is a production requirement (PV-9, DD-10) |
| L-4 | J2 tail deletion before delivery of the deleted entries is detection-only (§27.4 limit) |
| L-5 | The platform journal (`platform_journal_entries`) is outside E-6/E-7 and outside §27 |

### 27.6 Not claimed

No closure of SEC-BL-01, PD-15, G14, OI-DR-7 or OI-DR-9; E-6/E-7 closure is an owner decision on the implementation record. M2F3-43 stays FAILED pending formal WP-7 verification. DD-10 keeps its qualified resolution (A-5). No production enablement, no production database hardening claim, no protection against J2 or JP, no store independence and no independent review.

---

## 28. Amendment A-8 (dated 2026-11-18): G08 accepted, E-6/E-7 closed for Development/Testing, WP-6/WP-7 authorized (C-159)

**Authority:** owner decision **C-159** (owner package "WP-6 / WP-7 — Integrated Security Verification and Formal Acceptance Assessment", 2026-11-18). A-8 **records dispositions only**. It changes no rule in §1–§27 and grants no authority beyond the package. §1–§27 are preserved unchanged.

| # | Disposition |
|---|---|
| A8-1 | **G08 accepted for Development/Testing.** §27.4 (G8-1..G8-6) stands as implemented (`SEC_BL_01_G08_E6_E7_IMPLEMENTATION_RECORD.md`) |
| A8-2 | **E-6 closed for Development/Testing** against the **J1** runtime database credential threat model: the organization gate (§27.2, GT-1..GT-6) opens only on a trusted delivery receipt (§27.1); forged `Anchored` state, receipt fields, timestamps, positions and enforcement mode written by J1 do not open it |
| A8-3 | **E-7 closed for Development/Testing** against the same J1 threat model: content release (§27.3, CR-1..CR-3) requires a trusted receipt for the checkpoint entry bound to exactly the released head |
| A8-4 | **Limitations retained, not closed:** J2 is not constrained by G08, and J2 tail deletion before delivery is not prevented (§27.4 limit, §27.5 L-4); a compromised runtime process (JP) is out of scope; J4 + J6 control fabricates receipts (L-3); the Development/Testing stores are configuration-separated only (PV-9). None of these is a production closure |
| A8-5 | **CF-1** keeps its qualified closure (C-158: Development/Testing, delivery-integrity scope). **DD-10** keeps its qualified resolution (A-5: Development/Testing single-store threat model; open for production). Neither is re-decided |
| A8-6 | **WP-6 / WP-7 authorized** (Development/Testing, disposable PostgreSQL 18 and Azurite, synthetic data). **Reading of WP-6:** §17 defines WP-6 as M-6 (§11), whose design input A-7 §27.4 superseded and which G08 delivered. The package's WP-6 assessment items (journal completeness, checkpoint chain, enumeration, commitments, accepted exceptions, stops, recovery records, receipts, archive identity, findings, verifier restart) are WP-4..WP-5b verifier and D+ properties; they are assessed against their governing rules in the traceability matrix of `SEC_BL_01_WP6_WP7_VERIFICATION_RECORD.md` and are not re-specified here. OI-DR-7 is assessed there against VC-10 and IV-5, without changing the full-verification cadence or the checkpoint trust model |
| A8-7 | **Unchanged:** M2F3-43 stays **FAILED** until the owner reclassifies it on the WP-7 record (WP-7 proposes a result only). SEC-BL-01, PD-15, G14 (SR-B, SR-D, SR-E, SC-R3), OI-DR-7 and OI-DR-9 stay OPEN. Stage B is not authorized |

---

## 29. Amendment A-9 (dated 2026-11-19): WP-7A — receipt validity model TR-6′ (F-1), historical archive verification VF-15 (OI-DR-7), J2 revocation-deletion assessment (C-160)

**Authority:** owner decision **C-160** (owner package "WP-7A — Integrated Verification Remediation and Acceptance Completion", 2026-11-19): F-1 remediation and amendment of TR-6 authorized; OI-DR-7 remediation authorized; J2 revocation-deletion assessment authorized, with corrections limited to those the approved contract and trust model already support. Development/Testing only. §1–§28 are preserved unchanged; §29 governs where it conflicts with A-7 §27.1 TR-6, §8.3 (VF-12/VF-13 as to which archived objects the full sweep reads), A-6 §26 VC-10 (its "not re-read" sentence) and §19 OI-DR-7's recommended disposition. TR-1..TR-5, TR-7 and TR-8, GT-1..GT-6, CR-1..CR-3 and every other rule are unchanged. Results: `SEC_BL_01_WP7A_VERIFICATION_RECORD.md`.

### 29.1 TR-6′: the trusted-receipt validity model (replaces TR-6)

A receipt has two parts. **Immutable facts** are pure functions over bytes (the row's self-digest; that given object bytes hash to the row digest; that given commitment bytes name the row); they are recomputed or compared, never trusted from earlier store state. **Current state** is what the stores hold now: the archived object at the entry's key and its equality with the row, the sentinel's instance and the pin, both commitment copies at the current highest generation, the stop markers, and for a stream head the absence of successor evidence. One **establishment** reads all of it from the stores.

| # | Rule |
|---|---|
| RV-1 | No store state is cached as immutable. A receipt is never trusted indefinitely on the basis of an unchanged journal head or row digest |
| RV-2 | **Freshness bound Φ** (`JournalReceipts:CurrentStateMaxAgeMilliseconds`; Development/Testing default 1 000 ms; validated 0 ≤ Φ ≤ 30 s at construction, otherwise the host does not start). A verified establishment answers protected access only if it **started** at most Φ before that access. Φ = 0 re-establishes on every access. Expiry is not evidence: it only forces re-establishment |
| RV-3 | A refused or unavailable establishment is never reused. A store outage is `Unavailable` (TR-5), never "last known good" and never "absent" |
| RV-4 | **Stream-head receipts** (the organization gate, GT-2): refused (`RECEIPT_SUCCESSOR_EVIDENCE`) while any successor evidence exists outside AuthorityDb: an archived object at revision r + 1, a W2-S commitment at r + 1 in either store, or a stop marker above r. Positional only: it does not withdraw the entry's own evidence. Entry receipts (content checkpoints CR-2, projection GT-6) are not positional |
| RV-5 | **Stops:** a stop marker at s ≤ r of generation g is resolved only by a commitment at s of a generation > g present and identical in both stores (for s = r, the receipt's own generation); otherwise `RECEIPT_STOP_UNRESOLVED`. A non-canonical marker name is never ignored. A commitment of generation g ≥ 1 requires a durable stop marker of generation g − 1 at or below r (consistent with VC-5); otherwise the commitment does not verify |
| RV-6 | **Withdrawn evidence:** when evidence that verified a key earlier in the process later fails (object absent or different, instance or pin different or missing, commitment missing, contradictory or different) or changes identity (instance, pinned instance, generation, commitment bytes), the key is refused for the rest of the process lifetime (`RECEIPT_EVIDENCE_WITHDRAWN`), even if the evidence is later restored. Outages never withdraw |
| RV-7 | **Concurrency:** at most one establishment is in flight per (key, head-or-entry); accesses that arrive while it runs share it. A newly committed head is a new key and is never answered by an older head's attestation (GT-5 unchanged) |
| RV-8 | **Bounds:** attestations and first-established evidence are bounded (TR-7, default 200 000; cleared when full; a cleared key is re-established from the stores, never assumed). Withdrawn keys are bounded (default 200 000) and never cleared; when that bound is reached every receipt is refused (`RECEIPT_CONTRADICTION_LIMIT`) |
| RV-9 | **Restart:** a new process starts empty and decides from the stores only. It does not remember withdrawn evidence (L-9) |

**Achieved Development/Testing guarantee.** Against J4 (archive), J5 (archive instance) and J6 (verification store) changes to the evidence of an entry whose receipt was established: the change is honoured by a running process for at most Φ plus one establishment's duration; then the receipt is refused and stays refused in that process. A stream head with successor evidence is refused at its next establishment. **Not achieved:** immediate detection. A check-then-use window of at most Φ + establishment duration remains, and RV-9 forgets withdrawn evidence across a restart if the attacker restores it first (the verifier's sweeps still detect a change that persists). **Production requirement for immediate effect (not available in Development/Testing):** prevention rather than detection, by locked version-level immutability (WORM) on the archive and the verification store so that replacement and deletion are impossible (§14), plus an independently anchored instance identity (CK-6, CKR-2) and, where a bound below Φ is required, store change notification bound to the gate. These are production prerequisites, not implemented.

### 29.2 VF-15: historical archive verification by the full sweep (closes the OI-DR-7 gap for Development/Testing)

| # | Rule |
|---|---|
| VF-15 | The full sweep (§8.3) reads and byte-compares **every** archived object of the stream from revision 1 with its chain-verified AuthorityDb row (VF-7 applied to all revisions, including those at or below the trusted checkpoint), verifies the archive-side chain over the archived bytes (R-1..R-7), and compares the W2-S commitments with the **archived** digests at every revision (VC-6/VC-7). Replacement, deletion (AE-4, unchanged), digest substitution, wrong object identity, journal/archive disagreement and commitment disagreement below the checkpoint are therefore findings of the scheduled full sweep, without a re-baseline |
| VF-15a | Cadence and trust model unchanged: `T_full` = 24 h, `T_inc` = 5 min (§8.6); checkpoints, CKR-1..3 and the incremental sweep (IV-1..IV-6, bounded above the checkpoint) are unchanged. Detection latency below the checkpoint is `T_full` + the full sweep's duration |
| VF-15b | Archive reads within a row batch are issued with bounded concurrency (`JournalVerificationLimits.ArchiveReadConcurrency`, default 8) and processed strictly in revision order; outcomes, findings and inconclusive reasons are those of the sequential walk. The same reader is used by the baseline (BL-1) |

VC-10's sentence "Archived objects at or below the checkpoint are not re-read (OI-DR-7 unchanged)" is superseded for the full sweep; for the incremental sweep it stands. OI-DR-7 is remediated for Development/Testing; its formal closure is an owner decision.

### 29.3 J2 revocation-deletion assessment (no new protocol)

| Phase | J2 deletes the newest authorization entry (a revocation) and resets the head | After A-9 |
|---|---|---|
| P-1 | Before its W2-S commitment exists | **Not prevented, not detected.** No evidence of the entry exists outside AuthorityDb; the gate opens on the previous, genuinely delivered state and the revocation never becomes effective. Unchanged residual (R-DR-1 class, L-4) |
| P-2 | After its commitment, before delivery | **Prevented at the gate** (RV-4: the commitment above the head is successor evidence) and detected by the incremental sweep (`DPLUS_COMMITMENT_MISMATCH/BEYOND_HEAD`) |
| P-3 | After verified delivery | **Prevented at the gate** for every evaluation whose establishment starts after the deletion (RV-4: the archived object above the head); a running process with a fresh attestation of the previous head answers for at most Φ + establishment. Detected by the incremental sweep (`BEHIND_ARCHIVE`); the next change collides (permanent quarantine) |

Closing P-1 requires trusted evidence of the entry outside AuthorityDb **before** the command is acknowledged as committed (an externally trusted authorization-change commitment, or an independently established authorization freshness such as a ledger-anchored head that the gate requires). Each is a new commit-path step or trust dependency and is **not** implemented: owner decision (§29.5).

### 29.4 Limits (L-9..L-11)

| # | Limit |
|---|---|
| L-9 | RV-9: withdrawn evidence restored before a restart is not remembered by the new process |
| L-10 | RV-2: bounded staleness Φ + establishment duration; Φ is configuration |
| L-11 | §29.3 P-1 residual |
| L-12 | **Availability:** RV-3 means an archive or verification-store outage longer than Φ closes the gate (`AwaitingArchive`), and stops content release for every evaluation that re-establishes. Previously a cached receipt kept unchanged heads open during an outage. A command that needs the open gate is refused (`AUTHORITY_NOT_READY`) rather than accepted on stale evidence. Any grace period would reopen F-1 for its duration (owner decision, D-7A-6 of the WP-7A record) |

### 29.5 Not claimed

No immediate detection, store immutability, independent anchoring, closure of SEC-BL-01, PD-15, G14, OI-DR-7 (formal), OI-DR-9, AC-DR-11 or AC-DR-13; no reclassification of M2F3-43 (stays **FAILED**); no protection against J2 P-1, JP, or J4 + J6 collusion; no production enablement; no independent review. Stage B is not authorized.

---

## 30. Amendment A-10 (dated 2026-11-19): WP-7A dispositions and conclusion of the Development/Testing workstream (C-161)

**Authority:** owner decision **C-161** (owner package "EnterpriseForms — WP-8 Development Workstream Consolidation", 2026-11-19; documentation only). A-10 **records dispositions only**. It changes no rule in §1–§29 and grants no authority. §1–§29 are preserved unchanged. The package is **not** WP-8 of §17 (production prerequisites), which is not started. Consolidation, conflict analysis (CN-1..CN-9), acceptance matrix and the pre-production backlog PP-01..PP-15: `SEC_BL_01_DEVELOPMENT_WORKSTREAM_CONSOLIDATION.md`.

| # | Disposition |
|---|---|
| A10-1 | **TR-6′ (§29.1) accepted** for Development/Testing with Φ = 1 s (D-7A-1). F-1 remediated and accepted. The residual window is Φ plus one establishment's duration; L-9 and L-10 remain documented limits |
| A10-2 | **VF-15 (§29.2) accepted** (D-7A-2). OI-DR-7 is **resolved for Development/Testing** and open for production (§14.1 immutability; full-sweep cost in PP-08) |
| A10-3 | **L-12 accepted** (D-7A-6): an archive or verification-store outage longer than Φ closes the gate and refuses commands; no grace period. The SEC-P12 test change is accepted only as preserving the original SEC-P12 obligation ("remains non-effective or quarantined; never releases protected bytes without required receipt"); the accepted-and-held path must remain demonstrated, and the Azure staging SEC-P12 must demonstrate it for both streams (consolidation record CN-3) |
| A10-4 | **F-7A-4 remediation deferred** (D-7A-4) to PP-10. The A-4 EX-1 executor re-run remains the interim documented remedy. A-3 and A-4 are unchanged |
| A10-5 | **F-7A-5 deferred to the mandatory pre-production backlog** (D-7A-5; PP-08). §26.6 is unchanged. Production acceptance requires the incremental sweep to complete within `T_inc` = 5 min; for `T_inc` this owner-set production criterion supersedes OD-DV-6's "measurement defaults, not commitments" |
| A10-6 | **J2 P-1 (§29.3) accepted as a documented Development/Testing residual** (D-7A-3, option (c)) of the R-DR-1 / R-M2f3-6 class (§18), limit L-11. Reading, with no text change: the detection statements of the §27.4 limit and of §27.5 L-4 apply to P-2 and P-3 only; P-1 is neither prevented nor detected. In production the residual is acceptable only with independently administered AuthorityDb (PP-03); no record may represent the PB-09 "zero accepted authorization rollback" target as met against J2. No authorization architecture redesign is authorized; §29.3 options (a) and (b) are not authorized |
| A10-7 | **AC-DR-11 deferred and INCONCLUSIVE** (D-7A-7; PP-11). D-7A-8 (seed equivalence) is not decided and remains open |
| A10-8 | **WP-7A implementation and executed evidence accepted** for Development/Testing. **AC-DR-13 is conditionally met**: it covers the executed Development/Testing evidence only and is not an acceptance of AC-DR-11, of any production criterion or of any other unmet requirement |
| A10-9 | **Contract hash reconciliation accepted:** H-WP0 `4a8bd1b1…ef12e` and H-AWK `3549e3ba…f8f2` hash two byte ranges of the same unchanged §1–§20; both reproduced after A-10 |
| A10-10 | **M2F3-43 stays FAILED.** Technical verification of delivered-row deletion and alteration detection is recorded for Development/Testing. Reclassification is not permitted while `E3_M2F3_CONTRACT_CLOSURE.md` §14.2 ("acceptance by AC-DR-01..AC-DR-14") is unmet: AC-DR-11 is INCONCLUSIVE and AC-DR-13 conditional |
| A10-11 | **Development/Testing workstream concluded.** Outstanding requirements are deferred, not waived, to PP-01..PP-15, each production-blocking and each needing separate owner authorization. SEC-BL-01 and PD-15 stay **OPEN**; G14, SC-R3, OI-DR-9 and DD-10 (production) are unchanged |

### 30.1 Not claimed

No closure of SEC-BL-01, PD-15, G14, DD-10 (production), OI-DR-9 or SC-R3; no reclassification of M2F3-43; no unconditional AC-DR-13; no AC-DR-11 result; no production enablement, store independence, independent anchoring, two-human assurance or independent review. No PP item is authorized. Stage B is not authorized.
