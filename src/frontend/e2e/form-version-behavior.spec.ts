import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { addField, formIdFromUrl, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * Phase C9 M1: record layout (version behaviour) as people use it. The designer switches a published form to "Show
 * original, edit with current" (Hybrid) after seeing an impact that counts only records they can see; the clerk's record
 * is then shown with the version it was created with and edited with the current one; when a newer version is published
 * while the clerk is editing, the save is refused and the clerk reloads onto the new layout, keeping their changes.
 */
const run = Date.now().toString(36);
const formName = `Layout intake ${run}`;
let formId = "";
let recordUrl = "";

const axe = (page: import("@playwright/test").Page, scope: string) => new AxeBuilder({ page }).include(scope).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();

test.describe.serial("Form version behaviour", () => {
  test("designer publishes v1, the clerk creates a record, the designer publishes v2 with a new field", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());
    await addField(page, "Given name", "String");
    await placeFromPicker(page, "Add field", /Given name/);
    await saveAndPublish(page, "layout v1");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    await page.getByLabel(/Given name/).fill("Casey");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("Layout v1", { exact: true })).toBeVisible();
    recordUrl = page.url();
    await signOut(page);

    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await addField(page, "Alias", "String");
    await placeFromPicker(page, "Add field", /Alias/);
    await saveAndPublish(page, "layout v2: alias");
    await expect(page.getByText("Published v2")).toBeVisible();
    await signOut(page);
  });

  test("designer changes the record layout after reviewing an impact limited to records they can see", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tablist", { name: "Form designer" }).getByRole("tab", { name: /^Settings/ }).click();
    await expect(page.getByText("Current published version", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Change record layout" }).click();

    const dialog = page.getByRole("dialog", { name: "Record layout" });
    await expect(dialog.getByRole("radio", { name: /Current published version \(current setting\)/ })).toBeChecked();
    await expect(dialog.getByRole("button", { name: "Change record layout" })).toBeDisabled();
    await dialog.getByRole("radio", { name: /Show original, edit with current/ }).check();
    // The designer has no access to the clerk's record, so nothing is counted for them.
    await expect(dialog.getByText("You can't see any records of this form.")).toBeVisible();
    await expect(dialog.getByText(/Counts cover only records you can discover/)).toBeVisible();
    await dialog.getByRole("button", { name: "Change record layout" }).click();
    await expect(dialog.getByText(/at least 3 characters/)).toBeVisible();
    expect((await axe(page, '[role="dialog"]')).violations).toEqual([]);
    await dialog.getByLabel(/Reason for the change/).fill("edit older records with the alias field");
    await dialog.getByRole("button", { name: "Change record layout" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("status").filter({ hasText: "Record layout changed" })).toBeVisible();
    await expect(page.getByText("Show original, edit with current", { exact: true })).toBeVisible();
    // Still two published versions: changing the setting publishes nothing.
    await expect(page.getByText("Published v2")).toBeVisible();
    await signOut(page);
  });

  test("the clerk's record is shown with v1, edited with v2, and a newer version during editing asks for a reload", async ({ page, browser }) => {
    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await expect(page.getByText("Layout v1", { exact: true })).toBeVisible();
    await expect(page.getByText("Shown with layout v1; edits use layout v2")).toBeVisible();
    expect((await axe(page, "main")).violations).toEqual([]);

    await page.getByRole("link", { name: "Edit" }).click();
    await expect(page.getByText(/with layout v2\./)).toBeVisible();
    await expect(page.getByText("Editing with the current layout v2")).toBeVisible();
    await page.getByLabel(/^Alias/).fill("Cee");

    // Meanwhile the designer publishes v3.
    const designerContext = await browser.newContext();
    const designer = await designerContext.newPage();
    await signIn(designer, "designer");
    await designer.goto(`/app/forms/${formId}/design`);
    await addField(designer, "Nickname", "String");
    await placeFromPicker(designer, "Add field", /Nickname/);
    await saveAndPublish(designer, "layout v3: nickname");
    await expect(designer.getByText("Published v3")).toBeVisible();
    await signOut(designer);
    await designerContext.close();

    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("The form layout changed while you were editing")).toBeVisible();
    await expect(page.getByText("Someone else saved this record while you were editing")).toHaveCount(0);
    expect((await axe(page, "main")).violations).toEqual([]);
    await page.getByRole("button", { name: "Reload with the new layout and keep my changes" }).click();
    await expect(page.getByText(/with layout v3\./)).toBeVisible();
    await expect(page.getByLabel(/^Alias/)).toHaveValue("Cee");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("Layout v1", { exact: true })).toBeVisible();
    await expect(page.getByText("Shown with layout v1; edits use layout v3")).toBeVisible();
    await signOut(page);
  });

  test("a change to a field the new layout removed is reported with its value, not silently dropped", async ({ page, browser }) => {
    await signIn(page, "clerk");
    await page.goto(recordUrl + "/edit");
    await expect(page.getByText(/with layout v3\./)).toBeVisible();
    await page.getByLabel(/^Given name/).fill("Casey C");
    await page.getByLabel(/^Nickname/).fill("Kit");

    // Meanwhile the designer removes Nickname from the layout and publishes v4.
    const designerContext = await browser.newContext();
    const designer = await designerContext.newPage();
    await signIn(designer, "designer");
    await designer.goto(`/app/forms/${formId}/design`);
    await designer.getByRole("region", { name: "Form canvas" }).getByRole("button", { name: "Nickname", exact: true }).click();
    await designer.getByRole("button", { name: "More actions for Nickname" }).click();
    await designer.getByRole("menuitem", { name: "Remove from layout" }).click();
    await saveAndPublish(designer, "layout v4: nickname removed");
    await expect(designer.getByText("Published v4")).toBeVisible();
    await signOut(designer);
    await designerContext.close();

    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("The form layout changed while you were editing")).toBeVisible();
    await page.getByRole("button", { name: "Reload with the new layout and keep my changes" }).click();
    await expect(page.getByText(/with layout v4\./)).toBeVisible();
    await expect(page.getByText(/Some of your changes can.t be carried over/)).toBeVisible();
    await expect(page.getByRole("list", { name: "Changes not carried over" })).toContainText("Nickname: you had entered");
    await expect(page.getByRole("list", { name: "Changes not carried over" })).toContainText("Kit");
    await expect(page.getByLabel(/^Nickname/)).toHaveCount(0);
    await expect(page.getByLabel(/^Given name/)).toHaveValue("Casey C");
    expect((await axe(page, "main")).violations).toEqual([]);
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 60_000 });
    await signOut(page);
  });
});
