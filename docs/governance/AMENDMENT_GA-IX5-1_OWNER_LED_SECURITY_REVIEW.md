# Governance amendment GA-IX5-1: owner-led security review replaces the independent pre-implementation review for IX-5

- **Status:** **In force for Development/Testing: approved by the owner in C-147 (2026-11-10)** as an explicit owner-led security approval, not an independent security assessment. Drafted under C-146; design baseline `7ec5a79`.
- **Decision kind:** 1 and 3 under [REVIEW_AND_AUTHORIZATION_WORKFLOW.md](REVIEW_AND_AUTHORIZATION_WORKFLOW.md) §1 (owner-led review and implementation authorization).
- **Authority:** the project owner, who set the clauses amended here (C-121, C-122, C-138, C-139).

## 1. What this amends

The following clauses make an **independent review** a precondition for **implementing** Tier 2 (IX-5):

| Clause | Source | Requirement |
|---|---|---|
| IX-D13 | C-121; design §15 | "independent review and separate approval before implementation or enablement" |
| R-B | C-121; ADR-PB-004 Amendment A2 §3 | "require independent security assessment before implementation" |
| §17.1 status, FD-3 | C-122; design §17.1, §17.7 | Tier 2 completeness design approved for review only |
| FD-8 | C-122; design §17.7 | Submission to a qualified independent reviewer |
| IX5-D1 | C-138; design §24 | Review-first; no exception for a dark implementation |
| C-139, C-143 | design §25; review package §14B rows 1 and 9 | The prerequisite is unchanged |

**Amendment.** For IX-5 only, the precondition in each of these clauses is replaced by:

1. an **owner-led security review**, performed by the owner personally; and
2. the owner's signature on [INDEXED_SEARCH_IX5_DESIGN_ACCEPTANCE_RECORD.md](../architecture/INDEXED_SEARCH_IX5_DESIGN_ACCEPTANCE_RECORD.md); and
3. a separate owner implementation authorization for each increment: IX-5a, then IX-5b, then IX-5c.

Enabling Tier 2 to serve searches needs a further, explicit owner approval after IX-5c.

## 2. Statements recorded by this amendment

1. **The owner is the reviewer and the approval authority** for the IX-5 security design.
2. **The review is not independent.** The owner commissioned the design, and Claude wrote it; neither is independent of it. The review must never be described as "independent", "third-party", "attested" or "certified".
3. **The independent-review requirement is replaced, not satisfied.** The replacement is an owner-authorized governance amendment, limited to the clauses in §1.
4. **The residual risk of reduced reviewer independence is acknowledged:**
   * the design author's and the owner's shared blind spots are not challenged by an outside party;
   * in particular, no external adversarial assessment covers:
     - timing and statistics channels (TQ-14, TQ-23, UF-22, UF-28);
     - Tier 2 completeness by derivation equivalence (R-B, TQ-1);
     - FD-5-ALT checkpoint integrity (TQ-25);
   * these errors would be found later, or not at all.

   The owner accepts this risk **for Development/Testing implementation only.**
5. **No security control or technical acceptance criterion is waived.** All of these remain mandatory:
   * SAFE-1 to SAFE-11;
   * the Tier 2 invariants (INV-T2-*);
   * fail-closed behaviour and SpiceDB deciding every disclosed result;
   * ADR-PB-004 searchability limits;
   * the evidence register (R-xx), the release gates, PB-09/PB-14 and Stage B.
6. **External independent-attestation requirements remain unsatisfied.** This amendment does not change any of the following, which only their own governing bodies can amend:
   * workflow kinds 2 and 4, and workflow rule 4;
   * ADR-PB-003 (Azure staging adversarial proof);
   * ADR-PB-011 and ADR-PB-012 (independent Security Owner review and independent production approval).

   **Tier 2 cannot be production-ready without the independent third-party assessment.**
7. The FD-8 packages ([INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md](../architecture/INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md), [INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md](../architecture/INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md) at `f5b1ca6`) are retained, unsent, for that future assessment. Their attestation templates stay blank.

## 3. What does not change

- Every other owner decision, the workflow's four kinds of decision and its rules 1 to 6.
- Tier 1 (IX-1 to IX-4) acceptance conditions. UF-25 stays open.
- IX-6 and IX-7 are not authorized until IX-5 is accepted.

## 4. Owner confirmation

| Item | Owner entry |
|---|---|
| I confirm GA-IX5-1 as written (or with the changes noted) | Approved as written, for Development/Testing only (owner message C-147) |
| Decision-log reference | C-147 |
| Date | 2026-11-10 |
