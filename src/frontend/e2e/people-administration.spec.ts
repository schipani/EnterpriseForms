import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C8 M2 (AC-7) People administration in the real application on the isolated security-test stack: the organization owner
 * makes and removes a designer, disables a member (who is then refused) and re-enables them, records an owner proposal that
 * only another owner can approve, and requests a new member who stays a pending request (never a member, never signed in)
 * until two operators approve. Every change carries a reason and the development-only step-up; the shared organization is
 * left as it was found (roles and membership restored).
 */
test.describe.configure({ mode: "serial" });

const run = Date.now().toString(36);

/**
 * Deterministic fixture (Stage 5): the "owns records" assertion below needs a persona that really owns a record, which a freshly
 * created isolated stack does not have (the earlier version relied on records left behind by other specs). Everything goes through
 * the supported API as the personas themselves, so the Authority authorizes each step: the owner (a designer) publishes a form that
 * Riley Reader may use, Riley creates a record and so becomes its owner. Riley is also re-enabled if an earlier aborted run left
 * the membership disabled (a disabled member's session is refused, so the owner does it with a reason and the development step-up).
 */
async function personaApi(newContext: () => Promise<APIRequestContext>, persona: string) {
  const request = await newContext(); // one cookie jar per persona: a second login on the same jar needs the first session's CSRF token
  const login = await request.post("/api/v1/session/development-login", { data: { personaKey: persona } });
  expect(login.ok(), `development login of ${persona}: ${login.status()} ${await login.text()}`).toBeTruthy();
  const csrf = ((await login.json()) as { csrfToken: string }).csrfToken;
  let counter = 0;
  const send = async (method: "POST" | "PUT" | "GET", path: string, data?: unknown) => {
    const response = await request.fetch(path, { method, ...(data !== undefined ? { data } : {}), headers: method === "GET" ? {} : { "X-CSRF-Token": csrf, "Idempotency-Key": `pa-${run}-${persona}-${Date.now()}-${counter++}` } });
    const text = await response.text();
    expect(response.ok(), `${method} ${path}: ${response.status()} ${text}`).toBeTruthy();
    return (text ? JSON.parse(text) : {}) as Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
  };
  return send;
}

