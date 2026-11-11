# Indexed search (Option C): IX-2 implementation evidence report (C-129)

- **Status:** IX-2 implemented and closed (C-130) on local branch `ix-1/authority-search-index`, on top of IX-1 (`af6eaf3`); closure in section 8. Returned for owner acceptance. **Not accepted, not independently reviewed, not pushed, not merged, not deployed.** Stage B intentionally not run (C-131).
- **Authorization:** C-129 (owner, 2026-11-07): conditional acceptance of IX-1 at `af6eaf3`; authorization of **IX-2 implementation only**. IX-3 to IX-7 unauthorized; Tier 2 design-only; M4-1 open; OBS-IX-1 remediation and R6 separate and unauthorized; M4-2, G14 and RCF-1 unauthorized.
- **Data:** synthetic only. The index runs in **Dark** mode on the Testing stack only (no indexed query exists). Every other environment keeps **Disabled**, where every IX-2 endpoint answers 404 `SEARCH_INDEX_NOT_PRESENT` and the builder stays idle (no credential).
- **Kind:** implementation evidence for an owner-led review. Not an independent assessment; no attestation is created or implied.
- **Implementation decisions:** design §20 (D-IX2-1 to D-IX2-21).

## 1. Scope (from the approved text only)

| IX-2 item (design §16) | Source | Implemented | Where |
|---|---|---|---|
| O-1 client hardening | R1 §A.3 O-1(a); P-S1-1 §4; IX-D10 | Echoed-request mapping (count and per-pair echo must match, else 503); chunking at `SpiceDb:BulkCheckMaximumItems` (100) at the same snapshot, never truncation; strict variant (per-item error fails the call) for V2 | `SpiceDbBulkCheckMapping.cs`, `SpiceDbProjectionClient.cs`, `ISpiceDbProjection.CheckBulkStrictAsync`, `ProjectionIntegrityVerifier.CheckBulkStrictAsync` |
| IX2-CT-07 (gate item "CT-7"; renamed C-130) | R1 §E.3; P-S1-1 §4 | Contract probe: per-request item maximum, completeness and echo of every accepted request, refusal above the maximum | `EnterpriseForms.SpiceDbContractProbe/Program.cs`, `SpiceDbBehaviourContractTests` |
| O-2 reader | R1 §A.3 O-2; IX-D10 | One query per row table per batch; byte-identical to the per-record reader; used by the builder and by the V1 scope verifier | `BusinessStore.GetStagedContentBatchAsync`, `RecordCandidateStager.ReadStagedBatchAsync`, `IAcceptedRecordItemsReader.ReadAcceptedContentBatchAsync`, `TrustedScopeVerifier` |
| `search-index-builder` workload | R1 §C.3; IX-D6 (OD-1 pattern) | Own credential, scheme, role and policy; Worker host only; idle when unconfigured; stateless rounds | `InternalAuthentication` (`SearchIndexBuilder*`), `SearchIndexBuilderHttpClient`, `SearchIndexBuilderWorkflow`, `SearchIndexBuilderWorker` |
| Verified backfill | R1 §C.3 item 3, §C.4 | Shared workload verification (registry, quarantine, archive receipt, key, complete leaf set, every HMAC, root, expected head), then derivation and ES256 seal of the verified bytes under the organization lock; `HEAD_CHANGED` otherwise; idempotent per record, head and coverage set | `SearchIndexBuilderService.BackfillAsync`, `ContentService.VerifyWorkloadObservedContentAsync`, `SearchIndexWriter.DeriveForBackfillAsync` |
| Authority attestation | R1 §C.3 item 4; IX-D6 | From registry, heads and entries under the organization lock, journaled before return; Complete names its journal entry (FK + CHECK) | `SearchIndexBuilderService.AttestAsync` |
| Deep sweep (S-C) | §6.5, §17.5, §17.10 | Authority-chosen entries (oldest first plus `random()` sample); Pass writes only that entry's timestamp; posting mismatch Fail (index quarantine); business-byte mismatch record quarantine and Inconclusive | `SearchIndexBuilderService.DeepSweepWorkAsync`, `DeepVerifyAsync`, `CompareWithStored` |
| `SearchProjectionRebuild` | §12.2, IX-D7, §17.8 IX-45 | Security Administrator (S1 decision on `administer_security`), gate and revision re-check under the organization lock, purge, coverages Building, `Rebuilding`; Active only after attestation | `SearchProjectionRebuildService`, Authority `POST /internal/v1/search-index/rebuild`, business `POST /api/v1/organization/search-index/rebuild` |
| S-A completeness check | R1 §C.6 first check | `COMPLETE_COVERAGE_MISSING_ENTRY` Fail | `SearchIndexStore.RunCheapChecksAsync` |

