import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Locator, type Page, type Request } from "@playwright/test";
import { signIn } from "./support";

/**
 * Phase C7 M0 Advanced Field Search (C-56): layout, contracts and typed errors, against the real API and BFF session. In the
 * isolated environments these specs run in, the M3 execution gate is on (C-69); a form nobody approved fields for offers
 * nothing, and executed searches are covered by field-search-execution.spec.ts.
 *  1. My Records keeps the reference/ID search on top, Form | Status and Sort by | Rows per page in two columns, then the
 *     Advanced Search section (collapsed) — one card, one results table.
 *  2. Advanced Search is disabled with an explanation; the page never sends a field-search request.
 *  3. Direct API calls cannot bypass the server gate; malformed requests get typed 422s; values never echo or enter a URL.
 *  4. The PG-069 gallery preview (synthetic capabilities, nothing sent) shows the enabled layout: Field | Condition, Value
 *     beneath, typed controls, up to five conditions.
 *  5. With replaced server responses (the real server cannot produce results in M0): a typed failure is never shown as
 *     "no records", and a verified result renders in the same My Records table.
 *  6. Keyboard operation and axe WCAG 2.2 AA.
 */
const sentinel = "Zq7SentinelValueX";
const someForm = "0199a2b3-c4d5-7e6f-8123-456789abcdf9";

const advancedToggle = (page: Page) => page.getByRole("button", { name: /^Advanced Search/ });
const condition = (scope: Page | Locator, n: number) => scope.getByRole("group", { name: `Condition ${n}` });

async function box(locator: Locator) {
  const value = await locator.boundingBox();
  expect(value, "element has a layout box").not.toBeNull();
  return value!;
}

async function choose(page: Page, scope: Locator, label: string, option: string | RegExp) {
  await scope.getByRole("combobox", { name: label }).click();
  await page.getByRole("option", { name: option }).click();
}

function watchFieldSearch(page: Page): Request[] {
  const sent: Request[] = [];
  page.on("request", (request) => {
    if (request.url().includes("/field-search")) sent.push(request);
  });
  return sent;
}

