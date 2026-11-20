# SEC-BL-01 — Draft Amendment A-3: Durable Audit-Stream Stop (OI-DR-12, design M2)

**Status (2026-11-18):**

| Item | State |
|---|---|
| Document | **Historical draft.** The owner chose disposition **W1** on 2026-11-18; the normative text is amendment **A-3, §23** of `SEC_BL_01_DR_CONTRACT_CLOSURE.md`, which refines SM-5/SM-6 (see §23.1). This draft is preserved as written, apart from this row and the Implementation row |
| Package | "WP-5a.2: Durable Audit Stream Stop — OI-DR-12" (2026-11-18): select M2, no AuthorityDb Held state, existing state machine preserved |
| Implementation | Stopped under package §2 until the owner disposition, then implemented under W1: `SEC_BL_01_WP5A2_IMPLEMENTATION_RECORD.md` |
| DD-10 | **OPEN** |
| SEC-BL-01 / PD-15 / M2F3-43 | OPEN / OPEN / FAILED |
| Review | Owner-led; prepared by the implementing assistant; not an independent review |

---

## 1. Proposed design (M2)

| # | Rule |
|---|---|
| SM-1 | **Marker.** Create-only object `meta/dplus-stops/{organization:D}/{stream}/{revision:D12}.json` in the security-event archive. It sits under the reserved `meta/` prefix (like the sentinel `meta/archive-identity.json`), outside every `org/` entry prefix, so it can never collide with a journal revision key and is never listed by per-organization enumeration (AE-1/AE-2) |
| SM-2 | **Content** (`DynamicForms/DPlusStreamStop/v1`, canonical JSON, unknown members refused): organization, stream, failed revision, finding code and subtype (exact D+ classification), the finding's deterministic identity, the journal row's entry identifier, the archive instance identifier from the sentinel, detection time (UTC), tool version. No event body or digest of a body |
| SM-3 | **Identity and writes.** The key is deterministic (organization, stream, revision). Identical bytes on retry are idempotent; different bytes under the same key are a conflict and nothing is overwritten. A second marker for a different revision of the same stream is allowed (append-only history) and changes nothing: any marker stops the stream |
| SM-4 | **Write order on a confirmed refusal.** Critical alert, then marker (archive), then finding (journal-verification store), then row state. The row is marked `VerificationFailed` only when **both** durable writes are confirmed. The durable stop is established when **either** is confirmed (SM-5) |
| SM-5 | **Discovery, on every pass, not only at start-up.** Before D+ evaluates any audit entry, list `meta/dplus-stops/{organization}/audit/` completely and check the finding store for D+ refusals of that stream. If either holds a valid record, the stream is stopped: no evaluation, no append, rows untouched. Requires a new read operation on the finding store (it is write-only today). A failed listing or read is unavailability: the pass ends with the entry Pending (DP-7), never "no marker" |
| SM-6 | **Cross-check.** A marker without the matching finding, or the reverse, is reported (`DPLUS_STOP_RECORD_INCOMPLETE`, security) and keeps the stream stopped. A malformed marker, a marker for another organization, stream or archive instance, or an unexpected name under `meta/dplus-stops/` is `DPLUS_STOP_MARKER_INVALID` (security) and also stops the stream. Never ignored |
| SM-7 | **Verifier and completeness analyzer.** Per-organization enumeration stays limited to `org/{o}/`. The full sweep additionally enumerates `meta/` completely. It reports `meta/archive-identity.json` and canonical stop markers explicitly, validates markers as SM-6, and reports every other object under `meta/` as `ARCHIVE_UNEXPECTED_OBJECT`. Nothing under `meta/` is silently ignored. The provisioning check (§22.8 PV-3) already counts every object |
| SM-8 | **Independence from AuthorityDb.** The stop never depends on `archive_delivery_state` or `last_archive_error_code`; resetting, deleting or forging rows does not resume delivery |
| SM-9 | **No clearing.** D+ never deletes, rewrites or supersedes a marker or finding, and never repairs archived evidence. Resuming a stopped stream is an **authorized recovery action** (G14 or an owner-approved procedure), which is not part of this design and not implemented |
| SM-10 | Authorization and content delivery are unchanged (CF-1 open) |

## 2. Classification (package §4)

