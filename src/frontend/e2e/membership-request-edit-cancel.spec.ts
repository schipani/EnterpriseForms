import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * C8 M2 (AC-7.5) in the real application on the isolated security-test stack: an owner edits a pending membership request (identity
 * read-only; name, role and reason change; the request is replaced by a new pending one) and cancels another (confirmation, reason,
 * step-up). Finished requests keep their status (Replaced, Cancelled), offer the right actions and "Create new request" pre-fills Add
 * user. Five-row paging and status filters keep working, with keyboard use and axe at phone and desktop widths. Requests cannot be
 * withdrawn from the shared organization afterwards, so each run uses unique people.
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
}

async function createRequest(page: Page, key: string, name: string, role: string | null = "Designer") {
  const csrf = await page.evaluate(async () => ((await (await fetch("/api/v1/session")).json()) as { csrfToken: string }).csrfToken);
  const status = await page.evaluate(
    async ({ token, subject, display, initial }) =>
      (
        await fetch("/api/v1/organization/membership-requests", {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-CSRF-Token": token, "Idempotency-Key": crypto.randomUUID() },
          body: JSON.stringify({ subjectIdentifier: subject, displayNameHint: display, initialRoleName: initial, reason: "edit and cancel check", useDevelopmentStepUp: true }),
        })
      ).status,
    { token: csrf, subject: `e2e-${key}-${run}`, display: name, initial: role },
  );
  expect(status).toBe(200);
}

const requests = (page: Page) => page.getByRole("table", { name: "Membership requests list" });
const requestRow = (page: Page, name: RegExp) => requests(page).getByRole("row", { name });

async function searchRequests(page: Page, text: string, status = "Any status") {
  await page.getByLabel("Search requests").fill(text);
  if (status !== "Any status") {
    const select = page.getByRole("combobox", { name: "Status" }).nth(1);
    await select.click();
    await page.getByRole("option", { name: status, exact: true }).click();
  }
}

test("an owner edits a pending request: identity is read-only, the request is replaced by a new pending one and the original is kept as Replaced", async ({ page }) => {
  await signIn(page, "owner");
  await createRequest(page, "edit", `Edit Probe ${run}`);
  await openPeople(page);
  await searchRequests(page, `Edit Probe ${run}`);
  const row = requestRow(page, new RegExp(`Edit Probe ${run}`));
  await expect(row).toContainText("Awaiting operators");
  await row.getByRole("button", { name: /^Actions for/ }).click();
  await expect(page.getByRole("menuitem")).toHaveText(["Edit request", "Cancel request", "Copy ID"]);
  await page.getByRole("menuitem", { name: /^Edit request:/ }).click();

  const dialog = page.getByRole("dialog", { name: /Edit request: Edit Probe/ });
  await expect(dialog.getByLabel("Object ID")).toHaveValue(`e2e-edit-${run}`);
  await expect(dialog.getByLabel("Object ID")).toHaveAttribute("readonly", "");
  await expect(dialog.getByLabel("Identity provider")).toHaveAttribute("readonly", "");
  await expect(dialog.getByText(/cancel this request and create a new one/)).toBeVisible();
  await expect(dialog.getByLabel(/^Name/)).toHaveValue(`Edit Probe ${run}`);
  await expect(dialog.getByRole("combobox", { name: /Initial role/ })).toContainText("Designer");
  await axe(page, "edit request dialog");

  // validation, then a keyboard-only close and reopen
  await dialog.getByLabel(/^Name/).fill("");
  await dialog.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await dialog.getByRole("button", { name: "Save changes" }).click();
  await expect(dialog.getByText("Enter the person's name.")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(row).toContainText("Awaiting operators"); // nothing was saved

  await row.getByRole("button", { name: /^Actions for/ }).click();
  await page.getByRole("menuitem", { name: /^Edit request:/ }).click();
  const again = page.getByRole("dialog", { name: /Edit request/ });
  await again.getByLabel(/^Name/).fill(`Edit Probe ${run} Okafor`);
  await again.getByRole("combobox", { name: /Initial role/ }).click();
  await page.getByRole("option", { name: "No role" }).click();
  await again.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await again.getByRole("button", { name: "Save changes" }).click();
  await expect(again).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: `Request for Edit Probe ${run} Okafor updated` })).toBeVisible();

  // The list refreshed without a reload: the edited request is pending and linked; the original is kept as Replaced.
  await searchRequests(page, `Edit Probe ${run}`);
  const edited = requestRow(page, new RegExp(`Edit Probe ${run} Okafor`));
  await expect(edited).toContainText("Awaiting operators");
  await expect(edited).toContainText("Edited request (replaces an earlier one)");
  await expect(edited).toContainText(`e2e-edit-${run}`); // same identity
  const original = requestRow(page, new RegExp(`^Edit Probe ${run} e2e-edit`));
  await expect(original).toContainText("Replaced");
  await expect(original).toContainText("Replaced by a newer request");
  await original.getByRole("button", { name: /^Actions for/ }).click();
  await expect(page.getByRole("menuitem")).toHaveText(["View details", "Copy ID"]);
  await page.getByRole("menuitem", { name: /^View details:/ }).click();
  const details = page.getByRole("dialog", { name: /Request details/ });
  await expect(details).toContainText("Replaced by");
  await expect(details).toContainText("Designer"); // the original is preserved as it was
  await details.getByRole("button", { name: "Close" }).click();

  // Status filter: Replaced.
  await searchRequests(page, `Edit Probe ${run}`, "Replaced");
  await expect(requests(page).getByRole("row")).toHaveCount(2);
  await signOut(page);
});

