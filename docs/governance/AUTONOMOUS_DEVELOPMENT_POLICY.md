# Autonomous Development/Test coordination

The EnterpriseForms Standing Autonomous Development Mandate of 2026-11-25 delegates routine coordination within authorized Development/Test work. The [review and authorization workflow](REVIEW_AND_AUTHORIZATION_WORKFLOW.md), [Phase C decision log](../decisions/PHASE_C_DECISION_LOG.md), approved architecture and milestone contracts govern scope and acceptance. Use the approved roadmap and the CLAUDE.md §11 requirements register to identify dependencies and evidence obligations.

## Authorization and execution permissions

| Gate | What establishes it |
| --- | --- |
| Project authorization | A recorded owner decision or standing mandate covers the specific action and package. The package satisfies documented prerequisites and exclusions. |
| Technical execution permissions | The current environment, tool policy, Claude permission prompts, credentials and GitHub rules allow the action. Use the active controls and normal permission mechanisms. |
| Human review and acceptance | The applicable governance record names the required human reviewer or owner and records their decision on the relevant scope and revision. Production release requires the separate PB-12 process. |

Apply every gate that governs the action. An item on the roadmap establishes planning context. A successful command establishes only its observed result. Each package needs its own authorization and prerequisite record. Agent technical verification supports human review and owner acceptance. [Release evidence and governance](PB12_SECURITY_EVIDENCE_RELEASE_STANDARD.md)

## Roles

The coordinator selects packages, maintains provisional bases and owns publishing and integration. Claude Code is the primary implementation writer. A separate read-only review agent inspects the actual Git diff on an exact head and base and records PASS, FAIL or BLOCKED. The coordinator requests a fresh review after a material change. The separate agent's findings inform the existing review process while the [proposed amendment](PROPOSED_DEVELOPMENT_AI_REVIEW_AMENDMENT.md) awaits an owner decision.

## Coordination loop

1. Resume from the [work-package register](WORK_PACKAGE_REGISTER.md). Verify the recorded checkpoint against local and remote refs, actual worktree state, active writer and latest owner decisions. Check external operation results before retrying.
2. Select eligible authorized work in dependency order. Record scope, authority, base SHA, branch, worktree, writer, acceptance tests, exclusions and checkpoints. Give Claude Code isolated implementation scope and make it the sole writer during each pass. Limit the invocation to the tools the pass needs.
3. Compare the handoff with the actual diff. Reproduce authorized checks using disposable Development/Test resources, record the exact tested SHA and evidence, and return routine in-scope defects to Claude. Record any unrun checks and their reason.
4. Prepare signed commits and push normally. Attach evidence to a reviewable PR and keep affected §11 criteria linked to verified evidence. Require applicable human reviews and checks on the exact current head before any authorized merge. Reconcile strict base updates and generated files before re-verifying a changed head.
5. Commit material checkpoints to the register. Continue other eligible work while a dependency is blocked. Consolidate owner decisions only when the remaining affected action needs owner input, a policy decision, additional authorization or a human reviewer.

## Action boundaries

- Keep one active writer per worktree or PR branch. Serialize work that shares a manifest, branch, or disposable resource. Preserve the last known Git and external operation state if ownership becomes uncertain.
- Follow the recorded release-only Stage B policy and any narrower assignment limits. Dispatch, retry and environment approval follow the applicable owner authorization and release gate. [Stage B policy](../testing/STAGE_B_RELEASE_ONLY_POLICY.md)
- Preserve security controls and mandatory human reviews. Escalate discrepancies between live protections and approved policy. Stage changes to repository protections for the owner's review when live administration is outside the assigned scope. [Repository protection](../delivery/BUILD00_REPOSITORY_PROTECTION.md)
- Keep persistent environments, Azure, restricted activation, production deployment, alert dismissal, finding closure and milestone acceptance behind their applicable owner or release gates. Keep publicly tracked evidence free of credentials, customer data and private runtime material.

The register is the session handoff. It records the current milestone, active and completed work, delegations, exact-head verification, PRs, findings, blockers, next eligible actions and owner decisions. Each new session reads it and confirms live state before proceeding.
