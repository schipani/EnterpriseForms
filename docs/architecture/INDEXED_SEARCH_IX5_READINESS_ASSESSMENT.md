# IX-5 (Tier 2) pre-implementation readiness assessment (C-137 §5)

- **Status:** assessment for owner decision. **No Tier 2 code, schema or migration has been written.** Prepared by Claude (Opus) under C-137 §5 for an owner-led review; not an independent assessment, and no independent review of Tier 2 has occurred.
- **C-138 (2026-11-08):** accepted by the owner (owner-led). IX5-D1 decided review-first, with no exception for a dark implementation; IX5-D2 to IX5-D6 approved as evaluation or design work only. The review preparation is [INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md](INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md) and its annexes A to E. IX-5 is not implemented and not accepted.
- **Branch / base:** `ix-1/authority-search-index`, after `37ba4f5`.
- **Conclusion in one line:** the approved IX-5 specification requires an independent review of the Tier 2 completeness design (§17.1) and a separate approval **before implementation**; that review has not taken place, so implementing IX-5 now would need an owner decision this assessment does not presume. Implementation therefore stopped at the readiness step (C-137 §5: "stop and request owner approval before implementing that portion").

## 1. Exact approved scope (design §16, IX-5 row)

| Item | Approved content |
|---|---|
| Content | Tier 2 access postings, in three steps: **dark** (maintained, never used to answer) → **differential** (compared with Tier 1) → **enabled**; the schema coupling guard |
| Design | §9.2 (table, indexes, `AccessPostingDerivation/v1`), §9.3 (query), §9.4 (completeness controls M4–M6), §9.5 (ceilings), §17.1 (M1 verified-revision fence, M2 rolling sweep, M3 affected-set tests) |
| Tests | IX-38 (divergence), IX-39 (coupling guard), IX-40 (differential), IX-41 (sweep), IX-52 (fence, affected-set property test, M2), IX-33 (Tier 2 counts and limits; moved to IX-5 by PD-5, R-24) |
| Exit gate | IX-38–41; property-based differential with 0 differences; online sampling clean "over the agreed period"; **100K/250K benchmarks** |
| Not in IX-5 | My Records (IX-6, IX-D12 deferred); Azure staging, Managed HSM, PERF-01 (IX-7); any change to Tier 1 behaviour, S1 or ENC-SR text (FD-5) |

## 2. Gate status (the blocking finding)

| Source | Text (abridged) | Status |
|---|---|---|
| IX-D13 (C-121) | Tier 2 design approved **for further review only**; "independent review and separate approval before implementation or enablement" | Review not done |
| R-B (C-121) | Conditionally accepted: "independent assessment of Tier 2 completeness and SpiceDB equivalence **before implementation**" | Not assessed |
| §17.1 status box | "Tier 2 is **not** to be implemented or enabled until this section has been independently reviewed and separately approved" | Not reviewed |
| FD-3 (C-122) | M1 approved for review only; "Tier 2 implementation stays unauthorized" | Unchanged |
| FD-8 (C-122) | Package ready for a qualified independent reviewer; **no review has occurred** | Unchanged |
| C-123 | Owner-led Tier 1 review; **Tier 2 not accepted** | Unchanged |
| Package UF-2 | "Tier 2 completeness (R-B) not independently assessed — blocks Tier 2" | Open |

C-137 authorizes IX-5 "within its previously approved specification". That specification contains the independent-review precondition. I have not treated C-137 as satisfying it, and I have not treated it as waived (C-137 §7; owner-review labelling rule). **Decision IX5-D1** below asks the owner how to proceed.

## 3. Dependencies

| Dependency | State |
|---|---|
| Tier 1 (IX-1 to IX-4): postings, entries, seals, sweeps, S1 pin and verify, `indexed-query`, fallback IX-D8 | Conditionally accepted (C-129, C-133, C-136, C-137). Tier 1 is Tier 2's oracle and fallback |
| Canonical edges `authority_relationship_edges` and the security-command transaction (organization lock, revision CAS) | Exists. Tier 2 adds derivation **inside** every edge-changing security command and every content commit (§9.2): it changes the trusted command path |
| SpiceDB `LookupSubjects` (M1 fence, M2 sweep) | **Not present** in `ISpiceDbProjection` (only Check, CheckBulk, CheckBulkStrict, LookupResources). A new client surface with the O-1 hardening (echo mapping, per-item error fails the call) would be needed |
| SpiceDB `LookupResources` (M2 user differential; online sampling) | Present (Tier 1) |
| FD-1 deadlines and suspension; FD-2 snapshot invalidation | Present (Tier 1); Tier 2 would add a verification scope (M1/M2) needing its own deadline rule |
| Seeding at 100K/250K for the exit-gate benchmarks | **Not feasible through the public API today** (R-37: about 2 records/s serially at 1,000 records, and slower as the organization grows; 100K ≈ 14 hours or more). Needs an R-37 change or an approved seeding method |
| Independent review (FD-8) | Not started |

