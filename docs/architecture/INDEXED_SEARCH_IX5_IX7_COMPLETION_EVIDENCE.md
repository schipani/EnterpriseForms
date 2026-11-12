# IX-5, IX-6 and IX-7 completion evidence (consolidated directive, C-151)

- **Status (2026-11-11):** implemented and tested for Development/Testing under the owner's consolidated execution directive (C-151). Owner-led; **not independently reviewed.** Nothing here is production evidence, staging evidence or an independent security attestation.
- **Commits (signed, local):**
  * `5e0a0f0`: IX-5 completion;
  * `71687a8`: IX-6, IX-7 and the fixes;
  * this document's commit: evidence.
- **What is live:** nothing new is served to a user by default.
  * Tier 2 serves no search; live Tier 2 serving needs a separate owner approval and was not implemented.
  * The IX-6 indexed My Records engine is off by default (`RecordList:Engine=VerifiedScan`). It is refused at startup outside Development/Testing. On the security-test stack it is selected only by a Testing-only per-request header.
  * The IX-7 Azure Managed HSM seal provider is wired but contacted by nothing: Dark mode itself is still refused outside Development/Testing, and no Managed HSM exists for this project.
- **Labelling:**
  * "code fact" means checked by inspection;
  * "tested" means executed test evidence with a run id;
  * "measured" means a local Docker measurement (indicative only: not staging, not PERF-01).
- **Environment:**
  * Docker Desktop, the security-test stack, recreated fresh for IX-6 (`20261111-204204-7fbbd882`);
  * the Authority image was rebuilt once afterwards (IX-6 page-approval fix, IX-7 provider and harness);
  * a keep-awake process ran for the session.
- **Power:**
  * the R-02 preparation runs were on battery (70 %, 67 %);
  * the ST-PERF-7 runs and the final stack classes were on AC.

## 1. Scope and numbering

The governing design (revision 3, §L) numbers the stages:
- **IX-5:** Tier 2;
- **IX-6:** My Records on verified entries, and entry access;
- **IX-7:** Azure staging PERF-01 with Managed HSM, the DR rehearsal including the projection rebuild after G14 recovery, and the independent implementation review.

Revision 1 §L numbered them one lower; revision 3 governs. No requirement was added. Nothing outside these stages was redesigned.

## 2. IX-5

### 2.1 Completed in this cycle

Committed as `5e0a0f0`. The detail of the M1 fence, the M2 sweep, the IX-39 coupling guard and the shadow-sampling evidence is in that commit and its message. Those results and their stack runs were reported with C-150's continuation.

| Item | State |
|---|---|
| M1 fence (verified revision; affected records compared with SpiceDB `LookupSubjects` at one pin) | Implemented, tested |
| M2-E/V full equivalence (24 h) and M2-S rolling SpiceDB pass (7 d), no grace | Implemented, tested |
| IX-39 schema-coupling guard (SHA-256 of the zed schema; startup disables Tier 2 on a different schema; rows of another digest rebuilt) | Implemented, tested |
| IX-40 property-based differential against an independent zed-semantics oracle | Implemented (5 seeds), tested |
| Shadow sampling evidence (§6 of the plans): digests, restart events, canary flag, retention purge | Implemented, tested |
| **14-day sampling criterion** | **Not met: no 14-day period has been run.** The report computes it; nothing asserts it. **C-152: deferred by the owner (not waived)** |
| M2 split decision (acceptance record TQ 3–5) | Instrumented; the decision stays with the owner on real sampling evidence |

### 2.2 R-02: statement preparation (measured; R-02 stays FAIL)

**Question.** The C-150 finding F-150-2 proposed statement preparation, because the approvals statement is planned on every execution.

**Method.** One measurement per setting:
- same images, `IndexedCommitReadStageMeasurementTests`, 300 records each, both on battery;
- the Authority's runtime connection was recreated with Npgsql automatic preparation (`Max Auto Prepare=50; Auto Prepare Min Usages=2`) through the new Testing-stack variable `AUTHORITY_DB_CONNECTION_EXTRA`, which is empty by default;
- the default was restored afterwards.

