import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Browser, type Locator, type Page, type Route } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C8 M4-5 (AC-M4-5.1; PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §8.3): the Security Home (PG-052), audit explorer (PG-054), audit event
 * detail (PG-055) and security-command timeline in the real application (business API, Authorization Authority, SpiceDB; no mocks),
 * on the isolated security-test stack only (the global setup's isolation guard; `signIn` refuses any unverified origin).
 *  1. Owner, designer and clerk: no Security home or Audit navigation, and every direct route refuses.
 *  2. Security Administrator: every Security Home widget; no audit explorer (D8-6).
 *  3. Riley Reader is appointed Security Auditor through the People page (there is no auditor seed persona).
 *  4. Security Auditor: Security Home archive only; explorer, event detail and timeline; keyboard filters, refresh and paging.
 *  4a. Security Auditor: keyboard paging of the security-command timeline (both paged screens share checkKeyboardPaging: a second
 *     page is guaranteed, each read is held while its loading state is checked, unavailable controls keep focus and do nothing).
 *  4b, 4c. Security Auditor: a failed page read on the explorer and on the timeline (checkPagingFailure: one synthetic 500 on the
 *     page-2 read; focus moves to the error, Try again retries, focus follows to the page navigation, paging continues).
 *  4d, 4e. Security Auditor: a keyboard Refresh of the explorer and of the timeline (checkKeyboardRefresh: one read, held while
 *     Refresh keeps focus and ignores Enter, Space and a click; afterwards it is available with focus).
 *  5. axe WCAG 2.2 AA and no horizontal overflow at 320/768/1440 px and the 200%/400% zoom equivalents (720 and 320×256 CSS px).
 *  6. Revocation mid-session: a second browser context removes the auditor; the auditor's next request is refused.
 * The organization ends as it started (Riley holds no role; the afterAll removes the role if a failure left it). Gate-closed
 * behaviour is not exercised here: it is covered by Stage B T12 (run 20261120-210905-b5e56ad3) and the page tests (OD-5). Manual
 * NVDA/VoiceOver review is not claimed (PB-14 checklist, pending).
 */
test.describe.configure({ mode: "serial" });

const AUDITOR = /Riley Reader/;
const NOT_FOR_ROLE = "Not available to your role";
const LIVE_CHECK = /live permission check/;
const HOME_SECTIONS = ["Authorization gate", "Archive delivery", "Unresolved security commands", "Encryption coverage"] as const;
const PAGE_SIZE = 25; // AUDIT_PAGE_SIZE and TIMELINE_PAGE_SIZE, the page size of the explorer and of the timeline
const EXPLORER_READS = /\/api\/v1\/security\/audit-events\?/;
const TIMELINE_READS = /\/api\/v1\/security\/audit\/security-commands\?/;

const VIEWPORTS = [
  { name: "320 phone", width: 320, height: 720 },
  { name: "768 tablet", width: 768, height: 1024 },
  { name: "1440 desktop", width: 1440, height: 900 },
  { name: "200% zoom equivalent (720 css px)", width: 720, height: 600 },
  { name: "400% zoom equivalent (320 css px, 256 tall)", width: 320, height: 256 },
] as const;

const primary = (page: Page) => page.getByRole("navigation", { name: "Primary" });
const card = (page: Page, title: string): Locator => page.locator("section").filter({ has: page.getByRole("heading", { level: 2, name: title, exact: true }) });
const peopleRow = (page: Page, name: RegExp) => page.getByRole("table", { name: "People" }).getByRole("row", { name });

/** The same axe tags and filter as the PB-14 suite, plus the page-level reflow check. */
async function scan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"]).disableRules(["region"]).analyze();
  const violations = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical" || v.tags.some((t) => t.startsWith("wcag")));
  expect(violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `${label}: horizontal overflow`).toBeLessThanOrEqual(0);
}

/** Focus reached by the keyboard is conveyed by an outline (as in PB14-30), not by colour alone. */
async function expectVisibleFocus(page: Page, label: string) {
  const style = await page.evaluate(() => {
    const element = document.activeElement as HTMLElement | null;
    return element ? { tag: element.tagName, outline: getComputedStyle(element).outlineStyle } : null;
  });
  expect(style, `${label}: something has focus`).not.toBeNull();
  expect(style!.outline, `${label}: focus outline`).not.toBe("none");
}

/** Presses Tab until the given control has keyboard focus (bounded), then checks that the focus is visible. */
async function tabTo(page: Page, target: Locator, label: string, maximum = 80) {
  for (let i = 0; i < maximum; i++) {
    if (await target.evaluate((element) => element === document.activeElement)) {
      await expectVisibleFocus(page, label);
      return;
    }
    await page.keyboard.press("Tab");
  }
  throw new Error(`${label}: not reachable with Tab within ${maximum} presses`);
}

