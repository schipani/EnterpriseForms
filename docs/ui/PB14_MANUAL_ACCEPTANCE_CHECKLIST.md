# PB-14 Manual Design and Accessibility Acceptance Checklist — Stage B representative screens

**Scope:** ADR-PB-014 validation of the three representative flows (PG-006 dashboard, PG-010/016–021 Accused
workspace, PG-029/033/037 designer) and the internal PG-069 gallery as implemented in Stage B.
**Status:** automated checks executed (see §1); **human review items in §2 are PENDING and reserved for the project
owner / reviewers**. Nothing in this document is an AODA or WCAG certification claim.

Register of record: `docs/testing/PB14_UI_DESIGN_ACCEPTANCE_TESTS.csv` (PB14-01..36).

## 1. Automated evidence (executed by `src/frontend/e2e/pb14-accessibility.spec.ts`, `responsive.spec.ts`, vitest)

| Register id | What the automation checks | Where |
|---|---|---|
| PB14-01, 03, 04, 05 | Default black/white primary button, state tokens, focus ring, checkbox states — axe colour-contrast rules on the gallery and real screens; `packages/design-tokens` contrast unit tests | `pb14-accessibility.spec.ts`, `contrast.test.ts` |
| PB14-02, 06, 08, 35, 36 | Server rejects inaccessible palettes / unsafe logos; theme validation | `StageBUnitTests.ThemeContrastRejectsInaccessiblePairsAndAcceptsTheDefault`, `branding.test.ts` |
| PB14-10, 11, 12 | 320 / 768 / 1440 px: axe WCAG 2.2 AA (plus best-practice) with zero violations and no horizontal overflow on login, dashboard, records, record (details + access), Upload Center, gallery, designer (fields + layout) | `pb14-accessibility.spec.ts` test 1 |
| PB14-13, 14 | 200% / 400% zoom equivalents (CSS viewport 720 px and 320×256 px) on the same screens | `pb14-accessibility.spec.ts` test 1 |
| PB14-15 | Keyboard only: skip link first, WAI-ARIA tabs by arrow key on the record, designer dialog focus trap and Escape-return, designer reorder with the accessible Move buttons | `pb14-accessibility.spec.ts` test 2; `responsive.spec.ts` (gallery tab order, visible outline) |
| PB14-22, 23 | Accused record view/edit/addresses, documents and upload tray exercised end to end in a real browser | `accused-workflow.spec.ts` |
| PB14-25 | Designer keyboard reorder | `pb14-accessibility.spec.ts` test 2; `layoutEdits.test.ts` |
| PB14-28 | Validation summary announced (`role=alert`) on the record form | `accused-workflow.spec.ts` ("required", "UUID") |
| PB14-29 | Dialog focus trap / Escape | `pb14-accessibility.spec.ts` test 2 |
| PB14-30 | Forced colours + reduced motion: focus outline still visible, axe clean on login | `pb14-accessibility.spec.ts` test 3 |
| PB14-32 | The three representative flows run against the real stack | `accused-workflow.spec.ts`, Stage B integration tests |

Automation limits (stated, not hidden): axe covers roughly 30–40% of WCAG success criteria; the zoom checks use
narrow CSS viewports rather than browser zoom; screen-reader semantics, reading order judgement, meaningful
alternative text and cognitive clarity require the human items below.

## 2. Human review items — PENDING (owner / assigned reviewers)

Record the reviewer, date, build (commit SHA + image digest once PB-12 evidence exists), assistive technology and
browser versions, and the verdict for each row. Any failure is a Stage B defect to fix, not a note.

| Register id | Item | Steps | Pass criteria | Result |
|---|---|---|---|---|
| PB14-16 | NVDA + Chrome/Firefox on Windows | Sign in as clerk; dashboard → Records → open an Accused record → Details, Access, Activity tabs → Edit → add an address → upload a document → Upload Center | Landmarks, headings, tab names, table headers, form labels, errors, live progress and status badges are announced; nothing is announced only by colour | ☐ pending |
| PB14-17 | VoiceOver + Safari on macOS | Same route as PB14-16 | Same criteria; rotor lists headings/landmarks/form controls correctly | ☐ pending |
| PB14-13 (human) | Real browser zoom 200% at 1280 px | Dashboard, record, designer layout tab | No clipped content, no two-dimensional scrolling for text content, controls remain operable | ☐ pending |
| PB14-14 (human) | Real browser zoom 400% at 1280 px (= 320 px reflow) | Same screens | Single-column reflow, sticky elements do not hide content, dialogs scroll internally | ☐ pending |
| PB14-07, 09 | Organization logo upload + user text size 100/125/150% | Branding page (owner persona), Preferences page | Logo top-left, accessible fallback name, text scale applies everywhere without overlap | ☐ pending |
| PB14-19, 20 | Dashboard cards refresh independently per persona | Owner, designer, clerk, reader dashboards; press each card's Refresh | Only that card reloads; reader sees zero records; no card leaks another persona's data | ☐ pending |
| PB14-24 | Logout during a large upload | Start a 50 MB upload (security-test stack allows 20 MB per document policy — use the designer to raise the policy or a 15 MB file), sign out mid-transfer | Transfer stops, session is not reported Ready, re-sign-in shows the server session as resumable or expired, never as a document | ☐ pending |
| PB14-26 | Designer vs submitted record privacy | Designer persona opens Records | Designer sees no clerk records (zero rows), can still design | ☐ pending |
| PB14-27 | Long labels / long translated text | Rename a field label to 120+ characters, place at 1 of 4 width | Wraps, no overflow at 320 px | ☐ pending |
| PB14-22 (new UI) | Edit field dialog and Form permissions tab with NVDA / VoiceOver | Designer persona: Fields → Edit (label, read-only key and type announced with their explanations, validation errors announced, Save/Cancel, focus returns to the Edit button on close); Form permissions tab (source and revision badges, tables with row headers, Grant/Revoke dialogs, reason field required) | Every control has a name, read-only fields are announced as read-only, errors are announced, dialogs trap and restore focus, nothing relies on colour alone | ☐ pending |
| PB14-31 | Replace theme without backend change | Swap `packages/design-tokens` values | No .NET change required; contrast gate still enforced | ☐ pending |
| PB14-33 | Perceived performance on gallery/real screens | Timed walk-through on the development stack | Visible shell < 2 s, record read < 1 s p95 (PB-09 targets, informational in Stage B) | ☐ pending |
| PB14-34 | Evidence retention | Confirm Playwright report + axe JSON archived under `docs/evidence/stage-b/<date>/` | Present and linked from `docs/STAGE_B_STATUS.md` | ☐ pending |
| Sign-off | Product/design approval of the three flows (ADR-PB-014 gate) | Review this checklist and the evidence folder | Named approver, date, verdict | ☐ pending |

## 3. How to run the automated part locally

```bash
# Docker security-test stack (API 5180) must be up — see docs/STAGE_B_STATUS.md §1
cd src/frontend
npx playwright install chromium
API_TARGET=http://127.0.0.1:5180 npx playwright test            # workflow → accessibility → responsive
API_TARGET=http://127.0.0.1:5180 npx playwright test e2e/pb14-accessibility.spec.ts
npx playwright show-report ../../artifacts/stage-b/playwright-report
```
