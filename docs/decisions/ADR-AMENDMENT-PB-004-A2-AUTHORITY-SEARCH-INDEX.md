# ADR-PB-004 Amendment A2: Authority-owned trusted search index (V2)

- **Status:** **APPROVED BY THE OWNER AS THE GOVERNING ARCHITECTURAL AMENDMENT, CONDITIONAL (C-121, 2026-11-06). Subject to the mandatory security conditions in §6 and to independent design review before any implementation.**
  * Drafted 2026-11-06 under C-120 by Claude (Opus); not an independent review.
  * Relaxations: R-A accepted; R-B conditionally accepted; R-C accepted for the Tier 2 design; R-D accepted (§3).
  * C-122 (2026-11-06): FD-1 to FD-8 decided (design §17.7). No independent review has occurred.
  * C-124 (2026-11-06): corrected I-2 claim accepted; integrity-binding (design §18.1) and revision-synchronization (§18.2) contracts proposed, open until accepted.
  * C-123 (2026-11-06): owner-led Tier 1 review in progress (`docs/architecture/INDEXED_SEARCH_TIER1_OWNER_REVIEW_EVIDENCE.md`). It is not an independent third-party assessment, which remains an outstanding production-readiness requirement.
- **What this approval does not do:**
  * It does not change `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md`. Under owner decision FD-5 (C-122), the §4 text is applied only after independent review acceptance **and** separate authorization to begin IX-3. The existing tests are not modified now.
  * It authorizes **no** implementation stage (IX-1 to IX-7) and **no** Tier 2 implementation or enablement.
  * It grants no production approval. That needs independent design and implementation review, Azure staging evidence (PERF-01) and PB-12 release evidence.
- **Amends:** `ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md`:
  * §7, which reserved "independently maintained V2 trusted indexes" for a separate ADR;
  * §4, §5 and the API requirements, where stated below.
- **Design:**
  * [INDEXED_SEARCH_PROJECTION_DESIGN.md](../architecture/INDEXED_SEARCH_PROJECTION_DESIGN.md) (revision 2, "the design");
  * [INDEXED_SEARCH_PROJECTION_DESIGN_R1.md](../architecture/INDEXED_SEARCH_PROJECTION_DESIGN_R1.md).
- **Related:** `CLAUDE.md` §§4C.6, 4L.5, 4M–4R, 4T.4; Phase C7 D1–D7 and OD-1–OD-8; C8 M4 S1 (pin, decide, verify); Amendment A1.

## 1. Decision proposed

**Where the index lives:**
- An Authority-owned PostgreSQL search index in schema `authority_search` of AuthorityDb.
- It is written only by the Authority:
  * inside the content-commit transaction, from the canonical bytes verified in that transaction;
  * or from builder-submitted bytes the Authority verifies against the current head.

**What is indexed:** only fields with an Active AC-6 approval whose Authority-registered classification is Public or Internal and whose protection is Standard. Encrypted values, ciphertext-derived tokens, blind indexes and document content are never indexed.

**Permissions:** SpiceDB stays the decision engine under S1.
- **Tier 1** enumerates the caller's records at the pinned snapshot.
- **Tier 2** narrows candidates with Authority-maintained access postings, and has every disclosed result decided by SpiceDB.

**Unchanged:** opening a record keeps the full verified read.

## 2. Guarantees preserved (unchanged meaning)

| # | ADR-PB-004 guarantee | How it is kept |
|---|---|---|
| P-1 | Encrypt by default; four classifications; no Confidential/Restricted search (§1, §2) | Postings only for Active approvals of Public/Internal Standard fields; CHECK on coverage classification; encrypted leaves recorded as anomalies, never indexed |
| P-2 | Searchability approved independently, by the Authority (§2, AC-6) | Unchanged approvals; the Authority re-checks every condition at query time; coverage `Complete` is set only by Authority attestation |
| P-3 | The candidate population comes from the Authority, never from BusinessDb (§4) | Tier 1: SpiceDB lookup at the S1 pin. Tier 2: Authority-held access postings plus a SpiceDB decision per result (see R-B for the change of mechanism) |
| P-4 | Predicate-field authorization before execution (§4, §6) | AC-6 plus item-level `field_item#read`: value-independent in Tier 1, per result in Tier 2 |
| P-5 | A BusinessDb index cannot decide or prove the candidate set (§4) | No BusinessDb index exists on the trusted path |
| P-6 | Whole-query fail-closed; no truncated or partial results (§4, §5, API) | Scope guard I-1, per-match binding I-2, Tier 2 divergence, S1 verify and limits each fail the whole query |
| P-7 | Snapshot consistency; revocation invalidates pages and cursors; no combining of revisions (§5) | S1 pin and verify; authorization revision and `form_content_revision` checked on every page; Authority-held result snapshots |
| P-8 | Pagination is a partition of one result set (§5) | Authority-held ordered result snapshot |
| P-9 | No disclosure through counts, sorts, facets, errors or timing (§6) | Counts only over SpiceDB-decided matches; no facets or value sort; Tier 1 scope-driven plans and Tier 2 access-scoped plans whose cost depends only on caller-visible data; value-index-driven plans prohibited |
| P-10 | Classification strengthening invalidates projections and purges plaintext replicas (Versioning) | Same-transaction purge on withdrawal or invalidation; backups age out (residual) |
| P-11 | Audit of executions without values (API) | Evidence journaled in the result transaction; no predicate values in any log (design §5.3) |
| P-12 | Opening a known record requires authorization, integrity verification and, for protected values, decryption (capability table) | Unchanged |
| P-13 | Future blind indexes or encrypted search remain separate decisions (§7) | Unchanged; A2 covers only plaintext Public/Internal |