/**
 * Guarantees that a screen's default (unfiltered) view has a second page, from this auditor's own reads only (no other spec's
 * data): every explorer read records one AUDIT_EVENTS_VIEWED event, and every timeline read one AuditAccessAuthorized entry (an
 * allowlisted timeline kind), in this organization. So reading the first page every 250 ms (4 per second, under the Authority's
 * 5-per-second user limit) ends once that page returns a next cursor, after at most one page of reads.
 */
async function ensureSecondPage(page: Page, path: "/api/v1/security/audit-events" | "/api/v1/security/audit/security-commands", screen: string) {
  await expect
    .poll(
      async () => {
        const response = await page.request.get(`${path}?pageSize=${PAGE_SIZE}`);
        expect(response.status(), `auditor ${screen} read`).toBe(200);
        return ((await response.json()) as PageBody).nextCursor !== null;
      },
      { message: `the ${screen} has a second page`, intervals: [250], timeout: 60_000 },
    )
    .toBe(true);
}

type PageBody = { items: Array<Record<string, unknown>>; nextCursor: string | null };

/**
 * The real page reads of one screen (matched by URL), sent to the server and answered unchanged (no mocked data). Each read is
 * counted with the cursor it asked for, and its answer is kept. While held, a read waits until released, so a test can check the
 * loading state deterministically instead of racing it.
 */
async function pageReads(page: Page, url: RegExp) {
  const requested: Array<string | null> = [];
  const answered: Array<{ cursor: string | null; body: PageBody }> = [];
  const waiting: Array<() => void> = [];
  let holding = false;
  await page.context().route(url, async (route: Route) => {
    const cursor = new URL(route.request().url()).searchParams.get("cursor");
    requested.push(cursor);
    if (holding) await new Promise<void>((resume) => waiting.push(resume));
    const response = await route.fetch();
    if (response.ok()) answered.push({ cursor, body: (await response.json()) as PageBody });
    await route.fulfill({ response });
  });
  return {
    count: () => requested.length,
    lastCursor: () => requested[requested.length - 1],
    held: () => waiting.length,
    lastAnswer: () => {
      expect(answered.length, "a page has been answered").toBeGreaterThan(0);
      return answered[answered.length - 1]!;
    },
    hold: () => {
      holding = true;
    },
    release: () => {
      holding = false;
      waiting.splice(0).forEach((resume) => resume());
    },
  };
}

type PageReads = Awaited<ReturnType<typeof pageReads>>;

/** A paged screen: its page navigation, its reads, the keys of the rows it shows (in order) and the same keys from an answer. */
type PagedScreen = { name: string; nav: Locator; reads: PageReads; shownKeys: () => Promise<string[]>; answerKeys: (body: PageBody) => string[] };

/** The audit explorer as a paged screen: a row is identified by its event id. */
function explorerScreen(page: Page, reads: PageReads): PagedScreen {
  const events = page.getByRole("table", { name: "Audit events" });
  return {
    name: "audit explorer",
    nav: page.getByRole("navigation", { name: "Audit event pages" }),
    reads,
    shownKeys: () => events.getByRole("link", { name: /^Open event / }).evaluateAll((links) => links.map((link) => link.getAttribute("href")!.split("/").pop()!)),
    answerKeys: (body) => body.items.map((item) => String(item.auditEventId)),
  };
}

/** The security-command timeline as a paged screen: a row is identified by its evidence ("<stream> #<revision> · archive <state>"); rows without evidence have no key on either side. */
function timelineScreen(page: Page, reads: PageReads): PagedScreen {
  const items = page.getByRole("table", { name: "Security commands and audit entries" });
  return {
    name: "security-command timeline",
    nav: page.getByRole("navigation", { name: "Security command pages" }),
    reads,
    shownKeys: () =>
      items.locator("tbody tr").evaluateAll((rows) =>
        rows.flatMap((row) => {
          const match = /([A-Za-z][\w-]*) #(\d+) · archive /.exec((row as HTMLElement).innerText);
          return match ? [`${match[1]} #${match[2]}`] : [];
        }),
      ),
    answerKeys: (body) =>
      body.items.flatMap((item) => {
        const evidence = item.evidence as { stream: string; streamRevision: number } | null | undefined;
        return evidence ? [`${evidence.stream} #${String(evidence.streamRevision)}`] : [];
      }),
  };
}