| State | Meaning | Effect |
|---|---|---|
| Confirmed integrity failure | D+ obtained a definite refusal (`PREDECESSOR_MISSING`, `LINK_MISMATCH`, `CHAIN_INTEGRITY_FAILURE/*`, `ARCHIVE_REGRESSION/*`) | Alert; SM-4 writes |
| Operational storage failure | The archive or the finding store could not be read or written | Never a finding. The entry stays Pending. Already established stops stay in force |
| Unknown or inconclusive | Discovery could not complete (SM-5), or an envelope is unsupported | No delivery, no finding, pass ends |
| Durable stop established | At least one of marker and finding confirmed stored | Stream stopped across restarts, finding-store recovery, row edits and evidence replacement |
| Durable stop not established | Detection happened, neither write confirmed | Stream held **in memory only** (today's DD-9 behaviour): the gap in §3 |

## 3. The failure window that M2 cannot close

**Invariant (package §2):** once a confirmed integrity failure has been detected, delivery does not resume without an authorized recovery action, whether the process restarts, the finding store recovers, AuthorityDb state is modified, the archive evidence is replaced, or a retry observes valid-looking evidence.

**Failure sequence that violates it with M2:**
1. Pass P1 reads the evidence and D+ refuses revision *k*: a confirmed failure, held in process memory.
2. Before the marker **and** the finding are confirmed stored, one of these happens:
   - (a) the process terminates (crash, kill, deployment, host loss);
   - (b) both the archive and the finding store reject or time out the writes, and the process then terminates;
   - (c) the cancellation token fires during the writes (shutdown).
3. A privileged actor replaces the evidence:
   - J4/J5 upload the missing or genuine predecessor bytes (in Development/Testing; mutable Azurite);
   - or J1/J2 rewrite row *k* so that it links to the archived predecessor.
4. The restarted process finds no marker and no finding, re-derives D+ from the replaced evidence, and **delivers *k***.

**Why it is inherent.** The detection is a read and the durable stop is a write. No ordering of writes *after* the detection can survive a termination between the two. Discovering more stores at start-up does not help, because nothing was written.

**What M2 does guarantee.** If either write completes, the stop survives restarts, finding-store recovery, row edits and evidence replacement. The only exception is deletion of the marker by J4/J5 **and** of the finding by J6; SM-6 detects deletion of either one alone.

**Size of the window.** In normal operation it is the latency of two small create-only writes after a refusal. Under a sustained outage of both stores it extends until either store recovers, and a termination at any point in it loses the detection.

**Production note (not a Development/Testing remedy).** With WP-8's immutable archive, existing archive objects cannot be replaced. Step 3 then reduces to *adding* a missing predecessor or rewriting AuthorityDb rows. In both cases the entry delivered after the restart links to an archived predecessor (DP-9), but it still violates the invariant as stated.

## 4. Ways to close the window (each a new security decision)

| Option | Design | Closes §3? | Cost and risks |
|---|---|---|---|
| **W1: accept and narrow** | Implement M2 as §1. Record the §3 window as an accepted residual risk; DD-10 is reported as **narrowed, not closed** | No | Smallest change; meets every package requirement except the invariant inside the window |
| **W2: fail closed on unclean termination** | Before D+ evaluates the first pending audit entry of a pass, write a create-only **intent** object (`meta/dplus-intents/{org}/audit/{revision:D12}.json`). After the outcome (appended, refused and recorded, or cleanly abandoned on an outage), write a create-only **outcome** object. On start-up, an intent without an outcome is an *inconclusive durable stop*: the stream stays stopped until an authorized recovery action, and is reported as inconclusive, not as an integrity finding | Yes, for every termination after the intent is written. A termination before it leaves nothing to forget | Every crash, kill or deployment during an audit delivery pass stops the audit stream until a recovery that does not exist yet (G14): a de facto permanent stop in Development/Testing. Two extra archive writes per pass. The archive gains intent and outcome history (growth, verifier rules). It needs an owner-approved definition of "cleanly abandoned" |
| **W3: defer** | Keep DD-9 (in-memory hold) and DD-10 open until WP-8 provides an immutable archive and an independent anchor; no M2 now | No | No change; the residual stays as in WP-5a.1 |

**Recommendation: W1.** It closes every other path named in the invariant with durable evidence. W2 trades a narrow crash-and-replace window for a broad availability failure that can only be resolved by a recovery process that is out of scope. Whichever option is chosen, DD-10 should not be described as closed until an option that closes §3 is implemented and tested (W2), or the owner formally accepts the residual (W1).

## 5. Tests planned (not run; nothing implemented)

**Storage combinations:**
- both stores available;
- archive available, finding store unavailable;
- archive unavailable, finding store available;
- both unavailable.

**Termination points:**
- before the marker;
- after the marker, before the finding;
- after the finding.

**Attacker actions:**
- evidence replaced before restart;
- marker deleted or altered by an archive actor;
- finding deleted;
- row reset or forged;
- conflicting marker.

**Discovery:** listing failure.

**Regression:** ordering, cancellation, idempotency and isolation.

The termination-before-marker case with evidence replacement is the §3 sequence. Under W1 it would be an expected-gap test; under W2 it would assert the inconclusive stop. Disposable PostgreSQL/Azurite cases, SEC-P01..SEC-P12 and the Authority, architecture and contract suites would follow the implementation.

## 6. Open conflicts

| ID | Conflict |
|---|---|
| C-A3-1 | Package §2 invariant versus §3: unattainable for any detect-then-write design without W2 |
| C-A3-2 | SM-5 needs a read operation on the journal-verification finding store (write-only today); a small port change |
| C-A3-3 | SM-7 changes the verifier's enumeration rules (a `meta/` listing in the full sweep), amending AE-1..AE-3 |
| C-A3-4 | W2 would need a recovery action to be useful; recovery is excluded (G14) |
