# E3-M2 Authority and Database Contract — Design Review Package

**Status (2026-11-14):**

| Item | State |
|---|---|
| This package | Design for owner review. The package itself changed no code, migration, key provider or configuration. |
| E3-M1 (Envelope v2 core) | Accepted for Development/Testing (owner-led, 2026-11-14); local signed commit `de1729be569aa564d8a43a6c422f88a731ca48d1` on `e3/m1-envelope-v2-core`, parent `d6693838de4e7c2c421fe3f449cecb721d4fced2`; not pushed or merged |
| **E3-M2a** (universal Authority Envelope v1 validation) | **Conditionally accepted by the owner for Development/Testing** (2026-11-14), including the cross-revision data-key and nonce reuse remediation the owner authorized before commit. Implemented as recorded in §13. Owner-led acceptance, not an independent review. |
| E3-M2b to E3-M2g | **Not authorized** |
| E3-M3 to E3-M6, E-9 | Not authorized; nothing here implements them |
| OD-E3-15, OD-E3-16 | Decided by the owner (2026-11-14), E-3 §11 |
| Independent cryptographic review (V2-IND-01) | Required before production; not started |
| Production use of Envelope v2 | Not approved |

**Review:** owner-led design review. Not an independent security review and not a certification.

**Baseline verified at the start of this task:** branch `e3/m1-envelope-v2-core`, HEAD `de1729b`, parent `d669383`, clean working tree.

**Notation**

| Tag | Meaning |
|---|---|
| **[EXISTING]** | Implemented behaviour, verified in the cited source at `de1729b` |
| **[DECIDED]** | Previously approved by the owner (E-3 §11, E3-D*, OD-E3-15/16) |
| **[PROPOSED]** | New in this package; needs owner approval |
| **[CORRECTION]** | A statement in `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` that the code does not support; corrected here |

**Identifiers introduced:** findings F-01 to F-12; owner decisions OD-M2-01 to OD-M2-16; threats TH-M2-01 to TH-M2-16; increments E3-M2a to E3-M2g; tests M2-CMP, M2-SIG, M2-ENV, M2-DK, M2-CON, M2-IDM, M2-REP, M2-FEN, M2-RST, M2-CEN, M2-AUZ, M2-ARCH.

---

## 0. Summary

E3-M2 makes Envelope v2 data **acceptable, verifiable and readable**, while every writer keeps producing v1 (writers are E3-M4). It adds:

1. **Universal envelope validation in the Authority** (§2). Today the Authority checks the envelope only on share-editor updates (F-01). M2 validates every encrypted row on every write path, for v1 and v2.
2. **Signed schema 2** for `field_encrypted_values` and `file_attachment_versions` (§3). It adds three signed columns, uses the RowHmac v1 framing and Merkle v1 unchanged, and adds an explicit rule that ties the schema version to the envelope version.
3. **OD-E3-16 enforcement** (§4). A new insert-only Authority ledger, `content_data_key_bindings`, binds each v2 data-key id to exactly one organization, record, operation and commitment. It is enforced at Prepare and re-checked at Commit under the existing organization lock, and backed by a global unique index. Unchanged envelopes carried from the current head are exempt by an exact column-class rule.
4. **Business migration** (§5): three nullable columns on **two** tables (not three, F-02/F-03), with CHECK constraints and a guarded Down.
5. **An authoritative envelope census** (§6), computed inside the Authority at Commit from verified bytes, plus a verified backfill for older commits. A census with gaps reports `Incomplete` and is never treated as compliant.
6. **Rollback fences** (§7). A per-organization, data-conditional Authority write fence; an Authority-enforced business capability floor; business CHECK constraints; required-migration startup guards; and guarded Down migrations. Ordinary application rollback stays possible while it is compatible.
7. **An authorization matrix** (§8): tightening needs one authorized actor; loosening needs two.

Nothing in M2 writes v2 from a product path. The acceptance switch defaults to off and refuses Production. v2 rows reach the Authority only from test fixtures built with the M1 core.

**Recommended first increment:** E3-M2a, universal v1 envelope validation in the Authority (§10). It is Authority-only, has no schema change, tightens checks only, and is revertible by redeploying the binary.

---

## 1. Current Authority validation architecture [EXISTING]

### 1.1 What the Authority receives

For every content write, the business stages rows in BusinessDb and sends the Authority the **canonical RowHmac v1 bytes** of every candidate item, and of every current item (R-53). These go through `PrepareContentChangeRequest.CandidateItems` / `CurrentItems` and `CommitContentChangeRequest.StagedItems`, base64-encoded. The bytes contain:
- ciphertext, nonce, tag;
- the wrapped data key and the key reference;
- the algorithm name, envelope version and logical type.

They contain no plaintext and no unwrapped key. The Authority holds no encryption key and cannot decrypt (E-3 §1.3).

### 1.2 Prepare (`ContentService.PrepareCoreAsync`, `ContentService.cs:66-233`)

All of this runs in one AuthorityDb transaction under `LockOrganizationAsync` (the organization lock), with the record head read `FOR UPDATE`:

1. Idempotency: the same key with the same request digest returns the existing operation (`:82-92`).
2. Authorization-revision CAS, resource active, head not quarantined, content-revision/root CAS (`:94-120`).
3. `ParseCandidate` (`:1144-1230`):
   - framing and organization binding, current signing key version, entity kind allowed for the item kind;
   - `ContentDeltaRules.EnforceIdentityBinding` → `RequireRule`: the `(entity_kind, schema_version)` must be registered **and** the column set must match exactly. Otherwise `UNSUPPORTED_SIGNED_SCHEMA`.
4. `BindAcceptedSnapshotAsync`: the complete current snapshot is proven equal to the accepted head (coverage, SHA-256 digests, root, leaf index).
5. `ContentItemIdentityRules.EnforceAtPrepareAsync`: item anchors are unchanged (I-1), and new item ids were never bound or accepted (I-2).
6. Mutation classification:
   - **Owner:** `ClassifyMutations` compares **whole-row digests** only (`:1232-1253`).
   - **Share editor:** `ContentDeltaRules.ClassifyShareScoped` compares column classes Identity / Content / OperationBinding (`ContentDeltaRules.cs:232-373`).
   - **Reprotection:** `ReprotectionRules.Validate`.
7. Protection barrier (encrypted / plaintext per field); HMAC signing via `IIntegrityMacProvider`; candidate leaves stored.

### 1.3 Commit (`ContentService.CommitCoreAsync`, `:260-465`)

Same organization lock, head `FOR UPDATE`:
- an operation already Committed is replayed idempotently;
- expiry and head CAS;
- commit-time re-authorization;
- `VerifyStagedAgainstLeavesAsync`: digest, HMAC and root equality with the prepared candidate;
- `ContentItemIdentityRules.BindAtCommitAsync`, which inserts into `content_item_identities` (insert-only, globally unique item id);
- manifest commit, leaves and head advance, search-index derivation;
- one content-stream journal entry (`ContentHeadCommitted`, body `ContentCheckpointBody`) and a content checkpoint.

### 1.4 Reads (`VerifyRecordCoreAsync`, `VerifyStagedAgainstLeavesAsync`, `:1058-1142`)

A read verifies the leaf set, the digest of each item, the HMAC and the Merkle root. **It does not consult the column registry.** The registry is consulted only at Prepare (`ParseCandidate`) and in share classification (F-04).

### 1.5 Findings that change the M2 contract

| ID | Finding | Evidence | Consequence for M2 |
|---|---|---|---|
| **F-01** [CORRECTION of E-3 §1.3] | The envelope allow-lists `AllowedEnvelopeVersions = {1}` and `AllowedEnvelopeAlgorithms = {AES-256-GCM}` are enforced **only** for a share-editor `Update` of an encrypted value. Owner writes (digest classification), reprotection writes, every attachment row and record creation get **no Authority envelope check**. The census helper `EnvelopeLooksApproved` checks algorithm, lengths and (wrapped key, nonce) uniqueness, but **not** the envelope version. | `ContentDeltaRules.cs:358-371` (only use of the allow-lists); `ContentService.cs:899-913`; `ClassifyMutations` `:1232` | "Extend the allow-list to {1, 2}" is not enough. M2 needs one envelope validator applied to every encrypted row on every write path (§2). |
| **F-02** [CORRECTION of E-3 §3.8, §9] | `upload_sessions` has **no** envelope columns. The processed document envelope is a `file_attachment_versions` row in `version_state = 'Processed'`, with an empty operation id and an empty signature. | `UploadSession.cs`; `DocumentEntityConfigurations.cs:82` | No columns on `upload_sessions` (§5). |
| **F-03** | The creation-idempotency sealed key (SealedKey) is stored as **unsigned JSON** in `form_record_creation_attempts.fingerprint_key_envelope_json`. It is never sent to the Authority. | `FormRecordCreationAttempt.cs`; migration `20261024140510_RecordCreationAttempts` | No column change in M2. The JSON shape gains v2 fields in M4. The Authority has no role (§5.5). |
| **F-04** [CORRECTION of E-3 §6] | A pre-M2 Authority **verifies schema-2 rows correctly on read** (digest + HMAC + root), and **refuses** any candidate that contains one (`UNSUPPORTED_SIGNED_SCHEMA`). E-3 §6 said such a row "could be mistaken for tampering"; it would not. | `VerifyStagedAgainstLeavesAsync`; `RequireRule` | An Authority rollback fails closed for writes to v2 records without any fence. The fence (§7) is still needed so that M2's validation and ledger are not bypassed. |
| **F-05** | `BusinessSignedSchemas.Encode` picks the schema from the row's **stored** version only for `form_records`. Every other kind is always encoded under its v1 schema, and `CurrentSchemaVersion` is per type, not per row. | `BusinessSignedSchemas.cs:89-150` | M2 must dispatch on `row.RowSignatureSchemaVersion` for the two envelope tables (§3.4). |
| **F-06** | `UploadSessionHandlers.CloneValue` and `CandidateBuilder.CloneAttachment` copy envelopes byte for byte but **do not copy `RowSignatureSchemaVersion`**. Only `SharedFieldWriteBuilder` restores it (`KeepSchema`). Call sites: upload commit, `DocumentHandlers`, `RecordLifecycleHandler`, `FieldProtectionMigrationService` (unchanged rows), shared edits. | `UploadSessionHandlers.cs:344-369`; `CandidateBuilder.cs:36-66`; `SharedFieldWriteBuilder.cs:121, 194` | M2 clone helpers must preserve the schema version and the three new columns. Otherwise a v2 row would be silently re-encoded as schema 1. The database CHECK (§5) is the backstop. |
| **F-07** | Owner (full-scope) saves **re-seal every surviving encrypted value**. Byte-identical carry-forward happens only through the clone helpers of F-06. | `CandidateBuilder.cs:428-433`, `:515-519` | Defines the OD-E3-16 exemption precisely (§4.3). |
| **F-08** | Prepare and Commit each hold the organization lock. `content_item_identities` is an insert-only table with a globally unique item id and a `first_committed_operation_id`. | `ContentService.cs:76, 267`; `AuthorityEntityConfigurations.cs:306-323`; `AuthorityRuntimeGrants.cs:13` | Direct template for the data-key ledger (§4). Concurrency within an organization is already serialized. |
| **F-09** | Neither host checks schema compatibility at startup. Migrations run through the one-shot `--migrate` command. PB-11 pipeline fences are designed, not deployed. | `MigrateCommand.cs` (both hosts); ADR-PB-011 | Fences must be enforced in the database and at startup (§7), not by the pipeline alone. |
| **F-10** | The business CHECK `ck_field_encrypted_values_envelope_positive` is `encryption_envelope_version >= 1`, so it accepts any positive version. `file_attachment_versions` has no envelope-version CHECK. | `FormRecordEntityConfigurations.cs:179`; `DocumentEntityConfigurations.cs:76-82` | Replaced by closed `IN (1, 2)` constraints (§5). |
| **F-11** | `EnvelopeLooksApproved` requires a ciphertext longer than 0 bytes. AES-GCM of an empty plaintext is 0 bytes (v1 and v2; M1 round-trips the empty string). Whether empty encrypted strings are ever stored is **not verified** in this task. | `ContentService.cs:907` | The v2 rules allow 0-length ciphertext. Check the v1 behaviour in M2a before tightening (§2.3, OD-M2-09). |
| **F-12** | The `ContentHeadCommitted` body is `ContentCheckpointBody`. The only other reader, `SearchAccessJournalVerifier.VerifyContent`, reads `body.form_record_id` and nothing else. | `ContentService.cs:1553`; `SearchAccessJournalVerifier.cs:97-150` | A new body field is compatible (§4.6). |

