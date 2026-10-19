import { expect, type Page } from "@playwright/test";

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
  await page.getByRole("button", { name: "Add field" }).first().click();
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

export async function placeFromPicker(page: Page, pickerLabel: string, optionText: RegExp) {
  await page.getByRole("tab", { name: "Layout" }).click();
  await page.getByLabel(pickerLabel, { exact: true }).click();
  await page.getByRole("option", { name: optionText }).click();
  await page.getByRole("button", { name: pickerLabel, exact: true }).click();
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

export function formIdFromUrl(url: string): string {
  const match = /\/app\/forms\/([0-9a-f-]{36})/.exec(url);
  if (!match) {
    throw new Error("no form id in " + url);
  }
  return match[1]!;
}
