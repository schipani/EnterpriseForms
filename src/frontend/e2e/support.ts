import { expect, type Locator, type Page } from "@playwright/test";

export type Persona = "owner" | "designer" | "clerk" | "reader";

/** Development-login persona buttons end in "(<key>)"; the dev-seed organization is shared across specs, so each spec creates uniquely named forms. */
export async function signIn(page: Page, persona: Persona) {
  await page.goto("/login");
  await page.getByRole("button", { name: new RegExp("\\(" + persona + "\\)$") }).click();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
}

export async function signOut(page: Page) {
  await page.getByRole("button", { name: /Account menu/ }).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
}

export async function addField(page: Page, name: string, type: string) {
  await page.getByRole("tab", { name: /^Fields/ }).click();
  await page.getByRole("button", { name: "Add field", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Add field" });
  await dialog.getByLabel("Display name").fill(name);
  await dialog.getByLabel("Storage type").click();
  await page.getByRole("option", { name: new RegExp(`^${type}$`) }).click();
  await dialog.getByRole("button", { name: "Add field" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("rowheader", { name })).toBeVisible();
}

export async function addDocumentCollection(page: Page, name: string) {
  await page.getByRole("tab", { name: /^Fields/ }).click();
  await page.getByRole("button", { name: "Add document collection" }).click();
  const dialog = page.getByRole("dialog", { name: "Add document collection" });
  await dialog.getByLabel("Display name").fill(name);
  await dialog.getByRole("button", { name: "Add document collection" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("row", { name: new RegExp(name) })).toContainText("File");
}

/**
 * Places an existing (unplaced) field, document collection or related list on the Design canvas through the C1
 * designer's real UI: click the last "+ Add Field" cell of a section (default: the last section), choose the item in
 * the inspector's "Place an existing field" picker, and place it. Related lists and document collections default to a
 * full row, fields to the width given (default 1 column). `pickerLabel` is kept for call-site readability only.
 */
export async function placeFromPicker(page: Page, pickerLabel: string, optionText: RegExp, options: { section?: string; width?: string } = {}) {
  await page.getByRole("tab", { name: "Design" }).click();
  const canvas = page.getByRole("region", { name: "Form canvas" });
  const scope = options.section ? canvas.getByRole("region", { name: options.section, exact: true }) : canvas;
  await scope.getByRole("button", { name: /^Add Field to / }).last().click();
  const inspector = page.getByRole("complementary", { name: "Add Field" });
  await inspector.getByLabel("Place an existing field").click();
  await page.getByRole("option", { name: optionText }).click();
  const width = options.width ?? (pickerLabel === "Add field" ? undefined : "4 columns (full row)");
  if (width) {
    await inspector.getByLabel("Width").click();
    await page.getByRole("option", { name: width }).click();
  }
  await inspector.getByRole("button", { name: "Place field here" }).click();
  await expect(page.getByRole("complementary", { name: /Properties$/ })).toBeVisible();
}

/** Selects a placed tile on the Design canvas by its visible label; the inspector then shows its properties. */
export async function selectTile(page: Page, label: string) {
  await page.getByRole("tab", { name: "Design" }).click();
  await page.getByRole("region", { name: "Form canvas" }).getByRole("button", { name: label, exact: true }).click();
  const inspector = page.getByRole("complementary", { name: /Properties$/ });
  await expect(inspector).toBeVisible();
  return inspector;
}

export async function saveAndPublish(page: Page, reason: string) {
  await page.getByRole("button", { name: "Save draft" }).click();
  await expect(page.getByText("Unsaved changes")).toBeHidden();
  await publish(page, reason);
}

export async function publish(page: Page, reason: string) {
  await page.getByRole("button", { name: /^Publish/ }).click();
  const dialog = page.getByRole("dialog", { name: "Publish a new version" });
  await dialog.getByLabel("Change reason").fill(reason);
  await dialog.getByRole("button", { name: "Publish", exact: true }).click();
  await expect(dialog).toBeHidden();
}

/**
 * Selects a tile (same as `selectTile`), then opens the Configure Field modal from the quick inspector's prominent
 * button and, if given, switches to `tab` (Validation / Conditional Rules / Dropdown Choices / Placement & Alignment /
 * Security & Encryption / Advanced). Returns the modal dialog locator (Phase C3 designer UX refinement).
 */
export async function openFieldConfig(page: Page, label: string, tab?: string): Promise<Locator> {
  const quick = await selectTile(page, label);
  await quick.getByRole("button", { name: "Configure Field" }).click();
  const modal = page.getByRole("dialog", { name: new RegExp(`^Configure `) });
  await expect(modal).toBeVisible();
  if (tab) {
    await modal.getByRole("tab", { name: tab }).click();
  }
  return modal;
}

export function formIdFromUrl(url: string): string {
  const match = /\/app\/forms\/([0-9a-f-]{36})/.exec(url);
  if (!match) {
    throw new Error("no form id in " + url);
  }
  return match[1]!;
}
