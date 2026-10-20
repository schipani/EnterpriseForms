# Phase C3 — Repeating groups and dependent dropdowns

Branch `phase-c/repeating-groups` (from main `7b3c173`). **Status: implemented and verified as recorded below; awaiting owner review. Not merged.** Nothing here is an accessibility certification, an independent review or a production security certification.

Architecture assessment (written before implementation): `docs/architecture/PHASE_C3_REPEATING_GROUPS_AND_DEPENDENT_DROPDOWNS.md`. Decisions: `docs/decisions/PHASE_C_DECISION_LOG.md` **C-15 … C-21**.

**Trust boundary: unchanged.** No change to the Authorization Authority, its command protocol, RowHmac v1 or Merkle v1 (C-15). The one new signed artifact is the business signed schema `form_record_groups` v1, registered like the Stage B value schemas.

## What was built

### Priority 1 — Repeating groups

| # | Requirement | Where / how |
|---|---|---|
| 1 | "Allow multiple entries" on a section | Section Properties → **Allow multiple entries** (only for an empty section). The server creates a permanent group (`POST /api/v1/form-definitions/{id}/groups`); the section's layout gets `repeat`. Canvas badge "Multiple entries · min–max". |
| 2 | Minimum / maximum entries | Section Properties: Minimum entries, Maximum entries (0–50, max ≥ 1, min ≤ max); validated in the inspector, by the layout schema and by the server. |
| 3 | Allow adding / removing / reordering | Section Properties checkboxes; when entries cannot be added the minimum must be ≥ 1 (a new record starts with exactly that many). Enforced by the server on every write (C-18). |
| 4 | Fields, validation, conditional visibility, required-if per entry | Fields are created from the section's "+ Add Field" cell ("New field for each entry…") and belong to the group for life (C-16). Validation and Rules tabs work as for any field; rules inside an entry may read the same entry or record-level fields; a record-level rule cannot read entry fields (the rule builder only offers fields in scope). Evaluated per entry in the form and on the server. |
| 5 | Create / edit / remove / reorder entries | Record form: "Add *Section*", per entry "Move up", "Move down", "Remove" (only what the section allows), focus moves to a new entry, every change announced; errors name the entry ("… (entry 2)"). |
| 6 | Stable identity | Each entry has a permanent `formRecordGroupId` (signed `RepeatedGroup` manifest item); edits and reordering keep it, a removed entry leaves the next accepted revision, a new entry gets a new id. Verified through the API in the integration and browser tests. |
| 7 | Dropdowns, multi-select, defaults, read-only, encryption, published versions | All work per entry: dropdown defaults pre-fill a new entry, a read-only dropdown receives its default from the server, encrypted fields store one ciphertext per entry, published versions are immutable and older records without entries keep working (empty section, entries can be added). |
| 8 | Distinct from independently authorized child entities | Entries are content of one record (one owner, one authorization scope, one content revision). Related-form lists (e.g. AccusedAddress) are unchanged independent child records; neither document collections nor related lists can be placed in a repeating section. |

### Priority 2 — Dependent dropdowns

| # | Requirement | Where / how |
|---|---|---|
| 1 | One dropdown filters another (Country → Province → City) | Field Properties → Rules → **Dependent choices**: "Filter the choices by another dropdown", "Depends on", then per choice the parent values it belongs to ("Ontario when Canada"). Chains are supported. |
| 2 | Stable option identifiers | The mapping uses option keys only (`optionFilter.parentOptionKeysByOption`); relabelling never breaks it. Choices left unmapped are always available. |
| 3 | Single- and multi-select | Both as parent and child (a multi-select child keeps the choices that still fit). |
| 4 | Configured in the inspector | Rules tab of the dropdown; the left-nav Rules overview lists "Choices depend on …". |
| 5 | Invalid and circular dependencies | Rejected at draft save and publish (unknown or unplaced parent, non-dropdown, out-of-scope parent, unknown keys, self-dependency, **cycles**); the designer shows a "Circular dropdown dependency" message and only offers parents that cannot create a loop. |
| 6 | Behavior when a parent changes | The form clears dependent selections that no longer fit, down the chain, and announces it. The server **never clears**: it refuses an invalid combination with `DEPENDENT_OPTION_INVALID` (C-19). |
| 7 | Server-side validity | Every full-scope write (create, edit, child create), top level and per entry. |

### Security notes

* Entry rows are signed and part of the complete accepted manifest: reordering, moving a value between entries or deleting an entry in PostgreSQL makes the record fail closed (`INTEGRITY_VERIFICATION_FAILED`, tested with the attacker SQL role).
* Fields of repeating sections cannot be shared on their own (`FIELD_IN_REPEATING_GROUP`); shared views never contain repeating sections or entry values (C-17).
* Forged, foreign and duplicated entry ids are refused; field-scoped writers cannot change entries.
* Values inside entries keep their field's protection (application encryption per value, no plaintext copies).

## Known limits (tracked, not hidden)