Not built (later stages or unauthorized): indexed query and paging (IX-3), result snapshots, shadow engine, UI and dashboard health, Tier 2, Managed HSM providers, G14 replay.

## 2. Files and schema

**Authority (new):** `Application/Features/SearchIndex/SearchIndexBuilderService.cs`, `SearchProjectionRebuildService.cs`; `Api/Features/SearchIndex/SearchIndexBuilderEndpoints.cs`; `Contracts/V1/SearchIndexBuilderContracts.cs`; `Infrastructure/SpiceDb/SpiceDbBulkCheckMapping.cs`; migration `20261107213255_AuthoritySearchIndexIx2`.

**Authority (changed):** `ContentService.cs` (shared workload verification; census delegates to it); `SearchIndexWriter.cs` (`DeriveForBackfillAsync`); `SearchIndexSweepService.cs` (shared quarantine transition; reconciliation returns every Complete coverage to Building); `ISearchIndexStore.cs`, `SearchIndexStore.cs` (builder, deep-sweep and purge operations; eighth S-A check); `SearchIndexEntities.cs`, `SearchIndexConfigurations.cs`; `AuthorityRuntimeGrants.cs`; `AuditJournal.cs` (returns the appended entry); `AuthorityException.cs` (two codes); `InternalAuthentication.cs`; `SearchIndexComposition.cs`; `Program.cs`; `SpiceDbProjectionClient.cs`; `ISpiceDbProjection.cs`; `ProjectionIntegrityVerifier.cs`.

**Business (new):** `Application/Common/Authority/ISearchIndexBuilderAuthority.cs`; `Application/Features/Search/IndexBuilder/SearchIndexBuilderWorkflow.cs`, `SearchIndexAdministrationHandler.cs`; `Infrastructure/Authority/SearchIndexBuilderHttpClient.cs`; `Worker/Features/Search/SearchIndexBuilderWorker.cs`.

**Business (changed):** `IBusinessStore.cs`, `BusinessStore.cs`, `RecordCandidateStager.cs`, `IAcceptedRecordItemsReader.cs`, `TrustedScopeVerifier.cs`, `AuthorityHttpClient.cs`, `BusinessInfrastructureRegistration.cs`, `SearchEndpoints.cs`, `SearchTestingEndpoints.cs` (Testing-only parity endpoint), `FieldSearchContracts.cs`, `Worker/Program.cs`.

**Contracts:** `contracts/openapi/authorization-authority-internal-v1.json` and `contracts/openapi/enterprise-forms-v1.json` regenerated by the build; `src/frontend/packages/api-client/src/generated/enterprise-forms-v1.ts` regenerated (one new operation, two schemas).

**Configuration:** `compose.security-test.yaml` (synthetic builder credential for the Authority and the Worker only; Testing intervals). **Tests:** see section 3.

**Schema changes (AuthorityDb only, expand only):**

