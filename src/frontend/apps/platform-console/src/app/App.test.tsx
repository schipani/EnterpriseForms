import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DecisionResponse, RequestDetail, RequestItem } from "../api/client";
import { setCsrfToken } from "../api/client";
import { App } from "./App";

const ORG = "01a16640-87f6-7488-a1f1-000000000001";
const SHA = "a".repeat(16) + "b".repeat(48);

interface Call {
  method: string;
  path: string;
  body: Record<string, unknown> | null;
  csrf: string | null;
}

const item = (n: number, over: Partial<RequestItem> = {}): RequestItem => ({
  requestId: `01a194c3-cbb0-70ef-8e74-31d8e5d3${String(n).padStart(4, "0")}`,
  organizationId: ORG,
  organizationDisplayName: "Northwind Crown Office (synthetic)",
  displayNameHint: `Person ${n}`,
  target: { issuerUri: "https://test-idp.enterpriseforms.invalid", subjectIdentifier: `person-${n}` },
  initialRoleName: "Designer",
  requestedByAuthorityUserId: "01a194c3-cbb0-70ef-8e74-000000000001",
  requestedBy: { issuerUri: "https://test-idp.enterpriseforms.invalid", subjectIdentifier: "dev-owner" },
  requestedOn: "2026-10-30T12:00:00Z",
  expiresOn: "2099-11-06T12:00:00Z",
  requestState: "Pending",
  approvalStatus: { recorded: 0, required: 2, approvedByMe: false, awaitingOtherOperator: false, pendingApprovalExpiresOn: null },
  availableActions: ["review"],
  ...over,
});

const detailOf = (request: RequestItem, over: Partial<RequestDetail> = {}): RequestDetail => ({
  request,
  requestReason: "joins the intake team",
  replacesRequestId: null,
  statementJson: '{"statement_version":2}',
  statementSha256: SHA,
  consequences: ["Creates one organization membership"],
  approvals: [],
  myEligibility: { canApprove: true, canReject: true, canWithdraw: false, refusalCode: null, refusalDetail: null },
  evidenceAssurance: "SyntheticDevelopment (development-only operator keys; not two-human approval evidence)",
  correlationId: "01a194c3-cbb0-70ef-8e74-0000000000aa",
  ...over,
});

const sessionBody = (name = "Morgan Operator (synthetic)") => ({
  platformOperatorId: "01a194d8-7599-75e4-936d-94caa17075d7",
  displayName: name,
  issuerUri: "https://platform-operators.dev.enterpriseforms.invalid/",
  subjectIdentifier: "dev-platform-operator-morgan",
  operatorStatus: "Active",
  permissions: ["platform.membership_requests.review"],
  activeOperatorCount: 3,
  authenticationMode: "SyntheticDevelopment (development operator selection; not production authentication)",
  evidenceAssurance: "SyntheticDevelopment",
  environmentNotice: "Development operators: approvals made here are synthetic and are not two-person evidence.",
  csrfToken: "csrf-token-1",
  idleExpiresOn: new Date(Date.now() + 15 * 60_000).toISOString(),
  absoluteExpiresOn: new Date(Date.now() + 120 * 60_000).toISOString(),
});

type Handler = (call: Call) => { status: number; body?: unknown } | undefined;

function problem(status: number, errorCode: string, detail = "refused", retryable = false) {
  return { status, body: { type: "x", title: errorCode, status, detail, errorCode, retryable, correlationId: "01a194c3-cbb0-70ef-8e74-0000000000bb" } };
}

function installFetch(handler: Handler) {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = new URL(input, "http://127.0.0.1:8095");
      const headers = (init?.headers ?? {}) as Record<string, string>;
      const call: Call = { method: init?.method ?? "GET", path: url.pathname.replace("/console-api/v1", "") + url.search, body: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null, csrf: headers["X-CSRF-Token"] ?? null };
      calls.push(call);
      const answer = handler(call) ?? { status: 404, body: { errorCode: "NOT_FOUND" } };
      return new Response(answer.status === 204 ? null : JSON.stringify(answer.body ?? {}), { status: answer.status, headers: { "Content-Type": "application/json" } });
    }),
  );
  return calls;
}

