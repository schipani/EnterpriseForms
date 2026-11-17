# E-4 Controlled Encryption Key Activation (Development/Testing)

**Scope:** E-4 only, building on the accepted E-0, E-1 and E-2 work. Owner-authorized development task; owner-led review. E-3 (envelope v2, key hierarchy) is deferred; E-5 and later are not started.

There are no new algorithms, envelope formats, key purposes, database migrations, Authority changes, administrative UI or public API changes. Nothing is re-encrypted or rewrapped.

## The model (E4-1)

A wrapping-key version goes through two separate, explicit steps for one organization and one purpose:

1. **Provision.** The version comes into existence but is **not** used for new data.
   - Vault Transit: an operator adds the version in Vault with `provision-business-encryption-key-version.sh`.
   - Local keyring: the version is listed in that scope's `ProvisionedVersions` (new in E-4).
2. **Activate.** Configuration makes it the scope's `CurrentVersion`. The previous version moves to `RetainedVersions`.

New encryption always uses the configured current version. Existing ciphertext keeps the reference it was written with (`localdev:` or `vault-transit:{organization}:{purpose}:{version}`), and decryption resolves exactly that version.

Nothing activates a version automatically:
- The application never creates keys or versions.
- Vault's "latest version" is never used: every Vault encrypt call names `key_version`, and the result is accepted only if Vault reports that version (E-2).
- A newly provisioned version stays inactive until configuration names it.
- An invalid table fails closed. With `LocalDevelopment`, the API and Worker also refuse to start (new in E-4).

The version tables are the E-1/E-2 tables (`CurrentVersion`, `RetainedVersions`, `DisabledVersions`):
- per purpose for every organization: `…:Purposes:{Purpose}`;
- or for one organization: `…:Organizations:{organization id}:{Purpose}`, which **replaces** the purpose table for that organization.

Activating per organization therefore changes no other organization, and activating per purpose changes no other purpose (E4-3).

## Local keyring: what changed (E4-5)

`LocalKeyVersionOptions.ProvisionedVersions` (local keyring only):
- Absent: version 1 only, which is the E-1 default.
- `CurrentVersion` and every `RetainedVersions` entry must be provisioned. If not, the keyring is invalid: every operation fails closed, and the API and Worker refuse to start, naming the problem (never key material).
- Disabled versions do not need to be provisioned.

Local keys are still HKDF-derived from the keyring root. "Provisioning" a local version is therefore a declaration, not key generation. It exists so that activation is a separate, checkable step, as it is with Vault. Nothing is generated on demand. Nothing is deleted. The legacy `local-dev-kek-v1` reader is unchanged.

**Configuration impact:** tables that use any version other than 1 must now list `ProvisionedVersions`. No committed stack configuration uses such tables. Two E-1 test configurations were updated to declare them (`KeyProviderInterfaceV2Tests`, and the K2 setup in `EncryptionKnownAnswerTests`). The vectors themselves are unchanged.

## Activation procedure

Configuration is read when the API and Worker start, so restart both after each step, with **identical** encryption settings. The examples activate version 2 of `FieldValue` for one organization. Environment-variable form shown; JSON works the same way.

### Vault Transit (overlay `compose.encryption-vault.yaml`)

1. **Provision version 2** (operator, root token, never the application):
   ```
   docker compose -f compose.yaml -f compose.encryption-vault.yaml run --rm -e ORGANIZATION_ID=<org> -e KEY_PURPOSE=field-value -e EXPECTED_LATEST_VERSION=1 vault-encryption-provision-version
   ```
   The script:
   - refuses unless the key exists, is a non-exportable `aes256-gcm96` key without auto-rotation, and its latest version equals `EXPECTED_LATEST_VERSION`, so a repeated or concurrent run changes nothing;
   - adds exactly one version and checks that `min_decryption_version` is unchanged;
   - never creates, trims, retires or deletes anything.

   The application keeps wrapping under version 1.
