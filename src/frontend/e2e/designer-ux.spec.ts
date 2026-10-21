import { expect, test, type Page } from "@playwright/test";
import { addDocumentCollection, addField, formIdFromUrl, openFieldConfig, placeFromPicker, publish, selectTile, signIn, signOut } from "./support";

/**
 * C1 designer rebuild acceptance (docs/ui/c1-designer-rebuild/ACCEPTANCE_TEST_MATRIX.md), against the real API:
 *  UX-01 three-pane designer · UX-02 click-to-select + inspector · UX-03 inline rename keeps ids · UX-04 "+ Add Row"
 *  creates a titled section · UX-05 fields placed into that section's exact cells · UX-06 move a field between
 *  sections · UX-07 rename/collapse/reorder sections survive save + reload · UX-08 removing a non-empty section needs an
 *  explicit disposition · UX-09 publish → the record page renders the new section · UX-10 a document collection
 *  placed in a new section keeps its id · UX-11 inspector tabs keep unsaved input · UX-13 Actions column · UX-14
 *  keyboard-only section/placement work · unsaved-change navigation guard and undo/redo.
 */
const run = Date.now().toString(36);
const formName = `Sections ${run}`;
let formId = "";

const canvas = (page: Page) => page.getByRole("region", { name: "Form canvas" });
const section = (page: Page, title: string) => canvas(page).getByRole("region", { name: title, exact: true });
const tileNames = (page: Page, title: string) => section(page, title).locator("button[aria-keyshortcuts]").allTextContents();

async function saveDraft(page: Page) {
  await page.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Unsaved changes")).toBeHidden();
}

