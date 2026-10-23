#!/usr/bin/env python3
"""Persistent development stack operator tool (Phase C6, decision C-47). DEVELOPMENT ONLY.

Runs the local trust-separated stack (infrastructure/docker/compose.yaml + compose.dev-persistent.yaml) under its own
Compose project with a persistent, sealed-at-rest Vault. Secrets (Vault unseal key and root token, the Authority's Vault
token, the per-environment business KEK) live in a custody file OUTSIDE the repository; nothing secret is printed,
passed on a command line or written into the working tree.

    python scripts/devstack/devstack.py init                 # first time only: build, initialize Vault, seed
    python scripts/devstack/devstack.py up                   # start (unseals Vault) -- after any restart or reboot
    python scripts/devstack/devstack.py unseal               # unseal only (e.g. after `docker restart` of vault)
    python scripts/devstack/devstack.py status
    python scripts/devstack/devstack.py stop                 # stop containers; volumes are kept
    python scripts/devstack/devstack.py rebuild [authority api worker web]  # new application images; Vault and data untouched
    python scripts/devstack/devstack.py backup               # consistent cold backup of every volume of the project
    python scripts/devstack/devstack.py restore-test DIR     # restore a backup into a DISPOSABLE project and verify it
    python scripts/devstack/devstack.py verify-persistence   # restart/kill/stop-start proofs against the live stack
    python scripts/devstack/devstack.py verify-workflow      # M1 lifecycle and restriction checks through the API
    python scripts/devstack/devstack.py prepare-acceptance   # synthetic Draft records for the owner's browser review

See docs/infrastructure/DEV_ENVIRONMENT_PERSISTENT_VAULT.md. Production signing uses Azure Managed HSM; nothing here is a
production control or Level 2 evidence.
"""
from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import http.cookiejar
import json
import os
import re
import secrets
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DOCKER_DIR = REPO / "infrastructure" / "docker"
DEFAULT_PROJECT = "enterpriseforms-dev"
RESTORE_SUFFIX = "-restoretest"
VAULT_IMAGE = "hashicorp/vault:1.20.4"  # also used as the busybox tar helper (already present locally)
KEY_PURPOSE = "row-integrity-v1"
PROBE_KEY = "devstack-persistence-probe"  # separate non-organization transit key; never used to sign records
PROBE_INPUT_B64 = base64.b64encode(b"enterpriseforms-dev vault persistence probe v1").decode()

# Projects this tool must never operate on: the retired development project (in-memory Vault, keys lost 2026-10-22) and
# the disposable security-test stack, which the test harness owns.
PROTECTED_PROJECTS = {"enterpriseforms", "enterpriseforms-sectest"}
# Organizations of the retired project. A new environment must never provision a signing key under their identities.
RETIRED_ORGANIZATION_IDS = {"01a1579b-a25b-7eaa-bf8e-62f0461624a0"}

PERSONAS = ("owner", "designer", "clerk", "reader")
# Long-running services; `up --wait` would treat the one-shot dev-seed exiting 0 as a failure, so it is run separately.
LONG_RUNNING = ("business-db", "authority-db", "spicedb-db", "spicedb", "vault", "archive-store", "azurite", "clamav", "authority", "api", "worker", "web")


class DevStackError(Exception):
    pass


# --------------------------------------------------------------------------------------------------------------------
# Custody (secrets) and state (non-secret) files, outside the repository
# --------------------------------------------------------------------------------------------------------------------

def custody_root() -> Path:
    return Path(os.environ.get("DEVSTACK_CUSTODY_ROOT") or Path.home() / ".enterpriseforms" / "dev-vault")


def custody_dir(project: str) -> Path:
    return custody_root() / project


def restrict(path: Path, directory: bool) -> None:
    """Owner-only access: NTFS ACL without inheritance on Windows, 0700/0600 elsewhere."""
    if os.name == "nt":
        grant = f"{os.environ.get('USERNAME', '')}:{'(OI)(CI)F' if directory else 'F'}"
        subprocess.run(["icacls", str(path), "/inheritance:r", "/grant:r", grant], check=True, capture_output=True)
    else:
        os.chmod(path, 0o700 if directory else 0o600)


def write_private_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    restrict(path.parent, directory=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(data, indent=2), encoding="utf-8")
    restrict(temporary, directory=False)
    os.replace(temporary, path)


def load_custody(project: str) -> dict:
    path = custody_dir(project) / "custody.json"
    if not path.exists():
        raise DevStackError(f"no custody file for '{project}' at {path}; run `devstack.py init` first (or restore the custody file from your secure copy)")
    return json.loads(path.read_text(encoding="utf-8"))


def load_state(project: str) -> dict:
    path = custody_dir(project) / "state.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def save_state(project: str, state: dict) -> None:
    write_private_json(custody_dir(project) / "state.json", state)


# --------------------------------------------------------------------------------------------------------------------
# Docker / Compose
# --------------------------------------------------------------------------------------------------------------------

def run(command: list[str], *, cwd: Path | None = None, env: dict | None = None, input_text: str | None = None,
        check: bool = True, timeout: int | None = None) -> subprocess.CompletedProcess:
    # Binary stdin: text mode on Windows would translate "\n" to "\r\n" and corrupt a token read with `read -r`.
    raw = subprocess.run(command, cwd=cwd, env=env, input=input_text.encode("utf-8") if input_text is not None else None,
                         capture_output=True, timeout=timeout)
    result = subprocess.CompletedProcess(raw.args, raw.returncode, raw.stdout.decode("utf-8", errors="replace"), raw.stderr.decode("utf-8", errors="replace"))
    if check and result.returncode != 0:
        # stdin is never echoed; stdout/stderr of docker/vault carry no secrets for the commands used here.
        raise DevStackError(f"command failed ({result.returncode}): {' '.join(command[:8])} ...\n{result.stderr.strip()[-2000:]}")
    return result


