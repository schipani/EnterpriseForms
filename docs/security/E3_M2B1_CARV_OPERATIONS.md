# E3-M2b-1 CARV — Operations and Deployment Runbook

**Status (2026-11-15):** E3-M2b-1 accepted by the owner for Development/Testing (owner-led review; contract §20.9) and committed locally. E3-M2b-1F (the floor) implemented and accepted by the owner for Development/Testing (owner-led review; contract §21.10) and committed locally. Release B is not activated; mandatory gate RB-CARV-1 (§2a) is open. The RB-CARV-1 hardening (the Release A / Release B policy and the transactional write fence, §2b; contract §22) is implemented and conditionally accepted by the owner for Development/Testing (owner-led review; contract §22.13) and committed locally; it is not production-closed (deployment protection of the configuration, Azure compatibility, privileged schema mutation and performance remain open). Not deployed anywhere. Not an independent security review, not a certification. The normative contract is `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §19 (design) and §20 (implementation record).

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

0. **Every Authority deployment declares its phase** (RB-CARV-1, §2b). There is no default: `appsettings.json` carries `ContentAcceptance:EnforcementPhase = Unconfigured`, and a host without a valid phase refuses to start. Until step 4 is complete set `ContentAcceptance__EnforcementPhase=ReleaseA` and no `ContentAcceptance__RequiredFloor`. (`appsettings.Development.json` and the local Docker stacks declare `ReleaseA`.)
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
   - **After it:** confirm floor 1 (exit 0; readiness `floor 1`), then deploy the **Release B configuration** to every Authority instance through protected deployment configuration (mandatory, §2a): `ContentAcceptance__EnforcementPhase=ReleaseB` and `ContentAcceptance__RequiredFloor=1`. From then on every content write of those instances verifies the floor in its own transaction and is refused without it (§2b); readiness also reports a missing or lowered floor (§3, §6). Do **not** deploy the Release B configuration before activation: a Release B instance refuses every content write until floor 1 is installed (fail closed).
5. **Never run `VALIDATE CONSTRAINT`** on a floor (D-4): historical rows are permanently unstamped.

## 2a. Mandatory Release B gate RB-CARV-1 (open)

A Release B deployment is not permitted until all of the following hold (contract §21.10). This is a release requirement, **not** a claim that the current implementation satisfies it or has been proven in any deployment configuration.

1. **Protected configuration.** `ContentAcceptance:RequiredFloor = 1` is enforced through protected deployment configuration on every Authority instance after floor activation. Business deployers and the Authority runtime cannot remove or lower it (PB-11 separately permissioned Authority pipeline), and changes are audited. A Release B deployment without the setting fails the release gate (PB-12 evidence).
2. **Fail closed.** A Release B Authority instance refuses Authority writes when its required floor is absent, lowered, or cannot be verified.
3. **Restore.** After a restore of AuthorityDb to a pre-floor state, Authority writes do not resume until the floor has been reinstated with `--apply-content-acceptance-floor 1` and every instance reports `floor 1` (§6).
4. **Evidence.** Executed Azure staging tests for items 1–3, linked through the PB-12 chain.

**State at the E3-M2b-1F commit (kept for the record):** detection only; write paths did not consult it, and an absent `RequiredFloor` silently meant Release A.

**Current state (RB-CARV-1 implementation, Development/Testing, contract §22):**

| Item | Status |
|---|---|
| 1. Protected configuration | The application side is implemented: the phase is mandatory, strictly validated, read once at startup, never taken from the database, and an invalid or missing value refuses the start (and every content write). **The deployment protection of the setting is not implemented** (no Authority pipeline or Bicep in the repository; PB-11) |
| 2. Fail closed | Implemented and tested on real PostgreSQL: a Release B instance verifies the floor and the E3-M2b-1 fence in every content write transaction and refuses without it, independently of readiness (§2b) |
| 3. Restore | Implemented and tested: after a pre-floor restore the Release B instance refuses writes until the floor is re-applied. A legacy binary or a Release A-configured instance is stopped only by the floor itself (R-CARV-21) |
| 4. Azure staging evidence | None |

## 2b. Release B content write fence (RB-CARV-1)

| Setting | Release A | Release B |
|---|---|---|
| `ContentAcceptance__EnforcementPhase` | `ReleaseA` | `ReleaseB` |
| `ContentAcceptance__RequiredFloor` | absent (a value is refused) | `1` (anything else is refused) |

Values are matched exactly (no trimming, case-sensitive). A missing, blank, `Unconfigured` or unknown phase, a missing or malformed floor, a floor below 1 (a downgrade) or above this build's floor stops the host at startup (log event 9214) and refuses every content write of any store built with it. The resolved policy is logged at startup (event 9213, for example `phase=ReleaseB requiredFloor=1`). The values are read once; changing them requires a redeployment through the protected Authority pipeline (PB-11), and moving a deployment from Release B back to Release A is not an operational action: it needs an approved change, because the application cannot tell it from a legitimate Release A deployment (R-CARV-22).

**What a Release B instance does on every content write** (Prepare, Prepare replay, Commit, Commit replay, record registration, and any other write of content-acceptance state): it locks `content_change_operations` in ROW EXCLUSIVE mode, then verifies in the same transaction that floor 1 and the E3-M2b-1 fence are installed exactly as the migrations created them. The lock stays until the transaction ends, so a privileged DDL change cannot slip in between the check and the write. On failure the caller receives `503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (not retryable), nothing is written, and the alert below is raised. Aborts and quarantines still commit (they never accept content).

