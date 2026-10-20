# C1 Designer Rebuild — Binding visual and interaction contract

**Status:** Owner-directed visual implementation requirement. This contract governs the designer UI presentation and new row/section interactions. Existing ADRs and security architecture remain binding. **Target image:** `TARGET_DESIGN.png` provided by the owner. **Current image:** `CURRENT_IMPLEMENTATION.png` represents behavior/presentation to replace. **Branch:** `phase-c/form-platform-expansion`.

## 0. Definition of complete

Build the new designer **in the actual React application**. The result should visually read like the target screenshot, not a cosmetically updated form-card layout. Do not create an isolated mock page or only adjust colors. The user can select a field and edit it on the right; create a named section with `+ Add Row`; add/move multiple fields inside that section; and persist, reopen, preview and publish that structure. Prove all behaviors with real API/storage plus Playwright.

## 1. First compare screenshots and inventory the existing code

Before edits, find existing app-shell/nav, designer page, grid layout model, fields, related lists, document collections, form draft/version metadata, API endpoints, autosave/draft commands, and current Playwright tests. Document a concise comparison of TARGET vs CURRENT. Describe which components will be **replaced rather than simply augmented**. If an existing permission, document collection, history or field-migration feature is exposed through old tabs, preserve reachability in the new layout.

**The visual target is authoritative for structure and visual density.** It is not authoritative for fictitious sample values or security behavior. Use existing org branding and secure endpoints. No need to duplicate top app-shell navigation inside the designer. A selected field shows one compact preview, not a whole editing form inside its tile.

## 2. Desktop 3-pane layout (MUST)

At 1440px desktop widths, below the site's existing global app header, use:

**Left pane (about 190–235px):** `Form Designer` heading, form name `Accused`, status chips (`Draft` and published version), vertical navigation **Design, Fields, Rules, Preview, Versions, Settings**. Preserve **Form permissions** and **Related forms** (either explicit submenu items or clear destinations from Settings/Fields; no functionality loss). Active nav appears selected. Left pane scrolls independently where necessary.

**Center canvas (remaining flex width, dominant):** toolbar in one aligned row where space permits: `+ Add Field`, **`+ Add Row`**, `+ Add Column`, Undo, Redo, Preview, black Save Draft, distinct deliberate Publish action. Under it, a visual form with **titled sections** such as Personal Information and Contact Information; each is a bordered, subtle card with a header, collapse affordance and its own four-column grid. Render field *previews* as compact field cards (label + empty/text/date/select/toggle sample). At most minimal icon actions on hover/selection. An empty placement slot has a visible `+ Add Field` affordance. No per-field Up/Down/Wider/Narrower buttons always visible inside every card; those belong in contextual controls/inspector/drag handles/keyboard menu.

**Right pane (roughly 275–350px):** persistent `Field Properties` inspector when a field is selected. Tabs **General / Validation / Rules / Security & Encryption / Advanced**. Common edits (label, help, placeholder, required, span) can be made here. When section selected, show `Section Properties` (title, description, 4-column layout, remove/collapse/reorder controls). When empty cell selected, show Add Field options. Complex governance changes must use the existing secure approval flow; not generic text toggles.

The target's density and organization matter. Specifically **remove** the current screenshot's large configuration controls embedded in every field card and separate unconnected Preview card dominating the inspector. Make Preview a mode accessible by toolbar/nav, and a small optional preview subsection if useful.

## 3. `+ Add Row` is a titled SECTION that CONTAINS fields (MUST)

The owner uses the term **Row** to mean a group/section into which additional fields are placed, **not** a line-break checkbox or an empty one-cell spacer.

### Required behavior

1. User selects the Design view and clicks `+ Add Row` in the canvas toolbar.
2. A new **titled section** appears immediately after the selected section, otherwise at the bottom. Give a default title `New section`, focus it for renaming, and assign a stable section ID.
3. The section has a header (title, optional description, collapse chevron and contextual `...` actions) and an initially empty **four-column grid**, with a visible `+ Add Field` slot.
4. Clicking `+ Add Field` **inside this section** creates or places a field **in this exact section/cell**, not at the bottom of the entire form.
5. Users can move existing fields among sections using drag-and-drop and non-drag keyboard/menu alternatives, reorder fields within a section and select spans **1–4 columns**; prevent overlaps and silent drops.
6. Users can rename, reorder, collapse/expand and optionally duplicate the section. Remove-section flow MUST distinguish `Remove empty section` from `Move contained fields before removal` or an explicitly confirmed removal of placements. **Never silently delete field definitions, values, child links or files** when removing a layout section.
7. `Save Draft`, reload, browser refresh, Preview, Publish and record rendering preserve section titles, order, child placements and responsive spans. Published versions remain immutable; edits happen in the current draft/new version.
8. Non-fields (authorized child-record collections and document collections) may also be placed within a section, with their stable identifiers and backend relationships preserved.
9. **Migrate existing ungrouped layout items non-destructively** into a visible `General` section (or preserve preexisting named sections if already supported). No records or metadata are lost; define versioned layout migration and rollback if schema evolves.
10. `+ Add Column` changes/adds available placement positions **inside the selected section**, within the approved four-column form geometry. It must not add an unrelated global table column or silently change every section. Clarify in UI if four slots already exist.

