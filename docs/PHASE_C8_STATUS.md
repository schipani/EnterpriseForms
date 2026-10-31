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
