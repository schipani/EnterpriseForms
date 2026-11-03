import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Navigation, User area and the pinned header (PR #49): the black active underline, one active item per level, the User sidebar,
 * the account menu, the legacy redirects, and the header that compacts after about 72px of scrolling. Evidence screenshots are
 * written to artifacts/ui-evidence (not asserted against).
 */

const primary = (page: Page) => page.getByRole("navigation", { name: "Primary" });
const evidence = (page: Page, name: string) => page.screenshot({ path: `../../artifacts/ui-evidence/${name}.png`, fullPage: false });

async function underline(page: Page, link: ReturnType<Page["locator"]>) {
  return link.locator("span").first().evaluate((el) => {
    const c = getComputedStyle(el);
    return { width: c.borderBottomWidth, style: c.borderBottomStyle, color: c.borderBottomColor };
  });
}

async function scrollable(page: Page) {
  // Wait for the page to finish loading (the header grows when the primary links arrive; the router restores the scroll position
  // after a navigation), then make it taller than the viewport so scrolling is possible on every page of the test.
  await expect(primary(page)).toBeVisible();
  await page.waitForLoadState("networkidle");
  await page.addStyleTag({ content: "main { min-height: 2400px; }" });
}

/** Scrolls down and waits for the compact bar, retrying if the router restores the scroll position while the page is still settling. */
async function pinHeader(page: Page, y = 500) {
  const compact = page.getByTestId("compact-header");
  await expect(async () => {
    await page.evaluate((top) => window.scrollTo(0, top), y);
    await expect(compact).toHaveAttribute("data-active", "true", { timeout: 1000 });
  }).toPass({ timeout: 15_000 });
  return compact;
}

test.describe("active navigation indicator", () => {
  test("the primary navigation underlines the selected section in the black primary token, one item at a time, and Organization stays selected on its sub pages", async ({ page }) => {
    await signIn(page, "security");
    for (const [path, label] of [
      ["/app/dashboard", "Dashboard"],
      ["/app/organization", "Organization"],
      ["/app/organization/users", "Organization"],
      ["/app/organization/search-approvals", "Organization"],
      ["/app/security/incidents", "Security incidents"],
    ] as const) {
      await page.goto(path);
      const nav = primary(page);
      await expect(nav.locator('a[aria-current="page"]'), path).toHaveCount(1);
      const active = nav.getByRole("link", { name: label, exact: true });
      await expect(active, path).toHaveAttribute("aria-current", "page");
      const line = await underline(page, active);
      expect(line.style).toBe("solid");
      expect(line.width).toBe("4px");
      expect(line.color).toBe("rgb(17, 17, 17)"); // the default primary token: black
      // Every other link has no visible underline.
      for (const other of await nav.getByRole("link").all()) {
        if ((await other.getAttribute("aria-current")) !== "page") {
          expect((await underline(page, other)).color).toBe("rgba(0, 0, 0, 0)");
        }
      }
    }
    await evidence(page, "active-underline-primary");
  });

  test("Search Approvals highlights Organization in the primary navigation and Search Approvals, underlined, in the Organization sidebar", async ({ page }) => {
    await signIn(page, "security");
    await page.goto("/app/organization/search-approvals");
    const side = page.getByRole("navigation", { name: "Organization" });
    await expect(side.locator('a[aria-current="page"]')).toHaveCount(1);
    const item = side.getByRole("link", { name: "Search Approvals", exact: true });
    await expect(item).toHaveAttribute("aria-current", "page");
    expect((await underline(page, item)).color).toBe("rgb(17, 17, 17)");
    await expect(primary(page).locator('a[aria-current="page"]')).toHaveCount(1);
    await expect(primary(page).getByRole("link", { name: "Organization", exact: true })).toHaveAttribute("aria-current", "page");
    await expect(primary(page).getByRole("link", { name: /search approvals/i })).toHaveCount(0);
    await evidence(page, "organization-sidebar-search-approvals");
  });

  test("an owner without security.administer has no Search Approvals link and the page refuses them", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization/regional");
    await expect(page.getByRole("navigation", { name: "Organization" }).getByRole("link", { name: "Search Approvals" })).toHaveCount(0);
    await page.goto("/app/organization/search-approvals");
    await expect(page.getByText("Security administrators only")).toBeVisible();
    await expect(page.getByRole("table")).toHaveCount(0);
  });

  test("keyboard focus is visibly different from the active indicator", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/dashboard");
    const forms = primary(page).getByRole("link", { name: "Forms", exact: true });
    await forms.focus();
    const focusStyle = await forms.evaluate((el) => { const c = getComputedStyle(el); return `${c.outlineStyle}|${c.outlineWidth}|${c.boxShadow}`; });
    expect(focusStyle).not.toBe("none|0px|none");
    expect((await underline(page, forms)).color).toBe("rgba(0, 0, 0, 0)"); // focused, but not the current page
  });
});

