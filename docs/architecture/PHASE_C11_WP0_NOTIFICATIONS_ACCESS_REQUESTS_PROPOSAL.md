# Phase C11 WP-0 — Notifications, task inbox, access requests and welcome

**Status (2026-11-28): DESIGN PROPOSED for owner approval. Implementation NOT authorized.** No C11 code, migration,
endpoint or page may be written until the owner approves this design and separately authorizes a C11 implementation
package. Baseline: `main` `a929ed91` (C10 integrated at `5cc4efe3`, C-202; C10 formal Development/Test acceptance pending).
(The file name keeps "PROPOSAL" for link stability, as for C10.)

Normative sources:
- `CLAUDE-DESIGN.MD` §3.1 (first-use guidance), §17 PG-004, PG-009, PG-060, PG-065;
- CLAUDE.md §4B, §4C.6, §4D, §4H, §4J, §4M, §4T, §11 #29 and #60;
- ADR-PB-004 (free-text classification), ADR-PB-006 (API conventions), ADR-PB-007 (request coordinator, independent card refresh);
- PB-10 RES-12 (notifications processed in Canada);
- C10 WP-0 §2 invariants and §3.7 step-up (G27);
- roadmap §3 Phase C11 row and §6.

Approved scope (roadmap, C-193 RM-1): notifications and the task inbox (PG-009), access requests and invitations (PG-065),
and welcome / first use (PG-004).
Exit evidence: a request flow without existence disclosure (PG-065 rule).

## 1. Baseline on `a929ed91` (read-only assessment)

None of the C11 capabilities exist yet. There is no notification, inbox, task, access-request or invitation entity, table,
endpoint, route or audit action. `docs/ui/SCREEN_IMPLEMENTATION_MATRIX.md` lists PG-004, PG-009 and PG-065 as not implemented.

### 1.1 Notifications and task inbox (PG-009)

| Item | Reuse unchanged | Small extension | Missing |
|---|---|---|---|
| Durable background pattern | Worker BackgroundServices with `FOR UPDATE SKIP LOCKED` leases (`TeamMembershipReconciliationWorker`); `DocumentProcessingWorker` reaches the terminal upload states | — | — |
| Pending items that already exist | Membership requests and owner proposals (`GetApprovalsHandler`, `GET /api/v1/organization/membership-requests\|owner-proposals`); field-search approvals (`SearchApprovalsPage`); owner-recovery notice (`OwnerRecoveryNotice`); security incidents (`GET /api/v1/security/incidents`, with `AssignedToUserId` on `SecurityIncident`) | An "assigned to me" filter on the incident list | — |
| Frontend data and refresh | TanStack Query per section over the request coordinator (`DashboardSections.tsx`); endpoint keys in `packages/work-queue/src/endpoints.ts`; code-based lazy routes (`app/router.tsx`) | — | Inbox route, page and shell badge |
| Informational notifications (upload finished or failed, share granted, changed or revoked, ownership received, access-request outcome) | — | — | Per-user durable item with read and dismiss state |
| Business outbox | — | — | No business outbox exists; none is needed (§4.2) |

### 1.2 Access requests and invitations (PG-065)

| Item | Reuse unchanged | Small extension | Missing |
|---|---|---|---|
| Granting | C10 share commands: `POST …/shares` (`CreateShareHandler`, share id derived from the idempotency key), `PATCH …/shares/{id}` (If-Match, 412/428), lineage and delegable-scope checks, G27 step-up, A11 pending handling (202 `COMMAND_PENDING`) | — | — |
| Concealment | The Authority returns one 404 `NOT_FOUND` for a missing head, a missing or inactive resource and a failed discover check (`ContentService`, "concealment-safe"). Sharing maps refusals to 404 `RECORD_NOT_FOUND` (`ListSharesHandler`, `GetShareHandler`, `MyAccessHandlers`). Delegating callers get one 403 `FIELD_NOT_IN_SHAREABLE_SCOPE` (F-6) | — | — |
| "Who can manage what" enumeration | `GET /api/v1/me/access`: owned, direct and team-derived records through the Authority discover path | — | — |
| Request entity, lifecycle, lists | — | — | Request table, endpoints, PG-065 page |
| Organization invitations | C8 D8-3(a) operator-plane dual-control onboarding (`membership-requests`, two-operator approval, `AddUserDialog` keyed by Entra object id) stays in use | — | In-app Entra invitations: **blocked on PRD-1** (D8-3(b), C-78). Not designed here |
| Shares awaiting acceptance | Shares are effective at Effective (no acceptance state) | — | PG-065 asks for acceptance only "where product policy requires it". No such policy exists, so none is built |

