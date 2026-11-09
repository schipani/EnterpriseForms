# Tier 2 full equivalence sweep (M2): seven-day interval risk assessment (C-138 IX5-D5; review annex D)

- **Status:** risk assessment for the independent reviewer. **No production default is changed; no sweep exists.** Prepared by Claude (Opus) under C-138 §6; not an independent assessment.
- **Proposal under assessment:** design §17.1 M2 and §17.5 S-D. Every record is verified at least once per **7 days** against SpiceDB, plus a rotating user differential. Tier 2 is disabled when M2 exceeds **14 days**.

## 1. What M2 is for (and what it is not for)

| Threat or defect | Caught by | Detection bound | Role of M2 |
|---|---|---|---|
| Derivation defect on a record changed by a command or commit | M1 fence | **Before any Tier 2 search uses it** | None |
| Revocation not reflected in access rows (over-inclusion) | SpiceDB per-result decision; M1 | Per query (the search fails as a divergence) | None. Revocation safety does not depend on M2 |
| Grant not reflected (under-inclusion) on a changed record | M1 | Before use | None |
| **Affected-set defect:** a changed record never reported to M1 | M2 (and M3 tests before release) | **7 days (14 days until disable)** | Primary |
| **Authority-domain deletion of access rows after verification** | M2 | **7 days (14 days until disable)** | Primary |
| Authority-domain insertion of access rows | SpiceDB per result (the search fails) | Per query | Secondary |
| Schema (zed) change | Coupling guard at startup and in CI | Immediate (Tier 2 disabled) | After a derivation version change: rebuild and a full M2 before re-enabling |
| Divergence between the canonical edges and SpiceDB | S1 (journal and marker integrity); M1 and M2 comparisons escalate to S1 first (Q4) | S1 rules | Secondary |

M2 does not provide authorization freshness or revocation, which are per query or before use. **Its interval is the omission exposure window** for the two residual classes R-B leaves (design §17.1, "Residual after M1–M6").

## 2. The seven days in context

| Control | Detection bound for an omission | Source |
|---|---|---|
| Tier 1, deletion of a whole index entry | Every query (I-1) | §17.5 |
| Tier 1, change or deletion of a posting that removes a match | **24 h** (S-B deadline, no grace) | §17.10 (C-123, C-124) |
| Tier 1, derivation defect in postings | 30 days (S-C, no grace) | §17.10 |
| **Tier 2, deletion of an access row (Authority domain)** | **7 days, disable at 14** | §17.1 M2, §17.5 S-D |
| Tier 2, affected-set defect | 7 days, disable at 14 | Same |

**Findings:**
1. **Inconsistent with the no-grace rule.** C-123/C-124 removed the grace between scheduling target and deadline for S-A, S-B and S-C ("the comparison uses the deadline itself"). S-D still has a 7-day target and a 14-day disable threshold, which is the kind of grace the owner removed. If S-D follows §17.10, it would have:
   - a deadline of 7 days, with no grace;
   - a scheduling target inside it (for example 5 days);
   - the D1 to D8 rules (Pass, Fail, Inconclusive; per-record `last_access_verified_on`; monotonic clock K8 and K9).
2. **Weaker than the Tier 1 equivalent.** An Authority-domain actor who deletes a Tier 1 posting is detected within 24 h; deleting the corresponding Tier 2 access row hides the record for up to 7 to 14 days. Authority-domain manipulation is outside the Level 2 trust boundary (the Authority is trusted), but the design gives it explicit detection bounds, and these two bounds are inconsistent.
3. **The expensive part is optional for these two residual classes.** Both are caught by re-deriving the access rows from the canonical edges and comparing them with the stored rows. That comparison needs no SpiceDB and no HSM, only AuthorityDb reads and the pure derivation function, and it is **independent of the affected-set rule** (it recomputes every record). It does not catch a derivation that is wrong relative to the zed semantics; the SpiceDB comparison does.

## 3. Option for the reviewer: split M2

| Part | Compares | Catches | Cost | Proposed interval (for assessment) |
|---|---|---|---|---|
| **M2-E** (edge re-derivation) | Stored access rows with `AccessPostingDerivation(canonical edges)` for every record | Affected-set defects; Authority-domain deletion or insertion of access rows | O(rows) SQL and CPU; no external calls. Under option C ≈ 0.8M rows at 250K records (annex A §3.10) | **24 h**, aligning with S-B; possibly part of the hourly S-A |
| **M2-S** (SpiceDB equivalence) | Access rows with `LookupSubjects` (annex E) and the user differential | Derivation semantics relative to SpiceDB; edge and SpiceDB divergence (escalated to S1 first) | annex E §5: ≈ 9 to 207 lookups/s at 250K records over 7 days | **7 days** |

With the split, the omission window for the residual classes falls from 7 to 14 days to 24 hours, while the expensive SpiceDB comparison keeps its 7-day cycle. Both parts would use per-record timestamps, the §17.10 outcome classes and no grace.

**Caveat:** if the canonical edges themselves are altered in the Authority domain, M2-E re-derives from the altered edges and agrees with them. Only M2-S, S1 and the per-result SpiceDB decision see such a change. That is why M2-S cannot be removed.

## 4. Assessment of seven days

| Question | Assessment |
|---|---|
| Is 7 days sufficient for authorization freshness? | Not applicable: freshness is M1 (before use) |
| Is it sufficient for revocation? | Not applicable: revocation is per query |
| Is it sufficient for the integrity deadlines? | **Not consistent** with the 24 h S-B bound for the analogous Tier 1 manipulation, unless M2-E is added |
| Interaction with schema changes | None: the coupling guard disables Tier 2 at once; re-enabling requires a rebuild and a complete M2 (both parts) |
| Is it sufficient for index completeness? | It bounds omissions from affected-set defects and access-row deletion at 7 days (14 until disable). Acceptable only if the owner and reviewer accept that window for a residual that M3 should make rare; otherwise adopt M2-E at 24 h |
| Cost at 7 days | M2-S within plausible SpiceDB capacity for F ≤ 10; heavy at F = 250 (annex E §5). Unmeasured (PERF-01) |
| Availability | With no grace, a SpiceDB or worker outage longer than the slack (target to deadline) disables Tier 2. Fallback: Tier 1 within its ceiling, otherwise `SEARCH_INDEX_NOT_READY` (UF-11: V1 fallback limits) |

**The design author's reading (not a decision):**
- seven days is a reasonable cycle for M2-S, provided M2-E bounds the residual classes at 24 h;
- S-D should follow the §17.10 no-grace contract;
- the 14-day threshold should be removed.

## 5. Questions for the reviewer

| # | Question |
|---|---|
| TQ-4 | Is a 7-day (or 14-day) omission window acceptable for affected-set defects and Authority-domain access-row deletion, given the 24 h bound for Tier 1 postings? |
| TQ-5 | Should M2 be split into M2-E (24 h) and M2-S (7 days)? Should S-D follow §17.10 without grace? |
| — | Is the M2 user differential (`LookupResources` per user, rotating) needed in addition to per-record `LookupSubjects`, and over what rotation? |
