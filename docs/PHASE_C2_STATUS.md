# Phase C2 — Dropdowns, multi-select, conditional rules and field behavior

Branch `phase-c/form-platform-features` (from main `0cd1367`). **Status: implemented and verified locally; not merged; awaiting owner review.** Nothing here is an accessibility certification, an independent review or a production security certification; the open items below remain open.

Decisions: `docs/decisions/PHASE_C_DECISION_LOG.md` **C-05 … C-10**.

## What was built

### Priority 1 — Dropdown and multi-select fields (in the approved three-panel designer)

| # | Requirement | Where / how |
|---|---|---|
| 1 | Dropdown (single) and multi-select | Permanent per-field **selection mode** (`field_definitions.option_selection_mode`); chosen in *Add field*; layout control `select` / `multiselect` must match it (server-validated). Multi-select renders as an accessible checkbox group. |
| 2 | Option editor in the right-hand inspector | Field Properties → General → **Options** (`OptionsEditor.tsx`). |
| 3 | Add, edit, reorder, deactivate | Add option, label edit, *Move earlier/later*, *Active* checkbox, *Save options* / *Discard* (`PUT …/fields/{id}/options`). |
| 4 | Stable ids independent of labels | `FieldOptionId` + immutable `OptionKey`; keys cannot be renamed, options cannot be deleted; values reference the option id. |
| 5 | Default selections, required | *Default* checkbox per option (stored in the draft layout as `defaultOptionKeys`); *Required* in General. Defaults pre-fill new records only; the server validates what is submitted. |
| 6 | Reusable lists shared by forms | `option_lists` / `option_list_items` (organization-scoped, revision-guarded); *Fields → Option lists*; *Add field → Option source*. A list is **copied** into the field; changes arrive only through **Sync from list**; published versions are never affected. |
| 7 | Designer Preview and real record create/edit | Same `FormRenderer` everywhere; Preview starts from the defaults. |
| 8 | Typed storage + integrity | One `field_option_values` row per selection (one `field_encrypted_values` row per selection when the field is encrypted). Each row is its own signed Merkle leaf (`FieldSelection` / `FieldValue`). |
| 9 | Server-side validation | Unknown, inactive, duplicated and wrong-mode selections are refused atomically (`VALIDATION_FAILED` / `ORGANIZATION_OPTION_DISABLED`). |
| 10 | No silent corruption | Options are never deleted; published version carries an **option snapshot**; a stored selection that is no longer selectable stays visible (“no longer available”) and blocks a save that still contains it until replaced; list edits do not touch fields until synced. |
| 11 | Encryption, permissions, org settings | Encrypted dropdowns store ciphertext only; field-scoped shares cover every selection of a dropdown/multi-select (authority now authorizes selection leaves); organization classification floor and tenant isolation apply unchanged; option-list endpoints require the organization design permission. |
| 12 | Tests | See *Verification*. |

### Priority 2 — Conditional rules and field behavior

* **Conditional visibility / conditional required** — Field Properties → **Rules** tab: a rule builder (all/any groups, one nesting level in the UI, up to depth 4 in the schema) with operators filtered by the source field's type, only offering fields that cannot form a loop, plus **“Try it with sample values”** (synthetic, nothing saved).
* **Show in form** — presentation-only checkbox (hides the field from the data-entry form; not an authorization decision).
* **Read-only** — displays the value; the server refuses user changes (`FIELD_READ_ONLY`).
* **Indicators** — canvas tiles show text badges (*Conditional*, *Required if*, *Not in form*, *Read-only*); the left-nav **Rules** view lists every field with rules/behavior and opens the same inspector tab.
* **Validation** — circular dependencies, self-references, unknown/unplaced sources, wrong operators/operands and inconsistent combinations are rejected on **save draft and publish** with an explanation.
* **Enforcement** — the same rule engine runs in the server on every record write (create and edit) and in the renderer; shared test vectors keep them identical.
* **Hidden values** — retained by default (never silently overwritten or disclosed), cleared only by the explicit per-field *clear when hidden* opt-in (audited); a hidden field cannot be changed by a user write.
* **Published versions** — rule and option data live inside the immutable version; layouts that use none of the new properties serialize byte-for-byte as before.

