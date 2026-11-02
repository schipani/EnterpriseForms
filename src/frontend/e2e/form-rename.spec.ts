import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { addField, formIdFromUrl, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * Form rename, as a person does it: from the Forms list (published form) and from the Form Designer (draft-only form,
 * with unsaved layout edits that must survive). Published versions are untouched, the clerk's record shows the new name,
 * and someone without design rights is never offered the action.
 */
const run = Date.now().toString(36);
const publishedName = `Rename intake ${run}`;
const publishedRenamed = `Renamed intake ${run}`;
const draftName = `Rename draft ${run}`;
const draftRenamed = `Renamed draft ${run}`;
let publishedId = "";
let recordUrl = "";

test.describe.serial("Form rename", () => {
  test("designer publishes a form and the clerk creates a record", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(publishedName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: publishedName })).toBeVisible();
    publishedId = formIdFromUrl(page.url());
    await addField(page, "Given name", "String");
    await placeFromPicker(page, "Add field", /Given name/);
    await saveAndPublish(page, "rename baseline");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(publishedName) }).getByRole("button", { name: "New record" }).click();
    await page.getByLabel(/Given name/).fill("Casey");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    recordUrl = page.url();
    // No design right, no rename action.
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(publishedName) }).getByRole("button", { name: /More actions/ }).click();
    await expect(page.getByRole("menuitem", { name: "Rename" })).toHaveCount(0);
    await page.keyboard.press("Escape");
    await signOut(page);
  });

  test("designer renames the published form from the Forms list", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(publishedName) }).getByRole("button", { name: /More actions/ }).click();
    await page.getByRole("menuitem", { name: "Rename" }).click();
    const dialog = page.getByRole("dialog", { name: "Rename form" });
    await expect(dialog.getByText(publishedName)).toBeVisible();
    const input = dialog.getByLabel(/New name/);
    await expect(input).toHaveValue(publishedName);
    await expect(dialog.getByRole("button", { name: "Save name" })).toBeDisabled();
    await input.fill("   ");
    await dialog.getByRole("button", { name: "Save name" }).click();
    await expect(dialog.getByText("Enter a form name.")).toBeVisible();
    expect((await new AxeBuilder({ page }).include('[role="dialog"]').withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze()).violations).toEqual([]);
    await input.fill(publishedRenamed);
    await dialog.getByRole("button", { name: "Save name" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("status").filter({ hasText: "Form renamed" })).toContainText(publishedRenamed);
    await expect(page.getByRole("row", { name: new RegExp(publishedRenamed) })).toBeVisible();
    await expect(page.getByRole("row", { name: new RegExp(publishedName) })).toHaveCount(0);

    // Still one published version, and the designer heading shows the new name.
    await page.goto(`/app/forms/${publishedId}/design`);
    await expect(page.getByRole("heading", { level: 1, name: publishedRenamed })).toBeVisible();
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await expect(page.getByText(publishedRenamed).first()).toBeVisible();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await signOut(page);
  });

  test("designer renames a draft-only form from the designer without losing unsaved layout edits", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(draftName);
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: draftName })).toBeVisible();
    await addField(page, "Statement", "String");
    await placeFromPicker(page, "Add field", /Statement/);
    await expect(page.getByText("Unsaved changes")).toBeVisible();

    await page.getByRole("button", { name: `Edit name of ${draftName}` }).click();
    const dialog = page.getByRole("dialog", { name: "Rename form" });
    await dialog.getByLabel(/New name/).fill(draftRenamed);
    await dialog.getByRole("button", { name: "Save name" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("heading", { level: 1, name: draftRenamed })).toBeVisible();
    await expect(page.getByRole("status").filter({ hasText: "Form renamed" })).toBeVisible();
    // The unsaved placement is still there and nothing was published.
    await expect(page.getByText("Unsaved changes")).toBeVisible();
    await expect(page.getByText("Not published").first()).toBeVisible();
    // Not signing out here: leaving the designer with unsaved changes is guarded separately (see the status document).
  });
});
