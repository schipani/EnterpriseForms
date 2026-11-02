import { expect, test, type Page } from "@playwright/test";
import { RUN, createOrganization, expectNoAxeViolations, expectNoHorizontalPageScroll, operatorPage, signInAs, type Org } from "./support";

/**
 * Platform Console owner recovery (PG-075: queue, new case and case details) on the isolated security-test stack: three synthetic operators in separate
 * browser contexts open and approve a recovery case for a fresh single-owner fixture organization (the Authority operator plane's
 * bootstrap in ./support), execute is refused during the waiting period (20 s on this stack) and then succeeds. Synthetic evidence:
 * never three-person approval evidence.
 */
const replacementSubject = (label: string) => `rec-${RUN}-${label}-new-owner`;

async function fillCase(page: Page, org: Org, label: string): Promise<void> {
  await page.goto(`/platform/recovery/new?organizationId=${org.organizationId}`);
  await expect(page.getByRole("heading", { name: "New recovery case", level: 1 })).toBeVisible();
  await expect(page.getByText(org.name, { exact: false }).first()).toBeVisible();
  await expect(page.getByRole("list", { name: "Current owners" }).getByRole("listitem")).toHaveCount(1);
  await page.getByRole("textbox", { name: /Incident reference/ }).fill(`INC-${RUN}-${label}`);
  await page.getByRole("textbox", { name: /Your justification/ }).fill("The only owner left the company; the customer asked for recovery.");
  // The fixture organization has no other member, so the replacement is a new verified identity added to the one selected list.
  await page.getByRole("button", { name: "Propose a new verified identity" }).click();
  await page.getByRole("textbox", { name: "Subject identifier" }).fill(replacementSubject(label));
  await page.getByRole("textbox", { name: "Display name" }).fill(`Replacement ${label} ${RUN}`);
  await page.getByRole("button", { name: "Add identity" }).click();
  await expect(page.getByRole("list", { name: "Selected replacement owners" }).getByRole("listitem")).toHaveCount(1);
  await page.getByRole("combobox", { name: /Verification method \(replacement 1\)/ }).click();
  await page.getByRole("option", { name: "Support call-back" }).click();
  await page.getByRole("textbox", { name: /Verification reference \(replacement 1\)/ }).fill(`TICKET-${RUN}-${label}`);
}

/** Validates, creates the case and returns its id (taken from the details URL). Creating does not approve it. */
async function createCase(page: Page, org: Org, label: string): Promise<string> {
  await fillCase(page, org, label);
  await page.getByRole("button", { name: "Validate" }).click();
  await expect(page.getByText("Check passed — nothing was created")).toBeVisible();
  await page.getByRole("button", { name: "Create case" }).click();
  await page.waitForURL(/\/platform\/recovery\/[0-9a-f-]{36}$/i);
  await expect(page.getByRole("heading", { name: "Recovery case", level: 1 })).toBeVisible();
  const id = /\/platform\/recovery\/([0-9a-f-]{36})$/i.exec(new URL(page.url()).pathname)?.[1];
  expect(id).toBeTruthy();
  await expect(page.getByTestId("approval-progress")).toHaveText("0 of 3");
  return id!;
}