### 1.3 Welcome / first use (PG-004)

| Item | Reuse unchanged | Small extension | Missing |
|---|---|---|---|
| Landing | `LandingRedirect` sends users to `/app/dashboard` (matches §3.1) | — | — |
| Capability state | `GET /api/v1/bootstrap` (session, capabilities, branding, UI preferences, revision) | — | — |
| Dismissal persistence | `UserUiPreference` with `PreferenceRevision` | One nullable column for the dismissal time | — |
| Zero-grant experience | Dashboard hides Records without `records.discover`; Uploads, Activity and Forms show empty states | Positive empty-state wording | No zero-grant dashboard test exists |
| Page | — | — | `/app/welcome` and the inline checklist card |

### 1.4 Security, audit and ownership facts the design depends on

- **Audit.**
  - `AuditRecorder.Record` writes in the same scope as the business change.
  - A new action needs three things: an `AuditEvent` constant, an `AuditEventCatalog.Build()` entry with shown and withheld fields, and a call site (enforced by `AuditExplorerTests`).
  - `ACCESS_DENIED` is defined but never written. This is the §4D denial-evidence gap, which predates C11 and is not widened by it.
- **Persistence rules.**
  - Business migrations live in `Persistence/Migrations`.
  - Column naming is enforced by `DatabaseNamingTests`.
  - Every new table needs a `BusinessRuntimeGrants` entry.
- **Feature ownership.** `FEATURE_OWNERSHIP_MATRIX.md` gives `support` PG-004 and PG-009 with "none" persistence, and `sharing` PG-065. A C11 store changes the `support` row (§3.4).
- **Stale matrix row.** `SCREEN_IMPLEMENTATION_MATRIX.md` still lists PG-066 as not implemented, although `MyAccessPage` exists. This is corrected with C11's matrix update.

## 2. Invariants C11 must preserve

1. The Authority remains the sole authorization source. Access requests, notifications and tasks confer no access. Only the C10 share commands grant, with every C10 check: lineage, delegable scope, step-up, revision, idempotency, journal and A11.
2. No new grant mechanism, no new Authority command kind, and no change to the C10 share contracts.
3. **PG-065 rule:** never reveal the existence of a target to a user who cannot discover it (§5).
4. No protected field value, record summary value or confidential filename in any notification or request row (PG-009). Labels are rendered only at read time, after authorization (§4.3).
5. Contributor writes stay behind the default-off switch (ACT-5, G20). Approving a Contributor request creates a Contributor share exactly as C10 does.
6. Real passkey step-up stays dependent on PRD-1. In staging and production, approval fails closed (`PB08_STEP_UP_NOT_AVAILABLE`), as every C10 share command does.
7. In-app Entra invitations stay dependent on PRD-1. C8 operator onboarding stays the only way to add members.
8. Naming, UUIDv7, tenant-safe foreign keys, RowHmac v1 and Merkle v1 are unchanged. The new tables are business projections or user-state tables, not signed authorization state (§6).
9. Every C10 production and acceptance obligation stays open; C11 neither satisfies nor waives any of them.

## 3. Design

### 3.1 Access requests (PG-065, backend feature `Sharing`)

**Who can request.** C11 V1 accepts requests only for a record the requester can currently discover (owner decision OD-C11-1,
recommended option A). PG-065 requires submission "through a trusted invitation or known safe resource reference" and "an
access-request privilege". The design uses the record's existing discover permission as both: no new permission key.

Typical cases:
- a partial share recipient asking for more fields or documents;
- a Reader asking for Contributor;
- a recipient whose share expires soon asking for an extension;
- a team-derived reader asking for direct access.

**What is requested.** The request carries:
- a requested role (`Reader`, `Contributor` or `Manager`);
- an optional requested expiry;
- an optional bounded reason (OD-C11-2).

The requester cannot name fields or documents they cannot read: listing hidden items would itself disclose them. The approver selects items within the approver's own delegable scope, and the Authority enforces it.

