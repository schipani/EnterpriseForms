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

**What a Release B instance does on every content write** (Prepare, Prepare replay, Commit, Commit replay, record registration, and any other write of content-acceptance state): it locks `content_change_operations` in ROW EXCLUSIVE mode, then verifies in the same transaction that floor 1 and the E3-M2b-1 fence are installed exactly as the migrations created them. *(Since amendment A-1, 2026-11-16, contract §22.14: it first locks `ef_migrations_history` in ACCESS SHARE mode, the same order EF migrations use, so a floor raise or a migration waits for it instead of deadlocking with it.)* The lock stays until the transaction ends, so a privileged DDL change cannot slip in between the check and the write. On failure the caller receives `503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (not retryable), nothing is written, and the alert below is raised. Aborts and quarantines still commit (they never accept content).

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

## 8. E3-M2d: CARV 2 and floor 2 (Development/Testing only)

Record: `docs/security/E3_M2D_IMPLEMENTATION_RECORD.md`. **Owner decision D-3: floor 2 is exercised only in disposable PostgreSQL. It is not installed in, and no Release B configuration is changed for, any persistent environment by E3-M2d.** The sequence below is the designed procedure, recorded so that a later, separately approved release can follow it.

| Step | What | Check |
|---|---|---|
| 1 | Deploy the version-2 build (CARV 2) to every instance. Floor 1 stays installed; a Release B configuration with `RequiredFloor = 1` keeps working (minimum semantics). Operations prepared by version-1 instances during the rollout are refused at a version-2 Commit (`409 OPERATION_RULES_OUTDATED`, R-CARV-7) and the reverse is refused by the version-1 rule and by the database (C = P) | readiness `content acceptance rules v2 (floor 1)` on every instance |
| 2 | Confirm no version-1 instance remains (deployment inventory; no session marked `carv-v1`) and no unexpired operation prepared below version 2 is in flight | the floor-2 command refuses otherwise (step 3) |
| 3 | `ConnectionStrings__AuthorityDbMigration=<migration identity> dotnet AuthorizationAuthority.Api.dll --apply-content-acceptance-floor 2` | exit 0; one constraint `ck_content_operation_rule_floor_v2` (floor 1 dropped in the same transaction); both floor migrations in the history |
| 4 | Only after step 3, raise the protected Release B configuration to `ContentAcceptance__RequiredFloor=2` (PB-11) | readiness `... (floor 2); phase=ReleaseB requiredFloor=2` |

- **Refusals (exit 2, nothing applied):** `--migrate` in the same invocation; a floor this build does not recognize (3); floor 2 before floor 1 (floors are raised one step at a time); floor 1 after floor 2 (never lowered). **Refusals (exit 3, nothing applied):** a connected session able to write operations without a `carv-v2` (or later) marker; an unexpired operation prepared below version 2; the 5 s lock timeout; any other database error. Re-running floor 2 after success is a no-op.
- **Exactly one floor at a time:** the command migrates the floor context to exactly the requested floor migration, so `--apply-content-acceptance-floor 1` never applies floor 2.
- **Release B fence with floor 2:** the required floor is a minimum. Refusal reasons extend as follows: an installed floor below the required one is `FloorLowered`; a recorded higher floor whose constraint is missing (for example floor 2 dropped, or floor 1 put back in its place) is `FloorRecordedButMissing` even when only floor 1 is required; two floor constraints, or a floor constraint whose definition is not exactly the recognized one, is `FloorDefinitionMismatch`; an installed floor whose migration (or a lower floor's migration) is not recorded is `FloorNotRecorded`.
- **Recovery:** after a restore to before floor 2, re-apply the floors in order (`1`, then `2`); after a privileged drop or substitution of floor 2 with its history row kept, `--apply-content-acceptance-floor 2 --reinstate-recorded-floor` (adds floor 2, drops a remaining floor 1; same lock and preconditions; never touches the history).

## 9. E3-M2e-1: CARV 3, the data-key ledger and floor 3 (Development/Testing only)

Contract: `docs/security/E3_M2E_CONTRACT_CLOSURE.md` (OD-M2e-01..16, AM-1..AM-12). Record: `docs/security/E3_M2E1_IMPLEMENTATION_RECORD.md`. **Dark increment: Envelope v2 acceptance stays off everywhere; floor 3 is exercised only in disposable PostgreSQL; no persistent database, Release B configuration or deployment is changed by E3-M2e-1. No persistent activation is authorized (closure §4.3, §5).** The sequence below is the designed procedure (closure §5.1), recorded so that a later, separately approved release can follow it.

| Step | What | Check |
|---|---|---|
| 1 | Apply the ordinary migration `AuthorityContentDataKeyBindings` (`--migrate`; it installs **no** floor) | the history records `20261116031706_AuthorityContentDataKeyBindings`; the ledger table, its unique index and the P-1/P-2 triggers exist; the runtime role has SELECT and INSERT only |
| 2 | Deploy the version-3 build (CARV 3) with `Content:EnvelopeV2AcceptanceEnabled` absent (off). A version-3 build refuses readiness without the ledger migration (`ContentDataKeyLedgerMigrationMissing`) | readiness `content acceptance rules v3 (floor …)`; connection marker `carv-v3`; startup log event 9241 `configured=False` |
| 3 | Floor 2 must already be installed **before any v2 binding or acceptance** (AM-6; C-14: CARV-1 builds have no downgrade guard) | one constraint `ck_content_operation_rule_floor_v2` |
| 4 | Confirm no CARV ≤ 2 instance remains (no session marked `carv-v1`/`carv-v2`) and no unexpired operation prepared below version 3 is in flight | the floor-3 command refuses otherwise (step 5) |
| 5 | `ConnectionStrings__AuthorityDbMigration=<migration identity> dotnet AuthorizationAuthority.Api.dll --apply-content-acceptance-floor 3` | exit 0; one constraint `ck_content_operation_rule_floor_v3` (floor 2 dropped in the same transaction); the three floor migrations in the history |
| 6 | Only after step 5, raise the protected Release B configuration to `ContentAcceptance__RequiredFloor=3` (PB-11) | readiness `... (floor 3); phase=ReleaseB requiredFloor=3` |
| 7 | Ledger and recovery prerequisites: the ledger verification (L-13) passes; for any persistent environment, BD-1 and BD-2 must be decided, implemented and verified first (closure §4) | not available in E3-M2e-1 |
| 8 | Enabling acceptance: only under a separate owner authorization (OD-M2e-16); refused at startup outside Development/Testing, without Release B `RequiredFloor = 3`, or without the ledger migration | not authorized |

- **Floor-3 refusals (exit 2, nothing applied):** `--migrate` in the same invocation; a floor this build does not recognize (4); floor 3 before floors 1 and 2; floor 2 or 1 after floor 3. **(exit 3, nothing applied):** a connected session able to write operations without a `carv-v3` (or later) marker; an unexpired operation prepared below version 3; the data-key ledger migration not recorded or its unique index / binding-guard / immutability triggers not exactly as created; the 5 s lock timeout; any other database error.
- **Release B fence with floor 3:** as in §8; `RequiredFloor` may be 1, 2 or 3 (a minimum); 4 refuses the start.
- **Data-key ledger refusals (callers):** `422 ENVELOPE_INVALID: DATA_KEY_IDENTIFIER_REUSED` (an identifier already bound anywhere, including a concurrent binding from another organization: the whole Prepare rolls back); `409 DATA_KEY_BINDING_MISMATCH` (DK-6 / P-4: the operation is aborted and an `IntegrityVerificationFailed` incident with reason `DATA_KEY_BINDING_MISMATCH` is journaled on the content stream); `503 CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED` (L-13: the ledger's index or triggers are not exactly as created; nothing is decided; alert `CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED`); `503 DATA_KEY_LEDGER_INVARIANT_VIOLATED` (the database binding guard or immutability trigger refused a write; alert `CONTENT_DATA_KEY_LEDGER_INVARIANT_VIOLATED`; treat as an incident).
- **Recovery:** after a restore to before floor 3, re-apply the floors in order; after a privileged drop or substitution of floor 3 with its history row kept, `--apply-content-acceptance-floor 3 --reinstate-recorded-floor` (adds floor 3, drops a remaining lower floor). The ledger itself has **no** recovery command in E3-M2e-1: after an AuthorityDb restore, bindings of later commits are missing and acceptance must not be enabled in any persistent environment until BD-1/BD-2 exist (closure §4). Archived `new_data_key_bindings` tuples alone do not prove the ledger complete.
- **Floor raises under load (F-E1, amendment A-1, contract §22.14).** A build with A-1 locks the migration history first in every Release B write. The floor command waits for in-flight writes at EF's migration lock for at most 5 s (session `lock_timeout`). If it times out it exits 3 and applies nothing: re-run it. Writes arriving meanwhile queue behind it.
  - **Raise a floor only when every Release B instance runs an A-1 build.** Builds without A-1 (including `8915f77` and `a1127a1`) can still deadlock with the raise. The deadlock fails closed: the writer gets `503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (`FloorUnverifiable`), or the command exits 3. The `carv-v` marker cannot tell these builds apart, so confirm this from the deployment inventory.
  - **Mandatory prerequisite (owner decision OA-FE1-2, 2026-11-16):** before **any persistent floor raise**, a **verified A-1-capable deployment inventory** must confirm the build of every Release B instance. CARV connection markers alone are insufficient evidence.
  - **Do not run the ordinary `--migrate` under live Release B writes.** It re-applies the runtime grants one statement at a time, so writes are refused meanwhile, and a committed write can be reported as failed. By source and unit tests, retrying the Commit of the same operation after the window returns the committed result (an idempotent replay); this has not been reproduced in PostgreSQL after an F-E2 misreport. F-E2 is open: `F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md` (mitigations in its §6).
