# C8 M4-1 S1 and M1 implementation evidence (C-117)

- **What this is:** test-run summaries and latency measurements from Claude (Opus) on 2026-11-04/05, on the disposable security-test stack (compose project `enterpriseforms-sectest`, `authzed/spicedb:v1.56.2`). The stack was recreated (`down -v`, fresh build) before each shard.
- **What this is not:**
  * an independent review;
  * PB-09 or Azure staging evidence;
  * a production-readiness claim.
- **Commits:** S1 `5f5fb07`; M1 `9b47a8b`; S1 test fix `051a65d` (RC24). All are signed, local and unpushed.

## Files

| File | Content |
|---|---|
| `shards-s1-5f5fb07-<group>.txt` | The seven Stage B shards at `5f5fb07`, each on a fresh stack: failures by name, counts, elapsed time |
| `search-benchmark-*.json`, `search-benchmark-*-run.txt` | Search verification benchmark: pre-S1 and S1, the class run alone on a fresh stack, plus the S1 in-shard figures (below) |
| `shards-m1-051a65d-<group>.txt` | The M1-affected shards (`administration-membership`, `authorization-security-onboarding`) at `051a65d` (S1 + M1 + RC24 fix), each on a fresh stack |
| `shard-authorization-security-onboarding-run1.txt` | First S1 run of the authorization shard, 92/93: RB-10 failed because of a test setup error (it went through the business pre-check while SpiceDB was paused). Corrected before the commit |
| `s1-authorization-latency-pre-s1*.txt` | `AuthorizationLatencyTests` (PERF-01 local) on a worktree at `70894d2` (before S1) |
| `s1-authorization-latency-s1-*.txt` | The same test with S1. `s1-ab-1/2` ran from the working tree before the S1 commit, so their header names `70894d2`. The image digests in each header identify the build actually measured. `s1-in-shard` and `s1-warm-1` ran during or after other tests on the same stack and are noisier |

## Latency (fair A/B: fresh stack per run, two runs each, p95 in ms)

| Load | Pre-S1 (ab-1, ab-2) | S1 (ab-1, ab-2) | Added (mean) |
|---|---|---|---|
| single check @ 50 rps | 14.80, 15.59 | 21.86, 21.40 | **+6.4** |
| single check @ 150 rps | 20.62, 16.65 | 27.86, 38.63 | **+14.6** (+7.2 and +22.0) |
| batch of 25 @ 50 rps | 16.80, 14.55 | 22.70, 25.62 | **+8.5** |

- p50 added: about +5.5 ms (two extra SpiceDB round trips: the pin and the verify marker reads).
- **B-Q9 (added p95 ≤ 15 ms):**
  * met at 50 rps;
  * **not reliably met at 150 rps** on this host. One of the two runs added 22 ms.
  * The developer laptop also ran the owner's dev stack at the time; an earlier single pre-S1 run measured p95 11.1 ms.
- Azure staging PERF-01 is the acceptance measurement. Optimizations O-B2 (concurrent M2 and R2) and O-B1 (no M1 on check-only paths) are not implemented; they need separate approval.

## Search verification benchmark (`SearchCatalogueTests.BenchmarkTheVerifiedScopeAt50And150And480Records`, server ms, p50)

| Run | 50 | 150 | 480 |
|---|---|---|---|
| Pre-S1 `70894d2`, class alone, fresh stack (`search-benchmark-pre-s1-70894d2.json`) | 1,990 | 5,738 | 12,481 (passed) |
| S1 `5f5fb07`, in the shard (`search-benchmark-s1-5f5fb07-in-shard.json`) | 1,327 | 4,276 | refused, time limit |
| S1 + M1 `051a65d`, class alone, fresh stack (`search-benchmark-s1-051a65d.json`) | 1,561 | 4,451 | refused, time limit |

**Verdict:** S1 probably contributes to the 480-record refusal; this is not proven.
- At 50 and 150 records the S1 runs were faster than the pre-S1 run, so host variance dominates.
- Pre-S1 already used 83% of the 15 s budget.
- The refusal is the typed fail-closed answer, with no partial result.

See `docs/PHASE_C8_STATUS.md` §39.4 (finding P-S1-1).

## P-S1-1 timing probe (C-119, 2026-11-05)

Throwaway instrumentation, never committed, applied to `f117995` in a scratch worktree. The benchmark class was run once on a fresh stack.

| File | Content |
|---|---|
| `p-s1-1-probe-instrumentation.diff` | The exact timers added: `Stopwatch` around the S1 pin and verify, the SpiceDB decision calls, the MAC-provider calls, catalogue issuance, and the business-side content reads and verify calls |
| `p-s1-1-probe-authority-lines.txt`, `p-s1-1-probe-business-lines.txt` | Raw `P1PROBE` log lines from the Authority and API containers |
| `p-s1-1-probe-summary.txt` | Medians per 50-record batch, and the 480-record attempts |
| `p-s1-1-probe-run.txt` | Class result: 11/12; the 480-record scope was refused again (`time`) |

**Result:**
- S1 pin and verify: 2.8 ms and 4.6 ms per 50-record batch (0.7%); 68 ms in total across the 8 of 10 batches finished in the refused 480-record scope.
- MAC verification: 54% of Authority batch time.
- SpiceDB checks: 19%.
- BusinessDb content reads: about a third of the search.

Analysis and options: `docs/architecture/PHASE_C8_M4_1_P_S1_1_SEARCH_OPTIMIZATION_PROPOSAL.md`.

## Final Stage B matrix at `f117995` (C-119, 2026-11-05/06)

| File | Content |
|---|---|
| `shards-final-f117995-<group>.txt` | The final pass of all seven shards at `f117995`, each on a fresh stack, with failure excerpts. `protection-reconciliation` is marked INCOMPLETE: the host slept during it |
| `shards-final-rerun-f117995-<group>.txt` | The single reruns: `protection-reconciliation` 22/22 and `authorization-security-onboarding` 92/93 (the same failure) |
| `shards-final-rerun-f117995-quarantine-alerts.txt` | The Authority's quarantine alerts during the authorization rerun. RB-13's datastore replacement quarantined every organization (`PROJECTION_SCHEMA_MISSING`), including the seeded organization |

**Result:** five of seven shards pass in full at `f117995`; the suite is not green. See `docs/PHASE_C8_STATUS.md` §40.1.
