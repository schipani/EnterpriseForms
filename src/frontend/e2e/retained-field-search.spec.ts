import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Locator, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C-188 (Option B) in the real application (business API, Authorization Authority, SpiceDB; no mocks). CLAUDE.md §11 #2:
 *  1. Setup: a form whose Phone field was approved for search in v1 and removed in v2; the clerk holds records with phone values.
 *  2. Search approvals lists Phone "(no longer in the form)" with an explanation; a security administrator approves searching its
 *     latest retained values (development-only step-up); the inventory counts it; axe on the section.
 *  3. Advanced Search offers the clerk "Phone (no longer in the form)" and finds the clerk's records by their retained phone values.
 *  4. Withdrawing the approval removes it from Advanced Search at once.
 * Requires the dev-seed "security" persona and FieldSearch:RetainedFieldApprovals=Enabled (compose.security-test.yaml only).
 */
const run = Date.now().toString(36);
type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  const body = await response.text(); // the field-searchers PUT answers 204 with no body
  return (body === "" ? {} : JSON.parse(body)) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-c188-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

const field = (id: string, label: string) => ({ kind: "field", fieldDefinitionId: id, control: "text", label, required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });
const condition = (scope: Page | Locator, n: number) => scope.getByRole("group", { name: `Condition ${n}` });

test.describe.configure({ mode: "serial" });

