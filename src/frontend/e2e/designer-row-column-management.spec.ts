import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { addField, formIdFromUrl, openFieldConfig, publish, signIn, signOut } from "./support";

/**
 * FD-01 row and column management (owner visual review corrections): "+ Add Row" adds exactly one four-column row and no
 * trailing cell; a field dragged into it fills that row; Clear row / Delete row / Clear column / Delete column are
 * confirmed before any field is removed, are undoable, never move unrelated fields, and the result survives Save draft,
 * reload, Preview and publishing. Positions are read from the canvas tiles' data-grid-* attributes and the renderer's
 * explicit CSS grid placement, not screenshots.
 */
const run = Date.now().toString(36);
const formName = `Row management ${run}`;

const canvas = (page: Page) => page.getByRole("region", { name: "Form canvas" });
const section = (page: Page) => canvas(page).getByRole("region", { name: formName, exact: true });
const cells = (page: Page) => section(page).getByRole("button", { name: /^Add Field to / });
const cell = (page: Page, row: number, column: number) => section(page).getByRole("button", { name: `Add Field to ${formName}, row ${row}, column ${column}`, exact: true });
const rowCells = (page: Page, row: number) => section(page).getByRole("button", { name: new RegExp(`^Add Field to .*, row ${row}, column [1-4]$`) });
const tile = (page: Page, label: string): Locator => section(page).getByRole("button", { name: label, exact: true }).locator("xpath=ancestor::li[1]");
const inspector = (page: Page) => page.getByRole("complementary").filter({ has: page.locator("#designer-inspector-heading") });

async function positions(page: Page, labels: readonly string[]): Promise<Record<string, [number, number]>> {
  const result: Record<string, [number, number]> = {};
  for (const label of labels) {
    const li = tile(page, label);
    result[label] = [Number(await li.getAttribute("data-grid-row")), Number(await li.getAttribute("data-grid-column"))];
  }
  return result;
}

async function rendered(page: Page, fieldKey: string): Promise<{ column: string; row: string }> {
  return page.locator(`[data-field-key="${fieldKey}"]`).first().evaluate((node) => ({ column: (node as HTMLElement).style.gridColumn, row: (node as HTMLElement).style.gridRow }));
}

async function placeAt(page: Page, row: number, column: number, fieldName: string) {
  await cell(page, row, column).click();
  const panel = page.getByRole("complementary", { name: "Add Field" });
  await panel.getByLabel("Place an existing field").click();
  await page.getByRole("option", { name: new RegExp(`^${fieldName} \\(`) }).click();
  await panel.getByRole("button", { name: "Place field here" }).click();
  await expect(tile(page, fieldName)).toBeVisible();
}

async function addRow(page: Page) {
  await canvas(page).getByRole("button", { name: formName, exact: true }).click();
  await page.getByRole("button", { name: "Add Row", exact: true }).click();
}

async function selectTile(page: Page, label: string) {
  await section(page).getByRole("button", { name: label, exact: true }).click();
}

const confirmDialog = (page: Page) => page.getByRole("dialog", { name: /^(Clear|Delete) (row|column) — / });

