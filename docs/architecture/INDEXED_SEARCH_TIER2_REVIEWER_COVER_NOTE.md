# Indexed search Tier 2 (IX-5): cover note for the independent reviewer

**Status (2026-11-10, C-143):** prepared for handoff. **Not yet sent to any reviewer. No independent review has occurred.** All assessments in the package are the design author's (Claude, Opus) and the project owner's own, owner-led. None is an independent finding or an approval.

## What we are asking you to review

The Tier 2 **design** for EnterpriseForms' Authority-owned indexed field search, before any implementation.
- **Tier 1** (IX-1 to IX-4) is implemented and serves as Tier 2's oracle and fallback; its code is reviewable.
- **Tier 2** has no code, tables or migrations. Implementation needs your assessment and then a separate owner approval.

**Start here:** [INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md](INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md). Its §0 maps every subject below to its section; annexes A to E and the UF-25 document are linked there. The FD-8 package ([INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md](INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md)) sets the independence requirements (§2) and the findings and attestation templates (§8, §9).

## Decisions that need your independent assessment

| # | Decision | Owner's current position | Package |
|---|---|---|---|
| 1 | **Index architecture:** B2 (access index plus access-scoped value rows) versus C (access index, value evaluation by record) | B2 preferred, C baseline and fallback; B1 and A rejected | Annex A §6–§15 |
| 2 | **Is B2 safe under SAFE-1 to SAFE-11**, especially: structural keying and single-statement plans; statistics across principals and organizations (UF-22); value consistency; the plaintext inventory | Needs your judgment | Annex A §10.5; §14A.1 |
| 3 | **FD-5-ALT:** Tier 2 maintained only by consuming the Authority's ordered, hash-chained journals, with no write in Tier 1 transactions; compare-and-set checkpoints with digest continuity; readiness only at exact revision equality | Preferred design for review (C-143); the synchronous draft A1–A6 not approved | §14A.2 |
| 4 | **Fallback timing:** whether serving from Tier 1 or Tier 2 (which depends on organization-wide activity) leaks the activity rate, and whether that is acceptable (TQ-23); also the overflow channel UF-28 | Open | §14A.2, §14A.5 |
| 5 | **Multi-path sharing and revocation:** distinct-item union across direct and team paths (INV-T2-13), and no stale grant after revocation | Rule implemented in Tier 1 (C-141); Tier 2 by FD-5-ALT fencing | Annex A §10.3, §11; §14A.3a |
| 6 | **Copy amplification** and its resource bounds; no cap approved without evidence | Open | Annex A §13; §14A.3 |
| 7 | **`LookupSubjects`** limitations (no limit, cursor or bulk; duplicates) and the verification cost they impose | Deployed-version evidence (E-6) and throughput (PERF-01) missing | Annex E |
| 8 | **Sweeps and verification:** M1/M2 composition; the existing §17.10 deadlines stay in force | Proposal; deadlines retained | Annex D; §14A.6 |
| 9 | **UF-25 (Tier 1)** correction, and its residuals: the R-47 delayed detection of an inflated stored count (accepted conditionally, subject to your review), the R-48 journal-anchored version boundary, and R-46/R-50 | UF-25 open pending your review (TQ-24) | UF-25 document §8, §9 |
| 10 | **R-52** (builder journal amplification) correction: state-aware suppression decided from the journal | Corrected (C-143), not reviewed | §14C |

## What is not ready

The following production and performance gates are open (package §14B):
- the 20 ms p95 commit gate (R-02), which fails;
- the 100K/250K datasets (R-43);
- G14 recovery, Managed HSM and signed external checkpoints;
- a Stage B release run;
- PB-14;
- risks R-33 and R-36 to R-52.

Nothing in the package should be read as production readiness.

## What we ask you to return

- Findings in the FD-8 template, with your own severities.
- A verdict per decision above: accept, accept with conditions, or reject with a required change.
- An attestation naming the package commit you reviewed.

The owner will decide remediations afterwards; implementation starts only with a separate owner approval naming the reviewed design revision.