---

## 2. Envelope v2 acceptance contract [PROPOSED]

### 2.1 Division of responsibility

| Property | Who establishes it | How |
|---|---|---|
| Row integrity and membership (no tamper, no missing or extra row, no replay of an older revision) | **Authority** | RowHmac v1 + Merkle v1 + content head [EXISTING] |
| Envelope **shape** (closed codes, lengths, canonical reference, organization/purpose/family agreement, schema ↔ envelope-version tie) | **Authority** | §2.3, from canonical bytes, with no keys |
| Batch **consistency** inside a candidate (one batch = one record, one kind, identical batch metadata; no repeated item or nonce) | **Authority** | §2.4 |
| **Data-key id freshness** across accepted revisions (OD-E3-16) | **Authority** | §4 ledger |
| No per-item envelope **downgrade** (v2 → v1) | **Authority** | §3.5 |
| AEAD tag validity; correct commitment; that the wrapped bytes really wrap the seed behind the commitment; that the referenced key version wrapped it | **Business opener + key provider** | M1 opener: commitment check before AEAD, fail closed (`KEY_MISMATCH`, `AUTHENTICATION_FAILED`, provider refusal) |
| Seed and nonce randomness; seed never reused under a *fresh* data-key id | **Business writer** (CSPRNG) | M1 writer rules. **Not verifiable by the Authority** (TH-M2-02). |
| The key version is in an unwrap-permitted state; the family matches the organization's profile | **Authority catalog (E3-M3)** | Not M2. In M2 the Authority checks only the reference's syntax and family allow-list. |
| The document object in Blob storage matches `ciphertext_sha256` | **Business/Worker** at read; Merkle binds the digest | [EXISTING] |

**The Authority never verifies AES-GCM authentication.** It never sees a seed, PRK, item key or plaintext. A row that passes every Authority check can still fail to open (for example, a buggy writer). The result is a fail-closed read (`423 FIELD_DECRYPTION_FAILED` or a document failure), never a release of wrong data. This residual is availability, not confidentiality.

The Authority never receives plaintext, unwrapped data keys or provider secrets. The 32-byte commitment it sees is an HKDF-Expand output of a 256-bit seed and does not reveal the seed. The commitment is also stored in BusinessDb in v2 rows by design.

### 2.2 Allow-lists (Authority constants, cross-checked by architecture test)

| List | Values in M2 | Change process |
|---|---|---|
| Envelope versions | `{1, 2}`; `2` only when the acceptance switch is on (§8) | Code + release (PB-11 Authority release approval) |
| AEAD algorithm names | `{"AES-256-GCM"}` (unchanged spelling for v1 and v2) | Code + release |
| Wrap-algorithm codes | `{1 localdev-aes256gcm, 2 vault-transit-aes256-gcm96}`, accepted **only** when the Authority runs in Development/Testing. In any other environment the set is empty, so no v2 row is acceptable outside Development/Testing until a provider milestone. | Code + release; production codes 3 and 4 are reserved (E-3 §3.5) |
| Reference families | `localdev` ↔ code 1, `vault-transit` ↔ code 2; one family maps to exactly one code | Code + release |
| Purpose tokens | `field-value` (field rows), `document` (attachment rows) | Code + release |
| Logical types (field) | `String, Integer, Decimal, Date, DateTime, Boolean, Guid, Option` (unchanged) | — |
| Document segment size | `1,048,576` | Code + release |

The Authority keeps **its own copy** of these lists and of the reference grammar, as `ContentDeltaRules` already does for column classes. An architecture test cross-checks the copy against the business definitions (`EnvelopeV2Format`, `KeyScopeEncoding`) and shared vectors. The Authority assembly must not reference business Infrastructure (ADR-ARCH-001).

### 2.3 Per-row validation (`EnvelopeRules`, every encrypted candidate row, every write path)

The rules apply to every candidate row of `field_encrypted_values` or `file_attachment_versions`, including carried rows, on owner, share-editor and reprotection paths, at Prepare. They are re-run at Commit on the verified staged bytes. A violation is `422 ENVELOPE_INVALID` with a reason code only; no identifiers or bytes appear in error texts.

**Schema 1 (v1), tightening (M2a):**
- `encryption_envelope_version = 1`;
- `encryption_algorithm_name = "AES-256-GCM"`;
- fields: nonce 12 bytes, tag 16 bytes, wrapped key 60 bytes;
- documents: nonce prefix 8 bytes, segment size 1,048,576, wrapped key 60 bytes;
- `encryption_key_reference` is canonical in v1 form: `localdev:` / `vault-transit:` scoped form, or the legacy literal `local-dev-kek-v1`;
- the organization in a scoped reference equals `tenant_id` and the header organization;
- the purpose matches the entity kind (field rows `field-value`; attachment rows `document`).

The exact v1 wrapped-key length and the empty-ciphertext question (F-11) are confirmed against the existing development and security-test data in M2a **before** the check is switched on. A census of existing rows runs first (OD-M2-09).

**Schema 2 (v2), M2d:**

| Column | Rule |
|---|---|
| `encryption_envelope_version` | `= 2` |
| `encryption_algorithm_name` | `= "AES-256-GCM"` |
| `key_wrap_algorithm_code` | ∈ allowed wrap codes (§2.2) |
| `encryption_key_reference` | canonical v2 grammar `family:organization:purpose:version` (E-3 §3.4). Family is allowed and **equals** the wrap code's family; organization = `tenant_id` = header organization (lowercase canonical `D` format); purpose = `field-value` for field rows, `document` for attachment rows; version is decimal 1..2147483647 with no leading zeros; `local-dev-kek-v1` refused; length 1–128, alphabet `[A-Za-z0-9._:/-]` |
| `data_key_identifier` | a UUID with version nibble 7 and the RFC 9562 variant; not all-zero |
| `data_key_commitment_bytes` | exactly 32 bytes |
| `wrapped_data_key_bytes` / `wrapped_data_key` | exactly the registered length for the wrap code (60 for codes 1 and 2) |
| field: `encryption_nonce_bytes` / `encryption_auth_tag_bytes` | 12 / 16 bytes |
| field: `encrypted_value_bytes` | 0 ≤ length ≤ a bound consistent with the business 4,000-character limit. 12,000 bytes is the UTF-8 maximum for 4,000 UTF-16 code units; the exact bound is pinned in M2d against `FieldValueCodec`. |
| field: `encrypted_logical_type` | closed set |
| document: `encryption_nonce_prefix` / `encryption_segment_size_bytes` | 8 bytes / 1,048,576 |
| document: `ciphertext_size_bytes` | `= plaintext_size_bytes + 20 × max(1, ⌈plaintext_size_bytes / 1,048,576⌉)` (4-byte length prefix + 16-byte tag per segment; one segment for an empty document). Pinned against KAT D0–D4 in M2d. This adds a check the Authority can make that v1 never had. |

### 2.4 Candidate-level batch consistency (v2 rows, M2d)

Over all **new** v2 rows of a candidate (§4.3 defines "new"), grouped by `data_key_identifier`:

1. All rows of a group have the same entity kind. A field batch is field rows only. A document batch is **exactly one** attachment-version row: a document version is its own batch (E3-D3).
2. All rows of a group are byte-identical in `data_key_commitment_bytes`, `key_wrap_algorithm_code`, the wrapped key and `encryption_key_reference`.
3. Within a field group, item ids are distinct and nonces are distinct. This mirrors the M1 opener's group rule, so a refused row is refused before it can be stored.
4. All rows belong to the candidate's record. This is already guaranteed by identity binding, and restated because the batch-to-record rule (R-M1-2) becomes Authority-enforced here.

Carried rows are not re-grouped; they were checked when first accepted.

> **Superseded for E3-M2d (owner decision OA-M2d-4, 2026-11-15; `E3_M2D_IMPLEMENTATION_RECORD.md` §12).** Batch consistency is validated over **all** v2 rows of a candidate, carried rows included, at Prepare and at Commit, so that Commit can re-derive it from the staged bytes. Carried rows of one accepted batch are byte-consistent, so no legitimate candidate is affected.

### 2.5 Compatibility with v1

- v1 rows stay schema 1 forever. They are never re-signed and never rewritten.
- v1 and v2 rows may coexist in one record revision. One item has exactly one row per revision.
- A v1 → v2 change of an item (owner save in M4, reprotection, M5 Reseal) is permitted. v2 → v1 is refused (§3.5).
- A share-editor update keeps the existing framing rule `SHARE_ITEM_FRAMING_CHANGED`, so the schema may not change in a share edit. Relaxing it to permit 1 → 2 is **M4 work**, needed when share edits start sealing v2. M2 does not change it.

---

## 3. Signed row schema 2 specification [PROPOSED]

### 3.1 Canonical serialization

Unchanged `DynamicForms/RowHmac/v1` framing (`row-integrity-format.md` §2), with `schema_version = 2` in the canonical header. The **RowHmac v1 and Merkle v1 algorithms, domains and existing test vectors do not change.** New schema-2 conformance vectors go in a **new** file. No existing vector file is regenerated (OD-M2-14).

### 3.2 `field_encrypted_values` schema 2 (registry order = ordinal byte order)

| # | Column | Type | Nullable | Authority class | New in v2 |
|---|---|---|---|---|---|
| 1 | `content_change_operation_id` | Uuid | no | OperationBinding | |
| 2 | `data_key_commitment_bytes` | Bytes | **no** | Content | ✓ |
| 3 | `data_key_identifier` | Uuid | **no** | Content | ✓ |
| 4 | `encrypted_logical_type` | Text | no | Identity | |
| 5 | `encrypted_value_bytes` | Bytes | no | Content | |
| 6 | `encryption_algorithm_name` | Text | no | Content | |
| 7 | `encryption_auth_tag_bytes` | Bytes | no | Content | |
| 8 | `encryption_envelope_version` | Int32 | no | Content | |
| 9 | `encryption_key_reference` | Text | no | Content | |
| 10 | `encryption_nonce_bytes` | Bytes | no | Content | |
| 11 | `field_definition_id` | Uuid | no | Identity | |
| 12 | `field_value_item_id` | Uuid | no | Identity | |
| 13 | `form_record_group_id` | Uuid | yes | Identity | |
| 14 | `form_record_id` | Uuid | no | Identity | |
| 15 | `key_wrap_algorithm_code` | Int32 | **no** | Content | ✓ (OD-M2-02) |
| 16 | `tenant_id` | Uuid | no | Identity | |
| 17 | `wrapped_data_key_bytes` | Bytes | no | Content | |

The new columns are **non-nullable in the signed registry**, even though the database columns are nullable (v1 rows store NULL). The canonicalizer therefore refuses a schema-2 row with a NULL v2 column (`MissingColumn` / NULL-not-permitted). There is no ambiguous "schema 2 holding a v1 envelope".

