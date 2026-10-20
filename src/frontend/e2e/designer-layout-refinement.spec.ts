import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { addField, formIdFromUrl, openFieldConfig, placeFromPicker, selectTile, signIn, signOut } from "./support";

/**
 * Phase C3 designer UX refinement: a pinned header (one scroll surface, no nested scrollbars), a short quick
 * inspector (label / type / help text / placeholder + Configure Field), and the full General / Validation / Rules /
 * Security & Encryption / Advanced workspace moved into a large Configure Field modal with Apply Changes / Cancel.
 * Other specs (designer-ux, dropdowns-rules, accused-workflow, form-management, repeating-groups,
 * reusable-dropdown-list, pb14-accessibility) exercise the same modal for their own fields and rules; this file
 * checks the refinement itself.
 */
const run = Date.now().toString(36);

async function axeScan(page: Page, label: string, selector?: string) {
  let builder = new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]);
  if (selector) {
    builder = builder.include(selector);
  }
  const results = await builder.analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

async function createForm(page: Page, name: string): Promise<string> {
  await page.goto("/app/forms/new");
  await page.getByLabel("Form name").fill(name);
  await page.getByRole("checkbox", { name: /clerk/i }).check();
  await page.getByRole("button", { name: "Create form" }).click();
  await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
  return formIdFromUrl(page.url());
}

