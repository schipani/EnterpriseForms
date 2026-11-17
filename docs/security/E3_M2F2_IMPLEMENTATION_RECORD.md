# E3-M2f-2 — Authority Envelope Capability Enforcement: Implementation Record

**Status (2026-11-16):**

| Item | State |
|---|---|
| Increment | E3-M2f-2, as specified in `E3_M2F_CONTRACT_CLOSURE.md` §3–§9 and §12.2 (governing), OD-M2f-01..10, AM-F1 |
| Authorization | Owner, 2026-11-16: Authority capability enforcement only. No commit, push, merge, deployment, Azure call, Stage B run, Envelope v2 acceptance or persistent database change |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `6e0ee1d4029cef953a03aa0dddb1da8b65297db5` (E3-M2f-1), good SSH ED25519 signature, clean tree |
| Commit | Owner-accepted 2026-11-16 (§14); exactly one signed local commit authorized (OA-M2f2-07). Not pushed or merged |
| Environment | Development/Testing. In-process unit, contract, host and architecture tests only. No PostgreSQL, Docker stack, Vault, SpiceDB, Stage B or E3-M2f-2G gate was run |
| Review | Owner-led. Not an independent security review and not a certification. V2-IND-01 is still required before production |

---

## 1. Baseline verification

| Check | Result |
|---|---|
| Branch and HEAD | `e3/m1-envelope-v2-core` at `6e0ee1d4029cef953a03aa0dddb1da8b65297db5` |
| Signature | `G`: good `git` signature, ED25519 `SHA256:t6bvk1LKAW33NACJ+JrCxzj1JcgKGHY/RYT4XdX9HJg` |
| Working tree | Clean |
| E3-M2f-0 / E3-M2f-1 records | `E3_M2F_CONTRACT_CLOSURE.md` (committed `ba7b957`) and `E3_M2F1_IMPLEMENTATION_RECORD.md` (committed `6e0ee1d`) present |
| Business capability header | `EnvelopeCapability.Declare` in the send paths of the three content clients (source) |
| CARV and floors | `ContentAcceptanceRules.CurrentVersion = 3`; floor migrations 1, 2 and 3 in `FloorMigrations/` (source) |
| Release B fence | `RequireContentAcceptanceFloorAsync` first in Prepare and Commit (source) |
| Ledger protections | P-1, P-2, DK-4..DK-8, P-4, L-13 unchanged (source; no ledger, migration or trigger file changed) |
| Envelope v2 acceptance | Default off; no configuration file in the repository sets `Content:EnvelopeV2AcceptanceEnabled`; the switch still requires Release B `RequiredFloor = 3` |

## 2. Requirements traceability

