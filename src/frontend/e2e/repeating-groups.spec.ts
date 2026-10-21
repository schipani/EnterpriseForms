import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { formIdFromUrl, openFieldConfig, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * Phase C3, through the real application and API (no mocks):
 *  - designer: a section that allows multiple entries (settings in the section inspector), fields created for each entry,
 *    a dependent dropdown (Country → Province) configured in the Rules tab, Preview;
 *  - clerk: add / edit / reorder / remove entries with stable identities, per-entry validation, dependent choices with
 *    announced clearing when the parent changes, record view, accessibility scans.
 * Server enforcement (forged ids, bounds, settings, tampering, dependent validity, share refusal) is covered by the Stage B
 * integration tests (RepeatingGroupsTests).
 */
const run = Date.now().toString(36);
const canvas = (page: Page) => page.getByRole("region", { name: "Form canvas" });
const section = (page: Page, name: string) => canvas(page).getByRole("region", { name, exact: true });

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

async function chooseOption(page: Page, label: string | RegExp, option: string | RegExp, scope: Page | ReturnType<Page["locator"]> = page) {
  await scope.getByLabel(label).click();
  await page.getByRole("option", { name: option, exact: typeof option === "string" }).click();
}

async function addDropdown(page: Page, name: string, options: string[]) {
  await page.getByRole("tab", { name: /^Fields/ }).click();
  await page.getByRole("button", { name: "Add field", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Add field" });
  await dialog.getByLabel("Display name").fill(name);
  await dialog.getByLabel("Storage type").click();
  await page.getByRole("option", { name: "Dropdown (Single Select)", exact: true }).click();
  await dialog.getByLabel(/Options \(one per line\)/).fill(options.join("\n"));
  await dialog.getByRole("button", { name: "Add field" }).click();
  await expect(dialog).toBeHidden();
}

/** Creates a field for each entry from the repeating section's own "+ Add Field" cell. */
async function addEntryField(page: Page, sectionName: string, name: string, dropdownOptions?: string[]) {
  await page.getByRole("tab", { name: "Design" }).click();
  await section(page, sectionName).getByRole("button", { name: /^Add Field to / }).last().click();
  await page.getByRole("complementary", { name: "Add Field" }).getByRole("button", { name: "New field for each entry…" }).click();
  const dialog = page.getByRole("dialog", { name: "Add field" });
  await expect(dialog.getByText(/allows multiple entries: it has one value per entry/)).toBeVisible();
  await dialog.getByLabel("Display name").fill(name);
  if (dropdownOptions) {
    await dialog.getByLabel("Storage type").click();
    await expect(page.getByRole("option", { name: /File/ })).toHaveCount(0); // no document collections inside entries
    await page.getByRole("option", { name: "Dropdown (Single Select)", exact: true }).click();
    await dialog.getByLabel(/Options \(one per line\)/).fill(dropdownOptions.join("\n"));
  }
  await dialog.getByRole("button", { name: "Add field" }).click();
  await expect(dialog).toBeHidden();
  await expect(section(page, sectionName).getByRole("button", { name, exact: true })).toBeVisible();
}

const entry = (page: Page, index: number, count: number) => page.getByRole("region", { name: `People, entry ${index} of ${count}` });

test.describe.serial("Phase C3: repeating groups and dependent dropdowns", () => {
  const formName = `Case ${run}`;
  let formId = "";
  let recordUrl = "";

  test("designer makes a section allow multiple entries, creates per-entry fields and a dependent dropdown, and publishes", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());

    await addDropdown(page, "Country", ["Canada", "USA"]);
    await addDropdown(page, "Province", ["Ontario", "Quebec", "Texas"]);
    await placeFromPicker(page, "Add field", /^Country/, { width: "2 columns" });
    await placeFromPicker(page, "Add field", /^Province/, { width: "2 columns" });

    // A new section, then "Allow multiple entries" in its Section Properties.
    await page.getByRole("button", { name: "Add Section" }).click();
    const rename = page.getByRole("textbox", { name: "Rename section" });
    await rename.fill("People");
    await rename.press("Enter");
    await canvas(page).getByRole("button", { name: "People", exact: true }).click();
    const sectionInspector = page.getByRole("complementary", { name: "Section Properties" });
    await sectionInspector.getByRole("checkbox", { name: "Allow multiple entries" }).click();
    await expect(section(page, "People").locator("[data-repeat-badge]")).toHaveText("Multiple entries · 0–10");
    await sectionInspector.getByLabel("Maximum entries").fill("3");
    await expect(section(page, "People").locator("[data-repeat-badge]")).toHaveText("Multiple entries · 0–3");
    await expect(sectionInspector.getByRole("checkbox", { name: "People can reorder entries" })).toHaveAttribute("aria-checked", "true");

    await addEntryField(page, "People", "Person name");
    await addEntryField(page, "People", "Role", ["Witness", "Suspect"]);
    let modal = await openFieldConfig(page, "Person name");
    await modal.getByRole("checkbox", { name: "Required" }).click();
    // Entry fields stay in their section: the Section picker (Placement & Alignment tab) offers only that section.
    await modal.getByRole("tab", { name: "Placement & Alignment" }).click();
    await modal.getByRole("combobox", { name: "Section" }).click();
    await expect(page.getByRole("option")).toHaveText(["People"]);
    await page.keyboard.press("Escape");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    // With fields inside, the section cannot stop allowing multiple entries.
    await canvas(page).getByRole("button", { name: "People", exact: true }).click();
    await expect(sectionInspector.getByRole("checkbox", { name: "Allow multiple entries" })).toBeDisabled();

    // Province depends on Country (stable option keys; a choice with nothing ticked is always available).
    modal = await openFieldConfig(page, "Province", "Dropdown Choices");
    const dependent = modal.getByRole("region", { name: "Dependent choices" });
    await dependent.getByRole("checkbox", { name: "Filter the choices by another dropdown" }).click();
    await expect(dependent.getByLabel("Depends on")).toContainText("Country");
    await dependent.getByRole("checkbox", { name: "Ontario when Canada" }).click();
    await dependent.getByRole("checkbox", { name: "Quebec when Canada" }).click();
    await dependent.getByRole("checkbox", { name: "Texas when USA" }).click();
    await expect(dependent.locator('[data-dependent-choice="texas"] legend')).toHaveText("Texas — available when Country is USA");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // Preview: the section starts with no entries; adding one shows its fields; Province follows Country.
    await page.getByRole("tab", { name: "Preview" }).click();
    await expect(page.getByText("0 of up to 3 entries.")).toBeVisible();
    await page.getByRole("button", { name: "Add People" }).click();
    await expect(entry(page, 1, 1).getByLabel(/Person name/)).toBeVisible();
    await chooseOption(page, /^Country/, "Canada");
    await page.getByRole("combobox", { name: /^Province/ }).click();
    await expect(page.getByRole("option")).toHaveText(["Ontario", "Quebec"]);
    await page.keyboard.press("Escape");
    await axeScan(page, "designer preview with a repeating section");

    await saveAndPublish(page, "people entries and dependent province");
    await signOut(page);
  });

  test("clerk adds, edits, reorders and removes entries; dependent choices clear (announced) when the parent changes", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/forms/${formId}/records/new`);
    await expect(page.getByRole("heading", { level: 1, name: `New ${formName}` })).toBeVisible();

    // Dependent dropdown: changing the parent clears an incompatible child and says so.
    await chooseOption(page, /^Country/, "Canada");
    await chooseOption(page, /^Province/, "Ontario");
    await chooseOption(page, /^Country/, "USA");
    await expect(page.getByRole("status").filter({ hasText: "Province cleared" })).toBeVisible();
    await expect(page.getByRole("combobox", { name: /^Province/ })).not.toContainText("Ontario");
    await page.getByRole("combobox", { name: /^Province/ }).click();
    await expect(page.getByRole("option")).toHaveText(["Texas"]);
    await page.getByRole("option", { name: "Texas" }).click();

    // Entries: add two, per-entry required validation names the entry.
    await page.getByRole("button", { name: "Add People" }).click();
    await page.getByRole("button", { name: "Add People" }).click();
    await expect(page.getByRole("status").filter({ hasText: "People 2 added." })).toBeVisible();
    await entry(page, 1, 2).getByLabel(/Person name/).fill("Ada Browser");
    await chooseOption(page, /^Role/, "Witness", entry(page, 1, 2));
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(entry(page, 2, 2).getByRole("alert")).toContainText("Person name is required (entry 2)");
    await entry(page, 2, 2).getByLabel(/Person name/).fill("Bo Browser");
    await axeScan(page, "record form with entries");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(entry(page, 1, 2)).toContainText("Ada Browser");
    await expect(entry(page, 2, 2)).toContainText("Bo Browser");
    await expect(page.getByText("Texas", { exact: true })).toBeVisible();
    recordUrl = page.url();
    const recordId = /\/app\/records\/([0-9a-f-]{36})/.exec(recordUrl)![1]!;
    const idsOf = async () => {
      const response = await page.request.get(`/api/v1/form-records/${recordId}/projection`);
      expect(response.ok()).toBeTruthy();
      const body = (await response.json()) as { record: { groups: Array<{ groupKey: string; occurrences: Array<{ formRecordGroupId: string }> }> } };
      return body.record.groups[0]!.occurrences.map((o) => o.formRecordGroupId);
    };
    const [adaId, boId] = await idsOf();

    // Reorder: identities are kept, only the order changes.
    await page.getByRole("link", { name: "Edit" }).first().click();
    await page.getByRole("button", { name: "Move up, People 2" }).click();
    await expect(entry(page, 1, 2).getByLabel(/Person name/)).toHaveValue("Bo Browser");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Revision 2")).toBeVisible();
    await expect(entry(page, 1, 2)).toContainText("Bo Browser");
    expect(await idsOf()).toEqual([boId, adaId]);

    // Edit one entry and remove the other; a third entry gets a fresh identity.
    await page.getByRole("link", { name: "Edit" }).first().click();
    await entry(page, 2, 2).getByLabel(/Person name/).fill("Ada Edited");
    await page.getByRole("button", { name: "Remove People 1" }).click();
    await expect(page.getByRole("status").filter({ hasText: "People 1 removed." })).toBeVisible();
    await page.getByRole("button", { name: "Add People" }).click();
    await entry(page, 2, 2).getByLabel(/Person name/).fill("Cy Browser");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Revision 3")).toBeVisible();
    await expect(entry(page, 1, 2)).toContainText("Ada Edited");
    await expect(entry(page, 2, 2)).toContainText("Cy Browser");
    const finalIds = await idsOf();
    expect(finalIds[0]).toBe(adaId);
    expect(finalIds).not.toContain(boId);
    await axeScan(page, "record view with entries");

    // The share dialog does not offer entry fields on their own.
    await page.getByRole("tab", { name: "Access" }).click();
    await page.getByRole("button", { name: "Share fields" }).click();
    const share = page.getByRole("dialog", { name: "Share fields of this record" });
    await expect(share.getByRole("checkbox", { name: "Person name" })).toHaveCount(0);
    await expect(share.getByText("Fields in sections that allow multiple entries cannot be shared on their own.")).toBeVisible();
    await share.getByRole("button", { name: "Cancel" }).click();
    await signOut(page);
  });
});
