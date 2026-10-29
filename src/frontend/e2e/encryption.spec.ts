import { expect, test } from "@playwright/test";
import { formIdFromUrl, placeFromPicker, saveAndPublish, signIn, signOut } from "./support";

/**
 * Phase 1B — application-level field encryption as a person experiences it:
 *  1. The Add field dialog shows a "Security & Encryption" section whose defaults are Confidential + encryption required
 *     for free text, explains the search impact, and allows a plaintext Internal field only under the organization policy.
 *  2. The clerk's record shows the decrypted value with an "Encrypted" badge; the reader (no grant) is denied.
 *  3. The Edit field dialog strengthens a plaintext field to encrypted (audited migration); there is no way back.
 *  4. The organization owner can tighten the data-protection policy and the designer is then refused plaintext.
 *     The shared development organization's policy is restored at the end.
 */
const run = Date.now().toString(36);
const formName = `Sealed ${run}`;
let formId = "";
let recordUrl = "";

function policyCard(page: import("@playwright/test").Page) {
  return page.getByRole("region", { name: "Data protection policy" });
}

test.describe.serial("Field encryption: designer configuration, record display, strengthening, organization policy", () => {
  test("designer sees secure defaults, creates an encrypted and a plaintext field, and publishes", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(formName);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: formName })).toBeVisible();
    formId = formIdFromUrl(page.url());

    await page.getByRole("tab", { name: /^Fields/ }).click();
    await page.getByRole("button", { name: "Add field", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Add field" });
    await dialog.getByLabel("Display name").fill("Case notes");
    const security = dialog.getByRole("group", { name: "Security & Encryption" });
    await expect(security.getByRole("combobox", { name: /Classification/ })).toHaveText("Confidential");
    await expect(security.getByText("Enabled (required)")).toBeVisible();
    await expect(security.getByText(/Encrypted for privacy — not searchable/)).toBeVisible();
    await expect(security.getByText(/cannot be searched in V1/)).toBeVisible();
    await expect(security.getByRole("checkbox", { name: /Encrypt this field/ })).toHaveCount(0); // mandatory: no opt-out
    await dialog.getByRole("button", { name: "Add field" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("row", { name: /Case notes/ })).toContainText("Confidential · Encrypted");

    // A structured value defaults to Internal + plaintext (permitted by the development policy), with the DBA warning.
    await page.getByRole("button", { name: "Add field", exact: true }).click();
    const second = page.getByRole("dialog", { name: "Add field" });
    await second.getByLabel("Display name").fill("Exhibit count");
    await second.getByLabel("Storage type").click();
    await page.getByRole("option", { name: /^Integer$/ }).click();
    const secondSecurity = second.getByRole("group", { name: "Security & Encryption" });
    await expect(secondSecurity.getByRole("combobox", { name: /Classification/ })).toHaveText("Internal");
    await secondSecurity.getByRole("checkbox", { name: /Encrypt this field/ }).uncheck();
    await expect(secondSecurity.getByText("Off — plaintext")).toBeVisible();
    await expect(secondSecurity.getByText(/readable by a database administrator/)).toBeVisible();
    await second.getByRole("button", { name: "Add field" }).click();
    await expect(second).toBeHidden();
    await expect(page.getByRole("row", { name: /Exhibit count/ })).toContainText("Internal · Plaintext");

    // Restricted cannot be made plaintext: choosing it hides the opt-out and shows encryption as required.
    await page.getByRole("button", { name: "Add field", exact: true }).click();
    const third = page.getByRole("dialog", { name: "Add field" });
    await third.getByLabel("Storage type").click();
    await page.getByRole("option", { name: /^Integer$/ }).click();
    const thirdSecurity = third.getByRole("group", { name: "Security & Encryption" });
    await thirdSecurity.getByRole("combobox", { name: /Classification/ }).click();
    await page.getByRole("option", { name: "Restricted" }).click();
    await expect(thirdSecurity.getByText("Enabled (required)")).toBeVisible();
    await expect(thirdSecurity.getByRole("checkbox", { name: /Encrypt this field/ })).toHaveCount(0);
    await third.getByRole("button", { name: "Cancel" }).click();

    await placeFromPicker(page, "Add field", /Case notes/);
    await placeFromPicker(page, "Add field", /Exhibit count/);

    // The designer canvas shows a green "Encrypted" badge (lock icon, success styling) below the field's preview
    // control for the encrypted field, and none for the plaintext one — restored after it disappeared in the Phase
    // C3 layout refinement (TileBadges never received the field's protection mode, only the layout element).
    await page.getByRole("tab", { name: "Design" }).click();
    const canvas = page.getByRole("region", { name: "Form canvas" });
    const encryptedTile = canvas.getByRole("button", { name: "Case notes", exact: true }).locator("..").locator("..");
    const plaintextTile = canvas.getByRole("button", { name: "Exhibit count", exact: true }).locator("..").locator("..");
    const encryptedBadge = encryptedTile.locator('[data-flag="encrypted"]');
    await expect(encryptedBadge).toBeVisible();
    await expect(encryptedBadge).toHaveText("Encrypted");
    await expect(encryptedBadge).toHaveCSS("color", "rgb(20, 83, 45)"); // #14532d, the existing success/green tone
    await expect(plaintextTile.locator('[data-flag="encrypted"]')).toHaveCount(0);

    await saveAndPublish(page, "initial sealed form");
    await expect(page.getByText("Published v1")).toBeVisible();
    await signOut(page);
  });

  test("clerk sees the decrypted value with an Encrypted badge; the reader is denied", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/forms");
    await page.getByRole("row", { name: new RegExp(formName) }).getByRole("button", { name: "New record" }).click();
    await expect(page.getByText(/Encrypted for privacy — not searchable/)).toBeVisible();
    await page.getByLabel(/Case notes/).fill(`Marina meeting ${run}`);
    await page.getByLabel(/Exhibit count/).fill("3");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    recordUrl = page.url();
    await expect(page.getByText(`Marina meeting ${run}`)).toBeVisible();
    await expect(page.getByText("Encrypted", { exact: true })).toBeVisible();
    await signOut(page);

    await signIn(page, "reader");
    await page.goto(recordUrl);
    await expect(page.getByText(/Not found|do not have permission/i).first()).toBeVisible();
    await expect(page.getByText(`Marina meeting ${run}`)).toHaveCount(0);
    await signOut(page);
  });

  test("designer strengthens the plaintext field to encrypted; the change is one-way", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: /^Fields/ }).click();
    await page.getByRole("button", { name: "Edit Exhibit count" }).click();
    const dialog = page.getByRole("dialog", { name: "Edit field" });
    await dialog.getByRole("tab", { name: "Security & Encryption" }).click();
    const security = dialog.getByRole("group", { name: "Security & Encryption" });
    await expect(security.getByText("Off — plaintext")).toBeVisible();
    await security.getByRole("button", { name: /Strengthen protection/ }).click();
    await expect(security.getByText(/rewritten as ciphertext when each is next saved/)).toBeVisible();
    await security.getByRole("combobox", { name: /New classification/ }).click();
    await page.getByRole("option", { name: "Confidential" }).click();
    await expect(security.getByRole("button", { name: "Apply strengthening" })).toBeDisabled(); // reason required
    await security.getByLabel("Reason").fill("Exhibit counts are case-sensitive data");
    await security.getByRole("button", { name: "Apply strengthening" }).click();
    await expect(security.getByText("Enabled", { exact: true })).toBeVisible();
    // The background migration may already have re-encrypted the single record by now: either the pending count or the verified badge is correct.
    await expect(security.getByText(/1 record\(s\) still hold a plaintext copy|All listed records encrypted|Authority-verified: every record's current version encrypted/).first()).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("row", { name: /Exhibit count/ })).toContainText("Confidential · Encrypted");
    await page.getByRole("button", { name: "Edit Exhibit count" }).click();
    await page.getByRole("dialog", { name: "Edit field" }).getByRole("tab", { name: "Security & Encryption" }).click();
    const reopened = page.getByRole("dialog", { name: "Edit field" }).getByRole("group", { name: "Security & Encryption" });
    await expect(reopened.getByText("Enabled", { exact: true })).toBeVisible();
    // The background migration re-encrypts the clerk's existing record through the Authority; only then is the field reported as fully encrypted.
    const authorityBadge = "Authority-verified: every record's current version encrypted";
    await expect(reopened.getByText(new RegExp(`All listed records encrypted|${authorityBadge}`))).toBeVisible({ timeout: 120_000 });
    if (await reopened.getByText(authorityBadge).isVisible()) {
      // Phase C7 M4 (verifier configured): the Authority attestation, shown with what it does not cover (current versions only).
      await expect(reopened.getByText(/It covers current versions only/)).toBeVisible();
    } else {
      // Without the verifier the badge must not be read as an independent completeness proof (security finding G19): the caveat is shown with it.
      await expect(reopened.getByText(/not an independently verified completeness guarantee/)).toBeVisible();
    }
    await expect(reopened.getByRole("group", { name: "Encryption migration progress" })).toHaveCount(0);
    await reopened.getByRole("button", { name: /Strengthen protection/ }).click();
    await expect(reopened.getByRole("checkbox", { name: /Switch to application-level encryption/ })).toHaveCount(0);
    await reopened.getByRole("combobox", { name: /New classification/ }).click();
    await expect(page.getByRole("option", { name: "Internal" })).toHaveCount(0); // downgrade is not offered
    await expect(page.getByRole("option", { name: "Restricted" })).toBeVisible();
    await page.keyboard.press("Escape");
    await reopened.getByRole("button", { name: "Cancel" }).click();
    await page.getByRole("dialog", { name: "Edit field" }).getByRole("button", { name: "Cancel" }).click();

    await page.getByRole("tab", { name: "Versions" }).click();
    await expect(page.getByRole("row", { name: /^v1 / })).toBeVisible();
    await expect(page.getByRole("row", { name: /^v2 / })).toHaveCount(0); // strengthening is not a layout change
    await signOut(page);
  });

  test("organization owner tightens the data-protection policy; the designer is then refused plaintext", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization/branding");
    const card = policyCard(page);
    await expect(card.getByText(/Development default — not yet reviewed|^Reviewed /)).toBeVisible();
    await card.getByRole("checkbox", { name: /Permit plaintext storage/ }).uncheck();
    await card.getByLabel("Policy statement").fill(`Playwright ${run}: no plaintext customer data in this organization.`);
    await card.getByRole("button", { name: "Save data protection policy" }).click();
    await expect(card.getByText("Policy saved")).toBeVisible();
    await signOut(page);

    try {
      await signIn(page, "designer");
      await page.goto(`/app/forms/${formId}/design`);
      await page.getByRole("tab", { name: /^Fields/ }).click();
      await page.getByRole("button", { name: "Add field", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Add field" });
      await dialog.getByLabel("Display name").fill("Plain attempt");
      await dialog.getByLabel("Storage type").click();
      await page.getByRole("option", { name: /^Integer$/ }).click();
      const security = dialog.getByRole("group", { name: "Security & Encryption" });
      await expect(security.getByText(/plaintext Internal fields not permitted/)).toBeVisible();
      await security.getByRole("checkbox", { name: /Encrypt this field/ }).uncheck();
      await expect(security.getByText("Not allowed by the organization policy")).toBeVisible();
      await expect(dialog.getByRole("button", { name: "Add field" })).toBeDisabled();
      await security.getByRole("checkbox", { name: /Encrypt this field/ }).check();
      await expect(dialog.getByRole("button", { name: "Add field" })).toBeEnabled();
      await dialog.getByRole("button", { name: "Cancel" }).click();
      await signOut(page);
    } finally {
      // Restore the shared development organization's policy for the other specs.
      await signIn(page, "owner");
      await page.goto("/app/organization/branding");
      const restore = policyCard(page);
      await restore.getByRole("checkbox", { name: /Permit plaintext storage/ }).check();
      await restore.getByLabel("Policy statement").fill(`Playwright ${run}: restored the development default after the encryption spec.`);
      await restore.getByRole("button", { name: "Save data protection policy" }).click();
      await expect(restore.getByText("Policy saved")).toBeVisible();
    }
  });
});