| Requirement | Implementation | Verification |
|---|---|---|
| CAP-S1 header name, one constant per side | `ClientEnvelopeCapability.HeaderName` (Authority Application) = `EnvelopeCapability.HeaderName` (business) | M2F-31 (architecture) |
| CAP-S3/S5/S6 grammar; missing/unknown ⇒ 1 | `ClientEnvelopeCapability.FromHeaderValues`: exactly one value, exactly `"2"` ⇒ 2; anything else ⇒ 1 | M2F-06 (19 rows); host test with 9 header variants on 7 endpoints |
| CAP-S7 not in digest/identity; never echoed | Parsed in the Api from the request headers; passed as a separate argument; not part of any request body or digest; responses and logs carry no header value | M2f-1 M2F-05 unchanged; refusal detail is fixed |
| CAP-F1 `E = max(D, O)` | `EnvelopeCapabilityFloor.RequireAsync` | M2F-07 (12-row matrix) |
| CAP-F2 any binding state | Existence query has no operation-state filter | M2F-08 (Prepared, Committed, Aborted, expired) |
| CAP-F3 short-circuit | `C < D` refuses, `C ≥ max(D, 2)` admits, both without the query | M2F-22: query counts in the matrix; level 2 with an unreadable ledger; host tests (level 2 and `C < D` make no existence call) |
| CAP-F4 under the organization lock | Called with the locked state in the entry point's scope | M2F-25 (architecture position); host test (lock precedes the query) |
| CAP-F5 store failure refuses | Any exception except the caller's cancellation ⇒ `503 AUTHORITY_NOT_READY`, fixed detail | M2F-24 |
| CAP-F6 only refuses, writes nothing | No store member other than the existence port; no state | Architecture shape test; store fingerprints in every refusal test |
| CAP-F7 no setting lowers E | No organization, form, user or request input reaches the floor | M2F-09; no new configuration except §9 |
| FB-1 | Same organization row lock as Prepare's binding insert; no cache | Source and architecture order; no-stale-negative test; real concurrency is M2F-34 (E3-M2f-2G) |
| AM-F1 #1 boolean only | `IAuthorityScope.OrganizationHasDataKeyBindingsAsync` → `Task<bool>`; EF `AnyAsync` | M2F-20 in-process: exactly one `SELECT EXISTS (SELECT 1 FROM authority.content_data_key_bindings AS c WHERE c.organization_id = @p)`, one parameter, nothing tracked |
| AM-F1 #2/#3 sole caller, seven sites | Only `EnvelopeCapabilityFloor.RequireAsync` | M2F-21 (architecture, both directions) |
| AM-F1 #4 short-circuit | As CAP-F3 | M2F-22 |
| AM-F1 #5 transaction and lock | As CAP-F4 | M2F-25 |
| AM-F1 #6 use | Result feeds only the refusal; other ledger ports only on Prepare/Commit | M2F-23 (4 ports) |
| AM-F1 #7 failure | As CAP-F5 | M2F-24 |
| AM-F1 #8 no grant change | Uses the existing runtime `SELECT` (closure L-8); no migration | No database object changed (§10) |
| CAP-O1..O5 ordering | §4 | M2F-10..18, M2F-25, host test |
| CAP-O6 writes nothing | §4 | M2F-26: store fingerprint (rows, journal, heads, operations, revisions, scope commits, index entries and deep-check stamps) unchanged after every refusal |
| CAP-O7 one implementation | One instance, held by `ContentService.CapabilityFloor`; census and builder use it | M2F-21; host test (`Assert.Same`) |
| CAP-E1/E2 error | `409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, `retryable = false`, fixed detail, same for both floors | M2F-27; host tests |
| CAP-E3 business mapping | §5 | M2F-28 (host + predicates); architecture source pins; mutations MF2-27..31 |
| CAP-E4 log and metric | §6 | Observer test (outcome, level, organization only) |
| §9 deployment floor | `Content:MinimumBusinessEnvelopeCapability`: absent ⇒ 1, `"1"`, `"2"`, anything else refuses the start | Unit parse tests (15 rows); M2F-29 host tests (6 invalid values refuse the start; 3 valid values reach every service) |
| INV-4 readiness indicator | Readiness detail ends `envelope capability floor enforced; deployment floor N` | `Description` unit-tested; the composed readiness string was **not** executed (needs a ready provider, store, SpiceDB and archive) |
| CAP-E1 OpenAPI | Header parameter and 409 on exactly the seven operations; document regenerated | M2F-30 (architecture, reads the committed document) |
| M2F-32 non-content endpoints | The check is called from the seven entry points only | M2F-32 (head, history, user catalogue, catalogue validation in a bound organization: no refusal, no ledger read) and M2F-21 |
| M2F-33 mutations | §8 | 32 / 32 detected |

Not in this increment: M2F-34..M2F-40 (E3-M2f-2G), refusal evidence RE-1..RE-6 (E3-M2f-3).

## 3. Changed components

**Authority (product):**

| File | Change |
|---|---|
| `Application/Features/Content/EnvelopeCapabilityFloor.cs` | **New.** `ClientEnvelopeCapability` (header name, two recognized levels, parser), `EnvelopeCapabilityFloor` (deployment-floor parse, `RequireAsync`), `IEnvelopeCapabilityObserver`, outcome constants |
| `Application/Common/AuthorityException.cs` | Error code `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT` |
| `Application/Common/Ports/IAuthorityStore.cs` | `OrganizationHasDataKeyBindingsAsync(Guid, CancellationToken) → Task<bool>` (AM-F1) |
| `Infrastructure/Persistence/AuthorityStore.cs` | EF implementation: `ContentDataKeyBindings.AnyAsync(b => b.OrganizationId == id)` on the scope's context and transaction |
| `Application/Features/Content/ContentService.cs` | Optional `EnvelopeCapabilityFloor` dependency, exposed as `CapabilityFloor`; optional `ClientEnvelopeCapability?` (null ⇒ level 1) on Prepare, Commit, Verify and batch Verify; the check at the four positions of §4 |
| `Application/Features/Catalogue/ProtectionReconciliationService.cs` | Optional capability on `CensusAsync`; the check after the lock, through `content.CapabilityFloor` |
| `Application/Features/SearchIndex/SearchIndexBuilderService.cs` | Optional capability on `BackfillAsync` and `DeepVerifyAsync`; the check after the lock |
| `Api/Composition/InternalAuthentication.cs` | `EnvelopeCapability(HttpContext)`: every header instance to the shared parser |
| `Api/Features/Content/ContentEndpoints.cs`, `Catalogue/CatalogueEndpoints.cs`, `SearchIndex/SearchIndexBuilderEndpoints.cs` | The seven endpoints pass the parsed header; `.WithEnvelopeCapabilityContract()` documents it |
| `Api/Composition/EnvelopeCapabilityComposition.cs` | **New.** Registration (floor from `IConfiguration`, resolved lazily), startup guard (log 9252; invalid value refuses the start), observer (log 9250/9251, meter), OpenAPI contract helper |
| `Api/Composition/SigningProviderPolicy.cs` | Readiness detail appends the floor description |
| `Api/Program.cs` | `AddEnvelopeCapabilityFloor()` |
| `contracts/openapi/authorization-authority-internal-v1.json` | Regenerated by the build: header parameter and 409 on the seven operations only |

**Business (product; CAP-E3 only, no HTTP client changed):**

| File | Change |
|---|---|
| `Application/Common/Authority/EnvelopeCapabilityRefusal.cs` | **New.** Code constant and `Is(AuthorityClientException)` |
| `Api/Composition/BusinessProblemHandler.cs` | The code maps to `503 SERVICE_UNAVAILABLE`, `retryable = false`, fixed text, before the generic Authority case |
| `Application/Features/FormDesigner/ProtectionMigration/FieldProtectionMigrationService.cs` | The refusal makes the item `Failed` (`Retry: false`) before the generic non-retryable clause, which would otherwise start bounded fresh-operation attempts |
| `Application/Features/FormDesigner/ProtectionMigration/FieldProtectionReconciliationService.cs` | The refusal finishes the reconciliation `Incomplete` with the code; without this clause the exception escaped to the worker host, which polls again every 250 ms or more |

**Tests:** new `EnvelopeCapabilityFloorTests`, `BindingExistenceQueryTests`, four partial-class files (`ContributorWriteProtocolTests`, `SearchCatalogueTests`, `ProtectionReconciliationTests`, `SearchIndexBuilderTests` `.EnvelopeCapability.cs`), `EnvelopeCapabilityEnforcementHostTests` (contract), `EnvelopeCapabilityEnforcementBoundaryTests` (architecture). Adjusted, with no assertion change: `partial` on three test classes; the in-memory store (`OrganizationHasDataKeyBindingsAsync`, a query counter, failure injection); the two fake scopes (`CommandPlannerTests`, `ArchiveProcessorTests`); an optional harness capability in `ContributorWriteProtocolTests`; and level-2 declarations in three E3-M2e-1 ledger test flows that bind an organization and then commit (§7.3). The M2f-1 architecture pin "no Authority assembly references the header yet" is replaced, as the M2f-1 record required (§7.4).

**Documentation:** this record; `E3_M2B1_CARV_OPERATIONS.md` §10; `spec-files.json` and `SHA256SUMS.txt` regenerated.

## 4. Enforcement ordering by entry point

Line numbers are in the working tree. Every position is pinned by M2F-25 from IL, and every removal or move is detected (§8).

| # | Entry point | Before the check (unchanged, cannot quarantine or journal) | Check | First step after the check |
|---|---|---|---|---|
| 1 | `ContentService.PrepareCoreAsync` | Idempotency-key length, request digest (pure); scope; `LockOrganizationAsync` (:92); Release B fence (:96) | :98 | `ResolveActorAsync` (:99), then gate, replay, snapshot binding |
| 2 | `ContentService.CommitCoreAsync` | Test-only fault pause; scope; lock (:338); Release B fence (:342) | :344 | Organization gate (:345), then operation, actor, replay, expiry/CARV aborts, staged verification |
| 3 | `ContentService.VerifyProjectionAsync` | Scope; lock (:794) | :797 | `ActorResolver.BindAsync` (:798), then gate, `PinAsync`, `VerifyRecordCoreAsync` |
| 4 | `ContentService.VerifyProjectionsAsync` | Batch shape validation; scope; lock (:859) | :862 | `ActorResolver.BindAsync` (:863) |
| 5 | `ProtectionReconciliationService.CensusAsync` | Batch validation; scope; snapshot lookup and expiry (read-only, before the lock as in the baseline); lock (:61) | :64 | Organization gate (:65), decision, census verification |
| 6 | `SearchIndexBuilderService.BackfillAsync` | Dark-mode and batch checks; scope; lock (:121) | :124 | Organization gate (:125), projection, downgrade check, verification |
| 7 | `SearchIndexBuilderService.DeepVerifyAsync` | Dark-mode and batch checks; scope; lock (:335) | :338 | Organization gate (:339), deep-check bookkeeping, verification |

Endpoint-level authentication (workload credential, delegated user assertion) runs before the service, as before. For a refused level-1 caller, input-shape errors, an unknown or expired census snapshot, a disabled index and authentication failures therefore still take precedence; the gate, authorization, replay and every verification come after the capability refusal (CAP-O5). For level-2 callers the refusal order of every existing check is unchanged.

## 5. Header parsing contract

- Header `EnterpriseForms-Envelope-Capability`, read from every instance on the request (`HttpRequest.Headers[...]`).
- Level 2 only when there is exactly one instance and its value is exactly the ASCII string `2`. Everything else is level 1, including absent, empty, `1`, `02`, `+2`, ` 2`, `2 `, `3`, `0`, text, full-width digits, `1, 2`, `2,2`, two instances `2` and `2`, and two instances `1` and `2`. No value yields more than 2 (CAP-S6, R-M2f-10).
- A null argument at the Application boundary is level 1, the same as no header. Only the Api passes a parsed value (architecture test), and the seven Api call sites are the only product callers.
- Transport whitespace: surrounding whitespace is not part of an HTTP field value (RFC 9110 §5.5), so a server may deliver `2 ` as `2`. The parser treats a padded value as level 1. Transport-level trimming by Kestrel was **not** tested here.

## 6. Floor evaluation, transaction and locking

- `C` = parsed level; `D` = deployment floor (§9 of the closure); `O` = 2 if the organization has any binding row, else 1.
- `C < D` ⇒ refuse (no query). `C ≥ max(D, 2)` ⇒ admit (no query). Otherwise one `SELECT EXISTS (… WHERE organization_id = @p)` in the entry point's transaction, after `SELECT … FOR UPDATE` on the organization row. True ⇒ refuse; false ⇒ admit; any failure ⇒ `503 AUTHORITY_NOT_READY`. The caller's own cancellation propagates and is never an admit.
- **FB-1:** a Prepare inserts bindings only after the same organization row lock, in its own transaction. A level-1 decision that saw "no binding" therefore either completed before that transaction took the lock or started after it committed. Nothing is cached, so a later evaluation in the same scope sees a binding added meanwhile (tested in-memory). A level-1 Prepare in an unbound organization stays admitted even if it is the Prepare that creates the first binding (possible only with acceptance on). Its Commit is a later locked transaction and is refused, as closure §4.2 point 4 expects. That path is not testable without acceptance and belongs to M2F-34.
- **Lock order:** the query takes ACCESS SHARE on `content_data_key_bindings` only, after the organization row lock and (Prepare, Commit) after the fence's locks on the migration history and operations table. That is the same order as the existing L-13 ledger lock. The floor-3 migration locks the operations table first, and ACCESS SHARE conflicts only with ACCESS EXCLUSIVE, which no runtime path takes on the ledger. No new lock-order edge was found by source analysis. PostgreSQL behaviour was not exercised (§10).
- **No commit after refusal:** the refusal is thrown before any tracked change. The scope is disposed without commit, so the EF transaction rolls back. The in-memory `ScopeCommitCount` is unchanged in every refusal test.
- **Index:** `ix_content_data_key_bindings_record (organization_id, form_record_id)` has the organization as its leading column. Index use under PostgreSQL was not observed here.

## 7. Error, retry and observations

### 7.1 Authority

`409`, `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, `retryable: false`, detail `the calling workload's envelope capability is insufficient for this request; a current business build is required` (no identifier, level, floor or binding information). The response is identical for the deployment floor and the organization floor. Nothing is written: no operation, abort, quarantine, journal entry, revision, index entry, deep-check stamp or census result.