| Object | Change |
|---|---|
| `authority_search.search_projection_states` | `ck_search_projection_status` admits `Rebuilding` |
| `authority_search.search_field_coverages` | `coverage_attested_journal_entry_id uuid NULL`, FK `fk_search_field_coverage_attestation` to `authority.security_journal_entries`, index `ix_search_field_coverages_attestation`, CHECK `ck_search_coverage_attestation` (Complete ⇒ attested and completed) |
| `authority_search.search_record_entries` | `last_deep_attempted_on timestamptz NULL`, `last_deep_attempt_outcome_code varchar(64) NULL` |
| Runtime grant | column-limited `UPDATE` extended from the three S-B columns to also `last_deep_verified_on`, `last_deep_attempted_on`, `last_deep_attempt_outcome_code`; no other grant changes |

No BusinessDb schema change. No new authorization-stream event (D-IX1-12 preserved); every new event is on the audit stream: `SearchIndexPopulationIssued`, `SearchIndexCoverageAttested`, `SearchIndexRebuilt`, `SearchProjectionRebuildRequested`.

## 3. Tests executed

All on this workstation (Windows 11, Docker Desktop), 2026-11-07, synthetic data, Release builds. **Power: AC, charging** (checked with `SystemInformation.PowerStatus` and `root\wmi BatteryStatus.PowerOnline`; a first reading at 21:5x UTC showed offline and was stale, the next two showed online; logged in `artifacts/ix-2/integration/power.txt`).

### 3.1 Unit, vector and architecture tests

| Suite | Result | IX-2 content |
|---|---|---|
| `AuthorizationAuthority.Tests` | **417/417** (382 before IX-2) | `SearchIndexBuilderTests` 25 cases (backfill and attestation; IX-18 replay; IX-67 race; IX-25 five forgeries and other form; IX-26 withholding; record without content; IX-27 MAC and seal outages; S-A completeness Fail; INV-R reconciliation; S-C Pass, Fail, business-byte quarantine, moved entry, selection; IX-59; IX-45; rebuild authorization; Disabled mode; batch bounds). `SpiceDbBulkCheckMappingTests` 10 cases (O-1) |
| `EnterpriseForms.ArchitectureTests` | **92/92** (89 before) | Two IX-1 fitness functions updated deliberately (feature file list; column-limited grant). Three new: backfill derives only after the workload verification and only in the builder service; builder requests carry identifiers and bytes only; builder plane separated and the builder port registered by the Worker alone. INV-R single writer and the W1-W4 site count unchanged |
| `EnterpriseForms.Integrity.Tests` | **60/60** | Unchanged (seal vectors) |
| Contract tests | **507/507** | OpenAPI snapshot regenerated (one new business operation) |
| Platform console | **23/23** | Unchanged |

### 3.2 Integration tests on the Testing stack

`SearchIndexBuilderStackTests` (new; Stage B group `search-catalogue`): real business API and Worker, Authority, AuthorityDb, BusinessDb, SpiceDB, Vault Transit.

| Run | Stack | Result | Failures and cause |
|---|---|---|---|
| 1 | fresh (`-Fresh`) | 7/11 | Three test defects (the tests read Authority responses in camelCase; the Authority wire format is snake_case) and one Testing-endpoint defect (catalogue limit 5,000 above the Authority maximum of 500). No product defect |
| 2 | same stack, rebuilt images | 10/11 | `IX25…`: the test assumed the fabricated record could not be indexed; the worker had indexed it from its genuine bytes before the test's forged submission quarantined it. Correct product behaviour (the entry stays at the quarantined head, which the guard treats as unusable); assertion restated |
| 3 | same stack | **11/11** | — |