| Alert reason | Category | Operator action |
|---|---|---|
| `FloorNotInstalled` | MissingFloor | Restore to before the floor, or activation not yet done: keep the organization gates closed; `--apply-content-acceptance-floor 1` (§6) |
| `FloorRecordedButMissing` | MissingFloor | Privileged drop or partial restore: incident; then `--apply-content-acceptance-floor 1 --reinstate-recorded-floor` |
| `FloorLowered` | LoweredFloor | Incident; re-apply the floor in force |
| `FloorDefinitionMismatch`, `FenceCommitCheckMismatch`, `FencePositiveCheckMismatch`, `FenceStateGuardMismatch`, `FenceStampDefaultPresent`, `FenceUnexpectedTableObject` | WeakenedFence | Incident (privileged change). Restore the exact objects from the migrations (for the floor: drop the altered constraint, then reinstate with the command); never repair by hand-written variants |
| `FloorNotRecorded`, `FenceMigrationNotRecorded` | InconsistentMigrationHistory | Incident; restore the migration history from a trusted source |
| `FloorUnverifiable` | UnverifiableFloor | AuthorityDb reachability, the runtime role's read privileges on `authority.ef_migrations_history` and the catalogs, lock waits |
| `ConfigurationPolicyInvalid` | ConfigurationPolicyFailure | Correct the deployment configuration (the host should not have started) |
| `FloorAboveBuild` | BuildBelowFloor | Deploy a build whose rule version is at least the floor |

The expected definitions are PostgreSQL 18.0's renderings; if a server renders them differently, Release B writes are refused (`FloorDefinitionMismatch` or another WeakenedFence reason) until the build's constants are reviewed: verify on the target Azure PostgreSQL version before Release B (R-CARV-23).

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
| `ContentAcceptancePolicyInvalid(<reason>)` (RB-CARV-1) | the instance's phase configuration is invalid (§2b) | Correct the deployment configuration |
| `ContentAcceptanceFloorNotEnforced(reason=…, category=…)` (RB-CARV-1, Release B only) | the write fence verdict: the same check every Release B content write performs (§2b) | As the alert reason in §2b |

A ready Release B instance reports `content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1`; Release A details are unchanged. Readiness is operational hygiene. **The database refuses every crossing write whatever readiness says, and a Release B instance refuses every content write without a verified floor whatever readiness says** (§2b); enforcement never depends on the 30-second interval or the 10-minute operation lifetime.

## 4. Telemetry and alerts