class Stack:
    """One Compose project of the persistent development stack."""

    def __init__(self, project: str, custody: dict | None, overrides: dict | None = None):
        if project in PROTECTED_PROJECTS:
            raise DevStackError(f"refusing to operate on protected project '{project}'")
        if not (DOCKER_DIR / ".env").exists():
            raise DevStackError("infrastructure/docker/.env is missing; copy env.example to .env (synthetic values)")
        self.project = project
        self.custody = custody or {}
        self.overrides = overrides or {}

    def env(self) -> dict:
        env = dict(os.environ)
        env["DEVSTACK_PROJECT"] = self.project
        env["DEVSTACK_VAULT_AUTHORITY_TOKEN"] = self.custody.get("authority_vault_token") or "pending-initialization"
        env["DEVSTACK_LOCAL_DEV_KEK_BASE64"] = self.custody.get("local_dev_kek_base64") or "pending-initialization"
        env.update(self.overrides)
        return env

    def compose(self, *args: str, input_text: str | None = None, check: bool = True, timeout: int | None = 900) -> subprocess.CompletedProcess:
        command = ["docker", "compose", "-p", self.project, "-f", "compose.yaml", "-f", "compose.dev-persistent.yaml", "--env-file", ".env", *args]
        return run(command, cwd=DOCKER_DIR, env=self.env(), input_text=input_text, check=check, timeout=timeout)

    def volumes(self) -> list[str]:
        out = run(["docker", "volume", "ls", "-q", "--filter", f"label=com.docker.compose.project={self.project}"]).stdout
        return sorted(v for v in out.split() if v)

    def env_file_value(self, key: str) -> str:
        for line in (DOCKER_DIR / ".env").read_text(encoding="utf-8").splitlines():
            if line.startswith(key + "="):
                return line.split("=", 1)[1].strip()
        raise DevStackError(f"{key} not set in .env")

    def api_base(self) -> str:
        return f"http://127.0.0.1:{self.overrides.get('DEVSTACK_API_PORT', '5280')}"

    def web_base(self) -> str:
        return f"http://127.0.0.1:{self.overrides.get('DEVSTACK_WEB_PORT', '8091')}"

    # ---- Vault -------------------------------------------------------------------------------------------------------

    def vault(self, script: str, token: str | None = None, check: bool = True) -> subprocess.CompletedProcess:
        """Runs a shell script in the vault container. A token travels only on stdin, never in argv or the environment."""
        if token is not None:
            script = "IFS= read -r VAULT_TOKEN; export VAULT_TOKEN; " + script
        return self.compose("exec", "-T", "vault", "sh", "-c", script, input_text=(token + "\n") if token else "", check=check, timeout=120)

    def vault_status(self) -> str:
        result = self.vault("vault status -format=json", check=False)
        try:
            status = json.loads(result.stdout)
        except json.JSONDecodeError:
            return "unreachable"
        if not status.get("initialized"):
            return "uninitialized"
        return "sealed" if status.get("sealed") else "unsealed"

    def wait_vault(self, wanted: set[str], seconds: int = 90) -> str:
        deadline = time.monotonic() + seconds
        status = "unreachable"
        while time.monotonic() < deadline:
            status = self.vault_status()
            if status in wanted:
                return status
            time.sleep(2)
        raise DevStackError(f"vault did not reach {sorted(wanted)} within {seconds}s (last status: {status})")

    def unseal(self) -> None:
        if self.wait_vault({"sealed", "unsealed", "uninitialized"}) == "uninitialized":
            raise DevStackError("vault is not initialized for this project; run `devstack.py init`")
        if self.vault_status() == "unsealed":
            return
        for key in self.custody["vault_unseal_keys_b64"]:
            self.compose("exec", "-T", "vault", "vault", "write", "-format=json", "sys/unseal", "key=-", input_text=key, timeout=60)
        self.wait_vault({"unsealed"}, 60)
        self.wait_vault_active()

    def wait_vault_active(self, seconds: int = 60) -> None:
        """After unseal the single Raft node must elect itself before requests succeed."""
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                status = json.loads(self.vault("vault status -format=json", check=False).stdout)
                if not status.get("sealed") and (not status.get("ha_enabled") or status.get("is_self")):
                    return
            except json.JSONDecodeError:
                pass
            time.sleep(2)
        raise DevStackError("vault unsealed but its Raft node did not become active")

    def org_key_metadata(self, organization_id: str) -> dict:
        name = f"org-{uuid.UUID(organization_id).hex}-{KEY_PURPOSE}"
        data = json.loads(self.vault(f"vault read -format=json transit/keys/{name}", token=self.custody["authority_vault_token"]).stdout)["data"]
        fields = ("type", "key_size", "exportable", "allow_plaintext_backup", "deletion_allowed", "latest_version", "min_decryption_version")
        return {"key_name": name, **{field: data[field] for field in fields}}

    def archive_object_count(self) -> int:
        out = self.compose("exec", "-T", "archive-store", "sh", "-c", "find /data/__blobstorage__ -type f | wc -l", timeout=60).stdout
        return int(out.strip())

    def pin_authority_policy(self, key_name: str) -> None:
        """Replaces the bootstrap policy with one limited to the single organization key's exact paths, then proves the
        Authority's token can still read that key but can no longer change its configuration or rotate it."""
        if not key_name.startswith("org-") or not all(c.isalnum() or c == "-" for c in key_name):
            raise DevStackError(f"unexpected key name {key_name!r}")
        policy = "\n".join((
            f'path "transit/keys/{key_name}" {{ capabilities = ["read"] }}',
            f'path "transit/hmac/{key_name}/sha2-256" {{ capabilities = ["update"] }}',
            f'path "transit/verify/{key_name}/sha2-256" {{ capabilities = ["update"] }}',
            'path "auth/token/lookup-self" { capabilities = ["read"] }',
        ))
        self.vault("vault policy write authority-transit - <<'POLICY'\n" + policy + "\nPOLICY\n", token=self.custody["vault_root_token"])
        authority = self.custody["authority_vault_token"]
        self.vault(f"vault read -format=json transit/keys/{key_name} >/dev/null", token=authority)
        for denied in (f"vault write transit/keys/{key_name}/config deletion_allowed=true", f"vault write -f transit/keys/{key_name}/rotate",
                       "vault write transit/keys/org-00000000000000000000000000000000-row-integrity-v1 type=hmac key_size=32"):
            if self.vault(denied + " >/dev/null 2>&1", token=authority, check=False).returncode == 0:
                raise DevStackError("the pinned Authority policy still allows: " + denied.split(" ")[2])
        print(f"[init] Authority Vault policy pinned to {key_name} (config, rotate and other keys denied)")

    def probe_mac(self) -> str:
        out = self.vault(f"vault write -format=json transit/hmac/{PROBE_KEY}/sha2-256 input={PROBE_INPUT_B64}", token=self.custody["vault_root_token"]).stdout
        return json.loads(out)["data"]["hmac"]

    # ---- lifecycle ---------------------------------------------------------------------------------------------------

    def up(self, services: list[str] | None = None) -> None:
        self.compose("up", "-d", "--no-deps", "vault")
        self.unseal()
        self.compose("up", "-d", "--wait", "--wait-timeout", "600", *(services or LONG_RUNNING), timeout=1200)

    def seed(self) -> None:
        """Runs the one-shot dev-seed (idempotent: it reuses the persisted organization and persona ids) and checks its exit code."""
        self.compose("up", "-d", "--no-deps", "--force-recreate", "dev-seed")
        waited = self.compose("wait", "dev-seed", timeout=600, check=False)
        found = re.search(r"(\d+)\s*$", (waited.stdout + waited.stderr).strip())
        code = found.group(1) if found else "unknown"
        if code != "0":
            logs = self.compose("logs", "--no-color", "--tail", "20", "dev-seed", check=False).stdout
            raise DevStackError(f"dev-seed exited {code}:\n{logs}")

    def authority_head(self, record_id: str) -> dict:
        record = str(uuid.UUID(record_id))
        sql = ("SELECT coalesce(committed_operation_id::text, '') || '|' || committed_content_revision "
               f"FROM authority.record_content_heads WHERE form_record_id = '{record}';")
        # Read-only query as the database admin; the password comes from the container's own environment.
        out = self.compose("exec", "-T", "authority-db", "sh", "-c", 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -f -',
                           input_text=sql, timeout=60).stdout.strip()
        if not out:
            raise DevStackError(f"no Authority content head for record {record}")
        operation, revision = out.split("|")
        return {"committed_operation_id": operation, "committed_content_revision": int(revision)}


