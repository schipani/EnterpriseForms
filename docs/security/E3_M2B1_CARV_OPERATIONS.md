# E3-M2b-1 CARV — Operations and Deployment Runbook

**Status (2026-11-15):** E3-M2b-1 accepted by the owner for Development/Testing (owner-led review; contract §20.9) and committed locally. E3-M2b-1F (the floor) implemented and accepted by the owner for Development/Testing (owner-led review; contract §21.10) and committed locally. Release B is not activated; mandatory gate RB-CARV-1 (§2a) is open. Not deployed anywhere. Not an independent security review, not a certification. The normative contract is `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §19 (design) and §20 (implementation record).

**Scope.** The content acceptance rule version (CARV) fence on `authority.content_change_operations`: migration `20261115014221_AuthorityContentAcceptanceRuleVersion` (M2b-1) and the separate floor migration `20261115032228_AuthorityContentAcceptanceRuleFloor1` (M2b-1F: implemented for Development/Testing, contract §21; **not activated anywhere except disposable test databases**).

## 1. What the fence guarantees, and what it does not

| Guarantee | Mechanism |
|---|---|
| A CARV build never commits an operation prepared under other rules (an older build's NULL stamp included) | Application rule at Commit (abort, `409 OPERATION_RULES_OUTDATED`) **and** the database commit CHECK (*C* = *P*, or both NULL) |
| A pre-CARV build never commits an operation a CARV build prepared | Database commit CHECK (the legacy build writes no commit stamp) |
| No direct insert as Committed, no change of a terminal operation, no change of the Prepare stamp | Database state-guard trigger `trg_content_operation_rule_state_guard` |
| A refused write advances nothing | One transaction per Prepare/Commit; the database refusal rolls back the head, manifest, leaves, identities, index, journal and checkpoint |

**Not guaranteed before the floor (M2b-1F):** a pre-CARV instance that is still running keeps committing *its own* operations under its own (older) rules (R-CARV-1). The floor closes this once activated in Release B: afterwards the database refuses every Prepare and every Commit of a pre-CARV build (contract §21).

**Out of scope:** a compromised Authority runtime or migration identity (Level 2 trusts the Authority; R-CARV-3).

## 2. Deployment sequence (Development/Testing first; the same order later)

1. **Apply M2b-1** with the Authority migration identity (`AuthorizationAuthority.Api.dll --migrate`). Expand-only. The migration runs in one transaction with `lock_timeout = 5s`: if an open transaction holds `content_change_operations`, it fails with `55P03` and applies nothing (no column, CHECK, trigger or history row). Retry; on repeated timeouts find the long-running transaction in `pg_stat_activity`.
2. **Deploy the CARV v1 Authority binaries** (rolling or blue/green). Pre-CARV instances keep working. During the overlap an operation that crosses builds is refused (availability errors, never acceptance):
   - CARV Prepare → legacy Commit: database refusal; the legacy host answers `503 AUTHORITY_NOT_READY retryable` (R-CARV-2); the operation stays Prepared and expires.
   - legacy Prepare → CARV Commit: `409 OPERATION_RULES_OUTDATED`; the operation is aborted.
3. **Confirm that no pre-CARV instance remains.** Use the deployment inventory and the database:
   ```sql
   SELECT application_name, count(*) FROM pg_stat_activity WHERE usename = '<authority runtime role>' GROUP BY 1;
   ```
   CARV v1 connections carry `carv-v1` in `application_name` (for example `authority carv-v1`). Any Authority runtime connection without the marker is a pre-CARV instance (or a non-store connection such as `authority-work-listener`, which never writes content operations).
4. **Release B: activate the floor with the explicit command**, in a separate, later release (OD-M2-21/28), with the migration identity:
   ```
   ConnectionStrings__AuthorityDbMigration=<migration identity> dotnet AuthorizationAuthority.Api.dll --apply-content-acceptance-floor 1
   ```
   - **Separation (why this cannot happen by accident).** The floor migration belongs to its own model-less EF context (`ContentAcceptanceFloorContext`). The ordinary `--migrate` runs `AuthorityDbContext`, which cannot discover it, so deploying or migrating any build, including one that contains the floor, never applies the floor. The command refuses to run together with `--migrate`, and the floor number must be stated (`1`).
   - **Preconditions, checked by the migration itself under an ACCESS EXCLUSIVE lock on `content_change_operations`** (so no operation can start or finish between the check and the floor): the E3-M2b-1 fence is complete; no floor is installed yet; no unexpired operation prepared by a pre-CARV build is Prepared; no session that can write content operations is connected without a `carv-v` marker in `application_name` (the LISTEN-only `authority-work-listener` and superusers are ignored). Any refusal applies nothing.
   - **Exit codes:** 0 installed (or already installed: a re-run is a no-op); 2 usage; 3 not applied (precondition refusal, lock timeout `55P03`, interruption); 4 recorded but missing (§6).
   - **Before running it:** step 3 done; in-flight pre-CARV operations reconciled (let them expire, or let a CARV instance abort them: its Commit answers `OPERATION_RULES_OUTDATED`); a low-traffic window (the lock queue can stall new readers for up to 5 s, R-CARV-5).
   - **After it:** enforce `ContentAcceptance:RequiredFloor = 1` on every Authority instance through protected deployment configuration (mandatory, §2a). Readiness then detects a missing or lowered floor (§3, §6).
5. **Never run `VALIDATE CONSTRAINT`** on a floor (D-4): historical rows are permanently unstamped.

## 2a. Mandatory Release B gate RB-CARV-1 (open)

A Release B deployment is not permitted until all of the following hold (contract §21.10). This is a release requirement, **not** a claim that the current implementation satisfies it or has been proven in any deployment configuration.

1. **Protected configuration.** `ContentAcceptance:RequiredFloor = 1` is enforced through protected deployment configuration on every Authority instance after floor activation. Business deployers and the Authority runtime cannot remove or lower it (PB-11 separately permissioned Authority pipeline), and changes are audited. A Release B deployment without the setting fails the release gate (PB-12 evidence).
2. **Fail closed.** A Release B Authority instance refuses Authority writes when its required floor is absent, lowered, or cannot be verified.
3. **Restore.** After a restore of AuthorityDb to a pre-floor state, Authority writes do not resume until the floor has been reinstated with `--apply-content-acceptance-floor 1` and every instance reports `floor 1` (§6).
4. **Evidence.** Executed Azure staging tests for items 1–3, linked through the PB-12 chain.

**Current state (Development/Testing):** detection is implemented: with `RequiredFloor` set, an absent, lowered or recorded-but-missing floor, a failed schema read and a stale decision all make the instance **not ready** (§3). Two things are **not** implemented. First, Authority write paths do not consult that decision: writes stop only if the deployment withdraws a not-ready instance from all write traffic and the organization gates stay closed, which is unproven. Second, `RequiredFloor` is ordinary configuration, and an absent setting silently means Release A semantics. Item 2 needs either a separately authorized production change that makes writes refuse on a not-ready decision (recommended), or proven deployment wiring.

## 3. Readiness guard

Every CARV build evaluates the fence at startup and every 30 s (OD-M2-30) and reports **not ready** if:

| Detail | Meaning | Action |
|---|---|---|
| `ContentAcceptanceRuleMigrationMissing` | M2b-1 is not in `authority.ef_migrations_history` | Apply step 1 |
| `ContentAcceptanceRuleSchemaIncomplete` | a stamp column or the commit CHECK is missing | Investigate (privileged change); do not route traffic |
| `ContentAcceptanceRuleStateGuardMissing` | the state-guard trigger is missing or disabled | Investigate (privileged change); do not route traffic |
| `ContentAcceptanceRuleFloorAboveBuild(floor=F, build=V)` | a newer floor overtook this build | Deploy a build with rule version ≥ F |
| `ContentAcceptanceRuleFloorMissing(...)` | the migration history records the floor but its constraint is absent (privileged drop, partial restore) | §6: investigate, then `--apply-content-acceptance-floor 1 --reinstate-recorded-floor` |
| `ContentAcceptanceRuleFloorBelowRequired(installed=…, required=R)` | `ContentAcceptance:RequiredFloor` is R but a lower floor (or none) is installed, for example after a restore to before the floor | §6: re-apply the floor before reopening writes |
| `ContentAcceptanceRuleRequiredFloorAboveBuild(required=R, build=V)` | the configured required floor is above this build's version | Configuration error, or deploy a build with version ≥ R |
| `ContentAcceptanceRulesUnverified` / `ContentAcceptanceRulesStale` | the fence could not be read, or the monitor stopped | Check AuthorityDb reachability and the host |

Readiness is operational hygiene. **The database refuses every crossing write whatever readiness says**; enforcement never depends on the 30-second interval or the 10-minute operation lifetime.

## 4. Telemetry and alerts

| Signal | Source | Meaning |
|---|---|---|
| Security alert `CONTENT_ACCEPTANCE_RULE_REFUSED_BY_DATABASE` (event id 9100, Critical) | Authority store adapter | The database fence refused a write of this CARV build (a newer floor, or a mixed fleet). The caller received `409 OPERATION_RULES_OUTDATED`. |
| Log event 9211 (Critical) | Readiness guard | The instance turned not ready (detail as in §3) |
| Log events 9210 / 9212 | Readiness guard | Ready again / the fence could not be read |
| Authority `503 AUTHORITY_NOT_READY` during a rollout | Pre-CARV hosts | Probably a CARV-prepared operation reaching a legacy Commit (R-CARV-2); alert on Authority 503 rates during rollouts |
| `operation_quarantine_reason_code = 'OPERATION_RULES_OUTDATED'` on Aborted operations | AuthorityDb | Operations prepared under other rules and refused by a CARV Commit (expected during a rollout; should stop after step 3) |

**Open before deployment (R-CARV-12):** whether mandatory Authority audit policy requires a durable audit-stream event in addition to this operational alert.

## 5. Rollback

| When | Allowed | Effect |
|---|---|---|
| After step 1 or 2, before step 4 | Redeploy the E3-M2a binary | Works (it maps neither column). CARV-prepared in-flight operations fail at its Commit (503) and expire. |
| After step 4 | Binary rollback below CARV v1 | **Impossible by design**: every legacy Prepare is refused. Roll forward only. |
| M2b-1 Down | Development/Testing only | Refused while any CARV-prepared operation is Prepared, or once any floor exists |
| Floor migration Down | Never | Its Down raises `floors are never lowered` in every environment; there is no removal command |
| Legacy binary after the floor | Never | Every Prepare and Commit of a pre-CARV build is refused by the database; the legacy host reports `503 AUTHORITY_NOT_READY`. Roll forward only |
| Floor Down | Never | — |

## 6. Recovery (R-CARV-4)

| Situation | Detection | Operator action before reopening Authority writes |
|---|---|---|
| Floor activation interrupted (lock timeout, killed process or backend, error) | Exit code 3; no `ck_content_operation_rule_floor_v1`, no history row | Nothing to undo (one transaction). Fix the cause (long transaction, in-flight pre-CARV operation, unmarked session) and re-run the command. |
| A restore of AuthorityDb to a point before the floor (constraint and history row gone) | Readiness `ContentAcceptanceRuleFloorBelowRequired` on instances configured with `RequiredFloor = 1`. **Without that setting the database alone cannot tell** (it looks like Release A) | Keep the organization gate closed; re-run `--apply-content-acceptance-floor 1`; confirm every instance reports `floor 1`. Restored `Prepared` operations are past their lifetime or refused by CARV. |
| The floor constraint missing while its history row remains (a privileged drop, a partial restore) | Readiness `ContentAcceptanceRuleFloorMissing`; the command exits 4 | Investigate the privileged change (incident). Then `--apply-content-acceptance-floor 1 --reinstate-recorded-floor` (same preconditions and lock; never touches the history). |
| A lowered floor (a higher floor replaced by a lower one) | `ContentAcceptanceRuleFloorBelowRequired` (with `RequiredFloor` set) | Treat as an incident; re-apply the floor in force. |

**While the floor is missing, pre-CARV builds can write again under their old rules (R-CARV-1 reopens)**; CARV builds still refuse operations prepared under other rules. Hence, as a mandatory Release B requirement (§2a, RB-CARV-1): after any restore to a pre-floor state, Authority writes must not resume until the floor is safely reinstated and every instance reports `floor 1`. Do not rely on readiness alone to stop writes; it is a signal, not a write barrier (§2a). No path lowers or removes a floor. See `trust_authority/DISASTER_RECOVERY_OPERATOR_RUNBOOK.md` (quarterly checklist and STOP conditions).

## 7. Business behaviour

The business recovers from `409 OPERATION_RULES_OUTDATED` with a fresh operation, never by retrying the refused one. `retryable: false` applies to the **operation** (it can never be re-sent into acceptance); the **business action** may be performed again as a new Prepare under a new idempotency key (contract §20.9, R-CARV-15):
- record creation: step 1 treats it like `OPERATION_EXPIRED`, step 2 like `IDEMPOTENCY_CONFLICT`, so step 3's fresh retry key follows;
- field protection migration: the item stays Pending; the next attempt number is a fresh operation;
- every other write path returns it to the caller as a `409` problem (`retryable: false`, meaning the refused operation itself can never be retried); the caller re-submits as a new request with a new idempotency key, which prepares a fresh operation. Re-sending the same idempotency key is refused (`IDEMPOTENCY_CONFLICT` or `OPERATION_STATE_INVALID`), never accepted.
