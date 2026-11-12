# Review and authorization workflow

- **Status:** adopted by owner direction C-123 (2026-11-06).
- **Applies to:** every design, implementation stage and release decision in EnterpriseForms, starting with the indexed-search work (Option C).
- **Precedent:** Stage A decision A-23 (`docs/STAGE_A_GATE_CLOSURE.md`): "owner review — not an independent third-party review".

## 1. The four kinds of decision

These are four separate kinds of decision. None of them substitutes for another, and none may be described as another.

| # | Kind | Who | What it may decide | What it is not |
|---|---|---|---|---|
| 1 | **Owner-led architectural and security review** | The project owner, personally, as the designated owner reviewer. Claude may prepare evidence for it, but is never the reviewer | Accept or reject design claims, findings and risks; approve design decisions; record accepted development-stage risks | **Not** an independent third-party security assessment or attestation. Not production approval |
| 2 | **Independent third-party security assessment** | A qualified human reviewer independent of the design, implementation, testing and documentation (PB-12 independent Security Owner sense). Not the owner, not Claude, not any AI agent acting for either | Findings, severities and an attestation on a named commit | Not an implementation authorization. Its absence is never waived by an owner review |
| 3 | **Implementation authorization** | The project owner | Authorize a named stage (for example IX-1) to be built, in Development/Testing, on the basis of an owner-led review | Not a statement that the design is independently assessed, nor that the stage is production-ready |
| 4 | **Production-readiness approval** | The release process of ADR-PB-012: independent production approver, separately permissioned Authority approval, executed evidence | Promote a specific image digest to production | Cannot be granted without the independent third-party assessment of the relevant controls, PERF-01, Azure staging evidence and PB-12 evidence |

## 2. Rules

1. An owner-led review **may** authorize design decisions and implementation stages (kinds 1 and 3).
2. An owner-led review **must not** be represented, recorded or labelled as an independent third-party security assessment or attestation. Wording such as "independently reviewed", "attested" or "certified" is reserved for kind 2 and kind 4.
3. **No fictitious attestation.** No document may contain a completed independent-review attestation unless a qualified independent human reviewer delivered it. The attestation templates stay blank until then.
4. **Independent third-party security assessment remains an outstanding production-readiness requirement** for every Level 2 control it covers. It is listed as open on every status page until it is delivered.
5. Evidence prepared by Claude for an owner-led review is labelled as such. It distinguishes:
   - code facts checked by inspection (with file and line);
   - design clauses;
   - claims not yet experimentally verified.

   It never states that a design guarantee was experimentally verified unless executed test evidence exists.
6. Each owner decision is recorded in the decision log with its kind (1, 3 or both) and its scope.

## 3. Current state for indexed search (Option C)

| Kind | State |
|---|---|
| 1 Owner-led review | C-120 to C-122 decisions recorded. Tier 1 owner-review evidence prepared for C-123 (`docs/architecture/INDEXED_SEARCH_TIER1_OWNER_REVIEW_EVIDENCE.md`); the owner's decisions on it are pending |
| 2 Independent third-party assessment | **Not performed.** Package ready (`docs/architecture/INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md`). Outstanding production-readiness requirement |
| 3 Implementation authorization | **None.** IX-1 to IX-7 not authorized; Tier 2 design-only |
| 4 Production readiness | Not started (needs kind 2, PERF-01, Azure staging, G14, PB-12 evidence) |
