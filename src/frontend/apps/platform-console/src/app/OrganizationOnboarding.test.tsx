import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DirectoryItem, OnboardingDecisionResponse, OnboardingDetail, OnboardingItem } from "../api/client";
import { setCsrfToken } from "../api/client";
import { App } from "./App";

const ORG = "01a16640-87f6-7488-a1f1-000000000001";
const REQUEST = "01a194c3-cbb0-70ef-8e74-31d8e5d30001";
const SHA = "a".repeat(16) + "b".repeat(48);
const ISSUER = "https://test-idp.enterpriseforms.invalid";

interface Call {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
  csrf: string | null;
}
type Handler = (call: Call) => { status: number; body?: unknown } | undefined;

const sessionBody = () => ({
  platformOperatorId: "01a194d8-7599-75e4-936d-94caa17075d7",
  displayName: "Morgan Operator (synthetic)",
  issuerUri: "https://platform-operators.dev.enterpriseforms.invalid/",
  subjectIdentifier: "dev-platform-operator-morgan",
  operatorStatus: "Active",
  permissions: [],
  activeOperatorCount: 3,
  authenticationMode: "SyntheticDevelopment",
  evidenceAssurance: "SyntheticDevelopment",
  environmentNotice: "Development operators.",
  csrfToken: "csrf-token-1",
  idleExpiresOn: new Date(Date.now() + 15 * 60_000).toISOString(),
  absoluteExpiresOn: new Date(Date.now() + 120 * 60_000).toISOString(),
});

function problem(status: number, errorCode: string) {
  return { status, body: { type: "x", title: errorCode, status, detail: "refused", errorCode, retryable: false, correlationId: "01a194c3-cbb0-70ef-8e74-0000000000bb" } };
}

function installFetch(handler: Handler) {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = new URL(input, "http://127.0.0.1:8095");
      const headers = (init?.headers ?? {}) as Record<string, string>;
      const call: Call = {
        method: init?.method ?? "GET",
        path: url.pathname.replace("/console-api/v1", "") + url.search,
        body: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null,
        csrf: headers["X-CSRF-Token"] ?? null,
      };
      calls.push(call);
      const answer = (call.path === "/session" ? { status: 200, body: sessionBody() } : handler(call)) ?? { status: 404, body: { errorCode: "NOT_FOUND" } };
      return new Response(JSON.stringify(answer.body ?? {}), { status: answer.status, headers: { "Content-Type": "application/json" } });
    }),
  );
  return calls;
}

const dirItem = (n: number, over: Partial<DirectoryItem> = {}): DirectoryItem => ({
  organizationId: `01a16640-87f6-7488-a1f1-${String(n).padStart(12, "0")}`,
  organizationDisplayName: `Org ${n}`,
  lifecycle: "Active",
  enabledOwnerCount: 3,
  securityAdministratorCount: 1,
  memberCount: 4,
  needsOwners: false,
  onboardingRequestId: null,
  ...over,
});
const dirPage = (items: DirectoryItem[], over: Record<string, unknown> = {}) => ({ items, totalCount: items.length, nextCursor: null, limit: 15, filter: "all", correlationId: "x", ...over });

const queueItem = (n: number, over: Partial<OnboardingItem> = {}): OnboardingItem => ({
  requestId: `01a194c3-cbb0-70ef-8e74-31d8e5d3${String(n).padStart(4, "0")}`,
  organizationId: ORG,
  organizationDisplayName: `Onboard ${n}`,
  ownerCount: 3,
  state: "Pending",
  displayState: "AwaitingApproval",
  createdOn: "2026-10-30T12:00:00Z",
  expiresOn: "2099-11-06T12:00:00Z",
  approvalsRecorded: 0,
  approvalsRequired: 2,
  approvedByMe: false,
  createdByOperatorDisplayName: "Quinn Operator",
  ...over,
});
const queuePage = (items: OnboardingItem[], over: Record<string, unknown> = {}) => ({
  items,
  totalCount: items.length,
  nextCursor: null,
  limit: 15,
  status: "all",
  approvedIdentityIssuers: [ISSUER],
  approvalAssurance: "SyntheticDevelopment",
  correlationId: "x",
  ...over,
});