# --------------------------------------------------------------------------------------------------------------------
# Business API client (development login through the BFF; the same path as the browser)
# --------------------------------------------------------------------------------------------------------------------

class ApiError(Exception):
    def __init__(self, status: int, body: str):
        super().__init__(f"HTTP {status}: {body[:600]}")
        self.status = status
        self.body = body

    @property
    def code(self) -> str:
        try:
            parsed = json.loads(self.body)
            return parsed.get("errorCode") or parsed.get("title") or ""
        except json.JSONDecodeError:
            return ""


class Api:
    def __init__(self, base: str):
        self.base = base
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.csrf = ""

    def request(self, method: str, path: str, body: object | None = None) -> object:
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        if method != "GET":
            request.add_header("X-CSRF-Token", self.csrf)
            request.add_header("Idempotency-Key", "devstack-" + uuid.uuid4().hex)
        try:
            with self.opener.open(request, timeout=120) as response:
                text = response.read().decode("utf-8")
                return json.loads(text) if text else None
        except urllib.error.HTTPError as error:
            raise ApiError(error.code, error.read().decode("utf-8", errors="replace")) from None

    def get(self, path: str) -> object:
        return self.request("GET", path)

    def post(self, path: str, body: object) -> object:
        return self.request("POST", path, body)

    def sign_in(self, persona: str) -> "Api":
        session = self.post("/api/v1/session/development-login", {"personaKey": persona})
        self.csrf = session["csrfToken"]
        return self

    def refused(self, method: str, path: str, body: object | None = None) -> ApiError:
        try:
            self.request(method, path, body)
        except ApiError as error:
            return error
        raise DevStackError(f"expected {method} {path} to be refused, but it succeeded")


def signed_in(stack: Stack, persona: str) -> Api:
    return Api(stack.api_base()).sign_in(persona)


def wait_api(stack: Stack, seconds: int = 180) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            urllib.request.urlopen(stack.api_base() + "/health/live", timeout=5).close()
            return
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            time.sleep(2)
    raise DevStackError("business API did not become live")


def personas(stack: Stack) -> list[dict]:
    return Api(stack.api_base()).get("/api/v1/session/development-personas")


def user_ids(api: Api) -> dict:
    users = api.get("/api/v1/users")
    def find(fragment: str) -> str:
        return next(u["userId"] for u in users if fragment in u["displayName"])
    return {"owner": find("Avery Owner"), "designer": find("Dana Designer"), "clerk": find("Casey Clerk"), "reader": find("Riley Reader")}


CASE_FIELDS = (("case_title", "Case title", True), ("internal_notes", "Internal notes", False))
STAGED_FIELDS = (("case_title", "Case title", True), ("case_number", "Case number", False), ("internal_notes", "Internal notes", False))


