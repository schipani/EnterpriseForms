import { expect, test, type APIResponse, type Locator, type Page } from "@playwright/test";
import { signIn } from "./support";

/**
 * Consistent action-button icons (Phase C6): labeled actions carry a decorative icon on the left of the visible text, at the
 * size of the button, without changing the accessible name, the default black primary style or keyboard focus.
 * Geometry assertions are used instead of pixel baselines so the check holds across platforms and organization themes.
 */

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

/** The spec makes its own form and record: a fresh stack has neither, and "New record" and "Open" only exist with them. */
async function seed(page: Page) {
  const run = Date.now().toString(36);
  await signIn(page, "owner");
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-icons-${run}-${Math.random().toString(36).slice(2)}` });
  const get = async (path: string) => ok(await page.request.get(path));
  const post = async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() }));
  const put = async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() }));
  const users = (await get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
  const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
  const name = `C6 Icons ${run}`;
  const created = await post("/api/v1/form-definitions/designer", { formDefinitionName: name, formDefinitionDescription: "synthetic icons", formUserUserIds: [ownerId], formUserTeamIds: [] });
  const formId = (created["summary"] as Json)["formDefinitionId"] as string;
  const added = await post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "case_title", fieldDisplayName: "Case title", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null });
  const detail = await get(`/api/v1/form-definitions/${formId}`);
  const draft = detail["draft"] as Json | null;
  const element = { kind: "field", fieldDefinitionId: added["fieldDefinitionId"], control: "text", label: "Case title", required: true, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} };
  const saved = await put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: name, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [element] }] }) });
  await post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "icons" });
  await post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Icons case ${run}` }, ownerTeamId: null, groups: [] });
}

async function iconFacts(button: Locator) {
  return button.evaluate((element) => {
    const svg = element.querySelector("svg");
    const text = Array.from(element.childNodes).find((node) => node.nodeType === Node.TEXT_NODE && (node.textContent ?? "").trim() !== "");
    if (!svg || !text) {
      return null;
    }
    const range = document.createRange();
    range.selectNodeContents(text);
    const icon = svg.getBoundingClientRect();
    const label = range.getBoundingClientRect();
    const style = getComputedStyle(element);
    return { ariaHidden: svg.getAttribute("aria-hidden"), width: Math.round(icon.width), height: Math.round(icon.height), iconRight: icon.right, labelLeft: label.left, gap: Math.round(label.left - icon.right), background: style.backgroundColor, color: style.color };
  });
}

async function focusRing(page: Page, button: Locator) {
  await button.focus();
  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("Tab");
  return button.evaluate((element) => {
    const style = getComputedStyle(element);
    return element === document.activeElement && (style.outlineStyle !== "none" || style.boxShadow !== "none");
  });
}

test.describe("Action button icons", () => {
  test("New record and Search have a left, decorative, consistently sized icon and keep their names, style and focus ring", async ({ page }, info) => {
    test.setTimeout(180_000);
    await seed(page);
    await page.goto("/app/records");

    // Names are the visible text only (the icon is decorative).
    const newRecord = page.getByRole("button", { name: "New record", exact: true });
    const search = page.getByRole("search", { name: "Search records by reference or ID" }).getByRole("button", { name: "Search", exact: true });
    await expect(newRecord).toBeVisible();
    await expect(search).toBeVisible();

    const facts = { newRecord: await iconFacts(newRecord), search: await iconFacts(search) };
    for (const [name, fact] of Object.entries(facts)) {
      expect(fact, `${name} has an icon and a text label`).not.toBeNull();
      expect(fact!.ariaHidden, `${name} icon is decorative`).toBe("true");
      expect(fact!.iconRight, `${name} icon is left of the label`).toBeLessThanOrEqual(fact!.labelLeft);
      expect(fact!.gap, `${name} icon-to-text gap`).toBeGreaterThanOrEqual(4);
      expect(fact!.gap).toBeLessThanOrEqual(12);
    }

    // Same dimensions for both medium primary buttons; the default primary stays black on white (ADR-PB-014).
    expect({ w: facts.newRecord!.width, h: facts.newRecord!.height }).toEqual({ w: facts.search!.width, h: facts.search!.height });
    expect(facts.newRecord!.width).toBeGreaterThanOrEqual(16);
    expect(facts.newRecord!.width).toBeLessThanOrEqual(20);
    expect(facts.search!.background).toBe(facts.newRecord!.background);
    expect(facts.search!.color).toBe(facts.newRecord!.color);

    expect(await focusRing(page, search)).toBe(true);

    // The row of header actions keeps its layout at phone width.
    await page.setViewportSize({ width: 320, height: 720 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await expect(newRecord).toBeVisible();
    await info.attach("my-records-actions-320", { body: await page.screenshot(), contentType: "image/png" });
  });

  test("Simple Cancel buttons and the lifecycle actions follow the convention", async ({ page }) => {
    test.setTimeout(180_000);
    await seed(page);
    await page.goto("/app/records");
    // Clear filters (shown once a filter is active) carries the reset icon.
    await page.getByRole("checkbox", { name: "Include related (child) records" }).check();
    const clear = page.getByRole("button", { name: "Clear filters" }).first();
    await expect(clear).toBeVisible();
    expect(await iconFacts(clear)).not.toBeNull();

    // Open any record that offers lifecycle actions; each header action has an icon, Cancel in a dialog has none.
    await page.getByRole("link", { name: /^Open / }).first().click();
    const submitOrArchive = page.getByRole("button", { name: /^(Submit|Archive|Reopen as draft|Unarchive)$/ }).first();
    await expect(submitOrArchive).toBeVisible();
    expect(await iconFacts(submitOrArchive)).not.toBeNull();
    await submitOrArchive.click();
    const cancel = page.getByRole("dialog").getByRole("button", { name: "Cancel" });
    await expect(cancel).toBeVisible();
    expect(await cancel.locator("svg").count()).toBe(0);
  });
});
