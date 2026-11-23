import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { addField, formIdFromUrl, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * Phase C9 M2: the designer reviews what a new version changes before publishing it (semantic comparison, a plain
 * statement that stored values are never changed, counts over records they can see), publishes, and compares versions
 * on the Versions tab. Accessibility is checked with axe on the review dialog and the comparison.
 */
const run = Date.now().toString(36);
const formName = `Compare intake ${run}`;
let formId = "";

const axe = (page: Page, scope: string) => new AxeBuilder({ page }).include(scope).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();

test.describe.serial("Form version comparison", () => {
  test("designer publishes v1 with two fields", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());
    await addField(page, "Given name", "String");
    await placeFromPicker(page, "Add field", /Given name/);
    await addField(page, "Phone", "String");
    await placeFromPicker(page, "Add field", /Phone/);
    await saveAndPublish(page, "comparison v1");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);
  });

  test("the publish review shows the semantic changes and that stored values are never changed", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await addField(page, "Alias", "String");
    await placeFromPicker(page, "Add field", /Alias/);
    await page.getByRole("region", { name: "Form canvas" }).getByRole("button", { name: "Phone", exact: true }).click();
    await page.getByRole("button", { name: "More actions for Phone" }).click();
    await page.getByRole("menuitem", { name: "Remove from layout" }).click();
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();

    await page.getByRole("button", { name: /^Publish/ }).click();
    const dialog = page.getByRole("dialog", { name: "Publish a new version" });
    await expect(dialog.getByText("Stored values are never changed by publishing")).toBeVisible();
    await expect(dialog.getByText("Changes from v1 to v2")).toBeVisible();
    await expect(dialog.getByRole("list", { name: "Added" })).toContainText("Alias");
    await expect(dialog.getByRole("list", { name: "Removed from the layout" })).toContainText("Phone");
    await expect(dialog.getByText(/Stored values are kept/).first()).toBeVisible();
    await expect(dialog.getByText("You can't see any records of this form.")).toBeVisible();
    expect((await axe(page, '[role="dialog"]')).violations).toEqual([]);
    await dialog.getByLabel("Change reason").fill("comparison v2");
    await dialog.getByRole("button", { name: "Publish", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByText("Published v2")).toBeVisible();
    await signOut(page);
  });

  test("the Versions tab compares two published versions and a version with the draft", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tablist", { name: "Form designer" }).getByRole("tab", { name: /^Versions/ }).click();
    await expect(page.getByRole("cell", { name: "v2" })).toBeVisible();
    // Default after publishing: the current version against the draft, which is the same.
    await expect(page.getByText("No differences.")).toBeVisible();

    await page.getByRole("combobox", { name: "Compare from" }).click();
    await page.getByRole("option", { name: "v1", exact: true }).click();
    await page.getByRole("combobox", { name: "to", exact: true }).click();
    await page.getByRole("option", { name: "v2", exact: true }).click();
    await expect(page.getByText(/v1 → v2:/)).toBeVisible();
    await expect(page.getByText("1 added, 1 removed from the layout.")).toBeVisible();
    await expect(page.getByRole("list", { name: "Added" })).toContainText("Alias");
    await expect(page.getByRole("list", { name: "Removed from the layout" })).toContainText("Phone");
    expect((await axe(page, "main")).violations).toEqual([]);

    await page.getByRole("button", { name: "Swap" }).click();
    await expect(page.getByText(/v2 → v1:/)).toBeVisible();
    await expect(page.getByRole("list", { name: "Added" })).toContainText("Phone");
    await signOut(page);
  });
});