def publish_case_form(api: Api, name: str, description: str, owner_user_id: str, specs=CASE_FIELDS) -> tuple[str, list[dict]]:
    """Creates and publishes (version 1) a synthetic text-field form; returns its id and layout elements."""
    created = api.post("/api/v1/form-definitions/designer", {"formDefinitionName": name, "formDefinitionDescription": description,
                                                             "formUserUserIds": [owner_user_id], "formUserTeamIds": []})
    form_id = created["summary"]["formDefinitionId"]
    elements = []
    for key, label, required in specs:
        added = api.post(f"/api/v1/form-definitions/{form_id}/fields", {"fieldKey": key, "fieldDisplayName": label, "fieldDescription": "",
                                                                       "fieldDataType": "String", "options": None, "groupDefinitionId": None})
        elements.append({"kind": "field", "fieldDefinitionId": added["fieldDefinitionId"], "fieldKey": key, "control": "text", "label": label,
                         "required": required, "layout": {"columnSpan": 2, "startOnNewRow": False}, "validation": {}})
    publish_layout(api, form_id, name, elements, 0, "synthetic development data (devstack)")
    return form_id, elements


def publish_layout(api: Api, form_id: str, name: str, elements: list[dict], current_version: int, reason: str) -> None:
    detail = api.get(f"/api/v1/form-definitions/{form_id}")
    draft_revision = int(detail["draft"]["draftRevision"]) if detail.get("draft") else 0
    placed = [{k: v for k, v in e.items() if k != "fieldKey"} for e in elements]
    layout = {"layoutSchemaVersion": 2, "title": name, "sections": [{"sectionKey": "main", "title": "Case", "gridColumns": 4, "elements": placed}]}
    saved = api.request("PUT", f"/api/v1/form-definitions/{form_id}/draft", {"expectedDraftRevision": draft_revision, "layoutJson": json.dumps(layout)})
    api.post(f"/api/v1/form-definitions/{form_id}/publish", {"expectedDraftRevision": int(saved["draftRevision"]),
                                                            "expectedCurrentVersionNumber": current_version, "changeReason": reason})


def require_case_number(api: Api, form_id: str, name: str, elements: list[dict]) -> None:
    """Publishes version 2 in which "Case number" becomes required (records created under version 1 may lack it)."""
    for element in elements:
        if element["fieldKey"] == "case_number":
            element["required"] = True
    publish_layout(api, form_id, name, elements, 1, "Case number becomes required (synthetic)")


def create_record(api: Api, form_id: str, values: dict) -> str:
    return api.post(f"/api/v1/form-definitions/{form_id}/records", {"values": values, "ownerTeamId": None, "groups": []})["formRecordId"]


def projection(api: Api, record_id: str, seconds: int = 60) -> dict:
    deadline = time.monotonic() + seconds
    while True:
        try:
            view = api.get(f"/api/v1/form-records/{record_id}/projection")
            if view["summary"].get("recordReference"):
                return view
        except ApiError:
            if time.monotonic() > deadline:
                raise
        if time.monotonic() > deadline:
            raise DevStackError(f"projection of {record_id} not available")
        time.sleep(1)


def values_of(view: dict) -> dict:
    return {v["fieldKey"]: v.get("stringValue") for v in view["record"]["values"]}


def lifecycle(view: dict) -> tuple[str, list[str], int]:
    return view["lifecycle"]["status"], list(view["lifecycle"]["allowedTransitions"]), int(view["record"]["contentRevision"])


def transition(api: Api, record_id: str, name: str, view: dict, reason: str | None = None) -> dict:
    status, _, revision = lifecycle(view)
    return api.post(f"/api/v1/form-records/{record_id}/{name}", {"expectedContentRevision": revision, "expectedStatus": status, "reason": reason})


# --------------------------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------------------------

def cmd_init(args) -> None:
    project = args.project
    custody_file = custody_dir(project) / "custody.json"
    if custody_file.exists():
        # Resume an interrupted init of THIS environment: the custody file proves which Vault it initialized. Never
        # re-initializes Vault, never replaces a key or token that already exists.
        custody = load_custody(project)
        if load_state(project).get("organization_id"):
            raise DevStackError(f"'{project}' is already initialized; init never overwrites an environment (use `up`)")
        print(f"[init] resuming the interrupted initialization of {project} from its custody file")
        finish_init(Stack(project, custody), custody)
        return
    stack = Stack(project, None)
    if stack.volumes():
        raise DevStackError(f"volumes already exist for project '{project}': {stack.volumes()}; refusing to initialize over them")

    print(f"[init] building images for project {project} ...")
    stack.compose("build", timeout=3600)
    stack.compose("up", "-d", "--no-deps", "vault")
    if stack.wait_vault({"uninitialized", "sealed", "unsealed"}) != "uninitialized":
        raise DevStackError("new vault volume is unexpectedly initialized; stopping")

    init = json.loads(stack.compose("exec", "-T", "vault", "vault", "operator", "init", "-key-shares=1", "-key-threshold=1", "-format=json").stdout)
    custody = {
        "purpose": "DEVELOPMENT ONLY custody material for the persistent development stack (C-47). Keep a copy in a password manager; never commit.",
        "project": project,
        "created_on": dt.datetime.now(dt.timezone.utc).isoformat(),
        "vault_unseal_keys_b64": init["unseal_keys_b64"],
        "vault_unseal_threshold": init["unseal_threshold"],
        "vault_root_token": init["root_token"],
        "authority_vault_token": None,
        "local_dev_kek_base64": base64.b64encode(secrets.token_bytes(32)).decode(),
    }
    write_private_json(custody_dir(project) / "custody.json", custody)  # written before anything else can fail
    print(f"[init] vault initialized; custody file written to {custody_dir(project) / 'custody.json'} (owner-only ACL)")
    stack.custody = custody
    finish_init(stack, custody)


