# SEC-BL-01 WP-5a — Implementation Record: Audit-Stream D+ Delivery Enforcement

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | WP-5a: delivery-time chain enforcement D+ on the audit stream (`SEC_BL_01_DR_CONTRACT_CLOSURE.md` §5, phase P1; amendment **A-2**, §22) |
| Authorization | Owner package "WP-5a: Audit-Stream D+ Delivery Enforcement", 2026-11-17: accept WP-4A and A-1; adopt the OI-DR-9/-10/-11/-2 dispositions; authorize WP-5a and focused disposable security regression. The implementer read the package, sent as the owner's message, as the explicit approval it describes, as with the WP-4A package; **the owner should confirm**. "The stated dispositions" were read as the recommended dispositions already recorded: contract §19 for OI-DR-2, A-1 §21.6 for OI-DR-9 and OI-DR-11, and the package's own §5 for OI-DR-10 (A-2 §22.5) |
| Mid-increment decision | Sentinel provisioning: the owner chose "empty archive only" on 2026-11-17 (A-2 §22.2) |
| Not authorized and not done | Authorization/content D+, G14, G08 grants or triggers, WP-6/7/8, production infrastructure, Stage B, deployment, commit, push, merge, closure of SEC-BL-01/PD-15/M2F3-43, destructive operations on persistent data |
| Baseline | `main` at `a9b32de`; all uncommitted WP-0..WP-4A work preserved. Contract §1–§20 byte-identical (`4a8bd1b1…ef12e` reproduced); A-1 (§21) text unchanged |
| Result | Implemented and tested in Development/Testing. Owner acceptance pending. **Not committed** |
| SEC-BL-01 / PD-15 / M2F3-43 | **OPEN / OPEN / FAILED (not re-evaluated)** |
| Review | Owner-led; prepared by the implementing assistant. **Not an independent security review** |

---

## 1. Files

| Area | File | Change |
|---|---|---|
| D+ | `src/authority/AuthorizationAuthority.Application/Features/Journal/AuditDeliveryEnforcement.cs` | New: DP-2..DP-9 using `JournalChainRules`; streams fixed to `audit` |
| Delivery | `.../Features/Journal/ArchiveProcessor.cs` | The audit stream goes through D+ in its own batch, with refusal, finding, alert and stop handling. The authorization/content loop is the previous code, unchanged |
| Sentinel bootstrap | `.../Features/Journal/ArchiveIdentityBootstrap.cs` | New (A-2) |
| Ports | `.../Common/Ports/IAuthorityStore.cs` | `GetPendingJournalEntriesByStreamAsync`, with a default implementation for test doubles |
| Ports | `.../Common/Ports/ISecurityEventArchiveInventory.cs` | `HoldsOnlyPlatformJournalObjectsAsync` |
| Finding codes | `.../Features/Journal/Reconciliation/JournalVerificationFindings.cs` | `PREDECESSOR_MISSING`, `LINK_MISMATCH` |
| Store | `src/authority/AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs` | Per-stream pending query (SELECT only; no grant change) |
| Archive | `.../Infrastructure/Archive/BlobSecurityEventArchive.cs` | `HoldsOnlyPlatformJournalObjectsAsync` for Blob, in-memory and unavailable |
| Host | `src/authority/AuthorizationAuthority.Api/Composition/JournalVerificationComposition.cs` | `AddArchiveIdentityBootstrap` and its hosted service, Development/Testing only |
| Host | `.../Api/Program.cs` | One registration line, before the work loop |
| Tests | `tests/authority/.../AuditDeliveryEnforcementTests.cs` | New, 25 tests |
| Tests | `tests/authority/.../JournalVerificationPostgresIntegrationTests.cs` | 5 D+ cases on the real store and Blob archive |
| Test fixtures | `CapabilityRefusalEvidenceWorld.cs` | Genuine revision-40 predecessor, archive provisioning, fresh-stream option, per-stream query in the proxy |
| Test fixtures | `CapabilityRefusalEvidenceRecorderTests.cs`, `CapabilityRefusalEvidenceNoSummaryTests.cs`, `CapabilityRefusalEvidenceRemovalTests.cs`, `ArchiveDeliveryOutageCharacterizationTests.cs` | Archive provisioning before delivery; the outage double implements the inventory |
| Architecture | `tests/architecture/.../JournalVerificationBoundaryTests.cs` | One rule replaced, one added |
| Evidence | `docs/evidence/sec-bl-01-wp5a/` | `pg-azurite`, `sec-p`, `regression` |
| Documentation | Contract (§22, status row), this record, `SECURITY_BACKLOG.md` progress row, dated note in `SEC_BL_01_WP4A_IMPLEMENTATION_RECORD.md`, regenerated manifests | — |

The fixture changes only make the test archives look like a baselined archive: they add a sentinel and an archived genuine predecessor. The evidence world previously started its audit stream at revision 40 without a predecessor digest, which D+ correctly refuses (genesis rule R-5). No assertion of those tests was changed.

## 2. D+ enforcement

For each pending audit entry, in revision order, before `AppendAsync`:

