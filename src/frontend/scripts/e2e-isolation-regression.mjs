#!/usr/bin/env node
// C7 M4 incident regression at the Playwright level (docs/PHASE_C7_STATUS.md §31). Runs the real `playwright test` twice
// against a fake server that behaves like the misrouted target of the incident (a dev server proxying to the persistent
// Development stack: no Testing identity route, the persistent organization, every write "succeeds") and records every
// request it receives:
//   A. the base URL points at the fake server         -> the global setup must refuse; the fake sees GET requests only;
//   B. the fake server already occupies the E2E port  -> Playwright must refuse to start (no reuse); the fake sees no write.
// Exit code 0 only when both runs fail closed before the first write. Usage (from src/frontend): node scripts/e2e-isolation-regression.mjs
import { spawn } from "node:child_process";
import { createServer } from "node:http";

const PERSISTENT_ORG = "01a16640-87f6-7488-a1f1-664db54fbff3";

function fake(port) {
  const requests = [];
  const server = createServer((request, response) => {
    requests.push(`${request.method} ${request.url}`);
    response.writeHead(request.url === "/api/v1/testing/stack-identity" ? 404 : request.method === "GET" ? 200 : 201, { "content-type": request.url?.startsWith("/api/") ? "application/json" : "text/html" });
    response.end(request.url === "/api/v1/session/development-personas" ? JSON.stringify([{ personaKey: "owner", organizationId: PERSISTENT_ORG }]) : request.url?.startsWith("/api/") ? "{}" : "<html><body>Sign in</body></html>");
  });
  return new Promise((resolve) => server.listen(port, "127.0.0.1", () => resolve({ server, requests, port: server.address().port })));
}

function playwright(env) {
  // Asynchronous: the fake server runs in this process and must keep answering while Playwright runs.
  return new Promise((resolve) => {
    const child = spawn("npx", ["playwright", "test", "e2e/branding.spec.ts", "--reporter=line"], { env: { ...process.env, ...env }, shell: true });
    let output = "";
    child.stdout.on("data", (d) => (output += d));
    child.stderr.on("data", (d) => (output += d));
    const timer = setTimeout(() => child.kill(), 240_000);
    child.on("close", (status) => {
      clearTimeout(timer);
      resolve({ status, output });
    });
  });
}

const writes = (requests) => requests.filter((r) => !r.startsWith("GET "));
let failed = false;

// A. misrouted base URL
const a = await fake(0);
const runA = await playwright({ E2E_BASE_URL: `http://127.0.0.1:${a.port}`, E2E_PORT: "5198" });
a.server.close();
const okA = runA.status !== 0 && /Browser tests refused to start/.test(runA.output) && writes(a.requests).length === 0 && !a.requests.some((r) => r.includes("/login"));
console.log(`A. misrouted base URL: exit ${runA.status}; requests to the fake: ${JSON.stringify(a.requests)} -> ${okA ? "PASS (refused before any write or page load)" : "FAIL"}`);
failed ||= !okA;

// B. the dedicated E2E port is already taken by a server connected elsewhere
const b = await fake(5197);
const runB = await playwright({ E2E_PORT: "5197" });
b.server.close();
const okB = runB.status !== 0 && /already used|refused to start/i.test(runB.output) && writes(b.requests).length === 0;
console.log(`B. occupied E2E port: exit ${runB.status}; requests to the fake: ${JSON.stringify(b.requests)} -> ${okB ? "PASS (not reused, no write)" : "FAIL"}`);
failed ||= !okB;

// C. persistent endpoints are refused by the configuration itself
const runC = await playwright({ API_TARGET: "http://127.0.0.1:5280" });
const okC = runC.status !== 0 && /persistent development endpoint/.test(runC.output);
console.log(`C. API_TARGET on the persistent API port: exit ${runC.status} -> ${okC ? "PASS (refused at configuration load)" : "FAIL"}`);
failed ||= !okC;

if (failed) {
  console.log(runA.output.slice(-1500));
  console.log(runB.output.slice(-1500));
}

process.exit(failed ? 1 : 0);
