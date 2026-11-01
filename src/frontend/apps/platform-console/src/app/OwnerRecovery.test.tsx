import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DirectoryItem, RecoveryDecisionResponse, RecoveryDetail, RecoveryItem } from "../api/client";
import { setCsrfToken } from "../api/client";
import { App } from "./App";
import { formatCountdown } from "../pages/RecoveryDetailPage";

const ORG = "01a16640-87f6-7488-a1f1-000000000001";
const CASE = "01a194c3-cbb0-70ef-8e74-31d8e5d30001";
const SHA = "c".repeat(16) + "d".repeat(48);
const ISSUER = "https://test-idp.enterpriseforms.invalid";
const OWNER = "01a194d8-0000-7000-8000-000000000001";

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
  enabledOwnerCount: 0,
  securityAdministratorCount: 1,
  memberCount: 4,
  needsOwners: true,
  onboardingRequestId: null,
  ...over,
});
const dirPage = (items: DirectoryItem[]) => ({ items, totalCount: items.length, nextCursor: null, limit: 10, filter: "needs_owners", correlationId: "x" });

const listItem = (n: number, over: Partial<RecoveryItem> = {}): RecoveryItem => ({
  caseId: `01a194c3-cbb0-70ef-8e74-31d8e5d3${String(n).padStart(4, "0")}`,
  organizationId: ORG,
  organizationDisplayName: `Recover ${n}`,
  incidentReference: `INC-${n}`,
  state: "Open",
  displayState: "AwaitingApprovals",
  openedOn: "2026-10-30T12:00:00Z",
  waitingPeriodEndsOn: "2026-11-02T12:00:00Z",
  expiresOn: "2099-11-06T12:00:00Z",
  approvalsRecorded: 0,
  approvalsRequired: 3,
  approvedByMe: false,
  replacementCount: 1,
  openedByOperatorDisplayName: "Quinn Operator",
  ...over,
});
const listPage = (items: RecoveryItem[], over: Record<string, unknown> = {}) => ({
  items,
  totalCount: items.length,
  nextCursor: null,
  limit: 15,
  status: "all",
  waitingPeriodSeconds: 20,
  approvalAssurance: "SyntheticDevelopment",
  correlationId: "x",
  ...over,
});

const detailOf = (over: Partial<RecoveryDetail> = {}): RecoveryDetail => ({
  caseId: CASE,
  organizationId: ORG,
  organizationDisplayName: "Harbour Legal",
  state: "Open",
  displayState: "AwaitingApprovals",
  incidentReference: "INC-4411",
  justification: "The only owner left the company.",
  openedByPlatformOperatorId: "01a194d8-7599-75e4-936d-000000000002",
  openedByOperatorDisplayName: "Quinn Operator",
  openedOn: "2026-10-30T12:00:00Z",
  waitingPeriodEndsOn: "2099-11-01T12:00:00Z",
  waitingPeriodRemainingSeconds: 100,
  expiresOn: "2099-11-06T12:00:00Z",
  recoveredOn: null,
  closedOn: null,
  closureReason: null,
  closureJustification: null,
  lastExecutionFailureCode: null,
  lastExecutionFailureOn: null,
  eligibleOwnersBefore: 0,
  eligibleOwnersAfter: 0,
  currentOwners: [{ authorityUserId: OWNER, issuerUri: ISSUER, subjectIdentifier: "owner-old", enabled: true, declaredUnavailable: true, activeSinceCaseOpened: false }],
  subjects: [
    {
      role: "Replacement",
      authorityUserId: "01a194d8-0000-7000-8000-000000000009",
      issuerUri: ISSUER,
      subjectIdentifier: "owner-new",
      displayNameHint: "Robin Replacement",
      isNewIdentity: true,
      verificationMethod: "SupportCallback",
      verificationReference: "TICKET-9",
      unavailabilityReason: null,
      evidenceReference: null,
      evidenceSummary: null,
    },
    {
      role: "DeclaredUnavailable",
      authorityUserId: OWNER,
      issuerUri: ISSUER,
      subjectIdentifier: "owner-old",
      displayNameHint: "Lee Leaver",
      isNewIdentity: false,
      verificationMethod: null,
      verificationReference: null,
      unavailabilityReason: "LeftOrganization",
      evidenceReference: "HR-77",
      evidenceSummary: "HR confirmed departure.",
    },
  ],
  approvals: [],
  approvalsRecorded: 0,
  approvalsRequired: 3,
  objection: null,
  statementJson: '{"statement_version":1}',
  statementSha256: SHA,
  activation: null,
  myEligibility: { canApprove: true, canWithdraw: false, canCancel: true, canExecute: false, approveRefusalCode: null, approveRefusalDetail: null, executeRefusalCode: "OWNER_RECOVERY_APPROVALS_INCOMPLETE", executeRefusalDetail: null },
  rowRevision: 1,
  approvalAssurance: "SyntheticDevelopment (development-only operator keys)",
  correlationId: "x",
  ...over,
});

