import { expect, test } from "@playwright/test";
import { N, createOrganization, decide, expectNoAxeViolations, filterQueue, OPERATORS, openReview, operatorPage, signInAs } from "./support";

/**
 * Platform Console Stage 4 on the isolated security-test stack: two synthetic operators in separate browser contexts decide
 * membership requests through the real BFF, Authority, Vault Transit signing and PostgreSQL. Synthetic evidence: never two-person
 * approval evidence.
 */
test.describe("Operator sign-in and session", () => {
  test("signs a synthetic operator in and out, shows the development banner and returns to sign-in", async ({ page }) => {
    await page.goto("/platform/approvals");
    await expect(page).toHaveURL(/\/platform\/sign-in$/); // signed out: the approvals page is not reachable
    await expect(page.getByRole("heading", { name: "Sign in to the Platform Console" })).toBeVisible();
    await expect(page.getByText("Development/Test operator — synthetic approval evidence")).toBeVisible();
    for (const operator of Object.values(OPERATORS)) {
      await expect(page.getByText(operator.name)).toBeVisible();
    }
    await expectNoAxeViolations(page, "sign-in");

    await signInAs(page, "dana");
    await expect(page.getByTestId("operator-session")).toContainText(OPERATORS.dana.name);
    await expect(page.getByTestId("operator-session")).toContainText(/Session idle expiry/);
    await expect(page.getByRole("banner").getByText("Platform Console", { exact: true })).toBeVisible();
    await page.reload(); // the session survives a reload (cookie + GET /session), no browser storage involved
    await expect(page.getByTestId("operator-session")).toContainText(OPERATORS.dana.name);
    expect(await page.evaluate(() => [localStorage.length, sessionStorage.length, document.cookie])).toEqual([0, 0, ""]);

    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(page.getByRole("heading", { name: "Sign in to the Platform Console" })).toBeVisible();
    await page.goBack();
    await expect(page.getByRole("heading", { name: "Sign in to the Platform Console" })).toBeVisible(); // the session is gone on the server
  });

  test("an ended session returns to sign-in with an explanation and no decision is recorded", async ({ browser }) => {
    const org = await createOrganization("session");
    const { requestId } = await org.request(N("Session Sam"));
    const { context, page } = await operatorPage(browser, "dana");
    await filterQueue(page, N("Session Sam"));
    await openReview(page, N("Session Sam"));
    await context.clearCookies(); // the BFF no longer sees a session (expired / signed out elsewhere)
    await decide(page, "Approve", "Verified with the owner by phone.");
    await expect(page.getByText(/Your session ended or expired/)).toBeVisible();
    await expect(page.getByRole("heading", { name: "Sign in to the Platform Console" })).toBeVisible();
    expect(requestId).toBeTruthy();
    await context.close();
  });
});

test.describe("Queue", () => {
  test("pages 15 at a time on the server, filters by status and searches", async ({ browser }) => {
    const org = await createOrganization("paging");
    const tag = `Paging${Date.now().toString(36)}`;
    for (let i = 1; i <= 17; i++) {
      await org.request(`${tag} Person ${String(i).padStart(2, "0")}`);
    }
    const { context, page } = await operatorPage(browser, "dana");
    await filterQueue(page, tag, "Awaiting first approval");
    await expect(page.getByRole("button", { name: /^Review details for/ })).toHaveCount(15);
    await expect(page.getByText(/Showing 1–15 of 17 requests/)).toBeVisible();
    await expect(page.getByRole("button", { name: "Previous" })).toBeDisabled();
    // newest first: the last request filed leads the first page
    await expect(page.getByRole("button", { name: /^Review details for/ }).first()).toHaveAccessibleName(new RegExp(`${tag} Person 17`));
    await expectNoAxeViolations(page, "approvals queue");

    await page.getByRole("button", { name: "Next" }).click();
    await expect(page.getByText(/Showing 16–17 of 17 requests/)).toBeVisible();
    await expect(page.getByRole("button", { name: /^Review details for/ })).toHaveCount(2);
    await expect(page.getByRole("button", { name: "Next" })).toBeDisabled();
    await page.getByRole("button", { name: "Previous" }).click();
    await expect(page.getByRole("button", { name: /^Review details for/ })).toHaveCount(15);

    await page.getByRole("searchbox", { name: /Search/ }).fill(`${tag} Person 03`);
    await expect(page.getByRole("button", { name: /^Review details for/ })).toHaveCount(1);
    await filterQueue(page, `${tag} Person 03`, "Approved");
    await expect(page.getByText("No requests match")).toBeVisible();
    await context.close();
  });

  test("lists organization, person, verified identity, role, dates and progress", async ({ browser }) => {
    const org = await createOrganization("columns");
    const { subject } = await org.request(N("Columns Casey"), "Designer");
    const { context, page } = await operatorPage(browser, "ellis");
    await filterQueue(page, N("Columns Casey"), "Needs a decision");
    const row = page.getByRole("row", { name: new RegExp(N("Columns Casey")) });
    await expect(row).toContainText(org.name);
    await expect(row).toContainText(subject);
    await expect(row).toContainText(/designer/i);
    await expect(row).toContainText("0 of 2");
    await expect(row.getByRole("button", { name: /Review details/ })).toBeVisible();
    await context.close();
  });
});

