# C8 M2 — Edit and cancel pending membership requests: Authority proposal (awaiting owner approval)

Status: **proposal only; nothing in this document is implemented.** It needs the owner's approval because it adds two Authority security commands and a schema change to the independently administered Authority (CLAUDE.md §4M; no silent new Authority capability).

## 1. Review of the existing AC-7 lifecycle (what is and is not supported)

Read from `AuthorizationAuthority.Application/Features/Membership/MembershipProvisioningService.cs`, `…/Domain/Features/Organizations/MembershipAdministration.cs`, `SecurityCommandService`/`CommandPlanner` and the operator endpoints.

| Fact | Consequence |
|---|---|
| A request is created only by the owner command `RequestOrganizationMembership` (command id = request id; 7-day expiry; one pending request per identity). | There is no command that changes a request after creation. |
| States are `Pending`, `Approved`, `Rejected`, `Expired`. `Rejected` is an **operator-plane** single-operator action; `Expired` is recorded when an approval finds the request too old. | There is **no owner-initiated cancellation** and no `Cancelled`/`Replaced` state. |
| Approval is one operator call: the operator signs a statement (`MembershipApprovalStatement`: version, request id, organization, requested user id, issuer, subject, initial role, requester, expiry, justification) with two distinct approver keys; the Authority verifies both and creates the binding in one journaled command. **Approvals are not stored on the request beforehand.** | No approval exists to "invalidate" before the approving call; but an approval that was signed over an older statement can never verify against a changed one. The display-name hint and the reason are **not** part of the statement. |
| Approve and reject re-read the request inside the organization lock and refuse anything not `Pending`. | A cancelled or replaced request would already be impossible to approve once it is no longer `Pending` — the new state is the only missing piece. |

Conclusion: existing commands do **not** support edit or cancel. The business API cannot fake it (no client-side hiding, no direct writes to Authority tables). New Authority commands are required.

## 2. Proposed design (minimum necessary)

Edit is implemented as **cancel-and-replace**, as the owner preferred: a material change never mutates a request in place, so no approval material can ever carry over.

### 2.1 Commands (owner plane, through the existing security-command pipeline: journal → desired state → outbox → Effective gate)

1. **`CancelOrganizationMembershipRequest`** — payload: `membership_request_id`, `expected_request_digest`, `reason` (3–500), `step_up_evidence`. Effect: `Pending` → `Cancelled`; records who, when and why. Grants nothing.
2. **`ReplaceOrganizationMembershipRequest`** — payload: `membership_request_id`, `expected_request_digest`, `display_name_hint` (1–200), `initial_role_name` (`null` | `designer`), `reason` (3–500), `step_up_evidence`. In **one transaction under the organization lock**: the original (must be `Pending`, unexpired) becomes `Replaced`; a **new** request row is created with a **new id** (the command id), a fresh 7-day expiry, state `Pending`, and `replaces_membership_request_id` set. **Identity is never a parameter:** issuer, subject, requested Authority user id, organization and original requester are copied by the Authority from the original row (the new request records the *editing* owner as `requested_by` and keeps the original requester in the lineage). The existing one-pending-per-identity rule holds because the original leaves `Pending` in the same transaction.

### 2.2 Authorization, step-up, audit

- Actor must be an **enabled `organization_owner`** of the request's organization (Security Administrators and Designers never qualify, as for every AC-7 command). Another organization's owner gets 404.
- Step-up through the existing PB-08 gate (synthetic in Development/Testing; refused in staging/production), same as the other AC-7 commands.
- Idempotency key + canonical request digest (existing rule); refusals are journaled on the audit stream (`MembershipCommandRejected`).
- New audit-journal event kinds: `OrganizationMembershipRequestCancelled` (request id, actor, reason) and `OrganizationMembershipRequestReplaced` (old id, new id, actor, **names** of changed fields, reason; never an email). Both go through the archived, replayable security-event body path (G14) like every other command.

### 2.3 Concurrency and stale requests

