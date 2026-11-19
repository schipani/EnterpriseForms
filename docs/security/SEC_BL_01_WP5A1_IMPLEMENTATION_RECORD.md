# SEC-BL-01 WP-5a.1 — Implementation Record: Sentinel Provisioning and Durable D+ Findings

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | WP-5a.1: correction of the two WP-5a deviations, sentinel initialization (A-2 SP-1..SP-4) and durable findings (A-2 DD-4). Contract correction **§22.8** of `SEC_BL_01_DR_CONTRACT_CLOSURE.md` |
| Authorization | Owner decision package "WP-5a.1: Sentinel Provisioning and Durable D+ Findings", 2026-11-17, dispositions 1–6. The implementer read the package, sent as the owner's message, as the explicit authorization it describes (as with the WP-4A and WP-5a packages). The previous WP-5a.1 proposal, which said not to treat it as authorization, was **not** executed. **The owner should confirm this reading** |
| Not done | WP-5b, G14, G08, WP-6/7/8, production infrastructure, CI workflow changes, Stage B, commit, push, merge, deployment, persistent data changes, closure of SEC-BL-01/PD-15/M2F3-43. The durable stop marker (DD-10) is a proposal only (§5) |
| Baseline | `main` at `a9b32de`. All uncommitted WP-0..WP-5a work is preserved: contract §1–§20 byte-identical (`4a8bd1b1…ef12e` reproduced), A-1 (§21) and A-2 §22.1–§22.7 texts unchanged |
| Result | Implemented and tested in Development/Testing. Owner acceptance pending. **Not committed** |
| SEC-BL-01 / PD-15 / M2F3-43 | **OPEN / OPEN / FAILED (not re-evaluated)** |
| Review | Owner-led; prepared by the implementing assistant. **Not an independent security review** |

---

## 1. Root causes and corrections

| Deviation | Root cause | Correction |
|---|---|---|
| Sentinel initialization | The WP-5a host bootstrap accepted "no objects other than platform-journal entries" as proof of a fresh archive. That is absence of evidence: a full wipe looks identical, so an established archive could be stamped with a new identity (SP-4) | The bootstrap is removed. An explicit `--journal-archive-provision` step runs only in disposable stacks, needs five independent pieces of new-instance evidence, and writes a provisioning record **outside** the archive first. Every other state is refused (§22.8 PV-1..PV-9) |
| Durable findings | DP-12 was made conditional on an optional store. Without it, a refusal was marked `VerificationFailed` with only an alert and a log line | The store is required wherever D+ can refuse (startup refusal otherwise). The finding must be confirmed before the row changes; otherwise the entry stays Pending with `DPLUS_FINDING_STORE_UNAVAILABLE` and the stream is held (§22.8 DD-8, DD-9) |

## 2. Files

