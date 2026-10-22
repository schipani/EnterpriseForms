import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut } from "./support";

/**
 * Phase C4 — Record Workspace, through the real application, API and Authorization Authority (no mocks). Forms and
 * records are set up through the business API with the signed-in persona's own session (the same calls the SPA makes);
 * everything asserted is what the UI shows:
 *  - My Records: complete authorized listing, form/status filters, sorting, paging with Previous/Next, row actions that
 *    follow the authority's answer, a share recipient seeing exactly the shared record and no edit action;
 *  - Record Overview: summary and sections;
 *  - Record Editor: unsaved-changes guard, validation summary with focus, save confirmation, a concurrent save resolved
 *    by reapplying the user's own change, a true conflict resolved explicitly, repeating entries with dependent choices.
 */
const run = Date.now().toString(36);

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

/** The persona's own session (cookie + CSRF token), exactly as the SPA uses it. */
async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
    patch: async (path: string, data: unknown, ifMatch?: string) => ok(await page.request.patch(path, { data, headers: { ...headers(), ...(ifMatch ? { "If-Match": ifMatch } : {}) } })),
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

/** Designer creates and publishes a form whose records the clerk may create. */
async function publishForm(api: Api, name: string, clerkId: string, build: (formId: string) => Promise<Json[]>): Promise<string> {
  const created = await api.post("/api/v1/form-definitions/designer", { formDefinitionName: name, formDefinitionDescription: `synthetic Phase C4 ${run}`, formUserUserIds: [clerkId], formUserTeamIds: [] });
  const formId = (created["summary"] as Json)["formDefinitionId"] as string;
  const sections = await build(formId);
  const detail = await api.get(`/api/v1/form-definitions/${formId}`);
  const draft = detail["draft"] as Json | null;
  const saved = await api.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: name, sections }) });
  await api.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "Phase C4 e2e" });
  return formId;
}

async function addField(api: Api, formId: string, key: string, name: string, type: string, options?: string[], groupDefinitionId?: string): Promise<string> {
  const added = await api.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: key, fieldDisplayName: name, fieldDescription: "", fieldDataType: type, options: options?.map((option) => ({ optionKey: option, optionDisplayText: option[0]!.toUpperCase() + option.slice(1) })) ?? null, groupDefinitionId: groupDefinitionId ?? null });
  return added["fieldDefinitionId"] as string;
}

async function createRecord(api: Api, formId: string, values: Record<string, string>, groups: Json[] = []): Promise<string> {
  const record = await api.post(`/api/v1/form-definitions/${formId}/records`, { values, ownerTeamId: null, groups });
  return record["formRecordId"] as string;
}

const reference = (formRecordId: string) => "REC-" + formRecordId.replace(/-/g, "").slice(-8).toUpperCase();

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
}

