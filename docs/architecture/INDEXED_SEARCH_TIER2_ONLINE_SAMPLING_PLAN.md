# Tier 2 online differential sampling: proposed acceptance plan (C-138 IX5-D3; review annex B)

- **Status:** **proposal for review, not an approved gate.** C-138 §4 names 14 consecutive days with zero unexplained differences as the initial candidate. The independent reviewer is asked to assess the period and the coverage. Nothing is implemented, and no production or real-data run is implied. Prepared by Claude (Opus); not an independent assessment.
- **Design basis:** §9.4 control 3 (M5): online sampling against Tier 1; §16 IX-5 exit gate ("online sampling clean over the agreed period").
- **C-139:** §7A adds the B2 comparisons, scenarios S-23 to S-28 and value-row positive controls. Still a proposal.

## 1. What a sample is

A **sample** is one eligible Tier 2 search whose population and result are compared with an oracle **at the same S1-pinned snapshot**:

| Compared | Option C | Option B |
|---|---|---|
| Population | `D` from access rows against SpiceDB `LookupResources(form_record, discover)`, and the per-predicate-field `field_item#read` scope | Per-condition candidate sets against Tier 1's candidate sets |
| Authorized result | Record ids after SpiceDB decisions, Tier 2 against Tier 1 | Same |
| Count and refusal class | Count; `SEARCH_VERIFICATION_LIMIT` and other refusal codes | Same |

**Oracle:**
- callers with `|D| ≤ 50,000`: Tier 1, the complete V1/S1-gated path;
- larger callers, in sampling only: a background SpiceDB `LookupResources` with a sampling-only higher maximum, never used to serve. That maximum is a cost bound. A caller above it is recorded as **Inconclusive (oracle limit)** and covered by the M2 user differential instead.

**Same snapshot.** Both computations run under one pin. If the authorization revision, the content-integrity revision or `form_content_revision` changes between them, the sample is **Inconclusive (snapshot changed)**, never Match. Samples taken in the dark stage are never served.

## 2. Outcome classes

| Class | Meaning | Effect |
|---|---|---|
| **Match** | Population, result ids, count and refusal class are all identical | Counts toward the clean period |
| **Difference** | Any inequality at a common snapshot | Critical alert; the S1 decisive verification first (Q4); then Tier 2 disabled for the organization; finding recorded; **the period restarts** |
| **Inconclusive** | Not comparable: snapshot changed, oracle refused (ceiling, limit, timeout), SpiceDB or Authority unavailable, Tier 2 not used (fence closed, disabled, suspended or quarantined) | Recorded with the reason; not counted as Match; a rate above the threshold (§5) fails the period |

**No difference is pre-classified as "explained."** A difference counts as explained only after a root-cause analysis attributes it to the sampling harness, not to Tier 2, and the owner accepts that attribution with evidence. Even then, the period restarts after the harness fix, because a harness defect means the samples before it may not have tested anything.

## 3. Proposed period and restart rules

- **Period:** 14 consecutive days (C-138 candidate).
- **The period restarts on:**
  * any Difference;
  * any change to:
    - Tier 2 code, the derivation version or the zed schema hash;
    - the PostgreSQL major version or the SpiceDB version;
    - the index migrations;
    - the planner settings;
  * a sampling outage longer than 6 hours;
  * a missed positive control (§4.3).
- **The period is extended (not restarted)** until the coverage matrix (§4) is complete.
- The clean period of one environment does not carry over to another environment or deployment.

## 4. Coverage

### 4.1 Scenario matrix (minimum occurrences in the period, each followed by sampled searches from affected and unaffected users)

| # | Scenario | Minimum |
|---|---|---|
| S-01 | `RegisterRecord`, user owner and team owner | 1,000 |
| S-02 | `RegisterRecord` of a child (parent edge), nesting depth 1 and maximum | 100 |
| S-03 | `CreateShare`, whole record (user and team recipient) | 200 |
| S-04 | `CreateShare`, partial field scope including multi-select items | 200 |
| S-05 | `RevokeShare`, including during pagination of a stored result | 200 |
| S-06 | `TransferOwnership`: user→user, user→team, team→user, team→team, with descendants | 50 each |
| S-07 | `UnlinkChildRecord` | 50 |
| S-08 | `AddTeamMember` and `RemoveTeamMember` for teams that own or receive shares | 200 |
| S-09 | `DisableIdentityBinding` and `EnableIdentityBinding` | 20 |
| S-10 | `ApproveFieldSearch`, `WithdrawFieldSearch`, `ReclassifyField`, and the builder backfill or purge that follows | 10 each |
| S-11 | Content commits that add, replace and remove share-covered field items | 1,000 |
| S-12 | Concurrent security commands and searches in the same organization | Continuous |
| S-13 | Search during an open M1 fence (Tier 2 must not be used) | 100 |
| S-14 | Schema change: deploy a changed zed hash in the sampling environment. Expected: Tier 2 disabled by the coupling guard; samples Inconclusive; then rebuild, full M2 and re-enable | 1 |
| S-15 | Search-index quarantine (injected Fail), `SearchProjectionRebuild`, recovery | 2 |
| S-16 | Search-index suspension (overdue scope), catch-up, automatic clearing | 2 |
| S-17 | S1 organization quarantine (injected). Expected: 423, Tier 2 and Tier 1 refused; the organization leaves the sample. **Recovery cannot be exercised: G14 is not implemented**; recorded as a limitation | 1 |
| S-18 | Tier 2 disablement by an injected divergence: fallback to Tier 1, alert, rebuild, verification, re-enable | 2 |
| S-19 | Authority restart, SpiceDB outage, projection lag, archive delay | 2 each |
| S-20 | Two Authority instances (if the environment runs two; R-23, R-42) | Continuous |
| S-21 | Callers with `|D|` in each band: 0, 1–100, 100–10K, 10K–50K, above 50K | 50 per band |
| S-22 | Predicate shapes: equality, range, prefix, `contains`, multi-select membership, AND of two conditions, missing value | 50 each |