test.describe.serial("Phase C3 designer UX refinement: pinned header, quick inspector, Configure Field modal", () => {
  const formName = `Layout ${run}`;
  let formId = "";

  test("the header stays visible while a tall canvas scrolls, with no nested scrollbar of its own", async ({ page }) => {
    await signIn(page, "designer");
    formId = await createForm(page, formName);
    // Enough rows that the canvas is taller than the viewport.
    for (let i = 1; i <= 12; i++) {
      await addField(page, `Field ${i}`, "String");
      await page.getByRole("tab", { name: "Design" }).click();
      await page.getByRole("button", { name: "Add Row" }).click();
      await page.getByRole("textbox", { name: "Rename section" }).fill(`Section ${i}`);
      await page.keyboard.press("Enter");
      await placeFromPicker(page, "Add field", new RegExp(`^Field ${i} \\(`), { section: `Section ${i}` });
    }
    await page.setViewportSize({ width: 1280, height: 700 });

    // Scrolling from the very top first moves the pinned bar from its natural flow position (below the global app
    // header) up to the top of the viewport, where it then stays — that transition is expected. Pinned means it does
    // not move *again* on further scrolling once it has reached the top.
    const headerHeading = page.getByRole("heading", { level: 1, name: formName });
    await page.mouse.wheel(0, 3000);
    await expect(headerHeading).toBeVisible();
    const stuck = await headerHeading.boundingBox();
    expect(stuck).not.toBeNull();
    await page.mouse.wheel(0, 3000);
    await expect(headerHeading).toBeVisible();
    const after = await headerHeading.boundingBox();
    expect(after).not.toBeNull();
    expect(Math.abs(after!.y - stuck!.y)).toBeLessThanOrEqual(1);
    // The heading sits a little way down the pinned bar (below the "All forms" link and the eyebrow text above it,
    // all of which are pinned too), not at the very top of the viewport — just confirm it is near the top, not
    // scrolled halfway down the page the way it would be without a pinned bar at all.
    expect(stuck!.y).toBeLessThanOrEqual(120);
    // The primary actions (toolbar) pinned alongside it are still reachable without scrolling further.
    await expect(page.getByRole("button", { name: "Save draft" })).toBeInViewport();
    await expect(page.getByRole("button", { name: "Add Row" })).toBeInViewport();

    // One scroll surface: nothing inside the designer workspace has its own overflow/scrollbar.
    const innerScrollers = await page.evaluate(() => {
      const nodes = document.querySelectorAll("main *");
      let count = 0;
      for (const node of nodes) {
        const style = getComputedStyle(node);
        if ((style.overflowY === "auto" || style.overflowY === "scroll") && node.scrollHeight > node.clientHeight + 1) {
          count++;
        }
      }
      return count;
    });
    expect(innerScrollers).toBe(0);
    await page.evaluate(() => window.scrollTo(0, 0));
    // Save so the later tests in this file (fresh pages) find these fields already on the canvas, and so sign-out's
    // navigation is not held up by the "Discard unsaved layout changes?" guard.
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await signOut(page);
  });

  test("the quick inspector shows exactly label, type, help text and placeholder, plus Configure Field; an appropriate empty state otherwise", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    const inspector = page.getByRole("complementary", { name: /Properties$/ });
    await expect(inspector.getByText(/Select a field/)).toBeVisible(); // empty state: nothing selected

    await selectTile(page, "Field 1");
    await expect(inspector.getByRole("heading", { name: "Field Properties" })).toBeVisible();
    await expect(inspector.getByLabel("Field label")).toBeVisible();
    await expect(inspector.getByLabel("Field type")).toBeVisible();
    await expect(inspector.getByLabel("Help text")).toBeVisible();
    await expect(inspector.getByLabel("Placeholder")).toBeVisible();
    await expect(inspector.getByRole("button", { name: "Configure Field" })).toBeVisible();
    // Nothing else: no tabs, no validation/rules/security controls inline any more.
    await expect(inspector.getByRole("tab")).toHaveCount(0);
    await expect(inspector.getByLabel("Field width")).toHaveCount(0);
    await expect(inspector.getByRole("checkbox", { name: "Required" })).toHaveCount(0);
    // Scoped to the panel under test: the canvas tile's own rename button has a pre-existing target-size finding
    // (Phase C1, unrelated to this refinement) that a page-wide scan would otherwise also report here.
    await axeScan(page, "quick inspector with a field selected", '[aria-labelledby="designer-inspector-heading"]');

    await page.getByRole("button", { name: "Close properties" }).click();
    await expect(inspector.getByText(/Select a field/)).toBeVisible();
    await signOut(page);
  });

  test("Configure Field opens every tab; Apply Changes commits to the canvas and quick inspector; Cancel discards", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);

    // Cancel: toggling Required and cancelling leaves the field exactly as it was (no "*", no badge).
    const canvas = page.getByRole("region", { name: "Form canvas" });
    const tile = canvas.getByRole("button", { name: "Field 2", exact: true });
    await expect(tile).not.toContainText("*");
    let modal = await openFieldConfig(page, "Field 2");
    for (const tabName of ["General", "Validation", "Rules", "Security & Encryption", "Advanced"]) {
      await expect(modal.getByRole("tab", { name: tabName })).toBeVisible();
    }
    await modal.getByRole("checkbox", { name: "Required" }).click();
    await expect(modal.getByRole("checkbox", { name: "Required" })).toHaveAttribute("aria-checked", "true");
    await modal.getByRole("button", { name: "Cancel" }).click();
    await expect(modal).toBeHidden();
    await expect(tile).not.toContainText("*"); // Cancel preserved the previous value
    const reopenedAfterCancel = await openFieldConfig(page, "Field 2");
    await expect(reopenedAfterCancel.getByRole("checkbox", { name: "Required" })).toHaveAttribute("aria-checked", "false");
    await reopenedAfterCancel.getByRole("button", { name: "Cancel" }).click();

    // Apply: the same toggle, applied, appears on the canvas tile and survives reopening the quick inspector/modal.
    modal = await openFieldConfig(page, "Field 2");
    await modal.getByRole("checkbox", { name: "Required" }).click();
    await modal.getByRole("button", { name: "Apply Changes" }).click();
    await expect(modal).toBeHidden();
    await expect(tile).toContainText("*");
    const inspector = page.getByRole("complementary", { name: /Properties$/ });
    await expect(inspector.getByLabel("Field label")).toHaveValue("Field 2"); // quick panel reflects the same (still-selected) field
    const reopenedAfterApply = await openFieldConfig(page, "Field 2");
    await expect(reopenedAfterApply.getByRole("checkbox", { name: "Required" })).toHaveAttribute("aria-checked", "true");
    await reopenedAfterApply.getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByText("Unsaved changes")).toBeVisible(); // Apply alone does not save the draft
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Unsaved changes")).toBeHidden();
    await signOut(page);
  });

  test("Escape discards the same way Cancel does, and focus returns to Configure Field", async ({ page }) => {
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    const inspector = await selectTile(page, "Field 3");
    const configureButton = inspector.getByRole("button", { name: "Configure Field" });
    await configureButton.focus();
    await page.keyboard.press("Enter");
    const modal = page.getByRole("dialog", { name: /^Configure /i });
    await expect(modal).toBeVisible();
    await modal.getByRole("checkbox", { name: "Required" }).click();
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();
    await expect(configureButton).toBeFocused(); // WCAG 2.4.3 focus order: back to the control that opened it
    await expect(page.getByRole("region", { name: "Form canvas" }).getByRole("button", { name: "Field 3", exact: true })).not.toContainText("*");
    await signOut(page);
  });

  test("Configure Field and its tabs are reachable and usable by keyboard alone, and pass axe at phone width", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await signIn(page, "designer");
    await page.goto(`/app/forms/${formId}/design`);
    await page.getByRole("tab", { name: "Design" }).click();
    // Canvas is the default pane at phone width; select the tile there, then switch to Properties to reach the inspector.
    await page.getByRole("region", { name: "Form canvas" }).getByRole("button", { name: "Field 4", exact: true }).click();
    await page.getByRole("button", { name: "Properties", exact: true }).click();
    const inspector = page.getByRole("complementary", { name: /Properties$/ });
    await expect(inspector).toBeVisible();
    await inspector.getByRole("button", { name: "Configure Field" }).focus();
    await page.keyboard.press("Enter");
    const modal = page.getByRole("dialog", { name: /^Configure /i });
    await expect(modal).toBeVisible();
    await axeScan(page, "Configure Field modal at phone width");
    // Keyboard-only tab navigation (vertical nav: Up/Down per the WAI-ARIA tabs pattern) and a field inside it.
    await modal.getByRole("tab", { name: "General" }).focus();
    await page.keyboard.press("ArrowDown");
    await expect(modal.getByRole("tab", { name: "Validation" })).toBeFocused();
    await expect(modal.getByLabel("Minimum length")).toBeVisible(); // the panel has swapped before the next key press
    await page.keyboard.press("Tab");
    await expect(modal.getByRole("tabpanel")).toBeFocused(); // the panel itself is tabbable first (WAI-ARIA tabs pattern)
    await page.keyboard.press("Tab");
    await expect(modal.getByLabel("Minimum length")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();
    await signOut(page);
  });
});
