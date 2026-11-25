# Phase C9 — Form Version Management: status

- **Branches:** C-188 `c188/retained-field-search` (from `9903844`; pushed, PR #76 open, not merged, §11). M3 `phase-c9/m3-acceptance` (from `fc9e404`, the authorized baseline; tests and documentation only), squash-merged to `main` as `9903844` (PR #75). M1 `phase-c9/form-version-management` (from `198aeae`), squash-merged to `main` as `cd48e13` (PR #70; branch kept, tag `archive/c9-m1-form-version-management-9395ec5`). M2 `phase-c9/m2-version-comparison` (from `cd48e13`), squash-merged to `main` as `491fa9a` (PR #71; the branch was deleted automatically on merge; its commits are kept by tag `archive/c9-m2-version-comparison-03b5169`).
- **Architecture:** `docs/architecture/PHASE_C9_FORM_VERSION_MANAGEMENT.md`.
- **Decisions:** C-178 (PR #69 merge record, C8); C-179 (owner decisions D9-1…D9-9; WP-0 and M1 authorized); C-180 (M1 implemented; awaiting owner review); C-181 (D9-5 reconciliation and correction, §4); C-182 (M2 implemented; awaiting owner review, §5); C-183 (corrective review of PR #71, §6); C-184 (PR #71 merge record, §7); C-185 (PR #73 merge record and failed post-merge CI, §8); C-186 (test-host startup race, PR #74 and CI recovery, §9); C-187 (M3 authorized and implemented; awaiting owner review, §10); C-188 (§11 #2 searchability of off-layout historical values recorded as UNMET — deferred, §10.4); C-189 (Option B authorized and implemented in Development/Test; awaiting owner review, §11); C-190 (BF-1 remediation, §11.6).
- **State (2026-11-24):** M1 and M2 merged to `main` (code integration only: `cd48e13`, `491fa9a`). Post-merge CI on `main` failed at `491fa9a` (§7) and at `7d0c879` (§8), each time on a test defect, and has been green since `3d930f6` (§9). M3 (WP-7, WP-8; §10) squash-merged to `main` as `9903844` (PR #75); post-merge CI and CodeQL green. **C-188 Option B implemented in Development/Test on `c188/retained-field-search` (§11, C-189), pushed with PR #76 (owner-authorized); BF-1 remediated on the same branch (§11.6, C-190); not reviewed, not merged.** Manual screen-reader review OPEN. CLAUDE.md §11 #2 searchability of off-layout historical values remains UNMET until the owner accepts the C-189 evidence. Not final C9 acceptance, not independently reviewed, no production or Level 2 claim. G21 OPEN.

## 1. What M1 delivers

| Work package | Delivered |
|---|---|
| WP-0 | Decisions C-178/C-179, architecture document, this status, feature-ownership entry, C8 status §46 (PR #69 merged as `198aeae` despite the obsolete commit-subject wording). |
| WP-1 | One selection rule for every record read and write (`FormVersionResolution`, `RecordLayoutSelection`): Historical views and edits with the created version, Latest with the current version, Hybrid views with the created version and edits with the current one. Editing fails closed (`409 EDIT_LAYOUT_UNAVAILABLE`) when the edit version is missing, belongs to another form or the stored behaviour is unknown; another version is never substituted. Update, Submit, restore (preview and apply), upload begin/commit, document removal and child creation validate against the edit layout; Reopen/Archive/Unarchive use the view layout (they validate no content). Stale edit layouts are refused (`409 EDIT_LAYOUT_CHANGED`, D9-5). |
| WP-2 | `PUT /api/v1/form-definitions/{id}/version-behavior` (DesignForm, revision CAS, 3–500 character reason, audited `FORM_VERSION_BEHAVIOR_CHANGED` in the same transaction, idempotent resend) and `GET …/version-behavior-impact?target=` (approximate record-level counts over the caller's discoverable records only). Designer Form settings card: "Record layout" row and dialog. |
| WP-6 | `GET …/projection?purpose=view|edit`; the record page badge "Layout vN (created with vM)" and the Hybrid notice; the editor reads the edit layout, explains it, sends the expected layout, and offers a reload that keeps the user's changes when the layout changed. Submit and restore send the expected layout. No "Other recorded values" section (D9-8). |

No business or Authority migration; no signed-row, Merkle, envelope, journal or Authority change; field identities, stored values, authorization and encryption unchanged.

## 2. Verification (local, 2026-11-22)

| Check | Result |
|---|---|
| .NET Release build of `EnterpriseForms.All.slnx` | Pass: 0 warnings, 0 errors. Stage A and Stage B test projects compile |
| OpenAPI | Regenerated from the compiled endpoints and committed (additive: two routes, optional fields) |
| Test-result accounting self-tests | Pass |
| `run_dotnet_tests.py` (full CI set) | 3,259 executed, 3,259 passed, 0 failed, 223 authorized skips (Authority 1,893, contract 1,043, architecture 227, integrity 73, platform console 23) |
| Contract tests `FormVersionBehaviorTests` | 39 pass (rule matrix, selection, fail-closed, D9-5 guard, setting, impact) |
| `AuditExplorerTests.EveryAuditActionHasExactlyOneCatalogEntry` | Count pin updated from 80 to 81 for `FORM_VERSION_BEHAVIOR_CHANGED` (it first failed on the pin, as designed) |
| Stage B focused class `FormVersionBehaviorTests` (`scripts/dev/test.ps1 -Level 2 -Class`, C-131 §1) | 3/3 on a freshly built isolated security-test stack |
| Directly affected Stage B classes, focused, same stack | RecordLifecycleTests 12/12, RecordRestoreTests 11/11, ParentChildTests 2/2, DocumentTests 3/3, ContributorWriteTests 7/7, RecordWorkspaceTests 5/5, DesignerTests 3/3, FormRenameTests 1/1 |
| Frontend (existing node_modules; `npm ci` not re-run) | Lint, typecheck and build pass; Vitest 66 files, 549 tests (13 new); no API-client drift (web and platform console) |
| Playwright `form-version-behavior.spec.ts` (`-Level 2 -Spec`, isolation guard verified, port 5173 free) | 3/3 including axe scans of the dialog, the record page and the editor's layout-changed state. Two earlier attempts failed on test defects, corrected without weakening an assertion: an ambiguous locator (the option label "Current version" duplicated an existing settings row, so the label was renamed "Current published version") and a URL check (the record page drops `?saved=true`; the spec now waits for "Changes saved") |
| Compose profiles (dev, integration, security-test) | Validate |
| Integrity vectors (Python) | 46 checks pass |
| `validate-specifications.py --strict` | Pass |

Not run: hosted Stage B (release-only, C-131), any manual workflow dispatch, deployment.

## 3. Deviations from the preparation plan and open items

- **Submit vs. other transitions:** only Submit reads the edit layout; Reopen, Archive and Unarchive read the view layout because they validate no content (so a missing edit layout does not block archiving).
- **Compatibility exception: `expectedLayoutVersionId` is optional** on update, Submit, restore and upload begin. A legacy API client that omits it is validated against the current edit layout and receives no guaranteed stale-layout detection; the web application always sends it (§4).
- **Related lists on the record page** come from the view layout: under Hybrid, a related list added in a later version appears in the editor's layout but not on the record page of an older record (the child records are kept and remain reachable).
- The option label for Latest is "Current published version" (the settings card already has a "Current version" row).
- **PB-06 concurrency convention** (body revision/409 vs `If-Match`/412/428) remains open platform-wide (D9-3).
- **Deferred (D9-9):** translations in comparisons; organization-disabled catalog values; a separate publish permission.
- **Outstanding for M1:** owner review; manual screen-reader review of the new dialog and notices (PB-14); hosted Stage B only under separate release authorization.
- **Not started:** M2 (WP-3 comparison, WP-4 publish impact, WP-5 version comparison) and M3 (WP-7, WP-8).

## 4. D9-5 reconciliation (owner-directed review of PR #70, C-181)

Details: `docs/architecture/PHASE_C9_FORM_VERSION_MANAGEMENT.md` §4.5.

| Item | Finding | Action |
|---|---|---|
| 1. First-party write paths supply `expectedLayoutVersionId` where applicable | Update, Submit and restore already did. **Upload begin did not**, although it validates the file against the edit layout's upload policy. Upload chunks/complete/commit, document removal, child creation and unlink do not validate against the record's edit layout, so D9-5 does not apply. Reopen/Archive/Unarchive validate no content. | **Corrected:** `BeginUploadRequest.ExpectedLayoutVersionId` (optional), guarded in `UploadSessionHandlers.BeginAsync`; the record page passes `editLayoutVersionId` through `DocumentCollection` and the upload manager; an upload refused for a changed layout releases the file (no retry with the stale id) and re-reads the record. |
| 2. A supplied stale id always conflicts | `EditLayoutGuard.Require` compares with the edit layout resolved in the same verified read whose layout validates and stages the write, before any value or policy validation; covered: owner update, share-editor update, Submit, restore, upload begin. | No code change beyond item 1. Tests added for the paths not yet demonstrated (below). |
| 3. Compatibility exception | Documented in the architecture §4.5 and §6, in the request contracts' XML documentation and in the upload-begin endpoint summary. | Documentation only. |
| 4. Tests | Not yet demonstrated: stale after a *publish* (only after a behaviour change), the share-editor path, upload begin. | Stage B `AStaleEditLayoutIsRefusedAfterANewerVersionIsPublishedForOwnersAndSharedEditors` (owner update, share-editor update, Submit after a publish; the share editor's reload then saves); the upload stale/current checks in `DocumentsUploadsAndChildRecordsUseTheirFormsEditLayout`; upload-manager unit tests (expected id sent; no retry after `EDIT_LAYOUT_CHANGED`). |
| 5. "Reload with the new layout and keep my changes" | Values match by permanent field key, so nothing is applied to another field. **Defect:** a change to a field the new layout no longer lets the person change (removed, read-only, hidden from the form, unshared) or to a repeating section no longer placed was silently left out of the save and counted as carried over. | **Corrected:** such changes are withheld and listed ("Some of your changes can't be carried over", label and entered value); the form shows the latest saved value; nothing withheld is sent. Unit tests (`layoutReload.test.ts`) and a Playwright scenario (a field removed while editing). |

Observation, unchanged and outside D9-5: creating a record (top-level or child) validates against the form's current version at request time; a version published while the new-record page is open is applied without a 409.

Verification of the C-181 correction (local, 2026-11-22):

| Check | Result |
|---|---|
| .NET Release build of `EnterpriseForms.All.slnx` and the Stage A/Stage B test projects | Pass: 0 warnings, 0 errors |
| `run_dotnet_tests.py` (full CI set) | 3,259 executed, 3,259 passed, 0 failed, 223 authorized skips |
| Stage B focused `FormVersionBehaviorTests` (fresh isolated stack, C-131 §1) | 4/4 (the new stale-layout test and the extended uploads test included) |
| Stage B focused `DocumentTests` (upload begin changed) | 3/3 |
| Playwright `form-version-behavior.spec.ts` (isolation guard verified, port 5173 free) | 4/4, including the new removed-field reload scenario with an axe scan |
| Frontend | Lint, typecheck and build pass; Vitest 67 files, 556 tests (7 new: 5 reload-merge, 2 upload-manager); no API-client drift |
| OpenAPI | Regenerated (optional `expectedLayoutVersionId` on upload begin; endpoint summary) |

## 5. M2 — semantic comparison, publish impact, version comparison (C-182)

Design: architecture §7.

| Work package | Delivered |
|---|---|
| WP-3 | `FormLayoutComparison`: matching by stable id (fields, document collections, related lists, sections, options), change types Added/Removed/Modified/Moved, significance Structure/Behaviour/Presentation, stable aspect codes with before/after and a `Tightened` flag, deterministic order; moves and reorders are presentation changes, never removals. |
| WP-4 | `GET …/publish-impact?expectedDraftRevision=` (read-only, DesignForm): the current version against the draft exactly as publishing would store it (shared snapshot helper), blocked reason (`NO_DRAFT_CHANGES`, layout errors), potential-impact notices, approximate record counts over discoverable records only (D9-4), and a data statement that publishing never deletes or changes stored values. Publish dialog shows the review before the change reason. `FORM_VERSION_PUBLISHED` audit gains counts of added/removed/behaviour/presentation changes. |
| WP-5 | `GET …/versions/compare?from=&to=` (version ids or `draft`; own versions only). Versions tab "Compare versions" card with From/to selects, Swap, and the shared comparison view (presentation-only changes collapsed); "Restored from" names the source version. |

Verification (local, 2026-11-23):

| Check | Result |
|---|---|
| .NET Release build of `EnterpriseForms.All.slnx` and the Stage A/Stage B test projects | Pass: 0 warnings, 0 errors |
| `run_dotnet_tests.py` (full CI set) | 3,275 executed, 3,275 passed, 0 failed, 223 authorized skips (contract tests 1,059, 16 new in `FormVersionComparisonTests`) |
| Test-result accounting self-tests; Stage B grouping | Pass (`FormVersionComparisonTests` added to `forms-documents`) |
| Stage B focused (fresh isolated stack, C-131 §1) | `FormVersionComparisonTests` 1/1; `DesignerTests` 3/3 (publish refactored); `FormVersionBehaviorTests` 4/4 |
| Playwright (isolation guard verified, port 5173 free) | `form-version-comparison.spec.ts` 3/3 with axe scans of the publish review dialog and the comparison; regression `form-version-behavior` 4/4, `designer-ux` 5/5, `form-management` 3/3, `repeating-groups` 2/2 |
| Frontend | Lint, typecheck and build pass; Vitest 68 files, 564 tests (8 new); no API-client drift |
| Integrity vectors | Python and Node, 46 checks each, pass |
| OpenAPI | Regenerated (two read endpoints; additive) |
| `validate-specifications.py --strict` | Pass |

Not run: hosted Stage B (release-only, C-131), manual workflow dispatches, deployment.

Deviations and limitations:
- The publish impact is a `GET` (a read), not the `POST` sketched in CLAUDE.md §6.
- Comparisons word the layouts' own labels; translations, organization-disabled catalog values and a separate publish permission remain deferred (D9-9).
- Rules (visibility, conditional requirement, dependent-choice filters) are reported as added, removed or changed, not described condition by condition.
- The draft side of a comparison is the publish snapshot taken now; catalog changes since (labels, option availability, protection) therefore appear as changes, which is what publishing would record.
- Manual screen-reader review of the review dialog and comparison view is outstanding (PB-14); automated axe scans pass.

## 6. Corrective review of PR #71 (owner direction, C-183)

| Item | Finding | Correction and tests |
|---|---|---|
| 1. Publish-review enforcement | **Defect.** Publish was disabled only when a loaded review said `canPublish: false`; it stayed enabled while the review was loading, after it failed, while it was refreshing, or for a review of another draft revision. | `publishGate` allows Publish only on a loaded, current, successful review of exactly this draft revision that permits publishing; `PublishGateNote` says why otherwise; the click handler re-checks the gate. Server-side revision and validation checks unchanged. Vitest: ready, loading, failed (also with an earlier review held), stale revision, refreshing, blocked, the note. Playwright: a review request that fails keeps Publish disabled; after Try again it loads and Publish is enabled. |
| 2. Conditional rule classification | **Defect.** A changed `RequiredWhen` that stayed present was marked as tightening; a changed pattern too. | Tightening only when a condition is added to a field required neither before nor after (and for an added pattern). Contract tests: added, replaced, removed, unconditional → conditional, already required, new conditionally required field; pattern added/replaced/removed. |
| 3. Comparison completeness | **Gaps:** a section placing a different repeating group was not reported; protection recorded in only one version was ignored. **Not reachable under the contract, now handled defensively:** an upload policy in only one version; a field placed as a different element kind. All other persisted properties (label, help, placeholder, control, placement, validation, read-only, shown in form, clear when hidden, rules, dependent-choice filter, defaults, options, upload limits, summary fields, section title/description/order, repeat limits) were already compared. | `RepeatGroup` and `Kind` aspects, protection and upload null↔set reported; notice `REPEATING_SECTIONS_CHANGED`. Contract tests for each. |
| 4. Version selector synchronization | **Defect.** Selections were fixed at first render: after a new publish the defaults did not move forward, and a chosen side that disappeared (a removed draft) left an invalid selection. A changed draft revision already re-read the comparison (query key). | `reconcileSelection`: defaults follow new versions until the person chooses; a valid choice is kept; an invalid one is reset to the defaults with a "Comparison reset" notice; invalid sides are never shown or queried. Vitest: unit cases plus component tests (defaults move forward on publish, re-read on draft revision change, a chosen comparison kept while valid and reset when the draft disappears). |

Verification (local, 2026-11-23):

| Check | Result |
|---|---|
| .NET Release build (solution, Stage A and Stage B test projects) | Pass: 0 warnings, 0 errors; no OpenAPI drift |
| `run_dotnet_tests.py` (full CI set) | 3,283 executed, 3,283 passed, 0 failed, 223 authorized skips (contract `FormVersionComparisonTests` 24, 8 new). A first run made while the Docker stack was still up had 2 failures in untouched tests (`SecurityCommandTimelineTests.AuditEnvelopesWithholdCountsDetailsAndIdentityIdentifiers` in the Authority suite and `SearchIndexBuilderRunnerTests.CancellationDuringAPassEndsTheLoopPromptly`); each passed 3/3 alone, and the full run passed after the stack was removed. *Correction (C-184):* the first of these is not timing-sensitive but nondeterministic — see §7 |
| Stage B focused (fresh isolated stack, C-131 §1) | `FormVersionComparisonTests` 1/1; `DesignerTests` 3/3; `FormVersionBehaviorTests` 4/4 |
| Playwright (isolation guard verified, port 5173 free) | `form-version-comparison` 3/3 (now also: a failed review keeps Publish disabled until it loads; axe scans pass); regression `form-version-behavior` 4/4, `designer-ux` 5/5, `form-management` 3/3 |
| Frontend | Lint, typecheck and build pass; Vitest 69 files, 577 tests (13 new) |
| Integrity vectors | Python and Node, 46 checks each, pass |
| `validate-specifications.py --strict` | Pass |

Remaining limitations: rules are still reported as added, removed or changed (not condition by condition); a replaced rule or pattern is shown as a behaviour change without a direction; manual screen-reader review of the review dialog and comparison is outstanding (PB-14).

## 7. PR #71 merge record (C-184)

- **Merged:** PR #71 squash-merged into `main` as **`491fa9a2cb53a00a834fd02d6e6fe3af0ecb5eb4`** under the owner's explicit authorization, with the expected-head guard (`03b5169fc98e31054a9d3a5e49b7ae20cc7a67cc`). Single parent `cd48e13974d3539bfa18e65af6d94cbf06f91364`; GitHub signature verified and valid; file tree identical to `03b5169`. Signed annotated tag `archive/c9-m2-version-comparison-03b5169` keeps the branch's three signed commits (`1f769d3`, `61d8005`, `03b5169`); the branch itself was deleted automatically on merge.
- **Preconditions verified before merging:** PR open, CLEAN and mergeable; head and base as expected; exactly the three expected commits, all signed and verified; 27 files, none outside the M2 scope and none in the Authority, migrations, cryptography or signing; the four required checks and CodeQL passed; no reviews or review threads; 0 open code-scanning alerts.
- **Post-merge checks on `491fa9a`:** CodeQL passed for all four languages (0 open alerts). The `ci` run (`45112841517`) passed specifications/vectors, frontend and Compose, but **the .NET job failed on one test**: `AuthorizationAuthority.Tests.SecurityCommandTimelineTests.AuditEnvelopesWithholdCountsDetailsAndIdentityIdentifiers`. The identical tree passed on the PR.
- **Cause (pre-existing test defect, not an M2 regression):** the test asserts that the serialized audit envelope does not contain the text `917` (its withheld evaluated-record count), but the same envelope contains random UUIDs (`Guid.CreateVersion7()`), and in this run one was `01a206b2-97dc-737e-9178-…`. The test therefore fails at random whenever a generated UUID contains `917`. The test is unchanged by C9 (C8 M4-3 code); the earlier local failure in §6 was the same defect, not load.
- **Not done (outside the authorization):** the test was not changed (Authority test project) and the workflow was not re-run. A follow-up needs a separate owner decision: assert that no envelope detail carries the withheld value (instead of searching the serialized envelope, which also holds random UUIDs, for its digits) and re-run CI on `main`.
- **Follow-up completed (C-185, C-186):** the test was corrected by PR #73 (`7d0c879`, §8). The `ci` run on `7d0c879` still failed, in both attempts, on a different test defect; `main` became green only at `3d930f6`, after PR #74 (§9). Run `45112841517` remains recorded as failed.
- **Unchanged and still open:** C9 M3; manual screen-reader review of the M1 and M2 interfaces (PB-14); hosted Stage B; the deferred D9-9 requirements; G21; the PB-06 concurrency convention; all C8/M4-5 obligations (§45.5 of the C8 status); the six Dependabot alerts; production certification.

## 8. PR #73 merge record and failed post-merge CI (C-185)

- **Defect corrected:** `SecurityCommandTimelineTests.AuditEnvelopesWithholdCountsDetailsAndIdentityIdentifiers` asserted that the serialized audit envelope does not contain the text `917` (its withheld evaluated-record count), while the same envelope carries random UUIDs; it failed whenever a generated UUID contained those digits (§7).
- **Correction (test only, Authority test project):** fixed identifiers in the test; the envelope's permitted details are compared exactly (sorted ordinally); the withheld-fields contract is asserted (`evaluated_record_count`, `matched_record_count`); neither count (`917`, `4`) may appear as a standalone value anywhere in the serialized envelope (a bounded match that ignores digits inside identifiers); the sentinel check is kept. A new test proves that the scan finds an exposed value and ignores the same digits inside a UUID. No Authority production code, audit policy, journal or authorization change.
- **Merged:** PR #73 squash-merged into `main` as **`7d0c8796ba2000e1bf5eacddbf71c7d22f01060b`** under explicit owner authorization, with the expected-head guard (`f181984`); parent `491fa9a`; signature verified.
- **Post-merge `ci` run `45158422231` on `7d0c879`: failed in both attempts.** CodeQL passed. Specifications, frontend and Compose passed; the .NET job failed on one contract test each time, a different one per attempt:

| Attempt | Failed test | Result of the .NET test set |
|---|---|---|
| 1 | `AuthorityContentAcceptancePolicyStartupTests.AnInvalidOverrideOfTheDevelopmentSettingIsRefusedToo` (`ReleaseB`, floor missing, `RequiredFloorMissing`) | 3,283 passed, 1 failed, 223 authorized skips |
| 2 | `EnvelopeCapabilityEnforcementHostTests.M2F29AnInvalidDeploymentFloorRefusesTheAuthorityStart` (empty `Content:MinimumBusinessEnvelopeCapability`) | 3,283 passed, 1 failed, 223 authorized skips |

  Both failed with `Cannot access a disposed object. Object name: 'IServiceProvider'` in place of the expected startup refusal. Neither attempt was successful.
- **The corrected test passed in both attempts** (Authority project: 1,894 passed, 0 failed, 211 authorized skips; `AuditEnvelopesWithholdCountsDetailsAndIdentityIdentifiers` and the new scan test passed, per each attempt's test-result artifact).
- **Investigation (owner-authorized, no code change):** evidence preserved; one rerun of the failed job (attempt 2, above); the cause is §9.

## 9. Test-host startup race, PR #74 and CI recovery (C-186)

- **Cause:** a race in `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, confirmed against its source. `WebApplicationFactory` runs the app's `Main` on its own thread. When a hosted startup guard throws inside `Host.StartAsync`, that thread's `app.Run()` disposes the host. Meanwhile, `DeferredHost.StartAsync` resolves `IHostApplicationLifetime` before it waits for the start result; if the app thread disposes first, the factory throws `ObjectDisposedException` (`IServiceProvider`) and the refusal is not surfaced to the test.
- **Deterministic reproduction:** forcing the factory's start to run only after the host's container has been disposed reproduces the CI failure every time, and the original assertion fails on it.
- **The startup refusal remained enforced:** the host still refuses to start; only the test's view of the reason is lost. Production does not use `DeferredHost`; `app.Run()` propagates the refusal.
- **Classification of 23 startup-refusal assertion sites** (by recording each refusal's actual throw site in a temporary, uncommitted run):
  - **17 affected, migrated:** refusal thrown inside `Host.StartAsync` by a hosted startup guard — content acceptance policy 5, signing and identity policy 6, D+ finding store 3, envelope capability floor 1, reconciler credential 1, verifier credential 1.
  - **4 unaffected, refused before the host starts:** `TestStackIdentityBoundaryTests` ×2, `ReconcilerBoundaryTests.RC28OutOfBoundsRateLimitsRefuseToStart`, the synthetic platform mode.
  - **2 unaffected, weak assertion** (any exception accepted, so the race cannot fail them): `ReadinessFailsClosedTests`, `PlatformConsoleDecisionTests`. Unchanged; outside this correction.
  - **Uncertain:** none.
- **Correction (test only, contract test project, 8 files):** `StartupRefusal` accepts the factory's exception when it is itself the expected refusal (and agrees with any logged startup failure). It accepts the disposed-provider exception only when it is exactly the framework race (`ObjectDisposedException` for `IServiceProvider`, thrown from `DeferredHost.StartAsync`, no inner exception) **and** the host's own "Hosting failed to start" event (`HostedServiceStartupFaulted`, event 11, logged on the app thread before disposal) carries exactly one failure that is the expected refusal. It rejects unrelated failures, a different refusal, a disposed provider without logged evidence, a disposed-object exception that is not the race, and a host that starts. No delays, retries or broad suppression; no Authority production code, security policy or framework-version change.
- **Regression coverage:** `StartupRefusalTests` (12 tests) force both orderings against the real Authority host with synchronization, not sleeps: `RequiredFloorMissing` and an invalid envelope floor are proven in both orderings (after disposal, the old assertion is shown to fail); an unrelated failure and a mismatched refusal are rejected in both orderings; an unexpected start, a disposed provider without evidence, a non-race disposed-object exception and a direct match contradicted by the log are rejected. Local: 30 of 30 repeated runs, 12 of 12 each.
- **Merged:** PR #74 squash-merged into `main` as **`3d930f6de93728de9a5c8b358444ee7745972831`** under explicit owner authorization, with the expected-head guard (`a38e0903fea8e6c88d06eb2a5c20d7ce53da700e`). Preconditions verified: open, CLEAN and mergeable; one signed, verified commit; exactly the eight approved files; the four required checks and CodeQL passed; no reviews or threads; 0 open code-scanning alerts. Single parent `7d0c879`; tree identical to the head; signature verified.
- **Post-merge on `3d930f6`: green.** `ci` run `45170535971` succeeded (every job; strict specification validation passed); CodeQL run `45170535274` succeeded for all four languages.

| .NET test project | Passed | Failed | Authorized skips |
|---|---|---|---|
| Authority | 1,894 | 0 | 211 |
| Contract | 1,079 | 0 | 12 |
| Architecture | 227 | 0 | 0 |
| Integrity | 73 | 0 | 0 |
| Platform console | 23 | 0 | 0 |
| **Total** | **3,296** | **0** | **223** (unchanged) |

  `StartupRefusalTests` 12/12 and every migrated class passed in that run.
- **`main` became green at `3d930f6`, not at `7d0c879`.** Runs `45112841517` (`491fa9a`) and `45158422231` (`7d0c879`, both attempts) remain recorded as failed.
- **Remaining limitations:** the migrated tests are not individually run under a forced race (the helper's two orderings are); a future framework rename of the startup-failure event would fail those tests visibly; why the race hit two consecutive attempts is not explained; the `SearchIndexBuilderRunnerTests` cancellation failure seen once locally (§6) is not explained.
- **Unchanged and still open:** C9 M3; manual screen-reader review of the M1 and M2 interfaces (PB-14); hosted Stage B (release-only, C-131); the deferred D9-9 requirements; G21 and the platform-wide PB-06 concurrency convention (D9-3); all C8/M4-5 obligations (§45.5 of the C8 status); the six Dependabot alerts; production certification. Final C9 acceptance is not claimed.

## 10. M3 — consolidated acceptance and close-out preparation (C-187)

Authorized by the owner on the baseline `fc9e4044068e5d99df5921045a3ba8feb7bb4f42` (WP-7 and WP-8 only; implementation,
local Development/Testing validation, signed commits and one pull request; no merge, deployment, hosted Stage B,
production acceptance or further milestone). Branch `phase-c9/m3-acceptance`. Design: architecture §8.

### 10.1 Delivered

| Work package | Delivered |
|---|---|
| WP-7 Stage B | `tests/integration/EnterpriseForms.StageBTests/Tests/FormVersionAcceptanceTests.cs` (group `forms-documents`), three facts on the real stack: (1) V1 → V2 → V3 (restored from V1) → V4 (original email re-added); (2) Historical / Latest / Hybrid on three forms with equivalent histories, fail-closed edit layouts, stale layouts; (3) changing the behaviour after records exist. |
| WP-7 contract | `tests/features/EnterpriseForms.Api.ContractTests/FormVersionLifecycleContractTests.cs`, 9 tests: the designer lifecycle through the real `FormDesignerHandlers` over an in-memory store (identities, byte-identical restore, restore lineage, immutable snapshots, counts-only publish audit, Authority registration per version, comparisons and publish impact, stale/unauthorized/foreign refusals that change nothing) and the layout selection of each behaviour over the four-version history. |
| WP-7 browser | `src/frontend/e2e/form-version-acceptance.spec.ts`, 5 serial tests: publishing with the review, the Latest record indicator, Restore to draft (v3), the existing-field picker (v4) and the v3 → v4 comparison, the Record layout dialog (Hybrid), the Hybrid record indicators, and a stale-layout reload that keeps the email change and withholds the phone change; axe scans of every surface. |
| WP-8 | Architecture §8; this section (verification, requirement matrix, findings, obligations); decision C-187; PB-14 manual review checklist `docs/testing/PHASE_C9_MANUAL_ACCESSIBILITY_CHECKLIST.md` (OPEN) and register rows PB14-44…46; manifests regenerated. |

No production code, API contract, OpenAPI, migration, Authority, cryptographic, signing, Merkle or journal change.

### 10.2 Verification (local, 2026-11-23/24)

Focused integration and browser runs followed C-131 §1 (`scripts/dev/test.ps1 -Level 2 -Class` / `-Spec`) on the isolated
security-test stack (`enterpriseforms-sectest`), started from empty volumes (`down -v`) and removed afterwards; the
browser isolation guard verified the target (`http://127.0.0.1:5199`, stack `enterpriseforms-security-test`). Synthetic
data only. TRX and Playwright reports were written under `artifacts/` (not committed).

| Check | Result |
|---|---|
| Stage B `FormVersionAcceptanceTests` (new), fresh stack | **3/3** (1 min 46 s) |
| Contract `FormVersionLifecycleContractTests` (new) | **9/9**. One expectation was corrected during development: the first version's publish audit counts 4 added elements (its section and three fields), as `FormLayoutComparison` reports for a first version; the code was right, the expectation wrong. |
| Playwright `form-version-acceptance.spec.ts` (new) | **5/5** first run, then 5/5 again in the regression run; axe: no violations on any scanned surface |
| Stage B regressions, same stack | `*FormVersion*` 8/8 (M1 4, M2 1, M3 3), `DesignerTests` 3/3, `RecordRestoreTests` 11/11, `DocumentTests` 3/3, `RecordLifecycleTests` 12/12 |
| Playwright regressions, same stack | `form-version-*` 12/12 (M1 4, M2 3, M3 5), `designer-ux` 5/5, `form-management` 3/3, `record-restore` 5/5, `repeating-groups` 2/2 |
| .NET Release build of `EnterpriseForms.All.slnx`, Stage A and Stage B test projects | Pass: 0 warnings, 0 errors |
| OpenAPI | No drift (`git diff --exit-code -- contracts/openapi`) |
| Test-result accounting and Stage B grouping self-tests | 37/37 pass (`FormVersionAcceptanceTests` registered in `forms-documents`) |
| Frontend | Lint and typecheck pass; the new spec type-checks under `--strict`; Vitest 69 files, 577/577 (no new frontend unit tests: M3 adds no frontend production code); build passes |
| Integrity vectors | Python 46/46 and Node 46/46; regenerated vectors identical to the committed ones (Windows CRLF working-copy difference only, restored) |
| Compose profiles (dev, integration, security-test) | `config` validates |
| Full .NET CI set (`run_dotnet_tests.py`) | **3,305 executed, 3,305 passed, 0 failed, 223 authorized skips** (Authority 1,894 + 211 skips, contract 1,088 + 12, architecture 227, integrity 73, platform console 23). Against the 3,296 baseline: +9, the new `FormVersionLifecycleContractTests`; skips unchanged. Architecture and dependency-boundary tests are the 227. Run with the Docker stack removed. |
| `validate-specifications.py --strict` | Pass (manifests regenerated with `scripts/generate-spec-manifest.py`; 308 tracked files) |

Not run: hosted Stage B and the full local Stage B suite (release-only, C-131); manual workflow dispatches; deployment.

### 10.3 Requirement → test → result

Classifications: **T** implemented and tested; **M** implemented, awaiting independent or manual verification;
**D** deferred by an existing owner decision; **B** blocked or failed; **O** outside M3 scope. Test references: SB =
Stage B `FormVersionAcceptanceTests` (1 = lifecycle, 2 = behaviours, 3 = setting); CT = contract
`FormVersionLifecycleContractTests`; PW = Playwright `form-version-acceptance.spec.ts` (tests 1–5). Results are the
local runs in §10.2.

| # | Requirement (owner authorization §3–§6) | Tests | Result | Class |
|---|---|---|---|---|
| 1.1 | V1 with first name, last name, phone; records created and submitted with phone values | SB1, CT, PW1 | pass | T |
| 1.2 | Correct field identities (each value's `fieldDefinitionId`) | SB1, CT | pass | T |
| 1.3 | Created-version metadata (`created_form_version_id`, `createdFormVersionNumber`) | SB1 | pass | T |
| 1.4 | Authorized record retrieval; denial without grant, across organizations, without query permission | SB1 | pass | T |
| 1.5 | Authorized verified search of the phone value (discoverable records only) | SB1 | pass | T |
| 1.6 | Stored values and history | SB1 | pass | T |
| 2.1 | V2 removes phone from the layout and adds email | SB1, CT, PW2 | pass | T |
| 2.2 | V1 records not migrated or deleted; phone values intact; created version unchanged | SB1, PW2/PW5 | pass | T |
| 2.3 | Historical values available through authorized history | SB1 | pass | T |
| 2.4 | Historical values searchable through supported search paths after V2 | SB1 | **UNMET — deferred** (C-188). Evidence: V1 search succeeds with approval; V2 invalidates the approval (`FORM_VERSION_CHANGED`); search refuses with `SEARCH_FIELD_NOT_ALLOWED`; V3 restores the field but not the approval; administrator re-approval restores eligibility (architecture §8.3). Authorized history (2.3) does not satisfy this requirement. | **D (unmet; deferred by owner decision C-188)** |
| 2.5 | New records use V2 | SB1 | pass | T |
| 2.6 | Version comparison identifies added and removed elements | SB1, CT, PW2 (publish review), PW3 (Versions tab) | pass | T |
| 2.7 | Publish-impact notices describe potential effects (new required field, removed element); counts over discoverable records only; no values | SB1, CT, PW2 | pass | T |
| 2.8 | Removed fields not exposed through unauthorized projections (partial share; history whole-record only) | SB1, SB2 | pass | T |
| 3.1 | V1 restored as a draft and published as a new immutable V3 (`restoredFromFormVersionId`) | SB1, CT, PW3 | pass | T |
| 3.2 | Phone reappears with its original identity; existing phone values with the correct records | SB1, CT, PW5 | pass | T |
| 3.3 | V2 unchanged; email values still stored; history works | SB1, CT | pass | T |
| 4.1 | Original email `FieldDefinitionId` re-used; no duplicate logical field (`FIELD_KEY_EXISTS` for a new one) | SB1, CT, PW3 | pass | T |
| 4.2 | Email values with their own records; no crossover | SB1 | pass | T |
| 4.3 | V1, V2, V3 snapshots unchanged after V4 | SB1, CT | pass | T |
| 4.4 | V3 → V4 comparison reports the email addition only | SB1, CT, PW3 | pass | T |
| 5.1 | Historical / Latest / Hybrid view and edit layouts and version numbers | SB2, CT | pass | T |
| 5.2 | `created_form_version_id` unchanged; `last_edited_form_version_id` = applicable edit layout after update | SB2 | pass | T |
| 5.3 | Values outside the selected layout preserved | SB2, SB1 | pass | T |
| 5.4 | Authorization and field-level visibility unchanged by the behaviour | SB2 | pass | T |
| 5.5 | Restore validates against the applicable edit layout (D9-6) | SB2 | pass | T |
| 5.6 | Missing or foreign edit version fails closed (`409 EDIT_LAYOUT_UNAVAILABLE`); Historical unaffected | SB2, CT | pass; an *unknown stored behaviour* is covered by the M1 contract tests only (the database CHECK prevents it on the stack) | T |
| 5.7 | Stale layout after a publish: update, Submit, restore, upload begin → `409 EDIT_LAYOUT_CHANGED` | SB2 | pass | T |
| 5.8 | Stale layout after a behaviour change: update, Submit, upload begin | SB2 | pass | T |
| 5.9 | No partial write or silent data loss on refusal | SB2 | pass | T |
| 5.10 | Reload keeps permitted changes and withholds incompatible ones | PW5 (and M1 `form-version-behavior`) | pass | T |
| 5.11 | Legacy clients omitting `expectedLayoutVersionId`: contract unchanged | SB2 | pass | T |
| 6.1 | Impact preview before confirmation; approximate; discoverable records only; no confidential counts or values | SB3, PW4 | pass | T |
| 6.2 | DesignForm enforced; other organization denied | SB3 | pass | T |
| 6.3 | Reason 3–500 characters (2 and 501 refused; 3 and 500 accepted) | SB3 | pass | T |
| 6.4 | Stale form revision rejected | SB3 | pass | T |
| 6.5 | Setting and audit event atomic (one event per change; none for refused attempts) | SB3 (observable), M1 contract (same commit) | pass | T |
| 6.6 | No values, records or versions migrated or deleted | SB3 | pass | T |
| 6.7 | Subsequent view and edit follow the selected mode | SB3, PW5 | pass | T |
| A.1 | Automated accessibility (axe) of the dialog, review, comparison, indicators, stale-layout and withheld states | PW1–PW5, M1/M2 specs | pass (axe: 0 violations) | T |
| A.2 | Manual screen-reader review (NVDA, VoiceOver) | checklist prepared | not performed | **M (OPEN)** |
| W8.1 | Documentation, evidence locations, deviations, obligations | this section, architecture §8, C-187 | done | T |
| W8.2 | Final C9 acceptance | — | not claimed | O |
| — | Translations in comparisons; organization-disabled catalog values; separate publish permission | — | — | D (D9-9) |

### 10.4 Findings, deviations and limitations

- **No product defect found.** Every scenario passed against the merged M1/M2 implementation without a code change.
- **Search of a field no longer in the current version is not supported** (requirement 2.4; architecture §8.3). The
  existing verified field search binds approvals to the latest registered version: V2 invalidated the phone approval
  (`Invalidated|FORM_VERSION_CHANGED`), searches on phone were refused (`422 SEARCH_FIELD_NOT_ALLOWED`, no partial
  result) until a security administrator approved phone again after V3; the approval then carried over to V4. CLAUDE.md
  §11 #2's requirement that these values remain **searchable** with authorization while the field is outside the current
  version is therefore **UNMET — deferred pending a separate owner-approved security design (C-188)**; it is neither
  implemented nor passed. Authorized history keeps the values readable, but history access is a different capability and
  does not satisfy searchability. The owner accepted the fail-closed approval behaviour as it is; supporting approved
  searches of off-layout historical fields requires a separate design and security authorization.
- **Encrypted fields are not searchable** (ADR-PB-004): the email field (Confidential, encrypted by default) is refused
  by search, as designed.
- **Unknown stored behaviour** (fail closed) cannot be produced on the real stack (database CHECK); it is covered by the
  M1 contract tests only. "Invalid" edit versions on the stack are represented by a foreign and a missing current pointer.
- **Atomicity** of the behaviour change with its audit event is observable on the stack only as "one event per change,
  none for refused attempts"; the same-commit property is proven by the M1 contract test.
- **Deviation from the plan:** none in scope. The contract scenario uses the real designer handlers over an in-memory
  store (no database); the database-backed proof is the Stage B class.

### 10.5 Accessibility (PB-14)

Automated: axe scans (WCAG 2.0/2.1/2.2 A/AA tags) of the publish review dialog, the Versions-tab comparison, the Record
layout dialog, the record page under Latest and Hybrid, the editor's layout-changed state and the withheld-changes state
found no violations; keyboard-free role and name queries locate every control. **Manual screen-reader review: OPEN —
manual evidence outstanding.** Checklist, required reviewer, environment and assistive technology:
`docs/testing/PHASE_C9_MANUAL_ACCESSIBILITY_CHECKLIST.md` (register rows PB14-44…46). No WCAG 2.2 AA or AODA
conformance or certification is claimed. M3 can be implementation-complete with this open; final C9 acceptance stays
blocked on it unless the owner explicitly resolves that dependency.

### 10.6 Test-stability observation: `SearchIndexBuilderRunnerTests.CancellationDuringAPassEndsTheLoopPromptly`

Not recurred: it passed in the full .NET run of §10.2 (0.26 s, TRX `artifacts/test-results/EnterpriseForms.Api.ContractTests/`). The test is unchanged (not weakened, skipped or removed). The single earlier failure (§6) remains unexplained; no cause is attributed to timing or Docker load without evidence.

### 10.7 Outstanding obligations

Owner review of M3 and of this pull request; the manual screen-reader review (§10.5); final C9 acceptance (not
claimed); hosted Stage B (release-only, C-131); the unmet §11 #2 searchability requirement for fields outside the current version (deferred pending a separate owner-approved security design, C-188; §10.4);
the D9-9 deferrals; G21 and the platform-wide PB-06 concurrency convention (D9-3); all C8/M4-5 obligations (§45.5 of
the C8 status); the six Dependabot alerts; independent third-party assessment and production certification.

## 11. C-188 Option B — retained-field search approvals (C-189)

Authorized by the owner on 2026-11-24 (owner decisions D-H1…D-H18; H1–H6 in Development/Test only). Implemented on the
branch `c188/retained-field-search` from `9903844` (the PR #75 squash merge), pushed and opened as PR #76 with owner
authorization; BF-1 remediated on the same branch (§11.6), the final BF-1 check corrected (§11.8, C-191), and its final resolution implemented (§11.9: fail-closed abandonment, recovery-blocked state). **Not merged, not deployed; not owner-accepted and not independently
reviewed.** CLAUDE.md §11 #2 searchability stays
**UNMET** in the requirement register until the owner accepts the evidence below.

### 11.1 Delivered

The design and its behaviour are recorded in `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md` §6G (Authority,
migration, compatibility fence, flag, effectiveness check, rollback). In short: a security administrator can approve
searching the **latest retained value** of a field that the latest registered version no longer places
(`approvalScope = RetainedField`, step-up, governance reference, audited); removal still invalidates the ordinary
approval; a returning unchanged field converts the retained approval to an ordinary one only after full eligibility
revalidation, otherwise it is invalidated; Withdrawn and Invalidated approvals never become Active again (planner and
database trigger); the field-to-form binding is enforced in version registration, approvals and the protection
commands; the feature is behind `FieldSearch:RetainedFieldApprovals` (default Disabled; enabled only in the disposable
security-test overlay). Business API, generated clients, Search approvals and Advanced Search show "No longer in the
form".

### 11.2 Persistent development Authority (D-H17)

The three read-only detection queries of the readiness assessment §4.2, plus one added check, were run against the
persistent development Authority database (`enterpriseforms-dev-authority-db-1`, started for inspection only, queries in
a `READ ONLY` transaction ending in `ROLLBACK`, container stopped afterwards): **0** field ids registered in manifests of
more than one form; **0** protection requirements whose parent form differs from a form that registered the field;
**0** Active approvals whose field is absent from the form's latest registered manifest; **0** Active approvals of a
form with no registered version (added check). The database held 31 registered form versions and 3 approvals, so the
checks were not vacuous. That database is at migration
`20261101221445_OwnerRecovery` (behind `main`); no migration was applied and no data changed. This is **not** a claim of
persistent-environment compatibility: the migration has run only on disposable Development/Test databases.

### 11.3 Verification (local, 2026-11-24)

All runs are local, against disposable Development/Test databases and the isolated security-test stack (`enterpriseforms-sectest`); nothing ran against the persistent development stack except the read-only queries of §11.2. Hosted Stage B was not run (release-only, C-131).

| Suite | Result |
|---|---|
| Authority unit `RetainedFieldSearchApprovalTests` (T2–T4a, T7, T9–T11, T13–T15, T17, T23 as built) | 22/22 |
| Contract `RetainedFieldSearchContractTests` (capabilities marker, not-searchable hint, wire compatibility with an older Authority) | 3/3 |
| Contract `FormVersionLifecycleContractTests` (existing 9 plus the registration-failure regression test, §11.4) | 10/10 |
| Stage B `RetainedFieldSearchTests` (fresh stack; run `20261124-014839-a3c033f8`): lifecycle on both engines incl. latest-value-only, behaviour independence, record authorization, cross-organization, no-revival trigger, return conversion (T1–T15); withdrawal with an open page and changed return (T12, T14); concurrency (T16); migration fence, grants and backfill abort (T21); rollback rehearsal with the flag off (T22) | 5/5 |
| Playwright `retained-field-search.spec.ts` (approval from Search approvals, Advanced Search label and results, withdrawal; axe WCAG 2.2 A/AA) | 4/4 |
| Vitest (full; 4 new in `FieldSearchSection.retained.test.tsx`) | 581/581 |
| Full .NET CI-equivalent set (`run_dotnet_tests.py`, Release) | 3,331/3,331 executed, 223 authorized skips (main: 3,305 / 223; +22 Authority, +4 contract) |
| Existing Stage B regressions on the same branch: `FieldSearchApprovalTests` 5/5, `FieldSearchExecutionTests` 14/14, `FormVersion*` 8/8, `DesignerTests` 3/3, `FieldProtectionDowngradeTests` 8/8, `FieldProtectionMigrationTests` 8/8, `IndexedSearchQueryStackTests` 21/21, `SearchIndexBuilderStackTests` 12/12 (fresh stack), `SearchCatalogueTests` 11/12 (see §11.4) | as stated |
| Existing Playwright regressions: `field-search-approval` 5/5, `field-search-execution` 6/6, `form-version-acceptance` 5/5 | passed |
| Frontend lint, typecheck, build | clean |
| `validate-specifications.py --strict`, integrity vectors (Python and Node, 46 checks each), CI self-tests (37) | passed |

T16 (concurrency): four concurrent retained approvals left exactly one Active approval (the others superseded); in each of three races between a retained approval and the publication that returns the field, the approval's command closed the Authority gate first, the registration was refused and the version stayed published but unregistered, and the retained approval correctly remained Active while the Authority's latest registered version still omitted the field. The other two orderings (approval first then conversion; publication first then refusal) did not occur in these races; they are covered deterministically by the Stage B lifecycle fact (T13) and the Authority unit tests.

Order of events in this session: a first regression batch on a two-hour-old stack failed `SearchIndexBuilderStackTests` 6/12 (the worker's index builder stopped submitting between 04:14 and 05:13 UTC until a test recreated the worker) and `SearchCatalogueTests` 2/12; on a recreated stack the builder class passed 12/12 and the catalogue class failed only its benchmark, which fails the same way on unmodified `main` (below). The first full .NET run, executed concurrently with Vitest, failed `SearchIndexBuilderRunnerTests.CancellationDuringTheDelayEndsTheLoopPromptly` once (shutdown 5.6 s against a 5 s bound); the class then passed 25/25 five times and the full set passed without concurrent load (the class is unchanged from `main`; cf. §10.6).

### 11.4 Deviations and limitations

- **D-H10:** the owner approved correcting C7 architecture §4.4 for a later package. This branch corrects that row now,
  because the implemented behaviour would otherwise contradict it (documentation only; the owner can ask for it to be
  reverted to the D-H10 timing).
- **D-H16:** a retained approval whose field is back in the latest version (possible only after an older binary
  registered that version) is treated as not effective at every use and is converted or invalidated at the next
  registration; there is no separate sweep-time invalidation.
- **Rollback** is forward-only (flag off, withdraw, verify zero Active retained approvals). The migration's `Down` is
  for disposable databases only; it is not a rollback procedure.
- **Mixed versions:** an older Authority binary cannot see retained approvals (row-level security fence), so it fails
  closed; it would not invalidate them either, which is why every use re-checks eligibility. A single Authority instance
  is required during any authorized rollout (D-H12).
- **PB-08:** step-up is development-only; retained approvals, like ordinary ones, are unavailable in staging and
  production until PB-08 provider step-up exists.
- **Pre-existing defect found and fixed (separate commit, outside C-188's scope; the owner may drop it):** since C7 (PR #29) the business publish handler recorded a refused Authority registration with audit outcome `Failure`, which `ck_audit_events_outcome` rejects (`Success`/`Denied`/`Failed`); the insert failed, the failure's audit evidence was lost and the designer received `503 STORE_UNAVAILABLE` although the version had been published. The handler now records `Failed`; contract test `ARefusedAuthorityRegistrationIsReportedAndAuditedWithAnAllowedOutcome` fails without the fix and passes with it; the Stage B concurrency fact exercises the same path on the real database (201, `authorityRegistered=false`).
- **Pre-existing, not fixed:** `SearchCatalogueTests.BenchmarkTheVerifiedScopeAt50And150And480Records` fails on unmodified `main` (`9903844`, fresh stack) as on this branch: verified scope p50 about 1.5 s for 50 records and 4.9 s for 150 records (C7 M1 recorded 0.59 s and 1.8 s at `b146907`), and the 480-record scope exceeds the time limit (`SEARCH_VERIFICATION_LIMIT`). Not caused by C-188; needs its own investigation.
- **Registration during a concurrent security command (superseded by BF-1, §11.6):** a publication whose Authority registration meets a closed gate stays published but unregistered (existing C7 behaviour: only a stale revision is retried); until the form is published again, field search follows the previous registered version — for retained approvals, the field still counts as absent.
- **Test plan items as built:** T20 adds no step to `FormVersionAcceptanceTests` (the retained lifecycle is its own Stage B class); T22 simulates the older binary through the runtime role without the scope-awareness setting rather than starting a previous image; T23 differs from the plan: with the flag off the Authority starts and treats existing retained approvals as not effective (a refusal to start would block the authorized rollback step of turning the flag off); an unknown flag value refuses startup.

### 11.5 Outstanding obligations

Owner review and acceptance of this implementation and of the BF-1 remediation; merge and any deployment (each separately
authorized); recording §11 #2 as met only after acceptance; the BF-1 rollout prerequisites (§11.7); PB-08 provider step-up; manual
NVDA/VoiceOver review (PB14-47, PB14-48); an independent security review; the obligations of §10.7.

### 11.6 BF-1 remediation — publication registration barrier (C-190)

PR #76 review (owner-requested verification) found **BF-1**: a publication that removes an approved field and whose Authority
registration is refused (e.g. a gate closed by a concurrent security command) stayed current in the business while the Authority
kept the previous registered version, so the field's ordinary approval stayed Active and searchable - its retained values without
the explicit retained approval Option B requires - until a later registered publication, with no time limit. The C7 retry of a stale
revision could never succeed either: it reused an idempotency key the Authority had stored with the refusal. The owner approved
remediation on 2026-11-24 with the decision "fail closed whenever an active published form version is not registered with the
Authorization Authority" and authorized implementation on this branch and PR #76.

**Resolution** (design as built: `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md` §6H). The preferred option - keep a new
version inactive until registered - would change the C9 version lifecycle, so the owner's alternative was implemented: the business
stages every version with the Authority (`PrepareFormVersionPublication`, full manifest of the publish request) **before** committing
it, under a per-form advisory lock; while a publication is pending, no approval of the form is effective in the Authority
(capabilities, both engines, open pages, catalogue snapshots, new approvals); only `ResolveFormVersionPublication` (registered from
the Authority's staged manifest, or abandoned) ends it. Recovery (`POST …/authority-registration/recover`, "Retry registration" in the
designer) completes or withdraws a pending publication without a new publication, decided under the same lock. No existing security
invariant was weakened: OD-3 holds (nothing is registered from later business rows), D-H3/D-H4, revocation, completeness, integrity
and tenant isolation are unchanged.

**Verification (local, 2026-11-24).**

All runs local, against disposable Development/Test databases and the isolated security-test stack; nothing ran against a persistent environment. Hosted Stage B was not run (release-only, C-131).

| Suite | Result |
|---|---|
| Authority unit `FormVersionPublicationTests` (barrier on removal and on return, abandonment, exact resolution only, approvals and one-step registration refused while pending, supersede, re-validation, D-H7 at prepare, single pending) | 9/9 |
| Contract `FormVersionLifecycleContractTests` (existing 10, the C7 registration-failure test adapted to the pending state; new: stage-before-commit order, refused prepare publishes nothing, busy Authority retried with fresh keys, recovery registers (with jsonb-normalized storage) and is idempotent, recovery abandons, mismatched or unstaged versions never registered) | 16/16 |
| Stage B `FormVersionPublicationBarrierTests` (fresh stack, run `20261124-060623-3ea3a67d`): lost registration of a removal closed on business and Authority capabilities, V1 engine, open V1 page, indexed engine, open indexed page and approvals, then recovered and idempotent; lost commit abandoned; field return under a retained approval; concurrent security commands (4 rounds); database guards | 5/5 |
| Playwright `form-publication-registration.spec.ts` (Advanced Search unavailable with the reason; designer Retry registration; search resumes without the removed field; axe WCAG 2.2 A/AA on both pages) | 4/4 |
| Vitest (full; 4 new in `AuthorityRegistrationNotice.test.tsx`) | 585/585 |
| Full .NET CI-equivalent set (`run_dotnet_tests.py`, Release) | 3,346/3,346 executed, 223 authorized skips (unchanged) |
| Existing Stage B regressions (focused): `RetainedFieldSearchTests` 5/5 and `FieldSearchApprovalTests` 5/5 (fresh stack, after two test assertions were corrected, below); `FieldSearchExecutionTests` 14/14; `FormVersion*` 13/13; `DesignerTests` 3/3; `FieldProtectionDowngradeTests` 8/8; `FieldProtectionMigrationTests` 8/8; `SecurityCommandTimelineTests` 4/4; `IndexedSearchQueryStackTests` 21/21; `SearchIndexBuilderStackTests` 12/12; `SearchCatalogueTests` 11/12 (fresh stack; only the 50/150/480 benchmark already failing on `main`, §11.4) | as stated |
| Existing Playwright regressions: `retained-field-search` 4/4, `field-search-approval` 5/5, `field-search-execution` 6/6, `form-version-acceptance` 5/5 | passed |
| Frontend lint, typecheck; OpenAPI and generated clients regenerated without drift; CI self-tests (37) | passed |

Two existing Stage B assertions were corrected, not weakened: `FieldSearchApprovalTests` expected the one-step `RegisterPublishedFormVersion` command in the journal (publications now commit `PrepareFormVersionPublication` and `ResolveFormVersionPublication`; the test now requires both and no one-step command); the C-188 race (`RetainedFieldSearchTests`) assumed that a transient answer to an approval means nothing was committed - the journal of run `20261124-061002-d789bb6f` shows the approval committed and Effective, then converted by the registration (D-H4), while the business answered a transient error because the publication staged next moved the revision during its configuration re-read (possible in C7 too; an administrator can be told an approval failed when it succeeded). In the BF-1 race all four publications registered and the concurrent re-approvals were refused while the publication was in flight (12, 12, 7 and 12 of 12); the pending branch is proven by the fault-driven facts. A first regression batch on a stack that had run for over an hour failed `SearchCatalogueTests.BenchmarkA200RecordScopeSequentiallyAndUnderConcurrency` (no sequential verification within the time budget); on a fresh stack it passed.

**Defect found by the Stage B run and fixed before commit:** recovery compared the staged digest with the digest of the stored layout
text, but the business column is `jsonb` and does not keep the published bytes, so every recovery of a committed version was refused
`STAGED_VERSION_MISMATCH` (fail closed - the form stayed unsearchable - but never recoverable). Recovery now canonicalizes the stored
layout exactly as publishing does; the contract test reproduces the jsonb case and fails without the fix.

### 11.7 BF-1 limitations and rollout prerequisites

- **Mixed versions:** a business binary built before BF-1 does not stage publications (its publications keep the C7 window); an
  Authority binary built before BF-1 does not enforce the barrier. Deploy the Authority first and the business second as one release;
  a rollback of the Authority alone reopens BF-1. A single Authority instance during rollout (D-H12) as before.
- **Versions left unregistered before this change** have no staged publication, so the Authority cannot know about them; the
  registration status reports them as `NotRegistered` (publish again, OD-3). Before any rollout, a read-only business-side inventory is
  required: forms whose current version has a `FORM_VERSION_AUTHORITY_REGISTRATION_FAILED` audit event and no later
  `FORM_VERSION_AUTHORITY_REGISTERED` event for the same version. Not run on any persistent environment (not authorized).
- **Availability:** search on a form with a pending publication is unavailable until a designer retries the registration (since C-191
  a new publication is refused until then, also after a failed commit); there is no automatic background retry (it would need a non-human registration identity - a governance change not made).
- **A staged version whose stored layout no longer has the staged digest** (tampering, or a defect) is never registered, abandoned or
  superseded: the form reports `RecoveryBlocked` and stays unsearchable and unpublishable. Owner decision (C-191 final, §11.9): preserve
  the barrier and report it; an exceptional, separately authorized repair procedure is required and is **not implemented**.
- **Abandonment** needs the business publication coordinator's attestation since the C-191 final resolution (§11.9); design permission
  alone no longer withdraws a staged version. The attestation key is a new deployment secret of both the business API and the Authority.
- **Race coverage:** in the Stage B race the four publications all registered while the concurrent re-approvals were refused
  (pending or transient); the pending branch is proven deterministically with the Testing-only fault checkpoints, not by the race.
- **Concurrent C7 limitation recorded in §11.4** ("Registration during a concurrent security command") is superseded by this section.

### 11.8 BF-1 final check — a pending publication is never superseded (C-191)

Owner check of `34a5dae`: a new `PrepareFormVersionPublication` must never supersede a committed, unresolved publication in a way that
lets the Authority reopen search on an obsolete field manifest. **It did not hold.** Sequence: V1 (First, Phone) registered and Phone
approved; V2 removes Phone, the business commits it and the resolution is lost. A prepare of V3, or of another V2, was accepted and
marked V2 `Superseded`:

- registering V3 (Phone back) applied D-H3/D-H4 against V1, so the Phone approval carried over to V3 although V2 had removed Phone;
- registering another V2 that kept Phone left the approval effective on a manifest the business never made current (direct Authority
  access);
- abandoning V3 after a failed business commit lifted the barrier with the Authority judging V1 while the business was at V2: the
  removed field's ordinary approval was searchable again (BF-1 reopened). The publish path reached the first and third cases without
  any direct access: it never resolved a pending publication before preparing the next one.

Characterization tests run against the unchanged `34a5dae` code reproduced all three (3/3 passed, i.e. the defect present).

**Correction (smallest fail-closed):** the Authority refuses every prepare of a form while a publication of it is pending
(`FORM_PUBLICATION_PENDING`) - a later version, another version with the same number, or the same version again - and stages nothing;
the `Superseded` state is removed from the code and from the database check constraint (the migration is on this unmerged branch and
was never applied to a persistent environment). The business answers such a publish `409 FORM_PUBLICATION_PENDING`, commits nothing,
and the designer resolves the pending publication with Retry registration first. No security invariant is weakened; the cost is
availability (a publish after a lost resolution or a failed commit needs the retry first).

**Abandoned resolution.** An `Abandoned` resolution of a version already committed in the business would clear the barrier. The
Authority does **not** enforce this independently: it cannot see the business store and accepts `Abandoned` from any caller with
design permission on the form. Through the product it relies on trusted business-side sequencing - the publish path never abandons
after a commit (a failed or ambiguous commit leaves the publication pending), and recovery decides from the business store under the
per-form publication lock (Stage B: lost registration recovered as `Registered`, lost commit as `Abandoned`). A caller able to submit
Authority commands directly could abandon a committed version; that caller could equally register an invented manifest, since the
manifest is declared by the business (OD-3), so independent enforcement of abandonment alone would not close that class. Options for
an owner decision (not implemented): business-side evidence the Authority can verify, or a conservative abandonment that also applies
the staged manifest's invalidations.

**Superseded in part by §11.9:** the Abandoned analysis above describes `cffc57f`; the owner then required a fail-closed abandonment
contract and an explicit recovery-blocked state.

**Verification (local, 2026-11-24; nothing on a persistent environment):** Authority unit FormVersionPublicationTests 11/11 (3 new; the supersede test replaced; the three fail with the refusal removed); full Authority unit project 2,138 (1,927 passed, 211 authorized skips); contract FormVersionLifecycleContractTests 17/17 (1 new); Stage B FormVersionPublicationBarrierTests 6/6 on a freshly recreated stack (run 20261124-165940-9c181d41; 1 new); focused Stage B regressions on the same stack: RetainedFieldSearchTests 5/5 (race adapted: a pending publication is recovered before the next publish; in this run all three rounds registered, so that branch was not exercised), FieldSearchApprovalTests 5/5, FieldSearchExecutionTests 14/14, FormVersion* 14/14, DesignerTests 3/3; Playwright form-publication-registration 4/4; Release build without OpenAPI drift; full .NET CI-equivalent set 3,349/3,349 with 223 authorized skips (unchanged); characterization tests against unchanged 34a5dae reproduced the three superseding cases (3/3, not committed).

### 11.9 C-191 final resolution — fail-closed abandonment and recovery-blocked state (owner-authorized 2026-11-24)

The owner approved the C-191 supersession fix at `cffc57f` and authorized a final correction on PR #76.

**Abandonment contract (fail closed).** An `Abandoned` resolution is accepted only with the business publication coordinator's
attestation that the staged version was never committed:

- The Authority (`FormPublicationAbandonmentVerifier`) refuses `FORM_PUBLICATION_ABANDONMENT_UNVERIFIED` when the attestation is absent,
  signed with any other key, malformed, bound to another publication, outside a 5-minute issue window, or when its own statement shows
  a business version numbered at or above the staged one. Without a configured key no abandonment is accepted. The refusal is recorded
  like every refused field-search command; the publication stays pending and the barrier stays.
- The attestation is HMAC-SHA-256 with a dedicated deployment key (`FormPublication:AbandonmentAttestationKeyBase64`) over a pinned,
  domain-separated message: organization, form, the Authority's own publication id, business version id, number, layout digest, the
  highest version number in the business store, issue time. Binding to the Authority's publication id prevents replay onto a later
  prepare of the same business version. The key must differ from every workload, operator and user-assertion key; the business API and
  the Authority refuse to start otherwise. The Authority's pending-publication view now carries its publication id.
- The business (`FormPublicationAbandonmentAttestor`) signs only in recovery, holding the form's publication lock, after finding the
  staged version absent from its store and every stored version numbered below it. Otherwise it sends nothing and reports `Refused`
  (`ABANDONMENT_ATTESTATION_UNAVAILABLE` without the key, `NON_COMMITMENT_NOT_ESTABLISHED` when the store does not establish it).
- Authorization stays Authority-owned: the attestation decides only non-commitment and confers no permission (design permission is
  still checked); neither resolution changes the staged manifest; a registration carries no attestation.

**Trust boundary (stated, not closed).** The key is held by the business API. A fully compromised business runtime can still produce an
attestation, as it can already declare any manifest (OD-3). The mechanism removes the ability of any other holder of the business
workload credential, or of a design-permission user, to lift the barrier by submitting `Abandoned` directly. Production key custody
(Key Vault/Managed HSM, rotation) is not designed here; the local stacks use a synthetic Development/Testing value from `env.example`.

**Recovery-blocked state.** A committed pending version whose stored layout no longer has the staged digest is reported as
`RecoveryBlocked` (registration status and recovery outcome; `FailureReason` `STAGED_VERSION_MISMATCH`). Nothing is registered,
abandoned, regenerated or discarded; nothing is sent to the Authority; the barrier stays; a new publication is refused
(`409 FORM_PUBLICATION_PENDING`, nothing committed); a retry changes nothing; the failure is audited with `recoveryBlocked=true`; the
designer notice explains the state and offers no retry. **An exceptional, separately authorized repair procedure is required to end
it. It is not implemented** and needs its own design and owner authorization (scope, approvers, evidence, and how the Authority's
staged manifest and the business layout are reconciled without trusting the tampered row).

**Unchanged by design:** the designer's Retry registration requirement; no automatic retries; no other lifecycle change.

**Verification (local, 2026-11-24; nothing on a persistent environment):** Authority unit FormVersionPublicationTests 15/15 (4 new; the three attestation tests fail with the Authority check removed); contract FormVersionLifecycleContractTests 20/20 (2 new, 2 extended); Vitest AuthorityRegistrationNotice 6/6 (2 new); Stage B on a freshly recreated security-test stack: FormVersionPublicationBarrierTests 8/8 (run 20261124-203350-29924db6, 2 new; the first run 20261124-195607-0259a19f failed 7/8 on a wrong assertion in the new recovery-blocked test - it counted V1's own registration - corrected and rerun), RetainedFieldSearchTests 5/5, FieldSearchApprovalTests 5/5, FieldSearchExecutionTests 14/14, DesignerTests 3/3, FormVersion* 15/16 with the same assertion failure (run 20261124-202849-11ff4eaf; the corrected class then passed 8/8); Playwright form-publication-registration 4/4 (run 20261124-203918-6b67bc96); Release build; full .NET CI-equivalent set 3,356/3,356 with 223 authorized skips.