### Suggested layout metadata (adapt to actual existing model; do NOT replace its canonical schema blindly)

`Section { sectionId: UUID, title: string, description?: string, order: number, collapsedInDesigner?: boolean, children: Placement[] }`; `Placement { placementId: UUID, kind: Field|RelatedCollection|DocumentCollection|RepeatingGroup, targetId: UUID, order: number, span: 1|2|3|4, newLine?: boolean }`.

**The metadata represents layout only**; it does not create a new Accused table, modify stable field IDs or circumvent form-version integrity. If the existing layout engine has different entities, implement semantically equivalent behavior. Any signed/canonical representation changes require tests and the existing governance process; don't compromise the Trust Authority to implement UI.

## 4. Click-to-select, inline editing and state

- Click compact field card once: clearly indicate selection (blue selection outline is permitted) and populate the right inspector. Deselect on click outside.
- Double-click field label or use `General > Field label` to edit the **display label**, not UUID/internal key. When published, fork/modify draft only.
- Clicking a cell or its `+` control inserts a field in that exact cell and selected section. No unintended record-data entry in Design mode.
- No duplicated editing controls between canvas and inspector: inspector owns the controls. Use a modal/sheet only for advanced/risky operations when necessary; the same five stable tabs apply.
- Dragging uses accessible pointer sensors and keyboard alternatives. Reorder/resize feedback is clear; Undo/Redo works across field, placement and section draft changes.
- Show unsaved-changes indicator, save feedback, focus return, confirm on leaving with unsaved edits. Keep changes on tab switching and scroll.
- Responsive `320/768/1440px`: desktop three panes; tablet compact sidebar and inspector; phone navigate between canvas and inspector via accessible drawer/tabs. No horizontal body overflow or invisible buttons.

## 5. Theme and consistent controls

- **Default** org theme: black (`#111111` or comparable) filled primary buttons, white label; outlined neutral secondary buttons; black checked checkboxes/radio centers/toggle tracks; black active tab underline/text on ALL property tabs (both inspector and modal). Uniform field and tab styles across components.
- **Blue** reserved for visual field selection outlines, clear keyboard focus if contrast-approved, and numbered annotation markers in visual reference. Do **not** copy the reference's isolated blue active tab; enforce **black active tab styling consistently**.
- If an authorized organization owner/settings administrator configures button foreground/background and accent colors, **theme tokens** determine primary buttons, selected tabs, toggles, checkboxes and other semantic controls across the app; do not hard-code black on individual components. User-defined choices must pass WCAG contrast rules; reject or adjust incompatible combinations as approved by ADR-PB-014.
- The target picture does NOT supersede the organization's theme. It represents the **default organization** style.
- Consistent button height, radius, typography, borders, confirmation dialogs, toasts, disabled/focus/loading states. Real destructive actions distinct but accessible.
- Data-table `Actions` is always the **rightmost column**, right-aligned, consistent `Edit`/`View` button plus `...` overflow for secondary actions. No inconsistent inline `Configure`/`Remove` text links as actions.

## 6. Other critical requirements

- Existing **Field, Related Forms, Form Permissions, Versions, Document Collections, Encryption migrations, Declassification governance and authorization** continue working through the new shell. Avoid accidentally hiding core features while changing navigation.
- The data model remains generic and metadata-driven. Fields can be renamed by display label; field IDs remain immutable. Version semantics and stored values preserved. Field protections remain enforceable by backend Authority, not CSS.
- File/document collections stay application-encrypted, upload/download scan and revision flows unchanged. Do not fake successful operations with client-only state.
- No change to OAuth/Entra or Level 2 cryptography unless separately approved. Run existing Stage A and Stage B security regressions. Synthetic development data only.
- Don't call the feature complete merely because a screenshot looks good. The actual desktop browser at `1440×900` should be visually compared side-by-side to TARGET; record differences and correct them. The original target image is cropped, so compare designer-area structure/density rather than exact outer app-shell pixel positions.

## 7. Non-goals and rejection criteria

Reject an implementation that (a) merely renames `Layout` to `Design` while keeping the giant per-field property cards; (b) adds an `Add Row` button that inserts only a new-line flag; (c) creates an empty section that cannot contain fields; (d) shows an inspector but keeps all editing controls duplicated on every field; (e) replaces real data flows with mock data; (f) hard-codes colors so org overrides cannot work; (g) drops access to legacy permissions/collections; or (h) reports completion with failing browser or security tests.

Implementation must pass owner inspection of a real interactive UI, not just a synthetic mock.
