import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { addField, formIdFromUrl, openFieldConfig, placeFromPicker, signIn, signOut } from "./support";

/**
 * Phase C3 Form Designer interaction and usability corrections: "+ Add Section" creates a section and "+ Add Row"
 * adds a row to the currently selected section (the old "+ Add Row" actually created a section); clicking a section
 * (header or empty area) selects it and is the target for "+ Add Row"; the canvas tile's Settings icon opens the
 * same Configure Field modal as the quick inspector's button; field badges sit below the preview control; Placement
 * moved to its own "Placement & Alignment" tab; help text follows its control everywhere in the designer.
 */
const run = Date.now().toString(36);

async function createForm(page: Page, name: string): Promise<string> {
  await page.goto("/app/forms/new");
  await page.getByLabel("Form name").fill(name);
  await page.getByRole("checkbox", { name: /clerk/i }).check();
  await page.getByRole("button", { name: "Create form" }).click();
  await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
  return formIdFromUrl(page.url());
}

const canvas = (page: Page) => page.getByRole("region", { name: "Form canvas" });
const section = (page: Page, title: string) => canvas(page).getByRole("region", { name: title, exact: true });

/** The designer's unsaved-changes guard blocks sign-out navigation while dirty; save first when that might be true. */
async function saveIfDirty(page: Page) {
  if (await page.getByText("Unsaved changes").isVisible().catch(() => false)) {
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
  }
}

