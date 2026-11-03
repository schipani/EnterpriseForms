import { expect, test } from "@playwright/test";

/** Organization logo upload (ADR-PB-002 Stage B item 6): the owner uploads a PNG; the header shows it. Regression for the HTTP 415 seen when the client sent application/octet-stream. */
const onePixelPng = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==", "base64");

test("owner uploads a PNG logo and it appears top-left", async ({ page }) => {
  await page.goto("/login");
  await page.getByRole("button", { name: /\(owner\)$/ }).click();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();
  await page.goto("/app/organization/branding");
  await expect(page.getByRole("heading", { level: 1, name: "Organization branding" })).toBeVisible();
  await page.getByLabel("Choose a PNG logo").setInputFiles({ name: "logo.png", mimeType: "image/png", buffer: onePixelPng });
  await expect(page.getByText("A logo is set (shown top-left).")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("alert")).toHaveCount(0);
  const logo = page.getByRole("banner").getByRole("img");
  await expect(logo).toBeVisible();
  // The owner's configured alternative text (or "<organization> logo" when none is set) — never empty.
  expect((await logo.getAttribute("alt"))?.trim().length ?? 0).toBeGreaterThan(0);
  // A non-PNG file is refused by the server and reported, never applied.
  await page.getByLabel("Choose a PNG logo").setInputFiles({ name: "evil.png", mimeType: "image/png", buffer: Buffer.from("<svg onload=alert(1)></svg>") });
  await expect(page.getByText(/logo must be a PNG/)).toBeVisible();
  await expect(page.getByText("Code INVALID_REQUEST", { exact: false })).toBeVisible();
});