## 4. Tier 1 guarantees that Tier 2 must preserve (reviewed)

1. **Authority is the only decision-maker.** Every disclosed result is decided by SpiceDB at an S1-pinned, verified snapshot (Tier 1: the population is SpiceDB `discover` and `read`). Tier 2 keeps per-result SpiceDB decisions (§9.3 step 4); access rows only narrow candidates.
2. **S1 organization quarantine** is checked at the capability call (423 `INTEGRITY_QUARANTINE`) before any engine, and never cleared or masked by an index quarantine or suspension (§17.2, FD-4).
3. **Integrity per result:** I-1 scope guard, I-2 SHA-256 binding to the accepted leaf and Merkle root, entry seals checked by sweeps, deadlines that suspend (FD-1, C-124 K1–K9), whole-search failure on any mismatch.
4. **Tenant isolation:** every posting, entry and snapshot row carries `organization_id`; RLS and the `authority_search_reader` role (IX-D2); no business identity reads AuthorityDb.
5. **Timing and limits (IX-D4, §8.2, IX-08/09/29):** candidate SQL is scope-driven; every count and limit refusal depends only on quantities the caller is authorized for; value-index-driven plans are prohibited. Since PD-9 (C-135) this is enforced structurally: the Tier 1 posting tables carry no value-keyed index.
6. **Stored results** (FD-2): bounded lifetime and count, per-page re-decision, cross-instance invalidation by triggers, no partial page.
7. **Fallback** (IX-D8): one search never combines engines; fallback passes every V1 and S1 gate.

## 5. R-38: Tier 2 index design and PostgreSQL 18 skip scan

**Finding R38-1.** The approved §9.2 index set for `search_access_postings` is value-keyed:
- `(organization_id, access_principal_entity_id, field_definition_id, <value column>, form_record_id)`, partial per value type;
- a GIN (`btree_gin` + `gin_trgm_ops`) on (principal, normalized string);
- `(organization_id, form_record_id)` for maintenance.

**Finding R38-2.** The same table is probed **by record**:
- in maintenance (derivation in security commands and content commits; rebuild);
- by the M1 verifier (per-record comparison with `LookupSubjects`);
- **in the caller-facing query**: §9.3 step 3 "Evaluable parity" checks, per candidate record and predicate field, that no posting of the record lacks an access row for the caller's principals.

**Finding R38-3 (PD-9 lesson).** On PostgreSQL 18 the planner used a B-tree **skip scan** over a value-keyed index for a per-record probe (§22.4), and there is no setting to disable skip scans. Removing the Tier 1 value indexes fixed Tier 1; it says nothing about Tier 2, which reintroduces value-keyed indexes by design.

**Finding R38-4 (where it matters).** The Tier 2 timing claim is "cost depends only on what the caller may see". It holds for a plan that enters through `(organization_id, access_principal_entity_id = ANY(P), field_definition_id, value)`. It does **not** hold automatically for:
- the parity probe, if planned through the maintenance index `(organization_id, form_record_id)`: its cost then depends on **all** principals' access rows for that record, that is on how many other users or teams the record is shared with (data the caller is not authorized to see);
- the parity probe or any record-scoped query, if planned as a skip scan of the value index across principals: cost then depends on other principals' postings for that field and value.

Maintenance and verifier probes are not caller-facing, so their plan choice affects performance but not disclosure. Command latency is observable only by the commanding user, for records it manages.

**Options for owner decision IX5-D2** (none implemented):

| # | Option | Timing property | Cost |
|---|---|---|---|
| A | Keep §9.2 indexes; require **every caller-facing predicate on access postings to bind the principal** (`access_principal_entity_id = ANY(P)` as leading equality), including the parity probe; fence record-only probes to Authority-internal paths; plan-shape tests across statistics and distributions (as PD-9), on PostgreSQL 18 | Holds only as long as the planner keeps principal-leading plans; a passing plan is not proof of every plan (C-135 §2) | Lowest change to the design |
| B | **Split the structure**: a value-keyed, principal-leading table used only for candidate narrowing, and a record-keyed table (no value column indexed) for maintenance, parity and verification; the caller-facing parity probe also principal-bound | Structural, like PD-9: no value-keyed index exists on any table a caller-facing record probe touches | Two derived tables to maintain in the command transaction; more plaintext rows (value copies stay in one table only) |
| C | **No value-keyed access index.** Tier 2 replaces only the SpiceDB enumeration: the caller's discoverable records come from principal-keyed access rows (no values), then the Tier 1 record-scoped probe runs as today | Same as Tier 1 (scope-driven, O(caller-accessible records) in SQL) | Removes the O(organization) SpiceDB lookup, but not O(\|D\|) SQL probing; may be sufficient at 100K–250K, unmeasured |

