import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C6 M3 — restoring an earlier version in the real application (API, Authorization Authority, PostgreSQL ×2; no mocks):
 *  1. The owner of a Draft record previews a restore (current against restored values, what is not restored), must give a
 *     reason, confirms, and sees the new version with links to the editor and the history; the history keeps every version.
 *  2. A Submitted or an Archived record explains why it can't be restored and offers no Restore button.
 *  3. Casey (a field-share contributor) never sees a Restore action, not even by opening the version URL.
 *  4. The dialog works at 320 px and with the keyboard only (focus returns to the Restore button on Escape).
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-restore-${run}-${Math.random().toString(36).slice(2)}` });
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

/** Waits until the record's head is readable (its checkpoint archived) and returns its revision, as the UI does before the next change. */
async function readableRevision(api: Api, recordId: string, atLeast = 1): Promise<number> {
  let revision = 0;
  await expect(async () => {
    const projection = await api.get(`/api/v1/form-records/${recordId}/projection`);
    revision = Number((projection["record"] as Json)["contentRevision"]);
    expect(revision).toBeGreaterThanOrEqual(atLeast);
  }).toPass({ timeout: 60_000 });
  return revision;
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C6 M3 restore an earlier version", () => {
  const formName = `C6 Restore ${run}`;
  let draftRecord = "";
  let phoneRecord = "";
  let submittedRecord = "";
  let archivedRecord = "";

  test("setup: Draft, Submitted and Archived records with two versions each; one field shared with Casey as a contributor", async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const clerkId = users.find((user) => /Casey Clerk/.test(user.displayName))!.userId;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic restore ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const title = (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_title", fieldDisplayName: "Case title", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null }))["fieldDefinitionId"] as string;
    const notes = (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_notes", fieldDisplayName: "Case notes", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null }))["fieldDefinitionId"] as string;
    const draft = (await owner.get(`/api/v1/form-definitions/${formId}`))["draft"] as Json | null;
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [field(title, "Case title", true), field(notes, "Case notes", false)] }] }) });
    await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "restore e2e" });

    const twoVersions = async (label: string) => {
      const id = (await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Original ${label} ${run}`, case_notes: `First note ${label}` }, ownerTeamId: null, groups: [] }))["formRecordId"] as string;
      await owner.patch(`/api/v1/form-records/${id}`, { expectedContentRevision: await readableRevision(owner, id), values: { case_title: `Edited ${label} ${run}`, case_notes: "" } });
      await readableRevision(owner, id, 2);
      return id;
    };
    draftRecord = await twoVersions("draft");
    phoneRecord = await twoVersions("phone");
    submittedRecord = await twoVersions("submitted");
    await owner.post(`/api/v1/form-records/${submittedRecord}/submit`, { expectedContentRevision: 2, expectedStatus: "Draft" });
    archivedRecord = await twoVersions("archived");
    await owner.post(`/api/v1/form-records/${archivedRecord}/archive`, { expectedContentRevision: 2, expectedStatus: "Draft", reason: "restore e2e" });
    await owner.post(`/api/v1/form-records/${draftRecord}/shares`, { recipientUserId: clerkId, recipientTeamId: null, recipientRole: "Contributor", fieldKeys: ["case_title"] });
    await signOut(page);
  });

  test("the owner previews, gives a reason, confirms and sees the new version; the history keeps every version", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records/${draftRecord}/versions/1`);
    await expect(page.getByText("You are viewing an earlier version: version 1 of 2")).toBeVisible();
    await page.getByRole("button", { name: "Restore this version" }).click();

    const dialog = page.getByRole("dialog", { name: "Restore version 1?" });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText("Earlier versions, including the current one, are kept");
    await expect(dialog).toContainText("restoring creates version 3");
    await expect(dialog).toContainText("Avery Owner");

    // Before/after comparison.
    const compare = dialog.getByRole("table", { name: "Current and restored values" });
    const titleRow = compare.getByRole("row", { name: /Case title/ });
    await expect(titleRow).toContainText(`Edited draft ${run}`);
    await expect(titleRow).toContainText(`Original draft ${run}`);
    await expect(titleRow).toContainText("Value changes");
    const notesRow = compare.getByRole("row", { name: /Case notes/ });
    await expect(notesRow).toContainText("Empty");
    await expect(notesRow).toContainText("First note draft");
    await expect(notesRow).toContainText("Value comes back");
    await expect(dialog.getByRole("region", { name: "Not restored" })).toContainText("Sharing, access and ownership stay as they are now.");
    await expect(dialog.getByRole("region", { name: "Not restored" })).toContainText("Documents are not restored.");
    await axeScan(page, "restore dialog");

    // The reason is mandatory.
    const confirm = dialog.getByRole("button", { name: "Restore version 1" });
    await confirm.click();
    await expect(dialog.getByText("Enter a reason for restoring this version.")).toBeVisible();
    await expect(dialog).toBeVisible();
    await dialog.getByRole("textbox", { name: /Reason for restoring/ }).fill("The edit removed the notes by mistake");
    await confirm.click();

    const done = page.getByText("Version 1 was restored as version 3");
    await expect(done).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole("button", { name: "Restore this version" })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Edit the record" })).toBeVisible();
    await page.getByRole("link", { name: "Version history", exact: true }).click();

    const table = page.getByRole("tabpanel", { name: "History" }).getByRole("table", { name: "Saved versions of this record" });
    await expect(table.getByRole("row")).toHaveCount(4, { timeout: 30_000 }); // header + three versions
    const rows = table.getByRole("row");
    await expect(rows.nth(2)).toContainText("Edited");
    await expect(rows.nth(3)).toContainText("Restored version 1");
    await expect(rows.nth(3)).toContainText("from the activity log");
    await expect(rows.nth(3)).toContainText("Current");

    // The current record has version 1's values and stays a draft that opens in the editor.
    await page.goto(`/app/records/${draftRecord}/edit`);
    await expect(page.getByRole("textbox", { name: /Case title/ })).toHaveValue(`Original draft ${run}`, { timeout: 30_000 });
    await expect(page.getByRole("textbox", { name: /Case notes/ })).toHaveValue("First note draft");
  });

  test("a Submitted record and an Archived record explain why and offer no Restore button", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records/${submittedRecord}/versions/1`);
    await expect(page.getByText("This version can't be restored while the record is submitted")).toBeVisible();
    await expect(page.getByText("Reopen the record as a draft first")).toBeVisible();
    await expect(page.getByRole("button", { name: "Restore this version" })).toHaveCount(0);

    await page.goto(`/app/records/${archivedRecord}/versions/1`);
    await expect(page.getByText("This version can't be restored while the record is archived")).toBeVisible();
    await expect(page.getByRole("button", { name: "Restore this version" })).toHaveCount(0);
  });

  test("Casey, a field-share contributor, never sees a Restore action", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/records/${draftRecord}`);
    await expect(page.getByRole("tab", { name: "Details" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "History" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Restore this version" })).toHaveCount(0);
    await page.goto(`/app/records/${draftRecord}/versions/1`);
    await expect(page.getByText("This version isn't available")).toBeVisible();
    await expect(page.getByRole("button", { name: "Restore this version" })).toHaveCount(0);
  });

  test("the dialog fits a 320 px screen and works with the keyboard only", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await signIn(page, "owner");
    await page.goto(`/app/records/${phoneRecord}/versions/1`);
    const restore = page.getByRole("button", { name: "Restore this version" });
    await expect(restore).toBeVisible();
    await restore.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Restore version 1?" });
    await expect(dialog.getByRole("table", { name: "Current and restored values" })).toBeVisible();
    const box = await dialog.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(321);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await axeScan(page, "restore dialog at 320 px");

    // Keyboard: reach the reason field by Tab, type, and Escape closes without restoring; focus returns to the button.
    const reason = dialog.getByRole("textbox", { name: /Reason for restoring/ });
    for (let i = 0; i < 25 && !(await reason.evaluate((el) => el === document.activeElement)); i++) {
      await page.keyboard.press("Tab");
    }
    await expect(reason).toBeFocused();
    await page.keyboard.type("checking the keyboard path");
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
    await expect(restore).toBeFocused();
    await expect(page.getByText("You are viewing an earlier version: version 1 of 2")).toBeVisible(); // nothing was restored
  });
});
