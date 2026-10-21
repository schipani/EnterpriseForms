import { expect, test, type Page } from "@playwright/test";
import { openFieldConfig, placeFromPicker } from "./support";

/**
 * Full Accused vertical slice in a real browser: designer sign-in → create and publish AccusedAddress and Accused forms
 * (fields, relationship, document collection, 4-column layout) → clerk sign-in → create an Accused record → add two
 * addresses → upload documents while navigating away → verify Ready state, download link, activity, access denial.
 */
const run = Date.now().toString(36);

async function signIn(page: Page, persona: "owner" | "designer" | "clerk" | "reader") {
  await page.goto("/login");
  await page.getByRole("button", { name: new RegExp("\\(" + persona + "\\)$") }).click();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
}

async function signOut(page: Page) {
  await page.getByRole("button", { name: /Account menu/ }).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
}

async function addField(page: Page, name: string, type: string, options?: string) {
  await page.getByRole("tab", { name: /^Fields/ }).click();
  await page.getByRole("button", { name: "Add field", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Add field" });
  await dialog.getByLabel("Display name").fill(name);
  await dialog.getByLabel("Storage type").click();
  await page.getByRole("option", { name: type === "File" ? /File/ : type === "Option" ? /^Dropdown \(Single Select\)$/ : new RegExp(`^${type}$`) }).click();
  if (options) {
    await dialog.getByLabel(/Options/).fill(options);
  }
  await dialog.getByRole("button", { name: "Add field" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("rowheader", { name: name })).toBeVisible();
}

async function saveAndPublish(page: Page, reason: string) {
  await page.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Unsaved changes")).toBeHidden();
  await page.getByRole("button", { name: /^Publish/ }).click();
  const dialog = page.getByRole("dialog", { name: "Publish a new version" });
  await dialog.getByLabel("Change reason").fill(reason);
  await dialog.getByRole("button", { name: "Publish", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByText(/Published v\d/)).toBeVisible();
}

test.describe.serial("Stage B Accused workflow", () => {
  test("designer publishes AccusedAddress and Accused with a related list and document collection", async ({ page }) => {
    await signIn(page, "designer");

    // Child form first so it can be the target of the relationship.
    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(`AccusedAddress ${run}`);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: `AccusedAddress ${run}` })).toBeVisible();
    await addField(page, "Street address", "String");
    await addField(page, "City", "String");
    await addField(page, "Address kind", "Option", "Home\nWork");
    await placeFromPicker(page, "Add field", /Street address/);
    await placeFromPicker(page, "Add field", /City/);
    await placeFromPicker(page, "Add field", /Address kind/);
    await saveAndPublish(page, "initial address layout");

    await page.goto("/app/forms/new");
    await page.getByLabel("Form name").fill(`Accused ${run}`);
    await page.getByRole("checkbox", { name: /clerk/i }).check();
    await page.getByRole("button", { name: "Create form" }).click();
    await expect(page.getByRole("heading", { level: 1, name: `Accused ${run}` })).toBeVisible();
    await addField(page, "Given name", "String");
    await addField(page, "Family name", "String");
    await addField(page, "Date of birth", "Date");
    await addField(page, "In custody", "Boolean");
    await addField(page, "Arrest time", "DateTime");
    await addField(page, "External reference", "Guid");
    // "File" is listed in the generic Add field dialog…
    await page.getByRole("button", { name: "Add field", exact: true }).click();
    const generic = page.getByRole("dialog", { name: "Add field" });
    await generic.getByLabel("Storage type").click();
    await expect(page.getByRole("option", { name: "File (document collection)" })).toBeVisible();
    await page.keyboard.press("Escape");
    await generic.getByRole("button", { name: "Cancel" }).click();
    await expect(generic).toBeHidden();
    // …and the dedicated button creates the document collection's File field directly.
    await page.getByRole("button", { name: "Add document collection" }).click();
    const documentsDialog = page.getByRole("dialog", { name: "Add document collection" });
    await documentsDialog.getByLabel("Display name").fill("Case documents");
    await expect(documentsDialog.getByLabel("Storage type")).toHaveValue("File (document collection)");
    await documentsDialog.getByRole("button", { name: "Add document collection" }).click();
    await expect(documentsDialog).toBeHidden();
    await expect(page.getByRole("row", { name: /Case documents/ })).toContainText("File");

    await page.getByRole("tab", { name: /Related forms/ }).click();
    await page.getByRole("button", { name: "Add relationship" }).click();
    const relationship = page.getByRole("dialog", { name: "Add a related form list" });
    await relationship.getByLabel("Child form").click();
    await page.getByRole("option", { name: new RegExp(`AccusedAddress ${run}`) }).click();
    await relationship.getByLabel("List name").fill("Addresses");
    await relationship.getByRole("button", { name: "Add relationship" }).click();
    await expect(relationship).toBeHidden();
    await expect(page.getByRole("rowheader", { name: "Addresses" })).toBeVisible();

    await placeFromPicker(page, "Add field", /Given name/);
    await placeFromPicker(page, "Add field", /Family name/);
    await placeFromPicker(page, "Add field", /Date of birth/);
    await placeFromPicker(page, "Add field", /In custody/);
    await placeFromPicker(page, "Add field", /Arrest time/);
    await placeFromPicker(page, "Add field", /External reference/);
    await placeFromPicker(page, "Add related list", /Addresses/);
    await placeFromPicker(page, "Add document collection", /Case documents/);
    let modal = await openFieldConfig(page, "Case documents", "Validation");
    await modal.getByLabel("Maximum files").fill("10");
    await modal.getByLabel("Maximum file size (MB)").fill("20");
    await modal.getByLabel("Allowed content types").fill("application/pdf, text/plain");
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    // Two half-width name fields on one row, required.
    modal = await openFieldConfig(page, "Given name");
    await modal.getByRole("checkbox", { name: "Required" }).check();
    await modal.getByRole("tab", { name: "Advanced" }).click(); // Field width (Placement) lives here now
    await modal.getByLabel("Field width").click();
    await page.getByRole("option", { name: "2 columns" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    modal = await openFieldConfig(page, "Family name", "Advanced");
    await modal.getByLabel("Field width").click();
    await page.getByRole("option", { name: "2 columns" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();

    await saveAndPublish(page, "initial accused layout");
    await signOut(page);
  });

  test("clerk creates an Accused, adds addresses, uploads documents across navigation and reviews activity", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/forms");
    const row = page.getByRole("row", { name: new RegExp(`Accused ${run}`) }).filter({ hasNot: page.getByText("AccusedAddress") });
    await row.getByRole("button", { name: "New record" }).click();
    await expect(page.getByRole("heading", { level: 1, name: `New Accused ${run}` })).toBeVisible();

    // Client validation first, then a real create.
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByRole("alert").first()).toContainText("required");
    await page.getByLabel(/Given name/).fill("Avery");
    await page.getByLabel(/Family name/).fill("Browser");
    await page.getByLabel(/Date of birth/).fill("1990-02-28");
    await page.getByRole("checkbox", { name: "In custody" }).click();
    // DateTime is edited as local wall-clock time and stored as a UTC instant; Guid must be canonical.
    await page.getByLabel(/External reference/).fill("not-a-uuid");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByRole("alert").first()).toContainText(/UUID/);
    await page.getByLabel(/External reference/).fill("0199A2B3-C4D5-7E6F-8123-456789ABCDEF");
    await page.getByLabel(/Arrest time/).fill("2026-03-08T19:30");
    await page.getByRole("button", { name: "Create record" }).click();
    await expect(page.getByText("Integrity: Verified")).toBeVisible();
    await expect(page.getByText("0199a2b3-c4d5-7e6f-8123-456789abcdef")).toBeVisible();
    await expect(page.getByText(/\(2026-03-0[89]T\d{2}:30:00Z\)/)).toBeVisible();
    const recordUrl = page.url();

    // Two addresses through the reusable child list.
    for (const [street, kind] of [
      ["12 King Street W", "Home"],
      ["100 Queen Street W", "Work"],
    ] as const) {
      await page.getByRole("button", { name: /Add AccusedAddress/ }).click();
      await page.getByLabel(/Street address/).fill(street);
      await page.getByLabel(/City/).fill("Toronto");
      await page.getByLabel(/Address kind/).click();
      await page.getByRole("option", { name: kind }).click();
      await page.getByRole("button", { name: /^Add AccusedAddress/ }).click();
      await expect(page.getByRole("rowheader", { name: new RegExp(street) })).toBeVisible();
    }
    await expect(page.getByRole("heading", { name: /Addresses \(2\)/ })).toBeVisible();

    // Edit with optimistic concurrency, then view.
    await page.getByRole("link", { name: "Edit" }).first().click();
    await page.getByLabel(/Given name/).fill("Avery-Edited");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Avery-Edited")).toBeVisible();
    await expect(page.getByText("Revision 2")).toBeVisible();

    // The published collection is visible on the record with the designer's limits.
    await expect(page.getByRole("heading", { name: /Case documents \(0 of 10\)/ })).toBeVisible();
    await expect(page.getByText(/Up to 10 files, 20\.0 MB each; types: application\/pdf, text\/plain/)).toBeVisible();
    // Upload two documents concurrently, navigate away mid-flight, come back: the shell-owned manager finished them.
    const input = page.getByLabel(/Choose files for Case documents/);
    await input.setInputFiles([
      { name: "warrant.txt", mimeType: "text/plain", buffer: Buffer.from(`synthetic warrant ${run}`) },
      { name: "statement.txt", mimeType: "text/plain", buffer: Buffer.from(`synthetic statement ${run}`.repeat(2000)) },
    ]);
    await page.getByRole("link", { name: "Uploads" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Upload Center" })).toBeVisible();
    await expect(page.getByText("warrant.txt").first()).toBeVisible();
    // SPA navigation back (a full reload would discard the in-memory files by design; server sessions stay resumable).
    await page.getByRole("link", { name: "warrant.txt" }).click();
    await expect(page).toHaveURL(recordUrl);
    await expect(page.getByRole("rowheader", { name: "warrant.txt" })).toBeVisible({ timeout: 120_000 });
    await expect(page.getByRole("rowheader", { name: "statement.txt" })).toBeVisible({ timeout: 120_000 });
    const warrantRow = page.getByRole("row", { name: /warrant\.txt/ });
    await expect(warrantRow.getByText("Clean")).toBeVisible();
    await expect(warrantRow.getByText("Ready")).toBeVisible();
    const download = page.waitForEvent("download");
    await warrantRow.getByRole("link", { name: "Download" }).click();
    const file = await download;
    expect(await file.failure()).toBeNull();
    expect(file.suggestedFilename()).toBe("warrant.txt");

    // EICAR is rejected by the real scanner and never becomes a document.
    await input.setInputFiles([{ name: "eicar.txt", mimeType: "text/plain", buffer: Buffer.from("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*") }]);
    await expect(page.getByText("MALWARE_DETECTED").first()).toBeVisible({ timeout: 120_000 });
    await expect(page.getByRole("rowheader", { name: "eicar.txt" })).toHaveCount(0);

    // Activity tab lists the audit projection.
    await page.getByRole("tab", { name: "Activity" }).click();
    await expect(page.getByText(/record created/i).first()).toBeVisible();
    await expect(page.getByText(/record updated/i).first()).toBeVisible();
    await signOut(page);

    // A reader without grants cannot discover or open the record.
    await signIn(page, "reader");
    await page.goto(recordUrl);
    // Undiscoverable records are concealed (404, rendered as a status), denied ones are alerts; either is correct here.
    await expect(page.getByText(/Not found|do not have permission/).first()).toBeVisible();
    await page.goto("/app/records");
    // On a fresh stack the reader sees nothing at all; on a reused stack earlier runs may have left other shares,
    // so the assertion is about this run's record only.
    await expect(page.getByRole("heading", { level: 1, name: "Records" })).toBeVisible();
    await expect(page.getByRole("row", { name: new RegExp(`Accused ${run}`) })).toHaveCount(0);
    await signOut(page);

    // The owner shares one field with the reader (ADR-PB-002 Stage B item 5).
    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await page.getByRole("tab", { name: "Access" }).click();
    await expect(page.getByText("No shares. Only the owner can see this record.")).toBeVisible();
    await page.getByRole("button", { name: "Share fields" }).click();
    const shareDialog = page.getByRole("dialog", { name: "Share fields of this record" });
    await shareDialog.getByRole("combobox", { name: /Person/ }).click();
    await page.getByRole("option", { name: /Riley Reader/ }).click();
    await shareDialog.getByRole("checkbox", { name: "Given name" }).check();
    await shareDialog.getByRole("button", { name: "Share", exact: true }).click();
    await expect(shareDialog).toBeHidden();
    const shareRow = page.getByRole("row", { name: /Riley Reader/ });
    await expect(shareRow).toContainText("given_name");
    await expect(shareRow.getByText("Active")).toBeVisible();
    await signOut(page);

    // Effective access: the reader sees exactly the shared field; child addresses and documents stay invisible.
    await signIn(page, "reader");
    await expect(async () => {
      await page.goto(recordUrl);
      await expect(page.getByText("Partial view (shared fields only)")).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(page.getByText("Avery-Edited")).toBeVisible();
    await expect(page.getByText("Date of birth")).toHaveCount(0);
    await expect(page.getByText("Family name")).toHaveCount(0);
    await expect(page.getByRole("rowheader", { name: /12 King Street W/ })).toHaveCount(0);
    await expect(page.getByRole("rowheader", { name: "warrant.txt" })).toHaveCount(0);
    await page.getByRole("tab", { name: "Access" }).click();
    await expect(page.getByText("Shared fields only (1)")).toBeVisible();
    await expect(page.getByRole("button", { name: "Share fields" })).toHaveCount(0);
    await page.goto("/app/records");
    await expect(page.getByRole("row", { name: new RegExp(`Accused ${run}`) })).toHaveCount(1);
    await signOut(page);

    // Revocation takes effect immediately.
    await signIn(page, "clerk");
    await page.goto(recordUrl);
    await page.getByRole("tab", { name: "Access" }).click();
    await page.getByRole("row", { name: /Riley Reader/ }).getByRole("button", { name: "Revoke" }).click();
    const revokeDialog = page.getByRole("dialog", { name: "Revoke this share?" });
    await revokeDialog.getByRole("button", { name: "Revoke share" }).click();
    await expect(revokeDialog).toBeHidden();
    await expect(page.getByRole("row", { name: /Riley Reader/ }).getByText("Revoked")).toBeVisible();
    await signOut(page);

    await signIn(page, "reader");
    await page.goto(recordUrl);
    await expect(page.getByText(/Not found|do not have permission/).first()).toBeVisible();
    await page.goto("/app/records");
    // On a fresh stack the reader sees nothing at all; on a reused stack earlier runs may have left other shares,
    // so the assertion is about this run's record only.
    await expect(page.getByRole("heading", { level: 1, name: "Records" })).toBeVisible();
    await expect(page.getByRole("row", { name: new RegExp(`Accused ${run}`) })).toHaveCount(0);
    await signOut(page);
  });
});