| Test | Covers | Run 3 |
|---|---|---|
| `TheWorkerBackfillsExistingRecordsAndTheAuthorityAttestsComplete` | IX-D6 end to end; attestation journal reference; S-A clean after Complete; throughput | Passed |
| `IX67CommitsRacingTheBackfillKeepEveryEntryAtItsHead` | IX-67: 24 records, 36 concurrent edits during the backfill; every entry at its head, no quarantine, final values indexed | Passed |
| `IX25TamperedOrFabricatedBytesAreRefusedQuarantineTheRecordAndBlockComplete` | IX-25: business-DBA-tampered row submitted by the worker and fabricated bytes submitted directly: refused, record quarantined, nothing indexed from them, incident journaled with the workload, Complete withheld, index and organization not quarantined | Passed |
| `IX18AReplayOfAnOlderOperationIsRefusedAtTheHeadCheck` | IX-18: an older operation is refused (`HEAD_CHANGED`) without verification or quarantine; the indexed value is the current one | Passed |
| `OnlyTheBuilderCredentialReachesTheBuilderPlaneAndItReachesNothingElse` | Credential separation: business workload, verifier, operator, reconciler and a builder token with the wrong role are refused on all six builder routes; the builder token is refused on the verifier plane, the user search catalogue and the rebuild command | Passed |
| `IX27ABuilderOutageKeepsTheCoverageBuildingAndARestoredBuilderCompletesIt` | IX-26 (everything withheld) and IX-27 builder outage: worker recreated with a key the Authority does not accept; coverage Building, attestation `RECORDS_NOT_INDEXED`; restored worker completes | Passed |
| `IX27AVaultOutageDuringBackfillWritesNothingAndTheRoundResumes` | IX-27 HSM stand-in outage: Vault paused during the backfill; no entry, no record quarantine; resumes after unpause | Passed |
| `IX19AnEntryLossQuarantinesTheIndexAndOnlyARebuildRestoresIt` | IX-19 (**simulated, partial**), IX-44, IX-59: three entries deleted as AuthorityDb administrator (partial restore of `authority_search`) → `COMPLETE_COVERAGE_MISSING_ENTRY` index quarantine, organization not quarantined, records readable and editable, no clearing by sweeps or builder rounds, Clerk refused (403), Security Administrator rebuild → `Rebuilding` → Active and Complete, edit made during the quarantine indexed | Passed |
| `IX45ARebuildIsRefusedWhileTheOrganizationIsQuarantined` | IX-45: with the S1 quarantine flag set, rebuild 423, nothing journaled, attestation 423 | Passed |
| `TheDeepSweepPassesHonestBytesAndQuarantinesARecordWhoseBusinessBytesChanged` | S-C with the worker: honest record `MATCH` and timestamp advanced; business-DBA-tampered record quarantined, `BUSINESS_BYTES_MISMATCH`, no S-C timestamp, index Active | Passed |
| `TheSetBasedReaderReturnsExactlyWhatThePerRecordReaderReturns` | O-2 equality over 55 records (165 items, 110 values): **0 mismatches** | Passed |

Other Docker classes on the IX-2 code (run 1, fresh stack):

| Class | Result | Note |
|---|---|---|
| `SearchIndexFoundationTests` (IX-1) | **9/9** | Two assertions restated for the running builder (D-IX2-17); includes the IX-1 commit-latency measurement, not re-evaluated here (C-129: no IX-1 performance work) |
| `SpiceDbBehaviourContractTests` | **7/7** | CT-1 to CT-6 unchanged, IX2-CT-07 new (then named CT-7) |

**IX2-CT-07 detail** (named CT-7 until C-130) (probe run directly, `artifacts/ix-2/ct-probe.txt`): `configured_cap=100 cap_accepted=True every_accepted_complete=True maximum_accepted=10000 first_refused=10001 refusal=[InvalidArgument: check count of 10001 is greater than maximum allowed of 10000]`. SpiceDB v1.56.2 accepts at most 10,000 items per CheckBulkPermissions request, answers every accepted request completely with each item echoed in order, and refuses a larger request with an error (no truncation). The Authority's chunk size of 100 is below the maximum.

### 3.3 Measurements (local; not PERF-01)

| Measurement | Result | Conditions |
|---|---|---|
| Backfill, 60 pre-existing records | One worker round: 60 submitted, 60 indexed, 2 coverages completed, **1,259 ms** (≈ 48 records/s). Approval to Complete: **4.07 s** (≈ 14.7 records/s), including the 3 s polling interval and the attestation | AC power; Vault Transit (not Managed HSM); per record 3 MAC verifications and 1 ES256 seal sign; `artifacts/ix-2/backfill-throughput.json` |
| O-2 reader, 55 records | per-record reads 683 ms, set-based 27 ms | Same stack; one run; indicative only |

