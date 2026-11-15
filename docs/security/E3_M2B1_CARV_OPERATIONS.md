# E3-M2b-1 CARV — Operations and Deployment Runbook

**Status (2026-11-14):** Development/Testing implementation, accepted by the owner for Development/Testing (owner-led review; contract §20.9). Not deployed anywhere. Not an independent security review, not a certification. The normative contract is `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §19 (design) and §20 (implementation record).

**Scope.** The content acceptance rule version (CARV) fence on `authority.content_change_operations`: migration `20261115014221_AuthorityContentAcceptanceRuleVersion` (M2b-1) and the later, separate floor migration (M2b-1F, **not implemented, not authorized**).

## 1. What the fence guarantees, and what it does not

| Guarantee | Mechanism |
|---|---|
| A CARV build never commits an operation prepared under other rules (an older build's NULL stamp included) | Application rule at Commit (abort, `409 OPERATION_RULES_OUTDATED`) **and** the database commit CHECK (*C* = *P*, or both NULL) |
| A pre-CARV build never commits an operation a CARV build prepared | Database commit CHECK (the legacy build writes no commit stamp) |
| No direct insert as Committed, no change of a terminal operation, no change of the Prepare stamp | Database state-guard trigger `trg_content_operation_rule_state_guard` |
| A refused write advances nothing | One transaction per Prepare/Commit; the database refusal rolls back the head, manifest, leaves, identities, index, journal and checkpoint |

**Not guaranteed before the floor (M2b-1F):** a pre-CARV instance that is still running keeps committing *its own* operations under its own (older) rules (R-CARV-1). The floor closes this, in a later release.

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
4. **Apply M2b-1F in a separate, later release** (OD-M2-21/28). Never ship it together with M2b-1: EF `Migrate()` applies every pending migration at once and would fence out pre-CARV instances before step 2 completes. M2b-1F needs its own owner approval.
5. **Never run `VALIDATE CONSTRAINT`** on a floor (D-4): historical rows are permanently unstamped.

## 3. Readiness guard

Every CARV build evaluates the fence at startup and every 30 s (OD-M2-30) and reports **not ready** if:

| Detail | Meaning | Action |
|---|---|---|
| `ContentAcceptanceRuleMigrationMissing` | M2b-1 is not in `authority.ef_migrations_history` | Apply step 1 |
| `ContentAcceptanceRuleSchemaIncomplete` | a stamp column or the commit CHECK is missing | Investigate (privileged change); do not route traffic |
| `ContentAcceptanceRuleStateGuardMissing` | the state-guard trigger is missing or disabled | Investigate (privileged change); do not route traffic |
| `ContentAcceptanceRuleFloorAboveBuild(floor=F, build=V)` | a newer floor overtook this build | Deploy a build with rule version ≥ F |
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
| Floor Down | Never | — |

## 6. Recovery (R-CARV-4)

A database restore to a point before a floor migration removes that floor. **Before reopening the organization gate after any AuthorityDb restore**, the G14 recovery must re-apply every CARV floor migration that was in force at the incident, and confirm with the readiness guard (`floor F` reported, `F` = the floor in force) on every instance. Restored `Prepared` operations are past their lifetime or refused by CARV. See `trust_authority/DISASTER_RECOVERY_OPERATOR_RUNBOOK.md` (quarterly checklist and STOP conditions).

## 7. Business behaviour

The business recovers from `409 OPERATION_RULES_OUTDATED` with a fresh operation, never by retrying the refused one. `retryable: false` applies to the **operation** (it can never be re-sent into acceptance); the **business action** may be performed again as a new Prepare under a new idempotency key (contract §20.9, R-CARV-15):
- record creation: step 1 treats it like `OPERATION_EXPIRED`, step 2 like `IDEMPOTENCY_CONFLICT`, so step 3's fresh retry key follows;
- field protection migration: the item stays Pending; the next attempt number is a fresh operation;
- every other write path returns it to the caller as a `409` problem (`retryable: false`, meaning the refused operation itself can never be retried); the caller re-submits as a new request with a new idempotency key, which prepares a fresh operation. Re-sending the same idempotency key is refused (`IDEMPOTENCY_CONFLICT` or `OPERATION_STATE_INVALID`), never accepted.