1. **Existing R-1 check** of the row, with its existing quarantine response (DP-10, unchanged).
2. **The row's R-2..R-5:** envelope format, identity and columns, previous digest, genesis.
3. **Sentinel:** read once per pass. If absent or not canonical, the result is `ARCHIVE_REGRESSION`, which is not marked (DD-1).
4. **Revision 1:** delivered once the checks above pass (genesis).
5. **Revision greater than 1:** the predecessor is the bytes just appended in this pass, otherwise `archive[k − 1]`.
   - Absent: `PREDECESSOR_MISSING`.
   - Its own R-2/R-3/R-5 fail: `CHAIN_INTEGRITY_FAILURE` with the subtype.
   - R-7 fails: `LINK_MISMATCH`.
6. **Append** only after all of the above.

AuthorityDb receipts and delivery states are never used as evidence (DP-9). The rules come from WP-1's `JournalChainRules`, and the architecture tests check that `AuditDeliveryEnforcement` computes no digest itself.

## 3. Failure and recovery behaviour

| Case | Behaviour (tested) |
|---|---|
| Confirmed integrity failure | Finding object first (when a store is composed), then the Critical alert `JournalDeliveryRefused`, then `VerificationFailed` with `DPLUS_{code}[:{subtype}]`. No append, no quarantine, no repair |
| Later audit entries | Stay `Pending`, not cascaded. The first is labelled `DPLUS_STREAM_STOPPED`. Never delivered while the condition holds; re-derived from the archive on every pass |
| Attacker resets the refused row to Pending | Refused again; the finding is not stored twice and the alert is not repeated |
| Attacker forges the refused row as Anchored | The next entry is refused as `PREDECESSOR_MISSING` |
| Sentinel absent | Refused, not marked, finding and alert. Delivery resumes once the sentinel exists |
| Archive unavailable (sentinel read, predecessor read or append) | Entry `Pending` with the error text; no finding, no alert, no quarantine; the pass ends (existing policy). The next pass delivers in order, exactly once |
| Crash after the archive write, before the row update | The next pass completes idempotently (DP-8) |
| Restore collision at `r + 1` | Passes D+, then hits the existing `ARCHIVE_BODY_MISMATCH` quarantine (§5.2, DP-10) |
| Finding store unavailable | Row state unchanged (`… (finding not recorded)`), alert raised, refusal repeated; marked once the store is back |
| Earlier existing digest-mismatch row | The next revision is held: D+ removes the E-3 archive gap on the audit stream |
| Concurrent passes (4 parallel, two processor instances) | Each of 10 entries appended exactly once, in order; serialized by the organization row lock |

## 4. Stream isolation

- D+ is fixed to `audit`: there is no configuration, and an architecture test checks this.
- A stopped audit stream with 40 blocked rows, more than one batch, does not hold authorization or content delivery: tested in memory and on PostgreSQL.
- Authorization and content entries keep their previous behaviour. They are delivered even with no sentinel and a forged predecessor (CF-1), make no sentinel or predecessor read, and keep their outage and quarantine responses.
- **CF-1 residual** (contract §5.6): on those two streams a J1 actor can still have the Authority archive an entry linked to a forged predecessor. The verifier detects it after the fact; nothing prevents it. This closes only at the P2 gate, after demonstrated recovery behaviour.

## 5. SEC-P01–SEC-P12

The stack was a fresh disposable `enterpriseforms-sectest` stack, rebuilt with D+. On start, the Authority log shows the A-2 bootstrap creating the sentinel of the empty archive.

`dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj --no-build -c Release -- --minimum-expected-tests 13`: **13 total, 13 passed, 0 failed, 0 skipped**. This is SEC-P01..SEC-P12, including SEC-P12, plus the fixture fingerprint test.

After the run, all journal entries on the stack were Anchored (audit 15, authorization 59, content 25), no row was refused, and 0 of 15 organizations were quarantined. The stack was then removed with `down -v --remove-orphans`; no containers or volumes remain. Evidence: `sec-p/secp.txt`, `sec-p/sectest-up.txt`, `sec-p/stack-state.txt`.

## 6. Full regression

All three suites were rebuilt with `--no-incremental`: 0 warnings, 0 errors.

| Suite | Result | Evidence |
|---|---|---|
| Authority | **1451 total, 1410 passed, 0 failed, 41 skipped**. Against WP-4A: +25 D+ tests, plus 5 PostgreSQL D+ tests that skip outside the gate | `regression/authority.txt` |
| Architecture | **224/224** | `regression/architecture.txt` |
| Contract | **954 total, 942 passed, 0 failed, 12 skipped** (unchanged) | `regression/contract.txt` |

During development, the first full Authority run had 10 failures, all in the evidence world (§1); after the fixture provisioning, those classes pass 117/117.

## 7. PostgreSQL 18 + Azurite evidence

Command: `powershell -File scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -PerformanceRows 2000 -CheckpointCount 288 -EvidenceDirectory docs/evidence/sec-bl-01-wp5a/pg-azurite`. Result: **24/24 passed**, exit 0; containers and anonymous volumes removed. The runs use the real `AuthorityStore` as the runtime role on postgres:18.0 and the Blob archive on Azurite:

