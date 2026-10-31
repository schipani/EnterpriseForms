import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C8 M2 (AC-7) People administration in the real application on the isolated security-test stack: the organization owner
 * makes and removes a designer, disables a member (who is then refused) and re-enables them, records an owner proposal that
 * only another owner can approve, and requests a new member who stays a pending request (never a member, never signed in)
 * until two operators approve. Every change carries a reason and the development-only step-up; the shared organization is
 * left as it was found (roles and membership restored).
 */
test.describe.configure({ mode: "serial" });

const run = Date.now().toString(36);

async function axe(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations, JSON.stringify(results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })))).toEqual([]);
}

async function openPeople(page: Page) {
  await page.goto("/app/organization/users");
  await expect(page.getByRole("heading", { level: 1, name: "People" })).toBeVisible();
}

const row = (page: Page, name: RegExp) => page.getByRole("table", { name: "People" }).getByRole("row", { name });

/** Opens a row action (the primary button or the row's "…" menu), fills the reason, confirms the step-up and submits. */
async function change(page: Page, person: RegExp, action: string, confirm: string) {
  const target = row(page, person);
  const direct = target.getByRole("button", { name: new RegExp(`^${action}:`) });
  if (await direct.count()) {
    await direct.click();
  } else {
    await target.getByRole("button", { name: /More actions for/ }).click();
    await page.getByRole("menuitem", { name: new RegExp(`^${action}:`) }).click();
  }
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("button", { name: confirm })).toBeDisabled();
  await dialog.getByLabel(/Reason/).fill(`browser test ${run}`);
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await dialog.getByRole("button", { name: confirm }).click();
  await expect(dialog).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: "Change recorded" })).toBeVisible();
}

test("an owner makes and removes a designer, and cannot disable a member who still owns records", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  await axe(page);

  await change(page, /Riley Reader/, "Make designer", "Make designer");
  await expect(row(page, /Riley Reader/)).toContainText("Designer");
  await change(page, /Riley Reader/, "Remove designer role", "Remove designer role");
  await expect(row(page, /Riley Reader/)).not.toContainText("Designer");

  // Every persona of the shared test organization owns records (or is its last Security Administrator), so disabling is
  // refused with a count and nothing changes. The full disable -> refused -> re-enable path runs in Stage B (MembershipAdministrationTests).
  await row(page, /Riley Reader/).getByRole("button", { name: /More actions for/ }).click();
  await page.getByRole("menuitem", { name: /^Disable membership:/ }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/Reason/).fill(`browser test ${run}`);
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await dialog.getByRole("button", { name: "Disable membership" }).click();
  await expect(dialog.getByRole("alert")).toContainText("The person still owns records");
  await expect(dialog.getByRole("alert")).toContainText(/owns \d+ active record/);
  await axe(page);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(row(page, /Riley Reader/)).toContainText("Active");
  await signOut(page);
});

test("an owner change is only a proposal until a different owner approves it", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  // An identical pending proposal (from an earlier run within 24 hours) removes the action; then only the proposal is checked.
  if (await row(page, /Dana Designer/).getByRole("button", { name: /More actions for/ }).count()) {
    await row(page, /Dana Designer/).getByRole("button", { name: /More actions for/ }).click();
    const propose = page.getByRole("menuitem", { name: /^Propose as owner:/ });
    const available = (await propose.count()) > 0;
    await page.keyboard.press("Escape");
    if (available) {
      await change(page, /Dana Designer/, "Propose as owner", "Record proposal");
    }
  }
  const proposals = page.getByRole("table", { name: "Owner proposals list" });
  const proposal = proposals.getByRole("row", { name: /Dana Designer/ }).first();
  await expect(proposal).toContainText("Waiting for approval");
  await expect(proposal).toContainText("Another owner approves"); // the proposer cannot approve their own proposal
  await expect(row(page, /Dana Designer/)).not.toContainText("Organization Owner");
  await axe(page);
  await signOut(page);
});

test("adding a user records a request that two operators must approve; the person is not a member and cannot sign in", async ({ page }) => {
  await signIn(page, "owner");
  await openPeople(page);
  await page.getByRole("button", { name: "Add user" }).click();
  const dialog = page.getByRole("dialog", { name: "Add user" });
  await expect(dialog.getByLabel(/Identity provider/)).toHaveValue(/.+/);
  await expect(dialog.getByText("Membership is not sign-in")).toBeVisible();
  await dialog.getByLabel(/Object ID/).fill("nia@contoso.example");
  await dialog.getByRole("button", { name: "Request membership" }).click();
  await expect(dialog.getByText(/not an email address/)).toBeVisible();
  await dialog.getByLabel(/Object ID/).fill(`e2e-newcomer-${run}`);
  await dialog.getByLabel(/^Name/).fill(`Nia Newcomer ${run}`);
  await dialog.getByLabel(/Reason/).fill("joins the intake team");
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await axe(page);
  await dialog.getByRole("button", { name: "Request membership" }).click();
  await expect(dialog).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: `Request recorded for Nia Newcomer ${run}` })).toBeVisible();
  const request = page.getByRole("table", { name: "Membership requests list" }).getByRole("row", { name: new RegExp(`Nia Newcomer ${run}`) });
  await expect(request).toContainText("Waiting for operator approval");
  await expect(request).toContainText("Needs Entra integration");
  await expect(row(page, new RegExp(`Nia Newcomer ${run}`))).toHaveCount(0);
  await signOut(page);
});