### 7.2 Business (CAP-E3)

| Path | Behaviour | Verification |
|---|---|---|
| Users (every handler that lets the Authority exception propagate) | `503 SERVICE_UNAVAILABLE`, `retryable: false`, fixed text, no Authority code, detail or identifier | Behaviour (host test through the real Authority and the real business client) |
| `IsAbandonedOperation`, `IsUnusableOperationKey`, `IsRetryableConflict`, `FieldSearchRetryPolicy.IsRetryable`, incident classifier | Excluded; the predicates are allow-lists and were not changed | Behaviour (host test); mutations MF2-27/28 |
| Field-protection migration item | `Failed` with the code, `Retry: false`: no further attempt and no fresh operation | Structural pin and mutation MF2-29; the worker was not run |
| Protection reconciliation | Finishes `Incomplete` with the code: no re-run at poll rate | Structural pin and mutation MF2-30; the worker was not run |
| Search-index builder | **Unchanged.** By source analysis the workflow catches 409 per form or entry (`FORM_SKIPPED`, `inconclusive`) and re-reads on its next scheduled round; it creates no operation and writes nothing | Not tested |

### 7.3 Observations for owner review

| ID | Observation |
|---|---|
| **O-1** | **Business scope.** The brief says not to modify the business clients; none of the four HTTP clients changed. CAP-E3 needed four business changes: a constant, the problem-handler case, and the two worker clauses. The problem handler lives in the business Api, not the Application named in closure §12.2. Without the worker clauses, the generic non-retryable path would start bounded fresh-operation attempts (migration) and a poll-rate retry loop (reconciliation). Owner confirmation requested. |
| **O-2** | **Search-index builder left unchanged.** It already skips the form or entry and re-reads on its next scheduled round, which is cadence rather than a retry or new-Prepare loop. Making it stop instead would change its scheduling semantics, which is beyond E3-M2f-2. Owner decision if a stop is wanted. |
| **O-3** | **Existing tests that bind organizations.** Twelve E3-M2e-1 ledger tests insert bindings and then commit without a declaration. They were correctly refused, and failed with `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`. They model a current business build, so their commit helper and two harness flows now declare level 2. No assertion changed; all pass. |
| **O-4** | **Replaced M2f-1 pin.** `TheHeaderLiteralExistsOnlyInTheDeclarerAndNoAuthorityAssemblyReferencesItYet` became `TheHeaderLiteralExistsOnlyInTheDeclarerOnTheBusinessSide`. The Authority-side locations are pinned by the new boundary test, as the M2f-1 record required (its §3 and O-3). |
| **O-5** | **M2f-1 compatibility test still passes, for a different reason.** `TheExistingAuthorityGivesIdenticalOutcomesWithAnyCapabilityValueOrNone` uses an unreachable store, so the lock fails before the check and every header variant still gets the same 503. Its summary ("no E3-M2f-2 enforcement") is now historical. It was not edited. |
| **O-6** | **Metric tag.** Per CAP-E4 the counter carries `organization_id`. The existing `ProjectionIntegrity` meter carries no organization identifiers. The cardinality is bounded by the organization count (PB-09: 25). |
| **O-7** | **Gate harnesses.** `tools/experiments/*` (outside the CI solution) call the services without a declaration. Re-running the E3-M2e-2 gate on this tree would be refused after the first binding. E3-M2f-2G must declare level 2 there. They were not changed. |
| **O-8** | **Optional parameters.** The capability argument is the last optional parameter, after the `CancellationToken`, so the 18 existing direct constructions and all callers in tests and tools still compile. Null means level 1 (fail closed), and the Api is pinned to pass the parsed header. |