| Area | File | Change |
|---|---|---|
| Removed | `src/authority/AuthorizationAuthority.Application/Features/Journal/ArchiveIdentityBootstrap.cs` | The WP-5a automatic bootstrap, with its hosted service and `Program.cs` registration |
| Provisioning | `.../Features/Journal/Reconciliation/ArchiveProvisioning.cs` | New: service, record codec, outcomes, reasons |
| Provisioning | `.../Common/Ports/IArchiveProvisioningStore.cs` | New port |
| Provisioning | `src/authority/AuthorizationAuthority.Api/Composition/ArchiveProvisioningCommand.cs` | New command |
| Delivery | `.../Features/Journal/ArchiveProcessor.cs` | Required durable finding; hold and retry; distinct alert and code |
| Ports | `IJournalVerificationReader.cs` | `CountJournalRowsAsync` |
| Ports | `ISecurityEventArchiveInventory.cs` | `SummarizeAsync`, replacing `HoldsOnlyPlatformJournalObjectsAsync` |
| Infrastructure | `.../Persistence/JournalVerificationReader.cs` | Row count (SELECT only) |
| Infrastructure | `.../Archive/BlobSecurityEventArchive.cs` | `SummarizeAsync` for Blob, in-memory and unavailable; in-memory `RemoveIdentityForTesting` |
| Infrastructure | `.../Archive/VerificationCheckpointStores.cs` | `AccountKeyFile`; Blob and in-memory provisioning stores |
| Host | `JournalVerificationComposition.cs` | Store requirement at registration and in `DPlusStartupValidation` (started before the work loop); key file; provisioning registration |
| Host | `Program.cs` | Provisioning command dispatch; validation registration |
| Host | `appsettings.Development.json` | In-memory store paired with the Development in-memory archive |
| Compose | `infrastructure/docker/compose.yaml` | `journal-verification-keygen` (create-only per-stack key); store always on; the Authority uses it via key file; `archive-provision` (profile, inactive by default) |
| Compose | `compose.security-test.yaml`, `compose.integration.yaml` | Provisioner enabled before the Authority |
| Compose | `compose.journal-verification.yaml` | Only enables the verifier loop now |
| Compose | `env.example` | Comment: the key is generated |
| Tests | `tests/authority/.../ArchiveProvisioningTests.cs` | New, 11 tests |
| Tests | `AuditDeliveryEnforcementTests.cs` | Bootstrap test and the old store test replaced; 5 new tests |
| Tests | `JournalVerificationWorld.cs` | Row count |
| Tests | `JournalVerificationPostgresIntegrationTests.cs` | 2 new; the WP-5a isolation test now passes a store |
| Tests | `JournalVerificationAzuriteIntegrationTests.cs` | Store helpers |
| Tests | `tests/features/.../DPlusFindingStoreStartupTests.cs` | New, 4 tests |
| Tests | `tests/architecture/.../JournalVerificationBoundaryTests.cs` | Rule rewritten; lists extended |
| Evidence | `docs/evidence/sec-bl-01-wp5a1/` | `pg-azurite`, `sec-p`, `regression` |
| Documentation | Contract §22.8 and status row; this record; backlog row; dated note in `SEC_BL_01_WP5A_IMPLEMENTATION_RECORD.md`; regenerated manifests | — |

## 3. Provisioning lifecycle

**Disposable stacks** (security-test, integration):

1. `journal-verification-keygen` writes a random 64-byte key, create-only, into the stack's `journal-verification-keys` volume.
2. `journal-verification-store` starts with that key.
3. `authority-migrate` runs.
4. `archive-provision` checks the new-instance evidence:
   - no provisioning record;
   - no sentinel;
   - no archive container;
   - zero security and platform journal rows;
   - an empty store.
5. It then writes the record and the sentinel with the same instance identifier.
6. The Authority starts only after this step succeeds.

A rerun on a running stack returns `AlreadyProvisioned`, executed on the live stack.

**Persistent development stack:**
- It has the store and the generated key (a named volume), but never runs the provisioner; the provisioner refuses there anyway (no disposable flag, journal rows present).
- Its sentinel needs an operator `--journal-baseline --create-sentinel` (disposition 4).
- Until then, D+ refuses audit delivery there (ARCHIVE_REGRESSION, recorded, not marked).

**Production:** nothing initializes archives; the command refuses outside Development/Testing.

| State | Result (tested) |
|---|---|
| Fresh archive | `Provisioned`; rerun `AlreadyProvisioned` |
| Platform-journal rows before initialization | `JOURNAL_ACTIVITY_PRESENT` |
| Security-journal rows | `JOURNAL_ACTIVITY_PRESENT` |
| Existing archive without sentinel (organization or platform objects only) | `ARCHIVE_CONTAINER_EXISTS` |
| Sentinel but no record (baseline-established, or record lost) | `ARCHIVE_SENTINEL_WITHOUT_RECORD` |
| Sentinel-only, partial and full archive loss after activity | `ARCHIVE_LOST_AFTER_PROVISIONING`, never re-stamped |
| Record without sentinel before any activity | `PROVISIONING_INCOMPLETE` (ambiguous, fail closed) |
| Record lost while the store holds other objects | `VERIFICATION_STORE_NOT_FRESH` |
| Invalid record; mismatched sentinel | `PROVISIONING_RECORD_INVALID`; `ARCHIVE_IDENTITY_MISMATCH` |
| Concurrent provisioning (20 rounds × 4) | Exactly one `Provisioned`, one identity; losers converge or fail closed |
| Archive, store or listing unavailable | `Unavailable`; nothing written |

## 4. Durable finding and stream-stop behaviour

