# Indexed search (Option C): independent design review package

- **Status:** **Ready for submission to a qualified human reviewer independent of the implementation (C-121; owner decisions FD-1 to FD-8 recorded in C-122). NOT REVIEWED. NOT SUBMITTED. No review attestation exists.**
- **C-123:** the owner is conducting an **owner-led** Tier 1 review (`INDEXED_SEARCH_TIER1_OWNER_REVIEW_EVIDENCE.md`). It is **not** an independent third-party assessment and does not replace this package. Independent assessment remains an outstanding production-readiness requirement (`docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`). Its findings OR-1 to OR-4 are listed in §5 for your examination; you must not rely on them.
- **C-138 (2026-11-08):** the owner approved review-first for Tier 2 (IX5-D1): no Tier 2 implementation before this review and a separate owner approval. The Tier 2 subject (question 5, J-6) is expanded in the supplement [INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md](INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md) and its annexes A to E, prepared against the implemented Tier 1 (branch `ix-1/authority-search-index`). A reviewer receives both packages. **Still not submitted; no review has occurred.**
- **Independence notice:**
  * This package was written by the design agent (Claude).
  * Neither Claude nor the project owner is an independent reviewer.
  * Nothing here is a finding, an approval or a certification. Statements marked "claim" are the design's claims to be tested, not conclusions.
- **Review object:** a **design**. **No code exists for it.** No test has been executed, and no performance figure has been measured unless quoted from existing evidence.
- **Baseline:** local branch `design/indexed-search-projection`, created from `phase-c8/m4-governance-audit` at `e830d08`. Not pushed. The design commits are on that branch (§4).

## 1. Purpose and status

### 1.1 Under review

| Document | Role |
|---|---|
| [INDEXED_SEARCH_PROJECTION_DESIGN.md](INDEXED_SEARCH_PROJECTION_DESIGN.md) (revision 3, "the design") | Normative design |
| [INDEXED_SEARCH_PROJECTION_DESIGN_R1.md](INDEXED_SEARCH_PROJECTION_DESIGN_R1.md) ("R1") | Earlier sections that revision 3 cites as "R1 §…" (schema, lifecycle, flows) |
| [ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md](../decisions/ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md) ("A2") | Governing amendment: preserved guarantees P-1 to P-13, relaxations R-A to R-D, conditions |
| [OBS-IX-1](../security/OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md) | Separate pre-existing finding (question 12) |

**Context documents:**
- `docs/decisions/ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md`;
- `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md` (the current V1 engine);
- `docs/architecture/PHASE_C8_M4_1_DESIGNS_M1_S1.md` and `docs/security/PROJECTION_INTEGRITY_S1_OPERATIONS.md` (S1);
- `docs/architecture/PHASE_C8_M4_1_P_S1_1_SEARCH_OPTIMIZATION_PROPOSAL.md` (why V1 does not scale);
- `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md`;
- `CLAUDE.md` §§4L, 4M–4R, 4T.

### 1.2 Owner decisions so far

- **C-120:** conditional architectural approval of an Authority-owned PostgreSQL index, with conditions C1–C4.
- **C-121:**
  * architecture approved as the selected direction;
  * A2 approved as the governing amendment, subject to conditions and this review;
  * IX-D1 to IX-D14 decided (design §15.1);
  * R-A, R-C and R-D accepted, R-B conditionally accepted;
  * five mandatory conditions (design §17);
  * **no implementation stage authorized; Tier 2 approved for review only.**
- **C-122:**
  * FD-1 approved with conditions (completion per verification scope; no deadline reset by interrupted, failed or incomplete checks; overdue disables indexed search until the checks complete);
  * FD-2 approved (with cross-instance invalidation and whole-page fail-closed);
  * FD-3 approved for review only;
  * FD-4 and FD-5 approved;
  * FD-6 and FD-7 deferred to this review;
  * FD-8: external action; no review has occurred.
  * Details: design §17.7 and §17.9.

**Owner approval is not independent approval.** This review is a precondition for any implementation.

