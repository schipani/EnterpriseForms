import { execFileSync } from "node:child_process";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C8 M1 — the administration shell in the real application (business API, Authorization Authority, SpiceDB; no
 * mocks), on the isolated security-test stack only (C-74 guard):
 *  1. An organization owner opens the Organization overview, People (read-only), Teams, Branding, Data protection and
 *     Security Administrators pages from the left-hand navigation; every card refreshes on its own.
 *  2. The owner creates a team, adds and removes a member (Authority commands, projection follows), and renames it
 *     (display-only, audited).
 *  3. A Security Administrator sees the overview, people, teams and data protection read-only and no Branding.
 *  4. A member without administration capabilities sees no Organization navigation and a generic no-permission message.
 *  5. axe WCAG 2.2 AA on every administration page.
 */
test.describe.configure({ mode: "serial" });

const orgNav = (page: Page, name: string) => page.getByRole("navigation", { name: "Organization" }).getByRole("link", { name, exact: true });

async function axe(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations, JSON.stringify(results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })))).toEqual([]);
}

const teamName = `Intake E2E ${Date.now().toString(36)}`;

test.describe("Organization administration (C8 M1)", () => {
  test("an organization owner navigates the administration shell and every card refreshes independently", async ({ page }) => {
    await signIn(page, "owner");
    await page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Organization", exact: true }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Organization overview" })).toBeVisible();
    await expect(orgNav(page, "Overview")).toHaveAttribute("aria-current", "page");
    for (const name of ["Organization", "Members and roles", "Teams", "Authentication", "Data protection", "Security Administrators"]) {
      await expect(page.getByRole("region", { name })).toBeVisible();
      await expect(page.getByRole("button", { name: `Refresh ${name}` })).toBeVisible();
    }
    await expect(page.getByRole("region", { name: "Members and roles" })).toContainText(/Member count source: business projection/);
    await expect(page.getByRole("region", { name: "Authentication" })).toContainText("Not enforced — awaits PB-08");
    await expect(page.getByRole("region", { name: "Data protection" })).toContainText("Minimum field classification");
    await page.getByRole("button", { name: "Refresh Data protection" }).click();
    await expect(page.getByRole("region", { name: "Data protection" })).toContainText("Minimum field classification");
    await axe(page);

    await orgNav(page, "People").click();
    await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
    const people = page.getByRole("table", { name: "People" });
    await expect(people.getByRole("rowheader", { name: /Avery Owner/ })).toBeVisible();
    await expect(page.getByText(/Source: Authorization Authority roster/)).toBeVisible();
    // C8 M2 (AC-7): the owner's own sign-in is recorded; owner actions are offered for others, never for themself.
    await expect(people.getByRole("row", { name: /Avery Owner/ })).toContainText(/Available|Away|Offline|Presence unknown/);
    await expect(people.getByRole("row", { name: /Avery Owner/ }).getByRole("button")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Add user" })).toBeEnabled();
    await page.getByLabel("Search people").fill("casey");
    await expect(people.getByRole("rowheader", { name: /Casey Clerk/ })).toBeVisible();
    await expect(people.getByRole("rowheader", { name: /Avery Owner/ })).toHaveCount(0);
    await axe(page);

    await orgNav(page, "Branding").click();
    await expect(page.getByRole("heading", { level: 1, name: "Organization branding" })).toBeVisible();
    await expect(page.getByRole("region", { name: "Data protection policy" })).toHaveCount(0);
    await orgNav(page, "Data protection").click();
    await expect(page.getByRole("heading", { level: 1, name: "Data protection" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Save data protection policy" })).toBeVisible();
    await axe(page);
    await orgNav(page, "Security administrators").click();
    await expect(page.getByRole("heading", { level: 1, name: "Security Administrators" })).toBeVisible();
    await expect(page.getByText("Only Security Administrators can appoint or remove Security Administrators.")).toBeVisible();
    await axe(page);
    await signOut(page);
  });

  test("an organization owner creates a team, adds and removes a member, and renames it", async ({ page }) => {
    test.setTimeout(150_000);
    await signIn(page, "owner");
    await page.goto("/app/organization/teams");
    await expect(page.getByRole("heading", { level: 1, name: "Teams" })).toBeVisible();
    await page.getByRole("button", { name: "Create team" }).click();
    const create = page.getByRole("dialog", { name: "Create team" });
    await create.getByLabel("Team name").fill(teamName);
    await create.getByRole("button", { name: "Create team" }).click();
    await expect(page.getByRole("heading", { level: 1, name: teamName })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole("heading", { level: 2, name: /^Members \(0\)/ })).toBeVisible();
    await axe(page);

    await page.getByRole("button", { name: "Add member" }).click();
    const add = page.getByRole("dialog", { name: "Add a member" });
    await add.getByRole("combobox", { name: /Person/ }).click();
    await page.getByRole("option", { name: /Casey Clerk/ }).click();
    await add.getByRole("button", { name: "Add member" }).click();
    await expect(add).toBeHidden({ timeout: 60_000 });
    const roster = page.getByRole("table", { name: "Team members" });
    await expect(roster.getByRole("row", { name: /Casey Clerk/ })).toContainText("Effective", { timeout: 60_000 });
    await expect(page.getByRole("status").filter({ hasText: "Pending" })).toHaveCount(0);

    await page.getByRole("button", { name: "Rename" }).click();
    const rename = page.getByRole("dialog", { name: "Rename team" });
    await rename.getByLabel("Team name").fill(`${teamName} renamed`);
    await rename.getByRole("button", { name: "Save name" }).click();
    await expect(page.getByRole("heading", { level: 1, name: `${teamName} renamed` })).toBeVisible();
    // Membership survives the display-only rename.
    await expect(roster.getByRole("row", { name: /Casey Clerk/ })).toBeVisible();

    // The dev-seed display name carries a role suffix ("Casey Clerk (record user)"), so match the start and the end.
    await roster.getByRole("button", { name: /^Remove Casey Clerk.* from the team$/ }).click();
    await page.getByRole("dialog", { name: /^Remove Casey Clerk/ }).getByRole("button", { name: "Remove member" }).click();
    await expect(page.getByText("This team has no members yet.")).toBeVisible({ timeout: 60_000 });

    await page.getByRole("link", { name: "All teams" }).click();
    await expect(page.getByRole("table", { name: "Teams" }).getByRole("row", { name: new RegExp(`${teamName} renamed`) })).toContainText("0");
    await signOut(page);
  });

  test("a committed but non-effective membership command is shown as Pending, never as done, then reconciles itself to Effective", async ({ page }) => {
    // Deterministic fault (the SEC-P12 mechanism): pausing the mandatory audit archive keeps the Authority from making the command Effective.
    test.setTimeout(180_000);
    const archive = process.env["E2E_ARCHIVE_CONTAINER"] ?? "enterpriseforms-sectest-archive-store-1";
    const docker = (verb: "pause" | "unpause") => execFileSync("docker", [verb, archive], { stdio: "pipe" });
    const pendingTeam = `Pending E2E ${Date.now().toString(36)}`;
    await signIn(page, "owner");
    await page.goto("/app/organization/teams");
    await page.getByRole("button", { name: "Create team" }).click();
    const create = page.getByRole("dialog", { name: "Create team" });
    await create.getByLabel("Team name").fill(pendingTeam);
    await create.getByRole("button", { name: "Create team" }).click();
    await expect(page.getByRole("heading", { level: 1, name: pendingTeam })).toBeVisible({ timeout: 60_000 });

    docker("pause");
    let paused = true;
    try {
      await page.getByRole("button", { name: "Add member" }).click();
      const add = page.getByRole("dialog", { name: "Add a member" });
      await add.getByRole("combobox", { name: /Person/ }).click();
      await page.getByRole("option", { name: /Casey Clerk/ }).click();
      await add.getByRole("button", { name: "Add member" }).click();
      // The command is committed but not effective: the page says Pending and never shows the member or an Effective state.
      const pendingStatus = page.getByRole("status").filter({ hasText: "Pending" });
      await expect(pendingStatus).toBeVisible({ timeout: 90_000 });
      await expect(pendingStatus).toContainText("has not yet confirmed");
      await expect(page.getByRole("table", { name: "Team members" }).getByRole("row", { name: /Casey Clerk/ })).toHaveCount(0);
      await expect(page.getByText("Effective", { exact: true })).toHaveCount(0);
      // The pending operation (team, person, direction and the ORIGINAL idempotency key; nothing secret) is kept in session storage.
      // (A reload while the Authority gate is closed fails the whole application closed at bootstrap, so reload survival is covered by unit tests.)
      const stored = await page.evaluate(() => Object.entries(window.sessionStorage).filter(([k]) => k.startsWith("ef.pendingMembership.")).map(([, v]) => v));
      expect(stored).toHaveLength(1);
      expect(stored[0]).toContain("idempotencyKey");
    } finally {
      if (paused) {
        docker("unpause");
        paused = false;
      }
    }

    // After the archive returns, the page re-checks the SAME command by itself (no new click, no new key): Pending turns into
    // Effective only when the Authority confirms it, and the roster then shows the member exactly once.
    const roster = page.getByRole("table", { name: "Team members" });
    await expect(page.getByRole("status").filter({ hasText: "Effective" })).toBeVisible({ timeout: 150_000 });
    await expect(roster.getByRole("row", { name: /Casey Clerk/ })).toHaveCount(1, { timeout: 60_000 });
    await expect(page.getByRole("status").filter({ hasText: "Pending" })).toHaveCount(0);
    expect(await page.evaluate(() => Object.keys(window.sessionStorage).filter((k) => k.startsWith("ef.pendingMembership.")))).toEqual([]);
    await page.reload();
    await expect(roster.getByRole("row", { name: /Casey Clerk/ })).toHaveCount(1);
    await expect(page.getByRole("status").filter({ hasText: "Pending" })).toHaveCount(0);
    await signOut(page);
  });

  test("a Security Administrator reads the overview, people and teams, may administer teams but is never offered themself, and has no Branding", async ({ page }) => {
    await signIn(page, "security");
    await page.goto("/app/organization");
    await expect(page.getByRole("heading", { level: 1, name: "Organization overview" })).toBeVisible();
    await expect(orgNav(page, "Branding")).toHaveCount(0);
    await expect(orgNav(page, "Search approvals")).toBeVisible();
    await orgNav(page, "Teams").click();
    await expect(page.getByRole("heading", { level: 1, name: "Teams" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Create team" })).toBeVisible();
    await page.getByRole("table", { name: "Teams" }).getByRole("link", { name: `${teamName} renamed` }).click();
    await expect(page.getByRole("heading", { level: 1, name: `${teamName} renamed` })).toBeVisible();
    // The Authority's existing team rule (G25 mirrored): a Security Administrator may add others, never themself.
    await page.getByRole("button", { name: "Add member" }).click();
    const add = page.getByRole("dialog", { name: "Add a member" });
    await add.getByRole("combobox", { name: /Person/ }).click();
    await expect(page.getByRole("option", { name: /Casey Clerk/ })).toBeVisible();
    await expect(page.getByRole("option", { name: /Sam Security/ })).toHaveCount(0);
    await page.keyboard.press("Escape");
    await add.getByRole("button", { name: "Cancel" }).click();
    await expect(add).toBeHidden();
    await axe(page);
    // C8 M2: People for a Security Administrator lists every enabled Authority binding and offers the existing
    // Security Administrator appointment (reason + development step-up); cancelled here so the shared organization is unchanged.
    await orgNav(page, "People").click();
    await expect(page.getByText(/Lists every identity the Authorization Authority has bound/)).toBeVisible();
    await page.getByRole("button", { name: /^Actions for Casey Clerk/ }).click();
    await page.getByRole("menuitem", { name: /^Make Security Administrator: Casey Clerk/ }).click();
    const appoint = page.getByRole("dialog", { name: /Make Casey Clerk.* a Security Administrator/ });
    await expect(appoint.getByRole("button", { name: "Appoint" })).toBeDisabled();
    await appoint.getByLabel(/Reason/).fill("browser test, cancelled");
    await expect(appoint.getByRole("button", { name: "Appoint" })).toBeDisabled();
    await axe(page);
    await appoint.getByRole("button", { name: "Cancel" }).click();
    await expect(appoint).toBeHidden();
    await axe(page);
    await orgNav(page, "Data protection").click();
    await expect(page.getByText("Only the organization owner can change this policy.")).toBeVisible();
    await page.goto("/app/organization/branding");
    await expect(page.getByText("Only an organization owner can change branding.")).toBeVisible();
    await signOut(page);
  });

  test("a member without administration capabilities gets no Organization navigation and a generic message", async ({ page }) => {
    await signIn(page, "clerk");
    await expect(page.getByRole("navigation", { name: "Primary" }).getByRole("link", { name: "Organization", exact: true })).toHaveCount(0);
    for (const path of ["/app/organization", "/app/organization/users", "/app/organization/teams", "/app/organization/data-protection"]) {
      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1, name: "Organization" })).toBeVisible();
      await expect(page.getByText("Organization administration is for organization owners and Security Administrators.")).toBeVisible();
      await expect(page.getByRole("navigation", { name: "Organization" })).toHaveCount(0);
    }
    await axe(page);
    await signOut(page);
  });
});
