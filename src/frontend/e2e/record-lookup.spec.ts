import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C6 quick lookup on My Records (real API, Authorization Authority, PostgreSQL ×2; no mocks except where stated):
 *  1. The owner finds a record by pasting its reference (Enter) or its full id (Search button) and opens it.
 *  2. Record Overview copies the reference.
 *  3. An unknown reference, and a record the user may not access, both say "No record found" (no difference).
 *  4. An archived record is found by exact lookup although My Records hides it by default.
 *  5. Duplicate references and technical failures: the server response is replaced in the browser (route), because the
 *     server cannot be made to issue two colliding record ids or fail on demand; the server side of both is covered by
 *     .NET tests (PhaseC6RecordLookupTests).
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

async function find(page: Page, text: string, via: "enter" | "button" = "enter") {
  const input = page.getByRole("textbox", { name: "Record reference or ID" });
  await input.fill(text);
  if (via === "enter") {
    await input.press("Enter");
  } else {
    await page.getByRole("search", { name: "Find a record by reference or ID" }).getByRole("button", { name: "Search" }).click();
  }
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C6 quick record lookup", () => {
  const formName = `C6 Lookup ${run}`;
  let recordId = "";
  let archivedId = "";

  test("setup: the owner publishes a form and creates two records; one is archived", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic lookup ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
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

  test("the owner finds a record by reference (Enter) and by full id (Search), opens it and copies its reference", async ({ page, context }) => {
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await signIn(page, "owner");
    await page.goto("/app/records");
    const lookup = page.getByRole("region", { name: "Find a record" });
    await expect(lookup).toBeVisible();

    await find(page, referenceOf(recordId).toLowerCase(), "enter");
    await expect(lookup.getByText("Record found")).toBeVisible();
    await expect(lookup.getByRole("list", { name: "Matching records" }).getByRole("listitem")).toHaveCount(1);

    await find(page, recordId, "button");
    await expect(lookup.getByText("Record found")).toBeVisible();
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);

    await lookup.getByRole("link", { name: new RegExp(`^Open record ${formName} ${referenceOf(recordId)}`) }).click();
    await expect(page).toHaveURL(new RegExp(`/app/records/${recordId}$`));
    const summary = page.getByRole("region", { name: "Record summary" });
    await summary.getByRole("button", { name: `Copy reference ${referenceOf(recordId)}` }).click();
    await expect(summary.getByText("Reference copied")).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(referenceOf(recordId));
  });

  test("unknown references, invalid input and archived records", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    const lookup = page.getByRole("region", { name: "Find a record" });

    await find(page, "REC-00000000");
    await expect(lookup.getByText("No record found")).toBeVisible();

    await find(page, "Lookup case");
    await expect(lookup.getByText("Enter a record reference such as REC-1A2B3C4D or a full record ID.")).toBeVisible();

    // Archived: not in the default table, found by exact lookup with its status.
    await expect(page.getByRole("table", { name: "My Records" }).getByText(referenceOf(archivedId))).toHaveCount(0);
    await find(page, referenceOf(archivedId));
    const match = lookup.getByRole("list", { name: "Matching records" }).getByRole("listitem");
    await expect(match).toHaveCount(1);
    await expect(match).toContainText("Archived");
  });

  test("a member without access gets the same answer as for a record that does not exist", async ({ page }) => {
    await signIn(page, "reader");
    await page.goto("/app/records");
    const lookup = page.getByRole("region", { name: "Find a record" });
    for (const text of [referenceOf(recordId), recordId]) {
      await find(page, text);
      await expect(lookup.getByText("No record found")).toBeVisible();
      await expect(lookup.getByRole("list", { name: "Matching records" })).toHaveCount(0);
    }
  });

  test("duplicate references list every match; a technical failure is not reported as 'not found'", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    const lookup = page.getByRole("region", { name: "Find a record" });
    const item = (id: string, form: string, status: string) => ({ formRecordId: id, formDefinitionId: "00000000-0000-7000-8000-000000000001", formDefinitionName: form, recordStatus: status, createdOn: "2026-10-20T10:00:00Z", modifiedOn: "2026-10-21T10:00:00Z", parentFormRecordId: null, projectionVerified: false, recordReference: "REC-C0FFEE00", createdByDisplayName: "Avery Owner", createdByCurrentUser: true, canEdit: true });

    // Replaced server responses (see the file header for why).
    await page.context().route("**/api/v1/form-records/lookup**", (route) =>
      route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ normalizedQuery: "REC-C0FFEE00", lookupKind: "Reference", matches: [item("01a00000-0000-7000-8000-0000c0ffee00", "Intake A", "Draft"), item("01b00000-0000-7000-8000-0000c0ffee00", "Intake B", "Submitted")], matchCount: 2, unavailableRecordIds: [], authorizationRevision: 1 }) }),
    );
    await find(page, "REC-C0FFEE00");
    await expect(lookup.getByText("2 records have reference REC-C0FFEE00")).toBeVisible();
    await expect(lookup.getByRole("list", { name: "Matching records" }).getByRole("listitem")).toHaveCount(2);
    await expect(page).toHaveURL(/\/app\/records(\?.*)?$/); // nothing was opened automatically

    await page.context().unroute("**/api/v1/form-records/lookup**");
    await page.context().route("**/api/v1/form-records/lookup**", (route) =>
      route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ type: "https://enterpriseforms.invalid/problems/authority-unavailable", title: "AUTHORITY_UNAVAILABLE", status: 503, detail: "the authority is unavailable", correlation_id: "01a00000-0000-7000-8000-000000000099" }) }),
    );
    await find(page, "REC-11111111");
    await expect(lookup.getByText("The lookup could not be completed")).toBeVisible();
    await expect(lookup.getByText("No record found")).toHaveCount(0);
    await expect(lookup.getByRole("button", { name: "Try again" })).toBeVisible();
  });
});