### 3.3 `file_attachment_versions` schema 2

The 26 schema-1 columns unchanged, plus `data_key_commitment_bytes` (Bytes), `data_key_identifier` (Uuid) and `key_wrap_algorithm_code` (Int32). All three are non-nullable and in the Content class, inserted in ordinal order. `ShareUpdatable` stays `false`. `PrimaryKeyClass.StorageKey` and item id `file_attachment_id` are unchanged.

### 3.4 Schema-version selection and verification

| Rule | Detail |
|---|---|
| **S-1 Selection by envelope** | A row's signed schema is chosen from **its own** envelope version, not from the type: envelope 1 ⇒ schema 1; envelope 2 ⇒ schema 2. The business host sets `RowSignatureSchemaVersion` when sealing and never changes it afterwards. |
| **S-2 Encoding from stored version** | `BusinessSignedSchemas.Encode` dispatches on `row.RowSignatureSchemaVersion` for both tables (F-05). An unknown (entity, version) pair throws; it never falls back to the latest version. |
| **S-3 Clone fidelity** | `CloneValue` / `CloneAttachment` copy `RowSignatureSchemaVersion` and the three columns (F-06). An architecture test asserts that each clone helper copies every mapped envelope property. |
| **S-4 Authority tie** | Prepare refuses schema 1 with `encryption_envelope_version ≠ 1`, and schema 2 with `≠ 2` (`ENVELOPE_SCHEMA_MISMATCH`). |
| **S-5 Signing** | The Authority signs exactly the candidate bytes, as today. No signature is created by re-encoding. |
| **S-6 Verification** | Unchanged: digest + HMAC + root. Historical rows verify under their recorded schema. A schema-1 row is never reinterpreted as schema 2 or the reverse; the version is in the signed header. |
| **S-7 Registry permanence** | Schema 1 of both tables stays registered indefinitely (history and backups). Retiring it would need its own decision. |
| **S-8 Business reader** | Business decryption refuses schema 2 with envelope ≠ 2, and schema 1 with envelope ≠ 1. The existing v1 service already refuses version ≠ 1 (`AesGcmFieldEncryptionService.cs:97`). |

### 3.5 Version transition rules (per item across revisions)

| From → to (same item key) | Rule |
|---|---|
| none → v1 | allowed (writers in M2 are v1) |
| none → v2 | allowed only with the acceptance switch on (§8) and all §2/§4 rules passing |
| v1 → v1 | allowed (re-seal or carry) |
| v1 → v2 | allowed (M4 owner save; reprotection; M5 Reseal). Refused for share editors until M4 relaxes the framing rule. |
| v2 → v2 carried | allowed by the exemption (§4.3) |
| v2 → v2 re-sealed | allowed with a **fresh** data-key id (§4) |
| **v2 → v1** | **refused** (`ENVELOPE_DOWNGRADE`) on every path (OD-M2-06) |
| encrypted → plaintext | unchanged: only through ADR-PB-004 Amendment A1 deprotection (`ReprotectionRules`, Deprotect direction) |

### 3.6 Protection against metadata substitution and downgrade