### Security notes

* A partial (shared) projection never includes rule properties and never evaluates rules; a field-scoped contributor cannot change a multi-selection or a read-only field.
* The Authorization Authority change is small and read-only in effect: field shares may now name **selection leaves** (plaintext dropdown values), previously silently unshareable. Contributor edits of selection leaves remain forbidden by the authority.
* Migration `PhaseC2OptionListsAndSelectionMode`: two new tables (runtime role: select/insert/update, no delete), two new `field_definitions` columns (insert-only table, columns written at creation), occurrence unique indexes of the option and encrypted value tables now include the item id.

## Known limits (tracked, not hidden)

* The rule builder UI edits up to two nested group levels comfortably; deeper trees authored through the API are displayed but not offered for creation.
* Rule evaluation for a **partial projection is skipped** on purpose (the viewer lacks the source fields). Required-if is therefore enforced for full-scope writes only.
* Option **additions and relabels** reach the data-entry form after the next publish (snapshot); **deactivation** applies immediately.
* A shared option list is a template, not a live binding — explicit *Sync from list* by design (published-version compatibility).
* Field-scoped *contributors* cannot change a multi-selection (needs ownership). More generally, **no field-scoped contributor write passes the authority today** (every candidate re-signs every leaf, so the authority sees all items as updated); this is pre-existing and was not changed — Contributor shares are effectively read-only.
* `showInForm` is not a security control and must not be used as one.
* Still outstanding from earlier phases (unchanged): NVDA/VoiceOver and real 200/400 % zoom reviews (PB14-C1), PB-14 product sign-off record, security finding G19, and all production Level 2 requirements.

## Browser testing instructions (owner-reproducible)