### 4.2 Population

- At least three synthetic organizations.
- At least one at the 100K-record scale (annex C), when available.
- Team, share and nesting distributions as in annex C §3.
- Synthetic data only.

### 4.3 Positive controls (detector validation)

Zero differences proves nothing unless the sampler is shown to detect real ones. Every day, in a dedicated canary organization:
- a deliberately faulty derivation build (for example, one that drops `Share` rows for team recipients), or a direct test-only deletion of one access row;
- the sampler must report a Difference for an affected search within the same day.

A missed control restarts the period. Canary organizations are excluded from the clean count, and their alerts are labelled as controls.

## 5. Exit criteria (proposed)

1. 14 consecutive days without a restart event (§3).
2. Coverage matrix complete (§4.1).
3. Every positive control detected (§4.3).
4. Zero Differences in non-canary organizations.
5. Inconclusive samples below 5 % of eligible samples, excluding S-13 to S-19 where Inconclusive is expected. Every Inconclusive reason is reported.
6. Evidence complete (§6) and linked to the commit, image digests and environment (PB-12).

## 6. Evidence

Per day:
- eligible searches, samples, Match, Difference, Inconclusive by reason;
- scenario counts;
- positive-control results;
- `|D|` bands;
- predicate shapes;
- lookup and oracle costs.

Per sample, stored with the run:
- digests (SHA-256) of the sorted id lists;
- counts;
- the snapshot revisions.

No field values, predicate values, record ids or user ids appear in exported evidence. Ids remain only in AuthorityDb for the retention period needed to investigate a Difference.

## 7. Cost and placement

- **Cost:** the oracle doubles the SpiceDB enumeration cost of every sampled search. That is the cost Tier 2 is meant to remove, so 100 % sampling is affordable only in a pre-production environment.
- **Pre-production:** sample 100 % of eligible searches.
- **Production (if ever approved):** a configured fraction with a minimum daily count. The production fraction and period need a separate owner decision.
- **Placement:** the sampling environment must be a non-production environment with continuous synthetic workload: Azure staging under IX-7, or a long-running local stack. C-138 does not start IX-7. A clean period there is evidence for **that implementation under that workload**, not for production traffic.

## 7A. C-139 additions (B2 preferred for review; still a proposal, not an approved gate)

**Comparison under B2.** In addition to the population and result comparison of §1:
- the per-condition candidate set, after the parity rule, is compared with Tier 1's readable-for-predicate scope filtered by the same predicate, at the same pin;
- for 1 % of samples, the matched records' value rows are re-derived from the sealed postings and verified access rows (an online M2-V spot check).

**Additional scenarios (minimum occurrences in the period):**

| # | Scenario | Minimum |
|---|---|---|
| S-23 | Multi-path field readability: the items of one field readable only through different principals (direct share + team share); searches by the recipient (UF-23) | 100 |
| S-24 | Content commits that change the item count of a shared multi-select field (`field_item_count`), including adding an unshared selection | 200 |
| S-25 | `ApproveFieldSearch` and `WithdrawFieldSearch` with value-row backfill and purge; searches during backfill must use Tier 1 for that field (UF-24) | 10 each |
| S-26 | Records crossing the fan-out cap (D-5) in both directions; searches over high-fan-out records through the C path | 20 |
| S-27 | Membership churn under F-1: `AddTeamMember` and `RemoveTeamMember` at high rate, with searches by affected and unaffected members between commands | 1,000 |
| S-28 | Overlapping direct and team access, revoking one path while the other remains | 200 |

**Population:**
- at least one organization with thousands of users and hundreds of teams;
- records shared with many principals (above and below the cap);
- callers in every \|D\| band of S-21, including whole-organization callers.

**Positive controls for value rows:**
- a test-only deletion of one value row of a matching item;
- a wrong `field_item_count`.

Each must be reported as a Difference (or an M2-V Fail) within the same day.

**Unchanged:** the period (14 days), the exit criteria (§5) and the evidence rules (§6) are still proposals for the reviewer (TQ-3).

## 8. Questions for the reviewer

| # | Question |
|---|---|
| TQ-3 | Is 14 days sufficient, and are the scenario minimums sufficient? Should the period be expressed as a minimum number of security commands and samples per scenario rather than days? |
| — | Is the oracle for `|D| > 50,000` (sampling-only `LookupResources` with a higher bound) acceptable? |
| — | Are the positive controls (§4.3) sufficient to show the detector works? |
| — | Is 5 % the right Inconclusive threshold? |