- **Ledger Down (Development/Testing only):** refused while any binding exists; bindings are never deleted (P-2 refuses UPDATE, DELETE and TRUNCATE for every role the triggers fire for).

## 10. E3-M2f-2: business envelope capability floor (Development/Testing only)

Contract: `docs/security/E3_M2F_CONTRACT_CLOSURE.md` (OD-M2f-01..10, AM-F1, CAP-S/F/O/E, §9, §10). Record: `docs/security/E3_M2F2_IMPLEMENTATION_RECORD.md`. **No migration, table, trigger, CARV bump or floor (F0). No persistent database, Release B configuration or deployment is changed by E3-M2f-2, and Envelope v2 acceptance stays off everywhere. Persistent use additionally needs E3-M2f-2G evidence, E3-M2f-3 refusal evidence (RE-1..RE-6), the INV-1/INV-2 inventory and everything in closure §11.5.**

**What the Authority does.** At the seven content entry points (Prepare, Commit, Verify, batch Verify, protection census, search-index backfill, deep-sweep verify), right after the organization lock and (Prepare, Commit) the Release B fence, it reads the request header `EnterpriseForms-Envelope-Capability` and refuses with `409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT` (not retryable, fixed detail, nothing written) when the declared level is below `max(deployment floor, organization floor)`. Exactly one header instance whose value is exactly `1` or `2` is recognized; anything else, and no header, is level 1. The organization floor is 2 once the organization has any data-key binding (any state). Level 2 is never refused by this build and never reads the ledger. Every other Authority endpoint ignores the header.