test.describe.serial("C1 designer rebuild: sections (Add Row), placement, inspector, persistence and publish", () => {
  test("designer builds titled sections with fields, moves and renames them, and the layout survives save and reload", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());

    // UX-01: left designer navigation, centre toolbar + canvas, right inspector.
    const nav = page.getByRole("tablist", { name: "Form designer" });
    for (const item of ["Design", "Fields", "Rules", "Preview", "Versions", "Settings", "Form permissions", "Related forms"]) {
      await expect(nav.getByRole("tab", { name: new RegExp(`^${item}`) })).toBeVisible();
    }
    for (const action of ["Add Field", "Add Row", "Add Column", "Save draft"]) {
      await expect(page.getByRole("group", { name: "Designer actions" }).getByRole("button", { name: action })).toBeVisible();
    }
    await expect(page.getByRole("complementary", { name: "Properties" })).toBeVisible();

    for (const [name, type] of [["Last Name", "String"], ["First Name", "String"], ["Reference Number", "String"], ["Address Line 1", "String"], ["City", "String"]] as const) {
      await addField(page, name, type);
    }
    await addDocumentCollection(page, "Case documents");

    // The form's default section is renamed through the section inspector.
    await page.getByRole("tab", { name: "Design" }).click();
    await canvas(page).getByRole("button", { name: formName, exact: true }).click();
    const sectionInspector = page.getByRole("complementary", { name: "Section Properties" });
    await sectionInspector.getByLabel("Section title").fill("Personal Information");
    await expect(section(page, "Personal Information")).toBeVisible();
    for (const name of [/^Last Name/, /^First Name/, /^Reference Number/]) {
      await placeFromPicker(page, "Add field", name, { section: "Personal Information" });
    }
    expect(await tileNames(page, "Personal Information")).toEqual(["Last Name", "First Name", "Reference Number"]);

    // UX-04: "+ Add Row" creates a titled section with an empty "+ Add Field" cell, ready to rename (not a line break).
    await page.getByRole("button", { name: "Add Row" }).click();
    const rename = page.getByRole("textbox", { name: "Rename section" });
    await expect(rename).toBeFocused();
    await expect(rename).toHaveValue("New section");
    await rename.fill("Contact Information");
    await rename.press("Enter");
    const contact = section(page, "Contact Information");
    await expect(contact).toBeVisible();
    await expect(contact.getByRole("button", { name: /^Add Field to Contact Information/ })).toHaveCount(1);
    await expect(contact.locator("button[aria-keyshortcuts]")).toHaveCount(0);

    // UX-05: two fields placed into that section's own cells, with spans.
    await placeFromPicker(page, "Add field", /^Address Line 1/, { section: "Contact Information", width: "2 columns" });
    await placeFromPicker(page, "Add field", /^City/, { section: "Contact Information", width: "2 columns" });
    expect(await tileNames(page, "Contact Information")).toEqual(["Address Line 1", "City"]);
    expect(await tileNames(page, "Personal Information")).toEqual(["Last Name", "First Name", "Reference Number"]);

    // UX-02 / UX-11: click a tile → only it is selected; the quick inspector shows its stable id's core fields live,
    // and Configure Field's full workspace keeps input across tabs.
    const inspector = await selectTile(page, "Last Name");
    await expect(canvas(page).locator('button[aria-keyshortcuts][aria-pressed="true"]')).toHaveCount(1);
    await expect(inspector.getByRole("heading", { name: "Field Properties" })).toBeVisible();
    await inspector.getByLabel("Help text").fill("Legal family name");
    await inspector.getByLabel("Placeholder").fill("Enter last name"); // label/type/help text/placeholder live only in the quick inspector
    let modal = await openFieldConfig(page, "Last Name");
    for (const tabName of ["Validation", "Conditional rules", "Security & Encryption", "Advanced"]) {
      await expect(modal.getByRole("tab", { name: tabName })).toBeVisible();
    }
    await expect(modal.getByRole("tab", { name: "General" })).toHaveCount(0); // removed: nothing here duplicates the quick inspector
    await expect(modal.getByLabel("Maximum length")).toBeVisible(); // Validation is the modal's first (default) tab
    await modal.getByLabel("Maximum length").fill("abc");
    await expect(modal.getByText("Enter a whole number from 1 to 4000")).toBeVisible();
    await modal.getByLabel("Maximum length").fill("80");
    await modal.getByRole("tab", { name: "Advanced" }).click();
    await expect(modal.getByLabel("Field key")).toHaveValue("last_name");
    const lastNameId = await modal.getByLabel("Field UUID").inputValue();
    // Switching tabs must not lose the Validation-tab edit made above (C1-UX §C).
    await modal.getByRole("tab", { name: "Validation" }).click();
    await expect(modal.getByLabel("Maximum length")).toHaveValue("80");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    await expect(inspector.getByLabel("Placeholder")).toHaveValue("Enter last name"); // unaffected by the modal's Apply

    // UX-06: move a field from Personal Information into the new section (Configure Field's Advanced tab: Placement
    // → Section, no dragging).
    modal = await openFieldConfig(page, "Reference Number", "Advanced");
    await modal.getByRole("combobox", { name: "Section" }).click();
    await page.getByRole("option", { name: "Contact Information" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    expect(await tileNames(page, "Contact Information")).toEqual(["Address Line 1", "City", "Reference Number"]);
    expect(await tileNames(page, "Personal Information")).toEqual(["Last Name", "First Name"]);

    // UX-14: keyboard-only move inside a section (Alt+ArrowUp on the focused tile).
    await section(page, "Contact Information").getByRole("button", { name: "Reference Number", exact: true }).focus();
    await page.keyboard.press("Alt+ArrowUp");
    expect(await tileNames(page, "Contact Information")).toEqual(["Address Line 1", "Reference Number", "City"]);

    // UX-03: double-click rename changes only the display label.
    await section(page, "Personal Information").getByRole("button", { name: "First Name", exact: true }).dblclick();
    const renameField = page.getByRole("textbox", { name: "Rename field label" });
    await renameField.fill("Given name");
    await renameField.press("Enter");
    await expect(section(page, "Personal Information").getByRole("button", { name: "Given name", exact: true })).toBeVisible();

    // Undo / redo across section and field edits.
    await page.getByRole("button", { name: "Undo last layout change" }).click();
    await expect(section(page, "Personal Information").getByRole("button", { name: "First Name", exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Redo last undone layout change" }).click();
    await expect(section(page, "Personal Information").getByRole("button", { name: "Given name", exact: true })).toBeVisible();

    // UX-10: a document collection placed in another new section; UX-08: removing that non-empty section needs a choice.
    await page.getByRole("button", { name: "Add Row" }).click();
    await page.getByRole("textbox", { name: "Rename section" }).fill("Evidence");
    await page.keyboard.press("Enter");
    await placeFromPicker(page, "Add document collection", /^Case documents/, { section: "Evidence" });
    await section(page, "Evidence").getByRole("button", { name: "Section actions for Evidence" }).click();
    await page.getByRole("menuitem", { name: "Remove section…" }).click();
    const removal = page.getByRole("dialog", { name: /Remove section “Evidence”/ });
    const confirm = removal.getByRole("button", { name: "Remove section" });
    await expect(confirm).toBeDisabled();
    await removal.getByRole("radio", { name: /Move the 1 item to another section/ }).check();
    await removal.getByLabel("Move to section").click();
    await page.getByRole("option", { name: "Contact Information" }).click();
    await confirm.click();
    await expect(removal).toBeHidden();
    await expect(section(page, "Evidence")).toHaveCount(0);
    expect(await tileNames(page, "Contact Information")).toEqual(["Address Line 1", "Reference Number", "City", "Case documents"]);

    // UX-07: reorder and collapse sections.
    await section(page, "Contact Information").getByRole("button", { name: "Section actions for Contact Information" }).click();
    await page.getByRole("menuitem", { name: "Move section up" }).click();
    await expect(canvas(page).getByRole("heading", { level: 2 }).first()).toHaveText("Contact Information");
    await page.getByRole("button", { name: "Collapse Personal Information" }).click();
    await expect(section(page, "Personal Information").getByRole("list")).toBeHidden();

    await saveDraft(page);
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    await expect(canvas(page).getByRole("heading", { level: 2 })).toHaveText(["Contact Information", "Personal Information"]);
    expect(await tileNames(page, "Contact Information")).toEqual(["Address Line 1", "Reference Number", "City", "Case documents"]);
    await expect(page.getByRole("button", { name: "Expand Personal Information" })).toBeVisible(); // collapse is a per-viewer designer preference
    await page.getByRole("button", { name: "Expand Personal Information" }).click();
    expect(await tileNames(page, "Personal Information")).toEqual(["Last Name", "Given name"]);
    const reopened = await selectTile(page, "Last Name");
    await expect(reopened.getByLabel("Placeholder")).toHaveValue("Enter last name");
    const reopenedModal = await openFieldConfig(page, "Last Name", "Advanced");
    await expect(reopenedModal.getByLabel("Field UUID")).toHaveValue(lastNameId);
    await reopenedModal.getByRole("button", { name: "Cancel" }).click();

    // Preview uses the record renderer and shows both sections.
    await page.getByRole("tab", { name: "Preview" }).click();
    await expect(page.getByRole("group", { name: "Contact Information" })).toBeVisible();
    await expect(page.getByRole("group", { name: "Personal Information" })).toBeVisible();

    // UX-09: publish; the version is immutable and listed.
    await publish(page, "sections layout");
    await expect(page.getByText("Published v1")).toBeVisible();
    await page.getByRole("tab", { name: "Versions" }).click();
    await expect(page.getByRole("row", { name: /^v1 / })).toBeVisible();

    // The field catalog shows each field's section and keeps the original key after the rename.
    await page.getByRole("tab", { name: /^Fields/ }).click();
    await expect(page.getByRole("row", { name: /First Name/ })).toContainText("first_name");
    await expect(page.getByRole("row", { name: /Reference Number/ })).toContainText("Contact Information");
    await signOut(page);
  });

  test("the clerk's record page renders the published sections with their fields", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    const contact = page.getByRole("group", { name: "Contact Information" });
    await expect(contact).toBeVisible();
    await expect(contact.getByLabel(/City/)).toBeVisible();
    await expect(contact.getByLabel(/Reference Number/)).toBeVisible();
    await expect(page.getByRole("group", { name: "Personal Information" }).getByLabel(/Given name/)).toBeVisible();
    await expect(page.getByRole("group", { name: "Personal Information" }).getByPlaceholder("Enter last name")).toBeVisible();
    await signOut(page);
  });

  test("unsaved changes block navigation until the designer confirms or saves", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    const modal = await openFieldConfig(page, "City", "Advanced"); // Field width (Placement) now lives on the Advanced tab
    await modal.getByLabel("Field width").click();
    await page.getByRole("option", { name: "3 columns" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    await expect(page.getByText("Unsaved changes")).toBeVisible();

    await page.getByRole("link", { name: "All forms" }).click();
    const guard = page.getByRole("dialog", { name: "Discard unsaved layout changes?" });
    await expect(guard).toBeVisible();
    await guard.getByRole("button", { name: "Stay and keep editing" }).click();
    await expect(guard).toBeHidden();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();

    await saveDraft(page);
    await page.getByRole("link", { name: "All forms" }).click();
    await expect(page.getByRole("dialog", { name: "Discard unsaved layout changes?" })).toHaveCount(0);
    await expect(page.getByRole("heading", { level: 1, name: "Forms" })).toBeVisible();
    await signOut(page);
  });

  test("Actions columns are rightmost with one visible action plus an overflow menu", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms");
    const row = page.getByRole("row", { name: new RegExp(formName) });
    await expect(row.getByRole("cell").last().getByRole("link", { name: "Design" })).toBeVisible();
    await row.getByRole("button", { name: new RegExp(`More actions for ${formName}`) }).click();
    await page.getByRole("menuitem", { name: /Records using this form/ }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Records" })).toBeVisible();

    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: /^Fields/ }).click();
    const fieldRow = page.getByRole("row", { name: /City/ });
    const actions = fieldRow.getByRole("cell").last();
    await expect(actions.getByRole("button", { name: "Edit City" })).toBeVisible();
    await actions.getByRole("button", { name: "More actions for City" }).click();
    await page.getByRole("menuitem", { name: "Show on canvas" }).click();
    await expect(page.getByRole("complementary", { name: "Field Properties" }).getByLabel("Field label")).toHaveValue("City");
    await signOut(page);
  });

  test("UX-12: default theme is black and the designer's semantic controls follow the theme tokens", async ({ page }) => {
    // The organization override path itself (validated branding → --ui-primary-* on :root) is covered by
    // packages/app-runtime/src/branding.test.ts and branding.spec.ts; here we prove the designer uses those tokens
    // rather than hard-coded colours, without changing the shared development organization's real branding.
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    const inspector = await selectTile(page, "City");
    await inspector.getByLabel("Help text").fill("dirty"); // enables the primary Save draft button
    const navTab = page.getByRole("tablist", { name: "Form designer" }).getByRole("tab", { name: "Design" });
    const save = page.getByRole("button", { name: "Save draft" });
    // The modal's overlay marks the rest of the page aria-hidden while open, so the toolbar and nav are read with it
    // closed; the modal's own active-tab indicator (vertical nav, Phase C3: a left border) is read with it open.
    const pageColours = () => Promise.all([save.evaluate((el) => getComputedStyle(el).backgroundColor), navTab.evaluate((el) => getComputedStyle(el).borderLeftColor)]);
    const modalColour = async () => {
      const modal = await openFieldConfig(page, "City");
      const colour = await modal.getByRole("tab", { name: "Validation" }).evaluate((el) => getComputedStyle(el).borderLeftColor);
      await modal.getByRole("button", { name: "Cancel" }).click();
      return colour;
    };
    expect(await pageColours()).toEqual(["rgb(17, 17, 17)", "rgb(17, 17, 17)"]);
    expect(await modalColour()).toBe("rgb(17, 17, 17)");
    await page.evaluate(() => document.documentElement.style.setProperty("--ui-primary-bg", "#1d4ed8"));
    expect(await pageColours()).toEqual(["rgb(29, 78, 216)", "rgb(29, 78, 216)"]);
    expect(await modalColour()).toBe("rgb(29, 78, 216)");
    await page.getByRole("button", { name: "Undo last layout change" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await signOut(page);
  });
});
