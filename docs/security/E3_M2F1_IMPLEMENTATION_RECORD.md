# E3-M2f-1 — Business Envelope Capability Emission: Implementation Record

**Status (2026-11-16):**

| Item | State |
|---|---|
| Increment | E3-M2f-1, as specified in `E3_M2F_CONTRACT_CLOSURE.md` §3 and §12.1 (governing) |
| Authorization | Owner, 2026-11-16, after acceptance of E3-M2f-0 (OD-M2f-01..10, AM-F1) |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `ba7b9571f134228681b4bbd75998e17969eec92c` (E3-M2f-0, signed), parent `0be5f52` |
| Commit | Owner-accepted 2026-11-16 (§9); exactly one signed local commit authorized (OA-M2f1-03). Not pushed or merged |
| Environment | Development/Testing. Focused tests only; no Stage B, Azure, persistent database, Docker stack or Envelope v2 activation |
| Review | Owner-led. Not an independent security review and not a certification |

---

## 1. What was implemented

The three content workload clients declare `EnterpriseForms-Envelope-Capability: 2` on **every** Authority request (CAP-S2). The reconciler client does not.

| File | Change |
|---|---|
| `src/backend/EnterpriseForms.Infrastructure/Authority/EnvelopeCapability.cs` | **New.** `HeaderName` and `DeclaredLevel = "2"` constants; `internal static Declare(HttpRequestMessage)` removes any present value and adds exactly one `2`. No configuration, options, environment or state. |
| `src/backend/EnterpriseForms.Infrastructure/Authority/AuthorityHttpClient.cs` | One line in the single private `SendAsync`, after the existing headers and before the body is attached |
| `src/backend/EnterpriseForms.Infrastructure/Authority/ProtectionVerifierHttpClient.cs` | One line in `SendAsync`, same position |
| `src/backend/EnterpriseForms.Infrastructure/Authority/SearchIndexBuilderHttpClient.cs` | One line in `SendAsync`, same position |
| `tests/features/EnterpriseForms.Api.ContractTests/EnvelopeCapabilityEmissionTests.cs` | **New**, 11 tests (§3) |
| `tests/architecture/EnterpriseForms.ArchitectureTests/EnvelopeCapabilityEmissionBoundaryTests.cs` | **New**, 3 IL-level tests (§3) |

**Properties of the design:**
- **Fixed build constant (CAP-S3, CAP-S4).** The value is a `const` of the compiled build. No caller sees the request message: each client builds it inside its private send path.
- **Not overridable (CAP-S7).** No options class, configuration key, user, organization or request can change it. A pre-set value on the message is replaced, never added to.
- **Outside the body (CAP-S7).** Prepare digests, signed content, idempotency keys and cryptographic payloads are unchanged.
- **Every attempt carries it.** There are no HTTP-level retry or resilience handlers in the business hosts. Every retry is a new call through the same send path, which builds a fresh request with the header.
- **Unchanged:**
  - the Authority (no source change; enforcement is E3-M2f-2);
  - CARV, the floors, the ledger, the acceptance switch, configuration, migrations and databases;
  - E-9, M2g, M3 and M4; BD-1, BD-2 and F-E2;
  - refusal evidence.

## 2. Request paths covered

Each public method was invoked through reflection with synthetic arguments. The test asserts that each method sends exactly one request and that the request carries exactly the single value `2`. A method added later is covered automatically.

| Client (workload credential) | Methods | Content entry points among them (closure §6.1) |
|---|---|---|
| `AuthorityHttpClient` (business-api; API and Worker) | 32: CheckPermission, GetFormAccess, CheckPermissionsBatch, DiscoverAuthorizedRecords, SubmitSecurityCommand, GetSecurityCommandStatus, PrepareContentChange, CommitContentChange, GetCommittedContentRevision, VerifyRecordProjection, IssueSearchCatalogue, ValidateSearchCatalogue, VerifyRecordProjections, GetContentHistory, GetCommittedContentRevisionForReprotection, PrepareReprotection, CommitReprotection, GetFieldSearchCapabilities, GetSecurityAdministrators, GetRoster, GetOwnerRecoveryNotice, ObjectToOwnerRecovery, GetFieldSearchAdministration, RebuildSearchProjection, Search, Page, ListRecords, RecordSearchExecution, GetTestStackIdentity, CreateSession, ValidateSession, RevokeSession | Prepare (user and reprotection), Commit (user and reprotection), Verify, batch Verify |
| `ProtectionVerifierHttpClient` (field-protection-verifier; Worker) | 5: IssueMigrationCatalogue, ReadMigrationCatalogue, TakeCensus, Attest, GetCoverage | Census |
| `SearchIndexBuilderHttpClient` (search-index-builder; Worker) | 6: GetWork, ReadPopulation, Backfill, Attest, GetDeepSweepWork, DeepVerify | Backfill, deep-sweep verify |
| `TeamMembershipReconcilerHttpClient` | Lookup | **Not covered by design** (no content); asserted absent |