async function chooseOption(page: Page, scope: ReturnType<Page["locator"]> | Page, label: string, option: string) {
  await scope.getByLabel(label).click();
  await page.getByRole("option", { name: option, exact: true }).click();
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C4 record workspace", () => {
  let formA = "";
  let formB = "";
  let formC = "";
  let recordsA: string[] = [];
  let sharedRecord = "";
  let formBRecord = "";

  test("setup: the designer publishes three forms and the clerk creates records through the API", async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page, "designer");
    const designer = await apiAs(page);
    const clerkId = await userId(designer, /Casey Clerk/);
    formA = await publishForm(designer, `Workspace A ${run}`, clerkId, async (formId) => [{ sectionKey: "main", title: "Main", gridColumns: 4, elements: [field(await addField(designer, formId, "subject_line", "Subject", "String"), "Subject", "text", { required: true, layout: { columnSpan: 4, startOnNewRow: false } })] }]);
    formB = await publishForm(designer, `Workspace B ${run}`, clerkId, async (formId) => [{ sectionKey: "main", title: "Main", gridColumns: 4, elements: [field(await addField(designer, formId, "subject_line", "Subject", "String"), "Subject", "text")] }]);
    formC = await publishForm(designer, `Workspace C ${run}`, clerkId, async (formId) => {
      const subject = await addField(designer, formId, "subject_line", "Subject", "String");
      const priority = await addField(designer, formId, "priority_level", "Priority", "Option", ["low", "high"]);
      const group = await designer.post(`/api/v1/form-definitions/${formId}/groups`, { groupKey: "contacts", groupDisplayName: "Contacts" });
      const groupId = group["groupDefinitionId"] as string;
      const country = await addField(designer, formId, "contact_country", "Country", "Option", ["canada", "usa"], groupId);
      const province = await addField(designer, formId, "contact_province", "Province", "Option", ["ontario", "quebec", "texas"], groupId);
      return [
        { sectionKey: "main", title: "Main", gridColumns: 4, elements: [field(subject, "Subject", "text", { required: true }), field(priority, "Priority", "select")] },
        {
          sectionKey: "contacts",
          title: "Contacts",
          gridColumns: 4,
          repeat: { groupDefinitionId: groupId, minOccurrences: 0, maxOccurrences: 5, allowAdd: true, allowRemove: true, allowReorder: true },
          elements: [field(country, "Country", "select"), field(province, "Province", "select", { optionFilter: { parentFieldDefinitionId: country, parentOptionKeysByOption: { ontario: ["canada"], quebec: ["canada"], texas: ["usa"] } } })],
        },
      ];
    });
    await signOut(page);

    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    recordsA = [];
    for (let index = 1; index <= 12; index++) {
      recordsA.push(await createRecord(clerk, formA, { subject_line: `A record ${String(index).padStart(2, "0")} ${run}` }));
    }
    formBRecord = await createRecord(clerk, formB, { subject_line: `B record ${run}` });
    sharedRecord = recordsA[4]!;
    const readerId = await userId(clerk, /Riley Reader/);
    await clerk.post(`/api/v1/form-records/${sharedRecord}/shares`, { recipientUserId: readerId, recipientTeamId: null, recipientRole: "Reader", fieldKeys: ["subject_line"] });
    await signOut(page);
  });

  test("My Records filters, sorts and pages the complete authorized set; actions follow the authority", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "clerk");
    await page.goto(`/app/records?form=${formA}&size=10&sort=created-asc`);
    await expect(page.getByRole("heading", { level: 1, name: "My Records" })).toBeVisible();
    const table = page.getByRole("region", { name: "My Records" });
    await expect(page.getByText("Showing 1–10 of 12 records")).toBeVisible();
    await expect(table.getByRole("row")).toHaveCount(11); // header + 10
    // Oldest first: the first created record leads; every row is this form.
    await expect(table.getByRole("row").nth(1)).toContainText(reference(recordsA[0]!));
    await expect(table.getByRole("rowheader")).toHaveText(Array(10).fill(new RegExp(`^Workspace A ${run}`)));
    const firstRow = table.getByRole("row").nth(1);
    await expect(firstRow.getByRole("link", { name: `Open Workspace A ${run} ${reference(recordsA[0]!)}` })).toBeVisible();
    await expect(firstRow.getByRole("link", { name: `Edit Workspace A ${run} ${reference(recordsA[0]!)}` })).toBeVisible();
    await expect(firstRow).toContainText("by you");

    await page.getByRole("button", { name: "Next page" }).click();
    await expect(page.getByText("Showing 11–12 of 12 records")).toBeVisible();
    await expect(table.getByRole("row").nth(2)).toContainText(reference(recordsA[11]!));
    await expect(page.getByRole("button", { name: "Next page" })).toBeDisabled();
    await page.getByRole("button", { name: "Previous page" }).click();
    await expect(page.getByText("Showing 1–10 of 12 records")).toBeVisible();

    // Newest first reverses the order across the whole set, not only within a page.
    await page.getByLabel("Sort by").click();
    await page.getByRole("option", { name: "Created (newest first)" }).click();
    await expect(table.getByRole("row").nth(1)).toContainText(reference(recordsA[11]!));

    // A status with no records: an actionable empty state, then back.
    await page.getByLabel("Status").click();
    await page.getByRole("option", { name: "Submitted" }).click();
    await expect(page.getByText("No records match these filters")).toBeVisible();
    await page.getByRole("button", { name: "Clear filters" }).first().click();
    await expect(page.getByLabel("Form")).toContainText("All forms");

    // The form filter offers only forms of the clerk's own records; the New record menu offers forms the clerk may use.
    await page.getByLabel("Form").click();
    await expect(page.getByRole("option", { name: `Workspace B ${run}` })).toBeVisible();
    await page.getByRole("option", { name: `Workspace B ${run}` }).click();
    await expect(page.getByText("Showing 1–1 of 1 record")).toBeVisible();
    await expect(table.getByRole("row").nth(1)).toContainText(reference(formBRecord));
    await page.getByRole("button", { name: "New record" }).click();
    await expect(page.getByRole("menuitem", { name: `Workspace C ${run}` })).toBeVisible();
    await page.keyboard.press("Escape");
    await axeScan(page, "my records (clerk)");
    await signOut(page);

    // The share recipient discovers exactly the shared record, can open it, and is offered no edit action.
    await signIn(page, "reader");
    await page.goto(`/app/records?form=${formA}`);
    await expect(page.getByText("Showing 1–1 of 1 record")).toBeVisible();
    const row = page.getByRole("region", { name: "My Records" }).getByRole("row").nth(1);
    await expect(row).toContainText(reference(sharedRecord));
    await expect(row.getByRole("link", { name: /^Edit / })).toHaveCount(0);
    await page.goto(`/app/records?form=${formB}`);
    await expect(page.getByText("No records match these filters")).toBeVisible();
    await page.goto(`/app/records/${sharedRecord}`);
    await expect(page.getByText("Partial view (shared fields only)")).toBeVisible({ timeout: 60_000 });
    const summary = page.getByRole("region", { name: "Record summary" });
    await expect(summary).toContainText(reference(sharedRecord));
    await expect(summary).toContainText("Shared fields only");
    await expect(page.getByRole("link", { name: "Edit" })).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "Related records" })).toHaveCount(0);
    await signOut(page);
  });

  test("Record Overview and Editor: guard, validation summary, save confirmation, entries with dependent choices", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    const recordId = await createRecord(clerk, formC, { subject_line: `C record ${run}`, priority_level: "low" });
    await page.goto(`/app/records/${recordId}`);
    await expect(page.getByText("Integrity: Verified")).toBeVisible({ timeout: 60_000 });
    const summary = page.getByRole("region", { name: "Record summary" });
    await expect(summary).toContainText(reference(recordId));
    await expect(summary).toContainText("by you");
    await expect(summary).toContainText("Full access");
    await expect(page.getByRole("tab", { name: "Details" })).toHaveAttribute("aria-selected", "true");
    await page.getByRole("tab", { name: "Sharing and access" }).click();
    await expect(page).toHaveURL(/tab=sharing/);
    await page.getByRole("tab", { name: "Details" }).click();

    await page.getByRole("link", { name: "Edit" }).click();
    await expect(page.getByRole("heading", { level: 1, name: `Edit Workspace C ${run}` })).toBeVisible();
    await expect(page.getByText("No unsaved changes.")).toBeVisible();
    await page.getByLabel(/^Subject/).fill(`C record edited ${run}`);
    await expect(page.getByText("You have unsaved changes.")).toBeVisible();

    // Leaving with unsaved changes asks first; staying keeps the edit.
    await page.getByRole("link", { name: "Cancel" }).click();
    const leave = page.getByRole("dialog", { name: "Leave without saving?" });
    await expect(leave).toBeVisible();
    await leave.getByRole("button", { name: "Stay on this page" }).click();
    await expect(leave).toBeHidden();
    await expect(page.getByLabel(/^Subject/)).toHaveValue(`C record edited ${run}`);

    // A required field left empty: a validation summary that moves focus to the field.
    await page.getByLabel(/^Subject/).fill("");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("1 problem needs your attention before saving")).toBeVisible();
    await page.getByRole("button", { name: /Subject is required/ }).click();
    await expect(page.getByLabel(/^Subject/)).toBeFocused();
    await axeScan(page, "record editor with validation summary");

    // A repeating entry with dependent dropdowns: Canada offers its provinces only.
    await page.getByLabel(/^Subject/).fill(`C record edited ${run}`);
    await page.getByRole("button", { name: /^Add Contacts/ }).click();
    const entry = page.getByRole("region", { name: "Contacts, entry 1 of 1" });
    await chooseOption(page, entry, "Country", "Canada");
    await entry.getByLabel("Province").click();
    await expect(page.getByRole("option", { name: "Texas" })).toHaveCount(0);
    await page.getByRole("option", { name: "Ontario", exact: true }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText("Revision 2")).toBeVisible();
    await expect(page.getByText(`C record edited ${run}`)).toBeVisible();
    await expect(page.getByText("Ontario")).toBeVisible();
    await expect(page).not.toHaveURL(/saved=/);
    await signOut(page);
  });

  test("Concurrent saves never overwrite silently: own changes are reapplied, true conflicts are resolved explicitly", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    const recordId = await createRecord(clerk, formC, { subject_line: `Conflict base ${run}`, priority_level: "low" });
    await page.goto(`/app/records/${recordId}/edit`);
    await expect(page.getByLabel(/^Subject/)).toHaveValue(`Conflict base ${run}`);

    // Someone else saves Priority while this editor is open (revision 1 → 2).
    await clerk.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: 1, values: { subject_line: `Conflict base ${run}`, priority_level: "high" }, groups: [] });
    await page.getByLabel(/^Subject/).fill(`Mine ${run}`);
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Someone else saved this record while you were editing")).toBeVisible();
    await page.getByRole("button", { name: "Review and reapply my changes" }).click();
    await expect(page.getByText("Your changes were reapplied to the latest version")).toBeVisible();
    await expect(page.getByText("Your change was carried onto the latest version.")).toBeVisible();
    await expect(page.getByLabel(/^Subject/)).toHaveValue(`Mine ${run}`);
    await expect(page.getByLabel("Priority")).toContainText("High");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Changes saved")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText("Revision 3")).toBeVisible();
    await expect(page.getByText(`Mine ${run}`)).toBeVisible();
    await expect(page.getByText("High", { exact: true })).toBeVisible();

    // Both change Subject: the user must choose; saving is held until they do.
    await page.getByRole("link", { name: "Edit" }).click();
    await clerk.patch(`/api/v1/form-records/${recordId}`, { expectedContentRevision: 3, values: { subject_line: `Theirs ${run}`, priority_level: "high" }, groups: [] });
    await page.getByLabel(/^Subject/).fill(`Mine again ${run}`);
    await page.getByRole("button", { name: "Save changes" }).click();
    await page.getByRole("button", { name: "Review and reapply my changes" }).click();
    await expect(page.getByText("Choose a value for 1 field you both changed")).toBeVisible();
    await expect(page.getByText(`Their saved value: Theirs ${run}`)).toBeVisible();
    await page.getByRole("button", { name: "Use their value" }).click();
    await expect(page.getByLabel(/^Subject/)).toHaveValue(`Theirs ${run}`);
    // Taking their value leaves nothing to save: no empty revision is created.
    await expect(page.getByText("No unsaved changes.")).toBeVisible();
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("There are no changes to save")).toBeVisible();
    await page.getByRole("link", { name: "Cancel" }).click();
    await expect(page.getByText("Revision 4")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText(`Theirs ${run}`)).toBeVisible();
    await signOut(page);
  });
});
