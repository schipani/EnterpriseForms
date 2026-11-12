# Indexed search Tier 2 (IX-5): independent security and architecture review package

- **Status:** **finalized for handoff to a qualified independent reviewer (C-143; C-138 IX5-D1, review-first). NOT SUBMITTED. NOT REVIEWED. No review attestation exists.** This supplements the FD-8 package ([INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md](INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md)), whose question 5 (J-6) is the Tier 2 subject; it does not replace it. A reviewer receives both.
- **Independence notice:**
  * This package was written by the design agent (Claude, Opus) for the project owner.
  * Neither Claude nor the owner is independent.
  * Owner reviews to date (C-123, C-129, C-133, C-136, C-137) were **owner-led**.
  * Nothing here is a finding of an independent party, an approval or a certification. Statements marked "claim" are the design's claims, to be tested.
- **Review object:** the Tier 2 **design** (design §9, §17.1; A2 R-B, R-C) as refined by annexes A to E. **No Tier 2 code exists.** Tier 1 (IX-1 to IX-4) **is implemented** and conditionally owner-accepted for development (C-137). It is Tier 2's oracle and fallback, and its code is reviewable.
- **Baseline (C-144):** local branch `ix-1/authority-search-index`, not pushed. Latest code: `b4e2088` (C-143, R-52). Latest review documents before C-144: `8def00b`. **The revision under review is the signed commit that records C-144 in the decision log** (it adds this line, TQ-25 to TQ-27 and the evidence plans); the owner gives the reviewer that hash, and the attestation names it. History: first added on top of `1868e16`, revised under C-139 on top of `f7886b0`.
- **C-139 architecture direction (owner, 2026-11-09; owner-led, not an independent review):**
  * Option B is preferred **for independent review**, with separate access and searchable-value structures;
  * Option C is the comparison baseline and possible fallback;
  * Option A remains rejected.

  This is an architecture direction, not implementation approval, and it is **not** an independent security review. Under it, annex A §6–§15 evaluates B1 (canonical values), B2 (access-scoped values) and C. The design author's reading: B1 rejected, B2 preferred under conditions SAFE-1 to SAFE-11, C baseline and fallback.
- **C-140 (owner, 2026-11-09; owner-led, not an independent review):**
  * B2 is the preferred Tier 2 architecture **for the independent review**; C is the baseline and fallback; Tier 2 is not implemented;
  * six proposed decisions (FD-5 amendment, copy cap, partitions, command impact, no revealing search delays, sweep composition), recorded in §14A;
  * UF-25 investigated and reproduced (Tier 1; [INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md](INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md)).

  **This is not IX-5 implementation approval.**
- **C-141 (owner, 2026-11-09; owner-led, not an independent review):**
  * UF-25 is a Tier 1 defect. Its correction (U-1b) is **implemented** under C-141 with RQ-1 to RQ-8, with the approved search semantics unchanged. It is not independently reviewed (UF-25 document §8).
  * Directions:
    - B2 remains the preferred Tier 2 architecture for review; C remains the baseline and fallback;
    - **no arbitrary principal-copy cap is approved:** quantify the amplification and establish safe resource bounds first (§14A.3);
    - **logical tenant isolation is mandatory;** physical per-organization partitioning needs a justified operational design (§14A.4);
    - no verification catch-up wait in search requests (§14A.5);
    - partially shared field semantics are unchanged.
  * **FD-5 draft A1 to A6 is not approved as written** (§14A.2: the inconsistency, and alternative FD-5-ALT for owner decision).
- **Implementation still needs a separate owner approval after the review** (C-138 §2, C-139 §8, C-140, C-141, C-143).
- **C-143 (owner, 2026-11-10; owner-led, not an independent review):**
  * C-142 accepted: R-48 and R-51 corrected, the snapshot-lifecycle invariant restored.
  * R-47's delayed-detection model is accepted conditionally, subject to this review; no signature-format change.
  * **FD-5-ALT is approved as the preferred IX-5 design for independent review**, not for implementation.
  * B2 remains preferred, with C as the baseline.
  * No arbitrary copy cap and no physical per-organization partitions without evidence.
  * The existing sweep deadlines are retained until an approved replacement.
  * UF-25 stays open pending this review.
  * R-52 corrected (§14C).
  * **This package is finalized for external handoff. It has not been sent, and no independent review has occurred.** Cover note: [INDEXED_SEARCH_TIER2_REVIEWER_COVER_NOTE.md](INDEXED_SEARCH_TIER2_REVIEWER_COVER_NOTE.md).

## 0. Handoff index (where each review subject is)

| Subject | Where |
|---|---|
| B2 access-scoped value indexing; Option C baseline; B1 rejection; A rejected | Annex A §6–§15 (B2 §10, C §7 and §14, recommendation §15); §4 |
| SAFE-1 to SAFE-11 | Annex A §10.5; §14A.1 |
| FD-5-ALT asynchronous journal maintenance | §14A.2 (the A1–A6 inconsistency, then the FD-5-ALT security specification) |
| Authorization and content checkpoint consistency | §14A.2 FD-5-ALT table (checkpoints, consistency, recovery, replay) |
| Timing leakage from fallback and organization activity | §14A.2 (fallback timing risk), §14A.5, TQ-23; UF-28 (§9, §14A.3) |
| Multi-path field sharing | Annex A §10.3 and §11 (INV-T2-13), TQ-16; §14A.3a (the distinct-items rule implemented in Tier 1) |
| Revocation correctness | §14A.2 FD-5-ALT (revocation row), §14A.5, §10 (failure scenarios), §8 |
| Copy amplification | Annex A §13; §14A.3 (counting rules; no cap approved) |
| `LookupSubjects` limitations | Annex E §1, §5, §5A; E-6 and E-10 (§14) missing |
| Sweep and verification requirements | Annex D §2, §4A; §14A.6; design §17.10 (deadlines retained, C-143) |
| UF-25 residual risks | UF-25 document §8.6 and §9; register R-46 to R-51 |
| R-52 remediation evidence | §14C; evidence register R-52 |
| Open production and performance gates | §14B |
| Questions for the reviewer | §13 (TQ-1 to TQ-24) |


## 1. Documents

| Document | Role |
|---|---|
| [INDEXED_SEARCH_PROJECTION_DESIGN.md](INDEXED_SEARCH_PROJECTION_DESIGN.md) §9, §17.1, §17.2, §17.5, §17.10, §22.4–§22.5, §23 | Normative design; the Tier 1 implementation record |
| [ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md](../decisions/ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md) | R-B (completeness by derivation equivalence) and R-C (caps) |
| [INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md](INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md) | Scope, dependencies, Tier 1 guarantees, R-38 findings, invariants (C-137) |
| **Annex A** [INDEXED_SEARCH_TIER2_INDEX_ARCHITECTURE_ASSESSMENT.md](INDEXED_SEARCH_TIER2_INDEX_ARCHITECTURE_ASSESSMENT.md) | §1–§5: Option C against Option B (A rejected; C-138). §6–§15 (C-139): access index, the command-impact matrix over all contract kinds, B1/B2/C comparison, the sharing correctness matrix, the scaling model and the safety conditions |
| **Annex B** [INDEXED_SEARCH_TIER2_ONLINE_SAMPLING_PLAN.md](INDEXED_SEARCH_TIER2_ONLINE_SAMPLING_PLAN.md) | Proposed 14-day online sampling plan |
| **Annex C** [INDEXED_SEARCH_TIER2_BENCHMARK_DATA_DESIGN.md](INDEXED_SEARCH_TIER2_BENCHMARK_DATA_DESIGN.md) | 100K/250K synthetic dataset design |
| **Annex D** [INDEXED_SEARCH_TIER2_FULL_SWEEP_ASSESSMENT.md](INDEXED_SEARCH_TIER2_FULL_SWEEP_ASSESSMENT.md) | Seven-day full-sweep (M2) risk assessment |
| **Annex E** [INDEXED_SEARCH_TIER2_LOOKUPSUBJECTS_ASSESSMENT.md](INDEXED_SEARCH_TIER2_LOOKUPSUBJECTS_ASSESSMENT.md) | `LookupSubjects` client interface and cost model |
| [INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md](INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md) | C-140: UF-25 reproduction, impact assessment, correction U-1 and regression requirements; C-141: U-1b implementation, RQ-1 to RQ-8 results and residuals (§8) |
| [INDEXED_SEARCH_TIER2_REVIEWER_COVER_NOTE.md](INDEXED_SEARCH_TIER2_REVIEWER_COVER_NOTE.md) | C-143: cover note, the decisions requiring independent assessment |
| [INDEXED_SEARCH_IX5_EVIDENCE_PLANS.md](INDEXED_SEARCH_IX5_EVIDENCE_PLANS.md) | C-144: bounded plans P-1 to P-6 for the missing evidence (E-6, PERF-01, copy amplification, sharing workload, R-02, sweeps); **not run** |
| [INDEXED_SEARCH_IX4_EVIDENCE.md](INDEXED_SEARCH_IX4_EVIDENCE.md) §7, §10–§12, §15–§18 | The register (R-01 to R-52) and the measurements cited |
| [RECORD_REGISTRATION_THROUGHPUT_R37_RECOMMENDATION.md](RECORD_REGISTRATION_THROUGHPUT_R37_RECOMMENDATION.md) §7 | Ingestion findings (dataset feasibility) |
| `src/authority/AuthorizationAuthority.Infrastructure/SpiceDb/authority.schema.zed` | The permission model the derivation must mirror |
| `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`, `docs/security/PROJECTION_INTEGRITY_S1_OPERATIONS.md` | Revision, projection and S1 rules Tier 2 depends on |