| Stage (ms) | Preparation off p50 / p95 | Preparation on p50 / p95 |
|---|---|---|
| reads | 2.191 / 4.313 | 2.300 / 5.069 |
| approvals query, total | 1.488 / 2.259 | 1.513 / 2.733 |
| approvals command execution | 1.051 / 1.593 | 1.051 / 1.798 |
| materialization | 0.436 / 0.737 | 0.474 / 0.878 |

**Finding.** Automatic preparation gives no measurable gain on this path. The apparent planning cost is not removed by Npgsql's automatic preparation here.

**Disposition:**
- F-150-2 is closed as "not pursued";
- no production or default configuration was changed;
- R-02 stays FAIL;
- no multi-hour benchmark was run.

The raw output (not versioned) is `artifacts/ix-5/r02-read-stage-prepare-{off,on}.json`, with test runs `20261111-201919-31e3895b` and `20261111-202154-07f3a35c`.

### 2.3 Still open in IX-5

- The 14-day sampling period (§2.1).
- The bounded IX-40 stack workload with a canary positive control over a sustained period. Focused stack tests only so far.
- TQ-23: the fallback timing residual. It was accepted for Development/Testing only and is to be re-decided before serving.
- R-56: the organization-wide gate. R56-C was not implemented.
- F-150-3 to F-150-8 are retained. Loaded-stack builder timeouts (F-150-8) are **not** resolved by fresh-stack passes.
- Large-organization scaling (R-43, F-150-6) is unmeasured.

## 3. IX-6: My Records on verified index entries

### 3.1 Implementation (code fact)

**Authority** (`IndexedSearchService.RecordList.cs`): a new endpoint `POST /internal/v1/search/indexed-records`, on the same plane and the same delegated assertion as the Tier 1 search. It is off unless `SearchIndex:Query:RecordListEnabled`, which requires `QueryEnabled`. In order:
1. Organization gate and S1 (423 when quarantined), then the servable projection with the §17.10 deadlines.
2. `D` from SpiceDB `LookupResources(discover)` at one S1 pin, exactly as the business listing does.
3. In one read-only reader snapshot: every registered form record of `D`, with its form, its accepted head and its entry (new reader port `ReadListingScopeAsync`).
4. **I-1 for the listing:** every contentful record must have a current entry (entry at the committed operation, header-facts derivation version or later, not quarantined). Otherwise the Authority returns 503 `RECORD_LIST_INDEX_INCOMPLETE` and the business listing answers.
5. Filters (form, status Draft/Submitted/Archived, children, archived) and sort (`modifiedOn`, `createdOn`, `recordStatus`) run on the entries' header facts and the Authority-held times. Counts are exact.
6. **I-2:** every listed record's entry is bound to its accepted manifest (`IndexedSearchBinding.VerifyMatch`), and its decoded header must equal the one filtered on. A mismatch quarantines the index.
7. For the page:
   * the approvals of the page's forms are read at the same reader snapshot, with the search's consistency rule (same authorization revision; projection at the content revision);
   * every seal is verified;
   * edit flags come from SpiceDB at the pin.
8. The pin completes once; a moved revision gives 409.

**Paging:**
- by offset over the Authority's deterministic order;
- later pages carry the first page's authorization revision;
- any change gives 409 and the client restarts;
- nothing is stored.

**Without accepted content.** Records registered without accepted content are reported in their own category (count, first 50 ids) and are not listed. This is R1 §E.6: "unavailable-id reporting, renamed to the precise category".

**Business API** (`RecordListIndexedEngine`, `ListRecordsHandler`):
- `RecordList:Engine` is `VerifiedScan` (default) or `Indexed`; `Indexed` is refused at startup outside Development/Testing.
- These stay on the C-29 listing (R1 §E.6, kept unchanged):
  * the record-reference lookup `q`;
  * the `formName` sort (cosmetic, unsigned names);
  * the `Deleted` filter.
- Fallback codes:
  * not ready, inconsistent, verification overdue, verification limit and `RECORD_LIST_INDEX_INCOMPLETE` mean the C-29 listing answers;
  * every other failure propagates, including 423 `INTEGRITY_QUARANTINE`, `RECORD_QUARANTINED` and Authority unavailability. A failure is never hidden by a fallback.
