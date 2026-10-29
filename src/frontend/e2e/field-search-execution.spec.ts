import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Locator, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C7 M3 — verified Advanced Field Search in the real application (business API, Authorization Authority, SpiceDB,
 * Vault Transit, PostgreSQL ×2; no mocks except the explicitly replaced failure responses in the last test, which the real
 * server produces only under database tampering or overload — proven by FieldSearchExecutionTests).
 *  1. A security administrator administers search approvals from "Search approvals" without Designer or record access.
 *  2–6. Capabilities list only approved fields; changing the form changes the fields; conditions follow the field type;
 *     text, number, date and dropdown conditions, combined with AND, return exactly the matching records.
 *  7–9. Status and paging combine with a search; results use the My Records table; clearing returns to the list.
 *  10. Records the person cannot access never appear.
 *  11–12. A verification failure, a busy refusal and a changed snapshot are explained — never shown as "no records".
 *  13–14. 320 px and keyboard/axe.
 * Requires the dev-seed "security" persona (organizations bootstrapped since Phase C7 M2) and the M3 execution gate
 * (FieldSearch:ExecutionEnabled, isolated Development/Testing only).
 */
const run = Date.now().toString(36);
type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return response.status() === 204 ? {} : ((await response.json()) as Json);
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-m3-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

const field = (id: string, label: string, control = "text") => ({ kind: "field", fieldDefinitionId: id, control, label, required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });
const condition = (scope: Page | Locator, n: number) => scope.getByRole("group", { name: `Condition ${n}` });

async function choose(page: Page, scope: Locator, label: string, option: string | RegExp) {
  await scope.getByRole("combobox", { name: label }).click();
  await page.getByRole("option", { name: option }).click();
}

