# SEC-BL-01 WP-5a.2 — Implementation Record: Durable Audit-Stream Stop (OI-DR-12, M2)

**Status (2026-11-18):**

| Item | State |
|---|---|
| Increment | WP-5a.2: create-only archive stop marker (M2) plus durable findings, discovered on every pass. Contract amendment **A-3, §23** |
| Authorization | Owner package "WP-5a.2: Durable Audit Stream Stop — OI-DR-12" (2026-11-18), read as authorization as with the earlier packages (**owner to confirm**). The implementer first stopped under package §2 and reported that the invariant cannot be guaranteed across the detection-to-persistence window (draft `SEC_BL_01_A3_DURABLE_STOP_DRAFT.md`). The owner then chose **W1: accept and narrow** (2026-11-18), and the implementation followed |
| Not done | Recovery or marker clearing, G14, WP-5b, G08, WP-7/8, production resources, CI changes, Stage B, commit, push, merge, deployment, persistent data changes |
| Baseline | `main` at `a9b32de`; all uncommitted WP-0..WP-5a.1 work preserved. Contract §1–§20 byte-identical (`4a8bd1b1…ef12e`), and the texts of A-1, A-2 §22.1–§22.7 and §22.8 unchanged |
| DD-10 | **Narrowed, not closed** (§23.3) |
| SEC-BL-01 / PD-15 / M2F3-43 | OPEN / OPEN / FAILED |
| Review | Owner-led; prepared by the implementing assistant; not an independent review |

---

## 1. Design and conflicts

A-3 (§23.1, SM-1..SM-9) defines the following:
- the marker format and its deterministic key under the reserved `meta/dplus-stops/` prefix;
- create-only, idempotent writes;
- discovery on every pass (stronger than "on restart");
- the cross-check and completion of a missing half;
- the verifier's complete enumeration of `meta/`;
- failure behaviour for each store;
- authorized recovery as a requirement that is not implemented;
- the threat-model limits.

The design has these conflicts:

| Conflict | Resolution |
|---|---|
| **C-A3-1:** the package invariant cannot hold across the detection-to-persistence window | Owner disposition W1: the window is an accepted residual (§23.3) |
| **C-A3-2:** the finding store had no read operation | `ListAsync` added (read-only) |
| **C-A3-3:** the verifier's enumeration needed amending | SM-7 adds a complete `meta/` listing; AE-1..AE-3 are unchanged |
| Draft SM-6 said a marker without its finding is *reported* | §23.1 refines this. A store outage and a J6 deletion are indistinguishable, so it is completed as operational, which avoids fabricating an integrity finding from an outage. A finding without its marker is reported as security only when the finding records that its marker had been stored |

## 2. Invariant and limits

**Proven:** once either record (marker or finding) is durable, the audit stream stays stopped through:
- a restart;
- finding-store recovery;
- an AuthorityDb reset or forgery;
- replacement of the evidence;
- retries with valid-looking evidence.

**Not guaranteed** (accepted residual): a termination after detection but before either record is confirmed, followed by replacement of the evidence. **Known limit:** removal of both records by J4/J5 and J6.

## 3. Implementation

| Area | Change |
|---|---|
| `Common/Ports/ISecurityEventArchiveInventory.cs` | `CreateStopMarkerAsync`, `ReadStopMarkersAsync`, `ListMetaObjectNamesAsync`, `ArchiveMetaObject` |
| `Common/Ports/IJournalVerificationFindingStore.cs` | `ListAsync` (read-only) |
| `Features/Journal/DPlusStreamStops.cs` (new) | Marker record, strict codec, stream-stopping classification, D+ finding parser |
| `Features/Journal/ArchiveProcessor.cs` | Discovery before evaluation; write order and codes; completion; in-memory hold only when neither record is confirmed |
| `Features/Journal/AuditDeliveryEnforcement.cs` | The pass records the sentinel's archive instance |
| `Features/Journal/Reconciliation/JournalStreamComparer.cs` | SM-7 `meta/` inspection |
| `Features/Journal/Reconciliation/JournalVerificationFindings.cs` | Three codes |
| `Infrastructure/Archive/BlobSecurityEventArchive.cs` | Blob, in-memory and unavailable marker operations; the in-memory summary counts markers; test hooks |
| `Infrastructure/Archive/VerificationCheckpointStores.cs` | Blob and in-memory finding `ListAsync`; in-memory removal hook |
| Tests | `AuditDeliveryEnforcementTests.cs`: 6 WP-5a/5a.1 tests replaced by 13 A-3 tests, and the store double split into list/record switches. `StopMarkerVerifierTests.cs` (new, 2). `JournalVerificationPostgresIntegrationTests.cs`: 2 new; 1 WP-5a.1 expectation updated (§5). Archive doubles extended |
| Documentation | Contract §23 and status row; draft status updated; this record; backlog row; dated note in the WP-5a.1 record; regenerated manifests |
| Evidence | `docs/evidence/sec-bl-01-wp5a2/` (`pg-azurite`, `sec-p`, `regression`) |

Authorization/content delivery, the journal state machine, migrations, grants, compose and CI are unchanged.

## 4. Failure-window analysis

