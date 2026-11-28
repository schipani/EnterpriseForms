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
| [#78](https://github.com/schipani/EnterpriseForms/pull/78) · BH-1, C-193. After #77, combine main and regenerate manifests from the combined tree. Verify disposable-only 300 s default, three 10 s groups, exact-once 444-test/12-group discovery, release-only gates and fresh CI. | test/bh1-baseline-health · C:/t/bh1 · 21be48f05b56f154e429ada9c6d7088bf5bb694b | Implementation reported complete on the original head. GitHub verified signatures and checks there. Technical review confirmed overlay and override declarations and inspected release gates. Reused the C-195 discovery inventory with the BH-1 manifest and verified 444 tests across 12 groups exactly once. Final reconciliation and fresh exact-head CI are pending. Integration is conditional. Hosted post-BH-1 execution needs separate authorization. |
| [#79](https://github.com/schipani/EnterpriseForms/pull/79) · C-194 Package A. After #78, verify scope, dependency tree, lockfile, test and audit evidence on the current head. | deps/seroval-minimatch-remediation · C:/t/depA · 146dff37b17670f48d6f05fbadaf2010655734a5 | Implementation reported complete. GitHub verified signature and checks. Technical review confirmed the two-file scope, installed dependency tree, intact nested Vite fsevents entry, and npm audit reporting only Zod. GitHub frontend logs show 587 tests passed. Reconcile and recheck the exact final head after #78. Integration is conditional. |
| [#80](https://github.com/schipani/EnterpriseForms/pull/80) · C-194 Package B. After #79, regenerate the combined lockfile and verify clean install, lint, typecheck, build, relevant tests, differential evidence and supported transient inputs. | deps/zod-v4-migration · C:/t/depB · 1f9fbdff29425fa7761c48a5f0fe6e2785514202 | Implementation reported complete. GitHub verified signature and checks. GitHub frontend logs show 622 tests passed. Independent comparison of 600 schema inputs verified the unsafe-integer differences. With no maximum length set, the Designer accepts 9007199254740992 as transient minimum-length input and applies it to the local layout. Serializing that layout succeeds under v3 and throws under v4, before a server round trip. At the original head, the compatibility condition was unmet. The correction and current gating status are recorded below. |

**Merge governance blocker:** The live main rules returned zero required approvals and disabled approval after the last push. The checked-in ruleset, ADR-PB-011 A-21 and [BUILD-00](../delivery/BUILD00_REPOSITORY_PROTECTION.md) require both approvals and a manual, recorded security-owner review for applicable paths. No approving reviews were present on #77–#80. Resume at #77 after the owner establishes the applicable review path and the required reviews are recorded. Repeat live and exact-head checks before each merge.

### Coordination register

- **Scope and authority:** Assignment 001 §2. Coordinator is the only writer of this documentation package. Dedicated worktree C:/t/coord-001, branch docs/coordinator-assignment-001, common base above. Documentation and manifests only.
- **Acceptance and checkpoint:** Track the fields in §2 of the assignment and use existing governance sources. The PR identifies the signed checkpoint. Technical review records strict manifest validation and changed paths. Owner acceptance is pending. Merge requires further authorization.

### C-195 evidence follow-up

- **Scope and authority:** C-195 and Assignment 001 §4. Coordinator prepares the separate evidence-only PR after inspecting the hosted result. No approval, dispatch, retry, or merge. Branch docs/c195-hosted-baseline, worktree C:/t/coord-c195, common base above.
- **Dependency and acceptance criteria:** [Run 45592321078](https://github.com/schipani/EnterpriseForms/actions/runs/45592321078) completes. Verify exact SHA, authorization inputs, runner and containers, 50/150/480 timings versus 0.66 / 2.00 / 6.62 s, RB-13 and limits of the inference.
- **Checkpoint:** Plan logs confirm C-195, common base, search-catalogue and projection-integrity-restore, browser false. Independent verification matched 65/65 and 14/14 executed test identities, including RB-13. Server p50 was 0.5906 / 2.4474 / 8.2169 s against prior 0.6642 / 1.9955 / 6.6219 s. Two slower observations do not establish a reproducible code regression. Draft [PR #82](https://github.com/schipani/EnterpriseForms/pull/82) records the result at signed cdfc566b23a8c961678e1f1d8fce86fa74893036. Owner acceptance is pending.

### C10 M1

- **Scope and authority:** Assignment 001 §5 and C-194 WP-0 §9, linked from [PR #77](https://github.com/schipani/EnterpriseForms/pull/77). Claude Code is the designated sole implementation writer, with the coordinator as technical reviewer. Create its dedicated worktree and branch from verified integrated main after #77–#80 and the M1 prerequisite review.
- **Acceptance criteria:** WP-0 §9 M1-WP1 through M1-WP12 and §4 tests 1–20. Trace verified evidence to §11 #23–#27 and #56–#62. Keep G27 open for its provider step-up and owner-acceptance obligations.
- **Permitted scope:** Authority implementation, minimal business adapters and SRC-1 in disposable Development/Test. Use the normal approval mechanism for the Claude invocation and authorized local checks. Excludes M2 read endpoints, M3 pages, executed legacy migration, restricted activation, staging/production, Stage B execution, and merging the M1 PR.
- **Checkpoint:** Integration dependencies blocked. No M1 worktree, delegation, implementation, technical review or owner acceptance. Resume by checking integrated base, hosted findings relevant to M1, and the approved WP-0 boundaries.

## Standing mandate checkpoint — 2026-11-25

The owner delegated ongoing Development/Test coordination in the EnterpriseForms Standing Autonomous Development Mandate. See the [operating policy](AUTONOMOUS_DEVELOPMENT_POLICY.md). C9 close-out and dependency integration precede C10 M1. G27 remains open, and the six Dependabot alerts on main remain tracked.

- **PR #80 compatibility correction.** Claude Code is the sole implementation writer in C:/t/coord-zod-compat on review/zod-v4-compatibility, based on 1f9fbdff29425fa7761c48a5f0fe6e2785514202. The brief preserves Zod v3 integer behavior in the two affected renderer schema fields while keeping patched v4. The coordinator owns verification, signing and the normal PR update. The correction and verification results are recorded below.
- **PR #78 rehearsal.** Git merged the candidate #77 and #78 trees without conflicts. Recalculation from candidate tree 2222aaeef150b3df8c6fffb17f0a99bd697776bf matched both checked-in manifests across 312 files. After #77 is integrated, regenerate from the actual combined checkout and require fresh CI before considering #78 for merge.
- **C-195.** The completed review and the original SHA, inputs, measurements and RB-13 evidence remain in draft [PR #82](https://github.com/schipani/EnterpriseForms/pull/82). It remains a separate evidence change. Regenerate the manifests if its eventual authorized integration base changes.
- **C10 M1.** The 12 WP-0 §9 packages remain the implementation scope, subject to #77–#80 and the prerequisite check above. No owner acceptance has been recorded for this milestone.

### Main ruleset reconciliation

Live ruleset 25341169 is active with no bypass actors and the four approved required checks. Its history records the drop from one approving review to zero on October 19 (versions 53685602 and 53862490) and the removal of approval after the last push later that day (version 53863694). The November 7 C-132 removal of Stage B from required checks retained those older settings. The current [checked-in ruleset](../../.github/rulesets/main-branch-protection.json) and [ADR-PB-011 A-21](../decisions/ADR-PB-011-GITHUB-ACTIONS-SECURE-DELIVERY.md) still call for one approval and approval after the last push. Only the owner account currently appears as a direct collaborator.

1. The owner designates an eligible non-author reviewer who can approve the latest push. Use a manual, recorded security-owner review for paths defined in BUILD-00 §4.1. Enforceable CODEOWNERS review has a separate recorded dependency.
2. The owner restores the approving-review count to 1 and approval after the last push to true, retaining the strict required checks, stale-review dismissal, thread resolution, signed commits, linear history and absence of bypass actors. Preserve the release-only Stage B policy.
3. Read back the complete active rules and exact-head approvals. Resume at #77 only after the governance requirements are satisfied. Repeat the check at each successor's updated head.

### PR #80 correction and dependency rehearsal

- Claude Code completed the two-file correction in C:/t/coord-zod-compat. The first invocation waited on edit permission and left the worktree clean. It resumed under default permission mode with session approvals for the two renderer files. A second implementation pass addressed the coordinator's feedback and made the regression test exercise the public serializer.
- Signed [977e7ee501f13b9593e6ff68aa68d2f2db90802b](https://github.com/schipani/EnterpriseForms/commit/977e7ee501f13b9593e6ff68aa68d2f2db90802b) is on the existing deps/zod-v4-migration PR branch. It keeps Zod 4.6.7 and restores the v3 integer behavior for the two affected client fields. The coordinator checked the diff, the unchanged server contract, and the tested blobs against the signed commit.
- On the corrected checkout, npm ci, lint, typecheck, build and all 626 tests over 72 files passed. The independent comparison against Zod 3.25.76 matched 2,400 layout cases, 2,400 JSON parses, 671 rule cases and four actual Designer edit-to-serializer cases. The corrected branch audit reported no Zod finding. This is additional layout compatibility evidence for §11 #17. The authoritative register and its acceptance statuses are in PR #77.
- Rehearsal tree 36a3139428b39fc423871c62bd842b0021fd7817 combines the original #79 head and the corrected #80 head. In an isolated scratch checkout, a clean install and dependency-tree check passed and npm audit reported zero vulnerabilities. The combined lockfile stayed byte-for-byte unchanged during verification. The original Vite optional fsevents entry is preserved. This is a rehearsal. After #79 is merged, regenerate and verify the lockfile on the actual integration base.
- The reproduced compatibility defect is corrected. The four required checks and CodeQL passed for the corrected head against fbf18bc734f9b6d9563a94a66721af49a7177003. The hosted synthetic merge tree matched the signed head tree. Final base reconciliation after #79 still requires its own CI and human reviews. No alert or milestone acceptance is recorded. [Required checks](https://github.com/schipani/EnterpriseForms/actions/runs/45612255480) · [CodeQL](https://github.com/schipani/EnterpriseForms/actions/runs/45612252119)

## Mandate V2 checkpoint — 2026-11-25

**Current milestone:** C10 M1 development on an isolated provisional base. The owner authorized M1–M3 and integrated closeout preparation in Development/Test. The normative scope is C-194 and WP-0 §9. M2 and M3 depend on the Authority behavior and wire contracts. The subsequent integration base must match the actual approved main and be reverified.

**Provisional base:** signed 6cc574d25a180f2b031b1e113b19b0231893a634 on coord/c10-provisional-base, built from fbf18bc7 and then the original #77, #78, #79 and corrected #80 heads. Strict specifications passed with 312 files. Advance this base only by a recorded reconciliation. It does not establish acceptance or integration on main.

| Role | Active work and exact base |
| --- | --- |
| Codex coordinator | Provisional integration, #77 traceability corrections, review findings, signed check and PR checkpoints. |
| Claude Code implementation | WP1/WP12 handoff 1b24745e-90dd-46ff-b202-bf3163ec4596 in C:/t/c10-m1. WP7 handoffs fb45aa31-0f3d-40c1-b09d-e989b91299cf and 1cdb98a6-c0a5-43a5-9e5a-6a530e050188 in C:/t/c10-stepup. WP3 handoff 78b0afb5-f645-4073-a80d-d33db128ef0d in C:/t/c10-lineage. WP6 handoff b597f792-c049-4d0c-9547-67d548462144 in C:/t/c10-expiry-admission. WP10 corrective session 727c964e-c66a-4d99-af3b-bac4eea9c0e0 remains assigned to C:/t/c10-lineage-audit. Claude is the only writer in an assigned worktree until handoff. |
| Independent technical reviewer | Separate Codex session reviewed #77–#82 and exact C10 candidates. Read-only Claude review sessions covered WP2 and the WP7 three-command scope during the Codex availability interruption. The Codex reviewer resumed and independently verified WP7 and WP3. An AI PASS supplies technical evidence under existing policy. |

**Assignment 001 integration:** Independent PASS was recorded for #77 at 5865eb8982742dfd6cdf0a891218009a396ce248, #78 at be2e4f0bff8268f50b70d4c0c411f1e8ed28068a, #79 at 146dff37b17670f48d6f05fbadaf2010655734a5, #80 at 977e7ee501f13b9593e6ff68aa68d2f2db90802b and #82 at cdfc566b23a8c961678e1f1d8fce86fa74893036. The prior #81 head ed96c36c received technical PASS for proposal preparation. The required checks and CodeQL were green on those exact heads. All six were reconciled locally in C:/t/c10-reconcile at ccefd70ed990aa55f25a4f3c23c7c5105aae2994. The rehearsal regenerated the combined 316-file manifests. The combined frontend lockfile was preserved after reconciliation and a clean install and audit reported zero vulnerabilities. Main remains fbf18bc734f9b6d9563a94a66721af49a7177003. Replay the ordered integrations against the live base after the applicable human and policy gates pass.

**C10 functional checkpoints**

| Work package | Evidence and remaining work |
| --- | --- |
| M1-WP1 and WP12 | Provisional [PR #83](https://github.com/schipani/EnterpriseForms/pull/83), feat/c10-m1-authority at 1488f7751739fe3ea6ec621db6f2f65758e938b2, C:/t/c10-m1. Independent PASS. PostgreSQL 18 verified empty and populated upgrades, terminal refusal for migrator and runtime, tenant-bound foreign keys, the organization depth default and document view and flag constraints. Exact Authority run 1,931 passed and 211 service-dependent skips. Its TRX SHA-256 is 78AF1924741E4FB4E7613B8758F3C6E3EA117BDFF284D1DA2743C2F8C46517EB. Four provisional CI checks passed. WP12 independently passed at 3bbc1634387106849b386e6f2fae1ce4655b747a for seven comment-only edits. |
| M1-WP7 and adapter subset | Provisional [PR #84](https://github.com/schipani/EnterpriseForms/pull/84), feat/c10-m1-step-up at b70de6adec26d31c5be668f9a78503c8d8df9d4d, C:/t/c10-stepup. Independent PASS for create, revoke and transfer. Exact local runs recorded 1,949 Authority and 1,102 Business API contract passes, with 211 and 12 service-dependent skips. Remaining proof includes UpdateShareScope, deterministic enforcement, disposable-stack callers and provider-backed G27. |
| M1-WP2 | C:/t/c10-expiry, feat/c10-m1-lineage-expiry at 58e81105be7952778d531cc62e83571040f92fd0. Independent evaluator PASS with nine local tests. The call sites, committed data proof and cursor binding remain open. |
| M1-WP3 | [Draft PR #85](https://github.com/schipani/EnterpriseForms/pull/85), C:/t/c10-lineage, feat/c10-m1-lineage at 48dcc4b8a55814513e7ad92d2db68e0515cb9c1d. Independent PASS for CreateShare after the uniform parent confidentiality refusal. Exact Authority run 1,950 passed and 211 skipped. |
| Provisional M1 integration and WP11 | coord/c10-m1-provisional-integration in C:/t/c10-m1-join advances from cf7abb0e to signed 32590f5504db012ce1fc1912e5fd417b7e483031. Independent PASS confirms the successor preserves legacy request bytes and forwards optional parent and expiry. Five exact-head suites passed 3,399 tests with 223 service-dependent skips. The preliminary readiness probe timed out once and passed on the signed rerun. Ownership projection and end-to-end adapter evidence remain open. |
| M1-WP4 and WP5 | Scope updates in C:/t/c10-update-share on feat/c10-m1-update-share. Revoke cascade in C:/t/c10-revoke-desc on feat/c10-m1-revoke-descendants. Both use cf7abb0e as their recorded provisional base and will reconcile with its successor after handoff. |
| M1-WP6 admission | C:/t/c10-expiry-admission, feat/c10-m1-expiry-admission at 96e79b320b83d4b9607f891d37af02492443e08d. Independent PASS after correcting extraction from full committed source payloads. The coordinator and reviewer each ran all 28 admission tests. Verified journal source, plan-shape guard and worker remain open. |
| M1-WP10 | C:/t/c10-lineage-audit, feat/c10-m1-lineage-audit from 1488f775. Exact d5fd73b2473fadb9c232c3c0b868846f78d6aaf5 passed 24 local tests. Independent FAIL for historic ownership, indirect lineage and recipient selection. Corrective session active. Verified snapshot input, read-only database adapter and operator boundary remain open. |
| M2 share ETag preparation | feat/c10-m2-share-etags at 0ac77c3c71943a721a22efc3cbf10194e5421039, C:/t/c10-share-etags. Independent PASS for the 428/412 transport contract and 13 exact-head focused tests. The authorized route and atomic Authority CAS remain dependencies. |
| M3 PG-022 groundwork | C:/t/c10-m3-sharing, feat/c10-m3-sharing-page from cf7abb0e. Claude is preparing the authenticated route and existing read-path integration. The richer M2 inputs and the other M3 routes remain dependent work. |

**Next executable work** Finish and review scope updates, revoke cascades and the read-only classifier repairs. Reconcile those branches with the replay-corrected provisional base. Continue access-time enforcement and cursor binding on each authorized read path and connect the remaining Authority lifecycle packages. The evidence above covers scoped increments. Formal C10 requirements stay open until the corresponding integrated proof exists.

**Owner decision:** [Proposed Development/Test review amendment and exact settings](PROPOSED_DEVELOPMENT_AI_REVIEW_AMENDMENT.md). Existing merge gates continue meanwhile. Remaining C10 work includes graph commitment, access-time expiry, lineage and transitions, document binding, journal and adapters before dependent business APIs and UI. G27 is open. C-195 remains in draft [PR #82](https://github.com/schipani/EnterpriseForms/pull/82).

## Direct development checkpoint — 2026-11-26

Codex orchestration was discontinued; development continues directly in Claude Code under the same Development/Test mandate. Nothing below has an independent review yet: each row is signed implementation evidence with the exact local test runs, not acceptance. No PR was merged, no repository protection or persistent environment changed, and no Stage B run took place.

**Reconciled candidate:** coord/c10-m1-candidate-r2 in C:/t/c10-candidate, from the d090c646 lifecycle candidate plus the WP3 confidentiality fix 48dcc4b8, the WP6 admission branch 96e79b32 and WP10, then fast-forwarded through every package below. Head b21af132. The CI-equivalent .NET gate (scripts/ci/run_dotnet_tests.py) passed on each checkpoint; the last run was 3,542 passed, 223 authorized skips, 0 rejected.

| Work package | Signed head and evidence | Remaining |
| --- | --- | --- |
| Test defects | 72378136. The d090c646 lifecycle failures were load-induced: FieldSearchRetryPolicyTests waited on the real clock against a 2 s budget, and the readiness test started the in-memory Authority through the 2 s probe. Both now use a deterministic clock or warm host; production code unchanged. The concurrent five-project run passes. | — |
| M1-WP10 | feat/c10-m1-lineage-audit 65e476f8, b8860709, 7e38ac01. Unknown or empty grantors and missing, unverifiable, ambiguous or empty historical ownership are refused (InsufficientEvidence, LINEAGE_EVIDENCE_INCOMPLETE) before any classification, with no parent, depth, candidate or coverage claim; a covering Manager share with unresolved lineage refuses instead of proving NoCoveringParent. Read-only evidence assembler from committed facts, fixpoint dry run, REPEATABLE READ READ ONLY reader and --legacy-lineage-dry-run (Development/Testing). 67 unit tests; the out-of-solution disposable PostgreSQL 18 gate (scripts/dev/c10-wp10-lineage-pg-gate.ps1) passed as a SELECT-only role and as the administrator, with every table unchanged and only read-only statements logged. | Independent review; execution stays unauthorized. |
| M1-WP9 | feat/c10-m1-sharing-timeline 880d9769. ShareScopeUpdated and one ShareInvalidated per dependent on the audit stream, staged in the accepted command's transaction, attributed to the command and committed revision, command_state_at_commit CommittedAwaitingProjection, allowlisted timeline kinds; canonical bodies unchanged. 7 tests. | Record-scoped history query and route (M2). |
| M2 PATCH share | feat/c10-m2-edit-share 24a7332b. PATCH /api/v1/form-records/{id}/shares/{shareId}: strong If-Match (428/412), stale share revision 412 fresh or replayed, organization conflict 409 without retry, concealment 404, Authority refusals passed through, manifest-bound items, explicit expiry decision, step-up only through HumanCommandPayload, projection only after Effective, new ETag. OpenAPI and client regenerated. 15 tests. | Authoritative share-details read for the current revision and expiry. |
| M1-WP2 | feat/c10-m1-access-expiry 3377ce16. ShareExpiryOverlay in ProjectionIntegrityVerifier: an expired share (own expiry or lineage minimum) confers nothing on any released check or lookup, never turning a Deny into an Allow; projection comparisons stay raw; indexed engines fall back or invalidate while the caller holds an expired share; discover continuations and catalogue snapshots end at the earliest live-share expiry. 10 tests; the three new store queries executed on a disposable PostgreSQL 18. | Expiry-aware indexed postings (G06 review), running-stack download evidence, a share_expires_on index. |
| M1-WP6 | feat/c10-m1-expire-share 1c94fdfc. ExpireShare (internal kind, reserved key prefix) through ShareExpiryConvergenceService with journal-verified source, admission, plan-shape guard, derived attribution and ShareExpired; ShareExpiryLoop in Development/Testing only. 14 tests covering Section 4 tests 1–5, 7, 10–12, 14, 15, 17–20 offline. | Tests 8, 9, 13, 16 on real infrastructure. |
| M1-WP5 | feat/c10-m1-narrowing-cascade 301770ee, b21af132. Narrowing an item set or a Manager role invalidates uncovered descendants (UpstreamNarrowed) in the same command; a team removal that ends the grantor's only path invalidates that member's delegations (GrantorPathLost). 6 tests. | Member deactivation path loss; ownership-transfer invalidation needs an owner decision (WP-0 §3.4 and CLAUDE.md §4B differ on owner-granted shares). |

**Next executable work:** independent review of the rows above; reconcile M2 and M3 onto the candidate; WP8 document items; WP11 RECORD_OWNERSHIP_TRANSFERRED projection; WP9 record-scoped history; member-deactivation path loss.

## Direct development checkpoint 2 — 2026-11-26

Same mandate and limits as above; still no independent review, merge, push, protection change, persistent-environment change or Stage B run. Work now lands directly on coord/c10-m1-candidate-r2 (C:/t/c10-candidate) as signed commits. Head b35349b1: CI-equivalent .NET gate 3,608 passed, 223 authorized skips, 0 failed, 0 rejected; strict specification validation pass; frontend lint, typecheck, build and 77 files / 650 Vitest tests pass (first frontend run for C10).

| Work package | Signed head and evidence | Remaining |
| --- | --- | --- |
| Ownership decision (owner, 2026-11-26) | 669c916f, recorded as WP-0 §3.4.1. Owner-granted shares are durable grants; delegated shares depend on the grantor holding the committed parent Manager share; a transfer invalidates no share. A non-owner may revoke only a delegated share they registered while holding an Active Manager role on its committed parent (previously any grantor, so a former owner kept a revoke right). Unresolved legacy shares: not reparented, not invalidated, revoked by the current owner only. OwnershipTransferShareTests (11). | Independent review of the revoke tightening. |
| M1-WP5 member deactivation | 669c916f. DisableIdentityBinding invalidates the member's delegations and subtrees (GrantorPathLost) in the same command; held shares, durable and legacy grants kept; re-enabling restores no invalidated share. ShareMemberDeactivationTests (4). | — |
| M2 share details | 7c7fd9c3 (Authority GetShareDetails), 185833f8 (merge of feat/c10-m2-edit-share), dfd5c3b7 (business GET share, ETag from the Authority's committed share revision). ShareDetailsServiceTests (8), ShareDetailsTests (5). | Effective-scope, /me/access, reassignment endpoints. |
| M1-WP9 record history | 34af54d6 (Authority security-history from committed edges, own command state per item), 66ee875b (business shares/history and ownership-history). RecordSecurityHistoryTests (5), RecordSecurityHistoryHandlerTests (3). | Superseded expiry-only updates not listed (needs a command-to-record index); Security Auditor path not added. |
| M1-WP11 transfer projection | 70db79c3. RECORD_OWNERSHIP_TRANSFERRED once per Effective command, keyed by security command id. OwnershipTransferProjectionTests (2). | — |
| M3 | f958ef3d (merge of feat/c10-m3-sharing-page), d7a312bd (edit dialog: authoritative revision, 412 reload, 409 no retry, DST-safe expiry), b35349b1 (Authority history card). | PG-023 details page, PG-053 transfer UI, PG-066 my access; manual accessibility review. |
| M1-WP8 documents | Not started beyond the WP1 schema. | Full package. |

**Next executable work:** WP8 document items; remaining M2/M3 endpoints and pages; independent review of every row; PR consolidation once the owner decides (final report of 2026-11-26).

## Direct development checkpoint 3 — 2026-11-26

D6 complete on the candidate at 8c712cb3: security_commands.target_form_record_id (immutable by trigger, backfilled from committed payloads, Development/Test only) and ix_journal_security_command; the record history now lists every committed expiry change including superseded ones, each marked journal-backed only when its authorization-journal entry exists. Verified on a disposable PostgreSQL 18 (backfill, trigger, indexes, Down). WP8 reassessed (owner-approved split WP8-A / WP8-B); WP8-A is blocked on one design point: the Authority does not persist an attachment's version number, so shared_through_version_number cannot yet be set from Authority evidence at grant time (see the session report). No WP8 code committed.

## Direct development checkpoint 4 — 2026-11-27

Candidate coord/c10-m1-candidate-r2 at 2b0887c1 (clean tree, signed). CI-equivalent .NET gate: 3,635 passed, 0 failed, 223 authorized skips, 0 rejected; strict specification validation pass; frontend lint, typecheck, build and 78 files / 659 Vitest tests pass. No independent review, merge, push, protection change, persistent-environment change or Stage B run.

| Item | Signed heads | Evidence | Remaining |
| --- | --- | --- | --- |
| D6 expiry history | 8c712cb3 | target_form_record_id + journal index; disposable PostgreSQL 18 (backfill, trigger, Down) | — |
| WP8-A documents | 68bef48c, aa0b7abc, 6779cb71, 836fa9e8, b6f2dc98, 9ebb056d | version evidence from verified attachment rows; document-only shares; flags, delegation caps, boundaries; updates and narrowing; partial-read release and download within the boundary; details; UI | Existing attachments need their record's next accepted commit before sharing (production-readiness obligation) |
| WP8-B replacement | — | Blocked: needs CARV 4 and a fourth floor migration (E3 governance); owner decision pending | Owner decision |
| D5 indexed search | c64ae256 | Expired shares excluded from share-only candidates; no blanket refusal; snapshot ends at the earliest live expiry; real-PostgreSQL gate scripts/dev/c10-d5-share-only-pg-gate.ps1 4/4 | G06 review (not closed) |
| M2 | 492fe1a7 | effective scope, /me/access, member owned records and reassignment | — |
| M3 | 9d0b3f5f, 2b0887c1 | PG-023, PG-053 (transfer and reassignment), PG-066; disable dialog text per D2 | Playwright axe and manual NVDA/VoiceOver not run |

Consolidation check: every C10 worktree head is in the candidate except feat/c10-m2-share-etags 0ac77c3c, whose two files are byte-identical in the candidate (4f8ad734); other worktrees hold only untracked test logs.

## Direct development checkpoint 5 — 2026-11-27 (WP8-B)

Candidate coord/c10-m1-candidate-r2 at 695ee7b3 (signed, clean). CI-equivalent .NET gate 3,643 passed, 0 failed, 223 authorized skips, 0 rejected; strict specification validation; frontend lint, typecheck, build and 79 files / 660 tests. WP8-B implemented under the existing CARV per owner decisions C-196 (replacement only through owner-granted Contributor/Manager shares; delegated requests rejected; only the replacing share boundary advances, in the accepting commit; every check re-derived at Commit). Closeout record: WP-0 Sections 3.6.1 and 10 (22082f2f, 695ee7b3). Independent review must confirm that no CARV increment is required. Business upload admission for share replacement has no contract test (no upload-handler harness exists); it is covered by the planned focused Stage B. No review, merge, push, Stage B or persistent change.

## Direct development checkpoint 6 — 2026-11-27 (independent review)

Candidate coord/c10-m1-candidate-r2 at c68117de (signed, clean; corrections 1b2d74e2 and c68117de on top of 695ee7b3). CI-equivalent .NET gate 3,660 passed, 0 failed, 223 authorized skips, 0 rejected; strict specification validation; frontend lint, typecheck, build and 80 files / 662 tests. Three reviewer agents with fresh context and read-only access (Claude Code subagents, not a human or external review) reviewed 695ee7b3 and re-reviewed the corrections; dispositions in WP-0 Section 10.4. CARV: no increment required (two reviewers, re-confirmed on the corrected code). Corrected: expired shares conferred edit scope (high); replacement past the share boundary; lineage-minimum snapshot expiry; record-list continuation; Access-tab step-up; owned-records readers; ownership-history readers and Effective-only reader authority; quarantine; expiry-loop resilience; partial-reader document history and historic downloads; replacement field binding; non-decreasing attachment versions (partial D-1); ExpireShare inventory (Section 4 test 16, static). Open for owner decision: D-1 residual (same version number, new bytes; options in Section 10.4), D-5, F-6, F4, F6, N-2, read audit, replacement after an unaccepted upload, pre-existing child-record transfer. PR consolidation rehearsed locally (throwaway worktree, removed): squash simulation of #79, #80, #77, #82, #78 then merge into the candidate; mechanical conflicts only; resolution recipe in the final report. No push, merge, PR closure, Stage B or persistent change.

## Direct development checkpoint 7 — 2026-11-27 (final bounded defect correction, C-197)

Candidate coord/c10-m1-candidate-r2 at 241cf8b9 (signed, clean; corrections 3d1406da and 1de30596, record 241cf8b9, on top of c68117de). CI-equivalent .NET gate 3,674 passed, 0 failed, 223 authorized skips, 0 rejected; strict specification validation; frontend lint, typecheck, build and 81 files / 665 tests. Owner decisions 1-5 implemented for Development/Test: D-1 attachment content evidence (Authority migration AuthorityC10AttachmentContentEvidence; no CARV increment, re-confirmed by reviewers), version numbering from the accepted version (business migration BusinessC10ProcessedVersionNumbers), D-5 share revision and ShareDocumentBoundaryExtended audit entry, F-6, F4, F6, N-2, N-3. The same three reviewer agents (Claude Code subagents, fresh context, read-only; not a human or external review) re-examined the corrected areas twice; their findings on 3d1406da (F-6 regression, accepted-copy selection, share-view cap) were corrected in 1de30596 and confirmed resolved. Pre-existing child-record transfer: conflicts with mandatory requirements and is exploitable for insider retention; reported with the minimum correction (WP-0 Section 10.5), not corrected. No push, merge, PR closure, Stage B, persistent change, capability activation or acceptance.

## Direct development checkpoint 8 — 2026-11-27 (child-record ownership correction and verification preparation, C-198)

Candidate coord/c10-m1-candidate-r2 at f04cdb5b (signed, clean; 68ef7fb6 correction, 2c93c8d8 Stage B preparation and flag fix, f04cdb5b record). CI-equivalent .NET gate 3,677 passed, 0 failed, 223 authorized skips, 0 rejected; strict specification validation; frontend lint, typecheck, build and 81 files / 665 tests; Stage B and security test projects build; Stage B group coverage 456 tests in 12 groups. Child records are never transferred on their own (owner and administrator paths); the reported retention sequence is refused; reviewer agent (Claude Code subagent, fresh context, read-only; not a human or external review) confirmed the Authority correction and the corrected transfer flag. Limitation reported: pre-correction direct owner edges on children still confer ownership; minimum remediation needs an owner decision (WP-0 Section 10.6). Focused Stage B prepared (three classes, one Playwright spec, development step-up on 82 existing calls), not executed. Governance amendment A-24 prepared with exact changes, not applied; live and checked-in policies unchanged. No push, merge, PR closure, Stage B, live protection change, persistent change, capability activation or acceptance.

## Direct development checkpoint 9 — 2026-11-27 (governance, consolidation and focused Stage B, C-199/C-200)

Candidate `coord/c10-m1-candidate-r2`, pushed as `c10/consolidated` (PR #88, not merged). Records commit follows the last code commit `9403ace6` (signed).
- **Governance:** A-24 integrated as PR #86 (squash `e5a9f0fc`). The live ruleset matches the checked-in one. The verifier reports 17 PASS, 0 FAIL, 7 DEFERRED.
- **Consolidation:** #79, #80, #77 (with #82) and #78 integrated in that order. #81–#85 closed after preservation in signed `archive/*` tags (WP-0 §10.10). The signed merge of `main` is `4785eab5`.
- **Inventory:** 0 children with a direct owner edge, in any store.
- **Focused Stage B (C-200, local fresh `enterpriseforms-sectest`):** every selected group, class and spec passes at the final heads:
  - records-lifecycle C10 classes 9/9 and 2/2;
  - existing records-lifecycle classes 68/68 at `e6779ca2`, with the upload-dependent classes retested 12/12 and 11/11 at `9403ace6`;
  - projection-integrity-restore 14/14 (fresh stack);
  - Document 3/3, DocumentEncryptionAudit 5/5, ParentChild 2/2, FieldSearchExecution 14/14, IndexedSearchQueryStack 21/21;
  - Playwright c10-sharing-pages 6/6 (axe) and contributor-writes 4/4.
- **Stage B product defects:** four found and corrected, plus CodeQL alert 8.
- **CI-equivalent gate:** .NET 3,677 passed, 0 failed, 223 authorized skips, 0 rejected; frontend lint, typecheck, build and 81 files / 665 tests; strict specification validation; OpenAPI unchanged.
- **Not proven or not implemented:** real-loop expiry convergence; CONS-23 long-download re-check (mandatory; minimum correction reported).
- **Not done:** merge, formal acceptance, production change, capability activation outside the disposable stack, C11.

**Next permitted step:** owner review of PR #88 and the WP-0 §10.10 report, then the owner's integration decision.

## Direct development checkpoint 10 — 2026-11-27/28 (CONS-23 and real-elapsed-time expiry, C-201)

Candidate `coord/c10-m1-candidate-r2`, pushed to `c10/consolidated` (PR #88, not merged). The records commit follows the last test commit `6a824791` (signed).
- **CONS-23 (mandatory):** implemented on the document download path: bounded slices, an Authority and session re-check at most every 5 s, fail-closed audited stops, and no proxy read-ahead. The exposure window is documented (WP-0 §10.11). Reviewed by a Claude Code subagent (fresh context, read-only; not a human or external review); findings corrected and confirmed resolved.
- **Product defect found and corrected:** genuine share expiries not aligned to the microsecond never converged (precision mismatch in `ExpireShareAdmission`).
- **Real-elapsed-time expiry (no Testing clock):** §4 tests 8 and 10 pass. Test 13 asserts the denial and the detected rollback (`PROJECTION_MARKER_AHEAD`); "replay converges it" is UNMET, blocked on G14.
- **Targeted Stage B (local fresh `enterpriseforms-sectest`):**
  - C10DownloadRecheckTests 5/5;
  - group share-expiry-convergence 4/4 (`20261128-014838-1c42c802`);
  - regressions DocumentTests 3/3, DocumentEncryptionAuditTests 5/5, C10ShareLifecycleTests 9/9, SharingTests 2/2.
- **CI-equivalent gate (`6a824791`):** .NET 3,918 total, 3,695 passed, 0 failed, 223 authorized skips; frontend lint, typecheck, build and 81 files / 665 tests (no frontend change since); strict specification validation; OpenAPI unchanged.
- **Still open:** sign-out during a download end to end; a download through the web nginx; Azure ingress buffering; PB-09 re-check load and gate-closed interruption rate; test 13 replay (G14); every §10.9 obligation.
- **Not done:** merge, formal acceptance, production change, capability activation outside the disposable stack, C11.

**Next permitted step:** owner review of PR #88 and WP-0 §10.11, then the owner's integration decision.
