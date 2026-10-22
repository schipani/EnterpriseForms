import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C5 — collaboration and field-scoped contributor writes, through the real application, API and Authorization
 * Authority (no mocks). Contributor writes are OFF by default (D10); the CI browser stack turns them on
 * (AUTHORITY_SHARE_CONTRIBUTOR_WRITES_ENABLED=true). The test reads the switch from the projection and checks the
 * matching experience: with writes on, a contributor changes exactly the shared values, the owner sees the change and the
 * sharing history, and revocation is confirmed; with writes off, nothing offers an edit that cannot happen.
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-c5-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

type Api = Awaited<ReturnType<typeof apiAs>>;

const field = (id: string, label: string, control: string, extra: Json = {}) => ({ kind: "field", fieldDefinitionId: id, control, label, required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: {}, ...extra });

async function userId(api: Api, displayName: RegExp): Promise<string> {
  const users = (await api.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
  const user = users.find((candidate) => displayName.test(candidate.displayName));
  expect(user, `directory user ${displayName}`).toBeTruthy();
  return user!.userId;
}

async function addField(api: Api, formId: string, key: string, name: string, type: string, options?: string[]): Promise<string> {
  const added = await api.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: key, fieldDisplayName: name, fieldDescription: "", fieldDataType: type, options: options?.map((option) => ({ optionKey: option, optionDisplayText: option[0]!.toUpperCase() + option.slice(1) })) ?? null, groupDefinitionId: null });
  return added["fieldDefinitionId"] as string;
}

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C5 collaboration and contributor writes", () => {
  let recordId = "";
  let writesEnabled = false;

  test("setup: the designer publishes a form and the clerk creates a record", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "designer");
    const designer = await apiAs(page);
    const clerkId = await userId(designer, /Casey Clerk/);
    const name = `Collaboration ${run}`;
    const created = await designer.post("/api/v1/form-definitions/designer", { formDefinitionName: name, formDefinitionDescription: `synthetic Phase C5 ${run}`, formUserUserIds: [clerkId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const subject = await addField(designer, formId, "subject_line", "Subject", "String");
    const status = await addField(designer, formId, "case_status", "Status", "Option", ["open", "closed"]);
    const notes = await addField(designer, formId, "private_notes", "Private notes", "String");
    const detail = await designer.get(`/api/v1/form-definitions/${formId}`);
    const draft = detail["draft"] as Json | null;
    const sections = [{ sectionKey: "main", title: "Main", gridColumns: 4, elements: [field(subject, "Subject", "text"), field(status, "Status", "select"), field(notes, "Private notes", "text")] }];
    const saved = await designer.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: name, sections }) });
    await designer.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "Phase C5 e2e" });
    await signOut(page);

    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    const record = await clerk.post(`/api/v1/form-definitions/${formId}/records`, { values: { subject_line: `Original subject ${run}`, case_status: "open", private_notes: `Owner only ${run}` }, ownerTeamId: null, groups: [] });
    recordId = record["formRecordId"] as string;
    await expect(async () => {
      const projection = await clerk.get(`/api/v1/form-records/${recordId}/projection`);
      writesEnabled = (projection["permissions"] as Json)["contributorWritesEnabled"] === true;
    }).toPass({ timeout: 60_000 });
    // CI declares the mode its stack was started in, so a switch that did not reach the Authority fails the run instead of silently testing the other path.
    const expected = process.env["EXPECT_CONTRIBUTOR_WRITES"];
    if (expected !== undefined) {
      expect(writesEnabled, "contributor-write switch reported by the Authority").toBe(expected === "true");
    }
    await signOut(page);
  });

  test("the owner shares two fields with a contributor through the share dialog", async ({ page }) => {
    await signIn(page, "clerk");
    await page.goto(`/app/records/${recordId}`);
    await page.getByRole("tab", { name: "Access" }).click();
    await page.getByRole("button", { name: "Share fields" }).click();
    const dialog = page.getByRole("dialog", { name: "Share fields of this record" });
    await dialog.getByRole("combobox", { name: /Person/ }).click();
    await page.getByRole("option", { name: /Riley Reader/ }).click();
    await dialog.getByRole("combobox", { name: /Role/ }).click();
    await page.getByRole("option", { name: /^Contributor/ }).click();
    if (!writesEnabled) {
      await expect(dialog.getByRole("combobox", { name: /Role/ })).toContainText("changing shared values is not enabled");
    }
    await dialog.getByRole("checkbox", { name: "Subject" }).check();
    await dialog.getByRole("checkbox", { name: "Status" }).check();
    await expect(dialog.getByText(/^View only:/)).toHaveCount(0); // neither field is read-only, restricted, multi-choice or rule-dependent
    await dialog.getByRole("button", { name: "Share", exact: true }).click();
    await expect(dialog).toBeHidden();
    const row = page.getByRole("row", { name: /Riley Reader/ });
    await expect(row).toContainText("Contributor");
    await expect(row).toContainText(writesEnabled ? "Yes" : "No");
    await signOut(page);
  });

  test("the contributor changes exactly the shared values (or is told editing is not enabled)", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "reader");
    await expect(async () => {
      await page.goto(`/app/records/${recordId}`);
      await expect(page.getByText("Partial view (shared fields only)")).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    await expect(page.getByText(`Owner only ${run}`)).toHaveCount(0);

    if (!writesEnabled) {
      await expect(page.getByRole("link", { name: "Edit shared fields" })).toHaveCount(0);
      await page.goto(`/app/records/${recordId}/edit`);
      await expect(page.getByText("Changing shared fields is not enabled. You can still view the fields shared with you.")).toBeVisible();
      await signOut(page);
      return;
    }

    await page.getByRole("link", { name: "Edit shared fields" }).click();
    await expect(page.getByRole("heading", { level: 1, name: /Edit shared fields/ })).toBeVisible();
    await expect(page.getByText("You can change 2 shared fields")).toBeVisible();
    await expect(page.getByLabel("Private notes")).toHaveCount(0);
    await axeScan(page, "edit shared fields");
    await page.getByLabel("Subject").fill(`Changed by contributor ${run}`);
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText(`Changed by contributor ${run}`)).toBeVisible();
    await signOut(page);
  });

  test("the owner sees the change and the sharing history, then revokes with confirmation", async ({ page }) => {
    test.setTimeout(120_000);
    await signIn(page, "clerk");
    await expect(async () => {
      await page.goto(`/app/records/${recordId}`);
      await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 60_000 });
    if (writesEnabled) {
      await expect(page.getByText(`Changed by contributor ${run}`)).toBeVisible();
      await expect(page.getByText(`Owner only ${run}`)).toBeVisible(); // the unshared value is untouched
    }
    await page.getByRole("tab", { name: "Access" }).click();
    const history = page.getByRole("region", { name: "Sharing history" });
    await expect(history.getByText("field-scoped share committed by the authority")).toBeVisible();
    if (writesEnabled) {
      await expect(history.getByText("shared fields changed by a person with shared edit access")).toBeVisible();
    }
    await page.getByRole("row", { name: /Riley Reader/ }).getByRole("button", { name: "Revoke" }).click();
    const revokeDialog = page.getByRole("dialog", { name: "Revoke this share?" });
    await expect(revokeDialog).toContainText("Changes they already saved stay in the record history.");
    await revokeDialog.getByRole("button", { name: "Revoke share" }).click();
    await expect(revokeDialog).toBeHidden();
    await expect(page.getByText("Access revoked for Riley Reader")).toBeVisible();
    await axeScan(page, "sharing and access (owner)");
    await signOut(page);

    await signIn(page, "reader");
    await page.goto(`/app/records/${recordId}/edit`);
    await expect(page.getByText(/Not found|do not have permission|cannot edit/i).first()).toBeVisible();
    await signOut(page);
  });
});
