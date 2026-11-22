# Phase C8 M4 (AC-8.1–AC-8.5): consolidated reviewer package

- **Status:** **Prepared for submission to an independent security reviewer (C-174). NOT REVIEWED.** Every section below is evidence prepared by the implementation agent (Claude) for an owner-led review and for a later independent assessment. It contains no finding, approval, attestation or certification.
- **Independence notice:** neither Claude nor the project owner is an independent reviewer (`docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`, rules 1–4). The owner's acceptances below are owner-led decisions (kinds 1 and 3), not independent assessment (kind 2) or production approval (kind 4).
- **Relationship to the earlier package:** AC-8.1's S1 (projection integrity) and M1 (sealed auditor notes) have their own package, [PHASE_C8_M4_1_S1_M1_INDEPENDENT_REVIEW_PACKAGE.md](PHASE_C8_M4_1_S1_M1_INDEPENDENT_REVIEW_PACKAGE.md). It is not repeated here; this package adds AC-8.2–AC-8.5 and the M4 pages, and indexes the whole of AC-8. AC-8 joins the AC-5/AC-6/AC-7/AC-9 independent Authority review package (`PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §5).
- **Evidence level:** Development/Testing on disposable local Docker stacks, plus hosted `ci` and CodeQL on the merged commits. **No hosted Stage B, `level3-script` or `security-gate-a` run on M4 code, no Azure staging evidence, no PB-09/PERF-01 measurement.**
- **Contract:** [PHASE_C8_M4_AC8_SECURITY_CONTRACT.md](PHASE_C8_M4_AC8_SECURITY_CONTRACT.md) revision 2 with amendments C-165 and C-169.

## 1. What is under review

