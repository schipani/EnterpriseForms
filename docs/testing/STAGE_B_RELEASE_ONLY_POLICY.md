# Stage B testing is release-only (permanent owner policy, C-131)

- **Decision:** C-131 (owner, 2026-11-07). Permanent and project-wide; not an IX-2 exception.
- **Rule:** Stage B runs **only during release preparation and only when the owner explicitly authorizes it**. It is started by hand; nothing starts it automatically.
- **Kind:** testing and delivery policy. It changes when Stage B runs, not what Stage B checks: no Stage B test, assertion, group, floor or evidence rule is removed or weakened.

## 1. What counts as a Stage B run

| Counts as Stage B (release-only) | Does not count (focused development testing, permitted) |
|---|---|
| The `stage-b-integration` workflow, for any group | Unit, architecture, contract, integrity and platform-console tests |
| `scripts/dev/stage-b.ps1 -Group …` or `-All` | `scripts/dev/stage-b.ps1 -Class <Name>`: one named class directly affected by the change |
| `scripts/dev/test.ps1 -Level 2` without `-Class`/`-Spec` (the whole Stage B integration suite) | `scripts/dev/test.ps1 -Level 2 -Class <Name>` or `-Spec <name>` |
| `scripts/dev/test.ps1 -Level 2 -Browser` (the whole Playwright suite) | Security-specific tests of the changed area |
| The Stage B steps of `scripts/dev/test.ps1 -Level 3` | The rest of Level 3 (specifications, vectors, .NET suites and floors, frontend, SEC-P01..SEC-P12) |

Instructions such as "run tests", "validate the feature", "run regression", "finish the stage" or "prepare the commit" are **not** an authorization to run Stage B. Only an explicit owner instruction authorizing Stage B for release testing is.

Stage B is **not** a prerequisite for ordinary development-stage acceptance and is not recommended as one.

## 2. Stage A is separate

Stage A (SEC-P01..SEC-P12, `security-gate-a`, `validate_security_evidence.py`) is unchanged by this policy. Its existing requirements (ADR-PB-003) still apply wherever they applied before.

## 3. How Stage B is started (release testing)

**GitHub Actions:** Actions → `stage-b-integration` → *Run workflow*. Inputs:

| Input | Meaning |
|---|---|
| `release_authorization` (required) | The owner's authorization reference (for example the decision-log id). A run without one fails in its first step. Recorded in the run summary with the actor, branch and commit |
| `groups` | `all` (default), or a comma-separated list of group names from `tests/integration/EnterpriseForms.StageBTests/stage-b-groups.json`. Unknown names fail the plan |
| `browser` | Whether to run the browser end-to-end job (default true) |

Each selected group runs on its own runner and clean stack, with its own test results, logs and artifact `stage-b-evidence-<group>`. A failed group can be rerun alone ("Re-run failed jobs", or a new dispatch naming that group). For `all`, the aggregate proves every discovered test ran exactly once and passed (`verify-all`). For a selection, it verifies each selected group (`verify-group`) and states that the other groups were not run.

**Locally:** `scripts/dev/stage-b.ps1 -All -Fresh -ReleaseAuthorization <reference>` (or `-Group <name>`), or `scripts/dev/test.ps1 -Level 3 -StageBReleaseAuthorization <reference>`.

All seven groups remain defined, assigned and discoverable: forms-documents, authorization-security-onboarding, records-lifecycle, administration-membership, search-catalogue, search-execution, protection-reconciliation.

## 4. What changed

| Item | Before | After |
|---|---|---|
| `stage-b-integration.yml` triggers | `pull_request` (main and the Stage A branch), weekday `schedule`, `workflow_dispatch` | `workflow_dispatch` only, with required authorization reference and group selection; runs in environment `stage-b-release` |
| Indirect invocation | `level3-script.yml` (on pull requests changing the Level 3 script) ran Stage B through `test.ps1 -Level 3` | Level 3 skips the Stage B steps unless `-StageBReleaseAuthorization` is given; the workflow does not give it |
| Required status check | The Stage B aggregate was required on `main` (`.github/rulesets/main-branch-protection.json` and live ruleset 25341169) | Removed from the ruleset file; **the live ruleset must be updated by the owner** (section 5) |
| Developer scripts | `stage-b.ps1 -Group/-All`, `test.ps1 -Level 2` (unfiltered or `-Browser`) and `-Level 3` ran Stage B | They refuse (or skip, for Level 3) without an explicit release authorization; `-Class` and `-Spec` stay available |
| `ci.yml` | Builds (does not run) the Stage B project and runs the group checker's self-tests | Unchanged: compilation keeps the suite healthy without running it |