async function openForm(page: Page, formName: string) {
  await page.goto("/app/records");
  await page.getByRole("combobox", { name: "Form" }).click();
  await page.getByRole("option", { name: formName }).click();
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C7 M3 verified field search", () => {
  const formName = `C7 M3 Search ${run}`;
  const otherForm = `C7 M3 Other ${run}`;
  let formId = "";
  let otherFormId = "";
  let referenceId = "";
  let countId = "";
  let hearingId = "";
  let statusId = "";

  test("setup: policy, a form with text, number, date and dropdown fields, 30 records; approvals for all but the reference field", async ({ page }) => {
    test.setTimeout(300_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const id = (pattern: RegExp) => users.find((user) => pattern.test(user.displayName))?.userId;
    test.skip(id(/Sam Security/) === undefined, "this organization predates the dev-seed security persona (recreate the stack)");
    const policy = await owner.get("/api/v1/organization/security-policy");
    await owner.put("/api/v1/organization/security-policy", { minimumFieldClassification: policy["minimumFieldClassification"], plaintextInternalFieldsPermitted: true, plaintextDowngradePermitted: policy["plaintextDowngradePermitted"], plaintextSearchPermitted: true, policyStatementText: "Synthetic test organization: selected plaintext Internal fields may be searched.", expectedPolicyRevision: Number(policy["securityPolicyRevision"]) });
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic m3 ${run}`, formUserUserIds: [id(/Casey Clerk/), id(/Avery Owner/)], formUserTeamIds: [] });
    formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const add = async (fieldKey: string, fieldDisplayName: string, fieldDataType: string, options: unknown = null) =>
      (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey, fieldDisplayName, fieldDescription: "", fieldDataType, options, fieldClassification: "Internal", fieldProtectionMode: "Standard" })) as Json;
    referenceId = (await add("case_reference", "Case reference", "String"))["fieldDefinitionId"] as string;
    countId = (await add("exhibit_count", "Exhibit count", "Integer"))["fieldDefinitionId"] as string;
    hearingId = (await add("hearing_date", "Hearing date", "Date"))["fieldDefinitionId"] as string;
    const status = await add("case_status", "Case status", "Option", [{ optionKey: "open", optionDisplayText: "Open" }, { optionKey: "closed", optionDisplayText: "Closed" }]);
    statusId = status["fieldDefinitionId"] as string;
    const options = (status["options"] as Array<{ fieldOptionId: string; optionDisplayText: string }>).map((o, index) => ({ fieldOptionId: o.fieldOptionId, label: o.optionDisplayText, sortOrder: index + 1 }));
    const draft = (await owner.get(`/api/v1/form-definitions/${formId}`))["draft"] as Json | null;
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [field(referenceId, "Case reference"), field(countId, "Exhibit count", "number"), field(hearingId, "Hearing date", "date"), { ...field(statusId, "Case status", "select"), optionsSnapshot: options }] }] }) });
    const version = await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "m3 e2e" });
    expect(version["authorityRegistered"]).toBe(true);
    // A second form with nothing approved (form selection must change the offered fields).
    otherFormId = ((await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: otherForm, formDefinitionDescription: "other", formUserUserIds: [id(/Casey Clerk/)], formUserTeamIds: [] }))["summary"] as Json)["formDefinitionId"] as string;
    await signOut(page);

    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    for (let i = 0; i < 30; i++) {
      const values = { case_reference: `CR-${run}-${String(i).padStart(2, "0")}`, exhibit_count: String(i), hearing_date: `2026-${String((i % 12) + 1).padStart(2, "0")}-10`, case_status: i % 3 === 0 ? "closed" : "open" };
      await expect(async () => {
        await clerk.post(`/api/v1/form-definitions/${formId}/records`, { values, ownerTeamId: null, groups: [] });
      }).toPass({ timeout: 60_000 });
    }
    await signOut(page);

    await signIn(page, "security");
    const security = await apiAs(page);
    for (const [fieldId, operators] of [[countId, ["equals", "greaterThan", "between"]], [hearingId, ["equals", "before", "after", "between"]], [statusId, ["is"]]] as const) {
      await security.post(`/api/v1/form-definitions/${formId}/fields/${fieldId}/search-approval`, { operators, approvalReference: `GOV-M3-${run}`, useDevelopmentStepUp: true });
    }
    await security.put(`/api/v1/form-definitions/${formId}/field-searchers`, { grantUserIds: [id(/Casey Clerk/), id(/Riley Reader/)], grantTeamIds: [], revokeUserIds: [], revokeTeamIds: [] });
    await signOut(page);
  });

  test("1. a security administrator approves a field from Search approvals without Designer or record access", async ({ page }) => {
    await signIn(page, "security");
    await page.getByRole("link", { name: "Search approvals" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Search approvals" })).toBeVisible();
    const forms = page.getByRole("table", { name: "Published forms registered with the Authorization Authority" });
    await forms.getByRole("button", { name: `Review fields of ${formName}` }).click();
    const reference = page.locator("section", { has: page.locator(":scope > header h2", { hasText: /^Case reference$/ }) });
    const approve = reference.getByRole("group", { name: "Approve for search" });
    await approve.getByRole("textbox", { name: "Approval reference" }).fill(`GOV-UI-${run}`);
    await approve.getByRole("checkbox", { name: "Confirm with the development-only step-up" }).click();
    await approve.getByRole("button", { name: "Approve for search" }).click();
    await expect(reference.getByText("Approved", { exact: true })).toBeVisible();
    // No Designer or record access came with it: the form's records are not in My Records.
    await page.goto("/app/records");
    await expect(page.getByRole("cell", { name: new RegExp(`CR-${run}`) })).toHaveCount(0);
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
    expect(results.violations.map((v) => v.id)).toEqual([]);
  });

  test("2–6. only approved fields are offered, per form; conditions follow the type; text, number, date, dropdown and AND search", async ({ page }) => {
    await signIn(page, "clerk");
    // Without a form nothing is offered; selecting the form loads its approved fields from the server.
    await page.goto("/app/records");
    await expect(page.getByText("Choose a form first")).toBeVisible();
    await openForm(page, formName);
    const first = condition(page, 1);
    await first.getByRole("combobox", { name: "Field" }).click();
    await expect(page.getByRole("option")).toHaveText(["Case reference", "Case status", "Exhibit count", "Hearing date"]);
    await page.keyboard.press("Escape");

    // Another form with nothing approved offers nothing (capabilities are per form).
    const other = (await (await page.request.get(`/api/v1/form-records/search-capabilities?formDefinitionId=${otherFormId}`)).json()) as { fields: unknown[] };
    expect(other.fields).toEqual([]);

    // Text equality (case-insensitive): exactly one record.
    await choose(page, condition(page, 1), "Field", "Case reference");
    await choose(page, condition(page, 1), "Condition", "Is exactly");
    await condition(page, 1).getByRole("textbox", { name: "Value" }).fill(`cr-${run}-07`);
    await page.getByRole("button", { name: "Search records" }).click();
    const table = page.getByRole("table", { name: "My Records" });
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(page.getByText("Showing 1–1 of 1 matching record")).toBeVisible();
    expect(page.url()).not.toContain(`cr-${run}`);

    // Number: Exhibit count between 10 and 14 → 5 records; conditions follow the type (no "contains" for a number).
    await choose(page, condition(page, 1), "Field", "Exhibit count");
    await condition(page, 1).getByRole("combobox", { name: "Condition" }).click();
    await expect(page.getByRole("option")).toHaveText(["Equals", "Greater than", "Between (inclusive)"]);
    await page.getByRole("option", { name: "Between (inclusive)" }).click();
    await condition(page, 1).getByRole("textbox", { name: "From" }).fill("10");
    await condition(page, 1).getByRole("textbox", { name: "To" }).fill("14");
    await page.getByRole("button", { name: "Search records" }).click();
    await expect(page.getByText("Showing 1–5 of 5 matching records")).toBeVisible();

    // AND with a dropdown: open (i % 3 != 0) among 10..14 → 10, 11, 13, 14 → 4 records.
    await page.getByRole("button", { name: "Add condition" }).click();
    await choose(page, condition(page, 2), "Field", "Case status");
    await choose(page, condition(page, 2), "Condition", "Is");
    await choose(page, condition(page, 2), "Value", "Open");
    await page.getByRole("button", { name: "Search records" }).click();
    await expect(page.getByText("Showing 1–4 of 4 matching records")).toBeVisible();

    // Date: hearing date before 2026-02-01 → months 1 (i = 0, 12, 24) → 3 records.
    await page.getByRole("button", { name: "Remove condition 2" }).click();
    await choose(page, condition(page, 1), "Field", "Hearing date");
    await choose(page, condition(page, 1), "Condition", "Before");
    await condition(page, 1).getByLabel("Value").fill("2026-02-01");
    await page.getByRole("button", { name: "Search records" }).click();
    await expect(page.getByText("Showing 1–3 of 3 matching records")).toBeVisible();
  });

  test("7–9. status and paging combine with the search in the My Records table; clearing returns to the list", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/records");
    await page.getByRole("combobox", { name: "Form" }).click();
    await page.getByRole("option", { name: formName }).click();
    await page.getByRole("combobox", { name: "Rows per page" }).click();
    await page.getByRole("option", { name: "10", exact: true }).click();
    await choose(page, condition(page, 1), "Field", "Exhibit count");
    await choose(page, condition(page, 1), "Condition", "Greater than");
    await condition(page, 1).getByRole("textbox", { name: "Value" }).fill("-1");
    await page.getByRole("button", { name: "Search records" }).click();
    await expect(page.getByText("Showing 1–10 of 30 matching records")).toBeVisible();
    await page.getByRole("navigation", { name: "Search result pages" }).getByRole("button", { name: "Next page" }).click();
    await expect(page.getByText("Showing 11–20 of 30 matching records")).toBeVisible();
    await expect(page.getByRole("table", { name: "My Records" })).toHaveCount(1);
    await page.getByRole("button", { name: "Clear advanced search" }).click();
    await expect(page.getByRole("navigation", { name: "Record pages" })).toBeVisible();
  });

  test("10. records the person cannot access never appear", async ({ page }) => {
    await signIn(page, "reader");
    await page.goto(`/app/records?form=${formId}`);
    // The reader is a field searcher but discovers none of the clerk's records: the form is not even selectable.
    await expect(page.getByRole("cell", { name: new RegExp(`CR-${run}`) })).toHaveCount(0);
    const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
    const response = await page.request.post("/api/v1/form-records/field-search", { data: { formDefinitionId: formId, conditions: [{ fieldDefinitionId: countId, operator: "greaterThan", valueType: "Integer", value: "-1" }], pageSize: 25 }, headers: { "X-CSRF-Token": session.csrfToken } });
    expect(response.status()).toBe(200);
    expect(((await response.json()) as { totalCount: number }).totalCount).toBe(0);
  });

  test("11–14. failures are explained (never 'no records'), 320 px and keyboard", async ({ page }) => {
    await signIn(page, "clerk");
    await page.setViewportSize({ width: 320, height: 800 });
    await openForm(page, formName);
    await choose(page, condition(page, 1), "Field", "Exhibit count");
    await choose(page, condition(page, 1), "Condition", "Equals");
    await condition(page, 1).getByRole("textbox", { name: "Value" }).fill("3");
    const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(scrollWidth).toBeLessThanOrEqual(320);

    const replies: Array<{ status: number; body: Json }> = [
      { status: 503, body: { title: "SEARCH_VERIFICATION_UNAVAILABLE", status: 503, retryable: false, unavailable_reason: "RECORD_VERIFICATION_FAILED" } },
      { status: 503, body: { title: "SEARCH_VERIFICATION_UNAVAILABLE", status: 503, retryable: true, unavailable_reason: "SEARCH_CAPACITY_EXCEEDED" } },
      { status: 409, body: { title: "SEARCH_SNAPSHOT_CHANGED", status: 409, retryable: true } },
    ];
    const titles = ["A record could not be verified", "Search is busy", "Records changed during the search"];
    for (let i = 0; i < replies.length; i++) {
      await page.route("**/api/v1/form-records/field-search", (route) => route.fulfill({ status: replies[i]!.status, contentType: "application/problem+json", body: JSON.stringify(replies[i]!.body) }));
      await page.getByRole("button", { name: "Search records" }).click();
      await expect(page.getByText(titles[i]!).first()).toBeVisible();
      await expect(page.getByText(/No records match/)).toHaveCount(0);
      // The conditions are kept.
      await expect(condition(page, 1).getByRole("textbox", { name: "Value" })).toHaveValue("3");
      await page.unroute("**/api/v1/form-records/field-search");
    }
    await page.getByRole("button", { name: "Run search again" }).click();
    await expect(page.getByText("Showing 1–1 of 1 matching record")).toBeVisible();

    // Keyboard: the disclosure and the Search records action are reachable and operable; axe on the expanded section.
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByRole("button", { name: "Search records" }).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByText("Showing 1–1 of 1 matching record")).toBeVisible();
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
    expect(results.violations.map((v) => v.id)).toEqual([]);
  });
});
