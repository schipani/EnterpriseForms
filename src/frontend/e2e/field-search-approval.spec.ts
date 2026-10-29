import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Locator, type Page } from "@playwright/test";
import { openFieldConfig, signIn, signOut } from "./support";

/**
 * Phase C7 M2 — AC-6 search approval in the real application (business API, Authorization Authority, SpiceDB; no mocks).
 *  1. Configure Field has a Search section: classification, protection, eligibility, conditions and approval status as
 *     the Authority holds them. An encrypted field reads "Encrypted for privacy — not searchable." and offers no control
 *     that changes its encryption. A designer sees the status; approving is a security administrator's job.
 *  2. A security administrator approves a plaintext Internal field with the development-only step-up (this stack only)
 *     and grants search access to a person.
 *  3. My Records > Advanced Search offers that person exactly the approved field and its approved conditions; Search
 *     records stays disabled and nothing is sent: execution remains off (M3). Someone without access is offered nothing.
 *  4. Withdrawing the approval removes the field at once. axe WCAG 2.2 AA on the Search section.
 * Requires the dev-seed "security" persona (organizations bootstrapped since Phase C7 M2) and the Authority's
 * FieldSearch:ApprovalStepUp=SyntheticDevelopment (compose.security-test.yaml / compose.dev-persistent.yaml).
 */
const run = Date.now().toString(36);
type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-search-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

const field = (id: string, label: string) => ({ kind: "field", fieldDefinitionId: id, control: "text", label, required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });
const condition = (scope: Page | Locator, n: number) => scope.getByRole("group", { name: `Condition ${n}` });

test.describe.configure({ mode: "serial" });

