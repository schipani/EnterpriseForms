# Application-wide UI consistency standard

- **Status:** implemented on branch `ui/consistency-standardization` (from `main` `b1e1b30`), **awaiting the owner's review; not merged.** A frontend-only presentation change. No backend API, database schema, Authorization Authority or security logic was changed. This is separate from Phase C8.
- **Owner direction (2026-10-30):** the **My Records** page and its **Advanced Search** panel are the approved visual reference: their styling, spacing, borders, typography, control heights and alignment. The rest of the application follows them, without a redesign and without a new visual framework.
- **Companion:** `docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md` (field order, association, spacing; §3 now lists the shared tokens).

## 1. Why the screens looked different (inventory)

The application already had one set of shared components (`packages/ui`). The approved look of My Records did **not** come from them. It came from a CSS block scoped to that page (`.records-search` in `apps/web/src/app/styles.css`, Phase C7) that overrode the shared components inside the search area only. Every other screen, including the My Records page header, rendered the base components.

| Property | My Records / Advanced Search (reference) | Every other screen (before) |
|---|---|---|
| Input and select height | 40 px | 44 px |
| Control corner radius | 8 px | 4 px |
| Control text | 14 px | 16 px |
| Field label | 13 px medium | 14 px medium |
| Help text, notes, errors | 12 px | 14 px |
| Medium button | 40 px, 14 px, 8 px radius | 44 px, 16 px, 4 px radius |
| Card | 12 px radius, 20 px padding, 16 px gap, 16 px title | 8 px radius, 16 px padding, 12 px gap, 18 px title |
| Table header | 13 px semibold | 14 px semibold |
| Checkbox label | 13 px medium | 16 px regular |
| Font stack | Inter, then the system UI font | system UI font |

Further inconsistencies found in the review:

| Area | Finding |
|---|---|
| Radio buttons | No shared component. The designer's *Remove section* dialog hand-built native radios with their own label and legend styling. |
| Fieldset legends | Five different treatments across New form, field security, rules, search approval, sharing and the renderer (`text-sm font-medium`, `font-medium`, `text-sm font-semibold`, with or without `px-1`). |
| Validation messages | Seven hand-written error paragraphs with their own sizes and weights, and the error colour typed as a raw hex value in 20+ places. |
| Required marker | Re-implemented by hand in the renderer's multi-select. |
| Icon-only buttons | Three separate implementations (dialog close 36 px, inspector close 36 px, designer tile tools 32 px), each with its own classes. |
| Dropdown menus | Menu and item styles copied by hand into the account menu and the designer's Add field menu. |
| Links that look like buttons | The document *Download* link copied the secondary-button classes by hand (36 px, 4 px radius). |
| Designer | Pane toggle (Canvas / Properties) was a hand-built segmented control; panels and tiles used 8 px and 6 px radii; the inline title editor had its own border and radius. |
| Focus, hover, disabled | Already consistent: one global `:focus-visible` outline (3 px, focus token) and shared disabled treatment. Kept unchanged. Disabled controls now also show a not-allowed cursor. |
| Date and time | Already built on the shared `TextInput` (native date and datetime-local); they inherit the standard. |

## 2. The standard (what changed)

