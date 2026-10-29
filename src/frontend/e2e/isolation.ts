import { existsSync, readdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

/**
 * Browser-test isolation guard (C7 M4 incident, docs/PHASE_C7_STATUS.md §28.5/§31). Browser specs create and change data,
 * so before the first test runs the target must prove — through the very base URL the browser will use — that it is an
 * isolated test stack:
 *  1. the base URL and API target are not a known persistent-environment endpoint;
 *  2. `GET /api/v1/testing/stack-identity` answers 200 (the route exists only in the Testing environment), the API and the
 *     Authority it reaches server-side both run Testing and report the expected stack identity;
 *  3. no seeded organization is a known persistent development organization (from backup manifests or configuration).
 * Only GET requests are made, so a misrouted run stops before its first write. Any doubt fails closed.
 */

export const DEFAULT_EXPECTED_STACK_IDENTITY = "enterpriseforms-security-test";

/** Ports of the persistent development stack (web 8091, API 5280) and of the retired development profile (API 5080). */
export const PERSISTENT_PORTS = new Set(["8091", "5280", "5080"]);

export class IsolationError extends Error {
  constructor(message: string) {
    super(`Browser tests refused to start: ${message}. They create and change data and may run only against the isolated security-test stack (docs/PHASE_C7_STATUS.md §31).`);
    this.name = "IsolationError";
  }
}

export interface IsolationOptions {
  baseUrl: string;
  apiTarget?: string | undefined;
  expectedStackIdentity?: string | undefined;
  persistentOrganizationIds?: Iterable<string>;
  fetchImpl?: typeof fetch;
}

export interface IsolationResult {
  origin: string;
  stackIdentity: string;
  organizationIds: string[];
}

function portOf(url: URL): string {
  return url.port || (url.protocol === "https:" ? "443" : "80");
}

export function assertNotPersistentEndpoint(label: string, value: string | undefined): void {
  if (!value) {
    return;
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new IsolationError(`${label} "${value}" is not a URL`);
  }

  if (PERSISTENT_PORTS.has(portOf(url))) {
    throw new IsolationError(`${label} ${url.origin} is a persistent development endpoint`);
  }
}

/** Persistent development organization ids: E2E_PERSISTENT_ORGANIZATION_IDS plus every devstack backup manifest found locally (they hold no secrets). */
export function knownPersistentOrganizationIds(env: NodeJS.ProcessEnv = process.env): Set<string> {
  const ids = new Set((env["E2E_PERSISTENT_ORGANIZATION_IDS"] ?? "").split(",").map((v) => v.trim().toLowerCase()).filter(Boolean));
  const root = env["DEVSTACK_BACKUP_ROOT"] ?? join(homedir(), "Documents", "EnterpriseForms-dev-backups");
  try {
    if (existsSync(root)) {
      for (const project of readdirSync(root)) {
        for (const backup of readdirSync(join(root, project))) {
          const manifest = join(root, project, backup, "manifest.json");
          if (existsSync(manifest)) {
            const organization = (JSON.parse(readFileSync(manifest, "utf8")) as { organization_id?: string }).organization_id;
            if (organization) {
              ids.add(organization.toLowerCase());
            }
          }
        }
      }
    }
  } catch {
    // An unreadable manifest never relaxes the guard; the stack-identity check below still has to pass.
  }

  return ids;
}

async function getJson(fetchImpl: typeof fetch, url: string): Promise<{ status: number; body: unknown }> {
  let response: Response;
  try {
    response = await fetchImpl(url, { method: "GET", headers: { accept: "application/json" }, redirect: "error" });
  } catch (error) {
    throw new IsolationError(`${url} is unreachable (${error instanceof Error ? error.message : String(error)})`);
  }

  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }

  return { status: response.status, body };
}

export async function assertIsolatedStack(options: IsolationOptions): Promise<IsolationResult> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const expected = options.expectedStackIdentity || DEFAULT_EXPECTED_STACK_IDENTITY;
  assertNotPersistentEndpoint("the base URL", options.baseUrl);
  assertNotPersistentEndpoint("API_TARGET", options.apiTarget);
  const origin = new URL(options.baseUrl).origin;

  const identity = await getJson(fetchImpl, `${origin}/api/v1/testing/stack-identity`);
  if (identity.status !== 200 || typeof identity.body !== "object" || identity.body === null) {
    throw new IsolationError(`${origin} did not prove an isolated test stack (GET /api/v1/testing/stack-identity answered ${identity.status}; that route exists only in the Testing environment)`);
  }

  const proof = identity.body as { apiEnvironment?: string; apiStackIdentity?: string; authorityEnvironment?: string; authorityStackIdentity?: string; isolated?: boolean };
  if (proof.isolated !== true || proof.apiEnvironment !== "Testing" || proof.authorityEnvironment !== "Testing" || proof.apiStackIdentity !== expected || proof.authorityStackIdentity !== expected) {
    throw new IsolationError(`${origin} reported API ${proof.apiEnvironment}/${proof.apiStackIdentity} and Authority ${proof.authorityEnvironment}/${proof.authorityStackIdentity}, expected Testing/${expected} for both`);
  }

  const personas = await getJson(fetchImpl, `${origin}/api/v1/session/development-personas`);
  if (personas.status !== 200 || !Array.isArray(personas.body)) {
    throw new IsolationError(`${origin} lists no synthetic personas (status ${personas.status})`);
  }

  const organizationIds = [...new Set((personas.body as { organizationId?: string }[]).map((p) => String(p.organizationId ?? "").toLowerCase()).filter(Boolean))];
  const persistent = new Set([...(options.persistentOrganizationIds ?? [])].map((v) => v.toLowerCase()));
  const clash = organizationIds.find((id) => persistent.has(id));
  if (clash) {
    throw new IsolationError(`${origin} serves the persistent development organization ${clash}`);
  }

  return { origin, stackIdentity: expected, organizationIds };
}

export const VERIFIED_ORIGIN_VARIABLE = "E2E_ISOLATION_VERIFIED_ORIGIN";

/** Called before any write path (signIn): the global setup must have verified exactly the origin this page uses. */
export function assertVerifiedOrigin(pageUrl: string, env: NodeJS.ProcessEnv = process.env): void {
  const verified = env[VERIFIED_ORIGIN_VARIABLE];
  if (!verified) {
    throw new IsolationError("the isolation global setup did not run or did not succeed");
  }

  if (new URL(pageUrl).origin !== verified) {
    throw new IsolationError(`the page is on ${new URL(pageUrl).origin}, but only ${verified} was verified as isolated`);
  }
}
