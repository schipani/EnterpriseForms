# E-1 Key Provider Interface v2 (Development/Testing)

**Scope:** E-1 only, building on the accepted E-0 work. Owner-authorized development task; owner-led review. E-2 to E-10 are not started. There are no new algorithms, envelope formats, remote key services, database migrations, Authority changes or public API changes.

## Interface (E1-1, E1-2, E1-3, E1-4)

`IEncryptionKeyProvider` (Application, `Documents/Ports`) is provider-neutral and has two operations, both asynchronous and cancellable:

- `WrapDataKeyAsync(DataKeyScope, dataKey, CancellationToken) → WrappedDataKey(KeyReference, WrappedKeyBytes)`: wraps under the scope's **current** key version and returns the reference of exactly that version. There is no separate "current reference" lookup that could race with the wrap.
- `UnwrapDataKeyAsync(DataKeyScope, keyReference, wrappedDataKey, CancellationToken)`: the reference must name a **retained** version of **that** scope.
  - A reference to another organization or purpose fails as `CryptographicException`.
  - An unknown, disabled or unparsable reference fails as `EncryptionUnavailableException`.
  - Nothing is ever substituted.

`DataKeyScope(OrganizationId, Purpose)` refuses an empty organization. `EncryptionKeyPurpose` is a closed, strongly typed set with no public constructor: `FieldValue`, `Document`, `AuditorReasonNote` and `IdempotencyProtection`.

## Consumers (E1-7)

| Consumer | Organization | Purpose |
|---|---|---|
| Field values (`AesGcmFieldEncryptionService`, `FieldValueCodec.ProtectAsync`/`FormatAsync`) | value tenant | `FieldValue` |
| Documents (`AesGcmFileEncryptionService`) | document tenant | `Document` |
| Auditor reason notes (`AesGcmAuditorNoteSealer.SealAsync`) | command organization | `AuditorReasonNote` |
| Creation idempotency fingerprint key (`CreationRequestFingerprint.CreateAsync`/`MatchesAsync`) | tenant | `IdempotencyProtection` |

Field encryption and decryption are now asynchronous along the whole chain. That covers record projection, create, update, shared edit, lifecycle validation, restore, related forms, document responses, and the Worker's protection migration and reconciliation.

Synchronous validation code (the write rule engine, restore planner, record projection) does not block on key operations. It runs in three steps:
1. An asynchronous `DecryptedFieldValues.DecryptAsync` step decrypts the rows that the code reads.
2. The unchanged synchronous logic reads plaintext from that per-operation result.
3. Rows of encrypted fields are sealed afterwards.

The outcome is the same as before. The shared-edit path still decrypts lazily, per field read, and nothing is decrypted before the refusal checks; the existing C5 tests pin this.

Authority authorization boundaries and fail-closed behaviour are unchanged. The OpenAPI snapshot is unchanged.

## Local development keyring (E1-6)

`LocalDevelopmentKeyProvider` is still refused outside Development/Testing at startup (E0-1, API and Worker).

**Scoped reference (all new data):** `localdev:{organization}:{purpose token}:{version}`. It is canonical, unique per key version, and fits the Authority's reason-note key-reference rule and the 200-character columns.

- KEK = HKDF-SHA256-Expand(root PRK, "DynamicForms/LocalKeyring/Kek/v1" ‖ organization ‖ purpose ‖ version).
- Root PRK = HKDF-Extract(salt "DynamicForms/LocalKeyring/Root/v1", `LocalDevelopmentKeyring:RootKeyBase64`, or the legacy KEK when no root is configured).
- Wrap AAD = "DynamicForms/LocalKeyring/DekWrap/v1" ‖ organization ‖ purpose ‖ version. The organization and purpose are therefore authenticated in the wrapping context as well as being different keys.
- Wrapped-key layout is unchanged: nonce(12) ‖ ciphertext ‖ tag(16).

**Versions:** `LocalDevelopmentKeyring:Purposes:{Purpose}` sets `CurrentVersion`, `RetainedVersions` and `DisabledVersions`. Optional `LocalDevelopmentKeyring:Organizations:{organization id}:{Purpose}` tables replace the purpose table for one organization. With no configuration, every organization and purpose has current version 1.
- Only configured versions resolve; there is no automatic deletion and no on-demand derivation of unlisted versions.
- E-4: current and retained versions must also be listed in `ProvisionedVersions` (absent: version 1 only). See `E4_CONTROLLED_KEY_ACTIVATION.md`.
- An invalid table (current version disabled, overlap, unknown purpose, bad organization key, wrong-size or invalid root) makes the provider unavailable for every operation.
- Exceptions never contain key material.

**No configuration change is required:** existing `Encryption:LocalDevelopmentKekBase64` settings (dev stack, compose files, tests) keep working.

## Legacy compatibility (E1-5)

Data wrapped before E-1 references `local-dev-kek-v1`, which resolves only to the configured legacy KEK with its original wrap AAD.
- It is decrypt-only: new data never uses it.
- It can be switched off with `LocalDevelopmentLegacyKeyDecryptionEnabled = false`.
- Existing fields, documents and idempotency attempts remain decryptable. Existing reason notes are seal-only and untouched.
- No ciphertext or envelope format changed, and nothing is re-encrypted by a migration.

Evidence:
- The E0-6 v1 vectors (legacy-wrapped DEKs) decrypt unchanged.
- KAT-M1-01 still reproduces exactly, and its legacy-wrapped DEK still unwraps.
- A pre-E-1 idempotency attempt still matches.
- The new keyring wrap is pinned by vectors K1–K4, cross-checked independently with Python `cryptography` 50.0.2 (`docs/evidence/e1-key-provider-v2/`).

## Notes for the owner

- **Legacy wraps are not organization- or purpose-bound in the wrap itself** (they predate E-1). For that data, isolation relies, as before, on the payload AAD (tenant, record, field or attachment, item) and on the row HMAC and Merkle verification. A legacy reference cannot open a scoped wrap, and a scoped reference cannot open a legacy wrap.
- **An ordinary edit re-seals carried values under the current scoped key.** The write path has always re-sealed every carried encrypted value on save (carry-forward). After E-1 those values are sealed under the current scoped key, so an edited record moves off the legacy reference. Nothing rewrites records otherwise.
- **The default local root is extracted from the legacy KEK.** That reuses Development-only key material across two constructions, with domain separation. A separate root can be configured, but doing so makes data already sealed under the derived root undecryptable unless the derived version is otherwise retained.
- **Error precedence in the owner write path shifted slightly.** Decryption of current values happens before per-field validation, and sealing happens after rule validation. A request that both fails validation and hits a key outage now reports the validation error first. Both outcomes fail closed.
- **Rotation and remote providers are not implemented.** The local keyring supports versions; there is no rotation workflow, re-encryption or remote provider (E-2 and later).

## Tests executed (focused, no stack)

- `EnterpriseForms.Api.ContractTests`: `KeyProviderInterfaceV2Tests` (new), `EncryptionKnownAnswerTests` (with K1–K4), `EncryptionRemediationTests`, `StageBUnitTests`, `AuditorReasonNoteSealerTests`, `RecordCreationIdempotencyUnitTests`, `PhaseC2RulesAndOptionsTests`, `PhaseC3RepeatingGroupsTests`, `PhaseC5SharedEditTests`, `PhaseC6RestoreTests`, `ReadinessFailsClosedTests`, `OpenApiSnapshotTests`, `EndpointOwnershipTests`.
- `EnterpriseForms.ArchitectureTests`: full suite, including the updated decryption call-site allow-list (18 reviewed call sites) and the E0-1 Worker guard.
- Stage B was not run.
