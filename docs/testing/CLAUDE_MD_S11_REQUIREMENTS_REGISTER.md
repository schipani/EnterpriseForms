# CLAUDE.md §11 requirements traceability register

**Created 2026-11-25 under owner decision C-193 (RM-5); a mandatory project governance artifact from C-194.** Data: `CLAUDE_MD_S11_REQUIREMENTS_REGISTER.csv` (one row per
CLAUDE.md §11 acceptance criterion, #1…#82). This register records evidence; it does not change, reinterpret or close any
criterion, and a milestone assignment is not a finding closure.

## Columns

| Column | Meaning |
|---|---|
| `criterion_number` | CLAUDE.md §11 number |
| `criterion_text_verbatim` | The criterion text copied mechanically from CLAUDE.md §11 (whitespace normalized). It governs; the other columns never redefine it |
| `status` | One value from the vocabulary below, as of `status_date` |
| `evidence` | Existing tests, specifications and status records that exercise the criterion (each reference was checked to exist) |
| `acceptance_authority` | Who may accept the criterion at each level: `owner (Dev/Test)`, `owner + independent reviewer`, `staging evidence + independent reviewer` |
| `outstanding_obligations` | What remains before the criterion can be fully accepted |
| `milestone` | The approved-roadmap milestone(s) that own the remaining work, primary first (`docs/architecture/FORWARD_ROADMAP_PROPOSAL.md`) |
| `notes` | Conflicts with later accepted decisions (for example ADR-PB-004 encryption-first search) are recorded here without redefining the criterion |

## Status vocabulary (weakest applicable value used)

| Status | Meaning |
|---|---|
| `DEV-ACCEPTED` | Development/Test evidence accepted by a recorded owner decision for this criterion. Not production acceptance, not independent validation |
| `DEV-EVIDENCE` | Implemented; automated tests covering the main assertions exist and a status record shows them passing in Development/Test. No owner acceptance of the criterion recorded |
| `PARTIAL` | Part of the criterion is implemented or tested; the gap is named |
| `NOT-IMPLEMENTED` | The capability does not exist |
| `UNMET-DEFERRED` | An owner decision explicitly deferred it |
| `NOT-ASSESSED` | Evidence could not be established either way in the creating work package |

No row may be `MET`: full acceptance of a criterion that requires staging, production or independent evidence is outside
Development/Test, and the register records those requirements under `outstanding_obligations`.

## Maintenance rules

1. A status may move up only with cited evidence; `DEV-ACCEPTED` only with a recorded owner decision.
2. Every milestone that touches a criterion updates its row (evidence, status, obligations, `status_date`) in the same
   change.
3. Rows #1–#6 were consolidated by C9 M3 and accepted for Development/Test by C-193 C9-A; row #2 carries the five-part
   disposition of `docs/PHASE_C9_STATUS.md` §12.5.
4. Rows #7–#82 were mapped from repository evidence in the C9 close-out (2026-11-25) by read-only investigation; they have
   not been reviewed criterion-by-criterion by the owner. Treat `DEV-EVIDENCE` as a pointer to evidence, not a verdict.
5. Phase C19 completes the register with results for every row.
6. **Mandatory at every milestone (C-194):** update the affected rows in the same change; record exact supporting evidence;
   preserve the original criterion text; distinguish implementation, evidence, development acceptance, persistent
   activation, staging validation and full acceptance (in `status`, `outstanding_obligations` and `notes`); preserve
   unresolved conflicts and obligations; never infer acceptance from a merge or a passing test alone.
7. A conflict between a criterion and the approved security architecture (for example #13 and ADR-PB-004) is resolved only
   by an explicit owner decision, recorded in `notes` and in the decision log.
