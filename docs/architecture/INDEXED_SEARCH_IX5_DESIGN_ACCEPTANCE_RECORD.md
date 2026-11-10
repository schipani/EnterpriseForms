# IX-5 security design acceptance record (owner-led)

- **Status:** **Approved by the owner in C-147 (2026-11-10), for Development/Testing only.** An owner-led security approval under GA-IX5-1, **not** an independent security assessment; no independent review has occurred. Approved design baseline: `7ec5a79`. IX-5a only is authorized; IX-5b and IX-5c need separate owner approval.
- **Design baseline:** signed commit `f5b1ca6`: [INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md](INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md) with annexes A to E and the UF-25 document. It also covers the C-146 evidence in [INDEXED_SEARCH_IX5_EVIDENCE_PLANS.md](INDEXED_SEARCH_IX5_EVIDENCE_PLANS.md) §R.
- **Prepared by:** Claude (design author), for the owner. Positions in the tables below are **proposed**; they bind only once the owner signs them.

## 1. Design accepted

| # | Element | Accepted as | Supersedes |
|---|---|---|---|
| A-1 | **B2: access-scoped value indexing** | Annex A §6–§15, comprising: separate access and value structures; principals are users and teams, never expanded (D-1); distinct-item union across paths (INV-T2-13, §10.3); principal-leading indexes and single-table statements (SAFE-1 to SAFE-11). Option C remains the baseline and fallback; B1 and A are rejected | Design §9.2 index shape (UF-15) |
| A-2 | **FD-5-ALT: asynchronous maintenance** | Package §14A.2: Tier 2 is maintained only by consuming the ordered, hash-chained Authority journals; compare-and-set checkpoints with digest continuity; no Tier 2 write in any Tier 1 or security-command transaction | IX-D13's "in the same transaction as the commands that change them"; the FD-5 draft A1–A6 |
| A-3 | **Readiness and fallback** | Tier 2 is usable for an organization only when its checkpoint equals the current authorization revision **and** content-integrity revision exactly. Otherwise the request is served immediately by Tier 1 or refused; there is no catch-up wait (C-141) | F-2 (bounded wait), rejected |
| A-4 | **Authority of decisions** | SpiceDB decides every disclosed result at the pinned snapshot (IX-D13 retained). Tier 2 only narrows candidates | — |
| A-5 | **Searchable data** | Only approved, unencrypted Public/Internal fields (ADR-PB-004, PB-04 matrix). No ciphertext-derived index. No query path whose execution depends on unauthorized values. No cross-organization rows, statistics or plans | — |
| A-6 | **Command impact** | Annex A §8 command-impact matrix is normative for the affected sets | Design §17.1 affected-set table (UF-14) |
| A-7 | **Increments** | IX-5a access projection (not serving); IX-5b value projection, after IX-5a is accepted; IX-5c shadow comparison. Enabling Tier 2 to serve needs a separate owner approval | — |

## 2. Reviewer questions: proposed owner positions

The table is grouped by subject. **Binds at** is the increment whose acceptance depends on the answer.

