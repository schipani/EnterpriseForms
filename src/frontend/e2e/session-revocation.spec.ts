import { expect, test } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * G23 (C-63) browser flows on the real stack: the session cookie is only a carrier — the Authority decides on every
 * request whether the session is still valid. Signing out in one browser ends the session for a copy of its cookie in
 * another browser; signing in again works; a sign-out the server cannot confirm leaves the user visibly signed in.
 */
test("signing out ends the session everywhere, including a copied cookie in another browser", async ({ page, browser }) => {
  await signIn(page, "owner");
  const cookies = await page.context().cookies();
  expect(cookies.some((cookie) => cookie.name.startsWith("ef-session"))).toBe(true);

  // A second browser holding a copy of the cookie is signed in as the same user.
  const copied = await browser.newContext();
  await copied.addCookies(cookies);
  const other = await copied.newPage();
  await other.goto("/app/dashboard");
  await expect(other.getByRole("heading", { name: /Welcome/ })).toBeVisible();
  const token = ((await (await other.request.get("/api/v1/session")).json()) as { csrfToken: string }).csrfToken;

  await signOut(page);

  // The copy no longer authenticates: the app returns to sign-in, and a direct mutation with the old token is refused.
  await other.goto("/app/records");
  await expect(other.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
  const replay = await copied.request.put("/api/v1/me/ui-preferences", {
    data: { textScalePreferenceMode: "Explicit", explicitTextScalePercent: 125 },
    headers: { "X-CSRF-Token": token, Cookie: cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ") },
  });
  expect(replay.status()).toBe(401);
  await copied.close();

  // Back in the first browser: protected pages stay closed after sign-out, and a fresh sign-in works.
  await page.goto("/app/records");
  await expect(page.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
  await signIn(page, "owner");
  await page.goto("/app/records");
  await expect(page.getByRole("heading", { level: 1, name: "My Records" })).toBeVisible();
});

test("a sign-out the server cannot confirm keeps the user signed in and says so", async ({ page }) => {
  await signIn(page, "owner");
  await page.context().route("**/api/v1/session/logout", (route) =>
    route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ title: "SESSION_STATE_UNAVAILABLE", status: 503, detail: "Sign-out could not be confirmed. Try again." }) }),
  );
  await page.getByRole("button", { name: /Account menu/ }).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  const alert = page.getByRole("alert").filter({ hasText: "You are still signed in" });
  await expect(alert).toBeVisible();
  await expect(page.getByRole("heading", { name: /Welcome/ })).toBeVisible();

  await page.context().unroute("**/api/v1/session/logout");
  await alert.getByRole("button", { name: "Try signing out again" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Sign in", exact: true })).toBeVisible();
  expect(((await (await page.request.get("/api/v1/session")).json()) as { authenticated: boolean }).authenticated).toBe(false);
});
