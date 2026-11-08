# R-37: record-registration throughput — design recommendation (C-136)

- **Status:** **Recommendation for owner decision. Nothing here is implemented** (C-136 §6). Prepared by Claude (Opus) for an owner-led review; not an independent assessment.
- **Scope:** ingestion of new records into one organization. No change to the indexed search, to Tier 1 or to any security control is proposed here.

## 1. Measured problem (preserved)

| Measurement | Result | Source |
|---|---|---|
| 1,000 records, concurrency 4, organization growing to 1,000 | **944 s** (≈ 1.1 records/s) | IX-4 benchmark (evidence §4) |
| 20 records serially (170-record organization) | 2.48 records/s; p50 367 ms per record | C-135 diagnostic (evidence §10.4) |
| 150 records at concurrency 4 | 2.41 records/s; p50 1,555 ms; p50 7 attempts; 124 `STALE_AUTHORIZATION_REVISION`, 812 `AUTHORITY_NOT_READY` refusals | same |
| 1,000 records serially, seal sweep in bounded transactions | see evidence §11 (C-136) | C-136 measurement |

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