| Signal | Source | Meaning |
|---|---|---|
| Security alert `CONTENT_ACCEPTANCE_RULE_REFUSED_BY_DATABASE` (event id 9100, Critical) | Authority store adapter | The database fence refused a write of this CARV build (a newer floor, or a mixed fleet). The caller received `409 OPERATION_RULES_OUTDATED`. |
| Log event 9211 (Critical) | Readiness guard | The instance turned not ready (detail as in §3) |
| Log events 9210 / 9212 | Readiness guard | Ready again / the fence could not be read |
| Authority `503 AUTHORITY_NOT_READY` during a rollout | Pre-CARV hosts | Probably a CARV-prepared operation reaching a legacy Commit (R-CARV-2); alert on Authority 503 rates during rollouts |
| `operation_quarantine_reason_code = 'OPERATION_RULES_OUTDATED'` on Aborted operations | AuthorityDb | Operations prepared under other rules and refused by a CARV Commit (expected during a rollout; should stop after step 3) |
| Security alert `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (Critical; RB-CARV-1) | Authority store | A Release B instance refused a content write: `reason`, `category`, `recovery`, policy and build in the summary (§2b). The caller received `503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` |
| Log event 9213 (Information) / 9214 (Critical) | Startup policy guard | The resolved phase and floor / an invalid policy (the host stops) |
| Authority `503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` responses | Release B hosts | Content writes are suspended until the floor is restored; alert on any occurrence |

**Open before deployment (R-CARV-12):** whether mandatory Authority audit policy requires a durable audit-stream event in addition to these operational alerts (RB-CARV-1 does not answer it).

## 5. Rollback

| When | Allowed | Effect |
|---|---|---|
| After step 1 or 2, before step 4 | Redeploy the E3-M2a binary | Works (it maps neither column). CARV-prepared in-flight operations fail at its Commit (503) and expire. |
| After step 4 | Binary rollback below CARV v1 | **Impossible by design**: every legacy Prepare is refused. Roll forward only. |
| M2b-1 Down | Development/Testing only | Refused while any CARV-prepared operation is Prepared, or once any floor exists |
| Floor migration Down | Never | Its Down raises `floors are never lowered` in every environment; there is no removal command |
| Legacy binary after the floor | Never | Every Prepare and Commit of a pre-CARV build is refused by the database; the legacy host reports `503 AUTHORITY_NOT_READY`. Roll forward only |
| Floor Down | Never | — |
| Release B configuration back to Release A | Not an operational action | Needs an approved change through the protected Authority pipeline: the application cannot distinguish it from a legitimate Release A deployment, and a Release A instance does not verify the floor (R-CARV-22) |

## 6. Recovery (R-CARV-4)

| Situation | Detection | Operator action before reopening Authority writes |
|---|---|---|
| Floor activation interrupted (lock timeout, killed process or backend, error) | Exit code 3; no `ck_content_operation_rule_floor_v1`, no history row | Nothing to undo (one transaction). Fix the cause (long transaction, in-flight pre-CARV operation, unmarked session) and re-run the command. |
| A restore of AuthorityDb to a point before the floor (constraint and history row gone) | Release B instances refuse every content write (`FloorNotInstalled`) and report `ContentAcceptanceRuleFloorBelowRequired`. The database alone cannot tell (it looks like Release A): the Release B configuration, held outside the database, is what detects it | Keep the organization gate closed; re-run `--apply-content-acceptance-floor 1`; confirm every instance reports `floor 1` and no `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` alerts follow. Restored `Prepared` operations are past their lifetime or refused by CARV. |
| The floor constraint missing while its history row remains (a privileged drop, a partial restore) | Release B writes refused (`FloorRecordedButMissing`); readiness `ContentAcceptanceRuleFloorMissing`; the command exits 4 | Investigate the privileged change (incident). Then `--apply-content-acceptance-floor 1 --reinstate-recorded-floor` (same preconditions and lock; never touches the history). |
| A lowered floor (a higher floor replaced by a lower one) | Release B writes refused (`FloorLowered`); `ContentAcceptanceRuleFloorBelowRequired` | Treat as an incident; re-apply the floor in force. |
| The floor or the E3-M2b-1 fence replaced, disabled or weakened | Release B writes refused (a WeakenedFence reason, §2b); readiness `ContentAcceptanceFloorNotEnforced` | Incident; restore the exact migration objects (for the floor: drop the altered constraint, then reinstate with the command) |

**While the floor is missing, pre-CARV builds can write again under their old rules (R-CARV-1 reopens)**, and so can an instance configured as Release A; Release B instances refuse every content write themselves (RB-CARV-1, §2b) and CARV builds still refuse operations prepared under other rules. Hence, as a mandatory Release B requirement (§2a): after any restore to a pre-floor state, Authority writes must not resume until the floor is safely reinstated and every instance reports `floor 1`; keep pre-CARV binaries and Release A configurations out of the fleet (R-CARV-21). The floor command does not pass through the write fence, so recovery is never blocked by it. No path lowers or removes a floor. See `trust_authority/DISASTER_RECOVERY_OPERATOR_RUNBOOK.md` (quarterly checklist and STOP conditions).

## 7. Business behaviour

The business recovers from `409 OPERATION_RULES_OUTDATED` with a fresh operation, never by retrying the refused one. `retryable: false` applies to the **operation** (it can never be re-sent into acceptance); the **business action** may be performed again as a new Prepare under a new idempotency key (contract §20.9, R-CARV-15):
- record creation: step 1 treats it like `OPERATION_EXPIRED`, step 2 like `IDEMPOTENCY_CONFLICT`, so step 3's fresh retry key follows;
- field protection migration: the item stays Pending; the next attempt number is a fresh operation;
- every other write path returns it to the caller as a `409` problem (`retryable: false`, meaning the refused operation itself can never be retried); the caller re-submits as a new request with a new idempotency key, which prepares a fresh operation. Re-sending the same idempotency key is refused (`IDEMPOTENCY_CONFLICT` or `OPERATION_STATE_INVALID`), never accepted.
