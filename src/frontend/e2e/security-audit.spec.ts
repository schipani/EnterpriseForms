import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Browser, type Locator, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C8 M4-5 (AC-M4-5.1; PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §8.3): the Security Home (PG-052), audit explorer (PG-054), audit event
 * detail (PG-055) and security-command timeline in the real application (business API, Authorization Authority, SpiceDB; no mocks),
 * on the isolated security-test stack only (the global setup's isolation guard; `signIn` refuses any unverified origin).
 *  1. Owner, designer and clerk: no Security home or Audit navigation, and every direct route refuses.
 *  2. Security Administrator: every Security Home widget; no audit explorer (D8-6).
 *  3. Riley Reader is appointed Security Auditor through the People page (there is no auditor seed persona).
 *  4. Security Auditor: Security Home archive only; explorer, event detail and timeline; keyboard filters, refresh and paging.
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

    // Paging: Previous is disabled on the first page; Next is enabled only when the server returned a cursor, and then works.
    const pages = page.getByRole("navigation", { name: "Audit event pages" });
    await expect(pages.getByText(/^Page 1 ·/)).toBeVisible();
    await expect(pages.getByRole("button", { name: "Previous" })).toBeDisabled();
    const next = pages.getByRole("button", { name: "Next" });
    if (await next.isEnabled()) {
      await next.focus();
      await page.keyboard.press("Enter");
      await expect(pages.getByText(/^Page 2 ·/)).toBeVisible({ timeout: 30_000 });
      await pages.getByRole("button", { name: "Previous" }).focus();
      await page.keyboard.press("Enter");
      await expect(pages.getByText(/^Page 1 ·/)).toBeVisible({ timeout: 30_000 });
    }

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
