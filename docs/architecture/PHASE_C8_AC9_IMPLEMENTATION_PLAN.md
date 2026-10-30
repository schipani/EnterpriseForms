# Phase C8 — AC-9, C8-M1-01 and C8-M1-02: implementation plan (C-87)

- **Status:** **implemented and locally verified** (C-87; evidence `docs/evidence/phase-c8/ac9-reconciliation-verification.md`) on `phase-c8/ac9-durable-reconciliation` (from `phase-c8/administration-governance` at `69a6717`, the PR #31 head). Not merged, not deployed.
- **Authority for this work:** the owner's instruction of 2026-10-30 (recorded as C-87) reverses the earlier "accepted limitation" closure, authorizes implementation of Design R with D8-11…D8-23, and keeps independent security review and owner approval as **closure and merge gates**. It does not authorize anything outside the approved design.
- **Independence:** this plan and every implementation decision below were made by the implementation agent (Claude). None of it is an independent review. AC-9 remains **not independently reviewed** (package §11).
- **Base design:** `PHASE_C8_AC9_SECURITY_REVIEW_PACKAGE.md` (C-85). Where this plan says "as package", the package text governs.

## 1. Reconciliation of the package with the code at `69a6717`

| Package statement | Verified in code | Consequence for the implementation |
|---|---|---|
| Lookup by unique `(organization_id, command_idempotency_key)` | `uq_security_command_idempotency`; `FindCommandByIdempotencyKeyAsync` | reused |
| Rejections are persisted with their key | Only failures **inside** the `SubmitAsync` try block (quarantine, stale revision, planner refusals) persist a Rejected row. Refusals **before** it (unknown organization, unbound actor, invalid key length, idempotency conflict, 401) persist nothing | A refused submit with no persisted row looks like "not found" to AC-9. The API records definitive 4xx refusals on the operation itself (§3.2), so the Worker never waits out the in-flight window for them |
| Rejected payload | A rejected row stores the **raw** request payload, not a normalized one | AC-9 parses `team_id`/`member_user_id` from it; an unparseable payload returns null pair fields, which the reconciler treats as `OPERATION_MISMATCH` |
| Effective strictly in revision order | `ProjectionProcessor` applies `applied+1` only | relied on (§1.2.3) |
| Pair history from edges | `AuthorityRelationshipEdge.CreatedBySecurityCommandId` / `RemovedBySecurityCommandId`, removed edges retained | new read-only port `GetPairEdgeHistoryAsync` |
| One read-only snapshot | `BeginScopeAsync` opens **Read Committed** | new `BeginReadOnlySnapshotScopeAsync` (Repeatable Read, `READ ONLY`) used only by AC-9 |
| Roster rows | `team_memberships` keeps history (`removed_on`), unique active `(team_id, user_id)` | "present" = an active row exists; "absent" = none. The roster's `authority_command_id` is the operation's command when E equals its revision, otherwise null (AC-9 does not name the creating command; no contract extension) |
| Worker credentials | Worker holds `business-api` workload + verifier keys | reconciler key added beside them (F-9 unchanged; Q-10 open) |
| No business-side triggers yet | none in any business migration | the two schema-owned triggers below are the first; owned by the migration identity, not the runtime role |
| No Authority rate limiter | none | ASP.NET Core built-in limiter, first use |

## 2. AC-9 in the Authority

- `POST /internal/v1/security-command-outcomes/lookup`, contract exactly as package §1.1, error table as §1.1.
- `ReconcilerScheme` / `ReconcilerPolicy`, `workload_role = business-projection-reconciler`, own `Identity:Reconciler` credential. Unconfigured ⇒ the policy denies everything.
- C-2 startup guard: partial configuration refuses to start; key or issuer equal to the workload, operator, user-assertion or verifier credential refuses to start.
- C-1 startup guard: outside Development/Testing a configured reconciler requires `Kestrel:EndpointDefaults:ClientCertificateMode = RequireCertificate`; otherwise the Authority refuses to start. (The Authority cannot start outside Development/Testing today anyway; the guard makes the condition explicit.)
- `X-Delegated-User-Assertion` present ⇒ 400 `DELEGATED_ASSERTION_NOT_ACCEPTED`; `X-Correlation-Id` required and echoed.
- Read-only: one Repeatable Read, read-only transaction; no lock, revision, journal, outbox or SpiceDB call.
- C-3 logging: organization, kind, found, state, outcome, correlation, latency, `security_command_id`; never the key, team or member. Refusals counted by a `Meter` and logged as warnings.
- Rate limit: token bucket per workload name, 20/s, burst 40, configurable ≤ 50/s; 429 `RATE_LIMITED` with `Retry-After`.

## 3. Business side

### 3.1 Tables (migration `PhaseC8TeamMembershipReconciliation`, owner `Teams`)

- `authority_command_operations` (package §5.1) and `team_membership_projection_states` (package §5.2), descriptive multi-word columns, UUID v7 keys.
- Runtime grants: `SELECT, INSERT` plus **column-level** `UPDATE` on mutable columns only; no `DELETE`.
- Schema-owned triggers: identity columns immutable; `authority_command_id` / `committed_authorization_revision` only NULL → value; completion fields frozen once terminal; watermark never lowered.
- Coverage inventory: both tables registered as untrusted projection-class tables without `RowSignature` (package §5.3).

### 3.2 API path (R-3, single logical writer)

1. T1: insert the operation (`AwaitingCommit`) **before** submit; same key + same request ⇒ reuse; same key + different request ⇒ 409 `IDEMPOTENCY_KEY_REUSED`, no second operation (RC-12). A terminal operation is never re-sent (I-8).
2. Submit with the original prefixed key.
3. T2: committed ⇒ record command id and revision, `AwaitingEffective`, `next_attempt_on = now`. Definitive 4xx refusal ⇒ `Rejected` with the code (no roster change). Transport failure or 5xx ⇒ left `AwaitingCommit` for the Worker.
4. The API **never writes `team_memberships`**. It waits a bounded time (default 5 s) for the Worker to complete the operation and returns the operation state with the command outcome.

### 3.3 Worker reconciler

- `TeamMembershipReconciliationService` in `Application/Features/Teams/MembershipReconciliation`, hosted by `TeamMembershipReconciliationWorker` in the Worker only.
- Lease with `FOR UPDATE SKIP LOCKED`; lookup through `ITeamMembershipOutcomeAuthority` (reconciler credential only; no submit dependency).
- Validation per package §1.3 (fail closed, `INVALID_AUTHORITY_RESPONSE` / `OPERATION_MISMATCH`, alert, nothing projected).
- Projection per package §4.2 with R-6: one transaction holding the watermark row lock writes roster, watermark and completion together (D8-21); an answer older than W is discarded and re-looked-up once; a fresh one older than W is `LOCAL_WATERMARK_AHEAD`.
- `NotCommitted` after two not-found lookups at least one in-flight window apart, and only after the window has passed since creation; late-commit sweep afterwards.
- Quarantine ⇒ `AwaitingQuarantineClearance`, nothing projected. 401/403 ⇒ reconciler `Unavailable`, no operation change. 429 ⇒ `Retry-After`, no error budget consumed.
- Timing parameters per package §6.3 with startup validation; Testing-only shortening.
- Audit events per package §5.4.

### 3.4 UI (R-4)

- The team detail returns the server's open operations for the team (so a second administrator sees them, RC-16); the page polls the business read endpoint and **never re-POSTs**.
- Operator actions "Check again" (re-opens `ReconciliationError`, audited, never submits) and "Mark reviewed" (audited acknowledgement).

## 4. Reviewer questions Q-1…Q-11: what this implementation does

| ID | Implemented as | Still needs |
|---|---|---|
| Q-1 | Channel authentication + §1.3 validation; C-1 start guard. **No signed outcome** (would be a new cryptographic mechanism needing owner approval). Plain HTTP on synthetic-data stacks only | independent reviewer decision |
| Q-2 | Only the command's own pair is returned | reviewer decision |
| Q-3 | Structured logs + metrics; refusals counted. **Not journaled** | reviewer decision |
| Q-4 | Single logical writer (D8-21) implemented; API roster write removed | reviewer confirmation |
| Q-5 | Correct to the Authority state and alert (`TEAM_MEMBERSHIP_PROJECTION_DRIFT`) | reviewer decision (switching to "park" is a small change) |
| Q-6 | 20/s burst 40, per instance, bounded ≤ 50/s | reviewer decision |
| Q-7 | Uniform `found:false` | reviewer decision |
| Q-8 | Status only during quarantine (`membership` null) | reviewer decision |
| Q-9 | Not applicable until production identity exists; the reconciler has its own credential everywhere | reviewer + owner before production design |
| Q-10 | **Not implemented**: the reconciler runs in the existing Worker container (F-9 unchanged) | reviewer + owner decision |
| Q-11 | **Not implemented**: no new BusinessDb role; I-10 enforced by code and the RC-26 architecture test (F-10 unchanged) | reviewer + owner decision (a new role and migration) |

## 5. Approvals still outstanding (blocking closure and merge)

1. Independent security review of AC-9 per package §11 (not performed; must not be performed or recorded by the owner or Claude).
2. Owner decisions on reviewer conditions, especially Q-1 (signed outcome), Q-10 (own container), Q-11 (separate database role).
3. Hosted CI verification on the final commit (D8-19).
4. Owner authorization to merge.

Until these are satisfied, AC-9, C8-M1-01 and C8-M1-02 are **implemented, not resolved**.
