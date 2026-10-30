# Phase C8 — AC-9 independent security review submission (C8-M1-01, Design R)

- **Status:** **Final submission for independent security review (C-85, 2026-10-30). NOT REVIEWED.**
  * **Update C-87 (2026-10-30):** on the owner's instruction, Design R is **implemented** on branch `phase-c8/ac9-durable-reconciliation` (not merged, not deployed) — see `PHASE_C8_AC9_IMPLEMENTATION_PLAN.md` for how each question Q-1…Q-11 is handled in code. The reviewer may now review the implementation at the commit recorded in C-87 instead of only this design. The independent review below remains a **closure and merge gate**; the implementation is not evidence of review.
  * Nothing is deployed or merged, M2 has not started, and C8-M1-01 is **open** until §11/§12 and D8-19 are satisfied.
- **Independence notice:** this package, including every "proposed answer", was written by the implementation agent (Claude) on the owner's instruction, and the owner made the decisions in §0. **Neither the implementation agent nor the owner is an independent reviewer.** Nothing in this document is an independent review, approval or finding. §11 states who qualifies and what the reviewer must record.
- **History:**
  * C-83: original proposal;
  * C-84: Design R approved subject to this review; D8-11…D8-19;
  * C-85: owner answers to Q-O1…Q-O4 (D8-20…D8-23); one self-found correction (R-6); final reviewer questions Q-1…Q-11; per-environment guarantees.
