# Phase C8 M4-2…M4-4: sanitized Stage B run summaries

- **Prepared for:** the AC-8 reviewer package (`docs/architecture/PHASE_C8_M4_AC8_REVIEWER_PACKAGE.md`), under owner decision OD-6 (C-174).
- **Nature:** owner-led Development/Testing evidence. **Not an independent review, not hosted CI, not production evidence.**
- **Source:** the local, git-ignored run folders `artifacts/test-runs/<run id>/` written by `scripts/dev/stage-b.ps1 -Class <class> -Fresh`, each on a freshly recreated disposable `enterpriseforms-sectest` Docker stack on the implementer's workstation. The raw TRX and `run.json` files stay local; their SHA-256 digests are listed so a holder of the files can match them. Raw TRX, container logs, test output and error text are not reproduced.
- **Removed:** machine and user names, file paths, organization, command, record and correlation identifiers, test output and error messages. A generator check refuses to write the file if any of these patterns appears.
- **Code state:** each run executed the working tree of its milestone branch at the time (uncommitted until the milestone's signed commit); the milestone commits are M4-2 `3c645ac` (PR #60), M4-3 `ce4789c` (PR #61), M4-4 `6142f91` (PR #62). `run.json` does not record a commit, so the binding of a run to a commit rests on the status log (`docs/PHASE_C8_STATUS.md` §41.6, §42.6, §43.6–§43.9), not on the file.
- **Every run is listed, including failed ones.** Failed runs and their corrections are described in the status log.

## Runs

### 20261120-015735-1c1ac5c0

- **Milestone / authorizing decision:** M4-2 / C-163. **Status:** Failed: defect V-1 (fixed before the next run).
- **Class:** `SecurityAuditAccessTests`; retry of: `—`; elapsed 39 s; started 2026-11-20T06:57:50.7661718+00:00, finished 2026-11-20T06:58:12.8934869+00:00 (UTC).
- **Totals:** 7 total, 6 passed, 1 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `6774cb299a9d2a60a01e7c7fa3c01522b97ebe144c39e77551103c1e9a0ac8e3`; run.json SHA-256 `37fa86c4e097b3752182b72e8deeae2e6b8e8da1014c9b031138d8c23be528db`.

| Test | Outcome | Duration |
|---|---|---|
| `ABurstAboveTheUserLimitIsRefusedWith429` | Passed | 00:00:00.8730000 |
| `AHeldOrganizationLockIsNeverWaitedOnAndTheReadFailsFastAsBusy` | Passed | 00:00:07.5260000 |
| `ARevocationCommitsUnderAnAuditReadFloodAndItsDurationIsRecorded` | Passed | 00:00:04.7500000 |
| `ARevokedAuditorsNextPageAndDetailAreRefused` | Passed | 00:00:00.8990000 |
| `AnAuditorsPageIsServedWithACommittedAuthorityEntryThatSurvivesDeletionOfTheBusinessRow` | Passed | 00:00:04.4900000 |
| `AnotherOrganizationsAuditorSeesNothingOfThisOrganization` | Failed | 00:00:01.4770000 |
| `NonAuditorsAreRefusedAndNoAuthorizationIsRecordedForThem` | Passed | 00:00:01.3180000 |

### 20261120-020202-71e3c0be

- **Milestone / authorizing decision:** M4-2 / C-163. **Status:** Accepted run (C-164).
- **Class:** `SecurityAuditAccessTests`; retry of: `—`; elapsed 25 s; started 2026-11-20T07:02:05.5977665+00:00, finished 2026-11-20T07:02:26.9406865+00:00 (UTC).
- **Totals:** 7 total, 7 passed, 0 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `c0d7fed11f8a4ee49cd91b631197d0179a063e94f1cebae9ff28391fb5751e35`; run.json SHA-256 `1688dc005f2c54faee493955e5ee1a81c924d42b66531347204b674d15784d43`.

| Test | Outcome | Duration |
|---|---|---|
| `ABurstAboveTheUserLimitIsRefusedWith429` | Passed | 00:00:00.9780000 |
| `AHeldOrganizationLockIsNeverWaitedOnAndTheReadFailsFastAsBusy` | Passed | 00:00:07.4630000 |
| `ARevocationCommitsUnderAnAuditReadFloodAndItsDurationIsRecorded` | Passed | 00:00:04.7390000 |
| `ARevokedAuditorsNextPageAndDetailAreRefused` | Passed | 00:00:00.9000000 |
| `AnAuditorsPageIsServedWithACommittedAuthorityEntryThatSurvivesDeletionOfTheBusinessRow` | Passed | 00:00:03.7630000 |
| `AnotherOrganizationsAuditorSeesNothingOfThisOrganization` | Passed | 00:00:01.2780000 |
| `NonAuditorsAreRefusedAndNoAuthorizationIsRecordedForThem` | Passed | 00:00:01.4760000 |

### 20261120-020228-686be1ff

- **Milestone / authorizing decision:** M4-2 / C-163. **Status:** Failed: finding F-M42-V1 (busy refusal; accepted under option 1).
- **Class:** `AuditExplorerTests`; retry of: `—`; elapsed 22 s; started 2026-11-20T07:02:31.6060488+00:00, finished 2026-11-20T07:02:48.7800104+00:00 (UTC).
- **Totals:** 6 total, 5 passed, 1 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `208f568e4f62b6748d94d1aad541a5f3ea8bf76831b0dc7b90fb14c810ba8eff`; run.json SHA-256 `52522ae2b034dfafcf2036650481d4b458bbb82afa721553c84da3a1d85e2755`.

| Test | Outcome | Duration |
|---|---|---|
| `ACursorBelongsToItsCallerOrganizationAndFilters` | Passed | 00:00:02.7270000 |
| `DeliveryOutcomesAreRecordedForPagesDetailsAndNotFound` | Passed | 00:00:01.1010000 |
| `EveryFilterNarrowsToTheMatchingRowsOnly` | Failed | 00:00:03.1300000 |
| `KeysetPagingIsStableUnderConcurrentInsertsAndBreaksTiesBySequence` | Passed | 00:00:03.5040000 |
| `NoListOrDetailResponseContainsASentinel` | Passed | 00:00:03.5260000 |
| `TheOrganizationPredicateHoldsWithASecondOrganizationPresent` | Passed | 00:00:02.5600000 |

### 20261120-020620-4eca3728

- **Milestone / authorizing decision:** M4-2 / C-163. **Status:** Accepted run (C-164); retry of 686be1ff on a fresh stack.
- **Class:** `AuditExplorerTests`; retry of: `20261120-020228-686be1ff`; elapsed 32 s; started 2026-11-20T07:06:29.9754726+00:00, finished 2026-11-20T07:06:51.6278461+00:00 (UTC).
- **Totals:** 6 total, 6 passed, 0 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `62267f5dd7765da05297c19f0d816a3354031ebec32abf620cc4f3227ad45f72`; run.json SHA-256 `1c5afceb3b590c7863320b0668be1a3247677cae81e131211c555b11ae6fc70c`.

| Test | Outcome | Duration |
|---|---|---|
| `ACursorBelongsToItsCallerOrganizationAndFilters` | Passed | 00:00:02.9820000 |
| `DeliveryOutcomesAreRecordedForPagesDetailsAndNotFound` | Passed | 00:00:03.0130000 |
| `EveryFilterNarrowsToTheMatchingRowsOnly` | Passed | 00:00:03.8890000 |
| `KeysetPagingIsStableUnderConcurrentInsertsAndBreaksTiesBySequence` | Passed | 00:00:03.6760000 |
| `NoListOrDetailResponseContainsASentinel` | Passed | 00:00:04.0490000 |
| `TheOrganizationPredicateHoldsWithASecondOrganizationPresent` | Passed | 00:00:03.2540000 |

### 20261120-045331-b875f312

- **Milestone / authorizing decision:** M4-3 / C-166. **Status:** Failed: defect F-M43-V1 (corrected).
- **Class:** `SecurityCommandTimelineTests`; retry of: `—`; elapsed 27 s; started 2026-11-20T09:53:34.8450570+00:00, finished 2026-11-20T09:53:56.6638011+00:00 (UTC).
- **Totals:** 4 total, 3 passed, 1 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `48be6ec66984bd37963231b4256218aa8d40e53c449b77d65273d0d3b618c72e`; run.json SHA-256 `c010ffc327f4a7c06211cd8c72c2dc23ed2d31ed65f333d0d61cd1765ab979ca`.

| Test | Outcome | Duration |
|---|---|---|
| `ARevocationCommitsUnderATimelineReadFloodAndItsDurationIsRecorded` | Passed | 00:00:04.9790000 |
| `AnotherOrganizationsAuditorSeesNoneOfThisOrganizationsCommands` | Passed | 00:00:03.9490000 |
| `TheKeysetAndFiltersHoldOnPostgreSql` | Passed | 00:00:02.2719999 |
| `TimelineEnvelopesAreRestrictedFieldByFieldAndCarryNoSentinel` | Failed | 00:00:09.9590000 |

### 20261120-050134-e90c1a43

- **Milestone / authorizing decision:** M4-3 / C-166. **Status:** Failed: test defect F-M43-V2 (corrected).
- **Class:** `SecurityCommandTimelineTests`; retry of: `20261120-045331-b875f312`; elapsed 29 s; started 2026-11-20T10:01:37.2134103+00:00, finished 2026-11-20T10:02:01.6109008+00:00 (UTC).
- **Totals:** 4 total, 3 passed, 1 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `25f02be0aebfffa102d56dbad3955affd08e691bdb4c34f25c7546647382a564`; run.json SHA-256 `953664e4c392cdeaec813b5a11d87470c4a540d6d1994cd45eb51d1dc84754a6`.

| Test | Outcome | Duration |
|---|---|---|
| `ARevocationCommitsUnderATimelineReadFloodAndItsDurationIsRecorded` | Passed | 00:00:04.9189999 |
| `AnotherOrganizationsAuditorSeesNoneOfThisOrganizationsCommands` | Passed | 00:00:05.9290000 |
| `TheKeysetAndFiltersHoldOnPostgreSql` | Passed | 00:00:02.2839999 |
| `TimelineEnvelopesAreRestrictedFieldByFieldAndCarryNoSentinel` | Failed | 00:00:10.4820000 |

### 20261120-050354-ef37489e

- **Milestone / authorizing decision:** M4-3 / C-166. **Status:** Accepted run (C-167).
- **Class:** `SecurityCommandTimelineTests`; retry of: `20261120-050134-e90c1a43`; elapsed 32 s; started 2026-11-20T10:04:04.4551997+00:00, finished 2026-11-20T10:04:26.4502786+00:00 (UTC).
- **Totals:** 4 total, 4 passed, 0 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `b7b64cd8b0696c639e99ebf1a3edc400452f809cbd65e0518efdbd31b9627ec6`; run.json SHA-256 `82d3e5d01415d63dee6db294c02d1b72d0721498605ff2ab6e55e38adb10e0a3`.

| Test | Outcome | Duration |
|---|---|---|
| `ARevocationCommitsUnderATimelineReadFloodAndItsDurationIsRecorded` | Passed | 00:00:04.7930000 |
| `AnotherOrganizationsAuditorSeesNoneOfThisOrganizationsCommands` | Passed | 00:00:04.1840000 |
| `TheKeysetAndFiltersHoldOnPostgreSql` | Passed | 00:00:02.3809999 |
| `TimelineEnvelopesAreRestrictedFieldByFieldAndCarryNoSentinel` | Passed | 00:00:09.9880000 |

### 20261120-195129-d7e086ef

- **Milestone / authorizing decision:** M4-4 / C-170. **Status:** Failed: test defects F-M44-V1, F-M44-V2 (corrected).
- **Class:** `SecurityHomeTests`; retry of: `—`; elapsed 78 s; started 2026-11-21T00:51:45.9221386+00:00, finished 2026-11-21T00:52:45.2816124+00:00 (UTC).
- **Totals:** 5 total, 3 passed, 2 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `993b08c6c8e761a81a42605f65d0f0769db2317c308f3a426d2717361b96007a`; run.json SHA-256 `efabc8765e61f7809f0ab56789a91e70ee57510e5475ee7b10b0a426a302c31d`.

| Test | Outcome | Duration |
|---|---|---|
| `CoverageHasOneRowPerAuthorityDecisionMissingWithoutAttestationAndNoRecordIds` | Passed | 00:00:10.6140000 |
| `CoverageStatesFollowARealAttestationAndTheOrganizationWideContentRevision` | Passed | 00:00:11.1250000 |
| `EachSectionIsAuthorizedSeparatelyAndCarriesCountsAndKindsOnly` | Passed | 00:00:05.5870000 |
| `T09AnotherOrganizationsIdIsTheSameNotFoundAtTheAuthority` | Failed | 00:00:01.1860000 |
| `T12WhileTheGateIsClosedPendingGrantsConferNothingAdministratorsStillReadAndCommittedRevokesApplyAtOnce` | Failed | 00:00:29.7950000 |

### 20261120-205839-0f934820

- **Milestone / authorizing decision:** M4-4 / C-171. **Status:** Failed: test defect F-M44-V3 (corrected).
- **Class:** `SecurityHomeTests`; retry of: `—`; elapsed 53 s; started 2026-11-21T01:58:49.6375760+00:00, finished 2026-11-21T01:59:30.6471545+00:00 (UTC).
- **Totals:** 5 total, 4 passed, 1 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `8a2944477e4a63b78cf45c17b2b27bdd794881b4156013249d67988b4ebecf0b`; run.json SHA-256 `135d745ba6253c51486e269eba6469b4ad31f0f13d20446feb4453bad08be306`.

| Test | Outcome | Duration |
|---|---|---|
| `CoverageHasOneRowPerAuthorityDecisionMissingWithoutAttestationAndNoRecordIds` | Passed | 00:00:09.1940000 |
| `CoverageStatesFollowARealAttestationAndTheOrganizationWideContentRevision` | Passed | 00:00:11.0390000 |
| `EachSectionIsAuthorizedSeparatelyAndCarriesCountsAndKindsOnly` | Passed | 00:00:05.7990000 |
| `T09AnotherOrganizationsIdIsTheSameNotFoundAtTheAuthority` | Passed | 00:00:01.2960000 |
| `T12PendingGrantsConferNothingACommittedRevokeDeniesAtOnceAndEffectiveHoldersKeepTheirSections` | Failed | 00:00:12.9240000 |

### 20261120-210905-b5e56ad3

- **Milestone / authorizing decision:** M4-4 / C-172. **Status:** Accepted run (C-173).
- **Class:** `SecurityHomeTests`; retry of: `—`; elapsed 61 s; started 2026-11-21T02:09:15.5425929+00:00, finished 2026-11-21T02:10:06.8533503+00:00 (UTC).
- **Totals:** 5 total, 5 passed, 0 failed, 0 not executed.
- **Raw-file digests (files kept locally, not committed):** TRX SHA-256 `84c511c97fdb95679fe05d60209987f2a6b49e5a9326e36a4f44c05ba928bbb5`; run.json SHA-256 `e7eda66cf409f078a906689f419fb823b4bd1f3d3c91c262987ad56c69ce8f5c`.

| Test | Outcome | Duration |
|---|---|---|
| `CoverageHasOneRowPerAuthorityDecisionMissingWithoutAttestationAndNoRecordIds` | Passed | 00:00:08.5320000 |
| `CoverageStatesFollowARealAttestationAndTheOrganizationWideContentRevision` | Passed | 00:00:09.8560000 |
| `EachSectionIsAuthorizedSeparatelyAndCarriesCountsAndKindsOnly` | Passed | 00:00:05.6440000 |
| `T09AnotherOrganizationsIdIsTheSameNotFoundAtTheAuthority` | Passed | 00:00:01.1700000 |
| `T12PendingGrantsConferNothingACommittedRevokeDeniesAtOnceAndEffectiveHoldersKeepTheirSections` | Passed | 00:00:25.4660000 |
