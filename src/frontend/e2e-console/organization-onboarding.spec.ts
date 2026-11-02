import { expect, test, type Page } from "@playwright/test";
import { provisionOrganizationKey } from "./helpers/keyCustody";
import { N, RUN, expectNoAxeViolations, expectNoHorizontalPageScroll, operatorPage, signInAs } from "./support";

/**
 * Platform Console organization onboarding (PG-072, PG-073, PG-074) on the isolated security-test stack: two synthetic operators in
 * separate browser contexts propose, approve and initialize an organization through the real BFF and Authority. Synthetic evidence:
 * never two-person approval evidence. The key custody step is the helper in ./helpers/keyCustody.
 */
const ownerSubject = (name: string, n: number) => `onb-${RUN}-${name}-owner-${n}`;

async function fillOwnerWithMouse(page: Page, n: number, subject: string): Promise<void> {
  await page.getByRole("textbox", { name: new RegExp(`Subject identifier \\(owner ${n}\\)`) }).fill(subject);
  await page.getByRole("textbox", { name: new RegExp(`Display name \\(owner ${n}\\)`) }).fill(`Owner ${n} ${RUN}`);
  await page.getByRole("combobox", { name: new RegExp(`Verification method \\(owner ${n}\\)`) }).click();
  await page.getByRole("option", { name: "Support call-back" }).click();
  await page.getByRole("textbox", { name: new RegExp(`Verification reference \\(owner ${n}\\)`) }).fill(`TICKET-${RUN}-${n}`);
}

async function fillRequest(page: Page, organization: string, label: string): Promise<void> {
  await page.getByRole("textbox", { name: /Organization name/ }).fill(organization);
  for (const n of [1, 2, 3]) {
    await fillOwnerWithMouse(page, n, ownerSubject(label, n));
  }
  await page.getByRole("textbox", { name: /Your justification/ }).fill("New customer contract signed and owners verified by call-back.");
}

