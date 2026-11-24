# Phase C9 — manual accessibility review checklist (form version management)

**Status: OPEN — manual evidence outstanding.** This checklist is to be executed by a human reviewer with assistive
technology; no manual result exists. Automated checks (section A) are recorded separately and do not establish
screen-reader output, reading order, focus quality, zoom or reflow. Nothing here claims WCAG 2.2 AA or AODA conformance
or certification (PB-14, ADR-PB-014). The owner decided (C-187) that this review runs alongside M3 and is not a
prerequisite for starting it; final C9 acceptance stays blocked on it unless the owner explicitly resolves that
dependency.

## A. Automated results (executed separately; see `docs/PHASE_C9_STATUS.md` §10)

| Source | Surfaces | Kind of check |
|---|---|---|
| `src/frontend/e2e/form-version-behavior.spec.ts` (M1) | Record layout dialog; record page notices; editor "layout changed" state; withheld changes | axe-core (WCAG 2.0/2.1/2.2 A/AA tags), keyboard-free role/name queries |
| `src/frontend/e2e/form-version-comparison.spec.ts` (M2) | Publish review dialog (including the failed-review state); Versions tab comparison | axe-core |
| `src/frontend/e2e/form-version-acceptance.spec.ts` (M3) | All of the above in one workflow; record page under Latest ("Layout v2 (created with v1)") and Hybrid; v3 → v4 comparison | axe-core |
| Vitest component tests (`versionComparison.test.tsx`, `VersionBehaviorDialog`, `layoutReload.test.ts`, upload manager) | Dialog options, publish gate notes, comparison groups, reload merge | Role/name/state assertions in jsdom |

## B. Reviewer, environment and assistive technology

- **Reviewer:** a person experienced in manual screen-reader and keyboard testing (NVDA and VoiceOver), designated by the
  owner. If the reviewer is the owner or works on this project, the result is recorded as an owner-led review and never
  described as an independent assessment, attestation or certification.
- **Environment:** a local isolated stack with synthetic data only — the security-test stack
  (`scripts/dev/test.ps1 -Level 2 -Spec form-version-acceptance` leaves a form with five versions, a Hybrid record and
  a reload with a withheld change), or the owner's development stack with a synthetic organization. Never production
  data. Personas: `designer` (form design), `clerk` (record owner).
- **Assistive technology (record exact versions):** NVDA (current release) with Chrome or Edge on Windows 11; VoiceOver
  (current macOS) with Safari. Keyboard only (no pointer) for every flow. Browser zoom 200 % and 400 %; viewport widths
  320, 768 and 1440 CSS px.

## C. Surfaces in scope and how to reach them