## 2. Reviewer independence requirement

The reviewer must:
- not have contributed to the design, implementation, testing or documentation of this project;
- not rely on the owner's decisions (C-120, C-121, C-122) or on any claim in this package;
- not be an AI agent acting for the owner or for Claude (such a review is not independent and must not be recorded as such);
- be independent of the business-deployment chain, in the sense of the PB-12 independent Security Owner role;
- record any conflict of interest (section 9), including any relationship with the owner, Anthropic, Authzed/SpiceDB or Microsoft Azure.

Findings use the severities Critical, High, Medium, Low and Info (section 8).

## 3. The twelve assessment questions

For each question:
- **Claim:** what the design asserts;
- **Where:** where it is specified;
- **Basis:** the code facts at `e830d08` the claim relies on, checked by the design author;
- **Examine:** what the reviewer should test or try to break.

### Q1. AuthorityDb trust boundaries and plaintext exposure

- **Claim:**
  * Only the AuthorityDb administrator (just-in-time, audited), the Authority runtime role and a new SELECT-only `authority_search_reader` role can read indexed plaintext.
  * No business identity can.
  * No caller can write index rows.
  * Plaintext exposure through backups, replication, diagnostics and administration is bounded as inventoried.
- **Where:** design §5, §17.4; A2 R-D.
- **Basis:**
  * one runtime role created by `init-authority-roles.sh`;
  * grants in `AuthorityRuntimeGrants.cs`;
  * separate cluster and connection strings in `infrastructure/docker/compose.yaml`.
- **Examine:**
  * whether reader-role separation is meaningful while one Authority process holds both credentials (UF-5);
  * **FD-6 (deferred to you, C-122):** whether a separately deployed Authority search process, holding only its own read-only database credential, is necessary;
  * completeness of the exposure inventory: logs (bind parameters, constraint `DETAIL` lines), Query Store, temp files, PITR copies, geo-backups, support access;
  * whether every proposed PostgreSQL logging setting is available per role on Azure Database for PostgreSQL Flexible Server (UF-4);
  * whether R-D is acceptable for the regulated data categories expected.

### Q2. Atomic index maintenance

- **Claim:**
  * Postings and entries are derived inside the content-commit transaction, from bytes verified in that transaction, and commit or roll back with the head.
  * A derivation or seal failure aborts the commit.
- **Where:** design §3, §7; R1 §C.1.
- **Basis:**
  * `ContentService.CommitAsync` runs in one `ReadCommitted` transaction (`AuthorityStore.BeginScopeAsync`) with `FOR UPDATE` on the organization state and the record head;
  * staged bytes are verified (`VerifyStagedAgainstLeavesAsync`) before the manifest, leaves, head, checkpoint and journal are written;
  * heads change only in `CommitAsync`.
- **Examine:**
  * any other writer of heads, lifecycle or approvals (the design requires an architecture test at IX-1);
  * `ReadCommitted` anomalies in derivation;
  * the cost of the added seal MAC sign inside the organization lock (one per commit);
  * crash and fault-injection points;
  * whether builder backfill, a separate transaction with a head CAS, can race inline derivation.

### Q3. Search completeness and omission detection

- **Claim:**
  * Against the Level 2 adversary (business DBA), indexed search can neither omit nor add eligible results, because BusinessDb is not on the path.
  * Against Authority-domain manipulation, additions are blocked per query (I-2) and omissions are detected within the sweep guarantees.
- **Where:** design §6.1–§6.4, §17.5; A2 R-A.
- **Basis:** `canonical_payload_digest` is unkeyed SHA-256 over the exact canonical bytes (Merkle protocol line 57). So I-2 can tie each match to the accepted leaf and root without the HSM.
- **Examine:**
  * whether I-2 actually binds the *predicate-relevant* value (canonical decoding, normalization, multi-select);
  * whether I-1 is sufficient for Tier 1 omission of whole entries;
  * whether the R-A semantics are acceptable;
  * whether the UI and contract wording (`projectionVerified=false`, `resultBasis`, scope statement) prevents results being read as proof of business integrity.

