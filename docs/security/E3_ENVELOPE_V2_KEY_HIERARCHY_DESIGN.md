# E-3 Envelope Encryption v2 and Key Hierarchy — Design Review Package

**Status (2026-11-14):**

| Item | State |
|---|---|
| Design direction (E3-D1 to E3-D14) | **Conditionally accepted** by the owner. This is not approval of the complete E-3 design, of production cryptographic integration, or of any milestone after E3-M1. |
| E3-M1 (isolated Envelope v2 core and known-answer vectors) | **Accepted** by the owner for Development/Testing (2026-11-14), with the documented residual risks, after the final acceptance gate (§13.10). Local signed commit on branch `e3/m1-envelope-v2-core` authorized; not pushed or merged. Owner-led acceptance, not an independent review. |
| E3-M2 to E3-M6 | **Not authorized** |
| Authority encryption-key catalog (§5.1, E3-M3) | **Not authorized** |
| E-9 profile implementation (§4) | **Not authorized** |
| Production use of Envelope v2 | **Not approved.** E-3 is not production-approved. |
| Independent cryptographic review (V2-IND-01) | **Required** before any production claim; not started |

The core is not connected to any production path (§13.6). E-6 Managed HSM remains experimental.

**Review:** this is an owner-led design review. It is **not** an independent security review and **not** a certification. The E-0 to E-6 work it builds on is Development/Testing only, with owner-led review.

**Baseline:** local `main` at `d6693838de4e7c2c421fe3f449cecb721d4fced2` (FD-01 merged; E-0, E-1, E-2, E-4, E-5 and E-6 integrated by `5bc0702`). The working tree was clean when the inspection began.

**Notation:**
- **[EXISTING]** is implemented and accepted behaviour, verified in the source cited.
- **[PROPOSED]** is new in E-3 and needs owner approval.
- **[E-9]** is the approved E-9 direction. No E-9 record exists in the repository: the requirements used here are those stated in the owner's E-3 brief, §2C. They should be reconciled with the E-9 record when it is written (OD-E3-1).
- **[REAL-HSM]** / **[AZURE]** can be settled only against real Azure services, which needs separate authorization.

Identifiers used in this document:
- decisions E3-D1 to E3-D14;
- open decisions OD-E3-1 to OD-E3-16 (OD-E3-15 and OD-E3-16 decided by the owner at E3-M1 acceptance, 2026-11-14);
- threats TH-01 to TH-16;
- milestones E3-M0 to E3-M6;
- tests V2-KAT-*, V2-RT-*, V2-ISO-*, V2-TMP-*, V2-ROT-*, V2-REC-*, V2-CMP-*, V2-PRF-*, V2-PROF-*.

---

## 0. Summary

E-3 introduces five things that E-0 to E-6 did not provide:

1. **A trusted encryption-key catalog in the Authority** (E3-D4). Today the current and retained encryption-key versions exist only in each host's configuration (E-4). No trusted record, no inventory of referenced versions and no retirement state exist. The catalog is also the enforcement point that E-9 profiles need.
2. **Envelope v2** (E3-D2), a versioned, canonically serialized envelope with:
   - an explicit format, algorithm and wrap-algorithm identification;
   - a data-key identifier and a **key commitment**;
   - **provider-neutral context binding**: the data key is derived with HKDF from a wrapped seed, so binding does not depend on the key provider supporting associated data;
   - the full envelope header authenticated as AEAD associated data;
   - domain separation for documents (v1 document AAD has no domain label).
3. **Seal-batch data keys for field values** (E3-D3). This replaces one data key, and one KMS round trip, **per value** with one per (record, content operation, purpose). Per-item keys are derived with HKDF. A 250-field record read then costs one unwrap instead of up to 250.
4. **Governed rotation operations** (E3-D6, E3-D7):
   - KEK activation through the Authority catalog;
   - an inventory of referenced versions (census);
   - explicit `Reseal` (field) and `Rewrap` (document) content operations built on the existing reprotection engine;
   - a version lifecycle with retirement and a recovery-aware destruction rule.
5. **The E-9 boundary** (E3-D10). An immutable organization profile, owned by E-9, constrains which provider family a key registration may name. The business host routes each organization to exactly one provider, with no fallback.

E-3 keeps the following unchanged:
- AES-256-GCM as the payload AEAD;
- the two-tier model of a non-exportable provider KEK over application-generated data keys;
- the wrapped key inside the signed row;
- RowHmac v1 and Merkle v1 algorithms;
- the Confidential-by-default rules;
- the search rules (no ciphertext-derived tokens);
- every E-4 and E-5 guarantee (§5.6).

v1 ciphertext is never rewritten implicitly (§8).

**Recommendation (§12):** approve the design direction (E3-D1 to E3-D14) and milestones E3-M1 and E3-M2. Approve M3 to M6 individually later. Resolve OD-E3-1 to OD-E3-5 before M3.

---

## 1. Current-state architecture [EXISTING]

### 1.1 Payload envelopes

| Envelope | Where | Data key | Nonce | AAD | Stored |
|---|---|---|---|---|---|
| Field value v1 | `AesGcmFieldEncryptionService` | random 256-bit DEK **per value** | random 96-bit | `"DynamicForms/FieldValue/v1"` ‖ Int32BE(1) ‖ tenant ‖ record ‖ field ‖ item (UUIDs big-endian) ‖ Int32BE(len) ‖ logical type | `field_encrypted_values`: `encrypted_value_bytes`, `encryption_nonce_bytes` (12), `encryption_auth_tag_bytes` (16), `wrapped_data_key_bytes`, `encryption_algorithm_name` (`AES-256-GCM`), `encryption_key_reference`, `encryption_envelope_version` (1), `encrypted_logical_type` |
| Document v1 | `AesGcmFileEncryptionService` | random DEK per attachment **version** | 8-byte random prefix ‖ UInt32BE segment counter | Int32BE(1) ‖ tenant ‖ record ‖ attachment ‖ Int32BE(version) ‖ UInt32BE(segment) ‖ final flag. **No domain label.** | `file_attachment_versions` (and the processed copy in `upload_sessions`): algorithm, key reference, envelope version, `wrapped_data_key`, `encryption_nonce_prefix`, `encryption_segment_size_bytes` (1 MiB), ciphertext/plaintext SHA-256 and sizes. Object store: `[UInt32BE len][ciphertext][tag]` repeated. |
| Idempotency fingerprint key | `CreationRequestFingerprint` | field envelope v1, purpose `IdempotencyProtection`, fixed seal purpose id | as field | as field | creation-attempt row |
| Security Auditor reason note | `AesGcmAuditorNoteSealer` | per-note DEK, purpose `AuditorReasonNote` | random | own domain; organization, target, role, direction, note id | JSON envelope v1 in the Authority journal. Seal only; no decrypt anywhere. Validated by `AuditorReasonNoteEnvelopeValidation` (version 1, `AES-256-GCM`, key reference `^[A-Za-z0-9._:/-]{1,128}$`, 2,048-byte ciphertext). |

These formats are pinned by known-answer vectors:
- E0-6 (F1, F2, documents);
- E1 K1–K4 (keyring wrap);
- KAT-M1-01 (reason note).

All were independently cross-checked in Python.

### 1.2 Key wrapping (E-1, E-2, E-4)

- **`IEncryptionKeyProvider`** (`Application/Features/Documents/Ports/DocumentPorts.cs`):
  - `WrapDataKeyAsync(DataKeyScope(org, purpose), dek)` wraps under the scope's **current** version and returns the exact reference;
  - `UnwrapDataKeyAsync(scope, reference, wrapped)` resolves exactly one retained version of exactly that scope;
  - there is no fallback and no substitution.

  `EncryptionKeyPurpose` is a closed set: `FieldValue`, `Document`, `AuditorReasonNote`, `IdempotencyProtection`.
- **Providers:**

  | Provider | KEK | Wrap AAD |
  |---|---|---|
  | `LocalDevelopmentKeyProvider` | HKDF-derived | binds scope and version; legacy `local-dev-kek-v1` is decrypt-only and binds the reference only |
  | `VaultTransitKeyProvider` | `aes256-gcm96` key per (organization, purpose) | `associated_data` binds scope and version |
  | `UnavailableKeyProvider` | — | — |

  Both real providers are refused outside Development/Testing (E0-1). Exactly **one** provider is registered per host.
- **References:** `localdev:{org}:{purpose token}:{version}` and `vault-transit:{org}:{purpose token}:{version}`. Wrapped keys are 60 bytes: nonce, 32-byte ciphertext, tag.
- **Versions:** per-host configuration tables (`CurrentVersion`, `RetainedVersions`, `DisabledVersions`, and local `ProvisionedVersions`), read at startup.
  - Provisioning and activation are separate operator steps (E-4).
  - There is no trusted catalog, no inventory of referenced versions, no retirement, no rewrap and no re-encryption.
  - Hosts must carry identical tables.

### 1.3 Integrity and the Authority's role in encryption

- **Signed rows.** RowHmac v1 signs every envelope column, **including `wrapped_data_key_bytes` and `encryption_key_reference`** (`BusinessSignedSchemas.EncryptedColumns`, `AttachmentColumns`). The Merkle manifest covers each row. Any change to a wrap is therefore a **new content revision**.
- **The Authority holds no encryption key** and cannot decrypt. It enforces envelope **shape** (`ContentDeltaRules`, `ContentService.EnvelopeLooksApproved`, census):
  - `AllowedEnvelopeVersions = {1}` and `AllowedEnvelopeAlgorithms = {"AES-256-GCM"}`;
  - a 12-byte nonce, a 16-byte tag, non-empty ciphertext, wrapped key and reference;
  - a unique (wrapped key, nonce) pair per revision;
  - in share edits (`SHARE_ENVELOPE_REUSED`), a new nonce and a new wrapped key.
- **Reprotection** (B-28, Amendment A1, `ReprotectionRules`) is the only content change the Authority accepts without a record-permitted user. It replaces one field's rows between plaintext and ciphertext, with the same item identity, under an effective `PublishFormSecurityRevision` command. G19 adds census and attestation by the separate `field-protection-verifier` workload.
- **Integrity-key catalog (E-5).**
  - `IntegrityKeyRegistration` per organization and purpose (`row-integrity-v1`), holding the provider, the key name, `CurrentSigningKeyVersion`, `MinimumVerificationKeyVersion` and a pause flag.
  - Activation is a compare-and-set under the organization lock, with a provider probe and an audit-stream event.
  - Onboarding registers this key at `Initialize`. Onboarding runs PendingApproval → AwaitingKeyCustody → Initialized, with two Platform Operator approvals of a frozen statement.
  - **Business encryption keys are not part of onboarding or of any Authority state.** Each organization's Vault encryption keys are provisioned by a separate operator script.
- **Organization data-protection policy.** `OrganizationProtectionPolicy` (the "encryption floor", minimum field classification) is set by the Organization Owner through the `UpdateMandatoryPolicy` command. It governs *what* is encrypted, not *with which provider*. It is distinct from the E-9 profile.

### 1.4 Other relevant facts

- **Confidential defaults [EXISTING, unchanged by E-3]:**
  - `FieldProtectionPolicy` defaults unclassified free text to `ApplicationEncrypted` and never downgrades.
  - Documents are always encrypted, whatever the field label (DOCUMENT_ENCRYPTION_AUDIT §2 Q5).
  - Encrypted values have no lookup index (ADR-PB-004).
- **Read cost.** `DecryptedFieldValues.DecryptAsync` decrypts rows one by one, so each encrypted value costs one sequential `UnwrapDataKeyAsync`, which is one provider round trip with Vault or an HSM. Provider latency has **not** been measured (E-6a §12).
- **Decryption call sites** are allow-listed by `DecryptionCallSiteTests` (18 reviewed sites). Decryption failures emit one `DECRYPTION_FAILED` audit event (E0-4).
- **E-6.** A Managed HSM computed raw-input HS256 matching the existing providers, for the tested inputs, by undocumented behaviour. 0 bytes gave 400 and 64 KiB gave 413. It is **not production-approved**, and **no wrap/unwrap (encryption) operation was tested on an HSM**.
- **Production key providers do not exist** for either encryption or integrity.

### 1.5 What E-3 must add, exactly

