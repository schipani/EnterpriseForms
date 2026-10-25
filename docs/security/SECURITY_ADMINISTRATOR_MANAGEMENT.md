# Security Administrator management (Phase C7, owner message 7)

**Status:** appointment, removal and the Organization settings section are implemented and tested on the isolated security-test stack. **The first Security Administrator of an organization that already exists is not implemented:** it needs a new privileged Authority operator contract, presented here as decision **DR-C7-3** and not built. Production and staging cannot change Security Administrators until PB-08 step-up exists. Nothing here is an independent security review or a Level 2 claim.

## 1. Role model (inspection)

There is one role system: organization relations held by the independent Authorization Authority (AuthorityDb desired state, projected to SpiceDB, `authority.schema.zed`).

| Relation | SpiceDB permission | Business capability | UI name |
|---|---|---|---|
| `organization_owner` | `manage_configuration` | `organization.manage` | Organization owner |
| `designer` | `create_form` | `forms.design` | Designer |
| `system_administrator` | `administer_security` | `security.administer` | **Security Administrator** |

"Security Administrator" is the existing `system_administrator` relation; no second role or table was added. Business-database user, team or role rows confer nothing.

**Why the development organization has none.** Organization roles were granted only at creation, by the one-shot operator bootstrap (`POST /internal/v1/operator/organizations`), which returns an existing organization unchanged. The development organization was bootstrapped before the dev seed added the "Sam Security" persona, so it holds `organization_owner`, `designer` and members, and no `system_administrator`. `GrantSystemRole` / `RevokeSystemRole` were enumerated command kinds that the planner refused as not implemented. So no approved operation could give an existing organization a Security Administrator.

## 2. What the role gives, and what it never gives

A Security Administrator may (each decided by the Authority, most with step-up):

* approve and withdraw field-search approvals and grant or revoke `field_searcher` (Phase C7 M2);
* appoint and remove Security Administrators (this change);
* the earlier Stage A/B organization-administration commands it already held: transfer record ownership **between other principals**, manage teams **for others**, set form users, register published versions, and the A1 governance actions within policy (two-person where specified).

It never gives — enforced by the Authority, tested on the real stack:

* record ownership or record content (no discover/read; an ownership override may not make the administrator or a team containing the administrator the owner);
* Form Designer rights (`create_form` is `designer` only);
* the organization's encryption policy (`UpdateMandatoryPolicy` is organization-owner only);
* membership of a team on their own authority (a Security Administrator cannot add themself to a team; an owner can);
* access to another organization, its roster or its members;
* decryption of Confidential/Restricted data, bypass of encryption floors, or search of encrypted values (approvals are refused for anything encrypted, Confidential/Restricted or below the floor).

The ownership-override, encryption-policy and team self-join limits are separation-of-duties tightenings made in this change (finding **F-C7-02**: before it, an administrator override could transfer a record to the administrator, a Security Administrator could join a record-owning team, and `UpdateMandatoryPolicy` accepted `administer_security`; the business API already required `organization.manage` for the policy, so no data was exposed through the product).

## 3. Appointment and removal

`GrantSystemRole` / `RevokeSystemRole` security commands on the organization, payload `{authority_user_id, role_name: "system_administrator", reason, step_up_evidence}`:

1. The actor must currently hold `system_administrator` in **this** organization (desired state, organization gate open). Organization owners, designers and members are refused `FORBIDDEN` — including for themselves and including when the organization has no Security Administrator.
2. `role_name` must be `system_administrator`; other roles are refused (this is not a general role-assignment command).
3. The target must be an enabled identity binding of the organization (a verified principal: issuer + subject). Another organization's user, a disabled binding, a free-text name or an email address cannot be appointed.
4. A reason of 1–500 characters (the business API asks for 3–500).
5. Step-up: the shared `FieldSearchApprovalGate`. Unavailable by default and in staging/production (`STEP_UP_REQUIRED`); the synthetic development marker is accepted only where the Authority started in Development/Testing with the synthetic TestIdentityProvider (startup refuses otherwise). The marker is not journaled.
6. Appointing an existing administrator, or removing a non-administrator, is `INVALID_COMMAND`.
7. **Last-administrator protection:** a removal is refused `409 LAST_SECURITY_ADMINISTRATOR` unless another administrator with an **enabled** identity remains; self-removal is allowed while one does. An administrator whose identity was disabled can still be removed (the binding requirement applies to appointments only).
8. Committed through the normal journal, projection barrier and archive path, like every security command (`CommandStatus = Effective` before the business API reports success).

**Audit.** Committed commands are in the Authority's authorization stream (actor binding, target, reason, revision). Every refused appointment or removal appends `SecurityAdministratorCommandRejected` (command id, kind, actor binding, rejection code, correlation — no reason text) to the Authority's audit stream; the business API sends every attempt to the Authority, so owners' attempts are recorded there too. The business audit timeline adds `SECURITY_ADMINISTRATOR_APPOINTED` / `SECURITY_ADMINISTRATOR_REMOVED` (actor, target, reason) as a local projection. G24 (asynchronous archive copy of audit-stream events) applies to the refusal evidence.

**Sessions and capabilities.** Capabilities are recomputed from the Authority on each request (`/api/v1/dashboard`, `/api/v1/session` refresh). A removed administrator's existing bearer token and live browser session lose `security.administer`, Search approvals and the roster on their next request; nothing is cached in the cookie (G23: session state is Authority-held; a signed-out cookie cannot be replayed). Unsafe browser requests need the session's own CSRF token (G22).

## 4. Organization settings section

