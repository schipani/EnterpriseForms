# Search entry seal format — `DynamicForms/SearchEntrySeal/v1` (IX-1, C-126)

- **Status:** implemented in IX-1 (`src/shared/EnterpriseForms.Integrity/Search/SearchEntrySealV1.cs`), with vectors in `trust_authority/search-entry-seal-v1.test-vectors.json`. **Not independently reviewed.** It is part of the independent cryptographic review scope (indexed-search review package Q7; Stage A A-F1 is still open for the row format).
- **Primitive:** ES256 = ECDSA over NIST P-256 with SHA-256 of the message; signature in the fixed 64-byte IEEE P1363 form (r ‖ s, as JWS ES256). No new primitive. Only the message layout is new.
- **Keys:** one dedicated signing key per environment, key purpose `search-entry-seal-v1`.
  * Production: an EC-HSM P-256 key in Azure Managed HSM (not implemented in IX-1).
  * Docker: a non-exportable Vault Transit `ecdsa-p256` key.
  * Unit tests: an in-process key.
  * The organization is bound **inside** the message.
  * The verification public key comes only from deployment configuration (`SearchIndex:Seal:PinnedKeys`), never from AuthorityDb. The signer is used only after its public key equals the pin.

## 1. Encoding rules

These are the RowHmac v1 conventions (`row-integrity-format.md` §3):
- big-endian unsigned integers;
- UUIDs as 16 bytes big-endian (RFC 9562 byte order);
- identifiers as `u16` length plus ASCII from `[A-Za-z0-9-_./]` (1 to 200 characters);
- text as `u32` length plus strict UTF-8, written exactly as given;
- byte strings as `u32` length plus the bytes.

## 2. Layout

```
u8      0x11                                  framing marker (RowHmac v1 uses 0x10)
ascii16 "DynamicForms/SearchEntrySeal/v1"
u16     seal_schema_version = 1
u16     derivation_version  = 1               SearchPostingDerivation/v1
ascii16 key_name                              e.g. "search-entry-seal-v1"
u32     key_version                           >= 1
uuid    organization_id
uuid    form_definition_id
uuid    form_record_id
uuid    committed_operation_id                the head operation the entry was derived at
u64     committed_content_revision            >= 1
bytes   committed_manifest_root               exactly 32
text    record_status_name                    from the verified header leaf
u32     coverage_count, then per coverage, sorted by (field_definition_id, field_search_approval_id) bytes:
          uuid field_definition_id; uuid field_search_approval_id;
          ascii16 value_type; ascii16 classification; ascii16 protection_mode;
          u32 operator_count; ascii16 operator ... (sorted ordinal, no duplicates)
u32     posting_count, then per posting, sorted by (field_definition_id bytes, manifest_item_key bytes):
          uuid field_definition_id; uuid field_search_approval_id; bytes manifest_item_key (17);
          uuid manifest_item_id; ascii16 entity_kind; ascii16 value_type;
          bytes source_canonical_digest (32) = SHA-256 of the item's verified RowHmac v1 canonical bytes;
          u8 tag + value:
            0x08 text  normalized string (SearchValueNormalization/v1: NFC, trim, invariant upper case)
            0x03 i64   integer
            0x04       decimal: i32 scale (>= 0), u16 length, two's-complement big-endian coefficient
            0x05 i32   date: days since 1970-01-01
            0x06 i64   date-time: microseconds since the Unix epoch, UTC (sub-microsecond is a format error)
            0x01 u8    boolean (0 or 1)
            0x07 uuid  option id
u32     anomaly_count, then per anomaly, sorted by (field_definition_id bytes, kind ordinal):
          uuid field_definition_id; ascii16 anomaly_kind (EncryptedValuePresent)
```

## 3. Format errors

Each of these fails closed (`IntegrityFormatException`):
- duplicate coverage per field;
- duplicate operator, posting (same field and item key) or anomaly;
- a posting without a coverage for its field and approval;
- wrong root, digest or item-key length;
- `key_version` 0;
- revision 0;
- a negative decimal scale;
- a sub-microsecond timestamp;
- invalid identifier characters.

## 4. Properties and limits (by design, not measured)

**Bound by the signature:**
- organization, form and record;
- the accepted operation, revision and manifest root;
- for each posting: field, type, classification, protection and approval id with its operators (at derivation), the exact source bytes (by digest) and the derived value.

**Freshness is not bound.** A replayed older valid entry with a matching rolled-back head is detected only by external anchoring and reconciliation (C-125 §7, R5), which is not in IX-1.

**Malleability:** ECDSA signatures are malleable: (r, n−s) verifies the same message. A vector records this. It cannot produce a signature over a different message, and the index never treats signature bytes as an identifier.

**Not cross-language checked:** the vectors are produced and checked by the .NET implementation only. A second, independent implementation (as exists for RowHmac v1 in Python and Node) has not been written.