const decision = (over: Partial<RecoveryDecisionResponse>): RecoveryDecisionResponse => ({
  dryRun: false,
  eligible: true,
  refusalCode: null,
  refusalDetail: null,
  caseId: CASE,
  organizationId: ORG,
  decision: "approve",
  statementJson: "{}",
  statementSha256: SHA,
  outcome: "Recorded",
  approvalsRecorded: 1,
  approvalsRequired: 3,
  state: "Open",
  displayState: "AwaitingApprovals",
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
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
  setCsrfToken("");
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("formatCountdown", () => {
  it("renders seconds, minutes, hours and days", () => {
    expect(formatCountdown(7)).toBe("7 s");
    expect(formatCountdown(125)).toBe("2 min 5 s");
    expect(formatCountdown(3725)).toBe("1 h 2 min 5 s");
    expect(formatCountdown(90000)).toBe("1 d 1 h 0 min");
  });
});

describe("PG-075 Owner recovery queue", () => {
  it("lists organizations needing recovery and cases, with progress chips, filters and server paging", async () => {
    window.history.replaceState(null, "", "/platform/recovery");
    const first = Array.from({ length: 15 }, (_, i) => listItem(i + 1, i === 0 ? { approvalsRecorded: 1, approvedByMe: true } : i === 1 ? { displayState: "WaitingPeriod" } : {}));
    const calls = installFetch((c) => {
      if (c.path.startsWith("/organizations?")) return { status: 200, body: dirPage([dirItem(40)]) };
      if (!c.path.startsWith("/owner-recovery?")) return undefined;
      const params = new URL("http://x" + c.path).searchParams;
      if (params.get("cursor") === "c2") return { status: 200, body: listPage([listItem(16), listItem(17)], { totalCount: 17 }) };
      if (params.get("status") === "closed") return { status: 200, body: listPage([listItem(50, { displayState: "Expired", state: "Expired" })], { status: "closed" }) };
      return { status: 200, body: listPage(first, { totalCount: 17, nextCursor: "c2" }) };
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Owner recovery" })).toBeInTheDocument();
    const needing = await screen.findByRole("list", { name: "Organizations requiring recovery" });
    expect(within(needing).getByText("Org 40")).toBeInTheDocument();
    expect(calls.some((c) => c.path.includes("filter=needs_owners"))).toBe(true);
    expect(await screen.findByText("Recover 15")).toBeInTheDocument();
    expect(screen.getByText("Approvals 1 of 3 · you approved")).toBeInTheDocument();
    expect(screen.getByText("Waiting period", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByText(/Showing 1–15 of 17 cases/)).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/owner-recovery?")).every((c) => c.path.includes("limit=15"))).toBe(true);
    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(await screen.findByText("Recover 17")).toBeInTheDocument();
    expect(calls.some((c) => c.path.includes("cursor=c2"))).toBe(true);
    await user.click(screen.getByRole("combobox", { name: /Status/ }));
    await user.click(await screen.findByRole("option", { name: "Expired or cancelled" }));
    expect(await screen.findByText("Recover 50")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Create recovery case for Org 40" }));
    expect(window.location.pathname).toBe("/platform/recovery/new");
    expect(window.location.search).toBe(`?organizationId=${dirItem(40).organizationId}`);
  }, 30_000);

  it("has a navigation entry", async () => {
    window.history.replaceState(null, "", "/platform/recovery");
    installFetch((c) => (c.path.startsWith("/owner-recovery?") ? { status: 200, body: listPage([]) } : c.path.startsWith("/organizations?") ? { status: 200, body: dirPage([]) } : undefined));
    render(<App />);
    const nav = await screen.findByRole("navigation", { name: "Platform administration" });
    expect(within(nav).getByRole("link", { name: "Owner recovery" })).toHaveAttribute("aria-current", "page");
    expect(await screen.findByText("No organization currently has fewer than two enabled owners.")).toBeInTheDocument();
  });
});

describe("PG-075 New recovery case", () => {
  const orgDetail = {
    summary: dirItem(1, { organizationId: ORG, organizationDisplayName: "Harbour Legal", enabledOwnerCount: 1 }),
    authorizationRevision: 5,
    projectionAppliedRevision: 5,
    owners: [{ authorityUserId: OWNER, issuerUri: ISSUER, subjectIdentifier: "owner-old", enabled: true }],
    onboardingApprovedBy: [],
    correlationId: "x",
  };

  it("validates, runs a dry-run, then creates the case and opens it", async () => {
    window.history.replaceState(null, "", `/platform/recovery/new?organizationId=${ORG}`);
    const created = detailOf();
    const calls = installFetch((c) => {
      if (c.path === `/organizations/${ORG}`) return { status: 200, body: orgDetail };
      if (c.path === "/owner-recovery" && c.method === "POST") {
        return c.body?.["dryRun"] === true
          ? { status: 200, body: { dryRun: true, eligible: true, refusalCode: null, refusalDetail: null, warnings: ["Only one replacement owner is proposed."], case: null, replayed: false, correlationId: "x" } }
          : { status: 201, body: { dryRun: false, eligible: true, refusalCode: null, refusalDetail: null, warnings: [], case: created, replayed: false, correlationId: "x" } };
      }
      if (c.path === `/owner-recovery/${CASE}`) return { status: 200, body: created };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByRole("heading", { name: "New recovery case" })).toBeInTheDocument();
    expect(await screen.findByText("owner-old")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Validate" }));
    expect(await screen.findByText("Fix the highlighted fields")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);

    await user.type(screen.getByRole("textbox", { name: /Incident reference/ }), "INC-4411");
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "The only owner left the company.");
    await user.click(screen.getByRole("checkbox", { name: /Declare owner-old unavailable/ }));
    await user.click(screen.getByRole("combobox", { name: /Reason \(owner-old\)/ }));
    await user.click(await screen.findByRole("option", { name: "Left the organization" }));
    await user.type(screen.getByRole("textbox", { name: /Evidence reference \(owner-old\)/ }), "HR-77");
    await user.type(screen.getByRole("textbox", { name: /Evidence summary \(owner-old\)/ }), "HR confirmed departure.");
    await user.type(screen.getByRole("textbox", { name: /Subject identifier \(replacement 1\)/ }), "owner-new");
    await user.type(screen.getByRole("textbox", { name: /Display name \(replacement 1\)/ }), "Robin Replacement");
    await user.click(screen.getByRole("combobox", { name: /Verification method \(replacement 1\)/ }));
    await user.click(await screen.findByRole("option", { name: "Support call-back" }));
    await user.type(screen.getByRole("textbox", { name: /Verification reference \(replacement 1\)/ }), "TICKET-9");

    await user.click(screen.getByRole("button", { name: "Validate" }));
    expect(await screen.findByText("Check passed — nothing was created")).toBeInTheDocument();
    expect(screen.getByText("Only one replacement owner is proposed.")).toBeInTheDocument();
    const dry = calls.find((c) => c.method === "POST")!;
    expect(dry.body).toMatchObject({
      dryRun: true,
      organizationId: ORG,
      incidentReference: "INC-4411",
      declaredUnavailable: [{ authorityUserId: OWNER, reason: "LeftOrganization", evidenceReference: "HR-77", evidenceSummary: "HR confirmed departure." }],
      replacements: [{ existingAuthorityUserId: null, issuerUri: ISSUER, subjectIdentifier: "owner-new", displayNameHint: "Robin Replacement", verificationMethod: "SupportCallback", verificationReference: "TICKET-9" }],
    });
    expect(dry.csrf).toBe("csrf-token-1");

    await user.click(screen.getByRole("button", { name: "Create case" }));
    expect(await screen.findByRole("heading", { name: "Recovery case" })).toBeInTheDocument();
    expect(window.location.pathname).toBe(`/platform/recovery/${CASE}`);
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts[1]!.body).toMatchObject({ dryRun: false });
    expect(String(posts[1]!.body!["idempotencyKey"])).toMatch(/^rec-/);
    expect(posts[1]!.body!["idempotencyKey"]).toBe(posts[0]!.body!["idempotencyKey"]);
  }, 40_000);

  it("shows a refusal from the dry-run inline", async () => {
    window.history.replaceState(null, "", `/platform/recovery/new?organizationId=${ORG}`);
    installFetch((c) => {
      if (c.path === `/organizations/${ORG}`) return { status: 200, body: orgDetail };
      if (c.method === "POST") return { status: 200, body: { dryRun: true, eligible: false, refusalCode: "OWNER_RECOVERY_CASE_EXISTS", refusalDetail: null, warnings: [], case: null, replayed: false, correlationId: "x" } };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await screen.findByText("owner-old");
    await user.type(screen.getByRole("textbox", { name: /Incident reference/ }), "INC-4411");
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "The only owner left the company.");
    await user.type(screen.getByRole("textbox", { name: /Subject identifier \(replacement 1\)/ }), "owner-new");
    await user.type(screen.getByRole("textbox", { name: /Display name \(replacement 1\)/ }), "Robin Replacement");
    await user.click(screen.getByRole("combobox", { name: /Verification method \(replacement 1\)/ }));
    await user.click(await screen.findByRole("option", { name: "Support call-back" }));
    await user.type(screen.getByRole("textbox", { name: /Verification reference \(replacement 1\)/ }), "TICKET-9");
    await user.click(screen.getByRole("button", { name: "Validate" }));
    expect(await screen.findByText("A recovery case is already open")).toBeInTheDocument();
  }, 40_000);
});

describe("PG-075 Recovery case details and decisions", () => {
  const path = `/platform/recovery/${CASE}`;
  function open(detail: RecoveryDetail, extra: Handler = () => undefined) {
    window.history.replaceState(null, "", path);
    return installFetch((c) => (c.method === "GET" && c.path === `/owner-recovery/${CASE}` ? { status: 200, body: detail } : extra(c)));
  }
  const decide = async (user: ReturnType<typeof userEvent.setup>, button: string, confirm: string, text = "Verified with the customer.") => {
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), text);
    await user.click(screen.getByRole("button", { name: button }));
    await user.click(await screen.findByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: confirm }));
  };

  it("shows owners, proposed owners, evidence, progress, the synthetic label and the fingerprint", async () => {
    open(detailOf({ approvalsRecorded: 1 }));
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Recovery case" })).toBeInTheDocument();
    expect(await screen.findByText("The only owner left the company.")).toBeInTheDocument();
    expect(screen.getByTestId("approval-progress")).toHaveTextContent("1 of 3");
    expect(screen.getByText("Declared unavailable")).toBeInTheDocument();
    expect(screen.getByText("Robin Replacement", { exact: false })).toBeInTheDocument();
    expect(screen.getByText("New identity")).toBeInTheDocument();
    expect(screen.getByText("HR confirmed departure.")).toBeInTheDocument();
    expect(screen.getByText("No owner has objected.")).toBeInTheDocument();
    expect(screen.getByText(/SyntheticDevelopment \(development-only operator keys\)/)).toBeInTheDocument();
    expect(screen.getByTestId("fingerprint")).toHaveTextContent(SHA.slice(0, 16));
  });

  it("counts down the waiting period, reloads when it ends and disables Execute with the refusal text", async () => {
    const ends = new Date(Date.now() + 2500).toISOString();
    let reads = 0;
    window.history.replaceState(null, "", path);
    installFetch((c) => {
      if (c.method === "GET" && c.path === `/owner-recovery/${CASE}`) {
        reads++;
        return { status: 200, body: detailOf({ displayState: "WaitingPeriod", waitingPeriodEndsOn: ends, approvalsRecorded: 3, myEligibility: { ...detailOf().myEligibility, canExecute: false, executeRefusalCode: "OWNER_RECOVERY_WAITING_PERIOD" } }) };
      }
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Verified with the customer.");
    expect(screen.getByTestId("waiting-remaining")).toHaveTextContent(/remaining/);
    expect(screen.getByRole("button", { name: "Execute recovery" })).toBeDisabled();
    expect(screen.getByText(/Execute recovery is unavailable: Recovery cannot run until the waiting period ends/)).toBeInTheDocument();
    await waitFor(() => expect(reads).toBeGreaterThanOrEqual(2), { timeout: 6000 });
    expect(screen.getByTestId("waiting-remaining")).toHaveTextContent("ended");
  }, 20_000);

  it("shows an objection and the blocked state", async () => {
    open(
      detailOf({
        displayState: "BlockedByObjection",
        objection: { objectedOn: "2026-10-31T09:00:00Z", objectedByAuthorityUserId: OWNER, objectedBySubjectIdentifier: "owner-old", reason: "We did not ask for this." },
        myEligibility: { ...detailOf().myEligibility, canExecute: false, executeRefusalCode: "OWNER_RECOVERY_OBJECTED" },
      }),
    );
    render(<App />);
    expect(await screen.findByText("An owner objected")).toBeInTheDocument();
    expect(screen.getByText("We did not ask for this.")).toBeInTheDocument();
    expect(screen.getAllByText("Blocked by objection").length).toBeGreaterThan(0);
  });

  it("sends the statement fingerprint and justification with an approval and reads the case again before success", async () => {
    let reads = 0;
    const calls = open(detailOf(), (c) => (c.path.endsWith("/approvals") ? { status: 200, body: decision({}) } : undefined));
    const base = globalThis.fetch;
    vi.stubGlobal(
      "fetch",
      vi.fn((input: string, init?: RequestInit) => {
        if ((init?.method ?? "GET") === "GET" && String(input).endsWith(CASE)) reads++;
        return base(input, init);
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Approve", "Record my approval");
    expect(await screen.findByText("Approval recorded — 1 of 3")).toBeInTheDocument();
    const post = calls.find((c) => c.path.endsWith("/approvals"))!;
    expect(post.body).toEqual({ expectedStatementSha256: SHA, justification: "Verified with the customer." });
    expect(post.csrf).toBe("csrf-token-1");
    await waitFor(() => expect(reads).toBe(2));
  });

  it("executes a ready recovery and shows Recovered", async () => {
    open(detailOf({ displayState: "ReadyForRecovery", approvalsRecorded: 3, myEligibility: { ...detailOf().myEligibility, canExecute: true, executeRefusalCode: null } }), (c) =>
      c.path.endsWith("/execute") ? { status: 200, body: decision({ decision: "execute", displayState: "Recovered", state: "Recovered", approvalsRecorded: 3 }) } : undefined,
    );
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Execute recovery", "Execute recovery");
    expect(await screen.findByText("Recovery executed — the owners were restored")).toBeInTheDocument();
  });

  it("maps a refused execute to a friendly message and reloads the case", async () => {
    open(detailOf({ displayState: "ReadyForRecovery", myEligibility: { ...detailOf().myEligibility, canExecute: true, executeRefusalCode: null } }), (c) =>
      c.path.endsWith("/execute") ? problem(409, "OWNER_RECOVERY_WAITING_PERIOD") : undefined,
    );
    const user = userEvent.setup();
    render(<App />);
    await decide(user, "Execute recovery", "Execute recovery");
    expect(await screen.findByText("The waiting period has not ended")).toBeInTheDocument();
    expect(screen.queryByText("Recovery executed — the owners were restored")).not.toBeInTheDocument();
  });

  it("offers withdraw only to an operator who approved and disables approve with the server's reason", async () => {
    open(
      detailOf({
        approvalsRecorded: 1,
        approvals: [{ approvalId: "a1", platformOperatorId: "p1", operatorDisplayName: "Morgan Operator", isMine: true, justification: "Checked.", recordedOn: "2026-10-30T13:00:00Z", state: "Valid", closureReason: null }],
        myEligibility: { ...detailOf().myEligibility, canApprove: false, canWithdraw: true, approveRefusalCode: "OPERATOR_ALREADY_APPROVED" },
      }),
    );
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Changed my mind about this.");
    expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled();
    expect(screen.getByText(/Approve is unavailable: A second approval must come from a different operator/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw my approval" })).toBeEnabled();
  });
});
