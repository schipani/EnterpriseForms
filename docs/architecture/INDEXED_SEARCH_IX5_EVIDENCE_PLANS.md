# Indexed search IX-5: bounded evidence plans (C-144)

**Status (2026-11-10, C-146):** C-146 authorized completing P-1 to P-6. **P-1 and P-3 are complete (§R); P-2, P-4, P-5 and P-6 are not run** (the host was on battery, and timing needs AC power). Originally (C-144) plans only. None generates the 100K/250K datasets (R-43), runs Stage B, or implements Tier 2. Results feed the open evidence rows E-6, E-10 and E-14 (review package §14) and the gates in §14B; they do not replace the independent review.

**Common rules for every plan:**
- security-test stack only (`-Fresh`), synthetic data, never the dev stack;
- AC power checked before any timing; host sleep disabled for the run;
- each run is under 30 minutes; at most 5,000 records and 500 users per organization;
- each run writes its raw numbers to `artifacts/ix-5/` before asserting anything;
- local numbers are indicative only; production gates need Azure staging.

| Plan | Evidence | Reuses | Bound |
|---|---|---|---|
| P-1 | E-6: `LookupSubjects` behaviour on SpiceDB v1.56.2 | `EnterpriseForms.SpiceDbContractProbe` (CT pattern, as IX2-CT-07) | under 5 min |
| P-2 | PERF-01 / E-10: `LookupSubjects` latency, throughput and call amplification | the same probe; `AuthorizationLatencyTests` | under 15 min |
| P-3 | E-14: B2 principal-copy amplification | the Tier 1 index of an existing measurement organization; read-only SQL | under 20 min, at most 5,000 records |
| P-4 | Organization-scale sharing workload | the R-52 timed-share helper; `AuthorizationLatencyTests` | under 20 min |
| P-5 | R-02: 20 ms p95 commit gate | `SearchIndexFoundationTests.MeasureCommitLatencyWithAndWithoutTheIndex` | under 10 min |
| P-6 | Sweep deadlines and operational load | `SealSweepLockMeasurementTests`; `SearchIndexSweepService` timings | under 20 min |

## P-1. E-6: `LookupSubjects` behaviour on the deployed version

**Question:** what does SpiceDB v1.56.2 (the version in `infrastructure/docker/compose.yaml`) actually do for the Tier 2 verification calls (annex E §1, §5, §5A)?

**Procedure:** add contract checks `IX5-CT-08` to `IX5-CT-13` to the existing probe, against a fresh random probe organization:
1. **Limit and cursor:** whether `optional_concrete_limit` and `optional_cursor` are honoured, ignored or refused.
2. **Duplicates:** a user reachable through a direct share and a team share. Is the subject returned once or twice?
3. **Team expansion:** teams of 1, 10, 100 and 500 members. Are all members streamed, in a stable order?
4. **Snapshot:** a read at `at_exact_snapshot(S)` returns subjects as of S after a later revocation.
5. **Deadline:** the error class when the call deadline expires mid-stream.
6. **Empty result:** no message versus an empty message.

**Output:** one line per check (`PASS|FAIL detail`), as CT-1 to CT-7 already do.

**Not covered:** staging behaviour, and SpiceDB versions other than v1.56.2.

## P-2. PERF-01: latency, throughput and API-call amplification

**Question:** the per-lookup cost as a function of subject count, the sustainable rate, and how many SpiceDB calls each Authority operation makes today.

**Procedure:**
1. **Latency against subject count:** using the P-1 fixtures, 200 lookups at 1, 10, 100 and 500 subjects; record p50/p95/p99.
2. **Sustained rate:** 60 s each at concurrency 1, 4 and 16; record the rate achieved, the error rate and p95.
3. **Call amplification today:** count SpiceDB calls per Tier 1 search, per share, per revocation and per team-membership change. Use a client-side counter in `SpiceDbProjectionClient`, behind a measurement-only option that is off by default. This is a test-instrumentation change and needs the owner's approval as code.
4. **Tier 2 projection:** apply the annex E cost model to those counts. The result is labelled an estimate.

