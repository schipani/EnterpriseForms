# Phase C8 — AC-9 independent security review package (C8-M1-01, Design R)

- **Status:** **Prepared for independent security review (C-84, 2026-10-30). Not implemented.** No Authority, Worker, migration, projection or UI change has been made. Nothing is deployed or merged, M2 has not started, and C8-M1-01 is **open**.
- **Owner decisions:** D8-11…D8-19, recorded in C-84 (§0 below). Design R is approved as the direction, **subject to this review**.
- **Base documents:**
  * `docs/architecture/PHASE_C8_M1_01_DURABLE_COMMAND_RECONCILIATION.md` (the C-83 proposal; refinements R-1…R-5 below supersede it where they differ);
  * `docs/architecture/PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §5.5a, §10;
  * `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`;
  * `trust_authority/TRUST_AUTHORITY_ARCHITECTURE.md` §5.
- **Code baseline read for this package:** `phase-c8/administration-governance` at `02c3bb6`. Every "existing behaviour" statement below cites the code it was verified against.

## 0. Owner decisions recorded (C-84)

| ID | Decision | Effect on this package |
|---|---|---|
| D8-11 | Design R approved | AC-9 plus the Worker reconciler, subject to this review |
| D8-12 | Dedicated Worker-only credential | §2 |
| D8-13 | Scope limited to team add/remove; register RO-2 for the other handlers | §8 F-3 |
| D8-14 | Register C8-M1-02; resolve stale ordering in this implementation | §4 |
| D8-15 | **Conditional:** projection while the Authority gate is closed is allowed only with *previously authenticated Effective evidence* and **no integrity quarantine** | §4.5 (interpretation needs confirmation, Q-O1) |
| D8-16 | Timing defaults approved, configurable and tested | §6.3 |
| D8-17 | Keep auditable security-relevant failures and operator actions, plus **durable diagnostic evidence of successful reconciliation** | §5.4 |
| D8-18 | Operations table is non-authoritative; security-relevant fields protected from unauthorized modification; table in the integrity coverage inventory | §5 |
| D8-19 | C8-M1-01 stays open until implementation, RC-01…RC-20 and required hosted verification pass | §7 |

## 1. AC-9 interface contract (proposed)

### 1.1 Endpoint

`POST /internal/v1/security-command-outcomes/lookup` on the Authorization Authority. It is part of the internal Authority contract and **never** appears in the browser-facing business OpenAPI.

**Request**, `SecurityCommandOutcomeLookupRequest`:

| Field | Type | Rule |
|---|---|---|
| `organization_id` | uuid | required |
| `idempotency_key` | string | required; 8–128 characters (the existing Authority key rule); the exact key the business API sent (`team-add:<k>` or `team-remove:<k>`) |
| `expected_command_kind` | enum | required; **allowlist**: `AddTeamMember`, `RemoveTeamMember` |

Headers:
- `Authorization: Bearer <reconciler workload token>`;
- `X-Correlation-Id` (required), which the response echoes.

`X-Delegated-User-Assertion` must be **absent**. If present, the request is refused with 400 `DELEGATED_ASSERTION_NOT_ACCEPTED`, so this endpoint can never act as, or for, a user.

**Response 200**, `SecurityCommandOutcome`:

| Field | Type | Meaning |
|---|---|---|
| `found` | bool | `false` for an unknown organization, an unknown key, or a key whose command kind differs from `expected_command_kind` (one uniform answer; no other fields are populated) |
| `correlation_id` | uuid | echo of the request header |
| `organization_id` | uuid | the queried organization |
| `security_command_id` | uuid | the command bound to (organization, key) |
| `command_kind` | enum | equals `expected_command_kind` |
| `command_state` | enum | `CommittedAwaitingProjection` / `Effective` / `Rejected` / `FailedReconciliation` |
| `committed_authorization_revision` | long? | the command's own revision; null when rejected |
| `rejection_reason_code` | string? | stable code only |
| `team_id`, `member_user_id` | uuid | from the Authority's **normalized command payload**, never from the request |
| `organization_state` | object | `authorization_revision`, `projection_applied_revision`, `projection_gate_state`, `integrity_quarantined` |
| `membership` | object? | **null** when the command is rejected or the organization is integrity-quarantined. Otherwise: `evaluated_at_revision` (= `projection_applied_revision`), `effective_member` (bool), `effective_membership_revision` (long; 0 = never a member), `later_change_pending` (bool) |

`membership` describes **only the (team, member) pair named by the command itself**. The caller cannot choose another pair.

**Errors** (Problem Details; no internal detail):

| Status | Code | When |
|---|---|---|
| 400 | `INVALID_REQUEST` | malformed body or key length |
| 400 | `DELEGATED_ASSERTION_NOT_ACCEPTED` | a user assertion was sent |
| 401 | — | missing, expired, wrongly signed or wrong-audience token |
| 403 | — | a valid token of another workload role (business API, verifier, operator) |
| 422 | `KIND_NOT_RECONCILABLE` | kind outside the allowlist |
| 429 | `RATE_LIMITED` | reconciler rate limit (with `Retry-After`) |
| 503 | `AUTHORITY_NOT_READY` | Authority store unavailable (retryable) |

### 1.2 Semantics (all verified against existing Authority behaviour)

1. **Lookup key.** The lookup uses the unique `(organization_id, command_idempotency_key)` index (`uq_security_command_idempotency`, `AuthorityEntityConfigurations.cs:88`). Rejected commands are persisted with their key (`SecurityCommandService.SubmitAsync`, catch block), so a lookup also finds rejections.
2. **Revision.** `committed_authorization_revision` is assigned at commit as `state.AuthorizationRevision + 1`, under the organization row lock, in the same transaction as the desired-state edges, journal and outbox (`SecurityCommandService.ApplyPlan`). It is **unique and strictly increasing per organization**.
3. **Effective.** `command_state = Effective` is set by `ProjectionProcessor` only when it applies exactly `ProjectionAppliedRevision + 1` (after an archive receipt). Commands therefore become Effective **strictly in revision order**: a command at revision r is Effective exactly when `r ≤ projection_applied_revision`. A divergence marks it `FailedReconciliation` and quarantines the organization (`ProjectionProcessor.cs:74`, `:94–103`).
4. **Pair membership** is computed from the desired-state edges `(team, "member", user)`. Each edge records `CreatedBySecurityCommandId` and `RemovedBySecurityCommandId` (`AuthorityRelationshipEdge`), and each of those commands has its revision.
   * At P = `projection_applied_revision`, the pair is an **effective member** iff some edge was created at a revision ≤ P and is not removed at a revision ≤ P.
   * `effective_membership_revision` is the largest create or remove revision ≤ P among the pair's edges.
   * `later_change_pending` is true iff any of the pair's edges has a create or remove revision > P.
   * All of this is read in **one read-only transaction**, together with the organization state (snapshot isolation; no lock held).
5. **No side effects.** The lookup takes no lock, consumes no revision, writes no journal or outbox row and never touches SpiceDB. It works while the organization gate is closed, because it reports the state SpiceDB already enforces (revision P).

### 1.3 Reconciler-side validation (fail closed)

The reconciler accepts a 200 response only if **all** of the following hold. Otherwise the operation goes to `ReconciliationError` with `INVALID_AUTHORITY_RESPONSE` and an alert, and **nothing is projected**:

- `correlation_id` matches the request, and `organization_id` equals the request and the operation row;
- `found` is true;
- `command_kind` is the expected kind;
- `team_id` and `member_user_id` equal the row; a mismatch is `OPERATION_MISMATCH`;
- `projection_applied_revision ≤ authorization_revision`;
- for a command that is not Rejected: `1 ≤ committed_authorization_revision ≤ authorization_revision`;
- `Effective` ⇒ `committed ≤ projection_applied_revision`; `CommittedAwaitingProjection` ⇒ `committed > projection_applied_revision`;
- `membership` is present ⇔ not Rejected and not quarantined;
- `membership.evaluated_at_revision == projection_applied_revision`;
- `effective_membership_revision ≤ evaluated_at_revision`;
- `Effective` ⇒ `effective_membership_revision ≥ committed`.

## 2. Authentication and authorization model

| Aspect | Proposal |
|---|---|
| Identity | A new workload identity, `business-projection-reconciler`, held **only by the Worker host**. Stage A / local: its own HS256 credential (issuer, audience, ≥ 32-byte key) from deployment secrets, as with the verifier (OD-1). Production: its own Entra managed identity and app role, over mTLS (TRUST_AUTHORITY_ARCHITECTURE §5). |
| Separation | A new scheme and policy, `ReconcilerScheme` / `ReconcilerPolicy`, requiring `workload_role = business-projection-reconciler`. The existing `WorkloadPolicy` requires `business-api`, and verifier and operator use different schemes and keys, so this token satisfies **no other endpoint**, and no other token satisfies this one. Unconfigured ⇒ the policy denies everything (the verifier pattern in `InternalAuthentication`). |
| Deployment | The credential is present in the Worker's configuration only. A compose/Bicep check and an architecture test assert that the **API** container has no reconciler credential and the Worker's reconciler service has no `IAuthorizationAuthorityClient` submit path. |
| Authorization inside the endpoint | No user, no actor binding and no permission graph. The only scope is (organization, key, allowlisted kind). Possessing a key does not grant reading other keys; enumeration needs the key (UUID-entropy client keys). |
| No impersonation | The endpoint refuses delegated assertions and returns no actor identity, reason text or payload beyond team and member. The reconciler never holds a user assertion. |
| No mutation | Read-only by construction (§1.2.5). No command, replay, re-signing, journal write or SpiceDB access. |
| Rate limiting | ASP.NET Core's built-in rate limiter (part of the framework, no new package), partitioned by workload identity: a token bucket of 20 requests per second, burst 40, configurable within bounds. 429 carries `Retry-After`. The Authority has no rate limiter today; this is the first. It is per instance (residual F-7). |
| Audit and telemetry | Every lookup emits a structured Authority log and metric: organization, kind, found, state, outcome code, correlation, latency. **Never** the key. Refusals (401/403/400 assertion/422/429) are counted and alerted. Whether successful lookups must also go to the hash-chained audit journal is reviewer question **Q-3**. Today `GetSecurityCommandStatus` is not journaled. |

## 3. Trust boundaries and threat model

**Trust boundaries:**
1. Worker ↔ Authority: authenticated workload channel; mTLS in production.
2. Worker ↔ BusinessDb: runtime role; BusinessDb is **untrusted for security decisions** (§4M).
3. Authority internal state: trusted (an Authority compromise is out of scope, per §4M).
4. Browser ↔ business API: the business API never forwards AC-9 to browsers.

| # | Threat | Mitigation | Residual |
|---|---|---|---|
| T-A | **Reconciler credential stolen** | Read-only. It reveals the outcome and pair-membership state only for (organization, key) pairs the attacker already knows. Keys live in BusinessDb, whose projection already shows membership. No write, sign or content access. Rate-limited and alerted. Rotation as for OD-1. | Disclosure of team-membership state for known keys (≈ already in BusinessDb) |
| T-B | **Replay of a request** | Idempotent read; no state change. | none |
| T-C | **Replay of an old genuine response** (network position) | The response is bound to the request's correlation id. Projection is monotonic in `effective_membership_revision` (§4), so an older answer can never regress the projection. | — |
| T-D | **Forged Effective response** | Production: mTLS server authentication. All responses are schema- and invariant-validated (§1.3). A forged answer can still only change the *display* roster, which confers nothing. | **Stage A/local runs HTTP inside the Docker network without server authentication** (as do all business→Authority calls today); F-1 |
| T-E | **Stale operation ordering** (C8-M1-02) | Projection mirrors the Authority's pair state at revision P, guarded by a monotonic per-pair watermark (§4). | — |
| T-F | **Concurrent reconciliation** (Workers, API) | One projection writer (R-3); `SKIP LOCKED` leases; per-pair watermark row lock with compare-and-set. | — |
| T-G | **Tampered local operation row** | The projection uses only Authority-returned values for the pair named by the Authority's own command. Identity columns are immutable to the runtime role (§5). A fully forged but consistent row can at most cause the **true** Authority state of some same-organization pair to be projected again. | A DBA can stall reconciliation (liveness), F-4 |
| T-H | **Tampered watermark** | Raised above the Authority revision ⇒ detected (`LOCAL_WATERMARK_AHEAD`; Authority revisions never roll back, G14) ⇒ `ReconciliationError` and alert, nothing projected. Lowered ⇒ only allows a racing stale read, corrected by the next reconciliation of that pair. | F-4 |
| T-I | **Cross-tenant disclosure** | Uniform `found:false` (no "organization not registered" distinction); org-scoped unique key; the answer's organization must equal the row's; projection writes only into that tenant. | Timing side channel (rate limit bounds probing), F-6 |
| T-J | **Lookup used to act for a user** | Assertion header refused; no submit capability in the reconciler (architecture test). | — |
| T-K | **Disabled or revoked administrator during a pending operation** | The Authority decided the command at commit. The reconciler mirrors what became Effective and never re-submits. `NotCommitted` is terminal; restarting is a new, currently authorized user action. | — |
| T-L | **Integrity quarantine** | `membership` is null while quarantined, so there is nothing to project (fail closed by construction). The operation waits and is flagged. | — |
| T-M | **Denial of service on the Authority** | Rate limit, batch bound and backoff (§6.3). The lookup is a single indexed read plus pair edges. | Per-instance limiter, F-7 |

## 4. Operation ordering and state-machine invariants (C8-M1-02)

### 4.1 The ordering source

The only ordering key is the **Authority's committed authorization revision**. It is unique and strictly increasing per organization, assigned at commit, and Effective strictly in that order (§1.2.2–3).

**Never used as order:**
- callback or response arrival time;
- Worker processing time;
- `operation_created_on` and other local timestamps;
- the local operation row's own stored state.

The existing Authority already records enough to derive per-pair order (edge → creating and removing command → revision). **No new ordering guarantee is invented.** What AC-9 adds is only the *read* of that order for one pair.

### 4.2 Projection rule (refinement R-1, replaces the C-83 ledger guard)

The reconciler does not apply "the operation's own effect". It **projects the Authority's effective membership of the operation's (team, member) pair at revision P**, under a monotonic per-pair watermark W, inside one BusinessDb transaction:
1. Lock the pair's watermark row (created if absent with W = 0, present = false).
2. If `effective_membership_revision` < W: `LOCAL_WATERMARK_AHEAD`. Integrity anomaly; nothing projected; alert.
3. If `effective_membership_revision` > W: set the roster to `effective_member`, set W to that revision, and record the outcome.
4. If equal: no change, unless the roster disagrees with the recorded state (drift: Q-5).
5. Complete the operation with its own `superseded_by_later_revision = (effective_membership_revision > committed_authorization_revision)`.

### 4.3 Earlier Add, later Remove, with confirmations in either order

Revisions: Add = r1, Remove = r2, with r1 < r2. Because the Authority makes them Effective in order, "out of order" can only mean the **business side processes the confirmations** in either order:

| Processing order | Authority answer when processed | Result |
|---|---|---|
| Remove first (both Effective) | pair absent at r2 | roster: absent, W = r2. Then Add: absent at r2, r2 = W ⇒ unchanged; Add marked superseded. **Final: absent** ✔ |
| Add first, Remove still pending | present at r1, `later_change_pending` | roster: present, W = r1 (true effective state at that moment). Remove later: absent at r2 > r1 ⇒ absent. **Final: absent** ✔ |
| Add first, both already Effective | absent at r2 | roster: absent, W = r2 (the add is superseded immediately). **Final: absent** ✔ |
| Two Workers race (one read at r1, one at r2) | — | The watermark compare-and-set under the row lock keeps W at r2. A writer with r1 < W changes nothing. **Final: absent** ✔ |

### 4.4 Invariants (each maps to tests, §7)

| ID | Invariant |
|---|---|
| I-1 | A roster change happens only from a validated AC-9 answer obtained **in the same attempt**. A stored local "Effective" value is never evidence. |
| I-2 | The value projected is the Authority's effective pair membership at P, never the requested effect of the local operation. |
| I-3 | W per (tenant, team, member) never decreases; it is written only with the projection, in one transaction. |
| I-4 | Ordering uses Authority revisions only (§4.1). |
| I-5 | The reconciler cannot submit, replay or alter Authority state. |
| I-6 | Organization of the row = the AC-9 request organization = the response organization = the tenant of every write. |
| I-7 | Quarantined ⇒ nothing projected. |
| I-8 | `NotCommitted` is never re-sent. |
| I-9 | Terminal states (`Projected`, `Rejected`, `AuthorityFailed`) never re-open. `ReconciliationError` re-opens only through an audited operator "check again", which still projects only through I-1…I-3. |
| I-10 | The Worker is the **only** writer of the team-membership projection (R-3). |

### 4.5 D8-15 rule (gate closed)

While the organization gate is not Ready, the reconciler may project only if:
- (a) the Effective evidence is the authenticated, validated AC-9 answer of the current attempt (I-1);
- (b) that answer's `integrity_quarantined` is false (I-7);
- (c) `committed_authorization_revision ≤ evaluated_at_revision`.

Authority unreachable ⇒ nothing is projected, whatever the local row says. **Interpretation to confirm:** Q-O1.

## 5. Database integrity requirements (D8-18)

### 5.1 `authority_command_operations` (from C-83 §4, with changes)

- **New columns:** `evaluated_projection_revision`, `projected_membership_revision`, `projected_membership_present`, `superseded_by_later_revision`.
- **Outcome names:** `MembershipPresentProjected`, `MembershipAbsentProjected`, `ProjectionUnchanged`, `DriftCorrected`.
- **Immutable after insert** (runtime role): `authority_command_operation_id`, `tenant_id`, `initiated_by_user_id`, `command_kind_name`, `team_id`, `member_user_id`, `authority_idempotency_key`, `request_correlation_identifier`, `operation_created_on`.
  * Enforced with **column-level UPDATE grants** (the `field_definitions` precedent in `BusinessRuntimeGrants`), plus a `BEFORE UPDATE` trigger owned by the schema owner that rejects changes to those columns.
  * `authority_command_id` and `committed_authorization_revision` may change only from NULL to a value.
- **No DELETE** for the runtime role. Retention purge is a privileged job.
- **Non-authoritative:** nothing in this table is evidence for a projection (I-1). It drives *when* to ask the Authority, never *what* to project.

### 5.2 New `team_membership_projection_states` (refinement R-2: the per-pair watermark)

| Column | Type | Notes |
|---|---|---|
| `team_membership_projection_state_id` | uuid PK | UUID v7 |
| `tenant_id` | uuid FK | |
| `team_id` | uuid FK | |
| `member_user_id` | uuid FK | |
| `projected_membership_present` | bool | |
| `projected_membership_revision` | bigint | ≥ 0 |
| `last_authority_command_operation_id` | uuid FK | |
| `projection_state_modified_on` | timestamptz | |

- Unique `(tenant_id, team_id, member_user_id)`.
- Runtime role: SELECT, INSERT, and UPDATE on the three state columns only. Identity columns are immutable. No DELETE.
- Updated only in the reconciler's apply transaction, together with `team_memberships` and the operation row (I-3).

### 5.3 Coverage inventory

Both tables join the §4L coverage inventory as **untrusted projection-class tables** (no `RowSignature`). The justification:
- they confer no access;
- every value used is re-obtained from the Authority;
- a raised watermark is detectable against Authority revisions (T-H).

`team_memberships` keeps its existing classification.

### 5.4 Evidence (D8-17)

**Success:** the terminal operation row is the durable diagnostic evidence. Its immutable completion fields are:
- command id;
- committed revision;
- evaluated revision P;
- projected membership revision and presence;
- outcome;
- superseded flag;
- completion time;
- correlation id.

They are retained for the D8-16 period. The Authority journal remains the canonical record of the command itself.

**Failures and operator actions:**
- `TEAM_MEMBERSHIP_RECONCILIATION_FAILED` for `ReconciliationError` (including `OPERATION_MISMATCH`, `INVALID_AUTHORITY_RESPONSE` and `LOCAL_WATERMARK_AHEAD`), `AuthorityFailed`, `NotCommitted` and `LATE_COMMIT_DETECTED`;
- `TEAM_MEMBERSHIP_PROJECTION_DRIFT` if Q-5 chooses correct-and-alert;
- `TEAM_MEMBERSHIP_OPERATION_RECHECKED` and `TEAM_MEMBERSHIP_OPERATION_REVIEWED` for operator actions.

All of these are business `audit_events`: a service actor, with the initiator in metadata only.

## 6. Failure and recovery scenarios

### 6.1 Refinements to the C-83 flow

- **R-3, single writer.** The API path still records the operation **before** submit (T1) and records the command id and revision afterwards (T2), but **no longer writes the roster**. It sets `next_attempt_on = now`, and the Worker projects through AC-9.
  * One projection path, one evidence source.
  * Latency: Worker poll interval (≤ 1 s idle) plus one lookup.
  * The existing `PendingAuthorityCommandTests` / `AdministrationTests` expectations of a synchronous roster write will change in implementation.
  * Decision: Q-4 / D8-20.
- **R-4.** The C-82 browser re-POST loop is replaced by polling the business read endpoint, so the browser never re-submits.
- **R-5, F-3.** RO-2 counts **12** handlers (`CreateTeam` plus 11 other handler files), not "11". C-83 listed 12 names under "11"; corrected here.

### 6.2 Scenario table

| Scenario | Behaviour |
|---|---|
| Browser closed while Pending | Worker completes; no browser involvement (RC-01/02) |
| API crash after T1, before submit | `NotCommitted` after two not-found lookups ≥ window apart; never re-sent (RC-07) |
| API crash after the Authority commit, before T2 | Worker finds the command by key (RC-06) |
| Worker crash mid-lease | Lease expires; the read-only lookup repeats (RC-05) |
| Worker absent for hours | Operations stay open, then flagged after the attention threshold; completed on return (RC-04/17) |
| Authority 503, network down, 429 | Backoff; no state change; nothing projected (I-1) |
| Gate closed by a later command | Project effective state at P if not quarantined (D8-15) |
| Integrity quarantine | `membership` null ⇒ wait and flag (RC-24) |
| `FailedReconciliation` | `AuthorityFailed`; nothing projected (RC-09) |
| Invalid or forged response | `ReconciliationError` `INVALID_AUTHORITY_RESPONSE`; alert (RC-22) |
| Watermark ahead of the Authority | `LOCAL_WATERMARK_AHEAD`; alert (RC-23) |
| Reconciler credential missing, invalid or revoked | Every lookup 401/403 ⇒ reconciler marks itself `Unavailable`, alerts, changes no operation state, projects nothing (RC-25) |
| BusinessDb restored to an earlier point | Lost operations and watermarks ⇒ the roster converges on the next operation per pair; full re-sync deferred (O7), F-5 |
| Authority restored (G14) | Revisions never roll back; if they did, `LOCAL_WATERMARK_AHEAD` detects it |

### 6.3 D8-16 parameters

Configuration section `TeamMembershipReconciliation`. Each value is validated at startup; out of bounds ⇒ the Worker refuses to start.

| Setting | Default | Allowed bounds |
|---|---|---|
| In-flight window | 5 min | ≥ 3 × Authority client timeout; ≤ 60 min |
| Backoff | 5 s → cap 5 min | cap 30 s…30 min |
| Needs-attention threshold | 30 min | 5 min…24 h |
| Consecutive-error budget | 20 | 5…100 |
| Late-commit sweep | daily for 7 days | 1…30 days |
| Retention | 400 days | ≥ 90 days |
| Lookup rate | 20/s, burst 40 | ≤ 50/s (PB-09 envelope) |

Testing may shorten the windows only in the Testing environment, refused elsewhere (the existing fault-injection pattern). Tests cover the bounds and the refusal.

## 7. Test coverage mapping

RC-01…RC-20 are from C-83 §11, adjusted for R-1…R-3. **RC-21…RC-28 are proposed additions** for the properties this review introduced. Whether they join the D8-19 closure set is Q-O3.

| Test | Covers |
|---|---|
| RC-01, RC-02 | Browser closed (add / remove) → Worker completes; I-10 |
| RC-03 | API restart while pending |
| RC-04 | Worker absent, then started |
| RC-05 | Worker crash mid-lease |
| RC-06 | Crash after the Authority commit, before T2 |
| RC-07 | Crash before submit → `NotCommitted`, Authority command count 0; I-8 |
| RC-08 | Rejected (G25 self-add, already member, unknown user); `membership` null; I-9 |
| RC-09 | `FailedReconciliation` → `AuthorityFailed` |
| RC-10 | **C8-M1-02**: add r1 and remove r2, both effective, confirmations processed remove-first and add-first; final state = Authority state; W = r2; I-2/I-3/I-4 |
| RC-11 | Concurrent Workers and API (3 Workers, 6 re-POSTs) → one roster row; watermark CAS; T-F |
| RC-12 | Same key, different member → 409, no second operation |
| RC-13 | Tampered row (member, team, other-organization team) → `OPERATION_MISMATCH`; T-G |
| RC-14 | Cross-organization key → `found:false`; T-I |
| RC-15 | Initiator's Security Administrator role removed while pending → mirrors the Authority; no new command; T-K |
| RC-16 | Second administrator sees Pending → Effective by polling; no re-POST (R-4) |
| RC-17 | Needs-attention flag; "check again" audited, never submits |
| RC-18 | AC-9 negative suite: business-API, verifier, operator, expired and wrong-audience tokens; unconfigured policy; assertion header present; disallowed kind; uniform `found:false`; no actor fields; lookup changes no Authority state (revision, journal and outbox counts identical) |
| RC-19 | Migrations up/down; naming; grants (no DELETE; immutable identity columns, trigger); one migration owner |
| RC-20 | (F only) — **retired**, since D8-11 chose R. Replaced by RC-26. |
| RC-21 | Add effective while a later remove is pending → present at r1 with `later_change_pending`, then absent at r2 |
| RC-22 | Invalid or forged AC-9 responses (each §1.3 rule violated through a test double at the HTTP boundary) → `INVALID_AUTHORITY_RESPONSE`, nothing projected |
| RC-23 | Watermark raised by direct SQL → `LOCAL_WATERMARK_AHEAD`, alert, no projection |
| RC-24 | Integrity quarantine → `membership` null; nothing projected; flagged; resumes after operator clearance |
| RC-25 | Reconciler credential missing, rotated or revoked → reconciler `Unavailable`; operations unchanged; alert |
| RC-26 | Architecture: the API image has no reconciler credential; the Worker reconciler has no submit dependency; only the Worker writes `team_memberships` (I-5, I-10) |
| RC-27 | Rate limit → 429 and `Retry-After` honoured; no error budget consumed |
| RC-28 | D8-16 bounds validation and Testing-only shortening refusal |

All run on the isolated security-test stack (real Authority, both PostgreSQL databases, SpiceDB and Vault). Hosted workflows then run on the final commit (D8-19).

## 8. Residual risks and security findings

| ID | Finding | Severity (proposed) | Status |
|---|---|---|---|
| F-1 | Authority responses are authenticated only by the channel. The local/Stage A transport is HTTP inside the Docker network with no server authentication. This applies to **every** business→Authority call, not only AC-9. | Medium (production: mitigated by the required mTLS) | Open; production mTLS is an existing requirement. Q-1 |
| F-2 | **C8-M1-02**: a stale Effective add can resurrect a removed member in the display roster at `02c3bb6` (reachable through a duplicated tab or a direct same-key retry; display-only). | Low (no access impact) | **Registered** (D8-14); fixed by R-1/R-2 in implementation |
| F-3 | **RO-2**: 12 handlers write projections only in the request that observes Effective (`CreateTeam`, `CreateFormDefinition`, `FormAccess`, `FormDesigner`, `CreateFormRecord`, `TransferOwnership`, `SecurityAdministrators`, `SecurityPolicy`, `ChildRecord`, `FieldSearchPolicy`, `CreateShare`, `RevokeShare`). | Low–Medium (display drift; per-handler assessment needed) | **Registered** (D8-13); out of scope |
| F-4 | A hostile BusinessDb DBA can edit `team_memberships` directly or stall reconciliation. Display only; detection is partial (T-H). | Low (accepted §4M boundary) | Residual |
| F-5 | A BusinessDb restore loses operations and watermarks, and the roster converges only per pair on the next operation. Full roster re-sync (O7) is deferred. | Low | Residual |
| F-6 | Timing differences between `found:true` and `found:false` could leak key existence to a credential holder. | Low (the credential holder already needs the key) | Residual; RC-18 measures |
| F-7 | The rate limiter is per Authority instance. | Low | Residual |
| F-8 | AC-9 is a new Authority read surface and credential (operational burden; joins the AC-5…AC-8 review package). | — | Under review |

## 9. Questions requiring independent reviewer approval

| ID | Question | Proposed answer |
|---|---|---|
| Q-1 | Is channel authentication (mTLS plus workload identity in production) sufficient for AC-9 response authenticity, as for all other Authority responses? Or is a signed outcome required? A signature would be a new cryptographic mechanism, which needs owner approval. | Channel authentication, plus §1.3 validation |
| Q-2 | Is returning the **effective membership of the command's own (team, member) pair** acceptable disclosure under the reconciler credential? It is needed for authoritative ordering (§4). | Yes, scoped to the command's own pair |
| Q-3 | Must successful lookups be written to the hash-chained Authority audit journal, or are structured logs, metrics and journaled refusals sufficient? | Logs and metrics; refusals alerted |
| Q-4 | Approve **single-writer** projection (only the Worker writes the roster, R-3) over the C-83 dual-path design? | Approve |
| Q-5 | Drift at an equal revision (the roster disagrees with W's recorded state): correct to the Authority state and alert, or park for an operator? | Correct and alert (display data) |
| Q-6 | Rate-limit defaults (20/s, burst 40) and per-instance limiting acceptable? | Yes for the first release |
| Q-7 | Is a uniform `found:false` (no distinction for an unregistered organization) acceptable, given that the reconciler then cannot tell a mis-configured organization from a missing key? | Yes; the `NotCommitted` rule plus alerts cover it |
| Q-8 | Should AC-9 answer during integrity quarantine (status only, `membership` null) or refuse with 503? | Answer, with `membership` null |
| Q-9 | Is a separate production managed identity per Worker deployment required (no sharing with the verifier)? | Yes |

**Owner decisions still open (not for the reviewer):**

| ID | Decision |
|---|---|
| Q-O1 | Confirm the D8-15 interpretation (§4.5): "previously authenticated Effective evidence" means the validated AC-9 answer of the current attempt, never a stored local state. |
| Q-O2 | Approve refinements R-1…R-3: Authority pair state as the projected value, the new `team_membership_projection_states` watermark table, and single-writer projection. These change C-83's "no `team_memberships` change, operations ledger as guard" and "API also projects" statements. |
| Q-O3 | Add RC-21…RC-28 to the D8-19 closure set, and retire RC-20 (Design F only). |
| Q-O4 | Accept the RO-2 count correction (12 handlers). |
