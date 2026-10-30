# Phase C8 M2 — AC-7 security contract (user management in the Authorization Authority)

- **Status:** proposal for owner and security approval (C-93, 2026-10-30). **Nothing in this document is implemented.**
  No Authority command, relation, table, column, endpoint or SpiceDB schema was changed. Approval checkpoint: no AC-7
  Authority code is written until the decisions in §9 are recorded.
- **Audience:** the project owner (decisions), the independent Authority reviewer (AC-5/AC-6/AC-9 package), implementers.
- **Supersedes:** the AC-7 sketch in `PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §4 (M2) and §5.1–§5.4, which it makes
  specific. C-78 conditions are carried forward unchanged.
- **Already delivered without AC-7** (branch `phase-c8/m2-user-management`, `docs/PHASE_C8_STATUS.md` §14): server-paged,
  searchable People directory; Authority-bound members who never signed in (Security Administrators only, from the
  existing roster read); last sign-in; Security Administrator appointment/removal from the People page through the existing
  `GrantSystemRole`/`RevokeSystemRole` commands.

## 1. Verified facts about the current Authority (2026-10-30)

| # | Fact | Where |
|---|---|---|
| F-1 | Identity bindings are created **only** by operator bootstrap (`POST /internal/v1/operator/organizations`). There is no post-bootstrap membership addition. | `OrganizationBootstrapService` |
| F-2 | `identity_bindings.identity_binding_enabled` already exists and is already enforced: `ActorResolver` refuses a disabled binding on every delegated call, `SessionService.Validate` refuses sessions of a disabled binding, the Security Administrator rules count only enabled bindings. No command can change it today. | `IdentityBinding`, `ActorResolver`, `SessionService` |
| F-3 | `GrantSystemRole`/`RevokeSystemRole` manage **only** `system_administrator` (Security Administrator), require an existing Security Administrator, a reason (1–500), step-up (`FieldSearchApprovalGate.RequireStepUp`: synthetic in Development/Testing, unavailable in staging/production) and keep the last enabled administrator. | `CommandPlanner.SecurityAdministrators` |
| F-4 | `organization_owner` and `designer` edges are written only at bootstrap. No command grants or revokes them. | `CommandPlanner` (no kind) |
| F-5 | No roster read exists for owners/designers or binding state. The Security Administrator roster returns administrators to owners and Security Administrators, and every other **enabled** binding (as appointment candidates) to Security Administrators only. | `SecurityAdministratorRosterService` |
| F-6 | `SecurityCommandRequest` already carries an optional `ApprovalReference`; commands are journaled with idempotency, expected revision, outbox and the Effective gate (§4N); recovery replays the journal (G14). | contracts, `SecurityCommandService` |
| F-7 | Real Entra OIDC sign-in (PB-08) is not implemented; only the Development/Testing persona login exists. A binding created for a real Entra identity could not sign in yet. | `SessionEndpoints` |

## 2. Scope

| Capability (owner request, 2026-10-30) | Contract | Needs |
|---|---|---|
| Show all members with roles and membership state | AC-7.3 roster read | D8-24 |
| Grant / revoke Organization Owner | AC-7.1 with two distinct owners (C-78) | D8-25, D8-26 |
| Grant / revoke Designer | AC-7.1, one owner (C-78) | D8-25 |
| Disable / re-enable membership | AC-7.2 | D8-27, D8-28 |
| **Add user** from the website | AC-7.4 in-app request + operator dual approval (C-78: operator process, Development/Testing only) | D8-29, D8-30 |
| Security Administrator | **Unchanged** (C-70 rules). Already on the People page. | — |
| Step-up | PB-08 gate unchanged: synthetic in Development/Testing, refused in staging/production | D8-31 |

Out of scope: email invitations, Entra Graph lookups, self-service sign-up, deleting identities, SCIM, auditor role (AC-8).

## 3. AC-7.1 Organization roles

**Command kinds:** `GrantOrganizationRole`, `RevokeOrganizationRole`, and `ProposeOrganizationOwnerChange` (owner changes only).

**Payload (canonical JSON, unknown fields rejected):**

```json
{ "authority_user_id": "uuid", "role_name": "organization_owner | designer", "reason": "1..500 chars", "step_up_evidence": "…" }
```

`ProposeOrganizationOwnerChange` adds `"change": "grant | revoke"` and returns its command id as the **proposal id**.

**Rules:**

| # | Rule |
|---|---|
| R1 | Actor must be an enabled `organization_owner` of the same organization. `administer_security` does not qualify (G25 extension); `designer` does not qualify. |
| R2 | No self-grant, self-revoke, self-proposal or self-approval. |
| R3 | Target: an enabled identity binding of the same organization (issuer + subject). Never an email, business row or client flag. |
| R4 | `designer` grant/revoke: one owner (C-78). |
| R5 | `organization_owner` grant/revoke: **two distinct owners** (C-78). Owner A submits `ProposeOrganizationOwnerChange`; owner B (≠ A, ≠ target) submits `Grant`/`RevokeOrganizationRole` with `ApprovalReference` = proposal id. The Authority checks: proposal exists in this organization, is Effective, not consumed, not expired (proposed default 24 h), identical target/role/change, and that A is still an enabled owner. Consumption is recorded in the same journal transaction. |
| R6 | The last enabled `organization_owner` can never be revoked (409 `LAST_OWNER`). Recovery stays on the operator plane. |
| R7 | Granting a role already held, or revoking one not held → 409 (idempotent replay of the same key returns the original outcome). |
| R8 | Step-up per F-3; reason required; journaled; business audit `ORGANIZATION_ROLE_GRANTED` / `_REVOKED` / `ORGANIZATION_OWNER_CHANGE_PROPOSED`. |

**Effect:** add/remove the SpiceDB edge `organization:{org}#{role}@user:{id}` through the existing desired-state pipeline;
Effective only after projection (§4N). A revoked owner loses `manage_configuration` at the next decision after Effective;
a revoked designer loses `design_form`/`create_form` on that boundary. Record ownership and shares are untouched.

