import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { decide, filterQueue, N, OPERATORS, openReview, type OperatorKey } from "../e2e-console/support";
import { signIn } from "../e2e/support";

/**
 * Cross-application regression (C8 M2 Stage 5): a membership request is approved by two operators in the separate Platform Console
 * while the business People page stays open; Refresh alone must then show Approved and the new member (no navigation, no reload).
 * Real isolated stack throughout: business API, Authority, PostgreSQL, SpiceDB, Vault Transit signing; nothing is mocked.
 * The isolated console's synthetic operators are Dana and Ellis (the persistent development console has Morgan and Quinn); like
 * them, they are synthetic and the approvals are not two-person evidence.
 */
const CONSOLE = process.env["E2E_CONSOLE_BASE_URL"] ?? "http://127.0.0.1:5198";

async function consoleContext(browser: Browser, operator: OperatorKey): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ baseURL: CONSOLE, viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  await page.goto("/platform/sign-in");
  await page.getByRole("button", { name: `Sign in as ${OPERATORS[operator].name}` }).click();
  await expect(page.getByRole("heading", { name: "Operator approvals", level: 1 })).toBeVisible();
  return { context, page };
}

/** Files the membership request as the owner persona through the supported API (the same call the Add user dialog makes). */
async function fileRequest(page: Page, displayName: string, subject: string): Promise<void> {
  const request = page.context().request;
  // The page is already signed in as the owner (UI sign-in); the session endpoint returns that session's CSRF token.
  const session = await request.get("/api/v1/session");
  const csrf = ((await session.json()) as { authenticated: boolean; csrfToken: string }).csrfToken;
  expect(csrf, "the owner session is signed in").not.toBe("");
  const created = await request.post("/api/v1/organization/membership-requests", {
    data: { subjectIdentifier: subject, displayNameHint: displayName, initialRoleName: "Designer", reason: "joins the intake team (cross-application test)", useDevelopmentStepUp: true },
    headers: { "X-CSRF-Token": csrf, "Idempotency-Key": randomUUID().replaceAll("-", "") },
  });
  expect(created.ok(), `membership request ${created.status()} ${await created.text()}`).toBeTruthy();
}