| Step | If the process terminates here, then on restart: |
|---|---|
| D+ reads the evidence and refuses (memory only) | **Accepted residual window:** re-derived from the evidence; resumes if it was replaced |
| Marker confirmed, finding not | Stopped (marker); the finding is completed later |
| Finding confirmed, marker not | Stopped (finding); the marker is created later |
| Both confirmed, row not yet updated | Stopped; the row is marked from the records |
| Row marked `VerificationFailed` | Stopped |

| Store state at refusal | Classification | Result |
|---|---|---|
| Both available | Confirmed integrity failure | Durable stop established; row marked |
| Archive available, finding store down | Confirmed | Established (marker); row Pending, `DPLUS_FINDING_STORE_UNAVAILABLE` |
| Archive marker write failing, finding store up | Confirmed | Established (finding); row Pending, `DPLUS_STOP_MARKER_UNAVAILABLE` |
| Both failing | Confirmed | **Not established**: in-memory hold, `DPLUS_DURABLE_STOP_NOT_ESTABLISHED`, distinct Critical alert |
| Archive listing or read unavailable before evaluation | Operational | No evaluation, no finding; the pass ends (DP-7) |
| Finding listing unavailable before evaluation | Inconclusive | No evaluation, no finding; the audit stream waits; other streams continue |

## 5. Test results (restart and evidence-replacement cases)

**Focused in-memory tests:** `AuthorizationAuthority.Tests.exe --filter-class` `AuditDeliveryEnforcementTests`, `StopMarkerVerifierTests`, `JournalVerifierTests` and `JournalBaselineServiceTests`. All pass: `AuditDeliveryEnforcementTests` 37/37; the other classes in the full suite below. The cases:
- both available;
- finding store down, with restart and evidence replacement, then recovery completing the finding;
- marker write down, with restart and evidence replacement, then the marker completed;
- both down, held in memory and surviving evidence replacement in-process;
- **the accepted residual**, pinned;
- row reset and forgery;
- marker removed: reported and recreated;
- marker altered, foreign or non-canonical (3 cases): stopped and reported;
- a conflicting object at the key;
- **the known limit** (both records removed), pinned;
- discovery failures: no fabrication;
- isolation;
- idempotency (one marker, one finding);
- verifier: valid marker recognized; invalid marker and unexpected `meta/` objects reported.

**PostgreSQL 18 + Azurite** (`scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -PerformanceRows 2000 -CheckpointCount 288 -EvidenceDirectory docs/evidence/sec-bl-01-wp5a2/pg-azurite`): final run **28/28**, exit 0, containers removed. New cases:
- real Blob marker plus finding, surviving a restart, an SQL row reset and restoration of the genuine predecessor;
- deletion of the marker blob: reported and recreated, delivery stopped;
- the marker alone stopping the stream with no finding store, across a restart, after the evidence was replaced.

The first gate run was 27/28: the WP-5a.1 test of an unreachable finding store expected a refusal alert. Under A-3 the unreachable store makes discovery inconclusive and no refusal is evaluated, so its expectation was updated to "no alert, no marker, Pending". That is the intended "storage outage is not an integrity finding" behaviour.

## 6. SEC-P01–SEC-P12

The disposable security-test stack was rebuilt fresh; the provisioner reported `Provisioned`.

`dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj --no-build -c Release -- --minimum-expected-tests 13`: **13/13**, including SEC-P12.

Afterwards: audit 15, authorization 59 and content 25 entries were Anchored; 0 of 15 organizations were quarantined. The stack was removed with `down -v`; no containers or volumes are left. Evidence: `sec-p/`.

## 7. Full regression

All three suites were rebuilt with `--no-incremental`: 0 warnings, 0 errors.

| Suite | Result |
|---|---|
| Authority | **1480 total, 1435 passed, 0 failed, 45 skipped** |
| Architecture | **224/224** |
| Contract | **958 total, 946 passed, 0 failed, 12 skipped** |

## 8. Residual security risks

| ID | Risk |
|---|---|
| R-W5A2-1 | §23.3 residual window (accepted, W1): termination before the first durable record, followed by evidence replacement, resumes delivery |
| R-W5A2-2 | Removal of both the marker (J4/J5) and the finding (J6) leaves no record |
| R-W5A2-3 | Markers share the archive with the evidence: not independent of J5. A J5 who also deletes the finding store's record defeats the stop (R-W5A2-2) |
| R-W5A2-4 | A finding-store **listing** outage halts audit delivery, which is inconclusive by design; this is an availability cost |
| R-W5A2-5 | A marker without its finding is completed silently: a J6 deletion of a finding is not reported, only repaired |
| R-W5A2-6 | No recovery exists (SM-8): any confirmed refusal stops the audit stream until G14 or an approved procedure |
| — | Unchanged: CF-1, PV-9, R-W5A1-3..7, the persistent dev baseline step |

## 9. Recommendation on DD-10

**Do not close DD-10.** A-3 closes every restart and evidence-replacement path once a record is durable, and that is demonstrated with durable evidence. The window between detection and the first durable write remains, accepted under W1. DD-10 can be closed only by either of these:
- W2: fail closed on unclean termination, which needs a recovery action;
- production immutability with an independent anchor (WP-8), together with an owner decision that the remaining production window is acceptable.

Recommended status: **DD-10 narrowed; accepted residual R-W5A2-1**.