* **Deferred for review (would change the Authority):** field shares that cover entries; nested repeating groups; field-scoped contributor writes (refused for every write today — the C-05 finding).
* No group-level conditional visibility (rules are per field), no search UI over entry values, no per-entry documents.
* NVDA / VoiceOver / real browser zoom reviews of the new UI were **not** performed; automated axe scans pass (designer Preview, record form and record view with entries).
* Hosted SEC-P gate, Stage B integration/browser gates and the Level 3 script run: see the verification record — nothing is claimed beyond what ran.
* Open production items from earlier phases are unchanged: G19, the C-12 Authority review, PB14-C1 manual accessibility, every production Level 2 requirement.

## Browser testing instructions (owner-reproducible)

The development stack (`enterpriseforms`, API on 5080) was updated **in place** — `docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --no-deps business-migrate api worker web` — which applied the additive migration `PhaseC3RepeatingGroups`. Vault, the databases and their volumes were not restarted, so your development data and signing key are intact. Start the frontend with `npm run dev -w apps/web` in `src/frontend` (http://localhost:5173), or use the built web container on http://127.0.0.1:8081.

1. Sign in as **designer**. Forms → **New form** (tick the clerk as a form user) → open the designer.
2. **Fields** tab → *Add field* → "Country", **Dropdown (Single Select)**, options `Canada`, `USA`. Again: "Province", options `Ontario`, `Quebec`, `Texas`. Place both in the first section.
3. **+ Add Row**, name the new section "People", press Enter. Click the section title → Section Properties → tick **Allow multiple entries**. The canvas shows "Multiple entries · 0–10". Set **Maximum entries** to 3.
4. In the People section click **+ Add Field** → **New field for each entry…** → "Person name" (Text). Again for "Role" as a **Dropdown (Single Select)** with `Witness`, `Suspect` (note: no File type is offered here). Select "Person name" → tick **Required**.
5. Optional rule: select a field in People → Rules → "Show this field only when …" — the field list offers this section's fields and record-level fields only.
6. Select **Province** → Rules → **Dependent choices** → tick "Filter the choices by another dropdown" (Depends on: Country) → tick "Ontario when Canada", "Quebec when Canada", "Texas when USA".
7. **Preview**: "Add People" creates an entry; choosing Canada leaves only Ontario/Quebec in Province. **Save draft**, **Publish**.
8. Sign out, sign in as **clerk**, open the form → **New record**.
   * Choose Canada → Ontario, then change Country to USA: Province is cleared and the change is announced; only Texas is offered.
   * **Add People** twice; fill entry 1, leave entry 2's name empty and press **Create record** — the error names "entry 2". Fill it and create.
9. On the record: entries are listed in order. **Edit** → **Move up** on entry 2 → Save: the order is swapped, revision 2. Edit again → **Remove** one entry, **Add People**, Save.
10. **Access** → Share fields: Person name / Role are not offered ("Fields in sections that allow multiple entries cannot be shared on their own").
11. Try to break it (optional, API): sending an entry id that does not belong to the record returns `OCCURRENCE_UNKNOWN`; sending Province "Ontario" with Country "USA" returns `DEPENDENT_OPTION_INVALID`.

## Verification record

Local (Windows, this branch, before commit):

| Check | Result |
|---|---|
| .NET solution tests (Release; architecture, contract incl. 34 new C3 cases, authority unit tests) | **220 / 220** passed, 0 warnings |
| Frontend lint + typecheck | clean |
| Vitest (all 16 files) | **158 / 158** passed (incl. new `groups.test.ts`, `RepeatingSection.test.tsx`, `phaseC3.test.ts`) |
| Playwright `repeating-groups.spec.ts` against the **development** stack (5080) | **2 / 2** passed (axe: no violations after the label-in-name fix below) |
| OpenAPI / generated client | regenerated; additive changes only |

Finding fixed during verification: axe `label-content-name-mismatch` (WCAG 2.5.3) — the entry "Move up/down" buttons' accessible names did not contain their visible text; they are now "Move up, People 1".

Hosted (isolated, disposable runners), on commit `be5566b` (fixes to the three `RepeatingGroupsTests` cases that the first hosted run on `666c314` caught — see decision log and PR history for what each fix was):

| Gate | Commit | Result |
|---|---|---|
| ci (specs, .NET, frontend, compose) | `be5566b` | **success** |
| stage-b-integration (Stage B integration incl. `RepeatingGroupsTests`, all Playwright projects) | `be5566b` | **success** |
| security-gate-a (SEC-P01..SEC-P12) | `be5566b` | **success** |
| level3-script (the Level 3 script, unchanged, on its own clean stack) | `be5566b` | **success** |

### What this does not show

No manual screen-reader or zoom review, no independent security review of the C3 changes, no Azure staging evidence, and no production certification of any kind.

## Addendum: Form Designer UX refinement (owner-requested, after the C3 review above)

The owner reviewed the running designer and asked for fewer visible scrollbars and a smaller, less crowded Field Properties panel. This addendum covers that request; it changes the **frontend only** (no schema, Authority, encryption, integrity, published-version, rule-engine or repeating-group change). See `docs/decisions/PHASE_C_DECISION_LOG.md` **C-22**.

### What changed

- **Pinned header.** The form name, draft status and the toolbar (Add Field/Row/Column, Undo/Redo, Preview, Save draft, Publish) now sit in one bar pinned to the top of the page (`position: sticky`) instead of scrolling away inside the Design tab. There is exactly one scroll surface for the whole designer (the page itself); the two boxes that used to force their own inner scrollbar (the left-hand section nav and the right-hand inspector) are now `sticky` without a height cap, so they stay in view without a scrollbar of their own. The pinned bar's own height is measured (not guessed) so the nav and inspector sit exactly below it at any width. As a defensive bound only — not the normal case — the pinned bar itself caps at 70% of the viewport height with its own scroll, so at very short viewports (400% zoom) its buttons stay reachable instead of being able to cover the whole screen; this was found and fixed during verification (below), not assumed safe.
- **Short Field Properties panel.** Selecting a field or document collection now shows only **Field label**, **Field type** (still permanent/read-only), **Help text** and **Placeholder**, plus a **Configure Field** button (gear icon). A section's own properties (title, description, "Allow multiple entries", move/duplicate/remove) are unchanged — they were never part of the crowding complaint and stay inline.
- **Configure Field modal.** Clicking it opens the previous General / Validation / Rules / Security & Encryption / Advanced workspace unchanged in content, now in a large dialog (~90vw up to 1200px, ~85vh, full-screen below `sm`) with a vertical tab list (more room, same `role=tab`/`role=tablist` semantics) and a pinned header/footer around one scrolling middle region. **Apply Changes** commits the field's edits (label, type-appropriate validation, rules, placement) to the draft as one undo step; **Cancel**, Escape, the overlay and the × button all discard them the same way — this is new: the old inline panel applied every keystroke immediately, which is what made Cancel meaningless before. Security & Encryption and "Edit catalog name and description…" are unaffected: those already acted immediately through their own server calls (Authority-backed strengthening; a separate catalog save), and still do — Cancel never rolls them back, because they were never part of the draft layout.
- `Dialog` (`packages/ui`) gained an opt-in `size="large"` variant for this; every existing caller is unchanged (`size` defaults to the old behaviour). `SideNavTabs` dropped its `header` prop (folded into the page's own pinned bar, its only consumer) and its forced inner scrollbar.

### Required regressions re-run after the change

Every existing spec that used to reach General/Validation/Rules/Security/Advanced inline (`designer-ux.spec.ts`, `dropdowns-rules.spec.ts`, `accused-workflow.spec.ts`, `form-management.spec.ts`, `repeating-groups.spec.ts`, `reusable-dropdown-list.spec.ts`, `pb14-accessibility.spec.ts`) was updated to open Configure Field first, via a new `openFieldConfig` helper in `e2e/support.ts`, and re-run end to end against the development stack:

| Spec | Result |
|---|---|
| `designer-layout-refinement.spec.ts` (new; pinned header, 4-field quick panel + empty state, modal open/tabs/Apply/Cancel/Escape sync, keyboard-only nav, axe) | **5 / 5** passed |
| `designer-ux.spec.ts` | **5 / 5** passed |
| `dropdowns-rules.spec.ts` | **5 / 5** passed |
| `form-management.spec.ts` | **3 / 3** passed |
| `reusable-dropdown-list.spec.ts` | **2 / 2** passed |
| `accused-workflow.spec.ts` | **2 / 2** passed |
| `repeating-groups.spec.ts` | **2 / 2** passed |
| `encryption.spec.ts` + `encryption-downgrade.spec.ts` (unchanged files; confirm `Dialog`'s default size is pixel-identical) | **8 / 8** passed |
| `pb14-accessibility.spec.ts` (axe at 320/768/1440/720/320×256; keyboard-only; forced-colors/reduced-motion) | **3 / 3** passed, after one real fix (below) |
| Vitest (all packages) / lint / typecheck / production build | clean |

Two real problems were found and fixed by this verification, not assumed away:
1. **Target-size false attribution.** The new quick-panel axe scan initially reported a `target-size` (WCAG 2.5.8) finding on the canvas tile's own rename button — a **pre-existing** issue from Phase C1 (that button is untouched by this change) that a page-wide scan picks up incidentally whenever a tile is selected. The scan is now scoped to the inspector panel under test (`AxeBuilder.include`), and the pre-existing finding is left as a separate, tracked gap rather than silently working around it by disabling the rule.
2. **Pinned bar could cover the whole short viewport.** At the 200%-zoom-equivalent width (720×600), the header + full toolbar wrapped across enough rows to be taller than the viewport, and — because `position: sticky` keeps occupying that space at every scroll position — the canvas became permanently unclickable underneath it (242 failed click retries in `pb14-accessibility.spec.ts`, not a flaky timeout). This is exactly the "content becomes inaccessible" failure mode the task asked to avoid, and it was only caught by actually running the test at that viewport. Fixed by bounding the pinned bar to `max-h-[70vh]` with its own scroll as a fallback (described above); re-run confirmed clean.

### What this addendum does not show

No manual NVDA/VoiceOver review was performed for the new modal (automated axe only, as instructed). The pre-existing canvas-tile target-size finding above is not fixed here (out of scope for this refinement) and should get its own follow-up.
