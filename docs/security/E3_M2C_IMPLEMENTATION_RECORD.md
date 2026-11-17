# E3-M2c — Business Envelope v2 readers (dark) and OD-M2c-02a decryption modes — implementation record

**Status.** Implemented for **Development/Testing only** on `e3/m1-envelope-v2-core`, on top of the signed E3-M2b-2 + S-4 commit `9810a67a1135137585a1d0d7bc82e73a5bc7c1f9`. **Accepted by the owner for Development/Testing only (§12); recorded as one signed local commit (not pushed, not merged).** Owner-led work; not an independent review, not a certification. Envelope v2 is **not** activated: no product writer produces Envelope v2 or signed schema 2, and the Authority still refuses every new schema-2 candidate (CARV 1, M2d not implemented). The readers are dark: they open v2 rows only when such rows exist, which today means test fixtures sealed by the E3-M1 core. No Authority change, no BusinessDb or AuthorityDb migration, no CARV or floor change, no Azure call, no persistent database touched, Stage B not run. The final security closure (§11) is part of the accepted change set.

Companion documents: `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` (M1 core, §13), `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` (package; §10 E3-M2c scope), `E3_M2B_IMPLEMENTATION_CONTRACT.md` (§4.4 S-1..S-11, §6.1 permitted pairs, §15 revised increments; the capability header moved to M2f). Evidence: `docs/evidence/e3-m2c-readers/`.

---

## 1. Owner decision OD-M2c-02a (approved: Option 1)

Two explicit field-decryption modes with architecture-enforced call-site restrictions, amending the E3-M2c implementation instructions.

| Mode | Entry points | Admits | Never |
|---|---|---|---|
| **A — Authority-verified** | `IFieldEncryptionService.DecryptVerifiedAsync(organizationId, values)` (batch); application wrappers `DecryptedFieldValues.DecryptAsync` and `FieldValueCodec.FormatAsync` | Strict S-8 per row: (schema 1, envelope 1, no v2 column) → v1 reader; (schema 2, envelope 2, every v2 column) → E3-M1 v2 opener | Schema 0; any other pair; a schema-1 row carrying a v2 column; a schema-2 row missing one; a row of another organization; format guessing; provider fallback |
| **B — Self-sealed** | `IFieldEncryptionService.DecryptAsync(value, purpose)` (unchanged single-value API); application wrappers `DecryptedFieldValues.DecryptSelfSealedAsync` and `FieldValueCodec.FormatSelfSealedAsync` | Envelope 1, signed schema 0 or 1, every v2 column NULL; the unchanged v1 cryptography | The v2 opener (not referenced by this path at all); schema 2; envelope 2; any v2 metadata; automatic fallback |

The application wrappers of Mode B add a **run-time provenance check**: `FieldValueCodec.ProtectAsync`, the single product path that seals a field value, registers each envelope instance it freshly seals (`SelfSealedFieldValues`, a `ConditionalWeakTable`); the self-sealed wrappers refuse any row that is not a registered instance. Rows read from the store (EF materializes new instances), clones (`CloneValue` creates new instances) and envelopes rebuilt from stored bytes are never registered. An already-encrypted row passed into `ProtectAsync` is returned unchanged and is **not** registered. The raw `IFieldEncryptionService.DecryptAsync` has no registry check, so the existing v1 tests and path B are unaffected.

There is no general-purpose bypass: no API accepts a "mode" parameter, Mode A has no option to admit schema 0, and Mode B cannot reach the v2 opener.

## 2. Self-sealed call-site inventory (A–E) and provenance

| | Call site (pinned by `DecryptionCallSiteTests.SelfSealedCallersArePinnedToTheReviewedInventory`) | Entry point | Provenance established |
|---|---|---|---|
| **A** | `FieldProtectionMigrationService.MigrateRecordAsync` (Encrypt direction self-check) | `FieldValueCodec.FormatSelfSealedAsync` | Decrypts only `converted`, the envelope `ProtectAsync` sealed from the record's plaintext row in the same statement. The Decrypt direction of the same method decrypts **stored** committed rows and stays on `FieldValueCodec.FormatAsync` (Mode A). |
| ~~B~~ | `CreationRequestFingerprint.MatchesAsync` | **Moved out of Mode B by the closure (§11.1)**: `IFieldEncryptionService.OpenCreationFingerprintKeyAsync` | The stored sealed MAC key of a creation attempt is stored content, not a self-sealed value. It now has its own narrowly typed operation; the self-sealed inventory is A, C, D, E. |
| **C** | `CreateFormRecordHandler.CreateCoreAsync` (creation response) | `DecryptedFieldValues.DecryptSelfSealedAsync` | See §2.1. |
| **D** | `UpdateFormRecordHandler.OwnerUpdateAsync` (owner-edit response) | `DecryptedFieldValues.DecryptSelfSealedAsync` | See §2.2. |
| **E** | `UpdateFormRecordHandler.DecryptSharedResponseAsync` (shared-edit response, called by `SharedUpdateAsync`) | `DecryptedFieldValues.DecryptSelfSealedAsync` **and** `DecryptedFieldValues.DecryptAsync` | See §2.3. |

### 2.1 Path C — creation, retries and replays (data flow inspected)

