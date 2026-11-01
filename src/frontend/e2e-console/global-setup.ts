import type { FullConfig } from "@playwright/test";

const SECTEST_SUBJECTS = ["sectest-console-operator-dana", "sectest-console-operator-ellis", "sectest-console-operator-frankie"];

/** Refuses the whole run unless the console behind the browser's base URL is the isolated security-test console with its registered operators. GET requests and one sign-in/out only. */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const baseUrl = config.projects[0]?.use.baseURL;
  if (!baseUrl) {
    throw new Error("Console browser tests refused to start: no base URL is configured.");
  }
  // The Vite server is started after global setup in some Playwright versions; wait for it, then ask through it.
  const listed = await getJson(baseUrl, "/console-api/v1/session/development-operators");
  const subjects = (listed["operators"] as Array<{ subjectIdentifier: string }>).map((o) => o.subjectIdentifier).sort();
  if (JSON.stringify(subjects) !== JSON.stringify([...SECTEST_SUBJECTS].sort())) {
    throw new Error(`Console browser tests refused to start: ${baseUrl} offers operators ${subjects.join(", ")}, which are not the isolated security-test operators. They decide requests and may run only against the isolated stack.`);
  }
  const ready = await getJson(baseUrl, "/console-api/health/ready");
  if (ready["status"] !== "ready") {
    throw new Error(`Console browser tests refused to start: the console is not ready (${JSON.stringify(ready)}).`);
  }
  // The operators are registered in the Authority by the StageB harness (PlatformConsoleStackTests). Prove that now, not mid-spec.
  const response = await fetch(new URL("/console-api/v1/session/development-login", baseUrl), {
    method: "POST",
    headers: { "Content-Type": "application/json", Origin: baseUrl },
    body: JSON.stringify({ subjectIdentifier: SECTEST_SUBJECTS[0] }),
  });
  if (!response.ok) {
    throw new Error(`Console browser tests refused to start: operator ${SECTEST_SUBJECTS[0]} cannot sign in (${response.status}). Register the isolated operators first: dotnet test tests/integration/EnterpriseForms.StageBTests --filter-class "*PlatformConsoleStackTests".`);
  }
}

async function getJson(baseUrl: string, path: string): Promise<Record<string, unknown>> {
  let last = "";
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const response = await fetch(new URL(path, baseUrl), { headers: { Origin: baseUrl } });
      if (response.ok) {
        return (await response.json()) as Record<string, unknown>;
      }
      last = `HTTP ${response.status}`;
    } catch (error) {
      last = String(error);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`Console browser tests refused to start: ${path} did not answer through ${baseUrl} (${last}).`);
}