- The cursor records its engine (an optional field, backward compatible). A page of one engine's listing is never served by the other: 409 `LIST_CHANGED`, and the client restarts. A C-29 cursor continues on its own engine.
- Form names and creator names are added from business data (display only). `projectionVerified` stays false.
- Testing only: the header `X-Testing-Record-List-Engine` selects the engine per request. It is ignored in every other environment.
- Observability: log event 9335 records the engine and the fallback code. It carries codes only.

**Not in IX-6** (blocked dependency): "entry access", meaning answering from Tier 2 access rows. That is Tier 2 serving and needs separate owner approval. The IX-6 listing decides access from SpiceDB only; Tier 2 is not read.

**Known limitation (by design, I-1).** The indexed engine answers only when **every** contentful record the caller can discover has a current entry, which requires every form the caller sees to have search coverage. Otherwise the C-29 listing answers, unchanged. The fallback is per listing, never partial.

### 3.2 Tests

- **Contract `Ix6RecordListEngineTests`: 18/18.** It covers:
  * the indexed answer and no discover enumeration;
  * paging with revision and offset;
  * unsupported queries going to C-29;
  * each fallback code on the first page;
  * a later page never switching engine;
  * cursor engine checks in both directions;
  * propagation of 423, 503 and quarantine;
  * the Testing override;
  * the environment refusal;
  * old cursors decoding as C-29.
- **Stack `Ix6RecordListStackTests`: 4/4, run `20261111-205246-b044ecc5`.** It covers:
  * the indexed listing equal to the C-29 listing in ordered ids, statuses and totals for six query shapes (default, status filter, archived, `createdOn` ascending, `recordStatus`, form filter), with archived records handled, the engine log confirming the indexed engine answered, and cross-engine cursors refused;
  * a record on an uncovered form making the listing fall back unchanged (`RECORD_LIST_INDEX_INCOMPLETE`);
  * revocation being immediate: the next page gives 409 without record ids, and a new listing excludes the record;
  * damaged header facts quarantining the index (`MATCH_HEADER_FACT_MISMATCH`) with C-29 answering, and an S1 organization quarantine refusing both engines identically (423).
- **Defect found and fixed by the first stack run** (`20261111-204204-7fbbd882`, 2/4): the page seal check was given no approvals, so honest entries failed `APPROVAL_OF_ENTRY_MISSING` and the index quarantined. It **failed closed**: the C-29 listing answered correctly. The fix reads the page forms' approvals at the reader snapshot with the search's consistency rule.

## 4. IX-7

### 4.1 Azure Managed HSM seal provider (implemented; validated against a fake endpoint only)

`ManagedHsmSearchSealSigner` signs the SHA-256 digest of the canonical seal message with `ES256`, through the Managed HSM REST key operation `sign` (API version 7.4). Authentication:
- the Authority's managed identity only: system-assigned, or a configured user-assigned client id;
- token scope `https://managedhsm.azure.net/.default`;
- no secret in configuration.

The startup pin check fetches the public key and refuses the key unless all of these hold:
- an `EC-HSM` P-256 key that permits `sign`;
- enabled, not exportable, with no release policy;
- a `kid` naming the configured key and version.

Every failure fails closed with `SearchSealUnavailableException`, so the commit that needed the seal fails. There is no fallback signer. Failures include:
- unreachable, timeout;
- 429 throttling, 401/403 refused, 404;
- a malformed answer, a signature that is not 64 bytes;
- a credential failure.

Startup validation refuses:
- an endpoint that is not HTTPS;
- a missing or unsafe version identifier for the signing or any pinned version;
- an out-of-range timeout.

No package was added: the `Azure.Identity` credential types ship in the Azure.Core 1.60 assembly that the Authority already references through `Azure.Storage.Blobs`.

**Tests:**
- `ManagedHsmSearchSealSignerTests` 9/9: fake HSM endpoint, signature verified against the HSM public key, every failure kind, key-property refusals, unmapped version;
- `ManagedHsmSealCompositionTests` 5/5: provider selection and startup refusals.

**Not done:** any call to a real Managed HSM. There is no staging deployment; deployment is not authorized.

### 4.2 ST-PERF-7 seal-throughput harness (implemented; measured locally against Vault Transit)

