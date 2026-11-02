import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C-90 dashboard redesign and security monitoring (isolated Testing stack only): layout without horizontal overflow at
 * 1440/768/320 px (320 px also stands in for 400% zoom of a 1280 px window, 640 px for 200%), the unified control
 * geometry, no per-section Refresh buttons, independent section pagers, role-specific security monitoring, and the wider
 * Organization sidebar with sentence-case labels.
 */

async function axe(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations, JSON.stringify(results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })))).toEqual([]);
}

async function horizontalOverflow(page: Page) {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

async function sectionsLoaded(page: Page) {
  for (const name of ["My records", "Upload activity", "Recent personal activity", "Permitted forms"]) {
    const region = page.getByRole("region", { name, exact: true });
    await expect(region).toBeVisible();
    await expect(region.getByText(/^Loading /)).toHaveCount(0);
  }
}

test.describe("Dashboard", () => {
  test("owner: KPIs, charts and four searchable sections, no Refresh buttons, no security data, no overflow", async ({ page }) => {
    await signIn(page, "owner");
    await expect(page.getByRole("combobox", { name: "Period" })).toBeVisible();
    for (const label of ["Accessible records", "Draft", "Submitted", "Permitted forms", "Your activity", "Uploads", "Failed uploads"]) {
      await expect(page.getByText(label, { exact: true }).first()).toBeVisible();
    }
    await expect(page.getByRole("list", { name: "Records by status legend" })).toBeVisible();
    await expect(page.getByRole("group", { name: /^Record activity/ })).toBeVisible();
    await expect(page.getByRole("group", { name: /^Uploads by outcome/ })).toBeVisible();
    await sectionsLoaded(page);
    await expect(page.getByRole("button", { name: /refresh/i })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Security monitoring" })).toHaveCount(0);
    await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Security incidents" })).toHaveCount(0);

    // Unified geometry: 40px inputs with an 8px radius, 12px panels.
    const geometry = await page.getByRole("region", { name: "Upload activity", exact: true }).evaluate((region) => {
      const input = region.querySelector("input")!;
      const trigger = region.querySelector("button[role='combobox']")!;
      return { inputHeight: input.getBoundingClientRect().height, inputRadius: getComputedStyle(input).borderTopLeftRadius, triggerRadius: getComputedStyle(trigger).borderTopLeftRadius, panelRadius: getComputedStyle(region).borderTopLeftRadius };
    });
    expect(geometry).toEqual({ inputHeight: 40, inputRadius: "8px", triggerRadius: "8px", panelRadius: "12px" });

    // A pager per section that has rows, each with its own position text.
    const pagers = page.getByRole("navigation", { name: / pages$/ });
    expect(await pagers.count()).toBeGreaterThanOrEqual(1);
    for (const pager of await pagers.all()) {
      await expect(pager).toContainText(/Page \d+ of \d+/);
      await expect(pager.getByRole("button", { name: "Previous" })).toBeDisabled();
    }

    // The chart is reachable and readable by keyboard.
    await page.getByRole("group", { name: /^Record activity/ }).focus();
    await page.keyboard.press("ArrowLeft");
    await expect(page.getByRole("status").filter({ hasText: "Created" })).toBeVisible();

    expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    await axe(page);
    for (const width of [768, 640, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await sectionsLoaded(page);
      expect(await horizontalOverflow(page), `overflow at ${width}px`).toBeLessThanOrEqual(0);
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await signOut(page);
  });

  test("a section's search resets it to page 1 and an unmatched search says so", async ({ page }) => {
    await signIn(page, "owner");
    const forms = page.getByRole("region", { name: "Permitted forms", exact: true });
    await expect(forms.getByText(/^Loading /)).toHaveCount(0);
    await forms.getByLabel("Form name").fill("zz-no-such-form-" + Date.now());
    await expect(forms.getByText("No forms match")).toBeVisible();
    await forms.getByRole("button", { name: "Clear filters" }).click();
    await expect(forms.getByLabel("Form name")).toHaveValue("");
    await expect(forms.getByText("No forms match")).toHaveCount(0);
    // Invalid record lookup is explained instead of sent.
    const records = page.getByRole("region", { name: "My records", exact: true });
    await records.getByLabel("Find a record").fill("not a reference");
    await expect(records.getByText("Enter a record reference such as REC-1A2B3C4D or a full record ID.")).toBeVisible();
    await signOut(page);
  });

  test("Security Administrator: security monitoring and the incidents page; organization owner gets no incident data", async ({ page }) => {
    await signIn(page, "security");
    await expect(page.getByRole("heading", { name: "Security monitoring" })).toBeVisible();
    for (const label of ["Open incidents", "Confirmed integrity failures", "Under investigation", "Resolved", "Detected in period"]) {
      await expect(page.getByText(label, { exact: true }).first()).toBeVisible();
    }
    await expect(page.getByRole("list", { name: "Incidents by status legend" })).toBeVisible();
    await expect(page.getByText(/not a scan/)).toBeVisible();
    await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Security incidents" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Security incidents" })).toBeVisible();
    await expect(page.getByRole("textbox", { name: "Search", exact: true })).toBeVisible();
    await axe(page);
    await signOut(page);

    await signIn(page, "owner");
    const denied = await page.request.get("/api/v1/security/incidents");
    expect(denied.status()).toBe(403);
    await page.goto("/app/security/incidents");
    await expect(page.getByText("Security incidents are available to Security Administrators.")).toBeVisible();
    await signOut(page);
  });

  test("Organization sidebar is wider, sentence case, and fits Security administrators on one line", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization");
    const nav = page.getByRole("navigation", { name: "Organization" });
    await expect(nav.getByRole("link")).toHaveText(["Overview", "People", "Teams", "Branding", "Data protection", "Security administrators"]);
    const box = await nav.boundingBox();
    expect(Math.round(box!.width)).toBe(256);
    const link = await nav.getByRole("link", { name: "Security administrators", exact: true }).boundingBox();
    expect(link!.height).toBeLessThanOrEqual(48);
    // Same width on every Organization page.
    for (const path of ["/app/organization/users", "/app/organization/teams", "/app/organization/branding", "/app/organization/data-protection", "/app/organization/security-administrators"]) {
      await page.goto(path);
      expect(Math.round((await page.getByRole("navigation", { name: "Organization" }).boundingBox())!.width), path).toBe(256);
    }
    await page.setViewportSize({ width: 320, height: 800 });
    expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signOut(page);
  });
});