## 4. AC-7.2 Disable and re-enable membership

**Command kinds:** `DisableIdentityBinding`, `EnableIdentityBinding`. Payload: `authority_user_id`, `reason`, `step_up_evidence`.

| # | Rule |
|---|---|
| D1 | Actor: enabled `organization_owner` (R1). Whether a Security Administrator may also disable is D8-27. |
| D2 | No self-disable / self-enable. |
| D3 | Refused for the last enabled owner (`LAST_OWNER`) and the last enabled Security Administrator (`LAST_SECURITY_ADMINISTRATOR`). |
| D4 | Refused while the identity directly owns active records (C-78); the refusal reports a **count only** (`OWNS_RECORDS`, `owned_record_count`). Reassignment uses the existing audited `TransferOwnership` override. |
| D5 | Disabling an owner: one owner or two (D8-28). |
| D6 | In one Authority transaction: set `identity_binding_enabled = false`, the new lifecycle columns, and revoke **all** of the identity's Authority sessions (and record unlocks if present). Edges (roles, teams, shares, form users) are **kept** (C-78, D8-5) and become unusable because `ActorResolver` refuses the binding. |
| D7 | Re-enable restores exactly the kept edges; nothing new is granted. |
| D8 | Bytes already delivered cannot be recalled (documented, not claimed otherwise). |

**AuthorityDb (additive migration):** `identity_bindings.identity_disabled_on timestamptz null`,
`identity_disabled_by_security_command_id uuid null`, `identity_enabled_by_security_command_id uuid null`, plus a check
that `identity_binding_enabled = false` ⇔ `identity_disabled_on is not null`. Replay (G14) must reproduce them;
`AuthorityRuntimeGrants` updated for the new columns.

**Business side:** the projection may set `users.is_active` only from an Effective Authority outcome (never from an
administrator request); the People page shows "Disabled" only from the AC-7.3 read.

## 5. AC-7.3 Roster read

`POST /internal/v1/organizations/roster` (delegated user, business workload credential). Allowed for
`manage_configuration` or `administer_security`; otherwise the generic not-found. Returns, behind the organization gate:

```json
{ "authorization_revision": 41,
  "members": [ { "authority_user_id": "…", "issuer_uri": "…", "subject_identifier": "…", "binding_enabled": true,
                 "bound_on": "…", "disabled_on": null,
                 "organization_roles": ["organization_owner", "designer", "system_administrator"] } ],
  "pending_owner_proposals": [ { "proposal_id": "…", "change": "grant", "authority_user_id": "…", "proposed_by": "…", "expires_on": "…" } ],
  "pending_membership_requests": [ … ] }
```

No record content, no record counts (the owned-record count is returned only by a D4 refusal), no session data.
The business People page replaces its partial merge with this read (all members, roles and state for owners too).

## 6. AC-7.4 Add user (in-app request, operator dual approval)

C-78 keeps member additions on the operator two-approval process in Development/Testing. The owner now asks for an
**Add user** button. Proposed design that respects both:

1. **Business UI:** owner enters the identity: **issuer** (pre-filled with the organization's configured Entra tenant
   issuer, not editable unless D8-30 allows other issuers), **subject** (Entra object id, validated as a GUID for Entra),
   a display-name hint (≤200, display only, never a key) and optional initial role `designer` (never `organization_owner`;
   owners use AC-7.1 after the binding exists). Email is not accepted as an identifier.
2. **Authority command** `RequestOrganizationMembership` (owner, step-up, reason): journals a *pending request* only — no
   binding, no edge, no permission. Duplicate issuer/subject in the organization → 409. Expires after 7 days (proposed).
