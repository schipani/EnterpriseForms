# Indexed search (Option C): independent design review package

- **Status:** **Ready for submission to an independent reviewer (C-121). NOT REVIEWED. NOT SUBMITTED.**
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

**Owner approval is not independent approval.** This review is a precondition for any implementation.

## 2. Reviewer independence requirement

The reviewer must:
- not have contributed to the design, implementation, testing or documentation of this project;
- not rely on the owner's decisions (C-120, C-121) or on any claim in this package;
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
  * whether reader-role separation is meaningful while one Authority process holds both credentials (UF-5, FD-6);
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
  * Entry seals (HMAC with the existing organization integrity key, domain-separated) are verified at least every 24 h.
  * Deep re-derivation covers every record within 30 days.
  * An overdue sweep stops the index from serving.
- **Where:** design §4, §6.5, §17.5; IX-D14; FD-1.
- **Examine:**
  * domain separation from row HMACs;
  * absence of any signing-oracle exposure (the seal is computed only inside the commit or attestation paths);
  * whether 24 h and 30 days are appropriate;
  * the Managed HSM load of about 29 verifications/s at the PB-09 envelope (UF-3);
  * whether quarantine on an overdue sweep is the right availability trade.

### Q8. Stored result pagination and authorization revocation

- **Claim:** result snapshots hold only ids and sort keys, expire within 15 minutes, are bounded, and every page is re-authorized, including:
  * a SpiceDB re-decision of the page's records;
  * revalidation of the searchability approval;
  * whole-page failure on any unverifiable step.
- **Where:** design §17.3; IX-D5; FD-2.
- **Examine:**
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
- **Where:** design §17.2; IX-D7, IX-D8; FD-4.
- **Basis:** S1 quarantine reasons and the permanence trigger (`PROJECTION_INTEGRITY_S1_OPERATIONS.md`).
- **Examine:** any path where an index failure masks, delays or replaces an S1 quarantine, or where the fallback could serve during an S1 quarantine.

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
  * the proposed severity (Medium);
  * remediation options A to D.
- **No remediation is authorized.**

## 4. Commits and how to read them

| Commit | Content |
|---|---|
| `e830d08` | Code baseline (M4-1 state; no indexed-search code) |
| `a89b6e4` | Design revision 1 as presented (C-120 input) |
| `a24733e` | Design revision 2, A2 drafted, C-120 recorded |
| (this commit) | Revision 3, owner decisions C-121, OBS-IX-1, this package |

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
| UF-8 | Authority-domain omission detected within 24 h, not per query (R-IX-6) | Accepted design trade; question 7 |
| UF-9 | Backup retention is the persistence bound for withdrawn plaintext (R-IX-3); the PB-10 retention decision is pending | PB-10 |
| UF-10 | G14 recovery plane not implemented; projection rebuild after a full Authority recovery depends on it; T16-A untested | G14 |
| UF-11 | The V1 fallback engine fails its 480-record benchmark (P-S1-1), so fallback limits are lower than V1's nominal 500 | P-S1-1 |
| UF-12 | `LookupSubjects` cost for the Tier 2 fence scales with changed records × approved fields | Question 5; PERF-01 |

## 6. Owner decisions still pending

FD-1 to FD-8 are in design §17.7:
- sweep intervals and overdue rule;
- stored-result limits and per-page re-decision;
- the Tier 2 fence in review scope;
- the S1 escalation rule;
- timing of the ENC-SR text;
- a separate search container;
- the OBS-IX-1 disposition;
- reviewer selection and submission.

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
| Independence statement | I did not contribute to the design, implementation, testing or documentation of this project, and I am not an AI agent acting for the owner or for Claude. I have not relied on the owner's decisions (C-120, C-121). |
| Conflicts of interest (or "none") | |
| Commit reviewed | |
| Scope reviewed (questions 1 to 12; any excluded) | |
| Date | |
| Signature | |

The owner's decisions are **not** a substitute for this attestation. A favourable design review does not authorize implementation. Each stage still needs the owner's authorization, and production still needs implementation review, PERF-01 and PB-12 evidence.