| Required demonstration | Test | Result |
|---|---|---|
| A valid audit stream keeps delivering | `DPlusAValidAuditStreamKeepsDeliveringOnRealStores` | Genesis, 5 then 3 more; the predecessor is read back from Azurite |
| A corrupted archived predecessor prevents the append | `DPlus…(damage: "corrupted")` | `DPLUS_LINK_MISMATCH`, no object 4..6 |
| A missing predecessor prevents the append | `DPlus…(damage: "missing")` | `DPLUS_PREDECESSOR_MISSING` |
| Later audit entries cannot bypass a stopped stream | Same, three more passes and a J1 reset | 5 and 6 stay Pending; the reset row is refused again |
| An archive outage leaves the entry Pending | `DPlusAnArchiveOutageLeavesTheEntryPendingAndRecoveryDeliversIt` | Pending against an unreachable endpoint; delivered after recovery |
| Authorization and content keep their behaviour | `DPlusAStoppedAuditStreamLeavesAuthorizationAndContentDeliveryUnchanged` | 40 audit rows stopped at genesis (R-4); authorization and content Anchored |
| Quarantine unchanged | All of the above | `integrity_quarantine_enabled` false throughout |

The other 19 tests of the gate are the WP-3..WP-4A real-store tests, all passed. Their measurements are a small functional run, not a performance claim.

## 8. Contract compliance

| Contract | Status |
|---|---|
| DP-1..DP-4, DP-6..DP-11, DP-13, DP-14 | Implemented and tested |
| DP-5 | Implemented, stricter: sentinel confirmed on every pass; archive-level refusal not marked (A-2 DD-1) |
| DP-12 | Implemented when a finding store is composed. Otherwise alert and log only: **reduced** (A-2 DD-4) |
| §5.3 stream stop | Implemented. Rows held, not cascaded (DD-3). Recovery not provided (G14) |
| §5.5 performance | At most 2 archive reads per audit pass plus appends; not measured at scale |
| §5.6 phases | P1 only. P2 not started; CF-1 open |
| §5.7 no weakening | Fixed in code, no configuration (architecture-tested) |
| AS-1 / DP-3 sentinel | A-2 SP-1..SP-4 |
| A-1, §8–§10 | Unchanged |

## 9. Residual risks

| ID | Risk | Status |
|---|---|---|
| R-W5-1 | CF-1: authorization/content entries can still be archived onto a forged predecessor (detected after the fact) | Open until P2 |
| R-W5-2 | A stopped audit stream has no recovery path (G14 not implemented): its evidence stops being archived until an approved recovery | Inherent to §5.3; accepted for the non-gating audit stream |
| R-W5-3 | Without a composed finding store, a D+ refusal leaves only an alert and a log line as its finding (DD-4) | Open; production needs WP-8 |
| R-W5-4 | A-2: an archive wiped down to its platform entries gets a new sentinel at the next Development/Testing start; detection continues as `PREDECESSOR_MISSING` and the verifier's `ARCHIVE_REGRESSION` (SP-4) | Accepted with the owner's decision |
| R-W5-5 | **Persistent environments:** an existing non-empty archive without a sentinel, such as the owner's persistent dev stack, refuses all audit delivery (`ARCHIVE_REGRESSION`, not marked) until a baseline with `--create-sentinel` runs. Nothing here touched that stack | Operator step before deploying WP-5a there |
| R-W5-6 | An undelivered entry altered before delivery and re-linked is delivered as genuine (§5.4, R-DR-1) | Unchanged |
| R-W5-7 | Stage B was not run. Its stacks get the A-2 sentinel at start, so audit delivery should proceed; **not verified** | Open until a Stage B run is authorized |
| — | R-W4-1 (runtime credential), R-W4A-1..8 | Unchanged |

## 10. Open decisions

- Confirmation of the reading of the authorization package and of "the stated dispositions" (status table).
- OI-DR-10 implementation as a separately authorized follow-up.
- A recovery procedure for a stopped stream (OI-DR-1 / G14), needed before P2 or before any audit-stream stop must be resumable.
- A production finding store for D+ (WP-8).
- Running a baseline (sentinel) on the persistent dev stack before WP-5a is deployed there (R-W5-5).
- Whether Stage B should be run to confirm R-W5-7.
- WP-5b (P2) remains gated.

**Dated note (2026-11-17, WP-5a.1):** WP-5a was conditionally accepted pending correction of two deviations: the A-2 automatic sentinel bootstrap (SP-1..SP-4) and the conditional durable finding (DD-4). Both are corrected in WP-5a.1: contract §22.8 and `SEC_BL_01_WP5A1_IMPLEMENTATION_RECORD.md`. The bootstrap described above is removed, and R-W5-3 and R-W5-4 are superseded. The text and results above are preserved as executed.

## 11. Not claimed

No D+ on authorization or content; no G14 recovery; no G08 change; no production or staging enablement; no tamper-resistant D+ finding object without a composed store; no closure or re-evaluation of SEC-BL-01, PD-15, M2F3-43 or CF-1; no Stage B result; no independent review.
