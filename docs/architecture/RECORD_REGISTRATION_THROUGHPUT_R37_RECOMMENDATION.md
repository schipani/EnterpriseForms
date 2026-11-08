# R-37: record-registration throughput — design recommendation (C-136)

- **Status:** **Recommendation for owner decision. Nothing here is implemented** (C-136 §6; C-137 §4). **§7 (C-137) replaces the expectations of §5 with measured data.** Prepared by Claude (Opus) for an owner-led review; not an independent assessment.
- **Scope:** ingestion of new records into one organization. No change to the indexed search, to Tier 1 or to any security control is proposed here.

## 1. Measured problem (preserved)

| Measurement | Result | Source |
|---|---|---|
| 1,000 records, concurrency 4, organization growing to 1,000 | **944 s** (≈ 1.1 records/s) | IX-4 benchmark (evidence §4) |
| 20 records serially (170-record organization) | 2.48 records/s; p50 367 ms per record | C-135 diagnostic (evidence §10.4) |
| 150 records at concurrency 4 | 2.41 records/s; p50 1,555 ms; p50 7 attempts; 124 `STALE_AUTHORIZATION_REVISION`, 812 `AUTHORITY_NOT_READY` refusals | same |
| 1,000 records serially, seal sweep in bounded transactions | 506 s (1.98 records/s) | C-136 measurement (evidence §11) |
| Phase split at 100 and 1,000 records, serial and concurrency 4 | see §7 | C-137 measurement (evidence §12.4) |

Concurrency adds no throughput; it adds refusals and retries.

## 2. Where the time goes (current design)

One record creation (`CreateFormRecordHandler`) is, in order:
1. two Authority permission decisions (create-record; the organization decision that also returns the current authorization revision);
2. a `RegisterRecord` **security command** naming that revision exactly (authorization-revision CAS). In one AuthorityDb transaction under the organization lock: the command, the desired-state edges, the next authorization revision, the security-journal event and the projection outbox entry. The command loads the organization's **whole active relationship graph** (`GetActiveEdgesAsync`) to plan;
3. the business API **waits until the command is Effective**: projected to SpiceDB (projection barrier and marker) and, under `RequiredBeforeEffective`, archived with a receipt;
4. the business row;
5. the content prepare and commit (Authority, content-integrity revision, inline index derivation and entry seal, under the organization lock).

Consequences:
- **Serialization per organization.** Any concurrent security command makes a creation's revision stale (nothing written, 409). The client retries with a new revision; while a command is not yet Effective the next creation sees `AUTHORITY_NOT_READY`.
- **Cost grows with organization size.** The whole-graph read in step 2 is O(edges of the organization) per creation (1.1 records/s at 1,000 records against 2.4/s at 170).
- **Lock waits** behind the seal sweep (R-33/R-36, addressed separately in C-136) and behind other organization-lock holders.

## 3. Security invariants any change must keep

1. Every relationship change is a journaled Authority security command at a new, monotonic authorization revision; no business-side write of authorization state.
2. A grant or registration is never Effective before it is projected (marker and barrier, S1) and, under `RequiredBeforeEffective`, archived with a verified receipt (G14).
3. Revision CAS: a command is applied only against the revision its author decided on; nothing is applied against a revision it did not see.
4. Idempotency: a retried command (same key and digest) produces the same outcome, never a second registration.
5. Atomicity: a command's edges, revision, journal event and outbox entry commit together or not at all.
6. Content commits stay per record (one accepted manifest per record); index derivation and seals unchanged.
7. Quarantine (S1 and record), deadlines and fail-closed behaviour unchanged.

## 4. Options

| # | Option | Effect | Security assessment |
|---|---|---|---|
| A | **Batched registration command** (`RegisterRecords`): one security command registers N new records (resources and owner edges) at one revision, one journal event, one projection delta; all-or-nothing; idempotency key per batch plus per-record ids | Removes N−1 revision steps, N−1 projection and archive waits, and the CAS contention between creations of one import | Keeps 1, 2, 4 and 5 by construction (one command). Needs: a bounded batch size (journal-event and outbox size, SpiceDB write limits — the IX2-CT-07 per-request maximum applies), per-record permission checks for the creating user before the command, and a review of the import path (PB-13) as its natural caller |
| B | **Graph read restricted to the affected resources** in the command planner (load only the edges of the resources a command touches, plus the organization-level edges planning needs) | Removes the O(organization) cost per command; applies to every command, not only creations | Must prove the planner never needs an unrelated edge (privilege-escalation and cycle checks); CommandPlanner tests and the authorization-graph invariants must cover it. Probably the cheapest large win; needs its own review |
| C | **Server-side retry of a stale revision for registration only** (the business API re-reads the revision and resubmits a `RegisterRecord` once) | Hides retries from clients; no throughput gain under contention | Low risk (registration of a new id commutes with other commands), but it is a retry, not a fix; not recommended alone |
| D | Relax the Effective wait (return before projection or archive) | Large gain | **Rejected**: breaks invariant 2 |

## 5. Recommendation

1. **Option B first**, as a separately reviewed change to the command planner, measured before and after on the same synthetic dataset (expected: per-creation cost no longer growing with organization size; serial rate at 1,000 records close to the small-organization rate).
2. **Option A** for bulk ingestion only (PB-13 imports and similar), with a bounded batch, measured separately; interactive single-record creation unchanged.
3. Not C alone; never D.

**Expected improvement (estimates, not measured):** B removes the size-dependent part (1,000-record organization from ≈ 1.1 toward ≈ 2.4 records/s serially). A, for a batch of N records, replaces N revision steps and N Effective waits by one, leaving the per-record content commit (under the organization lock) as the floor; the floor itself is not measured yet and must be measured (content commit only) before any target is set. No PB-09 claim is made.