| Case | Behaviour (tested) |
|---|---|
| Store missing at startup (Development/Testing with an archive) | Startup refused, at registration and in `DPlusStartupValidation` before the work loop |
| Store missing at startup: Blob archive with an in-memory store | Startup refused (not durable) |
| Store missing at startup: unreadable key file | Startup refused |
| No store composed at runtime (production shape) | Entry Pending, `DPLUS_FINDING_STORE_UNAVAILABLE`; two distinct Critical alerts (the corruption, `JournalDeliveryRefused`, and the store, `JournalDeliveryFindingStoreUnavailable`); stream held; nothing marked |
| Store outage during delivery | Same. While held, the archive evidence is restored by an attacker, yet nothing is delivered; each pass retries the **original** finding |
| Store recovery | The original finding is stored once (deterministic identity, evidence reference `row:{id}`, exact code). The original revision is then marked `VerificationFailed`. Later entries stay held behind it |
| Idempotency | Retries, J1 row resets and a second processor produce one stored finding |
| Restart with the failure recorded | Stays stopped (row state plus archive) |
| Restart with the failure unrecorded, evidence unchanged | Refused again; recorded once the store is back |
| **Restart with the failure unrecorded, evidence replaced** | **Delivery resumes**, the expected gap DD-10, pinned by `ExpectedGapAnUnrecordedRefusalIsForgottenAcrossARestartWhenTheArchiveEvidenceWasReplaced` |
| Isolation and quarantine | Authorization and content are unaffected; no quarantine in any case |

## 5. Proposed durable stop marker (owner decision, OI-DR-12; not implemented)

The package said to stop and report if preserving an unresolved failure across restarts needs a new durable marker or state-machine contract. It does, so here are the options.

| Option | Design | Protects against | Cost |
|---|---|---|---|
| **M1:** a delivery state `Held` | A migration extends `ck_journal_archive_state` with `Held`. D+ writes `Held` when the finding cannot be stored, never delivers a Held row or anything after it, retries the finding, and moves `Held → VerificationFailed` once stored | Restarts. Not J1 (the runtime role can reset row state, until M-6 makes `Held` terminal) | A migration and a state-machine contract change; no grant change (the table UPDATE grant exists) |
| **M2:** a create-only stop marker in the archive | On a confirmed refusal with no stored finding, D+ writes `meta/dplus-stops/{org}/{stream}/{revision}.json` (create-only) to the archive. A confirmed failure implies the archive was readable; if the write fails, the stream is still held for the process. Every pass reads the marker before D+ and holds while it exists. Recording the finding later does not delete the marker (append-only); its successor finding references it | Restarts **and** J1 (outside AuthorityDb). Not J4/J5, who can delete it like any archive object | Contract change: AE-3 must exclude the reserved prefix by exact match, and the verifier must report markers |

Recommendation: **M2**. It survives restarts without trusting AuthorityDb state, needs no migration, and keeps D+ archive-derived (DP-9). It needs an owner-approved amendment and its own tests.

## 6. Evidence and results

| Command | Result | Evidence |
|---|---|---|
| Focused: `AuthorizationAuthority.Tests.exe --filter-class ...ArchiveProvisioningTests --filter-class ...AuditDeliveryEnforcementTests` | **39/39** | — |
| Contract `--filter-class EnterpriseForms.Api.ContractTests.DPlusFindingStoreStartupTests` | 4/4. The first attempt failed 3/4: `WebApplicationFactory` applies configuration overrides after registration. This led to the start-up validator, which re-checks against the final configuration | — |
| `powershell -File scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -PerformanceRows 2000 -CheckpointCount 288 -EvidenceDirectory docs/evidence/sec-bl-01-wp5a1/pg-azurite` | First run **25/26**: the WP-5a isolation test ran D+ without a store, so the refused row was now correctly held Pending; the test was given a store. Second run 26/26. After a one-line test fix (a missing `Require()` guard), **final run 26/26**, exit 0, containers removed | `pg-azurite/` |
| Security-test stack `up --build --wait` (fresh project) | First attempt: the provisioner failed closed (key file not found), because a tmpfs-driver named volume is mounted as a separate tmpfs per container. Overlays changed to the base named volume, removed by `down -v`. Second attempt: keygen generated the key; `archive provisioning Provisioned`; all services healthy | `sec-p/sectest-up.txt`, `sec-p/stack-state.txt` |
| `dotnet test --project tests/security/EnterpriseForms.SecurityTests/... --no-build -c Release -- --minimum-expected-tests 13` | **13/13**, SEC-P01..SEC-P12 including SEC-P12. Afterwards: all journal entries Anchored (audit 15, authorization 59, content 25), 0 of 15 organizations quarantined, the provisioner rerun `AlreadyProvisioned`; `down -v`, nothing left | `sec-p/secp.txt` |
| Authority suite (`--no-incremental`) | **1467 total, 1424 passed, 0 failed, 43 skipped**. The first run had 1 failure, the missing `Require()` guard, fixed | `regression/authority.txt` |
| Architecture suite | **224/224** | `regression/architecture.txt` |
| Contract suite | **958 total, 946 passed, 0 failed, 12 skipped** (+4 startup tests) | `regression/contract.txt` |
| CI compose renderings (`docker compose -f compose.yaml -f {dev,integration,security-test} --env-file env.example config --quiet`) | All exit 0 (CI workflows unchanged) | — |

