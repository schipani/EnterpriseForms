# Indexed search Tier 2 (IX-5): independent security and architecture review package

- **Status:** **prepared for a qualified independent reviewer (C-138 IX5-D1, review-first). NOT SUBMITTED. NOT REVIEWED. No review attestation exists.** This supplements the FD-8 package ([INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md](INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md)), whose question 5 (J-6) is the Tier 2 subject; it does not replace it. A reviewer receives both.
- **Independence notice:**
  * This package was written by the design agent (Claude, Opus) for the project owner.
  * Neither Claude nor the owner is independent.
  * Owner reviews to date (C-123, C-129, C-133, C-136, C-137) were **owner-led**.
  * Nothing here is a finding of an independent party, an approval or a certification. Statements marked "claim" are the design's claims, to be tested.
- **Review object:** the Tier 2 **design** (design §9, §17.1; A2 R-B, R-C) as refined by annexes A to E. **No Tier 2 code exists.** Tier 1 (IX-1 to IX-4) **is implemented** and conditionally owner-accepted for development (C-137). It is Tier 2's oracle and fallback, and its code is reviewable.
- **Baseline:** local branch `ix-1/authority-search-index` at the commit that adds this package (on top of `1868e16`). Not pushed.
- **Implementation still needs a separate owner approval after the review** (C-138 §2).

## 1. Documents

| Document | Role |
|---|---|
| [INDEXED_SEARCH_PROJECTION_DESIGN.md](INDEXED_SEARCH_PROJECTION_DESIGN.md) §9, §17.1, §17.2, §17.5, §17.10, §22.4–§22.5, §23 | Normative design; the Tier 1 implementation record |
| [ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md](../decisions/ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md) | R-B (completeness by derivation equivalence) and R-C (caps) |
| [INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md](INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md) | Scope, dependencies, Tier 1 guarantees, R-38 findings, invariants (C-137) |
| **Annex A** [INDEXED_SEARCH_TIER2_INDEX_ARCHITECTURE_ASSESSMENT.md](INDEXED_SEARCH_TIER2_INDEX_ARCHITECTURE_ASSESSMENT.md) | Option C against Option B (A rejected) |
| **Annex B** [INDEXED_SEARCH_TIER2_ONLINE_SAMPLING_PLAN.md](INDEXED_SEARCH_TIER2_ONLINE_SAMPLING_PLAN.md) | Proposed 14-day online sampling plan |
| **Annex C** [INDEXED_SEARCH_TIER2_BENCHMARK_DATA_DESIGN.md](INDEXED_SEARCH_TIER2_BENCHMARK_DATA_DESIGN.md) | 100K/250K synthetic dataset design |
| **Annex D** [INDEXED_SEARCH_TIER2_FULL_SWEEP_ASSESSMENT.md](INDEXED_SEARCH_TIER2_FULL_SWEEP_ASSESSMENT.md) | Seven-day full-sweep (M2) risk assessment |
| **Annex E** [INDEXED_SEARCH_TIER2_LOOKUPSUBJECTS_ASSESSMENT.md](INDEXED_SEARCH_TIER2_LOOKUPSUBJECTS_ASSESSMENT.md) | `LookupSubjects` client interface and cost model |
| [INDEXED_SEARCH_IX4_EVIDENCE.md](INDEXED_SEARCH_IX4_EVIDENCE.md) §7, §10–§12 | The register (R-01 to R-44) and the measurements cited |
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

## 4. Proposed design under review (summary)

1. **Stages** (each needs its own authorization):
   - **IX-5a, dark:** derivation and M1 bookkeeping; never serves;
   - **IX-5b, differential:** dark plus sampling and M2;
   - **IX-5c, enabled.**
2. **Index architecture:** Option C (access enumeration, no value columns; structural variant C-S) evaluated first; Option B (split value table and record table) second; Option A rejected. **Not selected** (annex A).
3. **Derivation:** `AccessPostingDerivation/v1` mirrors `authority.schema.zed` relation by relation:
   - `form_record.effective_owner`, `discover`;
   - `field_item.read`;
   - `record_share.read`;
   - `team.participant` (teams are kept as principals and never expanded per member).

   It runs inside every edge-writing security command (and, under B, inside content commits, builder backfill and purge), under the organization lock, at the committed revision.
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

## 7. PostgreSQL 18 timing risks (summary; detail in annex A §2, §3.7)

1. **Skip scan** lets the planner use a multi-column B-tree without equality on its leading columns. A value-keyed index on a table probed by record therefore leaks value distributions (PD-9, observed). Its counterpart: **an index that leads with the principal bounds every plan by the caller's own rows.**
2. **Record-leading maintenance indexes** on caller-facing tables could be scanned organization-wide for a caller statement. Variant C-S removes them by using exact-key maintenance.
3. **GIN (btree_gin + trigram)** intersects posting lists of independent keys. The trigram list is organization-wide, so it is not bounded by principal (UF-15). It also needs bitmap scans, which are prohibited per statement.
4. **Generic plans, statistics drift, parallel plans and JIT** change plans without code changes. That is why the guarantee must be structural, with a plan-shape test across distributions and statistics as regression evidence only (C-135: a passing plan is not proof of every plan).
5. **Residual:** the Tier 1 probe still depends on per-statement settings (no hash, merge, bitmap or sequential scan) for its record-keyed access, as accepted in IX-4.

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
| FS-20 | Field approval change (option B) | Every record of the form pending; the fence is closed until verified |

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
| IX-5a dark | Unit and contract tests for the derivation, relation by relation, against zed; golden vectors; M3 affected-set property tests (per command kind, UF-14); architecture tests (INV-T2-03 every edge-writing command derives; INV-T2-06 index keying; no GIN; no value column under C); fault injection FS-01, FS-11, FS-12, FS-19; security-command latency and lock-hold delta measured (R-37 interaction); SEC-P and IX-1 to IX-4 suites unchanged and passing |
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

## 15. Findings, verdict and attestation

Use the FD-8 package §8 template (ID, question, severity Critical/High/Medium/Low/Info, finding, evidence, recommendation, design response) with the TQ numbers. Per-question decisions: accept, accept with conditions, or reject with a required change.

Verdict (reviewer's choice):
- **Tier 2 design:** acceptable for separate approval / acceptable with conditions / not acceptable.
- **Index architecture:** C / B / neither, with conditions.
- **M2 interval and online sampling period:** acceptable / change required.

Attestation: as FD-8 package §9, naming this package's commit and scope (TQ-1 to TQ-12, annexes A to E).

## 16. After the review

1. Record each finding in a findings register in this package (new section), with the reviewer's severity, unedited.
2. The design author proposes a remediation per finding, marked **proposed**, with the design sections it changes.
3. The owner decides each remediation and the final index architecture.
4. Implementation of IX-5a starts only after a **separate owner approval** that names the accepted design revision (C-138 §2).

Nothing in this package, its annexes or the owner's decisions substitutes for that review or that approval.
