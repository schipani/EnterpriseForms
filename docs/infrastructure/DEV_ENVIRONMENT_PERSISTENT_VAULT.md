# Persistent development environment and Vault key custody (DEVELOPMENT ONLY)

Decision **C-47** (2026-10-22). Covers:
* the key-loss incident in the retired development project;
* the persistent replacement stack and its operating procedures;
* evidence that keys and data survive restarts and that backups restore.

This is local development tooling. It is **not**:
* production key management (production signing is Azure Managed HSM, ADR-PB-005 / G04);
* a Level 2 control or Level 2 evidence.

## 1. Incident: development signing keys lost (2026-10-22)

| | |
|---|---|
| What happened | The Docker engine stopped before the session (not by an agent). Every container of the `enterpriseforms` project exited `255`. |
| Root cause 1 | `compose.dev.yaml` ran Vault as `server -dev`: in-memory storage, auto-unsealed. The Vault Transit keys signing every record (`org-<organization>-row-integrity-v1`, non-exportable, no plaintext backup) existed only in that process. When it stopped, the keys were gone. |
| Root cause 2 (found during C-47) | Azurite (`archive-store` security-event archive, `azurite` documents) was started without `--location`. It stored its data in the container's writable layer (`/opt/azurite`), not in the `/data` volume. The data survives stop/start, but not container recreation or a volume restore. |
| Impact | Existing development records can no longer be verified (the HMAC key is gone), so the Authority fails closed on them. The data is synthetic; no production or customer data was involved. |

**Recovery sources checked before calling the keys unrecoverable:**
* **The old Vault container:** `-dev` mode, nothing on disk. Its anonymous `/vault/file` and `/vault/logs` volumes are empty, and `docker diff` shows only caches and a dev token file.
* **Every Docker volume on the machine,** mounted read-only: none holds Vault storage.
* **The host user profile,** searched for snapshots, unseal or init files and transit backups: nothing found.
* **The Authority:** keys are created `exportable=false` and never backed up, so neither its database nor its configuration holds key material.

**Conclusion:** no copy of the old keys exists. Nothing was generated under the old key identities. The retired organization `01a1579b-a25b-7eaa-bf8e-62f0461624a0` is on the tool's refusal list.