Neither figure is a PB-09 or PERF-01 result. Managed HSM latency, throttling and cost are unmeasured (IX-7, ST-PERF-7).

### 3.4 Regression

| Run | Result | Note |
|---|---|---|
| Authority, architecture, integrity, contract, platform console (section 3.1) | 417/417, 92/92, 60/60, 507/507, 23/23 | Release builds |
| Stage A gate: SEC-P01 to SEC-P12 (`--minimum-expected-tests 13`) on a clean stack (`down -v`, rebuilt images) | **13/13 passed** | `artifacts/ix-2/regression/stage-a-secp.txt` |
| Evidence validator (`validate_security_evidence.py`) | PASS | Manifest structure and digests only; not a security attestation |
| Stage B: `SearchIndexBuilderStackTests` 11/11, `SearchIndexFoundationTests` 9/9, `SpiceDbBehaviourContractTests` 7/7 | Passed | Section 3.2 |
| **Stage B, the other groups and classes** | **Not run** | Stopped after the Stage A gate on the owner's instruction (2026-11-07). Not executed on the IX-2 code: forms-documents, authorization-security-onboarding (except the SpiceDB contracts), records-lifecycle, administration-membership, protection-reconciliation, search-execution (except the IX-1 class), and `SearchCatalogueTests` / `FieldSearchApprovalTests` (including the 480-record benchmark). These exercise two IX-2 changes not otherwise covered end to end: the O-1 echo check on every bulk permission check, and the O-2 reader in V1 search |

Worker change after the Docker runs: the builder loop's catch clause was widened so no exception type can stop the Worker host (D-IX2-14 area; compiled, unit and architecture suites unaffected). The Docker classes above ran on the earlier, narrower catch.

## 4. Security and integrity invariants

| Invariant (C-129) | How IX-2 preserves it | Evidence |
|---|---|---|
| Authority ownership of the index | Every index write is an Authority transaction; the builder supplies bytes only; no endpoint accepts a posting, entry, seal or coverage state | Architecture `BuilderRequestsCarryNoIndexRowsSealsOrStates`; IX-25 |
| Atomic accepted-content/index updates | Inline derivation unchanged; a backfill derives in one transaction under the organization lock, conditional on the head, and an outage rolls the whole batch back | IX-67 (unit and Docker); IX-27; IX-1 crash test 9/9 |
| INV-R revision consistency | A backfill changes no content revision; quarantines go through the single W2 site; reconciliation returns Complete coverages to Building | Architecture W1-W4 test unchanged; IX-25 unit assertion on revision and projection; `ReconciliationReturnsEveryCompleteCoverage…` |
| ES256 authenticity and public-key pinning | Backfilled entries are sealed through the same pin-checking gate; S-B verifies them with the pinned key | Unit backfill test verifies every backfilled seal; seal outage refuses |
| Classification and searchability | Derivation is the IX-1 `SearchPostingDerivation/v1` from Authority approvals only; encrypted leaves stay anomalies | Unit backfill test: encrypted, repeating-group and unapproved fields not indexed |
| S1 authorization and quarantine precedence | Every builder and rebuild path requires the organization gate open (423 otherwise); rebuild decides `administer_security` on the S1 path and re-checks the revision under the lock; an index quarantine never touches the S1 state | IX-45 (unit and Docker); IX-19 asserts S1 flag unchanged |
| No Confidential or encrypted indexing | Unchanged derivation; S-C compares the same derivation | As above |
| Business database untrusted | Every byte verified against the accepted head; tampering quarantines the record | IX-25, S-C business-byte test |

## 5. Failures, limitations and deviations

