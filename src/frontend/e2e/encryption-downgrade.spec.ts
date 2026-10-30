import { expect, test } from "@playwright/test";
import { formIdFromUrl, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * ADR-PB-004 Amendment A1 — controlled removal of field encryption as a person experiences it:
 *  1. The organization owner opens the governance switch "Permit removal of encryption" on the data-protection policy.
 *  2. The owner (organization owner + designer) builds a form with an encrypted Internal field; a designer-only user,
 *     on a form of their own, never sees a way to remove encryption.
 *  3. The owner removes encryption: the clerk's record keeps its value, the background migration runs, and only then
 *     does the dialog say "All listed records migrated to plaintext".
 *  4. The shared development organization's policy is restored at the end.
 */
const run = Date.now().toString(36);
const formName = `Release ${run}`;
let formId = "";
let recordUrl = "";

function policyCard(page: import("@playwright/test").Page) {
  return page.getByRole("region", { name: "Data protection policy" });
}

async function addEncryptedInternalField(page: import("@playwright/test").Page, name: string) {
  await page.getByRole("tab", { name: /^Fields/ }).click();
  await page.getByRole("button", { name: "Add field", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Add field" });
  await dialog.getByLabel("Display name").fill(name);
  const security = dialog.getByRole("group", { name: "Security & Encryption" });
  await security.getByRole("combobox", { name: /Classification/ }).click();
  await page.getByRole("option", { name: "Internal" }).click();
  await expect(security.getByRole("checkbox", { name: /Encrypt this field/ })).toBeChecked();
  await dialog.getByRole("button", { name: "Add field" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("row", { name: new RegExp(name) })).toContainText("Internal · Encrypted");
}

test.describe.serial("Field encryption removal: policy switch, administrator-only controls, verified plaintext migration", () => {
  test("owner permits removal of encryption on the data-protection policy", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization/data-protection");
    const card = policyCard(page);
    await expect(card.getByRole("checkbox", { name: /Permit plaintext storage/ })).toBeChecked();
    await card.getByRole("checkbox", { name: /Permit removal of encryption/ }).check();
    await card.getByLabel("Policy statement").fill(`Playwright ${run}: governance approved removing encryption from Internal fields.`);
    await card.getByRole("button", { name: "Save data protection policy" }).click();
    await expect(card.getByText("Policy saved")).toBeVisible();
    await signOut(page);
  });

  test("owner publishes a form with an encrypted Internal field; a designer-only user has no control to remove encryption", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());
    await addEncryptedInternalField(page, "Badge number");
    await placeFromPicker(page, "Add field", /Badge number/);
    await saveAndPublish(page, "initial release form");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);

    // The designer-only persona, on its own form: strengthening is offered, no governance action exists, and the text says why.
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(`Release designer ${run}`);
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: `Release designer ${run}` })).toBeVisible();
    await addEncryptedInternalField(page, "Locker number");
    await page.getByRole("button", { name: "Edit Locker number" }).click();
    await page.getByRole("dialog", { name: "Edit field" }).getByRole("tab", { name: "Security & Encryption" }).click();
    const edit = page.getByRole("dialog", { name: "Edit field" }).getByRole("group", { name: "Security & Encryption" });
    await expect(edit.getByText("Enabled", { exact: true })).toBeVisible();
    await expect(edit.getByRole("button", { name: /Strengthen protection/ })).toBeVisible();
    await expect(edit.getByRole("button", { name: /Remove encryption/ })).toHaveCount(0);
    await expect(edit.getByRole("button", { name: /Declassify/ })).toHaveCount(0);
    // The simplified primary copy says this plainly; the full governance detail (why a designer can't do this) is
    // behind an accessible "Learn more" disclosure, not hidden entirely (Phase C3 usability corrections).
    await expect(edit.getByText(/Turning encryption off requires administrator approval/)).toBeVisible();
    await edit.getByText("Learn more", { exact: true }).click(); // a native <summary> disclosure, not exposed as role=button in this browser/Playwright combination
    await expect(edit.getByText(/Removing encryption is not a designer action/)).toBeVisible();
    await page.getByRole("dialog", { name: "Edit field" }).getByRole("button", { name: "Cancel" }).click();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    await page.getByLabel(/Badge number/).fill(`B-${run}`);
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    recordUrl = page.url();
    await expect(page.getByText("Encrypted", { exact: true })).toBeVisible();
    await signOut(page);
  });

  test("owner removes encryption; the value stays readable and the field is reported as fully migrated only after verification", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: /^Fields/ }).click();
    await page.getByRole("button", { name: "Edit Badge number" }).click();
    const dialog = page.getByRole("dialog", { name: "Edit field" });
    await dialog.getByRole("tab", { name: "Security & Encryption" }).click();
    const security = dialog.getByRole("group", { name: "Security & Encryption" });
    await expect(security.getByText("All listed records encrypted")).toBeVisible();
    await security.getByRole("button", { name: /Remove encryption…/ }).click();
    const panel = security.getByRole("group", { name: "Remove encryption" });
    await expect(panel.getByText(/cannot be taken back by re-encrypting later/)).toBeVisible();
    await expect(panel.getByRole("button", { name: "Remove encryption", exact: true })).toBeDisabled(); // reason required
    await panel.getByLabel("Reason").fill("Badge numbers are an Internal lookup key; governance approved plaintext");
    await panel.getByRole("button", { name: "Remove encryption", exact: true }).click();
    await expect(security.getByText("Off — plaintext")).toBeVisible();
    // With the field-protection verifier (security-test stack) the Authority attestation replaces the "listed records" badge (Phase C7 M4).
    await expect(security.getByText(/All listed records migrated to plaintext|Authority-verified: every record's current version in plaintext/)).toBeVisible({ timeout: 120_000 });
    await expect(security.getByRole("group", { name: "Decryption migration progress" })).toHaveCount(0);
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("row", { name: /Badge number/ })).toContainText("Internal · Plaintext");

    await page.getByRole("tab", { name: "Versions" }).click();
    await expect(page.getByRole("row", { name: /^v1 / })).toBeVisible();
    await expect(page.getByRole("row", { name: /^v2 / })).toHaveCount(0); // removing encryption is not a layout change
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await expect(page.getByText(`B-${run}`)).toBeVisible();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("Encrypted", { exact: true })).toHaveCount(0);
    await signOut(page);
  });

  test("the development organization's policy is restored", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization/data-protection");
    const card = policyCard(page);
    await card.getByRole("checkbox", { name: /Permit removal of encryption/ }).uncheck();
    await card.getByLabel("Policy statement").fill(`Playwright ${run}: restored the development default after the encryption removal spec.`);
    await card.getByRole("button", { name: "Save data protection policy" }).click();
    await expect(card.getByText("Policy saved")).toBeVisible();
    await signOut(page);
  });
});
