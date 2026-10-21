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
  await expect(page.getByRole("tab", { name: "Design" })).toBeVisible();
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
      // C1 designer rebuild: the Design view (sections canvas + inspector) is the default view.
      await scan(page, `${viewport.id} designer design view @${viewport.name}`);
      // Select a tile so the Field Properties inspector is rendered (on narrow widths it is behind the Properties pane switch).
      await page.getByRole("region", { name: "Form canvas" }).locator("button[aria-keyshortcuts]").first().click();
      if (viewport.width < 1024) {
        await page.getByRole("button", { name: "Properties", exact: true }).click();
      }
      const inspector = page.getByRole("complementary", { name: /Properties$/ });
      await expect(inspector).toBeVisible();
      await scan(page, `${viewport.id} designer quick inspector @${viewport.name}`);
      // Phase C3 designer UX refinement: Validation/Conditional rules/Security & Encryption/Advanced (and, for a
      // dropdown field, Dropdown choices and dependent choices) live in the large Configure Field modal
      // (90vw/1200px, full-screen below sm — this exercises exactly that at the 200%/400% zoom equivalent
      // viewports), opened from the quick inspector's prominent button. There is no General tab: label, type, help
      // text and placeholder live only in the quick inspector, scanned above.
      await inspector.getByRole("button", { name: "Configure Field" }).click();
      const configModal = page.getByRole("dialog", { name: /^Configure /i });
      await expect(configModal).toBeVisible();
      for (const tabName of ["Validation", "Conditional rules", "Security & Encryption", "Advanced"]) {
        await configModal.getByRole("tab", { name: tabName }).click();
        await scan(page, `${viewport.id} designer configure field ${tabName} tab @${viewport.name}`);
      }
      await configModal.getByRole("button", { name: "Cancel" }).click();
      await expect(configModal).toBeHidden();
      if (viewport.width < 1024) {
        await page.getByRole("button", { name: "Canvas", exact: true }).click();
      }
      await page.getByRole("tab", { name: /^Fields/ }).click();
      await scan(page, `${viewport.id} designer fields @${viewport.name}`);
      // New owner-acceptance UI: the Form permissions tab and the Edit field dialog get the same scans.
      await page.getByRole("tab", { name: "Form permissions" }).click();
      await expect(page.getByText(/Source: Authorization Authority/)).toBeVisible();
      await scan(page, `${viewport.id} designer form permissions @${viewport.name}`);
      await page.getByRole("tab", { name: /^Fields/ }).click();
      await page.getByRole("button", { name: /^Edit / }).first().click();
      const editDialog = page.getByRole("dialog", { name: /^Edit / });
      await expect(editDialog).toBeVisible();
      await scan(page, `${viewport.id} edit field dialog general tab @${viewport.name}`);
      // C1-UX §C: each tab of the Edit Field dialog (General/Validation/Rules/Security & Encryption/Advanced) gets its own scan.
      for (const tabName of ["Validation", "Rules", "Security & Encryption", "Advanced"]) {
        await editDialog.getByRole("tab", { name: tabName }).click();
        await scan(page, `${viewport.id} edit field dialog ${tabName} tab @${viewport.name}`);
      }
      await page.keyboard.press("Escape");
      await expect(page.getByRole("dialog", { name: /^Edit / })).toBeHidden();
      // Phase 1B: the Security & Encryption section of the Add field dialog.
      await page.getByRole("button", { name: "Add field", exact: true }).click();
      const add = page.getByRole("dialog", { name: "Add field" });
      await expect(add.getByRole("group", { name: "Security & Encryption" })).toBeVisible();
      await scan(page, `${viewport.id} add field dialog with security section @${viewport.name}`);
      await page.keyboard.press("Escape");
      await expect(add).toBeHidden();
    }
    await page.getByRole("button", { name: /Account menu/ }).click();
    await page.getByRole("menuitem", { name: "Sign out" }).click();

    // Phase 1B: the organization data-protection policy card (owner).
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, "owner");
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.goto("/app/organization/branding");
      await expect(page.getByRole("region", { name: "Data protection policy" })).toBeVisible();
      await scan(page, `${viewport.id} organization data-protection policy @${viewport.name}`);
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
    const opener = page.getByRole("button", { name: "Add field", exact: true });
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

    // Designer: keyboard reorder (Alt+ArrowDown on a focused tile, no pointer) changes element order deterministically.
    await page.getByRole("tab", { name: "Design" }).click();
    const tiles = page.getByRole("region", { name: "Form canvas" }).getByRole("list").first().locator("button[aria-keyshortcuts]");
    const before = await tiles.allTextContents();
    expect(before.length).toBeGreaterThan(1);
    await tiles.first().focus();
    await page.keyboard.press("Alt+ArrowDown");
    await expect.poll(async () => (await tiles.allTextContents())[1]).toBe(before[0]);
    const after = await tiles.allTextContents();
    expect(after[0]).toBe(before[1]);
    await expect(tiles.nth(1)).toBeFocused();
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
