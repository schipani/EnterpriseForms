# Development work-package register

Owner decisions and the repository's [review and authorization workflow](REVIEW_AND_AUTHORIZATION_WORKFLOW.md) govern each package. This register is a handoff and evidence index. Record the owner decision separately from its implementation, technical review, owner acceptance, and release approval. The [Phase C decision log](../decisions/PHASE_C_DECISION_LOG.md) records project decisions. Link the §11 requirements register from [PR #77](https://github.com/schipani/EnterpriseForms/pull/77) when a package affects a criterion.

## Run and resume a package

1. Record the owner authorization reference, permitted actions, exclusions, dependencies and acceptance criteria before assigning work. The coordinator verifies the base and prerequisites and assigns one implementation writer to a dedicated branch and worktree. Give the implementer the exact base and contract links. Serialize work that shares a branch, generated manifest, or test resource.
2. On handoff, record the last complete step, exact head, changed scope, test commands and results with run or artifact references, deviations, and the next authorized step. The technical reviewer checks the actual diff and exact tested head, records findings and test exclusions, and returns routine in-scope corrections to the writer.
3. After interruption, use the checkpoint to verify branch, worktree status, expected head, remote PR and any prior external operation before resuming. Keep partial changes intact and resolve writer ownership or a conflicting head before resuming. Confirm that authorization still covers the next action. Collect genuinely blocked decisions together for the owner.
4. Advance a package only when its prerequisites, recorded authorization and applicable checks are satisfied. Record owner acceptance only against the owner's decision. [C-131](../testing/STAGE_B_RELEASE_ONLY_POLICY.md) governs release-only Stage B. Keep secrets and customer data out of handoffs and repository evidence.

For each package record its ID and scope, authorization, allowed and prohibited actions, writer, branch/worktree/base, dependencies, acceptance criteria, implementation/review/acceptance status, evidence, blockers, and recovery checkpoint. A checkpoint identifies the verified head, completed step, and next permitted step.

## Assignment 001 checkpoint — 2026-11-25