test("an approval made by two operators in the Platform Console shows on the open People page after Refresh, without navigation", async ({ page, browser }) => {
  const person = N("Cross Casey");
  const subject = `cross-${randomUUID().slice(0, 10)}`;

  // 1-3. The request awaits operators; the People page shows it.
  await signIn(page, "owner");
  await fileRequest(page, person, subject);
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
  const requests = page.getByRole("region", { name: /^Membership requests/ });
  await requests.getByLabel("Search requests").fill(person); // an active search and the five-row list must survive Refresh
  const requestRow = requests.getByRole("table", { name: "Membership requests list" }).getByRole("row", { name: new RegExp(person) });
  await expect(requestRow).toContainText("Awaiting operators");
  const people = page.getByRole("table", { name: "People" });
  await expect(people.getByRole("row", { name: new RegExp(person) })).toHaveCount(0); // not a member yet

  // Mark this exact document and count navigations: neither may change before the assertions below.
  await page.evaluate(() => {
    (window as unknown as { __crossMarker: string }).__crossMarker = "same-document";
  });
  const navigations: string[] = [];
  page.on("framenavigated", (frame) => frame === page.mainFrame() && navigations.push(frame.url()));

  // 4-6. First approval in the separate Platform Console.
  const first = await consoleContext(browser, "dana");
  await filterQueue(first.page, person, "Needs a decision");
  await openReview(first.page, person);
  await decide(first.page, "Approve", "Verified with the owner by phone (cross-application test).");
  await expect(first.page.getByText("Approval recorded — 1 of 2")).toBeVisible();
  // 7. Sign out.
  await first.page.getByRole("button", { name: "Sign out" }).click();
  await expect(first.page.getByRole("heading", { name: "Sign in to the Platform Console" })).toBeVisible();
  await first.context.close();

  // 8-9. A different operator reviews the same request and approves.
  const second = await consoleContext(browser, "ellis");
  await filterQueue(second.page, person, "Awaiting second approval");
  await openReview(second.page, person);
  await decide(second.page, "Approve", "Second review: identity matches the owner request.");
  await expect(second.page.getByText("Membership approved — 2 of 2")).toBeVisible();
  await second.context.close();

  // 10. The Authority reports it approved (read through the business API, which asks the Authority).
  const asOwner = page.context().request;
  await expect
    .poll(async () => {
      const response = await asOwner.get(`/api/v1/organization/membership-requests?pageSize=50&q=${encodeURIComponent(person)}`);
      const body = (await response.json()) as { items: Array<{ displayNameHint: string; requestState: string }> };
      return body.items.find((r) => r.displayNameHint === person)?.requestState;
    }, { timeout: 60_000 })
    .toBe("Approved");

  // 11-13. Back on the original page, untouched: it still shows the old state until Refresh is clicked.
  await expect(requestRow).toContainText("Awaiting operators");
  await page.getByRole("button", { name: "Refresh People" }).click();
  await expect(requestRow).toContainText("Approved");
  await expect(requestRow).not.toContainText("Awaiting operators");

  // Search and the five-row list stayed as they were; actions follow the new status (no Edit or Cancel any more).
  await expect(requests.getByLabel("Search requests")).toHaveValue(person);
  await requestRow.getByRole("button", { name: /^Actions for/ }).click();
  await expect(page.getByRole("menuitem", { name: /^View details/ })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: /^Cancel request/ })).toHaveCount(0);
  await page.keyboard.press("Escape");

  // 14. The newly approved member appears in the People directory (15 per page; searched by name).
  await page.getByLabel("Search people").fill(person);
  await expect(people.getByRole("row", { name: new RegExp(person) })).toHaveCount(1);
  await expect(people.getByRole("row", { name: new RegExp(person) })).toContainText("Active");

  // 15. No reload and no navigation happened: the same document, no frame navigation.
  expect(await page.evaluate(() => (window as unknown as { __crossMarker?: string }).__crossMarker)).toBe("same-document");
  expect(navigations).toEqual([]);
  expect(await page.evaluate(() => performance.getEntriesByType("navigation").length)).toBe(1);

  // Sessions and cookies are separate: the business page holds no console cookie, the console holds no business cookie.
  const names = (await page.context().cookies()).map((c) => c.name);
  expect(names).not.toContain("ef-platform-console");
});

test("the Platform Console session cannot reach business records, and business sessions are not console sessions", async ({ page, browser }) => {
  await signIn(page, "owner");
  const businessCookies = (await page.context().cookies()).map((c) => `${c.name}=${c.value}`).join("; ");
  const { context, page: consolePage } = await consoleContext(browser, "dana");
  const consoleCookies = await context.cookies();
  expect(consoleCookies.map((c) => c.name)).toEqual(["ef-platform-console"]);
  // Its cookie is not accepted by the business API, and the console exposes no business route.
  for (const path of ["/console-api/v1/form-records", "/console-api/v1/organization/members", "/console-api/v1/documents"]) {
    expect([401, 404]).toContain((await consolePage.request.get(CONSOLE + path)).status());
  }
  // The console origin does not serve the business API at all: a business path returns the app shell (HTML), never business JSON.
  const stray = await consolePage.request.get(CONSOLE + "/api/v1/form-records");
  expect(stray.headers()["content-type"] ?? "").not.toContain("json");
  // A business cookie presented to the console is not a session.
  const anonymous = await browser.newContext({ baseURL: CONSOLE, extraHTTPHeaders: { Cookie: businessCookies } });
  expect((await anonymous.request.get("/console-api/v1/operator/me")).status()).toBe(401);
  await anonymous.close();
  // And the console cookie is not a business session (the business API reports no session).
  const withConsoleCookie = await browser.newContext({ baseURL: "http://127.0.0.1:5199", extraHTTPHeaders: { Cookie: consoleCookies.map((c) => `${c.name}=${c.value}`).join("; ") } });
  expect(((await (await withConsoleCookie.request.get("/api/v1/session")).json()) as { authenticated: boolean }).authenticated).toBe(false);
  await withConsoleCookie.close();
  await context.close();
});