/** An unavailable paging control stays focusable, says it is unavailable, and Enter, Space and a mouse click change nothing. */
async function expectInert(page: Page, screen: PagedScreen, control: Locator, label: string) {
  const status = screen.nav.locator("p[aria-live=polite]");
  const shown = await status.textContent();
  const requests = screen.reads.count();
  await control.focus();
  await expect(control, `${label}: still focusable`).toBeFocused();
  await expectVisibleFocus(page, label);
  await expect(control, `${label}: unavailable`).toHaveAttribute("aria-disabled", "true");
  await page.keyboard.press("Enter");
  await page.keyboard.press("Space");
  await control.click({ force: true }); // a real mouse click; force skips only Playwright's own wait for an available control
  // Read once, not polled: an activation would have re-rendered the page label at once (a discrete event), and any read it caused
  // is also caught by the exact read counts the caller checks once the page has loaded.
  expect(await status.textContent(), `${label}: the page and cursor are unchanged`).toBe(shown);
  expect(screen.reads.count(), `${label}: no read was requested`).toBe(requests);
  await expect(control, `${label}: focus stays`).toBeFocused();
}

/**
 * Keyboard paging of a screen whose default view has at least two pages (ensureSecondPage), from page 1 to page 2 and back, with
 * each read held until the loading state has been checked. The page label announces a page only once its rows have arrived; while
 * a page loads, both controls are unavailable but keep keyboard focus, and nothing activates them (on the first page Previous is
 * unavailable too). Each move makes exactly one read, with the cursor the previous answer gave, and shows exactly that answer.
 */
async function checkKeyboardPaging(page: Page, screen: PagedScreen) {
  const { name, nav, reads } = screen;
  const status = nav.locator("p[aria-live=polite]");
  const previous = nav.getByRole("button", { name: "Previous" });
  const next = nav.getByRole("button", { name: "Next" });

  // Page 1 is shown, and it is the last answer read; Previous is unavailable on the first page.
  await expect(status).toHaveText(/^Page 1 ·/);
  await expect(previous).toHaveAttribute("aria-disabled", "true");
  await expect(next).toHaveAttribute("aria-disabled", "false");
  const pageOne = reads.lastAnswer();
  expect(pageOne.cursor, `${name}: page 1 is read without a cursor`).toBeNull();
  expect(pageOne.body.nextCursor, `${name}: page 1 has a next cursor`).not.toBeNull();
  expect(await screen.shownKeys(), `${name}: page 1 shows its answer`).toEqual(screen.answerKeys(pageOne.body));
  await expectInert(page, screen, previous, `${name}: Previous on the first page`);

  // Forward to page 2, the read held while the loading state is checked.
  const beforeNext = reads.count();
  reads.hold();
  await next.focus();
  await expectVisibleFocus(page, `${name}: next page button`);
  await page.keyboard.press("Enter");
  await expect.poll(() => reads.held(), { message: `${name}: page 2 is requested` }).toBe(1);
  expect(reads.count()).toBe(beforeNext + 1);
  expect(reads.lastCursor(), `${name}: page 2 is read with page 1's next cursor`).toBe(pageOne.body.nextCursor);
  await expect(status, `${name}: page 2 is not announced while page 1's rows are still shown`).toHaveText("Loading page 2…");
  await expect(next, `${name}: focus stays on Next while it is unavailable`).toBeFocused();
  await expect(next).toHaveAttribute("aria-disabled", "true");
  await expect(previous).toHaveAttribute("aria-disabled", "true");
  await expectInert(page, screen, previous, `${name}: Previous while page 2 loads`);
  await expectInert(page, screen, next, `${name}: Next while page 2 loads`);
  reads.release();
  await expect(status).toHaveText(/^Page 2 ·/);
  const pageTwo = reads.lastAnswer();
  expect(pageTwo.cursor).toBe(pageOne.body.nextCursor);
  expect(await screen.shownKeys(), `${name}: page 2 shows its answer as soon as it is announced`).toEqual(screen.answerKeys(pageTwo.body));
  expect(screen.answerKeys(pageTwo.body).length, `${name}: page 2 has rows`).toBeGreaterThan(0);
  expect(screen.answerKeys(pageTwo.body).filter((key) => screen.answerKeys(pageOne.body).includes(key)), `${name}: page 2 lists other rows than page 1`).toEqual([]);
  await expect(previous).toHaveAttribute("aria-disabled", "false");
  await expect(next).toBeFocused();
  expect(reads.count(), `${name}: one read for page 2`).toBe(beforeNext + 1);

  // Back to page 1: its earlier rows are shown again at once and refreshed; the refresh is held while the controls are checked.
  const beforePrevious = reads.count();
  reads.hold();
  await previous.focus();
  await expectVisibleFocus(page, `${name}: previous page button`);
  await page.keyboard.press("Enter");
  await expect.poll(() => reads.held(), { message: `${name}: page 1 is requested again` }).toBe(1);
  expect(reads.count()).toBe(beforePrevious + 1);
  expect(reads.lastCursor(), `${name}: page 1 is read without a cursor`).toBeNull();
  await expect(status).toHaveText(/^Page 1 ·/);
  expect(await screen.shownKeys(), `${name}: page 1's earlier rows are shown`).toEqual(screen.answerKeys(pageOne.body));
  await expect(previous, `${name}: focus stays on Previous, unavailable on the first page`).toBeFocused();
  await expect(previous).toHaveAttribute("aria-disabled", "true");
  await expect(next).toHaveAttribute("aria-disabled", "true");
  await expectInert(page, screen, next, `${name}: Next while page 1 is refreshed`);
  await expectInert(page, screen, previous, `${name}: Previous on the first page while it is refreshed`);
  reads.release();
  await expect(next).toHaveAttribute("aria-disabled", "false"); // page 1 has been read again
  const refreshed = reads.lastAnswer();
  expect(refreshed.cursor).toBeNull();
  expect(await screen.shownKeys(), `${name}: page 1 shows its new answer`).toEqual(screen.answerKeys(refreshed.body));
  await expect(status).toHaveText(/^Page 1 ·/);
  await expect(previous).toHaveAttribute("aria-disabled", "true");
  await expect(previous).toBeFocused();
  expect(reads.count(), `${name}: one read for page 1`).toBe(beforePrevious + 1);
}

