import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Recovered-owner restriction on the People directory (C-106): a compact "Recovery restrictions" disclosure beside the Owner role,
 * the Authority's expiry timestamp, a plain explanation of what is temporarily restricted, no extra table height while closed, and
 * nothing at all once the server stops returning the restriction. The real response of the isolated stack is patched in the browser
 * so the layout runs for real at three widths; the real-stack API contract (the Authority's own expiry in the People response) is
 * covered by OwnerRecoveryStackTests. Read-only; isolated security-test stack only.
 */
const UNTIL = new Date(Date.now() + 12 * 24 * 3600 * 1000).toISOString();

async function patchOwnerRestriction(page: Page, until: string | null) {
  await page.route("**/api/v1/organization/members*", async (route) => {
    const response = await route.fetch();
    const body = (await response.json()) as { members?: Array<Record<string, unknown>> };
    if (!response.ok() || !Array.isArray(body.members)) {
      await route.fulfill({ response, json: body }); // not the directory (or an error): pass through unchanged
      return;
    }

    for (const member of body.members) {
      if ((member["authorityRoles"] as string[]).includes("OrganizationOwner")) {
        member["recoveryRestrictionsUntil"] = until;
      }
    }
    await route.fulfill({ response, json: body });
  });
}

async function openPeople(page: Page, width: number) {
  await page.setViewportSize({ width, height: 900 });
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
  await expect(page.getByRole("table", { name: "People" }).getByRole("row").nth(1)).toBeVisible();
}

for (const width of [320, 768, 1440]) {
  test(`the restriction disclosure is compact, explains itself and passes axe at ${width}px`, async ({ page }) => {
    await patchOwnerRestriction(page, UNTIL);
    await signIn(page, "owner");
    await openPeople(page, width);
    const table = page.getByRole("table", { name: "People" });
    const owner = table.getByRole("row", { name: /Avery Owner/ });
    const toggle = owner.getByRole("button", { name: "Recovery restrictions" });
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    const closedHeight = await owner.evaluate((row) => (row as HTMLElement).offsetHeight);
    await expect(owner.getByText(/Recovery restrictions end/)).toBeHidden(); // closed: no extra height, nothing read out
    await toggle.click();
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    const details = owner.getByText(/Recovery restrictions end/);
    await expect(details).toBeVisible();
    await expect(owner.getByText(/about 1[12] days/)).toBeVisible(); // remaining time from the Authority timestamp
    await expect(owner).toContainText("Designer role");
    await expect(owner).toContainText("second owner");
    await expect(owner).toContainText("never allowed");
    await expect(owner).toContainText("membership status");
    expect(await owner.evaluate((row) => (row as HTMLElement).offsetHeight)).toBeGreaterThan(closedHeight);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(axe.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
    // The keyboard can close it again.
    await toggle.focus();
    await page.keyboard.press("Enter");
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    await signOut(page);
  });
}

test("no indicator appears when the server returns no restriction (expired or never recovered)", async ({ page }) => {
  await patchOwnerRestriction(page, null);
  await signIn(page, "owner");
  await openPeople(page, 1440);
  await expect(page.getByRole("button", { name: "Recovery restrictions" })).toHaveCount(0);
  await signOut(page);
});

test("a restriction whose time has already passed is not shown even if a stale response carries it", async ({ page }) => {
  await patchOwnerRestriction(page, new Date(Date.now() - 60_000).toISOString());
  await signIn(page, "owner");
  await openPeople(page, 1440);
  await expect(page.getByRole("button", { name: "Recovery restrictions" })).toHaveCount(0);
  await signOut(page);
});
