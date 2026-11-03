import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Provisions the organization key for an approved onboarding request, as the key custodian would, using the isolated security-test
 * stack's dedicated custody credential (create-only HMAC keys; it cannot read, rotate, export or sign). The browser tests never hold
 * that credential in a page: this Node helper is the single place that acts as custody, through `docker compose exec`, exactly like
 * the .NET harness (OrganizationOnboardingStackTests.ProvisionKey). The key name is the one the Authority recorded for the request
 * (read-only query); nothing else is changed. Refuses anything but the sectest project.
 */
const dockerDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../../infrastructure/docker");
const custodyToken = "sectest-organization-key-custody-token-0001";

function compose(args: string[]): string {
  const files = ["-f", "compose.yaml", "-f", "compose.security-test.yaml"];
  const overlay = process.env["SECTEST_EXTRA_COMPOSE_FILE"];
  if (overlay) {
    files.push("-f", overlay);
  }
  return execFileSync("docker", ["compose", ...files, "--env-file", ".env", ...args], { cwd: dockerDirectory, encoding: "utf8", timeout: 120_000 });
}

function shell(service: string, script: string): string {
  const encoded = Buffer.from(script, "utf8").toString("base64");
  return compose(["exec", "-T", service, "sh", "-c", `echo ${encoded} | base64 -d | sh`]);
}

export async function provisionOrganizationKey(requestId: string): Promise<void> {
  if (!/^[0-9a-f-]{36}$/.test(requestId)) {
    throw new Error(`not an onboarding request id: ${requestId}`);
  }
  const project = compose(["config", "--format", "json"]);
  if (JSON.parse(project).name !== "enterpriseforms-sectest") {
    throw new Error("key custody helper refused: the compose project is not the isolated security-test stack");
  }
  const sql = `SELECT integrity_key_name FROM organization_onboarding_requests WHERE organization_onboarding_request_id = '${requestId}' AND onboarding_state_name = 'AwaitingKeyCustody';`;
  const keyName = shell(
    "authority-db",
    `echo ${Buffer.from(sql).toString("base64")} | base64 -d | PGPASSWORD="$POSTGRES_PASSWORD" PGOPTIONS='-c default_transaction_read_only=on -c search_path=authority' psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -qAt`,
  ).trim();
  if (!/^org-[0-9a-f]{32}-row-integrity-v1$/.test(keyName)) {
    throw new Error(`request ${requestId} is not awaiting key custody (got '${keyName}')`);
  }
  try {
    shell("vault", `VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='${custodyToken}' vault token lookup >/dev/null`);
  } catch {
    compose(["up", "--no-deps", "vault-custody-init"]);
  }
  shell("vault", `VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='${custodyToken}' vault write transit/keys/${keyName} type=hmac key_size=32 exportable=false allow_plaintext_backup=false >/dev/null`);
}
