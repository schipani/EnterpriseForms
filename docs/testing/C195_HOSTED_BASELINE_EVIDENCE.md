# C-195 hosted Stage B baseline evidence

Recorded 2026-11-25 under EnterpriseForms Coordinator Assignment 001 §4. The one-time authorization and C9 close-out are recorded in [PR #77](https://github.com/schipani/EnterpriseForms/pull/77).

## Run and environment

- [Run 45592321078, attempt 1](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078) succeeded. The plan recorded workflow dispatch on main at fbf18bc734f9b6d9563a94a66721af49a7177003, release authorization C-195, groups search-catalogue and projection-integrity-restore, and browser false. The browser job was skipped. [Plan job](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078/job/139129431009) · [Plan artifact](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078/artifacts/15014319272)
- The two shards ran on separate GitHub-hosted Ubuntu 24.04 runners, runner version 2.338.2, image version 20261116.133.1. Search-catalogue used the clean security-test Compose stack on the tested commit. The startup log shows locally built Authority, API and worker, PostgreSQL 18.0, SpiceDB v1.56.2, Vault 1.20.4, Azurite 3.9.0 and ClamAV 1.4. The tested overlay configured the cheap sweep and deadline evaluation defaults at 10 seconds. The benchmark evidence did not record actual CPU allocation, sweep frequency or organization census. [Search-catalogue job](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078/job/139155600981) · [Test overlay](https://github.com/schipani/EnterpriseForms/blob/fbf18bc734f9b6d9563a94a66721af49a7177003/infrastructure/docker/compose.security-test.yaml)

## Selected test results

| Group | Discovered and executed | Passed | Failed |
| --- | ---: | ---: | ---: |
| search-catalogue | 65 | 65 | 0 |
| projection-integrity-restore | 14 | 14 | 0 |

The coordinator reproduced the exact-once group checks with the downloaded discovery lists and TRX files. RB-13 passed in 25.104 s. Its assertions cover denial and quarantine on an empty replacement SpiceDB datastore, continued denial after restoring an older datastore with marker-behind quarantine, and service to a fresh organization afterward. This result is hosted Development/Test evidence for the named scenario. It leaves the PRD-5 staging recovery obligation open. [Aggregate job](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078/job/139169897013) · [RB-13 TRX artifact](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078/artifacts/15013864863) · [RB-13 test](https://github.com/schipani/EnterpriseForms/blob/fbf18bc734f9b6d9563a94a66721af49a7177003/tests/integration/EnterpriseForms.StageBTests/Tests/ProjectionIntegrityRollbackTests.cs#L311)

## Verified-search catalogue measurements

The values come from the completed benchmark's captured TRX text messages for this run and the previous hosted baseline, [run 44012820545](https://github.com/schipani/EnterpriseForms/actions/runs/44012820545), on c24520048e167f3852f76add653dc28da13da851. The previously quoted 0.66 / 2.00 / 6.62 seconds are rounded server p50. Both runs exclude one warm-up per scope. [Current TRX artifact](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078/artifacts/15016836874) · [Prior TRX artifact](https://github.com/schipani/EnterpriseForms/actions/runs/44012820545/artifacts/14298296278)

| Records | Samples | Prior server p50 / p95 (s) | C-195 server p50 / p95 (s) | C-195 client p50 / p95 (s) | Server p50 change | Authority calls / batches | C-195 verified bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 50 | 15 | 0.6642 / 0.7982 | 0.5906 / 0.7999 | 0.5917 / 0.8014 | −11.1% | 3 / 1 | 187,650 |
| 150 | 12 | 1.9955 / 2.0893 | 2.4474 / 2.8103 | 2.4482 / 2.8112 | +22.6% | 5 / 3 | 562,950 |
| 480 | 8 | 6.6219 / 6.7567 | 8.2169 / 8.5086 | 8.2178 / 8.5098 | +24.1% | 12 / 10 | 1,801,440 |

The two larger scenarios were slower on this run. All benchmark assertions passed. The runs used different commits and hosted image versions (prior 20261111.370.1), and C-195 verified about 2.3% more bytes per scope with the same Authority-call and batch counts. The samples are small. These observations establish neither the cause of the difference nor a reproducible code regression. The 480-record result remains outside the 3 s PB-09 objective, as it was on the prior hosted run. P-S1-1 and its remediation remain assigned to PRD-4. [Prior runner job](https://github.com/schipani/EnterpriseForms/actions/runs/44012820545/job/133803797776) · [PB-09](../performance/PB09_CAPACITY_SLO_STANDARD.md)

The evidence covers two groups on the pre-BH-1 SHA. Post-BH-1 hosted verification of all 12 shards requires a separate owner authorization under [C-131](STAGE_B_RELEASE_ONLY_POLICY.md).
