import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { addField, formIdFromUrl, openFieldConfig, publish, signIn, signOut } from "./support";

/**
 * FD-01 — Form Designer layout fidelity (Design → Save Draft → Reload → Preview → Publish → Render).
 *
 * Original defect: a field placed in an empty cell of a four-column row (e.g. column 3) was saved without any column
 * information and the renderer packed it into the next free position, often beside fields of an earlier row. Every
 * assertion below reads logical grid state — the canvas tiles' data-grid-row/column/span and the record renderer's
 * explicit CSS grid placement — rather than screenshots.
 */
const run = Date.now().toString(36);
const formName = `Layout fidelity ${run}`;

const canvas = (page: Page) => page.getByRole("region", { name: "Form canvas" });
const section = (page: Page) => canvas(page).getByRole("region", { name: formName, exact: true });
const cell = (page: Page, row: number, column: number) => section(page).getByRole("button", { name: `Add Field to ${formName}, row ${row}, column ${column}`, exact: true });
const tile = (page: Page, label: string): Locator => section(page).getByRole("button", { name: label, exact: true }).locator("xpath=ancestor::li[1]");

async function tilePosition(page: Page, label: string): Promise<[number, number, number]> {
  const li = tile(page, label);
  return [Number(await li.getAttribute("data-grid-row")), Number(await li.getAttribute("data-grid-column")), Number(await li.getAttribute("data-grid-span"))];
}

async function canvasLayout(page: Page, labels: readonly string[]) {
  const result: Record<string, [number, number, number]> = {};
  for (const label of labels) {
    result[label] = await tilePosition(page, label);
  }
  return result;
}

/** The renderer's explicit placement of a field cell (Preview and the record page use the same FormRenderer). */
async function rendered(page: Page, fieldKey: string): Promise<{ column: string; row: string }> {
  return page.locator(`[data-field-key="${fieldKey}"]`).first().evaluate((node) => ({ column: (node as HTMLElement).style.gridColumn, row: (node as HTMLElement).style.gridRow }));
}

async function placeExisting(page: Page, row: number, column: number, fieldName: string) {
  await page.getByRole("tab", { name: "Design" }).click();
  await cell(page, row, column).click();
  const inspector = page.getByRole("complementary", { name: "Add Field" });
  await expect(inspector.getByText(`Row ${row}, column ${column} in`)).toBeVisible();
  await inspector.getByLabel("Place an existing field").click();
  await page.getByRole("option", { name: new RegExp(`^${fieldName} \\(`) }).click();
  await inspector.getByRole("button", { name: "Place field here" }).click();
  await expect(tile(page, fieldName)).toBeVisible();
}

async function addRow(page: Page) {
  await canvas(page).getByRole("button", { name: formName, exact: true }).click();
  await page.getByRole("button", { name: "Add Row" }).click();
}

const DESIGN = {
  "Field A": [1, 1, 1],
  "Field B": [1, 4, 1],
  "Field C": [2, 3, 1],
  "Field D": [3, 2, 1],
  "Field E": [4, 3, 1],
} as const;
const LABELS = Object.keys(DESIGN);

