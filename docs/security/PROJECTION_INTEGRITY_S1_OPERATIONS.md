# S1 projection integrity: operations, deployment rules and recovery boundary

- **Status:** C8 M4 S1, implemented under C-117 (Development/Testing evidence only). It is **not** production-certified. Azure staging performance and recovery evidence and an independent security review are still required.
- **Audience:**
  * Authority operators and the deployment pipeline owner;
  * the future G14 recovery work;
  * the independent reviewer.
- **Normative design:** `docs/architecture/PHASE_C8_M4_1_DESIGNS_M1_S1.md` revision 2 (§2), approved as A-Q2/B-Q2/B-Q5…B-Q9 in C-116/C-117.

## 1. What S1 guarantees, and what it does not

Every protected SpiceDB decision in the Authority runs **pin, decide, verify**:
1. Pin: a marker read at least as fresh as the Authority-held token.
2. Decide: every check and lookup at exactly that snapshot, with `checked_at`/`looked_up_at` proven equal to it.
3. Verify: a `fully_consistent`, cache-bypassing marker read after the decision, then a re-read of the Authority's own state.

The marker names the Authority's committed revision **and** its hash-chained authorization-journal digest (`step-{org}-{n}-{digest}`).

**Guarantee G-S1 (claimed under the invariants in §3):** no decision evaluated on a SpiceDB state other than the projection of the Authority's current committed revision and history is released. A stale authorization from a SpiceDB rollback is never released if the rollback is in place when the verify read runs, or arrives before the decision completes.

**Not claimed:**

| Residual | Status |
|---|---|
| **RE-1** A datastore reverting to an older state and back within one request's decide-to-verify interval (about 8–10 ms at p95), or two live datastore backends behind one SpiceDB at once | **Accepted residual risk (C-117)**, subject to the binding deployment rule in §3 (I3) and independent review. It cannot be eliminated by any Authority-side protocol. Mitigation: any mismatch observed by any read (decision pin, decision verify or the periodic check) quarantines immediately. |
| RE-2 A writer inside the Authority domain copying the current marker over stale tuples | Outside the Level 2 trust assumption; G14/S3 full reconciliation |
| RE-3 AuthorityDb and SpiceDB restored together to the same older revision | G14 (T16-A), open |
| RE-4 A revoke committed after a decision was released | Pre-existing post-decision window, unchanged |
| RE-5 A false quarantine (for example from SpiceDB misbehaving) | Fail-closed by design; it lasts until G14 recovery (§4) |
| RE-6 Undocumented SpiceDB behaviours (the `checked_at` echo, error classes) | Defence in depth only. Pinned by CT-1…CT-6, so an upgrade that changes them fails CI. |

## 2. Minimum safe Authority version and the rollback fence

- **Minimum safe version:** the Authority build containing migration `20261104231152_ProjectionIntegrityS1` and the S1 code (C-117 commit). **Never deploy, or roll back to, an Authority image older than that against a database that has applied this migration.**
- **Rollback fence (enforced, not only documented):**
  * The migration moves the Authority-held ZedToken to `projection_snapshot_zed_token` and constrains the pre-S1 column `projection_zed_token` to NULL (`ck_legacy_projection_token_retired`).
  * A pre-S1 Authority build reads its token from the old column, finds none, and keeps every organization's gate closed (`AwaitingProjection`).
  * Its projector cannot store a token (the constraint fails its commit), and its revision-only marker CAS no longer matches the history-bound markers.
  * **A rollback below S1 therefore fails closed for every organization. It never silently decides without S1.**
  * RB-10 asserts that the legacy column stays NULL.
- **Pipeline rule (PB-11):** the Authority deployment must record the minimum safe Authority version, and refuse to promote an older image digest to an environment whose AuthorityDb is at or beyond this migration. Rolling the migration back (`Down`) is a Development/Testing operation only.
- **Upgrade path for an existing environment:** at startup the Authority upgrades every pre-S1 marker (`step-{org}-{n}`) to the history-bound form, by CAS, under each organization's lock (the transition). Until the transition completes, readiness is false, no decision is pinned and the projector does not write. A marker that does not match the Authority's revision is **quarantined, not upgraded**.

## 3. Binding deployment rules (B-Q4, B-Q8)

| ID | Rule |
|---|---|
| I1 | Only the Authority projector writes SpiceDB: the preshared key and the authority network are held by the Authority alone (existing) |
| I3 | **Exactly one database backend behind SpiceDB at a time.** One connection URI to one primary. **No SpiceDB read replicas** until S1 is independently re-verified with them. Failover only through synchronous, same-endpoint HA (no data loss). **Any datastore replacement, restore or asynchronous-replica promotion must stop SpiceDB first** and is a recovery event under §4. |
| — | Do not run a second, independent SpiceDB in front of the same organizations, and do not point SpiceDB at a database restored from another environment. A cloned environment's markers carry a different history digest and are quarantined as `PROJECTION_FOREIGN_HISTORY`. |

## 4. Quarantine: permanent under normal operation; cleared only by an authorized G14 recovery

