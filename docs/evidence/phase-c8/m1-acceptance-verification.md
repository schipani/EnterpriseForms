# Phase C8 M1 — acceptance verification evidence

- **Verified commit:** `b66d3578da0da73ec1caca55f08f6b2e64b85fbe` (`origin/phase-c8/administration-governance` = local HEAD, clean tree). Verified 2026-10-30.
- **Re-run on this commit by the verifier (not copied from earlier status):**

| Check | Result |
|---|---|
| `dotnet build -warnaserror` | 0 warnings, 0 errors |
| `EnterpriseForms.Api.ContractTests` | 306 / 306 |
| `EnterpriseForms.ArchitectureTests` | 67 / 67 |
| `EnterpriseForms.StageBTests` `AdministrationTests` (running security-test stack) | 5 / 5 |
| Frontend `lint`, `typecheck`, Vitest | clean, clean, 227 / 227 (29 files) |

- **Not re-run:** Playwright specs (reported 4/4 + 3/3 + 8/8 in `PHASE_C8_STATUS.md` §4.1; the owner's dev Vite and dev stack were running, so browser runs were not started in this checkpoint), real-stack regression (`FormAccessTests`, `SecurityAdministratorTests`, `BrandingTests`, reported 11/11).
- **Hosted workflows (`ci`, `stage-b-integration`, `level3-script`, `security-gate-a`):** **0 runs exist for this SHA or branch** (public GitHub API query). `ci` runs on `pull_request`/push to `main`; `stage-b-integration` and `level3-script` on `pull_request` to main/stage-a or `workflow_dispatch`; `security-gate-a` on `workflow_dispatch`/schedule only. No PR exists and no `gh`/dispatch credential is available here. Run identifiers: **none; not dispatched. Status: NOT RUN.**
- **Authority untouched:** `git diff b1e1b30..HEAD` lists no Authority, `trust_authority/`, SpiceDB schema or migration file.

## Remediation (C-81, 2026-10-30)

| Check | Result |
|---|---|
| `PendingAuthorityCommandTests` (2 tests, real stack, archive paused/unpaused) | 2 / 2; run three times in succession (once with the first authoring, twice afterwards), 0 failures, archive always left running |
| `organization-administration.spec.ts` (now 5 tests incl. UI Pending, isolated stack, port 5199) | 5 / 5 |
| Build `-warnaserror`; contract 306/306; architecture 67/67; frontend lint and typecheck | clean / passed |
| Manual NVDA/VoiceOver | **not performed**; checklist in `docs/testing/PHASE_C8_M1_MANUAL_ACCESSIBILITY_CHECKLIST.md` |

Finding C8-M1-01 (open, owner decision): see `docs/PHASE_C8_STATUS.md` §8; the Pending wording promises a roster update on refresh, but the projection is only written by a retry with the same idempotency key.

Hosted workflow results follow below (filled only from what GitHub reports).

### Hosted workflows — NOT RUN; manual actions required (recorded 2026-10-30)

The environment has no `gh` CLI and no GitHub API credential, and a token was deliberately not extracted from the Git credential manager. A pull request therefore could not be created, and no workflow was dispatched. **No hosted result exists; nothing is claimed to have passed. PR number: none.**

- Code-bearing candidate commit pushed to `origin/phase-c8/administration-governance`: `9f7b31c9fba68c8975d36297c8095dad5558445f`. The next commit on the branch is documentation-only (this section); the branch head at dispatch time is the final candidate.
- Manual actions for the owner (existing permissions, no protection bypass):
  1. Open a **draft** PR `phase-c8/administration-governance` → `main`: https://github.com/schipani/EnterpriseForms/pull/new/phase-c8/administration-governance . This triggers `ci` (`ci.yml`), `stage-b-integration` (`stage-b-integration.yml`) and `level3-script` (`level3-script.yml`; path-filtered, it only runs if the PR touches its watched paths — if it does not start, dispatch it manually).
  2. Actions → `security-gate-a` → Run workflow on branch `phase-c8/administration-governance` (`workflow_dispatch` only; it does not run on PRs).
  3. If `level3-script` or `stage-b-integration` do not start from the PR, Actions → choose the workflow → Run workflow on the same branch.
  4. Confirm every run shows the same head SHA, then record workflow name, run id, SHA and conclusion here.
- Do not merge the PR; M1 is not accepted until those runs are green and the owner accepts.