### Q4. Tier 1 SpiceDB consistency

- **Claim:**
  * Tier 1 decides the population with S1-pinned `LookupResources` (`discover`, `read`) and value-independent `field_item#read` bulk checks.
  * Candidate SQL is scope-driven, so its cost does not depend on unauthorized data.
  * The pin and the AuthorityDb read are consistent through S1 verify and the revision checks.
- **Where:** design §8; R1 §E.1–§E.3.
- **Basis:**
  * S1 `VerifiedProjectionSnapshot`;
  * existing catalogue lookups (`SearchCatalogueService`, ceiling `PermissionCheckService.MaximumDiscoverableRecords` = 50,000);
  * flat teams in `authority.schema.zed`.
- **Examine:**
  * the consistency window between the SpiceDB snapshot and the AuthorityDb REPEATABLE READ snapshot;
  * Evaluable parity with `FieldSearchEvaluator`;
  * whether a scope-driven plan can be enforced reliably in PostgreSQL (fences, planner settings, a plan-shape test);
  * residual timing channels;
  * whether every limit refusal depends only on caller-owned quantities.

### Q5. Tier 2 access-row equivalence (approved for review only)

- **Claim:**
  * Access postings derived from canonical edges narrow candidates only. SpiceDB decides every disclosed result; divergence fails closed.
  * **Missing** authorized records are detected by the verified-revision fence (M1) before any Tier 2 search can use changed rows, and otherwise within the 7-day sweep (M2).
- **Where:** design §9, §17.1; A2 R-B, R-C.
- **Basis:**
  * commit-time authorization already evaluates canonical edges in C# (`DesiredStateGraph`, `ContentService.cs` 274–279);
  * edges are written in the security-command transaction;
  * index `ix_authority_edges_subject` exists.
- **Examine:**
  * the derivation table against `authority.schema.zed` (owners, parents, shares with partial field scopes, teams);
  * the conservative affected-set rules;
  * whether `LookupSubjects`-based verification is sound and affordable;
  * whether the fence can stall indefinitely under write load;
  * whether R-C caps reveal anything;
  * **whether Tier 2's completeness guarantees are sufficient for separate approval**.
  * **FD-3 (C-122), specifically:**
    - omitted affected records: an affected-set computation that misses a record whose access rows changed (§17.1 table and M3);
    - missed events: a security command or content commit that changes edges or approved-field postings without writing `search_access_pending_verifications` in the same transaction;
    - projection divergence: access rows, canonical edges and SpiceDB disagreeing, and whether every divergence path reaches S1 first (Q4);
    - whether the 7-day M2 comparison interval (disable at 14 days) is adequate.

### Q6. Search-index tampering and rollback

- **Claim:** stale, replayed or partially restored index state is refused per query (I-1 operation binding and revision); an old Authority version is fenced by revision lag.
- **Where:** design §6.4, §12.2, §12.4.
- **Basis:** S1's rollback-fence precedent (`ck_legacy_projection_token_retired`); the content checkpoint journal.
- **Examine:**
  * restore of `authority_search` alone, of AuthorityDb with G14 replay, and of mixed versions;
  * whether the revision-lag fence is enough or a hard fence is required;
  * whether tampering with `search_projection_states` itself defeats the checks.

### Q7. Per-record seals and reconciliation

- **Claim:**
  * Entry seals (HMAC with the existing organization integrity key, domain-separated) are verified with a 24 h target; the index is suspended when any entry has gone 48 h without a successful verification.
  * Deep re-derivation targets every record within 30 days; suspension at 60 days.
  * Completion is tracked per verification scope; interrupted, failed or incomplete runs never reset a deadline; an overdue scope suspends indexed search until it completes successfully (FD-1, C-122).