| Item | Delivers | Code | Owner decision (owner-led) |
|---|---|---|---|
| AC-8.1 | Security Auditor role (`organization#security_auditor`, `view_audit`); appointment/removal with reason codes; administrator self-removal refused (D8-62a); S1; M1 | M4-1 (separate package) | C-111…C-119, C-162 |
| AC-8.3, PG-054, PG-055 | Audit-access evidence: `view_audit` check, then a revision-compared `AuditAccessAuthorized` entry committed in the Authority audit stream before any business audit row is served; redaction catalog; audit explorer and event detail | `3c645ac` (PR #60) | C-162…C-164 |
| AC-8.2 | Security-command timeline, read and served by the Authority inside the AC-8.3 transaction | `ce4789c` (PR #61; includes the C-168 CodeQL #4 remediation) | C-165…C-168 |
| AC-8.4, AC-8.5, PG-052 | Security overview (gate, commands, archive) and field-protection coverage under the Effective-edge rule; Security Home | `6142f91` (PR #62) | C-169…C-173 |
| M4-5 | `security-audit.spec.ts` (browser, axe, keyboard, widths, zoom), this package, sanitized evidence | branch `phase-c8/m4-5-accessibility-review` (uncommitted) | C-174 |

## 2. Reviewer independence requirement

As in the M4-1 package §2: the reviewer must not have contributed to the design, implementation, testing or documentation of this project, must be a qualified human security reviewer, and records findings in §9 and the attestation in §10. Neither may be filled by the owner or by an AI agent.

## 3. Assessment questions

The owner answered Q-1…Q-5 (C-109); the contract asks for independent confirmation (§10.3). Q-6…Q-11 are added by this package.

| # | Question | Where to look | Prepared evidence (not a conclusion) |
|---|---|---|---|
| Q-1 | Is the per-read organization-lock append of AC-8.3 acceptable (NOWAIT, bounded attempts, then 503), or should audit-access entries use a lock-free path? | contract §5.3; `AuditAccessService` | T21/T22 (Stage B `SecurityAuditAccessTests`, run `20261120-020202-71e3c0be`): a revocation committed under an audit-read flood within baseline + 2 s; F-M42-V1 (occasional `AUDIT_EVIDENCE_BUSY` from the archive processor's lock), accepted under option 1 |
| Q-2 | Is omitting business-reported result counts from Authority evidence right? | contract §5.3, AC-M4-2.3 | Unit tests: no count field in `AuditAccessAuthorized` |
| Q-3 | Is the Effective-edge rule a sound basis while the gate is closed, restricted to overview and coverage? | contract §5.4; `EffectiveEdgeRule.cs` | Q-6 |
| Q-4 | = D8-62a (administrator self-removal refused) | M4-1 package | M4-1 package |
| Q-5 | Is the AC-8.2 kind allowlist complete without disclosing record-scoped data? | contract §5.2 (C-165); `SecurityCommandTimelineCatalog` | Exhaustive classification and source-scan tests; T08 sentinel scan on the real stack (run `20261120-050354-ef37489e`) |
| Q-6 | Does the Effective-edge rule hold: enabled binding, an edge with no removing command, a creating command of the same organization in state `Effective`, all in one `REPEATABLE READ READ ONLY` AuthorityDb snapshot, with no SpiceDB call; used only by the overview and coverage services? | `EffectiveEdgeRule.cs`; `SecurityOverviewService.cs`; `SecurityCoverageService.cs`; `AuthorityStore.BeginReadOnlySnapshotScopeAsync` | Unit: `SecurityOverviewTests` (28; incl. guard test and one-snapshot assertion). Real stack T12 (run `20261120-210905-b5e56ad3`): pending administrator and auditor grants (CommittedAwaitingProjection, revisions 4, 5) conferred no section; a committed revoke (revision 6, not Effective) denied at once; Effective holders kept exactly their sections under the closed gate |
| Q-7 | Is the redaction catalog complete (D8-67/D8-67a): no free text, field keys or labels, option text, record scopes; unknown actions envelope-only? | contract §6.3; `AuditEventCatalog.cs` | Reflection and sentinel tests; `AuditExplorerTests` (run `20261120-020620-4eca3728`) |
| Q-8 | AC-8.5 (D8-66): is the coverage endpoint's authorization (system_administrator only, Effective-edge, else 404) and content (the §13 counts, Missing / StaleOrganizationWide / NoLaterOrganizationContentChange) acceptable? Is the organization-wide staleness comparison (OD-1) honestly presented? | contract §5.5 and C-169 amendment; `SecurityCoverageService.cs` | Unit coverage-state tests; real stack: Missing, then NoLater, then Stale with a genuine attestation (run `20261120-210905-b5e56ad3`); UI labels "complete when attested", never current verification |
| Q-9 | Is the T12 harness (C-171 P1) valid evidence? Pending states were produced by submitting genuine commands directly to the Authority's production command endpoint (business workload credential, the Security Administrator's own assertion, the business API's payloads) while the gate was closed, because the business API's own pre-check refuses every command then (T12-L1) | `SecurityHomeTests.T12…` | Status §43.7–§43.9; no database writes in the test class |
| Q-10 | Is the archive-gate behaviour during a receipt-verifier outage acceptable? With nothing pending, an unreachable verifier yields `AwaitingArchive` (closed), or 503 only if verification outlasts the business client timeout | `OrganizationGate`; `TrustedJournalReceipts` | Status §43.6 item 9; the gate-card wording now names both causes (C-171) |
| Q-11 | Do the M4 pages leak anything through the browser (navigation, direct routes, refusal states)? | `security-audit.spec.ts` | §5, M4-5 browser run |

## 4. Commits and how to reproduce

- Merged commits: `3c645ac` (M4-2), `ce4789c` (M4-3), `6142f91` (M4-4). M4-1: see its package.
- M4-5 (frontend accessibility and browser spec, not yet merged): `21c38de`, `3a7e4a2` (keyboard-focus remediation of the Audit Explorer and Security Command Timeline), `83d0dff` (test-only synchronization, frozen baseline C-176); integration branch `integration/c8-m4-5-into-main` (`docs/PHASE_C8_STATUS.md` §45).
- Tests without Docker: Authority unit tests (`SecurityOverviewTests`, `AuditAccessServiceTests`, `SecurityCommandTimelineTests`, …), business contract tests, architecture tests, Vitest.
- Stage B (disposable stack): `scripts/dev/stage-b.ps1 -Class <Class> -Fresh` for `SecurityAuditorTests`, `SecurityAuditAccessTests`, `AuditExplorerTests`, `SecurityCommandTimelineTests`, `SecurityHomeTests`.
- Browser (isolated stack only, guarded): `scripts/dev/test.ps1 -Level 2 -Spec security-audit`.

## 5. Evidence index

| Evidence | Where | Level |
|---|---|---|
| Stage B run summaries, M4-2…M4-4, every run including failed ones, with raw-file digests | [../evidence/phase-c8/m4-audit/STAGE_B_RUN_SUMMARIES.md](../evidence/phase-c8/m4-audit/STAGE_B_RUN_SUMMARIES.md) | Local, owner-authorized |
| Accepted Stage B runs | M4-2: `20261120-020202-71e3c0be` (7/7), `20261120-020620-4eca3728` (6/6); M4-3: `20261120-050354-ef37489e` (4/4); M4-4: `20261120-210905-b5e56ad3` (5/5) | Local |
| M4-1 Stage B and S1/M1 evidence | M4-1 package §5, §7; `docs/evidence/phase-c8/m4-1-*` | Local |
| Hosted `ci` and CodeQL on merged M4-4 | `ci` push run 44855369446 and CodeQL 44855369299 on `6142f91`: success | Hosted |
| Browser, axe, keyboard, widths, zoom (M4-5) | `src/frontend/e2e/security-audit.spec.ts`: 6/6 locally at C-174 (§44.2), before the focus defects found later and fixed in `3a7e4a2`; the 11-test spec on `83d0dff` passed in targeted hosted run `44988912226` (C-177; artifact `14741167172`), after the failed attempt `44972960809` (C-175, test defect, C-176); local results on the integration branch in §45.3; manual screen-reader review pending (PB14-M4) | Local; hosted, focused (C-131 §1) |
| Status and decisions | `docs/PHASE_C8_STATUS.md` §34–§45; `docs/decisions/PHASE_C_DECISION_LOG.md` C-108…C-177 | — |

## 6. Approved deviations (owner-approved; not independently confirmed)

- **M4-2:** D-M42-1 (no `actor.roleBadges`), D-M42-2 (additional `AUDIT_DELIVERY_FAILED` and `deliveryOutcome: NotFound`), D-M42-3 (per-organization burst setting, default 20), D-M42-4 (each retry repeats the `view_audit` check).
- **M4-3:** D-M43-1…D-M43-7 (status §42.5).
- **M4-4:** D-M44-1…D-M44-8 (status §43.5; D-M44-3 and D-M44-8 under the C-170 conditions; D-M44-8 superseded: no inserted authorization rows).

## 7. Known limitations and residual risks

- Contract §3.1 RR-1…RR-15 unchanged (business audit rows have no receipt; no business RLS; auditor tenure controlled by the audited; no audit-read step-up until PB-08; local archive is not immutable; …).
- F-M42-V1: occasional `AUDIT_EVIDENCE_BUSY` on sequential audit reads (accepted, option 1; frontend auto-retry deferred).
- T12-L1: pending states cannot be produced through the business API; T12 relies on the P1 direct-Authority harness (Q-9).
- Coverage staleness is organization-wide and conservative (OD-1); counts are as recorded by the latest attestation, not re-verified.
- RB-13 test-isolation defect and P-S1-1 benchmark: the full Stage B suite was not green at `f117995` (M4-1 package §7.1); unchanged.

## 8. Outstanding before closure or production (not claimed)

| Area | Outstanding |
|---|---|
| Hosted verification (AC-M4-5.1) | The seven Stage B shards plus the aggregate have not passed on M4-5 code: the only run, `44878220074` on `21c38de`, failed and predates the fixes. `level3-script` and `security-gate-a` passed on `21c38de` only, which is not evidence for `83d0dff` (C-177). The targeted hosted browser run (C-177) is focused verification, not Stage B. Each further run needs separate owner authorization. Unrun or failed shards are not reported as green |
| Accessibility | Manual NVDA/VoiceOver review of the M4 pages: pending (PB-14 checklist) |
| Performance (pre-production, PB-09/PERF-01) | M4-4 overview SQL aggregates at volume; C-162 record-time index and its 1M-row measurement |
| Independent review | This package, the M4-1 package and AC-5/AC-6/AC-7/AC-9: not performed |
| Production | Azure staging, PB-08 step-up, G08/G24, G14 recovery, SD-5, PB-12 release evidence |

## 9. Findings (for the independent reviewer only; blank)

| # | Question | Finding | Severity | Recommendation |
|---|---|---|---|---|
| | | | | |

## 10. Attestation (blank until delivered by a qualified independent reviewer)

Reviewer: ______________________ Organization: ______________________ Date: __________

Scope reviewed: ______________________________________________

Statement: ______________________________________________
