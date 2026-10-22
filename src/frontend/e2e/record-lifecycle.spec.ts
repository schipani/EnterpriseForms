import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C6 M1 — record lifecycle in the real application (API, Authorization Authority, PostgreSQL ×2; no mocks):
 *  1. The owner submits a record from the Record Workspace (confirmation dialog), sees the Submitted status and the lock.
 *  2. A shared recipient sees the status and the lock but is offered no lifecycle action.
 *  3. Reopening needs a reason; the record is editable again.
 *  4. Archiving hides the record from My Records until archived records are included; unarchive brings it back.
 * Hiding a button is never the control: the API refusal of a locked edit is asserted too.
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-c6-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
    patch: (path: string, data: unknown) => page.request.patch(path, { data, headers: headers() }),
  };
}

type Api = Awaited<ReturnType<typeof apiAs>>;

async function addField(api: Api, formId: string, key: string, name: string): Promise<string> {
  const added = await api.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: key, fieldDisplayName: name, fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null });
  return added["fieldDefinitionId"] as string;
}

const field = (id: string, label: string, required: boolean) => ({ kind: "field", fieldDefinitionId: id, control: "text", label, required, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C6 M1 record lifecycle", () => {
  const formName = `C6 Lifecycle ${run}`;
  let recordId = "";
  let reference = "";

  test("setup: the owner publishes a form with a required field, creates a record and shares one field with the clerk", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const clerkId = users.find((user) => /Casey Clerk/.test(user.displayName))!.userId;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic Phase C6 lifecycle ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const title = await addField(owner, formId, "case_title", "Case title");
    const notes = await addField(owner, formId, "internal_notes", "Internal notes");
    const detail = await owner.get(`/api/v1/form-definitions/${formId}`);
    const draft = detail["draft"] as Json | null;
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [field(title, "Case title", true), field(notes, "Internal notes", false)] }] }) });
    await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "Phase C6 lifecycle" });
    const record = await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Lifecycle case ${run}`, internal_notes: "synthetic note" }, ownerTeamId: null, groups: [] });
    recordId = record["formRecordId"] as string;
    await expect(async () => {
      const projection = await owner.get(`/api/v1/form-records/${recordId}/projection`);
      reference = ((projection["summary"] as Json)["recordReference"] as string) ?? "";
      expect(reference).not.toBe("");
    }).toPass({ timeout: 60_000 });
    await owner.post(`/api/v1/form-records/${recordId}/shares`, { recipientUserId: clerkId, recipientTeamId: null, recipientRole: "Reader", fieldKeys: ["case_title"] });
    await signOut(page);
  });

  test("the owner submits from the Record Workspace and the record is locked", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}`);
    await expect(page.getByRole("button", { name: "Submit" })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole("link", { name: "Edit" })).toBeVisible();
    await page.getByRole("button", { name: "Submit" }).click();
    const dialog = page.getByRole("dialog", { name: "Submit this record?" });
    await expect(dialog.getByText(/locked/)).toBeVisible();
    await axeScan(page, "submit dialog");
    await dialog.getByRole("button", { name: "Submit record" }).click();
    await expect(dialog).toBeHidden({ timeout: 60_000 });
    await expect(page.getByText("Record submitted. It is now locked.")).toBeVisible();
    await expect(page.getByText("This record is submitted")).toBeVisible();
    await expect(page.getByRole("link", { name: "Edit" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Submit", exact: true })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Reopen as draft" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Archive", exact: true })).toBeVisible();
    await axeScan(page, "submitted record");

    // The editor URL is not a back door: it explains the lock, and the API refuses the write itself.
    await page.goto(`/app/records/${recordId}/edit`);
    await expect(page.getByText("This record is submitted and can't be edited")).toBeVisible({ timeout: 60_000 });
    const owner = await apiAs(page);
    const projection = await owner.get(`/api/v1/form-records/${recordId}/projection`);
    const refused = await owner.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: Number((projection["record"] as Json)["contentRevision"]), values: { case_title: "Changed after submit" } });
    expect(refused.status()).toBe(409);
    expect(await refused.text()).toContain("RECORD_NOT_EDITABLE");
    await signOut(page);
  });

  test("a shared recipient sees the status and the lock but no lifecycle actions", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "clerk");
    await expect(async () => {
      await page.goto(`/app/records/${recordId}`);
      await expect(page.getByText("Partial view (shared fields only)")).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(page.getByText("This record is submitted")).toBeVisible();
    for (const name of ["Submit", "Reopen as draft", "Archive", "Unarchive"]) {
      await expect(page.getByRole("button", { name, exact: true })).toHaveCount(0);
    }
    await expect(page.getByText("Internal notes")).toHaveCount(0);
    await signOut(page);
  });

  test("reopening needs a reason, then the record is editable again", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}`);
    await page.getByRole("button", { name: "Reopen as draft" }).click();
    const dialog = page.getByRole("dialog", { name: "Reopen this record as a draft?" });
    const confirm = dialog.getByRole("button", { name: "Reopen as draft" });
    await expect(confirm).toBeDisabled();
    await dialog.getByLabel(/Reason/).fill("A witness statement was missing");
    await expect(confirm).toBeEnabled();
    await confirm.click();
    await expect(dialog).toBeHidden({ timeout: 60_000 });
    await expect(page.getByText("Record reopened as a draft.")).toBeVisible();
    await expect(page.getByRole("link", { name: "Edit" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Submit", exact: true })).toBeVisible();
    await signOut(page);
  });

  test("archiving hides the record from My Records until archived records are included; unarchive restores it", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}`);
    await page.getByRole("button", { name: "Archive", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Archive this record?" });
    await axeScan(page, "archive dialog");
    await dialog.getByRole("button", { name: "Archive record" }).click();
    await expect(dialog).toBeHidden({ timeout: 60_000 });
    await expect(page.getByText("Record archived.")).toBeVisible();
    await expect(page.getByText("This record is archived")).toBeVisible();
    await expect(page.getByRole("button", { name: "Unarchive" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Edit" })).toHaveCount(0);

    // My Records: hidden by default, found with the checkbox, and by the explicit Archived status filter.
    await page.goto("/app/records");
    await expect(page.getByRole("heading", { level: 1, name: "My Records" })).toBeVisible();
    await expect(page.getByText(reference)).toHaveCount(0);
    await page.getByLabel("Include archived records").check();
    await expect(page.getByText(reference)).toBeVisible({ timeout: 60_000 });
    await axeScan(page, "my records with archived");

    await page.goto(`/app/records/${recordId}`);
    await page.getByRole("button", { name: "Unarchive" }).click();
    await page.getByRole("dialog", { name: "Unarchive this record?" }).getByRole("button", { name: "Unarchive record" }).click();
    await expect(page.getByText("Record unarchived.")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole("link", { name: "Edit" })).toBeVisible();
    await page.goto("/app/records");
    await expect(page.getByText(reference)).toBeVisible({ timeout: 60_000 });
    await signOut(page);
  });
});