const operatorsList = { operators: [{ subjectIdentifier: "dev-platform-operator-morgan", displayName: "Morgan Operator (synthetic)", issuerUri: "https://x" }, { subjectIdentifier: "dev-platform-operator-quinn", displayName: "Quinn Operator (synthetic)", issuerUri: "https://x" }, { subjectIdentifier: "dev-platform-operator-rowan", displayName: "Rowan Operator (synthetic)", issuerUri: "https://x" }], authenticationMode: "SyntheticDevelopment", environmentNotice: "Development operators: approvals made here are synthetic and are not two-person evidence." };

const page = (items: RequestItem[], over: Record<string, unknown> = {}) => ({ items, totalCount: items.length, nextCursor: null, limit: 15, status: "needs_decision", journal: { revision: 1, pendingArchiveEntries: 0, quarantined: false, quarantineReason: null }, evidenceAssurance: "x", correlationId: "x", ...over });

beforeEach(() => {
  // jsdom lacks the pointer APIs Radix Select uses.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => undefined;
  Element.prototype.releasePointerCapture = () => undefined;
  Element.prototype.scrollIntoView = () => undefined;
  setCsrfToken("");
  window.history.replaceState(null, "", "/platform/approvals");
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Platform Console sign-in and session", () => {
  it("signs a synthetic operator in, shows the development banner and the operator, and signs out", async () => {
    let signedIn = false;
    const calls = installFetch((c) => {
      if (c.path === "/session" && c.method === "GET") return signedIn ? { status: 200, body: sessionBody() } : problem(401, "SESSION_REQUIRED");
      if (c.path === "/session/development-operators") return { status: 200, body: operatorsList };
      if (c.path === "/session/development-login") { signedIn = true; return { status: 200, body: sessionBody() }; }
      if (c.path.startsWith("/membership-requests")) return { status: 200, body: page([]) };
      if (c.path === "/session/sign-out") { signedIn = false; return { status: 204 }; }
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Sign in to the Platform Console" })).toBeInTheDocument();
    expect(screen.getByText(/Development\/Test operator — synthetic approval evidence/)).toBeInTheDocument();
    expect(await screen.findByText("Morgan Operator (synthetic)")).toBeInTheDocument();
    expect(screen.getByText("Quinn Operator (synthetic)")).toBeInTheDocument();
    expect(screen.getByText("Rowan Operator (synthetic)")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Sign in as Morgan Operator (synthetic)" }));
    expect(await screen.findByRole("heading", { name: "Operator approvals" })).toBeInTheDocument();
    expect(screen.getByTestId("operator-session")).toHaveTextContent("Morgan Operator (synthetic)");
    expect(screen.getByTestId("operator-session")).toHaveTextContent(/Session idle expiry/);
    expect(calls.find((c) => c.path === "/session/development-login")?.body).toEqual({ subjectIdentifier: "dev-platform-operator-morgan" });

    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await screen.findByRole("heading", { name: "Sign in to the Platform Console" })).toBeInTheDocument();
    const out = calls.find((c) => c.path === "/session/sign-out");
    expect(out?.csrf).toBe("csrf-token-1"); // the in-memory token from the session response
    expect(window.location.pathname).toBe("/platform/sign-in");
  });

  it("returns to sign-in with an explanation when the session has expired", async () => {
    let expired = false;
    installFetch((c) => {
      if (c.path === "/session") return { status: 200, body: sessionBody() };
      if (c.path === "/session/development-operators") return { status: 200, body: operatorsList };
      if (c.path.startsWith("/membership-requests")) return expired ? problem(401, "SESSION_REQUIRED") : { status: 200, body: page([item(1)]) };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByText("Person 1")).toBeInTheDocument();
    expired = true;
    await user.type(screen.getByRole("searchbox", { name: /Search/ }), "x");
    expect(await screen.findByText(/Your session ended or expired/)).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Sign in to the Platform Console" })).toBeInTheDocument();
  });

  it("tells a suspended operator that access ended", async () => {
    installFetch((c) => {
      if (c.path === "/session") return { status: 200, body: sessionBody() };
      if (c.path === "/session/development-operators") return { status: 200, body: operatorsList };
      if (c.path.startsWith("/membership-requests")) return problem(403, "PLATFORM_OPERATOR_NOT_ACTIVE");
      return undefined;
    });
    render(<App />);
    expect(await screen.findByText(/operator access is no longer valid/)).toBeInTheDocument();
  });

  it("stores nothing in browser storage", async () => {
    const set = vi.spyOn(Storage.prototype, "setItem");
    installFetch((c) => (c.path === "/session" ? { status: 200, body: sessionBody() } : c.path.startsWith("/membership-requests") ? { status: 200, body: page([item(1)]) } : undefined));
    render(<App />);
    await screen.findByText("Person 1");
    expect(set).not.toHaveBeenCalled();
    expect(document.cookie).toBe("");
  });
});

describe("Operator approvals queue", () => {
  it("pages on the server, 15 at a time, with the Authority's cursors, and filters by status and search", async () => {
    const first = Array.from({ length: 15 }, (_, i) => item(i + 1));
    const second = [item(16), item(17)];
    const calls = installFetch((c) => {
      if (c.path === "/session") return { status: 200, body: sessionBody() };
      if (!c.path.startsWith("/membership-requests")) return undefined;
      const params = new URL("http://x" + c.path).searchParams;
      if (params.get("cursor") === "cursor-2") return { status: 200, body: page(second, { totalCount: 17 }) };
      if (params.get("q") === "harbour") return { status: 200, body: page([item(30, { organizationDisplayName: "Harbour Legal" })], { totalCount: 1 }) };
      return { status: 200, body: page(first, { totalCount: 17, nextCursor: "cursor-2" }) };
    });
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByText("Person 15")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /^Review details for/ })).toHaveLength(15);
    expect(screen.getByText(/Showing 1–15 of 17 requests/)).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/membership-requests")).every((c) => c.path.includes("limit=15"))).toBe(true);
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(await screen.findByText("Person 17")).toBeInTheDocument();
    expect(screen.queryByText("Person 1")).not.toBeInTheDocument();
    expect(screen.getByText(/Showing 16–17 of 17 requests/)).toBeInTheDocument();
    expect(calls.some((c) => c.path.includes("cursor=cursor-2"))).toBe(true);
    expect(screen.getByRole("button", { name: "Next" })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "Previous" }));
    expect(await screen.findByText("Person 1")).toBeInTheDocument();

    await user.type(screen.getByRole("searchbox", { name: /Search/ }), "harbour");
    expect(await screen.findByText("Harbour Legal")).toBeInTheDocument();
    expect(calls.some((c) => c.path.includes("q=harbour") && c.path.includes("status=needs_decision"))).toBe(true);
    expect(screen.queryByText("Person 1")).not.toBeInTheDocument();
  });

  it("sends the chosen status filter and shows an empty state", async () => {
    const calls = installFetch((c) => (c.path === "/session" ? { status: 200, body: sessionBody() } : c.path.startsWith("/membership-requests") ? { status: 200, body: page([]) } : undefined));
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByText("No requests need a decision")).toBeInTheDocument();
    await user.click(screen.getByRole("combobox", { name: /Status/ }));
    await user.click(await screen.findByRole("option", { name: "Rejected" }));
    await waitFor(() => expect(calls.some((c) => c.path.includes("status=rejected"))).toBe(true));
    expect(await screen.findByText("No requests match")).toBeInTheDocument();
  });

  it("shows organization, person, verified identity, role, dates and progress without the requester identity", async () => {
    installFetch((c) =>
      c.path === "/session" ? { status: 200, body: sessionBody() } : c.path.startsWith("/membership-requests") ? { status: 200, body: page([item(1, { approvalStatus: { recorded: 1, required: 2, approvedByMe: true, awaitingOtherOperator: false, pendingApprovalExpiresOn: null } })]) } : undefined,
    );
    render(<App />);
    const row = (await screen.findByText("Person 1")).closest("tr")!;
    const cells = within(row);
    expect(cells.getByText("Northwind Crown Office (synthetic)")).toBeInTheDocument();
    expect(cells.getByText(/person-1 · test-idp.enterpriseforms.invalid/)).toBeInTheDocument();
    expect(cells.getByText("Designer")).toBeInTheDocument();
    expect(cells.getByText("1 of 2 · you approved")).toBeInTheDocument();
    expect(row.textContent).not.toContain("dev-owner");
  });

  it("reports an unavailable service explicitly and offers a retry", async () => {
    installFetch((c) => (c.path === "/session" ? { status: 200, body: sessionBody() } : c.path.startsWith("/membership-requests") ? problem(503, "PLATFORM_JOURNAL_UNVERIFIABLE", "x", true) : undefined));
    render(<App />);
    expect(await screen.findByText("Temporarily unavailable")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });
});

