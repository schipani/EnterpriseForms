# SEC-BL-01 WP-1 + WP-2 — Implementation Record: Chain Rules, Archive Inventory, Checkpoint Store, Development Persistence

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | WP-1 (chain-validation library) and WP-2 (archive contract extensions, checkpoint-store foundation, Development archive persistence and coordinated reset safeguards) of `SEC_BL_01_DR_CONTRACT_CLOSURE.md` §17 |
| Authorization | Owner, 2026-11-17: WP-0 accepted; WP-1 and WP-2 authorized for **Development/Testing only** under C-155 and the D-R contract. **Not authorized:** WP-3..WP-8, G14 recovery, database migrations, permission changes, production Azure resources, deployment, push, merge, Stage B, commit |
| Baseline | `main` at `a9b32dee5853b8178f7d1146a0817829e8404d65`; the uncommitted WP-0 documentation preserved unchanged (contract SHA-256 `4a8bd1b1c9f1126bf96db0817a7d662f23f21161337d71a5541e6325720ef12e` before this increment) |
| Result | Implemented and tested in Development/Testing. Owner acceptance pending. **Not committed** |
| Not changed | Journal recording (`JournalChain`, `AuditJournal`), archive delivery (`ArchiveProcessor`, `ISecurityEventArchive.AppendAsync`/`ReadAsync`/`ProbeAsync` bodies), `PlatformJournal`, request paths, quarantine triggers, key purposes and provider selection, migrations, grants, Release B fence, host composition |
| SEC-BL-01 / PD-15 / M2F3-43 | **OPEN / OPEN / FAILED**, unchanged. No verifier, D+, baseline or detection of the M2F3-43 cases exists yet |
| Review | Owner-led; prepared by the implementing assistant. Not an independent security review |

---

## 1. Files

| Area | File | Kind |
|---|---|---|
| WP-1 chain rules | `src/authority/AuthorizationAuthority.Application/Features/Journal/Verification/JournalChainRules.cs` | new |
| WP-2 ports | `src/authority/AuthorizationAuthority.Application/Common/Ports/ISecurityEventArchiveInventory.cs`, `.../IVerificationCheckpointStore.cs` | new |
| WP-2 pure logic | `.../Features/Journal/Verification/ArchiveCompleteness.cs`, `ArchiveIdentityCodec.cs`, `JournalVerificationCheckpoints.cs` | new |
| WP-2 adapters | `src/authority/AuthorizationAuthority.Infrastructure/Archive/BlobSecurityEventArchive.cs` (three archive classes now also implement the inventory; existing members unchanged), `BlobInventorySupport.cs`, `VerificationCheckpointStores.cs` | modified / new |
| Vectors | `trust_authority/journal-envelope-v1.test-vectors.json`, generator `scripts/integrity/generate_journal_envelope_vectors.py` | new |
| Tests | `tests/authority/AuthorizationAuthority.Tests/JournalEnvelopeKnownAnswerTests.cs`, `JournalChainRulesTests.cs`, `ArchiveInventoryTests.cs`, `VerificationCheckpointTests.cs`, `JournalVerificationAzuriteIntegrationTests.cs`; `tests/architecture/EnterpriseForms.ArchitectureTests/JournalVerificationBoundaryTests.cs` | new |
| Test runner | `scripts/dev/sec-bl-01-wp2-azurite-integration.ps1` (two throwaway Azurite instances, random keys per run, removed afterwards) | new |
| Development infrastructure | `infrastructure/docker/compose.yaml`, `compose.dev.yaml`, `compose.dev-persistent.yaml`, `compose.security-test.yaml`, `compose.integration.yaml`, `env.example` | modified |
| Reset safeguard | `scripts/dev/check-journal-store-coupling.py` (read-only) | new |
| Documentation | this record; a progress row in `SECURITY_BACKLOG.md`; a dated section in `docs/infrastructure/DEV_ENVIRONMENT_PERSISTENT_VAULT.md`. The accepted contract `SEC_BL_01_DR_CONTRACT_CLOSURE.md` is **unchanged** (SHA-256 still `4a8bd1b1…ef12e`) | new / modified |

## 2. WP-1 — chain-validation library (`JournalChainRules`)

