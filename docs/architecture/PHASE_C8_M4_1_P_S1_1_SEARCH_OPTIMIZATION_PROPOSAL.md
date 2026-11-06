# Phase C8 M4-1: P-S1-1 search verification timeout, investigation and optimization proposal

- **Status:** **Proposal for owner decision. Nothing proposed here is implemented.** Prepared under C-119 (2026-11-05) by Claude (Opus). Not an independent review.
- **Constraints observed:**
  * no change to authorization;
  * no timeout increase;
  * no suppression of the failure;
  * no protocol change without approval.

  The measurement code was throwaway instrumentation in a scratch worktree. It was never committed. Its diff is kept as evidence.
- **Evidence:** [docs/evidence/phase-c8/m4-1-s1-implementation/](../evidence/phase-c8/m4-1-s1-implementation/README.md), files `p-s1-1-probe-*`.

## 1. Question

With S1, the 480-record verified search scope in `SearchCatalogueTests.BenchmarkTheVerifiedScopeAt50And150And480Records` is refused at the 15 s budget (`SEARCH_VERIFICATION_LIMIT`, `time`). Before S1 it completed in 12.5 s p50.

The owner asked two things:
- Is this attributable to S1?
- Can bulk permission checking reduce round trips while keeping every S1 invariant?

## 2. Measurement

**Method.** I added timers (`Stopwatch`, diff in `p-s1-1-probe-instrumentation.diff`) to commit `f117995` at five points:
- the Authority's batch verification (`ContentService.VerifyProjectionsAsync`): pin, record loop, verify, SpiceDB decision calls, MAC-provider calls;
- catalogue issuance (`SearchCatalogueService`);
- the business scope verifier (`TrustedScopeVerifier`): catalogue call, BusinessDb content reads, Authority verify calls.

I ran the benchmark class once on a freshly recreated security-test stack, on 2026-11-05. The laptop and stack were the same as for all other evidence.

**Limits:**
- one run, one host;
- the timers' own overhead is negligible but not zero;
- the 480-record scope timed out, so its business-side line is missing, and the Authority lines cover the 8 batches that finished.

### 2.1 Authority, per 50-record batch (all batches of all scopes; `p-s1-1-probe-summary.txt`)

| Component | Calls per batch | Median ms | Share of batch |
|---|---|---|---|
| Whole batch | | 1,006 (830 to 1,374) | 100% |
| **S1 pin** (marker read, `at_least_as_fresh`) | 1 | 2.8 | **0.7%, pin and verify together** |
| **S1 verify** (`fully_consistent` marker read, AuthorityDb re-read) | 1 | 4.6 | |
| SpiceDB decisions (one check per record, at the exact snapshot) | 50 | 182 | 19% |
| MAC-provider verifications (one Vault Transit HTTP call per leaf, 8 leaves per record) | 400 | 556 | 54% |
| Everything else (per-record AuthorityDb reads, Merkle, mapping; not separately timed) | 5 or more queries per record | 272 | 27% |

### 2.2 Whole search (business side)

| Scope | Runs | Total median ms | Catalogue | BusinessDb content reads (one per record, sequential) | Authority verify calls |
|---|---|---|---|---|---|
| 50 | 16 | 1,801 | 39 | 620 | 1,122 |
| 150 | 13 | 5,523 | 101 | 1,986 | 3,403 |
| 480 | 8 runs plus a warm-up, all refused | budget exceeded | 178 | not logged | 8 of 10 batches done at the deadline: 8,349 ms Authority time, of which **S1 pin and verify: 68 ms** |

## 3. Finding: the refusal is not attributable to S1

S1's direct cost on this path is **about 7 ms per 50-record batch**:
- about 70 ms for a 480-record scope;
- plus about 10 ms for the catalogue pin and verify;
- in all about 0.5% of the 15 s budget.

The rest of the time goes to work that existed before S1:

| Work | Approximate time at 480 records |
|---|---|
| Per-leaf HMAC verification | about 5.3 s |
| Sequential BusinessDb content reads | about 6 s |
| Per-record AuthorityDb reads | about 2.6 s |
| Single-record SpiceDB checks | about 1.7 s |

Linear extrapolation gives about **18 s for 480 records on this host today**.