test.describe("Phase C7 M0 Advanced Field Search", () => {
  test("My Records keeps reference search on top, Form | Status and Sort | Rows in two columns, then a collapsed Advanced Search", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto("/app/records");
    const card = page.getByRole("region", { name: "Find and filter records" });
    const searchBox = card.getByRole("textbox", { name: "Search records" });
    const searchButton = card.getByRole("search").getByRole("button", { name: "Search", exact: true });
    const form = card.getByRole("combobox", { name: "Form" });
    const status = card.getByRole("combobox", { name: "Status" });
    const sort = card.getByRole("combobox", { name: "Sort by" });
    const rows = card.getByRole("combobox", { name: "Rows per page" });
    const [s, b, f, st, so, r, adv] = await Promise.all([box(searchBox), box(searchButton), box(form), box(status), box(sort), box(rows), box(advancedToggle(page))]);
    // The Search button is aligned with its input (same top and height, right of it).
    expect(Math.abs(b.y - s.y)).toBeLessThanOrEqual(1);
    expect(Math.abs(b.height - s.height)).toBeLessThanOrEqual(1);
    expect(b.x).toBeGreaterThan(s.x + s.width - 1);
    // Form and Status share a row below the search; Sort and Rows share the next row; Advanced Search follows.
    expect(f.y).toBeGreaterThan(s.y + s.height);
    expect(Math.abs(f.y - st.y)).toBeLessThanOrEqual(1);
    expect(st.x).toBeGreaterThan(f.x + f.width - 1);
    expect(Math.abs(f.height - st.height)).toBeLessThanOrEqual(1);
    expect(so.y).toBeGreaterThan(f.y + f.height);
    expect(Math.abs(so.y - r.y)).toBeLessThanOrEqual(1);
    expect(Math.abs(so.x - f.x)).toBeLessThanOrEqual(1);
    expect(adv.y).toBeGreaterThan(so.y + so.height);
    await expect(advancedToggle(page)).toHaveAttribute("aria-expanded", "false");
    await expect(page.getByRole("table", { name: "My Records" }).or(page.getByText(/^(You have no records yet|No records match these filters)$/))).toHaveCount(1);
    await expect(page.getByRole("table")).toHaveCount(await page.getByRole("table", { name: "My Records" }).count());
  });

  test("without a form, Advanced Search asks for one; a form without approved fields offers nothing, disables it and says why; nothing is ever sent", async ({ page }) => {
    await signIn(page, "owner");
    const sent = watchFieldSearch(page);
    const capabilityUrls: string[] = [];
    page.on("request", (request) => {
      if (request.url().includes("/search-capabilities")) capabilityUrls.push(request.url());
    });
    await page.goto("/app/records");
    await advancedToggle(page).click();
    await expect(advancedToggle(page)).toHaveAttribute("aria-expanded", "true");
    await expect(page.getByText("Choose a form first")).toBeVisible();
    await expect(page.getByRole("button", { name: "Search records" })).toBeDisabled();

    await page.goto(`/app/records?form=${someForm}`);
    await advancedToggle(page).click();
    // Phase C7 M3: execution is on in this isolated environment; a form the person may not search (here: unknown) offers no field.
    await expect(page.getByText("No fields can be searched in this form")).toBeVisible();
    const first = condition(page, 1);
    await expect(first.getByRole("combobox", { name: "Field" })).toBeDisabled();
    await expect(first.getByRole("combobox", { name: "Field" })).toHaveText("No searchable fields");
    await expect(first.getByRole("combobox", { name: "Condition" })).toBeDisabled();
    await expect(first.getByRole("textbox", { name: "Value" })).toBeDisabled();
    await expect(page.getByRole("button", { name: "Add condition" })).toBeDisabled();
    await expect(page.getByRole("button", { name: "Search records" })).toBeDisabled();
    await expect(page.getByRole("group", { name: /^Condition \d$/ })).toHaveCount(1);
    // The capability request carries only the form id; no field-search request was ever made by the page.
    await expect.poll(() => capabilityUrls.length).toBeGreaterThan(0);
    for (const url of capabilityUrls) {
      expect(new URL(url).searchParams.toString()).toBe(`formDefinitionId=${someForm}`);
    }
    expect(sent).toHaveLength(0);
    // The exact reference/ID search still works next to it.
    await page.getByRole("textbox", { name: "Search records" }).fill("REC-00000000");
    await page.getByRole("textbox", { name: "Search records" }).press("Enter");
    await expect(page).toHaveURL(/q=REC-00000000/);
    await expect(page.getByText("No record found", { exact: true })).toBeVisible();
    expect(sent).toHaveLength(0);
  });

  test("direct API calls cannot bypass the server gate; malformed requests get typed errors; values are never echoed", async ({ page }) => {
    await signIn(page, "owner");
    const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
    const headers = { "X-CSRF-Token": session.csrfToken };
    const condition = { fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcde1", operator: "equals", valueType: "String", value: sentinel };

    // Phase C7 M2: every condition is authorized against the Authority's approvals before the (still closed) execution
    // gate, so a field nobody approved is refused as not allowed; an approved condition reaches the 503 gate
    // (FieldSearchApprovalTests on the real stack).
    const refused = await page.request.post("/api/v1/form-records/field-search", { data: { formDefinitionId: someForm, conditions: [condition], pageSize: 25 }, headers });
    expect(refused.status()).toBe(422);
    const problem = (await refused.json()) as Record<string, unknown>;
    expect(problem).toMatchObject({ title: "SEARCH_FIELD_NOT_ALLOWED", retryable: false });
    expect(problem).not.toHaveProperty("items");
    const noConditions = await page.request.post("/api/v1/form-records/field-search", { data: { formDefinitionId: someForm, conditions: [], pageSize: 25 }, headers });
    expect(noConditions.status()).toBeGreaterThanOrEqual(422);
    expect(await noConditions.text()).not.toContain('"items"');

    const tooMany = await page.request.post("/api/v1/form-records/field-search", { data: { formDefinitionId: someForm, conditions: Array(6).fill(condition), pageSize: 25 }, headers });
    expect(tooMany.status()).toBe(422);
    expect(((await tooMany.json()) as { title: string }).title).toBe("SEARCH_REQUEST_INVALID");

    const regex = await page.request.post("/api/v1/form-records/field-search", { data: { formDefinitionId: someForm, conditions: [{ ...condition, operator: "regex" }], pageSize: 25 }, headers });
    expect(regex.status()).toBe(422);
    expect(((await regex.json()) as { title: string }).title).toBe("SEARCH_OPERATOR_NOT_SUPPORTED");

    // A forged page cursor is refused typed (M3: malformed ⇒ 422; altered or foreign ⇒ 409 SEARCH_SNAPSHOT_CHANGED).
    const page2 = await page.request.post("/api/v1/form-records/field-search/page", { data: { cursor: "forged-cursor" }, headers });
    expect([409, 422]).toContain(page2.status());
    const page3 = await page.request.post("/api/v1/form-records/field-search/page", { data: { cursor: "0.forged-cursor" }, headers });
    expect(page3.status()).toBe(409);

    for (const response of [refused, tooMany, regex, page2, page3]) {
      expect(await response.text()).not.toContain(sentinel);
      expect(response.url()).not.toContain(sentinel);
      expect(response.headers()["cache-control"] ?? "").toContain("no-store");
    }

    const capabilities = (await (await page.request.get(`/api/v1/form-records/search-capabilities?formDefinitionId=${someForm}`)).json()) as Record<string, unknown>;
    expect(capabilities).toMatchObject({ fieldValueSearchAvailable: true, fields: [], notSearchableFields: [], maximumConditions: 5, conditionJoin: "And" }); // M3 gate on (isolated environment); nothing approved for an unknown form
  });

  test("gallery preview: Field and Condition side by side, Value beneath, typed controls, up to five AND conditions", async ({ page }) => {
    await signIn(page, "reader");
    const sent = watchFieldSearch(page);
    await page.goto("/app/gallery");
    const preview = page.getByRole("region", { name: "Advanced Search (synthetic preview)" });
    await expect(preview.getByText("Preview only")).toBeVisible();
    await expect(preview.getByText("Encrypted for privacy — not searchable: Case notes (sample).")).toBeVisible();
    const first = condition(preview, 1);
    await choose(page, first, "Field", "Age (sample)");
    await choose(page, first, "Condition", "Between (inclusive)");
    const field = await box(first.getByRole("combobox", { name: "Field" }));
    const op = await box(first.getByRole("combobox", { name: "Condition" }));
    const from = await box(first.getByRole("textbox", { name: "From" }));
    const to = await box(first.getByRole("textbox", { name: "To" }));
    expect(Math.abs(field.y - op.y)).toBeLessThanOrEqual(1);
    expect(op.x).toBeGreaterThan(field.x + field.width - 1);
    expect(from.y).toBeGreaterThan(field.y + field.height);
    expect(Math.abs(from.y - to.y)).toBeLessThanOrEqual(1);
    expect(Math.abs(from.x - field.x)).toBeLessThanOrEqual(1);

    // Validation sits below the control; the controls do not move when it appears.
    await first.getByRole("textbox", { name: "From" }).fill("50");
    await first.getByRole("textbox", { name: "To" }).fill("10");
    await preview.getByRole("button", { name: "Search records" }).click();
    await expect(first.getByRole("alert")).toHaveText("The upper bound must not be before the lower bound.");
    const toAfter = await box(first.getByRole("textbox", { name: "To" }));
    expect(Math.abs(toAfter.y - to.y)).toBeLessThanOrEqual(1);
    const alert = await box(first.getByRole("alert"));
    expect(alert.y).toBeGreaterThan(to.y + to.height - 1);
    await first.getByRole("textbox", { name: "To" }).fill("65");

    const add = preview.getByRole("button", { name: "Add condition" });
    for (let i = 0; i < 4; i++) await add.click();
    await expect(preview.getByRole("group", { name: /^Condition \d$/ })).toHaveCount(5);
    await expect(add).toBeDisabled();
    await expect(preview.getByText("You can use up to 5 conditions.")).toBeVisible();
    await choose(page, condition(preview, 2), "Field", "Date of birth (sample)");
    await expect(condition(preview, 2).getByRole("textbox", { name: "Value" })).toHaveAttribute("type", "date");
    await choose(page, condition(preview, 3), "Field", "Status (sample)");
    await choose(page, condition(preview, 3), "Value", "Active");
    await choose(page, condition(preview, 4), "Field", "In custody (sample)");
    await expect(condition(preview, 4).getByText("No value is needed for this condition.")).toBeVisible();
    await preview.getByRole("button", { name: "Remove condition 5" }).click();
    await condition(preview, 2).getByRole("textbox", { name: "Value" }).fill("1990-01-01");
    await preview.getByRole("button", { name: "Search records" }).click();
    await expect(preview.getByText("Preview: a search with 4 conditions would be sent. Nothing was searched.", { exact: true })).toBeVisible();
    expect(sent).toHaveLength(0);
  });

  test("a typed failure is never shown as no records, and a verified result uses the same My Records table (replaced responses)", async ({ page }) => {
    await signIn(page, "owner");
    const fieldId = "0199a2b3-c4d5-7e6f-8123-456789abcde1";
    await page.context().route("**/api/v1/form-records/search-capabilities**", (route) =>
      route.fulfill({ json: { formDefinitionId: someForm, fieldValueSearchAvailable: true, unavailableReasonCode: null, maximumConditions: 5, conditionJoin: "And", policyRevision: 1, notSearchableFields: [], fields: [{ fieldDefinitionId: fieldId, displayLabel: "Surname", valueType: "String", operators: ["equals"], options: [], minimumTextLength: 3, maximumTextLength: 200 }] } }),
    );
    let reply: "limit" | "result" = "limit";
    const bodies: string[] = [];
    await page.context().route("**/api/v1/form-records/field-search", async (route) => {
      bodies.push(route.request().postData() ?? "");
      if (reply === "limit") {
        await route.fulfill({ status: 422, contentType: "application/problem+json", json: { title: "SEARCH_VERIFICATION_LIMIT", status: 422, detail: "too many records", retryable: false, limit_kind: "records" } });
      } else {
        const now = new Date().toISOString();
        await route.fulfill({ json: { items: [{ formRecordId: "0199a2b3-c4d5-7e6f-8123-4567017ef770", formDefinitionId: someForm, formDefinitionName: "Synthetic form", recordStatus: "Draft", createdOn: now, modifiedOn: now, parentFormRecordId: null, projectionVerified: false, recordReference: "REC-017EF770", createdByDisplayName: null, createdByCurrentUser: true, canEdit: false }], totalCount: 1, pageStart: 1, pageSize: 25, snapshotReference: "s1", authorizationRevision: 9, completeness: "Complete", scopeStatement: "Complete for records of Synthetic form that you can access and whose searched fields you can read.", continuationToken: null, previousToken: null } });
      }
    });
    await page.goto(`/app/records?form=${someForm}`);
    await advancedToggle(page).click();
    const first = condition(page, 1);
    await choose(page, first, "Field", "Surname");
    await first.getByRole("textbox", { name: "Value" }).fill(sentinel);
    await page.getByRole("button", { name: "Search records" }).click();
    await expect(page.getByText("This search is too large to verify")).toBeVisible();
    await expect(page.getByText(/no results are shown/)).toBeVisible();
    await expect(page.getByText(/No records match|No record found|You have no records/)).toHaveCount(0);
    await expect(page.getByRole("table", { name: "My Records" })).toHaveCount(0);
    // The value was sent in the body only, never in the address.
    expect(bodies[0]).toContain(sentinel);
    expect(page.url()).not.toContain(sentinel);

    reply = "result";
    await page.getByRole("button", { name: "Search records" }).click();
    const table = page.getByRole("table", { name: "My Records" });
    await expect(table).toBeVisible();
    await expect(page.getByRole("table")).toHaveCount(1);
    await expect(table.getByRole("rowheader")).toContainText("REC-017EF770");
    await expect(table.getByRole("columnheader", { name: "Actions" })).toBeVisible();
    await expect(page.getByText(/^Complete for records of Synthetic form/)).toBeVisible();
    await page.getByRole("button", { name: "Clear advanced search" }).click();
    await expect(page.getByText(/^Complete for records of Synthetic form/)).toHaveCount(0);
    expect(page.url()).not.toContain(sentinel);
  });

  test("keyboard: the disclosure and conditions are reachable and operable; axe WCAG 2.2 AA passes expanded", async ({ page }) => {
    await signIn(page, "owner");
    await page.goto(`/app/records?form=${someForm}`);
    const toggle = advancedToggle(page);
    await toggle.focus();
    await page.keyboard.press("Enter");
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    await page.keyboard.press("Space");
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    await page.keyboard.press("Enter");
    const outline = await toggle.evaluate((element) => getComputedStyle(element).outlineStyle);
    expect(outline).not.toBe("none");
    const scan = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"]).disableRules(["region"]).analyze();
    expect(scan.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);

    await page.goto("/app/gallery");
    const preview = page.getByRole("region", { name: "Advanced Search (synthetic preview)" });
    const field = condition(preview, 1).getByRole("combobox", { name: "Field" });
    await field.focus();
    await page.keyboard.press("Enter");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(field).not.toHaveText("Choose a field");
    await page.keyboard.press("Tab");
    await expect(condition(preview, 1).getByRole("combobox", { name: "Condition" })).toBeFocused();
    const gallery = await new AxeBuilder({ page }).include("main").withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"]).disableRules(["region"]).analyze();
    expect(gallery.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
  });
});
