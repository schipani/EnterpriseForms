import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Owner message 7 — Security Administrator management in Organization settings, in the real application (business API,
 * Authorization Authority, SpiceDB; no mocks).
 *  1. An organization owner sees the Security Administrators read-only: no Add, no Remove, and the reason why.
 *  2. A Security Administrator appoints an existing verified member (picked from a list, never typed), with a reason and
 *     the development-only step-up (this isolated stack only). The appointee gets Search approvals at once.
 *  3. The last Security Administrator cannot be removed; removing the appointee takes the role away at once.
 *  4. axe WCAG 2.2 AA on the section and its dialogs.
 * Requires the dev-seed "security" persona and the Authority's FieldSearch:ApprovalStepUp=SyntheticDevelopment
 * (compose.security-test.yaml). The seeded organization ends as it started (Sam Security the only administrator).
 */
test.describe.configure({ mode: "serial" });

const table = (page: Page) => page.getByRole("table", { name: "Security Administrators" });
const nav = (page: Page, name: string) => page.getByRole("navigation").getByRole("link", { name, exact: true });

async function axe(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations, JSON.stringify(results.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })))).toEqual([]);
}

test.describe("Security Administrators", () => {
  test("an organization owner sees the administrators read-only and cannot appoint anyone", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/organization/branding");
    await expect(table(page)).toBeVisible();
    test.skip((await table(page).getByRole("row", { name: /Sam Security/ }).count()) === 0, "this organization predates the dev-seed security persona (recreate the stack to run this spec)");
    await expect(table(page).getByRole("row", { name: /Sam Security/ })).toContainText("Active");
    await expect(page.getByRole("button", { name: "Add administrator" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /Remove .* as Security Administrator/ })).toHaveCount(0);
    await expect(page.getByText("Only Security Administrators can appoint or remove Security Administrators.")).toBeVisible();
    await signOut(page);
  });

  test("a Security Administrator appoints a verified member, who gets Search approvals at once, then removes them", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "security");
    await expect(nav(page, "Organization")).toBeVisible();
    await nav(page, "Organization").click();
    await expect(page.getByRole("heading", { name: "Organization settings" })).toBeVisible();
    await expect(table(page).getByRole("row", { name: /Sam Security.*\(you\)/ })).toBeVisible();

    // The last administrator cannot be removed.
    await expect(page.getByRole("button", { name: "Remove Sam Security (System Administrator) as Security Administrator" })).toBeDisabled();
    await expect(page.getByText("The last Security Administrator cannot be removed. Appoint another one first.")).toBeVisible();
    await axe(page);

    // Appoint Casey Clerk: a person from the list, a reason and the development step-up.
    await page.getByRole("button", { name: "Add administrator" }).click();
    const add = page.getByRole("dialog", { name: "Add a Security Administrator" });
    await expect(add.getByRole("button", { name: "Appoint" })).toBeDisabled();
    await add.getByRole("combobox", { name: /Person/ }).click();
    await page.getByRole("option", { name: /Casey Clerk/ }).click();
    await add.getByRole("textbox", { name: /Reason/ }).fill("Governance ticket SA-E2E: second administrator");
    await expect(add.getByRole("button", { name: "Appoint" })).toBeDisabled(); // step-up not yet confirmed
    await add.getByRole("checkbox", { name: "Confirm with the development-only step-up" }).check();
    await axe(page);
    await add.getByRole("button", { name: "Appoint" }).click();
    await expect(add).toBeHidden();
    await expect(table(page).getByRole("row", { name: /Casey Clerk/ })).toContainText("Active");
    await expect(page.getByRole("heading", { name: "Security Administrators (2)" })).toBeVisible();
    await signOut(page);

    // The appointee has Search approvals and the section, on a fresh sign-in, with no other change.
    await signIn(page, "clerk");
    await expect(nav(page, "Search approvals")).toBeVisible();
    await nav(page, "Search approvals").click();
    await expect(page.getByRole("heading", { name: /Search approvals/ }).first()).toBeVisible();
    await nav(page, "Organization").click();
    await expect(table(page).getByRole("row", { name: /Casey Clerk.*\(you\)/ })).toBeVisible();
    await signOut(page);

    // Removal: confirm with a reason and the step-up; the role is gone.
    await signIn(page, "security");
    await page.goto("/app/organization/branding");
    await page.getByRole("button", { name: /Remove Casey Clerk.* as Security Administrator/ }).click();
    const remove = page.getByRole("dialog", { name: /Remove Casey Clerk/ });
    await remove.getByRole("textbox", { name: /Reason/ }).fill("Governance ticket SA-E2E: rotation");
    await remove.getByRole("checkbox", { name: "Confirm with the development-only step-up" }).check();
    await axe(page);
    await remove.getByRole("button", { name: "Remove administrator" }).click();
    await expect(remove).toBeHidden();
    await expect(table(page).getByRole("row", { name: /Casey Clerk/ })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Security Administrators (1)" })).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await expect(nav(page, "Search approvals")).toHaveCount(0);
    await expect(nav(page, "Organization")).toHaveCount(0);
    await page.goto("/app/organization/branding");
    await expect(page.getByText("Only an organization owner can change branding.")).toBeVisible();
  });
});
