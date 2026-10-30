# Phase C8 — Administration and Governance: architecture and milestone plan

- **Status:** M0 (planning) only, C-77. **Nothing in this document is implemented.** Every Authority change it describes (AC-7, AC-8) is a **proposal awaiting owner approval** (§9). The C7 merge does not authorize them.
- **Branch:** `phase-c8/administration-governance`, from `main` `b1e1b30` (the Phase C7 squash merge, PR #29).
- **Status log:** `docs/PHASE_C8_STATUS.md`. **Decision:** C-77 in `docs/decisions/PHASE_C_DECISION_LOG.md`.
- **Normative inputs:**
  * `CLAUDE.md` §4B (RBAC, teams, ownership), §4D (audit), §4E–§4G (organization settings, preferences, catalogs), §4J (passwordless policy), §4M–§4N (Authority, consistency), §4T (simplification precedence), §4U (feature ownership);
  * `CLAUDE-DESIGN.MD` §16.6–§16.7 (PG-041…PG-059);
  * ADR-PB-008 (identity), ADR-PB-012 (evidence), ADR-PB-013 (offboarding), ADR-PB-014 (UI acceptance);
  * `SECURITY_GAP_ASSESSMENT.md` G19–G26.

## 0. Summary

C8 makes the organization administrable from the product: overview, people, roles, teams, organization defaults, the data-protection floor, and an administrative audit and health view. It does this **without moving any authorization decision out of the Authorization Authority**. Today the business database is only a display projection of users and teams, and it stays that.

The baseline (§1) shows the decisive constraint. **The Authority can currently change only one organization role after bootstrap (Security Administrator, C-70), and it can neither add a member nor disable one.** Organization owners and designers are fixed at operator bootstrap. Member lifecycle and role administration therefore need new Authority security commands. These are the proposed **AC-7**, plus **AC-8** for a dedicated audit role. They are Level 2 Authority-security changes, so they need owner approval now and, like AC-5 and AC-6, independent review before any production claim.

The plan is five milestones:

| Milestone | Theme | Authority change |
|---|---|---|
| M0 | This plan | None |
| M1 | Administration shell, read-only overview, team management UI over the existing commands, F-C7-03 audit fix | **None** |
| M2 | AC-7: members and organization roles | **Yes (AC-7)** — needs approval D8-2…D8-5 |
| M3 | Organization regional defaults and personal preferences (§4E/§4F subset) | None |
| M4 | AC-8: governance audit explorer, security administration home, DR-C7-5 coverage view | **Yes (AC-8)** — needs approval D8-6/D8-7 |

M1 and M3 can be built without any Authority change. M2 and M4 cannot start until their decisions are approved.

## 1. Baseline on `main` b1e1b30 (verified by reading the code, 2026-10-29)

### 1.1 Authority (source of truth)

- **SpiceDB `organization`:** `organization_owner`, `system_administrator`, `designer` (plus the projection cursor).
  * Permissions: `manage_configuration = organization_owner`, `administer_security = system_administrator`, `create_form = designer`.
  * There is **no organization-member relation.** Membership is the existence of an enabled `identity_binding` (issuer, subject → Authority user) for the organization. `ActorResolver.BindAsync` refuses an unbound or disabled identity with 403 `ACTOR_NOT_BOUND`.
- **`team`:** `member` only; no team-manager relation.
- **Security command kinds:**
  * teams: `RegisterTeam`, `AddTeamMember`, `RemoveTeamMember`;
  * forms: `RegisterFormDefinition`, `UpdateFormUsers`, `PublishFormSecurityRevision`, `ReclassifyField`;
  * records: `RegisterRecord`, `TransferOwnership` (owner, or audited `system_administrator` override), `CreateShare`, `RevokeShare`;
  * organization: `UpdateMandatoryPolicy`, `GrantSystemRole` / `RevokeSystemRole` (Security Administrator only, C-70);
  * C7: the field-search commands.
- **Identity bindings and the organization roles `organization_owner` and `designer` are created only by operator-plane bootstrap** (`OrganizationBootstrapService`). There is no command to add a member after bootstrap, to disable one, or to grant or revoke owner or designer.
- **First or recovery Security Administrator:** operator plane with two ES256 approvals (DR-C7-3, C-71). Production fails closed until PIM-backed approver identities exist (G26).
- **Step-up:** the Authority's PB-08 gate is unavailable in staging and production. Commands that require it are refused there; isolated Development/Testing use a synthetic path (OD-7 pattern).
- **Sessions:** the Authority holds the session registry (G23), so a session can be revoked server-side.

### 1.2 Business API and UI

| Area | Exists | Gaps against `CLAUDE-DESIGN.MD` §16.6–§16.7 |
|---|---|---|
| Organization page | Branding page (`/app/organization/branding`, PG-042 partial) with Security policy card (data-protection floor, PG-046 partial) and Security Administrators card (C-70); Search approvals page (C7) | No overview (PG-041); no separate data-protection page; logo upload not audited (**F-C7-03**) |
| Users | `users` projection created at sign-in for identities the Authority already bound; `GET /api/v1/users` returns display names to any member, for pickers | No user directory or detail for administrators (PG-050/051); no deactivation; no role view |
| Teams | `POST/GET /api/v1/teams`, add or remove member (Authority commands, `organization.manage`) | **No team UI at all** (PG-048/049); no rename, deactivation or manager role |
| Roles | Security Administrator appoint/remove (C-70); form-level designer/user grants (PG-040) | Owner/designer organization roles cannot be changed |
| Organization settings | None. Only `user_ui_preferences` (text scale) | No `OrganizationSettings`, `UserPreferences`, regional catalogs (§4E–§4G) |
| Audit | Business `audit_events` projection; My Activity; Record Activity; Authority journal and audit stream (C-62) | No administrative audit explorer or event detail (PG-054/055); no auditor role (§4D asks for a separate SecurityAuditor) |
| Security operations | — | No security home (PG-052); coverage is verifier-only (DR-C7-5) |
| Capabilities | `records.discover`, `forms.design`, `organization.manage`, `security.administer` (navigation hints, each decided by the Authority) | — |

## 2. Scope

### 2.1 In scope (proposed, D8-1)

1. **Organization administration and settings:**
   * overview (PG-041);
   * branding (existing PG-042), with F-C7-03 fixed;
   * data-protection floor moved to its own page (PG-046), with behaviour unchanged.
2. **Membership, users and administrative roles:**
   * user directory and user detail (PG-050/051);
   * enable/disable membership and grant/revoke `organization_owner` and `designer` through AC-7;
   * the Security Administrator card stays (C-70).
3. **Teams:** directory and detail (PG-048/049) over the existing Authority team commands. Team rename and deactivation are only if D8-8 approves them.
4. **Organization-wide configuration and defaults:** regional defaults (PG-043) and personal preferences (PG-007), covering time zone, date format, number format and language, as a subset of §4E/§4F.
5. **Security policy administration and governance:**
   * the existing floor;
   * a read-only view of authentication policy status, with no enforcement until PB-08;
   * the security administration home (PG-052);
   * the DR-C7-5 coverage view (if D8-7).
6. **Administrative audit trail and visibility:** audit explorer and event detail (PG-054/055) under a dedicated audit permission (AC-8).
7. **Tenant isolation:** every administrative read and command is scoped to the session's organization and decided by the Authority (§6).

### 2.2 Out of scope (proposed for later phases; nothing here is started)

| Item | Why it is excluded |
|---|---|
| Catalog availability and dependent mappings (PG-044/045, §4G) | Large, separable, needs its own catalogs and history rules |
| FIDO2 policy enforcement (PG-047, §4J) | Depends on PB-08 (real Entra and step-up); C8 shows status only |
| Ownership-transfer workflow page (PG-053) | Separable record workflow. The existing Authority override is used only for the deactivation prerequisite (D8-4) |
| Incidents, Authority health, DR, job operations (PG-056…059) | Controlled Operations pages; operator plane |
| Import, Export, Organization offboarding (ADR-PB-013) | Gated after Stage B and before real onboarding; its own phase |
| Entra-based invitations | Depends on PB-08 (D8-3) |

## 3. Architecture

### 3.1 Principles (unchanged guarantees)

1. **The Authority decides; the business database displays.**
   * Role holders, membership state and team membership shown to administrators are read from the Authority, as the C-70 roster already is.
   * Business `users`, `teams` and `team_memberships` rows remain display projections and confer nothing.
   * A hostile business DBA who edits them changes a label, never a permission.
2. **Every change is an authenticated, revision-bound Authority security command.** Each one has an idempotency key, the expected authorization revision, a reason, step-up where specified, and a journal entry and audit event. There is no business-side role table and no direct SpiceDB write.
3. **Fail closed.** If the Authority is unavailable, the projection is behind, or a command is pending, the UI shows **Pending** or **Unavailable**, never "done". Pages and cards refresh independently (PB-07).
4. **Separation of duties (extends G25):**
   * organization configuration (`organization_owner`), security administration (`system_administrator`), form design (`designer`) and audit (`security_auditor`, AC-8) are distinct;
   * none grants record content;
   * no actor can grant themself a role;
   * the last enabled holder of `organization_owner`, and of `system_administrator`, cannot be removed or disabled through the product.
5. **No new infrastructure.** C8 uses the existing ASP.NET Core Minimal APIs (`/api/v1`), EF Core and PostgreSQL, React/TypeScript, generated client, request coordinator, TanStack Query and `packages/ui`. No new package is planned. Any that becomes necessary will be justified in the decision log first.

### 3.2 Feature ownership (§4U)

| Feature (existing) | C8 additions |
|---|---|
| `Organizations` | overview, data-protection page, regional defaults (`organization_settings`), Authority roster port for display |
| `Identity` | member directory and detail, membership enable/disable and role commands (AC-7 adapters) |
| `Teams` | team directory and detail UI; rename and deactivation if D8-8 |
| `UserPreferences` | `user_preferences` and presentation resolver (M3) |
| `Audit` | audit explorer and event detail (M4) |
| `SecurityOperations` (new feature folder, frontend `security-operations`) | security home and coverage view (M4); no Authority state, signer or recovery plane |

Frontend routes stay owned by these features. Shared controls stay in `packages/ui`, and there is no global `src/pages`.

### 3.3 Administrative UI

The Organization area keeps the existing `SideNavTabs` pattern. Each tab is visible only when the session holds the matching capability. The server decides every action regardless.

```text
┌ Logo ─ EnterpriseForms ───────────────────────────── Org ▾ ─ User ▾ ┐
│ Dashboard  Forms  Records  Designer  Organization  Search approvals │
├──────────────────┬──────────────────────────────────────────────────┤
│ Organization     │  Overview                                [↻ each]│
│ ▸ Overview       │  ┌ Organization ─────┐ ┌ Members and roles ───┐   │
│   People         │  │ name, country,    │ │ 12 members, 2 owners,│   │
│   Teams          │  │ time zone (M3)    │ │ 1 sec. admin, 3 dsgn │   │
│   Branding       │  └───────────────────┘ └ (from the Authority) ┘   │
│   Regional (M3)  │  ┌ Teams ────────────┐ ┌ Data protection ─────┐   │
│   Data protection│  │ 4 teams           │ │ floor: Confidential  │   │
│   Security admins│  └───────────────────┘ └──────────────────────┘   │
│                  │  ┌ Authentication ───────────────────────────┐   │
│ Security (M4)    │  │ Status only — enforcement awaits PB-08    │   │
│   Home           │  └───────────────────────────────────────────┘   │
│   Audit          │                                                  │
└──────────────────┴──────────────────────────────────────────────────┘
```

| Route | Page | Visible to (capability) | Milestone |
|---|---|---|---|
| `/app/organization` | PG-041 Overview | `organization.manage` or `security.administer` | M1 |
| `/app/organization/people` | PG-050 directory | `organization.manage` or `security.administer` | M1 read-only; M2 actions |
| `/app/organization/people/:authorityUserId` | PG-051 detail | same | M2 |
| `/app/organization/teams`, `/teams/:teamId` | PG-048/049 | `organization.manage` | M1 |
| `/app/organization/branding` | PG-042 (existing) | `organization.manage` | M1 (audit fix) |
| `/app/organization/data-protection` | PG-046 (card moved) | `organization.manage` (edit), `security.administer` (view) | M1 |
| `/app/organization/regional` | PG-043 | `organization.manage` | M3 |
| `/app/settings/preferences` | PG-007 | every member | M3 |
| `/app/security`, `/app/security/audit`, `/app/security/audit/:auditEventId` | PG-052/054/055 | `security.administer` / `audit.view` (AC-8) | M4 |

*Route naming:* `CLAUDE-DESIGN.MD` says `/users` for PG-050. "People" is proposed as the visible label. The route segment will follow the design document (`/users`) unless the owner prefers otherwise (minor, part of D8-1).

Every page meets the PB-14 rules:
- 320, 768 and 1440 px widths, and 200% and 400% zoom;
- black primary buttons and visible checkbox states;
- per-card refresh;
- generic 403/404 pages (PG-060/061) with no existence hints;
- axe checks in Playwright.

Manual NVDA/VoiceOver review remains outstanding, as for every earlier page.

## 4. Milestones

### M1 — Administration shell, read-only overview, teams UI, F-C7-03 (no Authority change)

- **Functional:**
  * Organization area navigation;
  * Overview cards: organization, member/role counts, teams, data-protection floor, Security Administrators, authentication status "not enforced — PB-08";
  * People directory, read-only: display names and membership from the business projection, labelled as such, with role badges taken only from the Authority roster when available;
  * Teams directory and detail: create team, add/remove member, using the existing commands with Pending → Effective status;
  * data-protection page (existing card moved, behaviour unchanged);
  * **F-C7-03:** logo upload and replacement emit audit events.
- **Database:**
  * business: no new table;
  * audit action catalog gains `ORGANIZATION_LOGO_UPLOADED` (and `…_REPLACED` if distinguished);
  * Authority: none.
- **API:**
  * `GET /api/v1/organization/overview`, an aggregate of independently failing parts so a card can show "unavailable" without failing the page;
  * `GET /api/v1/teams/{teamId}` (detail and roster, from the projection with the command state);
  * existing team endpoints unchanged.
  * OpenAPI changes are additive only.
  * Role badges in M1 use only the existing Security Administrator roster. Owner and designer badges wait for the AC-7 roster read (M2).
- **Authorization:**
  * overview, people and team reads require the Authority `manage_configuration` or `administer_security` decision;
  * team commands keep requiring `manage_configuration` at the Authority, unchanged, including the C-70/G25 rule that a Security Administrator cannot join a team themself.
- **Tests:**
  * contract tests;
  * a test that every new endpoint is organization-scoped;
  * cross-tenant negative tests (another organization's team or user id ⇒ generic 404);
  * a member without the capability gets 403 with no counts;
  * logo audit event present;
  * real-stack team create/add/remove;
  * Playwright plus axe on the security-test stack only (C-74 guard);
  * responsive specs at 320 px.
- **Acceptance:** owner functional acceptance on the isolated stack. A pending command never shows as effective.
- **Risks:** the business projection can lag the Authority. The UI labels the source and shows pending state.

### M2 — AC-7: members and organization roles (Authority change; needs D8-2…D8-5)

- **Functional:**
  * People detail: identity (issuer and subject shown as non-secret metadata, never email as key), membership state, organization roles, teams, form-level grants (read), directly owned record **count** (from the Authority, no titles), and a security change timeline;
  * actions: grant/revoke `organization_owner` and `designer`; disable/enable membership;
  * the Security Administrator card stays as it is.
- **Authority (AC-7, §5):**
  * new command kinds `GrantOrganizationRole` / `RevokeOrganizationRole` (`organization_owner`, `designer`) and `DisableIdentityBinding` / `EnableIdentityBinding`;
  * read `GET /internal/v1/organizations/{id}/roster`.
- **Database:**
  * AuthorityDb: `identity_bindings` gains lifecycle columns `identity_disabled_on`, `identity_disabled_by_security_command_id`, `identity_enabled_by_security_command_id` (additive migration);
  * business: `users.is_active` is updated by the projection only, never by an administrator request.
- **API:**
  * `GET /api/v1/organization/members`, `GET /api/v1/organization/members/{authorityUserId}`;
  * `POST …/members/{id}/roles` and `POST …/members/{id}/roles/{roleName}/remove`;
  * `POST …/members/{id}/disable` and `POST …/members/{id}/enable`.
  * Every command takes `Idempotency-Key` and a reason, and returns `CommandStatusResponse` (Pending / Effective / Rejected).
- **Authorization:** see §5 rules R1–R9.
- **Tests:**
  * Authority unit tests and real-stack adversarial tests AC7-T01…T16 (§5.4);
  * business contract and negative tests;
  * a browser test of role grant/revoke and disable with pending state;
  * SEC-P regression and the existing G25 tests.
- **Acceptance:**
  * owner functional acceptance on the isolated stack;
  * **independent review of AC-7 recorded as outstanding** (same status as AC-5/AC-6);
  * production unavailable until PB-08 step-up exists.
- **Risks:** this extends the trusted computing base, and a lockout can occur if last-holder rules are wrong. Mitigation: R5/R6 tests and operator-plane recovery (DR-C7-3) unchanged.

### M3 — Organization regional defaults and personal preferences (no Authority change)

- **Functional:**
  * Regional defaults (PG-043): country (CA, MX, US, or a real European country), IANA time zone, language, date preset and number preset, with a live preview;
  * Personal preferences (PG-007): time zone, date, number and language, each independently **Use organization default / Use browser settings / Explicit**;
  * one shared client formatting resolver applied to record pages, timelines, audit and uploads;
  * stored instants and date-only values are never shifted.
- **Database (business, multiword columns, UUIDv7 keys):**
  * `organization_settings`: `organization_setting_id`, `tenant_id` unique, `default_country_code`, `default_iana_time_zone_id`, `default_language_code`, `default_date_format_preset_key`, `default_number_format_preset_key`, `organization_setting_revision`, created/modified on/by;
  * `user_preferences`: `user_preference_id`, `tenant_id`, `user_id` unique per tenant, `time_zone_preference_mode`, `iana_time_zone_id`, `date_format_preference_mode`, `date_format_preset_key`, `number_format_preference_mode`, `number_format_preset_key`, `language_preference_mode`, `language_code`, `preference_revision`, created/modified on/by;
  * check constraints: Explicit ⇔ value present.
  * Catalogs (supported countries, IANA zones and their country affinity, presets, ten languages) are a **typed, versioned, server-validated code catalog** in M3 (§4E allows this). Relational catalog tables arrive with §4G in a later phase.
  * The existing `user_ui_preferences` (text scale) is kept and not merged.
- **API:**
  * `GET/PUT /api/v1/organization/settings` (ETag/`If-Match`, reason, `organization.manage`);
  * `GET/PUT /api/v1/me/preferences`;
  * `GET /api/v1/catalog/time-zones?organizationContext=true` (Canadian zones first, then persisted order), plus presets and languages.
- **Authorization:** settings writes check Authority `manage_configuration`; preferences are the caller's own row only. Neither affects authorization, encryption, audit truth, search or stored values.
- **Integrity note:** organization settings are presentation configuration in unsigned business rows. A hostile business DBA could change a default time zone or format, which changes display but never stored data or permissions. This is recorded as a known residual of the G21 family, not a new security claim.
- **Tests:** DST gap and fold, date-only preservation, invalid or tampered IANA ids and preset keys rejected, resolver precedence, tenant isolation, optimistic concurrency, audit events (`ORGANIZATION_SETTINGS_UPDATED`, `USER_PREFERENCES_UPDATED`), and browser tests in two time zones.
- **Risk:** consistent use of the formatter across existing pages is touch-heavy. It needs a lint or architecture check against ad-hoc `toLocaleString` calls.

### M4 — AC-8: governance audit and security home (Authority change; needs D8-6/D8-7)

- **Functional:**
  * Audit explorer (PG-054): chronological business `audit_events` for the organization, with filters (date, actor, action, outcome, entity, correlation), keyset pagination ordered by `(event_occurred_on, audit_sequence)` and labelled as event-time order, not commit order;
  * Event detail (PG-055), with redaction as stored;
  * Authority security-command timeline for administrative commands (role, membership, policy, team, security administrator);
  * Security home (PG-052): read-only widgets for projection gate state, pending or failed security commands, field-protection migrations and attestations, search approvals, and the DR-C7-5 coverage view if D8-7 is approved.
- **Authority (AC-8, §5.5):**
  * new relation `organization#security_auditor` and permission `view_audit`, appointed and removed by Security Administrators (`GrantSystemRole` extended to this role name) under the same rules;
  * read `GET /internal/v1/organizations/{id}/security-command-timeline` (administrative kinds only, sanitized payload summary, no record content).
- **Database:**
  * business: index `(tenant_id, event_occurred_on, audit_sequence)` on `audit_events` if the existing indexes do not cover it (to be checked);
  * AuthorityDb: none beyond the relation.
- **API:** `GET /api/v1/organization/audit-events` (cursor, filters), `GET /api/v1/organization/audit-events/{id}`, `GET /api/v1/organization/security-commands`, and `GET /api/v1/security/overview`.
- **Authorization:**
  * the audit APIs require Authority `view_audit`, and audit access is itself audited;
  * reading an event never grants access to its target record, and links to records go through normal authorization;
  * the security home requires `administer_security`.
- **Tests:**
  * auditor sees events but no record content;
  * Security Administrator without `security_auditor` cannot read the explorer (if D8-6 separates them);
  * cross-tenant cursor and ID tampering;
  * redaction (no encrypted values, PINs or search terms);
  * audit of audit access;
  * keyset paging stability;
  * the AC8-T tests.
- **Explicit non-claims:**
  * the business `audit_events` table is a queryable projection, not tamper-proof evidence (§4D, ADR-SEC-2026-10-16-01);
  * G24 (archive durability) and G11 stay open;
  * audit export is left to ADR-PB-013 Exports.

## 5. Proposed Authority changes (approval required)

### 5.1 AC-7 rules (proposed)

| # | Rule |
|---|---|
| R1 | Only an enabled `organization_owner` of the same organization may grant or revoke `organization_owner` or `designer`, or disable/enable membership (D8-2 may tighten owner grants to dual approval). |
| R2 | No actor may grant a role to themself, enable themself, or disable themself. |
| R3 | A Security Administrator gains nothing from AC-7: `administer_security` does not satisfy R1 (G25 extension). An owner cannot appoint a Security Administrator (C-70 unchanged). |
| R4 | The target must be an enabled identity binding of the same organization (issuer and subject). Never an email address, business row or client flag. |
| R5 | The last enabled `organization_owner` cannot be revoked or disabled (409). Recovery stays on the operator plane. |
| R6 | Disabling the last enabled `system_administrator` is refused, mirroring C-70 removal. |
| R7 | Disabling a member atomically, in the same Authority command: (a) marks the binding disabled; (b) revokes all of that identity's Authority sessions (and record unlocks, where they exist); (c) removes nothing else from the graph. Roles, team edges and shares stay, but are unusable because `ActorResolver` refuses a disabled binding. Re-enabling restores them unchanged. Revocation is immediate at the Authority. Bytes already delivered cannot be recalled. |
| R8 | Disabling is refused while the identity **directly owns** active records, unless D8-4 chooses otherwise. The refusal reports a count only. Reassignment uses the existing audited `TransferOwnership` override. |
| R9 | All AC-7 commands need fresh step-up: the PB-08 gate, unavailable in staging and production, synthetic in isolated Development/Testing (OD-7 pattern). A reason of 1–500 characters is required, and the command is journaled and audited. |

### 5.2 Why R7 keeps edges rather than deleting them

Deleting a disabled user's shares, team memberships and roles would make re-enabling lossy and would create a large implicit security change. Keeping the edges and refusing the binding is a single, reversible and auditable state change. The cost is that a team-derived permission of a disabled member is not usable, but it is still listed. The roster shows this explicitly.

### 5.3 Consistency

- The new commands use the existing pipeline (§4N): the journal commit, desired state, outbox, SpiceDB projection, and the Effective gate.
- The binding flag lives in AuthorityDb under the same transaction as the command. `ActorResolver` reads it directly, so a disable is effective at commit and does not wait for projection.
- Recovery replay (G14) must reproduce the flag. The DR tests will be extended accordingly (`DR-*` regression; no new DR claim).

### 5.4 AC-7 adversarial tests (to be implemented with M2; real Authority, both PostgreSQL databases, SpiceDB, Vault)

| Test | Scenario | Expected result |
|---|---|---|
| T01 | Owner grants designer | Effective; the designer gains `create_form` and no record content |
| T02 | Self-grant / self-enable / self-disable | 403 |
| T03 | Security Administrator grants owner or designer | 403 (G25) |
| T04 | Designer grants designer | 403 |
| T05 | Grant to another organization's binding | 403 or 404, no existence hint |
| T06 | Revoke or disable the last owner | 409 |
| T07 | Disable the last Security Administrator | 409 |
| T08 | Disabled member's existing session | Rejected on the next request (session registry) |
| T09 | Disabled member's team-derived and share-derived access | Denied |
| T10 | Re-enable | Prior edges usable again; nothing new granted |
| T11 | Business DBA sets `users.is_active=true` or forges a role row | No effect |
| T12 | Stale expected revision | 409, retry |
| T13 | Replayed idempotency key with a different payload | `IDEMPOTENCY_CONFLICT` |
| T14 | Step-up gate unavailable (production configuration) | Refused |
| T15 | Disable while owning records (R8) | 409 with a count only |
| T16 | Restore replay | Flag preserved; a revoked role is not re-granted by an older snapshot |

### 5.5 AC-8 (proposed)

- **New relation** `organization#security_auditor`, permission `view_audit = security_auditor`.
  * Appointed and removed only by a Security Administrator, by extending `GrantSystemRole` / `RevokeSystemRole` to this role name.
  * Self-appointment is refused. A Security Administrator may also hold the role only if D8-6 allows it.
  * The role gives no record content, no design rights and no configuration rights.
- **Read-only security-command timeline:** administrative kinds only, with sanitized summaries (actor, kind, target identity, state, revision, timestamps, reason). There are no share field lists, record titles or values.
- **Tests:**

  | Test | Scenario | Expected result |
  |---|---|---|
  | T01 | Appointment | Allowed only for a Security Administrator |
  | T02 | Self-appointment | Refused |
  | T03 | Auditor reads a record or a document | Denied |
  | T04 | Cross-organization timeline | Denied |
  | T05 | Payload sanitization | Verified |
  | T06 | Revoked auditor's next read | Denied |
  | T07 | Business DBA forges an auditor row | No effect |

### 5.6 Review requirement

AC-7 and AC-8 join AC-5 and AC-6 in the **independent Authority review package**. None of them may be described as independently reviewed or production-ready until that review and Azure staging evidence exist.

## 6. Tenant isolation

- The organization always comes from the authenticated session, never from a route, body or query parameter.
- Business queries filter by `tenant_id` and run under the existing RLS session context.
- Authority reads bind the actor to the organization (`ActorResolver`) before any lookup.
- IDs of other organizations return the same generic 404 as unknown IDs.
- Directory search never spans organizations.
- Counts are returned only to callers holding the relevant permission.
- Cursors are bound to organization, filters and permission epoch (PB-06).
- Cross-tenant negative tests are mandatory in every milestone.

## 7. Outstanding findings: carried forward unchanged

C8 **does not close, downgrade or reinterpret** any finding.

| Finding | Effect on C8 |
|---|---|
| **G19** (migration completeness) | OPEN. The M4 security home may *display* existing attestations with the C-75 wording ("current versions only … DR-C7-4"); nothing claims more. |
| **G20** (field-scoped contributor writes) | OPEN, switch default off, refused in Production. C8 does not touch it. |
| **G21** (option/form metadata integrity) | OPEN. M3 settings are presentation configuration in the same unsigned-row residual family. Recorded, not fixed. |
| **G24** (search-audit archive durability) | OPEN. The M4 explorer reads projections; no durability claim. |
| **G25 / G26** | Open for independent review / production PIM. AC-7 extends G25's separation rules (R3), and AC-8 is appointed under them. |
| **C-74 development incident remediation** (R1–R8) | **Not executed** and awaits the owner's approval of exact IDs. C8 never writes to the development stack. All C8 browser tests use the isolated security-test stack with the Playwright guard. |
| **PB-08, PB-09, DR-C7-1…6, F-C7-03, AUD-C7-01, production Level 2** | Open. F-C7-03 is proposed for correction in M1 (an audit gap, not a security floor). Everything else is unchanged. |
| **AC-5/AC-6 independent review** | Still required. C8 adds AC-7/AC-8 to the same package. The C7 merge did not authorize any Authority change beyond those implemented. |

## 8. Verification approach per milestone

- **Local:**
  * .NET build (warnings as errors);
  * `scripts/ci/run_dotnet_tests.py`;
  * Vitest, ESLint and typecheck;
  * OpenAPI regeneration and drift check;
  * targeted real-stack classes on the **security-test stack** (recreated with `down -v` before use).
- **Hosted, on the final commit of each milestone:** `ci`, `stage-b-integration`, `level3-script`, CodeQL, and `security-gate-a`, which the owner dispatches.
- **Evidence:** status-log sections that record commit SHA, run IDs and pass counts. Unrun tests are never reported as passing.

## 9. Decisions requiring owner approval (before the named milestone starts)

| ID | Decision | Recommendation | Blocks |
|---|---|---|---|
| D8-1 | Approve the C8 scope (§2.1), exclusions (§2.2), milestone order M1→M4 and route/label choices (§3.3) | Approve | M1 |
| D8-2 | AC-7 as a direction. **Who may grant `organization_owner`:** (a) any enabled owner, single actor with step-up; (b) two distinct owners (propose + approve); (c) operator plane only | **(b)** for owner grants; (a) for designer | M2 |
| D8-3 | **New members after bootstrap:** (a) operator-plane dual-control onboarding (reuses the DR-C7-3 machinery), Development/Testing only until PIM; (b) in-app invitation by owners, which needs PB-08/Entra and is deferred; (c) none in C8 | **(a)**, with (b) deferred | M2 |
| D8-4 | **Disabling a member who owns records:** (a) refuse until reassigned (R8); (b) allow, leaving records owned but unusable until transfer | **(a)** | M2 |
| D8-5 | **Disable semantics:** keep edges and refuse the binding (R7), versus deleting edges | Keep edges (R7) | M2 |
| D8-6 | AC-8 auditor role: (a) a new `security_auditor` relation appointed by Security Administrators; (b) reuse `system_administrator` for audit. Also, may one person hold both? | **(a)**; holding both allowed but shown | M4 |
| D8-7 | DR-C7-5 read-only coverage view for Security Administrators in the M4 security home | Approve, read-only, with C-75 wording | M4 |
| D8-8 | Team lifecycle in C8: rename (display only) and deactivation (an Authority command: deactivation refused while the team owns records), and/or a team-manager relation | Rename only; deactivation and team managers deferred | M1 (rename) |
| D8-9 | Confirm the M3 subset of §4E/§4F (defaults and preferences, typed code catalog), with §4G availability and dependent lists deferred | Approve | M3 |
| D8-10 | C-74 development incident remediation: execute before C8 images reach the development stack, or keep deferred (C8 tests never use it) | Owner's choice; it does not block M1 on the security-test stack | — |

Each approval will be recorded as a decision-log entry (C-78 onward) before work on the milestone it unblocks begins.
