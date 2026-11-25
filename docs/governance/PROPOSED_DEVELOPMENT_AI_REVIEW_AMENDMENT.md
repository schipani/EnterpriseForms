# Proposed amendment for Development/Test technical review

**Status — proposed under the Standing Autonomous Development Mandate V2.** This document requests one owner decision. The existing review requirements apply until the owner adopts the amendment and the enforcement check is verified. No live setting was changed to prepare this proposal.

## Decision requested

Approve a revision-specific independent AI technical review for routine Development/Test integration on this public development repository. Retain the recorded human review for the security-owned paths in BUILD-00 §4.1 and for material changes to security semantics, enforcement, grant scope, sensitive data, recovery or review governance. Retain qualified human assessment, independent cryptographic review, PB-12 production acceptance, and the owner’s milestone acceptance. The review agent uses its own session and has read-only access to the exact Git objects. It neither edits the change nor submits a GitHub approval as a person.

The amendment would replace the universal human approval and approval-after-last-push clauses of [ADR-PB-011 A-21](../decisions/ADR-PB-011-GITHUB-ACTIONS-SECURE-DELIVERY.md) and [BUILD-00 §1](../delivery/BUILD00_REPOSITORY_PROTECTION.md) only for the routine lane. The same rules continue for human-required changes and for existing formal acceptance. BUILD-00 §4.1 path coverage and the [review and authorization workflow](REVIEW_AND_AUTHORIZATION_WORKFLOW.md) continue to apply. The personal-account repository cannot resolve the organization teams in CODEOWNERS. Keep the file and record the named security reviewer on changes it covers pending the existing Azure DevOps milestone.

## Evidence and operation

Every PR requires a PASS from the independent review agent on its current head and base. The review examines the actual diff, requirements, security contracts, tests and reproducible evidence. It records reviewer session identity, source and base SHAs, disposition, severities and references. FAIL, BLOCKED, stale evidence, unresolved applicable findings, missing human approval where required and a mismatch in changed-path classification all prevent integration. A change to the head or base requires a fresh review record. The coordinator logs milestones and evidence in the [work-package register](WORK_PACKAGE_REGISTER.md).

Use a repository-scoped GitHub App as the expected source of the required status. Grant repository Metadata, Contents and Pull requests read and Commit statuses write. The App has no repository administration, push, merge, bypass or human review powers. It checks authenticated reviewer evidence from a separate execution context and the live PR before publishing. Do not accept a status or receipt produced by code or configuration from the PR. Verify the applicable human approval on the current head, from the authorized human reviewer, after the last push where required. Require the correct App source in GitHub. [Required status source](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets#require-status-checks-to-pass-before-merging)

## Proposed live configuration

Apply to active ruleset 25341169 on refs/heads/main after the owner approves the specific configuration and the App has published and demonstrated the status. Require confirmation of the App's actual numeric integration ID before applying the change. [Rulesets API](https://docs.github.com/en/rest/repos/rules)

| Setting | Proposed value and rationale |
| --- | --- |
| Required status checks | Keep all four current CI contexts and strict base checks. Add the required context EnterpriseForms Development/Test review with integration_id set to the verified App ID. A status from any other source cannot meet the gate. |
| Pull request review parameters | After fail-closed validation, set repository-wide required_approving_review_count to 0 and require_last_push_approval to false. The required App status enforces exact-head AI review for all PRs and eligible non-author human approval after the latest push for the human-required lane. This expresses the path distinction that the native repository-wide settings cannot express here. |
| Review protections | Keep dismiss_stale_reviews_on_push, required review thread resolution and extra approval for unattributed changes enabled. Keep the manual named security-owner requirement on protected paths. Keep require_code_owner_review false until the organization/team dependency is resolved. |
| Branch and security protections | Preserve active enforcement, no bypass actors, signed commits, linear history, deletion and force-push protection, existing merge methods, secret scanning and push protection, CodeQL and existing test and evidence gates. Keep Stage B release-only. |

Apply the changes as one verified transition after demonstrating refusal on missing or stale evidence, changed base, wrong publisher, an unresolved finding, a fork-supplied receipt, and missing human approval on protected changes. If the App is unprovisioned or the enforcement tests fail, the amendment cannot be used for integration. To use the existing human route, restore required_approving_review_count to 1 and require_last_push_approval to true and obtain the eligible reviews. Agents cannot substitute for the reviewer role in that route.
