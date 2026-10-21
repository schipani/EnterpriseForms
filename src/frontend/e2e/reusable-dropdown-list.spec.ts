import { expect, test, type Page } from "@playwright/test";
import { formIdFromUrl, openFieldConfig, placeFromPicker, saveAndPublish, selectTile, signIn, signOut } from "./support";

/**
 * Reusable dropdown list workflow, exactly as a user does it in the browser:
 * create the option list "Marital Status" → open a form in the designer → Add field → "Dropdown (Single Select)" /
 * "Dropdown (Multi Select)" with "Use Existing List" → place it → Preview → publish → create a record → save, reopen and
 * edit it with the selection retained. No API shortcuts. The list name carries a run suffix because list names are
 * unique per organization and the development organization is shared between runs.
 */
const run = Date.now().toString(36);
const listName = `Marital Status ${run}`;
const formName = `Census ${run}`;
const choices = ["Single", "Married", "Divorced", "Widowed"];

async function pick(page: Page, label: string | RegExp, option: string | RegExp) {
  await page.getByLabel(label).click();
  await page.getByRole("option", { name: option, exact: typeof option === "string" }).click();
}

test.describe.serial("Reusable option list → dropdown field → record", () => {
  let formId = "";
  let recordUrl = "";

  test("designer creates the list, adds single- and multi-select dropdown fields from it, previews and publishes", async ({ page }) => {
    await signIn(page, "designer");

    // 1–2. Create the reusable list with four choices.
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());

    await page.getByRole("tab", { name: /^Fields/ }).click();
    await page.getByRole("button", { name: "Option lists", exact: true }).click();
    const lists = page.getByRole("dialog", { name: "Shared option lists" });
    await lists.getByRole("button", { name: "New list…" }).click();
    await lists.getByLabel("List name").fill(listName);
    await lists.getByLabel(/Options \(one per line\)/).fill(choices.join("\n"));
    await lists.getByRole("button", { name: "Create list" }).click();
    await expect(lists.getByRole("region", { name: `Edit ${listName}` })).toBeVisible();
    await expect(lists.getByRole("listitem")).toHaveCount(4);
    await lists.getByRole("button", { name: "Close" }).click();
    await expect(lists).toBeHidden();

    // 3–6. Add Field offers understandable dropdown types and "Use Existing List".
    for (const [name, type] of [["Marital status", "Dropdown (Single Select)"], ["Previous statuses", "Dropdown (Multi Select)"]] as const) {
      await page.getByRole("button", { name: "Add field", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Add field" });
      await dialog.getByLabel("Display name").fill(name);
      await dialog.getByLabel("Storage type").click();
      await expect(page.getByRole("option", { name: /^Option$/ })).toHaveCount(0); // the internal storage name is not the user-facing label
      await page.getByRole("option", { name: type, exact: true }).click();
      await dialog.getByLabel("Choices").click();
      await page.getByRole("option", { name: "Use Existing List", exact: true }).click();
      await dialog.getByLabel("Existing list").click();
      await page.getByRole("option", { name: new RegExp(listName) }).click();
      await dialog.getByRole("button", { name: "Add field" }).click();
      await expect(dialog).toBeHidden();
      await expect(page.getByRole("rowheader", { name })).toBeVisible();
    }
    await expect(page.getByRole("row", { name: /Marital status/ })).toContainText("Dropdown (Single Select)");
    await expect(page.getByRole("row", { name: /Previous statuses/ })).toContainText("Dropdown (Multi Select)");

    // Place both on the canvas; the inspector shows the four copied choices.
    await placeFromPicker(page, "Add field", /^Marital status/, { width: "2 columns" });
    await placeFromPicker(page, "Add field", /^Previous statuses/, { width: "2 columns" });
    const inspector = await selectTile(page, "Marital status");
    await expect(inspector.getByLabel("Field type")).toHaveValue("Dropdown (Single Select)");
    const modal = await openFieldConfig(page, "Marital status", "Dropdown Choices");
    const region = modal.getByRole("region", { name: "Options of Marital status" });
    await expect(region.getByRole("listitem")).toHaveCount(4);
    await expect(region.getByText("Shared list")).toBeVisible();
    await modal.getByRole("button", { name: "Cancel" }).click();
    await expect(modal).toBeHidden();

    // 7. Preview: pick a value.
    await page.getByRole("tab", { name: "Preview" }).click();
    await pick(page, /Marital status/, "Married");
    await expect(page.getByRole("combobox", { name: /Marital status/ })).toContainText("Married");
    await page.getByRole("checkbox", { name: "Single" }).click();
    await expect(page.getByRole("checkbox", { name: "Single" })).toHaveAttribute("aria-checked", "true");

    // 8. Publish.
    await saveAndPublish(page, "dropdowns from a shared list");
    await signOut(page);
  });

  test("clerk creates a record, saves it, reopens it and edits it with the selections retained", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/forms/${formId}/records/new`);
    await expect(page.getByRole("heading", { level: 1, name: `New ${formName}` })).toBeVisible();

    // 9. Choose values from the list.
    await page.getByLabel(/Marital status/).click();
    expect(await page.getByRole("option").allTextContents()).toEqual(choices);
    await page.getByRole("option", { name: "Married", exact: true }).click();
    await page.getByRole("checkbox", { name: "Single" }).click();
    await page.getByRole("checkbox", { name: "Divorced" }).click();
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 90_000 });
    recordUrl = page.url();
    await expect(page.getByText("Married", { exact: true })).toBeVisible();
    await expect(page.getByText("Single, Divorced")).toBeVisible();

    // 10. Reopen and edit: the stored selections are shown and retained.
    await page.reload();
    await expect(page.getByText("Married", { exact: true })).toBeVisible();
    await page.getByRole("link", { name: "Edit" }).first().click();
    await expect(page.getByRole("combobox", { name: /Marital status/ })).toContainText("Married");
    await expect(page.getByRole("checkbox", { name: "Single" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("checkbox", { name: "Divorced" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("checkbox", { name: "Widowed" })).toHaveAttribute("aria-checked", "false");
    await pick(page, /Marital status/, "Widowed");
    await page.getByRole("checkbox", { name: "Divorced" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Revision 2")).toBeVisible();
    await expect(page.getByText("Widowed", { exact: true })).toBeVisible();
    await expect(page.getByText("Single", { exact: true })).toBeVisible();

    await page.goto(recordUrl);
    await page.getByRole("link", { name: "Edit" }).first().click();
    await expect(page.getByRole("combobox", { name: /Marital status/ })).toContainText("Widowed");
    await expect(page.getByRole("checkbox", { name: "Single" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("checkbox", { name: "Divorced" })).toHaveAttribute("aria-checked", "false");
  });
});
