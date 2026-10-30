# Security Administrator management (Phase C7, owner message 7)

**Status:** appointment, removal and the Organization settings section are implemented and tested on the isolated security-test stack (C-70). **DR-C7-3 is implemented (C-71):** separate operator-plane operations for the first Security Administrator of an existing organization and for recovery, each executed only with two ES256 approvals from distinct registered approver keys. In staging and production these operations, like Security Administrator changes in the product, fail closed until the privileged identity (PIM) approval model and PB-08 step-up exist. Development uses clearly labelled synthetic approver keys. Nothing here is an independent security review or a Level 2 claim.

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

## 6. First and recovery Security Administrator (DR-C7-3, C-71)

### 6.1 Operations — operator plane only

All under `/internal/v1/operator/organizations/{organizationId}/security-administrators` and the operator credential (`OperatorCaller` policy). The business workload credential, a user token, a business workload carrying a user assertion, and no credential are refused (401/403, tested); the business API has no route and the product has no UI for them.

| Operation | Purpose |
|---|---|
| `GET …/security-administrators` | Organization (Authority record), revision, verified members (user id, issuer, subject, enabled), current Security Administrators, usable count, approval mode and assurance. Identities only. |
| `POST …/first-appointment` | First Security Administrator when the organization has **no Security Administrator with an enabled identity**. |
| `POST …/recovery` | Recovery when Security Administrators exist but are unavailable: one appointee plus an explicit list of administrators to remove; **incident reference required**; critical alert. |

Request: `operation_id` (single use), `confirm_organization_id` (must equal the path), `target_authority_user_id` + `target_issuer_uri` + `target_subject_identifier` (must match an **enabled identity binding** of this organization exactly — no new identity is ever created, no email or business row is consulted), `justification` (10–500 characters), recovery also `incident_reference` (`^[A-Za-z0-9][A-Za-z0-9._:/-]{2,63}$`) and `removed_administrator_ids` (≤ 20, current administrators, never the appointee), `dry_run`, `approvals`.

**Dry run** returns the exact organization, member identity, role (`system_administrator` only), current administrators, the recovery scope, the consequences, the approval requirements and the **approval statement** (canonical JSON) with its SHA-256. Nothing is changed or journaled.

### 6.2 Dual control

Each approval is a compact JWS, `alg` exactly `ES256`, `kid` = approver id, signed by that approver's **own P-256 key**, with claims `iss=enterpriseforms-operator-approval`, `aud=authorization-authority-operator-approval`, `sub` = approver id, `jti`, `iat`, `nbf`, `exp`, `statement_sha256`, `operation_id`, `operation_kind`, `organization_id`, `target_authority_user_id`. The statement binds the organization, member identity, role, kind, justification, incident reference, recovery scope and operation id.

The Authority accepts an execution only with **exactly two** approvals that: verify against two **different registered approver keys** (`OPERATOR_APPROVALS_NOT_INDEPENDENT` for the same approver twice); carry the right issuer, audience and approver; match this statement digest and every bound claim (`OPERATOR_APPROVAL_INVALID` otherwise — wrong organization, member, kind, operation or statement); have `exp − iat` ≤ the configured maximum (default 900 s) and are unexpired (30 s skew). The Authority holds **public keys only**, so neither the operator credential nor the Authority itself can produce an approval, and one approver key cannot produce two. A single operator credential or session is therefore never enough.

Modes (`OperatorApprovals:Mode`):

* `Unavailable` — the default and the **only** mode outside Development/Testing. Every execution is refused `OPERATOR_DUAL_CONTROL_UNAVAILABLE` (dry runs still work). Production approvals must come from the privileged identity model (PIM-activated approver identities with independently held keys); that is not implemented, so production fails closed.
* `SyntheticDevelopment` — Development/Testing only; startup refuses it elsewhere, refuses unknown modes, fewer than two approvers, duplicate ids or keys and non-P-256 keys. The verification is identical, but one person usually holds both keys: it is recorded as *"SyntheticDevelopment (development-only approver keys; not two-human approval evidence)"* and never presented as two-human approval.

### 6.3 Execution