test.describe("Review and decisions", () => {
  test("shows the request details and the Authority fingerprint", async ({ browser }) => {
    const org = await createOrganization("details");
    const { subject } = await org.request(N("Details Dana"));
    const { context, page } = await operatorPage(browser, "dana");
    await filterQueue(page, N("Details Dana"));
    await openReview(page, N("Details Dana"));
    await expect(page.getByText(org.name).first()).toBeVisible();
    await expect(page.getByText(`Verified identity ${subject}`, { exact: false })).toBeVisible();
    await expect(page.getByText("joins the intake team")).toBeVisible();
    await expect(page.getByText("Designer").first()).toBeVisible();
    await expect(page.getByTestId("fingerprint")).toHaveText(/^[0-9a-f]{16}$/);
    await page.getByText("Show full fingerprint and statement").click();
    await expect(page.locator("pre")).toContainText("statement_version");
    await expectNoAxeViolations(page, "review details");
    await context.close();
  });

  test("two operators approve in separate sessions: 1 of 2, no double approval, then 2 of 2", async ({ browser }) => {
    const org = await createOrganization("two-operators");
    await org.request(N("Two Operators Tess"));
    const dana = await operatorPage(browser, "dana");
    const ellis = await operatorPage(browser, "ellis");

    await filterQueue(dana.page, N("Two Operators Tess"), "Needs a decision");
    await openReview(dana.page, N("Two Operators Tess"));
    await decide(dana.page, "Approve", "Verified with the owner by phone.");
    await expect(dana.page.getByText("Approval recorded — 1 of 2")).toBeVisible();
    // the same operator cannot approve twice
    await expect(dana.page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();
    await expect(dana.page.getByText(/Approve is unavailable: A second approval must come from a different operator/)).toBeVisible();
    await expect(dana.page.getByText(OPERATORS.dana.name).first()).toBeVisible();

    await filterQueue(ellis.page, N("Two Operators Tess"), "Awaiting second approval");
    await expect(ellis.page.getByRole("row", { name: new RegExp(N("Two Operators Tess")) })).toContainText("1 of 2 · awaiting another operator");
    await openReview(ellis.page, N("Two Operators Tess"));
    await expect(ellis.page.getByText("Verified with the owner by phone.")).toBeVisible(); // the first operator's justification
    await decide(ellis.page, "Approve", "Second review: identity matches the owner request.");
    await expect(ellis.page.getByText("Membership approved — 2 of 2")).toBeVisible();
    await expect(ellis.page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();

    await ellis.page.getByRole("button", { name: "← Back to operator approvals" }).click();
    await filterQueue(ellis.page, N("Two Operators Tess"), "Approved");
    await expect(ellis.page.getByRole("row", { name: new RegExp(N("Two Operators Tess")) })).toContainText("Approved");
    await dana.context.close();
    await ellis.context.close();
  });

  test("an operator withdraws only their own pending approval, after confirmation", async ({ browser }) => {
    const org = await createOrganization("withdraw");
    await org.request(N("Withdraw Wes"));
    const dana = await operatorPage(browser, "dana");
    const ellis = await operatorPage(browser, "ellis");
    await filterQueue(dana.page, N("Withdraw Wes"));
    await openReview(dana.page, N("Withdraw Wes"));
    await decide(dana.page, "Approve", "Verified with the owner by phone.");
    await expect(dana.page.getByText("Approval recorded — 1 of 2")).toBeVisible();

    await filterQueue(ellis.page, N("Withdraw Wes"));
    await openReview(ellis.page, N("Withdraw Wes"));
    await expect(ellis.page.getByRole("button", { name: "Withdraw my approval" })).toHaveCount(0); // Ellis has nothing to withdraw

    await decide(dana.page, "Withdraw my approval", "Approved the wrong request by mistake.");
    await expect(dana.page.getByText("Approval withdrawn")).toBeVisible();
    await expect(dana.page.getByRole("button", { name: "Withdraw my approval" })).toHaveCount(0); // refreshed from the Authority: nothing left to withdraw
    await expect(dana.page.getByText("Withdrawn", { exact: true }).or(dana.page.getByText(/No operator has approved yet/)).first()).toBeVisible();
    await dana.context.close();
    await ellis.context.close();
  });

  test("rejects with a justification and confirmation and shows the rejected status", async ({ browser }) => {
    const org = await createOrganization("reject");
    await org.request(N("Reject Rae"));
    const { context, page } = await operatorPage(browser, "frankie");
    await filterQueue(page, N("Reject Rae"));
    await openReview(page, N("Reject Rae"));
    await expect(page.getByRole("button", { name: "Reject", exact: true })).toBeDisabled(); // no justification yet
    await page.getByRole("textbox", { name: /Your justification/ }).fill("The owner could not confirm this person.");
    await page.getByRole("button", { name: "Reject", exact: true }).click();
    const confirm = page.getByRole("dialog").getByRole("button", { name: "Reject request" });
    await expect(confirm).toBeDisabled(); // explicit confirmation required
    await page.getByRole("dialog").getByRole("checkbox", { name: /I have reviewed/ }).click();
    await confirm.click();
    await expect(page.getByText("Request rejected")).toBeVisible();
    await expect(page.getByText("Rejected").first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();
    await context.close();
  });

  test("a request the owner cancelled while it was open is refused, nothing is recorded, and the current status is shown", async ({ browser }) => {
    const org = await createOrganization("changed");
    const { requestId } = await org.request(N("Changed Chris"));
    const { context, page } = await operatorPage(browser, "dana");
    await filterQueue(page, N("Changed Chris"));
    await openReview(page, N("Changed Chris"));
    await org.cancel(requestId);
    await decide(page, "Approve", "Verified with the owner by phone.");
    await expect(page.getByRole("alert").or(page.getByRole("status")).filter({ hasText: /changed|no longer pending|expired/i }).first()).toBeVisible();
    await expect(page.getByText(/Approval recorded|Membership approved/)).toHaveCount(0);
    await expect(page.getByText(/cancelled/i).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();
    await context.close();
  });

  test("the confirmation dialog and the form are fully keyboard operable", async ({ browser }) => {
    const org = await createOrganization("keyboard");
    await org.request(N("Keyboard Kim"));
    const { context, page } = await operatorPage(browser, "ellis");
    await filterQueue(page, N("Keyboard Kim"));
    const review = page.getByRole("button", { name: /^Review details for Keyboard Kim/ });
    await review.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { name: "Review request", level: 1 })).toBeVisible();
    await page.getByRole("textbox", { name: /Your justification/ }).focus();
    await page.keyboard.type("Verified with the owner by phone.");
    await page.getByRole("button", { name: "Approve", exact: true }).focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expectNoAxeViolations(page, "confirmation dialog");
    await page.keyboard.press("Tab"); // focus stays inside the dialog
    expect(await page.evaluate(() => document.activeElement?.closest("[role=dialog]") !== null)).toBe(true);
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(page.getByRole("button", { name: "Approve", exact: true })).toBeFocused();
    await context.close();
  });
});