**Failures:** none outstanding in the IX-2 tests. The run-1 and run-2 failures were test defects (section 3.2). The full Stage B regression was not run (section 3.4), so regressions outside the tested classes are not excluded.

**Limitations:**
1. **IX-19 is partial.** There is no AuthorityDb PITR and no G14 journal replay (G14 unauthorized). The test simulates the resulting state (entries lost while heads stand) and shows detection, quarantine and rebuild. The design's "automatically entering `Rebuilding` after recovery cutover" is part of the G14 recovery runbook and is not implemented.
2. **No step-up** on `SearchProjectionRebuild` (PB-08 absent; D-IX2-9).
3. **Vault Transit is not an HSM**; outages were simulated by pausing the container.
4. **Pacing defaults are unmeasured** against Managed HSM (D-IX2-8).
5. **Population and attestation read the form's whole registry and entry set** in one transaction. Fine at the tested sizes; their cost at 100K records is unmeasured (PERF-01).
6. **The quarantined-record S-C consequence** (D-IX2-7) is open.
7. **No dashboard or UI** for coverage, attestation or rebuild state; the design's "shown on the Release Security Dashboard" is not built.
8. **CT-7 identifier collision** with S1 Design B (D-IX2-12); resolved by C-130 (IX2-CT-07).

**Deviations from the design text:** D-IX2-2 (idempotency by construction instead of a per-batch key), D-IX2-3 (keyset population without a snapshot), D-IX2-6 (all Complete coverages to Building on reconciliation), D-IX2-17 (two IX-1 assertions restated). None weakens a security property.

## 6. Traceable register of outstanding requirements (C-129 item 4)

| # | Requirement | Source | Status | Assigned to |
|---|---|---|---|---|
| R-01 | Azure Managed HSM ES256 seal provider (and the Managed HSM HMAC provider) | D-IX1-13; C-126 | Not implemented; providers fail closed | **IX-7** (ST-PERF-1, ST-PERF-7); blocks production |
| R-02 | 20 ms p95 incremental commit latency | IX-1 gate; C-128; C-129; C-149 | **FAIL**, carried. C-149 diagnostic (capped, deviating protocol): pooled Δp95 17.1 ms (15.3–19.3) over 5 × ≈900 pairs; the stopped full-size run gave 25.5 ms (18.1–31.5). Not a closure. The dominant derivation cost is the reads stage (`INDEXED_SEARCH_C149_EVIDENCE.md` §7) | **IX-7** ST-PERF-1..7; failure blocks production readiness |
| R-03 | Cross-language cryptographic test vectors (seal, posting derivation) | IX-1 §4.2 item 4 | .NET-only vectors | Independent cryptographic review (package Q7), **before production**; owner to schedule |
| R-04 | W3 and W4 integration coverage; IX-73 beyond W1 | IX-1 §4.2 item 5 | Unit-tested only | **Owner decision** (no stage assigns it); recommended before IX-3 exit, because the IX-3 guard depends on INV-R at every site |
| R-05 | pgAudit and Azure diagnostic log inspection (IX-28 partial) | IX-1 §2.5 | Local logs only | **IX-7** staging |
| R-06 | 480-record verified-search limitation (P-S1-1 / RR-15) | IX-D10 | Open known failure until V1 fixed or retired; O-2 now applied to V1 | Disposition at **IX-4** (§16 IX-4 exit: "P-S1-1 disposition recorded"); this regression's result: not run on the IX-2 code (section 3.4) |
| R-07 | Intermittent `RecordCreationIdempotencyTests` | IX-1 §2.4 | not run on the IX-2 code (section 3.4) | **Owner decision** (outside the indexed-search stages) |
| R-08 | Signed external checkpoints (freshness against AD-1/AD-3) | C-126, C-127; design §18.1.4 | Not implemented | **Before any real-data use**; owner decision on the vehicle (remediation proposal option E / R5) |
| R-09 | Independent third-party security and cryptographic review (design and implementation) | FD-8; C-123; §16 IX-7 | Not started; package ready | External; design review before production, implementation review at **IX-7** |
| R-10 | OBS-IX-1 and R6 (keyed journal) | C-124; AP-3 | Unauthorized; blocks real-data environments and production | **Owner decision** (separate authorization) |
| R-11 | IX-19 full: AuthorityDb restore, G14 replay, automatic `Rebuilding` after recovery cutover | §12.2; IX-19 | Simulated only | **G14** (unauthorized) and **IX-7** DR rehearsal |
| R-12 | Step-up on `SearchProjectionRebuild` | §12.2 | Not available | **PB-08** |
| R-13 | Quarantined record and the S-C 30-day deadline (D-IX2-7) | §17.10 | **Closed (C-130):** excluded while quarantined, under the ten rules (design §20) | — |
| R-14 | CT-7 identifier collision | D-IX2-12 | **Closed (C-130):** IX-2 test renamed IX2-CT-07 | — |
| R-15 | Builder and deep-sweep pacing; population/attestation cost at 100K | D-IX2-8; §5 item 5 | Unmeasured | **IX-7** PERF-01 |
| R-16 | Dashboard health for coverage, sweeps, attestation and rebuild | §6.5, §17.9.1 evidence | Not built | **Owner decision** (no stage names it; PB-12 dashboard) |
| R-17 | Client certificates for the builder outside Development/Testing | D-IX2-14 | Enforced at startup, not exercised | **IX-7** staging |
| R-18 | IX-3 tests depending on the query path (IX-07, 08-13, 20-24, 33, 36, 43, 46-50, 54-56, 68, 78-82), and D-IX2-7 rule 1 (quarantined records never in indexed results) | §16; C-130 | Not applicable before IX-3 | **IX-3** (unauthorized) |
| R-19 | Stage B regression on the IX-2 code (all seven groups, browser suite, 480-record benchmark) | C-131 | **Not run** (release-only policy) | Next **owner-authorized release test run** |

