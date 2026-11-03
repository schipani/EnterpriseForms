import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * People directory layout (C8 M2 clean-up): a Person cell with the verified identity beneath the name, compact rows with no
 * split words or badges, one Actions menu per row, no horizontal overflow at desktop, tablet and phone widths, and the narrow
 * layout as cards. Read-only checks (no data is changed); runs on the isolated security-test stack only.
 */
const screenshotDir = process.env["PEOPLE_LAYOUT_SHOTS"];

async function openPeople(page: Page, width: number, height = 900) {
  await page.setViewportSize({ width, height });
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
  await expect(page.getByRole("table", { name: "People" }).getByRole("row").nth(1)).toBeVisible();
}

const noHorizontalOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);

/** Every badge and name line stays on one line (a wrapped badge is taller than ~1.6 lines of 12px text). */
async function noSplitWords(page: Page) {
  const tall = await page.evaluate(() => {
    const table = document.querySelector('[role="table"][aria-label="People"]')!;
    const badges = [...table.querySelectorAll("span.inline-flex")].filter((b) => (b as HTMLElement).offsetHeight > 28).map((b) => b.textContent);
    const names = [...table.querySelectorAll('[role="rowheader"] > div')].filter((n) => (n as HTMLElement).offsetHeight > 24).map((n) => n.textContent);
    return { badges, names };
  });
  expect(tall).toEqual({ badges: [], names: [] });
}

test("desktop: Person cell, compact aligned rows, one Actions menu, no overflow", async ({ page }) => {
  await signIn(page, "owner");
  for (const width of [1440, 1600]) {
    await openPeople(page, width);
    const table = page.getByRole("table", { name: "People" });
    await expect(table.getByRole("columnheader")).toHaveText(["Person", "Roles", "Membership", "Presence", "Teams", "Actions"]);
    await expect(table.getByRole("columnheader", { name: "Verified identity" })).toHaveCount(0);
    const owner = table.getByRole("row", { name: /Avery Owner/ });
    await expect(owner.getByRole("rowheader")).toContainText("Avery Owner");
    await expect(owner.getByRole("rowheader")).toContainText("dev-owner · Test identity provider");
    await expect(owner.getByRole("rowheader")).toContainText("(you)");
    await expect(owner.locator('[title^="dev-owner · https://"]')).toHaveCount(1); // full issuer reachable as the tooltip
    await expect(owner).toContainText("Owner");
    await expect(owner).toContainText("Designer");
    expect(await noHorizontalOverflow(page)).toBe(true);
    await noSplitWords(page);
    const heights = await table.getByRole("row").evaluateAll((rows) => rows.slice(1).map((r) => (r as HTMLElement).offsetHeight));
    expect(Math.max(...heights), `row heights at ${width}px: ${heights}`).toBeLessThanOrEqual(96);
    // each person has at most one Actions button; no stand-alone action buttons and no "…" menus remain
    await expect(table.getByRole("button", { name: /^More actions/ })).toHaveCount(0);
    await expect(table.getByRole("button", { name: /^Make designer:/ })).toHaveCount(0);
    if (screenshotDir) {
      await page.screenshot({ path: `${screenshotDir}/people-${width}.png`, fullPage: true });
    }
  }
  await signOut(page);
});

test("the Actions menu works with the keyboard and names every action with its visible text", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page, 1440);
  const table = page.getByRole("table", { name: "People" });
  const trigger = table.getByRole("row", { name: /Dana Designer/ }).getByRole("button", { name: /^Actions for Dana Designer/ });
  await trigger.focus();
  await expect(trigger).toBeFocused();
  await page.keyboard.press("Enter");
  const items = page.getByRole("menuitem");
  await expect(items.first()).toBeVisible();
  const names = await items.evaluateAll((els) => els.map((e) => ({ label: e.getAttribute("aria-label") ?? "", text: (e.textContent ?? "").trim() })));
  expect(names.length).toBeGreaterThan(0);
  for (const { label, text } of names) {
    expect(label.startsWith(text), `${label} should start with ${text}`).toBe(true);
  }
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Escape");
  await expect(items).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
    .analyze()
    .then((r) => expect(r.violations.map((v) => v.id)).toEqual([]));
  await signOut(page);
});

test("tablet and phone: rows become cards without horizontal overflow", async ({ page }) => {
  await signIn(page, "owner");
  for (const width of [1024, 768, 320]) {
    await openPeople(page, width, 1000);
    if (screenshotDir) {
      await page.screenshot({ path: `${screenshotDir}/people-${width}.png`, fullPage: true });
    }
    const table = page.getByRole("table", { name: "People" });
    expect((await table.getByRole("columnheader").first().locator("xpath=../..").boundingBox())!.width).toBeLessThanOrEqual(1); // header row is for assistive technology only
    const card = table.getByRole("row", { name: /Avery Owner/ });
    await expect(card).toContainText("dev-owner");
    await expect(card).toContainText("Owner");
    expect(await noHorizontalOverflow(page)).toBe(true);
    const bleed = await table.evaluate((el) => el.scrollWidth <= el.clientWidth + 1);
    expect(bleed).toBe(true);
    await noSplitWords(page);
    const cardHeights = await table.getByRole("row").evaluateAll((rows) => rows.slice(1).map((r) => (r as HTMLElement).offsetHeight));
    expect(Math.max(...cardHeights), `card heights at ${width}px: ${cardHeights}`).toBeLessThanOrEqual(width === 320 ? 300 : 170);
    await expect(table.getByRole("button", { name: /^Actions for Dana Designer/ })).toBeVisible();
    await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
      .analyze()
      .then((r) =>
        expect(
          r.violations.map((v) => v.id),
          `axe at ${width}px`,
        ).toEqual([]),
      );
  }
  await signOut(page);
});

test("24px of space separates the approvals card from the People card at phone, tablet and desktop widths", async ({ page }) => {
  await signIn(page, "owner");
  for (const width of [320, 768, 1440]) {
    await openPeople(page, width);
    const card = (heading: RegExp) => page.getByRole("heading", { level: 2, name: heading }).locator("xpath=ancestor::*[contains(@class,'rounded-panel')][1]");
    const approvals = (await card(/Owner proposals and membership requests/).boundingBox())!;
    const people = (await card(/^People \(\d+\)$/).boundingBox())!;
    expect(Math.round(people.y - (approvals.y + approvals.height)), `gap at ${width}px`).toBe(24);
    expect(await noHorizontalOverflow(page)).toBe(true);
  }
  await signOut(page);
});
