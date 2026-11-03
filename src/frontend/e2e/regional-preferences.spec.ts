import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn } from "./support";

/**
 * C8 M3 (PG-043, PG-007): organization regional defaults and personal preferences in a real browser, in two browser time
 * zones. Runs only against the isolated Testing stack (the Playwright config refuses persistent endpoints). The dev-seed
 * organization is shared, so each test restores what it changed.
 */

async function useBrowserSettings(page: Page) {
  await page.goto("/app/settings/preferences");
  await expect(page.getByRole("heading", { level: 1, name: "Preferences" })).toBeVisible();
}

test.describe("browser in America/Toronto", () => {
  test.use({ timezoneId: "America/Toronto", locale: "en-CA" });

  test("owner sets the organization defaults; a member sees them, then chooses browser and explicit settings independently", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization/regional");
    await expect(page.getByRole("heading", { level: 1, name: "Regional settings" })).toBeVisible();
    // Canada-first default, previewed with the Toronto offset on the fixed March sample.
    await expect(page.getByTestId("preview-zone")).toContainText("America/Toronto");
    await expect(page.getByTestId("preview-date-only")).toHaveText("2026-03-08");

    await page.getByRole("combobox", { name: /Default time zone/ }).click();
    await page.getByRole("option", { name: /America\/Winnipeg/ }).click();
    await page.getByRole("combobox", { name: /Default number format/ }).click();
    await page.getByRole("option", { name: /Germany/ }).click();
    await expect(page.getByTestId("preview-zone")).toContainText("America/Winnipeg");
    await expect(page.getByTestId("preview-numbers")).toContainText("1.234.567,89");
    await page.getByRole("button", { name: "Save regional defaults" }).click();
    await expect(page.getByText("Regional defaults saved")).toBeVisible();

    // The owner still has no personal preference row: they now see the organization's defaults.
    await useBrowserSettings(page);
    await expect(page.getByTestId("preview-zone")).toContainText("America/Winnipeg");
    await expect(page.getByTestId("preview-numbers")).toContainText("1.234.567,89");

    // Browser settings for the time zone only: the organization's number format stays.
    const zoneGroup = page.getByRole("group", { name: "Time zone" });
    await zoneGroup.getByRole("radio", { name: /^Use browser settings/ }).check();
    await expect(zoneGroup.getByRole("radio", { name: /^Use browser settings \(America\/Toronto\)/ })).toBeChecked();
    await expect(page.getByTestId("preview-zone")).toContainText("America/Toronto");
    await expect(page.getByTestId("preview-numbers")).toContainText("1.234.567,89");
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();

    // Restore the shared organization: personal row back to organization defaults, then the Canadian defaults.
    await page.getByRole("button", { name: "Reset to organization defaults" }).click();
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();
    await page.goto("/app/organization/regional");
    await page.getByRole("combobox", { name: /Default time zone/ }).click();
    await page.getByRole("option", { name: /America\/Toronto/ }).click();
    await page.getByRole("combobox", { name: /Default number format/ }).click();
    await page.getByRole("option", { name: /Canada, English/ }).click();
    await page.getByRole("button", { name: "Save regional defaults" }).click();
    await expect(page.getByText("Regional defaults saved")).toBeVisible();
  });

  test("the preferences page has a left navigation with the two areas and each opens on its own address", async ({ page }) => {
    await signIn(page, "clerk");
    await useBrowserSettings(page);
    const nav = page.getByRole("navigation", { name: "Preferences" });
    await expect(nav.getByRole("link", { name: "Time zone, date, number and language" })).toHaveAttribute("aria-current", "page");
    await expect(page.getByRole("group", { name: "Time zone" })).toBeVisible();
    await nav.getByRole("link", { name: "Text size" }).click();
    await expect(page).toHaveURL(/\/app\/settings\/preferences\/text-size$/);
    await expect(nav.getByRole("link", { name: "Text size" })).toHaveAttribute("aria-current", "page");
    await expect(page.getByRole("combobox", { name: "Text size preference" })).toBeVisible();
    await expect(page.getByRole("group", { name: "Time zone" })).toHaveCount(0);
    await page.reload();
    await expect(page.getByRole("combobox", { name: "Text size preference" })).toBeVisible();
  });

  test("a member without the owner role reads the regional settings but cannot change them", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto("/app/organization/regional");
    // Not listed in the clerk's navigation; the API refuses a write and the page says so.
    await expect(page.getByRole("link", { name: "Regional settings" })).toHaveCount(0);
  });

  test("the preferences page has no accessibility violations and no horizontal scroll at 320 px", async ({ page }) => {
    await signIn(page, "clerk");
    await page.setViewportSize({ width: 320, height: 720 });
    await useBrowserSettings(page);
    await expect(page.getByRole("group", { name: "Time zone" })).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
  });
});

test.describe("browser in Asia/Tokyo", () => {
  test.use({ timezoneId: "Asia/Tokyo", locale: "en-GB" });

  test("browser settings follow the browser's own zone and locale; an explicit zone overrides it", async ({ page }) => {
    await signIn(page, "reader");
    await useBrowserSettings(page);
    const zoneGroup = page.getByRole("group", { name: "Time zone" });
    await zoneGroup.getByRole("radio", { name: /^Use browser settings/ }).check();
    await expect(zoneGroup.getByRole("radio", { name: /^Use browser settings \(Asia\/Tokyo\)/ })).toBeChecked();
    await expect(page.getByTestId("preview-zone")).toContainText("Asia/Tokyo (UTC+09:00");
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();

    // Reloading keeps it (server-side, per user) and the stored instants shown elsewhere use the Tokyo clock.
    await page.reload();
    await expect(page.getByRole("group", { name: "Time zone" }).getByRole("radio", { name: /^Use browser settings/ })).toBeChecked();
    await expect(page.getByTestId("preview-zone")).toContainText("Asia/Tokyo");

    // Explicit zone wins over the browser.
    await zoneGroup.getByRole("radio", { name: "Choose my own" }).check();
    await page.getByRole("combobox", { name: "My time zone" }).click();
    await page.getByRole("option", { name: /America\/Winnipeg/ }).click();
    await expect(page.getByTestId("preview-zone")).toContainText("America/Winnipeg");
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();

    // Restore.
    await page.getByRole("button", { name: "Reset to organization defaults" }).click();
    await page.getByRole("button", { name: "Save preferences" }).click();
    await expect(page.getByText("Preferences saved and applied")).toBeVisible();
  });
});
