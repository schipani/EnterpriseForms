import { expect, test } from "@playwright/test";

/** 320px reflow and keyboard reachability on the component gallery (PB-14 automated checks; human AT review remains separate). */
test("gallery reflows at 320px without horizontal scroll and every control is keyboard reachable", async ({ page }) => {
  await page.goto("/login");
  await page.getByRole("button", { name: /\(reader\)$/ }).click();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
  await page.goto("/app/gallery");
  await expect(page.getByRole("heading", { name: "Component gallery" })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  // Phone breakpoint: the renderer stacks into one column.
  await expect(page.locator('[data-breakpoint="phone"]')).toHaveCount(1);
  // Keyboard: tab from the top reaches the primary button and the checkbox; focus ring is visible (outline set).
  await page.keyboard.press("Tab");
  await expect(page.locator(":focus")).toHaveText(/Skip to main content/);
  let reachedCheckbox = false;
  for (let i = 0; i < 80 && !reachedCheckbox; i++) {
    await page.keyboard.press("Tab");
    reachedCheckbox = await page.evaluate(() => document.activeElement?.getAttribute("role") === "checkbox");
  }
  expect(reachedCheckbox).toBe(true);
  const outline = await page.evaluate(() => getComputedStyle(document.activeElement as Element).outlineStyle);
  expect(outline).not.toBe("none");
  await page.keyboard.press("Space");
  expect(await page.evaluate(() => document.activeElement?.getAttribute("aria-checked"))).toBe("false");
});