`Organization` navigation (owners and Security Administrators) → **Security Administrators** card: names (directory labels), verified identity (subject · issuer), appointment date, status; *Add administrator* (choose a verified member from a list; reason; step-up confirmation) and *Remove* (confirmation, reason, step-up) for Security Administrators only; the Remove button is disabled for the last administrator with an explanation; owners see the list read-only with "Only Security Administrators can appoint or remove Security Administrators."; an empty roster shows that owners cannot appoint themselves and that the first administrator comes from the operator process; where step-up is unavailable the dialogs say so and the Authority refuses. A Security Administrator who is not an owner sees only this section of Organization settings (no branding, no policy).

## 5. API contracts

Business (`/api/v1`, feature Organizations, `Cache-Control: no-store`):

* `GET /organization/security-administrators` → `SecurityAdministratorsResponse {canManage, appointmentOperational, appointmentUnavailableReason, administrators[], candidates[]}`; owners read-only (no candidates), others `403`.
* `POST /organization/security-administrators` `{userId, reason, useDevelopmentStepUp}` → `204` once the command is Effective.
* `POST /organization/security-administrators/{userId}/remove` `{reason, useDevelopmentStepUp}` → `204`; `409 LAST_SECURITY_ADMINISTRATOR`. (No roster in the response: an administrator who removed themself may no longer be allowed to read it; the UI refetches.)

Authority (private, business workload credential + delegated user assertion):

* `POST /internal/v1/security-administrators` `{organization_id}` → roster with verified identities and appointing command id; candidates only for administrators; `404` for anyone else, other organizations included.
* `POST /internal/v1/security-commands` with `GrantSystemRole` / `RevokeSystemRole` (above).

## 6. The first Security Administrator — decision DR-C7-3 (not implemented)

**Today:** the first administrator exists only when the operator bootstrap creates the organization with a `system_administrator` identity (Stage A stand-in for PIM / dual control). After creation, appointment needs an existing administrator. An organization without one — the development organization — has no supported path, by design. Repeating the bootstrap creates nobody (tested).

**Rejected shortcuts:** owner self-elevation; granting from an email, a business-database row or a client flag; a development database update; resetting or recreating the organization; an automatic grant in deployment or migration.

**Proposed contract (requires owner decision before implementation):**

* `POST /internal/v1/operator/organizations/{organizationId}/security-administrators` on the operator plane only (operator credential; never the business workload).
* Body: `confirm_organization_id` (must equal the path), `authority_user_id` (an existing enabled binding — no new identity is created), `mode` = `FirstAppointment` (refused when any administrator exists) or `Recovery` (allowed when every administrator is gone or locked out; reason and incident reference required; distinct audit event and alert), `approval_reference`, `requested_by` and **`approved_by` — two distinct operator principals** (dual control).
* Effect: a journaled `GrantSystemRole` security command attributed to the operator workload, an audit-stream event (`SecurityAdministratorBootstrapped` / `SecurityAdministratorRecovered`) naming approver, appointee, time and organization, the normal projection barrier and archive.
* Production: behind PIM just-in-time access and the separately permissioned Authority release path (ADR-PB-011/PB-012); staging the same; development via `scripts/devstack/devstack.py` with the development operator key, explicit confirmation of the organization and appointee, no automatic run.

**Security implications:** holders of the operator credential could create a Security Administrator in any existing organization. Mitigations: dual control, first-appointment refusal when an administrator exists, recovery as a separately alerted mode, an independently archived audit trail, and the role's limits (no records, no designer rights, no policy, step-up for its own actions). The operator plane already can create organizations with administrators, so the trust root does not widen in kind, but it becomes usable after creation — that is the decision the owner must accept or refuse.

**Recovery** (all administrators removed is impossible through the product because of last-administrator protection; loss of the person or identity is possible): the `Recovery` mode above. Until DR-C7-3, recovery is not available.

## 7. Development organization (`http://127.0.0.1:8091`)

**Missing prerequisite:** an independently trusted bootstrap principal for an existing organization — the DR-C7-3 operator contract. The development organization has none, so no one there can be appointed, and Search approvals cannot be administered there. Nothing was changed in it (no membership, grant, key or data change).

**Safest supported path until DR-C7-3:** use the disposable security-test stack, whose organization is bootstrapped with the Sam Security persona (operator bootstrap at creation). After DR-C7-3 is accepted and implemented: run the development operator command naming the development organization and the chosen verified identity, confirm, then that administrator approves fields and grants a test user search access (`docs/PHASE_C7_STATUS.md` §19).

## 8. Tests

* Authority unit (`tests/authority/.../SecurityAdministratorTests.cs`): appointment; owners/designers/members refused; role, binding, step-up and gate; last administrator; ownership override; team self-join; policy owner-only.
* Real stack (`tests/integration/.../SecurityAdministratorTests.cs`): appointment and immediate Search approvals; no record content; no designer rights; owner/designer/member refusals through the API and directly at the Authority, all audited; repeated bootstrap creates nobody; cross-organization appointment, removal and roster refused; removal, last-administrator protection and loss of privilege on existing tokens; step-up and missing/forged/expired credentials; browser sessions: CSRF, removal of a live session's privilege, signed-out cookie replay.
* Browser (`src/frontend/e2e/security-administrators.spec.ts`): owner read-only; appoint, appointee's Search approvals, removal, last-administrator protection; axe WCAG 2.2 AA.
* Staging/production synthetic shortcut: the shared gate's startup refusals (`FieldSearchApprovalTests.TheDevelopmentStepUpPathCannotBeEnabledInStagingOrProductionOrWithARealIdentityProvider`) and the Unavailable-gate refusal.

## 9. Open

G25 (independent review of F-C7-02), G26 / DR-C7-3; PB-08 (no step-up, so no changes in staging/production); G24; independent review of the Authority changes; Azure staging evidence. G06, G19, G21 and PB-09 are unaffected and remain open.
