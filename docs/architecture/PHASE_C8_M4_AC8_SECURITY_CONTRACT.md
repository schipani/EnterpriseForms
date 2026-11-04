# Phase C8 M4: governance audit and security home (AC-8 architecture and security contract)

- **Status:** **revision 2 (C-109, 2026-11-04), accepted with TC-1…TC-5 in C-110. M4-1 (AC-8.1 only) implemented for
  Development/Testing (C-111; `docs/PHASE_C8_STATUS.md` §34): not pushed, not merged, not independently reviewed.**
  AC-8.2–AC-8.5, the audit pages and the Security Home are not built. Revision 1 was C-108.
- **Baseline:** `main` = `c245200` (PR #50, merged 2026-11-04 09:50 UTC; it includes C8 M1–M3 and AC-7). Branch
  `phase-c8/m4-governance-audit` was created from that commit and is local only (not pushed, no PR).
- **Audience:** the project owner (final acceptance) and the independent Authority reviewer (AC-8 joins the
  AC-5/AC-6/AC-7/AC-9 package). The owner's decisions are owner decisions, not independent security approval. This
  document is by Claude and is **not** an independent review.
- **Supersedes:** revision 1, and the M4 sketch in `PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §4 (M4) and §5.5. C-78's
  **D8-6** (separate `security_auditor` relation appointed by Security Administrators; holding both roles is allowed and
  shown) and **D8-7** (read-only DR-C7-5 coverage view) are preserved.
- **Corrections from revision 1:**
  * **E-1 (F-2, Q-4):** revision 1 said that Security Administrator self-change refusal "changes C-70 behaviour only in
    wording". **Wrong:** self-**removal** is allowed today and covered by tests (F-15). D8-62a now changes this
    deliberately.
  * **E-2 (§5.3):** revision 1 described AC-8.3 as a check and a record "in one Authority transaction". **Wrong:** the
    SpiceDB check is a network call outside AuthorityDb (F-17), so check and record cannot be atomic. §5.3 now
    specifies a revision-compare protocol and claims no atomicity across SpiceDB and AuthorityDb.

## 1. Verified facts (2026-11-04, `c245200`)

| # | Fact | Where |
|---|---|---|
| F-1 | The `organization` definition has `organization_owner`, `system_administrator`, `designer` and `projection_cursor`. Its permissions are `manage_configuration`, `administer_security` and `create_form`. There is no audit relation or permission. | `src/authority/AuthorizationAuthority.Infrastructure/SpiceDb/authority.schema.zed` |
| F-2 | `GrantSystemRole`/`RevokeSystemRole` accept only `system_administrator`. They require an actor who is a Security Administrator, a free-text reason (1–500 characters) and step-up (`FieldSearchApprovalGate.RequireStepUp`: synthetic in Development/Testing, refused in staging/production). They keep the last enabled administrator. **Self-appointment** is refused only incidentally: the actor is already an administrator, so the "already a Security Administrator" check fires. **Self-removal is allowed** while another enabled administrator remains (F-15). The planner has no self-change rule. | `CommandPlanner.SecurityAdministrators.cs` |
| F-3 | AC-7 `GrantOrganizationRole` administers only `organization_owner` and `designer` (`AdministeredRoles`), for owners only, with `SelfChangeForbidden`. | `CommandPlanner.Membership.cs` |
| F-4 | `PermissionCheckService.MapCheck` maps the organization actions `ManageOrganization`, `AdministerSecurity` and `DesignForm`. Any unmapped action is `Invalid`. | `PermissionCheckService.cs` |
| F-5 | The Authority journal has three streams: `authorization`, `content` and `audit`. Entries are hash-chained (`JournalEventDigest`, `JournalPreviousDigest`) and delivered by `ArchiveProcessor` (`ArchiveDeliveryState` is Pending, Anchored or VerificationFailed, plus a receipt reference and digest). The audit stream does not gate access (OD-8). It already records platform-plane reads (`PlatformOrganizationDirectoryViewed`, `PlatformOwnerRecoveryCaseViewed`, `PlatformMembershipRequestViewed`). `AuditJournal.Append` requires the caller to hold the organization row lock and commits with the caller's transaction. | `AuditJournal.cs`, `SecurityJournalEntry.cs` |
| F-6 | The business API can read a security command only by id, and only as its originating actor (`GET /internal/v1/security-commands/{id}`). Gate status is exposed only on the operator plane. No delegated-user route lists commands, gate state, archive state or protection coverage. | `SecurityCommandEndpoints.cs`, operator-plane endpoints |
| F-7 | Business `audit_events` is an append-only projection. The runtime role is insert-only (`BusinessRuntimeGrants.InsertOnlyTables`), but a BusinessDb DBA can insert, alter or delete rows. `ix_audit_events_tenant_time (tenant_id, event_occurred_on, audit_sequence)` covers the default ordering. `ix_audit_events_record_time (form_record_id, event_occurred_on)` has no tenant column. There is no actor index and no correlation index. | `AuditEntityConfigurations.cs`, `BusinessRuntimeGrants.cs` |
| F-8 | `AuditRecorder` writes a free-form `EventDescription` (≤500 characters) and anonymous metadata serialized as JSON, from about 40 call sites. The metadata keys include: **free text** (`reason`, `changeReason`, `requestReason`, `retryReason`, `display_name_hint`); **directory display names** (`formDefinitionName`, `previousFormDefinitionName`, `teamName`, `previousTeamName`, `previousDisplayName`, `newDisplayName`); **designer-chosen identifiers and labels** (`fieldKey`, `groupKey`, `key`); **form-design diffs** (`changes`, `elements`, `items`, `groupChanges`); and **identity identifiers** (`subjectIdentifier`, `issuer_uri`). The inventory found no field-value keys, but write-time redaction is a convention, not a control. | `AuditRecorder.cs`, call-site inventory |
| F-9 | Business audit rows have no archive receipt (AUD-C7-01/G24 open). The `audit_event_types` table listed in the ownership matrix does not exist; actions are code constants (`AuditActions`). | `FEATURE_OWNERSHIP_MATRIX.md`, `AuditEvent.cs` |
| F-10 | **The business database has no PostgreSQL row-level security.** No migration, script or code contains `ENABLE ROW LEVEL SECURITY`, `CREATE POLICY` or `set_config`. Tenant isolation on `audit_events` is only the application's `tenant_id` predicate. This predates M4 (G08 and §4C.6 are open). The explorer would be the first organization-wide reader of every record event. | repository search |
| F-11 | Session capabilities come from one Authority batch check per request (`DashboardHandler.CapabilitiesAsync`) and drive navigation only. | `DashboardHandler.cs` |
| F-12 | DR-C7-5 is designed but not built: `POST /internal/v1/field-protection/coverage` requires `administer_security` (404 otherwise) and returns counts only. | `docs/security/FIELD_PROTECTION_RECONCILIATION.md` §13 |
| F-13 | The SpiceDB schema is embedded and written at Authority start-up. A new relation is additive, and recovery replay rebuilds edges from the journal. | `SpiceDbProjectionClient.cs` |
| F-14 | Authority search-execution evidence (`FieldSearchExecuted`) carries snapshot, form, user, field ids, operators, condition count, evaluated and matched counts, timing and revision. It never carries predicates or values. | `SearchCatalogueService.cs` |
| F-15 | Security Administrator self-removal is intended, specified and tested behaviour today:<br>• spec rule 7: "self-removal is allowed while one does";<br>• Authority unit test (self-removal while another administrator remains);<br>• Stage B test (the clerk removes themself);<br>• People directory action hints. | `docs/security/SECURITY_ADMINISTRATOR_MANAGEMENT.md` §rule 7; `tests/authority/.../SecurityAdministratorTests.cs:115-124`; `tests/integration/.../SecurityAdministratorTests.cs:260`; `GetMemberDirectoryHandler` |
| F-16 | Desired-state edges record `CreatedBySecurityCommandId` and `RemovedBySecurityCommandId`. The removal is set when the revoking command commits, inside the organization transaction. `DesiredStateGraph.HasOrganizationRole` ignores removed edges. | `AuthorityRelationshipEdge.cs`, `DesiredStateGraph.cs` |
| F-17 | A permission check runs: gate evaluation, then a fresh SpiceDB check at the Authority-held token, then a gate re-read (revisions and quarantine compared), with up to 2 retries. It does **not** hold the organization row lock, and the SpiceDB call is outside any AuthorityDb transaction. | `PermissionCheckService.cs` |
| F-18 | The archive is `BlobSecurityEventArchive`: InMemory for unit tests and local Development, Azurite on the Authority network in Docker. Azure immutable storage and Confidential Ledger are production designs that are not deployed. | `BlobSecurityEventArchive.cs` |
| F-19 | The only Authority rate limiter is the token bucket on the AC-9 outcome endpoint. There is none on permission checks or reads. | `SecurityCommandOutcomeEndpoints.cs` |

## 2. Scope

| Item | Delivers | Authority change |
|---|---|---|
| **AC-8.1** Security Auditor role | `organization#security_auditor` and `view_audit`; appointment and removal through `GrantSystemRole`/`RevokeSystemRole` with structured reason codes; Security Administrator self-removal refused (D8-62a) | Yes |
| **AC-8.2** Security-command timeline read | Restricted envelopes of security and governance commands (including Rejected and FailedReconciliation) and allowlisted audit-stream entries, with archive state | Yes (read and audit-access record) |
| **AC-8.3** Audit-access evidence | `view_audit` check, then a revision-compared `AuditAccessAuthorized` entry in the Authority audit stream. Authorization only, never delivery. | Yes (audit-stream write) |
| **AC-8.4** Security overview read | Gate, command and archive health, authorized by the Effective-edge rule | Yes (read) |
| **AC-8.5** Coverage read (DR-C7-5) | Counts-only coverage, authorized by the Effective-edge rule | Yes (read) |
| **PG-054** Audit explorer | Business endpoints and pages over `audit_events` | No (uses AC-8.1/8.3) |
| **PG-055** Audit event detail | As above | No (uses AC-8.1/8.3) |
| **PG-052** Security administration home | Independently refreshing widgets | No (uses AC-8.4/8.5) |

**Out of scope:**
- audit export (ADR-PB-013);
- PG-053 ownership transfer;
- incident disposition (SD-1/AC-10);
- archiving business audit rows (AUD-C7-01/G24);
- incident-note encryption (SD-4);
- business RLS (G08);
- real step-up (PB-08);
- reason codes for commands other than auditor appointment and removal;
- any additional approval workflow for auditor tenure (RR-3);
- staging and production enablement.

## 3. Security risks and treatment

| # | Risk | Treatment |
|---|---|---|
| R-1 | **Audit as a content side channel** (F-8). An auditor is not a record reader. | Read-time allowlist catalog in code (§6.3); D8-67/D8-67a defaults; unknown keys dropped; unknown or forged actions shown as envelope only; descriptions from the catalog unless marked safe; reflection completeness test; sentinel leak tests (§8). |
| R-2 | **A business DBA forges, alters or deletes audit rows** (F-7). | Never claimed as evidence. Business rows are labelled "Business projection — not independently verified (AUD-C7-01/G24 open)" and never carry a verified badge. |
| R-3 | **A forged auditor in BusinessDb** (role row, `users` flag, session flag). | Permission comes only from the Authority (`view_audit` in SpiceDB, or the Effective-edge rule for §5.4). Business rows confer nothing (AC8-T07). |
| R-4 | **The auditor reaches record content** through links, ids, titles or joins. | No record titles, values, file names, share or document scopes, field keys or labels. Record links go to normal routes, which authorize on their own. Accepted inherent disclosure: record **ids**, existence and activity patterns (D8-67). |
| R-5 | **Privilege escalation through appointment.** | Only an enabled Security Administrator may appoint or remove. No self-change for either role (A2, D8-62a). Owners, designers and auditors cannot appoint. Residual: two administrators can appoint each other as auditors, and a dual holder is shown (D8-6). |
| R-6 | **Audit access silently erased.** A business-only row is DBA-deletable. | AC-8.3: an Authority `AuditAccessAuthorized` entry must commit before any data is served. Failure is fail-closed 503. The business row is an additional projection. |
| R-7 | **Audit reads starve security commands**, especially revocations (F-5). | §5.3: the audit-access transaction takes the organization lock `NOWAIT` with a bounded hold, bounded retries, then 503. Security commands keep their normal lock wait. Per-user and per-organization rate limits; contention telemetry; revocation-latency acceptance test (AC-M4-2.6). |
| R-8 | **Cursor, filter or id tampering; cross-tenant access.** | Organization from the session only. Cursors bound to organization, actor and filter digest (`IOpaqueCursorProtector`). Every page re-authorized. Another organization's id gets the same 404 as an unknown id. |
| R-9 | **Revocation during paging.** | No permission caching. Every page and detail request re-checks, and §5.3 refuses when the authorization revision moved between check and record. A residual window remains between the Authority's answer and the business query, as for every read in the system. |
| R-10 | **A closed gate hides the security home** (§4N). | The Effective-edge rule (§5.4), for the overview and coverage only. The explorer and timeline stay fail-closed while the gate is closed (R-17). |
| R-11 | **Counts leak record facts.** | Overview and coverage carry counts and command kinds only. Search-execution matched and evaluated counts are withheld (D8-67a). |
| R-12 | **Platform-plane reads of the organization.** | Shown to auditors with the operator's display reference and no credentials. |
| R-13 | **Event time read as commit order.** | Always labelled "event-time order with sequence tie-break, not commit order"; timestamp source stated. |
| R-14 | **No step-up on audit reads** (PB-08 unavailable). | Documented interim risk SD-5 (D8-69). Appointment and removal keep the existing step-up gate (refused in staging/production). |
| R-15 | **Missing business RLS** (F-10). | One organization-bound query adapter, organization-bound cursors, second-organization negative tests. RLS stays G08 and is not claimed. |
| R-16 | **Audit-of-audit noise.** | Audit-access events are listed like any other (auditors see each other's reads); a filter can exclude them. |
| R-17 | **The explorer is unavailable while the gate is closed**, including a prolonged `FailedReconciliation`. | Accepted fail-closed behaviour (owner Q-3 decision restricts the Effective-edge rule). The overview shows the gate state, and the explorer reports 503 `AUTHORITY_GATE_CLOSED`. |
| R-18 | **Evidence claims more than happened.** | The Authority entry records authorization only (`delivery: not_attested`). The business projection records the delivery outcome, labelled as business-reported (§5.3). |

### 3.1 Residual risk register (M4)

| # | Residual | Status |
|---|---|---|
| RR-1 | Business audit rows can be forged, altered or deleted by a BusinessDb DBA. They have no receipt. | Open (AUD-C7-01/G24) |
| RR-2 | No business RLS. Isolation depends on application predicates. | Open (G08) |
| RR-3 | **Auditor tenure is controlled by the people audited.** A Security Administrator may remove any auditor, including one reviewing that administrator. An organization owner may disable an auditor's membership (AC-7 `DisableIdentityBinding`). If the last auditor is removed, nobody in the organization can read the explorer or timeline until an administrator appoints another. Removal and disablement are journaled in the authorization stream with actor, target and reason code, and the entries remain in AuthorityDb and the archive, but **nothing prevents them**. No additional approval workflow is designed. Any mitigation (dual administrator removal, owner or operator notification, platform-plane visibility) needs a separate design decision. | Open, documented (owner direction) |
| RR-4 | No step-up for audit reads until PB-08. | Accepted interim (D8-69, SD-5) |
| RR-5 | Auditors see record ids, existence and activity patterns, and directory display names. | Accepted by design (D8-67, D8-67a) |
| RR-6 | The local archive is InMemory or Azurite (F-18). Neither is immutable, WORM or Confidential Ledger. Local "Anchored" means an Azurite receipt only. | Open; no production immutability claim |
| RR-7 | An Authority database administrator could alter the journal; the hash chain detects it only against an external anchor. Separate production administration of AuthorityDb is unproven. | Open (G08, production) |
| RR-8 | An authorized read can be served in the gap between the Authority's answer and the business query, even if a revocation commits in that gap. | Accepted, system-wide (R-9) |
| RR-9 | A recorded `AuditAccessAuthorized` entry does not prove delivery. A business-reported delivery outcome is unverifiable. | By design (R-18) |
| RR-10 | Per-organization audit-access rate limits are per Authority instance. With several instances the effective limit multiplies. | Open; documented with the limits |
| RR-11 | **T16S-1:** restoring SpiceDB's datastore from an older backup makes revoked authorizations effective again (auditor, designer, and generally every SpiceDB-resolved permission) with the gate `Ready` | **Open, High** (pre-existing; C-114). S1 approved as a direction (D8-77); Design B revision 2 verified on the security-test stack (0 stale allows versus 106,058 today) and awaits final approval (B-Q2, B-Q5…B-Q9, C-116); residual RE-1 is not eliminable by protocol (deployment invariant I3); T16-S stays failing (D8-78). |
| RR-12 | **RN-1:** the auditor note was plaintext in AuthorityDb and the archive | **Contained** by D8-72 (notes refused); M1 conditionally approved (D8-76); padding and no-presence approved; contract revision 2 with a cross-checked KAT awaits final approval (A-Q2, C-116) |
| RR-13 | **RCF-1:** rejected commands of other kinds keep the raw request, step-up evidence included | Open, Low now and latent High; must be fixed before PB-08 (D8-75) |
| RR-14 | **T16-A:** full Authority recovery (AuthorityDb + SpiceDB restored) is untested and needs the G14 recovery plane | Open; production-readiness blocker (D8-74) |

## 4. Permission boundaries

| Principal | PG-054 / PG-055 | AC-8.2 timeline | PG-052 widgets | Coverage | Appoint / remove auditor | Record content through audit |
|---|---|---|---|---|---|---|
| Security Auditor (`view_audit`) | Yes | Yes | Archive health only (D8-70) | No | No | **Never** |
| Security Administrator (`administer_security`) | **No** (D8-6) | No | Yes | Yes | Yes, never themself | Never |
| Both roles | Yes | Yes | Yes | Yes | Yes, never themself (either role) | Never |
| Organization owner | No | No | No | No | No (sees the roster, D8-68) | Never |
| Designer or member | No | No | No | No | No | Never |
| Platform Operator | No (operator plane only) | No | No | No | No | Never |
| BusinessDb DBA | Raw rows (RR-1) | No | No | No | No effect (R-3) | Raw plaintext rows (existing residual) |

Seeing the auditor roster grants nothing (D8-68). My Activity and Record Activity are unchanged.

## 5. AC-8 Authority contract

### 5.1 AC-8.1 Security Auditor relation, commands and reason codes

```zed
definition organization {
    ...
    relation security_auditor: user
    permission view_audit = security_auditor
}
```

**Enums and mapping:**
- `OrganizationRoleNames.SecurityAuditor = "security_auditor"`.
- `RequestedAction.ViewAudit` maps `(Organization, ViewAudit)` to `view_audit`.
- The business enum gains `ProtectedAction.ViewAudit`.

**Payloads** (`SecurityAdministratorPayload`; canonical JSON, unknown fields rejected):

| `role_name` | Payload | Reason rules |
|---|---|---|
| `system_administrator` | Unchanged: `authority_user_id`, `role_name`, `reason` (1–500, required), `step_up_evidence` | `reason_code` is rejected (C-70 contract unchanged apart from D8-62a) |
| `security_auditor` | `authority_user_id`, `role_name`, **`reason_code`** (required), optional `reason_note` (0–500), `step_up_evidence` | The code must belong to the list for the direction (below). `reason` is rejected. |

**Reason codes** (proposed list; the owner may amend it before M4-1):
- Appointment: `AUDIT_COVERAGE_ASSIGNMENT`, `INVESTIGATION_SUPPORT`, `REPLACEMENT_OF_DEPARTING_AUDITOR`, `COMPLIANCE_REVIEW`.
- Removal: `ASSIGNMENT_ENDED`, `ROLE_CHANGE`, `MEMBERSHIP_ENDING`, `CONFLICT_OF_INTEREST`, `SECURITY_CONCERN`.

The optional `reason_note` is journaled in the authorization stream for the command, and the originating actor can read it through command status. It is **never** shown in AC-8.2, never copied to business audit metadata and never shown to auditors (D8-67).

| # | Rule |
|---|---|
| A1 | The actor must be an enabled `system_administrator` of the same organization. Owner, designer and auditor all get 403. |
| A2 | **No self-change for either role** (D8-62a): an actor may not appoint or remove themself as `security_auditor` or `system_administrator` (403 `SELF_CHANGE_FORBIDDEN`). This is an intentional change to C-70: self-removal of a Security Administrator, allowed today (F-15), is refused. |
| A3 | The target must be an enabled identity binding of the same organization (`RequireUserBindingAsync`). Removal also works for a disabled identity. |
| A4 | Appointing a current holder, or removing a non-holder, returns `INVALID_COMMAND`. |
| A5 | No last-holder rule for auditors: zero is allowed. The `system_administrator` last-holder rule is unchanged. |
| A6 | Step-up through the existing gate (synthetic in Development/Testing, refused in staging/production). |
| A7 | The existing pipeline: idempotency key with request digest, expected revision, journal, desired state, outbox, SpiceDB projection, the Effective gate and recovery replay. The normalized payload drops step-up evidence and keeps `role_name`, target, `reason_code` and `reason_note`. |
| A8 | Holding both roles is allowed (D8-6). Other `system_administrator` rules (C-70, G25) are unchanged. |
| A9 | AC-7 disable keeps the edge (R7). A disabled auditor is refused by `ActorResolver`; re-enabling restores the edge unchanged. |

**Journal evidence (explicit):**
- **Committed command:** the authorization-stream entry with kind `GrantSystemRole`/`RevokeSystemRole`, actor binding, target, `role_name`, `reason_code` and committed revision.
- **Refused attempt:** an audit-stream entry `SecurityAuditorCommandRejected` (command id, kind, actor binding, target, `role_name`, rejection code, correlation; never the note), mirroring `SecurityAdministratorCommandRejected`.
- **Business projection:** `SECURITY_AUDITOR_APPOINTED`/`SECURITY_AUDITOR_REMOVED` with `reasonCode` only.

**Roster:**
- `OrganizationRosterService` and `SecurityAdministratorRosterService` add `security_auditor`. The roster is visible to owners and Security Administrators (D8-68), and appointment candidates to Security Administrators only.
- The People page shows a "Security Auditor" badge; a dual holder shows both badges.
- Remove and appoint actions are never offered on the caller's own row, for either role.

### 5.2 AC-8.2 Security-command timeline read

`POST /internal/v1/organizations/audit/security-command-timeline` (business workload plus delegated user; organization bound by `ActorResolver`; body `{after?, page_size ≤ 50, kinds?, states?, from?, to?}`). It requires `view_audit` and follows the §5.3 protocol with `access_kind = CommandTimelinePage`. The page is read from AuthorityDb inside the same transaction as the access entry, so the entry and the data come from one AuthorityDb snapshot. That claim covers AuthorityDb only, not SpiceDB.

**Included kinds and their restricted envelopes.** Every state is included: Effective, CommittedAwaitingProjection, Pending, **Rejected** (with rejection code) and **FailedReconciliation**.

| Kinds | Envelope beyond the common fields |
|---|---|
| `GrantSystemRole`, `RevokeSystemRole`, `GrantOrganizationRole`, `RevokeOrganizationRole`, `ProposeOrganizationOwnerChange` | `role_name`, target user id, proposal id, `reason_code` where one exists |
| `DisableIdentityBinding`, `EnableIdentityBinding`, `RequestOrganizationMembership`, `CancelOrganizationMembershipRequest`, `ReplaceOrganizationMembershipRequest` | Target user id, request id, requested initial role |
| `RegisterTeam`, `AddTeamMember`, `RemoveTeamMember` | Team id, member user id |
| `UpdateFormUsers` | Form id, added and removed principal kinds and ids |
| `UpdateMandatoryPolicy`, `ReclassifyField` | Policy or form id, field id, previous and new classification codes |
| `ApproveFieldSearch`, `WithdrawFieldSearch`, `UpdateFieldSearchers` | Form id, field ids, searcher principal ids |
| **`TransferOwnership`** (Q-5 B) | Record id, previous and new owner principal kind and id |
| **`CreateShare`** (Q-5 B) | Record id, share id, recipient principal kind and id, access role (Reader/Contributor/Manager). **No field, group or document scope, no expiry.** |
| **`RevokeShare`** (Q-5 B) | Record id, share id |

- **Common fields:**
  * stream, stream revision, command id and kind, state, committed authorization revision, rejection code;
  * actor user id, created and effective time;
  * archive state (Pending, Anchored or VerificationFailed; local meaning per RR-6), whether a receipt reference exists, entry digest as hex.
- **Audit-stream entries (allowlisted):**
  * membership and provisioning approvals and rejections, owner recovery, `ProtectionReconciliationAttested`, `SecurityAlertRaised`, platform-plane reads, `*CommandRejected`, `AuditAccessAuthorized`;
  * `FieldSearchExecuted` with snapshot, form, user, field ids, operator names, condition count, outcome and revision. **Matched and evaluated counts are withheld** (D8-67a; evaluated counts are withheld as a conservative extension, because they reveal the size of a user's visible set).
- **Never included:** free-text `reason` values, `reason_note`, share field or document scope lists, step-up evidence, predicates, and the kinds `RegisterRecord`, `UnlinkChildRecord`, form registration and publication, and the whole content stream.
- **Construction:** envelopes are built field by field inside the Authority, never from a raw body. Keyset `(journal_created_on, stream, stream_revision)`, wrapped in the business opaque cursor.

### 5.3 AC-8.3 Audit-access evidence (revision-compared record; no cross-store atomicity)

`POST /internal/v1/organizations/audit/access`, body `{access_kind: ExplorerPage | EventDetail, filter_digest, page_size, audit_event_id?, correlation_id}`. The filter digest is SHA-256 of the canonical filter (ids, codes and dates only). Business-reported result counts are **not** sent or recorded (Q-2).

**Protocol:**
1. `ActorResolver` binds actor and organization; a disabled binding is refused.
2. A permission check for `view_audit` runs as today (F-17): gate, SpiceDB, gate re-read. It captures authorization revision *R* and projection revision *P*. A closed gate gives 503 `AUTHORITY_GATE_CLOSED`.
3. An AuthorityDb transaction begins with `SET LOCAL statement_timeout` (proposed 200 ms) and a bounded transaction lifetime: `SET LOCAL transaction_timeout` (proposed 500 ms; PostgreSQL ≥ 17, to verify on the Authority's server version, otherwise an equivalent application deadline).
4. The organization row is locked with **`FOR UPDATE NOWAIT`**, so an audit read never queues in front of a security command. If the row is locked: rollback, jittered backoff (proposed 20–80 ms), at most 3 attempts in total, then 503 `AUDIT_EVIDENCE_BUSY`.
5. Under the lock, the Authority verifies that the authorization revision is still *R*, projection still *P*, no integrity quarantine, and the binding still enabled. Any change means rollback and a return to step 2; that counts as an attempt.
6. `AuditJournal.Append("AuditAccessAuthorized", body)`. The body holds `access_kind`, actor binding, *R*, `filter_digest`, `page_size`, `audit_event_id`, `correlation_id`, and `delivery: "not_attested"` (or `served_by: "authority"` for AC-8.2).
7. Commit. Only a successful commit returns an allow decision with the journal revision. A commit failure, timeout or unknown outcome means **no allow** (fail closed, 503 `AUDIT_EVIDENCE_UNAVAILABLE`).

**Guarantees and non-claims:**
- No revocation can **commit** between the check and the record: security commands take the same lock, and the revision is compared under it.
- **There is no atomicity across SpiceDB and AuthorityDb.** The SpiceDB answer is bound to *R* only by the revision comparison.
- "Recorded" means **committed in AuthorityDb**. Archive delivery is asynchronous and non-gating, so a recorded entry may still be archive-Pending (RR-6).
- The entry records that access was **authorized**, not that data was delivered (RR-9).
- Security commands are not changed: they keep their normal lock wait. The worst case added to a command is one audit-access hold (≤ the transaction lifetime, expected a few milliseconds).

**Denials:** a denied check returns 403 at once. An `AuditAccessDenied` entry is attempted with the same `NOWAIT` rule but is best effort: when busy it is skipped and counted in telemetry. The denial never depends on it.

**Business side:**
1. Only after an allow does the business query `audit_events`.
2. In one business transaction, the business writes `AUDIT_EVENTS_VIEWED` or `AUDIT_EVENT_VIEWED` with `deliveryOutcome: Delivered`, the Authority journal revision and the correlation id.
3. If the query or the projection write fails, the business returns an error. It attempts a separate `deliveryOutcome: Failed` row, best effort.
4. The UI labels Authority evidence as "access authorized (Authority)" and the business outcome as "business-reported".

**Rate limits and telemetry:**
- Rate limits (proposed; to be tuned from measurements and documented):
  * per user: token bucket 5/s, burst 10;
  * per organization: 20/s per Authority instance (RR-10);
  * exceeded: 429.
- Telemetry (names indicative):
  * `authority_audit_access_lock_busy_total`, `authority_audit_access_attempts` (histogram), `authority_audit_access_hold_ms`;
  * `authority_audit_access_refused_total{reason}`, `authority_audit_access_denied_unrecorded_total`;
  * `authority_security_command_lock_wait_ms`, to observe impact on commands.

### 5.4 AC-8.4 Security overview read (Effective-edge rule)

`POST /internal/v1/organizations/security-overview?section=gate|commands|archive`.

**Effective-edge rule** (Q-3, owner-approved with conditions). Read in one `REPEATABLE READ READ ONLY` AuthorityDb snapshot (the AC-9 pattern). The actor is authorized for role *X* only if **all** of these hold:
1. The actor's identity binding is enabled.
2. An edge `organization#X@user:actor` exists with `RemovedBySecurityCommandId IS NULL`.
3. The edge's `CreatedBySecurityCommandId` command is in state `Effective`.

So a pending grant confers nothing, and a committed revoke applies immediately (F-16). **Use is restricted to AC-8.4 and AC-8.5.** It never authorizes record content, the audit explorer, event detail or the timeline.

| Section | Role *X* | Content (counts and kinds only) |
|---|---|---|
| `gate` | `system_administrator` | Authorization revision, projection-applied revision, content-integrity revision, gate state, quarantine flag |
| `commands` | `system_administrator` | Counts by state (Pending, CommittedAwaitingProjection, FailedReconciliation), oldest pending age, up to 20 non-Effective commands (kind, state, revision, created time, age); no payload or target |
| `archive` | `system_administrator` or `security_auditor` (D8-70) | Per stream: counts by delivery state, oldest pending age, last anchored time; RR-6 wording |

Search approvals reuse the existing `POST /internal/v1/field-search/administration`, unchanged.

### 5.5 AC-8.5 Coverage read (DR-C7-5)

`POST /internal/v1/field-protection/coverage`, following `FIELD_PROTECTION_RECONCILIATION.md` §13, except that authorization uses the §5.4 Effective-edge rule with role `system_administrator` (owner Q-3 decision), otherwise 404.
- **Returns:** one row per current protection decision: counts, attestation id, journal revision and time, and Stale/Missing states.
- **Never:** record ids or values, SpiceDB record lookups, or verifier-route calls.
- **UI:** the C-75 wording "current versions only … DR-C7-4".
- **Review:** the endpoint and authorization contract are submitted for verification (D8-66).

### 5.6 Authority migration and recovery

- **AuthorityDb:** no new table. Membership requests stay limited to `designer` (`ck_membership_request_role` unchanged).
- **SpiceDB:** additive schema change, written at start-up (F-13).
- **Recovery:** the DR regression proves that a revoked auditor is not re-granted by an older snapshot. No new DR claim.

## 6. Business contracts (`/api/v1`, owner feature `Audit`, PG-052 owned by `SecurityOperations`)

### 6.1 Endpoints

| Route | Permission | Notes |
|---|---|---|
| `GET /api/v1/security/audit-events` | `view_audit` + AC-8.3 | Filters: `from`, `to`, `actorUserId`, `action`, `category`, `outcome`, `entityType`, `entityId`, `formRecordId`, `correlationId`, `excludeAuditAccess`. `pageSize` ≤ 50, plus a cursor. |
| `GET /api/v1/security/audit-events/{auditEventId}` | `view_audit` + AC-8.3 | Another organization's id and an unknown id get the same 404 |
| `GET /api/v1/security/audit/security-commands` | `view_audit` (AC-8.2) | Cursor, `kind`, `state`, `from`, `to` |
| `GET /api/v1/security/overview/{section}` | §5.4 / §5.5 | `section` is one of `gate`, `commands`, `archive`, `search-approvals`, `incidents` (existing summary) or `coverage`. Each is independently refreshable (PB-07). |

- **Problems (RFC 9457):** 403 `AUDIT_VIEW_FORBIDDEN` or `SECURITY_ADMINISTRATION_FORBIDDEN`; 404 generic; 400 `CURSOR_INVALID`; 429; 503 `AUTHORITY_UNAVAILABLE`, `AUTHORITY_GATE_CLOSED`, `AUDIT_EVIDENCE_BUSY` or `AUDIT_EVIDENCE_UNAVAILABLE`.
- **Responses:** all carry `Cache-Control: no-store`. OpenAPI is regenerated; the frontend uses the coordinator only.
- **Capability:** `audit.view` joins the batch check (navigation only).

### 6.2 Response shapes

**List item (envelope):**
- `auditEventId`, `occurredOn` (UTC), `sequence`;
- `category`, `action`, `actionLabel`, `outcome`;
- `actor {userId, displayName, actorType, roleBadges}`;
- `target {entityType, entityId}`;
- `formDefinitionId` plus `formDisplayName`, `formRecordId` (id only), `fieldDefinitionId` (**id only**; no key or label);
- `correlationId`, `schemaVersion`, `evidence: "BusinessProjection"`.

**Page:** `items`, `nextCursor`, `orderingStatement`, `evidenceStatement`, and `accessEvidence {authorityJournalRevision, statement: "access authorized and recorded by the Authority; delivery is business-reported"}`.

**Detail:** the envelope plus:
- `metadata[]` of `{key, label, kind: Identifier | Count | Status | Code | DirectoryName, value}`; there is no `FreeText` kind in M4;
- `withheldMetadata[]` of `{label, policy}`, e.g. "Reason — withheld by policy" (the label only, never the value or its length);
- `description` (catalog text, or stored text when the catalog marks it safe);
- `links[]`, each to a normal route that is authorized separately;
- `receipt {state: "None", reason: "AUD-C7-01/G24"}`.

### 6.3 Redaction catalog (`Application/Features/Audit/AuditEventCatalog.cs`, code only)

Each action has a label, category, description policy (`Catalog` | `StoredSafe`) and a metadata allowlist (`key → kind`). Only allowlisted keys render.

| Class (F-8) | M4 rule |
|---|---|
| Directory display names (form, team, user, including previous/new in rename events) | **Shown** (D8-67a) |
| Field keys, group keys, field labels, option text, design-diff labels | **Withheld** (D8-67a). Design diffs render as counts plus field ids. |
| Free text (`reason`, `changeReason`, `requestReason`, `retryReason`, `display_name_hint`, notes) | **Withheld** (D8-67). Listed under `withheldMetadata`. |
| Structured codes (classification, protection mode, status, role, `reasonCode`) | Shown |
| Identity identifiers (`subjectIdentifier`, `issuer_uri`) | Detail view only |
| Counts other than search matched/evaluated | Shown |
| Record titles, values, file names, share or document scopes | Never (not present; sentinel-tested) |

- **Free-text policy:** any future disclosure of free text requires a separately approved disclosure and redaction policy (D8-67).
- **Tests:** reflection (every `AuditActions` constant has an entry); each allowlisted key exists at some call site; a forged row with unknown keys or actions renders as envelope only.

### 6.4 Frontend

- **`apps/web/src/features/audit` (new):** `AuditExplorerPage` and `AuditEventPage`.
- **`security-operations`:** `SecurityHomePage` with per-widget cards, each with its own refresh.
- **Routes:** `/app/security` (either role, widgets self-gate), `/app/security/audit`, `/app/security/audit/$auditEventId`.
- **People page (M4-1):** auditor badge, appoint and remove with a reason-code picker and optional note, never offered on the caller's own row for either role.

## 7. Migrations

| Database | Change | Milestone |
|---|---|---|
| Business | `ix_audit_events_tenant_actor_time (tenant_id, actor_user_id, event_occurred_on, audit_sequence)` | M4-2 |
| Business | `ix_audit_events_tenant_correlation (tenant_id, correlation_identifier)` | M4-2 |
| Business | `ix_audit_events_tenant_record_time (…)` only if `EXPLAIN (ANALYZE, BUFFERS)` on a seeded 1M-row organization requires it | M4-2 (measured) |
| Business | No table, no grant change | — |
| AuthorityDb | None | — |
| SpiceDB | Additive relation and permission | M4-1 |

**M4-1 has no business migration.** All migrations are additive and are tested on the disposable security-test stack first.

## 8. Tests and acceptance criteria

### 8.1 Authority adversarial tests (AC8-T; real Authority, both PostgreSQL databases, SpiceDB, Vault)

`SecurityAuditorTests` and a new `SecurityAuditAccessTests` go in shard `authorization-security-onboarding`; Authority unit tests go in `tests/authority`.

| Test | Scenario | Expected | Milestone |
|---|---|---|---|
| T01 | Administrator appoints an auditor with a valid reason code | Effective; `view_audit` allowed; no `administer_security`, `read`, `create_form` or `manage_configuration` | M4-1 |
| T02 | Self-appointment or self-removal as auditor | 403 `SELF_CHANGE_FORBIDDEN` | M4-1 |
| T02a | **Administrator self-removal (D8-62a)** while another administrator remains | 403 `SELF_CHANGE_FORBIDDEN` (was allowed) | M4-1 |
| T03 | Owner, designer or auditor appoints or removes | 403 | M4-1 |
| T04 | Target in another organization, or no binding | 403/404, no existence hint | M4-1 |
| T05 | Auditor opens a record, document, history or search | Denied by normal checks | M4-1 |
| T06 | Revoked auditor's next page or detail request | 403 | M4-2 |
| T07 | BusinessDb DBA forges an auditor (users row, audit row, session row) | No effect | M4-1 |
| T08 | Timeline sanitization: no free-text reason or note, share scope, step-up evidence, predicates, matched or evaluated counts | Verified field by field | M4-3 |
| T09 | Cross-organization timeline, overview or coverage | Denied or 404 | M4-3/M4-4 |
| T10 | Append or commit failure (fault injection) | 503; no data served | M4-2 |
| T11 | `AuditAccessAuthorized` carries actor, kind, filter digest, *R* and `delivery: not_attested`; deleting the business row leaves it | Verified | M4-2 |
| T12 | Gate closed: a pending grant of either role does not authorize the overview; a committed revoke denies at once; an enabled holder reads `gate`/`commands`; the explorer returns `AUTHORITY_GATE_CLOSED` | Verified | M4-4 |
| T13 | Disabled auditor (AC-7) | Refused; re-enable restores | M4-1 |
| T14 | Dual holder | Both badges and permissions; cannot self-remove either role | M4-1 |
| T15 | Step-up unavailable (production configuration) | Appointment and removal refused | M4-1 |
| T16 | Recovery replay from an older snapshot after revoke | Not re-granted | M4-1 |
| T17 | Stale revision; replayed idempotency key with a different payload | 409 / `IDEMPOTENCY_CONFLICT` | M4-1 |
| T18 | Missing or wrong-direction `reason_code`; `reason` sent for auditor; `reason_code` sent for administrator | `INVALID_COMMAND` | M4-1 |
| T19 | Refused auditor command | `SecurityAuditorCommandRejected` in the audit stream, never the note | M4-1 |
| T20 | Revision changes between check and lock (a revocation commits in between) | Not recorded under the old revision; re-check denies | M4-2 |
| T21 | Lock held by a long security command | Audit read gets 503 `AUDIT_EVIDENCE_BUSY` after ≤ 3 attempts; the command is not delayed | M4-2 |
| T22 | **Revocation under audit-read flood** (sustained reads at the rate limit) | Revocation commits; its lock wait is at most one audit-access hold above baseline (measured, recorded) | M4-2 |
| T23 | Rate limits (user, organization) | 429; telemetry counters increment | M4-2 |

### 8.2 Updated C-70 tests (M4-1, D8-62a)

- **Authority unit test** `SecurityAdministratorTests.RemovalNeedsAnotherAdministratorToRemainAndTheLastOneCannotBeRemoved`:
  * self-removal becomes `SELF_CHANGE_FORBIDDEN`;
  * the last-administrator check is asserted through removal by another actor.
- **Stage B** `SecurityAdministratorTests` (the clerk self-removal path at line 260): rewritten so another administrator removes the clerk, plus an explicit self-removal-refused case.
- **People directory:** no remove action on the caller's own row (`GetMemberDirectoryHandler`, `PeopleDirectoryTests`).
- **Specification:** `docs/security/SECURITY_ADMINISTRATOR_MANAGEMENT.md` rule 7 is updated when the change is implemented. Until then it carries an "approved change, not implemented" note.

### 8.3 Business, unit and browser tests

- **`AuditExplorerTests`** (shard `forms-documents`):
  * keyset stability under concurrent inserts; every filter;
  * cursor tampering and reuse by another actor, organization or filter; another organization's id returns 404;
  * `tenant_id` predicate with a second organization present (R-15);
  * **sentinel leak test:** Confidential and Public values, a PIN, a file name, a search term, a free-text reason, a field key, a field label, an option text and a membership display-name hint are created, then every explorer, detail and timeline response is scanned for them;
  * forged row rendered as envelope only; `deliveryOutcome` rows recorded.
- **`SecurityHomeTests`** (shard `authorization-security-onboarding`): per-section authorization, counts only, coverage Stale/Missing, gate-closed behaviour.
- **Manifest:** new classes added to `stage-b-groups.json` (exactly-once enforced by the plan job).
- **Unit and frontend:**
  * catalog completeness and allowlists, cursor binding, planner rules, reason-code validation;
  * Vitest for filter model and widget states;
  * Playwright `security-audit.spec.ts` on the **isolated security-test stack only**, with the guard: role flows, revocation mid-session, axe, 320/768/1440 px, 200%/400% zoom.
- **Local verification:** `scripts/dev/stage-b.ps1 -Class <Class> -Fresh` and `-Group <group>`; `scripts/ci/run_dotnet_tests.py`; Vitest, ESLint, typecheck; OpenAPI drift check.
- **Hosted:** the seven shards plus the aggregate, `ci`, `level3-script` and CodeQL; the owner dispatches `security-gate-a`. Unrun tests are never reported as passing.

### 8.4 Acceptance criteria per milestone

| ID | Criterion |
|---|---|
| AC-M4-1.1 | `view_audit` is held only through an Effective `security_auditor` edge, and grants nothing else (T01, T05). |
| AC-M4-1.2 | Only an enabled Security Administrator appoints or removes, never themself, for either role (T02, T02a, T03, T14). |
| AC-M4-1.3 | Every appointment and removal has a valid structured reason code and explicit journal evidence; refusals are in the audit stream (T18, T19). |
| AC-M4-1.4 | Owners and Security Administrators see auditors on the roster; seeing grants nothing (D8-68). |
| AC-M4-1.5 | C-70 tests and the specification are updated for D8-62a; all other C-70 behaviour is unchanged (§8.2). |
| AC-M4-1.6 | Step-up, idempotency, replay and recovery behave as for existing role commands (T15–T17, T16). |
| AC-M4-2.1 | No audit data is served without a committed `AuditAccessAuthorized` entry (T10, T11). |
| AC-M4-2.2 | Authority evidence states authorization only, and business evidence states the delivery outcome (R-18). |
| AC-M4-2.3 | No result count from the business enters Authority evidence (Q-2). |
| AC-M4-2.4 | The redaction catalog follows D8-67/D8-67a; the sentinel test finds no leak. |
| AC-M4-2.5 | Organization isolation holds without RLS (R-15). |
| AC-M4-2.6 | Audit reads never block security commands beyond one bounded hold; revocations commit under load (T21, T22); telemetry is present. |
| AC-M4-3.1 | Timeline envelopes match §5.2 exactly, including Rejected, FailedReconciliation, ownership and share kinds without scope (T08). |
| AC-M4-4.1 | The Effective-edge rule is used only by AC-8.4/8.5, in one snapshot, with enabled-binding verification (T12). |
| AC-M4-4.2 | Overview and coverage carry counts and kinds only. |
| AC-M4-5.1 | Accessibility checks pass and hosted runs are green; the status log and evidence are recorded; the reviewer package is updated. |

## 9. Milestones (independently testable)

| Milestone | Content | Authority | Gate to start |
|---|---|---|---|
| **M4-0** | This contract (revision 2). | No | — |
| **M4-1** | AC-8.1 only (§10 C-109 boundary) | **Yes** | Owner acceptance of revision 2 and C-109 |
| **M4-2** | Redaction catalog, AC-8.3, PG-054/055, business indexes; T06, T10, T11, T20–T23, `AuditExplorerTests` | **Yes** | M4-1 accepted; separate authorization |
| **M4-3** | AC-8.2 and its UI tab; T08, T09 | **Yes** | M4-2 accepted; separate authorization |
| **M4-4** | AC-8.4, AC-8.5, PG-052; T12, `SecurityHomeTests` | **Yes** | M4-3 accepted; separate authorization |
| **M4-5** | Accessibility, hosted verification, evidence, reviewer package | No | M4-1…M4-4 |

Each milestone ends with local verification, a signed commit, a status-log section and owner acceptance. Nothing is pushed, merged or deployed without a separate owner instruction.

## 10. Decisions

### 10.1 Owner decisions (recorded in C-109; owner decisions, not independent approval)

| ID | Decision | Disposition |
|---|---|---|
| D8-62 | Separate Security Auditor role; administrator-controlled appointment and revocation; no self-role changes | Approved |
| D8-62a | Security Administrator self-removal refused (intentional C-70 change) | Approved |
| D8-63 | Audit-read access recorded in the Authority audit stream; reads refused when authorization or recording fails | Approved; "recorded" = committed in AuthorityDb (§5.3) |
| D8-64 | Security and governance commands including Rejected and FailedReconciliation, with tenant isolation and redaction | Approved per Q-5 option B (§5.2) |
| D8-65 | Closed-gate Security Home from committed state; pending grants never confer access; committed revocations apply at once | Approved per Q-3 conditions (§5.4) |
| D8-66 | Read-only coverage, subject to verification of endpoint and authorization contract | Concept approved; contract §5.5 submitted for verification |
| D8-67 | Structured reason codes and safe metadata by default; no free text without a separate policy | Approved (§5.1, §6.3) |
| D8-67a | Directory display names shown; field keys, field labels, option text, design-diff labels, free-text reasons and matched counts withheld | Approved (§6.3) |
| D8-68 | Auditor roster visible to owners and Security Administrators; visibility grants nothing | Approved |
| D8-69 | No audit-read step-up until PB-08, as documented interim risk | Conditionally accepted (RR-4) |
| D8-70 | Security Home for either role; every widget independently authorized | Approved |
| Q-1 | Existing organization lock with revision comparison, short transaction, bounded lock acquisition and retries, per-user rate limiting, contention telemetry | Owner-approved option A (§5.3) |
| Q-2 | Business-reported result counts omitted from Authority evidence | Owner-approved |
| Q-3 | Effective-edge rule with consistent snapshot, enabled binding, restricted to overview and coverage | Owner-approved (§5.4) |
| Q-4 | = D8-62a | Owner-approved |
| Q-5 | Option B: ownership transfer, share and revoke, FailedReconciliation; envelopes without field or document scope | Owner-approved (§5.2) |

### 10.2 Claude's technical choices to confirm at final acceptance

- **TC-1:** the reason-code lists (§5.1).
- **TC-2:** evaluated search counts withheld together with matched counts (§5.2).
- **TC-3:** proposed lock and timeout values and rate limits (§5.3), to be measured and tuned.
- **TC-4:** `UpdateFormUsers` included in the timeline as a governance command (§5.2).
- **TC-5:** delivery outcome recorded only in the business projection, labelled business-reported; the Authority records authorization only (§5.3).

### 10.3 Independent reviewer approvals still required

- Independent confirmation of Q-1…Q-5 as answered.
- Review of AC-8.1–AC-8.5, added to the AC-5/AC-6/AC-7/AC-9 package.
- Verification of the AC-8.5 endpoint and authorization contract (D8-66).
- Review of the redaction catalog.

These are required for **closure and merge readiness**. Azure staging evidence is required before any production claim.

## 11. Reviewer questions (owner-answered; independent confirmation pending)

- **Q-1:** Is AC-8.3's per-read organization-lock append acceptable, or should audit-access entries use a lock-free append path in a separate stream? *Owner: option A with bounds (§5.3).*
- **Q-2:** Should AC-8.3 also bind the business result count (supplied by the business, so unverifiable) or omit it? *Owner: omit.*
- **Q-3:** Is the Effective-edge rule (§5.4) a sound authorization basis while the SpiceDB gate is closed? *Owner: yes, with conditions; overview and coverage only.*
- **Q-4:** Should the explicit self-change refusal (A2) also be applied to `system_administrator`? (Revision 1 wrongly said this changes C-70 only in wording; it changes behaviour, E-1.) *Owner: yes (D8-62a).*
- **Q-5:** Is the AC-8.2 kind allowlist complete for governance review without disclosing record-scoped data? *Owner: option B (§5.2).*

## 12. Carried forward unchanged

The following all stay **open**:
- G06, G08 (including missing business RLS), G19, G20, G21, G24/AUD-C7-01, G25, G26;
- PB-08, PB-09, DR-C7-1…6, SD-1…SD-6;
- the C-74 development incident remediation;
- the AC-5/AC-6/AC-7/AC-9 independent review;
- production Level 2.

M4 does not make the business audit projection tamper-proof, does not archive it, treats no local Azurite evidence as production-grade immutability, and makes no Level 2 or production claim.
