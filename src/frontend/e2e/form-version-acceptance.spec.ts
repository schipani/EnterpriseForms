import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { addField, formIdFromUrl, placeFromPicker, publish, saveAndPublish, signIn, signOut } from "./support";

/**
 * Phase C9 M3 (WP-7): the consolidated version-management workflow as people see it (CLAUDE.md §11 #1–#6). The designer
 * publishes v1 (first name, last name, phone); the clerk records a phone number; v2 removes phone and adds email after a
 * publish review; v3 restores v1 from the Versions tab; v4 places the original email field again from the existing-field
 * picker and the comparison shows exactly that; the designer switches the record layout to Hybrid after the impact
 * preview; the clerk's record is shown with v1 (the phone value is still there) and edited with v4; a version published
 * during editing asks for a reload that keeps permitted changes and lists the change it cannot carry over. Accessibility is
 * checked with axe on every new surface.
 */
const run = Date.now().toString(36);
const formName = `Acceptance intake ${run}`;
let formId = "";
let recordUrl = "";

const axe = (page: Page, scope: string) => new AxeBuilder({ page }).include(scope).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();

async function openDesigner(page: Page, tab?: RegExp) {
  await page.goto(`/app/forms/${formId}/design`);
  await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
  if (tab) {
    await page.getByRole("tablist", { name: "Form designer" }).getByRole("tab", { name: tab }).click();
  }
}

async function removeFromLayout(page: Page, label: string) {
  await page.getByRole("tab", { name: "Design" }).click();
  await page.getByRole("region", { name: "Form canvas" }).getByRole("button", { name: label, exact: true }).click();
  await page.getByRole("button", { name: `More actions for ${label}` }).click();
  await page.getByRole("menuitem", { name: "Remove from layout" }).click();
}

/** The row of the Versions table whose row header is exactly `label` (for example "v1"). */
function versionRow(page: Page, label: string) {
  return page.getByRole("table", { name: "Versions" }).getByRole("row").filter({ has: page.getByRole("rowheader", { name: label, exact: true }) });
}

async function compare(page: Page, from: string, to: string) {
  await page.getByRole("combobox", { name: "Compare from" }).click();
  await page.getByRole("option", { name: from, exact: true }).click();
  await page.getByRole("combobox", { name: "to", exact: true }).click();
  await page.getByRole("option", { name: to, exact: true }).click();
  await expect(page.getByText(new RegExp(`${from} → ${to}:`))).toBeVisible();
}

