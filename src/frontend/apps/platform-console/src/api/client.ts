import type { components } from "./generated/platform-console-v1";

type S = components["schemas"];
export type ConsoleSessionView = S["ConsoleSessionView"];
export type DevelopmentOperatorList = S["DevelopmentOperatorList"];
export type RequestPage = S["PlatformMembershipRequestPage"];
export type RequestItem = S["PlatformMembershipRequestItem"];
export type RequestDetail = S["PlatformMembershipRequestDetail"];
export type ApprovalView = S["PlatformApprovalView"];
export type DecisionResponse = S["PlatformMembershipDecisionResponse"];

/** The only module that talks to the network: same-origin calls to the Platform Console BFF (cookie session + CSRF). */
export const API_ROOT = "/console-api/v1";

/** A refusal from the BFF (RFC 9457 body) or a transport failure (status 0, code NETWORK). Never carries tokens or evidence. */
export class ConsoleApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly detail: string,
    readonly retryable: boolean,
    readonly correlationId?: string,
  ) {
    super(`${code}: ${detail}`);
    this.name = "ConsoleApiError";
  }
}

/** Codes after which the BFF has ended the operator's session; the app returns to sign-in. */
const SESSION_ENDING = new Set(["SESSION_REQUIRED", "PLATFORM_OPERATOR_NOT_ACTIVE", "OPERATOR_IDENTITY_MISMATCH", "OPERATOR_SIGNING_KEY_MISMATCH"]);
export const endsSession = (error: unknown): error is ConsoleApiError => error instanceof ConsoleApiError && SESSION_ENDING.has(error.code);

// The CSRF token lives in memory only: it is returned by sign-in and by GET /session, never stored.
let csrfToken = "";
export const setCsrfToken = (value: string): void => {
  csrfToken = value;
};

async function request<T>(method: "GET" | "POST", path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (method !== "GET") {
    headers["X-CSRF-Token"] = csrfToken;
  }
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  let response: Response;
  try {
    response = await fetch(API_ROOT + path, { method, headers, credentials: "same-origin", cache: "no-store", ...(body !== undefined ? { body: JSON.stringify(body) } : {}) });
  } catch {
    throw new ConsoleApiError(0, "NETWORK", "The Platform Console could not be reached.", true);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  const text = await response.text();
  let parsed: Record<string, unknown> = {};
  try {
    parsed = text ? (JSON.parse(text) as Record<string, unknown>) : {};
  } catch {
    parsed = {};
  }
  if (!response.ok) {
    throw new ConsoleApiError(
      response.status,
      typeof parsed["errorCode"] === "string" ? parsed["errorCode"] : `HTTP_${response.status}`,
      typeof parsed["detail"] === "string" ? parsed["detail"] : "The request was refused.",
      parsed["retryable"] === true,
      typeof parsed["correlationId"] === "string" ? parsed["correlationId"] : undefined,
    );
  }
  return parsed as T;
}

export interface QueueQuery {
  q: string;
  status: string;
  cursor?: string | undefined;
  limit: number;
}

export interface DecisionInput {
  expectedStatementSha256: string;
  justification: string;
}

export type DecisionKind = "approve" | "withdraw" | "reject";
const decisionPath: Record<DecisionKind, string> = { approve: "/approvals", withdraw: "/approvals/withdraw", reject: "/reject" };
const requestPath = (organizationId: string, requestId: string) => `/organizations/${encodeURIComponent(organizationId)}/membership-requests/${encodeURIComponent(requestId)}`;

export const consoleApi = {
  developmentOperators: () => request<DevelopmentOperatorList>("GET", "/session/development-operators"),
  signIn: (subjectIdentifier: string) => request<ConsoleSessionView>("POST", "/session/development-login", { subjectIdentifier }),
  session: () => request<ConsoleSessionView>("GET", "/session"),
  signOut: () => request<void>("POST", "/session/sign-out", {}),
  queue: ({ q, status, cursor, limit }: QueueQuery) => {
    const params = new URLSearchParams({ status, limit: String(limit) });
    if (q.trim()) {
      params.set("q", q.trim());
    }
    if (cursor) {
      params.set("cursor", cursor);
    }
    return request<RequestPage>("GET", `/membership-requests?${params.toString()}`);
  },
  detail: (organizationId: string, requestId: string) => request<RequestDetail>("GET", requestPath(organizationId, requestId)),
  decide: (kind: DecisionKind, organizationId: string, requestId: string, input: DecisionInput) =>
    request<DecisionResponse>("POST", requestPath(organizationId, requestId) + decisionPath[kind], { expectedStatementSha256: input.expectedStatementSha256, justification: input.justification }),
};
