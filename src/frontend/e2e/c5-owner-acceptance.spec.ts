import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Browser, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C5 owner acceptance scenario (real application, API and Authorization Authority; no mocks):
 *  1. The owner has a record with two ordinary text fields (Case title, Internal notes — both application-encrypted by
 *     default) and one single-select dropdown (Priority).
 *  2. The owner shares it with the clerk through the share dialog: edit access to Case title and Priority only.
 *  3. The clerk opens the shared record, edits exactly those two fields and saves; Internal notes is never shown,
 *     returned by the API, or changeable (refused like an unknown field).
 *  4. Stale edit: while the clerk edits, the owner saves a change; the clerk's save is refused as a conflict and the
 *     clerk reapplies their change onto the latest version.
 *  5. The owner sees the permitted changes (Internal notes untouched), then revokes; the clerk can no longer open,
 *     read or edit the record.
 * Runs only where contributor writes are enabled (local dev Authority switched on for acceptance; CI browser stack).
 */
const run = Date.now().toString(36);
const secret = `Internal note ${run} — never shared`;

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-c5a-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    raw: (path: string) => page.request.get(path),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
    patch: (path: string, data: unknown) => page.request.patch(path, { data, headers: headers() }),
  };
}

type Api = Awaited<ReturnType<typeof apiAs>>;

async function addField(api: Api, formId: string, key: string, name: string, type: string, options?: string[]): Promise<string> {
  const added = await api.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: key, fieldDisplayName: name, fieldDescription: "", fieldDataType: type, options: options?.map((option) => ({ optionKey: option, optionDisplayText: option[0]!.toUpperCase() + option.slice(1) })) ?? null, groupDefinitionId: null });
  return added["fieldDefinitionId"] as string;
}

const field = (id: string, label: string, control: string) => ({ kind: "field", fieldDefinitionId: id, control, label, required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: {} });

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

async function choose(page: Page, label: string, option: string) {
  await page.getByLabel(label).click();
  await page.getByRole("option", { name: option, exact: true }).click();
}

