# E-5 Controlled HMAC Signing-Key Rotation (Development/Testing)

**Scope:** E-5 only, building on the accepted E-0, E-1, E-2 and E-4 work. Owner-authorized development task; owner-led review. E-3 is deferred; E-6 and later are not started.

There are no new integrity algorithms, key purposes, evidence formats, database migrations, Authority contract (OpenAPI) changes, roles or permissions. No signature, manifest, head or journal entry is rewritten, and nothing is re-signed. E-4 encryption-key activation is untouched.

## Existing integrity-key architecture (E5-1 inventory)

| Question | Finding (before E-5) |
|---|---|
| Who signs and verifies | Only the Authorization Authority, through `IIntegrityMacProvider` (no endpoint signs arbitrary bytes). |
| What is signed | `ContentService.PrepareAsync` signs every candidate row (RowHmac v1 canonical bytes). |
| Where verification happens | `CommitAsync` re-verifies the staged rows; `VerifyProjectionAsync` covers current and historic reads; the verifier workload's content check, re-protection and share-editor writes verify the current snapshot. Onboarding signs and verifies one probe when a key is registered. |
| Providers | `LocalHmacProvider`: in-process random keys, test/offline only. `VaultTransitHmacProvider`: Docker Vault Transit. Managed HSM is not implemented. Production and Staging refuse both. |
| Which providers support versions | Vault: yes. Every sign sends `key_version`; verify rebuilds `vault:vN:`. Local: in practice version 1 only (`ProvisionKeyAsync` created only version 1). |
| The key catalog | `IntegrityKeyRegistration` in AuthorityDb (`integrity_key_registrations`), one per organization and purpose. It holds `ProviderName`, `ProviderKeyName`, `CurrentSigningKeyVersion`, `MinimumVerificationKeyVersion` and `KeyUsagePaused`. Onboarding and bootstrap set the current version to the provider's latest at registration. **Nothing could change it afterwards.** |
| Purposes | `row-integrity-v1` only. |
| How the signing version is recorded | In the row canonical header (`KeyVersion`), the prepared operation (`CandidateKeyVersion`), the manifest commit (`RowSignatureKeyVersion`), the content head (`SignatureKeyVersion`) and the content checkpoint body. The business stager writes the version the Authority reports (`CurrentSigningKeyVersion`). The data format already supports multiple versions. |
| How historical verification picks a key | The version recorded with the evidence: the head's for current reads, the commit's for historic reads, the operation's at commit. It must be at least `MinimumVerificationKeyVersion`; below that it fails closed with `KEY_UNAVAILABLE`. The provider verifies with exactly that version. |
| Legacy evidence without a version | None: every row header has carried a version since the start. |
| Search seal signing (`SearchSealSigners`, ECDSA) | A separate seal key with its own pinned public key. Not HMAC and not part of E-5. |

## What E-5 adds

1. **`IntegrityKeyActivationService`** (Authority Application, `Features/Keys`). It changes `CurrentSigningKeyVersion` of exactly one registration. It refuses, changing nothing, when:
   - the organization or registration is missing, or the request is malformed;
   - key usage is paused;
   - the registration belongs to a different provider than the configured one;
   - the expected current version does not match (compare-and-set under the organization row lock);
   - the target is below `MinimumVerificationKeyVersion`;
   - the provider does not report the version as provisioned and usable;
   - the Authority cannot sign and verify a domain-separated probe (`DynamicForms/IntegrityKeyActivationProbe/v1`) with exactly that version.

   On success it appends one `IntegrityKeyVersionActivated` event to the organization's existing non-gating **audit** journal stream, in the same transaction. The event records the versions, provider key name, operator workload, reason and correlation id; it uses the existing envelope and is archived by the unchanged processor. Rollback is the same operation with the versions swapped.
2. **`--activate-integrity-key-version`**: a Development/Testing-only command on the composed Authority host, alongside the existing `--measure-seal-throughput`. It calls the service once, prints a JSON result and exits:
   - 0: activated;
   - 1: refused, nothing changed;
   - 2: invalid arguments;
   - 3: refused outside Development/Testing.

   It is not an endpoint and grants no new right: anyone able to run the Authority host with its configuration can already act as the Authority.