**Not covered:** staging capacity, and multi-instance SpiceDB.

## P-3. B2 principal-copy amplification (E-14)

**Question:** how many access-scoped value rows B2 would hold, and how many rows one sharing change would rewrite.

**Procedure:** no B2 tables are created. Read-only SQL runs over an existing Tier 1 organization of 1,000 and 5,000 records, built with the existing benchmark seeding steps only, never the 50K steps.
- For each record, compute: readable principals (owner, direct shares, and team members expanded) × searchable field items, applying the annex A §13 / §14A.3 counting rules.
- Report the distribution of the copy factor: p50, p95, p99 and maximum.
- Report the rows touched by one share, one revocation, one team-member add and one team-member removal.
- Run under three sharing profiles: owner only; 5 direct shares; one team of 100 and one of 500.

**Output:** copy-factor tables and write-amplification counts per command kind. These are the evidence that §14A.3 requires before any cap is considered.

**Not covered:** extrapolation to 100K/250K is labelled an estimate; it needs the R-43 datasets.

## P-4. Organization-scale sharing workload

**Question:** permission-change latency, journal growth and archive lag under a realistic sharing mix. This repeats the R-52 measurement shape with many forms and principals instead of one blocked form.

**Procedure:**
- **Organization:** 5,000 records over 10 forms, 500 users, teams of 10, 100 and 500 members.
- **Command mix over 10 minutes:** direct shares, team shares, revocations, team-member adds and removals, at a fixed rate (for example 2 per second), while Tier 1 searches run at a fixed rate.
- **Record:**
  * command latency p50/p95/max;
  * `AwaitingArchive` occurrences;
  * journal events per minute by kind;
  * archive lag;
  * SpiceDB writes;
  * search latency during the run.
- **Repeat:** once with 5 forms deliberately blocked by a quarantined record, as R-52 regression evidence at scale.

**Not covered:** 100K/250K organizations; multi-organization contention.

## P-5. R-02: the 20 ms p95 commit gate

**Question:** the current commit-latency delta of the index after C-141 (stored counts) and C-143, and where the time goes.

**Procedure:**
- Run `MeasureCommitLatencyWithAndWithoutTheIndex` unchanged on the current head, on AC power and a fresh stack, three repetitions.
- Report the p95 delta against IX-1 evidence §6.4, with the existing phase decomposition.
- No remediation is proposed until the numbers are known.

**Expected outcome:** R-02 stays **FAIL** unless the measured delta is at most 20 ms. Even then the gate closes only on staging evidence (IX-7 ST-PERF-1..7).

## P-6. Sweep deadlines and operational load

**Question:** whether the retained §17.10 deadlines (1 h, 24 h, 30 days, no grace) are achievable, and at what lock-hold and CPU cost.

**Procedure:**
- Run `SealSweepLockMeasurementTests` at 1,000 and 5,000 records.
- Record:
  * the duration of each sweep kind;
  * records verified per second;
  * the organization-lock hold (R-33/R-36);
  * save and search latency while sweeps run.
- Extrapolate linearly to 100K/250K and compare with each deadline. The extrapolation is labelled an estimate and does not close any deadline.

**Not covered:** actual large-organization compliance (needs R-43); Tier 2's M2-S `LookupSubjects` sweep (needs P-2).

## What these plans cannot provide

- An independent review (none has occurred).
- Staging or production numbers.
- The 100K/250K datasets (R-43, a separate approval).
- Closure of R-02, PERF-01, UF-25 or any other gate. Each result is recorded against its register row; nothing is closed or waived by running a plan.

## R. Results (C-146)

Local Docker security-test stack, synthetic data, SpiceDB v1.56.2. Not staging. Raw output under `artifacts/ix-5/` (not versioned).

### R.1 P-1 (E-6): complete, 7/7 PASS

Probe mode `ix5-lookup` (`tests/integration/EnterpriseForms.SpiceDbContractProbe/Program.cs`). The fixture uses teams of 1, 10, 100 and 500 members.

