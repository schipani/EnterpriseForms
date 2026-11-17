# RB-CARV-1 evidence: the Release B content write fence against real PostgreSQL (2026-11-15)

**What this is.** Implementation evidence for the RB-CARV-1 hardening (contract `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §22): the mandatory Release A / Release B policy and the transactional write fence of the Authority. The E3-M2b-1 design-gate evidence (`../`), the E3-M2b-1 implementation evidence (`../implementation/`) and the E3-M2b-1F floor evidence (`../floor/`) are unchanged and preserved.

**Review.** Owner-led Development/Testing work; not an independent review, not a certification. The floor was activated, removed, weakened and restored only in disposable containers. No Azure service was called.

## Binaries and environment

| Item | Identity |
|---|---|
| Legacy builds | The frozen design-gate Release builds of `66f0aa7` (E3-M2a) and `de1729b` (pre-E3-M2a); `runs-of-record/legacy-*-sha256.txt` equal `../oldbin-sha256.txt` and `../pre-m2a-de1729b/oldbin-sha256.txt` |
| RB-CARV-1 build | A Release build of the RB-CARV-1 working tree as it stood at the gate run, before the commit (base `8c6367b`) made by the gate: `runs-of-record/new-sha256.txt`. Release builds of this repository are not byte-reproducible (contract §20.6) |
| Database | A fresh `postgres:18.0` container per run (tmpfs, loopback, removed afterwards); harness R's container alone runs with `track_commit_timestamp=on` |

## Runs of record (`runs-of-record/`, `scripts/dev/e3-m2b1-carv-pg-gate.ps1 -LegacyBinariesRoot <frozen builds>`)

| Folder | Harness | Result |
|---|---|---|
| `harness-a-*` | A: E3-M2b-1 regression | 50/50 each |
| `harness-b-*` | B: E3-M2b-1 regression (the real CARV build, Release A policy) | 36/36 each |
| `harness-f-*` | F: E3-M2b-1F floor regression | 30/30 each |
| `harness-r-66f0aa7/`, `harness-r-de1729b/` | **R: RB-CARV-1 scenarios A–H** | **42/42 each** |

`summary.json` is the gate's own summary (exit codes, check counts, containers removed).

## Enforcement-removal checks

`mutations/rb-carv-1-removals.json`: MU-1..MU-7 (Authority tests) and MP-1..MP-6 (harness R on real PostgreSQL, legacy `66f0aa7`); every removal detected; every mutated file restored and SHA-256 verified. Local paths are replaced by `<repo>` and `<scratch>`.

## Sources

`sources/`: the removal driver, the single-run harness R runner and the catalog probe used to capture PostgreSQL 18.0's rendering of the fence objects, with its output (all as `.txt`). Harness R itself is `tools/experiments/e3-m2b1-carv-gate/HarnessR`.