> **Wording note (C-123, owner review OR-1).** P-6 lists I-2 among the fail-closed checks. I-2 refuses false additions made by changing postings only. It does not refuse a coordinated rewrite of postings, leaves and head root by a writer holding the Authority runtime credential; that case is detected by entry seals and on open (design §6.3 correction). This is a corrected statement of strength inside the Authority domain, not a relaxation of the Level 2 guarantees: the population is still decided by SpiceDB, so no unauthorized record can be disclosed.

## 3. Changes (relaxations) and the owner's decisions on them (C-121)

| Relaxation | Owner decision |
|---|---|
| R-A | **Accepted.** Search may operate on Authority-accepted content without re-verifying BusinessDb for every search. Full verified reads on record opening and independent reconciliation remain mandatory |
| R-B | **Conditionally accepted.** Tier 2 access-row completeness and SpiceDB equivalence require independent security assessment before implementation; Tier 2 is not to be implemented or enabled until separately approved |
| R-C | **Accepted for the Tier 2 design.** Result caps, timeouts and admission controls must not disclose unauthorized information |
| R-D | **Accepted.** Only explicitly approved, unencrypted Public/Internal values may be stored in the Authority index; encrypted fields and their derived search representations remain prohibited |

The texts below are the amendment wording to which those decisions apply.

### R-A: search-time verification of BusinessDb bytes

**Current text (§4):**

> "…then retrieve and verify every eligible current record/field value and relationship that could match … A result is `complete` only after exhaustive verification at a coherent authorization/content snapshot."

**Proposed text:**

> "For the Authority-owned index (Amendment A2), integrity verification of field values happens when the Authority accepts the content: HMAC, complete leaf set and Merkle root, in the commit transaction that derives the index entries.
>
> At query time the Authority enforces:
> - the scope guard (I-1): every in-scope or matched record has a current entry bound to its accepted head, with no quarantine;
> - for every returned match, the binding of its indexed values to the accepted leaf digests and recomputed root (I-2).
>
> A result is `complete` relative to the Authority-accepted content at one coherent snapshot. Search does not verify the business database copy. Tampering with business bytes is detected when a record is opened (full verified read with quarantine) and by the reconciliation sweeps."

**Effect:** ENC-SR-08/09 change from "the search fails" to "the search answers from accepted content; open and sweep detect" (§4).

### R-B: Tier 2 population mechanism

**Proposed addition to §4:**

> "In Tier 2, the candidate population is narrowed by Authority-maintained access postings derived, in the same transaction, from the Authority's canonical relationship edges. Every disclosed result is decided by SpiceDB at the S1-verified snapshot, and any divergence fails the query.
>
> Completeness of the candidate population depends on the equivalence of that derivation with the SpiceDB schema. This is controlled by a schema coupling guard, property-based differential tests, online differential sampling against Tier 1 and periodic sweeps, rather than by a per-search enumeration."

**Effect:** disclosure safety is unchanged. A derivation defect could omit authorized results until the next differential check (residual R-IX-7).

### R-C: ceiling semantics

**Current §5:** a ceiling on "independently enumerated records, scan bytes and elapsed time".

**Proposed:**

> "For the Authority-owned index:
> - Tier 1 keeps a temporary discover ceiling (50,000);
> - Tier 2 has no enumeration ceiling;
> - both are bounded by a result cap on SpiceDB-decided matches, a statement timeout and admission;
> - every refusal depends only on quantities owned by the caller."

### R-D: plaintext exposure extended to AuthorityDb

**Proposed addition to §3:**

> "With the Authority-owned index, approved Public/Internal plaintext is also held in AuthorityDb and its backups. It is readable by:
> - the AuthorityDb administrator (just-in-time, audited);
> - the Authority runtime role;
> - the SELECT-only search reader role.
>
> Withdrawn values persist in WAL and backups until their retention expires."

## 4. Proposed acceptance-test changes (FD-5, C-122)