- **Persisted:** in `organization_authorization_states.integrity_quarantine_enabled` and `integrity_quarantine_reason_code`, together with an audit-stream journal event `ProjectionIntegrityQuarantined` in the same transaction (hash-chained, archived). After the commit, a Critical alert `PROJECTION_INTEGRITY_QUARANTINE` (log event id 9100) is raised.
- **Enforced at:**
  * every protected decision (gate);
  * catalogue snapshot validation;
  * every security command;
  * the projector, which does not write SpiceDB for a quarantined organization (it cannot mask a quarantine);
  * onboarding, owner recovery and provisioning.
- **Permanent under normal operation:**
  * No application code path clears a quarantine, enforced by architecture test ARCH-03.
  * `OrganizationQuarantine.Set` keeps the first reason.
  * The AuthorityDb trigger `trg_organization_integrity_quarantine_is_permanent` refuses any UPDATE that clears it or changes its reason, for every role, the database administrator's ordinary UPDATE included (RB-10).
- **Clearing is reserved for a future, independently authorized G14 recovery process.** The trigger does not prevent one. That process lifts a quarantine through its own reviewed migration or function, under the separately controlled migration identity (`CREATE OR REPLACE FUNCTION authority.organization_integrity_quarantine_is_permanent()`, or an approved recovery function), and must at minimum:
  1. rebuild the organization's SpiceDB relationships from AuthorityDb desired state, with a new history-bound marker and a new pin;
  2. verify the rebuilt projection (complete relationship comparison, not only the marker);
  3. require two distinct authorized human approvals with fresh step-up;
  4. record the recovery operation and its evidence in the independent archive;
  5. invalidate sessions, unlocks, tokens and cached decisions issued before recovery (recovery epoch).
- **S1 implements none of this,** by owner decision (B-Q1): until G14 exists, a quarantined organization stays unavailable for protected access. In Development and Testing the stack is disposable.
- **Testing only:** Stage B `RC24` (team-membership reconciler) needs a quarantine lifted to test that reconciliation resumes. It stands in for the privileged step with `ALTER TABLE … DISABLE TRIGGER`, one `UPDATE`, then `ENABLE TRIGGER`, in a single transaction, as the database owner on the disposable stack. The same DDL privilege that would let a recovery lift a quarantine lets an AuthorityDb administrator do so too: the trigger stops ordinary `UPDATE`s by any role, not DDL by the table owner. That administrator is inside the Level 2 Authority trust boundary (RR-7, G08).
- **Reason codes:**

| Reason code | Meaning |
|---|---|
| `PROJECTION_MARKER_BEHIND` | The marker is older than the committed revision (datastore rollback) |
| `PROJECTION_MARKER_AHEAD` | The marker is newer than the committed revision (AuthorityDb older than SpiceDB) |
| `PROJECTION_MARKER_MISSING` | No marker (empty, other-cluster or pre-bootstrap datastore) |
| `PROJECTION_MARKER_INVALID` | Several, malformed or legacy markers |
| `PROJECTION_FOREIGN_HISTORY` | The right revision with another history's digest |
| `PROJECTION_SCHEMA_MISSING` | A datastore without schema |
| `JOURNAL_CHAIN_GAP` | The projector found no chain digest to bind the marker to |

## 5. Telemetry and alerts

| Signal | Where | Meaning and action |
|---|---|---|
| `SECURITY ALERT PROJECTION_INTEGRITY_QUARANTINE` (Critical) | Authority log event 9100, routed to the SIEM in production | An organization was quarantined: investigate the datastore and start the G14 recovery procedure; never "fix" the row |
| `PROJECTION_SNAPSHOT_MISMATCH` (Critical) | same | SpiceDB returned a decision not provably at the pinned snapshot: SpiceDB behaviour changed; the contract tests must be re-run |
| `PROJECTION_SNAPSHOT_UNAVAILABLE` (Warning) | same | A pinned snapshot expired or was unknown mid-request; denied |
| `projection_integrity_verifications{outcome}`, `projection_integrity_marker_read_ms{phase}`, `projection_integrity_decision_ms{path}` | meter `AuthorizationAuthority.ProjectionIntegrity` | Outcome rates and marker-read and decision latency. Production export is a deployment item. |

The journal event and alerts carry organization, reason, revision and marker identifiers only, never users, subjects, resources or permissions.

## 6. Periodic check (S2)

Every `ProjectionIntegrity:PeriodicCheckSeconds` (10–600 s; default 60; the security-test stack uses 10), and 5 s after a pass that could not reach SpiceDB, every current, non-quarantined organization's marker is read fully consistently. A mismatch with an unchanged Authority state quarantines. It complements the decision-time protocol and never replaces it.

## 7. G14 backlog (unchanged, not implemented by S1)

S1 does not implement any of the following. They remain open G14 requirements (`trust_authority/COORDINATED_DISASTER_RECOVERY.md`, RR-14):
- coordinated database restoration;
- journal replay;
- the recovery epoch;
- SpiceDB rebuild (S3);
- reconciliation;
- quarantine clearing;
- T16-A;
- disaster-recovery drills.
