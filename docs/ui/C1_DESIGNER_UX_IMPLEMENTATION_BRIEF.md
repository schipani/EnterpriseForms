# Phase C1-UX — Enterprise Forms visual editor implementation contract

**Status:** Design proposal / implementation brief, **not** a modification to the approved security ADRs.
**Working branch:** `phase-c/form-platform-expansion`.
**Reference image:** `docs/ui/C1_DESIGNER_UX_VISUAL_REFERENCE.png` (concept only; the normative behavior below overrides the picture).
**Scope:** Upgrade the existing metadata-driven Form Designer and shared UI patterns. Do **not** replace the form engine, rebuild existing features, or create hard-coded Accused views.

## Design language and reference interpretation

- Professional, calm, high-density enterprise UI: white/off-white surfaces, subdued grays, subtle borders, consistent spacing, readable small labels and ample touch targets.
- **Primary action buttons are BLACK**, per approved Enterprise Forms theme; secondary buttons neutral/outlined. The reference image shows blue Save buttons; **those are nonnormative and must become black**. Blue may highlight *selected cells, selection outlines, focus, and informational accents*, subject to organization-theme and contrast requirements.
- Organization logo/organization context in upper left; navigation stable. Strong hierarchy and keyboard-focus contrast.
- Match approved typography, contrast and organization branding; do not hard-code generated image's sample names, dates or colors. Do not use the image as literal UI screenshot or pixel-perfect target.
- Use existing Tailwind v4, Radix/shadcn-style components, React/TypeScript architecture; prioritize reusable primitives and tokens.

## A. Form Designer desktop frame

Three working zones, not a modal-driven workflow:

1. **Navigation/toolbox** (left, about 200–260 px when space permits): sections for Design, Fields, Rules, Preview, Versions, Settings; compact field palette, optional searchable component list.
2. **Design canvas** (center, flexible width): visual rows, columns, sections, fields, related-form lists and document collections. Render controls as realistic samples but visibly in design mode. Support four-column form geometry where approved, with per-field span.
3. **Contextual properties inspector** (right, about 280–360 px): appears upon selection; edit commonly used properties without opening a modal; rich or risky configuration offers a full tabbed dialog. Inspector is persistent on desktop and becomes a drawer/sheet on narrower screens.

Top toolbar: Add field, Add row/section, Add column when valid, Undo, Redo, Preview, Save draft, Publish (separate deliberate action), draft/published-version indicator, unsaved changes state. Do not autosave security-sensitive governance changes.

## B. Selection, cell actions and editing

- Single click on any existing field selects that field, visibly outlines it and populates inspector. Clicking an empty canvas cell selects the placement target and exposes a `+ Add field` affordance.
- **Double click field label** or use inspector to rename **display label only**. Stable field GUID and internal key must not silently change. Editing a published form creates/edits a new draft; published versions remain immutable.
- Single click must *not* accidentally type into record data or change persisted form values. In Preview mode, fields are interactive inputs with synthetic data and no database writes.
- Selected card has visible actions: Move, Duplicate (new ID, approved cloning rules), Configure, Remove. Destructive actions need clear confirmations where records/layout history could be affected.
- Drag-and-drop supports moving fields across valid cells and adjusting spans; show a precise drop target, disallow collisions/invalid placement. Also provide Move up/down/left/right actions and keyboard shortcuts/buttons—dragging is not the sole mechanism.
- Changing width/span must preserve row/column geometry and handle layouts at each breakpoint. No silently lost fields when switching responsive modes.
- Editing an existing File field or document collection must preserve its storage associations, IDs and documents. Distinguish a File field, attached-document collection, repeating group, and independently authorized related child collection.
- Show unsaved-change indicator, block accidental navigation/closing with changes, apply meaningful undo/redo to layout/field drafts, and show success/failure notifications.

## C. Field inspector and advanced tabbed editor

**Inspector — quick edits:** Label, help text, placeholder, required status (subject to validation rules), visible/hidden, column span, simple formatting. Context and permissions determine editability; unsafe settings use secure workflows, not generic JSON editing.

**Edit Field dialog/sheet — stable accessible tabs:**