**What remains of the old environment, untouched:**
* **Project:** containers stopped; six volumes intact.
* **Volume backups:** `C:\Users\tsch\Documents\EnterpriseForms-dev-backups\20261022-193852\`. SHA-256 checksums were re-verified on 2026-10-22 and all six match `MANIFEST.txt`.
* **Container-layer copies:** `...\20261022-193852-container-layers\`, made read-only with `docker cp` from the stopped `archive-store` and `azurite` containers, with `MANIFEST.sha256`. These are the only copies of the old archive bodies and document blobs outside those containers.

Do not remove the old containers while those copies matter. The old records stay unverifiable whatever is done, because their keys are gone.

## 2. The persistent development stack

`infrastructure/docker/compose.yaml` + `compose.dev-persistent.yaml`, Compose project **`enterpriseforms-dev`**. It is operated through `scripts/devstack/devstack.py`.

**Vault:**
* **Storage:** a real server on Raft integrated storage in the named volume `enterpriseforms-dev_vault-data` (`vault/vault-dev-persistent.hcl`). It starts **sealed** after every restart.
* **Sealed behaviour:** while sealed, the Authority's readiness probe and every verified read fail closed (`503 SIGNING_UNAVAILABLE`).
* **Network:** plain HTTP on the internal `authority-net` only; no host port.

**The Authority's Vault token is least-privilege:**
* **Bootstrap policy:** `vault/authority-transit-policy.hcl` allows create/read/update on `transit/keys/org-*`, HMAC and verify, and token self-lookup.
* **Pinned policy:** after the organization is seeded, `init` replaces it with a policy pinned to that one key's exact paths (read the key; HMAC and verify with SHA-256).
* **Proof of the pin:** the tool then shows that the token is denied `config`, `rotate` and creation of any other key.
* **Root token:** never given to the Authority.

**Business KEK:** the `LocalDevelopment` KEK is generated per environment and held in custody, not taken from `.env`.

**Azurite:** started with `--location /data`, so the security-event archive and document blobs are in their volumes.

**Isolation from the retired project and from the security-test stack:**

| Item | Value |
|---|---|
| Project | `enterpriseforms-dev` |
| Volumes | `enterpriseforms-dev_*` |
| Subnets | `172.31.0.0/24`, `172.31.1.0/24` (own `postgres/dev-persistent/pg_hba.*.conf`) |
| Image tags | `enterpriseforms/*:dev` |
| Host ports | web `127.0.0.1:8091`, API `127.0.0.1:5280`; nothing else is published |
| Organization | `01a16640-87f6-7488-a1f1-664db54fbff3`; signing key `org-01a1664087f67488a1f1664db54fbff3-row-integrity-v1` v1 (`hmac`, 32 bytes, non-exportable, no plaintext backup, deletion not allowed) |

No old network had to be removed; the retired project and its resources were not modified.

A raw `docker compose` run without the custody values **fails fast**: the Vault token and KEK variables have no defaults. This prevents starting with the retired environment's KEK or without a token.

Contributor editing stays disabled by default (`AUTHORITY_SHARE_CONTRIBUTOR_WRITES_ENABLED` unset). The Production startup guard and the Staging/Production refusal of the Vault Transit provider are unchanged.

## 3. Key custody

The custody file `%USERPROFILE%\.enterpriseforms\dev-vault\enterpriseforms-dev\custody.json` is outside the repository. It has an NTFS ACL without inheritance, granting full control to the owning user only. It holds:
* the Vault unseal key (1 of 1);
* the root token;
* the Authority's Vault token;
* the business KEK.

The same folder holds `state.json`, which is not secret: organization id, key metadata, probe record and acceptance record ids.

**Rules:**
* **Never commit the custody file or paste it anywhere.** `.gitignore` already excludes `.env*`. The custody folder lives outside the working tree, and `artifacts/` (evidence) is git-ignored.
* **Keep a second copy** of `custody.json` in a password manager or an encrypted vault. Without it the Vault volume cannot be unsealed, and every record in this environment becomes unverifiable, exactly like the incident.
* **Store backups and custody apart.** A volume backup contains the sealed Vault data but **no custody material**; never store the two together.
* **Shamir 1-of-1 is a deliberate development simplification.** Production uses Managed HSM with security-domain quorum (G14).
* **Token handling:**
  * Vault CLI commands run by the tool get the root or Authority token, and the unseal key, on **stdin only**, never on a command line.
  * Compose receives the Authority token and the KEK as process environment for interpolation. They therefore appear in the configuration of the authority, vault-transit-init, api and worker containers, as the dev token and KEK always did.
  * The root token and unseal key never reach any container's configuration.

## 4. Procedures

Run from the repository root. Docker Desktop must be running.

| When | Command | What happens |
|---|---|---|
| First time only | `python scripts/devstack/devstack.py init` | Refuses if a custody file or volumes already exist. Builds `:dev` images, initializes Vault (custody written first), enables transit, writes the policy, creates the Authority token, starts and seeds the stack, refuses a retired organization id, pins the policy. Resumes safely if interrupted. |
| Every start, after a Docker Desktop restart or reboot | `python scripts/devstack/devstack.py up` | Starts Vault, unseals it from custody, starts everything else and waits for health. |
| Vault restarted on its own | `python scripts/devstack/devstack.py unseal` | Unseals; the Authority recovers without a restart. |
| Status | `python scripts/devstack/devstack.py status` | Containers, seal state, volumes, custody path, URL. |
| Stop | `python scripts/devstack/devstack.py stop` | Stops containers; volumes stay. |
| New application code | `python scripts/devstack/devstack.py rebuild [api worker web]` | Rebuilds those images from the working tree and recreates only those containers (the api first runs `business-migrate`). Vault, databases, SpiceDB and volumes are not restarted. |
| Backup | `python scripts/devstack/devstack.py backup` | Records the expected verified state, stops the stack, tars every project volume to `Documents\EnterpriseForms-dev-backups\enterpriseforms-dev\<timestamp>\` with `manifest.json` (SHA-256, entry counts, commit, expected state), then restarts. |
| Restore drill | `python scripts/devstack/devstack.py restore-test <backup dir>` | Verifies checksums. Restores into the disposable project `enterpriseforms-dev-restoretest` (own volumes, subnets `172.29.x`, ports 5281/8092), unseals with custody and compares against the manifest, then deletes **only** that disposable project. The live stack is not touched. |
| Proofs | `verify-persistence`, `verify-workflow`, `prepare-acceptance` | See §5. Evidence JSON goes to `artifacts/dev-persistent/`. |

**Restoring for real** (not drilled here; it overwrites):
1. Stop the stack.
2. Move the live volumes aside (`docker volume create` + copy). Never delete them first.
3. Extract each archive into an empty volume of the same name.
4. Run `up` with the **same** custody file.

A backup restored with a different custody file cannot be unsealed. That is by design.

**Never run** `docker compose ... down -v` on `enterpriseforms-dev`; the tool only ever does that to the `-restoretest` project. The tool refuses to operate on `enterpriseforms` (retired) or `enterpriseforms-sectest` (test harness).

## 5. Evidence (2026-10-22, commit of this change)

Evidence files are in `artifacts/dev-persistent/` (not committed).

**`verify-persistence`:** a synthetic probe record (revision 1) was signed and verified first. Each phase then compares the verified API values, the Authority content head, the signing-key properties, an HMAC with a separate probe key, and a non-decreasing security-archive object count.

| Phase | Result |
|---|---|
| Restart the vault container | Came back **sealed**; verified read refused `503 SIGNING_UNAVAILABLE`; after unseal everything identical |
| SIGKILL the vault container (as when the engine dies) | Same: sealed, refused, identical after unseal |
| `compose stop` of the whole stack, then `up` | Identical |
| `compose down` (volumes kept), then `up`: every container and network recreated | Identical (after the Azurite `--location` fix) |

**Backup and restore:**
* `backup`: 7 volumes, including `vault-data` (5 entries), `archive-store-data` (5) and `azurite-data` (4).
* An earlier backup taken before the Azurite fix is renamed `...-INCOMPLETE-azurite-in-container-layer` and must not be used.
* `restore-test`: checksums verified; the disposable copy unsealed with the custody key and verified the backed-up record identically (values, head, key properties, probe MAC); the disposable project was removed.

**Not demonstrated directly:**
* **Docker Desktop restart:** not done, because it would have stopped other projects' containers on this machine.
* **Host reboot:** not done.

Both stop the containers and keep the named volumes, which phases 2–4 cover: an unclean kill, and full container recreation from the volumes.

## 6. Limits

**This is a development configuration:**
* single Vault node;
* Shamir 1-of-1;
* plain HTTP on an internal Docker network;
* a long-lived Authority token (TTL 10 years, bounded by `max_lease_ttl`);
* custody in one user-profile file;
* no Vault audit device.

**What it does not do:**
* change production HSM architecture;
* change any signing protocol, manifest, journal or Authority behaviour;
* make the retired records readable.

Docker Desktop on Windows does not isolate bridge networks by IP. The internal `authority-net` remains the isolation used, as in the existing stack.