const detailOf = (over: Partial<OnboardingDetail> = {}): OnboardingDetail => ({
  requestId: REQUEST,
  organizationId: ORG,
  organizationDisplayName: "Harbour Legal",
  identityIssuerUri: ISSUER,
  state: "Pending",
  displayState: "AwaitingApproval",
  justification: "New customer contract signed.",
  createdByPlatformOperatorId: "01a194d8-7599-75e4-936d-000000000002",
  createdByOperatorDisplayName: "Quinn Operator",
  createdOn: "2026-10-30T12:00:00Z",
  expiresOn: "2099-11-06T12:00:00Z",
  approvedOn: null,
  keyCustodyExpiresOn: null,
  initializedOn: null,
  closedOn: null,
  closureReason: null,
  closureJustification: null,
  lastInitializationFailureCode: null,
  lastInitializationFailureOn: null,
  owners: [
    {
      authorityUserId: "01a194d8-0000-7000-8000-000000000001",
      issuerUri: ISSUER,
      subjectIdentifier: "owner-a",
      displayNameHint: "Owner A",
      verificationMethod: "SupportCallback",
      verificationReference: "TICKET-1",
      memberOfOtherOrganizations: true,
    },
    {
      authorityUserId: "01a194d8-0000-7000-8000-000000000002",
      issuerUri: ISSUER,
      subjectIdentifier: "owner-b",
      displayNameHint: "Owner B",
      verificationMethod: "ContractSignatory",
      verificationReference: "TICKET-2",
      memberOfOtherOrganizations: false,
    },
  ],
  approvals: [],
  approvalsRecorded: 0,
  approvalsRequired: 2,
  statementJson: '{"statement_version":1}',
  statementSha256: SHA,
  keyCustody: { keyName: "org-key-harbour", status: "NotRequired", detail: null },
  activation: null,
  myEligibility: {
    canApprove: true,
    canWithdraw: false,
    canReject: true,
    canCancel: false,
    canInitialize: false,
    approveRefusalCode: null,
    approveRefusalDetail: null,
    initializeRefusalCode: null,
    initializeRefusalDetail: null,
  },
  rowRevision: 1,
  approvalAssurance: "SyntheticDevelopment (development-only operator keys)",
  correlationId: "01a194c3-cbb0-70ef-8e74-0000000000aa",
  ...over,
});

const decision = (over: Partial<OnboardingDecisionResponse>): OnboardingDecisionResponse => ({
  dryRun: false,
  eligible: true,
  refusalCode: null,
  refusalDetail: null,
  requestId: REQUEST,
  organizationId: ORG,
  decision: "approve",
  statementJson: "{}",
  statementSha256: SHA,
  outcome: "AwaitingSecondApproval",
  approvalsRecorded: 1,
  approvalsRequired: 2,
  state: "Pending",
  displayState: "AwaitingApproval",
  securityCommandId: null,
  archiveState: null,
  replayed: false,
  approvalAssurance: "x",
  correlationId: "x",
  ...over,
});