| Element | Implementation |
|---|---|
| Inputs | `JournalEvidence` — a journal row (`FromRow`: UTF-8 of the stored body text exactly as `ArchiveProcessor` computes it, plus organization, stream, revision, kind, previous digest and digest columns), archived bytes (`FromArchive`: storage key plus bytes) or `Unavailable(key)` |
| R-1 | SHA-256 of the bytes equals the stored digest (rows); checked **first**, so a tampered or `jsonb`-era body is `BODY_DIGEST_MISMATCH` whatever it contains |
| R-2 | Strict header parse: a single JSON object, no trailing bytes, **no duplicate members**; `envelope_format` other than `DynamicForms/SecurityJournal/v1` ⇒ **Unsupported** (never valid); otherwise exactly the eight v1 members with canonical shapes (lower-case `D` identifier, integer revision, 64 lower-case hex or null previous digest, timestamp, object body) or **Malformed** |
| R-3 | Envelope organization, stream and revision equal the storage key; for rows also the columns including the event kind |
| R-4 | Envelope previous digest equals the previous-digest column |
| R-5 | Revisions start at 1; revision 1 ⇔ null previous digest |
| R-6 | `VerifySequence`: input in any order; duplicates (`DUPLICATE_REVISION`), gaps including a missing first revision or a missing tail up to an expected last revision (`REVISION_GAP`), entries outside the run or of another organization or stream |
| R-7 | `CheckLink` between consecutive entries, or against a trusted predecessor digest; optional trusted anchor for the first entry (`ANCHOR_MISMATCH`) |
| Outcomes | `Valid`, `IntegrityFailure`, `Malformed`, `Unsupported`, `Unavailable`; precedence integrity failure > malformed > unavailable > unsupported > valid. Findings collected (bounded at 256, truncation reported) for forensics; the highest contiguous valid, linked and anchored revision and its digest are returned |
| Purity | No I/O, no AuthorityDb, no keys, no quarantine, no alerts, no archive writes, no repair (architecture test) |
| Cryptography | Only the existing unkeyed SHA-256 of the journal format; no key purpose added or used. The library documents (and a test demonstrates) that a consistently rewritten chain is internally valid and is detected only against a trusted anchor (contract §2.1) |
| Reuse | One implementation for the future baseline tool (WP-3), verifier (WP-4) and D+ (WP-5); none of them exists yet |
| Known-answer vectors | Five vectors (genesis, two links, a second stream, a second organization; fraction trimming, relaxed escaping, quote and backslash, a 64-bit integer beyond double precision) produced by an **independent Python implementation** of the envelope; the real `JournalChain.Append` reproduces each byte for byte and the rules accept them as rows, as archived bytes and as a chain. Regeneration is deterministic (identical SHA-256 `154b4cbb…f444e` on rerun) |

## 3. WP-2 — archive inventory, completeness, sentinel, checkpoint store

### 3.1 Archive inventory (`ISecurityEventArchiveInventory`)

A **separate** port, so the delivery contract `ISecurityEventArchive` (and the four test fakes that implement it) is unchanged.

| Operation | Behaviour |
|---|---|
| `ReadIdentityAsync` / `CreateIdentityAsync` | Archive-identity sentinel at the reserved name `meta/archive-identity.json` (outside every `org/` entry prefix, never listed as an entry); create-only (`If-None-Match: *`); an existing sentinel is returned, never overwritten. Canonical, strict codec (`ArchiveIdentityCodec`); a non-canonical sentinel parses to null (a mismatch, never a match). AS-4: proves nothing about completeness or freshness |
| `ReadEntryAsync` | `Present` / `Absent` / **`ContainerAbsent`** (Blob `ContainerNotFound`); unavailability (any other failure, an internal timeout, or exhausted SDK retries reported as `AggregateException`) throws `ArchiveUnavailableException`; caller cancellation propagates as `OperationCanceledException` |
| `ListEntriesAsync` | One page of **every** object under `org/{organization}/` (or `.../{stream}/`), canonical or not; paging by continuation token; a failed page is unavailability, never an empty listing |
| Adapters | Blob (Azurite in Development/Testing), in-memory (unit tests; internal tampering hooks), unavailable (always throws) |

### 3.2 Completeness (`ArchiveCompleteness`)

`CollectAsync` gathers every page (bounded; reaching the bound is unavailability). `Analyze` reports per stream: archived count, highest contiguous revision from 1, highest revision, **gaps** (AE-4), **duplicates** (AE-5), **revisions beyond an assumed head** (AE-6), plus **unexpected objects** (AE-3: unknown stream, non-canonical revision encoding, revision 0, extra path segments) and an absent container (never clean). Probing is not used for completeness (AE-2).

### 3.3 Checkpoint store (`IVerificationCheckpointStore`, `JournalVerificationCheckpoints`)