**Owner authorization:** EnterpriseForms Coordinator Assignment 001, §§2–7. Project references C-193–C-195 are in [PR #77](https://github.com/schipani/EnterpriseForms/pull/77). This checkpoint describes the authorized pilot. The formal decision records govern their own scope.

**Common base:** fbf18bc734f9b6d9563a94a66721af49a7177003. The original worktree remains assigned to its existing branch. Claude Code implemented PRs #77–#80. The coordinator reviews and performs authorized integration reconciliation after its dependencies clear.

**Integration scope:** In order #77, #78, #79, #80, with any expressly authorized reconciliation, signed commits, normal pushes, exact-head passing checks, required reviews and exact-head merge protection. Record each resulting main SHA before advancing. Scope excludes force-push, policy bypass, alert dismissal, deployment, environment changes, persistent activation, finding closure, and milestone acceptance.

| Package and acceptance criteria | Branch / worktree / verified head | Implementation, technical review, owner acceptance and checkpoint |
| --- | --- | --- |
| [#77](https://github.com/schipani/EnterpriseForms/pull/77) · C9 close-out, C-194 WP-0, C-195, roadmap and §11 traceability. Confirm Development/Test disposition and open G27. First integration. | docs/c9-closeout-reconciliation · C:/t/c9close · 5dbc9beb642caffba9c17dc8d69fa7b4e6e13677 | Implementation reported complete. Technical review matched all 82 criteria to §11 and checked the design and close-out. GitHub verified both signatures and required checks. C-193 records C9 Development/Test acceptance. Merge blocked on review-policy discrepancy below. |
| [#78](https://github.com/schipani/EnterpriseForms/pull/78) · BH-1, C-193. After #77, combine main and regenerate manifests from the combined tree. Verify disposable-only 300 s default, three 10 s groups, exact-once 444-test/12-group discovery, release-only gates and fresh CI. | test/bh1-baseline-health · C:/t/bh1 · 21be48f05b56f154e429ada9c6d7088bf5bb694b | Implementation reported complete on the original head. GitHub verified signatures and checks there. Technical review confirmed overlay and override declarations and inspected release gates. Final reconciliation and exact-head review pending. Integration is conditional. Hosted post-BH-1 execution needs separate authorization. |
| [#79](https://github.com/schipani/EnterpriseForms/pull/79) · C-194 Package A. After #78, verify scope, dependency tree, lockfile, test and audit evidence on the current head. | deps/seroval-minimatch-remediation · C:/t/depA · 146dff37b17670f48d6f05fbadaf2010655734a5 | Implementation reported complete. GitHub verified signature and checks. Technical review found only the frontend package manifest and lockfile changed. Independent dependency verification and merge pending. Integration is conditional. |
| [#80](https://github.com/schipani/EnterpriseForms/pull/80) · C-194 Package B. After #79, regenerate the combined lockfile and verify clean install, lint, typecheck, build, relevant tests, differential evidence and supported transient inputs. | deps/zod-v4-migration · C:/t/depB · 1f9fbdff29425fa7761c48a5f0fe6e2785514202 | Implementation reported complete. GitHub verified signature and checks. Technical review found a transient Designer minimum-length path to the changed schema before a server round trip. Compatibility and integration remain blocked pending resolution. Owner acceptance of this tightening is conditional. |

**Merge governance blocker:** The live main rules returned zero required approvals and disabled approval after the last push. The checked-in ruleset, ADR-PB-011 A-21 and [BUILD-00](../delivery/BUILD00_REPOSITORY_PROTECTION.md) require both approvals and a manual, recorded security-owner review for applicable paths. No approving reviews were present on #77–#80. Resume at #77 after the owner establishes the applicable review path and the required reviews are recorded. Repeat live and exact-head checks before each merge.

### Coordination register

- **Scope and authority:** Assignment 001 §2. Coordinator is the only writer of this documentation package. Dedicated worktree C:/t/coord-001, branch docs/coordinator-assignment-001, common base above. Documentation and manifests only.
- **Acceptance and checkpoint:** Track the fields in §2 of the assignment and use existing governance sources. The PR identifies the signed checkpoint. Technical review records strict manifest validation and changed paths. Owner acceptance is pending. Merge requires further authorization.

### C-195 evidence follow-up

- **Scope and authority:** C-195 and Assignment 001 §4. Coordinator prepares a separate evidence-only PR after inspecting the existing hosted result. No approval, dispatch, retry, or merge. Branch and worktree to be assigned before writing.
- **Dependency and acceptance criteria:** [Run 45592321078](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078) completes. Verify exact SHA, authorization inputs, runner and containers, 50/150/480 timings versus 0.66 / 2.00 / 6.62 s, RB-13 and limits of the inference.
- **Checkpoint:** Plan logs confirm C-195, common base, search-catalogue and projection-integrity-restore, browser false. Two shards were in progress at review. Technical review, implementation and owner acceptance of the follow-up are pending.

### C10 M1

- **Scope and authority:** Assignment 001 §5 and C-194 WP-0 §9, linked from [PR #77](https://github.com/schipani/EnterpriseForms/pull/77). Claude Code is the designated sole implementation writer, with the coordinator as technical reviewer. Create its dedicated worktree and branch from verified integrated main after #77–#80 and the M1 prerequisite review.
- **Acceptance criteria:** WP-0 §9 M1-WP1 through M1-WP12 and §4 tests 1–20. Trace verified evidence to §11 #23–#27 and #56–#62. Keep G27 open for its provider step-up and owner-acceptance obligations.
- **Permitted scope:** Authority implementation, minimal business adapters and SRC-1 in disposable Development/Test. Use the normal approval mechanism for the Claude invocation and authorized local checks. Excludes M2 read endpoints, M3 pages, executed legacy migration, restricted activation, staging/production, Stage B execution, and merging the M1 PR.
- **Checkpoint:** Integration dependencies blocked. No M1 worktree, delegation, implementation, technical review or owner acceptance. Resume by checking integrated base, hosted findings relevant to M1, and the approved WP-0 boundaries.