| Attack | Defence |
|---|---|
| A BusinessDb writer edits a data-key id, commitment, wrap code or reference | RowHmac + Merkle → quarantine [EXISTING mechanism, covering the new columns] |
| A writer seals with consistent but substituted metadata (for example, another record's data-key id) | §4 ledger: a new row reusing a bound id is refused. H0 binding: the commitment covers organization, purpose, reference, data-key id and wrap code, so any change fails at read. |
| Moving a v2 envelope to another item, record or organization | `C_field` / `C_doc` in HKDF info and AAD; Authority identity binding; the ledger's id → record binding |
| Re-encoding a v2 row as schema 1 to strip the metadata | Schema 1 cannot carry the v2 columns; S-4 refuses schema 1 with envelope 2; the business CHECK (§5) refuses version 2 with NULL columns |
| Replacing a v2 item with a v1 row | §3.5 downgrade guard |
| Changing the AEAD, KDF or format | Closed codes; reader refuses unknown codes (M1); Authority allow-lists |

---

## 4. OD-E3-16 enforcement design [PROPOSED; decided requirement]

**Requirement [DECIDED, OD-E3-16]:** a newly introduced Envelope v2 data-key id must not have appeared in an earlier accepted revision. Unchanged envelopes legitimately carried forward from an earlier revision are exempt.

### 4.1 Why the Authority, and why a ledger

- The Authority stores only **digests** of accepted rows, not their bytes (`record_manifest_leaves.canonical_payload_digest`). It cannot search history for a data-key id without one.
- Business-side checks are not trusted: BusinessDb is in the hostile-write model (§4M), and the business runtime is the writer whose mistakes we guard against.
- A **dedicated Authority ledger** is the smallest mechanism that is durable, transactional with the head advance, enforceable by a database uniqueness constraint, and replayable from the archived journal (§4.6).

### 4.2 Ledger table: `authority.content_data_key_bindings`

All column names are descriptive multiword snake_case, and the primary key follows the entity name.

| Column | Type | Null | Notes |
|---|---|---|---|
| `content_data_key_binding_id` | uuid | no | PK, UUIDv7 generated in .NET |
| `data_key_identifier` | uuid | no | **UNIQUE across all organizations** (`uq_content_data_key_binding_identifier`, OD-M2-04) |
| `organization_id` | uuid | no | FK → `organization_authorization_states.organization_id` (restrict) |
| `form_record_id` | uuid | no | the only record that may use this id |
| `envelope_payload_kind_code` | smallint | no | CHECK `IN (1, 2)` (1 field item, 2 document version) |
| `data_key_commitment_bytes` | bytea | no | CHECK `octet_length(...) = 32` |
| `binding_item_count` | integer | no | CHECK `>= 1`. Number of new rows that carried the id in the binding operation (document: exactly 1, CHECK) |
| `binding_content_change_operation_id` | uuid | no | the single operation allowed to introduce this id; FK → `content_change_operations` |
| `data_key_bound_on` | timestamptz | no | UTC |

- Index `ix_content_data_key_bindings_record (organization_id, form_record_id)` for the census and capability floor.
- **Grants:** the runtime role gets SELECT and INSERT only, listed in `AuthorityRuntimeGrants.InsertOnlyTables`, as `content_item_identities` is. No UPDATE or DELETE for the runtime, and no API that writes or removes a binding.
- **Accepted state is derived, not stored:** a binding is *accepted* if and only if its operation is `Committed`, which is equivalent to a `record_manifest_commits` row with `committed_operation_id = binding_content_change_operation_id`. Deriving it keeps the table insert-only.
- What the ledger does **not** bind: the wrapped key and the key reference. A future M5 Rewrap legitimately changes them while keeping the id and commitment (§4.8).

> **E3-M2e owner decisions (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §1.2, §3).** Uniqueness is global (OD-M2e-01) and ids are bound at Prepare as permanent reservations (OD-M2e-02/03). The table additionally carries the P-1 binding guard (insert only for a `Prepared` operation of the same organization and record with a Prepare rule version ≥ 3) and the P-2 immutability triggers (no UPDATE, DELETE or TRUNCATE), which resolve OD-M2-23 in the database (OD-M2e-04; L-6, L-7). No wrapped-key digest (P-3) is added in E3-M2e.

### 4.3 Classification of each v2 candidate row

Let *R* be a schema-2 candidate row with item key *K*, and *C* the verified current-head row with key *K*, if any.

- **Carried** (exempt): *C* exists, *C* is schema 2, and **every Identity-class and Content-class column of R is byte-identical to C** (`RowCanonicalColumn.FramedValueEquals`, the share classifier's `Unchanged` test).
  - Only the OperationBinding column (`content_change_operation_id`), the storage primary key and the row revision may differ. These are exactly the fields the clone helpers change (F-06).
  - Whole-row digests are **not** usable for this test: owner classification (`ClassifyMutations`) sees every carried row as `Update` because the operation binding and storage key are inside the bytes.
- **New**: every other schema-2 row. This includes a v2 row for a new item, an updated item, a re-sealed item, and an item whose current row was v1.

The exemption covers carry-forward from the **current accepted head only** (OD-M2-05). An envelope copied from an older, non-current revision is *New*, and is refused because its id is already bound. Restores therefore re-seal, which matches current behaviour (F-07: restored values go through the sealing path). This is to be re-verified for `RestorePlanner` in M2e.

### 4.4 Rules

**At Prepare** (organization lock held; after `ParseCandidate`, `BindAcceptedSnapshotAsync` and identity rules; for owner, share-editor and reprotection paths alike):

| Rule | Check | Error |
|---|---|---|
| DK-1 | Classify every schema-2 row (§4.3). | — |
| DK-2 | Batch consistency over New rows (§2.4). | `ENVELOPE_BATCH_INCONSISTENT` |
| DK-3 | No New group's id equals the id of any Carried row in the candidate, or of any row in the verified current snapshot. A new item may not join an existing batch. | `DATA_KEY_IDENTIFIER_REUSED` |
| DK-4 | No New group's id exists in `content_data_key_bindings`, for any organization and in any state (prepared, accepted or aborted). | `DATA_KEY_IDENTIFIER_REUSED` |
| DK-5 | Insert one binding per New group, bound to this operation, organization, record, kind and commitment, in the **Prepare transaction**. | — |

**At Commit** (organization lock held; after `VerifyStagedAgainstLeavesAsync` proves the staged bytes equal the prepared candidate):

| Rule | Check | Error |
|---|---|---|
| DK-6 | Re-derive the New groups from the verified staged bytes. Each must have a binding with this operation, organization, record, kind, commitment and item count. | `DATA_KEY_BINDING_MISMATCH` (operation aborted; minimal content-stream incident) |
| DK-7 | Carried rows are re-classified against the head, which the existing CAS guarantees is unchanged since Prepare. | as DK-3 |
| DK-8 | The sorted list of New ids is added to the `ContentHeadCommitted` journal body (§4.6), in the commit transaction. | — |

The operation's `envelope_rule_version` (§7.2) must equal 2. A Prepared operation created by an older Authority (NULL) is aborted at Commit (`OPERATION_RULES_OUTDATED`).

> **E3-M2e contract closure (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §2, §3).** AM-7: the `envelope_rule_version` sentence is superseded by CARV (OD-M2-17); the CARV-3 reading is P = C = 3. AM-2: DK-7 is satisfied by the Prepare classification (DK-1), CARV 3 and the head CAS, because Commit holds only digests and cannot re-classify; P-4 adds a Commit-time check that every carried v2 id is bound to an accepted operation of the same organization and record with the same kind and commitment and no more rows than its binding. AM-8 (OD-M2e-12): DK-4 runs after authorization and before signing. AM-11 (OD-M2e-11): a DK-6 or P-4 mismatch aborts the operation and writes a minimal content-stream incident. AM-10 (OD-M2e-07): with the switch off, New groups are refused at Prepare and at Commit; carried groups are judged by P-4.

### 4.5 Coverage of the brief's cases

| Case | Behaviour |
|---|---|
| **Organization scope** | Global uniqueness (OD-M2-04), so a reuse across organizations is refused (TH-16). The binding also records the organization. |
| **Record scope** | DK-2 and identity binding: a batch belongs to one record. A later use from another record is refused by DK-4. |
| **Operation identity** | DK-5/DK-6: one id, one operation. Reserving at Prepare also refuses reuse after an **aborted or expired** attempt, which is stricter than "accepted" (OD-M2-03). |
| **Content revisions** | Accepted means bound to a committed operation, which advanced exactly one content revision. |
| **Key-id uniqueness** | DK-4 under the lock, plus the database UNIQUE index as backstop |
| **Retries and idempotency** | A Prepare replay with the same idempotency key and request returns the existing operation and inserts nothing (the existing early return). A Commit replay of a Committed operation returns the head and inserts nothing. A **new** attempt must use a **fresh** batch. Reusing the staged batch is refused by DK-4. Business rule: on `DATA_KEY_IDENTIFIER_REUSED`, re-stage once with a fresh batch; never reuse. |
| **Concurrent writes, same organization** | Prepare and Commit are serialized by the organization lock: check-then-insert is atomic. |
| **Concurrent writes, two organizations** | Not serialized by a shared lock. The global UNIQUE index refuses the second insert (SQLSTATE 23505). The transaction rolls back and is mapped to `422 DATA_KEY_IDENTIFIER_REUSED`. Nothing partial is written. |
| **Partial updates (share edits)** | Unedited v2 items are Carried (exempt). The edited item is New with a fresh id. The existing `SHARE_ENVELOPE_REUSED` check stays for v1. |
| **Re-protection (B-28 Encrypt)** | New encrypted rows are New, under the same rules. Deprotect produces no v2 rows. |
| **M5 Reseal** | New rows with fresh ids; nothing special |
| **M5 Rewrap (documents)** | Same id and commitment, changed wrap: **refused in M2** as New-with-bound-id. M5 must add an explicit `RewrapRules` exemption (§4.8). It is never an accidental allowance. |
| **Historical revisions** | Reads never consult the ledger. History remains verifiable. Old envelopes cannot be reintroduced as New. |
| **Restore (business-level)** | Restored values are re-sealed: New, fresh ids |
| **Replay of a historical row or revision** | A row copied from an older revision is New with a bound id → refused (DK-4). Whole-snapshot replay is refused by the content head CAS and R-53 [EXISTING]. |
| **Database restore** | §4.6 and §7.6 |

### 4.6 Durability, archive and restore

- Bindings are written in the same AuthorityDb transaction as the Prepare (DK-5). Accepted bindings become accepted in the same transaction as the head advance (DK-6/DK-8). An AuthorityDb restore therefore never shows a head that its bindings lack.
- **Archive:** `ContentCheckpointBody` gains `new_data_key_identifiers` (a sorted array, at most one id per New group, bounded by `MaxCandidateItems = 500`). The content stream is already archived and anchored (reads require `ArchiveDeliveryState = Anchored`). After this change, every **accepted** binding is in the independent archive. The only other parser of the body is unaffected (F-12). Data-key ids are not secret (E3-D11), so archiving them reveals only batch grouping, which the operation id already reveals.
- **AuthorityDb restore to an earlier point:**
  1. G14 keeps the organization gated until replay and evidence agree [EXISTING design; replay of the content stream is G14/E3-M6 work].
  2. Bindings of commits after the restore point are recovered from archived `ContentHeadCommitted` bodies.
  3. Bindings of operations that never committed are **not** recoverable. The stricter Prepare-reservation guarantee is best-effort across a restore; the decided "accepted" guarantee is preserved.
- **Until E3-M6 implements content-stream replay:** after any AuthorityDb restore, v2 acceptance stays disabled (switch off) until a ledger rebuild has been run. The rebuild uses verifier-workload bytes of every accepted commit, checked against Authority leaves (§6.3), plus the archive. Two authorized approvers are required (§8). Because M2 never enables v2 outside Development/Testing, this bounds the gap.

> **E3-M2e contract closure (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §3.6, §4).** AM-3 (C-4, amends OD-M2-15): the body carries the **complete binding tuple** of every New group (binding id, data-key id, kind, commitment, item count, bound-on time; organization, record and operation from the stream and the body), present on every CARV ≥ 3 commit. Archived identifiers alone do not prove ledger correctness: archive completeness needs the anchored journal chain, and agreement with the committed rows after the fact needs independently verified content bytes. AM-4 (C-7): turning the switch off is not an enforcement mechanism, because the switch is outside the restored database. Persistent activation requires an Authority-enforced, fail-closed, restore-resistant barrier (RB2-1..RB2-8); its mechanism is blocking decision BD-1, and re-insertion of bindings with their operations is blocking decision BD-2. Until both are decided, implemented and verified, v2 acceptance is limited to disposable Development/Testing environments.

### 4.7 Atomicity and failure behaviour

| Failure | Result |
|---|---|
| Rule DK-1 to DK-4 fails | 422; nothing written; nothing signed |
| Crash after Prepare commits | Bindings exist with a Prepared operation; the id is reserved; the operation expires; the writer re-stages fresh |
| Crash after Commit commits | Head, bindings and journal are durable together; the business repairs a lost acknowledgement through the existing path |
| Cross-organization unique violation | Transaction rolled back; 422; the writer re-stages fresh |
| DK-6 mismatch | Operation aborted; minimal incident on the content stream; no head advance |
| Ledger unavailable (database error) | 503; fail closed; no fallback |

### 4.8 What OD-E3-16 does not give (stated, not designed away)

- **Seed reuse under a fresh id is undetectable by the Authority** (TH-M2-02). A writer that reuses a seed under a new UUIDv7 produces a different, valid commitment, because H0 includes the id. Seed freshness stays a writer (CSPRNG) property, as in M1 (R-M1-8 is narrowed, not closed).
- **Rewrap (M5)** needs an explicit, separately reviewed exemption: same id, same commitment, only the wrap columns and operation binding changed, under `RewrapRules` and a governed command. M2 rejects it.

---

## 5. Database migration proposal (BusinessDb) [PROPOSED]

### 5.1 Tables actually affected

| Table | Change | Reason |
|---|---|---|
| `field_encrypted_values` | 3 nullable columns + CHECKs | signed v2 envelope rows |
| `file_attachment_versions` | 3 nullable columns + CHECKs | signed v2 document rows, including the `Processed` staged copy |
| `upload_sessions` | **none** | it holds no envelope (F-02) [CORRECTION] |
| `form_record_creation_attempts` | **none** | the sealed key is JSON; v2 JSON fields are an M4 writer concern (F-03) |

### 5.2 Columns (both tables)

| Column | SQL type | Null | Default | Notes |
|---|---|---|---|---|
| `data_key_identifier` | `uuid` | yes | none | UUIDv7 of the seal batch |
| `data_key_commitment_bytes` | `bytea` | yes | none | 32 bytes |
| `key_wrap_algorithm_code` | `integer` | yes | none | registry code. `integer` rather than `smallint`, because RowHmac has no Int16 type; this avoids a type-mapping exception and matches `encryption_envelope_version`. |

There are **no defaults**: a default would let a v2 row omit metadata silently. There is **no backfill**: v1 rows keep NULL, are never re-signed, and remain v1 (E-3 §8.2).

### 5.3 Constraints

`field_encrypted_values`:
- **drop** `ck_field_encrypted_values_envelope_positive` (`>= 1`, F-10) and **add**:
  - `ck_field_encrypted_values_envelope_version`: `encryption_envelope_version IN (1, 2)`
  - `ck_field_encrypted_values_envelope_metadata`:
    `(encryption_envelope_version = 1 AND data_key_identifier IS NULL AND data_key_commitment_bytes IS NULL AND key_wrap_algorithm_code IS NULL)`
    `OR (encryption_envelope_version = 2 AND data_key_identifier IS NOT NULL AND octet_length(data_key_commitment_bytes) = 32 AND key_wrap_algorithm_code IN (1, 2))`

`file_attachment_versions`:
- **add** `ck_file_attachment_versions_envelope_version` (`IN (1, 2)`, new; there was no check) and `ck_file_attachment_versions_envelope_metadata` (same form as above).

The wrap-code list is **explicit**, so a new code (3 or 4, provider milestone) needs a deliberate migration. The CHECKs are defence in depth, not authority: the Authority rules (§2) and RowHmac are the trust boundary. They also turn the F-06 clone defect (version 2 with NULL columns) into a hard insert failure instead of a silent downgrade.

### 5.4 Indexes

**None added.** Value rows are read by `(form_record_id, content_change_operation_id)` and the existing keys. A `data_key_identifier` index in BusinessDb would serve only non-authoritative diagnostics; the authoritative uniqueness index is in AuthorityDb (§4.2). This follows the "avoid redundant indexes" rule.

### 5.5 Ordering, rollback and existing behaviour

| Aspect | Contract |
|---|---|
| Ordering | Expand-only. Safe to apply while pre-M2 business hosts run: they insert version 1 with NULLs, which satisfies the CHECKs. Apply before the M2 business code (E3-M2b). |
| Existing v1 behaviour | Byte-identical: no v1 row changes, no signature changes, and existing v1 tests run unchanged |
| EF mapping | Domain properties `DataKeyIdentifier` (`Guid?`), `DataKeyCommitmentBytes` (`byte[]?`), `KeyWrapAlgorithmCode` (`int?`) on `FieldEncryptedValue` and `FileAttachmentVersion`. Registered as signed for schema 2 in `BusinessSignedSchemas`, and as unsigned for schema 1 in `SignedColumnRegistryTests` with the stated reason "NULL by construction for schema 1". |
| Down migration | Guarded: SQL `DO $$ … IF EXISTS (SELECT 1 FROM field_encrypted_values WHERE encryption_envelope_version = 2) OR EXISTS (… file_attachment_versions …) THEN RAISE EXCEPTION …`. A Down cannot destroy v2 metadata. Down is a Development/Testing operation only. |
| Spec | The migration is the only one owning these columns (single migration owner, §4U.5) |

### 5.6 Normalization assessment

| Option | Description | Assessment |
|---|---|---|
| **N1 (recommended)** | Three inline signed columns per row | Every row is self-describing and fully covered by RowHmac + Merkle v1. The cost is about 52 bytes per row of repeated batch metadata (16 + 32 + 4), in addition to the wrap and reference already repeated in v1. That is about 13 KB per 250-value record revision. |
| N2 | Separate **signed** `data_key_batches` entity referenced by id | Needs a new signed entity and a **new Merkle leaf kind**, and Merkle v1 kind codes are fixed (CLAUDE.md §2.3A: new leaf kinds need manifest v2). A batch row shared across revisions and records also complicates membership and anti-replay. Rejected for M2. |
| N3 | Unsigned wrap table (E-3 W2) | Rejected by E3-D5 / OD-E3-4 (unsigned, security-relevant side table) |
| N4 | Two columns: derive the wrap code from the reference family | Possible, because one family maps to one code. But H0 (and so every derived key) depends on the code. Storing it signed keeps historical key schedules independent of any future registry change ("no silent reinterpretation"), and the Authority can **check** agreement instead of inferring it. The cost is 4 bytes per row. See OD-M2-02. |

---

## 6. Envelope census design [PROPOSED]

### 6.1 Principle

The census is computed **inside the Authority from bytes it has verified**, either in the same transaction as the head advance or by an attested backfill. It is never a BusinessDb query or an asynchronous projection. A census with any unknown commit reports `Incomplete`. Every consumer treats `Incomplete` as "may contain v2 / may need re-protection" (fail closed).

### 6.2 Commit-time summaries (authoritative)

New AuthorityDb tables. Append-only to the runtime: INSERT and SELECT only.

**`manifest_envelope_summaries`** (one per `record_manifest_commits` row):

| Column | Type | Notes |
|---|---|---|
| `manifest_envelope_summary_id` | uuid | PK |
| `record_manifest_commit_id` | uuid | UNIQUE, FK → `record_manifest_commits` |
| `organization_id`, `form_record_id` | uuid | |
| `committed_content_revision` | bigint | |
| `summary_provenance_name` | text | CHECK `IN ('CommitTime', 'VerifiedBackfill')` |
| `encrypted_field_v1_count`, `encrypted_field_v2_count` | integer | ≥ 0 |
| `document_version_v1_count`, `document_version_v2_count` | integer | ≥ 0 |
| `field_data_key_batch_count` | integer | distinct v2 field data-key ids in the revision (provider unwraps per full read, V2-PRF) |
| `envelope_anomaly_count` | integer | rows failing §2.3 (only possible in backfilled pre-M2 commits) |
| `envelope_summary_recorded_on` | timestamptz | |

**`manifest_envelope_key_uses`** (child, one per distinct key use in the revision):

| Column | Type | Notes |
|---|---|---|
| `manifest_envelope_key_use_id` | uuid | PK |
| `manifest_envelope_summary_id` | uuid | FK |
| `envelope_payload_kind_code` | smallint | 1 field, 2 document |
| `encryption_envelope_version` | integer | 1 or 2 |
| `key_reference_family_name` | text | `localdev`, `vault-transit`, or `legacy-local-dev` for `local-dev-kek-v1` |
| `key_purpose_token` | text | |
| `key_version_number` | integer | NULL only for the legacy literal |
| `key_use_item_count` | integer | ≥ 1 |

Written in `CommitCoreAsync`, after `VerifyStagedAgainstLeavesAsync`, from the verified staged bytes, in the commit transaction. The work is linear in candidate size and adds no extra round trip.

### 6.3 Verified backfill (commits before M2)

The **existing** verifier workload pattern (G19 `VerifyForProtectionCensusAsync` / IX-2 `VerifyWorkloadObservedContentAsync`) is extended to **historical** commits:
1. The workload submits the business bytes of revision *n*.
2. The Authority verifies them against **that** commit's leaves, root and key version.
3. It writes a `VerifiedBackfill` summary.

A verification failure follows the existing rules (quarantine plus a minimal incident; never repaired). Missing historical bytes leave the commit unsummarized, so the census stays `Incomplete`. This is a G14 discrepancy, not an assumption. Historical verification under a key version below `MinimumVerificationKeyVersion` reports `KEY_UNAVAILABLE`, never compliant.

### 6.4 Census queries (counts only; never contents or identifiers to non-operators)

| Measure | Definition |
|---|---|
| v1 / v2 records | current heads whose summary has `encrypted_field_v1_count + document_version_v1_count > 0` / `encrypted_field_v2_count + document_version_v2_count > 0` |
| v1 / v2 documents | sum of `document_version_v1_count` / `_v2_count` over current heads (per attachment version present in the head) |
| Mixed-version records | current heads with both v1 and v2 counts > 0 |
| Active key versions | distinct (family, purpose, version) over current heads' key uses |
| Historical key dependencies | distinct (family, purpose, version) over **all** commits' key uses (Invariant K-1 input) |
| Records requiring re-protection | current heads with any v1 row. With a target key version (M5): any key use below it. |
| Records incompatible with an older application version | records with v2 in the current head **or any historical commit** (an older reader cannot read their history). Organization level: any accepted ledger binding. |
| Completeness | `Complete` if every `record_manifest_commits` row of the organization has a summary and every current head is verified-readable; otherwise `Incomplete(n unsummarized)` |

**Cross-validation (test and operator check):** for each record, the number of distinct v2 field data-key ids in the summaries must equal the number of accepted ledger bindings of kind 1 introduced by that record's commits, and likewise for documents. A mismatch is an integrity incident.

**Who may read:** see §8. The operator plane gets counts only; per-record lists go only to an authorized Security Administrator of that organization (OD-M2-11).

---

## 7. Deployment rollback fence [PROPOSED]

### 7.1 Compatibility matrix

| Component pair | Compatible? | Enforcement |
|---|---|---|
| Business pre-M2 ↔ BusinessDb post-M2 migration | **Yes** while no v2 rows exist (expand-only). After v2 exists: reads of v2 fail closed (423); writes that clone v2 rows fail the CHECK. | §5.3 CHECKs; capability floor (§7.3) |
| Business M2+ ↔ BusinessDb pre-M2 migration (partial or full restore) | **No** | Required-migration startup guard (§7.4) |
| Authority pre-M2 ↔ AuthorityDb post-M2, organization **without** v2 bindings | **Yes** (no v2 to protect) | — |
| Authority pre-M2 ↔ AuthorityDb post-M2, organization **with** v2 bindings | **No** | Data-conditional write fence (§7.2) |
| Authority M2+ ↔ AuthorityDb pre-M2 | **No** | Required-migration startup guard (§7.4) |
| Business pre-M2 ↔ Authority M2+, organization with v2 | **No** | Capability floor (§7.3) |
| Business M2 / M3 ↔ v2 data written by M4 | **Reads yes. Writes to v2 records refused** by the downgrade guard (§3.5) and, after cutover, by `MinimumWriteEnvelopeVersion` (M4). | Authority |

Minimum safe versions:
- **Business reader** = E3-M2c.
- **Business writer after any v2 write** = E3-M4.
- **Authority** = E3-M2e for any organization with a binding.

### 7.2 Authority write fence (data-conditional, per organization) — OD-M2-07

- **Add** `content_change_operations.envelope_rule_version integer NULL`. An M2+ Authority always writes `2`.
- **Add** a trigger `trg_content_change_operations_envelope_rules`, `BEFORE INSERT` on `content_change_operations`:
  - if `NEW.envelope_rule_version IS NULL` **and** `EXISTS (SELECT 1 FROM content_data_key_bindings WHERE organization_id = NEW.organization_id)`, then `RAISE EXCEPTION` (a fixed message, no identifiers).
  - A pre-M2 Authority does not know the column, inserts NULL, and so **cannot prepare any write** in an organization that has v2 bindings. Organizations without v2 keep working, so an ordinary rollback without a database restore remains possible while it is compatible.
- **M2+ Commit** refuses an operation with NULL `envelope_rule_version` (prepared by an older Authority): `OPERATION_RULES_OUTDATED`, aborted, re-stage.
- **Alternative F1 (S1-style, unconditional):** a `NOT NULL` column with no default makes every pre-M2 write fail in every organization. It is simpler and stricter, but makes any Authority rollback below M2 a Down-migration operation (Development/Testing only).
- **Rejected:** a documented-only fence (F-09: nothing enforces it).

The trigger function is owned by the schema owner, uses a fixed `search_path` with `pg_temp` last, has no SECURITY DEFINER, and is listed in the G08 privilege inventory.

> **E3-M2f contract closure (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` OD-M2f-02, §2).** OD-M2-07 is decided as **F0**: CARV 3, floors 1–3 and the Release B transactional fence are the Authority rollback fence. The `envelope_rule_version` column and the trigger above (F2) and the F1 alternative are **not built**; no CARV 4, floor 4 or trigger is introduced for M2f. Residual limitations are listed in that record §2 (R-M2f-1..R-M2f-4).

### 7.3 Business capability floor (Authority-enforced)

- Business hosts (API and Worker, through the shared Authority client) send `EnterpriseForms-Envelope-Capability: 2` on every internal Authority call. It is defined in the internal Authority OpenAPI and is **not** part of the request digest. Pre-M2 builds do not send it, which counts as level 1.
- **Floor:** level 2 for an organization with any ledger binding (any state). The floor is derived from the insert-only ledger, so it is monotonic. There is also an optional deployment-wide floor (`Content:MinimumBusinessEnvelopeCapability`, default 1; Production may only raise it).
- A Prepare, Commit, Verify or batch-Verify below the floor is refused with `409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, before any work. Reads by an old build therefore fail with one clear refusal instead of per-row decryption failures. The refusal is journaled once per (organization, client) per interval on the audit stream; no identifiers beyond the organization appear.
- **Trust:** this stops **honest old builds**. A compromised business runtime can send the header (A3 is already trusted with wrap and unwrap). That is stated, not hidden.
- Scope per organization (recommended) or per record: OD-M2-08.

> **E3-M2f contract closure (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` §1, §3–§9).** OD-M2-08 is decided: **per organization**. Effective floor = `max(deployment floor, organization floor)`, where the organization floor is 2 when any data-key binding exists for the organization, in any state.
> - The header is sent by `AuthorityHttpClient`, `ProtectionVerifierHttpClient` and `SearchIndexBuilderHttpClient` on every request. A missing, unknown or malformed value counts as level 1.
> - The check is enforced at **seven** entry points: Prepare, Commit, Verify, batch Verify, the verifier census, search-index backfill and deep-sweep verify.
> - It runs in the transaction, after the organization lock (and after the Release B fence where present), and before any verification or quarantine-capable step (finding G-2: an honest pre-E3-M2b-2 reader otherwise causes a false quarantine or integrity incident).
> - The ledger query is narrowed by AM-F1 (an L-10 exception).
> - The error code above is kept. "Journaled once per (organization, client) per interval" is replaced by an approved requirement for durable, bounded refusal evidence whose design is deferred to E3-M2f-3.
> - `Content:MinimumBusinessEnvelopeCapability` is governed by PB-11 procedures. "Production may only raise it" is procedural, not mechanical.
> - Where the text above differs, that record governs.

### 7.4 Required-migration startup guards (both hosts)

Each M2+ host checks at startup that its migrations history contains the **required migration ids** compiled into the binary:
- business: the E3-M2b migration;
- Authority: the E3-M2e / M2f migrations.

If any is missing (a database restored below the migration, or a migration not applied), the host refuses readiness: it fails closed and the error message carries no identifiers. This also protects every later fence: a future binary refuses an older schema. The startup guard is read-only (the runtime role can read `ef_migrations_history`; this is confirmed in M2b/M2f).

> **E3-M2e contract closure (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §2 AM-12).** The Authority required-migration guard includes the E3-M2e ledger migration id. Floor 3 additionally requires the ledger objects to exist exactly as created (AM-1).

### 7.5 Deployment patterns

| Pattern | Contract |
|---|---|
| **Rolling (business)** | M2 and pre-M2 instances can coexist while no v2 binding exists, which is always the case in M2 outside Development/Testing. Before enabling v2 anywhere, drain every pre-M2 instance; the floor then refuses stragglers. |
| **Blue/green (business)** | As rolling. Switching back to a pre-M2 colour is safe until the first v2 binding in an organization, and refused for that organization afterwards. |
| **Authority** | Migration first (the M2 migration adds only nullable columns, tables and the trigger), then the M2 binary. A pre-M2 Authority instance still running keeps working for organizations without bindings. Drain it before enabling v2. **(E3-M2e closure, 2026-11-15, AM-6:** floor 2 must be installed before the first v2 binding or acceptance, because CARV-1 builds have no downgrade guard; the full sequence is `E3_M2E_CONTRACT_CLOSURE.md` §5.1.**)** |
| **Failed rollout** | Expand-only migrations stay. Roll the binaries back. No v2 exists unless the switch was enabled, so nothing is fenced. |
| **Application rollback, no v2 present** | Allowed without a database restore (business and Authority) |
| **Application rollback, v2 present** | Business: to M2c+ only. Authority: to M2e+ only, per organization. Below that: refused; roll forward. **No database restore is needed**, because compatible versions exist. |
| **Database restore (full)** | G14 semantics. The startup guards refuse binaries that are too new for the schema. Ledger bindings are recovered from the archive (§4.6). |
| **Partial restore (BusinessDb only)** | The ledger in AuthorityDb is unaffected, so restored business rows cannot reintroduce bound ids as New. Heads ahead of business rows are quarantined or awaiting (G14). |
| **Partial restore (AuthorityDb only)** | Heads and bindings revert together. The business is ahead and quarantined per G14. v2 acceptance stays disabled until the ledger rebuild (§4.6). |
| **Authority/Business divergence** | No path trusts BusinessDb for bindings. The census reports `Incomplete` until reconciled. |

### 7.6 PB-11 pipeline rule [PROPOSED; not implemented]

The deployment pipeline should record the minimum safe Authority and business versions and refuse to promote an older digest to an environment past the M2 migrations. Until the pipeline exists (ADR-PB-011 is design only), §7.2–§7.4 are the enforced fences.

---

## 8. Security and authorization matrix [PROPOSED]

Principle (consistent with E-4, E-5 and `CRYPTOGRAPHIC_SIGNING_AUTHORITY.md` §5): **tightening needs one authorized actor and is audited; loosening needs two distinct authorized actors.** No business-API path, organization setting, form JSON or user role can change any item below.

| Operation | Mechanism | Who | Dual control | Audit |
|---|---|---|---|---|
| Change accepted envelope versions, algorithms, wrap codes or families | Code + Authority release | Authority release approver (PB-11), separate from business deployers; independent security approver in production (PB-12) | **Yes** in staging and production (author + independent approver) | Release evidence (PB-12) |
| Activate signed schema 2 (registry entries) | Code + Authority and business release, the same way | as above | **Yes** | Release evidence |
| Enable v2 acceptance (`Content:EnvelopeV2AcceptanceEnabled`, default off) | Authority deployment configuration only. **Production-designated Authority refuses to start with it on** (`ShareContributorWriteSwitch` precedent). Testing-environment override via the operator plane. | Development/Testing operator | No in Development/Testing. Not possible in Production in M2. A later production enablement needs E-9, M3, M4, V2-IND-01 and dual control. | Startup audit-stream event `EnvelopeAcceptancePolicyObserved` (policy digest) whenever the effective policy differs from the last recorded one |
| **Disable** v2 acceptance / emergency suspension | Configuration off, or the Testing override off | One authorized operator (tightening) | No | Same event |
| **Re-enable** after a suspension | Configuration | Two distinct operators (loosening) | **Yes** in staging and production | Same event, both approvals recorded |
| Record data-key usage | **Automatic only:** the Authority at Prepare and Commit. Runtime role INSERT-only. No API, no operator command. | — | n/a | Content journal (§4.6) |
| Remove or repair a ledger binding | Not possible for the runtime. Only through the G14 recovery control plane, migration identity, with evidence. | Recovery operators | **Yes** | Recovery evidence; gating stream |
| Re-protection (B-28 / A1) | Existing `PublishFormSecurityRevision` command and verifier attestation | unchanged | unchanged | unchanged |
| M5 Reseal / Rewrap campaigns | Out of M2 (OD-E3-2) | proposer + distinct approver | **Yes** | M5 |
| Disable envelope **v1** for new seals | Out of M2 (M4 `MinimumWriteEnvelopeVersion`) | — | M4 | M4 |
| Census read: counts | Operator plane | Platform Operator (aggregate, no identifiers) | No | Audit stream |
| Census read: per-record list | Authority API | Security Administrator of that organization | No | Audit stream |
| Ledger rebuild after AuthorityDb restore | Verifier workload + recovery control plane | Recovery operators | **Yes** | Gating stream |
| Raise the business capability floor | Configuration (tightening) | One operator | No | Startup event |
| Lower the business capability floor | Not permitted below a derived organization floor; the deployment floor is lowered only in Development/Testing | — | n/a | — |

No new bypass is introduced. The Authority stays the only acceptance point, and the switch only narrows what it accepts.

> **E3-M2e owner decisions (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §1.2, §2, §5).** OD-M2-10 is decided as OD-M2e-07:
> - The Authority refuses to start with the switch on in any environment other than Development/Testing, Staging included (AM-9: wrap codes {1, 2} only in Development/Testing).
> - With the switch on, it also requires Release B with `RequiredFloor = 3` and the ledger migration (AM-5). Acceptance happens only inside a transaction whose Release B fence has verified floor ≥ 3.
> - The Testing override may enable only when these prerequisites hold, and may always disable.
> - `EnvelopeAcceptancePolicyObserved` is written to the platform journal (OD-M2e-13).
> - Enabling the switch needs a separate owner authorization, even in a disposable environment (OD-M2e-16).
>
> No persistent-environment activation is authorized under E3-M2e.

---

## 9. Threat model [PROPOSED mitigations]

Attackers as in E-3 §7: A1 BusinessDb writer/DBA, A2 Blob writer, A3 compromised business runtime, A4 compromised provider credential, A5 operator, A6 restore or rollback actor. The independent Authority, Entra ID and HSM administrators are trusted.

| ID | Threat | Mitigation | Residual | Verification evidence |
|---|---|---|---|---|
| TH-M2-01 | **Data-key reuse:** a new row re-presents an accepted batch id (A3 writer bug, A1 staged edit) | Ledger DK-3/DK-4; Carried exemption only for byte-identical current-head rows | None for id reuse | M2-DK-01..08 |
| TH-M2-02 | **Seed reuse under a fresh id** | Not detectable by the Authority (§4.8); writer CSPRNG; M1 single-batch rules | **Material if the writer is buggy or compromised; accepted and stated.** A3 can already unwrap everything. | Code review; M1 tests |
| TH-M2-03 | **Key-id collision** (accidental UUIDv7 duplicate) | Global UNIQUE → refused; writer re-stages fresh | Availability only, negligible probability | M2-DK-09, M2-CON-02 |
| TH-M2-04 | **Cross-record metadata substitution** | Identity binding; `C_field`/`C_doc` binding; ledger id → record; RowHmac/Merkle | None identified | M2-ENV-10, M2-DK-05 |
| TH-M2-05 | **Cross-organization substitution** | Reference organization = tenant = header (§2.3); global ledger; HKDF H0 | None identified for A1. A3 logical isolation only (E-3 §2.2). | M2-ENV-04, M2-DK-06 |
| TH-M2-06 | **Signature or schema downgrade** (re-encode v2 as schema 1; sign under an older schema) | S-2/S-3/S-4; non-nullable v2 columns; business CHECK; schema in signed header | None identified | M2-SIG-03..07 |
| TH-M2-07 | **Envelope-version downgrade** (v2 → v1 replacement) | §3.5 guard on all paths; M4 `MinimumWriteEnvelopeVersion` | v1 stays readable (history) | M2-ENV-12, M2-CMP-06 |
| TH-M2-08 | **Concurrent acceptance races** | Organization lock (same organization); UNIQUE (cross-organization); head CAS | Throughput: writes contend on the organization lock (existing) | M2-CON-01..04 |
| TH-M2-09 | **Replay of historical revisions or rows** | R-53 snapshot proof; head CAS; ledger refuses old ids as New | None identified | M2-REP-01..03 |
| TH-M2-10 | **Rollback to an unsafe application version** | Write fence; capability floor; CHECKs; startup guards | Honest-build protection only against A3 (§7.3) | M2-FEN-01..08 |
| TH-M2-11 | **Partial database restore** | G14 quarantine; ledger in AuthorityDb independent of BusinessDb; guarded Down; startup guards | Business data loss down to the RPO is quarantined (G14) | M2-RST-01..05 |
| TH-M2-12 | **Missing key metadata** (v2 row without id, commitment or code) | Signed non-nullable columns; CHECK; Authority shape rules; M1 reader refuses | None identified | M2-SIG-05, M2-ENV-02 |
| TH-M2-13 | **Unauthorized feature activation** (enable v2 or loosen lists) | Deployment-only switch; Production refuses; code-level allow-lists under release governance; no API | Development/Testing switch is single-operator | M2-AUZ-01..05 |
| TH-M2-14 | **Corrupted census data** | Computed in the Authority from verified bytes, in the head transaction; append-only; `Incomplete` fails closed; ledger ↔ summary cross-check | An AuthorityDb-administrator compromise is out of the Level 2 model | M2-CEN-01..07 |
| TH-M2-15 | **Authority/Business inconsistency** | No path trusts BusinessDb for bindings or census; capability floor; quarantine | — | M2-RST-03..05 |
| TH-M2-16 | **Ledger loss on AuthorityDb restore** | Ids in archived journal bodies; v2 disabled until rebuild; G14 gate | Prepare-only reservations are lost (only the decided "accepted" guarantee is kept) | M2-RST-02 |

---

## 10. Incremental implementation plan [PROPOSED]

Each increment is independently reviewable, needs its own owner authorization, keeps v1 fully operational, and never runs Stage B (C-131). No M3 catalog work is included.

### E3-M2a — Universal v1 envelope validation (Authority only)

| Aspect | Content |
|---|---|
| Scope | New `EnvelopeRules` (Authority Application, Content feature): §2.3 schema-1 rules applied to every encrypted candidate row on every path, at Prepare and Commit. `EnvelopeLooksApproved` also checks the version. Pre-check: a read-only census of existing development and sectest data for anomalies (F-11). |
| Files | `ContentService.cs`, new `EnvelopeRules.cs`, `ContentDeltaRules.cs` (shared constants), Authority tests |
| Schema | none. **Authority:** behaviour tightening only. |
| Dependencies | OD-M2-09 |
| Acceptance | Every existing v1 write path accepted; malformed v1 refused on owner, reprotection and attachment paths (previously accepted); no vector change; existing Authority suite green |
| Tests | M2-ENV-01..05 (v1 cases), M2-CMP-01..03 |
| Rollback | Redeploy the previous Authority binary (no database change) |
| Gate | Owner; **Authority behaviour change** |

### E3-M2b — Business schema, signed schema 2 registry, clone fidelity

| Aspect | Content |
|---|---|
| Scope | BusinessDb migration (§5); EF mapping and domain properties; `BusinessSignedSchemas` schema-2 entries; `Encode` dispatch on the stored schema version (S-2); clone helpers preserve schema and columns (S-3); `SignedColumnRegistryTests`; business required-migration startup guard (§7.4); schema-2 RowHmac conformance vectors in a **new** file. Writers still v1. |
| Files | `FormRecord.cs`, `UploadSession.cs`, both entity configurations, a new migration, `BusinessSignedSchemas.cs`, `UploadSessionHandlers.cs`, `CandidateBuilder.cs`, startup composition, architecture tests, integrity tests |
| Schema | Business: 3 columns × 2 tables + CHECKs. **Authority:** none. |
| Dependencies | M2a; OD-M2-01, -02, -14; OD-E3-4, OD-E3-14 |
| Acceptance | v1 bytes unchanged (vectors); schema-2 bytes match new Python/Node-reproduced vectors; clone of a v2 fixture row preserves all fields; the CHECK refuses version 2 with NULLs; guarded Down refuses with v2 rows present |
| Tests | M2-SIG-01..07, M2-CMP-04, M2-FEN-05/06, M2-ARCH-01 |
| Rollback | Binary rollback is safe (expand-only). Down is guarded and Development/Testing only. |
| Gate | Owner; **business migration approval** |

### E3-M2c — Business v2 readers (dark)

| Aspect | Content |
|---|---|
| Scope | `AesGcmFieldEncryptionService` dispatches schema 2 / envelope 2 to the M1 opener. `DecryptedFieldValues` groups by data-key id (one unwrap per batch). Document download and Worker v2 decrypt (streaming as v1; OD-E3-15 verify-then-release is M4). `EnvelopeV2IsolationTests` and `DecryptionCallSiteTests` updated with reasons. The Authority client sends the capability header. |
| Files | Infrastructure FormRecords/Documents, `DecryptedFieldValues.cs`, `DocumentHandlers`, Worker processing, architecture tests |
| Schema | none. **Authority:** none (an unknown header is ignored by pre-M2 Authorities). |
| Dependencies | M2b |
| Acceptance | v2 fixture rows (M1 core) open through the product reader; one unwrap per batch (V2-PRF-01 locally); v1 unchanged; no new decrypt call site unreviewed |
| Tests | M2-CMP-05, V2-RT/V2-ISO on the product path, M2-ARCH-02 |
| Rollback | Binary rollback (no v2 data exists outside fixtures) |
| Gate | Owner |

### E3-M2d — Authority schema-2 registration and v2 validation (acceptance hard-disabled)

| Aspect | Content |
|---|---|
| Scope | `ContentDeltaRules` schema-2 rule entries (column classes §3.2/§3.3); §2.3 v2 shape, §2.4 batch consistency, S-4 tie, §3.5 downgrade guard. **New** schema-2 rows are refused with `ENVELOPE_VERSION_NOT_ENABLED` (no switch yet). Carried schema-2 rows are validated. |
| Files | Authority Content feature, Authority tests, `SignedColumnRegistryTests` (cross-check) |
| Schema | none |
| Dependencies | M2b, M2c |
| Acceptance | The rules accept M1-core fixture rows and refuse each malformed variant (pure-function tests); Prepare refuses new v2 |
| Tests | M2-ENV-06..14 |
| Rollback | Binary rollback (no v2 accepted) |
| Gate | Owner; Authority change |

### E3-M2e — OD-E3-16 ledger and acceptance switch

> **E3-M2e contract closure (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §6).** E3-M2e is split into **E3-M2e-1** (dark ledger: ledger, constraints, CARV 3, floor-3 support, default-off switch, migration guard, error mapping, journal changes, focused tests; no acceptance activation) and **E3-M2e-2** (controlled acceptance verification in disposable PostgreSQL only). Each needs its own owner authorization. The scope below is refined by that record; where they differ, the record governs.

| Aspect | Content |
|---|---|
| Scope | AuthorityDb migration: `content_data_key_bindings`, insert-only grant; DK-1..DK-8; journal body `new_data_key_identifiers`; `Content:EnvelopeV2AcceptanceEnabled` (default off, Production refuses, Testing override); `envelope_rule_version` written on operations; `EnvelopeAcceptancePolicyObserved` event; Authority required-migration guard. Re-verify that `RestorePlanner` re-seals. |
| Files | Authority Domain/Application/Infrastructure (entity, configuration, store port, migration, grants), `ContentService.cs`, operator plane (Testing override), Authority tests; a **new** throwaway-PostgreSQL script for constraint and concurrency tests (OD-M2-12) |
| Schema | AuthorityDb: 1 table + 1 nullable column |
| Dependencies | M2d; OD-M2-03, -04, -05, -10, -15 |
| Acceptance | v2 fixture commits succeed with the switch on (Testing) and are refused off; every DK rule has a positive and a negative test; cross-organization concurrent duplicate refused by the real PostgreSQL UNIQUE |
| Tests | M2-DK-01..10, M2-CON-01..04, M2-IDM-01..05, M2-REP-01..03, M2-AUZ-01..05 |
| Rollback | The switch off stops new v2. Binary rollback below M2e is fenced only once M2f lands; until then, rolling back with the switch never enabled outside Testing is safe. |
| Gate | Owner; Authority change; **AuthorityDb migration approval** |

### E3-M2f — Rollback fences

| Aspect | Content |
|---|---|
| Scope | §7.2 trigger (or the F1 alternative); M2+ Commit refuses NULL `envelope_rule_version`; §7.3 capability floor; runbook entries; G08 inventory update |
| Schema | AuthorityDb: trigger + function |
| Dependencies | M2e; OD-M2-07, -08 |
| Acceptance | A simulated pre-M2 insert (NULL rule version) succeeds for an organization without bindings and fails for one with bindings; a client without the header is refused only for an organization with bindings; startup guards refuse a missing migration |
| Tests | M2-FEN-01..08 |
| Rollback | Dropping the trigger is Development/Testing only; production rollback below M2f is fenced by design |
| Gate | Owner; Authority change; G08 review |

> **E3-M2f contract closure (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` §11, §12).** This block is superseded:
> - The §7.2 trigger is not built (F0).
> - The NULL `envelope_rule_version` Commit rule is met by CARV (AM-7).
> - The startup guards were delivered in E3-M2b-1, E3-M2b-2 and E3-M2e-1.
> - E3-M2f is now the business capability floor only, split into E3-M2f-1 (business emission), E3-M2f-2 (Authority enforcement, no database change), E3-M2f-2G (disposable PostgreSQL verification) and E3-M2f-3 (durable refusal evidence, after a separate design decision).
> - G08 is unchanged under F0.
>
> Each increment needs its own owner authorization.

### E3-M2g — Envelope census

| Aspect | Content |
|---|---|
| Scope | §6 tables, commit-time summaries, verified backfill through the verifier workload, count queries (operator plane and Security Administrator), ledger ↔ summary cross-check |
| Schema | AuthorityDb: 2 tables |
| Dependencies | M2e; OD-M2-11 |
| Acceptance | Counts equal fixtures exactly; `Incomplete` while any commit is unsummarized; backfill quarantines tampered history |
| Tests | M2-CEN-01..07 |
| Rollback | Tables are additive; queries can be withdrawn |
| Gate | Owner |

**Order rationale:** tighten v1 first, then readers before acceptance, then acceptance before fences (fences are tested against real bindings), then the census last (it relies on the ledger). An owner who wants fences before any binding exists can swap M2e and M2f, at the cost of testing the fence against fixture bindings inserted by migration-identity SQL.

---

## 11. Focused test strategy [PROPOSED]

Focused runs only: run the test executable directly with `--filter-class`. Real-PostgreSQL constraint tests use a **new throwaway-container script** on the existing E-2/E-5 pattern, never the developer stack or Stage B. Existing suites that must stay green unmodified: E-3 §8.3 list, `ContentDeltaRulesTests`, `ContentItemIdentityTests`, `ReprotectionTests`, `IntegrityKeyActivationTests`, `EnterpriseForms.Integrity.Tests`, `EnvelopeV2*Tests`, `EncryptionKnownAnswerTests`.

| Group | Tests (each a positive and a negative where meaningful) |
|---|---|
| **M2-CMP** v1 and schema-1 compatibility | 01 every v1 write path (owner, share, reprotection, upload, lifecycle, creation) accepted after M2a; 02 v1 reads byte-identical; 03 RowHmac v1 / Merkle v1 vectors unchanged; 04 schema-1 rows verify after M2b; 05 product readers open v1 and v2 in one record; 06 a v2 item cannot be replaced by v1 (downgrade) |
| **M2-SIG** signed schema 2 | 01 schema-2 canonical bytes match new cross-language vectors; 02 registry order and types; 03 `Encode` uses the stored schema; 04 clone helpers preserve schema and columns (architecture test over every mapped property); 05 NULL v2 column refused by the canonicalizer; 06 schema 1 + envelope 2 refused (Authority, business reader); 07 schema 2 + envelope 1 refused |
| **M2-ENV** acceptance and malformed metadata | 01–05 v1 shape on every path (algorithm, lengths, reference grammar, organization and purpose agreement, F-11 empty-ciphertext behaviour); 06 well-formed v2 field batch accepted (switch on); 07 v2 document accepted; 08 every v2 column malformed in turn (wrong length, non-UUIDv7 id, unknown wrap code, family ↔ code mismatch, legacy reference, foreign organization, wrong purpose, non-canonical version); 09 document size formula (D0–D4 and off-by-one); 10 batch inconsistency (differing commitment, wrap or reference; mixed kinds; two documents per id; repeated item; repeated nonce); 11 wrap codes refused outside Development/Testing; 12 downgrade guard; 13 new v2 with switch off → `ENVELOPE_VERSION_NOT_ENABLED`; 14 error texts carry no identifiers |
| **M2-DK** data-key uniqueness | 01 a new batch is bound at Prepare; 02 accepted at Commit (derived); 03 a later candidate reusing an accepted id as New → refused; 04 reusing the id of an aborted or expired attempt → refused (OD-M2-03); 05 the same id from another record → refused; 06 another organization → refused; 07 a new item joining an existing batch (DK-3) → refused; 08 a carried row with any Content column changed (nonce, ciphertext, wrap) becomes New → refused; 09 duplicate id inside one candidate across groups; 10 Rewrap-shaped change (same id and commitment, new wrap) → refused in M2 |
| **Carried-forward** | Within M2-DK: share edit carries v2 items unchanged; upload commit, lifecycle transition and protection-migration unchanged rows carry v2 items; a carried row from a **non-current** revision → refused |
| **M2-CON** concurrency | 01 two prepares in one organization with the same id: the second refused under the lock; 02 two organizations concurrently with the same id: one commits, one gets 422 (real PostgreSQL); 03 a record edit and a share edit race on the head: the loser's bindings stay reserved and it re-stages fresh; 04 a crash after Prepare (fault injector) then retry with a fresh batch |
| **M2-IDM** idempotent retries | 01 Prepare replay with the same key → same operation, no new binding; 02 a conflicting replay → 409; 03 Commit replay of a committed operation → head, no new binding; 04 lost-acknowledgement repair path unchanged; 05 a retry with a new operation and the same batch → refused |
| **M2-REP** historical replay | 01 row copied from revision n−2 into a new candidate → refused; 02 whole older snapshot → refused (existing CAS, reasserted); 03 history reads of v2 revisions unaffected by the ledger |
| **M2-FEN** rollback fences | 01 NULL rule-version insert allowed without bindings; 02 refused with bindings; 03 M2+ Commit aborts an outdated Prepared operation; 04 missing capability header refused only where bindings exist; 05 business CHECK refuses version 2 with NULLs (pre-M2 clone simulation); 06 guarded Down refuses with v2 present; 07 business startup guard (migration missing) refuses readiness; 08 Authority startup guard likewise |
| **M2-RST** restore divergence | 01 BusinessDb rows rolled back: a re-staged New with a previously accepted id is refused; 02 AuthorityDb restored before a v2 commit: bindings recovered from archived bodies (archive-replay unit test); v2 stays off until rebuild; 03 business ahead of Authority → quarantine (existing), census `Incomplete`; 04 Authority ahead of business → G14 awaiting, no binding trusted from BusinessDb; 05 ledger rebuild by verified sweep equals the original |
| **M2-CEN** census | 01 commit-time summary counts equal the fixture (v1 only, v2 only, mixed, documents); 02 key uses per version; 03 historical dependencies include superseded revisions; 04 backfill of pre-M2 commits verified; 05 tampered history → quarantine, not counted; 06 a missing summary → `Incomplete`; 07 ledger ↔ summary cross-check detects an injected mismatch |
| **M2-AUZ** unauthorized activation | 01 Production-designated Authority with the switch on refuses to start; 02 the Testing override is refused outside Testing; 03 no API, organization setting or form JSON changes the allow-lists (architecture test: constants only); 04 the runtime role cannot UPDATE or DELETE `content_data_key_bindings` (real PostgreSQL grants); 05 a policy change emits `EnvelopeAcceptancePolicyObserved` |
| **M2-ARCH** | 01 clone-helper property coverage; 02 decrypt call-site allow-list reasons; 03 Authority rule copies cross-checked against business constants and the reference grammar; 04 no Authority reference to business Infrastructure |

**Not run in M2:** Stage B shards (manual and on demand); Azure; real HSM; PB-09 performance; V2-IND-01.

> **E3-M2f contract closure (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` §11.1, §13).** The M2-FEN group is reassigned:
> - M2-FEN-01/-02 (F2 trigger): withdrawn (F0).
> - M2-FEN-03: met by CARV.
> - M2-FEN-05..08: met by E3-M2b-1, E3-M2b-2 and E3-M2e-1.
> - M2-FEN-04: replaced by M2F-01..M2F-40.

---

## 12. Open decisions requiring owner approval

| ID | Decision | Recommendation |
|---|---|---|
| OD-M2-01 | Correct E-3 §3.8/§9: M2 columns on **two** tables; `upload_sessions` and creation attempts unchanged (F-02, F-03) | Approve |
| OD-M2-02 | Store `key_wrap_algorithm_code` (N1) or derive it from the reference family (N4) | Store it (signed, 4 bytes; no registry-dependent key schedule) |
| OD-M2-03 | Bind data-key ids at **Prepare** (one id, one operation, including aborted attempts) or only at Commit (literal "accepted") | Prepare (stricter; closes R-M1-8 across retries) |
| OD-M2-04 | Ledger uniqueness global or per organization | Global |
| OD-M2-05 | Carried-forward exemption from the **current head only**, or from any earlier accepted revision of the same item | Current head only (restores re-seal) |
| OD-M2-06 | Enforce the per-item v2 → v1 downgrade guard from M2 | Yes. Consequence: records with dark v2 fixtures are owner-uneditable until M4 (no production v2 exists) |
| OD-M2-07 | Authority fence: data-conditional per organization (F2) or unconditional S1-style (F1) | F2 (permits rollback while compatible) |
| OD-M2-08 | Business capability floor per organization or per record | Per organization |
| OD-M2-09 | Apply universal v1 envelope validation to **all** candidate rows, including carried, after a read-only census of existing data (F-11) | Yes, as M2a |
| OD-M2-10 | Acceptance-switch governance (§8): config only, Production refuses, Testing override; re-enable after suspension needs two operators outside Development/Testing | Approve |
| OD-M2-11 | Census readers: Platform Operator counts only; Security Administrator per-record | Approve |
| OD-M2-12 | New throwaway-PostgreSQL test script for constraint, grant and concurrency tests (M2e/M2f) | Approve at M2e |
| OD-M2-13 | V2-IND-01 timing (E-3 §13.8 item 5) | Before M4 (unchanged) |
| OD-M2-14 | Schema-2 RowHmac vectors in a **new** vector file; existing vector files untouched | Approve |
| OD-M2-15 | Extend the archived `ContentHeadCommitted` body with `new_data_key_identifiers` | Approve |
| OD-M2-16 | Regenerate the specification manifest to list this document (`scripts/generate-spec-manifest.py`) | Authorize separately; **not done in this task** |

Still open from E-3 §13.8 and prerequisites for M2b/M2d/M2e:
- item 2: authorize an Authority behaviour change;
- item 3: authorize the business migration;
- item 4: OD-E3-4 (W1, wrap in the signed row) and OD-E3-14 (batch scope (record, operation, purpose)). M2's schema fixes both.

> **Decided for E3-M2e (2026-11-15; `E3_M2E_CONTRACT_CLOSURE.md` §1.2):**
> - OD-M2-03: Prepare (OD-M2e-02).
> - OD-M2-04: global (OD-M2e-01).
> - OD-M2-10: decided as OD-M2e-07.
> - OD-M2-15: amended to the full binding tuple (OD-M2e-10, AM-3).
>
> OD-M2-05 was already decided for classification (E3-M2d D-7).

> **Decided for E3-M2f (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` §1):** OD-M2-07 = F0 (OD-M2f-02); OD-M2-08 = per organization (OD-M2f-03).

---

## 13. E3-M2a implementation record (Development/Testing; owner-led review)

**Authorization:** owner, 2026-11-14. It covers E3-M2a only, plus the cross-revision reuse remediation authorized at the final acceptance check. No database migration, signed schema 2, Envelope v2 activation, data-key ledger, rollback fence, key-provider, E-9 or production-configuration change was made.

### 13.1 Read-only compatibility assessment (before any behaviour change)

Two persisted business databases were assessed: the developer stack and the older default-project database.
- **Method.** Each volume was copied into a scratch volume, with the source mounted read-only and never started. The copy was queried by a throwaway PostgreSQL 18 container with no network, returning aggregates only. The scratch containers and volumes were deleted.
- **Rows.** 1,038 encrypted field rows and 184 document-version rows (Committed and staged Processed).
- **Shape.** All of them match the v1 shape enforced below. Every row names the legacy reference `local-dev-kek-v1`.
- **Anomalies.** No empty ciphertext, no empty plaintext strings, and no repeated wrapped key, or (wrapped key, nonce) pair, within an operation.
- **Not assessed.** The security-test stack keeps no persisted business data. Scoped references (`localdev:` / `vault-transit:`) were validated against the business code and by an executed cross-component test, not against stored rows.

### 13.2 Rules (`EnvelopeRules`, Authority)

| Rule | Where |
|---|---|
| Shape, every encrypted row: envelope version 1, `AES-256-GCM`, wrapped key 60 bytes, key reference = legacy literal or canonical `localdev`/`vault-transit:{organization}:{purpose}:{version}` (organization = the row's; purpose `field-value` for field rows, `document` for document rows). Field rows: nonce 12, tag 16, non-empty ciphertext, closed logical type. Document rows: nonce prefix 8, segment size 1,048,576. | Prepare (every candidate row, every write path), Commit (verified staged rows), census |
| Within one candidate: no wrapped data key shared by two field rows (`ENVELOPE_NONCE_REUSED` if the nonce is shared too, `ENVELOPE_WRAPPED_KEY_REUSED` otherwise); v1 is one data key per value | Prepare, Commit, census |
| Against the **verified current revision** (the snapshot Prepare proves equal to the accepted head, never business rows): a field row whose wrapped key appears in the current revision is accepted only as the byte-identical carry of the same item (every signed column equal except the operation binding); otherwise refused with the reason above | Prepare, every write path |

- **Refusals.** `422` invalid command, detail `ENVELOPE_INVALID: <reason>`, with no identifiers. A refusal at Prepare happens before anything is signed or stored. At Commit, the refusal aborts the operation (`ENVELOPE_INVALID_AT_COMMIT`); this covers an operation prepared by an older Authority build.
- **Unchanged.** The v1 format, signed schemas, Merkle and RowHmac vectors.
- **Envelope v2.** Still refused: version 2 and schema-2 rows are rejected.

### 13.3 Write paths covered

| Path | Evidence |
|---|---|
| Record creation | `ARecordCreatedWithAMalformedEnvelopeIsRefusedBeforeAnythingIsSigned` (12 variants), `ARecordCreatedWithTwoValuesUnderOneWrappedKeyIsRefusedBeforeSigning` |
| Owner edits | `AnOwnerEditIntroducingAMalformedEnvelopeIsRefusedAndTheHeadStays` (12), `OwnerChangedCiphertextUnderTheCurrentKeyAndNonceIsRefusedBeforeSigning`, `OwnerReuseOfTheCurrentKeyUnderANewNonceIsRefusedBeforeSigning`, `AnotherItemCannotTakeTheCurrentKeyEvenWhenTheOriginalItemIsRemovedOrReplaced`, `OwnerByteIdenticalCarryForwardAndFreshEncryptionAreBothAccepted` |
| Share-editor edits | `AShareEditorCannotIntroduceAnEnvelopeTheRulesRefuseButAWellFormedUpdateStillCommits`, `AShareEditorCannotReuseTheCurrentKeyOfItsItemOrOfAnotherItem` |
| Protection migration (workload) | `AProtectionMigrationWriteWithAMalformedEnvelopeIsRefusedBeforeSigningAndAWellFormedOneCommits` (4), `AProtectionMigrationWriteCannotReuseADataKeyOfTheCurrentRevision` (3) |
| Commit of an operation prepared without the rules | `ACommitOfAnOperationPreparedWithoutTheRulesIsRefusedAndTheOperationAborted` |
| Concurrent / stale head | `AStaleHeadCannotBypassTheReuseRule`: Commit's head compare-and-set refuses the stale operation; re-prepared on the new head, it is refused by the reuse rule |
| Census | `EnvelopeShapeAndNonceReuseAreReportedAndBlockCompletion` (pre-E3-M2a history seeded by a test-only helper) |
| Business-produced envelopes | `EnvelopeRulesBusinessCompatibilityTests` (real field and document encryption services; constants and reference grammar cross-checked) |

"Before signing" is asserted with a counting decorator around the real HMAC provider: the signature count is unchanged by a refusal.

**Mutation checks** (each source file restored and verified identical by SHA-256):

| Mutation | Tests that failed |
|---|---|
| Prepare rule removed | 30 write-path tests |
| Commit rule removed | the commit test |
| Census rule removed | the census test |
| Cross-revision rule removed | all 8 cross-revision tests |
| Within-candidate rule weakened to "same pair only" | the 2 within-candidate tests |

### 13.4 Remaining limitations (accepted for Development/Testing)

- **R-M2a-1, historical v1 reuse.** The Authority keeps only digests of history, so a new value reusing a wrapped key (and nonce) from an **older, no longer current** revision is not detected. The business writer draws a fresh data key per value, and the stored data shows no reuse. The v2 data-key ledger (§4, OD-E3-16, E3-M2e) is the designed remedy for v2. Historical v1 key-use tracking is out of scope.
- **R-M2a-2, the same data key under a different wrap.** The Authority compares wrapped bytes. The same data key wrapped twice (a fresh wrap nonce) is not detectable without the key.
- **R-M2a-3, operations already prepared at deployment.** An operation prepared by an older Authority build and committed after the upgrade gets the shape and within-candidate rules at Commit, but not the cross-revision rule, because Commit has no current bytes. The window is bounded by the 10-minute operation lifetime.
- **R-M2a-4, documents across revisions.** v1 document rows are not compared across revisions (one data key per document version; carried versions keep their wrap).
- **R-M2a-5, legacy rows.** A malformed or key-sharing row accepted before E3-M2a (none found) is still readable and reported by the census. No write may carry it; the value must be re-sealed.
- **R-M2a-6, scope of the assessment.** Only the two local databases above were assessed. Any other environment needs the same read-only assessment before deployment.

---

## Sources inspected

- **Authority:**
  - `ContentService.cs` (Prepare, Commit, verification, census, `EnvelopeLooksApproved`, `ParseCandidate`, `ClassifyMutations`);
  - `ContentDeltaRules.cs`, `ContentItemIdentityRules.cs`, `ReprotectionRules.cs`, `ShareContributorWriteSwitch.cs`;
  - `SearchPostingDerivation.cs`, `SearchAccessJournalVerifier.cs`;
  - `AuthorityEntityConfigurations.cs`, `AuthorityRuntimeGrants.cs`, `MigrateCommand.cs`, migration `20261104231152_ProjectionIntegrityS1`.
- **Business:**
  - `BusinessSignedSchemas.cs`, `FormRecord.cs`, `UploadSession.cs`, `FormRecordCreationAttempt.cs`;
  - `FormRecordEntityConfigurations.cs`, `DocumentEntityConfigurations.cs`, migration `20261024140510_RecordCreationAttempts`;
  - `CandidateBuilder.cs`, `UploadSessionHandlers.cs`, `SharedFieldWriteBuilder.cs`, `IFieldEncryptionService.cs`, `DecryptedFieldValues.cs`, `AesGcmFieldEncryptionService.cs`, `AesGcmFileEncryptionService.cs`, `EnvelopeV2/DocumentEnvelopeV2.cs`.
- **Integrity:** `RowCanonicalHeader.cs`, `docs/dynamic-forms/row-integrity-format.md`.
- **Documents:** `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` (§1, §3, §5, §6, §7, §9, §11, §13), `PROJECTION_INTEGRITY_S1_OPERATIONS.md`, `E0`–`E6A` records (through E-3), ADR-PB-011/012.
- **Tests located:** `tests/authority/AuthorizationAuthority.Tests` (in-memory store), `tests/architecture` (`SignedColumnRegistryTests`, `DecryptionCallSiteTests`, `EnvelopeV2IsolationTests`), `tests/integrity`, `tests/security` (sectest stack).

No tests were run for this package. It is a design; every claim of existing behaviour comes from reading the source at `de1729b`.
