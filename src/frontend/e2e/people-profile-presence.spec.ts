import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * People profiles, live presence and the paged approval lists in the real application on the isolated security-test stack.
 * Profiles: an owner edits another person's profile from the Actions menu, a person edits their own from My profile, names change
 * everywhere and are restored. Presence: a person who signs in shows Available to the owner, signing out shows Offline, and the
 * indicator always has a text label. Approvals: five rows per page, Previous/Next, independent lists, filters that reset paging,
 * no overflow at 320, 768 and 1440 px. Shared organization: names are restored; membership requests cannot be withdrawn.
 */
test.describe.configure({ mode: "serial" });

const run = Date.now().toString(36);

async function axe(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations, `${label}: ${JSON.stringify(results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })))}`).toEqual([]);
}

async function openPeople(page: Page, width = 1440) {
  await page.setViewportSize({ width, height: 1000 });
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
  await expect(page.getByRole("table", { name: "People" }).getByRole("row").nth(1)).toBeVisible();
}

const person = (page: Page, name: RegExp) => page.getByRole("table", { name: "People" }).getByRole("row", { name });

const noHorizontalOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);

async function csrfToken(page: Page): Promise<string> {
  return page.evaluate(async () => ((await (await fetch("/api/v1/session")).json()) as { csrfToken: string }).csrfToken);
}

test("an owner edits another person's profile from the Actions menu; identity and roles stay read-only", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  await person(page, /Dana Designer/)
    .getByRole("button", { name: /^Actions for Dana Designer/ })
    .click();
  await page.getByRole("menuitem", { name: /^Edit profile:/ }).click();
  const dialog = page.getByRole("dialog", { name: /Edit profile: Dana Designer/ });
  await expect(dialog.getByLabel(/Display name/)).toHaveValue("Dana Designer");
  await expect(dialog.getByText(/cannot be changed in a profile/)).toBeVisible();
  await expect(dialog.getByText(/dev-designer · /)).toBeVisible(); // verified identity, read-only text (not an input)
  await expect(dialog.getByLabel(/email/i)).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Save profile" })).toBeDisabled();
  await axe(page, "edit profile dialog");
  await dialog.getByLabel("Job title").fill(`Designer ${run}`);
  await dialog.getByRole("button", { name: "Save profile" }).click();
  await expect(dialog).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: "Profile saved for Dana Designer." })).toBeVisible();

  // The saved value is read back with a new revision; restore the job title so the shared organization is as found.
  await person(page, /Dana Designer/)
    .getByRole("button", { name: /^Actions for Dana Designer/ })
    .click();
  await page.getByRole("menuitem", { name: /^Edit profile:/ }).click();
  const again = page.getByRole("dialog", { name: /Edit profile: Dana Designer/ });
  await expect(again.getByLabel("Job title")).toHaveValue(`Designer ${run}`);
  await again.getByLabel("Job title").fill("");
  await again.getByRole("button", { name: "Save profile" }).click();
  await expect(again).toBeHidden({ timeout: 60_000 });
  await expect(person(page, /Dana Designer/)).not.toContainText("Owner"); // a profile edit conferred nothing
  await signOut(page);
});

