import { expect, test } from "@playwright/test";
import { addField, formIdFromUrl, saveAndPublish, signIn, signOut } from "./support";

/**
 * C1-UX representative flows not already covered by form-management.spec.ts / pb14-accessibility.spec.ts:
 *  1. Click an empty grid cell on the Layout tab, add a field there, and resize it with the keyboard-accessible
 *     Narrower/Wider buttons (brief §B — drag is never the only way to change placement/width).
 *  2. Clicking a placed field selects it and populates the Properties inspector; double-clicking its title renames
 *     the display label inline without touching the field key, storage type or any stored value.
 *  3. The Actions column on the Forms list shows one visible primary action plus a "…" overflow menu for the rest.
 *  4. Unsaved layout changes block SPA navigation until the designer confirms or saves; Undo/Redo step through
 *     layout history.
 */
const run = Date.now().toString(36);
const formName = `Canvas ${run}`;
let formId = "";

test.describe.serial("Designer canvas: selection, empty-cell placement, rename, actions column, undo/nav-guard", () => {
  test("designer builds a form using the visual grid canvas", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());

    await addField(page, "Case title", "String");
    await page.getByRole("tab", { name: "Layout" }).click();

    // Click an empty cell's "+ Add field" affordance and place the unplaced field there.
    const emptyCell = page.getByRole("button", { name: /^Add field in .+, column \d+$/ }).first();
    await emptyCell.click();
    const picker = page.getByRole("group", { name: "Add field to this cell" });
    await picker.getByLabel("Field to place here").click();
    await page.getByRole("option", { name: /Case title/ }).click();
    await picker.getByRole("button", { name: "Place" }).click();
    const card = page.getByRole("listitem").filter({ hasText: "Field: Case title" });
    await expect(card).toBeVisible();

    // Clicking the card selects it and the inspector shows it; the inspector's Label input mirrors the card's.
    await card.click();
    const inspector = page.getByRole("complementary", { name: "Properties" });
    await expect(inspector.getByText(/Field: Case title/)).toBeVisible();

    // Keyboard-accessible width change: Narrower/Wider buttons are real buttons, reachable and operable by keyboard.
    const wider = card.getByRole("button", { name: "Wider: increase width of Field: Case title (String)" });
    await expect(wider).toBeDisabled(); // already full width (4 of 4)
    const narrower = card.getByRole("button", { name: "Narrower: decrease width of Field: Case title (String)" });
    await narrower.focus();
    await page.keyboard.press("Enter");
    await expect(card.getByRole("combobox", { name: "Width" }).first()).toHaveText(/75%/);
    await expect(wider).toBeEnabled();

    // Double-click the title to rename inline; the field key and type never change.
    await card.getByTitle("Double-click to rename the display label").dblclick();
    const rename = card.getByLabel("Display label");
    await rename.fill("Matter title");
    await rename.press("Enter");
    await expect(page.getByRole("listitem").filter({ hasText: "Field: Matter title" })).toBeVisible();
    await expect(page.getByText("Unsaved changes")).toBeVisible();

    // Undo restores the previous label; redo re-applies the rename.
    await page.getByRole("button", { name: "Undo last layout change" }).click();
    await expect(page.getByRole("listitem").filter({ hasText: "Field: Case title" })).toBeVisible();
    await page.getByRole("button", { name: "Redo last undone layout change" }).click();
    await expect(page.getByRole("listitem").filter({ hasText: "Field: Matter title" })).toBeVisible();

    await saveAndPublish(page, "initial canvas layout");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);
  });

  test("unsaved layout changes block navigation until the designer confirms or saves", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: "Layout" }).click();
    const card = page.getByRole("listitem").filter({ hasText: "Field: Matter title" });
    await card.getByRole("button", { name: "Narrower: decrease width of Field: Matter title (String)" }).click();
    await expect(page.getByText("Unsaved changes")).toBeVisible();

    await page.getByRole("link", { name: "All forms" }).click();
    const guard = page.getByRole("dialog", { name: "Discard unsaved layout changes?" });
    await expect(guard).toBeVisible();
    await guard.getByRole("button", { name: "Stay and keep editing" }).click();
    await expect(guard).toBeHidden();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();

    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await page.getByRole("link", { name: "All forms" }).click();
    await expect(page.getByRole("dialog", { name: "Discard unsaved layout changes?" })).toHaveCount(0);
    await expect(page.getByRole("heading", { level: 1, name: "Forms" })).toBeVisible();
    await signOut(page);
  });

  test("Forms list Actions column: one visible primary action plus an overflow menu", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms");
    const row = page.getByRole("row", { name: new RegExp(formName) });
    await expect(row.getByRole("link", { name: "Design" })).toBeVisible();
    const overflow = row.getByRole("button", { name: new RegExp(`More actions for ${formName}`) });
    await overflow.click();
    await page.getByRole("menuitem", { name: /Records using this form/ }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Records" })).toBeVisible();
    await signOut(page);
  });
});
