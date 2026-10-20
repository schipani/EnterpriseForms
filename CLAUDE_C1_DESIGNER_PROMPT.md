# Claude Code — Execute C1 Designer Rebuild (not another partial UI tweak)

You are working on `phase-c/form-platform-expansion` in the existing Enterprise Forms repository. **Use the actual two screenshots supplied by the owner**. Read these files BEFORE making any changes:

- `docs/ui/c1-designer-rebuild/TARGET_DESIGN.png` — what must be built
- `docs/ui/c1-designer-rebuild/CURRENT_IMPLEMENTATION.png` — what must be replaced
- `docs/ui/c1-designer-rebuild/DESIGN_AND_BEHAVIOR_CONTRACT.md` — binding implementation requirements
- `docs/ui/c1-designer-rebuild/ACCEPTANCE_TEST_MATRIX.md` — mandatory acceptance checks
- `CLAUDE.md`, `CLAUDE-DESIGN.MD`, ADR-PB-014 and all relevant platform/security ADRs

**My instruction is to REBUILD THE EXISTING FORM DESIGNER PRESENTATION TO MATCH TARGET_DESIGN.png. Do not merely add controls to the current card-based layout.** The previous implementation did that and I reject it.

## First, investigate and plan

1. Locate the actual current designer route/components/CSS, draft/version layout schema, metadata APIs, and tests. Identify which parts are existing functionality that must survive (form permissions, related children, document collections, saved field values, encryption governance).
2. Produce a concise screenshot comparison (target vs current) and a concrete file-level implementation plan. Then IMPLEMENT it; do not stop after writing a plan.
3. Replace the old per-field giant configuration cards and unconnected Preview side card. The new center canvas displays compact realistic field previews, selected by clicking; the right inspector owns common edits.

## Mandatory user experience

- Desktop: left vertical Designer navigation; center toolbar + compact titled-section canvas; right persistent tabbed Field/Section Properties inspector. Use current global app shell, don't duplicate it.
- The toolbar **`+ Add Row` MUST create a NEW TITLED SECTION** (`New section`) containing its own four-column placement grid and an `+ Add Field` slot. This is *not* the existing Start-on-a-new-row flag. You can rename/reorder/collapse the section and insert/move multiple fields into it.
- Clicking `+ Add Field` inside a section puts it in that section and cell, not at the form end. Drag/reorder/resize with accessible keyboard alternatives; support 1–4 column spans without collisions.
- Selected field has one compact tile, a selection outline and live inspector. Inline label editing preserves stable IDs and stored values.
- The right inspector has consistent **General, Validation, Rules, Security & Encryption, Advanced** tabs; meaningful quick edits; secure governance actions route to existing Authority-controlled processes.
- Preserve permissions, document collections, related-form lists, draft/publish/history and previous data. Existing flat form layouts need a safe default-section interpretation or versioned non-destructive migration.
- Undo/Redo, Save draft, Preview, Publish and unsaved-change handling must work with sections and placements.
- ALL table actions go in the rightmost Actions column with consistent buttons and `...` secondary menu.

## Theme — Do not misread the image

The screenshot depicts the **DEFAULT** theme. Default primary buttons, checked checkboxes, radios, toggles and active tab underlines are **black**, with neutral secondary buttons. Blue may mark **selected canvas cells**, focus/selection, and the five explanatory blue numbered reference callouts—not random buttons, tabs or switches. Existing **organization owner/settings admin theme choices must override the default consistently throughout the site through semantic theme tokens**, maintaining approved accessibility contrast. Do NOT hard-code black for all organizations. The target screenshot happens to show a blue General underline; normalize **every** active properties tab to the same default-black theme styling.

## Definition of done

1. Produce a real running **1440px screenshot** of your new Designer, and explicitly compare it side-by-side with TARGET_DESIGN.png. Do not claim a visual match without actually rendering and inspecting it. Correct structural discrepancies before asking for owner acceptance.
2. Demonstrate in the browser: Add Row -> rename section to `Contact Information` -> add Address Line 1 and City within section -> move one existing field into section -> save -> reload -> preview -> publish -> view the actual record renderer showing the section.
3. Demonstrate field click-to-edit via inspector, version-safe rename, tab navigation and real persistence, current document/related lists, accessible mobile inspector, consistent last Actions column.
4. Run and show actual results for unit tests, front-end tests, real Docker integration, Playwright designer and section persistence, and SEC-P regression (do not weaken). Capture evidence and screenshots.
5. Update UI design standards and `docs/PHASE_C1_UX_STATUS.md`; list all remaining limitations and human/manual accessibility reviews honestly.
6. Commit and push to `phase-c/form-platform-expansion` after verified success. Open/update the PR against `main`, do not merge it without my authorization. Do not implement unrelated Phase C features or deploy production.

**Do not stop at the previous interpretation.** The existing card UI is the example of what NOT to deliver. Implement the target layout and working Add Row sections, not a CSS-only facelift. Request owner review of the actual rendered UI before marking this milestone accepted.