test("an owner cancels a pending request after confirmation; the request stays as Cancelled and Create new request pre-fills Add user", async ({ page }) => {
  await signIn(page, "owner");
  await createRequest(page, "cancel", `Cancel Probe ${run}`);
  await openPeople(page);
  await searchRequests(page, `Cancel Probe ${run}`);
  const row = requestRow(page, new RegExp(`Cancel Probe ${run}`));
  await row.getByRole("button", { name: /^Actions for/ }).click();
  await page.getByRole("menuitem", { name: /^Cancel request:/ }).click();
  const dialog = page.getByRole("dialog", { name: /Cancel request: Cancel Probe/ });
  await axe(page, "cancel request dialog");
  await dialog.getByRole("button", { name: "Cancel request" }).click();
  await expect(dialog.getByText("Give a reason (3 to 500 characters).")).toBeVisible(); // confirmation needs a reason
  await dialog.getByRole("button", { name: "Keep request" }).click();
  await expect(dialog).toBeHidden();
  await expect(row).toContainText("Awaiting operators");

  await row.getByRole("button", { name: /^Actions for/ }).click();
  await page.getByRole("menuitem", { name: /^Cancel request:/ }).click();
  const confirm = page.getByRole("dialog", { name: /Cancel request: Cancel Probe/ });
  await confirm.getByLabel(/Reason/).fill(`wrong unit ${run}`);
  await confirm.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await confirm.getByRole("button", { name: "Cancel request" }).click();
  await expect(confirm).toBeHidden({ timeout: 60_000 });
  await expect(page.getByRole("status").filter({ hasText: /cancelled/ })).toBeVisible();
  await expect(row).toContainText("Cancelled");
  await row.getByRole("button", { name: /^Actions for/ }).click();
  await expect(page.getByRole("menuitem")).toHaveText(["View details", "Create new request", "Copy ID"]);
  await page.getByRole("menuitem", { name: /^Create new request:/ }).click();
  const add = page.getByRole("dialog", { name: "Add user" });
  await expect(add.getByLabel(/Object ID/)).toHaveValue(`e2e-cancel-${run}`);
  await expect(add.getByLabel(/^Name/)).toHaveValue(`Cancel Probe ${run}`);
  await add.getByLabel(/Reason/).fill("requested again after cancellation");
  await add.getByRole("checkbox", { name: /development-only step-up/ }).check();
  await add.getByRole("button", { name: "Request membership" }).click();
  await expect(add).toBeHidden({ timeout: 60_000 });
  await searchRequests(page, `Cancel Probe ${run}`);
  await expect(requests(page).getByRole("row")).toHaveCount(3); // header + the cancelled original + the new pending request
  await expect(requests(page).getByRole("row", { name: /Awaiting operators/ })).toHaveCount(1);
  await searchRequests(page, `Cancel Probe ${run}`, "Cancelled");
  await expect(requests(page).getByRole("row")).toHaveCount(2);
  await signOut(page);
});

test("the request actions stay usable at phone width without overflow and without accessibility violations; paging stays at five rows", async ({ page }) => {
  await signIn(page, "owner");
  for (let i = 0; i < 6; i++) {
    await createRequest(page, `page${i}`, `Page Probe ${run} ${i}`, null);
  }
  for (const width of [320, 768, 1440]) {
    await openPeople(page, width);
    await searchRequests(page, `Page Probe ${run}`);
    await expect(page.getByRole("navigation", { name: "Membership requests pages" })).toContainText("Showing 1–5 of 6 requests");
    await expect(requests(page).getByRole("row")).toHaveCount(6);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    const first = requests(page).getByRole("row").nth(1);
    await first.getByRole("button", { name: /^Actions for/ }).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("menuitem").first()).toBeVisible();
    await page.keyboard.press("Escape");
    await axe(page, `membership requests at ${width}px`);
  }
  await signOut(page);
});