## 7. Recommendation (before closure; superseded by section 8.5)

> Superseded by C-130/C-131: Stage B is release-only and is not a prerequisite for development-stage acceptance; D-IX2-7 and the CT-7 identifier were decided. See section 8.5.


**Not yet ready for an acceptance decision.** The IX-2 functions and their gate tests pass: IX-18, IX-19 (simulated, partial), IX-25 to IX-27, CT-7 and the backfill throughput, plus IX-67, IX-45, IX-59 and S-C. But the Stage B regression stopped after the Stage A gate.

**Recommended before an acceptance decision:**
1. Run Stage B on the IX-2 code, at least the groups that use the two changed shared paths: `search-catalogue`, `search-execution`, `protection-reconciliation` and `authorization-security-onboarding`. Better, all seven groups.
2. Rerun the IX-2 Docker class once on images that include the widened Worker catch.
3. Decide D-IX2-7 (R-13) and the CT-7 identifier (R-14).

**If those pass, the recommendation would be conditional acceptance of IX-2**:
- with IX-19 recorded as partial;
- with the register items carried to their assigned stages;
- with IX-3 and later unauthorized.

Acceptance is the owner's decision; this document does not record it.

## 8. IX-2 closure (C-130, 2026-11-07)

Owner instruction: complete the remaining IX-2 corrections, verify with focused tests only, do **not** run Stage B (permanent policy C-131), commit locally (signed), stop.

### 8.1 Working tree before closure

Inventory taken before any closure change (`git status --short`, nothing staged, branch `ix-1/authority-search-index` at `af6eaf3`):
- 60 entries: 45 modified files and 15 untracked paths. The untracked directories hold 19 files in total.
- Every entry is IX-2 work from the C-129 session: source, tests, migration, contracts, generated client, configuration, documents, spec manifest. None is unrelated or unexplained.
- All of it is preserved and included in the IX-2 commit; nothing was reset, cleaned or discarded.

### 8.2 Closure changes