The reviewer's independence requirements are those of the FD-8 package §2.

## 2. What Tier 2 is, in one paragraph (claim)

Tier 1 enumerates the caller's discoverable records in SpiceDB on every search (`LookupResources`, O(|D|), ceiling 50,000). Tier 2 replaces that enumeration with **access rows** that the Authority derives from its canonical relationship edges inside the same transactions that change those edges. SpiceDB still decides every disclosed result at the S1-verified snapshot, so access rows can only narrow candidates, never authorize them (§9.3). The price is relaxation **R-B**: completeness of the candidate population no longer comes from a per-search enumeration. It comes from derivation equivalence plus detection:
- **M1:** a verified-revision fence; Tier 2 serves only when every changed record has been compared with SpiceDB `LookupSubjects`;
- **M2:** a full rolling sweep;
- **M3:** affected-set property tests;
- **M4 to M6:** a property-based differential, online sampling and a schema coupling guard.

## 3. Changes since the FD-8 package was prepared

| Change | Effect on the review |
|---|---|
| Tier 1 implemented (IX-1 to IX-4), with ES256 entry seals, deadlines without grace, stored-result lifecycle, bounded seal transactions | Tier 2's oracle and fallback exist and can be inspected. Tier 1 tests pass on the local stack (evidence §12.5) |
| **PD-9 (C-135):** PostgreSQL 18 used a B-tree skip scan of a value-keyed index for a record-scoped probe; no setting disables skip scans; the value-keyed posting indexes were dropped | The §9.2 access-posting indexes (value-keyed, on a table also probed by record) are exposed to the same failure (R-38). Hence annex A |
| R-37 measurement (C-137) | 100K/250K datasets cannot be created through the trusted path in practical time without R-37 changes (annex C) |
| Design-author findings UF-14 to UF-21 (§9) | New items for the reviewer |
| **C-139 owner direction:** Option B preferred for review with separate access and value structures | Annex A §6–§15: B1 rejected (timing), B2 preferred under SAFE-1 to SAFE-11, C baseline and fallback; findings UF-22 to UF-27; questions TQ-13 to TQ-20 |

## 4. Proposed design under review (summary)

1. **Stages** (each needs its own authorization):
   - **IX-5a, dark:** derivation and M1 bookkeeping; never serves;
   - **IX-5b, differential:** dark plus sampling and M2;
   - **IX-5c, enabled.**