describe("Queue on narrow screens", () => {
  it("shows cards instead of a table below 768px, with the same facts and Review details action", async () => {
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: true, media: query, addEventListener: () => undefined, removeEventListener: () => undefined }));
    installFetch((c) => (c.path === "/session" ? { status: 200, body: sessionBody() } : c.path.startsWith("/membership-requests") ? { status: 200, body: page([item(1), item(2, { approvalStatus: { recorded: 1, required: 2, approvedByMe: false, awaitingOtherOperator: true, pendingApprovalExpiresOn: null } })], { totalCount: 17, nextCursor: "c2" }) } : undefined));
    render(<App />);
    const list = await screen.findByRole("list", { name: "Membership requests" });
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    const cards = within(list).getAllByRole("article");
    expect(cards).toHaveLength(2);
    expect(cards[0]).toHaveTextContent("Person 1");
    expect(cards[0]).toHaveTextContent("person-1 · test-idp.enterpriseforms.invalid");
    expect(cards[0]).toHaveTextContent("Northwind Crown Office (synthetic)");
    expect(cards[0]).toHaveTextContent("Designer");
    expect(cards[1]).toHaveTextContent("1 of 2 · awaiting another operator");
    expect(within(cards[0]!).getByRole("button", { name: /^Review details for Person 1/ })).toBeInTheDocument();
    expect(screen.getByText(/Showing 1–2 of 17 requests/)).toBeInTheDocument(); // pagination is unchanged
    expect(screen.getByRole("searchbox", { name: /Search/ })).toBeInTheDocument();
    await userEvent.setup().click(within(cards[0]!).getByRole("button", { name: /^Review details/ }));
    expect(window.location.pathname).toContain("/platform/approvals/");
  });
});

