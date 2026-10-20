import { expect, test } from "@playwright/test";
import { addDocumentCollection, addField, formIdFromUrl, openFieldConfig, placeFromPicker, publish, saveAndPublish, signIn, signOut } from "./support";

/**
 * Owner acceptance defects, exercised exactly as a person would:
 *  1. An existing field (including the document collection) can be renamed and its properties edited; the key and type
 *     stay permanent, published versions stay immutable, and stored values and attachments survive.
 *  2. Who may create records can be changed after the form is saved (Form permissions tab) — grant, effective access,
 *     revocation — without a new form version and without exposing existing records.
 */
const run = Date.now().toString(36);
const formName = `Intake ${run}`;
let formId = "";
let clerkRecordUrl = "";

async function expectDenied(page: import("@playwright/test").Page) {
  await expect(page.getByText(/Not found|do not have permission/i).first()).toBeVisible();
}

test.describe.serial("Form management: edit fields and form permissions", () => {
  test("designer builds and publishes a form with a document collection; the clerk records data and uploads a document", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());

    await addField(page, "Given name", "String");
    await addDocumentCollection(page, "Case documents");
    await placeFromPicker(page, "Add field", /Given name/);
    await placeFromPicker(page, "Add document collection", /Case documents/);
    let modal = await openFieldConfig(page, "Case documents", "Validation");
    await modal.getByLabel("Maximum files").fill("10");
    await modal.getByLabel("Allowed content types").fill("text/plain");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    modal = await openFieldConfig(page, "Given name");
    await modal.getByRole("checkbox", { name: "Required" }).check();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    await saveAndPublish(page, "initial intake layout");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    await page.getByLabel(/Given name/).fill("Casey");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    clerkRecordUrl = page.url();
    await page.getByLabel(/Choose files for Case documents/).setInputFiles({ name: "exhibit-a.txt", mimeType: "text/plain", buffer: Buffer.from(`synthetic exhibit ${run}`) });
    const row = page.getByRole("row", { name: /exhibit-a\.txt/ });
    await expect(row.getByText("Ready")).toBeVisible({ timeout: 120_000 });
    await signOut(page);
  });

  test("designer edits an existing field and the document collection, then publishes the next version; data and attachments are preserved", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    await page.getByRole("tab", { name: /^Fields/ }).click();

    // Edit the text field: label, description, help text and validation; the key and type are shown but cannot be changed.
    await page.getByRole("button", { name: "Edit Given name" }).click();
    const dialog = page.getByRole("dialog", { name: "Edit field" });
    await expect(dialog.getByLabel("Field key")).toHaveValue("given_name");
    await expect(dialog.getByLabel("Field key")).toHaveAttribute("readonly", "");
    await expect(dialog.getByLabel("Storage type")).toHaveValue("String");
    await expect(dialog.getByLabel("Storage type")).toHaveAttribute("readonly", "");
    await expect(dialog.getByText(/cannot be renamed/)).toBeVisible();
    await dialog.getByLabel("Display label").fill("Forename");
    await dialog.getByLabel("Description").fill("Legal first name");
    await dialog.getByLabel("Help text").fill("As on the charge sheet");
    // Incompatible values are explained before saving; validation constraints live on their own tab, and switching
    // tabs must not lose the General-tab edits made above (C1-UX §C).
    await dialog.getByRole("tab", { name: "Validation" }).click();
    await dialog.getByLabel("Maximum length").fill("abc");
    await expect(dialog.getByText("Enter a whole number from 1 to 4000")).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Save changes" })).toBeDisabled();
    await dialog.getByLabel("Minimum length").fill("10");
    await dialog.getByLabel("Maximum length").fill("5");
    await expect(dialog.getByText("The minimum length cannot exceed the maximum length")).toBeVisible();
    await dialog.getByLabel("Minimum length").fill("1");
    await dialog.getByLabel("Maximum length").fill("60");
    // Switching back to General must not have lost the unsaved edits made there before Validation was opened.
    await dialog.getByRole("tab", { name: "General" }).click();
    await expect(dialog.getByLabel("Display label")).toHaveValue("Forename");
    await expect(dialog.getByLabel("Description")).toHaveValue("Legal first name");
    await expect(dialog.getByLabel("Help text")).toHaveValue("As on the charge sheet");
    await dialog.getByRole("button", { name: "Save changes" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("rowheader", { name: "Forename" })).toBeVisible();
    await expect(page.getByRole("row", { name: /Forename/ })).toContainText("given_name"); // the key did not change
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await expect(page.getByText(/Draft revision \d+/)).toBeVisible();

    // Edit the document collection: rename it and change its upload restrictions.
    await page.getByRole("button", { name: "Edit Case documents" }).click();
    const collection = page.getByRole("dialog", { name: "Edit document collection" });
    await expect(collection.getByLabel("Storage type")).toHaveValue("File (document collection)");
    await collection.getByLabel("Display label").fill("Evidence");
    await collection.getByRole("tab", { name: "Validation" }).click();
    await collection.getByLabel("Maximum files").fill("5");
    await collection.getByLabel("Maximum file size (MB)").fill("8");
    await collection.getByLabel("Allowed content types").fill("text/plain");
    await collection.getByRole("button", { name: "Save changes" }).click();
    await expect(collection).toBeHidden();
    await expect(page.getByRole("rowheader", { name: "Evidence" })).toBeVisible();

    // The saved draft can be edited again (repeated saves), then published as the next immutable version.
    await page.getByRole("button", { name: "Edit Forename" }).click();
    await expect(page.getByRole("dialog", { name: "Edit field" }).getByLabel("Help text")).toHaveValue("As on the charge sheet");
    await page.getByRole("dialog", { name: "Edit field" }).getByRole("button", { name: "Cancel" }).click();
    await publish(page, "rename given name and document collection");
    await expect(page.getByText("Published v2")).toBeVisible();
    await page.getByRole("tab", { name: "Versions" }).click();
    await expect(page.getByRole("row", { name: /^v1 / })).toBeVisible();
    await expect(page.getByRole("row", { name: /^v2 / })).toBeVisible();
    await signOut(page);

    // The clerk's existing record shows the new labels, the stored value and the original attachment, and still accepts uploads.
    await signIn(page, "clerk");
    await page.goto(clerkRecordUrl);
    await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText("Layout v2")).toBeVisible();
    await expect(page.getByText("Forename", { exact: true })).toBeVisible();
    await expect(page.getByText("Casey", { exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: /Evidence \(1 of 5\)/ })).toBeVisible();
    const original = page.getByRole("row", { name: /exhibit-a\.txt/ });
    await expect(original.getByText("Ready")).toBeVisible();
    const download = page.waitForEvent("download");
    await original.getByRole("link", { name: "Download" }).click();
    expect((await download).suggestedFilename()).toBe("exhibit-a.txt");
    await page.getByLabel(/Choose files for Evidence/).setInputFiles({ name: "exhibit-b.txt", mimeType: "text/plain", buffer: Buffer.from(`second exhibit ${run}`) });
    await expect(page.getByRole("heading", { name: /Evidence \(2 of 5\)/ })).toBeVisible({ timeout: 120_000 });
    await signOut(page);
  });

  test("designer changes who can create records after the form is saved: grant, effective access, revocation, no new version", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    await expect(page.getByText("Published v2")).toBeVisible();
    await page.getByRole("tab", { name: "Form permissions" }).click();

    // The panel reports the authority's state, including the clerk granted when the form was created.
    await expect(page.getByText(/Source: Authorization Authority/)).toBeVisible();
    await expect(page.getByText(/Authorization revision \d+/)).toBeVisible();
    await expect(page.getByRole("row", { name: /Casey Clerk/ })).toBeVisible();
    await expect(page.getByRole("row", { name: /Riley Reader/ })).toHaveCount(0);

    await page.getByRole("button", { name: "Grant permission" }).click();
    const grant = page.getByRole("dialog", { name: "Grant permission to create records" });
    await expect(grant.getByRole("button", { name: "Grant", exact: true })).toBeDisabled();
    await grant.getByRole("combobox", { name: /Person/ }).click();
    await page.getByRole("option", { name: /Riley Reader/ }).click();
    await grant.getByLabel("Reason").fill("reader joins intake");
    await grant.getByRole("button", { name: "Grant", exact: true }).click();
    await expect(grant).toBeHidden();
    await expect(page.getByRole("row", { name: /Riley Reader/ })).toContainText("Create records");
    // Granting is not a form change: the published version and draft are untouched.
    await expect(page.getByText("Published v2")).toBeVisible();
    await signOut(page);

    // The reader can now create records, but sees nothing that belongs to anyone else.
    await signIn(page, "reader");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    await page.getByLabel(/Forename/).fill("Riley's own record");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    const readerRecordUrl = page.url();
    await page.goto(clerkRecordUrl);
    await expectDenied(page);
    await page.goto("/app/records");
    await expect(page.getByRole("heading", { level: 1, name: "Records" })).toBeVisible();
    await expect(page.getByRole("row", { name: new RegExp(formName) })).toHaveCount(1); // only their own record
    await signOut(page);

    // Revocation is immediate and removes only the create permission.
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: "Form permissions" }).click();
    await page.getByRole("button", { name: /^Revoke Riley Reader/ }).click();
    const revoke = page.getByRole("dialog", { name: /^Revoke Riley Reader/ });
    await expect(revoke.getByRole("button", { name: "Revoke permission" })).toBeDisabled();
    await revoke.getByLabel("Reason").fill("intake finished");
    await revoke.getByRole("button", { name: "Revoke permission" }).click();
    await expect(revoke).toBeHidden();
    await expect(page.getByRole("row", { name: /Riley Reader/ })).toHaveCount(0);
    await expect(page.getByRole("row", { name: /Casey Clerk/ })).toBeVisible();
    await expect(page.getByText("Published v2")).toBeVisible();
    await signOut(page);

    await signIn(page, "reader");
    await page.goto("/app/forms");
    await expect(page.getByRole("heading", { level: 1, name: "Forms" })).toBeVisible();
    await expect(page.getByRole("row", { name: new RegExp(formName) })).toHaveCount(0);
    await page.goto(`/app/forms/${formId}/records/new`);
    await expectDenied(page);
    await page.goto(readerRecordUrl); // the record they created stays theirs
    await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 60_000 });
    await signOut(page);

    // Other people's permissions are unaffected.
    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await expect(page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" })).toBeVisible();
    await signOut(page);
  });
});
