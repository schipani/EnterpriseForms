import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C6 unified My Records search (real API, Authorization Authority, PostgreSQL ×2; no mocks except where stated).
 * One search/filter area and ONE table:
 *  1. An exact reference (Enter) or full id (Search button) narrows the table to that record; Open works from the row.
 *  2. Unknown references and invalid input; an archived record is hidden by default but found by exact search; the other
 *     filters narrow the search (AND); Clear removes only the search; Clear filters resets everything.
 *  3. A member without access gets the same empty result as for a record that does not exist.
 *  4. Duplicate references list every match in the same table; a technical failure is not reported as "no record found".
 *     (Replaced server responses, because the real server cannot be made to issue colliding ids or fail on demand; the
 *     server side of both is covered by the .NET tests.)
 * Copy reference on Record Overview feeds the search.
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-lookup-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

const referenceOf = (id: string) => "REC-" + id.replace(/-/g, "").slice(-8).toUpperCase();

const searchBox = (page: Page) => page.getByRole("textbox", { name: "Search records" });
const searchButton = (page: Page) => page.getByRole("search", { name: "Search records by reference or ID" }).getByRole("button", { name: "Search", exact: true });
const table = (page: Page) => page.getByRole("table", { name: "My Records" });

async function find(page: Page, text: string, via: "enter" | "button" = "enter") {
  await searchBox(page).fill(text);
  if (via === "enter") {
    await searchBox(page).press("Enter");
  } else {
    await searchButton(page).click();
  }
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C6 unified My Records search", () => {
  const formName = `C6 Lookup ${run}`;
  let recordId = "";
  let archivedId = "";
  let formId = "";

  test("setup: the owner publishes a form and creates two records; one is archived", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic lookup ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const added = await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_title", fieldDisplayName: "Case title", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null });
    const detail = await owner.get(`/api/v1/form-definitions/${formId}`);
    const draft = detail["draft"] as Json | null;
    const element = { kind: "field", fieldDefinitionId: added["fieldDefinitionId"], control: "text", label: "Case title", required: true, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} };
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [element] }] }) });
    await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "lookup" });
    recordId = (await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Lookup case ${run}` }, ownerTeamId: null, groups: [] }))["formRecordId"] as string;
    archivedId = (await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Archived lookup case ${run}` }, ownerTeamId: null, groups: [] }))["formRecordId"] as string;
    let revision = 0;
    await expect(async () => {
      const projection = await owner.get(`/api/v1/form-records/${archivedId}/projection`);
      revision = Number((projection["record"] as Json)["contentRevision"]);
      expect(revision).toBeGreaterThan(0);
    }).toPass({ timeout: 60_000 });
    await owner.post(`/api/v1/form-records/${archivedId}/archive`, { expectedContentRevision: revision, expectedStatus: "Draft", reason: null });
    await signOut(page);
  });

  test("the owner finds a record by reference (Enter) and by full id (Search) in the one table, opens it and copies its reference", async ({ page, context }) => {
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await signIn(page, "owner");
    await page.goto("/app/records");
    await expect(page.getByRole("region", { name: "Find and filter records" })).toBeVisible();

    await find(page, referenceOf(recordId).toLowerCase(), "enter");
    await expect(table(page).getByRole("row")).toHaveCount(2); // header + the record: the existing table, not a second result list
    await expect(table(page)).toContainText(referenceOf(recordId));
    await expect(page.getByRole("list", { name: "Matching records" })).toHaveCount(0);
    await expect(page).toHaveURL(new RegExp(`q=${referenceOf(recordId)}`));

    await find(page, recordId, "button");
    await expect(table(page).getByRole("row")).toHaveCount(2);
    await expect(table(page)).toContainText(referenceOf(recordId));

    await table(page).getByRole("link", { name: new RegExp(`^Open ${formName} ${referenceOf(recordId)}`) }).click();
    await expect(page).toHaveURL(new RegExp(`/app/records/${recordId}$`));
    const summary = page.getByRole("region", { name: "Record summary" });
    await summary.getByRole("button", { name: `Copy reference ${referenceOf(recordId)}` }).click();
    await expect(summary.getByText("Reference copied")).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(referenceOf(recordId));
  });

  test("unknown references, invalid input (only on submit), archived records and the filters working together", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");

    await find(page, "REC-00000000");
    await expect(page.getByText("No record found")).toBeVisible();

    // Nothing is validated while typing; the message appears on submit.
    await searchBox(page).fill("Lookup c");
    await expect(page.getByRole("alert").filter({ hasText: "Enter a record reference" })).toHaveCount(0);
    await searchBox(page).press("Enter");
    await expect(page.getByRole("alert").filter({ hasText: "Enter a record reference such as REC-1A2B3C4D or a full record ID." })).toBeVisible();
    await searchBox(page).fill("Lookup ca");
    await expect(page.getByRole("alert").filter({ hasText: "Enter a record reference" })).toHaveCount(0); // editing clears it

    // Archived: not in the default table; found by exact search with its status; Status and Form filters narrow the search (AND).
    await page.getByRole("button", { name: "Clear filters" }).first().click();
    await expect(table(page).getByText(referenceOf(archivedId))).toHaveCount(0);
    await find(page, referenceOf(archivedId));
    await expect(table(page).getByRole("row")).toHaveCount(2);
    await expect(table(page)).toContainText("Archived");

    await page.getByRole("combobox", { name: "Status" }).click();
    await page.getByRole("option", { name: "Draft" }).click();
    await expect(page.getByText("No record found")).toBeVisible(); // search AND status
    await page.getByRole("combobox", { name: "Status" }).click();
    await page.getByRole("option", { name: "Archived" }).click();
    await expect(table(page).getByRole("row")).toHaveCount(2);

    // Clear removes only the search; Clear filters resets everything.
    await page.getByRole("search", { name: "Search records by reference or ID" }).getByRole("button", { name: "Clear" }).click();
    await expect(searchBox(page)).toHaveValue("");
    await expect(page).not.toHaveURL(/q=/);
    await expect(page).toHaveURL(/status=Archived/);
    await expect(page.getByRole("combobox", { name: "Status" })).toContainText("Archived");
    await page.getByRole("button", { name: "Clear filters" }).first().click();
    await expect(page).not.toHaveURL(/status=/);
    await expect(page.getByRole("combobox", { name: "Status" })).toContainText("All statuses");
  });

  test("a member without access gets the same answer as for a record that does not exist", async ({ page }) => {
    await signIn(page, "reader");
    await page.goto("/app/records");
    for (const text of [referenceOf(recordId), recordId, "REC-00000000"]) {
      await find(page, text);
      await expect(page.getByText("No record found")).toBeVisible();
      await expect(table(page)).toHaveCount(0);
      await expect(page.getByText(formName)).toHaveCount(0);
    }
  });

  test("duplicate references list every match in the same table; a technical failure is not reported as 'not found'", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    const item = (id: string, form: string, status: string) => ({ formRecordId: id, formDefinitionId: "00000000-0000-7000-8000-000000000001", formDefinitionName: form, recordStatus: status, createdOn: "2026-10-20T10:00:00Z", modifiedOn: "2026-10-21T10:00:00Z", parentFormRecordId: null, projectionVerified: false, recordReference: "REC-C0FFEE00", createdByDisplayName: "Avery Owner", createdByCurrentUser: true, canEdit: true });

    // Replaced server responses (see the file header for why); only requests that carry a search are replaced.
    await page.context().route("**/api/v1/form-records?**", (route) => {
      if (!new URL(route.request().url()).searchParams.has("q")) {
        return route.fallback();
      }
      return route.fulfill({
        status: 200, contentType: "application/json",
        body: JSON.stringify({ items: [item("01a00000-0000-7000-8000-0000c0ffee00", "Intake A", "Draft"), item("01b00000-0000-7000-8000-0000c0ffee00", "Intake B", "Submitted")], authorizationRevision: 1, continuationToken: null, totalCount: 2, pageStart: 1, pageSize: 25, previousToken: null, availableForms: [], unavailableRecordCount: 0, unavailableRecordIds: [], sortBy: "modifiedOn", sortDirection: "desc" }),
      });
    });
    await find(page, "REC-C0FFEE00");
    await expect(table(page).getByRole("row")).toHaveCount(3); // header + both matches, none chosen
    await expect(table(page)).toContainText("Intake A");
    await expect(table(page)).toContainText("Intake B");
    await expect(page).toHaveURL(/\/app\/records(\?.*)?$/); // nothing was opened automatically

    await page.context().unroute("**/api/v1/form-records?**");
    await page.context().route("**/api/v1/form-records?**", (route) => {
      if (!new URL(route.request().url()).searchParams.has("q")) {
        return route.fallback();
      }
      return route.fulfill({ status: 500, contentType: "application/problem+json", body: JSON.stringify({ type: "https://enterpriseforms.invalid/problems/internal", title: "INTERNAL_ERROR", status: 500, detail: "internal error", correlation_id: "01a00000-0000-7000-8000-000000000099" }) });
    });
    await find(page, "REC-11111111");
    await expect(page.getByText("The request failed")).toBeVisible();
    await expect(page.getByText("No record found")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Try again" })).toBeVisible();
  });
});