**Tokens** (`packages/design-tokens/src/tokens.css`): `--ui-control-height` (40 px), `--ui-control-height-compact` (36 px), `--ui-control-radius` (8 px), `--ui-panel-radius` (12 px), `--ui-text-control` (14 px), `--ui-text-label` (13 px), `--ui-text-help` (12 px), `--ui-danger-text` (#991b1b) and `--ui-font-family` (the reference font stack).
- All sizes are rem-based, so the per-user text-size preference and browser zoom still scale them.
- They are fixed product values; organization branding can still change only the validated colour tokens.
- `apps/web/src/app/styles.css` exposes them as Tailwind utilities: `min-h-control`, `min-h-control-compact`, `h-control`, `rounded-control`, `rounded-panel`, `text-control`, `text-label`, `text-help` and `text-danger`.

**Shared components** (`packages/ui`):

| Component | Change |
|---|---|
| `TextInput`, `TextArea`, `Select` | Reference height, radius, type size and error border, from tokens. |
| `FieldShell` | 13 px label, 12 px help, note and error. New exports: `fieldLabelClass`, `legendClass`, `helpTextClass`, `errorTextClass` and `RequiredMarker`. |
| `Checkbox` | 40 px row, reference label style, shared help and error styles. |
| `RadioGroup` | **New.** Native radios in a fieldset with a legend; same styling as Checkbox; optional per-choice details. |
| `Button` | 40 px medium and 36 px small, 8 px radius, 14 px label; `danger` uses the token. New `buttonClass()` for links that must look like buttons. |
| `IconButton` | **New.** Square icon-only button, 40 px (or 32 px small), accessible name and tooltip from `label`. |
| `Card` | 12 px radius, 20 px padding, 16 px gap, 16 px semibold title. |
| `Dialog`, `SideNavTabs` | Panel radius. The dialog close button is `IconButton`. |
| `OverflowMenu` | New exports `menuContentClass` and `menuItemClass`. Items use the control radius and 14 px text. |
| `DataTable` | Control radius; 13 px semibold headers. |
| `StatusMessage`, `EmptyState` | Control radius. |

**Screens and packages updated to use them:**
- **My Records and Advanced Search:** the scoped override was removed. Only the container-query layout of the two panels remains, and the page now gets the same look from the shared components. Its explanatory 12 px text uses `helpTextClass`, and the class names stay as test hooks.
- **Form Renderer** (record entry, edit, versions, child records): shared legend, help, error and required marker; panel radius and padding on sections.
- **Form Designer:**
  * toolbar, panels and tiles now use the shared radii;
  * the pane toggle is shared `Button`s;
  * the tile tools and the section collapse control are `IconButton`;
  * the title editor uses the compact control height and radius;
  * the Add field menu uses the shared menu styles;
  * the inspector close button is `IconButton`, and its errors use the shared style;
  * the Remove section dialog uses `RadioGroup`;
  * rules and field security legends and errors use the shared styles.
- **New form, Search approvals, Record sharing:** shared legend and help styles.
- **Documents:** the Download link uses `buttonClass("secondary", "sm")`.
- **Upload Center:** the error colour comes from the token.
- **App shell:** the account menu uses the shared menu styles; the session-expired panel uses the panel radius.
- **Branding, Preferences, Dashboard, Forms, Activity and the record pages** already used the shared components and inherit the standard without code changes.
- **Organization administration, Teams, People and the Security Administrators page** are on the Phase C8 branch, not on `main`. They are built from the same shared components and will inherit the standard when C8 is rebased; nothing on the C8 branch was touched.

**Deliberately unchanged:**
- primary navigation tabs, side-navigation tabs and record tabs (navigation, not form controls; 44 px);
- the designer canvas's miniature control previews (a schematic drawing, intentionally compact);
- badge shape;
- the colour scheme and organization theming.

## 3. Accessibility notes

- **Colour and contrast:** no colour value changed. The input boundary stays the validated `--ui-input-border` (4.76:1). The reference's lighter border was already rejected in Phase C7 under WCAG 1.4.11.
- **Target size:** 40 px controls and 36 px small buttons exceed WCAG 2.2 AA target size (24 px). The 44 px previously used is the AAA level, not an AA requirement.
- **Focus:** the focus indicator, keyboard order, labels, `aria-describedby` chains, `role="alert"` errors and the required marker's screen-reader text are unchanged or now shared. `RadioGroup` uses native radios (arrow keys, forced colours).
- **Text size:** 12 px help text is the approved reference size. Because it is rem-based, it scales with the per-user text-size preference and with browser zoom.

## 4. Component Gallery (PG-069): decision

**What it is.** `/app/gallery` is the internal component gallery required by ADR-PB-014 and `PB14_ENTERPRISE_DESIGN_VALIDATION_STANDARD.md` ("PG-069 (internal/developer only) … must never appear as a public route in production"). It renders every shared component and state with synthetic data, including the four-column form renderer and a synthetic Advanced Search preview. It loads and sends nothing.

**Who uses it.** Developers, testers and the automated suites; not end users. It is referenced by:
- `e2e/pb14-accessibility.spec.ts` (axe WCAG 2.2 AA at every width);
- `e2e/responsive.spec.ts` (320 px reflow and keyboard reachability);
- `e2e/advanced-field-search.spec.ts` (the enabled-layout preview, which needs synthetic capabilities no real form provides).

No application component depends on it. It imports shared components; nothing imports it.

**Problem.** It was listed in the primary navigation for every signed-in user ("Component gallery", `show: true`), and its route was compiled into every build, including production. That contradicts ADR-PB-014.

**Decision (UIC-1).** Keep it as a development-only page:
1. **Removed from navigation** for everyone.
2. **The route exists only in development builds** (`import.meta.env.DEV`: the Vite dev server, which developers and the Playwright suites use), or when a non-production build explicitly sets `VITE_ENABLE_COMPONENT_GALLERY=true`. Both conditions are replaced at build time.
3. **It is excluded from production bundles.** The page moved to its own entry, `features/support/developmentGallery.ts`, out of the support feature's main index. A default `vite build` therefore contains neither the route nor its code; `/app/gallery` there shows the not-found page.
4. Nothing was deleted, so no shared component and no test was removed.

Verified: a default production build contains no gallery code (0 bundle files mention it). A build with `VITE_ENABLE_COMPONENT_GALLERY=true` contains it (1 file). The three specs above still reach it on the dev server.

**Justification.** It is mandated by ADR-PB-014 and is the only synthetic, data-free surface that the automated accessibility and responsive suites can scan for every component state. Removing it would remove that coverage. Leaving it public contradicted the ADR.

## 5. Verification (2026-10-30)

All browser tests ran on the isolated security-test stack. Playwright started its own Vite server on port 5199, and the global setup verified the stack identity. The persistent development stack was not used.

| Check | Result |
|---|---|
| ESLint (`npm run lint`) | Pass, 0 problems |
| TypeScript (`npm run typecheck`, all workspaces) | Pass |
| Vitest | **225 / 225** (27 files), including 6 new shared-control tests (`packages/ui/src/SharedControls.test.tsx`: TextInput geometry and `aria-describedby`, Checkbox label, RadioGroup naming, selection, details, error, disabled and required, IconButton name and tooltip, `buttonClass`) |
| Production build (`vite build`) | Pass. All new utilities generated; the gallery is absent by default and present with `VITE_ENABLE_COMPONENT_GALLERY=true` |
| Playwright, desktop 1440 px | **51 / 51**: form-control-layout, advanced-field-search, designer-ux, designer-layout-refinement, designer-interaction-corrections, action-icons, records-workspace, branding, reusable-dropdown-list, dropdowns-rules, form-management, record-lookup, pb14-accessibility (axe WCAG 2.2 AA on login, dashboard, records, record, access, uploads, gallery and designer at every PB-14 width) |
| Playwright, phone 320 px | **2 / 2** (`responsive.spec.ts`: gallery and My Records at 320 px; no horizontal scroll; keyboard reachability) |
| Re-run after the last two help-text edits | form-management and field-search-approval, **8 / 8** |
| Visual comparison | `docs/evidence/ui-consistency/` (before and after, 1440 px and 320 px) |

**Not performed:**
- manual NVDA and VoiceOver review;
- manual browser-zoom review at 200 % and 400 % by a person (automated 320 px reflow covers the 400 % layout width);
- the remaining Playwright specs (encryption, record history, restore, lifecycle, sessions and others), which exercise the same shared components but were not re-run here;
- hosted CI workflows (not dispatched).