**Deployment floor `Content:MinimumBusinessEnvelopeCapability`.**

| Value | Effect |
|---|---|
| absent | 1 (the organization floor alone decides) |
| `1` | 1 |
| `2` | every level-1 caller is refused in every organization, bound or not (the only restore-independent backstop, closure §4.4) |
| anything else (empty, padded, `02`, `0`, `3`, text) | the Authority refuses to start (startup log names the key, never the value) |

- **Governance (OD-M2f-10, PB-11):** protected deployment configuration only. Raising it is a tightening (one approver); lowering it is a loosening (two distinct approvers outside Development/Testing). In Development/Testing it may be set freely. Its monotonicity is procedural only: a configuration rollback or a redeployment with older settings lowers it, and nothing in the Authority can detect that (OPEN-M2f-2). An AuthorityDb restore does not change it.
- **Signals:** startup log event 9252 `Envelope capability floor enforced: deployment floor N`; readiness detail ends `envelope capability floor enforced; deployment floor N` (supporting evidence only, INV-2/INV-4); refusals log event 9250 (Warning) and store failures 9251 (Error), each with the organization identifier and parsed level only; meter `AuthorizationAuthority.EnvelopeCapability`, counter `envelope_capability_evaluations` (tags `outcome` = admitted/refused/unavailable, `client_level`, `organization_id`). These are operational signals, **not** the durable refusal evidence required before persistent use (OD-M2f-09, E3-M2f-3).