| Gap | Evidence | E-3 answer |
|---|---|---|
| No trusted record of encryption-key versions; per-host tables; no inventory; no retirement | E-4 limitations | Authority encryption-key catalog (E3-D4) and census (E3-D7) |
| Provider-specific binding: the wrap is scope-bound only where the provider supports AAD (local, Vault); Managed HSM AES-KW and Key Vault RSA-OAEP have no AAD | provider code; [DOC] key-operation lists | HKDF context binding and key commitment in v2 (E3-D2) |
| No key commitment; AES-GCM is not key-committing | v1 format | commitment field (E3-D2) |
| N provider round trips per record read | `DecryptedFieldValues` | seal-batch data keys (E3-D3) |
| No organization-level profile or provider binding; one provider per host | E0-1, E-2 | E-9 boundary and per-organization router (E3-D10) |
| No rotation of existing data; no retirement; no destruction rule | E-4, E-5 | E3-D6, E3-D7, E3-D8 |
| Document AAD lacks domain separation | v1 document AAD | v2 header magic and kind (E3-D2) |
| Recovery of encryption-key state not designed (G14 covers HMAC and authorization) | COORDINATED_DISASTER_RECOVERY | §6 |

---

## 2. Proposed key hierarchy [PROPOSED]

### 2.1 Tiers

```
Organization  (tenant; E-9 profile fixed at activation)                     AuthorityDb: profile + catalog
 └─ Purpose KEK  per (organization × purpose × KEK version)                 provider-resident, non-exportable
      purposes: field-value | document | auditor-reason-note | idempotency-protection
     └─ Data-key seed  (256-bit, CSPRNG, one per seal batch)                stored only wrapped, in the signed envelope
          └─ PRK = HKDF-Extract(seed)                                       memory only, per operation
               ├─ key commitment            (stored, signed)
               ├─ field item key  per (record, field, item, logical type)   memory only, single use
               ├─ document key    per (record, attachment, version)         memory only, single use
               └─ sealed-key key  (idempotency fingerprint key)             memory only, single use
```

Integrity HMAC keys (`row-integrity-v1`) form a **separate, unchanged** hierarchy owned by the Authority (E-5). E-9 profiles govern both hierarchies (§4). E-3 changes only the encryption hierarchy.

### 2.2 Decisions

**E3-D1 Two provider tiers, no cached intermediate key.**
- The only long-lived keys are provider-resident, non-exportable purpose KEKs.
- An intermediate application-held organization key (a KEK cached in memory and wrapping data keys locally) is **rejected**. It would put long-lived wrapping-key plaintext in the business process, which E-2 prohibits ("caching wrapping keys is prohibited"). It would also make the business host, not the provider, the place where organization isolation holds.

E3-D3 removes the performance reason for an intermediate tier.

**Organization isolation**, stated precisely:

| Layer | Isolation it gives |
|---|---|
| Cryptographic | Distinct provider keys per (organization, purpose). The organization and purpose are also bound into the reference, the HKDF derivation and the AEAD associated data. |
| Against a database-only attacker | Substitution across organizations, purposes, records, items and kinds fails (§7). |
| Against the business runtime | **Logical only.** One business workload identity holds wrap and unwrap rights on every organization's KEKs. This is true today and stays true under E-3. Per-organization identities or HSM pools would change it (OD-E3-9). |

**E3-D2 Envelope v2** (specified in §3).

**E3-D3 Data-key granularity.**

| Payload | v1 | v2 |
|---|---|---|
| Field values | one data key per value | **one data-key seed per seal batch** = (record, content operation, purpose); each item uses an HKDF-derived item key |
| Documents | one per attachment version | unchanged: one per attachment version (a document version is its own batch) |
| Idempotency fingerprint key | per attempt | per attempt (batch of one) |
| Reason note | per note | per note; envelope unchanged (E3-D12) |

Options evaluated for field values:

| Option | Provider calls per record read | Rotation and history | Verdict |
|---|---|---|---|
| F1 per value (v1) | one per encrypted value (up to 250 per record at PB-09 limits, 250 child records each) | simple | Kept readable; no longer written |
| **F2 per seal batch** | one per distinct batch referenced by the current revision. An owner save re-seals every carried value (existing E-1 behaviour), so this is usually 1, and grows only by share edits since the last owner save. | the data key is single-use per batch; item keys are single-use per item | **Recommended** |
| F3 per record, long-lived | 1 | needs a mutable record-key table outside signed rows, cross-revision key reuse, and its own integrity scheme | Rejected (reasons as E3-D5 W2) |

Authorization still precedes decryption under F2:
- The verified reader returns only the authorized subset (existing).
- A seed is unwrapped only if at least one **authorized** item references it.
- Item keys are derived only for authorized items.

The business host could already decrypt everything it can unwrap, so F2 does not widen the trust boundary.

**E3-D4 Authority encryption-key catalog** (see §5.1).
- The catalog holds names, versions, states and provenance, **never key material**.
- The business host keeps doing wrap and unwrap itself.
- The Authority never gains an encryption-key operation.

**E3-D5 The wrapped seed stays inside the signed envelope.** Option W1 is recommended; W2 is rejected for E-3.

- **W1** (recommended): the wrapped seed and reference remain signed row columns, as in v1.
  - A rewrap or re-encryption is a new content revision through the Authority.
  - Historical revisions keep their wraps.
  - Coverage by RowHmac and Merkle is complete.
  - No new unsigned security-relevant table is introduced (CLAUDE.md §4L.1).
- **W2** (rejected): a separate mutable `data_key_wraps` table, with the signed row holding only a data-key id and commitment.
  - It would allow rewrapping history without content revisions.
  - But it adds an unsigned side table outside the manifest, a new availability attack (delete or roll back wraps), and a second integrity scheme.
  - Its main benefit, retiring a KEK version that history still references, is limited anyway: backups also hold old wraps, so KEK retention must cover backup retention in either design (E3-D8).

  W2 can be revisited as a separate decision if KEK-version destruction for long-lived history becomes a requirement (OD-E3-4).

**E3-D6 Rotation semantics** (§5): activation, re-sealing and rewrapping as explicit, governed operations.

**E3-D7 Census of referenced KEK versions.** This extends the G19 census and attestation. It reuses the verifier workload, counts from verified canonical bytes, and adds no write path.

**E3-D8 Version lifecycle and destruction rule** (§5.3).

**E3-D9 Fail closed everywhere.** There is no provider fallback, no "latest" version, no plaintext fallback and no silent repair (unchanged rule, restated for v2).

**E3-D10 The E-9 boundary** (§4).

**E3-D11 Search unchanged.**
- v2 adds no searchable artefact.
- The key commitment is a PRF output of a random per-batch seed, so equal plaintexts give unrelated commitments, nonces and ciphertexts.
- `data_key_identifier` only reveals which items were sealed together, which the operation id already reveals.
- No blind index, hash or deterministic token is introduced. Confidential and Restricted predicates stay unsupported (ADR-PB-004).

**E3-D12 The Security Auditor reason-note envelope stays v1.**
- It is seal-only, Authority-validated and pinned by KAT-M1-01.
- Only its KEK moves under the catalog, so its references follow the catalog provider family.
- Its validation regex already admits every v2 reference form (§3.4).

**E3-D13 Data keys are generated by the application CSPRNG**, as today. Provider-generated data keys (Vault `datakey/*`, a KMS GenerateDataKey operation) are not used: the business token deliberately lacks `datakey/*` (E-2), and a uniform generator keeps the known-answer vectors deterministic.

**E3-D14 Development providers stay development-only.** `LocalDevelopment` and `VaultTransit` can never satisfy either E-9 profile. In Development/Testing they stand in for a profile only under the explicit `Development` assurance marker (§4.3), and that marker is refused at startup outside Development/Testing.

---

## 3. Envelope v2 specification [PROPOSED]

### 3.1 Primitives

| Primitive | Choice |
|---|---|
| AEAD | AES-256-GCM, 96-bit nonce, 128-bit tag (.NET `AesGcm`). Unchanged. |
| KDF | HKDF-SHA256 (RFC 5869), .NET `HKDF`. Already used by the local keyring. |
| Seed | 32 bytes from `RandomNumberGenerator` |
| Commitment comparison | `CryptographicOperations.FixedTimeEquals` |
| Wrap | provider-specific; identified by `wrap_algorithm` (§3.5) |

No new cryptographic primitive is introduced. HKDF-SHA256 and AES-256-GCM are already in use and reviewed in this repository.

### 3.2 Canonical encoding rules

These apply to everything in §3.3 and §3.4:
- integers are unsigned, big-endian, of the stated width;
- UUIDs are 16 bytes in RFC 9562 network byte order, the same as v1 `TryWriteBytes(bigEndian: true)`;
- text is ASCII from the stated closed alphabet, prefixed by its length (u8 or u16 as stated), with no terminator;
- there are no optional fields, no padding and no alignment.

A reader **rejects** (as `UNSUPPORTED_ENVELOPE`):
- an unknown code;
- non-zero reserved bits;
- a length outside the stated bounds;
- text outside its alphabet;
- a non-canonical reference;
- trailing bytes.

The encoding is used to build the AAD, the HKDF info strings and the portable export form. Storage maps it to columns (§3.6), and a reader rebuilds it from the columns. Rebuilt bytes that do not round-trip are rejected.

### 3.3 Header

`H0` is the commitment input. `H = H0 ‖ key_commitment`.

| # | Field | Encoding | Value and rule |
|---|---|---|---|
| 1 | `magic` | 4 bytes | `0x45 0x46 0x45 0x32` ("EFE2") |
| 2 | `format_version` | u16 | `2` |
| 3 | `payload_kind` | u8 | `1` FieldValueItem, `2` DocumentVersion, `3` SealedKey (idempotency fingerprint key). `4` is reserved for a future reason-note v2 and is rejected. |
| 4 | `aead_algorithm` | u8 | `1` = AES-256-GCM (only value) |
| 5 | `kdf_algorithm` | u8 | `1` = HKDF-SHA256 (only value) |
| 6 | `wrap_algorithm` | u8 | registry §3.5 |
| 7 | `reserved_flags` | u8 | `0`; any other value is rejected |
| 8 | `organization_id` | 16 | tenant; must equal the row's `tenant_id` and the reference's organization |
| 9 | `purpose_token` | u8 length (1–32) + ASCII `[a-z-]` | `field-value`, `document` or `idempotency-protection`. It must match the payload kind: FieldValueItem → `field-value`; DocumentVersion → `document`; SealedKey → `idempotency-protection`. |
| 10 | `kek_reference` | u16 length (1–128) + ASCII `[A-Za-z0-9._:/-]` | canonical v2 reference (§3.4); must agree with fields 6, 8 and 9 |
| 11 | `data_key_identifier` | 16 | UUIDv7 of the seal batch; generated by the writer and unique per seed |
| 12 | `key_commitment` | 32 | §3.7; **not part of `H0`** |

### 3.4 KEK references

Grammar (v2 writers):

```
reference = family ":" organization ":" purpose ":" kek-version
family    = "localdev" | "vault-transit" | <production families registered by E-9 / provider milestones>
organization = canonical lowercase UUID "D" format
purpose   = purpose token
kek-version  = decimal 1..2147483647, no leading zeros
```

- This is the existing E-1/E-2 scoped form, unchanged, so v1 and v2 share the reference syntax.
- The legacy `local-dev-kek-v1` is **never** valid in v2.
- Provider-native version identifiers (Managed HSM versions are opaque 32-hex ids, E-6a §7) are **never** in the reference. The Authority catalog maps (organization, purpose, integer version) to the provider identifier. This is E-6a option 3 for encryption keys, and it needs an AuthorityDb migration (M3).
- The longest reference, `vault-transit:` + 36 + `:idempotency-protection:` + 10 digits, is 84 characters. It fits the 128-character reason-note rule and the 200-character columns.

### 3.5 Wrap-algorithm registry

| Code | Name | Wrapped length | Native AAD | Status |
|---|---|---|---|---|
| 1 | `localdev-aes256gcm` (E-1 keyring wrap) | 60 | scope and version | Development/Testing |
| 2 | `vault-transit-aes256-gcm96` (E-2) | 60 | scope and version | Development/Testing |
| 3 | reserved: Managed HSM `oct-HSM` wrap (A256KW / A256KWPAD, or A256GCM encrypt with AAD) | by algorithm | to be established | **[REAL-HSM]**; needs an E-6c experiment; OD-E3-10 |
| 4 | reserved: Azure Key Vault RSA-OAEP-256 | 256 / 384 / 512 | none | E-9 software profile; OD-E3-6 |
| other | — | — | — | rejected |