Applied to `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md` **only after independent review acceptance and separate authorization to begin IX-3** (FD-5, approved C-122). Not applied now. V1 (`VerifiedScan`) keeps its current pass criteria for as long as it remains in service.

| Test | Proposed pass criterion for the indexed engine |
|---|---|
| ENC-SR-08 (business DBA deletes a matching record row) | The search returns the record as a match on Authority-accepted content: never a false zero, never a partial result. Opening the record fails its verified read and quarantines it. The next deep sweep reports the record. No business-side deletion changes any search result |
| ENC-SR-09 (business DBA mutates a matching value or index to exclude a hit) | The search still returns the hit (accepted content). Opening fails and quarantines. The deep sweep detects. The mutation has no effect on any search result |
| ENC-SR-10, ENC-SR-11 | Unchanged criteria; satisfied structurally (no BusinessDb index on the path) |
| **ENC-SR-19** (new): Authority-domain posting alteration | An altered posting value never produces a match (I-2). An altered or deleted posting that removes a match is detected by the entry-seal sweep, and the projection is quarantined |
| **ENC-SR-20** (new): partial restore or rollback of the index | A stale or old-operation entry fails the whole query (I-1). The projection is quarantined and rebuilt; no result is drawn from stale entries |
| **ENC-SR-21** (new): Tier 2 divergence | An access row without a matching SpiceDB decision fails the whole query and disables Tier 2. Differential tests show zero under-inclusion over the agreed corpus |
| **ENC-SR-22** (new): result presentation | No indexed result is labelled or represented as verification of the business database copy (`projectionVerified=false`; scope statement per design §6.2) |

## 5. Conditions attached to acceptance

1. The design's IX-D1 to IX-D14 decided as a set. Any rejected relaxation (R-A to R-D) sends the design back for revision.
2. Implementation in stages IX-1 to IX-7, each separately authorized, with the gates in design §16.
3. The PB-10 data-flow register updated for AuthorityDb before IX-1 completes.
4. Independent design review before IX-1, and independent implementation review before any production use.
5. G06 stays OPEN until the indexed engine's tests (IX-01 to IX-42, ENC-SR-01 to 22) have executed evidence and independent review.

## 6. Mandatory security conditions (C-121)

| # | Condition | Design answer |
|---|---|---|
| 1 | **Tier 2 completeness:** document how missing authorized records are detected, not only unauthorized additions. No Tier 2 implementation or enablement before independent review and separate approval | Design §17.1 (verified-revision fence M1, rolling equivalence sweep M2, affected-set tests M3, differentials M4–M6) |
| 2 | **Quarantine separation:** a search-index integrity failure may quarantine the index without quarantining the organization. An S1 authorization-integrity failure always keeps its organization-level quarantine. Search fallback never bypasses an S1 quarantine | Design §17.2 (Q1–Q5) |
| 3 | **Stored search results:** expiration and cleanup; storage and resource limits; reauthorization on page retrieval; searchability-approval revalidation; behaviour after revocation, classification change or organization quarantine; whole-query failure when validation cannot complete | Design §17.3 |
| 4 | **AuthorityDb access:** strict separation of runtime, search-reader, administrator and business identities; plaintext exposure through backups, replication, diagnostics and administration documented | Design §5, §17.4 |
| 5 | **OBS-IX-1** recorded as a separate finding and included in the review package; no unrelated remediation without approval | `docs/security/OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md` |

**Independent design review** before implementation:
- package: `docs/architecture/INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md`;
- twelve questions, as specified by the owner in C-121.

**Conditions added by C-122** (design §17.7, §17.9):
- FD-1: sweep completion tracked per verification scope; interrupted, failed or incomplete checks never reset a deadline; overdue verification disables indexed search until the required checks complete successfully.
- FD-2: stored results are invalidated across all Authority instances on revocation, session termination, searchability change and organization quarantine; whole-page fail-closed behaviour is preserved.
- FD-4: any indication of permission-integrity damage invokes S1 verification first; no search-index state suppresses or downgrades an organization authorization quarantine.
- FD-3, FD-6, FD-7: Tier 2 completeness, a separate search process and OBS-IX-1 severity go to the independent reviewer.

## 7. Rejected alternatives (summary)

| Alternative | Reason |
|---|---|
| BusinessDb projection | Cannot prove completeness against the Level 2 adversary |
| External engine or separate search service | Another trust plane and residency scope; not needed at the PB-09 envelope |
| Match-first checking | Its cost and limits reveal unauthorized matches |
| Authenticated range-proof index | Novel, unreviewed, and does not compose with access filtering |
| Caching verified heads | Contrary to `CLAUDE.md` §4L.5 |
| Blind indexes or deterministic encryption | Outside A2 (ADR-PB-004 §7) |

The detail is in design §9.1 and §14, and R1 §H.
