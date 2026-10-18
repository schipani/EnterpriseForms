import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * PB-14 automated design validation (docs/testing/PB14_UI_DESIGN_ACCEPTANCE_TESTS.csv): axe-core WCAG 2.2 AA scans and
 * reflow checks at 320/768/1440 px plus the 200%/400% zoom equivalents (CSS viewport 720/320 px wide), keyboard-only
 * navigation of the dashboard, a record, the designer (incl. dialog focus trap and keyboard reorder), the upload
 * tray and the component gallery. Runs after accused-workflow.spec.ts (alphabetical, single worker) and uses the
 * record and forms it created. Human NVDA/VoiceOver review (PB14-16/17) is tracked separately in
 * docs/ui/PB14_MANUAL_ACCEPTANCE_CHECKLIST.md — this file does not claim it.
 */
const VIEWPORTS = [
  { id: "PB14-10", name: "320 phone", width: 320, height: 720 },
  { id: "PB14-11", name: "768 tablet", width: 768, height: 1024 },
  { id: "PB14-12", name: "1440 desktop", width: 1440, height: 900 },
  { id: "PB14-13", name: "200% zoom equivalent (720 css px)", width: 720, height: 600 },
  { id: "PB14-14", name: "400% zoom equivalent (320 css px, 256 tall)", width: 320, height: 256 },
] as const;

async function signIn(page: Page, persona: "owner" | "designer" | "clerk" | "reader") {
  await page.goto("/login");
  await page.getByRole("button", { name: new RegExp("\\(" + persona + "\\)$") }).click();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
}

async function scan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"]).disableRules(["region"]).analyze();
  const violations = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical" || v.tags.some((t) => t.startsWith("wcag")));
  expect(violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `${label}: horizontal overflow`).toBeLessThanOrEqual(0);
}

async function openLatestAccusedRecord(page: Page): Promise<string> {
  await page.goto("/app/records");
  const row = page.getByRole("row", { name: /^Accused /i }).first();
  await row.getByRole("link").first().click();
  await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 60_000 });
  return page.url();
}

async function openLatestDesigner(page: Page): Promise<void> {
  await page.goto("/app/forms");
  await page.getByRole("row", { name: /^Accused /i }).first().getByRole("link", { name: "Design" }).click();
  await expect(page.getByRole("tab", { name: "Layout" })).toBeVisible();
}