test.describe("User area", () => {
  test("the User sidebar has My Profile and Experience & Preferences, with the active item underlined", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/user");
    await expect(page.getByRole("heading", { level: 1, name: "My profile" })).toBeVisible();
    const side = page.getByRole("navigation", { name: "User" });
    await expect(side.getByRole("heading", { level: 2 })).toHaveText(["User", "Experience & Preferences"]);
    await expect(side.getByRole("link")).toHaveText(["My Profile", "Regional Settings", "Language", "Text Size"]);
    await expect(side.locator('a[aria-current="page"]')).toHaveCount(1);
    expect((await underline(page, side.getByRole("link", { name: "My Profile", exact: true }))).color).toBe("rgb(17, 17, 17)");
    // The User area is not a primary-navigation item: nothing in the primary navigation is current.
    await expect(primary(page).locator('a[aria-current="page"]')).toHaveCount(0);
    const icons = await side.locator("svg").evaluateAll((els) => els.map((e) => [e.getAttribute("width"), e.getAttribute("aria-hidden")]));
    expect(icons).toEqual([["18", "true"], ["16", "true"], ["18", "true"], ["16", "true"], ["16", "true"], ["16", "true"]]);
    for (const [name, path, heading] of [
      ["Regional Settings", /\/app\/user\/regional$/, "Regional settings"],
      ["Language", /\/app\/user\/language$/, "Language"],
      ["Text Size", /\/app\/user\/text-size$/, "Text size"],
      ["My Profile", /\/app\/user$/, "My profile"],
    ] as const) {
      await side.getByRole("link", { name, exact: true }).click();
      await expect(page).toHaveURL(path);
      await expect(page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
      await expect(side.locator('a[aria-current="page"]')).toHaveCount(1);
      await expect(side.getByRole("link", { name, exact: true })).toHaveAttribute("aria-current", "page");
    }
    await page.goto("/app/user/regional");
    await expect(page.getByRole("heading", { level: 1, name: "Regional settings" })).toBeVisible();
    await expect(page.getByTestId("preview-zone")).toBeVisible();
    await evidence(page, "user-sidebar");
  });

  test("the sidebar is as wide as the Organization sidebar", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/user");
    const user = Math.round((await page.getByRole("navigation", { name: "User" }).boundingBox())!.width);
    await page.goto("/app/organization");
    const organization = Math.round((await page.getByRole("navigation", { name: "Organization" }).boundingBox())!.width);
    expect(user).toBe(organization);
    expect(user).toBe(288);
  });

  test("the account menu goes to the User area; Sign out signs out", async ({ page }) => {
    await signIn(page, "clerk");
    await page.getByRole("button", { name: /Account menu/ }).click();
    await page.getByRole("menuitem", { name: "My Profile" }).click();
    await expect(page).toHaveURL(/\/app\/user$/);
    await page.getByRole("button", { name: /Account menu/ }).click();
    await page.getByRole("menuitem", { name: "Preferences" }).click();
    await expect(page).toHaveURL(/\/app\/user\/regional$/);
    await signOut(page);
  });

  test("earlier addresses redirect to the canonical routes and keep the query string", async ({ page }) => {
    await signIn(page, "clerk");
    for (const [from, to] of [
      ["/app/me/profile", "/app/user"],
      ["/app/me/preferences", "/app/user/regional"],
      ["/app/settings/preferences", "/app/user/regional"],
      ["/app/settings/preferences/language", "/app/user/language"],
      ["/app/settings/preferences/text-size", "/app/user/text-size"],
    ] as const) {
      await page.goto(from);
      await expect(page, from).toHaveURL(new RegExp(`${to.replace(/\//g, "\\/")}$`));
    }
    await page.goto("/app/me/preferences?from=menu");
    await expect(page).toHaveURL(/\/app\/user\/regional\?from=menu$/);
  });

  test("profile and preferences still work from their new place", async ({ page }) => {
    await signIn(page, "reader");
    await page.goto("/app/user/regional");
    const group = page.getByRole("group", { name: "Time zone" });
    await group.getByRole("radio", { name: /^Use browser settings/ }).check();
    await expect(page.getByTestId("preview-zone")).toBeVisible();
    // Unsaved edits survive moving around inside the page (the preview reacts at once); saving still works.
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();
    await page.getByRole("button", { name: "Reset to organization defaults" }).click();
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();
    await page.getByRole("navigation", { name: "User" }).getByRole("link", { name: "My Profile" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "My profile" })).toBeVisible();
    await expect(page.getByRole("textbox").first()).toBeVisible();
  });
});

