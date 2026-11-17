# E3-M2b-1F evidence: the CARV database floor against real PostgreSQL (2026-11-14)

**What this is.** Implementation evidence for the content acceptance rule floor (floor 1), recorded in `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §21. The E3-M2b-1 design-gate evidence (`../`, including the failed first SQL in `../run1-uncorrected-sql/`) and the E3-M2b-1 implementation evidence (`../implementation/`) are unchanged and preserved.

**Review.** Owner-led Development/Testing work; not an independent review, not a certification. The floor was activated only in disposable containers.

## Binaries and environment

| Item | Identity |
|---|---|
| Legacy builds | The frozen design-gate Release builds of `66f0aa7` (E3-M2a) and `de1729b` (pre-E3-M2a); `runs-of-record/legacy-*-sha256.txt` equal `../oldbin-sha256.txt` and `../pre-m2a-de1729b/oldbin-sha256.txt` |
| E3-M2b-1F build | A Release build of the final uncommitted working tree (base `c44be41`) made by the gate: `runs-of-record/new-sha256.txt`. Release builds of this repository are not byte-reproducible (contract §20.6) |
| Database | A fresh `postgres:18.0` container per run (tmpfs, loopback, removed afterwards) |
| Floor migration | `20261115032228_AuthorityContentAcceptanceRuleFloor1` (context `ContentAcceptanceFloorContext`), applied only by `--apply-content-acceptance-floor 1`. Its EF-scripted SQL: `sql/floor_up.sql.txt`, `sql/floor_down.sql.txt` |

## Runs of record (`runs-of-record/`, `scripts/dev/e3-m2b1-carv-pg-gate.ps1 -LegacyBinariesRoot <frozen builds>`)

| Folder | Harness | Result |
|---|---|---|
| `harness-a-*` | A: E3-M2b-1 regression (47 design-gate checks + RM-01..03) | 50/50 each |
| `harness-b-*` | B: E3-M2b-1 regression (the real CARV build) | 36/36 each |
| `harness-f-66f0aa7/`, `harness-f-de1729b/` | **F: the floor** (FX-01..FX-27 plus FX-12a/b) | **30/30 each** |

`summary.json` is the gate's own summary (exit codes, check counts, containers removed).

## Enforcement-removal checks

`mutations/floor-removals.json`: MF-1..MF-6 (Authority tests) and MP-1..MP-7 (harness F on real PostgreSQL, legacy `66f0aa7`), every one detected except MF-4 at first; `mutations/MF-4-rerun.json`: MF-4 detected after the test was strengthened (contract §21.7). Every mutated file was restored and SHA-256 verified. `sources/` holds the drivers and the scratchpad runner used (as `.txt`).

## Sources

Harness F, the legacy agent (now with an optional clock offset, default 0) and the test-only SQL are in `tools/experiments/e3-m2b1-carv-gate/`; the gate is `scripts/dev/e3-m2b1-carv-pg-gate.ps1`.