| Element | Implementation |
|---|---|
| Record | Contract §7.1 fields; schema `DynamicForms/JournalVerification/v1`; deterministic canonical JSON; parse is strict (unknown schema ⇒ Unsupported; unknown, missing or duplicate members, invalid values, wrong enum spelling, upper-case hex or any non-canonical byte ⇒ Malformed) |
| Field rules | Sequence ≥ 1; sequence 1 is a Baseline without a previous link; later sequences carry a link; verified digest present exactly when the verified revision is above 0; Full assurance ⇔ no baseline exception; exception ids distinct and ordinal-sorted; UTC time |
| Chain rules | Same organization and stream; sequence +1; link = SHA-256 of the predecessor's stored bytes; verified revision non-decreasing (equal revision ⇒ equal digest); archive instance, assurance and exception ids change **only** at a Baseline (BL-10 re-baseline; see OBS-4) |
| Store | Create-only: `Created`, `IdenticalExists` (idempotent retry), `Conflict` (never overwritten); `Present`/`Absent`/`ContainerAbsent` reads; complete listing; `CheckpointStoreUnavailableException` |
| Discovery | Complete listing; non-canonical object ⇒ Invalid (`CHECKPOINT_UNEXPECTED_OBJECT`); duplicate or non-contiguous sequences ⇒ Invalid; head below a caller-trusted minimum ⇒ **Regression**; reads head and predecessor (start) or the full chain (full sweep); unreadable listed object, unavailable store or unsupported schema ⇒ **Inconclusive**; never consults AuthorityDb |
| Create-next | Only after Empty or Head; refuses a candidate that does not follow; `Created` / `AlreadyStored` / `Conflict` / `Unavailable` (whether the write happened is decided by the next discovery) / `Refused` |
| Rollback classes | CKR-1 corruption detected by chain verification (tested). CKR-2 rollback detected only against a trusted head (tested with a caller-supplied minimum; production needs Confidential Ledger, not implemented). CKR-3 rollback **without** such a head is undetectable and yields the older head (tested and documented) |
| Development/Testing store | `BlobVerificationCheckpointStore` on a separate Azurite instance; construction refuses missing settings, the **publicly known Azurite key**, and any configuration equal to the archive's key, account or endpoint. Configuration separation only: not independence, no freshness assurance. Not registered in any host (WP-4 not authorized) |
| In-memory store | Unit tests only; internal tampering hooks |

## 4. Development archive persistence (N-1) and coordinated reset (RST-1..RST-3)

**Finding (source):** the base `compose.yaml` started the archive's Azurite without `--location`, so the archive lived in the container's writable layer. `compose.dev.yaml` (the retired `enterpriseforms` project) mounted `archive-store-data` at `/data` but Azurite never used it, while `authority-db-data` persisted: every container recreation lost the archive and kept AuthorityDb. `compose.dev-persistent.yaml` (the current stack, `devstack.py`) already passed `--location /data` and was correct.

| Change | Effect (verified by `docker compose config` before/after for all five stacks) |
|---|---|
| `compose.yaml` archive-store: `--location /data` | Without the new profile, the **only** rendered difference in every stack is this flag (`compose.dev-persistent.yaml` unchanged, it already had it). The archive now lives in `/data`: the image's declared volume (base stack), the named volume (`compose.dev.yaml`, `compose.dev-persistent.yaml`) or tmpfs (security-test, integration) |
| `journal-verification-store` service (profile `journal-verification`, not started by default) | Separate Azurite on `authority-net`, own account through `AZURITE_ACCOUNTS`; named volume `journal-verification-store-data` in both development overlays (same lifetime as AuthorityDb and archive; included in `devstack.py backup`, which backs up every project volume), tmpfs in the security-test and integration overlays |
| `env.example` | `JOURNAL_VERIFICATION_ACCOUNT_NAME` and an **empty** `JOURNAL_VERIFICATION_ACCOUNT_KEY` (no key committed). Probe: with the key unset Azurite starts with no usable account (`400 Invalid storage account` for the default account, against `403` on a default instance), so it fails closed instead of falling back to the well-known key |
| `scripts/dev/check-journal-store-coupling.py` | Read-only (`docker volume ls`/`inspect`): reports `COUPLING_BROKEN`, `ARCHIVE_NEWER`, `ARCHIVE_WITHOUT_DB`, `CHECKPOINT_STORE_NEWER` (exit 1) and informational `CHECKPOINT_STORE_ABSENT`. Run on this machine: `enterpriseforms` and `enterpriseforms-dev` coupled (checkpoint store absent, informational) |