**Lifecycle.** `Submitted` → `Fulfilled` | `Declined` | `Cancelled` | `Closed`.
- `Fulfilled` means the linked share command is Effective.
- `Closed` covers expiry of the request and target unavailability.
- Every terminal state is final.
- A request expires 30 days after submission (C11-D). Expiry is evaluated when read; no background job is needed.
- One open request per requester and record.
- At most 20 open requests per requester and organization (C11-D).

**Approval reuses the C10 commands, in-process.**
- `POST /api/v1/access-requests/{id}/approve` takes exactly the body of `CreateShareRequest`. It has one constraint: the recipient is fixed to the requester and cannot be redirected.
- With `existingRecordShareId` (the requester's current share, which the approver may update), the endpoint instead takes the `UpdateShare` body and requires its `If-Match`.
- The handler calls `CreateShareHandler` or `UpdateShareHandler` unchanged, under the caller's `Idempotency-Key`.
- C10 therefore decides everything: authority to grant (owner, or a Manager naming a parent share it holds), scope bounds, depth, step-up, revision conflicts and pending handling.
- The request stores the resulting `record_share_id` and `security_command_id`.
- While the command is pending, the request shows "Approval pending". Its status is re-derived on read from the share's Authority lifecycle (the C10 F6 precedent), so a later Effective resolves it without a worker.
- If the command is refused, the request stays `Submitted` and the approver sees the C10 refusal code.

**Decline and cancel** are business state changes, because they change no access. They need no step-up and use
`If-Match` on `request_revision` (PB-06, D9-3). Decline requires the same Authority manage check as approval. Cancel is the
requester's own action.

**Lists.**
- *My requests:* the caller's own rows. Labels and statuses follow §5.5.
- *Requests I may approve:* the open requests whose target is in the caller's manageable set. That set is the owned records and the records where the caller holds a Manager share, taken from the existing `/me/access` Authority enumeration. Each row is re-checked for manage authority at the Authority before display. Rows that fail are omitted, never counted.

**Endpoints** (`/api/v1`, owner `Sharing`):

| Method and path | Purpose | Authorization |
|---|---|---|
| `POST /form-records/{formRecordId}/access-requests` | Submit (Idempotency-Key) | Requester can discover the record |
| `GET /me/access-requests` | My requests (cursor) | Current user |
| `GET /access-requests/approvable` | Requests I may approve (cursor) | Manage authority per row |
| `GET /access-requests/{id}` | Detail (ETag `"{id}:{revision}"`) | Requester, or an approver with manage authority |
| `POST /access-requests/{id}/approve` | Approve through `CreateShare` or `UpdateShare` | C10 rules |
| `POST /access-requests/{id}/decline` | Decline (If-Match) | Manage authority |
| `POST /access-requests/{id}/cancel` | Cancel (If-Match) | Requester |

**Table** `record_access_requests` (owner `Sharing`):
- `record_access_request_id` uuid v7, primary key;
- `tenant_id`, `form_record_id`, `requester_user_id`;
- `requested_access_role`, `requested_share_expires_on` (nullable), `request_reason_text` (nullable, at most 500 characters, OD-C11-2);
- `request_status_name`, `request_revision`;
- `request_submitted_on`, `request_resolved_on`, `resolved_by_user_id`;
- `fulfilling_record_share_id`, `fulfilling_security_command_id`.

Constraints:
- a partial unique index on (`tenant_id`, `requester_user_id`, `form_record_id`) where the status is Submitted;
- CHECK constraints on status and role;
- tenant-safe foreign keys.

Runtime grants: INSERT, SELECT, and column-level UPDATE on the status, revision, resolution and fulfilment columns. No DELETE (G08).

**Frontend** (`features/sharing`):
- PG-065 at `/app/access-requests`, with three independently refreshing sections: My pending requests, Requests I may approve, and Request history.
- A "Request more access" action on the record page, shown when the record is discoverable and the caller is not its owner.
- Approval reuses the C10 share dialog (`RecordAccessPanel` / `EditShareDialog`), with the recipient fixed and the development step-up checkbox (G27).

### 3.2 Notifications (PG-009 notification section)

**Delivery:** in-app only. No email, SMS or browser push (push is optional per `CLAUDE-DESIGN.MD` and needs consent; it is not in scope). Processing stays in Canada (RES-12, BusinessDb).

**Table** `user_notifications` (backend feature `Notifications`, §3.4):
- `user_notification_id` uuid v7, primary key;
- `tenant_id`, `recipient_user_id`;
- `notification_kind_name`, `notification_created_on`;
- `target_form_record_id` (nullable), `target_upload_session_id` (nullable), `target_record_access_request_id` (nullable);
- `source_security_command_id` (nullable, for deduplication), `actor_user_id` (nullable);
- `notification_read_on`, `notification_dismissed_on`.

The table holds no text column, so no value, label or filename can be stored. Runtime grants: INSERT, SELECT, and UPDATE on the read and dismissed columns only.

**Producers.** Each writes in the same business scope as an event the business already handles, only after the outcome is known:

| Kind | Producer | Recipient |
|---|---|---|
| `ShareGranted`, `ShareScopeChanged`, `ShareRevoked` | C10 share handlers, when the command returns Effective | The direct user recipient (team recipients: §8 C11-D) |
| `OwnershipReceived` | Transfer handler, when Effective (where `RECORD_OWNERSHIP_TRANSFERRED` is written) | The new owner user |
| `UploadReady`, `UploadRejected`, `UploadFailed` | `DocumentProcessingWorker`, at the terminal state | The uploader |
| `AccessRequestFulfilled`, `AccessRequestDeclined`, `AccessRequestClosed` | §3.1 handlers | The requester |

**Accepted limitations.**
- A command that becomes Effective after the handler returned 202 produces no notification (the C10 F4 precedent). Notifications are convenience, not evidence; the Authority journal and the histories remain complete.
- Share expiry is decided in the Authority loop. It is shown in the existing My access "expiring soon" panel rather than as a notification.

**Endpoints** (owner `Notifications`):
- `GET /api/v1/me/notifications` (cursor, 90-day window, page size 25; C11-D);
- `POST /api/v1/me/notifications/{id}/read` and `…/dismiss` (idempotent; the caller's own rows only);
- `GET /api/v1/me/notifications/unread-count` (capped display "99+").

### 3.3 Task inbox (PG-009 task sections) and shell badge

PG-009 at `/app/notifications` (frontend owner `support`, unchanged). The notification list (§3.2) is followed by task
sections composed from existing endpoints. Each section is shown only with its capability and loads and refreshes on its own
(PB-07):

| Section | Source | Shown to |
|---|---|---|
| Access requests to review | §3.1 `GET /access-requests/approvable` | Owners and Managers |
| Membership requests and owner proposals | Existing approvals endpoints | Organization Owners |
| Field-search approvals | Existing approvals | Holders of the approval capability |
| Security alerts assigned to me | Existing incident list, plus a filter on `AssignedToUserId` (small extension) | Security Administrators |
| Owner-recovery notice | Existing notice | As today |
| Publishing reviews | **No publishing-review workflow exists**; the section is omitted until one exists | — |

The **shell badge** shows only the unread-notification count. It refreshes on window focus, after navigation and on manual
refresh. There is no periodic polling, consistent with ADR-PB-007 A1 and SD-6 (C11-D).

### 3.4 Feature ownership

- **New business feature `Notifications`.**
  - Domain, Application, Infrastructure and Api folders under `Features/Notifications`.
  - It owns `user_notifications` and the four endpoints of §3.2.
  - It exposes one port, `INotificationWriter`, used by `Sharing` and `Documents`. It makes no authorization decision.
- **`FEATURE_OWNERSHIP_MATRIX.md` `support` row.** Its backend columns change from "app-level status services only | none" to `Notifications` | `user_notifications`. PG-004 and PG-009 stay owned by the `support` frontend feature, so `CLAUDE-DESIGN.MD` §17 and `FeatureOwnershipMatrixTests` need no page-owner change.
- **`sharing` row.** It gains `record_access_requests` and the §3.1 endpoints.

### 3.5 Welcome / first use (PG-004)

- **Column.** `user_ui_preferences` gains `welcome_checklist_dismissed_on` (nullable). The existing preference endpoint and `PreferenceRevision` handle concurrency, and the change is audited as the existing preference changes are (§4E).
- **Page.** `/app/welcome`, frontend owner `support`. It has five capability-scoped checklist items, each refreshing separately:
  1. organization and role orientation, from bootstrap capabilities;
  2. permitted forms, from the existing dashboard `permitted-forms` source;
  3. profile and regional preferences (link);
  4. accessibility preference (link);
  5. passwordless enrollment guidance as static text that says it is managed by the organization's identity provider. It shows no enrollment state, which needs PRD-1.
- **Actions:** open the next permitted task; dismiss; revisit.
- **Dashboard.** While the checklist is not dismissed, the dashboard shows it as a dismissible inline card (§3.1: no modal tour). The landing redirect stays `/app/dashboard`.
- **Empty states.** A member with no grants sees positive empty states ("Nothing has been shared with you yet"), never organization-wide data. Access is never granted automatically (PG-004).

## 4. Lifecycle, delivery and display rules

### 4.1 Access requests

- **Created only by the requester.** The submit handler takes `requester_user_id` from the session, never from the body.
- **Exactly one terminal transition per request.**
  - Every transition is guarded by `request_revision`.
  - A concurrent approve and decline let only one win. If the share command becomes Effective after a decline was recorded, the share stands (the Authority is authoritative) and the request shows `Fulfilled`: derivation from the linked share always wins.
  - An approve that is pending blocks decline.

### 4.2 Notifications

- **Written in the producer's transaction or business scope.** No outbox, because the producer already knows the outcome.
- **The Worker producer** writes in the same transaction as the terminal upload state.
- **Deduplication** by (`recipient_user_id`, `notification_kind_name`, `source_security_command_id`) where a command id exists (unique partial index), so idempotent replays add nothing.

### 4.3 Display at read time (PG-009 "a revoked target becomes generic unavailable")

- For each page of notifications or requests, the handler performs one `/me/access` Authority enumeration.
- A target record shows its label only if it is in that set, and only through the existing authorized record-summary path. Otherwise the item reads "An item is no longer available", with no actor, form name, kind detail or link target.
- Upload notifications show no filename. They link to the Upload Center, which applies its own rules.
- Opening any target goes through the existing record route. Its concealment-safe 404 and the PG-060 page decide.

## 5. PG-065 rule: no disclosure of a protected record's existence

**Threat:** a member who cannot discover record R uses the request workflow to learn whether R exists, or something about it.
With option A, the submit path performs the same Authority discover decision as reading R. Every path below is designed so
that its observable result is identical for a nonexistent record, an undiscoverable record, a record in another
organization, and a removed or archived record.

1. **Response content and status.**
   - Submit returns the exact `RECORD_NOT_FOUND` 404 Problem Details used by the record and sharing endpoints. The body is identical except `traceId` and correlation id, and the headers are identical.
   - The handler maps every Authority refusal to that response.
2. **Validation order.**
   - Only record-independent validation (role value, reason length, expiry format and bounds, idempotency key) may run before the discover check.
   - Every record-dependent check runs after it: duplicate open request, the 20-request cap, already holding the requested role, record lifecycle, record owner.
   - The duplicate and cap errors therefore never reveal a hidden record.
3. **Idempotency.**
   - A replay returns the stored response, including a stored 404.
   - A key reused with a different body gets the existing record-independent payload-mismatch error.
   - Request ids are server-generated UUIDv7 values, never derived from the record.
4. **Timing.**
   - The found path performs no business-side work before the discover check that the not-found path skips. The business store is not consulted for R before the Authority answers.
   - The Authority's own 404 paths (missing head versus failed discover) differ slightly in work. This residual exists on every record endpoint today and is not introduced by C11.
   - No constant-time claim is made. Measuring it under PB-09 load is a staging obligation (PRD-4).
5. **Notifications.**
   - No notification is created before the discover check passes.
   - Approvers are never notified of requests for records they cannot manage, because the approvable list is Authority-filtered per row.
   - The requester receives only outcome notifications, rendered under §4.3.
6. **History and lists.**
   - The requester's own history shows a record label only while the record is currently discoverable.
   - When it is not, every state other than `Fulfilled`-with-current-access collapses to `Closed`, labelled "Unavailable item". Decline, approver withdrawal, record removal, the requester's loss of discover and request expiry are therefore indistinguishable.
   - Approver lists omit, rather than count, rows the approver can no longer manage.
7. **Counts and badges.**
   - The badge counts the caller's own notifications only.
   - The approvable count covers only the rows returned after the per-row Authority check.
   - No count is computed over raw `record_access_requests` rows for another user's records.
8. **Audit and logs.**
   - `ACCESS_REQUEST_SUBMITTED` is written only on success, after the discover check.
   - The requester cannot see audit events for undiscoverable records (existing audit ACL, AC-8).
   - Reason text is withheld from the audit catalog view and from logs (OD-C11-2).
   - The refusal itself is not audited, which matches today's record 404 behaviour (the §4D gap, unchanged).
9. **Cross-tenant.** The organization comes from the session. A record id from another organization fails at the Authority's tenant check with the same 404.
10. **Approver-side disclosure.** Revealing the requester's identity and reason to the record's owner or Managers is intended. Declining reveals nothing new to the requester beyond item 6.
11. **Option B** (not proposed for V1; OD-C11-1): a generic "request access" action on PG-060 for records the requester cannot discover. It needs a different design, because no discover check can be shown to the requester:
    - always-accepted, asynchronous routing by the Worker;
    - one status for every case until a grant;
    - closure at a fixed time;
    - rate limiting against owner spam;
    - a decision on what the owner may see.

## 6. Security and audit requirements

- **Authorization boundaries.**
  - Submit: discover on the record.
  - Approve: C10 share authority, unchanged.
  - Decline: the Authority manage check (owner, or a Manager share whose scope covers the record).
  - Cancel and the notification endpoints: the session principal's own rows.
  - The task sections keep their existing endpoint authorization.
- **Untrusted business rows.**
  - `record_access_requests` and `user_notifications` are untrusted business state. They confer nothing.
  - A compromised BusinessDb could forge a request row and show a false requester to an approver. Any grant still needs that approver's own human decision, step-up and the Authority's checks. This residual is recorded for G20/PRD-7 review and is not new authority.
- **Step-up.** Approval inherits G27 through `CreateShare` and `UpdateShare`. Submit, decline, cancel, read and dismiss confer or remove no access and require none.
- **Audit actions** (constant, catalog entry with shown and withheld fields, call site): `ACCESS_REQUEST_SUBMITTED`, `ACCESS_REQUEST_CANCELLED`, `ACCESS_REQUEST_DECLINED`, `ACCESS_REQUEST_FULFILLED`.
  - The `FULFILLED` entry carries the share's `security_command_id`. The C10 `RECORD_ACCESS_GRANTED` and the Authority journal remain the grant evidence; there is no new writer for grants (§4T).
  - Notification reads, dismissals and the welcome dismissal are not security events. The welcome dismissal is recorded as a preference change.
- **Data minimization.** No protected values, record summaries or filenames are stored in either table. Reason text is governed by OD-C11-2. Neither table is exported, indexed for search or sent to telemetry.
- **Retention.**
  - Notifications are listed for 90 days. Requests are listed for 365 days.
  - Physical purge of both tables is assigned to Phase C16 retention and purge work: the runtime role gets no DELETE.
  - No retention rule exists today; these are display windows, not a retention policy.
- **G08.** The tables join the existing business-table RLS obligation (PRD-5). No `SECURITY DEFINER`, and no new privileged role.

## 7. Acceptance criteria

| Id | Criterion |
|---|---|
| AC-C11-01 | Submit, approve (owner; Manager naming its parent share), decline and cancel work end to end. Approval creates or updates a share only through the unchanged C10 handlers, and the share is the only access change |
| AC-C11-02 | **Non-disclosure:** for a nonexistent id, an undiscoverable record, another organization's record and a removed record, submit returns byte-identical Problem Details (except trace and correlation ids) and identical headers. No row, notification or audit event is created |
| AC-C11-03 | Record-dependent errors (duplicate open request, cap, already holding the role) occur only after a successful discover check |
| AC-C11-04 | The requester's history collapses to `Closed` / "Unavailable item" when the target becomes undiscoverable, and is indistinguishable across decline, removal, revocation and expiry |
| AC-C11-05 | The approvable list shows only rows passing the per-row Authority manage check. A Manager sees only records within their manageable set. Counts never include omitted rows |
| AC-C11-06 | Approval without step-up is refused (`STEP_UP_REQUIRED`). Staging and production configurations refuse it (`PB08_STEP_UP_NOT_AVAILABLE`). A pending command shows "Approval pending" and later resolves from the share's lifecycle |
| AC-C11-07 | A concurrent approve and decline, and stale `If-Match`, give 412/428 per PB-06. Exactly one terminal state results; a share made Effective wins over a recorded decline |
| AC-C11-08 | Notifications are created exactly once per producer outcome (deduplicated on replay) and hold no text column. A revoked or unavailable target renders as "An item is no longer available", with no actor, form or link detail |
| AC-C11-09 | Notification and request endpoints return only the caller's own rows, with tenant isolation, and a cross-user id returns 404 |
| AC-C11-10 | The task sections show only with their capability, load and refresh independently, and fail in isolation |
| AC-C11-11 | Welcome: the checklist is capability-scoped, dismissible and revisitable; dismissal persists across devices under `PreferenceRevision`; a zero-grant member sees only positive empty states and no organization data |
| AC-C11-12 | PG-004, PG-009 and PG-065 pass axe (WCAG 2.0/2.1/2.2 A and AA) with no horizontal scroll at 320, 768 and 1440 px and at 200% and 400% zoom. Manual NVDA/VoiceOver stays a Phase C19 obligation |
| AC-C11-13 | Schema naming, runtime grants (no DELETE), audit catalog coverage, endpoint ownership, OpenAPI drift and frontend boundary tests pass. Contributor writes stay off by default |

## 8. Technical defaults (C11-D; engineering defaults, not owner decisions)

- **Access requests:**
  - an open request expires after 30 days;
  - at most 20 open requests per requester and organization;
  - one open request per requester and record.
- **Notifications:**
  - a 90-day listing window and page size 25;
  - the badge shows "99+" above 99;
  - no periodic polling.
- **Team share recipients** receive no notification row. The share appears in My access, which is Authority-enumerated. Membership rows are untrusted projections, so the design does not fan out notifications from them.
- **Request detail** uses ETag `"{id}:{revision}"`, with `If-Match` on every state change (D9-3 platform convention for new endpoints).

## 9. Owner decisions

| Id | Question | Options | Recommendation | Implications |
|---|---|---|---|---|
| **OD-C11-1** | Which targets may a request name? PG-065 allows "a trusted invitation or known safe resource reference" and PG-060 a generic request "where provided"; no artifact decides whether undiscoverable targets are in scope | **A.** Only records the requester can currently discover ("request more access"), using discover as the access-request privilege. **B.** A plus a generic request from PG-060 for undiscoverable ids (§5 item 11) | **A** for C11; record B as deferred, not designed | A satisfies the PG-065 rule by reusing the Authority's concealment-safe discover decision and adds no new existence oracle. B is a separate design with asynchronous routing, uniform status, abuse limits and new residuals (timing of owner actions, owner spam); about 1–1.5 days more |
| **OD-C11-2** | ADR-PB-004 makes new unclassified free text Confidential, encrypted by default, but PG-065 asks for "bounded reason text". Existing operational reasons (transfer reasons, incident notes, protection-migration reasons) are bounded plaintext | **A.** Bounded plaintext (≤500), classified Internal, never searched, exported or logged, withheld from the audit catalog view, with a UI warning not to enter confidential information. **B.** No free text: a fixed reason list. **C.** Application encryption with a new purpose-scoped envelope | **A** (follows the existing operational-reason precedent) | A: a plaintext reason is readable by a BusinessDb reader (the documented residual for Internal data). B: no residual, less useful. C: needs a non-record encryption purpose and AAD design, and a PRD-3 custody question (no new deployment secret without it); about 0.5–1 day more |

All other choices in this document are engineering defaults (§8) or follow existing approved contracts.

## 10. Implementation work packages (one consolidated implementation PR)

All three packages fit in one PR on one branch: no package has a security or technical dependency that needs a separate
merge. Order within the branch: WP1, then WP2 (its access-request producers use WP1), with WP3 independent.

| WP | Deliverables | Reuses | Changes | Depends on | Acceptance | Estimated actual time (implement + targeted tests) |
|---|---|---|---|---|---|---|
| **C11-WP1 Access requests** | `record_access_requests` migration and grants; seven endpoints; handlers delegating approval to `CreateShareHandler` and `UpdateShareHandler`; four audit actions with catalog entries; OpenAPI and client regeneration; PG-065 page; "Request more access" on the record page; contract tests | C10 share handlers, dialog, `/me/access` enumeration, concealment mapping, ETag/If-Match helpers | New table and endpoints only; no C10 contract change | OD-C11-1, OD-C11-2 | AC-C11-01…07, 09, 13 | 3–4.5 h |
| **C11-WP2 Notifications and task inbox** | `Notifications` feature (table, port, four endpoints); producers in the share, transfer, upload-worker and request handlers; PG-009 page with notification list and task sections; shell badge; incident "assigned to me" filter; ownership matrix rows | Existing approvals and incident endpoints, TanStack sections, Worker transaction | One port call per producer; one incident filter parameter | WP1 (request producers) | AC-C11-08, 09, 10, 13 | 3–4 h |
| **C11-WP3 Welcome** | `welcome_checklist_dismissed_on` migration; PG-004 page; dashboard inline card; positive empty states; zero-grant dashboard test | Bootstrap capabilities, `permitted-forms`, preference endpoint | One column and one preference field | — | AC-C11-11 | 1–1.5 h |
| **Cross-cutting** | Playwright and axe specs for the three pages (isolated stack); register rows; WP-0 implementation record; manifests; focused Stage B classes prepared (not run) | Existing e2e harness | — | WP1–WP3 | AC-C11-12; §11 register update | 1.5–2 h |

**Estimated total C11 implementation, actual Claude Code time** (from C10's observed pace):
- best 7 h, expected 10 h, worst 16 h (worst case: one review-correction cycle and OpenAPI or ownership-test churn);
- excludes CI waiting time (about 10–15 min per push);
- excludes the eventual focused Stage B (below).
- OD-C11-1 option B adds about 1–1.5 days; OD-C11-2 option C adds 0.5–1 day.

## 11. Required targeted verification

- **Unit and contract tests.** Covering:
  - non-disclosure response equality across the four target classes;
  - validation order;
  - idempotent replay including a stored 404;
  - the lifecycle state machine and revision races;
  - approval delegation to the C10 handlers, with step-up refusal, a Manager parent share and a pending result;
  - the read-time label rule;
  - notification deduplication;
  - own-rows-only access;
  - the audit catalog;
  - runtime grants (no DELETE);
  - naming;
  - endpoint ownership;
  - OpenAPI drift.
- **The full .NET CI-equivalent gate and frontend lint, typecheck, unit tests and build.**
- **AI technical review** by Claude Code subagents with fresh, read-only context, focused on §5. It is never described as independent human review (A-24).
- **Not run without separate authorization:** Stage B and any persistent-environment migration.

## 12. Eventual Stage B requirements (release-testing authorization, C-131; fresh isolated stack)

- **`C11AccessRequestTests`:**
  - real-Authority non-disclosure equality for the four target classes, including an archived record and another organization;
  - owner approval and Manager approval with a parent share;
  - approval under revocation between submission and approval;
  - requester history collapse after revocation;
  - an approve and decline race;
  - step-up refusal.
- **`C11NotificationTests`:**
  - raw BusinessDb inspection showing no values, labels or filenames;
  - a revoked target rendering as unavailable;
  - upload notifications from the real Worker;
  - deduplication on command replay.
- **Playwright `c11-pages.spec.ts`:** axe and reflow for PG-004, PG-009 and PG-065 at the AC-C11-12 widths and zooms; the zero-grant welcome path.
- **Regression classes:** `SharingTests`, `C10ShareLifecycleTests`, `DocumentTests`.
- **Estimated:** 1.5–2.5 h including stack setup; worst case 3.5 h with one fix cycle.

## 13. Production and later-milestone dependencies (unchanged by C11)

- **PRD-1:**
  - real passkey step-up for approval;
  - in-app Entra invitations (D8-3(b));
  - real enrollment state on PG-004.
- **PRD-4:** timing measurement under PB-09 load (§5 item 4); notification and request load.
- **PRD-5:** G08 business-table RLS for the new tables.
- **PRD-7:** independent review of §5 and the forged-request residual.
- **Phase C15:** record challenges gate approval once they exist. Approval goes through `CreateShare`, so it inherits them.
- **Phase C16:** physical retention and purge of the two tables.
- **Phase C19:** manual NVDA/VoiceOver and the §11 register results.
- **C10:** formal acceptance and every C10 obligation (C10 WP-0 §10.9, §10.12) remain open and are not affected.

**Not designed here, because later milestones own it:**
- exports and their notifications (Phase C12);
- imports (Phase C13);
- translations of notification text (Phase C14);
- record challenges (Phase C15);
- publishing-review workflows;
- help content (Phase C18).