beforeEach(() => {
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => undefined;
  Element.prototype.releasePointerCapture = () => undefined;
  Element.prototype.scrollIntoView = () => undefined;
  setCsrfToken("");
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("PG-072 Organizations", () => {
  it("lists organizations with owner coverage, filters to needs owners and pages with the server cursors", async () => {
    window.history.replaceState(null, "", "/platform/organizations");
    const first = Array.from({ length: 15 }, (_, i) => dirItem(i + 1, i === 0 ? { enabledOwnerCount: 1, needsOwners: true } : i === 1 ? { enabledOwnerCount: 2 } : {}));
    const calls = installFetch((c) => {
      if (!c.path.startsWith("/organizations?")) return undefined;
      const params = new URL("http://x" + c.path).searchParams;
      if (params.get("cursor") === "cursor-2") return { status: 200, body: dirPage([dirItem(16), dirItem(17)], { totalCount: 17 }) };
      if (params.get("filter") === "needs_owners") return { status: 200, body: dirPage([dirItem(40, { enabledOwnerCount: 0, needsOwners: true })], { filter: "needs_owners" }) };
      return { status: 200, body: dirPage(first, { totalCount: 17, nextCursor: "cursor-2" }) };
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Organizations" })).toBeInTheDocument();
    expect(screen.getByText(/no records, documents, teams or shares/)).toBeInTheDocument();
    expect(await screen.findByText("Org 15")).toBeInTheDocument();
    expect(screen.getByText("Fewer than two owners")).toBeInTheDocument();
    expect(screen.getByText("Two owners — three recommended")).toBeInTheDocument();
    expect(screen.getByText(/Showing 1–15 of 17 organizations/)).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/organizations?")).every((c) => c.path.includes("limit=15"))).toBe(true);

    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(await screen.findByText("Org 17")).toBeInTheDocument();
    expect(screen.getByText(/Showing 16–17 of 17 organizations/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Previous" }));
    expect(await screen.findByText("Org 1")).toBeInTheDocument();

    await user.click(screen.getByRole("combobox", { name: /Show/ }));
    await user.click(await screen.findByRole("option", { name: "Needs owners" }));
    expect(await screen.findByText("Org 40")).toBeInTheDocument();
    expect(calls.some((c) => c.path.includes("filter=needs_owners"))).toBe(true);
  });

  it("shows details with owners, provenance and a technical-details disclosure", async () => {
    const summary = dirItem(7, { onboardingRequestId: REQUEST, enabledOwnerCount: 1, needsOwners: true });
    window.history.replaceState(null, "", `/platform/organizations/${summary.organizationId}`);
    installFetch((c) =>
      c.path === `/organizations/${summary.organizationId}`
        ? {
            status: 200,
            body: {
              summary,
              authorizationRevision: 12,
              projectionAppliedRevision: 12,
              owners: [
                { authorityUserId: "01a194d8-0000-7000-8000-000000000001", issuerUri: ISSUER, subjectIdentifier: "owner-a", enabled: true },
                { authorityUserId: "01a194d8-0000-7000-8000-000000000002", issuerUri: ISSUER, subjectIdentifier: "owner-b", enabled: false },
              ],
              onboardingApprovedBy: ["Quinn Operator", "Rowan Operator"],
              correlationId: "x",
            },
          }
        : undefined,
    );
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Org 7" })).toBeInTheDocument();
    expect(screen.getByText(/Created through Platform Console onboarding, approved by Quinn Operator and Rowan Operator/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "View onboarding request" })).toBeInTheDocument();
    expect(screen.getByText("owner-a")).toBeInTheDocument();
    expect(screen.getByText("Disabled")).toBeInTheDocument();
    expect(screen.getByText("Owner recovery is not available yet.")).toBeInTheDocument();
    expect(screen.getByText("Technical details")).toBeInTheDocument();
  });
});

describe("PG-073 New organization", () => {
  const fillOwners = async (user: ReturnType<typeof userEvent.setup>, count: number) => {
    for (let n = 1; n <= count; n++) {
      await user.type(screen.getByRole("textbox", { name: new RegExp(`Subject identifier \\(owner ${n}\\)`) }), `subject-${n}`);
      await user.type(screen.getByRole("textbox", { name: new RegExp(`Display name \\(owner ${n}\\)`) }), `Owner ${n}`);
      await user.click(screen.getByRole("combobox", { name: new RegExp(`Verification method \\(owner ${n}\\)`) }));
      await user.click(await screen.findByRole("option", { name: "Support call-back" }));
      await user.type(screen.getByRole("textbox", { name: new RegExp(`Verification reference \\(owner ${n}\\)`) }), `TICKET-${n}`);
    }
  };

  it("says onboarding is unavailable and disables the form when no issuer is approved", async () => {
    window.history.replaceState(null, "", "/platform/onboarding/new");
    installFetch((c) => (c.path.startsWith("/organization-onboarding?") ? { status: 200, body: queuePage([], { approvedIdentityIssuers: [] }) } : undefined));
    render(<App />);
    expect(await screen.findByText("Organization onboarding is not available in this environment")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /Organization name/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Create request" })).toBeDisabled();
  });

  it("starts with three owners, enforces two as the minimum, validates and shows the three-owner warning", async () => {
    window.history.replaceState(null, "", "/platform/onboarding/new");
    const calls = installFetch((c) => (c.path.startsWith("/organization-onboarding?") ? { status: 200, body: queuePage([]) } : undefined));
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByRole("heading", { name: "New organization" })).toBeInTheDocument();
    await screen.findByRole("textbox", { name: /Organization name/ });
    expect(screen.getAllByRole("group", { name: /^Owner \d$/ })).toHaveLength(3);
    expect(screen.queryByText("Fewer than 3 owners")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Remove owner 3" }));
    expect(screen.getAllByRole("group", { name: /^Owner \d$/ })).toHaveLength(2);
    expect(screen.getByText("Fewer than 3 owners")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Remove owner/ })).not.toBeInTheDocument(); // two is the minimum

    await user.click(screen.getByRole("button", { name: "Create request" }));
    expect(await screen.findByText("Fix the highlighted fields")).toBeInTheDocument();
    expect(screen.getAllByText("Enter the subject identifier.").length).toBe(2);
    expect(calls.some((c) => c.method === "POST")).toBe(false);

    await user.type(screen.getByRole("textbox", { name: /Subject identifier \(owner 1\)/ }), "someone@example.org");
    expect(screen.getByText("Enter the stable subject, never an email address.")).toBeInTheDocument();
  });

  it("checks with a dry run, shows warnings, then creates the request and opens its details", async () => {
    window.history.replaceState(null, "", "/platform/onboarding/new");
    const created = detailOf();
    const calls = installFetch((c) => {
      if (c.path.startsWith("/organization-onboarding?")) return { status: 200, body: queuePage([]) };
      if (c.path === "/organization-onboarding" && c.method === "POST") {
        return c.body?.["dryRun"] === true
          ? {
              status: 200,
              body: { dryRun: true, eligible: true, refusalCode: null, refusalDetail: null, warnings: ["Fewer than three owners were entered."], request: null, replayed: false, correlationId: "x" },
            }
          : { status: 201, body: { dryRun: false, eligible: true, refusalCode: null, refusalDetail: null, warnings: [], request: created, replayed: false, correlationId: "x" } };
      }
      if (c.path === `/organization-onboarding/${REQUEST}`) return { status: 200, body: created };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("textbox", { name: /Organization name/ });
    await user.click(screen.getByRole("button", { name: "Remove owner 3" }));
    await user.type(screen.getByRole("textbox", { name: /Organization name/ }), "Harbour Legal");
    await fillOwners(user, 2);
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "New customer contract signed.");
    await user.click(screen.getByRole("button", { name: "Check" }));
    expect(await screen.findByText("Check passed — nothing was created")).toBeInTheDocument();
    expect(screen.getByText("Fewer than three owners were entered.")).toBeInTheDocument();
    const dry = calls.find((c) => c.method === "POST")!;
    expect(dry.body).toMatchObject({ dryRun: true, organizationDisplayName: "Harbour Legal", justification: "New customer contract signed." });
    expect((dry.body!["initialOwners"] as unknown[]).length).toBe(2);
    expect(dry.csrf).toBe("csrf-token-1");

    await user.click(screen.getByRole("button", { name: "Create request" }));
    expect(await screen.findByRole("heading", { name: "Onboarding request" })).toBeInTheDocument();
    expect(window.location.pathname).toBe(`/platform/onboarding/${REQUEST}`);
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts[1]!.body).toMatchObject({ dryRun: false });
    expect(String(posts[1]!.body!["idempotencyKey"])).toMatch(/^onb-/);
    expect(posts[1]!.body!["idempotencyKey"]).toBe(posts[0]!.body!["idempotencyKey"]); // unchanged form: same key
  }, 30_000);

  it("shows a dry-run refusal inline", async () => {
    window.history.replaceState(null, "", "/platform/onboarding/new");
    installFetch((c) => {
      if (c.path.startsWith("/organization-onboarding?")) return { status: 200, body: queuePage([]) };
      if (c.method === "POST")
        return {
          status: 200,
          body: { dryRun: true, eligible: false, refusalCode: "ORGANIZATION_NAME_CONFLICT", refusalDetail: null, warnings: [], request: null, replayed: false, correlationId: "x" },
        };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await screen.findByRole("textbox", { name: /Organization name/ });
    await user.type(screen.getByRole("textbox", { name: /Organization name/ }), "Harbour Legal");
    await fillOwners(user, 3);
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "New customer contract signed.");
    await user.click(screen.getByRole("button", { name: "Check" }));
    expect(await screen.findByText("Organization name already in use")).toBeInTheDocument();
  }, 30_000);
});

describe("PG-074 Onboarding queue", () => {
  it("lists requests with progress chips, filters and pages", async () => {
    window.history.replaceState(null, "", "/platform/onboarding");
    const first = Array.from({ length: 15 }, (_, i) =>
      queueItem(i + 1, i === 0 ? { approvalsRecorded: 1, approvedByMe: true } : i === 1 ? { displayState: "AwaitingKeyProvisioning", state: "AwaitingKeyCustody" } : {}),
    );
    const calls = installFetch((c) => {
      if (!c.path.startsWith("/organization-onboarding?")) return undefined;
      const params = new URL("http://x" + c.path).searchParams;
      if (params.get("cursor") === "c2") return { status: 200, body: queuePage([queueItem(16), queueItem(17)], { totalCount: 17 }) };
      if (params.get("status") === "closed") return { status: 200, body: queuePage([queueItem(50, { displayState: "Rejected", state: "Rejected" })], { status: "closed" }) };
      return { status: 200, body: queuePage(first, { totalCount: 17, nextCursor: "c2" }) };
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByText("Onboard 15")).toBeInTheDocument();
    expect(screen.getByText("Approvals 1 of 2 · you approved")).toBeInTheDocument();
    expect(screen.getByText("Awaiting key provisioning")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New organization" })).toBeInTheDocument();
    expect(screen.getByText(/Showing 1–15 of 17 requests/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(await screen.findByText("Onboard 17")).toBeInTheDocument();
    expect(calls.some((c) => c.path.includes("cursor=c2"))).toBe(true);
    await user.click(screen.getByRole("combobox", { name: /Status/ }));
    await user.click(await screen.findByRole("option", { name: "Closed" }));
    expect(await screen.findByText("Onboard 50")).toBeInTheDocument();
    expect(screen.getByText("Rejected")).toBeInTheDocument();
  });
});

describe("PG-074 Onboarding details and decisions", () => {
  const path = `/platform/onboarding/${REQUEST}`;
  function open(detail: OnboardingDetail, extra: Handler = () => undefined) {
    window.history.replaceState(null, "", path);
    return installFetch((c) => (c.method === "GET" && c.path === `/organization-onboarding/${REQUEST}` ? { status: 200, body: detail } : extra(c)));
  }
  const decide = async (user: ReturnType<typeof userEvent.setup>, button: string, confirm: string, text = "Verified with the owner by phone.") => {
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), text);
    await user.click(screen.getByRole("button", { name: button }));
    await user.click(await screen.findByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: confirm }));
  };

  it("shows a pending request with the stepper, owners, fingerprint and an enabled Approve", async () => {
    open(detailOf());
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Onboarding request" })).toBeInTheDocument();
    expect(await screen.findByText("New customer contract signed.")).toBeInTheDocument();
    const stepper = screen.getByRole("list", { name: "Onboarding progress" });
    expect(within(stepper).getByText("Current step")).toBeInTheDocument();
    expect(screen.getByText("Also a member of another organization.")).toBeInTheDocument();
    expect(screen.getByTestId("fingerprint")).toHaveTextContent(SHA.slice(0, 16));
    expect(screen.queryByText("Key custody")).not.toBeInTheDocument();
    await userEvent.setup().type(screen.getByRole("textbox", { name: /Your justification/ }), "Verified with the owner by phone.");
    expect(screen.getByRole("button", { name: "Approve" })).toBeEnabled();
  });

  it("disables Approve for an operator who already approved and says why", async () => {
    open(
      detailOf({
        approvalsRecorded: 1,
        approvals: [
          {
            approvalId: "a1",
            platformOperatorId: "p1",
            operatorDisplayName: "Morgan Operator",
            isMine: true,
            justification: "Checked.",
            recordedOn: "2026-10-30T13:00:00Z",
            state: "Valid",
            closureReason: null,
          },
        ],
        myEligibility: { ...detailOf().myEligibility, canApprove: false, canWithdraw: true, approveRefusalCode: "OPERATOR_ALREADY_APPROVED" },
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Changed my mind about this.");
    expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled();
    expect(screen.getByText(/Approve is unavailable: A second approval must come from a different operator/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw my approval" })).toBeEnabled();
    expect(screen.getByText("You")).toBeInTheDocument();
  });

  it("records an approval and reads the request again before it reports success", async () => {
    let reads = 0;
    const calls = open(detailOf(), (c) => (c.path.endsWith("/approvals") ? { status: 200, body: decision({}) } : undefined));
    const base = globalThis.fetch;
    vi.stubGlobal(
      "fetch",
      vi.fn((input: string, init?: RequestInit) => {
        if ((init?.method ?? "GET") === "GET" && String(input).endsWith(REQUEST)) reads++;
        return base(input, init);
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Approve", "Record my approval");
    expect(await screen.findByText("Approval recorded — 1 of 2")).toBeInTheDocument();
    const post = calls.find((c) => c.path.endsWith("/approvals"))!;
    expect(post.body).toEqual({ expectedStatementSha256: SHA, justification: "Verified with the owner by phone." });
    expect(post.csrf).toBe("csrf-token-1");
    await waitFor(() => expect(reads).toBe(2));
  });

  it("shows the next step after the second approval", async () => {
    open(detailOf(), (c) =>
      c.path.endsWith("/approvals") ? { status: 200, body: decision({ outcome: "Approved", approvalsRecorded: 2, state: "AwaitingKeyCustody", displayState: "AwaitingKeyProvisioning" }) } : undefined,
    );
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Approve", "Record my approval", "Second approval after review.");
    expect(await screen.findByText("Request approved — 2 of 2. Next: key provisioning.")).toBeInTheDocument();
  });

  it("refuses a changed request, records nothing and re-reads the current request", async () => {
    let reads = 0;
    window.history.replaceState(null, "", path);
    installFetch((c) => {
      if (c.method === "GET" && c.path === `/organization-onboarding/${REQUEST}`) {
        reads++;
        return { status: 200, body: detailOf({ statementSha256: reads > 1 ? "c".repeat(64) : SHA }) };
      }
      if (c.path.endsWith("/approvals")) return problem(409, "APPROVAL_STATEMENT_CHANGED");
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Approve", "Record my approval");
    expect(await screen.findByText("The request changed")).toBeInTheDocument();
    expect(screen.queryByText(/Approval recorded/)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("fingerprint")).toHaveTextContent("c".repeat(16)));
    expect(screen.getAllByRole("button", { name: "Reload request" }).length).toBeGreaterThan(0);
  });

  it("shows the missing key panel with the custody instruction and no Initialize", async () => {
    open(
      detailOf({
        state: "AwaitingKeyCustody",
        displayState: "AwaitingKeyProvisioning",
        approvalsRecorded: 2,
        keyCustody: { keyName: "org-key-harbour", status: "Missing", detail: null },
        myEligibility: { ...detailOf().myEligibility, canApprove: false, canReject: false, canCancel: true, canInitialize: false, initializeRefusalCode: "ORGANIZATION_KEY_NOT_PROVISIONED" },
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByText("org-key-harbour")).toBeInTheDocument();
    expect(screen.getByText(/Waiting for key custody/)).toBeInTheDocument();
    expect(screen.getByText(new RegExp(`onboarding provision-key ${REQUEST}`))).toBeInTheDocument();
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "Waiting on the custodian now.");
    expect(screen.getByRole("button", { name: "Initialize organization" })).toBeDisabled();
    expect(screen.getByText(/Initialize is unavailable: Key custody has not provisioned/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cancel request" })).toBeEnabled();
  });

  it("initializes a ready organization and reports activating only from the confirmed response", async () => {
    const ready = detailOf({
      state: "AwaitingKeyCustody",
      displayState: "AwaitingKeyProvisioning",
      approvalsRecorded: 2,
      keyCustody: { keyName: "org-key-harbour", status: "Ready", detail: null },
      myEligibility: { ...detailOf().myEligibility, canApprove: false, canReject: false, canCancel: true, canInitialize: true },
    });
    const activating = detailOf({
      state: "Initialized",
      displayState: "Activating",
      approvalsRecorded: 2,
      keyCustody: ready.keyCustody,
      activation: { status: "Activating", authorizationRevision: 5, projectionAppliedRevision: 4 },
      myEligibility: { ...ready.myEligibility, canCancel: false, canInitialize: false },
    });
    let reads = 0;
    window.history.replaceState(null, "", path);
    const calls = installFetch((c) => {
      if (c.method === "GET" && c.path === `/organization-onboarding/${REQUEST}`) {
        reads++;
        return { status: 200, body: reads > 1 ? activating : ready };
      }
      if (c.path.endsWith("/initialize")) return { status: 200, body: decision({ outcome: "Initialized", decision: "initialize", state: "Initialized", displayState: "Activating" }) };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByText("Key ready — the organization can be initialized.")).toBeInTheDocument();
    await decide(user, "Initialize organization", "Initialize organization", "Key verified, initializing now.");
    expect(await screen.findByText("Organization initialized — activating")).toBeInTheDocument();
    expect(calls.find((c) => c.path.endsWith("/initialize"))?.body).toEqual({ expectedStatementSha256: SHA, justification: "Key verified, initializing now." });
    expect(await screen.findByText(/The organization is activating/)).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Refresh" }).length).toBeGreaterThan(0);
  });

  it("explains a key that is not provisioned when initialization is refused by the Authority", async () => {
    const ready = detailOf({
      state: "AwaitingKeyCustody",
      displayState: "AwaitingKeyProvisioning",
      approvalsRecorded: 2,
      keyCustody: { keyName: "k", status: "Ready", detail: null },
      myEligibility: { ...detailOf().myEligibility, canApprove: false, canReject: false, canCancel: true, canInitialize: true },
    });
    open(ready, (c) => (c.path.endsWith("/initialize") ? problem(409, "ORGANIZATION_KEY_NOT_PROVISIONED") : undefined));
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Initialize organization", "Initialize organization", "Key verified, initializing now.");
    expect(await screen.findByText("The organization key is not provisioned yet")).toBeInTheDocument();
    expect(screen.queryByText(/initialized — activating/)).not.toBeInTheDocument();
  });

  it("shows an active organization", async () => {
    open(
      detailOf({
        state: "Initialized",
        displayState: "Active",
        approvalsRecorded: 2,
        activation: { status: "Active", authorizationRevision: 5, projectionAppliedRevision: 5 },
        myEligibility: { ...detailOf().myEligibility, canApprove: false, canReject: false },
      }),
    );
    render(<App />);
    expect(await screen.findByText("The organization is active.")).toBeInTheDocument();
    expect(screen.getByText(/No decision is available for this request/)).toBeInTheDocument();
  });
});