test.describe("C-188 retained-field search", () => {
  const formName = `C188 Retained ${run}`;
  let formId = "";

  test("setup: Phone approved in v1, clerk records with phone values, Phone removed in v2", async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page, "owner");
    let owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const id = (pattern: RegExp) => users.find((user) => pattern.test(user.displayName))?.userId;
    test.skip(id(/Sam Security/) === undefined, "this organization predates the dev-seed security persona (recreate the stack)");
    const policy = await owner.get("/api/v1/organization/security-policy");
    await owner.put("/api/v1/organization/security-policy", { minimumFieldClassification: policy["minimumFieldClassification"], plaintextInternalFieldsPermitted: true, plaintextDowngradePermitted: policy["plaintextDowngradePermitted"], plaintextSearchPermitted: true, policyStatementText: "Synthetic test organization: selected plaintext Internal fields may be searched.", expectedPolicyRevision: Number(policy["securityPolicyRevision"]) });
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic c188 ${run}`, formUserUserIds: [id(/Casey Clerk/), id(/Avery Owner/)], formUserTeamIds: [] });
    formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const add = async (fieldKey: string, fieldDisplayName: string) => ((await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey, fieldDisplayName, fieldDescription: "", fieldDataType: "String", options: null, fieldClassification: "Internal", fieldProtectionMode: "Standard" })) as Json)["fieldDefinitionId"] as string;
    const first = await add("first_name", "First name");
    const phone = await add("phone_number", "Phone");
    const publish = async (elements: unknown[], reason: string, expectedCurrentVersionNumber: number) => {
      const draft = (await owner.get(`/api/v1/form-definitions/${formId}`))["draft"] as Json | null;
      const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Main", gridColumns: 4, elements }] }) });
      const version = await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber, changeReason: reason });
      expect(version["authorityRegistered"]).toBe(true);
    };
    await publish([field(first, "First name"), field(phone, "Phone")], "c188 v1", 0);
    await signOut(page);

    await signIn(page, "security");
    const security = await apiAs(page);
    await security.post(`/api/v1/form-definitions/${formId}/fields/${phone}/search-approval`, { operators: ["equals", "startsWith"], approvalReference: `GOV-C188-${run}`, useDevelopmentStepUp: true });
    await security.put(`/api/v1/form-definitions/${formId}/field-searchers`, { grantUserIds: [id(/Casey Clerk/)], grantTeamIds: [], revokeUserIds: [], revokeTeamIds: [] });
    await signOut(page);

    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    for (const [name, number] of [["Avery", "555-0101"], ["Blake", "555-0102"], ["Casey", "555-0900"]] as const) {
      await expect(async () => {
        await clerk.post(`/api/v1/form-definitions/${formId}/records`, { values: { first_name: `${name} ${run}`, phone_number: number }, ownerTeamId: null, groups: [] });
      }).toPass({ timeout: 60_000 });
    }
    await signOut(page);

    await signIn(page, "owner");
    owner = await apiAs(page); // a new session has a new CSRF token
    await publish([field(first, "First name")], "c188 v2: phone removed", 1);
    await signOut(page);
  });

  test("a security administrator approves searching Phone's retained values from Search approvals", async ({ page }) => {
    await signIn(page, "security");
    await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Organization", exact: true }).click();
    await page.getByRole("navigation", { name: "Organization" }).getByRole("link", { name: "Search Approvals", exact: true }).click();
    const forms = page.getByRole("table", { name: "Published forms registered with the Authorization Authority" });
    await expect(forms.getByRole("columnheader", { name: "Removed fields searchable" })).toBeVisible();
    await forms.getByRole("button", { name: `Review fields of ${formName}` }).click();
    const phone = page.locator("section", { has: page.locator(":scope > header h2", { hasText: /^Phone \(no longer in the form\)$/ }) });
    await expect(phone.getByRole("status").filter({ hasText: /^No longer in the form/ })).toBeVisible(); // the notice adds a visually hidden "(Information)"
    await expect(phone.getByText(/Last included in published version 1/)).toBeVisible();
    await expect(phone.getByText("Approval no longer valid", { exact: true })).toBeVisible();
    const approve = phone.getByRole("group", { name: "Approve searching retained values" });
    await expect(approve.getByText(/Earlier revisions are never searched/)).toBeVisible();
    await approve.getByRole("checkbox", { name: "Contains" }).click(); // exact and prefix only
    await approve.getByRole("textbox", { name: "Approval reference" }).fill(`GOV-C188-RETAINED-${run}`);
    await approve.getByRole("checkbox", { name: "Confirm with the development-only step-up" }).click();
    await approve.getByRole("button", { name: "Approve retained values" }).click();
    await expect(phone.getByText("Approved for its latest retained values: Is exactly, Starts with.")).toBeVisible();
    const results = await new AxeBuilder({ page }).include("main").withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join("; ")}`)).toEqual([]);
    await page.getByRole("button", { name: "Close" }).click();
    await page.reload();
    await expect(forms.getByRole("row", { name: new RegExp(formName) }).getByRole("cell").nth(4)).toHaveText("1"); // Published version, Can be approved, Approved, No longer valid, Removed fields searchable
    await signOut(page);
  });

  test("Advanced Search offers the clerk Phone (no longer in the form) and finds records by their retained values", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/records");
    await page.getByRole("combobox", { name: "Form" }).click();
    await page.getByRole("option", { name: formName }).click();
    const first = condition(page, 1);
    await first.getByRole("combobox", { name: "Field" }).click();
    await expect(page.getByRole("option")).toHaveText(["Phone (no longer in the form)"]);
    await page.getByRole("option", { name: "Phone (no longer in the form)" }).click();
    await first.getByRole("combobox", { name: "Condition" }).click();
    await page.getByRole("option", { name: "Starts with" }).click();
    await first.getByRole("textbox", { name: "Value" }).fill("555-01");
    await page.getByRole("button", { name: "Search records" }).click();
    const table = page.getByRole("table", { name: "My Records" });
    await expect(table.getByRole("row")).toHaveCount(3); // header + Avery and Blake (Casey's phone does not match)
    await expect(page.getByText("Showing 1–2 of 2 matching records")).toBeVisible();
    expect(page.url()).not.toContain("555-01");
    const results = await new AxeBuilder({ page }).include("main").withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations.map((v) => v.id)).toEqual([]);
    await signOut(page);
  });

  test("withdrawing the retained approval removes Phone from Advanced Search at once", async ({ page }) => {
    await signIn(page, "security");
    await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Organization", exact: true }).click();
    await page.getByRole("navigation", { name: "Organization" }).getByRole("link", { name: "Search Approvals", exact: true }).click();
    await page.getByRole("table", { name: "Published forms registered with the Authorization Authority" }).getByRole("button", { name: `Review fields of ${formName}` }).click();
    const phone = page.locator("section", { has: page.locator(":scope > header h2", { hasText: /^Phone \(no longer in the form\)$/ }) });
    const withdraw = phone.getByRole("group", { name: "Withdraw approval" });
    await withdraw.getByRole("textbox", { name: "Reason" }).fill("c188 e2e withdrawal");
    await withdraw.getByRole("button", { name: "Withdraw search approval" }).click();
    await expect(phone.getByText("Approval withdrawn", { exact: true })).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto("/app/records");
    await page.getByRole("combobox", { name: "Form" }).click();
    await page.getByRole("option", { name: formName }).click();
    await expect(condition(page, 1).getByRole("combobox", { name: "Field" })).toBeDisabled();
  });
});