test.describe("PB-14 automated accessibility and responsive validation", () => {
  test("login, dashboard, records, record, uploads, gallery and designer pass axe WCAG 2.2 AA at every width (PB14-10..14, 22, 23, 32)", async ({ page }) => {
    test.setTimeout(600_000);
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.goto("/login");
      await expect(page.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
      await scan(page, `${viewport.id} login @${viewport.name}`);
    }

    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, "clerk");
    const recordUrl = await openLatestAccusedRecord(page);
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.goto("/app/dashboard");
      await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
      await scan(page, `${viewport.id} dashboard @${viewport.name}`);
      await page.goto("/app/records");
      await expect(page.getByRole("heading", { level: 1, name: "Records" })).toBeVisible();
      await scan(page, `${viewport.id} records @${viewport.name}`);
      await page.goto(recordUrl);
      await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 60_000 });
      await scan(page, `${viewport.id} record details @${viewport.name}`);
      await page.getByRole("tab", { name: "Access" }).click();
      await scan(page, `${viewport.id} record access @${viewport.name}`);
      await page.goto("/app/uploads");
      await expect(page.getByRole("heading", { level: 1, name: "Upload Center" })).toBeVisible();
      await scan(page, `${viewport.id} uploads @${viewport.name}`);
      await page.goto("/app/gallery");
      await expect(page.getByRole("heading", { name: "Component gallery" })).toBeVisible();
      await scan(page, `${viewport.id} gallery @${viewport.name}`);
    }
    await page.getByRole("button", { name: /Account menu/ }).click();
    await page.getByRole("menuitem", { name: "Sign out" }).click();

    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, "designer");
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await openLatestDesigner(page);
      await scan(page, `${viewport.id} designer fields @${viewport.name}`);
      await page.getByRole("tab", { name: "Layout" }).click();
      await scan(page, `${viewport.id} designer layout @${viewport.name}`);
      // New owner-acceptance UI: the Form permissions tab and the Edit field dialog get the same scans.
      await page.getByRole("tab", { name: "Form permissions" }).click();
      await expect(page.getByText(/Source: Authorization Authority/)).toBeVisible();
      await scan(page, `${viewport.id} designer form permissions @${viewport.name}`);
      await page.getByRole("tab", { name: /^Fields/ }).click();
      await page.getByRole("button", { name: /^Edit / }).first().click();
      await expect(page.getByRole("dialog", { name: /^Edit / })).toBeVisible();
      await scan(page, `${viewport.id} edit field dialog @${viewport.name}`);
      await page.keyboard.press("Escape");
      await expect(page.getByRole("dialog", { name: /^Edit / })).toBeHidden();
    }
  });

  test("keyboard only: skip link, tabs, dialog focus trap and designer reorder (PB14-15, 25, 29)", async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, "clerk");
    // Skip link is the first focusable element on every page and lands in main.
    await page.goto("/app/dashboard");
    await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
    // A freshly loaded document has no focused element; the first Tab must land on the skip link.
    await expect(async () => {
      if (await page.evaluate(() => document.activeElement === document.body || document.activeElement === null)) {
        await page.keyboard.press("Tab");
      }
      await expect(page.locator(":focus")).toHaveText(/Skip to main content/, { timeout: 1_000 });
    }).toPass({ timeout: 15_000 });
    await page.keyboard.press("Enter");
    expect(await page.evaluate(() => document.activeElement?.closest("main") !== null || document.activeElement?.tagName === "MAIN")).toBe(true);

    // Record tabs follow the WAI-ARIA tabs pattern: arrow keys move the active tab without a mouse.
    const recordUrl = await openLatestAccusedRecord(page);
    await page.getByRole("tab", { name: "Details" }).focus();
    await page.keyboard.press("ArrowRight");
    await expect(page.getByRole("tab", { name: "Access" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByText("Your permissions on this record")).toBeVisible();
    await page.keyboard.press("ArrowRight");
    await expect(page.getByRole("tab", { name: "Activity" })).toHaveAttribute("aria-selected", "true");
    expect(recordUrl).toContain("/app/records/");
    await page.getByRole("button", { name: /Account menu/ }).click();
    await page.getByRole("menuitem", { name: "Sign out" }).click();

    // Designer: dialog traps focus and closes on Escape, returning focus to the opener.
    await signIn(page, "designer");
    await openLatestDesigner(page);
    await page.getByRole("tab", { name: /^Fields/ }).click();
    const opener = page.getByRole("button", { name: "Add field" }).first();
    await opener.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Add field" });
    await expect(dialog).toBeVisible();
    for (let i = 0; i < 25; i++) {
      await page.keyboard.press("Tab");
      expect(await page.evaluate(() => document.activeElement?.closest("[role=dialog]") !== null)).toBe(true);
    }
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused();

    // Designer: keyboard reorder through the accessible Move buttons changes element order deterministically.
    await page.getByRole("tab", { name: "Layout" }).click();
    const items = page.getByRole("listitem").filter({ hasText: /Field: / });
    const before = await items.allTextContents();
    expect(before.length).toBeGreaterThan(1);
    // The first element's "Move … down" button is the first enabled mover in document order.
    const moveDown = page.getByRole("button", { name: /^Move Field: .* down$/ }).first();
    await expect(moveDown).toBeEnabled();
    const movedTitle = (await moveDown.getAttribute("aria-label"))!.replace(/^Move /, "").replace(/ down$/, "");
    await moveDown.focus();
    await page.keyboard.press("Enter");
    const after = await items.allTextContents();
    expect(after[1]).toContain(movedTitle);
    expect(after[0]).not.toContain(movedTitle);
    await expect(page.getByText("Unsaved changes")).toBeVisible();
  });

  test("forced-colors and prefers-reduced-motion do not hide focus or state (PB14-30)", async ({ browser }) => {
    const context = await browser.newContext({ colorScheme: "light", forcedColors: "active", reducedMotion: "reduce", viewport: { width: 1440, height: 900 } });
    const page = await context.newPage();
    await page.goto("/login");
    const persona = page.getByRole("button", { name: /\(reader\)$/ });
    await expect(persona).toBeVisible();
    await persona.focus();
    expect(await page.evaluate(() => document.activeElement?.tagName)).toBe("BUTTON");
    // Focus must be conveyed by an outline (forced-colors mode drops box-shadow rings), not by colour alone.
    const outline = await page.evaluate(() => getComputedStyle(document.activeElement as Element).outlineStyle);
    expect(outline).not.toBe("none");
    await scan(page, "PB14-30 login forced-colors");
    await context.close();
  });
});
