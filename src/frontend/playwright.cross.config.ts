import { defineConfig, devices } from "@playwright/test";
import { assertNotPersistentEndpoint } from "./e2e/isolation";

/**
 * Cross-application regression (C8 M2 Stage 5): the business app (People page) and the Platform Console in one run, both on the
 * isolated security-test stack: Playwright starts two Vite servers on dedicated ports (business 5199 -> API 5180, console 5198 ->
 * console 5192). The persistent development endpoints (8091, 8092, 8095, 5173, 5195, 5280) are refused here, and the global setup
 * runs BOTH isolation proofs (business stack identity, console sectest operators). Real Authority, PostgreSQL, SpiceDB, Vault.
 */
const businessPort = process.env["E2E_PORT"] ?? "5199";
const consolePort = process.env["E2E_CONSOLE_PORT"] ?? "5198";
const apiTarget = process.env["API_TARGET"] ?? "http://127.0.0.1:5180";
const consoleTarget = process.env["CONSOLE_TARGET"] ?? "http://127.0.0.1:5192";
for (const [label, value] of [["API_TARGET", apiTarget], ["CONSOLE_TARGET", consoleTarget], ["business base URL", `http://127.0.0.1:${businessPort}`], ["console base URL", `http://127.0.0.1:${consolePort}`]] as const) {
  assertNotPersistentEndpoint(label, value);
  if (["8092", "8095", "5195", "5174", "5173"].includes(new URL(value).port)) {
    throw new Error(`Cross-application tests refused to start: ${label} ${value} is a persistent development endpoint.`);
  }
}
process.env["E2E_CONSOLE_BASE_URL"] = `http://127.0.0.1:${consolePort}`;

export default defineConfig({
  testDir: "./e2e-cross",
  testMatch: /.*\.spec\.ts$/,
  globalSetup: "./e2e-cross/global-setup.ts",
  timeout: 240_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["junit", { outputFile: "../../artifacts/stage-b/playwright-cross-junit.xml" }]],
  use: { baseURL: `http://127.0.0.1:${businessPort}`, trace: "retain-on-failure", screenshot: "only-on-failure", ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } },
  webServer: [
    { command: `npm run dev -w apps/web -- --host 127.0.0.1 --port ${businessPort} --strictPort`, url: `http://127.0.0.1:${businessPort}`, reuseExistingServer: false, timeout: 120_000, env: { API_TARGET: apiTarget } },
    { command: `npm run dev -w apps/platform-console -- --host 127.0.0.1 --port ${consolePort} --strictPort`, url: `http://127.0.0.1:${consolePort}`, reuseExistingServer: false, timeout: 120_000, env: { CONSOLE_TARGET: consoleTarget, PLATFORM_CONSOLE_PORT: consolePort } },
  ],
});
