# Phase C8 — Administration and Governance: status

- **Branch:** `phase-c8/administration-governance`, from `main` `b1e1b30a78ebe36b8055ec52343390419dff9ba9` (Phase C7 squash merge, PR #29). Not merged; no PR.
- **Architecture and milestone plan:** `docs/architecture/PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md`.
- **Decisions:** C-77 (M0 plan), C-78 (owner approval of D8-1…D8-10 with conditions; M1 authorized), C-79 (M1 implementation).
- **State (2026-10-29):**
  * M0 (planning) delivered as documentation only (29a1226).
  * The owner approved D8-1…D8-10 as proposed, with the eight conditions recorded in C-78, and authorized **M1 only**.
  * **M1 implemented** (§3–§6) and **awaiting the owner's acceptance testing on the isolated stack**. No Authority command, relation, schema or database migration was changed; business schema unchanged (no new table or column).
  * M2 (AC-7), M3 and M4 (AC-8) are not started. AC-7 and AC-8 remain proposals that need separate architecture/security approval (C-78 condition 8).
- **Findings:** G19, G20, G21, G24, G25, G26, PB-08, PB-09, DR-C7-1…6, AUD-C7-01, the C-74 development incident remediation and production Level 2 are all **unchanged and open**. **F-C7-03 is corrected in M1** (logo upload/replacement audited, §3.4) and is proposed for closure on the owner's acceptance. The AC-5/AC-6 independent review is still required; AC-7/AC-8 would join it.

## 1. Baseline verified for M0

- `origin/main` = `b1e1b30` ("Phase C7: Advanced Field Search (M0–M4) (#29)"). Its tree is identical to the reviewed C7 head `9bda786`.
- Administration capabilities on `main` were inventoried from the code: Authority SpiceDB schema, security command kinds, operator plane, business endpoints, entities and frontend features. The results are in architecture §1.
- The decisive finding: after bootstrap the Authority can change only the Security Administrator role. Adding, disabling or re-roling members needs new Authority commands (proposed AC-7).

## 2. Owner approvals (C-78)

D8-1…D8-10 approved as proposed, with conditions: owner-role grants need two distinct existing owners (Authority-enforced); designer grants need one authorized owner; membership additions use the existing two-approval operator process in Development/Testing only until production controls exist; members cannot be disabled while directly owning records; disabling preserves roles and shares while blocking access immediately and revoking sessions; the auditor role is separate from Security Administrator and cannot access record content; C-74 cleanup must be verified before C8 images reach the development environment; AC-7 and AC-8 need separate approval before their Authority changes are implemented.

## 3. M1 — what was implemented (C-79)

### 3.1 Backend (feature-owned, `/api/v1`, additive OpenAPI only)

| Endpoint | Owner | Who | What |
|---|---|---|---|
| `GET /api/v1/organization/overview` | `Organizations` | owner or Security Administrator | PG-041 aggregate of independently computed parts (organization identity, member and Security Administrator counts, teams, data-protection floor, authentication status); an unavailable part is reported as such instead of failing the page |
| `GET /api/v1/organization/members` | `Identity` | owner or Security Administrator | PG-050 read-only people directory: business `users` projection (identities bound by the Authority that have signed in; the source is stated in the response), non-secret issuer/subject, team affiliations, and a **Security Administrator** badge taken from the Authority roster; owner/designer badges wait for AC-7 |
| `GET /api/v1/organization/teams` | `Teams` | owner or Security Administrator | PG-048 directory with member counts |
| `GET /api/v1/teams/{teamId}` | `Teams` | owner or Security Administrator | PG-049 detail: roster with the Authority command id that made each membership effective; `availableMembers` for the add picker (a Security Administrator is never offered themself, mirroring G25) |
| `PATCH /api/v1/teams/{teamId}` | `Teams` | owner or Security Administrator | D8-8 display-only rename of the projection label; concurrency on `expectedModifiedOn` (409 `TEAM_MODIFIED_CONCURRENTLY`); audited `TEAM_RENAMED` |
| `POST /api/v1/organization/branding/logo` (existing) | `Organizations` | owner | **F-C7-03:** now audited `ORGANIZATION_LOGO_UPLOADED` / `ORGANIZATION_LOGO_REPLACED` in the same transaction as the projection write (size and SHA-256 of the public logo bytes in the metadata) |

- Existing team commands (`POST /teams`, `POST /teams/{id}/members`, `…/remove`) are unchanged and still decided by the Authority.
- New shared port: `AdministrationAccess` (`Application/Common/Authority`) batch-checks `manage_configuration` and `administer_security` on every administrative read; neither ⇒ 403 `FORBIDDEN` with no counts or names.
- New store query `GetActiveTeamMembershipsAsync` (projection rows; confers nothing). New audit actions: `ORGANIZATION_LOGO_UPLOADED`, `ORGANIZATION_LOGO_REPLACED`, `TEAM_RENAMED`.
- Files: `Contracts/Features/PhaseC8Contracts.cs`; `Application/Common/Authority/AdministrationAccess.cs`; `Application/Features/Organizations/OrganizationOverviewHandler.cs`; `Application/Features/Identity/MemberDirectory/GetMemberDirectoryHandler.cs`; `Application/Features/Teams/ManageTeam/TeamDirectoryHandlers.cs`; `Application/Features/Organizations/BrandingHandlers.cs` (logo audit); `Api/Features/{Organizations,Identity,Teams}/*Endpoints.cs`; `Domain/Features/Audit/AuditEvent.cs`; `Application/Common/Persistence/IBusinessStore.cs`; `Infrastructure/Persistence/BusinessStore.cs`; `Infrastructure/BusinessInfrastructureRegistration.cs`; `contracts/openapi/enterprise-forms-v1.json` (regenerated).

### 3.2 Frontend

- **Administration shell:** `features/organizations/components/OrganizationLayout.tsx` — left-hand "Organization" navigation (collapses to a scrollable strip below `lg`), items filtered by the session's projected capabilities (navigation hints only; the server decides every request). A session with neither capability gets a generic message and no navigation. The primary "Organization" link now opens the overview.
- **Routes (TanStack, lazy feature entrypoints):** `/app/organization` (overview, `organizations`), `/app/organization/users` (People, `identity`), `/app/organization/teams` and `/app/organization/teams/$teamId` (`teams`, new feature folder), `/app/organization/branding` (existing, now inside the shell), `/app/organization/data-protection` (PG-046, the existing policy card moved; behaviour unchanged), `/app/organization/security-administrators` (the C-70 card moved; behaviour unchanged). `/app/organization/search-approvals` is unchanged and linked from the shell for Security Administrators.
- **Overview page:** six cards, each with its own refresh action; the data-protection and Security Administrators cards use their own existing queries. An unavailable part shows "Unavailable" and the reason, never a guessed value. Authentication is shown as *PasswordlessFederated / Not enforced — awaits PB-08*.
- **People page:** read-only table (name, verified identity, Authority roles, teams, first seen), client-side filter over the rows the server already authorized, no action column.
- **Teams pages:** directory with counts; create team (RegisterTeam); detail with roster, add member (picker from `availableMembers`), remove member, rename dialog. A 202 or non-effective command outcome is shown as **Pending** and the roster is re-read; nothing is shown as done before the Authority reports it effective.
- **Shared UI:** `ActionIcons.refresh` added to `packages/ui`. Generated client regenerated; new operations in `packages/api-client` and endpoint keys in `packages/work-queue` (no feature transport).
- Files: `apps/web/src/app/{router,AppShellLayout}.tsx`; `features/organizations/{index.ts,components/OrganizationLayout.tsx,components/OverviewCards.tsx,pages/OverviewPage.tsx,pages/DataProtectionPage.tsx,pages/SecurityAdministratorsPage.tsx,pages/BrandingPage.tsx}`; `features/identity/{index.ts,components/MemberTable.tsx,pages/PeopleDirectoryPage.tsx}`; `features/teams/{index.ts,components/TeamRoster.tsx,pages/TeamsPage.tsx,pages/TeamDetailPage.tsx}`; `packages/api-client/src/{index.ts,generated/enterprise-forms-v1.ts}`; `packages/work-queue/src/endpoints.ts`; `packages/ui/src/actionIcons.ts`.

### 3.3 Authorization and tenant isolation (as implemented)

- The organization always comes from the authenticated session; no route, body or query parameter names it.
- Every administrative read calls the Authority (`CheckPermissionsBatch`: `manage_configuration`, `administer_security`) before any lookup; a member, reader or designer-only user gets 403 without counts.
- Team commands are unchanged Authority commands. **The Authority's existing rule is organization owner or Security Administrator, with a Security Administrator never able to add themself (G25)**; the M0 document's statement that team commands need `manage_configuration` alone was inaccurate against the code and is corrected in the architecture document. M1 mirrors the Authority's rule in `canManage` and in the picker; the rename accepts the same principals.
- Another organization's team id returns the same generic 404 as an unknown id (same problem title, no name leaks); directories never span organizations.
- The people directory never uses email as a key and returns no record content; role badges come only from the Authority roster, and the response states when the roster was unavailable rather than showing "no roles".
- Business projection rows (`users`, `teams`, `team_memberships`) remain labels; a forged membership row without an Authority command id renders as "Unverified projection row" and confers nothing.

### 3.4 F-C7-03

Logo upload and replacement now emit `ORGANIZATION_LOGO_UPLOADED` / `ORGANIZATION_LOGO_REPLACED` (actor, branding revision, byte size, SHA-256 of the public PNG) in the same business transaction as the projection write; a refused upload (non-owner, non-PNG) writes no event. Verified on the real stack (`AdministrationTests.LogoUploadAndReplacementAreAudited`).

## 4. Tests and evidence (local, isolated security-test stack rebuilt from this tree)

The security-test stack was recreated (`down -v`, `up --build --wait`) from the M1 tree before any real-stack test; on this workstation it runs on a local subnet overlay because a stopped older stack holds the pinned 172.30.x subnets (test-harness `SECTEST_EXTRA_COMPOSE_FILE`; nothing in the repository changed for this). The development stack (`:8091`, `:5280`, `:5173`) was not touched.

| Suite | Result |
|---|---|
| .NET build (warnings as errors), OpenAPI regenerated and committed | passed |
| `EnterpriseForms.Api.ContractTests` (OpenAPI snapshot, endpoint ownership, existing unit/contract tests) | 306 passed, 0 failed |
| `EnterpriseForms.ArchitectureTests` (dependency direction, naming, ownership matrix) | 67 passed, 0 failed |
| `EnterpriseForms.StageBTests.Tests.AdministrationTests` (real stack: overview and people authorization and isolation, team directory/detail/commands/cross-tenant 404, rename rules and audit, logo audit) | 5 passed, 0 failed (`artifacts/test-results/c8-m1`) |
| Real-stack regression of the touched areas (`FormAccessTests`, `SecurityAdministratorTests`, `BrandingTests`) | see §4.1 |
| Frontend lint, typecheck, Vitest (new: `OverviewCards.test.tsx`, `MemberTable.test.tsx`, `TeamRoster.test.tsx`) | lint clean, typecheck clean, 227 tests passed (29 files) |
| Playwright on the isolated stack (`organization-administration.spec.ts` with axe WCAG 2.2 AA on every page; updated `security-administrators.spec.ts`; `branding.spec.ts`) | see §4.1 |

### 4.1 Real-stack regression and browser runs

| Run | Result |
|---|---|
| `FormAccessTests` + `SecurityAdministratorTests` + `BrandingTests` (real stack, `artifacts/test-results/c8-m1-regression`) | 11 passed, 0 failed |
| `organization-administration.spec.ts` (4 tests: owner shell navigation and per-card refresh; owner team create/add/remove/rename; Security Administrator reads, administers teams, never offered themself, no Branding; member without capabilities gets no navigation; axe WCAG 2.2 AA on each page) | 4 passed, 0 failed (26 s) |
| `security-administrators.spec.ts` (updated for the moved page) + `branding.spec.ts` | 3 passed, 0 failed |
| `pb14-accessibility.spec.ts` + `encryption.spec.ts` + `encryption-downgrade.spec.ts` (updated for the moved data-protection page) | 8 passed, 0 failed for `encryption` + `encryption-downgrade`; `pb14-accessibility` 3 passed, 0 failed once `accused-workflow.spec.ts` (2 passed) had created the Accused record it depends on (the first attempt failed only on that missing prerequisite of the freshly rebuilt stack, not on the moved page) |

Two selector corrections were needed in the new spec before it passed (an unnamed card region, and the dev-seed display-name suffix "(record user)" in the remove button and dialog names); no product change resulted from them.

- Hosted runs (`ci`, `stage-b-integration`, `level3-script`, `security-gate-a`) have **not** been dispatched for this commit; the owner dispatches them (no `gh` here, API dispatch refused by policy).
- Manual NVDA/VoiceOver review of the new pages is outstanding, as for every earlier page.

## 5. Limitations and outstanding findings

- **People directory is a projection:** it lists identities that have signed in at least once; a bound identity that never signed in is not listed, and membership state (enabled/disabled) and owner/designer roles are not shown. Both wait for the AC-7 roster read (M2). The page says so.
- **No member or role actions** (by design, M1). PG-051 (user detail) is M2.
- **Team lifecycle:** rename only (D8-8); deactivation and team managers deferred. The rename is a business-projection label change with a business audit event; the Authority holds no team name.
- **Overview counts** are projection counts (members) and Authority counts (Security Administrators); both sources are labelled on the card.
- **Authentication card** is status text only; nothing is enforced or verified against Entra (PB-08 unchanged).
- The e2e spec runs only on the isolated Testing stack (Playwright guard from C-74); nothing was run against the development organization. The C-74 remediation itself remains unexecuted and is a precondition (C-78 condition 7) before C8 images reach the development stack.
- All other findings listed at the top are unchanged.

## 6. Next step

Owner acceptance testing of M1 on the isolated stack (architecture §4 M1 acceptance). Afterwards, M2 (AC-7) still needs its separate architecture/security approval before any Authority change starts. Not merged to `main`.

## 7. M1 acceptance verification (2026-10-30, commit `b66d357`)

Evidence: `docs/evidence/phase-c8/m1-acceptance-verification.md`. Decision: C-80.

- **Passed (re-run by the verifier):** build with warnings as errors; contract tests 306/306; architecture tests 67/67; real-stack `AdministrationTests` 5/5; frontend lint, typecheck and Vitest 227/227.
- **Authorization / isolation (code and test review):** every administrative read calls the Authority (`manage_configuration` / `administer_security`) before any lookup; members, readers and designer-only users get 403 without counts; another organization's team id is the same 404 as an unknown id; the organization is taken from the session only. Covered by `AdministrationTests`.
- **Pending commands:** the Teams pages treat HTTP 202 or a non-effective command as "Pending" and re-read the roster; no optimistic update. Limit: no automated test forces a genuinely pending Authority command (component tests only), so the fail-closed pending path is verified by code review, not by fault injection.
- **Logo audit:** `ORGANIZATION_LOGO_UPLOADED` / `…_REPLACED` are written in the same transaction as the projection write; refused uploads write nothing (`LogoUploadAndReplacementAreAudited`).
- **Authority:** no Authority, SpiceDB schema or migration change since `b1e1b30`.
- **Not run / not verified:** hosted workflows (no runs exist for this SHA; not dispatched, see §4 note); Playwright and the 11-test real-stack regression were not re-run in this checkpoint (earlier results in §4.1 stand as reported).
- **Manual accessibility outstanding:** NVDA and VoiceOver on Overview, People, Teams, Team detail, Data Protection, Security Administrators, Branding (axe WCAG 2.2 AA is automated only), plus 320/768/1440 px and 200%/400% zoom review per PB-14.
- **Doc defect found:** the `AdministrationAccess` XML comment says team commands and renames "keep requiring `manage_configuration` alone", contradicting the implemented and documented owner-or-Security-Administrator rule. Comment-only; not changed here (code frozen at the verified SHA).
- **Findings status unchanged:** C-74 remediation remains unexecuted and is not marked resolved; G19–G21, G24–G26, PB-08, PB-09, DR-C7-1…6, AUD-C7-01 and production Level 2 remain open. F-C7-03 stays "proposed for closure on owner acceptance".
- **Owner decisions needed:** (1) dispatch the four hosted workflows (open a PR to `main`, or `workflow_dispatch` for `security-gate-a`) and accept or reject M1 on their outcome; (2) accept the pending-path evidence level or require a forced-pending integration test; (3) approve the comment-only fix; (4) close F-C7-03; (5) separately, schedule C-74 remediation before any C8 image reaches the development stack.

## 8. M1 acceptance remediation (2026-10-30, C-81)

Owner decisions applied to the verification report of §7. Evidence: `docs/evidence/phase-c8/m1-acceptance-verification.md` (§Remediation).

- **Pending Authority integration test (added):** `PendingAuthorityCommandTests` (real stack, deterministic: the mandatory audit archive is paused, the SEC-P12 mechanism, and always unpaused in `finally`). It proves the API never reports the membership command complete (no 201, `effective` false or HTTP 202 `COMMAND_PENDING`), the Authority gate is not Ready, the roster is not updated optimistically (read fails closed or lacks the member), the business projection stays empty even after the Authority recovers, and a retry with the same idempotency key resolves to the one committed command, now Effective, adding the member exactly once. A second test proves rejected commands (G25 self-add, unknown principal) never succeed and leave the roster unchanged.
- **UI Pending (added):** `organization-administration.spec.ts` pauses the archive on the isolated stack, adds a member through the UI and asserts the **Pending** status ("has not made it effective yet"), no roster row and no Effective state.
- **Finding C8-M1-01 (original observation; remediated in section 9):** the pending membership command is not written to the business projection by the Authority recovering; the projection gains the member only when the same request is retried with its idempotency key (the UI sends a new key per click). The Pending text "The roster shows it once the Authority reports it effective; refresh to check" is therefore **not accurate**: refreshing alone never shows it. The projection stays safe (confers nothing), but the wording and a retry/reconcile path need an owner decision (reword now; reconcile-from-Authority is a feature). Left unchanged pending approval.
- **Comment correction (done):** `AdministrationAccess` XML comment now states the owner-or-Security-Administrator rule; no implementation change.
- **Manual accessibility:** checklist prepared in `docs/testing/PHASE_C8_M1_MANUAL_ACCESSIBILITY_CHECKLIST.md`; automated and manual results are recorded separately; **no manual NVDA/VoiceOver result exists**.
- **Findings:** F-C7-03 stays open until formal M1 acceptance; C-74 remediation unexecuted; G19–G21, G24–G26, PB-08, PB-09, DR-C7-1…6, AUD-C7-01 and production Level 2 unchanged. No development deployment.
- **Hosted workflows: NOT RUN.** No `gh`/API credential here, so no draft PR was created and nothing was dispatched (PR number: none). Candidate code commit `9f7b31c`; the exact manual actions (draft PR link, `security-gate-a` dispatch, same-SHA check) are in the evidence file. No result is claimed.

## 9. Finding C8-M1-01 remediation (2026-10-30, C-82)

**Cause.** A team membership command that was committed but not yet Effective returned 200 `effective:false`. The business projection (the roster) is written only inside the request that observes the Authority's Effective state, and the UI generated a new idempotency key per click and never re-asked. When the Authority later recovered, nothing re-entered that code path, so the roster stayed empty until the original request was repeated with its original key.

**Smallest safe correction (no Authority, schema, OpenAPI or backend change).** The Authority already treats the idempotency key as the identity of one command (digest of kind, resource and payload plus the actor; 409 on any mismatch; scoped to the organization). Re-sending the same add/remove with the *same key* therefore re-checks the one committed command and, only once the Authority reports it Effective, runs the existing idempotent projection step. The correction is client-side:
- the UI creates the key once per administrator action, records the operation (team, person, direction, **original key**, no secret) in tab session storage *before* sending, and re-checks it automatically (2 s growing to 10 s, 40 attempts, then a manual "Check now") with that key, never a new one;
- outcomes are distinguished: **Pending** (Authority not Effective, or an ambiguous transport/gate failure: treated as unknown, never success), **Effective** (Authority-confirmed, then the roster is re-read), **Rejected** (422/403/404/409, e.g. G25 self-add, idempotency mismatch), **Failed** (`PROJECTION_DIVERGENCE`, needs an operator);
- the roster is never updated optimistically; the projection row exists only after an Effective response; the unique active-membership index (`uq_team_memberships_active`) already forbids duplicates.
`api-client` `addMember`/`removeMember` accept an optional key; files: `features/teams/components/membershipOutcome.ts(.test.ts)`, `TeamDetailPage.tsx`, `packages/api-client/src/index.ts`.

**Tests.** Real stack (`PendingAuthorityCommandTests`, 6/6; `AdministrationTests` 5/5): pending to Effective after recovery with the same key and no new key; roster reflects it only then; rejected commands (G25, unknown user) create nothing; repeated and 6-way concurrent same-key checks leave exactly one membership and one projection row with no server error; a pending removal keeps the member until confirmed; another organization, another actor, or the same key with a different person cannot reconcile or hijack (and create nothing); an API + Authority restart while the command is pending loses nothing and duplicates nothing. Playwright (`organization-administration.spec.ts`, 5/5): Pending shown with the original key kept in session storage, then the page reconciles itself to **Effective** with the member shown once and the stored operation cleared, without any new click. Vitest 231/231 (outcome classification, persistence, back-off).

**Limitations (not fixed; need separate approval).**
- Reconciliation is driven by the open browser tab. If the tab is closed or its session storage is lost before the Authority becomes Effective, the membership is Effective in the Authority (and confers access there) but the *display* roster stays without the row until the administrator repeats the action. A server-side durable reconciler needs a persisted business-side intent record (new table, RLS, grants and integrity coverage) and is therefore not done here.
- While the organization's Authority gate is closed the whole application fails closed at bootstrap ("The application cannot start"); a reload during that window cannot show Pending. Reload survival is covered by unit tests, not the browser test.
- Playwright pauses the archive container by name (`E2E_ARCHIVE_CONTAINER`, default `enterpriseforms-sectest-archive-store-1`).

Status: remediation implemented and verified locally; C8-M1-01 is **proposed for closure on the owner's acceptance**. Hosted workflows still not run (see section 8 and the evidence file).

## 10. C8-M1-01 durable server-side reconciler: architecture proposal (2026-10-30, C-83)

The owner approved **architecture planning only** for a server-side durable reconciler on candidate `160efdb`. The proposal is in `docs/architecture/PHASE_C8_M1_01_DURABLE_COMMAND_RECONCILIATION.md`, summarized in the architecture document §10.

- **Decisive constraint (verified in code):** `SecurityCommandService.GetStatusAsync` answers only the originating actor (any other binding gets a generic 404). No workload-only status read or Authority team-roster read exists. A background worker therefore cannot confirm Effective without impersonation, so "independent of browser and actor" and "no Authority change" cannot both be met.
- **Recommended design R:**
  * a durable `authority_command_operations` row written before the submit;
  * a leased Worker reconciler over a proposed **AC-9** read-only outcome lookup, keyed by organization and idempotency key, under a separate Worker-only credential;
  * one shared projector that writes the roster only on Authority-confirmed Effective, is idempotent, serializes per team and is revision-guarded;
  * no submit capability in the reconciler, and no re-submission after not-found.
- **Fallback F:** no Authority change; reconciliation runs only in the initiating administrator's own later requests. It stays Pending if they never return or are disabled.
- **New observations:**
  * **C8-M1-02 (proposed):** a stale Effective add observed after a later remove resurrects the member in the display roster at `160efdb`. Reachable through a duplicated tab or a direct same-key retry; display-only.
  * **RO-2 (proposed):** the same observe-and-project pattern exists in 11 other handlers.
- **Deliverables in the proposal:**
  * architecture and sequence diagrams;
  * table and indexes (proposal SQL);
  * state machine;
  * transaction boundaries and failure catalogue;
  * threats T1–T16 with residual risks;
  * real-stack tests RC-01…RC-20, including browser closure, API, Worker and Authority restarts, crash windows, revision guard, tampering and cross-organization cases;
  * rollout and rollback;
  * decisions D8-11…D8-19.
- **Not done (by instruction):** no reconciler code; no migration or database change; no Authority, SpiceDB or OpenAPI change; no UI change; no deployment; no merge; M2 not started.
- **Findings:** C8-M1-01 stays **open, mitigated by C-82** (browser-driven) until D8-11…D8-19 are decided and the chosen design passes its tests. F-C7-03, C-74 remediation (unexecuted), G08, G19–G21, G24–G26, PB-08, PB-09, DR-C7-1…6, AUD-C7-01 and production Level 2 are unchanged. Hosted workflows have still not run.

**Next step:** the owner decides D8-11…D8-19, in particular R versus F and whether AC-9 may enter the independent Authority review package. Implementation starts only after those decisions are recorded.

## 11. AC-9 architecture approval and independent review package (2026-10-30, C-84)

The owner approved **Design R** as the direction for C8-M1-01, **subject to independent security review of AC-9**, and recorded D8-11…D8-19 (architecture §10). The review package is `docs/architecture/PHASE_C8_AC9_SECURITY_REVIEW_PACKAGE.md`.

- **Ordering (C8-M1-02) resolved from existing Authority facts (verified in code):**
  * every command's `CommittedAuthorizationRevision` is assigned at commit under the organization lock and is unique and strictly increasing per organization;
  * `ProjectionProcessor` makes commands Effective strictly in revision order;
  * desired-state edges record their creating and removing commands.

  AC-9 therefore returns the **effective membership of the command's own (team, member) pair at the applied revision P**. The reconciler projects that value under a monotonic per-pair watermark. Arrival time, Worker time and local timestamps are never used. Add and Remove processed in either order converge on the Authority's state. No new ordering guarantee is required.
- **Refinements for owner approval (Q-O2):**
  * R-1: project the Authority pair state, not the operation's own effect;
  * R-2: new watermark table `team_membership_projection_states`;
  * R-3: the Worker is the single projection writer;
  * R-4: the browser polls, never re-POSTs.

  Correction R-5: RO-2 covers **12** handlers (`CreateTeam` plus 11 other handler files), not 11.
- **AC-9 contract:**
  * read-only `POST /internal/v1/security-command-outcomes/lookup`, scoped to (organization, idempotency key, allowlisted kind);
  * its own Worker-only workload identity and policy;
  * delegated user assertions refused;
  * uniform `found:false`;
  * `membership` null when rejected or quarantined;
  * new framework rate limiter;
  * structured audit;
  * reconciler-side invariant validation that fails closed.
- **Findings:**
  * F-1: Stage A/local Authority responses are authenticated only by the channel (HTTP inside Docker), as for every Authority call; production mTLS is required;
  * F-2 (C8-M1-02) and F-3 (RO-2) are registered;
  * F-4…F-8 are residual risks.
- **Tests:** RC-01…RC-19 mapped; RC-20 (Design F only) proposed for retirement; RC-21…RC-28 proposed additions (Q-O3).
- **Not done (by instruction):** no AC-9, Worker, migration or projection implementation; no deployment; no merge; M2 not started. **C8-M1-01 remains OPEN.** All other findings unchanged; hosted workflows still not run.

**Next step:** independent security review of the package (Q-1…Q-9). Owner answers to Q-O1…Q-O4. Implementation only after both are recorded.

## 12. Owner answers Q-O1…Q-O4 and final independent-review submission (2026-10-30, C-85)

**Owner decisions (approved with conditions):**
- D8-20: only a validated AC-9 response from the current attempt is Effective evidence;
- D8-21: Authority pair-state projection, `team_membership_projection_states` and a single logical Worker writer, with the roster and the high-water mark updated atomically;
- D8-22: closure set RC-01…RC-19 and RC-21…RC-28; RC-20 retired;
- D8-23: RO-2 = 12 handlers.

**Submission:** `docs/architecture/PHASE_C8_AC9_SECURITY_REVIEW_PACKAGE.md`, rewritten as the final submission.
- **Self-found correction R-6:** C-84's rule treated every answer older than the watermark as tampering, which would have raised false alarms when two Workers race. A stale answer is now discarded and looked up once more; only a fresh answer below the watermark is an anomaly. RC-11 and RC-23 cover both branches.
- **Guards C-1…C-3:**
  * C-1: outside Development/Testing the reconciler refuses to start without HTTPS and a client certificate;
  * C-2: the reconciler credential must have its own key and issuer;
  * C-3: the idempotency key is never logged.
- **Verified facts that the reviewer needs:**
  * Stage A workload credentials are HS256 shared secrets, so separation comes from per-scheme keys, not claims;
  * mTLS is not implemented anywhere, and every runnable environment uses plain HTTP to the Authority;
  * the Authority as built refuses to start outside Development/Testing, because Entra is not implemented. Azure staging and production therefore do not exist yet.
- **New findings:**
  * F-9: the Worker already holds the business-API and verifier credentials, so AC-9's isolation is at the Authority policy level, not the process level;
  * F-10: API and Worker share one BusinessDb role, so single-writer is enforced by code and test, not by the database;
  * F-11: R-6;
  * F-12: the security-test stack exposes the Authority on host loopback and commits test-only keys.
- **Reviewer questions:** Q-1…Q-9, plus new Q-10 (own container for the reconciler?) and Q-11 (separate BusinessDb reconciler role?). Each has its evidence and a proposed answer, explicitly marked as not independent.
- **Per-environment guarantees (§10):** C8-M1-01 closure evidence from the security-test stack and hosted CI can prove AC-9's logic, not its transport or identity guarantees. Those stay with the Azure-staging gate.
- **Independence:** this submission was prepared by the implementation agent on the owner's instruction. **No independent review has taken place.** §11 states who qualifies and what must be recorded.

**Not done (by instruction):** no AC-9, Authority, Worker, migration or projection change; no deployment; no merge; M2 not started. **C8-M1-01 remains OPEN.**

**Remaining before implementation authorization:**
1. the independent review recorded per §11, with decisions on Q-1…Q-11;
2. owner decisions on reviewer conditions (notably Q-10, Q-11, and any signed-outcome request under Q-1);
3. owner authorization naming the implementation scope (package §12).

## 13. AC-9, C8-M1-01 and C8-M1-02 implemented (2026-10-30, C-87) — not reviewed, not merged

- **Owner instruction:** implement all three technically (no closure by waiver); do not claim independent approval; do not merge.
- **Branch:** `phase-c8/ac9-durable-reconciliation` from `69a6717` (PR #31 head, untouched). Commits `a75827a`, `c600288`, `bc8fe05` and the documentation commit.
- **What exists now:**
  * **AC-9** in the Authority: read-only outcome lookup under its own reconciler credential (package §1–§2, C-1…C-3).
  * **C8-M1-01:** durable `authority_command_operations`; a Worker-only reconciler completes every add/remove from a validated AC-9 answer, independent of the browser, API restarts and Authority outages; the API never writes the roster; the team page sends once and only reads status.
  * **C8-M1-02:** `team_membership_projection_states` per-pair watermark; roster, watermark and completion in one transaction; stale answers re-looked-up once (R-6); a watermark ahead of the Authority is an alerted anomaly.
- **Evidence:** `docs/evidence/phase-c8/ac9-reconciliation-verification.md` — RC suite 31/31 on the security-test stack, regression 35/35, unit/contract/architecture suites green; one product defect (V-1) found and fixed during verification.
- **How Q-1…Q-11 are handled in code:** `docs/architecture/PHASE_C8_AC9_IMPLEMENTATION_PLAN.md` §4. Q-1 signed outcome, Q-10 own container and Q-11 separate database role are **not implemented** (approvals outstanding).
- **Independence:** all of this was built and verified by the implementation agent. **No independent review has taken place.**

**State: implemented, NOT resolved.** AC-9, C8-M1-01 and C8-M1-02 stay open until:
1. the independent security review is recorded per package §11 (reviewer reviews the implementation commit);
2. the owner decides any reviewer conditions (notably Q-1, Q-10, Q-11);
3. hosted CI and the full Stage B suite pass on the final commit (D8-19);
4. the owner authorizes the merge.

## 14. M2 user management — part available within the current Authority (2026-10-30, C-93) — not merged

Branch `phase-c8/m2-user-management`, from `feature/form-rename` (which is on the owner-accepted C-91 baseline).
**No Authority change.** The remaining M2 scope (add user, owner/designer roles, disable/enable, roster read) is the
AC-7 security contract `docs/architecture/PHASE_C8_M2_AC7_SECURITY_CONTRACT.md`, awaiting decisions D8-24…D8-32.

### 14.1 Delivered

| Item | Detail |
|---|---|
| People directory API | `GET /api/v1/organization/members?q&role&signInStatus&pageSize&continuationToken`: server pages (default 15) with opaque cursors bound to caller, organization and filters; search over name, identity and team; filters role (`SecurityAdministrator`/`None`) and sign-in (`SignedIn`/`NotRecorded`/`NeverSignedIn`). Authorization unchanged (owner or Security Administrator, decided by the Authority before any read). |
| All members | For **Security Administrators** the directory adds every enabled Authority binding returned by the **existing** Security Administrator roster read (appointment candidates), so people who have never signed in are listed (name not known yet). Organization owners see people known to the business directory plus the Security Administrators; the page states this coverage. |
| Last sign-in | New business column `users.last_signed_in_on` (migration `20261030225401_UserLastSignIn`, additive, display only), stamped at sign-in (`EnsureIdentityProjectionHandler.RecordSignInAsync`; today the Development/Testing persona login — the future PB-08 OIDC callback must call it too). |
| Roles and actions | Security Administrator badges from the Authority roster. For Security Administrators each row carries action hints mirroring the existing C-70 rules (`AppointSecurityAdministrator` for enabled non-holders, `RemoveSecurityAdministrator` unless it is the last); the People page runs them through the existing `appoint`/`remove` endpoints with reason and the development-only step-up. |
| Not offered | **Add user** is shown disabled, described by an on-page explanation; no owner/designer role or disable controls are rendered (AC-7). |
| UI | People page uses the approved standard: 40px inputs, 8px controls, 12px cards, search + filters, 15-row `Pager`, success message, dialogs with locked state. Accessible names of row actions start with their visible text (axe `label-content-name-mismatch` found and fixed). |

### 14.2 Tests (executed 2026-10-30, isolated security-test stack rebuilt from this branch)

| Suite | Result |
|---|---|
| .NET contract tests (incl. 7 new `MemberDirectoryTests`) | 468 / 468 |
| Architecture tests (naming, boundaries, ownership, signed-column registry) | 71 / 71 |
| Stage B: new `PeopleDirectoryTests` + `AdministrationTests`, `SecurityAdministratorTests`, `FormRenameTests` | 13 / 13 |
| Vitest (incl. updated `MemberTable.test.tsx`, new `PeopleDirectoryPage.test.tsx`) | 268 / 268 |
| ESLint, TypeScript, production build | clean |
| Playwright: `organization-administration.spec.ts` (5, owner and Security Administrator People flows with axe), `security-administrators`, `dashboard`, `ui-consistency`, `responsive` (320 px) | 14 / 14 |

`PeopleDirectoryTests` proves on the real stack: the Security Administrator sees 6 people including a bound member who
never called the API (empty name, `NeverSignedIn`, appoint hint) and has no removal hint on themself as the last
administrator; the owner sees 5 and no hints; clerk, reader and designer-only get 403 without counts; 4-row pages do not
overlap; a cursor used by another caller or with other filters is refused; search by subject and the role and sign-in
filters work; an unknown role filter is 422; another organization's Security Administrator sees none of these people.

Not run: the complete Stage B and SEC-P suites; hosted CI.

### 14.3 Limitations

- Owners do not see members who never signed in, nor anyone's owner/designer role, until AC-7.3.
- "Sign-in not recorded" covers people seen before the column existed and development personas projected without logging in.
- Last sign-in is a business-database display value (G21 family residual), not evidence.

## 15. AC-7 implemented for Development/Testing (2026-10-31, C-94 approval, C-95) — not merged, not independently reviewed

Owner approved D8-24…D8-32 and SD-6 (C-94). Implemented on `phase-c8/m2-user-management` on top of §14 (same branch).
The approved Dashboard, charts, security monitoring, unified styling, form rename and the §14 People features are preserved.

### 15.1 Authorization Authority

| Item | Detail |
|---|---|
| Commands | `GrantOrganizationRole` / `RevokeOrganizationRole` (`organization_owner`, `designer`), `ProposeOrganizationOwnerChange` (GrantOwner, RevokeOwner, DisableOwner, EnableOwner), `DisableIdentityBinding` / `EnableIdentityBinding`, `RequestOrganizationMembership`. All through the existing journal → desired state → outbox → SpiceDB projection → Effective gate, with idempotency key + canonical digest, expected-revision CAS, the organization row lock, and refusals journaled on the audit stream (`MembershipCommandRejected`). |
| Rules | Actor must be an enabled `organization_owner` (`administer_security` and `designer` never qualify); no self-change; step-up through the existing PB-08 gate (synthetic in Development/Testing, refused in staging/production); designer changes by one owner; owner grant/revoke and owner disable/enable by two distinct owners via a single-use proposal (24 h) whose proposer must still be an enabled owner when it is executed and which the target can never approve; last enabled owner and last enabled Security Administrator protected; disabling refused while the person directly owns active records (`OWNS_RECORDS`, count only); a disabled member cannot receive roles. |
| Disable / enable | One transaction: binding disabled, lifecycle columns stamped, every unrevoked Authority session of that binding revoked (`MEMBERSHIP_DISABLED`). Edges (roles, teams, shares) are kept and unusable because `ActorResolver` and session validation already refuse disabled bindings. Re-enable restores them; revoked sessions stay revoked. |
| Membership requests | `RequestOrganizationMembership` records a request only (identity of the organization's issuer, immutable subject, display hint, optional designer role, 7-day expiry, Authority user id allocated up front). Operator plane `GET /internal/v1/operator/organizations/{id}/membership-requests`, `POST …/{requestId}/approve` (dry run, then exactly two ES256 approvals from distinct registered approvers over the statement; Development/Testing only) and `POST …/{requestId}/reject` (one operator; grants nothing). Approval re-checks inside the organization lock: still pending, unexpired (an expired one is recorded `Expired`), requester still an enabled owner, identity not bound, issuer still the organization's. Creates the binding (and designer edge) as one journaled command; the request id is the single-use operation id (replay returns the recorded outcome). |
| Roster read | `POST /internal/v1/organizations/roster` (AC-7.3): every binding with enabled state and roles, proposals and requests of the last 30 days with computed state, flags for command/operator availability, the organization identity issuer. Owners and Security Administrators only; anyone else 404. Behind the organization gate. |
| Database | AuthorityDb migration `MembershipAdministration`: `identity_bindings` + `identity_disabled_on`, `identity_disabled_by_security_command_id`, `identity_enabled_by_security_command_id`, `organization_membership_request_id`, check `ck_identity_binding_disabled_state`; new `organization_role_change_proposals` and `organization_membership_requests` (multiword snake_case, FKs to the commands, state/expiry/self-proposal checks, one pending request per identity); both added to `AuthorityRuntimeGrants`. No business schema change in this step. |

### 15.2 Business API and UI

- `GET /api/v1/organization/members` now reads the Authority roster: owners and Security Administrators see every member (enabled or disabled, including never signed in), roles and membership state; new filters `role` (OrganizationOwner, Designer, SecurityAdministrator, None) and `membershipState`; owner and Security Administrator action hints; pending proposals and membership requests. Fallback when the roster is unavailable: names only, no roles, no actions.
- Commands (`Idempotency-Key`, reason, development step-up): `POST …/members/{id}/roles`, `…/roles/{roleName}/remove` (Designer), `…/owner-proposals`, `…/disable`, `…/enable`, `POST /api/v1/organization/owner-proposals/{id}/approve`, `POST /api/v1/organization/membership-requests` (issuer set by the server from the Authority). Business audit: one event per effective command (a retry records nothing new).
- People page: Add user (request dialog: issuer read-only, object id, name, initial role, reason, step-up, "Membership is not sign-in"); row actions (Make designer, Remove designer role, Propose as owner / owner removal / disabling / re-enabling, Disable / Re-enable membership, Security Administrator actions); "Owner proposals and membership requests" panel with Approve for a second owner; Membership filter and column. Existing 15-row searchable pagination, 8px controls, dialogs and notices unchanged in style.
- Development tooling: `python scripts/devstack/devstack.py membership list | approve --request-id ID --justification "…" [--execute] | reject --request-id ID --reason "…"` (synthetic approver keys; not two-human evidence).

### 15.3 Implementation choices the owner should know

1. **Re-enabling an owner also needs two owners** (symmetric with disabling, D8-28); the contract only named disabling.
2. **"Configured Entra tenant" (D8-30)** is enforced as the single identity issuer of the organization's enabled owners, which the operator plane bound at bootstrap; for Entra issuers the subject must be the object id (GUID). Email-like subjects are always refused. There is no separate tenant setting yet; real Entra validation arrives with PB-08.
3. **With exactly two owners, neither can be removed or disabled**: the proposer and the executor must both differ from the person, so a third owner is needed. The last-owner rule is enforced in addition.
4. Approving a proposal re-reads it from the Authority and sends the matching command; the Authority decides. Concurrent approvals of one proposal produce exactly one change (the organization lock serializes them).
5. Business `users.is_active` is not changed by disabling (the Authority decides; pickers may still list a disabled person, and the Authority refuses them).

### 15.4 Tests (executed 2026-10-31; isolated security-test stack rebuilt from this branch, Authority included)

| Suite | Result |
|---|---|
| Authority unit tests (incl. 21 new `MembershipAdministrationTests`) | 231 / 231 |
| Business contract tests (incl. rewritten `MemberDirectoryTests`) | 475 / 475 |
| Architecture tests | 71 / 71 |
| Stage B real stack: new `MembershipAdministrationTests` (3) + `PeopleDirectoryTests`, `AdministrationTests`, `SecurityAdministratorTests`, `SecurityAdministratorProvisioningTests`, `SessionRevocationTests`, `FormRenameTests` | 28 / 28 |
| Vitest | 270 / 270 |
| ESLint, TypeScript, production build | clean |
| Playwright (isolated): new `people-administration.spec.ts` (3, axe), `organization-administration` (5), `security-administrators`, `dashboard`, `ui-consistency`, `form-rename`, `responsive` (320 px) | 20 / 20 |

Real-stack proofs include: clerk, Security Administrator, designer-only and another organization's owner refused; self-change and missing step-up refused and journaled; a granted designer can create forms and a revoked one cannot; a disabled member's next request is refused, roles are kept, re-enabling restores access; disabling a record owner refused with a count; the last Security Administrator protected; owner grant needs another owner, the proposer and the target cannot approve, a consumed proposal cannot be reused, two simultaneous approvals produce one change, a disabled owner is refused; a membership request grants nothing, email subjects and duplicates refused, one operator approval refused, two create the member once (replay reports replayed), the new member shows as never signed in with the requested name and designer role, a rejected request cannot be approved; the business audit sequence is exact. In the browser, the disable attempt on a record-owning persona showed the `OWNS_RECORDS` refusal with its count (every persona of the shared test organization owns records or is its last Security Administrator, so the full disable/re-enable path is proven in Stage B).

Not run: complete Stage B and SEC-P suites, hosted CI, human screen-reader review, real Entra.

### 15.5 Remaining restrictions

- AC-7 is unavailable in staging and production until PB-08 step-up and the required assurance exist (D8-31): the Authority's step-up gate refuses every AC-7 command there; operator approval is Development/Testing only until production PIM.
- Membership is not sign-in: no Entra OIDC sign-in exists yet; an added real identity cannot sign in.
- AC-7 is not independently reviewed (joins the AC-5/AC-6/AC-9 package); G25/G26, PB-08, DR (G14 replay of the new lifecycle columns has no executed test) and production Level 2 are unchanged.
- FR-OBS-1 (Form Designer sign-out with unsaved changes) remains open.

## 16. People profiles, live presence and paged approvals (2026-10-31) — Development/Testing, not merged, not independently reviewed

Owner request after visual review of the People page. **No new Authority command, permission or Authority schema change.** Everything below is business-side (BusinessDb migration `UserProfilesAndPresence`, business API, web). Profile edits and presence are display data: neither grants, removes or changes any permission, membership, session or role.

### 16.1 People table

Columns: Person (name, verified identity beneath it, "(you)"), Roles, Membership, **Presence**, Teams, Actions. The Sign-in and Last sign-in columns were removed from the table (the API still returns them and the Sign-in filter is kept). Membership (Active/Disabled, from the Authority) and presence (live activity) are separate columns and never share a badge.

### 16.2 Profiles

| Item | Detail |
|---|---|
| Storage | Existing `users` row. New columns: `preferred_name`, `job_title`, `identity_display_name`, `is_display_name_overridden`, `profile_revision`, `profile_modified_on`, `profile_modified_by_user_id`. |
| Editable | Display name, preferred name, job title. **Never** issuer, subject, tenant binding, Authority membership or roles (the request has no such field); no email field exists. A display name that looks like an email address is refused. |
| Entra-sourced name policy | `identity_display_name` records what the identity provider last asserted at every sign-in. `display_name` (what the application shows everywhere) follows it **unless** a profile edit overrode it; sign-in never overwrites an override. "Use the name from the identity provider" returns to it. This is the explicit supported policy for the one identity attribute that is editable. |
| Who | A person edits their own (My profile, account menu). An organization owner (the Authority's ManageOrganization decision, the same authority as membership) edits anyone's in the same organization from People → Actions → Edit profile. Another organization's user is simply not found. The action is not offered on one's own row (My profile covers it). A person who has never signed in has no profile row and cannot be edited. |
| Concurrency and audit | `expectedRevision` token; stale → 409 `PROFILE_REVISION_CONFLICT` and the dialog offers a reload. One business audit event `USER_PROFILE_UPDATED` per effective change with changed field **names** only (never values); no event for a no-op. |
| Consistency | Directory, pickers, rosters, audit actors and the shell header use `users.display_name`; bootstrap and `GET /users/me` return it, so the header changes at once after a save. |

### 16.3 Presence

Informational only (never read by authentication, authorization, membership or session code). One row per user in `user_presences` (`last_heartbeat_on`, `last_interaction_on`), shared by all tabs and devices: heartbeat = latest, interaction = greatest. A closed tab sends nothing, so it cannot mark anyone Offline while another tab/device beats.

| State | Rule |
|---|---|
| Available | a heartbeat within 150 s and an interaction within 5 min |
| Away | a heartbeat within 150 s but no interaction for more than 5 min |
| Offline | no heartbeat within 150 s (network failure, closed browser), explicit sign-out (heartbeat cleared), or never seen |
| Unknown | presence could not be determined (store failure, person not in the caller's organization, unknown id) — shown as "Presence unknown", never as Offline |

Client: one heartbeat on shell mount and once a minute through the coordinator's low-priority `polling` lane, carrying only "seconds since last interaction"; never retried or queued; an interaction after more than 4 minutes idle sends one early beat so the person shows Available at once; after 30 minutes without interaction the tab stops beating. Presence readers refresh once a minute and only while the page is visible (no hidden-tab polling). Server: user and organization come from the session (the request carries no user id), atomic upsert, repeats within 10 s write nothing, `interactionAgeSeconds` bounded 0–86400, queries bounded to 100 ids and the caller's own organization.

Shown in: People, team rosters (Presence column), team "Add member" picker, record-share recipient picker. Not shown in audit logs, histories or in every mention of a name. Not added to the form-access picker (its component renders the picker after early returns; deferred rather than risk hook order). Documented limitation: signing out on one device while another device is active clears the shared heartbeat until that device's next beat (at most about a minute).

### 16.4 Approvals

`GET /api/v1/organization/owner-proposals` and `/membership-requests`: server pages of **5**, newest first (id tie-break), `q` search, `status` filter (Pending, Approved, Rejected, Expired), opaque cursor bound to the caller and filters (a cursor from other filters is refused). The two lists page independently. Owners and Security Administrators only. The Authority roster window is 30 days and is read per request; only one page leaves the server. The members endpoint still returns the proposal/request arrays for compatibility (the web no longer uses them). Rows are compact two-line rows (long object IDs truncated, full value as tooltip plus a Copy ID action) and become cards below 768 px.

### 16.5 Tests (executed 2026-10-31)

| Suite | Result |
|---|---|
| Stage B (isolated stack rebuilt from this tree): new `PeopleProfilesPresenceTests` (3) + `MembershipAdministrationTests`, `PeopleDirectoryTests`, `SecurityAdministratorTests`, `SessionRevocationTests`, `FormRenameTests` | 21 / 21 |
| Contract tests (OpenAPI snapshot, directory hints) | 475 / 475 |
| Architecture tests | 71 / 71 |
| Vitest | 302 / 302 |
| ESLint, TypeScript, production build | clean |
| Playwright (isolated): `people-profile-presence` (8), `people-layout` (3), `people-administration` (2 of 3), `organization-administration` (5), `security-administrators` (2) | 20 / 20 run |

Not run: the complete Stage B and SEC-P suites; the first `people-administration` test (it needs a persona that owns records, which a freshly built isolated stack does not have; its designer grant/remove path is covered by `MembershipAdministrationTests`); hosted CI; human screen-reader review. Note: that test, run on a stack without records, really disables its target (it did in the isolated stack; restored).

## 17. People spacing done; membership-request edit/cancel stopped for Authority approval (2026-10-31)

- **Spacing (done):** the People page now separates the Owner proposals and membership requests card from the People card with 24px (`gap-6` on their shared wrapper, not card padding), unchanged card styling; verified at 320, 768 and 1440 px by `people-layout.spec.ts`.
- **Edit/Cancel request (not implemented):** the Authority has no owner-initiated cancel/replace for a membership request (states Pending/Approved/Rejected/Expired; reject is operator-only). New Authority commands and an AuthorityDb migration are required, so the security-sensitive part is **documented and awaiting approval**: `docs/architecture/PHASE_C8_M2_MEMBERSHIP_REQUEST_EDIT_CANCEL_PROPOSAL.md` (decisions requested in its §3).

## 18. Membership request edit and cancel implemented (2026-10-31, owner approval of the proposal) — Development/Testing, not merged, not independently reviewed

Authority commands `CancelOrganizationMembershipRequest` and `ReplaceOrganizationMembershipRequest` plus the AuthorityDb migration `MembershipRequestEditCancel`, as approved in `docs/architecture/PHASE_C8_M2_MEMBERSHIP_REQUEST_EDIT_CANCEL_PROPOSAL.md` (see its §4 for three small differences). Any enabled owner may cancel or edit a Pending request, with the existing AC-7 step-up (refused in staging and production until PB-08). Edit never mutates a request: the original becomes **Replaced**, a new Pending request with a new id, a fresh seven-day expiry and a link back to the original is created in the same transaction, the verified identity and organization are copied by the Authority (never accepted from the caller), and no approval carries over. Cancelled and Replaced requests can never be approved or rejected (operator dry run and execute both refuse `MEMBERSHIP_REQUEST_NOT_PENDING`); approved memberships are never touched. A stale or concurrently changed request is refused (`MEMBERSHIP_REQUEST_CHANGED` / `STALE_AUTHORIZATION_REVISION`); the commands are idempotent; refusals are journaled (`MembershipCommandRejected`) and accepted commands are in the archived security-command journal; the business audit timeline records `MEMBERSHIP_REQUEST_CANCELLED` / `MEMBERSHIP_REQUEST_REPLACED`.

People page: Membership requests Actions by status — Awaiting operators: Edit request, Cancel request, Copy ID; Approved and Replaced: View details, Copy ID; Rejected, Expired and Cancelled: View details, Create new request (opens Add user pre-filled), Copy ID. Edit shows the identity read-only; cancel needs a reason and the step-up. Five-row paging, search and the status filter (now including Cancelled and Replaced) are unchanged.

| Suite | Result (2026-10-31) |
|---|---|
| Authority unit tests (incl. 7 new AC-7.5 tests) | 241 / 241 |
| Stage B real stack: new `MembershipRequestEditCancelTests` (5) + `MembershipAdministrationTests`, `PeopleProfilesPresenceTests` | 11 / 11 |
| Business contract tests / architecture tests | 475 / 475, 71 / 71 |
| Vitest | 309 / 309 |
| ESLint, TypeScript, production build | clean |
| Playwright (isolated): `membership-request-edit-cancel` (3), `people-profile-presence` (7), `people-layout` (4), `people-administration` (2), `organization-administration` (5), `security-administrators` (2) | all run passed |

Not run: the complete Stage B and SEC-P suites; the first `people-administration` test (needs a persona that owns records); hosted CI; human screen-reader review. G14 replay of the new request states has no executed test.

## 19. Platform Operator approvals — architecture proposal (2026-10-31, C-96) — awaiting owner decisions, nothing implemented

`docs/architecture/PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md` proposes replacing `devstack.py membership approve` with a separate Platform Console in which two distinct, individually authenticated Platform Operators each approve the exact request and the Authority executes the membership only after verifying both. It lists the reusable machinery, gaps G-1…G-10, the threat model, tests PO-T01…T25, the staged implementation (Opus/Sonnet) and owner decisions D8-33…D8-44. Until those are recorded, the CLI procedure in §15 remains the only way to decide membership requests (Development/Testing only).

## 20. Platform Operator Stages 1-2 implemented in the Authority (2026-10-31, C-97 approval, C-98) — Development/Testing, not merged, not independently reviewed

The owner approved D8-33…D8-44 for Development/Testing (C-97) with an added condition: a separately protected checkpoint so a database restore cannot silently restore suspended operators. Stages 1 and 2 are implemented in the Authority only, as described in `docs/architecture/PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md` §16 (registry, two-operator changes, suspension, recovery ceremony, platform journal with archive rollback detection and replay, Platform Console credential, statement v2, per-operator evidence, pending approvals, atomic completion, withdrawal, rejection, invalidation, conflict of interest, cross-organization reads). AuthorityDb migration `PlatformOperatorApprovals` (additive). There is no Platform Console BFF or web UI yet, the People page is unchanged, and the legacy `devstack.py membership approve` path is retained (it now closes any pending platform approval of the request it decides).

| Suite | Result (2026-10-31) |
|---|---|
| Authority unit tests (22 new) | 263 / 263 |
| Contract tests (13 new `PlatformConsoleBoundaryTests`) | 488 / 488 |
| Architecture tests | 71 / 71 |
| Real stack (isolated stack rebuilt from this branch): `PlatformOperatorApprovalTests` (4) + `MembershipAdministrationTests` + `MembershipRequestEditCancelTests` | 11 / 12 on the first run; the failure was the existing AC-7.5 three-way owner race test (PF-10, body not captured); 21 / 21 on re-run |

Not run: the complete Stage B and SEC-P suites, Playwright, hosted CI. The persistent development stack was not changed: activating the platform plane there (migration, credentials, three synthetic operators) needs owner approval with Stage 3. Production remains closed: `PlatformOperators:Mode=SyntheticDevelopment` is refused outside Development/Testing and the default `Unavailable` refuses every platform operation; Entra transaction-bound step-up evidence is a refusal stub until PB-08.

## 21. Platform Console Stage 3 — BFF, development sessions, Vault signing, development-stack activation (2026-10-31, C-99) — Development/Testing, not merged, not independently reviewed

The owner approved Stage 3 for Development/Testing (C-99). Built: the separately deployed Platform Console BFF
(`src/platform-console/EnterpriseForms.PlatformConsole`, dev `http://127.0.0.1:8092/console-api`) with its own container, networks,
cookie and Authority credential; development operator sign-in bound to one registered operator and its own key; server-side sessions
(15 min idle, 2 h absolute, server-side sign-out, rotation, ending on suspension); CSRF and origin checks; evidence signed in Vault
Transit with each operator's own non-exportable ECDSA P-256 key, only after the Authority confirms the reviewed statement, never
returned to the browser; the PG-070 API (session, queue, details, approval status, approve, withdraw, reject) reusing the Stage 2
Authority commands unchanged. Authority change: request-detail views audited on the organization audit stream instead of the platform
journal (PF-8, fail closed). `devstack.py platform-console init|status`; `dev-seed` mode `operator-platform-bootstrap`. PG-070
registered (owner Platform administration / Security). Details: `docs/architecture/PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md` §17.

| Suite | Result (2026-10-31) |
|---|---|
| Platform Console BFF tests (new, 23) | 23 / 23 |
| Authority unit tests | 263 / 263 |
| Contract tests | 488 / 488 |
| Architecture tests (3 new; 70-page ownership) | 75 / 75 |
| Real stack, isolated security-test stack recreated with the console: `PlatformConsoleStackTests` (6 new) + `PlatformOperatorApprovalTests` (4) + `MembershipAdministrationTests` (3) + `MembershipRequestEditCancelTests` (5) | 18 / 18 |
| Chromium cookie check (isolated console): Secure + HttpOnly cookie stored and sent over `http://127.0.0.1`, unreadable by script, sign-out without CSRF 403, after sign-out 401 | passed |
| Development stack activation (backup verified + restore-test IDENTICAL, migration, Vault keys/policy/isolation, ceremony, verification, console sessions) and two idempotent re-runs | passed |

PF-10 stays open; the race test now reports every racer's actor, status, code and body. Not run: the complete Stage B and SEC-P suites,
Playwright UI tests (no UI yet), hosted CI. Production remains closed. The legacy CLI approval path is retained (D8-42: retire only
after the Stage 4 console passes its real-stack tests). Next: Stage 4 (`apps/platform-console`), awaiting the owner's go-ahead.

## 22. Platform Console Stage 4 — web app (2026-11-01, C-100) — Development/Testing, not merged, not independently reviewed

`apps/platform-console` is built and served by `platform-console-web` at `http://127.0.0.1:8095/platform/sign-in` (BFF `:8092`): operator sign-in, PG-070 Operator approvals queue, Review details, approve/withdraw/reject. Details and limits: `docs/architecture/PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md` §18. Tests: Vitest 18 new (327 total), ESLint/tsc/build clean, Playwright `e2e-console` 15/15 on the isolated stack; the dev queue was only read (no request opened, none decided). Not done: legacy CLI retirement, M3, merge, production authentication, human screen-reader review.

## 23. People Refresh fix and Platform Console queue cards (2026-11-01, C-101)

Root cause: the People page Refresh button called `refetch()` on the directory query only; the owner proposals and membership requests lists are separate queries (their own search, status and cursor) and were never refetched, so an approval made in the Platform Console appeared only when navigating away remounted them. Fix: Refresh refetches every active `organization` query (directory and both approval lists, with their current parameters), shows a busy state and "Updated hh:mm:ss", and on failure shows "Refresh failed … may be out of date" with Try again; overlapping refreshes are impossible (button busy) and TanStack Query never applies an older response over a newer one. The API had no cache. Platform Console queue: cards below 768 px (same facts, Review details, paging, search and filters), table from 768 px. Tests: 5 new People Vitest (external approval, parameters kept, member appears, busy, failure, slow response), 1 new console Vitest; Vitest workspace 332/332; console Playwright 15/15; People Playwright 14/15, the one failure (`people-administration` first test, needs a persona that owns no records) fails identically without this change.

## 24. Stage 5 — integration and security verification (2026-11-01, C-102) — Development/Testing, not merged, not independently reviewed

Details, evidence table and status vocabulary: `docs/architecture/PHASE_C8_M2_PLATFORM_OPERATOR_APPROVALS.md` §19. Summary: the People Playwright failure was a fixture defect (a fresh isolated stack has no record-owning persona; the first run disabled Riley and left the membership disabled) and is fixed with a supported-API fixture, 7 consecutive passes; a cross-application Playwright test reproduces the accepted workflow (two operators approve in the Platform Console, the open People page shows Approved and the new member after Refresh without navigation) on the isolated stack, 6/6 repetitions, and fails against the pre-fix page; operator security controls were executed (real-stack operator suites 11/11, one new suspended-operator test); PF-10 passed 40/40 repetitions and stays OPEN; persistent-stack integration checks were read-only. Implemented and development-verified only: no independent review, PB-08, PB-10, PB-11, PB-12 or G14 is complete; production is not ready.

## 25. Organization onboarding and owner recovery — architecture and security assessment (2026-11-01, C-103) — proposal, nothing implemented

Document: `docs/architecture/PHASE_C8_ORGANIZATION_ONBOARDING_OWNER_RECOVERY.md`. Inspection of the existing organization bootstrap, dev seed and key custody, owner rules, SpiceDB schema, team rules, Platform Operator infrastructure, Security Administrator recovery and sessions. Findings recorded (not fixed): the legacy operator bootstrap needs one owner, no approval and accepts caller-chosen ids, and answers success for a different request with an existing id (OB-F1…OB-F3, OB-F5); the Authority creates organization keys itself, which works in development only while root custody widens the pinned Vault policy (OB-F4); no organization lifecycle or list (OB-F6); with two owners no owner change is possible and with one no owner can be added (OB-F7, the development main organization has one owner); an organization owner can add themselves to any team and teams can own records, so ownership indirectly reaches team-owned records (OB-F8, High for recovery); conflict of interest compares exact identities only (OB-F9). Proposed: two-operator onboarding with an Authority-built statement, separated key custody and atomic initialization; owner recovery with three operators, incident reference, cooling-off with organization veto, add-only scope and recovered-owner probation; multi-organization identity confirmed at the data-model level with MO-1…MO-6 as separate changes; console pages PG-072…PG-075. Focused Authority unit tests 63/63 (Membership administration, Platform Operators, Security Administrator provisioning). Awaiting owner decisions D8-45…D8-61. No Authority command, migration, permission, page or recovery function implemented; development data unchanged; nothing merged; no M3; legacy CLI unchanged.


## 26. Organization onboarding implemented for Development/Testing (2026-11-01, C-104) — not merged, not independently reviewed

The owner approved the onboarding architecture for Development/Testing with decisions 1–11 (separate console; two distinct operators, the initiator may give approval one; exact immutable request; two owners minimum, three recommended; Authority-generated ids; independently provisioned keys; inactive until initialization and journal verification; production needs verified owner identity control; legacy creation restricted to fixtures; PG-072…PG-075 with PG-071 preserved) and directed a permanent fix for OB-F8. Delivered (details `docs/architecture/PHASE_C8_ORGANIZATION_ONBOARDING_OWNER_RECOVERY.md` §16): Authority onboarding (create, two-operator approval, reject/withdraw/cancel, key verification, atomic initialization, organizations directory, platform journal events, legacy bootstrap restriction and the OB-F2 false-success fix), AuthorityDb migration `20261101131420_OrganizationOnboarding` (additive), a dedicated Vault custody credential (create-only HMAC keys; the Authority verifies and never creates organization keys), `devstack.py onboarding init|status|provision-key|personas`, Platform Console BFF routes and pages PG-072 Organizations, PG-073 New organization, PG-074 Onboarding request (PG-071 registered as reserved), and the OB-F8 Authority rule (no self-join for anyone; only a different owner can add an owner to a team) with regression tests. Owner recovery not implemented; PG-075 not built.

| Suite | Result (2026-11-01) |
|---|---|
| Authority unit tests (24 new, 1 updated) | 282 / 282 |
| Contract / BFF / architecture / integrity | 488 / 488, 23 / 23, 75 / 75, 34 / 34 |
| Real stack (isolated stack recreated from this tree): 7 new + 35 regression | 42 / 42 |
| Frontend Vitest / ESLint / tsc / build | 348 / 348, clean |
| Platform Console Playwright (isolated stack; 8 new) | 23 / 23 |

Defects found and fixed during testing: the frozen statement did not survive PostgreSQL's microsecond timestamps (refused fail closed; timestamps now truncated); Vault authorizes a Transit key create as `update`, so the create-only custody policy could not create keys (replaced by a single-segment `update` grant with pinned parameters; denials re-verified). Unresolved: D8-49 (edits; requests are immutable instead), D8-48 production detail, D8-54…D8-56 (recovery), D8-59 confirmation; table shape deviates from the D8-51 generic-table proposal. Not run: complete Stage B and SEC-P suites, business-app Playwright, hosted CI, screen-reader review. Production remains closed.

Development stack (2026-11-01): `devstack.py onboarding init` — backup `20261101-085519` verified (checksums) and restore-tested (IDENTICAL), migration applied, custody token and policy created, Authority Vault policy switched to read/hmac/verify by prefix, 12 isolation denials verified, Authority/API/worker/console rebuilt; existing data unchanged (2 organizations, 13 identity bindings, no onboarding request). Console onboarding: `http://127.0.0.1:8095/platform/onboarding`.

## 27. Owner recovery implemented for Development/Testing (2026-11-01, C-105) — not merged, not independently reviewed

The owner directed owner recovery for Development/Testing with strong separation of duties and no implicit access to confidential records, applying the approved direction to D8-54…D8-57. Delivered (details and the proposal-by-proposal review in `docs/architecture/PHASE_C8_ORGANIZATION_ONBOARDING_OWNER_RECOVERY.md` §17): Authority owner recovery (eligibility from Authority state with evidence-backed declared unavailability, three distinct operator approvals with per-operator evidence, trusted-clock waiting period, objections that end the case, invalidation on owner-set or identity changes and on declared owners signing in, add-only atomic execution, idempotent retries, platform journal and organization audit events, replay), AuthorityDb migration `20261101221445_OwnerRecovery` (additive), the recovered-owner safeguard on top of the permanent OB-F8 rule, business notice and objection routes (owners and Security Administrators only), Platform Console BFF routes and PG-075, the PG-041 objection notice, and `devstack.py owner-recovery init|status`. Production execution stays unavailable (PB-08 and independent assurance outstanding); notifications are in-app only and no delivery is claimed.

| Suite | Result (2026-11-01) |
|---|---|
| Authority unit tests (19 new) | 301 / 301 |
| Contract / BFF / architecture (75-page ownership) / integrity | 488 / 488, 23 / 23, 75 / 75, 34 / 34 |
| Real stack (isolated stack recreated from this tree): `OwnerRecoveryStackTests` (4 new) + regression `OrganizationOnboardingStackTests`, `OwnerTeamJoinStackTests`, `PlatformConsoleStackTests`, `MembershipAdministrationTests`, `TeamMembershipReconciliationTests`, `PeopleDirectoryTests`, `MembershipRequestEditCancelTests` | 57 / 58 combined; the one failure (RC27 rate-limit throughput, load-sensitive) passes alone 1 / 1; concurrent execution repeated 5 / 5 after the fix |
| Frontend ESLint / tsc / build | clean |
| Vitest workspace (17 new) | 365 / 365 with two workers; the default parallel run shows 2–4 intermittent 5 s timeouts in existing heavy tests (`PeopleDirectoryPage`, `OrganizationOnboarding`), which pass alone |
| Platform Console Playwright (isolated stack; 9 new: three-operator lifecycle with the real waiting period, keyboard-only decision, axe, 320/768/1440 px, 200 %/400 % zoom) | 26 / 32 first run; the 6 failures were a spec race (URL read before navigation) — fixed; rerun 6 / 6 and owner-recovery spec 9 / 9 |
| Business Playwright (isolated stack): objection notice (5 new) and organization administration regression | 5 / 5, 5 / 5 |

Defects found and fixed during testing: under concurrent execution the losing request read a stale tracked case and reported `OWNER_RECOVERY_OWNER_SET_CHANGED` (and journaled a spurious invalidation) instead of `Replayed` — the unlocked lookup is now untracked; the objection confirmation disappeared when the refreshed notice no longer had a case — it now stays; the "cannot object" text named the wrong rule. Covered: one-owner and zero-eligible-owner cases, two eligible owners refused, declared-unavailable evidence, three approvals, same operator twice, suspended operator, conflict of interest, changed owner set, stale/replayed evidence, waiting period, objection blocks, unauthorized and cross-organization objection refused, identity conflicts, concurrent openings and executions, retried execution, archive outage, restore and replay (unit), no record/team/share change, no self-join, recovered owners cannot add each other. Not run: complete Stage B and SEC-P suites, hosted CI, screen-reader review.

Development stack: `devstack.py owner-recovery init` (2026-11-01): backup `Documents\EnterpriseForms-dev-backups\enterpriseforms-dev\20261101-185205` (7 volumes, cold copy) verified (checksums, entry counts) and restore-tested (IDENTICAL); migration `20261101221445_OwnerRecovery` applied; Authority, API, worker, web, console BFF and console web rebuilt from this tree and recreated; data unchanged before/after (3 organizations, 16 identity bindings, 1029 active edges, 8 owner edges, 3 platform operators, 253 registered records); PG-075 at `http://127.0.0.1:8095/platform/recovery` (waiting period 600 s). No recovery case was opened. Evidence: `artifacts/dev-persistent/enterpriseforms-dev-owner-recovery-init-20261101-185506.json`.


## 28. Owner recovery refinements (2026-11-01, C-106) — not merged, not independently reviewed

Delivered on the owner's approval (details `docs/architecture/PHASE_C8_ORGANIZATION_ONBOARDING_OWNER_RECOVERY.md` §18): a searchable, server-paged replacement-owner member picker in PG-075 (Authority member search limited to eligible members, minimum metadata, audited, BFF route, no business credential), the recovered-owner restriction in People management (the Authority's own expiry timestamp, compact accessible disclosure, reason shown on refusal), and test stabilization with measured root causes (limiter first-burst-after-idle admits up to 2 × burst; Vitest oversubscription; Playwright navigation race). The owner recovery security model is unchanged.

| Suite | Result (2026-11-01) |
|---|---|
| Authority unit tests (5 new: picker eligibility, paging, isolation, id binding, restriction expiry) | 306 / 306 |
| Contract / BFF / architecture / integrity | 488 / 488, 23 / 23, 75 / 75, 34 / 34 |
| Real stack, one combined run (recovery incl. 1 new picker test, onboarding, team join, console, membership, reconciliation incl. RC27, people) | 59 / 59 |
| RC27 rate-limit test, repeated in isolation | 5 / 5 |
| Vitest workspace (default command, three consecutive full runs; 6 new tests) | 371 / 371 each |
| ESLint, tsc, build | clean |
| Platform Console Playwright, complete suite as one run (4 new picker tests, results listed at 320/768/1440 px with axe and no horizontal scroll) | 36 / 36 (first run of the round 35 / 36: a wrong heading name in the new spec, fixed) |
| Business Playwright: People recovery restrictions (new; 320/768/1440 px, axe, expiry hidden; server response patched in the browser), objection notice, People layout and administration | all passed |

Two harness defects found and fixed: console sign-ins from many test classes in one run cross the console deliberate 20-per-minute login limit, so the console test session now waits out the window (tests of the limiter itself are unchanged); and killing a test run midway can leave the stack worker stopped, which the next run reports as a fixture failure (restore the stack before rerunning).

Development stack: `devstack.py owner-recovery init` re-run (2026-11-01): migration `20261101221445_OwnerRecovery` already applied and backup `20261101-185205` already recorded (no new backup, no migration); Authority, API, worker, web, console BFF and console web rebuilt from this tree and recreated only; Vault, databases, SpiceDB and Azurite untouched. Data unchanged before and after (3 organizations, 16 identity bindings, 1029 active edges, 8 owner edges, 3 platform operators, 253 registered records). No recovery case was opened and the single-owner main organization was not touched. Evidence: `artifacts/dev-persistent/enterpriseforms-dev-owner-recovery-init-20261101-201341.json`. PG-075 `http://127.0.0.1:8095/platform/recovery`; People `http://127.0.0.1:8091/app/organization/users`.


## 29. Multi-owner selection and the ten-owner limit (2026-11-01, C-107) — not merged, not independently reviewed

Delivered per the owner's direction (details `docs/architecture/PHASE_C8_ORGANIZATION_ONBOARDING_OWNER_RECOVERY.md` §19): one selection mechanism for every replacement owner (selected list, one searchable server-paged picker of 10, collapsed new-identity form, remove, duplicate and limit messages, owner capacity), server-side duplicate and three-replacement rules, and a single Authority-enforced limit of ten active owners across onboarding, owner grants, owner re-enablement, recovery and the fixture bootstrap, checked at the final operation inside the organization lock. The recovery security model is unchanged.

| Suite | Result (2026-11-01) |
|---|---|
| Authority unit tests (7 new: owner limit in the command service incl. re-enable and disabled owners, recovery at the limit, server-side duplicate and three-replacement rules, 10-per-page picker with exclusion and capacity) | 312 / 312 |
| Contract / BFF / architecture / integrity | 488 / 488, 23 / 23, 75 / 75, 34 / 34 |
| Real stack, one combined run (recovery incl. 2 new tests, onboarding, team join, console, membership, reconciliation, people) | 61 / 61 |
| Concurrent owner grants at the limit, repeated | 3 / 3 |
| Vitest workspace (default command; 5 new) | 376 / 376 |
| ESLint, tsc, build | clean |
| Platform Console Playwright, complete suite as one run (selection model, three members one after another, blocked fourth, remove and replace, 320/768/1440 px with the picker, selected list and new-identity form open) | 36 / 36 (two earlier runs of this round failed one new spec on over-broad locators, fixed in the spec) |

Development stack: `devstack.py owner-recovery init` re-run (2026-11-01): migration and backup unchanged (no new backup, no migration); Authority, API, worker, web, console BFF and console web rebuilt from this tree and recreated only; Vault, databases, SpiceDB and Azurite untouched. Data unchanged before and after (3 organizations, 16 identity bindings, 1029 active edges, 8 owner edges, 3 platform operators, 253 registered records; no development organization is near the limit — the largest has fewer than 10 owners). No recovery case was opened. PG-075 `http://127.0.0.1:8095/platform/recovery`.


## 30. M3 — Organization regional defaults and personal preferences (2026-11-03) — not merged, awaiting owner functional acceptance

Branch `phase-c8/m3-regional-preferences` from `main` 935f241. Specification: `docs/architecture/PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` M3 (D8-9). No Authority, SpiceDB or permission change; `user_ui_preferences` unchanged.

**Delivered**
- Business migration `PhaseC8M3RegionalPreferences`: `organization_settings` (unique per tenant, revision, created/modified on/by) and `user_preferences` (unique per tenant and user; the four modes and their values; check constraints make a value present exactly when the mode is Explicit). UUIDv7 keys, multiword columns; runtime grants select/insert/update only.
- API (`/api/v1`): `GET/PUT /organization/settings` (ETag and `If-Match`: 428 missing, 412 stale; no reason is asked for (owner decision, section 31); Authority `manage_configuration` checked on every write), `GET/PUT /me/preferences` (own row only, tenant-scoped; `If-Match` honored when sent), `GET /catalog/{countries,time-zones,languages,date-formats,number-formats}`.
- Typed, versioned code catalog (`RegionalCatalog`, version 2026.11.1): Canada, United States, Mexico then European countries (Europe is a region, not a country), 38 IANA zones with country affinity (`organizationContext=true` lists the organization country first, each zone once), ten languages, six date presets (locale tag + month style + hour cycle), seven number presets. No relational catalog tables.
- Audit: `ORGANIZATION_SETTINGS_UPDATED` (actor, revision, previous and current values) and `USER_PREFERENCES_UPDATED` (modes only), written in the same transaction as the change.
- One client formatter (`packages/ui/src/formatting.ts`): resolves explicit, then organization, then browser, per setting; instants shown in the effective IANA zone and never altered; date-only values never shift; DST gap and fold helpers. All 30 former `toLocale*String`/`Intl` call sites use it (record pages, timelines, audit, uploads, dashboards, security monitoring, people and teams). ESLint rule `no-restricted-syntax` forbids new ones outside `formatting.ts` (verified with a probe file).
- DateTime fields now edit wall-clock time in the user's effective zone (previously the browser's zone): a time inside a spring-forward gap is rejected with a message; a time in a fall-back fold requires choosing the first or second occurrence.
- Pages: PG-043 `/app/organization/regional` (owners edit, others read; live preview) and PG-007 `/app/settings/preferences` (`/app/me/preferences` kept as alias) with a filterable zone list, per-setting radio modes, preview, reset, and the existing text-size card.

**Tests run**
| Suite | Result |
|---|---|
| .NET (Authority 312, contract 499 incl. OpenAPI snapshot and 11 new catalog tests, architecture 75, integrity 34, console 23) | 943 / 943 |
| Frontend: Vitest 413, ESLint, tsc, build, generated-client drift | clean (clean worktree, Level 3) |
| Stage A gate SEC-P01..SEC-P12 | passed (Level 3) |
| Stage B integration, full | 269 / 270; see below |
| `RegionalPreferencesTests` (new, real stack: Authority-decided writes, 428/412, catalog validation, org and user isolation, audit, DB check) | 3 / 3 |
| Playwright `regional-preferences.spec.ts` (America/Toronto and Asia/Tokyo browsers, axe, 320 px) | 4 / 4 |
| Playwright, full suite on the isolated stack (all projects) | 172 passed, 4 skipped, 1 failed: the Organization sidebar test pinned the owner's link list and needed the new "Regional settings" link; updated, rerun with the new spec 8 / 8 |

**Known issues**
- Stage B `SearchCatalogueTests.BenchmarkTheVerifiedScopeAt50And150And480Records` fails on this developer laptop at 480 records with the typed `SEARCH_VERIFICATION_LIMIT` (time) refusal, reproducibly in isolation. PHASE_C7_STATUS already records that this laptop is about twice as slow as the reference runner and refuses at 480 records (PB-09 open). Unrelated to M3 (search untouched); the test was not changed.
- `npm ci` in the owner's working tree failed on locked files (running Vite servers) and left its `node_modules` incomplete; Level 3 was therefore run from a clean worktree. Run `npm ci` in the working tree after stopping the dev servers.
- Organization settings are unsigned presentation rows (G21 family, recorded in the architecture document). Language affects only the `lang` attribute and the preference value in M3; form translation is a later phase.
- Not done (out of scope): §4G option availability policies, dependent lists, translations, M4, legacy CLI retirement.


## 31. M3 owner-review refinements (2026-11-03) — PR #49, not merged, awaiting owner re-acceptance

Changes after the owner's review of M3 (no Authority, permission or migration change):
- **Organization overview:** the owner recovery notice has 24px (`mb-6`) below it, only while the notice is displayed. The per-card Refresh buttons are removed (props, handlers and the "refresh to try again" wording); queries still load on open and the page-level error panel keeps its Retry. The People directory Refresh is unchanged (it synchronizes requests approved in the Platform Operator Console).
- **Regional settings and Personal preferences:** form left (about 65%), live preview right (about 35%), top-aligned; one column on narrow screens; form controls limited to a readable width. The preview follows unsaved choices.
- **Reasons for change (decided by the owner, supersedes the first version of this refinement):** routine organization presentation settings do not ask for a reason. Branding, the logo upload or replacement and the regional defaults save without one, and no reason is invented for the audit record. The audit events still record actor, organization, time, action, revision and the previous and current values (regional settings) or the revision and logo size and digest (branding, logo). Mandatory reasons for security-sensitive operations (Authority commands, owner recovery, membership changes, data-protection governance, field-protection changes, form publishing) are unchanged. Authority check, revision CAS, contrast validation and logo validation are unchanged. Personal preferences never needed one.
- **Language wording:** the language setting now says that the choice is saved, that application translation is planned but not available yet (English is shown), that English remains the fallback, and that entered values and documents are never translated automatically.

**Backlog (not implemented, not scheduled ahead of M4):** application localization. The owner expects translated application vocabulary (navigation, buttons, validation messages, form labels, dropdown display text, instructions). The architecture already holds PG-036, the Form Localization Editor, and CLAUDE.md section 4F (translation snapshots, English fallback, no translation of stored data). M3 resolves and stores the language only. A separate decision is needed for scope, catalog ownership and ordering.

**Verification approach for this refinement (owner decision, see `docs/testing/FAST_FEEDBACK_TESTING.md`):** targeted tests locally, full verification by GitHub Actions after the push. The full local Level 3 script was not rerun.

- **Tabs (owner review):** Personal preferences has the tabs Regional settings, Language and Text size (`/app/settings/preferences`, `/language`, `/text-size`). The Organization pages have Regional settings, Language (`/app/organization/language`) and Text size (`/app/organization/text-size`) in that order below Branding; the organization default text size moved out of Branding. Each tab saves only its own settings and sends the others as last saved (default language through `PUT /organization/settings`, default text size through the branding update with its revision check); no API or database change.

- **Organization navigation groups (owner request):** the Organization navigation is three labelled groups with Lucide icons (`building-2`, `layout-dashboard`, `users`, `users-round`, `palette`, `paintbrush`, `globe`, `languages`, `accessibility`, `shield-check`, `lock-keyhole`, `shield-user`, `clipboard-check`): Organization (Overview, People, Teams), Appearance & Localization (Branding, Regional Settings, Language, Text Size) and Security & Governance (Data Protection, Security Administrators, Search Approvals). Declarative configuration in `OrganizationLayout.tsx`; per-item visibility is the unchanged capability logic and an empty group is not shown. All ten routes are unchanged; page titles are unchanged. In Lucide 1.61 the icon named `building-2` is an alias of `building-complex` (same glyph); the specified `Building2` export is used. Below `lg` the groups stack above the page content (no scrolling strip, 44px targets). The Platform Operator Console, the primary navigation and the Preferences navigation are unchanged.

- **Search Approvals navigation (owner correction):** Search Approvals is no longer in the primary navigation; it is reached under Organization, Security & Governance, in the Organization sidebar, and the page uses the shared Organization layout. The URL `/app/organization/search-approvals`, the `security.administer` requirement (checked again by the Authority on every request) and the page content are unchanged; an owner without that capability gets the layout and a refusal and the page requests nothing. The Organization sidebar column is 18rem wide (was 16rem).

- **Active indicator, User area and pinned header (owner request):** the active primary item had a 4px underline, but TanStack merges the base and active class names, so `text-muted` won over `text-body` and the underline took the muted slate colour at the very edge of the 44px link. Both navigation levels now draw a 3-4px solid underline under the label in the primary token (black by default) from the router's `aria-current="page"`, with a background and semibold text; focus keeps its own outline. A shared `SideNavLayout` (support feature) renders the Organization and the new User sidebars identically (18rem, 18px group and 16px item Lucide icons, 44px items). The User area is `/app/user` (My Profile) and `/app/user/regional`, `/language`, `/text-size` (Experience & Preferences); the five earlier addresses redirect with the router's `redirect` (query and hash kept), and the account menu goes to My Profile and Preferences there. The header stays in the page flow at the top; after about 72px of scrolling (back near 40px) a fixed 3.5rem compact bar takes over with the small logo, the primary links on one row from 1280px (a Menu button below that, which also holds My Profile and Preferences on a phone), the account menu and a visible Sign out (the one session logout). One passive, frame-throttled scroll listener; state changes only at the thresholds; no layout shift; reduced motion respected; `scroll-padding-top: 4.5rem` keeps focused content clear; the sidebars stick below it. Backend, routes of other areas, authorization and the Platform Console are unchanged.

## 32. M4 — Governance audit and security home: architecture and AC-8 security review (2026-11-04, C-108) — proposal, stopped at the AC-8 approval checkpoint

- **Baseline verified:** PR #50 merged (2026-11-04 09:50 UTC); `main` = `c245200`. Branch `phase-c8/m4-governance-audit` was created from that commit.
- **Delivered:** `docs/architecture/PHASE_C8_M4_AC8_SECURITY_CONTRACT.md`, which contains:
  * verified facts F-1…F-14;
  * risks R-1…R-16;
  * the permission matrix;
  * AC-8.1 (`security_auditor` / `view_audit` through `GrantSystemRole`, rules A1–A9);
  * AC-8.2 (sanitized security-command timeline);
  * AC-8.3 (Authority check-and-record evidence of audit access, fail closed);
  * AC-8.4 (security overview, authorized from desired state with an Effective-edge rule so it works while the gate is closed);
  * AC-8.5 (DR-C7-5 coverage as designed);
  * business contracts, redaction catalog, migrations (indexes only) and tests (AC8-T01…T17, `AuditExplorerTests`, `SecurityHomeTests` in existing shards);
  * milestones M4-0…M4-5;
  * owner decisions D8-62…D8-70 and reviewer questions Q-1…Q-5.
- **Finding recorded (pre-existing, not new to M4):** the business database has no PostgreSQL row-level security (F-10; G08 open). The audit explorer relies on a single tenant-bound query adapter plus negative tests, and claims no RLS.
- **Not done:** nothing implemented. No Authority, business, schema or UI change. D8-6/D8-7 (C-78) preserved unchanged. All findings in the contract §12 stay open. This is not an independent review.

## 33. M4 — owner decisions and corrected AC-8 contract (2026-11-04, C-109) — awaiting final owner acceptance; M4-1 not authorized

- **Owner decisions recorded:** D8-62…D8-70, D8-62a (Security Administrator self-removal refused — intentional C-70 change), D8-67a (directory display names shown; field keys, labels, option text, design-diff labels, free-text reasons and matched counts withheld), and Q-1…Q-5. These are owner decisions, not independent approval.
- **Contract revision 2:** `docs/architecture/PHASE_C8_M4_AC8_SECURITY_CONTRACT.md`.
  * **Corrections:** E-1 (self-removal is allowed and tested today, F-15); E-2 (no check-and-record atomicity across SpiceDB and AuthorityDb, F-17).
  * **AC-8.1:** reason codes for auditor commands.
  * **AC-8.2:** restricted ownership and share envelopes.
  * **AC-8.3 protocol:** check → `FOR UPDATE NOWAIT` with a bounded transaction → revision compare → `AuditAccessAuthorized` with `delivery: not_attested` → commit, or 503. Bounded retries, rate limits, telemetry.
  * **AC-8.4/8.5:** Effective-edge rule in one read-only snapshot.
  * **Redaction table:** D8-67/D8-67a.
  * **Risk register:** RR-1…RR-10, including RR-3 (auditor tenure).
  * **Tests and criteria:** tests T01…T23 and acceptance criteria AC-M4-1.1…AC-M4-5.1.
- **C-70 specification:** `SECURITY_ADMINISTRATOR_MANAGEMENT.md` rule 7 carries an "approved change, not implemented" note. Code and tests are unchanged until M4-1.
- **Proposed M4-1 boundary:** C-109. Claude's technical choices to confirm: TC-1…TC-5 (contract §10.2).
- **Not done:** nothing implemented; nothing pushed; no PR. Independent review is still required for closure and merge; Azure staging evidence is still required before production claims.

## 34. M4-1 — AC-8.1 Security Auditor role implemented (2026-11-04, C-110 authorization, C-111) — Development/Testing, not pushed, not merged, not independently reviewed

**Scope (C-109 boundary, nothing beyond it):**
- **Authority:**
  * `organization#security_auditor` / `view_audit` and `RequestedAction.ViewAudit`;
  * `GrantSystemRole` / `RevokeSystemRole` for `security_auditor` with the C-110 reason codes;
  * D8-62a self-change refusal for both roles;
  * sanitized rejection evidence (`SecurityAuditorCommandRejected`, sanitized rejected rows);
  * the roster lists the role.
- **Business:**
  * `/api/v1/organization/security-auditors` (reason codes, appoint, remove);
  * `SECURITY_AUDITOR_APPOINTED` / `_REMOVED` with the reason code only;
  * People-page role, filter and own-row-free hints;
  * the Security Administrators card no longer offers self-removal.
- **UI:** People-page dialog with a required reason-code select and an optional bounded note. It was drafted by a Sonnet subagent and reviewed before commit.
- **Not built:** AC-8.2–AC-8.5, any audit read endpoint or page, the `audit.view` capability, business migrations.

**Commits (signed, local only):**
- `b54821f`: Authority.
- `667643e`: business API and UI.
- `7c9acf4`: Stage B tests, browser spec and C-70 specification.
- The commit carrying this section: contract-test update and documentation.

**Verification (local, real stack; each Stage B shard on its own freshly recreated security-test stack, `down -v` plus rebuild, at `7c9acf4`):**

| Run | Result |
|---|---|
| Stage B `authorization-security-onboarding` (incl. new `SecurityAuditorTests` ×7, rewritten `SecurityAdministratorTests`) | **69/69 passed**, 0 skipped, 525 s |
| Stage B `administration-membership` (incl. updated `PeopleDirectoryTests`) | **55/55 passed**, 0 skipped, 687 s |
| `run_dotnet_tests.py EnterpriseForms.All.slnx` (Authority 322, contract 500, architecture 75, integrity 34, console 23) | **954/954 passed** |
| Vitest (whole frontend) / ESLint / typecheck | **450/450**, clean, exit 0 |
| Playwright `security-administrators.spec.ts` + `organization-administration.spec.ts` (isolated stack, guard verified) | **8/8 passed** |
| OpenAPI and TypeScript client regeneration | Regenerated and committed. The Authority OpenAPI gains only `ViewAudit`. |

An earlier run of the first shard had 3 failures in the new test class. Two were `jsonb` text formatting in assertions, and one was a missing projection-gate wait after a direct command, which correctly fails closed. Those test defects were fixed before the final runs. A contract test that pinned the Security Administrator hint list was updated after the shard runs; that change is test-only.

**Hosted verification:** not run (nothing pushed; owner instruction).

**Contract tests executed:** AC8-T01–T05, T07, T13–T15, T17–T19, T02a, plus the §8.2 C-70 test updates.

**Not executed: T16** (recovery replay after revoke). Neither the Stage B nor the security harness can rebuild SpiceDB from the journal. AC-7's T16 is likewise not executed. T16 stays open.

**Findings recorded in this milestone:**
- **F-M4-1a:** with self-removal refused, `LAST_SECURITY_ADMINISTRATOR` can no longer trigger through the command path, because the actor is always a remaining enabled administrator. It is kept as defence in depth and unit-tested at planner level.
- **F-M4-1b:** rejected `GrantSystemRole`/`RevokeSystemRole` rows (both roles) now keep only validated identifiers instead of the raw request body. This tightens storage; the wire contract is unchanged.
- **F-M4-1c:** `reason_note` is plaintext in AuthorityDb (command payload and authorization journal body) and in the archive, like every existing command reason. The Authority has no application-level encryption for journal text. It never reaches BusinessDb, logs, rejection evidence or auditors. Encrypting it would be a new design (owner decision).
- **F-M4-1d (pre-existing, out of scope):** rejected commands of other kinds (AC-7 and others) still store the raw request payload, step-up evidence included, on the rejected row.
- **F-M4-1e (tooling):** `scripts/dev/stage-b.ps1` says the dev and security-test stacks pin the same subnets. They do not (172.31.x versus 172.30.x), so both can run at once. Under Windows PowerShell 5.1, redirecting the script's output in-session turns Docker's stderr into a terminating error. Run it as a child process instead.

**Still open:** RR-1…RR-10 (contract §3.1, RR-3 auditor tenure included), T16, and everything in contract §12. Independent security review is the closure and merge gate; Azure staging evidence is required before production claims.

## 35. M4-1 conditional acceptance and open findings (2026-11-04, C-112) — proposals only

- **Owner:** AC-8.1 conditionally accepted as implementation evidence. This is not closure and not independent approval; C-111 stays open.
- **Open findings:**
  * **RN-1:** `reason_note` is plaintext in AuthorityDb and the archive, which does not meet C-110. Remediation option R1 is proposed.
  * **RCF-1:** rejected commands of other kinds keep the raw request, step-up evidence included. Severity: Low now, latent High before PB-08.
  * **T16:** outstanding, mandatory, not waived; decomposition T16-B, T16-S and T16-A proposed.
- **Reference:** `docs/architecture/PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md` holds the designs, tests, the review package addendum and decisions D8-71…D8-75.
- **Not done:** nothing implemented, pushed or merged. M4-2 is not started.

## 36. C-113 execution: containment, R1 checkpoint, T16-B/T16-S (2026-11-04, C-114) — not pushed, not merged, not independently reviewed

- **D8-72 containment:** implemented. Commit `b662309` (the Authority refuses notes; the business API refuses before sending; the UI field is removed).
- **R1:** stopped. The existing service does not support the required context binding; design change M1 is requested (D8-76).
- **T16-B:** **PASS**.
- **T16-S:** **FAIL, security defect T16S-1.** After an older SpiceDB datastore is restored, revoked auditor and designer authorizations are effective again with the gate `Ready`. It is pre-existing and affects all SpiceDB-resolved permissions. Remediation S1 is requested (D8-77); the test is kept failing (D8-78).

**Evidence at `b662309`:**

| Run | Result |
|---|---|
| Stage B `authorization-security-onboarding` (fresh stack) | **71/72**; the sole failure is T16-S (the defect). The new containment test and T16-B pass; the SpiceDB restore caused no collateral failures. 551 s |
| Stage B `administration-membership` (fresh stack) | **55/55**, 683 s |
| .NET sweep | **955/955** |
| Vitest / ESLint / typecheck | 450/450, clean, clean |
| Playwright (two affected specs, isolated stack) | 8/8 |

**Open:** T16S-1 (RR-11), RN-1 (contained, RR-12), RCF-1 (RR-13), T16-A under G14 (RR-14), and every item listed in §34. C-111 is open; M4-2 is not started.

## 37. Owner decisions D8-76…D8-78; Designs A and B presented (2026-11-04, C-115) — design only, nothing implemented

- **Design document:** `docs/architecture/PHASE_C8_M4_1_DESIGNS_M1_S1.md` (Opus).
- **Design A (M1):** a seal-only note sealer with its own AAD domain, using the existing primitive and key provider. It defines the exact AAD, envelope schema, validation contract (V1–V11, B1–B8), key-version and recovery behaviour, and tests A-T01…A-T13.
- **Design B (S1):** pin, decide, verify:
  * marker pin, then `at_exact_snapshot` decisions with a `checked_at` proof, then a decisive `fully_consistent` marker read, then the AuthorityDb re-read;
  * lock-confirmed persistent quarantine, an audit event and a Critical alert;
  * the S2 periodic check;
  * the regression matrix RB-01…RB-15 and characterisation tests CT-1…CT-6.
- **New evidence (read-only probes on `authzed/spicedb:v1.56.2`, security-test stack):**
  * `at_least_as_fresh` ignores a foreign-datastore token and accepts a future token, echoing it;
  * `checked_at` equals the exact-snapshot token;
  * zero-match reads stream no token;
  * the `at_exact_snapshot` error classes are `Unknown` (different datastore), `OutOfRange` (future or expired) and `InvalidArgument` (malformed).

  No SpiceDB data was written.
- **Unchanged:**
  * T16-S stays failing (D8-78);
  * T16S-1 is open (RR-11);
  * D8-72 containment is active (RR-12);
  * C-111 is open;
  * M4-2 is not started;
  * nothing is pushed.
- **Decisions requested:** A-Q1…A-Q3 and B-Q1…B-Q4.

## 38. C-116 design review: revision 2 and focused protocol verification (2026-11-04) — documentation and verification only, nothing implemented

- **Approved:** A-Q1, A-Q3, B-Q1, B-Q3, B-Q4. **Conditional:** A-Q2 and B-Q2, answered in `docs/architecture/PHASE_C8_M4_1_DESIGNS_M1_S1.md` revision 2 (§1.9, §4).
- **Evidence:** `docs/evidence/phase-c8/m4-1-protocol-verification/`, from throwaway probes on the disposable security-test stack.

| Check | Result |
|---|---|
| Revoked user during and after a live datastore restore (E1), today's check / S1 protocol | **44,024 stale allows / 0** |
| The same with SpiceDB stopped and restarted (E3) | **62,034 / 0** |
| Snapshot pinned before a restore, decision after (E2/E3) | Exact-snapshot check `ALLOW` with `checked_at = S`, so a pin alone is unsafe; the post-decision `fully_consistent` marker read detected it |
| Datastore replaced (E4); old dump restored into it under a running SpiceDB (E5) | `FailedPrecondition` (no schema); old exact tokens fail "different datastore instance"; the marker reads detect it |
| Latency added by S1 (50–150 requests/s, batch 1 and 25) | p95 **8.8–9.8 ms**, p99 11.1–14.4 ms (local only, not PB-09 evidence) |
| M1 known-answer vector | .NET and Python `cryptography` match byte for byte |

- **Revision 2:** history-bound marker; quarantine on every unchanged-state mismatch; permanent-quarantine trigger; single-use verified snapshot; guarantee G-S1 under I1–I6; residuals RE-1…RE-6.
- **After the probes,** the security-test SpiceDB datastore holds a restored dump. Recreate the stack (`down -v`) before any shard run.
- **Unchanged:** T16-S failing (D8-78); T16S-1 open; D8-72 containment active; C-111 open; M4-2 not started; nothing pushed.
- **Decisions requested:** A-Q2, B-Q2, B-Q5…B-Q9.

## 39. C-117 implementation of S1 and M1; C-118 interim acceptance (2026-11-04/05) — Development/Testing, not pushed, not merged, not independently reviewed

- **C-117:** A-Q2, B-Q2 and B-Q5…B-Q9 approved.
- **C-118:** interim owner acceptance of S1 and M1, and approval of the V3 change.
- **Still open:** M4-1, pending the final evidence and an independent security review.
- **Not authorized:** M4-2, push, PR, merge, deploy, G14 and the RCF-1 fix.
- **Evidence:** `docs/evidence/phase-c8/m4-1-s1-implementation/` (README, per-shard summaries, latency files).
- **Operations guide:** `docs/security/PROJECTION_INTEGRITY_S1_OPERATIONS.md`.

### 39.1 Commits (signed, local)

| Commit | Content |
|---|---|
| `5f5fb07` | **S1 projection integrity** (57 files). Authority: ports and `VerifiedProjectionSnapshot`; `ProjectionMarker` v2; `ProjectionIntegrityVerifier` (pin, decide, verify, lock-confirmed quarantine); `ProjectionIntegrityMonitor` (marker transition and S2 periodic check); projector changes; migration `20261104231152_ProjectionIntegrityS1` (snapshot-token column, legacy-token check constraint, permanence trigger); metrics; readiness gating. Tests: 21 Authority unit tests; architecture tests ARCH-01…03 and U-09; SpiceDB contract probe CT-1…CT-6; Stage B RB-02…RB-15, T16-S tightened as RB-01, and a latency test. Docs: operations guide; DR §10 item 10 |
| `9b47a8b` | **M1 sealed Security Auditor notes** (24 files): `IAuditorNoteSealer` and `AesGcmAuditorNoteSealer`; normalization and padding; Authority validation V1–V11; handlers, contracts, OpenAPI and the TypeScript client; the People dialog note field; Authority, contract and Stage B tests |
| `051a65d` | RC24 test: since the trigger refuses ordinary UPDATEs, it now simulates the privileged G14 clearance. No product change |
| documentation commit (this section) | C-117 and C-118 records; V3 contract amendment with its equivalence test; risk register; evidence; manifest |

### 39.2 Stage B results by shard (each run on a freshly recreated security-test stack)

| Shard | S1 run at `5f5fb07` | Rerun at `051a65d` (S1 + M1 + RC24 fix) | Status |
|---|---|---|---|
| authorization-security-onboarding | **93/93**. An earlier run from the uncommitted working tree was 92/93: an RB-10 test setup error, fixed before the commit | **93/93** | Passed in full at both commits |
| forms-documents | **38/38** | not rerun | Passed in full at `5f5fb07`; not rerun (M1 does not touch it) |
| records-lifecycle | **62/62** | not rerun | Passed in full at `5f5fb07`; not rerun |
| administration-membership | **52/55**, three failures (listed below the table) | **55/55** | Passed in full only at `051a65d` |
| search-catalogue | **16/17**: `BenchmarkTheVerifiedScopeAt50And150And480Records` failed, because the 480-record scope was refused with `SEARCH_VERIFICATION_LIMIT` (time) | shard not rerun; class `SearchCatalogueTests` alone **11/12** (the same benchmark failure) | **Partial.** See 39.4: the 480-record time-limit refusal is **probably contributed to by S1, not proven** (pre-S1 passed alone at 12.5 s p50; S1 was refused in both runs) |
| search-execution | **14/14** | not rerun | Passed in full at `5f5fb07`; not rerun |
| protection-reconciliation | **22/22** | not rerun | Passed in full at `5f5fb07`; not rerun |

The three `administration-membership` failures at `5f5fb07`:
- **RC24 (caused by S1):** the new permanence trigger refused the test's own SQL clearance. The test was fixed in `051a65d`.
- **RC19 (environment):** NETSDK1004, because the worktree had never been restored with NuGet.
- **`StaleAndConcurrentChangesLoseCleanlyAndValidationHolds` (timing, intermittent):** the losing request got a retryable 503 `AUTHORITY_NOT_READY` (`AwaitingArchive`) instead of a 409. Nothing was changed.

**Not every shard passed in full at one commit.**
- Five shards passed in full at `5f5fb07`.
- `administration-membership` passed in full only on the rerun at `051a65d`.
- `search-catalogue` never passed in full. At `051a65d` only its main class was rerun, 11/12, with the same benchmark refusal.
- Four shards were not rerun at `051a65d`: forms-documents, records-lifecycle, search-execution and protection-reconciliation.

### 39.3 Required tests

| Test | Result (fresh stack) |
|---|---|
| T16-S = RB-01 (SpiceDB datastore restored from before a revoke) | **Pass** at `5f5fb07` and `051a65d`: 423 `INTEGRITY_QUARANTINE`; gate `Quarantined`; reason `PROJECTION_MARKER_BEHIND`; one `ProjectionIntegrityQuarantined` journal event; the alert is logged. The revoked auditor and the revoked designer are denied |
| T16-B (hostile BusinessDb restore) | **Pass** at both commits |
| RB-02…RB-15 | **Pass** at both commits (cases listed below the table) |
| CT-1…CT-6 (SpiceDB behaviour contract) | **Pass** at both commits |
| M1 Stage B `ASealedReasonNoteReachesTheAuthorityAsCiphertextOnlyAndThePlaintextIsNeverKept` | **Pass** at `051a65d` (checks listed below the table) |
| M1 contract tests (business API) | **Pass**: KAT-M1-01; context binding (organization, target, role, direction, note id); tampering; cross-domain; padding and normalization rules; key provider unavailable (503, nothing sent); no decrypt operation exists |
| M1 Authority tests | **Pass**: plaintext refused permanently; envelope rules V2–V10; omitted and `null` equivalent (C-118) |
| Non-stack sweep at the documentation commit | **990/990** (Authority 346 including the new V3 equivalence test, contract 507, architecture 80, integrity 34, platform console 23) |

RB-02…RB-15 cover:
- shares;
- field items;
- team membership;
- discovery and the catalogue;
- form permissions;
- cross-organization isolation;
- a missing marker;
- the Authority behind SpiceDB;
- permanence across restarts, commands, projection and a direct UPDATE;
- legitimate concurrent changes, which never quarantine;
- a live restore;
- a replaced datastore;
- the periodic check;
- an outage, which fails closed without quarantine.

The M1 Stage B test checks that:
- the Authority receives only the envelope with its 2,048-byte ciphertext;
- the plaintext sentinel appears nowhere: not in a `pg_dump` of AuthorityDb or BusinessDb, the rejected rows, the audit stream, or the api, authority and worker logs;
- the Authority refuses plaintext;
- a note id cannot be reused;
- an idempotent replay is one command;
- an invalid note sends nothing.

### 39.4 Performance

**Authorization checks** (`AuthorizationLatencyTests`: open loop, host to Authority; fresh stack per run; two runs each; p95 in ms):

| Load | Pre-S1 | S1 | Added (mean) |
|---|---|---|---|
| single @ 50 rps | 14.80, 15.59 | 21.86, 21.40 | +6.4 |
| single @ 150 rps | 20.62, 16.65 | 27.86, 38.63 | +14.6 (+7.2 and +22.0) |
| batch of 25 @ 50 rps | 16.80, 14.55 | 22.70, 25.62 | +8.5 |

- **B-Q9 target (added p95 ≤ 15 ms):** met at 50 rps. At 150 rps it is **not reliably met** on this laptop: the mean is 14.6 ms, but one run added 22 ms.
- **Measurement limits:**
  * one developer laptop, with the owner's dev stack running at the same time;
  * no TLS, zones or staging data volume;
  * two runs per configuration.
- **The staging performance gate decides:** Azure staging PERF-01 under PB-09 conditions is required before production readiness.
- **Optimizations O-B2 and O-B1:** not implemented; they need separate approval.

**Verified search scope** (`SearchCatalogueTests` benchmark, server ms):

| Run (fresh stack each) | 50 records p50 / p95 | 150 records p50 / p95 | 480 records p50 / p95 |
|---|---|---|---|
| Pre-S1 `70894d2`, class alone | 1,990 / 2,132 | 5,738 / 6,197 | **12,481 / 12,952 (passed)** |
| S1 `5f5fb07`, in the shard | 1,327 / 1,498 | 4,276 / 4,824 | **refused** (`SEARCH_VERIFICATION_LIMIT`, time, 15 s) |
| S1 + M1 `051a65d`, class alone | 1,561 / 1,697 | 4,451 / 4,717 | **refused** (same) |

- **Verdict: S1 probably contributes, but this is not proven.**
- **Evidence for an S1 contribution:** like-for-like (class alone, fresh stack), pre-S1 completed 480 records, and S1 was refused in both of its runs.
- **Evidence against a simple slowdown:**
  * At 50 and 150 records S1 was *faster* than the pre-S1 run, so run-to-run variance on this laptop (about ±30%) dominates.
  * Pre-S1 already used 83% of the 15 s budget at 480 records.
  * Pre-S1 has exceeded the budget on this laptop before (`PHASE_C7_STATUS.md` §15.5).
  * The comparison is three runs, not a statistical sample.
- **What S1 adds on this path:**
  * for each verification batch, one pin (marker read) and one verify (marker read plus AuthorityDb re-read): about 20 extra round trips over the 10 batches of a 480-record scope;
  * the per-record checks at an exact snapshot instead of `at_least_as_fresh`.
  * This is enough to push a scope at the edge of the budget over it. It has not been isolated.
- **Behaviour is safe:**
  * the refusal is the typed, non-partial fail-closed answer (PB-04);
  * no result was partial;
  * no test other than the benchmark failed.
- **Effect:** on this laptop, verified search near the 500-record ceiling is refused more often. Hosted runners were about 2× faster in C7 (480 records at 6.1 s). Whether S1 changes this on staging is unmeasured.
- **Possible remedy (not implemented, needs approval):** verify each batch with one bulk check instead of one check per record. This uses the same snapshot and the same S1 protocol. A staging measurement of the search path should be added to PERF-01.

### 39.5 Invariants (as implemented and tested)

- **Every protected SpiceDB decision is pinned and verified:**
  * permission checks, bulk checks and lookups run at the pinned exact snapshot, and `checked_at`/`looked_up_at` must equal it;
  * a `fully_consistent` marker read and an AuthorityDb re-read follow every decision;
  * the single-use `VerifiedProjectionSnapshot` is the only route to the SpiceDB ports (ARCH-01…03).
- **A confirmed mismatch quarantines the organization:**
  * only when the Authority state is unchanged;
  * the quarantine is lock-confirmed and persisted together with an audit-stream event, and a Critical alert follows;
  * if the Authority state changed in the meantime, the decision is retried; it never results in an allow.
- **A quarantine is permanent under normal operation:**
  * no code path clears it;
  * the trigger refuses, for every role, UPDATEs that clear it or change its reason;
  * the projector does not write for a quarantined organization;
  * only a privileged G14 recovery could lift it, and that recovery is not implemented.
- **A pre-S1 Authority cannot run silently against an S1 database:** the legacy token column is constrained `NULL`. The minimum safe version and the pipeline rule are documented, but the PB-11 pipeline that would enforce it does not exist yet.
- **Single SpiceDB backend (I3, B-Q8):** a deployment rule that the code cannot enforce. RE-1 is the accepted residual risk.
- **M1:**
  * no plaintext note is persisted anywhere;
  * there is no decrypt operation;
  * notes are refused when no key provider is configured;
  * an omitted envelope and a `null` envelope are equivalent.

### 39.6 Remaining defects, risks and deviations

- **Defects:** no known open security defect in S1 or M1 on the tested paths. There has been no independent security review.
- **Open performance finding P-S1-1:** a 480-record verified search scope is refused at the 15 s budget with S1 on this laptop; S1 probably contributes (§39.4). This is fail-closed, not a security defect. It needs either the bulk-check remedy (approval required) or staging evidence.
- **Risks:**
  * RE-1, accepted under I3;
  * RE-2…RE-6, see `PROJECTION_INTEGRITY_S1_OPERATIONS.md`;
  * a false quarantine lasts until G14 exists, an availability risk;
  * RR-13 (RCF-1) and RR-14 (T16-A, needs G14) remain open;
  * RR-6 and RR-7: archive immutability and AuthorityDb administration in production;
  * M1 has no production key providers or key rotation yet;
  * the trigger stops UPDATEs, not DDL by the table owner, who is inside the Authority trust boundary.
- **Deviations:**
  * V3, approved in C-118;
  * B-Q9 not reliably met at 150 rps locally;
  * the `search-catalogue` shard was not rerun in full at the final commit, and four other shards were not rerun at it.
- **Observed test flakiness:** the membership race test once got a retryable 503 (`AwaitingArchive`). The test still expects only 409s, and is unchanged here.

## 40. C-119: final Stage B reruns, P-S1-1 investigation and the independent review package (2026-11-05/06). Development/Testing, not pushed, not independently reviewed

Owner decision C-119: S1 and M1 accepted **as implemented, for the purpose of independent review**. This does not close M4-1 and is not independent approval.

- M4-2 is unauthorized.
- No push, PR, merge or deploy.
- No G14 or RCF-1 work.

Code baseline: `f117995`, with no product or test change since. This section and its evidence are documentation only.

### 40.1 Final Stage B matrix at `f117995` (fresh stack per run; a failed shard rerun once, except the known benchmark)

| Shard | Final pass | Single rerun | Full pass at `f117995`? |
|---|---|---|---|
| forms-documents | 38/38 | not needed | yes |
| authorization-security-onboarding | 92/93: `SecurityAdministratorTests.BrowserSessionsNeedCsrfAndKeepNoPrivilegeAfterRemovalOrSignOut`, 423 | 92/93, the same | **no** |
| records-lifecycle | 62/62 | not needed | yes |
| administration-membership | 55/55 | not needed | yes |
| search-catalogue | 16/17: the 480-record benchmark refused (`time`, P-S1-1) | not rerun (known finding) | **no** |
| search-execution | 14/14 | not needed | yes |
| protection-reconciliation | incomplete: 1 failure, then the host slept during the next test | 22/22 | yes (rerun) |

**Five of seven shards pass in full at `f117995`. The Stage B suite is not green.**

**Failures:**
- **Authorization failure, reproducible, a test-isolation defect:**
  * RB-13 swaps in an empty SpiceDB datastore, and S1 then quarantines every organization on the stack (`PROJECTION_SCHEMA_MISSING`; 18 organizations in one burst in the rerun);
  * those include the seeded development organization (`01a1aee5-0415…`, the earliest on the stack, created by dev-seed), which the browser-session tests share;
  * a later seeded-organization test therefore gets 423;
  * S1 behaved as designed;
  * it passed at `5f5fb07` and `051a65d`, so test order evidently varies between runs;
  * the fix, not implemented, is a test change for owner decision: isolate the tests that destroy the whole datastore.
- **Protection-reconciliation final-pass failure:** `TheCatalogueCeilingEndsIncompleteAndExpiredSnapshotsAreRenewedSafely` expected round 1 and saw round 2 of a correctly blocked reconciliation. It did not recur in the rerun. Recorded as intermittent timing; the cause has not been investigated.

**Evidence:** `docs/evidence/phase-c8/m4-1-s1-implementation/shards-final-f117995-*.txt`, `shards-final-rerun-f117995-*.txt`, `shards-final-rerun-f117995-quarantine-alerts.txt`.

### 40.2 P-S1-1 investigation (Opus)

**Method:** a throwaway timing probe on `f117995`, never committed, with its diff kept as evidence. One run of the benchmark class on a fresh stack. Per 50-record Authority batch (median):

| Component | Time | Share of the batch |
|---|---|---|
| S1 pin | 2.8 ms | 0.7%, together with the verify |
| S1 verify | 4.6 ms | |
| 50 single-record SpiceDB checks | 182 ms | 19% |
| 400 per-leaf MAC verifications | 556 ms | 54% |
| Other AuthorityDb reads and processing | 272 ms | 27% |

**Business side:** sequential BusinessDb content reads took about a third of the search.

**480-record scope:** refused again. In the 8 of 10 batches that finished, S1's pin and verify took 68 ms in total.

**Conclusion, for the independent reviewer to confirm:** the refusal is a capacity limit that predates S1, not an S1 regression. The pre-S1 480-record figure (12.5 s) is below a linear extrapolation of its own 150-record figure, and this is unexplained.

**Proposal** (`docs/architecture/PHASE_C8_M4_1_P_S1_1_SEARCH_OPTIMIZATION_PROPOSAL.md`; nothing implemented):
- O-1: batch-level bulk SpiceDB checks, with result mapping by echoed request and fail-the-call on per-item errors; about −1.4 s at 480 records;
- O-2: batched BusinessDb content reads, no authorization change; about −4 to −5 s;
- O-3 and O-4: concurrent MAC verification and batched AuthorityDb reads, deferred to PERF-01;
- rejected options listed in the proposal.

**New PERF-01 observation:** 3,840 HMAC verifications per 480-record search against Managed HSM in production.

### 40.3 Independent review package

`docs/architecture/PHASE_C8_M4_1_S1_M1_INDEPENDENT_REVIEW_PACKAGE.md`:
- the eight owner questions, with the source, test and evidence paths for each;
- how to reproduce;
- limitations;
- the final matrix;
- the P-S1-1 summary;
- the findings and attestation templates.

**It has not been submitted:** no independent reviewer is available inside this session, and an AI agent acting for the owner or Claude is excluded by the package's own independence rule. The owner sends it to a reviewer.

### 40.4 Open items

- the Stage B suite is not green: the RB-13 test-isolation defect, and P-S1-1;
- independent security review not started;
- PERF-01 and the Azure staging recovery evidence remain mandatory;
- §39.6 risks unchanged.
