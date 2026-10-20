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

Hosted (isolated, disposable runners) — filled in from the actual runs on the pushed commit; see the PR:

| Gate | Commit | Result |
|---|---|---|
| ci (specs, .NET, frontend, compose) | *pending* | *pending* |
| stage-b-integration (Stage B integration incl. `RepeatingGroupsTests`, all Playwright projects) | *pending* | *pending* |
| security-gate-a (SEC-P01..SEC-P12) | *pending* | *pending* |
| level3-script (the Level 3 script, unchanged, on its own clean stack) | *pending* | *pending* |

### What this does not show

No manual screen-reader or zoom review, no independent security review of the C3 changes, no Azure staging evidence, and no production certification of any kind.