A family maps to exactly one wrap code. A mismatch is rejected before the provider is called. The v2 binding (§3.7) never depends on native AAD: where the provider supplies AAD, it adds a second, independent binding.

### 3.6 Context blocks and associated data

**FieldValueItem / SealedKey**

```
C_field = form_record_id(16) ‖ field_definition_id(16) ‖ field_value_item_id(16) ‖ u8 len ‖ logical_type ASCII
```

- `logical_type` is the existing closed set: `String`, `Integer`, `Decimal`, `Date`, `DateTime`, `Boolean`, `Guid`, `Option`. SealedKey uses the existing seal logical type and seal purpose id.
- `AAD_field = H ‖ C_field`.
- The plaintext is the existing canonical invariant wire string in UTF-8 (unchanged).

**DocumentVersion**

```
C_doc = form_record_id(16) ‖ file_attachment_id(16) ‖ u32 version_number ‖ u32 segment_size_bytes ‖ nonce_prefix(8)
AAD_segment = H ‖ C_doc ‖ u32 segment_index ‖ u8 final_flag (0|1)
```

- The segment framing, look-ahead, bounds and truncation rules are those of v1, including the E0-2 constant-depth reader.
- `segment_size_bytes` is fixed at 1,048,576 in v2 and kept in the AAD so a future size is an explicit change.
- Nonce = `nonce_prefix(8) ‖ u32 segment_index`.

### 3.7 Derivations

```
PRK         = HKDF-Extract(salt = ASCII "DynamicForms/EnvelopeV2/Extract", IKM = seed)
commitment  = HKDF-Expand(PRK, ASCII "DynamicForms/EnvelopeV2/Commitment"  ‖ H0,          32)
item_key    = HKDF-Expand(PRK, ASCII "DynamicForms/EnvelopeV2/FieldItem"   ‖ H0 ‖ C_field, 32)   (kinds 1 and 3)
document_key= HKDF-Expand(PRK, ASCII "DynamicForms/EnvelopeV2/Document"    ‖ H0 ‖ C_doc,   32)   (kind 2)
```

**What this gives:**
- **Provider-neutral context binding.** A wrapped seed moved to another organization, purpose, reference, data-key id, record, field, item, logical type, attachment or version yields a different key, so AEAD fails, and in most cases the commitment fails first.
- **Key commitment.** The commitment check runs **before** payload decryption. It also defeats AES-GCM's lack of key commitment: a seed that does not reproduce the stored commitment is never used.
- **Single-use keys.** An item key is used exactly once: one item per batch (enforced, §3.9), and a fresh seed per batch. A document key is used once per version. A random 96-bit nonce is still used for field items, as defence in depth against implementation error. Document nonces stay prefix ‖ counter under a single-use key.

### 3.8 Storage mapping

v1 rows are unchanged and never re-signed. v2 rows use signed schema version 2 of each entity kind.

**`field_encrypted_values`** (also the creation-attempt sealed key and the `upload_sessions` processed copy, as applicable):

| Envelope field | Column | v1 | v2 |
|---|---|---|---|
| version | `encryption_envelope_version` | 1 | 2 |
| aead | `encryption_algorithm_name` | `AES-256-GCM` | `AES-256-GCM` (unchanged spelling) |
| wrap algorithm | **new** `key_wrap_algorithm_code` smallint | NULL | registry code |
| organization | `tenant_id` | — | must equal header |
| purpose | derived from `encryption_key_reference` | — | must match kind |
| reference | `encryption_key_reference` | v1 form | v2 form |
| data-key id | **new** `data_key_identifier` uuid | NULL | UUIDv7 |
| commitment | **new** `data_key_commitment_bytes` bytea | NULL | 32 bytes |
| wrapped seed | `wrapped_data_key_bytes` / `wrapped_data_key` | DEK wrap | seed wrap |
| nonce | `encryption_nonce_bytes` (12) / `encryption_nonce_prefix` (8) | same | same |
| tag | `encryption_auth_tag_bytes` (16) | same | same (field kinds) |
| ciphertext | `encrypted_value_bytes` / object store | same | same |
| logical type | `encrypted_logical_type` | same | same |

The same three new columns go on `file_attachment_versions` and `upload_sessions`.

CHECK constraints (descriptive multiword names, per CLAUDE.md §1 rule 5):
- `encryption_envelope_version = 1` ⇒ the three new columns are NULL;
- `= 2` ⇒ all three are non-NULL and `octet_length(data_key_commitment_bytes) = 32`;
- `encryption_envelope_version IN (1, 2)`.

Signed schemas:
- `field_encrypted_values` schema 2 and `file_attachment_versions` schema 2 add the three columns as class-C columns.
- They are registered in `BusinessSignedSchemas`, in the Authority's `ContentDeltaRules` and in `SignedColumnRegistryTests`.
- RowHmac v1 and Merkle v1 **algorithms are unchanged**: the schema version travels in the canonical row header, as it already does for `form_records` v2/v3. To be confirmed in M2 against `merkle-v1.test-vectors.json` (no vector change expected).

### 3.9 Writer and reader rules

**Writer (field batch):**
1. Obtain the organization's catalog entry for the purpose (§5.1). The family must equal the organization's registration, and the version is the catalog's **current** version.
2. Generate the seed and the `data_key_identifier` (UUIDv7).
3. Wrap through the organization's provider at exactly that version.
4. Compute PRK and the commitment.
5. For each item: derive the item key, take a random nonce, encrypt with `AAD_field`, zero the item key.
6. Zero the seed and PRK in `finally`.
7. All items in a batch carry byte-identical (`wrapped`, reference, wrap code, data-key id, commitment).
8. An item id appears at most once per batch.

**Reader (per authorized row set):**
1. Authority verification and authorization projection (existing, unchanged; not part of E-3).
2. Parse and validate each row's header fields (§3.2, §3.3). Group rows by `data_key_identifier`. Rows of one group must agree byte for byte on wrap, reference, code and commitment; otherwise `UNSUPPORTED_ENVELOPE`.
3. Catalog check: the reference's family equals the organization's registration, and the version state permits unwrap (§5.3). Otherwise `KEY_UNAVAILABLE`, without calling the provider.
4. Unwrap exactly the referenced version, once per group in this operation.
5. Compute PRK, then the commitment. Compare in constant time; on a mismatch, fail with `KEY_MISMATCH` and zero the seed.
6. For each authorized item: derive the key, decrypt with the AAD, zero the key (`AUTHENTICATION_FAILED` on a tag failure).
7. Zero the seed and PRK at the end of the operation. Seeds are cached **only** inside one operation's `DecryptedFieldValues`, never across requests.

Failure reasons reuse the E0-4 codes, so no new audit vocabulary is needed. Responses and status codes are unchanged:
- `503 ENCRYPTION_UNAVAILABLE` for unavailable;
- `423 FIELD_DECRYPTION_FAILED` for mismatch or authentication.

### 3.10 Portable form (for PB-13 export)

Field kinds (FieldValueItem, SealedKey):

`EFE2 portable field envelope = H ‖ C_field ‖ u16 len ‖ wrapped ‖ nonce(12) ‖ tag(16) ‖ u32 len ‖ ciphertext`

Documents (clarified in E3-M1): the nonce prefix is in `C_doc`, and the tags are per segment inside the stream. A document therefore has a portable **header**, and its segment stream is kept separately with the v1 framing:

`EFE2 portable document header = H ‖ C_doc ‖ u16 len ‖ wrapped`

These forms are defined now so export does not invent a second format. PB-13 decides whether exports re-encrypt to an export key (expected), so the portable form is not a commitment to export organization ciphertext.

### 3.11 Coexistence with v1

| Rule | Detail |
|---|---|
| Readers | All readers (API, Worker, Authority shape checks) accept v1 and v2 for as long as any retained revision or backup can contain v1 (indefinitely, for now). |
| Writers | After the M4 cutover, writers emit v2 only. The catalog field `MinimumWriteEnvelopeVersion` (per organization and purpose) lets the Authority **refuse** new v1 seals, so a rolled-back business host cannot keep writing v1. |
| No implicit rewrite | No implicit re-encryption. A v1 value moves to v2 only when it is re-sealed by an ordinary owner save (existing carry-forward) or by an explicit governed `Reseal` (§5.4). A read never rewrites. |
| Documents | v1 documents stay v1 unless explicitly re-encrypted (§5.4, OD-E3-11). |
| Legacy `local-dev-kek-v1` | v1 only, decrypt only, unchanged. |

---

## 4. E-9 integration boundaries [PROPOSED; E-9 owns the profile]

### 4.1 Three separate concerns

| Concern | Owner | Holds | Mutability |
|---|---|---|---|
| **Organization cryptographic protection profile** | E-9 (Authority) | `HardwareBacked` or `SoftwareBacked`; selected in the onboarding statement; fixed at `Initialize` (activation) | Immutable after activation (AuthorityDb trigger, the E-5/S1 permanence pattern) |
| **Provider family registry** | deployment configuration, reviewed (PB-11) | the families this deployment offers and the profile each satisfies, for example `mhsm` → HardwareBacked | changes only by release |
| **Key catalog** | E-3 (Authority) | per (organization, purpose): family, provider key name, versions, states, provenance | governed commands (§5) |

### 4.2 Enforcement points

1. **Onboarding (E-9).** The frozen statement names the profile. `Initialize` refuses unless:
   - the deployment offers a family for that profile;
   - E-9's custody step has provisioned and verified an encryption KEK for **every** purpose in that family, as it does today for the HMAC key.
2. **Catalog (E-3).** Registering or activating a key version refuses when its family does not satisfy the organization's profile. There is no command that changes the family of a registration. Any future provider migration needs its own approved design: "no implicit provider migration".
3. **Content Prepare (E-3, Authority).** Every **new** v2 envelope in a candidate must name the organization's registered family, the current version and the matching wrap code. Carried items may name retained versions. New v1 seals are refused once `MinimumWriteEnvelopeVersion = 2`.
4. **Business host (E-3).** An `OrganizationKeyProviderRouter` implements `IEncryptionKeyProvider`. It resolves the organization's registration from the catalog and dispatches to the one provider instance of that family registered in the host. An unknown family, a family not registered in the host, or a mismatch fails closed. There is no default provider and no fallback.
5. **Startup (E0-1 extended).** A host refuses to start when it registers a family not in the deployment registry, or when it registers a development family outside Development/Testing.

### 4.3 Development stand-ins (E3-D14)

- In Development/Testing, the catalog may register `localdev` or `vault-transit` for an organization whose profile is either value, but the registration then carries `ProviderAssuranceName = Development`.
- Outside Development/Testing, the Authority refuses to start if any registration carries `Development`.
- Existing development organizations are registered as `Unassigned` profile + `Development` assurance by the M3 backfill (OD-E3-8). This is refused outside Development/Testing.

### 4.4 What E-3 does not decide (E-9 or later)

- The concrete hardware family: Managed HSM `oct-HSM` only, or also Key Vault Premium HSM-backed RSA (OD-E3-5).
- The concrete software family: Azure Key Vault Standard RSA-OAEP-256 (OD-E3-6).
- **The integrity HMAC provider for the software profile.** Azure Key Vault has no symmetric or HMAC keys, so a software-backed organization's `row-integrity-v1` key needs a different provider (OD-E3-7). E-5's `IntegrityKeyRegistration` would gain the same profile check, and the Authority would need a per-organization MAC provider router, as the business host does for encryption.
- Onboarding UI and the statement schema change, provenance evidence format, and the profile's audit events.

The E-6 Managed HSM probe is **not** treated as production-ready for anything.

---

## 5. Key lifecycle and rotation [PROPOSED]

### 5.1 Catalog (AuthorityDb, M3)

The catalog has two tables. All columns use descriptive multiword snake_case names, and primary keys follow the entity name.

**`encryption_key_registrations`**, one per (organization, purpose):

| Column | Purpose |
|---|---|
| `encryption_key_registration_id` | primary key |
| `organization_id` | owning organization |
| `key_purpose_token` | purpose |
| `provider_family_name` | provider family; immutable |
| `provider_key_name` | provider-native key name |
| `provider_assurance_name` | `Production` or `Development` |
| `current_wrap_key_version` | version used for new seals |
| `minimum_unwrap_key_version` | lowest version that may still be unwrapped |
| `minimum_write_envelope_version` | lowest envelope version accepted for new seals |
| `key_usage_paused` | pause flag |
| `registration_revision` | revision for compare-and-set |
| `key_registered_on`, `key_modified_on` | timestamps |