**No volume was created, removed or reset; no stack was started.** Existing `enterpriseforms` (retired) data: its archive objects, if any, are in the `archive-store` container layer; the next recreation of that container will make the (empty) named volume the archive. That loss is pre-existing behaviour, not caused by this change, and RST-2 applies.

**Operator rules (contract §6.3):**

| Rule | Operator practice |
|---|---|
| **RST-1** | AuthorityDb, the archive and the checkpoint store keep the same lifetime in every stack: all persistent (development overlays, named volumes) or all disposable (security-test, integration, tmpfs). Never add persistence to one of them alone |
| **RST-2** | Reset them **together or not at all**. A partial reset (for example recreating `archive-store` with a new volume while keeping `authority-db-data`) produces genuine findings (`ARCHIVE_REGRESSION`, `BEHIND_ARCHIVE`, `CHECKPOINT_STORE_REGRESSION`) that must **not** be suppressed (OD-DV-4); triage them as "development reset" and record that. Run `python -I scripts/dev/check-journal-store-coupling.py` before and after any volume operation. For `enterpriseforms-dev` the existing rule stands: never `down -v` (only `devstack.py` operations) |
| **RST-3** | Disposable stacks (`compose.security-test.yaml`, `compose.integration.yaml`, `down -v`) satisfy RST-1 by construction; each new stack is baselined (WP-3) before any verification starts |

## 5. Tests and commands (Development/Testing; exact results)

| Command | Result |
|---|---|
| `AuthorizationAuthority.Tests.exe --filter-class` the six new classes (no Azurite) | 112 total, **102 passed, 0 failed, 10 skipped** (the Azurite classes skip without `SECBL01_*`) |
| `powershell -File scripts/dev/sec-bl-01-wp2-azurite-integration.ps1` (run of record) | phase before-restart `JournalVerificationAzuriteIntegrationTests` **9/9 passed**; `docker restart` of both instances; phase after-restart `JournalVerificationAzuritePersistenceTests` **1/1 passed**; containers and anonymous volumes removed (none left) |
| First integration attempt | **8/9**: unreachable store raised `AggregateException` (SDK retries exhausted with mixed failures) instead of unavailability, a defect in the new adapter. Fixed in `BlobInventorySupport` (including the read-back path of the create-only write); the rerun above passed. See OBS-1 for the pre-existing delivery methods |
| `EnterpriseForms.ArchitectureTests.exe --filter-class ...JournalVerificationBoundaryTests` | 5/5 passed |
| Full Authority suite (`AuthorizationAuthority.Tests.exe`) | **1338 total, 1324 passed, 0 failed, 14 skipped** (pre-existing tests: 1226 total, 1222 passed, 4 skipped) |
| Full architecture suite | **219/219 passed** |
| Full contract suite (`EnterpriseForms.Api.ContractTests.exe`, rebuilt `--no-incremental`) | **954 total, 942 passed, 0 failed, 12 skipped** (identical to the last recorded baseline; TS-M2f-01 did not fail and stays open) |
| Builds (`--no-incremental`, Authority tests, architecture tests, contract tests) | 0 errors, 0 warnings |
| Vector regeneration twice | identical SHA-256; `python scripts/integrity/verify_vectors.py` still PASS (46 checks) |
| `docker compose config`, five stacks, with and without the profile | all render; differences as in §4 |
| Not run | Stage B; security-test stack; PostgreSQL gates; integrity, platform-console and Stage B suites (code not touched); mutation testing (the MV set belongs to WP-7) |

Coverage of the owner's test list: R-1..R-7; valid and invalid genesis; digest mismatch; envelope/column mismatch; incorrect predecessor; missing and duplicate revision; cross-organization and cross-stream; unknown envelope version and malformed envelopes; sentinel creation and absence (in-memory and Blob); enumeration with gaps and unexpected objects (in-memory and Blob); unavailability versus missing object versus missing container (Blob); checkpoint monotonicity, chaining and conflicts (in-memory and Blob); store unavailability; cancellation (pure, in-memory and Blob); restart persistence (Blob across `docker restart`).

## 6. Contract compliance

