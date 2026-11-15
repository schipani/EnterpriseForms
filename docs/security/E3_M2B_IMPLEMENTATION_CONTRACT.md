# E3-M2b Implementation Contract — Design and Implementation Readiness

**Status (2026-11-14):**

| Item | State |
|---|---|
| This document | §0–§19: design for owner review (they changed no code). §20: the record of the authorized E3-M2b-1 implementation. |
| E3-M1 | Accepted for Development/Testing; local signed commit `de1729be569aa564d8a43a6c422f88a731ca48d1` |
| E3-M2a | Accepted for Development/Testing; local signed commit `66f0aa7b257d984555533ceb10dfccfa921aa273` (branch `e3/m1-envelope-v2-core`, clean tree at the start of this task) |
| E3-M2b-1 (CARV) | **Implemented and owner-accepted for Development/Testing (2026-11-14, §20.9), signed local commit `c44be41`: §20** |
| E3-M2b-1F (floor) | **Implemented and owner-accepted for Development/Testing (2026-11-15), signed local commit; Release B not activated; mandatory gate RB-CARV-1 open: §21** |
| RB-CARV-1 (Release B write fence) | **Implemented and conditionally owner-accepted for Development/Testing (2026-11-15), signed local commit; not production-closed: deployment protection, Azure compatibility, privileged schema mutation and performance remain open release gates: §22** |
| E3-M2b-2 and later | **Not authorized** |
| Independent cryptographic review (V2-IND-01) | Required before production; not started |
| Production use of Envelope v2 | Not approved |
| **E3-M2b-1 CARV final design gate** | **§19** (2026-11-14). The owner conditionally approved the CARV direction. The real-PostgreSQL gate ran against two real pre-CARV Authority binaries (47/47 checks each; evidence in `docs/evidence/e3-m2b1-carv/`). It found and corrected design defects D-1 to D-5. **§19 supersedes §3, §9.1, §9.2, §16 and §17.1.** |
| **E3-M2b-1 implementation record** | **§20** (2026-11-14): migration `20261115014221_AuthorityContentAcceptanceRuleVersion`, application rules, store mapping, readiness guard, business mapping, real-PostgreSQL gate against both legacy builds and enforcement-removal checks. Evidence: `docs/evidence/e3-m2b1-carv/implementation/`. |

**Review:** owner-led design review. Not an independent security review and not a certification.

**Relationship to the E3-M2 package.** `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` (committed in `66f0aa7`) stays the baseline. This document **supersedes it where they differ**: findings F-13 to F-20, the deployment-window fence (§3), increment boundaries (§15), and the decision register (§14). Sections of the package not restated here are unchanged.

**Notation:** [EXISTING] = verified in source at `66f0aa7`; [DECIDED] = previously approved by the owner; [PROPOSED] = needs owner approval; [CORRECTION] = a statement in the M2 package that the code does not support.

**Identifiers introduced:** findings F-13 to F-20; mechanism CARV (content acceptance rule version); increments E3-M2b-1, E3-M2b-1F, E3-M2b-2; decisions OD-M2-17 to OD-M2-26; tests M2-CARV, M2-CARV-PG, plus extensions of the package's groups.

---

## 0. Summary

1. **The E3-M2a deployment-window limitation is larger than the package stated** (F-15). It is not bounded by the ten-minute operation lifetime. Any Authority instance still running the older build keeps preparing new operations for as long as it runs, and a newer instance commits them. Commit cannot re-run the cross-revision rule, because it holds only digests of the current revision.
2. **Recommended remedy: the content acceptance rule version (CARV)** (§3). Each content operation is stamped with the rule version its Prepare applied. Commit refuses a stamp below its minimum. A database CHECK stops an older binary from committing an operation prepared under newer rules. A later floor migration stops older binaries from preparing at all. The design is fail closed in both directions and does not depend on time. It also fences every future Prepare-only rule (v2 shape, the data-key ledger), not just the M2a one.
3. **E3-M2a's v1 reuse rules cannot accept legitimate v2 batches** (F-14). A v2 field batch shares one wrapped key across items by design. M2a refuses that both within a candidate and against the current revision, and the census counts it as an anomaly. The rules must be split by envelope version in M2d. This is not a defect today, because v2 is refused.
4. **Schema selection is done by the stager, not the clone helpers** (F-16). `RecordCandidateStager` re-derives the signed schema from the row's *type* on every non-share write, and document processing hard-codes schema 1. S-1 (schema chosen by envelope version) must be implemented in `CurrentSchemaVersion`. A database CHECK tying `row_signature_schema_version` to `encryption_envelope_version` is a cheap, exact backstop.
5. **The Authority pins column names, not types or nullability** (F-13). Schema 2 should be registered with types and nullability at the Authority (S-9).
6. **Recommended smallest safe increment: E3-M2b-1, CARV only** (§16). It touches the Authority and AuthorityDb (two nullable columns, one CHECK, one immutability trigger, a startup guard), with a small business retry mapping. It introduces no v2, closes R-M2a-3, is a tightening only, and the previous Authority binary can still be redeployed until the separate floor migration (E3-M2b-1F).

---

## 1. Additional findings (verified at `66f0aa7`)

| ID | Finding | Evidence | Consequence |
|---|---|---|---|
| **F-13** | `ContentDeltaRules.RequireRule` checks that the column **names** match the registered set. It checks neither **types** nor **nullability**. The decoder accepts any defined type tag and NULL in any position. Today only the accessor-based checks catch a wrong type or NULL: `EnvelopeRules` (`ColumnMissing`), identity binding (`AsUuid`) and share classification. | `ContentDeltaRules.cs:385-395`; `RowCanonicalDecoder.cs:95-135` | Register schema 2 at the Authority with (name, type, nullable) and enforce it in `RequireRule` (S-9, §4.4). Whether to extend this to every kind is OD-M2-22. |
| **F-14** | `EnvelopeRules.Enforce` refuses any wrapped key shared by two field rows of a candidate. `EnforceAgainstCurrent` indexes the current revision by wrapped key with `TryAdd` (first row only) and requires a same-item byte-identical carry. `EnvelopeLooksApproved` (census) counts a repeated wrapped key as an issue. In v2, every item of a field batch shares one wrapped key. | `EnvelopeRules.cs:96-160`; `ContentService.cs:916-917` | A v2 batch of two or more items would be refused at Prepare, at Commit and by the census. M2d must make all three version-dispatched (§5). v1 behaviour stays exactly as accepted. |
| **F-15** [CORRECTION of package §13.4 R-M2a-3] | Commit re-runs `EnvelopeRules.Enforce` on the staged bytes (`ContentService.cs:377-388`). It cannot run `EnforceAgainstCurrent`: `CommitContentChangeRequest` carries no current items, and the Authority keeps only digests of the current revision. `content_change_operations` records nothing about the rules its Prepare applied. Expiry is a time bound on **one operation**. It does not limit how long an older instance keeps **creating** operations during a rolling or blue/green deployment. | `ContentService.cs:66-233, 265-486`; `ContentChangeOperation.cs` | "Bounded by the ten-minute lifetime" is incorrect. The exposure lasts as long as any older instance runs. It also applies to every other Prepare-only decision: identity rules I-1/I-2, share and owner classification stored as `MutationKindName`, the reprotection classification. Commit trusts these from the candidate leaves. §3 fixes the general case. |
| **F-16** [CORRECTION of package F-06 emphasis] | `RecordCandidateStager` sets `RowSignatureSchemaVersion = CurrentSchemaVersion(row)`, which is **type-based**, on every owner, lifecycle, upload, document, restore and reprotection write (`:58`, `:127`). Only share writes preserve the stored version (`preserveSchemaVersions`, `UpdateFormRecordHandler.cs:95`). `DocumentProcessingService` hard-codes `RowSignatureSchemaVersion = 1` for the processed copy (`:318`). `Encode` uses the type's v1 schema for both envelope tables. | cited lines | With v2 rows present, a type-based assignment would encode a v2 envelope under schema 1. That silently drops the three v2 columns; the Authority S-4 tie refuses it. The fix belongs in `CurrentSchemaVersion`, by deriving the schema from `EncryptionEnvelopeVersion` (S-1). The clone fix (S-3) is still needed for the three columns themselves. |
| **F-17** | Prepare's idempotent replay returns the existing operation **in any state** (`ContentService.cs:82-92`). `CreateFormRecordHandler` step 2 re-prepares under the same key, and catches only `IDEMPOTENCY_CONFLICT` and `CONTENT_REVISION_CONFLICT`. A replayed, aborted operation therefore surfaces at Commit as an uncaught `OPERATION_STATE_INVALID`. | `CreateFormRecordHandler.cs:325-372` | This is existing behaviour (for example, after expiry), not introduced by M2. CARV adds a new source of aborted operations, so the replay rule and the business mapping are specified in §3.6. |
| **F-18** | The Authority's existing "constraint" tests inspect the EF model's CHECK text against an `unit-test.invalid` connection. No Authority test runs against real PostgreSQL. | `SessionRevocationConstraintTests.cs:16-31`; `RuntimeGrantsTests.cs` | Database fences (CHECKs, triggers, unique races, the behaviour of an older EF model) need the throwaway-PostgreSQL script. OD-M2-12 moves from M2e to M2b-1. |
| **F-19** | `row-integrity-format.md` §7 "Registered schemas" lists only `form_records` 1/2, `field_string_values` 1 and the test-only kind. The other registered kinds, including `field_encrypted_values` 1 and `file_attachment_versions` 1, are not listed. | `docs/dynamic-forms/row-integrity-format.md:157-176` | M2b-2 updates §7 with all registered kinds and the two schema-2 entries (documentation only; no vector change). |
| **F-20** | Restore re-seals restored values through `CandidateBuilder.ApplyAsync`, and carries documents from the **current** head only ("documents are never restored"). Both runtime roles already have `SELECT` on their migrations-history table. | `RecordRestoreHandler.cs:126-139`; `BusinessRuntimeGrants.cs:81`; `AuthorityRuntimeGrants.cs:84` | Supports OD-M2-05 (carry from the current head only). Startup migration guards need no new grants. |

---

## 2. Scope of E3-M2b (this contract)

