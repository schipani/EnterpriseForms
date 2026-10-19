# Canonical Row Integrity Format — `DynamicForms/RowHmac/v1`

**Status:** Gate 0 deliverable (BUILD-02). Format frozen for v1; machine-verified by three independent implementations
(Python reference `scripts/integrity/row_integrity_v1.py`, C# `src/shared/EnterpriseForms.Integrity/Canonical/`,
Node `scripts/integrity/verify-vectors.mjs`) against `trust_authority/row-integrity-v1.test-vectors.json`.
**Independent human cryptographic review is still required** before Stage A sign-off (see §9).

Normative requirements come from `CLAUDE.md` §4L.2 (canonical payload), §4L.1 (protected columns) and
`trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md` §2.1 (leaf binding to `row_signature` and
`canonical_payload_digest`). This document defines the exact bytes. Any incompatible change increments the version
string and `row_signature_schema_version`; historical rows keep verifying under their recorded version.

## 1. Purpose and non-goals

The canonical bytes are the sole input to `row_signature = HMAC-SHA-256(org_integrity_key[key_version], canonical_bytes)`
and to `canonical_payload_digest = SHA-256(canonical_bytes)` used by the Merkle leaf. Two implementations that agree on
the inputs MUST produce identical bytes regardless of language, locale, time zone, JSON library, PostgreSQL column
order or .NET formatting.

Not provided by this format: confidentiality (plaintext typed values remain readable to a database reader), proof of
authorization (a valid tag never confers permission), proof of currency (anti-replay is the authority head/revision),
or proof of completeness (membership is the Merkle manifest and authority leaf index).

## 2. Byte layout

All integers are big-endian. `UUID16` is RFC 4122 network byte order, identical to the canonical hex string order.
(.NET: `Guid.TryWriteBytes(span, bigEndian: true, out _)`; `Guid.ToByteArray()` without the flag is mixed-endian and
is forbidden.)

```text
canonical_bytes =
  0x10                                      framing marker (RowHmac v1)
  || UInt16BE(len) || ASCII("DynamicForms/RowHmac/v1")
  || UInt16BE(schema_version)               row_signature_schema_version, 1..65535
  || UInt32BE(key_version)                  row_signature_key_version reference (not key material), >= 1
  || UInt16BE(len) || ASCII(entity_kind)    canonical table identifier, e.g. "form_records"
  || UUID16(organization_id)                trusted tenant identity from the authority context
  || UUID16(primary_key_id)                 the row's surrogate primary key
  || UInt64BE(row_revision)                 >= 1
  || UInt32BE(column_count)
  || for each registered column, in registry order:
       UInt16BE(len) || ASCII(column_name) || value_encoding
```

`value_encoding` is one type tag byte followed by type-specific bytes:

| Tag | Type | Encoding after the tag |
|---|---|---|
| `0x00` | NULL | nothing. Permitted only for columns registered `nullable: true`. |
| `0x01` | Boolean | 1 byte: `0x00` false, `0x01` true |
| `0x02` | Int32 | 4 bytes two's complement |
| `0x03` | Int64 | 8 bytes two's complement |
| `0x04` | Decimal | `Int32BE(scale) || UInt16BE(len) || minimal two's-complement coefficient` (see §3.3) |
| `0x05` | Date | `Int32BE(days since 1970-01-01, proleptic Gregorian)` |
| `0x06` | TimestampUtc | `Int64BE(microseconds since 1970-01-01T00:00:00Z)` (see §3.4) |
| `0x07` | Uuid | `UUID16` |
| `0x08` | Text | `UInt32BE(byte_len) || UTF-8 of NFC-normalized string` (see §3.5) |
| `0x09` | Bytes | `UInt32BE(len) || raw bytes` |
| `0x0A` | Json | `UInt32BE(byte_len) || UTF-8 of RFC 8785 canonical JSON, v1 subset` (see §3.6) |

The type tag written is always the **registered** column type; a value of a different logical type is rejected
(`TypeMismatch`), never coerced.

## 3. Encoding rules

### 3.1 Signed-column registry

Every `(entity_kind, schema_version)` has an immutable registry entry listing `(column_name, type, nullable)`.
Column names are lowercase ASCII snake_case and MUST be unique and sorted by ordinal byte order; a registry that is
not sorted is rejected at load (`RegistryNotSorted`). Registry order is therefore language-independent.

- **All security-significant columns are signed, nullable or not** (`owner_user_id`, `owner_team_id`, tenant, flags,
  timestamps, classification/encryption settings). The rejected "NOT NULL columns only" shortcut is not used.
- Values must supply **exactly** the registered columns: an extra key is `UnregisteredColumn`; a missing key is
  `MissingColumn`. **Absent is not NULL**: NULL must be stated explicitly.