- **Where:** design §4, §6.5, §17.5, §17.9.1; IX-D14; FD-1.
- **Examine:**
  * domain separation from row HMACs;
  * absence of any signing-oracle exposure (the seal is computed only inside the commit or attestation paths);
  * **FD-1 (C-122):** whether the intervals (1 h, 24 h, 30 days) adequately address the integrity threat model;
  * whether the grace between target and threshold (2 h, 48 h, 60 days) is acceptable, or the threshold should equal the target (UF-13);
  * rules R1 to R8 of §17.9.1: can any run path record completion it did not earn, and is automatic clearing of `Suspended` safe;
  * the Managed HSM load of about 29 verifications/s at the PB-09 envelope (UF-3);
  * whether quarantine on an overdue sweep is the right availability trade.

### Q8. Stored result pagination and authorization revocation

- **Claim:** result snapshots hold only ids and sort keys, expire within 15 minutes, are bounded, and every page is re-authorized, including:
  * a SpiceDB re-decision of the page's records;
  * revalidation of the searchability approval;
  * whole-page failure on any unverifiable step.
- **Where:** design §17.3, §17.9.2; IX-D5; FD-2.
- **Examine:**
  * **FD-2 (C-122):** cross-instance invalidation for revocation, session termination, searchability changes and organization quarantine (rules X1 to X4): any instance-local state a page check could rely on;
  * the nonce-protected query digest;
  * session-bound deletion;
  * the capacity refusal at instance level (aggregate-load signal);
  * the behaviour table for every revocation path;
  * replay of an old cursor after a snapshot is deleted.

### Q9. Search-index quarantine versus S1 organization quarantine

- **Claim:**
  * The two quarantines are independent.
  * S1 organization quarantine always applies and is evaluated first.
  * The V1 fallback re-enters every V1 and S1 gate.
  * Search-side signals of authorization damage escalate through S1's decisive check (Q4 rule) before being classified as index failures.
- **Where:** design §17.2, §17.9.1 (new `Suspended` state); IX-D7, IX-D8; FD-4 (approved, C-122).
- **Basis:** S1 quarantine reasons and the permanence trigger (`PROJECTION_INTEGRITY_S1_OPERATIONS.md`).
- **Examine:** any path where an index failure or suspension masks, delays, downgrades or replaces an S1 quarantine, or where the fallback could serve during an S1 quarantine.

### Q10. Disaster recovery and application rollback

- **Claim:**
  * All `authority_search` data is derived.
  * Loss affects search availability only.
  * Rebuild uses verified BusinessDb bytes (postings) and canonical edges (access postings).
  * Feature flags and an additive schema allow rollback without data loss.
- **Where:** design §12; R1 §G.
- **Examine:**
  * interaction with the G14 recovery plane, which is not implemented (UF-10);
  * rebuild time and HSM cost at the PB-09 envelope;
  * whether a rebuild can expose stale plaintext;
  * flag-combination hazards.

### Q11. ADR-PB-004 relaxations R-A to R-D

- **Claim:** every changed guarantee is named. The rest (P-1 to P-13) keep their meaning.
- **Where:** A2 §2–§4; design §10.
- **Examine:**
  * whether any further relaxation is implicit but unnamed;
  * whether the proposed ENC-SR-08/09 text and the new ENC-SR-19 to 22 are adequate (to be applied only under FD-5).

### Q12. OBS-IX-1

- **Claim:** the Authority runtime role holds `UPDATE` on 14 tables the code calls append-only. Legitimate updates touch only lifecycle columns of four of them.
- **Where:** the OBS-IX-1 document.
- **Examine:**
  * which alterations are detected today, by what and when;
  * **FD-7 (deferred to you, C-122):** establish the severity (the proposed Medium is **not** accepted) and recommend remediation among options A to D or another;
- **No remediation is authorized.**

## 4. Commits and how to read them

| Commit | Content |
|---|---|
| `e830d08` | Code baseline (M4-1 state; no indexed-search code) |
| `a89b6e4` | Design revision 1 as presented (C-120 input) |
| `a24733e` | Design revision 2, A2 drafted, C-120 recorded |
| `1a74acc` | Revision 3, owner decisions C-121, OBS-IX-1, this package |
| (this commit) | Owner decisions C-122 (FD-1 to FD-8); design §17.7 and §17.9; corrected sweep guarantees (§17.5); this package updated |