Start the dev stack and the web app exactly as in `README.md` (`docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --build --wait` in `infrastructure/docker`, then `npm run dev -w apps/web` in `src/frontend`, <http://localhost:5173>). Sign in at `/login` with the seeded development personas.

**A. Dropdown and multi-select** (as `designer`, then `clerk`)

1. Forms → *New form* (tick the clerk as a form user) → **Fields → Add field**: *Priority*, storage type **Option**, options `Low / Medium / High`. Add a second one: *Topics*, **Option**, *Selection* = **Multi-select**, options `Red / Green / Blue`. (Try *Option source* = a shared list after step 6.)
2. **Design** tab → *Add Field* cells → place both → click the **Priority** tile. In the right-hand inspector (General tab) the **Options** section lists the options: add *Urgent*, press **Move Urgent earlier**, relabel *High*, **Save options**; tick **Default** on *Medium*; tick **Required**.
3. Click **Topics**: tick **Default** on *Red* and *Green*. **Preview** shows *Medium* pre-selected and Red/Green checked.
4. **Save draft → Publish**. Sign in as `clerk` → *New record* for the form: defaults are pre-filled; choose *High*, tick *Blue*, **Create record** → the record shows the labels; **Edit** → untick *Green* → saved (Revision 2).
5. As `designer`, open the form's Design view → Topics → untick **Active** on *Blue* → **Save options**. As `clerk`: the record still shows “Red, Blue (no longer available)”; **Edit → Save changes** is refused with a clear message until *Blue* is unticked.
6. *Fields → Option lists → New list…* creates a reusable list; create a field from it with *Add field → Option source*. Edit the list (relabel, add, deactivate) — the field is unchanged until you open it in the inspector and press **Sync from list**.

**B. Conditional rules, Show in form, Read-only** (as `designer`, then `clerk`)

1. New form with fields: *Applicant kind* (Option `Person / Company`), *Company name* (text), *Applicant age* (Integer), *Guardian name* (text), *Intake status* (Option `Draft / Final`), *Reference note* (text). Place them all.
2. **Company name** → inspector **Rules** tab → *Show this field only when conditions are met* → field *Applicant kind*, operator *is*, value *Company*. Under **Try it with sample values** pick *Person* → “hidden”; *Company* → “shown”. (General tab: tick **Required** — it applies only while the field is shown.)
3. **Guardian name** → Rules → *Make this field required when conditions are met* → *Applicant age* *is less than* *18*; try ages 15 and 30 in the sample panel.
4. **Intake status** → General: **Default** = *Draft*; Rules: **Read-only**. **Reference note** → Rules: untick **Show in form**.
5. Canvas tiles now carry text badges (*Conditional*, *Required if*, *Read-only*, *Not in form*); the left-nav **Rules** view lists them and opens the same inspector tab. In *Applicant kind* → Rules, the field list never offers *Company name* (it would create a loop).
6. **Preview**: Company name appears only after choosing *Company*; the read-only status shows *Draft*; the reference note is absent. **Save draft → Publish**.
7. As `clerk`: *New record* — Company name is absent until *Company* is chosen, then required; an age under 18 demands the guardian; status is a read-only *Draft*. Create it, then **Edit** and switch the kind to *Person*: Company name disappears from the form and from the record page, but the value is **retained** (reopen Edit and choose *Company* again — it is back).

## Verification record

Run locally on 2026-10-20 against the working tree that became the Phase C2 commit (Windows, Docker Desktop, real PostgreSQL ×2, SpiceDB, Vault Transit, Trust Authority, Azurite, ClamAV, hostile `attacker-sql`). **Not** hosted-CI evidence: hosted CI runs when the branch is pushed and its result is recorded on the branch, not here.

| Check | Result |
|---|---|
| Specifications + integrity vectors (`validate-specifications.py --strict`) | **PASS** (manifest regenerated: 137 files) |
| `.NET` solution tests (unit, architecture incl. database naming/ownership/boundaries, contract incl. OpenAPI snapshot, authority, integrity) | **186 / 186** (166 before; +20 `PhaseC2RulesAndOptionsTests`, incl. the 51 shared rule vectors and 10 invalid-condition vectors) |
| Frontend: ESLint, typecheck, vitest, production build, regenerated API client = committed | **PASS**, vitest **138 / 138** (47 before; incl. the same 51 vectors run through the TypeScript evaluator, renderer, rule-graph and record-payload tests) |
| Stage A gate SEC-P01 … SEC-P12 + harness guard (fresh stack, adversarial) | **13 / 13** |
| Stage B integration (fresh stack, real services) | **48 / 48** (42 before; +3 `DropdownTests`, +3 `RulesTests`) |
| Playwright, all projects (real stack through the Vite proxy; includes axe WCAG 2.2 AA scans, PB-14 reflow at 320/768/1440 px and zoom equivalents) | **28 / 28** (23 before; +5 `dropdowns-rules.spec.ts`) |
| Mutation check of the server rules | With the read-only and hidden-by-rule refusals removed from `CandidateBuilder`, 3 of the 20 unit tests fail; restored afterwards |

Notes:

* `scripts/dev/test.ps1 -Level 3` stopped at *Stage B: start full stack* with `FAILED … (exit 1)` although every container was healthy: the script's `compose up --wait` treats the one-shot `dev-seed` container exiting 0 as a failure (already documented for Level 2). The remaining Level 3 steps (full Stage B integration, full Playwright, teardown) were then run by hand with the script's own commands against that healthy stack. The first Level 3 attempt stopped earlier at `npm ci` because stray Vite dev servers held the Tailwind native module open; they were stopped.
* The local dev stack (`docker compose … compose.dev.yaml`) was stopped and its containers/networks removed (volumes untouched) because the security-test stack pins the same subnets. Rebuild it with the README command to browse the new features (the API image must be rebuilt: this phase adds a migration).

### What this does not show

No NVDA / VoiceOver / real-zoom human review of the new Rules tab, Options editor, multi-select group or Option-lists dialog (axe and the automated reflow checks only); no independent security review of the one-line Authority change; no production Level 2 evidence. Phase C2 is for the owner to review before anything is merged.