### 7.4 Retry behaviour summary

Non-retryable at the Authority. Never added to a retryable or fresh-operation set. Terminal in the migration and reconciliation workers. Service-unavailable to users. No automatic fresh operation anywhere in the business build containing this increment.

## 8. Test results

Test executables were run directly. All results are from the final tree after a `--no-incremental` rebuild, except the full contract suite, which ran on the same sources before the mutation runs.

| Suite | Result |
|---|---|
| Authority (`AuthorizationAuthority.Tests`) | **1108 total: 1104 passed, 0 failed, 4 skipped** (the same four real-Vault skips as before). New: 75 (`EnvelopeCapabilityFloorTests` 60, entry-point tests 13, `BindingExistenceQueryTests` 2) |
| Architecture | **173 / 173** (157 existing, of which 1 replaced (O-4), plus 16 new) |
| Contract (full) | **948 total: 935 passed, 1 failed, 12 skipped** (real-Vault classes, as before). The failure is **TS-M2f-01** (`PhaseC7FieldSearchExecutionTests.AChangedSnapshotIsRetriedWithinTheBoundedBudgetAndAPendingCreationNeverBlocks`, expected 6 attempts, actual 5 under full-suite load), the same tracked timing test recorded at E3-M2f-1. Rerun of its class in isolation five times: **12 / 12 each**. New: 13 host tests, all passed in the full run |
| Capability contract classes (after the mutation runs) | 24 / 24 (13 enforcement host + 11 M2f-1 emission) |
| Integrity | 73 / 73 |
| Platform console | 23 / 23 |
| Release build `EnterpriseForms.All.slnx`; `SecurityTests`, `StageBTests` projects | Solution: 0 warnings, 0 errors. The two projects: 0 errors (warnings are errors in this repository). **Not run** (the security tests need the Docker stack; Stage B is not authorized) |
| E3-M2f-2G (M2F-34..M2F-40), PostgreSQL, Docker, Stage B | **Not run** |