All seven content entry points of closure §6.1 are covered.

## 3. Tests

**New contract tests (`EnvelopeCapabilityEmissionTests`, 11):**

| Test | Closure ID | Proves |
|---|---|---|
| `EveryAuthorityHttpClientRequestDeclaresCapabilityTwoExactlyOnce` | M2F-01 | All 32 methods; one request each; value exactly `2` |
| `EveryProtectionVerifierRequestDeclaresCapabilityTwoExactlyOnce` | M2F-02 | All 5 methods |
| `EverySearchIndexBuilderRequestDeclaresCapabilityTwoExactlyOnce` | M2F-03 | All 6 methods |
| `TheTeamMembershipReconcilerDoesNotDeclareTheCapability` | M2F-04 | Exclusion |
| `ThePrepareBodyIsExactlyTheRequestAndTheAuthorityDigestDependsOnTheBodyOnly` | M2F-05 | The body equals the client serialization of the request and has no capability member. The Authority digest (`CanonicalJson.Digest` over the deserialized body, as in `ContentService.PrepareCoreAsync`) is identical to the digest computed with no HTTP at all. Idempotency keys are passed through unchanged |
| `ARetriedPrepareDeclaresTheCapabilityOnEveryAttemptWithAnUnchangedBodyAndIdempotencyKey` | retry | 503 then retry: both attempts declare `2`; same body bytes and same Idempotency-Key; a fresh correlation id per attempt (existing behaviour) |
| `TheDeclaredCapabilityIsAFixedBuildConstant` | CAP-S7 | Constants; `Declare` internal with one parameter; no options property about capability or envelope |
| `DeclaringReplacesAnyPresetValueWithExactlyOneTwo` | CAP-S3 | Preset `1` and `3` replaced by a single `2` |
| `ConfigurationUsersAndOrganizationsCannotChangeTheDeclaredCapability` | CAP-S7 | Configuration keys such as `TrustAuthority:EnvelopeCapability=1` and `Content:MinimumBusinessEnvelopeCapability=1`, and different organizations, all still produce `2` |
| `HeadersAndWorkloadCredentialsAreUnchangedApartFromTheDeclaration` | credentials | Exact header-name sets per path. Each workload token validates with its own key, issuer and audience, keeps its `workload_role` and `workload_name`, and carries no capability claim (no token claim in M2f, OD-M2f-07) |
| `TheExistingAuthorityGivesIdenticalOutcomesWithAnyCapabilityValueOrNone` | compatibility | §4 |

**New architecture tests (`EnvelopeCapabilityEmissionBoundaryTests`, 3, from compiled IL):**
- Only the `SendAsync` path of each of the three clients calls `Declare`, once each; the reconciler does not.
- The header literal exists only in the declarer. No Authority assembly contains it yet. **E3-M2f-2 must change this pin.**
- The declarer has no non-constant fields, calls only `ThrowIfNull`, `get_Headers`, `HttpHeaders.Remove` and `HttpHeaders.Add`, and loads only the header name and `2`.

**Suite results (test executables run directly; final restored tree):**

| Suite | Result |
|---|---|
| New contract class | 11 / 11 |
| New architecture class | 3 / 3 |
| Full architecture suite | **157 / 157** (154 pre-existing + 3 new) |
| Full contract suite | 935 total: **922 passed, 12 skipped** (real-Vault classes, as before), **1 failed**: `PhaseC7FieldSearchExecutionTests.AChangedSnapshotIsRetriedWithinTheBoundedBudgetAndAPendingCreationNeverBlocks` (expected 6 catalogue attempts, observed 5). Rerun in isolation five times: **12 / 12 each**. See O-1 |
| Authority, integrity, platform-console suites | Not run: no Authority, integrity or console source changed |

## 4. Compatibility evidence

**On the real Authority host pipeline.** The test uses `WebApplicationFactory<AuthorizationAuthority.Api.Program>` in Development, with the verifier and builder credentials configured and the store deliberately unreachable.
- The three real clients sent all seven content entry points through a recording handler forwarding to the host.
- Each request was then replayed raw with the capability header absent, `1`, `2`, `3`, `not-a-level`, and the two values `1` and `2`.
- Every replay produced the **same status and error code** as the original, and no request was rejected as malformed (no 400).

**From source and IL.** No Authority assembly references the header (architecture pin), and the Authority reads request headers only by explicit name (`InternalAuthentication`, `SecurityCommandEndpoints`).

## 5. Enforcement-removal checks

A mutation driver (session scratchpad, not in the repository) applied each mutation with an exact single-match anchor, rebuilt both test projects, ran both new classes, and restored the file. Every restore was verified by SHA-256. Afterwards both projects were rebuilt with `--no-incremental` and the classes rerun green.