**To reproduce the code facts:**
- `git show e830d08:<path>` for each source file cited in the design §3 table and in question 2;
- `py -I scripts/validate-specifications.py --strict` checks the specification manifest.

No runtime stack is needed for a design review.

## 5. Unresolved findings (known to the design author)

| ID | Finding | Status |
|---|---|---|
| UF-1 | OBS-IX-1: runtime `UPDATE` on append-only tables | Open; question 12 |
| UF-2 | Tier 2 completeness (R-B) not independently assessed | Open; question 5; blocks Tier 2 |
| UF-3 | Managed HSM throughput for seal signs per commit, seal sweeps, backfill and deep sweeps is unmeasured | PERF-01 |
| UF-4 | Azure Flexible Server support for per-role logging settings, pgAudit object audit and Query Store behaviour is unverified | IX-1 / staging |
| UF-5 | The runtime and reader credentials are in one Authority process; reader separation limits query-path SQL only | FD-6 |
| UF-6 | Page-invalidation rate under write load (`form_content_revision`) unknown | IX-3 measurement |
| UF-7 | I-2 cost, Tier 1 probe cost and the timing residual are unmeasured | IX-3/IX-13 |
| UF-8 | Authority-domain omission detected within the S-B bound (target 24 h, guaranteed 48 h), not per query (R-IX-6) | Accepted design trade; question 7 |
| UF-9 | Backup retention is the persistence bound for withdrawn plaintext (R-IX-3); the PB-10 retention decision is pending | PB-10 |
| UF-10 | G14 recovery plane not implemented; projection rebuild after a full Authority recovery depends on it; T16-A untested | G14 |
| UF-11 | The V1 fallback engine fails its 480-record benchmark (P-S1-1), so fallback limits are lower than V1's nominal 500 | P-S1-1 |
| UF-12 | `LookupSubjects` cost for the Tier 2 fence scales with changed records × approved fields | Question 5; PERF-01 |
| UF-13 | Revision 3 stated sweep targets as guarantees. Corrected in C-122: guaranteed bounds are the overdue thresholds (2 h, 48 h, 60 days). C-123 proposes removing the grace (design §17.10) | Question 7 |
| OR-1 | I-2 is unkeyed; a coordinated rewrite of posting, leaf and head root by the Authority runtime credential passes it (design §6.3 correction). C-124: binding contract proposed (design §18.1): the new seal message schema `DynamicForms/SearchEntrySeal/v1` and its separate key purpose are **in your scope** | Questions 3, 7, 12 |
| OR-2 | Four `ContentIntegrityRevision` increment sites; revision equality in I-1 would let one share-editor incident quarantine the index (design §3 note) | Questions 2, 6 |
| OR-3 | Abort paths of `CommitAsync` commit before the head changes; derivation placement clause | Question 2 |
| OR-4 | No catalogue-snapshot purge or session binding exists today; C-124 requirements in design §18.4 | Question 8 |
| OBS-IX-1 (C-124) | Provisional High. The remediation proposal reads an INSERT-based path (AP-3: unkeyed journal chain, outbox delta applied by the projector) by which the runtime database credential alone could forge archived, projected history. Not executed | Question 12 |

## 6. Owner decisions (C-122) and items for your judgment

### 6.1 Owner decisions

Recorded in design §17.7; conditions in §17.9. They are **not** evidence for any claim, and you must not rely on them (section 2).

| # | Decision |
|---|---|
| FD-1 | Sweep intervals approved with conditions (completion per scope; no reset by failed runs; overdue suspends) |
| FD-2 | Stored-result limits and per-page re-decision approved; cross-instance invalidation required |
| FD-3 | Tier 2 completeness in review scope; implementation unauthorized |
| FD-4 | S1 verification first for any permission-integrity signal |
| FD-5 | ENC-SR text applied only after review acceptance and separate IX-3 authorization |
| FD-6 | Separate search process: **deferred to you** |
| FD-7 | OBS-IX-1 severity and remediation: **deferred to you** |
| FD-8 | Reviewer selection and submission: external, not yet done |

