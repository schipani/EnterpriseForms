# Fast-feedback testing and CI speed

Three levels. Only Level 3 (and its CI equivalent) gates a merge; Levels 1–2 exist for quick feedback and never replace it.

| Level | Command | Typical time | Docker | Scope |
|---|---|---|---|---|
| 1 | `scripts/dev/test.ps1 -Level 1` | ~1.5 min (measured 84 s warm; vitest on changed files only) | none | .NET build + unit/architecture/contract tests, ESLint, typecheck, changed vitest files |
| 2 | `scripts/dev/test.ps1 -Level 2 [-Class X] [-Spec y] [-Browser] [-NoRebuild]` | seconds to a few min | security-test stack, **kept running** | Real stack (two PostgreSQL, SpiceDB, Vault, ClamAV, authority, API, worker): impacted integration class and/or Playwright spec |
| 3 | `scripts/dev/test.ps1 -Level 3` | full | clean volumes, twice | Everything CI runs: specs, vectors, .NET + floor, frontend, SEC-P01..P12 (Stage A gate), Stage B integration, full Playwright |

Level 3 has no filter parameters: nothing can be skipped, every `--minimum-expected-tests` floor still applies, and a non-zero exit from any step fails the run.

Notes:
- Level 2 uses the `enterpriseforms-sectest` compose project, which pins the same subnets as the dev project (`compose.dev.yaml`). Stop the dev stack (`docker compose -f compose.yaml -f compose.dev.yaml down`) before using it.
- Level 2 `up --build` is incremental: unchanged image layers are reused and only changed containers are recreated. Use `-NoRebuild` for UI-only edits (Vite hot-reloads; the API container is untouched).
- A stack that has run the Stage A tamper tests must not be reused for Stage B (`ARCHIVE_BODY_MISMATCH` quarantine). Level 3 therefore runs `down -v` between the two.
- Examples: `-Level 2 -Class DocumentTests`, `-Level 2 -Spec designer-ux -NoRebuild`, `-Level 2 -Browser`.

## What changed in CI

Required status-check names are unchanged (the four `ci` jobs listed in `.github/rulesets/main-branch-protection.json`; the `stage-b` job keeps its original name).

1. **Concurrency cancellation** on `stage-b-integration` and `security-gate-a` for superseded pull-request runs (`ci` already had it). Scheduled/manual runs are never cancelled.
2. **`stage-b-integration` split into two parallel jobs**, each on its own clean stack: `Stage B integration tests (real stack)` and `Stage B browser end-to-end (real stack)`. The original-named job is now an aggregate (`needs` both, `if: always()`) that fails unless both reported `success`; skipped/cancelled/failed all fail it. Previously these ran serially on one stack.
3. **ClamAV starts first** (`.github/actions/start-stack`), so its 30–90 s signature load overlaps image building instead of following it. Third-party images are pulled in the background.
4. **BuildKit layer caching** (`docker/bake-action`, GitHub Actions cache, per-image scopes) for the four first-party images. Cache keys are content hashes, so the image under test is always built from the commit; only unchanged layers (NuGet restore, base layers) are reused. The step is best-effort — if it fails, compose builds the missing images itself (slower, never different). Shared images (`authority`/`authority-migrate`, `api`/`business-migrate`) now carry one explicit tag so they are built once.
5. **Playwright browser binary cache** keyed by the Playwright version (`install-deps` still runs for system libraries).
6. **Slowest-tests table** (`scripts/ci/summarize_trx.py`) in each integration job's summary, for future measurement.
7. Clean-state isolation is unchanged: stacks use tmpfs volumes and `down -v`; no container, database or volume is reused between runs.

Not done: `ci.yml` (≈1.4 min wall clock; slowest step is the 38 s build) and NuGet caching (restore is ~11 s; no packages lock file, so a cache would be keyed loosely for ≈5 s) — judged not worth the added surface. Security tests, counts, floors and evidence generation are untouched. `security-gate-a` still runs on schedule/manual only (unchanged); Level 3 runs it locally against the final tree.

## Measurements

Baseline, from the public Actions API for real runs on `phase-c/form-platform-expansion` (PR #11), `stage-b-integration` job:

| Run | Wall | Build test project | Stack start (build + ClamAV + migrations) | Integration tests | npm ci + Playwright install | Playwright | Teardown |
|---|---|---|---|---|---|---|---|
| 39370674594 | 735 s | 6 s | 112 s | 352 s | 18 s | 213 s | 11 s |
| 39364375259 | 799 s | 8 s | 121 s | 370 s | 24 s | 245 s | 10 s |

`ci` workflow, same run: 79 s (.NET job; build 38 s, test 7 s), 32 s frontend, 12 s specifications, 5 s compose. Integration tests (≈6 min) and browser tests (≈4 min) were serial; they are the bulk of the 12–13 min.

Expected after (not yet measured): wall clock ≈ max(integration job, browser job) ≈ 8 min instead of ≈ 12.5 min, plus whatever the cache saves on stack start.

**After: pending.** No pull request exists for this branch yet and this session has no GitHub credentials, so no post-change run has happened. Once a PR is opened, record the comparable numbers here (runs are readable at `https://api.github.com/repos/schipani/EnterpriseForms/actions/runs/<id>/jobs`) and note any optimisation that showed no gain. Not yet verified: that `docker/bake-action` with `source: .`/`workdir` loads images under the compose-expected tags (otherwise compose rebuilds them — visible as the `Build first-party images` step's outcome and a long `Start the stack` step).