/** A second, independent signed-in browser context (the owner saving while the clerk is editing). */
async function asOwnerElsewhere(browser: Browser) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, "owner");
  return { context, page, api: await apiAs(page) };
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C5 owner acceptance: field-scoped contributor editing", () => {
  let recordId = "";
  let writesEnabled = false;

  test("setup: the owner publishes the form and creates the record", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "owner");
    const owner = await apiAs(page);
    const users = (await owner.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
    const ownerId = users.find((user) => /Avery Owner/.test(user.displayName))!.userId;
    const name = `C5 Acceptance ${run}`;
    const created = await owner.post("/api/v1/form-definitions/designer", { formDefinitionName: name, formDefinitionDescription: `synthetic Phase C5 acceptance ${run}`, formUserUserIds: [ownerId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const title = await addField(owner, formId, "case_title", "Case title", "String");
    const notes = await addField(owner, formId, "internal_notes", "Internal notes", "String");
    const priority = await addField(owner, formId, "priority_level", "Priority", "Option", ["low", "medium", "high"]);
    const detail = await owner.get(`/api/v1/form-definitions/${formId}`);
    const draft = detail["draft"] as Json | null;
    const saved = await owner.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: name, sections: [{ sectionKey: "main", title: "Case", gridColumns: 4, elements: [field(title, "Case title", "text"), field(notes, "Internal notes", "text"), field(priority, "Priority", "select")] }] }) });
    await owner.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "Phase C5 acceptance" });
    const record = await owner.post(`/api/v1/form-definitions/${formId}/records`, { values: { case_title: `Original title ${run}`, internal_notes: secret, priority_level: "low" }, ownerTeamId: null, groups: [] });
    recordId = record["formRecordId"] as string;
    await expect(async () => {
      const projection = await owner.get(`/api/v1/form-records/${recordId}/projection`);
      writesEnabled = (projection["permissions"] as Json)["contributorWritesEnabled"] === true;
    }).toPass({ timeout: 60_000 });
    const expected = process.env["EXPECT_CONTRIBUTOR_WRITES"];
    if (expected !== undefined) {
      expect(writesEnabled, "contributor-write switch reported by the Authority").toBe(expected === "true");
    }
    await signOut(page);
  });

  test("the owner shares Case title and Priority with the clerk for editing", async ({ page }) => {
    test.skip(!writesEnabled, "contributor writes are disabled on this stack (default)");
    await signIn(page, "owner");
    await page.goto(`/app/records/${recordId}`);
    await expect(page.getByText(secret)).toBeVisible({ timeout: 60_000 });
    await page.getByRole("tab", { name: "Access" }).click();
    await page.getByRole("button", { name: "Share fields" }).click();
    const dialog = page.getByRole("dialog", { name: "Share fields of this record" });
    await dialog.getByRole("combobox", { name: /Person/ }).click();
    await page.getByRole("option", { name: /Casey Clerk/ }).click();
    await dialog.getByRole("combobox", { name: /Role/ }).click();
    await page.getByRole("option", { name: /^Contributor/ }).click();
    await dialog.getByRole("checkbox", { name: "Case title" }).check();
    await dialog.getByRole("checkbox", { name: "Priority" }).check();
    await expect(dialog.getByRole("checkbox", { name: "Internal notes" })).not.toBeChecked();
    await dialog.getByRole("button", { name: "Share", exact: true }).click();
    await expect(dialog).toBeHidden();
    const row = page.getByRole("row", { name: /Casey Clerk/ });
    await expect(row).toContainText("Contributor");
    await expect(row).toContainText("case_title");
    await expect(row).toContainText("priority_level");
    await expect(row).not.toContainText("internal_notes");
    await signOut(page);
  });

  test("the clerk edits exactly the two shared fields; the unshared field is never shown, returned or changeable", async ({ page }) => {
    test.skip(!writesEnabled, "contributor writes are disabled on this stack (default)");
    test.setTimeout(120_000);
    await signIn(page, "clerk");
    await expect(async () => {
      await page.goto(`/app/records/${recordId}`);
      await expect(page.getByText("Partial view (shared fields only)")).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(page.getByText("Internal notes")).toHaveCount(0);
    await expect(page.getByText(secret)).toHaveCount(0);

    // The API never returns the unshared field (name or value), and refuses it exactly like a field that does not exist.
    const clerk = await apiAs(page);
    const projection = await clerk.raw(`/api/v1/form-records/${recordId}/projection`);
    const body = await projection.text();
    expect(body).not.toContain(secret);
    expect(body).not.toContain("internal_notes");
    const revision = ((JSON.parse(body) as Json)["record"] as Json)["contentRevision"];
    const unshared = await clerk.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: revision, values: { internal_notes: "overwritten by clerk" } });
    const unknown = await clerk.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: revision, values: { no_such_field: "x" } });
    expect(unshared.status()).toBe(403);
    expect(unknown.status()).toBe(403);
    const unsharedProblem = (await unshared.json()) as Json;
    const unknownProblem = (await unknown.json()) as Json;
    expect(unsharedProblem["title"]).toBe("FIELD_NOT_EDITABLE");
    expect(unsharedProblem["detail"]).toBe(unknownProblem["detail"]);

    await page.getByRole("link", { name: "Edit shared fields" }).click();
    await expect(page.getByText("You can change 2 shared fields")).toBeVisible();
    await expect(page.getByLabel("Internal notes")).toHaveCount(0);
    await axeScan(page, "clerk: edit shared fields");
    await page.getByLabel("Case title").fill(`Title changed by clerk ${run}`);
    await choose(page, "Priority", "High");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(`Title changed by clerk ${run}`)).toBeVisible();
    await expect(page.getByText(secret)).toHaveCount(0);
    await signOut(page);
  });

  test("stale edit: the owner saves while the clerk edits; the clerk's save is a conflict and the clerk reapplies", async ({ page, browser }) => {
    test.skip(!writesEnabled, "contributor writes are disabled on this stack (default)");
    test.setTimeout(150_000);
    await signIn(page, "clerk");
    await expect(async () => {
      await page.goto(`/app/records/${recordId}/edit`);
      await expect(page.getByText("You can change 2 shared fields")).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await choose(page, "Priority", "Medium");

    // Meanwhile the owner saves a different title in their own session.
    const owner = await asOwnerElsewhere(browser);
    const current = await owner.api.get(`/api/v1/form-records/${recordId}/projection`);
    const ownerSave = await owner.api.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: (current["record"] as Json)["contentRevision"], values: { case_title: `Title changed by owner ${run}` } });
    expect(ownerSave.ok(), await ownerSave.text()).toBe(true);
    await owner.context.close();

    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Someone else saved this record while you were editing")).toBeVisible({ timeout: 30_000 });
    await page.getByRole("button", { name: "Review and reapply my changes" }).click();
    await expect(page.getByText("Your changes were reapplied to the latest version")).toBeVisible();
    await expect(page.getByLabel("Case title")).toHaveValue(`Title changed by owner ${run}`); // their saved value, not overwritten
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 30_000 });
    await signOut(page);
  });

  test("the owner sees the permitted changes, the unshared value is intact, and revocation ends the clerk's access", async ({ page }) => {
    test.skip(!writesEnabled, "contributor writes are disabled on this stack (default)");
    test.setTimeout(120_000);
    await signIn(page, "owner");
    await expect(async () => {
      await page.goto(`/app/records/${recordId}`);
      await expect(page.getByText(`Title changed by owner ${run}`)).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(page.getByText(secret)).toBeVisible(); // Internal notes unchanged
    await expect(page.getByText("Medium")).toBeVisible(); // the clerk's reapplied Priority
    await page.getByRole("tab", { name: "Access" }).click();
    const history = page.getByRole("region", { name: "Sharing history" });
    await expect(history.getByText("shared fields changed by a person with shared edit access").first()).toBeVisible();
    await page.getByRole("row", { name: /Casey Clerk/ }).getByRole("button", { name: "Revoke" }).click();
    const revokeDialog = page.getByRole("dialog", { name: "Revoke this share?" });
    await revokeDialog.getByRole("button", { name: "Revoke share" }).click();
    await expect(page.getByText("Access revoked for Casey Clerk")).toBeVisible();
    await signOut(page);

    await signIn(page, "clerk");
    await page.goto(`/app/records/${recordId}`);
    await expect(page.getByText(/Not found|do not have permission/).first()).toBeVisible();
    await page.goto(`/app/records/${recordId}/edit`);
    await expect(page.getByText(/Not found|do not have permission|cannot edit/i).first()).toBeVisible();
    const clerk = await apiAs(page);
    const after = await clerk.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: 1, values: { case_title: "after revocation" } });
    expect([403, 404]).toContain(after.status());
    await signOut(page);
  });
});
