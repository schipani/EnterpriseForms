# E3-M2d CARV 2 / floor 2 focused real-PostgreSQL gate

**Development/Testing only.** Focused gate for the E3-M2d dark Authority rules, content acceptance rule version 2 and floor 2
(record: `docs/security/E3_M2D_IMPLEMENTATION_RECORD.md`). Not part of `EnterpriseForms.All.slnx`; never built in place.

**Run:** `powershell -File scripts\dev\e3-m2d-carv2-pg-gate.ps1 [-Evidence <dir>] [-V1Commit <sha>]`. Needs Docker, git and the local
`postgres:18.0` image. One fresh container per run (tmpfs data, loopback port, removed afterwards). No developer stack, security-test
stack, persistent volume or Azure service is touched. Not Stage B. **Floor 2 is installed only in that disposable container
(owner decision D-3).**

| Folder | Built against | Role |
|---|---|---|
| `HarnessD` | this tree's Authority Release build (CARV 2) | The real `ContentService` and EF `AuthorityStore` in-process (Release B with required floor 1, Release B with required floor 2, Release A) on an ordinary runtime role, after this build's own `--migrate` and floor commands; 47 checks (floor-1 compatibility, cross-build, dark v2, floor-2 activation and refusals, missing/altered floors, recovery, historical replay) |
| `V1Agent` | a Release build of `git archive` of the accepted signed E3-M2c commit `8915f77` (CARV 1) | One Prepare or Commit of the version-1 build per process, with that build's own `carv-v1` connection marker and alerts, for the cross-build checks |

**Controlled test-only mechanisms** (as in the E3-M2b-2 S-4 gate): a candidate signed with the harness's deterministic TEST-ONLY HMAC
provider is recorded as a Prepared operation through an ordinary admin session, so every table constraint, the CARV state guard and
the floor CHECK apply; catalog mutations (dropping, weakening, duplicating or substituting a floor, deleting a history row) are made
with the container superuser, as a privileged change would. No production code, configuration, verification or validation is
changed. Release builds are not byte-reproducible, so the recorded binary hashes identify the run, not provenance.