- Adding a column to a table never changes existing signatures. It is protected by a new `schema_version` with a
  planned, audited migration (CLAUDE.md §4L.6). New dynamic form fields are separate signed value rows and never
  cause `form_records` rows to be re-signed.
- The column name is included in the bytes so a registry mismatch between signer and verifier is detected.

### 3.2 Integers and booleans

Fixed-width two's complement, big-endian. Boolean is one byte. Out-of-range input is rejected.

### 3.3 Decimal

The logical value is `coefficient × 10^-scale`, normalized so that the result is unique:

1. If the coefficient is 0 the encoding is `(coefficient 0, scale 0)`.
2. Otherwise strip trailing decimal zeros while `scale > 0` (`1.5000 → 15, 1`; `1234.5600 → 123456, 2`).
3. A negative scale (positive exponent) is expanded into the coefficient (`1.5E+3 → 1500, 0`).
4. The coefficient is the **minimal** big-endian two's-complement byte string (`128 → 00 80`, `-128 → 80`, `-1 → ff`,
   `0 → 00`).

Floating-point inputs are rejected; callers pass `decimal`/arbitrary-precision literals. Non-finite values are
rejected. `trust_authority/row-integrity-v1.test-vectors.json#decimalEncodingExamples` fixes nine examples.

### 3.4 Dates and timestamps

- `Date` is a calendar date with no time zone: days since 1970-01-01 (negative before). Date-only values are never
  shifted by time zones (CLAUDE.md §4E).
- `TimestampUtc` is an instant: microseconds since the Unix epoch after conversion to UTC (PostgreSQL `timestamptz`
  precision). Inputs MUST carry an explicit offset (`Z` or `±hh:mm`); naive timestamps are rejected
  (`TimestampOffsetRequired`). Sub-microsecond precision is rejected (`TimestampPrecision`), never silently truncated.
  `2024-03-10T02:00:00.000001-05:00` and `2024-03-10T07:00:00.000001Z` encode identically.

### 3.5 Text

Unicode NFC normalization, then UTF-8. Composed `Café` and decomposed `Cafe` + U+0301 produce identical bytes
(vector `ROWV1-TYPES-NFC-EQUIVALENCE`). No trimming, case folding, or whitespace collapsing. Unpaired surrogates are
rejected (`TextInvalidUnicode`). The empty string is `0x08 00000000` and is **distinct from NULL** (vector
`ROWV1-EMPTY-STRING-NOT-NULL`). Normalization requires ICU on Linux; runtime images use `*-chiseled-extra` variants.

### 3.6 JSON (v1 subset of RFC 8785)

Used for JSONB layout/configuration columns. The v1 subset guarantees identical output from PostgreSQL JSONB
(which reorders and de-duplicates keys) and from any JSON library:

- Objects: keys MUST be ASCII (`JsonKeyNotAscii` otherwise), sorted by ordinal code unit; no duplicate keys.
- Numbers: integers only, within ±(2^53−1), serialized as plain decimal digits. Any literal containing `.`, `e` or
  `E`, or any non-integer, is rejected (`JsonFloatNotPermitted`). Decimal or textual columns carry fractional data.