`dotnet AuthorizationAuthority.Api.dll --measure-seal-throughput [--count N] [--concurrency C]`:
- builds the fully composed Authority host (the same signer, provider policy and pin) and does not start it;
- signs N synthetic messages, with a domain prefix that no seal uses, **through `SearchSealSigningGate`**: the provider public key must equal the pin, and every signature is verified against the pin, exactly as for real seals. The architecture test keeps the signer reachable only through the gate;
- verifies every signature again independently;
- prints one JSON report;
- is refused outside Development/Testing.

Local results (VaultTransit through the gate, 1,000 signatures each, AC):

| Concurrency | Signed | Failures | ops/s | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---|---|---|---|---|---|
| 1 | 1000 | 0 | 257 | 3.1 | 4.6 | 8.0 | 349 |
| 4 | 1000 | 0 | 738 | 3.0 | 7.6 | 13.8 | 366 |
| 16 | 1000 | 0 | 1372 | 5.5 | 27.2 | 177 | 216 |

An earlier run bypassed the gate and called the signer directly (401 / 861 / 1508 ops/s). The architecture test `TheSealSignerIsReachedOnlyThroughThePinCheckingGate` refused it, and the harness was corrected; those figures are not used.

**This is not ST-PERF-7.** ST-PERF-7 asks for the Managed HSM throughput in staging, including throttling behaviour and the backfill and sweep sizing. That remains **blocked**: no staging deployment is authorized. The raw output (not versioned) is `artifacts/ix-7/st-perf-7-vault-c{1,4,16}.json`.

### 4.3 Local recovery rehearsal of the search projections (implemented)

`Ix7SearchRecoveryRehearsalStackTests` (run id in §6) restores an organization's `authority_search` and `authority_search_access` rows from a backup taken before a revocation and a new record. This is what a projection restore from an older backup leaves behind, while the journal, heads and SpiceDB are current. It is applied as `pg_restore --disable-triggers` would apply it, with the Tier 2 maintainers paused during the restore.

Required, and asserted:
- The revoked share is never served. The Reader's indexed search and both listing engines exclude the record, because they decide from SpiceDB at a fresh pin. Tier 2 is never reported verified while it holds the revoked grant (polled until it settles).
- **Tier 1:** the restored index is detected and quarantined. The C-29 / V1 path answers meanwhile, including the record added after the backup.
- **Tier 2:** it either catches up from the hash-chained journal and re-verifies (M1 fence on the events after the restored verified revision), or quarantines. If it caught up while Tier 1 is quarantined, it stays not-Ready, because Tier 2 readiness requires an Active Tier 1 (A-3). All three are acceptable; silent trust is not.
- **Recovery:** the administrator's Tier 1 rebuild returns it to `Active` with every entry at its head. The controlled Tier 2 rebuild and a complete M2 pass return Tier 2 to verified without the revoked grant. Searches afterwards equal the authorized truth.

The times are written to `artifacts/ix-7/recovery-rehearsal.json` (not versioned). Run `20261111-212015-51b005cc`, AC:
- Tier 1 restore detected (quarantined) after 3.0 s;
- Tier 2 settled after 3.9 s: caught up from the journal, revoked grant gone, held not-Ready by the Tier 1 quarantine;
- Tier 1 rebuild to `Active` in 4.2 s;
- Tier 2 rebuild plus a complete M2 pass in 6.3 s.

These are local rehearsal figures for four records, **not an RTO**.

**What this is not:**
- the G14 coordinated recovery: AuthorityDb point-in-time restore with journal replay, SpiceDB rebuild, Managed HSM key and archive recovery, recovery epoch, and two approvals. **G14 is not implemented, and that part of IX-7 is blocked on it;**
- the staging DR exercise.

### 4.4 Blocked in IX-7 (needs separate authorization)

| Item | Why blocked |
|---|---|
| Azure staging PERF-01 (Managed HSM backfill and seal throughput, SpiceDB at scale) | No staging deployment authorized; no Managed HSM |
| ST-PERF-1..7 in staging; R-02 closure | Same; R-02 stays FAIL |
| DR rehearsal after **G14** recovery | G14 not implemented |
| Independent implementation review | A process gate; owner-led work cannot satisfy it |
| Per-organization sweep lease for more than one Authority instance (design §22.7) | A decision for more than one instance; the stack runs one; not decided here |
| 100K/250K datasets, multi-hour benchmarks, Stage B | Not authorized in this directive |