test.describe.serial("FD-01 row and column management", () => {
  let formId = "";

  test("1/2: Add Row adds exactly one four-column row, and dragging a field into it fills that row instead of adding another", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());
    for (const name of ["Field A", "Field B", "Field C", "Field D", "Field E"]) {
      await addField(page, name, "String");
    }
    await page.getByRole("tab", { name: "Design" }).click();

    // A new section shows one row of four cells — and no separate single "Add Field" target below it.
    await expect(cells(page)).toHaveCount(4);
    await expect(rowCells(page, 1)).toHaveCount(4);
    await placeAt(page, 1, 1, "Field A");
    await placeAt(page, 1, 2, "Field B");
    await placeAt(page, 1, 4, "Field C");
    await expect(cells(page)).toHaveCount(1); // only row 1, column 3 is empty; no trailing cell

    await addRow(page);
    await expect(rowCells(page, 2)).toHaveCount(4);
    await expect(cells(page)).toHaveCount(5); // exactly one new row of four
    await expect(rowCells(page, 3)).toHaveCount(0);

    // Dragging Field C into the new row fills it: still two rows, nothing below.
    await tile(page, "Field C").dragTo(cell(page, 2, 3).locator("xpath=ancestor::li[1]"));
    await expect.poll(() => positions(page, ["Field C"])).toEqual({ "Field C": [2, 3] });
    await expect(rowCells(page, 3)).toHaveCount(0);
    await expect(rowCells(page, 2)).toHaveCount(3);
    await expect(rowCells(page, 1)).toHaveCount(2); // columns 3 and 4 of row 1

    await placeAt(page, 2, 1, "Field D");
    await addRow(page);
    await placeAt(page, 3, 2, "Field E");
    expect(await positions(page, ["Field A", "Field B", "Field C", "Field D", "Field E"])).toEqual({ "Field A": [1, 1], "Field B": [1, 2], "Field C": [2, 3], "Field D": [2, 1], "Field E": [3, 2] });
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await signOut(page);
  });

  test("3/8/9/14: Clear row asks first, keeps the empty row usable, and Undo restores the fields in place", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(tile(page, "Field E")).toBeVisible();
    const before = await positions(page, ["Field A", "Field B", "Field C", "Field D", "Field E"]);

    // Keyboard only: select Field D and open Clear row from the inspector.
    await section(page).getByRole("button", { name: "Field D", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(inspector(page).getByRole("heading", { name: "Row 2 · Column 1" })).toBeVisible();
    const clearRow = inspector(page).getByRole("button", { name: "Clear row 2" });
    await clearRow.focus();
    await page.keyboard.press("Enter");
    const dialog = confirmDialog(page);
    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleName(`Clear row — Row 2 of “${formName}”?`);
    await expect(dialog.getByTestId("grid-operation-count")).toHaveText("2 fields will be removed from the layout:");
    await expect(dialog).toContainText("Field D");
    await expect(dialog).toContainText("Field C");
    const axe = await new AxeBuilder({ page }).include('[role="dialog"]').withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(axe.violations.map((violation) => violation.id)).toEqual([]);

    // 8: Escape cancels — nothing changes and focus returns to the button.
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(clearRow).toBeFocused();
    expect(await positions(page, ["Field A", "Field B", "Field C", "Field D", "Field E"])).toEqual(before);

    // 3: confirm — the row stays as four empty cells; Field E stays in row 3.
    await clearRow.click();
    await confirmDialog(page).getByRole("button", { name: "Clear row", exact: true }).click();
    await expect(confirmDialog(page)).toBeHidden();
    await expect(tile(page, "Field D")).toHaveCount(0);
    await expect(tile(page, "Field C")).toHaveCount(0);
    await expect(rowCells(page, 2)).toHaveCount(4);
    expect(await positions(page, ["Field A", "Field B", "Field E"])).toEqual({ "Field A": [1, 1], "Field B": [1, 2], "Field E": [3, 2] });

    // The cleared row is still a working drop target: dragging Field E into it fills that row (no new row appears).
    await tile(page, "Field E").dragTo(cell(page, 2, 4).locator("xpath=ancestor::li[1]"));
    await expect.poll(() => positions(page, ["Field E"])).toEqual({ "Field E": [2, 4] });
    await expect(rowCells(page, 3)).toHaveCount(0);

    // 9: Undo twice — Field E goes back, then Field D and Field C return to their exact cells.
    await page.getByRole("button", { name: "Undo last layout change" }).click();
    await page.getByRole("button", { name: "Undo last layout change" }).click();
    await expect(tile(page, "Field D")).toBeVisible();
    expect(await positions(page, ["Field A", "Field B", "Field C", "Field D", "Field E"])).toEqual(before);
    await expect(page.getByText("Unsaved changes")).toBeHidden(); // back to exactly the saved draft
    await signOut(page);
  });

  test("4/5/6/7/11/12: Delete row, Clear column and Delete column update the saved, previewed and published layout", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(tile(page, "Field E")).toBeVisible();

    // 4: Delete row 1 from Field A's own "More actions" menu; the rows below move up with their columns unchanged.
    await selectTile(page, "Field A");
    await page.getByRole("button", { name: "More actions for Field A" }).click();
    await page.getByRole("menuitem", { name: "Delete row 1…" }).click();
    await expect(confirmDialog(page).getByTestId("grid-operation-count")).toHaveText("2 fields will be removed from the layout:");
    await confirmDialog(page).getByRole("button", { name: "Delete row", exact: true }).click();
    await expect(tile(page, "Field A")).toHaveCount(0);
    expect(await positions(page, ["Field C", "Field D", "Field E"])).toEqual({ "Field D": [1, 1], "Field C": [1, 3], "Field E": [2, 2] });

    // 5: Clear column 3 (Field C) — Field D and Field E do not move.
    await selectTile(page, "Field C");
    await inspector(page).getByRole("button", { name: "Clear column 3 of row 1" }).click();
    await expect(confirmDialog(page).getByTestId("grid-operation-count")).toHaveText("1 field will be removed from the layout:");
    await confirmDialog(page).getByRole("button", { name: "Clear column", exact: true }).click();
    await expect(tile(page, "Field C")).toHaveCount(0);
    expect(await positions(page, ["Field D", "Field E"])).toEqual({ "Field D": [1, 1], "Field E": [2, 2] });

    // 6: put Field A back at row 1, column 3, then Delete column 1 (Field D): Field A moves one column left; row 2 is untouched.
    await placeAt(page, 1, 3, "Field A");
    await selectTile(page, "Field D");
    await inspector(page).getByRole("button", { name: "Delete column 1 of row 1" }).click();
    await expect(confirmDialog(page)).toContainText("the field to its right moves one column left");
    await confirmDialog(page).getByRole("button", { name: "Delete column", exact: true }).click();
    await expect(tile(page, "Field D")).toHaveCount(0);
    expect(await positions(page, ["Field A", "Field E"])).toEqual({ "Field A": [1, 2], "Field E": [2, 2] });

    // 7: a column that is part of a wider field cannot be deleted (it would resize the field); nothing changes.
    const modal = await openFieldConfig(page, "Field E", "Placement & Alignment");
    await modal.getByLabel("Field width").click();
    await page.getByRole("option", { name: "2 columns", exact: true }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    await selectTile(page, "Field E");
    await inspector(page).getByRole("heading", { name: "Row 2 · Columns 2–3" }).waitFor();
    await inspector(page).getByRole("button", { name: "Delete column 2 of row 2" }).click();
    await expect(confirmDialog(page)).toHaveCount(0);
    await expect(page.getByText(/is part of “Field E”, which spans columns 2–3/).first()).toBeVisible();
    expect(await tile(page, "Field E").getAttribute("data-grid-span")).toBe("2");

    // 11: Save draft and reload reproduce the result.
    const expected = { "Field A": [1, 2], "Field E": [2, 2] };
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await page.reload();
    await expect(tile(page, "Field E")).toBeVisible();
    expect(await positions(page, ["Field A", "Field E"])).toEqual(expected);

    // 12: Preview (desktop) matches the designer; phone reflows in reading order.
    await page.getByRole("tab", { name: "Preview" }).click();
    await page.getByRole("button", { name: "Desktop" }).click();
    expect(await rendered(page, "field_a")).toEqual({ column: "2 / span 1", row: "1" });
    expect(await rendered(page, "field_e")).toEqual({ column: "2 / span 2", row: "2" });
    await page.getByRole("button", { name: "Phone 320" }).click();
    expect(await rendered(page, "field_a")).toEqual({ column: "1 / span 1", row: "1" });
    expect(await rendered(page, "field_e")).toEqual({ column: "1 / span 1", row: "2" });

    await page.getByRole("tab", { name: "Design" }).click();
    const results = await new AxeBuilder({ page }).include('[aria-label="Form canvas"]').include("aside").withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations.map((violation) => violation.id)).toEqual([]);
    await publish(page, "FD-01 row and column management");
    await signOut(page);

    // 12: the published form renders the saved layout.
    await page.setViewportSize({ width: 1920, height: 1080 });
    await signIn(page, "clerk");
    await page.goto(`/app/forms/${formId}/records/new`);
    await expect(page.getByLabel("Field E")).toBeVisible();
    await expect(page.locator('[data-breakpoint="desktop"]').first()).toBeVisible();
    expect(await rendered(page, "field_a")).toEqual({ column: "2 / span 1", row: "1" });
    expect(await rendered(page, "field_e")).toEqual({ column: "2 / span 2", row: "2" });
    await signOut(page);
  });
});