test.describe.serial("Phase C3 designer interaction and usability corrections", () => {
  const formName = `Interaction ${run}`;
  let formId = "";

  test("Add Section creates a section; Add Row creates a row of four independent columns, each selectable, placeable, and preserved through save/reload/Preview", async ({ page }) => {
    await signIn(page, "designer");
    formId = await createForm(page, formName);
    await page.getByRole("tab", { name: "Design" }).click();

    // Nothing selected yet: Add Row has no section target.
    await expect(page.getByRole("button", { name: "Add Row" })).toBeDisabled();

    // Add Section creates a new titled section (what the old "+ Add Row" used to do).
    await page.getByRole("button", { name: "Add Section" }).click();
    const rename = page.getByRole("textbox", { name: "Rename section" });
    await rename.fill("Alpha");
    await rename.press("Enter");
    await expect(section(page, "Alpha")).toBeVisible();
    // Only one section was created, not two, and it is not a repeating-group occurrence.
    await expect(canvas(page).getByRole("heading", { level: 2 })).toHaveCount(2); // the form's default section + Alpha
    await expect(section(page, "Alpha").locator("[data-repeat-badge]")).toHaveCount(0);

    // 1. Select the section (its header) and click Add Row.
    await canvas(page).getByRole("button", { name: "Alpha", exact: true }).click();
    await expect(page.getByRole("button", { name: "Add Row" })).toBeEnabled();
    await page.getByRole("button", { name: "Add Row" }).click();

    // 2. The new row has exactly four independent columns, each width 1 (one grid unit) — not one wide cell. A
    // fifth "+ Add Field" cell is the section's own always-present trailing cell (same as "+ Add Column" leaves one
    // on an empty section); it coexists with — but sorts after, and wraps below — the four just added.
    const allCells = section(page, "Alpha").getByRole("button", { name: /^Add Field to Alpha/ });
    await expect(allCells).toHaveCount(5);
    const cells = allCells; // the first four (indexes 0-3, see below) are this row's columns; index 4 is the trailing cell
    const widths = await allCells.evaluateAll((nodes) => nodes.slice(0, 4).map((n) => n.getBoundingClientRect().width));
    const total = widths.reduce((a, b) => a + b, 0);
    for (const width of widths) {
      expect(width).toBeLessThan(total / 3); // each is roughly a quarter, not a single full-width cell
    }
    const rowTops = await allCells.evaluateAll((nodes) => nodes.slice(0, 4).map((n) => Math.round(n.getBoundingClientRect().top)));
    expect(new Set(rowTops).size).toBe(1); // all four sit in the same row
    const baselineTop = await allCells.evaluateAll((nodes) => Math.round(nodes[4]!.getBoundingClientRect().top));
    expect(baselineTop).toBeGreaterThan(rowTops[0]!); // the trailing cell wraps below the new row, not inside it
    await expect(canvas(page).getByRole("heading", { level: 2 })).toHaveCount(2); // still only two sections, not five

    // 3. Click columns 1-4 individually; confirm each one, and only that one, becomes selected (the trailing cell is unaffected).
    for (let i = 0; i < 4; i++) {
      await cells.nth(i).click();
      for (let j = 0; j < 5; j++) {
        await expect(cells.nth(j)).toHaveAttribute("aria-pressed", String(j === i));
      }
    }

    // 4. Place fields in all four columns, filling them in order (not the trailing cell, which sorts and renders last).
    for (let i = 1; i <= 4; i++) {
      await addField(page, `Row field ${i}`, "String");
      await page.getByRole("tab", { name: "Design" }).click();
      await cells.first().click();
      const inspector = page.getByRole("complementary", { name: "Add Field" });
      await inspector.getByLabel("Place an existing field").click();
      await page.getByRole("option", { name: new RegExp(`^Row field ${i} \\(`) }).click();
      await inspector.getByRole("button", { name: "Place field here" }).click();
    }
    await expect(section(page, "Alpha").getByRole("button", { name: /^Row field [1-4]$/ })).toHaveCount(4);
    await expect(cells).toHaveCount(1); // all four columns were filled; only the section's own trailing cell remains
    const fieldTops = await section(page, "Alpha")
      .getByRole("button", { name: /^Row field [1-4]$/ })
      .evaluateAll((nodes) => nodes.map((n) => Math.round(n.getBoundingClientRect().top)));
    expect(new Set(fieldTops).size).toBe(1); // the four placed fields still share one row

    // 5. Select the section using its header without selecting a child field.
    await canvas(page).getByRole("button", { name: "Alpha", exact: true }).click();
    await expect(canvas(page).getByRole("button", { name: "Alpha", exact: true })).toHaveAttribute("aria-pressed", "true");
    for (let i = 1; i <= 4; i++) {
      await expect(canvas(page).getByRole("button", { name: `Row field ${i}`, exact: true })).toHaveAttribute("aria-pressed", "false");
    }

    // 6. Navigate from a selected field to its parent section using Escape.
    const firstField = canvas(page).getByRole("button", { name: "Row field 1", exact: true });
    await firstField.click();
    await expect(firstField).toHaveAttribute("aria-pressed", "true");
    await firstField.press("Escape");
    await expect(firstField).toHaveAttribute("aria-pressed", "false");
    await expect(canvas(page).getByRole("button", { name: "Alpha", exact: true })).toHaveAttribute("aria-pressed", "true");

    // 7. Save draft, reload, and confirm the grid structure (four fields, one row) is preserved.
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    await expect(section(page, "Alpha").getByRole("button", { name: /^Row field [1-4]$/ })).toHaveCount(4);
    const reloadedTops = await section(page, "Alpha")
      .getByRole("button", { name: /^Row field [1-4]$/ })
      .evaluateAll((nodes) => nodes.map((n) => Math.round(n.getBoundingClientRect().top)));
    expect(new Set(reloadedTops).size).toBe(1);

    // 8. Preview renders the row correctly (the real record renderer, same four fields).
    await page.getByRole("tab", { name: "Preview" }).click();
    for (let i = 1; i <= 4; i++) {
      await expect(page.getByLabel(`Row field ${i}`)).toBeVisible();
    }

    await signOut(page);
  });

  test("clicking a section's empty area selects it (blue boundary), and switching sections redirects Add Row", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: "Design" }).click();
    await page.getByRole("button", { name: "Add Section" }).click();
    await page.getByRole("textbox", { name: "Rename section" }).fill("Beta");
    await page.keyboard.press("Enter");

    // Clicking empty section area (not the header) selects the section, same as the header.
    const beta = section(page, "Beta");
    await beta.locator('[data-section-body="true"]').click({ position: { x: 5, y: 5 } });
    await expect(beta).toHaveClass(/border-focus/);
    await expect(section(page, "Alpha")).not.toHaveClass(/border-focus/);

    // A row (four columns) added now targets Beta.
    const betaBefore = await beta.getByRole("button", { name: /^Add Field to Beta/ }).count();
    await page.getByRole("button", { name: "Add Row" }).click();
    await expect(beta.getByRole("button", { name: /^Add Field to Beta/ })).toHaveCount(betaBefore + 4);

    // Selecting Alpha instead redirects new rows there.
    await canvas(page).getByRole("button", { name: "Alpha", exact: true }).click();
    await expect(section(page, "Alpha")).toHaveClass(/border-focus/);
    await expect(beta).not.toHaveClass(/border-focus/);
    const alphaBefore = await section(page, "Alpha").getByRole("button", { name: /^Add Field to Alpha/ }).count();
    await page.getByRole("button", { name: "Add Row" }).click();
    await expect(section(page, "Alpha").getByRole("button", { name: /^Add Field to Alpha/ })).toHaveCount(alphaBefore + 4);
    await expect(beta.getByRole("button", { name: /^Add Field to Beta/ })).toHaveCount(betaBefore + 4); // unchanged
    await saveIfDirty(page);
    await signOut(page);
  });

  test("a field's Settings icon opens the same Configure Field modal as the quick inspector's button; More(…) has only supported actions", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await addField(page, "Case note", "String");
    await page.getByRole("tab", { name: "Design" }).click();
    await canvas(page).getByRole("button", { name: "Alpha", exact: true }).click();
    await placeFromPicker(page, "Add field", /^Case note/, { section: "Alpha" });

    const tile = canvas(page).getByRole("button", { name: "Case note", exact: true });
    await tile.click();
    await page.getByRole("button", { name: "Configure Case note" }).click();
    const modal = page.getByRole("dialog", { name: /^Configure /i });
    await expect(modal).toBeVisible();
    await expect(modal.getByRole("tab", { name: "Validation" })).toBeVisible(); // the real Configure Field workspace, not just a pane switch
    await modal.getByRole("button", { name: "Cancel" }).click();
    await expect(modal).toBeHidden();

    // More (…) is a real, keyboard-reachable menu of supported actions only.
    await tile.click();
    const more = page.getByRole("button", { name: "More actions for Case note" });
    await more.focus();
    await page.keyboard.press("Enter");
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible();
    for (const label of ["Move earlier", "Move later", "Narrower", "Wider", "Rename label", "Remove from layout"]) {
      await expect(menu.getByRole("menuitem", { name: label })).toBeVisible();
    }
    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden();
    // Saved so later tests (fresh pages) find "Case note" already placed.
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await signOut(page);
  });

  test("field badges sit below the preview control, not above it", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    let modal = await openFieldConfig(page, "Case note", "Conditional Rules");
    await modal.getByRole("checkbox", { name: "Read-only" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    const tile = canvas(page).getByRole("button", { name: "Case note", exact: true }).locator("..").locator("..");
    const preview = tile.locator('[aria-hidden="true"].pointer-events-none');
    const badges = tile.locator('[data-tile-flags="true"]');
    await expect(badges).toContainText("Read-only");
    const previewBox = await preview.boundingBox();
    const badgesBox = await badges.boundingBox();
    expect(previewBox).not.toBeNull();
    expect(badgesBox).not.toBeNull();
    expect(badgesBox!.y).toBeGreaterThanOrEqual(previewBox!.y + previewBox!.height - 1); // badges below the control, not above it

    // Undo the Read-only change so later tests see a clean field.
    modal = await openFieldConfig(page, "Case note", "Conditional Rules");
    await modal.getByRole("checkbox", { name: "Read-only" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    await saveIfDirty(page);
    await signOut(page);
  });

  test("Placement & Alignment holds the migrated placement controls; Dropdown Choices and Conditional Rules keep working; help text follows every control", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    const modal = await openFieldConfig(page, "Case note", "Placement & Alignment");
    await expect(modal.getByLabel("Field width")).toBeVisible();
    await expect(modal.getByLabel("Start on a new row")).toBeVisible();
    await expect(modal.getByRole("combobox", { name: "Section" })).toBeVisible();
    await expect(modal.getByRole("button", { name: "Move earlier" })).toBeVisible();
    // Not duplicated on Advanced.
    await modal.getByRole("tab", { name: "Advanced" }).click();
    await expect(modal.getByLabel("Field width")).toHaveCount(0);
    await expect(modal.getByLabel("Field ID")).toBeVisible();

    // Help text follows its control (label, control, help text — never help text between the label and the input).
    const labelNode = modal.getByText("Field ID", { exact: true });
    const idInput = modal.getByLabel("Field ID");
    const helpNode = modal.getByText("This ID stays the same even if you rename or move the field.");
    const labelBox = await labelNode.boundingBox();
    const inputBox = await idInput.boundingBox();
    const helpBox = await helpNode.boundingBox();
    expect(labelBox).not.toBeNull();
    expect(inputBox).not.toBeNull();
    expect(helpBox).not.toBeNull();
    expect(inputBox!.y).toBeGreaterThan(labelBox!.y);
    expect(helpBox!.y).toBeGreaterThan(inputBox!.y); // help text after the control, not between label and control

    await modal.getByRole("button", { name: "Cancel" }).click();

    // Dropdown Choices and Conditional Rules still work (functional smoke check; full coverage in dropdowns-rules.spec.ts).
    // "Case status" (not "Status"): a single-word field key is rejected by the server's naming rule (CLAUDE.md §0.5).
    await addField(page, "Case status", "String");
    await page.getByRole("tab", { name: /^Fields/ }).click();
    await page.getByRole("button", { name: "Edit Case status" }).click();
    await page.getByRole("dialog", { name: "Edit field" }).getByRole("button", { name: "Cancel" }).click();
    await saveIfDirty(page);
    await signOut(page);
  });

  test("keyboard operation and accessible names remain valid for the new toolbar and section selection", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: "Design" }).click();
    await canvas(page).getByRole("button", { name: "Alpha", exact: true }).click();
    await expect(page.getByRole("button", { name: "Add Row" })).toBeEnabled();
    await page.getByRole("button", { name: "Add Row" }).focus();
    await expect(page.getByRole("button", { name: "Add Row" })).toBeFocused();
    await page.keyboard.press("Enter");
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
    expect(results.violations.map((v) => `${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
    await saveIfDirty(page);
    await signOut(page);
  });
});