/** Opens PG-073 from the onboarding queue, files a request and returns its id (taken from the details URL). */
async function createRequest(page: Page, organization: string, label: string): Promise<string> {
  await page.goto("/platform/onboarding");
  await expect(page.getByRole("heading", { name: "Organization onboarding", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: "New organization" }).click();
  await expect(page.getByRole("heading", { name: "New organization", level: 1 })).toBeVisible();
  await expect(page.getByRole("textbox", { name: /Organization name/ })).toBeEnabled();
  await fillRequest(page, organization, label);
  await page.getByRole("button", { name: "Create request" }).click();
  await expect(page.getByRole("heading", { name: "Onboarding request", level: 1 })).toBeVisible();
  const id = /\/platform\/onboarding\/([0-9a-f-]{36})$/i.exec(new URL(page.url()).pathname)?.[1];
  expect(id).toBeTruthy();
  return id!;
}

async function decide(page: Page, button: string, confirm: string, justification: string): Promise<void> {
  await page.getByRole("textbox", { name: /Your justification/ }).fill(justification);
  await page.getByRole("button", { name: button, exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("checkbox", { name: /I have reviewed/ }).click();
  await dialog.getByRole("button", { name: confirm, exact: true }).click();
}

test.describe("Organization onboarding lifecycle", () => {
  test("two operators approve, key custody provisions the key, an operator initializes and the organization appears in the directory", async ({ browser }) => {
    const organization = N("Onboarded Org");
    const first = await operatorPage(browser, "dana");
    const second = await operatorPage(browser, "ellis");
    const requestId = await createRequest(first.page, organization, "life");
    await expect(first.page.getByText("Current step")).toBeVisible();

    await decide(first.page, "Approve", "Record my approval", "Owners verified by call-back, approving.");
    await expect(first.page.getByText("Approval recorded — 1 of 2")).toBeVisible();

    // The same operator cannot approve twice: the button is disabled and says why.
    await first.page.getByRole("textbox", { name: /Your justification/ }).fill("Trying to approve a second time.");
    await expect(first.page.getByRole("button", { name: "Approve", exact: true })).toBeDisabled();
    await expect(first.page.getByText(/Approve is unavailable: A second approval must come from a different operator/)).toBeVisible();

    await second.page.goto(`/platform/onboarding/${requestId}`);
    await expect(second.page.getByRole("heading", { name: "Onboarding request", level: 1 })).toBeVisible();
    await decide(second.page, "Approve", "Record my approval", "Second operator verified the contract.");
    await expect(second.page.getByText("Request approved — 2 of 2. Next: key provisioning.")).toBeVisible();
    await expect(second.page.getByText(/Waiting for key custody/)).toBeVisible();
    await expect(second.page.getByRole("button", { name: "Initialize organization", exact: true })).toBeDisabled();

    await provisionOrganizationKey(requestId);

    await first.page.goto(`/platform/onboarding/${requestId}`);
    await expect(first.page.getByText("Key ready — the organization can be initialized.")).toBeVisible();
    await decide(first.page, "Initialize organization", "Initialize organization", "Key is ready, initializing the organization.");
    await expect(first.page.getByText("Organization initialized — activating")).toBeVisible();
    await expect(async () => {
      await first.page.getByRole("button", { name: "Refresh" }).first().click();
      await expect(first.page.getByText("The organization is active.")).toBeVisible({ timeout: 2000 });
    }).toPass({ timeout: 60_000 });

    await first.page.goto("/platform/organizations");
    await first.page.getByRole("searchbox", { name: /Search/ }).fill(organization);
    await expect(first.page.getByRole("button", { name: `Details for ${organization}` })).toBeVisible();
    await first.page.getByRole("button", { name: `Details for ${organization}` }).click();
    await expect(first.page.getByRole("heading", { name: organization, level: 1 })).toBeVisible();
    await expect(first.page.getByText(/approved by .* and /)).toBeVisible();
    await first.context.close();
    await second.context.close();
  });
});

test.describe("Creating a request with the keyboard only", () => {
  test("every field, the owner method lists and the submit button are reachable and operable without a pointer", async ({ browser }) => {
    const { context, page } = await operatorPage(browser, "frankie");
    await page.goto("/platform/onboarding/new");
    await expect(page.getByRole("heading", { name: "New organization", level: 1 })).toBeFocused();
    await page.getByRole("textbox", { name: /Organization name/ }).focus();
    await page.keyboard.type(N("Keyboard Org"));
    for (const n of [1, 2, 3]) {
      await page.getByRole("textbox", { name: new RegExp(`Subject identifier \\(owner ${n}\\)`) }).focus();
      await page.keyboard.type(ownerSubject("kbd", n));
      await page.keyboard.press("Tab");
      await page.keyboard.type(`Owner ${n} ${RUN}`);
      await page.keyboard.press("Tab"); // the verification method list
      await page.keyboard.press("Space");
      await page.keyboard.press("Enter"); // first option: Support call-back
      await page.keyboard.press("Tab");
      await page.keyboard.type(`TICKET-${RUN}-${n}`);
    }
    await page.getByRole("textbox", { name: /Your justification/ }).focus();
    await page.keyboard.type("Created with the keyboard only, owners verified.");
    await page.keyboard.press("Tab"); // Check
    await page.keyboard.press("Enter");
    await expect(page.getByText("Check passed — nothing was created")).toBeVisible();
    await page.keyboard.press("Tab"); // Create request
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { name: "Onboarding request", level: 1 })).toBeVisible();
    await context.close();
  });
});

test.describe("Accessibility", () => {
  test("PG-072 list, PG-073 form, PG-074 queue, details and the confirmation dialog pass axe", async ({ browser }) => {
    const { context, page } = await operatorPage(browser, "dana");
    await createRequest(page, N("Axe Org"), "axe");

    await page.goto("/platform/organizations");
    await expect(page.getByRole("heading", { name: "Organizations", level: 1 })).toBeVisible();
    await expect(page.getByRole("table", { name: "Organizations" })).toBeVisible();
    await expectNoAxeViolations(page, "PG-072 organizations");

    await page.goto("/platform/onboarding/new");
    await expect(page.getByRole("textbox", { name: /Organization name/ })).toBeEnabled();
    await expectNoAxeViolations(page, "PG-073 new organization");
    await page.getByRole("button", { name: "Create request" }).click(); // field-level errors are shown and still accessible
    await expect(page.getByText("Fix the highlighted fields")).toBeVisible();
    await expectNoAxeViolations(page, "PG-073 with validation errors");

    await page.goto("/platform/onboarding");
    await expect(page.getByRole("table", { name: "Onboarding requests" })).toBeVisible();
    await expectNoAxeViolations(page, "PG-074 queue");
    await page
      .getByRole("button", { name: /^Review / })
      .first()
      .click();
    await expect(page.getByRole("heading", { name: "Onboarding request", level: 1 })).toBeVisible();
    await expectNoAxeViolations(page, "PG-074 details");
    await page.getByRole("textbox", { name: /Your justification/ }).fill("Reviewing for the accessibility check.");
    await page.getByRole("button", { name: "Reject", exact: true }).click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await expectNoAxeViolations(page, "PG-074 confirmation dialog");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await context.close();
  });
});

/** 320 / 768 / 1440 px and 200% / 400% zoom (equivalent CSS widths 720 and 360): no horizontal page scroll on any new page. */
const viewports: Array<[string, number]> = [
  ["320px", 320],
  ["768px", 768],
  ["1440px", 1440],
  ["200% zoom", 720],
  ["400% zoom", 360],
];
for (const [label, width] of viewports) {
  test(`new pages fit ${label} without horizontal page scroll`, async ({ browser }) => {
    const setup = await operatorPage(browser, "ellis");
    const requestId = await createRequest(setup.page, N(`Viewport Org ${width}`), `vp${width}`);
    await setup.context.close();

    const context = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();
    await signInAs(page, "ellis");
    for (const route of ["/platform/organizations", "/platform/onboarding", "/platform/onboarding/new", `/platform/onboarding/${requestId}`]) {
      await page.goto(route);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expectNoHorizontalPageScroll(page);
      if (route === "/platform/organizations" || route === "/platform/onboarding") {
        const list = route === "/platform/organizations" ? "Organizations" : "Onboarding requests";
        if (width < 768) {
          await expect(page.getByRole("list", { name: list })).toBeVisible();
          await expect(page.getByRole("table")).toHaveCount(0);
        } else {
          await expect(page.getByRole("table", { name: list })).toBeVisible();
        }
      }
    }
    await context.close();
  });
}
