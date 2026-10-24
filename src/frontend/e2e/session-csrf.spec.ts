import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * G22 (C-58) browser regression: a real BFF session (UI sign-in, cookie + per-session CSRF claim). Unsafe requests that
 * carry the session cookie need that session's X-CSRF-Token; the application itself (coordinator) sends it, so normal UI
 * mutations keep working — the rest of this suite (records, lifecycle, sharing, documents, designer) exercises that path.
 */
async function sessionToken(page: Page): Promise<string> {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { authenticated: boolean; csrfToken: string };
  expect(session.authenticated).toBe(true);
  return session.csrfToken;
}

const preference = (scale: number) => ({ textScalePreferenceMode: "Explicit", explicitTextScalePercent: scale });

test("cookie-session mutations need the session's CSRF token; rejected ones change nothing; the UI keeps working", async ({ page }) => {
  await signIn(page, "owner");
  const token = await sessionToken(page);
  const before = ((await (await page.request.get("/api/v1/me/ui-preferences")).json()) as { preferenceRevision: number }).preferenceRevision;

  for (const headers of [{}, { "X-CSRF-Token": "not-the-token" }, { "X-CSRF-Token": token.slice(0, -2) }, { "X-CSRF-Token": "", Origin: "https://attacker.example", "Sec-Fetch-Site": "cross-site" }]) {
    const rejected = await page.request.put("/api/v1/me/ui-preferences", { data: preference(125), headers });
    expect(rejected.status()).toBe(403);
    const text = await rejected.text();
    expect(JSON.parse(text)).toMatchObject({ title: "CSRF_TOKEN_INVALID", status: 403 });
    expect(text).not.toContain(token);
  }
  expect(((await (await page.request.get("/api/v1/me/ui-preferences")).json()) as { preferenceRevision: number }).preferenceRevision).toBe(before);
  expect((await page.request.post("/api/v1/session/logout")).status()).toBe(403);
  await sessionToken(page); // still signed in

  const accepted = await page.request.put("/api/v1/me/ui-preferences", { data: { textScalePreferenceMode: "OrganizationDefault", explicitTextScalePercent: null }, headers: { "X-CSRF-Token": token } });
  expect(accepted.status()).toBe(200);

  // The application's own requests carry the token: navigating and a UI sign-out work.
  await page.goto("/app/records");
  await expect(page.getByRole("heading", { level: 1, name: "My Records" })).toBeVisible();
  await signOut(page);

  // After signing in again, the earlier session's token is useless.
  await signIn(page, "owner");
  const fresh = await sessionToken(page);
  expect(fresh).not.toBe(token);
  expect((await page.request.put("/api/v1/me/ui-preferences", { data: preference(125), headers: { "X-CSRF-Token": token } })).status()).toBe(403);
});