## 9. Enforcement-removal results (M2F-33)

A driver in the session scratchpad (not in the repository) applied each mutation with exact single-match anchors, built the affected test projects, ran the named suites, and restored the files in `finally`. SHA-256 was verified after every restore. Afterwards the tracked diff hash equalled the pre-mutation hash, the projects were rebuilt with `--no-incremental`, and the suites were rerun green (§8).

| ID | Mutation | Detected by (failing tests) |
|---|---|---|
| MF2-01..07 | Remove the check from Prepare, Commit, Verify, batch Verify, census, backfill, deep-sweep verify (one each) | Authority (1–4) and architecture (2) for each |
| MF2-08 | Remove the shared check (`RequireAsync` returns at once) | Authority (28) |
| MF2-09 | Move the Verify check after `VerifyRecordCoreAsync` | Authority (1), architecture (1) |
| MF2-10 | Move the Prepare check after snapshot binding | Authority (2), architecture (1) |
| MF2-11 | Bypass the organization floor (ignore the existence result) | Authority (19) |
| MF2-12 | Bypass the deployment floor (D treated as 1) | Authority (5) |
| MF2-13 | Missing capability (null) treated as level 2 | Authority (16) |
| MF2-14 | Absent header parsed as level 2 | Authority (2) |
| MF2-15 | Unknown header value parsed as level 2 | Authority (12) |
| MF2-16 | Duplicate headers: first value wins | Authority (2) |
| MF2-17 | Level-2 short-circuit bypassed (level 2 reads the ledger) | Authority (30) |
| MF2-18 | `C < D` short-circuit dropped | Authority (5) |
| MF2-19 | Deployment comparison inverted | Authority (39) |
| MF2-20 | Admit on store error | Authority (1) |
| MF2-21 | Refusal marked retryable | Authority (22) |
| MF2-22 | Lenient deployment-floor parse (invalid ⇒ 1) | Authority (12) |
| MF2-23 | Release B fence moved after the check (Prepare) | Authority (1), architecture (1) |
| MF2-24 | EF existence query always false | Authority (2) |
| MF2-25 | EF existence query not keyed on the organization | Authority (2) |
| MF2-26 | Verify endpoint does not pass the parsed header | Architecture (1), contract host (2) |
| MF2-27 | Business: refusal added to the migration's retryable conflicts | Contract host (1) |
| MF2-28 | Business: refusal treated as an abandoned operation | Contract host (1) |
| MF2-29 | Business: migration terminal clause removed | Architecture (1) |
| MF2-30 | Business: reconciliation terminal clause disabled | Architecture (1) |
| MF2-31 | Business: user-facing mapping disabled | Architecture (1), contract host (1) |
| MF2-32 | OpenAPI contract dropped from one endpoint (the build regenerates the document) | Architecture (1); the generated document was also restored byte-identical |

