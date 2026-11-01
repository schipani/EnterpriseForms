import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C8 owner recovery (C-105), business side, in the real application on the isolated security-test stack (C-74 guard in force: signIn
 * refuses any origin the global setup did not verify). Platform Operators cannot be driven from this harness, so the case is opened
 * through the isolated Platform Console BFF (127.0.0.1:5192, synthetic operator Dana) for the seeded organization, and the seeded
 * organization is left as found: the case is cancelled again whatever happens. The owner sees the notice and objects (the
 * confirmation appears only after the server recorded it); a member who is not an owner sees nothing.
 */
test.describe.configure({ mode: "serial" });

const CONSOLE = "http://127.0.0.1:5192";
const OPERATOR_SUBJECT = "sectest-console-operator-dana";
const RUN = Date.now().toString(36);
const INCIDENT = `E2E-OBJ-${RUN}`;

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

async function consoleClient() {
  const login = await fetch(`${CONSOLE}/console-api/v1/session/development-login`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Origin: CONSOLE },
    body: JSON.stringify({ subjectIdentifier: OPERATOR_SUBJECT }),
  });
  if (!login.ok) {
    throw new Error(`console sign-in failed (${login.status}); the isolated console and its synthetic operators must be running on ${CONSOLE}`);
  }
  const cookie = login.headers.getSetCookie().map((c) => c.split(";")[0]).join("; ");
  const csrf = ((await login.json()) as { csrfToken: string }).csrfToken;
  return async (method: "GET" | "POST", path: string, body?: unknown): Promise<{ status: number; body: Json }> => {
    const response = await fetch(`${CONSOLE}/console-api/v1${path}`, {
      method,
      headers: { Cookie: cookie, Origin: CONSOLE, Accept: "application/json", ...(method === "POST" ? { "X-CSRF-Token": csrf, "Content-Type": "application/json" } : {}) },
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    });
    const text = await response.text();
    return { status: response.status, body: text ? (JSON.parse(text) as Json) : {} };
  };
}

let organizationId = "";
let caseId = "";

async function cancelOpenCases(call: Awaited<ReturnType<typeof consoleClient>>): Promise<void> {
  const list = await call("GET", "/owner-recovery?status=open&limit=50");
  for (const item of (list.body["items"] ?? []) as Json[]) {
    if (item["organizationId"] !== organizationId) continue;
    const detail = await call("GET", `/owner-recovery/${item["caseId"]}`);
    await call("POST", `/owner-recovery/${item["caseId"]}/cancel`, { expectedStatementSha256: detail.body["statementSha256"], justification: "Cleaning up a browser-test case." });
  }
}

test.beforeAll(async ({ request }) => {
  const personas = await request.get("/api/v1/session/development-personas");
  organizationId = ((await personas.json()) as Array<{ organizationId: string }>)[0]!.organizationId;
  const call = await consoleClient();
  await cancelOpenCases(call); // a case left by an aborted run would block this one
  const org = await call("GET", `/organizations/${organizationId}`);
  expect(org.status, JSON.stringify(org.body)).toBe(200);
  const issuerUri = (org.body["owners"] as Json[])[0]!["issuerUri"] as string;
  const created = await call("POST", "/owner-recovery", {
    idempotencyKey: `rec-obj-${RUN}`,
    organizationId,
    incidentReference: INCIDENT,
    justification: "Browser test: the owner must see this case and be able to object to it.",
    declaredUnavailable: [],
    replacements: [{ existingAuthorityUserId: null, issuerUri, subjectIdentifier: `rec-obj-${RUN}-new-owner`, displayNameHint: `Replacement Owner ${RUN}`, verificationMethod: "SupportCallback", verificationReference: `TICKET-${RUN}` }],
    dryRun: false,
  });
  expect(created.status, JSON.stringify(created.body)).toBeLessThan(300);
  caseId = created.body["case"]["caseId"] as string;
});

test.afterAll(async () => {
  if (!organizationId) return;
  await cancelOpenCases(await consoleClient()).catch(() => undefined);
  // An objected case is no longer "open"; cancel it explicitly so the shared organization is left as it was found.
  if (caseId) {
    const call = await consoleClient();
    const detail = await call("GET", `/owner-recovery/${caseId}`);
    if (detail.status === 200 && detail.body["myEligibility"]?.["canCancel"]) {
      await call("POST", `/owner-recovery/${caseId}/cancel`, { expectedStatementSha256: detail.body["statementSha256"], justification: "Cleaning up a browser-test case." });
    }
  }
});

async function openOverview(page: Page) {
  await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Organization", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Organization overview" })).toBeVisible();
}

async function axe(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`), `axe violations on ${label}`).toEqual([]);
}

test.describe("Owner recovery notice (C-105)", () => {
  test("a member who is not an owner sees no recovery notice", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/organization");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Owner recovery case in progress" })).toHaveCount(0);
    await expect(page.getByText(INCIDENT)).toHaveCount(0);
    await signOut(page);
  });

  for (const width of [320, 768, 1440]) {
    test(`the owner's notice passes axe at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await signIn(page, "owner");
      await openOverview(page);
      const notice = page.getByRole("region", { name: "Owner recovery case in progress" });
      await expect(notice).toBeVisible();
      await expect(notice).toContainText(INCIDENT);
      await expect(notice).toContainText("0 of 3");
      await expect(notice).toContainText(`Replacement Owner ${RUN}`);
      await page.getByRole("button", { name: "Object to this recovery" }).click();
      await expect(page.getByRole("textbox", { name: /Why are you objecting/ })).toBeVisible();
      await axe(page, `owner recovery notice at ${width}px`);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, "horizontal page scroll").toBeLessThanOrEqual(1);
    });
  }

  test("the owner objects and sees the confirmation only after the server recorded it", async ({ page }) => {
    await signIn(page, "owner");
    await openOverview(page);
    await page.getByRole("button", { name: "Object to this recovery" }).click();
    const submit = page.getByRole("button", { name: "Record objection" });
    await expect(submit).toBeDisabled();
    await page.getByRole("textbox", { name: /Why are you objecting/ }).fill("We did not ask for this recovery; our owner is available.");
    await expect(submit).toBeDisabled(); // the step-up confirmation is still missing
    await page.getByRole("checkbox", { name: /development-only step-up/ }).click();
    await submit.click();
    await expect(page.getByText(/Your objection was recorded. The recovery is blocked./)).toBeVisible();

    // The Platform Operators now see the objection and a blocked case: the server recorded what the owner was told.
    const detail = await (await consoleClient())("GET", `/owner-recovery/${caseId}`);
    expect(detail.body["displayState"]).toBe("BlockedByObjection");
    expect(detail.body["objection"]?.["reason"]).toContain("We did not ask for this recovery");
    await signOut(page);
  });
});