- **Base documents:**
  * `docs/architecture/PHASE_C8_M1_01_DURABLE_COMMAND_RECONCILIATION.md` (C-83; R-1…R-6 below supersede it where they differ);
  * `docs/architecture/PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §5.5a, §10;
  * `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`;
  * `trust_authority/TRUST_AUTHORITY_ARCHITECTURE.md` §5.
- **Code baseline:** `phase-c8/administration-governance` at `83f062b` (no code differences from `02c3bb6`). Every "existing behaviour" statement cites the code it was verified against.

## 0. Owner decisions

### 0.1 C-84 (recorded 2026-10-30)

| ID | Decision | Where |
|---|---|---|
| D8-11 | Design R approved, subject to this review | whole package |
| D8-12 | Dedicated Worker-only credential | §2 |
| D8-13 | Scope limited to team add/remove; RO-2 registered for the other handlers | §8 F-3 |
| D8-14 | C8-M1-02 registered; stale ordering resolved in this implementation | §4 |
| D8-15 | Conditional: projection while the Authority gate is closed only with previously authenticated Effective evidence and no integrity quarantine | §4.5 |
| D8-16 | Timing defaults approved, configurable and tested | §6.3 |
| D8-17 | Auditable security-relevant failures and operator actions, plus durable diagnostic evidence of successful reconciliation | §5.4 |
| D8-18 | Operations table non-authoritative; security-relevant fields protected; table in the integrity coverage inventory | §5 |
| D8-19 | C8-M1-01 stays open until implementation, the required RC tests and required hosted verification pass (closure set amended by D8-22) | §7 |

### 0.2 C-85 (recorded 2026-10-30): answers to Q-O1…Q-O4, approved with conditions

| ID | Answers | Decision and condition | Where |
|---|---|---|---|
| D8-20 | Q-O1 | **Only a validated AC-9 response from the current reconciliation attempt qualifies as Effective evidence.** A stored local state, an earlier attempt's answer, or a browser report never does. | I-1, §4.5 |
| D8-21 | Q-O2 | **Approved:** projection of the Authority's pair state (R-1), the `team_membership_projection_states` table (R-2), and a **single logical Worker writer** (R-3). **Condition: the roster projection and the high-water mark are updated atomically** (one BusinessDb transaction, together with the operation's completion). | §4.2, §5.2, I-3, I-10 |
| D8-22 | Q-O3 | **Adopted:** RC-01…RC-19 and RC-21…RC-28 form the D8-19 closure set. **RC-20 retired** (justification in §7). | §7 |
| D8-23 | Q-O4 | **RO-2 corrected to 12 affected handlers.** | §8 F-3 |

"Single logical writer" means: one component, the Worker's reconciler, is the only code that writes the team-membership projection. It may run as several Worker instances; those are serialized per pair by the watermark row lock (§4.2). It does not mean one process.

## 1. AC-9 interface contract

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

### 1.2 Semantics (verified against existing Authority behaviour)

1. **Lookup key.** The lookup uses the unique `(organization_id, command_idempotency_key)` index (`uq_security_command_idempotency`, `AuthorityEntityConfigurations.cs:88`). Rejected commands are persisted with their key (`SecurityCommandService.SubmitAsync`, catch block), so a lookup also finds rejections.
2. **Revision.** `committed_authorization_revision` is assigned at commit as `state.AuthorizationRevision + 1`. This happens under the organization row lock, in the same transaction as the desired-state edges, journal and outbox (`SecurityCommandService.ApplyPlan`). It is **unique and strictly increasing per organization**.
3. **Effective.** `command_state = Effective` is set by `ProjectionProcessor` only when it applies exactly `ProjectionAppliedRevision + 1`, after an archive receipt. Commands therefore become Effective **strictly in revision order**: a command at revision r is Effective exactly when `r ≤ projection_applied_revision`. A divergence marks the command `FailedReconciliation` and quarantines the organization (`ProjectionProcessor.cs:74`, `:94–103`).
4. **Pair membership** is computed from the desired-state edges `(team, "member", user)`. Each edge records `CreatedBySecurityCommandId` and `RemovedBySecurityCommandId` (`AuthorityRelationshipEdge`), and each of those commands has its revision.
   * At P = `projection_applied_revision`, the pair is an **effective member** iff some edge was created at a revision ≤ P and is not removed at a revision ≤ P.
   * `effective_membership_revision` is the largest create or remove revision ≤ P among the pair's edges.
   * `later_change_pending` is true iff any of the pair's edges has a create or remove revision > P.
   * All of this is read in **one read-only transaction**, together with the organization state (snapshot isolation; no lock held).
5. **No side effects.** The lookup takes no lock, consumes no revision, writes no journal or outbox row, and never touches SpiceDB. It works while the organization gate is closed, because it reports the state SpiceDB already enforces (revision P).
6. **Monotonic answers.** `authorization_revision` and `projection_applied_revision` never decrease; G14 forbids Authority rollback. A lookup that starts after another lookup has completed therefore never reports a smaller P. §4.2 relies on this.

### 1.3 Reconciler-side validation (fail closed)

**Before sending:** the reconciler sends a lookup only when the row's key prefix matches its kind (`team-add:` ⇔ `AddTeamMember`, `team-remove:` ⇔ `RemoveTeamMember`). Otherwise the row goes to `ReconciliationError` `OPERATION_MISMATCH`.

**On a 200 response,** all of the following must hold. Otherwise the operation goes to `ReconciliationError` with `INVALID_AUTHORITY_RESPONSE`, an alert is raised, and **nothing is projected**:
- `correlation_id` matches the request, and `organization_id` equals both the request and the operation row;
- `found` is true;
- `command_kind` is the expected kind;
- `team_id` and `member_user_id` equal the row (a mismatch is `OPERATION_MISMATCH` instead);
- `projection_applied_revision ≤ authorization_revision`;
- for a command that is not Rejected: `1 ≤ committed_authorization_revision ≤ authorization_revision`;
- `Effective` ⇒ `committed ≤ projection_applied_revision`, and `CommittedAwaitingProjection` ⇒ `committed > projection_applied_revision`;
- `membership` is present ⇔ the command is not Rejected and the organization is not quarantined;
- `membership.evaluated_at_revision == projection_applied_revision`;
- `effective_membership_revision ≤ evaluated_at_revision`;
- `Effective` ⇒ `effective_membership_revision ≥ committed`.

### 1.4 Additional contract requirements added in C-85

| ID | Requirement | Reason |
|---|---|---|
| C-1 | Outside Development and Testing, the reconciler refuses to start unless the Authority base address is `https` and a client certificate is configured. The Authority's reconciler policy is registered outside Development and Testing only if the server requires client certificates. | Turns production mTLS (F-1) into a fail-closed start condition for this path. It is a configuration guard, not a new cryptographic mechanism. |
| C-2 | The Authority startup guard extends the OD-1 rule to the reconciler credential: its own issuer and its own key, distinct from the workload, operator, user-assertion and verifier credentials. A partial configuration refuses to start; an absent one makes the policy deny everything. | With HS256 the scheme's key, not the `workload_role` claim, is what separates callers (§2.2). |
| C-3 | The lookup never returns, logs or echoes the idempotency key, and never logs `team_id` or `member_user_id`. A success is logged with `security_command_id`. | Keys are lookup capabilities (§2.4). |

## 2. Authentication, authorization, credential isolation and audit

### 2.1 Proposal

| Aspect | Proposal |
|---|---|
| Identity | A new workload identity, `business-projection-reconciler`, held **only by the Worker host**. Stage A / local: its own HS256 credential (issuer, audience, ≥ 32-byte key) from deployment secrets, as with the verifier (OD-1). Production: its own Entra managed identity and app role, over mTLS (TRUST_AUTHORITY_ARCHITECTURE §5; C-1). |
| Separation | A new scheme and policy, `ReconcilerScheme` / `ReconcilerPolicy`, requiring `workload_role = business-projection-reconciler`. The existing `WorkloadPolicy` requires `business-api` (`InternalAuthentication.cs:53`). The verifier and operator use their own schemes and keys. So this token satisfies **no other endpoint**, and no other token satisfies this one. Unconfigured ⇒ the policy denies everything (the verifier pattern, `InternalAuthentication.cs:62–65`). |
| Deployment | The credential is present in the Worker's configuration only. A compose/Bicep check and an architecture test assert that the **API** container has no reconciler credential, and that the reconciler component has no submit dependency (RC-26). |
| Authorization inside the endpoint | No user, no actor binding and no permission graph. The only scope is (organization, key, allowlisted kind). |
| No impersonation | The endpoint refuses delegated assertions and returns no actor identity, reason text or payload beyond team and member. The reconciler never holds a user assertion. |
| No mutation | Read-only by construction (§1.2.5). No command, replay, re-signing, journal write or SpiceDB access. |
| Rate limiting | ASP.NET Core's built-in rate limiter (part of the framework, no new package), partitioned by workload identity: a token bucket of 20 requests per second, burst 40, configurable within bounds. A 429 carries `Retry-After`. The Authority has no rate limiter today; this is the first. It is per instance (F-7). |
| Audit and telemetry | Every lookup emits a structured Authority log and metric: organization, kind, found, state, outcome code, correlation and latency; **never** the key (C-3). Refusals (401, 403, 400 assertion, 422, 429) are counted and alerted. Today `GetSecurityCommandStatus` is not journaled. Whether successful lookups must also go to the hash-chained journal is **Q-3**. |

### 2.2 How the credentials actually work today (verified)

- **Stage A workload credentials are HS256 shared secrets** (`CredentialOptions.SymmetricKey`, `InternalAuthentication.cs:120`). The Authority and the caller hold the same key, so whoever holds it can mint a token with **any** claims for that scheme. Separation between callers therefore comes from **each scheme having its own key and issuer**, enforced by `IdentityPolicyStartupGuard` (distinct keys; OD-1 distinct issuer). It does not come from the `workload_role` claim. C-2 extends that guard to the reconciler.
- **Production identity is not implemented.** The startup guard refuses any user-assertion provider other than `TestIdentityProvider`, and permits that provider only in Development and Testing (`InternalAuthentication.cs`, `IdentityPolicyStartupGuard`). As built, the Authority **cannot start in Staging or Production**. Entra managed identities for workloads are a PB-08 / PB-11 deliverable that does not exist yet.
- **mTLS is not implemented anywhere.** No source file configures client certificates or HTTPS on Kestrel. Every Authority call in every runnable environment is plain HTTP.

### 2.3 Credential isolation: the honest boundary (new finding F-9)

The Worker host **already holds two Authority credentials**:
- the `business-api` workload credential (`TrustAuthority__WorkloadSharedKeyBase64`, `infrastructure/docker/compose.yaml`, worker service). It registers the full `AuthorityHttpClient`, including submit (`BusinessInfrastructureRegistration.cs:48–54`), used today by field-protection migration;
- the field-protection verifier credential (OD-1, `Program.cs` `AddProtectionVerifier`).

Adding the reconciler credential to the same process means **a compromise of the Worker process yields all three**. The isolation that AC-9 adds is therefore at the **Authority policy** level: each token is accepted by its own endpoints only. It is not at the process level.

What a Worker compromise already allowed before AC-9:
- workload-only content operations under the business-api credential (`ContentEndpoints.cs`, `TryActor`, bounded by the reprotection rules of an effective command);
- verifier lookups.

It cannot submit user commands, because those require a delegated user assertion signed with the user-assertion key. The Worker does not hold that key: compose gives `TEST_IDP_SHARED_KEY_BASE64` to the API and the Authority, not the Worker.

AC-9 adds read-only outcome and pair-membership lookups to that set. Whether the reconciler must run in its **own container with its own identity** is **Q-10**.

### 2.4 Lookup binding and enumeration

- **Binding.** A lookup is bound to exactly one command: the unique (organization, key) index, the allowlisted kind check, and the pair taken from the Authority's own command payload. The reconciler then cross-checks organization, kind, prefix, team and member against its row (§1.3). There is no parameter for a team, a user or a revision range.
- **No arbitrary enumeration.** The caller cannot name a pair; it gets the pair of a command it already identified by key.
  * Keys are `team-add:` or `team-remove:` followed by a browser-generated `crypto.randomUUID()` (`TeamDetailPage.tsx:90`; 122 random bits). Guessing is infeasible at 20 lookups per second.
  * **Bound on disclosure:** a holder of the reconciler credential *and* read access to BusinessDb can read every stored key, and so learn the Authority's current state of every pair that ever had a team-membership command. That is close to what the BusinessDb roster already shows, which the same attacker can read directly.
  * Residual: the API accepts any 8–128 character client key, so a malicious administrator could choose guessable keys for **their own** commands. That reveals only their own commands' pairs.
- **Cross-tenant.** Every unknown answer is `found:false`. The organization is part of the unique key, the response organization must equal the row's, and every write goes to that tenant (I-6). Timing differences: F-6.

## 3. Trust boundaries and threat model

**Trust boundaries:**
1. Worker ↔ Authority: authenticated workload channel; mTLS in production (not implemented, §2.2).
2. Worker ↔ BusinessDb: runtime role. BusinessDb is **untrusted for security decisions** (§4M).
3. Authority internal state: trusted (an Authority compromise is out of scope, per §4M).
4. Browser ↔ business API: the business API never forwards AC-9 to browsers.

| # | Threat | Mitigation | Residual |
|---|---|---|---|
| T-A | **Reconciler credential stolen** | Read-only. It reveals outcome and pair state only for keys the attacker already holds (§2.4). No write, sign or content access. Rate-limited and alerted. Rotation as for OD-1. | Pair-state disclosure for known keys; F-9 co-location |
| T-B | **Request replay** | Idempotent read; no state change. | none |
| T-C | **Replay of an old genuine response** (network position) | Bound to the request's correlation id. Projection is monotonic in `effective_membership_revision` (§4), so an older answer can never regress the projection. | — |
| T-D | **Forged Effective response** | Production: mTLS server authentication (C-1). Every response is schema- and invariant-validated (§1.3). A forged answer can still only change the *display* roster, which confers nothing. | **Local and Stage A: plain HTTP, no server authentication** (F-1) |
| T-E | **Stale operation ordering** (C8-M1-02) | Projection mirrors the Authority's pair state at revision P, under a monotonic per-pair watermark (§4). | — |
| T-F | **Concurrent Workers** | Single logical writer (D8-21); `SKIP LOCKED` leases; per-pair watermark row lock with compare-and-set; benign stale answers re-looked-up, never alerted (R-6). | — |
| T-G | **Tampered local operation row** | The projection uses only Authority-returned values for the pair of the Authority's own command. Identity columns are immutable to the runtime role (§5). A fully forged but consistent row can at most cause the **true** Authority state of some same-organization pair to be projected again. | A DBA can stall reconciliation (liveness), F-4 |
| T-H | **Tampered watermark** | Raised above any real pair change ⇒ detected by a fresh lookup (`LOCAL_WATERMARK_AHEAD`, §4.2), alert, nothing projected. Lowered ⇒ refused by the monotonic trigger for the runtime role (§5.2); a DBA can bypass the trigger, which allows at most a racing stale write, corrected by the next reconciliation. | F-4 |
| T-I | **Cross-tenant disclosure** | §2.4. | Timing, F-6 |
| T-J | **Lookup used to act for a user** | Assertion header refused; no submit dependency in the reconciler component (RC-26). | Process-level co-location, F-9 |
| T-K | **Administrator disabled or revoked during a pending operation** | The Authority decided the command at commit. The reconciler mirrors what became Effective and never re-submits. `NotCommitted` is terminal; restarting is a new, currently authorized user action. | — |
| T-L | **Integrity quarantine** | §4.6. | — |
| T-M | **Denial of service on the Authority** | Rate limit, batch bound and backoff (§6.3). The lookup is a single indexed read plus the pair's edges. | Per-instance limiter, F-7 |
| T-N | **API writes the roster despite R-3** | Code path removed; architecture test (RC-26). | **Not database-enforced while the API and the Worker share one runtime role** (F-10, Q-11) |

## 4. Operation ordering and state-machine invariants (C8-M1-02)

### 4.1 The ordering source

The only ordering key is the **Authority's committed authorization revision**. It is unique and strictly increasing per organization, assigned at commit, and Effective strictly in that order (§1.2.2–3).

**Never used as order:**
- callback or response arrival time;
- Worker processing time;
- `operation_created_on` and other local timestamps;
- the local operation row's own stored state.

The existing Authority already records enough to derive per-pair order: edge → creating and removing command → revision. **No new ordering guarantee is invented.** AC-9 adds only the *read* of that order for one pair.

### 4.2 Projection rule (R-1, with the R-6 correction)

The reconciler projects the **Authority's effective membership of the operation's (team, member) pair at revision P** under a monotonic per-pair watermark W. Let E = the answer's `effective_membership_revision`.

1. Obtain a validated AC-9 answer in this attempt (I-1).
2. Begin one BusinessDb transaction and lock the pair's watermark row. If the row is absent, create it with W = 0 and present = false.
3. **E > W:** set the roster to `effective_member` and set W = E, then complete the operation, all in this transaction (D8-21).
4. **E = W:** the roster is not changed, unless it disagrees with the state recorded at W (drift, Q-5). Complete the operation.
5. **E < W:** the answer is older than a projection another writer already made. **Discard it and roll back.** Then perform **one fresh lookup**, which starts after W was committed, and re-enter at step 2 with it:
   * the fresh answer has E ≥ W, because W was written from a real pair change at or below an earlier P, and P never decreases (§1.2.6). The rule then proceeds normally;
   * if the fresh answer still has E < W, W names a pair change the Authority does not have. That is `LOCAL_WATERMARK_AHEAD`: an integrity anomaly. Nothing is projected and an alert is raised.
6. Record on the operation `superseded_by_later_revision = (E > committed_authorization_revision)`.

**R-6 (correction found while preparing C-85).** The C-84 text classified every E < W as `LOCAL_WATERMARK_AHEAD`. That contradicted its own concurrent-Worker scenario: a benign race in which the later answer is written first would have raised a false integrity alarm. A stale answer is now retried once. Only a **fresh** answer below W is an anomaly. RC-11 and RC-23 test both branches.

### 4.3 Earlier Add, later Remove, with confirmations in either order

Revisions: Add = r1, Remove = r2, with r1 < r2. Because the Authority makes them Effective in order, "out of order" can only mean that the **business side processes the confirmations** in either order.

| Processing order | Authority answer when processed | Result |
|---|---|---|
| Remove first (both Effective) | absent, E = r2 | roster absent, W = r2. Then Add: absent, E = r2 = W ⇒ unchanged; Add marked superseded. **Final: absent** ✔ |
| Add first, Remove still pending | present, E = r1, `later_change_pending` | roster present, W = r1 (the true effective state at that moment). Remove later: absent, E = r2 > r1. **Final: absent** ✔ |
| Add first, both already Effective | absent, E = r2 | roster absent, W = r2 (the add is superseded immediately). **Final: absent** ✔ |
| Two Workers race: A reads at r1, B reads at r2, B commits first | — | A locks, sees E = r1 < W = r2 ⇒ discards, fresh lookup ⇒ E = r2 = W ⇒ no change. No alert. **Final: absent** ✔ |

### 4.4 Invariants (each maps to tests, §7)

| ID | Invariant |
|---|---|
| I-1 | A roster change happens only from a validated AC-9 answer obtained **in the same attempt** (D8-20). A stored local "Effective" value is never evidence. |
| I-2 | The value projected is the Authority's effective pair membership at P, never the requested effect of the local operation. |
| I-3 | W per (tenant, team, member) never decreases. It is written only together with the roster projection and the operation completion, in one transaction (D8-21). |
| I-4 | Ordering uses Authority revisions only (§4.1). |
| I-5 | The reconciler cannot submit, replay or alter Authority state. |
| I-6 | The row's organization = the AC-9 request organization = the response organization = the tenant of every write. |
| I-7 | Quarantined ⇒ nothing projected. |
| I-8 | `NotCommitted` is never re-sent. |
| I-9 | Terminal states (`Projected`, `Rejected`, `AuthorityFailed`) never re-open. `ReconciliationError` re-opens only through an audited operator "check again", which still projects only through I-1…I-3. |
| I-10 | The reconciler is the **only** writer of the team-membership projection (D8-21). |
| I-11 | An answer older than W is never written and never alerted. Only a fresh answer older than W is an anomaly (R-6). |

### 4.5 D8-15 rule (gate closed), as confirmed by D8-20

While the organization gate is not Ready, the reconciler may project only if all three hold:
- (a) the Effective evidence is the authenticated, validated AC-9 answer of the **current attempt**;
- (b) that answer's `integrity_quarantined` is false;
- (c) `committed_authorization_revision ≤ evaluated_at_revision`.

If the Authority is unreachable, nothing is projected, whatever the local row says.

### 4.6 Integrity quarantine (fail closed)

- The Authority answers during quarantine, with **status only**: `membership` is null and `integrity_quarantined` is true (proposal; Q-8).
- The reconciler projects nothing for that organization, flags the operation `AwaitingQuarantineClearance`, and backs off to the needs-attention cadence.
- No operator action can project during quarantine. "Check again" performs a lookup, which again returns no membership.
- After the Authority's own audited clearance, the next lookup returns membership and normal projection resumes (RC-24).

## 5. Database integrity requirements (D8-18, D8-21)

### 5.1 `authority_command_operations` (C-83 §4, with changes)

- **New columns:** `evaluated_projection_revision`, `projected_membership_revision`, `projected_membership_present`, `superseded_by_later_revision`.
- **Outcome names:** `MembershipPresentProjected`, `MembershipAbsentProjected`, `ProjectionUnchanged`, `DriftCorrected`.
- **Immutable after insert** (runtime role): `authority_command_operation_id`, `tenant_id`, `initiated_by_user_id`, `command_kind_name`, `team_id`, `member_user_id`, `authority_idempotency_key`, `request_correlation_identifier`, `operation_created_on`.
  * Enforced with **column-level UPDATE grants** (the `field_definitions` precedent in `BusinessRuntimeGrants`), plus a `BEFORE UPDATE` trigger owned by the schema owner that rejects changes to those columns.
  * `authority_command_id` and `committed_authorization_revision` may change only from NULL to a value.
  * The completion fields (§5.4) may change only while the row is not terminal.
- **No DELETE** for the runtime role. Retention purge is a privileged job.
- **Non-authoritative:** nothing in this table is evidence for a projection (I-1). It drives *when* to ask the Authority, never *what* to project.

### 5.2 New `team_membership_projection_states` (R-2: the per-pair high-water mark)

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
- Runtime role: SELECT, INSERT, and UPDATE on the three state columns only. The identity columns are immutable. No DELETE.
- A schema-owned `BEFORE UPDATE` trigger refuses any update that **lowers** `projected_membership_revision`. This makes I-3 database-enforced against the runtime role. A DBA can still bypass it (F-4).
- **Atomicity (D8-21):** the watermark update, the `team_memberships` change and the operation completion are made in **one** BusinessDb transaction that holds the watermark row lock. A failure rolls back all three. RC-11 verifies this, including a fault injected between the statements.

### 5.3 Coverage inventory

Both tables join the §4L coverage inventory as **untrusted projection-class tables**, without `RowSignature`. Justification:
- they confer no access;
- every value used is re-obtained from the Authority;
- a raised watermark is detectable against the Authority (T-H).

`team_memberships` keeps its existing classification.

### 5.4 Evidence (D8-17)

**Success:** the terminal operation row is the durable diagnostic evidence. Its completion fields are frozen when the row becomes terminal:
- command id;
- committed revision;
- evaluated revision P;
- projected membership revision and presence;
- outcome;
- superseded flag;
- completion time;
- correlation id.

They are retained for the D8-16 period. The Authority journal remains the canonical record of the command itself.

**Failures and operator actions** are business `audit_events`, with a service actor and the initiator in metadata only:
- `TEAM_MEMBERSHIP_RECONCILIATION_FAILED` for `ReconciliationError` (including `OPERATION_MISMATCH`, `INVALID_AUTHORITY_RESPONSE` and `LOCAL_WATERMARK_AHEAD`), `AuthorityFailed`, `NotCommitted` and `LATE_COMMIT_DETECTED`;
- `TEAM_MEMBERSHIP_PROJECTION_DRIFT`, if Q-5 chooses correct-and-alert;
- `TEAM_MEMBERSHIP_OPERATION_RECHECKED` and `TEAM_MEMBERSHIP_OPERATION_REVIEWED` for operator actions.

## 6. Failure and recovery scenarios

### 6.1 Refinements to the C-83 flow

- **R-3, single logical writer (D8-21).** The API path still records the operation **before** submit (T1) and records the command id and revision afterwards (T2), but **no longer writes the roster**. It sets `next_attempt_on = now`, and the reconciler projects through AC-9.
  * One projection path, one evidence source.
  * Latency: the Worker poll interval (≤ 1 s idle) plus one lookup.
  * The existing `PendingAuthorityCommandTests` / `AdministrationTests` expectations of a synchronous roster write will change in implementation.
  * The owner approved this. The reviewer is asked to confirm it in Q-4.
- **R-4.** The C-82 browser re-POST loop is replaced by polling the business read endpoint; the browser never re-submits.
- **R-5 (D8-23).** RO-2 counts **12** handlers.
- **R-6.** Stale-answer retry (§4.2).

### 6.2 Scenario table

| Scenario | Behaviour |
|---|---|
| Browser closed while Pending | The Worker completes; no browser involvement (RC-01/02) |
| API crash after T1, before submit | `NotCommitted` after two not-found lookups at least a window apart; never re-sent (RC-07) |
| API crash after the Authority commit, before T2 | The Worker finds the command by key (RC-06) |
| Worker crash mid-lease | The lease expires; the read-only lookup repeats (RC-05) |
| Worker crash inside the apply transaction | The transaction rolls back roster, watermark and completion together (RC-11) |
| Worker absent for hours | Operations stay open, are flagged after the attention threshold, and complete on return (RC-04/17) |
| Authority 503, network down, 429 | Backoff; no state change; nothing projected (I-1) |
| Gate closed by a later command | Project the effective state at P, if not quarantined (D8-15) |
| Integrity quarantine | Nothing projected; wait and flag (§4.6, RC-24) |
| `FailedReconciliation` | `AuthorityFailed`; nothing projected (RC-09) |
| Invalid or forged response | `ReconciliationError` `INVALID_AUTHORITY_RESPONSE`; alert (RC-22) |
| Stale answer behind W (race) | Discard, fresh lookup; no alert (RC-11) |
| Watermark ahead of the Authority | `LOCAL_WATERMARK_AHEAD` after a fresh lookup; alert (RC-23) |
| Reconciler credential missing, invalid or revoked | Every lookup 401/403 ⇒ the reconciler marks itself `Unavailable`, alerts, changes no operation state and projects nothing (RC-25) |
| BusinessDb restored to an earlier point | Lost operations and watermarks; the roster converges per pair on the next operation; full re-sync deferred (O7), F-5 |
| Authority restored (G14) | Revisions never roll back; if they did, `LOCAL_WATERMARK_AHEAD` would detect it |

### 6.3 D8-16 parameters

Configuration section `TeamMembershipReconciliation`. Every value is validated at startup; a value out of bounds ⇒ the Worker refuses to start.

| Setting | Default | Allowed bounds |
|---|---|---|
| In-flight window | 5 min | ≥ 3 × the Authority client timeout; ≤ 60 min |
| Backoff | 5 s, capped at 5 min | cap 30 s…30 min |
| Needs-attention threshold | 30 min | 5 min…24 h |
| Consecutive-error budget | 20 | 5…100 |
| Late-commit sweep | daily for 7 days | 1…30 days |
| Retention | 400 days | ≥ 90 days |
| Lookup rate | 20/s, burst 40 | ≤ 50/s (PB-09 envelope) |

Testing may shorten the windows only in the Testing environment; any other environment refuses (the existing fault-injection pattern). Tests cover the bounds and the refusal.

## 7. Test coverage (D8-22 closure set)

**Closure set:** RC-01…RC-19 and RC-21…RC-28 (D8-22), plus the hosted verification required by D8-19.

**RC-20 retirement justification.** RC-20 tested Design F (projection from the existing status read under a user assertion, with a no-assertion fallback). D8-11 rejected Design F. No code path it would exercise will exist. Its one transferable property, that the reconciler can never act under a user identity, is covered by RC-18 (assertion refused) and RC-26 (no submit dependency).

| Test | Covers |
|---|---|
| RC-01, RC-02 | Browser closed (add / remove) → the Worker completes; I-10 |
| RC-03 | API restart while pending |
| RC-04 | Worker absent, then started |
| RC-05 | Worker crash mid-lease |
| RC-06 | Crash after the Authority commit, before T2 |
| RC-07 | Crash before submit → `NotCommitted`, Authority command count 0; I-8 |
| RC-08 | Rejected (G25 self-add, already a member, unknown user); `membership` null; I-9 |
| RC-09 | `FailedReconciliation` → `AuthorityFailed` |
| RC-10 | **C8-M1-02**: add r1 and remove r2, both effective, confirmations processed remove-first and add-first; final state = Authority state; W = r2; I-2/I-3/I-4 |
| RC-11 | Concurrent Workers (3 Worker instances, 6 browser polls) → one roster row; watermark compare-and-set; **benign stale answer re-looked-up and not alerted (R-6, I-11)**; fault injected inside the apply transaction rolls back roster, watermark and completion together (D8-21) |
| RC-12 | Same key, different member → 409, no second operation |
| RC-13 | Tampered row (member, team, other-organization team, kind/prefix mismatch) → `OPERATION_MISMATCH`; T-G |
| RC-14 | Cross-organization key → `found:false`; T-I |
| RC-15 | Initiator's Security Administrator role removed while pending → mirrors the Authority; no new command; T-K |
| RC-16 | A second administrator sees Pending → Effective by polling; no re-POST (R-4) |
| RC-17 | Needs-attention flag; "check again" audited, never submits |
| RC-18 | AC-9 negative suite: business-API, verifier, operator, expired and wrong-audience tokens; unconfigured policy; **reconciler key or issuer equal to another credential refused at startup (C-2)**; assertion header present; disallowed kind; uniform `found:false`; no actor fields; key never logged (C-3); a lookup changes no Authority state (revision, journal and outbox counts identical) |
| RC-19 | Migrations up and down; naming; grants (no DELETE; immutable identity columns; triggers, **including the watermark monotonic trigger**); one migration owner |
| RC-20 | **Retired** (above) |
| RC-21 | Add effective while a later remove is pending → present at r1 with `later_change_pending`, then absent at r2 |
| RC-22 | Invalid or forged AC-9 responses (each §1.3 rule violated through a test double at the HTTP boundary) → `INVALID_AUTHORITY_RESPONSE`; nothing projected |
| RC-23 | Watermark raised by direct SQL → `LOCAL_WATERMARK_AHEAD` **after the fresh lookup**; alert; no projection. Watermark lowered by the runtime role → refused by the trigger |
| RC-24 | Integrity quarantine → `membership` null; nothing projected; flagged; resumes after the Authority's clearance |
| RC-25 | Reconciler credential missing, rotated or revoked → reconciler `Unavailable`; operations unchanged; alert |
| RC-26 | Architecture: the API image has no reconciler credential; the reconciler component has no submit dependency; only the reconciler writes `team_memberships` (I-5, I-10) |
| RC-27 | Rate limit → 429, and `Retry-After` honoured; no error budget consumed |
| RC-28 | D8-16 bounds validation; Testing-only shortening refused elsewhere; **C-1 https/client-certificate start guard outside Development/Testing** |

The extensions in bold (RC-11, RC-18, RC-19, RC-23, RC-28) sharpen existing tests to cover R-6, D8-21, C-1 and C-2. No new test identifiers were added.

All tests run on the isolated security-test stack (real Authority, both PostgreSQL databases, SpiceDB and Vault). Hosted workflows then run on the final commit (D8-19). What that evidence does and does not prove per environment is in §10.

## 8. Residual risks and findings

All severities below are **proposed by the implementation agent**; the reviewer assigns the final ones.

| ID | Finding | Proposed severity | Status |
|---|---|---|---|
| F-1 | Authority responses and requests are protected only by the channel, and **every runnable environment uses plain HTTP**, with no mTLS implemented (§2.2, §10). This applies to every business→Authority call, including bearer workload tokens and delegated user assertions, not only AC-9. `trust-edge` is an ordinary bridge network; the compose file records that Docker Desktop does not isolate bridge networks by IP. | Medium for Stage A evidence; would be High if any environment carried real data, which none may today | Open. Production mTLS is an existing requirement; C-1 makes AC-9 fail closed without it. Q-1 |
| F-2 | **C8-M1-02**: a stale Effective add can resurrect a removed member in the display roster at `02c3bb6` (display-only). | Low (no access impact) | Registered (D8-14); fixed by R-1, R-2 and R-6 in implementation |
| F-3 | **RO-2**: 12 handlers write projections only in the request that observes Effective (`CreateTeam`, `CreateFormDefinition`, `FormAccess`, `FormDesigner`, `CreateFormRecord`, `TransferOwnership`, `SecurityAdministrators`, `SecurityPolicy`, `ChildRecord`, `FieldSearchPolicy`, `CreateShare`, `RevokeShare`). | Low–Medium (display drift; per-handler assessment needed) | Registered (D8-13, D8-23); out of scope |
| F-4 | A hostile BusinessDb DBA can edit `team_memberships` directly, bypass the triggers, or stall reconciliation. Display only; detection is partial (T-H). | Low (accepted §4M boundary) | Residual |
| F-5 | A BusinessDb restore loses operations and watermarks. The roster converges only per pair, on the next operation. Full roster re-sync (O7) is deferred. | Low | Residual |
| F-6 | Timing differences between `found:true` and `found:false` could leak key existence to a credential holder. | Low (the holder already needs the key) | Residual. **Not measured by any test** (see §13.4); reviewer assessment required |
| F-7 | The rate limiter is per Authority instance. | Low | Residual |
| F-8 | AC-9 is a new Authority read surface and credential (operational burden; joins the AC-5…AC-8 review package). | — | Under review |
| F-9 | **Credential co-location.** The Worker process already holds the `business-api` workload credential, with the full Authority client registered, and the verifier credential. Adding the reconciler credential gives a Worker compromise all three. AC-9's isolation is at the Authority policy level, not the process level (§2.3). | Low–Medium | **New.** Q-10 |
| F-10 | **Single writer is not database-enforced.** The API and the Worker connect as the same BusinessDb runtime role (`BUSINESS_RUNTIME_USER`, compose api and worker services). I-10 is enforced by code and an architecture test only. A compromised API runtime could still write `team_memberships` (display only). | Low | **New.** Q-11 |
| F-11 | **Self-found design defect (R-6):** C-84 §4.2 rule 2 would have raised false integrity alarms in a benign Worker race. | — | Corrected in this package; RC-11 and RC-23 |
| F-12 | **Security-test stack exposure:** the Authority's internal contract is published on host loopback `127.0.0.1:5190` for the test harness, and the security-test overlay commits test-only credentials (for example the verifier key). Acceptable for synthetic data only. The reconciler test credential would follow the same pattern. | Low (synthetic data only) | Residual; must never be reused outside Testing |

## 9. Final questions for the independent reviewer

Each question lists the evidence to examine and the implementation agent's proposed answer. **The proposed answers are not review conclusions.**

| ID | Question | Evidence to examine | Proposed answer (not independent) |
|---|---|---|---|
| Q-1 | Is channel authentication (production mTLS plus workload identity) sufficient for AC-9 response authenticity, or is a signed outcome required? A signature would be a new cryptographic mechanism and needs owner approval. Is plain HTTP acceptable for **local and Stage A evidence**, given F-1? | §2.2, §10, F-1, C-1; `infrastructure/docker/compose.yaml` networks; TRUST_AUTHORITY_ARCHITECTURE §5 | Channel authentication plus §1.3 validation. Plain HTTP accepted for synthetic-data environments only. C-1 makes production start conditional on HTTPS with client certificates. |
| Q-2 | Is returning the effective membership of the **command's own** (team, member) pair acceptable disclosure under the reconciler credential? | §1.1, §2.4 | Yes, limited to the command's own pair |
| Q-3 | Must successful lookups go to the hash-chained Authority audit journal, or are structured logs, metrics and alerted refusals sufficient? | §2.1; the Authority journal design; existing unjournaled `GetSecurityCommandStatus` | Logs and metrics; refusals alerted |
| Q-4 | Confirm the owner-approved single logical writer (D8-21) from a security standpoint. | §4.2, §6.1, I-10, F-10 | Confirm |
| Q-5 | Drift at an equal revision (the roster disagrees with W's recorded state): correct to the Authority state and alert, or park for an operator? | §4.2 step 4 | Correct and alert (display data) |
| Q-6 | Are the rate-limit defaults (20/s, burst 40) and per-instance limiting acceptable? | §2.1, §6.3, F-7 | Yes, for the first release |
| Q-7 | Is a uniform `found:false` acceptable, given that the reconciler then cannot tell a misconfigured organization from a missing key? | §1.1, §2.4, F-6 | Yes; the `NotCommitted` rule plus alerts cover it |
| Q-8 | During integrity quarantine, should AC-9 answer with status only (`membership` null) or refuse with 503? | §4.6 | Answer with status only |
| Q-9 | Is a separate production managed identity for the reconciler required (not shared with the verifier or the business API)? | §2.1, §2.3 | Yes |
| Q-10 | **New.** Is co-locating the reconciler credential with the business-API and verifier credentials in one Worker process acceptable? Or must the reconciler run as its own container with its own identity? | §2.3, F-9; `src/backend/EnterpriseForms.Worker/Program.cs`; compose worker service | Acceptable for Stage A. For production, run the reconciler as its own container with its own managed identity (no extra Authority surface). Decision needed before production design. |
| Q-11 | **New.** Must I-10 be database-enforced? That would mean a separate BusinessDb login for the reconciler with exclusive INSERT/UPDATE on `team_memberships` and `team_membership_projection_states`, and the API role reduced to SELECT on both. | §5.2, F-10; `BusinessRuntimeGrants.cs`; compose api and worker connection strings | Recommended; the only API writer of `team_memberships` today is `ChangeTeamMembershipHandler` (`TeamHandlers.cs:64`), which R-3 removes. It adds a database role and a second Worker connection string; owner approval needed. |

### 9.1 How the nine points of attention are addressed

| # | Point of attention | Sections | Remaining decision |
|---|---|---|---|
| 1 | Plain HTTP locally and in Stage A vs production mTLS | §2.2, §10, F-1, C-1 | Q-1 |
| 2 | Strict binding of each lookup to the original organization and command | §1.1, §1.3, §2.4, I-6 | — |
| 3 | No arbitrary membership enumeration | §2.4, §1.1 (pair fixed by the command) | Q-2, Q-7 |
| 4 | Authentication, auditing and credential isolation | §2, C-2, C-3, F-9 | Q-3, Q-9, Q-10 |
| 5 | Stale revisions and concurrent Workers | §4.2 (R-6), §4.3, I-11 | Q-4 |
| 6 | Protection of the high-water mark and operation records | §5.1, §5.2, T-G, T-H, F-10 | Q-11 |
| 7 | Fail closed during integrity quarantine | §4.6, I-7 | Q-8 |
| 8 | Roster divergence at the same revision | §4.2 step 4 | Q-5 |
| 9 | Development, staging and production differences | §10 | Q-1 |

## 10. Security guarantees by environment

| Environment | Exists today | Authority transport | Workload credentials | User identity | Network | What its evidence can prove | What it cannot prove |
|---|---|---|---|---|---|---|---|
| **Development** (persistent stack, `devstack.py`, `ASPNETCORE_ENVIRONMENT=Development`) | Yes | Plain HTTP `http://authority:8080` | HS256 shared secrets from the local `.env` | `TestIdentityProvider` | Docker bridge networks; `authority-net` internal | Functional behaviour only. **Never used as security evidence.** | Anything about security |
| **Security-test stack** (`enterpriseforms-sectest`, `Testing`, fault injection on) | Yes | Plain HTTP; Authority internal contract also on `127.0.0.1:5190` for the harness | HS256; some test-only keys committed in the overlay (F-12) | `TestIdentityProvider` | As Development, plus an "attacker" BusinessDb-admin client on `business-net` only | **Logic and database-privilege properties:** ordering, monotonicity, rejection handling, refusal of wrong credentials, no state change by lookups, grants and triggers, quarantine behaviour, hostile-BusinessDb writes. This is where RC-01…RC-28 run. | Transport authenticity, production identity, key custody, network isolation in Azure |
| **Hosted CI** (GitHub Actions) | Workflows exist; **not run for C8** (C-81) | Same as the security-test stack, on an ephemeral runner | Ephemeral synthetic secrets | `TestIdentityProvider` | Runner-local Docker | Reproducibility of the security-test results on a clean machine, tied to a commit (D8-19) | As the security-test stack |
| **Azure staging** | **No.** The Authority as built refuses to start outside Development and Testing (Entra not implemented, §2.2) | Required: HTTPS with mTLS (C-1) | Required: separate Entra managed identities per workload (Q-9, Q-10) | Required: Entra ID (PB-08) | Required: private networking, Canadian regions (PB-10) | Once built: transport and identity properties, credential isolation, Managed HSM and ledger behaviour (PB-03 Level B) | — |
| **Production** | **No**; blocked by PB-03, PB-12 and the same startup guard | As staging | As staging | As staging | As staging | — | — |

