# Enterprise Forms C1 — Exact Form Designer UI replacement

This handoff compares the **actual target screenshot** supplied by the owner with the **actual current implementation screenshot**. It is a replacement-UI contract, not a suggestion to improve the existing card editor.

## Files

- `docs/ui/c1-designer-rebuild/TARGET_DESIGN.png` — **normative target for desktop layout, density, navigation and behavior**; reference for visual screenshot comparison
- `docs/ui/c1-designer-rebuild/CURRENT_IMPLEMENTATION.png` — the existing interface **to replace**, not the target
- `docs/ui/c1-designer-rebuild/DESIGN_AND_BEHAVIOR_CONTRACT.md` — normative layout, interactions, row/section structure, theme and scope
- `docs/ui/c1-designer-rebuild/ACCEPTANCE_TEST_MATRIX.md` — mandatory UI and regression acceptance tests
- `CLAUDE_C1_DESIGNER_PROMPT.md` — exact prompt to give Claude Code

## Install on Windows

1. Open the existing `EnterpriseForms` repository in VS Code, terminal at repository root.
2. Verify `git branch --show-current` says `phase-c/form-platform-expansion`; `Test-Path .\CLAUDE.md` should say `True`.
3. Ensure existing implementation changes are saved/committed before beginning. Do not destroy unfinished work.
4. Expand this ZIP **into the repository root**, not into an extra `C1_Exact_Form_Designer_Handoff` folder:
   `Expand-Archive -Path "$env:USERPROFILE\Downloads\EnterpriseForms_C1_Exact_Designer_Rebuild.zip" -DestinationPath . -Force`
5. Verify: `Test-Path .\docs\ui\c1-designer-rebuild\TARGET_DESIGN.png` = `True`.
6. Run Claude Code with **Opus** and give the full contents of `CLAUDE_C1_DESIGNER_PROMPT.md`, or tell it: `Read CLAUDE_C1_DESIGNER_PROMPT.md and execute it, starting by comparing both images and tracing the current UI.`

These files do not overwrite the project's existing CLAUDE.md or source code. The specification takes precedence over the older `C1_DESIGNER_UX_IMPLEMENTATION_BRIEF.md` **for designer look, row semantics and acceptance**, without altering security ADRs.