## 5. Security verification (summary)

| Boundary | How it holds | Evidence |
|---|---|---|
| Authority-controlled authorization | IX-6 decides the listed set from SpiceDB `discover` at one pin and completes it. Edit flags at the pin. Tier 2 is never read for a decision | Code fact; architecture test `EveryDecisionPathPinsAndCompletes` (pin = completion count); stack revocation test |
| Stale grants and revocation | A revocation refuses the next page (409) and excludes the record from a new listing. The rehearsal shows a restored stale Tier 2 grant is never served or reported verified | `Ix6RecordListStackTests`, `Ix7SearchRecoveryRehearsalStackTests` |
| Integrity | Every listed record is bound to its accepted manifest. Header facts are compared with the sealed header; page seals are verified. Damage quarantines the index. Restored search rows are detected | Stack tests (damage, restore) |
| Fail closed | Every index failure falls back to the C-29 listing (whole listing, never mixed). Organization quarantine, record quarantine and Authority unavailability propagate. HSM failures fail the commit | Contract and stack tests; HSM unit tests |
| Searchable data | Unchanged: no new indexed field, no Confidential/encrypted value indexed. The listing reads header facts only | Code fact |
| Production activation | None. Indexed listing refused outside Development/Testing. Dark mode still refused outside Development/Testing. No production configuration changed | Startup validation tests |

## 6. Tests executed

- Authority unit **559/559** (545 before, plus 9 HSM signer and 5 composition).
- Business contract **626/626**. One pre-existing C-150 timing test, `ARetryWaitIsCancelledWithTheRequest`, failed once under full-suite load: its 450 ms bound sat next to the 500 ms minimum jittered wait. Its delays were scaled four times, giving the same discrimination with headroom: an uncancelled wait is now at least 2 s, against a bound of 1.5 s. It passed 3/3 alone before the change and in the full run after it.
- Architecture **107/107**:
  * the IX-6 file was added to the pin-and-complete allowlist, with its pin and completion counts checked;
  * the Managed HSM signer was added to the seal-signer allowlist as an implementation;
  * the throughput harness goes through the gate (see §4.2).
- **Stack:**