## 5. Repository settings

### 5.1 Live branch ruleset (done, C-132, 2026-11-07)

On the owner's instruction (C-132), the required status check "Designer, parent-child, documents (real ClamAV), authorization, tamper regression, browser e2e" was removed from live ruleset 25341169 through the GitHub API (repository administrator credential), to match `.github/rulesets/main-branch-protection.json`. It was read back and compared with the state before the change:
- the only difference is that one required status check;
- unchanged: the four required `ci` checks, strict status checks, deletion and non-fast-forward protection, required signatures, linear history, the pull-request rule (thread resolution, stale-review dismissal), no bypass actors, enforcement active, target `refs/heads/main`.

The first API update also replaced the em dash in the ruleset **name** with mojibake (an encoding fault in the update script). The name was restored by a second update about a minute later and verified byte for byte. No protection was off at any point.

Until `b282cbb` reaches `main`, the old workflow on `main` still starts Stage B on pull requests. It is no longer required, so its result does not block a merge.

### 5.2 Owner-only execution (not enforced yet: owner action)

`workflow_dispatch` lets anyone with write access start the workflow, so owner-only execution is **not** enforced today. The environment `stage-b-release` does not exist yet. GitHub creates an environment automatically, without protection rules, the first time a job references it, so it must be created and configured **before** the first dispatch.

Steps (repository Settings → Environments → New environment):
1. Name: `stage-b-release` (exactly; the `plan` job references it).
2. **Required reviewers:** add the owner (`schipani`). Every run then pauses at the `plan` job until the owner approves it. All other jobs need `plan`, so no stack or runner starts before the approval.
3. **Prevent self-review:** leave **off**. The owner dispatches release runs and must be able to approve them. Runs dispatched by anyone else still wait for the owner.
4. **Allow administrators to bypass configured protection rules:** turn **off**, if the setting is offered, so the approval cannot be skipped.
5. **Deployment branches and tags:** "No restriction", or "Selected branches and tags" if release runs should be limited to named release branches.
6. **Environment secrets and variables:** none. Stage B uses synthetic credentials only.

Verification after configuring: `gh api repos/schipani/EnterpriseForms/environments/stage-b-release` shows a `required_reviewers` protection rule naming the owner. Until that check passes, the policy relies on the required authorization reference and on review, and owner-only execution must not be described as enforced.

## 6. Security coverage that now runs only at release

Many security regression tests live only in the Stage B suite, among them:
- the authorization and tamper tests;
- S1 rollback RB-02 to RB-15;
- the SpiceDB contracts CT-1 to CT-6 and IX2-CT-07;
- session CSRF and revocation;
- cross-tenant denial;
- the hostile-business-DBA search tests;
- the search-index foundation and builder tests;
- protection reconciliation.

Those assertions are unchanged, but they no longer run automatically.

**Proposed focused development-time coverage (for owner approval):** when a change touches one of these areas, run the area's classes with `stage-b.ps1 -Class` as a focused test of that change.

| Changed area | Focused classes |
|---|---|
| Authority authorization, SpiceDB client, projection | `AuthorizationAndTamperTests`, `ProjectionIntegrityRollbackTests`, `SpiceDbBehaviourContractTests` |
| Sessions, CSRF, identity | `SessionCsrfTests`, `SessionRevocationTests` |
| Content commit, manifests, verification | `AuthorizationAndTamperTests`, `RecordLifecycleTests` |
| Search index | `SearchIndexFoundationTests`, `SearchIndexBuilderStackTests` |
| Verified search, catalogue | `FieldSearchExecutionTests` (without its long benchmark where practical), `SearchCatalogueTests` |
| Protection migration and census | `ProtectionReconciliationTests`, `FieldProtectionMigrationTests` |

A further option is to give the pure assertions (not the Docker-dependent ones) unit-level equivalents over time. Nothing in this proposal changes a Stage B assertion.

## 7. Recording

- When Stage B was not run for a stage, the stage's evidence says so explicitly and never reports the omitted tests as passed.
- When it is run for a release, the run summary records the authorization reference, actor, branch, commit, the selected groups and the per-group results.