All three options keep the R-B completeness question: candidates still come from derived access rows, so M1–M3 and the independent review (IX5-D1) are needed whichever is chosen.

**Recommendation:** **B or C, not A.** A relies on planner behaviour that PD-9 showed to be unreliable. C is the smallest security surface and keeps Tier 1's proven plan shape; B keeps the §9.5 scaling goal. C should be measured first (it needs no value index at all). This is a design change to §9.2/§9.3 and needs owner approval and the independent reviewer's assessment (it changes the Q5 subject).

## 6. Invariants any Tier 2 implementation must satisfy (checklist for the reviewer and the tests)

| Area | Invariant |
|---|---|
| Authorization | Access rows never authorize: no result is disclosed without a SpiceDB decision at the S1-verified snapshot; a SQL-yes / SpiceDB-no divergence fails the whole search, disables Tier 2 for the organization and raises a Critical alert (IX-38) |
| Completeness | A Tier 2 search runs only if `access_verified_revision = authorization_revision` at the pinned snapshot (M1); otherwise Tier 1 within its ceiling or `SEARCH_INDEX_NOT_READY` |
| Derivation | `AccessPostingDerivation/v1` mirrors `authority.schema.zed` relation by relation (checked: the current `team`, `record_share`, `form_record`, `field_item` definitions match the §9.2 mapping); the schema-hash coupling guard disables Tier 2 on mismatch (IX-39) |
| Transactions | Derivation runs in the same AuthorityDb transaction as the edge change or content commit, under the organization lock, at the committed revision; a derivation failure aborts the command (never a silently stale row) |
| Tenant isolation | `organization_id` on every row; principals resolved only from the organization's canonical edges; team rows hold the team, never expanded members |
| Integrity | Tier 1 I-1, I-2, seals and sweeps unchanged; access rows are derived state rebuilt from canonical edges, never trusted for disclosure |
| Quarantine | S1 checked first; a Tier 2 divergence never downgrades an S1 quarantine (FD-4, Q4 escalation to S1 verification) |
| Timing | Every caller-facing statement's cost and every limit refusal depend only on quantities the caller is authorized for (IX-33); see §5 |
| Stored results | FD-2 unchanged; Tier 2 results go through the same snapshot and per-page re-decision |

## 7. Decisions requested (none presumed)

| # | Decision | Recommendation |
|---|---|---|
| **IX5-D1** | How to satisfy the IX-D13 / R-B / §17.1 precondition: (a) submit the review package (FD-8) and implement IX-5 only after the review and a separate approval; or (b) an explicit, recorded owner decision allowing a **dark, non-serving** IX-5a (derivation, M1 bookkeeping, differential harness; never used to answer a search) before the review, with enablement still requiring the review | **(a).** The open question is a design question (completeness of the affected-set rule and the fence), best answered before the trusted command path is changed. If (b) is chosen, it should be recorded as an owner exception to the sequencing, not as a review |
| **IX5-D2** | R-38 index design (§5 options A/B/C) | **C measured first; B if C does not meet the target; not A** |
| IX5-D3 | The "agreed period" for clean online differential sampling | Owner and reviewer to set; at least one full M2 cycle (7 days proposed) |
| IX5-D4 | How to seed 100K/250K records for the exit-gate benchmarks | Depends on R-37 (option B and/or A) or an approved seeding method that still goes through Authority commands; each run needs prior owner approval (over 30 minutes) |
| IX5-D5 | M2 interval (7 days proposed) and its deadline rule (FD-1 pattern) | Reviewer (Q5, J-6) |
| IX5-D6 | `LookupSubjects` client and the M1 cost (UF-12: changed records × approved fields) | Add with O-1 hardening; measure before enablement (PERF-01) |

## 8. What was not done

- No Tier 2 table, index, migration, derivation, verifier, query path or test was written.
- IX-33, IX-38–41 and IX-52 were not executed (they need Tier 2); R-24 stays open.
- No 100K/250K benchmark (not feasible today and over the 30-minute limit).
- No change to Tier 1, S1, the plan fence or any security assertion.