- Strings: escape only `"`, `\`, and control characters < 0x20 using `\b \f \n \r \t` short forms and otherwise
  `\u00xx` lowercase hex; all other characters are raw UTF-8.
- Separators are `,` and `:` with no whitespace. `true`, `false`, `null` literals.

### 3.7 Encrypted values

For `ApplicationEncrypted` fields the signed columns are the **ciphertext envelope** (ciphertext bytes, nonce, tag,
algorithm, key reference, envelope version, logical type). The HMAC therefore binds the exact ciphertext, never a
reconstructed plaintext; AEAD authentication is verified independently and neither mechanism depends on the other's
secret (CLAUDE.md §4L.2).

## 4. Key management binding

`key_version` is a reference into the organization's integrity key catalog held by the independent signing authority
(Vault Transit locally, Azure Managed HSM in production; `trust_authority/CRYPTOGRAPHIC_SIGNING_AUTHORITY.md`). Keys
are high-entropy, organization-scoped, purpose-separated (`row-integrity-v1`) and never present in business
PostgreSQL, JSON, logs or the browser. The business API never holds a key; it submits authorized, revision-bound
content commands and verifies through the authority. There is no generic "sign these bytes" endpoint.

The published test key `000102…1f` in the vector file exists only to make expected tags reproducible in CI.

## 5. Verification contract

A verifier MUST, in order: resolve the `(entity_kind, schema_version)` registry; reject unknown or retired versions
(`UnknownSchemaVersion`); re-encode the row from trusted inputs (organization, PK, revision, values) — never from
caller-supplied canonical bytes; compare the tag with a constant-time comparison; and treat **any** non-valid result
(`Invalid`, `Unsigned`, `UnknownKey`, `StaleRevision`, `ManifestMismatch`, `IntegrityServiceUnavailable`) as denial.
Nothing is auto-repaired or re-signed on read (CLAUDE.md §4L.5).

## 6. Relationship to the Merkle leaf

`canonical_payload_digest = SHA-256(canonical_bytes)` and the verified `row_signature` are both embedded in the
Merkle v1 leaf (`trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md` §2.1). The two framings use different domain
strings and separate test vectors; neither may be altered in place.

## 7. Registered schemas

Phase 0 froze the format and registered two schemas for conformance testing. Stage A (2026-10-18) registers the first
production tables in `src/backend/EnterpriseForms.Application/Features/FormRecords/Integrity/BusinessSignedSchemas.cs`;
`tests/architecture/.../SignedColumnRegistryTests.cs` rejects any protected EF column that is neither signed nor
explicitly justified as unsigned (`SEC-42`, acceptance test 82). The authority accepts candidate items only whose
entity kind is allow-listed for the Merkle item kind (`ContentService.AllowedEntityKinds`).

| Entity kind | Version | Status | Columns (ordinal order) |
|---|---|---|---|
| `form_records` | 1 | conformance vectors only | `created_by_user_id` Uuid, `created_form_version_id` Uuid, `form_definition_id` Uuid, `owner_team_id` Uuid?, `owner_user_id` Uuid?, `record_status` Text, `submitted_on` TimestampUtc?, `tenant_id` Uuid |
| `form_records` | 2 | **production (Stage A)** — RecordHeader leaf, pk = `form_record_id`, row_revision = content revision | `content_change_operation_id` Uuid, `created_by_user_id` Uuid, `created_form_version_id` Uuid, `form_definition_id` Uuid, `record_status` Text, `submitted_on` TimestampUtc?, `tenant_id` Uuid |
| `field_string_values` | 1 | **production (Stage A)** — FieldValue leaf, pk = physical staged row id, item id = `field_value_item_id` | `content_change_operation_id` Uuid, `field_definition_id` Uuid, `field_value_item_id` Uuid, `form_record_group_id` Uuid?, `form_record_id` Uuid, `string_value` Text, `tenant_id` Uuid |
| `integrity_type_coverage` | 1 | test-only | one column per type incl. nullable variants |

`form_records` v2 deliberately omits `owner_user_id`/`owner_team_id`: ownership is authoritative only in the
Authorization Authority and the business columns are an untrusted projection (TRUST_AUTHORITY_ARCHITECTURE.md §2).
Signing them would bind a stale projection into content history. The v1 entry remains for the conformance vectors.
The operation id is a signed column so a validly signed row from an earlier operation cannot be replayed into a later
candidate (SEC-P05).

## 8. Conformance vectors

`trust_authority/row-integrity-v1.test-vectors.json` contains seven positive cases (user-owned vs team-owned record,
full type coverage, NFC/decimal/offset/UUID-case equivalence, all-null, empty-string-vs-null, extreme ranges with
`row_revision = 2^64−1` and `key_version 3`), nine decimal encoding examples and eight negative cases. Each positive
case states the exact canonical hex, length, SHA-256 digest and HMAC with the published test key.

Reproduction:

```sh
python scripts/integrity/verify_vectors.py      # Python reference against committed JSON
node scripts/integrity/verify-vectors.mjs       # independent Node implementation
dotnet test tests/integrity                     # C# implementation (EnterpriseForms.Integrity.Tests)
python scripts/integrity/generate_vectors.py && git diff --exit-code trust_authority/*.test-vectors.json
```

## 9. Review requirements and open items

- [ ] Independent cryptographic review of the format by a reviewer who did not author it (Stage A gate owner records
      approval outside the business database, SECURITY_VALIDATION_RUNBOOK.md §4).
- [~] Real HMAC tags are produced through Vault Transit in the Docker stack for every Stage A record (SEC-P04/P05
      verify them through the authority); the explicit LocalHmac-vs-Vault same-key vector comparison (SIG-11) is
      documented as a reviewer procedure and not yet executed.
- [~] Registry entries exist for the two Stage A protected tables with a CI inventory test; every further protected
      table (CLAUDE.md §4L.1) must be added with its feature.
- [x] **F-03 decided (Stage A, 2026-10-18):** JSON v1 stays integer-only. No Stage A JSONB column is signed; layout
      JSON (`form_versions.layout_json`) is form-definition integrity (separate registry, Phase 2). Any future signed
      JSONB column that needs fractional numbers must carry them as Decimal/Text columns or wait for a reviewed v2 JSON
      subset — floats are never admitted into v1. Independent review of this decision is part of R3 in
      `docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md`.
- Known limitation: NFC normalization needs ICU at runtime; invariant-globalization mode is not permitted for any
  process that signs or verifies rows.