**Unexplained:** the pre-S1 run's 480-record figure, 12.5 s, is well below a linear extrapolation of that same run's own 150-record figure. That extrapolation is about 18.3 s, and the probe's 150-record median (5.5 s) is close to pre-S1's (5.7 s). I cannot explain why the pre-S1 480-record run was faster. The probe measures S1's cost directly instead of inferring it from that comparison.

There is one change this probe cannot separate: S1 moved decisions from `at_least_as_fresh` to `at_exact_snapshot`, and their per-call cost is not isolated. However:
- in the earlier latency A/B, the added cost of a single check matched the two extra round trips;
- SpiceDB decisions are only 19% of the time.

So no plausible per-call change accounts for several seconds.

**Revised conclusion for P-S1-1 / RR-15:**
- The timeout is a capacity limit that predates S1 and now sits at the budget on this host. The 480-record scenario already used 83% of the budget before S1, and the benchmark has been close to the limit since C7.
- The refusal is the correct fail-closed answer.
- This replaces the earlier wording "probably S1-contributed, not proven". An independent reviewer should check this conclusion (review package, question 8).

## 4. Options

### O-1 (asked for): batch-level bulk permission checks in `VerifyProjectionsAsync`. Authorization-adjacent

**Today:** for each record in a batch, the Authority issues one `CheckBulkPermissions` call with one item (`read`). If that is not allowed, a second call follows (`discover`). For a partial reader, a third follows (all field leaves of that record).

**Proposal:** after the pin, issue the same checks grouped by phase:
1. Registry and head lookups for every record, as today and in the same order. A record that is not registered or has no head gets `RECORD_UNAVAILABLE` as today, and no check is issued for it.
2. **One** bulk call with the `read` items for the remaining records.
3. **One** bulk call with the `discover` items for those not allowed to read.
4. The unchanged per-record steps, using the precomputed outcomes: quarantine, archive receipt, key, leaf set, HMACs, root.
5. **One** bulk call, chunked, with the field-leaf `read` items of all partial readers.
6. `CompleteAsync` after all decisions, as today.

**Expected saving:** at 480 records, about 480 SpiceDB round trips become about 20 to 30. That saves about 1.2 to 1.6 s on this host. **O-1 alone does not bring the 480-record scope under the budget.**

**S1 invariants:**

| Invariant | Effect |
|---|---|
| Every decision at the pinned exact snapshot; `checked_at` must equal the pin | Unchanged: the same port and client. Each bulk response's `checked_at` is checked |
| Decisive verify (`fully_consistent` marker read and AuthorityDb re-read) after every decision | Unchanged: `CompleteAsync` runs after the last bulk call |
| Single-use `VerifiedProjectionSnapshot` | Unchanged |
| No decision released after a detected mismatch | Unchanged |
| Organization lock across pin, decide and verify | Unchanged; it is held for less time |
| RE-1 exposure (decide-to-verify interval) | **Reduced:** today the first decision in a batch is about 1 s before the verify; with O-1 it is tens of milliseconds |
| Decision semantics | Unchanged: the same set of items is asked at the same snapshot, only grouped differently. Exact-snapshot checks are deterministic, so for any snapshot the outcomes equal the per-record path's |
| Error precedence and non-inference | Preserved: an unregistered record is refused before any check; a quarantined record is reported only after `discover` is allowed, so "quarantined but not discoverable" stays indistinguishable from "does not exist" |
| Information sent to SpiceDB | The same items as today; nothing new |

**Two hardening changes, part of O-1 and requiring approval:**
- **Response mapping:** the client currently maps results by position. O-1 would also require each response pair's echoed `request` to equal the issued item: resource type and id, permission and subject. A mismatch or a count difference fails the call as provider-unavailable (503, no quarantine).
- **Per-item errors:** today a per-item error becomes `Unknown`, which is treated as "not allowed". In this verification path the record then reads as unavailable. That is fail-closed, but it reports an outage as a missing record. O-1 would fail the whole call (503) instead. Other callers keep today's behaviour unless separately approved.

**Item limit:** a batch has at most 50 records, so the `read` and `discover` calls have at most 50 items each. The field-leaf call is chunked below a configured cap. SpiceDB's per-request item maximum must be confirmed by a new contract test (CT-7). Going over the limit is an error, never a truncation.