2. *(Optional, several hosts)* Make readers ready first: `…__FieldValue__CurrentVersion=1` and `…__RetainedVersions__0=2`, then restart. Every host can then read version-2 data before any host writes it.
3. **Activate:**
   ```
   Encryption__VaultTransit__Organizations__<org>__FieldValue__CurrentVersion=2
   Encryption__VaultTransit__Organizations__<org>__FieldValue__RetainedVersions__0=1
   ```
   Then restart the API and Worker.

### Local keyring

1. **Provision version 2** (the current version stays 1):
   ```
   Encryption__LocalDevelopmentKeyring__Organizations__<org>__FieldValue__ProvisionedVersions__0=1
   Encryption__LocalDevelopmentKeyring__Organizations__<org>__FieldValue__ProvisionedVersions__1=2
   ```
2. *(Optional)* Add `RetainedVersions__0=2`, as in the Vault procedure.
3. **Activate:** add `…__CurrentVersion=2` and `…__RetainedVersions__0=1`, then restart.

For every organization at once, use `…:Purposes:FieldValue:…` instead of `…:Organizations:<org>:FieldValue:…`. An organization table replaces the purpose table, so it must repeat any retained versions that organization still needs.

## Retaining historical versions (E4-2)

Every version that still protects data must stay in `RetainedVersions`, and must remain decryptable in Vault:
- not trimmed;
- not below `min_decryption_version`.

There is no tool that reports which versions are still referenced, and no retirement workflow (deferred). The operator must therefore keep every version used since the scope was created. The business token cannot trim, retire or reconfigure keys (E4-4). The real-Vault test checks this.

## Verifying historical decryption

- **Automated:** `KeyActivationTests` (both providers) and `KeyActivationVaultIntegrationTests` (real Vault). They write data under version 1, provision and activate version 2, and then show that:
  - version-1 data reads back with its exact version and nothing else;
  - nothing is rewrapped or rewritten;
  - new data uses version 2.
- **On a dev stack:** open a record written before activation; it must read normally. Save a new record, then check the stored references with a read-only query against the development BusinessDb:
  ```
  SELECT encryption_key_reference, count(*) FROM field_encrypted_values GROUP BY 1;
  ```
  Old rows keep `…:field-value:1`, and new rows carry `…:field-value:2`.

## Recovering from an incorrect activation (no data loss)

No activation mistake changes stored data. Data sealed under a version that is missing from the configuration simply fails closed, with `ENCRYPTION_UNAVAILABLE` and audit reason `KEY_UNAVAILABLE`, until the configuration is corrected.

| Mistake | Symptom | Fix |
|---|---|---|
| Activated a version that was never provisioned | Local: host refuses to start ("… is not provisioned"). Vault: every new protected write fails closed; reads still work. Vault is never asked for "latest", and no version is created. | Provision the version first, or set `CurrentVersion` back to the previous version. |
| Dropped the previous version from `RetainedVersions`, or disabled it | Older data fails closed ("the key version is not retained" / "… is disabled"). It is never tried under the new version. | Put the version back in `RetainedVersions` and restart. The tests show the same rows read again. |
| Activated for the wrong organization or purpose | Only that scope's new writes use the new version. Its version-1 data still reads if version 1 was retained. | Correct the table. Data written meanwhile carries its version reference: keep that version retained. |
| Want to undo the activation | — | Set `CurrentVersion` back to 1 and keep 2 in `RetainedVersions`, so data written under version 2 stays readable. Tested. |
| Raised `min_decryption_version` in Vault (operator action) | That version fails closed in Vault. | Lower it again (root token). Vault keeps the version material unless it is trimmed. |

Never trim or delete a Vault key version, and never change the keyring root, while data references it. Either loses that data: no backup exists for the dev-mode Vault, and the local key derivation needs the root.

## Failure handling (E4-6)

Behaviour is the same as E-1/E-2: activation adds no new error paths.
- **Unprovisioned activation** (local), invalid tables, foreign organization or purpose, missing, disabled or retired historical versions: covered in `KeyActivationTests`.
- **Vault outage and permission denial after activation:** exactly one attempt at the configured version, and no retry under another version.
- **Cancellation** propagates and is not a decryption failure.

No new audit or monitoring was added.

