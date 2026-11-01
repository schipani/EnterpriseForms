import { defineConfig, devices } from "@playwright/test";

/**
 * Platform Console browser tests (C8 M2 Stage 4). They sign operators in and decide membership requests, so they run ONLY against
 * the isolated security-test stack: a Vite server that Playwright starts on a dedicated port proxies /console-api to the sectest
 * console (CONSOLE_TARGET, default 127.0.0.1:5192). The persistent development console (8092), its web app (8095) and the
 * developer Vite port (5195) are refused here, before anything starts, and the global setup proves through the browser's own base
 * URL that the console fronts the isolated stack (sectest operators only, no development operators). `reuseExistingServer` is
 * false, so an owner's running server is never reused.
 */
const port = process.env["E2E_CONSOLE_PORT"] ?? "5198";
const baseURL = process.env["E2E_CONSOLE_BASE_URL"] ?? `http://127.0.0.1:${port}`;
const target = process.env["CONSOLE_TARGET"] ?? "http://127.0.0.1:5192";
const forbiddenPorts = new Set(["8092", "8095", "5195", "5174", "5173", "8091", "5280"]);
for (const [label, value] of [["E2E_CONSOLE_BASE_URL", baseURL], ["CONSOLE_TARGET", target]] as const) {
  if (forbiddenPorts.has(new URL(value).port)) {
    throw new Error(`Platform Console browser tests refused to start: ${label} ${value} is a persistent development endpoint. Use the isolated security-test console (5192).`);
  }
}

export default defineConfig({
  testDir: "./e2e-console",
  testMatch: /.*\.spec\.ts$/,
  globalSetup: "./e2e-console/global-setup.ts",
  timeout: 120_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["junit", { outputFile: "../../artifacts/stage-b/playwright-console-junit.xml" }]],
  use: { baseURL, trace: "retain-on-failure", screenshot: "only-on-failure" },
  webServer: {
    command: `npm run dev -w apps/platform-console -- --host 127.0.0.1 --port ${port} --strictPort`,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { CONSOLE_TARGET: target, PLATFORM_CONSOLE_PORT: port },
  },
  projects: [
    { name: "console-desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } }, testIgnore: /responsive\.spec\.ts/ },
    { name: "console-responsive", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } }, testMatch: /responsive\.spec\.ts/ },
  ],
});