**Deployment order.**

| Step | What | Check |
|---|---|---|
| 1 | Business API and Worker instances on E3-M2f-1 or later (they declare level 2) | business build inventory; a level-1 straggler is refused only in bound organizations (availability, R-M2f-8) |
| 2 | Authority instances on an E3-M2f-2 build | INV-1/INV-2: deployed image digest mapped to a commit containing E3-M2f-2; `carv-v3` markers do **not** distinguish it from `0be5f52`/`6e0ee1d` (INV-3, R-M2f-3) |
| 3 | Optionally set the deployment floor to `2` (PB-11) | readiness and log event 9252 show `deployment floor 2` |

**Refusal handling.** The business build containing E3-M2f-2 treats the code as terminal: users get `503 SERVICE_UNAVAILABLE` without detail (behaviour-tested); a field-protection migration item becomes `Failed` with the code (no further attempt, so no fresh operation) and a protection reconciliation finishes `Incomplete` with the code (both pinned structurally by architecture tests and mutations; the workers were not run in E3-M2f-2); the search-index builder is unchanged and, by source analysis, skips the form or entry for the round and re-reads on its next scheduled round (nothing is written). E3-M2f-1 business builds without this mapping surface the 409 with the Authority's code. Remedy: deploy a current business build, or check that no intermediary strips the header. The header is self-asserted by the authenticated workload (R-M2f-6).

**Header transport note.** HTTP field values exclude surrounding optional whitespace (RFC 9110 §5.5), so a server may deliver a value sent as `2 ` as `2`. The Authority parser itself treats any padded value as level 1 (unit-tested); the transport-level trimming by Kestrel was **not** tested in E3-M2f-2. Inner whitespace, lists (`1, 2`), repeated header lines and any other value are level 1 (host-tested for lists and repeats).

**Rollback and restore.** Rolling the Authority back to an E3-M2f-1 build removes enforcement (nothing is persisted; R-M2f-3 applies). An AuthorityDb restore to before an organization's first binding lowers that organization's floor to 1 while BusinessDb may still hold its schema-2 rows (R-M2f-4, BD-1): only deployment floor `2` remains in force. Any future recovery path that re-inserts bindings must take the organization lock (FB-1 obligation on BD-2).

## 11. E3-M2f-3i: capability-refusal evidence index (Development/Testing only)

Contract: `E3_M2F3_CONTRACT_CLOSURE.md` §5A (OD-IX-1 approved with conditions); record `E3_M2F3I_IMPLEMENTATION_RECORD.md`. The index is a cost device for the future evidence lookup (E3-M2f-3b, not implemented). It confers no permission, changes no data and is not read by any code in this build. **Persistent rollout is not authorized (OD-IX-2 open).** *(2026-11-17: superseded in part. E3-M2f-3b is now implemented and its recorder reads this index (catalogue check and eight-row lookup, §12). E3-M2f-3i, -3b and -3G are accepted for Development/Testing only. Persistent rollout is still not authorized.)*

**Command and identity.**

```
dotnet AuthorizationAuthority.Api.dll --provision-capability-refusal-evidence-index              # provision (create if absent) and verify
dotnet AuthorizationAuthority.Api.dll --provision-capability-refusal-evidence-index --verify-only  # inspect only; never creates
```