3. **`LocalHmacProvider.ProvisionKeyVersion`**: an explicit action that adds one version to an existing in-process key. `ProvisionKeyAsync` and `InspectKeyAsync` now report the latest version. Signing and verification still use exactly the requested version.
4. **`infrastructure/docker/vault/provision-integrity-key-version.sh`**: the Vault operator action, run with the root token. It adds exactly one version to `transit/keys/org-<organization>-row-integrity-v1`, and refuses unless:
   - the key exists and is a non-exportable HMAC key without plaintext backup or auto-rotation;
   - its latest version equals `EXPECTED_LATEST_VERSION`.

   It checks that `min_decryption_version` is unchanged. It never activates, creates, trims, retires or reconfigures.
5. `IAuthorityScope.FindKeyRegistrationForUpdateAsync`: a tracked read of the registration. This is an internal port, not a contract, and needs no schema or grant change: the runtime role already has UPDATE on `integrity_key_registrations` (G08 grants).

## Provisioning and activation procedure (development stack)

1. **Provision version 2 in Vault** (root custody token from the dev stack's custody file; never the Authority's token):
   ```
   docker cp infrastructure/docker/vault/provision-integrity-key-version.sh <vault container>:/tmp/
   docker exec -e VAULT_ADDR=http://127.0.0.1:8200 -e VAULT_TOKEN=<root> -e ORGANIZATION_ID=<org> -e EXPECTED_LATEST_VERSION=1 <vault container> sh /tmp/provision-integrity-key-version.sh
   ```
   The Authority keeps signing with version 1. Every HMAC call names `key_version`.
2. **Activate:**
   ```
   docker exec <authority container> dotnet AuthorizationAuthority.Api.dll --activate-integrity-key-version --organization <org> --expected-current-version 1 --signing-key-version 2 --reason "<why>"
   ```
   It takes effect on the next prepare, with no restart:
   - The Authority reads the registration on every write.
   - The business stager stages with the version the Authority reports.
   - An operation prepared before the activation commits under its own prepared version.
   - A writer that staged with the old version before the activation is refused (`422`, "candidate key version must equal the current signing key version") and must re-stage. Nothing is accepted under a mismatched version.
3. **Verify:** reads of older revisions continue to verify. The Authority's content history lists each revision with its `RowSignatureKeyVersion`, and `VerifiableNow` stays true. New commits record version 2.

**Rollback:** run the same command with `--expected-current-version 2 --signing-key-version 1`. Version-2 evidence stays verifiable, because nothing lowers `MinimumVerificationKeyVersion`.

## Retention requirements

Every version that signed retained evidence must stay usable in the provider. In Vault that means:
- never trimmed (`min_available_version`);
- not below `min_decryption_version`;
- the key never deleted.

It must also stay at or above the registration's `MinimumVerificationKeyVersion`. E-5 adds no way to raise the floor, retire or delete versions (excluded). There is no inventory of referenced versions, so the operator must keep every version used since the key was registered.

A retired or missing version makes its evidence fail closed. It is never verified under another version. See failure handling below.

## Failure handling (E5-6)

| Condition | Result |
|---|---|
| Activation of an unprovisioned version | `ORGANIZATION_KEY_NOT_PROVISIONED`; nothing changed. |
| Stale expected version | `OPERATOR_STATE_CONFLICT`. |
| Paused key, missing registration, wrong provider, below the floor | `KEY_UNAVAILABLE`. |
| Provider down, permission denied, or version unusable during activation | `SIGNING_UNAVAILABLE`; nothing changed. |
| Invalid input | `INVALID_COMMAND`. |
| Historical version retired or missing in **Vault** | Vault refuses → `IntegrityProviderUnavailableException` → `SIGNING_UNAVAILABLE` (503). No quarantine, no incident, never valid. Verifies again once restored. |
| Historical key missing in the **local provider** (new process) | `VerifyAsync` returns false → `ITEM_SIGNATURE_INVALID`, and the head is quarantined. This is existing local behaviour, kept unchanged. |
| Version below `MinimumVerificationKeyVersion` | `KEY_UNAVAILABLE` and `VerifiableNow=false` (existing floor semantics). |
| Recorded version altered in the store, or a foreign organization's rows | Binding mismatch → not verified, quarantined (unchanged rule). The other version's key is never used. |
| Wrong organization's key reference | Refused by the provider, or not valid. |
| Cancellation | `OperationCanceledException` propagates; nothing changed. |