| Check | Result |
|---|---|
| IX5-CT-08 limit | `optional_concrete_limit` is **refused** (`Unimplemented: concrete limit is not yet supported`), with no partial stream. Annex E holds: the client must enforce its own maximum |
| IX5-CT-09 duplicates | A user reachable directly and through a team appeared **once**, with no conflicting permissionship. The API still does not guarantee uniqueness, so the client must keep de-duplicating |
| IX5-CT-10 expansion | User lookups returned exactly N + 1 distinct subjects for every N up to 501. Every `looked_up_at` equalled the pin, and two calls returned the same set and order |
| IX5-CT-11 team-scoped | A lookup with subject type `team` and relation `participant` returned **the team only, not its 500 members**. B2's team principals (D-1) can therefore be verified without member expansion |
| IX5-CT-12 snapshot | After a team revocation, the pin still showed 101 subjects; the new revision and fully consistent reads showed 2 |
| IX5-CT-13 deadline | An expired deadline ended with `DeadlineExceeded`, never a truncated OK stream |
| IX5-CT-14 empty | Zero messages, OK status |

### R.2 P-3 (E-14): counts complete

Read-only SQL over all live Tier 1 postings and canonical edges of the stack: 461 records in 51 organizations, produced by the existing tests.

| Measure | Value |
|---|---|
| Tier 1 value rows | 1,715 |
| B2 value rows (one per posting × reading principal, teams not expanded) | 1,813 |
| Overall copy factor | 1.057 |
| Per-record copy factor | p50 1.0, p95 1.67, p99 2.0, max 3.0 |
| Postings with no reading principal | 0 |
| Team-membership edges copied | 0 of 4 (D-1) |

The counting is structural. For an item, the copy factor is 1 (effective owner) plus the distinct share principals covering it, **independent of team size and record count**. The sharing profiles therefore follow exactly:

| Profile | Copy factor |
|---|---|
| Owner only | 1 |
| 5 direct shares of every item | 6 |
| One share to a team of 100 or 500 | 2 |

Write amplification per command (rows touched are value rows of the affected items):

| Command | Rows written |
|---|---|
| Create or revoke a share of K items | K items' value rows, for one principal |
| Team-member add or remove | 0 |
| Ownership transfer | every item's rows, for the old and the new owner |

The seeded 1,000/5,000-record runs would only replay this arithmetic, so they were not run. Large-scale distributions need the R-43 datasets.

### R.3 FD-5-ALT input check (journal content)

- `SecurityCommandCommitted` journal bodies carry `relationship_delta` (every touched or deleted edge, including each share's per-item `field_item#shared_via` allow-list) and `committed_authorization_revision`.
- `ContentHeadCommitted` bodies carry the content revision, root and leaf digest, but **not values**.

Consequences:
- The IX-5a access projection can be derived from the journal alone.
- The IX-5b value rows must read the accepted content (Tier 1 sealed postings or the accepted leaf index) at the journaled content revision.

### R.4 Not run

| Plan | Why | Needed before |
|---|---|---|
| P-2 | Latency and throughput done at C-148; **call amplification done at C-149** (`INDEXED_SEARCH_C149_EVIDENCE.md` §5) | IX-5c |
| P-4 | **Done at C-149**, bounded: 40 users, 90 records, 300 s (`INDEXED_SEARCH_C149_EVIDENCE.md` §6). The 5,000-record and 500-user size was not run | IX-5c |
| P-5 | Measured on AC at C-148; the C-149 paired diagnostic (capped, deviating) is in `INDEXED_SEARCH_C149_EVIDENCE.md` §7. **R-02 FAIL** | IX-7 |
| P-6 | **Done at C-148 and C-149** (Tier 2 on and off; long-lived, fresh and seeded stacks), `INDEXED_SEARCH_C149_EVIDENCE.md` §1.3 | IX-5c |

**No result exposes a security defect or invalidates a design assumption.** TQ-6 (item identity stability) remains unverified; it is the first IX-5a deliverable.