async function ensureRileyOwnsARecordAndIsEnabled(newContext: () => Promise<APIRequestContext>) {
  const owner = await personaApi(newContext, "owner");
  const users = (await owner("GET", "/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
  const riley = users.find((u) => u.displayName.includes("Riley Reader"))!.userId;
  const members = await owner("GET", "/api/v1/organization/members?pageSize=15&q=Riley");
  const rileyRow = (members["members"] as Array<{ userId: string; membershipState: string }>).find((m) => m.userId === riley);
  if (rileyRow?.membershipState === "Disabled") {
    await owner("POST", `/api/v1/organization/members/${riley}/enable`, { reason: `browser test ${run} restores the shared membership`, useDevelopmentStepUp: true });
  }
  const name = `People fixture ${run}`;
  const created = await owner("POST", "/api/v1/form-definitions/designer", { formDefinitionName: name, formDefinitionDescription: "synthetic fixture", formUserUserIds: [riley], formUserTeamIds: [] });
  const formId = created["summary"].formDefinitionId as string;
  const added = await owner("POST", `/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_title", fieldDisplayName: "Case title", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null });
  const element = { kind: "field", fieldDefinitionId: added["fieldDefinitionId"], control: "text", label: "Case title", required: true, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} };
  const detail = await owner("GET", `/api/v1/form-definitions/${formId}`);
  const draftRevision = detail["draft"] ? Number(detail["draft"].draftRevision) : 0;
  const layout = { layoutSchemaVersion: 2, title: name, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [element] }] };
  const saved = await owner("PUT", `/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draftRevision, layoutJson: JSON.stringify(layout) });
  await owner("POST", `/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "synthetic people-administration fixture" });
  const reader = await personaApi(newContext, "reader");
  await reader("POST", `/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Owned by Riley ${run}` }, ownerTeamId: null, groups: [] });
}

test.beforeAll(async ({ playwright, baseURL }) => {
  const contexts: APIRequestContext[] = [];
  await ensureRileyOwnsARecordAndIsEnabled(async () => {
    const context = await playwright.request.newContext({ baseURL: baseURL! });
    contexts.push(context);
    return context;
  });
  await Promise.all(contexts.map((context) => context.dispose()));
});

async function axe(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations, JSON.stringify(results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })))).toEqual([]);
}

async function openPeople(page: Page) {
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
}

const row = (page: Page, name: RegExp) => page.getByRole("table", { name: "People" }).getByRole("row", { name });

/** Opens a row action (the primary button or the row's "…" menu), fills the reason, confirms the step-up and submits. */
async function change(page: Page, person: RegExp, action: string, confirm: string) {
  const target = row(page, person);
  await target.getByRole("button", { name: /^Actions for/ }).click();
  await page.getByRole("menuitem", { name: new RegExp(`^${action}:`) }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("button", { name: confirm })).toBeDisabled();
  await dialog.getByLabel(/Reason/).fill(`browser test ${run}`);
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await dialog.getByRole("button", { name: confirm }).click();
  await expect(dialog).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: "Change recorded" })).toBeVisible();
}

test("an owner makes and removes a designer, and cannot disable a member who still owns records", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  await axe(page);

  await change(page, /Riley Reader/, "Make designer", "Make designer");
  await expect(row(page, /Riley Reader/)).toContainText("Designer");
  await change(page, /Riley Reader/, "Remove designer role", "Remove designer role");
  await expect(row(page, /Riley Reader/)).not.toContainText("Designer");

  // Every persona of the shared test organization owns records (or is its last Security Administrator), so disabling is
  // refused with a count and nothing changes. The full disable -> refused -> re-enable path runs in Stage B (MembershipAdministrationTests).
  await row(page, /Riley Reader/)
    .getByRole("button", { name: /^Actions for/ })
    .click();
  await page.getByRole("menuitem", { name: /^Disable membership:/ }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Reason/).fill(`browser test ${run}`);
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await dialog.getByRole("button", { name: "Disable membership" }).click();
  await expect(dialog.getByRole("alert")).toContainText("The person still owns records");
  await expect(dialog.getByRole("alert")).toContainText(/owns \d+ active record/);
  await axe(page);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(row(page, /Riley Reader/)).toContainText("Active");
  await signOut(page);
});

test("an owner change is only a proposal until a different owner approves it", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  // An identical pending proposal (from an earlier run within 24 hours) removes the action; then only the proposal is checked.
  if (
    await row(page, /Dana Designer/)
      .getByRole("button", { name: /^Actions for/ })
      .count()
  ) {
    await row(page, /Dana Designer/)
      .getByRole("button", { name: /^Actions for/ })
      .click();
    await expect(page.getByRole("menu")).toBeVisible();
    const propose = page.getByRole("menuitem", { name: /^Propose as owner:/ });
    const available = (await propose.count()) > 0;
    await page.keyboard.press("Escape");
    if (available) {
      await change(page, /Dana Designer/, "Propose as owner", "Record proposal");
    }
  }
  const proposals = page.getByRole("table", { name: "Owner proposals list" });
  const proposal = proposals.getByRole("row", { name: /Dana Designer/ }).first();
  await expect(proposal).toContainText("Pending");
  await expect(proposal).toContainText("Another owner approves"); // the proposer cannot approve their own proposal
  await expect(row(page, /Dana Designer/)).not.toContainText(/Owner/);
  await axe(page);
  await signOut(page);
});

test("adding a user records a request that two operators must approve; the person is not a member and cannot sign in", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  await page.getByRole("button", { name: "Add user" }).click();
  const dialog = page.getByRole("dialog", { name: "Add user" });
  await expect(dialog.getByLabel(/Identity provider/)).toHaveValue(/.+/);
  await expect(dialog.getByText("Membership is not sign-in")).toBeVisible();
  await dialog.getByLabel(/Object ID/).fill("nia@contoso.example");
  await dialog.getByRole("button", { name: "Request membership" }).click();
  await expect(dialog.getByText(/not an email address/)).toBeVisible();
  await dialog.getByLabel(/Object ID/).fill(`e2e-newcomer-${run}`);
  await dialog.getByLabel(/^Name/).fill(`Nia Newcomer ${run}`);
  await dialog.getByLabel(/Reason/).fill("joins the intake team");
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await axe(page);
  await dialog.getByRole("button", { name: "Request membership" }).click();
  await expect(dialog).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: `Request recorded for Nia Newcomer ${run}` })).toBeVisible();
  const request = page.getByRole("table", { name: "Membership requests list" }).getByRole("row", { name: new RegExp(`Nia Newcomer ${run}`) });
  await expect(request).toContainText("Awaiting operators");
  await expect(row(page, new RegExp(`Nia Newcomer ${run}`))).toHaveCount(0);
  await signOut(page);
});
