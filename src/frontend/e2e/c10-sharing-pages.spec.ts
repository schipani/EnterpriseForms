import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIResponse, type Page } from "@playwright/test";
import { signIn, signOut, type Persona } from "./support";

/**
 * Phase C10 focused Stage B (PB-14): automated axe checks (WCAG 2.0/2.1/2.2 A and AA) of the five C10 pages through the real application,
 * API and Authorization Authority — the record Access tab (PG-022), share details (PG-023), ownership transfer (PG-053), member record
 * reassignment (PG-053, Security Administrator) and My access (PG-066) — at 320, 768 and 1440 px, and at 640 px and 320 px for a 1280 px
 * window zoomed to 200% and 400% (the convention of dashboard-charts.spec.ts). Each page also must not scroll horizontally. Manual NVDA
 * and VoiceOver review remains a separate obligation (PB14-44…48). Every share command sends the development step-up (G27).
 */
const run = Date.now().toString(36);

const VIEWPORTS = [
  { name: "320-mobile", width: 320, height: 900 },
  { name: "768-tablet", width: 768, height: 1000 },
  { name: "1440-desktop", width: 1440, height: 900 },
  { name: "1280-zoom200", width: 640, height: 900 },
  { name: "1280-zoom400", width: 320, height: 450 },
];

type Json = Record<string, unknown>;

async function ok(response: APIResponse): Promise<Json> {
  expect(response.ok(), `${response.url()} → ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Json;
}

async function apiAs(page: Page) {
  const session = (await (await page.request.get("/api/v1/session")).json()) as { csrfToken: string };
  const headers = () => ({ "X-CSRF-Token": session.csrfToken, "Idempotency-Key": `e2e-c10-${run}-${Math.random().toString(36).slice(2)}` });
  return {
    get: async (path: string) => ok(await page.request.get(path)),
    post: async (path: string, data: unknown) => ok(await page.request.post(path, { data, headers: headers() })),
    put: async (path: string, data: unknown) => ok(await page.request.put(path, { data, headers: headers() })),
  };
}

type Api = Awaited<ReturnType<typeof apiAs>>;

async function userId(api: Api, displayName: RegExp): Promise<string> {
  const users = (await api.get("/api/v1/users")) as unknown as Array<{ userId: string; displayName: string }>;
  const user = users.find((candidate) => displayName.test(candidate.displayName));
  expect(user, `directory user ${displayName}`).toBeTruthy();
  return user!.userId;
}

async function axeScan(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).disableRules(["region"]).analyze();
  expect(results.violations.map((v) => `${label}: ${v.id} (${v.impact}) ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join("; ")}`)).toEqual([]);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `${label}: horizontal scrolling`).toBeLessThanOrEqual(1);
}

test.describe.configure({ mode: "serial" });

test.describe("Phase C10 sharing and ownership pages (automated accessibility)", () => {
  let recordId = "";
  let shareId = "";
  let readerId = "";

  test("setup: a published form, a clerk's record and a share to the reader", async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page, "designer");
    const designer = await apiAs(page);
    const clerkId = await userId(designer, /Casey Clerk/);
    readerId = await userId(designer, /Reader/);
    const name = `C10 pages ${run}`;
    const created = await designer.post("/api/v1/form-definitions/designer", { formDefinitionName: name, formDefinitionDescription: `synthetic C10 ${run}`, formUserUserIds: [clerkId], formUserTeamIds: [] });
    const formId = (created["summary"] as Json)["formDefinitionId"] as string;
    const subject = (await designer.post(`/api/v1/form-definitions/${formId}/fields`, { fieldKey: "subject_line", fieldDisplayName: "Subject", fieldDescription: "", fieldDataType: "String", options: null, groupDefinitionId: null }))["fieldDefinitionId"] as string;
    const detail = await designer.get(`/api/v1/form-definitions/${formId}`);
    const draft = detail["draft"] as Json | null;
    const sections = [{ sectionKey: "main", title: "Main", gridColumns: 4, elements: [{ kind: "field", fieldDefinitionId: subject, control: "text", label: "Subject", required: false, layout: { columnSpan: 4, startOnNewRow: false }, validation: {} }] }];
    const saved = await designer.put(`/api/v1/form-definitions/${formId}/draft`, { expectedDraftRevision: draft ? Number(draft["draftRevision"]) : 0, layoutJson: JSON.stringify({ layoutSchemaVersion: 2, title: name, sections }) });
    await designer.post(`/api/v1/form-definitions/${formId}/publish`, { expectedDraftRevision: Number(saved["draftRevision"]), expectedCurrentVersionNumber: 0, changeReason: "C10 accessibility e2e" });
    await signOut(page);

    await signIn(page, "clerk");
    const clerk = await apiAs(page);
    const record = await clerk.post(`/api/v1/form-definitions/${formId}/records`, { values: { subject_line: `C10 pages ${run}` }, ownerTeamId: null, groups: [] });
    recordId = record["formRecordId"] as string;
    await expect(async () => {
      const share = await clerk.post(`/api/v1/form-records/${recordId}/shares`, { recipientUserId: readerId, recipientTeamId: null, recipientRole: "Reader", fieldKeys: ["subject_line"], useDevelopmentStepUp: true });
      shareId = share["recordShareId"] as string;
    }).toPass({ timeout: 60_000 });
    await signOut(page);
  });

  const pages: { name: string; persona: Persona; path: () => string; ready: RegExp }[] = [
    { name: "PG-022 Access tab", persona: "clerk", path: () => `/app/records/${recordId}?tab=sharing`, ready: /Shares \(\d+ active\)/ },
    { name: "PG-023 Share details", persona: "clerk", path: () => `/app/records/${recordId}/sharing/${shareId}`, ready: /Share|Reader/ },
    { name: "PG-053 Ownership transfer", persona: "clerk", path: () => `/app/records/${recordId}/ownership`, ready: /Transfer ownership/ },
    { name: "PG-053 Member reassignment", persona: "security", path: () => `/app/members/${readerId}/reassign`, ready: /Reassign|records/ },
    { name: "PG-066 My access", persona: "reader", path: () => `/app/me/access`, ready: /My access|access/ },
  ];

  for (const target of pages) {
    test(`${target.name} has no automated accessibility violations at every width and zoom`, async ({ page }) => {
      test.setTimeout(240_000);
      await signIn(page, target.persona);
      for (const viewport of VIEWPORTS) {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await page.goto(target.path());
        await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
        await expect(page.getByText(target.ready).first()).toBeVisible();
        await page.waitForLoadState("networkidle");
        await axeScan(page, `${target.name} @ ${viewport.name}`);
      }

      await signOut(page);
    });
  }
});
