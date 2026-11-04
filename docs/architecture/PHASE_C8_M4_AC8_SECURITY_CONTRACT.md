# Phase C8 M4: governance audit and security home (AC-8 architecture and security contract)

- **Status:** **proposal (C-108, 2026-11-04). Stopped at the AC-8 architecture/security approval checkpoint.** Nothing is
  implemented. No Authority relation, permission, command, route or schema and no business endpoint, migration or page was
  changed. C-78 condition 8 requires this separate approval before any AC-8 Authority change.
- **Baseline:** `main` = `c245200` (PR #50 "CI: shard the Stage B integration suite", merged 2026-11-04 09:50 UTC; it
  includes C8 M1–M3 and AC-7). Branch `phase-c8/m4-governance-audit` was created from that commit.
- **Audience:** the project owner (decisions D8-62…D8-70) and the independent Authority reviewer (AC-8 joins the
  AC-5/AC-6/AC-7/AC-9 package). This work is by Claude, so it is **not** an independent review.
- **Supersedes:** the M4 sketch in `PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §4 (M4) and §5.5, which this document makes
  specific. **D8-6** (a separate `security_auditor` relation appointed by Security Administrators; one person may hold both
  roles, and the UI shows it) and **D8-7** (read-only DR-C7-5 coverage view for Security Administrators with the C-75
  wording) were approved in C-78 and are preserved unchanged.

## 1. Verified facts (2026-11-04, `c245200`)

| # | Fact | Where |
|---|---|---|
| F-1 | The `organization` definition has `organization_owner`, `system_administrator`, `designer` and `projection_cursor`; its permissions are `manage_configuration`, `administer_security` and `create_form`. There is no audit relation or permission. | `src/authority/AuthorizationAuthority.Infrastructure/SpiceDb/authority.schema.zed` |
| F-2 | `GrantSystemRole`/`RevokeSystemRole` accept only `system_administrator`. They require an actor who is a Security Administrator, a reason of 1–500 characters and step-up (`FieldSearchApprovalGate.RequireStepUp`: synthetic in Development/Testing, refused in staging/production), and they keep the last enabled administrator. **Self-appointment is refused only incidentally:** the actor is already an administrator, so the "already a Security Administrator" check fires. No explicit self-change rule exists in this planner. | `CommandPlanner.SecurityAdministrators.cs` |
| F-3 | AC-7 `GrantOrganizationRole` administers only `organization_owner` and `designer` (`AdministeredRoles`), for owners only, with `SelfChangeForbidden`. | `CommandPlanner.Membership.cs` |
| F-4 | `PermissionCheckService.MapCheck` maps the organization actions `ManageOrganization`, `AdministerSecurity` and `DesignForm`; any unmapped action is `Invalid`. | `PermissionCheckService.cs` |
| F-5 | The Authority journal has three streams: `authorization`, `content` and `audit`. Entries are hash-chained (`JournalEventDigest`, `JournalPreviousDigest`) and delivered by `ArchiveProcessor` (`ArchiveDeliveryState` Pending, Anchored or VerificationFailed, plus a receipt reference and digest). The audit stream does not gate access (OD-8). It already records platform-plane **reads** (`PlatformOrganizationDirectoryViewed`, `PlatformOwnerRecoveryCaseViewed`, `PlatformMembershipRequestViewed`), so read-access evidence in the Authority has precedent. | `AuditJournal.cs`, `SecurityJournalEntry.cs` |
| F-6 | The business API can read a security command only by id, and only as its originating actor (`GET /internal/v1/security-commands/{id}`). Gate status is exposed only on the operator plane (`/operator/organizations/{id}/gate`). No delegated-user route lists commands, gate state, archive state or protection coverage. | `SecurityCommandEndpoints.cs`, `OperatorPlaneEndpoints` |
| F-7 | Business `audit_events` is an append-only projection. The runtime role has insert-only access (`BusinessRuntimeGrants.InsertOnlyTables`: no UPDATE or DELETE), but a BusinessDb DBA can insert, alter or delete rows. Index `ix_audit_events_tenant_time (tenant_id, event_occurred_on, audit_sequence)` covers the default ordering. `ix_audit_events_record_time (form_record_id, event_occurred_on)` has no tenant column. There is **no** actor or correlation index. | `AuditEntityConfigurations.cs`, `BusinessRuntimeGrants.cs` |
| F-8 | `AuditRecorder` writes a free-form `EventDescription` (≤500 characters) and anonymous metadata serialized as JSON, from about 40 call sites. The metadata keys found include **free text** (`reason`, `changeReason`, `requestReason`, `retryReason`), **display names** (`formDefinitionName`, `previousFormDefinitionName`, `teamName`, `previousTeamName`, `previousDisplayName`, `newDisplayName`), **identity identifiers** (`subjectIdentifier`, `issuer_uri`, `display_name_hint`) and **form-design diffs** (`changes`, `elements`, `items`, `groupChanges`). The inventory found no field-value keys. However, write-time redaction is a **convention, not a control**: nothing rejects a value-bearing key. | `AuditRecorder.cs`, call-site inventory |
| F-9 | Business audit rows have no archive receipt: AUD-C7-01/G24 are open. The `audit_event_types` table listed in the feature ownership matrix does not exist; actions are code constants (`AuditActions`). | `FEATURE_OWNERSHIP_MATRIX.md`, `AuditEvent.cs` |
| F-10 | **No PostgreSQL row-level security exists in the business database.** No migration, script or code contains `ENABLE ROW LEVEL SECURITY`, `CREATE POLICY` or `set_config`. Tenant isolation on `audit_events` is only the application's `tenant_id` predicate. This is pre-existing (G08 and §4C.6 are open), not introduced by M4. However, the audit explorer would be the **first organization-wide reader across all record events**, so it is a sharper exposure. | repository search |
| F-11 | Session capabilities come from one Authority batch check per request (`DashboardHandler.CapabilitiesAsync`) and control navigation only. | `DashboardHandler.cs` |
| F-12 | DR-C7-5 is fully designed: `POST /internal/v1/field-protection/coverage` requires `administer_security` (404 otherwise), returns counts only and never enumerates records. | `docs/security/FIELD_PROTECTION_RECONCILIATION.md` §13 |
| F-13 | The SpiceDB schema is embedded and written at Authority start-up. A new relation is additive, and recovery replay rebuilds edges from the journal. | `SpiceDbProjectionClient.cs` |
| F-14 | Authority search-execution evidence (`FieldSearchExecuted`) carries snapshot, form, user, field ids, operators, counts, timing and revision. It never carries predicates or values. The business projection has no search-execution action. | `SearchCatalogueService.cs` |

## 2. Scope

| Item | Delivers | Authority change |
|---|---|---|
| **AC-8.1** Security Auditor role | `organization#security_auditor` and `view_audit`; appointment and removal through `GrantSystemRole`/`RevokeSystemRole` | Yes |
| **AC-8.2** Security-command timeline read | Sanitized administrative command history and allowlisted audit-stream entries, with archive state | Yes (read) |
| **AC-8.3** Audit-access evidence | Check-and-record: `view_audit` decision and an audit-stream entry in one Authority transaction | Yes (read + audit-stream write) |
| **AC-8.4** Security overview read | Gate, command, archive and approval health for `administer_security` | Yes (read) |
| **AC-8.5** DR-C7-5 coverage read | As designed in F-12 (D8-7) | Yes (read) |
| **PG-054** Audit explorer | Business endpoints and pages over `audit_events` | No (uses AC-8.1/8.3) |
| **PG-055** Audit event detail | As above | No (uses AC-8.1/8.3) |
| **PG-052** Security administration home | Independently refreshing widgets | No (uses AC-8.4/8.5) |