def finish_init(stack: Stack, custody: dict) -> None:
    project = stack.project
    stack.compose("up", "-d", "--no-deps", "vault")
    stack.unseal()
    root = custody["vault_root_token"]
    mounts = json.loads(stack.vault("vault secrets list -format=json", token=root).stdout)
    if "transit/" not in mounts:
        stack.vault("vault secrets enable transit", token=root)
    stack.vault("vault policy write authority-transit /vault/policies/authority-transit.hcl", token=root)  # idempotent
    stack.vault(f"vault write transit/keys/{PROBE_KEY} type=hmac key_size=32 exportable=false", token=root)  # no-op if it exists
    if not custody.get("authority_vault_token"):
        token = json.loads(stack.vault("vault token create -orphan -no-default-policy -policy=authority-transit -ttl=87600h "
                                       "-display-name=authorization-authority-dev -format=json", token=root).stdout)
        custody["authority_vault_token"] = token["auth"]["client_token"]
        write_private_json(custody_dir(project) / "custody.json", custody)
    stack.custody = custody
    print("[init] transit engine, Authority policy and least-privilege Authority token in place")

    print("[init] starting the stack (migrations, SpiceDB, Authority, dev-seed) ...")
    stack.up()
    stack.seed()
    wait_api(stack)
    seeded = personas(stack)
    organization_ids = {p["organizationId"] for p in seeded}
    if organization_ids & RETIRED_ORGANIZATION_IDS:
        raise DevStackError("dev-seed produced a retired organization id; refusing to continue")
    if sorted(p["personaKey"] for p in seeded) != sorted(PERSONAS):
        raise DevStackError(f"unexpected personas: {[p['personaKey'] for p in seeded]}")
    organization_id = organization_ids.pop()
    key = stack.org_key_metadata(organization_id)
    if key["exportable"] or key["type"] != "hmac":
        raise DevStackError(f"organization key has unexpected properties: {key}")
    stack.pin_authority_policy(key["key_name"])
    save_state(project, {"organization_id": organization_id, "organization_key": key, "probe_mac": stack.probe_mac()})
    print(f"[init] organization {organization_id}; signing key {key['key_name']} v{key['latest_version']} (non-exportable)")
    print(f"[init] ready: {stack.web_base()}  (API {stack.api_base()})")