| Item | Change | Decision |
|---|---|---|
| Quarantined records and the S-C deadline | Excluded from the S-C deadline and from S-C selection while quarantined in the Authority; the exclusion count is journaled and alerted on every change; leaving quarantine re-enters the deadline with the old timestamp (suspension until the record passes S-C); S-A and S-B still verify every entry. Additive migration `AuthoritySearchIndexIx2DeepExclusion` | D-IX2-7 (approved), D-IX2-21 |
| CT-7 collision | IX-2 test renamed **IX2-CT-07**; S1's CT-7 untouched | D-IX2-12 |
| Worker error handling | `SearchIndexBuilderRunner`: transient → bounded exponential backoff (2 s doubling to 5 min); refused credential (401/403) → builder stops, fail closed, Critical log; other failures logged at Error, never escape; cancellation ends the loop from a pass or a delay. Logs carry counts, exception type names and error codes only | D-IX2-20 |
| IX-19 | Accepted as **partial** (simulated restore state). No real database restore or journal replay was tested; the full test stays with G14 (R-11) | C-130 |

### 8.3 Focused tests on the final code

All Release builds; AC power. **Stage B was intentionally not run** (C-131): no Stage B group run, no `-All`, no browser suite, no 480-record benchmark. The classes below were run one by one with `stage-b.ps1 -Class` as focused tests of the changed code.

| Suite | Result | Note |
|---|---|---|
| `AuthorizationAuthority.Tests` | **421/421** | Adds 4 D-IX2-7 tests: exclusion without verification and with journal/alert; quarantined entries never selected; leaving quarantine suspends until S-C passes; exclusion never hides an S-B Fail. IX-45 now also covers the deep sweep |
| `EnterpriseForms.ArchitectureTests` | **92/92** | Unchanged count |
| `EnterpriseForms.Api.ContractTests` | **527/527** | Adds `SearchIndexBuilderRunnerTests` (20): 12 classification cases, bounded backoff, transient retry with growing delays and reset, credential refusal stops without retry, unexpected failures never escape, cancellation during a pass and during the delay (each under 5 s), unconfigured idle, a failed backfill never reaches the attestation |
| `EnterpriseForms.Integrity.Tests` | **60/60** | — |

Docker, **fresh images** (`-Fresh`: `down -v`, images rebuilt with the final Worker and Authority code):

| Class | Result | Note |
|---|---|---|
| `SearchIndexBuilderStackTests` | **11/11** (run 2; run 1 was 10/11: the IX-27 test searched only the last 300 worker log lines, and the worker's database logging pushed the new fail-closed line out of that window. Test fix: wait for the line in a 20,000-line window. No product change between the two runs) | |
| `SearchIndexFoundationTests` (IX-1) | **9/9** | |
| `SpiceDbBehaviourContractTests` | **7/7** | CT-1..CT-6 and IX2-CT-07 |
| `ProtectionReconciliationTests` | **14/14** | G19 census now uses the shared verification core (D-IX2-1) |

Stage A (SEC-P01..SEC-P12, 13/13, validator PASS) is the run of section 3.4. Not rerun: the closure changes touch no Stage A requirement (the search index feature, the builder Worker loop and a test rename).

### 8.4 Register update

R-13 (quarantined record vs S-C deadline) and R-14 (CT-7 identifier) are **closed** by C-130. R-11 stays open: IX-19 is accepted as partial and the full restore and journal replay remain with G14. All other items, R-01 to R-18, are unchanged, including the 20 ms p95 gate (FAIL, carried to IX-7). New: **R-19**, Stage B on the IX-2 code, carried to the next owner-authorized release test run (C-131).

### 8.5 Recommendation

IX-2 is ready for **conditional owner acceptance**:
- its functions and gate tests pass on the final code, with focused coverage of every changed component;
- IX-19 is partial, as accepted;
- the register items stay carried to their stages;
- the full Stage B regression has not been run on this code and is a release-time item (R-19), never reported as passed.

IX-3 and later remain unauthorized.