/**
 * Keyboard Refresh of a paged screen on page 1, its read held while it loads: Enter starts exactly one read (of page 1); Refresh
 * keeps focus and is unavailable meanwhile, and Enter, Space and a mouse click start no second read; once the page has been read,
 * Refresh is available with focus, the page shows that answer, and Enter refreshes again.
 */
async function checkKeyboardRefresh(page: Page, screen: PagedScreen) {
  const { name, nav, reads } = screen;
  const refresh = page.getByRole("button", { name: "Refresh", exact: true });
  await expect(nav.locator("p[aria-live=polite]")).toHaveText(/^Page 1 ·/);
  await expect(refresh).toHaveAttribute("aria-disabled", "false");
  const before = reads.count();
  reads.hold();
  await refresh.focus();
  await expectVisibleFocus(page, `${name}: refresh`);
  await page.keyboard.press("Enter");
  await expect.poll(() => reads.held(), { message: `${name}: the refresh is requested` }).toBe(1);
  expect(reads.count(), `${name}: one read for the refresh`).toBe(before + 1);
  expect(reads.lastCursor(), `${name}: the refresh reads page 1`).toBeNull();
  await expect(refresh, `${name}: focus stays on Refresh while it is unavailable`).toBeFocused();
  await expect(refresh).toHaveAttribute("aria-disabled", "true");
  await expectVisibleFocus(page, `${name}: refresh while loading`);
  await page.keyboard.press("Enter");
  await page.keyboard.press("Space");
  await refresh.click({ force: true }); // a real mouse click; force skips only Playwright's own wait for an available control
  expect(reads.count(), `${name}: no second read while the refresh loads`).toBe(before + 1);
  await expect(refresh).toBeFocused();
  reads.release();
  await expect(refresh, `${name}: the refresh has completed`).toHaveAttribute("aria-disabled", "false");
  await expect(refresh).toBeFocused();
  expect(await screen.shownKeys(), `${name}: the page shows the refreshed answer`).toEqual(screen.answerKeys(reads.lastAnswer().body));
  expect(reads.count(), `${name}: still one read for the refresh`).toBe(before + 1);

  // Focus is still usable: Enter refreshes again.
  await page.keyboard.press("Enter");
  await expect.poll(() => reads.count(), { message: `${name}: a second refresh is requested` }).toBe(before + 2);
  await expect(refresh).toHaveAttribute("aria-disabled", "false");
  await expect(refresh).toBeFocused();
}

const FAILURE_CODE = "SYNTHETIC_PAGE_READ_FAILURE";
const FAILURE_DETAIL = "Synthetic page-read failure injected by the security-audit browser test.";

/**
 * A failed page move (ensureSecondPage guarantees page 2). Only the first page-2 read is changed: it is answered here with a
 * synthetic, non-retryable 500 (a server error, not an authorization or security answer); every other read reaches the server
 * through `reads` unchanged, and the interception is removed afterwards. The rows and paging controls are replaced by the error
 * panel, which keeps its single role=alert; keyboard focus, which was on Next, moves once to the error container (not a live
 * region and not a Tab stop). Tab reaches "Try again", which retries the same page; focus then moves to the page navigation (Next
 * and Previous are unavailable while the retry loads), the page is announced once it has arrived, and keyboard paging continues.
 */