Inside the organization lock: idempotency (the operation id is the command's idempotency key — the same statement again returns the recorded outcome with `replayed: true` and changes nothing; a different statement under the same id is `IDEMPOTENCY_CONFLICT`), approval verification, then the preconditions re-checked on the Authority's own state:

* first appointment: target enabled member, not already an administrator, and **no administrator with an enabled identity** (`SECURITY_ADMINISTRATOR_EXISTS` otherwise; an administrator whose identity is disabled is not usable);
* recovery: at least one administrator exists (`RECOVERY_NOT_APPLICABLE` otherwise — use a first appointment), the named removals are current administrators.

One security command (`ProvisionFirstSecurityAdministrator` / `RecoverSecurityAdministrator`) adds the `system_administrator` relation (and, for recovery, removes the named ones) through the normal journal, projection barrier and archive, and advances the authorization revision — so catalogue snapshots and every capability check see the change at once. Concurrent requests serialize on the lock: two concurrent first appointments yield exactly one administrator (tested). Nothing else is granted: no designer rights, record ownership or content, policy change or other organization.

The command's actor binding column holds the **appointee's** binding (operator commands have no organization identity, as in the bootstrap); the operator workload, approvers, assurance, justification and incident are in the command payload and the audit event.

### 6.4 Evidence and alerts

* Authorization stream: the committed command (payload: operation, kind, target identity, justification, incident, removed ids, operator workload, assurance, statement digest, approver ids with approval digests — never the approvals themselves).
* Audit stream (hash-chained, archived): `SecurityAdministratorFirstAppointed` / `SecurityAdministratorRecovered` and `SecurityAlertRaised`; every refusal `SecurityAdministratorProvisioningRejected` (approver ids only when verified); refused recoveries also raise an alert.
* Alert: `ISecurityAlertSink` → structured log event 9100 `SecurityAlert` ("SECURITY ALERT …"; Critical for recovery, Warning otherwise). Production must route it to a monitored SIEM rule (not configured here).

### 6.5 Security implications and residual risks

* A first appointment or recovery needs the operator credential **and** two approver keys; compromise of one is insufficient. Compromise of the operator credential plus both approver keys creates a Security Administrator — whose powers remain limited (§2) and step-up gated.
* Production: dual control fails closed until PIM-backed approvals exist, so in production a first Security Administrator still only comes from the organization bootstrap. The operator credential itself is still the Stage A shared HS256 credential (production requires Entra workload identity — existing gap).
* The incident reference is bound into both approvals (the approvers attest to it); it is not verified against an incident system.
* Synthetic development keys: one person holds both; not evidence of two humans.
* Alerting is a log event locally; durable evidence is the archived audit stream (G24 applies to its archive lag).

## 7. Development organization (`http://127.0.0.1:8091`) — supported procedure

Nothing was changed in the development organization. The owner runs, from the repository root (PowerShell or Git Bash):

1. `python scripts/devstack/devstack.py backup` (optional, recommended), then `python scripts/devstack/devstack.py rebuild authority api web` — new application images; Vault, databases, SpiceDB, Azurite, keys, forms and records untouched; no migration in this change.
2. `python scripts/devstack/devstack.py security-admin status` — lists the organization, its verified members with their dev persona, subject and Authority user id, and its Security Administrators (none). Builds the dev-seed tool image first.
3. `python scripts/devstack/devstack.py security-admin enable-approvals` — once: generates two DEVELOPMENT-ONLY synthetic approver key pairs into `%USERPROFILE%\.enterpriseforms\dev-vault\enterpriseforms-dev\operator-approvers.json` (owner-only ACL; never printed; `custody.json` is not touched) and recreates the Authority with the public halves (`OperatorApprovals:Mode=SyntheticDevelopment`). No data or privilege changes.
4. `python scripts/devstack/devstack.py security-admin appoint --persona owner --justification "First Security Administrator for the development organization"` — **dry run**: shows the organization, member, identity, role, current administrators, consequences and the statement digest. Nothing changes.
5. The same command with `--execute`: after the dry run it asks you to type the organization id and `APPOINT`; only then the tool signs the two synthetic approvals and the Authority executes the first appointment. Any mismatch stops with nothing changed.
6. In the browser, sign in again as Avery Owner: navigation shows **Search approvals** and **Organization → Security Administrators**.

No automatic elevation exists in deployment, migration or startup; the operator tool never uses a business-database role, and there is no reusable administrator token (approvals expire in 10 minutes and bind one operation). If a step fails, nothing has been changed; report the error code.

Recovery (only if the appointed administrator becomes unavailable): `security-admin recover --persona <member> --incident <REF> --remove-user-id <administrator id> --justification "..."` (dry run), then `--execute`.

## 8. Tests

* Authority unit (`tests/authority/.../SecurityAdministratorTests.cs`): appointment; owners/designers/members refused; role, binding, step-up and gate; last administrator; ownership override; team self-join; policy owner-only.
* Real stack (`tests/integration/.../SecurityAdministratorTests.cs`): appointment and immediate Search approvals; no record content; no designer rights; owner/designer/member refusals through the API and directly at the Authority, all audited; repeated bootstrap creates nobody; cross-organization appointment, removal and roster refused; removal, last-administrator protection and loss of privilege on existing tokens; step-up and missing/forged/expired credentials; browser sessions: CSRF, removal of a live session's privilege, signed-out cookie replay.
* Browser (`src/frontend/e2e/security-administrators.spec.ts`): owner read-only; appoint, appointee's Search approvals, removal, last-administrator protection; axe WCAG 2.2 AA.
* Staging/production synthetic shortcut: the shared gate's startup refusals (`FieldSearchApprovalTests.TheDevelopmentStepUpPathCannotBeEnabledInStagingOrProductionOrWithARealIdentityProvider`) and the Unavailable-gate refusal.
* DR-C7-3 Authority unit (`tests/authority/.../SecurityAdministratorProvisioningTests.cs`): verifier startup rules (synthetic refused in Production/Staging/Preproduction, unknown modes, fewer than two approvers, duplicate keys or ids, non-P-256); dry run changes nothing; two approvals appoint once with evidence and alert; replay and conflicting reuse; existing administrator refused; disabled administrator not usable; every approval defect (none, one, same approver twice, forged, unregistered, expired, too long-lived, wrong organization/member/kind/statement, wrong algorithm, tampered signature, malformed; Unavailable mode); non-member and identity mismatch; recovery rules, scope and critical alert.
* DR-C7-3 real stack (`tests/integration/.../SecurityAdministratorProvisioningTests.cs`): first appointment end to end (operator status, owner still refused, dry run, two approvals, appointee's existing token gains only `security.administer`, Search approvals works, no record content or designer rights, appointee appoints another administrator through the normal endpoint, audit and alert evidence, authority log alert, replay, conflict, later first appointment refused); approval defects on the real stack; concurrent first appointments → one administrator; non-members, other organizations, non-operator credentials and the business API refused; recovery (incident required, first-appointment approvals rejected, named administrator removed and their token loses the role at once, critical alert).

## 9. Open

G25 (independent review of F-C7-02); G26 (DR-C7-3 implemented; production PIM-backed approvals and independent review outstanding); PB-08 (no step-up, so no changes in staging/production); G24; independent review of the Authority changes; Azure staging evidence. G06, G19, G21 and PB-09 are unaffected and remain open.
