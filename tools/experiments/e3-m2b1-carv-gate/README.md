# E3-M2b-1 CARV real-PostgreSQL gate (M2-CARV-PG)

**Development/Testing only.** Re-runnable gate for the content acceptance rule version (CARV) fence, approved as OD-M2-29 (disposable PostgreSQL per OD-M2-12). Contract: `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §19 (design) and §20 (implementation record). Not part of `EnterpriseForms.All.slnx`; never built in place.

**Run:** `powershell -File scripts\dev\e3-m2b1-carv-pg-gate.ps1 [-LegacyBinariesRoot <dir>] [-Evidence <dir>]`. Needs Docker and the local `postgres:18.0` image. Each run uses a fresh container (tmpfs data, loopback port, removed afterwards). No developer stack, security-test stack, persistent volume or Azure service is touched. Not Stage B.

| Folder | Built against | Role |
|---|---|---|
| `HarnessA` | a legacy (pre-CARV) Authority Release build | The design-gate scenarios (47 checks) plus RM-01..03, with the CARV migration being this tree's **real** migration (`--migrate` of this tree's build; EF-scripted Down). CARV instances are the test-only database emulation in `sql/emulation.sql.txt`. |
| `HarnessB` | this tree's Release build | The **real** CARV build in-process (Prepare/Commit rules, store refusal mapping and alert, readiness guard and host readiness, versioned application name) against a legacy build driven through `LegacyAgent`. No emulation trigger. |
| `LegacyAgent` | a legacy Authority Release build | One Prepare or Commit of the legacy build per process (its own `ContentService`, EF store and HTTP error mapping). |
| `sql/*.sql.txt` | — | Test-only SQL: the CARV-build emulation and the floor migrations (M2b-1F is **not** a repository migration; it is a separate, later release). Copied to the work directory as `.sql`. |

Both harnesses use a deterministic test-only HMAC provider shared with the agent, so the two builds verify each other's rows. The signing provider is not under test here.

**Legacy builds.** By default the runner builds each legacy commit from `git archive` in the work directory. `-LegacyBinariesRoot <root>` uses prebuilt builds from `<root>\<first 7 characters of the commit>\` instead, for example the frozen design-gate builds whose SHA-256 hashes are recorded in `docs/evidence/e3-m2b1-carv/` (the evidence of record used those).