test.describe("pinned, collapsing header", () => {
  test("the full header at the top becomes a compact pinned bar after about 72px, without moving the content, and returns at the top", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization");
    await scrollable(page);
    const compact = page.getByTestId("compact-header");
    await expect(compact).toHaveAttribute("data-active", "false");
    await expect(page.locator("header").first().getByRole("link").first()).toBeVisible(); // logo or initials and the organization name
    await evidence(page, "header-full");
    const mainTop = () => page.locator("main").evaluate((el) => Math.round(el.getBoundingClientRect().top + window.scrollY));
    const before = await mainTop();

    await page.evaluate(() => window.scrollTo(0, 60));
    await expect(compact).toHaveAttribute("data-active", "false");
    await pinHeader(page, 400);
    expect(await mainTop()).toBe(before); // the page did not jump when the header changed
    await expect.poll(async () => (await compact.boundingBox())!.y).toBe(0); // pinned to the top once its slide-in has finished
    expect((await compact.boundingBox())!.height).toBe(56);
    await expect(compact.getByRole("link", { name: "Organization", exact: true })).toHaveAttribute("aria-current", "page");
    await expect(compact.getByRole("button", { name: "Sign out" })).toBeVisible();
    await expect(compact.getByRole("button", { name: /Account menu/ })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Primary" })).toHaveCount(1); // only the compact bar's controls are exposed
    await expect(page.locator("header").first()).toHaveAttribute("aria-hidden", "true");
    await evidence(page, "header-compact");

    await page.evaluate(() => window.scrollTo(0, 0));
    await expect(compact).toHaveAttribute("data-active", "false");
    await expect(page.getByRole("navigation", { name: "Primary" })).toHaveCount(1);
    await expect(page.getByRole("button", { name: "Sign out" })).toHaveCount(0); // Sign out is in the account menu again
    await signOut(page);
  });

  test("a focused control scrolled into view is not hidden behind the compact bar, and menus open above it", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/user/regional");
    await scrollable(page);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).scrollPaddingTop)).toBe("72px");
    const compact = await pinHeader(page);
    await page.getByRole("button", { name: "Save preferences" }).focus();
    await page.getByRole("link", { name: "My Profile", exact: true }).focus(); // sidebar link near the top of the content
    const y = (await page.getByRole("link", { name: "My Profile", exact: true }).boundingBox())!.y;
    expect(y).toBeGreaterThanOrEqual(56);
    // The account menu opens over the page, below the bar's own controls, and is usable.
    await compact.getByRole("button", { name: /Account menu/ }).click();
    const item = page.getByRole("menuitem", { name: "Sign out" });
    await expect(item).toBeVisible();
    await page.keyboard.press("Escape");
  });

  for (const [width, height, label] of [
    [320, 700, "320"],
    [768, 900, "768"],
    [1440, 900, "1440"],
  ] as const) {
    test(`responsive ${label}px: no sideways scroll, 44px targets, Sign out and account access available, axe clean`, async ({ page }) => {
      await signIn(page, "owner");
      await page.setViewportSize({ width, height });
      await page.goto("/app/organization");
      await scrollable(page);
      const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(await overflow()).toBeLessThanOrEqual(0);
      await evidence(page, `header-full-${label}`);
      const compact = await pinHeader(page);
      expect(await overflow()).toBeLessThanOrEqual(0);
      await expect(compact.getByRole("button", { name: "Sign out" })).toBeVisible();
      for (const control of await compact.getByRole("button").all()) {
        if (await control.isVisible()) {
          expect((await control.boundingBox())!.height, await control.innerText()).toBeGreaterThanOrEqual(43.9); // 44px, allowing sub-pixel rounding
        }
      }
      if (width < 1280) {
        // Primary links collapse into one accessible Menu button; account actions join it on a phone.
        await expect(compact.getByRole("navigation", { name: "Primary" })).toBeHidden();
        await compact.getByRole("button", { name: "Main menu" }).click();
        await expect(page.getByRole("menuitem", { name: "Organization", exact: true })).toBeVisible();
        if (width < 768) {
          await expect(page.getByRole("menuitem", { name: "My Profile" })).toBeVisible();
          await expect(page.getByRole("menuitem", { name: "Preferences" })).toBeVisible();
        }
        for (const item of await page.getByRole("menuitem").all()) {
          expect((await item.boundingBox())!.height).toBeGreaterThanOrEqual(43.9);
        }
        await evidence(page, `header-menu-${label}`);
        await page.getByRole("menuitem", { name: "Forms", exact: true }).click(); // choosing a destination closes the menu
        await expect(page).toHaveURL(/\/app\/forms$/);
        await expect(page.getByRole("menu")).toHaveCount(0);
      } else {
        await expect(compact.getByRole("navigation", { name: "Primary" })).toBeVisible();
        const row = await compact.getByRole("navigation", { name: "Primary" }).getByRole("link").evaluateAll((els) => new Set(els.map((e) => Math.round(e.getBoundingClientRect().top))).size);
        expect(row).toBe(1); // all links share one row
        await evidence(page, "header-compact-desktop");
      }
      await scrollable(page);
      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
      expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
    });
  }

  test("signing out from the compact bar on a phone works", async ({ page }) => {
    await signIn(page, "clerk");
    await page.setViewportSize({ width: 320, height: 700 });
    await page.goto("/app/user");
    await scrollable(page);
    const compact = await pinHeader(page);
    await compact.getByRole("button", { name: "Sign out" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
  });

  test("capability-based links: a member sees neither Organization nor security links in either bar", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/dashboard");
    await expect(primary(page).getByRole("link", { name: "Organization", exact: true })).toHaveCount(0);
    await expect(primary(page).getByRole("link", { name: /Security incidents/ })).toHaveCount(0);
    await scrollable(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    const compact = await pinHeader(page);
    await expect(compact.getByRole("link", { name: "Organization", exact: true })).toHaveCount(0);
    await expect(compact.getByRole("link", { name: /Security incidents/ })).toHaveCount(0);
  });

  test("the full-width Form Designer keeps its wide layout under the same header", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/forms");
    await expect(page.getByRole("navigation", { name: "Primary" })).toBeVisible();
    await expect(page.getByTestId("compact-header")).toHaveAttribute("data-active", "false");
  });
});