**PostgreSQL 18 + Azurite cases added by WP-5a.1** (the other 24 gate tests from WP-3..WP-5a also passed):
- **Provisioning on a newly created database:** fresh, then provisioned; rerun idempotent; after journal activity and deletion of the whole archive container, `ARCHIVE_LOST_AFTER_PROVISIONING`; on the shared database with rows, `JOURNAL_ACTIVITY_PRESENT`; with an existing empty container, `ARCHIVE_CONTAINER_EXISTS`.
- **Unreachable finding store:** the row stays Pending with `DPLUS_FINDING_STORE_UNAVAILABLE`. With the real Blob store, it becomes `VerificationFailed` with exactly one create-only finding blob. No quarantine.

**Existing data (disposition 5).** Docker metadata only; no data was read or changed:
- `enterpriseforms-dev` containers last ran 12 days ago.
- Its `enterpriseforms/authority:dev` image dates from 2026-11-01, before SEC-BL-01.
- The `fd01` and retired `enterpriseforms` projects are older and stopped.
- WP-5a code ran only in the disposable security-test project.

No migration or repair is required.

## 7. Remaining security limitations

| ID | Limitation |
|---|---|
| R-W5A1-1 | DD-10: an unrecorded refusal is held for the process lifetime only; after a restart, replaced evidence can resume delivery (expected-gap test). Proposal in §5 |
| R-W5A1-2 | PV-9: the provisioning evidence is configuration-separated, not independent. An actor controlling both stores and AuthorityDb, and erasing all of them, is indistinguishable from a new stack |
| R-W5A1-3 | The generated store key sits in a Docker volume of the stack (persistent named volume in development; removed with `down -v` in disposable stacks). It is readable by the keygen, store, Authority and provisioner containers |
| R-W5A1-4 | **Persistent development stack:** after rebuilding with this code, its Authority needs the new store (provided automatically by compose). Its audit delivery is refused until an operator baseline creates the sentinel (disposition 4). Nothing was run there |
| R-W5A1-5 | Stage B was not run. Its stacks use the security-test overlay, so they get the provisioner and the store; **not verified** |
| R-W5A1-6 | Production has no finding store (WP-8); any D+ refusal there would hold the audit stream |
| R-W5A1-7 | Observation, not investigated: `compose.security-test.yaml` describes its tmpfs `api-keyring` volume as "shared by the two API containers". The WP-5a.1 finding (a tmpfs-driver volume is a separate tmpfs per container) suggests that claim may not hold |
| — | Unchanged: CF-1 (until P2), no recovery for a stopped stream (G14), R-W4-1, R-DR-1/2/4 |

## 8. Open owner decisions

- Confirm the authorization reading.
- OI-DR-12: durable stop marker, M1 or M2 (§5).
- Operator baseline on the persistent dev stack.
- Whether to authorize a Stage B run (R-W5A1-5).
- R-W5A1-7 triage.
- Earlier items unchanged: OI-DR-10 follow-up, G14 recovery for a stopped stream, the production finding store (WP-8), P2.

**Dated note (2026-11-18, WP-5a.2):** the owner accepted WP-5a.1 for Development/Testing and selected M2 for OI-DR-12, with disposition W1. A-3 (contract §23) replaces the process-lifetime hold with a durable stop outside the accepted residual window; DD-10 is narrowed, not closed (`SEC_BL_01_WP5A2_IMPLEMENTATION_RECORD.md`). Several tests listed above were replaced or re-expected under A-3. The text and results above are preserved as executed.

## 9. Not claimed

No durable hold across restarts; no independence of stores or provisioning evidence; no production behaviour change beyond D+'s fail-closed hold; no Stage B result; no closure of SEC-BL-01, PD-15, M2F3-43 or CF-1; no independent review.
