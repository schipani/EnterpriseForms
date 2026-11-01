import { expect, test } from "@playwright/test";
import { N, createOrganization, expectNoAxeViolations, expectNoHorizontalPageScroll, filterQueue, openReview, operatorPage } from "./support";

/** 320 / 768 / 1440 px, plus 200% and 400% zoom (equivalent CSS widths 720 and 360 at the 1440 px window): no horizontal page scroll, axe clean. */
const widths = [320, 768, 1440];

for (const width of widths) {
  test(`sign-in, queue and review fit ${width}px without horizontal page scroll and pass axe`, async ({ browser }) => {
    const org = await createOrganization(`responsive-${width}`);
    const person = N(`Responsive Rory ${width}`);
    await org.request(person);
    const context = await browser.newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();
    await page.goto("/platform/sign-in");
    await expectNoHorizontalPageScroll(page);
    await expectNoAxeViolations(page, `sign-in ${width}`);
    await page.getByRole("button", { name: /Sign in as Dana/ }).click();
    await expect(page.getByRole("heading", { name: "Operator approvals", level: 1 })).toBeVisible();
    await filterQueue(page, person);
    await expect(page.getByRole("button", { name: /^Review details for/ })).toHaveCount(1);
    await expectNoHorizontalPageScroll(page);
    await expectNoAxeViolations(page, `queue ${width}`);
    await openReview(page, person);
    await expectNoHorizontalPageScroll(page);
    await expectNoAxeViolations(page, `review ${width}`);
    await page.getByRole("textbox", { name: /Your justification/ }).fill("Verified with the owner by phone.");
    await page.getByRole("button", { name: "Approve", exact: true }).click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await expectNoHorizontalPageScroll(page);
    await expectNoAxeViolations(page, `dialog ${width}`);
    await context.close();
  });
}

for (const [label, width] of [["200%", 720], ["400%", 360]] as const) {
  test(`${label} zoom (${width}px equivalent) keeps the queue and review usable`, async ({ browser }) => {
    const org = await createOrganization(`zoom-${width}`);
    const person = N(`Zoom Zed ${width}`);
    await org.request(person);
    const { context, page } = await operatorPage(browser, "ellis");
    await page.setViewportSize({ width, height: 900 });
    await filterQueue(page, person);
    await expectNoHorizontalPageScroll(page);
    await openReview(page, person);
    await expectNoHorizontalPageScroll(page);
    await expect(page.getByRole("button", { name: "Approve", exact: true })).toBeVisible();
    await context.close();
  });
}