| Class | Result | Run id |
|---|---|---|
| `Ix6RecordListStackTests` (first, found the page-approval defect) | 2/4 | `20261111-204204-7fbbd882` |
| `Ix6RecordListStackTests` | **4/4** | `20261111-205246-b044ecc5` |
| `Ix7SearchRecoveryRehearsalStackTests` (first; its assertion did not allow F-151-7) | 0/1 | `20261111-205457-2ed6ad00` |
| `SearchAccessVerificationStackTests` (found F-151-6) | 1/2 | `20261111-205724-19de4760` |
| `SearchAccessShadowStackTests` | 2/2 | `20261111-205950-6b4a3b20` |
| `IndexedFieldSearchEngineStackTests` | 5/5 | `20261111-210041-eb1a65be` |
| `IndexedSearchQueryStackTests` | 21/21 | `20261111-210158-6e5f2f19` |
| **After the F-151-6 fix** (Authority image rebuilt; the shadow, engine and query classes above ran on the image before the fix, which changes only the verification loop's scheduling): | | |
| `SearchAccessVerificationStackTests` | **2/2** | `20261111-211156-d2be4ed0` |
| `Ix7SearchRecoveryRehearsalStackTests` | **1/1** | `20261111-211250-9e276dc7` |
| IX-5b `SearchAccessValueProjectionStackTests`, including the C-149 idle-load bound | **10/10** | `20261111-211327-89ce136a` |
| IX-5a `SearchAccessProjectionStackTests` | **10/10** | `20261111-211650-82f56510` |
| `Ix6RecordListStackTests` | **4/4** | `20261111-211817-1848eebf` |
| `Ix7SearchRecoveryRehearsalStackTests`, final image (gate-based harness), with timings | **1/1** | `20261111-212015-51b005cc` |

- **IX-5 stack runs of this cycle (at `5e0a0f0`):**
  * `SearchAccessVerificationStackTests` 2/2, `20261111-195307-f5c48c9d`;
  * `SearchAccessShadowStackTests` 2/2, `20261111-195439-da503bc6`;
  * IX-5a 10/10, `20261111-195528-0016be59`;
  * IX-5b 10/10, `20261111-201249-047c5695`, after the idle-backoff fix;
  * `AuthorityWorkWakeupStackTests` 4/4, `20261111-195954-f2f7013d`;
  * `IndexedSearchQueryStackTests` 21/21, `20261111-200036-435a64c7`;
  * `IndexedFieldSearchEngineStackTests` 5/5, `20261111-200932-94793880`.
- Not run:
  * Stage B (manual, release-only, C-131);
  * the 100K/250K datasets;
  * multi-hour benchmarks;
  * staging;
  * more than one Authority instance;
  * `AuthorityWorkWakeupStackTests` and the builder classes were not rerun after this cycle's changes (no change touches their paths).

## 7. Findings of this cycle

| Id | Finding | Kind | Status |
|---|---|---|---|
| F-151-1 | The first IX-6 stack run quarantined two indexes. The page seal check had no approvals, so honest entries failed. The listing fell back correctly (fail closed) | Tested defect | Fixed, retested 4/4 |
| F-151-2 | The Managed HSM startup validation dereferenced a configuration key bound to null instead of refusing | Tested defect | Fixed |
| F-151-3 | Npgsql automatic preparation does not reduce the commit's approvals-statement cost on this path | Measured | F-150-2 closed as not pursued; R-02 FAIL |
| F-151-4 | The indexed listing answers only when every form the caller sees has search coverage; otherwise it falls back to the C-29 listing | Code fact, by design (I-1) | Recorded; coverage policy is an owner topic before any default change |
| F-151-5 | A projection restore leaves Tier 1 claiming `Active` until the first search or the sweep detects it. The window is unserved (V1 answers) but is not zero | Tested | Recorded with the rehearsal times; staging DR to measure |
| F-151-6 | Regression from this cycle's IX-5 idle backoff: the verification loop's memo was keyed on the authorization revision only, so after a Tier 2 quarantine and rebuild (which leave that revision unchanged) verification resumed only after the backoff, up to the full-check interval (1 h by default). Tier 2 stayed unverified meanwhile (fail closed; availability only). Found by `SearchAccessVerificationStackTests` (`20261111-205724-19de4760`, 1/2). The memo is now keyed on the whole change marker; rebuild and quarantine append audit events, so they re-arm verification on every instance with no new read | Tested defect | Fixed, retested (§6) |
| F-151-7 | After a projection restore, Tier 2 caught up from the journal (the revoked grant removed) but stayed not-Ready while Tier 1 was quarantined, because Tier 2 readiness requires an Active Tier 1 (A-3). This is correct; the first rehearsal assertion did not allow it | Tested | Test corrected (§4.3) |
| F-151-8 | The first ST-PERF-7 harness called the seal signer directly, bypassing the pin-checking gate. The architecture test refused it before commit | Tested (architecture) | Harness routed through `SearchSealSigningGate`; re-measured |

## 8. Outstanding release and production gates (unchanged by this work)

- R-02 FAIL (20 ms p95 commit gate). Closure only by IX-7 staging ST-PERF.
- PERF-01 and ST-PERF-1..7 in staging, with Managed HSM.
- G14 coordinated recovery, then its DR rehearsal including the projection rebuild.
- Independent implementation review of IX-1 to IX-7 and of Tier 2; the independent security review of the design (FD-8).
- Tier 2 serving approval, with the 14-day sampling evidence, the M2 split decision and the TQ-23 re-decision.
- R-56 (organization-wide gate), R-43 and F-150-6 (scaling), and F-150-8 (loaded-stack sweep latency).
- G06, G19 and G21 stay open. M4-1 is pending independent review.
- Stage B release testing, run only on the owner's explicit request.
- Production Level 2 certification: independent cryptographic attestation, Managed HSM, Confidential Ledger, Azure Level B.

## 9. Restrictions observed

- No live Tier 2 serving.
- No R56-C.
- No production activation and no production configuration change.
- No Stage B.
- No 100K/250K dataset and no multi-hour benchmark.
- No push, pull request, merge or deployment.
- Signed local commits only.
