import { expect, test, type Locator, type Page } from "@playwright/test";
import { signIn } from "./support";

/**
 * Consistent action-button icons (Phase C6): labeled actions carry a decorative icon on the left of the visible text, at the
 * size of the button, without changing the accessible name, the default black primary style or keyboard focus.
 * Geometry assertions are used instead of pixel baselines so the check holds across platforms and organization themes.
 */
async function iconFacts(button: Locator) {
  return button.evaluate((element) => {
    const svg = element.querySelector("svg");
    const text = Array.from(element.childNodes).find((node) => node.nodeType === Node.TEXT_NODE && (node.textContent ?? "").trim() !== "");
    if (!svg || !text) {
      return null;
    }
    const range = document.createRange();
    range.selectNodeContents(text);
    const icon = svg.getBoundingClientRect();
    const label = range.getBoundingClientRect();
    const style = getComputedStyle(element);
    return { ariaHidden: svg.getAttribute("aria-hidden"), width: Math.round(icon.width), height: Math.round(icon.height), iconRight: icon.right, labelLeft: label.left, gap: Math.round(label.left - icon.right), background: style.backgroundColor, color: style.color };
  });
}

async function focusRing(page: Page, button: Locator) {
  await button.focus();
  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("Tab");
  return button.evaluate((element) => {
    const style = getComputedStyle(element);
    return element === document.activeElement && (style.outlineStyle !== "none" || style.boxShadow !== "none");
  });
}

test.describe("Action button icons", () => {
  test("New record and Search have a left, decorative, consistently sized icon and keep their names, style and focus ring", async ({ page }, info) => {
    await signIn(page, "owner");
    await page.goto("/app/records");

    // Names are the visible text only (the icon is decorative).
    const newRecord = page.getByRole("button", { name: "New record", exact: true });
    const search = page.getByRole("search", { name: "Search records by reference or ID" }).getByRole("button", { name: "Search", exact: true });
    await expect(newRecord).toBeVisible();
    await expect(search).toBeVisible();

    const facts = { newRecord: await iconFacts(newRecord), search: await iconFacts(search) };
    for (const [name, fact] of Object.entries(facts)) {
      expect(fact, `${name} has an icon and a text label`).not.toBeNull();
      expect(fact!.ariaHidden, `${name} icon is decorative`).toBe("true");
      expect(fact!.iconRight, `${name} icon is left of the label`).toBeLessThanOrEqual(fact!.labelLeft);
      expect(fact!.gap, `${name} icon-to-text gap`).toBeGreaterThanOrEqual(4);
      expect(fact!.gap).toBeLessThanOrEqual(12);
    }

    // Same dimensions for both medium primary buttons; the default primary stays black on white (ADR-PB-014).
    expect({ w: facts.newRecord!.width, h: facts.newRecord!.height }).toEqual({ w: facts.search!.width, h: facts.search!.height });
    expect(facts.newRecord!.width).toBeGreaterThanOrEqual(16);
    expect(facts.newRecord!.width).toBeLessThanOrEqual(20);
    expect(facts.search!.background).toBe(facts.newRecord!.background);
    expect(facts.search!.color).toBe(facts.newRecord!.color);

    expect(await focusRing(page, search)).toBe(true);

    // The row of header actions keeps its layout at phone width.
    await page.setViewportSize({ width: 320, height: 720 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await expect(newRecord).toBeVisible();
    await info.attach("my-records-actions-320", { body: await page.screenshot(), contentType: "image/png" });
  });

  test("Simple Cancel buttons and the lifecycle actions follow the convention", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    // Clear filters (shown once a filter is active) carries the reset icon.
    await page.getByRole("checkbox", { name: "Include related (child) records" }).check();
    const clear = page.getByRole("button", { name: "Clear filters" }).first();
    await expect(clear).toBeVisible();
    expect(await iconFacts(clear)).not.toBeNull();

    // Open any record that offers lifecycle actions; each header action has an icon, Cancel in a dialog has none.
    await page.getByRole("link", { name: /^Open / }).first().click();
    const submitOrArchive = page.getByRole("button", { name: /^(Submit|Archive|Reopen as draft|Unarchive)$/ }).first();
    await expect(submitOrArchive).toBeVisible();
    expect(await iconFacts(submitOrArchive)).not.toBeNull();
    await submitOrArchive.click();
    const cancel = page.getByRole("dialog").getByRole("button", { name: "Cancel" });
    await expect(cancel).toBeVisible();
    expect(await cancel.locator("svg").count()).toBe(0);
  });
});