| Contract item | WP-1/WP-2 status |
|---|---|
| §4 R-1..R-7 | Implemented and tested |
| §6.1 AS-1, AS-2 (port side), AS-4 | Implemented (sentinel, create-only, `ContainerAbsent` distinction); AS-2/AS-3 enforcement belongs to the consumers (WP-3..WP-5) |
| §6.2 AE-1..AE-6 | Implemented and tested; AE-7 (Azure listing consistency) not verified; AE-8 measurement is WP-7 |
| §6.3 RST-1..RST-3 | Implemented in Development infrastructure and documented (§4) |
| §7.1, CK-1..CK-5, CK-7..CK-9, CK-11 | Implemented as primitives and tested; CK-6 (ledger head) not implemented (WP-8); CK-10 recovery and CK-12 identity separation are WP-3/WP-4/WP-8 |
| §7.3 CKR-1..CKR-3 | Behaviour implemented and demonstrated; CKR-2 only with a caller-supplied trusted head |
| §5 D+, §8 verifier, §9 finding emission, §10 baseline, §11 M-6 | **Not implemented** (WP-3..WP-6). Finding codes used by the primitives match §9 |
| §2 J1..J6 | Primitives make no trust claim; nothing here detects anything in production |

## 7. Observations (for owner disposition; nothing changed beyond WP-1/WP-2)

| ID | Observation | Tag | Recommended disposition |
|---|---|---|---|
| OBS-1 | The pre-existing `BlobSecurityEventArchive.AppendAsync` and `ReadAsync` catch only `RequestFailedException`/`OperationCanceledException`. The same SDK behaviour that broke the new adapter (exhausted retries ⇒ `AggregateException`) would make them throw `AggregateException`, which `AuthorityWorkLoop`, `ArchiveProcessor` and `PlatformJournal` do not map to `ArchiveUnavailableException`; the work loop's catch list does not include it. Fails closed (nothing is accepted), but an outage may surface as an unhandled background-service failure instead of a retry | [I] (behaviour [T] for the new adapter only) | Separate defect record and fix in the delivery path, with its own authorization (it changes delivery code) |
| OBS-2 | `check-journal-store-coupling.py` compares volume creation times; it cannot see that the retired project's archive volume was mounted but unused before this change | [S] | Accept as a documented limit; content-level coupling is the baseline's job (WP-3) |
| OBS-3 | CI regenerates only the three existing vector files (`.github/workflows/ci.yml`); `journal-envelope-v1.test-vectors.json` is checked by the .NET known-answer test but not by the CI regeneration diff | [S] | Add the generator to the CI regeneration step (PB-11 change, separate authorization) |
| OBS-4 | Contract BL-10 says a re-baseline "starts a new checkpoint sequence". Implemented as a Baseline record that **continues** the same linked chain (a create-only store cannot restart at sequence 1, and continuing keeps a regression visible); a restart from 1 happens only in a new or replaced store (CK-10) | Interpretation | Confirm, or amend BL-10 wording |
| OBS-5 | The documents Azurite (`azurite`) has the same missing `--location` in the base compose file and `compose.dev.yaml`; outside SEC-BL-01 (business documents, not the journal) | [S] | Separate decision |

*(Note 2026-11-17, WP-3/WP-4 increment, `SEC_BL_01_WP3_WP4_IMPLEMENTATION_RECORD.md` §7: **OBS-1** reproduced by an executed, opt-in characterization test (timing-dependent: at an 8 s archive timeout the pre-existing `ReadAsync`/`AppendAsync` and `ArchiveProcessor` let `AggregateException` escape; at 1, 2 and 5 s they did not); delivery code unchanged. **OBS-2** clarified in the checker's documentation only. **OBS-3**, **OBS-5** unchanged. **OBS-4**: the baseline never re-baselines over an existing chain; it reports the proposed continuation and writes nothing, pending owner confirmation.)*

## 8. Remaining limitations

- Development/Testing only. The Azurite instances share one administrator and use keys known to that administrator; no independence (J4/J5/J6), no checkpoint freshness (CKR-3), no WORM. Production prerequisites (contract §14) are unchanged and unmet.
- Nothing detects the M2F3-43 cases yet: the primitives have no caller in product code; M2F3-43 stays FAILED, R-FU-1 open.
- Blob listing consistency, throttling and paging at production scale are not verified (AE-7, OI-DR-8).
- Performance not measured (AC-DR-11, WP-7).

## 9. Not claimed

No baseline, verifier, D+, finding emission, Confidential Ledger, G14 recovery, M-6, migration, grant change or deployment; no change to journal recording or archive delivery; no closure of SEC-BL-01, PD-15, M2F3-43 or R-FU-1; no production assurance; no independent review.