`candidateValues` is always `applied.Rows` of `CandidateBuilder.ApplyValuesDetailedAsync([], request values, …, creating: true)`: no current rows are passed, so every encrypted row is a typed row created from **this request's** input and sealed by `ProtectAsync`. The branches that answer with this list:
- first attempt (`CommitInitialContentAsync` stages and commits these rows);
- retry where an earlier attempt already **staged** its rows (`CommitInitialContentAsync` path 1 commits the earlier staged rows; the answer still formats this request's own fresh seals of the same input — the creation fingerprint proved the request identical);
- replay of an accepted creation (`head.CommittedContentRevision == 1`): answers with this request's fresh seals and the Authority's head.

The branches that would involve previously accepted content (`head.CommittedContentRevision > 1`, or another attempt won with a later revision) return `CompletedAsync`, i.e. the **verified read** (Mode A). No branch puts committed or staged ciphertext into `candidateValues`. Provenance is established; no change to the handler's flow was needed.

### 2.2 Path D — owner update and idempotent replay (data flow inspected)

The owner path decrypts the verified current snapshot (Mode A, inside `CandidateBuilder.ApplyValuesDetailedAsync`), then **re-creates every carried and edited value as a typed row** (`Carry` → `FieldValueCodec.CreateRow`) and seals each one with `ProtectAsync` (F-07: the owner path re-seals every encrypted value). Retired fields are not carried; orphaned occurrences are carried through the same `Carry`. So every `FieldEncryptedValue` in `candidateValues` is a fresh seal of this operation; no stored ciphertext row is ever in the list. On an idempotent replay (`RecordCandidateStager.StageAndCommitAsync` returns the accepted head because `head.CommittedOperationId == operationId`), the handler still formats the candidate it has just rebuilt — fresh seals. (Reaching that branch needs the new revision as `expected_content_revision`; with the old one the handler answers 409 before anything is built.) The plaintexts in the response come either from the request or from Mode A decryption of the verified snapshot, which the owner (complete projection) may read. Provenance is established; no change to the flow was needed.

### 2.3 Path E — shared edit and idempotent replay (data flow inspected; required a split)

`SharedFieldWriteBuilder.BuildAsync` copies the verified snapshot **verbatim** (sealed ciphertext byte-for-byte, `CloneValue`) and replaces only the edited items. The released rows of the response therefore mix two provenances:
- `built.EditedRows` — this request's fresh seals (`ProtectAsync`): Mode B;
- every other released row — a byte-identical copy of a row of the Authority-verified partial projection, i.e. stored ciphertext: **Mode A**.

The pre-M2c code decrypted all released rows through one call. The smallest safe change, within the existing authorization contract, is `UpdateFormRecordHandler.DecryptSharedResponseAsync`: it splits by **reference** to the builder's own `EditedRows` (never by inspecting a row), decrypts copies in Mode A and fresh seals in Mode B, and combines the two (`DecryptedFieldValues.Combine` refuses a row decrypted twice). Release scope is unchanged: only rows the editor may read (the Authority's readable items) are decrypted, as before. On an idempotent replay the builder runs again on the current verified snapshot (where the first attempt's edit is now stored content): a value equal to the stored one is refused `NO_CHANGE` (nothing fresh to self-seal); a different value is a new fresh seal; the stored copies stay on Mode A. No Authority, contract or authorization change.

### 2.4 Mode A callers (unchanged call sites, now strict S-8)

`VerifiedRecord.ToValueResponsesAsync`, `CandidateBuilder.ApplyValuesDetailedAsync` (current rows), `SharedFieldWriteBuilder.BuildAsync`, `RestorePlanner.DecryptAsync`, `FieldProtectionMigrationService.MigrateRecordAsync` (Decrypt direction), `FieldProtectionReconciliationService.DecryptsToTheFieldTypeAsync`, `DocumentHandlers.DownloadAsync` (documents, §4). Each one receives rows of an Authority-verified read, or (the two Worker paths) stored rows of the head the Authority names, bound by the Authority later in the same operation (R-M2c-2). All of them were already reached only with stored rows of signed schema ≥ 1; refusing schema 0 does not affect a legitimate path (full suites, §8).

## 3. Field batch reader (Mode A)

`AesGcmFieldEncryptionService.DecryptVerifiedAsync`:
1. **Key-free validation of every row first**: non-null, listed once, `TenantId == organizationId`, the S-8 pair and v2-column presence; each v2 row is turned into M1 envelope objects (`EnvelopeV2ProductReader.FieldBatch.Add`), whose constructors validate the AEAD name, wrap code (closed registry, family equal to the reference family), canonical reference (organization = row organization, purpose `field-value`, version), UUIDv7 data-key id, 32-byte commitment, 60-byte wrapped seed, 12-byte nonce, 16-byte tag, ciphertext bound, field logical type (the creation-fingerprint seal type is refused) and item identity. A refusal here happens **before any key-provider call** (E0-4 reason `UNSUPPORTED_ENVELOPE`).
2. Provider availability check (`KEY_UNAVAILABLE`).
3. v1 rows: the unchanged v1 core (`OpenV1Async`, one unwrap per row, as before).
4. v2 rows: **one** M1 opener call for the whole batch — every request's identity and every data-key group's consistency checked before the first unwrap, then **one unwrap per data-key group**, key commitment compared in constant time before any AEAD, item keys and seeds zeroed.
5. First failure aborts the call: no partial result. Reasons: opener `KeyCommitmentMismatch`/`InvalidDataKey` → `KEY_MISMATCH`, `AuthenticationFailed` → `AUTHENTICATION_FAILED`, other envelope refusals → `UNSUPPORTED_ENVELOPE`, provider unavailable → `KEY_UNAVAILABLE`, provider refusal → `KEY_MISMATCH`. Cancellation propagates unchanged and records no decryption failure.

`DecryptedFieldValues.DecryptAsync` now makes exactly one verified call per operation (rows deduplicated by reference) and refuses a batch that spans organizations. **Organization isolation**: every unwrap is scoped to `(organizationId, FieldValue)`; a row of another organization, or a v2 reference naming another organization, is refused before the provider is asked. **Purpose isolation**: Mode A is FieldValue only; a v2 envelope of another purpose (idempotency key, document) is refused before the provider. **Exact key version**: the reference names the version and is passed to the provider unchanged; the version is part of H0, so the commitment binds it; there is no retry with another version and no "latest" fallback (tests with the counting double and with real Vault, §7).

## 4. Document reader (dark, Mode A)

`IFileEncryptionService.DecryptVerifiedAsync(ciphertext, sink, VerifiedDocumentEnvelope, context)`, with `VerifiedDocumentEnvelope.Of(FileAttachmentVersion)` carrying the stored signed schema and the three v2 columns exactly. S-8: (1, 1, no v2 column) → the unchanged v1 decryptor; (2, 2, every v2 column) → `EnvelopeV2ProductReader.PrepareDocument` (full envelope and version-identity validation, no key) then the M1 document decryptor (seed unwrapped once with the referenced version, key commitment checked before any segment, segment AEAD with H ‖ C_doc); anything else is refused before the provider. `DocumentHandlers.DownloadAsync` now calls this method. The v1 `DecryptAsync` remains on the port but has **no product caller** other than the dispatcher (`OnlyTheVerifiedDocumentDispatcherReachesTheV1DocumentDecryptor`). Streaming and the caller contract are those of v1 and of the M1 cipher (OD-E3-15: verify-then-release is M4): only whole authenticated segments are written, nothing is written before the commitment matches, and a mid-stream failure aborts the response (R-M2c-7).

## 5. Isolation of the v2 core

`EnvelopeV2ProductReader` (internal, inside the isolated namespace) is the only product entry: `FieldBatch` (Add/Open), `PrepareDocument`/`PreparedDocument.DecryptAsync`, `ReasonFor`. It references opener types only. `EnvelopeV2IsolationTests`:
- `OnlyTheApprovedDecryptionServicesReachEnvelopeV2AndOnlyThroughTheProductReader` — outside the namespace, in every Business and Authority assembly, only `AesGcmFieldEncryptionService` and `AesGcmFileEncryptionService` (and their compiler-generated types) reference v2 code, and only the product reader; both are wired;
- `TheProductReaderReachesNoWriterEntryPointOrKeySchedule` — no `FieldSealBatchV2`, no `EnvelopeV2KeySchedule`, no `DocumentEnvelopeV2Cipher.EncryptAsync` (positive control: it does reach the opener and the document decryptor);
- unchanged: every v2 type non-public; no logging/console/trace API in v2 code.

v2 writer isolation is therefore preserved: no product path can seal an Envelope v2 value or document.

## 6. Architecture enforcement (`DecryptionCallSiteTests`)

| Rule | Test |
|---|---|
| Every decrypting call site is reviewed, with its reason (exact list; new entries for both modes, the dispatchers and the product reader) | `EveryDecryptionCallSiteIsReviewedAndAllowListed` |
| Product callers of a self-sealed entry point are exactly A–E | `SelfSealedCallersArePinnedToTheReviewedInventory` |
| An unapproved self-sealed caller is detected (positive control in the test assembly) | `AnUnapprovedSelfSealedCallerIsDetected` |
| A method that reads stored record content (`VerifiedRecordReader.ReadAsync`, `RecordCandidateStager.ReadStagedAsync/ReadStagedBatchAsync`) never also calls a self-sealed entry point, except path A | `NoStoredContentReaderSwitchesToSelfSealedMode` (positive control `ReadThenSelfSeal`) |
| The reviewed verified readers call a verified entry point and no self-sealed one | `TheVerifiedStorageReadersUseTheVerifiedReaderOnly` |
| The v1 document decryptor is reached only by the verified dispatcher | `OnlyTheVerifiedDocumentDispatcherReachesTheV1DocumentDecryptor` |
| The Worker composes the same S-8 dispatchers | `WorkerFieldReaderCompositionTests` (2 tests) |

Static rules are an additional safeguard. The primary enforcement is at run time: Mode A refuses schema 0 and every non-S-8 pair; the Mode B wrappers refuse every row this process did not freshly seal.

## 7. Tests and results

New and updated tests (contract suite unless noted):

| Area | Tests |
|---|---|
| Mode A dispatch | schema 1/envelope 1 opens, schema 0 refused before provider; schema 2/envelope 2 via the v2 opener; 7 mismatched pairs refused with zero unwraps (even with a valid neighbour row); 15 v2-metadata defects refused with zero unwraps; schema-1 rows with any v2 column refused; null/duplicate rows and empty organization refused |
| Provider and integrity | unavailable provider → `KEY_UNAVAILABLE`; wrong key → `KEY_MISMATCH` after exactly one unwrap (no retry, no fallback); unknown wrap refused; tampered ciphertext → `AUTHENTICATION_FAILED`; tampered commitment or data-key id → `KEY_MISMATCH`; transplanted item → `AUTHENTICATION_FAILED` |
| Unwrap count | 2 v1 rows + 5 v2 rows in 2 data-key groups → 4 unwraps; Vault: 1 v1 + 4 v2 in 2 groups → 3 unwraps; documents: one unwrap per version |
| Isolation | foreign-organization v1 and v2 rows, idempotency-key seal presented as a field value, mixed-organization application batch |
| Key version | relabelled version refused after one unwrap (double and Vault); Vault: a retired version 1 is refused while version 2 still opens — no fallback |
| Cancellation | cancelled field batch and document read: `OperationCanceledException`, no failure record, nothing returned |
| Mode B | schema 0 and 1 accepted; (2,1), (2,2), (0,2), (1,2), (3,1) refused with zero unwraps; v2 metadata refused; a relabelled genuine v2 row fails closed on both paths; registry: own seal accepted (also after in-place staging), materialized copy and direct `EncryptAsync` result refused, already-encrypted row passed through `ProtectAsync` not registered |
| Documents | v1 and 3-segment v2 versions open; 7 non-S-8 combinations refused with zero unwraps and nothing written; commitment tamper and segment tamper write nothing; another version's identity refused; foreign organization refused; unavailable provider; cancellation; `VerifiedDocumentEnvelope.Of` exact mapping |
| Worker | real local keyring v2 row through `FieldValueCodec.FormatAsync`; wrap-code relabel refused; unstaged row refused; Worker composition resolves the dispatchers (architecture suite) |
| Provenance C/D/E | C: first attempt and retry answer with fresh seals, committed rows refused by Mode B and readable by Mode A; D: carried values are fresh seals, the verified reader is used for current rows (2 unwraps), idempotent replay again fresh, stored rows of both revisions refused by Mode B; candidate builder refuses unstaged current rows; A: self-check decrypts only its own seal; E (PhaseC5SharedEditTests): edited row Mode B, copied row Mode A (spy), unreadable row never decrypted, replay `NO_CHANGE`, misclassification fails closed at run time |
| Existing fixtures | `PhaseC2RulesAndOptionsTests`, `PhaseC3RepeatingGroupsTests`: rows fed back as the record's current content are marked with signed schema 1 (`VerifiedFixtureRows.AsCommitted`), as staging would; assertion helpers that read candidate output use the self-sealed wrapper. The two test doubles (`SpyEncryption`, `FakeEncryption`) implement the new port member. No known-answer vector, no v1 crypto test and no other v1 test was rewritten |

Results (see `docs/evidence/e3-m2c-readers/`):

| Suite | Before (9810a67) | After |
|---|---|---|
| Contract (`EnterpriseForms.Api.ContractTests`) | 818 / 0 / 9 skipped | **880 / 0 / 12 skipped** (+62 tests; +3 Vault-gated) |
| Architecture | 134 / 0 | **142 / 0** |
| Authority | 849 / 0 / 4 skipped | 849 / 0 / 4 skipped (no Authority change) |
| Integrity | 73 / 0 | 73 / 0 |
| Real Vault 1.20.4 (`scripts/dev/e3-m2c-vault-reader.ps1`) | — | **3 / 0** |

## 8. Enforcement-removal checks

Driver: `docs/evidence/e3-m2c-readers/mutations/m2c_mutations.py` (one mutation at a time: backup, apply, build, run 10 focused contract classes (193 tests) and the full architecture suite (142), time-boxed; restore from the backup and compare SHA-256). Every file was restored with a matching hash; the working-tree diff hash was identical before and after; a final rebuild succeeded. Results: `mutations/results.json`.

| ID | Enforcement removed | Detected | By (failing tests: contract / architecture) |
|---|---|---|---|
| MU-01 | verified reader accepts schema 0 | yes | 3 / 1 |
| MU-02 | verified reader ignores v2 columns on schema 1 | yes | 3 / 0 |
| MU-03 | verified reader skips the v2 completeness check (second layer: product reader) | **Not detected by design**: the second layer (the product reader) still refuses a schema-2 row with a missing v2 column (`MalformedEnvelope` → `UNSUPPORTED_ENVELOPE`), so no test can fail | 0 / 0 |
| MU-04 | verified reader skips the organization check | yes | 1 / 0 |
| MU-05 | verified reader unwraps v1 rows before every row is validated | yes | 25 / 0 |
| MU-06 | v2 failure falls back to the v1 reader | yes | 3 / 0 |
| MU-07 | self-sealed API accepts schema 2 | yes | 1 / 0 |
| MU-08 | self-sealed API ignores v2 metadata | yes | 3 / 0 |
| MU-09 | self-sealed registry check disabled | yes | 5 / 0 |
| MU-10 | ProtectAsync registers an already-encrypted row passed in | yes | 1 / 0 |
| MU-11 | path C switched to verified mode | yes | 0 / 2 |
| MU-12 | path E sends every released row to self-sealed mode | yes | 2 / 0 |
| MU-13 | path E sends every released row to verified mode | yes | 2 / 0 |
| MU-14 | document reader accepts schema 0 | yes | 1 / 0 |
| MU-15 | document reader skips the v2 completeness check (second layer: product reader) | **Not detected by design**: as MU-03, for documents (`PrepareDocument` refuses the missing columns) | 0 / 0 |
| MU-16 | product reader derives the wrap from the reference, not the stored code | yes | 2 / 0 |
| MU-17 | product reader references a v2 writer | yes | 0 / 1 |
| MU-18a | another product type references the v2 core directly | **Ineffective mutation**, not a test gap: a discarded `_ = typeof(...)` is removed by the compiler, so no reference reached the IL. Superseded by MU-18 | 0 / 0 |
| MU-18 | another product type references the v2 core directly | yes | 0 / 1 |
| MU-19 | a verified storage reader switched to self-sealed mode | yes | 0 / 3 |
| MU-20 | application batch step skips the one-organization check (second layer: service) | yes | 1 / 0 |

**18 of 20 effective removals detected.** The two undetected ones (MU-03, MU-15) remove a check that is deliberately duplicated in the product reader; removing both layers together is not covered by a single mutation, and the record states the duplication rather than claiming either layer is independently tested. MU-11, MU-17, MU-18 and MU-19 are caught only by the architecture rules: MU-11 (path C moved to verified mode) is behaviour-preserving at run time, and MU-17/18/19 introduce references or callers that the static rules forbid; For MU-19 the registry would also refuse real stored rows at run time (they are never registered), but no focused contract test drives `VerifiedRecord.ToValueResponsesAsync` with stored rows, so only the architecture rule is evidenced.

## 9. v1 compatibility

- The v1 field and document cryptography is unchanged (the v1 field body moved verbatim into `OpenV1Async`; the document v1 method is unchanged). No known-answer vector file changed; `EncryptionKnownAnswerTests`, `EncryptionRemediationTests`, `EnvelopeV2KnownAnswerTests` and `EnvelopeV2Tests` pass unchanged.
- The ~70 existing test calls of `IFieldEncryptionService.DecryptAsync` are unchanged and pass (their rows are envelope 1, schema 0).
- Every stored product row is schema 1 / envelope 1 (E3-M2b-2 pre-check: 1,038 field and 184 document rows in the local copies), so Mode A accepts all existing data.
- Writers are unchanged: envelope and schema assignment (S-1) untouched; no product v2 writer.

## 10. Residual risks and open items

| ID | Risk / limitation | Status |
|---|---|---|
| R-M2c-1 | Path B decrypted a stored unsigned envelope through the self-sealed API | **Closed for Development/Testing (owner, §12)**: dedicated `OpenCreationFingerprintKeyAsync` accepted, pinned to its one caller; the raw self-sealed API has no product caller other than the two FieldValue wrappers |
| R-M2c-2 | The two Worker Mode A paths decrypt stored rows of the Authority-named head before the Authority binds those rows later in the same operation | **Accepted by the owner as a documented existing Worker design property; remains open for future review** (traced in §11.2: no plaintext crosses an unverified boundary; unchanged by M2c) |
| R-M2c-3 | A failed v2 batch is attributed to the record and the batch's first v2 item (the opener refuses the whole batch) | Accepted limitation |
| R-M2c-4 | The self-sealed registry is in-process: it stops misrouting in code, not a compromised business runtime (A3, already trusted with unwrap) | **Narrowed by the closure (§11.4)**: entries are now bound to the seal they were issued for; mutating a registered instance (bytes, organization, item, reference, v2 columns) voids it. Remaining: A3 is out of scope |
| R-M2c-5 | The static stored-read heuristic covers `VerifiedRecordReader.ReadAsync` and staged-row reads only; other store reads rely on the run-time checks | Accepted limitation |
| R-M2c-6 | C/D/E handlers exercised against in-memory fakes only | Handler decisions evidenced (§11.3). **Real Authority/PostgreSQL integration evidence deferred to a separately authorized Stage B run (owner, §12)** |
| R-M2c-7 | v2 documents stream as v1 (OD-E3-15); verify-then-release is M4 | Carried (M4) |
| R-M2c-8 | Mode A enforces format rules, not provenance: a caller passing unverified rows labelled schema 1/2 passes dispatch. The verified-reader inventory and the existing Authority verification are the guard | Accepted limitation |
| R-M2c-9 | Vault check uses a dev-mode server; no Managed HSM or Azure evidence | Carried (E-6/E-7, PB-12) |
| R-M2c-10 | Two Authority-host startup-refusal contract tests failed once ("Cannot access a disposed object") while an Authority build ran concurrently; 0 failures in 3 isolated reruns and in the isolated full run | Observation (pre-existing flake, unrelated) |
| R-M2c-11 | Creation retry/replay answers item and occurrence ids that were never committed (defect record §12.2) | **Open defect, tracked separately; no correction authorized within E3-M2c (owner, §12)** |
| — | Capability header (`EnterpriseForms-Envelope-Capability`) not sent: moved to M2f (M2b contract §15) | Not in M2c |
| — | V2-IND-01 independent cryptographic review; R-M1-1..8, R-M2a-1..6, R-CARV-*, R-S4-* | Carried |

Stop conditions (amendment §8): none triggered. C, D and E do not decrypt stored content outside Mode A; no authorization-contract change; Mode A does not accept schema 0; no writer change; no migration; CARV and floor unchanged; v2 acceptance not enabled; v1 preserved; the separation is enforceable; no vector changed.

---

## 11. Final security closure (owner conditional acceptance, 2026-11-15)

The owner conditionally accepted E3-M2c for Development/Testing and asked for a focused closure before any commit. Implemented on the same uncommitted tree; no Authority, migration, CARV, floor, writer or vector change; Stage B not run; no Azure, no persistent database.

### 11.1 R-M2c-1 — stored fingerprint MAC key

**Flow inspected.** `CreateFormRecordHandler.RequireSameRequestAsync` runs after the Authority's create-record permission (or, for a child, parent Edit and the verified parent read). It looks up the attempt row by (organization, deterministic record id) and calls `MatchesAsync`. The attempt row is business-DB state, **unsigned**, holding a JSON copy of the envelope sealed under the organization's IdempotencyProtection key, with AAD = (tenant, record, `SealPurposeId`, attempt id, `CreationFingerprintKey/v1`). The decrypted key is used only for a fixed-time HMAC comparison and zeroed; the result is a boolean (match → retry, else 409 `IDEMPOTENCY_CONFLICT`). It never reaches a response, log or audit.

**Change (smallest safe separation).**
- New `IFieldEncryptionService.OpenCreationFingerprintKeyAsync(CreationFingerprintKeyEnvelope, ct)`. The input type carries only the attempt identities and the seven stored envelope fields; it has **no** field id, logical type, signed schema or Envelope v2 member. The service fixes the purpose (IdempotencyProtection), the AAD identity (`SealPurposeId`, `CreationFingerprintKey/v1`, attempt id), envelope version 1 and schema 0, uses the unchanged v1 core, and refuses a key that is not 32 bytes. Envelope ≠ 1 is refused before the key provider (`UNSUPPORTED_ENVELOPE`).
- `MatchesAsync` parses the stored JSON **strictly** (`JsonUnmappedMemberHandling.Disallow`): any unknown member — for example `DataKeyIdentifier`, `KeyWrapAlgorithmCode`, `RowSignatureSchemaVersion` — is refused (treated as a conflict) before any key operation. Existing envelopes have exactly the seven members (current and pre-E-1 legacy form), so legitimate behaviour is unchanged.
- The operation cannot become a general decrypt API: a field-value envelope presented through it fails (FieldValue key ≠ IdempotencyProtection key, and the AAD names a real field, not `SealPurposeId`); a well-formed seal of anything but a 32-byte key is refused; the result is key bytes for the caller to zero.
- Path B left Mode B. The raw single-value `DecryptAsync` keeps its v1 behaviour (three existing key-purpose tests still use it with IdempotencyProtection) but now has **no product caller** except the two FieldValue wrappers.

Enforcement: `OnlyTheCreationFingerprintCheckOpensStoredFingerprintKeys` and `TheRawSingleValueApiIsReachedOnlyThroughTheFieldValueWrappers` (architecture, each with a positive control); the reviewed envelope-row construction inventory (`EnvelopeRowFidelityTests`) records the moved transient decrypt input; runtime tests in `DecryptionClosureRuntimeTests` (legitimate key; moved attempt/record/organization refused; envelope 0/2/3 refused with zero unwraps, also via stored JSON; four v2/schema JSON members refused with zero unwraps; field-value envelope and short key refused; the seal never opens through the self-sealed wrappers). `RecordCreationIdempotencyUnitTests`, `KeyProviderInterfaceV2Tests` (incl. the pre-E-1 legacy envelope) and `KeyActivationTests` pass unchanged.

### 11.2 R-M2c-2 — Worker trust boundary

| | Protection migration, Decrypt direction (`FieldProtectionMigrationService.MigrateRecordAsync`) | Protection reconciliation census (`FieldProtectionReconciliationService.CensusPageAsync` → `DecryptsToTheFieldTypeAsync`) |
|---|---|---|
| 1. Authorization | The Authority's effective `PublishFormSecurityRevision` command (`migration.AuthorityCommandId`): `GetCommittedContentRevisionForReprotectionAsync` names the head under that command; Worker workload identity, no user | The Worker-only protection-verifier credential; the Authority's migration catalogue snapshot names each record's accepted operation |
| 2. Authority verification / binding | At `StageAndCommitReprotectionAsync` → Prepare with `CurrentItems` = the same staged rows: the Authority binds them to its accepted head (R-53) and applies the reprotection rules; Commit re-checks the staged bytes | `verifier.TakeCensusAsync(snapshot, batch)` with the same rows: the Authority verifies each record and decides compliance; the attestation is the completion authority |
| 3. Plaintext before binding | Decrypted wire → typed plaintext candidate row → self-check → canonical bytes sent **to the Authority** (trusted) in Prepare. Nothing is staged, logged, audited (counts only) or returned to a user before Prepare succeeds; a tampered row (even one that authenticates, e.g. an older ciphertext of the same item) is refused at Prepare and nothing is written | Plaintext is discarded inside the method; only per-batch counters survive (`checked`, `failed`, `typeMismatch`), recorded after `TakeCensusAsync` returns. The counters can only make completion **stricter** (`DECRYPT_CHECK_FAILED`, `LOGICAL_TYPE_MISMATCH`, checked ≠ attested occurrences fail the reconciliation) |
| 4. Changed by M2c? | No. Only the dispatch tightened (S-8: schema 0 / mismatched pairs refused); stored rows are schema 1, so behaviour is unchanged | No (same) |

**Finding:** neither path releases plaintext across an unverified trust boundary; no stop condition. **5. Naming:** `DecryptVerifiedAsync` could be read as "this call verifies with the Authority". It does not: it validates the cryptographic envelope and the S-8 format rules only (no RowHmac, Merkle, head or permission). The port documentation now says so explicitly and names the caller contract (rows of an Authority-verified read, or Authority-named stored rows whose plaintext stays inside the trusted operation until the Authority binds them). The reviewed caller list is pinned by `DecryptionCallSiteTests`. A rename (for example `DecryptStoredEnvelopesAsync`) is possible but was not made without approval (it touches every Mode A wrapper and the owner-approved terminology).

**Cryptographic envelope validation vs Authority verification.** Envelope validation (business host, no Authority): closed codes, lengths, canonical reference, organization/purpose/version, key commitment, AEAD — proves the bytes form a well-formed envelope that opens under the named key for the named identity. Authority verification (independent Authority): RowHmac, Merkle membership, accepted head and operation, permission and share scope — proves the rows are the accepted, complete, authorized content. A row can pass the first and fail the second (a replayed older ciphertext of the same item authenticates); only the second authorizes release.

### 11.3 C/D/E handler-level evidence (`HandlerDecryptionModeTests`, no Stage B)

The real `CreateFormRecordHandler` and `UpdateFormRecordHandler`, with the real `RecordCandidateStager`, `VerifiedRecordReader`, candidate builder, shared-edit builder and AES-GCM field service, run against an in-memory business store and an in-memory Authority (`DispatchProxy` fakes: any method not implemented throws). The store returns **new instances on every read**, as EF does with a fresh scope and `AsNoTracking` (guarded by its own test). The Authority fake models heads, prepare/commit, revision conflicts and partial share scope; it does not verify signatures or manifests (that is the real Authority's role, covered by `AuthorizationAuthority.Tests`).

| Scenario | Result |
|---|---|
| Creation, first attempt | 3 encrypted values answered, all Mode B; no Mode A decryption |
| Creation, replay of the accepted revision 1 | nothing prepared again; answer Mode B (fresh seals); committed rows never decrypted; committed rows refused by the self-sealed step |
| Creation, retry after a crash between stage and commit | the earlier attempt's staged rows are committed; the answer is Mode B over the retry's own seals; answered item ids ∩ committed item ids = ∅ (see R-M2c-11) |
| Creation, replay after the record was edited (revision 2) | answered by the verified read (Mode A only, current values), never self-sealed |
| Owner update, first attempt | stored current rows Mode A (carry-forward), answer Mode B |
| Owner update, idempotent replay (new revision, same key) | nothing prepared again; committed rows Mode A, answer Mode B; stale revision → 409 with nothing self-sealed |
| Shared edit, first attempt (mixed rows) | edited row Mode B; copied readable row Mode A; the unshared encrypted field is never decrypted and absent from the response |
| Shared edit, replay | same value → `NO_CHANGE` 422, nothing self-sealed, unshared field never decrypted; stale revision → 409 with nothing decrypted |

### 11.4 Provenance scope and lifetime

| Property | How it holds | Evidence |
|---|---|---|
| Scoped to the trusted operation | An entry is keyed by object identity; only code holding the instance can present it. Stored reads always create new instances (every encrypted-row read is `AsNoTracking` from a fresh `DbContext`; the only tracked query projects ids). No service holds envelope instances | store code review; harness guard test; `ProvenanceDoesNotCrossServiceInstancesThroughSharedState` (the field service has exactly one instance field, its key provider) |
| Bound to organization and purpose | **New:** each entry records the binding it was issued for (organization, record, field, item, logical type, FieldValue purpose, SHA-256 over every envelope byte and column); the check requires the instance to still match. The wrappers decrypt with FieldValue only | `MutatingARegisteredSealVoidsItsProvenanceButStagingMetadataDoesNot` (bytes, organization, reference, v2 column changes refused; stager metadata accepted) |
| No inheritance by matching ciphertext | Identity, not value | equal-bytes twin refused (same test; also `TheNamedSelfSealedEntryPoints…`) |
| No accumulation | `ConditionalWeakTable`: entries die with their keys | `RegistrationsDoNotKeepSealsAliveOrAccumulate` (50 seals, all collected) |
| Failed / cancelled operations | Registration happens only after a successful seal; a failed or cancelled seal produces no instance; a failed operation's sealed instances become unreachable and are collected | `AFailedOrCancelledSealLeavesNoProvenance`, the weak-reference test |
| No cross-request authorization through a singleton | The registry is static but confers nothing without the instance; no singleton stores instances | as above |

### 11.5 Closure enforcement-removal checks

Same driver, the focused classes extended with the new test classes (233 contract tests) plus the architecture suite (144). Results in `docs/evidence/e3-m2c-readers/mutations/closure-results.json`.

| ID | Removal | Detected (contract / architecture failures) |
|---|---|---|
| MU-11 | path C switched to verified mode (rerun) | yes (3 / 2) — now also at run time by the handler tests |
| MU-12 | path E all self-sealed (rerun) | yes (4 / 0) |
| MU-13 | path E all verified (rerun) | yes (3 / 0) |
| MX-1 | registry ignores the recorded binding | yes (1 / 0) |
| MX-2 | fingerprint JSON parsed leniently | yes (4 / 0) |
| MX-3 | fingerprint operation skips its envelope-version check | **not detected by design**: the v1 core refuses envelope ≠ 1 before any unwrap (redundant layer) |
| MX-4a | key-length check removed with `if (false)` | ineffective (does not compile: unreachable code is an error); superseded by MX-4 |
| MX-4 | fingerprint key of any length accepted | yes (1 / 0) |
| MX-5 | fingerprint check back on the raw self-sealed API | yes (3 / 5) |

Every file was restored with a matching hash; the working-tree diff hash was identical before and after.

### 11.6 Results after the closure

| Suite | Before closure | After closure |
|---|---|---|
| Contract | 880 / 0 / 12 skipped | **901 / 0 / 12 skipped** (+8 handler, +13 closure runtime) |
| Architecture | 142 / 0 | **144 / 0** (+2) |
| Authority, Integrity, real Vault | 849/0/4, 73/0, 3/0 | not rerun: no Authority, integrity-format or provider change in the closure (the Vault tests exercise only `DecryptVerifiedAsync`, unchanged) |

Confirmed unchanged: no Authority file, no database migration, CARV `CurrentVersion = 1`, no v2 writer (isolation tests), no schema-2 Authority acceptance, no provider fallback (v2 and fingerprint paths make exactly one unwrap attempt), v1 vectors untouched, Stage B not run.

---

## 12. Final owner acceptance (2026-11-15)

### 12.1 Decisions

| Item | Owner decision |
|---|---|
| E3-M2c | **Accepted for Development/Testing only.** Exactly one signed local commit authorized; no push, merge, deployment or further milestone (E3-M2d+) |
| R-M2c-1 | **Closed for Development/Testing.** The dedicated fingerprint MAC-key opener (`OpenCreationFingerprintKeyAsync`) is accepted |
| R-M2c-2 | **Accepted as a documented existing Worker design property; remains open for future review** |
| R-M2c-6 | Real Authority/PostgreSQL integration evidence **deferred to a separately authorized Stage B** run |
| R-M2c-11 | New creation-retry item-id defect: **tracked separately; no correction within E3-M2c** (§12.2) |
| `DecryptVerifiedAsync` | **Name kept**, with the documentation that it validates the envelope and the S-8 format, not Authority provenance (§11.2) |

No production release gate is closed or weakened by this acceptance: V2-IND-01, PB-11/PB-12 release governance, Azure staging evidence (Managed HSM, Confidential Ledger, real Entra), RB-CARV-1 production configuration controls (R-CARV-22), Envelope v2 activation prerequisites (M2d Authority v2 rules and CARV 2, M2e data-key ledger and acceptance switch, M3 catalog, M4 writers, E-9) and the Level 2 adversarial gates remain open.

> **Correction (2026-11-15, E3-M2d, documentation only).** The sentence above originally read "M2d ledger/acceptance". Both governing contracts place the data-key ledger and the acceptance switch in **E3-M2e** (`E3_M2_AUTHORITY_DATABASE_CONTRACT.md` §10; `E3_M2B_IMPLEMENTATION_CONTRACT.md` §15); E3-M2d is the dark Authority v2 rule set, version dispatch, the downgrade guard and CARV 2 (`E3_M2D_IMPLEMENTATION_RECORD.md`). The accepted E3-M2c implementation, its evidence and its owner decisions are unchanged.

### 12.2 Defect record R-M2c-11 — creation retry answers uncommitted item ids

**Status:** open, tracked separately. **The correction is outside E3-M2c** and was not implemented; E3-M2c changed none of this behaviour (it only changed which decryption mode formats the response).

**Scenario.** `CreateFormRecordHandler.CreateCoreAsync` builds its candidate on every attempt with `CandidateBuilder.ApplyValuesDetailedAsync([], …, creating: true)`, which gives every new value (`FieldValueItemId`) and every new repeating-section entry (`FormRecordGroupId`) a fresh random UUIDv7. The response is formatted from that candidate. Two branches answer from a candidate that is **not** the one the Authority accepted:
1. **Retry after a crash between stage and commit** — the earlier attempt staged its candidate, the process stopped before Commit; the retry commits the earlier attempt's **staged** rows (`CommitInitialContentAsync` step 1) but answers with its own new candidate.
2. **Replay of an accepted creation** (`head.CommittedContentRevision == 1`, same idempotency key, same fingerprint) — the retry answers with its new candidate and the Authority's head.

(Discovered with branch 1 in `HandlerDecryptionModeTests.CreationRetryAfterACrashBetweenStageAndCommitCommitsTheStagedRowsButAnswersWithItsOwnFreshSeals`, which asserts the answered and committed item ids are disjoint; review of the code shows branch 2 has the same cause.)

**Difference.** The answered values, field ids, record id, revision and operation id are correct (the fingerprint proves the request identical). The answered `field_value_item_id` of every value and the `form_record_group_id` of every repeating-section entry created by the request differ from the committed ones and exist nowhere in BusinessDb or the Authority.

**Potential downstream effects.**
- A client that sends an update with a repeating-section entry id taken from that response is refused `OCCURRENCE_UNKNOWN` (422): the builder never adopts unknown ids. Fails closed; the user must reload.
- Client-side state keyed by `field_value_item_id` (for example UI correlation, optimistic caches) disagrees with the next read.
- No request accepts a `field_value_item_id` from the client (shares and edits are keyed by field key; documents by attachment id), so an uncommitted id cannot address, change, share or reveal anything. **No security or authorization consequence identified**: the ids name nothing, and access decisions use only the Authority's accepted items.

**Expected behaviour.** A retry or replay response must identify the actually committed items: the item and entry ids of the accepted revision (for example by answering from the verified committed rows, as the revision > 1 branch already does through `CompletedAsync`, or by deriving the candidate's ids deterministically from the request so every attempt produces the same ids). The choice needs its own design decision (it interacts with the self-sealed response path C and the idempotency fingerprint).

**Required future tests.**
- Reproduction: crash between stage and commit, retry → answered item and entry ids equal the committed ones (handler-level, extending `HandlerDecryptionModeTests`), and the same for a plain replay at revision 1.
- Regression: a follow-up update that references a repeating-section entry id from the retry response succeeds; a fresh first attempt is unchanged; the decryption-mode properties of §11.3 still hold (no stored row self-sealed).
- Stage B equivalent against the real Authority and PostgreSQL, when separately authorized.

