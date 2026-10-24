import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Activity log and History for people with limited access (Phase C6). A field-share recipient (Casey Clerk) can read only the
 * shared fields: the record page offers no Activity or History, no page shows a technical NOT_FOUND or a correlation id, and
 * the server still refuses the data. The owner keeps Activity and History. An expected refusal (access removed while the
 * tab was open) shows a plain unavailable state; a genuine service failure keeps the error panel with a retry.
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-activity-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

const TECHNICAL = /NOT_FOUND|correlation|Not found|record not found/i;

test.describe.configure({ mode: "serial" });

test.describe("Phase C6 activity log for field-scoped users", () => {
  const formName = `C6 Activity ${run}`;
  let recordId = "";

  test("setup: the owner creates a record and shares one field with the clerk", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const clerkId = users.find((user) => /Casey Clerk/.test(user.displayName))!.userId;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic activity ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const added = await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_title", fieldDisplayName: "Case title", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null });
    const detail = await owner.get(`/api/v1/form-definitions/${formId}`);
    const draft = detail["draft"] as Json | null;
    const element = { kind: "field", fieldDefinitionId: added["fieldDefinitionId"], control: "text", label: "Case title", required: true, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} };
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [element] }] }) });
    await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "activity" });
    recordId = (await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Activity case ${run}` }, ownerTeamId: null, groups: [] }))["formRecordId"] as string;
    await expect(async () => {
      const projection = await owner.get(`/api/v1/form-records/${recordId}/projection`);
      expect(Number((projection["record"] as Json)["contentRevision"])).toBeGreaterThan(0);
    }).toPass({ timeout: 60_000 });
    await owner.post(`/api/v1/form-records/${recordId}/shares`, { recipientUserId: clerkId, recipientTeamId: null, recipientRole: "Reader", fieldKeys: ["case_title"] });
    await signOut(page);
  });

  test("Casey sees the shared field but no Activity or History tab, and no technical error on any tab", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/records/${recordId}`);
    await expect(page.getByRole("tab", { name: "Details" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Activity" })).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "History" })).toHaveCount(0);
    await expect(page.getByText(`Activity case ${run}`).first()).toBeVisible();

    for (const tab of ["Details", "Access"]) {
      await page.getByRole("tab", { name: tab }).click();
      await expect(page.getByRole("tabpanel")).toBeVisible();
      await expect(page.locator("main")).not.toContainText(TECHNICAL);
    }

    // A direct address for a section she can't use falls back to Details without any technical message.
    await page.goto(`/app/records/${recordId}?tab=activity`);
    await expect(page.getByRole("tab", { name: "Details" })).toHaveAttribute("aria-selected", "true");
    await expect(page.locator("main")).not.toContainText(TECHNICAL);

    // The server still refuses the data itself (no weakening): activity and history are not available to her.
    const activity = await page.request.get(`/api/v1/form-records/${recordId}/activity`);
    expect(activity.status()).toBe(404);
    const versions = await page.request.get(`/api/v1/form-records/${recordId}/versions`);
    expect(versions.status()).toBe(404);
  });

  test("the owner keeps Activity and History", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}`);
    await page.getByRole("tab", { name: "Activity" }).click();
    await expect(page.getByRole("tabpanel", { name: "Activity" })).toContainText("record created");
    await page.getByRole("tab", { name: "History" }).click();
    await expect(page.getByRole("table", { name: "Saved versions of this record" })).toBeVisible();
  });

  test("an expected refusal shows a plain unavailable state; a real service failure keeps the error panel with a retry", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}`);
    await page.context().route("**/api/v1/form-records/*/activity**", (route) =>
      route.fulfill({ status: 404, contentType: "application/problem+json", body: JSON.stringify({ type: "https://enterpriseforms.invalid/problems/not-found", title: "NOT_FOUND", status: 404, detail: "record not found", correlation_id: "01a00000-0000-7000-8000-000000000001" }) }),
    );
    await page.getByRole("tab", { name: "Activity" }).click();
    await expect(page.getByText("Activity isn't available for this record")).toBeVisible();
    await expect(page.getByRole("tabpanel", { name: "Activity" })).not.toContainText(TECHNICAL);

    await page.context().unroute("**/api/v1/form-records/*/activity**");
    await page.context().route("**/api/v1/form-records/*/activity**", (route) =>
      route.fulfill({ status: 500, contentType: "application/problem+json", body: JSON.stringify({ type: "https://enterpriseforms.invalid/problems/internal", title: "INTERNAL_ERROR", status: 500, detail: "internal error", correlation_id: "01a00000-0000-7000-8000-000000000002" }) }),
    );
    await page.getByRole("tab", { name: "Details" }).click();
    await page.getByRole("tab", { name: "Activity" }).click();
    await expect(page.getByText("The request failed")).toBeVisible();
    await expect(page.getByRole("button", { name: "Try again" })).toBeVisible();
    await expect(page.getByText("Activity isn't available for this record")).toHaveCount(0);
  });
});