**Consequence for C8-M1-01 closure.** D8-19 closure evidence comes from the security-test stack and hosted CI. It can establish AC-9's **logic** (binding, ordering, fail-closed, no mutation, credential refusal) but **not** its transport or identity guarantees. Those remain part of the open Azure-staging (PB-03 Level B) gate, which C-1 makes a hard start condition. Closing C8-M1-01 must not be described as proving production transport security.

## 11. Independent review requirements

**Who qualifies.** A reviewer who:
- is not the owner;
- is not the implementation agent or another instance of it;
- did not author AC-9 or Design R;
- is independent of the business-deployment chain, as the PB-12 independent Security Owner role requires.

A review performed by the owner, by Claude, or by another automated agent acting on the owner's or Claude's instruction is **not** independent and must not be recorded as such.

**What the reviewer receives:**
- this package;
- the C-83 proposal;
- `docs/architecture/PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md` §5.5a and §10;
- `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md` and `TRUST_AUTHORITY_ARCHITECTURE.md` §5;
- the source files cited in §1.2, §2.2, §2.3 and §2.4, at commit `83f062b` or the commit that records this package.

**What the reviewer must record** (in `docs/PHASE_C8_STATUS.md` and as a decision-log entry, made by the owner from the reviewer's signed statement):
1. reviewer name or role and the independence statement above;
2. the commit reviewed;
3. a decision for each of Q-1…Q-11: accept the proposed answer, accept with conditions, or reject with a required change;
4. final severities for F-1…F-12, and any new findings;
5. an overall verdict: AC-9 approved for implementation, approved with conditions, or not approved;
6. date.

**Not evidence of review:** this package, its proposed answers, the owner's approvals, automated test results, and the spec-manifest validation.

## 12. What remains before implementation is authorized

1. **Independent security review** recorded as in §11, with a decision on every question Q-1…Q-11.
2. **Owner decisions on any reviewer conditions.** In particular:
   * Q-10: whether the reconciler runs as its own container;
   * Q-11: whether a separate BusinessDb reconciler role is introduced, which would add a database role and a migration;
   * any request for a signed outcome under Q-1, which would be a new cryptographic mechanism.
3. **Owner authorization to implement**, naming the scope:
   * the AC-9 endpoint and the C-1…C-3 guards in the Authority;
   * the reconciler in the Worker;
   * migrations for `authority_command_operations` and `team_membership_projection_states`;
   * removal of the API roster write, and the browser polling change (R-4);
   * RC-01…RC-19 and RC-21…RC-28.
4. Nothing else is required in advance. C8-M1-01 then closes only under D8-19: implementation complete, the closure set passing on the security-test stack, and the required hosted verification passing.

Until then: no AC-9 or Authority change, no Worker or migration work, no projection change, no deployment or merge, and no M2.

*§12 is superseded by C-87: the owner authorized implementation on 2026-10-30. Review, owner decisions and merge authorization remain gates (§13.5).*

## 13. Implementation handoff for the independent reviewer (C-87)

Prepared by the implementation agent. **This section is not a review.** It shows where each design element lives in the code, so the reviewer can check the code instead of trusting this package.

### 13.1 What to review

- **Branch:** `phase-c8/ac9-durable-reconciliation`, PR #32. Its base is the PR #31 branch `phase-c8/administration-governance` at `69a6717`; PR #31 is a separate review and merge.
- **Diff under review:** `git diff 69a6717..<head of PR #32>`. Code was frozen at `79978a3`. Later commits change only the CI budget (`stage-b-integration.yml` timeout) and documentation.
- **Evidence:** `docs/evidence/phase-c8/ac9-reconciliation-verification.md`, which keeps local, hosted and not-run results separate.

### 13.2 File map

| Design element | Code | Tests |
|---|---|---|
| AC-9 contract (§1.1) | `src/authority/AuthorizationAuthority.Contracts/V1/ReconcilerContracts.cs`; `contracts/openapi/authorization-authority-internal-v1.json` | `ReconcilerBoundaryTests` |
| AC-9 semantics (§1.2): binding, snapshot, pair at P, uniform not found, status only | `src/authority/AuthorizationAuthority.Application/Features/SecurityCommands/SecurityCommandOutcomeService.cs`; `AuthorityStore.BeginReadOnlySnapshotScopeAsync` | `SecurityCommandOutcomeServiceTests` |
| Endpoint, assertion refusal, body parsing, 503 mapping, rate limit, C-3 logging | `src/authority/AuthorizationAuthority.Api/Features/SecurityCommands/SecurityCommandOutcomeEndpoints.cs`; `Program.cs` | `ReconcilerBoundaryTests` (RC-18, RC-27, RC-28) |
| Reconciler scheme and policy; C-1/C-2 Authority guards (§1.4, §2.2) | `src/authority/AuthorizationAuthority.Api/Composition/InternalAuthentication.cs` (`ValidateReconciler`) | `ReconcilerBoundaryTests` |
| Reconciler client; C-1/C-2 Worker guards | `src/backend/EnterpriseForms.Infrastructure/Authority/TeamMembershipReconcilerHttpClient.cs`; port `ITeamMembershipOutcomeAuthority.cs` | `TeamMembershipReconciliationUnitTests` |
| §1.3 validation (fail closed) | `.../Features/Teams/MembershipReconciliation/TeamMembershipOutcomeValidator.cs` | `TeamMembershipReconciliationUnitTests` (RC-22) |
| §4.2 projection with R-6, I-1…I-11, NotCommitted, quarantine, 401/403/429 handling | `.../MembershipReconciliation/TeamMembershipReconciliationService.cs`; host `src/backend/EnterpriseForms.Worker/Features/Teams/TeamMembershipReconciliationWorker.cs` | `TeamMembershipReconcilerServiceTests`; Stage B `TeamMembershipReconciliationTests` |
| §5 schema, column grants, guard triggers | `src/backend/EnterpriseForms.Infrastructure/Persistence/Migrations/20261030130521_PhaseC8TeamMembershipReconciliation.cs`; `BusinessRuntimeGrants.cs`; `TeamMembershipReconciliationConfigurations.cs`; domain `TeamMembershipReconciliation.cs` | RC-19, RC-23 (Stage B) |
| R-3 API path (T1/T2, never writes the roster) | `.../Features/Teams/ManageTeam/ChangeTeamMembershipHandler.cs` | `TeamMembershipSingleWriterTests` (RC-26); `PendingAuthorityCommandTests` |
| R-4 read-only status and audited operator actions | `.../ManageTeam/TeamMembershipOperationHandlers.cs`; `TeamEndpoints.cs`; `TeamDirectoryHandlers.cs`; `apps/web/src/features/teams/pages/TeamDetailPage.tsx` | RC-16, RC-17; `membershipOutcome.test.ts` |

### 13.3 Q-1…Q-11 as implemented (reviewer decides each)

| Q | State in code | Decision still open |
|---|---|---|
| Q-1 signed outcome | **Not implemented.** Channel authentication, §1.3 validation and C-1 start guards. Plain HTTP on synthetic stacks only | Signed responses: required or not; if required, a new cryptographic mechanism needing owner approval |
| Q-2 own pair only | Implemented: the pair comes from the command's stored payload, never from the caller | Accept or change |
| Q-3 journaling | Logs and metrics; refusals counted and logged as warnings; not journaled | Accept or require journal entries |
| Q-4 single writer | Implemented in code. The API roster write is removed; RC-26 enforces it | Confirm |
| Q-5 drift at E = W | Corrected and alerted (`TEAM_MEMBERSHIP_PROJECTION_DRIFT`) | Correct or park |
| Q-6 rate limit | 20/s, burst 40, per instance, per validated issuer, bounded ≤ 50/s at startup | Accept or change |
| Q-7 uniform not found | Implemented | Accept or change |
| Q-8 quarantine | Status only (`membership` null) | Accept or require 503 |
| Q-9 production identity | Not applicable until Entra exists; own credential everywhere | Before production design |
| Q-10 Worker isolation | **Not implemented.** Reconciler credential sits in the existing Worker beside the `business-api` and verifier credentials (F-9) | Own container and managed identity: yes/no, and by when |
| Q-11 database permissions | **Not implemented.** API and Worker share one runtime role; I-10 enforced by code and RC-26 only (F-10) | Separate reconciler role with API reduced to SELECT: yes/no |

### 13.4 Findings: implementation status (final severities are the reviewer's)

| F | State after implementation |
|---|---|
| F-1 | Unchanged (plain HTTP everywhere); C-1 implemented on both sides as a start condition |
| F-2 | Fixed by R-1, R-2, R-6; RC-10 and RC-11 |
| F-3 | Unchanged (out of scope, D8-23) |
| F-4, F-5, F-7, F-12 | Unchanged residuals |
| F-6 | Unchanged residual. **No test measures the found / not-found timing difference, and no timing result exists.** RC-18 asserts only status codes, bodies and absence of state change; the earlier statement that RC-18 measures timing was wrong and is withdrawn (C-88). Non-disclosure is **not claimed**: a deterministic test cannot establish it on a shared CI host (scheduler and database latency noise exceed any plausible code-path difference), and a statistical test would be flaky or prove nothing. Both answers run the same bound lookup in one snapshot, but that is a design argument, not evidence. The reviewer must assess it, and if a measurement is wanted, a dedicated test on a quiet staging host (the `reconciler_outcome_lookup_duration` histogram is not labelled by `found`, so today it cannot separate the two cases) |
| F-8 | Implemented surface; this review |
| F-9 | Unchanged (Q-10) |
| F-10 | Unchanged (Q-11) |
| F-11 | Corrected (R-6); RC-11 and RC-23 |

**Observations from the implementation agent's own read-through (not findings of an independent review):**

| N | Observation | Effect | Suggested handling |
|---|---|---|---|
| N-1 | **Corrected (C-88).** AC-9 enforced its 4 KiB request-body cap only when `Content-Length` was declared | A holder of the reconciler key could send a large body; that holder is already rate-limited | The read itself is now capped at 4096 bytes (a 4097-byte buffer is read at most), so chunked bodies and bodies without `Content-Length` are refused with the same 400 `INVALID_REQUEST` before any parsing or store access; the Kestrel limit is also set when the host allows. Tests `N1TheFourKibBodyCapHoldsForChunkedAndDeclaredLengthBodies` (4096, 4097 and 1,000,000 bytes, declared and chunked; a body over the cap that would otherwise reach the store is refused 400, not 503) |
| N-2 | **Corrected (C-88).** R-3 recorded the operation (T1) before any permission result was checked | A non-administrator in the same organization could create operation rows that ended `Rejected` | `ChangeTeamMembershipHandler` now runs `AdministrationAccess.RequireAdministratorAsync` (the Authority's own answer for organization owner or Security Administrator, the two principals its team-command rule accepts) before T1; a denial is 403 `FORBIDDEN` with no row and no submit. Nothing is widened, and the Authority still decides the command at submit, bound to the revision from the check: access lost after the check is refused there and recorded `Rejected`. Residual: an administrator can still create rows the Authority later rejects (for example a Security Administrator adding themself, G25, or a nonexistent team). The team is not checked against the projection first, because the Authority may know a team the projection has not received yet. Tests `TeamMembershipPreliminaryAuthorizationTests` (neither role, other organization, either role then Authority refusal, revocation between check and command) |
| N-3 | The Authority-side C-1 checks `ClientCertificateMode = RequireCertificate` but does not bind the certificate to the reconciler identity | Not reachable today: the Authority refuses to start outside Development/Testing | Part of the production identity design (Q-9) |
| N-4 | The new tables have no row-level security, like every other BusinessDb table today (G08 open). Tenant isolation is the tenant predicate in each query and handler (uniform 404 across tenants; RC-14) | No regression against the existing business schema | Part of G08 |
| N-5 | `authority_command_operations.team_id` and `member_user_id` have no foreign keys; `team_membership_projection_states` has them | A row naming a nonexistent team or user cannot project anything: AC-9 answers about the Authority's pair, and the validator rejects any mismatch (RC-13) | Accept or add the FKs |

### 13.5 Confirmations requested from the reviewer

1. **Tenant isolation.** AC-9 reads one organization's rows in one snapshot and answers cross-organization keys with the same `found:false` (RC-14). Business handlers check tenant and team and answer 404 uniformly. The reconciler binds organization across row, request, response and writes (I-6; validator `answer.OrganizationId != operation.TenantId`).
2. **Authorization.** The Authority remains the only decider of team membership. The reconciler holds no submit capability (RC-26, architecture test). The roster is a display projection and never confers permission (§4M). The reconciler credential satisfies only AC-9, and no other credential satisfies AC-9 (RC-18).
3. **Integrity.** AC-9 is read-only: no lock, revision, journal, outbox or SpiceDB call. No `RowSignature`, Merkle or signing path changed. The new tables are projection-class (§5.3), guarded by schema-owned triggers and column grants, with no DELETE.

Record the review as §11 requires. Until then AC-9, C8-M1-01 and C8-M1-02 stay **implemented, not resolved**.

### 13.6 Post-implementation corrections before the independent review (C-88)

Owner-approved, narrowly scoped: N-1 and N-2 corrected as above; F-6 evidence statement corrected (no timing claim). Authority semantics, permissions and the Authority API contract are unchanged. Row 409 (RC-18) lists no timing assertion and none exists.