**Measurement plan before approval of either option:** split the per-creation time into permission decisions, security command (planning, transaction, Effective wait: projection and archive) and content commit, at 100, 1,000 and 5,000 records, serial and at concurrency 4, using the bounded diagnostics of C-135 (no 10K/50K seeding).

## 6. Decision requested

R-37 stays **open**. Owner decision: approve the measurement plan (§5) and then option B, option A, both, or neither. No implementation without that approval.

## 7. C-137 measurement and the A/B comparison

### 7.1 What the measurement shows (evidence §12.4)

| | ≈ 100 records | ≈ 1,000 records |
|---|---|---|
| Serial creation, p50 | 327 ms (2.6 records/s) | 1,845 ms (0.65 records/s) |
| Organization-lock waits per creation (command + prepare + commit) | ≈ 5 ms | ≈ 895 ms |
| Graph load in `RegisterRecord` (edges) | 1.4 ms (165) | 7.2 ms (1,965) |
| Effective wait (business view) | 164 ms | 513 ms |
| Content prepare + commit | 72 ms | 709 ms |
| Concurrency 4 | no gain; 7.8 attempts per record | no gain; 16.7 attempts per record |

**Correction to §2 and §5.** §2 attributed the slowdown with organization size mainly to the whole-graph read, and §5 expected option B to bring the 1,000-record rate back towards the small-organization rate. The measurement does not support that. At 1,000 records the graph read costs about 7 ms; the slowdown is organization-lock waiting, caused in this stack mainly by the seal sweep, whose 10 s test interval keeps the lock about 90 % of the time at this size (R-39). The graph read is linear (≈ 4 µs per edge) and becomes significant only at much larger organizations (estimate, not measured: ≈ 0.4–0.8 s per command at 100,000–200,000 edges).

### 7.2 Components, by size of effect

| Component | Effect measured | Addressed by |
|---|---|---|
| Organization-lock waits behind the sweep (security-test interval) | Dominant at 1,000 records (≈ 0.9 s of 1.85 s) | Not R-37: R-39 / IX-7 sweep sizing; production interval 900 s gives an estimated ≈ 3 % duty cycle |
| Organization gate closed while a command is not Effective; revision compare-and-swap | Serializes creations; concurrency only adds refusals and retries | **Option A** (one gate closure and one revision per batch) |
| Effective wait: archive step and projection, work-loop idle poll (250 ms) and client status-poll backoff | ≈ 150–165 ms of the ≈ 330 ms small-organization floor | Neither A nor B per record; A amortizes it per batch. (A wake-up of the work loop after a commit, or a shorter idle poll, would change only scheduling; **not proposed for implementation here**; it would need its own review because the work loop is the projection and archive gate) |
| Content prepare and commit (per record, under the lock) | ≈ 70 ms without contention | Neither (invariant 6: one accepted manifest per record) |
| Graph load | ≈ 0.5 % at 1,000 records; linear | **Option B**; matters at tens of thousands of records and above |

### 7.3 Option A versus option B

| | **A: bounded batched `RegisterRecords`** | **B: graph read restricted to affected resources** |
|---|---|---|
| Benefit measured or estimated | Removes N−1 gate closures, revision steps and Effective waits per batch; at the small-organization floor that is up to ≈ 50–60 % of per-record time for bulk ingestion (estimate); interactive creation unchanged | ≈ 7 ms per command at 1,000 records (measured share ≈ 0.5 %); estimated 0.4–0.8 s per command at 100K–200K edges; applies to every security command |
| Invariants 1–5 (journaled command, Effective gate, CAS, idempotency, atomicity) | Kept by construction: one command, one revision, one journal event, one projection delta, all-or-nothing | Kept if the planner never needs an edge it did not load; the risk is a **missed privilege or cycle check** (owner limits, delegation bounds, parent cycles, team-owner eligibility) |
| Invariant 6 (per-record content) | Unchanged: content commits stay per record after the batch is Effective | Unchanged |
| New failure modes | One refused item refuses the whole batch (all-or-nothing) — the caller must split and retry; a larger journal event and outbox delta (bounded by the SpiceDB per-request limit, IX2-CT-07) | A planner query that loads too little gives a wrong decision rather than a slow one; needs a per-command-kind proof of the edge set |
| Recovery | Same as any command: idempotency key per batch plus per-record ids; a crash before Effective leaves the whole batch pending and gated (fail closed); replay is idempotent | Unchanged |
| Implementation complexity | Medium: new command kind, payload bounds, per-record permission checks before the command, business import path (PB-13) as caller, projection and archive of a larger delta, tests | Medium to high: every command kind's planner must declare its edge needs; property tests that restricted planning equals whole-graph planning for random graphs |
| Review need | Owner approval; batch-size and journal-size bounds; PB-13 alignment | Owner approval and a security review of the planner (it is the authorization decision core) |

### 7.4 Revised recommendation (no implementation without separate approval)

1. **Do not prioritize option B on current evidence.** It removes a measured ≈ 0.5 % at 1,000 records. Keep it as a scaling item, measured again at the 100K/250K sizes (with IX5-D4).
2. **Option A is the change that addresses the measured serialization**, for bulk ingestion only (PB-13 imports, benchmark seeding with owner approval), interactive creation unchanged.
3. **Separate the sweep effect before any target is set:** a measurement at 1,000 and 5,000 records with a production-like sweep configuration, explicitly labelled as such and approved in advance (C-137 §2 forbids changing the interval merely to improve results; this would be a different, declared configuration, not a replacement for the 10 s test results).
4. Never relax the Effective wait (option D stays rejected).

R-37 stays **open**.

