# E3-M2b-1 evidence: CARV against real pre-CARV Authority binaries (design gate, 2026-11-14)

**What this is.** Design-gate evidence for the proposed CARV database fence, required by `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §19. **No CARV application code exists.** The SQL under `sql/` is a design artifact, not a repository migration.

**Review.** Owner-led design work; not an independent review.

## How it ran

- **Binaries.** The real Release builds of two pre-CARV Authority versions were run, unmodified:
  - `66f0aa7` (E3-M2a; `oldbin-sha256.txt`);
  - `de1729b` (pre-E3-M2a; `pre-m2a-de1729b/oldbin-sha256.txt`).

  The harness compiles against those assemblies and runs byte-identical copies of them. The runner re-checks the hashes before every run.
- **Code exercised.** Their own `ContentService` Prepare and Commit, and their own EF Core `AuthorityStore`, running on the least-privilege runtime role. The host's own `AuthorityExceptionHandler` gives the HTTP mapping of each refusal. The HTTP routing and authentication layer is not exercised; it does not take part in database enforcement.
- **Schema.** Created by each binary's own `--migrate` entry point (29 migrations). The proposed CARV SQL was then applied on top, in the same single-transaction form an EF migration uses.
- **Database.** A disposable `postgres:18.0` container: image `sha256:6cb75ec2…` (`postgres-image.txt`), data on tmpfs, port bound to loopback only, removed when the run ends. No developer, security-test or persistent database was touched.
- **Holding transactions open.** Done only through the binaries' own ports:
  - `IFaultInjector` holds a Commit transaction after every check, before its writes;
  - a wrapped `IIntegrityMacProvider` holds a Prepare transaction while it signs, before its insert.
- **CARV-build emulation (test only).** No CARV build exists yet. A test-only trigger (`sql/emulation.sql.txt`), active only on connections that set `carv.emulate_version`, writes the stamps a CARV build would write. Everything else a "CARV instance" does is the unmodified legacy code. The CARV application rules themselves are **not** exercised: Commit-time refusal, the Prepare replay rule, error mapping and the startup guard. They are M2b-1 implementation tests (contract §19.10).

## Files

| File | Content |
|---|---|
| `carv-evidence.json`, `carv-log.txt` | Final run against `66f0aa7`: **47 checks, 0 failed**, with an identical repeat run |
| `pre-m2a-de1729b/` | The same scenarios and SQL against `de1729b`: **47 checks, 0 failed** |
| `run1-uncorrected-sql/` | The first run, using the SQL as originally drafted: **16 of 47 failed**. Kept as evidence of defect D-1 (contract §19.2) |
| `sql/*.sql.txt` | The corrected SQL (hashes in `sql-sha256.txt`, identical for both binaries) |
| `carv-harness-Program.cs.txt`, `carv-harness.csproj.txt`, `carv-run.ps1.txt` | The harness and runner (scratchpad sources, `.txt` so nothing here can be compiled or run as a migration) |
| `schema-migrations.txt`, `postgres-version.txt`, `postgres-image.txt`, `oldbin-sha256.txt` | Environment and binary identity |

## Reproduce

Copy the harness and runner back to a scratch folder: remove the `.txt` suffix, and point `oldbin` at the Release output of the binary under test. Then run `powershell -File carv-run.ps1`. It needs Docker and the `postgres:18.0` image. Nothing persists after the run.