### 6.2 Items requiring your judgment

The owner has asked specifically for your judgment on these. Each maps to a question.

| # | Item | Question |
|---|---|---|
| J-1 | Whether a separately deployed Authority search process with its own read-only credential is necessary (FD-6) | Q1 |
| J-2 | Whether the plaintext-exposure inventory is complete and R-D acceptable for the expected data | Q1 |
| J-3 | Whether inline derivation is truly atomic with the head, and builder backfill race-free | Q2 |
| J-4 | Whether I-2 binds the predicate-relevant value, and R-A semantics are acceptable | Q3 |
| J-5 | Tier 1 snapshot consistency, enforceability of scope-driven plans, residual timing channels | Q4 |
| J-6 | Tier 2: omitted affected records, missed events, projection divergence, the 7-day comparison interval, and sufficiency for separate approval (FD-3) | Q5 |
| J-7 | Whether rollback and partial-restore fencing is enough | Q6 |
| J-8 | Whether the sweep intervals address the integrity threat model; target-versus-threshold grace; safety of rules R1 to R8 (FD-1, UF-13) | Q7 |
| J-9 | Cross-instance invalidation and page reauthorization (FD-2) | Q8 |
| J-10 | Whether any path lets an index failure or suspension suppress or downgrade an S1 quarantine (FD-4) | Q9 |
| J-11 | DR and rollback with G14 unimplemented | Q10 |
| J-12 | Whether any relaxation beyond R-A to R-D is implicit, and the adequacy of the proposed ENC-SR text | Q11 |
| J-13 | OBS-IX-1 severity and recommended remediation (FD-7) | Q12 |
| J-14 | Overall verdicts for Tier 1 and Tier 2 (section 8) | All |

## 7. Known limitations of this package

- It reviews a design. Implementation review (IX-7) is separate and mandatory.
- Code facts were checked by the design author by inspection only, with no executed tests.
- Performance statements are estimates.
- Azure-specific behaviour is assumed from documentation, not observed (UF-4).

## 8. Findings template

| ID | Question # | Severity | Finding | Evidence | Recommendation | Design response |
|---|---|---|---|---|---|---|
| F-1 | | | | | | |
| F-2 | | | | | | |
| F-3 | | | | | | |

Per question: accept the claim, accept with conditions, or reject with a required change.

| Question | Decision | Conditions or required change |
|---|---|---|
| 1 AuthorityDb boundaries, plaintext | | |
| 2 Atomic maintenance | | |
| 3 Completeness, omission | | |
| 4 Tier 1 SpiceDB consistency | | |
| 5 Tier 2 equivalence | | |
| 6 Tampering, rollback | | |
| 7 Seals, reconciliation | | |
| 8 Stored results, revocation | | |
| 9 Quarantine separation | | |
| 10 DR, application rollback | | |
| 11 R-A to R-D | | |
| 12 OBS-IX-1 | | |

Overall verdict (reviewer's choice):
- Tier 1 design acceptable for implementation / acceptable with conditions / not acceptable.
- Tier 2 design: acceptable for separate approval / acceptable with conditions / not acceptable.

## 9. Attestation

| Field | Entry |
|---|---|
| Reviewer name | |
| Organization | |
| Independence statement | I did not contribute to the design, implementation, testing or documentation of this project, and I am not an AI agent acting for the owner or for Claude. I have not relied on the owner's decisions (C-120, C-121, C-122). |
| Conflicts of interest (or "none") | |
| Commit reviewed | |
| Scope reviewed (questions 1 to 12; any excluded) | |
| Date | |
| Signature | |

The owner's decisions are **not** a substitute for this attestation. A favourable design review does not authorize implementation. Each stage still needs the owner's authorization, and production still needs implementation review, PERF-01 and PB-12 evidence.
