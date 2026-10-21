import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { addField, formIdFromUrl, openFieldConfig, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * Phase C2, through the real application and API (no mocks):
 *  - dropdown + multi-select: add/relabel/reorder/deactivate options in the inspector, defaults, required, Preview,
 *    record create / edit / view, deactivation that never corrupts a stored record;
 *  - conditional visibility, conditional required, Show in form, Read-only: rule builder in the Conditional rules tab, "try it"
 *    preview, canvas indicators, cycle prevention, Preview, record create / edit, retention of hidden values.
 * The server enforcement behind each behavior is covered by the Stage B integration tests (DropdownTests, RulesTests).
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

async function addOptionField(page: Page, name: string, options: string[], mode: "Single" | "Multiple" = "Single") {
  await page.getByRole("tab", { name: /^Fields/ }).click();
  await page.getByRole("button", { name: "Add field", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Add field" });
  await dialog.getByLabel("Display name").fill(name);
  await dialog.getByLabel("Storage type").click();
  await page.getByRole("option", { name: mode === "Multiple" ? "Dropdown (Multi Select)" : "Dropdown (Single Select)", exact: true }).click();
  await dialog.getByLabel(/Options \(one per line\)/).fill(options.join("\n"));
  await dialog.getByRole("button", { name: "Add field" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("rowheader", { name })).toBeVisible();
}

// Options editing lives on the "Dropdown choices and dependent choices" tab of the Configure Field modal (Phase C3
// designer UX refinement); callers pass the modal dialog locator from openFieldConfig.
const optionsRegion = (modal: Locator, field: string) => modal.getByRole("region", { name: `Options of ${field}` });
const optionRow = (modal: Locator, field: string, key: string) => optionsRegion(modal, field).locator(`[data-option-key="${key}"]`);

async function chooseOption(page: Page, label: string | RegExp, option: string | RegExp) {
  await page.getByLabel(label).click();
  await page.getByRole("option", { name: option, exact: typeof option === "string" }).click();
}

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

test.describe.serial("Phase C2 priority 1: dropdowns and multi-select", () => {
  const formName = `Choices ${run}`;
  let formId = "";
  let recordUrl = "";

  test("designer adds a dropdown and a multi-select, manages their options in the inspector, sets defaults and publishes", async ({ page }) => {
    await signIn(page, "designer");
    formId = await createForm(page, formName);
    await addOptionField(page, "Priority", ["Low", "Medium", "High"]);
    await addOptionField(page, "Topics", ["Red", "Green", "Blue"], "Multiple");
    await placeFromPicker(page, "Add field", /^Priority/, { width: "2 columns" });
    await placeFromPicker(page, "Add field", /^Topics/, { width: "2 columns" });

    // Dropdown options: add, reorder, relabel, default — all in Configure Field's "Dropdown choices and dependent
    // choices" tab; Required is a Validation-tab setting (not duplicated here).
    let modal = await openFieldConfig(page, "Priority", "Dropdown choices and dependent choices");
    await expect(optionsRegion(modal, "Priority").getByText("Choose one")).toBeVisible();
    await expect(optionsRegion(modal, "Priority").getByRole("listitem")).toHaveCount(3);
    await optionsRegion(modal, "Priority").getByLabel("New option").fill("Urgent");
    await optionsRegion(modal, "Priority").getByRole("button", { name: "Add option" }).click();
    await expect(optionsRegion(modal, "Priority").getByRole("listitem")).toHaveCount(4);
    await optionsRegion(modal, "Priority").getByRole("button", { name: "Move Urgent earlier" }).click(); // Low, Medium, Urgent, High
    await optionRow(modal, "Priority", "high").getByLabel(/Label of option/).fill("High (escalate)");
    await optionsRegion(modal, "Priority").getByRole("button", { name: "Save options" }).click();
    await expect(optionsRegion(modal, "Priority").getByText(/Options saved/)).toBeVisible();
    await expect(optionsRegion(modal, "Priority").getByLabel("Label of option 3")).toHaveValue("Urgent");
    await expect(optionsRegion(modal, "Priority").getByLabel("Label of option 4")).toHaveValue("High (escalate)");
    await optionRow(modal, "Priority", "medium").getByRole("checkbox", { name: "Default" }).click();
    await modal.getByRole("tab", { name: "Validation" }).click();
    await modal.getByRole("checkbox", { name: "Required" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // Multi-select: the permanent selection mode is shown, two defaults, option identities listed.
    modal = await openFieldConfig(page, "Topics", "Dropdown choices and dependent choices");
    await expect(optionsRegion(modal, "Topics").getByText("Multi-select")).toBeVisible();
    await optionRow(modal, "Topics", "red").getByRole("checkbox", { name: "Default" }).click();
    await optionRow(modal, "Topics", "green").getByRole("checkbox", { name: "Default" }).click();
    await expect(optionRow(modal, "Topics", "blue").getByText("blue", { exact: true })).toBeVisible();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // Preview renders the same controls the record form uses, with the defaults applied.
    await page.getByRole("tab", { name: "Preview" }).click();
    await expect(page.getByRole("combobox", { name: /Priority/ })).toContainText("Medium");
    await expect(page.getByRole("checkbox", { name: "Red" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("checkbox", { name: "Green" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("checkbox", { name: "Blue" })).toHaveAttribute("aria-checked", "false");
    await axeScan(page, "designer preview with dropdown and multi-select");

    await saveAndPublish(page, "dropdown and multi-select");
    await signOut(page);
  });

  test("clerk creates, edits and views a record: defaults, server-validated selections, typed values round-trip", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/forms/${formId}/records/new`);
    await expect(page.getByRole("heading", { level: 1, name: `New ${formName}` })).toBeVisible();
    await expect(page.getByRole("combobox", { name: /Priority/ })).toContainText("Medium"); // default selection
    await expect(page.getByRole("checkbox", { name: "Red" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("checkbox", { name: "Green" })).toHaveAttribute("aria-checked", "true");
    await axeScan(page, "record form with dropdown and multi-select");

    // The options reflect the published snapshot and order; the new label is used.
    await page.getByRole("combobox", { name: /Priority/ }).click();
    expect(await page.getByRole("option").allTextContents()).toEqual(["Low", "Medium", "Urgent", "High (escalate)"]);
    await page.getByRole("option", { name: "High (escalate)" }).click();
    await page.getByRole("checkbox", { name: "Blue" }).click();
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("High (escalate)")).toBeVisible();
    await expect(page.getByText("Red, Green, Blue")).toBeVisible();
    recordUrl = page.url();

    // The selection survives a reload (typed rows read back through the verified projection).
    await page.reload();
    await expect(page.getByText("Red, Green, Blue")).toBeVisible();

    // Edit: dropping one selection changes only that option.
    await page.getByRole("link", { name: "Edit" }).first().click();
    await page.getByRole("checkbox", { name: "Green" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Revision 2")).toBeVisible();
    await expect(page.getByText("Red, Blue", { exact: true })).toBeVisible();

    // A required dropdown offers no "clear selection" (it cannot be left empty).
    await page.getByRole("link", { name: "Edit" }).first().click();
    await expect(page.getByRole("button", { name: /^Clear Priority selection$/ })).toHaveCount(0);
    await signOut(page);
  });

  test("deactivating an option never rewrites a stored record: it stays visible, marked, and blocks a save until replaced", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(page.getByRole("tab", { name: "Design" })).toBeVisible();
    const deactivateModal = await openFieldConfig(page, "Topics", "Dropdown choices and dependent choices");
    await optionRow(deactivateModal, "Topics", "blue").getByRole("checkbox", { name: "Active" }).click();
    await optionsRegion(deactivateModal, "Topics").getByRole("button", { name: "Save options" }).click();
    await expect(optionsRegion(deactivateModal, "Topics").getByText(/Options saved/)).toBeVisible();
    await deactivateModal.getByRole("button", { name: "Cancel" }).click(); // Save Options already persisted; nothing else here needs applying
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await expect(page.getByText("Red, Blue (no longer available)")).toBeVisible(); // historical value intact and marked
    await page.getByRole("link", { name: "Edit" }).first().click();
    await expect(page.getByRole("checkbox", { name: "Blue (no longer available)" })).toHaveAttribute("aria-checked", "true");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByRole("alert").first()).toContainText(/no longer available/);
    await expect(page).toHaveURL(/\/edit$/); // nothing was saved
    await page.getByRole("checkbox", { name: "Blue (no longer available)" }).click(); // replace it (here: remove it)
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Revision 3")).toBeVisible();
    await expect(page.getByText("Red", { exact: true })).toBeVisible();
    await signOut(page);
  });
});

test.describe.serial("Phase C2: conditional rules, Show in form and Read-only", () => {
  const formName = `Rules ${run}`;
  let formId = "";
  let recordUrl = "";

  test("designer builds visibility, required-if, read-only and not-in-form behavior with the rule builder and tries it with sample values", async ({ page }) => {
    await signIn(page, "designer");
    formId = await createForm(page, formName);
    await addOptionField(page, "Applicant kind", ["Person", "Company"]);
    await addField(page, "Company name", "String");
    await addField(page, "Applicant age", "Integer");
    await addField(page, "Guardian name", "String");
    await addOptionField(page, "Intake status", ["Draft", "Final"]);
    await addField(page, "Reference note", "String");
    for (const name of [/^Applicant kind/, /^Company name/, /^Applicant age/, /^Guardian name/, /^Intake status/, /^Reference note/]) {
      await placeFromPicker(page, "Add field", name, { width: "2 columns" });
    }

    // Applicant kind is required. Company name: shown only for companies, and required whenever it is shown.
    let modal = await openFieldConfig(page, "Applicant kind");
    await modal.getByRole("checkbox", { name: "Required" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    modal = await openFieldConfig(page, "Company name");
    await modal.getByRole("checkbox", { name: "Required" }).click();
    await modal.getByRole("tab", { name: "Conditional rules" }).click();
    await modal.getByRole("checkbox", { name: "Show this field only when conditions are met" }).click();
    await chooseOption(page, "Visibility condition 1.1 field", "Applicant kind");
    await chooseOption(page, "Visibility condition 1.1 operator", "is");
    await chooseOption(page, "Visibility condition 1.1 value", "Company");
    await expect(modal.getByTestId("visibility-summary")).toHaveText("Applicant kind is Company");

    // Try it: synthetic values show the effect without saving anything.
    await chooseOption(page, "Sample Applicant kind", "Person");
    await expect(modal.getByTestId("rule-preview-result")).toHaveText("With these values this field is hidden and optional.");
    await chooseOption(page, "Sample Applicant kind", "Company");
    await expect(modal.getByTestId("rule-preview-result")).toHaveText("With these values this field is shown and required.");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // Guardian name: required when the age is under 18. The operator list offers number operators only.
    modal = await openFieldConfig(page, "Guardian name", "Conditional rules");
    await modal.getByRole("checkbox", { name: "Make this field required when conditions are met" }).click();
    await chooseOption(page, "Required condition 1.1 field", "Applicant age");
    await page.getByLabel("Required condition 1.1 operator").click();
    await expect(page.getByRole("option", { name: "contains" })).toHaveCount(0);
    await page.getByRole("option", { name: "is less than" }).click();
    await modal.getByLabel("Required condition 1.1 value").fill("abc");
    await expect(modal.getByText("Enter a whole number")).toBeVisible();
    await modal.getByLabel("Required condition 1.1 value").fill("18");
    await modal.getByLabel("Sample Applicant age").fill("15");
    await expect(modal.getByTestId("rule-preview-result")).toHaveText("With these values this field is shown and required.");
    await modal.getByLabel("Sample Applicant age").fill("30");
    await expect(modal.getByTestId("rule-preview-result")).toHaveText("With these values this field is shown and optional.");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // Intake status: read-only with a default of Draft. Reference note: not shown in the form.
    modal = await openFieldConfig(page, "Intake status", "Dropdown choices and dependent choices");
    await optionRow(modal, "Intake status", "draft").getByRole("checkbox", { name: "Default" }).click();
    await modal.getByRole("tab", { name: "Conditional rules" }).click();
    await modal.getByRole("checkbox", { name: "Read-only" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    modal = await openFieldConfig(page, "Reference note", "Conditional rules");
    await modal.getByRole("checkbox", { name: "Show in form" }).click();
    await expect(modal.getByRole("checkbox", { name: "Show in form" })).toHaveAttribute("aria-checked", "false");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // A rule can never form a loop: fields that already depend on Applicant kind are not offered as its source.
    // (An experiment, discarded with Cancel: nothing here should end up on the draft.)
    modal = await openFieldConfig(page, "Applicant kind", "Conditional rules");
    await modal.getByRole("checkbox", { name: "Show this field only when conditions are met" }).click();
    await page.getByLabel("Visibility condition 1.1 field").click();
    await expect(page.getByRole("option", { name: "Company name" })).toHaveCount(0);
    await expect(page.getByRole("option", { name: "Applicant age" })).toBeVisible();
    await page.keyboard.press("Escape");
    await modal.getByRole("button", { name: "Cancel" }).click();
    await expect(modal).toBeHidden();

    // Clear indicators on the canvas (text, never color alone) and an overview in the left navigation.
    await page.getByRole("tab", { name: "Design" }).click();
    const canvas = page.getByRole("region", { name: "Form canvas" });
    await expect(canvas.locator('[data-flag="visible-rule"]')).toHaveCount(1);
    await expect(canvas.locator('[data-flag="required-rule"]')).toHaveCount(1);
    await expect(canvas.locator('[data-flag="read-only"]')).toHaveCount(1);
    await expect(canvas.locator('[data-flag="not-in-form"]')).toHaveCount(1);
    await page.getByRole("tablist", { name: "Form designer" }).getByRole("tab", { name: /^Rules/ }).click();
    const overview = page.getByRole("table", { name: "Fields with rules or behavior settings" });
    await expect(overview.getByRole("row", { name: /Company name.*Applicant kind is Company/ })).toBeVisible();
    await expect(overview.getByRole("row", { name: /Guardian name.*When Applicant age is less than 18/ })).toBeVisible();
    await expect(overview.getByRole("row", { name: /Intake status.*Read-only/ })).toBeVisible();
    await expect(overview.getByRole("row", { name: /Reference note.*Not in form/ })).toBeVisible();

    // Preview: Company name appears only when the kind is Company; the read-only status shows Draft; the not-in-form field is absent.
    await page.getByRole("tab", { name: "Preview" }).click();
    await expect(page.getByLabel(/Company name/)).toHaveCount(0);
    await expect(page.getByLabel(/Reference note/)).toHaveCount(0);
    await expect(page.locator('[data-readonly-field="true"]')).toContainText("Draft");
    await expect(page.locator('[data-readonly-field="true"]')).toContainText("Read-only");
    await chooseOption(page, /Applicant kind/, "Company");
    await expect(page.getByLabel(/Company name/)).toBeVisible();
    await axeScan(page, "designer preview with rules");
    await saveAndPublish(page, "rules and field behavior");
    await signOut(page);
  });

  test("clerk's form honors the rules; hidden values are retained when a field is hidden and the server keeps them", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/forms/${formId}/records/new`);
    await expect(page.getByLabel(/Company name/)).toHaveCount(0);
    await expect(page.getByLabel(/Reference note/)).toHaveCount(0);
    await expect(page.locator('[data-readonly-field="true"]')).toContainText("Draft");

    // Required Applicant kind; choosing Company reveals the (required) Company name.
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByRole("alert").first()).toContainText("required");
    await chooseOption(page, /Applicant kind/, "Company");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Company name is required" })).toBeVisible();
    await page.getByLabel(/Company name/).fill("Acme Browser Ltd");

    // Required-if: a minor needs a guardian; an adult does not.
    await page.getByLabel(/Applicant age/).fill("15");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Guardian name is required" })).toBeVisible();
    await page.getByLabel(/Applicant age/).fill("40");
    await axeScan(page, "record form with rules");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("Acme Browser Ltd")).toBeVisible();
    await expect(page.getByText("Draft", { exact: true })).toBeVisible(); // read-only default applied
    recordUrl = page.url();
    const recordId = /\/app\/records\/([0-9a-f-]{36})/.exec(recordUrl)![1]!;

    // Switch to Person: Company name disappears from the edit form and from the record view, but its value is retained by the server.
    await page.getByRole("link", { name: "Edit" }).first().click();
    await expect(page.locator('[data-readonly-field="true"]')).toContainText("Draft");
    await chooseOption(page, /Applicant kind/, "Person");
    await expect(page.getByLabel(/Company name/)).toHaveCount(0);
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Revision 2")).toBeVisible();
    await expect(page.getByText("Acme Browser Ltd")).toHaveCount(0);
    const projection = await page.request.get(`/api/v1/form-records/${recordId}/projection`);
    expect(projection.ok()).toBeTruthy();
    const values = ((await projection.json()) as { record: { values: Array<{ fieldKey: string; stringValue: string }> } }).record.values;
    expect(values.find((v) => v.fieldKey === "company_name")?.stringValue).toBe("Acme Browser Ltd");

    // Switching back shows the retained value again.
    await page.getByRole("link", { name: "Edit" }).first().click();
    await chooseOption(page, /Applicant kind/, "Company");
    await expect(page.getByLabel(/Company name/)).toHaveValue("Acme Browser Ltd");
    await signOut(page);
  });
});
