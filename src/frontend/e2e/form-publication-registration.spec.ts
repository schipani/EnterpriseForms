import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C-188 BF-1 in the real application (business API, Authorization Authority, SpiceDB; no mocks):
 *  1. Setup: First name and Phone approved for search in v1; the clerk holds a record.
 *  2. v2 removes Phone; the business commits it and its registration with the Authority is lost (Testing-only fault after the commit).
 *  3. The clerk's Advanced Search is unavailable for the form, with the reason (fail closed - not an empty result); axe.
 *  4. The designer sees the pending registration and retries it without publishing again; axe.
 *  5. Search resumes: First name is offered, Phone is not (its approval was invalidated by the registered removal).
 * Requires the security-test stack (Testing environment with Testing__WriteFaultInjection) and the dev-seed personas.
 */
const run = Date.now().toString(36);
type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  const body = await response.text();
  return (body === "" ? {} : JSON.parse(body)) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = (extra: Record<string, string> = {}) => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-bf1-${run}-${Math.random().toString(36).slice(2)}`, ...extra });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown, extra: Record<string, string> = {}) => page.request.post(path, { data, headers: headers(extra) }),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

const field = (id: string, label: string) => ({ kind: "field", fieldDefinitionId: id, control: "text", label, required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });
const axeTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

test.describe.configure({ mode: "serial" });

test.describe("C-188 BF-1 publication registration", () => {
  const formName = `BF1 Registration ${run}`;
  let formId = "";

  test("setup: v1 searchable, then v2 removes Phone and its registration is lost", async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page, "owner");
    let owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const id = (pattern: RegExp) => users.find((user) => pattern.test(user.displayName))?.userId;
    test.skip(id(/Sam Security/) === undefined, "this organization predates the dev-seed security persona (recreate the stack)");
    const policy = await owner.get("/api/v1/organization/security-policy");
    await owner.put("/api/v1/organization/security-policy", { minimumFieldClassification: policy["minimumFieldClassification"], plaintextInternalFieldsPermitted: true, plaintextDowngradePermitted: policy["plaintextDowngradePermitted"], plaintextSearchPermitted: true, policyStatementText: "Synthetic test organization: selected plaintext Internal fields may be searched.", expectedPolicyRevision: Number(policy["securityPolicyRevision"]) });
    formId = ((await ok(await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: formName, formDefinitionDescription: `synthetic bf1 ${run}`, formUserUserIds: [id(/Casey Clerk/), id(/Avery Owner/)], formUserTeamIds: [] })))["summary"] as Json)["formDefinitionId"] as string;
    const add = async (fieldKey: string, fieldDisplayName: string) => (await ok(await owner.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey, fieldDisplayName, fieldDescription: "", fieldDataType: "String", options: null, fieldClassification: "Internal", fieldProtectionMode: "Standard" })))["fieldDefinitionId"] as string;
    const first = await add("first_name", "First name");
    const phone = await add("phone_number", "Phone");
    const save = async (elements: unknown[]) => {
      const draft = (await owner.get(`/api/v1/form-definitions/${formId}`))["draft"] as Json | null;
      return Number((await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: formName, sections: [{ sectionKey: "main", title: "Main", gridColumns: 4, elements }] }) }))["draftRevision"]);
    };
    const v1 = await ok(await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: await save([field(first, "First name"), field(phone, "Phone")]), expectedCurrentVersionNumber: 0, changeReason: "bf1 v1" }));
    expect(v1["authorityRegistered"]).toBe(true);
    await signOut(page);

    await signIn(page, "security");
    const security = await apiAs(page);
    for (const fieldId of [first, phone]) {
      await expect(async () => ok(await security.post(`/api/v1/form-definitions/${formId}/fields/${fieldId}/search-approval`, { operators: ["equals", "startsWith"], approvalReference: `GOV-BF1-${run}`, useDevelopmentStepUp: true }))).toPass({ timeout: 60_000 });
    }
    await security.put(`/api/v1/form-definitions/${formId}/field-searchers`, { grantUserIds: [id(/Casey Clerk/)], grantTeamIds: [], revokeUserIds: [], revokeTeamIds: [] });
    await signOut(page);

    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    await expect(async () => ok(await clerk.post(`/api/v1/form-definitions/${formId}/records`, { values: { first_name: `Avery ${run}`, phone_number: "555-0701" }, ownerTeamId: null, groups: [] }))).toPass({ timeout: 60_000 });
    await signOut(page);

    await signIn(page, "owner");
    owner = await apiAs(page);
    const lost = await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: await save([field(first, "First name")]), expectedCurrentVersionNumber: 1, changeReason: "bf1 v2: phone removed" }, { "X-Testing-Fault": "publish:after-commit" });
    expect(lost.status()).toBe(503);
    expect(((await lost.json()) as Json)["title"]).toBe("TESTING_FAULT_INJECTED");
    expect(((await owner.get(`/api/v1/form-definitions/${formId}/authority-registration`)) as Json)["registrationState"]).toBe("Pending");
    await signOut(page);
  });

  test("the clerk's Advanced Search is unavailable for the form while its registration is pending", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/records");
    await page.getByRole("combobox", { name: "Form" }).click();
    await page.getByRole("option", { name: formName }).click();
    await expect(page.getByText("Advanced field search is not available yet")).toBeVisible();
    await expect(page.getByText(/not yet registered with the Authorization Authority, so none of its fields can be searched/)).toBeVisible();
    await expect(page.getByRole("group", { name: "Condition 1" }).getByRole("combobox", { name: "Field" })).toBeDisabled();
    const results = await new AxeBuilder({ page }).include("main").withTags(axeTags).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join("; ")}`)).toEqual([]);
    await signOut(page);
  });

  test("the designer retries the registration without publishing again", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/forms/${formId}/design`);
    await expect(page.getByText("This version is not yet registered with the Authority")).toBeVisible();
    await expect(page.getByText(/Field search on this form is unavailable until the latest published version is registered/)).toBeVisible();
    const results = await new AxeBuilder({ page }).include("main").withTags(axeTags).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join("; ")}`)).toEqual([]);
    await page.getByRole("button", { name: "Retry registration" }).click();
    await expect(page.getByText("Registration complete")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(/now registered/)).toBeVisible();
    await page.getByRole("button", { name: "Dismiss" }).click();
    await expect(page.getByText("This version is not yet registered with the Authority")).toBeHidden();
    await signOut(page);
  });

  test("search resumes without the removed field", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/records");
    await page.getByRole("combobox", { name: "Form" }).click();
    await page.getByRole("option", { name: formName }).click();
    const field = page.getByRole("group", { name: "Condition 1" }).getByRole("combobox", { name: "Field" });
    await expect(field).toBeEnabled({ timeout: 30_000 });
    await field.click();
    await expect(page.getByRole("option")).toHaveText(["First name"]); // Phone's approval was invalidated by the registered removal
    await page.keyboard.press("Escape"); // close the option list (it hides the rest of the page from assistive technology while open)
    await expect(page.getByRole("option")).toHaveCount(0);
    await signOut(page);
  });
});