test.describe.serial("FD-01 Form Designer layout fidelity", () => {
  let formId = "";

  test("FD-01-01/02/03: fields placed in exact cells — including empty leading columns and direct creation — keep their row and column", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());
    for (const name of ["Field A", "Field B", "Field C", "Field D"]) {
      await addField(page, name, "String");
    }

    // Row 1 (a new form's section starts with one empty row of four cells): A in column 1, B in column 4 (columns 2 and 3
    // stay empty).
    await page.getByRole("tab", { name: "Design" }).click();
    await expect(section(page).getByRole("button", { name: /^Add Field to / })).toHaveCount(4);
    await placeExisting(page, 1, 1, "Field A");
    await placeExisting(page, 1, 4, "Field B");
    // FD-01-01 (the original defect): row 2 has a single field in column 3 with two empty leading columns. Before FD-01
    // it was packed beside Field B / into column 1.
    await addRow(page);
    await placeExisting(page, 2, 3, "Field C");
    await addRow(page);
    await placeExisting(page, 3, 2, "Field D");
    // FD-01-03: a field created directly in row 4, column 3.
    await addRow(page);
    await cell(page, 4, 3).click();
    await page.getByRole("complementary", { name: "Add Field" }).getByRole("button", { name: "New field…" }).click();
    const dialog = page.getByRole("dialog", { name: "Add field" });
    await dialog.getByLabel("Display name").fill("Field E");
    await dialog.getByLabel("Storage type").click();
    await page.getByRole("option", { name: /^String$/ }).click();
    await dialog.getByRole("button", { name: "Add field" }).click();
    await expect(dialog).toBeHidden();
    await expect(tile(page, "Field E")).toBeVisible();

    expect(await canvasLayout(page, LABELS)).toEqual(DESIGN);
    // Empty cells of occupied rows remain real, selectable placement targets.
    for (const [row, column] of [[1, 2], [1, 3], [2, 1], [2, 2], [2, 4], [3, 1], [3, 3], [3, 4]] as const) {
      await expect(cell(page, row, column)).toBeVisible();
    }

    // FD-01-07: Save draft, reload, same logical layout.
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    await expect(tile(page, "Field E")).toBeVisible();
    expect(await canvasLayout(page, LABELS)).toEqual(DESIGN);

    // FD-01-08: Preview (the record renderer, desktop) draws the same rows and columns as the designer.
    await page.getByRole("tab", { name: "Preview" }).click();
    await page.getByRole("button", { name: "Desktop" }).click();
    for (const [label, [row, column, span]] of Object.entries(DESIGN)) {
      expect(await rendered(page, label.toLowerCase().replace(" ", "_"))).toEqual({ column: `${column} / span ${span}`, row: String(row) });
    }

    // FD-01-10: tablet (≤ 2 columns) and phone (1 column) reflow in source order without preserving empty desktop cells.
    await page.getByRole("button", { name: "Phone 320" }).click();
    for (const [index, key] of ["field_a", "field_b", "field_c", "field_d", "field_e"].entries()) {
      expect(await rendered(page, key)).toEqual({ column: "1 / span 1", row: String(index + 1) });
    }
    await page.getByRole("button", { name: "Tablet 768" }).click();
    for (const key of ["field_a", "field_b", "field_c", "field_d", "field_e"]) {
      expect(Number((await rendered(page, key)).column.split(" ")[0])).toBeLessThanOrEqual(2);
    }
    const order = await page.locator("[data-field-key]").evaluateAll((nodes) => nodes.map((node) => node.getAttribute("data-field-key")));
    expect(order).toEqual(["field_a", "field_b", "field_c", "field_d", "field_e"]);
    await signOut(page);
  });

  test("FD-01-04/05/06: drag-and-drop and resizing update the persisted positions without moving unrelated fields", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(tile(page, "Field B")).toBeVisible();

    // FD-01-05: drag Field B from row 1, column 4 to the empty cell at row 3, column 4; its old cell becomes empty.
    await tile(page, "Field B").dragTo(cell(page, 3, 4).locator("xpath=ancestor::li[1]"));
    await expect.poll(() => tilePosition(page, "Field B")).toEqual([3, 4, 1]);
    expect(await canvasLayout(page, LABELS)).toEqual({ ...DESIGN, "Field B": [3, 4, 1] });
    await expect(cell(page, 1, 4)).toBeVisible();

    // FD-01-04: drag Field A from column 1 to column 4 of the same row.
    await tile(page, "Field A").dragTo(cell(page, 1, 4).locator("xpath=ancestor::li[1]"));
    await expect.poll(() => tilePosition(page, "Field A")).toEqual([1, 4, 1]);
    await expect(cell(page, 1, 1)).toBeVisible();

    // An occupied cell or a too-narrow gap is not a drop target: dropping Field D onto row 2's two-cell gap with width 3
    // is impossible, so first try widening D in place — 3 columns has no room (B is at column 4), 2 does.
    const modal = await openFieldConfig(page, "Field D", "Placement & Alignment");
    await modal.getByLabel("Field width").click();
    await expect(page.getByRole("option", { name: "3 columns (no room in this row)" })).toHaveAttribute("aria-disabled", "true");
    await expect(page.getByRole("option", { name: "4 columns (full row) (no room in this row)" })).toHaveAttribute("aria-disabled", "true");
    await page.getByRole("option", { name: "2 columns", exact: true }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    // FD-01-06: D spans columns 2-3, B stays at column 4, nothing else moved.
    const moved = { "Field A": [1, 4, 1], "Field B": [3, 4, 1], "Field C": [2, 3, 1], "Field D": [3, 2, 2], "Field E": [4, 3, 1] };
    expect(await canvasLayout(page, LABELS)).toEqual(moved);

    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await page.reload();
    await expect(tile(page, "Field E")).toBeVisible();
    expect(await canvasLayout(page, LABELS)).toEqual(moved);

    const results = await new AxeBuilder({ page }).include('[aria-label="Form canvas"]').withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations.map((violation) => violation.id)).toEqual([]);

    await publish(page, "FD-01 explicit placement");
    await signOut(page);
  });

  test("FD-01-09/13: the published renderer draws the saved positions, and records are created and validated as before", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await signIn(page, "clerk");
    await page.goto(`/app/forms/${formId}/records/new`);
    await expect(page.getByLabel("Field E")).toBeVisible();
    // The record page measures its container; at this width it renders the desktop grid.
    await expect(page.locator('[data-breakpoint="desktop"]').first()).toBeVisible();
    const expected = { field_a: [1, 4, 1], field_b: [3, 4, 1], field_c: [2, 3, 1], field_d: [3, 2, 2], field_e: [4, 3, 1] } as const;
    // Rows 1-4 in the designer: A (row 1), C (row 2), D + B (row 3), E (row 4).
    for (const [key, [row, column, span]] of Object.entries(expected)) {
      expect(await rendered(page, key)).toEqual({ column: `${column} / span ${span}`, row: String(row) });
    }
    // Keyboard / reading order follows the rows: A, C, D, B, E.
    const order = await page.locator("[data-field-key]").evaluateAll((nodes) => nodes.map((node) => node.getAttribute("data-field-key")));
    expect(order).toEqual(["field_a", "field_c", "field_d", "field_b", "field_e"]);

    for (const [label, value] of [["Field A", "alpha"], ["Field B", "bravo"], ["Field C", "charlie"], ["Field D", "delta"], ["Field E", "echo"]] as const) {
      await page.getByLabel(label).fill(value);
    }
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page).toHaveURL(/\/app\/records\/[0-9a-f-]{36}/);
    for (const value of ["alpha", "bravo", "charlie", "delta", "echo"]) {
      await expect(page.getByText(value, { exact: true }).first()).toBeVisible();
    }
    for (const [key, [row, column, span]] of Object.entries(expected)) {
      expect(await rendered(page, key)).toEqual({ column: `${column} / span ${span}`, row: String(row) });
    }
    await signOut(page);
  });

  test("rollback protection: a save whose response lost the column positions is reported and stays unsaved", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(tile(page, "Field C")).toBeVisible();
    // Simulate a server that predates FD-01: it stores the draft but drops every columnStart from the saved layout.
    await page.route(`**/api/v1/form-definitions/${formId}/draft`, async (route) => {
      if (route.request().method() !== "PUT") {
        return route.continue();
      }
      const response = await route.fetch();
      const body = (await response.json()) as Record<string, unknown>;
      const draft = (body["layoutJson"] ? body : body["result"]) as { layoutJson: string };
      draft.layoutJson = draft.layoutJson.replace(/,"columnStart":\d/g, "");
      await route.fulfill({ response, json: body });
    });
    await tile(page, "Field C").dragTo(cell(page, 2, 1).locator("xpath=ancestor::li[1]"));
    await expect.poll(() => tilePosition(page, "Field C")).toEqual([2, 1, 1]);
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByRole("status").or(page.getByRole("alert")).filter({ hasText: "did not keep the column positions" }).first()).toBeVisible();
    await expect(page.getByText("Unsaved changes")).toBeVisible();
    // The designer keeps the designed arrangement on screen instead of adopting the stripped layout.
    expect(await tilePosition(page, "Field C")).toEqual([2, 1, 1]);
    expect(await tilePosition(page, "Field A")).toEqual([1, 4, 1]);

    // With a server that keeps positions the warning clears and the save completes.
    await page.unroute(`**/api/v1/form-definitions/${formId}/draft`);
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await expect(page.getByText("did not keep the column positions")).toHaveCount(0);
    expect(await tilePosition(page, "Field C")).toEqual([2, 1, 1]);
    await signOut(page);
  });
});
