# E3-M2d — Dark Authority Envelope v2 Rules, CARV 2 and Floor 2: Implementation Record

**Status.** Implemented for **Development/Testing only** on `e3/m1-envelope-v2-core`, on top of the signed E3-M2c commit
`8915f77c10299c08b27e36d40b7a2388d1e38d0f`. **Accepted by the owner for Development/Testing (2026-11-15, §12); OA-M2d-1 to OA-M2d-8
approved; one signed local commit authorized (no push, merge or deployment).** Owner-led work; not an
independent review, not a certification, not production-ready. Envelope v2 is **not** accepted anywhere: no new v2 row is accepted at
Prepare and no v2 row is committed at all. Floor 2 was installed **only in disposable PostgreSQL containers** (owner decision D-3); no
persistent database, deployed Release B configuration, Azure service or deployment was touched; Stage B did not run.

Governing documents: `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` (package; §2.3, §2.4, §3.5, §10 E3-M2d), `E3_M2B_IMPLEMENTATION_CONTRACT.md`
(§3.3, §5, §6, §8, §15, §19–§23; supersedes the package where they differ), the E3-M2d readiness report (owner review, 2026-11-15).
Evidence: `docs/evidence/e3-m2d/`.

**Review:** owner-led. Not an independent security review and not a certification.

---

## 1. Owner decisions (2026-11-15)

| ID | Decision | Applied as |
|---|---|---|
| D-1 | M2b §15 governs M2d where it supersedes M2 §10 | Scope §2 (version-split rules, downgrade guard, CARV 2, floor 2) |
| D-2 | Advance `ContentAcceptanceRules.CurrentVersion` to 2 | §5 |
| D-3 | Implement the floor-2 migration and generalized Release B floor support; floor 2 only in disposable PostgreSQL | §6; the M2d gate (§8) is the only place floor 2 was installed |
| D-4 | Minimum-floor semantics with exact validation of recognized floor definitions; unknown or unverifiable floors fail closed | §6.3 |
| D-5 | Preserve exact Prepare/Commit rule-version equality | §5 (`P = V` at Commit, `C = P` in the database, unchanged) |
| D-6 | Implement the v2-to-v1 downgrade guard | §4.3 |
| D-7 | Classify carried rows against the current accepted head only | §4.3 |
| D-8 | Establish the actual `FieldValueCodec` ciphertext bound before implementing the v2 limit | §3 (12,000 bytes) |
| D-9 | Defer environment-specific wrap-code policy to M2e; reject unsupported codes; accept no new v2 rows | §4.2 (codes {1, 2} in every environment; 3/4 refused); §4.3 |
| D-10 | Implement DK-3 classification in M2d | §4.3 |
| D-11 | Approve the refusal codes and private OpenAPI updates | §4.5; `trust_authority/trust-authority.openapi.yaml` header note |
| D-12 | M2d remains a dark implementation | §4.4 (Commit refuses every v2 row) |

---

## 2. Scope delivered

- **Version-dispatched Authority envelope rules** (`EnvelopeV2Rules.cs`, new; dispatch hooks in `EnvelopeRules.cs`): v1 rows keep the E3-M2a
  rules unchanged; schema-2 / envelope-2 rows are judged by the v2 rules only.
- **Prepare-only v2 rules** (CARV 2): carried/new classification against the verified current head, DK-3, refusal of every new v2 row,
  the downgrade guard, v1-to-current-v2 wrapped-key separation.
- **Commit**: the v2 shape and batch rules re-derived from the staged bytes, then refusal of every v2 row (dark).
- **Census**: version-specific structural rules; v1 behaviour unchanged.
- **CARV 2**: `CurrentVersion = 2`; no ordinary migration (the CARV CHECKs and state guard already admit any positive version).
- **Floor 2**: a separate floor migration `20261115200000_AuthorityContentAcceptanceRuleFloor2` in the floor-only context, its exact SQL
  (`ContentAcceptanceFloor2Sql`), a recognized-floor registry, the generalized write fence, policy, readiness and floor command.
- **Not delivered (by decision):** the data-key ledger (DK-4..DK-8), the acceptance switch, the capability header, the census tables
  (M2e/M2f/M2g), any v2 writer, any business change, any production configuration.

---

## 3. Pre-implementation checks (owner §3)

### 3.1 Field ciphertext bound (D-8)

| Fact | Source |
|---|---|
| A v2 field ciphertext is exactly the strict UTF-8 bytes of the canonical wire string; no framing (the 16-byte tag is a separate column) | `FieldEnvelopeV2.cs:200-208` (`StrictUtf8.GetBytes`; `ciphertext = new byte[plaintext.Length]`) |
| v1 seals the same canonical wire string (UTF-8, no framing) | `AesGcmFieldEncryptionService.cs:58-63` |
| The wire string comes from `FieldValueCodec.Format` of a validated typed row | `FieldValueCodec.cs:100-123`, `:166-182` |
| String values: length ≤ `MaxLength ?? 4000` UTF-16 code units | `FieldValueCodec.ValidateString` (`:202`) |
| `maxLength` is capped at 1..4000 by the layout parser | `FormLayout.cs:490` |
| Option keys: `^[a-z][a-z0-9_]{0,63}$` (ASCII, ≤ 64) | `FormDesignerHandlers.cs:26`; column `HasMaxLength(64)` |
| Both caps exist since the first application commit | `git log -S` → `556c562` (Stage B) |

Largest UTF-8 length per logical type: String 4,000 × 3 = **12,000** (a BMP code unit is at most 3 bytes; a surrogate pair is 4 bytes
for 2 units; a lone surrogate cannot be encoded by the strict encoder, so no ciphertext exists for it); Option 64; Guid 36; Decimal 24
(`-999999999999999999.9999`); Integer 20; DateTime 20; Date 10; Boolean 5. **Bound: 0 ≤ length ≤ 12,000** (0 = AES-GCM of the empty
string, allowed in v2 by the contract). The M1 reader's own cap (`MaximumFieldCiphertextBytes = 1 MiB`) is looser; the Authority's is
the tighter, codec-derived bound, which the contract (package §2.3) proposed and which needs no amendment.

