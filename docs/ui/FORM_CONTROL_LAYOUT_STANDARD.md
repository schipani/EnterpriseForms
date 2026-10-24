# Form control layout standard

Status: adopted for development in Phase C6 (decision C-52). It extends the ADR-PB-014 design validation standard and changes no color, token or theme rule.

Every labelled control in EnterpriseForms is built from the shared components in `src/frontend/packages/ui` (`TextInput`, `TextArea`, `Select`, `Checkbox`, and `FieldShell` for anything else). **Do not hand-write label, help or error markup in a feature.** A feature that needs a new kind of control wraps it in `FieldShell`.

## 1. The order

Top to bottom, always:

1. **Label**, with the required marker (`*`, decorative, plus "(required)" for screen readers) after the text.
2. **The control**, and any buttons that belong to it, in one row.
3. **Help text**, then a **status note** (for example "Encrypted for privacy").
4. **The validation message.**

Help text is never placed between the label and the control. There is no per-screen order and no provider that changes it. `FieldLayoutProvider` remains only as a no-op so older wrappers compile; remove it when touching a screen.

## 2. Association and announcement

* The label is a real `<label for>` for the control; a group of checkboxes uses `<fieldset>` with `<legend>`.
* Help, note and error are tied to the control with `aria-describedby`, in that order.
* An error also sets `aria-invalid` and is announced when it appears (`role="alert"`).
* The visible label text is the accessible name; icons are decorative.

## 3. Sizes and spacing

* Text inputs, selects and medium buttons share one height (`min-h-11`, 44 px). A small button (`size="sm"`) is for table rows and dense toolbars, **not** next to a field.
* Label to control: `gap-1.5`. Control to its supporting text: `gap-1`. Supporting text is `text-sm`; help and notes are muted, errors are bold red.
* Fields in one row share a grid with `items-start`, so labels and controls line up on one baseline each. Never use `items-end` to align a field with a button: a help text or error would push the button.
* No arbitrary margins, negative offsets or absolute positioning to compensate for misalignment.

## 4. Buttons that belong to a control

Use the `actions` slot of `FieldShell` / `TextInput` / `Select` (for example Search and Clear next to a search box, "Add option" next to "New option", "New list" next to "Option list").

* The control and its buttons are one row, top-aligned at control height.
* On a narrow screen the buttons wrap **below the control, inside the field**, before the help text.
* Help and the error always sit below the whole group, so a message appearing never moves the buttons.

Action buttons use the shared icon convention (`Button icon={ActionIcons.…}`, icon left, text right; see `packages/ui/src/actionIcons.ts`). Cancel, Yes/No and informational controls get no icon.

## 4A. Repeatable condition groups (Phase C7 Advanced Search)

Advanced Search (`apps/web/src/features/search`) is the reference pattern for repeatable groups of controls:

* Each group is a `fieldset` whose `legend` names it ("Condition 2"), so every group's controls share short labels (Field, Condition, Value) and stay unambiguous for assistive technology.
* Within a group, the two selectors share one `items-start` two-column grid (`sm:grid-cols-2`) and the value control sits beneath them at full width. A range uses the same two-column grid (From | To) directly beneath, so its inputs align with the selectors above.
* A group's Remove button (ghost, `size="sm"`, delete icon, accessible name "Remove condition N") sits below the group's controls, never beside a field.
* The group actions (Add condition, then the primary Search records) form one row below the last group, with the shared icons; a limit message sits beneath that row.
* Disabled states keep the full layout and say why (for example "Not available yet") instead of hiding controls.
* Controls are enabled only where they work (Phase C7 M2): Field and Condition come from the server's approved capabilities and can be used to compose conditions, while the action that cannot run yet (Search records) stays disabled with the reason beside it.

## 5. When to validate

* Do not show an error while someone is still typing a partial value. Validate on submit (Enter or the button), on leaving the field for rules that need a complete value, or on the server's answer.
* Editing a field clears its client-side error.

## 6. Responsive behavior

Layouts must work from 320 px (400 % zoom of a 1280 px window) and at 640 px (200 %) with no horizontal page scroll. Inputs keep a usable minimum width; wrapped buttons stay with their field.

## 7. How it is tested

`src/frontend/e2e/form-control-layout.spec.ts` measures the real browser (the My Records filters are checked as the C7 approved two-column rows Form | Status and Sort by | Rows per page; `advanced-field-search.spec.ts` checks the condition groups): label above control, help and error below, Search and Clear on the input's row and height, filter labels and controls aligned, an error that does not move adjacent buttons, 320 px and 640 px, and the same order in the designer's Add field dialog. Assertions use tolerances (2 px), not pixel baselines. `FormRenderer.test.tsx` checks the order and `aria-describedby` for record entry.

New feature screens must use the shared components; a screen that does not is a defect to fix in the component, not on the screen.
