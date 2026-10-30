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