test.describe.serial("Form version acceptance (C9 M3)", () => {
  test("v1: the designer publishes first name, last name and phone; the clerk records a phone number", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());
    for (const label of ["First name", "Last name", "Phone"]) {
      await addField(page, label, "String");
      await placeFromPicker(page, "Add field", new RegExp(label));
    }
    await saveAndPublish(page, "acceptance v1");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    await page.getByLabel(/^First name/).fill("Avery");
    await page.getByLabel(/^Last name/).fill("Synthetic");
    await page.getByLabel(/^Phone/).fill("555-0101");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("Layout v1", { exact: true })).toBeVisible();
    await expect(page.getByText("555-0101")).toBeVisible();
    recordUrl = page.url();
    await signOut(page);
  });

  test("v2: the publish review shows email added and phone removed, and that stored values are kept", async ({ page }) => {
    await signIn(page, "designer");
    await openDesigner(page);
    await addField(page, "Email", "String");
    await placeFromPicker(page, "Add field", /Email/);
    await removeFromLayout(page, "Phone");
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await page.getByRole("button", { name: /^Publish/ }).click();
    const dialog = page.getByRole("dialog", { name: "Publish a new version" });
    await expect(dialog.getByText("Stored values are never changed by publishing")).toBeVisible();
    await expect(dialog.getByText("Changes from v1 to v2")).toBeVisible();
    await expect(dialog.getByRole("list", { name: "Added" })).toContainText("Email");
    await expect(dialog.getByRole("list", { name: "Removed from the layout" })).toContainText("Phone");
    await expect(dialog.getByText(/Stored values are kept/).first()).toBeVisible();
    await expect(dialog.getByText("You can't see any records of this form.")).toBeVisible();
    expect((await axe(page, '[role="dialog"]')).violations).toEqual([]);
    await dialog.getByLabel("Change reason").fill("acceptance v2: email added, phone removed");
    await dialog.getByRole("button", { name: "Publish", exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByText("Published v2")).toBeVisible();
    await signOut(page);

    // Latest (the default): the record is shown with v2; the phone value is kept but v2 does not place it.
    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await expect(page.getByText("Layout v2 (created with v1)", { exact: true })).toBeVisible();
    await expect(page.getByText("Avery")).toBeVisible();
    await expect(page.getByText("555-0101")).toHaveCount(0);
    expect((await axe(page, "main")).violations).toEqual([]);
    await signOut(page);
  });

  test("v3 restores v1 from the Versions tab; v4 places the original email field again", async ({ page }) => {
    await signIn(page, "designer");
    await openDesigner(page, /^Versions/);
    await versionRow(page, "v1").getByRole("button", { name: "Restore to draft" }).click();
    // The draft is now v1's layout: the default comparison (current version → draft) shows phone back and email out.
    await expect(page.getByText(/v2 → Draft \(unpublished, revision \d+\):/)).toBeVisible();
    await expect(page.getByRole("list", { name: "Added" })).toContainText("Phone");
    await expect(page.getByRole("list", { name: "Removed from the layout" })).toContainText("Email");
    await publish(page, "acceptance v3: restore the original layout");
    await expect(page.getByText("Published v3")).toBeVisible();
    await openDesigner(page, /^Versions/);
    await expect(versionRow(page, "v3").getByRole("cell", { name: "v1", exact: true })).toBeVisible(); // "Restored from"
    await compare(page, "v1", "v3");
    await expect(page.getByText("No differences.")).toBeVisible();

    // The original email field is placed from the existing-field picker: no new field is created.
    await openDesigner(page);
    await placeFromPicker(page, "Add field", /Email/);
    await saveAndPublish(page, "acceptance v4: original email field re-added");
    await expect(page.getByText("Published v4")).toBeVisible();
    await page.getByRole("tab", { name: /^Fields/ }).click();
    await expect(page.getByRole("rowheader", { name: "Email", exact: true })).toHaveCount(1);

    await openDesigner(page, /^Versions/);
    await compare(page, "v3", "v4");
    await expect(page.getByText("1 added.")).toBeVisible();
    await expect(page.getByRole("list", { name: "Added" })).toContainText("Email");
    await expect(page.getByRole("list", { name: "Removed from the layout" })).toHaveCount(0);
    expect((await axe(page, "main")).violations).toEqual([]);
    await signOut(page);
  });

  test("the designer reviews the impact and switches the record layout to Hybrid", async ({ page }) => {
    await signIn(page, "designer");
    await openDesigner(page, /^Settings/);
    await page.getByRole("button", { name: "Change record layout" }).click();
    const dialog = page.getByRole("dialog", { name: "Record layout" });
    // Each option explains itself in plain language.
    await expect(dialog.getByRole("radio", { name: /Current published version \(current setting\)/ })).toBeChecked();
    await expect(dialog.getByRole("radio", { name: /Version a record was created with/ })).toBeVisible();
    await expect(dialog.getByText(/Newer versions apply to new records only/)).toBeVisible();
    await dialog.getByRole("radio", { name: /Show original, edit with current/ }).check();
    await expect(dialog.getByText("You can't see any records of this form.")).toBeVisible();
    await expect(dialog.getByText(/Counts cover only records you can discover/)).toBeVisible();
    expect((await axe(page, '[role="dialog"]')).violations).toEqual([]);
    await dialog.getByLabel(/Reason for the change/).fill("show the original, edit with the current version");
    await dialog.getByRole("button", { name: "Change record layout" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("status").filter({ hasText: "Record layout changed" })).toBeVisible();
    await expect(page.getByText("Show original, edit with current", { exact: true })).toBeVisible();
    await signOut(page);
  });

  test("the clerk's record is shown with v1 and edited with v4; a version published meanwhile asks for a reload", async ({ page, browser }) => {
    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await expect(page.getByText("Layout v1", { exact: true })).toBeVisible();
    await expect(page.getByText("Shown with layout v1; edits use layout v4")).toBeVisible();
    await expect(page.getByText("555-0101")).toBeVisible(); // shown again with v1: the value was never changed
    expect((await axe(page, "main")).violations).toEqual([]);

    await page.getByRole("link", { name: "Edit" }).click();
    await expect(page.getByText("Editing with the current layout v4")).toBeVisible();
    await page.getByLabel(/^Email/).fill("avery@example.test");
    await page.getByLabel(/^Phone/).fill("555-0199");

    // Meanwhile the designer removes phone again and publishes v5.
    const designerContext = await browser.newContext();
    const designer = await designerContext.newPage();
    await signIn(designer, "designer");
    await designer.goto(`/app/forms/${formId}/design`);
    await expect(designer.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    await removeFromLayout(designer, "Phone");
    await saveAndPublish(designer, "acceptance v5: phone removed again");
    await expect(designer.getByText("Published v5")).toBeVisible();
    await signOut(designer);
    await designerContext.close();

    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("The form layout changed while you were editing")).toBeVisible();
    await expect(page.getByText("Someone else saved this record while you were editing")).toHaveCount(0);
    expect((await axe(page, "main")).violations).toEqual([]);
    await page.getByRole("button", { name: "Reload with the new layout and keep my changes" }).click();
    await expect(page.getByText(/with layout v5\./)).toBeVisible();
    await expect(page.getByLabel(/^Email/)).toHaveValue("avery@example.test");
    await expect(page.getByText(/Some of your changes can.t be carried over/)).toBeVisible();
    const withheld = page.getByRole("list", { name: "Changes not carried over" });
    await expect(withheld).toContainText("Phone: you had entered");
    await expect(withheld).toContainText("555-0199");
    await expect(page.getByLabel(/^Phone/)).toHaveCount(0);
    expect((await axe(page, "main")).violations).toEqual([]);
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 60_000 });

    // The saved record: still shown with v1, edits now use v5, the withheld phone change was not applied.
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("Layout v1", { exact: true })).toBeVisible();
    await expect(page.getByText("Shown with layout v1; edits use layout v5")).toBeVisible();
    await expect(page.getByText("555-0101")).toBeVisible();
    await expect(page.getByText("555-0199")).toHaveCount(0);
    await signOut(page);
  });
});