test.describe("Phase C7 M2 search approval", () => {
  const formName = `C7 Search ${run}`;
  let formId = "";

  test("setup: plaintext search permitted by policy; a form with a plaintext Internal field and an encrypted Confidential field, published", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const id = (pattern: RegExp) => users.find((user) => pattern.test(user.displayName))?.userId;
    const security = id(/Sam Security/);
    test.skip(security === undefined, "this organization predates the dev-seed security persona (recreate the stack to run this spec)");
    const policy = await owner.get("/api/v1/organization/security-policy");
    await owner.put("/api/v1/organization/security-policy", {
      minimumFieldClassification: policy["minimumFieldClassification"],
      plaintextInternalFieldsPermitted: true,
      plaintextDowngradePermitted: policy["plaintextDowngradePermitted"],
      plaintextSearchPermitted: true,
      policyStatementText: "Synthetic test organization: selected plaintext Internal fields may be approved for search.",
      expectedPolicyRevision: Number(policy["securityPolicyRevision"]),
    });
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic search ${run}`, formUserUserIds: [id(/Avery Owner/), id(/Casey Clerk/), security], formUserTeamIds: [] });
    formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const reference = (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_reference", fieldDisplayName: "Case reference", fieldDescription: "", fieldDataType: "String", options: null, fieldClassification: "Internal", fieldProtectionMode: "Standard" }))["fieldDefinitionId"] as string;
    const note = (await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_note", fieldDisplayName: "Case note", fieldDescription: "", fieldDataType: "String", options: null, fieldClassification: "Confidential", fieldProtectionMode: "ApplicationEncrypted" }))["fieldDefinitionId"] as string;
    const draft = (await owner.get(`/api/v1/form-definitions/${formId}`))["draft"] as Json | null;
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [field(reference, "Case reference"), field(note, "Case note")] }] }) });
    const version = await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "search e2e" });
    expect(version["authorityRegistered"]).toBe(true);
    await signOut(page);
  });

  test("a designer sees each field's search status; encrypted fields are not searchable and nothing here removes encryption", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/forms/${formId}/design`);
    const note = await openFieldConfig(page, "Case note", "Search");
    const panel = note.getByRole("tabpanel", { name: "Search" });
    await expect(panel.getByText("Encrypted for privacy — not searchable.", { exact: true })).toBeVisible();
    await expect(panel.getByText("Confidential", { exact: true })).toBeVisible();
    await expect(panel.getByText("Not approved", { exact: true })).toBeVisible();
    await expect(panel.getByRole("checkbox")).toHaveCount(0);
    await expect(panel.getByRole("button", { name: /Approve/ })).toHaveCount(0);
    const results = await new AxeBuilder({ page }).include('[role="dialog"]').withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join("; ")}`)).toEqual([]);
    await page.keyboard.press("Escape");

    const reference = await openFieldConfig(page, "Case reference", "Search");
    const status = reference.getByRole("tabpanel", { name: "Search" });
    await expect(status.getByText("Can be approved", { exact: true })).toBeVisible();
    await expect(status.getByText("Is exactly, Starts with, Contains")).toBeVisible();
    await expect(status.getByText(/Approving a field for search is done by a security administrator/)).toBeVisible();
    await expect(status.getByRole("button", { name: /Approve/ })).toHaveCount(0);
  });

  test("a security administrator approves with the development step-up and grants search access", async ({ page }) => {
    await signIn(page, "security");
    await page.goto(`/app/forms/${formId}/design`);
    const modal = await openFieldConfig(page, "Case reference", "Search");
    const panel = modal.getByRole("tabpanel", { name: "Search" });
    const approve = panel.getByRole("group", { name: "Approve for search" });
    await expect(approve.getByRole("button", { name: "Approve for search" })).toBeDisabled();
    await approve.getByRole("checkbox", { name: "Contains" }).click(); // approve exact and prefix matching only
    await approve.getByRole("textbox", { name: "Approval reference" }).fill(`GOV-E2E-${run}`);
    await approve.getByRole("checkbox", { name: "Confirm with the development-only step-up" }).click();
    await approve.getByRole("button", { name: "Approve for search" }).click();
    await expect(panel.getByText("Approved", { exact: true })).toBeVisible();
    await expect(panel.getByText("Approved for version 1: Is exactly, Starts with.")).toBeVisible();
    await page.keyboard.press("Escape");

    await page.getByRole("tab", { name: "Form permissions" }).click();
    const card = page.locator("section").filter({ has: page.getByRole("heading", { name: /^Who can search field values/ }) });
    await card.getByRole("combobox", { name: "Person" }).click();
    await page.getByRole("option", { name: /Casey Clerk/ }).click();
    await card.getByRole("button", { name: "Grant search access" }).click();
    await expect(card.getByRole("rowheader", { name: /Casey Clerk/ })).toBeVisible();
  });

  test("Advanced Search offers the searcher exactly the approved field and conditions, runs verified, and offers nothing to others", async ({ page }) => {
    const sent: string[] = [];
    page.on("request", (request) => {
      if (/\/field-search(\/|$|\?)/.test(new URL(request.url()).pathname)) sent.push(request.url());
    });
    await signIn(page, "clerk");
    await page.goto(`/app/records?form=${formId}`);
    const first = condition(page, 1);
    await expect(first.getByRole("combobox", { name: "Field" })).toBeEnabled();
    await first.getByRole("combobox", { name: "Field" }).click();
    await expect(page.getByRole("option")).toHaveText(["Case reference"]);
    await page.getByRole("option", { name: "Case reference" }).click();
    await first.getByRole("combobox", { name: "Condition" }).click();
    await expect(page.getByRole("option")).toHaveText(["Is exactly", "Starts with"]);
    await page.getByRole("option", { name: "Starts with" }).click();
    await first.getByRole("textbox", { name: "Value" }).fill("CR-");
    // Phase C7 M3 (gate on in this isolated environment): the search runs verified; the clerk has no records in this form,
    // so the complete, verified answer is "no records match" — and the value never reaches the address bar.
    await page.getByRole("button", { name: "Search records" }).click();
    await expect(page.getByText("No records match these conditions")).toBeVisible();
    expect(sent).toHaveLength(1);
    expect(page.url()).not.toContain("CR-");
    await signOut(page);

    await signIn(page, "reader");
    await page.goto(`/app/records?form=${formId}`);
    await expect(condition(page, 1).getByRole("combobox", { name: "Field" })).toBeDisabled();
    await expect(condition(page, 1).getByRole("combobox", { name: "Field" })).toHaveText("No searchable fields");
  });

  test("withdrawing the approval removes the field from Advanced Search at once", async ({ page }) => {
    await signIn(page, "security");
    await page.goto(`/app/forms/${formId}/design`);
    const modal = await openFieldConfig(page, "Case reference", "Search");
    const withdraw = modal.getByRole("group", { name: "Withdraw approval" });
    await withdraw.getByRole("textbox", { name: "Reason" }).fill("e2e withdrawal");
    await withdraw.getByRole("button", { name: "Withdraw search approval" }).click();
    await expect(modal.getByText("Approval withdrawn", { exact: true })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto(`/app/records?form=${formId}`);
    await expect(condition(page, 1).getByRole("combobox", { name: "Field" })).toBeDisabled();
  });
});