- Identity: `ConnectionStrings__AuthorityDbMigration` only (the table owner or a member of it). There is no fallback to `ConnectionStrings__AuthorityDb`; provisioning with any identity that may not act as the table owner is refused (exit 7) before anything is attempted. `--verify-only` may run with any identity that can connect.
- It cannot be combined with `--migrate`, `--apply-content-acceptance-floor` or `--reinstate-recorded-floor`, and accepts no other option.
- It never runs EF migrations, never reads or locks `authority.ef_migrations_history`, never re-applies runtime grants (no F-E2 window), never locks an organization row, never drops, rebuilds, reindexes or validates anything, and writes no data.

**What it does, in order (one connection, autocommit; no explicit transaction).** `SET lock_timeout = '5s'`; `SET statement_timeout = 0`; session advisory lock `pg_try_advisory_lock(1161055538, 1177774385)` (a second concurrent invocation exits 10 at once); ownership check; catalogue inspection; only if the index is absent: a lock-free catalogue snapshot, the single statement

```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_journal_capability_refusal_evidence
    ON authority.security_journal_entries (organization_id, journal_stream_revision)
    WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused';
```

then a re-inspection and a second snapshot. Success requires the approved, valid index **and** a catalogue difference of exactly that index; `IF NOT EXISTS` alone is never taken as proof. One JSON report line goes to stdout (success) or stderr (failure); it carries no connection details or credentials.

**Expected catalogue state after success.** `authority.ix_journal_capability_refusal_evidence`, B-tree, non-unique, keys `(organization_id, journal_stream_revision)` ascending, `indisvalid`, `indisready`, `indislive` all true, owned by the table owner, no ACL, and `pg_get_indexdef` exactly:

```
CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries USING btree (organization_id, journal_stream_revision) WHERE (((journal_stream_name)::text = 'audit'::text) AND ((journal_event_kind)::text = 'EnvelopeCapabilityRefused'::text))
```

(PostgreSQL 18 rendering; Azure PostgreSQL rendering not yet verified, TQ-19.)

**Exit codes.**