**Tests required:**
- RB-01 to RB-15 and CT-1 to CT-6 unchanged and passing;
- a differential unit test: for mixed full-reader, partial-reader, discover-only, denied, unregistered and quarantined records at one snapshot, O-1 returns exactly what the per-record path returns;
- the mapping-mismatch and per-item-error tests above;
- a rollback injected between the bulk calls still quarantines with no allow released;
- CT-7.

### O-2: batched BusinessDb content reads in `TrustedScopeVerifier`. No authorization change

`ReadAcceptedContentAsync` runs once per record, sequentially, and accounts for about a third of search time (about 12 ms per record).

The proposal reads the 50 records of a chunk with one query per row table: a set-based version of `RecordCandidateStager.ReadStagedAsync`.

The rows are untrusted input: the Authority verifies every item against its accepted manifest, so how they are read does not affect security. Equality tests are needed to show the batched reader returns byte-identical items to the per-record reader.

**Expected saving:** about 4 to 5 s at 480 records.

### O-3: bounded concurrent MAC verification inside the Authority. Integrity path, no protocol change

Leaves are verified one call at a time; this is 54% of the Authority's batch time. The proposal verifies a record's leaves with bounded concurrency (for example 8).

Guarantees kept:
- every leaf is still verified;
- the complete-set and root checks are unchanged;
- the first failure cancels the remaining calls and fails closed exactly as today.

**Expected saving:** up to about 4 s locally. **Production caution:** Azure Managed HSM has no batch HMAC verify, and its per-operation latency and throttling are unmeasured. Concurrency raises the request rate against the HSM, so O-3 must be sized from PERF-01 staging data.

### O-4: batched AuthorityDb reads per batch. Integrity path, no protocol change

Fetching resources, heads, checkpoint journals and manifest leaves for a whole batch in a few queries would save about 2 s at 480 records.

The content heads are locked `FOR UPDATE`, so they must be locked in a fixed order (by record id) to avoid deadlocks with writers. That needs a concurrency test.

### Rejected

| Option | Why |
|---|---|
| Raise the 15 s budget, or exempt the benchmark | Prohibited by the owner (C-119) |
| Skip or cache HMAC verification of leaves the Authority already indexes | Contrary to the read-verification rule (CLAUDE.md §4L.5: verify on every read; never cache `RowSignatureValid=true`) |
| Verify batches in parallel | Each batch holds the organization lock across pin, decide and verify (S1), so they would serialize; avoiding that means changing the lock semantics |
| Drop the per-batch pin and verify | Removes the S1 guarantee; saves about 0.5% |
| Vault Transit `batch_input` as the fix | Local provider only. It would make the laptop benchmark pass while the production HSM path stays one call per leaf, which is a misleading result |
| Return partial results | Contrary to ADR-PB-004 |

## 5. New observation for PB-09 / PERF-01 (not S1)

A 480-record scope with 8 leaves per record means **3,840 HMAC verifications per search**. With Managed HSM in production, each is a network operation subject to service throttling. The present design may not complete large scopes within 15 s in production even with O-1 to O-4.

This is a capacity question for the PB-09 targets (verified search p95 under 3 s; 100K records per organization) and belongs in PERF-01. I recommend recording it in the risk register when the owner decides on this proposal. It is not recorded yet.

## 6. Recommendation and decision requested

1. **Record** that P-S1-1 is not attributable to S1 (section 3), subject to independent review.
2. **Approve O-2** (no authorization change) and **O-1** (with the two hardening changes and the tests listed), implemented together. Expected effect on this host: about 18 s becomes about 12 to 13 s at 480 records, which is within the budget with a modest margin. That is not guaranteed, and the benchmark must pass on a fresh stack before anything is claimed. O-1 touches the S1 decision path, so it should also go to the independent reviewer.
3. **Defer O-3 and O-4** until PERF-01 staging data on Managed HSM latency and limits is available.
4. **Until then**, disposition the `search-catalogue` benchmark failure as an open, known failing performance test (P-S1-1 / RR-15):
   - not a security defect;
   - the threshold unchanged;
   - the Stage B suite **not** represented as green.

Owner decision needed: approve, modify or reject items 2 to 4.
