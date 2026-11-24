# Phase C9 — Form Version Management: status

- **Branches:** M1 `phase-c9/form-version-management` (from `198aeae`), squash-merged to `main` as `cd48e13` (PR #70; branch kept, tag `archive/c9-m1-form-version-management-9395ec5`). M2 `phase-c9/m2-version-comparison` (from `cd48e13`), squash-merged to `main` as `491fa9a` (PR #71; the branch was deleted automatically on merge; its commits are kept by tag `archive/c9-m2-version-comparison-03b5169`).
- **Architecture:** `docs/architecture/PHASE_C9_FORM_VERSION_MANAGEMENT.md`.
- **Decisions:** C-178 (PR #69 merge record, C8); C-179 (owner decisions D9-1…D9-9; WP-0 and M1 authorized); C-180 (M1 implemented; awaiting owner review); C-181 (D9-5 reconciliation and correction, §4); C-182 (M2 implemented; awaiting owner review, §5); C-183 (corrective review of PR #71, §6); C-184 (PR #71 merge record, §7); C-185 (PR #73 merge record and failed post-merge CI, §8); C-186 (test-host startup race, PR #74 and CI recovery, §9).
- **State (2026-11-23):** M1 and M2 merged to `main` (code integration only: `cd48e13`, `491fa9a`). Post-merge CI on `main` failed at `491fa9a` (§7) and at `7d0c879` (§8), each time on a test defect, and has been green since `3d930f6` (§9). Not final C9 acceptance, not independently reviewed, no production or Level 2 claim. M3 not started. G21 OPEN.

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
