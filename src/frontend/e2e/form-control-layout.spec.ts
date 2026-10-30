import { expect, test, type Locator, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * The form-control layout standard (docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md) measured in the real browser: label, then the
 * control (with its own action buttons in the same row), then help text, then the validation message. These are geometry
 * assertions with tolerances, not pixel baselines, so they hold across platforms, themes and fonts.
 */
const TOLERANCE = 2;

type Box = { x: number; y: number; width: number; height: number };

async function box(locator: Locator): Promise<Box> {
  const result = await locator.boundingBox();
  expect(result, "element is visible and measurable").not.toBeNull();
  return result!;
}

const bottom = (b: Box) => b.y + b.height;
const centerY = (b: Box) => b.y + b.height / 2;

const searchForm = (page: Page) => page.getByRole("search", { name: "Search records by reference or ID" });
const searchInput = (page: Page) => page.getByRole("textbox", { name: "Search records" });
const searchButton = (page: Page) => searchForm(page).getByRole("button", { name: "Search", exact: true });
const clearButton = (page: Page) => searchForm(page).getByRole("button", { name: "Clear", exact: true });
const help = (page: Page) => page.getByText("Enter an exact record reference (for example REC-017EF770) or a full record ID.");

test.describe("Form control layout standard", () => {
  test("label, control, help and error are in that order, associated, and Search and Clear align with the input", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");

    // Association: the label names the control; help is announced with it.
    const input = searchInput(page);
    await expect(input).toBeVisible();
    await expect(page.getByLabel("Search records", { exact: true })).toHaveCount(1);
    const labelFor = await page.locator("label", { hasText: "Search records" }).first().getAttribute("for");
    expect(labelFor).toBe(await input.getAttribute("id"));
    const helpId = await help(page).getAttribute("id");
    expect((await input.getAttribute("aria-describedby"))?.split(" ")).toContain(helpId);

    await input.fill("REC-017EF77"); // a partial reference: no error while typing
    await expect(page.getByRole("alert").filter({ hasText: "Enter a record reference" })).toHaveCount(0);

    const label = await box(page.locator("label", { hasText: "Search records" }).first());
    const field = await box(input);
    const search = await box(searchButton(page));
    const clear = await box(clearButton(page));
    const helpBox = await box(help(page));

    // Order: label above the control; help below the control group, never between label and control.
    expect(bottom(label)).toBeLessThanOrEqual(field.y + TOLERANCE);
    expect(helpBox.y).toBeGreaterThanOrEqual(bottom(field) - TOLERANCE);
    expect(helpBox.y).toBeGreaterThanOrEqual(bottom(search) - TOLERANCE);

    // Input, Search and Clear: same row, same height, same vertical position.
    expect(Math.abs(centerY(search) - centerY(field))).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(centerY(clear) - centerY(field))).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(search.height - field.height)).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(clear.height - field.height)).toBeLessThanOrEqual(TOLERANCE);
    expect(search.x).toBeGreaterThanOrEqual(field.x + field.width - TOLERANCE);
    expect(clear.x).toBeGreaterThanOrEqual(search.x + search.width - TOLERANCE);

    // The error appears below the group and does not move the buttons.
    await input.fill("Smith");
    await input.press("Enter");
    const error = page.getByRole("alert").filter({ hasText: "Enter a record reference such as REC-1A2B3C4D or a full record ID." });
    await expect(error).toBeVisible();
    const errorBox = await box(error);
    expect(errorBox.y).toBeGreaterThanOrEqual(bottom(await box(input)) - TOLERANCE);
    expect(errorBox.y).toBeGreaterThanOrEqual(bottom(await box(searchButton(page))) - TOLERANCE);
    const searchAfter = await box(searchButton(page));
    const fieldAfter = await box(input);
    expect(Math.abs(searchAfter.y - search.y)).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(fieldAfter.y - field.y)).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(centerY(searchAfter) - centerY(fieldAfter))).toBeLessThanOrEqual(TOLERANCE);
    expect(await input.getAttribute("aria-invalid")).toBe("true");
    expect((await input.getAttribute("aria-describedby"))?.split(" ")).toContain(await error.getAttribute("id"));
  });

  test("Form | Status and Sort by | Rows per page form two aligned two-column rows with equal control heights and the input's height (C7 approved layout)", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    const names = ["Form", "Status", "Sort by", "Rows per page"];
    const labels = await Promise.all(names.map((name) => box(page.locator("label", { hasText: new RegExp(`^${name}$`) }).first())));
    const controls = await Promise.all(names.map((name) => box(page.getByRole("combobox", { name, exact: true }))));
    for (let i = 0; i < names.length; i++) {
      expect(Math.abs(controls[i]!.height - controls[0]!.height), `${names[i]} control height`).toBeLessThanOrEqual(TOLERANCE);
      expect(bottom(labels[i]!), `${names[i]} label above its control`).toBeLessThanOrEqual(controls[i]!.y + TOLERANCE);
    }
    for (const [left, right] of [[0, 1], [2, 3]] as const) {
      expect(Math.abs(labels[right]!.y - labels[left]!.y), `${names[right]} label top`).toBeLessThanOrEqual(TOLERANCE);
      expect(Math.abs(controls[right]!.y - controls[left]!.y), `${names[right]} control top`).toBeLessThanOrEqual(TOLERANCE);
      expect(controls[right]!.x, `${names[right]} sits to the right of ${names[left]}`).toBeGreaterThan(controls[left]!.x + controls[left]!.width - TOLERANCE);
    }
    // Second row below the first, in the same two columns.
    expect(labels[2]!.y).toBeGreaterThan(bottom(controls[0]!) - TOLERANCE);
    expect(Math.abs(controls[2]!.x - controls[0]!.x)).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(controls[3]!.x - controls[1]!.x)).toBeLessThanOrEqual(TOLERANCE);
    expect(Math.abs(controls[0]!.height - (await box(searchInput(page))).height)).toBeLessThanOrEqual(TOLERANCE); // selects and inputs share a height
  });

  test("the search stays usable at 320 px and at 200% and 400% zoom widths without horizontal scrolling", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    await searchInput(page).fill("REC-017EF770");
    for (const width of [640, 320]) {
      await page.setViewportSize({ width, height: 720 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), `no horizontal scroll at ${width}`).toBe(true);
      for (const control of [searchInput(page), searchButton(page), clearButton(page)]) {
        const b = await box(control);
        expect(b.x).toBeGreaterThanOrEqual(0);
        expect(b.x + b.width).toBeLessThanOrEqual(width + 1);
        expect(b.width).toBeGreaterThan(40);
      }
      // Wrapped or beside, the buttons stay with the input (never above it) and the help text stays below the group.
      const field = await box(searchInput(page));
      const search = await box(searchButton(page));
      expect(search.y).toBeGreaterThanOrEqual(field.y - TOLERANCE);
      expect((await box(help(page))).y).toBeGreaterThanOrEqual(bottom(search) - TOLERANCE);
      await searchButton(page).focus();
      await expect(searchButton(page)).toBeFocused();
    }
  });

  test("the same order holds in the designer's Add field dialog (label, control, then help text)", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms");
    // Open any form the designer can design.
    await page.locator('a[href*="/design"]').first().click();
    await page.getByRole("tab", { name: /^Fields/ }).click();
    await page.getByRole("button", { name: "Add field", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Add field" });
    await expect(dialog).toBeVisible();
    const key = dialog.getByLabel("Field key");
    const label = await box(dialog.locator("label", { hasText: "Field key" }).first());
    const control = await box(key);
    const describedBy = (await key.getAttribute("aria-describedby"))?.split(" ")[0];
    expect(describedBy, "Field key has help text").toBeTruthy();
    const helpText = await box(dialog.locator(`[id="${describedBy}"]`));
    expect(bottom(label)).toBeLessThanOrEqual(control.y + TOLERANCE);
    expect(helpText.y).toBeGreaterThanOrEqual(bottom(control) - TOLERANCE);
    await page.keyboard.press("Escape");
    await signOut(page);
  });
});
