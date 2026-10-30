import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import type { FullConfig } from "@playwright/test";
import { assertIsolatedStack, knownPersistentOrganizationIds, VERIFIED_ORIGIN_VARIABLE } from "./isolation";

/** Runs before any spec: refuses the whole run unless the target proves to be the isolated test stack (see isolation.ts). */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const baseUrl = config.projects[0]?.use.baseURL;
  if (!baseUrl) {
    throw new Error("Browser tests refused to start: no base URL is configured.");
  }

  const result = await assertIsolatedStack({
    baseUrl,
    apiTarget: process.env["API_TARGET"],
    expectedStackIdentity: process.env["E2E_EXPECTED_STACK_IDENTITY"],
    persistentOrganizationIds: knownPersistentOrganizationIds(),
  });
  process.env[VERIFIED_ORIGIN_VARIABLE] = result.origin;
  const evidence = resolve(process.cwd(), "../../artifacts/stage-b/e2e-isolation.json");
  mkdirSync(dirname(evidence), { recursive: true });
  writeFileSync(evidence, JSON.stringify({ verifiedOn: new Date().toISOString(), ...result }, null, 2));
  console.log(`[e2e isolation] ${result.origin} verified as stack ${result.stackIdentity} (organizations ${result.organizationIds.join(", ")})`);
}