The brief's ten topics span what the package split into M2b–M2g. This contract therefore:
- **specifies** all ten (§3–§13), so that every increment has one exact contract;
- **re-cuts** the first increments (§15): CARV first (M2b-1), then its floor (M2b-1F), then the business schema-2 groundwork (M2b-2, the package's "M2b");
- keeps M2c–M2g as in the package, with the deltas listed in §15.

Nothing here writes Envelope v2 from a product path. The writers are E3-M4.

---

## 3. Deployment-window fence: content acceptance rule version (CARV) [PROPOSED]

> **Superseded by §19.** The mechanism is unchanged in principle. The commit rule, the floor expression and scope, the trigger and the VALIDATE plan were corrected after real-PostgreSQL testing (§19.2, defects D-1 to D-5). Read §19 for the final contract.

### 3.1 The problem, precisely

| Scenario | Today (M2a) |
|---|---|
| Old Prepare → new Commit | Commit re-checks the shape and within-candidate rules only. Cross-revision reuse, and any other rule added later at Prepare, is never applied. **Accepted.** |
| Old Prepare → old Commit, while new instances exist | The old rules end to end. Accepted for as long as the old instance runs. |
| New Prepare → old Commit | The old Commit has no M2a Commit rule at all. Accepted. Harmless for M2a's rules, which Prepare applied; **not** harmless for a future Commit-only rule such as DK-6/DK-8 (ledger confirmation and journal ids). |

The operation lifetime cannot fix any of these: the first two arise for every operation created during the overlap.

### 3.2 Options considered

| Option | Assessment |
|---|---|
| Shorter lifetime | Narrows one operation, not the overlap. **Rejected** (and rejected by the brief). |
| Abort every Prepared operation in the migration | Older instances still running keep preparing new ones. Insufficient alone. |
| Commit re-validates: the business resends the current items at Commit and the Authority re-proves and re-checks them | Strong for the envelope rule. But it changes the API, costs a second snapshot proof on every commit, needs a business change, and does not cover Prepare-only decisions such as classification or (later) ledger reservation. Kept as optional defence in depth (OD-M2-24). |
| Process start-time heuristic | Not fail closed in a mixed fleet. **Rejected.** |
| **CARV** (stamp + DB CHECK + migration floor) | Fail closed in both directions, independent of time, covers every Prepare-time rule, and is declarative in the database. **Recommended.** |

### 3.3 Mechanism

**Constants (Authority, `ContentAcceptanceRules`):**
- `CurrentVersion`: incremented whenever Prepare starts enforcing a rule that Commit does not fully re-derive. M2b-1 = **1** (the M2a rules, including cross-revision). M2d = 2 (version-dispatched v1/v2 rules). M2e = 3 (ledger DK-1..DK-5). Each bump is a reviewed code change.
- `MinimumCommittablePrepareVersion`: normally equal to `CurrentVersion`.

**Columns on `authority.content_change_operations`** (nullable, no default):
- `prepare_acceptance_rule_version integer` — written once, at Prepare, by a CARV build.
- `commit_acceptance_rule_version integer` — written at the Committed transition by a CARV build.

An older binary's EF model does not map these columns. EF Core inserts only mapped columns and updates only modified mapped properties, so an older binary leaves both NULL. This assumption is load-bearing and is **proven** by M2-CARV-PG-06 with a model that omits the properties.

**Rules (application):**

| Point | Rule |
|---|---|
| Prepare (new operation) | Stamp `prepare_acceptance_rule_version = CurrentVersion`. |
| Prepare (idempotent replay of an existing operation) | If the operation is `Prepared` and its stamp is NULL or below `MinimumCommittablePrepareVersion`: `409 OPERATION_RULES_OUTDATED`, nothing written. Other replays are unchanged. |
| Commit, operation already `Committed` | Unchanged idempotent replay, **evaluated first**. Historical committed operations have NULL stamps and must still replay. |
| Commit, operation `Prepared` | Immediately after the state and expiry checks, before any verification: if the stamp is NULL or below `MinimumCommittablePrepareVersion`, then abort with `operation_quarantine_reason_code = 'OPERATION_RULES_OUTDATED'`, commit the abort, and return `409 OPERATION_RULES_OUTDATED`. |
| Commit, success | Set `commit_acceptance_rule_version = CurrentVersion` together with `Committed`. |
| Defence in depth | The M2a Commit shape rule (`ENVELOPE_INVALID_AT_COMMIT`) stays. It runs only for operations that pass CARV. |

**Database (fence against older binaries; see §9.1 for the exact SQL):**
- `ck_content_operation_commit_rule_version`: a `Committed` row needs `COALESCE(commit, 0) >= COALESCE(prepare, 0)`; a non-Committed row needs a NULL commit stamp. Valid for all existing rows (NULL/NULL).
- `trg_content_operation_rule_version_immutable`: the prepare stamp cannot change after insert.
- **Floor** (`ck_content_operation_prepare_rule_floor`, a separate migration, M2b-1F): a `Prepared` row needs `prepare_acceptance_rule_version >= N`. It is added `NOT VALID`, so existing NULL Prepared rows need no rewrite: they can still move to `Aborted`, but can never be committed by a CARV build. `VALIDATE CONSTRAINT` runs after they expire. Raising the floor to a later version replaces the constraint.

### 3.4 Behaviour matrix

"Old" = an Authority build without CARV (M2a). "New" = M2b-1 or later.

| Prepared by → committed by | Before the floor (M2b-1) | After the floor (M2b-1F) |
|---|---|---|
| Old → old | Allowed (equivalent to today; NULL/NULL passes the CHECK) | **Impossible**: the old Prepare insert fails the floor CHECK |
| Old → new | **Refused**: `OPERATION_RULES_OUTDATED`, aborted, head unchanged (**closes R-M2a-3**) | Cannot arise |
| New → old | **Refused** by the CHECK (commit 0 < prepare 1). The old binary gets a database error; nothing is accepted | Refused the same way |
| New → new | Allowed | Allowed |
| Newer rules → older CARV build (for example, prepared at 3, committed by 2) | Refused by the CHECK (2 < 3) | Refused |
| Older CARV build → newer (prepared at 2, committed by 3) | Refused by the application (2 < minimum 3) | Refused, and impossible once the floor is 3 |

**Result:** once the floor is raised, no operation is accepted unless both its Prepare and its Commit ran rules at least as new as the floor. Before the floor, the only remaining exposure is old-to-old operations on instances that have not yet been stopped. That is exactly the pre-upgrade behaviour; nothing is weakened.

### 3.5 Rollback

| Action | Effect |
|---|---|
| Redeploy the M2a binary **before** the floor | Works. Its own operations commit. Operations prepared by M2b-1 instances fail at its Commit with a database error (refused, not accepted); the business re-stages. |
| Redeploy the M2a binary **after** the floor | Every Prepare fails (fail closed). Lowering the floor is a loosening: Down migration, Development/Testing only (§12). |
| Down migration of M2b-1 | Guarded: it refuses while any `Prepared` row has a non-NULL stamp. Development/Testing only. |

### 3.6 Business handling (part of M2b-1)

- `CreateFormRecordHandler`: treat `OPERATION_RULES_OUTDATED` like `OPERATION_EXPIRED` in step 1, and like `IDEMPOTENCY_CONFLICT` in step 2, so the existing step 3 (fresh retry key) follows.
- `FieldProtectionMigrationService`: add it to the existing retryable set (`:260`).
- Other paths surface it as a retryable conflict to the caller.

This is an error-code mapping only. Business persistence and signed data are unchanged.

### 3.7 Governance and residuals

- **Raising the floor** is a migration, applied by the Authority migration identity under the PB-11 Authority release approval. It is a tightening (one approver; dual control in staging and production per PB-12).
- **Lowering it** is only possible through a Down migration in Development/Testing.
- The runtime role owns no table and cannot alter constraints (G08).
- **Residual:** a compromised Authority runtime can stamp anything. That is out of the Level 2 model, which trusts the Authority. CARV protects against **honest older builds** and mixed fleets.

---

## 4. Signed schema 2 canonical format [PROPOSED; refines package §3]

### 4.1 Framing

`DynamicForms/RowHmac/v1` framing is unchanged (`row-integrity-format.md` §2). Only the header `schema_version` (UInt16BE, `0x0002`) and `column_count` differ. RowHmac v1, Merkle v1, their domains and every existing vector file are unchanged (OD-M2-14).

### 4.2 `field_encrypted_values` schema 2 — 17 columns, ordinal order

| # | Column | Type | Nullable | Authority class |
|---|---|---|---|---|
| 1 | `content_change_operation_id` | Uuid | no | OperationBinding |
| 2 | `data_key_commitment_bytes` | Bytes | **no** | Content |
| 3 | `data_key_identifier` | Uuid | **no** | Content |
| 4 | `encrypted_logical_type` | Text | no | Identity |
| 5 | `encrypted_value_bytes` | Bytes | no | Content |
| 6 | `encryption_algorithm_name` | Text | no | Content |
| 7 | `encryption_auth_tag_bytes` | Bytes | no | Content |
| 8 | `encryption_envelope_version` | Int32 | no | Content |
| 9 | `encryption_key_reference` | Text | no | Content |
| 10 | `encryption_nonce_bytes` | Bytes | no | Content |
| 11 | `field_definition_id` | Uuid | no | Identity |
| 12 | `field_value_item_id` | Uuid | no | Identity |
| 13 | `form_record_group_id` | Uuid | yes | Identity |
| 14 | `form_record_id` | Uuid | no | Identity |
| 15 | `key_wrap_algorithm_code` | Int32 | **no** | Content |
| 16 | `tenant_id` | Uuid | no | Identity |
| 17 | `wrapped_data_key_bytes` | Bytes | no | Content |

### 4.3 `file_attachment_versions` schema 2 — 29 columns, ordinal order

`ciphertext_sha256`, `ciphertext_size_bytes`, `content_change_operation_id`, **`data_key_commitment_bytes`** (Bytes), **`data_key_identifier`** (Uuid), `detected_content_type`, `encryption_algorithm_name`, `encryption_envelope_version`, `encryption_key_reference`, `encryption_nonce_prefix`, `encryption_segment_size_bytes`, `field_definition_id`, `file_attachment_id`, `form_record_id`, **`key_wrap_algorithm_code`** (Int32), `original_file_name`, `plaintext_sha256`, `plaintext_size_bytes`, `scan_signature_name` (nullable), `scan_verdict`, `scanned_on`, `scanner_name`, `storage_object_key`, `storage_provider_name`, `tenant_id`, `uploaded_by_user_id`, `uploaded_on`, `version_number`, `wrapped_data_key`.

The types of the 26 schema-1 columns are unchanged. The three new columns are non-nullable and Content-class. `ShareUpdatable = false`. The ordinal order was checked mechanically (see §4.5).

### 4.4 Rules

| Rule | Detail |
|---|---|
| S-1 Schema by envelope | `BusinessSignedSchemas.CurrentSchemaVersion` returns, for `FieldEncryptedValue` and `FileAttachmentVersion`, `EncryptionEnvelopeVersion` (1 → 1, 2 → 2, anything else throws). It applies to the stager (`:58`, `:127`) and replaces the hard-coded `1` in `DocumentProcessingService` (F-16). |
| S-2 Encode from the stored version | `Encode` dispatches on `row.RowSignatureSchemaVersion` for both tables. An unknown pair throws, with no fallback. |
| S-3 Clone fidelity | `CloneValue` / `CloneAttachment` copy `RowSignatureSchemaVersion` and the three columns. An architecture test asserts that every mapped envelope property is copied. |
| S-4 Authority tie | Prepare refuses schema 1 with `encryption_envelope_version ≠ 1`, and schema 2 with `≠ 2` (`ENVELOPE_SCHEMA_MISMATCH`). |
| S-5 / S-6 / S-7 / S-8 | As in package §3.4 (sign exact bytes; verification unchanged; schema 1 permanent; the business reader refuses a mismatched pair). |
| **S-9 Types pinned** (new, F-13) | The Authority rule for both envelope tables, in both schema versions, records (name, type, nullable). `RequireRule` refuses a column whose type tag differs, or a NULL in a non-nullable column (`UNSUPPORTED_SIGNED_SCHEMA`). For schema 1 this is a pure tightening: business encoding already matches the registry. Extending it to all kinds is OD-M2-22. |
| **S-10 Business self-check** (new) | `Encode` throws when `row.RowSignatureSchemaVersion` and `EncryptionEnvelopeVersion` disagree, before anything is sent. The Authority S-4 tie remains authoritative. |
| **S-11 DB tie** (new) | `CHECK (row_signature_schema_version = encryption_envelope_version)` on both tables (§9.3). This turns any F-06/F-16 regression into an insert failure. |

### 4.5 Worked example (illustrative, not a vector file)

The example was computed with the repository's Python reference encoder (`scripts/integrity/row_integrity_v1.py`). The same run first reproduced all **7 of 7** committed RowHmac v1 positive vectors byte for byte. Fixture values: organization `11111111-1111-4111-8111-111111111111`; primary key `…ce05`; operation `…ce06`; record `…ce07`; field `…ce08`; item `…ce09`; data-key id `…ce0a`; key version 1; row revision 1; ciphertext `0001020304`; tag `aa`×16; nonce `0b`×12; wrapped key `cc`×60; commitment `dd`×32; wrap code 1; reference `localdev:{organization}:field-value:1`; logical type `String`; no group.

| Variant | Columns | Length | SHA-256 | HMAC-SHA-256 (published test key `00…1f`) |
|---|---|---|---|---|
| schema 1, envelope 1 | 14 | 719 | `a03eaa90d0e5d2755761e3390fb5d79473e9e95d5ff2e305431d62ae60c1dbb9` | `46ae5e55728e492617015fa94b0ff590bbcb48bb036cf4146f06eb641cd671a2` |
| schema 2, envelope 2 | 17 | 851 | `b21eb45e7b049c1ab139fc4ecb2b1707981aa3df300e7779cae0d7e4fe60b418` | `a08b895a2a8e9936f39a5b9cba27550bdb9b19c640a7ff72150621d9f8d1fe51` |

The schema-2 bytes differ from schema 1 only in:
- the header `schema_version` (`0001` → `0002`) and `column_count` (`0e` → `11`);
- `encryption_envelope_version` (`…01` → `…02`);
- the three inserted columns at their ordinal positions:

```text
0019 "data_key_commitment_bytes" 09 00000020 dd…dd(32)
0013 "data_key_identifier"       07 0199a2b3c4d57e6f8123456789abce0a
0017 "key_wrap_algorithm_code"   02 00000001
```

The encoder refuses a NULL in any of the three new columns ("not nullable"). The C# canonicalizer has the same rule (`NullNotPermitted`). The normative schema-2 vectors belong in a **new** file in M2b-2, generated by a new generator entry that does not rewrite `row-integrity-v1.test-vectors.json` (OD-M2-14, OD-M2-25).

---

## 5. Authority Envelope v2 validation across Prepare, Commit and census [PROPOSED; refines package §2]

### 5.1 Version dispatch (M2d)

`EnvelopeRules.Validate` dispatches on `(schema_version, encryption_envelope_version)`:
- (1, 1) uses the M2a rules, unchanged;
- (2, 2) uses the package §2.3 v2 rules;
- every other pair is refused (`ENVELOPE_SCHEMA_MISMATCH`, or `ENVELOPE_VERSION_UNSUPPORTED`).

The reuse rules split as follows (F-14):

| Rule | v1 rows (unchanged) | v2 rows |
|---|---|---|
| Within a candidate | No wrapped key shared by two field rows | Group **New** rows by `data_key_identifier`. Package §2.4 batch consistency: same kind, byte-identical commitment, wrap code, wrapped key and reference; distinct items; distinct nonces; one document per id. |
| Cross-version within a candidate | — | A v2 wrapped key must not equal any v1 wrapped key, and must not appear in two v2 groups (`ENVELOPE_WRAPPED_KEY_REUSED`) |
| Against the current revision | `EnforceAgainstCurrent` as accepted | Classify per **item key** (package §4.3): Carried = the same item, current row schema 2, every Identity- and Content-class column byte-identical. Otherwise New, and DK-3 applies (no New id equals a current or carried id). The v1 map is built only from v1 rows. |
| Census (`EnvelopeLooksApproved`) | Shape + per-revision unique wrapped key | Shape + per-revision batch consistency; no wrapped key shared between v1 and v2, or between two ids |

### 5.2 Where each check runs

| Check | Prepare | Commit | Census |
|---|---|---|---|
| Shape (§2.3 of the package, v1 and v2) | ✓ | ✓ (staged bytes) | ✓ |
| S-4 tie, S-9 types | ✓ | ✓ | ✓ |
| Within-candidate / batch consistency | ✓ | ✓ | per revision |
| Against the current revision (v1 reuse, v2 Carried/New, downgrade §6.2) | ✓ | **covered by CARV** (§3): no current bytes at Commit; the head CAS proves the same head | — |
| Ledger DK-3/DK-4/DK-5 (M2e) | ✓ | DK-6 re-derive and confirm, DK-8 journal | cross-check (package §6.4) |
| Acceptance switch | New v2 refused when off | Re-checked: an operation prepared with the switch on and committed after it was switched off is refused (tightening wins) | — |

Every check that only Prepare can run is covered by CARV. Every check Commit can re-derive from staged bytes is re-run there.

---

## 6. Compatibility rules: Envelope v1 / schema 1 and Envelope v2 / schema 2 [PROPOSED]

### 6.1 Permitted pairs

| Pair | Business writer | Business reader | Authority | BusinessDb CHECK |
|---|---|---|---|---|
| schema 1 / envelope 1 | Yes (all M2 writers) | Yes | Accepted (M2a rules) | Yes |
| schema 2 / envelope 2 | No product writer until M4; fixtures only | M2c+ | Carried rows validated from M2d. New rows from M2e, only with the switch on (Development/Testing) | Yes, from M2b-2 |
| schema 1 / envelope 2 | Never (S-1, S-10) | Refused (S-8) | Refused (S-4) | Refused (S-11) |
| schema 2 / envelope 1 | Never | Refused | Refused | Refused |

### 6.2 Per-item transitions

These are the package §3.5 rules, restated, with OD-M2-06 analysed:

| From → to | Rule |
|---|---|
| none → v1, v1 → v1 | Allowed |
| none → v2, v2 → v2 re-sealed | Allowed only with fresh data-key ids (M2e) and the switch on |
| v1 → v2 | Allowed on owner and reprotection paths (M4 / M5 writers). Share edits keep `SHARE_ITEM_FRAMING_CHANGED`; relaxing that is M4. |
| v2 → v2 carried | Allowed by the exemption |
| **v2 → v1** | **Refused** (`ENVELOPE_DOWNGRADE`), if OD-M2-06 is approved |

**OD-M2-06 analysis (downgrade enforcement and sequencing).**
- **Why refuse.** v2 → v1 loses key commitment and the HKDF item binding. It also makes a record readable by older builds again, which an attacker who can trigger rewrites could use to defeat the reader floor.
- **Cost.** Before M4 there are no v2 writers, so a record holding v2 fixture rows cannot be edited by an owner until M4. Only Testing fixtures exist.
- **Recommended sequencing.**
  - The guard lands in M2d, together with the v2 rule set (CARV 2), before any v2 can be accepted.
  - The switch stays off by default.
  - M4 adds v2 writers and `MinimumWriteEnvelopeVersion`.
- **Alternative.** Allow the downgrade until M4 and enforce it afterwards. Weaker, and it needs a second cutover.
- **Not affected.** Deprotection (encrypted → plaintext) stays the separately governed ADR-PB-004 Amendment A1 path.

### 6.3 Per write path (M2b-2 through M2e; writers v1)

| Path | v1 behaviour | v2 rows present (fixtures) |
|---|---|---|
| Owner save (`CandidateBuilder`) | Re-seals every encrypted value (F-07) | Re-seal produces v1 → downgrade refused (OD-M2-06) until M4 |
| Share edit (`SharedFieldWriteBuilder`) | Copies rows, keeps the schema, re-seals the edited item | Unedited v2 items Carried; an edited v2 item is refused (framing) until M4 |
| Upload / document commit, lifecycle, protection-migration unchanged rows (`CloneValue` / `CloneAttachment`) | Byte-identical carry | Carried, provided S-3 copies the three columns |
| Restore (`RecordRestoreHandler`) | Re-seals restored values; carries documents from the current head | Restored values are New (v1 → downgrade rule applies); documents Carried |
| Record creation | New v1 | n/a (no current revision) |
| Reprotection Encrypt (B-28) | New v1 encrypted rows | Unchanged until M4/M5 |

---

## 7. Data-key id uniqueness and the OD-E3-16 ledger [PROPOSED; refines package §4]

### 7.1 OD-M2-03 analysis: bind at Prepare or at Commit

| | Bind at Prepare (recommended) | Bind at Commit |
|---|---|---|
| OD-E3-16 ("not in an earlier **accepted** revision") | Satisfied; strictly stronger (ids of aborted attempts also stay used) | Satisfied literally |
| **Business staging of a reuse** | The business stages only after a successful Prepare (`RecordCandidateStager.cs:68-87`). A reused id is refused **before** its ciphertext is ever written to BusinessDb. | A reused batch is prepared and **staged in BusinessDb** (readable by A1), then refused at Commit. A reused seed under the same id and nonce leaves two ciphertexts under one key and nonce, at rest, for an attacker. |
| Retry after an aborted or expired attempt | Must use a fresh batch (the M1 writer rule) | Could reuse the batch: weaker, and exactly R-M1-8 |
| Ids consumed by attempts that never commit | Yes (harmless: UUIDv7 space) | No |
| Recovery after an AuthorityDb restore | Reservations of never-committed operations are lost (best effort); accepted bindings are rebuilt from the archive | Same for accepted ones |
| Cost | One INSERT per New group in Prepare | One INSERT per New group in Commit |

Binding at Prepare is the only option that keeps a reused-key ciphertext pair from ever being staged. It also turns R-M1-8 from Inconclusive into enforced (for id reuse; seed reuse under a fresh id stays a writer property, TH-M2-02). **Recommendation: Prepare. This remains the owner's decision.**

### 7.2 Ledger table

As in the package §4.2, with exact constraints in §9.4. Changes: an added UUIDv7 CHECK on the id; a second index on the binding operation (for DK-6); an explicit statement that organization ↔ operation agreement is enforced by the application in the same transaction. A composite FK would need a new unique key on `content_change_operations (content_change_operation_id, organization_id)`; that is OD-M2-23.

### 7.3 Transaction protocol

**Prepare** (`PrepareCoreAsync`; organization lock and head `FOR UPDATE` already held), inserted after `EnforceAgainstCurrent` and the identity rules, before mutation classification and signing:

1. Classify every schema-2 candidate row: Carried or New (package §4.3).
2. DK-2: batch consistency over New rows (§5.1).
3. DK-3: no New id equals any id in the verified current snapshot or among Carried rows.
4. DK-4: `SELECT 1 FROM content_data_key_bindings WHERE data_key_identifier = ANY(:new_ids)` → any hit is `422 DATA_KEY_IDENTIFIER_REUSED`. Nothing is written or signed.
5. Sign (existing code).
6. Insert one binding per New group, together with the operation (`prepare_acceptance_rule_version = 3`) and the candidate leaves, in **the same transaction**.
7. Commit the transaction. A cross-organization race surfaces as SQLSTATE 23505 on `uq_content_data_key_binding_identifier`. The whole transaction rolls back (operation, leaves and bindings) and is mapped to `422 DATA_KEY_IDENTIFIER_REUSED`. No signature leaves the Authority, because the response is never sent.

Idempotent replay returns before step 1 and inserts nothing (existing early return), subject to the §3.3 CARV replay rule.

**Commit** (`CommitCoreAsync`), after `VerifyStagedAgainstLeavesAsync` and the CARV check:

1. Re-derive the New groups from the verified staged bytes. The head CAS guarantees the same current head as at Prepare.
2. DK-6: the bindings of this operation must equal the derived groups exactly: same set of ids, organization, record, kind, commitment and item count. Any difference aborts the operation (`DATA_KEY_BINDING_MISMATCH`) and writes a minimal content-stream incident.
3. DK-8: append the sorted `new_data_key_identifiers` to the `ContentHeadCommitted` body (OD-M2-15), in the commit transaction.

Bindings never change. "Accepted" is derived from the operation's `Committed` state.

### 7.4 Recovery protocol

| Event | Procedure |
|---|---|
| Crash after the Prepare commit | The bindings are reserved with a Prepared operation. The operation expires or is aborted. The writer re-stages with a fresh batch. |
| Crash after the Commit commit | Head, bindings and journal are durable together. The existing lost-acknowledgement repair applies. |
| BusinessDb restore only | The ledger is unaffected. Restored rows re-presented as New are refused (DK-4). |
| AuthorityDb restore to time T | (1) G14 gate closed. (2) Bindings of commits after T are re-inserted from the archived `ContentHeadCommitted` bodies by the recovery control plane, using the migration identity, under dual control. (3) Reservations of operations that never committed are not recoverable (stated). (4) v2 acceptance stays off until a verified ledger rebuild matches the archive: every accepted commit's ids equal the summary census (package §6.4). (5) Reopen under G14. |
| Ledger ↔ archive mismatch | Integrity incident; the organization stays gated; no automatic repair. |

The ledger is never written by an operator command or a business API, and never deleted by the runtime role.

---

## 8. Legitimate unchanged ciphertext carry-forward [PROPOSED; exact definition]

A candidate row *R* (item key *K*) is **Carried** if and only if:
- the verified current snapshot holds a row *C* with key *K*;
- *C* and *R* have the same entity kind and schema version;
- every signed column of *R* is byte-identical (framed) to *C*, except `content_change_operation_id`. The header's storage primary key and row revision differ by design.

This is the M2a `IsByteIdenticalCarry` definition, applied per item key for v2 (§5.1).

| Property | Rule |
|---|---|
| Source | **The current accepted head only** (OD-M2-05). A copy from any earlier revision is New. For v2 it is then refused by DK-4; for v1 it is undetected (R-M2a-1). |
| Partial batches | Carried items keep their old id. Newly sealed items in the same revision need a fresh id (DK-3), so a revision may hold part of an old batch plus a new batch. Removing a carried item is permitted. |
| Batch rules | Applied to New rows only. Carried rows were checked when first accepted. |
| Census | Counts carried rows by envelope version. Their ids are not new and are not re-bound. |
| Business producers | The seven clone call sites (`UploadSessionHandlers.cs:306-309`, `DocumentHandlers.cs:161-163`, `RecordLifecycleHandler.cs:91-93`, `FieldProtectionMigrationService.cs:192, 231`, `RecordRestoreHandler.cs:139`, `UpdateFormRecordHandler.cs:65`, `SharedFieldWriteBuilder.cs:121-123`) must copy all envelope columns and the schema version (S-3). They are covered by an architecture test. |
| Not carry | M5 Rewrap (same id and commitment, new wrap): refused until a separately reviewed `RewrapRules` exemption exists. |

---

## 9. Exact schema changes [PROPOSED]

All new columns and constraints follow the multiword snake_case rule. Migrations are EF Core migrations whose `Up` emits exactly this SQL (Fluent API where EF can express it; `migrationBuilder.Sql` for the trigger and `NOT VALID`).

### 9.1 AuthorityDb — E3-M2b-1 `AuthorityContentAcceptanceRuleVersion`

> **Superseded by §19.5.** The commit CHECK below uses `>=` (corrected to exact equality, D-3). The trigger guards only the Prepare stamp (extended to a state guard, D-5).

```sql
ALTER TABLE authority.content_change_operations
    ADD COLUMN prepare_acceptance_rule_version integer NULL,
    ADD COLUMN commit_acceptance_rule_version integer NULL;

ALTER TABLE authority.content_change_operations
    ADD CONSTRAINT ck_content_operation_rule_versions_positive
        CHECK ((prepare_acceptance_rule_version IS NULL OR prepare_acceptance_rule_version >= 1)
           AND (commit_acceptance_rule_version IS NULL OR commit_acceptance_rule_version >= 1)),
    ADD CONSTRAINT ck_content_operation_commit_rule_version
        CHECK ((operation_state_name = 'Committed'
                AND COALESCE(commit_acceptance_rule_version, 0) >= COALESCE(prepare_acceptance_rule_version, 0))
            OR (operation_state_name <> 'Committed' AND commit_acceptance_rule_version IS NULL));

CREATE FUNCTION authority.content_operation_rule_version_is_immutable() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF NEW.prepare_acceptance_rule_version IS DISTINCT FROM OLD.prepare_acceptance_rule_version THEN
        RAISE EXCEPTION 'content operation prepare rule version is immutable';
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION authority.content_operation_rule_version_is_immutable() FROM PUBLIC;
CREATE TRIGGER trg_content_operation_rule_version_immutable
    BEFORE UPDATE ON authority.content_change_operations
    FOR EACH ROW EXECUTE FUNCTION authority.content_operation_rule_version_is_immutable();
```

- **Down:** guarded. It raises if any `Prepared` row has a non-NULL stamp, then drops the trigger, function, constraints and columns. Development/Testing only.
- **Grants:** unchanged (the table is already runtime `SELECT, INSERT, UPDATE`). The trigger function is listed in the G08 privilege inventory, following the precedent of `organization_integrity_quarantine_is_permanent` in `ProjectionIntegrityS1`.
- **Existing data:** all existing rows have NULL stamps, so both CHECKs are valid without any rewrite.

### 9.2 AuthorityDb — E3-M2b-1F `AuthorityContentAcceptanceRuleFloor1` (after every instance runs M2b-1)

> **[CORRECTION] Superseded by §19.5. Do not use the SQL below.**
> - **D-1:** `prepare_acceptance_rule_version >= 1` is NULL for an unstamped row, and PostgreSQL accepts a CHECK that evaluates to NULL. Against the real legacy binary, this floor stopped nothing (run 1).
> - **D-2:** the floor is restricted to `Prepared` rows. A legacy instance still running after the floor could therefore commit an operation prepared before it.
> - **D-4:** `VALIDATE` can never succeed, because historical rows are unstamped.

```sql
ALTER TABLE authority.content_change_operations
    ADD CONSTRAINT ck_content_operation_prepare_rule_floor
        CHECK (operation_state_name <> 'Prepared' OR prepare_acceptance_rule_version >= 1) NOT VALID;
-- After the last pre-floor Prepared row has expired or been aborted (operator-run, Development/Testing first):
-- ALTER TABLE authority.content_change_operations VALIDATE CONSTRAINT ck_content_operation_prepare_rule_floor;
```

Later floors (2 in M2d, 3 in M2e) drop and re-add this constraint with the new bound, in the same `NOT VALID` → `VALIDATE` pattern. **Down:** Development/Testing only (a loosening).

### 9.3 BusinessDb — E3-M2b-2 `EnvelopeV2SignedColumns`

```sql
ALTER TABLE field_encrypted_values
    ADD COLUMN data_key_identifier uuid NULL,
    ADD COLUMN data_key_commitment_bytes bytea NULL,
    ADD COLUMN key_wrap_algorithm_code integer NULL;
ALTER TABLE field_encrypted_values DROP CONSTRAINT ck_field_encrypted_values_envelope_positive;
ALTER TABLE field_encrypted_values
    ADD CONSTRAINT ck_field_encrypted_values_envelope_version CHECK (encryption_envelope_version IN (1, 2)),
    ADD CONSTRAINT ck_field_encrypted_values_schema_envelope_tie CHECK (row_signature_schema_version = encryption_envelope_version),
    ADD CONSTRAINT ck_field_encrypted_values_envelope_metadata CHECK (
        (encryption_envelope_version = 1 AND data_key_identifier IS NULL AND data_key_commitment_bytes IS NULL AND key_wrap_algorithm_code IS NULL)
     OR (encryption_envelope_version = 2 AND data_key_identifier IS NOT NULL
         AND data_key_commitment_bytes IS NOT NULL AND octet_length(data_key_commitment_bytes) = 32
         AND key_wrap_algorithm_code IN (1, 2)));

ALTER TABLE file_attachment_versions
    ADD COLUMN data_key_identifier uuid NULL,
    ADD COLUMN data_key_commitment_bytes bytea NULL,
    ADD COLUMN key_wrap_algorithm_code integer NULL;
ALTER TABLE file_attachment_versions
    ADD CONSTRAINT ck_file_attachment_versions_envelope_version CHECK (encryption_envelope_version IN (1, 2)),
    ADD CONSTRAINT ck_file_attachment_versions_schema_envelope_tie CHECK (row_signature_schema_version = encryption_envelope_version),
    ADD CONSTRAINT ck_file_attachment_versions_envelope_metadata CHECK ( /* same form as above */ );
```

- **No defaults, no backfill, no index** (package §5.2 / §5.4).
- **Down:** guarded. It raises if any row of either table has envelope version 2, then drops the constraints and columns and restores `ck_field_encrypted_values_envelope_positive`.
- **Pre-check** (read-only, before the migration is authorized, on copies of the local databases as in M2a §13.1): count rows violating each new CHECK, in particular `row_signature_schema_version <> encryption_envelope_version` on both tables, including `Processed` rows. The expected count is zero. The M2a assessment did not query the schema-version column.
- **EF:** domain properties `DataKeyIdentifier` (`Guid?`), `DataKeyCommitmentBytes` (`byte[]?`), `KeyWrapAlgorithmCode` (`int?`) on `FieldEncryptedValue` and `FileAttachmentVersion`. `SignedColumnRegistryTests` lists them as signed in schema 2 and unsigned in schema 1, with the reason "NULL by construction for schema 1".

### 9.4 AuthorityDb — E3-M2e `AuthorityContentDataKeyBindings`

```sql
CREATE TABLE authority.content_data_key_bindings (
    content_data_key_binding_id uuid NOT NULL PRIMARY KEY,
    data_key_identifier uuid NOT NULL,
    organization_id uuid NOT NULL,
    form_record_id uuid NOT NULL,
    envelope_payload_kind_code smallint NOT NULL,
    data_key_commitment_bytes bytea NOT NULL,
    binding_item_count integer NOT NULL,
    binding_content_change_operation_id uuid NOT NULL,
    data_key_bound_on timestamptz NOT NULL,
    CONSTRAINT uq_content_data_key_binding_identifier UNIQUE (data_key_identifier),
    CONSTRAINT fk_content_data_key_binding_org FOREIGN KEY (organization_id)
        REFERENCES authority.organization_authorization_states (organization_id) ON DELETE RESTRICT,
    CONSTRAINT fk_content_data_key_binding_operation FOREIGN KEY (binding_content_change_operation_id)
        REFERENCES authority.content_change_operations (content_change_operation_id) ON DELETE RESTRICT,
    CONSTRAINT ck_content_data_key_binding_kind CHECK (envelope_payload_kind_code IN (1, 2)),
    CONSTRAINT ck_content_data_key_binding_commitment CHECK (octet_length(data_key_commitment_bytes) = 32),
    CONSTRAINT ck_content_data_key_binding_count CHECK (binding_item_count >= 1 AND (envelope_payload_kind_code = 1 OR binding_item_count = 1)),
    CONSTRAINT ck_content_data_key_binding_identifier_v7 CHECK (
        substring(data_key_identifier::text FROM 15 FOR 1) = '7'
        AND substring(data_key_identifier::text FROM 20 FOR 1) IN ('8', '9', 'a', 'b'))
);
CREATE INDEX ix_content_data_key_bindings_record ON authority.content_data_key_bindings (organization_id, form_record_id);
CREATE INDEX ix_content_data_key_bindings_operation ON authority.content_data_key_bindings (binding_content_change_operation_id);
```

- **Grants:** add the table to `AuthorityRuntimeGrants.InsertOnlyTables` (runtime `SELECT, INSERT` only).
- **Same migration:** raise the CARV floor to 3 (§9.2 pattern).
- **Down:** guarded. It refuses while any binding exists. Development/Testing only.

### 9.5 AuthorityDb — E3-M2g census tables

As in package §6.2, unchanged.

---

## 10. Deployment capability and rollback fences [PROPOSED; consolidated]

| Fence | Protects against | Mechanism | Increment |
|---|---|---|---|
| **CARV** | Mixed Authority builds bypassing Prepare-time rules (R-M2a-3 and every later rule) | Stamp + CHECK + immutable trigger (§3) | M2b-1 |
| **CARV floor** | Older Authority builds preparing at all | `NOT VALID` → `VALIDATE` CHECK, raised per rule version | M2b-1F, re-raised in M2d and M2e |
| Authority required-migration guard | A new binary on an older AuthorityDb (partial restore, unapplied migration) | Startup reads `authority.ef_migrations_history` for compiled required ids; readiness refused, with an identifier-free message | M2b-1 (CARV id), extended in M2d/M2e |
| Business required-migration guard | The M2b-2 business binary on an older BusinessDb | Same pattern, on `public."__EFMigrationsHistory"` | M2b-2 |
| Business CHECKs S-11 / metadata | A business build that drops v2 metadata (F-06/F-16) | §9.3 | M2b-2 |
| Business capability header and floor | Honest older business builds reading or writing v2 records | Package §7.3. Only meaningful once v2 bindings exist. | M2f |
| Acceptance switch | Unauthorized v2 acceptance | Package §8; a Production-designated Authority refuses to start with it on. **New precondition:** the switch cannot be on unless the floor-3 migration id is present (startup guard). | M2e |
| Per-organization data-conditional fence (OD-M2-07) | An older Authority serving an organization that has v2 | **Reframed by CARV** (§14) | M2f, if still wanted |

**OD-M2-07 reframed:**
- **F0 (recommended).** The CARV floor is the fence. It is raised to the ledger rule version (3) before v2 acceptance can be enabled anywhere. It is global, declarative and migration-governed, and needs no trigger reading the ledger.
- **F2 (package).** Additionally keep a per-organization trigger: an organization with bindings requires `prepare_acceptance_rule_version >= 3`, even if the global floor were lower. This is only useful if older Authority builds must keep serving v2-free organizations after the floor is raised. With a single Authority deployment, that is not a current need.
- **F1 (package).** An unconditional NOT NULL column is subsumed by F0.

This remains the owner's decision.

**Order of a rollout (Development/Testing first, then any later environment):**
1. Apply the migration (expand-only).
2. Deploy the new binaries.
3. Confirm no instance runs the older build.
4. Apply the floor migration.
5. Optionally `VALIDATE` once old Prepared rows have expired.

Rolling back before step 4 needs no database change.

---

## 11. Historical revision and restore protection [PROPOSED; consolidated]

| Threat | Protection |
|---|---|
| Re-presenting a row from an older revision | v2: New with a bound id → DK-4. v1: undetected (R-M2a-1, accepted). Whole-snapshot replay: head CAS and R-53 [EXISTING]. |
| Reading history | Unchanged. Verification uses the recorded schema (S-6). The ledger is never consulted on reads. Schema 1 stays registered permanently (S-7). |
| Business restore (`RecordRestoreHandler`) | Values are re-sealed through `ApplyAsync` → New → fresh ids. Documents are carried from the **current** head only (F-20). An M2e test re-verifies that a v2 value restore yields fresh ids. |
| BusinessDb restore | The ledger in AuthorityDb is unaffected. Heads ahead of business rows are quarantined or awaiting (G14). |
| AuthorityDb restore | §7.4. Bindings are rebuilt from the archive. v2 stays off until the verified rebuild. CARV columns revert with their operations, which is harmless: restored Prepared operations are past expiry or are re-checked by CARV. |
| Old binary after restore | The required-migration guards refuse a binary newer than its schema. CARV and its floor refuse older binaries. |

---

## 12. Authorization and audit requirements [PROPOSED; additions to package §8]

| Operation | Who | Dual control | Audit / evidence |
|---|---|---|---|
| Raise the CARV floor | Authority migration identity, under the PB-11 Authority release approval | Staging and production: author + independent approver (PB-12) | Release evidence; migration history |
| Lower the CARV floor / Down migrations | Development/Testing only | n/a | — |
| Bump `ContentAcceptanceRules.CurrentVersion` | Code change, Authority release | As above | Release evidence; tests M2-CARV |
| `OPERATION_RULES_OUTDATED` refusals | Automatic | n/a | `operation_quarantine_reason_code` on the operation; content-operation timing outcome. No journal entry: it is a benign refusal, not an integrity event. Repeated refusals beyond the rollout window are an operational alert. |
| `DATA_KEY_BINDING_MISMATCH` | Automatic | n/a | Minimal content-stream incident (identifier-free), as for the existing candidate mismatch |
| Ledger repair after a restore | Recovery control plane only | **Yes** | Gating stream, recovery evidence |
| Acceptance switch | As in package §8 (OD-M2-10) | Re-enable outside Development/Testing: yes | `EnvelopeAcceptancePolicyObserved` |

No business API, organization setting, form JSON or user role can change any rule version, floor, allow-list or ledger row. Error texts carry reason codes only.

---

## 13. Compatibility with existing v1 records and E3-M2a protections

| E3-M2a protection | After E3-M2b-1 / -1F / -2 | After M2d / M2e |
|---|---|---|
| v1 shape on every write path at Prepare | Unchanged | Unchanged for (1, 1); (2, 2) adds the v2 rules |
| Shape re-check at Commit (`ENVELOPE_INVALID_AT_COMMIT`) | Kept; now runs after CARV | Version-dispatched |
| Within-candidate unique wrapped key (v1) | Unchanged | Unchanged for v1 rows; v2 batch rules for v2 rows (F-14) |
| Cross-revision reuse (`EnforceAgainstCurrent`) | Unchanged; **now also guaranteed for every committed operation** (CARV) | v1 map from v1 rows only; v2 Carried/New per item |
| Census shape and reuse | Unchanged | Version-dispatched |
| Error format `ENVELOPE_INVALID: <reason>` (422) | Unchanged; adds `409 OPERATION_RULES_OUTDATED` | Adds the v2 reasons |
| Existing v1 rows (1,038 field / 184 document in the assessed copies) | Unaffected: no rewrite, no re-sign; all satisfy the new CHECKs (pre-check §9.3 to confirm the schema-version tie) | Unaffected |
| RowHmac v1 / Merkle v1 vectors | Unchanged | Unchanged; schema-2 vectors in a new file |

**Test that changes:** `ACommitOfAnOperationPreparedWithoutTheRulesIsRefusedAndTheOperationAborted` seeds an operation without a stamp, so it would now be refused by CARV first. In M2b-1 it is split into two tests:
- a seeded operation with a NULL stamp, refused with `OPERATION_RULES_OUTDATED`;
- a seeded operation stamped at the current version with a malformed envelope, still refused with `ENVELOPE_INVALID_AT_COMMIT`. This keeps the defence-in-depth rule covered.

---

## 14. Open decision register

**Status key:** Prereq = must be decided before the named increment is authorized.

| ID | Decision | Recommendation | Prereq for |
|---|---|---|---|
| **OD-M2-17** | Adopt CARV (stamp + commit CHECK + immutable trigger + floor) as the deployment-window fence, replacing the package's `envelope_rule_version` design | Adopt | **M2b-1** |
| **OD-M2-18** | Prepare replay of an outdated Prepared operation returns `409 OPERATION_RULES_OUTDATED` (instead of returning the operation) | Yes | **M2b-1** |
| **OD-M2-19** | Prepare stamp made immutable by trigger (vs application discipline only) | Trigger | **M2b-1** |
| **OD-M2-12** | Throwaway-PostgreSQL test script (moved earlier, F-18) | Approve at M2b-1 | **M2b-1** |
| **OD-M2-20** | Authority required-migration startup guard introduced in M2b-1 | Yes | **M2b-1** |
| **OD-M2-21** | Floor timing and governance: a separate migration after drain; `NOT VALID` then `VALIDATE`; lowering only in Development/Testing | Approve | **M2b-1F** |
| E-3 §13.8 item 2 | Authorize an Authority behaviour change and an AuthorityDb migration | Owner | **M2b-1** |
| OD-M2-01 | Columns on two tables only | Approve | **M2b-2** |
| OD-M2-02 | Store `key_wrap_algorithm_code` | Store it | **M2b-2** |
| OD-M2-14 | Schema-2 vectors in a new file | Approve | **M2b-2** |
| **OD-M2-25** | Vector generator: a new script or a new entry point in `generate_vectors.py`, writing only the new file; both cross-language checkers (Python, Node) extended | New entry point, new file | **M2b-2** |
| **OD-M2-22** | S-9 type and nullability pinning: the two envelope tables only (M2b-2/M2d) or every registered kind | Envelope tables now; all kinds as a separate tightening | **M2b-2** (scope) |
| OD-E3-4, OD-E3-14 | Wrap in the signed row (W1); batch scope (record, operation, purpose) | W1; (record, operation, purpose) | **M2b-2** (the schema fixes both) |
| E-3 §13.8 item 3 | Authorize the business migration | Owner | **M2b-2** |
| OD-M2-06 | v2 → v1 downgrade refused from M2d (§6.2) | Yes, with switch-off default | **M2d** |
| OD-M2-03 | Bind data-key ids at Prepare (§7.1) | Prepare | **M2e** |
| OD-M2-04 | Global ledger uniqueness | Global | **M2e** |
| OD-M2-05 | Carry from the current head only | Current head only | **M2d** (classification), **M2e** |
| OD-M2-10 | Acceptance-switch governance, plus the new floor-3 precondition | Approve | **M2e** |
| OD-M2-15 | `new_data_key_identifiers` in the archived journal body | Approve | **M2e** |
| **OD-M2-23** | Organization ↔ operation agreement for bindings: application-enforced (same transaction) or composite FK (new unique key on operations) | Application, with a test | **M2e** |
| OD-M2-07 | Reframed (§10): F0 CARV floor, or F2 per-organization trigger in addition | F0 | **M2f** |
| OD-M2-08 | Business capability floor per organization | Per organization | **M2f** |
| OD-M2-11 | Census readers | Approve | **M2g** |
| **OD-M2-24** | Optional Commit-time re-validation with resent current items (defence in depth beyond CARV) | Not now; revisit after M2e | — |
| OD-M2-13 | V2-IND-01 before M4 | Before M4 | M4 |
| **OD-M2-26** | Specification-manifest entry for this document (`generate-spec-manifest.py`) | Authorize with M2b-1 | M2b-1 commit |
| OD-M2-09 | Universal v1 validation after a census | **Implemented in M2a** (accepted) | — |
| OD-M2-16 | Manifest entry for the M2 package | **Done in `66f0aa7`** | — |

None of the above is decided by this document.

---

## 15. Revised increment plan

| Increment | Scope | Schema | Prereqs | Rollback |
|---|---|---|---|---|
| **E3-M2b-1** CARV | `ContentAcceptanceRules`; stamp at Prepare; replay and Commit rules; `OPERATION_RULES_OUTDATED` (private Authority OpenAPI + contracts); immutable trigger; Authority required-migration guard; business error mapping (§3.6); test split (§13); throwaway-PostgreSQL script | AuthorityDb §9.1 | OD-M2-17/18/19/12/20, §13.8 item 2 | Binary rollback to M2a is safe until M2b-1F; Down guarded (Development/Testing) |
| **E3-M2b-1F** floor 1 | Floor migration only, after confirming every instance runs M2b-1 | AuthorityDb §9.2 | OD-M2-21 | Development/Testing only |
| **E3-M2b-2** business schema 2 groundwork | The package's M2b plus: S-1 in `CurrentSchemaVersion` and document processing (F-16); S-10; S-11 CHECKs; schema-2 entries in `BusinessSignedSchemas`; Authority schema-2 rule entries with S-9 typing (no acceptance; new schema-2 rows still refused); clone fidelity S-3; business required-migration guard; new vector file; `row-integrity-format.md` §7 update (F-19); read-only pre-check | BusinessDb §9.3 | OD-M2-01/02/14/22/25, OD-E3-4/14, §13.8 item 3 | Binary rollback safe (expand-only); Down guarded |
| E3-M2c | As package (dark v2 readers) | none | M2b-2 | Binary |
| E3-M2d | As package, plus the version-dispatched rules (F-14), the downgrade guard, `CurrentVersion = 2`, floor 2 | Floor migration | OD-M2-06, -05 | Binary before floor 2 |
| E3-M2e | As package, plus §7.3 exact protocol, `CurrentVersion = 3`, floor 3, switch precondition | §9.4 | OD-M2-03/04/10/15/23 | Switch off; binary before floor 3 |
| E3-M2f | Capability header and floor; OD-M2-07 per its decision; runbook | Per OD-M2-07 | OD-M2-07/08 | — |
| E3-M2g | Census (package §6) | §9.5 | OD-M2-11 | Additive |

The package's M2a → M2b ordering rationale still holds: tighten v1 → fence → schema → readers → acceptance → ledger → fences → census. CARV is placed first because every later rule depends on it to be enforceable across a deployment.

---

## 16. Recommended smallest safe increment: E3-M2b-1 (CARV)

> **Superseded by §19.12** (exact scope) and §19.10 (tests).

**Why this one first:**
- It closes the only known bypass of an accepted M2a control (R-M2a-3, F-15).
- It is a tightening only: no v2, no business schema, no signed-format change, no vector change.
- It is the enforcement point that M2d's and M2e's Prepare-time rules need. Without it, each would have the same deployment window.
- It is small: one Authority constant class, two nullable columns, two CHECKs, one trigger, a startup guard, about three call-site changes in `ContentService`, and two business catch lists.
- It is revertible: the previous Authority binary still works until the separate floor migration.

**Acceptance criteria:**
- Every existing Authority, contract, architecture and integrity suite is green, with only the §13 test split.
- New tests M2-CARV-01..08 and M2-CARV-PG-01..06 pass (§17).
- Mutation checks: removing the Commit rule, the replay rule, the CHECK or the trigger each fails at least one test.
- No Stage B run.

---

## 17. Focused test plan

Runs are focused only: test executables are run directly with `--filter-class`. Real PostgreSQL runs only through the new throwaway-container script (a fresh `postgres:18` container with no network beyond localhost, created and destroyed by the script), never against the developer or security-test stack, and never as Stage B.

### 17.1 E3-M2b-1

> **Superseded by §19.10.** Several database-level cases below are now executed design-gate evidence (§19.6, §19.7).

| ID | Test | Kind |
|---|---|---|
| M2-CARV-01 | Prepare stamps `CurrentVersion`; Commit stamps the commit version; both are visible on the stored operation | Authority (in-memory) |
| M2-CARV-02 | A seeded operation with a NULL stamp → Commit `409 OPERATION_RULES_OUTDATED`; aborted with that reason; head, leaves, identities, journal and checkpoint unchanged | Authority |
| M2-CARV-03 | **R-M2a-3 regression:** a NULL-stamped operation whose candidate reuses the current head's wrapped key and nonce for changed ciphertext (which `EnforceAgainstCurrent` would refuse) → refused at Commit by CARV, never accepted | Authority |
| M2-CARV-04 | Stamp below the minimum refused; equal accepted (boundary, using a test-configured minimum) | Authority |
| M2-CARV-05 | Prepare replay of an outdated Prepared operation → `409 OPERATION_RULES_OUTDATED`, no insert; replay of a current-stamp operation unchanged | Authority |
| M2-CARV-06 | Commit replay of a historical `Committed` operation with NULL stamps still returns the head | Authority |
| M2-CARV-07 | The M2a commit-time shape rule still fires for a current-stamped malformed operation (`ENVELOPE_INVALID_AT_COMMIT`) | Authority (the split test) |
| M2-CARV-08 | Business: creation step 1 and step 2 map `OPERATION_RULES_OUTDATED` to a fresh operation; protection migration retries | Contract (focused) |
| M2-CARV-PG-01 | Real PostgreSQL: a raw "old-binary" insert (columns omitted) then a new-build Commit transition is refused by the application; a raw Committed update without a commit stamp on a stamped operation fails `ck_content_operation_commit_rule_version` | Throwaway PostgreSQL |
| M2-CARV-PG-02 | NULL/NULL Committed transition passes (old → old before the floor) | Throwaway PostgreSQL |
| M2-CARV-PG-03 | The trigger refuses any change of the prepare stamp | Throwaway PostgreSQL |
| M2-CARV-PG-04 | The floor (`NOT VALID`) refuses a new NULL Prepared insert; an existing NULL Prepared row can still be set `Aborted`; `VALIDATE` succeeds once none remain | Throwaway PostgreSQL |
| M2-CARV-PG-05 | The runtime role cannot drop or alter the constraints or the trigger (G08) | Throwaway PostgreSQL |
| M2-CARV-PG-06 | **Load-bearing assumption:** an `AuthorityDbContext` model with the two properties ignored (simulating the M2a binary) inserts and updates operations without touching the new columns | Throwaway PostgreSQL |
| M2-FEN-08 | The Authority required-migration guard refuses readiness when the M2b-1 migration id is missing | Authority |

### 17.2 E3-M2b-2

These are the package's M2-SIG-01..07, M2-CMP-04, M2-FEN-05/06/07 and M2-ARCH-01, plus:

| ID | Test |
|---|---|
| M2-SIG-08 | `CurrentSchemaVersion` derives from the envelope version for both tables; any other envelope version throws (S-1) |
| M2-SIG-09 | The processed document copy takes its schema from its envelope (F-16) |
| M2-SIG-10 | `Encode` throws on a schema/envelope mismatch (S-10) |
| M2-SIG-11 | The Authority refuses a schema-2 row with a wrong type tag or a NULL in a non-nullable column (S-9); identical schema-1 checks pass for every existing v1 fixture |
| M2-SIG-12 | Every new-file vector is reproduced by C#, Python and Node; the existing vector files are byte-identical (`git diff --exit-code`) |
| M2-FEN-09 | The S-11 CHECK refuses schema 1 / envelope 2 and schema 2 / envelope 1 (throwaway PostgreSQL) |
| M2-CMP-07 | Read-only pre-check on copies of the local business databases: zero rows violate any §9.3 CHECK |

### 17.3 M2d / M2e additions

These extend the package's M2-ENV, M2-DK, M2-CON, M2-IDM and M2-REP groups:

| ID | Test |
|---|---|
| M2-ENV-15 | A two-item v2 batch is accepted within a candidate, and is not reported by the census (F-14) |
| M2-ENV-16 | A carried two-item v2 batch is accepted against the current revision |
| M2-ENV-17 | A v2 wrapped key equal to a v1 wrapped key is refused |
| M2-ENV-18 | A switch turned off between Prepare and Commit refuses at Commit |
| M2-DK-11 | A reused id is refused at Prepare and **no business staging occurs** (stager stops before staging) |
| M2-DK-12 | DK-6: a binding set altered between Prepare and Commit (test seam) aborts with `DATA_KEY_BINDING_MISMATCH` |
| M2-RST-06 | Ledger rebuild from archived bodies equals the ledger before the simulated restore |
| M2-CARV-09 | An operation prepared at version 2 cannot be committed by a version-3 build, and the reverse is refused by the CHECK |

**Not run:** Stage B, Azure, a real HSM, PB-09 performance, V2-IND-01.

---

## 18. What this contract does not claim

- Apart from E3-M2b-1 (§20, Development/Testing only), no part of it is implemented or tested. The worked example in §4.5 is illustrative output of the existing reference encoder, not a normative vector.
- CARV protects against honest older builds and mixed fleets, not against a compromised Authority runtime (outside the Level 2 model).
- Seed reuse under a fresh data-key id stays undetectable by the Authority (TH-M2-02). Historical v1 key reuse stays undetected (R-M2a-1).
- No compliance, certification or production readiness is implied.

---

## 19. E3-M2b-1 CARV — final design gate (2026-11-14)

**Brief.** The owner conditionally approved the CARV direction and asked for:
- proof against a real pre-CARV binary;
- a mixed-version concurrency analysis with a clear serialization point;
- an explicit floor contract;
- a migration safety review;
- isolated PostgreSQL verification (OD-M2-12, approved for disposable local PostgreSQL only).

Implementation is **not** authorized. No production code or migration was written. No repository file other than this document and the evidence bundle changed.

### 19.1 What was executed

| Item | Detail |
|---|---|
| Legacy binaries | Real Release builds of `66f0aa7` (E3-M2a; the build that will be running when M2b-1 deploys) and `de1729b` (pre-E3-M2a; the build R-M2a-3 is about). Their `ContentChangeOperation` entity and EF configuration are identical (`git diff de1729b 66f0aa7` on both files is empty). The harness runs byte-identical copies of the assemblies; SHA-256 is re-checked on every run. |
| Code exercised | Each binary's own `ContentService.PrepareAsync` / `CommitAsync`, its EF Core `AuthorityStore` (READ COMMITTED scopes, organization row `FOR UPDATE`) on the least-privilege runtime role, its `--migrate` entry point (29 migrations plus runtime grants), and its `AuthorityExceptionHandler` (HTTP mapping). The host's routing and authentication are not exercised; they do not participate in database enforcement. |
| Database | Disposable `postgres:18.0` (PostgreSQL 18.0, image `sha256:6cb75ec2…`): tmpfs data, loopback port, removed after each run. Nothing persistent was touched. The developer and security-test stacks were not started or modified. |
| Holding transactions open | Only through the binaries' own ports: `IFaultInjector` at the in-transaction Commit point (after every check, before writes), and a wrapped `IIntegrityMacProvider` inside the Prepare transaction (before its insert). |
| CARV builds | **Emulated at the database level only** (test-only trigger `sql/emulation.sql.txt`, active per connection). The CARV application rules are not exercised (§19.10). |
| Results | **47/47 checks** against `66f0aa7`, repeated twice with identical outcome; **47/47** against `de1729b` with the same SQL hashes. The first run, with the SQL as originally drafted, failed **16/47** (kept as evidence). |

### 19.2 Defects found in the earlier design (corrected here)

| ID | Defect (§3 / §9.1 / §9.2 as drafted) | Evidence | Correction |
|---|---|---|---|
| **D-1** | **A CHECK that evaluates to NULL passes in PostgreSQL.** `commit = prepare` with a NULL commit stamp, and `prepare >= 1` with a NULL prepare stamp, are NULL, so both were satisfied. The real legacy binary committed a CARV-prepared operation (`1/NULL Committed`), and the floor refused nothing. | run 1: MX-01b/c/e, FL-02..04, PF-01/02, PF-07 failed | Every comparison is preceded by an explicit `IS NOT NULL` term, so each CHECK is strictly two-valued (§19.5) |
| **D-2** | The floor applied to `Prepared` rows only. A legacy instance still running after the floor could commit an operation prepared before it (the new row is `Committed`). | analysis; the corrected floor is proven by FL-02b, FL-04, PF-02 | The floor applies to `Prepared` **and** `Committed` rows |
| **D-3** | The commit rule `COALESCE(commit,0) >= COALESCE(prepare,0)` let the database accept a CARV commit of a legacy-prepared operation (1 ≥ 0). Only the not-yet-written application rule would have refused it. | analysis; the corrected rule is proven by MX-02a, FL-02c, PF-02 | **Exact equality**: a CARV commit requires a CARV prepare stamped at the same version. Enforced by the database, independent of application code (OD-M2-27). |
| **D-4** | The plan to run `VALIDATE CONSTRAINT` after old operations expire can never succeed: historical rows are permanently unstamped. | PF-07 (VALIDATE fails, as now expected) | The floor stays permanently `NOT VALID`. That is safe only because of D-5. |
| **D-5** | A trigger guarding only the Prepare stamp leaves terminal rows updatable, so a `NOT VALID` floor would be re-evaluated against them, and an operation could be inserted directly as `Committed`. | MG-07d/e | The trigger becomes a **state guard**: insert only `Prepared` without a commit stamp; terminal rows are immutable; the Prepare stamp is immutable |

The corrected migration is still the smallest form: **2 nullable columns, 2 CHECKs and 1 trigger** (M2b-1), plus **1 CHECK** in a separate floor migration (M2b-1F).

### 19.3 Final CARV state machine

**Notation:**
- *P* = `prepare_acceptance_rule_version`;
- *C* = `commit_acceptance_rule_version`;
- *F* = installed floor (none before M2b-1F);
- *V* = a CARV build's compiled `ContentAcceptanceRules.CurrentVersion` (1 in M2b-1).

| Transition | Writer | Allowed when | Enforced by |
|---|---|---|---|
| ∅ → `Prepared` | Prepare INSERT | *C* is NULL; and, once a floor exists, *P* is not NULL and *P* ≥ *F*. A CARV build writes *P* = *V*; a legacy build writes *P* = NULL. | trigger (state, *C*); floor CHECK (*P*) |
| ∅ → anything else | — | never | trigger (MG-07e) |
| `Prepared` → `Committed` | Commit UPDATE | **either** *P* = *C* = NULL and no floor exists (legacy → legacy, pre-floor only), **or** *P* and *C* not NULL and *C* = *P* (and *P* ≥ *F*) | commit CHECK; floor CHECK; CARV builds additionally refuse *P* ≠ *V* before any write (§19.4) |
| `Prepared` → `Aborted` | expiry, conflict, revocation, envelope or outdated-rule abort | always (*C* stays NULL) | commit CHECK (*C* NULL); the floor ignores `Aborted` (FL-02e) |
| `Prepared` → `Prepared` (field change) | — | *P* unchanged; *C* NULL | trigger (MG-07a); commit CHECK (MG-07b) |
| `Committed` / `Aborted` / `Quarantined` → anything | — | never | trigger (MG-07d) |

The code inventory confirms the trigger breaks no existing writer. Only `ContentService` writes operation rows, and every Aborted or Committed transition starts from a row it has just checked to be `Prepared` (`ContentService.cs:306-451`). Nothing updates a terminal operation.

**Consequence (the invariant):** once floor *F* is installed, every operation committed afterwards satisfies *P* = *C* ≥ *F*. Its Prepare and its Commit both ran CARV rules of version ≥ *F*. With *F* = 1, every Prepare ran the full E3-M2a rule set, including the cross-revision check. **R-M2a-3 is closed for every commit after the floor**, whatever binary prepared it (pre-M2a binaries included: PF-02 on `de1729b`).

### 19.4 Exact transactional enforcement rules

**Database (both legacy and CARV builds):**
1. CHECK constraints and row triggers are evaluated on every row write. They do not depend on the transaction snapshot or isolation level. (Authority write scopes are READ COMMITTED with the organization row locked `FOR UPDATE`, `AuthorityStore.cs:31`; nothing in CARV relies on that.)
2. A refused write raises SQLSTATE 23514 with the constraint name. EF Core aborts `SaveChanges`, and the scope's transaction rolls back **everything**:
   - the operation transition;
   - manifest commit and leaves;
   - item identities;
   - head advance;
   - search index rows;
   - content journal entry and checkpoint.

   Shown by snapshot equality in MX-01c, MX-02b, FL-02d, FL-04 and PF-02. A refused Prepare persists neither the operation nor its candidate leaves (FL-03, PF-01).

**CARV build (application; implemented in M2b-1, not yet exercised):**

| Point | Rule |
|---|---|
| Prepare, new operation | INSERT with *P* = *V* |
| Prepare, idempotent replay | Returns the existing operation only if it is `Prepared` with *P* = *V*. A `Prepared` operation with *P* ≠ *V* (including NULL) gets `409 OPERATION_RULES_OUTDATED`, nothing written (OD-M2-18). Other states keep the existing replay behaviour. |
| Commit | Order of checks: (1) the operation is already `Committed` → idempotent replay (must work for historical NULL/NULL rows: MG-05); (2) state is not `Prepared` → `OPERATION_STATE_INVALID`; (3) expiry → abort; (4) **CARV: *P* ≠ *V*** → abort with reason `OPERATION_RULES_OUTDATED`, commit the abort, `409 OPERATION_RULES_OUTDATED`; (5) the existing checks unchanged; (6) on success write *C* = *V* with `Committed`. |
| Database refusal seen by a CARV build | A `DbUpdateException` whose `PostgresException` is 23514 with constraint `ck_content_operation_commit_rule_version`, `ck_content_operation_rule_floor_v*` or `trg_content_operation_rule_state_guard` maps to `409 OPERATION_RULES_OUTDATED` plus an operational alert. In a pure CARV fleet this only happens if a newer floor appears while an older CARV build runs. |

**Serialization point of the floor transition:**
- **The lock.** The floor migration's `ALTER TABLE … ADD CONSTRAINT` takes **ACCESS EXCLUSIVE** on `content_change_operations`, observed in `pg_locks` (check `LOCK`). It holds the lock until its transaction commits.
- **Writers already inside a transaction.** Every Prepare and Commit transaction reads that table before writing it, so it holds at least ACCESS SHARE until it ends. The ALTER therefore waits for every such transaction (MG-02, FL-01, FL-02: observed `wait_event_type = Lock`).
  - The in-flight transaction's later write requests ROW EXCLUSIVE. Because the transaction already holds a lock on the relation, PostgreSQL queues it ahead of the waiting ALTER and grants it; no deadlock (FL-01 and FL-02 completed).
  - That write is checked against the **pre-floor** constraint set.
- **Writers that start after the lock is taken.** A transaction that first touches the table after the ALTER holds the lock blocks until the floor commits. It then sees the new constraint, because the relation's invalidation is processed when the lock is acquired (FL-03, FL-04: blocked, then refused).
- **Result:** every operation write is either wholly before or wholly after the floor commit. The floor is linearizable at the commit of its migration transaction. Concurrent CARV writers simply wait out the lock (FL-05: 4 instances, floor held 1.5 s, all OK).
- **Lock-queue hazard.** While the ALTER waits, every new reader of the table queues behind it. Each migration therefore runs `SET LOCAL lock_timeout = '5s'`. On 55P03 nothing is applied and the migration is retried (MG-01, FL-06).

### 19.5 Corrected SQL (final design artifact)

Exact text: `docs/evidence/e3-m2b1-carv/sql/` (hashes in `sql-sha256.txt`). It is reproduced here for review.

**M2b-1 `AuthorityContentAcceptanceRuleVersion`** (one transaction; Up):

```sql
SET LOCAL lock_timeout = '5s';
ALTER TABLE authority.content_change_operations
    ADD COLUMN prepare_acceptance_rule_version integer NULL,
    ADD COLUMN commit_acceptance_rule_version integer NULL;
ALTER TABLE authority.content_change_operations
    ADD CONSTRAINT ck_content_operation_rule_versions_positive
        CHECK ((prepare_acceptance_rule_version IS NULL OR prepare_acceptance_rule_version >= 1)
           AND (commit_acceptance_rule_version IS NULL OR commit_acceptance_rule_version >= 1)),
    ADD CONSTRAINT ck_content_operation_commit_rule_version
        CHECK ((operation_state_name = 'Committed'
                AND ((prepare_acceptance_rule_version IS NULL AND commit_acceptance_rule_version IS NULL)
                  OR (prepare_acceptance_rule_version IS NOT NULL AND commit_acceptance_rule_version IS NOT NULL
                      AND commit_acceptance_rule_version = prepare_acceptance_rule_version)))
            OR (operation_state_name <> 'Committed' AND commit_acceptance_rule_version IS NULL));
CREATE FUNCTION authority.content_operation_rule_state_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.operation_state_name <> 'Prepared' OR NEW.commit_acceptance_rule_version IS NOT NULL THEN
            RAISE EXCEPTION 'content operation must be inserted as Prepared without a commit rule version'
                USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_operation_rule_state_guard';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.operation_state_name <> 'Prepared' THEN
        RAISE EXCEPTION 'a terminal content operation is immutable'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_operation_rule_state_guard';
    END IF;
    IF NEW.prepare_acceptance_rule_version IS DISTINCT FROM OLD.prepare_acceptance_rule_version THEN
        RAISE EXCEPTION 'content operation prepare rule version is immutable'
            USING ERRCODE = 'check_violation', CONSTRAINT = 'trg_content_operation_rule_state_guard';
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION authority.content_operation_rule_state_guard() FROM PUBLIC;
CREATE TRIGGER trg_content_operation_rule_state_guard
    BEFORE INSERT OR UPDATE ON authority.content_change_operations
    FOR EACH ROW EXECUTE FUNCTION authority.content_operation_rule_state_guard();
```

- **Down (Development/Testing only, guarded).** It refuses if any `ck_content_operation_rule_floor_v*` exists (PF-06), or if any `Prepared` row has a non-NULL *P*. Otherwise it drops the trigger, the function, both CHECKs and both columns (MG-06 round trip).
- **Existing rows.** All satisfy the CHECKs as NULL/NULL; `ADD CONSTRAINT` validates them (MG-03).
- **Grants.** Unchanged. The table is already runtime `SELECT, INSERT, UPDATE`. The trigger fires for the runtime role without an EXECUTE grant (every check above). The runtime role cannot drop or alter the constraints or the trigger, or switch triggers off (PF-04).

**M2b-1F `AuthorityContentAcceptanceRuleFloor1`** (a later, separate release; no Down):

```sql
SET LOCAL lock_timeout = '5s';
ALTER TABLE authority.content_change_operations
    ADD CONSTRAINT ck_content_operation_rule_floor_v1
        CHECK (operation_state_name NOT IN ('Prepared', 'Committed')
               OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID;
```

**Later floor raise** (pattern for M2d/M2e; one transaction): add `ck_content_operation_rule_floor_v{N}` with `>= N` `NOT VALID`, then `DROP CONSTRAINT ck_content_operation_rule_floor_v{N-1}`. Version-1 builds are refused immediately afterwards; version-2 builds are admitted (PF-08).

### 19.6 Legacy-binary compatibility evidence (owner item 1)

All references are to `carv-evidence.json` checks. Each one passed against both `66f0aa7` and `de1729b`.

| Owner requirement | Evidence |
|---|---|
| Legacy commits fail closed | MX-01b: the legacy Commit of a CARV-prepared operation is refused (23514 `ck_content_operation_commit_rule_version`), and identically on retry. FL-02b, FL-04, PF-02: after the floor, the legacy Commit of a pre-floor operation is refused (23514 `ck_content_operation_rule_floor_v1`). PF-01: the legacy Prepare after the floor is refused. |
| Missing CARV metadata cannot bypass enforcement | MX-02a (CARV commit of a NULL-stamped operation refused), MG-07a (stamping it afterwards refused), MG-07c (forging Committed refused), MG-07e (direct Committed insert refused), PF-04 (constraints, trigger and `session_replication_role` out of the runtime role's reach). Run 1 is the negative control: without D-1's `IS NOT NULL` terms, missing metadata **did** bypass. |
| No authoritative commit, journal or head advance | Snapshot equality (head revision and root, manifest commits, leaves, item identities, content checkpoints, journal entries, committed operations) in MX-01c, MX-02b, FL-02d, FL-04, PF-02; no persisted operation or leaf in FL-03, PF-01 |
| Legitimate operations complete | Before the floor: legacy→legacy (MG-04), across the migration (MG-02 → MG-04), replay (MG-05). CARV-prepared committed by CARV (MX-01e), concurrent CARV writes (MX-03, FL-05), CARV after the floor (PF-03), version-2 after a raise (PF-08). |
| Failed operations produce recoverable, correctly classified errors | Legacy host: `503 AUTHORITY_NOT_READY retryable` (MX-01d, PF-01b), the legacy binary's own classification of any database error; fail closed, but labelled transient (R-CARV-2). The operation stays `Prepared` and later aborts with `409 OPERATION_EXPIRED` (MX-02c, FL-02e). A fresh operation then succeeds (MX-02d). A retry that reaches a CARV instance commits a CARV-prepared operation (MX-01e). CARV builds return `409 OPERATION_RULES_OUTDATED` (specified, §19.4). |

### 19.7 Concurrent floor-transition evidence (owner item 2)

| Owner case | Check | Observed |
|---|---|---|
| Old Authority prepares while the floor changes (Prepare began before) | FL-02a | Floor waited on the lock; the Prepare's insert completed first (NULL/NULL `Prepared`); afterwards neither build can commit it (FL-02b/c); it aborts on expiry (FL-02e) |
| Old Authority prepares while the floor changes (Prepare began during) | FL-03 | The Prepare blocked on the lock, then was refused when the floor committed; nothing persisted |
| Old Authority commits while the floor changes (Commit began before) | FL-01 | Floor waited; the Commit completed and is linearized before the floor |
| Old Authority commits while the floor changes (Commit began during) | FL-04 | The Commit blocked, then was refused; nothing advanced |
| New prepares, old commits | MX-01 | Refused by the database; the CARV commit then succeeds |
| Old prepares, new commits | MX-02, FL-02c, PF-02 | Refused by the database (independent of the CARV application rule) |
| Multiple new instances concurrently | MX-03 | 6 instances × 4 rounds, 4 records, 2 organizations: only OK or `CONTENT_REVISION_CONFLICT`; every commit *P* = *C* = 1 |
| Multiple new instances during the floor transition | FL-05 | 4 instances; floor held 1.5 s; all OK; floor installed |
| Transaction begins before the floor and completes after | FL-01 (commit), FL-02 (prepare), MG-02 (prepare across the CARV migration) | Each linearized before the DDL, as in §19.4 |
| Migration blocked by an open transaction | MG-01, FL-06 | `lock_timeout` → 55P03; nothing applied; the held transaction completes; retry succeeds |

**Not covered by design-gate evidence:** concurrency of the CARV application rules themselves (they do not exist yet), cross-host network partitions, and production-scale lock contention (PB-09).

### 19.8 Prepare and Commit floor contract (owner item 3)

| Question | Contract |
|---|---|
| CARV recorded at Prepare | *P* = the preparing build's compiled `CurrentVersion`, written in the INSERT; immutable by trigger. Legacy builds record NULL. |
| Minimum permitted Prepare version | The installed floor *F*: the database refuses a `Prepared` row with *P* NULL or *P* < *F*. A CARV build also refuses readiness if *V* < *F* (below). |
| Minimum permitted Commit version | *C* must equal *P* (database) and *P* ≥ *F* (database), so *C* ≥ *F*. A CARV build also refuses before any write when *P* ≠ *V*. There is no separate commit floor: equality makes it redundant. |
| How each floor is stored | *P* and *C* per operation row. *F* is the constant in the CHECK constraint `ck_content_operation_rule_floor_v{F}` (exactly one present). It can be read through `pg_catalog.pg_constraint` by the runtime role (PF-05). *V* is compiled into the Authority. |
| Who may advance the floors | Only an Authority migration in an Authority release, applied by the migration identity. In staging and production it needs the PB-11 Authority release approval, with independent approval under PB-12. No runtime, operator-plane, business or configuration path exists. A floor migration is a tightening (one approver in Development/Testing). |
| Monotonic | Yes. Floor migrations have no Down. The CARV Down refuses while any floor exists (PF-06). The runtime role cannot drop or alter constraints (PF-04). A raise is atomic, adding the higher floor before dropping the lower (PF-08). **The only way a floor disappears is a database restore to before it**, which is governed by G14 (§19.11 R-CARV-4). |
| Startup guards and running instances | A CARV build at startup, and again in its readiness check every 30 s (OD-M2-30), refuses readiness if: the M2b-1 migration id is missing from `authority.ef_migrations_history`; the trigger or columns are missing; or the installed *F* > *V*. A running instance that a newer floor overtakes becomes not-ready, and the database already refuses its writes. **Legacy instances have no guard. The database is their only fence**, and FL-03, FL-04, PF-01 and PF-02 prove it works. |
| Retries | The same operation is never retried after `OPERATION_RULES_OUTDATED`. The business starts a fresh operation (new idempotency key, fresh data keys). Legacy `503 retryable` responses may be retried by the existing client logic. Because the refusal is deterministic, the retry ends in success on a CARV instance (MX-01e), `OPERATION_EXPIRED` (MX-02c), or `OPERATION_RULES_OUTDATED`. Never in acceptance. |
| Aborted record creation | `CreateFormRecordHandler` maps `OPERATION_RULES_OUTDATED` like `OPERATION_EXPIRED` in step 1 and like `IDEMPOTENCY_CONFLICT` in step 2, so step 3's fresh retry key follows (§3.6, F-17). `FieldProtectionMigrationService` adds it to its retryable set. |
| Implicit rollback or lowering | None. No code path lowers *F* or rewrites *P* or *C*. A CARV build never falls back to legacy behaviour. |

### 19.9 Deployment, rollback, failure and recovery

**Deployment sequence (Development/Testing first; the same order later):**
1. **Apply M2b-1 migration** (expand-only). It waits for or times out against open transactions (`lock_timeout` 5 s), then retries. Legacy instances keep working unchanged (MG-04).
2. **Deploy CARV v1 binaries** (rolling or blue/green). During the overlap, any operation that crosses builds is refused by the database (MX-01, MX-02): availability errors, never acceptance.
3. **Confirm no legacy instance remains.** An operator step: deployment inventory, plus `pg_stat_activity.application_name` once CARV builds set a versioned application name (part of M2b-1).
4. **Apply M2b-1F floor migration** in a **separate, later release**. EF `Migrate()` applies every pending migration at once, so shipping the floor in the same release as step 1 would fence out legacy instances before step 2 completes (OD-M2-28).
5. **No VALIDATE**, ever (D-4).

**Rollback:**

| When | Allowed | Effect |
|---|---|---|
| After step 1 or 2, before step 4 | Redeploy the M2a binary | Works (MG-04). CARV-prepared in-flight operations fail at legacy Commit (503) and expire. |
| After step 4 | Binary rollback below CARV v1 | **Impossible by design** (fail closed: PF-01). Roll forward only. |
| M2b-1 Down | Development/Testing only | Refused once a floor exists (PF-06), or while stamped `Prepared` rows exist |
| Floor Down | Never | — |

**Failure and recovery:**

| Failure | Procedure |
|---|---|
| Migration lock timeout | Nothing applied (MG-01, FL-06). Retry later; on repeated timeouts, find the long-running transaction (`pg_stat_activity`). |
| Migration error mid-way | One transaction: nothing applied. Fix and re-run. |
| A legacy instance still running after the floor | Every write it attempts is refused (fail closed) and surfaces as 503 retryable. Drain it. No data effect. |
| CARV build older than a newly raised floor | Not ready (guard); its writes are refused by the database. Deploy the newer build. |
| AuthorityDb restored to before M2b-1 or M2b-1F | G14 recovery: the CARV build's startup guard refuses a missing M2b-1 migration. The recovery runbook must re-apply every floor migration that was in force, before reopening the organization gate (R-CARV-4). Restored `Prepared` operations are past expiry, or are refused by CARV. |
| Business sees `OPERATION_RULES_OUTDATED` | Fresh operation (§19.8) |

### 19.10 Tests for the E3-M2b-1 implementation

These replace §17.1. The design-gate evidence (§19.6, §19.7) is **not** a substitute: it proves the database fence and legacy behaviour, not the new application code.

| ID | Test | Kind |
|---|---|---|
| M2-CARV-01 | Prepare stamps *P* = *V*; Commit stamps *C* = *V* | Authority (in-memory) |
| M2-CARV-02 | A `Prepared` operation with *P* NULL or ≠ *V* → Commit `409 OPERATION_RULES_OUTDATED`; aborted with that reason; head, leaves, identities, journal and checkpoint unchanged | Authority |
| M2-CARV-03 | R-M2a-3 regression: a NULL-stamped operation whose candidate reuses the current wrapped key and nonce → refused by CARV at Commit | Authority |
| M2-CARV-04 | Prepare replay of an outdated `Prepared` operation → 409, nothing written; a current-stamp replay is unchanged | Authority |
| M2-CARV-05 | Commit replay of a historical NULL/NULL `Committed` operation returns the head | Authority |
| M2-CARV-06 | The split M2a test: a current-stamp malformed operation is still refused `ENVELOPE_INVALID_AT_COMMIT` | Authority |
| M2-CARV-07 | A database 23514 on a CARV constraint maps to `OPERATION_RULES_OUTDATED` plus an alert | Authority (store adapter) |
| M2-CARV-08 | Business creation steps 1 and 2 and the protection-migration retry handle `OPERATION_RULES_OUTDATED` | Contract (focused) |
| M2-CARV-09 | Startup and readiness guard: missing migration, missing trigger, *F* > *V* → not ready; *F* ≤ *V* → ready; re-evaluated when a floor appears while running | Authority |
| **M2-CARV-PG** | **Re-run this design-gate harness unchanged against the repository's real M2b-1 migration** (applied by the new binary's `--migrate`), with both legacy binaries. Add the same scenarios with the **real CARV build** in place of the emulation trigger. Disposable PostgreSQL only, via a new script on the E-2/E-5 pattern (OD-M2-29). | Throwaway PostgreSQL |
| — | Existing suites green: Authority (663 + new), contract focused, architecture 124, integrity 60. Mutation checks: removing the Commit rule, the replay rule, an `IS NOT NULL` term, the floor's `Committed` scope, or the state guard each fails at least one test. | — |

### 19.11 Remaining risks

| ID | Risk | Disposition |
|---|---|---|
| R-CARV-1 | Between step 2 and step 4, legacy instances still run legacy (M2a) rules for their own operations. | Equivalent to today's accepted behaviour; bounded by the rollout. Shortened by applying the floor promptly after drain. |
| R-CARV-2 | Legacy binaries classify CARV refusals as `503 AUTHORITY_NOT_READY retryable`, a transient label. | Fail closed, never acceptance. Cannot be changed in existing binaries. Mitigated operationally: alert on Authority 503s during a rollout. |
| R-CARV-3 | A compromised Authority runtime can stamp its own operations, and the migration identity can drop constraints. | Outside the Level 2 model (the Authority is trusted). The runtime role cannot alter the fence (PF-04). |
| R-CARV-4 | A database restore to before a floor migration removes that floor. | G14 recovery must re-apply the floors before reopening. Added as a runbook requirement. |
| R-CARV-5 | Lock-queue stall: a waiting ACCESS EXCLUSIVE blocks new readers for up to `lock_timeout` (5 s). | A bounded availability blip. Retry; run in low traffic. Production-scale behaviour belongs to PB-09. |
| R-CARV-6 | The CARV application rules are untested until implemented. | §19.10. The database fence holds regardless of application bugs, for cross-build cases (MX-02a). |
| R-CARV-7 | The equality rule makes operations in flight at a CARV version bump uncommittable. | Accepted availability cost: re-stage. Rare (once per rule bump). |
| R-CARV-8 | The state guard forbids any future legitimate update of a terminal operation. | Verified: no such writer exists today. A future need requires a deliberate, reviewed guard change. |
| R-CARV-9 | "CARV instances" in the evidence are database-level emulations. | M2-CARV-PG re-runs the scenarios with the real build. |
| R-CARV-10 | Only two legacy binaries were tested. | Their operation mapping is identical. Older builds are not deployed anywhere the floor will apply. |

### 19.12 Exact scope proposed for E3-M2b-1

**Authority:**
- **Domain:** `ContentChangeOperation.PrepareAcceptanceRuleVersion` and `CommitAcceptanceRuleVersion` (`int?`).
- **Application (Content):**
  - new `ContentAcceptanceRules` (`CurrentVersion = 1`);
  - Prepare stamp and replay rule;
  - Commit rule and stamp (§19.4);
  - `AuthorityErrorCodes.OperationRulesOutdated`.
- **Infrastructure:**
  - EF mapping;
  - migration `AuthorityContentAcceptanceRuleVersion`, exactly the §19.5 SQL (trigger through `migrationBuilder.Sql`), with the guarded Down;
  - store-adapter mapping of CARV constraint refusals;
  - G08 privilege inventory entry for the trigger function.
- **Api:** startup and readiness guard (§19.8); versioned `ApplicationName` on the Authority's database connections.
- **Contracts:** `OPERATION_RULES_OUTDATED` in `trust_authority/trust-authority.openapi.yaml` (private contract).

**Business:** the `OPERATION_RULES_OUTDATED` mapping in `CreateFormRecordHandler` and `FieldProtectionMigrationService` only.

**Tests:** §19.10, plus the new throwaway-PostgreSQL script.

**Documents:** this contract's implementation record; the runbook entries for §19.9 and R-CARV-4.

**Not in E3-M2b-1:**
- the floor migration (E3-M2b-1F, a separate later release);
- any v2 acceptance, ledger, business schema, signed schema 2, vectors or key-provider change;
- Stage B; Azure.

**Owner decisions needed before implementation:**

| ID | Decision | Recommendation |
|---|---|---|
| OD-M2-17 | Adopt CARV as corrected in §19 (two-valued CHECKs, equality commit rule, state guard, permanently `NOT VALID` floor covering `Prepared` and `Committed`) | Adopt |
| OD-M2-18 | The Prepare replay rule for outdated stamps | Yes |
| OD-M2-19 | The trigger as a state guard (not only stamp immutability) | Yes (required for D-4/D-5) |
| OD-M2-20 | Authority startup and readiness guard in M2b-1 | Yes |
| OD-M2-21 | The floor in a separate later release; no Down; raise pattern add-then-drop | Approve |
| **OD-M2-27** | Exact equality *C* = *P* (database-enforced) rather than *C* ≥ *P* | Equality |
| **OD-M2-28** | The floor stays permanently `NOT VALID`; never `VALIDATE` | Approve |
| **OD-M2-29** | Keep the design-gate harness as a re-runnable gate (M2-CARV-PG), in a new `scripts/dev` throwaway-PostgreSQL script; legacy binaries built from tagged commits | Approve |
| **OD-M2-30** | Readiness re-check interval for the floor | 30 s |
| OD-M2-12 | Disposable local PostgreSQL for constraint tests | **Approved by the owner (2026-11-14)** |
| OD-M2-26 | Specification-manifest entries for this document and the evidence README | Authorize with the M2b-1 commit |

---

## 20. E3-M2b-1 CARV — implementation record (2026-11-14)

**Status.** Implemented for **Development/Testing only**, on branch `e3/m1-envelope-v2-core` on top of `66f0aa7`. **Accepted by the owner for Development/Testing (2026-11-14)** with the decisions in §20.9, and committed locally as one signed commit (not pushed, merged or deployed). Owner-led review; not an independent review, not a certification, not production-ready. No Azure call; no persistent database was touched; Stage B did not run.

**Not implemented (as authorized):** the floor migration E3-M2b-1F (no repository migration creates a floor; the gate uses test-only floor SQL), E3-M2b-2 and later increments, any Envelope v2 acceptance, signed schema 2, vectors, and key-provider changes. Every E3-M2a protection is unchanged (§20.4).

### 20.1 Owner authorization and decisions applied

The owner approved E3-M2b-1 for Development/Testing on the baseline `66f0aa7b257d984555533ceb10dfccfa921aa273`, with §19 superseding §3, §9.1, §9.2, §16 and §17.1.

| Decision | Applied as |
|---|---|
| OD-M2-17 (CARV as corrected in §19) | Two nullable stamp columns, two strictly two-valued CHECKs, the state-guard trigger; the exact §19.5 SQL (§20.3) |
| OD-M2-18 (Prepare replay rule) | `ContentService` Prepare: a `Prepared` operation with *P* ≠ *V* (NULL included) → `409 OPERATION_RULES_OUTDATED`, nothing written; an idempotency conflict still wins; other states replay unchanged |
| OD-M2-19 (state guard) | `trg_content_operation_rule_state_guard` exactly as §19.5 |
| OD-M2-20 (startup and readiness guard) | `ContentAcceptanceRuleGuard` + `ContentAcceptanceRuleMonitor` (evaluated at host start, then every 30 s) feeding `AuthorityReadiness` |
| OD-M2-21 / OD-M2-28 (floor in a separate later release; never VALIDATE) | No floor in any repository migration; an Authority test fails if one appears. Floor SQL exists only as test-only gate SQL |
| OD-M2-27 (exact equality *C* = *P*) | Commit CHECK and the application rule both require equality |
| OD-M2-29 (re-runnable gate) | `scripts/dev/e3-m2b1-carv-pg-gate.ps1` + `tools/experiments/e3-m2b1-carv-gate/` (§20.6) |
| OD-M2-30 (30 s) | `ContentAcceptanceRules.ReadinessRecheckInterval` (asserted by a test) |
| OD-M2-12 (disposable PostgreSQL) | Every PostgreSQL run used a fresh `postgres:18.0` container (tmpfs, loopback, removed afterwards) |
| OD-M2-26 (manifest entries) | The specification manifest was regenerated because the documented process requires it (`validate-specifications.py --strict` failed on the changed private OpenAPI reference); see §20.8 |

### 20.2 What was implemented

**Authority — Domain:** `ContentChangeOperation.PrepareAcceptanceRuleVersion` / `CommitAcceptanceRuleVersion` (`int?`).

**Authority — Application:**
- `ContentAcceptanceRules` (new): `CurrentVersion = 1`, `ReadinessRecheckInterval = 30 s`, `IsCommittableByThisBuild(P) ⇔ P = V`, the fence object names (`IsFenceObject`: the commit CHECK, the state guard, `ck_content_operation_rule_floor_v<digits>`), and the pure readiness decision `Evaluate(SchemaFacts)`.
- `AuthorityErrorCodes.OperationRulesOutdated = "OPERATION_RULES_OUTDATED"` (409, not retryable: the refused operation itself can never be retried).
- `ContentService.PrepareCoreAsync`: the replay rule (after the idempotency-conflict check, before the existing replay); the insert carries *P* = *V*.
- `ContentService.CommitCoreAsync`, in the §19.4 order: (1) Committed replay unchanged (historical NULL/NULL replays); (2) `OPERATION_STATE_INVALID`; (3) expiry abort unchanged; (4) **new:** *P* ≠ *V* → abort with `operation_quarantine_reason_code = 'OPERATION_RULES_OUTDATED'`, commit the abort, `409 OPERATION_RULES_OUTDATED`, before any signature verification; (5) every existing check unchanged, including the M2a commit-time envelope rule; (6) *C* = *V* written with `Committed`.

**Authority — Infrastructure:**
- EF mapping: both columns (snake_case via `MapSnakeCase`) and both CHECKs, declared with the same SQL text the migration creates (`ContentAcceptanceRuleSql`), so the model snapshot and the database agree.
- Migration `20261115014221_AuthorityContentAcceptanceRuleVersion` (§20.3).
- `ContentAcceptanceRuleRefusals` + `AuthorityStore`: every scope write (`FlushAsync` and `CommitAsync`) goes through one `SaveAsync`; a `DbUpdateException` whose `PostgresException` is SQLSTATE 23514 on a fence object becomes `409 OPERATION_RULES_OUTDATED` and raises the operational security alert `CONTENT_ACCEPTANCE_RULE_REFUSED_BY_DATABASE` (Critical; organization and operation from the failed entry when available). Any other database error is unchanged.
- `ContentAcceptanceRuleSchemaReader`: one read-only catalog query with the runtime role (migration history row, both columns, the validated commit CHECK, an enabled state guard, the highest installed floor).
- `ContentAcceptanceRuleConnection`: the Authority store connection's `application_name` carries `carv-v1` (§19.9 step 3).

**Authority — Api:** `ContentAcceptanceRuleGuard` (latest decision; never evaluated, unreadable or older than 90 s ⇒ not ready) and `ContentAcceptanceRuleMonitor` (hosted; skipped during OpenAPI generation). `AuthorityReadiness` consults the guard after the store-reachability check.

**Private contract:** `trust_authority/trust-authority.openapi.yaml` documents `OPERATION_RULES_OUTDATED` (header note and the Conflict response). The compiled OpenAPI artifact is unchanged (no endpoint or schema changed).

**Business (error mapping only):** `CreateFormRecordHandler.IsAbandonedOperation` (step 1: adds the code to `OPERATION_EXPIRED`/`OPERATION_STATE_INVALID`) and `IsUnusableOperationKey` (step 2: adds it to `IDEMPOTENCY_CONFLICT`); `FieldProtectionMigrationService.IsRetryableConflict` (adds it to the retryable set; the next attempt number is a fresh operation). The catch filters now call these predicates; every previous code keeps its classification. No business persistence or signed data changed.

**Documents:** this record; `docs/security/E3_M2B1_CARV_OPERATIONS.md` (deployment, readiness, alerts, rollback, recovery); two R-CARV-4 lines in `trust_authority/DISASTER_RECOVERY_OPERATOR_RUNBOOK.md`; the evidence bundle `docs/evidence/e3-m2b1-carv/implementation/`.

### 20.3 Migration

| Item | Value |
|---|---|
| Id | `20261115014221_AuthorityContentAcceptanceRuleVersion` (EF timestamp in UTC) |
| Up | `migrationBuilder.Sql(ContentAcceptanceRuleSql.UpSql)`: exactly the §19.5 SQL (the design artifact's comments omitted), including `SET LOCAL lock_timeout = '5s'` |
| Down | `ContentAcceptanceRuleSql.DownSql`: the §19.5 guarded Down (refuses once a floor exists or while CARV-prepared operations are Prepared); Development/Testing only |
| Atomicity | EF applies the migration in one transaction. Proven: under a held legacy transaction the real `--migrate` failed with 55P03 and left no column, CHECK, trigger or history row (RM-01); a retry applied it once (RM-02, 30 migrations) |
| Existing rows | All validate as NULL/NULL (MG-03) |
| Grants | Unchanged. G08 inventory of the trigger function on real PostgreSQL: `secdef=false`, owner = the migration identity, `search_path=pg_catalog, pg_temp`, ACL `{postgres=X/postgres}` (no PUBLIC), runtime `EXECUTE` = false, and the trigger still fires for the runtime role (RB-13). The runtime role cannot drop, disable or alter the fence (PF-04) |
| Floor | **Not** included (OD-M2-21) |

The EF-scripted Up and Down (`dotnet ef migrations script`) are in the evidence bundle.

### 20.4 Test results (all runs on 2026-11-14)

| Suite | Result |
|---|---|
| Authority (full) | 704 total: **700 passed, 0 failed, 4 skipped** (the real-Vault classes, which need their own scripts). Before E3-M2b-1: 663/0/4. +37 new tests |
| Authority focused (`ContributorWriteProtocolTests`, `ContentAcceptanceRuleGuardTests`) | 102/102 |
| Contract (full) | 792 total: **783 passed, 0 failed, 9 skipped** (real-Vault classes) |
| Contract focused (`ContentAcceptanceRuleBusinessMappingTests` and the nine classes that host the Authority or cover E3-M2a) | 102/102 |
| Architecture | **124/124** |
| Integrity | **60/60** |
| Re-run after the enforcement-removal checks (restored source) | Authority 700 passed / 4 skipped, contract 783 passed / 9 skipped, architecture 124/124, integrity 60/60 |
| Stage B | **Not run** |

New tests (contract IDs §19.10):

| ID | Test(s) |
|---|---|
| M2-CARV-01 | `PrepareStampsTheBuildRuleVersionAndCommitStampsItWithTheCommittedTransition`, `AbortedOperationsKeepTheirPrepareStampAndNeverReceiveACommitStamp`; real PostgreSQL RB-01, RB-08, RB-09 |
| M2-CARV-02 | `ACommitOfAnOperationPreparedUnderOtherRulesIsAbortedBeforeAnyVerificationAndNothingIsAccepted` (NULL and *V*+1; proves no signature verification ran); `AnExpiredOutdatedOperationIsStillReportedAsExpired`; RB-03a/b/c |
| M2-CARV-03 | `AnUnstampedOperationReusingTheCurrentKeyAndNonceIsRefusedAtCommitAndNeverAccepted`; real legacy build RB-06b (§20.5) |
| M2-CARV-04 | `APrepareReplayOfAnOutdatedPreparedOperationIsRefusedAndWritesNothing` (NULL, *V*+1), `APrepareReplayOfACurrentOperationIsUnchangedAndAnIdempotencyConflictStillWins`; RB-04 |
| M2-CARV-05 | `ACommitReplayOfAHistoricalUnstampedCommittedOperationReturnsTheHeadAndWritesNothing`; RB-05 |
| M2-CARV-06 | the M2a test `ACommitOfAnOperationPreparedWithoutTheRulesIsRefusedAndTheOperationAborted` now carries a current stamp and still proves `ENVELOPE_INVALID_AT_COMMIT` (the split) |
| M2-CARV-07 | `ADatabaseFenceRefusalBecomesOperationRulesOutdatedAndRaisesAnAlert` (4 fence names), `AnyOtherDatabaseErrorIsLeftUnchangedAndRaisesNoAlert` (6 cases); real PostgreSQL RB-18 (Commit) and RB-19 (Prepare) |
| M2-CARV-08 | `ContentAcceptanceRuleBusinessMappingTests` (3 tests). **Scope:** the exact predicates the catch filters use, not an end-to-end handler run (the handlers' end-to-end flows are Stage B, not run) |
| M2-CARV-09 | `TheGuardIsReadyOnlyForACompleteFenceAtOrBelowThisBuild` (8 cases), `ARunningInstanceBecomesNotReadyWhenANewerFloorAppearsOrTheFenceCannotBeRead`, `TheMonitorEvaluatesTheGuardAtStartup`, `TheApprovedRecheckIntervalIsThirtySeconds`; real PostgreSQL RB-G0..G3, RB-12, and host readiness RB-R0..R2 |
| Schema | `TheModelMigrationAndContractSqlAgree`, `NoMigrationInstallsAFloorAndTheCarvMigrationIsTheLatest`, `StoreConnectionsCarryTheBuildRuleVersion` (3), `AMissingConnectionStringStaysMissing` |
| M2-CARV-PG | §20.5 |

### 20.5 Real-PostgreSQL gate (M2-CARV-PG) and legacy-binary compatibility

**Binaries.** The frozen design-gate Release builds of `66f0aa7` (E3-M2a) and `de1729b` (pre-E3-M2a), SHA-256 re-verified against `docs/evidence/e3-m2b1-carv/` before every run, and a Release build of the final working tree made by the gate itself. PostgreSQL 18.0, image `sha256:6cb75ec2…` (the design gate's image).

**Runs of record:** `scripts/dev/e3-m2b1-carv-pg-gate.ps1 -LegacyBinariesRoot <frozen builds>` (evidence `docs/evidence/e3-m2b1-carv/implementation/runs-of-record/`). The same harnesses were first run from the scratchpad against an earlier build of the working tree (`development-runs/`), with identical results; the enforcement-removal checks (§20.6) used those harness builds.

| Run | What it exercises | Result |
|---|---|---|
| Harness A × `66f0aa7` | The **unchanged** 47 design-gate checks, but with the **real** migration applied by this tree's `--migrate` (MG-01, MG-02, MG-06 Up) and its EF-scripted Down (MG-06, PF-06), plus RM-01 (atomic failed migration), RM-02 (recorded once), RM-03 (real Down refused while a CARV-prepared operation is in flight). CARV instances: test-only emulation | **50/50 passed** |
| Harness A × `de1729b` | the same | **50/50 passed** |
| Harness B × `66f0aa7` | The **real** CARV build (no emulation) with the legacy build through the legacy agent: 36 checks | **36/36 passed** |
| Harness B × `de1729b` | the same | **36/36 passed** |

**Compatibility findings (owner §3 list):**

| Requirement | Evidence |
|---|---|
| Legacy binaries cannot commit operations that require newer rules | MX-01b (both legacy builds; database commit CHECK); RB-02b with the real build's own stamp; after the floor: FL-02b, FL-04, PF-02, RB-16 |
| New binaries reject operations prepared under outdated rules | RB-03a/b (real application rule: 409, aborted with `OPERATION_RULES_OUTDATED`, before any signature verification), RB-16; the database independently: MX-02a |
| No failed operation advances head, manifest, journal, checkpoint or other authoritative state | Snapshot equality (head revision and root, manifest commits, leaves, item identities, checkpoints, journal entries, committed operations) in MX-01c, MX-02b, FL-02d, FL-04, PF-02, RB-02b, RB-03b, RB-05, RB-06b, RB-16, RB-18; no persisted operation in FL-03, PF-01, RB-15, RB-19 |
| Concurrent transactions remain safe | MX-03 / RB-09 (6 instances × 4 rounds, 2 organizations: only OK or `CONTENT_REVISION_CONFLICT`; every Committed row *P* = *C*), FL-05 / RB-14 (writers during the floor transition), FL-01..04 and MG-02 (linearization at the DDL lock) |
| CARV metadata cannot be changed after Prepare | MG-07a..e (runtime-role SQL), PF-04 (fence out of the runtime role's reach), RB-12 (disabled guard ⇒ not ready) |
| Aborted operations and record-creation retries recover | MX-02c/d, RB-03c/d (an aborted operation is never accepted, a fresh one commits), RB-07/RB-08 (existing abort paths under the guard); business mapping §20.2 |
| Unsupported runtime versions fail readiness or startup | RB-G3/RB-R2 (floor 2 above build 1: not ready), RB-G0/RB-R0 (migration missing), RB-12; the v1 build's writes after a raise are refused and alerted (RB-18/19) |
| No dependence on the 10-minute lifetime or the 30-second interval | Every refusal above is immediate and database- or rule-enforced; the guard is not consulted on the write path |

**R-M2a-3 with the real builds (RB-06).** The pre-M2a build `de1729b` **did** prepare a candidate that reuses the current head's wrapped key and nonce for changed ciphertext (it has no cross-revision rule); the real CARV Commit refused it (`OPERATION_RULES_OUTDATED`) and nothing was accepted. The E3-M2a build `66f0aa7` refuses that candidate at its own Prepare. This build's Prepare refuses it too (RB-06a, control).

**What the legacy builds report.** A database refusal reaches a legacy host as `503 AUTHORITY_NOT_READY retryable` (MX-01d, PF-01b, RB-02c): fail closed, transient label (R-CARV-2, unchanged).

### 20.6 Deliberate enforcement-removal checks

Each removal was applied alone, built, tested, then reverted; every file's SHA-256 matched its backup afterwards.

**Application level (in-memory Authority tests and contract tests):**

| ID | Removed | Detected by |
|---|---|---|
| MA-1 | the CARV Commit rule | 3 tests, including the R-M2a-3 regression |
| MA-2 | the Prepare replay rule | 2 tests (both stamp variants) |
| MA-3 | the Prepare stamp | 61 tests |
| MA-4 | the Commit stamp | 1 test |
| MA-5 | the store adapter's recognition of fence refusals | 4 tests |
| MA-6 | the floor-above-build readiness condition | 2 tests |
| MA-7 | the missing-migration readiness condition | 1 test |
| MA-8 / MA-9 | record creation step 1 / step 2 mapping | 1 test each |
| MA-10 | the protection-migration mapping | 1 test |

**Real PostgreSQL (mutated Release build or mutated gate SQL, legacy `66f0aa7`):**

| ID | Removed | Harness | Failed checks |
|---|---|---|---|
| MD-1 | the commit CHECK's `IS NOT NULL` terms (D-1) | A | 9/50: MG-07c, MX-01b..e, MX-02a..c, MX-03 (a legacy build committed a CARV-prepared operation) |
| MD-2 | exact equality, replaced by the drafted `COALESCE(C,0) >= COALESCE(P,0)` (D-3) | A | 4/50: MG-07c, MX-02a..c (the database accepted a CARV commit of a legacy-prepared operation) |
| MD-3 | the state guard's insert and terminal-row checks (D-5) | A | 2/50: MG-07d, MG-07e |
| MD-4 | the state-guard trigger | A | 6/50: MG-06, MG-07a, MG-07c..e, PF-04 |
| MD-5 | the floor's `Committed` scope (D-2; gate floor SQL) | A | 7/50: FL-02a..e, FL-04, PF-02 |
| MD-6 | the floor's `IS NOT NULL` term (D-1; gate floor SQL) | A | 11/50: FL-02a..e, FL-03, FL-04, PF-01, PF-01b, PF-02, PF-07 |
| MR-1 | the store adapter's refusal mapping | B | 2/36: RB-18, RB-19 |
| MR-2 | the readiness guard in host readiness | B | 3/36: RB-R0..R2 |
| MR-3 | the CARV Commit rule (database fence only) | B | 4/36: RB-03a..c, RB-16 |

**Finding from MR-3 (consistent with the approved design; recorded for review).** Without the application Commit rule, the database still refused the real build's commit of a legacy-prepared operation and nothing advanced; but the operation stayed `Prepared`, and a legacy instance then committed it before the floor (RB-03c). Old→old commits before the floor are the accepted R-CARV-1 path; the point is that the CARV **abort** is load-bearing before the floor: it is what stops a legacy instance from later committing an operation that a CARV build has already refused. With the rule in place, RB-03c shows the aborted operation can never be accepted.

**Binary reproducibility.** Release builds of this repository are not byte-reproducible: three builds of unchanged source gave three different hash sets, including for projects nobody changed. Binary hashes therefore cannot prove that a mutation was reverted; the per-file SHA-256 checks above are the proof. For the same reason the runs of record were repeated with a fresh build of the final source through the in-repository gate (§20.5).

### 20.7 Remaining risks

| ID | Risk | Disposition |
|---|---|---|
| R-CARV-1..5, -7, -8, -10 | As §19.11 | Unchanged |
| R-CARV-6 | Application rules untested | **Closed for Development/Testing**: the in-memory tests and harness B exercise the real rules |
| R-CARV-9 | Evidence used a database emulation | **Closed for Development/Testing**: harness B uses the real build |
| R-CARV-11 | F-17 (existing): if a record-creation retry reproduced the identical request digest under the same key, step 2 would replay an aborted operation and fail with an unmapped `OPERATION_STATE_INVALID`. Not reachable in practice (fresh row identities and ciphertext change the digest, giving `IDEMPOTENCY_CONFLICT`), and never an acceptance | **Deferred by owner decision (§20.9).** Open backlog item; not resolved. Rationale and regression evidence preserved (§20.9) |
| R-CARV-12 | The alert is an operational log entry (event 9100) and is not journaled on the audit stream; production SIEM routing is not configured | **Open release-gate question (§20.9):** before any deployment, determine whether mandatory Authority audit policy requires a durable audit-stream event in addition to the operational alert. Production routing is a deployment prerequisite |
| R-CARV-13 | Up to one recheck interval (30 s) may pass before an overtaken instance reports not ready | **Accepted by the owner (§20.9).** No security impact: the database refuses its writes immediately (RB-18/19) |
| R-CARV-14 | Legacy builds produced by the in-repository gate from `git archive` do not have the frozen builds' hashes (paths are embedded in the assemblies) | The evidence of record used the frozen builds; the gate records the hashes of whatever it builds |
| R-CARV-15 | "Other paths surface it as a retryable conflict" (§3.6) is realised as a 409 problem with `retryable: false` (the operation cannot be retried; the caller re-submits with a new idempotency key). No client consumes `retryable` today | **Accepted by the owner (§20.9)**; operation retry versus business-action retry documented there |
| — | Not covered: Stage B end-to-end business flows; production-scale lock contention (PB-09); Azure PostgreSQL behaviour; cross-host partitions | Release gates, unchanged |

### 20.8 Deployment prerequisites and repository checks

**Release-gate question carried forward:** R-CARV-12 (§20.9) must be answered before any deployment beyond Development/Testing.

**Before any deployment beyond Development/Testing:** owner acceptance of this record and a signed commit; PB-11/PB-12 Authority release approval; routing of alert `CONTENT_ACCEPTANCE_RULE_REFUSED_BY_DATABASE` and log event 9211 to monitoring; the §19.9 sequence (`docs/security/E3_M2B1_CARV_OPERATIONS.md`), with the floor (E3-M2b-1F) as a separate, separately approved release; the G14 runbook entries for R-CARV-4; Azure PostgreSQL verification of the fence and the lock behaviour.

**Repository checks:** builds of the Authority solution and every test project succeed with warnings as errors; no unfilled placeholder, local path or secret is in the changed files; the specification manifest was regenerated with `scripts/generate-spec-manifest.py` (required: the private OpenAPI reference, the DR runbook and new `docs/` and `scripts/` files are in its scope) and `scripts/validate-specifications.py --strict` passes. `docs/evidence/**` is outside the manifest by design.

### 20.9 Owner acceptance and decisions (2026-11-14)

The owner accepted E3-M2b-1 for Development/Testing on the reported implementation and test evidence, and authorized one signed local commit. Every residual risk documented earlier (R-M2a-1..6, R-CARV-1..15, the §19.11 list) is preserved.

| Risk | Owner decision |
|---|---|
| **R-CARV-15** | **Accepted.** `409 OPERATION_RULES_OUTDATED` carries `retryable: false` for the rejected operation. Recovery requires a new Prepare. |
| **R-CARV-11** (existing F-17) | **Deferral accepted.** The backlog entry stays open; it is **not** resolved. |
| **R-CARV-12** | **Open release-gate question.** Before deployment, determine whether mandatory Authority audit policy requires a durable event in addition to the operational alert `CONTENT_ACCEPTANCE_RULE_REFUSED_BY_DATABASE`. |
| **R-CARV-13** | **Accepted.** The readiness polling delay (at most one 30 s interval) is accepted because database enforcement is immediate. |

**Operation retry versus business-action retry (R-CARV-15).**

| Level | Meaning of retry | After `OPERATION_RULES_OUTDATED` |
|---|---|---|
| **Operation** (Authority content operation: one idempotency key, one operation id, one Prepare and its Commit) | Re-sending the same Prepare or Commit | **Never.** `retryable: false`. The operation is aborted (Commit) or never handed back (Prepare replay); re-sending it gives `OPERATION_RULES_OUTDATED`, `OPERATION_STATE_INVALID` or `IDEMPOTENCY_CONFLICT`, never acceptance. |
| **Business action** (the user's or worker's intent: create a record, save an edit, migrate a field) | Performing the action again | **Allowed**, as a new Prepare under a new idempotency key (a new operation, fresh row identities and data keys). Record creation does this itself (steps 1–3); the protection migration does it with the next attempt number; other write paths return the 409 to the caller, who re-submits as a new request. |

The Authority's `retryable` flag describes the operation level only; it does not mean the business action is forbidden.

**R-CARV-11 (F-17) backlog entry, preserved.** *Behaviour:* if a record-creation retry reproduced the identical canonical request digest under the same idempotency key, `CreateFormRecordHandler` step 2 would receive the existing (aborted) operation from the Prepare replay, and the subsequent Commit would fail with an unmapped `OPERATION_STATE_INVALID`. *Reachability:* not reachable in practice: every retry rebuilds the candidate with fresh row identities and fresh ciphertext, so the digest differs and the Authority answers `IDEMPOTENCY_CONFLICT`, which step 2 maps to step 3 (fresh retry key). *Safety:* never an acceptance; the aborted operation cannot commit (RB-03c; `ACommitOfAnOperationPreparedUnderOtherRulesIsAbortedBeforeAnyVerificationAndNothingIsAccepted` asserts `OPERATION_STATE_INVALID` on the second commit). *Regression evidence:* M2-CARV-08 (`ContentAcceptanceRuleBusinessMappingTests`) pins the step 1/2 code sets, and MA-8/MA-9 show they are load-bearing. *Status:* open, deferred; resolving it (mapping `OPERATION_STATE_INVALID` in step 2) needs a separate authorization.

---

## 21. E3-M2b-1F CARV database floor — implementation record (2026-11-14)

**Status.** Implemented for **Development/Testing only** on `e3/m1-envelope-v2-core` on top of the signed E3-M2b-1 commit `c44be41c45991e1247de37eb12e871a920182a05`; **owner-accepted for Development/Testing (2026-11-15, §21.10)** and committed as one signed local commit (not pushed). Release B is not activated; gate RB-CARV-1 is open. Owner-led work; not an independent review, not a certification. The floor was activated **only in disposable PostgreSQL containers**; no persistent database, Azure service or deployment was touched; Stage B did not run. Envelope v2, signed schema 2, encryption and key providers are unchanged; E3-M2b-2 was not started.

**Objective.** Close R-CARV-1: after controlled activation, no pre-CARV Authority build can prepare or commit a content operation.

### 21.1 What was implemented

| Area | Change |
|---|---|
| Floor migration | `20261115032228_AuthorityContentAcceptanceRuleFloor1` in `Persistence/FloorMigrations/`, belonging to the new model-less `ContentAcceptanceFloorContext` (§21.2). Up = `ContentAcceptanceFloorSql.UpSql`; Down always raises `floors are never lowered`. No existing migration was modified. |
| Floor SQL (`ContentAcceptanceFloorSql`) | One transaction: `SET LOCAL lock_timeout = '5s'`; `LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE`; preconditions under the lock (§21.3); then exactly the §19.5 floor `ck_content_operation_rule_floor_v1 CHECK (operation_state_name NOT IN ('Prepared', 'Committed') OR (prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 1)) NOT VALID`. |
| Operator command | `AuthorizationAuthority.Api.dll --apply-content-acceptance-floor 1 [--reinstate-recorded-floor]` (`ContentAcceptanceFloorCommand`): migration identity only, the floor number stated explicitly, refused together with `--migrate`. It migrates only the floor context, then verifies that the constraint exists (exit 4 if the history records it but it is missing; §21.6). |
| Detection (readiness, additive to E3-M2b-1) | `SchemaFacts.FloorRecorded` (history row of the floor migration); `ContentAcceptanceRules.Evaluate(..., requiredFloor)`; option `ContentAcceptance:RequiredFloor` (`ContentAcceptanceRuleOptions`; invalid values fail at startup). New not-ready reasons: `ContentAcceptanceRuleFloorMissing`, `ContentAcceptanceRuleFloorBelowRequired`, `ContentAcceptanceRuleRequiredFloorAboveBuild`. Every E3-M2b-1 decision and detail string is unchanged. |
| Unchanged | The E3-M2b-1 migration, its CHECKs and state guard, the Prepare/Commit rules, the store mapping (floor refusals already map to `409 OPERATION_RULES_OUTDATED`), the business mapping, the ordinary `--migrate`. |
| Gate | Harness F (`tools/experiments/e3-m2b1-carv-gate/HarnessF`) added to `scripts/dev/e3-m2b1-carv-pg-gate.ps1`; the legacy agent gained an optional clock offset (default 0, so harness B is unaffected); the gate passes `--context AuthorityDbContext` to `dotnet ef` (two contexts now exist in the assembly). |
| Documents | `docs/security/E3_M2B1_CARV_OPERATIONS.md` (Release B procedure, readiness, recovery, rollback); `trust_authority/DISASTER_RECOVERY_OPERATOR_RUNBOOK.md` (the re-apply command); evidence `docs/evidence/e3-m2b1-carv/floor/`. |

### 21.2 Release A / Release B separation (the activation mechanism)

| Property | How it is guaranteed | Proof |
|---|---|---|
| The ordinary migration runner can never apply the floor | EF discovers migrations per context by their `[DbContext]` attribute. The floor migration is attributed to `ContentAcceptanceFloorContext`; `--migrate`, the host and `dotnet ef` for the Authority run `AuthorityDbContext`, which cannot see it. This is structural and independent of which build is deployed. | `TheOrdinaryAuthorityMigrationSetNeverContainsTheFloor`, `TheFloorContextOwnsExactlyTheFloorMigrationAndSharesTheAuthorityHistoryTable`; real PostgreSQL FX-02/FX-03 (a build that contains the floor, migrated twice with `--migrate`: E3-M2b-1 applied, floor never); FX-22 (`--migrate` after activation keeps the floor) |
| Activation is an explicit, separate action | Only `--apply-content-acceptance-floor 1`; refused with `--migrate` or without the explicit floor number | `TheFloorCommandRefusesAnUnstatedFloorOrACombinationWithMigrate` (5 cases) |
| Activation fails closed before every instance is upgraded and in-flight work is reconciled | Preconditions inside the locked transaction (§21.3) | FX-01, FX-06, FX-08, FX-12a |
| One history table | The floor context uses `authority.ef_migrations_history`, which the runtime role already reads, so readiness can see that a floor was recorded (no new grant) | FX-25 |

### 21.3 Activation preconditions (inside the floor transaction, after the ACCESS EXCLUSIVE lock)

1. The E3-M2b-1 fence is complete: its migration is recorded, the commit CHECK is present and validated, the state guard is enabled.
2. No floor is installed yet.
3. No **unexpired** `Prepared` operation with a NULL Prepare stamp (a pre-CARV build's operation still in flight). Reconcile by expiry or by letting a CARV instance abort it (FX-06/07).
4. No connected client session of a non-superuser role that may `UPDATE` `content_change_operations`, other than the migration identity, whose `application_name` lacks a `carv-v` marker (the LISTEN-only `authority-work-listener` excepted). A session whose name is not visible counts as unmarked (fail closed).

Because they run under the ACCESS EXCLUSIVE lock, no operation write can happen between the checks and the floor: any writer either finished before the lock or waits and is then checked against the floor (FL-01..FL-06 for the constraint; FX-12a with a legacy Prepare in progress). The preconditions are availability and correctness guards; **security does not depend on them**: once the floor exists, the database refuses every pre-CARV Prepare and Commit whatever happened before.

### 21.4 Real-PostgreSQL results (runs of record)

`scripts/dev/e3-m2b1-carv-pg-gate.ps1 -LegacyBinariesRoot <frozen builds>`: a fresh Release build of this tree, the frozen `66f0aa7` and `de1729b` builds (SHA-256 equal to the design-gate evidence), a fresh `postgres:18.0` container per run. Evidence: `docs/evidence/e3-m2b1-carv/floor/runs-of-record/`.

| Run | 66f0aa7 | de1729b |
|---|---|---|
| Harness A (E3-M2b-1 regression: 47 design-gate checks + RM-01..03) | 50/50 | 50/50 |
| Harness B (E3-M2b-1 regression: the real CARV build) | 36/36 | 36/36 |
| **Harness F (the floor)** | **30/30** | **30/30** |

**Owner proofs (authorization §4):**

| # | Requirement | Evidence (both legacy builds) |
|---|---|---|
| 1 | Legacy Prepare refused after the floor | FX-14 (23514 `ck_content_operation_rule_floor_v1`, nothing persisted, legacy host 503), FX-27 (after re-application) |
| 2 | Legacy Commit refused after the floor | FX-15: a legacy instance whose clock lags the database by 11 minutes commits its pre-floor operation after activation; refused by the floor, snapshot unchanged |
| 3 | Legacy-to-legacy cannot reach acceptance | FX-14 + FX-15 + FX-16 (no Committed row with a NULL stamp after activation). Before the floor the same path is accepted (FX-04: the R-CARV-1 exposure this closes) |
| 4 | Current CARV operations work | FX-12 (4 instances writing concurrently during activation), FX-17, FX-21 |
| 5 | Version mismatches rejected | FX-18 (forged unstamped or zero-stamped Prepared rows; a commit at another rule version), FX-19 (an operation stamped by a newer rule version is aborted by the v1 Commit rule) |
| 6 | Historical operations cannot bypass the floor | FX-20 (a Committed row cannot be reopened, an Aborted row cannot be revived, and even a superuser update of an unstamped Prepared row violates the floor), FX-15 |
| 7 | No rejected operation advances head, manifest, leaves, identities, journal or checkpoint | FX-15 snapshot equality; FX-14 no persisted operation; the E3-M2b-1 snapshot checks in A and B |
| 8 | Concurrency during activation | FX-12 (concurrent CARV writers wait out the lock and succeed), FX-12a (a legacy Prepare in progress makes activation refuse; it lands pre-floor and is reconciled, FX-12b), harness A FL-01..FL-06 (the identical constraint under held transactions) |
| 9 | Failure or lock timeout leaves no partial floor | FX-09 (55P03), FX-10 (backend terminated while waiting), FX-01/06/08 (refusals): no constraint and no history row each time |
| 10 | Re-running is safe | FX-22 (no-op, one history row), FX-26 (re-applied after a simulated restore) |

### 21.5 Concurrency and atomicity

The serialization point is the floor transaction's ACCESS EXCLUSIVE lock, taken before the preconditions (§19.4 still holds: every operation write is wholly before or wholly after the floor commit). A lock timeout, a terminated backend or any refusal rolls the whole migration back, including EF's history row (FX-01, -06, -08, -09, -10). Concurrent CARV writers simply wait (FX-12: 24 writes, no unexpected outcome).

### 21.6 Recovery and rollback (tested)

| Case | Finding |
|---|---|
| Interrupted activation | Nothing applied; re-run (FX-09/10) |
| Constraint dropped, history row present | Readiness `ContentAcceptanceRuleFloorMissing`; the command exits 4; `--reinstate-recorded-floor` re-installs it under the same preconditions (FX-25) |
| Restore to before the floor (constraint and history gone) | **Undetectable from the database alone** (it looks like Release A); detected by readiness when `ContentAcceptance:RequiredFloor = 1` is configured; the command re-applies the floor (FX-26). While it is missing, pre-CARV builds can write again: during development a harness defect left the floor un-reapplied and a legacy Prepare was accepted, which is exactly this exposure |
| Lowering or removal | The floor's Down refuses; the E3-M2b-1 Down refuses while a floor exists; the runtime role cannot drop it (FX-23). No downgrade mechanism exists |
| Legacy binary rollback after activation | Impossible by design: every legacy Prepare is refused (FX-14) and the legacy host reports 503. Roll forward only |
| Operator actions before reopening writes | Confirm `floor 1` on every instance (readiness with `RequiredFloor = 1`); re-apply the floor after any restore; treat any `FloorMissing` as an incident (operations runbook §6) |

### 21.7 Enforcement-removal checks

Each removal was applied alone, built and tested, then reverted; every file's SHA-256 matched its backup afterwards.

**Application level (Authority tests):**

| ID | Removed | Detected by |
|---|---|---|
| MF-1 | the separation (floor migration attributed to `AuthorityDbContext`) | 2 tests (`TheOrdinaryAuthorityMigrationSetNeverContainsTheFloor`, `TheFloorContextOwns...`) |
| MF-2 | readiness on a recorded-but-missing floor | 1 test |
| MF-3 | readiness on the required floor | 2 tests |
| MF-4 | the refusal of `--migrate` in the same invocation | **Not detected at first**: the test cases supplied no connection string, so the command still exited 2, for a different reason. The tests now inject a (non-connectable) configuration, so exit 2 can only come from the refusal, with a positive control (exit 3). Re-run: **detected by 2 tests** |
| MF-5 | the floor Down's refusal (replaced by a DROP) | 1 test |
| MF-6 | the lock before the preconditions | 1 test (the only detection; R-CARV-19) |

**Real PostgreSQL (mutated Release build, harness F, legacy `66f0aa7`):**

| ID | Removed | Failed checks |
|---|---|---|
| MP-1 | the separation | FX-01..03 and the harness aborts: the ordinary `--migrate` installed the floor (FX-02 `floor=1`), i.e. premature activation in Release A |
| MP-2 | the in-flight pre-CARV operation precondition | FX-06, FX-08..10 (activation succeeded with a legacy operation in flight) |
| MP-3 | the unmarked-session precondition | FX-08..10 (activation succeeded with a pre-CARV session connected) |
| MP-4 | the floor's `Committed` scope (D-2) | FX-13, **FX-15: the lagging legacy instance's Commit of its pre-floor operation was accepted (the head advanced)**, FX-20 |
| MP-5 | the floor's `IS NOT NULL` term (D-1) | 8 checks: FX-13..15, -18, -20, -25..27 (legacy Prepare and Commit accepted) |
| MP-6 | the floor Down's refusal | FX-23..25 (the Down removed the floor) |
| MP-7 | the command's check that the recorded floor exists | FX-25 |

### 21.8 Test results

| Suite | Result |
|---|---|
| Authority (full) | 724 total: **720 passed, 0 failed, 4 skipped** (real-Vault classes). Before E3-M2b-1F: 704. +20 new tests (`ContentAcceptanceFloorTests`) |
| Contract (full) | 792 total: **783 passed, 0 failed, 9 skipped** (real-Vault classes) |
| Architecture | **124/124** |
| Integrity | **60/60** |
| Real PostgreSQL | §21.4: A 50/50 ×2, B 36/36 ×2, F 30/30 ×2 |
| Specification manifest | regenerated (the operations runbook, the DR runbook, the gate script and this contract changed); `validate-specifications.py --strict` passes |
| Stage B | **Not run** |

### 21.9 Residual risks

| ID | Risk | Disposition |
|---|---|---|
| R-CARV-1 | Legacy builds committing under old rules | **Closed after activation** (Development/Testing evidence), as long as the floor stays installed. Reopens while the floor is missing (restore, privileged drop): R-CARV-4/-17 |
| R-CARV-4 | A restore removes the floor | Mitigated: re-apply command, `RequiredFloor` detection, runbook entries. Still an operator duty |
| R-CARV-16 | The session precondition trusts a self-declared `application_name` marker and ignores the LISTEN-only connection name; a pre-CARV instance holding no store connection at the moment of activation is not seen | **Accepted by the owner (§21.10)** with this limitation. Availability guard only: after activation such an instance is refused by the database anyway |
| R-CARV-17 | Without `ContentAcceptance:RequiredFloor`, a restore to before the floor is not detected by readiness | **Mandatory Release B gate RB-CARV-1 (§21.10)**: protected deployment configuration and fail-closed writes. Open |
| R-CARV-18 | `pg_stat_activity` visibility for a non-superuser migration identity (Azure) is unverified; invisible names count as unmarked, so activation could be refused | **Accepted by the owner (§21.10)** with this limitation. Fail closed; verify on Azure PostgreSQL |
| R-CARV-19 | The lock-before-preconditions order is enforced by a unit test only; no PostgreSQL test fails if it is removed, because the floor itself still refuses later legacy writes | **Retained (§21.10)**: recommendation to add stronger PostgreSQL regression evidence before Release B. Defence in depth; documented |
| R-CARV-20 | Two DbContexts in the Authority Infrastructure assembly: `dotnet ef` now needs `--context` | **Accepted by the owner (§21.10)**. Developer workflow change; the gate is updated |
| R-CARV-5, -11, -12, -13 and the rest | As before | Unchanged (R-CARV-12 remains an open release-gate question; R-CARV-11/F-17 remains open) |

**Deployment prerequisites (Release B):** owner approval of this record (given for Development/Testing, §21.10); PB-11/PB-12 approvals; Release A fully rolled out and pre-CARV instances drained (deployment inventory plus the `carv-v` marker check); in-flight pre-CARV operations reconciled; a low-traffic window; **gate RB-CARV-1 satisfied (§21.10)**; verification on Azure PostgreSQL (R-CARV-18); recommended: PostgreSQL regression evidence for the lock order (R-CARV-19).

### 21.10 Owner acceptance and the mandatory Release B safeguard (2026-11-15)

**Owner acceptance (Development/Testing).** The owner accepted the E3-M2b-1F implementation on the reported implementation and test evidence and authorized one signed local commit. Owner-led review; not an independent review, not a certification, not a Release B activation.

| # | Owner decision |
|---|---|
| 1 | The separate `ContentAcceptanceFloorContext` and the dedicated activation command `--apply-content-acceptance-floor 1` are approved (§21.2) |
| 2 | The ACCESS EXCLUSIVE table lock before the activation preconditions is approved (§21.3, §21.5) |
| 3 | The additive readiness detection of a missing or lowered floor is approved (§21.1) |
| 4 | The optional clock offset in the test-only legacy agent is approved (§21.1 Gate) |
| 5 | R-CARV-16, R-CARV-18 and R-CARV-20 are accepted with their documented limitations (§21.9) |
| 6 | R-CARV-19 is retained as a recommendation for stronger PostgreSQL regression evidence before Release B |
| 7 | R-CARV-11 and R-CARV-12 remain open risks |

**Mandatory Release B gate RB-CARV-1 (open).** This is a release requirement. It is **not** a claim that the current implementation already satisfies it or has been proven in any deployment configuration.

1. **Protected configuration.** After floor activation, `ContentAcceptance:RequiredFloor = 1` must be enforced through protected deployment configuration on every Authority instance. Business deployers and the Authority runtime must not be able to remove or lower it (PB-11 separately permissioned Authority pipeline), and changes must be audited. A Release B deployment without the setting fails the release gate (PB-12 evidence).
2. **Fail closed.** A Release B Authority instance must refuse Authority writes when its required floor is absent, lowered, or cannot be verified.
3. **Restore.** After a restore of AuthorityDb to a pre-floor state, Authority writes must not resume until the floor has been safely reinstated (`--apply-content-acceptance-floor 1` with the migration identity) and every instance reports `floor 1`.
4. **Evidence.** Executed tests in Azure staging, linked through the PB-12 chain, for items 1–3.

**What exists today (Development/Testing) and what does not:**

| Item | Status |
|---|---|
| Detection of an absent, lowered or recorded-but-missing floor when `RequiredFloor` is set | **Implemented and tested**: not-ready `ContentAcceptanceRuleFloorBelowRequired` / `ContentAcceptanceRuleFloorMissing` (unit tests; FX-25/26) |
| Floor that cannot be verified | **Implemented**: a failed schema read, a decision never made, or a decision older than `MaximumDecisionAge` is not ready |
| Invalid setting (below 1) | **Implemented**: startup fails |
| Write refusal on a not-ready decision | **Not implemented.** The guard feeds Authority readiness only; no write path consults it. Writes stop only if the deployment removes a not-ready instance from every write path and the organization gates stay closed. Neither is proven for any deployment configuration |
| Protected delivery of `RequiredFloor` | **Not implemented.** It is ordinary configuration today; an absent setting silently means Release A semantics (R-CARV-17) |
| Azure staging evidence | **None** |

Satisfying item 2 therefore needs either a separately authorized production change that makes Authority writes refuse on the guard's not-ready decision (the recommended, stronger option), or proven deployment wiring that withdraws a not-ready instance from all write traffic, including internal callers and background work. No production code was changed for this record.

**Update (2026-11-15, §22):** the owner authorized the RB-CARV-1 hardening for Development/Testing. Items 1–3 are now implemented at the application and database level (a mandatory Release A / Release B policy and a transactional write fence that does not depend on readiness; §22). The deployment protection of the configuration (item 1, PB-11) and the Azure evidence (item 4) remain open, so RB-CARV-1 is **not closed for production**. The table above is kept as the record of the state at the E3-M2b-1F commit.

## 22. RB-CARV-1 Release B write fence — implementation record (2026-11-15)

**Status.** Implemented for **Development/Testing only** on `e3/m1-envelope-v2-core` on top of the signed E3-M2b-1F commit `8c6367b25b74cef248481c41dac8ef0f189f7e6d`; **conditionally owner-accepted for Development/Testing (2026-11-15, §22.13)** and committed as one signed local commit (not pushed). Not production-closed. Owner-led work; not an independent review, not a certification. The floor was activated, removed, weakened and restored **only in disposable PostgreSQL containers**. No persistent database, Azure service or deployment was touched; Stage B did not run. Envelope v2, signed schema 2, encryption and key providers are unchanged; E3-M2b-2 was not started.

**Objective.** Close the gap left by E3-M2b-1F (§21.10): readiness detected a missing floor but no write path enforced it, and an omitted `ContentAcceptance:RequiredFloor` silently meant Release A. Invariant: once Release B is required, no Authority content operation may prepare, commit, replay or otherwise advance authoritative content state unless the required floor is verified and enforced, in the same transaction, whatever readiness says.

### 22.1 What was implemented

| Area | Change |
|---|---|
| Policy (`ContentAcceptancePolicy`, Application) | The deployment phase `ContentAcceptance:EnforcementPhase` (`ReleaseA` or `ReleaseB`, exact ordinal match) and `ContentAcceptance:RequiredFloor` (Release B: exactly `1`; Release A: must be absent). No default: absent, blank, `Unconfigured`, unknown, malformed, below 1 (a downgrade), above this build's floor, or a floor in Release A are each an invalid policy. `Resolve` never throws and never falls back. Replaces the E3-M2b-1F `ContentAcceptanceRuleOptions` (§22.12) |
| Composition (Api) | `AddContentAcceptancePolicy` resolves the policy once from the host's final configuration (a singleton; nothing re-reads it, so a configuration reload cannot change a running instance). `ContentAcceptancePolicyStartupGuard` refuses the host start on an invalid policy (not during build-time OpenAPI generation) and logs the resolved phase (event 9213; 9214 when invalid) |
| Settings | `appsettings.json`: `ContentAcceptance:EnforcementPhase = Unconfigured` (the explicit fail value, like `Signing:Provider`); `appsettings.Development.json`: `ReleaseA`; the local Docker stacks set `ContentAcceptance__EnforcementPhase: ReleaseA` in `infrastructure/docker/compose.yaml` (the security-test stack runs `Testing`, which has no settings file). Staging and Production supply it from deployment configuration |
| Write fence (`ContentAcceptanceWriteFence`, Infrastructure) | `LOCK TABLE authority.content_change_operations IN ROW EXCLUSIVE MODE`, then one catalog query in the same transaction, evaluated against the exact PostgreSQL 18 definitions of the floor and the E3-M2b-1 fence (§22.5). Pure evaluator with fixed reason order |
| Store (`AuthorityStore`) | Takes the policy as a **required** constructor argument (no construction can silently be Release A). New port method `IAuthorityScope.RequireContentAcceptanceFloorAsync`: Release A no-op; Release B (or an invalid policy) runs the fence once per transaction inside a savepoint (so a refused caller can still record its refusal) and keeps the lock to commit. **Catch-all:** before any `SaveChanges` that would write content-acceptance state, the store runs the same fence itself (§22.3) |
| Entry calls | `ContentService.PrepareAsync` and `CommitAsync` call the fence right after the organization lock, before every replay branch; `SecurityCommandService.SubmitAsync` calls it before staging a plan that registers a record (its empty accepted head) |
| Readiness | `ContentAcceptanceRules.EvaluateForPolicy`: Release A and the E3-M2b-1 tests are exactly the previous decision; Release B adds the required floor and the write fence verdict (the reader runs the same fence query, contained so Release A readiness can never depend on it). The Release B ready detail names the phase: `content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1` |
| Error | `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED`, 503, not retryable, generic message (no database internals). Never `OPERATION_RULES_OUTDATED` or another content conflict. The business has no mapping for it, so it surfaces as an ordinary non-retryable Authority failure |
| Unchanged | The E3-M2b-1 and E3-M2b-1F migrations and SQL, the Prepare/Commit rules (P = V, C = P), the state guard, the store's refusal mapping and alert, the floor command and its preconditions, the business mappings, Envelope v2, signed schema 2 |
| Gate | Harness R (`tools/experiments/e3-m2b1-carv-gate/HarnessR`, scenarios A–H) added to `scripts/dev/e3-m2b1-carv-pg-gate.ps1`; its container alone runs with `track_commit_timestamp=on`. Harnesses B and F construct the store with a Release A policy (their behaviour is unchanged); F's FX-24 expects the new Release B ready detail (§22.12) |

### 22.2 Release A / Release B configuration model

| Phase | Configuration | Behaviour |
|---|---|---|
| Release A | `EnforcementPhase = ReleaseA`, no `RequiredFloor` | CARV builds as committed in E3-M2b-1; no write fence; the floor may or may not be installed (activation happens while instances are still Release A). Readiness and its detail strings are exactly as before |
| Release B | `EnforcementPhase = ReleaseB`, `RequiredFloor = 1` | Every content write transaction verifies floor 1 and the E3-M2b-1 fence first, in the transaction, or refuses. Readiness additionally requires the floor and the write fence verdict |
| Invalid | anything else, including a missing value | The host refuses to start; if a store is constructed anyway (tests, tools), every content write is refused (`ConfigurationPolicyInvalid`). Never Release A |

- **Source of Release B intent:** deployment configuration only (environment or deployment-supplied settings), never the database, an organization setting or an API. A restored AuthorityDb therefore cannot change it (RX-D1).
- **Downgrade restrictions:** a lower floor, a malformed value or a phase spelled differently is refused at startup; the value is read once, so a configuration reload has no effect; no API reads or writes it. Changing the deployment from Release B back to Release A is a redeployment that the application cannot distinguish from a legitimate Release A deployment: preventing it is the PB-11 deployment control (R-CARV-22).
- **Observability:** startup log event 9213 (`phase=ReleaseB requiredFloor=1`), the Release B readiness detail, and every refusal alert name the phase; no secret is involved.
- **What is not guaranteed:** that the setting is tamper-proof. An environment variable or application setting is only as protected as the pipeline and identities that can change it; the repository has no such mechanism for the Authority (no Authority Bicep) and none was invented. **Deployment control still required:** deliver `ContentAcceptance__EnforcementPhase` and `ContentAcceptance__RequiredFloor` from the separately permissioned Authority release pipeline (PB-11), unchangeable by business deployers and the Authority runtime, with audited changes and PB-12 evidence (RB-CARV-1 item 1).
- **Rollout order:** activate the floor while every instance is Release A, verify it (`floor 1`, verdict `none`), then deploy the Release B configuration. A Release B instance started before activation refuses every content write (fail closed: RX-A3).

### 22.3 Protected content-write boundaries

| Boundary | Mechanism | Evidence |
|---|---|---|
| Prepare (new operation) | Entry call after the organization lock, before any reads, signing or writes | unit; RX-A3, RX-G1, RX-D1 |
| Prepare replay (idempotent retry) | The same entry call precedes the replay branch | unit; RX-C1 |
| Commit (in-flight operation) | Entry call after the organization lock, before the replay, expiry, CARV and verification branches; the operation stays Prepared (not aborted) | unit; RX-C1, RX-C5 |
| Commit replay (already committed) | The same entry call precedes the replay | unit; RX-C1 |
| Commit-time aborts (expired, outdated rules, revoked, envelope, locked) | Reached only after the entry call succeeded; while the floor is missing no Commit proceeds at all | RX-C1 |
| Record registration (security command creating the empty accepted head) | Entry call before the plan is staged; refusal recorded as an ordinary rejected command, no revision consumed; commands that register no record never ask | unit (3 tests) |
| Any other path that stages content-acceptance state | Store catch-all before `SaveChanges`: operations (insert, or any change other than a Prepared→Aborted abort), content heads (insert, or any change other than quarantining), manifest commits, manifest leaves, item identities, candidate leaves, content checkpoints | RX-S1, RX-S2 |
| Protective changes (exempt on purpose) | An abort (state and abort reason only) and a head quarantine (quarantine fields and modification time only) still commit while the floor is missing: they move away from acceptance | RX-S3 |
| Background work and maintenance | No background job prepares or commits content: the integrity-incident paths (W2/W3/W4) only quarantine heads and journal incidents (exempt), and W4 runs inside Prepare after the entry call; search projections are derived, not authoritative content | code inspection (§22.11) |
| Writes outside this build | Legacy binaries, a Release A-configured instance, and direct SQL are **not** bound by this build's fence; only the database floor stops them | RX-C3, RX-D2 (R-CARV-21) |

### 22.4 Transactional enforcement and race safety

1. Write transactions run READ COMMITTED (unchanged). The fence takes ROW EXCLUSIVE on `content_change_operations` first and keeps it to commit or rollback.
2. Every statement that can remove, replace or weaken what the fence checks needs a lock that conflicts with ROW EXCLUSIVE: `ALTER TABLE … DROP/ADD CONSTRAINT`, `ALTER COLUMN … SET DEFAULT`, `DISABLE/ENABLE TRIGGER`, `DROP/CREATE TRIGGER`, `CREATE/DROP RULE`, and the floor migration (ACCESS EXCLUSIVE). None can commit between the verification and this transaction's commit. Deterministic proof RX-H1: with a verified writer open, a privileged `DROP CONSTRAINT` times out (`55P03`) and the floor stays.
3. The facts are read by a later statement, so its READ COMMITTED snapshot includes every catalog change committed before the lock was granted, which is also the definition PostgreSQL enforces on this transaction's later writes (the relation cache is refreshed when the lock is acquired).
4. A writer that finished before a removal committed was verified against the floor that existed then; a writer that starts or waits after it sees the removal and refuses. Concurrency proof RX-H2: four Release B instances writing while the floor is dropped — every accepted operation's commit timestamp precedes the drop's commit timestamp, and every later write was refused.
5. A floor activation or reinstatement (ACCESS EXCLUSIVE, 5 s lock timeout) waits for in-flight writers or times out without applying anything; it never deadlocks with them (the writers' other lock, the organization row, is not taken by the floor transaction).
6. **Not serialized:** `CREATE OR REPLACE FUNCTION` of the state-guard function takes no table lock (the fence verifies its source at every write, and RX-C9 shows the refusal, but a replacement committed during an open write is not excluded); a superuser editing the catalogs directly (R-CARV-24). The floor CHECK itself cannot be changed without ACCESS EXCLUSIVE.

### 22.5 What the fence verifies (in order; the first failure is the reason)

1. The E3-M2b-1 migration is recorded in `authority.ef_migrations_history` (`FenceMigrationNotRecorded`).
2. `ck_content_operation_commit_rule_version` and `ck_content_operation_rule_versions_positive` exist, are validated and render exactly as created (`FenceCommitCheckMismatch`, `FencePositiveCheckMismatch`).
3. `trg_content_operation_rule_state_guard` renders exactly as created (BEFORE INSERT OR UPDATE, FOR EACH ROW, the expected function), is enabled (`O` or `A`), and its function is plpgsql, not SECURITY DEFINER, with exactly the migration's body (line endings normalized) (`FenceStateGuardMismatch`).
4. Neither stamp column has a default or is generated (`FenceStampDefaultPresent`: RX-C13 shows a default would let a legacy Prepare pass the floor with a stamp it never applied).
5. No other non-internal trigger, no rewrite rule, no inheritance child, and the table is a plain table (`FenceUnexpectedTableObject`).
6. No floor above this build (`FloorAboveBuild`).
7. `ck_content_operation_rule_floor_v1` exists (otherwise `FloorLowered` if only a lower floor exists, `FloorRecordedButMissing` if its history row exists, else `FloorNotInstalled`) and renders exactly as the floor migration creates it, NOT VALID or validated (`FloorDefinitionMismatch`).
8. The floor migration is recorded (`FloorNotRecorded`).
9. Any failure to read (permission, connection, timeout) is `FloorUnverifiable`.

The expected texts are PostgreSQL 18.0's `pg_get_constraintdef`/`pg_get_triggerdef`/`prosrc` output, captured from the disposable container and verified live by RX-B1 (`verdict none`). A server that renders them differently refuses Release B writes until the constants are reviewed (R-CARV-23).

### 22.6 Failure behaviour and diagnostics

- The caller receives only `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (503, `retryable: false`). Nothing of the operation is written: the refusal happens before any staged state, and the transaction rolls back (every RX refusal check compares operations, head, manifests, leaves, identities, checkpoints and journal before and after).
- Each refusal raises the operational alert `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (Critical) with `reason`, `category`, `recovery`, the policy and the build version, and no secrets or content. Categories: `MissingFloor`, `LoweredFloor`, `WeakenedFence`, `InconsistentMigrationHistory`, `UnverifiableFloor`, `ConfigurationPolicyFailure`, `BuildBelowFloor`; recovery actions: `RecoveryRequired:ApplyFloor`, `RecoveryRequired:ReinstateRecordedFloor`, `RecoveryRequired:Incident`, `CheckAuthorityDbReachabilityAndPrivileges`, `CorrectDeploymentConfiguration`, `DeployBuildAtOrAboveFloor`.
- Release B readiness reports `ContentAcceptanceFloorNotEnforced(reason=…, category=…)` or `ContentAcceptancePolicyInvalid(…)`; the existing E3-M2b-1/1F reasons and alerts are unchanged.
- **R-CARV-12 is not closed:** the alert is operational, like the E3-M2b-1 refusal alert; whether mandatory audit policy also requires a durable audit-stream event remains the open release-gate question.

### 22.7 Recovery-command isolation

The floor command (`--apply-content-acceptance-floor 1 [--reinstate-recorded-floor]`) runs before the host is composed, with the migration identity and its own floor context; it never constructs a store and never passes through the write fence, so there is no circular dependency (RX-C4, RX-S4, RX-H3, RX-D3 apply or reinstate the floor while the Release B instance is refusing). Inspection uses the readiness detail and the fence query with the runtime role's existing read privileges. No new privilege, endpoint or bypass was added; the runtime role still cannot drop or alter the floor (FX-23).

### 22.8 Real-PostgreSQL results

`scripts/dev/e3-m2b1-carv-pg-gate.ps1 -LegacyBinariesRoot <frozen builds>`: a fresh Release build of the final working tree (`runs-of-record/new-sha256.txt`), the frozen `66f0aa7` and `de1729b` builds (hashes equal to the design-gate evidence; never modified), a fresh `postgres:18.0` container per run. Evidence: `docs/evidence/e3-m2b1-carv/rb-carv-1/`.

| Run | 66f0aa7 | de1729b |
|---|---|---|
| Harness A (E3-M2b-1 regression) | 50/50 | 50/50 |
| Harness B (E3-M2b-1 regression, real CARV build) | 36/36 | 36/36 |
| Harness F (E3-M2b-1F floor) | 30/30 | 30/30 |
| **Harness R (RB-CARV-1)** | **42/42** | **42/42** |

| Owner scenario | Harness R checks |
|---|---|
| A. Release A, no floor: CARV operations work; legacy compatibility unchanged | RX-A1 (CARV 1/1 Committed; legacy NULL/NULL Committed), RX-A2 (Release A readiness detail unchanged) |
| B. Release B, floor 1: CARV works; legacy Prepare and Commit fail | RX-B1 (live verdict `none`: the expected definitions match PostgreSQL 18.0), RX-B2, RX-B3, RX-B4 (lagging-clock legacy Commit refused, nothing advances), RX-B5 |
| C. Floor removed: new writes and in-flight commits refused; reinstated through the approved command; writes resume only after verification | RX-C1 (in-flight Commit, Prepare replay, Commit replay all refused; operation stays Prepared; nothing advances), RX-C2, RX-C3, RX-C4, RX-C5; weakened variants RX-C6..C13 (floor replaced twice, state guard disabled, state guard function replaced, commit CHECK dropped, extra trigger, rewrite rule, stamp default) |
| D. Restore of the pre-floor database | RX-D1 (the running Release B instance refuses, `FloorNotInstalled`; readiness not ready), RX-D2 (a Release A-configured instance and a legacy build write again: R-CARV-21), RX-D3 (floor re-applied by the command; writes resume; legacy refused again) |
| E. Missing, malformed, unsupported or downgraded configuration | RX-E1 (7 invalid policies refuse every write while the floor is fully enforced); host start: contract tests `AuthorityContentAcceptancePolicyStartupTests` (14 cases) |
| F. Metadata unavailable, connection interrupted, inconsistent history | RX-F1 (history SELECT revoked), RX-F2 (`pg_constraint` SELECT revoked), RX-F3/F4 (history rows deleted), RX-F5 (backend terminated while waiting for the fence lock) |
| G. Long-running instance | RX-G1: the same instance object throughout; the very next write after the removal is refused while its cached readiness still says ready |
| H. Concurrent floor removal | RX-H1 (a privileged DROP times out while a verified writer is open), RX-H2 (4 instances: 0 of 13 and 0 of 12 accepted operations committed after the drop committed; 25 and 26 refused; no unexpected outcome), RX-H3 |
| Secondary paths | RX-S1/S2 (direct head insert and head advance through a store scope without an explicit call: refused by the catch-all), RX-S3 (a protective abort still commits), RX-S4 |

**Legacy-binary verification (owner §8):** harnesses A, B and F re-ran unchanged against both frozen builds (the legacy Prepare and Commit refusals after activation, the historical-operation restrictions, P = V / C = P, the cross-version replay refusals and the non-advancement checks all still hold), and harness R repeats the legacy refusals under a Release B instance (RX-B3, RX-B4, RX-D3).

### 22.9 Enforcement-removal checks

Each removal was applied alone, built and tested, then reverted; every file's SHA-256 matched its backup afterwards and the final rebuild succeeded (`mutations/rb-carv-1-removals.json`).

| ID | Removed | Detected by |
|---|---|---|
| MU-1 | the Prepare entry call | 3 Authority tests |
| MU-2 | the Commit entry call | 3 Authority tests |
| MU-3 | the record-registration entry call (secondary path) | 2 Authority tests |
| MU-4 | configuration validation: a missing phase falls back to Release A | 4 Authority tests |
| MU-5 | configuration validation: a lower required floor (downgrade) accepted | 1 Authority test |
| MU-6 | floor-integrity verification: a replaced floor definition accepted | 2 Authority tests |
| MU-7 | fence verification: a stamp default accepted | 1 Authority test |
| MP-1 | the write-path guard (the store fence is a no-op) | harness R: 19 checks, then the harness aborts (RX-A3: a Release B write before activation committed) |
| MP-2 | secondary-path coverage (the store catch-all never runs) | RX-S1, RX-S2 |
| MP-3 | transactional protection (no table lock before the verification) | RX-H1 (the privileged DROP succeeded during a verified write), then the harness aborts |
| MP-4 | floor-integrity verification (real PostgreSQL) | RX-C6, RX-C7 |
| MP-5 | the Prepare entry call (the catch-all alone remains) | RX-C1 (the Prepare replay returned an operation while the floor was missing) |
| MP-6 | configuration validation: a missing phase falls back to Release A (real PostgreSQL) | RX-E1 |

**Narrower evidence for MP-1 and MP-3 (owner acceptance, §22.13).** Both removals were detected, but each run ended early: after the first detecting checks the harness aborted because a later step's precondition no longer held (MP-1: a write committed that should have been refused, so the following steps met unexpected state; MP-3: the privileged `DROP` succeeded during the verified write, so the RX-H2 drop hit SQLSTATE 42704). Their later checks therefore did not run against the mutated build. These two results show that the removal is noticed; they are not a complete assertion of the intended invariant under each removal, and are narrower evidence than the removals whose detecting checks ran to completion.

### 22.10 Test results

| Suite | Result |
|---|---|
| Authority (full) | 783 total: **779 passed, 0 failed, 4 skipped** (real-Vault classes). Before RB-CARV-1: 724. +59 tests (`ContentAcceptancePolicyTests`, `ContentAcceptanceRegistrationTests`, `ContributorWriteProtocolTests.ContentAcceptanceFloor`) |
| Contract (full) | 806 total: **797 passed, 0 failed, 9 skipped** (real-Vault classes). +14 (`AuthorityContentAcceptancePolicyStartupTests`) |
| Architecture | **124/124** |
| Integrity | **60/60** |
| Real PostgreSQL | §22.8: A 50/50 ×2, B 36/36 ×2, F 30/30 ×2, R 42/42 ×2 |
| Specification manifest | regenerated; `validate-specifications.py --strict` passes |
| Stage B | **Not run** |

### 22.11 Residual risks and RB-CARV-1 status

| ID | Risk | Disposition |
|---|---|---|
| R-CARV-17 | Without `RequiredFloor`, a restore to before the floor was undetectable | **Resolved at the application level:** a Release B instance cannot run without it (startup refusal) and refuses writes without the floor (RX-D1). Protection of the setting itself: R-CARV-22 |
| R-CARV-21 | The write fence binds this build in Release B only. A legacy binary, a Release A-configured instance or direct SQL is stopped only by the database floor; while the floor is missing (restore, privileged drop) they can write again (RX-C3, RX-D2) | Open, operational: deployment inventory, keep organization gates closed, re-apply the floor before reopening writes, never deploy Release A configuration after Release B |
| R-CARV-22 | The configuration is not protected by anything in the repository; the application reads it once and refuses invalid values but cannot detect a deliberate redeployment from Release B to Release A | **Open, RB-CARV-1 item 1:** PB-11 protected Authority configuration, audited changes, PB-12 evidence |
| R-CARV-23 | The expected definitions are PostgreSQL 18.0 renderings; another server version or minor release may render them differently | Fail closed (writes refused, availability only); verify on Azure PostgreSQL before Release B |
| R-CARV-24 | Not serialized by the fence lock: `CREATE OR REPLACE FUNCTION` of the state guard (function owner), direct catalog edits (superuser) | Detected at the next write and by readiness; privileged access is governed by G08 |
| R-CARV-25 | Cost per Release B content write transaction: one savepoint, one table lock, one catalog query; the ROW EXCLUSIVE lock is held for the whole Prepare/Commit transaction (it was already taken at the first operation write) | Not benchmarked against PB-09; measure in staging |
| R-CARV-26 | The catch-all (`AuthorityStore` scope, `HasContentAcceptanceWrites`) lists the content-acceptance entities explicitly; a future entity of that kind must be added to it | Review rule (recorded here; RX-S1/S2 exercise the current list) |
| R-CARV-27 | Monitoring that matches readiness detail strings exactly must accept the Release B ready detail (it now names the phase) | Operational |
| R-CARV-11, -12, -16, -18, -19, -20 and the rest | As before | Unchanged (R-CARV-11/F-17 and R-CARV-12 open; R-CARV-19 recommendation retained) |

**RB-CARV-1 status.**

| Item (§21.10) | Status |
|---|---|
| 1. Protected configuration | Application side implemented (mandatory, strict, read once, observable). **Deployment protection open (PB-11)** |
| 2. Fail closed | Implemented and tested on real PostgreSQL: absent, lowered, recorded-but-missing, weakened, unverifiable floors and invalid policies refuse every content write of this build in the write transaction |
| 3. Restore | Implemented and tested: a pre-floor restore leaves the Release B instance refusing until the floor is re-applied (RX-D1..D3). Legacy binaries and Release A-configured instances: R-CARV-21 |
| 4. Azure staging evidence | **None** |

RB-CARV-1 is resolved at the application and database enforcement level for this build. It is **not closed for production**: items 1 and 4 remain, together with Azure PostgreSQL verification (R-CARV-18, R-CARV-23) and the existing release approvals.

### 22.12 Changes to accepted E3-M2b-1F items (for owner review)

| Item | Change | Why |
|---|---|---|
| `ContentAcceptanceRuleOptions` (E3-M2b-1F, decision 3) | Replaced by `ContentAcceptancePolicy`. The readiness detection it fed is unchanged in Release A and stricter in Release B; `RequiredFloor` without a Release B phase is now refused | RB-CARV-1 requires that a missing or partial configuration never means Release A |
| Release B readiness ready detail | Appends `; phase=ReleaseB requiredFloor=1` | Observability of the phase (owner requirement 4.8) |
| Harness F FX-24 | Expects the new Release B ready detail; its 30 checks are otherwise unchanged | The detail string changed, not the decision |
| Harnesses B and F | Construct the store with an explicit Release A policy | The constructor now requires a policy |
| `compose.yaml`, `appsettings*.json` | Declare the phase (`Unconfigured` default, `ReleaseA` for Development and the local stacks) | The phase is mandatory |
| `AuthoritySigningPolicyTests` helper | Adds `ContentAcceptance:EnforcementPhase = ReleaseA` | Its Staging/Production hosts must reach the refusal each test checks |

### 22.13 Owner acceptance (2026-11-15)

The owner **conditionally accepted RB-CARV-1 for Development/Testing** and authorized one signed local commit. This is owner-led acceptance, not an independent review; it is **not** Release B deployment approval and **not** production security certification. RB-CARV-1 is **not production-closed**.

| # | Owner decision |
|---|---|
| 1 | The mandatory `EnforcementPhase` policy and the removal of the standalone E3-M2b-1F `RequiredFloor` configuration model are accepted |
| 2 | Release A / Release B selection at startup, with a missing or invalid phase failing closed, is accepted |
| 3 | The transactional PostgreSQL content-write fence and its restore and concurrency evidence (§22.8) are accepted for Development/Testing |
| 4 | The §22.12 configuration, readiness-text, harness and test-helper changes are accepted |
| 5 | R-CARV-21 retained: legacy binaries, Release A instances and direct SQL remain outside the application fence |
| 6 | R-CARV-22 retained: the deployment configuration must be independently protected and audited through PB-11 |
| 7 | R-CARV-24 retained: privileged function replacement and catalog changes are not fully serialized by the table lock |
| 8 | R-CARV-23, R-CARV-25, R-CARV-26 and R-CARV-27 retained as documented release requirements |
| 9 | Every earlier unresolved risk is preserved, including R-CARV-11, R-CARV-12 and R-CARV-18 |

**Open release gates (unchanged by this acceptance):** protection and audit of the Release B configuration (R-CARV-22, PB-11/PB-12); Azure PostgreSQL compatibility of the fence definitions and the floor (R-CARV-18, R-CARV-23); privileged schema mutation outside the lock (R-CARV-24, G08); performance of the Release B write path (R-CARV-25, PB-09); Azure staging evidence (§22.11 item 4). The MP-1 and MP-3 removal results are recorded as narrower evidence (§22.9).

---

## Sources inspected (at `66f0aa7`)

- **Authority:** `ContentService.cs` (`PrepareCoreAsync` 66-233, `CommitCoreAsync` 265-486, census 764-917); `EnvelopeRules.cs`; `ContentDeltaRules.cs` (`RequireRule`, `BuildRules`); `ContentChangeOperation.cs`; `AuthorityEntityConfigurations.cs` (operations, heads, leaves); `AuthorityRuntimeGrants.cs`; `AuthorityStore.cs` (idempotency lookup); migration `20261104231152_ProjectionIntegrityS1` (trigger precedent); the migrations list.
- **Shared:** `RowCanonicalizerV1.cs`, `RowCanonicalDecoder.cs`, `RowSchemaDefinition.cs`; `docs/dynamic-forms/row-integrity-format.md`; `scripts/integrity/row_integrity_v1.py`; `trust_authority/row-integrity-v1.test-vectors.json`.
- **Business:** `BusinessSignedSchemas.cs`; `RecordCandidateStager.cs`; `CandidateBuilder.cs`; `UploadSessionHandlers.cs`; `SharedFieldWriteBuilder.cs`; `DocumentProcessingService.cs`; `RecordRestoreHandler.cs`; `CreateFormRecordHandler.cs`; `FieldProtectionMigrationService.cs`; `FormRecordEntityConfigurations.cs`; `DocumentEntityConfigurations.cs`; `BusinessRuntimeGrants.cs`; `BusinessDbContextModelSnapshot.cs`.
- **Tests:** `ContributorWriteProtocolTests.EnvelopeRules.cs`, `ProtectionReconciliationTests.cs`, `SessionRevocationConstraintTests.cs`, `RuntimeGrantsTests.cs`.
- **Documents:** `E3_M2_AUTHORITY_DATABASE_CONTRACT.md`, `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` (§11, §13).

No test suite was run for this contract. The only executed code was the reference-encoder script in the session scratchpad (§4.5), which reads the repository and writes nothing to it.
