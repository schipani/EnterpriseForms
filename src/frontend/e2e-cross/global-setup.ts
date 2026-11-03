import type { FullConfig } from "@playwright/test";
import consoleSetup from "../e2e-console/global-setup";
import businessSetup from "../e2e/global-setup";

/** Both isolation proofs: the business origin must be the isolated stack, and the console must front the isolated console with its test operators. */
export default async function globalSetup(config: FullConfig): Promise<void> {
  await businessSetup(config);
  await consoleSetup({ ...config, projects: [{ ...config.projects[0]!, use: { baseURL: process.env["E2E_CONSOLE_BASE_URL"] } }] } as FullConfig);
}
