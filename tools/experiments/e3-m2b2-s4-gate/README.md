# E3-M2b-2 S-4 focused real-PostgreSQL gate

**Development/Testing only.** Focused gate for the Authority S-4 schema/envelope tie (R-M2b2-11), required by the owner before the
E3-M2b-2 signed local commit (R-S4-2). Contract: `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §23.14 (S-4) and §23.15 (this
gate). Not part of `EnterpriseForms.All.slnx`; never built in place.

**Run:** `powershell -File scripts\dev\e3-m2b2-s4-pg-gate.ps1 [-Evidence <dir>]`. Needs Docker and the local `postgres:18.0` image.
One fresh container per run (tmpfs data, loopback port, removed afterwards). No developer stack, security-test stack, persistent
volume or Azure service is touched. Not Stage B.

| Folder | Built against | Role |
|---|---|---|
| `HarnessS4` | this tree's Authority Release build | The real `ContentService` and EF `AuthorityStore` in-process, Release B policy (RB-CARV-1 write fence), connected as an ordinary runtime role, after this build's own `--migrate` and `--apply-content-acceptance-floor 1`. S-4 at Prepare and Commit for field and document rows, the unchanged authoritative state, Commit replay, positive controls, schema 2 / envelope 2 still refused, CARV and floor still enforced (38 checks) |

**Controlled test-only mechanism.** A mismatched candidate cannot pass this build's Prepare, so the harness records an otherwise
valid Prepared operation directly: the candidate is signed with the harness's deterministic test-only HMAC provider (the same
provider the in-process Authority verifies with) and inserted with its candidate leaves through an ordinary administrative session,
so every table constraint, the CARV state guard and the floor CHECK apply. This is the PostgreSQL counterpart of
`SeedPreparedWithoutPrepareRulesAsync` in the Authority unit tests. No production code, configuration, verification or validation
is changed: Commit verifies the staged bytes and signatures exactly as in production. The signing provider itself is not under test.