**Existing data (read-only):** both persisted business volumes were copied into scratch volumes (sources mounted read-only, never
started), queried by a no-network `postgres:18.0` container (aggregates only) and deleted: developer stack 602 encrypted field rows
(largest ciphertext 75 bytes), older default project 436 (39 bytes); none empty, none above 12,000, no v2 row. (Local
Development/Testing data only; R-S4-4's production inventory remains open.)

**Cross-component evidence:** a 4,000 × `€` value sealed by the real M1 core is exactly 12,000 bytes and passes the Authority; one byte
more (12,001) is refused (`EnvelopeV2AuthorityCompatibilityTests`).

### 3.2 Document ciphertext formula

`ciphertext_size_bytes = plaintext_size_bytes + 20 × max(1, ⌈plaintext_size_bytes / 1,048,576⌉)` (4-byte length prefix + 16-byte tag
per segment; the object carries no in-object header; one segment for an empty document; `DocumentEnvelopeV2Cipher`). Pinned against
the E3-M1 vectors: D0 0→20, D1 37→57, D2 1,048,576→1,048,596, D3 1,048,581→1,048,621, D4 2,097,152→2,097,192 (all match). All 184
stored (v1) document rows of the assessed copies satisfy it as well. Overflow and negative sizes are refused.

### 3.3 Floor ordering and compatibility; floor 2 beside floor 1

| Question | Finding |
|---|---|
| Can CARV 2 run on floor 1? | Yes, with no RB-CARV-1 change: the floor-1 CHECK admits P ≥ 1, the write fence accepted floor 1 with build 2, readiness reports `v2 (floor 1)`, the connection marker becomes `carv-v2` |
| Could floor 2 be installed with the accepted floor-1 code? | No: `ReleaseBFloor = 1` was exact; the fence returned `FloorUnverifiable` for any other required floor and recognized only the floor-1 migration and constraint; installing floor 2 (add-then-drop) under `RequiredFloor = 1` would have refused every write (`FloorRecordedButMissing`); the floor command accepted only `1` and called `Database.Migrate()`, which would apply **every** pending floor |
| Does generalizing weaken floor 1? | No (§6.3): with only floor 1 installed, every floor-1 decision is unchanged (the existing evaluator matrix passes unchanged except the "floor above this build" case, now floor 3); minimum semantics only admit a **higher recognized** floor with its exact definition; a recorded higher floor whose constraint is missing is refused even when floor 1 is required |
| Ordering | Floors are raised one step at a time (floor 2 requires floor 1 installed, recorded and alone), never lowered, add-then-drop in one transaction under ACCESS EXCLUSIVE |

No stop condition arose.

---

## 4. Version-dispatched Authority rules

### 4.1 Dispatch

`EnvelopeRules.Enforce` (the entry point of Prepare's `ParseCandidate` and of Commit) skips v2 rows in its v1 loop and then calls
`EnvelopeV2Rules.Enforce`. `EnvelopeRules.EnforceAgainstCurrent` indexes and judges v1 rows only. `EnvelopeRules.Validate` (the
single-row v1 validator) is unchanged and still refuses schema 2. S-4 (`EnvelopeSchemaTie`) runs first and admits only (1,1) and (2,2);
S-9 pins types and nullability. **`AllowedEnvelopeVersions` stays `{1}`** and `ContentDeltaRules.AllowedEnvelopeVersions` still aliases
it, so the share-editor allow-list is not widened (an architecture test pins both).

### 4.2 v2 shape (every v2 row; Prepare, Commit, census)

Envelope version 2; `AES-256-GCM`; wrap code ∈ {1 `localdev`, 2 `vault-transit`} (60-byte wraps; codes 3/4 reserved and refused;
environment-specific restriction deferred, D-9); key reference `family:organization:purpose:version` with the family equal to the wrap
code's, the organization equal to the row's (lowercase `D`), the row kind's purpose, a decimal version without sign or leading zero,
1–128 characters of `[A-Za-z0-9._:/-]`, never the legacy v1 literal; data-key id a UUIDv7 with the RFC 9562 variant, not all-zero;
commitment exactly 32 bytes; fields: nonce 12, tag 16, ciphertext 0..12,000, closed logical type; documents: nonce prefix 8, segment
1,048,576, the §3.2 size formula. NULL in a v2 column is refused (`ENVELOPE_COLUMN_MISSING`; S-9 refuses it first; NULL is tested
explicitly because `RowCanonicalColumn.AsBytes()` reads a NULL bytes column as empty).

### 4.3 Batch, separation and transitions

| Rule | Where | Reason |
|---|---|---|
| Rows grouped by data-key id (all v2 rows, so Commit can re-derive it): one entity kind; identical commitment, wrap code, wrapped key and reference; distinct items; distinct nonces; one document per id | Prepare, Commit | `ENVELOPE_BATCH_INCONSISTENT` (`ENVELOPE_NONCE_REUSED` for a repeated nonce) |
| No v2 wrapped key equal to a v1 wrapped key of the candidate; one wrapped key under one id only | Prepare, Commit | `ENVELOPE_WRAPPED_KEY_REUSED` |
| Carried = the current accepted head (D-7) holds the same item as a schema-2 row of the same kind, every signed column byte-identical except the operation binding; otherwise New | Prepare only | — |
| DK-3 (D-10): a New row's id equals the id of a Carried row or of any current row | Prepare only | `DATA_KEY_IDENTIFIER_REUSED` |
| Every New v2 row refused (D-12) | Prepare only | `ENVELOPE_VERSION_NOT_ENABLED` |
| Downgrade (D-6): an item that is a v2 envelope row in the current head becomes a v1 envelope row (field or document; any write path) | Prepare only | `ENVELOPE_DOWNGRADE` |
| A v1 candidate row reuses a wrapped key of a current v2 row | Prepare only | `ENVELOPE_WRAPPED_KEY_REUSED` |

Removing a v2 item is permitted. Encrypted → plaintext remains the separately governed deprotection path. All refusals happen before
classification, authorization or signing (`ContentService.PrepareCoreAsync`, after `BindAcceptedSnapshotAsync`).

### 4.4 Commit (dark) — a decision for the owner

Commit holds only digests of the current revision, so it cannot re-derive the carried/new decision. Two designs were possible:

1. Commit relies on CARV 2 for that decision and accepts carried v2 rows. A Prepared operation recorded outside this build's Prepare
   with a version-2 stamp could then commit a v2 replacement of an existing item, which breaks the accepted E3-M2b-2 property
   that schema 2 / envelope 2 is never accepted at Commit (S-4 gate S4-T1/T2). This would hit the stop condition "a new v2 row can
   be accepted".
2. **Implemented:** Commit re-applies the v2 shape and batch rules to the staged bytes, then refuses **every** v2 row
   (`ENVELOPE_VERSION_NOT_ENABLED`; abort `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`). Carried rows are classified and validated at
   Prepare but cannot be committed in E3-M2d; no product path produces a v2 head, so nothing real is affected.

Committing carried v2 rows is left to the activation increment, E3-M2e, together with the ledger (DK-6). **Approved by the owner
(OA-M2d-1, 2026-11-15, §11).**

### 4.5 Census

`EnvelopeV2Rules.CensusState`: v1 rows judged exactly as before (E3-M2a rules, one value per v1 wrapped key) plus the v1/v2 separation
(reachable only when v2 rows exist); v2 rows by the v2 shape, batch consistency and separation. A v2 batch is no longer counted as an
anomaly (F-14).

### 4.6 Refusal codes (D-11)

All `422 InvalidCommand`, `ENVELOPE_INVALID: <reason>`, not retryable, identifier-free: `ENVELOPE_VERSION_NOT_ENABLED`,
`ENVELOPE_DOWNGRADE`, `DATA_KEY_IDENTIFIER_REUSED`, `ENVELOPE_BATCH_INCONSISTENT`, `ENVELOPE_WRAP_ALGORITHM_UNSUPPORTED`,
`ENVELOPE_KEY_REFERENCE_FAMILY`, `ENVELOPE_DATA_KEY_IDENTIFIER_INVALID`, `ENVELOPE_COMMITMENT_LENGTH`, `ENVELOPE_CIPHERTEXT_LENGTH`,
`ENVELOPE_CIPHERTEXT_SIZE`; Commit abort reason `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`. The compiled OpenAPI artifact is unchanged
(no endpoint or schema changed); the private reference documents the codes.

---

## 5. CARV 2

| Rule | Behaviour |
|---|---|
| Prepare | stamps P = 2 |
| Prepare replay of a Prepared operation with P ≠ 2 (a version-1 operation, NULL, 3) | `409 OPERATION_RULES_OUTDATED`, nothing written (unchanged rule) |
| Commit of a Prepared operation with P ≠ 2 | aborted with `OPERATION_RULES_OUTDATED` before any verification (unchanged rule; D-5) |
| Commit stamp | C = 2; the database enforces C = P exactly |
| Version-1 build committing a P = 2 operation | refused by that build's own rule, and by the database (C = 1 ≠ P = 2) |
| Finished operations | replayed first, whatever their stamps (P = C = 1 and NULL/NULL included); terminal rows immutable (state guard) |
| Connection marker | `carv-v2`; a configured stale `carv-vN` token is now replaced, not kept beside it |

Why a bump: the classification, DK-3, the new-v2 refusal and the downgrade guard are Prepare-only (Commit has no current bytes), the
CARV contract's bump criterion (`ContentAcceptanceRules` §3.3). R-CARV-7 applies: operations in flight across the bump are refused and
re-staged.

---

## 6. Floor 2

### 6.1 Migration and SQL

`20261115200000_AuthorityContentAcceptanceRuleFloor2` (floor context only; ordinary `--migrate` never sees it), Up =
`ContentAcceptanceFloor2Sql.UpSql`: `SET LOCAL lock_timeout = '5s'`; `LOCK … ACCESS EXCLUSIVE`; under the lock: the E3-M2b-1 fence
complete; floor 1 installed, recorded and the only floor; no unexpired Prepared operation with P NULL or < 2; no connected session able
to update operations whose application name lacks a `carv-v` marker ≥ 2 (the LISTEN-only connection excepted); then add
`ck_content_operation_rule_floor_v2` (`NOT VALID`; Prepared and Committed rows need P IS NOT NULL AND P ≥ 2) and drop floor 1, in one
transaction. Down always refuses. A separate reinstatement SQL re-adds floor 2 (and drops a remaining floor 1) when floor 2 is recorded
but its constraint is missing. **No floor SQL may contain a brace** (EF's `ExecuteSqlRaw`, used by the reinstatement, reads braces as
format items: found by the gate, run 1; a unit test now forbids it).

### 6.2 Activation command

`--apply-content-acceptance-floor 1|2 [--reinstate-recorded-floor]`, migration identity only: the floor must be recognized and ≤ the
build; refused with `--migrate`; refused if a higher floor is recorded (never lowered) or a lower floor is not recorded (no skipping);
migrates the floor context to **exactly** the requested floor's migration (`IMigrator.Migrate(target)`, never `Database.Migrate()`);
exit 4 for a recorded-but-missing floor without the reinstate flag. Operations procedure: `E3_M2B1_CARV_OPERATIONS.md` §8.

### 6.3 Generalized Release B fence, policy and readiness (D-4)

- **Policy:** `RequiredFloor` must be a recognized floor ≤ `CurrentVersion` (1 or 2); 0 below minimum; 3 unsupported. Release A unchanged.
- **Write fence (in every Release B content write transaction, after the ROW EXCLUSIVE lock), order:** the E3-M2b-1 fence checks
  (unchanged); highest floor above the build → `FloorAboveBuild`; no floor constraint → `FloorRecordedButMissing` if any floor is recorded,
  else `FloorNotInstalled`; more than one floor constraint, an unparseable or unrecognized one → `FloorDefinitionMismatch` (or `FloorLowered`
  for a lone lower one); installed < required → `FloorLowered`; definition ≠ the recognized one (NOT VALID or validated) →
  `FloorDefinitionMismatch`; a recorded floor above the installed one → `FloorRecordedButMissing`; a recognized floor ≤ installed not recorded
  → `FloorNotRecorded`; a required floor that is unrecognized or above the build → `FloorUnverifiable`.
- **Readiness:** a recorded floor above the installed one is `ContentAcceptanceRuleFloorMissing`; the reader also reports the highest
  recorded floor.
- **Floor-1 behaviour:** unchanged for every state with only floor 1.

---

## 7. Historical compatibility

- Reads and verification never consult the envelope rules or CARV; RowHmac v1 / Merkle v1 and every vector file are unchanged.
- v1 rows are judged by the v1 rules in every phase (Prepare, Commit, census).
- Committed operations with P = C = 1 (or NULL/NULL) replay unchanged under floor 1 and floor 2 (gate D-X5, D-F13); floor 2 is `NOT VALID`
  and terminal rows are immutable.
- No stored data changes; no migration rewrites rows.

---

## 8. Evidence

All runs on 2026-11-15, on the final restored tree. Test binaries run directly (no `dotnet test` wrapper). Stage B not run.

### 8.1 Test totals

| Suite | Before (E3-M2c, record §12) | Now | Change |
|---|---|---|---|
| Authority | 849 / 0 / 4 | **972 passed / 0 failed / 4 skipped** (976) | new: `EnvelopeV2RulesTests`, `ContributorWriteProtocolTests.EnvelopeV2`, `ContentAcceptanceFloor2Tests`, the version-1 stamp case of the CARV abort theory; the 29 existing test cases that failed on the change updated to the approved behaviour (§8.5) |
| Contract | 901 / 0 / 12 | **908 / 0 / 12** (920) | new: `EnvelopeV2AuthorityCompatibilityTests` (real M1 core + real local key provider + business encoder → Authority rules), a Release B floor-2 startup case; one startup case moved from floor 2 to 3 |
| Architecture | 144 / 0 | **149 / 0** | new: `EnvelopeV2RuleBoundaryTests` |
| Integrity | 73 / 0 | **73 / 0** | none |
| Vectors | Python 46, Node 46 | **46 / 46 each, pass** | v1 vector files and the schema-2 vector file byte-identical (no diff) |

The skips are the real-Vault classes (they need their own scripts), unchanged.

### 8.2 Disposable-PostgreSQL gate

`scripts/dev/e3-m2d-carv2-pg-gate.ps1` (harness `tools/experiments/e3-m2d-carv2-gate/`): this tree's Authority Release build in-process
(Release B with required floor 1, Release B with required floor 2, Release A) on an ordinary runtime role, the version-1 build (a Release
build of `git archive 8915f77`) in a separate agent process, one fresh `postgres:18.0` container per run. **Runs of record: 47 / 47 checks
× 2, container removed each time** (`docs/evidence/e3-m2d/pg-gate/`).

| Owner item | Checks | Observed |
|---|---|---|
| Floor-1 compatibility | D-00, D-00b, D-A1..A5 | `--migrate` applies no floor; the floor command with `1` applies exactly floor 1 (floor 2 not applied implicitly); readiness `v2 (floor 1); phase=ReleaseB requiredFloor=1`; 10 writes committed P = C = 2; a Release B instance requiring floor 2 refuses every write on floor 1 (`FloorLowered`, nothing advances) and is not ready |
| Cross-build version-1 / version-2 | D-X1..X5 | version-1 Prepare (P = 1) → version-2 Commit refused `OPERATION_RULES_OUTDATED`, aborted, nothing advances; version-2 Prepare → version-1 Commit refused by that build's rule; a forced `C = 1` on a P = 2 operation refused by the database (23514 commit CHECK); version-1 end to end on floor 1 commits (mixed fleet compatible before floor 2); version-2 Prepare replay of a version-1 operation refused, nothing written |
| Historical replay | D-X5, D-F13 | the committed version-1 operation (P = C = 1) replays under floor 1 and under Release B floor 2 and writes nothing |
| Envelope v2 dark on the real persistence path | D-V1..V5 | real Prepare of a new v2 field / document refused `ENVELOPE_VERSION_NOT_ENABLED` before any signature, operation or leaf; a seeded P = 2 operation holding a well-formed v2 row: signatures verified, then refused and aborted `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`, record and organization snapshots unchanged; S-4 still refuses a mismatched pair at Commit; control commits |
| Floor-2 preconditions and refusals | D-F0..F5 | floor 2 before floor 1 refused (exit 2); `--migrate` combination and floor 3 refused; a connected `carv-v1` session blocks the raise (exit 3); an unexpired version-1 operation blocks it (exit 3); a held writer lock times out (`55P03`, exit 3); nothing applied each time; an already-expired lagging version-1 operation does not block |
| Activation and concurrency | D-F6..F9 | applied while four Release B (floor 1) writers ran: 12 of 12 writes OK, floor 1 dropped in the same transaction, both floor migrations recorded; the live definition equals the recognized one exactly; readiness ready at required 1 and 2; writes at both |
| Monotonicity | D-F10 | re-apply 2 is a no-op (one history row); apply 1 after 2 refused "never lowered" |
| Database floor 2 against the version-1 build | D-F11, D-F12, D-F14 | version-1 Prepare after floor 2 refused (that build's alert names `ck_content_operation_rule_floor_v2`), nothing persisted; a lagging version-1 instance cannot commit its pre-floor operation, nothing advances; direct insert stamped 1 refused (23514 floor_v2), stamped 2 accepted |
| Missing and altered floors (Release B transactional) | D-M1..M7 | floor 2 dropped → writes requiring 1 and 2 both refused `FloorRecordedButMissing`, both not ready; a Release A instance still writes (R-CARV-21, documented); exit 4 then reinstate with the flag, writes resume; floor 2 weakened in place → `FloorDefinitionMismatch`; floor 2 replaced by floor 1 → `FloorRecordedButMissing` (required 1) / `FloorLowered` (required 2), reinstated; two floors → `FloorDefinitionMismatch`; history row deleted → `FloorNotRecorded`; exact catalog restored → ready, verdict none |
| No advancement after refusals | all refusal checks | record snapshot (head, root, operation, quarantine, commits, leaves, identities, committed operations) and organization snapshot (content-integrity and authorization revisions, checkpoints, journal, commits, identities) unchanged |

**Development runs (not of record).** Run 1: 38 / 47. It found a real defect: the floor-2 marker regex `carv-v([0-9]{1,9})` contained
braces, which EF's `ExecuteSqlRaw` (used by the reinstatement path only) reads as format items, so `--reinstate-recorded-floor` crashed
with an unhandled exception (`-532462766`; nothing applied, fail closed). D-M2..M7 then failed as consequences. Fixed by
`carv-v([0-9]+)')::numeric` (no braces, no overflow) and a unit test that forbids braces in all floor SQL. D-F11/D-F12 also failed
because the version-1 store reports the database refusal only in its alert; the agent now captures that build's alerts. Run 2:
47 / 47.

### 8.3 Enforcement-removal checks

Driver `docs/evidence/e3-m2d/mutations/m2d_mutations.py`: one mutation at a time, backups outside the repository, every file restored
and its sha256 verified (**33 / 33 restored**). Non-constant conditions were used, so every mutant compiled with warnings as errors.

| ID | Removed or weakened | In-process (Authority + architecture classes) | PostgreSQL gate |
|---|---|---|---|
| MD-01 | the version dispatch | detected (40) | — |
| MD-02 | the v2 candidate rules reached from the entry point | detected (14) | — |
| MD-03 | the new-v2 refusal | detected (17) | — |
| MD-04 | the downgrade guard | detected (5) | — |
| MD-05 | DK-3 | detected (7) | — |
| MD-06 | the Prepare call of the Prepare-only v2 rules | detected (22) | — |
| MD-07 | the Commit refusal call | detected (4) | — |
| MD-08 | the Commit refusal weakened | detected (5) | — |
| MD-09 | batch consistency (commitment) | detected (3) | — |
| MD-10 | v1/v2 wrapped-key separation in a candidate | detected (2) | — |
| MD-11 | v1 reuse of a current v2 wrapped key | detected (2) | — |
| MD-12 | the codec-derived field bound | detected (1) | — |
| MD-13 | the document size formula | detected (8) | — |
| MD-14 | the v1 cross-revision rule indexing v2 rows (F-14) | detected (14) | — |
| MD-15 | the census v2 branch | detected (1) | — |
| MD-16 | CARV reverted to 1 | detected (32) | — |
| MD-17 | the CARV Commit rule accepting any stamp | detected (4) | — |
| MD-18 | fence minimum semantics | detected (2) | **detected** (D-A1, D-A4, D-M4b) |
| MD-19 | fence: recorded higher floor | detected (1) | — |
| MD-20 | fence: exact definition | detected (5) | — |
| MD-21 | fence: single floor constraint | detected (1) | — |
| MD-22 | policy accepts an unrecognized floor | detected (2) | — |
| MD-23 | command migrates every pending floor | detected (1, source-level architecture test) | **detected** (D-00b: floor 1 also installed floor 2; 30+ checks) |
| MD-24 | floor-2 SQL older-marker precondition | detected (1, SQL-text unit test) | **detected** (D-F2, then D-F3..F5, D-F12) |
| MD-25 | floor-2 SQL does not drop floor 1 | detected (1, SQL-text unit test) | **detected** (D-F6..F10, D-F13, D-M1a) |
| MD-26 | command refusal of lowering | **not detected** (needs a database) | **detected** (D-F10) |
| MD-27 | command refusal of skipping | **not detected** (needs a database) | **detected** (D-F0) |

Every removal is detected by at least one layer. MD-23..MD-25 have only narrow (source or SQL text) in-process detection; their
behavioural evidence is the PostgreSQL gate. MD-26/27 are detected only by the gate.

### 8.4 Repository checks

- The specification manifest was regenerated (`scripts/generate-spec-manifest.py`) and `scripts/validate-specifications.py --strict` passes.
- RowHmac v1, Merkle v1 and the schema-2 vector files are byte-identical; no AuthorityDb ordinary migration and no BusinessDb migration
  were added; the only new migration is the floor-2 floor migration.
- No business source file changed. Only the Authority, its tests, the private OpenAPI reference note, runbooks and this record changed.
- The gate evidence was scrubbed of local paths and contains no credential.

### 8.5 Existing tests changed (for owner review)

| Change | Reason |
|---|---|
| Readiness and connection-marker strings `v1` / `carv-v1` → `v2` / `carv-v2`; "floor above the build" cases 2 → 3; floor-2 startup/policy cases moved to 3 | CARV 2 (D-2); floor 2 recognized (D-3/D-4) |
| `TheFloorContextOwnsExactlyTheFloorMigration…` expects floors 1 and 2 | D-3 |
| Schema-2 / envelope-2 refusals: `ENVELOPE_SCHEMA_UNSUPPORTED` → `ENVELOPE_VERSION_NOT_ENABLED` (Prepare) and `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT` (Commit); fixtures made well-formed under the v2 rules | D-12 and the approved refusal codes (D-11) |
| `SchemaTwoIs…AndTheRuleVersionIsUnchanged` renamed `…AndTheV1AllowListIsUnchanged`, asserts `CurrentVersion = 2` | D-2 |
| `StoreConnectionsCarryTheBuildRuleVersion` gains a stale-marker replacement case | OA-M2d-7 |

No assertion was weakened: each changed expectation names the new refusal or version it now requires.

---

## 9. Contract corrections and owner approvals requested

| ID | Item | Requested |
|---|---|---|
| OA-M2d-1 | §4.4: Commit refuses every v2 row in M2d (carried rows validated at Prepare only), instead of committing carried rows on CARV 2 | **Approved by the owner (§11)** |
| OA-M2d-2 | M2b §17.3 M2-ENV-16 ("a carried two-item v2 batch is accepted against the current revision") is met at Prepare only, as a consequence of OA-M2d-1 | **Approved by the owner (§12)** |
| OA-M2d-3 | M2b §6.3 says an edited v2 item in a share edit is refused "(framing)"; a v2→v2 share edit keeps schema 2, and it is refused as a new v2 row (`ENVELOPE_VERSION_NOT_ENABLED`) | **Approved by the owner (§12)** |
| OA-M2d-4 | Batch consistency is applied to every v2 row of a candidate (not only New rows, package §2.4) so Commit can re-derive it; carried rows of one batch are byte-consistent, so no legitimate row is affected | **Approved by the owner (§12)** |
| OA-M2d-5 | DK-3 is ordered before the new-v2 refusal so that it is observable and testable while every new row is refused | **Approved by the owner (§12)** |
| OA-M2d-6 | The floor command now refuses lowering and skipping, and migrates to an explicit target; floor 1 is still applied exactly as before | **Approved by the owner (§12)** |
| OA-M2d-7 | The connection marker builder replaces a stale `carv-vN` token instead of appending a second marker | **Approved by the owner (§12)** |
| OA-M2d-8 | M2c record §12 line corrected (ledger/acceptance are M2e) | **Accepted by the owner (§12)** (documentation only) |

All eight are decided (§12). The implementation was not changed by any of these decisions; the approved contract corrections are listed in §12.2.

---

## 10. Remaining risks and release gates

| ID | Risk | Status |
|---|---|---|
| R-M2d-1 | Carried-v2 commits are impossible in M2d (OA-M2d-1); a test-seeded v2 head is owner-uneditable until M2e/M4 | By design; no product path produces v2 |
| R-M2d-2 | The downgrade guard makes any record whose head holds v2 owner-uneditable until M4 writers exist (OD-M2-06 consequence) | Accepted design; only test fixtures |
| R-M2d-3 | The carried/new decision is Prepare-only: a forged Prepared operation stamped 2 by a privileged AuthorityDb writer bypasses it, but Commit still refuses every v2 row | Out of the Level 2 model (the Authority store is trusted); M2e DK-6 adds a Commit-side check |
| R-M2d-4 | Seed reuse under a fresh data-key id and id reuse from an older, non-current revision are not detected (TH-M2-02; DK-4 is the M2e ledger) | Open; M2e |
| R-M2d-5 | The floor-2 session precondition trusts a self-declared `carv-v` marker (R-CARV-16 extended) | Availability guard only; the database floor refuses older builds anyway |
| R-M2d-6 | Floor-2 definitions are PostgreSQL 18.0 renderings (R-CARV-23 extended); Azure PostgreSQL unverified (R-CARV-18) | Open |
| R-M2d-7 | Raising `RequiredFloor` to 2 after the floor raise is a protected-configuration change (PB-11, R-CARV-22); while it stays 1, a restore to floor 1 admits version-1 writers again (R-CARV-21) | Open, operational |
| R-M2d-8 | `RowCanonicalColumn.AsBytes()` reads a NULL bytes column as empty bytes (shared integrity code); the v2 rules test NULL explicitly, the v1 rules rely on length checks (fail closed) | Recorded; shared code unchanged |

Carried forward unchanged (none closed): R-M2c-2, R-M2c-6, R-M2c-11; R-S4-1/-3/-4; D-6 (R-M2b2-1); R-M2b2-2..10; R-CARV-11, -12, -18,
-21..-27; R-M1-1..8; R-M2a-1..6; V2-IND-01; PB-03 Azure staging evidence; PB-11/PB-12 governance; RB-CARV-1 production configuration
controls.

**Explicit M2e dependencies:** v2 acceptance (switch, Production refusal, floor-3 precondition), the data-key ledger DK-4..DK-8, carried-v2
commits with DK-6, the archive body `new_data_key_identifiers`, CARV 3 and floor 3, environment-specific wrap codes (D-9). M2f: capability
header and floor. M2g: census tables. M3: key catalog. M4: writers, `MinimumWriteEnvelopeVersion`, share framing relaxation.

**Not claimed.** No Envelope v2 acceptance, no v2 writer, no floor 2 in any persistent environment, no Azure or staging evidence, no
production readiness, no independent review.

---

## 11. Owner decision closure (2026-11-15)

The owner conditionally accepted the E3-M2d Development/Testing implementation. No commit was authorized at this point (see §12 for the
final acceptance).

### 11.1 OA-M2d-1 — approved

During E3-M2d, Commit refuses **every** Envelope v2 row, carried rows included (`EnvelopeV2Rules.CommitViolation`, abort
`ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`). The implementation is unchanged by this approval. The distinctions the owner requires:

| Control | Nature | Scope |
|---|---|---|
| S-4 (`EnvelopeSchemaTie`) | **Permanent** schema/envelope consistency: only (schema 1, envelope 1) and (schema 2, envelope 2) | Prepare, Commit, census; every later increment |
| M2d Commit refusal of v2 rows | **Temporary** prohibition of every Envelope v2 row at Commit | E3-M2d only; it is lifted only by an explicitly approved, ledger-backed E3-M2e policy |
| CARV 2 | The rule version Prepare applied (P = 2) and Commit requires (C = P) | **Does not authorize** bypassing the Commit prohibition: a version-2 stamp, genuine or forged, never makes a v2 row committable in M2d |
| Prepare | May classify and validate carried v2 rows (byte-identical to the current accepted head); **may not accept new v2 rows** (`ENVELOPE_VERSION_NOT_ENABLED`) | E3-M2d |
| E3-M2e | Any future v2 Commit acceptance must be introduced through an explicitly approved, ledger-backed policy (DK-4..DK-8, the acceptance switch, CARV 3, floor 3) | Not implemented, not authorized |

All existing fail-closed behaviour is preserved.

### 11.2 OA-M2d-2 to OA-M2d-7 — analysis (since approved, §12; no change made)

| ID | Issue | Current behaviour | Contract | Alternatives | Consequences | Recommendation | Affects |
|---|---|---|---|---|---|---|---|
| OA-M2d-2 | M2-ENV-16 cannot be met end to end in M2d | A carried two-item v2 batch passes Prepare (`AnOwnerEditCarryingATwoItemV2BatchAndAV2DocumentPassesPrepareAndIsNotCommitted`, the share-edit equivalent, `ByteIdenticalCarriesOfTheCurrentHeadPassAndRemovalsArePermitted`) and Commit refuses it | M2b §17.3 M2-ENV-16: "a carried two-item v2 batch is accepted against the current revision" | (a) read it as "passes the against-current Prepare rules", re-run end to end in M2e; (b) mark M2-ENV-16 unmet and move it to M2e; (c) commit carried rows (excluded by OA-M2d-1) | (a)/(b): none for the implementation; only the test register changes. (c) would reverse OA-M2d-1 | (a), with an explicit M2e obligation to re-run M2-ENV-16 through Commit once the ledger policy exists | Documentation (test-register reading) |
| OA-M2d-3 | The contract gives the wrong refusal for a v2 item in a share edit | A share edit introducing a v2 row (v2→v2 re-seal or v1→v2) is refused at Prepare as a new v2 row (`ENVELOPE_VERSION_NOT_ENABLED`) before the share classifier; a v2→v1 share edit is `ENVELOPE_DOWNGRADE` | M2b §6.3: "an edited v2 item is refused (framing) until M4" | (a) correct the documentation; (b) move the v2 rules after the share classifier so `SHARE_ITEM_FRAMING_CHANGED` appears for v1→v2 | (a): none. (b): an implementation change with no security gain; v2→v2 would still not be refused by framing (schema unchanged) | (a); keep the M4 obligation to relax `SHARE_ITEM_FRAMING_CHANGED` for v1→v2 | Documentation |
| OA-M2d-4 | Batch consistency applies to every v2 row, not only new ones | `EnvelopeV2Rules.Enforce` groups all v2 rows of a candidate (Prepare and Commit) | Package §2.4: "over all **new** v2 rows of a candidate … carried rows are not re-grouped" | (a) keep all-row grouping (current); (b) group only new rows, at Prepare only (the new/carried decision needs current bytes) | (a): re-derivable at Commit; strictly stricter; a head holding an inconsistent batch (impossible through any accepted path) would make carrying it fail closed until the items are removed. (b): the Commit-side re-check of batch consistency is lost (Prepare-only, CARV-covered) | (a) | Implementation if (b) is chosen (rules, tests) |
| OA-M2d-5 | Order of DK-3 against the new-v2 refusal | DK-3 runs first (`DATA_KEY_IDENTIFIER_REUSED`), then every remaining new v2 row is refused (`ENVELOPE_VERSION_NOT_ENABLED`) | DK-3 is a Prepare rule (package §4.4; M2b §5.1); no order is specified against an M2d-only refusal | (a) DK-3 first (current); (b) the new-v2 refusal first | Both refuse the same candidates; only the reason code differs, and it is identifier-free. (a) keeps DK-3 reachable and mutation-tested (MD-05 detected by 7 tests); with (b) DK-3 would be unreachable code until M2e | (a) | Implementation if (b) is chosen (order, tests) |
| OA-M2d-6 | The floor command was tightened | Requires an explicit recognized floor ≤ the build; refuses `--migrate`, a recorded higher floor (no lowering) and an unrecorded lower floor (no skipping); migrates the floor context to exactly the requested migration | M2b §19.5/§19.8 (floors monotonic, raised add-then-drop), §21 (floor-1 command), OD-M2-21 (never lowered) | (a) keep (current); (b) `Database.Migrate()` (all pending floors); (c) allow skipping by applying lower floors in sequence | (a): floor-1 behaviour unchanged except a refusal that cannot arise before floor 2 exists. (b): `--apply-content-acceptance-floor 1` would also install floor 2, a premature activation (shown by MD-23: D-00b fails). (c): bypasses the per-floor preconditions as an operator step | (a) | Implementation (accepted M2b-1F/RB-CARV-1 command behaviour) |
| OA-M2d-7 | The CARV connection marker handling changed | `ContentAcceptanceRuleConnection` replaces any configured `carv-vN` token with this build's marker; otherwise appends it | M2b §19.9 step 3: connections carry the build's rule version (informational, never enforcement); the floor preconditions read it | (a) replace (current); (b) append (previous); (c) remove every marker, then append | (a)/(c): the marker always names the running build. (b): a stale configured `carv-v1` beside `carv-v2` misreports the build and blocks floor-2 activation (the precondition reads the first marker), an availability fault only. The marker is self-declared in every option (R-CARV-16) | (a) | Implementation (one method, one test case) |

### 11.3 Forged Prepared-operation check (Commit control flow)

An operation recorded outside this build's Prepare with a forged Prepare stamp of 2 and staged Envelope v2 content (the gate's controlled
test-only mechanism) passes CARV (P = V = 2) but is refused in `ContentService.CommitCoreAsync` before any acceptance:

| Order | Step | Effect on the forged operation |
|---|---|---|
| 1 | `RequireContentAcceptanceFloorAsync`, organization gate | pass (Release B fence verified in the transaction) |
| 2 | committed replay (returns the existing head, writes nothing); state, expiry | not Committed (the state guard refuses inserting a non-Prepared row), so continues |
| 3 | CARV `P = V` | passes with a forged 2 (with 1, NULL or 3: aborted `OPERATION_RULES_OUTDATED`) |
| 4 | quarantine, head CAS, commit-time authorization | unchanged checks |
| 5 | `VerifyStagedAgainstLeavesAsync` | verifies (never signs) the staged digests, HMACs and root against the candidate leaves |
| 6 | S-9, S-4, `EnvelopeRules.Enforce` (v1 rules + v2 shape and batch) | a malformed v2 row is aborted here (`ENVELOPE_INVALID_AT_COMMIT`) |
| 7 | **`EnvelopeV2Rules.CommitViolation`** (`ContentService.cs`, the line before `BindAtCommitAsync`) | **every v2 row: aborted `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`, `422`** |
| 8+ | item-identity binding, manifest commit and leaves, item identities, head advance, `Committed`, search index and content-integrity revision, content journal entry and checkpoint, final commit | **never reached** |

- **Commit never signs.** There is no MAC signing call in `CommitCoreAsync`; only Prepare signs.
- **Nothing is staged before step 7.** Staged verification calls only `VerifyAsync`, and no step before step 7 adds or changes a tracked
  entity other than the operation. The abort therefore persists exactly one change: the operation's `Prepared → Aborted` state and
  reason. This is the protective transition that RB-CARV-1 exempts.
- **No durable authorization or acceptance effect.** There is no head, manifest, identity, journal, checkpoint, revision or index change,
  and no signature is issued.

**Evidence:**
- in-process: `AtCommitATiedSchemaTwoRowIsStillRefused` (field, document), `AtCommitTheV2RulesAreReappliedToTheStagedBytesBeforeTheNotEnabledRefusal`,
  the carried-commit tests (`CommitRefusedAsNotEnabledAsync`: P = 2 / C = NULL, head and commits unchanged) and
  `CommitRefusesEveryV2RowAndNothingElse`;
- architecture: `CommitRefusesEveryV2RowOnceAfterTheStagedRulesAndBeforeTheHeadAdvances` (order before `BindAtCommitAsync` and
  `AddManifestCommit`);
- real PostgreSQL: gate D-V3 (a seeded P = 2 operation holding a well-formed v2 row; 4 signatures verified, then refused and aborted;
  record snapshot (head, root, commits, leaves, identities, committed operations) and organization snapshot (content-integrity and
  authorization revisions, checkpoints, journal) unchanged), × 2 runs of record;
- removals: MD-07 and MD-08 detected.

**No gap found.**

Residual (unchanged, R-M2d-3): a forger able to write AuthorityDb and sign with the organization's integrity key is outside the Level 2
model. Even so, M2d's Commit refuses every v2 row, so such a forgery cannot introduce v2 content. Like the other Commit-time aborts, this
abort is recorded on the operation and is not journaled (the R-CARV-12 / R-M2b2-7 question).

---

## 12. Final owner acceptance (2026-11-15)

The owner accepts E3-M2d for **Development/Testing** and authorizes exactly one signed local commit. No push, merge, deployment or later
milestone (E3-M2e or after) is authorized. The implementation is preserved exactly as reviewed; none of the decisions below changed code.

### 12.1 Decisions

| ID | Owner decision |
|---|---|
| OA-M2d-1 | **Approved.** Commit unconditionally refuses every Envelope v2 row during E3-M2d, carried rows included (`ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`). The permanent S-4 schema/envelope consistency enforcement (`EnvelopeSchemaTie`) is preserved separately and is not part of this temporary refusal (§11.1). |
| OA-M2d-2 | **Approved.** M2-ENV-16 is accepted at Prepare level during E3-M2d (a carried two-item v2 batch passes the against-current Prepare rules). Full end-to-end Commit acceptance of M2-ENV-16 belongs to E3-M2e and must be re-run through Commit once the ledger-backed policy exists. |
| OA-M2d-3 | **Approved.** The share-edit refusal description is corrected: a share edit introducing a v2 row is refused `ENVELOPE_VERSION_NOT_ENABLED`, and one replacing a v2 item with a v1 envelope is refused `ENVELOPE_DOWNGRADE`, as applicable. Relaxing `SHARE_ITEM_FRAMING_CHANGED` remains E3-M4 work. |
| OA-M2d-4 | **Approved.** Batch consistency is validated across **all** v2 rows of a candidate, carried rows included, at Prepare and Commit. |
| OA-M2d-5 | **Approved.** DK-3 is evaluated before the blanket refusal of new v2 rows. |
| OA-M2d-6 | **Approved.** The floor command applies exactly the explicitly requested next floor; it refuses lowering, skipping and a combination with `--migrate`. |
| OA-M2d-7 | **Approved.** The connection marker builder replaces a stale `carv-vN` marker with the current build's marker. |
| OA-M2d-8 | **Accepted.** The completed E3-M2c documentation correction (ledger and acceptance switch are E3-M2e). |

### 12.2 Contract corrections made (documentation only)

Each correction is an inline, dated note naming its OA decision; no other contractual requirement was changed.

| Clarification | Where |
|---|---|
| M2-ENV-16 passes Prepare in E3-M2d; Commit only in a later authorized milestone (OA-M2d-2) | `E3_M2B_IMPLEMENTATION_CONTRACT.md` §17.3 |
| Share-edit refusal codes are `ENVELOPE_VERSION_NOT_ENABLED` / `ENVELOPE_DOWNGRADE`; framing relaxation is M4 (OA-M2d-3) | `E3_M2B_IMPLEMENTATION_CONTRACT.md` §6.3 |
| Batch consistency covers carried v2 rows (OA-M2d-4) | `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` §2.4; `E3_M2B_IMPLEMENTATION_CONTRACT.md` §5.1 and §7.3 |
| DK-3 runs before the blanket new-v2 refusal (OA-M2d-5) | `E3_M2B_IMPLEMENTATION_CONTRACT.md` §5.1 |
| The floor command applies only the explicitly requested next floor (OA-M2d-6) | `E3_M2B_IMPLEMENTATION_CONTRACT.md` §21.1; already stated in `E3_M2B1_CARV_OPERATIONS.md` §8 |
| The connection marker reports the current build's CARV version (OA-M2d-7) | `E3_M2B_IMPLEMENTATION_CONTRACT.md` §20.2 |

Conflict check: none of these corrections contradicts another binding contract. The M2b contract already supersedes the package where they
differ (M2b status table); OA-M2d-4 is stricter than package §2.4 and was approved as such; OA-M2d-6 is consistent with OD-M2-21 (floors are
never lowered) and M2b §19.8 (floors raised one step, add-then-drop); OA-M2d-7 keeps the marker informational (R-CARV-16).

### 12.3 Release state

Everything listed in §10 stays open: R-M2d-1..8 and every carried-forward risk; V2-IND-01; PB-03 Azure staging evidence; PB-11/PB-12
governance; RB-CARV-1 production configuration controls; the E3-M2e activation prerequisites. Floor 2 remains installed only in disposable
test PostgreSQL; no persistent environment, Release B configuration or Azure service was changed; Stage B was not run.
