import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C6 M2 — trusted record history in the real application (API, Authorization Authority, PostgreSQL ×2; no mocks):
 *  1. The owner sees every saved version in the History tab, oldest first, with who and when, and opens an earlier version
 *     read-only (keyboard only), clearly labelled, with that version's values and status.
 *  2. Values of a field that is no longer in the form stay visible in the earlier version.
 *  3. A field-share reader has no History tab and cannot open a version by URL; neither can a member without access.
 *  4. A version number that does not exist is reported as unavailable.
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-history-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
    patch: async (path: string, data: unknown) => ok(await page.request.patch(path, { data, headers: headers() })),
  };
}

type Api = Awaited<ReturnType<typeof apiAs>>;

const field = (id: string, label: string, required: boolean) => ({ kind: "field", fieldDefinitionId: id, control: "text", label, required, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

async function revisionOf(api: Api, recordId: string): Promise<number> {
  let revision = 0;
  await expect(async () => {
    const projection = await api.get(`/api/v1/form-records/${recordId}/projection`);
    revision = Number((projection["record"] as Json)["contentRevision"]);
    expect(revision).toBeGreaterThan(0);
  }).toPass({ timeout: 60_000 });
  return revision;
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C6 M2 record history", () => {
  const formName = `C6 History ${run}`;
  let recordId = "";

  test("setup: three saved versions (created, edited, submitted); one field shared with the clerk; a field later removed from the form", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const clerkId = users.find((user) => /Casey Clerk/.test(user.displayName))!.userId;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic history ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const title = (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_title", fieldDisplayName: "Case title", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null }))["fieldDefinitionId"] as string;
    const code = (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "legacy_code", fieldDisplayName: "Legacy code", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null }))["fieldDefinitionId"] as string;
    const publish = async (elements: unknown[], currentVersion: number) => {
      const detail = await owner.get(`/api/v1/form-definitions/${formId}`);
      const draft = detail["draft"] as Json | null;
      const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements }] }) });
      await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: currentVersion, changeReason: "history e2e" });
    };
    await publish([field(title, "Case title", true), field(code, "Legacy code", false)], 0);
    recordId = (await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Original title ${run}`, legacy_code: "LC-42" }, ownerTeamId: null, groups: [] }))["formRecordId"] as string;
    await owner.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: await revisionOf(owner, recordId), values: { case_title: `Edited title ${run}`, legacy_code: "LC-42" } });
    // Wait until the edited version is readable (its checkpoint archived) before the next change, as the UI does.
    await expect.poll(() => revisionOf(owner, recordId), { timeout: 60_000 }).toBe(2);
    await owner.post(`/api/v1/form-records/${recordId}/submit`, { expectedContentRevision: 2, expectedStatus: "Draft" });
    await owner.post(`/api/v1/form-records/${recordId}/shares`, { recipientUserId: clerkId, recipientTeamId: null, recipientRole: "Reader", fieldKeys: ["case_title"] });
    await publish([field(title, "Case title", true)], 1); // Legacy code leaves the form
    await signOut(page);
  });

  test("the owner reviews the version list and opens an earlier version read-only with the keyboard", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}?tab=history`);
    const history = page.getByRole("tabpanel", { name: "History" });
    const table = history.getByRole("table", { name: "Saved versions of this record" });
    await expect(table.getByRole("row")).toHaveCount(4); // header + three versions
    const rows = table.getByRole("row");
    await expect(rows.nth(1)).toContainText("Version 1");
    await expect(rows.nth(1)).toContainText("Created");
    await expect(rows.nth(2)).toContainText("Edited");
    await expect(rows.nth(2)).toContainText("from the activity log");
    await expect(rows.nth(3)).toContainText("Submitted");
    await expect(rows.nth(3)).toContainText("Current");
    await expect(rows.nth(1)).toContainText("Avery Owner");
    await axeScan(page, "history tab");

    // Keyboard only: focus the View link of version 1 and press Enter.
    const view = history.getByRole("link", { name: "View version 1 of 3" });
    await view.focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`/app/records/${recordId}/versions/1$`));
    await expect(page.getByText("You are viewing an earlier version: version 1 of 3")).toBeVisible();
    await expect(page.getByText("This page is read-only")).toBeVisible();
    const details = page.getByRole("region", { name: "Details in version 1" });
    await expect(details).toContainText(`Original title ${run}`);
    await expect(details).not.toContainText(`Edited title ${run}`);
    await expect(page.getByRole("region", { name: "About this version" })).toContainText("Status in this version: Draft");
    await expect(page.getByRole("button", { name: /^(Edit|Save|Submit)/ })).toHaveCount(0);

    // The field removed from the form is still shown with the value saved in this version.
    const others = page.getByRole("region", { name: "Other values saved in this version" });
    await expect(others).toContainText("Legacy code");
    await expect(others).toContainText("LC-42");
    await axeScan(page, "version view");

    await page.getByRole("button", { name: "Back to version history" }).click();
    await expect(page.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
  });

  test("the version view works at phone width without horizontal scrolling", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}/versions/2`);
    await expect(page.getByText("You are viewing an earlier version: version 2 of 3")).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  });

  test("a version number that does not exist is unavailable", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}/versions/99`);
    await expect(page.getByText("This version isn't available")).toBeVisible();
  });

  test("a field-share reader and a member without access cannot see history", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/records/${recordId}`);
    await expect(page.getByRole("tab", { name: "Details" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "History" })).toHaveCount(0);
    await page.goto(`/app/records/${recordId}/versions/1`);
    await expect(page.getByText("This version isn't available")).toBeVisible();
    await expect(page.getByText(`Original title ${run}`)).toHaveCount(0);
    await signOut(page);

    await signIn(page, "reader");
    await page.goto(`/app/records/${recordId}/versions/1`);
    await expect(page.getByText("This version isn't available")).toBeVisible();
    await expect(page.getByText(`Original title ${run}`)).toHaveCount(0);
  });
});