**Out of scope:** audit export (ADR-PB-013 Exports); PG-053 ownership transfer; incident disposition (SD-1/AC-10); archiving business audit rows (AUD-C7-01/G24); encrypting incident notes (SD-4); business RLS (G08); real step-up (PB-08); and production or staging enablement.

## 3. Security risks and how the design handles them

| # | Risk | Treatment |
|---|---|---|
| R-1 | **Audit as a content side channel.** Metadata and descriptions carry names, free text and design diffs (F-8). An auditor is not a record reader. | Redaction happens **at read time** through a code-resident allowlist catalog (§6.3). Unknown keys are dropped, an unknown or forged action shows only the envelope, and descriptions are rendered from the catalog unless the catalog marks the stored text safe. A reflection test fails the build if any `AuditActions` constant lacks a catalog entry. Sentinel-value leak tests (§8) cover the rest. The catalog is code, not a DBA-editable table. |
| R-2 | **A business DBA forges, alters or deletes audit rows** (F-7). | Never claimed as evidence. Every business row is labelled "Business projection — not independently verified (AUD-C7-01/G24 open)". No business row ever shows a verified badge. Authority timeline entries show their real archive state and nothing more. |
| R-3 | **A forged auditor in BusinessDb** (a role row, `users` flag or session flag). | The permission comes only from the Authority's SpiceDB check of `view_audit`. Business rows confer nothing (AC8-T07). |
| R-4 | **The auditor reaches record content** through links, ids, titles or joins. | The explorer never joins record titles, values, file names or share scopes. A record link opens the normal record route, whose own authorization decides. Accepted, inherent disclosure: an auditor learns record **identifiers and existence** and who acted on them (D8-67). |
| R-5 | **Privilege escalation through appointment.** | Only a Security Administrator may appoint. An explicit self-change refusal is added (F-2 shows today's protection is incidental). Owners, designers and auditors cannot appoint. Residual: two Security Administrators can appoint each other. D8-6 allows this, so the roster and explorer show dual holders. |
| R-6 | **Access to audit is silently erased.** A business-only `AUDIT_VIEWED` row is DBA-deletable. | AC-8.3: the business API obtains the decision and an Authority audit-stream entry from a single call. If the entry cannot be written, access is denied (fail closed, 503 `AUDIT_EVIDENCE_UNAVAILABLE`). A business row is also written for the timeline. |
| R-7 | **Audit-access writes contend on the organization lock** (each append takes it, F-5). | One entry per page or detail view, never per row. A per-user rate limit uses the AC-9 limiter pattern. Contention is measured against security commands in M4-3 (PB-09 numbers recorded, not assumed). |
| R-8 | **Cursor, filter or id tampering, or cross-tenant access.** | The organization comes from the session only. Cursors use `IOpaqueCursorProtector` and are bound to organization, actor and a filter digest. Every page is re-authorized at the Authority. Another organization's event id gets the same 404 as an unknown id. |
| R-9 | **Revocation during paging.** | No permission caching. The next page or detail request is re-checked (AC8-T06). |
| R-10 | **The security home cannot show an unhealthy gate** because a closed gate fails every SpiceDB check (§4N). | AC-8.4 authorizes from the Authority's canonical desired state with an **Effective-edge rule** (§5.4): a revoke applies at commit, a grant only once Effective. Both directions fail closed. Reviewer question Q-3. |
| R-11 | **Counts leak record facts.** | The PG-052 and coverage responses carry counts and command kinds only, never record ids, titles, values or payloads (F-12 rules). |
| R-12 | **Platform-plane reads of the organization** appear in the audit stream. | Shown to the organization auditor for transparency, with the operator's display reference and no operator credentials (D8-67). |
| R-13 | **Event time read as commit order**; the clock is the business server's. | Always labelled "event-time order with sequence tie-break, not commit order", and the timestamp source is stated. |
| R-14 | **No step-up for reading audit** (PB-08 unavailable). | Appointment requires the existing step-up gate (refused in staging/production). Audit reads do not require step-up in M4. This is residual risk SD-5 (D8-69). |
| R-15 | **Missing business RLS** (F-10). | All reads go through one query adapter that always applies `tenant_id = @session_org` and is tested against a second organization's rows. RLS remains G08, out of scope and not claimed. |
| R-16 | **Recursion and noise** from audit-access events. | `AUDIT_*_VIEWED` events are listed like any other event (auditors see each other), and a filter can exclude them. |

## 4. Permission boundaries

| Principal | PG-054 / PG-055 | AC-8.2 timeline | PG-052 widgets | Coverage (AC-8.5) | Appoint / remove auditor | Record content through audit |
|---|---|---|---|---|---|---|
| Security Auditor (`view_audit`) | Yes | Yes | Archive health only (D8-70) | No | No | **Never** (normal record authorization decides) |
| Security Administrator (`administer_security`) | **No** (D8-6) | No | Yes | Yes | Yes (not themself) | Never |
| Both roles | Yes | Yes | Yes | Yes | Yes (not themself) | Never |
| Organization owner | No | No | No | No | No (G25 pattern) | Never |
| Designer / member | No | No | No | No | No | Never |
| Platform Operator | No (operator plane only) | No | No | No | No | Never |
| BusinessDb DBA | Can read and alter raw rows (residual R-2) | No | No | No | No effect (R-3) | Raw plaintext rows only (existing residual) |

Owners and members keep the existing My Activity and Record Activity views, which are unchanged.

## 5. AC-8 Authority contract (for approval)

### 5.1 AC-8.1 Security Auditor relation and commands

```zed
definition organization {
    ...
    relation security_auditor: user
    permission view_audit = security_auditor
}
```

- `OrganizationRoleNames.SecurityAuditor = "security_auditor"`; `RequestedAction.ViewAudit` maps `(Organization, ViewAudit)` to `view_audit`, and the business enum gains `ProtectedAction.ViewAudit`.
- `GrantSystemRole`/`RevokeSystemRole` accept `role_name ∈ {system_administrator, security_auditor}`. The payload is unchanged: `authority_user_id`, `role_name`, `reason` and `step_up_evidence`.

| # | Rule (for `security_auditor`) |
|---|---|
| A1 | The actor must be an enabled `system_administrator` of the same organization. Owner, designer and auditor all get 403. |
| A2 | The actor may not appoint or remove themself (403 `SELF_CHANGE_FORBIDDEN`). This explicit rule also covers the role-holder case that F-2 handles only incidentally. |
| A3 | The target must be an enabled identity binding of the same organization (`RequireUserBindingAsync`). Removal also works for a disabled identity. |
| A4 | Appointing a current holder, or removing a non-holder, returns `Invalid` (same as for administrators). |
| A5 | There is **no last-holder rule**: an organization may have zero auditors. |
| A6 | A reason of 1–500 characters and step-up through the existing gate (synthetic in Development/Testing, refused in staging/production). |
| A7 | The existing pipeline applies: idempotency key with request digest, expected revision, journal, desired state, outbox, SpiceDB projection, the Effective gate and recovery replay. The normalized payload drops step-up evidence. |
| A8 | `system_administrator` rules are unchanged (C-70 and G25). Holding both roles is allowed (D8-6). |
| A9 | AC-7 disable keeps the edge (R7). A disabled auditor is refused by `ActorResolver`; re-enabling restores the edge unchanged. |

**Roster:** `OrganizationRosterService` and `SecurityAdministratorRosterService` add `security_auditor` to their role lists. Auditors are visible to owners and Security Administrators, exactly like Security Administrators (D8-68). Appointment candidates are listed to Security Administrators only. **Business:** the actions `SECURITY_AUDITOR_APPOINTED` and `SECURITY_AUDITOR_REMOVED` are added, and the People page shows a "Security Auditor" badge, so a dual holder shows both badges. The Authority journal command is the evidence.

*Alternative considered:* new command kinds (`GrantSecurityAuditor`). Rejected because the approved M4 text extends `GrantSystemRole`, and the rules are the same administrator-governed pattern.

### 5.2 AC-8.2 Security-command timeline read

`POST /internal/v1/organizations/audit/security-command-timeline` (business workload plus delegated user; the organization is bound by `ActorResolver`; body `{after?, page_size ≤ 50, kinds?, states?, from?, to?}`). It requires `view_audit` and records itself (AC-8.3) **in the same transaction**.

- **Included:**
  * authorization-stream entries for the administrative kinds `RegisterTeam`, `AddTeamMember`, `RemoveTeamMember`, `GrantSystemRole`, `RevokeSystemRole`, `GrantOrganizationRole`, `RevokeOrganizationRole`, `ProposeOrganizationOwnerChange`, `DisableIdentityBinding`, `EnableIdentityBinding`, the three membership-request kinds, `UpdateMandatoryPolicy`, `ReclassifyField`, `ApproveFieldSearch`, `WithdrawFieldSearch` and `UpdateFieldSearchers`, **including Rejected commands** with their rejection code;
  * audit-stream entries of an allowlisted set: the membership, provisioning, owner-recovery, protection-attestation, search-execution, alert and platform-read kinds in F-5/F-14.
- **Excluded:** `RegisterRecord`, `CreateShare`, `RevokeShare`, `TransferOwnership`, `UnlinkChildRecord`, form registration/publication kinds and the entire content stream. They are record-scoped, and their payloads carry record ids and share field lists.
- **Per-entry projection, built inside the Authority field by field (never the raw body):**
  * stream, stream revision and event or command kind;
  * command state, committed authorization revision and rejection code;
  * actor and target Authority user ids or team id;
  * created and effective time, and reason text (D8-67);
  * archive state (Pending, Anchored or VerificationFailed), whether a receipt reference exists, and the entry digest as hex.
- **Keyset:** `(journal_created_on, stream, stream_revision)`. The business wraps it in its opaque cursor.

### 5.3 AC-8.3 Audit-access evidence (check-and-record)

`POST /internal/v1/organizations/audit/access` with body `{access_kind: ExplorerPage | EventDetail | CommandTimelinePage, filter_digest, audit_event_id?, result_count, correlation_id}`. The filter digest is SHA-256 of the canonical filter. Filters hold only ids, codes and dates, never free text.

1. `ActorResolver` binds the actor and organization.
2. A fresh `view_audit` check runs (gate open required).
3. The organization row is locked, then `AuditJournal.Append("AuditAccessed", body)` runs and the transaction commits.
4. The decision and the journal revision are returned.

A denial appends `AuditAccessDenied` (best effort; the denial itself never depends on it). If the append fails, the business receives no allow, so the read fails closed. The business adds `AUDIT_EVENTS_VIEWED`, `AUDIT_EVENT_VIEWED` or `SECURITY_COMMAND_TIMELINE_VIEWED` to its own projection after a successful read.

*Trade-off:* one Authority write per page view. The alternative, a business-only row, is cheaper but erasable by a BusinessDb DBA, which is exactly the threat this evidence exists for (D8-63).

### 5.4 AC-8.4 Security overview read

`POST /internal/v1/organizations/security-overview`. It requires `administer_security`, except the `archive` section, which accepts `administer_security` or `view_audit` (D8-70). It is authorized from the **desired-state graph**: the actor must hold the role edge, the edge's creating command must be Effective, and no committed removal may exist. This lets the page work while the gate is closed (R-10).

| Section | Content (counts and kinds only) |
|---|---|
| `gate` | Authorization revision, projection-applied revision, content-integrity revision, gate state, integrity quarantine flag |
| `commands` | Counts by state (Pending, CommittedAwaitingProjection, FailedReconciliation) and oldest pending age; up to 20 non-Effective administrative commands (kind, state, revision, created time, age), with no payload or target |
| `archive` | Per stream: counts by delivery state, oldest pending age, last anchored time. The label states the archive is the local development archive, not Azure Confidential Ledger. |

Search approvals reuse the existing `POST /internal/v1/field-search/administration` (no change).

### 5.5 AC-8.5 Coverage read (DR-C7-5)

Exactly as `FIELD_PROTECTION_RECONCILIATION.md` §13: `POST /internal/v1/field-protection/coverage`, `administer_security`, otherwise 404. It returns one row per current protection decision with counts, attestation id, journal revision and time, and Stale/Missing states. It shows no record ids or values, performs no SpiceDB record lookup and never calls the verifier routes. The UI carries the C-75 wording: "current versions only … DR-C7-4".

### 5.6 Authority migration and recovery

- **AuthorityDb:** no new table. Membership requests stay constrained to `designer` (`ck_membership_request_role` unchanged). Auditors cannot be requested through membership; they are only appointed.
- **SpiceDB:** additive schema change, written at start-up (F-13).
- **Recovery:** the DR regression proves that a revoked auditor is not re-granted by an older snapshot, as in AC-7 T16. This adds no new DR claim.

## 6. Business contracts (`/api/v1`, owner feature `Audit`, PG-052 owned by `SecurityOperations`)

### 6.1 Endpoints

| Route | Permission | Notes |
|---|---|---|
| `GET /api/v1/security/audit-events` | `view_audit` + AC-8.3 | Filters: `from`, `to`, `actorUserId`, `action`, `category`, `outcome`, `entityType`, `entityId`, `formRecordId`, `correlationId`, `excludeAuditAccess`. `pageSize` is at most 50, plus a cursor. |
| `GET /api/v1/security/audit-events/{auditEventId}` | `view_audit` + AC-8.3 | Another organization's id and an unknown id get the same 404 |
| `GET /api/v1/security/audit/security-commands` | `view_audit` (AC-8.2 records itself) | Cursor, `kind`, `state`, `from`, `to` |
| `GET /api/v1/security/overview/{section}` | Per §5.4 | `section` is one of `gate`, `commands`, `archive`, `search-approvals`, `incidents` (existing summary, `administer_security`) or `coverage` (AC-8.5). Each section is independently refreshable (PB-07). |

- **Problems (RFC 9457):**
  * 403 `AUDIT_VIEW_FORBIDDEN` or `SECURITY_ADMINISTRATION_FORBIDDEN`;
  * 404 generic;
  * 400 `CURSOR_INVALID`;
  * 503 `AUTHORITY_UNAVAILABLE` or `AUDIT_EVIDENCE_UNAVAILABLE`;
  * 429 rate limited.
- All responses carry `Cache-Control: no-store`. The routes generate OpenAPI and TypeScript clients, and the frontend uses the coordinator only.
- **Capability:** `audit.view` is added to `DashboardHandler.CapabilityNames` from the same batch check (navigation only).

### 6.2 Response shapes

- **List item (envelope):**
  * `auditEventId`, `occurredOn` (UTC) and `sequence`;
  * `category`, `action`, `actionLabel` (catalog) and `outcome`;
  * `actor {userId, displayName, actorType, roleBadges}`;
  * `target {entityType, entityId}`, `formDefinitionId` plus `formDisplayName`, `formRecordId` (id only) and `fieldDefinitionId` plus `fieldDisplayName`;
  * `correlationId`, `schemaVersion`, and `evidence: "BusinessProjection"`.
- **Page:** `items`, `nextCursor`, `orderingStatement` ("event time, then sequence; not commit order") and `evidenceStatement`.
- **Detail:** the envelope plus:
  * `metadata[]` of `{key, label, kind: Identifier | Count | Status | DisplayName | FreeText, value}`;
  * `omittedMetadataCount`;
  * `description`, either catalog text or stored text when the catalog marks it safe;
  * `links[]`, each pointing at a normal route that is authorized separately;
  * `receipt: {state: "None", reason: "AUD-C7-01/G24"}`.

### 6.3 Redaction catalog (`Application/Features/Audit/AuditEventCatalog.cs`)

- Each action has a label, category, description policy (`Catalog` or `StoredSafe`) and a metadata allowlist (`key → kind`). Rendering emits only allowlisted keys, which closes R-1 by construction.
- Rules for the kinds in D8-67: `FreeText` (reasons) is shown with a "text entered by the actor" label. `DisplayName` covers form, field and team names. Identity subject identifiers are shown in detail only. Design diffs show a change summary (counts and field ids), not labels or option texts. Record titles are **never** shown.
- Tests:
  * reflection: every `AuditActions` constant has an entry;
  * each allowlist key exists at some call site, which catches stale keys;
  * a forged row with unknown keys or actions renders as envelope only.

### 6.4 Frontend

- `apps/web/src/features/audit` (new): `AuditExplorerPage` (filters, DataTable "Audit events list", Pager, evidence banner) and `AuditEventPage` (structured detail, copy id, separately authorized links).
- `security-operations`: `SecurityHomePage` with per-widget cards, each with its own refresh.
- Routes: `/app/security` (either permission; widgets self-gate), `/app/security/audit` and `/app/security/audit/$auditEventId`.
- Navigation: "Security" shows when either capability is present. An auditor-only user sees Audit, and an administrator-only user sees the home without audit links.

## 7. Migrations

| Database | Change | Note |
|---|---|---|
| Business | `ix_audit_events_tenant_actor_time (tenant_id, actor_user_id, event_occurred_on, audit_sequence)` | Actor filter (F-7) |
| Business | `ix_audit_events_tenant_correlation (tenant_id, correlation_identifier)` | Correlation view |
| Business | `ix_audit_events_tenant_record_time (tenant_id, form_record_id, event_occurred_on, audit_sequence)` **only if** `EXPLAIN (ANALYZE, BUFFERS)` on a seeded 1M-row organization shows the existing record index is insufficient | Measured, not assumed |
| Business | No table, no grant change (SELECT and INSERT already exist; re-verified by the privilege inventory). New action constants are code only. | — |
| AuthorityDb | None | §5.6 |
| SpiceDB | Additive relation and permission | §5.6 |

The migrations are additive and reversible (drop index), and they are tested on the disposable security-test stack before the development stack.

## 8. Tests

**Authority adversarial tests (AC8-T, real Authority, both PostgreSQL databases, SpiceDB and Vault; `SecurityAuditorTests` in shard `authorization-security-onboarding`):**

| Test | Scenario | Expected |
|---|---|---|
| T01 | A Security Administrator appoints an auditor | Effective; `view_audit` allowed; no `administer_security`, `read`, `create_form` or `manage_configuration` |
| T02 | Self-appointment and self-removal (auditor role) | 403 `SELF_CHANGE_FORBIDDEN` |
| T03 | An owner, designer or auditor appoints | 403 |
| T04 | Target in another organization, or no binding | 403/404 with no existence hint |
| T05 | An auditor opens a record, document, history or search | Denied by the normal checks |
| T06 | A revoked auditor's next page or detail request | 403 |
| T07 | A BusinessDb DBA forges an auditor (users row, audit row or session row) | No effect |
| T08 | Timeline payload sanitization: no share field lists, record ids, step-up evidence or predicates | Verified field by field |
| T09 | Cross-organization timeline, overview or coverage | Denied or 404 |
| T10 | AC-8.3 append failure (fault injection) | Read refused 503; no data returned |
| T11 | Audit access recorded in the Authority audit stream with the correct actor, kind and filter digest; a DBA deleting the business row leaves the Authority entry | Verified |
| T12 | Overview with the gate closed (pending command) | Administrator can read gate/commands; a pending grant does **not** authorize; a committed revoke denies |
| T13 | Disabled auditor (AC-7) | Refused; re-enable restores |
| T14 | Dual holder | Both badges; holds both permissions; cannot self-remove either role |
| T15 | Step-up gate unavailable (production configuration) | Appointment refused |
| T16 | Recovery replay from an older snapshot after revoke | Not re-granted |
| T17 | Stale revision, replayed idempotency key with a different payload | 409 / `IDEMPOTENCY_CONFLICT` |

**Business integration tests:**

- `AuditExplorerTests` (shard `forms-documents`, the lightest at about 180 s):
  * keyset stability under concurrent inserts, with no duplicates or gaps across pages;
  * every filter;
  * cursor tampering, and a cursor reused by another actor, organization or filter;
  * another organization's id returns 404;
  * the `tenant_id` predicate holds with a second organization's rows present (R-15);
  * a sentinel leak test: Confidential and Public field values, a PIN, a file name and a search term are created, then every explorer and detail response is scanned for them;
  * a forged row is rendered as envelope only;
  * audit-of-audit rows are recorded.
- `SecurityHomeTests` (shard `authorization-security-onboarding`): per-section authorization, counts-only responses, coverage Stale/Missing.
- Both new classes are added to `stage-b-groups.json`; the plan job enforces exactly-once.

**Unit tests and frontend:**

- Catalog completeness and allowlist tests; cursor binding; AC-8 planner rules (Authority unit tests).
- Vitest for the explorer filter model and widget states.
- Playwright `security-audit.spec.ts` against the **isolated security-test stack only**, with the Playwright guard:
  * auditor-only, administrator-only and dual-holder flows;
  * revocation mid-session;
  * axe;
  * 320/768/1440 px and 200%/400% zoom.

**Local verification per milestone:**

- `scripts/dev/stage-b.ps1 -Class <Class> -Fresh` for new classes and `-Group <group>` for the affected shard;
- `scripts/ci/run_dotnet_tests.py`, Vitest, ESLint, typecheck, and OpenAPI regeneration with a drift check.

**Hosted, on each milestone's final commit:** the seven Stage B shards plus the aggregate, `ci`, `level3-script` and CodeQL. The owner dispatches `security-gate-a`. Unrun tests are never reported as passing.

## 9. Milestones (independently testable)

| Milestone | Content | Authority | Gate to start |
|---|---|---|---|
| **M4-0** | This contract (done). Then: privilege inventory re-run, seeded EXPLAIN baseline, sentinel harness design | No | — |
| **M4-1** | AC-8.1: schema, `ViewAudit`, commands A1–A9, rosters, People-page appoint/remove and badges, business actions, AC8-T01–T07 and T13–T17 | **Yes** | D8-62, D8-68 |
| **M4-2** | Redaction catalog plus AC-8.3 plus PG-054/055, business indexes, `audit` feature pages; T10, T11, `AuditExplorerTests` | **Yes** (AC-8.3) | D8-63, D8-67, D8-69, M4-1 accepted |
| **M4-3** | AC-8.2 timeline and its UI tab; T08, T09; measured lock contention (R-7) | **Yes** | D8-64, M4-2 accepted |
| **M4-4** | AC-8.4 and AC-8.5 plus the PG-052 home with independent widgets; T12, `SecurityHomeTests` | **Yes** | D8-65, D8-66, D8-70 |
| **M4-5** | Accessibility, hosted verification, status and evidence, reviewer package update (AC-8 added) | No | M4-1…M4-4 |

Each milestone ends with local verification, a signed commit, the status-log section and owner acceptance before the next one begins. Nothing is merged without the owner's decision.

## 10. Owner decisions required (C-78 condition 8)

| ID | Decision | Recommendation |
|---|---|---|
| D8-62 | Approve AC-8.1 (relation, `view_audit`, `GrantSystemRole` extension, rules A1–A9) | Approve |
| D8-63 | Audit-access evidence: (a) Authority check-and-record (AC-8.3), fail closed; (b) business projection only | **(a)** |
| D8-64 | AC-8.2 scope: administrative kinds, including Rejected, plus allowlisted audit-stream kinds. Record, share, ownership and content kinds are excluded. | Approve |
| D8-65 | AC-8.4 authorized from desired state with the Effective-edge rule, so it works while the gate is closed | Approve |
| D8-66 | Confirm AC-8.5 as designed in DR-C7-5 §13 (D8-7 approved the view; this approves the endpoint) | Approve |
| D8-67 | Redaction: show free-text reasons (labelled), form, field and team display names, record **ids** (never titles or values), identity subject identifiers in detail only, and the platform-operator display reference | Approve |
| D8-68 | Auditor roster visibility: owners and Security Administrators, like Security Administrators | Approve |
| D8-69 | No step-up on audit reads until PB-08 (residual SD-5); appointment keeps the existing gate | Approve as residual |
| D8-70 | PG-052 accessible with either permission; widgets authorized separately; auditor sees archive health only | Approve |

## 11. Questions for the independent reviewer

- **Q-1:** Is AC-8.3's per-read organization-lock append acceptable, or should audit-access entries use a lock-free append path in a separate stream?
- **Q-2:** Should AC-8.3 also bind the business result count (supplied by the business, so unverifiable) or omit it?
- **Q-3:** Is the Effective-edge rule (§5.4) a sound authorization basis while the SpiceDB gate is closed?
- **Q-4:** Should the explicit self-change refusal (A2) also be applied to `system_administrator`? That changes C-70 behaviour only in wording, because the outcome is already a refusal.
- **Q-5:** Is the AC-8.2 kind allowlist complete for governance review without disclosing record-scoped data?

## 12. Carried forward unchanged

G06, G08 (including missing business RLS, F-10), G19, G20, G21, G24/AUD-C7-01, G25, G26, PB-08, PB-09, DR-C7-1…6, SD-1…SD-6, the C-74 development incident remediation, the AC-5/AC-6/AC-7/AC-9 independent review and production Level 2 all stay **open**. M4 does not make the business audit projection tamper-proof, does not archive it, and makes no Level 2 or production claim.
