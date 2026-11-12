# E-2 Vault Transit Encryption Key Provider (Development/Testing)

**Scope:** E-2 only, building on the accepted E-0 and E-1 work. Owner-authorized development task; owner-led review. E-3 to E-10 are not started.

There are no new algorithms, envelope formats, key purposes, database migrations, Authority changes or public API changes. There are also no rotation workflows, staging or production configuration, or cloud infrastructure.

## What it is

`VaultTransitKeyProvider` (Infrastructure, `Documents`) implements the E-1 `IEncryptionKeyProvider` with HashiCorp Vault Transit. It is meant for the local Docker stack.

The application never holds a wrapping key:
- each 256-bit data key is wrapped by Vault's `encrypt` endpoint and unwrapped by its `decrypt` endpoint;
- the wrapping key is a non-exportable `aes256-gcm96` Vault key;
- nothing is cached.

Field-value and document payload encryption (AES-256-GCM, the v1 envelopes) is unchanged. The provider replaces only the key wrap.

The local keyring (`LocalDevelopment`) remains available and remains the default in every existing stack.

## Configuration (E2-5)

| Setting | Value |
|---|---|
| `Encryption:KeyProvider` | `VaultTransit`: an explicit selection. Exactly one provider is registered; the local keyring is never registered beside it. |
| `Encryption:VaultTransit:Address` | Absolute `http(s)` URI with no path, query or credentials, for example `http://vault:8200`. Redirects are not followed. |
| `Encryption:VaultTransit:Token` | The dedicated business encryption token. It comes from the environment (`.env`: `BUSINESS_ENCRYPTION_VAULT_TOKEN`) and is never in committed JSON. |
| `Encryption:VaultTransit:MountPath` | Default `enterpriseforms-encryption`. `transit` (the Authority's HMAC mount) and Vault system paths are refused. |
| `Encryption:VaultTransit:KeyNamePrefix` | Default `ef-enc` (1–24 lowercase letters, digits or inner hyphens). |
| `Encryption:VaultTransit:TimeoutSeconds` | Per operation, 1–60, default 5. Exceeding it fails closed. |
| `Encryption:VaultTransit:Purposes:{Purpose}` | `CurrentVersion`, `RetainedVersions`, `DisabledVersions`. Same shape and rules as the E-1 local keyring. Absent: current version 1. |
| `Encryption:VaultTransit:Organizations:{organization id}:{Purpose}` | Optional per-organization replacement of the purpose table. |

**Startup (API and Worker, the E0-1 policy):**
- `VaultTransit`, like `LocalDevelopment`, is accepted only in Development and Testing. Anywhere else the host refuses to start.
- If `VaultTransit` is selected with a missing or invalid setting, the host also refuses to start, naming the setting (never the token).
- Were it started anyway, the provider reports `IsConfigured = false` and fails every operation closed.
- There is never a fallback to `LocalDevelopment`.

**Docker (opt-in):** `infrastructure/docker/compose.encryption-vault.yaml` layers onto `compose.yaml`:

```
docker compose -f compose.yaml -f compose.encryption-vault.yaml up -d
docker compose -f compose.yaml -f compose.encryption-vault.yaml run --rm -e ORGANIZATION_ID=<organization uuid> vault-encryption-provision
```

The overlay is not used by the security-test or persistent development stacks. Add `BUSINESS_ENCRYPTION_VAULT_TOKEN` to your `.env`; `env.example` holds a synthetic value.

## Dedicated Vault boundary (E2-4)

The overlay reuses the existing dev-mode Vault container, with a separate mount, policy and token. `vault/business-encryption-init.sh` runs once with the root token. It:
1. Enables the `enterpriseforms-encryption` Transit mount.
2. Writes the `business-encryption` policy (`vault/business-encryption-policy.hcl`).
3. Creates the business token: `-orphan -no-default-policy -policy=business-encryption`, TTL 720 h.

It refuses to use `transit` as the mount, or the root token as the business token.

The policy grants **`update` only** on `enterpriseforms-encryption/encrypt/ef-enc-*` and `.../decrypt/ef-enc-*`. Because it has no `create`, Transit's encrypt endpoint cannot create a missing key (its "upsert" needs `create`). The token also has nothing on:
- `keys/*` (read, create, rotate, config, delete), `export/*`, `backup/*`, `datakey/*`, `rewrap/*`;
- `sys/*`, `auth/*` (not even lookup-self);
- the Authority's `transit` mount.

The real-Vault test asserts each of these refusals.

**Network.** The API and Worker reach Vault over a new internal network, `encryption-kms-net`. They are still not on `authority-net`, so AuthorityDb, SpiceDB and the archive remain unreachable from them. Nothing on `business-net`, including the security-test "attacker" container (SEC-P11), can reach Vault.

**Residual risk.** In the overlay, the business workloads can open a network connection to the dev Vault server that also holds the Authority's HMAC keys. What they can do there is limited only by their token's policy. A separate business Vault container would remove this shared surface; the owner's instruction to reuse the existing Vault infrastructure was followed.

## Key provisioning (development)

`vault/provision-business-encryption-keys.sh` is an operator action, run with the root token through the overlay's `vault-encryption-provision` service. For one `ORGANIZATION_ID` it:
- creates the four purpose keys, if absent: `ef-enc-{organization, 32 lowercase hex}-{field-value|document|auditor-reason-note|idempotency-protection}`;
- makes each `type=aes256-gcm96 exportable=false allow_plaintext_backup=false`;
- then verifies those properties.

It never changes, rotates or deletes an existing key, and it refuses a non-UUID organization.

Until an organization's keys are provisioned, its protected writes fail closed with `ENCRYPTION_UNAVAILABLE`. The application never creates keys.

## Key references and versions (E2-2, E2-3)

- **Reference** (stored in the unchanged v1 envelopes): `vault-transit:{organization}:{purpose token}:{version}`.
  - It is canonical: any other spelling (case, compact UUID, leading zeros, extra segments, another provider's prefix) is not a reference and fails closed without calling Vault.
  - It fits the Authority's reason-note key-reference rule and the 200-character columns.
- **Key selection** is deterministic: configured mount, plus key name `{prefix}-{organization N}-{purpose token}`, plus a version from the configured table. No caller input names a key or a mount.
- **Wrap context:** Vault's AES-GCM `associated_data` is "DynamicForms/VaultTransit/DekWrap/v1" ‖ organization (16 bytes) ‖ Int32BE(token length) ‖ purpose token ‖ Int32BE(version). That is the E-1 scope encoding under its own domain label. A wrapped key is therefore bound to its scope and version twice: by the scope's own Vault key and by the authenticated context. The context still separates organizations even if two names ever pointed at the same Vault key.
- **Wrapped-key bytes:** Vault's ciphertext body for `aes256-gcm96` is exactly nonce(12) ‖ ciphertext(32) ‖ tag(16), the same 60-byte layout the local keyring stores. The real-Vault test confirms the length.
  - The `vault:vN:` prefix is not stored. On unwrap it is rebuilt from the reference's version, so Vault decrypts with exactly the version the reference names.
  - Any other stored length is refused before Vault is called.
- **Wrap** sends `key_version` = the configured current version (never Vault's "latest"). It accepts the result only if Vault reports that same version, in both `key_version` and the ciphertext prefix.
- **Unwrap** resolves only the referenced version. It must be current or retained in the table.
- **Fail closed, no fallback, nothing substituted:**
  - versions that are disabled or absent from the table fail closed without calling Vault;
  - a version Vault does not have, or one Vault itself retired (`min_decryption_version`), fails closed as unavailable.
- Historical ciphertext stays readable while its version is retained in configuration and still decryptable in Vault.

Adding a Vault key version (`.../rotate`) is an operator action, used by the integration test's setup only. No rotation workflow is implemented.

## Failure handling (E2-6)

| Condition | Result |
|---|---|
| Vault unreachable, timed out, 5xx, sealed, unreadable or incomplete response | `EncryptionUnavailableException` → `503 ENCRYPTION_UNAVAILABLE`; decryption audit reason `KEY_UNAVAILABLE` |
| Invalid, expired or revoked token; permission denied (`403`) | `EncryptionUnavailableException` (message carries only the status code) |
| Key not provisioned; configured version not in Vault; version retired by Vault; version disabled or not retained in configuration; unparsable or foreign-provider reference | `EncryptionUnavailableException` |
| Reference of another organization or purpose; wrapped key not exactly 60 bytes; Vault reports AES-GCM authentication failure (tampered, relabelled, cross-scope or version-substituted wrap); unwrapped key not 32 bytes | `CryptographicException` → `423 FIELD_DECRYPTION_FAILED`; audit reason `KEY_MISMATCH` (E0-4) |
| Caller cancellation | `OperationCanceledException` propagates unchanged; it is never reported as an outage |

Public error contracts are unchanged. Exception messages never contain the token, key material, the data key, or Vault's error text. The Vault error text is used only to tell an authentication failure apart from other refusals.

Decryption failures are recorded by the existing E0-4 `DECRYPTION_FAILED` audit path. No new monitoring was added.

## Tests (E2-7)

| Item | Test (`EnterpriseForms.Api.ContractTests`) |
|---|---|
| 1 wrap/unwrap | `VaultTransitKeyProviderTests.AWrappedDataKeyRoundTripsThroughVaultUnderTheScopesOwnKey`; real Vault: `RealVaultWrapsAndUnwrapsUnderTheScriptProvisionedKeys` |
| 2 organization isolation | `AKeyOfOneOrganizationCannotBeUsedForAnother`, `TheWrapContextBindsTheOrganizationEvenWhenTwoScopesReachTheSameVaultKey`; real Vault: `RealVaultRejectsCrossOrganizationCrossPurposeAndVersionSubstitution` |
| 3 purpose isolation | `AKeyOfOnePurposeCannotBeUsedForAnother`; real Vault (same test) |
| 4 reference substitution | `SubstitutedOrNonCanonicalReferencesAreRefused`, `TheVaultKeyNameIsDerivedOnlyFromTrustedConfigurationAndTheScope`; real Vault (same test) |
| 5 current version | `NewWrapsUseTheConfiguredCurrentVersionAndRefuseAnyOtherReportedVersion` |
| 6 historical version | `RetainedHistoricalVersionsStillUnwrapAfterTheCurrentVersionMoves`; real Vault (rotate, then v1 and v2) |
| 7 missing/disabled | `MissingDisabledAndVaultRetiredVersionsFailClosed`; real Vault: `TheApplicationCannotCreateAKeyByEncryptingUnderAnUnprovisionedOrganization` and `min_decryption_version` |
| 8 Vault unavailable | `AnUnreachableSlowFailingOrGarbledVaultFailsClosed`; real Vault: `InvalidCredentialsAndAnUnreachableVaultFailClosed` |
| 9 credentials/permission | `InvalidOrUnauthorizedCredentialsFailClosedWithoutRevealingThem`; real Vault: `TheBusinessTokenHasNoAdministrativeExportOrAuthorityCapability` |
| 10 cancellation | `CancellationPropagatesAndIsNotReportedAsAnOutage` |
| 11 v1 envelope compatibility | `TheVaultProviderProducesUnchangedV1EnvelopesForEveryConsumer`: E0-6 vector F1 payload bytes are byte-identical; documents, idempotency and reason notes (Authority validation) work. The existing `EncryptionKnownAnswerTests`, `KeyProviderInterfaceV2Tests` and legacy vectors pass unchanged. |
| 12 no fallback | `SelectingVaultTransitRegistersOnlyItAndNeverFallsBackToTheLocalKeyring`, `VaultTransitIsADevelopmentAndTestingProviderOnly`, `IncompleteOrInvalidVaultSettingsAreRefusedAtStartupAndAtUse`, `TheApiAndWorkerStartupGuardsApplyTheVaultSettingsCheck` |

The deterministic tests run against `FakeVaultTransit`, an in-process model of the Transit endpoints the provider uses. It models versions, `associated_data`, `min_decryption_version`, and an update-only token policy with no upsert.

**Run the focused tests:**

```
dotnet build tests/features/EnterpriseForms.Api.ContractTests
tests\features\EnterpriseForms.Api.ContractTests\bin\Debug\net10.0\EnterpriseForms.Api.ContractTests.exe --filter-class EnterpriseForms.Api.ContractTests.VaultTransitKeyProviderTests
powershell -File scripts\dev\e2-vault-integration.ps1
```

The script starts a throwaway `hashicorp/vault:1.20.4` dev container on `127.0.0.1:18201` with random tokens, and touches no compose stack. It:
- prepares Vault with the repository's own init and provisioning scripts, and checks their refusals;
- adds an Authority-style HMAC key on `transit`;
- runs `VaultTransitDockerIntegrationTests`, then removes the container.

Without the script those tests skip themselves. Evidence of a run is in `docs/evidence/e2-vault-transit-provider/`.

## Compatibility limitations

- **No cross-provider reads.** A stack switched from `LocalDevelopment` to `VaultTransit` cannot read data sealed by the local keyring (scoped or legacy references), and vice versa. This is intentional (no fallback). Use a fresh stack; a migration between providers is out of scope.
- **Development/Testing only.** The dev-mode Vault is in-memory and unsealed with a root token, so it is not a production key store. Restarting the container loses every key and makes all data it protected unreadable. Production still requires the Azure Key Vault/Managed HSM providers, which are not implemented.
- **Plaintext key in transit to Vault.** Wrap sends the data key to Vault, base64-encoded in the HTTP request, and unwrap returns it the same way. Those managed strings cannot be zeroed. The local stack uses plain HTTP on an internal Docker network.
- **Custom token IDs.** Vault warns that custom token IDs (used here, as for the existing console and custody tokens) are stored with SHA-1 obfuscation. This is development only.
- **One HTTP round trip per wrap and unwrap.** There is no batching or caching (none was requested, and caching wrapping keys is prohibited).
- **Key type is not checked at runtime.** The application token cannot read key metadata (least privilege), so it does not re-check that a key is `aes256-gcm96`. The provisioning script does.