3. **Operator plane** `POST /internal/v1/operator/organizations/{id}/membership-requests/{requestId}/approve` with two
   ES256 approvals from distinct registered approvers (the DR-C7-3 machinery, `OPERATOR_DUAL_CONTROL_*`), Development/
   Testing only until production PIM exists. Approval creates the binding (and the optional designer edge) in one journaled
   command; rejection or expiry closes the request.
4. **Business side** shows the request as **Pending approval** (never as a member) until the roster read returns the binding.
5. **Limitation (F-7):** until PB-08 OIDC sign-in exists, an added real Entra identity cannot sign in; in Development/
   Testing only seeded personas can. The UI says so.

Alternative for D8-29: keep "Add user" as operator-only (no in-app request) and show the operator procedure instead.

## 7. Business API (after approval)

| Route | Command / read |
|---|---|
| `GET /api/v1/organization/members` | switches to AC-7.3 (same query parameters and 15-row paging as today) |
| `GET /api/v1/organization/members/{authorityUserId}` | PG-051 detail from AC-7.3 + teams |
| `POST /api/v1/organization/members/{id}/roles` `{ roleName, reason, approvalReference? }` | AC-7.1 grant / approve |
| `POST /api/v1/organization/members/{id}/roles/{roleName}/remove` | AC-7.1 revoke |
| `POST /api/v1/organization/members/{id}/owner-proposals` | `ProposeOrganizationOwnerChange` |
| `POST /api/v1/organization/members/{id}/disable` / `/enable` | AC-7.2 |
| `POST /api/v1/organization/membership-requests` | AC-7.4 request |

Every command: `Idempotency-Key`, reason, development step-up flag, `CommandStatusResponse` (Pending/Effective/Rejected);
Pending is never shown as done. Durable operations reuse the C8-M1-01/AC-9 reconciler pattern; AC-9's lookup would be
extended to these command kinds (needs its own approval; RO-2 already registers the gap for other handlers).

## 8. Adversarial tests (real Authority, both PostgreSQL databases, SpiceDB, Vault)

AC7-T01…T16 from the M0 plan are kept. Additions:

| Test | Scenario | Expected |
|---|---|---|
| T17 | Owner grant with no proposal, with own proposal, with an expired or consumed proposal, or a proposal for another target/role | 403/409; no edge |
| T18 | Proposer approves own proposal; target approves own proposal | 403 |
| T19 | Proposer loses owner role before approval | approval refused |
| T20 | Two concurrent approvals of one proposal | exactly one Effective |
| T21 | Roster read by designer, clerk, other organization | not-found; no counts |
| T22 | Membership request: duplicate subject, email as subject, non-tenant issuer (unless D8-30), designer-only initial role enforced | refused |
| T23 | Membership request approved with one approval, two approvals from the same approver, or outside Development/Testing | refused |
| T24 | Business DBA inserts a `users` row or sets `is_active` | no binding, no session, not listed by AC-7.3 |
| T25 | Disabled user's open SPA session: next API call, upload finalize, download, search | all refused; session revoked at the Authority |
| T26 | Restore (G14) from before a disable | replay keeps the identity disabled |

## 9. Owner decisions required

| ID | Decision | Proposal |
|---|---|---|
| D8-24 | Approve the AC-7.3 roster read (§5), including pending proposals and requests | Approve |
| D8-25 | Approve AC-7.1 command kinds and rules R1–R8 | Approve |
| D8-26 | Owner **revocation** also needs two distinct owners (C-78 named grants only) | Yes, symmetric with grants |
| D8-27 | May a Security Administrator disable/enable members? | No: owners only (G25 separation); Security Administrators keep their own role commands |
| D8-28 | Disabling an **owner** needs two distinct owners | Yes (same proposal mechanism as R5) |
| D8-29 | Add user: in-app request + operator dual approval (§6), or operator-only | In-app request + operator dual approval, Development/Testing only until PIM |
| D8-30 | Identity issuers accepted for Add user | The organization's configured Entra tenant issuer only |
| D8-31 | Confirm: no AC-7 command is available in staging/production until PB-08 step-up exists | Confirm |
| D8-32 | Proposal and request lifetimes (24 h owner proposals, 7-day membership requests) | Approve, configurable within bounds |

Independent review: AC-7 joins the AC-5/AC-6/AC-9 package. Nothing may be called independently reviewed until that
review exists (C-85).

## 10. Implementation order once approved

1. AuthorityDb migration (lifecycle columns, proposals/requests tables with multiword snake_case names, UUIDv7 keys),
   runtime grants, desired-state planner rules, unit tests.
2. AC-7.3 roster read + business People page switch.
3. AC-7.1 roles (designer first, then dual-owner proposals).
4. AC-7.2 disable/enable with session revocation; G14 replay test.
5. AC-7.4 request + operator approval; UI "Pending approval".
6. AC7-T01…T26 on the security-test stack; Playwright; hosted CI; status, decision log.