async function decide(page: Page, button: string, confirm: string, justification: string): Promise<void> {
  await page.getByRole("textbox", { name: /Your justification/ }).fill(justification);
  await page.getByRole("button", { name: button, exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("checkbox", { name: /I have reviewed/ }).click();
  await dialog.getByRole("button", { name: confirm, exact: true }).click();
}

test.describe("Owner recovery lifecycle", () => {
  test("three operators approve, execute is refused during the waiting period, then recovery runs and the case shows Recovered", async ({ browser }) => {
    test.setTimeout(240_000);
    const org = await createOrganization("recovery");
    const first = await operatorPage(browser, "dana");
    const second = await operatorPage(browser, "ellis");
    const third = await operatorPage(browser, "frankie");
    const caseId = await createCase(first.page, org, "life");
    await expect(first.page.getByText("Awaiting approvals").first()).toBeVisible();

    // The case is listed in the queue and the organization appears as needing recovery.
    await first.page.goto("/platform/recovery");
    await expect(first.page.getByRole("heading", { name: "Owner recovery", level: 1 })).toBeVisible();
    await first.page.getByRole("searchbox", { name: /Search/ }).fill(org.name);
    await expect(first.page.getByRole("button", { name: new RegExp(`^Review ${org.name} INC-${RUN}-life`) })).toBeVisible();

    await first.page.goto(`/platform/recovery/${caseId}`);
    await decide(first.page, "Approve", "Record my approval", "Customer verified by call-back, approving.");
    await expect(first.page.getByText("Approval recorded — 1 of 3")).toBeVisible();

    // The same operator cannot approve twice: the button is disabled and says why.
    await first.page.getByRole("textbox", { name: /Your justification/ }).fill("Trying to approve a second time.");
    await expect(first.page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();
    await expect(first.page.getByText(/Approve is unavailable: A second approval must come from a different operator/)).toBeVisible();

    await second.page.goto(`/platform/recovery/${caseId}`);
    await expect(second.page.getByRole("heading", { name: "Recovery case", level: 1 })).toBeVisible();
    await decide(second.page, "Approve", "Record my approval", "Second operator checked the evidence.");
    await expect(second.page.getByText("Approval recorded — 2 of 3")).toBeVisible();

    await third.page.goto(`/platform/recovery/${caseId}`);
    await expect(third.page.getByRole("heading", { name: "Recovery case", level: 1 })).toBeVisible();
    await decide(third.page, "Approve", "Record my approval", "Third operator confirms the request.");
    await expect(third.page.getByText("Approval recorded — 3 of 3. Next: the waiting period.")).toBeVisible();

    // During the waiting period the countdown runs and Execute is unavailable with the server's reason.
    await expect(third.page.getByTestId("waiting-remaining")).toBeVisible();
    await third.page.getByRole("textbox", { name: /Your justification/ }).fill("Waiting period ended, executing the recovery.");
    const execute = third.page.getByRole("button", { name: "Execute recovery", exact: true });
    await expect(execute).toBeDisabled();
    await expect(third.page.getByText(/Execute recovery is unavailable: /)).toBeVisible();

    // When the countdown reaches zero the page reloads the case and Execute becomes available.
    await expect(execute).toBeEnabled({ timeout: 90_000 });
    await execute.click();
    const dialog = third.page.getByRole("dialog");
    await dialog.getByRole("checkbox", { name: /I have reviewed/ }).click();
    await dialog.getByRole("button", { name: "Execute recovery", exact: true }).click();
    await expect(third.page.getByText(/Recovery (executed|started)/)).toBeVisible();
    await expect(async () => {
      await third.page.getByRole("button", { name: "Refresh" }).first().click();
      await expect(third.page.getByText(/enabled owners? before/)).toBeVisible({ timeout: 2000 });
    }).toPass({ timeout: 60_000 });

    await first.context.close();
    await second.context.close();
    await third.context.close();
  });
});

test.describe("Deciding a case with the keyboard only", () => {
  test("the justification, the Approve button, the confirmation checkbox and the confirm button are operable without a pointer", async ({ browser }) => {
    const org = await createOrganization("recovery-kbd");
    const creator = await operatorPage(browser, "dana");
    const caseId = await createCase(creator.page, org, "kbd");
    await creator.context.close();

    const { context, page } = await operatorPage(browser, "frankie");
    await page.goto(`/platform/recovery/${caseId}`);
    await expect(page.getByRole("heading", { name: "Recovery case", level: 1 })).toBeFocused();
    await page.getByRole("textbox", { name: /Your justification/ }).focus();
    await page.keyboard.type("Reviewed with the keyboard only, approving.");
    await page.keyboard.press("Tab");
    await expect(page.getByRole("button", { name: "Approve", exact: true })).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.getByRole("checkbox", { name: /I have reviewed/ }).focus();
    await page.keyboard.press("Space");
    await page.getByRole("button", { name: "Record my approval", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByText("Approval recorded — 1 of 3")).toBeVisible();
    await context.close();
  });
});

test.describe("Accessibility", () => {
  test("PG-075 queue, new-case form and details and confirmation dialog pass axe", async ({ browser }) => {
    const org = await createOrganization("recovery-axe");
    const { context, page } = await operatorPage(browser, "dana");
    const caseId = await createCase(page, org, "axe");

    await page.goto("/platform/recovery");
    await expect(page.getByRole("table", { name: "Recovery cases" })).toBeVisible();
    await expectNoAxeViolations(page, "PG-075 queue");

    await page.goto(`/platform/recovery/new?organizationId=${org.organizationId}`);
    await expect(page.getByRole("textbox", { name: /Incident reference/ })).toBeEnabled();
    await expectNoAxeViolations(page, "PG-075 new case");
    await page.getByRole("button", { name: "Create case" }).click(); // field-level errors are shown and still accessible
    await expect(page.getByText("Fix the highlighted fields")).toBeVisible();
    await expectNoAxeViolations(page, "PG-075 new case with validation errors");

    await page.goto(`/platform/recovery/${caseId}`);
    await expect(page.getByRole("heading", { name: "Recovery case", level: 1 })).toBeVisible();
    await expectNoAxeViolations(page, "PG-075 details");
    await page.getByRole("textbox", { name: /Your justification/ }).fill("Reviewing for the accessibility check.");
    await page.getByRole("button", { name: "Approve", exact: true }).click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await expectNoAxeViolations(page, "PG-075 confirmation dialog");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await context.close();
  });
});

/** 320 / 768 / 1440 px and 200% / 400% zoom (equivalent CSS widths 720 and 360): no horizontal page scroll and axe clean on every new page. */
const viewports: Array<[string, number]> = [
  ["320px", 320],
  ["768px", 768],
  ["1440px", 1440],
  ["200% zoom", 720],
  ["400% zoom", 360],
];
for (const [label, width] of viewports) {
  test(`owner recovery pages fit ${label} without horizontal page scroll and pass axe`, async ({ browser }) => {
    const org = await createOrganization(`recovery-vp-${width}`);
    const setup = await operatorPage(browser, "ellis");
    const caseId = await createCase(setup.page, org, `vp${width}`);
    await setup.context.close();

    const context = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();
    await signInAs(page, "ellis");
    for (const route of ["/platform/recovery", `/platform/recovery/new?organizationId=${org.organizationId}`, `/platform/recovery/${caseId}`]) {
      await page.goto(route);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      if (route === "/platform/recovery") {
        if (width < 768) {
          await expect(page.getByRole("list", { name: "Recovery cases" })).toBeVisible();
          await expect(page.getByRole("table")).toHaveCount(0);
        } else {
          await expect(page.getByRole("table", { name: "Recovery cases" })).toBeVisible();
        }
      }
      await expectNoHorizontalPageScroll(page);
      await expectNoAxeViolations(page, `${route} at ${label}`);
    }
    await context.close();
  });
}

test.describe("Member picker", () => {
  test("search, paging, duplicate prevention and choosing three members creates a case bound to the chosen people", async ({ browser }) => {
    test.setTimeout(240_000);
    const org = await createOrganization("recovery-picker", 17); // 17 members: more than one page of 10
    const { context, page } = await operatorPage(browser, "dana");
    await page.goto(`/platform/recovery/new?organizationId=${org.organizationId}`);
    await expect(page.getByRole("heading", { name: "New recovery case", level: 1 })).toBeVisible();
    const list = page.getByRole("list", { name: "Members" });
    const selected = page.getByRole("list", { name: "Selected replacement owners" });
    await expect(list.getByRole("listitem")).toHaveCount(10); // server-side page size
    await expect(page.getByText("Showing 10 of 17 active members.")).toBeVisible();
    await expect(list.getByText("Console E2E Owner")).toHaveCount(0); // the existing owner is never offered
    await page.getByRole("button", { name: "Load more" }).click();
    await expect(list.getByRole("listitem")).toHaveCount(17);
    await expect(page.getByRole("button", { name: "Load more" })).toHaveCount(0);

    // Server-side search narrows the list; an unknown term shows the empty state.
    const search = page.getByRole("searchbox", { name: "Search members" });
    await search.fill("pce-member-03");
    await expect(page.getByText("Showing 1 of 1 active member.")).toBeVisible();
    await search.fill("zz-no-such-member");
    await expect(page.getByText("No members found.")).toBeVisible();
    await search.fill("");
    await expect(list.getByRole("listitem")).toHaveCount(10);

    // Pick three members one after another: each appears in the selected list and the picker excludes it.
    const names: string[] = [];
    for (let n = 1; n <= 3; n++) {
      const add = page.getByRole("button", { name: /^Add pce-member-/ }).first();
      const label = (await add.getAttribute("aria-label")) ?? "";
      names.push(label.replace(/^Add /, ""));
      await add.click();
      await expect(selected.getByRole("listitem")).toHaveCount(n);
      await expect(selected.getByText(names[n - 1]!, { exact: true })).toBeVisible();
      await expect(list.getByText(names[n - 1]!)).toHaveCount(0);
    }
    await expect(page.getByText("Maximum of three replacement owners reached")).toBeVisible();
    await expect(page.getByRole("button", { name: /^Add pce-member-/ }).first()).toBeDisabled();
    await expect(page.getByRole("button", { name: "Propose a new verified identity" })).toBeDisabled();

    // Remove one and pick another.
    await page.getByRole("button", { name: "Remove replacement 2" }).click();
    await expect(selected.getByRole("listitem")).toHaveCount(2);
    await expect(selected.getByText(names[1]!)).toHaveCount(0);
    await page.getByRole("button", { name: /^Add pce-member-/ }).first().click();
    await expect(selected.getByRole("listitem")).toHaveCount(3);

    for (const n of [1, 2, 3]) {
      await page.getByRole("combobox", { name: new RegExp(`Verification method \\(replacement ${n}\\)`) }).click();
      await page.getByRole("option", { name: "Support call-back" }).click();
      await page.getByRole("textbox", { name: new RegExp(`Verification reference \\(replacement ${n}\\)`) }).fill(`TICKET-${RUN}-pick${n}`);
    }
    await page.getByRole("textbox", { name: /Incident reference/ }).fill(`INC-${RUN}-pick`);
    await page.getByRole("textbox", { name: /Your justification/ }).fill("The only owner left; three existing members are confirmed as the new owners.");
    await page.getByRole("button", { name: "Validate" }).click();
    await expect(page.getByText("Check passed — nothing was created")).toBeVisible();
    await page.getByRole("button", { name: "Create case" }).click();
    await page.waitForURL(/\/platform\/recovery\/[0-9a-f-]{36}$/i);
    await expect(page.getByRole("heading", { name: "Recovery case", level: 1 })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Proposed owners" })).toBeVisible();
    const proposedList = page.getByRole("list", { name: "Proposed owners" }).getByRole("listitem");
    await expect(proposedList).toHaveCount(3); // the three chosen members
    await expect(proposedList.filter({ hasText: "Existing member" })).toHaveCount(3);
    await expect(proposedList.filter({ hasText: /pce-member-\d+-/ })).toHaveCount(3); // shown by their verified identity
    await expect(page.getByTestId("approval-progress")).toHaveText("0 of 3");
    await context.close();
  });

  for (const [label, width] of [["320px", 320], ["768px", 768], ["1440px", 1440]] as const) {
    test(`the picker, selected list and new-identity form fit ${label} and pass axe`, async ({ browser }) => {
      const org = await createOrganization(`recovery-picker-${width}`, 6);
      const context = await browser.newContext({ viewport: { width, height: 900 } });
      const page = await context.newPage();
      await signInAs(page, "ellis");
      await page.goto(`/platform/recovery/new?organizationId=${org.organizationId}`);
      await expect(page.getByRole("list", { name: "Members" }).getByRole("listitem")).toHaveCount(6);
      await page.getByRole("button", { name: /^Add pce-member-/ }).first().click();
      await expect(page.getByRole("list", { name: "Selected replacement owners" }).getByRole("listitem")).toHaveCount(1);
      await page.getByRole("button", { name: "Propose a new verified identity" }).click();
      await expect(page.getByRole("button", { name: "Add identity" })).toBeVisible();
      await expectNoHorizontalPageScroll(page);
      await expectNoAxeViolations(page, `member picker, selected list and new-identity form at ${label}`);
      await context.close();
    });
  }
});

test("the browser tab title and navigation entry identify Owner recovery", async ({ browser }) => {
  const { context, page } = await operatorPage(browser, "dana");
  await page.goto("/platform/recovery");
  await expect(page).toHaveTitle(/Owner recovery — Platform Console/);
  await expect(page.getByRole("navigation", { name: "Platform administration" }).getByRole("link", { name: "Owner recovery" })).toHaveAttribute("aria-current", "page");
  await context.close();
});