| Code | Outcome | Meaning and operator action |
|---|---|---|
| 0 | `Created` / `AlreadyValid` | Done. Re-running is an idempotent success |
| 2 | usage | Wrong flags or no migration connection string; nothing attempted |
| 3 | `DatabaseFailure` | Connection, catalogue or unexpected database error; the report gives the SQLSTATE. Re-run with `--verify-only` to see the state |
| 4 | `IndexInvalid` | An invalid index of that name exists (an interrupted or failed build). **Not repaired automatically.** Manual recovery below |
| 5 | `DefinitionMismatch` | A relation of that name exists with another definition (the report lists every deviation). **Not changed.** Investigate how it was created; removal is a separately approved privileged change |
| 6 | `BuildInProgress` | Another session is building it (or holds the table's SHARE UPDATE EXCLUSIVE lock, for example VACUUM). Re-run after it finishes |
| 7 | `NotOwner` | The identity may not act as the table owner. Use the migration identity |
| 8 | `LockTimeout` | A lock wait exceeded 5 s. `state_after = Absent`: nothing was created (a conflicting table lock); re-run later. `state_after = Invalid`: the build was created but could not finish waiting for an older transaction; treat as exit 4 |
| 9 | `VerificationFailed` | The index is valid but the catalogue changed by more than the index during the run; investigate before relying on it |
| 10 | `Busy` | Another invocation holds the provisioning lock; nothing was inspected or changed |
| 11 | `Absent` | `--verify-only` only: the index does not exist |

**Preconditions.** The Authority schema is migrated (`authority.security_journal_entries` exists). No other DDL on the journal is running. For any persistent environment: **OD-IX-2 decision first** (recommended: quiesced Authority writers, then the command, then `--verify-only`). Long-running transactions in the database delay the build (it waits for them at its phase changes) and, after 5 s, end it with exit 8 and an invalid index; check `pg_stat_activity` for old transactions before starting.

**Manual recovery of an invalid index (exit 4, or exit 8 with `state_after = Invalid`).** Only after confirming that no build is running (`SELECT * FROM pg_stat_progress_create_index`):

```sql
DROP INDEX CONCURRENTLY authority.ix_journal_capability_refusal_evidence;
```

run with the migration identity outside a transaction, then re-run the command and `--verify-only`. In a persistent environment this DROP is a privileged change that needs its own approval. Never use `REINDEX` or catalogue edits to "fix" the index.

**Verification.** `--verify-only` (exit 0). Optionally, as any identity with SELECT on the journal, `EXPLAIN` of the approved lookup (closure IXP-6) shows `Limit` over `Index Scan Backward using ix_journal_capability_refusal_evidence`. A plan observed on one PostgreSQL server is not proof of the plan on another (Azure planner behaviour unverified).

**Rollback.** The index is harmless to every build (no build before E3-M2f-3b writes a matching row). Rolling back an Authority binary leaves it in place. Removing it is `DROP INDEX CONCURRENTLY` under a separate approval; once E3-M2f-3b exists, its absence makes evidence recording fail closed (`INDEX_UNAVAILABLE`) without affecting refusals. An AuthorityDb restore from a backup taken before the index existed removes it; re-run the command after such a restore.

**Disposable verification.** `scripts/dev/e3-m2f3i-index-pg-gate.ps1` (40 checks on throwaway PostgreSQL 18 databases; evidence `docs/evidence/e3-m2f3i/`).

## 12. E3-M2f-3b: capability-refusal evidence (Development/Testing only)

Contract: `E3_M2F3_CONTRACT_CLOSURE.md` §3–§9; record `E3_M2F3B_IMPLEMENTATION_RECORD.md`. **Prerequisite:** the E3-M2f-3i index (§11). Without it, every refusal is still refused exactly as before, but no evidence is recorded (`INDEX_UNAVAILABLE`). **Persistent use is not authorized** (OD-IX-2, owner acceptance of E3-M2f-3G, and every item of `E3_M2F_CONTRACT_CLOSURE.md` §11.5). *(2026-11-17: E3-M2f-3G executed on disposable PostgreSQL, `E3_M2F3G_VERIFICATION_RECORD.md`; operational notes in §13. Final closure the same day: E3-M2f-3i, -3b and -3G accepted for Development/Testing; persistent use remains blocked by OD-IX-2, PD-14 monitoring routes and `E3_M2F_CONTRACT_CLOSURE.md` §11.5.)*

**What happens on a capability refusal.** The refused request's transaction (T1) ends exactly as in E3-M2f-2 and the caller receives the same `409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`. Independently, after T1 has been disposed, the endpoint filter on the seven capability routes asks the recorder to record one `EnvelopeCapabilityRefused` entry on the organization's **audit stream**, in its own transaction (T2). At most one entry per organization, route (entry point) and AuthorityDb UTC hour; at most seven per organization per hour. The entry is delivered to the archive by the existing archive processor (pending, then anchored).

**Where to look.** AuthorityDb (privileged access only; there is no API):

```sql
SELECT journal_stream_revision, journal_created_on, archive_delivery_state, journal_event_body_json
  FROM authority.security_journal_entries
 WHERE organization_id = '<organization>' AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'
 ORDER BY journal_stream_revision DESC LIMIT 50;
```

The body holds `workload_name`, `entry_point`, `client_capability_level` (always 1), `evidence_database_time_utc` (authoritative AuthorityDb time), `evidence_bucket_start_utc` (its UTC hour) and `refusal_observed_on_utc` (the recording instance's clock; informational). It never holds a floor or its cause, binding data, correlation, request, record or user identifiers, headers, tokens, build or instance identifiers, content or keys.

**Operational signal (not evidence).** Counter `capability_refusal_evidence` on meter `AuthorizationAuthority.EnvelopeCapability`, tags `outcome`, `reason`, `entry_point`:

| Outcome | Meaning | Log |
|---|---|---|
| `Committed` | T2 wrote one entry (archive delivery pending) | none |
| `AlreadyRecorded` | T2's lookup found the key already recorded in the hour | none |
| `SuppressedRecorded` / `SuppressedInconclusive` / `SuppressedPending` / `SuppressedInFlight` | No T2: the key is known recorded, recently inconclusive, waiting for a retry, or being recorded by a concurrent request of this process | none |
| `Deferred` (reasons `INDEX_UNAVAILABLE`, `CLOCK_REGRESSION`, `TIMEOUT`, `ERROR`) | T2 reached no decision; a bounded in-memory retry is scheduled (5 s doubling to 5 min, given up after 1 h) | 9253 Warning |
| `Ambiguous` (reason `AMBIGUOUS_COMMIT`) | The commit outcome is unknown; the retry re-reads the journal before anything else (never appends twice) | 9253 Warning |
| `Inconclusive` (reasons `BOUND_INCONSISTENT`, `UNVERIFIABLE_BODY`, `TIME_ORDER_VIOLATION`) | The newest evidence cannot be trusted or contradicts the bound; nothing is recorded for that key until the end of the hour, and nothing is retried. **Investigate**: these indicate journal damage or tampering | 9254 Error |
| `QuotaSuppressed` (reason `ATTEMPT_LIMIT`) | The process used its 256 T2 attempts for the current hour; the key is not attempted again this hour (one signal per key per hour) | 9254 Error |
| `Abandoned` (reasons `ABANDONED`, `RETRY_CAPACITY`) | A retry was given up after 1 h, or the retry set (1,024 keys) was full | 9254 Error |
| `NotAttempted` | Not one of the seven approved keys (should never happen) | 9254 Error |

Log 9256 (Warning) reports an unexpected failure of the retry loop. Monitoring routes for 9253, 9254 and 9256 are a deployment prerequisite (PD-14), together with 9100 and 9250.

**What is not guaranteed (closure §4).** A refusal is not proof of evidence. Evidence is best effort: lost on a process crash before T2 commits, on retry abandonment, on quota exhaustion, while the index is missing, and on an AuthorityDb restore before archive delivery. There is no per-request, exactly-once or at-least-once guarantee before commit; after a commit, archive delivery is at least once while AuthorityDb is not restored.

**Restore.** After an AuthorityDb restore, the first audit append of a restored organization (an evidence entry or any other event) collides with the archive, and the archive processor quarantines that organization (`ARCHIVE_BODY_MISMATCH`). That is the existing G14 detection; recovery is the separately reviewed G14 process (not implemented). If the restored database predates the E3-M2f-3i index, re-run the index command; until then no evidence is recorded.

**Rollback.** Rolling the Authority back to an E3-M2f-3i build stops recording; nothing persisted needs removal (entries already written are ordinary, permanent audit evidence).

## 13. Capability-refusal evidence: disposable PostgreSQL verification gate (E3-M2f-3G)

Development/Testing only. Record: `E3_M2F3G_VERIFICATION_RECORD.md`. Never against a persistent database.

```
powershell -File scripts\dev\e3-m2f3g-evidence-pg-gate.ps1 -Evidence <folder> -RunLabel <label> [-Groups all|S,H,M,T,F,Q,L,W,G,B] [-BigRows 2000000]
powershell -File scripts\dev\e3-m2f3g-mutations.ps1 -Out <folder> [-LegacyBuildsRoot <work dir of an earlier gate run>] [-DryRun]
```

- **Duration.** About 8 minutes plus the wait for the next real hour boundary (group B; skipped as NOT EXECUTED when that boundary is more than `-BoundaryMaxWaitMinutes` away or under 3 minutes away). Start a run of record between about HH:15 and HH:45. Never run two gate runs at once (shared ephemeral ports and timing checks).
- **Statement traces.** The runtime role has `log_statement = 'none'` by product design (`AuthorityRuntimeGrants`); the gate never changes it. T2 statement shapes come from `auto_explain` (a database setting of the disposable copy) and an observing EF interceptor.
- **Archive delivery.** The host's work loop archives only after SpiceDB reports its schema ready. Without SpiceDB, evidence entries stay `Pending`; recording is unaffected (T-12). In an environment where SpiceDB is down, expect Pending evidence, not missing evidence.
- **Log classes observed on the real host.** 9253 for a T2 that could not complete (lock or statement bound, index unavailable) and is retried; the client's 409 is unchanged and immediate.
- **Cleanup.** Both containers are removed with their anonymous volumes (`docker rm -f -v`); the script fails if the container or volume list changed.