async function checkPagingFailure(page: Page, screen: PagedScreen, url: RegExp) {
  const { name, nav, reads } = screen;
  const status = nav.locator("p[aria-live=polite]");
  const previous = nav.getByRole("button", { name: "Previous" });
  const next = nav.getByRole("button", { name: "Next" });
  const alert = page.getByRole("alert");
  let failures = 0;
  const failFirstPageTwoRead = async (route: Route) => {
    if (failures === 0 && new URL(route.request().url()).searchParams.get("cursor") !== null) {
      failures += 1;
      await route.fulfill({ status: 500, contentType: "application/problem+json", body: JSON.stringify({ title: FAILURE_CODE, status: 500, detail: FAILURE_DETAIL, retryable: false }) });
    } else {
      await route.fallback();
    }
  };
  await page.context().route(url, failFirstPageTwoRead);
  try {
    await expect(status).toHaveText(/^Page 1 ·/);
    await expect(next).toHaveAttribute("aria-disabled", "false");
    const pageOne = reads.lastAnswer();
    const before = reads.count();

    // The move to page 2 fails.
    await next.focus();
    await expectVisibleFocus(page, `${name}: next page button`);
    await page.keyboard.press("Enter");
    await expect(alert).toBeVisible();
    expect(failures, `${name}: the page-2 read failed once`).toBe(1);
    await expect(alert).toContainText("The request failed");
    await expect(alert).toContainText(FAILURE_DETAIL);
    await expect(alert).toContainText(`Code ${FAILURE_CODE}`);
    await expect(alert, `${name}: one announcement`).toHaveCount(1);
    await expect(nav, `${name}: the paging controls are replaced by the error`).toHaveCount(0);

    // Focus moved to the error container, which holds the alert and is neither a live region nor a Tab stop.
    const focusedError = () =>
      page.evaluate(() => {
        const active = document.activeElement as HTMLElement | null;
        const shown = document.querySelector('[role="alert"]');
        return active && shown && active !== document.body && active.contains(shown) ? { tabIndex: active.tabIndex, role: active.getAttribute("role"), live: active.getAttribute("aria-live"), text: active.textContent ?? "" } : null;
      });
    await expect.poll(focusedError, { message: `${name}: focus is on the error` }).not.toBeNull();
    const target = (await focusedError())!;
    expect(target.tabIndex, `${name}: the error is focusable without being a Tab stop`).toBe(-1);
    expect(target.role, `${name}: the error container adds no second announcement`).toBeNull();
    expect(target.live).toBeNull();
    expect(target.text).toContain(FAILURE_DETAIL);
    await expectVisibleFocus(page, `${name}: error`);
    await scan(page, `${name} page-read error (focused)`);

    // Recovery: Tab reaches Try again; it retries the same page and focus follows to the page navigation.
    const retry = alert.getByRole("button", { name: "Try again" });
    await page.keyboard.press("Tab");
    await expect(retry, `${name}: Tab from the error reaches Try again`).toBeFocused();
    await expectVisibleFocus(page, `${name}: try again`);
    expect(await focusedError(), `${name}: focus is not moved back to the error`).toBeNull();
    reads.hold();
    await page.keyboard.press("Enter");
    await expect.poll(() => reads.held(), { message: `${name}: page 2 is read again` }).toBe(1);
    expect(reads.lastCursor(), `${name}: the same page is retried`).toBe(pageOne.body.nextCursor);
    await expect(alert).toHaveCount(0);
    await expect(nav, `${name}: focus follows to the page navigation`).toBeFocused();
    await expectVisibleFocus(page, `${name}: page navigation`);
    await expect(status).toHaveText("Loading page 2…");
    await expect(next).toHaveAttribute("aria-disabled", "true");
    await expect(previous).toHaveAttribute("aria-disabled", "true");
    reads.release();
    await expect(status).toHaveText(/^Page 2 ·/);
    const pageTwo = reads.lastAnswer();
    expect(pageTwo.cursor).toBe(pageOne.body.nextCursor);
    expect(await screen.shownKeys(), `${name}: page 2 shows its answer`).toEqual(screen.answerKeys(pageTwo.body));
    await expect(nav, `${name}: the arrival does not move focus`).toBeFocused();
    expect(failures, `${name}: the retry was not failed`).toBe(1);
    expect(reads.count(), `${name}: only the retry reached the server`).toBe(before + 1);

    // Keyboard paging continues from the navigation: Tab reaches Previous, which returns to page 1.
    await page.keyboard.press("Tab");
    await expect(previous).toBeFocused();
    await expect(previous).toHaveAttribute("aria-disabled", "false");
    await page.keyboard.press("Enter");
    await expect(next).toHaveAttribute("aria-disabled", "false"); // page 1 has been read again
    await expect(status).toHaveText(/^Page 1 ·/);
    await expect(previous).toBeFocused();
    expect(await screen.shownKeys(), `${name}: page 1 shows its new answer`).toEqual(screen.answerKeys(reads.lastAnswer().body));
  } finally {
    await page.context().unroute(url, failFirstPageTwoRead);
  }
}