**32 / 32 detected; every file restored byte-identical.** Weakest detections: MF2-20 (one test) and MF2-29/30 (structural pins only, §7.2).

## 10. Database and infrastructure

- **No migration, table, column, index, trigger, grant, CARV version or floor** (F0, AM-F1 #8). The EF model snapshot is unchanged. G08 inventory unchanged: no new database object.
- The existence query uses the existing ledger table, its existing index and the runtime role's existing `SELECT`.
- **Not verified:** the PostgreSQL plan and index use, behaviour under the real organization lock and concurrent first-binding Prepares (M2F-34), privileges under the runtime role, or Azure PostgreSQL (R-CARV-18). All of these belong to E3-M2f-2G and later gates.

## 11. Known limitations

- The in-memory store does not serialize transactions. FB-1 is argued from source and pinned by order, not proven under concurrency (M2F-34, E3-M2f-2G).
- G-2 is demonstrated in-process with re-encoded canonical bytes, the mechanism of closure §6.3. It is not demonstrated with a real pre-M2b-2 business build and a real schema-2 head (M2F-35, E3-M2f-2G).
- Worker terminal behaviour is pinned structurally, not run.
- The composed readiness text was not executed. Kestrel whitespace trimming was not tested.
- Refusals leave only non-durable signals (logs, metric). RE-1..RE-6 are unmet until E3-M2f-3 (R-M2f-9).

## 12. Open risks and production gates (unchanged unless stated)

- Persistent use requires everything in closure §11.5: E3-M2f-2 acceptance, E3-M2f-2G evidence (OPEN-M2f-4 authorization), E3-M2f-3 (OPEN-M2f-1), the INV-1/INV-2 inventory (INV-3: CARV markers are insufficient), OA-FE1-2 where a floor is raised, business instances at E3-M2f-1+ before the first binding, and the deployment floor set under PB-11.
- Still open: BD-1, BD-2 (with the FB-1 obligation), F-E2, R-A1-1..R-A1-5, TS-M2f-01, OPEN-M2f-1..4, V2-IND-01, RB-CARV-1 production closure, OA-FE1-2, PB-08, PB-11, PB-12, Azure PostgreSQL verification, INV-1..INV-3, and R-M2f-1..R-M2f-10.
- Not implemented: E-9, E3-M2g, E3-M3, E3-M4, refusal evidence, any CARV change or Floor 4.

## 13. Verification classes

| Class | What it covers here |
|---|---|
| **In-process (executed)** | Authority unit and entry-point tests on the in-memory store; the EF store method with a never-opened connection and captured SQL; the real Authority host pipeline (`WebApplicationFactory`) with a scripted store; the real business client against it and the business problem handler; architecture tests over compiled IL and source; the contract, integrity and platform-console suites (§8) |
| **Compile-only (built, not run)** | Release build of `EnterpriseForms.All.slnx`; the `SecurityTests` and `StageBTests` projects |
| **Not executed: PostgreSQL** | Existence-query plan and index use, runtime-role privileges, behaviour under the real organization lock and concurrent first-binding Prepares (M2F-34), real G-2 with a pre-M2b-2 business build (M2F-35), and M2F-36..M2F-40: all E3-M2f-2G. Azure PostgreSQL (R-CARV-18): later gate |
| **Not executed: Stage B** | Not run (not authorized); the workers' terminal handling is pinned structurally only |
| **Open production gates** | §12 and closure §11.5, unchanged by this increment |

## 14. Owner acceptance (2026-11-16)

Owner-led conditional acceptance for Development/Testing. **Not** an independent review, not a certification and not production readiness. It closes no production, Staging, Azure or independent-review gate.

| ID | Decision |
|---|---|
| **OA-M2f2-01** | The Authority capability enforcement is accepted |
| **OA-M2f2-02** | The additional business API and worker terminal-refusal handling (O-1) is accepted |
| **OA-M2f2-03** | The existing search-index builder skip-and-revisit behaviour (O-2) is accepted without change |
| **OA-M2f2-04** | The twelve E3-M2e-1 ledger tests declaring level 2 without assertion changes (O-3) are accepted |
| **OA-M2f2-05** | Capability-aware gate harness updates (O-7) are **required** in E3-M2f-2G |
| **OA-M2f2-06** | **TS-M2f-01** is retained as an open contract-suite failure; the full contract suite stays recorded as 935 passed, 1 failed, 12 skipped |
| **OA-M2f2-07** | Exactly one signed local commit is authorized, containing only the approved E3-M2f-2 changes |

**File scope at commit: 40 paths** (28 modified, 12 new), exactly those listed in §3, the regenerated OpenAPI document, the runbook, this record, `spec-files.json` and `SHA256SUMS.txt`. The completion report's "13 new" was a miscount; Git lists 12 new files. No CARV, floor, ledger, migration, acceptance-switch, business HTTP client, configuration, CI workflow or `tools/` file is included.

**Final verification before the commit (no code change since §8):** the focused enforcement classes were rerun on the same binaries: `EnvelopeCapabilityFloorTests` + `BindingExistenceQueryTests` 62 / 62, entry-point tests 13 / 13, architecture boundary 16 / 16, contract host 13 / 13. The evidence of §8 and §9 is preserved unchanged.

**Evidence preserved as reported:** Authority 1104 passed, 0 failed, 4 skipped (1108); architecture 173 / 173; contract 935 passed, **1 failed** (TS-M2f-01), 12 skipped; integrity 73 / 73; platform console 23 / 23; enforcement-removal 32 / 32 detected and restored; specification manifest 257 files, strict pass.

**Not authorized:** push, merge, deployment, E3-M2f-2G, E3-M2f-3, PostgreSQL, Docker, Azure, Stage B, Envelope v2 acceptance, persistent database changes, any CARV, floor, ledger or migration change, BD-1, BD-2, F-E2, a TS-M2f-01 fix, or any expansion of the business-client scope.

## 15. Not claimed

- No commit, push, merge, deployment, Azure call, Stage B run, PostgreSQL or Docker run, or Envelope v2 acceptance.
- No persistent activation; no closure of BD-1, BD-2, F-E2, R-CARV-21 or any production gate.
- No independent review, certification or production-readiness claim.
- Not that the full contract suite passes: it is recorded as 935 passed, **1 failed** (TS-M2f-01), 12 skipped.