| TQ | Proposed position | Binds at |
|---|---|---|
| 1 (R-B completeness) | Accept for development. Proof obligation: IX-5c shadow comparison with zero unexplained differences, plus the M1 sweep | IX-5c |
| 2, 14, 22 (keying, SAFE, partitions) | Accept B2 with SAFE-1 to SAFE-11; no physical partitions without evidence (C-143). Plan-shape and statistics tests E-8/E-15 are required | IX-5b |
| 3, 4, 5 (sampling, M2) | The §17.10 deadlines are retained (C-143). The M2 split and the 14-day sampling are decided with IX-5c evidence | IX-5c |
| 6 (item identity stability) | **Not verified. A gating test is the first IX-5a deliverable;** if it fails, IX-5a stops and returns to the owner | IX-5a |
| 7, 15 (fence) | Accept F-1 (an empty affected set advances the fence). Reject the pending-record side path and F-2 (organization-activity timing) | IX-5a |
| 8, 10, 11 (verification lookups) | Decomposed lookups with team-scoped subjects (P-1 IX5-CT-11) at `at_exact_snapshot(pin)` (IX5-CT-12). The client enforces its own maximum, because the server refuses limits (IX5-CT-08); over-limit fails closed to Tier 1. The client always de-duplicates | IX-5a |
| 9 (microbenchmark) | Not needed now | — |
| 12 (trusted path) | Superseded by A-2. A maintainer failure quarantines Tier 2 for the organization; security commands are never aborted | IX-5a |
| 13 (B1) | B1 rejection confirmed | — |
| 16 (multi-path parity) | Accept the distinct-items rule; it must be tested for direct, team, overlapping, multi-select and repeating-group items | IX-5a, IX-5b |
| 17 (`contains`) | Tier 1 serves `contains` until a principal-bound strategy is evidenced | IX-5b |
| 18 (fan-out) | The fan-out unit is distinct principals per item (P-3: maximum 3 on current data). Records above K use the C path; K is set from evidence | IX-5b |
| 19, 24, 26 (UF-25, R-47) | Tier 1; UF-25 stays open. R-47 is conditionally accepted (C-143). Neither blocks IX-5a | — |
| 20 (value consistency) | Decided at IX-5b. The default is per-match re-evaluation against the sealed Tier 1 postings | IX-5b |
| 21 (copy cap) | No cap approved (C-141, C-143). Amplification is monitored (P-3 method); a cap needs evidence | IX-5b |
| 23 (fallback timing) | Accept the residual **for Development/Testing**. It is re-decided before serving is enabled, with P-2/P-4 evidence | IX-5c |
| 25 (FD-5-ALT) | Accepted as A-2 | IX-5a |
| 27 (R-52) | Accepted as engineering-remediated (C-144) | — |

## 3. Open design-author findings: proposed dispositions

| Finding | Disposition |
|---|---|
| UF-14 | Resolved by A-6 |
| UF-15 | Resolved by A-1 (principal-leading B-tree) |
| UF-16 | IX-5b requirement (TQ-20) |
| UF-17 | Annex E §5A cost model adopted; P-2 measures it |
| UF-18 | F-1 (TQ-7) |
| UF-20 | IX-5a requirement: the design §12.4 fencing also fences the Tier 2 maintainer and its checkpoints |
| UF-21 | IX-5a requirement: defined states `Disabled`, `Building`, `Ready`, `Quarantined`, with serving off in every state during IX-5 |
| UF-22 | SAFE conditions; E-15 at IX-5b |
| UF-28 | No cap, so there is no overflow channel; revisited only if a cap is proposed |

## 4. Carried, not waived

- R-02 (20 ms p95 commit gate) is still FAIL. A-2 adds no commit-path work; IX-5a must show the commit latency is unchanged (P-5).
- G14, Managed HSM, signed external checkpoints, PERF-01 staging, R-43 datasets, Stage B release run, PB-14 and R-50 remain open.
- Production readiness needs the independent third-party assessment (GA-IX5-1 §2.6).

## 5. Owner signature

| Item | Owner entry |
|---|---|
| Design A-1 to A-7 accepted (or the changes noted) | Approved (C-147), Development/Testing only |
| TQ positions (§2) accepted (or the changes noted) | Approved (C-147) |
| Finding dispositions (§3) accepted (or the changes noted) | Approved (C-147) |
| Acknowledgement: owner-led, not independent; no control waived | Acknowledged (C-147): residual risk of proceeding without an independent reviewer accepted for Development/Testing; external independent assessment remains unsatisfied and mandatory before production readiness |
| IX-5a implementation authorized | IX-5a only (C-147); first gate TQ-6, then the journal-completeness gate |
| Date and decision-log reference | 2026-11-10, C-147 |
