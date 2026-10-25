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

/** Phase C7 M0: the My Records filters and Advanced Search stack into one column at 320px without horizontal scroll. */
test("My Records filters and Advanced Search stack at 320px without horizontal scroll", async ({ page }) => {
  await page.goto("/login");
  await page.getByRole("button", { name: /\(owner\)$/ }).click();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
  await page.goto("/app/records?form=0199a2b3-c4d5-7e6f-8123-456789abcdf9");
  const toggle = page.getByRole("button", { name: /^Advanced Search/ });
  await toggle.click();
  // Phase C7 M3: execution is on in this isolated environment; an unknown form offers no searchable field.
  await expect(page.getByText("No fields can be searched in this form")).toBeVisible();
  const form = await page.getByRole("combobox", { name: "Form" }).boundingBox();
  const status = await page.getByRole("combobox", { name: "Status" }).boundingBox();
  expect(status!.y).toBeGreaterThan(form!.y + form!.height - 1);
  const group = page.getByRole("group", { name: "Condition 1" });
  const field = await group.getByRole("combobox", { name: "Field" }).boundingBox();
  const operator = await group.getByRole("combobox", { name: "Condition" }).boundingBox();
  expect(operator!.y).toBeGreaterThan(field!.y + field!.height - 1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);

  await page.goto("/app/gallery");
  const preview = page.getByRole("region", { name: "Advanced Search (synthetic preview)" });
  for (let i = 0; i < 4; i++) await preview.getByRole("button", { name: "Add condition" }).click();
  await expect(preview.getByRole("group", { name: /^Condition \d$/ })).toHaveCount(5);
  const buttons = [await preview.getByRole("button", { name: "Add condition" }).boundingBox(), await preview.getByRole("button", { name: "Search records" }).boundingBox()];
  for (const b of buttons) expect(b!.x + b!.width).toBeLessThanOrEqual(320);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
});
