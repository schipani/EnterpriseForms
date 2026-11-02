import AxeBuilder from "@axe-core/playwright";
import { expect, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { createHmac, randomUUID } from "node:crypto";

/** Isolated security-test stack only (compose.security-test.yaml synthetic keys, mirrored by TestIdentity). Never a persistent environment. */
const AUTHORITY = "http://127.0.0.1:5190";
const API = "http://127.0.0.1:5180";
const OPERATOR_KEY = "b3BlcmF0b3ItZGV2LW9ubHktc2hhcmVkLWtleS0wMDAwMDAwMDAwMA==";
const USER_KEY = "dGVzdC1pZHAtZGV2LW9ubHktc2lnbmluZy1rZXktMDAwMDAwMDAwMA==";
const IDP_ISSUER = "https://test-idp.enterpriseforms.invalid";

export const OPERATORS = {
  dana: { subject: "sectest-console-operator-dana", name: "Dana Console Operator (synthetic)" },
  ellis: { subject: "sectest-console-operator-ellis", name: "Ellis Console Operator (synthetic)" },
  frankie: { subject: "sectest-console-operator-frankie", name: "Frankie Console Operator (synthetic)" },
} as const;
/** Unique per run: the isolated stack keeps requests from earlier runs, so names must not repeat across runs. */
export const RUN = randomUUID().slice(0, 5);
export const N = (name: string): string => `${name} ${RUN}`;

export type OperatorKey = keyof typeof OPERATORS;

const b64u = (value: Buffer | string) => Buffer.from(value).toString("base64url");
function jwt(keyBase64: string, claims: Record<string, unknown>): string {
  const now = Math.floor(Date.now() / 1000);
  const header = b64u(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const payload = b64u(JSON.stringify({ iat: now, nbf: now - 5, exp: now + 600, ...claims }));
  return `${header}.${payload}.${b64u(createHmac("sha256", Buffer.from(keyBase64, "base64")).update(`${header}.${payload}`).digest())}`;
}
const operatorToken = () => jwt(OPERATOR_KEY, { iss: "authority-operator", aud: "authorization-authority-operator", sub: "console-e2e", workload_role: "authority-operator", workload_name: "console-e2e" });
const userToken = (organizationId: string, user: { id: string; subject: string; name: string }) => jwt(USER_KEY, { iss: IDP_ISSUER, aud: "enterpriseforms", sub: user.subject, org: organizationId, authority_user_id: user.id, name: user.name });

async function json(url: string, init: RequestInit & { token: string }): Promise<{ status: number; body: Record<string, unknown> }> {
  const { token, ...rest } = init;
  const response = await fetch(url, { ...rest, headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json", "Idempotency-Key": randomUUID().replaceAll("-", ""), ...(rest.headers ?? {}) } });
  const text = await response.text();
  return { status: response.status, body: text ? (JSON.parse(text) as Record<string, unknown>) : {} };
}

/** A fresh synthetic organization (owner/designer persona) on the isolated stack, and a way to file membership requests in it. */
export interface Org {
  organizationId: string;
  name: string;
  request: (displayName: string, role?: string) => Promise<{ requestId: string; subject: string }>;
  cancel: (requestId: string) => Promise<void>;
}

/** `extraMembers` enabled members without roles are bound at creation (names Member 01…), e.g. for the owner recovery member picker. */
export async function createOrganization(label: string, extraMembers = 0): Promise<Org> {
  const organizationId = uuidV7();
  const owner = { id: uuidV7(), subject: `pce-owner-${randomUUID().slice(0, 8)}`, name: "Console E2E Owner" };
  const name = `Console E2E ${label} ${randomUUID().slice(0, 6)}`;
  const created = await json(`${AUTHORITY}/internal/v1/operator/organizations`, {
    method: "POST",
    token: operatorToken(),
    body: JSON.stringify({
      organization_id: organizationId,
      organization_display_name: name,
      identities: [
        { authority_user_id: owner.id, issuer_uri: IDP_ISSUER, subject_identifier: owner.subject, organization_roles: ["organization_owner", "designer"] },
        ...Array.from({ length: extraMembers }, (_, i) => ({ authority_user_id: uuidV7(), issuer_uri: IDP_ISSUER, subject_identifier: `pce-member-${String(i + 1).padStart(2, "0")}-${randomUUID().slice(0, 6)}`, organization_roles: [] as string[] })),
      ],
    }),
  });
  if (created.status >= 300) {
    throw new Error(`organization bootstrap failed: ${created.status} ${JSON.stringify(created.body)}`);
  }
  for (let attempt = 0; attempt < 100; attempt++) {
    const gate = await json(`${AUTHORITY}/internal/v1/operator/organizations/${organizationId}/gate`, { token: operatorToken() });
    if (gate.body["projection_gate_state"] === "Ready") break;
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
  await json(`${API}/api/v1/users/me`, { token: userToken(organizationId, owner) });
  const owner_ = () => userToken(organizationId, owner);
  return {
    organizationId,
    name,
    async request(displayName, role = "Designer") {
      const subject = `pce-person-${randomUUID().slice(0, 10)}`;
      const response = await json(`${API}/api/v1/organization/membership-requests`, {
        method: "POST",
        token: owner_(),
        body: JSON.stringify({ subjectIdentifier: subject, displayNameHint: displayName, initialRoleName: role, reason: "joins the intake team", useDevelopmentStepUp: true }),
      });
      if (response.status >= 300) {
        throw new Error(`membership request failed: ${response.status} ${JSON.stringify(response.body)}`);
      }
      return { requestId: response.body["requestId"] as string, subject };
    },
    async cancel(requestId) {
      const list = await json(`${API}/api/v1/organization/membership-requests?pageSize=50`, { token: owner_() });
      const entry = (list.body["items"] as Array<Record<string, unknown>>).find((r) => r["requestId"] === requestId);
      const response = await json(`${API}/api/v1/organization/membership-requests/${requestId}/cancel`, {
        method: "POST",
        token: owner_(),
        body: JSON.stringify({ expectedRequestDigest: entry?.["requestDigest"], reason: "entered for the wrong unit", useDevelopmentStepUp: true }),
      });
      if (response.status >= 300) {
        throw new Error(`cancel failed: ${response.status} ${JSON.stringify(response.body)}`);
      }
    },
  };
}

function uuidV7(): string {
  const bytes = Buffer.from(randomUUID().replaceAll("-", ""), "hex");
  bytes.writeUIntBE(Date.now(), 0, 6);
  bytes[6] = (bytes[6]! & 0x0f) | 0x70;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = bytes.toString("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Each operator is its own browser context, so sessions and cookies are separate (two people, two browsers). */
export async function operatorPage(browser: Browser, operator: OperatorKey): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signInAs(page, operator);
  return { context, page };
}

export async function signInAs(page: Page, operator: OperatorKey): Promise<void> {
  await page.goto("/platform/sign-in");
  await page.getByRole("button", { name: `Sign in as ${OPERATORS[operator].name}` }).click();
  await expect(page.getByRole("heading", { name: "Operator approvals", level: 1 })).toBeVisible();
}

export async function filterQueue(page: Page, search: string, status = "All"): Promise<void> {
  await page.getByRole("combobox", { name: /Status/ }).click();
  await page.getByRole("option", { name: status, exact: true }).click();
  await page.getByRole("searchbox", { name: /Search/ }).fill(search);
}

export async function openReview(page: Page, displayName: string): Promise<void> {
  await page.getByRole("button", { name: new RegExp(`^Review details for ${displayName}`) }).click();
  await expect(page.getByRole("heading", { name: "Review request", level: 1 })).toBeVisible();
  await expect(page.getByTestId("fingerprint")).toBeVisible();
}

export async function decide(page: Page, kind: "Approve" | "Reject" | "Withdraw my approval", justification: string): Promise<void> {
  await page.getByRole("textbox", { name: /Your justification/ }).fill(justification);
  await page.getByRole("button", { name: kind, exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("checkbox", { name: /I have reviewed/ }).click();
  const confirm = { Approve: "Record my approval", Reject: "Reject request", "Withdraw my approval": "Withdraw my approval" }[kind];
  await dialog.getByRole("button", { name: confirm, exact: true }).click();
}

export async function expectNoAxeViolations(page: Page, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`), `axe violations on ${label}`).toEqual([]);
}

export async function expectNoHorizontalPageScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, "horizontal page scroll").toBeLessThanOrEqual(1);
}
