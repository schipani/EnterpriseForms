// @vitest-environment node
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { assertIsolatedStack, assertNotPersistentEndpoint, assertVerifiedOrigin, IsolationError } from "./isolation";

/**
 * C7 M4 incident regression: the browser-test guard against a server that behaves like the persistent development stack
 * (Development environment: no stack-identity route; serves the persistent organization) and against look-alikes. Every
 * request the guard makes is recorded: it must refuse before any non-GET request reaches the server.
 */
const PERSISTENT_ORG = "01a16640-87f6-7488-a1f1-664db54fbff3";
const ISOLATED_ORG = "01a18801-f43f-7f9c-b6c6-f721abfb9de7";

interface FakeStack {
  url: string;
  requests: { method: string; path: string }[];
}

let server: Server | undefined;

afterEach(async () => {
  await new Promise<void>((resolve) => (server ? server.close(() => resolve()) : resolve()));
  server = undefined;
});

async function fakeStack(behaviour: { identity?: object | null; identityStatus?: number; organization: string }): Promise<FakeStack> {
  const requests: FakeStack["requests"] = [];
  server = createServer((request: IncomingMessage, response: ServerResponse) => {
    requests.push({ method: request.method ?? "", path: request.url ?? "" });
    const json = (status: number, body: unknown) => {
      response.writeHead(status, { "content-type": "application/json" });
      response.end(JSON.stringify(body));
    };
    if (request.url === "/api/v1/testing/stack-identity") {
      return behaviour.identity ? json(behaviour.identityStatus ?? 200, behaviour.identity) : json(404, { title: "Not Found" });
    }

    if (request.url === "/api/v1/session/development-personas") {
      return json(200, [{ personaKey: "owner", displayName: "Avery Owner", organizationId: behaviour.organization, organizationDisplayName: "Northwind", organizationRoles: ["organization_owner"] }]);
    }

    return json(request.method === "GET" ? 200 : 201, {}); // any write would "succeed" here, so the test can see it happened
  });
  await new Promise<void>((resolve) => server!.listen(0, "127.0.0.1", () => resolve()));
  return { url: `http://127.0.0.1:${(server!.address() as AddressInfo).port}`, requests };
}

const isolatedIdentity = { apiEnvironment: "Testing", apiStackIdentity: "enterpriseforms-security-test", authorityEnvironment: "Testing", authorityStackIdentity: "enterpriseforms-security-test", isolated: true };

describe("browser-test isolation guard", () => {
  it("refuses the original misrouting (a dev server proxying to the persistent Development stack) before any write", async () => {
    const stack = await fakeStack({ identity: null, organization: PERSISTENT_ORG });
    await expect(assertIsolatedStack({ baseUrl: stack.url, persistentOrganizationIds: [PERSISTENT_ORG] })).rejects.toBeInstanceOf(IsolationError);
    expect(stack.requests.every((r) => r.method === "GET")).toBe(true);
    expect(stack.requests.map((r) => r.path)).toEqual(["/api/v1/testing/stack-identity"]);
  });

  it("refuses a Testing look-alike that serves the persistent organization", async () => {
    const stack = await fakeStack({ identity: isolatedIdentity, organization: PERSISTENT_ORG });
    await expect(assertIsolatedStack({ baseUrl: stack.url, persistentOrganizationIds: [PERSISTENT_ORG] })).rejects.toThrow(/persistent development organization/);
    expect(stack.requests.every((r) => r.method === "GET")).toBe(true);
  });

  it.each([
    ["a Development API", { ...isolatedIdentity, apiEnvironment: "Development" }],
    ["a Development Authority", { ...isolatedIdentity, authorityEnvironment: "Development" }],
    ["another stack identity", { ...isolatedIdentity, apiStackIdentity: "someone-else", authorityStackIdentity: "someone-else" }],
    ["mismatched API and Authority identities", { ...isolatedIdentity, authorityStackIdentity: "other-authority" }],
    ["an unconfirmed claim", { ...isolatedIdentity, isolated: false }],
  ])("refuses %s", async (_, identity) => {
    const stack = await fakeStack({ identity, organization: ISOLATED_ORG });
    await expect(assertIsolatedStack({ baseUrl: stack.url })).rejects.toBeInstanceOf(IsolationError);
    expect(stack.requests.every((r) => r.method === "GET")).toBe(true);
  });

  it("refuses an identity route that does not answer 200", async () => {
    const stack = await fakeStack({ identity: isolatedIdentity, identityStatus: 503, organization: ISOLATED_ORG });
    await expect(assertIsolatedStack({ baseUrl: stack.url })).rejects.toBeInstanceOf(IsolationError);
  });

  it("refuses an unreachable target", async () => {
    await expect(assertIsolatedStack({ baseUrl: "http://127.0.0.1:9" })).rejects.toThrow(/unreachable/);
  });

  it("refuses persistent endpoints without contacting them", () => {
    for (const url of ["http://127.0.0.1:8091", "http://localhost:5280/api", "http://127.0.0.1:5080"]) {
      expect(() => assertNotPersistentEndpoint("API_TARGET", url)).toThrow(IsolationError);
    }

    expect(() => assertNotPersistentEndpoint("API_TARGET", "http://127.0.0.1:5180")).not.toThrow();
  });

  it("accepts the isolated stack and reports what it verified", async () => {
    const stack = await fakeStack({ identity: isolatedIdentity, organization: ISOLATED_ORG });
    const result = await assertIsolatedStack({ baseUrl: stack.url, persistentOrganizationIds: [PERSISTENT_ORG] });
    expect(result).toEqual({ origin: stack.url, stackIdentity: "enterpriseforms-security-test", organizationIds: [ISOLATED_ORG] });
    expect(stack.requests.every((r) => r.method === "GET")).toBe(true);
  });

  it("signIn refuses any origin the global setup did not verify", () => {
    expect(() => assertVerifiedOrigin("http://127.0.0.1:5199/login", {})).toThrow(/did not run/);
    expect(() => assertVerifiedOrigin("http://127.0.0.1:5173/login", { E2E_ISOLATION_VERIFIED_ORIGIN: "http://127.0.0.1:5199" })).toThrow(/only http:\/\/127.0.0.1:5199 was verified/);
    expect(() => assertVerifiedOrigin("http://127.0.0.1:5199/login", { E2E_ISOLATION_VERIFIED_ORIGIN: "http://127.0.0.1:5199" })).not.toThrow();
  });
});