- **General** — display label, description/help, placeholder, field type (read-only if populated or incompatible), default/display formatting, column span.
- **Validation** — required, length/number/range/date constraints, patterns, server-authoritative validation messages.
- **Rules** — conditional visibility, required-if, dependencies, declarative rule builder and cycle detection.
- **Security & Encryption** — Public/Internal/Confidential/Restricted, actual application encryption state, policy enforcement, authorized strengthening and governance-controlled downgrade operations. Display background migration state and verified completion; NEVER implement a free-form encryption toggle that bypasses the Authority.
- **Advanced** — immutable field UUID, internal name/key if policy permits, version metadata, diagnostics; avoid exposing secrets or dangerous arbitrary properties.

Tab changes preserve unsaved input, validation errors identify their tab, and Save/Cancel are always anchored bottom right (Cancel left, black Save on right). Esc/focus trap/focus restoration and keyboard tab navigation must meet WCAG 2.2 AA engineering targets. Use responsive drawer/sheet for small screens rather than offscreen dialog content. Buttons clearly show disabled/loading states.

## D. Standard table and action component contract

- Shared DataTable component for all applicable screens. **Rightmost column is `Actions` and is right aligned**. Optionally sticky at desktop widths where horizontal overflow would otherwise hide it.
- One visible primary row action (View or Edit) rendered as a neutral button, with other permitted operations in a consistent `…` overflow menu. No arbitrary clickable text posing as an action; hyperlinks only when semantically navigating to resources.
- Consistent row density, header styling, sorting indicators, pagination, empty/loading/error states, keyboard operation, menu accessible names, and destructive confirmations.
- At 320px allow deliberate horizontal scrolling while maintaining keyboard access and a usable action affordance; no clipping or 2D page overflow.
- Permissions must govern which actions are shown **and** be enforced by the backend. Hiding buttons is not security.

## E. Representative flows to demonstrate in the actual browser

1. As designer open Accused draft → click Last Name field → inspector updates → double click label → rename to Family Name → save draft → published historical version unchanged; stable field ID and existing values retained.
2. Click empty grid cell → Add field → place a DateTime field → change span → preview responsive layouts. Keyboard-only alternative accomplishes same operation.
3. Open tabbed Edit Field → edit General, Validation, Rules and Security without losing unsaved values while switching tabs → save → see accurate error indicators and persisted values.
4. Open document collection → inspect relationship to File field, allowed types and limits; editing label preserves documents; no implicit change to mandatory document-byte encryption.
5. Open Fields table, Form list, Record list, Documents and Organization screens → `Actions` is consistently last and the controls behave consistently.
6. Test field encryption migration statuses (plaintext→encrypted and governed reverse) are unchanged; no broken audit, authorization, signatory or integrity rules.

## F. Accessibility, viewport, correctness and scope guards

- Test 320, 768, 1440 px; 200%/400% zoom/reflow; keyboard-only; accessible drag alternatives; focus restore; reduced motion; high-contrast/forced colors; screen-reader semantics where automatable. Manual NVDA and VoiceOver acceptance remain pending until human verified.
- Preserve existing Entra/BFF contracts, organization isolation, Authority, SpiceDB, HMAC/Merkle, signed row integrity, application-layer encryption, scanning, docs, migration, and versioning; do not weaken prior test gates.
- First audit existing components and extract reusable primitives. Prefer minimal compatible refactoring with visual-regression coverage. Do not hard-code a UI based on the reference screenshot.
- Do not call C1-UX complete unless real browser Playwright flows verify selection, label edits, published/draft separation, tab input retention, security restrictions, action-column placement, keyboard access, and responsive overflow.
- Run existing `ci`, frontend checks, Stage B Docker integration and SEC-P suite; document actual outcomes, manual pending checks and the commit SHA in `docs/PHASE_C1_UX_STATUS.md`.
- Commit and push to `phase-c/form-platform-expansion`, open/update a PR targeting `main`, but don't merge or begin unrelated features without owner authorization.

## Acceptance criteria (must be demonstrable)

- [ ] Designer can click any existing field and inspect its properties without navigating away.
- [ ] Designer can click empty cell and add a field in the chosen location.
- [ ] Display label can be changed without changing stable IDs or losing values/attachments.
- [ ] Four-column responsive grid and span controls work without collisions or inaccessible fields.
- [ ] Drag operation has keyboard-accessible equivalent.
- [ ] Edit Field tabs have persistent unsaved state, clear validation and consistent Save/Cancel placement.
- [ ] Encryption and protected-permission settings use existing governance/Authority routes only.
- [ ] Every applicable table has its last column `Actions`, consistent primary button and menu.
- [ ] Required back-end, UI, browser and regression checks all pass on final committed code.
- [ ] Human acceptance items are reported separately and not fabricated.