async function openSecurityHome(page: Page) {
  await page.goto("/app/security");
  await expect(page.getByRole("heading", { level: 1, name: "Security home" })).toBeVisible();
}

/** Each Authority-backed section either shows its content or says it is not available to the role; nothing else. */
async function expectHomeSections(page: Page, allowed: readonly string[]) {
  for (const title of HOME_SECTIONS) {
    const section = card(page, title);
    if (allowed.includes(title)) {
      await expect(section.getByText(/^As of /)).toBeVisible({ timeout: 30_000 });
      await expect(section.getByText(NOT_FOR_ROLE)).toHaveCount(0);
    } else {
      await expect(section.getByText(NOT_FOR_ROLE)).toBeVisible({ timeout: 30_000 });
    }
  }
}

/** A People-page role change by the Security Administrator: reason code, development-only step-up, confirm. */
async function changeAuditorRole(page: Page, action: "Make Security Auditor" | "Remove Security Auditor", reasonLabel: string, confirm: string) {
  await page.goto("/app/organization/users");
  const row = peopleRow(page, AUDITOR);
  await expect(row).toBeVisible();
  await row.getByRole("button", { name: /^Actions for/ }).click();
  await page.getByRole("menuitem", { name: new RegExp(`^${action}:`) }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("button", { name: confirm })).toBeDisabled();
  await dialog.getByRole("combobox", { name: /Reason/ }).selectOption({ label: reasonLabel });
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await dialog.getByRole("button", { name: confirm }).click();
  await expect(dialog).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: "Change recorded" })).toBeVisible();
}

async function rileyIsAuditor(page: Page): Promise<boolean> {
  await page.goto("/app/organization/users");
  const row = peopleRow(page, AUDITOR);
  await expect(row).toBeVisible();
  return (await row.getByText("Security auditor").count()) > 0;
}

async function withPersona<T>(browser: Browser, persona: Parameters<typeof signIn>[1], run: (page: Page) => Promise<T>): Promise<T> {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  try {
    const page = await context.newPage();
    await signIn(page, persona);
    return await run(page);
  } finally {
    await context.close();
  }
}