## Tests (E4-7)

| Item | Test |
|---|---|
| 1 v1 readable after v2 activation | `KeyActivationTests.VersionOneDataStaysReadableAfterVersionTwoIsActivatedAndNewWritesUseVersionTwo` (field value, document, idempotency key; both providers); real Vault: `ActivatingVersionTwoChangesOnlyThatOrganizationAndPurposeAndKeepsVersionOneReadable` |
| 2 new writes use v2 | same tests (also: provisioning alone keeps wraps on v1; real Vault: `TheVersionScriptProvisionedVersionTwoWithoutActivatingIt`) |
| 3 no rewrite | same test: no wrap during reads, only `vault:v1` decrypts, rows byte-identical |
| 4 organization-specific | `ActivationAppliesOnlyToTheIntendedOrganization`; real Vault (version-2 test) |
| 5 purpose-specific | `ActivationAppliesOnlyToTheIntendedPurpose`; real Vault (version-2 test) |
| 6 invalid activation fails closed | `ActivatingAnUnprovisionedLocalVersionMakesTheKeyringUnavailableAndStopsTheHost`, `ActivatingAVersionVaultDoesNotHaveFailsEveryWriteWithoutFallingBackAndKeepsHistoricalReads`; real Vault (unprovisioned org) |
| 7 historical loss, no fallback | `LosingAHistoricalVersionFailsClosedWithoutFallbackAndIsRecoverableByConfiguration`; real Vault: `AnIncorrectActivationThatDropsVersionOneFailsClosedAndCorrectingItRecoversTheData` |
| 8 legacy data | `LegacyDataAndTheV1VectorsAreUnaffectedByActivation` (E0-6 vector F1 opens; legacy-off fails closed) |
| 9 v1 vectors unchanged | same test (F1 payload bytes reproduce under an activated v2 keyring; K1/K2 open), plus unchanged `EncryptionKnownAnswerTests` |
| 10 Vault runtime restricted | `TheVaultRuntimeOnlyEverWrapsAndUnwrapsAndCannotProvisionOrActivate`; real Vault: `TheBusinessTokenCannotProvisionActivateOrRetireVersions` (rotate, min versions, auto-rotate, trim, read, rewrap all `403`) |
| failures | `OutagesPermissionDenialAndCancellationAfterActivationFailClosedWithoutFallback` |

**Run:**
```
dotnet build tests/features/EnterpriseForms.Api.ContractTests
tests\features\EnterpriseForms.Api.ContractTests\bin\Debug\net10.0\EnterpriseForms.Api.ContractTests.exe --filter-class EnterpriseForms.Api.ContractTests.KeyActivationTests
powershell -File scripts\dev\e2-vault-integration.ps1
```

The runner (extended for E-4):
1. provisions a second organization;
2. adds version 2 of its `field-value` key with the version script;
3. checks five refusals of that script;
4. runs the E-2 and E-4 real-Vault classes.

Evidence: `docs/evidence/e4-controlled-key-activation/`.

## Limitations and deferred work

- **Vault activation of an unprovisioned version is caught at the first write, not at startup.** The business token cannot read key metadata (least privilege, E-2), so it cannot check in advance. The host starts, and protected writes for that scope fail closed until the configuration is corrected.
- **Configuration is per host and read at startup.** The API and Worker must carry the same tables. A host whose configuration does not retain a version cannot read data sealed under it, and fails closed. Use the optional "retain first" step when hosts restart separately.
- **No inventory of referenced versions, and no retirement, deletion, rewrapping or bulk re-encryption** (excluded). Old versions are kept for as long as configuration and Vault keep them.
- **Edits re-seal carried values (existing E-1 behaviour).** A record edit re-seals the encrypted values it carries under the current version. After activation, an edited record therefore moves to the new version. Reading never rewrites.
- **Development/Testing only.** The dev-mode Vault is in memory. The local keyring is refused outside Development/Testing. Production key management (Azure Key Vault/Managed HSM), E-3 envelope v2 and the key hierarchy are not implemented.
- The Authority's HMAC keys and the Authority itself are unchanged.