**`encryption_key_versions`**, one per (registration, integer version):

| Column | Purpose |
|---|---|
| `encryption_key_version_id` | primary key |
| `encryption_key_registration_id` | owning registration |
| `key_version_number` | integer version, as used in references |
| `provider_version_identifier` | the opaque provider id; the E-6a mapping |
| `key_version_state_name` | lifecycle state (§5.3) |
| `provenance_evidence_sha256` | digest of the provenance evidence |
| `version_provisioned_on`, `version_activated_on`, `version_retired_on`, `version_suspended_on` | lifecycle timestamps |
| `state_changed_by_command_id` | the command that last changed the state |

Grants:
- The runtime role gets SELECT, INSERT and UPDATE, with no DELETE.
- A trigger forbids changing `provider_family_name` and moving a version out of `Destroyed`.

Business access:
- The business workload reads its organization's catalog through a new internal Authority read: per purpose, the family, the current version, the unwrap-permitted versions and the catalog revision.
- The business host caches it by revision for a bounded time.
- Prepare re-checks against the live catalog, so a stale cache can only cause a refused write, never an accepted wrong one.
- The E-4 per-host version tables are retired in M5. Until then they must agree with the catalog, or the host refuses to start.

### 5.2 Provisioning and activation (E-4/E-5 semantics preserved)

1. **Provision** (operator, provider side): add a version to the provider key, as with the E-4/E-5 scripts. Nothing changes in the application.
2. **Register** (Authority operator command):
   - The Authority inspects the version's metadata through a **metadata-read-only** identity on the encryption provider (OD-E3-3): it exists, is non-exportable, has the expected type and size, is enabled, and has no release policy.
   - The provider id and provenance digest are recorded.
   - The version state becomes `Provisioned`.
   - The Authority performs no crypto operation and never receives key material.
3. **Activate** (Authority command):
   - Compare-and-set of `current_wrap_key_version` under the organization lock.
   - Refused if the version is not `Provisioned` or `Retained`, the family mismatches, usage is paused, or the expected current version is stale.
   - The previous current version becomes `Retained`.
   - The event is journaled.
   - In production this needs a second authorized operator (dual control; CRYPTOGRAPHIC_SIGNING_AUTHORITY §5). The development command is single-operator, as in E-5.
4. **Effect:**
   - New seals use the new version from the next Prepare.
   - A writer that staged under the old version is refused once (`422`, stale wrap version) and re-stages, exactly as E-5 does for HMAC.
   - Nothing existing is rewritten.

### 5.3 Version states

```
Provisioned ──activate──▶ Active ──activate other──▶ Retained ──destroy (crypto-erase workflow)──▶ Destroyed
     │                      │                          ▲  │
     └──────────────────────┴──suspend (incident)──▶ Suspended ──reinstate (dual control)──┘
```

| State | Wrap | Unwrap | Notes |
|---|---|---|---|
| Provisioned | no | no | registered, not yet used |
| Active | yes (only one per registration) | yes | |
| Retained | no | yes | default after rotation |
| Suspended | no | **no** (fail closed, `KEY_UNAVAILABLE`) | incident response: unwrap access is believed misused. Journaled on the **gating** stream, so a restore cannot silently undo it (§6). Reinstatement needs dual control. |
| Destroyed | no | no | irreversible. Allowed only through the crypto-erase workflow (§5.5). The provider version is then disabled or purged by the operator. |

`minimum_unwrap_key_version` stays monotonic, and only the destroy workflow raises it (E-5 floor semantics).

### 5.4 Rotating existing data: rewrap versus re-encryption

Under E3-D5, both are **new content revisions** through the Authority. Historical revisions keep their original envelopes, so their KEK versions remain needed (§5.5).

| Operation | When | What changes | Authority rule (new mutation kinds; reprotection engine reused) |
|---|---|---|---|
| **Reseal** (fields; v1 or v2 → v2 under the current version) | KEK rotation campaign, v1 → v2 migration, suspected seed exposure | Every encrypted row of the record gets a new batch: new seed, data-key id, commitment, nonces and ciphertext. **Re-encryption with fresh data keys**: rewrap alone does not exist for fields, because re-sealing a value is cheap. | `ResealRules`, like `ReprotectionRules`: same item set, same item identities, same field and logical type; only envelope columns and the operation binding change; every new envelope v2, current version, organization family; nothing added or removed. Authorized by a new governed command `ResealOrganizationKeyScope` (operator plus owner or security administrator, OD-E3-2), not by a record permission. |
| **Rewrap** (documents, v2 only) | KEK rotation for large documents | Only `wrapped_data_key`, `encryption_key_reference` and `key_wrap_algorithm_code`, plus the operation binding, change. **The seed and commitment are unchanged**, so no object rewrite is needed. | `RewrapRules`: identical leaf except those columns. The commitment column must be byte-identical. A business host that wraps a different seed produces a commitment mismatch at read, which fails closed (availability only). |
| **Re-encrypt** (documents; v1 → v2, or after seed exposure) | v1 retirement, incident | New object, new version row of the **same** attachment version identity, new digests | `ReencryptDocumentRules`. Expensive (2 GB uploads); explicit only (OD-E3-11). |

Campaign execution reuses the B-28 machinery:
- the leased, resumable Worker with idempotency keys per attempt;
- prepare → stage → read-back → commit;
- lost-acknowledgement repair.

Campaign completion reuses the G19 machinery: the verifier census counts, **from verified bytes**, the rows per (family, version, envelope version) for the current heads, and the Authority attests completion under the organization lock (E3-D7).

**Concurrency:**
- A record edit and a campaign commit race on the content head CAS (existing). The loser re-stages. A user edit that wins already re-sealed everything under the current version, and the campaign's census then finds the record compliant.
- Share edits seal only the edited item in a new batch (the existing D3 single-item rule). The Authority requires a fresh `data_key_identifier` and nonce for the changed item, which replaces `SHARE_ENVELOPE_REUSED`'s wrapped-key comparison.
- Activation during a campaign: items staged under the old version are refused at commit (stale), and the attempt re-stages under the new version.

**Idempotency:**
- Activation and registration are compare-and-set commands with idempotency keys.
- Reseal and rewrap attempts are keyed per (campaign, record, head operation).
- A replayed commit for an already-accepted head is acknowledged without a second revision (the existing lost-acknowledgement path).

### 5.5 Retention and destruction

- **Invariant K-1.** A KEK version stays unwrap-capable at the provider while **any** of the following can reference it:
  1. a current head;
  2. a historical revision within its retention period;
  3. a BusinessDb or Blob backup or PITR window;
  4. an export or archive that holds organization ciphertext (PB-13).

  Retirement is `Retained`, never deletion.
- **Destroy** is a separate crypto-erase workflow, owned with PB-13 offboarding and G16 retention, not E-3 code. It requires:
  - a census of current heads **and** history showing zero references within retention;
  - backup windows expired;
  - legal-hold clearance;
  - dual approval.

  It journals the state change before the provider version is disabled. A Destroyed version makes any surviving reference fail closed permanently. This is intentional: crypto-erasure.
- E-3 implements the `Destroyed` **state** and its refusals, but **no destroy command** (deferred to PB-13).

### 5.6 E-4 and E-5 guarantees preserved

| Guarantee | Under E-3 |
|---|---|
| Provisioning never activates | Provisioned → Active needs an explicit Activate. |
| Never "latest" | Wrap names the catalog's current version; the provider's reported version must equal it (E-2 rule). |
| Exact-version unwrap, no substitution | Reference names the version, the catalog state is checked, and the provider decrypts that version only. |
| Organization- and purpose-specific activation | The catalog is per (organization, purpose). |
| No rewrite on activation or read | Unchanged. Reseal and rewrap are explicit governed campaigns. |
| Runtime cannot provision, activate or retire | Unchanged at the provider (business token: wrap and unwrap only). The Authority catalog adds the trusted record, and the business workload has no catalog write. |
| E-5 HMAC activation, signatures, manifests, journal | Untouched. No HMAC key, signature or manifest is rewritten by E-3. |

---

## 6. Recovery and rollback [PROPOSED]

This section builds on G14 (`COORDINATED_DISASTER_RECOVERY.md`): the Authority's accepted revisions are authoritative, and protected access stays gated until replay and evidence agree. E-3 adds encryption-key state to that model.

| Scenario | Detection | Behaviour | Recovery |
|---|---|---|---|
| **Provider outage** (HSM, Key Vault or Vault unreachable, throttled, 5xx) | provider exception | `503 ENCRYPTION_UNAVAILABLE`; reads and writes of encrypted content fail closed; no other provider is tried | Restore the provider. Nothing to repair. |
| **Provider lost** (HSM destroyed, key deleted) | unwrap refused for every version | as above, permanently | Restore keys **into a separate replacement** HSM from protected backup plus Security Domain quorum; for software, restore the key backup within the same geography. Verify each catalog version with a known-answer unwrap of a fixture seed. Without key backups the data is **irrecoverable**: stated, not mitigated. PB-10 Canadian-only manual path. |
| **Key-version mismatch**: reference names a version the catalog lacks or holds in a non-unwrap state | catalog check, before the provider is called | `KEY_UNAVAILABLE`, nothing substituted | Correct the catalog through a governed command, or restore the provider version. |
| **Catalog says Retained, provider lacks the version** | provider refusal | `503`, no substitution (as in E-4) | Restore the provider version. |
| **BusinessDb restore (full)** | Authority heads ahead of business rows → existing quarantine or awaiting-archive rules | unchanged G14 behaviour; envelopes in restored rows reference versions that Invariant K-1 keeps retained | as G14 |
| **Partial restore** (database without matching blobs, or the reverse) | Merkle ciphertext digest and object checks (existing) | document quarantined; never substituted by an older version | as G14 (missing bytes → quarantine) |
| **AuthorityDb restore** reverting catalog state | E-3 journals every catalog change. Suspend and Destroy go on the **gating** stream (replayed and anti-rollback). Register, Activate and Retire go on the audit stream (E-5 precedent). | A restored older Active pointer only sends new seals to an older **Retained** version, which is not a security rollback. A restored un-suspension or un-destruction is prevented by replaying the gating stream before access reopens. | G14 replay. M6 adds encryption-catalog events to the replay inventory and the DR test plan. |
| **Application rollback, business host below E-3** | it cannot parse v2 → `UNSUPPORTED_ENVELOPE` (423 and one `DECRYPTION_FAILED` event per read); its v1 writes are refused by `MinimumWriteEnvelopeVersion` | fail closed; no v1 data is written after cutover | Roll forward. **Minimum safe business version after the M4 cutover = the M2 reader.** |
| **Authority rollback below E-3** | a pre-E-3 Authority refuses v2 at Prepare and has no rules for schema-2 rows | must be prevented; a schema-2 row reaching an old Authority's verify path could be mistaken for tampering | **Fence**, as S1's `ck_legacy_projection_token_retired`: M2 adds an AuthorityDb constraint or column that a pre-E-3 Authority cannot satisfy, so it fails at startup instead of running. **Minimum safe Authority version = M2.** To be designed precisely in M2. |
| **Interrupted activation** | compare-and-set; the event is in the same transaction | all-or-nothing | Retry with the same idempotency key. |
| **Interrupted campaign** | leased jobs; head CAS | the partially resealed organization is consistent per record; the census shows the remaining records | Resume. Nothing is half-written per record. |
| **Corrupted envelope metadata** | a database edit fails RowHmac/Merkle → quarantine (existing). A writer bug that HMAC accepted → commitment or AEAD failure. | quarantine, or `423` with `KEY_MISMATCH` / `AUTHENTICATION_FAILED`; never released | G14 quarantine handling. A writer bug needs a fix plus a Reseal from a trusted earlier revision, if one exists. |