| # | Surface | How to reach it |
|---|---|---|
| S1 | Form settings card, "Record layout" row and the **Record layout** dialog (three options, impact summary, reason, validation) | Designer → form → Settings tab → "Change record layout" |
| S2 | **Publish a new version** dialog: data statement, attention/information notices, approximate counts, comparison, change reason, publish gate notes (loading, failed with "Try again", stale, blocked) | Designer → change the draft → Save draft → Publish |
| S3 | **Compare versions** panel on the Versions tab: "Compare from"/"to" selects, Swap, summary sentence, Added / Removed from the layout / behaviour groups, collapsed presentation-only changes, "Comparison reset" notice | Designer → Versions tab |
| S4 | Added, removed, behaviour and presentation changes as read in S2 and S3 (each group's list name, the "Stored values are kept" statement) | S2 / S3 with a draft that adds, removes, tightens and relabels fields |
| S5 | Record version indicators: badge "Layout vN" / "Layout vN (created with vM)"; Hybrid notice "Shown with layout vN; edits use layout vM"; "This record can't be edited right now" | Clerk → record page |
| S6 | Historical / Latest / Hybrid explanations: the dialog's option descriptions and the editor notices ("Editing with layout vN, the version this record was created with", "Editing with the current layout vN") | S1; Clerk → Edit |
| S7 | Stale-layout warning and recovery: "The form layout changed while you were editing", "Reload with the new layout and keep my changes", "Discard my changes and reload" | Clerk opens Edit; designer publishes; clerk saves |
| S8 | Withheld changes and validation messages: "Some of your changes can't be carried over" and the list "Changes not carried over"; field errors after reload (new required field, tightened rule) | S7 with a field removed or tightened in the new version |

## D. Screen reader (per surface)

| # | Check | NVDA + Chrome/Edge | VoiceOver + Safari |
|---|---|---|---|
| 1 | S1: dialog title announced on open; focus moves into the dialog and returns to "Change record layout" on close; Esc closes | ☐ | ☐ |
| 2 | S1: radio group name and each option's label and description announced; the current setting identified in words ("current setting"), not by colour | ☐ | ☐ |
| 3 | S1: impact summary announced when the option changes (counts, "approximate", "only records you can discover", or "You can't see any records of this form.") | ☐ | ☐ |
| 4 | S1: reason field label and the 3–500 character rule announced; the validation error is announced and associated with the field | ☐ | ☐ |
| 5 | S1: "Record layout changed" status announced after saving | ☐ | ☐ |
| 6 | S2: dialog title; reading order data statement → notices → counts → comparison → change reason → actions | ☐ | ☐ |
| 7 | S2: publish gate: while the review loads, fails or is stale, the reason Publish is unavailable is announced; "Try again" operable; Publish announced as unavailable/available correctly | ☐ | ☐ |
| 8 | S2/S4: each list (Added, Removed from the layout, behaviour changes) is announced with its name and item count; "Stored values are kept" announced with removals | ☐ | ☐ |
| 9 | S3: both selects announce their label and chosen value; Swap operable; the summary sentence ("v3 → v4: 1 added.") announced after a change | ☐ | ☐ |
| 10 | S3: presentation-only changes disclosure announces its expanded/collapsed state; "Comparison reset" notice announced | ☐ | ☐ |
| 11 | S5: the badge text is read with the record heading region; the Hybrid notice is read as an information message, not an error | ☐ | ☐ |
| 12 | S6: editor notice read before the first field; it states which version the edit validates against | ☐ | ☐ |
| 13 | S7: the stale-layout message is announced when it appears (alert), distinct from "Someone else saved this record"; both actions reachable and announced | ☐ | ☐ |
| 14 | S7: after reload, focus lands on a meaningful element (not the page body); the new layout version is announced | ☐ | ☐ |
| 15 | S8: "Some of your changes can't be carried over" announced; the list items read "Field: you had entered …"; field errors announced and linked to their fields | ☐ | ☐ |
| 16 | No surface announces a stored value of a field the person cannot read, or a record count beyond their discoverable records | ☐ | ☐ |

## E. Keyboard, focus, responsive and zoom (S1–S8)

| Check | 320 px | 768 px | 1440 px | 200 % zoom | 400 % zoom |
|---|---|---|---|---|---|
| Every action reachable by keyboard in reading order; visible focus; no keyboard trap | ☐ | ☐ | ☐ | ☐ | ☐ |
| Dialogs (S1, S2) fit the viewport, scroll internally, keep focus inside and restore it on close | ☐ | ☐ | ☐ | ☐ | ☐ |
| No horizontal page scroll; comparison lists and notices wrap | ☐ | ☐ | ☐ | ☐ | ☐ |
| Text not clipped or overlapping; touch targets ≥ 24 × 24 px | ☐ | ☐ | ☐ | ☐ | ☐ |
| Change types and notices are not conveyed by colour alone (Added / Removed / Attention / Information in words) | ☐ | ☐ | ☐ | ☐ | ☐ |

## F. Evidence template (blank until performed)

| Field | Value |
|---|---|
| Reviewer (name, role) | |
| Kind of review (owner-led / designated reviewer / independent) | |
| Date | |
| Commit reviewed | |
| Environment (stack, organization, personas) | |
| NVDA version, browser and version, Windows version | |
| VoiceOver / macOS version, Safari version | |
| Checks D1–D16 and E: pass / finding per row | |
| Findings (id, surface, severity, description, WCAG criterion) | |
| Corrections made and re-test result | |
| Outcome | ☐ accepted ☐ accepted with findings ☐ rejected |

Findings are recorded in this file (or a linked file under `docs/evidence/phase-c9/`) and in `docs/PHASE_C9_STATUS.md`.