test.describe("Security Home and audit (C8 M4-5)", () => {
  test.afterAll(async ({ browser }) => {
    // Restore the shared organization if a failure left Riley as an auditor (the revocation test normally removes the role).
    await withPersona(browser, "security", async (page) => {
      if (await rileyIsAuditor(page)) {
        await changeAuditorRole(page, "Remove Security Auditor", "Assignment ended", "Remove auditor");
      }
    });
  });

  test("owner, designer and clerk have no security navigation and every direct route refuses", async ({ page }) => {
    test.setTimeout(240_000);
    for (const persona of ["owner", "designer", "clerk"] as const) {
      await signIn(page, persona);
      await expect(primary(page).getByRole("link", { name: "Security home", exact: true })).toHaveCount(0);
      await expect(primary(page).getByRole("link", { name: "Audit", exact: true })).toHaveCount(0);

      await openSecurityHome(page);
      await expectHomeSections(page, []);
      await expect(card(page, "Search approvals").getByText(LIVE_CHECK)).toBeVisible();
      await expect(card(page, "Security incidents").getByText(LIVE_CHECK)).toBeVisible();

      for (const route of ["/app/security/audit", "/app/security/audit/commands", `/app/security/audit/${crypto.randomUUID()}`]) {
        await page.goto(route);
        await expect(page.getByText(/^No permission/), `${persona} ${route}`).toBeVisible();
        await expect(page.getByRole("table")).toHaveCount(0);
      }

      await signOut(page);
    }
  });

  test("a Security Administrator sees every Security Home widget, refreshes by keyboard, and has no audit explorer", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "security");
    await expect(primary(page).getByRole("link", { name: "Security home", exact: true })).toBeVisible();
    await expect(primary(page).getByRole("link", { name: "Audit", exact: true })).toHaveCount(0); // D8-6
    await primary(page).getByRole("link", { name: "Security home", exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Security home" })).toBeVisible();
    await expect(primary(page).getByRole("link", { name: "Security home", exact: true })).toHaveAttribute("aria-current", "page");

    await expectHomeSections(page, HOME_SECTIONS);
    await expect(card(page, "Search approvals").getByRole("link", { name: "Manage search approvals" })).toBeVisible();
    await expect(card(page, "Security incidents").getByRole("link", { name: "All incidents" })).toBeVisible();
    await expect(card(page, "Encryption coverage").getByText(/organization-wide/).first()).toBeVisible();

    // Keyboard: reach a section's refresh control, see its focus, and refresh that section only.
    const refreshGate = page.getByRole("button", { name: "Refresh authorization gate" });
    await page.locator("body").click({ position: { x: 1, y: 1 } });
    await tabTo(page, refreshGate, "refresh authorization gate");
    const gateRequest = page.waitForResponse((response) => response.url().includes("/api/v1/security/overview/gate"));
    await page.keyboard.press("Enter");
    expect((await gateRequest).status()).toBe(200);
    await expect(card(page, "Authorization gate").getByText(/^As of /)).toBeVisible();

    await page.goto("/app/security/audit");
    await expect(page.getByText(/^No permission/)).toBeVisible();
    await signOut(page);
  });

  test("a Security Administrator appoints Riley Reader as Security Auditor through the People page", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "security");
    expect(await rileyIsAuditor(page), "Riley must start without the auditor role (organization not restored by an earlier run)").toBe(false);
    await changeAuditorRole(page, "Make Security Auditor", "Assigned to audit coverage", "Appoint");
    expect(await rileyIsAuditor(page)).toBe(true);
    await signOut(page);
  });

  test("a Security Auditor reads archive health, the explorer, an event and the timeline, by keyboard where it matters", async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page, "reader");
    await expect(primary(page).getByRole("link", { name: "Security home", exact: true })).toBeVisible();
    await expect(primary(page).getByRole("link", { name: "Audit", exact: true })).toBeVisible();
    await ensureSecondPage(page, "/api/v1/security/audit-events", "audit explorer");
    const explorerReads = await pageReads(page, EXPLORER_READS);

    // Security Home: archive only (D8-70); the live-check widgets request nothing for this role.
    await openSecurityHome(page);
    await expectHomeSections(page, ["Archive delivery"]);
    await expect(card(page, "Search approvals").getByText(LIVE_CHECK)).toBeVisible();
    await expect(card(page, "Security incidents").getByText(LIVE_CHECK)).toBeVisible();

    // Audit explorer (PG-054).
    await primary(page).getByRole("link", { name: "Audit", exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Audit explorer" })).toBeVisible();
    const events = page.getByRole("table", { name: "Audit events" });
    await expect(events).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole("navigation", { name: "Audit views" }).getByRole("link", { name: "Audit events" })).toHaveAttribute("aria-current", "page");

    // Keyboard filters: an invalid action code is reported next to its field and refused; clearing it restores the list.
    const actionCode = page.getByRole("textbox", { name: "Action code" });
    await page.locator("body").click({ position: { x: 1, y: 1 } });
    await tabTo(page, actionCode, "action code filter");
    await page.keyboard.type("not a code");
    await expect(actionCode).toHaveAttribute("aria-invalid", "true");
    await page.keyboard.press("Control+A");
    await page.keyboard.press("Delete");
    await expect(actionCode).not.toHaveAttribute("aria-invalid", "true");
    await expect(events).toBeVisible({ timeout: 30_000 });

    // Keyboard refresh of the explorer.
    const refresh = page.getByRole("button", { name: "Refresh", exact: true });
    await tabTo(page, refresh, "explorer refresh");
    const refreshed = page.waitForResponse((response) => response.url().includes("/api/v1/security/audit-events?"));
    await page.keyboard.press("Enter");
    expect((await refreshed).status()).toBe(200);

    // Paging by keyboard: a second page always exists (ensureSecondPage); loading, boundaries and focus as in checkKeyboardPaging.
    await checkKeyboardPaging(page, explorerScreen(page, explorerReads));

    // Event detail (PG-055), opened from the list by keyboard.
    const open = events.getByRole("link", { name: /^Open event / }).first();
    await open.focus();
    await expectVisibleFocus(page, "open event link");
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/app\/security\/audit\/[0-9a-f-]{36}$/);
    await expect(page.getByText(/^Not independently verified/)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole("link", { name: "All audit events" })).toBeVisible();

    // Security-command timeline (AC-8.2): Riley's own appointment is a command on it.
    await page.goto("/app/security/audit");
    await page.getByRole("navigation", { name: "Audit views" }).getByRole("link", { name: "Security commands" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Security commands" })).toBeVisible();
    await expect(page.getByRole("table", { name: "Security commands and audit entries" })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole("navigation", { name: "Security command pages" }).getByRole("button", { name: "Previous" })).toBeDisabled();
    await signOut(page);
  });

  test("a Security Auditor pages the security-command timeline by keyboard, and unavailable paging controls do nothing", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "reader");
    await ensureSecondPage(page, "/api/v1/security/audit/security-commands", "security-command timeline");
    const timelineReads = await pageReads(page, TIMELINE_READS);
    await page.goto("/app/security/audit/commands");
    await expect(page.getByRole("heading", { level: 1, name: "Security commands" })).toBeVisible();
    await expect(page.getByRole("table", { name: "Security commands and audit entries" })).toBeVisible({ timeout: 30_000 });
    await checkKeyboardPaging(page, timelineScreen(page, timelineReads));
    await signOut(page);
  });

  for (const target of [
    { name: "audit explorer", path: "/app/security/audit", api: "/api/v1/security/audit-events", reads: EXPLORER_READS, table: "Audit events", screen: explorerScreen },
    { name: "security-command timeline", path: "/app/security/audit/commands", api: "/api/v1/security/audit/security-commands", reads: TIMELINE_READS, table: "Security commands and audit entries", screen: timelineScreen },
  ] as const) {
    test(`a failed ${target.name} page read moves focus to the error, and Try again resumes keyboard paging`, async ({ page }) => {
      test.setTimeout(180_000);
      await signIn(page, "reader");
      await ensureSecondPage(page, target.api, target.name);
      const reads = await pageReads(page, target.reads);
      await page.goto(target.path);
      await expect(page.getByRole("table", { name: target.table })).toBeVisible({ timeout: 30_000 });
      await checkPagingFailure(page, target.screen(page, reads), target.reads);
      await signOut(page);
    });

    test(`a keyboard Refresh of the ${target.name} keeps focus and starts exactly one read`, async ({ page }) => {
      test.setTimeout(180_000);
      await signIn(page, "reader");
      await ensureSecondPage(page, target.api, target.name);
      const reads = await pageReads(page, target.reads);
      await page.goto(target.path);
      await expect(page.getByRole("table", { name: target.table })).toBeVisible({ timeout: 30_000 });
      await checkKeyboardRefresh(page, target.screen(page, reads));
      await signOut(page);
    });
  }

  test("PG-052, PG-054, PG-055 and the timeline pass axe WCAG 2.2 AA without overflow at every width", async ({ browser }) => {
    test.setTimeout(900_000);
    await withPersona(browser, "reader", async (page) => {
      await page.goto("/app/security/audit");
      const firstEvent = page.getByRole("table", { name: "Audit events" }).getByRole("link", { name: /^Open event / }).first();
      await expect(firstEvent).toBeVisible({ timeout: 30_000 });
      const eventUrl = new URL((await firstEvent.getAttribute("href"))!, page.url()).pathname;
      for (const viewport of VIEWPORTS) {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await openSecurityHome(page);
        await expectHomeSections(page, ["Archive delivery"]);
        await scan(page, `security home (auditor) @${viewport.name}`);
        await page.goto("/app/security/audit");
        await expect(page.getByRole("table", { name: "Audit events" })).toBeVisible({ timeout: 30_000 });
        await scan(page, `audit explorer @${viewport.name}`);
        await page.goto(eventUrl);
        await expect(page.getByText(/^Not independently verified/)).toBeVisible({ timeout: 30_000 });
        await scan(page, `audit event detail @${viewport.name}`);
        await page.goto("/app/security/audit/commands");
        await expect(page.getByRole("table", { name: "Security commands and audit entries" })).toBeVisible({ timeout: 30_000 });
        await scan(page, `security-command timeline @${viewport.name}`);
      }
    });
    await withPersona(browser, "security", async (page) => {
      for (const viewport of VIEWPORTS) {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await openSecurityHome(page);
        await expectHomeSections(page, HOME_SECTIONS);
        await scan(page, `security home (administrator) @${viewport.name}`);
      }
    });
  });

  test("an auditor's next request is refused once a Security Administrator removes the role in another session", async ({ browser }) => {
    test.setTimeout(180_000);
    const auditorContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    try {
      const auditor = await auditorContext.newPage();
      await signIn(auditor, "reader");
      await auditor.goto("/app/security/audit");
      await expect(auditor.getByRole("table", { name: "Audit events" })).toBeVisible({ timeout: 30_000 });

      // A second, independent session removes the role and waits until the change is recorded.
      await withPersona(browser, "security", async (administrator) => {
        await changeAuditorRole(administrator, "Remove Security Auditor", "Assignment ended", "Remove auditor");
        expect(await rileyIsAuditor(administrator)).toBe(false);
      });

      // The auditor's page still holds the earlier capability; the server refuses the next request.
      const refused = auditor.waitForResponse((response) => response.url().includes("/api/v1/security/audit-events?"));
      await auditor.getByRole("button", { name: "Refresh", exact: true }).click();
      expect((await refused).status()).toBe(403);
      await expect(auditor.getByText("Code AUDIT_VIEW_FORBIDDEN", { exact: false })).toBeVisible();
      await expect(auditor.getByText("The authorization authority denied this request. Nothing was changed.")).toBeVisible();

      // The Security Home archive section is refused too.
      await openSecurityHome(auditor);
      await expectHomeSections(auditor, []);
    } finally {
      await auditorContext.close();
    }
  });
});