describe("Review details and decisions", () => {
  const request = item(1);
  const path = `/platform/approvals/${ORG}/${request.requestId}`;

  function review(extra: (c: Call) => ReturnType<Handler>, detail: RequestDetail = detailOf(request)) {
    window.history.replaceState(null, "", path);
    return installFetch((c) => {
      if (c.path === "/session") return { status: 200, body: sessionBody() };
      if (c.method === "GET" && c.path === `/organizations/${ORG}/membership-requests/${request.requestId}`) return { status: 200, body: detail };
      return extra(c);
    });
  }

  const response = (over: Partial<DecisionResponse>): DecisionResponse => ({
    dryRun: false, eligible: true, refusalCode: null, refusalDetail: null, requestId: request.requestId, organizationId: ORG, decision: "approve", statementJson: "{}", statementSha256: SHA,
    outcome: "AwaitingSecondApproval", approvalsRecorded: 1, approvalsRequired: 2, myApprovalId: "x", securityCommandId: null, commandStatus: null, authorizationRevision: 1, journalRevision: 1, journalArchiveState: "Anchored", evidenceAssurance: "x", correlationId: "x", ...over,
  });

  it("shows the request, the owner's reason, identities, the fingerprint and the statement", async () => {
    review(() => undefined);
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Review request" })).toBeInTheDocument();
    expect(await screen.findByText("joins the intake team")).toBeInTheDocument();
    expect(screen.getAllByText("Northwind Crown Office (synthetic)").length).toBeGreaterThan(0);
    expect(screen.getByText(/Verified identity person-1/)).toBeInTheDocument();
    expect(screen.getByText("dev-owner", { exact: false })).toBeInTheDocument();
    expect(screen.getByTestId("fingerprint")).toHaveTextContent(SHA.slice(0, 16));
    expect(screen.getByText("Creates one organization membership")).toBeInTheDocument();
  });

  it("requires a justification and an explicit review, sends the reviewed fingerprint and shows 'Approval recorded — 1 of 2' only after the BFF confirms", async () => {
    const calls = review((c) => (c.path.endsWith("/approvals") ? { status: 200, body: response({}) } : undefined));
    const user = userEvent.setup();
    render(<App />);
    const approve = await screen.findByRole("button", { name: "Approve" });
    expect(approve).toBeDisabled();
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "Verified with the owner by phone.");
    expect(approve).toBeEnabled();
    await user.click(approve);
    const confirm = await screen.findByRole("button", { name: "Record my approval" });
    expect(confirm).toBeDisabled(); // the operator must tick the review confirmation
    expect(screen.queryByText(/Approval recorded/)).not.toBeInTheDocument();
    await user.click(screen.getByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(confirm);
    expect(await screen.findByText("Approval recorded — 1 of 2")).toBeInTheDocument();
    const post = calls.find((c) => c.path.endsWith("/approvals"))!;
    expect(post.body).toEqual({ expectedStatementSha256: SHA, justification: "Verified with the owner by phone." });
    expect(post.csrf).toBe("csrf-token-1");
    // Nothing but the fingerprint and justification is ever sent: no operator, evidence or key.
    expect(Object.keys(post.body!).sort()).toEqual(["expectedStatementSha256", "justification"]);
  });

  it("shows 'Membership approved — 2 of 2' after a valid second approval", async () => {
    review((c) => (c.path.endsWith("/approvals") ? { status: 200, body: response({ outcome: "Approved", approvalsRecorded: 2 }) } : undefined));
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Second approval after review.");
    await user.click(screen.getByRole("button", { name: "Approve" }));
    await user.click(await screen.findByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(screen.getByRole("button", { name: "Record my approval" }));
    expect(await screen.findByText("Membership approved — 2 of 2")).toBeInTheDocument();
  });

  it("does not offer approval to an operator who already approved, and says why", async () => {
    const mine = item(1, { approvalStatus: { recorded: 1, required: 2, approvedByMe: true, awaitingOtherOperator: false, pendingApprovalExpiresOn: null } });
    window.history.replaceState(null, "", `/platform/approvals/${ORG}/${mine.requestId}`);
    installFetch((c) =>
      c.path === "/session"
        ? { status: 200, body: sessionBody() }
        : c.method === "GET" && c.path.includes("/membership-requests/")
          ? { status: 200, body: detailOf(mine, { myEligibility: { canApprove: false, canReject: true, canWithdraw: true, refusalCode: "OPERATOR_ALREADY_APPROVED", refusalDetail: null } }) }
          : undefined,
    );
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Changed my mind about this.");
    expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled();
    expect(screen.getByText(/Approve is unavailable: A second approval must come from a different operator/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw my approval" })).toBeEnabled();
  });

  it("withdraws only after confirmation and refreshes the request", async () => {
    const mine = item(1, { approvalStatus: { recorded: 1, required: 2, approvedByMe: true, awaitingOtherOperator: false, pendingApprovalExpiresOn: null } });
    window.history.replaceState(null, "", `/platform/approvals/${ORG}/${mine.requestId}`);
    let fetchedDetail = 0;
    const calls = installFetch((c) => {
      if (c.path === "/session") return { status: 200, body: sessionBody() };
      if (c.method === "GET" && c.path.includes("/membership-requests/")) { fetchedDetail++; return { status: 200, body: detailOf(mine, { myEligibility: { canApprove: false, canReject: true, canWithdraw: true, refusalCode: null, refusalDetail: null } }) }; }
      if (c.path.endsWith("/approvals/withdraw")) return { status: 200, body: response({ outcome: "Withdrawn", approvalsRecorded: 0, decision: "withdraw" }) };
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Approved the wrong request by mistake.");
    await user.click(screen.getByRole("button", { name: "Withdraw my approval" }));
    expect(calls.some((c) => c.path.endsWith("/approvals/withdraw"))).toBe(false); // confirmation first
    await user.click(await screen.findByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Withdraw my approval" }));
    expect(await screen.findByText("Approval withdrawn")).toBeInTheDocument();
    await waitFor(() => expect(fetchedDetail).toBe(2));
  });

  it("rejects with a justification and explicit confirmation and shows the result", async () => {
    const calls = review((c) => (c.path.endsWith("/reject") ? { status: 200, body: response({ outcome: "Rejected", decision: "reject", approvalsRecorded: 0 }) } : undefined));
    const user = userEvent.setup();
    render(<App />);
    expect(await screen.findByRole("button", { name: "Reject" })).toBeDisabled();
    await user.type(screen.getByRole("textbox", { name: /Your justification/ }), "The owner could not confirm this person.");
    await user.click(screen.getByRole("button", { name: "Reject" }));
    const confirm = await screen.findByRole("button", { name: "Reject request" });
    expect(confirm).toBeDisabled();
    await user.click(screen.getByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(confirm);
    expect(await screen.findByText("Request rejected")).toBeInTheDocument();
    expect(calls.find((c) => c.path.endsWith("/reject"))?.body).toEqual({ expectedStatementSha256: SHA, justification: "The owner could not confirm this person." });
  });

  it("refuses a changed request, shows nothing as recorded and reloads the current request", async () => {
    let details = 0;
    window.history.replaceState(null, "", path);
    installFetch((c) => {
      if (c.path === "/session") return { status: 200, body: sessionBody() };
      if (c.method === "GET" && c.path.includes("/membership-requests/")) { details++; return { status: 200, body: detailOf(request, { statementSha256: details > 1 ? "c".repeat(64) : SHA }) }; }
      if (c.path.endsWith("/approvals")) return problem(409, "APPROVAL_STATEMENT_CHANGED");
      return undefined;
    });
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Verified with the owner by phone.");
    await user.click(screen.getByRole("button", { name: "Approve" }));
    await user.click(await screen.findByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(screen.getByRole("button", { name: "Record my approval" }));
    expect(await screen.findByText("The request changed")).toBeInTheDocument();
    expect(screen.queryByText(/Approval recorded/)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("fingerprint")).toHaveTextContent("c".repeat(16)));
    expect(screen.getAllByRole("button", { name: "Reload request" }).length).toBeGreaterThan(0);
  });

  it("disables decisions for an expired or already decided request", async () => {
    const expired = item(1, { expiresOn: "2020-01-01T00:00:00Z" });
    window.history.replaceState(null, "", `/platform/approvals/${ORG}/${expired.requestId}`);
    installFetch((c) => (c.path === "/session" ? { status: 200, body: sessionBody() } : c.method === "GET" && c.path.includes("/membership-requests/") ? { status: 200, body: detailOf(expired, { myEligibility: { canApprove: false, canReject: false, canWithdraw: false, refusalCode: "MEMBERSHIP_REQUEST_EXPIRED", refusalDetail: null } }) } : undefined));
    render(<App />);
    expect(await screen.findByRole("button", { name: "Approve" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reject" })).toBeDisabled();
    expect(screen.getByText(/Approve is unavailable: This request has expired/)).toBeInTheDocument();
  });

  it("shows a fail-closed message for a closed platform plane and ends nothing", async () => {
    review((c) => (c.path.endsWith("/approvals") ? problem(423, "PLATFORM_JOURNAL_BEHIND_ARCHIVE") : undefined));
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Verified with the owner by phone.");
    await user.click(screen.getByRole("button", { name: "Approve" }));
    await user.click(await screen.findByRole("checkbox", { name: /I have reviewed/ }));
    await user.click(screen.getByRole("button", { name: "Record my approval" }));
    expect(await screen.findByText("Platform operations are closed")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Review request" })).toBeInTheDocument();
  });

  it("moves keyboard focus into the confirmation dialog and back to the opener on cancel", async () => {
    review(() => undefined);
    const user = userEvent.setup();
    render(<App />);
    await user.type(await screen.findByRole("textbox", { name: /Your justification/ }), "Verified with the owner by phone.");
    const approve = screen.getByRole("button", { name: "Approve" });
    approve.focus();
    await user.keyboard("{Enter}");
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toContainElement(document.activeElement as HTMLElement);
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(approve).toHaveFocus();
  });
});