> **E3-M2f contract closure (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` §2).** The "Authority rollback below E-3" fence above is realized by F0: CARV 3, floors 1–3 and the Release B fence; no additional trigger. The "Application rollback, business host below E-3" row is complemented by the E3-M2f business capability floor, enforced before verification. Profiles (§4) remain E-9; catalog and routing remain E3-M3.

**Never permitted:**
- re-signing tampered rows;
- substituting another key version or an older content revision;
- clearing a Suspended state through a restore;
- falling back to plaintext or to a development provider;
- changing an organization's profile or family as recovery.

---

## 7. Threat model [PROPOSED mitigations]

The attackers considered:
- **A1** BusinessDb writer or DBA;
- **A2** blob storage writer;
- **A3** compromised business runtime;
- **A4** compromised software provider or its credential;
- **A5** malicious or mistaken operator;
- **A6** restore or rollback actor.

The independent Authority, Entra ID and the HSM administrators are trusted, as in the Level 2 model.

| ID | Threat | Mitigation | Residual risk |
|---|---|---|---|
| TH-01 | Cross-organization key substitution: the reference or wrap of organization B placed on organization A's row (A1) | RowHmac and Merkle (quarantine). The reference organization must equal the tenant (reader and Authority). HKDF binding over `H0`, the commitment, and per-organization provider keys. | None identified for A1. A3 can already unwrap every organization (logical isolation only, §2.2). |
| TH-02 | Cross-record or cross-item ciphertext substitution (A1) | RowHmac and Merkle; `C_field` / `C_doc` in the HKDF info and the AAD | none identified |
| TH-03 | Wrapped-key substitution within one organization and purpose (A1) | RowHmac; the commitment check before decryption; HKDF binding to the data-key id | A3 can wrap any seed; that is inside its existing trust |
| TH-04 | Unauthorized key activation (A5, A3) | Authority-only catalog commands; the business workload has no catalog write and no provider admin; compare-and-set; dual control in production; journaled | Development commands are single-operator (as E-5) |
| TH-05 | Compromised software-backed provider or credential (A4) | Profile choice is the organization's (E-9). Narrow wrap/unwrap identity, Suspend state, monitoring, reseal campaign under a new version after credential rotation. | **Material:** an attacker with unwrap rights and database read can decrypt all data of the affected organizations until access is revoked. Rotation does not recall exposure. The software profile accepts this by definition. |
| TH-06 | Provider outage used as denial of service | fail closed; PB-09 availability budget; no fallback | Availability loss is accepted by design |
| TH-07 | Key-version rollback: data or a pointer moved back to an older or suspended version (A1, A6) | reference versions are signed; catalog states with Suspend and Destroy on the gating stream (replayed); unwrap refused for non-unwrap states | An older **Retained** version is legitimately readable; that is not a confidentiality loss |
| TH-08 | Partial database restore resurrecting older envelopes or content (A6) | Authority content heads, Merkle, quarantine (G14); Invariant K-1 | Business data loss down to the RPO → quarantine (G14, PB-09) |
| TH-09 | Rotation race: an item sealed under a stale version is accepted, or a campaign overwrites a user edit | head CAS; Prepare requires the current version for new seals; refuse and re-stage; reseal rules allow envelope changes only | Throughput: campaigns contend with edits |
| TH-10 | Unauthorized recovery: restoring keys to a wrong HSM, or reinstating a suspended version (A5) | separate replacement HSM; Security Domain quorum; dual control; gating-stream replay; known-answer verification | Quorum custodian collusion (out of scope) |
| TH-11 | Plaintext or key leakage through logs | existing rule: exceptions carry identifiers and reason only (E0-4). The seed, PRK, item keys and commitment are never logged. `data_key_identifier` and the reference are not secret but are excluded from routine logs. Architecture test for log calls in crypto classes (M1). | Managed strings in provider HTTP bodies cannot be zeroed (E-2 residual) |
| TH-12 | Encryption downgrade: v2 → v1, AEAD swap, plaintext | Authority allow-lists; `MinimumWriteEnvelopeVersion`; header codes closed; the protection floor (existing) and reprotection rules unchanged | v1 remains readable (necessary for history) |
| TH-13 | Compromised onboarding configuration: a wrong profile or family at activation (A5) | the profile is in the frozen statement under two Platform Operator approvals; the deployment family registry; provenance inspection at registration; immutable after activation | A wrong choice at activation is permanent by design. Correction means a new organization plus export and import (PB-13). Stated as an E-9 risk. |
| TH-14 | Nonce reuse or data-key reuse | single-use item and document keys (HKDF info unique per item or version, fresh seed per batch); random nonce; one item per batch enforced by the writer and the Authority | none identified |
| TH-15 | Cross-kind confusion: a document key used as a field key | `payload_kind` and the purpose token in `H0`; distinct HKDF labels | none identified |
| TH-16 | Seed reuse across organizations through a buggy writer | `data_key_identifier` is unique per seed; the Authority refuses a data-key id seen with a different (organization, reference, commitment) within a revision. The census flags duplicate (data-key id, nonce) pairs. | Cross-revision duplicates are detectable only by census (best effort) |

Not claimed:
- confidentiality against A3 combined with provider rights;
- recall of plaintext already delivered;
- protection of Public/Internal plaintext against database reads (existing, documented);
- that a provider's undocumented behaviour is stable (E-6).

---

## 8. Compatibility and migration analysis

### 8.1 Per change

Key: DB = database migration; API = public API contract change; Auth = Authority command or behaviour; Port = provider interface; Wkr = background worker; Mig = existing ciphertext migration; Cfg = configuration; Onb = onboarding workflow; Sec = additional security approval.

| Change | DB | API | Auth | Port | Wkr | Mig | Cfg | Onb | Sec |
|---|---|---|---|---|---|---|---|---|---|
| v2 crypto core and KATs (M1) | — | — | — | — | — | — | — | — | owner |
| v2 columns, signed schema 2 (M2) | **business**: 3 nullable columns × 3 tables, CHECKs | none (OpenAPI unchanged; envelopes are internal) | **yes**: allow-lists, schema-2 rules, shape checks, census, fence | — | reader in Worker | none | — | — | owner; independent review before production |
| Authority catalog (M3) | **AuthorityDb**: 2 tables, trigger, grants, backfill | internal Authority OpenAPI: catalog read and commands | **yes**: new commands, Prepare enforcement | `IEncryptionKeyProvider` unchanged; new `OrganizationKeyProviderRouter` and catalog client | — | none | provider connection settings stay; version tables deprecated | registration at Initialize (E-9 hook) | owner; **Authority change approval** |
| v2 writers and cutover (M4) | — | — | `MinimumWriteEnvelopeVersion` set per organization | wrap of the 32-byte seed (same call) | Worker writes v2 documents | none implicit | — | — | owner |
| Rotation operations (M5) | AuthorityDb: campaign and census tables (additive) | — | **yes**: Reseal, Rewrap and Re-encrypt rules and commands | — | **yes**: campaign jobs | explicit campaigns only | E-4 tables removed | — | owner; dual-control design review |
| Recovery integration (M6) | — | — | replay inventory | — | — | — | — | — | G14 review |

### 8.2 Existing ciphertext

- **No implicit rewrite.**
- v1 field values become v2 only through ordinary owner saves (existing carry-forward, already described in E-1 and E-4) or explicit Reseal campaigns.
- v1 documents stay v1 (OD-E3-11).
- v1 historical revisions are immutable and stay v1 forever.
- The legacy `local-dev-kek-v1` data is unchanged.
- There is no production data, so the only population affected is development and security-test data.

### 8.3 Tests that must keep passing unmodified

- E0-6 F1/F2 and documents, E1 K1–K4, KAT-M1-01;
- `KeyProviderInterfaceV2Tests`, `VaultTransitKeyProviderTests`, `KeyActivationTests`;
- `IntegrityKeyActivationTests`, `EnterpriseForms.Integrity.Tests` (RowHmac and Merkle vectors);
- `ReprotectionTests`.

Changing an existing vector is a stop-and-report event.

`DecryptionCallSiteTests` changes with stated reasons wherever v2 adds or moves a decrypt site; the seed-unwrap site replaces per-value unwrap.

---

## 9. Implementation milestones

Each milestone is independently reviewable and needs its own owner authorization. Stage B is never run as part of a milestone (owner rule C-131). Focused tests only.

### E3-M0 — Decisions (documentation)
- **Scope:** owner decisions on OD-E3-1 to OD-E3-5; E-9 record written or reconciled.
- **Dependencies:** this package.
- **Approval gate:** owner.

### E3-M1 — Envelope v2 cryptographic core (business Infrastructure, not wired)

- **Scope:**
  - `EnvelopeV2Codec` (§3.2 to §3.6, parse and serialize, strict);
  - `EnvelopeV2KeySchedule` (§3.7);
  - deterministic internal cores for vectors;
  - field batch sealer and opener, document v2 encryptor and decryptor;
  - provider-agnostic: uses `IEncryptionKeyProvider` as-is.
- **Files:** new files in `Infrastructure/Features/FormRecords/` and `Infrastructure/Features/Documents/`; tests in `EnterpriseForms.Api.ContractTests`; evidence in `docs/evidence/e3-envelope-v2/` with a Python cross-check.
- **Database:** none. **Authority:** none.
- **Security:** a new format is defined but not reachable from any endpoint.
- **Acceptance:**
  - V2-KAT-01 to V2-KAT-12 reproduced independently in Python;
  - V2-RT, V2-TMP and V2-ISO unit suites pass;
  - every v1 vector unchanged;
  - no new decrypt call site reachable (the architecture test is unchanged or extended with reasons).
- **Rollback:** delete unused code.
- **Gate:** owner.

### E3-M2 — v2 readers everywhere, dark (business and Authority)

- **Scope:**
  - business migration: three columns on `field_encrypted_values`, `file_attachment_versions` and `upload_sessions`, plus CHECKs;
  - signed schema 2 in `BusinessSignedSchemas`, Authority `ContentDeltaRules` and the shape checks, `SignedColumnRegistryTests`;
  - readers in `DecryptedFieldValues`, the document download and the Worker accept v2;
  - Authority allow-list {1, 2}, the v2 shape and consistency checks (§3.9, TH-16), census v2 categories;
  - the Authority rollback fence (§6).

  Writers still write v1.
- **Security:** Authority behaviour changes. **This needs the owner's explicit approval of an Authority change.**
- **Acceptance:**
  - v2 rows produced by the M1 core in test fixtures commit and verify;
  - the Authority refuses malformed v2 rows (V2-TMP-*);
  - v1 behaviour byte-identical;
  - Merkle and RowHmac vectors unchanged;
  - a pre-M2 Authority binary is refused by the fence (V2-CMP-05).
- **Tests:** contract and Authority unit suites, signed-column registry, focused real-Vault class.
- **Rollback:** the columns are nullable, and v1 continues. Authority rollback **below M2 is blocked by the fence by design**, so roll forward.
- **Gate:** owner, plus independent review before any production claim.

### E3-M3 — Authority encryption-key catalog and per-organization routing

- **Scope:**
  - the catalog tables, trigger and grants (§5.1); backfill of existing development organizations from their current configuration (`Development` assurance, `Unassigned` profile hook);
  - catalog read for the business workload; Register, Activate and Pause commands (development single-operator, production dual-control design);
  - Prepare enforcement of family and current version for new seals;
  - `OrganizationKeyProviderRouter` with dev providers; E0-1 startup extension;
  - the `IOrganizationProtectionProfileSource` port. In E-3 it reads only `Unassigned`; E-9 supplies the real profile.
- **Not included:** onboarding UI, the statement schema, production providers (all E-9 or the provider milestones).
- **Security:** a new Authority command surface, needing the owner's Authority-change approval.
- **Acceptance:**
  - V2-PROF-01 to V2-PROF-06;
  - V2-ROT-01 to V2-ROT-04 (activation through the catalog);
  - the E-4 tables must agree with the catalog;
  - E-5 suites unchanged.
- **Rollback:** additive tables; the router can be pinned to the single configured provider only in Development/Testing, as a documented emergency procedure, not a runtime switch.
- **Gate:** owner; OD-E3-3 and OD-E3-8 resolved.

### E3-M4 — v2 writers and cutover

- **Scope:** field batches (create, owner edit, shared edit, restore, related forms, reprotection Encrypt direction), documents, idempotency key; set `MinimumWriteEnvelopeVersion = 2` per development organization; measure provider calls per read (V2-PRF-*).
- **Security:** the write format changes; downgrade refused.
- **Acceptance:**
  - all write paths emit v2;
  - the Authority refuses new v1;
  - v1 history reads;
  - owner save re-seals v1 → v2;
  - shared edit seals one item in a fresh batch;
  - provider unwraps per record read = number of distinct batches.
- **Rollback:** lower `MinimumWriteEnvelopeVersion` by governed command (development only) and redeploy the M3 build. Readers stay M2+.
- **Gate:** owner.

### E3-M5 — Rotation operations

- **Scope:**
  - Reseal (fields), Rewrap (v2 documents) and Re-encrypt (documents) mutation kinds and rules;
  - the governed campaign command; Worker campaign jobs on the reprotection engine;
  - verifier census of (family, version, envelope version) and attestation;
  - Retire and Suspend states and refusals; retirement of the E-4 per-host tables.
- **Security:** new Authority-accepted non-user content changes (the B-28 class), needing an independent review recommendation.
- **Acceptance:** V2-ROT-05 to V2-ROT-14 and V2-REC-04 to V2-REC-06.
- **Rollback:** campaigns stop cleanly; resealed records remain valid v2.
- **Gate:** owner; OD-E3-2 resolved.

### E3-M6 — Recovery integration

- **Scope:**
  - encryption-catalog events in the G14 replay inventory;
  - Suspend and Destroy on the gating stream (if not already done in M5);
  - DR runbook and test-plan entries;
  - local fault-injection tests: AuthorityDb restore with catalog regression, provider version loss, partial restore.
- **Database:** none, or documentation only.
- **Acceptance:** V2-REC-01 to V2-REC-10 locally. **Azure parts deferred to staging.**
- **Gate:** owner, G14 review.

**Outside E-3:**
- the production providers (Managed HSM wrap, Key Vault): need an E-6c-style real-provider experiment for wrap and unwrap, then a provider milestone;
- the E-9 profile entity, onboarding UI and statement;
- the integrity-provider profile check and the Authority MAC router;
- the PB-13 destroy and crypto-erase workflow.

---

## 10. Test and verification plan

### 10.1 Local (focused; contract, Authority and architecture suites; real Vault via the existing throwaway-container scripts)

| ID | Test |
|---|---|
| V2-KAT-01 to 04 | Field item v2: two items in one batch, kinds `String` and `Decimal`, fixed seed, data-key id and nonces. Expected `H0`, commitment, item keys, AAD, ciphertext and tag. Python `cryptography` cross-check. |
| V2-KAT-05 to 08 | Document v2: 0 bytes, 37 bytes, exactly one segment, one segment + 5 bytes, two segments (mirrors E0-6) |
| V2-KAT-09 | SealedKey (idempotency) |
| V2-KAT-10 | Portable form serialization (§3.10) |
| V2-KAT-11 | Every header field's encoding, including maximum-length reference and purpose |
| V2-KAT-12 | v1 vectors F1, F2, documents, K1–K4 and KAT-M1-01 reproduce unchanged in the v2-capable build |
| V2-RT-01 to 05 | Round trips for every logical type, empty string, maximum field length, multi-segment documents, each provider (local, fake Vault, real Vault) |
| V2-ISO-01 to 06 | Seed, reference or row of organization B under organization A; purpose swap; record, field, item or logical-type swap; attachment or version swap; kind swap (document key as a field key). Each fails with the expected reason, without the provider being called where the catalog check applies. |
| V2-TMP-01 to 12 | Tamper with each header field, the commitment, the wrapped seed, nonce, tag, ciphertext, segment order, truncation, final flag, trailing bytes, non-canonical reference, reserved flags. Inconsistent wraps within one data-key group. Duplicate item in a batch. Each is rejected; zeroization asserted on the failure paths. |
| V2-ROT-01 to 04 | Catalog: provisioning does not activate; activation is compare-and-set and journaled; stale expected version refused; a staged write under the old version is refused once, then succeeds re-staged. |
| V2-ROT-05 to 09 | Reseal campaign: current heads move to the new version and v2. History unchanged and still readable under the retained version. Census counts per version. Attestation refused while any remains. Concurrent user edit wins without loss. |
| V2-ROT-10 to 12 | Rewrap documents: only wrap columns change, commitment identical, object untouched, download reproduces the bytes. A rewrap with a different seed fails closed at read. |
| V2-ROT-13 to 14 | Retire, then unwrap still works. Suspend, then unwrap is refused with `KEY_UNAVAILABLE`, without the provider being called. |
| V2-PRF-01 to 03 | Unwrap calls per read: 1 for an owner-saved record of 250 encrypted values, versus 250 under v1. k + 1 after k share edits. Seed never reused across operations. Local timing recorded as non-PB-09 evidence. |
| V2-PROF-01 to 06 | Router dispatches only to the organization's family. Unknown or unregistered family fails closed. A development family outside Development/Testing refuses startup. Registration with a family not satisfying the profile is refused. A family change command does not exist (architecture test). No fallback when the organization's provider is down. |
| V2-CMP-01 to 06 | v1 reads after M2, M4 and M5. Owner save converts v1 to v2. Authority refuses new v1 after cutover. A pre-E-3 business build reading v2 fails closed. The Authority fence refuses a pre-M2 Authority. Signed-column registry passes for schema 2. |
| V2-REC-01 to 06 (local) | Provider unavailable and recovered. Version missing at the provider (503, then readable again after restore). Catalog and provider mismatch. AuthorityDb restore reverting Activate (safe) and reverting Suspend (blocked by replay). Interrupted campaign resumes idempotently. Corrupted envelope via direct SQL causes quarantine. |
| V2-ARCH-01 to 03 | No logging of seed, PRK or keys in crypto classes; decryption call-site allow-list updated with reasons; no use of provider-generated data-key endpoints. |

### 10.2 Needs external infrastructure or independent assessment (not run in E-3)

| ID | Test | Needs |
|---|---|---|
| V2-HSM-01 to 05 | Managed HSM wrap and unwrap algorithm behaviour, AAD support, wrapped length, explicit-version semantics, latency and throughput | real Managed HSM (E-6c), separate authorization |
| V2-AKV-01 to 03 | Key Vault RSA-OAEP-256 wrap, software-profile behaviour, key backup and restore within geography | Azure Key Vault |
| V2-REC-07 to 10 | Separate replacement HSM restore with Security Domain quorum; Canada Central → Canada East manual path; coordinated restore with blobs and both databases; RTO and RPO | Azure staging (G14, PB-10) |
| V2-PRF-04 | PB-09 p95 read and write with v2 at 250 fields and 100 documents | Azure staging (PERF-01) |
| V2-IND-01 | Independent cryptographic review of §3 (format, KDF use, commitment, nonce discipline) | independent reviewer (INDEPENDENT_CRYPTO_REVIEW_PACKAGE) |

Stage B integration shards stay manual and on demand (owner rule C-131). E-3 milestones list the shards an owner may choose to run, but never run them as part of a milestone.

---

## 11. Open architectural decisions

| ID | Decision | Recommendation |
|---|---|---|
| OD-E3-1 | Record of the E-9 approved direction in the repository | Write a short E-9 decision record before M3, so E-3 references a fixed text |
| OD-E3-2 | Who may start Reseal, Rewrap and Re-encrypt campaigns; production dual control | Security Administrator or Organization Owner proposes; a second distinct authorized principal approves; a Platform Operator executes the provider-side steps |
| OD-E3-3 | May the Authority hold a **metadata-read-only** identity on the encryption provider, to verify provenance at registration? | Yes. It closes the E-2 gap where the runtime cannot check key type. No crypto operations. |
| OD-E3-4 | W1 (wrap in the signed envelope, recommended) versus W2 (separate wrap table) | W1 |
| OD-E3-5 | Hardware profile families: Managed HSM `oct-HSM` only, or also Key Vault Premium HSM-backed RSA | E-9 decision; E-3 supports either through the wrap registry |
| OD-E3-6 | Software profile family (Key Vault Standard RSA-OAEP-256?) | E-9 decision |
| OD-E3-7 | Integrity HMAC provider for the software profile (Key Vault has no HMAC) | E-9 decision; it blocks a usable software profile end to end |
| OD-E3-8 | Profile assignment of existing development organizations | `Unassigned` + `Development` assurance, refused outside Development/Testing |
| OD-E3-9 | Shared HSM pool with per-key RBAC, versus per-organization pools or identities | Shared pool for the first release; record the logical-only isolation against the business runtime as residual risk |
| OD-E3-10 | Managed HSM wrap mechanism (A256KW / A256KWPAD versus A256GCM with AAD) | Decide after an E-6c real-HSM experiment. v2 does not depend on native AAD. |
| OD-E3-11 | Re-encrypt v1 documents ever, or keep them v1 until crypto-erase | Keep v1 unless a KEK version must be destroyed |
| OD-E3-12 | Reason-note envelope stays v1 | Yes (E3-D12) |
| OD-E3-13 | Suspend and Destroy on the gating stream (anti-rollback, replayed) versus the audit stream | Gating stream |
| OD-E3-14 | Field seal-batch scope: (record, operation, purpose), recommended, versus per field per operation | (record, operation, purpose) |
| OD-E3-15 | Document release mode (added at the E3-M1 gate, §13.9): stream authenticated segments (v1 behaviour, an authenticated prefix may reach the client before a later failure) versus verify-then-release for organizations that require it | **Decided (owner, 2026-11-14).** Authenticated-segment streaming is accepted as an M1 limitation. M4 must support verify-then-release document delivery, using bounded memory or protected staging; streaming is subject to explicit policy. |
| OD-E3-16 | Operation-boundary enforcement (added at the E3-M1 gate, §13.10): Authority rule that a new v2 data-key id appears in no earlier accepted revision and belongs to one operation and one record | **Decided (owner, 2026-11-14).** Seed reuse across distinct operations remains unresolved in M1. M2 must enforce data-key id uniqueness across accepted revisions, with an explicit exemption for unchanged carried-forward envelopes. |

---

## 12. Recommendation for owner approval

1. **Approve the design direction** E3-D1 to E3-D14:
   - two-tier, non-exportable provider KEKs with no cached intermediate;
   - Envelope v2 as specified in §3;
   - seal-batch field keys;
   - wrapped seed kept in the signed envelope;
   - an Authority encryption-key catalog;
   - governed Reseal, Rewrap and Re-encrypt;
   - the version lifecycle with Invariant K-1;
   - the E-9 boundary of §4.
2. **Resolve OD-E3-1 to OD-E3-5** before M3. OD-E3-6, OD-E3-7 and OD-E3-10 can wait for E-9 and the provider milestones, but **block any production claim**.
3. **Authorize E3-M1** (crypto core and known-answer vectors, no wiring, no database, no Authority change) as the first implementation step.
4. Authorize **E3-M2 separately**, because it changes Authority behaviour.

Each later milestone needs its own authorization. Nothing in this package changes a production or certification gate. Production still requires:
- real providers and the E-9 profile implementation;
- Azure staging evidence (G14, PB-09, PB-10);
- independent review (ADR-PB-003, ADR-PB-012).

---

## 13. E3-M1 implementation record (Development/Testing; owner-led review)

**Authorization:** owner, 2026-11-13. It covers E3-M1 only: the isolated Envelope v2 core and deterministic test vectors.

**Branch:** `e3/m1-envelope-v2-core`, created from local `main` at `d6693838de4e7c2c421fe3f449cecb721d4fced2`. Not committed, pushed or merged.

### 13.1 Design verification before implementation (brief §3)

The shared seal-batch seed with HKDF-derived item keys was checked against each requirement. **No blocking issue was found, so the approved hierarchy is unchanged.**

| Requirement | Finding |
|---|---|
| Partial record updates | A shared (contributor) edit seals only the edited item in a **new** batch (fresh seed and data-key id). Unchanged items keep their envelopes byte for byte, so a revision can reference several batches. The reader groups by data-key id and unwraps once per group. An owner save re-seals every carried value (existing E-1 behaviour) into one batch. |
| Concurrent updates | Each staging attempt creates its own batch: a fresh seed from the CSPRNG and a fresh UUIDv7 data-key id. Two writers never share a seed, and the content head compare-and-set chooses the winner. **Writer rule:** a batch is never reused across attempts. It is created per attempt and is disposable; a disposed batch refuses to seal. |
| Historical field revisions | Historical rows are immutable and keep their own batch envelopes. Nothing is re-derived or rewritten. Reading history needs the batch's KEK version retained (Invariant K-1). |
| Per-field deletion | A deleted item is a missing row in later revisions. Its ciphertext stays only in history and backups. The shared seed survives in sibling rows, so the deleted item's key remains *derivable*, but without its ciphertext that grants nothing. This is equivalent to v1, where deletion is also the removal of the row and no per-value key can be destroyed separately (the KEK is shared). |
| Field movement or copying | Reordering a layout does not change `FieldDefinitionId`, so there is no effect. Copying an envelope to another item, field, record or organization fails: the item key and the AAD bind record, field, item and logical type, and the commitment binds organization, purpose and kind. A copy must be **re-sealed**. The existing carry-forward copies an envelope only for the **same** item, which stays valid. |
| Key rotation | A new batch is sealed under the catalog's current KEK version. Older batches stay readable under retained versions. Reseal re-encrypts into a fresh batch; Rewrap (documents only) keeps the seed and commitment. |
| Organization isolation | The organization is in H0, and so in the commitment and every derivation. The reference must name the same organization. The provider key is per organization. |
| Record and document domain separation | `payload_kind` and the purpose token are in H0. The HKDF labels `FieldItem` and `Document` are distinct, and none prefixes another. |

Two residuals are recorded rather than designed away:
- **R-M1-1, larger seed blast radius.** A leaked seed exposes one whole batch (one record revision of one purpose), where a leaked v1 DEK exposed one value. The seed exists only in the trusted business process, which can already unwrap anything, so the trust boundary is unchanged. The blast radius per accidental leak is larger.
- **R-M1-2, batch-to-record binding is enforced by the writer, not by the header.** H0 has no record id, so a batch's record binding comes from the sealer API: the record is fixed at creation and items carry no record parameter. Every item is still cryptographically bound to its record through `C_field`. Since the acceptance gate (§13.10), the opener also refuses a data-key group spanning two records within one call. Putting the record into H0 would not help against a writer that holds the seed, because such a writer can compute a commitment for any header.

### 13.2 Files

New, in `src/backend/EnterpriseForms.Infrastructure/Features/Documents/EnvelopeV2/`. Every type is `internal`.

| File | Content |
|---|---|
| `EnvelopeV2Format.cs` | Codes, constants, labels, strict UTF-8, wrap-family mapping (reusing the E-1/E-2 canonical reference parser `KeyScopeEncoding.TryParseReference`), the canonical big-endian writer and the strict reader, and `EnvelopeV2Exception` with fixed messages |
| `EnvelopeV2Header.cs` | `EnvelopeV2HeaderPrefix` (H0, validated), `EnvelopeV2Header` (H), `FieldItemIdentity` (C_field), `DocumentVersionIdentity`, `DocumentContext` (C_doc), `EnvelopeV2KeySchedule` (HKDF) |
| `FieldEnvelopeV2.cs` | `FieldEnvelopeV2` (portable codec), `FieldSealBatchV2` (sealer), `FieldEnvelopeV2Opener` |
| `DocumentEnvelopeV2.cs` | `DocumentEnvelopeV2` (portable header), `DocumentEnvelopeV2Cipher` (segmented encrypt and decrypt) |

Tests and evidence:

| File | State |
|---|---|
| `tests/features/EnterpriseForms.Api.ContractTests/EnvelopeV2KnownAnswerTests.cs` | new |
| `tests/features/EnterpriseForms.Api.ContractTests/EnvelopeV2Tests.cs` | new |
| `tests/features/EnterpriseForms.Api.ContractTests/EnvelopeV2TestSupport.cs` | new: test key double and helpers |
| `tests/architecture/EnterpriseForms.ArchitectureTests/EnvelopeV2IsolationTests.cs` | new |
| `tests/architecture/EnterpriseForms.ArchitectureTests/DecryptionCallSiteTests.cs` | modified: four reviewed v2 call sites allow-listed; the v2 entry points registered as decrypting operations |
| `docs/evidence/e3-envelope-v2/` | new: README, Python cross-check script and its output |

Unchanged: v1 envelopes and services, key providers, provider registration, API and Worker composition, Authority, SpiceDB, database schema and migrations, published forms, indexed-search contracts and E-9 onboarding.

### 13.3 Construction as implemented

It is exactly §3, with these clarifications made during implementation. None changes the hierarchy.

- **Strict reader.** A short read is `MalformedEnvelope`, including empty input. A wrong magic, another format version, an unknown kind, AEAD, KDF or wrap code are `UnsupportedEnvelope`. Non-zero flags, bounds and alphabet violations, a non-canonical or foreign-scope reference, a non-UUIDv7 data-key id, an empty organization, record, field or item id, a logical type outside its set, a wrapped seed not exactly 60 bytes, a ciphertext over 1 MiB, and trailing bytes are `MalformedEnvelope`.
- **Logical types.** A FieldValueItem carries one of `String`, `Integer`, `Decimal`, `Date`, `DateTime`, `Boolean`, `Guid`, `Option`. A SealedKey carries exactly `CreationFingerprintKey/v1` (the existing seal type). The alphabet is `[A-Za-z0-9/._-]`, 1–32 characters.
- **Strict UTF-8.** It is used both ways: invalid UTF-16 input is refused at seal, and authenticated bytes that are not UTF-8 are refused at open. v1 used replacement.
- **Opener order.** All key-free checks run first, for every request: kind and purpose, organization, expected identity, and group consistency (identical H and wrapped seed, one record, no repeated item or nonce). Then one unwrap per data-key group, the constant-time commitment check, item-key derivation, and AEAD. The first failure aborts the whole call. Provider exceptions propagate unchanged.
- **Zeroization.** The seed is zeroed as soon as the PRK exists. The PRK is zeroed on dispose, and item keys, document keys and plaintext buffers in `finally` blocks.
- **Randomness.** Production paths take seeds, nonces, nonce prefixes and data-key ids only from `RandomNumberGenerator` and `Guid.CreateVersion7()`. The deterministic overloads are `internal`, named for vectors, and used only by tests. A batch refuses a repeated nonce or item even on the deterministic path.
- **Batch serialization (gate, §13.10).** `Seal` and `Dispose` of one batch are serialized. Concurrent seals cannot both seal one item, and a dispose can never zero the key schedule while a seal is deriving from it; without this, a racing seal could encrypt under a key derived from an all-zero PRK, which anyone can compute.
- **Wrapped seed.** The wrapped seed is not part of H or the AAD. Its integrity is the provider's authenticated wrap, plus the commitment: a different seed fails it. Once wired, RowHmac also signs it (§3.8).

### 13.4 Known-answer vectors

All were produced independently with Python `cryptography` 50.0.2 from §3 (`docs/evidence/e3-envelope-v2/`) and are asserted byte for byte:

| Vector | Pinned values |
|---|---|
| B1 (FieldValue batch of three) | H0, commitment `0c323214…2d28`, item keys, AAD, ciphertext and tag per item, portable SHA-256 and length (317, 313, 321 bytes) |
| S1 (SealedKey) | H0, commitment `710d7ae7…267d`, ciphertext, tag, portable SHA-256 (387 bytes) |
| D0–D4 (documents) | H0, commitment `191701db…ee34`, document key, portable header, ciphertext length and SHA-256, every segment tag, full ciphertext of D0 and D1 |

The document plaintext SHA-256 values equal the E0-6 v1 values (same plaintext pattern): a cross-check. Every v1 vector is unchanged: `EncryptionKnownAnswerTests` passes as part of the full suite.

### 13.5 Test results (focused; no stack; Stage B not run)

| Run | Result |
|---|---|
| `EnvelopeV2KnownAnswerTests` + `EnvelopeV2Tests` | **29 passed**, 0 failed |
| `EnterpriseForms.ArchitectureTests` (full) | **124 passed**, 0 failed (includes `EnvelopeV2IsolationTests` ×4 and the updated `DecryptionCallSiteTests`) |
| `EnterpriseForms.Api.ContractTests` (full) | **770 total: 761 passed, 0 failed, 9 skipped.** The 9 skips are the real-Vault classes `VaultTransitDockerIntegrationTests` and `KeyActivationVaultIntegrationTests`, which skip without their throwaway-container script. |

One defect was found by the tests and fixed before the final run: a batch for an empty record id called the key provider (a wrap) before refusing. The check now precedes the wrap.

What the tests cover:

| Area | Tests |
|---|---|
| Known-answer vectors | B1, S1, D0–D4, H0 field offsets |
| Round trips through the real Development keyring | every logical type, empty, Unicode and 64 KiB strings; SealedKey; documents at 0, 1, 37, segment − 1, one segment, two segments + 3 bytes |
| Incorrect keys | random seed → commitment mismatch; 16-byte seed → invalid data key; another keyring → the provider's own refusal |
| Identity checks before any key operation | organization, purpose, record, field, item, logical type |
| Relabelling with a provider that returns the true seed for any request (models a wrap without associated data) | organization, KEK version, data-key id, family or kind → commitment mismatch; record, field, item or type → AEAD failure |
| Copying | a ciphertext moved between items of one batch fails |
| Tampering | every single-bit flip of a 317-byte portable envelope, 2,536 cases, is refused; each authenticated byte is bound even with the permissive provider (the wrapped-seed range is the documented exception) |
| Specific tampering | commitment, ciphertext, truncated ciphertext, tag, nonce, wrapped seed |
| Data-key groups | inconsistent groups (another wrap or commitment, repeated nonce or item) refused before any unwrap |
| Documents | segment tamper, final-segment drop, partial tag, trailing bytes, empty stream, reordering, oversize frame, identity mismatch, relabelled version and nonce prefix, commitment tamper |
| Malformed input | every truncation length; trailing byte |
| Unsupported input | magic, versions 1 and 3, kinds 0 and 4, AEAD 2, KDF 2, wrap codes 0, 3, 4 and 255, segment size; document versus field cross-parsing |
| Malformed fields | 18 cases, plus six non-canonical references |
| Failure messages | carry no identifiers or hex |
| Nonces | 2,000 distinct nonces in one batch; repeated nonce and item refused |
| Freshness | fresh data-key id, commitment, wrap and ciphertext per batch; fresh nonce prefix and data key per document version |
| Domain separation | nine distinct derivations; no label prefixes another; disposed schedule refuses |
| Batch creation | foreign purposes, empty record and foreign, legacy or other-scope references refused with **no** wrap call |
| Legacy isolation | the v1 service refuses v2 rows (version 2, and relabelled as 1); v1 AAD and ciphertext are not v2; `local-dev-kek-v1` names no v2 family; a v1 wrapped DEK presented as a v2 seed fails the commitment |
| Outage and cancellation | provider outage and cancellation propagate, with no fallback |
| Isolation (architecture) | no product type outside the namespace references v2 (Business and Authority assemblies), with a positive control; every v2 type is non-public; no logging, console, debug or trace reference |

### 13.6 Compatibility findings

- **Disconnected.** No product code reaches the core. This is enforced by `EnvelopeV2IsolationTests`, and by `DecryptionCallSiteTests`, which now fails on the first product caller of `FieldEnvelopeV2Opener.OpenAsync` or `DocumentEnvelopeV2Cipher.DecryptAsync`.
- **Providers.** `IEncryptionKeyProvider` is used unchanged. Both Development providers wrap the 32-byte seed exactly as they wrap a v1 DEK (60-byte wrap), so no provider change is needed for M2.
- **References.** The v2 reference grammar is the existing scoped grammar. The parser is reused, not duplicated.
- **Size.** H0 is 121 bytes for a field-value reference. A portable field envelope is about 300 bytes plus the ciphertext; the stored row form (M2) is smaller, because H and C are rebuilt from columns.
- **Confirmed again: M2 needs Authority changes** (allow-lists, signed schema 2, shape checks, census, rollback fence), as §8.1 states. Nothing in M1 needs or makes them.

### 13.7 Remaining security risks (E3-M1 scope)

| Risk | Note |
|---|---|
| R-M1-1 | Seed blast radius per batch (§13.1) |
| R-M1-2 | Batch-to-record binding is a writer rule (§13.1). Mitigated within one open call by the opener's group rule (§13.10). |
| R-M1-3 | **No independent cryptographic review.** The construction (HKDF use, commitment as key commitment, nonce discipline, encoding) has been reviewed only by the owner-led process. V2-IND-01 is required before production. |
| R-M1-4 | Streaming document decryption releases each authenticated segment before the whole stream is known to be complete. A later failure (truncation, tamper) throws after earlier segments were written. Same as v1. The caller contract is in §13.9 and is tested. Bytes already sent to an HTTP client cannot be recalled. Accepted as an M1 limitation; M4 must support verify-then-release (OD-E3-15, decided). |
| R-M1-5 | Managed strings: the field plaintext arrives and leaves as .NET strings, which cannot be zeroed. Same as v1. |
| R-M1-6 | No production provider wraps a v2 seed yet. Managed HSM and Key Vault wrap behaviour is untested (E-6 tested HMAC only). |
| R-M1-7 | Not wired: none of the v2 benefits (fewer unwraps, provider-neutral binding, commitment) apply to stored data until M2/M4. |
| R-M1-8 | **Operation boundary.** One seed per content operation is a writer rule: a live batch object reused by a caller for a second staging attempt is not detectable in M1, because the core has no notion of an operation. Re-sealing the same item in the same batch is refused. Accepted as unresolved in M1; M2 must enforce data-key id uniqueness across accepted revisions (OD-E3-16, decided; §13.10). |

### 13.8 Decisions required before E3-M2

1. **Accept E3-M1** as implemented, including the clarifications in §13.3 and the residuals R-M1-1 and R-M1-2.
2. **Authorize an Authority behaviour change** for M2: envelope-version and algorithm allow-lists, signed schema 2 for `field_encrypted_values` and `file_attachment_versions`, v2 shape and group-consistency checks, census categories and the rollback fence.
3. **Authorize the business migration** for M2: three nullable columns on three tables, with CHECK constraints.
4. **Decide OD-E3-4** (wrap kept in the signed row) and **OD-E3-14** (batch scope), which M2's schema fixes.
5. **Decide whether V2-IND-01 must precede M2 or M4.** Recommendation: before M4, when v2 data first gets written.
6. **Optional:** a commit of E3-M1 on this branch, signed as for the earlier E-milestones.
7. **Decide OD-E3-16** (the M2 operation-boundary rule, §13.10) and **OD-E3-15** (document release mode, §13.9).

**Decided by the owner on 2026-11-14:** items 1 (E3-M1 accepted with the documented residual risks), 6 (local signed commit authorized) and 7 (OD-E3-15 and OD-E3-16, recorded in §11). Items 2 to 5 remain open, and E3-M2 is not authorized.


### 13.9 Document decryption caller contract (gate check 2)

`DocumentEnvelopeV2Cipher.DecryptAsync` streams, exactly as v1 does. Each segment is authenticated before it is written to the sink, so a later failure (truncation, tamper, reordering, a dropped final segment) throws after earlier segments were written.

The contract is stated on the type and tested by `ALaterSegmentFailureReleasesOnlyWholeAuthenticatedSegmentsAndNeverSignalsCompletion`:

1. **The only success signal** is normal completion of `DecryptAsync`. Until then, the sink holds provisional output.
2. **Provisional output** is always whole authenticated segments: an exact prefix of the plaintext at segment boundaries. No byte of an unauthenticated segment is ever written, and the sink is flushed only on success.
3. **Failures before the first segment write nothing:** identity, foreign key, commitment, empty stream and short first frame.
4. **On any exception the caller must discard the sink.**
   - A server-side sink (export, scan, preview, reseal) must be a non-published staging location. It is published only after completion and deleted on failure.
   - An HTTP response must be aborted, never completed or retried onto the same response, and must carry the full `Content-Length` so the client sees the truncation.

**Can production integration discard all partial plaintext?**
- **Server-side sinks: yes**, with the staging rule above.
- **HTTP downloads: no.** The authenticated prefix already transmitted cannot be recalled. The existing v1 download (`DocumentHandlers.DownloadAsync`, `DocumentEndpoints`) sets `Content-Length` to the plaintext size, streams, and records the failure once; an exception after the response has started fails the transfer. This was verified by reading the code, not by an HTTP test in M1.

What is released is authentic plaintext of the document the caller is authorized to read, never attacker-chosen bytes. If an organization requires that no byte leave before the whole document is verified, the integration needs a verify-then-release mode. For example, decrypt to an encrypted temporary staging object, then stream it out. The owner decided **OD-E3-15** on 2026-11-14: authenticated-segment streaming is accepted as an M1 limitation, and M4 must support verify-then-release document delivery, using bounded memory or protected staging, with streaming subject to explicit policy. It is not M1 work.

### 13.10 E3-M1 final acceptance gate (owner brief, 2026-11-14)

Development/Testing; owner-led review.

**Check 1. Cryptographic scope of a shared wrapped seed**

| Boundary | How it is enforced | Result |
|---|---|---|
| Organization | Organization in H0, so in the commitment and every derivation. The reference must name the organization (`EnvelopeV2HeaderPrefix.Create`). The provider wrap is scope-bound to (organization, purpose). The opener refuses another organization before any key operation. | Pass (cryptographic and reader) |
| Purpose | Kind and purpose token in H0 and the commitment; kind-to-purpose mapping checked by the batch and the opener; per-purpose provider key. | Pass (cryptographic and reader) |
| Record | Record id in `C_field`, so in each item key and AAD. The batch fixes the record at creation. **Added at this gate:** the opener refuses a data-key group spanning two records, before any unwrap. | Pass (cryptographic per item; reader per call; writer per batch) |
| Operation | Each `CreateAsync` draws a fresh seed and UUIDv7 data-key id. No production path can construct a batch from an existing seed: the deterministic constructors are `internal`, and only the contract tests are a friend assembly. Re-sealing an item already sealed in a batch is refused. A caller that keeps one batch alive for a second attempt with different items is **not detectable in M1** (R-M1-8). | **Inconclusive** until M2 |
| Partial updates | A partial update seals only the changed items in a new batch, so a revision references several groups. The opener checks each group independently (existing test `OneUnwrapServesAWholeBatchAndEachDistinctBatchCostsOne`). | Pass |
| Concurrency | Concurrent saves use separate batches. **Added at this gate:** `Seal` and `Dispose` of one batch are serialized. Before this, concurrent use could seal one item twice, corrupt the batch's item set, or let a dispose zero the PRK under a running derivation. | Pass (after the fix) |
| Retries and idempotency | A retry creates a new batch; ciphertexts differ by design and nothing compares them. Creation idempotency compares a MAC of the canonical plaintext request (`CreationRequestFingerprint`), and its sealed key is a batch of one per attempt. | Pass |

**M2 operation-boundary rule (OD-E3-16, decided by the owner 2026-11-14).** Seed reuse across distinct operations remains unresolved in M1. M2 must enforce data-key id uniqueness across accepted revisions, with an explicit exemption for unchanged carried-forward envelopes. It is not M1 work. The rule as proposed at the gate: the Authority accepts a v2 row in a candidate only if one of these holds:
- it is byte-identical to the accepted row of the same item (carried forward);
- its `data_key_identifier` appears in no earlier accepted revision of the organization, and all new rows with that identifier belong to this operation and this record.

Neither the format nor the hierarchy changes.

**Check 2. Document decryption safety:** Pass, with the limitation in §13.9 (OD-E3-15).

**Check 3. Isolation:**

| Item | Evidence | Result |
|---|---|---|
| Every v2 type internal | `EnvelopeV2IsolationTests.EveryEnvelopeV2TypeIsNonPublic`; no `public` type declaration in the folder | Pass |
| No production caller | `EnvelopeV2IsolationTests.NoProductCodeOutsideTheCoreReferencesEnvelopeV2` (Business and Authority assemblies, with a positive control); `DecryptionCallSiteTests`; no source outside the folder names a v2 type | Pass |
| v1 unchanged | No tracked source file changed (the tracked diff is `DecryptionCallSiteTests.cs` and the two manifest files); `EncryptionKnownAnswerTests`, `EncryptionRemediationTests`, `KeyProviderInterfaceV2Tests`, `VaultTransitKeyProviderTests`, `AuditorReasonNoteSealerTests` and `RecordCreationIdempotencyUnitTests` pass | Pass |
| Authority, databases, provider registration, E-9 | Untouched: no file under the Authority, migrations, composition or provider registration is changed | Pass |
| Spec manifest | Regenerated; `validate-specifications.py --strict` passes | Pass |
| No secrets in logs or evidence | v2 code references no logging, console or trace API (architecture test). Failure messages carry no content (`ExceptionsNeverCarryEnvelopeContent`). The evidence holds only fixed synthetic vector inputs and outputs. None of the development KEKs or tokens configured in the repository appears in the evidence, the v2 tests or the core, and the test keyrings (`Seq(0x00,32)`, `Seq(0x40,32)`) equal no configured key. | Pass |

**Tests added at the gate**, in `EnvelopeV2Tests`:
- `ADataKeyGroupSpanningTwoRecordsIsRefusedBeforeAnyUnwrap`;
- `ConcurrentSealsNeverSealAnItemTwiceAndADisposeNeverRacesADerivation`;
- `ALaterSegmentFailureReleasesOnlyWholeAuthenticatedSegmentsAndNeverSignalsCompletion`.

Each was checked against a deliberate regression, and each restored source file was verified identical to its saved copy:
- removing the opener's record rule fails the first test;
- removing the batch lock failed the second in 10 of 10 runs (a double seal, or corruption of the item set);
- writing a failing segment's bytes to the sink fails the third.

**Results after the gate:**

| Run | Result |
|---|---|
| `EnvelopeV2Tests` + `EnvelopeV2KnownAnswerTests` | 32 passed |
| Focused v1 encryption classes, together with the v2 classes | 122 passed, 0 failed, 0 skipped |
| `EnterpriseForms.ArchitectureTests` | 124 passed |

The full contract suite was not re-run (focused gate). Stage B was not run, and no Azure call was made.

**Owner acceptance (2026-11-14).** E3-M1 is accepted for Development/Testing with the documented residual risks R-M1-1 to R-M1-8. OD-E3-15 and OD-E3-16 are decided as recorded in §11. Independent cryptographic review V2-IND-01 remains required before production approval. E3-M2 is not authorized. This is an owner-led acceptance, not an independent review or a certification.

---

## Sources inspected

- **Business:**
  - `AesGcmFieldEncryptionService.cs`, `AesGcmFileEncryptionService.cs`, `EncryptionKeyProviders.cs`, `VaultTransitKeyProvider.cs`, `EncryptionProviderEnvironmentPolicy.cs`;
  - `DocumentPorts.cs`, `IFieldEncryptionService.cs`, `DecryptedFieldValues.cs`, `CreationRequestFingerprint.cs`, `AesGcmAuditorNoteSealer.cs`;
  - `FormRecord.cs` (`FieldEncryptedValue`), `UploadSession.cs`, `FormRecordEntityConfigurations.cs`, `DocumentEntityConfigurations.cs`, `BusinessSignedSchemas.cs`.
- **Authority:**
  - `ContentDeltaRules.cs`, `ContentService.cs` (`EnvelopeLooksApproved`), `ReprotectionRules.cs`, `AuditorReasonNoteEnvelopeValidation.cs`;
  - `IntegrityKeyRegistration`, `OrganizationOnboardingService.cs`, `OrganizationOnboardingPolicy.cs`;
  - `CommandPlanner.UpdateMandatoryPolicyAsync`.
- **Documents:**
  - `E0_ENCRYPTION_REMEDIATION.md`, `E1_KEY_PROVIDER_INTERFACE_V2.md`, `E2_VAULT_TRANSIT_PROVIDER.md`, `E4_CONTROLLED_KEY_ACTIVATION.md`, `E5_HMAC_SIGNING_KEY_ROTATION.md`, `E6A_MANAGED_HSM_COMPATIBILITY_PROBE.md`;
  - `DOCUMENT_ENCRYPTION_AUDIT.md`, `FIELD_PROTECTION_RECONCILIATION.md`, `docs/dynamic-forms/row-integrity-format.md`;
  - `trust_authority/CRYPTOGRAPHIC_SIGNING_AUTHORITY.md`, `trust_authority/COORDINATED_DISASTER_RECOVERY.md`, `SECURITY_GAP_ASSESSMENT.md` (G04, G12, G14, G19).
- **Tests located:** `EncryptionKnownAnswerTests`, `KeyProviderInterfaceV2Tests`, `KeyActivationTests`, `VaultTransitKeyProviderTests`, `DecryptionCallSiteTests`, `SignedColumnRegistryTests`, `ReprotectionTests`, `IntegrityKeyActivationTests`.