| ID | Mutation | Result |
|---|---|---|
| MF1-01 | Remove the declaration from `AuthorityHttpClient` | Detected (7 tests) |
| MF1-02 | Remove it from `ProtectionVerifierHttpClient` | Detected (4) |
| MF1-03 | Remove it from `SearchIndexBuilderHttpClient` | Detected (4) |
| MF1-04 | Declared level `2` → `1` | Detected (10) |
| MF1-05 | Add without removing a preset value | Detected (2) |
| MF1-06 | Declare in `TeamMembershipReconcilerHttpClient` | Detected (2) |
| MF1-07 | Declare only when the request has a body (GET paths lose it) | Detected (1: the per-method coverage test) |

7 / 7 detected; all files restored byte-identical.

## 6. Observations for owner review

| ID | Observation |
|---|---|
| **O-1** | The single full-suite failure is **unrelated and pre-existing in kind**. The test stubs `IAuthorizationAuthorityClient` with a `DispatchProxy` (no HTTP client is involved). `FieldSearchRetryPolicy` enforces a wall-clock total budget (`RetryMaximumTotalMilliseconds`) against the real `TimeProvider.System`, so under full-suite parallel load one fewer retry fits (5 instead of 6). It passed 5 / 5 isolated runs. Recorded, not changed. Tracked separately as **TS-M2f-01** (OA-M2f1-04). |
| O-2 | The owner brief says "every applicable Authority content request"; the governing contract (CAP-S2) says every request of the three clients. Implemented per CAP-S2, which is a superset. Non-content Authority endpoints receive and ignore the header (closure §4.3). |
| O-3 | The architecture compatibility pin ("no Authority assembly references the header") is intentionally temporary; E3-M2f-2 replaces it with its enforcement pins. |
| O-4 | Closure M2F-01..M2F-05 are met by the tests in §3. The retry, override, credential and real-host compatibility tests are additional. |

## 7. Residual risks (unchanged by this increment)

- **No security effect yet.** No Authority enforces the floor until E3-M2f-2. This increment only makes business builds level 2 ahead of enforcement (business-first order, closure §12.1).
- **Self-asserted header (R-M2f-6, CAP-S7).** A compromised business runtime can send `2`.
- **Level-2 claim.** The claim is true for this build: signed schema-2 fidelity (E3-M2b-2) and verified v2 readers (E3-M2c) are present on this branch. A future build that removed either would have to change the constant; the architecture test pins its value.
- Carried forward: R-M2f-1..R-M2f-10, BD-1, BD-2, F-E2, R-A1-1..R-A1-5, V2-IND-01, RB-CARV-1 production closure, PB-11/PB-12, Azure PostgreSQL verification.

## 8. Not claimed

- No Authority enforcement and no refusal evidence.
- No persistent activation and no Envelope v2 acceptance.
- No Azure, staging or production evidence; no independent review; no certification.
- **Not** that the full contract suite passes: it is recorded as 922 passed, **1 failed** (O-1 / TS-M2f-01), 12 skipped.

## 9. Owner acceptance (2026-11-16)

Owner-led acceptance for Development/Testing. **Not** an independent review, not a certification and not production readiness. It closes no production, Staging, Azure or independent-review gate.

| ID | Decision |
|---|---|
| **OA-M2f1-01** | The E3-M2f-1 business capability-emission implementation is accepted |
| **OA-M2f1-02** | O-1 is accepted as an unrelated, non-blocking, timing-sensitive test observation. The full contract suite **remains recorded with one failure** (922 passed, 1 failed, 12 skipped) |
| **OA-M2f1-03** | Exactly one signed local commit is authorized, containing the nine files of this increment: the three client files, `EnvelopeCapability.cs`, the two new test files, this record, `spec-files.json` and `SHA256SUMS.txt` |
| **OA-M2f1-04** | **TS-M2f-01** (`PhaseC7FieldSearchExecutionTests.AChangedSnapshotIsRetriedWithinTheBoundedBudgetAndAPendingCreationNeverBlocks`: under full-suite parallel load the real-clock retry budget admits 5 attempts instead of 6) is tracked separately and **not** fixed in this increment |

**Evidence preserved as reported:**
- capability contract tests 11 / 11;
- new architecture tests 3 / 3;
- full architecture suite 157 / 157;
- full contract suite 922 passed, 1 failed, 12 skipped;
- enforcement-removal checks 7 / 7 detected and restored;
- compatibility replay of all seven content request types with absent, valid, unknown, malformed and duplicate header variants;
- strict specification manifest validation passed.

**Not authorized:** push, merge, deployment, Stage B, Azure calls, persistent database changes, Envelope v2 acceptance, E3-M2f-2, E3-M2f-2G, refusal evidence (E3-M2f-3), any CARV, floor or ledger change, BD-1, BD-2, F-E2, or a TS-M2f-01 fix.
