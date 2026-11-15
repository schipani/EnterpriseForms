# E3-M2b-1 implementation evidence: CARV against real PostgreSQL (2026-11-14)

**What this is.** Implementation evidence for the content acceptance rule version (CARV) fence, required by `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §19.10 (M2-CARV-PG) and recorded in §20. The design-gate evidence one level up (`../`, including `../run1-uncorrected-sql/`, the failed first design) is unchanged and preserved.

**Review.** Owner-led Development/Testing work; not an independent review, not a certification.

## Binaries and environment

| Item | Identity |
|---|---|
| Legacy builds | The frozen design-gate Release builds of `66f0aa7` (E3-M2a) and `de1729b` (pre-E3-M2a); their SHA-256 hashes equal `../oldbin-sha256.txt` and `../pre-m2a-de1729b/oldbin-sha256.txt` and were re-verified before every run. |
| E3-M2b-1 build | A Release build of the final uncommitted working tree on `e3/m1-envelope-v2-core` (base `66f0aa7`), made by the gate itself: `runs-of-record/new-sha256.txt`. Release builds of this repository are not byte-reproducible (contract §20.6), so this hash identifies this build only. |
| Database | A fresh `postgres:18.0` container per run (PostgreSQL 18.0, image `sha256:6cb75ec2…`, the design gate's image): tmpfs data, loopback port, removed afterwards. No developer, security-test or persistent database was touched. |
| Migration | `20261115014221_AuthorityContentAcceptanceRuleVersion`, applied by the E3-M2b-1 build's own `--migrate`; its EF-scripted Up and Down are `sql/real_up.sql.txt` and `sql/real_down.sql.txt` |

## Runs of record (`runs-of-record/`, produced by `scripts/dev/e3-m2b1-carv-pg-gate.ps1`)

| Folder | Harness | Result |
|---|---|---|
| `harness-a-66f0aa7/` | A: the 47 design-gate checks, unchanged, with the real migration in place of the design SQL, plus RM-01..03; CARV instances are the test-only database emulation | **50 checks, 0 failed** |
| `harness-a-de1729b/` | A | **50 checks, 0 failed** |
| `harness-b-66f0aa7/` | B: the real E3-M2b-1 build in-process (no emulation) with the legacy build through the legacy agent | **36 checks, 0 failed** |
| `harness-b-de1729b/` | B | **36 checks, 0 failed** |

`summary.json` is the gate's own summary (every run exit code 0, every container removed). The two `legacy-*-origin.txt` files were rewritten afterwards to remove a local scratch path; the gate script now never records local paths. Each folder holds `carv-evidence.json` (every check with its expectation and observation), `carv-log.txt`, `console.txt` and the PostgreSQL identity; harness B also writes `trigger-function-privilege-inventory.txt` (G08).

`development-runs/` holds the first runs of the same harnesses (scratchpad runners in `sources/`, an earlier Release build of the working tree: `development-runs/newbin-sha256.txt`), with identical results (50/50 ×2, 36/36 ×2). The enforcement-removal checks used those harness builds.

## Enforcement-removal checks

`mutations/application-removals.json` (MA-1..MA-10, in-memory and contract tests) and `mutations/postgres-removals.json` (MD-1..MD-6, MR-1..MR-3, real PostgreSQL), summarised in the contract §20.6. Every mutated file was restored and its SHA-256 verified against its backup. (A rebuild of the restored source does **not** reproduce `development-runs/newbin-sha256.txt` (`mutations/rebuild-after-restore-sha256.txt`): Release builds of this repository are not byte-reproducible, so binary hashes cannot serve as a restore proof; see the contract §20.6.)

## Sources

The harness, legacy-agent and test-only SQL sources are in the repository at `tools/experiments/e3-m2b1-carv-gate/` (byte-identical to the as-run copies: HarnessA `6360c23c…`, HarnessB `2a241dfb…`, LegacyAgent `f566bd32…`; the test-only SQL has the design gate's hashes). The re-runnable gate is `scripts/dev/e3-m2b1-carv-pg-gate.ps1` (OD-M2-29). `sources/` holds the scratchpad runners and mutation drivers used for these runs, as `.txt` so nothing here can be run as a migration or script by accident.