`expected_request_digest` is the SHA-256 of the canonical editable content plus state (display-name hint, initial role, reason, state, id). If the request was cancelled, replaced, decided or expired since the owner opened the dialog, the command fails with `409 MEMBERSHIP_REQUEST_CHANGED` and nothing changes. A second owner editing the same request at the same time loses with the same code (the lock serializes; exactly one replacement is created).

### 2.4 Approval invalidation (the security argument)

- An operator can approve only a `Pending` request, re-read under the organization lock. `Cancelled` and `Replaced` are refused (`MEMBERSHIP_REQUEST_NOT_PENDING`, 409), including a dry run, so a stale approval attempt (an operator who loaded the old request) cannot succeed.
- The new request has a different id, and the approval statement includes the request id, so **no approval signed for the old request verifies for the new one**, whatever it contained.
- Editing/cancelling never creates or changes a binding, role or session; an already `Approved` request is immutable (the commands refuse any state but `Pending`).

### 2.5 Schema (AuthorityDb migration `MembershipRequestEditCancel`, multiword snake_case)

`organization_membership_requests`: allow states `Cancelled` and `Replaced` in the state check; add `request_cancelled_on`, `request_cancelled_by_authority_user_id`, `request_cancellation_reason_text`, `replaces_membership_request_id` (self FK), `replaced_by_membership_request_id` (self FK); checks that cancellation columns are present exactly for `Cancelled`/`Replaced`, that a replacement is linked on both sides, and that a non-`Pending` row never changes again. Both id columns are added to the runtime grants as column-level `UPDATE` only for the cancellation/lineage columns. Roster read (`/internal/v1/organizations/roster`) returns the two new states and `replaced_by_membership_request_id`; operator list shows them.

### 2.6 Business API and UI (after approval)

- `POST /api/v1/organization/membership-requests/{requestId}/cancel` and `/replace` (Idempotency-Key, reason, development step-up, `expectedRequestDigest`); business audit events `MEMBERSHIP_REQUEST_CANCELLED` / `MEMBERSHIP_REQUEST_REPLACED`; the approvals list invalidated so the Membership requests section updates without a reload.
- Membership requests Actions column, by status: **Pending** ("Awaiting operators"): Edit request, Cancel request, Copy ID; **Approved**: View details, Copy ID; **Rejected / Expired / Cancelled**: View details, Create new request; **Replaced** shows "Replaced by …" with a link to the replacement. Edit dialog shows display name, initial role (None/Designer) and reason; the Object ID, verified identity and issuer are read-only text with the note that a wrong identity needs Cancel and a new request. Cancel uses the shared confirmation dialog.

### 2.7 Tests (to be implemented and executed after approval)

Authority unit/real-stack: owner-only (clerk, designer, Security Administrator, another organization's owner refused); step-up required; edit creates a new id with lineage and a fresh expiry; identity unchanged and never accepted from the request; cancel then approve refused (dry run and execute); replace then approve-old refused; approve-new works with two approvals; a stale digest, a concurrent double edit (one winner), cancel after decision, cancel after expiry; idempotent retry returns the recorded outcome; the audit journal contains the events and the replayable archive bodies; a cancelled identity can be requested again; one pending request per identity still holds. Business: handler, contract and OpenAPI snapshot tests; Vitest for the dialogs and status actions; Playwright for edit, cancel with confirmation, status-dependent actions, keyboard and axe, five-row paging with the new states, no overflow at 320/768/1440 px.

## 3. Decisions requested

1. **Approve the two new Authority commands and the `MembershipRequestEditCancel` migration** as specified above (cancel-and-replace, no in-place edit).
2. **Who may cancel/edit:** any enabled owner of the organization (recommended — owners already see every request and any owner can approve the people changes), or only the original requester?
3. **State naming:** a separate `Replaced` state (recommended; the list can say "Replaced by …") versus recording an edit as `Cancelled` with a link.
4. Confirm that an edit restarts the 7-day expiry (recommended: the replacement is a new request).

Until approved, the People page shows pending requests read-only (Copy ID only), exactly as before.