2. **Index architecture (C-139):** **B2 preferred for review.** B2 is a principal-leading access index (C's table) plus a physically separate, principal-leading, access-scoped value index, with controlled duplication rules D-1 to D-6 and the multi-path parity rule (annex A §10). C is the baseline and fallback (the access index alone, with the Tier 1 record probe). B1 (canonical values with a later authorization intersection) is rejected for timing (annex A §9). A remains rejected. **Not selected** (annex A §15).
3. **Derivation:** `AccessPostingDerivation/v1` mirrors `authority.schema.zed` relation by relation:
   - `form_record.effective_owner`, `discover`;
   - `field_item.read`;
   - `record_share.read`;
   - `team.participant` (teams are kept as principals and never expanded per member).

   It runs inside every edge-writing security command (and, under B2, value-row derivation runs inside content commits, builder backfill and purge), under the organization lock, at the committed revision. The command-by-command effect is annex A §8.
4. **M1 fence:** affected records are written to `search_access_pending_verifications` in the same transaction. A verifier compares them with `LookupSubjects` at an S1-pinned snapshot (annex E). Tier 2 serves only when `access_verified_revision = authorization_revision` at the pin.
5. **M2:** every record re-verified within 7 days (proposed), plus a user differential. Annex D proposes splitting it into M2-E (24 h edge re-derivation) and M2-S (7-day SpiceDB comparison).
6. **Query:**
   - the policy check and S1 pin;
   - the principal set `P` resolved from canonical edges;
   - candidates from the access rows;
   - SpiceDB bulk decisions on every match;
   - I-1, I-2 and S1 verify.

   A SQL-yes / SpiceDB-no divergence fails the search, runs the S1 decisive verification (Q4), and disables Tier 2 for the organization.
7. **Fallback:** Tier 2 not usable ⇒ the complete Tier 1 path within its ceiling, otherwise `SEARCH_INDEX_NOT_READY`.

## 5. Threat model (Tier 2 specific)

### 5.1 Actors and trust

| Actor | Trust | Relevant capability |
|---|---|---|
| Caller (authenticated user) | Untrusted | Chooses predicates; observes results, counts, refusals and timing |
| Business DBA or compromised business runtime (**Level 2 adversary**) | Untrusted | Arbitrary BusinessDb writes. **Not on the Tier 2 path**: access rows, edges and postings live in AuthorityDb |
| Authority runtime credential or AuthorityDb administrator | Trusted for Level 2. The design still bounds detection (OR-1, OBS-IX-1) | Can delete or insert access rows and edit canonical edges |
| SpiceDB projection | Trusted query engine; integrity checked by S1 | Lag, outage, snapshot expiry |
| Software defects | — | Derivation, affected set, verifier, planner |
| Operations | — | Restore, rollback to an old Authority version, multiple instances |

### 5.2 Threats

| # | Threat | Control | Residual |
|---|---|---|---|
| T2-01 | Disclosure through a stale or forged access row (over-inclusion) | SpiceDB decides every result; divergence fails the whole search | None for disclosure; an availability cost |
| T2-02 | **Omission** through a derivation defect on a changed record | M1 fence before use | Relies on the `LookupSubjects` oracle (annex E) |
| T2-03 | Omission through an affected-set defect | M3 tests; M2 | Window 7 days (14 until disable), or 24 h with M2-E (annex D) |
| T2-04 | Omission through Authority-domain deletion of rows after verification | M2 | Same window; Tier 1's analogue is 24 h (annex D §2) |
| T2-05 | Omission or false candidates through a tampered **value copy** (option B only) | Re-evaluate predicates on the I-2-bound posting; value-consistency sweep (UF-16) | Not in the original design |
| T2-06 | Timing or limit side channel through organization-wide data (PostgreSQL 18 plans, GIN) | Principal-leading keys only (C-S); no value column (C); no GIN (UF-15); architecture test | Planner settings still needed for the Tier 1 probe |
| T2-07 | Revoked access retained by a stored result | FD-2 (revision-bound snapshots, per-page re-decision) | Unchanged from Tier 1 |
| T2-08 | Tier 2 masks or downgrades an S1 quarantine | Q1 order (S1 first), Q3 independence, Q4 escalation | Reviewer check (J-10) |
| T2-09 | Schema drift between the derivation and zed | Coupling guard (hash) at startup and in CI | A semantics change without a hash change is impossible (the hash covers the file) |
| T2-10 | An old Authority version writes edges without derivation or pending rows | Minimum-safe-version fence (§12.4) extended to Tier 2 | **Not yet specified** (UF-20) |
| T2-11 | Partial restore: AuthorityDb restored without matching access rows, or the reverse | Access rows are derived: rebuild from edges and a full M2 before use; revision checks | G14 not implemented (UF-10) |
| T2-12 | The verifier accepts a partial `LookupSubjects` stream | Uncapped streams, client limit fails closed, end-of-stream status, de-duplication rules (annex E §4) | SpiceDB API behaviour to be re-verified per version |
| T2-13 | Fence starvation (Tier 2 never usable under write load), pushing large callers to `SEARCH_INDEX_NOT_READY` | Fallback | An availability risk (UF-18) |
| T2-14 | Derivation failure blocks security commands, a trusted-path availability risk | Dark-stage measurement; a kill switch that stops Tier 2 use without leaving stale rows trusted | UF-20, TQ-12 |
| T2-15 | Cross-tenant rows | `organization_id` keys, RLS, principals from the organization's edges, S-A cross-organization check | As Tier 1 |
| T2-16 | Planner statistics (histograms, most-common values, n_distinct, visibility state) cover unauthorized principals and other organizations, and shape plan choice (C-139) | SAFE-1 to SAFE-3 (one index, one single-table statement per condition: no plan choice for statistics to act on); SAFE-4 per-organization partitions | Reviewer judgment (UF-22). Buffer-cache effects are a general residual, as in Tier 1 |
| T2-17 | Omission through multi-path field readability (items readable only through different principals) | Parity by `field_item_count` and distinct `FieldItemRead` items across `P` (annex A §10.3) | M3 vectors needed (UF-23) |
| T2-18 | B2 value row tampered (Authority domain) to add or drop a candidate | Predicate re-evaluation on the I-2-bound posting (addition); M2-V daily re-derivation (omission); `indexed_operation_id` binding | Omission window 24 h, as Tier 1 S-B |
| T2-19 | Value-row amplification for records shared with many principals: storage, write latency under the organization lock | D-1 to D-5 (fan-out cap with C-path fallback); D-6 | Availability and cost (UF-26) |
| T2-20 | Tier 1 share-only check set and `items` limit depend on the presence and count of items the caller cannot read (implemented Tier 1) | B2 parity avoids it for Tier 2; C-141: corrected in Tier 1 (U-1b), not independently reviewed | Reviewer and owner (UF-25) |

## 6. Authorization and completeness invariants (to be proven by the reviewer's assessment and later by tests)

| # | Invariant |
|---|---|
| INV-T2-01 | No result is disclosed without a SpiceDB `discover` decision, and `read` or every predicate-field `field_item#read`, at the S1-verified pin |
| INV-T2-02 | Tier 2 serves a search only if `access_verified_revision = authorization_revision = projection_applied_revision` at the pin, and the S1 state is consistent |
| INV-T2-03 | Every committed change of an access-conferring edge (`RegisterRecord`, `TransferOwnership`, `CreateShare`, `RevokeShare`, `UnlinkChildRecord`, and any future edge-writing command) derives access rows and writes pending verifications **in the same transaction**, or the command aborts |
| INV-T2-04 | For every record r and principal p: an access row (r, p, scope) exists **iff** SpiceDB at the committed revision grants p `discover` on r (record scope) or `field_item#read` on the item (field scope), with teams compared at team level |
| INV-T2-05 | Affected sets are supersets of the records whose derived rows changed (M3 property) |
| INV-T2-06 | Every caller-facing statement on Tier 2 tables binds `access_principal_entity_id = ANY(P)` as an index condition of an index that leads with `(organization_id, access_principal_entity_id)`. No Tier 2 table has a value-keyed index readable without that prefix, and no GIN index is used |
| INV-T2-07 | Every count and limit refusal depends only on quantities the caller is authorized for (IX-33) |
| INV-T2-08 | A Tier 2 divergence or verification Fail runs the S1 decisive verification before any classification; a search-side state never clears or masks an S1 quarantine |
| INV-T2-09 | Verification timestamps are written only on Pass, for the exact unit (§17.10 D1, K1 to K9) |
| INV-T2-10 | Tier 1's I-1, I-2, seals, sweeps, deadlines and plan structure are unchanged |
| INV-T2-11 | No access row contains plaintext (option C). Under B, value rows are covered by the INV-T2-06 key rule, predicate re-evaluation and a value-consistency control |
| INV-T2-12 | (B2) Every value row (p, r, f, i) exists **iff** p reads item i through a single path (effective owner, or a share covering i), f has an Active approval with complete coverage, and the row's `indexed_operation_id` equals r's Tier 1 entry. `field_item_count` equals the number of f's posting items at that operation |
| INV-T2-13 | (B2) A share-only candidate is evaluable for f iff the distinct items of f readable through `P` equal `field_item_count` (Tier 1 parity, R1 §E.3) |
| INV-T2-14 | (B2) No match is disclosed unless every predicate re-evaluates true on the I-2-bound Tier 1 posting |
| INV-T2-15 | Every implemented command kind has a declared Tier 2 affected-set rule (annex A §8). A kind without one fails the architecture test; empty-affected-set kinds may advance `access_verified_revision` only under F-1 |

## 7. PostgreSQL 18 timing risks (summary; detail in annex A §2, §3.7)

1. **Skip scan** lets the planner use a multi-column B-tree without equality on its leading columns. A value-keyed index on a table probed by record therefore leaks value distributions (PD-9, observed). Its counterpart: **an index that leads with the principal bounds every plan by the caller's own rows.**
2. **Record-leading maintenance indexes** on caller-facing tables could be scanned organization-wide for a caller statement. Variant C-S removes them by using exact-key maintenance.
3. **GIN (btree_gin + trigram)** intersects posting lists of independent keys. The trigram list is organization-wide, so it is not bounded by principal (UF-15). It also needs bitmap scans, which are prohibited per statement.
4. **Generic plans, statistics drift, parallel plans and JIT** change plans without code changes. That is why the guarantee must be structural, with a plan-shape test across distributions and statistics as regression evidence only (C-135: a passing plan is not proof of every plan).
5. **Residual:** the Tier 1 probe still depends on per-statement settings (no hash, merge, bitmap or sequential scan) for its record-keyed access, as accepted in IX-4.
6. **Statistics and plan choice (C-139, UF-22):** table statistics are computed over every principal and, without partitions, every organization. Even when every available plan is bounded by the caller's rows, statistics choose **which** plan runs. B2 removes the choice structurally: one index per table, one single-table statement per condition, intersection in the application (SAFE-1 to SAFE-3). Per-organization LIST partitions (SAFE-4) isolate statistics and visibility state per organization.
7. **Canonical value indexes (B1):** any value-leading index on a table a caller statement reads lets the planner drive from unauthorized values, or skip-scan it for record probes (PD-9). B1 is rejected on this ground (annex A §9).

## 8. SpiceDB consistency requirements

| # | Requirement |
|---|---|
| SC-1 | All decision and verification reads use `at_exact_snapshot(pin)` from a verified S1 pin; every response token equals the pin (existing rule; annex E §1 for `LookupSubjects`) |
| SC-2 | The verifier reads access rows and edges in one AuthorityDb transaction whose `authorization_revision` equals the pinned marker's revision; otherwise Inconclusive |
| SC-3 | `projection_applied_revision ≥` the pending row's revision before it is verified |
| SC-4 | `LookupSubjects`: `EXCLUDE_WILDCARDS`; no conditional subjects; de-duplicate; client-enforced maximum; no partial results; whole-stream retry at the same pin (annex E §4) |
| SC-5 | Datastore change or snapshot unavailability (S1 E4/E5) is Inconclusive and escalated through S1 |
| SC-6 | The zed schema hash is embedded in the derivation version; a mismatch disables Tier 2 |
| SC-7 | No business-held ZedToken is ever a freshness proof (CROSS_SYSTEM_CONSISTENCY_PROTOCOL) |

## 9. Design-author findings new in this package (for the reviewer; not independent findings)

| ID | Finding | Where |
|---|---|---|
| UF-14 | **The §17.1 affected-set table does not match the command catalogue at `1868e16`.** There is no team-removal command; `UpdateShareScope` is declared but not implemented (`COMMAND_KIND_NOT_IMPLEMENTED`); `RegisterRecord` (owner and parent edges) and `UnlinkChildRecord` are not listed; `DisableIdentityBinding` changes no edges (it revokes sessions). Under option B, the approval commands (`ApproveFieldSearch`, `WithdrawFieldSearch`, `ReclassifyField`) and builder backfill and purge change value rows for every record of a form. The affected-set specification must be restated per command kind, with an architecture test that fails when a new edge-writing command kind lacks a rule | Annex A §2.5 |
| UF-15 | The §9.2 GIN (principal, trigram) index does not bound cost by principal (independent keys, TID intersection; the trigram list is organization-wide) and needs bitmap scans | Annex A §2.3 |
| UF-16 | Option B's value copies need predicate re-evaluation on the I-2-bound posting and a value-consistency control; M1 and M2 compare principals only | Annex A §3.4 |
| UF-17 | `LookupSubjects` has no server limit, no cursor and no bulk form, and may return duplicates; verification needs 2 × (1 + F) streams per record (§17.1 counted one per field item) | Annex E §1–§2 |
| UF-18 | **Fence starvation:** every security command advances `authorization_revision`, so sustained commands keep Tier 2 unused. The S-D 14-day disable threshold is inconsistent with the no-grace rule of §17.10 | Annex A §3.2, annex D §2 |
| UF-19 | The benchmark stacks run Vault in dev mode (in-memory), so a reusable dataset snapshot cannot carry its keys | Annex C §5.1 (R-43) |
| UF-20 | §12.4 minimum-safe-version fencing must cover Tier 2: an Authority version without derivation would commit edges with no access rows and no pending verifications | Annex A §3.9 |
| UF-21 | "Tier 2 disabled for the organization" is not a specified state: who sets and clears it, how it is journaled, and its precedence with `Quarantined`, `Suspended` and S1 | Annex A §3.6 |

**Added under C-139:**

| ID | Finding | Where |
|---|---|---|
| UF-22 | **Planner statistics are shared** across principals and, without partitions, across organizations. Structural bounds hold for each plan, but statistics select the plan. Mitigation: one index per table, one single-table statement per condition, application-side intersection (SAFE-1 to SAFE-3), and per-organization partitions (SAFE-4). Needs the reviewer's judgment on sufficiency | Annex A §10.5, §13.7 |
| UF-23 | **Multi-path field readability:** a caller can read the items of one field through different principals (direct share and team share). Per-principal completeness would omit records Tier 1 and SpiceDB include. Rule: per-item rows plus a stored `field_item_count`, compared with the distinct readable items across `P` | Annex A §10.3 |
| UF-24 | **Correction:** under the split design (B2), approvals, withdrawals and reclassification change no access row, so they do not reopen the SpiceDB fence. Value rows are verified by re-derivation, and availability is gated by value-row coverage. Annex A §3.2, §3.10 and annex E §5 overstated B's verification cost | Annex A §6.2 |
| UF-25 | **Implemented Tier 1 (for the reviewer and owner; no change made):** the share-only check set (`ReadPredicateItemsAsync`; R1 §E.3) is every posting item of each predicate field, including items the caller cannot read. Its size, which drives the number of `field_item#read` checks and the `items` limit (`MaximumShareOnlyItems`), therefore depends on the presence and count of values in fields the caller can discover but not read (for example, the number of selected options of an unshared multi-select). It is independent of predicate values, as R1 §E.3 states. The statement that limits are "caller-owned quantities" is imprecise for these items. **C-140: reproduced on the stack for direct, team and overlapping shares** (refusal 200 → 422 with a limit of 2; +42 ms p50 for 1,000 unauthorized items, informational; results unchanged). Correction U-1 proposed (C-140). **C-141: U-1b implemented** (candidates from the caller's grants, accepted per-field item counts, limit on readable candidates); not independently reviewed ([INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md](INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md) §8) | `IndexedSearchService.cs` at `f7886b0`, lines 114–132 (search) and 331–334 (page); `SearchQueryReader.ReadPredicateItemsAsync` |
| UF-26 | **Value-row amplification:** a record shared with many principals multiplies value rows (and content-commit writes under the organization lock) by its path-principal count. Proposed fan-out cap D-5, with C-path fallback for high-fan-out records. **C-140:** cap of 100 principals evaluated, with counting rules; the C-path fallback is withdrawn (UF-28) | Annex A §10.2, §13.3; §14A.3 |
| UF-27 | **FD-5 boundary:** B2 adds value-row writes inside Tier 1's writer transactions (W1 content commit, builder). Tier 1 query behaviour is unchanged, but FD-5 ("no change to Tier 1 behaviour" in IX-5) needs an explicit owner decision for these additive writes. C changes only the five edge-writing commands | Annex A §10.1, SAFE-11 |
| UF-28 | **(C-140) Overflow channel:** serving records above the copy cap through the C path makes the caller's search work depend on how many of their discoverable records exceed the cap, i.e. on share counts the caller is not authorized to see (the UF-25 class). D-5's overflow path is withdrawn; refuse at the cap or apply no cap | §14A.3 |

**Status of the C-138 findings (C-139 §5; none closed or waived):**

| Finding | Status after C-139 |
|---|---|
| UF-14 command impact | **Proposed resolution:** per-command matrix over every contract kind (annex A §8), with `UpdateShareScope` declared but unimplemented and no record-deletion command; INV-T2-15 architecture test. Open until reviewed and tested (M3) |
| UF-15 organization-wide trigram | **Proposed resolution:** no GIN in any option; `contains` as a secondary condition or a principal-bound scan; trigram B-tree deferred (TQ-17). Open |
| UF-16 value-copy integrity | **Proposed resolution:** INV-T2-14 (predicate re-evaluation), M2-V (24 h), `indexed_operation_id` binding. Open |
| UF-17 `LookupSubjects` limits, duplicates, 2 × (1 + F) | Unchanged API facts. Edge-informed F reduces calls (annex A §13.6; TQ-8). E-6 (deployed version) still missing. Open |
| UF-18 fence starvation and S-D grace | **Proposed:** F-1 (empty affected sets advance the fence) and F-2 (inline verification, bounded wait; TQ-15); S-D without grace (annex D). Open |
| UF-19 persistent signing keys for datasets | Unchanged; R-43. Open |
| UF-20 minimum-safe-version fencing | Unchanged; now also covers value rows (SAFE-10). Open |
| UF-21 Tier 2 disabled state | Unchanged; now also covers value rows. Open |
| Dataset preparation throughput (R-37) | Unchanged; annex C; B2 adds content-commit work (UF-26, UF-27). Open |

The FD-8 package's UF-1 to UF-13 and OR-1 to OR-4 remain. UF-2 (Tier 2 completeness not independently assessed) is the subject of this package; UF-12 is refined by UF-17.

## 10. Failure scenarios and required behaviour

| # | Scenario | Required behaviour |
|---|---|---|
| FS-01 | Derivation throws inside a security command | The command aborts (nothing committed); retryable error; alert. Tier 1 unaffected |
| FS-02 | Verifier unavailable, or SpiceDB down | Pending rows remain; the fence stays closed; Tier 2 not used; Inconclusive recorded |
| FS-03 | Verifier finds a difference | S1 decisive verification first. If S1 is consistent: Tier 2 disabled for the organization, rebuild from edges, re-verify; a persisting difference is a Critical finding |
| FS-04 | A search finds a SQL-yes / SpiceDB-no divergence | Whole search fails (`SEARCH_INDEX_INCONSISTENT`); Q4; Tier 2 disabled; never silently dropped |
| FS-05 | A `LookupSubjects` stream exceeds the client maximum | Inconclusive with a distinct alert; the record cannot pass; the owner adjusts the limit |
| FS-06 | The pin expires during verification (SpiceDB GC window) | Inconclusive; re-pin and restart the record |
| FS-07 | Concurrent command on a record under verification | Verification valid only up to its revision; the new pending row remains |
| FS-08 | Sustained commands keep the fence closed | Tier 2 unused; fallback; observable fence-lag metric (UF-18) |
| FS-09 | M2 overdue | Tier 2 disabled (no grace, if S-D adopts §17.10); cleared by successful completion of every overdue scope |
| FS-10 | Zed schema hash mismatch at startup | Tier 2 disabled; CI fails until the derivation version is bumped and the differential suite passes |
| FS-11 | Authority restarted mid-rebuild | The rebuild is not complete, and Tier 2 is not re-enabled until a complete rebuild and full M2 |
| FS-12 | An old Authority version starts against a Tier 2 schema | Refused by the minimum-safe-version fence (UF-20) |
| FS-13 | AuthorityDb restored to an earlier point | Revision checks; access rows rebuilt from restored edges and fully verified before use; G14 rules (unimplemented) |
| FS-14 | Access table lost or corrupted | Tier 2 disabled; rebuild from canonical edges (no HSM); Tier 1 unaffected |
| FS-15 | S1 organization quarantine while Tier 2 is active | 423 first (Q1); Tier 2, Tier 1 and pages refused; nothing in Tier 2 clears it |
| FS-16 | Search-index quarantine or suspension (Tier 1 state) | Tier 2 not served either (it relies on Tier 1 postings and entries) |
| FS-17 | Caller with very large `|D|` (option C) | Bounded by `statement_timeout` and admission; a refusal depends only on the caller's own `|D|` |
| FS-18 | Two Authority instances run the verifier | Idempotent per record and revision; no lease today (R-42) means duplicated work. To be specified |
| FS-19 | The kill switch turns derivation off | Tier 2 marked not maintained (journaled); re-enabling requires a rebuild and a full M2 |
| FS-20 | Field approval change (option B) | ~~Every record of the form pending; the fence is closed until verified~~ **C-139 (UF-24), for B2:** no access row changes; the builder backfills or purges value rows; the field is not used by Tier 2 until value coverage is complete (Tier 1 serves it meanwhile) |
| FS-21 | (B2) Value row disagrees with the I-2-bound posting at query time | Divergence: the search fails (`SEARCH_INDEX_INCONSISTENT`), Q4 first, then Tier 2 disabled for the organization and value rows rebuilt |
| FS-22 | (B2) M2-V finds a missing or extra value row | Fail per §17.10 classes: S1 first; Tier 2 disabled; rebuild from postings and verified access rows |
| FS-23 | (B2) A content commit fails in value-row derivation | The commit aborts (as FS-01 for commands): a record-save availability risk; dark-stage measurement before enablement |
| FS-24 | (B2) A record would exceed the copy cap | ~~Marked high-fan-out, value rows removed, C-path candidates~~ **C-140 (UF-28):** the C-path overflow is withdrawn. Under O-1 the `CreateShare` is refused with a typed error to the grantor; under O-2 there is no cap (§14A.3) |
| FS-25 | Fence closed when a search arrives | ~~Wait up to the bounded time~~ **C-140 decision 5:** no wait. Immediate fallback to Tier 1 within its ceiling, otherwise `SEARCH_INDEX_NOT_READY` |

## 11. Rollback strategy

| Level | Mechanism | Data effect |
|---|---|---|
| Stop serving Tier 2 | Configuration and organization state (Tier 2 disabled) | None; Tier 1 serves |
| Stop derivation (kill switch) | Journaled Authority operation marking Tier 2 not maintained | Rows go stale and are never trusted again without a rebuild and full M2 |
| Remove Tier 2 | Drop the tables through a forward-only migration (the PD-9 pattern) | Derived data only; nothing authoritative lost |
| Roll back the Authority image | Allowed only to a version that still derives, or after the kill switch; the minimum-safe-version fence refuses versions that do not derive while Tier 2 is maintained (UF-20) | — |

Not rolled back, ever: authorization revisions, revocations, journal entries, S1 state (CROSS_SYSTEM_CONSISTENCY_PROTOCOL).

## 12. Acceptance gates (proposed; each stage separately authorized)

| Stage | Exit gate |
|---|---|
| IX-5a dark | Unit and contract tests for the derivation, relation by relation, against zed; golden vectors; M3 affected-set property tests (per command kind, UF-14); architecture tests (INV-T2-03 every edge-writing command derives; INV-T2-06 index keying; no GIN; no value column under C); fault injection FS-01, FS-11, FS-12, FS-19; security-command latency and lock-hold delta measured (R-37 interaction); SEC-P and IX-1 to IX-4 suites unchanged and passing. **C-139 additions:** INV-T2-15 command-matrix architecture test. If B2 is approved: INV-T2-12 to 14 golden vectors; multi-path parity vectors (UF-23); M2-V; content-commit latency and lock-hold delta (UF-27); fan-out transitions (FS-24); SAFE-1 to SAFE-3 architecture tests |
| IX-5b differential | IX-38 (divergence), IX-39 (coupling guard), IX-40 (property-based differential, 0 differences), IX-41 (M2), IX-52 (fence, affected sets, M2); annex E outcome-class tests; plan-shape tests across distributions on PostgreSQL 18; IX-33 (counts and limits); online sampling per annex B (period subject to review) |
| IX-5c enabled | 100K and 250K benchmarks on datasets prepared per annex C (verified after restore); PB-09 search targets reported (not assumed); fence-lag and verifier throughput measured; independent review findings dispositioned; owner approval |
| Production | IX-7 (Azure staging, Managed HSM, PERF-01, DR with G14), independent implementation review, PB-12 evidence. **Not in IX-5** |

## 13. Questions for the reviewer

| # | Question | Annex |
|---|---|---|
| TQ-1 | Is R-B (completeness by derivation equivalence plus M1 to M6) acceptable for this product's search, in place of per-search enumeration? Is the Tier 2 design sufficient for separate approval (J-6)? | — |
| TQ-2 | Is the C-S keying rule (principal-leading indexes only, exact-key maintenance) structurally sufficient on PostgreSQL 18? Is any plan for a caller statement not bounded by the caller's rows? Option C or B? | A |
| TQ-3 | Online sampling: is 14 days sufficient; are the scenario coverage and positive controls sufficient? | B |
| TQ-4 | Is the M2 omission window acceptable compared with Tier 1's 24 h? | D |
| TQ-5 | Split M2 (M2-E 24 h, M2-S 7 days)? S-D without grace? | D |
| TQ-6 | Are field-item identities (`manifest_item_id`) stable across content revisions, so that share-covered rows are unaffected by content edits, and new items excluded by default? (Not verified by the design author) | A |
| TQ-7 | Fence starvation: acceptable? May a command with an empty affected set advance `access_verified_revision` in its own transaction? A pending-record side path would make cost depend on organization-wide activity: is that acceptable? | A, E |
| TQ-8 | Per-item `LookupSubjects` oracle, or the decomposed form? | E |
| TQ-9 | May an isolated PostgreSQL 18 planner and cost microbenchmark (synthetic rows, not through the Authority, never gate evidence) be used to inform the C/B decision early? | A, C |
| TQ-10 | Behaviour for records whose subject count exceeds the client limit | E |
| TQ-11 | Verification at the projection revision under concurrent commands | E |
| TQ-12 | Trusted-path change: is aborting security commands on derivation failure the right trade? Kill-switch semantics; minimum-safe-version fencing | A §3.9 |
| TQ-13 | (C-139) Do you agree that B1, canonical values with an authorization intersection, cannot meet the timing invariants on PostgreSQL 18 in any value-first or mixed-index form, and is rejected? Is any B1 variant (for example B1-G access classes) worth pursuing? | A §9 |
| TQ-14 | Are SAFE-1 to SAFE-11 sufficient for B2? In particular: does "one index, one single-table statement per condition" remove the statistics channel (UF-22)? Are per-organization partitions required? | A §10.5 |
| TQ-15 | Fence availability: F-1 (empty affected sets advance the fence), and F-2 (inline verification with a bounded search wait, which reveals recent organization security activity)? | A §12 |
| TQ-16 | Multi-path parity: is the `field_item_count` rule (annex A §10.3) correct and complete against the zed union semantics, including multi-select fields and repeating groups? | A §10.3 |
| TQ-17 | `contains`: is a secondary-condition or principal-bound-scan strategy acceptable for V1 Tier 2, or is a principal-leading trigram B-tree needed (storage × value length)? | A §10.4 |
| TQ-18 | Fan-out cap D-5: is a C-path fallback for high-fan-out records acceptable, and what K? | A §10.2 |
| TQ-19 | UF-25 (implemented Tier 1): is the dependence of the share-only check set and the `items` limit on unreadable items acceptable? If not, what change is appropriate (outside IX-5 scope, FD-5)? | §9 |
| TQ-20 | Value consistency: is M2-V at 24 h plus per-match re-evaluation sufficient, or should value rows carry a seal? | A §10.5, D |
| TQ-21 | (C-140) Copy cap: are the counting rules of §14A.3 right? Overflow O-1 (refuse at the cap), O-2 (no cap), or another option without a search-side channel (UF-28)? | §14A.3 |
| TQ-22 | Partitions: is SAFE-3 sufficient without per-organization partitions? If partitions are required, is the privileged provisioning step at onboarding acceptable (G08, PB-13)? | §14A.4 |
| TQ-23 | Is the immediate Tier 2 → Tier 1 fallback on a closed fence observable as organization activity, and is that acceptable (decision 5)? | §14A.5 |
| TQ-24 | UF-25 (implemented Tier 1): is correction U-1b as implemented under C-141 sound and sufficient (count integrity by recount against the sealed postings, the 2→3 upgrade path, the residual of §8.6), and are RQ-1 to RQ-8 and their results the right regression evidence? Should the result-level effect (§4.3 of the UF-25 document) stay as specified? | UF-25 document |
| TQ-25 | (C-144) FD-5-ALT as a whole: does journal-only maintenance with compare-and-set checkpoints, digest continuity and readiness at exact revision equality guarantee that Tier 2 never serves a revoked grant, an unaccepted content revision or a replayed journal segment, including across crashes, restores and concurrent maintainers? | §14A.2 |
| TQ-26 | (C-144) R-47: is delayed detection of an inflated stored field-item count (detected by the S-B/S-C recount and the page seal, not at search time) acceptable without a signature-format change, given what it can and cannot affect before detection? | UF-25 document §8.6, §9 |
| TQ-27 | (C-144) R-52: is suppressing an unchanged incomplete attestation by its journal-recorded state digest correct (no suppressed state change, no false attestation, no quarantine bypass, correct across instances and restarts), and is trusting the latest journal body digest acceptable? | §14C |

## 14. Evidence the reviewer will need

| # | Evidence | Available |
|---|---|---|
| E-1 | Tier 1 source (Authority `SearchIndex` feature, migrations, query path) and tests | Yes, at the baseline commit |
| E-2 | `authority.schema.zed` and `CommandPlanner*.cs` (every edge-writing command) | Yes |
| E-3 | PD-9 plan evidence (design §22.4–§22.5; evidence §10) | Yes |
| E-4 | R-37 and R-39 measurements (evidence §11–§12) | Yes |
| E-5 | Vendored SpiceDB API (`Protos/authzed`, `VENDORED_FROM.txt`) | Yes |
| E-6 | SpiceDB `LookupSubjects` behaviour on the deployed version (limits, cursors, uniqueness) | **No:** to be tested at IX-5a |
| E-7 | Derivation golden vectors and M3 property-test results | **No:** IX-5a |
| E-8 | Plan-shape evidence for Tier 2 statements on PostgreSQL 18 | **No:** IX-5a/b |
| E-9 | Option C probe cost at large `|D|` | **No:** annex C datasets, or the TQ-9 microbenchmark if approved |
| E-10 | `LookupSubjects` latency and throughput (PERF-01) | **No:** IX-5b and IX-7 |
| E-11 | Online sampling evidence | **No:** IX-5b |
| E-12 | G14 recovery behaviour | **No:** G14 not implemented (UF-10) |
| E-13 | Command-impact matrix verified against `CommandPlanner*.cs` (annex A §8) | Design author's reading only; **reviewer to confirm** |
| E-14 | B2 value-row and access-row cardinality and write cost at 100K/250K (annex A §13) | **No:** estimates only; annex C datasets and IX-5a measurement |
| E-15 | Plan-shape and statistics-sensitivity evidence for B2 statements, including with per-organization partitions | **No:** IX-5a/b |

Bounded plans for E-6, E-10 (PERF-01), E-14, the sharing workload, R-02 and the sweep deadlines: [INDEXED_SEARCH_IX5_EVIDENCE_PLANS.md](INDEXED_SEARCH_IX5_EVIDENCE_PLANS.md) (C-144; not run, each needs owner authorization).

## 14A. C-140 owner decisions and the resulting review requirements

**Owner decisions (C-140, 2026-11-09; owner-led, not an independent review, not IX-5 implementation approval).**
- C-139 (`22732d8`) accepted.
- **B2 is the preferred Tier 2 architecture for the independent review;** C is the baseline and fallback.
- Tier 2 is not to be implemented.
- Six proposed decisions are recorded below. **None waives an existing security gate.**

### 14A.1 B2 security assumptions (for the reviewer)

B2 is safe only if **all** of SAFE-1 to SAFE-11 hold (annex A §10.5):
- the structural keying and the single-statement rule (SAFE-1 to SAFE-3);
- per-organization statistics isolation, or an accepted alternative (SAFE-4, §14A.4);
- no authority in value rows (SAFE-5);
- multi-path parity (SAFE-6);
- value consistency (SAFE-7, SAFE-8);
- the plaintext inventory (SAFE-9);
- fencing and the disabled state (SAFE-10);
- the FD-5 amendment (SAFE-11, §14A.2).

B2 additionally assumes:
- the Authority domain is trusted (Level 2);
- SpiceDB at the S1-verified pin is the authorization oracle;
- canonical edges and SpiceDB are equivalent whenever S1 is consistent.

**Unresolved PostgreSQL timing risks:**
- UF-22, statistics and plan choice: mitigated structurally, needs the reviewer's judgment;
- the per-statement planner settings Tier 1 already relies on;
- buffer-cache and visibility effects (general residual);
- `contains` (UF-15, TQ-17);
- the overflow-path channel UF-28 (§14A.3).

**Multi-path authorization correctness:** annex A §10.3 (INV-T2-13, TQ-16), and the same distinct-items rule in the proposed UF-25 correction ([INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md](INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md)).

**SpiceDB verification cost and `LookupSubjects` behaviour:**
- annex E §1 (no limit, no cursor, no bulk, duplicates), §5 and §5A;
- evidence E-6 (deployed-version behaviour) and E-10 (PERF-01) are still missing.

**Integrity and completeness:**
- INV-T2-01 to INV-T2-15;
- M1, M2-E, M2-V, M2-S (§14A.6, annex D §4A);
- predicate re-evaluation (INV-T2-14);
- fail closed throughout (§10).

### 14A.2 FD-5 amendment (decision 1): draft for the review, not approved

**Proposed text.**
- **Scope.** IX-5 may add Tier 2 maintenance (access-row and value-row derivation and writes) inside the Tier 1 writer transactions:
  * the W1 content commit (`ContentRevisionCoordinator`);
  * builder backfill, purge and rebuild;
  * the five edge-writing security commands.
- **Conditions:**
  * **A1:** Tier 1 query behaviour, postings, entries, seals, sweeps, deadlines and plan structure are unchanged (INV-T2-10).
  * **A2:** Tier 2 maintenance is behind a configuration switch, off by default, and Testing/Development-only until separately approved.
  * **A3:** a Tier 2 derivation failure never aborts, delays beyond a measured bound, or changes the outcome of a Tier 1 commit or of a security command. The Tier 2 work runs under a savepoint. On failure it is rolled back to the savepoint, and the same transaction marks Tier 2 **not maintained** for the organization (journaled, the UF-21 state). Tier 2 then stays unused until a rebuild and a full M2 succeed. *(This replaces FS-01/FS-23's "the command aborts" for the record-save and command paths; it is the reviewer's choice between the two, TQ-12.)*
  * **A4:** the organization-lock hold and latency delta of each affected path is measured at IX-5a and reported against PB-09. No new threshold is introduced without owner approval.
  * **A5:** golden comparison tests prove that Tier 1 outputs (postings, entries, seals, journal entries, revisions) are byte-identical with Tier 2 maintenance on and off.
  * **A6:** minimum-safe-version fencing (UF-20) covers the switch.
- **Not authorized by the amendment:** any change to Tier 1 transaction behaviour now (C-140 decision 1), and any Tier 2 query use.

**C-141: A1 to A6 are not approved as written.** The owner identified an inconsistency; the design author's analysis follows (owner-led, not independent).

*The inconsistency.* The draft puts Tier 2 maintenance **synchronously inside** the Tier 1 writer transactions, and also claims that Tier 2 cannot delay or change Tier 1. Those cannot both hold:
1. **Latency and lock hold.** Synchronous work is on Tier 1's critical path, under the organization lock that serializes every commit and security command of the organization. B2's write amplification is principals × approved items per record (§14A.3), so a share, revocation, ownership transfer or record save can rewrite many value rows inside that lock. A3's "never delays beyond a measured bound" is self-referential: the bound comes from the measurement, so it is a cost, not a guarantee. Revocations are delayed with everything else.
2. **Failure coupling.** A savepoint isolates statement errors, but not everything:
   - lock waits, timeouts and connection loss still affect the enclosing transaction;
   - the "not maintained" row that A3 writes is itself a new write in Tier 1's transaction.
3. **A Tier 1 behaviour change.** The savepoint, the extra rows and the journaled "not maintained" state change Tier 1's transactions. That contradicts both the amendment's own exclusion ("no change to Tier 1 transaction behaviour now") and A1/A5's byte-identical claim for journal entries.

*Alternative for owner decision (FD-5-ALT; not implemented, not reviewed).* Tier 2 maintenance writes **nothing** in any Tier 1 transaction:
- **Maintainer.** An Authority-internal maintainer consumes the Authority's existing ordered change sources:
  * the authorization revision journal (edge writes);
  * the content-integrity revision with its content heads (commits).

  It writes Tier 2 rows in its own transactions, and records Tier 2's maintained-through authorization and content revisions.
- **Fence.** A query may use Tier 2 only when both maintained-through revisions equal the pinned revisions and M1 holds (§14A.5, decision 5). Otherwise it falls back to Tier 1 immediately.
- **Effect on Tier 1.** Its transactions, cost, failure modes and outputs are exactly unchanged, so A1/A5 hold trivially. A Tier 2 failure only disables Tier 2.
- **Cost.** Tier 2 lags every write, so the fence is closed more often, and whether it is closed depends on organization-wide activity. The immediate fallback is therefore more frequent. That makes TQ-23 (is the fallback observable?) central, and the fallback rate must be measured at IX-5a.
- **Revocation.** Revocations are never stale: until consumed, Tier 2 is fenced off and Tier 1 decides at the pin.
- **Requirements that would replace A1 to A6:**
  * no Tier 1 transaction change;
  * a switch, off by default (A2);
  * minimum-safe-version fencing (A6);
  * a measured fallback rate and maintainer lag at IX-5a;
  * the TQ-23 analysis.

The synchronous design remains an option **only** if the claim is restated as a bounded, measured cost with an owner-approved budget, and A3's savepoint and state rows are approved as a Tier 1 change. TQ-12 and TQ-23 are extended to this choice.

**C-142: FD-5-ALT is the preferred architecture for the independent review** (owner direction; not approved for implementation, not implemented). Security design summary (design author; for the reviewer):

| Concern | Specification |
|---|---|
| Change sources | Per organization, the Authority's existing hash-chained journal streams (`JournalChain`, `DynamicForms/SecurityJournal/v1`, each event carrying `stream_revision` and `previous_digest_hex`): the **authorization** stream, whose revision is `authorization_revision` (every security command that changes edges, ownership, shares, teams, roles or approvals), and the **content** stream, which tracks `content_integrity_revision` (every content commit and index-affecting content event). Tier 2 maintenance reads them in stream order. It never reads business data, and it writes nothing into any Tier 1 transaction |
| Durable ordered checkpoints | One checkpoint row per (organization, stream): maintained-through revision and that event's digest. It is advanced only by compare-and-set from revision *n − 1* to *n*, in the **same transaction** as the Tier 2 rows derived from event *n*. The event is accepted only if its `previous_digest_hex` equals the checkpoint digest. Checkpoints are the maintainer's own state, owned and granted like the Tier 1 state tables |
| Content and authorization consistency | Tier 2 may serve a query pinned at (A, C) only if, in the same reader snapshot: authorization checkpoint = A, content checkpoint = C, both checkpoint digests equal the organization's latest journal digests, Tier 2 is Active, Tier 1 is servable, and M1 holds. Any inequality means Tier 2 is not ready |
| Recovery after process failure | Restart from the checkpoints. Re-processing an applied event is refused by the compare-and-set (idempotent). A missing or out-of-order event, a digest mismatch, or a checkpoint ahead of the organization state sets Tier 2 to **not maintained** (journaled, alert) until a rebuild from Tier 1's verified state and a full M2 succeed. Never skip or guess |
| What invalidates readiness | **Every** authorization-stream and content-stream event, because readiness is exact revision equality. Events proven not to affect Tier 2 (decision 4, annex A §8, INV-T2-15) are consumed cheaply by advancing the checkpoint without row changes; they do not stay "ready" by exemption |
| Quarantine | An S1 quarantine closes the organization gate first: no Tier 1 or Tier 2. A Tier 1 search-index quarantine means Tier 2 is unused. A Tier 2 divergence (M1/M2, a contradicted decision under Q4) quarantines Tier 2 only (after the S1 check); queries fall back to Tier 1 if Tier 1 is servable, otherwise they are refused |
| Revocation | A revocation advances `authorization_revision`. Until Tier 2 has consumed it, its checkpoint is behind the pin, so Tier 2 is not ready and the query is served by Tier 1 at the pin. No stale grant can be served by Tier 2 |
| Fallback | Immediate Tier 1 within its ceilings, or a typed refusal (`SEARCH_INDEX_NOT_READY`). **Never a wait** for Tier 2 to catch up (decision 5) |
| Stale or incomplete replay | Defended by: compare-and-set on revisions; digest continuity; checkpoints bound to the journal head digests; idempotent derivation keyed by event revision; refusal of any event other than checkpoint + 1. After a restore of AuthorityDb or Tier 2 storage, the checkpoints disagree with the organization's revisions or digests, so Tier 2 is not ready until rebuilt. Authorization rollback protection stays with S1/G14 |
| Fallback timing risk | Whether a query is served by Tier 2 or Tier 1 depends on organization-wide write activity (any command or commit closes the fence until consumed), and the two paths have different costs. A caller may therefore infer the organization's recent activity rate from latency. It does not reveal content or other callers' authorizations. This is TQ-23; the fallback rate and latency difference must be measured at IX-5a, and the reviewer must accept the residual or require mitigation (for example, serving every query of an organization from one engine for a period) |

### 14A.3 Copy cap (decision 2): 100 principals per record, evaluated, not enforced

**How principals are counted (proposed):**
- A **principal** is a user or a team. A team counts once, however many members it has (D-1).
- **Path principals of an item:** the effective-owner principal, plus each distinct share recipient whose share covers the item.
  * The same principal reached through two shares counts once. Value rows are keyed by (principal, item), not by share, so a revocation removes a row only when no other share of that principal still covers the item. Rows are re-derived from edges.
  * Different principals (a user and a team the user belongs to) count separately: they are separate rows, and the union happens at query time.
- **Record count** = the number of distinct path principals over the record's **approved-field** items. Discover-only shares and shares covering only unapproved fields produce no value rows and do not count. They still produce access rows, which are never capped.
- **Field-specific permissions** change the row count (Σ over items of path principals), not the principal count. The value-row total per record (≤ 100 × F_a × k̄ at the cap) is reported as a secondary figure.

**At the candidate of 100** (base case, annex A §13): about 0.1 % of records in the heavy-sharing scenario exceed it; in the base case essentially none. Unmeasured.

**Overflow behaviour (new finding UF-28).** Annex A's D-5 (high-fan-out records served by the C path) makes the caller's search work depend on how many of their discoverable records are shared with more than K principals. Those share counts are not authorized to a recipient, so this is the same class of channel as UF-25. **D-5 as proposed in annex A is therefore withdrawn as a recommendation.** The alternatives, for the owner and reviewer:

| Option | Behaviour | Security | Cost |
|---|---|---|---|
| O-1 Refuse at the cap | `CreateShare` that would exceed the cap is refused with a typed error, visible to the grantor (owner or manager), who is entitled to manage access | No search-side channel | A product limit on sharing |
| O-2 No cap | Value rows for every path principal | No channel | Storage grows with sharing (annex A §13.2) |
| O-3 Organization-level switch | The organization (or form) leaves B2 for C when any record exceeds K | The switch depends on organization-wide sharing: a coarse channel | Simple |

The design author's reading: O-1 or O-2. Not enforced; no decision.

**C-141:** no arbitrary cap (100 or any other) is approved. Before implementation, the review and IX-5a must:
- quantify the amplification: value rows per record and per command, as a function of principals × approved items × selections;
- establish safe resource bounds from measurement: storage, write time under the organization lock, and sweep cost.

### 14A.3a UF-25 correction and its bearing on B2

- The Tier 1 fallback no longer has the UF-25 dependence (§14A.5 residual).
- The distinct-items rule that B2 needs (INV-T2-13) is now implemented in Tier 1:
  * `ShareOnlyReadability`, with candidates from the caller's own grants and the accepted per-field item counts;
  * tested by RQ-1, a differential against the R1 rule over 600 generated configurations, and on the stack for direct, team and overlapping paths.
- B2's access rows would need the same count source, `derived_field_item_counts`, or an equivalent. The reviewer should check it against TQ-16.

### 14A.4 Partitions (decision 3): tenant isolation required; per-organization partitions evaluated

- **Required (unchanged from Tier 1):** `organization_id` leading every key, RLS, and principals from the organization's own edges. Partitions are **not** what provides tenant isolation. They address statistics isolation (UF-22) and per-organization maintenance.
- **Per-organization LIST partitions, viability:**

| Factor | Assessment |
|---|---|
| Creating a partition per new organization | Needs DDL at onboarding. The runtime identity has no DDL rights (G08). It needs a privileged provisioning step (JIT migration identity, or a pre-provisioning job), which is a new operational dependency on the onboarding path (PB-13) |
| Partition count | First release ≈ 25 organizations (PB-09): viable. Hundreds: planning and lock overhead to be measured. Thousands: likely not advisable without measurement |
| Offboarding (PB-13) | Advantage: detach or drop one organization's partition |
| Statistics | Isolated per organization: removes the cross-organization part of UF-22 only, not the cross-principal part (still handled by SAFE-3) |

- **Alternative:** fixed HASH partitions (for example 32 or 64) by `organization_id`. No per-organization DDL, but organizations share statistics within a partition.
- **The design author's reading:**
  * SAFE-3, which leaves the planner no choice, is the primary control;
  * LIST partitions are viable for the first-release scale only with a privileged provisioning workflow;
  * HASH partitions, or none, at larger scale, if the reviewer accepts SAFE-3 as sufficient (TQ-14).
- Not decided.
- **C-141:** logical tenant isolation (above) is mandatory. Physical per-organization partitioning is adopted only with a justified operational design covering:
  * the privileged provisioning step at onboarding (G08, PB-13);
  * offboarding;
  * partition-count limits from measurement;
  * monitoring;
  * failure handling when a partition is missing.

### 14A.5 Security commands and fence (decisions 4 and 5)

- **Decision 4.** Tier 2 may remain usable after a command **provably** not affecting its indexed authorization dependencies. Annex A §8 is the complete impact analysis over every contract kind. The proof obligations are:
  * the INV-T2-15 architecture test (every implemented kind has a declared rule);
  * an M3 property test that, for each empty-affected-set kind, the derived access rows and value rows are identical before and after the command;
  * F-1 only when no pending verification exists.
- **Decision 5.** **No search delay that reveals unrelated organization security activity.** F-2's bounded search wait (annex A §12) is withdrawn. Inline verification after a command may still shorten fence lag, but a search that finds the fence closed **falls back immediately** to Tier 1 within its ceiling, otherwise `SEARCH_INDEX_NOT_READY`. FS-25 is changed accordingly.
- **Residual (for the reviewer):** whether the immediate fallback is itself observable. Tier 1 and Tier 2 costs differ, and the fence state depends on organization-wide command activity. The design author notes this as the same question as F-3 (TQ-7, TQ-15). The fallback is served by the complete Tier 1 path, whose own cost depends only on the caller's records, since the C-141 correction of UF-25 (UF-25 document §8.6 lists the residuals).

### 14A.6 Full sweep (decision 6): daily local reconciliation and 7-day SpiceDB verification, no grace

Evaluated against the existing requirements (annex D §2, §4A):

| Requirement | M2-E and M2-V daily, M2-S 7 days, no grace |
|---|---|
| §17.10 no-grace deadlines (C-123, C-124) | Consistent: the deadlines are the comparison values, with the D1 to D8 outcome classes and per-record timestamps |
| Tier 1 S-B 24 h for posting changes and deletions | Consistent: M2-E and M2-V bound access-row and value-row omissions at 24 h |
| Tier 1 S-C 30 days for derivation defects | Consistent: M2-S at 7 days is stricter |
| Revocation and freshness | Not M2's role (per query and M1) |
| Availability | An overrun disables Tier 2 (fallback to Tier 1), without grace |

The 14-day disable threshold of §17.5 S-D would be removed. **Proposal only;** production defaults unchanged.

### 14A.7 Production-scale sharing workloads

Annex A §13 (estimates):
- base, heavy-sharing and high-field cases at 100K, 250K and 1M records per organization;
- 2.5M records across organizations;
- the fan-out dataset shape (annex C §5A);
- online sampling scenarios S-23 to S-28 (annex B §7A).

All are unmeasured. The 100K and 250K gates are unchanged.

## 14B. C-142 owner-review checklist: mandatory decisions before any IX-5 implementation

B2 remains the preferred architecture and C the comparison baseline. **The independent-review requirement is not satisfied: the package has not been submitted, and no review has occurred.**

| # | Decision or evidence | Status |
|---|---|---|
| 1 | Independent security and architecture review of this package (TQ-1 to TQ-27, annexes A to E, the UF-25 document) | **Not started** |
| 2 | FD-5: FD-5-ALT (§14A.2) accepted as the maintenance model, including its checkpoint design and the TQ-23 fallback-timing residual | **Approved as the preferred design for review (C-143)**; not approved for implementation |
| 3 | Copy amplification: measured value-row and write bounds; no cap approved (§14A.3) | Open; no arbitrary cap without evidence (C-143) |
| 4 | Partitioning: logical isolation mandatory; physical partitions only with an operational design (§14A.4) | Open; not required without evidence (C-143) |
| 5 | Sweep composition and deadlines (§14A.6) | Existing deadlines retained until an approved replacement (C-143) |
| 6 | SpiceDB evidence: `LookupSubjects` on the deployed version (E-6), PERF-01 | Missing |
| 7 | UF-25 closure: R-46 (review), R-47 delayed detection, R-48 boundary, R-50, R-51, R-52 | R-47 conditionally accepted subject to this review (C-143); R-48, R-51 (C-142) and R-52 (C-143, §14C) corrected; R-46 and R-50 open; **UF-25 open** |
| 8 | Carried gates: R-02 (20 ms p95, FAIL), R-33, R-36 to R-45, R-43 (100K/250K datasets), G14, Managed HSM, signed checkpoints, Stage B release run, PB-14 | Open |
| 9 | A separate owner implementation approval naming the reviewed design revision | Not given |

## 14C. R-52: builder journal amplification (corrected, C-143)

- **Root cause.** The builder polls every Building form about every 3 s. On every round, the Authority journaled a `SearchIndexPopulationIssued` event (the first page) and a `SearchIndexCoverageAttested` event, even when the attestation was incomplete for exactly the same reason as last time. A form that can never complete (for example one holding a quarantined record) therefore grew the audit journal without bound. Each event must be archived, so new security commands waited behind the archive (`AUTHORITY_NOT_READY` / `AwaitingArchive`). The C-141 upgrade amplified this by returning such forms to Building.
- **Correction (state-aware, durable; no rate limit, no new state):**
  * every incomplete attestation journals a `blocking_state_digest`: SHA-256 over the reason, the counts, the building coverages and each blocking record with its state and head;
  * every population issuance journals a `population_digest`: required approvals, plus each record's state and head;
  * under the organization lock, the Authority reads the form's **latest** event of that kind from the append-only, hash-chained journal itself, and does not journal again when the state is unchanged. An unchanged incomplete attestation returns the original failure's journal id;
  * the attestation is evaluated every time; Complete is never suppressed; any change of state (including A → B → A) is journaled;
  * the only new persistence is a partial expression index on the journal, an access path (`ix_journal_search_index_form_events`).
- **Why it is correct across builders, instances and restarts:** the decision reads committed journal state under the organization row lock, so concurrent callers serialize and see the same latest event. Nothing is held in memory.
- **What it does not change:**
  * nothing is ever marked attested without a Complete attestation;
  * record, index and organization quarantines are untouched;
  * the journal hash chain is untouched (only appends are skipped);
  * operator rebuild (`SearchProjectionRebuild`) and builder outage recovery behave as before.
- **Evidence (focused, local Docker; not Stage B).** One permanently blocked form under normal worker polling:

| | Uncorrected image (`27ae7ca`) | Corrected |
|---|---|---|
| Attestation events in a ~70 s window | 17 | **0** (the first failure, journaled before the window, remains) |
| Population events in the window | 17 | **0** |
| Events from 8 concurrent attestation calls | 8 | **0**; all returned the same incomplete result and journal id |
| Events after an Authority restart | 2 | **0** |
| Share (permission change) latency, 10 commands, p50 / max | 358.7 / 367.1 ms | 375.0 / 402.6 ms (no measurable difference at this scale) |

  * At the larger scale seen in C-142 (about 30 blocked forms, about 110 attestation events a minute), the archive backlog delayed permission changes until they failed. That source of events is now zero for unchanged states.
  * Tests: unit `AttestationJournalSuppressionTests` (5); stack `SearchIndexBuilderStackTests` 12/12 including `R52ABlockedFormJournalsItsFailureOnceAcrossBuildersAndRestarts`; evidence register R-52.
- **Limitations:**
  * The builder still polls blocked forms every round: Authority reads under the organization lock, a few milliseconds per form, without journal writes.
  * The first attestation of each distinct blocked state is still journaled, as required.
  * A state that keeps changing (for example a large backfill in progress) is journaled at each change; that is bounded by real progress.
  * The suppression trusts the journal body digest; an appended forgery could only cause extra journaling, never a suppressed new state.
  * The index adds a small insert cost to the two builder event kinds only.
## 15. Findings, verdict and attestation

Use the FD-8 package §8 template (ID, question, severity Critical/High/Medium/Low/Info, finding, evidence, recommendation, design response) with the TQ numbers. Per-question decisions: accept, accept with conditions, or reject with a required change.

Verdict (reviewer's choice):
- **Tier 2 design:** acceptable for separate approval / acceptable with conditions / not acceptable.
- **Index architecture:** B2 / C / neither, with conditions. B1: rejection confirmed, or the variant to pursue.
- **M2 interval and online sampling period:** acceptable / change required.

Attestation: as FD-8 package §9, naming this package's commit and scope (TQ-1 to TQ-27, annexes A to E, the UF-25 document).

## 16. After the review

1. Record each finding in a findings register in this package (new section), with the reviewer's severity, unedited.
2. The design author proposes a remediation per finding, marked **proposed**, with the design sections it changes.
3. The owner decides each remediation and the final index architecture.
4. Implementation of IX-5a starts only after a **separate owner approval** that names the accepted design revision (C-138 §2).

Nothing in this package, its annexes or the owner's decisions substitutes for that review or that approval.