def cmd_up(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    stack.up()
    wait_api(stack)
    print(f"[up] vault unsealed; stack running: {stack.web_base()}")


def cmd_unseal(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    stack.unseal()
    print("[unseal] vault unsealed")


APPLICATION_SERVICES = ("authority", "api", "worker", "web")


def cmd_rebuild(args) -> None:
    """Rebuilds application images from the working tree and recreates only those containers (no dependencies): Vault,
    the databases, SpiceDB and their volumes are not restarted. The authority and api migrations run first (idempotent)."""
    services = args.services or list(APPLICATION_SERVICES)
    unknown = [s for s in services if s not in APPLICATION_SERVICES]
    if unknown:
        raise DevStackError(f"rebuild only recreates application services {APPLICATION_SERVICES}; refused: {unknown}")
    stack = Stack(args.project, load_custody(args.project))
    if stack.vault_status() != "unsealed":
        raise DevStackError("vault is not unsealed; run `devstack.py up` first")
    stack.compose("build", *services, timeout=3600)  # the *-migrate services share the authority and api images
    for service, migrate in (("authority", "authority-migrate"), ("api", "business-migrate")):
        if service in services:
            stack.compose("up", "--no-deps", "--force-recreate", "--exit-code-from", migrate, migrate, timeout=900)
    stack.compose("up", "-d", "--no-deps", "--force-recreate", "--wait", "--wait-timeout", "300", *services, timeout=900)
    print(f"[rebuild] recreated {', '.join(services)}; Vault and data services untouched")


def cmd_stop(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    stack.compose("stop")
    print("[stop] containers stopped; volumes kept")


def cmd_status(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    print(stack.compose("ps", "-a", "--format", "table {{.Service}}\t{{.State}}\t{{.Status}}").stdout)
    print("vault:", stack.vault_status())
    print("volumes:", ", ".join(stack.volumes()))
    print("custody:", custody_dir(args.project) / "custody.json")
    print("browser:", stack.web_base())


def snapshot(stack: Stack, state: dict) -> dict:
    """Everything a later comparison needs: verified record values, Authority head, signing-key metadata, probe MAC."""
    owner = signed_in(stack, "owner")
    view = projection(owner, state["probe"]["record_id"], seconds=90)
    return {
        "projection_values": values_of(view),
        "projection_revision": int(view["record"]["contentRevision"]),
        "projection_status": view["lifecycle"]["status"],
        "authority_head": stack.authority_head(state["probe"]["record_id"]),
        "organization_key": stack.org_key_metadata(state["organization_id"]),
        "probe_mac": stack.probe_mac(),
        # New security events may be archived between snapshots (e.g. the refused reads while sealed); none may disappear.
        "archive_objects_nondecreasing": stack.archive_object_count(),
    }


def ensure_probe(stack: Stack, state: dict) -> dict:
    if state.get("probe"):
        return state
    owner = signed_in(stack, "owner")
    ids = user_ids(owner)
    form_id, _ = publish_case_form(owner, "Devstack persistence probe", "synthetic record used to prove Vault key persistence", ids["owner"])
    record_id = create_record(owner, form_id, {"case_title": "Persistence probe record", "internal_notes": "signed before the restarts"})
    projection(owner, record_id)
    state["probe"] = {"form_id": form_id, "record_id": record_id}
    save_state(stack.project, state)
    return state


def compare(label: str, baseline: dict, current: dict) -> dict:
    differences = {k: {"before": baseline[k], "after": current[k]} for k in baseline
                   if (current[k] < baseline[k] if k.endswith("_nondecreasing") else current[k] != baseline[k])}
    print(f"  [{label}] {'IDENTICAL' if not differences else 'DIFFERENT: ' + json.dumps(differences)}")
    if differences:
        raise DevStackError(f"{label}: state changed across the restart: {differences}")
    return {"phase": label, "identical": True}


def read_while_sealed(stack: Stack, state: dict) -> dict:
    """While Vault is sealed the verified read must fail closed, never fall back to unverified data."""
    owner = signed_in(stack, "owner")
    try:
        owner.get(f"/api/v1/form-records/{state['probe']['record_id']}/projection")
    except ApiError as error:
        print(f"  [sealed] verified read refused: HTTP {error.status} {error.code}")
        return {"status": error.status, "code": error.code}
    raise DevStackError("a verified read succeeded while Vault was sealed")


def wait_reads(stack: Stack, state: dict, seconds: int = 120) -> None:
    deadline = time.monotonic() + seconds
    while True:
        try:
            signed_in(stack, "owner").get(f"/api/v1/form-records/{state['probe']['record_id']}/projection")
            return
        except (ApiError, urllib.error.URLError, ConnectionError):
            if time.monotonic() > deadline:
                raise
            time.sleep(2)


def cmd_verify_persistence(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    state = ensure_probe(stack, load_state(args.project))
    evidence = {"project": args.project, "started_on": dt.datetime.now(dt.timezone.utc).isoformat(), "probe": state["probe"], "phases": []}
    baseline = snapshot(stack, state)
    evidence["baseline"] = baseline
    print(f"[verify] baseline: record {state['probe']['record_id']} revision {baseline['projection_revision']} values {baseline['projection_values']}")
    print(f"[verify] key {baseline['organization_key']['key_name']} v{baseline['organization_key']['latest_version']}; probe MAC captured")

    print("[verify] phase 1: restart the vault container")
    stack.compose("restart", "vault")
    phase = {"phase": "vault-restart", "status_after_restart": stack.wait_vault({"sealed", "unsealed"})}
    phase["sealed_read"] = read_while_sealed(stack, state) if phase["status_after_restart"] == "sealed" else None
    stack.unseal()
    wait_reads(stack, state)
    phase.update(compare("vault-restart", baseline, snapshot(stack, state)))
    evidence["phases"].append(phase)

    print("[verify] phase 2: SIGKILL the vault container (unclean stop, as when the Docker engine dies)")
    stack.compose("kill", "-s", "SIGKILL", "vault")
    stack.compose("up", "-d", "--no-deps", "vault")
    phase = {"phase": "vault-kill", "status_after_restart": stack.wait_vault({"sealed", "unsealed"})}
    phase["sealed_read"] = read_while_sealed(stack, state) if phase["status_after_restart"] == "sealed" else None
    stack.unseal()
    wait_reads(stack, state)
    phase.update(compare("vault-kill", baseline, snapshot(stack, state)))
    evidence["phases"].append(phase)

    print("[verify] phase 3: docker compose stop of the whole stack, then start")
    stack.compose("stop")
    stack.up()
    wait_api(stack)
    wait_reads(stack, state)
    evidence["phases"].append(compare("stack-stop-start", baseline, snapshot(stack, state)))

    print("[verify] phase 4: docker compose down (volumes kept), then up: every container and network is recreated")
    stack.compose("down")  # never -v: the volumes are the environment
    stack.up()
    wait_api(stack)
    wait_reads(stack, state)
    evidence["phases"].append(compare("stack-down-up", baseline, snapshot(stack, state)))

    evidence["finished_on"] = dt.datetime.now(dt.timezone.utc).isoformat()
    write_evidence(args.project, "persistence", evidence)
    print("[verify] PASSED: the same record verifies with the same signing key after every restart")


def write_evidence(project: str, kind: str, evidence: dict) -> Path:
    directory = REPO / "artifacts" / "dev-persistent"  # git-ignored
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{project}-{kind}-{dt.datetime.now().strftime('%Y%m%d-%H%M%S')}.json"
    path.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(f"[evidence] {path}")
    return path


def backup_root(project: str) -> Path:
    return Path(os.environ.get("DEVSTACK_BACKUP_ROOT") or Path.home() / "Documents" / "EnterpriseForms-dev-backups") / project


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def cmd_backup(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    state = ensure_probe(stack, load_state(args.project))
    expected = snapshot(stack, state)  # what a restore of this backup must reproduce
    target = backup_root(args.project) / dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    target.mkdir(parents=True)
    commit = run(["git", "rev-parse", "HEAD"], cwd=REPO).stdout.strip()
    print(f"[backup] stopping {args.project} for a consistent cold copy ...")
    stack.compose("stop")
    entries = []
    try:
        for volume in stack.volumes():
            archive = target / f"{volume}.tgz"
            run(["docker", "run", "--rm", "--entrypoint", "tar", "-v", f"{volume}:/source:ro", "-v", f"{target}:/backup",
                 VAULT_IMAGE, "czf", f"/backup/{archive.name}", "-C", "/source", "."], timeout=1800)
            with tarfile.open(archive, "r:gz") as handle:
                count = sum(1 for _ in handle)
            entries.append({"volume": volume, "file": archive.name, "sha256": sha256(archive), "entries": count, "bytes": archive.stat().st_size})
            print(f"  {volume}: {count} entries")
    finally:
        print("[backup] restarting the stack ...")
        stack.up()
    manifest = {
        "purpose": "DEVELOPMENT ONLY backup of the persistent development stack (C-47). Contains NO custody secrets; the Vault data is "
                   "sealed and needs the separately held custody file to open.",
        "project": args.project, "created_on": dt.datetime.now(dt.timezone.utc).isoformat(), "git_commit": commit,
        "organization_id": state["organization_id"], "probe": state["probe"], "expected": expected, "volumes": entries,
    }
    (target / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"[backup] {len(entries)} volumes -> {target}")


def restore_pg_hba(directory: Path, business_prefix: str, authority_prefix: str) -> None:
    for name, prefix in (("business", business_prefix), ("authority", authority_prefix)):
        source = (DOCKER_DIR / "postgres" / "dev-persistent" / f"pg_hba.{name}.conf").read_text(encoding="utf-8")
        live = "172.31.0" if name == "business" else "172.31.1"
        (directory / f"pg_hba.{name}.conf").write_text(source.replace(live, prefix), encoding="utf-8", newline="\n")


def cmd_restore_test(args) -> None:
    source_project = args.project
    custody = load_custody(source_project)
    backup = Path(args.backup_dir)
    manifest = json.loads((backup / "manifest.json").read_text(encoding="utf-8"))
    if manifest["project"] != source_project:
        raise DevStackError(f"backup belongs to '{manifest['project']}', not '{source_project}'")
    for entry in manifest["volumes"]:
        if sha256(backup / entry["file"]) != entry["sha256"]:
            raise DevStackError(f"checksum mismatch: {entry['file']}")
    print(f"[restore-test] {len(manifest['volumes'])} archives verified against manifest checksums")

    project = source_project + RESTORE_SUFFIX
    work = Path(tempfile.mkdtemp(prefix="devstack-restore-"))
    restore_pg_hba(work, "172.29.0", "172.29.1")
    overrides = {"DEVSTACK_BUSINESS_SUBNET": "172.29.0.0/24", "DEVSTACK_AUTHORITY_SUBNET": "172.29.1.0/24", "DEVSTACK_PG_HBA_DIR": work.as_posix(),
                 "DEVSTACK_API_PORT": "5281", "DEVSTACK_WEB_PORT": "8092"}
    stack = Stack(project, custody, overrides)
    destroy_disposable(stack)
    result = {"source_project": source_project, "restore_project": project, "backup": str(backup), "started_on": dt.datetime.now(dt.timezone.utc).isoformat()}
    try:
        for entry in manifest["volumes"]:
            logical = entry["volume"][len(source_project) + 1:]
            volume = f"{project}_{logical}"
            run(["docker", "volume", "create", "--label", f"com.docker.compose.project={project}", "--label", f"com.docker.compose.volume={logical}", volume])
            run(["docker", "run", "--rm", "--entrypoint", "tar", "-v", f"{volume}:/target", "-v", f"{backup}:/backup:ro",
                 VAULT_IMAGE, "xzf", f"/backup/{entry['file']}", "-C", "/target"], timeout=1800)
        print(f"[restore-test] restored into disposable volumes of project {project}")
        stack.up(["api"])
        wait_api(stack)
        state = {"probe": manifest["probe"], "organization_id": manifest["organization_id"]}
        wait_reads(stack, state)
        restored = snapshot(stack, state)
        expected = manifest["expected"]
        result["comparison"] = compare("restore", expected, restored)
        result["restored"] = restored
        print("[restore-test] PASSED: the restored copy verifies the backed-up record with the same signing key")
    finally:
        if not args.keep:
            destroy_disposable(stack)
            print(f"[restore-test] disposable project {project} removed")
    result["finished_on"] = dt.datetime.now(dt.timezone.utc).isoformat()
    write_evidence(source_project, "restore-test", result)


def destroy_disposable(stack: Stack) -> None:
    if not stack.project.endswith(RESTORE_SUFFIX):
        raise DevStackError(f"refusing to remove non-disposable project '{stack.project}'")
    stack.compose("down", "-v", "--remove-orphans", check=False)
    for volume in stack.volumes():
        run(["docker", "volume", "rm", volume], check=False)


def cmd_verify_workflow(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    state = ensure_probe(stack, load_state(args.project))
    owner = signed_in(stack, "owner")
    clerk = signed_in(stack, "clerk")
    ids = user_ids(owner)
    form_id = state["probe"]["form_id"]
    checks: list[dict] = []

    def check(name: str, outcome: object) -> None:
        checks.append({"check": name, "outcome": outcome})
        print(f"  ok  {name}: {outcome}")

    record = create_record(owner, form_id, {"case_title": "Workflow check " + uuid.uuid4().hex[:6], "internal_notes": "draft"})
    owner.post(f"/api/v1/form-records/{record}/shares", {"recipientUserId": ids["clerk"], "recipientTeamId": None, "recipientRole": "Reader", "fieldKeys": ["case_title"]})
    view = projection(owner, record)
    check("new record is Draft with Submit/Archive offered", lifecycle(view)[:2])

    staged_name = "Devstack workflow check " + uuid.uuid4().hex[:6]
    staged_form, staged_elements = publish_case_form(owner, staged_name, "synthetic", ids["owner"], STAGED_FIELDS)
    missing = create_record(owner, staged_form, {"case_title": "Created before Case number was required", "internal_notes": ""})
    require_case_number(owner, staged_form, staged_name, staged_elements)
    error = owner.refused("POST", f"/api/v1/form-records/{missing}/submit", {"expectedContentRevision": lifecycle(projection(owner, missing))[2], "expectedStatus": "Draft"})
    check("submit with a required field missing is refused and names it", f"HTTP {error.status} {error.code} {json.loads(error.body).get('field_keys')}")

    transition(owner, record, "submit", view)
    view = projection(owner, record)
    check("owner submit -> Submitted", lifecycle(view)[:2])
    error = owner.refused("PATCH", f"/api/v1/form-records/{record}", {"expectedContentRevision": lifecycle(view)[2], "values": {"internal_notes": "edit while submitted"}})
    check("owner edit while Submitted is refused", f"HTTP {error.status} {error.code}")

    clerk_view = projection(clerk, record)
    check("clerk sees the status but no lifecycle action", lifecycle(clerk_view)[:2])
    error = clerk.refused("POST", f"/api/v1/form-records/{record}/reopen", {"expectedContentRevision": lifecycle(clerk_view)[2], "expectedStatus": "Submitted", "reason": "clerk attempt"})
    check("clerk reopen is refused", f"HTTP {error.status} {error.code}")
    error = clerk.refused("PATCH", f"/api/v1/form-records/{record}", {"expectedContentRevision": lifecycle(clerk_view)[2], "values": {"case_title": "clerk edit"}})
    check("clerk edit is refused", f"HTTP {error.status} {error.code}")

    error = owner.refused("POST", f"/api/v1/form-records/{record}/reopen", {"expectedContentRevision": lifecycle(view)[2], "expectedStatus": "Submitted", "reason": "  "})
    check("reopen without a reason is refused", f"HTTP {error.status} {error.code}")
    transition(owner, record, "reopen", view, reason="correcting the internal notes")
    view = projection(owner, record)
    check("owner reopen with a reason -> Draft", lifecycle(view)[:2])
    owner.request("PATCH", f"/api/v1/form-records/{record}", {"expectedContentRevision": lifecycle(view)[2], "values": {"case_title": values_of(view)["case_title"], "internal_notes": "edited after reopening"}})
    view = projection(owner, record)
    check("owner edit after reopening is saved", values_of(view)["internal_notes"])

    transition(owner, record, "submit", view)
    view = projection(owner, record)
    transition(owner, record, "archive", view)
    view = projection(owner, record)
    check("owner archive of a Submitted record -> Archived", lifecycle(view)[:2])
    default_list = {i["formRecordId"] for i in owner.get(f"/api/v1/form-records?formDefinitionId={form_id}&pageSize=100")["items"]}
    with_archived = {i["formRecordId"] for i in owner.get(f"/api/v1/form-records?formDefinitionId={form_id}&pageSize=100&includeArchived=true")["items"]}
    check("archived record hidden by default, listed with includeArchived", {"default": record in default_list, "includeArchived": record in with_archived})
    transition(owner, record, "unarchive", view)
    view = projection(owner, record)
    check("unarchive returns to the archived-from state (Submitted)", lifecycle(view)[:2])

    draft = create_record(owner, form_id, {"case_title": "Draft archive round trip", "internal_notes": ""})
    draft_view = projection(owner, draft)
    transition(owner, draft, "archive", draft_view)
    transition(owner, draft, "unarchive", projection(owner, draft))
    check("archive then unarchive of a Draft returns to Draft", lifecycle(projection(owner, draft))[:2])

    try:
        owner.post(f"/api/v1/form-records/{draft}/shares", {"recipientUserId": ids["clerk"], "recipientTeamId": None, "recipientRole": "Contributor", "fieldKeys": ["case_title"]})
        contributor_view = projection(clerk, draft)
        error = clerk.refused("PATCH", f"/api/v1/form-records/{draft}", {"expectedContentRevision": lifecycle(contributor_view)[2], "values": {"case_title": "contributor edit"}})
        check("Contributor share: clerk edit refused while contributor writes are disabled (default)", f"HTTP {error.status} {error.code}")
    except ApiError as error:
        check("Contributor share refused while contributor writes are disabled (default)", f"HTTP {error.status} {error.code}")

    write_evidence(args.project, "workflow", {"project": args.project, "checks": checks, "finished_on": dt.datetime.now(dt.timezone.utc).isoformat()})
    print(f"[verify-workflow] PASSED ({len(checks)} checks)")


def cmd_prepare_acceptance(args) -> None:
    stack = Stack(args.project, load_custody(args.project))
    state = load_state(args.project)
    owner = signed_in(stack, "owner")
    ids = user_ids(owner)
    name = "M1 Acceptance - Case intake"
    form_id, elements = publish_case_form(owner, name, "Synthetic form for the Phase C6 M1 browser acceptance review", ids["owner"], STAGED_FIELDS)
    incomplete = create_record(owner, form_id, {"case_title": "M1 acceptance case - missing case number",
                                                "internal_notes": "Synthetic. Created before Case number became required: Submit must name it."})
    require_case_number(owner, form_id, name, elements)
    ready = create_record(owner, form_id, {"case_title": "M1 acceptance case", "case_number": "C6-0001",
                                           "internal_notes": "Synthetic. Use for Submit, Reopen, Archive and Unarchive."})
    owner.post(f"/api/v1/form-records/{ready}/shares", {"recipientUserId": ids["clerk"], "recipientTeamId": None, "recipientRole": "Reader", "fieldKeys": ["case_title"]})
    references = {}
    for record in (ready, incomplete):
        view = projection(owner, record)
        references[record] = view["summary"]["recordReference"]
        if lifecycle(view)[0] != "Draft":
            raise DevStackError(f"{record} is not a Draft")
    state["acceptance"] = {"form_id": form_id, "ready_record_id": ready, "incomplete_record_id": incomplete, "references": references}
    save_state(args.project, state)
    print(f"[acceptance] form 'M1 Acceptance - Case intake' ({form_id})")
    print(f"[acceptance] Draft ready to submit: {references[ready]}  {stack.web_base()}/app/records/{ready}")
    print(f"[acceptance] Draft missing the now-required Case number: {references[incomplete]}  {stack.web_base()}/app/records/{incomplete}")
    print("[acceptance] Casey Clerk has a Reader share of 'Case title' on the first record")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--project", default=os.environ.get("DEVSTACK_PROJECT", DEFAULT_PROJECT))
    commands = parser.add_subparsers(dest="command", required=True)
    for name, handler in (("init", cmd_init), ("up", cmd_up), ("unseal", cmd_unseal), ("stop", cmd_stop), ("status", cmd_status),
                          ("backup", cmd_backup), ("verify-persistence", cmd_verify_persistence), ("verify-workflow", cmd_verify_workflow),
                          ("prepare-acceptance", cmd_prepare_acceptance)):
        commands.add_parser(name).set_defaults(handler=handler)
    rebuild = commands.add_parser("rebuild", help="rebuild and recreate stateless application services only (authority, api, worker, web)")
    rebuild.add_argument("services", nargs="*")
    rebuild.set_defaults(handler=cmd_rebuild)
    restore = commands.add_parser("restore-test")
    restore.add_argument("backup_dir")
    restore.add_argument("--keep", action="store_true", help="leave the disposable restore project running for inspection")
    restore.set_defaults(handler=cmd_restore_test)
    args = parser.parse_args()
    if args.project.endswith(RESTORE_SUFFIX):
        parser.error("operate on the source project; restore-test derives the disposable project name itself")
    try:
        args.handler(args)
    except DevStackError as error:
        print(f"devstack: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