Verification results, reason codes, quarantine (G14) and the history `VerifiableNow` flag are unchanged. The full Authority unit suite passes unmodified. Pass, Fail, Inconclusive and Suspended belong to search and projection verification, which E-5 does not touch.

## Tests (E5-7)

`AuthorizationAuthority.Tests`:
- `IntegrityKeyActivationTests` (11): real `ContentService`, canonical encoding, local provider, in-memory store.
- `IntegrityKeyActivationVaultIntegrationTests` (4): real Vault via `scripts/dev/e5-vault-hmac-integration.ps1`, with the dev stack's Authority onboarding policy.

| Item | Test |
|---|---|
| 1 v1 verifies before activation | `VersionOneEvidenceVerifiesBeforeAndAfter…` |
| 2 provisioning does not activate | same test (registration and new commits stay on v1; a v2-staged candidate is refused); real Vault: `AScriptProvisionedVersionIsUnusedUntilActivated…` |
| 3 new signatures use v2 | same tests (row header, operation, commit, head; provider sign calls only v2) |
| 4 v1 still verifies after activation | same tests (exactly v1 used; history `VerifiableNow`) |
| 5 no rewrite | same test: fingerprint of commits, leaves, signatures, roots, content journal and stored rows unchanged |
| 6 organization isolation | `ActivationChangesOnlyItsOwnOrganization…`; real Vault: `ActivatingAVersionVaultDoesNotHaveIsRefusedAndKeysDoNotCrossOrganizations` |
| 7 wrong version rejected | `AWrongRecordedVersionIsRejectedAndNeverSubstituted`; real Vault (v1 MAC under v2 and the reverse) |
| 8 missing historical key | `AMissingHistoricalKeyFailsClosedWithoutFallbackToTheActiveVersion`; real Vault: `ARetiredOrMissingHistoricalVersionFailsClosed…` |
| 9 no fallback | same tests (recorded provider calls) |
| 10 invalid configuration | `InvalidActivationsAreRefusedAndChangeNothing`, `AVersionBelowTheVerificationFloor…` |
| 11 Vault permissions | `TheAuthorityTokenCanOnlyReadSignAndVerify…`: rotate, config (min versions, auto-rotate, deletion), trim, create, delete, export, backup and sys are all `403`. The runner also checks that the version script fails with the Authority's token. |
| 12 integrity vectors | `EnterpriseForms.Integrity.Tests` (60, Merkle and RowHmac vectors) unchanged |
| 13 cancellation | `CancellationPropagatesAndChangesNothing`; real Vault (verify) |
| 14 statuses unchanged | full `AuthorizationAuthority.Tests` suite unmodified |

Also covered:
- `AnOperationPreparedBeforeActivationCommitsUnderItsPreparedVersion…`
- `RollingBackAnActivation…`
- `TheLocalProviderAddsVersionsOnlyByTheExplicitAction…`
- `TheOperatorCommandParsesStrictly…` (refused in Production and Staging)

Evidence: `docs/evidence/e5-hmac-signing-key-rotation/`.

## Provider limitations and residual risks

- **Local provider keys are per process.** The local provider is a test/offline tool. Its versions exist only in the process that provisioned them. The activation command against a host using `LocalDevelopment` would see different random keys, so use Vault for a running stack. A missing local key is reported as an invalid signature, which quarantines; Vault reports an unavailable version, which does not.
- **The activation is Development/Testing only and single-operator.** CRYPTOGRAPHIC_SIGNING_AUTHORITY.md §5 requires, for production, separately authorized security operators, step-up, dual control and an independent checkpoint. None of that is implemented; it needs a governed operator workflow (excluded here). The audit journal event is evidence, not approval.
- **AuthorityDb restore can revert an activation.** The registration is AuthorityDb state, not replayed from the journal. Restoring an older backup returns new writes to the older version. Evidence stays verifiable because each signature records its version. This is not a security rollback, but the audit event remains as the record of the activation.
- **In-flight writes during activation are refused once** (stale staged version) and must be re-staged.
- **The command was not run against a live stack in E-5.** Doing so would change the owner's development organization. The service, provider, script and command are covered by the focused and real-Vault tests.
- SIG-03 now has local and real-Vault development evidence. Managed HSM, staging and production evidence remain open.