test("a person edits their own name from My profile and it changes everywhere; resetting restores the identity provider's name", async ({ page }) => {
  await signIn(page, "clerk");
  await page.getByRole("button", { name: /Account menu/ }).click();
  await page.getByRole("menuitem", { name: "My profile" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "My profile" })).toBeVisible();
  const name = page.getByLabel(/Display name/);
  const original = await name.inputValue();
  await axe(page, "my profile");
  await name.fill(`Casey Edited ${run}`);
  await page.getByLabel("Preferred name").fill("Case");
  await page.getByRole("button", { name: "Save profile" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Profile saved" })).toBeVisible();
  await expect(page.getByRole("button", { name: `Account menu for Casey Edited ${run}` })).toBeVisible(); // the header follows at once

  // Sign-in never overwrites the edited name; the owner sees it in the directory.
  await signOut(page);
  await signIn(page, "clerk");
  await expect(page.getByRole("button", { name: `Account menu for Casey Edited ${run}` })).toBeVisible();
  await signOut(page);
  await signIn(page, "owner");
  await openPeople(page);
  await expect(person(page, new RegExp(`Casey Edited ${run}`))).toBeVisible();
  await signOut(page);

  // Restore the identity provider's name.
  await signIn(page, "clerk");
  await page.getByRole("button", { name: /Account menu/ }).click();
  await page.getByRole("menuitem", { name: "My profile" }).click();
  await page.getByRole("checkbox", { name: /Use the name from the identity provider/ }).check();
  await page.getByLabel("Preferred name").fill("");
  await page.getByRole("button", { name: "Save profile" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Profile saved" })).toBeVisible();
  await expect(page.getByRole("button", { name: `Account menu for ${original}` })).toBeVisible();
  await signOut(page);
});

test("presence follows real activity: Available after sign-in, Offline after sign-out, always with a text label", async ({ page, browser, baseURL }) => {
  await signIn(page, "owner");
  const other = await browser.newContext({ baseURL: baseURL ?? "http://127.0.0.1:5199", viewport: { width: 1440, height: 900 } });
  const designer = await other.newPage();
  try {
    await signIn(designer, "designer"); // the shell sends its first heartbeat as soon as it mounts
    await openPeople(page);
    const dana = person(page, /Dana Designer/);
    await expect(dana).toContainText(/Available|Away/);
    await expect(dana).toContainText("Active"); // membership is a separate column from presence
    await expect(person(page, /Avery Owner/)).toContainText(/Available/);
    await expect(dana.locator("[data-presence]")).toHaveAttribute("data-presence", /Available|Away/);
    await axe(page, "people with presence");

    await signOut(designer); // explicit sign-out clears the heartbeat at once
    await openPeople(page);
    await expect(person(page, /Dana Designer/)).toContainText("Offline");
    await expect(person(page, /Dana Designer/)).toContainText("Active"); // still an Active member: offline is not disabled
  } finally {
    await other.close();
  }
  await signOut(page);
});

test("approvals are paged five at a time with independent lists and filters that reset the page", async ({ page }) => {
  await signIn(page, "owner");
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
  const csrf = await csrfToken(page);
  for (let i = 0; i < 6; i++) {
    const status = await page.evaluate(
      async ({ token, subject, name }) =>
        (
          await fetch("/api/v1/organization/membership-requests", {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-CSRF-Token": token, "Idempotency-Key": crypto.randomUUID() },
            body: JSON.stringify({ subjectIdentifier: subject, displayNameHint: name, initialRoleName: null, reason: "paging check", useDevelopmentStepUp: true }),
          })
        ).status,
      { token: csrf, subject: `e2e-page-${run}-${i}`, name: `Page Probe ${run} ${i}` },
    );
    expect(status).toBe(200);
  }
  await openPeople(page);
  const requests = page.getByRole("table", { name: "Membership requests list" });
  await expect(requests.getByRole("row")).toHaveCount(6); // header + five rows, never more
  const nav = page.getByRole("navigation", { name: "Membership requests pages" });
  await expect(nav).toContainText(/Showing 1–5 of \d+ requests/);
  await expect(nav.getByRole("button", { name: "Previous" })).toBeDisabled();
  await expect(requests.getByRole("row", { name: new RegExp(`Page Probe ${run} 5`) })).toBeVisible(); // newest first
  expect(await noHorizontalOverflow(page)).toBe(true);

  await nav.getByRole("button", { name: "Next" }).click();
  await expect(nav).toContainText(/Showing 6–/);
  await expect(page.getByRole("navigation", { name: "Owner proposals pages" }).or(page.getByText("No owner proposals in the last 30 days."))).toBeVisible(); // the other list never moved
  await nav.getByRole("button", { name: "Previous" }).click();
  await expect(nav).toContainText(/Showing 1–5 of/);

  // A filter narrows the server page and starts again at page one; clearing it returns to the full list.
  await page.getByLabel("Search requests").fill(`Page Probe ${run} 3`);
  await expect(requests.getByRole("row")).toHaveCount(2);
  await expect(nav).toContainText("Showing 1–1 of 1 requests");
  await page.getByLabel("Search requests").fill("no such person");
  await expect(page.getByText("No requests match.")).toBeVisible();
  await page.getByRole("button", { name: "Clear filters" }).first().click();
  await expect(requests.getByRole("row")).toHaveCount(6);
  await page
    .getByRole("combobox", { name: "Status" })
    .first()
    .click()
    .catch(() => undefined);
  await page.keyboard.press("Escape");

  // The full object ID is reachable (tooltip and a Copy ID action in the row menu) while the row stays one line high.
  const first = requests.getByRole("row").nth(1);
  await expect(first.locator('[title^="e2e-page-"]')).toHaveCount(1);
  await first.getByRole("button", { name: /^Actions for/ }).click();
  await expect(page.getByRole("menuitem", { name: /^Copy ID:/ })).toBeVisible(); // Copy ID is part of the row Actions menu
  await page.keyboard.press("Escape");
  const height = await first.evaluate((el) => (el as HTMLElement).offsetHeight);
  expect(height).toBeLessThanOrEqual(80);
  await signOut(page);
});

for (const width of [320, 768, 1440]) {
  test(`People and approvals lay out without horizontal overflow at ${width}px`, async ({ page }) => {
    await signIn(page, "owner");
    await openPeople(page, width);
    expect(await noHorizontalOverflow(page)).toBe(true);
    await expect(page.getByRole("table", { name: "Membership requests list" })).toBeVisible();
    await expect(person(page, /Avery Owner/).locator("[data-presence]")).toBeVisible();
    await axe(page, `people at ${width}px`);
    await signOut(page);
  });
}
